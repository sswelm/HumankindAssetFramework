using System;
using System.Linq;
using Xunit;

/// <summary>What the Lab's probe does to the scene before it measures - the second model's merge matrix, the placement's
/// matrix and the matrix_world round trip, the orientation the inside-out verdicts are judged in, Mesh.transform - in Blender's
/// arithmetic (VehicleProbe.Merge.cs). Every expectation is a value Blender 5.1.2 printed (repr) on 2026-10-03 for the same
/// inputs through mathutils and an object's matrix_world, compared BIT FOR BIT; the drill holds the same on the second_model and
/// placement fixtures and on the recipes that set these inputs (tools/vehicle-probe-drill/probe_jobs.py).</summary>
public class VehicleProbeMergeTests
{
    static HafModel BoxModel()
    {
        var m = new HafModel(); var mesh = new HafMesh();
        mesh.Primitives.Add(new HafPrimitive { VertexCount = 8,
            Positions = new float[] { -1,-1,-1, 1,-1,-1, -1,1,-1, 1,1,-1, -1,-1,1, 1,-1,1, -1,1,1, 1,1,1 },
            Indices = new[] { 0,2,1, 1,2,3, 4,5,6, 5,7,6, 0,1,5, 0,5,4, 2,6,7, 2,7,3, 0,4,6, 0,6,2, 1,3,7, 1,7,5 } });
        m.Meshes.Add(mesh); return m;
    }

    [Fact]
    public void Detaching_a_sheared_child_refreshes_its_box_and_its_descendants_boxes()
    {
        var m = BoxModel();
        m.Nodes.Add(new HafNode { Name = "Carrier", Mesh = 0, Scale = new double[] { 2,1,1 } }); m.Nodes[0].Children.Add(1);
        m.Nodes.Add(new HafNode { Name = "Child", Mesh = 0, Parent = 0,
            Rotation = new[] { 0, Math.Sin(Math.PI / 8), 0, Math.Cos(Math.PI / 8) } }); m.Nodes[1].Children.Add(2);
        m.Nodes.Add(new HafNode { Name = "Grand", Mesh = 0, Parent = 1, Translation = new double[] { 0,0,3 } });
        var input = new VehicleProbe.Input { Model = m }; input.AddPlacementLines(new[] { "Carrier|0,0,2|1,1,1" });
        var r = VehicleProbe.Run(input);
        // Blender 5.1.2: the child's matrix_world assignment removes its shear. World boxes follow the rebuilt matrix.
        Assert.Equal("PART|Child|8|0.0000,0.0000,0.0000|4.4711,4.4711,2.0000|1||0", r.Parts.Single(p => p.Name == "Child").Row);
        Assert.Equal("PART|Grand|8|3.2801,-3.4265,0.0000|4.4711,4.4711,2.0000|1||0", r.Parts.Single(p => p.Name == "Grand").Row);
    }

    [Fact]
    public void Preview_surface_normals_stay_perpendicular_under_nonuniform_scale()
    {
        var m = new HafModel(); var mesh = new HafMesh(); float n = F(1 / Math.Sqrt(2));
        mesh.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[] { 0,0,0, 1,0,1, 0,1,0 },
            Normals = new[] { -n,0,n, -n,0,n, -n,0,n } });
        m.Meshes.Add(mesh); m.Nodes.Add(new HafNode { Name = "Panel", Mesh = 0, Scale = new double[] { 2,1,1 } });
        var data = Assert.Single(VehicleProbe.Run(m).Parts).Mesh;
        float dot = 0, rayDot = 0;
        for (int i = 0; i < 3; i++)
        {
            float tangent = data.World[3 + i] - data.World[i];
            dot += tangent * data.PreviewNormal[i]; rayDot += tangent * data.Normal[i];
        }
        Assert.InRange(Math.Abs(dot), 0f, 0.0002f);
        Assert.True(Math.Abs(rayDot) > 1f); // the script's visibility ray intentionally follows the forward matrix
    }

    [Fact]
    public void Unsupported_morph_and_animated_second_models_keep_the_Blender_path()
    {
        var m = BoxModel(); m.Nodes.Add(new HafNode { Mesh = 0 });
        var input = new VehicleProbe.Input { Model = m };
        Assert.Null(input.InProcessFallbackReason());
        m.Meshes[0].Primitives[0].MorphTargets = 1;
        Assert.Contains("morph targets", input.InProcessFallbackReason());
        m.Meshes[0].Primitives[0].MorphTargets = 0;
        var second = BoxModel(); input.Second = second;
        Assert.Null(input.InProcessFallbackReason());
        second.Animations.Add(new HafAnimation());
        Assert.Contains("second model is animated", input.InProcessFallbackReason());
    }

    [Fact]
    public void Bone_parented_and_evaluated_skinned_meshes_keep_the_Blender_path()
    {
        var m = BoxModel();
        m.Nodes.Add(new HafNode { Name = "Rig" }); m.Nodes[0].Children.Add(1);
        m.Nodes.Add(new HafNode { Name = "Joint", Parent = 0 }); m.Nodes[1].Children.Add(2);
        m.Nodes.Add(new HafNode { Name = "BoneMesh", Mesh = 0, Parent = 1 });
        m.Nodes.Add(new HafNode { Name = "SkinMesh", Mesh = 0, Skin = 0 });
        m.Skins.Add(new HafSkin { Skeleton = 0, Joints = new[] { 1 } });
        var input = new VehicleProbe.Input { Model = m };
        Assert.Contains("parented to a bone", input.InProcessFallbackReason());
        m.Nodes[2].Mesh = -1;
        var primitive = m.Meshes[0].Primitives[0]; primitive.Joints = new ushort[32];
        primitive.Weights = Enumerable.Range(0, 32).Select(i => i % 4 == 0 ? 1f : 0f).ToArray();
        Assert.Null(input.InProcessFallbackReason());
        input.AddPlacementLines(new[] { "SkinMesh|0,0,1|1,1,1" });
        Assert.Contains("evaluated pose", input.InProcessFallbackReason());
        input.Placements.Clear(); m.Animations.Add(new HafAnimation());
        Assert.Contains("evaluated pose", input.InProcessFallbackReason());
    }

    static float F(double x) => (float)x;
    static float[] Row(params double[] v) => v.Select(F).ToArray();
    static double Rad(double deg) => deg * (Math.PI / 180.0);

    [Fact]
    public void The_second_models_T2_is_Blenders_product_of_translation_rotations_and_diagonal()
    {
        // Matrix.Translation(off) @ Rotation(rz) @ Rotation(ry) @ Rotation(rx) @ Diagonal(scale), as merge2= composes it; the
        // 30-degree sine is 0.5000001192092896 - angle_wrap_rad in float32, then sinf
        Assert.Equal(Row(1.7320506572723389, -0.5000001192092896, 0, 0.5, 1.000000238418579, 0.8660253286361694, 0, 0, 0, 0, 1, 2, 0, 0, 0, 1),
                     VehicleProbe.Merge2Matrix(new double[] { 0.5, 0, 2 }, new double[] { 0, 0, 30 }, new double[] { 2, 1, 1 }));
        Assert.Equal(Row(0.4068987965583801, -0.2204848676919937, 0.18926110863685608, 0, 0.23492321372032166, 0.4412820041179657, 0.009014174342155457, -3, -0.17101003229618073, 0.08158794045448303, 0.4627082645893097, 0, 0, 0, 0, 1),
                     VehicleProbe.Merge2Matrix(new double[] { 0, -3, 0 }, new double[] { 10, 20, 30 }, new double[] { 0.5, 0.5, 0.5 }));
        Assert.Equal(Row(0.7071056365966797, -0.07564842700958252, -0.7030496597290039, 1.25, 0.001234058989211917, 0.9943913221359253, -0.10575571656227112, -0.5, 0.7071068286895752, 0.07391286641359329, 0.7032330632209778, 3, 0, 0, 0, 1),
                     VehicleProbe.Merge2Matrix(new double[] { 1.25, -0.5, 3 }, new double[] { 6, -45, 0.1 }, new double[] { 1, 1, 1 }));
    }

    [Fact]
    public void Matrix_Rotation_wraps_the_angle_in_float32_before_cosf_and_sinf()
    {
        // (cos, sin) Blender holds for Matrix.Rotation(radians(deg), 4, 'Z'): R[0][0] and R[1][0] - 180 degrees gives sin 8.74e-8, not 0
        foreach (var (deg, c, s) in new[] { (6.0, 0.9945218563079834, 0.10452858358621597), (30.0, 0.8660253286361694, 0.5000001192092896), (-45.0, 0.7071067094802856, -0.7071068286895752), (180.0, -1.0, 8.742277657347586e-08), (0.1, 0.9999984502792358, 0.0017452230677008629) })
        {
            var r = VehicleProbe.MatRotation(Rad(deg), 'Z');
            Assert.Equal(F(c), r[0]); Assert.Equal(F(s), r[4]);
        }
    }

    [Fact]
    public void The_probe_orientation_is_Rx_Ry_Rz()
    {
        Assert.Equal(Row(0.7032330632209778, 0.7032331824302673, 0.10452858358621597, 0, -0.7071068286895752, 0.7071067094802856, 0, 0, -0.07391286641359329, -0.07391287386417389, 0.9945218563079834, 0, 0, 0, 0, 1),
                     VehicleProbe.ProbeOrientMatrix(new[] { 0.0, 6.0, -45.0 }));   // the rah66 recipe's Orientation
        Assert.Equal(Row(7.549790126404332e-08, -1, 0, 0, 1, 7.549790126404332e-08, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1), VehicleProbe.ProbeOrientMatrix(new[] { 0.0, 0.0, 90.0 }));
        Assert.Equal(Row(1, 0, 0, 0, 0, 7.549790126404332e-08, -1, 0, 0, 1, 7.549790126404332e-08, 0, 0, 0, 0, 1), VehicleProbe.ProbeOrientMatrix(new[] { 90.0, 0.0, 0.0 }));
    }

    [Fact]
    public void Assigning_matrix_world_decomposes_and_recomposes_the_matrix()
    {
        // a 90-degree permutation with residues comes back with Blender's quaternion round-trip bits; a rotation with a scale
        // comes back unchanged; a shear cannot be held and comes back as the nearest loc-rot-size
        Assert.Equal(Row(1, 0, 0, 0, 0, -1.3435885648505064e-07, -1.0000001192092896, 0, 0, 1.0000001192092896, -1.3435885648505064e-07, 0, 0, 0, 0, 1),
                     VehicleProbe.ApplyMat4(Row(1, 0, 0, 0, 0, 2.220446049250313e-16, -1, 0, 0, 1, 2.220446049250313e-16, 0, 0, 0, 0, 1)));
        var rotz = Row(1.7320506572723389, -0.5000001192092896, 0, 1.5, 1.000000238418579, 0.8660253286361694, 0, -2, 0, 0, 1, 0.25, 0, 0, 0, 1);
        Assert.Equal(rotz, VehicleProbe.ApplyMat4(rotz));
        Assert.Equal(Row(0.9739694595336914, 0.2534351646900177, 0, 3, -0.22667929530143738, 1.0889309644699097, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
                     VehicleProbe.ApplyMat4(Row(1, 0.5, 0, 3, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)));
    }

    [Fact]
    public void A_placement_is_T_onto_matrix_world_about_the_world_box_centre()
    {
        var mw = Row(1.7320506572723389, -0.5000001192092896, 0, 1.5, 1.000000238418579, 0.8660253286361694, 0, -2, 0, 0, 1, 0.25, 0, 0, 0, 1);
        VehicleProbe.WorldBox(mw, new float[] { -1, -1, -1 }, new float[] { 1, 1, 1 }, out var c0, out var size);
        Assert.Equal(new[] { 1.5f, -2f, 0.25f }, c0);
        Assert.Equal(Row(4.464101791381836, 3.732050895690918, 2), size);
        Assert.Equal(Row(1.1230887174606323, -0.47632279992103577, 0, 1.5, 0.6990508437156677, 0.7652558088302612, 0, -2, 0, 0, 1, 2.25, 0, 0, 0, 1),
                     VehicleProbe.PlacementMatrix(mw, c0, new double[] { 0, 0, 2 }, new double[] { 0.5, 1, 1 }));
    }

    [Fact]
    public void Mesh_transform_moves_a_vertex_as_transform_point_does()
    {
        var T2 = VehicleProbe.Merge2Matrix(new double[] { 0.5, 0, 2 }, new double[] { 0, 0, 30 }, new double[] { 2, 1, 1 });
        var expected = new[] { Row(0.5732050538063049, 0.27320510149002075, 2.299999952316284), Row(3.232050895690918, -0.7320504188537598, 5), Row(214.33154296875, 123.45690155029297, -5.5) };
        var input = new[] { (0.1f, 0.2f, 0.3f), (1.0f, -2.0f, 3.0f), (123.456f, 0.001f, -7.5f) };
        for (int i = 0; i < 3; i++)
        {
            VehicleProbe.TransformPoint(T2, input[i].Item1, input[i].Item2, input[i].Item3, out float x, out float y, out float z);
            Assert.Equal(expected[i], new[] { x, y, z });
        }
        Assert.Equal(2f, VehicleProbe.Determinant4(T2));
    }

    [Fact]
    public void Flipping_faces_swaps_corners_1_and_2_and_carries_their_data()
    {
        var d0 = new short[] { 10, 11, 12, 20, 21, 22 }; var d1 = new short[] { 1, 2, 3, 4, 5, 6 };
        var flipped = VehicleProbe.FlipFaces(new[] { 0, 1, 2, 3, 4, 5 }, d0, d1);
        Assert.Equal(new[] { 0, 2, 1, 3, 5, 4 }, flipped);
        Assert.Equal(new short[] { 10, 12, 11, 20, 22, 21 }, d0);
        Assert.Equal(new short[] { 1, 3, 2, 4, 6, 5 }, d1);
    }

    [Fact]
    public void The_merge2_text_is_parsed_as_the_script_parses_it()
    {
        Assert.True(VehicleProbe.Input.TryParseMerge2("D:/x/b.glb|0.5,0,2|0,0,30|2,1,1", out string path, out var off, out var rot, out var scl, out var bad, out string error));
        Assert.Equal("D:/x/b.glb", path); Assert.Equal(new[] { 0.5, 0, 2 }, off); Assert.Equal(new[] { 0.0, 0, 30 }, rot); Assert.Equal(new[] { 2.0, 1, 1 }, scl); Assert.Empty(bad); Assert.Null(error);
        Assert.True(VehicleProbe.Input.TryParseMerge2("b.glb|0,0,0|0,0,0|0.5", out _, out _, out _, out scl, out bad, out _));
        Assert.Equal(new[] { 0.5, 0.5, 0.5 }, scl);   // one number is a uniform scale
        Assert.True(VehicleProbe.Input.TryParseMerge2("b.glb|0,0,0|0,0,0|0,-2,abc", out _, out _, out _, out scl, out bad, out _));
        Assert.Equal(new[] { 1.0, 1, 1 }, scl); Assert.Equal(new[] { "0", "-2", "abc" }, bad);   // not positive finite: 1, and said
        Assert.False(VehicleProbe.Input.TryParseMerge2("b.glb|0,0,0|0,0,0", out _, out _, out _, out _, out _, out error));
        Assert.Contains("malformed merge2 argument", error);
        Assert.False(VehicleProbe.Input.TryParseMerge2("b.glb|0,0,0|0,0,0|1,2", out _, out _, out _, out _, out _, out error));
        Assert.Contains("expected one number or three, got 2", error);
    }

    [Fact]
    public void Placement_lines_split_from_the_right_and_bad_ones_are_warned_and_dropped()
    {
        var input = new VehicleProbe.Input();
        input.AddPlacementLines(new[] { "Skin|A|1,2,3|2,1,1", "nonsense", "Flat|0,0,0|0,1,1", "", "Skin|A|4,5,6|1,1,1" });
        Assert.Equal(2, input.Placements.Count);
        Assert.Equal("Skin|A", input.Placements[0].Name); Assert.Equal(new[] { 4.0, 5, 6 }, input.Placements[0].Offset);   // a dict: first place, last numbers
        Assert.Equal(new[] { 1.0, 1, 1 }, input.Placements[1].Scale);
        Assert.Equal(2, input.Warnings.Count);
        Assert.Contains("bad placement line ignored: 'nonsense'", input.Warnings[0]);
        Assert.Contains("non-positive scale [0.0, 1.0, 1.0]", input.Warnings[1]);
        input.SetProbeRotation("0,6");
        Assert.Equal(new[] { 0.0, 6, 0 }, input.ProbeRotation);
        input.SetProbeRotation("x,y,z");
        Assert.Null(input.ProbeRotation); Assert.Contains("bad proberot argument", input.Warnings.Last());
    }
}
