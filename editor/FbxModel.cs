using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// FbxModel.cs - the HafModel Blender's glTF exporter would write of an FBX file it imported (step 6a of the native FBX
// reader, 2026-11-01): FbxImport's scene through the exporter's rules the earlier ports hold - the node tree
// (BlenderExportTree's order: the scene's objects without a parent in creation order, an object's children by name
// (BLI_strcasecmp, stable), the members of a node indexed before it: children, then mesh, then the node; a transform
// decomposed by VehicleProbe.ExporterTrs), the mesh layout (BlenderExport.MeshPrimitives: the dots per corner sorted and made
// unique per material slot) over the mesh TESSELLATED as Mesh.loop_triangles tessellates it (mesh_tessellate.cc: a triangle
// as is; a quad (0, 1, 2) + (0, 2, 3), or (0, 1, 3) + (1, 2, 3) when is_quad_flip_v3_first_third_fast says the 0-2 split is
// folded; an n-gon projected by axis_dominant_v3_to_m3_negate of its Newell normal and ear-clipped by BLI_polyfill_calc
// without beautify), with the corner normals of the POLYGON mesh (VehicleProbe.PolygonCornerNormals) carried to the triangles'
// corners; a mesh datablock shared by objects with the same material slots is written once; materials by name in first-use
// order, each with its fields, textures, images and samplers as FbxMaterials.Export writes them (sub-step 6b, numbered at the
// material's first use as the exporter's traversal numbers them). A mesh object whose mesh has no triangle is written as a
// node without a mesh.
// Proof: tools/fbx_model_drill.sh.
public static class FbxModel
{
    public static HafModel Read(string path) => Build(FbxImport.Read(path));

    /// <summary>The model, or null when the import named a fallback (then `why` says what).</summary>
    public static HafModel Build(FbxImport.Scene s) => Build(s, out _, out _);

    public static HafModel Build(FbxImport.Scene s, out string why) => Build(s, out why, out _);

    /// <summary>`composed` lists the images whose bytes Blender's exporter encodes anew (a composition): the drill compares their pixels.</summary>
    public static HafModel Build(FbxImport.Scene s, out string why, out HashSet<int> composed)
    {
        why = s.Fallback; composed = null;
        if (why != null) return null;
        var o = new HafModel { Generator = "HAF FbxModel" };
        var writer = new FbxMaterials.Writer(o);
        string materialWhy = null;
        // ---- the tree
        var children = new Dictionary<FbxImport.Obj, List<FbxImport.Obj>>();
        foreach (var ob in s.Objects) children[ob] = new List<FbxImport.Obj>();
        foreach (var ob in s.Objects) if (ob.Parent != null) children[ob.Parent].Add(ob);
        foreach (var list in children.Values) BlenderExportTree.StableSort(list, (a, b) => BlenderExportTree.StrCaseCmp(a.Name, b.Name));
        var roots = s.Objects.Where(ob => ob.Parent == null).ToList();
        // ---- meshes, tessellated and laid out, cached per (datablock, material slots)
        var meshCache = new Dictionary<string, int>();
        var materialIndex = new Dictionary<string, int>();   // Blender material name -> glTF material index (first use)
        int MaterialOf(FbxObject mat)
        {
            if (mat == null) return -1;
            string name = s.MaterialName[mat];
            if (!materialIndex.TryGetValue(name, out int i))
            {
                // gather_material at the material's first use: its textures, images and samplers are numbered here (6b)
                i = o.Materials.Count; materialIndex[name] = i;
                var hm = FbxMaterials.Export(writer, s.MaterialData.Materials[mat], name, out string mw);
                if (hm == null) { if (materialWhy == null) materialWhy = "material '" + name + "': " + mw; hm = new HafMaterial { Name = name, NameAbsent = false }; }
                o.Materials.Add(hm);
            }
            return i;
        }
        int indexOfNode = 0;
        var indexOf = new Dictionary<FbxImport.Obj, int>();
        int Visit(FbxImport.Obj ob)
        {
            var node = new HafNode { Name = ob.Name };
            foreach (var c in children[ob]) node.Children.Add(Visit(c));
            if (ob.Mesh != null)
            {
                var tris = Tessellate(ob.Mesh);
                if (tris.Count > 0)
                {
                    string key = ob.Mesh.Name + "\0" + string.Join("\0", ob.Materials.Select(m => m == null ? "" : s.MaterialName[m]));
                    if (!meshCache.TryGetValue(key, out int mi))
                    {
                        mi = o.Meshes.Count; meshCache[key] = mi;
                        o.Meshes.Add(ExportMesh(ob.Mesh, ob.Materials, tris, MaterialOf));
                    }
                    node.Mesh = mi;
                }
            }
            var parentWorld = ob.Parent?.World;
            VehicleProbe.ExporterTrs(parentWorld, ob.World, out var t, out var r, out var sc);
            if (t != null) node.Translation = new double[] { t[0], t[1], t[2] };
            if (r != null) node.Rotation = new double[] { r[0], r[1], r[2], r[3] };
            if (sc != null) node.Scale = new double[] { sc[0], sc[1], sc[2] };
            int idx = o.Nodes.Count; o.Nodes.Add(node); indexOf[ob] = idx;
            foreach (int c in node.Children) o.Nodes[c].Parent = idx;
            indexOfNode++;
            return idx;
        }
        var scene = new HafScene { Name = "Scene" };
        foreach (var root in roots) scene.Nodes.Add(Visit(root));
        o.Scenes.Add(scene); o.Scene = 0;
        if (materialWhy != null) { why = materialWhy; return null; }
        composed = writer.ComposedImages;
        return o;
    }

    /// <summary>One tessellated triangle: its three corners (indices into the mesh's corner arrays) and its polygon.</summary>
    public struct Tri { public int C0, C1, C2, Face; }

    /// <summary>Mesh.loop_triangles of a polygon mesh (mesh_tessellate.cc), in face order.</summary>
    public static List<Tri> Tessellate(FbxImport.Mesh m)
    {
        var tris = new List<Tri>();
        var P = m.Positions;
        int nf = m.FaceCount;
        var n1 = new float[3]; var n2 = new float[3];
        for (int f = 0; f < nf; f++)
        {
            int begin = m.FaceOffsets[f], size = m.FaceOffsets[f + 1] - begin;
            if (size == 3) { tris.Add(new Tri { C0 = begin, C1 = begin + 1, C2 = begin + 2, Face = f }); continue; }
            if (size == 4)
            {
                int v0 = m.CornerVerts[begin], v1 = m.CornerVerts[begin + 1], v2 = m.CornerVerts[begin + 2], v3 = m.CornerVerts[begin + 3];
                if (IsQuadFlipFirstThirdFast(P, v0, v1, v2, v3)) { tris.Add(new Tri { C0 = begin, C1 = begin + 1, C2 = begin + 3, Face = f }); tris.Add(new Tri { C0 = begin + 1, C1 = begin + 2, C2 = begin + 3, Face = f }); }
                else { tris.Add(new Tri { C0 = begin, C1 = begin + 1, C2 = begin + 2, Face = f }); tris.Add(new Tri { C0 = begin, C1 = begin + 2, C2 = begin + 3, Face = f }); }
                continue;
            }
            if (size < 3) continue;
            // the normal, flipped (a positive 2D cross product): Newell from the last corner, normalize_v3, (0, 0, 1) when zero
            float nx = 0f, ny = 0f, nz = 0f;
            int prev = m.CornerVerts[begin + size - 1] * 3;
            for (int j = 0; j < size; j++)
            {
                int curr = m.CornerVerts[begin + j] * 3;
                nx = (float)(nx + (float)((float)(P[prev + 1] - P[curr + 1]) * (float)(P[prev + 2] + P[curr + 2])));
                ny = (float)(ny + (float)((float)(P[prev + 2] - P[curr + 2]) * (float)(P[prev] + P[curr])));
                nz = (float)(nz + (float)((float)(P[prev] - P[curr]) * (float)(P[prev + 1] + P[curr + 1])));
                prev = curr;
            }
            VehicleProbe.NormalizeV3(ref nx, ref ny, ref nz);
            if (nx == 0f && ny == 0f && nz == 0f) nz = 1.0f;
            BlenderPolyfill.AxisDominantRows(-nx, -ny, -nz, n1, n2);   // axis_dominant_v3_to_m3_negate
            var proj = new float[2 * size];
            for (int j = 0; j < size; j++)
            {
                int v = m.CornerVerts[begin + j];
                BlenderPolyfill.ProjectV2(n1, n2, P[3 * v], P[3 * v + 1], P[3 * v + 2], out proj[2 * j], out proj[2 * j + 1]);
            }
            foreach (var t in BlenderPolyfill.Calc(proj, size, 1)) tris.Add(new Tri { C0 = begin + t[0], C1 = begin + t[1], C2 = begin + t[2], Face = f });
        }
        return tris;
    }

    /// <summary>is_quad_flip_v3_first_third_fast: the 0-2 split is folded when the triangles (0, 1, 2) and (0, 3, 2) face the same way.</summary>
    static bool IsQuadFlipFirstThirdFast(float[] P, int v1, int v2, int v3, int v4)
    {
        float d12x = (float)(P[3 * v2] - P[3 * v1]), d12y = (float)(P[3 * v2 + 1] - P[3 * v1 + 1]), d12z = (float)(P[3 * v2 + 2] - P[3 * v1 + 2]);
        float d13x = (float)(P[3 * v3] - P[3 * v1]), d13y = (float)(P[3 * v3 + 1] - P[3 * v1 + 1]), d13z = (float)(P[3 * v3 + 2] - P[3 * v1 + 2]);
        float d14x = (float)(P[3 * v4] - P[3 * v1]), d14y = (float)(P[3 * v4 + 1] - P[3 * v1 + 1]), d14z = (float)(P[3 * v4 + 2] - P[3 * v1 + 2]);
        float ax = (float)((float)(d12y * d13z) - (float)(d12z * d13y)), ay = (float)((float)(d12z * d13x) - (float)(d12x * d13z)), az = (float)((float)(d12x * d13y) - (float)(d12y * d13x));
        float bx = (float)((float)(d14y * d13z) - (float)(d14z * d13y)), by = (float)((float)(d14z * d13x) - (float)(d14x * d13z)), bz = (float)((float)(d14x * d13y) - (float)(d14y * d13x));
        float dot = (float)((float)((float)(ax * bx) + (float)(ay * by)) + (float)(az * bz));
        return dot > 0.0f;
    }

    static HafMesh ExportMesh(FbxImport.Mesh m, List<FbxObject> slots, List<Tri> tris, Func<FbxObject, int> materialOf)
    {
        int nt = tris.Count;
        // the polygon mesh's corner normals, then the triangle mesh the layout takes
        var cn = m.CustomNormal != null || m.SharpEdges != null || m.SharpFace.Any(x => x)
            ? VehicleProbe.PolygonCornerNormals(m.Positions, m.CornerVerts, m.FaceOffsets, m.SharpFace, m.SharpEdges, m.CustomNormal)
            : VehicleProbe.PolygonCornerNormals(m.Positions, m.CornerVerts, m.FaceOffsets, m.SharpFace, null, null);
        var r = new BlenderReduce.Result { VertexCount = m.Positions.Length / 3, FaceCount = nt, Positions = m.Positions, Faces = new int[3 * nt], FaceMaterial = new int[nt], FaceSharp = new bool[nt], Edges = new int[0] };
        var triNormals = new float[9 * nt];
        for (int u = 0; u < m.Uv.Count; u++) r.Uv.Add(new float[6 * nt]);
        for (int t = 0; t < nt; t++)
        {
            var tri = tris[t];
            int[] corners = { tri.C0, tri.C1, tri.C2 };
            for (int k = 0; k < 3; k++)
            {
                int c = corners[k];
                r.Faces[3 * t + k] = m.CornerVerts[c];
                for (int i = 0; i < 3; i++) triNormals[9 * t + 3 * k + i] = cn[3 * c + i];
                for (int u = 0; u < m.Uv.Count; u++) { r.Uv[u][6 * t + 2 * k] = m.Uv[u].uv[2 * c]; r.Uv[u][6 * t + 2 * k + 1] = m.Uv[u].uv[2 * c + 1]; }
            }
            r.FaceMaterial[t] = m.FaceMaterial != null ? m.FaceMaterial[tri.Face] : 0;
            r.FaceSharp[t] = m.SharpFace[tri.Face];
        }
        // the slots the exporter sees: the object's materials (none: every face in slot 0 without a material)
        for (int i = 0; i < Math.Max(1, slots.Count); i++) { r.Slots.Add((i < slots.Count && slots[i] != null ? i : -1, false)); r.SlotAlpha.Add(("OPAQUE", 0.5, 1.0)); }
        var prims = BlenderExport.MeshPrimitives(r, null, triNormals);
        var mesh = new HafMesh { Name = m.Name };
        foreach (var p in prims)
        {
            var hp = new HafPrimitive { Mode = 4, VertexCount = p.VertexCount, Positions = p.Positions, Normals = p.Normals, Indices = p.Indices };
            hp.Material = p.MaterialSlot < slots.Count ? materialOf(slots[p.MaterialSlot]) : -1;
            if (p.Uv.Count > 0) hp.Uv0 = p.Uv[0];
            if (p.Uv.Count > 1) hp.Uv1 = p.Uv[1];
            if (p.Uv.Count > 2) hp.UvMore = p.Uv.Skip(2).ToList();
            mesh.Primitives.Add(hp);
        }
        return mesh;
    }
}
