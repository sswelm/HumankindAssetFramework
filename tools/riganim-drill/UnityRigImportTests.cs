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
            Check("acceptance: another motion of the same length is caught at a sampled frame", twist.Any(p => p.Contains("transform differs")) && !twist.Any(p => p.Contains("the clip is") || p.Contains("rest bounds")), twist.Count > 0 ? twist[0] : "not caught");
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
        Check("acceptance: absent pose streams fail", RigRouteAcceptanceTest.ComparePoseStreams(a, b, 1, 3, 1, 3) != null, "both roles missing");
        File.WriteAllBytes(a, new byte[0]); File.WriteAllBytes(b, new byte[0]);
        Check("acceptance: empty pose streams fail", RigRouteAcceptanceTest.ComparePoseStreams(a, b, 1, 3, 1, 3) != null, "both roles empty");
        File.WriteAllBytes(a, new byte[48]); File.WriteAllBytes(b, new byte[48]);
        Check("acceptance: valid one-frame pose streams pass", RigRouteAcceptanceTest.ComparePoseStreams(a, b, 1, 3, 1, 3) == null, "48 bytes each");
        File.WriteAllBytes(b, new byte[96]);
        Check("acceptance: different pose stream lengths at equal frames fail", RigRouteAcceptanceTest.ComparePoseStreams(a, b, 1, 3, 1, 3) != null, "48 vs 96 bytes, one frame each");
        // the SDK's ceil(length x frame rate) lands either side of a whole number: one frame apart with one frame's bytes passes, more does not
        Check("acceptance: one frame apart with one frame's bytes passes", RigRouteAcceptanceTest.ComparePoseStreams(a, b, 1, 3, 2, 3) == null, "48 vs 96 bytes, 1 vs 2 frames of 3 bones");
        Check("acceptance: one frame apart with the wrong bytes fails", RigRouteAcceptanceTest.ComparePoseStreams(b, a, 1, 3, 2, 3) != null, "96 vs 48 bytes, 1 vs 2 frames (the extra frame has fewer bytes)");
        Check("acceptance: two frames apart fail", RigRouteAcceptanceTest.ComparePoseStreams(a, b, 1, 3, 3, 3) != null, "1 vs 3 frames");
        Check("acceptance: different bone counts fail", RigRouteAcceptanceTest.ComparePoseStreams(a, b, 1, 3, 1, 4) != null, "3 vs 4 bones");
        File.WriteAllBytes(a, new byte[24]); File.WriteAllBytes(b, new byte[24]);
        Check("acceptance: packed rotation rounding passes both ways", RigRouteAcceptanceTest.ComparePoseStreams(a, b, 3, 1, 4, 1) == null && RigRouteAcceptanceTest.ComparePoseStreams(a, b, 4, 1, 3, 1) == null, "two pairs hold either three or four frames");
        File.WriteAllBytes(a, new byte[12]); File.WriteAllBytes(b, new byte[12]);
        Check("acceptance: valid short pose streams pass", RigRouteAcceptanceTest.ComparePoseStreams(a, b, 1, 1, 1, 1) == null, "one held bone needs twelve bytes");
        File.WriteAllBytes(b, new byte[13]);
        Check("acceptance: partial encoded words fail", RigRouteAcceptanceTest.ComparePoseStreams(a, b, 1, 1, 2, 1) != null, "twelve vs thirteen bytes");
        File.Delete(b);
        Check("acceptance: one missing pose stream fails", RigRouteAcceptanceTest.ComparePoseStreams(a, b, 1, 3, 1, 3) != null, "one role missing");
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
        Check("acceptance: CompareBakes preserves imported root placement and scale", problems.Any(p => p.Contains("first-frame bounds differ")) && problems.Any(p => p.Contains("the root bone")), string.Join("; ", problems.Where(p => p.Contains("rest bounds") || p.Contains("the root bone"))));
    }
    static void ToleranceCounterexamples()
    {
        var go = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/FactorySource/RigImport_plain/anim/RigImport_plain_anim.prefab");
        foreach (var scenario in new[] { (scale: false, weighted: false), (scale: true, weighted: false), (scale: false, weighted: true), (scale: true, weighted: true) })
        {
            bool scale = scenario.scale;
            var meshCopies = new List<Mesh>();
            var a = UnityEngine.Object.Instantiate(go); var b = UnityEngine.Object.Instantiate(go);
            var ca = new AnimationClip { legacy = true }; var cb = new AnimationClip { legacy = true };
            try
            {
                foreach (var inst in new[] { a, b })
                {
                    var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>(); var parent = smr.bones[0];
                    for (int i = 0; i < 3; i++) { var bone = new GameObject("Socket" + i).transform; bone.SetParent(parent, false); smr.bones = smr.bones.Concat(new[] { bone }).ToArray(); parent = bone; }
                    if (scenario.weighted)
                    {
                        var mesh = UnityEngine.Object.Instantiate(smr.sharedMesh); meshCopies.Add(mesh); smr.sharedMesh = mesh;
                        mesh.bindposes = mesh.bindposes.Concat(smr.bones.Skip(smr.bones.Length - 3).Select(t => t.worldToLocalMatrix * smr.transform.localToWorldMatrix)).ToArray();
                        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = smr.bones.Length - 1, weight0 = 1 }, mesh.vertexCount).ToArray();
                    }
                }
                foreach (var bone in a.GetComponentsInChildren<Transform>().Where(t => t.name.StartsWith("Socket", StringComparison.Ordinal)))
                {
                    string path = AnimationUtility.CalculateTransformPath(bone, a.transform);
                    foreach (var property in scale ? new[] { "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z" } : new[] { "localEulerAnglesRaw.z" })
                    {
                        float start = scale ? 1 : 0, end = scale ? 1.006f : 0.4f;
                        var binding = EditorCurveBinding.FloatCurve(path, typeof(Transform), property);
                        AnimationUtility.SetEditorCurve(ca, binding, AnimationCurve.Linear(0, start, 1, start));
                        AnimationUtility.SetEditorCurve(cb, binding, AnimationCurve.Linear(0, start, 1, end));
                    }
                }
                var differences = RigRouteAcceptanceTest.CompareRigs(a, b, ca, cb);
                Check("acceptance: " + (scale ? "0.6% local scale" : "0.4-degree local rotation") + " errors through three " + (scenario.weighted ? "weighted" : "unweighted") + " bones pass", differences.Count == 0, string.Join("; ", differences));
                ca.SampleAnimation(a, 0); cb.SampleAnimation(b, 0);
                string tip = AnimationUtility.CalculateTransformPath(a.GetComponentsInChildren<Transform>().First(t => t.name == "Socket2"), a.transform);
                var excessiveBinding = EditorCurveBinding.FloatCurve(tip, typeof(Transform), scale ? "m_LocalScale.x" : "localEulerAnglesRaw.z");
                AnimationUtility.SetEditorCurve(cb, excessiveBinding, AnimationCurve.Linear(0, scale ? 1 : 0, 1, scale ? 1.03f : 2));
                var tooFar = RigRouteAcceptanceTest.CompareRigs(a, b, ca, cb);
                Check("acceptance: excessive local " + (scale ? "scale" : "rotation") + " on " + (scenario.weighted ? "weighted" : "unweighted") + " chain fails", tooFar.Count > 0, string.Join("; ", tooFar));
            }
            finally { UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b); UnityEngine.Object.DestroyImmediate(ca); UnityEngine.Object.DestroyImmediate(cb); foreach (var mesh in meshCopies) UnityEngine.Object.DestroyImmediate(mesh); }
        }
        var sa = UnityEngine.Object.Instantiate(go); var sb = UnityEngine.Object.Instantiate(go); var held = new AnimationClip { legacy = true };
        try
        {
            foreach (var item in new[] { (inst: sa, angle: 30f), (inst: sb, angle: -30f) })
            {
                var smr = item.inst.GetComponentInChildren<SkinnedMeshRenderer>();
                var helper = new GameObject("SocketParent").transform; helper.SetParent(smr.bones[0], false);
                helper.localRotation = Quaternion.Euler(0, 0, item.angle); helper.localScale = new Vector3(2, 1, 1);
                var socket = new GameObject("ShearedSocket").transform; socket.SetParent(helper, false); socket.localRotation = Quaternion.Euler(0, 0, -item.angle);
                smr.bones = smr.bones.Concat(new[] { socket }).ToArray();
            }
            var ta = sa.GetComponentsInChildren<Transform>().First(t => t.name == "ShearedSocket"); var tb = sb.GetComponentsInChildren<Transform>().First(t => t.name == "ShearedSocket");
            Check("acceptance: shear fixture has equal world rotation and lossy scale", Quaternion.Angle(ta.rotation, tb.rotation) < 0.06f && (ta.lossyScale - tb.lossyScale).magnitude < 0.001f, "TRS hides different world matrices");
            var differences = RigRouteAcceptanceTest.CompareRigs(sa, sb, held, held);
            Check("acceptance: opposite socket shear is caught", differences.Count > 0, string.Join("; ", differences));
        }
        finally { UnityEngine.Object.DestroyImmediate(sa); UnityEngine.Object.DestroyImmediate(sb); UnityEngine.Object.DestroyImmediate(held); }
    }
    static void WrongSkinWeights()
    {
        var go = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/FactorySource/RigImport_plain/anim/RigImport_plain_anim.prefab");
        var a = UnityEngine.Object.Instantiate(go); var b = UnityEngine.Object.Instantiate(go);
        var mesh = UnityEngine.Object.Instantiate(b.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh);
        try
        {
            b.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh = mesh;
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, mesh.vertexCount).ToArray();
            var clip = AssetDatabase.LoadAllAssetsAtPath("Assets/FactorySource/RigImport_plain/anim/_RigImport_plain_Clip_primary.asset").OfType<AnimationClip>().First(AssetDatabase.IsSubAsset);
            var differences = RigRouteAcceptanceTest.CompareRigs(a, b, clip, clip);
            Check("acceptance: wrong weights with identical bone motion fail", differences.Any(p => p.Contains("skinned vertices differ")), string.Join("; ", differences));
        }
        finally { UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b); UnityEngine.Object.DestroyImmediate(mesh); }
    }
    static void NonUniformParent(float stretch)
    {
        var go = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/FactorySource/RigImport_plain/anim/RigImport_plain_anim.prefab");
        var a = UnityEngine.Object.Instantiate(go); var b = UnityEngine.Object.Instantiate(go);
        var ca = new AnimationClip { legacy = true }; var cb = new AnimationClip { legacy = true };
        try
        {
            foreach (var inst in new[] { a, b })
            {
                var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>();
                var helper = new GameObject("Stretch").transform; helper.SetParent(smr.bones[0], false); helper.localScale = new Vector3(stretch, 1, 1);
                var socket = new GameObject("StretchedSocket").transform; socket.SetParent(helper, false); smr.bones = smr.bones.Concat(new[] { socket }).ToArray();
            }
            string path = AnimationUtility.CalculateTransformPath(a.GetComponentsInChildren<Transform>().First(t => t.name == "StretchedSocket"), a.transform);
            var binding = EditorCurveBinding.FloatCurve(path, typeof(Transform), "localEulerAnglesRaw.z");
            AnimationUtility.SetEditorCurve(ca, binding, AnimationCurve.Linear(0, 0, 1, 0));
            AnimationUtility.SetEditorCurve(cb, binding, AnimationCurve.Linear(0, 0, 1, 0.4f));
            var differences = RigRouteAcceptanceTest.CompareRigs(a, b, ca, cb);
            Check("acceptance: allowed rotation under " + stretch + "x static parent passes", differences.Count == 0, string.Join("; ", differences));
            ca.SampleAnimation(a, 0); cb.SampleAnimation(b, 0);
            AnimationUtility.SetEditorCurve(cb, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalScale.x"), AnimationCurve.Linear(0, 1, 1, 1.03f));
            var tooFar = RigRouteAcceptanceTest.CompareRigs(a, b, ca, cb);
            Check("acceptance: excess bone scale under " + stretch + "x static parent fails", tooFar.Count > 0, string.Join("; ", tooFar));
        }
        finally { UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b); UnityEngine.Object.DestroyImmediate(ca); UnityEngine.Object.DestroyImmediate(cb); }
    }
    // the Vehicle Lab's rig arguments: the window's fields against the recipe reader (VehicleRigArgLine.RigArgsFromRecipe) on every
    // saved recipe the run is pointed at (-hafRecipes <dir>) - the drill's jobs come from the reader, a Generate from the window
    static void RecipeArgsParity()
    {
        string dir = Arg("-hafRecipes");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { Rows.Add("SKIP recipe args parity: no -hafRecipes directory"); return; }
        var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        int agree = 0;
        foreach (var f in files)
        {
            string why = VehicleLabWindow.RecipeArgsDisagreement(f);
            if (why == null) agree++; else Check("recipe args parity " + Path.GetFileNameWithoutExtension(f), false, why);
        }
        Check("recipe args parity", agree == files.Length && files.Length > 0, agree + " of " + files.Length + " saved recipes: the window's rig arguments equal the recipe reader's");
    }
    static void SparseRecipeParity()
    {
        var cases = new[] { "{\"srcFile\":\"C:/source.glb\",\"parts\":[]}",
            "{\"srcFile\":\"C:/source.glb\",\"parts\":[{\"name\":\"P\",\"scale\":{\"x\":2}}],\"model2ScaleXYZ\":{\"x\":2}}" };
        for (int i = 0; i < cases.Length; i++)
        {
            string path = Path.Combine(fixtures, "sparse_recipe_" + i + ".json"); File.WriteAllText(path, cases[i]);
            string why = VehicleLabWindow.RecipeArgsDisagreement(path);
            Check("sparse recipe parity " + i, why == null, why ?? "window and recipe reader agree");
        }
    }
    static void WeldCounterexamples()
    {
        void Case(string name, List<Vector3> positions, List<Vector3> normals, List<Vector2> uv, int[] triangles, int expectedVertices, int expectedDropped)
        {
            var subs = new List<int[]> { triangles };
            HafModelRig.WeldEqualVertices(positions, normals, uv, new List<byte>(), new List<BoneWeight1>(), subs, out int welded, out int dropped);
            Check("weld: " + name, positions.Count == expectedVertices && dropped == expectedDropped,
                $"{positions.Count} vertices, {welded} welded, {dropped} dropped");
        }
        Case("nearby positions stay distinct", new List<Vector3> { Vector3.zero, new Vector3(0.0000001f, 0, 0), Vector3.right }, new List<Vector3>(), new List<Vector2>(), new[] { 0, 1, 2 }, 3, 0);
        Case("distinct normals stay distinct", new List<Vector3> { Vector3.zero, Vector3.zero, Vector3.right }, new List<Vector3> { Vector3.forward, new Vector3(0, 0.00001f, 1), Vector3.forward }, new List<Vector2>(), new[] { 0, 1, 2 }, 3, 0);
        Case("distinct UVs stay distinct", new List<Vector3> { Vector3.zero, Vector3.zero, Vector3.right }, new List<Vector3>(), new List<Vector2> { Vector2.zero, new Vector2(0.0000001f, 0), Vector2.zero }, new[] { 0, 1, 2 }, 3, 0);
        Case("collapsed indices drop without a weld", new List<Vector3> { Vector3.zero, Vector3.right, Vector3.up }, new List<Vector3>(), new List<Vector2>(), new[] { 0, 0, 1 }, 3, 1);
    }

    static void WrapperCounterexamples()
    {
        Vector3[] Snapshot(HafModelRig.Result rig, float time)
        {
            rig.Clips[0].SampleAnimation(rig.Root, time);
            var points = new List<Vector3>(); var baked = new Mesh();
            try
            {
                foreach (var renderer in rig.Root.GetComponentsInChildren<SkinnedMeshRenderer>())
                { renderer.BakeMesh(baked); points.AddRange(baked.vertices.Select(renderer.transform.TransformPoint)); }
            }
            finally { UnityEngine.Object.DestroyImmediate(baked); }
            return points.ToArray();
        }
        foreach (bool shared in new[] { true, false })
        {
            var model = GlbReader.Read(Path.Combine(fixtures, "wrapped.glb"));
            if (shared)
            {
                var body = model.Nodes.First(n => n.Mesh >= 0);
                int index = model.Nodes.Count;
                model.Nodes.Add(new HafNode { Name = "BodyInstance", Mesh = body.Mesh, Skin = body.Skin, Parent = body.Parent });
                model.Nodes[body.Parent].Children.Add(index);
            }
            else model.Nodes[0].Scale = new[] { 0.0000001, 0.0000002, 0.0000003 };
            var assets = new List<UnityEngine.Object>(); var rig = HafModelRig.Build(model, assets, forFactory: true);
            try
            {
                var times = new[] { 0f, 0.5f, 1f }; var before = times.Select(t => Snapshot(rig, t)).ToArray();
                var folded = UniversalBaker.FoldScaledWrappers(model, rig);
                double worst = 0; bool equal = true;
                for (int ti = 0; ti < times.Length; ti++)
                {
                    var after = Snapshot(rig, times[ti]);
                    float tolerance = Mathf.Max(1e-10f, before[ti].Max(v => v.magnitude) * 1e-5f);
                    for (int vi = 0; vi < after.Length; vi++) { float distance = (before[ti][vi] - after[vi]).magnitude; worst = Math.Max(worst, distance); if (distance > tolerance) equal = false; }
                }
                Check(shared ? "wrapper: shared skin remains unchanged" : "wrapper: tiny nonuniform scale remains unchanged", equal && (shared ? folded.Count == 1 : folded.Count == 0), $"{folded.Count} folded, worst vertex difference {worst:R}");
            }
            finally { UnityEngine.Object.DestroyImmediate(rig.Root); foreach (var asset in assets) if (asset != null) UnityEngine.Object.DestroyImmediate(asset); }
        }
    }

    // 2026-10-27, the acceptance row's first run: the Factory's GLB import follows the FBX route's conventions - the prefab posed at
    // the primary clip's first frame (the SDK's reference pose), a uniformly scaled wrapper folded into the rig, vertices equal in
    // every attribute welded and the triangles that collapse dropped (Unity's FBX import does both)
    static void FbxRouteConventions()
    {
        var offset = Import("offset", "Spin", out var clipsOffset);
        if (offset != null)
        {
            var arm = offset.GetComponentsInChildren<Transform>(true).First(t => t.name == "Arm");
            var clip = Subclip(clipsOffset["primary"]);
            var x = AnimationUtility.GetEditorCurve(clip, AnimationUtility.GetCurveBindings(clip).First(b => b.path.EndsWith("Arm", StringComparison.Ordinal) && b.propertyName == "m_LocalPosition.x"));
            Check("the prefab sits at the primary clip's first frame", Mathf.Abs(arm.localPosition.x - x.keys[0].value) < 1e-4f && Mathf.Abs(x.keys[0].value) > 0.1f,
                FormattableString.Invariant($"Arm x {arm.localPosition.x} against the first key {x.keys[0].value} (the rest is 0)"));
        }
        var plain = Import("plain", "Spin", out var clipsPlain); var wrapped = Import("wrapped", "Spin", out var clipsWrapped);
        if (plain != null && wrapped != null)
        {
            var scaled = wrapped.GetComponentsInChildren<Transform>(true).Where(t => (t.lossyScale - Vector3.one).magnitude >= 1e-4f).ToList();
            Check("a scaled wrapper is folded: no scaled transform remains", scaled.Count == 0, scaled.Count == 0 ? "every transform at unit scale" : string.Join(",", scaled.Select(t => t.name + t.lossyScale)));
            // the SDK's skeleton counts every transform under the root as a bone (the ReconDrone: 89 against the FBX route's 88, 2026-10-28)
            int plainCount = plain.GetComponentsInChildren<Transform>(true).Length, wrappedCount = wrapped.GetComponentsInChildren<Transform>(true).Length;
            Check("the folded wrapper node is gone: the same transforms as the plain rig", plainCount == wrappedCount, $"plain {plainCount} transform(s), wrapped {wrappedCount}");
            var a = UnityEngine.Object.Instantiate(plain); var b = UnityEngine.Object.Instantiate(wrapped);
            try
            {
                var differences = RigRouteAcceptanceTest.CompareRigs(a, b, Subclip(clipsPlain["primary"]), Subclip(clipsWrapped["primary"]));
                Check("a folded wrapper equals the plain rig", differences.Count == 0, differences.Count == 0 ? "equal" : string.Join("; ", differences));
            }
            finally { UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b); }
        }
        var dupes = Import("dupes", "Spin", out _);
        if (dupes != null)
        {
            var mesh = dupes.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
            Check("equal vertices welded and the collapsed triangle dropped", mesh.vertexCount == 8 && mesh.triangles.Length / 3 == 14,
                $"{mesh.vertexCount} vertices, {mesh.triangles.Length / 3} triangles (16 vertices and 15 triangles in the file)");
        }
    }
    static void AlbedoPixelsAndOutputDirectory()
    {
        byte[] Image(Color32[] pixels, int width, int height, TextureFormat format)
        {
            var texture = new Texture2D(width, height, format, false);
            try { texture.SetPixels32(pixels); texture.Apply(); return texture.EncodeToPNG(); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
        var pixels = new[] { new Color32(1, 2, 3, 255), new Color32(4, 5, 6, 255), new Color32(7, 8, 9, 255), new Color32(10, 11, 12, 255) };
        byte[] rgba = Image(pixels, 2, 2, TextureFormat.RGBA32), rgb = Image(pixels, 2, 2, TextureFormat.RGB24);
        Check("albedo: equal pixels with different PNG encodings pass", !rgba.SequenceEqual(rgb) && BlenderRigAnimHeadlessTest.PngPixelDifference(rgba, rgb) == null, "RGB and RGBA PNGs decode equally");
        pixels[0].r++;
        Check("albedo: a changed color fails", BlenderRigAnimHeadlessTest.PngPixelDifference(rgba, Image(pixels, 2, 2, TextureFormat.RGBA32)) != null, "one red byte differs");
        pixels[0].r--; pixels[0].a--;
        Check("albedo: a changed alpha fails", BlenderRigAnimHeadlessTest.PngPixelDifference(rgba, Image(pixels, 2, 2, TextureFormat.RGBA32)) != null, "one alpha byte differs");
        Check("albedo: different dimensions fail", BlenderRigAnimHeadlessTest.PngPixelDifference(rgba, Image(pixels, 4, 1, TextureFormat.RGBA32)) != null, "same pixel count, different dimensions");
        Check("albedo: unreadable PNG fails", BlenderRigAnimHeadlessTest.PngPixelDifference(rgba, new byte[] { 1, 2, 3 }) != null, "invalid image bytes");
        string nested = Path.Combine(fixtures, "new-output", "nested", "rig.glb");
        var argv = BlenderRigAnimInProcess.Argv(0, "", "Spin", "", true, "0,0,0", true, "", false, "", true, "", false);
        Check("output: parent starts absent", !Directory.Exists(Path.GetDirectoryName(nested)), nested);
        var written = BlenderRigAnimInProcess.Run(Path.Combine(fixtures, "plain.glb"), nested, argv);
        Check("output: missing parent created", written.Written && File.Exists(nested), written.UseBlender ?? nested);
        if (written.Written)
        {
            byte[] expected = File.ReadAllBytes(nested);
            File.WriteAllText(nested, "previous output");
            var replaced = BlenderRigAnimInProcess.Run(Path.Combine(fixtures, "plain.glb"), nested, argv);
            Check("output: existing GLB atomically replaced", replaced.Written && expected.SequenceEqual(File.ReadAllBytes(nested)) && !File.Exists(nested + ".tmp"), replaced.UseBlender ?? nested);
        }
    }

    public static void Run()
    {
        fixtures = Arg("-hafRigImportFixtures"); output = Arg("-hafRigImportOutput");
        try
        {
            Motion("plain"); Motion("late"); Atlas();
            foreach (var item in new[] { (fixture: "long", action: "Spin_" + new string('a', 120)), (fixture: "underscore", action: "_Spin") })
                if (Import(item.fixture, item.action, out _) != null) Preview(item.fixture, item.action);
            Acceptance(); AcceptanceCounterexamples(); PoseStreams(); CrossRoute(); RootPlacement(); ToleranceCounterexamples(); WrongSkinWeights(); NonUniformParent(2); NonUniformParent(100);
            FbxRouteConventions(); WeldCounterexamples(); WrapperCounterexamples();
            RecipeArgsParity(); SparseRecipeParity(); AlbedoPixelsAndOutputDirectory();
        }
        catch (Exception e) { Rows.Add("FAIL exception: " + e); failed++; }
        Rows.Add("TOTAL failed " + failed); File.WriteAllLines(output, Rows);
        EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}
