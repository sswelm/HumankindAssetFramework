// ModelWorkshopWindow.cs — SELECTIVE part surgery on a GLB, before it enters the Vehicle Lab.
//
// Born from the Great Galley (2026-09-06): after ruthless vertex cutting the ship carried floating junk islands
// that could not be marked Ignore in the Vehicle Lab, because they live INSIDE parts shared with hull geometry —
// one row, one role, junk welded to keel. The blunt fix existed (Model Tools ▸ Split disconnected GLB parts…,
// PR #19) but it explodes EVERY part — the Khalandion's rigging alone became ~1,500 objects, far past reviewable.
//
// The Workshop is the aimed version of the same lossless splitter: Probe lists every mesh-carrying node with its
// triangle count and how many disconnected islands it holds — AND shows the model in a turntable (the Vehicle
// Lab's proven preview, minus clips) where clicking a row highlights that part, because an island count without
// eyes is guesswork. Check exactly the parts that hide junk; Split writes a new GLB in which ONLY those become
// _Part_NNN children (GlbDisconnectedParts' method untouched: byte-identical vertex data, appended index
// accessors, triangle-total verification). The output then goes through the normal pipeline: Vehicle Lab probe →
// mark the junk Ignore → rig → Factory bake.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class ModelWorkshopWindow : EditorWindow
{
    [MenuItem("Tools/HAF/Model Workshop")]
    static void Open() => GetWindow<ModelWorkshopWindow>("Model Workshop");

    [Serializable]
    class Row
    {
        public int nodeIndex;    // the STABLE identity Split filters on (names can be null or duplicated)
        public string node;      // display name
        public string mesh;
        public int tris;
        public int islands;      // 1 = nothing to split (row disabled)
        public string blocked;   // non-null = the analyzer's reason this part cannot be split
        public bool split;       // the checkbox (Split)
        public string fuse = ""; // FUSE GROUP letter A..H (2026-09-15): rows sharing a letter fuse into one shell each; "" = none
    }
    static readonly string[] FuseLabels = { "–", "⊕A", "⊕B", "⊕C", "⊕D", "⊕E", "⊕F", "⊕G", "⊕H" };   // the per-row fuse popup; keys A–H set it, 0/Backspace clears

    const string PreviewDir = "Assets/FactorySource/ModelWorkshop";

    [SerializeField] string srcFile = "";
    [SerializeField] string outGlb = "";
    [SerializeField] bool outGlbAuto = true;   // output path is auto-derived from srcFile and TRACKS it until the user edits the field to something else (external review of PR #22)
    [SerializeField] string probedFile = "";   // the file `rows` (and the checks/preview/output path) were built from — serialized so a domain reload doesn't read surviving rows as stale (review finding 8)
    [SerializeField] List<Row> rows = new List<Row>();
    [SerializeField] bool hideWhole = false;   // filter: hide "1 island — already whole" rows (nothing to split there)
    // DISTANCE MERGE (2026-09-06, the 602-island rope): topology alone shreds segmented geometry into hundreds
    // of 3-vert parts millimetres apart. Islands within this % of a part's own diagonal count as ONE part, so
    // only genuinely distant geometry — the floating junk — separates. 0 = pure topology.
    [SerializeField] float mergePct = 1f;
    // FUSE (2026-09-15): the checked parts become ONE welded shell with consistent winding — the fix for a hull
    // authored as separate plates (the Teutonic: see-through, a hole in its side, gaps under any reduction).
    // Seam vertices closer than this (in thousandths of the model's length) become one vertex. DEFAULT 0 = exactly
    // coincident positions only: measured on the Teutonic, its plates already touch exactly (the majority rule found
    // the 1,613-face hole at 0), while 0.5‰ collapsed 1,564 rivet-sized triangles and broke the hull island apart.
    [SerializeField] float weldPermille = 0f;
    bool analyzePending;   // slider moved: recount on the first Layout pass after the drag releases
    [SerializeField] Vector2 scroll;
    [SerializeField] Vector2 windowScroll;   // the WHOLE window: header + list (≤330) + preview (600) + Split/Fuse controls overflow a short window, and the Fuse row was cut off with no way to reach it (user 2026-09-16)
    string status = "Pick a GLB and press Probe parts.";

    // ---- turntable preview state (the Vehicle Lab's proven camera, minus clips/waterline) ----
    GameObject inst; PreviewRenderUtility pru;
    [SerializeField] Vector2 orbit = new Vector2(30f, -20f);
    [SerializeField] float zoom = 1.5f;
    Vector2 previewPan;
    Bounds bounds; bool boundsValid; float fullRadius;
    string selectedRow = "";   // the highlighted part's NAME (renderer matching in the Blender preview is name-based)
    int selectedIdx = -1;      // the selected ROW's identity (node index — names can be duplicated)
    Material highlightMat;
    List<Renderer> highlightedRenderers; List<Material[]> highlightedOriginals;

    // ---- plane-cut state (2026-09-13, the Bremen deck: hull and deck are ONE welded island — nothing for the
    // island splitter to do). The cut preview is built from the SOURCE GLB's own bytes (ExtractPart), not the
    // Blender FBX round-trip, so the two-color partition on screen is exactly the triangle partition the cut
    // writes — no axis-convention mapping to get wrong. ----
    [SerializeField] int cutAxis = 1;      // source-file world axis: 0=X 1=Y (up in standard glTF) 2=Z
    [SerializeField] float cutPct = 50f;
    [SerializeField] int cutRule = 0;      // 0 = flat plane; 1 = horizontal surfaces (facing) — deck vs bow plating
    [SerializeField] float cutTiltDeg = 45f;   // facing rule: a face counts as horizontal when tilted less than this from level
    GlbDisconnectedParts.PartGeometry cutGeo;
    Mesh cutMesh; GameObject cutGO;
    Material cutMatA, cutMatB;
    int cutTrisA, cutTrisB;
    bool cutNormalsDone;
    bool CutModeActive => cutGO != null;

    void OnDisable() => DestroyPreview();

    void OnGUI()
    {
        windowScroll = EditorGUILayout.BeginScrollView(windowScroll);   // a vertical bar appears when the window is shorter than its content; the preview keeps its scroll-wheel zoom (it Use()s the event first)
        EditorGUILayout.LabelField("Model Workshop — split chosen parts into their disconnected islands, or plane-cut a connected one", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("For a part whose junk islands share a mesh with real geometry: split ONLY that part, then mark the junk Ignore in the Vehicle Lab. Lossless — vertex data, materials, skins and animations are preserved; only the checked parts gain _Part_NNN children. A CONNECTED part (1 island) can instead be plane-cut in two: select its row and press Plane cut.", EditorStyles.wordWrappedMiniLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            srcFile = EditorGUILayout.TextField(new GUIContent("Source GLB", "The model to operate on. Never overwritten."), srcFile);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                string p = EditorUtility.OpenFilePanel("Choose the source GLB", string.IsNullOrEmpty(srcFile) ? "D:/3DModels" : Path.GetDirectoryName(srcFile), "glb");
                if (!string.IsNullOrEmpty(p)) { srcFile = p.Replace('\\', '/'); outGlbAuto = true; rows.Clear(); DestroyPreview(); }
            }
        }
        // AUTO-DERIVED OUTPUT that TRACKS the source (external review of PR #22, 2026-09-07): the old
        // fill-when-empty derived exactly once — typing a source path derived from the FIRST keystroke's
        // fragment and then stuck (the reset below fires only while rows exist), so Split could write the
        // completed source's output to a "sh_split.glb" stub, over whatever lived there. While the user
        // hasn't overridden the field it now re-derives every pass; an edit that differs takes ownership.
        string autoOut = string.IsNullOrEmpty(srcFile) ? ""
            : Path.Combine(Path.GetDirectoryName(srcFile), Path.GetFileNameWithoutExtension(srcFile) + "_split.glb").Replace('\\', '/');
        if (outGlbAuto && !string.IsNullOrEmpty(autoOut)) outGlb = autoOut;
        using (new EditorGUILayout.HorizontalScope())
        {
            string typedOut = EditorGUILayout.TextField(new GUIContent("Output GLB", "Where the split copy is written — feed THIS file to the Vehicle Lab afterwards. Auto-follows the source file until you edit it."), outGlb);
            if (typedOut != outGlb) { outGlb = typedOut; outGlbAuto = SamePath(typedOut, autoOut); }
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(srcFile)))
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string p = EditorUtility.SaveFilePanel("Write split GLB", Path.GetDirectoryName(srcFile), Path.GetFileNameWithoutExtension(srcFile) + "_split", "glb");
                    if (!string.IsNullOrEmpty(p)) { outGlb = p.Replace('\\', '/'); outGlbAuto = SamePath(outGlb, autoOut); }
                }
        }

        float newMergePct = EditorGUILayout.Slider(new GUIContent("Merge closer than (%)",
            "Islands whose VERTICES come nearer than this (percent of each part's own size) count as ONE part — so a " +
            "segmented rope stays one rope instead of shredding into hundreds of 3-vert fragments, while genuinely " +
            "distant junk still separates. Measured between vertices (a long edge passing near a vertex is not seen), " +
            "accurate to about one grid cell. 0 = pure topology. Release the slider and the counts recount (no Blender re-run)."),
            mergePct, 0f, 10f);
        if (!Mathf.Approximately(newMergePct, mergePct)) { mergePct = newMergePct; analyzePending = rows.Count > 0; }
        // DEFERRED recount (review find 2026-09-06): running Analyze mid-OnGUI replaced `rows` between IMGUI's
        // Layout and event passes (control-count mismatch exceptions), and doing it per drag-tick re-parsed the
        // whole GLB on every mouse move (seconds per tick on a real ship). Recount once, on the first Layout
        // pass AFTER the drag ends.
        if (analyzePending && Event.current.type == EventType.Layout && GUIUtility.hotControl == 0)
        { analyzePending = false; Analyze(); }
        // SOURCE-SWITCH HYGIENE (review finding 8, 2026-09-07): only the Browse button cleared the probe state —
        // TYPING or pasting a different path kept the previous file's rows, checked node indices, preview and
        // output path live: Split then ran the NEW file with the OLD file's node indices (splitting whatever
        // those happen to be there) and wrote over the OLD file's _split.glb. Deferred to the Layout pass like
        // the recount above (clearing `rows` mid-pass is the control-count exception this window documents).
        if (rows.Count > 0 && Event.current.type == EventType.Layout && GUIUtility.hotControl == 0 && !SamePath(srcFile, probedFile))
        {
            rows.Clear(); outGlbAuto = true; selectedIdx = -1; selectedRow = ""; DestroyPreview();
            status = "Source file changed — press Probe parts to analyze the new file.";
        }

        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(srcFile) || !File.Exists(srcFile)))
            if (GUILayout.Button(new GUIContent("Probe parts", "List every mesh-carrying node with triangle and island counts (instant, pure C#), and build the turntable preview (headless Blender export) so a clicked row lights up in yellow."), GUILayout.Height(24)))
                Probe();

        if (rows.Count > 0)
        {
            int splittable = rows.Count(r => r.islands > 1 && r.blocked == null);
            int chosen = rows.Count(r => r.split);
            EditorGUILayout.LabelField($"Parts ({rows.Count} node(s), {splittable} with more than one island) — click a row to highlight it below; check the parts to split:", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Check all splittable", GUILayout.Width(140))) foreach (var r in rows) r.split = r.islands > 1 && r.blocked == null;
                if (GUILayout.Button("Uncheck all", GUILayout.Width(100))) foreach (var r in rows) r.split = false;
                hideWhole = EditorGUILayout.ToggleLeft(new GUIContent("Hide already-whole parts",
                    "Hide the rows with a single island — there is nothing to split in them, they only pad the list."), hideWhole, GUILayout.Width(180));
                // A 300-island rope part is a legitimate but LOUD choice — say what a check costs before Split.
                EditorGUILayout.LabelField(chosen > 0 ? $"{chosen} checked → +{rows.Where(r => r.split).Sum(r => r.islands) - chosen} new part(s) in the output" : " ", EditorStyles.miniLabel);
            }
            var shown = hideWhole ? rows.Where(r => r.islands > 1 || r.blocked != null).ToList() : rows;
            // KEYBOARD MARKING (2026-09-15, the Vehicle Lab's idiom): ↑/↓ move the highlight, A–H put the highlighted row in
            // a fuse group, 0/Backspace clear it, Space toggles its Split checkbox — marking dozens of hull plates by mouse
            // was the complaint. The Workshop has no role hotkeys, so the letters are free here.
            EditorGUILayout.LabelField("  Keys:  ↑/↓ = previous/next part   ·   A–H = fuse group of the highlighted part (⊕ column)   ·   0 / Backspace = no group   ·   Space = Split checkbox", EditorStyles.miniLabel);
            var ev = Event.current;
            if (ev.type == EventType.KeyDown && shown.Count > 0 && !EditorGUIUtility.editingTextField)
            {
                int idx = shown.FindIndex(x => x.nodeIndex == selectedIdx);
                if (ev.keyCode == KeyCode.UpArrow || ev.keyCode == KeyCode.DownArrow)
                {
                    idx = ev.keyCode == KeyCode.DownArrow ? Mathf.Min(idx + 1, shown.Count - 1) : Mathf.Max(idx - 1, 0);
                    ExitCutMode(); selectedIdx = shown[idx].nodeIndex; SelectRow(shown[idx].node);
                    scroll.y = Mathf.Max(0f, idx * 22f - 120f);
                    GUIUtility.keyboardControl = 0;
                    ev.Use(); Repaint();
                }
                else if (idx >= 0 && shown[idx].blocked == null && ev.keyCode >= KeyCode.A && ev.keyCode <= KeyCode.H)
                {
                    shown[idx].fuse = ((char)('A' + (ev.keyCode - KeyCode.A))).ToString();
                    GUIUtility.keyboardControl = 0;
                    ev.Use(); Repaint();
                }
                else if (idx >= 0 && (ev.keyCode == KeyCode.Alpha0 || ev.keyCode == KeyCode.Keypad0 || ev.keyCode == KeyCode.Backspace || ev.keyCode == KeyCode.Delete))
                {
                    shown[idx].fuse = "";
                    GUIUtility.keyboardControl = 0;
                    ev.Use(); Repaint();
                }
                else if (idx >= 0 && ev.keyCode == KeyCode.Space && shown[idx].blocked == null && shown[idx].islands > 1)
                {
                    shown[idx].split = !shown[idx].split;
                    GUIUtility.keyboardControl = 0;
                    ev.Use(); Repaint();
                }
            }
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Min(330, 22 * shown.Count + 8)));   // cap 220 -> 330 (2026-09-08 user request: +50% — a real ship's part list is dozens of rows)
            foreach (var r in shown)
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(r.islands <= 1 || r.blocked != null))
                        r.split = EditorGUILayout.Toggle(r.split, GUILayout.Width(20));
                    // FUSE GROUP (2026-09-15): an independent per-row mark — rows sharing a letter fuse into one welded shell each
                    using (new EditorGUI.DisabledScope(r.blocked != null))
                    {
                        int fi = string.IsNullOrEmpty(r.fuse) ? 0 : Mathf.Clamp(r.fuse[0] - 'A' + 1, 0, FuseLabels.Length - 1);
                        int fj = EditorGUILayout.Popup(fi, FuseLabels, GUILayout.Width(46));
                        r.fuse = fj <= 0 ? "" : ((char)('A' + fj - 1)).ToString();
                    }
                    bool isSel = selectedIdx == r.nodeIndex;
                    string label = r.blocked != null ? $"{(isSel ? "◉ " : "")}{r.node}   — skipped: {r.blocked}"
                                 : $"{(isSel ? "◉ " : "")}{r.node}   ({r.tris:N0} tris, {(r.islands == 1 ? "1 island — already whole" : r.islands.ToString("N0") + " islands")})";
                    // the row label is a BUTTON, exactly like the Vehicle Lab: click = highlight + frame in the preview
                    if (GUILayout.Button(label, isSel ? EditorStyles.whiteLabel : (r.islands > 1 && r.blocked == null ? EditorStyles.label : EditorStyles.miniLabel)))
                    { ExitCutMode(); selectedIdx = isSel ? -1 : r.nodeIndex; SelectRow(isSel ? "" : r.node); }
                }
            EditorGUILayout.EndScrollView();

            // ---- plane cut: for the selected part, connected or not — the escape hatch when island
            // splitting has nothing to grab (hull welded to deck). Whole triangles, nothing sliced. ----
            var selRowObj = rows.FirstOrDefault(r => r.nodeIndex == selectedIdx);
            if (selRowObj != null && selRowObj.blocked == null)
            {
                if (!CutModeActive)
                {
                    if (GUILayout.Button(new GUIContent($"Plane cut '{selRowObj.node}'…  (split a connected part in two along a flat cut)",
                            "For geometry the island split can't separate — a hull welded to its deck. Pick an axis and slide the plane; " +
                            "whole triangles go to one side or the other by centroid (nothing is sliced, vertex data stays byte-identical), " +
                            "and the two sides become _CutA/_CutB children in the output GLB."), GUILayout.Height(22)))
                        EnterCutMode(selRowObj);
                }
                else
                {
                    EditorGUILayout.LabelField($"Cut '{cutGeo.NodeName}' — yellow side becomes _CutA, grey side _CutB:", EditorStyles.miniBoldLabel);
                    int newRule = EditorGUILayout.Popup(new GUIContent("Cut rule",
                        "Flat plane: everything at or above the plane goes to _CutA — a straight geometric slice. " +
                        "Horizontal surfaces: a triangle goes to _CutA when its FACE lies flatter than the tilt limit — the deck " +
                        "separates from the bow plating by orientation, where no flat plane can trace the boundary."),
                        cutRule, new[] { "Flat plane", "Horizontal surfaces (deck vs sides)" });
                    int newAxis = EditorGUILayout.Popup(new GUIContent(cutRule == 0 ? "Cut axis" : "Up axis",
                        "World axis in the SOURCE file's own frame. On a standard glTF ship Y is up: for the plane rule that means a " +
                        "horizontal cut (deck off hull), X/Z are vertical cuts (bow section, side); for the facing rule it defines " +
                        "which way 'level' faces. Watch the preview — the colors are the actual partition."),
                        cutAxis, new[] { "X", "Y  (up, in most GLBs)", "Z" });
                    float newTilt = cutTiltDeg;
                    if (cutRule == 1)
                        newTilt = EditorGUILayout.Slider(new GUIContent("Max tilt (°)",
                            "How far from level a face may lean and still count as horizontal (deck camber, sheer). Undersides count " +
                            "too — a deck's ceiling is as horizontal as its planking. 45 is a good start; lower = stricter deck. " +
                            "At 0 only mathematically perfect level faces pass — float noise can drop even those, so prefer 1-2 as the practical minimum."),
                            cutTiltDeg, 0f, 85f);
                    float newPct = EditorGUILayout.Slider(new GUIContent(cutRule == 0 ? "Position (%)" : "Only above (%)",
                        cutRule == 0 ? "Where the plane sits between the part's two ends on that axis. Live: what shows yellow is exactly what _CutA gets."
                                     : "Height floor: horizontal faces BELOW this stay in _CutB — keeps the equally-horizontal hull BOTTOM out of the deck piece. 0 = judge the whole part by facing alone."),
                        cutPct, 0f, 100f);
                    if (newRule != cutRule || newAxis != cutAxis || !Mathf.Approximately(newPct, cutPct) || !Mathf.Approximately(newTilt, cutTiltDeg))
                    { cutRule = newRule; cutAxis = newAxis; cutPct = newPct; cutTiltDeg = newTilt; UpdateCutPartition(); }
                    EditorGUILayout.LabelField($"   _CutA (yellow): {cutTrisA:N0} tris  ·  _CutB (grey): {cutTrisB:N0} tris — the boundary follows the existing triangulation", EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(cutTrisA == 0 || cutTrisB == 0 || string.IsNullOrEmpty(outGlb)))
                            if (GUILayout.Button(new GUIContent($"Cut  →  {(string.IsNullOrEmpty(outGlb) ? "(set the Output GLB)" : Path.GetFileName(outGlb))}",
                                    "Writes the output GLB with this ONE part split into _CutA/_CutB children. The source file is never touched. " +
                                    "To cut again (the bow off the deck piece, say), point Source GLB at the output and re-Probe."), GUILayout.Height(24)))
                                DoPlaneCut();
                        if (GUILayout.Button("Close", GUILayout.Width(60), GUILayout.Height(24))) ExitCutMode();
                    }
                }
            }

            if (inst != null || CutModeActive)
            {
                EditorGUILayout.LabelField(CutModeActive
                    ? "Cut preview   (drag = orbit · middle/right-drag = pan · scroll = zoom — yellow = _CutA, grey = _CutB)"
                    : "Preview   (drag = orbit · middle/right-drag = pan · scroll = zoom · click a part row to highlight)", EditorStyles.miniBoldLabel);
                var rect = GUILayoutUtility.GetRect(200f, 4000f, 600f, 600f, GUILayout.ExpandWidth(true));
                HandlePreviewInput(rect);
                if (Event.current.type == EventType.Repaint) RenderPreview(rect);
            }
            else if (rows.Count > 0)
                EditorGUILayout.LabelField("  (no preview — the Blender probe export failed or is still pending; the list and Split still work)", EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(chosen == 0 || string.IsNullOrEmpty(outGlb)))
                if (GUILayout.Button(new GUIContent($"Split {chosen} checked part(s)  →  {(string.IsNullOrEmpty(outGlb) ? "(set the Output GLB)" : Path.GetFileName(outGlb))}",
                        "Writes the output GLB with ONLY the checked parts exploded into _Part_NNN children. The source file is never touched."), GUILayout.Height(28)))
                    SplitChecked();

            // ---- FUSE (2026-09-15): the opposite of Split — every fuse GROUP (rows sharing a ⊕ letter) becomes ONE welded
            // shell with consistent winding, at the source, so the Lab, the Factory and the bake all see a whole hull. ----
            EditorGUILayout.Space(4);
            int fusedRows = rows.Count(r => !string.IsNullOrEmpty(r.fuse));
            var fuseGroups = rows.Where(r => !string.IsNullOrEmpty(r.fuse)).Select(r => r.fuse).Distinct().OrderBy(g => g).ToList();
            weldPermille = EditorGUILayout.Slider(new GUIContent("Fuse — weld seams closer than (‰ of length)",
                "When fusing, vertices of the checked parts closer than this (in thousandths of the model's longest extent) become ONE vertex — " +
                "so plates that merely touch turn into one connected surface (a UV seam or a hard edge keeps its own vertex; connectivity is by " +
                "position regardless). START AT 0 = only exactly coincident positions merge — the Teutonic's plates already touch exactly, and " +
                "that alone found and fixed its hole. Raise it only for sources whose plates leave gaps, knowing that every triangle smaller " +
                "than the distance collapses (kept, zero-area, no longer connecting anything): at 0.5‰ the Teutonic lost 1,564 rivet-sized " +
                "faces AND its hull island broke apart. The result line reports the collapsed count."), weldPermille, 0f, 5f);
            using (new EditorGUILayout.HorizontalScope())
            {
                bool sidecarExists = File.Exists(FuseSidecarPath(srcFile) ?? "");
                using (new EditorGUI.DisabledScope(fusedRows == 0 && !sidecarExists))   // with no letters AND a sidecar on disk, saving means "clear it" (review of 0a8b56e)
                    if (GUILayout.Button(new GUIContent("Save groups", $"Writes the ⊕ letters to {Path.GetFileName(srcFile)}.fuse.txt next to the source (a Fuse writes it too); the first Probe of a file restores them from there. With no letters marked this removes the file."), GUILayout.Width(100)))
                    {
                        WriteFuseSidecar(srcFile);
                        status = fusedRows == 0 ? $"Groupings cleared: {FuseSidecarPath(srcFile)} removed" : $"Groupings saved: {FuseSidecarPath(srcFile)}";
                    }
                using (new EditorGUI.DisabledScope(!File.Exists(FuseSidecarPath(srcFile) ?? "")))
                    if (GUILayout.Button(new GUIContent("Load groups", "Reads the ⊕ letters back from the sidecar next to the source, by part name and node index (a name shared by several parts is refused unless the index settles it)."), GUILayout.Width(100)))
                    {
                        int n = ApplyFuseSidecar(rows, out int refused);
                        status = $"Groupings loaded: {n} part(s) marked from {FuseSidecarPath(srcFile)}" + (refused > 0 ? $" — {refused} line(s) fit no single part (see the console)" : "");
                    }
                EditorGUILayout.LabelField(fusedRows == 0 ? " " : $"{fusedRows} marked in {fuseGroups.Count} group(s): {string.Join("  ", fuseGroups.Select(g => "⊕" + g + "×" + rows.Count(r => r.fuse == g)))}", EditorStyles.miniLabel);
            }
            using (new EditorGUI.DisabledScope(fusedRows == 0 || string.IsNullOrEmpty(outGlb)))
                if (GUILayout.Button(new GUIContent(fusedRows == 0
                            ? "Fuse — mark parts with a ⊕ letter first (popup per row, or keys A–H on the highlighted row)"
                            : $"Fuse {fusedRows} marked part(s) in {fuseGroups.Count} group(s) ({string.Join(", ", fuseGroups.Select(g => "⊕" + g + "×" + rows.Count(r => r.fuse == g)))}) into one shell each  →  {(string.IsNullOrEmpty(outGlb) ? "(set the Output GLB)" : Path.GetFileName(outGlb))}",
                        "Joins the parts of each ⊕ group into ONE mesh in the output GLB, welds their seams, makes the winding consistent by MAJORITY across each " +
                        "welded island (the minority of faces reversed to agree with the rest), and judges direction once where it can be judged: a shell, " +
                        "a thin solid or a convex plating region by its signed volume about its own centroid (inside-out = reversed whole), a FLAT sheet " +
                        "(a deck, a bulwark) by the inside-out score. Mirrored instances (negative node scale) are read with the winding they render with. The cure for " +
                        "a hull authored as dozens of separate plates — see-through, a hole in its side, gaps under any reduction. Triangles are preserved " +
                        "exactly; the source parts keep their transforms and children and lose only their mesh. The source file is never touched."), GUILayout.Height(28)))
                    FuseMarked();
        }

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
        EditorGUILayout.EndScrollView();
    }

    void Probe()
    {
        if (Analyze()) BuildPreviewViaBlender();
    }

    // Path identity for the source-switch hygiene: separator- and case-insensitive (Windows paths), so retyping
    // the same file with the other slash style is not read as a switch.
    static bool SamePath(string a, string b) =>
        string.Equals((a ?? "").Replace('\\', '/').Trim(), (b ?? "").Replace('\\', '/').Trim(), StringComparison.OrdinalIgnoreCase);

    // The list half of Probe: pure C#, fast enough to re-run live when the merge slider moves. Checked state
    // survives a recount by part name (island counts change; the chosen parts don't). NATURAL name order:
    // Object_2 follows Object_1 and Object_10 comes after Object_9 — trailing digits compare as numbers.
    bool Analyze()
    {
        // kept-state keyed by NODE INDEX (review round 2): keying by name re-checked every duplicate namesake.
        var kept = new HashSet<int>(rows.Where(r => r.split).Select(r => r.nodeIndex));
        // FUSE LETTERS survive too (2026-09-15, user: "it doesn't seem to be able to save the groupings, only the
        // checkboxes"): the merge slider re-analyzes and used to rebuild every row blank. Same key. And a re-Probe of a
        // file with NO letters in memory restores them from the sidecar the last Fuse wrote (<source>.fuse.txt).
        var keptFuse = rows.Where(r => !string.IsNullOrEmpty(r.fuse)).ToDictionary(r => r.nodeIndex, r => r.fuse);
        // The sidecar is consulted only when this file is being loaded INTO the window (no rows yet, or rows of another
        // file) — a re-Probe or a slider move of a file whose letters the user cleared keeps them cleared (review of
        // 0a8b56e: the old rule "no letters in memory" reloaded the sidecar over a deliberate clear). "Load groups" is
        // the explicit way back.
        bool initialLoad = rows.Count == 0 || !SamePath(probedFile, srcFile);
        try
        {
            rows = GlbDisconnectedParts.Analyze(File.ReadAllBytes(srcFile), mergePct / 100.0)
                .Select(p => new Row { nodeIndex = p.NodeIndex, node = p.NodeName, mesh = p.MeshName, tris = p.Triangles, islands = p.Islands, blocked = p.Blocked, split = kept.Contains(p.NodeIndex),
                                       fuse = keptFuse.TryGetValue(p.NodeIndex, out string kf) ? kf : "" })
                .OrderBy(r => NaturalPrefix(r.node), StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => NaturalNumber(r.node))
                .ThenBy(r => r.node, StringComparer.OrdinalIgnoreCase).ToList();
            if (keptFuse.Count == 0 && initialLoad) ApplyFuseSidecar(rows, out _);
            foreach (var r in rows) if (r.islands <= 1 || r.blocked != null) r.split = false;   // no longer splittable at this distance
            int multi = rows.Count(r => r.islands > 1 && r.blocked == null);
            probedFile = srcFile;   // the rows now describe THIS file (the source-switch hygiene above keys on it)
            status = multi == 0 ? "Every part is a single attached island (at this merge distance) — nothing to split."
                   : $"{rows.Count} part(s); {multi} hold more than one island at merge distance {mergePct:0.#}%. Check the ones hiding junk; raise the slider if a part still shreds into fragments.";
            Repaint();
            return true;
        }
        catch (Exception e) { rows.Clear(); status = "Probe failed: " + e.Message; return false; }
    }

    // "Object_12" -> ("Object_", 12): sort names by prefix, then by the trailing number as a NUMBER.
    // The logic lives in the pure NaturalOrder kernel (EditorRules.cs) so NaturalOrderTests can lock it.
    static string NaturalPrefix(string s) => NaturalOrder.Prefix(s);
    static long NaturalNumber(string s) => NaturalOrder.Number(s);

    // ---- preview build: the Vehicle Lab's probe export (headless Blender writes an FBX of the model), imported
    // and instanced with AddSingleGO. Node names survive the trip, so rows highlight renderers by name. ----
    void BuildPreviewViaBlender()
    {
        DestroyPreview();
        try
        {
            string projRoot = Directory.GetParent(Application.dataPath).FullName;
            Directory.CreateDirectory(Path.Combine(projRoot, PreviewDir));
            string prevRel = PreviewDir + "/" + Path.GetFileNameWithoutExtension(srcFile) + "_wprobe.fbx";
            string prevFull = Path.Combine(projRoot, prevRel).Replace('\\', '/');
            string script = HafPackageContext.ToolPath("vehicle_rig.py");
            if (!File.Exists(script)) { status += "\n(no preview: Tools/vehicle_rig.py missing)"; return; }
            EditorUtility.DisplayProgressBar("Model Workshop", "Exporting preview via Blender…", 0.4f);
            var p = new System.Diagnostics.Process();
            p.StartInfo.FileName = UniversalBaker.FindBlender();
            p.StartInfo.Arguments = $"--background --python \"{script}\" -- probe \"{srcFile}\" \"{prevFull}\"";
            p.StartInfo.UseShellExecute = false; p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true; p.StartInfo.RedirectStandardError = true;
            p.Start();
            if (!UniversalBaker.RunBounded(p, 300000, out _, out _)) { status += "\n(no preview: Blender timed out)"; return; }
            if (!File.Exists(prevFull)) { status += "\n(no preview: Blender wrote no FBX)"; return; }
            AssetDatabase.ImportAsset(prevRel, ImportAssetOptions.ForceUpdate);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prevRel);
            if (prefab == null) { status += "\n(no preview: FBX import failed)"; return; }
            if (pru == null) pru = new PreviewRenderUtility();
            inst = Instantiate(prefab);
            pru.AddSingleGO(inst);
            boundsValid = false; previewPan = Vector2.zero; zoom = 1.5f;
        }
        catch (Exception e) { status += "\n(no preview: " + e.Message + ")"; }
        finally { EditorUtility.ClearProgressBar(); }
    }

    void DestroyPreview()
    {
        ExitCutMode();
        selectedIdx = -1;
        SelectRow("");
        if (inst != null) DestroyImmediate(inst);
        inst = null;
        if (pru != null) { pru.Cleanup(); pru = null; }
    }

    // ---- plane-cut mode: preview mesh built from the SOURCE bytes (ExtractPart), so what's yellow IS what
    // the cut writes to _CutA. The Blender turntable model hides while the cut preview is up. ----
    void EnterCutMode(Row row)
    {
        ExitCutMode();
        try { cutGeo = GlbDisconnectedParts.ExtractPart(File.ReadAllBytes(srcFile), row.nodeIndex); }
        catch (Exception e) { status = $"Plane cut unavailable for '{row.node}': {e.Message}"; cutGeo = null; return; }
        cutMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        var verts = new Vector3[cutGeo.Positions.Length / 3];
        for (int i = 0; i < verts.Length; i++)
            verts[i] = new Vector3(cutGeo.Positions[i * 3], cutGeo.Positions[i * 3 + 1], cutGeo.Positions[i * 3 + 2]);
        cutMesh.vertices = verts;
        cutMesh.subMeshCount = 2;
        if (cutMatA == null)
        {
            var sh = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
            cutMatA = new Material(sh) { color = new Color(1f, 0.85f, 0.1f), hideFlags = HideFlags.HideAndDontSave };
            cutMatB = new Material(sh) { color = new Color(0.55f, 0.55f, 0.6f), hideFlags = HideFlags.HideAndDontSave };
        }
        cutGO = new GameObject("__cutPreview") { hideFlags = HideFlags.HideAndDontSave };
        cutGO.AddComponent<MeshFilter>().sharedMesh = cutMesh;
        cutGO.AddComponent<MeshRenderer>().sharedMaterials = new[] { cutMatA, cutMatB };
        if (pru == null) pru = new PreviewRenderUtility();
        pru.AddSingleGO(cutGO);
        SetInstVisible(false);
        cutNormalsDone = false;
        UpdateCutPartition();
        boundsValid = false; previewPan = Vector2.zero;
        status = $"Plane cut mode on '{cutGeo.NodeName}': pick the axis, slide the plane, then Cut. Yellow → _CutA, grey → _CutB.";
    }

    void ExitCutMode()
    {
        if (cutGO != null) DestroyImmediate(cutGO);
        cutGO = null;
        if (cutMesh != null) DestroyImmediate(cutMesh);
        cutMesh = null;
        cutGeo = null; cutNormalsDone = false;
        SetInstVisible(true);
        boundsValid = false;
    }

    void SetInstVisible(bool on)
    {
        if (inst == null) return;
        foreach (var r in inst.GetComponentsInChildren<Renderer>(true)) if (r != null) r.enabled = on;
    }

    double CutPlaneValue() => cutGeo.Min[cutAxis] + (cutGeo.Max[cutAxis] - cutGeo.Min[cutAxis]) * Mathf.Clamp(cutPct, 0f, 100f) / 100.0;

    void UpdateCutPartition()
    {
        if (cutGeo == null || cutMesh == null) return;
        double v = CutPlaneValue();
        double cosLimit = Math.Cos(Mathf.Clamp(cutTiltDeg, 0f, 90f) * Math.PI / 180.0);
        var a = new List<int>(); var b = new List<int>();
        int[] t = cutGeo.Triangles; float[] p = cutGeo.Positions;
        for (int i = 0; i < t.Length; i += 3)
        {
            // the same rules as GlbDisconnectedParts.CutNodeByPlane/ByFacing, on the same world-space data
            int i0 = t[i] * 3, i1 = t[i + 1] * 3, i2 = t[i + 2] * 3;
            double c = (p[i0 + cutAxis] + p[i1 + cutAxis] + p[i2 + cutAxis]) / 3.0;
            bool sideA;
            if (cutRule == 0) sideA = c >= v;
            else
            {
                double ux = p[i1] - p[i0], uy = p[i1 + 1] - p[i0 + 1], uz = p[i1 + 2] - p[i0 + 2];
                double wx = p[i2] - p[i0], wy = p[i2 + 1] - p[i0 + 1], wz = p[i2 + 2] - p[i0 + 2];
                double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                double up = len < 1e-30 ? 0 : (cutAxis == 0 ? nx : cutAxis == 1 ? ny : nz) / len;
                sideA = Math.Abs(up) >= cosLimit && c >= v;
            }
            var side = sideA ? a : b;
            side.Add(t[i]); side.Add(t[i + 1]); side.Add(t[i + 2]);
        }
        cutTrisA = a.Count / 3; cutTrisB = b.Count / 3;
        cutMesh.SetTriangles(a, 0);
        cutMesh.SetTriangles(b, 1);
        if (!cutNormalsDone) { cutMesh.RecalculateNormals(); cutNormalsDone = true; }   // normals depend on the full set, not the partition — once is enough
        Repaint();
    }

    void DoPlaneCut()
    {
        if (File.Exists(outGlb) && !EditorUtility.DisplayDialog("Overwrite existing file?", outGlb, "Overwrite", "Cancel")) return;
        try
        {
            EditorUtility.DisplayProgressBar("Model Workshop", "Cutting…", 0.4f);
            var result = cutRule == 0
                ? GlbDisconnectedParts.CutFileByPlane(srcFile, outGlb, cutGeo.NodeIndex, cutAxis, CutPlaneValue())
                : GlbDisconnectedParts.CutFileByFacing(srcFile, outGlb, cutGeo.NodeIndex, cutAxis, cutTiltDeg, CutPlaneValue());
            if (!result.Changed) { status = "Nothing changed — the cut leaves every triangle on one side."; return; }
            foreach (var w in result.Warnings) Debug.LogWarning("[Workshop] " + w);
            status = $"Plane cut done: {result.Details.FirstOrDefault()}\n{outGlb}\nNext: open it in the Vehicle Lab — or cut again by pointing Source GLB at this output and re-Probing.";
        }
        catch (Exception e) { status = "Plane cut failed (source untouched): " + e.Message; Debug.LogException(e); }
        finally { EditorUtility.ClearProgressBar(); }
    }

    // Click a row → tint that part's renderer(s) yellow and frame them with context (the Vehicle Lab mechanism,
    // name-matched: probe part names ARE the glTF node names, with StartsWith for Blender's collision suffixes).
    void SelectRow(string name)
    {
        if (highlightedRenderers != null)
            for (int i = 0; i < highlightedRenderers.Count; i++)
                try { if (highlightedRenderers[i] != null) highlightedRenderers[i].sharedMaterials = highlightedOriginals[i]; } catch { }
        highlightedRenderers = null; highlightedOriginals = null;
        selectedRow = name;
        boundsValid = false;
        previewPan = Vector2.zero;
        if (inst == null || string.IsNullOrEmpty(name)) return;
        var all = inst.GetComponentsInChildren<Renderer>();
        // DUPLICATE NAMES (review round 3): split/check identity is the node INDEX, but the Blender preview
        // round-trip carries only names — namesake rows all light the same renderers. Rare in this pipeline
        // (the splitter uniquifies its output names); when it happens, say so instead of pretending precision.
        int namesakes = rows.Count(r => r.node == name);
        if (namesakes > 1)
            status = $"⚠ {namesakes} parts share the name '{name}' — the preview highlight shows all of them; checkbox and Split still target exactly the row you clicked (by node index).";
        // EXACT name PLUS Blender's collision-suffix form ("Object_2.001") — combined, not fallback (review
        // round 4: exact-only found the first namesake and starved the suffix branch, so duplicate rows lit the
        // same renderer). Never a mere prefix — a bare StartsWith made "Object_2" light up Object_20..Object_29.
        var hits = all.Where(x => x != null && (x.gameObject.name == name || x.gameObject.name.StartsWith(name + "."))).ToList();
        if (hits.Count == 0) return;
        if (highlightMat == null)
        {
            var sh = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            highlightMat = new Material(sh) { color = new Color(1f, 0.85f, 0.1f), hideFlags = HideFlags.HideAndDontSave };
        }
        highlightedRenderers = hits;
        highlightedOriginals = hits.Select(r => r.sharedMaterials).ToList();
        Bounds b = hits[0].bounds;
        foreach (var r in hits)
        {
            r.sharedMaterials = Enumerable.Repeat(highlightMat, r.sharedMaterials.Length).ToArray();
            b.Encapsulate(r.bounds);
        }
        bounds = b; bounds.Expand(bounds.size.magnitude * 0.6f + 0.1f); boundsValid = true;
        Repaint();
    }

    void HandlePreviewInput(Rect rect)
    {
        var e = Event.current;
        if (!rect.Contains(e.mousePosition)) return;
        if (e.type == EventType.ScrollWheel) { zoom = Mathf.Clamp(zoom * Mathf.Pow(1.12f, e.delta.y > 0 ? 1f : -1f), 0.1f, 50f); e.Use(); Repaint(); }   // 0.1 = camera at a fifth of the radius (was 0.2; user 2026-09-16: "double the maximum zoom-in" to judge plating up close)
        else if (e.type == EventType.MouseDrag && e.button == 0) { orbit += new Vector2(e.delta.x, -e.delta.y) * 0.7f; orbit.y = Mathf.Clamp(orbit.y, -89f, 89f); e.Use(); Repaint(); }
        else if (e.type == EventType.MouseDrag && (e.button == 1 || e.button == 2)) { previewPan += new Vector2(-e.delta.x, e.delta.y) * 0.0035f; e.Use(); Repaint(); }
    }

    void RenderPreview(Rect rect)
    {
        if (pru == null || (inst == null && !CutModeActive)) return;
        if (!boundsValid)
        {
            if (CutModeActive)
            {
                bounds = cutMesh.bounds;   // cutGO sits at the identity, so mesh bounds ARE world bounds
                boundsValid = true;
                fullRadius = bounds.extents.magnitude;
            }
            else
            {
                bool first = true;
                foreach (var r in inst.GetComponentsInChildren<Renderer>())
                { if (r == null) continue; if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds); }
                boundsValid = !first;
                if (boundsValid) fullRadius = bounds.extents.magnitude;
            }
        }
        if (!boundsValid) return;
        pru.BeginPreview(rect, GUIStyle.none);
        var cam = pru.camera;
        float radius = Mathf.Max(bounds.extents.magnitude, 0.1f);
        float dist = radius * 2f * zoom;
        var rot = Quaternion.Euler(-orbit.y, orbit.x, 0f);
        var lookAt = bounds.center + rot * new Vector3(previewPan.x, previewPan.y, 0f) * dist;
        cam.transform.position = lookAt + rot * (Vector3.back * dist);
        cam.transform.rotation = Quaternion.LookRotation(lookAt - cam.transform.position);
        cam.nearClipPlane = 0.01f; cam.farClipPlane = dist + Mathf.Max(radius, fullRadius) * 4f; cam.fieldOfView = 30f;
        pru.lights[0].intensity = 1.3f;
        pru.lights[0].transform.rotation = Quaternion.Euler(45f, 45f, 0f);
        if (pru.lights.Length > 1) pru.lights[1].intensity = 0.6f;
        pru.ambientColor = new Color(0.3f, 0.3f, 0.3f);
        cam.Render();
        GUI.DrawTexture(rect, pru.EndPreview(), ScaleMode.StretchToFill, false);
    }

    // THE GROUPINGS PERSIST beside the source GLB as `<source>.fuse.txt` — a header line, then `<letter>|<node
    // index>|<part name>` per line in row order (the first name of a group names the fused part). Written by every
    // successful Fuse, read when a file is first loaded into the window. Name AND index: the file stays
    // human-editable and survives a re-export that keeps names, while two parts with one name are told apart
    // (WorkshopRules.ResolveFuseSidecar — a name alone marked every namesake, review of 82088d4; the name is LAST
    // since 2026-09-16 so a '|' inside it is never mistaken for a field). Also written on demand by "Save groups".
    static string FuseSidecarPath(string glb) => string.IsNullOrEmpty(glb) ? null : glb + ".fuse.txt";
    void WriteFuseSidecar(string glb)
    {
        try
        {
            string path = FuseSidecarPath(glb); if (path == null) return;
            var lines = rows.Where(r => !string.IsNullOrEmpty(r.fuse) && !string.IsNullOrEmpty(r.node)).Select(r => WorkshopRules.SidecarLine(r.fuse, r.node, r.nodeIndex)).ToArray();
            if (lines.Length == 0) { if (File.Exists(path)) File.Delete(path); return; }
            File.WriteAllLines(path, new[] { WorkshopRules.SidecarHeader }.Concat(lines));   // v2: header, then letter|index|name (the name last, so a '|' in it is nothing to guess)
        }
        catch (Exception e) { Debug.LogWarning("[Workshop] could not write the fuse groupings sidecar: " + e.Message); }
    }
    // Marks `target` from the sidecar; returns how many rows got a letter, `refused` = lines that fit no single row
    // (each already logged as a warning). Rows the file does not mention are left as they are.
    int ApplyFuseSidecar(List<Row> target, out int refused)
    {
        refused = 0;
        try
        {
            string path = FuseSidecarPath(srcFile);
            if (path == null || !File.Exists(path)) return 0;
            var problems = new List<string>();
            var letters = WorkshopRules.ResolveFuseSidecar(File.ReadAllLines(path), target.Select(r => new KeyValuePair<int, string>(r.nodeIndex, r.node)).ToList(), problems);
            foreach (var r in target) if (letters.TryGetValue(r.nodeIndex, out string letter)) r.fuse = letter;
            foreach (string p in problems) Debug.LogWarning("[Workshop] fuse groupings sidecar: " + p);
            refused = problems.Count;
            return letters.Count;
        }
        catch (Exception e) { Debug.LogWarning("[Workshop] could not read the fuse groupings sidecar: " + e.Message); return 0; }
    }

    // Every ⊕ group becomes its own shell, chained through one in-memory GLB: FuseNodes never removes or reorders
    // nodes (fused sources only lose their mesh; the fused part is appended), so group B's node indices stay valid
    // in group A's output. One write at the end; the source file is never touched.
    void FuseMarked()
    {
        try { GlbDisconnectedParts.GuardPaths(srcFile, outGlb); }   // the file entry points refuse output == source; this path writes the bytes itself, so it asks the same guard
        catch (Exception e) { status = "Fuse refused (source untouched): " + e.Message; return; }
        if (File.Exists(outGlb) && !EditorUtility.DisplayDialog("Overwrite existing file?", outGlb, "Overwrite", "Cancel")) return;
        try
        {
            var groups = rows.Where(r => !string.IsNullOrEmpty(r.fuse)).GroupBy(r => r.fuse).OrderBy(g => g.Key).ToList();
            byte[] bytes = File.ReadAllBytes(srcFile);
            var lines = new List<string>(); int done = 0;
            foreach (var g in groups)
            {
                EditorUtility.DisplayProgressBar("Model Workshop", $"Fusing group ⊕{g.Key} ({g.Count()} part(s))…", 0.2f + 0.6f * done / Math.Max(1, groups.Count));
                var picked = g.Select(r => r.nodeIndex).ToList();   // row order: the first becomes the fused part's name
                var result = GlbDisconnectedParts.FuseNodes(bytes, picked, weldPermille / 1000.0);
                foreach (var w in result.Warnings) Debug.LogWarning($"[Workshop] ⊕{g.Key}: " + w);
                if (!result.Changed) { lines.Add($"⊕{g.Key}: nothing fused ({string.Join("; ", result.Warnings)})"); continue; }
                bytes = result.Bytes; done++;
                lines.Add($"⊕{g.Key}: {result.Details.FirstOrDefault()}");
                Debug.Log($"[Workshop] ⊕{g.Key} {string.Join(" | ", result.Details)}");
            }
            if (done == 0) { status = "Nothing changed — no group produced a fused mesh (see warnings in the console)."; return; }
            File.WriteAllBytes(outGlb, bytes);
            WriteFuseSidecar(srcFile);   // the groupings, next to the source: a later Probe of this file restores them
            status = $"Fuse done ({done} group(s)):\n{string.Join("\n", lines)}\n{outGlb}\nNext: point Source GLB at this output and re-Probe to see each fused shell as one part, or open it in the Vehicle Lab.";
        }
        catch (Exception e) { status = "Fuse failed (source untouched): " + e.Message; Debug.LogException(e); }
        finally { EditorUtility.ClearProgressBar(); }
    }

    void SplitChecked()
    {
        if (File.Exists(outGlb) && !EditorUtility.DisplayDialog("Overwrite existing file?", outGlb, "Overwrite", "Cancel")) return;
        try
        {
            EditorUtility.DisplayProgressBar("Model Workshop", "Splitting checked parts…", 0.4f);
            var picked = new HashSet<int>(rows.Where(r => r.split).Select(r => r.nodeIndex));
            var result = GlbDisconnectedParts.SplitFile(srcFile, outGlb, picked, mergePct / 100.0);
            if (!result.Changed) { status = "Nothing changed — the checked parts produced no split (see warnings in the console)."; return; }
            foreach (var w in result.Warnings) Debug.LogWarning("[Workshop] " + w);
            status = $"Split done: {result.NodesSplit} part(s) → {result.ChildPartsCreated} sub-parts, {result.SourceTriangles:N0} triangles preserved.\n{outGlb}\nNext: open it in the Vehicle Lab, Probe parts, and mark the junk islands Ignore.";
            Debug.Log($"[Workshop] {string.Join(" | ", result.Details)}");
        }
        catch (Exception e) { status = "Split failed (source untouched): " + e.Message; Debug.LogException(e); }
        finally { EditorUtility.ClearProgressBar(); }
    }
}
