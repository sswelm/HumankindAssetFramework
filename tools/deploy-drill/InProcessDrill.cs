// A complete Factory conversion under the selected runtime, held to Blender's independently written GLB.
// deploy.exe --in-process <jobs file> <Blender GLB directory> <C# output directory>
using System;
using System.IO;
using System.Linq;

static class InProcessDrill
{
    public static int Run(string[] args)
    {
        Console.WriteLine($"RUNTIME\t{IntPtr.Size * 8}-bit Mono in-process conversion");
        int jobs = 0, written = 0, fails = 0; long bytes = 0;
        try
        {
            if (IntPtr.Size != 8 || Type.GetType("Mono.Runtime") == null || !BlenderTrig.Exact)
                throw new InvalidDataException("complete conversion requires Unity's 64-bit Mono and exact native float math");
            if (args.Length != 3) throw new InvalidDataException("expected jobs, Blender GLB directory and output directory");
            var definitions = File.ReadAllLines(args[0]).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (definitions.Length == 0) throw new InvalidDataException("no conversion jobs to compare");
            Directory.CreateDirectory(args[2]);
            foreach (string line in definitions)
            {
                jobs++;
                string[] t = line.TrimEnd('\r').Split('|');
                string key = t[0];
                try
                {
                    if (t.Length < 4 || key.Length == 0 || Path.GetFileName(key) != key)
                        throw new InvalidDataException("invalid conversion job");
                    string oracle = Path.Combine(args[1], key.Replace(':', '_') + ".glb");
                    if (!File.Exists(oracle)) throw new InvalidDataException("Blender's written GLB is missing");
                    string dst = Path.Combine(args[2], key.Replace(':', '_') + ".glb");
                    if (File.Exists(dst)) File.Delete(dst);
                    var converted = BlenderDeployInProcess.Run(t[1], dst, t.Skip(2).ToArray());
                    if (converted.UseBlender != null) throw new InvalidDataException("conversion did not run to completion: " + converted.UseBlender);
                    if (!converted.Written || converted.ScriptExit || !File.Exists(dst))
                        throw new InvalidDataException("conversion stopped without writing a GLB");
                    byte[] mine = File.ReadAllBytes(dst), blender = File.ReadAllBytes(oracle);
                    if (mine.Length < 20 || !mine.SequenceEqual(blender)) throw new InvalidDataException("written GLB bytes differ from Blender's");
                    written++; bytes += mine.Length;
                    Console.WriteLine($"PASS {key}: {mine.Length} written bytes equal to Blender's");
                }
                catch (Exception e) { fails++; Console.WriteLine($"FAIL {key}: {e.GetType().Name}: {e.Message}"); }
            }
        }
        catch (Exception e) { fails++; Console.WriteLine($"FAIL in-process: {e.GetType().Name}: {e.Message}"); }
        Console.WriteLine($"TOTAL in-process jobs {jobs} written {written} bytes {bytes} failed {fails}");
        return fails == 0 && written > 0 && written == jobs ? 0 : 1;
    }
}
