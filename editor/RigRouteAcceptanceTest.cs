// RigRouteAcceptanceTest.cs - the GLB rig route against the FBX rig route, bake for bake, in UNITY'S OWN RUNTIME, as a Bake
// Tests row (step (h) of the rig_anim port, 2026-10-14; docs/Review-Backlog.md item (4)). The push gate holds the C# port to
// rig_anim.py row for row and the GLB it writes byte for byte, and the "rig slim in C#" row holds the same in Unity; what
// neither can show is that the FACTORY makes the same model of that GLB as it makes of the script's FBX files: the rig Unity
// imports (ModelImporter on the FBX; HafModelRig on the GLB), the clip per role, the Amplitude skeleton and clip collections.
// This row bakes every animated glTF entry of the registry twice under throwaway names - the recipe as it is, rigRoute
// "blender" and then "csharp" - and compares the outputs in Unity: the Amplitude assets present and non-empty on both; the
// imported rig of every role (UniversalBaker.RigPreviewPath resolves either route's): the bones by name, the skinned mesh's
// triangle count, bounds and the SET of vertex positions (the FBX import splits vertices at seams, the glTF carries them
// split already - the multiset of positions is what both must share), the root's placement; every role's clip: its length
// and, at its first, middle and last frame, the skinned vertices (SkinnedMeshRenderer.BakeMesh) and every bone's world
// position; and each role's baked pose stream length (frames x bones). Positions within 1e-3 of the rig's extent.
// A recipe the port leaves to Blender by name bakes through the FBX route both times: SKIP, saying why. A port crash is a
// FAIL. The in-game check of each model playing its roles stays the user's; only the two together flip the default.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class RigRouteAcceptanceTest
{
    static readonly string[] RoleDirs = { "anim", "anim_move", "anim_after", "anim_attack", "anim_combat", "anim_premove", "anim_idle", "anim_idlealt", "anim_idlealt2" };
    static readonly string[] RoleSuffixes = { "_Clips", "_ClipsMove", "_ClipsAfter", "_ClipsAttack", "_ClipsCombat", "_ClipsPreMove", "_ClipsIdle", "_ClipsIdleAlt", "_ClipsIdleAlt2" };

    public static BakeTestSection RunSection()
    {
        var s = new BakeTestSection { title = "rig route acceptance: every animated glTF entry baked on the FBX route and on the GLB route, the Factory's outputs compared in Unity" };
        var body = new System.Text.StringBuilder();
        var entries = ModelRegistry.Load();
        if (ModelRegistry.LastLoadFailed) { s.skip++; s.body = "SKIP: the model registry " + ModelRegistry.LastLoadProblem; return s; }
        if (!UniversalBaker.BlenderAvailable()) { s.skip++; s.body = "SKIP: Blender is not installed - the FBX route has nothing to bake with"; return s; }
        var jobs = entries.Where(e => e.animated && !e.resourceName.StartsWith(BakeSmokeTest.PREFIX, StringComparison.Ordinal)).ToList();
        if (jobs.Count == 0) { s.skip++; s.body = "SKIP: no registry entry is animated - nothing to bake both ways"; return s; }
        int left = 0, notGlb = 0, equal = 0; bool cancelled = false;
        for (int i = 0; i < jobs.Count && !cancelled; i++)
        {
            var e = jobs[i];
            if (BakeTestRunnerWindow.Progress.CancelRequested) break;
            if (string.IsNullOrWhiteSpace(e.modelFile) || !File.Exists(e.modelFile)) { s.skip++; body.AppendLine($"SKIP: {e.resourceName}: {e.modelFile} is not on this machine"); continue; }
            string ext = Path.GetExtension(e.modelFile).ToLowerInvariant();
            if (ext != ".glb" && ext != ".gltf") { s.skip++; notGlb++; body.AppendLine($"SKIP: {e.resourceName}: {Path.GetFileName(e.modelFile)} is not a glTF file - the port reads .glb/.gltf only, the FBX route stays Blender's"); continue; }
            // the run position rides in the throwaway name (Unity's own Importing modal shows the asset path and nothing else of ours);
            // the prefix first: the delete guard, the registry filter and Ship Status match it
            string nameA = BakeSmokeTest.PREFIX + $"{i + 1:00}of{jobs.Count:00}_fbx_" + e.resourceName;
            string nameB = BakeSmokeTest.PREFIX + $"{i + 1:00}of{jobs.Count:00}_glb_" + e.resourceName;
            try
            {
                UniversalBaker.QuietDialogs = true;
                BakeTestRunnerWindow.Progress.Step($"{e.resourceName} ({i + 1}/{jobs.Count}) - the FBX route", (float)i / jobs.Count);
                var rA = BakeOne(e, nameA, "blender");
                if (!rA.ok) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the FBX route's bake failed - {rA.error}"); continue; }
                BakeTestRunnerWindow.Progress.ThrowIfCancelled();
                BakeTestRunnerWindow.Progress.Step($"{e.resourceName} ({i + 1}/{jobs.Count}) - the GLB route", (float)(i + 0.5) / jobs.Count);
                var rB = BakeOne(e, nameB, "csharp");
                if (!rB.ok) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the GLB route's bake failed - {rB.error}"); continue; }
                if (rB.rigRoute != "csharp")
                {
                    bool crash = (rB.rigRouteNote ?? "").StartsWith("the C# rig run failed", StringComparison.Ordinal);
                    if (crash) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the port crashed and the bake took the FBX route - {rB.rigRouteNote} (a crash is not a named shape)"); continue; }
                    s.skip++; left++; body.AppendLine($"SKIP: {e.resourceName}: left to Blender by name, the bake took the FBX route twice - {rB.rigRouteNote}"); continue;
                }
                var problems = new List<string>(); var notes = new List<string>();
                var missingA = BakeSmokeTest.MissingAssets(nameA, true); var missingB = BakeSmokeTest.MissingAssets(nameB, true);
                if (missingA.Count > 0) problems.Add("the FBX route's bake lacks " + string.Join(", ", missingA.Select(x => x.Substring(nameA.Length))));
                if (missingB.Count > 0) problems.Add("the GLB route's bake lacks " + string.Join(", ", missingB.Select(x => x.Substring(nameB.Length))));
                if (problems.Count == 0) CompareBakes(nameA, nameB, problems, notes);
                if (problems.Count > 0) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: " + string.Join("; ", problems)); }
                else { s.pass++; equal++; body.AppendLine($"PASS: {e.resourceName}: " + string.Join("; ", notes)); }
            }
            // a cancel keeps every verdict made so far (an hour's worth): the entry under way is named, the rest are not run
            catch (OperationCanceledException) { cancelled = true; body.AppendLine($"CANCELLED by the user during {e.resourceName} - the entries above are counted, the rest were not run"); }
            catch (Exception ex) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                UniversalBaker.QuietDialogs = false;
                BakeSmokeTest.CleanupTestAssets(nameA); BakeSmokeTest.CleanupTestAssets(nameB);
            }
        }
        body.Insert(0, $"{jobs.Count} animated registry entr{(jobs.Count == 1 ? "y" : "ies")}; {equal} baked both ways and equal in Unity, {left} left to Blender by name, {notGlb} not glTF sources{(cancelled ? " (cancelled before the end)" : "")}\n");
        s.body = body.ToString();
        return s;
    }

    static BakeResult BakeOne(ModelDef src, string testName, string route)
    {
        var clone = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(src));   // never the real entry
        clone.resourceName = testName; clone.rigRoute = route; clone.reuseExtracted = false;
        var cfg = ModelFactoryWindow.ConfigFor(clone);
        return UniversalBaker.BuildAnimated(cfg);
    }

    /// <summary>The two bakes' outputs side by side: the rig of every role, its clip, the pose stream lengths.</summary>
    static void CompareBakes(string nameA, string nameB, List<string> problems, List<string> notes)
    {
        string root = Directory.GetParent(Application.dataPath).FullName;
        int roles = 0, vertexSamples = 0; double worst = 0;
        for (int ri = 0; ri < RoleDirs.Length; ri++)
        {
            string dir = RoleDirs[ri];
            string pathA = UniversalBaker.RigPreviewPath(nameA, dir, out var clipA), pathB = UniversalBaker.RigPreviewPath(nameB, dir, out var clipB);
            if (pathA == null && pathB == null) continue;
            if (pathA == null || pathB == null) { problems.Add($"role '{dir}' is baked on the {(pathA == null ? "GLB" : "FBX")} route only"); continue; }
            if (clipA == null || clipB == null) { problems.Add($"role '{dir}' has no clip on the {(clipA == null ? "FBX" : "GLB")} route"); continue; }
            roles++;
            var goA = AssetDatabase.LoadAssetAtPath<GameObject>(pathA); var goB = AssetDatabase.LoadAssetAtPath<GameObject>(pathB);
            GameObject instA = null, instB = null;
            try
            {
                instA = UnityEngine.Object.Instantiate(goA); instB = UnityEngine.Object.Instantiate(goB);
                instA.hideFlags = instB.hideFlags = HideFlags.HideAndDontSave;
                instA.transform.position = instB.transform.position = Vector3.zero; instA.transform.rotation = instB.transform.rotation = Quaternion.identity; instA.transform.localScale = instB.transform.localScale = Vector3.one;
                CompareRole(dir, instA, instB, clipA, clipB, problems, ref vertexSamples, ref worst);
            }
            finally
            {
                if (instA != null) UnityEngine.Object.DestroyImmediate(instA);
                if (instB != null) UnityEngine.Object.DestroyImmediate(instB);
            }
            // the baked pose stream: frames x bones x the stride - the same clip through the same skeleton gives the same length
            string poseA = Path.Combine(root, "Assets", "Resources", nameA + RoleSuffixes[ri] + "PoseData.bytes"), poseB = Path.Combine(root, "Assets", "Resources", nameB + RoleSuffixes[ri] + "PoseData.bytes");
            long lenA = File.Exists(poseA) ? new FileInfo(poseA).Length : -1, lenB = File.Exists(poseB) ? new FileInfo(poseB).Length : -1;
            if (lenA != lenB) problems.Add($"role '{dir}': the baked pose stream is {lenA} bytes on the FBX route, {lenB} on the GLB route");
        }
        if (roles == 0) problems.Add("no role's rig could be found on either route");
        notes.Add(FormattableString.Invariant($"{roles} role(s) equal: bones, mesh, placement and clips, {vertexSamples} skinned vertex samples within {worst:0.#####} of each other; the pose streams of the same length"));
    }

    /// <summary>Two instantiated rigs with a clip each, judged as the row judges a role (tools/riganim_unity_import_drill.sh runs
    /// this on two imports of one fixture, then on a scaled one and on a clip of another length).</summary>
    internal static List<string> CompareRigs(GameObject a, GameObject b, AnimationClip clipA, AnimationClip clipB)
    {
        var problems = new List<string>(); int samples = 0; double worst = 0;
        CompareRole("test", a, b, clipA, clipB, problems, ref samples, ref worst);
        return problems;
    }

    static void CompareRole(string dir, GameObject a, GameObject b, AnimationClip clipA, AnimationClip clipB, List<string> problems, ref int vertexSamples, ref double worst)
    {
        var smrA = a.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToArray();
        var smrB = b.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToArray();
        if (smrA.Length == 0 || smrB.Length == 0) { problems.Add($"role '{dir}': no skinned mesh on the {(smrA.Length == 0 ? "FBX" : "GLB")} route's rig"); return; }
        // the extent, for the tolerance: both rigs are scaled to the recipe's size
        var boundsA = WorldBounds(smrA); var boundsB = WorldBounds(smrB);
        float ext = Mathf.Max(1e-3f, Mathf.Max(boundsA.size.x, Mathf.Max(boundsA.size.y, boundsA.size.z)));
        float tol = 1e-3f * ext;
        // the bones by name
        var bonesA = smrA.SelectMany(r => r.bones).Where(t => t != null).Select(t => t.name).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var bonesB = smrB.SelectMany(r => r.bones).Where(t => t != null).Select(t => t.name).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!bonesA.SequenceEqual(bonesB))
        {
            problems.Add($"role '{dir}': the bones differ - FBX route {bonesA.Length} ({string.Join(", ", bonesA.Except(bonesB).Take(5))} only there), GLB route {bonesB.Length} ({string.Join(", ", bonesB.Except(bonesA).Take(5))} only there)");
            return;
        }
        // the mesh: triangles, bounds, the set of positions
        int triA = smrA.Sum(r => Enumerable.Range(0, r.sharedMesh.subMeshCount).Sum(si => (int)r.sharedMesh.GetIndexCount(si) / 3));
        int triB = smrB.Sum(r => Enumerable.Range(0, r.sharedMesh.subMeshCount).Sum(si => (int)r.sharedMesh.GetIndexCount(si) / 3));
        if (triA != triB) problems.Add($"role '{dir}': {triA} triangles on the FBX route, {triB} on the GLB route");
        if ((boundsA.min - boundsB.min).magnitude > tol || (boundsA.max - boundsB.max).magnitude > tol)
            problems.Add(FormattableString.Invariant($"role '{dir}': the rest bounds differ - FBX route {boundsA.min}..{boundsA.max}, GLB route {boundsB.min}..{boundsB.max} (tolerance {tol:0.####})"));
        // the placement: the root-most bone's world position (the model's box is centred by the slim; the Factory grounds the rest)
        var rootA = RootBone(smrA[0]); var rootB = RootBone(smrB[0]);
        if (rootA != null && rootB != null && (rootA.position - rootB.position).magnitude > tol)
            problems.Add(FormattableString.Invariant($"role '{dir}': the root bone '{rootA.name}' sits at {rootA.position} on the FBX route, {rootB.position} on the GLB route"));
        // the clip: its length, then the skinned vertices and the bone positions at its first, middle and last frame
        if (Mathf.Abs(clipA.length - clipB.length) > 1f / 48f + 1e-4f) problems.Add(FormattableString.Invariant($"role '{dir}': the clip is {clipA.length:0.####} s on the FBX route, {clipB.length:0.####} s on the GLB route"));
        float len = Mathf.Min(clipA.length, clipB.length);
        foreach (float t in new[] { 0f, len / 2f, len })
        {
            clipA.SampleAnimation(a, t); clipB.SampleAnimation(b, t);
            var byNameA = BonesByName(smrA); var byNameB = BonesByName(smrB);
            foreach (var name in bonesA)
            {
                if (!byNameA.TryGetValue(name, out var ta) || !byNameB.TryGetValue(name, out var tb)) continue;
                float d = (ta.position - tb.position).magnitude;
                if (d > tol) { problems.Add(FormattableString.Invariant($"role '{dir}' at {t:0.###} s: bone '{name}' is {d:0.####} apart ({ta.position} against {tb.position}; tolerance {tol:0.####})")); break; }
                worst = Math.Max(worst, d);
            }
            var cloudA = Positions(smrA); var cloudB = Positions(smrB);
            vertexSamples += cloudA.Count;
            string cloudProblem = ComparePositionSets(cloudA, cloudB, tol, out double cloudWorst);
            worst = Math.Max(worst, cloudWorst);
            if (cloudProblem != null) { problems.Add(FormattableString.Invariant($"role '{dir}' at {t:0.###} s: the skinned vertices differ - {cloudProblem}")); break; }
        }
    }

    static Bounds WorldBounds(SkinnedMeshRenderer[] rs)
    {
        Bounds? all = null;
        foreach (var r in rs) { var m = new Mesh(); r.BakeMesh(m); var l2w = r.transform.localToWorldMatrix; foreach (var v in m.vertices) { var w = l2w.MultiplyPoint3x4(v); if (all == null) all = new Bounds(w, Vector3.zero); else { var bb = all.Value; bb.Encapsulate(w); all = bb; } } UnityEngine.Object.DestroyImmediate(m); }
        return all ?? new Bounds();
    }

    static Transform RootBone(SkinnedMeshRenderer r)
    {
        var set = new HashSet<Transform>(r.bones.Where(t => t != null));
        foreach (var t in r.bones) if (t != null && (t.parent == null || !set.Contains(t.parent))) return t;
        return r.rootBone;
    }

    static Dictionary<string, Transform> BonesByName(SkinnedMeshRenderer[] rs)
    {
        var d = new Dictionary<string, Transform>(StringComparer.Ordinal);
        foreach (var r in rs) foreach (var t in r.bones) if (t != null && !d.ContainsKey(t.name)) d[t.name] = t;
        return d;
    }

    /// <summary>Every skinned vertex in world space, the renderer's pose baked (BakeMesh gives the renderer's space).</summary>
    static List<Vector3> Positions(SkinnedMeshRenderer[] rs)
    {
        var all = new List<Vector3>();
        foreach (var r in rs) { var m = new Mesh(); r.BakeMesh(m); var l2w = r.transform.localToWorldMatrix; foreach (var v in m.vertices) all.Add(l2w.MultiplyPoint3x4(v)); UnityEngine.Object.DestroyImmediate(m); }
        return all;
    }

    /// <summary>The two position sets within `tol` of each other: every point of each has a point of the other within tol (a grid of
    /// tol cells, the 27 neighbours looked up). Vertex counts may differ - the FBX import splits at seams - the sets may not.</summary>
    internal static string ComparePositionSets(List<Vector3> a, List<Vector3> b, float tol, out double worst)
    {
        worst = 0;
        if (a.Count == 0 || b.Count == 0) return $"{a.Count} vertices on the FBX route, {b.Count} on the GLB route";
        string one = Unmatched(a, b, tol, ref worst, "FBX"); if (one != null) return one;
        return Unmatched(b, a, tol, ref worst, "GLB");
    }

    static string Unmatched(List<Vector3> from, List<Vector3> into, float tol, ref double worst, string side)
    {
        float cell = Mathf.Max(tol, 1e-6f);
        var grid = new Dictionary<(int, int, int), List<Vector3>>();
        foreach (var p in into)
        {
            var k = Key(p, cell);
            if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<Vector3>();
            list.Add(p);
        }
        int unmatched = 0; Vector3 worstPoint = Vector3.zero; float worstD = 0;
        foreach (var p in from)
        {
            var (kx, ky, kz) = Key(p, cell); float best = float.MaxValue;
            for (int dx = -1; dx <= 1 && best > tol; dx++) for (int dy = -1; dy <= 1 && best > tol; dy++) for (int dz = -1; dz <= 1 && best > tol; dz++)
                if (grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) foreach (var q in list) { float d = (p - q).magnitude; if (d < best) best = d; }
            if (best > tol) { unmatched++; if (best > worstD) { worstD = best; worstPoint = p; } }
            else worst = Math.Max(worst, best);
        }
        return unmatched == 0 ? null : FormattableString.Invariant($"{unmatched} of {from.Count} vertices of the {side} route have no counterpart within {tol:0.####} (the farthest at {worstPoint}, {worstD:0.####} away)");
    }

    static (int, int, int) Key(Vector3 p, float cell) => (Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
}
