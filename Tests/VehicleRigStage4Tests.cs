using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

public class VehicleRigStage4Tests
{
    // A ten-unit hull and three wheels. Blender's last frame_set evaluates the preceding rock key; assigning
    // the final sample afterwards restores the original pose, but leaves the evaluated hull below the wheels.
    [Theory]
    [InlineData(15, true)]
    [InlineData(2, true)]  // several rock samples replace the key at the last frame
    [InlineData(15, false)]
    public void Ground_contact_uses_the_evaluated_pose_and_the_final_pose_keeps_the_last_sample(int frames, bool rock)
    {
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene());
        Box(m, "Hull", 0, .5f, 0, 10, 1, 2);
        Box(m, "Big0", 3, .4f, 1.2f, .8f, .8f, .2f);
        Box(m, "Big1", -3, .4f, 1.2f, .8f, .8f, .2f);
        Box(m, "Small", 0, .22f, 1.2f, .3f, .3f, .2f);
        var argv = Enumerable.Repeat("", 76).ToList();
        argv[0] = "rig"; argv[1] = "scene.glb"; argv[2] = "scene_Spin.glb";
        argv[6] = "AUTO"; argv[7] = frames.ToString(); argv[8] = "-360";
        argv[17] = "0"; argv[18] = frames.ToString(); argv[21] = rock ? "20" : "0"; argv[24] = "0";
        var a = BlenderVehicleRig.ParseArgs(argv);
        a.Names["wheels"] = new List<string> { "Big0", "Big1", "Small" };
        foreach (var n in a.Names["wheels"]) a.Marked.Add(n);
        var s = BlenderVehicleRig.RunStage1(m, a);
        BlenderVehicleRig.RunStage2(s, a); BlenderVehicleRig.RunStage3(s, a); BlenderVehicleRig.RunStage4(s, a);
        Assert.Null(s.Fallback); Assert.Null(s.Dies); Assert.False(s.Exits);
        var spin = Assert.Single(s.Actions);
        var small = Assert.Single(Assert.Single(spin.Slots).FCurves.Where(fc => fc.Bone == "Wheel_02" && fc.Prop == "rotation_euler" && fc.Index == 1));
        // Blender-measured native fixtures fx_e_groundeval{,_dense,_off}: rock leaves every wheel airborne;
        // without rock the small wheel rolls 0.8 / 0.3 times as fast as the largest wheels.
        float last = small.Keys.Last().Value;
        Assert.InRange(Math.Abs(last - (rock ? -2 * Math.PI : -960 * (Math.PI / 180))), 0, 1e-4);
        if (rock)
        {
            Assert.Contains(s.Logs4, l => l.StartsWith("VEHICLE rolling-contact: 3 wheel(s) above"));
            int body = ((BlenderVehicleRig.RigState)s.VehicleState).BoneId["RootHull"];
            Assert.All(s.Armature.PoseEuler[body], v => Assert.InRange(Math.Abs(v), 0f, 1e-6f));
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Furl_is_separate_and_Spin_holds_sails_raised_and_flags_struck_or_folded(bool fold, bool flagFold, bool wheel)
    {
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene());
        Box(m, "Hull", 0, .5f, 0, 4, 1, 2);
        Box(m, "Canvas", 0, 3, 0, .04f, 3, 4);
        Box(m, "Banner", 0, 5, 0, .04f, 1, 2);
        var a = E2Args();
        a.SailFold = fold; a.SailFoldFrames = 7; a.SailFoldAngle = 180; a.SailFoldSag = .5;
        a.FlagFoldOn = flagFold; a.FlagFoldDeg = 110; a.FlagFoldFrames = 17;
        Mark(a, "sails", "Canvas"); Mark(a, "flags", "Banner");
        if (wheel) { Box(m, "Wheel", 2, .3f, 1.3f, .6f, .6f, .1f); Mark(a, "wheels", "Wheel"); }
        var s = Rig(m, a);
        Assert.Equal(wheel ? 15 : 1, s.FrameCurrent); // authoring Furl/holds never calls frame_set
        var spin = Assert.Single(s.Actions.Where(x => x.Name == "Spin"));
        var furl = Assert.Single(s.Actions.Where(x => x.Name == "Furl"));
        Assert.Equal(flagFold ? 17f : fold ? 7f : 1f, furl.FrameEnd);
        Assert.Equal(15f, spin.FrameEnd);
        var all = s.Actions.SelectMany(x => x.Slots).SelectMany(x => x.FCurves).SelectMany(x => x.Keys);
        Assert.All(all, k => Assert.Equal("LINEAR", k.Interpolation));
        Assert.All(Curve(spin, "Sail", 0).Keys, k => Assert.Equal(1f, k.Value));
        Assert.Equal(1f, Curve(furl, "Flag", 0).Keys[0].Value);
        if (fold) Assert.NotEqual(1f, Curve(furl, "SailF1", 0).Keys.Last().Value);
        var state = (BlenderVehicleRig.RigState)s.VehicleState;
        var flag = s.Armature.PoseProps[state.BoneId["Flag"]][1];
        if (wheel) Assert.True(Math.Abs(flag[0]) < .99f); else Assert.Equal(new[] { 1f, 0f, 0f, 0f }, flag);
        // Constant signed-zero Spin channels must not overwrite the original +0 left by the explicit identity reset.
        Assert.All(flag.Where(v => v == 0f), v => Assert.Equal(0, BitConverter.ToInt32(BitConverter.GetBytes(v), 0)));
    }

    [Fact]
    public void Rowing_keys_every_frame_of_the_shared_clip_without_changing_the_current_frame()
    {
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene());
        Box(m, "Hull", 0, .5f, 0, 4, 1, 2);
        Box(m, "Left", 0, 1.2f, 2, .03f, .03f, 2);
        Box(m, "Right", 0, 1.2f, -2, .03f, .03f, 2);
        var a = E2Args(); a.OarSweep = 48; a.OarDip = 12; a.OarLift = 15; a.OarRake = -20;
        Mark(a, "oars", "Left", "Right");
        var s = Rig(m, a); var state = (BlenderVehicleRig.RigState)s.VehicleState;
        Assert.Equal(2, state.Oars.Count); Assert.Equal(1, s.FrameCurrent); Assert.Equal(24, s.FrameEnd);
        var spin = Assert.Single(s.Actions);
        foreach (var oar in state.Oars)
            for (int c = 0; c < 4; c++)
            {
                var curve = Curve(spin, oar.bone, c); Assert.Equal(25, curve.Keys.Count);
                Assert.All(curve.Keys, k => Assert.Equal("LINEAR", k.Interpolation));
                Assert.Equal(curve.Keys.Last().Value, s.Armature.PoseProps[state.BoneId[oar.bone]][1][c]);
            }
    }

    [Theory]
    [InlineData("", "", "", "", 12, 270, 0, 24)]
    [InlineData("0", "nan", "inf", "3", 1, 30, 1, 4)]
    [InlineData("7.9", "inf", "nan", "5.9", 7, 360, 0, 5)]
    [InlineData("-2", "-inf", "-inf", "-4", 1, 30, 0, 4)]
    public void E2_dials_follow_Pythons_defaults_truncation_and_clamps(string frames, string angle, string sag, string oarFrames, int sf, double sa, double ss, int of)
    {
        var argv = Enumerable.Repeat("", 76).ToList(); argv[0] = "rig"; argv[1] = "scene.glb"; argv[6] = "AUTO"; argv[7] = "15"; argv[8] = "-360";
        argv[72] = frames; argv[73] = angle; argv[75] = sag; argv[45] = oarFrames;
        var a = BlenderVehicleRig.ParseArgs(argv); Assert.Null(a.FoldDie); Assert.Null(a.NamesDie);
        Assert.Equal(sf, (int)a.SailFoldFrames); Assert.Equal(sa, a.SailFoldAngle); Assert.Equal(ss, a.SailFoldSag); Assert.Equal(of, (int)a.OarFrames);
    }

    [Theory]
    [InlineData(0, 10, false)] // 1.5 rounds to an even two-frame kick; scene remains at frame 1
    [InlineData(5, 30, true)] // 4.5 rounds to four; the current frame is on the return
    [InlineData(24, 16, true)] // current frame is still in the lead-in hold
    public void Recoil_holds_the_deployed_stance_and_evaluates_at_the_existing_frame(int lead, int frames, bool wheel)
    {
        var a = E2Args(); a.RecoilDist = .25; a.GunDeployElev = 32; a.TrailFrames = 13; a.RecoilLead = lead; a.RecoilFrames = frames;
        var s = E3Rig(a, wheel); var st = (BlenderVehicleRig.RigState)s.VehicleState;
        var dep = Assert.Single(s.Actions.Where(x => x.Name == "Deploy")); var rec = Assert.Single(s.Actions.Where(x => x.Name == "Recoil"));
        Assert.Equal("Spin", st.Arm.Action); Assert.Equal(wheel ? 15 : 1, s.FrameCurrent); Assert.Equal(15, s.FrameEnd);
        Assert.Equal(13f, dep.FrameEnd); Assert.Equal((float)(lead + frames), rec.FrameEnd);
        int kick = frames == 10 ? 2 : frames == 30 ? 4 : 2;
        Assert.Equal(lead > 0 ? new float[] { 0, lead, lead + kick, lead + frames } : new float[] { 0, kick, frames }, Curve(rec, "RecoilArm", 0).Keys.Select(k => k.Frame));
        foreach (string bone in new[] { "Trail_00", "Trail_01", "Gun" })
            for (int i = 0; i < 4; i++)
            {
                float q = Curve(dep, bone, i).Keys.Last().Value;
                Assert.All(Curve(rec, bone, i).Keys, k => Assert.Equal(q, k.Value));
                Assert.Equal(q, s.Armature.PoseProps[st.BoneId[bone]][1][i]);
            }
        for (int i = 0; i < 4; i++)
        {
            var keys = Curve(rec, "RecoilArm", i).Keys;
            Assert.Equal(BlenderRigAnim.EvaluateCurve(keys, s.FrameCurrent), s.Armature.PoseProps[st.BoneId["RecoilArm"]][1][i]);
        }
        Assert.All(s.Actions.SelectMany(x => x.Slots).SelectMany(x => x.FCurves).SelectMany(x => x.Keys), k => Assert.Equal("LINEAR", k.Interpolation));
        Assert.All(Assert.Single(s.Actions.Where(x => x.Name == "Spin")).Slots.SelectMany(x => x.FCurves), fc => Assert.DoesNotContain(fc.Bone, new[] { "Gun", "RecoilArm", "Trail_00", "Trail_01" }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.01)]
    [InlineData(-35)]
    [InlineData(double.NaN)]
    public void Disabled_spread_skips_Deploy_and_its_gun_hold(double spread)
    {
        var a = E2Args(); a.RecoilDist = .25; a.TrailSpread = spread; a.GunDeployElev = 45;
        var s = E3Rig(a, false);
        Assert.DoesNotContain(s.Actions, x => x.Name == "Deploy");
        Assert.All(Assert.Single(s.Actions.Where(x => x.Name == "Recoil")).Slots.SelectMany(x => x.FCurves), fc => Assert.Equal("RecoilArm", fc.Bone));
    }

    [Fact]
    public void Deploy_without_Recoil_keeps_its_final_assignment_without_moving_the_scene_frame()
    {
        var a = E2Args(); a.GunDeployElev = -24;
        var s = E3Rig(a, false); var st = (BlenderVehicleRig.RigState)s.VehicleState;
        Assert.Equal(1, s.FrameCurrent); Assert.DoesNotContain(s.Actions, x => x.Name == "Recoil");
        var dep = Assert.Single(s.Actions.Where(x => x.Name == "Deploy"));
        foreach (string bone in new[] { "Trail_00", "Trail_01", "Gun" })
            for (int i = 0; i < 4; i++) Assert.Equal(Curve(dep, bone, i).Keys.Last().Value, s.Armature.PoseProps[st.BoneId[bone]][1][i]);
        Assert.DoesNotContain(s.Logs4, l => l.StartsWith("VEHICLE TRAIL *** WARNING:"));
    }

    [Theory]
    [InlineData("", "", "", "", "", 35, 12, 0, 16, 0)]
    [InlineData("-2", "1.9", "-30", "2.9", "-2.9", -2, 2, -30, 3, 0)]
    [InlineData("nan", "7.9", "45", "30.9", "5.9", double.NaN, 7, 45, 30, 5)]
    public void E3_dials_follow_Python_defaults_and_integer_clamps(string spread, string tf, string elev, string rf, string lead, double es, int et, double ee, int er, int el)
    {
        var argv = Enumerable.Repeat("", 76).ToList(); argv[0] = "rig"; argv[1] = "scene.glb"; argv[7] = "15"; argv[8] = "-360";
        argv[32] = spread; argv[33] = tf; argv[35] = elev; argv[39] = rf; argv[40] = lead;
        var a = BlenderVehicleRig.ParseArgs(argv); Assert.Null(a.NamesDie);
        Assert.Equal(es, a.TrailSpread); Assert.Equal(et, (int)a.TrailFrames); Assert.Equal(ee, a.GunDeployElev); Assert.Equal(er, (int)a.RecoilFrames); Assert.Equal(el, (int)a.RecoilLead);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void E3_limits_apply_only_to_an_enabled_action(bool enabled)
    {
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene()); Box(m, "Hull", 0, .5f, 0, 2, 1, 2); Box(m, "Trail", .7f, .3f, 2, .2f, .2f, 4);
        var a = E2Args(); Mark(a, "trails", "Trail"); a.TrailFrames = 100001; a.TrailSpread = enabled ? 35 : 0;
        var s = BlenderVehicleRig.RunStage1(m, a); BlenderVehicleRig.RunStage2(s, a); BlenderVehicleRig.RunStage3(s, a); BlenderVehicleRig.RunStage4(s, a);
        if (enabled) Assert.StartsWith("stage 4: the Deploy or Recoil clip", s.Fallback); else Assert.Null(s.Fallback);
    }

    [Fact]
    public void Zero_span_tube_reproduces_the_scripts_stage_four_division_failure()
    {
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene()); Box(m, "Hull", 0, .5f, 0, 2, 1, 2); Box(m, "Dot", 1.5f, 1.2f, 0, 0, 0, 0);
        var a = E2Args(); Mark(a, "guns", "Dot"); a.RecoilDist = .4;
        var s = BlenderVehicleRig.RunStage1(m, a); BlenderVehicleRig.RunStage2(s, a); BlenderVehicleRig.RunStage3(s, a);
        Assert.Null(s.Dies); BlenderVehicleRig.RunStage4(s, a);
        Assert.Null(s.Fallback); Assert.Equal("ZeroDivisionError: float division by zero", s.Dies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recoil_view_layer_update_does_not_advance_unrelated_object_animation(bool frameSet)
    {
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene()); Box(m, "Hull", 0, .5f, 0, 2, 1, 2); Box(m, "Tube", 0, 1.5f, -2, .25f, .25f, 4); Box(m, "Mast", 0, 2, 0, .1f, .1f, .1f);
        var a = E2Args(); a.RecoilDist = .25; Mark(a, "guns", "Tube"); Mark(a, "preserve", "Mast");
        var anim = new HafAnimation { Name = "Deploy" }; anim.Samplers.Add(new HafSampler { Times = new[] { 0f, 2f }, Values = new[] { 0f, 0f, 0f, .3f, 0f, 0f }, Components = 3 });
        anim.Channels.Add(new HafChannel { Node = 2, Sampler = 0, Path = "translation" }); m.Animations.Add(anim);
        if (frameSet) { Box(m, "Wheel", 1.2f, .4f, 0, .2f, .8f, .8f); Mark(a, "wheels", "Wheel"); }
        var s = Rig(m, a); var mast = Assert.Single(s.Objects.Where(o => o.Name == "Mesh___keep__Mast"));
        // Native probes 022, 055, 077: no frame_set keeps zero. A wheel does advance the object action.
        Assert.Equal(frameSet ? .09375f : 0f, mast.World[12]);
    }

    static BlenderRigAnim.Stage1 E3Rig(BlenderVehicleRig.Args a, bool wheel)
    {
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene());
        Box(m, "Hull", 0, .5f, 0, 2, 1, 2); Box(m, "Tube", 0, 1.5f, -2, .25f, .25f, 4);
        Box(m, "Left", -.7f, .3f, 2, .2f, .2f, 4); Box(m, "Right", .7f, .3f, 2, .2f, .2f, 4);
        Mark(a, "guns", "Tube"); Mark(a, "trails", "Left", "Right");
        if (wheel) { Box(m, "Wheel", 1.2f, .4f, 0, .2f, .8f, .8f); Mark(a, "wheels", "Wheel"); }
        return Rig(m, a);
    }

    static BlenderVehicleRig.Args E2Args()
    {
        var argv = Enumerable.Repeat("", 76).ToList(); argv[0] = "rig"; argv[1] = "scene.glb"; argv[6] = "AUTO"; argv[7] = "15"; argv[8] = "-360"; argv[17] = "0"; argv[21] = "0";
        return BlenderVehicleRig.ParseArgs(argv);
    }
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void E2_limits_decline_only_when_the_corresponding_parts_exist(bool rowing, bool parts)
    {
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene()); Box(m, "Hull", 0, .5f, 0, 4, 1, 2);
        var a = E2Args();
        if (rowing)
        {
            a.OarSweep = double.NaN;
            if (parts) { Box(m, "Oar", 0, 1.2f, 2, .03f, .03f, 2); Mark(a, "oars", "Oar"); }
        }
        else
        {
            a.FlagFoldOn = true; a.FlagFoldFrames = 100001;
            if (parts) { Box(m, "Banner", 0, 5, 0, .04f, 1, 2); Mark(a, "flags", "Banner"); }
        }
        var s = BlenderVehicleRig.RunStage1(m, a); BlenderVehicleRig.RunStage2(s, a); BlenderVehicleRig.RunStage3(s, a); BlenderVehicleRig.RunStage4(s, a);
        Assert.Null(s.Dies);
        if (parts) Assert.StartsWith(rowing ? "stage 4: the rowing stroke" : "stage 4: the Furl clip", s.Fallback); else Assert.Null(s.Fallback);
    }
    static void Mark(BlenderVehicleRig.Args a, string role, params string[] names) { a.Names[role] = names.ToList(); foreach (string n in names) a.Marked.Add(n); }
    static BlenderRigAnim.Stage1 Rig(HafModel m, BlenderVehicleRig.Args a)
    {
        var s = BlenderVehicleRig.RunStage1(m, a); BlenderVehicleRig.RunStage2(s, a); BlenderVehicleRig.RunStage3(s, a); BlenderVehicleRig.RunStage4(s, a);
        Assert.Null(s.Fallback); Assert.Null(s.Dies); Assert.False(s.Exits); return s;
    }
    static BlenderRigAnim.FCurveData Curve(BlenderRigAnim.ActionData a, string bone, int c)
        => Assert.Single(Assert.Single(a.Slots).FCurves.Where(fc => fc.Bone == bone && fc.Prop == "rotation_quaternion" && fc.Index == c));

    static void Box(HafModel m, string name, float x, float y, float z, float sx, float sy, float sz)
    {
        var p = new List<float>();
        foreach (int dx in new[] { -1, 1 }) foreach (int dy in new[] { -1, 1 }) foreach (int dz in new[] { -1, 1 })
            p.AddRange(new[] { x + dx * sx / 2, y + dy * sy / 2, z + dz * sz / 2 });
        var mesh = new HafMesh { Name = name };
        mesh.Primitives.Add(new HafPrimitive { Positions = p.ToArray(), Indices = new[] { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 } });
        m.Nodes.Add(new HafNode { Name = name, Mesh = m.Meshes.Count }); m.Meshes.Add(mesh); m.Scenes[0].Nodes.Add(m.Nodes.Count - 1);
    }
}
