// VehicleProbePreview.cs - the Vehicle Lab's turntable instance built from the C# probe's own parts (step 3d of replacing
// Blender, 2026-10-03), where the Blender probe exported a preview FBX of the split scene for Unity to import and instantiate.
// One GameObject per part, named EXACTLY as its row names it (the Lab highlights and zooms by that name; a bone row finds its
// shards by the row's dominant bone), its vertices the ones the verdicts read: world space, the second model baked, the
// placements applied, the first model posed at its first clip's start. Unity's frame is the one the FBX import gave the old
// preview - Blender's (X, Y, Z) is Unity's (X, Z, Y): the exporter's -Z forward / Y up puts Blender's Z up and its Y along
// the FBX's -Z, and Unity's handedness flip negates that Z again - with every face rewound for the flip, so it faces the same
// way. Materials as the Model Reader draws them (ModelPreview.MaterialFor: base colour, roughness, metallic, the base-colour
// image through the file's UVs), tinted by the Lab's per-source brightness dial, which the Blender path baked into the
// preview's textures. Every Unity object made goes to `assets` for the Lab to destroy with the instance.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class VehicleProbePreview
{
    public static GameObject Build(VehicleProbe.Result r, HafModel[] models, float[] brightness, List<UnityEngine.Object> assets)
    {
        var root = new GameObject("__vehicleProbePreview") { hideFlags = HideFlags.HideAndDontSave };
        var sh = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
        var textures = models.Select(_ => new Dictionary<int, Texture2D>()).ToArray();
        var materials = models.Select(_ => new Dictionary<int, Material>()).ToArray();
        foreach (var part in r.Parts)
        {
            var pm = part.Mesh;
            if (pm == null || pm.Count == 0 || pm.Tris.Length == 0) continue;   // lines and points draw no face
            int src = Math.Min(Math.Max(part.Source, 0), models.Length - 1);
            var model = models[src];
            float tint = brightness != null && src < brightness.Length && brightness[src] > 0f ? brightness[src] : 1f;
            var mesh = new Mesh { name = part.Name, hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            var verts = new Vector3[pm.Count]; var norms = new Vector3[pm.Count]; var uvs = new Vector2[pm.Count];
            bool normalsOk = true;
            for (int i = 0; i < pm.Count; i++)
            {
                verts[i] = new Vector3(pm.World[i * 3], pm.World[i * 3 + 2], pm.World[i * 3 + 1]);
                var nv = new Vector3(pm.Normal[i * 3], pm.Normal[i * 3 + 2], pm.Normal[i * 3 + 1]);
                if (float.IsNaN(nv.x) || float.IsNaN(nv.y) || float.IsNaN(nv.z) || nv.sqrMagnitude < 0.25f) normalsOk = false;
                norms[i] = nv;
                uvs[i] = pm.Uv != null && pm.Uv.Length >= pm.Count * 2 ? new Vector2(pm.Uv[i * 2], 1f - pm.Uv[i * 2 + 1]) : Vector2.zero;   // glTF's origin is top-left, Unity's bottom-left
            }
            // one submesh per material, in the order the faces first use them; the winding reversed for the frame's mirror
            var order = new List<int>(); var subs = new Dictionary<int, List<int>>();
            for (int f = 0; f < pm.Tris.Length / 3; f++)
            {
                int mat = pm.TriMaterial != null && f < pm.TriMaterial.Length ? pm.TriMaterial[f] : -1;
                if (!subs.TryGetValue(mat, out var tri)) { subs[mat] = tri = new List<int>(); order.Add(mat); }
                tri.Add(pm.Tris[f * 3]); tri.Add(pm.Tris[f * 3 + 2]); tri.Add(pm.Tris[f * 3 + 1]);
            }
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = order.Count;
            for (int si = 0; si < order.Count; si++) mesh.SetTriangles(subs[order[si]], si, false);
            if (normalsOk) mesh.SetNormals(norms); else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            assets.Add(mesh);
            var go = new GameObject(part.Name) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = order.Select(mat => ModelPreview.MaterialFor(model, mat, true, sh, textures[src], materials[src], assets, tint)).ToArray();
        }
        return root;
    }
}
