// ZeppelinModel.cs  (TEMPORARY, ENC editor) — build a simple zeppelin (airship) model rigged to ONE "Base"
// bone, then bake it into an Amplitude Skeleton/MeshCollection asset that ships in the mod. The BepInEx
// plugin then registers that skeleton at runtime and points the zeppelin unit at it. Because the mesh + its
// skeleton are authored together (consistent), the pawn binds cleanly — one zeppelin, no detached/doubled parts.
//
// RUN:  Tools > Zeppelin > Build Zeppelin Skeleton
// Then BUILD the mod and tell Cloud the two GUIDs it logs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class ZeppelinModel
{
    const string PrefabPath   = "Assets/Resources/Zeppelin_Model.prefab";
    const string SkeletonPath = "Assets/Resources/Zeppelin_Skeleton.asset";

    [MenuItem("Tools/Zeppelin/Build Zeppelin Skeleton")]
    static void Build()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");

        // --- 1) procedural ZEPPELIN mesh (ref: Zeppelin.png): an elongated ellipsoid hull + 3 engine gondolas
        //        slung underneath + a cross of 4 tail fins, all combined into ONE skinned mesh.
        //        Axes in mesh space: Y = long axis (the missile's long axis); underside (gondolas) = -Z; rear = -Y.
        //        If the gondolas come out on the side/top instead of underneath, flip the Z sign on them. ---
        var parts = new List<CombineInstance>();
        var temps = new List<GameObject>();
        void AddPart(PrimitiveType type, Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            temps.Add(go);
            parts.Add(new CombineInstance { mesh = go.GetComponent<MeshFilter>().sharedMesh, transform = Matrix4x4.TRS(pos, Quaternion.identity, scale) });
        }
        // Travel/forward direction is mesh -Y (proven in-game), so the NOSE is at -Y, the tail fins trail at +Y,
        // and the gondolas sit slightly forward of centre (toward -Y).
        AddPart(PrimitiveType.Sphere, Vector3.zero, new Vector3(0.95f, 4.0f, 0.95f));               // hull (cigar)
        AddPart(PrimitiveType.Cube, new Vector3(0f, -0.7f, -0.52f), new Vector3(0.16f, 0.28f, 0.22f)); // fore gondola
        AddPart(PrimitiveType.Cube, new Vector3(0f, -0.1f, -0.55f), new Vector3(0.18f, 0.34f, 0.26f)); // control car
        AddPart(PrimitiveType.Cube, new Vector3(0f, 0.5f, -0.52f), new Vector3(0.16f, 0.28f, 0.22f));  // aft gondola
        AddPart(PrimitiveType.Cube, new Vector3(0f, 1.6f, 0.5f), new Vector3(0.05f, 0.7f, 0.5f));     // tail fin top
        AddPart(PrimitiveType.Cube, new Vector3(0f, 1.6f, -0.5f), new Vector3(0.05f, 0.7f, 0.5f));    // tail fin bottom
        AddPart(PrimitiveType.Cube, new Vector3(0.5f, 1.6f, 0f), new Vector3(0.5f, 0.7f, 0.05f));     // tail fin right
        AddPart(PrimitiveType.Cube, new Vector3(-0.5f, 1.6f, 0f), new Vector3(0.5f, 0.7f, 0.05f));    // tail fin left

        var mesh = new Mesh { name = "Zeppelin_ModelMesh" };
        mesh.CombineMeshes(parts.ToArray(), true, true);
        foreach (var g in temps) UnityEngine.Object.DestroyImmediate(g);

        // --- 2) rig: root -> Dummy_Root -> Base  (same shape as the cruise-missile rig) ---
        var root  = new GameObject("Zeppelin_Model");
        var dummy = new GameObject("Dummy_Root"); dummy.transform.SetParent(root.transform); dummy.transform.localPosition = Vector3.zero;
        var bone  = new GameObject("Base");       bone.transform.SetParent(dummy.transform);  bone.transform.localPosition = Vector3.zero;

        // skin 100% to Base, using the missile's bindpose (orients the hull nose-forward like the missile)
        var bind = new Matrix4x4();
        bind.SetRow(0, new Vector4(1f,  0f, 0f,  0f));
        bind.SetRow(1, new Vector4(0f,  0f, 1f,  0.101f));
        bind.SetRow(2, new Vector4(0f, -1f, 0f, -0.069f));
        bind.SetRow(3, new Vector4(0f,  0f, 0f,  1f));
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, mesh.vertexCount).ToArray();
        mesh.bindposes = new[] { bind };
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();   // the game's VertexEncodingFormat 6 needs tangents — missing => GPU hang
        AssetDatabase.CreateAsset(mesh, "Assets/Resources/Zeppelin_ModelMesh.asset");

        var mat = new Material(Shader.Find("Standard")) { name = "Zeppelin_ModelMat", color = new Color(0.75f, 0.75f, 0.8f) };
        AssetDatabase.CreateAsset(mat, "Assets/Resources/Zeppelin_ModelMat.mat");

        var meshGO = new GameObject("Unit_Era6_CruiseMissile_01"); meshGO.transform.SetParent(root.transform);
        meshGO.transform.localPosition = new Vector3(0f, 0.10f, -0.07f); meshGO.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
        var smr = meshGO.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh; smr.bones = new[] { bone.transform }; smr.rootBone = bone.transform; smr.sharedMaterial = mat; smr.updateWhenOffscreen = true;

        // generic Avatar so the rig is a valid animation target
        var anim = root.AddComponent<Animator>();
        anim.avatar = AvatarBuilder.BuildGenericAvatar(root, "");

        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        var prefabGuid = AssetDatabase.AssetPathToGUID(PrefabPath);

        // --- 3) bake the Amplitude Skeleton/MeshCollection from the prefab ---
        var skelType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
            .FirstOrDefault(t => t.FullName == "Amplitude.Mercury.Animation.Skeleton");
        if (skelType == null) { Debug.LogError("[Zeppelin] Skeleton type not found."); return; }

        var skel = ScriptableObject.CreateInstance(skelType);
        AssetDatabase.CreateAsset(skel, SkeletonPath);
        // SetPrefab(GameObject) -> stores the prefab GUID, then Reimport() bakes bones + skinned mesh
        var setPrefab = skelType.GetMethod("SetPrefab", new[] { typeof(GameObject) });
        var reimport  = skelType.GetMethod("Reimport", Type.EmptyTypes);
        try { setPrefab?.Invoke(skel, new object[] { prefab }); reimport?.Invoke(skel, null); }
        catch (Exception e) { Debug.LogError("[Zeppelin] bake failed: " + e); }

        EditorUtility.SetDirty(skel);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        var skelGuid = AssetDatabase.AssetPathToGUID(SkeletonPath);

        // log the AMPLITUDE runtime GUID (the exact value RegisterMeshCollection/LoadAsset use at runtime),
        // so the plugin doesn't have to guess the Unity->Amplitude encoding.
        string ampInfo = "(Amplitude AssetDatabase not found)";
        var adbType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
            .FirstOrDefault(t => t.FullName == "Amplitude.Framework.Asset.AssetDatabase");
        if (adbType != null)
        {
            var getGuid = adbType.GetMethod("GetAssetGUID", new[] { typeof(UnityEngine.Object) });
            object sg = null, pg = null;
            try { sg = getGuid?.Invoke(null, new object[] { skel }); pg = getGuid?.Invoke(null, new object[] { prefab }); } catch (Exception e) { ampInfo = "GetAssetGUID failed: " + e.Message; }
            if (sg != null)
            {
                string Fields(object g)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var f in g.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        sb.Append($"{f.Name}({f.FieldType.Name})={f.GetValue(g)} ");
                    return sb.ToString();
                }
                ampInfo = $"Amplitude skeleton GUID = {sg}\nAmplitude prefab GUID   = {pg}\nAmplitude Guid type     = {sg.GetType().FullName}\n" +
                          $"skeleton Guid fields: {Fields(sg)}\nprefab Guid fields:   {Fields(pg)}";
            }
        }

        var report = $"[Zeppelin] DONE.\n  prefab GUID   = {prefabGuid}\n  skeleton GUID = {skelGuid}\n{ampInfo}\n" +
                     "Now: BUILD the mod, then give Cloud both GUIDs.";
        File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ZeppelinModelGuids.txt"), report);
        Debug.Log(report);
    }

    static Type[] SafeTypes(Assembly a)
    { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); } catch { return Array.Empty<Type>(); } }
}
