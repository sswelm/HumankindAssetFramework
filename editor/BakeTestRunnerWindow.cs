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

            // THE WORKSHOP AND THE VEHICLE LAB (2026-09-25, user: "could you add fuse and split and extra generate test
            // to the bake test"): the two tools no row exercised - split/tear/fuse on real ships, and the Lab's Generate.
            new TestRow { name = "Does the Model Workshop still split, tear and fuse? (every recipe's source)", quick = true, on = true,
                cost = "in-memory Workshop passes over every Vehicle Lab recipe's source GLB — no Blender, minutes",
                what = "Runs the Workshop's split (every multi-island part; no merge, and the 1% default), a tear (the " +
                       "smallest multi-island part — a tear costs minutes on a large one) and a fuse of the largest parts " +
                       "up to 150k triangles into one shell, on every GLB a Vehicle Lab recipe names as its source, in " +
                       "memory (nothing on disk is touched), and checks: no " +
                       "triangle is lost by a split or a tear, every split child is one island, node names stay unique, " +
                       "the fused shell is in the output with the reported triangle count and kept at least half the " +
                       "triangles. Then diffs the counts — parts, islands, children, welded vertices, faces rewound — " +
                       "against a blessed golden (Tools/workshop_golden/<file>.txt; the first run captures it). A " +
                       "rewound-faces count that moves on a ship nobody touched is a direction-pass regression.",
                run = WorkshopGateTest.RunSection },

            new TestRow { name = "Does the Vehicle Lab still generate? (representative recipes)", quick = true, on = true, needsBlender = true, group = "lab",
                cost = "one Blender rig run per representative recipe (up to six) — minutes",
                what = "Runs the Vehicle Lab's Generate — the SAME code the button runs, headless: no dialogs, no " +
                       "preview, everything under Logs/bake_tests/lab and nothing under Assets/ — on the first saved " +
                       "recipe that marks oars, a gun, wheels, sails, a rotor and tracks. Requires the rig script's " +
                       "completion marker and the output GLB, an armature within the 256-bone cap, and diffs the run's " +
                       "summary lines (bones, wheel clusters, clip frames, every reduce tier's vertex counts) against a " +
                       "blessed golden (Tools/lab_golden/<recipe>.txt; the first run captures it). A lost bone or a " +
                       "changed reduction fails here without anyone reading the log.",
                run = VehicleLabGateTest.RunRepresentativesSection },

            new TestRow { name = "Does every Vehicle Lab recipe still generate? (every recipe)", needsBlender = true, group = "lab", thorough = true,
                cost = "one Blender rig run per saved recipe — slow",
                what = "The same check as the row above on EVERY saved recipe (Assets/FactorySource/VehicleLab/Recipes), " +
                       "not just the representatives. Mutually exclusive with that row. Run before a release, or after " +
                       "touching vehicle_rig.py.",
                run = VehicleLabGateTest.RunAllSection },
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
            "rewritten after every test, so even a cancelled run leaves what finished. To stop a run, press Cancel on " +
            "the progress bar: it takes effect within seconds (between models; a running Blender step is killed).", MessageType.Info);

        // THE TWO IN-WINDOW BARS — run level and step level — live during a run via Progress.RepaintNow().
        // They freeze only while Unity's own native dialogs (Importing…, Hold on…) hold the screen; the run
        // position keeps showing there too, ridden into the fixture filenames those dialogs display.
        if (Running && Progress.Active)
        {
            EditorGUILayout.Space(2);
            var r1 = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.ProgressBar(r1, Progress.OverallFrac, "Run:  " + Progress.RowLabel);
            var r2 = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.ProgressBar(r2, Progress.InnerFrac, Progress.InnerLabel);
            // STOP, IN THE WINDOW (2026-09-17, user: "why not put the cancel button here instead" — the modal bar's
            // Cancel kept vanishing under Unity's Importing dialog). The run blocks the main thread, so this button
            // never receives a click the IMGUI way; instead its screen rectangle is recorded here and the runner asks
            // the OS at every poll whether the mouse button is down over it (or Esc is held) — NativeCancel below.
            // Hold it for a moment: the poll runs at every phase boundary and every 250 ms during a Blender step.
            var r3 = EditorGUILayout.GetControlRect(false, 26);
            GUI.Button(r3, Progress.CancelRequested ? "Stopping after the current step…" : "STOP the run  —  hold the button (or hold Esc) until it says 'Stopping'");
            if (Event.current.type == EventType.Repaint) Progress.StopRect = GUIUtility.GUIToScreenRect(r3);
            EditorGUILayout.Space(2);
        }
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
        Progress.Attach(this);   // the in-window bars need the instance to force synchronous repaints
        lastVerdict = null;
        foreach (var r in queue) r.last = null;
        ConversionRowSelected = queue.Any(x => x.run.Method.Name.Contains("RunRegistryConvertedSection")
                                            || x.name.StartsWith("Do the real rigs"));
        bool cancelled = false;
        try
        {
            // No modal per fixture, for EVERY row (2026-09-13 round 2: the first QuietDialogs pass covered only
            // the feature-test sections — the smoke test's over-ceiling NuclearWarheads still raised the dialog).
            // The runner owns the whole run, so the runner owns the flag; console warnings still log per bake.
            UniversalBaker.QuietDialogs = true;
            for (int i = 0; i < queue.Count; i++)
            {
                var r = queue[i];
                current = r;
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Bake Tests — safe to leave running",
                        FormattableString.Invariant($"{r.name}  ({i + 1} of {queue.Count}, {runWatch.Elapsed.TotalMinutes:0.0} min elapsed)"),
                        (float)i / Math.Max(1, queue.Count)))
                { cancelled = true; break; }
                Progress.BeginRow(r.name, i, queue.Count, runWatch);   // sections compose their step into this row's context
                RunOne(r);
                collected.Add(r.last);
                if (r.last.fail > 0) r.open = true;   // failures unfold themselves — the detail is the point
                pending.Dequeue();
                // durable after EVERY test: an interrupted run still leaves a report of what did finish
                lastReportPath = WriteReport(collected, InterimVerdict(collected, runWatch, finished: false));
                if (Progress.CancelRequested) { cancelled = true; r.open = true; break; }   // Cancel pressed inside the row: the section stopped early and says so in its body
            }
        }
        finally { UniversalBaker.QuietDialogs = false; EditorUtility.ClearProgressBar(); Progress.EndRun(); }
        FinishRun(cancelled, queue.Count);
    }

    // TWO-LEVEL PROGRESS IN THE ONE BAR THE EDITOR HAS (2026-08-24, user: "why doesn't it use 2 progress bars?").
    // It can't — EditorUtility's modal is title + one line + one float, and Unity's own Importing dialog covers
    // everything during synchronous imports regardless. But the run DOES know both levels internally: the runner
    // knows row i/N and elapsed, each section knows model j/M and the step. This composes them into the one bar:
    // title carries the RUN level, the info line carries the SECTION level, and the fraction is overall progress
    // (rows weighted equally). Sections call Step() instead of DisplayProgressBar; run standalone (no runner on
    // the stack) they degrade to the plain single-level bar they always had.
    internal static class Progress
    {
        static string rowName; static int rowIndex, rowCount; static System.Diagnostics.Stopwatch watch;
        static BakeTestRunnerWindow window;                 // the open window, for the IN-WINDOW bars below
        static System.Reflection.MethodInfo repaintNow;     // EditorWindow.RepaintImmediately (internal)

        // Read by OnGUI to draw the two bars INSIDE the window while a run is on the stack.
        internal static bool Active => rowName != null;
        internal static string RowLabel => rowName == null ? "" : $"{rowIndex + 1}/{rowCount} · {rowName}";
        internal static string InnerText { get; private set; }
        internal static float InnerFrac { get; private set; }
        internal static float OverallFrac => rowName == null ? 0f : (rowIndex + Mathf.Clamp01(InnerFrac)) / Math.Max(1, rowCount);
        internal static string InnerLabel => watch == null || InnerText == null ? (InnerText ?? "")
            : FormattableString.Invariant($"{InnerText}   ({watch.Elapsed.TotalMinutes:0.0} min)");

        // CANCEL ANYWHERE (2026-09-17, user: "I have no way to stop it"): the run blocks the main thread, so the modal
        // bar's Cancel button is the only input there is — and it existed only on the between-rows bar, while a row is
        // a whole section of bakes lasting many minutes. Every bar this class draws is cancelable now; a click sets
        // CancelRequested, and the sections stop between models, the runner stops between rows, and RunBounded kills
        // a running Blender within its next 250 ms slice. Nothing already baked is lost: the report is rewritten
        // after every row, and a section reports what it finished plus a CANCELLED line.
        internal static bool CancelRequested { get; private set; }
        internal static Rect StopRect;   // the in-window STOP button, in screen points (recorded at each repaint)
        internal static void Attach(BakeTestRunnerWindow w) { window = w; CancelRequested = false; StopRect = default; NativeCancel.Reset(); }
        // the OS-level check: the mouse held down over the STOP button, or Esc held — works while the main thread is blocked
        // and while Unity's own Importing modal covers every bar (the bars only need to be drawn for the RECT to be current)
        static void PollNative() { if (!CancelRequested && NativeCancel.Pressed(StopRect)) CancelRequested = true; }
        internal static void BeginRow(string name, int index, int count, System.Diagnostics.Stopwatch w)
        { rowName = name; rowIndex = index; rowCount = count; watch = w; Step("starting…", 0f); }   // the bar is up from the first second (2026-09-17: "it takes a long time for something to appear")
        // Polled from the BAKER at its phase boundaries (UniversalBaker.TestPoll): Unity's own Importing modal covers
        // every bar during a synchronous import and eats the clicks; the runner's bar returns the moment the import
        // ends, and a click then is honoured at the next boundary instead of the next model. Throws out of the bake.
        internal static void Poll() { Heartbeat(); }
        internal static void ThrowIfCancelled() { if (CancelRequested) throw new OperationCanceledException("Bake Tests: cancelled by the user"); }
        internal static void EndRun() { rowName = null; watch = null; window = null; CancelRequested = false; }
        // the modal's width is Unity's and fixed: the title carries only the run position and the row name (the cancel
        // hint lives in the window, which is as wide as the user makes it — 2026-09-17: the long title was clipped)
        // …and Unity appends its own " (busy for 34s)…" once a step runs long, so the row's parenthetical detail is
        // dropped from the title too ("Does every model still bake?" — the window shows the full name)
        static string ShortRow => rowName == null ? "" : (rowName.IndexOf(" (", StringComparison.Ordinal) > 0 ? rowName.Substring(0, rowName.IndexOf(" (", StringComparison.Ordinal)) : rowName);
        static string Title(string plain) => rowName == null ? plain : FormattableString.Invariant($"Bake Tests {rowIndex + 1}/{rowCount} · {ShortRow}");
        /// Re-render the bars with live elapsed time while a SUBPROCESS runs (RunBounded's sliced wait calls this
        /// every 250 ms). Text and fraction stay put — only the elapsed figure and the modal repaint move, which
        /// is exactly the "still alive" signal a minutes-long Blender step was missing. No-op outside a run.
        internal static void Heartbeat()
        {
            if (rowName == null || InnerText == null) return;
            if (EditorUtility.DisplayCancelableProgressBar(Title("HAF Bake Tests"),
                    FormattableString.Invariant($"{InnerText}   ({watch.Elapsed.TotalMinutes:0.0} min elapsed)"),
                    OverallFrac)) CancelRequested = true;
            PollNative();
            RepaintNow();
        }

        internal static void Step(string inner, float innerFrac)
        {
            InnerText = inner; InnerFrac = Mathf.Clamp01(innerFrac);
            if (rowName == null) { if (EditorUtility.DisplayCancelableProgressBar("HAF Bake Tests", inner, innerFrac)) CancelRequested = true; return; }
            if (EditorUtility.DisplayCancelableProgressBar(Title("HAF Bake Tests"),
                    FormattableString.Invariant($"{inner}   ({watch.Elapsed.TotalMinutes:0.0} min elapsed)"),
                    OverallFrac)) CancelRequested = true;
            PollNative();
            RepaintNow();
        }

        // THE ONLY WAY AN EDITOR WINDOW UPDATES DURING A SYNCHRONOUS RUN (2026-08-24, user: "no progress bar in
        // the test dialog as I asked you to add"). The run blocks the main thread by design (the fire-and-forget
        // decision — a tick-driven queue silently stopped when the editor lost focus), and a blocked main thread
        // never services Repaint() — an in-window bar drawn the normal way would sit frozen at 0% for the whole
        // run, which lies. EditorWindow.RepaintImmediately() paints synchronously but is INTERNAL; reflected here,
        // pinned to Unity 2021.3.1f1 like every other internal this project reaches. If Unity ever removes it the
        // catch degrades to a queued Repaint: the bars freeze instead of erroring, and the modal bar still works.
        static void RepaintNow()
        {
            if (window == null) return;
            try
            {
                if (repaintNow == null)
                    repaintNow = typeof(EditorWindow).GetMethod("RepaintImmediately",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (repaintNow != null) repaintNow.Invoke(window, null); else window.Repaint();
            }
            catch { try { window.Repaint(); } catch { } }
        }
    }

    // OS-LEVEL CANCEL (Windows editor only; a no-op elsewhere). GetAsyncKeyState reports a key's state without any
    // message pump: bit 15 = down right now, bit 0 = pressed since the last call (sticky, so a short click between two
    // polls is still seen — unless something else queried the key first, which is why the advice says HOLD).
    // The mouse counts only over the STOP button's screen rect; Esc counts anywhere. Screen points vs physical pixels:
    // GetCursorPos is physical, GUIToScreenRect is points — divided by the editor's pixelsPerPoint.
    static class NativeCancel
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        static readonly int ownPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct Point { public int X, Y; }
        const int VK_LBUTTON = 0x01, VK_ESCAPE = 0x1B;
        static bool unavailable;

        internal static void Reset()
        {   // drain the "pressed since last call" bits so a click from before the run cannot cancel it
            if (unavailable || Application.platform != RuntimePlatform.WindowsEditor) return;
            try { GetAsyncKeyState(VK_LBUTTON); GetAsyncKeyState(VK_ESCAPE); } catch { unavailable = true; }
        }

        internal static bool Pressed(Rect stopScreenRect)
        {
            if (unavailable || Application.platform != RuntimePlatform.WindowsEditor) return false;
            try
            {
                // only while UNITY owns the foreground window: the key state is global, and Esc or a click in another
                // application over the button's stored coordinates must not stop a background run (review of 8cff051)
                GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
                if (fgPid != (uint)ownPid) { GetAsyncKeyState(VK_ESCAPE); GetAsyncKeyState(VK_LBUTTON); return false; }   // drain the sticky bits too
                if ((GetAsyncKeyState(VK_ESCAPE) & 0x8001) != 0) return true;
                bool mouse = (GetAsyncKeyState(VK_LBUTTON) & 0x8001) != 0;
                if (!mouse || stopScreenRect.width <= 0 || !GetCursorPos(out Point p)) return false;
                float scale = Mathf.Max(0.5f, EditorGUIUtility.pixelsPerPoint);
                return stopScreenRect.Contains(new Vector2(p.X / scale, p.Y / scale));
            }
            catch { unavailable = true; return false; }
        }
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
        catch (OperationCanceledException)
        { r.last = new BakeTestSection { title = r.name, skip = 1, body = "CANCELLED by the user before this row finished — nothing counted; the rows above are complete." }; }
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
