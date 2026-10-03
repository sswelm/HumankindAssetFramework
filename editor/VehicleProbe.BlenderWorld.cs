// VehicleProbe.BlenderWorld.cs - every node's matrix_world as Blender holds it: float32, composed the way Blender composes it
// from what the glTF importer sets on the object (step 3 of replacing Blender, the visibility and inside-out verdicts read
// these - a grazing ray along a panel edge is decided by the last bit of the sample position, and the re-fused Dragon
// had 12 verdicts off against Blender with the double chain rounded once).
//   The importer (io_scene_gltf2: blender_gltf.py, vnode.py, node.py): a TRS node's location (x, -z, y), rotation
//   (w, x, -z, y) and scale (x, z, y) as given; a MATRIX node converted (convert_matrix) and decomposed by mathutils
//   (mat4_to_loc_rot_size: each column's float32 length is the scale, the normalized columns the rotation, negated
//   together when the determinant is negative; mat3_normalized_to_quat_fast reads the quaternion); the object is
//   parented with an identity parent-inverse.
//   Blender (object.cc, math_rotation_c.cc, math_matrix_c.cc): BKE_object_to_mat4 normalizes the quaternion
//   (normalize_qt_qt, float32), quat_to_mat3 (double inside, M_SQRT2 scaling, float32 out), scales each column
//   (mul_m3_m3m3 with a diagonal: one rounded product), adds the location; solve_parenting multiplies parent @ local
//   with mul_m4_m4m4's SSE2 association ((b0 a0 + b1 a1) + (b2 a2 + b3 a3)), float32 at every step.
// Proved against Blender's matrix_world bit for bit (the drill's MATRIX rows). A matrix is Blender's float m[4][4]:
// column-major, bm[col * 4 + row]; ToRowMajor gives the layout MatRow (VehicleProbe.InsideOut.cs) reads.
using System;
using System.Collections.Generic;

public static partial class VehicleProbe
{
    static float Sqrtf(float x) => (float)Math.Sqrt((double)x);   // sqrtf: the double square root rounded to float is the correctly rounded float

    static float[] IdentityF() { var m = new float[16]; m[0] = m[5] = m[10] = m[15] = 1f; return m; }

    internal static float[] ToRowMajor(float[] bm) { var r = new float[16]; for (int c = 0; c < 4; c++) for (int w = 0; w < 4; w++) r[w * 4 + c] = bm[c * 4 + w]; return r; }

    /// <summary>The importer's convert_loc, convert_quat, convert_scale: glTF (x, y, z) to Blender (x, -z, y); the quaternion as
    /// (w, x, -z, y). Unit scale 1, so the multiply by u is exact.</summary>
    static void ConvertTrs(double[] t, double[] q, double[] s, out float[] loc, out float[] quat, out float[] size)
    {
        loc = new[] { (float)t[0], (float)-t[2], (float)t[1] };
        quat = new[] { (float)q[3], (float)q[0], (float)-q[2], (float)q[1] };
        size = new[] { (float)s[0], (float)s[2], (float)s[1] };
    }

    /// <summary>get_node_trs for a matrix node: convert_matrix, then Matrix.decompose() - mat4_to_loc_rot_size and
    /// mat3_normalized_to_quat_fast, float32 as Blender runs them.</summary>
    static void DecomposeAsBlender(double[] g, out float[] loc, out float[] quat, out float[] size)
    {
        var m = new float[16]; for (int i = 0; i < 16; i++) m[i] = (float)g[i];
        // convert_matrix's rows: item(row, col)
        var it = new float[4, 4];
        it[0, 0] = m[0]; it[0, 1] = -m[8]; it[0, 2] = m[4]; it[0, 3] = m[12];
        it[1, 0] = -m[2]; it[1, 1] = m[10]; it[1, 2] = -m[6]; it[1, 3] = -m[14];
        it[2, 0] = m[1]; it[2, 1] = -m[9]; it[2, 2] = m[5]; it[2, 3] = m[13];
        it[3, 0] = m[3]; it[3, 1] = -m[11]; it[3, 2] = m[7]; it[3, 3] = m[15];
        // mat3_to_rot_size: size[c] = normalize_v3_v3(rot[c], mat3[c]) over the columns
        var rot = new float[3][]; size = new float[3];
        for (int c = 0; c < 3; c++)
        {
            float x = it[0, c], y = it[1, c], z = it[2, c];
            float d = (float)((float)((float)(x * x) + (float)(y * y)) + (float)(z * z));
            if (d > 1.0e-35f) { d = Sqrtf(d); float f = (float)(1.0f / d); rot[c] = new[] { (float)(x * f), (float)(y * f), (float)(z * f) }; }
            else { rot[c] = new float[3]; d = 0f; }
            size[c] = d;
        }
        if (Det3(rot) < 0f) for (int c = 0; c < 3; c++) { size[c] = -size[c]; for (int k = 0; k < 3; k++) rot[c][k] = -rot[c][k]; }
        loc = new[] { it[0, 3], it[1, 3], it[2, 3] };
        quat = Mat3NormalizedToQuatFast(rot);
    }

    /// <summary>determinant_m3_array over m[col][row].</summary>
    static float Det3(float[][] m)
    {
        float a = (float)(m[0][0] * (float)((float)(m[1][1] * m[2][2]) - (float)(m[1][2] * m[2][1])));
        float b = (float)(m[1][0] * (float)((float)(m[0][1] * m[2][2]) - (float)(m[0][2] * m[2][1])));
        float c = (float)(m[2][0] * (float)((float)(m[0][1] * m[1][2]) - (float)(m[0][2] * m[1][1])));
        return (float)((float)(a - b) + c);
    }

    static float DotQt(float[] a, float[] b) => (float)((float)((float)((float)(a[0] * b[0]) + (float)(a[1] * b[1])) + (float)(a[2] * b[2])) + (float)(a[3] * b[3]));

    /// <summary>normalize_qt: length sqrtf(dot), each component times 1.0f / length; a zero quaternion becomes (0, 1, 0, 0).</summary>
    static void NormalizeQt(float[] q)
    {
        float len = Sqrtf(DotQt(q, q));
        if (len != 0f) { float f = (float)(1.0f / len); for (int i = 0; i < 4; i++) q[i] = (float)(q[i] * f); }
        else { q[1] = 1f; q[0] = q[2] = q[3] = 0f; }
    }

    /// <summary>mat3_normalized_to_quat_fast (Mike Day's method as Blender writes it), mat[col][row], quaternion (w, x, y, z).</summary>
    static float[] Mat3NormalizedToQuatFast(float[][] mat)
    {
        var q = new float[4];
        if (mat[2][2] < 0f)
        {
            if (mat[0][0] > mat[1][1])
            {
                float trace = (float)((float)((float)(1.0f + mat[0][0]) - mat[1][1]) - mat[2][2]);
                float s = (float)(2.0f * Sqrtf(trace));
                if (mat[1][2] < mat[2][1]) s = -s;
                q[1] = (float)(0.25f * s); s = (float)(1.0f / s);
                q[0] = (float)((float)(mat[1][2] - mat[2][1]) * s); q[2] = (float)((float)(mat[0][1] + mat[1][0]) * s); q[3] = (float)((float)(mat[2][0] + mat[0][2]) * s);
                if (trace == 1.0f && q[0] == 0f && q[2] == 0f && q[3] == 0f) q[1] = 1.0f;
            }
            else
            {
                float trace = (float)((float)((float)(1.0f - mat[0][0]) + mat[1][1]) - mat[2][2]);
                float s = (float)(2.0f * Sqrtf(trace));
                if (mat[2][0] < mat[0][2]) s = -s;
                q[2] = (float)(0.25f * s); s = (float)(1.0f / s);
                q[0] = (float)((float)(mat[2][0] - mat[0][2]) * s); q[1] = (float)((float)(mat[0][1] + mat[1][0]) * s); q[3] = (float)((float)(mat[1][2] + mat[2][1]) * s);
                if (trace == 1.0f && q[0] == 0f && q[1] == 0f && q[3] == 0f) q[2] = 1.0f;
            }
        }
        else
        {
            if (mat[0][0] < -mat[1][1])
            {
                float trace = (float)((float)((float)(1.0f - mat[0][0]) - mat[1][1]) + mat[2][2]);
                float s = (float)(2.0f * Sqrtf(trace));
                if (mat[0][1] < mat[1][0]) s = -s;
                q[3] = (float)(0.25f * s); s = (float)(1.0f / s);
                q[0] = (float)((float)(mat[0][1] - mat[1][0]) * s); q[1] = (float)((float)(mat[2][0] + mat[0][2]) * s); q[2] = (float)((float)(mat[1][2] + mat[2][1]) * s);
                if (trace == 1.0f && q[0] == 0f && q[1] == 0f && q[2] == 0f) q[3] = 1.0f;
            }
            else
            {
                float trace = (float)((float)((float)(1.0f + mat[0][0]) + mat[1][1]) + mat[2][2]);
                float s = (float)(2.0f * Sqrtf(trace));
                q[0] = (float)(0.25f * s); s = (float)(1.0f / s);
                q[1] = (float)((float)(mat[1][2] - mat[2][1]) * s); q[2] = (float)((float)(mat[2][0] - mat[0][2]) * s); q[3] = (float)((float)(mat[0][1] - mat[1][0]) * s);
                if (trace == 1.0f && q[1] == 0f && q[2] == 0f && q[3] == 0f) q[0] = 1.0f;
            }
        }
        float len2 = DotQt(q, q);
        if (Math.Abs((double)(float)(len2 - 1.0f)) >= (double)(float)(0.0002f * 3)) NormalizeQt(q);
        return q;
    }

    /// <summary>BKE_object_to_mat4: the normalized quaternion through quat_to_mat3 (double inside), each column times its scale
    /// (one rounded product - the other two terms are zeros), the location; column-major.</summary>
    static float[] ObjectMatrix(float[] loc, float[] quat, float[] size)
    {
        var tq = (float[])quat.Clone(); NormalizeQt(tq);
        const double M_SQRT2 = 1.4142135623730951;
        double q0 = M_SQRT2 * (double)tq[0], q1 = M_SQRT2 * (double)tq[1], q2 = M_SQRT2 * (double)tq[2], q3 = M_SQRT2 * (double)tq[3];
        double qda = q0 * q1, qdb = q0 * q2, qdc = q0 * q3, qaa = q1 * q1, qab = q1 * q2, qac = q1 * q3, qbb = q2 * q2, qbc = q2 * q3, qcc = q3 * q3;
        var r = new float[3][];
        r[0] = new[] { (float)(1.0 - qbb - qcc), (float)(qdc + qab), (float)(-qdb + qac) };
        r[1] = new[] { (float)(-qdc + qab), (float)(1.0 - qaa - qcc), (float)(qda + qbc) };
        r[2] = new[] { (float)(qdb + qac), (float)(-qda + qbc), (float)(1.0 - qaa - qbb) };
        var bm = new float[16];
        for (int c = 0; c < 3; c++) for (int w = 0; w < 3; w++) bm[c * 4 + w] = (float)(size[c] * r[c][w]);
        bm[12] = loc[0]; bm[13] = loc[1]; bm[14] = loc[2]; bm[15] = 1f;
        return bm;
    }

    /// <summary>mul_m4_m4m4(R, A, B) = A @ B with the SSE2 association: column i of R = (B[i][0] A[0] + B[i][1] A[1]) + (B[i][2] A[2] + B[i][3] A[3]).</summary>
    static float[] MulM4(float[] A, float[] B)
    {
        var R = new float[16];
        for (int i = 0; i < 4; i++)
            for (int w = 0; w < 4; w++)
                R[i * 4 + w] = (float)((float)((float)(B[i * 4 + 0] * A[0 * 4 + w]) + (float)(B[i * 4 + 1] * A[1 * 4 + w])) + (float)((float)(B[i * 4 + 2] * A[2 * 4 + w]) + (float)(B[i * 4 + 3] * A[3 * 4 + w])));
        return R;
    }

    /// <summary>Every node's matrix_world as Blender holds it (column-major float32), at the pose: posedTrs gives a node's
    /// animated translation, rotation and scale (any of them null for an unanimated channel), or null for an unanimated node.
    /// An animated channel replaces the base value the importer set - for a matrix node, the decomposed matrix's.</summary>
    internal static float[][] BlenderWorldMatrices(HafModel m, Func<int, double[][]> posedTrs)
    {
        var world = new float[m.Nodes.Count][];
        var stack = new Stack<int>(); var seen = new bool[m.Nodes.Count];
        for (int i = m.Nodes.Count - 1; i >= 0; i--) if (m.Nodes[i].Parent < 0) stack.Push(i);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            if (seen[n]) continue;
            seen[n] = true;
            var node = m.Nodes[n];
            float[] loc, quat, size;
            if (node.HasMatrix) DecomposeAsBlender(node.Matrix, out loc, out quat, out size);
            else ConvertTrs(node.Translation, node.Rotation, node.Scale, out loc, out quat, out size);
            var p = posedTrs?.Invoke(n);
            if (p != null)
            {
                float[] l2, q2, s2;
                ConvertTrs(p[0] ?? new double[] { 0, 0, 0 }, p[1] ?? new double[] { 0, 0, 0, 1 }, p[2] ?? new double[] { 1, 1, 1 }, out l2, out q2, out s2);
                if (p[0] != null) loc = l2;
                if (p[1] != null) quat = q2;
                if (p[2] != null) size = s2;
            }
            var local = ObjectMatrix(loc, quat, size);
            world[n] = node.Parent >= 0 && world[node.Parent] != null ? MulM4(world[node.Parent], local) : local;
            foreach (var c in node.Children) stack.Push(c);
        }
        for (int i = 0; i < m.Nodes.Count; i++) if (world[i] == null) world[i] = IdentityF();
        return world;
    }
}
