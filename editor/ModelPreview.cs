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
    }

    public sealed class Result
    {
        public GameObject Root;           // null when nothing is drawable; Note says why
        public string Note = "";
        public int Meshes, Vertices, Triangles;
    }

    public const long TriangleCap = 20_000_000;

    public static Result Build(HafModel model, Options o, List<UnityEngine.Object> assets)
    {
        var r = new Result();
        if (model.TriangleCount > TriangleCap) { r.Note = $"no preview: {model.TriangleCount:N0} triangles is over the {TriangleCap / 1_000_000} M the preview builds"; return r; }
        var sh = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
        var world = HafTransforms.WorldMatrices(model, o.ClipStart ? HafTransforms.PoseAt(model, 0, 0.0) : null);
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
                if (o.Textured && hm.BaseColorTexture >= 0 && hm.BaseColorTexture < model.Textures.Count)
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
            assets.Add(mat);
            materials[index] = mat;
            return mat;
        }

        var root = new GameObject("__modelReaderPreview") { hideFlags = HideFlags.HideAndDontSave };
        float sx = o.Unmirror ? -1f : 1f;
        for (int ni = 0; ni < model.Nodes.Count; ni++)
        {
            var node = model.Nodes[ni];
            if (node.Mesh < 0) continue;
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
                subs.Add(tri.ToArray()); mats.Add(MaterialFor(p.Material));
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
        if (r.Meshes == 0) { r.Note = "no preview: the file has no triangle primitive on any node"; UnityEngine.Object.DestroyImmediate(root); return r; }
        r.Root = root;
        return r;
    }
}
