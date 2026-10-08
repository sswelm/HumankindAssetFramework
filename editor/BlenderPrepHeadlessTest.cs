// BlenderPrepHeadlessTest.cs - the model prep in C# (BlenderPrep, UniversalBaker.PrepInProcess) in UNITY'S OWN RUNTIME, as a
// Bake Tests row. The push gate holds the prepared file to Blender's outside Unity (tools/prep_drill.sh: the converter's
// output of this file and of Blender's, byte for byte, on every registry model, recipe source and fixture) - in a
// .NET Framework process. What only Unity can show is that the same code gives the same file under Unity's Mono, which
// evaluates an uncast float product in double (the decimate port casts every one; this row is what says so here): for
// every registry entry that preps a .glb/.gltf (a reduce target, with its strip list and the double-sided halving, as
// the Factory computes them), the file is prepared in process AND by Blender's prep_model.py, the Factory's converter
// runs on both at the entry's own grid, and its two output folders must be equal file by file, byte for byte.
// An entry the C# prep leaves to Blender is a SKIP that names why - that is the fallback working, not a failure.
// NOT covered: district entries (DistrictRegistry) - they prep through the same UniversalBaker.Build, on other sources.
using System;
using System.IO;
using System.Linq;
using UnityEngine;

public static class BlenderPrepHeadlessTest
{
    public static BakeTestSection RunSection()
    {
        var s = new BakeTestSection { title = "model prep in C#: the converter's output of the in-process prep against Blender's prep_model.py, in Unity" };
        var body = new System.Text.StringBuilder();
        var entries = ModelRegistry.Load();
        if (ModelRegistry.LastLoadFailed) { s.skip++; s.body = "SKIP: the model registry " + ModelRegistry.LastLoadProblem; return s; }
        if (!UniversalBaker.BlenderAvailable()) { s.skip++; s.body = "SKIP: Blender is not installed - there is nothing to hold the in-process prep to"; return s; }
        var jobs = entries.Where(e => (e.modelFile ?? "").EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || (e.modelFile ?? "").EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
                          .Where(e => e.targetTris > 0 && !e.animated).ToList();   // the entries the Factory preps: static ones (an animated entry goes through the rig path)
        string root = Path.Combine(Path.GetTempPath(), "haf_prep_test");
        int left = 0;
        for (int i = 0; i < jobs.Count; i++)
        {
            var e = jobs[i];
            BakeTestRunnerWindow.Progress.Step($"{e.resourceName} ({i + 1}/{jobs.Count})", (float)i / Math.Max(1, jobs.Count));
            BakeTestRunnerWindow.Progress.ThrowIfCancelled();
            if (!File.Exists(e.modelFile)) { s.skip++; body.AppendLine($"SKIP: {e.resourceName}: {e.modelFile} is not on this machine"); continue; }
            try
            {
                string strip = string.IsNullOrWhiteSpace(e.stripParts) ? "" : e.stripParts;
                int target = e.doubleSided ? Mathf.Max(1, e.targetTris / 2) : e.targetTris;
                // one file name in two folders: the converter writes the name into the OBJ's first line
                string dirA = Path.Combine(root, "cs"), dirB = Path.Combine(root, "blender"), outA = Path.Combine(root, "out_cs"), outB = Path.Combine(root, "out_blender");
                foreach (var d in new[] { dirA, dirB, outA, outB }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }
                string glbA = Path.Combine(dirA, "prepped.glb"), glbB = Path.Combine(dirB, "prepped.glb");
                if (!UniversalBaker.PrepInProcess(e.modelFile, glbA, strip, target, out string useBlender))
                { s.skip++; left++; body.AppendLine($"SKIP: {e.resourceName}: left to Blender - {useBlender}"); continue; }
                int trisInProcess = UniversalBaker.LastPrepSourceTris;
                if (!UniversalBaker.PrepViaBlender(e.modelFile, glbB, strip, target)) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: Blender's prep failed on a file the C# prep wrote (see the console)"); continue; }
                if (trisInProcess != UniversalBaker.LastPrepSourceTris) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the source has {trisInProcess} triangles to the C# prep, {UniversalBaker.LastPrepSourceTris} to Blender's"); continue; }
                if (!UniversalBaker.ConvertGlb(glbA, outA, "model", e.convertGrid) || !UniversalBaker.ConvertGlb(glbB, outB, "model", e.convertGrid))
                { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the converter failed (see the console)"); continue; }
                var fa = Directory.GetFiles(outA).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var fb = Directory.GetFiles(outB).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (!fa.SequenceEqual(fb)) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the converter wrote [{string.Join(", ", fa)}] from the in-process file, [{string.Join(", ", fb)}] from Blender's"); continue; }
                string differs = null;
                foreach (var f in fa)
                {
                    byte[] a = File.ReadAllBytes(Path.Combine(outA, f)), b = File.ReadAllBytes(Path.Combine(outB, f));
                    if (a.SequenceEqual(b)) continue;
                    differs = $"{f} differs ({a.Length} bytes in process, {b.Length} from Blender's)";
                    if (f.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] la = File.ReadAllLines(Path.Combine(outA, f)), lb = File.ReadAllLines(Path.Combine(outB, f));
                        int at = 0; while (at < la.Length && at < lb.Length && la[at] == lb[at]) at++;
                        differs = $"{f} differs from line {at + 1}: '{(at < la.Length ? la[at] : "<end>")}' in process, '{(at < lb.Length ? lb[at] : "<end>")}' from Blender's";
                    }
                    break;
                }
                if (differs != null) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: {differs}"); continue; }
                s.pass++; body.AppendLine($"PASS: {e.resourceName}: {fa.Count} converter file(s) equal ({trisInProcess} source triangles, target {target}{(strip.Length > 0 ? ", strip '" + strip + "'" : "")}, grid {e.convertGrid})");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: {ex.GetType().Name}: {ex.Message}"); }
        }
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        body.Insert(0, $"{jobs.Count} registry entries prep a .glb/.gltf; {s.pass} equal through the converter, {left} left to Blender by name\n");
        s.body = body.ToString();
        return s;
    }
}
