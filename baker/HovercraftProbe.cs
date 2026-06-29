// HovercraftProbe.cs (ENC editor) — RECON: how to reach VANILLA skeletons in the mod-tools editor.
// FindAssetPathsOfType only sees the mod's own assets; vanilla skeletons live in game asset bundles that aren't
// mounted in an editor script session. This probe reports the bundle/provider state and tries to enable bundle
// access, then re-enumerates to see if vanilla skeletons (LandingCrafts) appear.
// Output: <project>/HovercraftProbe.txt  (FILE ONLY). Restores any toggled state.
//
// RUN: Tools > Hovercraft > Probe Vanilla Skeleton

using System;
using System.Collections;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class HovercraftProbe
{
    const BindingFlags ANY = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    const BindingFlags PUBSTAT = BindingFlags.Public | BindingFlags.Static;
    static StringBuilder _sb;
    static Type _adb, _skel;
    static void L(string s) { _sb.AppendLine(s); }

    [MenuItem("Tools/Hovercraft/Probe Vanilla Skeleton")]
    static void Probe()
    {
        _sb = new StringBuilder();
        string summary;
        try { summary = Run(); }
        catch (Exception e) { L("FATAL: " + e); summary = "FAILED: " + e.Message; }
        var path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "HovercraftProbe.txt");
        File.WriteAllText(path, _sb.ToString());
        Debug.Log("[Probe] " + summary + "  ->  " + path);
    }

    static string Run()
    {
        _adb = FindType("Amplitude.Framework.Asset.AssetDatabase");
        _skel = FindType("Amplitude.Mercury.Animation.Skeleton");
        if (_adb == null || _skel == null) return "AssetDatabase/Skeleton type not found";

        // baseline enumeration
        var paths0 = EnumSkeletons();
        L($"=== baseline: FindAssetPathsOfType<Skeleton>() = {paths0.Length} ===");
        foreach (var p in paths0) L("  " + p);

        // bundle / provider state
        L("\n=== bundle state ===");
        L("SimulateAssetBundlesInPlay = " + TryProp("SimulateAssetBundlesInPlay"));
        L("LoadedAssetBundleFlags = " + TryProp("LoadedAssetBundleFlags"));
        L("ContentRevision = " + TryProp("ContentRevision"));
        L("\n=== providers (AllProviders) ===");
        var providers = TryProp("AllProviders") as IEnumerable;
        if (providers != null)
            foreach (var pr in providers)
            {
                string nm = (Get(pr, "Name") ?? Get(pr, "ProviderName") ?? pr.GetType().Name)?.ToString();
                object mounted = Invoke("IsMounted", new[] { typeof(string) }, new object[] { nm });
                L($"  {pr.GetType().Name}  name='{nm}'  IsMounted={mounted}");
            }
        else L("  (AllProviders null)");

        // --- reach into the mounted units bundle and enumerate its skeletons ---
        object unitsProv = null;
        if (providers != null)
            foreach (var pr in providers)
            {
                var nm = (Get(pr, "Name") ?? "")?.ToString() ?? "";
                if (nm.IndexOf("units", StringComparison.OrdinalIgnoreCase) >= 0) { unitsProv = pr; break; }
            }
        if (unitsProv == null) return $"baseline {paths0.Length}; units provider not found";

        L("\n=== units provider members (looking for AssetBundle) ===");
        var ab = ExtractAssetBundle(unitsProv);
        if (ab == null) { L("  (no AssetBundle found among members — see member list above)"); return $"baseline {paths0.Length}; units AssetBundle not extractable"; }

        L("\n=== units AssetBundle assets ===");
        var allNames = (string[])ab.GetType().GetMethod("GetAllAssetNames", Type.EmptyTypes).Invoke(ab, null);
        L("GetAllAssetNames count = " + allNames.Length);
        var navalSkel = allNames.Where(n => n.IndexOf("skeleton", StringComparison.OrdinalIgnoreCase) >= 0 && IsNaval(n)).OrderBy(n => n).ToArray();
        L("naval/transport skeleton asset names:");
        foreach (var n in navalSkel) L("  " + n);

        var lcName = allNames.FirstOrDefault(n => n.IndexOf("landingcraft", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("skeleton", StringComparison.OrdinalIgnoreCase) >= 0);
        if (lcName == null) return $"units bundle has {allNames.Length} assets; LandingCrafts skeleton name NOT found";
        L("\nLandingCrafts asset name = " + lcName);

        // load via the bundle directly, then map to its Amplitude GUID
        var loadFromBundle = ab.GetType().GetMethod("LoadAsset", new[] { typeof(string), typeof(Type) });
        var lcSkel = loadFromBundle.Invoke(ab, new object[] { lcName, _skel }) as UnityEngine.Object;
        if (lcSkel == null) return "LandingCrafts asset found but bundle.LoadAsset returned null";
        var getGuid = _adb.GetMethod("GetAssetGUID", new[] { typeof(UnityEngine.Object) });
        L("LandingCrafts Amplitude GUID = " + getGuid.Invoke(null, new object[] { lcSkel }));
        DumpSkeleton(lcSkel);

        var bones = Get(lcSkel, "BoneInfos") as Array; var smi = GetField(lcSkel, "skinnedMeshInfos") as Array;
        return $"LOADED vanilla LandingCrafts from units bundle: bones={(bones?.Length ?? -1)}, meshes={(smi?.Length ?? -1)} — deep-clone VIABLE";
    }

    // ---- helpers ----
    static string[] EnumSkeletons()
    {
        var find = _adb.GetMethods(PUBSTAT).FirstOrDefault(m => m.Name == "FindAssetPathsOfType" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        try { return (string[])find.MakeGenericMethod(_skel).Invoke(null, null) ?? Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }
    static bool IsNaval(string p) => new[] { "transport", "landing", "hover", "carrack", "galley", "boat", "ship" }.Any(k => p.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
    static string FindLanding(string[] paths) => paths.FirstOrDefault(p => p.IndexOf("LandingCraft", StringComparison.OrdinalIgnoreCase) >= 0);

    static object LoadByPath(string path)
    {
        try
        {
            var guid = _adb.GetMethod("GetGuidFromAssetPath", new[] { typeof(string) }).Invoke(null, new object[] { path });
            var load = _adb.GetMethods(PUBSTAT).FirstOrDefault(m => m.Name == "LoadAsset" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == guid.GetType());
            L("  LandingCrafts GUID = " + guid);
            return load.MakeGenericMethod(_skel).Invoke(null, new[] { guid });
        }
        catch (Exception e) { L("  LoadByPath failed: " + (e.InnerException ?? e).Message); return null; }
    }

    static void DumpSkeleton(object skel)
    {
        L("\n=== LandingCrafts skeleton dump ===");
        L("name = " + (skel as UnityEngine.Object)?.name);
        L("prefab (SourcePrefab) = " + Get(skel, "prefab"));
        L("animatorController = " + Get(skel, "animatorController") + "  animatorOverrideController = " + Get(skel, "animatorOverrideController"));
        var bones = Get(skel, "BoneInfos") as Array;
        L("BoneInfos count = " + (bones?.Length ?? -1));
        if (bones != null && bones.Length > 0) L("  first bones: " + string.Join(", ", bones.Cast<object>().Take(8).Select(b => Get(b, "Name"))));
        var smi = GetField(skel, "skinnedMeshInfos") as Array;
        L("skinnedMeshInfos count = " + (smi?.Length ?? -1));
        if (smi != null) for (int i = 0; i < smi.Length; i++) { var it = smi.GetValue(i); var fc = Get(it, "FxMeshContent"); L($"  [{i}] MeshName='{Get(it, "MeshName")}' FxMeshContent.Guid={(fc != null ? Get(fc, "Guid") : "null")} vertexCount={(fc != null ? Get(fc, "vertexCount") : "?")}"); }
    }

    // Find a UnityEngine.AssetBundle held by the provider (field/property), logging members for visibility.
    static UnityEngine.AssetBundle ExtractAssetBundle(object prov)
    {
        UnityEngine.AssetBundle found = null;
        foreach (var f in prov.GetType().GetFields(ANY))
        {
            object v = null; try { v = f.GetValue(prov); } catch { }
            L($"  field {f.Name} : {f.FieldType.Name}" + (v is UnityEngine.AssetBundle ? "  <-- AssetBundle" : ""));
            if (v is UnityEngine.AssetBundle ab) found = found ?? ab;
        }
        foreach (var p in prov.GetType().GetProperties(ANY))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object v = null; try { v = p.GetValue(prov); } catch { }
            L($"  prop  {p.Name} : {p.PropertyType.Name}" + (v is UnityEngine.AssetBundle ? "  <-- AssetBundle" : ""));
            if (v is UnityEngine.AssetBundle ab) found = found ?? ab;
        }
        return found;
    }

    static object TryProp(string n) { try { var p = _adb.GetProperty(n, PUBSTAT); if (p != null) return p.GetValue(null); var g = _adb.GetMethod("get_" + n, PUBSTAT); return g?.Invoke(null, null); } catch (Exception e) { return "(err " + e.Message + ")"; } }
    static bool TrySetProp(string n, object v) { try { var s = _adb.GetMethod("set_" + n, PUBSTAT); if (s == null) return false; s.Invoke(null, new[] { v }); return true; } catch { return false; } }
    static void TryVoid(string n) { try { _adb.GetMethod(n, PUBSTAT, null, Type.EmptyTypes, null)?.Invoke(null, null); } catch (Exception e) { L($"  {n}() threw: " + (e.InnerException ?? e).Message); } }
    static bool HasMethod(string n) => _adb.GetMethod(n, PUBSTAT, null, Type.EmptyTypes, null) != null;
    static object Invoke(string n, Type[] sig, object[] args) { try { return _adb.GetMethod(n, PUBSTAT, null, sig, null)?.Invoke(null, args); } catch { return null; } }

    static Type FindType(string fullName) => AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).FirstOrDefault(t => t.FullName == fullName);
    static Type[] SafeTypes(Assembly a) { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); } catch { return Array.Empty<Type>(); } }
    static object Get(object o, string name) { if (o == null) return null; var p = o.GetType().GetProperty(name, ANY); if (p != null) try { return p.GetValue(o); } catch { } return GetField(o, name); }
    static object GetField(object o, string name) { var f = o.GetType().GetField(name, ANY); return f != null ? f.GetValue(o) : null; }
}
