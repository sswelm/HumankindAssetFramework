// SoundOverrideRegistry.cs (HAF editor) — the Game Sound Lab's config store, read by the plugin's audio-override path
// (UniversalInject.EnsureSoundOverrides / ShouldSilenceEvent) from haf_sounds.json in the game's BepInEx/config.
// Global AUDIO OVERRIDES: each entry silences a vanilla Wwise event by name-substring (and, later, substitutes a better
// one).
//
// THE COLLAPSE, finally (critical review 2026-09-30): this registry still ran the OLD two-file pattern — it READ the
// deployed file and wrote the git-tracked copy non-atomically after it, so an older deployed copy (a git pull brought a
// newer tracked file) overwrote the newer rules on the next save, and `{}` read as "no rules". It now runs on the shared
// SingleSourceRegistry engine like districts and formations: the git-tracked project file is THE registry, the deployed
// haf_sounds.json is a build artifact, and every save is checked. The source keeps its historical ".backup.json" name.
//
// The RUNTIME reads only { silence } today (Newtonsoft JObject — extra fields ignored); `replaceWith` is reserved for
// the future silence-then-substitute step and `note` is editor-only. Same JsonUtility caveat as ModelRegistry: the
// editor WRITES with JsonUtility, the plugin must keep parsing with Newtonsoft.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// One audio override. `silence` is the key (one rule per event-substring).
[Serializable]
public class SoundOverrideDef
{
    public string silence = "";      // Wwise event-name SUBSTRING to drop (case-insensitive) — RUNTIME
    public string replaceWith = "";  // reserved: event to post instead — RUNTIME (unused today)
    public string note = "";         // editor-only reminder of what this rule targets
}

[Serializable]
class SoundRegistryFile
{
    public List<SoundOverrideDef> overrides = new List<SoundOverrideDef>();
}

public static class SoundOverrideRegistry
{
    static readonly SingleSourceRegistry<SoundRegistryFile> Store = new SingleSourceRegistry<SoundRegistryFile>(
        "[Sound]",
        () => Path.Combine(Application.dataPath, "Databases", "haf_sounds.backup.json"),
        () => Path.Combine(ModelRegistry.ConfigDir, "haf_sounds.json"),
        f => f?.overrides?.Count ?? 0,
        "HAF.Sounds.SingleSource", "Assets/Databases/haf_sounds.backup.json", "sound override(s)", "overrides");

    public static string RegistryPath => Store.ArtifactPath;        // what the running game reads (derived)
    public static string SourcePath => Store.SourcePath;            // what the editor reads and writes (git-tracked)
    public static string ProjectBackupPath => Store.SourcePath;     // historical name, kept for callers
    public static bool LastLoadFailed => Store.LastLoadFailed;
    public static string LastLoadProblem => Store.LastLoadProblem;
    public static string TakeNotice() => Store.TakeNotice();

    /// <summary>The version of the rules the last successful Load returned; null after a failed one (see Save).</summary>
    public static string LoadedVersion => Store.LoadedVersion;

    static List<SoundOverrideDef> Clean(List<SoundOverrideDef> list)
    {
        list = list ?? new List<SoundOverrideDef>();
        list.RemoveAll(o => o == null || string.IsNullOrWhiteSpace(o.silence));
        list.Sort((a, b) => string.Compare(a?.silence, b?.silence, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public static List<SoundOverrideDef> Load() => Clean(Store.Load()?.overrides);

    /// <summary>
    /// Save the Lab's WHOLE list — only over the version the Lab loaded (<paramref name="loadedVersion"/> = LoadedVersion
    /// right after its Load). Another writer's change since, a broken source, or a source emptied outside the editor
    /// while the deploy still holds rules all refuse, with the reason in the Console.
    /// </summary>
    public static RegistryRules.SaveOutcome Save(List<SoundOverrideDef> overrides, string loadedVersion) =>
        Store.ReplaceAll(new SoundRegistryFile { overrides = Clean(new List<SoundOverrideDef>(overrides ?? new List<SoundOverrideDef>())) },
                         loadedVersion, "the sound overrides");
}
