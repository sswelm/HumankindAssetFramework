// VehicleProbe.BlenderPose.cs - an imported armature's POSE as Blender evaluates it, and what hangs from its bones
// (replacing deploy_convert.py, part 2b, 2026-10-10). The dugout canoe needs it: ten of its objects are parented to
// bones the animation moves.
//
// The importer (io_scene_gltf2 blender/imp/vnode.py, node.py, animation_node.py):
//   - prettify_bones (the default BLENDER heuristic) turns EVERY bone's edit rotation by (sqrt(1/2), sqrt(1/2), 0, 0)
//     and cancels it in the node's own transform: the bone's vnode gets that turn as `rotation_before`, every child of
//     it - bone or object - the conjugate as `rotation_after`. A vnode's final transform is
//     (rotation_after @ t, rotation_after @ r @ rotation_before, m @ s), m the axis swap of rotation_before;
//   - a pose bone is set to make edit bone x pose bone = that transform: location = er^-1 @ (t - et),
//     rotation_quaternion = er^-1 @ r, scale = s (et, er the bone's edit translation and rotation);
//   - an object under a bone is parented to the BONE and moved back from the tip to the root:
//     location += (0, -bone_length, 0);
//   - the animation's keys go through the same steps, key by key (BlenderPosedState.Import takes them from here).
// Blender (armature.cc): BKE_pchan_to_mat4 (the normalized quaternion's matrix times the scale, the location);
// BKE_armature_mat_bone_to_pose - for a root bone arm_mat @ chan_mat, for a child (parent's pose_mat @ offs_bone) @
// chan_mat, offs_bone the bone's own matrix at its head with the parent's length added along Y - the location column
// taken separately through the same matrix (mul_v3_m4v3); object.cc ob_parbone: the bone's pose matrix moved to its
// TAIL (the Y axis times bone.length). Held by tools/deploy_drill.sh: every pose bone's location, rotation, scale and
// pose matrix and every bone-parented object's matrix_world, as bits.
using System;
using System.Collections.Generic;
using System.Linq;

public static partial class VehicleProbe
{
    /// <summary>What the importer made of a file's armatures: per bone node its armature's rest (BlenderArmature), and
    /// per node the two rotations prettify_bones left on its vnode.</summary>
    public sealed class ImportRig
    {
        public readonly Dictionary<int, ArmatureResult> ArmatureOfBone = new Dictionary<int, ArmatureResult>();   // by bone node
        public readonly Dictionary<int, ArmatureResult> Armatures = new Dictionary<int, ArmatureResult>();        // by armature node (-1: the dummy root's)
        public HafModel Model; public BlenderNames.Result Names;
        public static readonly float[] Turn = { (float)(Math.Sqrt(2) / 2), (float)(Math.Sqrt(2) / 2), 0f, 0f };
        static readonly float[] TurnInv = { Turn[0], -Turn[1], -Turn[2], -Turn[3] };
        static readonly float[] One = { 1f, 0f, 0f, 0f };

        public bool IsBone(int node) => node >= 0 && Names.IsBone[node];
        /// <summary>The bone a node's object hangs from (its parent in the file is a bone), or -1.</summary>
        public int ParentBone(int node) { int p = Model.Nodes[node].Parent; return p >= 0 && Names.IsBone[p] ? p : -1; }

        /// <summary>vnode.trs(): the node's transform in Blender's convention through its vnode's rotations.</summary>
        public void FinalTrs(int node, float[] loc, float[] quat, float[] scale, out float[] t, out float[] r, out float[] s)
        {
            float[] after = ParentBone(node) >= 0 ? TurnInv : One, before = IsBone(node) ? Turn : One;
            t = MulQtV3(after, loc);
            r = MulQt(MulQt(after, quat), before);
            // scale_rot_swap_matrix(rotation_before) @ s: the turn swaps Y and Z; mathutils sums each row in a double
            s = IsBone(node) ? new[] { Row(scale[0], scale[1], scale[2], 0), Row(scale[0], scale[2], scale[1], 1), Row(scale[0], scale[2], scale[1], 2) }
                             : new[] { Row(scale[0], scale[1], scale[2], 0), Row(scale[0], scale[1], scale[2], 1), Row(scale[0], scale[1], scale[2], 2) };
        }

        static float Row(float x, float y, float z, int row)
        {
            double dot = 0.0;
            dot += (double)(float)((row == 0 ? 1f : 0f) * x); dot += (double)(float)((row == 1 ? 1f : 0f) * y); dot += (double)(float)((row == 2 ? 1f : 0f) * z);
            return (float)dot;
        }

        /// <summary>What the property holds for a final transform: a pose bone's location, rotation_quaternion and scale
        /// (relative to its edit bone), or an object's - moved back by its parent bone's length when it hangs from one.</summary>
        public void Property(int node, float[] t, float[] r, float[] s, out float[] loc, out float[] quat, out float[] scale)
        {
            if (IsBone(node))
            {
                var arm = ArmatureOfBone[node];
                float[] et = arm.EditTrans[node], er = arm.EditRot[node], inv = { er[0], -er[1], -er[2], -er[3] };
                loc = MulQtV3(inv, new[] { (float)(t[0] - et[0]), (float)(t[1] - et[1]), (float)(t[2] - et[2]) });
                quat = MulQt(inv, r); scale = s;
                return;
            }
            loc = t; quat = r; scale = s;
            int pb = ParentBone(node);
            if (pb >= 0)
            {
                // Vector((0, -bone_length, 0)): the double negated, stored float32; Vector + Vector in float32
                float off = (float)(-ArmatureOfBone[pb].PyLength[pb]);
                loc = new[] { (float)(t[0] + 0f), (float)(t[1] + off), (float)(t[2] + 0f) };
            }
        }

        /// <summary>One key of a channel, as the importer converts it (animation_node.py do_channel): each path on its own.</summary>
        public float[] KeyLocation(int node, float[] loc)
        {
            FinalTrs(node, loc, One, new[] { 1f, 1f, 1f }, out var t, out _, out _);
            if (IsBone(node))
            {
                var arm = ArmatureOfBone[node];
                float[] et = arm.EditTrans[node], er = arm.EditRot[node], inv = { er[0], -er[1], -er[2], -er[3] };
                return MulQtV3(inv, new[] { (float)(t[0] - et[0]), (float)(t[1] - et[1]), (float)(t[2] - et[2]) });
            }
            int pb = ParentBone(node);
            if (pb < 0) return t;
            float off = (float)(-ArmatureOfBone[pb].PyLength[pb]);
            return new[] { (float)(t[0] + 0f), (float)(t[1] + off), (float)(t[2] + 0f) };
        }

        public float[] KeyRotation(int node, float[] quat)
        {
            FinalTrs(node, new float[3], quat, new[] { 1f, 1f, 1f }, out _, out var r, out _);
            if (!IsBone(node)) return r;
            var er = ArmatureOfBone[node].EditRot[node];
            return MulQt(new[] { er[0], -er[1], -er[2], -er[3] }, r);
        }

        public float[] KeyScale(int node, float[] scale)
        {
            FinalTrs(node, new float[3], One, scale, out _, out _, out var s);
            return s;
        }

        /// <summary>The importer's static transform of a node as its property holds it.</summary>
        public void StaticProperty(int node, out float[] loc, out float[] quat, out float[] scale)
        {
            BlenderTrs(Model.Nodes[node], out var l, out var q, out var sc);
            FinalTrs(node, l, q, sc, out var t, out var r, out var s);
            Property(node, t, r, s, out loc, out quat, out scale);
        }
    }

    internal static ImportRig BuildImportRig(HafModel m, BlenderNames.Result names)
    {
        var rig = new ImportRig { Model = m, Names = names };
        var world = BlenderWorldMatrices(m, null);
        foreach (int armNode in names.BoneNodesInOrder.Select(b => names.ArmatureNodeOfBone[b]).Distinct())
        {
            var arm = BlenderArmature(m, names, armNode, armNode >= 0 ? world[armNode] : IdentityF(), ArmatureScale(m, armNode));
            rig.Armatures[armNode] = arm;
            foreach (int b in arm.Bones) rig.ArmatureOfBone[b] = arm;
        }
        return rig;
    }

    /// <summary>BKE_pose_where_is for one armature: every bone's pose matrix (armature space, column-major) from its pose
    /// bone's location, quaternion and scale.</summary>
    internal static Dictionary<int, float[]> PoseMatrices(ArmatureResult arm, Func<int, float[][]> poseTrs)
    {
        var pose = new Dictionary<int, float[]>();
        foreach (int b in arm.Bones)   // creation order: a parent before its children
        {
            var p = poseTrs(b);
            var chan = ObjectMatrix(p[0], p[1], p[2], pchan: true);
            int parent = arm.Parent[b];
            // BKE_bone_parent_transform_calc_from_matrices: full transform; rotscale_mat and loc_mat are the same matrix
            var rotscale = parent >= 0 ? MulM4(pose[parent], arm.OffsBone[b]) : arm.ArmMat[b];
            var outm = MulM4(rotscale, chan);
            // mul_v3_m4v3(outmat[3], loc_mat, tmploc): x*m[0] + y*m[1] + z*m[2] + m[3], left to right
            float x = chan[12], y = chan[13], z = chan[14];
            for (int k = 0; k < 3; k++)
                outm[12 + k] = (float)((float)((float)((float)(x * rotscale[k]) + (float)(y * rotscale[4 + k])) + (float)(z * rotscale[8 + k])) + rotscale[12 + k]);
            // rescale_m4 by (1, 1, 1): mul_v3_fl on the three axes
            for (int k = 0; k < 12; k++) if (k % 4 != 3) outm[k] = (float)(outm[k] * 1f);
            pose[b] = outm;
        }
        return pose;
    }

    /// <summary>Every node's matrix_world with the armatures POSED: as BlenderWorldMatrices, and an object that hangs
    /// from a bone under that bone's pose (the armature's matrix, ob_parbone, its own transform) with what hangs below
    /// it. `animated` gives a node's evaluated properties (null for a node or a path nothing animates) - a bone node's
    /// are its pose bone's. poseOut: per bone node its pose matrix (armature space, column-major); propsOut: per bone
    /// node the pose bone's location, quaternion and scale.</summary>
    internal static float[][] BlenderWorldMatricesPosed(HafModel m, ImportRig rig, Func<int, float[][]> animated, out Dictionary<int, float[]> poseOut, out Dictionary<int, float[][]> propsOut)
    {
        float[][] Props(int n)
        {
            var p = animated?.Invoke(n);
            if (!rig.IsBone(n) && rig.ParentBone(n) < 0) return p;
            rig.StaticProperty(n, out var l, out var q, out var s);
            return new[] { p?[0] ?? l, p?[1] ?? q, p?[2] ?? s };
        }
        var world = BlenderWorldMatrices(m, Props, out var local);
        var pose = new Dictionary<int, float[]>(); var props = new Dictionary<int, float[][]>();
        foreach (var arm in rig.Armatures.Values)
        {
            foreach (int b in arm.Bones) props[b] = Props(b);
            foreach (var kv in PoseMatrices(arm, b => props[b])) pose[kv.Key] = kv.Value;
        }
        // the objects under bones and what hangs below them - each after what it hangs from: an armature may itself hang
        // from another armature's bone (through an object), so the ARMATURE's matrix is resolved first, whatever the
        // nodes' indices (a nested armature read its unposed matrix here until the review of 2026-10-10)
        var affected = new bool[m.Nodes.Count]; var done = new bool[m.Nodes.Count];
        void Mark(int n, bool under) { under = under || (!rig.IsBone(n) && rig.ParentBone(n) >= 0); affected[n] = under && !rig.IsBone(n); foreach (int c in m.Nodes[n].Children) Mark(c, under); }
        for (int n = 0; n < m.Nodes.Count; n++) if (m.Nodes[n].Parent < 0) Mark(n, false);
        void Resolve(int n)
        {
            if (!affected[n] || done[n]) return;
            done[n] = true;
            int pb = rig.ParentBone(n);
            if (pb >= 0)
            {
                int a = rig.Names.ArmatureNodeOfBone[pb];
                if (a >= 0) Resolve(a);
                var parentMat = MulM4(a >= 0 ? world[a] : IdentityF(), ParBone(pose[pb], rig.ArmatureOfBone[pb].Length[pb]));
                world[n] = MulM4(parentMat, local[n]);
            }
            else { int p = m.Nodes[n].Parent; Resolve(p); world[n] = MulM4(world[p], local[n]); }
        }
        for (int n = 0; n < m.Nodes.Count; n++) Resolve(n);
        poseOut = pose; propsOut = props;
        return world;
    }

    /// <summary>ob_parbone: the bone's pose matrix moved to its tail - the parent matrix of an object that hangs from it.</summary>
    internal static float[] ParBone(float[] poseMat, float boneLength)
    {
        var r = (float[])poseMat.Clone();
        for (int k = 0; k < 3; k++) r[12 + k] = (float)(r[12 + k] + (float)(r[4 + k] * boneLength));
        return r;
    }
}
