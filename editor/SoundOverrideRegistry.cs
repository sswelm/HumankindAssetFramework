// SoundOverrideRegistry.cs (HAF editor) — the Game Sound Lab's config store: haf_sounds.json in the game's
// BepInEx/config, read by the plugin's audio-override path (UniversalInject.EnsureSoundOverrides / ShouldSilenceEvent).
// Mirrors DistrictRegistry (same target dir, corrupt-guard + atomic write + git-tracked project backup) but for global
// AUDIO OVERRIDES: each entry silences a vanilla Wwise event by name-substring (and, later, substitutes a better one).
//
// The RUNTIME reads only { silence } today (Newtonsoft JObject — extra fields ignored); `replaceWith` is reserved for
// the future silence-then-substitute step and `note` is editor-only. Same JsonUtility caveat as ModelRegistry: the
// editor WRITES with JsonUtility, the plugin must keep parsing with Newtonsoft.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
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
    public static string RegistryPath => Path.Combine(ModelRegistry.ConfigDir, "haf_sounds.json");
    public static string ProjectBackupPath => Path.Combine(Application.dataPath, "Databases", "haf_sounds.backup.json");

    // Set when the last Load() found a file it couldn't parse; Save() refuses while set, so a corrupt / half-edited
    // registry is never silently replaced with a fresh empty list.
    static bool lastLoadCorrupt;

    static List<SoundOverrideDef> Clean(List<SoundOverrideDef> list)
    {
        list = list ?? new List<SoundOverrideDef>();
        list.RemoveAll(o => o == null || string.IsNullOrWhiteSpace(o.silence));
        list.Sort((a, b) => string.Compare(a?.silence, b?.silence, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public static List<SoundOverrideDef> Load()
    {
        try
        {
            if (!File.Exists(RegistryPath))
            {
                lastLoadCorrupt = false;
                if (File.Exists(ProjectBackupPath))
                {
                    // parse the backup in its OWN try/catch (see ModelRegistry E6): an unreadable backup while the live
                    // file is missing must read as "no backup", not lock Save forever.
                    try
                    {
                        var backupJson = File.ReadAllText(ProjectBackupPath);
                        var b = JsonUtility.FromJson<SoundRegistryFile>(backupJson);
                        if (b?.overrides != null && b.overrides.Count > 0)
                        {
                            try { Directory.CreateDirectory(ModelRegistry.ConfigDir); File.WriteAllText(RegistryPath, backupJson); } catch { }
                            Debug.Log($"[Sound] game sound-override registry was missing — restored {b.overrides.Count} rule(s) from the project backup.");
                            return Clean(b.overrides);
                        }
                    }
                    catch (Exception be) { Debug.LogWarning($"[Sound] the project backup '{ProjectBackupPath}' is unreadable ({be.Message}) — treating as no backup."); }
                }
                return new List<SoundOverrideDef>();
            }
            var data = JsonUtility.FromJson<SoundRegistryFile>(File.ReadAllText(RegistryPath));
            lastLoadCorrupt = false;
            return Clean(data?.overrides ?? new List<SoundOverrideDef>());
        }
        catch (Exception e)
        {
            lastLoadCorrupt = true;
            try { File.Copy(RegistryPath, RegistryPath + ".corrupt.json", true); } catch { }
            Debug.LogError($"[Sound] registry '{RegistryPath}' is unreadable ({e.Message}) — backed up to " +
                           $"'{Path.GetFileName(RegistryPath)}.corrupt.json'. Fix or delete it; the Lab won't save until then.");
            return new List<SoundOverrideDef>();
        }
    }

    // True = written. False = nothing saved (corrupt-guard tripped, or the atomic write hit a lock) — surface it.
    public static bool Save(List<SoundOverrideDef> overrides)
    {
        if (lastLoadCorrupt)
        {
            Debug.LogError("[Sound] not saving: the existing sound-override registry was unreadable (see the .corrupt.json backup). Fix or delete it first.");
            return false;
        }
        var cleaned = Clean(overrides);
        var json = JsonUtility.ToJson(new SoundRegistryFile { overrides = cleaned }, true);
        try
        {
            Directory.CreateDirectory(ModelRegistry.ConfigDir);
            var tmp = RegistryPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(RegistryPath)) File.Replace(tmp, RegistryPath, null);
            else File.Move(tmp, RegistryPath);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Sound] registry write FAILED — the override list was NOT saved to '{RegistryPath}' ({e.Message}). " +
                           "Close whatever's locking it (AV, indexer, the running game) and retry.");
            return false;
        }
        try { File.WriteAllText(ProjectBackupPath, json); } catch (Exception e) { Debug.LogWarning("[Sound] project backup write failed: " + e.Message); }
        AssetDatabase.Refresh();
        return true;
    }

    public static bool Upsert(SoundOverrideDef def)
    {
        var list = Load();
        list.RemoveAll(o => string.Equals(o.silence, def.silence, StringComparison.OrdinalIgnoreCase));
        list.Add(def);
        return Save(list);
    }

    public static bool Remove(string silence)
    {
        var list = Load();
        int before = list.Count;
        list.RemoveAll(o => string.Equals(o.silence, silence, StringComparison.OrdinalIgnoreCase));
        if (list.Count == before) return false;
        return Save(list);
    }
}
