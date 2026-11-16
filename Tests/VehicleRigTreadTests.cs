using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>The tread rig in C# (part d6, 2026-11-16): what the stage models do with LIVE tracks. Parity with Blender is the
/// vehicle_rig drill's (fx_r_tracks_fallback and fx_r_tracks_recoil: every stage row and the GLB's bytes); these hold the
/// shape of the result and the branches that are still left to Blender BY NAME.</summary>
public class VehicleRigTreadTests
{
    static readonly string[] Carriers = { "Bot", "Top", "RampF", "RampR", "RampRT", "WrapF", "WrapR", "WrapGF", "WrapGR" };

    /// <summary>The drill fixture's layout: a hull, two wheels of 0.8 on the belt's side, two of 0.6 on the other, and a belt
    /// of 8 vertices (4 long, 0.1 high) - no link pitch, so the carrier skinning.</summary>
    static HafModel Tracked()
    {
        var m = Model(); Box(m, "Hull", 0, 0, 0, 3, 2, 2);
        Box(m, "W0", 1.6f, 0, 1.2f, .8f, .8f, .25f); Box(m, "W1", -1.6f, 0, 1.2f, .8f, .8f, .25f);
        Box(m, "W2", 1.6f, 0, -1.2f, .6f, .6f, .25f); Box(m, "W3", -1.6f, 0, -1.2f, .6f, .6f, .25f);
        Box(m, "Belt", 0, -.3f, 1.25f, 4, .1f, .2f);
        return m;
    }

    static BlenderVehicleRig.Args TrackedArgs(params string[] tags)
    {
        var a = Args(tags); Mark(a, "wheels", "W0", "W1", "W2", "W3"); Mark(a, "tracks", "Belt"); return a;
    }

    [Fact]
    public void Live_tracks_get_nine_bones_after_the_wheels_and_a_subdivided_mesh_of_polygons()
    {
        var a = TrackedArgs(); var s = Stages(Tracked(), a, 3);
        Assert.Null(s.Fallback);
        var bones = s.Armature.BoneNodes.Select(b => s.Names.BoneOfJoint[b]).ToList();
        // the belt sits at Blender y = -1.25: the R side; track 0
        Assert.Equal(new[] { "Root", "Wheel_00", "Wheel_01", "Wheel_02", "Wheel_03" }.Concat(Carriers.Select(c => "Track_00_R_" + c)), bones);
        var belt = Mesh(s, "Mesh___track__Belt");
        Assert.NotNull(belt.PolyStart);                       // the subdivision leaves quads and larger faces
        Assert.True(belt.Vertices > 8);
        Assert.Equal(belt.Faces.Length, belt.PolyStart[belt.FaceCount]);
        Assert.All(Enumerable.Range(0, belt.FaceCount), f => Assert.True(belt.CornerCount(f) >= 3));
        Assert.Contains(Enumerable.Range(0, belt.FaceCount), f => belt.CornerCount(f) > 3);
        // a group per carrier bone, by name; every vertex's weights make one
        Assert.Equal(Carriers.Select(c => "Track_00_R_" + c).OrderBy(n => n, StringComparer.Ordinal), belt.Groups);
        // the smoothing normalizes a vertex to 1 and then drops each share of 0.005 or less (at most 8 of the 9 groups): what stays
        // is each above 0.005 and together within 0.04 under 1
        Assert.All(Enumerable.Range(0, belt.Vertices), v => Assert.InRange(belt.DefWeight[v].Sum(), 1f - 8 * .005f, 1f + 1e-5f));
        Assert.All(Enumerable.Range(0, belt.Vertices), v => Assert.All(belt.DefWeight[v], w => Assert.True(w > .005f)));
        Assert.All(Enumerable.Range(0, belt.Vertices), v => Assert.Equal(belt.DefNr[v].Count, belt.DefNr[v].Distinct().Count()));
        Assert.Contains(s.Logs3, l => l.StartsWith("VEHICLE tread 'Belt' subdivided: 8 -> " + belt.Vertices + " verts", StringComparison.Ordinal));
        Assert.Contains(s.Logs3, l => l.StartsWith("VEHICLE tread 'Belt' skinned (carrier blend fallback): ", StringComparison.Ordinal));
        // every other part keeps its triangles
        Assert.All(s.Objects.Where(o => o.Type == "MESH" && o.Name != "Mesh___track__Belt"), o => Assert.Null(o.Mesh.PolyStart));
    }

    [Fact]
    public void The_conveyor_keys_the_wrap_bones_rotation_and_the_carriers_location_and_the_polygons_export_as_triangles()
    {
        var a = TrackedArgs(); var s = Stages(Tracked(), a, 4);
        Assert.Null(s.Fallback);
        var spin = Assert.Single(s.Actions.Where(x => x.Name == "Spin"));
        var curves = spin.Slots.SelectMany(sl => sl.FCurves).ToList();
        foreach (string c in new[] { "WrapF", "WrapR", "WrapGF", "WrapGR" })
            Assert.Equal(3, curves.Count(fc => fc.Bone == "Track_00_R_" + c && fc.Prop == "rotation_euler"));
        foreach (string c in new[] { "Bot", "Top", "RampF", "RampR", "RampRT" })
        {
            Assert.Equal(3, curves.Count(fc => fc.Bone == "Track_00_R_" + c && fc.Prop == "location"));
            Assert.DoesNotContain(curves, fc => fc.Bone == "Track_00_R_" + c && fc.Prop != "location");
        }
        // the bottom run and the top run slide by the same distance, opposite ways (their bones stand alike)
        float End(string bone, int index) => curves.Single(fc => fc.Bone == bone && fc.Prop == "location" && fc.Index == index).Keys.Last().Value;
        var bot = Enumerable.Range(0, 3).Select(i => End("Track_00_R_Bot", i)).ToArray(); var top = Enumerable.Range(0, 3).Select(i => End("Track_00_R_Top", i)).ToArray();
        Assert.Contains(bot, v => v != 0f);
        Assert.Equal(bot.Select(v => -v), top);
        Assert.Contains(s.Logs4, l => l.StartsWith("VEHICLE tread conveyor v5: 1 tread(s), tread quantum -120.0 deg (wheels -360.0)", StringComparison.Ordinal));

        var belt = Mesh(s, "Mesh___track__Belt"); int tris = belt.Faces.Length - 2 * belt.FaceCount;   // a face of n corners makes n - 2 triangles
        var x = BlenderVehicleRig.RunStage5(s, a);
        Assert.Null(s.Fallback); Assert.NotNull(x); Assert.Null(x.Fallback);
        // the exported meshes carry their datablock names; the belt is the one that is not a box of 12 triangles
        var exported = Assert.Single(x.Meshes.Where(me => me.Primitives.Sum(p => p.Indices.Length) / 3 != 12));
        Assert.Equal(tris, exported.Primitives.Sum(p => p.Indices.Length) / 3);
        int all = s.Objects.Where(o => o.Type == "MESH").Sum(o => o.Mesh.Faces.Length - 2 * o.Mesh.FaceCount);
        Assert.Contains(s.Logs5, l => l.Contains(", " + all + " tris across 6 mesh(es)"));
    }

    [Theory]
    [InlineData("double", "stage 5: the double-sided shell of a tread mesh of polygons (d6) is not modelled yet")]
    [InlineData("insideout", "stage 5: the inside-out fix of a tread mesh of polygons (d6) is not modelled yet")]
    public void A_pass_that_reads_triangles_leaves_a_polygon_tread_by_name(string pass, string reason)
    {
        var a = TrackedArgs(); if (pass == "double") a.DoubleSided = true; else a.FixInsideOut = true;
        var s = Stages(Tracked(), a, 4); Assert.Null(s.Fallback);
        Assert.Null(BlenderVehicleRig.RunStage5(s, a));
        Assert.Equal(reason, s.Fallback);
    }

    [Fact]
    public void Rigid_links_only_count_on_tracks_that_run_and_a_belt_without_a_pitch_keeps_its_carriers()
    {
        var stat = Args(new[] { "treadmode=links" }, tracksStatic: true); Mark(stat, "wheels", "W0", "W1", "W2", "W3"); Mark(stat, "tracks", "Belt");
        Assert.True(stat.TracksStatic); Assert.False(stat.TreadLinks);   // the mode only counts on tracks that run
        var s = Stages(Tracked(), stat, 3);
        Assert.Null(s.Fallback);
        Assert.DoesNotContain(s.Armature.BoneNodes, b => s.Names.BoneOfJoint[b].StartsWith("Track_", StringComparison.Ordinal));
        Assert.DoesNotContain(s.Objects, o => o.Mesh?.PolyStart != null);

        // live tracks, rigid links asked, but the 8-vertex belt has no link pitch: nothing to instance, the nine carriers stay
        var links = TrackedArgs("treadmode=links"); Assert.True(links.TreadLinks);
        var live = Stages(Tracked(), links, 4);
        Assert.Null(live.Fallback);
        Assert.Equal(Carriers.Select(c => "Track_00_R_" + c), live.Armature.BoneNodes.Select(b => live.Names.BoneOfJoint[b]).Where(n => n.StartsWith("Track_", StringComparison.Ordinal)));
        Assert.Equal(15, live.FrameEnd);   // no link job: the clip is not stretched to a circuit
    }

    /// <summary>Rigid links on a belt with a link pitch: one bone per link, no shuttles; the band is rebuilt from copies of one link
    /// (so a link's vertices all sit on its own bone, and the source's custom normals are gone); every link keyed as a quaternion on
    /// every frame of a clip that is a whole number of spin cycles long.</summary>
    [Fact]
    public void Rigid_links_give_one_bone_per_link_and_key_the_whole_circuit()
    {
        var m = Tracked(); Ladder(m);
        var a = TrackedArgs("treadmode=links"); var s = Stages(m, a, 4);
        Assert.Null(s.Fallback);
        var bones = s.Armature.BoneNodes.Select(b => s.Names.BoneOfJoint[b]).ToList();
        var links = bones.Where(n => n.StartsWith("Track_00_R_L", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(links);
        Assert.Equal(new[] { "Root", "Wheel_00", "Wheel_01", "Wheel_02", "Wheel_03" }.Concat(links), bones);   // no carriers, no shuttles
        Assert.True(bones.Count <= 126);
        Assert.Contains(s.Logs3, l => l.StartsWith("VEHICLE tread 'Belt' RIGID LINKS instanced: " + links.Count + " link(s) x 1 cell(s), each a copy of link ", StringComparison.Ordinal));
        var mesh = Mesh(s, "Mesh___track__Belt");
        Assert.Null(mesh.CustomNormal);
        Assert.All(Enumerable.Range(0, mesh.Vertices), v => Assert.Equal(new[] { 1f }, mesh.DefWeight[v]));
        // rigid: no face spans two links
        for (int f = 0; f < mesh.FaceCount; f++)
            Assert.Single(Enumerable.Range(mesh.CornerStart(f), mesh.CornerCount(f)).Select(c => mesh.DefNr[mesh.Faces[c]][0]).Distinct());
        // the clip: the spin's 15 frames times a whole number of cycles; every link keyed on every frame, location and quaternion
        Assert.True(s.FrameEnd >= 15 && s.FrameEnd % 15 == 0);
        var curves = s.Actions.Single(x => x.Name == "Spin").Slots.SelectMany(sl => sl.FCurves).ToList();
        foreach (string n in links)
        {
            var mine = curves.Where(fc => fc.Bone == n).ToList();
            Assert.Equal(new[] { "location", "location", "location", "rotation_quaternion", "rotation_quaternion", "rotation_quaternion", "rotation_quaternion" }, mine.Select(fc => fc.Prop));
            Assert.All(mine, fc => Assert.Equal(s.FrameEnd + 1, fc.Keys.Count));
            // the whole loop: a link is back where it started on the last frame
            foreach (var fc in mine.Where(c => c.Prop == "location")) Assert.Equal(fc.Keys[0].Value, fc.Keys[fc.Keys.Count - 1].Value, 4);
        }
        Assert.Contains(s.Logs4, l => l.StartsWith("VEHICLE tread 'Belt' RIGID LINKS circuit: " + links.Count + " link bones, the whole loop ", StringComparison.Ordinal));
    }

    /// <summary>The script's own error: with fewer than four bones left for a track's links it prints a VEHICLE ERROR and dies. 122
    /// wheels far apart leave (126 - (1 root + 122 wheels + 9 carriers) + 9) // 1 = 3.</summary>
    [Fact]
    public void Rigid_links_without_four_bones_to_spare_die_as_the_script_does()
    {
        var m = Tracked(); Ladder(m);
        var extra = new List<string>();
        for (int i = 0; i < 118; i++) { string n = "X" + i; Box(m, n, 10f + 3f * i, 0, 1.2f, .5f, .5f, .25f); extra.Add(n); }
        var a = Args(new[] { "treadmode=links" }); Mark(a, "wheels", new[] { "W0", "W1", "W2", "W3" }.Concat(extra).ToArray()); Mark(a, "tracks", "Belt");
        var s = BlenderVehicleRig.RunStage1(m, a); BlenderVehicleRig.RunStage2(s, a); BlenderVehicleRig.RunStage3(s, a);
        Assert.Null(s.Fallback);
        const string err = "rigid links need at least 4 bones per track; the 126-bone budget leaves 3. Mark fewer separate animated parts.";
        Assert.Equal("RuntimeError: " + err, s.Dies);
        Assert.Equal("VEHICLE ERROR: " + err, s.Logs3.Last());
    }

    /// <summary>A belt whose bottom run repeats every 0.1 along x has a link pitch: the tread is cut into cells along its path, the
    /// cells on the wraps get a bone each and the straight runs a shuttle bone; the nine carriers are gone.</summary>
    [Fact]
    public void A_tread_with_a_link_pitch_gets_link_and_shuttle_bones_keyed_every_frame()
    {
        var m = Tracked(); Ladder(m);
        var a = TrackedArgs(); var s = Stages(m, a, 4);
        Assert.Null(s.Fallback);
        // the measurement itself: 0.1 is the largest strong period, so the physical link; the pitch is the smallest strong one
        string line = Assert.Single(s.Logs3.Where(l => l.StartsWith("VEHICLE tread 'Belt' link pitch: ", StringComparison.Ordinal)));
        Assert.Contains("physical link 0.100 (from", line);
        var bones = s.Armature.BoneNodes.Select(b => s.Names.BoneOfJoint[b]).ToList();
        Assert.DoesNotContain(bones, n => Carriers.Any(c => n == "Track_00_R_" + c));
        var links = bones.Where(n => n.StartsWith("Track_00_R_L", StringComparison.Ordinal)).ToList(); var shuttles = bones.Where(n => n.StartsWith("Track_00_R_S", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(links);
        Assert.Equal(new[] { "Root", "Wheel_00", "Wheel_01", "Wheel_02", "Wheel_03" }.Concat(links).Concat(shuttles), bones);   // the new bones after every other, links before shuttles
        Assert.Contains(s.Logs3, l => l == "VEHICLE armature: " + bones.Count + " bones total (budget 126: bone indices break past 127)");
        // every vertex whole on one bone of the rig; no bone without a vertex (a bone without weights is dropped in the game's bake)
        var mesh = Mesh(s, "Mesh___track__Belt");
        Assert.All(Enumerable.Range(0, mesh.Vertices), v => Assert.Equal(new[] { 1f }, mesh.DefWeight[v]));
        Assert.Equal(links.Concat(shuttles).OrderBy(n => n, StringComparer.Ordinal), mesh.Groups.OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(Enumerable.Range(0, mesh.Groups.Count), g => Assert.Contains(Enumerable.Range(0, mesh.Vertices), v => mesh.DefNr[v][0] == g));
        // a link bone is keyed on every frame of the clip (0..15), location and rotation; a shuttle bone at both ends, location only
        var curves = s.Actions.Single(x => x.Name == "Spin").Slots.SelectMany(sl => sl.FCurves).ToList();
        foreach (string n in links)
        {
            var mine = curves.Where(fc => fc.Bone == n).ToList();
            Assert.Equal(new[] { "location", "location", "location", "rotation_euler", "rotation_euler", "rotation_euler" }, mine.Select(fc => fc.Prop));
            Assert.All(mine, fc => Assert.Equal(Enumerable.Range(0, 16).Select(f => (float)f), fc.Keys.Select(k => k.Frame)));
        }
        foreach (string n in shuttles)
        {
            var mine = curves.Where(fc => fc.Bone == n).ToList();
            Assert.Equal(new[] { "location", "location", "location" }, mine.Select(fc => fc.Prop));
            Assert.All(mine, fc => Assert.Equal(new[] { 0f, 15f }, fc.Keys.Select(k => k.Frame)));
        }
        var x = BlenderVehicleRig.RunStage5(s, a);
        Assert.Null(s.Fallback); Assert.NotNull(x); Assert.Null(x.Fallback);
    }

    [Fact]
    public void A_polygon_mesh_the_exporters_validate_would_change_is_refused()
    {
        var me = new BlenderRigAnim.MeshData
        {
            Vertices = 4, Positions = new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }, Faces = new[] { 0, 1, 2, 3, 3, 2, 1, 0 }, PolyStart = new[] { 0, 4, 8 },
            FaceMaterial = new[] { 0, 0 }, FaceSharp = new bool[2], Edges = new[] { 0, 1, 1, 2, 2, 3, 3, 0 },
        };
        Assert.Null(BlenderRigExport.PolygonResult(me, out _, out string why));
        Assert.Contains("validate()", why);
        me.Faces = new[] { 0, 1, 2, 3 }; me.PolyStart = new[] { 0, 4 }; me.FaceMaterial = new[] { 0 }; me.FaceSharp = new bool[1];
        var r = BlenderRigExport.PolygonResult(me, out var normals, out why);
        Assert.Null(why); Assert.Equal(2, r.FaceCount); Assert.Equal(18, normals.Length);
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, r.Faces);   // a flat quad splits along its first diagonal
        Assert.All(Enumerable.Range(0, 6), c => Assert.Equal(new[] { 0f, 0f, 1f }, normals.Skip(3 * c).Take(3)));
    }

    /// <summary>Replaces the belt: a ladder of 41 rungs along x (pitch 0.1) at the bottom, and a top rail far above so the rungs are "the bottom run".</summary>
    static void Ladder(HafModel m)
    {
        var pts = new List<float>(); var idx = new List<int>();
        for (int i = 0; i <= 40; i++) foreach (float w in new[] { 1.15f, 1.35f }) pts.AddRange(new[] { -2f + .1f * i, -.35f, w });
        for (int i = 0; i < 40; i++) { int q = 2 * i; idx.AddRange(new[] { q, q + 2, q + 1, q + 1, q + 2, q + 3 }); }
        int top = pts.Count / 3; pts.AddRange(new[] { -2f, .45f, 1.15f, 2f, .45f, 1.15f, 0f, .45f, 1.35f }); idx.AddRange(new[] { top, top + 1, top + 2 });
        var belt = m.Meshes.Single(me => me.Name == "Belt").Primitives[0]; belt.Positions = pts.ToArray(); belt.Indices = idx.ToArray();
    }

    static BlenderRigAnim.Stage1 Stages(HafModel m, BlenderVehicleRig.Args a, int upTo)
    {
        var s = BlenderVehicleRig.RunStage1(m, a); BlenderVehicleRig.RunStage2(s, a);
        if (upTo >= 3) BlenderVehicleRig.RunStage3(s, a);
        if (upTo >= 4) BlenderVehicleRig.RunStage4(s, a);
        Assert.Null(s.Dies); return s;
    }
    static BlenderRigAnim.MeshData Mesh(BlenderRigAnim.Stage1 s, string name) => Assert.Single(s.Objects.Where(o => o.Name == name)).Mesh;
    static HafModel Model() { var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene()); return m; }
    static BlenderVehicleRig.Args Args(string[] tags = null, bool tracksStatic = false)
    {
        var argv = Enumerable.Repeat("", 76).ToList(); argv[0] = "rig"; argv[1] = "scene.glb"; argv[2] = "out.glb"; argv[7] = "15"; argv[8] = "-360"; argv[17] = "0"; argv[21] = "0";
        if (tracksStatic) argv[16] = "1";
        if (tags != null) argv.AddRange(tags);
        return BlenderVehicleRig.ParseArgs(argv);
    }
    static void Mark(BlenderVehicleRig.Args a, string role, params string[] names) { a.Names[role] = names.ToList(); foreach (string name in names) a.Marked.Add(name); }
    static void Box(HafModel m, string name, float x, float y, float z, float sx = 1, float sy = 1, float sz = 1)
    {
        var points = new List<float>(); foreach (int dx in new[] { -1, 1 }) foreach (int dy in new[] { -1, 1 }) foreach (int dz in new[] { -1, 1 }) points.AddRange(new[] { x + dx * sx / 2, y + dy * sy / 2, z + dz * sz / 2 });
        var mesh = new HafMesh { Name = name }; mesh.Primitives.Add(new HafPrimitive { Positions = points.ToArray(), Indices = new[] { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 } });
        m.Nodes.Add(new HafNode { Name = name, Mesh = m.Meshes.Count }); m.Meshes.Add(mesh); m.Scenes[0].Nodes.Add(m.Nodes.Count - 1);
    }
}
