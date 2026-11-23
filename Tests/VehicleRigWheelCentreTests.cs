using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

/// <summary>The Vehicle Lab's Wheel centre (vehicle_rig.py's tagged argument <c>wheelcentre=area</c>, 2026-11-23; the H.L. Hunley's
/// screw). The expectations come from the INPUT and the contract, not from the code: a shape that maps onto itself under a turn
/// about its axle has its surface centroid ON that axle, whatever its blade count; its box centre is on the axle only when the
/// count is even. The drill (tools/vehiclerig_drill.sh, fx_r_wheel_centre*) holds the same branches against Blender.</summary>
public class VehicleRigWheelCentreTests
{
    const string Tag = "wheelcentre=area";

    // ---- the contract on a three-bladed screw

    [Fact]
    public void A_three_bladed_screws_box_centre_is_off_its_shaft()
    {
        // the fixture discriminates: without this the tests below could pass on a shape whose box centre is already on the axis
        var s = Stages(ScrewModel(), ScrewArgs());
        var head = Head(s, "Wheel_00");
        Assert.True(Math.Abs(head[0] - ShaftX) > 0.05, "the box centre should sit more than 0.05 off the shaft in x, got " + (head[0] - ShaftX));
        Assert.True(Math.Abs(head[2] - ShaftZ) > 0.05, "the box centre should sit more than 0.05 off the shaft in z, got " + (head[2] - ShaftZ));
        Assert.DoesNotContain(s.Logs3, l => l.Contains("wheel centre"));
    }

    [Fact]
    public void The_tag_puts_the_bone_on_the_shaft_and_leaves_the_axle_alone()
    {
        var box = Stages(ScrewModel(), ScrewArgs()); var area = Stages(ScrewModel(), ScrewArgs(Tag));
        var hb = Head(box, "Wheel_00"); var ha = Head(area, "Wheel_00");
        Near(ShaftX, ha[0], 1e-5); Near(ShaftZ, ha[2], 1e-5);     // across the axle: the shaft
        Assert.Equal(hb[1], ha[1]);                                         // along the axle: the box centre, to the bit
        Assert.Equal(Axis(box, "Wheel_00"), Axis(area, "Wheel_00"));        // the bone still runs along +Y
        Assert.Equal(new[] { 0f, 1f, 0f }, Axis(area, "Wheel_00"));
        // the whole line, by hand: the shaft at (2, 1) across the axle, the box's middle 3 along it; the box centre of this screw is
        // 0.0868 and 0.1710 off the shaft (recomputed from the blade angles), which is 0.192 apart
        Assert.Equal("VEHICLE wheel centre: Wheel_00 at its surface centroid (2.000,3.000,1.000), 0.192 from the box centre",
            Assert.Single(area.Logs3.Where(l => l.StartsWith("VEHICLE wheel centre:", StringComparison.Ordinal))));
        // the Root bone and the hull's mesh are where the box rig has them
        Assert.Equal(Head(box, "Root"), Head(area, "Root"));
        Assert.Equal(Assert.Single(box.Objects.Where(o => o.Name == "Mesh_Root")).Mesh.Positions, Assert.Single(area.Objects.Where(o => o.Name == "Mesh_Root")).Mesh.Positions);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public void The_surface_centroid_is_on_the_shaft_for_every_blade_count(int blades)
    {
        var area = Stages(ScrewModel(blades), ScrewArgs(Tag)); var h = Head(area, "Wheel_00");
        Near(ShaftX, h[0], 1e-5); Near(ShaftZ, h[2], 1e-5);
    }

    [Theory]
    [InlineData("X", 0, 2)]
    [InlineData("Z", 2, 0)]   // review 2026-11-23: a plant that took the centroid ALONG a Z axle passed every test
    public void A_forced_axle_keeps_the_box_centre_along_it_and_takes_the_centroid_across_it(string axis, int along, int across)
    {
        var box = Stages(ScrewModel(), ScrewArgs(axis: axis)); var area = Stages(ScrewModel(), ScrewArgs(axis: axis, tags: Tag));
        var hb = Head(box, "Wheel_00"); var ha = Head(area, "Wheel_00");
        float[] shaft = { ShaftX, 0f, ShaftZ };
        Assert.True(Math.Abs(hb[0] - ShaftX) > 0.05 && Math.Abs(hb[2] - ShaftZ) > 0.05, "the box centre must be off the shaft on both axes for this to say anything");
        Assert.Equal(hb[along], ha[along]);               // along the forced axle: the box centre, to the bit - NOT the centroid
        Near(shaft[across], ha[across], 1e-5);            // across it: the centroid (the screw's own shaft)
        // the third axis is across the forced axle too: the centroid's height through the screw's thickness, by hand - the hub's
        // faces are centred on y 3, the six blade triangles lie at y 3 and 2.98; weighed by area that is below 3 and above 2.98
        Assert.True(ha[1] < 3f && ha[1] > 2.98f, "across a forced axle the screw's own thin axis takes the centroid, got " + ha[1]);
        var want = new float[3]; want[along] = 1f;
        Assert.Equal(want, Axis(area, "Wheel_00"));
    }

    // ---- the centroid is the whole cluster's, weighted by area

    [Fact]
    public void A_cluster_of_two_parts_pivots_at_their_combined_surface_centroid()
    {
        // a hub box 1 x 0.2 x 1 (Blender x, y, z) about (6, 0, 1): area 2 * (0.2 + 1 + 0.2) = 2.8, centroid its centre;
        // a right triangle on it with legs 0.4 along x and 0.3 along z from (6.3, 0, 1.2): area 0.06, centroid at a third of each leg
        var m = Model(); Box(m, "Hull", 0, 0, 0, 2, 2, 2);
        Box(m, "FanHub", 6f, 1f, 0f, 1f, 1f, 0.2f);                       // glTF (x, y, z) = Blender (x, z, -y)
        Raw(m, "FanBlade", new[] { 6.3f, 1.2f, 0f, 6.7f, 1.2f, 0f, 6.3f, 1.5f, 0f }, new[] { 0, 1, 2 });   // its box centre 0.61 from the hub's: inside 0.75 of the hub's width
        var a = Args(Tag); Mark(a, "wheels", "FanHub", "FanBlade");
        var s = Stages(m, a);
        Assert.Equal(new[] { "Root", "Wheel_00" }, s.Armature.BoneNodes.Select(b => s.Names.BoneOfJoint[b]));
        double aHub = 2.8, aBlade = 0.06;
        double x = (aHub * 6.0 + aBlade * (6.3 + 0.4 / 3.0)) / (aHub + aBlade), z = (aHub * 1.0 + aBlade * (1.2 + 0.3 / 3.0)) / (aHub + aBlade);
        var h = Head(s, "Wheel_00");
        Near(x, h[0], 1e-5); Near(z, h[2], 1e-5);
        Assert.Equal(0f, h[1]);                            // along the axle (Y): the anchor's box centre
        // a part LISTED TWICE is one surface (review 2026-11-23: the blade weighed double and the pivot moved)
        var twice = Args(Tag); Mark(twice, "wheels", "FanHub", "FanBlade", "FanBlade");
        Assert.Equal(h, Head(Stages(m, twice), "Wheel_00"));
    }

    [Fact]
    public void A_face_of_four_corners_weighs_as_its_two_triangles()
    {
        var tri = Part(new float[] { 0, 0, 0, 2, 0, 0, 2, 1, 0, 0, 3, 0 }, new[] { 0, 1, 2, 0, 2, 3 }, null);
        var quad = Part(new float[] { 0, 0, 0, 2, 0, 0, 2, 1, 0, 0, 3, 0 }, new[] { 0, 1, 2, 3 }, new[] { 0, 4 });
        var ct = Centroid(tri); var cq = Centroid(quad);
        Assert.Equal(ct, cq);
        // by hand: triangles (0,0)(2,0)(2,1) area 1 centroid (4/3, 1/3) and (0,0)(2,1)(0,3) area 3 centroid (2/3, 4/3)
        Near((1 * 4.0 / 3.0 + 3 * 2.0 / 3.0) / 4.0, cq[0], 1e-12); Near((1 * 1.0 / 3.0 + 3 * 4.0 / 3.0) / 4.0, cq[1], 1e-12); Assert.Equal(0.0, cq[2]);
    }

    [Fact]
    public void The_centroid_does_not_depend_on_how_finely_a_surface_is_cut()
    {
        // one square as two triangles, and the same square with one half cut into three: a vertex mean moves, the area centroid does not
        var coarse = Part(new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }, new[] { 0, 1, 2, 0, 2, 3 }, null);
        var fine = Part(new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0.75f, 0.25f, 0 }, new[] { 0, 1, 4, 1, 2, 4, 2, 0, 4, 0, 2, 3 }, null);
        var cc = Centroid(coarse); var cf = Centroid(fine);
        Near(0.5, cc[0], 1e-12); Near(0.5, cc[1], 1e-12);
        Near(0.5, cf[0], 1e-12); Near(0.5, cf[1], 1e-12);
    }

    // ---- what the tag leaves alone

    [Fact]
    public void A_wheel_without_surface_area_keeps_its_box_centre_and_says_so()
    {
        var m = Model(); Box(m, "Hull", 0, 0, 0, 2, 2, 2);
        Raw(m, "Line", new[] { 4f, 0f, 0f, 5f, 0f, 0f, 6f, 0f, 0f }, new[] { 0, 1, 2 });
        var box = Args(); Mark(box, "wheels", "Line"); var area = Args(Tag); Mark(area, "wheels", "Line");
        var sb = Stages(m, box); var sa = Stages(m, area);
        Assert.Equal(Head(sb, "Wheel_00"), Head(sa, "Wheel_00"));
        Assert.Contains("VEHICLE *** WARNING: wheel centre: Wheel_00 has no surface area - the box centre kept", sa.Logs3);   // "*** WARNING": the shape the Lab's status box surfaces
        Assert.DoesNotContain(sa.Logs3, l => l.StartsWith("VEHICLE wheel centre:", StringComparison.Ordinal));
        Assert.Null(Centroid(Part(new float[] { 4, 0, 0, 5, 0, 0, 6, 0, 0 }, new[] { 0, 1, 2 }, null)));
        Assert.Null(Centroid(Part(new float[] { 0, 0, 0, float.NaN, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, null)));
        Assert.Null(Centroid(Part(new float[] { 0, 0, 0, float.PositiveInfinity, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, null)));
    }

    [Fact]
    public void A_rotor_keeps_its_hub_under_the_tag()
    {
        // a flat triangular plate: its surface centroid is NOT its box centre, so a rotor moved by the tag would show
        HafModel M()
        {
            var m = ScrewModel();
            Raw(m, "Mast", new[] { -6f, 2f, 0f, -5f, 2f, 0f, -6f, 2f, -1f, -6f, 2.2f, 0f, -5f, 2.2f, 0f, -6f, 2.2f, -1f },
                new[] { 0, 1, 2, 3, 5, 4, 0, 3, 4, 0, 4, 1, 1, 4, 5, 1, 5, 2, 2, 5, 3, 2, 3, 0 });
            return m;
        }
        var box = ScrewArgs(); Mark(box, "rotors", "Mast"); var area = ScrewArgs(Tag); Mark(area, "rotors", "Mast");
        var sb = Stages(M(), box); var sa = Stages(M(), area);
        Assert.Null(sb.Fallback); Assert.Null(sa.Fallback);
        Assert.Equal(Head(sb, "Wheel_01"), Head(sa, "Wheel_01"));           // the rotor: the cluster after the wheels
        var plate = Centroid(Assert.Single(sa.Objects.Where(o => o.Name == "Mesh_Wheel_01")));
        Assert.True(Math.Abs(plate[0] - Head(sa, "Wheel_01")[0]) > 0.05, "the rotor fixture must have its centroid off its box centre");
        Assert.NotEqual(Head(sb, "Wheel_00"), Head(sa, "Wheel_00"));        // the screw moved
        Assert.Single(sa.Logs3.Where(l => l.StartsWith("VEHICLE wheel centre:", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("wheelcentre=area", true)]
    [InlineData(" wheelcentre=area ", true)]       // a.strip()
    [InlineData("wheelcentre=box", false)]
    [InlineData("wheelcentre=AREA", false)]        // the script compares the text as written
    [InlineData("wheelcentre=", false)]
    public void Only_the_exact_tag_turns_it_on(string tag, bool on) => Assert.Equal(on, Args(tag).WheelCentreArea);

    [Fact]
    public void Without_the_tag_it_is_off() => Assert.False(Args().WheelCentreArea);

    // ---- the fixtures

    const float ShaftX = 2f, ShaftZ = 1f;   // Blender's frame: the screw turns about the line x = 2, z = 1, thin along Y

    /// <summary>A screw about glTF Z (Blender's Y) through glTF (2, 1, -3): a square hub and `blades` flat blades of two unequal
    /// triangles each, the first 20 degrees from glTF +X - so the shape has no mirror plane through an axis and its box centre is
    /// off the shaft in x AND in z. It maps onto itself under a turn of 360 / blades degrees.</summary>
    static HafModel ScrewModel(int blades = 3)
    {
        var m = Model(); Box(m, "Hull", 0, 0, 0, 2, 2, 2);
        const float cx = 2f, cy = 1f, cz = -3f, h = 0.1f, t = 0.05f, radius = 1f;
        var pos = new List<float>(); var idx = new List<int>();
        foreach (var (x, y, z) in new[] { (-h, -h, -t), (h, -h, -t), (h, h, -t), (-h, h, -t), (-h, -h, t), (h, -h, t), (h, h, t), (-h, h, t) }) pos.AddRange(new[] { cx + x, cy + y, cz + z });
        idx.AddRange(new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7 });
        for (int b = 0; b < blades; b++)
        {
            double ang = (20.0 + 360.0 * b / blades) * Math.PI / 180.0, ca = Math.Cos(ang), sa = Math.Sin(ang);
            int at = pos.Count / 3;
            foreach (var (rad, off, z) in new[] { ((double)h, -0.08, 0.0), ((double)h, 0.08, 0.0), ((double)radius, 0.0, 0.02), (radius * 0.6, 0.3, 0.02) })
                pos.AddRange(new[] { cx + (float)(rad * ca - off * sa), cy + (float)(rad * sa + off * ca), cz + (float)z });
            idx.AddRange(new[] { at, at + 1, at + 2, at + 1, at + 3, at + 2 });
        }
        Raw(m, "Screw", pos.ToArray(), idx.ToArray());
        return m;
    }

    static BlenderVehicleRig.Args ScrewArgs(string tags = null, string axis = "AUTO")
    {
        var a = Args(tags, axis); Mark(a, "wheels", "Screw"); return a;
    }

    static float[] Head(BlenderRigAnim.Stage1 s, string bone)
    {
        var ml = s.Armature.Rest.MatrixLocal[Node(s, bone)];   // row-major: the translation is the last column
        return new[] { ml[3], ml[7], ml[11] };
    }

    static float[] Axis(BlenderRigAnim.Stage1 s, string bone)
    {
        var ml = s.Armature.Rest.MatrixLocal[Node(s, bone)];   // the bone runs along its local Y: the second column
        return new[] { (float)Math.Round(ml[1], 6), (float)Math.Round(ml[5], 6), (float)Math.Round(ml[9], 6) };
    }

    static void Near(double expected, double actual, double tolerance) => Assert.True(Math.Abs(expected - actual) <= tolerance, "expected " + expected.ToString("R") + " within " + tolerance + ", got " + actual.ToString("R"));

    static int Node(BlenderRigAnim.Stage1 s, string bone) => Assert.Single(s.Armature.BoneNodes.Where(b => s.Names.BoneOfJoint[b] == bone));

    static BlenderRigAnim.Stage1 Stages(HafModel m, BlenderVehicleRig.Args a)
    {
        var s = BlenderVehicleRig.RunStage1(m, a); BlenderVehicleRig.RunStage2(s, a); BlenderVehicleRig.RunStage3(s, a);
        Assert.Null(s.Dies); Assert.Null(s.Fallback); Assert.False(s.Exits); return s;
    }

    static BlenderRigAnim.SceneObject Part(float[] positions, int[] faces, int[] polyStart) => new BlenderRigAnim.SceneObject
    {
        Name = "P", Type = "MESH", World = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
        Mesh = new BlenderRigAnim.MeshData { Vertices = positions.Length / 3, Positions = positions, Faces = faces, PolyStart = polyStart },
    };

    static double[] Centroid(params BlenderRigAnim.SceneObject[] parts)
        => (double[])typeof(BlenderVehicleRig).GetMethod("AreaCentroid", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { parts });

    static HafModel Model() { var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene()); return m; }

    static BlenderVehicleRig.Args Args(string tags = null, string axis = "AUTO")
    {
        var argv = Enumerable.Repeat("", 76).ToList(); argv[0] = "rig"; argv[1] = "scene.glb"; argv[2] = "out.glb"; argv[6] = axis; argv[7] = "15"; argv[8] = "-360"; argv[17] = "0"; argv[21] = "0";
        if (tags != null) argv.Add(tags);
        return BlenderVehicleRig.ParseArgs(argv);
    }

    static void Mark(BlenderVehicleRig.Args a, string role, params string[] names) { a.Names[role] = names.ToList(); foreach (string name in names) a.Marked.Add(name); }

    static void Raw(HafModel m, string name, float[] positions, int[] indices)
    {
        var mesh = new HafMesh { Name = name }; mesh.Primitives.Add(new HafPrimitive { Positions = positions, Indices = indices });
        m.Nodes.Add(new HafNode { Name = name, Mesh = m.Meshes.Count }); m.Meshes.Add(mesh); m.Scenes[0].Nodes.Add(m.Nodes.Count - 1);
    }

    static void Box(HafModel m, string name, float x, float y, float z, float sx = 1, float sy = 1, float sz = 1)
    {
        var points = new List<float>(); foreach (int dx in new[] { -1, 1 }) foreach (int dy in new[] { -1, 1 }) foreach (int dz in new[] { -1, 1 }) points.AddRange(new[] { x + dx * sx / 2, y + dy * sy / 2, z + dz * sz / 2 });
        Raw(m, name, points.ToArray(), new[] { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 });
    }
}
