// TechRequireAllWindow.cs (HAF editor) — authors haf_tech_requires_all.json: the list of technologies that unlock only
// when ALL their prerequisites are completed. The game's own rule is ANY ONE; the plugin (TechRequireAllPatch.cs)
// applies the stricter rule to the technologies named here, and queues every missing prerequisite in front of one
// when it is queued while locked. Relaunch the game to apply edits.
//
// The window holds a list of technologies and nothing else. Which prerequisites a technology has is not edited here:
// that stays in its TechnologyDefinition (Tools > HAF > Tech Tree > Open Viewer). The rows only SHOW them, so it is
// plain what "all" means for each entry, and say so when an entry cannot have any effect.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class TechRequireAllWindow : EditorWindow
{
    List<string> entries = new List<string>();
    RegistryLoadVerdict verdict = new RegistryLoadVerdict();   // what this window's last Reload found (see RegistryBanner)
    string loadedVersion;                                       // Save writes only over the version this window loaded
    List<string> loadedEntries = new List<string>();            // the list as loaded: "unsaved changes" is a comparison, not a flag
    string status = "";
    Vector2 scroll;

    // the tech tree, for titles and each entry's prerequisites (read-only here)
    List<TechTreeData.Node> nodes = new List<TechTreeData.Node>();
    Dictionary<string, TechTreeData.Node> byName = new Dictionary<string, TechTreeData.Node>();
    string treeProblem = "";

    [MenuItem("Tools/HAF/Tech Tree/Requires All Prerequisites")]
    static void Open() => GetWindow<TechRequireAllWindow>("Tech Requires All").minSize = new Vector2(460, 320);

    void OnEnable() { LoadTree(); Reload(); }

    void LoadTree()
    {
        treeProblem = "";
        try
        {
            nodes = TechTreeData.Build();
            byName = nodes.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.First());
            if (nodes.Count == 0) treeProblem = "No technologies were found in this project, so entries cannot be checked or picked from a list.";
        }
        catch (Exception e)
        {
            nodes = new List<TechTreeData.Node>(); byName = new Dictionary<string, TechTreeData.Node>();
            treeProblem = "The tech tree could not be read (" + e.Message + "), so entries cannot be checked or picked from a list.";
            Debug.LogError("[TechAll] tech tree read failed: " + e);
        }
    }

    void Reload()
    {
        entries = TechRequireAllRegistry.Load();
        loadedEntries = new List<string>(entries);
        loadedVersion = TechRequireAllRegistry.LoadedVersion;   // null after a failed load: Save then refuses
        verdict = TechRequireAllRegistry.Snapshot();
        string notice = TechRequireAllRegistry.TakeNotice();
        status = verdict.Failed
            ? "The list can't be used as loaded (see the banner above); Save is refused until that is resolved (then Reload)."
            : $"{entries.Count} technolog{(entries.Count == 1 ? "y" : "ies")} loaded." + (notice != "" ? " " + notice : "");
        TechTreeWindow.RepaintOpenWindows();   // an open viewer shows this list's rules: it re-reads on its next draw
    }

    bool Dirty => !TechRequireAllRegistry.Clean(entries).SequenceEqual(loadedEntries);

    /// <summary>
    /// The Tech Tree viewer saved: prerequisites, the list (its OR / AND switch), or both. Every open window reads the
    /// tech tree again, because its rows judge an entry by its prerequisites ("one prerequisite, no effect") and the
    /// picker offers by them (review 2026-11-21: the list was refreshed, the tree was not). A window without unsaved
    /// changes also shows the new list at once. One WITH unsaved changes keeps them: its Save is then refused,
    /// because the list changed since it loaded, and its status line says to reload and redo them.
    /// </summary>
    internal static void RefreshOpenWindows()
    {
        foreach (var w in Resources.FindObjectsOfTypeAll<TechRequireAllWindow>())
        {
            w.LoadTree();
            if (!w.Dirty) w.Reload();
            w.Repaint();
        }
    }

    string Title(string name) => byName.TryGetValue(name, out var n) ? n.Label : name;

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "A technology in this list unlocks only when ALL its prerequisites are researched. Every other technology keeps the " +
            "game's rule: any ONE prerequisite unlocks it.\n\n" +
            "Saves to the project's haf_tech_requires_all.backup.json (git-tracked) and deploys " + TechRequireAllRegistry.ArtifactName +
            " for the game — relaunch the game to apply. In multiplayer every player needs the same list.", MessageType.Info);

        string bannerResult = RegistryBanner.Draw(verdict, "tech-requires-all", TechRequireAllRegistry.BannerActions);
        if (bannerResult != null) { Reload(); status = bannerResult; Debug.Log("[TechAll] " + status); GUIUtility.ExitGUI(); }

        if (treeProblem != "") EditorGUILayout.HelpBox(treeProblem, MessageType.Warning);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField($"Technologies that require all prerequisites ({entries.Count})", EditorStyles.boldLabel);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        int removeAt = -1;
        if (entries.Count == 0)
            EditorGUILayout.LabelField("  (none — every technology unlocks on any one prerequisite)", EditorStyles.miniLabel);
        for (int i = 0; i < entries.Count; i++)
        {
            string name = entries[i];
            byName.TryGetValue(name, out var node);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(node != null ? node.Label : name, EditorStyles.boldLabel);
            if (node != null && GUILayout.Button(new GUIContent("Select", "Select this technology's definition in the Project window."), GUILayout.Width(60)))
            { Selection.activeObject = node.Asset; EditorGUIUtility.PingObject(node.Asset); }
            if (GUILayout.Button("Remove", GUILayout.Width(70))) removeAt = i;
            EditorGUILayout.EndHorizontal();
            if (node != null) EditorGUILayout.LabelField(name, EditorStyles.miniLabel);

            if (node == null)
            {
                if (treeProblem == "")
                    EditorGUILayout.HelpBox("No technology with this name exists in the project. The game ignores this entry.", MessageType.Warning);
            }
            else if (node.BasePrereqs.Length < 2)
                EditorGUILayout.HelpBox(node.BasePrereqs.Length == 0
                    ? "This technology has no prerequisites, so this entry has no effect."
                    : $"This technology has one prerequisite ({Title(node.BasePrereqs[0])}), so this entry has no effect. Add a second one in the Tech Tree viewer.", MessageType.Warning);
            else
                EditorGUILayout.LabelField("Needs all of: " + string.Join("  +  ", node.BasePrereqs.Select(Title)), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndScrollView();
        if (removeAt >= 0) entries.RemoveAt(removeAt);

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(nodes.Count == 0))
            if (GUILayout.Button(new GUIContent("+ Add technology", "Pick from the technologies that have two or more prerequisites and are not in the list yet.")))
                ShowPicker(GUILayoutUtility.GetLastRect());
        if (GUILayout.Button(new GUIContent("Reload", "Read the list and the tech tree again. Unsaved changes are asked about first."), GUILayout.Width(70))
            && (!Dirty || EditorUtility.DisplayDialog("Reload", "Discard the unsaved changes to the list?", "Discard", "Cancel")))
        { LoadTree(); Reload(); }
        GUILayout.FlexibleSpace();
        bool saveClicked;
        using (new EditorGUI.DisabledScope(verdict.Failed || !Dirty))   // not over a load that failed
            saveClicked = GUILayout.Button(Dirty ? "Save *" : "Save", GUILayout.Width(120));
        EditorGUILayout.EndHorizontal();

        if (saveClicked) Save();
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);   // wraps: a refusal's reason must not be cut off
    }

    void Save()
    {
        // Only a save that went through reloads: reloading after a failed one would discard the edits and replace the
        // failure with "N loaded". The status says what the outcome guarantees.
        var outcome = TechRequireAllRegistry.Save(entries, loadedVersion);
        int n = TechRequireAllRegistry.Clean(entries).Count;
        if (outcome == RegistryRules.SaveOutcome.Saved)
        {
            Reload();
            status = TechRequireAllRegistry.DeployPending
                ? $"Saved {n} technolog{(n == 1 ? "y" : "ies")} to the project, but the game's {TechRequireAllRegistry.ArtifactName} is not refreshed yet (see the Console) — Reload retries it."
                : $"Saved {n} technolog{(n == 1 ? "y" : "ies")} -> {TechRequireAllRegistry.ArtifactName}. Relaunch the game to apply.";
        }
        else status = outcome == RegistryRules.SaveOutcome.Refused
            ? "SAVE REFUSED — nothing was written; your changes are still here (see the Console for why — if the list changed since you loaded it, note your changes, Reload, and redo them)."
            : "Save could NOT be confirmed — the file may hold this list or the previous one (see the Console). Your changes are still here.";
    }

    void ShowPicker(Rect anchor)
    {
        var have = new HashSet<string>(entries, StringComparer.Ordinal);
        var candidates = nodes.Where(n => n.BasePrereqs.Length >= 2 && !have.Contains(n.Name)).ToList();
        if (candidates.Count == 0)
        {
            status = "Nothing to add: every technology with two or more prerequisites is already in the list.";
            return;
        }
        new TechPickerDropdown(new AdvancedDropdownState(), candidates, Add, "Add technology").Show(anchor);
    }

    void Add(string name)
    {
        if (!entries.Contains(name)) entries.Add(name);
        entries = TechRequireAllRegistry.Clean(entries);
        status = $"Added '{Title(name)}' — not saved yet.";
        Repaint();
    }
}
