// Live Unity regressions for the Factory's GLB rig import. Run tools/riganim_unity_import_drill.sh;
// these exercise actual persistence, curve bindings, previews and atlas remapping, not just a compiled API.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class UnityRigImportTests
{
    static readonly List<string> Rows = new List<string>(); static int failed;
    static string fixtures, output;
    static string Arg(string key) { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    static void Check(string name, bool ok, string detail) { Rows.Add((ok ? "PASS " : "FAIL ") + name + ": " + detail); if (!ok) failed++; }
    static MethodInfo Baker(string name) => typeof(UniversalBaker).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
    static AnimationClip Subclip(UnityEngine.Object container) => AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(container)).OfType<AnimationClip>().First(c => AssetDatabase.IsSubAsset(c));
    static GameObject Import(string fixture, string action, out Dictionary<string, UnityEngine.Object> clips)
    {
        string name = "RigImport_" + fixture, dir = "Assets/FactorySource/" + name + "/anim";
        Directory.CreateDirectory(dir); AssetDatabase.Refresh();
        object[] args = { name, dir, Path.Combine(fixtures, fixture + ".glb"), 10f, action,
            new List<(string role, string action)> { ("move", action) }, null, null, null };
        var go = (GameObject)Baker("ImportGlbRig").Invoke(null, args);
        clips = args[6] as Dictionary<string, UnityEngine.Object>;
        Check(fixture + " import", go != null, go != null ? "prefab written" : (string)args[8]);
        return go;
    }
    static void Preview(string fixture, string action)
    {
        foreach (string role in new[] { "anim", "anim_move" })
        {
            object[] args = { "RigImport_" + fixture, role, null };
            string path = (string)Baker("RigPreviewPath").Invoke(null, args);
            var clip = args[2] as AnimationClip;
            Check(fixture + " " + role + " preview", path != null && clip != null && clip.name == (action.StartsWith("_", StringComparison.Ordinal) ? "HAF" + action : action) && AnimationUtility.GetCurveBindings(clip).Length > 0,
                clip != null ? clip.name + " / " + AnimationUtility.GetCurveBindings(clip).Length + " bindings" : "no clip");
            if (clip != null) Check(fixture + " " + role + " playable name", !clip.name.StartsWith("_", StringComparison.Ordinal), "the SDK will not ignore the action");
        }
    }
    static void Motion(string fixture)
    {
        var go = Import(fixture, "Spin", out var clips); if (go == null) return;
        var model = GlbReader.Read(Path.Combine(fixtures, fixture + ".glb")); var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
        var expectedNames = model.Skins[0].Joints.Select(j => model.Nodes[j].Name).ToArray();
        Check(fixture + " bones", smr.bones.Select(t => t.name).SequenceEqual(expectedNames), string.Join(",", smr.bones.Select(t => t.name)));
        var clip = Subclip(clips["primary"]);
        Check(fixture + " clip containers", AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(clips["primary"])).OfType<AnimationClip>().Count(c => !c.name.StartsWith("_", StringComparison.Ordinal)) == 1,
            "exactly the playable subasset is visible to the SDK");
        var xb = AnimationUtility.GetCurveBindings(clip).First(b => b.path.Split('/').Last().StartsWith("Arm", StringComparison.Ordinal) && b.propertyName == "m_LocalPosition.x");
        var curve = AnimationUtility.GetEditorCurve(clip, xb);
        var temp = new List<UnityEngine.Object>(); var raw = HafModelRig.Build(model, temp);
        float longest = (float)Baker("MeasureLongestAxis").Invoke(null, new object[] { raw.Root }); float wanted = -2f * 10f / longest;
        Check(fixture + " scaled translation", Mathf.Abs(curve.keys.Last().value - wanted) < 0.001f, curve.keys.Last().value + " expected " + wanted);
        Check(fixture + " rebased time", Mathf.Abs(curve.keys.First().time) < 0.0001f && Mathf.Abs(clip.length - (fixture == "late" ? 0.5f : 1f)) < 0.001f, clip.length + " seconds");
        Check(fixture + " picker names unchanged", raw.Nodes.Any(t => t.name.Contains(" #")), "preview builder still assigns unique indexed paths");
        UnityEngine.Object.DestroyImmediate(raw.Root); foreach (var a in temp) if (a != null) UnityEngine.Object.DestroyImmediate(a);
        Check(fixture + " role copies", Subclip(clips["primary"]) != Subclip(clips["move"]), "shared action has an independent subasset per role");
        Preview(fixture, "Spin");
    }
    static void Atlas()
    {
        var go = Import("materials", "Spin", out _); if (go == null) return;
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
        foreach (var mat in smr.sharedMaterials) AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(mat), ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        Check("material persistence", smr.sharedMaterials.Select(m => m.name).SequenceEqual(new[] { "Blue", "Red" }), string.Join(",", smr.sharedMaterials.Select(m => m.name)));
        var inst = UnityEngine.Object.Instantiate(go);
        var before = inst.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
        var firstVertex = Enumerable.Range(0, 2).Select(i => before.GetTriangles(i)[0]).ToArray();
        Texture2D Swatch(Color c) { var t = new Texture2D(4, 4); t.SetPixels(Enumerable.Repeat(c, 16).ToArray()); t.Apply(); return t; }
        var ordered = new List<KeyValuePair<string, Texture2D>> { new KeyValuePair<string, Texture2D>("mat0_Red", Swatch(Color.red)), new KeyValuePair<string, Texture2D>("mat1_Blue", Swatch(Color.blue)) };
        object[] args = { inst, ordered, "RigImport_materials", new BakeConfig { albedoBrightness = 1f, albedoSaturation = 1f, atlasMaxDim = 64 }, null };
        var atlas = (Texture2D)Baker("BuildMultiAtlasAndRemap").Invoke(null, args);
        var mesh = inst.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
        for (int i = 0; i < 2; i++) { var uv = mesh.uv[firstVertex[i]]; var color = atlas.GetPixelBilinear(uv.x, uv.y); Check("atlas submesh " + i, i == 0 ? color.b > color.r : color.r > color.b, color.ToString()); }
        UnityEngine.Object.DestroyImmediate(inst); foreach (var meshCopy in (List<Mesh>)args[4]) if (meshCopy != null && !AssetDatabase.Contains(meshCopy)) UnityEngine.Object.DestroyImmediate(meshCopy);
        UnityEngine.Object.DestroyImmediate(atlas);
    }
    // step (h): the acceptance row's comparison on two imports of one fixture (equal), a scaled rig and a clip of another length (not)
    static void Acceptance()
    {
        File.Copy(Path.Combine(fixtures, "plain.glb"), Path.Combine(fixtures, "plain_b.glb"), true);
        var goA = Import("plain", "Spin", out var clipsA); var goB = Import("plain_b", "Spin", out var clipsB); var goLate = Import("late", "Spin", out var clipsLate); var goTwist = Import("twist", "Spin", out var clipsTwist);
        if (goA == null || goB == null || goLate == null || goTwist == null) return;
        var instA = UnityEngine.Object.Instantiate(goA); var instB = UnityEngine.Object.Instantiate(goB); var instLate = UnityEngine.Object.Instantiate(goLate); var instTwist = UnityEngine.Object.Instantiate(goTwist);
        try
        {
            var same = RigRouteAcceptanceTest.CompareRigs(instA, instB, Subclip(clipsA["primary"]), Subclip(clipsB["primary"]));
            Check("acceptance: two imports of one rig are equal", same.Count == 0, same.Count == 0 ? "no difference" : string.Join("; ", same));
            instB.transform.localScale = Vector3.one * 1.02f;
            var scaled = RigRouteAcceptanceTest.CompareRigs(instA, instB, Subclip(clipsA["primary"]), Subclip(clipsB["primary"]));
            Check("acceptance: a rig 2% larger is caught", scaled.Count > 0, scaled.Count > 0 ? scaled[0] : "not caught");
            instB.transform.localScale = Vector3.one;
            var other = RigRouteAcceptanceTest.CompareRigs(instA, instLate, Subclip(clipsA["primary"]), Subclip(clipsLate["primary"]));
            Check("acceptance: a clip of another length is caught", other.Any(p => p.Contains("the clip is")), other.Count > 0 ? other[0] : "not caught");
            // the same rest, the same length, another motion: only the sampled frames can tell them apart
            // a fresh instance: the checks above left instA posed at its clip's end, and the row judges the REST of a fresh instance
            var instA2 = UnityEngine.Object.Instantiate(goA);
            var twist = RigRouteAcceptanceTest.CompareRigs(instA2, instTwist, Subclip(clipsA["primary"]), Subclip(clipsTwist["primary"]));
            UnityEngine.Object.DestroyImmediate(instA2);
            Check("acceptance: another motion of the same length is caught at a sampled frame", twist.Any(p => p.Contains(" at 0.5 s")) && !twist.Any(p => p.Contains("the clip is") || p.Contains("rest bounds")), twist.Count > 0 ? twist[0] : "not caught");
        }
        finally { UnityEngine.Object.DestroyImmediate(instA); UnityEngine.Object.DestroyImmediate(instB); UnityEngine.Object.DestroyImmediate(instLate); UnityEngine.Object.DestroyImmediate(instTwist); }
    }
    public static void Run()
    {
        fixtures = Arg("-hafRigImportFixtures"); output = Arg("-hafRigImportOutput");
        try
        {
            Motion("plain"); Motion("late"); Atlas();
            foreach (var item in new[] { (fixture: "long", action: "Spin_" + new string('a', 120)), (fixture: "underscore", action: "_Spin") })
                if (Import(item.fixture, item.action, out _) != null) Preview(item.fixture, item.action);
            Acceptance();
        }
        catch (Exception e) { Rows.Add("FAIL exception: " + e); failed++; }
        Rows.Add("TOTAL failed " + failed); File.WriteAllLines(output, Rows);
        EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}
