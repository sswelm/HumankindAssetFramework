using System;
using Xunit;

// The node hierarchy's transforms (editor/HafTransforms.cs): glTF conventions, locked against known rotations, so a
// preview or a probe that looks wrong cannot blame the matrix chain.
public class HafTransformsTests
{
    static double[] Q(double axisX, double axisY, double axisZ, double degrees)
    {
        double h = degrees * Math.PI / 360.0, s = Math.Sin(h);
        return new[] { axisX * s, axisY * s, axisZ * s, Math.Cos(h) };
    }
    static void Near(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], 6);
    }

    [Fact]
    public void A_quarter_turn_about_Y_takes_X_to_minus_Z_in_a_right_handed_frame()
    {
        var m = HafTransforms.Trs(new double[] { 0, 0, 0 }, Q(0, 1, 0, 90), new double[] { 1, 1, 1 });
        Near(new[] { 0, 0, -1.0 }, HafTransforms.Apply(m, 1, 0, 0, 1));
        Near(new[] { 1, 0, 0.0 }, HafTransforms.Apply(m, 0, 0, 1, 1));
        Near(new[] { 0, 1, 0.0 }, HafTransforms.Apply(m, 0, 1, 0, 1));   // the axis is fixed
    }

    [Fact]
    public void Translation_scale_and_rotation_compose_as_T_R_S()
    {
        // a point at (1,0,0): scaled by 2 -> (2,0,0), rotated 90° about Z -> (0,2,0), moved by (10,20,30) -> (10,22,30)
        var m = HafTransforms.Trs(new double[] { 10, 20, 30 }, Q(0, 0, 1, 90), new double[] { 2, 2, 2 });
        Near(new[] { 10, 22, 30.0 }, HafTransforms.Apply(m, 1, 0, 0, 1));
        Near(new[] { 0, 2, 0.0 }, HafTransforms.Apply(m, 1, 0, 0, 0));    // a direction ignores the translation
        Assert.Equal(1.0, m[15]); Assert.Equal(10.0, m[12]);              // column-major: the translation is the last column
    }

    [Fact]
    public void A_child_inherits_its_parents_world_transform()
    {
        var model = new HafModel();
        var parent = new HafNode { Name = "hull", Translation = new double[] { 5, 0, 0 }, Rotation = Q(0, 1, 0, 90) };
        var child = new HafNode { Name = "turret", Parent = 0, Translation = new double[] { 1, 0, 0 } };
        parent.Children.Add(1);
        model.Nodes.Add(parent); model.Nodes.Add(child); model.Roots.Add(0);
        var world = HafTransforms.WorldMatrices(model);
        // the child's origin: its (1,0,0) rotated by the parent's quarter turn -> (0,0,-1), then the parent's offset
        Near(new[] { 5, 0, -1.0 }, HafTransforms.Apply(world[1], 0, 0, 0, 1));
        Near(new[] { 5, 0, 0.0 }, HafTransforms.Apply(world[0], 0, 0, 0, 1));
        // a node with a matrix uses it as given
        var fixedNode = new HafNode { Name = "fixed", Matrix = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 7, 8, 9, 1 } };
        model.Nodes.Add(fixedNode); model.Roots.Add(2);
        Near(new[] { 7, 8, 9.0 }, HafTransforms.Apply(HafTransforms.WorldMatrices(model)[2], 0, 0, 0, 1));
    }

    [Fact]
    public void Normals_survive_non_uniform_scale_and_mirrors()
    {
        // a surface tilted 45° in XY, scaled x4 along X: the surface flattens toward the XZ plane and its normal tips
        // toward Y - the inverse transpose gives that; the plain matrix would tip it the wrong way
        var m = HafTransforms.Trs(new double[] { 0, 0, 0 }, new double[] { 0, 0, 0, 1 }, new double[] { 4, 1, 1 });
        double r = Math.Sqrt(0.5);
        var n = HafTransforms.ApplyNormal(m, r, r, 0);
        Assert.True(n[1] > n[0], $"expected the normal to tip toward Y, got ({n[0]:0.###},{n[1]:0.###})");
        Assert.Equal(1.0, Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]), 6);
        // a mirror in X: an outward +X normal becomes an outward -X normal; the determinant says the node mirrors
        var mirror = HafTransforms.Trs(new double[] { 0, 0, 0 }, new double[] { 0, 0, 0, 1 }, new double[] { -1, 1, 1 });
        Near(new[] { -1, 0, 0.0 }, HafTransforms.ApplyNormal(mirror, 1, 0, 0));
        Assert.True(HafTransforms.Determinant3(mirror) < 0);
        Assert.True(HafTransforms.Determinant3(m) > 0);
    }

    [Fact]
    public void Mul_is_matrix_multiplication_in_column_major_order()
    {
        var t = HafTransforms.Trs(new double[] { 1, 2, 3 }, new double[] { 0, 0, 0, 1 }, new double[] { 1, 1, 1 });
        var s = HafTransforms.Trs(new double[] { 0, 0, 0 }, new double[] { 0, 0, 0, 1 }, new double[] { 2, 2, 2 });
        Near(new[] { 3, 4, 5.0 }, HafTransforms.Apply(HafTransforms.Mul(t, s), 1, 1, 1, 1));   // scale first, then translate
        Near(new[] { 4, 6, 8.0 }, HafTransforms.Apply(HafTransforms.Mul(s, t), 1, 1, 1, 1));   // translate first, then scale
        Near(HafTransforms.Identity, HafTransforms.Mul(HafTransforms.Identity, HafTransforms.Identity));
    }
}
