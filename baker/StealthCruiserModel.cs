// StealthCruiserModel.cs (ENC editor) — bake the USS Zumwalt OBJ into an Amplitude skeleton for the Era-6
// StealthCruisers unit. Unlike the hovercraft (procedural skin), this model ships its OWN texture + UVs, so we keep
// the OBJ's UVs and bake the extracted albedo into an atlas asset the plugin applies as _MainTex.
//
// MODEL: "USS Zumwalt (DDG-1000)" by Yakudami — Sketchfab, CC-BY. GLB -> OBJ (grid 1024, ~754 verts) + albedo PNG.
//
// RUN: Tools > StealthCruiser > Build StealthCruiser Skeleton   (then BUILD the mod)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class StealthCruiserModel
{
    const string ObjPath      = "Assets/Resources/StealthCruiser/StealthCruiser.obj";
    const string AlbedoFile   = "Resources/StealthCruiser/StealthCruiser_Material_albedo.png"; // under Application.dataPath
    const string AtlasPath    = "Assets/Resources/StealthCruiser_Atlas.asset";
    const string MeshPath     = "Assets/Resources/StealthCruiser_ModelMesh.asset";
    const string MatPath      = "Assets/Resources/StealthCruiser_Mat.mat";
    const string PrefabPath   = "Assets/Resources/StealthCruiser_Model.prefab";
    const string SkeletonPath = "Assets/Resources/StealthCruiser_Skeleton.asset";
    const float  TargetLength = 5.0f;                               // long-axis size (tune to fit the tile)
    static readonly Vector3 OrientEuler = new Vector3(180f, 0f, 0f); // deck-up + bow-FORWARD (180,0,0 reverses heading vs 0,180,0 while keeping the deck up)
    const bool   FlatShading = false;                              // false = keep the MODEL'S OWN normals (faithful as-is; the artist's smoothing groups give the angular edges); true = force-facet everything
    const float  SmoothingAngle = 20f;                             // the model ships smooth normals, so we RE-CALCULATE: any crease sharper than this becomes a HARD edge (lower = more angular). Tune to taste.

    // Called by the StealthCruiserBuilder dialog. posOffset.z is the waterline (− sinks the hull); sizeMult scales it.
    public static void Build(Vector3 posOffset, float sizeMult)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");

        // --- 1) load OBJ, combine submeshes into one mesh, KEEP UVs (the model is textured) ---
        // the model ships SMOOTH normals, so importing them as-is looks rounded. Re-CALCULATE normals with a low
        // smoothing angle so every sharp crease (deck edge, chine, superstructure facets) becomes a hard edge.
        if (AssetImporter.GetAtPath(ObjPath) is ModelImporter imp &&
            (imp.importNormals != ModelImporterNormals.Calculate || Mathf.Abs(imp.normalSmoothingAngle - SmoothingAngle) > 0.5f))
        { imp.importNormals = ModelImporterNormals.Calculate; imp.normalSmoothingAngle = SmoothingAngle; imp.SaveAndReimport(); }

        var src = AssetDatabase.LoadAssetAtPath<GameObject>(ObjPath);
        if (src == null) { Debug.LogError("[Cruiser] OBJ not found at " + ObjPath); return; }
        var inst = (GameObject)UnityEngine.Object.Instantiate(src);
        var parts = new List<CombineInstance>();
        var rootInv = inst.transform.worldToLocalMatrix;
        foreach (var mf in inst.GetComponentsInChildren<MeshFilter>())
        {
            var m = mf.sharedMesh; if (m == null) continue;
            var local = rootInv * mf.transform.localToWorldMatrix;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var sub = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = m.vertices };
                if (m.uv != null && m.uv.Length == m.vertexCount) sub.uv = m.uv;            // keep the model's UVs
                if (m.normals != null && m.normals.Length == m.vertexCount) sub.normals = m.normals;
                sub.triangles = m.GetTriangles(s);
                parts.Add(new CombineInstance { mesh = sub, transform = local });
            }
        }
        UnityEngine.Object.DestroyImmediate(inst);
        var mesh = new Mesh { name = "StealthCruiser_ModelMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.CombineMeshes(parts.ToArray(), true, true);

        // --- normalize: recenter, scale longest axis to TargetLength, align longest -> Y (+ OrientEuler) ---
        mesh.RecalculateBounds();
        var bb = mesh.bounds; var size = bb.size;
        float longest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        float scl = (longest > 0f ? TargetLength / longest : 1f) * Mathf.Max(0.01f, sizeMult);
        Quaternion align = (size.x >= size.y && size.x >= size.z) ? Quaternion.FromToRotation(Vector3.right, Vector3.up)
                         : (size.z >= size.x && size.z >= size.y) ? Quaternion.FromToRotation(Vector3.forward, Vector3.up)
                         : Quaternion.identity;
        Quaternion rot = Quaternion.Euler(OrientEuler) * align;
        var vv = mesh.vertices; var nrm = mesh.normals;
        for (int i = 0; i < vv.Length; i++) vv[i] = rot * ((vv[i] - bb.center) * scl);
        mesh.vertices = vv;
        if (nrm != null && nrm.Length == vv.Length) { for (int i = 0; i < nrm.Length; i++) nrm[i] = rot * nrm[i]; mesh.normals = nrm; } // rotate the model's own normals with it

        // --- position: settle the keel to the water plane (z=0), then apply the configured offset ---
        //     posOffset.z is the waterline tweak (− sinks the hull so the red band goes under); x/y nudge horizontally.
        mesh.RecalculateBounds();
        float raise = -mesh.bounds.min.z;   // keel -> water plane
        var vr = mesh.vertices;
        for (int i = 0; i < vr.Length; i++)
            vr[i] = new Vector3(vr[i].x + posOffset.x, vr[i].y + posOffset.y, vr[i].z + raise + posOffset.z);
        mesh.vertices = vr;
        Debug.Log($"[Cruiser] baked: verts={mesh.vertexCount}, size×{sizeMult}, offset={posOffset}");

        // --- 2) texture: load the extracted albedo, force opaque, bake an atlas asset ---
        var pngPath = Path.Combine(Application.dataPath, AlbedoFile);
        var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "StealthCruiser_Atlas" };
        if (File.Exists(pngPath))
        {
            atlas.LoadImage(File.ReadAllBytes(pngPath));
            var px = atlas.GetPixels();
            for (int i = 0; i < px.Length; i++) px[i].a = 1f;       // opaque hull
            atlas.SetPixels(px); atlas.Apply();
            Debug.Log($"[Cruiser] albedo baked: {atlas.width}x{atlas.height}");
        }
        else Debug.LogWarning("[Cruiser] albedo PNG not found: " + pngPath);
        AssetDatabase.CreateAsset(atlas, AtlasPath);

        // --- 3) rig: root -> Dummy_Root -> Base, skinned 100% to Base (same rig as the other naval models) ---
        var root  = new GameObject("StealthCruiser_Model");
        var dummy = new GameObject("Dummy_Root"); dummy.transform.SetParent(root.transform); dummy.transform.localPosition = Vector3.zero;
        var bone  = new GameObject("Base");       bone.transform.SetParent(dummy.transform);  bone.transform.localPosition = Vector3.zero;

        var bind = new Matrix4x4();
        bind.SetRow(0, new Vector4(1f,  0f, 0f,  0f));
        bind.SetRow(1, new Vector4(0f,  0f, 1f,  0.101f));
        bind.SetRow(2, new Vector4(0f, -1f, 0f, -0.069f));
        bind.SetRow(3, new Vector4(0f,  0f, 0f,  1f));
        // flat/faceted shading: unweld so every triangle has its own 3 verts -> RecalculateNormals yields a single
        // face normal per face (hard angular edges) instead of smoothed/rounded ones.
        if (FlatShading)
        {
            var sv = mesh.vertices; var suv = mesh.uv; var st = mesh.triangles;
            bool hasUV = suv != null && suv.Length == sv.Length;
            var nv = new Vector3[st.Length]; var nuv = new Vector2[st.Length]; var nt = new int[st.Length];
            for (int i = 0; i < st.Length; i++) { nv[i] = sv[st[i]]; if (hasUV) nuv[i] = suv[st[i]]; nt[i] = i; }
            mesh.Clear();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = nv; if (hasUV) mesh.uv = nuv; mesh.triangles = nt;
            Debug.Log($"[Cruiser] flat-shaded: unwelded to {nv.Length} verts");
        }

        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, mesh.vertexCount).ToArray();
        mesh.bindposes = new[] { bind };
        mesh.RecalculateBounds();
        if (FlatShading || mesh.normals == null || mesh.normals.Length != mesh.vertexCount) mesh.RecalculateNormals(); // else KEEP the model's own normals (faithful look)
        mesh.RecalculateTangents();   // VertexEncodingFormat 6 needs tangents
        AssetDatabase.CreateAsset(mesh, MeshPath);

        var mat = new Material(Shader.Find("Standard")) { name = "StealthCruiser_Mat", mainTexture = atlas };
        AssetDatabase.CreateAsset(mat, MatPath);

        var meshGO = new GameObject("Unit_StealthCruiser"); meshGO.transform.SetParent(root.transform);
        meshGO.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
        var smr = meshGO.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh; smr.bones = new[] { bone.transform }; smr.rootBone = bone.transform; smr.sharedMaterial = mat; smr.updateWhenOffscreen = true;

        var anim = root.AddComponent<Animator>();
        anim.avatar = AvatarBuilder.BuildGenericAvatar(root, "");

        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);

        // --- 4) bake the Amplitude Skeleton from the prefab ---
        var skelType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
            .FirstOrDefault(t => t.FullName == "Amplitude.Mercury.Animation.Skeleton");
        if (skelType == null) { Debug.LogError("[Cruiser] Skeleton type not found."); return; }
        var skel = ScriptableObject.CreateInstance(skelType);
        AssetDatabase.CreateAsset(skel, SkeletonPath);
        try { skelType.GetMethod("SetPrefab", new[] { typeof(GameObject) })?.Invoke(skel, new object[] { prefab });
              skelType.GetMethod("Reimport", Type.EmptyTypes)?.Invoke(skel, null); }
        catch (Exception e) { Debug.LogError("[Cruiser] bake failed: " + e); }
        EditorUtility.SetDirty(skel);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        // --- 5) log the Amplitude GUIDs (skeleton + atlas) for the plugin ---
        var report = new System.Text.StringBuilder();
        report.AppendLine("[StealthCruiser] DONE.");
        report.AppendLine("skeleton asset GUID = " + AssetDatabase.AssetPathToGUID(SkeletonPath));
        report.AppendLine("atlas asset GUID    = " + AssetDatabase.AssetPathToGUID(AtlasPath));
        var adbType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
            .FirstOrDefault(t => t.FullName == "Amplitude.Framework.Asset.AssetDatabase");
        if (adbType != null)
        {
            var getGuid = adbType.GetMethod("GetAssetGUID", new[] { typeof(UnityEngine.Object) });
            report.AppendLine("skeleton Amplitude GUID fields: " + GuidFields(getGuid, skel));
            report.AppendLine("atlas Amplitude GUID fields:    " + GuidFields(getGuid, atlas));
            report.AppendLine("SourcePrefab (Template) fields: " + MemberFields(skel, "prefab"));
        }
        File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "StealthCruiserGuids.txt"), report.ToString());
        Debug.Log(report.ToString());
    }

    static string GuidFields(MethodInfo getGuid, UnityEngine.Object asset)
    {
        try { var g = getGuid.Invoke(null, new object[] { asset }); return FieldsOf(g); } catch (Exception e) { return "(err " + e.Message + ")"; }
    }
    static string MemberFields(object o, string field)
    {
        var f = o.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return f != null ? FieldsOf(f.GetValue(o)) : "(no " + field + ")";
    }
    static string FieldsOf(object g)
    {
        if (g == null) return "(null)";
        var sb = new System.Text.StringBuilder();
        foreach (var f in g.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) sb.Append(f.Name + "=" + f.GetValue(g) + " ");
        return sb.ToString();
    }
    static Type[] SafeTypes(Assembly a) { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); } catch { return Array.Empty<Type>(); } }
}

// Persistent bake dialog. Values are stored in EditorPrefs, so they survive script recompiles AND Unity restarts.
public class StealthShipConfigWindow : EditorWindow
{
    const string KX = "ENC.StealthShip.offX", KY = "ENC.StealthShip.offY", KZ = "ENC.StealthShip.offZ", KS = "ENC.StealthShip.size";
    float offX, offY, offZ, sizeMult;

    [MenuItem("Tools/StealthCruiser/Configure Stealth Ship")]
    static void Open()
    {
        var w = GetWindow<StealthShipConfigWindow>(true, "Configure Stealth Ship");
        w.minSize = new Vector2(380, 240);
        w.Show();
    }

    void OnEnable()
    {
        offX = EditorPrefs.GetFloat(KX, 0f);
        offY = EditorPrefs.GetFloat(KY, 0f);
        offZ = EditorPrefs.GetFloat(KZ, -0.2f);
        sizeMult = EditorPrefs.GetFloat(KS, 1f);
    }

    void Save()
    {
        EditorPrefs.SetFloat(KX, offX); EditorPrefs.SetFloat(KY, offY);
        EditorPrefs.SetFloat(KZ, offZ); EditorPrefs.SetFloat(KS, sizeMult);
    }

    void OnDisable() { Save(); }   // persist on close

    void OnGUI()
    {
        EditorGUILayout.LabelField("Stealth Ship - bake settings", EditorStyles.boldLabel);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Position offset (applied after orient + scale)", EditorStyles.miniBoldLabel);
        offX = EditorGUILayout.FloatField("Offset X (sway)", offX);
        offY = EditorGUILayout.FloatField("Offset Y (fore/aft)", offY);
        offZ = EditorGUILayout.FloatField("Offset Z (waterline, - = lower)", offZ);
        EditorGUILayout.Space();
        sizeMult = EditorGUILayout.FloatField("Size multiplier", sizeMult);
        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Build Skeleton", GUILayout.Height(30))) { Save(); StealthCruiserModel.Build(new Vector3(offX, offY, offZ), sizeMult); }
            if (GUILayout.Button("Reset", GUILayout.Height(30), GUILayout.Width(72))) { offX = 0f; offY = 0f; offZ = -0.2f; sizeMult = 1f; Save(); GUI.FocusControl(null); }
        }
        EditorGUILayout.HelpBox("Build re-bakes the skeleton + atlas with these values (auto-saved).\n" +
            "Then rebuild the mod and relaunch to see it in-game.\n" +
            "Waterline: nudge Offset Z - negative sinks the hull, positive raises it.", MessageType.Info);
    }
}
