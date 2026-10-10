using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>BlenderDeploy.Decide: the decisions deploy_convert.py takes before it builds anything. tools/deploy_drill.sh
/// holds whole jobs to the script itself (its log, its decisions, the scene it leaves, to the bit); here each rule on
/// a model small enough to read, and Python's number formatting.</summary>
public class BlenderDeployTests
{
    const string Default = "0|24|||||||0|||4|0|1";

    sealed class Scene
    {
        public readonly HafModel M = new HafModel();
        readonly HafAnimation anim = new HafAnimation { Name = "Deploy" };

        public int Node(string name, int parent = -1, bool mesh = false, double[] t = null, double[] s = null, string meshName = null)
        {
            var n = new HafNode { Name = name, Parent = parent };
            if (t != null) n.Translation = t;
            if (s != null) n.Scale = s;
            if (mesh)
            {
                var p = new HafPrimitive { VertexCount = 3, Positions = new float[] { -1, 0, 0, 1, 0, 0, 0, 1, 0.5f } };   // two across, standing on the ground, about the origin
                var hm = new HafMesh { Name = meshName ?? name.ToLowerInvariant() + "_mesh" }; hm.Primitives.Add(p);
                M.Meshes.Add(hm); n.Mesh = M.Meshes.Count - 1;
            }
            M.Nodes.Add(n);
            if (parent >= 0) M.Nodes[parent].Children.Add(M.Nodes.Count - 1);
            return M.Nodes.Count - 1;
        }

        public void Move(int node, float seconds = 1f, float[] to = null)
        {
            var t = M.Nodes[node].Translation; to = to ?? new[] { (float)t[0] + 1f, (float)t[1], (float)t[2] };
            anim.Samplers.Add(new HafSampler { Times = new[] { 0f, seconds }, Values = new[] { (float)t[0], (float)t[1], (float)t[2], to[0], to[1], to[2] }, Components = 3, Interpolation = "LINEAR" });
            anim.Channels.Add(new HafChannel { Node = node, Path = "translation", Sampler = anim.Samplers.Count - 1 });
        }

        public void Scale(int node, params float[] keys)
        {
            anim.Samplers.Add(new HafSampler { Times = Enumerable.Range(0, keys.Length / 3).Select(i => i * 14f / 24f).ToArray(), Values = keys, Components = 3, Interpolation = "LINEAR" });
            anim.Channels.Add(new HafChannel { Node = node, Path = "scale", Sampler = anim.Samplers.Count - 1 });
        }

        public BlenderDeploy.Result Decide(string args = Default)
        {
            if (M.Scenes.Count == 0)
            {
                var sc = new HafScene(); sc.Nodes.AddRange(Enumerable.Range(0, M.Nodes.Count).Where(i => M.Nodes[i].Parent < 0)); M.Scenes.Add(sc); M.Scene = 0;
                M.Animations.Add(anim);
            }
            return BlenderDeploy.Decide(M, args.Split('|'));
        }
    }

    [Fact]
    public void The_strip_reads_object_and_mesh_names_and_a_removed_objects_child_becomes_a_root()
    {
        var s = new Scene();
        int hull = s.Node("Hull", mesh: true);
        int crew = s.Node("Crew_Soldier", hull, mesh: true, t: new double[] { 0, 3, 0 });
        int hat = s.Node("Hat", crew, mesh: true, t: new double[] { 0, 1, 0 });
        s.Node("Prop", hull, mesh: true, meshName: "ShellCase");
        int gun = s.Node("Gun", hull, mesh: true); s.Move(gun);
        var r = s.Decide();
        Assert.Null(r.Fallback);
        // by name ignoring case: Gun, Hat, Hull - the crew and the prop (by its MESH's name) are gone
        Assert.Equal(new[] { "Gun", "Hat", "Hull" }, r.Objects.Where(o => o.Type != "ARMATURE").Select(o => o.Name));
        var hatObj = r.Objects.First(o => o.Name == "Hat"); Assert.Null(hatObj.Parent);
        // the hat keeps its own transform: where its parent was no longer counts (glTF y 1 is Blender z 1)
        Assert.Equal(1f, hatObj.World[14]); Assert.Equal(0f, hatObj.World[13]);
        Assert.Equal("DEPLOY after strip: 3 objects, 3 meshes: Gun, Hat, Hull", r.Log[0]);
        Assert.Equal(new[] { "Gun" }, r.Parts.Select(p => p.Name));
        Assert.True(r.Legacy);
        Assert.Contains($"   part: {"Gun",-40} parent=Hull", r.Log);
    }

    [Fact]
    public void A_strip_list_given_replaces_the_default_one_and_an_empty_name_in_it_takes_everything()
    {
        Scene Make() { var s = new Scene(); int hull = s.Node("Hull", mesh: true); s.Node("Soldier", hull, mesh: true); s.Move(s.Node("Gun", hull, mesh: true)); return s; }
        Assert.Equal(new[] { "Gun", "Hull" }, Make().Decide().Objects.Where(o => o.Type != "ARMATURE").Select(o => o.Name));   // the default list takes the soldier
        Assert.Equal(new[] { "Hull", "Soldier" }, Make().Decide("0|24| GUN ||||||0|||4|0|1").Objects.Where(o => o.Type != "ARMATURE").Select(o => o.Name));
        // stripExtra (the script's argv[16]) comes on top of the default list
        Assert.Equal(new[] { "Hull" }, Make().Decide("0|24|||||||0|||4|0|1|gun").Objects.Where(o => o.Type != "ARMATURE").Select(o => o.Name));
        var all = Make().Decide("0|24|gun,,zzz||||||0|||4|0|1");
        Assert.Empty(all.Objects); Assert.True(all.Exit);
        Assert.Equal(1, all.FrameMin); Assert.Equal(1, all.FrameMax);   // no action left: 1..1
    }

    [Fact]
    public void The_frame_range_is_the_actions_cut_to_whole_frames_with_a_removed_objects_curves_in_it()
    {
        var s = new Scene();
        int hull = s.Node("Hull", mesh: true);
        s.Move(s.Node("Gun", hull, mesh: true), 0.98f);          // frame 23.52
        s.Move(s.Node("Soldier", hull, mesh: true), 1.53f);      // frame 36.72 - stripped, and still in the action
        var r = s.Decide();
        Assert.Equal(0, r.FrameMin); Assert.Equal(36, r.FrameMax);
        Assert.Contains("DEPLOY frame range: 0..36", r.Log);
    }

    [Fact]
    public void A_model_under_half_a_unit_is_scaled_a_hundredfold_and_one_parked_off_the_origin_is_recentred()
    {
        var s = new Scene();
        int hull = s.Node("Hull", mesh: true, s: new double[] { 0.1, 0.1, 0.1 });     // 0.2 across
        s.Move(s.Node("Gun", hull, mesh: true));
        var r = s.Decide();
        Assert.Equal(100.0, r.NormScale); Assert.False(r.Recenter);
        Assert.Equal("UnitNormalize", r.Objects.Last().Name);
        Assert.Equal(r.Objects.Last(), r.Objects.First(o => o.Name == "Hull").Parent);
        Assert.Equal(10f, r.Objects.First(o => o.Name == "Hull").World[0], 5);        // 0.1 x 100
        Assert.Contains(r.Log, l => l.StartsWith("DEPLOY normalization: dim 0.200 -> x100 scale") && !l.Contains("recentered"));

        var far = new Scene();
        int h2 = far.Node("Hull", mesh: true, t: new double[] { 40, 0, 0 });
        far.Move(far.Node("Gun", h2, mesh: true));
        var r2 = far.Decide();
        Assert.Equal(1.0, r2.NormScale); Assert.True(r2.Recenter);
        // the root takes the centre back to the origin and the lowest point to the ground: 39..41 in x
        Assert.Equal(-40f, r2.Objects.Last().Loc[0]);
        Assert.Contains("DEPLOY normalization: dim 2.000 -> x1 scale, recentered (offset was h=40.00 v=0.00)", r2.Log);
    }

    [Fact]
    public void A_part_whose_scale_passes_through_zero_on_a_sampled_frame_is_culled_with_what_hangs_from_it()
    {
        var s = new Scene();
        s.Node("Hull", mesh: true);
        s.Move(s.Node("Good", mesh: true));
        int flat = s.Node("Flat", mesh: true); s.Scale(flat, 1, 1, 1, 1, 0, 1, 1, 1, 1);   // zero at frame 14, back at 28
        s.Node("Below", flat, mesh: true);
        var r = s.Decide();
        Assert.Equal(new[] { "Flat" }, r.Bad);
        Assert.Equal(new[] { "Good", "Hull" }, r.Objects.Where(o => o.Type != "ARMATURE").Select(o => o.Name));
        Assert.Contains("DEPLOY culled 1 degenerate part(s) (garbage world matrix): ['Flat']  (+1 descendant object(s))", r.Log);
    }

    [Fact]
    public void Over_the_wall_only_binding_targets_keep_a_bone_and_big_classes_are_pair_merged_down_to_the_budget()
    {
        var s = new Scene();
        s.Node("Hull", mesh: true);
        for (int i = 0; i < 130; i++) s.Move(s.Node(i == 0 ? "Link" : $"Link.{i:000}", mesh: true));
        for (int i = 0; i < 6; i++) s.Move(s.Node(i == 0 ? "Few" : $"Few.{i:000}", mesh: true));
        for (int i = 0; i < 5; i++) s.Move(s.Node("Wrap" + i));                       // animated, nothing hangs from them
        var r = s.Decide();
        Assert.Contains("DEPLOY bone slimming: kept 136 binding-target node(s), skipped 5 wrapper/ancestor node(s) — over the 124-bone wall (world-space keys carry the ancestors' motion)", r.Log);
        Assert.False(r.Legacy);
        Assert.Equal(12, r.Alias.Count); Assert.Equal(124, r.Parts.Count);
        // every rider is an odd member of the big class and rides the member before it; the small class is left alone
        Assert.All(r.Alias, a => Assert.StartsWith("Link", a.dropped.Name));
        Assert.Equal(("Link.001", "Link"), (r.Alias[0].dropped.Name, r.Alias[0].kept.Name));
        Assert.Equal(6, r.Parts.Count(p => p.Name.StartsWith("Few")));
    }

    [Fact]
    public void The_armature_has_a_bone_per_part_at_the_parts_place_and_StaticRoot_for_what_no_bone_carries()
    {
        var s = new Scene();
        int hull = s.Node("Hull", mesh: true);
        int gun = s.Node("Gun", hull, mesh: true, t: new double[] { 0, 2, 0 }); s.Move(gun, 1f, new[] { 0f, 2f, 0.01f });
        int sight = s.Node("Sight", gun, mesh: true, t: new double[] { 0, 0.5, 0 }); s.Move(sight, 1f, new[] { 0f, 0.5f, 0.01f });
        var r = s.Decide();
        Assert.Null(r.Fallback);
        Assert.Equal("DeployArm", r.Armature.Name);                 // the legacy path's name
        Assert.Equal(new[] { "Gun", "Sight", "StaticRoot" }, r.Bones.Select(b => b.Name));
        Assert.Null(r.Bones[0].Parent); Assert.Same(r.Bones[0], r.Bones[1].Parent); Assert.Null(r.Bones[2].Parent);
        // glTF y is Blender z: the gun's bone stands at height 2, its tail 0.1 above; the sight's at 2.5
        Assert.Equal(new[] { 0f, 0f, 2f }, r.Bones[0].Head); Assert.Equal((float)(2f + 0.1f), r.Bones[0].Tail[2]);
        Assert.Equal(2.5f, r.Bones[1].Head[2]);
        Assert.Equal(new[] { 0f, 0f, 0f }, r.Bones[2].Head);
        Assert.Equal(new[] { ("Gun", "Gun"), ("Sight", "Sight") }, r.BoneOf);
        // the hull has no part above it: StaticRoot is anchored to it - a mesh without a parent, so to the mesh itself
        Assert.Same(r.Objects.First(o => o.Name == "Hull"), r.StaticAnchor);
        Assert.Contains("DEPLOY StaticRoot baked against 'Hull' (static geometry scale anchor)", r.Log);
        // the biggest mesh on a bone hardly moves (0.01 against a model of 2): no root-motion anchor
        Assert.True(r.TravelMeasured); Assert.Null(r.Hull); Assert.Null(r.Armature.Parent);
    }

    [Fact]
    public void A_part_that_travels_becomes_the_root_motion_anchor_and_bone_names_are_made_unique()
    {
        var s = new Scene();
        s.Node("Hull", mesh: true);
        s.Move(s.Node("StaticRoot", mesh: true), 1f, new[] { 3f, 0f, 0f });   // travels 3 units; and takes the name
        var r = s.Decide();
        Assert.Equal(new[] { "StaticRoot", "StaticRoot.001" }, r.Bones.Select(b => b.Name));
        Assert.Equal("StaticRoot", r.Hull.Name);
        Assert.Equal(3.0, r.Travel, 5);
        Assert.Same(r.Hull, r.Armature.Parent);
        // the parent inverse pins the armature where it was at the bind frame: the identity
        for (int i = 0; i < 16; i++) Assert.Equal(i % 5 == 0 ? 1f : 0f, r.Armature.World[i], 5);
        Assert.Contains(r.Log, l => l.StartsWith("DEPLOY root-motion anchor: 'StaticRoot' travels 3.00 units (model 2.00)"));
    }

    [Fact]
    public void The_bake_gives_every_bone_a_key_a_frame_in_its_own_space()
    {
        var s = new Scene();
        int hull = s.Node("Hull", mesh: true);
        int gun = s.Node("Gun", hull, mesh: true, t: new double[] { 0, 2, 0 }); s.Move(gun, 1f, new[] { 0f, 2f, 0.024f });   // 0.001 a frame along glTF z
        s.Decide();   // builds the scene and the animation
        var r = BlenderDeploy.Decide(s.M, Default.Split('|'), null, true);
        Assert.Null(r.Fallback);
        Assert.Equal(new[] { "Gun", "StaticRoot" }, r.Keys.Keys.OrderBy(k => k));
        var k = r.Keys["Gun"];
        Assert.Equal(25, k.Length);                                  // frames 0..24
        Assert.All(k, key => Assert.Equal(10, key.Length));           // location 3, quaternion 4, scale 3
        // the bone stands at the part's place and points up Z: in its own space the part is turned a quarter back
        // (w = sqrt(1/2)) and its move along glTF z - Blender's -y - runs along the bone's own Z
        Assert.Equal(0f, k[0][2], 6); Assert.Equal(0.012f, Math.Abs(k[12][2]), 5); Assert.Equal(0.024f, Math.Abs(k[24][2]), 5);
        Assert.All(k, key => { Assert.Equal(0f, key[0], 5); Assert.Equal(0f, key[1], 5); Assert.Equal(0.70711f, key[3], 4); Assert.Equal(1f, key[7], 5); });
        Assert.True(r.ScaleKeys);                                    // the legacy path keeps the scale curves
        Assert.Equal(new[]
        {
            "DEPLOY scale-free rig: SKIPPED (legacy path keeps the cm-verts x0.01 pose scale)",
            "DEPLOY delta-form rebase: SKIPPED (legacy path — pre-contract engine handling renders absolute poses correctly; bind==f0 would fold the legs' rest and cross them)",
            "DEPLOY baked 1 bones",
        }, r.BakeLog);
        // without `bake` the conversion stops before it
        Assert.Empty(s.Decide().Keys);
    }

    [Fact]
    public void The_fire_window_is_snapshot_frame_by_frame_and_a_range_the_script_cannot_read_is_left_to_Blender()
    {
        Scene Make() { var s = new Scene(); int hull = s.Node("Hull", mesh: true); s.Move(s.Node("Gun", hull, mesh: true, t: new double[] { 0, 2, 0 }), 1f, new[] { 0f, 2f, 0.024f }); s.Decide(); return s; }
        // starts "3,10", ends "5,12/2", the step on: frames 3..5 and 10..12; the bones as the baked action has them there
        var r = BlenderDeploy.Decide(Make().M, "0|24|||||3,10|5,12/2|1|||0|0|1".Split('|'), null, true);
        Assert.Null(r.Fallback);
        Assert.Equal(new[] { (3, 5, 1), (10, 12, 2) }, r.Segments);
        Assert.Equal(new[] { 3, 4, 5, 10, 11, 12 }, r.FireSnap.Keys);
        Assert.Equal(r.Keys["Gun"][11].Take(7), r.FireSnap[11]["Gun"]);
        Assert.Equal(r.Keys["Gun"][12], r.ArmPose["Gun"]);                 // the last frame set is what the bone holds
        Assert.Equal(new[] { "DEPLOY fire-window snapshot: 6 frames (3..5/1, 10..12/2) captured PRISTINE (pre-retarget)" }, r.FireLog);
        // the step off (argv[10] empty or "0"): the range is blanked, no snapshot
        Assert.Empty(BlenderDeploy.Decide(Make().M, "0|24|||||3|5|0|||0|0|1".Split('|'), null, true).FireSnap);
        // Python's int() takes white space but not the separators str.strip() removes: the script dies on such a start
        Assert.Contains("cannot read", BlenderDeploy.Decide(Make().M, "0|24|||||\u001f3|5|1|||0|0|1".Split('|'), null, true).Fallback);
        Assert.Contains("cannot read", BlenderDeploy.Decide(Make().M, "0|24|||||3|5/2/3|1|||0|0|1".Split('|'), null, true).Fallback);
        Assert.Null(BlenderDeploy.Decide(Make().M, "0|24||||| +3 | 5 / 2 |1|||0|0|1".Split('|'), null, true).Fallback);
    }

    [Fact]
    public void The_barrel_is_rekeyed_to_its_scaled_ready_pose_and_the_legs_to_a_scaled_spread()
    {
        Scene Make()
        {
            var s = new Scene(); int hull = s.Node("Hull", mesh: true);
            s.Move(s.Node("Barrel", hull, mesh: true, t: new double[] { 0, 2, 0 }), 1f, new[] { 0f, 2f, 0.024f });
            s.Move(s.Node("Leg", hull, mesh: true, t: new double[] { 1, 0, 0 }), 1f, new[] { 1f, 0f, 0.048f });
            s.Decide(); return s;
        }
        BlenderDeploy.Result Run(string args) => BlenderDeploy.Decide(Make().M, args.Split('|'), null, true);
        // argv[5] the ready frame, argv[7] the barrel scale: the barrel's curves go, a rest key at the mid frame
        // (int(24 / 2)) and the ready pose - its turn and its move doubled - at the end
        var r = Run("0|24||24||2|||0|||4|0|1");
        Assert.Null(r.Fallback);
        Assert.Equal(new[] { "DEPLOY barrel retargeted to ready-frame 24 over 12..24 (1 bones)" }, r.RetargetLog);
        var b = r.Rekeyed["Barrel"]; var baked = r.Keys["Barrel"][24];
        for (int c = 0; c < 7; c++)
        {
            Assert.Equal(new[] { 12f, 24f }, b[c].Select(k => k.Frame));
            Assert.Equal(c == 3 ? 1f : 0f, b[c][0].Value);
            // flat handles a third of the way to the neighbour
            Assert.All(b[c], k => { Assert.Equal(k.Value, k.LeftY); Assert.Equal(k.Value, k.RightY); Assert.Equal(k.Frame - 4f, k.LeftX, 4); Assert.Equal(k.Frame + 4f, k.RightX, 4); });
            // what the bone HOLDS afterwards is the end pose assigned last: nothing evaluates the new curve
            Assert.Equal(b[c][1].Value, r.ArmPose["Barrel"][c]);
        }
        for (int c = 0; c < 3; c++) Assert.Equal((float)(baked[c] * 2f), b[c][1].Value);
        Assert.Equal(0f, b[3][1].Value, 5);                          // a quarter turn doubled: w = cos(90 degrees)
        Assert.Null(b[7]); Assert.Null(b[8]); Assert.Null(b[9]);     // the scale curves are cleared and not keyed again
        Assert.False(r.Rekeyed.ContainsKey("Leg"));
        // the same with the ready frame before the end: the bone still holds the end pose, not the curve at frame 3
        var early = Run("0|24||3||2|||0|||4|0|1");
        Assert.Equal(early.Rekeyed["Barrel"][2][1].Value, early.ArmPose["Barrel"][2]);
        // argv[6] the leg scale: the quaternion keyed at the first frame, at the spread frame and at the end; the
        // location's curves cleared
        var l = Run("0|24|||0.5||||0|||4|0|1");
        Assert.Null(l.Fallback);
        Assert.Equal(new[] { "DEPLOY legs scaled x0.50 from initial (1 bones), spread by 12 held to 24" }, l.RetargetLog);
        for (int c = 3; c < 7; c++) Assert.Equal(new[] { 0f, 12f, 24f }, l.Rekeyed["Leg"][c].Select(k => k.Frame));
        Assert.Null(l.Rekeyed["Leg"][0]);
        // Python's float(): a negative zero keeps its sign (the log prints it); what it refuses, and a scale
        // Quaternion.slerp refuses, are left to Blender
        Assert.StartsWith("DEPLOY legs scaled x-0.00 ", Run("0|24|||-0||||0|||4|0|1").RetargetLog.Single());
        Assert.Null(Run("0|24||24| .5 |+1.E0|||0|||4|0|1").Fallback);
        Assert.Contains("outside 0..1", Run("0|24|||1.5||||0|||4|0|1").Fallback);
        Assert.Contains("float()", Run("0|24|||1e||||0|||4|0|1").Fallback);
        Assert.Contains("float()", Run("0|24||24||nan|||0|||4|0|1").Fallback);
        Assert.Contains("past a float", Run("0|24||24||1e39|||0|||4|0|1").Fallback);
        // a negative end: the legs' first frame_set lands between the barrel's two new keys (-5 and 1) - the curve is
        // evaluated there, and frame after frame the barrel moves from its ready pose to its rest
        var neg = Run("0|-5||24|0.5|2|||0|||4|0|1");
        Assert.Null(neg.Fallback);
        var k2 = neg.Rekeyed["Barrel"][2];
        Assert.Equal(new[] { -5f, 1f }, k2.Select(k => k.Frame));
        float before = neg.ArmAt(-9)["Barrel"][2], nearFirst = neg.ArmAt(-4)["Barrel"][2], mid = neg.ArmAt(-2)["Barrel"][2], late = neg.ArmAt(0)["Barrel"][2], after = neg.ArmAt(7)["Barrel"][2];
        Assert.Equal(k2[0].Value, before); Assert.Equal(k2[1].Value, after);
        Assert.Equal((k2[0].Value + k2[1].Value) / 2f, mid, 6);                 // flat handles a third out: the middle is the mean
        Assert.True(Math.Abs(nearFirst - k2[0].Value) < Math.Abs(mid - k2[0].Value) && Math.Abs(late - k2[1].Value) < Math.Abs(mid - k2[1].Value));
        Assert.True(Math.Abs(nearFirst - k2[0].Value) < Math.Abs(k2[1].Value - k2[0].Value) / 6f);   // eased: slower than a straight line near the key
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void The_last_frame_probe_evaluates_the_same_clamped_frame_as_the_pose_probe(int frame)
    {
        var s = new Scene(); s.Node("Hull", mesh: true);
        s.Move(s.Node("Barrel", mesh: true), 1f, new[] { 1f, 0f, 0f });
        s.Move(s.Node("Leg", mesh: true), 1f, new[] { 0f, 1f, 0f });
        s.Decide();
        // A bake beginning after zero exposes subtraction overflow at int.MinValue. A re-keyed barrel whose
        // first key lies beyond Blender's scene limit exposes evaluation at the raw int.MaxValue.
        foreach (var sampler in s.M.Animations[0].Samplers) sampler.Times = sampler.Times.Select(t => t + 1f).ToArray();
        var r = BlenderDeploy.Decide(s.M, "0|2147483647||48||1|||0|||4|0|1".Split('|'), null, true);
        Assert.Null(r.Fallback); Assert.Equal(24, r.BakeFrameMin);
        var expected = r.ArmAt(Math.Max(-1048574, Math.Min(1048574, frame)));
        r.ProbeAt(frame);
        int Bits(float v) => BitConverter.ToInt32(BitConverter.GetBytes(v), 0);
        foreach (var bone in expected.Keys)
            Assert.Equal(expected[bone].Select(Bits), r.ArmPose[bone].Select(Bits));
    }

    [Theory]
    // rows of Blender 5.1.2's own handle calculation (tools/deploy-drill/blender_handles_dump.py, seed 1): every key
    // AUTO_CLAMPED, as frame:value:left handle:right handle in float hex
    // CONSTANT extrapolation, a plateau and a step: every key an extreme or an end - all flat
    [InlineData("C 43dc8000:00000000:436a9d65:00000000:4421d8a7:00000000 448484fa:00000000:4455714d:00000000:44863ebc:00000000 4489b23f:00000000:4487f87d:00000000:448ab17a:00000000 448caff0:40400000:448bb0b5:40400000:448daf2b:40400000")]
    // a rising run between flat ends, the intervals far from even: the two middle keys are solved together
    [InlineData("C 3f800000:3dcccccd:3f555555:3dcccccd:3f955555:3dcccccd 3fc00000:3eade21b:3faaaaab:3eadd770:44d08555:3f93a238 459c4c00:3f93a238:45506d56:3eecc800:45d06155:3fec1270 461c4600:407ab18b:46023b55:407ab18b:463650ab:407ab18b")]
    // LINEAR extrapolation: the ends are free - solved with the run, their outer handle a mirror of the inner one
    [InlineData("L 453b8000:42c80000:453b7aab:42aa3f5e:453b8555:42e5c0a2 453b9000:431495cd:453b8aab:4301c0a2:453b9555:43276af8 453ba000:435cc686:453b9aab:433e34fe:453ba555:437b580e 453bb000:43a40511:453baaab:4390d88c:453bb555:43b73196")]
    // a fall, an extreme, a rise: two runs, a handle stopped at its neighbour's height, a zero that keeps its sign
    [InlineData("L 453b8000:80000000:453b4000:3e85bc83:453bc000:be85bc83 453c4000:bf3504f3:453c0000:bf05bc84:453c8000:bf644d62 453d0000:bf800000:453cc000:bf800000:453d4000:bf800000 453dc000:bf2aaaab:453d8000:bf800000:453dc555:bf238e39 453dd000:beaaaaab:453dcaab:beb1c71d:453e5000:00000000 453f5000:80000000:453ed000:80000000:453fd000:00000000")]
    public void The_handles_of_automatic_keys_are_Blenders(string curve)
    {
        float F(string hex) => BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(hex, 16)), 0);
        string H(float v) => BitConverter.ToUInt32(BitConverter.GetBytes(v), 0).ToString("x8");
        var t = curve.Split(' ');
        var keys = t.Skip(1).Select(k => new BlenderDeploy.ArmKey { Frame = F(k.Split(':')[0]), Value = F(k.Split(':')[1]) }).ToList();
        BlenderFCurve.RecalcHandles(keys, t[0] == "C");
        Assert.Equal(t.Skip(1), keys.Select(k => $"{H(k.Frame)}:{H(k.Value)}:{H(k.LeftX)}:{H(k.LeftY)}:{H(k.RightX)}:{H(k.RightY)}"));
    }

    [Fact]
    public void A_lone_key_keeps_handles_a_frame_to_each_side()
    {
        var keys = new List<BlenderDeploy.ArmKey> { new BlenderDeploy.ArmKey { Frame = 7f, Value = 2f } };
        BlenderFCurve.RecalcHandles(keys);
        Assert.Equal(new[] { 6f, 2f, 8f, 2f }, new[] { keys[0].LeftX, keys[0].LeftY, keys[0].RightX, keys[0].RightY });
    }

    [Theory]
    // rows of Blender 5.1.2's own FCurve.evaluate() (tools/deploy-drill/blender_bezier_dump.py, seed 1): the curve as
    // frame:value:left handle:right handle in float hex, then time and value
    // free handles inside the span: one real root, and three
    [InlineData("C 44980636:c2944cdc:43ac691b:00000000:44dc0467:00000000 452e3ee6:3f800000:44e6a3bb:41d89800:458169b0:3a25e781", "44fa4201 40f77606", "44a5ddb6 c26b88b9", "44cf452a c17ac003")]
    // handles past the neighbour: cut back to the span, their height with them
    [InlineData("C c381316d:00000000:c381b16d:00000000:c380be47:3f789489 c380fcfc:3f800000:c3812f9f:b9f3425b:c3807cfc:3f800000", "c3811734 3e7b23a4", "c3811e1f 3e28a1a7", "c38111c1 3f07305a")]
    // the time curve exactly quadratic
    [InlineData("C 42180000:3f800000:c57da000:3f800000:45813000:3f800000 46409800:00000000:46009800:429d36d1:46804c00:00000000", "45c13000 41efd23a", "45b90e99 41e5af1c", "462eb9e4 41909aa8")]
    // symmetric handles, LINEAR extrapolation: before the first key its left handle's slope (flat here)
    [InlineData("L 44ee85cd:00000000:44ee7374:00000000:44ee9826:3db5230f 44eeaa7f:3f800000:44ee9826:3f1daacd:44eebcd8:3f800000", "44ee25cd 00000000", "44ee9826 3ec73b64", "44eea245 3f491f7f")]
    // four keys: a time within 0.0001 frame of a key IS that key; one float step before it is not
    [InlineData("L 44bb2000:bfeb484e:44bae93a:be6f1747:44bb2b56:4110eb2f 44bc0000:41ec44e9:44bbca8a:3f800000:44bc1318:380ddf41 44bef775:c121f53c:44bed4f3:00000000:44bf17b6:3e791671 44bf3775:39932c5e:44befb02:00000000:44bf8293:3f800000", "44bb1fff bfeb448e", "44bc0000 41ec44e9", "44bbffff 41ec40a3")]
    // keyframe_insert's own shape - flat handles a third out: the time curve is a straight line
    [InlineData("C 443e8000:bf292751:44362aab:bf292751:4446d555:bf292751 44578000:00000000:444f2aab:00000000:445fd555:00000000", "444b0000 bea92751", "444e4000 be515728", "44410000 bf246ad2")]
    public void A_Bezier_curve_is_evaluated_as_Blender_evaluates_it(string curve, params string[] rows)
    {
        float F(string hex) => BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(hex, 16)), 0);
        var t = curve.Split(' ');
        var keys = t.Skip(1).Select(k => k.Split(':').Select(F).ToArray()).Select(p => new BlenderDeploy.ArmKey { Frame = p[0], Value = p[1], LeftX = p[2], LeftY = p[3], RightX = p[4], RightY = p[5] }).ToList();
        foreach (string row in rows)
        {
            var p = row.Split(' '); float got = BlenderFCurve.Evaluate(keys, F(p[0]), t[0] == "C");
            // to the bit where the C runtime's own exp, log, acos and cos are at hand (a 64-bit Windows process)
            if (BlenderTrig.Exact) Assert.Equal(p[1], BitConverter.ToUInt32(BitConverter.GetBytes(got), 0).ToString("x8"));
            else Assert.Equal(F(p[1]), got, 4);
        }
    }

    [Theory]
    [InlineData("0.39499999999999999", "3fd947ae147ae147")]
    [InlineData("0.5749999999999999", "3fe2666666666665")]
    [InlineData("0.8249999999999999", "3fea666666666665")]
    [InlineData("-0.39499999999999999", "bfd947ae147ae147")]
    [InlineData("-0", "8000000000000000")]
    [InlineData("-1e-500", "8000000000000000")]
    [InlineData("5e-324", "0000000000000001")]
    [InlineData("2.4703282292062327e-324", "0000000000000000")]
    [InlineData("2.4703282292062328e-324", "0000000000000001")]
    [InlineData("2.2250738585072012e-308", "0010000000000000")]
    [InlineData("1.00000000000000011102230246251565404236316680908203125", "3ff0000000000000")]
    [InlineData("1.00000000000000033306690738754696212708950042724609375", "3ff0000000000002")]
    [InlineData("0.999999999999999944488848768742172978818416595458984375", "3ff0000000000000")]
    public void Scale_decimals_round_to_the_nearest_Python_double(string input, string bits)
    {
        Assert.True(BlenderDeploy.ReadPythonFloat(input, out double value));
        Assert.Equal(bits, BitConverter.DoubleToInt64Bits(value).ToString("x16"));
    }

    [Fact]
    public void A_leg_scale_decimal_is_rounded_as_Python_reads_it()
    {
        var s = new Scene(); int hull = s.Node("Hull", mesh: true);
        s.Move(s.Node("Leg", hull, mesh: true)); s.Decide();
        var r = BlenderDeploy.Decide(s.M, "0|24|||0.39499999999999999||||0|||4|0|1".Split('|'), null, true);
        Assert.Null(r.Fallback);
        Assert.Contains("DEPLOY legs scaled x0.39 from initial (1 bones), spread by 12 held to 24", r.RetargetLog);
    }

    [Theory]
    [InlineData("3\0", "5")]
    [InlineData("3\0\0", "5")]
    [InlineData("3", "5\0")]
    [InlineData("3", "5/2\0")]
    public void Python_integer_ranges_reject_NUL_characters(string start, string end)
    {
        var s = new Scene(); int hull = s.Node("Hull", mesh: true);
        s.Move(s.Node("Gun", hull, mesh: true), to: new[] { 0.04f, 0f, 0f }); s.Decide();
        var r = BlenderDeploy.Decide(s.M, $"0|24|||||{start}|{end}|1|||0|0|1".Split('|'), null, true);
        Assert.Contains("cannot read", r.Fallback);
    }

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(int.MaxValue, 24)]
    public void Extreme_fire_frames_hold_the_correct_end_of_a_late_clip(int frame, int key)
    {
        var s = new Scene(); int hull = s.Node("Hull", mesh: true);
        s.Move(s.Node("Gun", hull, mesh: true), to: new[] { 0.04f, 0f, 0f }); s.Decide();
        // The action starts at frame 24: subtracting it from int.MinValue used to wrap to a positive index.
        foreach (var sampler in s.M.Animations[0].Samplers) sampler.Times = new[] { 1f, 2f };
        var r = BlenderDeploy.Decide(s.M, $"0|48|||||{frame}|{frame}|1|||0|0|1".Split('|'), null, true);
        Assert.Null(r.Fallback);
        Assert.Equal(24, r.FrameMin);
        Assert.Equal(frame, Assert.Single(r.FireSnap.Keys));
        Assert.Equal(r.Keys["Gun"][key].Take(7), r.FireSnap[frame]["Gun"]);
    }

    [Fact]
    public void What_the_matrices_here_do_not_model_is_left_to_Blender_by_name()
    {
        var s = new Scene();
        int hull = s.Node("Hull", mesh: true); s.Move(s.Node("Gun", hull, mesh: true));
        s.M.ExtensionsUsed.Add("KHR_animation_pointer");
        Assert.Contains("KHR_animation_pointer", s.Decide().Fallback);
        var lit = new Scene(); lit.Move(lit.Node("Gun", mesh: true)); lit.M.ExtensionsUsed.Add("KHR_lights_punctual");
        Assert.Contains("light", lit.Decide().Fallback);
        var odd = new Scene(); odd.Move(odd.Node("Gun", mesh: true));
        Assert.Contains("not ASCII", odd.Decide("0|24|café||||||0|||4|0|1").Fallback);
    }

    [Fact]
    public void What_the_review_found_morph_targets_a_node_outside_the_scene_instancing_and_a_freed_name()
    {
        // bound_box is the EVALUATED mesh: shape keys move it - left to Blender
        var morph = new Scene(); morph.Move(morph.Node("Gun", mesh: true)); morph.M.Meshes[0].Primitives[0].MorphTargets = 1;
        Assert.Contains("morph targets", morph.Decide().Fallback);
        // the importer excludes what the named scene does not hold: no frame reaches such an object
        var outside = new Scene(); outside.Move(outside.Node("Gun", mesh: true)); int stray = outside.Node("Stray", mesh: true);
        var sc = new HafScene(); sc.Nodes.Add(0); outside.M.Scenes.Add(sc); outside.M.Scene = 0;
        outside.M.Animations.Add(new HafAnimation { Name = "Unused" });
        Assert.Contains("outside the scene", BlenderDeploy.Decide(outside.M, Default.Split('|')).Fallback);
        var inst = new Scene(); inst.Move(inst.Node("Gun", mesh: true)); inst.M.ExtensionsUsed.Add("EXT_mesh_gpu_instancing");
        Assert.Contains("EXT_mesh_gpu_instancing", inst.Decide().Fallback);
        // a stripped object's name is free: the root is "UnitNormalize", not ".001"
        var named = new Scene();
        int u = named.Node("UnitNormalize", t: new double[] { 40, 0, 0 }); named.Node("Hull", mesh: true, t: new double[] { 40, 0, 0 }); named.Move(named.Node("Gun", u, mesh: true));
        Assert.Contains(named.Decide("0|24|unitnorm||||||0|||4|0|1").Objects, o => o.Name == "UnitNormalize" && o.Node < 0);
        var kept = new Scene();
        int u2 = kept.Node("UnitNormalize", t: new double[] { 40, 0, 0 }); kept.Node("Hull", mesh: true, t: new double[] { 40, 0, 0 }); kept.Move(kept.Node("Gun", u2, mesh: true));
        Assert.Contains(kept.Decide().Objects, o => o.Name == "UnitNormalize.001" && o.Node < 0);
    }

    [Fact]
    public void Names_are_ordered_padded_and_quoted_as_Blender_and_Python_do()
    {
        // bpy.data.objects: unsigned UTF-8 bytes, upper-case ASCII folded - a letter past ASCII after every ASCII name
        var names = new List<string> { "zeta", "Émile", "~tilde", "café", "cafz", "Hull" };
        names.Sort(BlenderDeploy.IdNameCmp);
        Assert.Equal(new[] { "cafz", "café", "Hull", "zeta", "~tilde", "Émile" }, names);
        Assert.Equal(0, BlenderDeploy.IdNameCmp("Alpha", "aLPHA"));
        // %-40s counts code points
        Assert.Equal(40 + 1, BlenderDeploy.PyPad("\U0001F600p", 40).Length);
        Assert.Equal(new string('a', 45), BlenderDeploy.PyPad(new string('a', 45), 40));
        // repr
        Assert.Equal("'plain'", BlenderDeploy.PyRepr("plain"));
        Assert.Equal("\"It's\"", BlenderDeploy.PyRepr("It's"));
        Assert.Equal("'Say\"hi\\''", BlenderDeploy.PyRepr("Say\"hi'"));
        Assert.Equal("'back\\\\slash'", BlenderDeploy.PyRepr("back\\slash"));
        Assert.Equal("'tab\\there'", BlenderDeploy.PyRepr("tab\there"));
        Assert.Equal("'bell\\x07'", BlenderDeploy.PyRepr("bell\u0007"));
    }

    [Theory]
    [InlineData(0.125, 2, "0.12")]        // an exact half goes to the even digit - .NET's F2 gives 0.13
    [InlineData(0.375, 2, "0.38")]
    [InlineData(2.675, 2, "2.67")]        // the double is just under 2.675
    [InlineData(1.005, 2, "1.00")]
    [InlineData(0.5, 0, "0")]
    [InlineData(1.5, 0, "2")]
    [InlineData(100.0, 0, "100")]
    [InlineData(-0.125, 2, "-0.12")]
    [InlineData(9.995, 2, "9.99")]        // 9.99499999999999957...
    [InlineData(9.9996, 3, "10.000")]
    [InlineData(199.09005737304688, 3, "199.090")]
    [InlineData(1e21, 1, "1000000000000000000000.0")]
    [InlineData(1e-7, 3, "0.000")]
    [InlineData(0.0, 2, "0.00")]
    public void A_number_is_printed_as_Python_prints_it(double v, int decimals, string expected) => Assert.Equal(expected, PyFormat.Fixed(v, decimals));
}
