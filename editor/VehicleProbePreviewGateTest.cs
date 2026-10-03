// VehicleProbePreviewGateTest.cs - the in-process probe PREVIEW against the Blender preview FBX Unity imported for the same source
// (2026-10-03, step 3d of replacing Blender; run via Tools > HAF > Bake Tests). The C# probe's ROWS are held to Blender's own
// probe by tools/vehicle_probe_drill.sh; what no drill outside Unity can hold is the preview: the FRAME the parts land in
// (Blender's (X, Y, Z) drawn as Unity's (X, Z, Y) with the faces rewound - what the FBX exporter and Unity's importer gave the
// old preview), the FACING of the faces, and that every Unity object the preview makes is destroyed with it. The oracle is
// already in the project: every source the Lab ever probed through Blender left <source>_probe.fbx under
// Assets/FactorySource/VehicleLab, imported by Unity's own FBX importer. For every saved recipe whose source is a .glb/.gltf and
// whose preview FBX exists, the in-process preview is built for the recipe's inputs and set beside the imported FBX part for part
// by NAME: the world bounds centre and size (tolerance 1e-3 of the model's extent), and the facing - the sign of the sum over the
// faces of (area-weighted normal) . (face centre - part centre), which a wrong frame or a wrong winding turns (a flat part gives
// no sign and is not judged). Parts on one side only are counted, not held: the FBX is as old as the last Blender probe of that
// source, and the recipe may have gained a placement or a second model since. A leaked Unity mesh is a FAIL.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class VehicleProbePreviewGateTest
{
    const string RecipesDir = "Assets/FactorySource/VehicleLab/Recipes";
    const string PrevDir = "Assets/FactorySource/VehicleLab";

    public static BakeTestSection RunSection()
    {
        var s = new BakeTestSection { title = "In-process probe preview — every recipe with a Blender preview FBX, part for part against Unity's import of it" };
        var body = new StringBuilder();
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        string recipesDir = Path.Combine(projRoot, RecipesDir);
        if (!Directory.Exists(recipesDir)) { s.skip = 1; s.body = "SKIP — no Vehicle Lab recipes (" + RecipesDir + ")"; return s; }
        var recipes = Directory.GetFiles(recipesDir, "*.json").OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
        var assets = new List<UnityEngine.Object>();
        int judged = 0, matchedTotal = 0;
        for (int i = 0; i < recipes.Count; i++)
        {
            string recipe = recipes[i]; string name = Path.GetFileNameWithoutExtension(recipe);
            BakeTestRunnerWindow.Progress.Step($"{name} ({i + 1}/{recipes.Count}) — probing in-process…", (float)i / recipes.Count);
            BakeTestRunnerWindow.Progress.ThrowIfCancelled();
            if (!VehicleLabWindow.ReadRecipe(recipe, out string src, out _)) { s.skip++; body.AppendLine($"SKIP {name} — not a Vehicle Lab recipe"); continue; }
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) { s.skip++; body.AppendLine($"SKIP {name} — source {src} is not on this machine"); continue; }
            string prevRel = PrevDir + "/" + Path.GetFileNameWithoutExtension(src) + "_probe.fbx";
            if (!File.Exists(Path.Combine(projRoot, prevRel))) { s.skip++; body.AppendLine($"SKIP {name} — no Blender preview FBX ({prevRel}): probe it once through Blender to make one"); continue; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prevRel);
            if (prefab == null) { s.skip++; body.AppendLine($"SKIP {name} — {prevRel} is not imported"); continue; }
            int meshesBefore = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            GameObject preview = null;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (!VehicleLabWindow.ProbeRecipeHeadless(recipe, assets, out var r, out preview, out string error)) { s.skip++; body.AppendLine($"SKIP {name} — {error}"); continue; }
                sw.Stop();
                var fbx = Parts(prefab); var mine = Parts(preview);
                float ext = Mathf.Max(fbx.Values.Select(p => p.bounds.size.magnitude).DefaultIfEmpty(0f).Max(), 1e-3f);
                float tol = 1e-3f * ext + 1e-3f;
                int matched = 0, centreOff = 0, sizeOff = 0, facingOff = 0, facingJudged = 0; float worst = 0f;
                var offenders = new List<string>();
                foreach (var kv in mine)
                {
                    if (!fbx.TryGetValue(kv.Key, out var f)) continue;
                    matched++;
                    float dc = (kv.Value.bounds.center - f.bounds.center).magnitude, ds = (kv.Value.bounds.size - f.bounds.size).magnitude;
                    worst = Mathf.Max(worst, dc);
                    bool bad = false;
                    if (dc > tol) { centreOff++; bad = true; }
                    if (ds > tol) { sizeOff++; bad = true; }
                    if (kv.Value.facing != 0 && f.facing != 0) { facingJudged++; if (kv.Value.facing != f.facing) { facingOff++; bad = true; } }
                    if (bad && offenders.Count < 3) offenders.Add($"{kv.Key}: centre {Fmt(kv.Value.bounds.center)} vs FBX {Fmt(f.bounds.center)}, size {Fmt(kv.Value.bounds.size)} vs {Fmt(f.bounds.size)}, facing {kv.Value.facing:+0;-0;0} vs {f.facing:+0;-0;0}");
                }
                int onlyMine = mine.Keys.Count(k => !fbx.ContainsKey(k)), onlyFbx = fbx.Keys.Count(k => !mine.ContainsKey(k));
                judged++; matchedTotal += matched;
                string counts = $"{matched} parts matched by name ({onlyMine} only in the probe, {onlyFbx} only in the FBX - a preview as old as the last Blender probe), " +
                                $"{matched - centreOff} centres and {matched - sizeOff} sizes within {tol:0.####} (worst centre {worst:0.####}), facing agrees on {facingJudged - facingOff} of {facingJudged} judged, {r.Parts.Count} parts probed in {sw.Elapsed.TotalSeconds:0.0} s";
                // a wrong frame or winding moves EVERY part; a stale FBX moves a few - 5 % of the matched parts is the line between them
                if (matched == 0) { s.fail++; body.AppendLine($"FAIL {name}: no part name in common between the probe and {prevRel} ({mine.Count} vs {fbx.Count}) - the names, or the preview, are wrong"); }
                else if (centreOff + sizeOff + facingOff > Math.Max(1, matched / 20)) { s.fail++; body.AppendLine($"FAIL {name}: {counts}; e.g. {string.Join("; ", offenders)}"); }
                else { s.pass++; body.AppendLine($"PASS {name}: {counts}" + (offenders.Count > 0 ? $"; the few off: {string.Join("; ", offenders)}" : "")); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { s.fail++; body.AppendLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                if (preview != null) UnityEngine.Object.DestroyImmediate(preview);
                foreach (var a in assets) if (a != null) UnityEngine.Object.DestroyImmediate(a);
                assets.Clear();
                int meshesAfter = Resources.FindObjectsOfTypeAll<Mesh>().Length;
                if (meshesAfter != meshesBefore) { s.fail++; body.AppendLine($"FAIL {name}: the in-process preview leaked {meshesAfter - meshesBefore} Unity mesh(es)"); }
            }
        }
        body.Insert(0, $"{judged} recipes judged, {matchedTotal} parts matched against Unity's import of the Blender preview FBX; frame Blender (X, Y, Z) = Unity (X, Z, Y), faces rewound\n");
        s.body = body.ToString();
        return s;
    }

    /// <summary>Per child mesh of an instance or prefab, by object name (the first of a duplicated name): its world bounds and the sign
    /// of its facing - the sum over the faces of the area-weighted normal dotted with (face centre - bounds centre), 0 when too small to
    /// read (a flat part).</summary>
    static Dictionary<string, (Bounds bounds, int facing)> Parts(GameObject root)
    {
        var d = new Dictionary<string, (Bounds, int)>(StringComparer.Ordinal);
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = mf.sharedMesh; if (mesh == null || d.ContainsKey(mf.gameObject.name)) continue;
            var l2w = mf.transform.localToWorldMatrix;
            var v = mesh.vertices; var t = mesh.triangles;
            if (v.Length == 0) continue;
            var w = new Vector3[v.Length]; for (int i = 0; i < v.Length; i++) w[i] = l2w.MultiplyPoint3x4(v[i]);
            var b = new Bounds(w[0], Vector3.zero); foreach (var p in w) b.Encapsulate(p);
            double sum = 0, area = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 a = w[t[i]], bb = w[t[i + 1]], c = w[t[i + 2]];
                var n = Vector3.Cross(bb - a, c - a);   // twice the area, along the face normal
                sum += Vector3.Dot(n, (a + bb + c) / 3f - b.center); area += n.magnitude;
            }
            int facing = area > 0 && Math.Abs(sum) > 0.05 * area * b.extents.magnitude ? Math.Sign(sum) : 0;
            d[mf.gameObject.name] = (b, facing);
        }
        return d;
    }

    static string Fmt(Vector3 v) => $"({v.x:0.###}, {v.y:0.###}, {v.z:0.###})";
}
