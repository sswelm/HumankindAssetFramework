// BlenderReduce.cs - prep_model.py's reduce step for one mesh object in C# (step 5 of replacing Blender, milestone c,
// 2026-10-03): the mesh as the glTF importer stores it (positions in Blender's frame - the bind pose for a skinned mesh -,
// the file normals as custom normals, sharp faces, UV layers, material slots, vertex groups), as BMesh with the face and
// vertex normals the Decimate modifier computes on conversion, collapsed by BlenderDecimate at the modifier's ratio, and
// read back out as BM_mesh_bm_to_me does (elements in creation order, the dead skipped). What this port does NOT cover is
// named in Result.Fallback and the caller keeps Blender for it.
// Read at Blender 5.1.2: io_scene_gltf2/blender/imp/mesh.py (do_primitives, set_poly_smoothing, the material slots with
// import_merge_material_slots on, the vertex groups filled from JOINTS_n/WEIGHTS_n with non-zero weights in slot order),
// MOD_decimate.cc, bmesh_mesh_convert.cc.
using System;
using System.Collections.Generic;

public static class BlenderReduce
{
    public sealed class Result
    {
        public int VertexCount, FaceCount;
        public float[] Positions;                 // Blender's frame, 3 per vertex
        public int[] Faces;                       // 3 per face
        public int[] Edges;                       // pairs
        public int[] FaceMaterial;                // material slot per face
        public bool[] FaceSharp;                  // sharp_face per face
        public List<float[]> Uv = new List<float[]>();   // per UV layer, 2 per corner
        public short[] CustomNormal;              // 2 per corner, or null when no primitive had normals
        public List<int>[] DefNr; public List<float>[] DefWeight;   // per vertex, or null when the object has no vertex groups
        public List<(string name, bool point, byte[] bytes)> Colors = new List<(string, bool, byte[])>();   // per colour layer: 4 bytes per corner, or per vertex for the point domain
        public List<(int material, bool vertexColor)> Slots = new List<(int, bool)>();   // the mesh's material slots: the glTF material (-1 none) and whether the importer built it WITH the vertex colour
        public List<(string mode, double cutoff, double factor)> SlotAlpha = new List<(string, double, double)>();   // per slot: the material's alphaMode, alphaCutoff and effective source alpha factor ("OPAQUE" without a material)
        public float Ratio;                       // the modifier's ratio as stored (float32)
        public bool Collapsed;                    // false when the modifier would return the mesh untouched
        public string Fallback;                   // non-null: the mesh is outside this port; why
    }

    /// <summary>The ratio as prep_model.py computes and the modifier stores it: min(1, max(0.001, target / total)) in
    /// double, then float32.</summary>
    public static float Ratio(long target, long total) => (float)Math.Min(1.0, Math.Max(0.001, (double)target / Math.Max(1, total)));

    // The importer gives unlit precedence; otherwise specular-glossiness uses diffuseFactor instead of baseColorFactor.
    internal static double MaterialAlphaFactor(HafMaterial material)
    {
        if (material.ExtensionsJson != null)
        {
            var extensions = GlbReader.ParseObject(material.ExtensionsJson);
            if (extensions["KHR_materials_unlit"] == null && extensions["KHR_materials_pbrSpecularGlossiness"] != null)
                return (double?)extensions["KHR_materials_pbrSpecularGlossiness"]?["diffuseFactor"]?[3] ?? 1.0;
        }
        return material.BaseColorFactor[3];
    }

    /// <summary>Why this port keeps Blender for a mesh, or null when it covers it.</summary>
    public static string FallbackReason(HafModel m, int node)
    {
        var mesh = m.Meshes[m.Nodes[node].Mesh];
        foreach (var p in mesh.Primitives)
        {
            if (p.Colors != null && !BlenderColor.TableKnown) return "colours on a CPU whose rsqrtps table is not measured";   // BlenderColor.cs
            if (p.MorphTargets > 0) return "morph targets";
            if (p.Normals != null && !BlenderTrig.Exact) return "normals in a process without the 64-bit C runtime's cosf";   // BlenderTrig.cs
        }
        return null;
    }

    public static Result Reduce(HafModel m, int node, float ratio, BlenderNames.Result names, int threads = 0)
    {
        var r = new Result { Ratio = ratio };
        r.Fallback = FallbackReason(m, node);
        if (r.Fallback != null) return r;
        int meshIndex = m.Nodes[node].Mesh, skin = m.Nodes[node].Skin;
        var mesh = m.Meshes[meshIndex];
        var layout = BlenderMesh.FromGltf(m, meshIndex, threads);
        int nv = layout.VertexCount, nf = layout.Faces.Length / 3;

        // --- the vertices as the importer stores them: Blender's frame, the bind pose for a skinned mesh
        // skin_into_bind_pose runs when the node has a skin AND a primitive has joints (num_joint_sets); every primitive of
        // such a mesh goes through the skinner (an unskinned one through joint 0, BlenderSkinner's rule)
        bool skinned = skin >= 0 && skin < m.Skins.Count && mesh.Primitives.Exists(p => p.Skinned);
        var sknr = skinned ? VehicleProbe.BlenderSkinner.Build(m, skin, names) : null;
        var P = new float[nv * 3]; var N = new float[nv * 3];
        bool hasNormals = false;
        for (int v = 0; v < nv; v++)
        {
            var p = mesh.Primitives[layout.RankPrimitive[v]]; int idx = layout.RankIndex[v];
            if (skinned) { var co = sknr.Position(p, idx); P[3 * v] = co[0]; P[3 * v + 1] = co[1]; P[3 * v + 2] = co[2]; }
            else { P[3 * v] = p.Positions[3 * idx]; P[3 * v + 1] = -p.Positions[3 * idx + 2]; P[3 * v + 2] = p.Positions[3 * idx + 1]; }
            if (p.Normals == null) { N[3 * v] = N[3 * v + 1] = N[3 * v + 2] = float.NaN; continue; }
            hasNormals = true;
            if (skinned) { var no = sknr.Normal(p, idx); N[3 * v] = no[0]; N[3 * v + 1] = no[1]; N[3 * v + 2] = no[2]; }
            else { N[3 * v] = p.Normals[3 * idx]; N[3 * v + 1] = -p.Normals[3 * idx + 2]; N[3 * v + 2] = p.Normals[3 * idx + 1]; }
        }
        // set_poly_smoothing, then normals_split_custom_set_from_vertices (the shorts per corner), then the mesh's own vertex normals
        var sharp = VehicleProbe.SharpFaces(P, layout.Faces, N);
        short[] customNormal = null;
        float[] vno;
        if (hasNormals)
        {
            var (d0, d1) = VehicleProbe.EncodeCustomShorts(P, layout.Faces, N, sharp);
            customNormal = new short[2 * nf * 3];
            for (int c = 0; c < nf * 3; c++) { customNormal[2 * c] = d0[c]; customNormal[2 * c + 1] = d1[c]; }
            // BM_mesh_bm_from_me copies mesh->vert_normals(): with custom normals, their corner normals mixed per vertex (the
            // shorts decoded) - the topology fallback cost reads these on tiny or flat geometry (the Cobra's 3 mm parts
            // collapsed differently with the plain normals)
            vno = VehicleProbe.DecodeCustomShorts(P, layout.Faces, sharp, d0, d1);
        }
        else vno = VehicleProbe.BlenderVertexNormals(P, layout.Faces, null);   // the angle-weighted face normals

        // --- material slots: with import_merge_material_slots, one slot per distinct material in order of first appearance
        // over ALL primitives; primitives without a material share one empty slot
        // a material with COLOR_0 on the primitive is ANOTHER Blender material (blender_material[vertex_color]); a primitive
        // without a material but with COLOR_0 shares one "default + vertex colour" slot, one without either the empty slot
        var slotOf = new int[mesh.Primitives.Count]; var slots = new List<(int mat, bool vc)>();
        for (int pi = 0; pi < mesh.Primitives.Count; pi++)
        {
            var key = (mesh.Primitives[pi].Material, mesh.Primitives[pi].Colors != null);
            int s = slots.IndexOf(key);
            if (s < 0) { s = slots.Count; slots.Add(key); }
            slotOf[pi] = s;
        }
        r.Slots = slots;
        foreach (var (mat, _) in slots) r.SlotAlpha.Add(mat >= 0 ? (m.Materials[mat].AlphaMode, m.Materials[mat].AlphaCutoff, MaterialAlphaFactor(m.Materials[mat])) : ("OPAQUE", 0.5, 1.0));
        var faceMaterial = new int[nf];
        for (int f = 0; f < nf; f++) faceMaterial[f] = slotOf[layout.FacePrimitive[f]];

        // --- UV layers: as many as the longest run of TEXCOORD_n on any primitive; a primitive lacking a set gives zeros;
        // v flipped as uvs_gltf_to_blender does (v * -1, then + 1, float32)
        // The importer caps TEXCOORD_n and COLOR_n at eight even when the GLB carries more.
        int numUv = 0;
        foreach (var p in mesh.Primitives) numUv = Math.Max(numUv, UvSetCount(p));
        var d = new BlenderDecimate.MeshData();
        for (int i = 0; i < numUv; i++)
        {
            var uv = new float[2 * nf * 3];
            for (int f = 0; f < nf; f++)
                for (int k = 0; k < 3; k++)
                {
                    int rank = layout.Faces[3 * f + k]; var p = mesh.Primitives[layout.RankPrimitive[rank]]; int idx = layout.RankIndex[rank];
                    var set = UvSet(p, i);
                    int c = 3 * f + k;
                    if (set == null) { uv[2 * c] = 0f; uv[2 * c + 1] = 1f; }   // the importer's zeros, flipped with the rest: (0, 1)
                    else { uv[2 * c] = set[2 * idx]; uv[2 * c + 1] = (float)(1.0f + (float)(-set[2 * idx + 1])); }
                }
            d.Uv.Add(uv);
        }
        d.CustomNormal = customNormal;

        // --- colour layers: as many as the longest run of COLOR_n on any primitive; the POINT domain when a non-triangle
        // primitive carries that set, else CORNER; a primitive lacking the set gives white (1, 1, 1, 1); RGB padded with
        // alpha 1; each value stored through the BYTE_COLOR setter (BlenderColor)
        int numCols = 0;
        foreach (var p in mesh.Primitives) numCols = Math.Max(numCols, ColorSetCount(p));
        var colorNames = new List<string>(); var colorPoint = new List<bool>();
        for (int i = 0; i < numCols; i++)
        {
            bool point = false;
            foreach (var p in mesh.Primitives) if (p.Mode != 4 && p.Mode != 5 && p.Mode != 6 && ColorSet(p, i) != null) point = true;
            colorNames.Add(i == 0 ? "Color" : "Color." + i.ToString("000")); colorPoint.Add(point);
            if (point)
            {
                var bytes = new byte[4 * nv];
                for (int v = 0; v < nv; v++) { var p = mesh.Primitives[layout.RankPrimitive[v]]; StoreColor(ColorSet(p, i), layout.RankIndex[v], bytes, 4 * v); }
                d.PointColor.Add(bytes);
            }
            else
            {
                var bytes = new byte[4 * nf * 3];
                for (int c = 0; c < nf * 3; c++) { int rank = layout.Faces[c]; var p = mesh.Primitives[layout.RankPrimitive[rank]]; StoreColor(ColorSet(p, i), layout.RankIndex[rank], bytes, 4 * c); }
                d.CornerColor.Add(bytes);
            }
        }

        // --- vertex groups: one per joint of the skin; each vertex gets its non-zero weights in slot order (JOINTS_0's four,
        // then JOINTS_1's), a repeated joint replacing its weight
        int jointSets = 0;
        foreach (var p in mesh.Primitives) jointSets = Math.Max(jointSets, p.Joints1 != null && p.Weights1 != null ? 2 : p.Skinned ? 1 : 0);
        if (skinned && jointSets > 0)
        {
            d.DefNr = new List<int>[nv]; d.DefWeight = new List<float>[nv];
            for (int v = 0; v < nv; v++)
            {
                var nrs = new List<int>(); var ws = new List<float>();
                var p = mesh.Primitives[layout.RankPrimitive[v]]; int idx = layout.RankIndex[v];
                // skin_into_bind_pose: a vertex whose weights sum (float32, set by set, slot by slot) to exactly zero - a
                // primitive without joints reads as zeros - gets weight 1.0 in set 0's first slot, whatever joint sits there
                float sum = 0f;
                for (int set = 0; set < jointSets; set++)
                {
                    var wsrc = set == 0 ? p.Weights : p.Weights1;
                    if (wsrc == null || (set == 0 ? p.Joints : p.Joints1) == null) continue;
                    for (int k = 0; k < 4; k++) sum = (float)(sum + wsrc[4 * idx + k]);
                }
                bool zeroSum = sum == 0f;
                for (int set = 0; set < jointSets; set++)
                {
                    var joints = set == 0 ? p.Joints : p.Joints1; var weights = set == 0 ? p.Weights : p.Weights1;
                    bool present = joints != null && weights != null;   // the importer fills zeros for a missing set
                    for (int k = 0; k < 4; k++)
                    {
                        float w = present ? weights[4 * idx + k] : 0f;
                        if (zeroSum && set == 0 && k == 0) w = 1.0f;
                        if (w == 0f) continue;
                        int j = present ? joints[4 * idx + k] : 0;
                        int at = nrs.IndexOf(j);
                        if (at >= 0) ws[at] = w; else { nrs.Add(j); ws.Add(w); }
                    }
                }
                d.DefNr[v] = nrs; d.DefWeight[v] = ws;
            }
        }

        // --- the BMesh as the modifier converts it: positions, the face normals recomputed (normal_tri_v3), the vertex normals copied
        var bm = BMesh.FromMesh(layout, P, sharp, faceMaterial);
        Array.Copy(vno, bm.VNo, vno.Length);
        BlenderDecimate.FaceNormalsUpdate(bm);
        if (BlenderDecimate.WouldRun(bm.TotFace, ratio)) { BlenderDecimate.Collapse(bm, ratio, d); r.Collapsed = true; }

        // --- BM_mesh_bm_to_me: elements in creation order, the dead skipped
        var newIndex = new int[bm.VertCount]; int count = 0;
        for (int v = 0; v < bm.VertCount; v++) newIndex[v] = bm.VAlive[v] ? count++ : -1;
        r.VertexCount = count; r.Positions = new float[3 * count];
        if (d.DefNr != null) { r.DefNr = new List<int>[count]; r.DefWeight = new List<float>[count]; }
        for (int v = 0; v < bm.VertCount; v++)
        {
            if (!bm.VAlive[v]) continue;
            int i = newIndex[v];
            r.Positions[3 * i] = bm.VCo[3 * v]; r.Positions[3 * i + 1] = bm.VCo[3 * v + 1]; r.Positions[3 * i + 2] = bm.VCo[3 * v + 2];
            if (d.DefNr != null) { r.DefNr[i] = d.DefNr[v]; r.DefWeight[i] = d.DefWeight[v]; }
        }
        var edges = new List<int>();
        for (int e = 0; e < bm.EdgeCount; e++) if (bm.EAlive[e]) { edges.Add(newIndex[bm.EV1[e]]); edges.Add(newIndex[bm.EV2[e]]); }
        r.Edges = edges.ToArray();
        r.FaceCount = bm.TotFace;
        r.Faces = new int[3 * bm.TotFace]; r.FaceMaterial = new int[bm.TotFace]; r.FaceSharp = new bool[bm.TotFace];
        var uvOut = new List<float[]>(); foreach (var _ in d.Uv) uvOut.Add(new float[2 * 3 * bm.TotFace]);
        var cnOut = customNormal != null ? new short[2 * 3 * bm.TotFace] : null;
        int fo = 0;
        for (int f = 0; f < bm.FaceCount; f++)
        {
            if (!bm.FAlive[f]) continue;
            int l = bm.FLFirst[f];
            for (int k = 0; k < 3; k++)
            {
                int c = 3 * fo + k;
                r.Faces[c] = newIndex[bm.LV[l]];
                for (int i = 0; i < d.Uv.Count; i++) { uvOut[i][2 * c] = d.Uv[i][2 * l]; uvOut[i][2 * c + 1] = d.Uv[i][2 * l + 1]; }
                if (cnOut != null) { cnOut[2 * c] = customNormal[2 * l]; cnOut[2 * c + 1] = customNormal[2 * l + 1]; }
                l = bm.LNext[l];
            }
            r.FaceMaterial[fo] = bm.FMat[f];
            r.FaceSharp[fo] = (bm.FFlag[f] & BMesh.FlagSmooth) == 0;
            fo++;
        }
        r.Uv = uvOut; r.CustomNormal = cnOut;
        // bpy.ops.object.modifier_apply's merge_customdata (on by default, so prep_model runs it after EVERY apply, a ratio of
        // 1 included): UVs of the corners at one vertex that lie within 12 ulps of each other are snapped together
        MergeUvsForApply(count, r.Faces, r.Uv);
        int cornerLayer = 0, pointLayer = 0;
        for (int i = 0; i < colorNames.Count; i++)
        {
            if (colorPoint[i])
            {
                var src = d.PointColor[pointLayer++]; var outb = new byte[4 * count];
                for (int v = 0; v < bm.VertCount; v++) if (bm.VAlive[v]) Array.Copy(src, 4 * v, outb, 4 * newIndex[v], 4);
                r.Colors.Add((colorNames[i], true, outb));
            }
            else
            {
                var src = d.CornerColor[cornerLayer++]; var outb = new byte[4 * 3 * bm.TotFace]; int o = 0;
                for (int f = 0; f < bm.FaceCount; f++)
                {
                    if (!bm.FAlive[f]) continue;
                    int l = bm.FLFirst[f];
                    for (int k = 0; k < 3; k++) { Array.Copy(src, 4 * l, outb, 4 * o, 4); o++; l = bm.LNext[l]; }
                }
                r.Colors.Add((colorNames[i], false, outb));
            }
        }
        return r;
    }

    /// <summary>BKE_mesh_merge_customdata_for_apply_modifier (blenkernel/intern/mesh_merge_customdata.cc): per vertex, its
    /// corners in ascending order (vert_to_corner_map sorts each group); per UV layer, the first remaining corner's UV is the
    /// reference, every later corner bitwise EQUAL to it or CLOSE (each component within 1e-12 or 12 ulps) is dropped from
    /// the list - a close one first takes the reference's value -, the dropped one replaced by the list's last; then the
    /// reference itself is replaced by the last and the round repeats until one corner is left.</summary>
    public static void MergeUvsForApply(int vertexCount, int[] cornerVerts, List<float[]> uvLayers)
    {
        if (cornerVerts.Length == 0 || uvLayers.Count == 0) return;
        var offsets = new int[vertexCount + 1];
        foreach (int v in cornerVerts) offsets[v + 1]++;
        for (int v = 0; v < vertexCount; v++) offsets[v + 1] += offsets[v];
        var fill = (int[])offsets.Clone(); var corners = new int[cornerVerts.Length];
        for (int c = 0; c < cornerVerts.Length; c++) corners[fill[cornerVerts[c]]++] = c;   // ascending within each vertex
        var merge = new List<int>();
        for (int v = 0; v < vertexCount; v++)
        {
            int n = offsets[v + 1] - offsets[v];
            if (n <= 1) continue;
            foreach (var uv in uvLayers)
            {
                merge.Clear();
                for (int i = offsets[v]; i < offsets[v + 1]; i++) merge.Add(corners[i]);
                while (merge.Count > 1)
                {
                    int iLast = merge.Count - 1;
                    int src = merge[0];
                    for (int i = 1; i <= iLast;)
                    {
                        int dst = merge[i];
                        int cls = ClassifyUv(uv[2 * src], uv[2 * src + 1], uv[2 * dst], uv[2 * dst + 1]);
                        if (cls == 0) { uv[2 * dst] = uv[2 * src]; uv[2 * dst + 1] = uv[2 * src + 1]; }
                        if (cls != 2) merge[i] = merge[iLast--];
                        else i++;
                    }
                    merge[0] = merge[iLast];
                    merge.RemoveRange(iLast, merge.Count - iLast);
                }
            }
        }
    }

    /// <summary>compare_v2_classify: 1 EQUAL (==, both components), 0 CLOSE (compare_ff_relative with 1e-12 and 12 ulps,
    /// both components), 2 APART.</summary>
    static int ClassifyUv(float a0, float a1, float b0, float b1)
    {
        if (a0 == b0 && a1 == b1) return 1;
        return CompareRelative(a0, b0) && CompareRelative(a1, b1) ? 0 : 2;
    }

    static bool CompareRelative(float a, float b)
    {
        if (Math.Abs((float)(a - b)) <= 1e-12f) return true;
        return UlpDiff(a, b) <= 12u;
    }

    /// <summary>ulp_diff_ff: the distance in representable floats; NaN is the maximum.</summary>
    static uint UlpDiff(float a, float b)
    {
        uint ua = BitConverter.ToUInt32(BitConverter.GetBytes(a), 0), ub = BitConverter.ToUInt32(BitConverter.GetBytes(b), 0);
        uint aSign = ua & 0x80000000u, bSign = ub & 0x80000000u, aAbs = ua & 0x7fffffffu, bAbs = ub & 0x7fffffffu;
        if (aAbs > 0x7f800000u || bAbs > 0x7f800000u) return 0xffffffffu;
        if (aSign == bSign) return aAbs > bAbs ? aAbs - bAbs : bAbs - aAbs;
        return aAbs + bAbs;
    }

    static void StoreColor(float[] set, int idx, byte[] into, int at)
    {
        if (set == null) { BlenderColor.ToBytes(1f, 1f, 1f, 1f, into, at); return; }
        BlenderColor.ToBytes(set[4 * idx], set[4 * idx + 1], set[4 * idx + 2], set[4 * idx + 3], into, at);
    }

    static int ColorSetCount(HafPrimitive p)
    {
        int n = 0;
        while (n < 8 && ColorSet(p, n) != null) n++;
        return n;
    }

    static float[] ColorSet(HafPrimitive p, int i)
    {
        if (i == 0) return p.Colors;
        return p.ColorMore != null && i - 1 < p.ColorMore.Count ? p.ColorMore[i - 1] : null;
    }

    static int UvSetCount(HafPrimitive p)
    {
        int n = 0;
        while (n < 8 && UvSet(p, n) != null) n++;
        return n;
    }

    static float[] UvSet(HafPrimitive p, int i)
    {
        if (i == 0) return p.Uv0;
        if (i == 1) return p.Uv1;
        return p.UvMore != null && i - 2 < p.UvMore.Count ? p.UvMore[i - 2] : null;
    }
}
