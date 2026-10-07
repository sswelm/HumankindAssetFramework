using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>A skin's bones as Blender's importer builds them and its exporter writes them (VehicleProbe.BlenderArmature,
/// BlenderExportTree): the export_skin fixture's rig against the edit bones, `bone.matrix_local` and the written joints
/// read off Blender 5.1.2 on 2026-10-07, bit for bit. tools/prep_drill.sh holds every skin of the population the same way.</summary>
public class BlenderArmatureTests
{
    static uint U(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
    static uint[] Hex(string s) => s.Split(' ').Select(h => Convert.ToUInt32(h, 16)).ToArray();

    /// <summary>The export_skin fixture: an armature node turned (a unit quaternion), scaled (1.5, 0.75, 1.25) and moved;
    /// Root at (0, 0.1, 0) under it, Tip at (1, 0.2, 0) under Root; the skin names both joints, without inverse bind
    /// matrices; the skinned mesh is a child of the armature node.</summary>
    static HafModel Rig()
    {
        var m = new HafModel();
        var mesh = new HafMesh { Name = "body" };
        var p = new HafPrimitive { VertexCount = 3, Positions = new float[9], Indices = new[] { 0, 1, 2 }, Joints = new ushort[12], Weights = new float[12] };
        for (int v = 0; v < 3; v++) p.Weights[4 * v] = 1f;
        mesh.Primitives.Add(p); m.Meshes.Add(mesh);
        var rig = new HafNode { Name = "Rig", Translation = new double[] { 2, 0.5, -1 }, Rotation = new double[] { 0.2886751, 0.2886751, 0.2886751, 0.8660254 }, Scale = new double[] { 1.5, 0.75, 1.25 } };
        rig.Children.AddRange(new[] { 1, 2 });
        var body = new HafNode { Name = "Body", Mesh = 0, Skin = 0, Parent = 0 };
        var root = new HafNode { Name = "Root", Translation = new double[] { 0, 0.1, 0 }, Parent = 0 }; root.Children.Add(3);
        var tip = new HafNode { Name = "Tip", Translation = new double[] { 1, 0.2, 0 }, Parent = 2 };
        m.Nodes.AddRange(new[] { rig, body, root, tip });
        m.Skins.Add(new HafSkin { Name = "rig", Joints = new[] { 2, 3 } });
        var sc = new HafScene(); sc.Nodes.Add(0); m.Scenes.Add(sc); m.Scene = 0;
        return m;
    }

    [Fact]
    public void The_edit_bones_and_matrix_local_equal_Blenders_bit_for_bit()
    {
        var m = Rig(); var names = BlenderNames.Compute(m); var bw = VehicleProbe.BlenderWorldMatrices(m, null);
        Assert.Equal(new[] { U(1.5f), U(1.25f), U(0.75f) }, VehicleProbe.ArmatureScale(m, 0).Select(U));   // the importer's frame: (x, z, y)
        var a = VehicleProbe.BlenderArmature(m, names, 0, bw[0], VehicleProbe.ArmatureScale(m, 0));
        // EB Root head 0 0 0.1 tail 0 2.3e-8 0.78 roll 3.4e-8; EB Tip head 0.99999994 1.7e-8 0.3 tail ... (Blender's edit mode)
        Assert.Equal(Hex("00000000 00000000 3DCCCCCD"), a.EditHead[2].Select(U)); Assert.Equal(Hex("00000000 32C7E555 3F47A584"), a.EditTail[2].Select(U)); Assert.Equal(0x331302AFu, U(a.EditRoll[2]));
        Assert.Equal(Hex("3F7FFFFF 31EB377D 3E999999"), a.EditHead[3].Select(U)); Assert.Equal(Hex("3F7FFFFF 3301599A 3F7AD8B6"), a.EditTail[3].Select(U)); Assert.Equal(0x331302AFu, U(a.EditRoll[3]));
        // BONE matrix_local, column by column (Blender's own after leaving edit mode: the roll correction through Eigen's
        // inverse, mul_mat3_m4_v3, where_is_bone twice and atan2f)
        uint[] ML(int b) => Enumerable.Range(0, 16).Select(i => U(a.MatrixLocal[b][(i % 4) * 4 + i / 4])).ToArray();
        Assert.Equal(Hex("3F800000 331302AF A6A8D82A 00000000 00000000 331302AF 3F800000 00000000 331302AF BF800000 00000000 00000000 00000000 00000000 3DCCCCCD 3F800000"), ML(2));
        Assert.Equal(Hex("3F800000 331302AF A6A8D82A 00000000 00000000 331302AF 3F800000 00000000 331302AF BF800000 00000000 00000000 3F7FFFFF 31EB3778 3E999998 3F800000"), ML(3));
    }

    [Fact]
    public void A_joints_transform_keeps_its_float_noise_and_the_neutral_bone_is_the_basis_change()
    {
        // the written joints of the fixture: Root t (7.4505806e-08, 0.0999998152, 4.47034836e-08) - no snapping, no
        // normalization for a joint (joints.py), a -0.0 written as 0 (__fix_json)
        var m = Rig(); var names = BlenderNames.Compute(m);
        var t = BlenderExportTree.Build(m, names, VehicleProbe.BlenderWorldMatrices(m, null));
        var root = t.Nodes.Single(n => n.Name == "Root"); var tip = t.Nodes.Single(n => n.Name == "Tip");
        Assert.True(root.TransformKnown && tip.TransformKnown);
        Assert.Equal(new[] { 7.4505806e-08f, 0.0999998152f, 4.47034836e-08f }.Select(U), root.Translation.Select(U));
        Assert.Equal(new[] { -1.86264515e-08f, 1.11758709e-08f, -5.58793545e-09f, 1f }.Select(U), root.Rotation.Select(U));
        Assert.Equal(new[] { 1f, 0.19999969f, -5.96046448e-08f }.Select(U), tip.Translation.Select(U));
        Assert.Equal(new[] { 1.49011612e-08f, -3.7252903e-09f, -7.4505806e-09f, 1f }.Select(U), tip.Rotation.Select(U));
        Assert.All(t.Nodes.Where(n => n.BoneNode >= 0), n => Assert.NotNull(n.InverseBind));
        // the neutral bone: the basis change decomposed, nothing swizzled or snapped
        var tn = BlenderExportTree.Build(m, names, VehicleProbe.BlenderWorldMatrices(m, null), null, new HashSet<int> { 0 });
        var neutral = tn.Nodes.Single(n => n.NeutralBone);
        Assert.Null(neutral.Translation); Assert.Null(neutral.Scale);
        Assert.Equal(new[] { U(-0.70710677f), U(0f), U(0f), U(0.70710677f) }, neutral.Rotation.Select(U));
        Assert.NotNull(neutral.InverseBind);
        // an object node snaps and normalizes; a -0.0 is written as 0 either way
        VehicleProbe.ExporterTrs(null, new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }, out var tr, out var ro, out var sc, joint: true);
        Assert.Null(tr); Assert.Null(ro); Assert.Null(sc);
    }
}
