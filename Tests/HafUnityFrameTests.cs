using System;
using System.Linq;
using Xunit;

/// <summary>The preview frame the Clip Range picker's in-process rig lives in (HafUnityFrame): glTF conjugated by the X mirror.
/// What matters is that products survive - a hierarchy of mirrored local transforms, a skin's mirrored inverse bind matrices
/// and a mirrored vertex give Unity's skinning formula the mirrored world vertex the reader computes - so that is what these
/// hold, with the reader (HafTransforms) as the reference; Unity's own evaluation of the same matrices is the Bake Tests row.
/// And Blender's track names, which a clip spec carries.</summary>
public class HafUnityFrameTests
{
    static HafModel Link(HafModel m) { for (int i = 0; i < m.Nodes.Count; i++) foreach (var c in m.Nodes[i].Children) m.Nodes[c].Parent = i; return m; }
    static double[] Unit(double x, double y, double z, double w) { double l = Math.Sqrt(x * x + y * y + z * z + w * w); return new[] { x / l, y / l, z / l, w / l }; }
    static void Close(double[] a, double[] b, double tol = 1e-9) { for (int i = 0; i < 3; i++) Assert.InRange(a[i] - b[i], -tol, tol); }

    [Fact]
    public void A_rotation_under_the_mirror_moves_the_mirrored_point_where_the_mirror_of_the_moved_point_is()
    {
        var q = Unit(0.3, -0.5, 0.7, 0.4); double[] p = { 1.5, -2.25, 0.75 };
        var moved = HafTransforms.Apply(HafTransforms.Trs(new double[] { 0, 0, 0 }, q, new double[] { 1, 1, 1 }), p[0], p[1], p[2], 1);
        var mirroredMoved = HafTransforms.Apply(HafTransforms.Trs(new double[] { 0, 0, 0 }, HafUnityFrame.Rotation(q), new double[] { 1, 1, 1 }), -p[0], p[1], p[2], 1);
        Close(mirroredMoved, HafUnityFrame.Point(moved[0], moved[1], moved[2]));
    }

    [Fact]
    public void A_matrix_under_the_mirror_is_the_mirror_of_its_effect()
    {
        var M = HafTransforms.Trs(new double[] { 3, -1, 2 }, Unit(0.1, 0.2, -0.3, 0.9), new double[] { 2, 0.5, 1.5 });
        double[] p = { -0.4, 1.1, 2.2 };
        var expect = HafTransforms.Apply(M, p[0], p[1], p[2], 1);
        var got = HafTransforms.Apply(HafUnityFrame.Matrix(M), -p[0], p[1], p[2], 1);
        Close(got, HafUnityFrame.Point(expect[0], expect[1], expect[2]));
        // and a product of mirrored matrices is the mirrored product
        var N = HafTransforms.Trs(new double[] { -1, 0, 4 }, Unit(0.5, 0.5, 0.5, 0.5), new double[] { 1, 1, 1 });
        var mn = HafTransforms.Mul(HafUnityFrame.Matrix(M), HafUnityFrame.Matrix(N));
        Assert.Equal(HafUnityFrame.Matrix(HafTransforms.Mul(M, N)).Select(x => Math.Round(x, 9)), mn.Select(x => Math.Round(x, 9)));
    }

    [Fact]
    public void A_matrix_node_decomposes_to_the_mirrored_TRS_of_what_built_it()
    {
        var q = Unit(0.2, 0.6, -0.1, 0.75); double[] t = { 1, 2, 3 }, s = { 2, 3, 0.5 };
        var n = new HafNode { Matrix = HafTransforms.Trs(t, q, s) };
        HafUnityFrame.LocalTrs(n, out var t2, out var q2, out var s2);
        Close(t2, HafUnityFrame.Point(t[0], t[1], t[2]));
        Close(s2, s);
        // the quaternion up to its sign: compare the rotations
        var want = HafTransforms.Trs(new double[] { 0, 0, 0 }, HafUnityFrame.Rotation(q), new double[] { 1, 1, 1 });
        var got = HafTransforms.Trs(new double[] { 0, 0, 0 }, q2, new double[] { 1, 1, 1 });
        for (int i = 0; i < 16; i++) Assert.InRange(want[i] - got[i], -1e-9, 1e-9);
    }

    [Fact]
    public void Unitys_skinning_of_the_mirrored_rig_lands_on_the_readers_posed_vertex_mirrored()
    {
        // a root turned 30 degrees about Y and moved; two joints under it, each translated; a triangle skinned 0.3 / 0.7 between them
        var m = new HafModel();
        m.Nodes.Add(new HafNode { Name = "Root", Translation = new double[] { 1, 2, 3 }, Rotation = Unit(0, Math.Sin(Math.PI / 12), 0, Math.Cos(Math.PI / 12)) }); m.Nodes[0].Children.Add(1); m.Nodes[0].Children.Add(2);
        m.Nodes.Add(new HafNode { Name = "J1", Translation = new double[] { 0, 1, 0 }, Rotation = Unit(0.3, 0, 0, 0.95) });
        m.Nodes.Add(new HafNode { Name = "J2", Translation = new double[] { 0, 2, 0 }, Scale = new double[] { 1, 1.5, 1 } });
        m.Nodes.Add(new HafNode { Name = "Skin", Mesh = 0, Skin = 0 });
        Link(m);
        var world = HafTransforms.WorldMatrices(m, null);
        var ibm = new double[32];
        Array.Copy(HafTransforms.Invert(world[1]), 0, ibm, 0, 16); Array.Copy(HafTransforms.Invert(world[2]), 0, ibm, 16, 16);
        m.Skins.Add(new HafSkin { Joints = new[] { 1, 2 }, InverseBindMatrices = ibm });
        var prim = new HafPrimitive { VertexCount = 3, Positions = new float[] { 0.5f, 1, 0, 1, 1.2f, 0.3f, -0.2f, 2.5f, 0.1f }, Joints = new ushort[] { 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0 }, Weights = new float[] { 0.3f, 0.7f, 0, 0, 1, 0, 0, 0, 0.5f, 0.5f, 0, 0 } };
        var mesh = new HafMesh(); mesh.Primitives.Add(prim); m.Meshes.Add(mesh);
        var expect = HafTransforms.WorldPositions(m, 3, prim, world);
        // what the rig hands Unity: the joints' world matrices and the skin's inverse bind matrices, all mirrored
        var boneWorld = new[] { HafUnityFrame.Matrix(world[1]), HafUnityFrame.Matrix(world[2]) };
        var bindposes = new[] { HafUnityFrame.Matrix(ibm.Take(16).ToArray()), HafUnityFrame.Matrix(ibm.Skip(16).ToArray()) };
        for (int v = 0; v < 3; v++)
        {
            var got = HafUnityFrame.Skin(boneWorld, bindposes, new[] { (int)prim.Joints[v * 4], (int)prim.Joints[v * 4 + 1] }, new double[] { prim.Weights[v * 4], prim.Weights[v * 4 + 1] },
                                         HafUnityFrame.Point(prim.Positions[v * 3], prim.Positions[v * 3 + 1], prim.Positions[v * 3 + 2]));
            Close(got, HafUnityFrame.Point(expect[v * 3], expect[v * 3 + 1], expect[v * 3 + 2]), 1e-6);
        }
    }

    [Fact]
    public void Track_names_are_the_importers()
    {
        var m = new HafModel();
        m.Animations.Add(new HafAnimation { Name = "Walk" }); m.Animations.Add(new HafAnimation()); m.Animations.Add(new HafAnimation { Name = "Walk" }); m.Animations.Add(new HafAnimation { Name = "Walk" });
        m.Animations.Add(new HafAnimation { Name = new string('x', 70) }); m.Animations.Add(new HafAnimation { Name = new string('x', 70) });
        var names = BlenderNames.TrackNames(m);
        Assert.Equal(new[] { "Walk", "Anim_1", "Walk.001", "Walk.002", new string('x', 63), new string('x', 59) + ".001" }, names);
    }

    [Fact]
    public void Track_names_truncate_whole_Unicode_characters_at_Blenders_byte_limit()
    {
        string rocket = char.ConvertFromUtf32(0x1F680);
        string longName = string.Concat(Enumerable.Repeat(rocket, 40));
        var m = new HafModel();
        m.Animations.Add(new HafAnimation { Name = longName });
        m.Animations.Add(new HafAnimation { Name = longName });
        m.Animations.Add(new HafAnimation { Name = longName });
        var names = BlenderNames.TrackNames(m);
        Assert.Equal(new[] { string.Concat(Enumerable.Repeat(rocket, 15)),
            string.Concat(Enumerable.Repeat(rocket, 14)) + ".001",
            string.Concat(Enumerable.Repeat(rocket, 14)) + ".002" }, names);
        foreach (string name in names)
        {
            Assert.True(new System.Text.UTF8Encoding(false, true).GetByteCount(name) <= 63);
            Assert.False(char.IsHighSurrogate(name[name.Length - 1]));
        }
    }

    [Fact]
    public void Track_names_reserve_suffix_bytes_for_multibyte_names()
    {
        var m = new HafModel();
        m.Animations.Add(new HafAnimation { Name = new string('\u65E5', 40) });
        m.Animations.Add(new HafAnimation { Name = new string('\u65E5', 40) });
        Assert.Equal(new[] { new string('\u65E5', 21), new string('\u65E5', 19) + ".001" }, BlenderNames.TrackNames(m));
    }
}
