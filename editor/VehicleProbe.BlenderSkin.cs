// VehicleProbe.BlenderSkin.cs - a skinned mesh's vertex positions and normals as Blender's importer
// stores them: skinned into the bind pose in numpy float32 (skin_into_bind_pose, io_scene_gltf2 mesh.py), with the joint
// matrices the importer computes in mathutils (vnode.py): each joint's bind matrix is GUESSED from the inverse bind
// matrices (guess_original_bind_pose, on by default) - bind_local = inv_binds[parent] @ inv_binds[joint].inverted_safe()
// (mathutils' adjugate over the determinant, float32), decomposed to a translation and a
// quaternion, rebuilt as Translation(t) @ Quaternion(r).to_matrix() and chained to the armature; joint_mat = bind_arma_mat
// @ inv_bind; then per vertex the float32 blend of the joint matrices by weight, divided by the weight sum, the 3x3 times
// the position plus the translation (numpy's matmul is a plain float32 chain here: measured, no BLAS, no FMA), the normal
// through the 3x3 and normalized. A zero-area triangle's normal is decided by these bits (3 skinned parts of 14,023 read their inside-out verdict
// off that ulp until this; now all 14,023 agree). Read from Blender 5.1's source and the installed importer; the build is
// clang-cl at x86-64-v2 with fp-contract off, so every step here is the binary's own arithmetic.
// Matrices here are mathutils' item(row, col) layout stored row-major, Blender's frame.
using System;
using System.Collections.Generic;

public static partial class VehicleProbe
{
    /// <summary>mathutils Matrix @ Matrix: item(row, col) = float(sum over k in double of float(a(row, k) * b(k, col))).</summary>
    static float[] MatMulMathutils(float[] a, float[] b)
    {
        var r = new float[16];
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 4; col++)
            {
                double d = 0.0;
                for (int k = 0; k < 4; k++) d += (double)(float)(a[row * 4 + k] * b[k * 4 + col]);
                r[row * 4 + col] = (float)d;
            }
        return r;
    }

    static float[] IdentityRow() { var m = new float[16]; m[0] = m[5] = m[10] = m[15] = 1f; return m; }

    /// <summary>The importer's convert_matrix for an inverse bind matrix (16 floats, column-major glTF): rows (m0 -m8 m4 m12),
    /// (-m2 m10 -m6 -m14), (m1 -m9 m5 m13), (m3 -m11 m7 m15); row-major item layout.</summary>
    static float[] ConvertMatrixGltf(double[] g, int offset)
    {
        var m = new float[16]; for (int i = 0; i < 16; i++) m[i] = (float)g[offset + i];
        return new[] { m[0], -m[8], m[4], m[12], -m[2], m[10], -m[6], -m[14], m[1], -m[9], m[5], m[13], m[3], -m[11], m[7], m[15] };
    }

    /// <summary>determinant_m3(a1, a2, a3, b1, b2, b3, c1, c2, c3) as Blender writes it - a1 det2(b2, b3, c2, c3) - b1 det2(a2, a3,
    /// c2, c3) + c1 det2(a2, a3, b2, b3), det2(a, b, c, d) = a d - b c - float32, left to right.</summary>
    static float Det3(float a1, float a2, float a3, float b1, float b2, float b3, float c1, float c2, float c3)
    {
        float t1 = (float)(a1 * (float)((float)(b2 * c3) - (float)(b3 * c2)));
        float t2 = (float)(b1 * (float)((float)(a2 * c3) - (float)(a3 * c2)));
        float t3 = (float)(c1 * (float)((float)(a2 * b3) - (float)(a3 * b2)));
        return (float)((float)(t1 - t2) + t3);
    }

    /// <summary>mathutils Matrix.inverted_safe(): matrix_invert_safe_internal - the determinant (determinant_m4, float32) and
    /// the adjugate (adjoint_m4_m4) divided by it, entry by entry; NOT Eigen, which only Matrix.inverted() reaches. A
    /// zero float32 determinant gets PSEUDOINVERSE_EPSILON on its diagonal first, then identity if still zero.
    /// In and out in the row-major item layout; Blender's M[col][row] is read off it. The input is never modified.</summary>
    internal static float[] InvertedSafe(float[] it)
    {
        return InvertedSafe(it, true);
    }

    static float[] InvertedSafe(float[] it, bool perturb)
    {
        // a1..d4 as adjoint_m4_m4 names them: aN = M[N-1][0] (column N-1, row 0), bN = M[N-1][1], cN = M[N-1][2], dN = M[N-1][3]
        float a1 = it[0 * 4 + 0], b1 = it[1 * 4 + 0], c1 = it[2 * 4 + 0], d1 = it[3 * 4 + 0];
        float a2 = it[0 * 4 + 1], b2 = it[1 * 4 + 1], c2 = it[2 * 4 + 1], d2 = it[3 * 4 + 1];
        float a3 = it[0 * 4 + 2], b3 = it[1 * 4 + 2], c3 = it[2 * 4 + 2], d3 = it[3 * 4 + 2];
        float a4 = it[0 * 4 + 3], b4 = it[1 * 4 + 3], c4 = it[2 * 4 + 3], d4 = it[3 * 4 + 3];
        // determinant_m4: a1 D1 - b1 D2 + c1 D3 - d1 D4, left to right
        float det = (float)((float)((float)((float)(a1 * Det3(b2, b3, b4, c2, c3, c4, d2, d3, d4)) - (float)(b1 * Det3(a2, a3, a4, c2, c3, c4, d2, d3, d4))) + (float)(c1 * Det3(a2, a3, a4, b2, b3, b4, d2, d3, d4))) - (float)(d1 * Det3(a2, a3, a4, b2, b3, b4, c2, c3, c4)));
        if (det == 0f)
        {
            // Even an invertible glTF IBM can underflow here. Match mathutils' single retry, including signed zeros
            // from the identity's adjugate when the perturbed determinant is still zero.
            if (!perturb) return InvertedSafe(IdentityRow(), false);
            var adjusted = (float[])it.Clone();
            for (int i = 0; i < 4; i++) adjusted[i * 5] = (float)(adjusted[i * 5] + 1e-8f);
            return InvertedSafe(adjusted, false);
        }
        // adjoint_m4_m4: R[col][row]
        var R = new float[4][];
        for (int c = 0; c < 4; c++) R[c] = new float[4];
        R[0][0] = Det3(b2, b3, b4, c2, c3, c4, d2, d3, d4);
        R[1][0] = -Det3(a2, a3, a4, c2, c3, c4, d2, d3, d4);
        R[2][0] = Det3(a2, a3, a4, b2, b3, b4, d2, d3, d4);
        R[3][0] = -Det3(a2, a3, a4, b2, b3, b4, c2, c3, c4);
        R[0][1] = -Det3(b1, b3, b4, c1, c3, c4, d1, d3, d4);
        R[1][1] = Det3(a1, a3, a4, c1, c3, c4, d1, d3, d4);
        R[2][1] = -Det3(a1, a3, a4, b1, b3, b4, d1, d3, d4);
        R[3][1] = Det3(a1, a3, a4, b1, b3, b4, c1, c3, c4);
        R[0][2] = Det3(b1, b2, b4, c1, c2, c4, d1, d2, d4);
        R[1][2] = -Det3(a1, a2, a4, c1, c2, c4, d1, d2, d4);
        R[2][2] = Det3(a1, a2, a4, b1, b2, b4, d1, d2, d4);
        R[3][2] = -Det3(a1, a2, a4, b1, b2, b4, c1, c2, c4);
        R[0][3] = -Det3(b1, b2, b3, c1, c2, c3, d1, d2, d3);
        R[1][3] = Det3(a1, a2, a3, c1, c2, c3, d1, d2, d3);
        R[2][3] = -Det3(a1, a2, a3, b1, b2, b3, d1, d2, d3);
        R[3][3] = Det3(a1, a2, a3, b1, b2, b3, c1, c2, c3);
        // matrix_invert_with_det_n_internal: item(row j, col i) = R[i][j] / det
        var r = new float[16];
        for (int c = 0; c < 4; c++) for (int w = 0; w < 4; w++) r[w * 4 + c] = (float)(R[c][w] / det);
        return r;
    }

    /// <summary>Matrix.decompose() of a matrix already in Blender's frame (row-major item layout): mat4_to_loc_rot_size and
    /// mat3_normalized_to_quat_fast.</summary>
    static void DecomposeBlenderMatrix(float[] it, out float[] loc, out float[] quat, out float[] size)
    {
        var rot = new float[3][]; size = new float[3];
        for (int c = 0; c < 3; c++)
        {
            float x = it[0 * 4 + c], y = it[1 * 4 + c], z = it[2 * 4 + c];
            float d = (float)((float)((float)(x * x) + (float)(y * y)) + (float)(z * z));
            if (d > 1.0e-35f) { d = Sqrtf(d); float f = (float)(1.0f / d); rot[c] = new[] { (float)(x * f), (float)(y * f), (float)(z * f) }; }
            else { rot[c] = new float[3]; d = 0f; }
            size[c] = d;
        }
        if (Det3(rot) < 0f) for (int c = 0; c < 3; c++) { size[c] = -size[c]; for (int k = 0; k < 3; k++) rot[c][k] = -rot[c][k]; }
        loc = new[] { it[0 * 4 + 3], it[1 * 4 + 3], it[2 * 4 + 3] };
        quat = Mat3NormalizedToQuatFast(rot);
    }

    /// <summary>Matrix.Translation(t) @ Quaternion(r).to_matrix().to_4x4(): quat_to_mat3 (double inside, no normalization here)
    /// into a 4x4 with the translation; row-major item layout. The product with the translation matrix is exact.</summary>
    static float[] TranslationRotation(float[] t, float[] q)
    {
        const double M_SQRT2 = 1.4142135623730951;
        double q0 = M_SQRT2 * (double)q[0], q1 = M_SQRT2 * (double)q[1], q2 = M_SQRT2 * (double)q[2], q3 = M_SQRT2 * (double)q[3];
        double qda = q0 * q1, qdb = q0 * q2, qdc = q0 * q3, qaa = q1 * q1, qab = q1 * q2, qac = q1 * q3, qbb = q2 * q2, qbc = q2 * q3, qcc = q3 * q3;
        // m[col][row] as quat_to_mat3 writes it
        float[][] mcol = { new[] { (float)(1.0 - qbb - qcc), (float)(qdc + qab), (float)(-qdb + qac) }, new[] { (float)(-qdc + qab), (float)(1.0 - qaa - qcc), (float)(qda + qbc) }, new[] { (float)(qdb + qac), (float)(-qda + qbc), (float)(1.0 - qaa - qbb) } };
        var r = IdentityRow();
        for (int c = 0; c < 3; c++) for (int w = 0; w < 3; w++) r[w * 4 + c] = mcol[c][w];
        r[3] = t[0]; r[7] = t[1]; r[11] = t[2];
        return r;
    }

    /// <summary>One skin as the importer stores its meshes: the joint matrices (bind_arma_mat @ inv_bind, float32) and the
    /// skinning of a vertex by them.</summary>
    internal sealed class BlenderSkinner
    {
        public float[][] JointMats;   // per joint, row-major item layout, Blender's frame

        public static BlenderSkinner Build(HafModel m, int skinIndex, BlenderNames.Result names)
        {
            var sk = m.Skins[skinIndex];
            // pick_bind_pose builds ONE inverse-bind map over every skin, in file order (the last skin wins for a shared
            // joint). A mesh is then retargeted with its OWN skin's inverse binds, not that global map.
            var invBinds = new Dictionary<int, float[]> { { -1, IdentityRow() } };
            foreach (var bindSkin in m.Skins)
            {
                if (bindSkin.InverseBindMatrices == null) continue;
                if (bindSkin.Skeleton >= 0)
                {
                    int skel = bindSkin.Skeleton;
                    if (Array.IndexOf(bindSkin.Joints, skel) >= 0) skel = names.BoneParent[skel];
                    if (!invBinds.ContainsKey(skel)) invBinds[skel] = IdentityRow();
                }
                for (int i = 0; i < bindSkin.Joints.Length; i++) invBinds[bindSkin.Joints[i]] = ConvertMatrixGltf(bindSkin.InverseBindMatrices, i * 16);
            }
            // bind_trans / bind_rot per bone node (every node the importer makes a bone of), then bind_arma_mat down the chain
            var bindArma = new Dictionary<int, float[]>();
            float[] BindArmaOf(int node)
            {
                if (bindArma.TryGetValue(node, out var done)) return done;
                var n = m.Nodes[node];
                float[] loc, quat, size;
                if (n.HasMatrix) DecomposeAsBlender(n.Matrix, out loc, out quat, out size); else ConvertTrs(n.Translation, n.Rotation, n.Scale, out loc, out quat, out size);
                int parent = names.BoneParent[node];
                if (invBinds.ContainsKey(node) && invBinds.ContainsKey(parent))
                {
                    var bindLocal = MatMulMathutils(invBinds[parent], InvertedSafe(invBinds[node]));
                    DecomposeBlenderMatrix(bindLocal, out loc, out quat, out size);
                }
                var local = TranslationRotation(loc, quat);
                var parentBind = parent >= 0 && names.IsBone[parent] ? BindArmaOf(parent) : IdentityRow();
                return bindArma[node] = MatMulMathutils(parentBind, local);
            }
            var s = new BlenderSkinner { JointMats = new float[sk.Joints.Length][] };
            for (int i = 0; i < sk.Joints.Length; i++)
            {
                int j = sk.Joints[i];
                var inv = sk.InverseBindMatrices != null ? ConvertMatrixGltf(sk.InverseBindMatrices, i * 16) : IdentityRow();
                s.JointMats[i] = MatMulMathutils(BindArmaOf(j), inv);
            }
            return s;
        }

        /// <summary>The vertex's skinning matrix: the float32 blend of its joints' matrices by weight, divided by the weight sum.</summary>
        float[] SkinningMatrix(HafPrimitive p, int v)
        {
            var acc = new float[16]; float wsum = 0f;
            // the importer loops over the influence sets in order (JOINTS_0/WEIGHTS_0, then JOINTS_1/WEIGHTS_1), four influences each
            for (int set = 0; set < 2; set++)
            {
                var joints = set == 0 ? p.Joints : p.Joints1; var weights = set == 0 ? p.Weights : p.Weights1;
                if (joints == null || weights == null) continue;
                for (int i = 0; i < 4; i++)
                {
                    float w = weights[v * 4 + i]; int j = joints[v * 4 + i];
                    if (j >= JointMats.Length) j = 0;
                    var J = JointMats[j];
                    for (int e = 0; e < 16; e++) acc[e] = (float)(acc[e] + (float)(w * J[e]));
                    wsum = (float)(wsum + w);
                }
            }
            // The importer sets WEIGHTS_0[v, 0] to 1: the first influence can name any joint in the skin.
            if (wsum == 0f) { int j = p.Joints[v * 4]; var J = JointMats[j < JointMats.Length ? j : 0]; for (int e = 0; e < 16; e++) acc[e] = J[e]; wsum = 1f; }
            for (int e = 0; e < 16; e++) acc[e] = (float)(acc[e] / wsum);
            return acc;
        }

        /// <summary>The skinned position, Blender's frame (the file's glTF position converted first, as the importer converts the batch).</summary>
        public float[] Position(HafPrimitive p, int v)
        {
            var M = SkinningMatrix(p, v);
            float x = p.Positions[v * 3], y = -p.Positions[v * 3 + 2], z = p.Positions[v * 3 + 1];
            var r = new float[3];
            for (int row = 0; row < 3; row++)
            {
                float s = (float)(0f + (float)(M[row * 4 + 0] * x)); s = (float)(s + (float)(M[row * 4 + 1] * y)); s = (float)(s + (float)(M[row * 4 + 2] * z));
                r[row] = (float)(s + M[row * 4 + 3]);
            }
            return r;
        }

        /// <summary>The skinned normal, Blender's frame: the 3x3 times the file normal, normalized as numpy does (x*x summed, sqrt, divide).</summary>
        public float[] Normal(HafPrimitive p, int v)
        {
            var M = SkinningMatrix(p, v);
            float x = p.Normals[v * 3], y = -p.Normals[v * 3 + 2], z = p.Normals[v * 3 + 1];
            var r = new float[3];
            for (int row = 0; row < 3; row++)
            {
                float s = (float)(0f + (float)(M[row * 4 + 0] * x)); s = (float)(s + (float)(M[row * 4 + 1] * y)); s = (float)(s + (float)(M[row * 4 + 2] * z));
                r[row] = s;
            }
            float n2 = (float)((float)((float)(r[0] * r[0]) + (float)(r[1] * r[1])) + (float)(r[2] * r[2]));
            float nrm = Sqrtf(n2);
            if (nrm != 0f) { r[0] = (float)(r[0] / nrm); r[1] = (float)(r[1] / nrm); r[2] = (float)(r[2] / nrm); }
            return r;
        }
    }
}
