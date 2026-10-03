using System;
using System.Linq;
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
        model.Nodes.Add(parent); model.Nodes.Add(child);
        var world = HafTransforms.WorldMatrices(model);
        // the child's origin: its (1,0,0) rotated by the parent's quarter turn -> (0,0,-1), then the parent's offset
        Near(new[] { 5, 0, -1.0 }, HafTransforms.Apply(world[1], 0, 0, 0, 1));
        Near(new[] { 5, 0, 0.0 }, HafTransforms.Apply(world[0], 0, 0, 0, 1));
        // a node with a matrix uses it as given
        var fixedNode = new HafNode { Name = "fixed", Matrix = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 7, 8, 9, 1 } };
        model.Nodes.Add(fixedNode);
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

    [Fact]
    public void A_skinned_vertex_is_the_weighted_blend_of_its_joints_over_both_influence_sets()
    {
        // two joints: A at the origin, B moved by (10,0,0); identity inverse bind matrices, so each joint's matrix is its own
        // world. A vertex weighted 1 on B lands at +10; half-and-half lands at +5; a vertex whose second set carries the
        // other half is the same blend; an unweighted vertex stays where the file put it. The mesh node's transform is ignored.
        var model = new HafModel();
        model.Nodes.Add(new HafNode { Name = "A" });
        model.Nodes.Add(new HafNode { Name = "B", Translation = new double[] { 10, 0, 0 } });
        model.Nodes.Add(new HafNode { Name = "skinned", Mesh = 0, Skin = 0, Translation = new double[] { 999, 999, 999 } });
        var prim = new HafPrimitive
        {
            VertexCount = 4, Positions = new float[] { 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0 },
            Joints = new ushort[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            Weights = new float[] { 1, 0, 0, 0, 0.5f, 0.5f, 0, 0, 0.5f, 0, 0, 0, 0, 0, 0, 0 },
            Joints1 = new ushort[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 },
            Weights1 = new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0, 0, 0 },
            Normals = new float[] { 1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 1, 0 },
        };
        model.Meshes.Add(new HafMesh { Primitives = { prim } });
        model.Skins.Add(new HafSkin { Joints = new[] { 0, 1 } });
        var world = HafTransforms.WorldMatrices(model);
        var pos = HafTransforms.WorldPositions(model, 2, prim, world);
        Near(new[] { 11, 0, 0.0 }, new[] { pos[0], pos[1], pos[2] });    // all on B
        Near(new[] { 6, 0, 0.0 }, new[] { pos[3], pos[4], pos[5] });     // half A, half B
        Near(new[] { 6, 0, 0.0 }, new[] { pos[6], pos[7], pos[8] });     // half A (set 0), half B (set 1)
        Near(new[] { 1, 0, 0.0 }, new[] { pos[9], pos[10], pos[11] });   // unweighted: the file's position
        // weights that do not sum to 1 are normalized: 0.25 on B alone is still "all on B"
        prim.Weights[0] = 0.25f;
        Near(new[] { 11, 0, 0.0 }, HafTransforms.WorldPositions(model, 2, prim, world).Take(3).ToArray());
        // normals go through the blend too: a joint rotated a quarter turn about Y turns the normal with it
        model.Nodes[1].Rotation = new double[] { 0, Math.Sin(Math.PI / 4), 0, Math.Cos(Math.PI / 4) };
        world = HafTransforms.WorldMatrices(model);
        var nrm = HafTransforms.WorldNormals(model, 2, prim, world);
        Near(new[] { 0, 0, -1.0 }, new[] { nrm[0], nrm[1], nrm[2] });   // vertex 0, all on B
        Near(new[] { 0, 1, 0.0 }, new[] { nrm[9], nrm[10], nrm[11] });  // unweighted: unchanged
        // an unskinned primitive on a node goes through that node's world matrix ...
        var plain = new HafPrimitive { VertexCount = 1, Positions = new float[] { 1, 2, 3 } };
        Near(new[] { 1000, 1001, 1002.0 }, HafTransforms.WorldPositions(model, 2, plain, world));
        // ... unless it is one of a skinned mesh's own primitives: Blender's importer skins the whole mesh, this primitive's joint
        // data is all zero, and its vertices ride joint 0 at weight 1 - joint A at the origin here, so the file's position
        // (measured 2026-10-03 on the mixed_skin fixture; no registry file has the shape)
        model.Meshes[0].Primitives.Add(plain);
        Near(new[] { 1, 2, 3.0 }, HafTransforms.WorldPositions(model, 2, plain, world));
    }

    [Fact]
    public void A_pose_samples_each_channel_at_a_time_and_leaves_the_rest_static()
    {
        var model = new HafModel();
        model.Nodes.Add(new HafNode { Name = "moves", Translation = new double[] { 0, 0, 0 } });
        model.Nodes.Add(new HafNode { Name = "still", Translation = new double[] { 7, 7, 7 } });
        var anim = new HafAnimation { Name = "walk" };
        anim.Samplers.Add(new HafSampler { Times = new float[] { 0, 1 }, Values = new float[] { 0, 0, 0, 10, 0, 0 }, Components = 3, Interpolation = "LINEAR" });
        anim.Samplers.Add(new HafSampler { Times = new float[] { 0, 1 }, Values = new float[] { 1, 1, 1, 3, 3, 3 }, Components = 3, Interpolation = "STEP" });
        anim.Samplers.Add(new HafSampler { Times = new float[] { 0, 1 }, Values = new float[] { 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, Components = 4, Interpolation = "CUBICSPLINE" });
        anim.Channels.Add(new HafChannel { Sampler = 0, Node = 0, Path = "translation" });
        anim.Channels.Add(new HafChannel { Sampler = 1, Node = 0, Path = "scale" });
        anim.Channels.Add(new HafChannel { Sampler = 2, Node = 0, Path = "rotation" });
        model.Animations.Add(anim);
        // t = 0: the first keys; the still node is untouched (null = static)
        var pose0 = HafTransforms.PoseAt(model, 0, 0.0);
        Assert.Null(pose0(1));
        var w0 = HafTransforms.WorldMatrices(model, pose0);
        Near(new[] { 0, 0, 0.0 }, HafTransforms.Apply(w0[0], 0, 0, 0, 1));
        Near(new[] { 7, 7, 7.0 }, HafTransforms.Apply(w0[1], 0, 0, 0, 1));
        Near(new[] { 1, 1, 1.0 }, HafTransforms.Apply(w0[0], 1, 1, 1, 0));                      // scale 1 at the first STEP key
        // t = 0.5: LINEAR translation halfway, STEP scale still the first key, CUBICSPLINE rotation the key's VALUE (identity)
        var w5 = HafTransforms.WorldMatrices(model, HafTransforms.PoseAt(model, 0, 0.5));
        Near(new[] { 5, 0, 0.0 }, HafTransforms.Apply(w5[0], 0, 0, 0, 1));
        Near(new[] { 1, 1, 1.0 }, HafTransforms.Apply(w5[0], 1, 1, 1, 0));
        // t = 1 and beyond: the last keys hold
        var w2 = HafTransforms.WorldMatrices(model, HafTransforms.PoseAt(model, 0, 2.0));
        Near(new[] { 10, 0, 0.0 }, HafTransforms.Apply(w2[0], 0, 0, 0, 1));
        Near(new[] { 3, 3, 3.0 }, HafTransforms.Apply(w2[0], 1, 1, 1, 0));
        // no such animation: null, the static transforms are the pose
        Assert.Null(HafTransforms.PoseAt(model, 1, 0.0));
        // a LINEAR rotation between two keys is the SLERP: identity to a half turn about Y, halfway is a quarter turn
        var rot = new HafSampler { Times = new float[] { 0, 1 }, Values = new float[] { 0, 0, 0, 1, 0, 1, 0, 0 }, Components = 4, Interpolation = "LINEAR" };
        Near(new[] { 0, 0, 0, 1.0 }, HafTransforms.Sample(rot, 0));
        Near(new[] { 0, 1, 0, 0.0 }, HafTransforms.Sample(rot, 1));
        Near(new[] { 0, Math.Sin(Math.PI / 4), 0, Math.Cos(Math.PI / 4) }, HafTransforms.Sample(rot, 0.5));
        Near(new[] { 0, Math.Sin(Math.PI / 8), 0, Math.Cos(Math.PI / 8) }, HafTransforms.Sample(rot, 0.25));   // a quarter of the way is an eighth turn - lerp would not give this
        // a CUBICSPLINE translation is the cubic Hermite over value and tangents: zero tangents give the smoothstep,
        // whose midpoint is halfway and whose quarter point is 5/32 of the way (a held-value sampler would give 0)
        var cub = new HafSampler { Times = new float[] { 0, 2 }, Values = new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0 }, Components = 3, Interpolation = "CUBICSPLINE" };
        Near(new[] { 4, 0, 0.0 }, HafTransforms.Sample(cub, 1.0));
        Near(new[] { 8 * 5 / 32.0, 0, 0 }, HafTransforms.Sample(cub, 0.5));
        Near(new[] { 8, 0, 0.0 }, HafTransforms.Sample(cub, 2.0));
        // with a tangent: out-tangent 4 at the first key over a 2 s interval pulls the first half up (h10 = 1/8 at the midpoint: +1)
        cub.Values[6] = 4;   // key 0's out-tangent, x
        Near(new[] { 5, 0, 0.0 }, HafTransforms.Sample(cub, 1.0));
        // before the first key the first value holds; after the last, the last
        var late = new HafSampler { Times = new float[] { 1, 2 }, Values = new float[] { 3, 3, 3, 9, 9, 9 }, Components = 3, Interpolation = "LINEAR" };
        Near(new[] { 3, 3, 3.0 }, HafTransforms.Sample(late, 0.0));
        Near(new[] { 6, 6, 6.0 }, HafTransforms.Sample(late, 1.5));
        Near(new[] { 9, 9, 9.0 }, HafTransforms.Sample(late, 5.0));
    }
}
