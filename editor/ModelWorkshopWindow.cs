// ModelWorkshopWindow.cs — SELECTIVE part surgery on a GLB, before it enters the Vehicle Lab.
//
// Born from the Great Galley (2026-09-06): after ruthless vertex cutting the ship carried floating junk islands
// that could not be marked Ignore in the Vehicle Lab, because they live INSIDE parts shared with hull geometry —
// one row, one role, junk welded to keel. The blunt fix existed (Model Tools ▸ Split disconnected GLB parts…,
// PR #19) but it explodes EVERY part — the Khalandion's rigging alone became ~1,500 objects, far past reviewable.
//
// The Workshop is the aimed version of the same lossless splitter: Probe lists every mesh-carrying node with its
// triangle count and how many disconnected islands it holds; you check exactly the parts that hide junk; Split
// writes a new GLB in which ONLY those become _Part_NNN children (GlbDisconnectedParts' method untouched:
// byte-identical vertex data, appended index accessors, triangle-total verification). The output then goes
// through the normal pipeline: Vehicle Lab probe → mark the junk Ignore → rig → Factory bake.
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
        public string node;      // the node name Split filters on
        public string mesh;
        public int tris;
        public int islands;      // 1 = nothing to split (row disabled)
        public string blocked;   // non-null = the analyzer's reason this part cannot be split
        public bool split;       // the checkbox
    }

    [SerializeField] string srcFile = "";
    [SerializeField] string outGlb = "";
    [SerializeField] List<Row> rows = new List<Row>();
    [SerializeField] Vector2 scroll;
    string status = "Pick a GLB and press Probe parts.";

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
                if (!string.IsNullOrEmpty(p)) { srcFile = p.Replace('\\', '/'); outGlb = ""; rows.Clear(); }
            }
        }
        if (string.IsNullOrEmpty(outGlb) && !string.IsNullOrEmpty(srcFile))
            outGlb = Path.Combine(Path.GetDirectoryName(srcFile), Path.GetFileNameWithoutExtension(srcFile) + "_split.glb").Replace('\\', '/');
        using (new EditorGUILayout.HorizontalScope())
        {
            outGlb = EditorGUILayout.TextField(new GUIContent("Output GLB", "Where the split copy is written — feed THIS file to the Vehicle Lab afterwards."), outGlb);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                string p = EditorUtility.SaveFilePanel("Write split GLB", Path.GetDirectoryName(srcFile), Path.GetFileNameWithoutExtension(srcFile) + "_split", "glb");
                if (!string.IsNullOrEmpty(p)) outGlb = p.Replace('\\', '/');
            }
        }

        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(srcFile) || !File.Exists(srcFile)))
            if (GUILayout.Button(new GUIContent("Probe parts", "Read the GLB (no Blender, nothing written) and list every mesh-carrying node with its triangle count and disconnected-island count."), GUILayout.Height(24)))
                Probe();

        if (rows.Count > 0)
        {
            int splittable = rows.Count(r => r.islands > 1 && r.blocked == null);
            int chosen = rows.Count(r => r.split);
            EditorGUILayout.LabelField($"Parts ({rows.Count} node(s), {splittable} with more than one island) — check the parts to split:", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Check all splittable", GUILayout.Width(140))) foreach (var r in rows) r.split = r.islands > 1 && r.blocked == null;
                if (GUILayout.Button("Uncheck all", GUILayout.Width(100))) foreach (var r in rows) r.split = false;
                // A 300-island rope part is a legitimate but LOUD choice — say what a check costs before Split.
                EditorGUILayout.LabelField(chosen > 0 ? $"{chosen} checked → +{rows.Where(r => r.split).Sum(r => r.islands) - chosen} new part(s) in the output" : " ", EditorStyles.miniLabel);
            }
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Min(320, 22 * rows.Count + 8)));
            foreach (var r in rows)
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(r.islands <= 1 || r.blocked != null))
                        r.split = EditorGUILayout.Toggle(r.split, GUILayout.Width(20));
                    string label = r.blocked != null ? $"{r.node}   — skipped: {r.blocked}"
                                 : $"{r.node}   ({r.tris:N0} tris, {(r.islands == 1 ? "1 island — already whole" : r.islands.ToString("N0") + " islands")})";
                    EditorGUILayout.LabelField(label, r.islands > 1 && r.blocked == null ? EditorStyles.label : EditorStyles.miniLabel);
                }
            EditorGUILayout.EndScrollView();

            using (new EditorGUI.DisabledScope(chosen == 0 || string.IsNullOrEmpty(outGlb)))
                if (GUILayout.Button(new GUIContent($"Split {chosen} checked part(s)  →  {(string.IsNullOrEmpty(outGlb) ? "(set the Output GLB)" : Path.GetFileName(outGlb))}",
                        "Writes the output GLB with ONLY the checked parts exploded into _Part_NNN children. The source file is never touched."), GUILayout.Height(28)))
                    SplitChecked();
        }

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
    }

    void Probe()
    {
        try
        {
            rows = GlbDisconnectedParts.Analyze(File.ReadAllBytes(srcFile))
                .Select(p => new Row { node = p.NodeName, mesh = p.MeshName, tris = p.Triangles, islands = p.Islands, blocked = p.Blocked })
                .OrderByDescending(r => r.islands).ThenBy(r => r.node, StringComparer.OrdinalIgnoreCase).ToList();
            int multi = rows.Count(r => r.islands > 1 && r.blocked == null);
            status = multi == 0 ? "Every part is a single attached island — nothing to split."
                   : $"{rows.Count} part(s); {multi} hold more than one island. Check the ones hiding junk (a huge island count usually means ropes/rigging — splitting those explodes the part list; usually leave them whole).";
        }
        catch (Exception e) { rows.Clear(); status = "Probe failed: " + e.Message; }
    }

    void SplitChecked()
    {
        if (File.Exists(outGlb) && !EditorUtility.DisplayDialog("Overwrite existing file?", outGlb, "Overwrite", "Cancel")) return;
        try
        {
            EditorUtility.DisplayProgressBar("Model Workshop", "Splitting checked parts…", 0.4f);
            var names = new HashSet<string>(rows.Where(r => r.split).Select(r => r.node));
            var result = GlbDisconnectedParts.SplitFile(srcFile, outGlb, names);
            if (!result.Changed) { status = "Nothing changed — the checked parts produced no split (see warnings in the console)."; return; }
            foreach (var w in result.Warnings) Debug.LogWarning("[Workshop] " + w);
            status = $"Split done: {result.NodesSplit} part(s) → {result.ChildPartsCreated} sub-parts, {result.SourceTriangles:N0} triangles preserved.\n{outGlb}\nNext: open it in the Vehicle Lab, Probe parts, and mark the junk islands Ignore.";
            Debug.Log($"[Workshop] {string.Join(" | ", result.Details)}");
        }
        catch (Exception e) { status = "Split failed (source untouched): " + e.Message; Debug.LogException(e); }
        finally { EditorUtility.ClearProgressBar(); }
    }
}
