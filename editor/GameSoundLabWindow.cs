// GameSoundLabWindow.cs (HAF editor) — authors haf_sounds.json: game-wide audio overrides that silence vanilla Wwise
// sounds by event-name substring (any event — unit, ambient, music, UI). The plugin (UniversalInject.ShouldSilenceEvent)
// drops any posted event whose name contains one of these substrings, at the AudioManager.PostEvent service sink.
// Relaunch the game to apply edits.
//
// Distinct from the Sound Studio (which edits PER-MODEL sounds in the unit registry): this is a GLOBAL override list,
// not tied to any one model. `Replace with` is reserved for a future silence-then-substitute step (unused today).
//
// PICK LIST: the game's full Wwise event-name list is dumped by the plugin (F8 window -> Dump Sound Catalog) to
// BepInEx/config/haf_sound_catalog.txt. This window reads that file so you can SEARCH + click real event names instead
// of typing them blind.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class GameSoundLabWindow : EditorWindow
{
    List<SoundOverrideDef> entries;
    string status = "";
    Vector2 scroll;

    // catalog (pick list)
    string[] catalog = new string[0];
    string catalogFilter = "";
    bool showCatalog = true;
    Vector2 catScroll;
    int catIdx = 0;   // default to All — the Game Sound Lab spans every event family (units, ambient, music, UI)

    // Category tabs over the flat catalog. A name matches a category if it contains ANY of the category's keywords
    // (case-insensitive); "All" matches everything. Keywords track the game's event-name prefixes.
    static readonly (string label, string[] keys)[] Cats =
    {
        ("All",     new string[0]),
        ("Ambient", new[] { "ENV", "_Amb", "Ambient", "Atmo" }),
        ("Units",   new[] { "UNIT" }),
        ("Music",   new[] { "_SC_", "Raga", "Music", "Theme" }),
        ("UI",      new[] { "_UI_", "Menu", "Button", "Click" }),
    };

    static string CatalogPath => Path.Combine(ModelRegistry.ConfigDir, "haf_sound_catalog.txt");

    [MenuItem("Tools/HAF/Game Sound Lab")]
    static void Open() => GetWindow<GameSoundLabWindow>("Game Sound Lab").minSize = new Vector2(480, 420);

    void OnEnable() { Reload(); LoadCatalog(); }

    void Reload()
    {
        entries = SoundOverrideRegistry.Load();
        status = $"{entries.Count} override(s) loaded from haf_sounds.json.";
    }

    void LoadCatalog()
    {
        try { catalog = File.Exists(CatalogPath) ? File.ReadAllLines(CatalogPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray() : new string[0]; }
        catch { catalog = new string[0]; }
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Silence vanilla Wwise sounds by event-name SUBSTRING (case-insensitive). The plugin drops any sound whose " +
            "event name contains one of these, at the service sink every sound passes through — so keep substrings " +
            "SPECIFIC. Tip: trim a picked name (drop '_Start'/'_Stop') to catch a whole family of related events.\n\n" +
            "Writes haf_sounds.json — relaunch the game to apply. 'Replace with' is reserved for a future substitute " +
            "step (no effect yet).", MessageType.Info);

        // ---- override list ----
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Overrides", EditorStyles.boldLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(180));
        int removeAt = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Silence (event substring)", GUILayout.Width(170));
            e.silence = EditorGUILayout.TextField(e.silence);
            if (GUILayout.Button("Remove", GUILayout.Width(70))) removeAt = i;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Note (editor only)", GUILayout.Width(170));
            e.note = EditorGUILayout.TextField(e.note);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField("Replace with (future)", GUILayout.Width(170));
                e.replaceWith = EditorGUILayout.TextField(e.replaceWith);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndScrollView();
        if (removeAt >= 0) entries.RemoveAt(removeAt);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+ Add override")) entries.Add(new SoundOverrideDef());
        if (GUILayout.Button("Reload")) Reload();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Save", GUILayout.Width(120)))
        {
            status = SoundOverrideRegistry.Save(entries)
                ? $"Saved {entries.Count(o => !string.IsNullOrWhiteSpace(o.silence))} override(s) -> haf_sounds.json. Relaunch the game to apply."
                : "SAVE FAILED — see the Console.";
            Reload();
        }
        EditorGUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.LabelField(status, EditorStyles.miniLabel);

        // ---- catalog pick list ----
        EditorGUILayout.Space();
        showCatalog = EditorGUILayout.Foldout(showCatalog, $"Browse sound catalog ({catalog.Length} events)", true);
        if (!showCatalog) return;

        if (catalog.Length == 0)
        {
            EditorGUILayout.HelpBox("No event catalog found. In-game: open the plugin's F8 window and click \"Dump Sound Catalog\" " +
                                    "(writes haf_sound_catalog.txt), then click Reload catalog below.", MessageType.Warning);
            if (GUILayout.Button("Reload catalog")) LoadCatalog();
            return;
        }

        catIdx = GUILayout.Toolbar(catIdx, Cats.Select(c => c.label).ToArray());

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Search", GUILayout.Width(50));
        catalogFilter = EditorGUILayout.TextField(catalogFilter);
        if (GUILayout.Button("Reload catalog", GUILayout.Width(110))) LoadCatalog();
        EditorGUILayout.EndHorizontal();

        var keys = Cats[catIdx].keys;
        bool InCategory(string n) => keys.Length == 0 || keys.Any(k => n.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0);
        var matches = catalog.Where(n =>
            InCategory(n) &&
            (string.IsNullOrWhiteSpace(catalogFilter) || n.IndexOf(catalogFilter, System.StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
        EditorGUILayout.LabelField($"{matches.Length} match(es)" + (matches.Length > 200 ? " — showing first 200; refine the search" : "") +
                                   "  ·  click a name to add it as an override", EditorStyles.miniLabel);

        catScroll = EditorGUILayout.BeginScrollView(catScroll);
        int shown = 0;
        foreach (var name in matches)
        {
            if (shown++ >= 200) break;
            if (GUILayout.Button(name, EditorStyles.miniButton))
            {
                if (!entries.Any(o => string.Equals(o.silence, name, System.StringComparison.OrdinalIgnoreCase)))
                {
                    entries.Add(new SoundOverrideDef { silence = name });
                    status = $"Added '{name}' — trim it (drop _Start/_Stop) to catch related events, then Save.";
                }
            }
        }
        EditorGUILayout.EndScrollView();
    }
}
