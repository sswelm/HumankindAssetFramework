using System;
using System.Linq;
using Xunit;

/// <summary>The mesh data as Blender's importer stores it and as its `vertex.normal` reads it (VehicleProbe.BlenderSkin.cs,
/// VehicleProbe.CustomNormals.cs). Every expectation is a value Blender 5.1.2 printed (repr) on 2026-10-03, compared BIT FOR
/// BIT: `Matrix.inverted_safe()` of two matrices, the skinned vertices of the `visibility_skinned` fixture, the vertex normals
/// of the `custom_normals` fixture. The drill holds vertex 0 of every part of 119 files to the same standard (VERTEX rows).</summary>
public class VehicleProbeBlenderMeshTests
{
    static float F(double x) => (float)x;
    static HafModel Link(HafModel m) { for (int i = 0; i < m.Nodes.Count; i++) foreach (var c in m.Nodes[i].Children) m.Nodes[c].Parent = i; return m; }

    [Fact]
    public void The_safe_inverse_of_a_permutation_and_of_a_general_matrix_matches_Blenders_bits()
    {
        // Matrix(((1,0,0,0),(0,2.22e-16,-1,0),(0,1,2.22e-16,0),(0,0,0,1))).inverted_safe() and a general affine one, row-major
        var perm = new float[] { 1, 0, 0, 0, 0, F(2.220446049250313e-16), -1, 0, 0, 1, F(2.220446049250313e-16), 0, 0, 0, 0, 1 };
        var invPerm = VehicleProbe.InvertedSafe(perm);
        var expPerm = new float[] { 1, -0f, 0, -0f, -0f, F(2.220446049250313e-16), 1, 0, 0, -1, F(2.220446049250313e-16), -0f, -0f, 0, -0f, 1 };
        Assert.Equal(expPerm.Select(Bits), invPerm.Select(Bits));
        var gen = new float[] { 0.8f, 0.1f, -0.3f, 1.5f, 0.2f, 0.9f, 0.4f, -2.0f, -0.1f, 0.3f, 0.7f, 0.25f, 0, 0, 0, 1 };
        var invGen = VehicleProbe.InvertedSafe(gen);
        var expGen = new[] { F(1.47826087474823), F(-0.4637680947780609), F(0.8985507488250732), F(-3.369565010070801), F(-0.52173912525177), F(1.5362317562103271), F(-1.1014493703842163), F(4.130434989929199), F(0.43478262424468994), F(-0.7246376872062683), F(2.0289855003356934), F(-2.6086955070495605), -0f, 0f, -0f, 1f };
        Assert.Equal(expGen.Select(Bits), invGen.Select(Bits));
    }
    static uint Bits(float x) => BitConverter.ToUInt32(BitConverter.GetBytes(x), 0);

    [Fact]
    public void A_skinned_vertex_lands_where_the_importers_float32_chain_puts_it()
    {
        // the visibility_skinned fixture's TurnedMesh: one joint turned atan2(2, 1) about glTF Y, no inverse bind matrices;
        // Blender: v1 co (0.08944272994995117, 0.17888543009757996, 0.0), v2 co (0, 0, 0.20000000298023224)
        double th = Math.Atan2(2.0, 1.0);
        var m = new HafModel();
        var me = new HafMesh { Name = "turned" };
        me.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[] { 0, 0, 0, 0.2f, 0, 0, 0, 0.2f, 0 }, Normals = new float[] { 1, 0, 0, 1, 0, 0, 1, 0, 0 }, Mode = 4,
                                             Joints = new ushort[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Weights = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 } });
        m.Meshes.Add(me);
        m.Nodes.Add(new HafNode { Name = "Rig" }); m.Nodes[0].Children.Add(1);
        m.Nodes.Add(new HafNode { Name = "Turned", Rotation = new[] { 0, Math.Sin(th / 2), 0, Math.Cos(th / 2) } });
        m.Nodes.Add(new HafNode { Name = "TurnedMesh", Mesh = 0, Skin = 0 });
        m.Skins.Add(new HafSkin { Name = "Skin", Joints = new[] { 1 } });
        var names = BlenderNames.Compute(Link(m));
        var sk = VehicleProbe.BlenderSkinner.Build(m, 0, names);
        Assert.Equal(new[] { F(0.08944272994995117), F(0.17888543009757996), 0f }.Select(Bits), sk.Position(me.Primitives[0], 1).Select(Bits));
        Assert.Equal(new[] { 0f, 0f, F(0.20000000298023224) }.Select(Bits), sk.Position(me.Primitives[0], 2).Select(Bits));
        // the vertex normals Blender then reads are the skinned normals through the custom-normal encoding: v0 (0.44721362, 0.89442724, 0),
        // v1 and v2 with 1.9e-5 and 3.8e-5 of z from the quantization - the fan normal for v0 (its custom normal lies within 1e-4 of it)
        var P = new float[9]; var N = new float[9];
        for (int v = 0; v < 3; v++) { var q = sk.Position(me.Primitives[0], v); var n = sk.Normal(me.Primitives[0], v); for (int k = 0; k < 3; k++) { P[v * 3 + k] = q[k]; N[v * 3 + k] = n[k]; } }
        var vn = VehicleProbe.BlenderVertexNormals(P, new[] { 0, 1, 2 }, N);
        var expected = new[] { F(0.44721361994743347), F(0.8944272398948669), 0f, F(0.4472135901451111), F(0.8944271802902222), F(1.9043684005737305e-05), F(0.4472135901451111), F(0.8944271206855774), F(3.8135043723741546e-05) };
        Assert.Equal(expected.Select(Bits), vn.Select(Bits));
    }

    [Fact]
    public void A_vertex_normal_is_the_file_normal_through_Blenders_two_short_custom_normal_encoding()
    {
        // the custom_normals fixture, Blender's frame: Leaning - file normals leaning 10 degrees about X (+Z side for v0, v1; -Z for v2, v3)
        double th = Math.PI / 18.0;
        float c = (float)Math.Cos(th), s = (float)Math.Sin(th);
        var P = new float[] { 0, 0, 0, 1, 0, 0, 1, -1, 0, 0, -1, 0 };   // glTF (x, 0, z) -> Blender (x, -z, 0)
        var tris = new[] { 0, 1, 2, 0, 2, 3 };
        var N = new float[] { 0, -s, c, 0, -s, c, 0, s, c, 0, s, c };   // glTF (0, cos, +-sin) -> Blender (0, -+sin, cos)
        var vn = VehicleProbe.BlenderVertexNormals(P, tris, N);
        var expected = new[] { F(-7.588794481705463e-09), F(-0.1736113727092743), F(0.9848142266273499), 0f, F(-0.17361138761043549), F(0.9848142862319946),
                               F(7.588794481705463e-09), F(0.1736113727092743), F(0.9848142266273499), 0f, F(0.17361138761043549), F(0.9848142862319946) };
        Assert.Equal(expected.Select(Bits), vn.Select(Bits));
        // Flat: file normals +Y (Blender +Z) on a quad wound to face -Y - the custom normal is the fan normal's opposite and the
        // decode lands 4.777e-5 off axis, as on the Dragon's decals
        var N2 = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 };
        var vn2 = VehicleProbe.BlenderVertexNormals(P, tris, N2);
        var expected2 = new[] { F(-2.0881428097058174e-12), F(-4.777114008902572e-05), 1f, F(-4.777114008902572e-05), F(2.0881428097058174e-12), 1f,
                                F(2.0881428097058174e-12), F(4.777114008902572e-05), 1f, F(4.777114008902572e-05), F(-2.0881428097058174e-12), 1f };
        Assert.Equal(expected2.Select(Bits), vn2.Select(Bits));
    }
}
