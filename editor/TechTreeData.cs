using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the tech-tree data layer: for every TechnologyDefinition, resolve its
/// grid position + localized title + prerequisite edges into a clean row.
///
/// Three resolvers, all by-name reflection (never field enumeration, to avoid the
/// static/recursion noise the probes showed):
///   1. tech name -> TechnologyUIMapper -> TechTreeX/Y + Title (%key)
///   2. Title (%key) -> processed localization collection -> CompactedNodes[0].TextValue
///   3. tech -> TechnologyPrerequisite.SerializableTechnologyNames -> prereq names (OR)
///
/// Run "Tools/Tech Tree/Dump Data" to verify rows before any canvas is built.
/// </summary>
public static class TechTreeData
{
    const BindingFlags ALL =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public class Node
    {
        public string Name;        // asset name, e.g. "Technology_Era1_01"
        public string TitleKey;    // raw "%...Title"
        public string Label;       // resolved display text (falls back to key/name)
        public string Era;         // EraReference name, for grouping/tinting
        public string Tier;        // TechnologyTier enum

        // Per-asset references. "Vanilla*" = read-only reference-folder copy (fallback);
        // "Mod*" = the editable copy under Databases/ (null if not yet imported there).
        public UnityEngine.Object VanillaDef,    ModDef;     // TechnologyDefinition
        public UnityEngine.Object VanillaMapper, ModMapper;  // TechnologyUIMapper

        // Active = Databases copy if present, else reference copy. Reads come from these.
        public UnityEngine.Object ActiveDef    => ModDef    != null ? ModDef    : VanillaDef;
        public UnityEngine.Object ActiveMapper => ModMapper != null ? ModMapper : VanillaMapper;

        // "Modded" here means "exists under Databases/ (editable in place)".
        // Reference-only assets (ModDef/ModMapper null) get copied to New Additions on first edit.
        public bool DefModded    => ModDef    != null;
        public bool MapperModded => ModMapper != null;
        public bool AnyModded    => DefModded || MapperModded;

        // Base (saved-to-disk) values, read from the active assets at build time.
        public int    BaseX, BaseY;
        public string[] BasePrereqs;

        // Convenience: the asset to ping/select (prefer the def).
        public UnityEngine.Object Asset => ActiveDef ?? ActiveMapper;
    }

    // Default copy-on-write destination for lifting reference-only assets / new techs.
    public const string DefaultModPath = "Assets/Databases/New Additions";
    public const string DatabasesRoot  = "Assets/Databases";

    // Reference (read-only archive) path — shares the Index tool's EditorPrefs key,
    // so editing it in either tool stays in sync.
    static string ReferenceKey => "DescIndex.VanillaFolder." + Application.dataPath.GetHashCode();
    public static string ReferenceFolder
    {
        get => EditorPrefs.GetString(ReferenceKey, "Assets/VanillaReference");
        set => EditorPrefs.SetString(ReferenceKey, value);
    }

    static bool Under(UnityEngine.Object o, string root)
    {
        var p = AssetDatabase.GetAssetPath(o);
        return !string.IsNullOrEmpty(p) &&
               (p == root || p.StartsWith(root + "/", StringComparison.Ordinal));
    }

    public static List<Node> Build() => Build(DefaultModPath);

    public static List<Node> Build(string modPath)
    {
        var loc        = BuildLocalizationDict();
        var techType   = FindType("Amplitude.Mercury.Data.Simulation.TechnologyDefinition");
        var mapperType = FindType("Amplitude.Mercury.UI.TechnologyUIMapper");
        if (techType == null || mapperType == null)
        { Debug.LogError("[TechTree] Tech/Mapper type not found."); return new(); }

        string refRoot = ReferenceFolder;

        // Classify each asset: under Databases = shippable/editable; under reference = read-only archive.
        // Active = Databases copy if present, else reference copy.
        var dbDef = new Dictionary<string, UnityEngine.Object>();
        var refDef = new Dictionary<string, UnityEngine.Object>();
        foreach (var o in LoadAllOfType(techType))
        {
            if (Under(o, DatabasesRoot)) dbDef[o.name] = o;
            else if (Under(o, refRoot))  refDef[o.name] = o;
        }
        var dbMap = new Dictionary<string, UnityEngine.Object>();
        var refMap = new Dictionary<string, UnityEngine.Object>();
        foreach (var o in LoadAllOfType(mapperType))
        {
            if (Under(o, DatabasesRoot)) dbMap[o.name] = o;
            else if (Under(o, refRoot))  refMap[o.name] = o;
        }

        var f_era  = techType.GetField("EraReference", ALL);
        var f_tier = techType.GetField("TechnologyTier", ALL);

        var names = new HashSet<string>(dbDef.Keys);
        names.UnionWith(refDef.Keys);

        var nodes = new List<Node>();
        foreach (var name in names)
        {
            var node = new Node
            {
                Name          = name,
                VanillaDef    = refDef.TryGetValue(name, out var rd) ? rd : null,
                ModDef        = dbDef.TryGetValue(name, out var dd) ? dd : null,
                VanillaMapper = refMap.TryGetValue(name, out var rm) ? rm : null,
                ModMapper     = dbMap.TryGetValue(name, out var dm) ? dm : null,
            };

            var mapper = node.ActiveMapper;
            if (mapper != null)
            {
                node.BaseX    = GetInt(mapper, "TechTreeX");
                node.BaseY    = GetInt(mapper, "TechTreeY");
                node.TitleKey = GetString(mapper, "Title");
            }
            node.Label = !string.IsNullOrEmpty(node.TitleKey) && loc.TryGetValue(node.TitleKey, out var t)
                ? t : (string.IsNullOrEmpty(node.TitleKey) ? name : node.TitleKey);

            var def = node.ActiveDef;
            node.BasePrereqs = Array.Empty<string>();
            if (def != null)
            {
                var prereqObj = def.GetType().GetField("TechnologyPrerequisite", ALL)?.GetValue(def);
                if (prereqObj != null &&
                    prereqObj.GetType().GetField("SerializableTechnologyNames", ALL)?.GetValue(prereqObj) is string[] arr)
                    node.BasePrereqs = arr.Where(s => !string.IsNullOrEmpty(s)).ToArray();
                node.Era  = ResolveReferenceName(f_era?.GetValue(def));
                node.Tier = f_tier?.GetValue(def)?.ToString() ?? "";
            }
            nodes.Add(node);
        }
        return nodes;
    }

    // ── Resolver 2: build the %key -> text dictionary once ────────────────────
    static Dictionary<string, string> BuildLocalizationDict()
    {
        var dict = new Dictionary<string, string>(48000);

        // The processed/shipped collection: LocalizedStringElementCollection with
        // lineCollection : List<LocalizedStringElement>{ LineId, CompactedNodes[].TextValue }
        var collType = FindType("Amplitude.Framework.Localization.LocalizedStringElementCollection");
        if (collType == null) { Debug.LogWarning("[TechTree] Localization collection type not found; labels will fall back to keys."); return dict; }

        foreach (var coll in LoadAllOfType(collType))
        {
            // prefer en-US if multiple languages are present
            string lang = GetString(coll, "LanguageId");
            if (!string.IsNullOrEmpty(lang) && !lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) && dict.Count > 0)
                continue;

            var lines = collType.GetField("lineCollection", ALL)?.GetValue(coll) as IEnumerable;
            if (lines == null) continue;

            foreach (var line in lines)
            {
                if (line == null) continue;
                var lt = line.GetType();
                string id = lt.GetField("LineId", ALL)?.GetValue(line) as string;
                if (string.IsNullOrEmpty(id)) continue;

                if (lt.GetField("CompactedNodes", ALL)?.GetValue(line) is IList nodesArr && nodesArr.Count > 0)
                {
                    var first = nodesArr[0];
                    string text = first?.GetType().GetField("TextValue", ALL)?.GetValue(first) as string;
                    if (text != null) dict[id] = text;   // last-wins; en-US preferred above
                }
            }
        }
        return dict;
    }

    // ── Asset loading helpers ─────────────────────────────────────────────────
    static IEnumerable<UnityEngine.Object> LoadAllOfType(Type t)
    {
        foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && t.IsInstanceOfType(o))
                    yield return o;
        }
    }

    // ── Field read helpers (by name, never enumeration) ───────────────────────
    static int GetInt(object obj, string field)
    {
        var v = obj.GetType().GetField(field, ALL)?.GetValue(obj);
        return v is int i ? i : 0;
    }
    static string GetString(object obj, string field)
        => obj.GetType().GetField(field, ALL)?.GetValue(obj) as string ?? "";

    // DatatableElementReference -> its serializableElementName
    static string ResolveReferenceName(object reference)
    {
        if (reference == null) return "";
        return reference.GetType().GetField("serializableElementName", ALL)?.GetValue(reference) as string ?? "";
    }

    static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t != null) return t;
        }
        return null;
    }

    // ── Write path: ensure a writable asset, then field writers ───────────────
    // Returns the asset to write into. Active asset already under Databases/ -> edit
    // in place. Reference-only -> copy into modPath (New Additions) and return the
    // copy. Null if nothing to write (new-tech creation is v2).
    public static UnityEngine.Object EnsureWritable(Node node, bool isMapper, string modPath)
    {
        var active = isMapper ? node.ActiveMapper : node.ActiveDef;
        if (active == null)
        {
            Debug.LogWarning($"[TechTree] {node.Name}: no {(isMapper ? "mapper" : "def")} to write — new-tech creation is v2.");
            return null;
        }
        if (Under(active, DatabasesRoot)) return active;             // edit in place
        return CopyReferenceIntoMod(active, modPath, isMapper);      // lift reference -> New Additions
    }

    // Collection types that host the elements. Defs have a dedicated collection;
    // mappers live in the universal UIMappersCollection (mixed types, membership by
    // sub-asset type), so we locate it by PATH under modPath, not by type alone.
    const string DefCollectionType    = "Amplitude.Mercury.Data.Simulation.TechnologyDefinitionCollection";
    const string MapperCollectionType = "Amplitude.UI.UIMappersCollection";

    static UnityEngine.Object CopyReferenceIntoMod(UnityEngine.Object refAsset, string modPath, bool isMapper)
    {
        // Standalone (non-sub) reference asset: simple file copy into modPath.
        if (!AssetDatabase.IsSubAsset(refAsset))
        {
            EnsureFolder(modPath);
            string src = AssetDatabase.GetAssetPath(refAsset);
            string dst = $"{modPath}/{refAsset.name}.asset";
            if (!AssetDatabase.CopyAsset(src, dst))
            { Debug.LogError($"[TechTree] CopyAsset failed: {src} -> {dst}"); return null; }
            AssetDatabase.ImportAsset(dst);
            return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(dst);
        }

        // Sub-asset: lift it into the New Additions collection of the matching type.
        string wantType = isMapper ? MapperCollectionType : DefCollectionType;
        var host = FindCollectionUnder(modPath, wantType);
        if (host == null)
        {
            string shortName = wantType.Substring(wantType.LastIndexOf('.') + 1);
            Debug.LogWarning($"[TechTree] No '{shortName}' found under \"{modPath}\". " +
                             $"Create it via the DatatableElement Collection Editor first, then re-save. " +
                             $"({refAsset.name} not lifted.)");
            return null;
        }

        // Type-preserving duplicate, same name (so it overrides by name at load).
        var copy = UnityEngine.Object.Instantiate(refAsset);
        copy.name = refAsset.name;
        AssetDatabase.AddObjectToAsset(copy, host);
        EditorUtility.SetDirty(host);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(host));
        return copy;
    }

    // Finds the main collection asset of the given type whose path is under modPath.
    static UnityEngine.Object FindCollectionUnder(string modPath, string fullTypeName)
    {
        foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (path != modPath && !path.StartsWith(modPath + "/", StringComparison.Ordinal)) continue;
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            if (main != null && main.GetType().FullName == fullTypeName) return main;
        }
        return null;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parts = path.Split('/');
        string cur = parts[0];               // "Assets"
        for (int i = 1; i < parts.Length; i++)
        {
            string next = cur + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }

    public static void WritePosition(UnityEngine.Object mapper, int x, int y)
    {
        var t = mapper.GetType();
        t.GetField("TechTreeX", ALL)?.SetValue(mapper, x);
        t.GetField("TechTreeY", ALL)?.SetValue(mapper, y);
    }

    public static bool WritePrereqs(UnityEngine.Object def, string[] names)
    {
        var f_prereq = def.GetType().GetField("TechnologyPrerequisite", ALL);
        var prereqObj = f_prereq?.GetValue(def);
        if (prereqObj == null) return false;
        var f_names = prereqObj.GetType().GetField("SerializableTechnologyNames", ALL);
        if (f_names == null) return false;
        f_names.SetValue(prereqObj, names);
        if (prereqObj.GetType().IsValueType) f_prereq.SetValue(def, prereqObj);  // struct write-back
        return true;
        // The runtime-resolved TechnologyNames (StaticString[]) is rebuilt from
        // SerializableTechnologyNames on load, so only the serializable form is written.
    }

    // ── Verification dump ─────────────────────────────────────────────────────
    [MenuItem("Tools/HAF/Tech Tree/Diagnose Mod Split")]
    static void DiagnoseModSplit()
    {
        DiagnoseModSplit(DefaultModPath);
    }

    public static void DiagnoseModSplit(string modPath)
    {
        var techType   = FindType("Amplitude.Mercury.Data.Simulation.TechnologyDefinition");
        var mapperType = FindType("Amplitude.Mercury.UI.TechnologyUIMapper");
        string refRoot = ReferenceFolder;
        var sb = new StringBuilder();
        sb.AppendLine("=== Mod-split diagnostic ===");
        sb.AppendLine($"reference (read-only) = \"{refRoot}\"");
        sb.AppendLine($"databases (shippable) = \"{DatabasesRoot}\"");
        sb.AppendLine($"copy-on-write target  = \"{modPath}\"\n");

        void Report(string label, Type t)
        {
            if (t == null) { sb.AppendLine($"{label}: TYPE NOT FOUND\n"); return; }
            int inDb = 0, inRef = 0, elsewhere = 0;
            var dbFolders = new Dictionary<string, int>();
            foreach (var o in LoadAllOfType(t))
            {
                var p = AssetDatabase.GetAssetPath(o);
                if (Under(o, DatabasesRoot))
                {
                    inDb++;
                    var dir = System.IO.Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "";
                    dbFolders[dir] = dbFolders.TryGetValue(dir, out var c) ? c + 1 : 1;
                }
                else if (Under(o, refRoot)) inRef++;
                else elsewhere++;
            }
            sb.AppendLine($"{label}: {inDb} in Databases, {inRef} reference-only, {elsewhere} elsewhere(ignored)");
            foreach (var kv in dbFolders.OrderByDescending(k => k.Value).Take(12))
                sb.AppendLine($"      [{kv.Value,4}]  {kv.Key}");
            if (inDb == 0)
                sb.AppendLine("      (NONE in Databases — if techs should be editable, check that they were imported there)");
            sb.AppendLine();
        }

        Report("TechnologyDefinition", techType);
        Report("TechnologyUIMapper", mapperType);
        Debug.Log(sb.ToString());
    }

    [MenuItem("Tools/HAF/Tech Tree/Dump Data")]
    static void DumpData()
    {
        var nodes = Build();
        var sb = new StringBuilder();
        sb.AppendLine($"=== Tech Tree: {nodes.Count} technologies ===\n");

        int noPos = nodes.Count(n => n.BaseX == 0 && n.BaseY == 0);
        int noLabel = nodes.Count(n => n.Label == n.TitleKey || n.Label == n.Name);
        int modded = nodes.Count(n => n.AnyModded);
        sb.AppendLine($"warnings: {noPos} no position, {noLabel} unresolved label · {modded} modded\n");

        foreach (var n in nodes.OrderBy(n => n.Era).ThenBy(n => n.BaseX).ThenBy(n => n.BaseY))
        {
            string mod = n.AnyModded ? $" [{(n.DefModded ? "def" : "")}{(n.DefModded && n.MapperModded ? "+" : "")}{(n.MapperModded ? "map" : "")}]" : "";
            sb.AppendLine($"({n.BaseX,3},{n.BaseY,2}) [{n.Era}/{n.Tier}]{mod} {n.Name}");
            sb.AppendLine($"        \"{n.Label}\"");
            if (n.BasePrereqs.Length > 0)
                sb.AppendLine($"        <= {string.Join("  OR  ", n.BasePrereqs)}");
        }
        Debug.Log(sb.ToString());
    }
}