using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// One-click re-bake of the ReconDrone animated skeleton + clip after the slim FBX is
// regenerated. The FBX GUID is unchanged, so nothing in the registry needs editing —
// this just forces the two Amplitude assets to re-bake their cached data from the new
// 213-frame FBX (fixes the ~1s propeller stall caused by a padded static tail).
public static class ReconDroneRebake
{
    const string Fbx  = "Assets/Resources/ReconDrone/drone_rigged_slim.fbx";
    const string Skel = "Assets/Resources/ReconDrone_Skeleton.asset";
    const string Clip = "Assets/Resources/ReconDrone/camera_base.114Collection.asset";

    [MenuItem("Tools/ReconDrone/Rebake Animation (skeleton + clip)")]
    public static void Rebake()
    {
        // 1) reimport the FBX itself so its animation clip sub-asset updates to 213 frames
        AssetDatabase.ImportAsset(Fbx, ImportAssetOptions.ForceUpdate);

        // 2) reimport the skeleton (re-bakes bones + skinned mesh from the FBX) — proven method
        var skel = AssetDatabase.LoadMainAssetAtPath(Skel);
        if (skel == null) { Debug.LogError("[ReconDroneRebake] skeleton not found: " + Skel); return; }
        Invoke(skel, "Reimport");
        EditorUtility.SetDirty(skel);

        // 3) reimport the clip collection (re-bakes poseData from the clean 213-frame clip)
        var clip = AssetDatabase.LoadMainAssetAtPath(Clip);
        if (clip == null) { Debug.LogError("[ReconDroneRebake] clip collection not found: " + Clip); return; }
        if (!Invoke(clip, "Reimport"))
        {
            var names = clip.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.GetParameters().Length == 0).Select(m => m.Name).Distinct();
            Debug.LogWarning("[ReconDroneRebake] ClipCollection has no Reimport(); parameterless methods are: "
                + string.Join(", ", names) + "  — tell Claude which looks right.");
        }
        EditorUtility.SetDirty(clip);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[ReconDroneRebake] DONE — skeleton + ClipCollection rebaked from the 213-frame FBX. Launch and check the props.");
    }

    static bool Invoke(object obj, string method)
    {
        var m = obj.GetType().GetMethod(method,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (m == null) return false;
        try { m.Invoke(obj, null); Debug.Log("[ReconDroneRebake] " + obj.GetType().Name + "." + method + "() ok"); return true; }
        catch (Exception e) { Debug.LogError("[ReconDroneRebake] " + method + " threw: " + (e.InnerException ?? e).Message); return false; }
    }
}
