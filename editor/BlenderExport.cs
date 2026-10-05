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
//     NOT laid out: a deciding material that is not OPAQUE (its alpha depends on the material's node tree) - named.
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

    public sealed class Primitive
    {
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
    public static float[] CornerNormals(float[] positions, int[] faces, bool[] faceSharp, short[] customNormal)
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
            ExportedNormal(ref x, ref y, ref z);
            outN[3 * c] = x; outN[3 * c + 1] = z; outN[3 * c + 2] = -y;   // zup2yup
        }
        return outN;
    }

    /// <summary>A corner normal as the exporter keeps it, still Z up: np.round(normals, 4) in float32, then normalize_vecs
    /// (a zero norm leaves the vector alone), then a zero vector made (0, 0, 1).</summary>
    internal static void ExportedNormal(ref float x, ref float y, ref float z)
    {
        x = Round4(x); y = Round4(y); z = Round4(z);
        float norm = (float)Math.Sqrt((double)(float)((float)((float)(x * x) + (float)(y * y)) + (float)(z * z)));
        if (norm != 0f) { x = (float)(x / norm); y = (float)(y / norm); z = (float)(z / norm); }
        if (x == 0f && y == 0f && z == 0f) z = 1f;
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
            Slots = r.Slots, Ratio = r.Ratio, Collapsed = r.Collapsed, Fallback = r.Fallback,
            Faces = PerFace(r.Faces, 3), FaceMaterial = PerFace(r.FaceMaterial, 1), FaceSharp = PerFace(r.FaceSharp, 1), CustomNormal = PerFace(r.CustomNormal, 6),
        };
        foreach (var uv in r.Uv) v.Uv.Add(PerFace(uv, 6));
        foreach (var (name, point, bytes) in r.Colors) v.Colors.Add((name, point, point ? bytes : PerFace(bytes, 12)));
        return v;
    }

    /// <summary>Why this layout does not cover a reduced mesh, or null.</summary>
    public static string NotLaidOut(BlenderReduce.Result r, HafModel m)
    {
        if (r.Colors.Count == 0) return null;
        r = Validated(r);
        foreach (int slot in new SortedSet<int>(r.FaceMaterial))
        {
            var (material, vertexColor) = r.Slots[slot];
            if (!vertexColor) { if (material < 0) return null; continue; }   // an empty slot first decides (the active layer, with alpha): laid out
            if (material >= 0 && m.Materials[material].AlphaMode != "OPAQUE") return "a coloured material with alpha (its colour set depends on the material's node tree)";
            return null;
        }
        return null;
    }

    /// <summary>The colour sets the exporter writes for the mesh: (layer, with alpha), layer -1 for the forced set.</summary>
    static List<(int layer, bool alpha)> ColorPlan(BlenderReduce.Result r)
    {
        var plan = new List<(int, bool)>();
        if (r.Colors.Count == 0) return plan;
        bool noMaterials = true, decided = false;
        foreach (int slot in new SortedSet<int>(r.FaceMaterial))
        {
            var (material, vertexColor) = r.Slots[slot];
            bool hasMaterial = material >= 0 || vertexColor;   // the importer invents a material for a coloured primitive without one
            if (hasMaterial) noMaterials = false;
            if (decided) continue;
            if (vertexColor) { plan.Add((0, false)); decided = true; }          // the material's own vertex colour: RGB (an OPAQUE material)
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
    public static List<Primitive> MeshPrimitives(BlenderReduce.Result r)
    {
        r = Validated(r);
        int nc = r.Faces.Length, nf = nc / 3;
        var plan = ColorPlan(r);
        var normals = CornerNormals(r.Positions, r.Faces, r.FaceSharp, r.CustomNormal);
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
                p.Positions[3 * i] = r.Positions[3 * v]; p.Positions[3 * i + 1] = r.Positions[3 * v + 2]; p.Positions[3 * i + 2] = -r.Positions[3 * v + 1];
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
