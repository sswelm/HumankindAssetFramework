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

    // THE PREVIEW - the proof. The model the reader produced, built as real Unity meshes with the node hierarchy's
    // transforms applied, the file's normals and UVs, and the base-colour textures decoded from the embedded images; no
    // Blender, no file in between. If the reader got any of those wrong, it is visible here. Coordinates and winding
    // are copied verbatim, as the Model Cutter's preview does (measured 2026-09-19: facing survives, the image is a
    // mirror); the Unmirror box flips X and the winding, as the Cutter's does.
    PreviewRenderUtility pru;
    GameObject inst;
    readonly List<UnityEngine.Object> previewAssets = new List<UnityEngine.Object>();
    Bounds bounds; bool boundsValid;
    Vector2 orbit = new Vector2(30f, 15f); float zoom = 1f; bool spin = true; double lastTick;
    bool showPreview = true, unmirror, fileNormals = true, textured = true, clipStart = true;
    string previewNote = "";

    void OnEnable() { EditorApplication.update += Tick; lastTick = EditorApplication.timeSinceStartup; }
    void OnDisable() { EditorApplication.update -= Tick; DestroyPreview(); if (pru != null) { try { pru.Cleanup(); } catch { } pru = null; } }
    void Tick() { double now = EditorApplication.timeSinceStartup; if (spin && inst != null) { orbit.x += (float)((now - lastTick) * 20.0); Repaint(); } lastTick = now; }

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
            using (new EditorGUI.DisabledScope(model == null))
                if (GUILayout.Button(new GUIContent("Save as GLB…", "Write the model as the reader holds it to a new .glb (GlbWriter) - open the result in the Model Cutter or Blender to see the round trip; a model the writer cannot write as it is is refused by name."), GUILayout.Width(110))) SaveAs();
        }
        if (status.Length > 0) EditorGUILayout.HelpBox(status, status.StartsWith("⚠") ? MessageType.Error : MessageType.Info);
        if (model == null) return;

        using (new EditorGUILayout.HorizontalScope())
        {
            showPreview = EditorGUILayout.ToggleLeft(new GUIContent("Preview", "The model as the reader read it, built as Unity meshes: hierarchy transforms, normals, UVs and textures all come from the reader."), showPreview, GUILayout.Width(70));
            bool wantUnmirror = EditorGUILayout.ToggleLeft(new GUIContent("Unmirror", "Flip X and the winding: the image as the file has it (glTF is right-handed; the verbatim copy is a mirror image, facing correct)."), unmirror, GUILayout.Width(80));
            bool wantNormals = EditorGUILayout.ToggleLeft(new GUIContent("File normals", "Use the normals the reader read; off = let Unity recompute them. A model that looks right only one way has a normals problem."), fileNormals, GUILayout.Width(100));
            bool wantTextured = EditorGUILayout.ToggleLeft(new GUIContent("Textures", "Decode the embedded images and map the base-colour texture through the reader's UVs."), textured, GUILayout.Width(80));
            bool wantClipStart = model.Animations.Count == 0 ? clipStart : EditorGUILayout.ToggleLeft(new GUIContent("Clip start", "Pose the nodes at the first animation's start (what a viewer shows; the parity drill's reference). Off = the file's static transforms."), clipStart, GUILayout.Width(80));
            spin = EditorGUILayout.ToggleLeft("Spin", spin, GUILayout.Width(50));
            if (wantUnmirror != unmirror || wantNormals != fileNormals || wantTextured != textured || wantClipStart != clipStart) { unmirror = wantUnmirror; fileNormals = wantNormals; textured = wantTextured; clipStart = wantClipStart; BuildPreview(); }
        }
        if (showPreview)
        {
            if (inst == null && previewNote.Length == 0) BuildPreview();
            if (previewNote.Length > 0) EditorGUILayout.HelpBox(previewNote, MessageType.Warning);
            else
            {
                var rect = GUILayoutUtility.GetRect(10, 10000, 300, 300);
                HandlePreviewInput(rect);
                if (Event.current.type == EventType.Repaint) RenderPreview(rect);
            }
        }

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

    // STEP 2 (the writer): the model back to a file, so a round trip can be tried by hand on any model the reader opened.
    void SaveAs()
    {
        if (model == null) return;
        string suggested = Path.GetFileNameWithoutExtension(model.SourcePath) + "_haf.glb";
        string p = EditorUtility.SaveFilePanel("Save the model as GLB", string.IsNullOrEmpty(model.SourcePath) ? Application.dataPath : Path.GetDirectoryName(model.SourcePath), suggested, "glb");
        if (string.IsNullOrEmpty(p)) return;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            GlbWriter.Write(model, p);
            sw.Stop();
            status = $"Wrote {Path.GetFileName(p)} ({new FileInfo(p).Length / 1e6:0.0} MB) in {sw.Elapsed.TotalMilliseconds:0} ms. Material extensions are carried verbatim; sampler settings and morph-target data are not (the writer's contract).";
            if (p.Replace(Path.DirectorySeparatorChar, '/').StartsWith(Application.dataPath.Replace(Path.DirectorySeparatorChar, '/'), StringComparison.OrdinalIgnoreCase)) AssetDatabase.Refresh();
        }
        catch (Exception e) { status = "⚠ NOT written — " + e.Message; Debug.LogError("[ModelReader] " + p + ": " + e.Message); }
    }

    void ReadOne()
    {
        model = null; status = ""; DestroyPreview();
        try
        {
            fileBytes = new FileInfo(path).Length;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            model = GlbReader.Read(path);
            sw.Stop(); readMs = sw.Elapsed.TotalMilliseconds;
            status = $"Read {Path.GetFileName(path)} in {readMs:0} ms.";
            BuildPreview();
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

    // ---------------------------------------------------------------- the preview

    void DestroyPreview()
    {
        if (inst != null) { DestroyImmediate(inst); inst = null; }
        foreach (var a in previewAssets) if (a != null) DestroyImmediate(a);
        previewAssets.Clear(); boundsValid = false; previewNote = "";
    }

    void BuildPreview()
    {
        DestroyPreview();
        if (model == null) return;
        if (model.TriangleCount > 20_000_000) { previewNote = $"no preview: {model.TriangleCount:N0} triangles is over the 20 M the preview builds"; return; }
        try
        {
            if (pru == null) pru = new PreviewRenderUtility();
            var sh = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
            var world = HafTransforms.WorldMatrices(model, clipStart ? HafTransforms.PoseAt(model, 0, 0.0) : null);
            var textures = new Dictionary<int, Texture2D>();
            var materials = new Dictionary<int, Material>();
            Material MaterialFor(int index)
            {
                if (materials.TryGetValue(index, out var have)) return have;
                var mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                var hm = index >= 0 && index < model.Materials.Count ? model.Materials[index] : null;
                if (hm != null)
                {
                    mat.color = new Color(hm.BaseColorFactor[0], hm.BaseColorFactor[1], hm.BaseColorFactor[2], 1f);
                    if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 1f - hm.RoughnessFactor);
                    if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", hm.MetallicFactor);
                    if (textured && hm.BaseColorTexture >= 0 && hm.BaseColorTexture < model.Textures.Count)
                    {
                        int img = model.Textures[hm.BaseColorTexture].Source;
                        if (img >= 0 && img < model.Images.Count && model.Images[img].Bytes != null)
                        {
                            if (!textures.TryGetValue(img, out var tex))
                            {
                                tex = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave, name = model.Images[img].Name };
                                if (!tex.LoadImage(model.Images[img].Bytes)) { DestroyImmediate(tex); tex = null; }
                                textures[img] = tex; if (tex != null) previewAssets.Add(tex);
                            }
                            if (tex != null) mat.mainTexture = tex;
                        }
                    }
                }
                previewAssets.Add(mat);
                materials[index] = mat;
                return mat;
            }

            inst = new GameObject("__modelReaderPreview") { hideFlags = HideFlags.HideAndDontSave };
            float sx = unmirror ? -1f : 1f;
            bool any = false;
            for (int ni = 0; ni < model.Nodes.Count; ni++)
            {
                var node = model.Nodes[ni];
                if (node.Mesh < 0) continue;
                var hm = model.Meshes[node.Mesh];
                var mesh = new Mesh { name = node.Name, hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
                var subs = new List<int[]>(); var mats = new List<Material>();
                bool allNormals = true;
                foreach (var p in hm.Primitives)
                {
                    if (p.Mode != 4) continue;   // the preview draws triangles; strips and fans are read but not drawn
                    // positions and normals through the same matrices: the node's, or per vertex the weighted blend of its joints
                    // at the chosen pose (review of PR #109: the normals once went through identity while the positions were posed)
                    var gl = HafTransforms.WorldPositions(model, ni, p, world);
                    var gn = HafTransforms.WorldNormals(model, ni, p, world);
                    int baseIndex = verts.Count;
                    for (int v = 0; v < p.VertexCount; v++)
                    {
                        verts.Add(new Vector3(sx * (float)gl[v * 3], (float)gl[v * 3 + 1], (float)gl[v * 3 + 2]));
                        if (gn != null) norms.Add(new Vector3(sx * (float)gn[v * 3], (float)gn[v * 3 + 1], (float)gn[v * 3 + 2]));
                        else allNormals = false;
                        // the UV set the material's base colour selects (review of PR #109: UV0 was used whatever texCoord said); glTF's origin is top-left, Unity's bottom-left
                        var uvSet = p.Material >= 0 && p.Material < model.Materials.Count && model.Materials[p.Material].BaseColorTexCoord == 1 && p.Uv1 != null ? p.Uv1 : p.Uv0;
                        uvs.Add(uvSet != null ? new Vector2(uvSet[v * 2], 1f - uvSet[v * 2 + 1]) : Vector2.zero);
                    }
                    int[] tri;
                    if (p.Indices != null) { tri = new int[p.Indices.Length]; for (int i = 0; i < tri.Length; i++) tri[i] = baseIndex + p.Indices[i]; }
                    else { tri = new int[p.VertexCount]; for (int i = 0; i < tri.Length; i++) tri[i] = baseIndex + i; }
                    if (unmirror) for (int t = 0; t + 2 < tri.Length; t += 3) { int tmp = tri[t + 1]; tri[t + 1] = tri[t + 2]; tri[t + 2] = tmp; }
                    subs.Add(tri); mats.Add(MaterialFor(p.Material));
                }
                if (subs.Count == 0) continue;
                mesh.SetVertices(verts);
                mesh.SetUVs(0, uvs);
                mesh.subMeshCount = subs.Count;
                for (int si = 0; si < subs.Count; si++) mesh.SetTriangles(subs[si], si, false);
                if (fileNormals && allNormals && norms.Count == verts.Count) mesh.SetNormals(norms); else mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                previewAssets.Add(mesh);
                var go = new GameObject(node.Name.Length > 0 ? node.Name : "node " + ni) { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(inst.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();
                any = true;
            }
            if (!any) { previewNote = "no preview: the file has no triangle primitive on any node"; DestroyPreview(); return; }
            pru.AddSingleGO(inst);
            var rs = inst.GetComponentsInChildren<Renderer>();
            bounds = rs[0].bounds; foreach (var r in rs) bounds.Encapsulate(r.bounds); boundsValid = true;
        }
        catch (Exception e) { previewNote = "no preview: " + e.Message; Debug.LogError("[ModelReader] preview: " + e); DestroyPreview(); }
    }

    void HandlePreviewInput(Rect rect)
    {
        var e = Event.current;
        if (!rect.Contains(e.mousePosition)) return;
        if (e.type == EventType.ScrollWheel) { zoom = Mathf.Clamp(zoom * Mathf.Pow(1.12f, e.delta.y > 0 ? 1f : -1f), 0.2f, 5f); e.Use(); Repaint(); }
        else if (e.type == EventType.MouseDrag && e.button == 0) { spin = false; orbit += new Vector2(e.delta.x, -e.delta.y) * 0.7f; orbit.y = Mathf.Clamp(orbit.y, -89f, 89f); e.Use(); Repaint(); }
    }

    void RenderPreview(Rect rect)
    {
        if (!boundsValid || pru == null || inst == null) return;
        pru.BeginPreview(rect, GUIStyle.none);
        var cam = pru.camera;
        float radius = Mathf.Max(bounds.extents.magnitude, 0.1f);
        float dist = radius * 2.2f * zoom;
        var rot = Quaternion.Euler(-orbit.y, orbit.x, 0f);
        cam.transform.position = bounds.center + rot * (Vector3.back * dist);
        cam.transform.rotation = Quaternion.LookRotation(bounds.center - cam.transform.position);
        cam.nearClipPlane = Mathf.Max(0.001f, dist * 0.01f); cam.farClipPlane = dist + radius * 4f; cam.fieldOfView = 30f;
        pru.lights[0].intensity = 1.3f; pru.lights[0].transform.rotation = Quaternion.Euler(45f, 45f, 0f);
        if (pru.lights.Length > 1) pru.lights[1].intensity = 0.6f;
        pru.ambientColor = new Color(0.3f, 0.3f, 0.3f);
        cam.Render();
        GUI.DrawTexture(rect, pru.EndPreview(), ScaleMode.StretchToFill, false);
    }
}
