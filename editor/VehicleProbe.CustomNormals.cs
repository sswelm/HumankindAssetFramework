// VehicleProbe.CustomNormals.cs - a vertex's normal as Blender's `vertex.normal` reads it for an imported glTF mesh WITH
// normals (step 3 of replacing Blender; the visibility verdict's normal ray). The importer sets the file's normals as
// custom normals FROM VERTICES (normals_split_custom_set_from_vertices), and Blender stores a custom normal as TWO SHORTS
// against the corner's smooth-fan space (mesh_normals.cc): the fan normal from the face normals weighted by the corner
// angle (safe_acos_approx), a reference edge, the angle to the other edge; the encode quantizes alpha and beta to
// 1/32767 (corner_space_custom_normal_to_data), the decode rebuilds the vector with cosf/sinf
// (corner_space_custom_data_to_normal), and mix_normals_corner_to_vert weights the decoded corner normals by the corner
// angle and normalizes. 4.8e-5 rad of quantization: a file normal of exactly (0, 0, 1) comes back as (0, -1, -4.8e-5) in
// Blender's frame, and the re-fused Dragon's grazing normal rays were decided by that (4 of its 2,507 parts).
// Before any of it, the importer's set_poly_smoothing marks a face SHARP when every corner's file normal equals the face
// normal (dot above 0.9999999 as float32: a flat-shaded face), and a sharp face breaks the fans at its edges.
// All float32, the C++ math:: flavour: normalize = v / sqrt(len2), dot summed left to right, read from Blender 5.1's
// source. Mesh::face_normals uses Newell's sum even for triangles; MeshPolygon.normal's triangle cross product is a
// different accessor. The distinction changes the fan spaces and the Dragon spin's grazing normal-ray verdicts.
// cosf/sinf are the C runtime's, here the double ones rounded; normal differences are still counted by the drill.
using System;
using System.Collections.Generic;

public static partial class VehicleProbe
{
    const float PI2F = (float)(Math.PI * 2.0);
    const float LNOR_TRIGO = 1.0f - 1e-4f;

    /// <summary>math::dot: summed left to right in float32.</summary>
    static float Dot3(float ax, float ay, float az, float bx, float by, float bz) => (float)((float)((float)(ax * bx) + (float)(ay * by)) + (float)(az * bz));

    /// <summary>math::normalize: v / sqrt(length_squared), zero below 1e-35 (a division per component, not 1 / len).</summary>
    static void Normalize3(ref float x, ref float y, ref float z)
    {
        float len2 = Dot3(x, y, z, x, y, z);
        if (len2 > 1.0e-35f) { float len = Sqrtf(len2); x = (float)(x / len); y = (float)(y / len); z = (float)(z / len); }
        else { x = 0f; y = 0f; z = 0f; }
    }

    /// <summary>safe_acos_approx (BLI_math_base.hh): the polynomial Blender uses for corner angles, max error 4.5e-5.</summary>
    static float SafeAcosApprox(float x)
    {
        float f = Math.Abs(x);
        float m = f < 1.0f ? (float)(1.0f - (float)(1.0f - f)) : 1.0f;
        float t = (float)(m * -0.02164095f);
        t = (float)(0.077980478f + t);
        t = (float)(m * t);
        t = (float)(-0.213300989f + t);
        t = (float)(m * t);
        t = (float)(1.5707963267f + t);
        float a = (float)(Sqrtf((float)(1.0f - m)) * t);
        return x < 0.0f ? (float)((float)Math.PI - a) : a;
    }

    static short UnitFloatToShort(float v) => (short)Math.Floor((double)(float)((float)(v * 32767f) + 0.5f));

    struct FanSpace { public float lx, ly, lz, rx, ry, rz, ox, oy, oz, refAlpha, refBeta; public float fnx, fny, fnz; }   // fn: the fan's own normal, also when the space is invalid (l is zero then)

    /// <summary>corner_fan_space_define: the space a fan's custom normals are encoded against.</summary>
    static FanSpace DefineSpace(float lx, float ly, float lz, float vrx, float vry, float vrz, float vox, float voy, float voz, List<float[]> edgeVectors)
    {
        var s = new FanSpace();
        float dtpRef = Dot3(vrx, vry, vrz, lx, ly, lz), dtpOther = Dot3(vox, voy, voz, lx, ly, lz);
        if (Math.Abs(dtpRef) >= LNOR_TRIGO || Math.Abs(dtpOther) >= LNOR_TRIGO) { s.refAlpha = s.refBeta = 0f; return s; }
        s.lx = lx; s.ly = ly; s.lz = lz;
        if (edgeVectors.Count > 0)
        {
            float alpha = 0f;
            foreach (var e in edgeVectors) alpha = (float)(alpha + SafeAcosApprox(Dot3(e[0], e[1], e[2], lx, ly, lz)));
            s.refAlpha = (float)(alpha / (float)edgeVectors.Count);
        }
        else s.refAlpha = (float)((float)(SafeAcosApprox(Dot3(vrx, vry, vrz, lx, ly, lz)) + SafeAcosApprox(Dot3(vox, voy, voz, lx, ly, lz))) / 2.0f);
        float rx = (float)(vrx - (float)(lx * dtpRef)), ry = (float)(vry - (float)(ly * dtpRef)), rz = (float)(vrz - (float)(lz * dtpRef));
        Normalize3(ref rx, ref ry, ref rz);
        s.rx = rx; s.ry = ry; s.rz = rz;
        float ox = (float)((float)(ly * rz) - (float)(lz * ry)), oy = (float)((float)(lz * rx) - (float)(lx * rz)), oz = (float)((float)(lx * ry) - (float)(ly * rx));
        Normalize3(ref ox, ref oy, ref oz);
        s.ox = ox; s.oy = oy; s.oz = oz;
        float px = (float)(vox - (float)(lx * dtpOther)), py = (float)(voy - (float)(ly * dtpOther)), pz = (float)(voz - (float)(lz * dtpOther));
        Normalize3(ref px, ref py, ref pz);
        float dtp = Dot3(rx, ry, rz, px, py, pz);
        if (dtp < LNOR_TRIGO)
        {
            float beta = SafeAcosApprox(dtp);
            s.refBeta = Dot3(ox, oy, oz, px, py, pz) < 0.0f ? (float)(PI2F - beta) : beta;
        }
        else s.refBeta = PI2F;
        return s;
    }

    /// <summary>corner_space_custom_normal_to_data: the custom normal as two shorts against the space.</summary>
    static void EncodeCustom(FanSpace s, float cx, float cy, float cz, out short d0, out short d1)
    {
        d0 = 0; d1 = 0;
        // is_zero_v3 || compare_v3v3(vec_lnor, custom, 1e-4f): compare_ff is fabsf(a - b) <= max_diff, the difference rounded
        // to float32 (a strict < kept two Cobra corners at the boundary from Blender's (0, 0) - the Decimate drill, step 5 c)
        if ((cx == 0f && cy == 0f && cz == 0f) || (Math.Abs((float)(s.lx - cx)) <= 1e-4f && Math.Abs((float)(s.ly - cy)) <= 1e-4f && Math.Abs((float)(s.lz - cz)) <= 1e-4f)) return;
        float cosAlpha = Dot3(s.lx, s.ly, s.lz, cx, cy, cz);
        float alpha = SafeAcosApprox(cosAlpha);
        if (alpha > s.refAlpha) d0 = UnitFloatToShort((float)(-(float)(PI2F - alpha) / (float)(PI2F - s.refAlpha)));
        else d0 = UnitFloatToShort((float)(alpha / s.refAlpha));
        float nc = -cosAlpha;
        float vx = (float)((float)(s.lx * nc) + cx), vy = (float)((float)(s.ly * nc) + cy), vz = (float)((float)(s.lz * nc) + cz);
        Normalize3(ref vx, ref vy, ref vz);
        float cosBeta = Dot3(s.rx, s.ry, s.rz, vx, vy, vz);
        if (cosBeta < LNOR_TRIGO)
        {
            float beta = SafeAcosApprox(cosBeta);
            if (Dot3(s.ox, s.oy, s.oz, vx, vy, vz) < 0.0f) beta = (float)(PI2F - beta);
            if (beta > s.refBeta) d1 = UnitFloatToShort((float)(-(float)(PI2F - beta) / (float)(PI2F - s.refBeta)));
            else d1 = UnitFloatToShort((float)(beta / s.refBeta));
        }
        else d1 = 0;
    }

    /// <summary>corner_space_custom_data_to_normal: the two shorts back to a vector.</summary>
    static void DecodeCustom(FanSpace s, short d0, short d1, out float x, out float y, out float z)
    {
        if (d0 == 0 || s.refAlpha == 0.0f || s.refBeta == 0.0f) { x = s.lx; y = s.ly; z = s.lz; return; }
        float alphafac = (float)((float)d0 / 32767f);
        float alpha = (float)((alphafac > 0.0f ? s.refAlpha : (float)(PI2F - s.refAlpha)) * alphafac);
        float betafac = (float)((float)d1 / 32767f);
        float ca = BlenderTrig.Cosf(alpha);   // cosf and sinf as the C runtime computes them: not the rounded double (BlenderTrig.cs)
        x = (float)(s.lx * ca); y = (float)(s.ly * ca); z = (float)(s.lz * ca);
        if (betafac == 0.0f)
        {
            float sa = BlenderTrig.Sinf(alpha);
            x = (float)(x + (float)(s.rx * sa)); y = (float)(y + (float)(s.ry * sa)); z = (float)(z + (float)(s.rz * sa));
        }
        else
        {
            float sinalpha = BlenderTrig.Sinf(alpha);
            float beta = (float)((betafac > 0.0f ? s.refBeta : (float)(PI2F - s.refBeta)) * betafac);
            float f1 = (float)(sinalpha * BlenderTrig.Cosf(beta)), f2 = (float)(sinalpha * BlenderTrig.Sinf(beta));
            x = (float)(x + (float)(s.rx * f1)); y = (float)(y + (float)(s.ry * f1)); z = (float)(z + (float)(s.rz * f1));
            x = (float)(x + (float)(s.ox * f2)); y = (float)(y + (float)(s.oy * f2)); z = (float)(z + (float)(s.oz * f2));
        }
    }

    sealed class VertCornerInfo { public int face, corner, cornerPrev, cornerNext, vertPrev, vertNext, edgePrev, edgeNext; }

    /// <summary>Mesh::face_normals() uses normal_calc_ngon even for triangles: Newell's sum, then normalize_v3
    /// (multiply by the reciprocal length). MeshPolygon.normal uses a triangle cross product instead; using that here
    /// changes fan spaces on thin triangles far from the origin. A zero normal is (0, 0, 1).</summary>
    internal static float[] FaceNormals(float[] P, int[] tris)
    {
        int nf = tris.Length / 3;
        var FN = new float[nf * 3];
        for (int f = 0; f < nf; f++)
        {
            float nx = 0f, ny = 0f, nz = 0f;
            int prev = tris[f * 3 + 2] * 3;
            for (int k = 0; k < 3; k++)
            {
                int curr = tris[f * 3 + k] * 3;
                nx = (float)(nx + (float)((float)(P[prev + 1] - P[curr + 1]) * (float)(P[prev + 2] + P[curr + 2])));
                ny = (float)(ny + (float)((float)(P[prev + 2] - P[curr + 2]) * (float)(P[prev] + P[curr])));
                nz = (float)(nz + (float)((float)(P[prev] - P[curr]) * (float)(P[prev + 1] + P[curr + 1])));
                prev = curr;
            }
            NormalizeV3(ref nx, ref ny, ref nz);
            if (nx == 0f && ny == 0f && nz == 0f) nz = 1.0f;
            FN[f * 3] = nx; FN[f * 3 + 1] = ny; FN[f * 3 + 2] = nz;
        }
        return FN;
    }

    /// <summary>normalize_v3 (BLI_math_vector_inline.cc), the C++ one: the squared length summed in float32, sqrtf, and a
    /// multiply by the float32 reciprocal; zero below 1e-35. NOT NormalizeVn (mathutils: the length in double) - the face
    /// normals used that until the Decimate drill (step 5 c) caught 1-2 ulp differences that turned a flat face smooth
    /// (the Zumwalt hull's face 3: the importer's dot of 0.9999999 is decided on those bits).</summary>
    internal static void NormalizeV3(ref float x, ref float y, ref float z)
    {
        float d = Dot3(x, y, z, x, y, z);
        if (d > 1.0e-35f) { d = Sqrtf(d); float s = (float)(1.0f / d); x = (float)(x * s); y = (float)(y * s); z = (float)(z * s); }
        else { x = 0f; y = 0f; z = 0f; }
    }

    /// <summary>Every vertex's `vertex.normal`, in Blender's frame. P: positions (3 per vertex, Blender's frame), tris: the faces
    /// after validate (3 corners each, corner c = 3 f + k), N: the file normals per vertex (3 per vertex; NaN for a vertex of a
    /// primitive without normals - its faces are flat-shaded, its custom normal zero), or null for a mesh WITHOUT file normals:
    /// then Blender's normals_calc_verts - the face normals weighted by the corner angle (safe_acos_approx), normalized; a
    /// vertex of no face points along its position.</summary>
    internal static float[] BlenderVertexNormals(float[] P, int[] tris, float[] N)
    {
        int nv = P.Length / 3, nf = tris.Length / 3;
        if (N == null)
        {
            var FN0 = FaceNormals(P, tris);
            var vf = new List<int>[nv];
            for (int f = 0; f < nf; f++) for (int k = 0; k < 3; k++) { int v = tris[f * 3 + k]; (vf[v] ?? (vf[v] = new List<int>())).Add(f); }
            var res = new float[nv * 3];
            for (int v = 0; v < nv; v++)
            {
                float sx = 0f, sy = 0f, sz = 0f;
                if (vf[v] == null) { sx = P[v * 3]; sy = P[v * 3 + 1]; sz = P[v * 3 + 2]; }
                else foreach (int f in vf[v])
                {
                    int k = tris[f * 3] == v ? 0 : tris[f * 3 + 1] == v ? 1 : 2;
                    int vp = tris[f * 3 + (k + 2) % 3], vn = tris[f * 3 + (k + 1) % 3];
                    float pdx = (float)(P[vp * 3] - P[v * 3]), pdy = (float)(P[vp * 3 + 1] - P[v * 3 + 1]), pdz = (float)(P[vp * 3 + 2] - P[v * 3 + 2]);
                    float ndx = (float)(P[vn * 3] - P[v * 3]), ndy = (float)(P[vn * 3 + 1] - P[v * 3 + 1]), ndz = (float)(P[vn * 3 + 2] - P[v * 3 + 2]);
                    Normalize3(ref pdx, ref pdy, ref pdz); Normalize3(ref ndx, ref ndy, ref ndz);
                    float factor = SafeAcosApprox(Dot3(pdx, pdy, pdz, ndx, ndy, ndz));
                    sx = (float)(sx + (float)(FN0[f * 3] * factor)); sy = (float)(sy + (float)(FN0[f * 3 + 1] * factor)); sz = (float)(sz + (float)(FN0[f * 3 + 2] * factor));
                }
                Normalize3(ref sx, ref sy, ref sz);
                res[v * 3] = sx; res[v * 3 + 1] = sy; res[v * 3 + 2] = sz;
            }
            return res;
        }
        var sharpFace = SharpFaces(P, tris, N);
        var (d0, d1) = EncodeCustomShorts(P, tris, N, sharpFace);
        return DecodeCustomShorts(P, tris, sharpFace, d0, d1);
    }

    /// <summary>set_poly_smoothing: a face is SHARP when no corner's file normal differs from the face normal (dot above 0.9999999
    /// as float32), or when a corner has no file normal. Decided at import, against the geometry as imported; a mesh the Lab
    /// transforms afterwards (its second model) keeps these flags.</summary>
    internal static bool[] SharpFaces(float[] P, int[] tris, float[] N)
    {
        int nf = tris.Length / 3;
        var FN = FaceNormals(P, tris);
        var sharpFace = new bool[nf];
        const float flatDot = 0.9999999f;
        for (int f = 0; f < nf; f++)
        {
            bool smooth = false, hasNormals = true;
            for (int k = 0; k < 3; k++)
            {
                int v = tris[f * 3 + k];
                if (float.IsNaN(N[v * 3])) { hasNormals = false; break; }
                if (Dot3(N[v * 3], N[v * 3 + 1], N[v * 3 + 2], FN[f * 3], FN[f * 3 + 1], FN[f * 3 + 2]) <= flatDot) smooth = true;
            }
            sharpFace[f] = !hasNormals || !smooth;
        }
        return sharpFace;
    }

    /// <summary>normals_split_custom_set_from_vertices as the importer calls it: the corner-fan spaces of the geometry as
    /// imported, and each vertex's file normal encoded against its fan's space as two shorts per corner (a fan of two or more
    /// corners takes the float32 average of the same vector that many times - mesh_normals_corner_custom_set). What Blender
    /// STORES; `vertex.normal` is read back from it against whatever the geometry is by then (DecodeCustomShorts).
    /// The Python API clamps each component to [-1, 1] first (RNA_def_float_array's hard range). Before encoding,
    /// mesh_set_custom_normals_from_verts normalizes every vector (math::normalize) and
    /// mesh_normals_corner_custom_set replaces a zero one - the importer's zeros for a primitive without normals (NaN here)
    /// - with the mesh's own vertex normal (vert_normals_true); without both, a unit-ish file normal encodes one short off
    /// (found by the Decimate drill, step 5 c: 35 of 642 corners on the LCAC).</summary>
    internal static (short[] d0, short[] d1) EncodeCustomShorts(float[] P, int[] tris, float[] N, bool[] sharpFace)
    {
        var d0 = new short[tris.Length]; var d1 = new short[tris.Length];
        int nv = P.Length / 3;
        var C = new float[nv * 3]; float[] trueNormals = null;
        for (int v = 0; v < nv; v++)
        {
            bool none = float.IsNaN(N[v * 3]);
            float x = none ? 0f : N[v * 3], y = none ? 0f : N[v * 3 + 1], z = none ? 0f : N[v * 3 + 2];
            // normals_split_custom_set_from_vertices receives the file normals through RNA's [-1, 1] float array,
            // after set_poly_smoothing used their original values. Clamping changes even (2, .5, -.25)'s direction.
            x = Math.Max(-1f, Math.Min(1f, x)); y = Math.Max(-1f, Math.Min(1f, y)); z = Math.Max(-1f, Math.Min(1f, z));
            Normalize3(ref x, ref y, ref z);
            if (x == 0f && y == 0f && z == 0f)
            {
                trueNormals = trueNormals ?? BlenderVertexNormals(P, tris, null);
                x = trueNormals[v * 3]; y = trueNormals[v * 3 + 1]; z = trueNormals[v * 3 + 2];
            }
            C[v * 3] = x; C[v * 3 + 1] = y; C[v * 3 + 2] = z;
        }
        N = C;
        WalkFans(P, tris, sharpFace, (v, infos, fan, space) =>
        {
            float cx = N[v * 3], cy = N[v * 3 + 1], cz = N[v * 3 + 2];
            if (fan.Count >= 2)
            {
                float ax = 0f, ay = 0f, az = 0f;
                for (int i = 0; i < fan.Count; i++) { ax = (float)(ax + cx); ay = (float)(ay + cy); az = (float)(az + cz); }
                float inv = (float)(1.0f / (float)fan.Count);
                cx = (float)(ax * inv); cy = (float)(ay * inv); cz = (float)(az * inv);
            }
            short e0, e1; EncodeCustom(space, cx, cy, cz, out e0, out e1);
            foreach (int lc in fan) { int c = infos[lc].corner; d0[c] = e0; d1[c] = e1; }
        }, null, null);
        return (d0, d1);
    }

    /// <summary>`vertex.normal` from the stored two shorts per corner: normals_calc_corners over the geometry as it is NOW - the
    /// fans walked afresh, each fan's stored shorts averaged as int2 (integer division) and decoded against its space -, then
    /// mix_normals_corner_to_vert. The same geometry gives the file normal back to the quantization; a transformed one (the Lab's
    /// second model: Mesh.transform moves the vertices and leaves the shorts, flip_normals reverses the corners and carries
    /// each corner's shorts with it) gives what Blender then holds.</summary>
    internal static float[] DecodeCustomShorts(float[] P, int[] tris, bool[] sharpFace, short[] d0, short[] d1)
    {
        int nv = P.Length / 3, nf = tris.Length / 3;
        var cornerNormal = new float[nf * 3 * 3];
        var result = new float[nv * 3];
        WalkFans(P, tris, sharpFace, (v, infos, fan, space) =>
        {
            int s0 = 0, s1 = 0;
            foreach (int lc in fan) { int c = infos[lc].corner; s0 += d0[c]; s1 += d1[c]; }
            short a0 = (short)(s0 / fan.Count), a1 = (short)(s1 / fan.Count);   // int2 /= size: C++ integer division, towards zero
            float dx, dy, dz; DecodeCustom(space, a0, a1, out dx, out dy, out dz);
            foreach (int lc in fan) { int c = infos[lc].corner; cornerNormal[c * 3] = dx; cornerNormal[c * 3 + 1] = dy; cornerNormal[c * 3 + 2] = dz; }
        }, (v, infos) =>
        {
            // mix_normals_corner_to_vert: the decoded corner normals weighted by the corner angle, normalized
            float sx = 0f, sy = 0f, sz = 0f;
            foreach (var ci in infos)
            {
                float pdx = (float)(P[ci.vertPrev * 3] - P[v * 3]), pdy = (float)(P[ci.vertPrev * 3 + 1] - P[v * 3 + 1]), pdz = (float)(P[ci.vertPrev * 3 + 2] - P[v * 3 + 2]);
                float ndx = (float)(P[ci.vertNext * 3] - P[v * 3]), ndy = (float)(P[ci.vertNext * 3 + 1] - P[v * 3 + 1]), ndz = (float)(P[ci.vertNext * 3 + 2] - P[v * 3 + 2]);
                Normalize3(ref pdx, ref pdy, ref pdz); Normalize3(ref ndx, ref ndy, ref ndz);
                float factor = SafeAcosApprox(Dot3(pdx, pdy, pdz, ndx, ndy, ndz));
                int c = ci.corner;
                sx = (float)(sx + (float)(cornerNormal[c * 3] * factor)); sy = (float)(sy + (float)(cornerNormal[c * 3 + 1] * factor)); sz = (float)(sz + (float)(cornerNormal[c * 3 + 2] * factor));
            }
            Normalize3(ref sx, ref sy, ref sz);
            result[v * 3] = sx; result[v * 3 + 1] = sy; result[v * 3 + 2] = sz;
        }, v =>
        {
            float px = P[v * 3], py = P[v * 3 + 1], pz = P[v * 3 + 2]; Normalize3(ref px, ref py, ref pz);
            result[v * 3] = px; result[v * 3 + 1] = py; result[v * 3 + 2] = pz;
        });
        return result;
    }

    /// <summary>Mesh::corner_normals(): per corner (3 per face, 3 floats each), by the mesh's normal domain. With custom
    /// normals (the two shorts per corner) always the fan walk: each fan's shorts averaged and decoded against its space - an
    /// invalid space decodes to a ZERO vector, which the glTF exporter later replaces. Without them: every face sharp gives
    /// the face normals, none sharp the vertex normals, a mix the fan walk with each fan's own normal.</summary>
    internal static float[] BlenderCornerNormals(float[] P, int[] tris, bool[] sharpFace, short[] d0, short[] d1)
    {
        int nf = tris.Length / 3;
        var cn = new float[nf * 9];
        if (nf == 0) return cn;
        if (d0 == null)
        {
            bool all = true, any = false;
            for (int f = 0; f < nf; f++) { if (sharpFace[f]) any = true; else all = false; }
            if (all)
            {
                var FN = FaceNormals(P, tris);
                for (int c = 0; c < nf * 3; c++) { cn[c * 3] = FN[(c / 3) * 3]; cn[c * 3 + 1] = FN[(c / 3) * 3 + 1]; cn[c * 3 + 2] = FN[(c / 3) * 3 + 2]; }
                return cn;
            }
            if (!any)
            {
                var VN = BlenderVertexNormals(P, tris, null);
                for (int c = 0; c < nf * 3; c++) { int v = tris[c]; cn[c * 3] = VN[v * 3]; cn[c * 3 + 1] = VN[v * 3 + 1]; cn[c * 3 + 2] = VN[v * 3 + 2]; }
                return cn;
            }
            WalkFans(P, tris, sharpFace, (v, infos, fan, space) =>
            {
                foreach (int lc in fan) { int c = infos[lc].corner; cn[c * 3] = space.fnx; cn[c * 3 + 1] = space.fny; cn[c * 3 + 2] = space.fnz; }
            }, null, null);
            return cn;
        }
        WalkFans(P, tris, sharpFace, (v, infos, fan, space) =>
        {
            int s0 = 0, s1 = 0;
            foreach (int lc in fan) { int c = infos[lc].corner; s0 += d0[c]; s1 += d1[c]; }
            short a0 = (short)(s0 / fan.Count), a1 = (short)(s1 / fan.Count);
            float dx, dy, dz; DecodeCustom(space, a0, a1, out dx, out dy, out dz);
            foreach (int lc in fan) { int c = infos[lc].corner; cn[c * 3] = dx; cn[c * 3 + 1] = dy; cn[c * 3 + 2] = dz; }
        }, null, null);
        return cn;
    }

    /// <summary>Every corner's FAN normal (accumulate_fan_normal), 3 per corner: what the corner's normal is before the
    /// custom normals are decoded against it. For telling a decoded zero on a fan that points somewhere from one that
    /// points up anyway (tools/prep-drill).</summary>
    internal static float[] BlenderFanNormals(float[] P, int[] tris, bool[] sharpFace)
    {
        var fn = new float[tris.Length * 3];
        if (tris.Length == 0) return fn;
        WalkFans(P, tris, sharpFace, (v, infos, fan, space) =>
        {
            foreach (int lc in fan) { int c = infos[lc].corner; fn[c * 3] = space.fnx; fn[c * 3 + 1] = space.fny; fn[c * 3 + 2] = space.fnz; }
        }, null, null);
        return fn;
    }

    /// <summary>normals_calc_corners' walk: for every vertex its corner infos, its local edges, the edge kinds (sharp faces, a
    /// third face, a winding mismatch), the fans (traverse_fan_local_corners), each fan's normal (accumulate_fan_normal) and
    /// space (corner_fan_space_define) - handed to onFan with the corners in the fan; onVertex after a vertex's fans; onLone
    /// for a vertex of no face.</summary>
    static void WalkFans(float[] P, int[] tris, bool[] sharpFace, Action<int, List<VertCornerInfo>, List<int>, FanSpace> onFan, Action<int, List<VertCornerInfo>> onVertex, Action<int> onLone)
    {
        int nv = P.Length / 3, nf = tris.Length / 3;
        var FN = FaceNormals(P, tris);
        // the faces around each vertex, ascending (build_vert_to_face_map sorts each group)
        var vertFaces = new List<int>[nv];
        for (int f = 0; f < nf; f++) for (int k = 0; k < 3; k++) { int v = tris[f * 3 + k]; (vertFaces[v] ?? (vertFaces[v] = new List<int>())).Add(f); }
        var infos = new List<VertCornerInfo>(); var localEdgeVerts = new List<int>(); var fan = new List<int>();
        for (int v = 0; v < nv; v++)
        {
            var faces = vertFaces[v];
            if (faces == null) { onLone?.Invoke(v); continue; }
            // collect_corner_info
            infos.Clear();
            foreach (int f in faces)
            {
                int k = tris[f * 3] == v ? 0 : tris[f * 3 + 1] == v ? 1 : 2;
                var ci = new VertCornerInfo { face = f, corner = f * 3 + k, cornerPrev = f * 3 + (k + 2) % 3, cornerNext = f * 3 + (k + 1) % 3 };
                ci.vertPrev = tris[ci.cornerPrev]; ci.vertNext = tris[ci.cornerNext];
                infos.Add(ci);
            }
            // calc_local_edge_indices: a VectorSet keyed by the other vertex, in encounter order
            localEdgeVerts.Clear();
            foreach (var ci in infos)
            {
                ci.edgePrev = localEdgeVerts.IndexOf(ci.vertPrev); if (ci.edgePrev < 0) { localEdgeVerts.Add(ci.vertPrev); ci.edgePrev = localEdgeVerts.Count - 1; }
                ci.edgeNext = localEdgeVerts.IndexOf(ci.vertNext); if (ci.edgeNext < 0) { localEdgeVerts.Add(ci.vertNext); ci.edgeNext = localEdgeVerts.Count - 1; }
            }
            // calc_connecting_edge_info: 0 uninitialized, 1 one corner, 2 two corners, 3 sharp (a sharp face, a third face, a winding mismatch)
            int ne = localEdgeVerts.Count;
            var kind = new int[ne]; var c1 = new int[ne]; var c2 = new int[ne]; var towards = new bool[ne];
            void AddCornerToEdge(int e, int lc, bool windingTowardsVert)
            {
                if (kind[e] == 0) { kind[e] = 1; c1[e] = lc; towards[e] = windingTowardsVert; }
                else if (kind[e] == 1) { if (towards[e] == windingTowardsVert) kind[e] = 3; else { kind[e] = 2; c2[e] = lc; } }
                else kind[e] = 3;
            }
            for (int lc = 0; lc < infos.Count; lc++)
            {
                var ci = infos[lc];
                if (sharpFace[ci.face]) { kind[ci.edgePrev] = 3; kind[ci.edgeNext] = 3; continue; }
                AddCornerToEdge(ci.edgePrev, lc, true);
                AddCornerToEdge(ci.edgeNext, lc, false);
            }
            // calc_edge_directions
            var edgeDirs = new float[ne][];
            for (int e = 0; e < ne; e++)
            {
                int o = localEdgeVerts[e];
                float dx = (float)(P[o * 3] - P[v * 3]), dy = (float)(P[o * 3 + 1] - P[v * 3 + 1]), dz = (float)(P[o * 3 + 2] - P[v * 3 + 2]);
                Normalize3(ref dx, ref dy, ref dz);
                edgeDirs[e] = new[] { dx, dy, dz };
            }
            var visited = new bool[infos.Count]; int visitedCount = 0, start = 0;
            while (true)
            {
                // traverse_fan_local_corners: forward over next edges, reversed; a cycle rotates to its lowest corner; else backward over prev edges
                fan.Clear(); fan.Add(start);
                bool cyclic = false;
                int current = start, le = infos[current].edgeNext;
                while (kind[le] == 2)
                {
                    current = c1[le] == current ? c2[le] : c1[le];
                    if (current == start) { cyclic = true; break; }
                    fan.Add(current); le = infos[current].edgeNext;
                }
                fan.Reverse();
                if (cyclic)
                {
                    int best = 0;
                    for (int i = 1; i < fan.Count; i++) if (infos[fan[i]].corner < infos[fan[best]].corner) best = i;
                    var rotated = new List<int>(); for (int i = 0; i < fan.Count; i++) rotated.Add(fan[(best + i) % fan.Count]);
                    fan.Clear(); fan.AddRange(rotated);
                }
                else
                {
                    current = start; le = infos[current].edgePrev;
                    while (kind[le] == 2)
                    {
                        current = current == c1[le] ? c2[le] : c1[le];
                        fan.Add(current); le = infos[current].edgePrev;
                    }
                }
                // accumulate_fan_normal
                float fx, fy, fz;
                if (fan.Count == 1) { int f0 = infos[fan[0]].face; fx = FN[f0 * 3]; fy = FN[f0 * 3 + 1]; fz = FN[f0 * 3 + 2]; }
                else
                {
                    fx = fy = fz = 0f;
                    foreach (int lc in fan)
                    {
                        var ci = infos[lc]; var dp = edgeDirs[ci.edgePrev]; var dn = edgeDirs[ci.edgeNext];
                        float factor = SafeAcosApprox(Dot3(dp[0], dp[1], dp[2], dn[0], dn[1], dn[2]));
                        fx = (float)(fx + (float)(FN[ci.face * 3] * factor)); fy = (float)(fy + (float)(FN[ci.face * 3 + 1] * factor)); fz = (float)(fz + (float)(FN[ci.face * 3 + 2] * factor));
                    }
                    Normalize3(ref fx, ref fy, ref fz);
                }
                // the fan's space: corner_fan_space_define over the first and last edge and, for a fan of several corners, every edge
                int edgeFirst = infos[fan[0]].edgeNext, edgeLast = infos[fan[fan.Count - 1]].edgePrev;
                var fanEdgeDirs = new List<float[]>();
                if (fan.Count > 1)
                {
                    foreach (int lc in fan) fanEdgeDirs.Add(edgeDirs[infos[lc].edgeNext]);
                    if (edgeLast != edgeFirst) fanEdgeDirs.Add(edgeDirs[edgeLast]);
                }
                var space = DefineSpace(fx, fy, fz, edgeDirs[edgeFirst][0], edgeDirs[edgeFirst][1], edgeDirs[edgeFirst][2], edgeDirs[edgeLast][0], edgeDirs[edgeLast][1], edgeDirs[edgeLast][2], fanEdgeDirs);
                space.fnx = fx; space.fny = fy; space.fnz = fz;
                onFan(v, infos, fan, space);
                visitedCount += fan.Count;
                if (visitedCount == infos.Count) break;
                foreach (int lc in fan) visited[lc] = true;
                while (visited[start]) start++;
            }
            onVertex?.Invoke(v, infos);
        }
    }
}
