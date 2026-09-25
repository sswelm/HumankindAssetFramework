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

// TWO WINDOWS, ONE IMPLEMENTATION (2026-09-18, user: "the Model Workshop has too many responsibilities, which makes it
// cluttered — split it up into two screens, one for cutting and one for merging"): the SPLITTER (island split + plane cut)
// and the FUSER (⊕ groups → welded shells) share the file pickers, the probe, the filtered part list, the preview, the
// mirror finder and the keyboard sweep; each shows only its own column, keys and buttons. The one-step Generate (fuse
// AND split) went with the split — chain the outputs instead: the ⊕ letters travel through a cut, so either order works.
public abstract class ModelWorkshopWindow : EditorWindow
{
    protected abstract bool Fusing { get; }   // the Fuser: ⊕ letters, weld, Fuse — else the Splitter: Split checkboxes, plane cut, Split

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
        public bool tear;        // TEAR (2026-09-25): Split, plus a cut wherever the other side of the ship has the welded object as a separate island; T key or the row popup
        public bool delete;      // MARKED FOR DELETION (2026-09-18): the part loses its mesh in the output — Delete key or the row popup, in both windows; exclusive with split and fuse
        public string fuse = ""; // FUSE GROUP letter A..Z (2026-09-15; A..H until 09-16): rows sharing a letter fuse into one shell each; "" = none
        public int verts;        // for the list filters (2026-09-16): vertex count and the world bbox (min/max null = unmeasured, never hidden)
        public float[] min, max;
    }
    static GUIStyle wrappedButton;
    static GUIStyle WrappedButton => wrappedButton ?? (wrappedButton = new GUIStyle(GUI.skin.button) { wordWrap = true });   // built lazily: GUI.skin exists only inside OnGUI
    static readonly string[] FuseLabels = new[] { "–" }.Concat(Enumerable.Range(0, 26).Select(i => "⊕" + (char)('A' + i))).Concat(new[] { "✕ Delete" }).ToArray();   // the Fuser's row popup: no group, A–Z (was A–H; user 2026-09-16: a ship has more than eight boats), or marked for deletion; keys A–Z set it, – / 0 / Backspace clear
    const int DeleteEntry = 27;   // FuseLabels index of "✕ Delete"
    static readonly string[] SplitLabels = { "–", "Split", "Tear", "Delete" }, WholeLabels = { "–", "Tear", "Delete" };   // the Splitter's row popup (a whole part cannot be split, but it can be torn)

    [SerializeField] string srcFile = "";
    [SerializeField] string outGlb = "";
    [SerializeField] bool outGlbAuto = true;   // output path is auto-derived from srcFile and TRACKS it until the user edits the field to something else (external review of PR #22)
    [SerializeField] string probedFile = "";   // the file `rows` (and the checks/preview/output path) were built from — serialized so a domain reload doesn't read surviving rows as stale (review finding 8)
    [SerializeField] List<Row> rows = new List<Row>();
    [SerializeField] bool hideWhole = false;   // filter: hide "1 island — already whole" rows (nothing to split there)
    // LIST FILTERS (2026-09-16, the Vehicle Lab's sliders brought over — user: "make these selection tools also available in
    // the Model Workshop"): the same bands, over the node bbox the analyzer reads from the accessors. Height is glTF +Y, the
    // side axis is the model's shorter horizontal extent (as the fuse's belly axis). The flat-surface filter came over on
    // 2026-09-18 and is measured on the preview meshes; the visibility filter stays Lab-only, needing the Blender probe's
    // escape rays, which the Workshop does not run.
    [SerializeField] int minVerts = 1;
    [SerializeField] float minPartSize = 0f;
    // The UPPER size bound (2026-09-19, user: "a hide parts over size filter so I can find all the small items, so I
    // can delete them"): the mirror of the slider above, so the two bracket a size band. Its natural use is alone —
    // drag it down and only the clutter is left in the list, ready for the Delete key or the row popup.
    [SerializeField] float maxPartSize = 1e9f;
    [SerializeField] float minHeight = -1e9f, maxHeight = 1e9f, minWidth = -1e9f, maxWidth = 1e9f;   // clamped into the model's span each frame: a fresh model hides nothing
    [SerializeField] int showOnly = 0;
    [SerializeField] float minFlatPct = 0f;   // 0 = off — hide parts whose LEVEL-surface share (% of area within 30° of horizontal) is below this: the Lab's deck finder, brought over 2026-09-18 ("group D is still missing part of the deck")
    Dictionary<int, float> flatShare; GameObject flatShareFor;   // per-NODE level-surface share over the preview meshes (the preview is one object per node since 2026-09-19 — no name aliasing needed)
    [SerializeField] string showOnlyLetter = "";   // "Show only" can also be ONE fuse group (user 2026-09-16): the popup lists every letter in use after the fixed kinds
    static readonly string[] ShowOnlyOptions = { "None (all parts)", "Checked for Split", "In a fuse group", "Not in a fuse group", "More than one island", "Already whole", "Skipped by the analyzer", "Marked for deletion" };
    // DISTANCE MERGE (2026-09-06, the 602-island rope): topology alone shreds segmented geometry into hundreds
    // of 3-vert parts millimetres apart. Islands within this % of a part's own diagonal count as ONE part, so
    // only genuinely distant geometry — the floating junk — separates. 0 = pure topology.
    [SerializeField] float mergePct = 1f;
    // Every list filter back to "hide nothing". Called when a file is probed INTO the window (2026-09-19, user: "ensure
    // that the first time a model is probed it minimizes the filter allowing you to see all parts") — the sliders keep
    // their values across files and are only clamped into the new model's span, so four sliders left at the ends of a
    // metre-scale ship arrived at the ends of a centimetre-scale one and hid all 113 parts ("why don't I see any
    // parts?"). A re-Probe or a slider move on the SAME file keeps the settings; the "Show all" button is the manual way.
    void ResetFilters()
    {
        minVerts = 1; minPartSize = 0f; maxPartSize = 1e9f; minFlatPct = 0f;
        minHeight = -1e9f; maxHeight = 1e9f; minWidth = -1e9f; maxWidth = 1e9f;
        showOnly = 0; showOnlyLetter = ""; hideWhole = false;
    }
    // FUSE (2026-09-15): the checked parts become ONE welded shell with consistent winding — the fix for a hull
    // authored as separate plates (the Teutonic: see-through, a hole in its side, gaps under any reduction).
    // Seam vertices closer than this (in thousandths of the model's length) become one vertex. DEFAULT 0 = exactly
    // coincident positions only: measured on the Teutonic, its plates already touch exactly (the majority rule found
    // the 1,613-face hole at 0), while 0.5‰ collapsed 1,564 rivet-sized triangles and broke the hull island apart.
    [SerializeField] float weldPermille = 0f;
    // CHECK MIRRORED PARTS (2026-09-19, the Confederate frigate; user: "make it an option"). Off by default: every file
    // fuses exactly as before. On, the fuse compares each mirrored part with the plain parts it is welded to and, when
    // they clearly agree the file stores mirrored parts already facing outward, undoes the glTF reversal that would turn
    // them inward (see GlbDisconnectedParts, step 3b). The fuse report and a warning say when it would help.
    [SerializeField] bool checkMirrored = false;
    bool analyzePending;   // slider moved: recount on the first Layout pass after the drag releases
    [SerializeField] Vector2 scroll;
    readonly List<Rect> rowRects = new List<Rect>();   // per shown row, measured at Repaint: the ↑/↓ keys keep the highlight in view by these, not by an assumed row height
    [NonSerialized] int shownSignature;                // the shown list's identity (which rows, in which order) at the last Repaint: a change reveals the highlighted row
    float listViewHeight = 330f;
    [SerializeField] Vector2 windowScroll;   // the WHOLE window: header + list (≤330) + preview (600) + Split/Fuse controls overflow a short window, and the Fuse row was cut off with no way to reach it (user 2026-09-16)
    string status = "Pick a GLB and press Probe parts.";

    // ---- turntable preview state (the Vehicle Lab's proven camera, minus clips/waterline) ----
    GameObject inst; PreviewRenderUtility pru;
    readonly Dictionary<int, Renderer> previewByNode = new Dictionary<int, Renderer>();   // node index -> its preview renderer (the identity rows and preview share)
    readonly List<Mesh> previewAssets = new List<Mesh>();                                  // built meshes, destroyed with the preview (runtime Unity objects never GC)
    readonly Dictionary<Color, Material> previewMats = new Dictionary<Color, Material>();
    // UN-MIRROR (2026-09-19, user: "could you make it a checkbox allowing me to unmirror it?"): glTF is right-handed
    // and Unity left-handed, so handing the file's coordinates over unchanged shows the model as a MIRROR — screen-left
    // is the file's starboard. Ticking this negates X and flips every triangle to compensate, which is measured to keep
    // the surface solid (drill_unity_facing.py on the Romanic split: 1.0 % of struck cells render back-facing either
    // way; negating WITHOUT the flip would invert the whole ship). Off by default so the view does not move under
    // anyone mid-session. Nothing else changes: the sliders, the mirror finder and every output read file coordinates.
    [SerializeField] bool unmirror = false;
    [SerializeField] Vector2 orbit = new Vector2(30f, -20f);
    [SerializeField] float zoom = 1.5f;
    Vector2 previewPan;
    Bounds bounds; bool boundsValid; float fullRadius;
    string selectedRow = "";   // the highlighted part's NAME (labels only — the preview is matched by node index)
    int selectedIdx = -1;      // the selected ROW's identity (node index — names can be duplicated)
    Material highlightMat;
    List<Renderer> highlightedRenderers; List<Material[]> highlightedOriginals;

    // ---- plane-cut state (2026-09-13, the Bremen deck: hull and deck are ONE welded island — nothing for the
    // island splitter to do). The cut preview is built from the SOURCE GLB's own bytes (ExtractPart — the same
    // world-space data the turntable is built from since 2026-09-19), so the two-color partition on screen is
    // exactly the triangle partition the cut writes — no axis-convention mapping to get wrong. ----
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
        windowScroll = EditorGUILayout.BeginScrollView(windowScroll, GUIStyle.none, GUI.skin.verticalScrollbar);   // vertical only: a bar appears when the window is shorter than its content, and no long line can push the window wide; the preview keeps its scroll-wheel zoom (it Use()s the event first)
        if (Fusing)
        {
            EditorGUILayout.LabelField("Model Fuser — weld the parts of each ⊕ group into ONE shell with consistent winding", EditorStyles.boldLabel);
            // two short lines, not one long one: a single long label sets the window's minimum width (user 2026-09-16)
            EditorGUILayout.LabelField("For a hull authored as separate plates (see-through, holes, gaps under reduction): mark the plates with one letter, Fuse, then feed the output to the Vehicle Lab.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("Triangles are preserved exactly; the source parts keep their transforms and children and lose only their mesh. To cut as well, open the output in the Model Cutter: each shell keeps its ⊕ letter there.", EditorStyles.wordWrappedMiniLabel);
        }
        else
        {
            EditorGUILayout.LabelField("Model Cutter — cut away the parts you do not need; split a part into its disconnected islands, or plane-cut a connected one", EditorStyles.boldLabel);
            // two short lines, not one long one: a single long label sets the window's minimum width (user 2026-09-16)
            EditorGUILayout.LabelField("For a part whose junk islands share a mesh with real geometry: split ONLY that part, then mark the junk Ignore in the Vehicle Lab.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("Lossless — vertex data, materials, skins and animations are preserved; only the checked parts gain _Part_NNN children. A CONNECTED part (1 island) can instead be plane-cut in two. To fuse as well, open the output in the Model Fuser.", EditorStyles.wordWrappedMiniLabel);
        }

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
        // the suffix follows the operation — _split, or _cut while the cut panel is open — and chains: cutting ship_cut.glb
        // proposes ship_cut2.glb (user 2026-09-16: two cuts in a row needed two names typed by hand)
        string autoOut = string.IsNullOrEmpty(srcFile) ? ""
            : Path.Combine(Path.GetDirectoryName(srcFile), WorkshopRules.NextOutputName(Path.GetFileNameWithoutExtension(srcFile), Fusing ? "_fused" : CutModeActive ? "_cut" : "_split") + ".glb").Replace('\\', '/');
        if (outGlbAuto && !string.IsNullOrEmpty(autoOut)) outGlb = autoOut;
        using (new EditorGUILayout.HorizontalScope())
        {
            string typedOut = EditorGUILayout.TextField(new GUIContent("Output GLB", (Fusing ? "Where the fused copy is written" : "Where the split copy is written") + " — feed THIS file to the Vehicle Lab afterwards. Auto-follows the source file until you edit it."), outGlb);
            if (typedOut != outGlb) { outGlb = typedOut; outGlbAuto = SamePath(typedOut, autoOut); }
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(srcFile)))
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string p = EditorUtility.SaveFilePanel("Write split GLB", Path.GetDirectoryName(srcFile), Path.GetFileNameWithoutExtension(srcFile) + (Fusing ? "_fused" : "_split"), "glb");
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
            int deleted = rows.Count(r => r.delete);
            EditorGUILayout.LabelField(Fusing
                ? $"Parts ({rows.Count} node(s)) — click a row to highlight it below; give the parts of one shell the same ⊕ letter:"
                : $"Parts ({rows.Count} node(s), {splittable} with more than one island) — click a row to highlight it below; check the parts to split:", EditorStyles.boldLabel);
            if (!Fusing)
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Check all splittable", GUILayout.Width(140))) foreach (var r in rows) r.split = r.islands > 1 && r.blocked == null;
                    if (GUILayout.Button("Uncheck all", GUILayout.Width(100))) foreach (var r in rows) r.split = false;
                    hideWhole = EditorGUILayout.ToggleLeft(new GUIContent("Hide already-whole parts",
                        "Hide the rows with a single island — there is nothing to split in them, they only pad the list."), hideWhole, GUILayout.Width(180));
                    if (GUILayout.Button(new GUIContent("Show all", "Every list filter back to 'hide nothing' — the sliders, 'Show only' and this toggle. Marks are kept. (A file probed into the window for the first time starts this way.)"), GUILayout.Width(70)))
                        ResetFilters();
                    bool marksOnDisk = File.Exists(MarksSidecarPath(srcFile) ?? "");
                    using (new EditorGUI.DisabledScope(chosen == 0 && deleted == 0 && !marksOnDisk))   // with no marks AND a file on disk, saving means "clear it"
                        if (GUILayout.Button(new GUIContent("Save marks", $"Writes the Split checks and deletion marks to {Path.GetFileName(srcFile)}.marks.txt next to the source (a Split writes it too); the first Probe of the file reads it back."), GUILayout.Width(90)))
                        {
                            WriteMarksSidecar(srcFile);
                            status = chosen == 0 && deleted == 0 ? $"Marks cleared: {MarksSidecarPath(srcFile)} removed" : $"Marks saved: {MarksSidecarPath(srcFile)}";
                        }
                    using (new EditorGUI.DisabledScope(!marksOnDisk))
                        if (GUILayout.Button(new GUIContent("Load marks", "Reads the Split checks and deletion marks back from the sidecar next to the source, by part name and node index."), GUILayout.Width(90)))
                        {
                            int n = ApplyMarksSidecar(rows, out int refused);
                            status = $"Marks loaded: {n} part(s) marked from {MarksSidecarPath(srcFile)}" + (refused > 0 ? $" — {refused} line(s) fit no single part (see the console)" : "");
                        }
                    // A 300-island rope part is a legitimate but LOUD choice — say what a check costs before Split.
                    EditorGUILayout.LabelField(chosen > 0 ? $"{chosen} checked → +{rows.Where(r => r.split).Sum(r => r.islands) - chosen} new part(s) in the output" : " ", EditorStyles.miniLabel);
                }
            // the sliders auto-fit the model's span, padded a hair past the outermost part (the Lab's finding 2026-08-01: an exact
            // clamp rounds slightly inside and hides the edge part at rest)
            var boxed = rows.Where(r => r.min != null).ToList();
            int sideAxis = 2;
            if (boxed.Count > 0)
            {
                float ex = boxed.Max(r => r.max[0]) - boxed.Min(r => r.min[0]), ez = boxed.Max(r => r.max[2]) - boxed.Min(r => r.min[2]);
                sideAxis = ex >= ez ? 2 : 0;   // the LONGER horizontal extent is the length; the side axis is the other one
            }
            float Centre(Row r, int axis) => 0.5f * (r.min[axis] + r.max[axis]);
            minVerts = EditorGUILayout.IntSlider(new GUIContent("Hide parts under (verts)", "Rows with fewer vertices than this are hidden from the list (they are still in the file and still fuse/split if marked)."), minVerts, 1, 2000);
            float biggestPart = boxed.Count > 0 ? boxed.Max(r => Mathf.Max(r.max[0] - r.min[0], r.max[1] - r.min[1], r.max[2] - r.min[2])) : 1f;
            minPartSize = EditorGUILayout.Slider(new GUIContent("Hide parts under (size)", "Rows whose largest bbox dimension is below this are hidden. Drop the verts slider and raise this to find LARGE parts with few vertices."), minPartSize, 0f, biggestPart);
            // LOGARITHMIC, unlike its neighbour (measured 2026-09-19): part sizes span four decades on a split model
            // (the Romanic's 1,796 parts run 0.017 to 173), so a linear slider spends 99 % of its travel doing nothing
            // and crosses "keeps 219 parts" to "keeps 1,361" inside one pixel. Even travel per decade instead, with the
            // number box for an exact threshold. The lower slider is left linear: it is dragged UP to find big parts,
            // where the range is not compressed.
            float smallestPart = boxed.Count > 0 ? boxed.Min(r => Mathf.Max(r.max[0] - r.min[0], r.max[1] - r.min[1], r.max[2] - r.min[2])) : 0f;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Hide parts over (size)",
                    "Rows whose largest bbox dimension is above this are hidden — drag it down and only the small clutter is left, ready for the Delete key. " +
                    "The travel is logarithmic because part sizes span decades; the box takes an exact figure. Pair it with the slider above to keep one size band."));
                // a hair ABOVE the biggest part, so at rest the threshold cannot round just inside it and hide the
                // largest row (the Lab's 2026-08-01 finding, which is why the height sliders carry a pad too)
                float lo = Mathf.Max(1e-4f, smallestPart), hi = Mathf.Max(lo * 1.0001f, biggestPart * 1.001f);
                float cur = Mathf.Clamp(maxPartSize, lo, hi);
                float t = (Mathf.Log(cur) - Mathf.Log(lo)) / (Mathf.Log(hi) - Mathf.Log(lo));
                t = GUILayout.HorizontalSlider(t, 0f, 1f);
                maxPartSize = Mathf.Exp(Mathf.Lerp(Mathf.Log(lo), Mathf.Log(hi), t));
                maxPartSize = Mathf.Clamp(EditorGUILayout.FloatField(maxPartSize, GUILayout.Width(64)), 0f, hi);
            }
            if (boxed.Count > 0)
            {
                float yLo = boxed.Min(r => Centre(r, 1)), yHi = boxed.Max(r => Centre(r, 1)), yPad = Mathf.Max(0.02f, (yHi - yLo) * 0.02f);
                minHeight = EditorGUILayout.Slider(new GUIContent("Hide parts below (height)", "Parts whose bbox centre is below this height are hidden. Slide up past the hull to isolate deck-level parts."), Mathf.Clamp(minHeight, yLo - yPad, yHi + yPad), yLo - yPad, yHi + yPad);
                maxHeight = EditorGUILayout.Slider(new GUIContent("Hide parts above (height)", "Parts whose bbox centre is above this height are hidden. Slide down to strip the superstructure."), Mathf.Clamp(maxHeight, yLo - yPad, yHi + yPad), yLo - yPad, yHi + yPad);
                float wLo = boxed.Min(r => Centre(r, sideAxis)), wHi = boxed.Max(r => Centre(r, sideAxis)), wPad = Mathf.Max(0.02f, (wHi - wLo) * 0.02f);
                minWidth = EditorGUILayout.Slider(new GUIContent("Hide parts left of (side)", "Parts whose bbox centre is on the far side of this across the beam are hidden — bracket with the next slider to keep one side (the starboard hull plates, say)."), Mathf.Clamp(minWidth, wLo - wPad, wHi + wPad), wLo - wPad, wHi + wPad);
                maxWidth = EditorGUILayout.Slider(new GUIContent("Hide parts right of (side)", "Parts whose bbox centre is beyond this across the beam are hidden."), Mathf.Clamp(maxWidth, wLo - wPad, wHi + wPad), wLo - wPad, wHi + wPad);
            }
            // THE DECK FINDER (2026-09-18, user: "group D in the Vehicle Lab still seems to be missing part of the deck"): the
            // Lab's flat-surface filter, measured on the preview meshes the Workshop builds from the GLB. Without a preview every
            // part passes — a filter never hides what it cannot measure.
            minFlatPct = EditorGUILayout.Slider(new GUIContent("Only flat parts (≥ % level)",
                "The deck finder: hide parts whose surface area is less than this % LEVEL (within 30° of horizontal, measured on the " +
                "preview meshes). Slide up to ~60 and the walking decks, platforms and hatch tops remain while masts, hull plating " +
                "and rigging vanish; combine with the height sliders to pick one deck level. 0 = off. Needs the preview (Probe parts " +
                "builds it); parts the preview can't measure stay visible."), minFlatPct, 0f, 100f);
            var lettersInUse = rows.Where(r => !string.IsNullOrEmpty(r.fuse)).Select(r => r.fuse).Distinct().OrderBy(l => l).ToList();
            // each window lists its own kinds (the Splitter: checked / islands; the Fuser: groups / islands, then every letter in use)
            int[] kinds = Fusing ? new[] { 0, 2, 3, 7, 4, 5, 6 } : new[] { 0, 1, 7, 4, 5, 6 };
            if (Array.IndexOf(kinds, showOnly) < 0) showOnly = 0;
            var showOptions = kinds.Select(k => ShowOnlyOptions[k]).Concat(Fusing ? lettersInUse.Select(l => $"Group ⊕{l}  ({rows.Count(r => r.fuse == l)} part(s))") : Enumerable.Empty<string>()).ToArray();
            int showIdx = Fusing && !string.IsNullOrEmpty(showOnlyLetter) && lettersInUse.Contains(showOnlyLetter) ? kinds.Length + lettersInUse.IndexOf(showOnlyLetter) : Array.IndexOf(kinds, showOnly);
            int picked = EditorGUILayout.Popup(new GUIContent("Show only", Fusing ? "Filter the list to one kind of row, or to ONE fuse group (every letter in use is listed). Marks on hidden rows are kept."
                                                                                  : "Filter the list to one kind of row. Checks on hidden rows are kept."), showIdx, showOptions);
            if (picked >= kinds.Length) { showOnly = 0; showOnlyLetter = lettersInUse[picked - kinds.Length]; }
            else { showOnly = kinds[picked]; showOnlyLetter = ""; }
            bool Passes(Row r)
            {
                if (!Fusing && hideWhole && r.islands <= 1 && r.blocked == null) return false;   // the Splitter's toggle; the Fuser has no use for island counts
                if (Fusing && !string.IsNullOrEmpty(showOnlyLetter) && r.fuse != showOnlyLetter) return false;
                switch (showOnly)
                {
                    case 1: if (!r.split && !r.tear) return false; break;
                    case 2: if (string.IsNullOrEmpty(r.fuse)) return false; break;
                    case 3: if (!string.IsNullOrEmpty(r.fuse)) return false; break;
                    case 4: if (r.islands <= 1 || r.blocked != null) return false; break;
                    case 5: if (r.islands != 1 || r.blocked != null) return false; break;
                    case 6: if (r.blocked == null) return false; break;
                    case 7: if (!r.delete) return false; break;
                }
                if (r.verts > 0 && r.verts < minVerts) return false;
                if (!FlatOk(r)) return false;
                if (r.min == null) return true;   // unmeasured: a filter never hides what it cannot measure
                float span = Mathf.Max(r.max[0] - r.min[0], r.max[1] - r.min[1], r.max[2] - r.min[2]);
                if (span < minPartSize || span > maxPartSize) return false;
                float h = Centre(r, 1), w = Centre(r, sideAxis);
                return h >= minHeight && h <= maxHeight && w >= minWidth && w <= maxWidth;
            }
            var shown = rows.Where(Passes).ToList();
            int hiddenRows = rows.Count - shown.Count;
            // AUTO-ADVANCE (user 2026-09-18): a mark that takes the highlighted row OUT of the filtered list ("Show only:
            // Group Z", then the letter changes) left the highlight on a row no longer drawn, and the next ↓ started over at
            // the FIRST row. The Lab's sweep idiom instead: the highlight moves to the row after it (before it at the end).
            // The list keeps its place — the next row slides up into the vacated slot, so no scroll is needed.
            void AdvanceIfHidden(int at)
            {
                if (at < 0 || at >= shown.Count || Passes(shown[at])) return;
                int next = WorkshopRules.NextHighlight(at, shown.Count);
                ExitCutMode(); selectedIdx = next >= 0 ? shown[next].nodeIndex : -1; SelectRow(next >= 0 ? shown[next] : null);
            }
            int advanceFrom = -1;   // a popup/checkbox change on the highlighted row inside the list loop: advanced after the loop
            if (hiddenRows > 0) EditorGUILayout.LabelField($"  {shown.Count} shown, {hiddenRows} hidden by the filters (marks on hidden rows are kept; {(Fusing ? "Fuse" : "Split")} acts on ALL marked rows)", EditorStyles.miniLabel);
            // KEYBOARD MARKING (2026-09-15, the Vehicle Lab's idiom): ↑/↓ move the highlight, A–Z put the highlighted row in
            // a fuse group, 0/Backspace clear it, Space toggles its Split checkbox — marking dozens of hull plates by mouse
            // was the complaint. The Workshop has no role hotkeys, so the letters are free here.
            EditorGUILayout.LabelField(Fusing
                ? "  Keys:  ↑/↓ = previous/next part   ·   A–Z = fuse group of the highlighted part (⊕ column)   ·   Delete = mark for deletion   ·   – / 0 / Backspace = no mark"
                : "  Keys:  ↑/↓ = previous/next part   ·   Insert = mark for split   ·   T = mark for tear   ·   Delete = mark for deletion   ·   Space = toggle split   ·   – / 0 / Backspace = no mark", EditorStyles.wordWrappedMiniLabel);
            var ev = Event.current;
            if (ev.type == EventType.KeyDown && shown.Count > 0 && !EditorGUIUtility.editingTextField)
            {
                int idx = shown.FindIndex(x => x.nodeIndex == selectedIdx);
                if (ev.keyCode == KeyCode.UpArrow || ev.keyCode == KeyCode.DownArrow)
                {
                    idx = ev.keyCode == KeyCode.DownArrow ? Mathf.Min(idx + 1, shown.Count - 1) : Mathf.Max(idx - 1, 0);
                    ExitCutMode(); selectedIdx = shown[idx].nodeIndex; SelectRow(shown[idx]);
                    RevealRow(idx);
                    GUIUtility.keyboardControl = 0;
                    ev.Use(); Repaint();
                }
                else if (Fusing && idx >= 0 && shown[idx].blocked == null && ev.keyCode >= KeyCode.A && ev.keyCode <= KeyCode.Z)
                {
                    shown[idx].fuse = ((char)('A' + (ev.keyCode - KeyCode.A))).ToString(); shown[idx].delete = false;
                    AdvanceIfHidden(idx);
                    GUIUtility.keyboardControl = 0;
                    ev.Use(); Repaint();
                }
                // MARKS BY KEY (2026-09-18, user: "an easy way to mark a unit for removal with the Del key … Insert should mark it for
                // split-up … Delete should also be active in the Fuser"): Delete toggles the deletion mark in both windows and
                // clears the other marks (a deleted part is neither split nor fused); Insert marks for split in the Splitter
                else if (idx >= 0 && ev.keyCode == KeyCode.Delete)
                {
                    Row d = shown[idx]; d.delete = !d.delete; if (d.delete) { d.split = false; d.tear = false; d.fuse = ""; }
                    AdvanceIfHidden(idx);
                    GUIUtility.keyboardControl = 0;
                    ev.Use(); Repaint();
                }
                else if (!Fusing && idx >= 0 && ev.keyCode == KeyCode.Insert && shown[idx].blocked == null && shown[idx].islands > 1)
                {
                    shown[idx].split = true; shown[idx].tear = false; shown[idx].delete = false;
                    AdvanceIfHidden(idx);
                    GUIUtility.keyboardControl = 0;
                    ev.Use(); Repaint();
                }
                else if (!Fusing && idx >= 0 && ev.keyCode == KeyCode.T && shown[idx].blocked == null)   // TEAR (2026-09-25): the T key, any part
                {
                    shown[idx].tear = true; shown[idx].split = false; shown[idx].delete = false;
                    AdvanceIfHidden(idx);
                    GUIUtility.keyboardControl = 0;
                    ev.Use(); Repaint();
                }
                else if (idx >= 0 && (ev.keyCode == KeyCode.Alpha0 || ev.keyCode == KeyCode.Keypad0 || ev.keyCode == KeyCode.Minus || ev.keyCode == KeyCode.KeypadMinus || ev.keyCode == KeyCode.Backspace))   // '-' too: it is what the popup shows for "no mark" (user 2026-09-16)
                {
                    if (Fusing) shown[idx].fuse = ""; else shown[idx].split = false;
                    shown[idx].delete = false;
                    AdvanceIfHidden(idx);
                    GUIUtility.keyboardControl = 0;
                    ev.Use(); Repaint();
                }
                else if (!Fusing && idx >= 0 && ev.keyCode == KeyCode.Space && shown[idx].blocked == null && shown[idx].islands > 1)
                {
                    shown[idx].split = !shown[idx].split; if (shown[idx].split) shown[idx].delete = false;
                    AdvanceIfHidden(idx);
                    GUIUtility.keyboardControl = 0;
                    ev.Use(); Repaint();
                }
            }
            listViewHeight = Mathf.Min(330, 22 * shown.Count + 8);
            if (Event.current.type == EventType.Repaint) rowRects.Clear();
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(listViewHeight));   // cap 220 -> 330 (2026-09-08 user request: +50% — a real ship's part list is dozens of rows)
            for (int ri = 0; ri < shown.Count; ri++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    Row r = shown[ri]; bool splitBefore = r.split, tearBefore = r.tear, deleteBefore = r.delete; string fuseBefore = r.fuse;
                    if (!Fusing)
                    {
                        // the Splitter's mark: nothing, Split (a part with more than one island), or Delete — a popup, as the user asked
                        // for the same reach by mouse as by key (2026-09-18); the analyzer's skipped parts can still be deleted
                        bool canSplit = r.islands > 1 && r.blocked == null;
                        string[] labels = canSplit ? SplitLabels : WholeLabels;
                        int tearEntry = labels.Length - 2;   // "Tear" sits just before "Delete" in both label sets
                        int si = r.delete ? labels.Length - 1 : r.tear && r.blocked == null ? tearEntry : r.split && canSplit ? 1 : 0;
                        int sj = EditorGUILayout.Popup(si, labels, GUILayout.Width(64));
                        if (sj != si) { r.delete = sj == labels.Length - 1; r.tear = r.blocked == null && sj == tearEntry; r.split = canSplit && sj == 1; }
                    }
                    // FUSE GROUP (2026-09-15): an independent per-row mark — rows sharing a letter fuse into one welded shell each; or Delete
                    else
                        using (new EditorGUI.DisabledScope(r.blocked != null))
                        {
                            int fi = r.delete ? DeleteEntry : string.IsNullOrEmpty(r.fuse) ? 0 : Mathf.Clamp(r.fuse[0] - 'A' + 1, 0, DeleteEntry - 1);
                            int fj = EditorGUILayout.Popup(fi, FuseLabels, GUILayout.Width(62));
                            if (fj != fi) { r.delete = fj == DeleteEntry; r.fuse = fj <= 0 || fj == DeleteEntry ? "" : ((char)('A' + fj - 1)).ToString(); }
                        }
                    if (r.nodeIndex == selectedIdx && (r.split != splitBefore || r.tear != tearBefore || r.fuse != fuseBefore || r.delete != deleteBefore)) advanceFrom = ri;   // the mouse path of the A–Z / Space keys
                    bool isSel = selectedIdx == r.nodeIndex;
                    string label = r.blocked != null ? $"{(isSel ? "◉ " : "")}{r.node}   — skipped: {r.blocked}{(r.delete ? "   ✕ deleted in the output" : "")}"
                                 : Fusing ? $"{(isSel ? "◉ " : "")}{r.node}   ({r.tris:N0} tris, {r.islands:N0} island{(r.islands == 1 ? "" : "s")}{SizeOf(r)}){(r.delete ? "   ✕ deleted in the output" : "")}"
                                 : $"{(isSel ? "◉ " : "")}{r.node}   ({r.tris:N0} tris, {(r.islands == 1 ? "1 island — already whole" : r.islands.ToString("N0") + " islands")}{SizeOf(r)}){(r.delete ? "   ✕ deleted in the output" : "")}";
                    // the row label is a BUTTON, exactly like the Vehicle Lab: click = highlight + frame in the preview
                    if (GUILayout.Button(label, isSel ? EditorStyles.whiteLabel : (r.blocked == null && (Fusing || r.islands > 1) ? EditorStyles.label : EditorStyles.miniLabel)))   // the Splitter dims what it cannot split
                    { ExitCutMode(); selectedIdx = isSel ? -1 : r.nodeIndex; SelectRow(isSel ? null : r); }
                    if (Event.current.type == EventType.Repaint) rowRects.Add(GUILayoutUtility.GetLastRect());   // the row's real rect in scroll-content space (see the ↑/↓ handler)
                }
            EditorGUILayout.EndScrollView();
            // THE HIGHLIGHT STAYS IN VIEW WHEN THE LIST CHANGES (2026-09-25, user: a part selected under a filter should
            // remain selected AND in the window when the filter is dropped): the highlight did survive, but the scroll
            // offset was the old list's and the row landed anywhere. The shown list's identity is hashed at every Repaint,
            // after the rows' rects are measured; when it differs from the last one and the highlighted row is shown, the
            // list scrolls to it exactly as the ↑/↓ keys do. A mark that hides the highlighted row advances it to the
            // neighbour in the same slot (AdvanceIfHidden), which is already in view: a no-op here.
            if (Event.current.type == EventType.Repaint)
            {
                int sig = 17; foreach (Row r in shown) sig = unchecked(sig * 31 + r.nodeIndex);
                if (sig != shownSignature)
                {
                    shownSignature = sig;
                    int selShown = shown.FindIndex(x => x.nodeIndex == selectedIdx);
                    if (selShown >= 0) { RevealRow(selShown); Repaint(); }
                }
            }
            if (advanceFrom >= 0) AdvanceIfHidden(advanceFrom);

            // ---- plane cut: for the selected part, connected or not — the escape hatch when island
            // splitting has nothing to grab (hull welded to deck). Whole triangles, nothing sliced. ----
            var selRowObj = rows.FirstOrDefault(r => r.nodeIndex == selectedIdx);
            // FIND THE MIRROR (2026-09-18, user: "I want to find the mirror item of Object_6"): the part whose box is the
            // highlighted part's box reflected across the centreline (WorkshopRules.FindMirror) — highlighted and scrolled
            // into view; a letter key then marks it. Hidden by the filters? It is still highlighted in the preview.
            if (selRowObj != null && selRowObj.min != null && !CutModeActive)
                if (GUILayout.Button(new GUIContent($"Find the mirror of '{selRowObj.node}'  (the part on the other side of the centreline with the same box)",
                        "Looks for the part whose world bounding box is this part's box reflected across the model's centreline (every bound within 3 % of the part's size; " +
                        "triangle counts may differ — the two sides are often remodelled). The twin is highlighted and scrolled into view: press its letter to mark it."), GUILayout.Height(22)))
                {
                    var mins = rows.Select(r => r.min).ToList(); var maxs = rows.Select(r => r.max).ToList(); var tri = rows.Select(r => r.tris).ToList();
                    float centre = WorkshopRules.MirrorCentre(mins, maxs, sideAxis);
                    int m = WorkshopRules.FindMirror(mins, maxs, tri, rows.IndexOf(selRowObj), sideAxis, centre, out bool selfSym);
                    if (m < 0)
                        status = selfSym ? $"'{selRowObj.node}' is symmetric about the centreline (side {centre:0.00}) — it is its own mirror."
                                         : $"No mirror of '{selRowObj.node}' found: no part's box is its reflection across the centreline (side {centre:0.00}).";
                    else
                    {
                        Row mr = rows[m]; string from = selRowObj.node;
                        ExitCutMode(); selectedIdx = mr.nodeIndex; SelectRow(mr);
                        int si = shown.FindIndex(x => x.nodeIndex == mr.nodeIndex);
                        if (si >= 0) RevealRow(si);
                        status = $"Mirror of '{from}': '{mr.node}' ({mr.tris:N0} tris{(string.IsNullOrEmpty(mr.fuse) ? "" : $", group {mr.fuse}")}) — highlighted" +
                                 (si < 0 ? "; its row is hidden by the filters (widen them to see it)." : Fusing ? "; press a letter to mark it." : mr.islands > 1 && mr.blocked == null ? "; press Space to check it." : ".");
                        GUIUtility.keyboardControl = 0; Repaint();
                    }
                }
            if (!Fusing && selRowObj != null && selRowObj.blocked == null)
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
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(CutModeActive
                        ? "Cut preview   (drag = orbit · middle/right-drag = pan · scroll = zoom — yellow = _CutA, grey = _CutB)"
                        : "Preview   (drag = orbit · middle/right-drag = pan · scroll = zoom · click a part row to highlight)", EditorStyles.miniBoldLabel);
                    bool wantUnmirror = EditorGUILayout.ToggleLeft(new GUIContent("Un-mirror",
                        "glTF and Unity disagree on handedness, so the preview shows the model MIRRORED — screen-left is the file's starboard. " +
                        "Tick this to see it the right way round (the geometry is flipped back so nothing turns see-through). The part list, the " +
                        "sliders, Find the mirror and every output always work in the file's own coordinates and are unaffected either way."),
                        unmirror, GUILayout.Width(90));
                    if (wantUnmirror != unmirror) { unmirror = wantUnmirror; FlipPreviewX(); }
                }
                var rect = GUILayoutUtility.GetRect(200f, 4000f, 600f, 600f, GUILayout.ExpandWidth(true));
                HandlePreviewInput(rect);
                if (Event.current.type == EventType.Repaint) RenderPreview(rect);
            }
            else if (rows.Count > 0)
                EditorGUILayout.LabelField($"  (no preview — the preview could not be built from the file; the list and {(Fusing ? "Fuse" : "Split")} still work)", EditorStyles.miniLabel);

            if (!Fusing)
            using (new EditorGUI.DisabledScope((chosen == 0 && deleted == 0) || string.IsNullOrEmpty(outGlb)))
                if (GUILayout.Button(new GUIContent($"Split {chosen} checked part(s){(deleted > 0 ? $" and delete {deleted} marked" : "")}  →  {(string.IsNullOrEmpty(outGlb) ? "(set the Output GLB)" : Path.GetFileName(outGlb))}",
                        "Writes the output GLB with ONLY the checked parts exploded into _Part_NNN children, and every part marked for deletion stripped of its mesh (its transform and children stay). The source file is never touched."), GUILayout.Height(28)))
                    SplitChecked();

            if (Fusing)
            {
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
            checkMirrored = EditorGUILayout.ToggleLeft(new GUIContent("Check mirrored parts",
                "glTF says a mirrored part (a negative-scale node, typically one side of a symmetric hull) must have its winding reversed, and the " +
                "fuse does that. Some files store their mirrored parts ALREADY facing outward, and the reversal then turns that whole side " +
                "inward: see-through (the Confederate frigate's hull, 94.8 % back-facing from one side). Ticked, the fuse compares every mirrored " +
                "part with the plain parts it is welded to and undoes the reversal only when they clearly agree the file is stored that way; " +
                "mixed evidence changes nothing, and a file stored the standard way (the Teutonic) comes out byte-identical. Off by default. " +
                "The fuse warns you when a group would need it."), checkMirrored);
            using (new EditorGUILayout.HorizontalScope())
            {
                bool sidecarExists = File.Exists(FuseSidecarPath(srcFile) ?? "");
                using (new EditorGUI.DisabledScope(fusedRows == 0 && !sidecarExists))   // with no letters AND a sidecar on disk, saving means "clear it" (review of 0a8b56e)
                    if (GUILayout.Button(new GUIContent("Save groups", $"Writes the ⊕ letters to {Path.GetFileName(srcFile)}.fuse.txt next to the source (a Fuse writes it too); the first Probe of a file restores them from there. With no letters marked this removes the file."), GUILayout.Width(100)))
                    {
                        WriteFuseSidecar(srcFile); WriteMarksSidecar(srcFile);   // the deletion marks ride along in their own sidecar
                        status = (fusedRows == 0 ? $"Groupings cleared: {FuseSidecarPath(srcFile)} removed" : $"Groupings saved: {FuseSidecarPath(srcFile)}") + (deleted > 0 ? $"; {deleted} deletion mark(s) saved: {MarksSidecarPath(srcFile)}" : "");
                    }
                using (new EditorGUI.DisabledScope(!File.Exists(FuseSidecarPath(srcFile) ?? "")))
                    if (GUILayout.Button(new GUIContent("Load groups", "Reads the ⊕ letters back from the sidecar next to the source, by part name and node index (a name shared by several parts is refused unless the index settles it)."), GUILayout.Width(100)))
                    {
                        int n = ApplyFuseSidecar(rows, out int refused);
                        int m = ApplyMarksSidecar(rows, out int refusedMarks);
                        status = $"Groupings loaded: {n} part(s) marked from {FuseSidecarPath(srcFile)}" + (refused > 0 ? $" — {refused} line(s) fit no single part (see the console)" : "")
                               + (m > 0 ? $"; {m} mark(s) from {MarksSidecarPath(srcFile)}" : "") + (refusedMarks > 0 ? $" — {refusedMarks} mark line(s) fit no single part" : "");
                    }
                EditorGUILayout.LabelField(fusedRows == 0 ? " " : $"{fusedRows} marked in {fuseGroups.Count} group(s): {string.Join("  ", fuseGroups.Select(g => "⊕" + g + "×" + rows.Count(r => r.fuse == g)))}", EditorStyles.miniLabel);
            }
            using (new EditorGUI.DisabledScope((fusedRows == 0 && deleted == 0) || string.IsNullOrEmpty(outGlb)))
                if (GUILayout.Button(new GUIContent(fusedRows == 0 && deleted == 0
                            ? "Fuse — mark parts with a ⊕ letter first (popup per row, or keys A–Z on the highlighted row)"
                            : fusedRows == 0
                            ? $"Delete {deleted} marked part(s)  →  {(string.IsNullOrEmpty(outGlb) ? "(set the Output GLB)" : Path.GetFileName(outGlb))}"
                            : $"Fuse {fusedRows} marked part(s) in {fuseGroups.Count} group(s) ({string.Join(", ", fuseGroups.Select(g => "⊕" + g + "×" + rows.Count(r => r.fuse == g)))}) into one shell each{(deleted > 0 ? $" and delete {deleted} marked" : "")}  →  {(string.IsNullOrEmpty(outGlb) ? "(set the Output GLB)" : Path.GetFileName(outGlb))}",
                        "Joins the parts of each ⊕ group into ONE mesh in the output GLB, welds their seams, makes the winding consistent by MAJORITY across each " +
                        "welded island (the minority of faces reversed to agree with the rest), and judges direction once where it can be judged: a shell, " +
                        "a thin solid or a convex plating region by its signed volume about its own centroid (inside-out = reversed whole), a FLAT sheet " +
                        "(a deck, a bulwark) by the inside-out score. Mirrored instances (negative node scale) are read with the winding they render with. The cure for " +
                        "a hull authored as dozens of separate plates — see-through, a hole in its side, gaps under any reduction. Triangles are preserved " +
                        "exactly; the source parts keep their transforms and children and lose only their mesh. The source file is never touched."), WrappedButton, GUILayout.Height(56)))   // twice the height, wrapped: 19 groups' letters and counts fit (user 2026-09-18)
                    FuseMarked();
            }
        }

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
        EditorGUILayout.EndScrollView();
    }

    void Probe()
    {
        if (Analyze()) BuildPreviewFromGlb();
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
        var keptDelete = new HashSet<int>(rows.Where(r => r.delete).Select(r => r.nodeIndex));   // the deletion marks survive a re-Probe as the checks do
        bool firstLoad = rows.Count == 0 || !SamePath(probedFile, srcFile);   // the marks sidecar is read once, when a file comes into the window
        // FUSE LETTERS survive too (2026-09-15, user: "it doesn't seem to be able to save the groupings, only the
        // checkboxes"): the merge slider re-analyzes and used to rebuild every row blank. Same key. And a re-Probe of a
        // file with NO letters in memory restores them from the sidecar the last Fuse wrote (<source>.fuse.txt).
        // …in the FUSER, where the letters are the user's edits. The SPLITTER never edits a letter: its letters are the
        // sidecar's, and a re-Probe there re-reads the sidecar so groups saved in the Fuser meanwhile reach the next cut
        // (review of PR #63: a re-Probe kept the Splitter's stale letters while the docs told the user to re-Probe)
        var keptFuse = Fusing ? rows.Where(r => !string.IsNullOrEmpty(r.fuse)).ToDictionary(r => r.nodeIndex, r => r.fuse) : new Dictionary<int, string>();
        // The sidecar is consulted only when this file is being loaded INTO the window (no rows yet, or rows of another
        // file) — a re-Probe or a slider move of a file whose letters the user cleared keeps them cleared (review of
        // 0a8b56e: the old rule "no letters in memory" reloaded the sidecar over a deliberate clear). "Load groups" is
        // the explicit way back.
        bool initialLoad = rows.Count == 0 || !SamePath(probedFile, srcFile) || !Fusing;
        if (firstLoad) ResetFilters();   // a model probed into the window starts fully visible (see ResetFilters)
        try
        {
            rows = GlbDisconnectedParts.Analyze(File.ReadAllBytes(srcFile), mergePct / 100.0)
                .Select(p => new Row { nodeIndex = p.NodeIndex, node = p.NodeName, mesh = p.MeshName, tris = p.Triangles, islands = p.Islands, blocked = p.Blocked, split = kept.Contains(p.NodeIndex), delete = keptDelete.Contains(p.NodeIndex),
                                       fuse = keptFuse.TryGetValue(p.NodeIndex, out string kf) ? kf : "",
                                       verts = p.Vertices, min = p.Min?.Select(d => (float)d).ToArray(), max = p.Max?.Select(d => (float)d).ToArray() })
                .OrderBy(r => NaturalPrefix(r.node), StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => NaturalNumber(r.node))
                .ThenBy(r => r.node, StringComparer.OrdinalIgnoreCase).ToList();
            if (keptFuse.Count == 0 && initialLoad) ApplyFuseSidecar(rows, out _);
            if (firstLoad && kept.Count == 0 && keptDelete.Count == 0) ApplyMarksSidecar(rows, out _);
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

    // MIRRORED IMAGE, NOT MIRRORED FACING (measured 2026-09-19, drill_unity_facing.py on the Romanic split: 856
    // mirrored nodes, 425,632 triangles, rays from the beam): the winding below is handed to Unity UNCHANGED and
    // 1.0 % of the struck surface renders back-facing; adding the swap glTF's negative-determinant rule seems to ask
    // for takes that to 97.6 % — the whole port side inside-out, which is what a user reported. Unity reads these
    // numbers in a left-handed frame AND calls a clockwise triangle front-facing, and the two conventions cancel, so
    // facing survives the copy. The IMAGE does not: the preview is a mirror of the model, so screen-left is the
    // file's starboard. The sliders and the mirror finder work in FILE coordinates and stay correct.
    // ---- preview build (2026-09-19): straight from the GLB, one object per mesh-carrying NODE. Until now the Vehicle
    // Lab's Blender probe exported an FBX that Unity imported (24 s of a 214 MB ship's probe) and the rows found their
    // renderers by NAME — a file that names all 113 of its nodes "Material2" (the Salegs Revenge) lit the whole ship for
    // any row. Rows and preview objects now meet on the node index. Same coordinates and winding as the cut preview
    // (world-space glTF, as ExtractPart reports them), so the turntable and the cut mode agree; a submesh per primitive
    // carries its material's base colour, which is what the FBX trip showed too. ----
    void BuildPreviewFromGlb()
    {
        DestroyPreview();
        try
        {
            // MEMORY CEILING (2026-09-19, user: "1GB should be a good limit"): the preview holds the whole model as
            // managed arrays AND as Unity meshes — positions, the Vector3 copy, recalculated normals and the index
            // arrays. Analyze has already counted this file's vertices and triangles, so the cost is known BEFORE a
            // byte is read; past the ceiling the window says so and keeps the list, which is what the Splitter and
            // Fuser actually act on. (Your 214 MB ship: 5.2 M vertices, 6.2 M triangles, about 0.4 GB — it still builds.)
            long need = PreviewBytes();
            if (need > PreviewByteCeiling)
            {
                status += $"\n(no preview: this model would need about {need / (1024f * 1024f * 1024f):0.0} GB of mesh memory, over the {PreviewByteCeiling / (1024 * 1024 * 1024)} GB ceiling — the list, the filters and {(Fusing ? "Fuse" : "Split")} work as usual)";
                return;
            }
            EditorUtility.DisplayProgressBar("Model Workshop", "Building the preview…", 0.4f);
            var parts = GlbDisconnectedParts.ExtractAll(File.ReadAllBytes(srcFile));
            inst = new GameObject("__workshopPreview") { hideFlags = HideFlags.HideAndDontSave };
            var sh = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
            foreach (var g in parts)
            {
                var mesh = new Mesh { name = g.NodeName, hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                var verts = new Vector3[g.Positions.Length / 3];
                float sx = unmirror ? -1f : 1f;
                for (int i = 0; i < verts.Length; i++) verts[i] = new Vector3(sx * g.Positions[i * 3], g.Positions[i * 3 + 1], g.Positions[i * 3 + 2]);
                mesh.vertices = verts;
                int subs = Math.Max(1, g.PrimitiveStart.Length);
                mesh.subMeshCount = subs;
                var mats = new Material[subs];
                for (int s = 0; s < subs; s++)
                {
                    int start = s < g.PrimitiveStart.Length ? g.PrimitiveStart[s] : 0, end = s + 1 < g.PrimitiveStart.Length ? g.PrimitiveStart[s + 1] : g.Triangles.Length;
                    var tri = new int[Math.Max(0, end - start)]; Array.Copy(g.Triangles, start, tri, 0, tri.Length);
                    if (unmirror) for (int t = 0; t + 2 < tri.Length; t += 3) { int tmp = tri[t + 1]; tri[t + 1] = tri[t + 2]; tri[t + 2] = tmp; }
                    mesh.SetTriangles(tri, s, false);
                    mats[s] = PreviewMaterial(sh, s < g.PrimitiveColour.Length ? g.PrimitiveColour[s] : null);
                }
                mesh.RecalculateNormals(); mesh.RecalculateBounds();
                previewAssets.Add(mesh);
                var go = new GameObject(g.NodeName) { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(inst.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterials = mats;
                previewByNode[g.NodeIndex] = mr;
            }
            if (pru == null) pru = new PreviewRenderUtility();
            pru.AddSingleGO(inst);
            boundsValid = false; previewPan = Vector2.zero; zoom = 1.5f;
            int missing = rows.Count(r => !previewByNode.ContainsKey(r.nodeIndex));
            if (missing > 0) status += $"\n(preview: {missing} part(s) the extractor refused are not drawn — their rows say why)";
        }
        catch (Exception e) { status += "\n(no preview: " + e.Message + ")"; }
        finally { EditorUtility.ClearProgressBar(); }
    }

    // What the preview will cost, from the counts Analyze already has: per vertex the float positions, the Vector3
    // copy and the recalculated normals (3 x 12 bytes) with the same again for Unity's own copy of the mesh, and per
    // index the managed array plus Unity's (2 x 4 bytes). Deliberately an over-estimate — the ceiling is a guard, and
    // refusing a preview costs the user a picture, while running out of memory costs the editor.
    const long PreviewByteCeiling = 1L << 30;   // 1 GB
    long PreviewBytes()
    {
        long verts = 0, idx = 0;
        // r.verts counts the whole POSITION accessor, which a split fragment SHARES with its parent — 13x too high on
        // a real Khalandion split (PR 66 review). The extractor compacts to referenced vertices, and a part can never
        // reference more than three per triangle, so the smaller of the two is the honest figure.
        foreach (var r in rows) { verts += Math.Min(r.verts, 3L * r.tris); idx += 3L * r.tris; }
        return verts * 72L + idx * 8L;
    }

    // Flip every built mesh in place when the checkbox moves: negate X and reverse each submesh's winding (the two
    // together are what the drill measured as solid). Cheaper and steadier than re-reading a 214 MB file, and the cut
    // preview follows through UpdateCutPartition, which re-emits its triangles anyway.
    void FlipPreviewX()
    {
        foreach (var m in previewAssets)
        {
            if (m == null) continue;
            var v = m.vertices;
            for (int i = 0; i < v.Length; i++) v[i].x = -v[i].x;
            m.vertices = v;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var tri = m.GetTriangles(s);
                for (int t = 0; t + 2 < tri.Length; t += 3) { int tmp = tri[t + 1]; tri[t + 1] = tri[t + 2]; tri[t + 2] = tmp; }
                m.SetTriangles(tri, s, false);
            }
            m.RecalculateNormals(); m.RecalculateBounds();
        }
        if (cutMesh != null)
        {
            var v = cutMesh.vertices;
            for (int i = 0; i < v.Length; i++) v[i].x = -v[i].x;
            cutMesh.vertices = v;
            UpdateCutPartition();   // re-emits both sides, winding included
            cutMesh.RecalculateNormals();
        }
        flatShare = null; flatShareFor = null;   // measured on the preview meshes, which just moved
        boundsValid = false;
        Repaint();
    }

    // One material per distinct base colour, shared across the preview (a 1,400-part liner has a dozen colours).
    // COLOUR SPACE, measured 2026-09-19 (drill_colour_space.py) and deliberately NOT changed: glTF stores
    // baseColorFactor in LINEAR space and this project renders in Gamma, so the honest conversion would brighten every
    // flat colour (a charcoal of 0.137 would display at 0.406). The OLD Blender/FBX preview handed Unity the linear
    // number unconverted — checked material by material on the Salegs Revenge, all twelve identical — so converting
    // here would not restore anything, it would make the Workshop disagree with every other preview in the toolkit.
    // Bake-time tone is the albedoBrightness / albedoSaturation dials' job, not this window's.
    Material PreviewMaterial(Shader sh, float[] rgb)
    {
        var c = rgb != null && rgb.Length >= 3 ? new Color(rgb[0], rgb[1], rgb[2]) : Color.white;
        if (!previewMats.TryGetValue(c, out Material m) || m == null)
        {
            m = new Material(sh) { color = c, hideFlags = HideFlags.HideAndDontSave };
            previewMats[c] = m;
        }
        return m;
    }

    void DestroyPreview()
    {
        ExitCutMode();
        selectedIdx = -1;
        SelectRow(null);
        if (inst != null) DestroyImmediate(inst);
        inst = null;
        previewByNode.Clear();
        foreach (var a in previewAssets) if (a != null) DestroyImmediate(a);
        previewAssets.Clear();
        foreach (var m in previewMats.Values) if (m != null) DestroyImmediate(m);
        previewMats.Clear();
        flatShare = null; flatShareFor = null;
        if (pru != null) { pru.Cleanup(); pru = null; }
    }

    // ---- plane-cut mode: preview mesh built from the SOURCE bytes (ExtractPart), so what's yellow IS what
    // the cut writes to _CutA. The turntable model hides while the cut preview is up. ----
    void EnterCutMode(Row row)
    {
        ExitCutMode();
        try { cutGeo = GlbDisconnectedParts.ExtractPart(File.ReadAllBytes(srcFile), row.nodeIndex); }
        catch (Exception e) { status = $"Plane cut unavailable for '{row.node}': {e.Message}"; cutGeo = null; return; }
        cutMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        var verts = new Vector3[cutGeo.Positions.Length / 3];
        float csx = unmirror ? -1f : 1f;
        for (int i = 0; i < verts.Length; i++)
            verts[i] = new Vector3(csx * cutGeo.Positions[i * 3], cutGeo.Positions[i * 3 + 1], cutGeo.Positions[i * 3 + 2]);
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

    // SIZE ON THE ROW (2026-09-20, user: "it would really help if this list also included the dimensions"): the same
    // figure the Vehicle Lab prints, from the box Analyze already read out of the accessors. Blank for a part the file
    // does not measure.
    static string SizeOf(Row r) =>
        r.min == null || r.max == null ? "" :
        string.Format(System.Globalization.CultureInfo.InvariantCulture, ", size {0:0.##}×{1:0.##}×{2:0.##}",
                      r.max[0] - r.min[0], r.max[1] - r.min[1], r.max[2] - r.min[2]);

    // keep a shown row in view by its MEASURED rect (rows are not one height: "already whole" rows draw in the mini font and
    // are shorter, and an assumed 22 px per row drifted the highlight out of view past ~100 rows — user 2026-09-16)
    void RevealRow(int idx)
    {
        if (idx < rowRects.Count) { Rect rr = rowRects[idx]; scroll.y = WorkshopRules.RevealScroll(rr.yMin, rr.yMax, scroll.y, listViewHeight); }
        else scroll.y = Mathf.Max(0f, idx * 22f - 120f);   // no rects measured yet (first frame): the old estimate
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
            if (unmirror) { side.Add(t[i]); side.Add(t[i + 2]); side.Add(t[i + 1]); }   // the mirrored mesh needs the reversed winding
            else { side.Add(t[i]); side.Add(t[i + 1]); side.Add(t[i + 2]); }
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
            string deletedLine = ApplyDeletionsTo(outGlb);   // the marks for deletion, on the written output (the cut only appends)
            WriteMarksSidecar(srcFile);
            WriteFuseSidecarForOutput(outGlb);   // the ⊕ letters travel with the output, passed down to the _CutA/_CutB children (user 2026-09-16; review of 0097bd5)
            status = $"Plane cut done: {result.Details.FirstOrDefault()}{(deletedLine != null ? " " + deletedLine + "." : "")}\n{outGlb}\nNext: open it in the Vehicle Lab — or cut again by pointing Source GLB at this output and re-Probing (your ⊕ letters travel with it).";
        }
        catch (Exception e) { status = "Plane cut failed (source untouched): " + e.Message; Debug.LogException(e); }
        finally { EditorUtility.ClearProgressBar(); }
    }

    // Click a row → tint that part's renderer yellow and frame it with context (the Vehicle Lab mechanism, matched by
    // node index since the preview is built per node).
    // FLAT-SURFACE FILTER (the Vehicle Lab's, verbatim in spirit): the share of a part's surface area lying within 30° of
    // level, measured on the PREVIEW meshes (glTF +Y is up, so world +Y is 'level'; the geometric triangle normal is
    // used — no reliance on authored normals). Cached once per preview instance; a part the preview doesn't carry (or
    // any state before a probe) PASSES the filter — a filter never hides what it cannot measure.
    bool FlatOk(Row x)
    {
        if (minFlatPct <= 0f) return true;
        BuildFlatShareIFN();
        if (flatShare == null || !flatShare.TryGetValue(x.nodeIndex, out float s)) return true;
        return s * 100f >= minFlatPct;
    }
    void BuildFlatShareIFN()
    {
        if (inst == null) { flatShare = null; flatShareFor = null; return; }
        if (flatShare != null && flatShareFor == inst) return;
        flatShare = new Dictionary<int, float>();
        foreach (var kv in previewByNode)
        {
            var mf = kv.Value != null ? kv.Value.GetComponent<MeshFilter>() : null;
            if (mf == null || mf.sharedMesh == null) continue;
            var m = mf.sharedMesh; var l2w = mf.transform.localToWorldMatrix;
            var v = m.vertices; var t = m.triangles;
            double a0 = 0, af = 0;
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 p0 = l2w.MultiplyPoint3x4(v[t[i]]), p1 = l2w.MultiplyPoint3x4(v[t[i + 1]]), p2 = l2w.MultiplyPoint3x4(v[t[i + 2]]);
                Vector3 c = Vector3.Cross(p1 - p0, p2 - p0);
                float a2 = c.magnitude;
                if (a2 <= 0f) continue;
                a0 += a2;
                if (Mathf.Abs(c.y) / a2 >= 0.866f) af += a2;   // cos 30° — the facing cut's default tilt
            }
            if (a0 > 0) flatShare[kv.Key] = (float)(af / a0);
        }
        flatShareFor = inst;
    }

    void SelectRow(Row row)
    {
        if (highlightedRenderers != null)
            for (int i = 0; i < highlightedRenderers.Count; i++)
                try { if (highlightedRenderers[i] != null) highlightedRenderers[i].sharedMaterials = highlightedOriginals[i]; } catch { }
        highlightedRenderers = null; highlightedOriginals = null;
        selectedRow = row?.node ?? "";
        boundsValid = false;
        previewPan = Vector2.zero;
        if (inst == null || row == null) return;
        // by NODE INDEX (2026-09-19): the preview is built from the GLB one object per node, so a row lights exactly its
        // own part — the old name match lit every namesake, all 113 of them on a file that names every node "Material2"
        if (!previewByNode.TryGetValue(row.nodeIndex, out Renderer hit) || hit == null) return;   // a part the extractor refused: nothing to light
        var hits = new List<Renderer> { hit };
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
    // THE MARKS SIDECAR (2026-09-18, user: "how is the delete state persisted?" after marking 144 chain links): the Split
    // checks and the deletion marks, next to the source as <glb>.marks.txt — the fuse sidecar's format with S (split) and
    // X (delete) for letters, so the same resolver reads it by node index and name. Written by Save marks and by every
    // Split, Plane cut and Fuse; read at the first Probe of a file (a re-Probe keeps the marks in memory, as the Fuser
    // keeps its letters). Both windows read and write it: a deletion marked in the Fuser reaches the Splitter.
    static string MarksSidecarPath(string glb) => string.IsNullOrEmpty(glb) ? null : glb + ".marks.txt";
    void WriteMarksSidecar(string glb)
    {
        try
        {
            string path = MarksSidecarPath(glb); if (path == null) return;
            var lines = rows.Where(r => (r.split || r.tear || r.delete) && !string.IsNullOrEmpty(r.node)).Select(r => WorkshopRules.SidecarLine(r.delete ? "X" : r.tear ? "T" : "S", r.node, r.nodeIndex)).ToArray();
            if (lines.Length == 0) { if (File.Exists(path)) File.Delete(path); return; }
            File.WriteAllLines(path, new[] { WorkshopRules.SidecarHeader }.Concat(lines));
        }
        catch (Exception e) { Debug.LogWarning("[Workshop] could not write the marks sidecar: " + e.Message); }
    }
    int ApplyMarksSidecar(List<Row> target, out int refused)
    {
        refused = 0;
        try
        {
            string path = MarksSidecarPath(srcFile);
            if (path == null || !File.Exists(path)) return 0;
            var problems = new List<string>();
            var marks = WorkshopRules.ResolveFuseSidecar(File.ReadAllLines(path), target.Select(r => new KeyValuePair<int, string>(r.nodeIndex, r.node)).ToList(), problems);
            int applied = 0;
            foreach (var r in target)
            {
                if (!marks.TryGetValue(r.nodeIndex, out string mark)) continue;
                if (mark == "X") { r.delete = true; r.split = false; r.tear = false; r.fuse = ""; applied++; }
                else if (mark == "S" && r.islands > 1 && r.blocked == null) { r.split = true; r.tear = false; r.delete = false; applied++; }
                else if (mark == "T" && r.blocked == null) { r.tear = true; r.split = false; r.delete = false; applied++; }
            }
            foreach (string q in problems) Debug.LogWarning("[Workshop] marks sidecar: " + q);
            refused = problems.Count;
            return applied;
        }
        catch (Exception e) { Debug.LogWarning("[Workshop] could not read the marks sidecar: " + e.Message); return 0; }
    }
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
    // The sidecar for a SPLIT or CUT output: the letters of the rows in memory, passed down to the new _Part_NNN / _CutA
    // children in the output (the split parent is meshless there and would resolve to nothing — review of 0097bd5).
    void WriteFuseSidecarForOutput(string outputGlb)
    {
        try
        {
            string path = FuseSidecarPath(outputGlb); if (path == null) return;
            var letters = rows.Where(r => !string.IsNullOrEmpty(r.fuse)).ToDictionary(r => r.nodeIndex, r => r.fuse);
            if (letters.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
            var parts = GlbDisconnectedParts.Analyze(File.ReadAllBytes(outputGlb));
            // every node's parent (the split parent is meshless, so the analyzer does not list it — read the hierarchy directly)
            var table = GlbDisconnectedParts.NodeParents(File.ReadAllBytes(outputGlb));
            int firstNewNode = GlbDisconnectedParts.NodeParents(File.ReadAllBytes(srcFile)).Count;   // the operation only appends: nodes past the source's count are the ones it created
            var transferred = WorkshopRules.TransferLetters(letters, table, new HashSet<int>(parts.Select(q => q.NodeIndex)), firstNewNode);
            var nameOf = parts.ToDictionary(q => q.NodeIndex, q => q.NodeName);
            var lines = transferred.OrderBy(kv => kv.Key).Where(kv => nameOf.ContainsKey(kv.Key)).Select(kv => WorkshopRules.SidecarLine(kv.Value, nameOf[kv.Key], kv.Key)).ToArray();
            if (lines.Length == 0) { if (File.Exists(path)) File.Delete(path); return; }
            File.WriteAllLines(path, new[] { WorkshopRules.SidecarHeader }.Concat(lines));
        }
        catch (Exception e) { Debug.LogWarning("[Workshop] could not write the output's fuse groupings sidecar: " + e.Message); }
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
    // (The 2026-09-16 Generate — fuse AND split in one output — went with the two-window split of 2026-09-18: the
    // Splitter's checks and the Fuser's letters no longer live in one window. Chain the outputs; the letters travel.)
    void FuseMarked()
    {
        const string verb = "Fuse";
        try { GlbDisconnectedParts.GuardPaths(srcFile, outGlb); }   // the file entry points refuse output == source; this path writes the bytes itself, so it asks the same guard
        catch (Exception e) { status = verb + " refused (source untouched): " + e.Message; return; }
        if (File.Exists(outGlb) && !EditorUtility.DisplayDialog("Overwrite existing file?", outGlb, "Overwrite", "Cancel")) return;
        try
        {
            var groups = rows.Where(r => !string.IsNullOrEmpty(r.fuse)).GroupBy(r => r.fuse).OrderBy(g => g.Key).ToList();
            byte[] bytes = File.ReadAllBytes(srcFile);
            var lines = new List<string>(); int done = 0;
            var report = new List<WorkshopRules.FuseGroupReport>();   // the evidence goes to a file, not the status box (711 parts in six groups made the status unreadable — user 2026-09-16)
            List<GlbDisconnectedParts.Result> results = null;   // one per group, in letter order; the output sidecar below names each shell from them
            if (groups.Count > 0)
            {
                // every group planned AT ONCE on the thread pool against the same source, applied in letter order into one
                // output (2026-09-18, user: "can't we process it in parallel?" — 19 groups took 3½ minutes chained); the
                // fused parts are appended in the same order chaining would, so node indices and names come out the same
                // the group letter leads the fused part's name — "Fused_B_Object_54" — so the Lab's list shows at a glance which
                // group a shell came from and the fused parts sort together (user 2026-09-17)
                var jobs = groups.Select(g => new GlbDisconnectedParts.FuseJob { NodeIndices = g.Select(r => r.nodeIndex).ToList(), Name = "Fused_" + g.Key + "_" + g.First().node, CheckMirrored = checkMirrored }).ToList();   // row order: the first becomes the fused part's name
                int total = jobs.Count; string letters = string.Join(" ", groups.Select(g => "⊕" + g.Key));
                EditorUtility.DisplayProgressBar("Model Workshop", $"Fusing {total} group(s) in parallel: {letters}…", 0.2f);
                byte[] fused = GlbDisconnectedParts.FuseGroups(bytes, jobs, weldPermille / 1000.0, out results,
                    planned => EditorUtility.DisplayProgressBar("Model Workshop", $"Fusing {total} group(s) in parallel — {planned} of {total} planned…", 0.2f + 0.6f * planned / Math.Max(1, total)));
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var g = groups[gi]; var result = results[gi];
                    report.Add(new WorkshopRules.FuseGroupReport { Letter = g.Key, PartNames = g.Select(r => r.node).ToList(), Details = result.Details, Warnings = result.Warnings, Changed = result.Changed, Islands = result.IslandLines });
                    if (!result.Changed) { lines.Add($"⊕{g.Key}: nothing fused ({string.Join("; ", result.Warnings)})"); continue; }
                    done++;
                    lines.Add($"⊕{g.Key}: {result.Details.FirstOrDefault()}" + (result.Warnings.Count > 0 ? $"   ⚠ {result.Warnings.Count} warning(s)" : ""));
                }
                if (done > 0) bytes = fused;
            }
            // …then every part marked for deletion loses its mesh (the fuse never renumbers nodes, so the marks' indices hold)
            var toDelete = new HashSet<int>(rows.Where(r => r.delete).Select(r => r.nodeIndex));
            if (toDelete.Count > 0)
            {
                var rr = GlbDisconnectedParts.RemoveMeshes(bytes, toDelete);
                foreach (var w in rr.Warnings) Debug.LogWarning("[Workshop] delete: " + w);
                if (rr.Changed) { bytes = rr.Bytes; done++; lines.Add(rr.Details[0]); }
            }
            if (done == 0) { status = "Nothing changed — no group produced a fused mesh and nothing was deleted (see warnings in the console)."; return; }
            File.WriteAllBytes(outGlb, bytes);
            // the groupings and the marks, next to the source: a later Probe of this file restores them. An empty write
            // CLEARS the file (Save groups' documented "clear"), so a delete-only run — no letters at all — must not
            // touch it: it would silently drop the groups saved earlier (self-review of PR #63).
            if (groups.Count > 0) WriteFuseSidecar(srcFile);
            WriteMarksSidecar(srcFile);
            // ...and next to the OUTPUT: each shell under its group's letter, so the Splitter (or the Fuser again) opens it knowing its groups
            if (results != null) try
            {
                string outSidecar = FuseSidecarPath(outGlb);
                var byLetter = new Dictionary<string, KeyValuePair<int, string>>();
                for (int gi = 0; gi < groups.Count; gi++) byLetter[groups[gi].Key] = new KeyValuePair<int, string>(results[gi].FusedNodeIndex, results[gi].FusedNodeName);
                var outLines = WorkshopRules.FusedOutputSidecarLines(groups.Select(g => new KeyValuePair<string, IList<KeyValuePair<int, string>>>(g.Key, g.Select(r => new KeyValuePair<int, string>(r.nodeIndex, r.node)).ToList())), byLetter);
                if (outSidecar != null) { if (outLines.Count > 0) File.WriteAllLines(outSidecar, new[] { WorkshopRules.SidecarHeader }.Concat(outLines)); else if (File.Exists(outSidecar)) File.Delete(outSidecar); }
            }
            catch (Exception e) { Debug.LogWarning("[Workshop] could not write the output's fuse groupings sidecar: " + e.Message); }
            string reportPath = outGlb + ".fuse-report.txt";
            try { File.WriteAllText(reportPath, WorkshopRules.FuseReport(srcFile, outGlb, weldPermille, report)); }
            catch (Exception e) { Debug.LogWarning("[Workshop] could not write the fuse report: " + e.Message); reportPath = "(not written: " + e.Message + ")"; }
            foreach (var g in report) foreach (var w in g.Warnings) Debug.LogWarning($"[Workshop] ⊕{g.Letter}: " + w);
            Debug.Log($"[Workshop] fuse report: {reportPath}");
            status = $"{verb} done ({done} step(s)):\n{string.Join("\n", lines)}\n{outGlb}\nReport (every group's islands, warnings and stitched-part numbers): {reportPath}\nNext: point Source GLB at this output and re-Probe to see each fused shell as one part, or open it in the Vehicle Lab.";
        }
        catch (Exception e) { status = verb + " failed (source untouched): " + e.Message; Debug.LogException(e); }
        finally { EditorUtility.ClearProgressBar(); }
    }

    // the parts marked for deletion, stripped from a file already written (the plane cut writes its own output)
    string ApplyDeletionsTo(string glb)
    {
        var toDelete = new HashSet<int>(rows.Where(r => r.delete).Select(r => r.nodeIndex));
        if (toDelete.Count == 0) return null;
        var rr = GlbDisconnectedParts.RemoveMeshes(File.ReadAllBytes(glb), toDelete);
        foreach (var w in rr.Warnings) Debug.LogWarning("[Workshop] delete: " + w);
        if (!rr.Changed) return null;
        File.WriteAllBytes(glb, rr.Bytes);
        return rr.Details[0];
    }

    void SplitChecked()
    {
        if (File.Exists(outGlb) && !EditorUtility.DisplayDialog("Overwrite existing file?", outGlb, "Overwrite", "Cancel")) return;
        try
        {
            EditorUtility.DisplayProgressBar("Model Workshop", "Splitting checked parts…", 0.4f);
            GlbDisconnectedParts.GuardPaths(srcFile, outGlb);   // SplitFile used to guard; this path writes the bytes itself
            var picked = new HashSet<int>(rows.Where(r => r.split).Select(r => r.nodeIndex));
            var toTear = new HashSet<int>(rows.Where(r => r.tear).Select(r => r.nodeIndex));
            var toDelete = new HashSet<int>(rows.Where(r => r.delete).Select(r => r.nodeIndex));
            byte[] bytes = File.ReadAllBytes(srcFile);
            GlbDisconnectedParts.Result result = picked.Count > 0 ? GlbDisconnectedParts.Split(bytes, picked, mergePct / 100.0) : null;
            if (result != null && result.Changed) bytes = result.Bytes;
            // TEAR (2026-09-25), after the split: both only append nodes, so the marked indices still name their parts
            GlbDisconnectedParts.Result torn = toTear.Count > 0 ? GlbDisconnectedParts.Tear(bytes, toTear, mergePct / 100.0) : null;
            if (torn != null && torn.Changed) bytes = torn.Bytes;
            string deletedLine = null;
            if (toDelete.Count > 0)   // after the split: the split only appends, so the marked indices still name their nodes
            {
                var rr = GlbDisconnectedParts.RemoveMeshes(bytes, toDelete);
                foreach (var w in rr.Warnings) Debug.LogWarning("[Workshop] delete: " + w);
                if (rr.Changed) { bytes = rr.Bytes; deletedLine = rr.Details[0]; }
            }
            // UNIQUE NAMES (2026-09-25): whatever else happened, every part leaves the Cutter with a name of its own
            string renamedLine = null;
            {
                var un = GlbDisconnectedParts.UniqueNodeNames(bytes);
                if (un.Changed) { bytes = un.Bytes; renamedLine = un.Details[0]; }
            }
            if ((result == null || !result.Changed) && (torn == null || !torn.Changed) && deletedLine == null && renamedLine == null) { status = "Nothing changed — the checked parts produced no split, no tear, nothing was deleted and every part already had a unique name (see warnings in the console)."; return; }
            File.WriteAllBytes(outGlb, bytes);
            WriteMarksSidecar(srcFile);   // the checks and deletion marks, next to the source: the first Probe of it restores them
            if (result != null) foreach (var w in result.Warnings) Debug.LogWarning("[Workshop] " + w);
            WriteFuseSidecarForOutput(outGlb);   // the ⊕ letters travel with the output, passed down to the _Part_NNN children
            status = (result != null && result.Changed ? $"Split done: {result.NodesSplit} part(s) → {result.ChildPartsCreated} sub-parts, {result.SourceTriangles:N0} triangles preserved." : "Split: nothing checked.")
                   + (torn != null && torn.Changed ? $" Tear: {torn.NodesSplit} part(s) → {torn.ChildPartsCreated} pieces." : torn != null ? " Tear: nothing came apart (the mirror side has no separate island where this part is welded)." : "")
                   + (deletedLine != null ? " " + deletedLine + "." : "") + (renamedLine != null ? " " + renamedLine + "." : "") + $"\n{outGlb}\nNext: open it in the Vehicle Lab, Probe parts, and mark the junk islands Ignore.";
            if (result != null) Debug.Log($"[Workshop] {string.Join(" | ", result.Details)}");
            if (torn != null) { foreach (var w in torn.Warnings) Debug.LogWarning("[Workshop] tear: " + w); Debug.Log($"[Workshop] tear: {string.Join(" | ", torn.Details)}"); }
        }
        catch (Exception e) { status = "Split failed (source untouched): " + e.Message; Debug.LogException(e); }
        finally { EditorUtility.ClearProgressBar(); }
    }
}
