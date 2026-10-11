using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>BlenderDeployExport: the export step's log lines and the structure of the file Blender's exporter writes
/// (part 8a). tools/deploy_drill.sh holds whole jobs to the GLB the script writes (every node, skin, primitive and
/// animation row); here each rule on a model small enough to read.</summary>
public class BlenderDeployExportTests
{
    const string Default = "0|24|||||||0|||4|0|1";

    sealed class Scene
    {
        public readonly HafModel M = new HafModel();
        readonly HafAnimation anim = new HafAnimation { Name = "Deploy" };

        public int Node(string name, int parent = -1, bool mesh = false, double[] t = null, string meshName = null)
        {
            var n = new HafNode { Name = name, Parent = parent };
            if (t != null) n.Translation = t;
            if (mesh)
            {
                var p = new HafPrimitive { VertexCount = 3, Positions = new float[] { -1, 0, 0, 1, 0, 0, 0, 1, 0.5f } };
                var hm = new HafMesh { Name = meshName ?? name.ToLowerInvariant() + "_mesh" }; hm.Primitives.Add(p);
                M.Meshes.Add(hm); n.Mesh = M.Meshes.Count - 1;
            }
            M.Nodes.Add(n);
            if (parent >= 0) M.Nodes[parent].Children.Add(M.Nodes.Count - 1);
            return M.Nodes.Count - 1;
        }

        public void Move(int node, float[] to)
        {
            var t = M.Nodes[node].Translation ?? new double[3];
            anim.Samplers.Add(new HafSampler { Times = new[] { 0f, 1f }, Values = new[] { (float)t[0], (float)t[1], (float)t[2], to[0], to[1], to[2] }, Components = 3, Interpolation = "LINEAR" });
            anim.Channels.Add(new HafChannel { Node = node, Path = "translation", Sampler = anim.Samplers.Count - 1 });
        }

        public void Turn(int node)
        {
            anim.Samplers.Add(new HafSampler { Times = new[] { 0f, 1f }, Values = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f }, Components = 4, Interpolation = "LINEAR" });
            anim.Channels.Add(new HafChannel { Node = node, Path = "rotation", Sampler = anim.Samplers.Count - 1 });
        }

        public void Finish(int scenes = 1, string secondName = null)
        {
            if (M.Scenes.Count > 0) return;
            var sc = new HafScene(); sc.Nodes.AddRange(Enumerable.Range(0, M.Nodes.Count).Where(i => M.Nodes[i].Parent < 0)); M.Scenes.Add(sc); M.Scene = 0;
            for (int i = 1; i < scenes; i++) M.Scenes.Add(new HafScene { Name = secondName ?? "" });
            M.Animations.Add(anim);
        }

        public (BlenderDeploy.Result baked, BlenderDeployExport.Result export) Export(string args = Default)
        {
            Finish();
            var names = BlenderNames.Compute(M);
            var r = BlenderDeploy.Decide(M, args.Split('|'), names, true);
            Assert.Null(r.Fallback);
            r.Finish();
            Assert.Null(r.RoleFallback);
            return (r, BlenderDeployExport.Build(M, names, r, args.Split('|')));
        }
    }

    static Scene Tank()
    {
        var s = new Scene();
        int hull = s.Node("Hull", mesh: true);
        int turret = s.Node("Turret", hull, t: new double[] { 0, 1, 0 }); s.Turn(turret);
        int gun = s.Node("Gun", turret, mesh: true, t: new double[] { 1, 0.5, 0 }); s.Move(gun, new[] { 2.5f, 0.5f, 0f });
        s.Node("Sight", gun, mesh: true, t: new double[] { 0, 0.5, 0 });
        return s;
    }

    [Fact]
    public void The_file_has_the_joints_before_the_mesh_nodes_and_the_armature_last()
    {
        var (baked, x) = Tank().Export();
        Assert.Null(x.Fallback);
        // the trim, the purge of the turret (an animated empty: gone at step 7 already, so nothing is left to purge), the sanitize
        // the trim; the purge of the normalization's empty (the model is small: scaled by 100 under "UnitNormalize"; the
        // turret, an animated empty, went at step 7 already); the sanitize, printed always
        Assert.Equal(new[] { "UnitNormalize" }, x.Purged);
        Assert.Equal(new[] { "DEPLOY trim to frames 0..24", "DEPLOY purged 1 leftover source object(s) (empties/lights/cameras/locators/groups)", "DEPLOY sanitized: 0 garbage bone(s) de-animated (0 curves) — rest-pose ride: none" }, x.Log);
        // bones post-order (Gun under Turret, StaticRoot), then the mesh nodes by name, then the armature
        Assert.Equal(new[] { "Gun", "Turret", "StaticRoot", "Gun", "Hull", "Sight", "DeployArm" }, x.Nodes.Select(n => n.Name));
        Assert.Equal(new[] { 0 }, x.Nodes[1].Children); Assert.Equal(1, x.Nodes[0].Parent);
        Assert.Equal(new[] { 3, 4, 5, 1, 2 }, x.Nodes[6].Children);
        Assert.All(x.Nodes.Take(3), n => Assert.True(n.Joint));
        Assert.All(x.Nodes.Skip(3).Take(3), n => { Assert.Equal(0, n.Skin); Assert.True(n.Mesh >= 0); Assert.Null(n.Translation); Assert.Null(n.Rotation); Assert.Null(n.Scale); });
        // the skin: the bones pre-order (Turret, Gun, StaticRoot), one inverse bind matrix each; one scene with the armature at its root
        Assert.Single(x.Skins); Assert.Equal(new[] { 1, 0, 2 }, x.Skins[0].Joints); Assert.Equal(3, x.Skins[0].InverseBind.Count); Assert.Equal("DeployArm", x.Skins[0].Name);
        Assert.Equal(new[] { ("Scene", new List<int> { 6 }) }, x.Scenes);
        Assert.False(x.NeutralBone); Assert.False(x.UnusedSkin);
        // the meshes in node order, named after their datablocks; every vertex on its part's joint (Gun 0, Hull StaticRoot 2, Sight on Gun 0) at weight 1
        Assert.Equal(new[] { "gun_mesh", "hull_mesh", "sight_mesh" }, x.Meshes.Select(m => m.Name));
        ushort[] First(ushort[] joints) => joints.Where((j, i) => i % 4 == 0).ToArray();   // the first of a vertex's four slots; the others are padding
        Assert.All(First(x.Meshes[0].Primitives[0].Joints), j => Assert.Equal(1, j));   // Gun is joint 1 of the skin (pre-order)
        Assert.All(First(x.Meshes[1].Primitives[0].Joints), j => Assert.Equal(2, j));
        Assert.All(First(x.Meshes[2].Primitives[0].Joints), j => Assert.Equal(1, j));
        Assert.All(x.Meshes.SelectMany(m => m.Primitives[0].Weights.Where((w, i) => i % 4 == 0)), w => Assert.Equal(1f, w));
        Assert.All(x.Meshes, m => Assert.Equal(new[] { -1 }, m.Material));
        // a joint's transform: the root bone against the armature's world, written with its float noise (no snapping)
        Assert.NotNull(x.Nodes[1].Translation);
        Assert.Equal(new[] { "deploy", "deployed", "fold", "folded", "unfold" }, x.Animations);
    }

    [Fact]
    public void The_trim_goes_through_the_scene_range_setters()
    {
        // a start below MINFRAME is clamped to 0; without a third argument there is no trim line at all
        Assert.Equal("DEPLOY trim to frames 0..24", Tank().Export("-5|24|||||||0|||4|0|1").export.Log[0]);
        Assert.DoesNotContain(Tank().Export("0").export.Log, l => l.StartsWith("DEPLOY trim"));
        // an end below the start pulls the start down with it (rna_Scene_end_frame_set)
        Assert.Equal("DEPLOY trim to frames 0..0", Tank().Export("3|0|||||||0|||4|0|1").export.Log[0]);
    }

    [Fact]
    public void Every_glTF_scene_but_the_default_makes_an_empty_Blender_scene()
    {
        var s = Tank(); s.Finish(scenes: 2);
        var x = s.Export().export;
        Assert.Equal(new[] { "Scene", "Scene 1" }, x.Scenes.Select(sc => sc.name));
        Assert.Empty(x.Scenes[1].roots);
        var named = Tank(); named.Finish(scenes: 2, secondName: "Scene");   // the name is taken: Blender numbers it
        Assert.Equal(new[] { "Scene", "Scene.001" }, named.Export().export.Scenes.Select(sc => sc.name));
    }

    [Fact]
    public void A_role_clip_without_a_key_is_no_animation()
    {
        // a wheel count below zero keys no frame of the folded role: the action has no curve, and the exporter writes no animation for it
        var s = new Scene();
        int hull = s.Node("Hull", mesh: true);
        int wheel = s.Node("Wheel", hull, mesh: true, t: new double[] { 1, 0.5, 0 }); s.Turn(wheel);
        int gun = s.Node("Gun", hull, mesh: true, t: new double[] { 0, 1, 0 }); s.Move(gun, new[] { 0f, 1.5f, 0f });
        var x = s.Export("0|24|||||||0|||4|0|1||Wheel|AUTO|-3|-360").export;
        Assert.Null(x.Fallback);
        Assert.Equal(new[] { "deploy", "deployed", "fold", "unfold" }, x.Animations);
    }

    [Theory]
    [InlineData(" 12 ", true, 12)]
    [InlineData("+3", true, 3)]
    [InlineData("-0", true, 0)]
    [InlineData("1_000", false, 0)]
    [InlineData("12\0", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("2147483648", false, 0)]
    public void Python_int_of_an_argument(string s, bool ok, int value)
    {
        Assert.Equal(ok, BlenderDeploy.PyIntParse(s, out int v));
        if (ok) Assert.Equal(value, v);
    }

    [Fact]
    public void Sanitization_counts_path_keys_when_quoted_bone_names_share_a_prefix()
    {
        var s = new Scene();
        int hull = s.Node("Hull", mesh: true);
        foreach (string name in new[] { "Part\"one", "Part\"two" })
            s.Move(s.Node(name, hull, mesh: true), new[] { 0f, 1f, 0f });
        s.Finish();
        var names = BlenderNames.Compute(s.M);
        var baked = BlenderDeploy.Decide(s.M, Default.Split('|'), names, true);
        baked.Finish();
        baked.Keys["Part\"one"][0][0] = 2000000f;
        var x = BlenderDeployExport.Build(s.M, names, baked, Default.Split('|'));
        Assert.Null(x.Fallback);
        Assert.Equal(new[] { "Part\\" }, x.GarbageBones);
        Assert.StartsWith("DEPLOY sanitized: 1 garbage bone(s)", x.Log.Single(l => l.StartsWith("DEPLOY sanitized:")));
        Assert.True(x.GarbageCurves >= 14); // both bones lose their channels, though the printed key is shared
    }
}
