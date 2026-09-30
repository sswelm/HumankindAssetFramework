using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// MODEL READER (2026-09-30, step 1 of replacing Blender): the editor's window onto GlbReader. Pick a .glb/.gltf - or
// read every model the registry names - and see what the reader makes of it: the counts, one row per mesh, the skins,
// the animations, the read time, and a refusal by name when the file holds something the reader does not implement.
// Read-only: nothing in the pipeline consumes the model yet; this is where a source is inspected before it does (the
// Vehicle Lab's Probe is the first consumer planned - the same parts list, without the 25 s Blender round trip).
public class ModelReaderWindow : EditorWindow
{
    [MenuItem("Tools/HAF/Model Reader (GLB)…")]
    public static void Open() { var w = GetWindow<ModelReaderWindow>(false, "Model Reader"); w.minSize = new Vector2(560, 420); }

    string path = "";
    string status = "";
    HafModel model;
    double readMs;
    long fileBytes;
    Vector2 scroll;
    bool showMeshes = true, showSkins = true, showAnims = true, showMaterials;

    void OnGUI()
    {
        EditorGUILayout.LabelField("Model Reader — what GlbReader sees in a .glb / .gltf", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Step 1 of replacing Blender: the C# reader, read-only. The pipeline still runs on Blender; this shows the model the later steps will consume. " +
                                "A file the reader cannot read whole is refused by name (sparse accessors, Draco, a malformed file).", MessageType.None);
        using (new EditorGUILayout.HorizontalScope())
        {
            path = EditorGUILayout.TextField("Model file", path);
            if (GUILayout.Button("Browse…", GUILayout.Width(70)))
            {
                string p = EditorUtility.OpenFilePanelWithFilters("Pick a model", string.IsNullOrEmpty(path) ? Application.dataPath : Path.GetDirectoryName(path), new[] { "glTF", "glb,gltf", "All files", "*" });
                if (!string.IsNullOrEmpty(p)) { path = p; ReadOne(); }
            }
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(path)))
                if (GUILayout.Button(new GUIContent("Read", "Read this file with GlbReader and show what it holds."), GUILayout.Width(80))) ReadOne();
            if (GUILayout.Button(new GUIContent("Read every registry model", "Read every .glb/.gltf the model registry names, one line per file in the Console, and the totals here."), GUILayout.Width(190))) ReadRegistry();
        }
        if (status.Length > 0) EditorGUILayout.HelpBox(status, status.StartsWith("⚠") ? MessageType.Error : MessageType.Info);
        if (model == null) return;

        scroll = EditorGUILayout.BeginScrollView(scroll);
        int joints = model.Skins.Sum(s => s.Joints.Length);
        EditorGUILayout.LabelField($"{Path.GetFileName(model.SourcePath)}   {fileBytes / 1e6:0.0} MB   read in {readMs:0} ms   generator: {(model.Generator.Length > 0 ? model.Generator : "-")}");
        EditorGUILayout.LabelField($"nodes {model.Nodes.Count}   meshes {model.Meshes.Count}   triangles {model.TriangleCount:N0}   vertices {model.VertexCount:N0}   materials {model.Materials.Count}   images {model.Images.Count}   skins {model.Skins.Count} ({joints} joints)   animations {model.Animations.Count}");
        if (model.ExtensionsUsed.Count > 0) EditorGUILayout.LabelField("extensions: " + string.Join(", ", model.ExtensionsUsed));

        if (showMeshes = EditorGUILayout.Foldout(showMeshes, $"Meshes ({model.Meshes.Count}) — the parts", true))
        {
            var byMesh = new Dictionary<int, List<string>>();
            for (int i = 0; i < model.Nodes.Count; i++) if (model.Nodes[i].Mesh >= 0) { if (!byMesh.TryGetValue(model.Nodes[i].Mesh, out var l)) byMesh[model.Nodes[i].Mesh] = l = new List<string>(); l.Add(model.Nodes[i].Name); }
            for (int i = 0; i < model.Meshes.Count; i++)
            {
                var m = model.Meshes[i];
                long tris = m.Primitives.Sum(p => p.TriangleCount), verts = m.Primitives.Sum(p => p.VertexCount);
                bool skinned = m.Primitives.Any(p => p.Skinned);
                string nodes = byMesh.TryGetValue(i, out var l) ? string.Join(", ", l) : "(no node)";
                string attrs = string.Join(" ", new[] { m.Primitives.Any(p => p.Normals != null) ? "N" : "", m.Primitives.Any(p => p.Tangents != null) ? "T" : "", m.Primitives.Any(p => p.Uv0 != null) ? "UV" : "", m.Primitives.Any(p => p.Uv1 != null) ? "UV1" : "", m.Primitives.Any(p => p.Colors != null) ? "C" : "", skinned ? "SKIN" : "" }.Where(a => a.Length > 0));
                EditorGUILayout.LabelField(new GUIContent($"  {(m.Name.Length > 0 ? m.Name : "(unnamed)")}  ·  {m.Primitives.Count} prim  ·  {tris:N0} tris  ·  {verts:N0} verts  ·  {attrs}  ·  node: {nodes}", $"mesh {i}"));
            }
        }
        if (showSkins = EditorGUILayout.Foldout(showSkins, $"Skins ({model.Skins.Count})", true))
            foreach (var s in model.Skins)
                EditorGUILayout.LabelField($"  {(s.Name.Length > 0 ? s.Name : "(unnamed)")}  ·  {s.Joints.Length} joints  ·  inverse bind matrices: {(s.InverseBindMatrices != null ? "yes" : "identity")}  ·  root: {(s.Skeleton >= 0 && s.Skeleton < model.Nodes.Count ? model.Nodes[s.Skeleton].Name : "-")}" +
                                           (s.Joints.Length > 126 ? "   ⚠ over the 126-bone GPU wall" : ""));
        if (showAnims = EditorGUILayout.Foldout(showAnims, $"Animations ({model.Animations.Count})", true))
            foreach (var a in model.Animations)
            {
                int keys = a.Samplers.Count == 0 ? 0 : a.Samplers.Max(s => s.KeyCount);
                var paths = a.Channels.Select(c => c.Path).Distinct();
                EditorGUILayout.LabelField($"  {(a.Name.Length > 0 ? a.Name : "(unnamed)")}  ·  {a.Duration:0.###} s  ·  {a.Channels.Count} channels ({string.Join("/", paths)})  ·  up to {keys} keys  ·  {string.Join("/", a.Samplers.Select(s => s.Interpolation).Distinct())}");
            }
        if (showMaterials = EditorGUILayout.Foldout(showMaterials, $"Materials ({model.Materials.Count}) and images ({model.Images.Count})", true))
        {
            foreach (var mt in model.Materials)
                EditorGUILayout.LabelField($"  {(mt.Name.Length > 0 ? mt.Name : "(unnamed)")}  ·  base ({mt.BaseColorFactor[0]:0.##},{mt.BaseColorFactor[1]:0.##},{mt.BaseColorFactor[2]:0.##})  ·  tex {(mt.BaseColorTexture >= 0 ? mt.BaseColorTexture.ToString() : "-")}  ·  metal {mt.MetallicFactor:0.##} rough {mt.RoughnessFactor:0.##}  ·  {mt.AlphaMode}{(mt.DoubleSided ? "  ·  double-sided" : "")}");
            foreach (var im in model.Images)
                EditorGUILayout.LabelField($"  image {(im.Name.Length > 0 ? im.Name : "(unnamed)")}  ·  {im.MimeType}  ·  {(im.Bytes != null ? (im.Bytes.Length / 1024) + " KB" : "NOT RESOLVED: " + im.Uri)}");
        }
        EditorGUILayout.EndScrollView();
    }

    void ReadOne()
    {
        model = null; status = "";
        try
        {
            fileBytes = new FileInfo(path).Length;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            model = GlbReader.Read(path);
            sw.Stop(); readMs = sw.Elapsed.TotalMilliseconds;
            status = $"Read {Path.GetFileName(path)} in {readMs:0} ms.";
        }
        catch (Exception e) { status = "⚠ NOT read — " + e.Message; Debug.LogError("[ModelReader] " + path + ": " + e.Message); }
    }

    // Every model the registry names: the drill's loop, in the editor, so a source the reader refuses is seen here first.
    void ReadRegistry()
    {
        var entries = ModelRegistry.Load();
        if (ModelRegistry.LastLoadFailed) { status = "⚠ the model registry " + ModelRegistry.LastLoadProblem; return; }
        int ok = 0, refused = 0, missing = 0; long bytes = 0; double ms = 0;
        var lines = new List<string>();
        foreach (var e in entries)
        {
            string f = e.modelFile ?? "";
            if (!f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(f)) { missing++; lines.Add($"MISSING  {e.resourceName}: {f}"); continue; }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var m = GlbReader.Read(f);
                sw.Stop(); ms += sw.Elapsed.TotalMilliseconds; bytes += new FileInfo(f).Length; ok++;
                lines.Add($"OK       {e.resourceName}: {m.TriangleCount:N0} tris, {m.Meshes.Count} meshes, {m.Skins.Sum(s => s.Joints.Length)} joints, {m.Animations.Count} anim, {sw.Elapsed.TotalMilliseconds:0} ms");
            }
            catch (Exception ex) { refused++; lines.Add($"REFUSED  {e.resourceName}: {ex.Message}"); }
        }
        Debug.Log($"[ModelReader] registry: {ok} read ({bytes / 1e6:0.0} MB in {ms:0} ms), {refused} refused, {missing} missing\n" + string.Join("\n", lines));
        status = $"Registry: {ok} model(s) read ({bytes / 1e6:0.0} MB in {ms:0} ms), {refused} refused, {missing} missing — one line per model in the Console." + (refused > 0 ? " ⚠ a refused file is one the reader cannot read whole; its line says why." : "");
        if (refused > 0) status = "⚠ " + status;
    }
}
