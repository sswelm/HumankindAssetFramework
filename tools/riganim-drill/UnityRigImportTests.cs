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
            Check("acceptance: another motion of the same length is caught at a sampled frame", twist.Any(p => p.Contains("world transform differs")) && !twist.Any(p => p.Contains("the clip is") || p.Contains("rest bounds")), twist.Count > 0 ? twist[0] : "not caught");
        }
        finally { UnityEngine.Object.DestroyImmediate(instA); UnityEngine.Object.DestroyImmediate(instB); UnityEngine.Object.DestroyImmediate(instLate); UnityEngine.Object.DestroyImmediate(instTwist); }
    }
    // Counterexamples for the route acceptance gate: invisible sockets, motion between the original three samples,
    // and absent role pose streams. Use real AnimationClip sampling and BakeMesh in native Unity.
    static void AcceptanceCounterexamples()
    {
        var go = Import("plain", "Spin", out var clips); if (go == null) return;
        var a = UnityEngine.Object.Instantiate(go); var b = UnityEngine.Object.Instantiate(go);
        var clipA = new AnimationClip { legacy = true }; var clipB = new AnimationClip { legacy = true };
        try
        {
            foreach (var inst in new[] { a, b })
            {
                var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>();
                var socket = new GameObject("Muzzle").transform; socket.SetParent(smr.bones[0], false);
                smr.bones = smr.bones.Concat(new[] { socket }).ToArray();
            }
            string socketPath = AnimationUtility.CalculateTransformPath(a.GetComponentsInChildren<Transform>().First(t => t.name == "Muzzle"), a.transform);
            AnimationUtility.SetEditorCurve(clipA, EditorCurveBinding.FloatCurve(socketPath, typeof(Transform), "localEulerAnglesRaw.z"), AnimationCurve.Linear(0, 0, 1, 0));
            AnimationUtility.SetEditorCurve(clipB, EditorCurveBinding.FloatCurve(socketPath, typeof(Transform), "localEulerAnglesRaw.z"), AnimationCurve.Linear(0, 0, 1, 2));   // 2 degrees at the end: past the clip-time allowance
            var clipC = new AnimationClip { legacy = true };
            AnimationUtility.SetEditorCurve(clipC, EditorCurveBinding.FloatCurve(socketPath, typeof(Transform), "localEulerAnglesRaw.z"), AnimationCurve.Linear(0, 0, 1, 0.4f));   // inside Unity's 0.5-degree compression allowance
            var identicalSocket = RigRouteAcceptanceTest.CompareRigs(a, b, clipA, clipA);
            Check("acceptance: identical unweighted socket motion passes", identicalSocket.Count == 0, string.Join("; ", identicalSocket));
            // Restore both rigs' rest after sampling the equal pair.
            clipA.SampleAnimation(a, 0); clipA.SampleAnimation(b, 0);
            var socketDifference = RigRouteAcceptanceTest.CompareRigs(a, b, clipA, clipB);
            Check("acceptance: unweighted socket rotation past the allowance is caught", socketDifference.Count > 0, string.Join("; ", socketDifference));
            clipA.SampleAnimation(a, 0); clipA.SampleAnimation(b, 0);
            var socketWithin = RigRouteAcceptanceTest.CompareRigs(a, b, clipA, clipC);
            Check("acceptance: a socket rotation within Unity's compression allowance passes", socketWithin.Count == 0, string.Join("; ", socketWithin));
            UnityEngine.Object.DestroyImmediate(clipC);
            UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b);
            a = UnityEngine.Object.Instantiate(go); b = UnityEngine.Object.Instantiate(go);
            var original = Subclip(clips["primary"]);
            UnityEngine.Object.DestroyImmediate(clipA); UnityEngine.Object.DestroyImmediate(clipB);
            clipA = UnityEngine.Object.Instantiate(original); clipB = UnityEngine.Object.Instantiate(original);
            var binding = AnimationUtility.GetCurveBindings(clipB).First(x => x.propertyName == "m_LocalPosition.x" && x.path.EndsWith("Arm", StringComparison.Ordinal));
            var curve = AnimationUtility.GetEditorCurve(clipA, binding);
            AnimationUtility.SetEditorCurve(clipB, binding, new AnimationCurve(new Keyframe(0, curve.Evaluate(0)), new Keyframe(0.25f, curve.Evaluate(0.25f) + 2), new Keyframe(0.5f, curve.Evaluate(0.5f)), new Keyframe(1, curve.Evaluate(1))));
            var pulseDifference = RigRouteAcceptanceTest.CompareRigs(a, b, clipA, clipB);
            Check("acceptance: motion between first/middle/last is caught", pulseDifference.Count > 0, string.Join("; ", pulseDifference));
            UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b);
            a = UnityEngine.Object.Instantiate(go); b = UnityEngine.Object.Instantiate(go);
            AnimationUtility.SetEditorCurve(clipB, binding, new AnimationCurve(new Keyframe(0, curve.Evaluate(0)), new Keyframe(0.01f, curve.Evaluate(0.01f) + 2), new Keyframe(0.02f, curve.Evaluate(0.02f)), new Keyframe(0.5f, curve.Evaluate(0.5f)), new Keyframe(1, curve.Evaluate(1))));
            var subframeDifference = RigRouteAcceptanceTest.CompareRigs(a, b, clipA, clipB);
            Check("acceptance: a keyed excursion between bake frames is caught", subframeDifference.Any(x => x.Contains(" at 0.01 s")), string.Join("; ", subframeDifference));
        }
        finally { if (a != null) UnityEngine.Object.DestroyImmediate(a); if (b != null) UnityEngine.Object.DestroyImmediate(b); UnityEngine.Object.DestroyImmediate(clipA); UnityEngine.Object.DestroyImmediate(clipB); }
    }
    static void PoseStreams()
    {
        string a = Path.Combine(fixtures, "pose_a.bytes"), b = Path.Combine(fixtures, "pose_b.bytes");
        Check("acceptance: absent pose streams fail", RigRouteAcceptanceTest.ComparePoseStreams(a, b) != null, "both roles missing");
        File.WriteAllBytes(a, new byte[0]); File.WriteAllBytes(b, new byte[0]);
        Check("acceptance: empty pose streams fail", RigRouteAcceptanceTest.ComparePoseStreams(a, b) != null, "both roles empty");
        File.WriteAllBytes(a, new byte[48]); File.WriteAllBytes(b, new byte[48]);
        Check("acceptance: valid one-frame pose streams pass", RigRouteAcceptanceTest.ComparePoseStreams(a, b) == null, "48 bytes each");
        File.WriteAllBytes(b, new byte[96]);
        Check("acceptance: different pose stream lengths fail", RigRouteAcceptanceTest.ComparePoseStreams(a, b) != null, "48 vs 96 bytes");
        File.Delete(b);
        Check("acceptance: one missing pose stream fails", RigRouteAcceptanceTest.ComparePoseStreams(a, b) != null, "one role missing");
    }
    static void CrossRoute()
    {
        var glb = Import("route", "Spin", out var clips); if (glb == null) return;
        string path = "Assets/FactorySource/RigImport_fbx/anim/RigImport_fbx_anim.fbx";
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.Copy(Path.Combine(fixtures, "route.fbx"), path, true);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var imp = (ModelImporter)AssetImporter.GetAtPath(path);
        imp.animationType = ModelImporterAnimationType.Generic; imp.importAnimation = true; imp.useFileScale = false; imp.globalScale = 1; imp.SaveAndReimport();
        var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        float longest = (float)Baker("MeasureLongestAxis").Invoke(null, new object[] { fbx });
        imp.globalScale = 10f / longest; imp.SaveAndReimport(); fbx = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        void Compare(bool expectEqual, string label)
        {
            var fbxClip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview", StringComparison.Ordinal));
            var a = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path)); var b = UnityEngine.Object.Instantiate(glb);
            try
            {
                var differences = RigRouteAcceptanceTest.CompareRigs(a, b, fbxClip, Subclip(clips["primary"]));
                Check(label, expectEqual ? differences.Count == 0 : differences.Count > 0, string.Join("; ", differences));
            }
            finally { UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b); }
        }
        // the Factory keeps the FBX importer's default compression (keys dropped within 0.5 degrees / 0.5%): the row's clip-time
        // tolerance covers that allowance, so the default import must agree, and so must the uncompressed one
        Compare(true, "acceptance: the real FBX pair with Unity's default compression agrees within the clip-time allowance");
        imp.animationCompression = ModelImporterAnimationCompression.Off; imp.SaveAndReimport();
        Compare(true, "acceptance: real uncompressed FBX and GLB slim imports agree");
    }

    static void RootPlacement()
    {
        File.Copy(Path.Combine(fixtures, "plain.glb"), Path.Combine(fixtures, "placed.glb"), true);
        if (Import("placed", "Spin", out _) == null) return;
        string path = "Assets/FactorySource/RigImport_placed/anim/RigImport_placed_anim.prefab";
        var root = PrefabUtility.LoadPrefabContents(path);
        try { root.transform.position += new Vector3(0, 3, 0); root.transform.localScale *= 1.02f; PrefabUtility.SaveAsPrefabAsset(root, path); }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        var problems = new List<string>(); var notes = new List<string>();
        typeof(RigRouteAcceptanceTest).GetMethod("CompareBakes", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { "RigImport_plain", "RigImport_placed", problems, notes });
        // This lightweight project lacks the Amplitude SDK: disregard missing baked collections and assert the actual
        // production comparison reports the root-transform discrepancy, rather than resetting both roots to identity.
        Check("acceptance: CompareBakes preserves imported root placement and scale", problems.Any(p => p.Contains("rest bounds differ")) && problems.Any(p => p.Contains("the root bone")), string.Join("; ", problems.Where(p => p.Contains("rest bounds") || p.Contains("the root bone"))));
    }
    public static void Run()
    {
        fixtures = Arg("-hafRigImportFixtures"); output = Arg("-hafRigImportOutput");
        try
        {
            Motion("plain"); Motion("late"); Atlas();
            foreach (var item in new[] { (fixture: "long", action: "Spin_" + new string('a', 120)), (fixture: "underscore", action: "_Spin") })
                if (Import(item.fixture, item.action, out _) != null) Preview(item.fixture, item.action);
            Acceptance(); AcceptanceCounterexamples(); PoseStreams(); CrossRoute(); RootPlacement();
        }
        catch (Exception e) { Rows.Add("FAIL exception: " + e); failed++; }
        Rows.Add("TOTAL failed " + failed); File.WriteAllLines(output, Rows);
        EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}
