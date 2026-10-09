using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>VehicleProbe.BlenderPose: an imported armature's pose and what hangs from its bones. tools/deploy_drill.sh
/// holds whole files to Blender, bit for bit (pose bones, pose matrices, bone-parented objects); here the rules on a
/// rig small enough to read.</summary>
public class BlenderPoseTests
{
    /// <summary>Rig (node 0) > J0 (1) > J1 (2); a skinned mesh (3) that makes the armature; Hang (4) under J1.</summary>
    static HafModel Rig(out BlenderNames.Result names)
    {
        var m = new HafModel();
        HafNode N(string name, int parent, double[] t = null) { var n = new HafNode { Name = name, Parent = parent }; if (t != null) n.Translation = t; m.Nodes.Add(n); if (parent >= 0) m.Nodes[parent].Children.Add(m.Nodes.Count - 1); return n; }
        N("Rig", -1); N("J0", 0, new double[] { 0, 1, 0 }); N("J1", 1, new double[] { 0, 2, 0 });
        var body = N("Body", 0); N("Hang", 2, new double[] { 0.5, 0, 0 });
        var p = new HafPrimitive { VertexCount = 3, Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Joints = new ushort[12], Weights = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 } };
        var mesh = new HafMesh { Name = "body" }; mesh.Primitives.Add(p); m.Meshes.Add(mesh); body.Mesh = 0; body.Skin = 0;
        var q = new HafPrimitive { VertexCount = 3, Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 } };
        var hm = new HafMesh { Name = "hang" }; hm.Primitives.Add(q); m.Meshes.Add(hm); m.Nodes[4].Mesh = 1;
        var skin = new HafSkin(); skin.Joints = new[] { 1, 2 }; m.Skins.Add(skin);
        var sc = new HafScene(); sc.Nodes.Add(0); m.Scenes.Add(sc); m.Scene = 0;
        names = BlenderNames.Compute(m);
        return m;
    }

    [Fact]
    public void A_bone_at_rest_has_an_identity_pose_and_its_pose_matrix_is_its_rest_matrix()
    {
        var m = Rig(out var names);
        var rig = VehicleProbe.BuildImportRig(m, names);
        Assert.True(rig.IsBone(1)); Assert.True(rig.IsBone(2)); Assert.False(rig.IsBone(4));
        Assert.Equal(2, rig.ParentBone(4));
        foreach (int b in new[] { 1, 2 })
        {
            rig.StaticProperty(b, out var loc, out var quat, out var scale);
            // the node's transform IS the bind: the pose bone holds nothing (up to the rounding of the turn and back)
            Assert.All(loc, v => Assert.True(Math.Abs(v) < 1e-6f));
            Assert.Equal(1f, quat[0], 6); Assert.All(quat.Skip(1), v => Assert.True(Math.Abs(v) < 1e-6f));
            Assert.Equal(new[] { 1f, 1f, 1f }, scale);
        }
        VehicleProbe.BlenderWorldMatricesPosed(m, rig, null, out var pose, out _);
        var arm = rig.ArmatureOfBone[1];
        for (int i = 0; i < 16; i++) { Assert.Equal(arm.ArmMat[1][i], pose[1][i], 6); Assert.Equal(arm.ArmMat[2][i], pose[2][i], 6); }
    }

    [Fact]
    public void An_object_under_a_bone_is_moved_back_by_the_bones_length_and_sits_where_the_file_puts_it()
    {
        var m = Rig(out var names);
        var rig = VehicleProbe.BuildImportRig(m, names);
        rig.StaticProperty(4, out var loc, out _, out _);
        // J1 has no bone child: it takes its parent's length, 2 (the distance J0 > J1). The object's own (0.5, 0, 0) is turned
        // into the bone's frame and moved back along the bone's Y by that length
        Assert.Equal(-2f, loc[1], 5);
        var world = VehicleProbe.BlenderWorldMatricesPosed(m, rig, null, out _, out _);
        // glTF (0.5, 3, 0) is Blender (0.5, 0, 3): the bone's turn, its length and the move back cancel
        Assert.Equal(0.5f, world[4][12], 5); Assert.Equal(0f, world[4][13], 5); Assert.Equal(3f, world[4][14], 5);
    }

    [Fact]
    public void An_armature_with_a_zero_scale_is_left_to_Blender_whose_importer_fails_on_it()
    {
        var m = Rig(out _);
        m.Nodes[0].Scale = new double[] { 1, 0, 1 };
        Assert.Contains("zero scale", BlenderDeploy.Decide(m, "0|24|||||||0|||4|0|1".Split('|')).Fallback);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1e-50)]
    public void A_matrix_armatures_effective_zero_scale_is_left_to_Blender(double axis)
    {
        var m = Rig(out _);
        m.Nodes[0].Matrix = new double[] { 1, 0, 0, 0, 0, axis, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        Assert.Contains("zero scale", BlenderDeploy.Decide(m, "0|24|||||||0|||4|0|1".Split('|')).Fallback);
    }

    [Fact]
    public void A_TRS_armature_scale_that_underflows_float_is_left_to_Blender()
    {
        var m = Rig(out _);
        m.Nodes[0].Scale = new double[] { 1, 1e-50, 1 };
        Assert.Contains("zero scale", BlenderDeploy.Decide(m, "0|24|||||||0|||4|0|1".Split('|')).Fallback);
    }

    [Fact]
    public void ob_parbone_moves_the_pose_matrix_to_the_bones_tail_along_its_Y_axis()
    {
        var pose = new float[] { 1, 0, 0, 0, 0, 0, 2, 0, 0, -1, 0, 0, 5, 6, 7, 1 };   // column-major: Y axis is (0, 0, 2)
        var r = VehicleProbe.ParBone(pose, 1.5f);
        Assert.Equal(new[] { 5f, 6f, 10f }, new[] { r[12], r[13], r[14] });
        Assert.Equal(pose.Take(12), r.Take(12));
    }
}
