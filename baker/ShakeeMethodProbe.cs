// ShakeeMethodProbe.cs (ENC editor) — POC step 1 for shakee's data-driven approach.
// Can the mod-tools editor CREATE + populate + save an AnimationManagerContent asset (the "data file" a modder would
// ship, listing their custom skeleton)? This is the crux of whether shakee's merge workflow is authorable/easy.
// Lists the already-baked Zeppelin skeleton, dumps the AnimationManagerContent structure, saves a mod asset,
// reloads + verifies. Output: <project>/ShakeeMethodProbe.txt
//
// RUN: Tools > ShakeePOC > Create Mod AnimationContent

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class ShakeeMethodProbe
{
    const BindingFlags ANY = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static StringBuilder _sb;
    static void L(string s) { _sb.AppendLine(s); }

    [MenuItem("Tools/ShakeePOC/Create Mod AnimationContent")]
    static void Create()
    {
        _sb = new StringBuilder();
        string summary;
        try { summary = Run(); }
        catch (Exception e) { L("FATAL: " + e); summary = "FAILED: " + e.Message; }
        var path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ShakeeMethodProbe.txt");
        File.WriteAllText(path, _sb.ToString());
        Debug.Log("[ShakeePOC] " + summary + "  ->  " + path);
    }

    static string Run()
    {
        var amcType = FindType("Amplitude.Mercury.Animation.AnimationManagerContent");
        var skelType = FindType("Amplitude.Mercury.Animation.Skeleton");
        var adb = FindType("Amplitude.Framework.Asset.AssetDatabase");
        if (amcType == null || skelType == null || adb == null) return "AnimationManagerContent/Skeleton/AssetDatabase type not found";

        bool isSO = typeof(ScriptableObject).IsAssignableFrom(amcType);
        L("AnimationManagerContent base = " + amcType.BaseType?.FullName + "  (ScriptableObject=" + isSO + ")");

        // 1) what fields does AnimationManagerContent have? (what a modder would need to populate)
        L("\n=== AnimationManagerContent fields ===");
        foreach (var f in amcType.GetFields(ANY)) L("  " + f.Name + " : " + Pretty(f.FieldType));

        if (!isSO) return "AnimationManagerContent is NOT a ScriptableObject (base=" + amcType.BaseType?.Name + ") — can't CreateInstance/CreateAsset directly";

        // 2) get the already-baked Zeppelin skeleton's Amplitude GUID
        var zepPath = "Assets/Resources/Zeppelin_Skeleton.asset";
        var zep = AssetDatabase.LoadAssetAtPath(zepPath, skelType);
        if (zep == null) return "Zeppelin_Skeleton.asset not found at " + zepPath;
        var getGuid = adb.GetMethod("GetAssetGUID", new[] { typeof(UnityEngine.Object) });
        var zepGuid = getGuid.Invoke(null, new object[] { zep });
        L("\nZeppelin skeleton Amplitude GUID = " + zepGuid + "  (type " + zepGuid.GetType().Name + ")");

        // 3) create + populate the AnimationManagerContent: MeshCollections = [zeppelin guid]
        var inst = ScriptableObject.CreateInstance(amcType);
        inst.name = "ENC_ModAnimationContent";
        var mcField = amcType.GetField("MeshCollections", ANY);
        if (mcField == null) L("WARNING: MeshCollections field not found");
        else
        {
            var guidType = zepGuid.GetType();                 // Amplitude.Framework.Guid
            var arr = Array.CreateInstance(guidType, 1);
            arr.SetValue(zepGuid, 0);
            mcField.SetValue(inst, arr);
            L("set MeshCollections = [zeppelin guid]  (element type " + guidType.Name + ")");
        }

        // 4) save as a mod asset
        var outPath = "Assets/Databases/ENC_ModAnimationContent.asset";
        AssetDatabase.CreateAsset(inst, outPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        L("\nCreated mod asset at " + outPath);

        // 5) reload + verify it round-tripped, and report its own Amplitude GUID (needed to find/merge it at runtime)
        var reloaded = AssetDatabase.LoadAssetAtPath(outPath, amcType);
        if (reloaded == null) return "created but reload returned null";
        var mc2 = mcField?.GetValue(reloaded) as Array;
        L("reloaded MeshCollections length = " + (mc2?.Length ?? -1) + (mc2 != null && mc2.Length > 0 ? "  [0]=" + mc2.GetValue(0) : ""));
        object newGuid = null;
        try { newGuid = getGuid.Invoke(null, new object[] { reloaded }); } catch { }
        L("new AnimationManagerContent Amplitude GUID = " + newGuid);
        L("  AMC GUID fields: " + GuidFields(newGuid));
        L("  zeppelin skeleton GUID fields: " + GuidFields(zepGuid));

        return "CREATED + round-tripped AnimationManagerContent (MeshCollections=" + (mc2?.Length ?? -1) + ") — shakee data-file is authorable";
    }

    static string GuidFields(object guid)
    {
        if (guid == null) return "(null)";
        var sb = new StringBuilder();
        foreach (var f in guid.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            sb.Append(f.Name + "=" + f.GetValue(guid) + " ");
        return sb.ToString();
    }

    static Type FindType(string n) => AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).FirstOrDefault(t => t.FullName == n);
    static Type[] SafeTypes(Assembly a) { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); } catch { return Array.Empty<Type>(); } }
    static string Pretty(Type t) => t.IsArray ? Pretty(t.GetElementType()) + "[]" : (t.IsGenericType ? t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(a => a.Name)) + ">" : t.Name);
}
