// ModelPreview.cs - a HafModel built as Unity meshes: hierarchy transforms, normals, UVs and textures all from the
// reader (HafTransforms.WorldPositions / WorldNormals at the chosen pose). The Model Reader window draws it on a
// turntable; the headless editor lane (ModelReaderHeadlessTest) builds it for every registry model to prove the
// reader's output is a mesh Unity accepts, in Unity's own runtime. Every Unity object made is added to `assets` for
// the caller to destroy.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class ModelPreview
{
    public sealed class Options
    {
        public bool Unmirror = true;      // flip X and the winding: the image as the file has it (glTF is right-handed; the verbatim copy is a mirror image)
        public bool FileNormals = true;   // the normals the reader read; off = Unity recomputes them
        public bool Textured = true;      // decode the embedded images, map the base-colour texture through the reader's UVs
        public bool ClipStart = true;     // pose the nodes at the first animation's start (the parity drill's reference); off = the static transforms
        public long TriangleCap = DefaultTriangleCap;   // over this many DRAWN triangles (instances in the default scene) nothing is built
    }

    public sealed class Result
    {
        public GameObject Root;           // null when nothing is drawable; Note says why
        public string Note = "";
        public int Meshes, Vertices, Triangles;
        public long DrawnTriangles;       // what the default scene draws, counted before anything is built (the cap is judged on it)
    }

    public const long DefaultTriangleCap = 20_000_000;

    /// <summary>The Unity material for one of a model's materials: its base colour factor (times `tint`, the Vehicle Lab's
    /// per-source brightness), roughness and metallic, and - textured - its base-colour image decoded from the file. Cached per
    /// index in `materials` (and per image in `textures`); every Unity object made goes to `assets` for the caller to destroy.
    /// The Model Reader's turntable and the Vehicle Lab's in-process probe preview share it.</summary>
    public static Material MaterialFor(HafModel model, int index, bool textured, Shader sh, Dictionary<int, Texture2D> textures, Dictionary<int, Material> materials, List<UnityEngine.Object> assets, float tint = 1f)
    {
        if (materials.TryGetValue(index, out var have)) return have;
        var mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        assets.Add(mat); // owns the material even if a later texture or property operation throws
        var hm = index >= 0 && index < model.Materials.Count ? model.Materials[index] : null;
        if (hm != null)
        {
            // glTF factors are linear; Standard's colour property is sRGB, and so is what Unity's FBX importer makes of a
            // Blender export: MEASURED 2026-10-03 in the project's own import cache (Library/Artifacts) of the Salegs Revenge's
            // probe FBX - 0136_Charcoal's linear 0.137255 is imported as 0.4062, Color_A06's 0.8 as 0.90633, the sRGB encoding -
            // although io_scene_fbx writes DiffuseColor unconverted. So the old Blender previews showed these colours, and the
            // project's gamma colour space shows them as glTF means them. Convert RGB before the Lab's brightness multiplier;
            // alpha is coverage and stays linear. (A revert of this on the exporter's line alone was wrong: PR #118.)
            var colour = new Color((float)hm.BaseColorFactor[0], (float)hm.BaseColorFactor[1], (float)hm.BaseColorFactor[2],
                hm.AlphaMode == "OPAQUE" ? 1f : (float)hm.BaseColorFactor[3]).gamma;
            mat.color = new Color(colour.r * tint, colour.g * tint, colour.b * tint, colour.a);
            if (mat.HasProperty("_Mode"))
            {
                if (hm.AlphaMode == "MASK")
                {
                    mat.SetFloat("_Mode", 1f);
                    mat.SetOverrideTag("RenderType", "TransparentCutout");
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                    mat.SetInt("_ZWrite", 1);
                    mat.SetFloat("_Cutoff", (float)hm.AlphaCutoff);
                    mat.EnableKeyword("_ALPHATEST_ON");
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                }
                else if (hm.AlphaMode == "BLEND")
                {
                    // Standard's transparent mode premultiplies alpha in the shader, as the FBX importer does.
                    mat.SetFloat("_Mode", 3f);
                    mat.SetOverrideTag("RenderType", "Transparent");
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.SetInt("_ZWrite", 0);
                    mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                }
            }
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
                        if (!tex.LoadImage(model.Images[img].Bytes)) { UnityEngine.Object.DestroyImmediate(tex); tex = null; }
                        textures[img] = tex; if (tex != null) assets.Add(tex);
                    }
                    if (tex != null) mat.mainTexture = tex;
                }
            }
        }
        else if (tint != 1f) mat.color = new Color(tint, tint, tint, 1f);
        materials[index] = mat;
        return mat;
    }

    public static Result Build(HafModel model, Options o, List<UnityEngine.Object> assets)
    {
        var r = new Result();
        // what a viewer shows: the nodes the default scene reaches (review of PR #111: every node was drawn, other scenes' and orphans' too)
        if (!model.HasDefaultScene) { r.Note = model.Scenes.Count == 0 ? "no preview: the file declares no scene, so a viewer shows nothing at load" : $"no preview: the file names no default scene ({model.Scenes.Count} declared), so a viewer shows nothing at load"; return r; }
        var drawn = model.NodesInScene(model.Scene);
        // the cap is measured on what WILL be built: every node instance the default scene reaches, each with its mesh's triangles
        // (review of PR #111: the file's mesh total counted each mesh once - instances could exceed the cap, a mesh another scene uses could block a small scene)
        foreach (int ni in drawn) if (model.Nodes[ni].Mesh >= 0) foreach (var p in model.Meshes[model.Nodes[ni].Mesh].Primitives) r.DrawnTriangles += p.TriangleCount;
        if (r.DrawnTriangles > o.TriangleCap) { r.Note = $"no preview: the default scene draws {r.DrawnTriangles:N0} triangles, over the {o.TriangleCap:N0} the preview builds"; return r; }
        var sh = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
        var world = HafTransforms.WorldMatrices(model, o.ClipStart ? HafTransforms.PoseAt(model, 0, 0.0) : null);
        var textures = new Dictionary<int, Texture2D>();
        var materials = new Dictionary<int, Material>();
        Material MatFor(int index) => MaterialFor(model, index, o.Textured, sh, textures, materials, assets);

        var root = new GameObject("__modelReaderPreview") { hideFlags = HideFlags.HideAndDontSave };
        float sx = o.Unmirror ? -1f : 1f;
        for (int ni = 0; ni < model.Nodes.Count; ni++)
        {
            var node = model.Nodes[ni];
            if (node.Mesh < 0 || !drawn.Contains(ni)) continue;
            var hm = model.Meshes[node.Mesh];
            if (hm.Primitives.All(p => p.TriangleCount == 0)) continue;   // lines and points only: nothing to draw, and no Mesh allocated for it (review of PR #111: one leaked per rebuild)
            var mesh = new Mesh { name = node.Name, hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
            var subs = new List<int[]>(); var mats = new List<Material>();
            bool allNormals = true;
            foreach (var p in hm.Primitives)
            {
                if (p.TriangleCount == 0) continue;   // lines and points draw no face
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
                // every triangle as drawn - strips and fans unrolled with their winding (HafPrimitive.Triangles); mirrored = the winding flipped
                var tri = new List<int>((int)p.TriangleCount * 3);
                foreach (var (a, b, c) in p.Triangles())
                {
                    tri.Add(baseIndex + a);
                    if (o.Unmirror) { tri.Add(baseIndex + c); tri.Add(baseIndex + b); } else { tri.Add(baseIndex + b); tri.Add(baseIndex + c); }
                }
                subs.Add(tri.ToArray()); mats.Add(MatFor(p.Material));
                r.Triangles += tri.Count / 3;
            }
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = subs.Count;
            for (int si = 0; si < subs.Count; si++) mesh.SetTriangles(subs[si], si, false);
            if (o.FileNormals && allNormals && norms.Count == verts.Count) mesh.SetNormals(norms); else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            assets.Add(mesh);
            var go = new GameObject(node.Name.Length > 0 ? node.Name : "node " + ni) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();
            r.Meshes++; r.Vertices += verts.Count;
        }
        if (r.Meshes == 0) { r.Note = "no preview: the default scene reaches no node with a triangle primitive"; UnityEngine.Object.DestroyImmediate(root); return r; }
        r.Root = root;
        return r;
    }
}
