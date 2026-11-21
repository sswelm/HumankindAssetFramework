// TechRequireAllRegistry.cs (HAF editor) — the "Tech Requires All" window's config store, read by the plugin's
// TechRequireAll (Patches/TechRequireAllPatch.cs) from haf_tech_requires_all.json in the game's BepInEx/config.
//
// The game unlocks a technology when ANY ONE of its prerequisites is completed, and a TechnologyDefinition has no field
// to say otherwise. This list names the technologies that must wait for ALL of theirs. It holds names and nothing else:
// the prerequisites themselves stay where they are, in the TechnologyDefinition (edit those in the Tech Tree viewer).
//
// One file on the shared SingleSourceRegistry engine, like districts, formations and sounds: the git-tracked project
// file is THE list, the deployed haf_tech_requires_all.json is a build artifact regenerated on every Save.
// Same JsonUtility caveat as the other registries: the editor WRITES with JsonUtility, the plugin parses with Newtonsoft.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

[Serializable]
class TechRequireAllFile
{
    public List<string> technologies = new List<string>();   // TechnologyDefinition names — RUNTIME
}

public static class TechRequireAllRegistry
{
    public const string ArtifactName = "haf_tech_requires_all.json";

    // Seam for the drill (tools/tech_requires_all_unity_drill.sh): the game's config folder is found through
    // EditorPrefs, which every Unity project on the machine shares - a drill must never redirect THAT.
    internal static Func<string> ConfigDirImpl = () => ModelRegistry.ConfigDir;

    static readonly SingleSourceRegistry<TechRequireAllFile> Store = new SingleSourceRegistry<TechRequireAllFile>(
        "[TechAll]",
        () => Path.Combine(Application.dataPath, "Databases", "haf_tech_requires_all.backup.json"),
        () => Path.Combine(ConfigDirImpl(), ArtifactName),
        f => f?.technologies?.Count ?? 0,
        "HAF.TechRequireAll.SingleSource", "Assets/Databases/haf_tech_requires_all.backup.json", "technologies", "technologies");

    public static string RegistryPath => Store.ArtifactPath;        // what the running game reads (derived)
    public static string SourcePath => Store.SourcePath;            // what the editor reads and writes (git-tracked)
    public static bool DeployPending => Store.DeployPending;
    public static string TakeNotice() => Store.TakeNotice();
    public static RegistryLoadVerdict Snapshot() => Store.Snapshot();   // the last Load's findings, frozen for the window
    public static RegistryBanner.Actions BannerActions => new RegistryBanner.Actions { RestoreDeploy = Store.RecoverFromArtifact, RestoreCommit = Store.RecoverFromGit, AcceptSource = Store.AcceptSource, SourcePath = Store.SourcePath };

    /// <summary>The version of the list the last successful Load returned; null after a failed one (see Save).</summary>
    public static string LoadedVersion => Store.LoadedVersion;

    /// <summary>The last Load, by whichever window, could not use the list (see RegistryLoadVerdict.Failed).</summary>
    public static bool LastLoadFailed => Store.LastLoadFailed;

    /// <summary>Trimmed, without blanks or duplicates, sorted by name: the form the file is written in.</summary>
    public static List<string> Clean(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var n in names ?? new string[0])
        {
            var s = (n ?? "").Trim();
            if (s.Length > 0 && seen.Add(s)) list.Add(s);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    public static List<string> Load() => Clean(Store.Load()?.technologies);

    /// <summary>
    /// Save the window's WHOLE list — only over the version the window loaded (<paramref name="loadedVersion"/> =
    /// LoadedVersion right after its Load). Another writer's change since, or a broken source, refuses with the reason
    /// in the Console.
    /// </summary>
    public static RegistryRules.SaveOutcome Save(IEnumerable<string> names, string loadedVersion) =>
        Store.ReplaceAll(new TechRequireAllFile { technologies = Clean(names) }, loadedVersion, "the technologies that require all prerequisites");

    /// <summary>
    /// Switch single technologies between the two rules: true = requires ALL its prerequisites (in the list),
    /// false = the game's own rule, any one (not in the list). For the Tech Tree viewer, which edits one technology at
    /// a time and never holds the whole list: an OPERATION on the list as it is at write time, so a name added or
    /// removed elsewhere since (the list's own window, git) is kept. <paramref name="changed"/> = something was written.
    /// </summary>
    public static RegistryRules.SaveOutcome SetRules(IDictionary<string, bool> requireAll, out bool changed) =>
        Store.Change(f => ApplyRules(f, requireAll), "the prerequisite rule (any one / all) of " + (requireAll?.Count ?? 0) + " technolog" + ((requireAll?.Count ?? 0) == 1 ? "y" : "ies"), out changed);

    internal static bool ApplyRules(TechRequireAllFile file, IDictionary<string, bool> requireAll)
    {
        var before = Clean(file.technologies);
        var names = new HashSet<string>(before, StringComparer.Ordinal);
        foreach (var kv in requireAll ?? new Dictionary<string, bool>())
        {
            string name = (kv.Key ?? "").Trim();
            if (name.Length == 0) continue;
            if (kv.Value) names.Add(name); else names.Remove(name);
        }
        var after = Clean(names);
        if (after.Count == before.Count && !after.Except(before).Any()) return false;   // both are cleaned: same names = same list
        file.technologies = after;
        return true;
    }
}
