using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

// THE VEHICLE LAB'S PROBE IN C#, DRILLED AGAINST BLENDER'S (2026-10-02, step 3 of replacing Blender): every file
// given is read as the Lab reads it (HafModelLoader.ReadForVehicle: a .glb/.gltf through GlbReader, an .fbx through the native
// FBX reader - step 6f, 2026-11-02) and probed by VehicleProbe; the rows are printed as vehicle_rig.py prints them, each
// behind its file's key, for tools/vehicle-probe-drill/compare_probe.py to set beside Blender's own rows.
//   FILE\t<key>\tparts=<n>\trigbones=<n>\tsplit=<0|1>\tms=<read+probe>
//   LEFT\t<key>\t<reason>     an .fbx the vehicle import leaves to Blender by name (skinned, animated, the reader's own limit): no rows
//   ROW\t<key>\tPART|name|verts|cx,cy,cz|sx,sy,sz|vis|bone|flip      (and RIGBONE|name|count|c|s, MATRIX|name|16 floats, VERTEX|name|p|n)
// A JOBS file (an argument `@<path>.json`, written by probe_jobs.py) adds probes with the Lab's OTHER inputs (step 3d):
//   [{"key": "...", "file": "...", "merge2": "path|ox,oy,oz|rx,ry,rz|sx,sy,sz" | null, "parttx": ["name|o|s", ...] | null, "proberot": "x,y,z" | null}]
// - the second model, the per-part placements and the orientation, as the SAME text the Lab hands vehicle_rig.py; the rows
// come out behind the job's key, and blender_probe_many.py runs Blender's probe on the same jobs.
static class ProbeDrill
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        int fails = 0, left = 0; double totalMs = 0; int count = 0;
        foreach (var arg in args)
        {
            if (arg.StartsWith("@") && arg.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var job in JArray.Parse(File.ReadAllText(arg.Substring(1))))
                {
                    count++;
                    string key = (string)job["key"];
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var input = new VehicleProbe.Input { Model = Read((string)job["file"]) };
                        string merge2 = (string)job["merge2"];
                        if (!string.IsNullOrEmpty(merge2) && !input.SetSecond(merge2, Read, out string error)) throw new InvalidDataException(error);
                        if (job["parttx"] is JArray lines) input.AddPlacementLines(lines.Select(l => (string)l));
                        string proberot = (string)job["proberot"];
                        if (!string.IsNullOrEmpty(proberot)) input.SetProbeRotation(proberot);
                        Print(key, VehicleProbe.Run(input), sw);
                        totalMs += sw.Elapsed.TotalMilliseconds;
                    }
                    catch (NamedLeave e) { Console.WriteLine($"LEFT\t{key}\t{e.Message}"); left++; }
                    catch (Exception e) { Console.WriteLine($"FAIL\t{key}\t{e.GetType().Name}: {e.Message}"); fails++; }
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                }
                continue;
            }
            count++;
            string path = arg;
            string fileKey = path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').ToLowerInvariant();
            try
            {
                var sw = Stopwatch.StartNew();
                var m = Read(path);
                var r = VehicleProbe.Run(m);
                Print(fileKey, r, sw);
                totalMs += sw.Elapsed.TotalMilliseconds;
            }
            catch (NamedLeave e) { Console.WriteLine($"LEFT\t{fileKey}\t{e.Message}"); left++; }
            catch (Exception e) { Console.WriteLine($"FAIL\t{fileKey}\t{e.GetType().Name}: {e.Message}"); fails++; }
            // the Lab's sources are the unreduced originals (one is 398 MB): collect between files, or Mono's large-object space fragments into "Insufficient memory"
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Console.WriteLine($"TOTAL\tfiles={count}\tleft={left}\tms={totalMs:0}");
        return fails == 0 ? 0 : 1;
    }

    sealed class NamedLeave : Exception { public NamedLeave(string message) : base(message) { } }
    // the Lab's own read (VehicleLabWindow.BuildProbeInput: HafModelLoader.ReadForVehicleOrThrow): an .fbx the vehicle import leaves is a LEFT row
    static HafModel Read(string path) { var m = HafModelLoader.ReadForVehicle(path, out string leave); if (m == null) throw new NamedLeave(leave); return m; }

    static void Print(string key, VehicleProbe.Result r, Stopwatch sw)
    {
        sw.Stop();
        Console.WriteLine($"FILE\t{key}\tparts={r.Parts.Count}\trigbones={r.RigBones.Count}\tsplit={(r.Split ? 1 : 0)}\tms={sw.Elapsed.TotalMilliseconds:0}");
        foreach (var n in r.Notes) Console.WriteLine($"ROW\t{key}\tVEHICLE {n}");
        foreach (var b in r.RigBones) Console.WriteLine($"ROW\t{key}\t{b.Row}");
        foreach (var p in r.Parts) Console.WriteLine($"ROW\t{key}\t{p.Row}");
        foreach (var p in r.Parts) if (p.BlenderMatrix != null) Console.WriteLine($"ROW\t{key}\t{p.MatrixRow}");
        foreach (var p in r.Parts) if (p.FirstVertex != null) Console.WriteLine($"ROW\t{key}\t{p.VertexRow}");
    }
}
