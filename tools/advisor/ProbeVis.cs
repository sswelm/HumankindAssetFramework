using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

// THE PROBE'S VERDICTS FOR THE ADVISOR'S CORPUS (2026-11-09, Workshop-Advisor-Plan.md Phase 1): every file given is read as
// the Vehicle Lab reads it (HafModelLoader.ReadForVehicle) and probed by VehicleProbe - the same code path the Lab runs, so
// the visibility verdict (escape rays over one BVH of the whole file, VehicleProbe.Visibility.cs) and the inside-out count
// (VehicleProbe.InsideOut.cs) come out for EVERY part of every file, not only the ones a saved recipe happens to hold.
// corpus.py joins the rows to its own per-node table by the glTF NODE index (a single-mesh file the probe separates into
// loose parts gives several rows for one node; corpus.py folds them). One line per part, tab-separated:
//   FILE\t<path>\t<size>\t<mtime ticks>\tparts=<n>\tsplit=<0|1>\tms=<read+probe>
//   PART\t<path>\t<node>\t<name>\t<verts>\t<vis>\t<flip>\t<bone>
//   LEFT\t<path>\t<reason>      an .fbx the vehicle import leaves to Blender: no rows
//   FAIL\t<path>\t<error>
// The size and write time are what corpus.py checks the table against: a file re-cut after the probe ran is reported stale
// and its rows fall back to the recipe's verdicts, never silently used. Built and run by tools/advisor/probe_vis.sh.
static class ProbeVis
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        int fails = 0;
        var files = new List<string>();
        foreach (var a in args)
        {
            if (a.StartsWith("@")) files.AddRange(File.ReadAllLines(a.Substring(1)).Select(l => l.Trim()).Where(l => l.Length > 0));
            else files.Add(a);
        }
        foreach (var path in files)
        {
            string key = path.Replace('\\', '/');
            try
            {
                var sw = Stopwatch.StartNew();
                var m = HafModelLoader.ReadForVehicle(path, out string leave);
                if (m == null) { Console.WriteLine($"LEFT\t{key}\t{leave}"); continue; }
                var r = VehicleProbe.Run(m);
                sw.Stop();
                var fi = new FileInfo(path);
                Console.WriteLine($"FILE\t{key}\t{fi.Length}\t{fi.LastWriteTimeUtc.Ticks}\tparts={r.Parts.Count}\tsplit={(r.Split ? 1 : 0)}\tms={sw.Elapsed.TotalMilliseconds:0}");
                foreach (var p in r.Parts)
                    Console.WriteLine($"PART\t{key}\t{p.Node}\t{p.Name}\t{p.Verts}\t{p.Vis}\t{p.Flip}\t{p.Bone}");
                Console.Error.WriteLine($"{Path.GetFileName(path)}: {r.Parts.Count} parts, {sw.Elapsed.TotalMilliseconds:0} ms");
            }
            catch (Exception e) { Console.WriteLine($"FAIL\t{key}\t{e.GetType().Name}: {e.Message}"); fails++; }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        return fails == 0 ? 0 : 1;
    }
}
