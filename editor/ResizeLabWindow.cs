// ResizeLabWindow.cs — the RESIZE LAB (2026-07-28, user-designed): runtime unit scaling, NO bake, no assets.
//
// v1 (this window): INDIVIDUAL unit rescaling — rules {match, scale} applied by the plugin to ANY unit whose
// presentation definition name contains `match` (vanilla units included). Save writes the registry; relaunch to
// see it. All matching rules MULTIPLY (a unit-specific correction can ride on top of a broader rule).
//
// HOW THE RUNTIME APPLIES IT (verified in-game 2026-07-29, Bireme x2): two halves, because the game's shaders were
// disassembled and NO instruction scales geometry by a transform (the animation pass writes bone scale as a literal
// 1.0; the draw VS applies scale only to bind-pose offsets):
//   1. GEOMETRY — the unit's mesh vertices are multiplied by s in the live Fx content-layer vertex buffer (once per
//      unit type per session; positions are raw floats in the pawn layer's format), bboxes included.
//   2. PLACEMENT — ObjectSpace.Scale *= s per pawn per frame, which spreads bone positions and bind offsets so the
//      parts stay attached to the grown geometry.
// Consequences: free on the vertex budget (no clone), but the mesh is SHARED — a rule resizes every unit of that
// type. Full write-up: ENCAccessProof/docs/Unit-Size.md.
//
// ERA ANCHORING (WORKING since 2026-07-29, crude PoC): the plugin divides the rule's scale by the CURRENT GAME ERA
// (Sandbox.Timeline.GetGlobalEraIndex() — game-wide, across all empires), so a hull authored x4 for era 1 renders
// x0.8 in era 5: epic in its own age, a toy beside a modern cruiser. Because the runtime re-scales by RATIOS, the
// size also changes LIVE when the era turns. VERIFIED in-game (x4 bireme rule -> x0.8 at era 5).
// Refinement still open: divide trueSize (metres, field already reserved) by a per-era REFERENCE size table, so
// each era's jump is authored rather than an artifact of dividing by the era number.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class ResizeLabWindow : EditorWindow
{
    [MenuItem("Tools/HAF/Resize Lab")]
    public static void Open() => GetWindow<ResizeLabWindow>("Resize Lab");

    Vector2 scroll;
    List<UnitScaleRule> rules;          // working copy of ModelRegistry.UnitScales
    List<ModelDef> models;              // working copy of the model entries (for their runtime `scale`)
    string status = "";
    bool dirty;

    // SCALABLE pawn names only (2026-07-28, user rule): the pick list must not even OFFER human-presentation
    // definitions — the runtime skips them anyway, but offering them invites the disappointment the exclusion
    // exists to prevent. Same structural check as the plugin: PresentationPawnDefinition.AnimationCapabilityProfile
    // against the human-carrying family (Human, mounted fighter/driver, servant, chariot crew, Mount, Chariot).
    static string[] scalableCache;
    static readonly HashSet<int> HumanProfiles = new HashSet<int> { 1, 2, 4, 6, 9, 10, 12, 13, 16 };
    static string[] GatherScalablePawnNames()
    {
        if (scalableCache != null) return scalableCache;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in AssetDatabase.FindAssets("PresentationPawnDefinition"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".asset")) continue;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o == null || o.GetType().Name != "PresentationPawnDefinition" || string.IsNullOrEmpty(o.name)) continue;
                var prop = new SerializedObject(o).FindProperty("AnimationCapabilityProfile");
                if (prop != null && HumanProfiles.Contains(prop.intValue)) continue;
                names.Add(o.name);
            }
        }
        scalableCache = names.ToArray();
        return scalableCache;
    }

    void OnEnable() { Reload(); }

    void Reload()
    {
        models = ModelRegistry.Load();                       // also (re)fills ModelRegistry.UnitScales
        rules = ModelRegistry.UnitScales.Select(r => new UnitScaleRule { match = r.match, scale = r.scale, era = r.era, trueSize = r.trueSize, note = r.note }).ToList();
        dirty = false;
        status = $"Loaded {rules.Count} rule(s), {models.Count} model entr{(models.Count == 1 ? "y" : "ies")}.";
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Resize Lab", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Runtime unit scaling — NO bake, no assets touched. Rules apply to ANY unit (vanilla included) whose " +
            "pawn definition name CONTAINS the match text; our own model entries have their own multiplier below. " +
            "All matching rules MULTIPLY. Save writes the registry; RELAUNCH the game to see changes.\n" +
            "HUMAN presentation (soldiers, riders, mounts, chariot crews) is EXCLUDED automatically — scaled humans " +
            "read as absurd. Animals (cave bears!), ships, planes and vehicles scale freely.\n" +
            "SCALE = the unit's size IN ITS OWN ERA. How it then ages as the world advances is authored in the " +
            "GLOBAL ERA LAB grid (Tools ▸ HAF ▸ Global Era Lab) — untouched, that grid is all 1.0, so your scale " +
            "applies in every era. The unit's era is read from its name (Era4_… → 4); set Era to override it.", MessageType.None);

        scroll = EditorGUILayout.BeginScrollView(scroll);

        // ── Section 1: per-unit rules (vanilla or any pawn) ─────────────────────────────────────────────
        GUILayout.Label("Unit scale rules (any unit, by pawn-name match)", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Pawn definition contains", EditorStyles.miniBoldLabel, GUILayout.MinWidth(220));
            EditorGUILayout.LabelField("", GUILayout.Width(44));
            EditorGUILayout.LabelField("Scale in its own era", EditorStyles.miniBoldLabel, GUILayout.MinWidth(160));
            EditorGUILayout.LabelField("Era", EditorStyles.miniBoldLabel, GUILayout.Width(30));
            EditorGUILayout.LabelField("Note", EditorStyles.miniBoldLabel, GUILayout.Width(120));
        }
        int removeAt = -1;
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            using (new EditorGUILayout.HorizontalScope())
            {
                var nm = EditorGUILayout.TextField(r.match, GUILayout.MinWidth(220));
                if (nm != r.match) { r.match = nm; dirty = true; }
                if (GUILayout.Button("Pick", GUILayout.Width(44)))
                {
                    int idx = i;   // capture
                    var rect = GUILayoutUtility.GetLastRect();
                    new PawnDropdown(new AdvancedDropdownState(), GatherScalablePawnNames(), n =>
                    { rules[idx].match = n; dirty = true; Repaint(); }).Show(rect);
                }
                float sc = EditorGUILayout.Slider(r.scale, 0.1f, 10f, GUILayout.MinWidth(160));
                if (!Mathf.Approximately(sc, r.scale)) { r.scale = sc; dirty = true; }
                // The unit's OWN era — what the Global Era Lab grid ages it from. 0 = read it off the name.
                int era = EditorGUILayout.IntField(new GUIContent("", "The unit's own era (row in the Global Era Lab grid). 0 = auto-detect from the name, e.g. Era4_… → 4."), r.era, GUILayout.Width(30));
                if (era < 0) era = 0;
                if (era > 6) era = 6;
                if (era != r.era) { r.era = era; dirty = true; }
                var note = EditorGUILayout.TextField(r.note, GUILayout.Width(120));
                if (note != r.note) { r.note = note; dirty = true; }
                if (GUILayout.Button("X", GUILayout.Width(22))) removeAt = i;
            }
        }
        if (removeAt >= 0) { rules.RemoveAt(removeAt); dirty = true; }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("+ Add rule", GUILayout.Width(90)))
            { rules.Add(new UnitScaleRule()); dirty = true; }
            GUILayout.FlexibleSpace();
        }

        EditorGUILayout.Space();

        // ── Section 2: our own model entries (runtime multiplier on top of their baked size) ────────────
        GUILayout.Label("Custom model entries — PLACEMENT ONLY (scale your model with the Factory's Size instead)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "This multiplier writes the pawn's ObjectSpace.Scale, which the GPU honours for part PLACEMENT only — it " +
            "spreads a multi-part model apart without resizing the geometry (shader-verified 2026-07-29). To resize " +
            "your own model, re-bake it with the Factory's Size field.", MessageType.Warning);
        foreach (var m in models)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(m.resourceName, GUILayout.MinWidth(200));
                float sc = EditorGUILayout.Slider(m.scale <= 0f ? 1f : m.scale, 0.1f, 10f);
                if (!Mathf.Approximately(sc, m.scale)) { m.scale = sc; dirty = true; }
                if (!Mathf.Approximately(m.scale, 1f) && GUILayout.Button("reset", GUILayout.Width(46)))
                { m.scale = 1f; dirty = true; }
            }
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!dirty))
                if (GUILayout.Button("Save (runtime-only — relaunch the game)", GUILayout.Height(30)))
                {
                    // Merge our per-model `scale` edits onto the CURRENT on-disk models (re-read fresh) so a bake or
                    // edit made in another window isn't reverted by this window's stale snapshot. Set the UnitScales
                    // static AFTER Load (Load repopulates it from disk), then save the merged list.
                    var fresh = ModelRegistry.Load();
                    foreach (var f in fresh) { var mine = models.FirstOrDefault(x => x.resourceName == f.resourceName); if (mine != null) f.scale = mine.scale; }
                    models = fresh;
                    ModelRegistry.UnitScales = rules.Where(r => !string.IsNullOrWhiteSpace(r.match)).ToList();
                    bool ok = ModelRegistry.Save(models);
                    status = ok ? $"Saved {ModelRegistry.UnitScales.Count} rule(s) + {models.Count} entr{(models.Count == 1 ? "y" : "ies")}. Relaunch the game to apply."
                                : "Save FAILED — see the Console (registry locked or corrupt).";
                    dirty = !ok;
                }
            if (GUILayout.Button("Reload", GUILayout.Width(70), GUILayout.Height(30))) Reload();
        }
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
    }
}
