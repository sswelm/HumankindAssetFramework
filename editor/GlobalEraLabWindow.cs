// GlobalEraLabWindow.cs — the GLOBAL ERA LAB (2026-07-29, user-designed): modifiers applied to scaled units as the
// world ages, authored as a GRID of (unit era) x (current era).
//
// The first modifier is RESCALE. A unit is authored ONCE in the Resize Lab ("how big is this ship, in its own
// age"), and this grid says how it should read later on: an Ancient trireme is epic in the Ancient era, a curiosity
// beside Industrial battleships, and a toy next to a Contemporary carrier. One value per era can't express that,
// because how much a unit shrinks depends on BOTH how old it is and how far the world has moved — hence a grid.
//
// SCOPE (user rules): the grid only ever multiplies units that ALREADY have a Resize Lab rule — it never resizes
// anything the modder hasn't opted in — and for now it applies to NAVAL units only. Land and air keep their
// authored size in every era, which also keeps the cave-bear case intact (an animal is a land unit: still
// scalable via the Resize Lab, just not aged by this grid).
//
// WHY 5x5 (user): the trivial cases don't need cells. A unit from the LAST era has no later age to recede into
// (its row would be all 1.0), and in the FIRST era nothing has aged yet (that column would be all 1.0). So rows are
// unit eras 1..5 and columns are current eras 2..6 — every cell in the grid is a case that can genuinely differ.
// Anything outside the grid (a unit in its own era or earlier, an era-6 unit, the Neolithic) is 1.0 = unchanged.
//
// DEFAULTS ARE 1.0 (user rule): the Lab ships neutral and the runtime invents nothing. Every number that changes a
// unit's size is authored here, so an untouched grid means units render exactly at their Resize Lab size.
//
// The world's era comes from what has actually been BUILT (the max era among existing units of the unit's own
// domain), combined with Humankind's global era as a floor — see ENCAccessProof/docs/Unit-Size.md.
// Engine index: 0 = Neolithic, 1 = Ancient ... 6 = Contemporary (verified in-game).
//
// SECOND TABLE — FORMATION BY SIZE (2026-07-29, user-designed): as an aged unit shrinks, swap its formation, so a
// tiny lone hull becomes a squadron of small hulls. Rows are {threshold, formation} with INCREASING thresholds and
// the first row whose threshold >= the effective scale wins. Editor side only for now: the runtime still needs the
// formation swap + a re-form (the Formation axis already owns both moves — repoint the definition's formation
// reference, then re-run the game's UpdatePawns on live units).

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;   // AdvancedDropdownState, for the formation Pick
using UnityEngine;

public class GlobalEraLabWindow : EditorWindow
{
    [MenuItem("Tools/HAF/Global Era Lab")]
    public static void Open() => GetWindow<GlobalEraLabWindow>("Global Era Lab");

    public const int FirstUnitEra = 1, LastUnitEra = 5;      // rows    — an era-6 unit has no later era
    public const int FirstNowEra = 2, LastNowEra = 6;        // columns — in era 1 nothing has aged yet

    public static readonly string[] EraNames =
    {
        "Neolithic", "Ancient", "Classical", "Medieval", "Early Modern", "Industrial", "Contemporary",
    };
    static string Short(int era) => era >= 0 && era < EraNames.Length ? EraNames[era].Substring(0, 3) : "?";

    Vector2 scroll;
    List<ModelDef> models;                 // carried through Save (the registry writes models + rules + this grid)
    float[,] grid;                         // [unitEra, nowEra], absolute indices
    List<FormationThreshold> thresholds;    // second table: swap formation as an aged unit shrinks
    string[] formationNames;                // Pick list: ENC formation entries + vanilla names
    bool dirty;
    string status = "";

    // Vanilla formation names worth offering (mirrors the Formation Override window's list), union'd with the
    // formations authored in the ENC formation registry — those are the interesting ones here, since a "3 hulls"
    // naval formation is something you build there and then select in this table.
    static readonly string[] KnownVanillaFormations =
    {
        "Formation_1", "Formation_Close_5", "Formation_Close_9",
        "Formation_Line_2_1R2C", "Formation_Line_Front_9", "Formation_Line_Spaced_9",
        "Formation_Scatter_Organized_9", "Formation_Scatter_Spaced_5", "Formation_Scatter_Spaced_9",
        "Formation_Wedge_3", "Formation_Wedge_Spaced_9", "Formation_VIP_5",
    };

    string[] GatherFormationNames()
    {
        if (formationNames != null) return formationNames;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in KnownVanillaFormations) names.Add(n);
        try
        {
            foreach (var link in FormationRegistry.Load())
                if (link != null && !string.IsNullOrWhiteSpace(link.formation)) names.Add(link.formation);
        }
        catch { }   // the formation registry is optional — vanilla names alone are still useful
        formationNames = names.ToArray();
        return formationNames;
    }

    void OnEnable() { Reload(); }

    void Reload()
    {
        models = ModelRegistry.Load();                       // also (re)fills ModelRegistry.EraGrid
        grid = new float[EraNames.Length, EraNames.Length];
        for (int r = FirstUnitEra; r <= LastUnitEra; r++)
            for (int c = FirstNowEra; c <= LastNowEra; c++)
                grid[r, c] = 1f;   // neutral until authored (user rule: no invented curve)

        int loaded = 0;
        foreach (var row in ModelRegistry.EraGrid ?? new List<EraScaleRow>())
        {
            if (row == null || row.unitEra < FirstUnitEra || row.unitEra > LastUnitEra || row.scales == null) continue;
            for (int c = FirstNowEra; c <= LastNowEra && c < row.scales.Count; c++)
                if (row.scales[c] > 0f) { grid[row.unitEra, c] = row.scales[c]; loaded++; }
        }
        thresholds = (ModelRegistry.FormationThresholds ?? new List<FormationThreshold>())
            .Select(t => new FormationThreshold { threshold = t.threshold, formation = t.formation, note = t.note })
            .OrderBy(t => t.threshold).ToList();
        formationNames = null;   // re-gather (the formation registry may have changed since last open)
        dirty = false;
        status = loaded > 0
            ? $"Loaded {loaded} grid cell(s), {thresholds.Count} formation threshold(s)."
            : "No grid saved yet — every cell is 1.0, so units keep their Resize Lab size in every era. Author the ageing you want, then Save.";
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Global Era Lab", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "How a scaled unit's size ages. Rows = the era the UNIT belongs to, columns = the era the WORLD is in; " +
            "the cell multiplies that unit's Resize Lab scale.\n\n" +
            "Only units with a Resize Lab rule are affected — this grid never resizes anything else. A unit in its " +
            "own era always renders at its authored size (1.0), which is why the grid starts one era later.\n\n" +
            "NAVAL ONLY for now: ships age, land and air units keep their authored size in every era (animals like " +
            "cave bears still scale — they just don't drift with the eras).\n\n" +
            "The era is the GLOBAL era (computed from all empires' research, identical for everyone). Runtime-only: " +
            "nothing is baked, and a unit re-scales LIVE when the era turns mid-game.", MessageType.None);

        scroll = EditorGUILayout.BeginScrollView(scroll);

        // header: current eras
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("unit era \\ world", EditorStyles.miniBoldLabel, GUILayout.Width(110));
            for (int c = FirstNowEra; c <= LastNowEra; c++)
                EditorGUILayout.LabelField($"{c} {Short(c)}", EditorStyles.miniBoldLabel, GUILayout.Width(52));
        }

        for (int r = FirstUnitEra; r <= LastUnitEra; r++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{r} {EraNames[r]}", GUILayout.Width(110));
                for (int c = FirstNowEra; c <= LastNowEra; c++)
                {
                    if (c <= r)   // a unit in its own era (or earlier) is never modified — keep the shape, show why
                    {
                        using (new EditorGUI.DisabledScope(true)) EditorGUILayout.LabelField("—", GUILayout.Width(52));
                        continue;
                    }
                    float v = EditorGUILayout.FloatField(grid[r, c], GUILayout.Width(52));
                    if (v <= 0f) v = 0.01f;
                    if (!Mathf.Approximately(v, grid[r, c])) { grid[r, c] = v; dirty = true; }
                }
            }
        }

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Reset all to 1.0", GUILayout.Width(150)))
            {
                for (int r = FirstUnitEra; r <= LastUnitEra; r++)
                    for (int c = FirstNowEra; c <= LastNowEra; c++) grid[r, c] = 1f;
                dirty = true; GUI.FocusControl(null);
            }
            GUILayout.FlexibleSpace();
        }

        // ── Formation-by-size MOVED (2026-07-30, user ruling): thresholds are configured PER UNIT in the
        //    Formation Override window ("Formation by size" section of a unit link). This lab keeps only the
        //    ageing grid. Legacy GLOBAL thresholds saved here earlier still work as a runtime fallback — shown
        //    below with a Clear button so they can be dropped once migrated.
        if (thresholds.Count > 0)
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                $"{thresholds.Count} LEGACY global formation threshold(s) still saved here ({string.Join(", ", thresholds.Select(t => $"<= x{t.threshold:0.###} -> {t.formation}"))}).\n" +
                "Formation-by-size now lives in the FORMATION OVERRIDE window, per unit (a unit link's 'Formation by size' " +
                "section). The runtime keeps honoring these global rows as a fallback for units without per-unit thresholds — " +
                "re-author them on the units you care about, then Clear.", MessageType.Warning);
            if (GUILayout.Button("Clear legacy global thresholds", GUILayout.Width(230)))
            { thresholds.Clear(); dirty = true; }
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Preview — an Ancient unit whose Resize Lab rule is x4", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Anc x4   " + string.Join("   ", Enumerable.Range(FirstNowEra, LastNowEra - FirstNowEra + 1)
                .Select(c => $"{Short(c)} x{4f * grid[FirstUnitEra, c]:0.##}").ToArray()),
            MessageType.None);

        EditorGUILayout.EndScrollView();
        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!dirty))
                if (GUILayout.Button("Save (runtime-only — relaunch the game)", GUILayout.Height(30)))
                {
                    var rows = new List<EraScaleRow>();
                    for (int r = FirstUnitEra; r <= LastUnitEra; r++)
                    {
                        var row = new EraScaleRow { unitEra = r, scales = new List<float>() };
                        for (int c = 0; c <= LastNowEra; c++)                       // index by absolute era, so the
                            row.scales.Add(c >= FirstNowEra && c > r ? grid[r, c] : 1f);   // plugin can read cell[era] directly
                        rows.Add(row);
                    }
                    ModelRegistry.EraGrid = rows;   // (below) SaveStatics re-reads models from disk so this Lab's stale model snapshot can't revert a bake/edit made in another window
                    // thresholds are stored ASCENDING so the runtime can take the first match without sorting
                    thresholds = thresholds.Where(t => !string.IsNullOrWhiteSpace(t.formation)).OrderBy(t => t.threshold).ToList();
                    ModelRegistry.FormationThresholds = thresholds;
                    bool ok = ModelRegistry.SaveStatics();   // era/threshold statics only — preserves the on-disk models (this Lab never edits them)
                    status = ok ? $"Saved a {LastUnitEra - FirstUnitEra + 1}x{LastNowEra - FirstNowEra + 1} grid + {thresholds.Count} formation threshold(s). Relaunch the game to apply."
                                : "Save FAILED — see the Console (registry locked or corrupt).";
                    dirty = !ok;
                }
            if (GUILayout.Button("Reload", GUILayout.Width(70), GUILayout.Height(30))) Reload();
        }
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
    }
}
