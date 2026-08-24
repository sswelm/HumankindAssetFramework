// BakeTestRunnerWindow.cs — THE central testing suite (Tools ▸ HAF ▸ Bake Tests…).
//
// Seven bare menu items ("Bake Conversion Gate Test (litmus)"?) meant nobody could tell what a test did — or which
// to run — without reading source (user, 2026-08-20: "this looks ridiculous… we need a specialized testing dialog
// with clear explanation what we are testing… the center testing suite with clear UI feedback"). This window
// replaces ALL of them:
//   * every bake integration test is one ROW — a plain-language what-it-tests, what it costs, a checkbox,
//   * Quick/Everything presets and ONE Run button,
//   * FIRE AND FORGET: the whole selected set runs in ONE synchronous call behind a cancellable progress bar, so it
//     completes with the editor unfocused/minimised (an editor-tick queue silently STOPPED when you alt-tabbed away),
//     and the report is rewritten after every test so an interrupted run still leaves what finished,
//   * per-row expandable detail (the full per-model lines, in the window — the Console keeps the deep errors),
//   * one durable report per run: Logs/haf_bake_tests_report.txt (the editor twin of the runtime's
//     haf_smoke_report.txt), so "did the tests pass before this release?" has an answer after the window closes.
// The tests themselves live unchanged in BakeSmokeTest / BakeFeatureTest / ConversionGateTest — they just return a
// BakeTestSection now instead of each talking to its own dialog.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// What every bake test hands back: counts + the human-readable detail its dialog used to show.
public class BakeTestSection
{
    public string title;
    public int pass, fail, skip;
    public double seconds;      // wall time for this row — filled by the runner, shown per row and in the report
    public string body;
}

public class BakeTestRunnerWindow : EditorWindow
{
    class TestRow
    {
        public string name;            // row title (also the report section title)
        public string what;            // plain language: WHAT is being tested and how
        public string cost;            // what running it costs (time / dependencies)
        public bool needsBlender;      // auto-skipped when Blender is missing
        public bool quick;             // part of the "Quick" preset
        public string group;           // rows sharing a group are mutually exclusive (radio behavior)
        public bool thorough;          // the group member the "Everything" preset picks
        public bool on;                // checkbox state
        public Func<BakeTestSection> run;
        public BakeTestSection last;   // result of the most recent run (this session)
        public bool open;              // detail foldout
    }

    // Set for the duration of a run: true when the converted-rigs row is part of THIS run, which is the only
    // condition under which the catalog row may hand its converted models over (see BakeSmokeTest.RunAllSection).
    // A field rather than a parameter because each row is a plain Func — the runner owns the cross-row knowledge.
    static bool ConversionRowSelected;

    List<TestRow> rows;
    Vector2 scroll;
    string lastReportPath, lastVerdict;
    GUIStyle wrap, mono, wrapBold;

    // Run state. `pending` is what's left to do; it exists so OnGUI can say so, NOT to drive the run — the run is one
    // synchronous loop (see StartRun), which is what lets it finish while the editor sits unfocused.
    Queue<TestRow> pending;
    TestRow current;
    List<BakeTestSection> collected;
    System.Diagnostics.Stopwatch runWatch;
    bool blenderAtRunStart;

    [MenuItem("Tools/HAF/Bake Tests…", false, 30)]
    static void Open() => GetWindow<BakeTestRunnerWindow>("Bake Tests");

    void OnEnable()
    {
        rows = new List<TestRow>
        {
            new TestRow { name = "Does the baker still work? (one model per path)", quick = true, on = true, needsBlender = true, group = "smoke",
                cost = "a handful of real bakes (~minutes)",
                what = "Re-bakes ONE representative model per bake path (static / animated / rig-converted, per material " +
                       "mode) under a throwaway name and checks the baked assets exist and are not empty stubs. The " +
                       "quick \"did I break the baker?\" check after baker changes. (The 'smoke test'.)",
                run = BakeSmokeTest.RunRepresentativesSection },

            new TestRow { name = "Does every model still bake? (whole catalog)", needsBlender = true, group = "smoke", thorough = true,
                cost = "one full bake per registry model — slow",
                what = "The same check as the row above, but for EVERY registry entry, not just representatives. " +
                       "Mutually exclusive with that row (this one already covers everything it bakes). Run before a release. " +
                       "When the converted-rigs row is also selected, those models are baked once there rather than twice — " +
                       "it asserts these same assets, so nothing is lost and a full run is minutes shorter.",
                run = () => BakeSmokeTest.RunAllSection(skipConverted: ConversionRowSelected) },

            new TestRow { name = "Do the bake options do what they claim? (synthetic cubes)", quick = true, on = true,
                cost = "~15 fast cube bakes, no Blender",
                what = "Bakes tiny synthetic cubes with one baker option toggled at a time — double-sided, normal modes, " +
                       "heightUV, atlas size cap, size, position offset, winding fix, multi-material, brightness/" +
                       "saturation — and asserts each one measurably changed the baked result. Also proves the rollback " +
                       "safety net restores your assets after a FAILED re-bake. (The 'feature test', Tier 1.)",
                run = BakeFeatureTest.RunTier1Section },

            new TestRow { name = "Do the Blender + animation options work? (real rigs)", needsBlender = true,
                cost = "real Blender bakes — slow",
                what = "The options a cube can't exercise: triangle-budget decimation (targetTris), removing a named " +
                       "part (stripParts), and the full ANIMATED pipeline end-to-end on two real rigged models borrowed " +
                       "from the registry (skeleton + clip must come out). (The 'feature test', Tier 2.)",
                run = BakeFeatureTest.RunTier2Section },

            new TestRow { name = "Is rig conversion still correct? (control rig)", quick = true, on = true, needsBlender = true,
                cost = "one synthetic rig bake (fast after the first run)",
                what = "Synthesizes a known 12-bone test rig (the 'litmus'), bakes it through the raw-rig conversion, " +
                       "and checks the four invariants the game silently requires: every bone scale exactly 1, parents " +
                       "sorted before children, rotation-only clips, and the animation actually baked. Each invariant " +
                       "was once violated and cost hours of in-game diagnosis. This is the CONTROL for the row below: " +
                       "a synthetic rig separates 'the pipeline broke' from 'this model broke'.",
                run = ConversionGateTest.RunLitmusSection },

            new TestRow { name = "Do the real rigs still convert correctly? (every converted model)", needsBlender = true,
                cost = "a full conversion bake per converted model — slow",
                what = "The same four invariants, but on every REAL converted rig in the registry (animated + 'Convert " +
                       "raw rig', e.g. the Combine soldier's 62-bone auto-rig). The strongest net; needs each source " +
                       "model file on disk. COMPLEMENTS the control-rig row (different fixtures, nothing baked twice): a " +
                       "real rig failing while the control passes points at the model, not the pipeline.",
                run = ConversionGateTest.RunRegistryConvertedSection },

            new TestRow { name = "Did a deploy model change unexpectedly? (golden snapshot)", needsBlender = true,
                cost = "one Blender conversion + bone dump per deploy model",
                what = "Re-runs the deploy conversion for every deploy-converted model (the m114 howitzers, T-62) and " +
                       "diffs the resulting bone poses against a blessed golden snapshot. Catches the per-model " +
                       "regressions the invariant checks can't (the crossed-legs class of bug). NO overlap with the two " +
                       "rows above — they SKIP deploy-convert models entirely.",
                run = ConversionGateTest.RunDeployGoldenSection },
        };
    }

    // The run is synchronous, so closing the window cannot interrupt it mid-suite; this just clears the transient state.
    void OnDisable() { pending = null; current = null; }

    bool Running => pending != null;

    void OnGUI()
    {
        if (wrap == null) wrap = new GUIStyle(EditorStyles.label) { wordWrap = true };
        if (mono == null) mono = new GUIStyle(EditorStyles.miniLabel) { wordWrap = false, font = EditorStyles.miniFont };
        if (wrapBold == null) wrapBold = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true };   // titles WRAP, never clip
        bool blender = UniversalBaker.BlenderAvailable();

        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox(
            "Integration tests that run REAL bakes. All of them are non-destructive: everything bakes under throwaway " +
            "names — your models, assets and registry are never touched. Results appear on each row (expand for " +
            "detail), in the Console, and in Logs/haf_bake_tests_report.txt.\n" +
            "Fire and forget: a run finishes on its own — you can alt-tab away or minimise Unity, and the report is " +
            "rewritten after every test, so even a cancelled run leaves what finished.", MessageType.Info);
        if (!blender)
            EditorGUILayout.HelpBox("Blender not found — rows marked 'needs Blender' will be skipped.", MessageType.Warning);

        using (new EditorGUI.DisabledScope(Running))
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Select:", GUILayout.Width(44));
            if (GUILayout.Button("Quick set", GUILayout.Width(90))) foreach (var r in rows) r.on = r.quick;
            // "Everything" honors the exclusive groups: it picks the thorough member (ALL models), not both scopes.
            if (GUILayout.Button("Everything", GUILayout.Width(90))) foreach (var r in rows) r.on = r.group == null || r.thorough;
            if (GUILayout.Button("None", GUILayout.Width(60))) foreach (var r in rows) r.on = false;
        }
        EditorGUILayout.Space(2);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (var r in rows)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                // Title, status, description each get their OWN full-width line: a shared horizontal row made the
                // layout demand the title's full unwrapped width, which pushed the whole scroll content wider than a
                // narrow window and CLIPPED every label at the window edge ("…(c" — user-caught, 2026-08-20).
                using (new EditorGUI.DisabledScope(Running))
                using (new EditorGUILayout.HorizontalScope())
                {
                    // Checkbox + a label-styled BUTTON as the title. NOT ToggleLeft: with a word-wrapping style it
                    // mis-sizes itself (a ~180px column, too little height — the "even worse" round, 2026-08-20).
                    // The button takes ALL remaining width, wraps properly, and clicking the text still toggles.
                    bool was = r.on, v = EditorGUILayout.Toggle(r.on, GUILayout.Width(16));
                    if (GUILayout.Button(r.name, wrapBold, GUILayout.ExpandWidth(true))) v = !v;
                    r.on = v;
                    if (r.on && !was && r.group != null)   // radio behavior inside a group: checking one unchecks the rest
                        foreach (var other in rows)
                            if (other != r && other.group == r.group) other.on = false;
                }
                string status = null; var col = GUI.color; var keep = GUI.color;
                if (Running && r == current) { status = "RUNNING…"; col = new Color(0.5f, 0.8f, 1f); }
                else if (Running && pending.Contains(r)) { status = "queued"; col = new Color(0.7f, 0.7f, 0.7f); }
                else if (r.last != null)
                {
                    status = ResultLabel(r.last);
                    col = r.last.fail > 0 ? new Color(1f, 0.45f, 0.45f)
                        : r.last.pass > 0 ? new Color(0.45f, 1f, 0.45f) : new Color(1f, 0.85f, 0.4f);
                }
                if (status != null)
                { GUI.color = col; EditorGUILayout.LabelField(status, wrapBold); GUI.color = keep; }
                EditorGUILayout.LabelField(r.what, wrap);
                EditorGUILayout.LabelField("Costs: " + r.cost + (r.needsBlender ? "  •  needs Blender" : ""), EditorStyles.miniLabel);
                if (r.last != null && !string.IsNullOrEmpty(r.last.body))
                {
                    r.open = EditorGUILayout.Foldout(r.open, "details", true);
                    if (r.open)
                        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                            foreach (var line in r.last.body.Split('\n'))
                                EditorGUILayout.LabelField(line, mono);
                }
            }
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(2);
        int selected = rows.Count(x => x.on);
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(Running || selected == 0))
                if (GUILayout.Button(Running ? "Running…" : selected == 0 ? "Run (nothing selected)" : $"Run {selected} selected test(s)", GUILayout.Height(28)))
                    StartRun(blender);
            if (!string.IsNullOrEmpty(lastReportPath))
                if (GUILayout.Button("Open report", GUILayout.Width(100), GUILayout.Height(28)))
                    EditorUtility.OpenWithDefaultApp(lastReportPath);
        }
        if (Running)
            EditorGUILayout.LabelField($"Running {current?.name}…  ({collected.Count} done, {pending.Count} to go — the editor is busy until the whole run ends — you can leave it, minimise it, or alt-tab away)", EditorStyles.boldLabel);
        else if (!string.IsNullOrEmpty(lastVerdict))
            EditorGUILayout.LabelField(lastVerdict, EditorStyles.boldLabel);
        EditorGUILayout.Space(2);
    }

    // FIRE AND FORGET (2026-08-22, user: "you need to be in the dialog active for it to complete everything, which is
    // extremely annoying, it should be fire and forget"). The run used to be a chain of `EditorApplication.delayCall`
    // ticks — one test per tick, so each row could paint its result live. But the editor only TICKS while the Unity
    // window has OS focus: alt-tab away from a 28-minute suite and the queue simply stops between tests, and you come
    // back to a run that never finished. A synchronous loop on the main thread has no such dependency — nothing
    // interrupts a method that is already executing — so the whole suite now runs in ONE call and completes with the
    // editor in the background, minimised, or on another desktop.
    //
    // What replaces the live rows: a CANCELLABLE progress bar (drawn by the editor itself, so it updates while
    // unfocused) naming the running test and the count, and — the part that makes it fire-and-forget rather than
    // fire-and-hope — the report is REWRITTEN AFTER EVERY TEST, so a cancel, a crash, or a domain reload still leaves
    // Logs/haf_bake_tests_report.txt with everything that finished. The per-row PASS/FAIL detail is all still there
    // when the run ends; it just arrives at the end instead of one at a time. (Each individual test already froze the
    // editor while it baked, so almost no interactivity is lost — only the gaps between tests.)
    void StartRun(bool blender)
    {
        var queue = rows.Where(x => x.on).ToList();
        pending = new Queue<TestRow>(queue);   // keeps `Running` true for OnGUI while the loop is on the stack
        collected = new List<BakeTestSection>();
        current = null;
        blenderAtRunStart = blender;
        runWatch = System.Diagnostics.Stopwatch.StartNew();
        lastVerdict = null;
        foreach (var r in queue) r.last = null;
        ConversionRowSelected = queue.Any(x => x.run.Method.Name.Contains("RunRegistryConvertedSection")
                                            || x.name.StartsWith("Do the real rigs"));
        bool cancelled = false;
        try
        {
            for (int i = 0; i < queue.Count; i++)
            {
                var r = queue[i];
                current = r;
                if (EditorUtility.DisplayCancelableProgressBar(
                        "HAF Bake Tests — safe to leave running",
                        FormattableString.Invariant($"{r.name}  ({i + 1} of {queue.Count}, {runWatch.Elapsed.TotalMinutes:0.0} min elapsed)"),
                        (float)i / Math.Max(1, queue.Count)))
                { cancelled = true; break; }
                RunOne(r);
                collected.Add(r.last);
                if (r.last.fail > 0) r.open = true;   // failures unfold themselves — the detail is the point
                pending.Dequeue();
                // durable after EVERY test: an interrupted run still leaves a report of what did finish
                lastReportPath = WriteReport(collected, InterimVerdict(collected, runWatch, finished: false));
            }
        }
        finally { EditorUtility.ClearProgressBar(); }
        FinishRun(cancelled, queue.Count);
    }

    void RunOne(TestRow r)
    {
        if (r.needsBlender && !blenderAtRunStart)
        { r.last = new BakeTestSection { title = r.name, skip = 1, body = "SKIP — Blender not found." }; return; }
        Debug.Log("[BakeTests] running: " + r.name + "…");
        // PER-ROW DURATION (2026-08-22): the suite is minutes long and only ever reported one total, so "which row
        // costs the time?" could not be answered from the report — the same blind spot the per-phase probe timers
        // closed. Now every row carries its own, in the window and in the durable report.
        var w = System.Diagnostics.Stopwatch.StartNew();
        try { r.last = r.run(); r.last.title = r.name; }
        catch (Exception ex)
        { r.last = new BakeTestSection { title = r.name, fail = 1, body = "harness exception: " + ex.GetType().Name + ": " + ex.Message }; }
        w.Stop();
        r.last.seconds = w.Elapsed.TotalSeconds;
        Debug.Log("[BakeTests] " + r.name + ": " + ResultLabel(r.last) + "\n" + r.last.body);
    }

    // ONE interpolated string per Invariant() call: concatenating two of them yields a plain `string`, which the
    // overload can't take (the Roslyn gate caught exactly that).
    // ZERO FAILURES IS NOT SUCCESS WHEN NOTHING RAN (2026-08-22 review). The verdict read `fail == 0 ? PASS : FAIL`,
    // so an all-skipped run wrote "PASS — 0 passed, 0 failed, 1 skipped" into the window headline AND into
    // Logs/haf_bake_tests_report.txt — the durable artifact whose whole job is answering "did the tests pass before
    // this release?". Reachable on any machine without Blender (select only Blender-dependent rows), or by cancelling
    // after a skip. The per-row label already said SKIPPED for a zero-pass section; the summary never learned the same
    // rule, which is the "a check that can pass while nothing was checked" shape this project treats as its worst sin.
    static string VerdictWord(int pass, int fail) => fail > 0 ? "FAIL" : pass > 0 ? "PASS" : "NOTHING VERIFIED";

    static string InterimVerdict(List<BakeTestSection> done, System.Diagnostics.Stopwatch w, bool finished)
        => FormattableString.Invariant(
               $"{VerdictWord(done.Sum(s => s.pass), done.Sum(s => s.fail))} — {done.Sum(s => s.pass)} passed, {done.Sum(s => s.fail)} failed, {done.Sum(s => s.skip)} skipped, in {w.Elapsed.TotalMinutes:0.0} min")
           + (finished ? "" : "  (run in progress…)");

    void FinishRun(bool cancelled, int planned)
    {
        runWatch.Stop();
        lastVerdict = InterimVerdict(collected, runWatch, finished: true)
                    + (cancelled ? FormattableString.Invariant($"  — CANCELLED after {collected.Count} of {planned} test(s)") : "");
        lastReportPath = WriteReport(collected, lastVerdict);
        // A run that verified nothing must not read as success in the Console either — same rule as the verdict word.
        string line = "[BakeTests] " + lastVerdict + " — report: " + lastReportPath;
        if (collected.Sum(s => s.fail) > 0 || collected.Sum(s => s.pass) == 0) Debug.LogWarning(line);
        else Debug.Log(line);
        pending = null; current = null;
        Repaint();
    }

    static string ResultLabel(BakeTestSection s) =>
        (s.fail > 0 ? $"FAIL — {s.fail} failed, {s.pass} passed"
         : s.pass > 0 ? $"PASS — {s.pass} passed" + (s.skip > 0 ? $", {s.skip} skipped" : "")
         : "SKIPPED")
        + (s.seconds > 0 ? FormattableString.Invariant($"   ({s.seconds / 60.0:0.0} min)") : "");

    // One durable record per run (overwritten each run — git/backup history is not the job of a test artifact).
    static string WriteReport(List<BakeTestSection> sections, string verdict)
    {
        string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Logs");
        string path = Path.Combine(dir, "haf_bake_tests_report.txt");
        try
        {
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine(FormattableString.Invariant($"HAF bake-test report — {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
            sb.AppendLine(verdict);
            sb.AppendLine();
            foreach (var s in sections)
            {
                sb.AppendLine("== " + s.title + ": " + ResultLabel(s));
                if (!string.IsNullOrEmpty(s.body)) sb.AppendLine(s.body.TrimEnd());
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception ex) { Debug.LogWarning("[BakeTests] could not write the report: " + ex.Message); return null; }
    }
}
