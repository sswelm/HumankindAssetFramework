// VehicleProbe.BlenderArmature.cs - a skin's bones as Blender's importer builds them and its exporter writes them (step 5 of
// replacing Blender, milestone d, part 4b, 2026-10-07): the joint nodes' transforms and the skin's inverse bind matrices
// in the file prep_model.py writes. The chain, each step float32 as the binary computes it:
//   importer (io_scene_gltf2/blender/imp/vnode.py, node.py): the bind translation and rotation per bone (pick_bind_pose,
//     as BlenderSkinner has them); prettify_bones with the default BLENDER heuristic - every bone's edit rotation is
//     turned by (sqrt(1/2), sqrt(1/2), 0, 0), its bone children's translation and rotation turned back (mathutils
//     mul_qt_qtqt, mul_qt_v3), and a bone length picked: the shortest bone child over 0.004 away, else the parent's
//     length, else the bone's own distance, else 1 (Vector.length: a double square root of the float dot); then
//     calc_bone_matrices chains Translation(t) @ Quaternion(r).to_matrix() down the tree; create_bones sets each edit
//     bone's head to arma_mat @ (0, 0, 0), tail to arma_mat @ (0, 1, 0) (mathutils: double sums of float products),
//     its length to bone_length / max(armature.scale) (a double, then float32: rna_EditBone_length_set), and aligns the
//     roll to arma_mat @ (0, 0, 1) - head (ED_armature_ebone_roll_to_vector).
//   Blender (armature_utils.cc, armature.cc): leaving edit mode makes the bones - arm_head/arm_tail from the edit bone;
//     armature_finalize_restpose: a child's head and tail relative to its parent's tail through Eigen's inverse of the
//     parent's arm_mat (BlenderEigen) and mul_mat3_m4_v3; where_is_bone with roll 0 (vec_roll_to_mat3, the offset
//     matrix with the parent's length added along Y, mul_m4_m4m4's SSE association); then the roll that brings that
//     matrix onto the edit bone's (invert_m3_m3, mul_m3_m3m3, -atan2f) and where_is_bone once more: bone.matrix_local.
//   exporter (tree.py, nodes.py, skins.py): a joint's matrix_world is armature.matrix_world @ bone.matrix_local @ the
//     Z-up to Y-up basis change; its transform follows the node rule (ExporterTrs against its parent joint, or the
//     armature); the inverse bind matrix is (basis @ armature.matrix_world @ bone.matrix_local).inverted_safe(),
//     written column by column.
// Proof: tools/prep-drill (every joint Blender wrote: transform bits and inverse bind matrix bits).
using System;
using System.Collections.Generic;
using System.Linq;

public static partial class VehicleProbe
{
    public sealed class ArmatureResult
    {
        public Dictionary<int, float[]> MatrixLocal = new Dictionary<int, float[]>();   // per bone node: bone.matrix_local, row-major items (Blender's frame)
        public Dictionary<int, float[]> World = new Dictionary<int, float[]>();         // per bone node: the exporter's matrix_world (with the basis change), column-major as the probe holds matrices
        public Dictionary<int, float[]> InverseBind = new Dictionary<int, float[]>();   // per bone node: the written inverse bind matrix, 16 floats column by column (glTF order)
        public Dictionary<int, float[]> EditHead = new Dictionary<int, float[]>(), EditTail = new Dictionary<int, float[]>();   // the edit bone, armature space
        public Dictionary<int, float> EditRoll = new Dictionary<int, float>();
        public List<string> Notes = new List<string>();
    }

    static readonly float[] AxisBasisChange = { 1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1 };   // row-major items: (x, y, z) -> (x, z, -y)

    /// <summary>The bones of one armature (its glTF node, or -1 for the dummy root's) as Blender holds and writes them.
    /// armatureWorld: the armature object's matrix_world, column-major float32 (the probe's); armatureScale: its scale
    /// as the importer set it (float32, 3).</summary>
    internal static ArmatureResult BlenderArmature(HafModel m, BlenderNames.Result names, int armatureNode, float[] armatureWorld, float[] armatureScale)
    {
        var r = new ArmatureResult();
        var bones = names.BoneNodesInOrder.Where(b => names.ArmatureNodeOfBone[b] == armatureNode).ToList();
        if (bones.Count == 0) return r;
        var boneSet = new HashSet<int>(bones);
        var children = new Dictionary<int, List<int>>();   // bone -> bone children, creation order (the vnode's children order)
        foreach (int b in bones) children[b] = new List<int>();
        foreach (int b in bones) { int p = names.BoneParent[b]; if (boneSet.Contains(p)) children[p].Add(b); }
        var roots = bones.Where(b => !boneSet.Contains(names.BoneParent[b])).ToList();

        // ---- pick_bind_pose: bind_trans / bind_rot per bone (BlenderSkinner's rule)
        BindTransRot(m, names, boneSet, out var bindTrans, out var bindRot);
        var ebTrans = bones.ToDictionary(b => b, b => (float[])bindTrans[b].Clone());
        var ebRot = bones.ToDictionary(b => b, b => (float[])bindRot[b].Clone());

        // ---- prettify_bones, BLENDER heuristic: depth-first; the length first, then the turn
        var boneLength = new Dictionary<int, double>();
        var turn = new[] { (float)(Math.Sqrt(2) / 2), (float)(Math.Sqrt(2) / 2), 0f, 0f };   // Quaternion((2**0.5 / 2, 2**0.5 / 2, 0, 0)) as float32
        void Prettify(int b)
        {
            // pick_bone_length: Vector.length is sqrt of the double sum of float products
            var childLens = children[b].Select(c => VecLength(ebTrans[c])).Where(l => l > 0.004).ToList();
            double len;
            if (childLens.Count > 0) len = childLens.Min();
            else if (boneSet.Contains(names.BoneParent[b])) len = boneLength[names.BoneParent[b]];
            else if (VecLength(ebTrans[b]) > 0.004) len = VecLength(ebTrans[b]);
            else len = 1;
            boneLength[b] = len;
            // rotate_edit_bone(rot): editbone_rot @= rot; the children's trans and rot turned by rot's conjugate
            ebRot[b] = MulQt(ebRot[b], turn);
            var inv = new[] { turn[0], -turn[1], -turn[2], -turn[3] };
            foreach (int c in children[b]) { ebTrans[c] = MulQtV3(inv, ebTrans[c]); ebRot[c] = MulQt(inv, ebRot[c]); }
            foreach (int c in children[b]) Prettify(c);
        }
        foreach (int b in roots) Prettify(b);

        // ---- calc_bone_matrices: editbone_arma_mat down the tree
        var armaMat = new Dictionary<int, float[]>();
        void ArmaMats(int b, float[] parent)
        {
            var local = TranslationRotation(ebTrans[b], ebRot[b]);
            armaMat[b] = MatMulMathutils(parent, local);
            foreach (int c in children[b]) ArmaMats(c, armaMat[b]);
        }
        foreach (int b in roots) ArmaMats(b, IdentityRow());

        // ---- create_bones: head, tail, length, roll (an edit bone: head, tail in armature space, roll)
        float maxScale = Math.Max(armatureScale[0], Math.Max(armatureScale[1], armatureScale[2]));
        var head = new Dictionary<int, float[]>(); var tail = new Dictionary<int, float[]>(); var roll = new Dictionary<int, float>();
        foreach (int b in bones)
        {
            var am = armaMat[b];
            var h = MatVec(am, 0f, 0f, 0f); var t = MatVec(am, 0f, 1f, 0f);
            // editbone.length = bone_length / max(scale): a double divided, stored as float32; rna_EditBone_length_set
            float length = (float)(boneLength[b] / (double)maxScale);
            float dx = (float)(t[0] - h[0]), dy = (float)(t[1] - h[1]), dz = (float)(t[2] - h[2]);
            if (NormalizeV3Blender(ref dx, ref dy, ref dz) == 0f) dz = 1f;
            t = new[] { (float)(h[0] + (float)(dx * length)), (float)(h[1] + (float)(dy * length)), (float)(h[2] + (float)(dz * length)) };
            // align_roll(arma_mat @ (0, 0, 1) - head)
            var z = MatVec(am, 0f, 0f, 1f);
            var axis = new[] { (float)(z[0] - h[0]), (float)(z[1] - h[1]), (float)(z[2] - h[2]) };
            head[b] = h; tail[b] = t;
            roll[b] = RollToVector(h, t, axis);
            r.EditHead[b] = h; r.EditTail[b] = t; r.EditRoll[b] = roll[b];
        }

        // ---- ED_armature_from_edit + armature_finalize_restpose + where_is_bone: bone.matrix_local (arm_mat)
        var armMat = new Dictionary<int, float[]>();   // Blender float[4][4] as 16 floats, M[col][row]
        var bLength = new Dictionary<int, float>();
        void Finalize(int b, int parent)
        {
            float[] bh, bt;   // the bone's head and tail in the parent's space
            if (parent >= 0)
            {
                var parInv = BlenderEigen.InvertM4(armMat[parent]);
                bh = new[] { (float)(head[b][0] - tail[parent][0]), (float)(head[b][1] - tail[parent][1]), (float)(head[b][2] - tail[parent][2]) };
                bt = new[] { (float)(tail[b][0] - tail[parent][0]), (float)(tail[b][1] - tail[parent][1]), (float)(tail[b][2] - tail[parent][2]) };
                MulMat3M4V3(parInv, bh); MulMat3M4V3(parInv, bt);
            }
            else { bh = (float[])head[b].Clone(); bt = (float[])tail[b].Clone(); }
            // where_is_bone with roll 0
            float[] arm = WhereIsBone(bh, bt, 0f, parent >= 0 ? armMat[parent] : null, parent >= 0 ? bLength[parent] : 0f, out float len0);
            // the roll that brings it onto the edit bone's matrix: premat = ebone_to_mat3, imat = its inverse, difmat = imat @ postmat
            float ex = (float)(tail[b][0] - head[b][0]), ey = (float)(tail[b][1] - head[b][1]), ez = (float)(tail[b][2] - head[b][2]);
            float[] premat;
            if (NormalizeV3Blender(ref ex, ref ey, ref ez) == 0f && parent >= 0)
            {
                ex = (float)(tail[parent][0] - head[parent][0]); ey = (float)(tail[parent][1] - head[parent][1]); ez = (float)(tail[parent][2] - head[parent][2]);
                NormalizeV3Blender(ref ex, ref ey, ref ez);
                premat = VecRollToMat3Normalized(ex, ey, ez, roll[parent]);
            }
            else premat = VecRollToMat3Normalized(ex, ey, ez, roll[b]);
            var imat = InvertM3(premat);
            var postmat = new[] { arm[0], arm[1], arm[2], arm[4], arm[5], arm[6], arm[8], arm[9], arm[10] };   // copy_m3_m4: M[col][row], three columns
            var difmat = MulM3(imat, postmat);
            float boneRoll = -BlenderTrig.Atan2f(difmat[2 * 3 + 0], difmat[2 * 3 + 2]);
            arm = WhereIsBone(bh, bt, boneRoll, parent >= 0 ? armMat[parent] : null, parent >= 0 ? bLength[parent] : 0f, out float len1);
            armMat[b] = arm; bLength[b] = len1;
            foreach (int c in children[b]) Finalize(c, b);
        }
        foreach (int b in roots) Finalize(b, -1);

        // ---- the exporter: matrix_world of each joint and its inverse bind matrix
        var armaWorldRM = ToRowMajor(armatureWorld);
        foreach (int b in bones)
        {
            var ml = ToRowMajor(armMat[b]);   // bone.matrix_local as mathutils items
            r.MatrixLocal[b] = ml;
            var world = MatMulMathutils(MatMulMathutils(armaWorldRM, ml), AxisBasisChange);
            var cm = new float[16]; for (int col = 0; col < 4; col++) for (int row = 0; row < 4; row++) cm[col * 4 + row] = world[row * 4 + col];
            r.World[b] = cm;
            var ibm = InvertedSafe(MatMulMathutils(AxisBasisChange, MatMulMathutils(armaWorldRM, ml)));
            var flat = new float[16];
            for (int col = 0; col < 4; col++) for (int row = 0; row < 4; row++) flat[col * 4 + row] = ibm[row * 4 + col];
            r.InverseBind[b] = flat;
        }
        return r;
    }

    /// <summary>The neutral bone the exporter adds (tree.py add_neutral_bones): its transform is the Z-up to Y-up basis
    /// change decomposed - no swizzle, no snapping -, its inverse bind matrix (basis @ armature.matrix_world).inverted_safe()
    /// written column by column. armatureWorld column-major as the probe holds it.</summary>
    internal static void NeutralBone(float[] armatureWorld, out float[] translation, out float[] rotation, out float[] scale, out float[] inverseBind)
    {
        DecomposeBlenderMatrix(AxisBasisChange, out var loc, out var quat, out var size);
        float Z(float v) => v == 0f ? 0f : v;
        translation = loc[0] != 0f || loc[1] != 0f || loc[2] != 0f ? new[] { Z(loc[0]), Z(loc[1]), Z(loc[2]) } : null;
        rotation = quat[0] != 1f || quat[1] != 0f || quat[2] != 0f || quat[3] != 0f ? new[] { Z(quat[1]), Z(quat[2]), Z(quat[3]), Z(quat[0]) } : null;
        scale = size[0] != 1f || size[1] != 1f || size[2] != 1f ? new[] { Z(size[0]), Z(size[1]), Z(size[2]) } : null;
        var ibm = InvertedSafe(MatMulMathutils(AxisBasisChange, ToRowMajor(armatureWorld)));
        inverseBind = new float[16];
        for (int col = 0; col < 4; col++) for (int row = 0; row < 4; row++) inverseBind[col * 4 + row] = ibm[row * 4 + col];
    }

    /// <summary>The armature object's scale as the importer set it: the node's scale (or a matrix's decomposed size) as
    /// float32; (1, 1, 1) for the dummy root's armature.</summary>
    internal static float[] ArmatureScale(HafModel m, int armatureNode)
    {
        if (armatureNode < 0) return new[] { 1f, 1f, 1f };
        var n = m.Nodes[armatureNode];
        float[] loc, quat, size;
        if (n.HasMatrix) DecomposeAsBlender(n.Matrix, out loc, out quat, out size); else ConvertTrs(n.Translation, n.Rotation, n.Scale, out loc, out quat, out size);
        return size;
    }

    /// <summary>pick_bind_pose for every bone of the set: from the inverse bind matrices where the bone and its parent have
    /// one (bind_local = inv_binds[parent] @ inv_binds[bone].inverted_safe(), decomposed), else the node's own TRS.</summary>
    static void BindTransRot(HafModel m, BlenderNames.Result names, HashSet<int> boneSet, out Dictionary<int, float[]> trans, out Dictionary<int, float[]> rot)
    {
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
        trans = new Dictionary<int, float[]>(); rot = new Dictionary<int, float[]>();
        foreach (int b in boneSet)
        {
            var n = m.Nodes[b];
            float[] loc, quat, size;
            if (n.HasMatrix) DecomposeAsBlender(n.Matrix, out loc, out quat, out size); else ConvertTrs(n.Translation, n.Rotation, n.Scale, out loc, out quat, out size);
            int parent = names.BoneParent[b];
            if (invBinds.ContainsKey(b) && invBinds.ContainsKey(parent))
            {
                var bindLocal = MatMulMathutils(invBinds[parent], InvertedSafe(invBinds[b]));
                DecomposeBlenderMatrix(bindLocal, out loc, out quat, out size);
            }
            trans[b] = loc; rot[b] = quat;
        }
    }

    // ---- mathutils
    /// <summary>Vector.length: sqrt (double) of the dot summed in double from float products.</summary>
    static double VecLength(float[] v) { double d = 0; for (int i = 0; i < 3; i++) d += (double)(float)(v[i] * v[i]); return Math.Sqrt(d); }

    /// <summary>mul_qt_qtqt (w, x, y, z), float32 left to right.</summary>
    static float[] MulQt(float[] a, float[] b)
    {
        float t0 = (float)((float)((float)((float)(a[0] * b[0]) - (float)(a[1] * b[1])) - (float)(a[2] * b[2])) - (float)(a[3] * b[3]));
        float t1 = (float)((float)((float)((float)(a[0] * b[1]) + (float)(a[1] * b[0])) + (float)(a[2] * b[3])) - (float)(a[3] * b[2]));
        float t2 = (float)((float)((float)((float)(a[0] * b[2]) + (float)(a[2] * b[0])) + (float)(a[3] * b[1])) - (float)(a[1] * b[3]));
        float t3 = (float)((float)((float)((float)(a[0] * b[3]) + (float)(a[3] * b[0])) + (float)(a[1] * b[2])) - (float)(a[2] * b[1]));
        return new[] { t0, t1, t2, t3 };
    }

    /// <summary>mul_qt_v3: the quaternion applied to a vector, float32 as written.</summary>
    static float[] MulQtV3(float[] q, float[] v)
    {
        float r0 = v[0], r1 = v[1], r2 = v[2];
        float t0 = (float)((float)((float)(-q[1] * r0) - (float)(q[2] * r1)) - (float)(q[3] * r2));
        float t1 = (float)((float)((float)(q[0] * r0) + (float)(q[2] * r2)) - (float)(q[3] * r1));
        float t2 = (float)((float)((float)(q[0] * r1) + (float)(q[3] * r0)) - (float)(q[1] * r2));
        r2 = (float)((float)((float)(q[0] * r2) + (float)(q[1] * r1)) - (float)(q[2] * r0));
        r0 = t1; r1 = t2;
        t1 = (float)((float)((float)((float)(t0 * -q[1]) + (float)(r0 * q[0])) - (float)(r1 * q[3])) + (float)(r2 * q[2]));
        t2 = (float)((float)((float)((float)(t0 * -q[2]) + (float)(r1 * q[0])) - (float)(r2 * q[1])) + (float)(r0 * q[3]));
        r2 = (float)((float)((float)((float)(t0 * -q[3]) + (float)(r2 * q[0])) - (float)(r0 * q[2])) + (float)(r1 * q[1]));
        return new[] { t1, t2, r2 };
    }

    /// <summary>mathutils Matrix @ Vector for a 4x4 and a 3-vector (w = 1): each row's products summed in double.</summary>
    static float[] MatVec(float[] it, float x, float y, float z)
    {
        var r = new float[3];
        for (int row = 0; row < 3; row++)
        {
            double d = 0.0;
            d += (double)(float)(it[row * 4 + 0] * x); d += (double)(float)(it[row * 4 + 1] * y); d += (double)(float)(it[row * 4 + 2] * z); d += (double)(float)(it[row * 4 + 3] * 1f);
            r[row] = (float)d;
        }
        return r;
    }

    // ---- Blender's C
    /// <summary>normalize_v3: the length, or 0 when the vector is (nearly) zero - then zeroed.</summary>
    static float NormalizeV3Blender(ref float x, ref float y, ref float z)
    {
        float d = (float)((float)((float)(x * x) + (float)(y * y)) + (float)(z * z));
        if (d > 1.0e-35f) { d = Sqrtf(d); float f = (float)(1.0f / d); x = (float)(x * f); y = (float)(y * f); z = (float)(z * f); return d; }
        x = y = z = 0f; return 0f;
    }

    /// <summary>mul_mat3_m4_v3: the 3x3 of a Blender float[4][4] (M[col][row], 16 floats) applied in place.</summary>
    static void MulMat3M4V3(float[] mat, float[] v)
    {
        float x = v[0], y = v[1], z = v[2];
        v[0] = (float)((float)((float)(x * mat[0]) + (float)(y * mat[4])) + (float)(mat[8] * z));
        v[1] = (float)((float)((float)(x * mat[1]) + (float)(y * mat[5])) + (float)(mat[9] * z));
        v[2] = (float)((float)((float)(x * mat[2]) + (float)(y * mat[6])) + (float)(mat[10] * z));
    }

    /// <summary>vec_roll_to_mat3_normalized; the matrix as Blender's float[3][3] (M[col][row], 9 floats).</summary>
    static float[] VecRollToMat3Normalized(float x, float y, float z, float roll)
    {
        const float SAFE_THRESHOLD = 6.1e-3f, CRITICAL_THRESHOLD = 2.5e-4f, THRESHOLD_SQUARED = CRITICAL_THRESHOLD * CRITICAL_THRESHOLD;
        float theta = (float)(1.0f + y);
        float thetaAlt = (float)((float)(x * x) + (float)(z * z));
        var b = new float[9];
        if (theta > SAFE_THRESHOLD || thetaAlt > THRESHOLD_SQUARED)
        {
            b[0 * 3 + 1] = -x; b[1 * 3 + 0] = x; b[1 * 3 + 1] = y; b[1 * 3 + 2] = z; b[2 * 3 + 1] = -z;
            if (theta <= SAFE_THRESHOLD) theta = (float)((float)(thetaAlt * 0.5f) + (float)((float)(thetaAlt * thetaAlt) * 0.125f));
            b[0 * 3 + 0] = (float)(1f - (float)((float)(x * x) / theta));
            b[2 * 3 + 2] = (float)(1f - (float)((float)(z * z) / theta));
            b[2 * 3 + 0] = b[0 * 3 + 2] = (float)((float)(-x * z) / theta);
        }
        else { b[0] = b[4] = -1f; b[8] = 1f; }
        var rm = AxisAngleNormalizedToMat3(x, y, z, roll);
        return MulM3(rm, b);
    }

    /// <summary>axis_angle_normalized_to_mat3 with the C runtime's sinf and cosf; M[col][row].</summary>
    static float[] AxisAngleNormalizedToMat3(float ax, float ay, float az, float angle)
    {
        float s = BlenderTrig.Sinf(angle), c = BlenderTrig.Cosf(angle);
        float ico = (float)(1.0f - c);
        float n0 = (float)(ax * s), n1 = (float)(ay * s), n2 = (float)(az * s);
        float n00 = (float)((float)(ax * ax) * ico), n01 = (float)((float)(ax * ay) * ico), n11 = (float)((float)(ay * ay) * ico), n02 = (float)((float)(ax * az) * ico), n12 = (float)((float)(ay * az) * ico), n22 = (float)((float)(az * az) * ico);
        var m = new float[9];
        m[0 * 3 + 0] = (float)(n00 + c); m[0 * 3 + 1] = (float)(n01 + n2); m[0 * 3 + 2] = (float)(n02 - n1);
        m[1 * 3 + 0] = (float)(n01 - n2); m[1 * 3 + 1] = (float)(n11 + c); m[1 * 3 + 2] = (float)(n12 + n0);
        m[2 * 3 + 0] = (float)(n02 + n1); m[2 * 3 + 1] = (float)(n12 - n0); m[2 * 3 + 2] = (float)(n22 + c);
        return m;
    }

    /// <summary>mul_m3_m3m3(R, A, B): R[i][j] = B[i][0] A[0][j] + B[i][1] A[1][j] + B[i][2] A[2][j], left to right; M[col][row].</summary>
    static float[] MulM3(float[] A, float[] B)
    {
        var R = new float[9];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
            R[i * 3 + j] = (float)((float)((float)(B[i * 3 + 0] * A[0 * 3 + j]) + (float)(B[i * 3 + 1] * A[1 * 3 + j])) + (float)(B[i * 3 + 2] * A[2 * 3 + j]));
        return R;
    }

    /// <summary>invert_m3_m3: the adjoint, the determinant (determinant_m3_array), each entry times 1 / det; M[col][row].</summary>
    static float[] InvertM3(float[] m)
    {
        float m00 = m[0], m01 = m[1], m02 = m[2], m10 = m[3], m11 = m[4], m12 = m[5], m20 = m[6], m21 = m[7], m22 = m[8];
        var r = new float[9];
        r[0] = (float)((float)(m11 * m22) - (float)(m12 * m21)); r[1] = (float)((float)(-m01 * m22) + (float)(m02 * m21)); r[2] = (float)((float)(m01 * m12) - (float)(m02 * m11));
        r[3] = (float)((float)(-m10 * m22) + (float)(m12 * m20)); r[4] = (float)((float)(m00 * m22) - (float)(m02 * m20)); r[5] = (float)((float)(-m00 * m12) + (float)(m02 * m10));
        r[6] = (float)((float)(m10 * m21) - (float)(m11 * m20)); r[7] = (float)((float)(-m00 * m21) + (float)(m01 * m20)); r[8] = (float)((float)(m00 * m11) - (float)(m01 * m10));
        float a = (float)(m00 * (float)((float)(m11 * m22) - (float)(m12 * m21)));
        float b = (float)(m10 * (float)((float)(m01 * m22) - (float)(m02 * m21)));
        float c = (float)(m20 * (float)((float)(m01 * m12) - (float)(m02 * m11)));
        float det = (float)((float)(a - b) + c);
        if (det != 0f) { det = (float)(1.0f / det); for (int i = 0; i < 9; i++) r[i] = (float)(r[i] * det); }
        return r;
    }

    /// <summary>BKE_armature_where_is_bone for one bone: head and tail in the parent's space, the roll, the parent's arm_mat
    /// (16 floats, M[col][row]) and length; returns arm_mat and the bone's length.</summary>
    static float[] WhereIsBone(float[] head, float[] tail, float roll, float[] parentArm, float parentLength, out float length)
    {
        float vx = (float)(tail[0] - head[0]), vy = (float)(tail[1] - head[1]), vz = (float)(tail[2] - head[2]);
        length = Sqrtf((float)((float)((float)(vx * vx) + (float)(vy * vy)) + (float)(vz * vz)));
        // vec_roll_to_mat3: normalize, then the normalized form
        float nx = vx, ny = vy, nz = vz; NormalizeV3Blender(ref nx, ref ny, ref nz);
        var boneMat = VecRollToMat3Normalized(nx, ny, nz, roll);
        var offs = new float[16];
        for (int col = 0; col < 3; col++) for (int row = 0; row < 3; row++) offs[col * 4 + row] = boneMat[col * 3 + row];
        offs[15] = 1f;
        offs[12] = head[0]; offs[13] = head[1]; offs[14] = head[2];
        if (parentArm == null) return offs;
        offs[13] = (float)(offs[13] + parentLength);   // offs_bone[3][1] += parent->length
        return MulM4(parentArm, offs);
    }

    /// <summary>ED_armature_ebone_roll_to_vector(bone, align_axis, false).</summary>
    static float RollToVector(float[] head, float[] tail, float[] alignAxis)
    {
        float nx = (float)(tail[0] - head[0]), ny = (float)(tail[1] - head[1]), nz = (float)(tail[2] - head[2]);
        if (NormalizeV3Blender(ref nx, ref ny, ref nz) <= 1.1920929e-7f) return 0f;
        float dot = (float)((float)((float)(alignAxis[0] * nx) + (float)(alignAxis[1] * ny)) + (float)(alignAxis[2] * nz));
        if (Math.Abs(dot) >= (float)(1.0f - 1.1920929e-7f)) return 0f;
        var mat = VecRollToMat3Normalized(nx, ny, nz, 0f);
        // project_v3_v3v3_normalized(vec, align_axis, nor); align_axis_proj = align_axis - vec
        float px = (float)(nx * dot), py = (float)(ny * dot), pz = (float)(nz * dot);
        float ax = (float)(alignAxis[0] - px), ay = (float)(alignAxis[1] - py), az = (float)(alignAxis[2] - pz);
        float zx = mat[2 * 3 + 0], zy = mat[2 * 3 + 1], zz = mat[2 * 3 + 2];   // mat[2]
        float roll = AngleV3V3(ax, ay, az, zx, zy, zz);
        // cross(vec, mat[2], align_axis_proj); dot with nor
        float cx = (float)((float)(zy * az) - (float)(zz * ay)), cy = (float)((float)(zz * ax) - (float)(zx * az)), cz = (float)((float)(zx * ay) - (float)(zy * ax));
        float d = (float)((float)((float)(cx * nx) + (float)(cy * ny)) + (float)(cz * nz));
        return d < 0f ? -roll : roll;
    }

    /// <summary>angle_v3v3: both normalized, then angle_normalized_v3v3 - 2 asinf(|a - b| / 2), or pi minus that of -b.</summary>
    static float AngleV3V3(float ax, float ay, float az, float bx, float by, float bz)
    {
        NormalizeV3Blender(ref ax, ref ay, ref az); NormalizeV3Blender(ref bx, ref by, ref bz);
        float dot = (float)((float)((float)(ax * bx) + (float)(ay * by)) + (float)(az * bz));
        if (dot >= 0f)
        {
            float dx = (float)(bx - ax), dy = (float)(by - ay), dz = (float)(bz - az);
            float len = Sqrtf((float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz)));
            return (float)(2.0f * SafeAsinf((float)(len / 2.0f)));
        }
        float ex = (float)(-bx - ax), ey = (float)(-by - ay), ez = (float)(-bz - az);
        float len2 = Sqrtf((float)((float)((float)(ex * ex) + (float)(ey * ey)) + (float)(ez * ez)));
        return (float)((float)Math.PI - (float)(2.0f * SafeAsinf((float)(len2 / 2.0f))));
    }

    static float SafeAsinf(float x) => BlenderTrig.Asinf(x < -1f ? -1f : x > 1f ? 1f : x);
}
