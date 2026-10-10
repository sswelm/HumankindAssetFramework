// BezierDrill.cs - BlenderFCurve.Evaluate against Blender's own FCurve.evaluate() (blender_bezier_dump.py): every
// value to the bit, and which branches of the evaluation the generated curves reached.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

static class BezierDrill
{
    static float F(string hex)
    {
        if (hex.Length != 8) throw new InvalidDataException($"not a float32 in hex: '{hex}'");
        return BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(hex, 16)), 0);
    }
    static string H(float v) => BitConverter.ToUInt32(BitConverter.GetBytes(v), 0).ToString("x8");

    public static int Run(string[] args)
    {
        Console.WriteLine($"RUNTIME\t{(IntPtr.Size * 8)}-bit, the C runtime's own functions: {BlenderTrig.Exact}");
        long curves = 0, values = 0, wrong = 0, nans = 0, ended = -1, endedValues = 0;
        BlenderFCurve.Hits = new int[BlenderFCurve.Branches.Length];
        List<BlenderDeploy.ArmKey> keys = null; bool constant = true; string shown = null;
        foreach (string dump in args)
            foreach (string line in File.ReadLines(dump))
            {
                var t = line.Split('\t');
                if (t[0] == "CURVE")
                {
                    if (t.Length < 3 || (t[1] != "CONSTANT" && t[1] != "LINEAR")) throw new InvalidDataException($"a malformed curve row: {line}");
                    constant = t[1] == "CONSTANT";
                    keys = t.Skip(2).Select(k =>
                    {
                        var p = k.Split(':'); if (p.Length != 6) throw new InvalidDataException($"a key without its six floats: {k}");
                        return new BlenderDeploy.ArmKey { Frame = F(p[0]), Value = F(p[1]), LeftX = F(p[2]), LeftY = F(p[3]), RightX = F(p[4]), RightY = F(p[5]) };
                    }).ToList();
                    curves++; shown = line;
                }
                else if (t[0] == "E")
                {
                    if (keys == null || t.Length != 3) throw new InvalidDataException($"a malformed evaluation row: {line}");
                    float time = F(t[1]); string mine = H(BlenderFCurve.Evaluate(keys, time, constant));
                    values++;
                    // a NaN is held as a NaN: which of two NaNs survives an addition follows the compiler's operand order
                    if (float.IsNaN(F(t[2])) && float.IsNaN(F(mine))) { nans++; continue; }
                    if (mine != t[2])
                    {
                        wrong++;
                        if (wrong <= 12) Console.WriteLine($"DIFF at {time:R}: here {mine} ({F(mine):R}), Blender {t[2]} ({F(t[2]):R}) on {shown}");
                    }
                }
                else if (t[0] == "END") { if (t.Length != 3) throw new InvalidDataException("a malformed end row"); ended = (ended < 0 ? 0 : ended) + long.Parse(t[1]); endedValues += long.Parse(t[2]); }
                else throw new InvalidDataException($"a row the drill does not know: '{(line.Length > 60 ? line.Substring(0, 60) : line)}'");
            }
        for (int i = 0; i < BlenderFCurve.Branches.Length; i++) Console.WriteLine($"BRANCH\t{BlenderFCurve.Hits[i]}\t{BlenderFCurve.Branches[i]}");
        Console.WriteLine($"TOTAL\tcurves {curves} values {values} wrong {wrong} nan {nans}");
        if (ended != curves || endedValues != values) { Console.WriteLine($"FAIL — the dump ends after {ended} curves and {endedValues} values and holds {curves} and {values} (cut short?)"); return 1; }
        if (curves == 0 || values == 0) { Console.WriteLine("FAIL — the dump holds no curve"); return 1; }
        if (wrong > 0) { Console.WriteLine($"FAIL — {wrong} of {values} values differ from Blender's"); return 1; }
        Console.WriteLine($"PASS — {values} values of {curves} Bezier curves equal to Blender's FCurve.evaluate(), bit for bit ({nans} of them a NaN on both sides, its sign not compared)");
        return 0;
    }
}
