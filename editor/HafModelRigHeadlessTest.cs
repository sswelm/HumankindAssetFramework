// HafModelRigHeadlessTest.cs - the Clip Range picker's in-process rig (HafModelRig) in UNITY'S OWN RUNTIME, as a Bake Tests row
// and a section of the headless editor lane (HeadlessBakeTests.Run). The reader's pose arithmetic is drilled against Blender
// outside Unity; what only Unity can show is that the rig hands Unity's skinning and transform path the right matrices: for
// every registry .glb/.gltf and every fixture, the rig is built, each clip sampled at its first frame and its middle frame
// (frames of the clip, so the keys hold the sampler's own values), every skinned mesh baked through Unity's skinning and
// every static mesh taken through its transform, and every vertex set beside the reader's posed world position
// (HafTransforms.WorldPositions at HafTransforms.PoseAt) in the preview frame (HafUnityFrame: X mirrored), within 1e-4 of
// the model's extent. The clip names must be Blender's track names. A leaked Unity mesh is a FAIL.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class HafModelRigHeadlessTest
{
    public static BakeTestSection RunSection()
    {
        var s = new BakeTestSection { title = "glTF clip player: every registry model and fixture as a live rig, Unity's skinning against the reader's pose" };
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
        files = files.GroupBy(x => x.path.ToLowerInvariant()).Select(g => g.First()).ToList();
        var assets = new List<UnityEngine.Object>();
        int totalVerts = 0; double worstAll = 0;
        for (int fi = 0; fi < files.Count; fi++)
        {
            var (label, path) = files[fi];
            BakeTestRunnerWindow.Progress.Step($"{label} ({fi + 1}/{files.Count})", (float)fi / Math.Max(1, files.Count));
            BakeTestRunnerWindow.Progress.ThrowIfCancelled();
            int meshesBefore = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            try
            {
                var m = GlbReader.Read(path);
                if (m.Meshes.Any(x => x.Primitives.Any(p => p.MorphTargets > 0))) { s.skip++; body.AppendLine($"SKIP: {label}: morph targets - the picker uses Blender for it"); continue; }
                var rig = HafModelRig.Build(m, assets, 24f);
                var expectedNames = BlenderNames.TrackNames(m);
                if (!rig.ClipNames.SequenceEqual(expectedNames)) { s.fail++; body.AppendLine($"FAIL: {label}: clip names {string.Join(", ", rig.ClipNames)} are not Blender's track names {string.Join(", ", expectedNames)}"); continue; }
                // the poses to judge: the static transforms when there is no clip; else each clip at frame 0 and at its middle frame
                var poses = new List<(string what, int anim, double time)>();
                if (m.Animations.Count == 0) poses.Add(("static", -1, 0));
                for (int ai = 0; ai < m.Animations.Count; ai++)
                {
                    int frames = Mathf.Max(1, Mathf.RoundToInt((float)(m.Animations[ai].Duration * 24f)));
                    poses.Add(($"'{rig.ClipNames[ai]}' frame 0", ai, 0)); poses.Add(($"'{rig.ClipNames[ai]}' frame {frames / 2}", ai, (frames / 2) / 24.0));
                }
                double worst = 0; int judged = 0; string worstWhere = "";
                foreach (var (what, ai, time) in poses)
                {
                    if (ai >= 0) rig.Clips[ai].SampleAnimation(rig.Root, (float)time);
                    // the reader's pose as the rig can hold it: an animated channel's value, else the node's local matrix with its rotation
                    // normalized and a matrix node decomposed (HafUnityFrame.ReaderLocal - a Transform holds no shear, Blender drops it too)
                    var pose = ai >= 0 ? HafTransforms.PoseAt(m, ai, time) : null;
                    var world = HafTransforms.WorldMatrices(m, i => pose?.Invoke(i) ?? HafUnityFrame.ReaderLocal(m.Nodes[i]));
                    double ext = 1e-3;
                    for (int ni = 0; ni < m.Nodes.Count; ni++)
                    {
                        var node = m.Nodes[ni]; if (node.Mesh < 0) continue;
                        var trn = rig.Nodes[ni];
                        var smr = trn.GetComponent<SkinnedMeshRenderer>(); var mf = trn.GetComponent<MeshFilter>();
                        Vector3[] actual; Matrix4x4 toWorld;
                        if (smr != null) { var baked = new Mesh(); smr.BakeMesh(baked); actual = baked.vertices; toWorld = smr.transform.localToWorldMatrix; UnityEngine.Object.DestroyImmediate(baked); }
                        else if (mf != null && mf.sharedMesh != null) { actual = mf.sharedMesh.vertices; toWorld = trn.localToWorldMatrix; }
                        else continue;
                        int at = 0;
                        foreach (var p in m.Meshes[node.Mesh].Primitives)
                        {
                            if (p.TriangleCount == 0) continue;
                            var expect = HafTransforms.WorldPositions(m, ni, p, world);
                            for (int v = 0; v < p.VertexCount; v++)
                            {
                                var a = toWorld.MultiplyPoint3x4(actual[at + v]);
                                double ex = -expect[v * 3], ey = expect[v * 3 + 1], ez = expect[v * 3 + 2];   // the preview frame: X mirrored
                                ext = Math.Max(ext, Math.Max(Math.Abs(ex), Math.Max(Math.Abs(ey), Math.Abs(ez))));
                                double d = Math.Sqrt((a.x - ex) * (a.x - ex) + (a.y - ey) * (a.y - ey) + (a.z - ez) * (a.z - ez));
                                if (d > worst) { worst = d; worstWhere = $"{what}, node {ni} '{node.Name}' vertex {v}: rig ({a.x:0.####}, {a.y:0.####}, {a.z:0.####}) vs reader ({ex:0.####}, {ey:0.####}, {ez:0.####})"; }
                                judged++;
                            }
                            at += p.VertexCount;
                        }
                    }
                    double tol = 1e-4 * ext + 1e-4;
                    if (worst > tol) { s.fail++; body.AppendLine($"FAIL: {label}: a vertex is {worst:0.#####} off the reader's pose (tolerance {tol:0.#####}) - {worstWhere}"); goto next; }
                }
                totalVerts += judged; worstAll = Math.Max(worstAll, worst);
                s.pass++; body.AppendLine($"PASS: {label}: {rig.Meshes} mesh(es) ({rig.SkinnedMeshes} skinned), {rig.Clips.Length} clip(s), {judged} vertex samples within {worst:0.#####} of the reader's pose");
                next:;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { s.fail++; body.AppendLine($"FAIL: {label}: {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                foreach (var a in assets) if (a != null) UnityEngine.Object.DestroyImmediate(a);
                assets.Clear();
                int meshesAfter = Resources.FindObjectsOfTypeAll<Mesh>().Length;
                if (meshesAfter != meshesBefore) { s.fail++; body.AppendLine($"FAIL: {label}: the rig leaked {meshesAfter - meshesBefore} Unity mesh(es)"); }
            }
        }
        body.Insert(0, $"{files.Count} files; {totalVerts} vertex samples judged; the worst deviation {worstAll:0.#####}; frame X mirrored (HafUnityFrame)\n");
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
