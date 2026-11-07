using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

public class RigAnimLegacyTranslationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Attack_permission_does_not_keep_small_move_deploy_or_primary_translations(bool sliced, bool cleanUnits)
    {
        var m = Model(cleanUnits);
        Translation(m, "Travel", .5f, 1.5f);
        Translation(m, "Attack", .5f, .7f); Translation(m, "Move", 2, 2.2f); Translation(m, "Deploy", 3, 3.3f);
        var s = Run(m, sliced ? "attack=Travel[12..36];move=Travel[0..6];deploy=Travel[6..12]" : "attack=Attack;move=Move;deploy=Deploy");
        var kept = Locations(Role(s, "attack")); Assert.NotEmpty(kept);
        Assert.InRange(Math.Abs(Range(kept) - (sliced ? .5 : .2) * (cleanUnits ? 1 : 100)), 0, 1e-4);
        if (!cleanUnits) Assert.All(kept, fc => Assert.Equal(0f, fc.Keys.OrderBy(k => k.Frame).First().Value));
        Assert.Empty(Locations(Role(s, "move")));
        Assert.Empty(Locations(Role(s, "deploy")));
        Assert.Empty(Locations(s.Actions.Single(a => a.Name == s.PrimaryAction)));
    }

    [Fact]
    public void Large_move_and_attack_on_the_same_bone_both_keep_their_own_slides()
    {
        var m = Model(); Translation(m, "Attack", .5f, .7f); Translation(m, "Move", 2, 3);
        var s = Run(m, "attack=Attack;move=Move");
        Assert.InRange(Math.Abs(Range(Locations(Role(s, "attack"))) - 20), 0, 1e-4);
        Assert.InRange(Math.Abs(Range(Locations(Role(s, "move"))) - 100), 0, 1e-4);
    }

    [Fact]
    public void Roles_sharing_one_action_use_the_lower_floor_and_amplify_it_only_once()
    {
        var m = Model(); Translation(m, "Shared", .5f, .7f);
        var s = Run(m, "attack=Shared;move=Shared");
        Assert.Same(Role(s, "attack"), Role(s, "move"));
        Assert.InRange(Math.Abs(Range(Locations(Role(s, "attack"))) - 20), 0, 1e-4);
    }

    [Fact]
    public void Last_role_assignment_keeps_its_own_floor()
    {
        var m = Model(); Translation(m, "Attack", .5f, .7f); Translation(m, "Large", 0, 1); Translation(m, "Small", 0, .2f);
        var s = Run(m, "attack=Attack;move=Large;move=Small");
        Assert.Equal("Small", Role(s, "move").Name);
        Assert.Empty(Locations(Role(s, "move")));
    }

    [Fact]
    public void Literal_rebaked_action_names_do_not_inherit_another_actions_floor()
    {
        var m = Model(); Translation(m, "Slide", .5f, .7f); Translation(m, "Slide_rebaked_copy", 0, .2f);
        var s = Run(m, "attack=Slide;move=Slide_rebaked_copy");
        Assert.NotEmpty(Locations(Role(s, "attack")));
        Assert.Empty(Locations(Role(s, "move")));
    }

    [Fact]
    public void Poisoned_channels_still_veto_the_bone_path_across_eligible_actions()
    {
        var m = Model(); Translation(m, "Bad", 0, 200000); Translation(m, "Good", 0, 1);
        var s = Run(m, "attack=Bad;move=Good");
        Assert.Empty(Locations(Role(s, "attack"))); Assert.Empty(Locations(Role(s, "move")));
        Assert.Contains(s.Logs3, l => l.Contains("REJECTED as decomposition garbage"));
    }

    static List<BlenderRigAnim.FCurveData> Locations(BlenderRigAnim.ActionData action)
        => action.FCurves.Where(fc => fc.Bone == "Root" && fc.Prop == "location").ToList();
    static double Range(List<BlenderRigAnim.FCurveData> curves)
        => curves.Max(fc => (double)fc.Keys.Max(k => k.Value) - fc.Keys.Min(k => k.Value));
    static BlenderRigAnim.ActionData Role(BlenderRigAnim.Stage1 s, string role)
        => s.Actions.Single(a => a.Name == s.RoleActions.Single(r => r.role == role).action);
    static BlenderRigAnim.Stage1 Run(HafModel m, string roles)
    {
        var a = BlenderRigAnim.ParseArgs(new[] { "scene.glb", "scene.fbx", "0", "", "Idle", "", "1", "0,0,0", "0", roles, "0", "", "1", "", "0" });
        var s = BlenderRigAnim.RunStage1(m, a); BlenderRigAnim.RunStage2(s, a); BlenderRigAnim.RunStage3(s, a);
        Assert.Null(s.Fallback); Assert.Null(s.Deferred); Assert.False(s.Exits); return s;
    }
    static void Translation(HafModel m, string name, float start, float end)
    {
        var a = new HafAnimation { Name = name, Duration = 2 };
        a.Samplers.Add(new HafSampler { Times = new[] { 0f, 2f }, Values = new[] { start, 0f, 0f, end, 0f, 0f }, Components = 3 });
        a.Channels.Add(new HafChannel { Node = 1, Sampler = 0, Path = "translation" }); m.Animations.Add(a);
    }
    static HafModel Model(bool cleanUnits = false)
    {
        var m = new HafModel { Scene = 0 }; var scene = new HafScene(); scene.Nodes.Add(0); m.Scenes.Add(scene);
        var arm = new HafNode { Name = cleanUnits ? "DeployArmV2" : "Armature" }; arm.Children.AddRange(new[] { 1, 3 });
        var root = new HafNode { Name = "Root", Parent = 0 }; root.Children.Add(2);
        m.Nodes.AddRange(new[] { arm, root, new HafNode { Name = "Arm", Parent = 1, Translation = new double[] { 0, 1, 0 } },
            new HafNode { Name = "Body", Parent = 0, Mesh = 0, Skin = 0 } });
        m.Skins.Add(new HafSkin { Name = "Skin", Joints = new[] { 1, 2 }, Skeleton = 1 });
        m.Materials.Add(new HafMaterial { Name = "Paint" });
        var p = new HafPrimitive { Positions = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f, 1f, 0f },
            Indices = new[] { 0, 1, 2, 1, 3, 2 }, Material = 0, Joints = new ushort[16], Weights = new float[16] };
        for (int v = 0; v < 4; v++) p.Weights[4 * v] = 1;
        var mesh = new HafMesh { Name = "Body" }; mesh.Primitives.Add(p); m.Meshes.Add(mesh);
        Translation(m, "Idle", 4, 4.25f);
        return m;
    }
}
