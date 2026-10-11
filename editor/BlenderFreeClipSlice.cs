// An opt-in vertical slice for a pre-rigged, animated glTF. It persists the Unity rig and
// AnimationClip built by HafModelRig, then asks the game's SDK to bake a Skeleton and
// ClipCollection from them. The normal Factory route remains unchanged while this contract
// is measured against its Blender/FBX route.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class BlenderFreeClipSlice
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Unity batch invocation: -executeMethod BlenderFreeClipSlice.RunBatch -hafSliceInput <absolute .glb>
    public static void RunBatch()
    {
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-hafSliceInput");
        if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("-hafSliceInput <absolute .glb> is required");
        int reportAt = Array.IndexOf(args, "-hafSliceReport");
        Run(args[at + 1], reportAt >= 0 && reportAt + 1 < args.Length ? args[reportAt + 1] : null);
    }

    public static void Run(string source, string reportPath = null)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Animated GLB", source);
        var watch = Stopwatch.StartNew();
        string tag = "__haf_csharp_clip_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        string dir = "Assets/FactorySource/" + tag;
        string resources = "Assets/Resources/";
        Directory.CreateDirectory(Path.Combine(Application.dataPath, "FactorySource", tag));
        Directory.CreateDirectory(Path.Combine(Application.dataPath, "Resources"));
        var temporary = new List<UnityEngine.Object>();
        bool succeeded = false;
        try
        {
            var m = GlbReader.Read(source);
            var rig = HafModelRig.Build(m, temporary);
            if (rig.SkinnedMeshes == 0 || rig.Clips.Length == 0)
                throw new InvalidDataException("The slice needs a skinned mesh and an animation");
            // Narrow game-facing contract: one skin, only skinned geometry, one body mesh.
            // The runtime retargets a single _ModelMesh onto the donor's body fragment.
            if (m.Skins.Count != 1 || rig.Root.GetComponentsInChildren<MeshRenderer>(true).Length != 0)
                throw new NotSupportedException("The first C# slice supports one skin and no rigid mesh renderers");
            var body = MergeBody(rig.Root, tag, temporary);
            Bounds bounds = body.bounds;
            float longest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            if (longest <= 1e-4f) throw new InvalidDataException("The skinned body's bounds are empty");
            float scale = 5f / longest;
            rig.Root.transform.localScale = Vector3.one * scale;
            rig.Root.transform.position = new Vector3(-bounds.center.x * scale, -bounds.min.y * scale, -bounds.center.z * scale);
            // A solid atlas is intentional for this first geometry/animation runtime
            // slice. Per-material colour/UV parity is a separate prerequisite for
            // replacing Blender on production assets.
            var atlas = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = tag + "_Atlas" };
            for (int i = 0; i < 16; i++) atlas.SetPixel(i % 4, i / 4, Color.white);
            atlas.Apply();
            string atlasPath = resources + tag + "_Atlas.asset";
            AssetDatabase.CreateAsset(atlas, atlasPath);
            var whiteMat = new Material(Shader.Find("Standard")) { name = tag + "_Mat", mainTexture = atlas };
            temporary.Add(whiteMat);
            body.sharedMaterials = new[] { whiteMat };
            long readRigMs = watch.ElapsedMilliseconds;

            // The preview rig normally lives only in memory. Persist its dependencies before
            // saving the prefab, so SDK Reimport can instantiate it after a domain reload.
            int n = 0;
            Persist(whiteMat, dir, ref n);
            // The runtime's body retargeter searches for the _ModelMesh suffix.
            // AssetDatabase names a main asset after its file, so keep it here.
            body.sharedMesh.hideFlags = HideFlags.None;
            AssetDatabase.CreateAsset(body.sharedMesh, dir + "/" + tag + "_ModelMesh.asset");
            foreach (var t in rig.Root.GetComponentsInChildren<Transform>(true))
                t.gameObject.hideFlags = HideFlags.None;
            if (rig.Root.GetComponent<Animator>() == null) rig.Root.AddComponent<Animator>();
            var clip = rig.Clips[0];
            // This first slice uses the Factory's rotation-only contract. Blender's
            // rig_anim removes bone LOCATION curves unless keepTranslations is set.
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                if (binding.propertyName.StartsWith("m_LocalPosition", StringComparison.Ordinal))
                    AnimationUtility.SetEditorCurve(clip, binding, null);
            clip.legacy = false;   // ClipEntry.Reimport plays it through an AnimatorController
            clip.hideFlags = HideFlags.None;
            string clipPath = dir + "/" + tag + "_ClipSource.asset";
            // The SDK scans subassets of this GUID and ignores names beginning '_'.
            // A native AnimationClip container also survives runtime without an
            // editor-only ScriptableObject type in the game's assemblies.
            var clipSource = new AnimationClip { name = "_" + tag };
            AssetDatabase.CreateAsset(clipSource, clipPath);
            AssetDatabase.AddObjectToAsset(clip, clipSource);
            string prefabPath = dir + "/" + tag + ".prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(rig.Root, prefabPath);
            if (prefab == null) throw new InvalidOperationException("Unity did not save the rig prefab");
            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                int maxBone = mesh.boneWeights.SelectMany(w => new[] { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 }).Max();
                UnityEngine.Debug.Log($"[HAF C# clip slice] mesh={mesh.name} vertices={mesh.vertexCount} normals={mesh.normals.Length} tangents={mesh.tangents.Length} bones={smr.bones.Length} bindposes={mesh.bindposes.Length} maxBone={maxBone}");
                if (mesh.normals.Length != mesh.vertexCount || mesh.tangents.Length != mesh.vertexCount || maxBone >= smr.bones.Length)
                    throw new InvalidDataException("The persisted skinned mesh cannot be imported by the game SDK");
            }
            AssetDatabase.SaveAssets();
            long prefabMs = watch.ElapsedMilliseconds;

            Type skelType = FindType("Amplitude.Mercury.Animation.Skeleton");
            Type clipType = FindType("Amplitude.Mercury.Animation.ClipCollection");
            Type adbType = FindType("Amplitude.Framework.Asset.AssetDatabase");
            if (skelType == null || clipType == null || adbType == null)
                throw new InvalidOperationException("The game's Skeleton, ClipCollection or AssetDatabase is unavailable");
            var skel = ScriptableObject.CreateInstance(skelType);
            string skelPath = resources + tag + "_Skeleton.asset";
            AssetDatabase.CreateAsset(skel, skelPath);
            Call(skel, "SetPrefab", new[] { typeof(GameObject) }, prefab);
            Call(skel, "Reimport", Type.EmptyTypes);
            var infoType = skelType;
            FieldInfo infos = null;
            while (infoType != null && infos == null)
            {
                infos = infoType.GetField("skinnedMeshInfos", Instance);
                infoType = infoType.BaseType;
            }
            var bakedMeshes = infos?.GetValue(skel) as Array;
            if (bakedMeshes == null || bakedMeshes.Length != 1)
                throw new InvalidDataException("The game skeleton must contain exactly one body mesh");
            string bodyName = bakedMeshes.GetValue(0).GetType().GetField("MeshName", Instance)?.GetValue(bakedMeshes.GetValue(0)) as string;
            if (bodyName == null || !bodyName.EndsWith("_ModelMesh", StringComparison.Ordinal))
                throw new InvalidDataException("The baked body mesh lost its _ModelMesh runtime name");
            EditorUtility.SetDirty(skel);
            AssetDatabase.SaveAssets();
            long skeletonMs = watch.ElapsedMilliseconds;

            var getGuid = adbType.GetMethod("GetAssetGUID", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(UnityEngine.Object) }, null);
            if (getGuid == null) throw new MissingMethodException("Amplitude AssetDatabase.GetAssetGUID(Object)");
            object skelGuid = getGuid.Invoke(null, new object[] { skel });
            object clipGuid = getGuid.Invoke(null, new object[] { clipSource });
            var cc = ScriptableObject.CreateInstance(clipType);
            string ccPath = resources + tag + "_Clips.asset";
            AssetDatabase.CreateAsset(cc, ccPath);
            var skelField = clipType.GetField("skeleton", Instance);
            var entriesField = clipType.GetField("animationClipEntries", Instance);
            if (skelField == null || entriesField == null)
                throw new MissingFieldException("ClipCollection's skeleton or animationClipEntries field");
            skelField.SetValue(cc, skelGuid);
            Type entryType = entriesField.FieldType.GetElementType();
            var entries = Array.CreateInstance(entryType, 1);
            object entry = Activator.CreateInstance(entryType);
            entryType.GetField("UnityAnimationClip", Instance).SetValue(entry, clipGuid);
            entries.SetValue(entry, 0);
            entriesField.SetValue(cc, entries);
            // SetFromDirectory only scans *.fbx; Reimport itself reads the referenced clip.
            Call(cc, "Reimport", Type.EmptyTypes);
            EditorUtility.SetDirty(cc);
            AssetDatabase.SaveAssets();
            long clipMs = watch.ElapsedMilliseconds;

            var baked = (Array)entriesField.GetValue(cc);
            if (baked == null || baked.Length != 1) throw new InvalidDataException("SDK did not retain one clip entry");
            object one = baked.GetValue(0);
            int frames = (int)entryType.GetField("FrameCount", Instance).GetValue(one);
            int bones = (int)entryType.GetField("BonesCount", Instance).GetValue(one);
            float duration = (float)entryType.GetField("Duration", Instance).GetValue(one);
            if (frames <= 0 || bones <= 0 || duration <= 0f)
                throw new InvalidDataException($"SDK gave an empty clip: {frames} frames, {bones} bones, {duration}s");
            if (!Convert.ToBoolean(Call(cc, "Load", Type.EmptyTypes)))
                throw new InvalidDataException("The game's ClipCollection.Load rejected the baked pose data");
            object collGuid = getGuid.Invoke(null, new object[] { cc });
            object atlasGuid = getGuid.Invoke(null, new object[] { atlas });
            string report = $"[HAF C# clip slice] PASS source={source} name={clip.name} nodes={m.Nodes.Count} meshes={rig.Meshes} skinned={rig.SkinnedMeshes} " +
                $"frames={frames} bones={bones} duration={duration:R} read+rig={readRigMs}ms prefab={prefabMs}ms skeleton={skeletonMs}ms clip={clipMs}ms " +
                $"prefab={prefabPath} skeleton={skelPath} collection={ccPath} atlas={atlasPath} skeletonGuid={GuidCsv(skelGuid)} collectionGuid={GuidCsv(collGuid)} atlasGuid={GuidCsv(atlasGuid)}";
            UnityEngine.Debug.Log(report);
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, report);
            succeeded = true;
        }
        finally
        {
            foreach (var asset in temporary)
                if (asset != null && !AssetDatabase.Contains(asset)) UnityEngine.Object.DestroyImmediate(asset);
            if (!succeeded)
            {
                AssetDatabase.DeleteAsset(resources + tag + "_Clips.asset");
                AssetDatabase.DeleteAsset(resources + tag + "_ClipsPoseData.bytes");
                AssetDatabase.DeleteAsset(resources + tag + "_Skeleton.asset");
                AssetDatabase.DeleteAsset(resources + tag + "_Atlas.asset");
                AssetDatabase.DeleteAsset(dir);
            }
        }
    }

    static SkinnedMeshRenderer MergeBody(GameObject root, string tag, List<UnityEngine.Object> temporary)
    {
        var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (renderers.Length == 0) throw new InvalidDataException("No skinned body");
        var first = renderers[0];
        var bones = first.bones;
        int bindCount = first.sharedMesh.bindposes.Length;
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uvs = new List<Vector2>();
        var weights = new List<BoneWeight>();
        var triangles = new List<int>();
        foreach (var smr in renderers)
        {
            var mesh = smr.sharedMesh;
            if (smr.transform.parent != root.transform || smr.transform.localPosition != Vector3.zero ||
                smr.transform.localRotation != Quaternion.identity || smr.transform.localScale != Vector3.one ||
                !smr.bones.SequenceEqual(bones) || mesh.bindposes.Length != bindCount)
                throw new NotSupportedException("The first C# slice needs skinned mesh hosts sharing one root and bone palette");
            int offset = vertices.Count;
            vertices.AddRange(mesh.vertices); normals.AddRange(mesh.normals);
            tangents.AddRange(mesh.tangents); uvs.AddRange(mesh.uv);
            weights.AddRange(mesh.boneWeights);
            triangles.AddRange(mesh.triangles.Select(i => i + offset));
        }
        var merged = new Mesh { name = tag + "_ModelMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        merged.SetVertices(vertices); merged.SetNormals(normals); merged.SetTangents(tangents); merged.SetUVs(0, uvs);
        merged.SetTriangles(triangles, 0);
        merged.bindposes = first.sharedMesh.bindposes;
        merged.boneWeights = weights.ToArray();
        merged.RecalculateBounds();
        temporary.Add(merged);
        first.sharedMesh = merged;
        for (int i = 1; i < renderers.Length; i++) UnityEngine.Object.DestroyImmediate(renderers[i].gameObject);
        return first;
    }

    static string GuidCsv(object guid)
    {
        var type = guid.GetType();
        return string.Join(",", new[] { "a", "b", "c", "d" }.Select(n => type.GetField(n, Instance).GetValue(guid).ToString()));
    }

    static void Persist(UnityEngine.Object asset, string dir, ref int n)
    {
        asset.hideFlags = HideFlags.None;
        AssetDatabase.CreateAsset(asset, dir + "/part_" + (++n) + ".asset");
    }

    static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

    static object Call(object target, string method, Type[] types, params object[] args)
    {
        var mi = target.GetType().GetMethod(method, Instance, null, types, null);
        if (mi == null) throw new MissingMethodException(target.GetType().FullName, method);
        try { return mi.Invoke(target, args); }
        catch (TargetInvocationException e) { throw new InvalidOperationException(method + " failed", e.InnerException ?? e); }
    }
}
