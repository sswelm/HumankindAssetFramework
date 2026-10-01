using System;
using System.Diagnostics;
using System.IO;
using System.Text;

// THE VEHICLE LAB'S PROBE IN C#, DRILLED AGAINST BLENDER'S (2026-10-02, step 3 of replacing Blender): every file
// given is read by GlbReader and probed by VehicleProbe; the rows are printed as vehicle_rig.py prints them, each
// behind its file's key, for tools/vehicle-probe-drill/compare_probe.py to set beside Blender's own rows.
//   FILE\t<key>\tparts=<n>\trigbones=<n>\tsplit=<0|1>\tms=<read+probe>
//   ROW\t<key>\tPART|name|verts|cx,cy,cz|sx,sy,sz|vis|bone|flip      (and RIGBONE|name|count|c|s)
static class ProbeDrill
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        int fails = 0; double totalMs = 0;
        foreach (var path in args)
        {
            string key = path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').ToLowerInvariant();
            try
            {
                var sw = Stopwatch.StartNew();
                var m = GlbReader.Read(path);
                var r = VehicleProbe.Run(m);
                sw.Stop(); totalMs += sw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"FILE\t{key}\tparts={r.Parts.Count}\trigbones={r.RigBones.Count}\tsplit={(r.Split ? 1 : 0)}\tms={sw.Elapsed.TotalMilliseconds:0}");
                foreach (var b in r.RigBones) Console.WriteLine($"ROW\t{key}\t{b.Row}");
                foreach (var p in r.Parts) Console.WriteLine($"ROW\t{key}\t{p.Row}");
            }
            catch (Exception e) { Console.WriteLine($"FAIL\t{key}\t{e.GetType().Name}: {e.Message}"); fails++; }
            // the Lab's sources are the unreduced originals (one is 398 MB): collect between files, or Mono's large-object space fragments into "Insufficient memory"
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Console.WriteLine($"TOTAL\tfiles={args.Length}\tms={totalMs:0}");
        return fails == 0 ? 0 : 1;
    }
}
