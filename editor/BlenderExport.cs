// BlenderExport.cs - a mesh as Blender's glTF exporter writes it (step 5 of replacing Blender, milestone d, 2026-10-05):
// what prep_model.py's `export_scene.gltf(export_format='GLB')` makes of a reduced mesh, so the C# prep can hand the
// Factory's converter the same vertices in the same order. Read at Blender 5.1.2: io_scene_gltf2/blender/exp/
// primitive_extract.py (PrimitiveCreator), primitive_attributes.py, blender/com/gltf2_blender_utils.py
// (fast_structured_np_unique), with the exporter's defaults (normals and UVs on, tangents off, accessors NOT shared between
// primitives, Y up, vertex colours by MATERIAL plus every other colour layer).
//   * FIRST the exporter validates the mesh (nodes.py: `blender_object.data.validate()`; mesh_validate.cc): a face with a
//     repeated vertex goes, then a face on the same vertices as an EARLIER face (either winding) - the collapse leaves such
//     twins where two sheets close onto each other. Their corners go with them, so the fans around their vertices, and
//     with them the normals of the faces that stay, change. Edges and vertices stay. (Validate also clamps a vertex-group
//     weight to 0..1 and zeroes a non-finite one - the skinned layout's concern.)
//   * One "dot" per corner: the vertex number, then the corner's normal, then every UV layer, then the colour sets - all
//     4-byte fields.
//   * The normal is Mesh.corner_normals rounded to 4 decimals (float32: times 10000, rint, divided by 10000), renormalized
//     (float32 norm, a division per component, left alone when the norm is zero), a zero vector made (0, 0, 1), then
//     turned Y up: (x, z, -y). The UV is (u, 1 - v) as float32 (v * -1, + 1).
//   * Triangles are bucketed by material slot, ascending; each bucket's dots are made unique and SORTED - as raw
//     little-endian 32-bit words, field by field (numpy sorts the records viewed as UCS4 strings): by vertex number, then
//     by the normal's bit patterns (so negative floats come after positive ones), then the UVs', then the colours'. Every
//     -0.0 becomes 0.0 first. The sorted unique dots are the primitive's vertices; a triangle corner's index is its dot's
//     place.
//   * A vertex's position is the mesh vertex's, Y up.
//   * Colour sets (manage_material_info), the same on EVERY primitive of the mesh: the first slot in use (ascending) whose
//     material the importer built with the vertex colour makes the first layer COLOR_0 as linear RGB floats; a slot WITHOUT
//     any material, met before one, makes it COLOR_0 with alpha instead (the active layer); when the mesh has materials and
//     colour layers but none of the above, COLOR_0 is a "forced" set of 255 bytes; then every colour layer not yet written
//     follows as the next COLOR_n with alpha. A layer's value is its sRGB bytes through Blender's table (alpha: byte / 255);
//     a set with alpha is written as normalized unsigned shorts (clip, times 65535, + 0.5, truncated).
//     A deciding material built with the vertex colour writes the set WITH alpha when the importer wired the vertex
//     colour's alpha into the material (pbrMetallicRoughness.py base_color drops the alpha socket for OPAQUE - or no
//     mode at all - and for MASK at a cutoff of 0 or over 1; every other mode string and cutoff keeps it, a negative
//     cutoff and a mode the spec does not know included) - the exporter's add_alpha is "a colour attribute feeds the alpha
//     socket" and "the detected alpha mode is not OPAQUE". The importer compares the JSON DOUBLE, and so does this: the
//     model keeps the cutoff as a double (HafMaterial.AlphaCutoff). One edge stays on float32: a BLEND material whose
//     baseColorFactor alpha is within a float32 ulp of 1 (the importer adds an alpha-factor node the exporter then reads
//     as 1 = OPAQUE, and writes RGB) - named in docs/Testing.md, no file has it.
//   * A SKINNED mesh (the object carries an armature modifier): the positions go through the object's matrix_world and
//     the normals - after the rounding and normalizing above - through armature.matrix_world.to_3x3() @ (armature.
//     matrix_world.inverted_safe() @ object.matrix_world).to_3x3().inverted_safe().transposed(), both by np.matmul in
//     FLOAT32 (numpy reads a mathutils matrix through the buffer protocol as float32 - measured, not the float64 a list
//     of Python floats would give): a plain chain x m0 + y m1 + z m2, then the translation; the normals normalized again. Per vertex
//     the vertex groups over 0.0001 whose bone is a joint, sorted by weight (stable, descending), the first four kept
//     (the exporter's default of 4 influences), the weights divided by their float32 sum; a vertex without any goes to a
//     "neutral bone" the exporter adds after the armature's bones. The joints are the armature's bones depth-first.
// Proof: tools/prep-drill (Blender runs the real prep_model.py; its GLB's primitives against these, bit for bit).
using System;
using System.Collections.Generic;

public static class BlenderExport
{
    public sealed class ColorSet
    {
        public bool Alpha;        // four components, written as normalized unsigned shorts; else RGB floats
        public bool Forced;       // the exporter's placeholder: every byte 255
        public float[] Data;      // linear, 3 or 4 per vertex (null when Forced)
        public ushort[] Shorts;   // the written shorts of a set with alpha, 4 per vertex
    }

    /// <summary>What the layout needs of a skinned mesh's armature.</summary>
    public sealed class Skin
    {
        public float[] ObjectWorld, ArmatureWorld;   // matrix_world as Blender holds it: column-major float32
        public int[] GroupJoint;                     // per vertex group: its bone's place among the exported joints, or -1
        public int JointCount;                       // the exported joints (the armature's bones); the neutral bone is this index
    }

    public sealed class Primitive
    {
        public ushort[] Joints;                          // JOINTS_0, 4 per vertex, or null (not skinned)
        public float[] Weights;                          // WEIGHTS_0
        public bool NeutralBone;                         // some vertex of the MESH has no bone: the exporter adds a joint
        public int MaterialSlot;
        public int VertexCount;
        public int[] SourceVertex;                       // per exported vertex: the reduced mesh's vertex
        public float[] Positions, Normals;               // 3 per vertex, glTF's frame
        public List<float[]> Uv = new List<float[]>();   // per UV layer, 2 per vertex, glTF's convention
        public List<ColorSet> Colors = new List<ColorSet>();   // COLOR_0, COLOR_1, ...
        public int[] Indices;                            // 3 per triangle
    }

    static uint Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
    static float FromBits(uint u) => BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
    static float NoNegativeZero(float f) => f == 0f ? 0f : f;   // arr[arr == -0.0] = 0.0

    /// <summary>The exporter's normals for every corner of a mesh in Blender's frame (3 per corner, already Y up).</summary>
    public static float[] CornerNormals(float[] positions, int[] faces, bool[] faceSharp, short[] customNormal, float[] skinTransform = null)
    {
        int nc = faces.Length;
        short[] d0 = null, d1 = null;
        if (customNormal != null)
        {
            d0 = new short[nc]; d1 = new short[nc];
            for (int c = 0; c < nc; c++) { d0[c] = customNormal[2 * c]; d1[c] = customNormal[2 * c + 1]; }
        }
        var cn = VehicleProbe.BlenderCornerNormals(positions, faces, faceSharp, d0, d1);
        var outN = new float[nc * 3];
        for (int c = 0; c < nc; c++)
        {
            float x = cn[3 * c], y = cn[3 * c + 1], z = cn[3 * c + 2];
            ExportedNormal(ref x, ref y, ref z, skinTransform);
            outN[3 * c] = x; outN[3 * c + 1] = z; outN[3 * c + 2] = -y;   // zup2yup
        }
        return outN;
    }

    /// <summary>A corner normal as the exporter keeps it, still Z up: np.round(normals, 4) in float32, then normalize_vecs
    /// (a zero norm leaves the vector alone); for a skinned mesh the 3x3 (row-major items) applied as numpy's float32
    /// matmul does - a plain chain - and normalize_vecs again; then a zero vector made (0, 0, 1).</summary>
    internal static void ExportedNormal(ref float x, ref float y, ref float z, float[] skinTransform = null)
    {
        x = Round4(x); y = Round4(y); z = Round4(z);
        NormalizeVecs(ref x, ref y, ref z);
        if (skinTransform != null)
        {
            var t = skinTransform; float nx = x, ny = y, nz = z;
            x = (float)((float)((float)(nx * t[0]) + (float)(ny * t[1])) + (float)(nz * t[2]));
            y = (float)((float)((float)(nx * t[3]) + (float)(ny * t[4])) + (float)(nz * t[5]));
            z = (float)((float)((float)(nx * t[6]) + (float)(ny * t[7])) + (float)(nz * t[8]));
            NormalizeVecs(ref x, ref y, ref z);
        }
        if (x == 0f && y == 0f && z == 0f) z = 1f;
    }

    static void NormalizeVecs(ref float x, ref float y, ref float z)
    {
        float norm = (float)Math.Sqrt((double)(float)((float)((float)(x * x) + (float)(y * y)) + (float)(z * z)));
        if (norm != 0f) { x = (float)(x / norm); y = (float)(y / norm); z = (float)(z / norm); }
    }

    /// <summary>apply_mat_to_all(object.matrix_world, locs): np.matmul of the float32 positions with the float32 3x3
    /// transposed, plus the translation, each step float32. Blender's frame in and out; the matrix column-major.</summary>
    internal static float[] SkinnedPositions(float[] positions, float[] world)
    {
        var o = new float[positions.Length];
        for (int v = 0; v < positions.Length / 3; v++)
        {
            float x = positions[3 * v], y = positions[3 * v + 1], z = positions[3 * v + 2];
            for (int row = 0; row < 3; row++)
                o[3 * v + row] = (float)((float)((float)((float)(x * world[0 * 4 + row]) + (float)(y * world[1 * 4 + row])) + (float)(z * world[2 * 4 + row])) + world[3 * 4 + row]);
        }
        return o;
    }

    /// <summary>__get_bone_data and the joint attributes (primitive_attributes.py), per vertex of the mesh: 4 joints, 4
    /// weights. mesh.validate() ran first: a weight that is not finite is 0, one outside 0..1 is clamped.</summary>
    internal static void VertexBones(BlenderReduce.Result r, Skin skin, out ushort[] joints, out float[] weights, out bool neutral)
    {
        int nv = r.Positions.Length / 3;
        joints = new ushort[4 * nv]; weights = new float[4 * nv]; neutral = false;
        var bones = new List<(int joint, float weight)>();
        for (int v = 0; v < nv; v++)
        {
            bones.Clear();
            if (r.DefNr != null && r.DefNr[v] != null)
                for (int i = 0; i < r.DefNr[v].Count; i++)
                {
                    int g = r.DefNr[v][i]; float w = r.DefWeight[v][i];
                    if (float.IsNaN(w) || float.IsInfinity(w)) w = 0f; else if (w < 0f) w = 0f; else if (w > 1f) w = 1f;
                    if ((double)w <= 0.0001) continue;
                    if (g < 0 || g >= skin.GroupJoint.Length || skin.GroupJoint[g] < 0) continue;
                    bones.Add((skin.GroupJoint[g], w));
                }
            // bones.sort(key=weight, reverse=True): stable, so equal weights keep the vertex's group order
            for (int i = 1; i < bones.Count; i++)
            {
                var b = bones[i]; int k = i - 1;
                while (k >= 0 && bones[k].weight < b.weight) { bones[k + 1] = bones[k]; k--; }
                bones[k + 1] = b;
            }
            if (bones.Count == 0) { bones.Add((skin.JointCount, 1f)); neutral = true; }
            float sum = 0f;
            for (int j = 0; j < 4 && j < bones.Count; j++) { joints[4 * v + j] = (ushort)bones[j].joint; weights[4 * v + j] = bones[j].weight; }
            for (int j = 0; j < 4; j++) sum = (float)(sum + weights[4 * v + j]);
            for (int j = 0; j < 4; j++) weights[4 * v + j] = (float)(weights[4 * v + j] / sum);
        }
    }

    /// <summary>numpy's around(x, 4) on a float32: x * 10000, rint (ties to even), / 10000, each in float32.</summary>
    internal static float Round4(float x) => (float)((float)Math.Round((double)(float)(x * 10000f), MidpointRounding.ToEven) / 10000f);

    /// <summary>The mesh after the exporter's `mesh.validate()`: without the faces that repeat a vertex and without the
    /// faces on the same three vertices as an earlier kept face. The same object when nothing goes.</summary>
    public static BlenderReduce.Result Validated(BlenderReduce.Result r)
    {
        int nf = r.Faces.Length / 3, kept = 0;
        var keep = new bool[nf];
        var seen = new HashSet<(int, int, int)>();
        for (int f = 0; f < nf; f++)
        {
            int a = r.Faces[3 * f], b = r.Faces[3 * f + 1], c = r.Faces[3 * f + 2];
            if (a == b || b == c || a == c) continue;
            int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c));
            if (!seen.Add((lo, a + b + c - lo - hi, hi))) continue;
            keep[f] = true; kept++;
        }
        if (kept == nf) return r;
        T[] PerFace<T>(T[] src, int per)
        {
            if (src == null) return null;
            var dst = new T[kept * per]; int o = 0;
            for (int f = 0; f < nf; f++) if (keep[f]) { Array.Copy(src, f * per, dst, o, per); o += per; }
            return dst;
        }
        var v = new BlenderReduce.Result
        {
            VertexCount = r.VertexCount, FaceCount = kept, Positions = r.Positions, Edges = r.Edges, DefNr = r.DefNr, DefWeight = r.DefWeight,
            Slots = r.Slots, SlotAlpha = r.SlotAlpha, Ratio = r.Ratio, Collapsed = r.Collapsed, Fallback = r.Fallback,
            Faces = PerFace(r.Faces, 3), FaceMaterial = PerFace(r.FaceMaterial, 1), FaceSharp = PerFace(r.FaceSharp, 1), CustomNormal = PerFace(r.CustomNormal, 6),
        };
        foreach (var uv in r.Uv) v.Uv.Add(PerFace(uv, 6));
        foreach (var (name, point, bytes) in r.Colors) v.Colors.Add((name, point, point ? bytes : PerFace(bytes, 12)));
        return v;
    }

    /// <summary>Whether the importer wires a coloured material's vertex alpha into it (base_color): not for OPAQUE or no
    /// mode, not for MASK at a cutoff of 0 or over 1; for every other mode and cutoff (BLEND, MASK in (0, 1] and below 0,
    /// a mode string the spec does not know).</summary>
    internal static bool VertexAlphaWired(string alphaMode, double alphaCutoff) => !string.IsNullOrEmpty(alphaMode) && alphaMode != "OPAQUE" && !(alphaMode == "MASK" && (alphaCutoff == 0.0 || alphaCutoff > 1.0));

    /// <summary>The colour sets the exporter writes for the mesh: (layer, with alpha), layer -1 for the forced set.</summary>
    static List<(int layer, bool alpha)> ColorPlan(BlenderReduce.Result r)
    {
        var plan = new List<(int, bool)>();
        if (r.Colors.Count == 0) return plan;
        if (r.SlotAlpha.Count != r.Slots.Count) throw new InvalidOperationException($"a reduced mesh with {r.Slots.Count} slots carries {r.SlotAlpha.Count} alpha modes (a Result rebuilt without them)");
        bool noMaterials = true, decided = false;
        foreach (int slot in new SortedSet<int>(r.FaceMaterial))
        {
            var (material, vertexColor) = r.Slots[slot];
            bool hasMaterial = material >= 0 || vertexColor;   // the importer invents a material for a coloured primitive without one
            if (hasMaterial) noMaterials = false;
            if (decided) continue;
            if (vertexColor) { plan.Add((0, material >= 0 && VertexAlphaWired(r.SlotAlpha[slot].mode, r.SlotAlpha[slot].cutoff))); decided = true; }   // the material's own vertex colour: RGB, or RGBA when its alpha is wired
            else if (!hasMaterial) { plan.Add((0, true)); decided = true; }     // no material: the active (render) layer, with alpha
        }
        if (!noMaterials && plan.Count == 0) plan.Add((-1, true));              // a forced COLOR_0 of 255s
        for (int layer = 0; layer < r.Colors.Count; layer++)
        {
            bool written = false;
            foreach (var (l, _) in plan) if (l == layer) written = true;
            if (!written) plan.Add((layer, true));
        }
        return plan;
    }

    /// <summary>The triangle primitives of a reduced, unskinned mesh, one per material slot in use, ascending.</summary>
    public static List<Primitive> MeshPrimitives(BlenderReduce.Result r, Skin skin = null)
    {
        r = Validated(r);
        int nc = r.Faces.Length, nf = nc / 3;
        var plan = ColorPlan(r);
        float[] nt = skin != null ? VehicleProbe.ExporterNormalTransform(skin.ArmatureWorld, skin.ObjectWorld) : null;
        var normals = CornerNormals(r.Positions, r.Faces, r.FaceSharp, r.CustomNormal, nt);
        float[] locs = r.Positions; ushort[] vertJoints = null; float[] vertWeights = null; bool neutral = false;
        if (skin != null) { locs = SkinnedPositions(r.Positions, skin.ObjectWorld); VertexBones(r, skin, out vertJoints, out vertWeights, out neutral); }
        int colorAt = 1 + 3 + 2 * r.Uv.Count;
        var colorOffset = new int[plan.Count]; int fields = colorAt;
        for (int k = 0; k < plan.Count; k++) { colorOffset[k] = fields; fields += plan[k].layer < 0 ? 1 : plan[k].alpha ? 4 : 3; }   // the forced set: four bytes, one word
        // the dots: per corner its fields as 32-bit words, -0.0 already 0.0
        var dots = new uint[nc * fields];
        for (int c = 0; c < nc; c++)
        {
            int o = c * fields;
            dots[o] = (uint)r.Faces[c];
            dots[o + 1] = Bits(NoNegativeZero(normals[3 * c])); dots[o + 2] = Bits(NoNegativeZero(normals[3 * c + 1])); dots[o + 3] = Bits(NoNegativeZero(normals[3 * c + 2]));
            for (int u = 0; u < r.Uv.Count; u++)
            {
                dots[o + 4 + 2 * u] = Bits(NoNegativeZero(r.Uv[u][2 * c]));
                dots[o + 5 + 2 * u] = Bits(NoNegativeZero((float)(1.0f + (float)(-r.Uv[u][2 * c + 1]))));
            }
            for (int k = 0; k < plan.Count; k++)
            {
                if (plan[k].layer < 0) { dots[o + colorOffset[k]] = 0xFFFFFFFFu; continue; }
                var layer = r.Colors[plan[k].layer]; int at = 4 * (layer.point ? r.Faces[c] : c);
                for (int i = 0; i < 3; i++) dots[o + colorOffset[k] + i] = Bits(BlenderColor.SrgbByteToLinear(layer.bytes[at + i]));
                if (plan[k].alpha) dots[o + colorOffset[k] + 3] = Bits((float)(layer.bytes[at + 3] * (float)(1.0f / 255.0f)));
            }
        }
        var slots = new SortedSet<int>(r.FaceMaterial);
        var result = new List<Primitive>();
        foreach (int slot in slots)
        {
            var corners = new List<int>();
            for (int f = 0; f < nf; f++) if (r.FaceMaterial[f] == slot) { corners.Add(3 * f); corners.Add(3 * f + 1); corners.Add(3 * f + 2); }
            // np.unique(..., return_inverse=True) over the records as words: sort, drop repeats, each corner's place
            var order = corners.ToArray();
            Array.Sort(order, (a, b) =>
            {
                int oa = a * fields, ob = b * fields;
                for (int k = 0; k < fields; k++) { uint x = dots[oa + k], y = dots[ob + k]; if (x != y) return x < y ? -1 : 1; }
                return 0;
            });
            var place = new Dictionary<int, int>(order.Length);
            var unique = new List<int>();
            for (int i = 0; i < order.Length; i++)
            {
                bool same = i > 0;
                if (same) { int oa = order[i] * fields, ob = order[i - 1] * fields; for (int k = 0; k < fields && same; k++) same = dots[oa + k] == dots[ob + k]; }
                if (!same) unique.Add(order[i]);
                place[order[i]] = unique.Count - 1;
            }
            int n = unique.Count;
            var p = new Primitive { MaterialSlot = slot, VertexCount = n, SourceVertex = new int[n], Positions = new float[3 * n], Normals = new float[3 * n], Indices = new int[corners.Count] };
            for (int u = 0; u < r.Uv.Count; u++) p.Uv.Add(new float[2 * n]);
            if (skin != null) { p.Joints = new ushort[4 * n]; p.Weights = new float[4 * n]; p.NeutralBone = neutral; }
            foreach (var (layer, alpha) in plan)
            {
                var set = new ColorSet { Alpha = alpha, Forced = layer < 0 };
                if (layer >= 0) set.Data = new float[(alpha ? 4 : 3) * n];
                if (alpha && layer >= 0) set.Shorts = new ushort[4 * n];
                p.Colors.Add(set);
            }
            for (int i = 0; i < n; i++)
            {
                int c = unique[i], o = c * fields, v = r.Faces[c];
                p.SourceVertex[i] = v;
                // locs[vertex], zup2yup
                p.Positions[3 * i] = locs[3 * v]; p.Positions[3 * i + 1] = locs[3 * v + 2]; p.Positions[3 * i + 2] = -locs[3 * v + 1];
                if (skin != null) for (int j = 0; j < 4; j++) { p.Joints[4 * i + j] = vertJoints[4 * v + j]; p.Weights[4 * i + j] = vertWeights[4 * v + j]; }
                p.Normals[3 * i] = FromBits(dots[o + 1]); p.Normals[3 * i + 1] = FromBits(dots[o + 2]); p.Normals[3 * i + 2] = FromBits(dots[o + 3]);
                for (int u = 0; u < r.Uv.Count; u++) { p.Uv[u][2 * i] = FromBits(dots[o + 4 + 2 * u]); p.Uv[u][2 * i + 1] = FromBits(dots[o + 5 + 2 * u]); }
                for (int k = 0; k < plan.Count; k++)
                {
                    var set = p.Colors[k];
                    if (set.Forced) continue;
                    int per = set.Alpha ? 4 : 3;
                    for (int j = 0; j < per; j++)
                    {
                        float x = FromBits(dots[o + colorOffset[k] + j]);
                        set.Data[per * i + j] = x;
                        if (set.Alpha)
                        {
                            // np.clip(data, 0, 1); data *= 65535; data += 0.5; astype(uint16)
                            float q = x < 0f ? 0f : x > 1f ? 1f : x;
                            set.Shorts[4 * i + j] = (ushort)(float)((float)(q * 65535f) + 0.5f);
                        }
                    }
                }
            }
            for (int i = 0; i < corners.Count; i++) p.Indices[i] = place[corners[i]];
            result.Add(p);
        }
        return result;
    }
}
