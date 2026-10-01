// ModelReaderHeadlessTest.cs - the GLB reader, the preview builder and the writer in UNITY'S OWN RUNTIME, as a
// section of the headless editor lane (HeadlessBakeTests.Run, tools/editor_tests.ps1). The drills prove the same
// code on Unity's Mono outside the editor; this proves it inside: the project's Newtonsoft (Json.Net 11), the
// editor's Mono, the SYSTEM locale a batch run gets (the GUI editor pins invariant culture; a batch run does not -
// learned on the bake fixtures 2026-09-08), Unity's mesh limits. Every registry model and every fixture
// (-hafFixtures <dir>, written by tools/glb-reader-drill/fixtures.py) is: read; built as meshes (ModelPreview;
// the vertex count must be the model's); written to a temporary file and read back equal field by field
// (HafModelDiff), or refused by name for the one reason the contract allows (morph targets). Not in the per-push
// gate: a Unity boot is a minute and hosted CI has no Unity.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class ModelReaderHeadlessTest
{
    public static BakeTestSection RunSection()
    {
        var s = new BakeTestSection { title = "Model Reader: every registry model and fixture through the reader, the preview and the writer, in Unity" };
        var body = new System.Text.StringBuilder();
        var files = new List<(string label, string path)>();
        var entries = ModelRegistry.Load();
        if (ModelRegistry.LastLoadFailed) { s.skip++; body.AppendLine("SKIP: the model registry " + ModelRegistry.LastLoadProblem); }
        else
            foreach (var e in entries)
            {
                string f = e.modelFile ?? "";
                if (!f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(f)) { s.skip++; body.AppendLine($"SKIP: {e.resourceName}: {f} is not on this machine"); continue; }
                files.Add((e.resourceName, f));
            }
        string fixtures = ArgAfter("-hafFixtures");
        if (fixtures != null && Directory.Exists(fixtures))
            foreach (var f in Directory.GetFiles(fixtures).Where(f => f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal))
                files.Add(("fixture " + Path.GetFileName(f), f));
        else { s.skip++; body.AppendLine("SKIP: no fixtures (-hafFixtures <dir> from tools/glb-reader-drill/fixtures.py)"); }
        // two registry entries may share a file; once is enough
        files = files.GroupBy(x => x.path.ToLowerInvariant()).Select(g => g.First()).ToList();

        string tmpDir = Path.Combine(Path.GetTempPath(), "haf_headless_glb_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmpDir);
        var assets = new List<UnityEngine.Object>();
        long bytes = 0; double readMs = 0, writeMs = 0;
        try
        {
            foreach (var (label, path) in files)
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var m = GlbReader.Read(path);
                    sw.Stop(); readMs += sw.Elapsed.TotalMilliseconds; bytes += new FileInfo(path).Length;
                    // the preview: every triangle primitive on every mesh node, as Unity meshes
                    var r = ModelPreview.Build(m, new ModelPreview.Options(), assets);
                    long expectVerts = 0, expectTris = 0;
                    foreach (var n in m.Nodes) if (n.Mesh >= 0) foreach (var p in m.Meshes[n.Mesh].Primitives) if (p.TriangleCount > 0) { expectVerts += p.VertexCount; expectTris += p.TriangleCount; }
                    bool built = r.Root != null;   // judged BEFORE the destroy: a destroyed Unity object compares equal to null (the first run failed all 42 files on exactly that)
                    if (built) UnityEngine.Object.DestroyImmediate(r.Root);
                    foreach (var a in assets) if (a != null) UnityEngine.Object.DestroyImmediate(a);
                    assets.Clear();
                    if (expectTris > 0 && (!built || r.Vertices != expectVerts || r.Triangles != expectTris))
                    { s.fail++; body.AppendLine($"FAIL: {label}: preview built {r.Vertices} vertices / {r.Triangles} triangles, the model draws {expectVerts} / {expectTris} ({r.Note})"); continue; }
                    // the writer: to disk, read back, field by field
                    string target = Path.Combine(tmpDir, Path.GetFileNameWithoutExtension(path) + ".glb");
                    string refused = null;
                    sw.Restart();
                    try { GlbWriter.Write(m, target); }
                    catch (InvalidDataException ex) { refused = ex.Message; }
                    sw.Stop(); writeMs += sw.Elapsed.TotalMilliseconds;
                    if (refused != null)
                    {
                        if (refused.Contains("morph target")) { s.pass++; body.AppendLine($"PASS: {label}: read, previewed; the writer refuses it by name ({refused})"); }
                        else { s.fail++; body.AppendLine($"FAIL: {label}: the writer refused it: {refused}"); }
                        continue;
                    }
                    string diff = HafModelDiff.FirstDifference(m, GlbReader.Read(target));
                    File.Delete(target);
                    if (diff != null) { s.fail++; body.AppendLine($"FAIL: {label}: written and read back, first difference: {diff}"); continue; }
                    s.pass++; body.AppendLine($"PASS: {label}: {m.TriangleCount:N0} tris read, previewed, written and read back equal");
                }
                catch (Exception ex) { s.fail++; body.AppendLine($"FAIL: {label}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
        body.Insert(0, $"{files.Count} files, {bytes / 1e6:0.0} MB: read in {readMs:0} ms, written in {writeMs:0} ms; culture {System.Globalization.CultureInfo.CurrentCulture.Name}\n");
        s.body = body.ToString();
        return s;
    }

    static string ArgAfter(string flag)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
