// FormationOverrideWindow.cs (HAF editor) — the FORMATION Override dialog (Tools ▸ HAF ▸ Formation Override).
// Links a UNIT (PresentationUnitDefinition name) to a CUSTOM formation asset authored in this project (extract a
// vanilla PresentationFormationDefinition into Assets/Databases/UnitFormation, duplicate it, edit dummies in the
// SDK's visual inspector), changing how many pawn models the unit displays: pawn count = ceil(health% × dummies).
//
// No bake, no bundle: a formation asset shipped in the mod bundle would never enter the game's datatable system
// (unit references resolve BY NAME against the live database). Instead this window serializes the formation's FULL
// data into haf_formations.json — dummy positions, the 6 per-orientation coordinate grids AND the six HIDDEN
// ColumnsCountPerRow arrays (invisible in the Inspector; historically the reason hand-made formations crashed the
// load with the misleading "mismatched mods" dialog) — and the plugin rebuilds + injects it at runtime through the
// public Database.Add, then repoints the unit. Consistency is VALIDATED here before anything can be saved.
//
// Picking a formation name that already exists in the game's database (e.g. a vanilla Formation_Close_9) makes the
// entry a pure repoint: the plugin links the unit to the existing formation and injects nothing.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class FormationOverrideWindow : EditorWindow
{
    [MenuItem("Tools/HAF/Formation Override")]
    static void Open() => GetWindow<FormationOverrideWindow>("Formation Override");

    List<FormationLink> all = new List<FormationLink>();
    string[] existing = { "<New>" };
    int selected;
    FormationLink cur = new FormationLink();
    string status = "";
    Vector2 scroll;
    bool macroMode;   // entry-type toolbar state: false = link one unit, true = replace a formation (macro rule)

    // Vanilla formation names (1.0.x archives) offered in the macro-replacement Pick — union'd with whatever
    // PresentationFormationDefinition assets exist in the project, so extracted/authored ones appear too.
    static readonly string[] KnownVanillaFormations =
    {
        "Formation_1", "Formation_Close_5", "Formation_Close_9",
        "Formation_Line_2_1R2C", "Formation_Line_Back_9_2R5C", "Formation_Line_Front_9", "Formation_Line_Spaced_9",
        "Formation_Scatter_Organized_9", "Formation_Scatter_Spaced_5", "Formation_Scatter_Spaced_9",
        "Formation_Arrowhead_Close_9_4R4C", "Formation_Arrowhead_Spaced_9_4R2C",
        "Formation_Diamond_Close_9_5R3C", "Formation_Turtle_9", "Formation_VIP_5",
        "Formation_Wedge_3", "Formation_Wedge_Close_9_2R5C", "Formation_Wedge_Spaced_9",
        "AirStrikeFormation_1", "AirStrikeFormation_2", "AirStrikeFormation_3", "AirStrikeFormation_4",
        "AirStrikeFormation_5", "AirStrikeFormation_6", "AirStrikeFormation_7", "AirStrikeFormation_8",
        "AirStrikeFormation_9", "AirStrikeFormation_10",
    };

    // Shared by both modes: re-extract the layout data from its source asset (after Inspector edits).
    void DrawRereadButton()
    {
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(cur.sourceAsset)))
            if (GUILayout.Button(new GUIContent("Re-read", "Re-extract the layout data from its source asset — press after editing dummies in the Inspector."), GUILayout.Width(70)))
            {
                var srcName = string.IsNullOrEmpty(cur.sourceFormation) ? cur.formation : cur.sourceFormation;
                status = ExtractFormation(cur.sourceAsset, srcName, cur, adoptName: false)
                    ? $"Re-read '{srcName}' from {cur.sourceAsset} ({cur.dummies.Count} dummies)."
                    : $"Could not re-read '{srcName}' from '{cur.sourceAsset}' — pick it again.";
            }
    }

    void OnEnable() => RefreshList();

    void RefreshList()
    {
        all = FormationRegistry.Load();
        var notice = FormationRegistry.TakeNotice(); if (!string.IsNullOrEmpty(notice)) status = notice;   // self-healing is shown, not Console-only
        existing = new[] { "<New>" }.Concat(all.Select(EntryLabel)).ToArray();
    }

    // List/dropdown label. Macro entries read like the unit links do — left side now USES the right side:
    //   «MACRO» Formation_Turtle_9 ⇒ Formation_Turtle_16 (16)
    static string EntryLabel(FormationLink l)
    {
        if (!string.IsNullOrEmpty(l.unit)) return $"{l.unit}  →  {l.formation}";
        var src = string.IsNullOrEmpty(l.sourceFormation) ? $"{l.dummies.Count} dummies" : l.sourceFormation;
        return $"«MACRO» {l.formation}  ⇒  {src} ({l.dummies.Count})";
    }

    void OnSelect()
    {
        cur = selected > 0 && selected <= all.Count
            ? JsonUtility.FromJson<FormationLink>(JsonUtility.ToJson(all[selected - 1]))   // edit a COPY
            : new FormationLink();
        macroMode = selected > 0 && string.IsNullOrEmpty(cur.unit);   // loaded entry decides the mode; <New> defaults to unit link
        status = "";
    }

    void OnGUI()
    {
        // CORRUPT-SOURCE RECOVERY banner (the Factory's, via the shared SingleSourceRegistry engine — 2026-08-20): the
        // fault is PINPOINTED (line/column) and recovery is ONE CLICK, each path validated before it writes and the broken
        // file already preserved timestamped. Saving stays locked until recovered.
        if (FormationRegistry.LastLoadCorrupt)
        {
            EditorGUILayout.HelpBox("FORMATION REGISTRY SOURCE IS CORRUPT — " + FormationRegistry.LastCorruptDetail + "\n" +
                "The broken file is preserved beside the source; Save is locked so nothing can be wiped. Recover:", MessageType.Error);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Restore last deploy", "Copy the deployed file (refreshed on every Save — usually the freshest valid copy) back over the source. Validated before writing; the corrupt file stays preserved."), GUILayout.Width(140)))
                { status = FormationRegistry.RecoverFromArtifact(); RefreshList(); Debug.Log("[Formation] " + status); GUIUtility.ExitGUI(); }
                if (GUILayout.Button(new GUIContent("Restore last commit", "git checkout the source — the last committed version. Validated before accepting; the corrupt file stays preserved."), GUILayout.Width(140)))
                { status = FormationRegistry.RecoverFromGit(); RefreshList(); Debug.Log("[Formation] " + status); GUIUtility.ExitGUI(); }
                if (GUILayout.Button(new GUIContent("Open broken file", "Reveal the source in Explorer to fix the reported line by hand — then reopen or refresh the window."), GUILayout.Width(120)))
                { EditorUtility.RevealInFinder(FormationRegistry.SourcePath); }
            }
        }
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            int sel = EditorGUILayout.Popup("Formation link", selected, existing);
            if (GUILayout.Button("Refresh", GUILayout.Width(70))) RefreshList();
            using (new EditorGUI.DisabledScope(selected <= 0))
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    var entry = selected > 0 && selected <= all.Count ? all[selected - 1] : null;   // key on the SELECTED entry
                    var label = entry == null ? "" : string.IsNullOrEmpty(entry.unit) ? $"the macro replacement of '{entry.formation}'" : $"the formation link for '{entry.unit}'";
                    if (entry != null &&
                        EditorUtility.DisplayDialog("Remove formation entry",
                            $"Remove {label}? Affected units show their vanilla formation again on next launch.",
                            "Remove", "Cancel"))
                    {
                        bool removed = FormationRegistry.Remove(entry);
                        selected = 0; cur = new FormationLink(); RefreshList(); GUI.FocusControl(null);
                        status = removed ? $"Removed {label}." : "Entry was not in the registry — nothing removed.";
                    }
                }
            if (sel != selected) { selected = sel; OnSelect(); GUI.FocusControl(null); }
        }
        EditorGUILayout.Space();

        // ---- entry type: an explicit choice, not an empty-field trick ----
        bool isMacro = string.IsNullOrWhiteSpace(cur.unit) && macroMode;
        int kind = GUILayout.Toolbar(isMacro ? 1 : 0, new[]
        {
            new GUIContent("Link one unit", "Repoint ONE PresentationUnitDefinition at a formation of your choice."),
            new GUIContent("Replace a formation (macro)", "Overwrite a (vanilla) formation's layout in the live database — " +
                "EVERY unit of every era and every mod pack that references that name inherits it. Unit links overrule it per unit."),
        });
        bool newMacro = kind == 1;
        if (newMacro != isMacro)
        {
            macroMode = newMacro;
            if (newMacro) cur.unit = "";     // a macro entry has no unit
            status = "";
        }
        isMacro = newMacro;
        EditorGUILayout.Space(2);

        var formations = GatherFormations();
        var assetLabels = formations.Keys.ToArray();

        if (!isMacro)
        {
            // ---- unit link: Unit + Formation ----
            using (new EditorGUILayout.HorizontalScope())
            {
                cur.unit = EditorGUILayout.TextField(new GUIContent("Unit",
                    "The unit's PresentationUnitDefinition name — e.g. Era5_Common_Riflemen. The plugin repoints this " +
                    "definition's formation reference at load. Type it, or Pick from the definitions found in the project."),
                    cur.unit);
                var units = GatherUnitNames();
                using (new EditorGUI.DisabledScope(units.Length == 0))
                    if (GUILayout.Button(new GUIContent("Pick", units.Length == 0 ? "No PresentationUnitDefinition assets in the project — type the name" : null), GUILayout.Width(70)))
                    {
                        var r = GUILayoutUtility.GetLastRect();
                        new StringDropdown(new AdvancedDropdownState(), units, units, "Units", n =>
                        {
                            cur.unit = n;
                            // PREFILL the Formation with the unit's CURRENT one (read off the definition asset), so
                            // the link starts as a neutral no-op instead of leaving the player wondering what to type.
                            // Any captured layout data is cleared with it — a stale capture under a vanilla formation
                            // name would OVERWRITE that formation in the live database at load.
                            var f = CurrentFormationOf(n);
                            if (!string.IsNullOrEmpty(f))
                            {
                                cur.formation = f;
                                cur.dummies = new List<FormationDummy>();
                                cur.columns0 = new List<int>(); cur.columns1 = new List<int>(); cur.columns2 = new List<int>();
                                cur.columns3 = new List<int>(); cur.columns4 = new List<int>(); cur.columns5 = new List<int>();
                                cur.sourceAsset = ""; cur.sourceFormation = ""; cur.lowSpec = "Formation_1";
                                status = $"Formation prefilled with the unit's current '{f}' (pure repoint — pick a different formation to change the layout).";
                            }
                            Repaint();
                        }).Show(r);
                    }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                cur.formation = EditorGUILayout.TextField(new GUIContent("Formation",
                    "The formation to link. Pick a PresentationFormationDefinition asset from the project to capture its " +
                    "data (injected at runtime), or type the name of a formation that already exists in the game's database " +
                    "(e.g. Formation_Close_9) for a pure repoint with no custom data."), cur.formation);
                using (new EditorGUI.DisabledScope(assetLabels.Length == 0))
                    if (GUILayout.Button(new GUIContent("Pick", assetLabels.Length == 0 ? "No PresentationFormationDefinition assets in the project — type a name for a pure repoint" : null), GUILayout.Width(70)))
                    {
                        var r = GUILayoutUtility.GetLastRect();
                        new StringDropdown(new AdvancedDropdownState(), assetLabels, assetLabels, "Formations", n =>
                        {
                            if (ExtractFormation(formations[n], n, cur)) status = $"Read '{n}' from {formations[n]}.";
                            Repaint();
                        }).Show(r);
                    }
                DrawRereadButton();
            }
        }
        else
        {
            // ---- macro replacement: TARGET (what to replace) + LAYOUT (what to replace it with) ----
            using (new EditorGUILayout.HorizontalScope())
            {
                cur.formation = EditorGUILayout.TextField(new GUIContent("Replace formation",
                    "The formation NAME to overwrite in the live database — normally a vanilla one like " +
                    "Formation_Scatter_Spaced_9. Every unit referencing it (any era, any mod pack) inherits the new layout."),
                    cur.formation);
                var known = KnownVanillaFormations.Union(assetLabels).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
                if (GUILayout.Button("Pick", GUILayout.Width(70)))
                {
                    var r = GUILayoutUtility.GetLastRect();
                    new StringDropdown(new AdvancedDropdownState(), known, known, "Formations to replace", n => { cur.formation = n; Repaint(); }).Show(r);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField(new GUIContent("With layout",
                        "The project asset that provides the NEW layout (dummy count + positions + grids). Pick it — " +
                        "the name stays as-is; only its data is used."),
                        string.IsNullOrEmpty(cur.sourceFormation) ? "<pick a layout asset>" : $"{cur.sourceFormation} ({cur.dummies.Count} dummies)");
                using (new EditorGUI.DisabledScope(assetLabels.Length == 0))
                    if (GUILayout.Button(new GUIContent("Pick", assetLabels.Length == 0 ? "No PresentationFormationDefinition assets in the project — extract/author one first" : null), GUILayout.Width(70)))
                    {
                        var r = GUILayoutUtility.GetLastRect();
                        new StringDropdown(new AdvancedDropdownState(), assetLabels, assetLabels, "Layout source", n =>
                        {
                            if (ExtractFormation(formations[n], n, cur, adoptName: false)) status = $"Layout read from '{n}' ({cur.dummies.Count} dummies).";
                            Repaint();
                        }).Show(r);
                    }
                DrawRereadButton();
            }
            if (!string.IsNullOrEmpty(cur.formation) && cur.dummies.Count > 0)
                EditorGUILayout.HelpBox($"Every unit referencing '{cur.formation}' will field {cur.dummies.Count} models " +
                    $"(layout from '{cur.sourceFormation}'). Unit links overrule this per unit. Jitter/scale are per-unit " +
                    "knobs and don't apply to macro replacements.", MessageType.Info);
        }

        // ---- packing: override the unit's random per-model jitter (tighter block, no rebuild) ----
        using (new EditorGUI.DisabledScope(isMacro))
        using (new EditorGUILayout.HorizontalScope())
        {
            bool over = cur.dummyOffset >= 0f;
            bool newOver = EditorGUILayout.ToggleLeft(new GUIContent("Override packing jitter",
                "The game scatters each model by a small random offset (the unit's DummyOffsetPosition), so formations read " +
                "loose. Tick to override it — 0 sits models perfectly on the dummy grid, a small value packs them tightly. " +
                "RUNTIME-ONLY: Save + relaunch, no rebuild."), over, GUILayout.Width(180));
            if (newOver != over) cur.dummyOffset = newOver ? 0.05f : -1f;
            if (newOver)
                cur.dummyOffset = EditorGUILayout.Slider(Mathf.Max(0f, cur.dummyOffset), 0f, 0.5f);
        }

        // ---- scale: whole-formation size multiplier (models + spacing shrink/grow together) ----
        using (new EditorGUI.DisabledScope(isMacro))
        using (new EditorGUILayout.HorizontalScope())
        {
            bool sOver = cur.scale > 0f;
            bool sNew = EditorGUILayout.ToggleLeft(new GUIContent("Formation scale",
                "EXPERIMENTAL: uniform scale for this unit's formation — the MODELS (pawn root localScale) and the dummy " +
                "SPACING shrink or grow together. ⚠ HUMAN units break: equipment (helmets/weapons) are attachment FRAGMENTS " +
                "whose GPU record has no scale channel — bodies shrink, gear vanishes. Use only on vehicles/creatures/custom " +
                "models (pure skinned mesh). RUNTIME-ONLY: Save + relaunch. Untick = vanilla."), sOver, GUILayout.Width(180));
            if (sNew != sOver) cur.scale = sNew ? 1f : -1f;   // enable at 1 = neutral; dial down from there
            if (sNew)
                cur.scale = EditorGUILayout.Slider(Mathf.Clamp(cur.scale, 0.2f, 2f), 0.2f, 2f);
        }
        if (cur.scale > 0f && !isMacro)
        {
            int mode = cur.scaleMode == "data" ? 1 : 0;
            int newMode = EditorGUILayout.Popup(new GUIContent("Scale mode",
                "Transform: pawn root localScale — simple, models+spacing look right; on HUMANS the rigid gear " +
                "(helmet/shield) mis-anchors. Skeleton data: cloned skeleton with scaled binds+meshes — the deep " +
                "path; humans still WIP (procedural bone layers ignore it), promising for vehicles."),
                mode, new[] { "Transform (simple)", "Skeleton data (deep, WIP)" });
            if (newMode != mode) cur.scaleMode = newMode == 1 ? "data" : "transform";
        }
        // ---- footprint: optional decoupled spacing multiplier ----
        using (new EditorGUI.DisabledScope(isMacro))
        using (new EditorGUILayout.HorizontalScope())
        {
            bool fOver = cur.layoutScale > 0f;
            bool fNew = EditorGUILayout.ToggleLeft(new GUIContent("Footprint override",
                "Optional: scale the dummy SPACING independently of model size (e.g. small men on a WIDE skirmish line, " +
                "or full-size men squeezed tighter). Off = spacing follows Formation scale."), fOver, GUILayout.Width(180));
            if (fNew != fOver) cur.layoutScale = fNew ? (cur.scale > 0f ? cur.scale : 1f) : -1f;   // enable at current/neutral
            if (fNew)
                cur.layoutScale = EditorGUILayout.Slider(Mathf.Clamp(cur.layoutScale, 0.2f, 2f), 0.2f, 2f);
        }

        // ---- turn ease (unit links only): eased facing + attack hold — the per-UNIT route to the battle-turn
        // feature, and the ONLY route for VANILLA units (our own models set it in the Model Factory instead).
        // Runtime reads `turnRate` straight off this link; a link may even be turn-ease-only (no formation data). ----
        using (new EditorGUI.DisabledScope(isMacro))
        using (new EditorGUILayout.HorizontalScope())
        {
            bool tOver = cur.turnRate > 0f;
            bool tNew = EditorGUILayout.ToggleLeft(new GUIContent("Turn ease",
                "Smooth the engine's instant facing SNAP for THIS unit — vanilla units included: it TURNS toward a new " +
                "heading (move orders and attacks) at the set rate (deg/s) instead of teleporting, and its map bombard " +
                "WAITS for the pivot — muzzle flash, shot sound, shell and recoil all hold until the barrel faces the " +
                "target. 180 = a 90° turn in half a second; lower is more majestic. RUNTIME-ONLY: Save + relaunch, no " +
                "rebuild. Untick = vanilla snap. See docs/Turn-Ease.md."), tOver, GUILayout.Width(180));
            if (tNew != tOver) cur.turnRate = tNew ? 180f : 0f;
            if (tNew)
                cur.turnRate = EditorGUILayout.Slider(Mathf.Clamp(cur.turnRate, 30f, 720f), 30f, 720f);
        }
        // ---- pivot in place (unit links only): per-UNIT override of HAF's 90-degree turn-first rule — vanilla units
        // and HAF models alike (the runtime keys it by the unit's descriptor). Independent of the Turn ease row above:
        // a unit eased by its CATEGORY default (land/turret/ship in haf_turnease.txt) may carry only this. ----
        using (new EditorGUI.DisabledScope(isMacro))
        using (new EditorGUILayout.HorizontalScope())
        {
            int mode = cur.turnPivot > 0f ? 1 : cur.turnPivot < 0f ? 2 : 0;
            int newMode = EditorGUILayout.Popup(new GUIContent("Pivot in place",
                "Turn on the spot FIRST, then move off, when a heading change is at least the set angle — the rendered " +
                "unit parks on its tile while it swings round, then catches up. Default = HAF's global rule " +
                "(haf_turnease.txt `pivot=`, 90°; ground and naval only, never helicopters/planes). Custom = this " +
                "unit's own angle: 1° makes it ALWAYS turn fully before moving (a tank, a towed gun), 150° only on " +
                "near-reversals — it can even opt a helicopter in. Never = turn while rolling. Needs turn ease (the row " +
                "above or a category default). RUNTIME-ONLY: Save + relaunch. See docs/Turn-Ease.md."),
                mode, new[] { "Default (global rule)", "Custom angle", "Never" }, GUILayout.Width(320));
            if (newMode != mode) cur.turnPivot = newMode == 1 ? 90f : newMode == 2 ? -1f : 0f;
            if (newMode == 1)
                cur.turnPivot = EditorGUILayout.Slider(Mathf.Clamp(cur.turnPivot, 1f, 180f), 1f, 180f);   // > 180 is unreachable
        }

        // ---- formation by size (unit links only): era-ageing swaps, moved here from the Global Era Lab ----
        if (!isMacro)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Formation by size (era ageing)", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Only fires for units with a RESIZE LAB rule: as the Global Era Lab shrinks the unit, the first row whose " +
                "threshold is >= the unit's effective scale (rule × era cell) wins and the unit re-forms live. Above every " +
                "threshold the unit keeps the formation configured above (or its own). Rows are sorted on Save.", MessageType.None);
            int rmRow = -1;
            for (int i = 0; i < cur.sizeFormations.Count; i++)
            {
                var t = cur.sizeFormations[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Scale up to", GUILayout.Width(75));
                    t.threshold = Mathf.Max(0.01f, EditorGUILayout.FloatField(t.threshold, GUILayout.Width(60)));
                    t.formation = EditorGUILayout.TextField(t.formation, GUILayout.MinWidth(160));
                    if (GUILayout.Button("Pick", GUILayout.Width(44)))
                    {
                        int idx = i;
                        var rect = GUILayoutUtility.GetLastRect();
                        var opts = GatherFormations().Keys.Concat(all.Where(l => !string.IsNullOrWhiteSpace(l.formation)).Select(l => l.formation))
                                       .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToArray();
                        new StringDropdown(new AdvancedDropdownState(), opts, opts, "Formations",
                            n => { cur.sizeFormations[idx].formation = n; Repaint(); }).Show(rect);
                    }
                    if (GUILayout.Button("X", GUILayout.Width(22))) rmRow = i;
                }
            }
            if (rmRow >= 0) cur.sizeFormations.RemoveAt(rmRow);
            if (GUILayout.Button("+ Add size threshold", GUILayout.Width(150)))
                cur.sizeFormations.Add(new SizeFormation { threshold = cur.sizeFormations.Count > 0 ? cur.sizeFormations.Max(t => t.threshold) * 2f : 0.3f });
        }

        // ---- captured data summary + validation ----
        EditorGUILayout.Space();
        string error = Validate(cur);
        if (cur.dummies.Count > 0)
        {
            EditorGUILayout.LabelField("Captured formation data", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField($"Dummies (pawns at full health): {cur.dummies.Count}");
            EditorGUILayout.LabelField($"Rows per orientation: {string.Join(" / ", Enumerable.Range(0, 6).Select(i => Columns(cur, i).Count.ToString()).ToArray())}");
            EditorGUILayout.LabelField($"Low-spec fallback: {(string.IsNullOrEmpty(cur.lowSpec) ? "Formation_1 (default)" : cur.lowSpec)}");
            if (error == null && cur.dummies.Count > 10)
                EditorGUILayout.HelpBox($"{cur.dummies.Count} dummies exceeds the vanilla Formation3D dummy pool (vanilla's biggest formation has 10). " +
                                        "The plugin grows the pool automatically before it is cloned — just make sure you run a plugin build that has the " +
                                        "Formation axis (check the log for '[Formation] Formation3DPrefab dummy pool extended').", MessageType.Info);
        }
        else if (!string.IsNullOrEmpty(cur.formation))
        {
            if (isMacro)
                EditorGUILayout.HelpBox("A MACRO replacement needs formation DATA (a macro entry with no data would replace nothing). " +
                                        "Pick the asset that carries the layout you want, then set the Formation field to the vanilla name to replace.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox("No formation data captured — this saves as a PURE REPOINT: the plugin links the unit to a formation " +
                                        "already in the game's database with this exact name. For a custom formation, Pick its project asset instead.", MessageType.Info);
        }
        if (error != null)
            EditorGUILayout.HelpBox("Formation data INVALID — " + error + "\nThe game would throw during load (the misleading \"mismatched mods\" " +
                                    "dialog). Fix the asset in the Inspector, then Re-read.", MessageType.Error);

        // ---- save ----
        EditorGUILayout.Space();
        bool canSave = !string.IsNullOrWhiteSpace(cur.formation) && error == null
                       && (!isMacro || cur.dummies.Count > 0);   // a macro replacement without data replaces nothing
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!canSave))
                if (GUILayout.Button(isMacro ? "Save MACRO replacement" : "Save link", GUILayout.Height(34)))
                {
                    cur.unit = cur.unit.Trim(); cur.formation = cur.formation.Trim();
                    // size thresholds: drop empty rows, store ASCENDING so the runtime takes the first match
                    cur.sizeFormations = (cur.sizeFormations ?? new List<SizeFormation>())
                        .Where(t => !string.IsNullOrWhiteSpace(t.formation)).OrderBy(t => t.threshold).ToList();
                    // ALWAYS re-read the source asset first so edits made in the Inspector since Pick are captured — the
                    // window only DISPLAYS the dummy data (the asset is the single source of truth), so a Save that wrote
                    // the Pick-time cache silently shipped stale positions/coords ("as if the save had no effect").
                    // Re-read by the SOURCE sub-asset name: for a macro replacement the Formation field holds the vanilla
                    // TARGET name, which doesn't exist in the source asset.
                    string staleWarn = null;
                    if (!string.IsNullOrEmpty(cur.sourceAsset))
                    {
                        var srcName = string.IsNullOrEmpty(cur.sourceFormation) ? cur.formation : cur.sourceFormation;
                        if (!ExtractFormation(cur.sourceAsset, srcName, cur, adoptName: false))
                            staleWarn = $" (⚠ could not re-read '{srcName}' from its asset — saved the last-read data; re-Pick it)";
                    }
                    string reErr = Validate(cur);   // re-validate the freshly-read data before it can be written
                    if (reErr != null)
                    {
                        status = $"NOT saved — re-read gave invalid data: {reErr}";
                    }
                    else
                    {
                        bool saved = FormationRegistry.Upsert(cur);
                        RefreshList();
                        var label = EntryLabel(cur);
                        selected = Array.IndexOf(existing, label); if (selected < 0) selected = 0;
                        status = saved
                            ? (isMacro
                                ? $"Saved MACRO replacement: '{cur.formation}' ⇒ {cur.dummies.Count} pawns for EVERY unit referencing it." + (staleWarn ?? "")
                                : $"Saved: '{cur.unit}' → '{cur.formation}'" + (cur.dummies.Count > 0 ? $" ({cur.dummies.Count} pawns at full health)." : " (pure repoint).") + (staleWarn ?? ""))
                            : "REGISTRY SAVE FAILED (see Console).";
                    }
                }
            if (GUILayout.Button("Reset", GUILayout.Height(34), GUILayout.Width(72))) { cur = new FormationLink(); selected = 0; status = ""; GUI.FocusControl(null); }
        }
        if (!canSave && error == null)
            EditorGUILayout.HelpBox(isMacro
                ? "MACRO replacement: set Formation (the vanilla name to replace) and Pick an asset for the layout data — or set a Unit for a per-unit link."
                : "Set Unit and Formation to save.", MessageType.Warning);
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);

        DrawTurnEaseDefaults();

        EditorGUILayout.HelpBox(
            "Workflow: extract a vanilla formation into Assets/Databases/UnitFormation (Database Browser), duplicate + edit it in the " +
            "Inspector (the SDK preview shows the layout), then Pick it here, Pick the unit, Save. No mod rebuild needed — the plugin " +
            "reads the registry at game launch.\n" +
            "• Pawn count on the map scales with health: ceil(health% × dummies).\n" +
            "• The hidden per-orientation grids are captured and validated here — the historical crash cause for hand-made formations.\n" +
            "• Plugin prerequisite: [Formations] FormationOverride = true (default ON).\n" +
            "Registry source (edit this, git-tracked): " + FormationRegistry.SourcePath + "\nDeployed artifact (what the game reads, regenerated on Save): " + FormationRegistry.RegistryPath, MessageType.None);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Open config folder", GUILayout.Width(150)))
                EditorUtility.RevealInFinder(System.IO.File.Exists(FormationRegistry.RegistryPath)
                    ? FormationRegistry.RegistryPath : ModelRegistry.ConfigDir);
            GUILayout.Label("↑ haf_formations.json + the plugin .cfg", EditorStyles.miniLabel);
        }
        EditorGUILayout.EndScrollView();
    }

    static List<int> Columns(FormationLink l, int i) =>
        i == 0 ? l.columns0 : i == 1 ? l.columns1 : i == 2 ? l.columns2 : i == 3 ? l.columns3 : i == 4 ? l.columns4 : l.columns5;

    // Same rules the game's BuildDummiesGrid enforces by crashing (and the plugin re-checks before injecting). Null = valid.
    static string Validate(FormationLink l)
    {
        int n = l.dummies.Count;
        if (n == 0) return null;   // pure repoint
        foreach (var d in l.dummies)
            if (d.coords.Count != 6) return $"a dummy has {d.coords.Count} orientation coordinates (need 6)";
        for (int i = 0; i < 6; i++)
        {
            var cols = Columns(l, i);
            if (cols == null || cols.Count == 0) return $"ColumnsCountPerRow{i} is empty (the hidden grid arrays weren't authored — " +
                "duplicate an existing formation asset instead of creating one from scratch, or fill them via the Inspector's Debug mode)";
            int total = 0; foreach (var c in cols) total += c;
            if (total != n) return $"ColumnsCountPerRow{i} cells ({total}) != dummy count ({n})";
            var seen = new HashSet<int>();
            foreach (var d in l.dummies)
            {
                var c = d.coords[i];
                if (c.x < 0 || c.x >= cols.Count) return $"orientation {i}: row {c.x} out of range (rows={cols.Count})";
                if (c.y < 0 || c.y >= cols[c.x]) return $"orientation {i}: column {c.y} out of range (row {c.x} has {cols[c.x]} columns)";
                if (!seen.Add(c.x * 4096 + c.y)) return $"orientation {i}: duplicate cell ({c.x},{c.y})";
            }
        }
        return null;
    }

    // Read the formation's data off the asset via SerializedObject — no compile-time Amplitude type references,
    // and it reaches the [HideInInspector] ColumnsCountPerRow0..5 the Inspector never shows.
    static bool ExtractFormation(string assetPath, string formationName, FormationLink into, bool adoptName = true)
    {
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(assetPath))
        {
            if (o == null || o.GetType().Name != "PresentationFormationDefinition" || o.name != formationName) continue;
            var so = new SerializedObject(o);
            if (adoptName) into.formation = formationName;   // Pick adopts the asset's name; re-reads keep the (possibly retargeted) Formation field
            into.sourceFormation = formationName;
            into.sourceAsset = assetPath;
            into.lowSpec = so.FindProperty("LowSpecFormationDefinition")?.FindPropertyRelative("serializableElementName")?.stringValue ?? "";
            into.dummies = new List<FormationDummy>();
            var dArr = so.FindProperty("Dummies");
            for (int i = 0; i < (dArr != null ? dArr.arraySize : 0); i++)
            {
                var el = dArr.GetArrayElementAtIndex(i);
                var fd = new FormationDummy { position = el.FindPropertyRelative("Position")?.vector3Value ?? Vector3.zero };
                var cp = el.FindPropertyRelative("CoordinatePerDirection");
                for (int j = 0; j < (cp != null ? cp.arraySize : 0); j++)
                {
                    var v = cp.GetArrayElementAtIndex(j).vector2IntValue;
                    fd.coords.Add(new GridCell { x = v.x, y = v.y });
                }
                into.dummies.Add(fd);
            }
            for (int i = 0; i < 6; i++)
            {
                var list = new List<int>();
                var p = so.FindProperty("ColumnsCountPerRow" + i);
                for (int j = 0; j < (p != null ? p.arraySize : 0); j++) list.Add(p.GetArrayElementAtIndex(j).intValue);
                switch (i)
                {
                    case 0: into.columns0 = list; break;
                    case 1: into.columns1 = list; break;
                    case 2: into.columns2 = list; break;
                    case 3: into.columns3 = list; break;
                    case 4: into.columns4 = list; break;
                    case 5: into.columns5 = list; break;
                }
            }
            return true;
        }
        return false;
    }

    // ---- TURN EASE DEFAULTS (docs/Turn-Ease.md): the per-TYPE global rates the plugin reads live off
    // BepInEx/config/haf_turnease.txt (~1/s poll — edits reach a RUNNING game within a second, no rebuild).
    // This window is HAF's per-unit config surface, so the type-level defaults live here too; the per-link
    // Turn ease row above overrides its unit, and a model's own Factory value overrides everything.
    // The editor OWNS the file format: Save rewrites the canonical commented template with these values. ----
    [SerializeField] float teHuman, teLand = 180f, teTurret = 180f, teHover = 180f, teShip = 90f, teRate, teBank;
    [SerializeField] float teHoverBank = 6f, teShipBank = 3f;   // per-category bank: a chopper banks, a ship heels
    [SerializeField] bool teLoaded;
    static string TurnEasePath => System.IO.Path.Combine(ModelRegistry.ConfigDir, "haf_turnease.txt");

    void LoadTurnEase()
    {
        teLoaded = true;
        try
        {
            if (!System.IO.File.Exists(TurnEasePath)) return;   // keep the seeded defaults
            foreach (var raw in System.IO.File.ReadAllLines(TurnEasePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.Split('=');
                if (eq.Length != 2 || !float.TryParse(eq[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
                switch (eq[0].Trim().ToLowerInvariant())
                {
                    case "human": teHuman = v; break;
                    case "land": teLand = v; break;
                    case "turret": teTurret = v; break;
                    case "hover": case "air": teHover = v; break;   // "air" = legacy alias — planes are always excluded
                    case "hoverbank": teHoverBank = v; break;
                    case "shipbank": teShipBank = v; break;
                    case "ship": teShip = v; break;
                    case "rate": teRate = v; break;
                    case "bank": teBank = v; break;
                }
            }
        }
        catch (Exception e) { Debug.LogWarning("[Formation] haf_turnease.txt read: " + e.Message); }
    }

    void SaveTurnEase()
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var txt =
            "# Turn ease — smooth the facing SNAP (move orders AND map attacks) into an eased turn. docs/Turn-Ease.md.\n" +
            "# Written by the Formation Override window (Turn ease defaults); polled ~1x/s by HAF — edits apply to a\n" +
            "# running game within a second, no rebuild.\n" +
            "#\n" +
            "# PRECEDENCE: a model's own Factory value > its CATEGORY default below > the global `rate`.\n" +
            "#\n" +
            "# CATEGORY DEFAULTS (deg/s, 0 = that type keeps the vanilla snap). Classified by CHARACTERISTIC,\n" +
            "# applies to HAF models AND vanilla units:\n" +
            "#   human  = infantry, cavalry, chariots, animals (organic capability profiles)\n" +
            "#   land   = turretless land vehicles (towed guns, assault guns, carts)\n" +
            "#   turret = land vehicles WITH a traversing turret (azimuth transforms, learned from live pawns)\n" +
            "#   hover  = units with the game's Hover ability — ignores terrain (helicopters, hovercraft)\n" +
            "#   ship   = boats and ships\n" +
            "# Fixed-wing PLANES and missiles are always excluded — the engine flies them on natural curved paths.\n" +
            "# hoverbank / shipbank = max roll INTO the turn per category, degrees — a chopper banks, a ship heels\n" +
            "# rate   = last-resort global for anything uncategorized (0 = off; never touches planes)\n" +
            "# bank   = legacy/fallback bank for rate-eased models (per-model bank wins over everything)\n" +
            "human=" + teHuman.ToString("0.#", ic) + "\n" +
            "land=" + teLand.ToString("0.#", ic) + "\n" +
            "turret=" + teTurret.ToString("0.#", ic) + "\n" +
            "hover=" + teHover.ToString("0.#", ic) + "\n" +
            "hoverbank=" + teHoverBank.ToString("0.#", ic) + "\n" +
            "shipbank=" + teShipBank.ToString("0.#", ic) + "\n" +
            "ship=" + teShip.ToString("0.#", ic) + "\n" +
            "rate=" + teRate.ToString("0.#", ic) + "\n" +
            "bank=" + teBank.ToString("0.#", ic) + "\n";
        try
        {
            System.IO.Directory.CreateDirectory(ModelRegistry.ConfigDir);
            System.IO.File.WriteAllText(TurnEasePath, txt);
            status = "Turn ease defaults saved — a running game picks them up within a second.";
        }
        catch (Exception e) { status = "Turn ease defaults NOT saved: " + e.Message; Debug.LogError("[Formation] " + status); }
    }

    void DrawTurnEaseDefaults()
    {
        if (!teLoaded) LoadTurnEase();
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Turn ease defaults — by unit type (all units, live)", EditorStyles.miniBoldLabel);
        EditorGUILayout.HelpBox(
            "Global defaults per unit TYPE, degrees/second (0 = that type keeps the vanilla snap). Applies to HAF models " +
            "AND vanilla units. Precedence: a model's own Factory value > these category defaults > the Fallback rate. " +
            "The per-link Turn ease row above overrides just its unit. Saved to haf_turnease.txt — a running game reacts " +
            "within a second.", MessageType.None);
        teHuman = EditorGUILayout.Slider(new GUIContent("Human", "Infantry, cavalry, chariots, animals. These pivot naturally in vanilla — 0 (off) is a good default."), teHuman, 0f, 720f);
        teLand = EditorGUILayout.Slider(new GUIContent("Land vehicle (no turret)", "Towed guns, assault guns, carts — the whole hull must pivot to aim."), teLand, 0f, 720f);
        teTurret = EditorGUILayout.Slider(new GUIContent("Land vehicle (turret)", "Tanks and other vehicles with a traversing turret — detected from live pawns (needs a different value than the turretless rate to matter)."), teTurret, 0f, 720f);
        teHover = EditorGUILayout.Slider(new GUIContent("Hover", "Units with the game's Hover ability — they ignore terrain: helicopters, hovercraft. Fixed-wing PLANES are always excluded (the engine flies them on natural curved paths)."), teHover, 0f, 720f);
        teHoverBank = EditorGUILayout.Slider(new GUIContent("   Hover bank (deg)", "Roll INTO the turn for Hover units — a helicopter leaning through its sweep. A model's own Factory bank wins."), teHoverBank, -30f, 30f);
        teShip = EditorGUILayout.Slider(new GUIContent("Ship", "Boats and ships — low values read as stately."), teShip, 0f, 720f);
        teShipBank = EditorGUILayout.Slider(new GUIContent("   Ship heel (deg)", "Roll INTO the turn for ships — a subtle heel (2-4) sells the water; 0 = flat. A model's own Factory bank wins."), teShipBank, -30f, 30f);
        teRate = EditorGUILayout.Slider(new GUIContent("   Fallback rate (uncategorized)", "Last-resort global for anything without a category; 0 = off. Prefer the categories above."), teRate, 0f, 720f);
        teBank = EditorGUILayout.Slider(new GUIContent("   Fallback bank (deg)", "Bank for models eased by the Fallback rate only (categories use their own bank above)."), teBank, -30f, 30f);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Save turn ease defaults", GUILayout.Width(180))) SaveTurnEase();
            if (GUILayout.Button("Reload", GUILayout.Width(70))) LoadTurnEase();
        }
    }

    // The formation a unit CURRENTLY references, read off its PresentationUnitDefinition asset — the same
    // "PresentationFormationDefinition" element reference the plugin repoints at load, via the same
    // serializableElementName route ExtractFormation uses for the low-spec reference. "" = not found.
    static string CurrentFormationOf(string unitName)
    {
        foreach (var guid in AssetDatabase.FindAssets("PresentationUnitDefinition"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".asset")) continue;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && o.GetType().Name == "PresentationUnitDefinition" && o.name == unitName)
                    return new SerializedObject(o).FindProperty("PresentationFormationDefinition")
                               ?.FindPropertyRelative("serializableElementName")?.stringValue ?? "";
        }
        return "";
    }

    // Every PresentationUnitDefinition name found in the project databases (sub-assets of the collection assets).
    // NOT cached: the user extracts new definitions from the archives while the window is open.
    static string[] GatherUnitNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in AssetDatabase.FindAssets("PresentationUnitDefinition"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".asset")) continue;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && o.GetType().Name == "PresentationUnitDefinition" && !string.IsNullOrEmpty(o.name))
                    names.Add(o.name);
        }
        return names.ToArray();
    }

    // Every PresentationFormationDefinition in the project (e.g. the user's Assets/Databases/UnitFormation extraction),
    // name -> asset path. The collection asset itself (…DefinitionCollection) is excluded by the exact type-name match.
    static Dictionary<string, string> GatherFormations()
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in AssetDatabase.FindAssets("PresentationFormationDefinition"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".asset")) continue;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && o.GetType().Name == "PresentationFormationDefinition" && !string.IsNullOrEmpty(o.name))
                    map[o.name] = path;
        }
        return new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
    }
}
