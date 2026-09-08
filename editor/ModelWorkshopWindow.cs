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
        public bool split;       // the checkbox
    }

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
    bool analyzePending;   // slider moved: recount on the first Layout pass after the drag releases
    [SerializeField] Vector2 scroll;
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

    void OnDisable() => DestroyPreview();

    void OnGUI()
    {
        EditorGUILayout.LabelField("Model Workshop — split chosen parts into their disconnected islands", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("For a part whose junk islands share a mesh with real geometry: split ONLY that part, then mark the junk Ignore in the Vehicle Lab. Lossless — vertex data, materials, skins and animations are preserved; only the checked parts gain _Part_NNN children.", EditorStyles.wordWrappedMiniLabel);

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
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Min(330, 22 * shown.Count + 8)));   // cap 220 -> 330 (2026-09-08 user request: +50% — a real ship's part list is dozens of rows)
            foreach (var r in shown)
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(r.islands <= 1 || r.blocked != null))
                        r.split = EditorGUILayout.Toggle(r.split, GUILayout.Width(20));
                    bool isSel = selectedIdx == r.nodeIndex;
                    string label = r.blocked != null ? $"{(isSel ? "◉ " : "")}{r.node}   — skipped: {r.blocked}"
                                 : $"{(isSel ? "◉ " : "")}{r.node}   ({r.tris:N0} tris, {(r.islands == 1 ? "1 island — already whole" : r.islands.ToString("N0") + " islands")})";
                    // the row label is a BUTTON, exactly like the Vehicle Lab: click = highlight + frame in the preview
                    if (GUILayout.Button(label, isSel ? EditorStyles.whiteLabel : (r.islands > 1 && r.blocked == null ? EditorStyles.label : EditorStyles.miniLabel)))
                    { selectedIdx = isSel ? -1 : r.nodeIndex; SelectRow(isSel ? "" : r.node); }
                }
            EditorGUILayout.EndScrollView();

            if (inst != null)
            {
                EditorGUILayout.LabelField("Preview   (drag = orbit · middle/right-drag = pan · scroll = zoom · click a part row to highlight)", EditorStyles.miniBoldLabel);
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
        }

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
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
        try
        {
            rows = GlbDisconnectedParts.Analyze(File.ReadAllBytes(srcFile), mergePct / 100.0)
                .Select(p => new Row { nodeIndex = p.NodeIndex, node = p.NodeName, mesh = p.MeshName, tris = p.Triangles, islands = p.Islands, blocked = p.Blocked, split = kept.Contains(p.NodeIndex) })
                .OrderBy(r => NaturalPrefix(r.node), StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => NaturalNumber(r.node))
                .ThenBy(r => r.node, StringComparer.OrdinalIgnoreCase).ToList();
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
        selectedIdx = -1;
        SelectRow("");
        if (inst != null) DestroyImmediate(inst);
        inst = null;
        if (pru != null) { pru.Cleanup(); pru = null; }
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
        if (e.type == EventType.ScrollWheel) { zoom = Mathf.Clamp(zoom * Mathf.Pow(1.12f, e.delta.y > 0 ? 1f : -1f), 0.2f, 50f); e.Use(); Repaint(); }
        else if (e.type == EventType.MouseDrag && e.button == 0) { orbit += new Vector2(e.delta.x, -e.delta.y) * 0.7f; orbit.y = Mathf.Clamp(orbit.y, -89f, 89f); e.Use(); Repaint(); }
        else if (e.type == EventType.MouseDrag && (e.button == 1 || e.button == 2)) { previewPan += new Vector2(-e.delta.x, e.delta.y) * 0.0035f; e.Use(); Repaint(); }
    }

    void RenderPreview(Rect rect)
    {
        if (inst == null || pru == null) return;
        if (!boundsValid)
        {
            bool first = true;
            foreach (var r in inst.GetComponentsInChildren<Renderer>())
            { if (r == null) continue; if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds); }
            boundsValid = !first;
            if (boundsValid) fullRadius = bounds.extents.magnitude;
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
