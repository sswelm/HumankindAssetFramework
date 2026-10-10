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
        try { return Compare(args); }
        catch (InvalidDataException e) { Console.WriteLine($"FAIL — {e.Message}"); return 1; }
    }

    static int Compare(string[] args)
    {
        Console.WriteLine($"RUNTIME\t{(IntPtr.Size * 8)}-bit, the C runtime's own functions: {BlenderTrig.Exact}");
        long curves = 0, values = 0, wrong = 0, nans = 0;
        BlenderFCurve.Hits = new int[BlenderFCurve.Branches.Length];
        List<BlenderDeploy.ArmKey> keys = null; bool constant = true; string shown = null;
        foreach (string dump in args)
        {
            long fileCurves = 0, fileValues = 0; bool ended = false;
            string[] times = null; int sample = 0; keys = null;
            void CompleteCurve()
            {
                if (keys != null && (times == null || sample != times.Length))
                    throw new InvalidDataException("the dump is cut short: a curve lacks its requested evaluations");
            }
            foreach (string line in File.ReadLines(dump))
            {
                var t = line.Split('\t');
                if (ended) throw new InvalidDataException("a row follows the dump's end row");
                if (t[0] == "CURVE")
                {
                    CompleteCurve();
                    if (t.Length < 4 || (t[2] != "CONSTANT" && t[2] != "LINEAR")) throw new InvalidDataException($"a malformed curve row: {line}");
                    if (!long.TryParse(t[1], out long ordinal) || ordinal != fileCurves) throw new InvalidDataException("the dump's curve ordinal is not the next curve");
                    constant = t[2] == "CONSTANT";
                    keys = t.Skip(3).Select(k =>
                    {
                        var p = k.Split(':'); if (p.Length != 6) throw new InvalidDataException($"a key without its six floats: {k}");
                        return new BlenderDeploy.ArmKey { Frame = F(p[0]), Value = F(p[1]), LeftX = F(p[2]), LeftY = F(p[3]), RightX = F(p[4]), RightY = F(p[5]) };
                    }).ToList();
                    curves++; fileCurves++; shown = line; times = null; sample = 0;
                }
                else if (t[0] == "TIMES")
                {
                    if (keys == null || times != null || t.Length < 2) throw new InvalidDataException("a malformed or duplicated evaluation request list");
                    times = t.Skip(1).ToArray();
                    foreach (string time in times) if (float.IsNaN(F(time))) throw new InvalidDataException("a NaN evaluation request");
                }
                else if (t[0] == "E")
                {
                    if (keys == null || t.Length != 3) throw new InvalidDataException($"a malformed evaluation row: {line}");
                    if (times == null || sample >= times.Length || t[1] != times[sample]) throw new InvalidDataException("the dump's evaluation is not the next requested time (cut short or replaced?)");
                    sample++;
                    float time = F(t[1]); string mine = H(BlenderFCurve.Evaluate(keys, time, constant));
                    values++; fileValues++;
                    // a NaN is held as a NaN: which of two NaNs survives an addition follows the compiler's operand order
                    if (float.IsNaN(F(t[2])) && float.IsNaN(F(mine))) { nans++; continue; }
                    if (mine != t[2])
                    {
                        wrong++;
                        if (wrong <= 12) Console.WriteLine($"DIFF at {time:R}: here {mine} ({F(mine):R}), Blender {t[2]} ({F(t[2]):R}) on {shown}");
                    }
                }
                else if (t[0] == "END")
                {
                    CompleteCurve();
                    if (t.Length != 3 || !long.TryParse(t[1], out long nc) || !long.TryParse(t[2], out long nv)) throw new InvalidDataException("a malformed end row");
                    if (nc != fileCurves || nv != fileValues) throw new InvalidDataException($"the dump is cut short: ends after {nc} curves and {nv} values but holds {fileCurves} and {fileValues}");
                    ended = true;
                }
                else throw new InvalidDataException($"a row the drill does not know: '{(line.Length > 60 ? line.Substring(0, 60) : line)}'");
            }
            if (!ended) throw new InvalidDataException("the dump is cut short: no end row");
        }
        for (int i = 0; i < BlenderFCurve.Branches.Length; i++) Console.WriteLine($"BRANCH\t{BlenderFCurve.Hits[i]}\t{BlenderFCurve.Branches[i]}");
        Console.WriteLine($"TOTAL\tcurves {curves} values {values} wrong {wrong} nan {nans}");
        if (curves == 0 || values == 0) { Console.WriteLine("FAIL — the dump holds no curve"); return 1; }
        if (wrong > 0) { Console.WriteLine($"FAIL — {wrong} of {values} values differ from Blender's"); return 1; }
        Console.WriteLine($"PASS — {values} values of {curves} Bezier curves equal to Blender's FCurve.evaluate(), bit for bit ({nans} of them a NaN on both sides, its sign not compared)");
        return 0;
    }

    /// <summary>--handles: BlenderFCurve.RecalcHandles against the handles Blender itself gives AUTO_CLAMPED keys
    /// (blender_handles_dump.py): both handles of every key, to the bit.</summary>
    public static int RunHandles(string[] args)
    {
        try { return CompareHandles(args); }
        catch (InvalidDataException e) { Console.WriteLine($"FAIL — {e.Message}"); return 1; }
    }

    static int CompareHandles(string[] args)
    {
        Console.WriteLine($"RUNTIME\t{(IntPtr.Size * 8)}-bit");
        long curves = 0, keysSeen = 0, wrong = 0, wrongCurves = 0, nans = 0;
        BlenderFCurve.Hits = new int[BlenderFCurve.Branches.Length];
        foreach (string dump in args)
        {
            long fileCurves = 0, fileKeys = 0; bool ended = false;
            foreach (string line in File.ReadLines(dump))
            {
                var t = line.Split('\t');
                if (ended) throw new InvalidDataException("a row follows the dump's end row");
                if (t[0] == "H")
                {
                    if (t.Length < 5 || (t[2] != "CONSTANT" && t[2] != "LINEAR")) throw new InvalidDataException($"a malformed curve row: {(line.Length > 80 ? line.Substring(0, 80) : line)}");
                    if (!long.TryParse(t[1], out long ordinal) || ordinal != fileCurves) throw new InvalidDataException("the dump's curve ordinal is not the next curve");
                    if (t[3] != "CONT_ACCEL") throw new InvalidDataException($"a curve whose smoothing is {t[3]}: the port is CONT_ACCEL's (Blender's default)");
                    var theirs = t.Skip(4).Select(k => { var p = k.Split(':'); if (p.Length != 6) throw new InvalidDataException($"a key without its six floats: {k}"); return p; }).ToList();
                    var keys = theirs.Select(p => new BlenderDeploy.ArmKey { Frame = F(p[0]), Value = F(p[1]) }).ToList();
                    BlenderFCurve.RecalcHandles(keys, t[2] == "CONSTANT");
                    bool bad = false;
                    for (int i = 0; i < keys.Count; i++)
                    {
                        var k = keys[i]; string mine = $"{H(k.LeftX)}:{H(k.LeftY)}:{H(k.RightX)}:{H(k.RightY)}", blender = string.Join(":", theirs[i].Skip(2));
                        keysSeen++; fileKeys++;
                        if (mine == blender) continue;
                        // a NaN is held as a NaN (a key past float32, or not finite: which of two NaNs survives follows the
                        // compiler's operand order) - every other float of the key to the bit
                        var m4 = new[] { k.LeftX, k.LeftY, k.RightX, k.RightY };
                        if (Enumerable.Range(0, 4).All(q => H(m4[q]) == theirs[i][2 + q] || (float.IsNaN(m4[q]) && float.IsNaN(F(theirs[i][2 + q]))))) { nans++; continue; }
                        wrong++;
                        if (!bad && wrongCurves < 12) Console.WriteLine($"DIFF curve {ordinal} ({t[2]}, {keys.Count} keys) key {i} at {k.Frame:R} = {k.Value:R}: here [{k.LeftX:R}, {k.LeftY:R}] [{k.RightX:R}, {k.RightY:R}] {mine}, Blender [{F(theirs[i][2]):R}, {F(theirs[i][3]):R}] [{F(theirs[i][4]):R}, {F(theirs[i][5]):R}] {blender}");
                        bad = true;
                    }
                    if (bad) wrongCurves++;
                    curves++; fileCurves++;
                }
                else if (t[0] == "END")
                {
                    if (t.Length != 3 || !long.TryParse(t[1], out long nc) || !long.TryParse(t[2], out long nk)) throw new InvalidDataException("a malformed end row");
                    if (nc != fileCurves || nk != fileKeys) throw new InvalidDataException($"the dump is cut short: ends after {nc} curves and {nk} keys but holds {fileCurves} and {fileKeys}");
                    ended = true;
                }
                else throw new InvalidDataException($"a row the drill does not know: '{(line.Length > 60 ? line.Substring(0, 60) : line)}'");
            }
            if (!ended) throw new InvalidDataException("the dump is cut short: no end row");
        }
        for (int i = 0; i < BlenderFCurve.Branches.Length; i++) if (BlenderFCurve.Branches[i].StartsWith("handles:")) Console.WriteLine($"BRANCH\t{BlenderFCurve.Hits[i]}\t{BlenderFCurve.Branches[i]}");
        Console.WriteLine($"TOTAL\tcurves {curves} keys {keysSeen} wrong {wrong} in {wrongCurves} curves nan {nans}");
        if (curves == 0 || keysSeen == 0) { Console.WriteLine("FAIL — the dump holds no curve"); return 1; }
        if (wrong > 0) { Console.WriteLine($"FAIL — {wrong} of {keysSeen} keys have other handles than Blender's ({wrongCurves} of {curves} curves)"); return 1; }
        Console.WriteLine($"PASS — both handles of {keysSeen} keys on {curves} curves equal to Blender's, bit for bit");
        return 0;
    }
}
