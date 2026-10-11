using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>BlenderChannelExport: the animations' channels as the glTF exporter samples them (part 8b).
/// tools/deploy_drill.sh holds whole jobs to Blender's channel rows (every sampled key as bytes); here each rule on a
/// model small enough to read.</summary>
public class BlenderChannelExportTests
{
    const string Default = "0|24|||||||0|||4|0|1";

    sealed class Scene
    {
        public readonly HafModel M = new HafModel();
        readonly HafAnimation anim = new HafAnimation { Name = "Deploy" };

        public int Node(string name, int parent = -1, bool mesh = false, double[] t = null)
        {
            var n = new HafNode { Name = name, Parent = parent };
            if (t != null) n.Translation = t;
            if (mesh)
            {
                var p = new HafPrimitive { VertexCount = 3, Positions = new float[] { -1, 0, 0, 1, 0, 0, 0, 1, 0.5f } };
                var hm = new HafMesh { Name = name.ToLowerInvariant() + "_mesh" }; hm.Primitives.Add(p);
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

        public (BlenderDeploy.Result baked, BlenderDeployExport.Result export) Export(string args = Default)
        {
            if (M.Scenes.Count == 0)
            {
                var sc = new HafScene(); sc.Nodes.AddRange(Enumerable.Range(0, M.Nodes.Count).Where(i => M.Nodes[i].Parent < 0)); M.Scenes.Add(sc); M.Scene = 0;
                M.Animations.Add(anim);
            }
            var names = BlenderNames.Compute(M);
            var r = BlenderDeploy.Decide(M, args.Split('|'), names, true);
            Assert.Null(r.Fallback);
            r.Finish();
            Assert.Null(r.RoleFallback);
            var x = BlenderDeployExport.Build(M, names, r, args.Split('|'));
            Assert.Null(x.Fallback);
            return (r, x);
        }
    }

    /// <summary>A hull and a gun that slides along X: the bake rebases the motion onto StaticRoot (the root-motion
    /// anchor), so StaticRoot's translation moves and Gun's own stands still.</summary>
    static Scene Tank()
    {
        var s = new Scene();
        int hull = s.Node("Hull", mesh: true);
        int gun = s.Node("Gun", hull, mesh: true, t: new double[] { 1, 0.5, 0 }); s.Move(gun, new[] { 2.5f, 0.5f, 0f });
        return s;
    }

    static BlenderChannelExport.Animation Anim(BlenderDeployExport.Result x, string name) => x.ChannelRows.Animations[x.Animations.IndexOf(name)];
    static BlenderChannelExport.Channel Of(BlenderDeployExport.Result x, string anim, string bone, string path) =>
        Anim(x, anim).Channels.Single(c => x.Nodes[c.Node].Name == bone && c.Path == path);

    [Fact]
    public void Every_bone_gets_translation_rotation_scale_in_the_walks_order_one_sampler_each()
    {
        var (baked, x) = Tank().Export();
        Assert.Contains("deploy", x.Animations);
        var bones = baked.BonesAfterRecoil;
        // the vtree's walk: root bones in pose order, each followed by its children
        var walk = new List<string>();
        void Visit(BlenderDeploy.Bone parent) { foreach (string n in baked.BoneOrder) { var b = bones.Single(q => q.Name == n); if (b.Parent == parent) { walk.Add(n); Visit(b); } } }
        Visit(null);
        foreach (var a in x.ChannelRows.Animations)
        {
            Assert.Equal(3 * bones.Count, a.Channels.Count);
            for (int i = 0; i < a.Channels.Count; i++)
            {
                Assert.Equal(i, a.Channels[i].Sampler);
                Assert.Equal(new[] { "translation", "rotation", "scale" }[i % 3], a.Channels[i].Path);
                Assert.Equal(walk[i / 3], x.Nodes[a.Channels[i].Node].Name);
                Assert.True(x.Nodes[a.Channels[i].Node].Joint);
            }
        }
    }

    [Fact]
    public void A_moving_bone_samples_every_frame_LINEAR_a_still_one_keeps_its_first_and_last_STEP()
    {
        var (_, x) = Tank().Export();
        var root = Of(x, "deploy", "StaticRoot", "translation");
        Assert.Equal("LINEAR", root.Interpolation);
        Assert.Equal(25, root.Times.Length); Assert.Equal(75, root.Values.Length);
        for (int k = 0; k < 25; k++) Assert.Equal((float)(k / 24.0), root.Times[k]);
        Assert.NotEqual(root.Values[0], root.Values[72]);   // it moved along X
        var gun = Of(x, "deploy", "Gun", "translation");
        Assert.Equal("STEP", gun.Interpolation);
        Assert.Equal(new[] { 0f, 1f }, gun.Times);
        Assert.Equal(6, gun.Values.Length);
        Assert.Equal(gun.Values.Take(3), gun.Values.Skip(3));
        // the bake keys the scale too: constant up to float noise, kept as two samples (STEP when the two are equal within
        // one float step, else LINEAR - the exporter's own rule, held by the drill on 197 jobs)
        Assert.Equal(2, Of(x, "deploy", "Gun", "scale").Times.Length);
        Assert.Equal(6, Of(x, "deploy", "Gun", "scale").Values.Length);
    }

    [Fact]
    public void A_role_of_two_identical_frames_keeps_two_STEP_keys_at_the_frames_it_was_keyed()
    {
        var (_, x) = Tank().Export();
        foreach (var c in Anim(x, "deployed").Channels)
        {
            Assert.Equal("STEP", c.Interpolation);
            Assert.Equal(new[] { 0f, (float)(1 / 24.0) }, c.Times);   // keyed at fmin and fmin + 1
        }
        // unfold replays the deploy: the root moves over 25 frames there too
        var root = Of(x, "unfold", "StaticRoot", "translation");
        Assert.Equal("LINEAR", root.Interpolation); Assert.Equal(25, root.Times.Length);
        Assert.Equal(Of(x, "deploy", "StaticRoot", "translation").Values, root.Values);
    }

    [Fact]
    public void The_scenes_range_cuts_every_action_and_drops_the_ones_outside_it()
    {
        var (_, x) = Tank().Export("5|24|||||||0|||4|0|1");
        Assert.DoesNotContain("deployed", x.Animations);   // keyed at 0 and 1: outside 5..24
        Assert.DoesNotContain("folded", x.Animations);
        var root = Of(x, "deploy", "StaticRoot", "translation");
        Assert.Equal(20, root.Times.Length);
        Assert.Equal((float)(5 / 24.0), root.Times[0]); Assert.Equal(1f, root.Times[19]);
    }

    [Fact]
    public void The_active_action_is_left_unreset_only_when_it_sorts_first()
    {
        var (baked, x) = Tank().Export();
        // StaticRoot without curves in the deploy action: a property without a curve holds the pose the script left (the
        // root at the deploy's end, moved along X), unless the exporter switched the armature away before reaching "deploy"
        baked.Keys.Remove("StaticRoot");
        BlenderChannelExport.Channel Root(BlenderChannelExport.Result res) => res.Animations[x.Animations.IndexOf("deploy")].Channels.Single(c => x.Nodes[c.Node].Name == "StaticRoot" && c.Path == "translation");
        var held = BlenderChannelExport.Build(baked, x, x.SceneStart, x.SceneEnd);
        Assert.Null(held.Fallback);
        Assert.Equal("STEP", Root(held).Interpolation);
        // a role named below "deploy" in the sort: the exporter switches to it first, and "deploy" is reset like the rest
        var aim = new BlenderDeploy.Role { Name = "aim", Slot = "OBDeployArm" };
        var deployed = baked.Roles.Single(ro => ro.Name == "deployed");
        foreach (var kv in deployed.Curves) aim.Curves[kv.Key] = kv.Value;
        aim.BoneOrder.AddRange(deployed.BoneOrder);
        baked.Roles.Add(aim); x.Animations.Insert(0, "aim");
        var reset = BlenderChannelExport.Build(baked, x, x.SceneStart, x.SceneEnd);
        Assert.Null(reset.Fallback);
        var resetValues = Root(reset).Values;   // looked up while "aim" still sits first in the animations
        Assert.NotEqual(Root(held).Values, resetValues);
        // reset, the root's location is 0: the same as the script leaving it at the identity
        baked.Roles.Remove(aim); x.Animations.RemoveAt(0);
        baked.ArmPose7["StaticRoot"] = ("QUATERNION", new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f });
        var identity = BlenderChannelExport.Build(baked, x, x.SceneStart, x.SceneEnd);
        Assert.Equal(Root(identity).Values, resetValues);
    }

    [Fact]
    public void Two_samples_are_equal_within_one_float_step_signed_zeros_included()
    {
        float up = BitConverter.ToSingle(BitConverter.GetBytes(BitConverter.ToUInt32(BitConverter.GetBytes(1f), 0) + 1), 0);
        float up2 = BitConverter.ToSingle(BitConverter.GetBytes(BitConverter.ToUInt32(BitConverter.GetBytes(1f), 0) + 2), 0);
        Assert.True(BlenderChannelExport.FloatsAreEqual(1f, up, 1));
        Assert.False(BlenderChannelExport.FloatsAreEqual(1f, up2, 1));
        Assert.True(BlenderChannelExport.FloatsAreEqual(0f, -0f, 1));
        Assert.False(BlenderChannelExport.FloatsAreEqual(1f, -1f, 1));
    }

    [Fact]
    public void Euler_XYZ_angles_become_the_matrix_Blender_makes()
    {
        var id = BlenderChannelExport.EulXyzToMat3(new[] { 0f, 0f, 0f });
        for (int c = 0; c < 3; c++) for (int r = 0; r < 3; r++) Assert.Equal(c == r ? 1f : 0f, id[c][r]);
        var x90 = BlenderChannelExport.EulXyzToMat3(new[] { (float)(Math.PI / 2), 0f, 0f });   // mat[col][row]: Y -> Z, Z -> -Y
        Assert.Equal(1f, x90[0][0]);
        Assert.InRange(x90[1][2], 0.9999999f, 1.0000001f); Assert.InRange(Math.Abs(x90[1][1]), 0f, 1e-7f);
        Assert.InRange(x90[2][1], -1.0000001f, -0.9999999f); Assert.InRange(Math.Abs(x90[2][2]), 0f, 1e-7f);
    }

    [Fact]
    public void The_root_correction_turns_Y_up_and_writes_the_quaternion_x_y_z_w()
    {
        var basis = new float[] { 1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1 };   // the Y-up basis change, row-major items
        var sample = new[] { 1f, 2f, 3f, 1f, 0f, 0f, 0f, 1f, 1f, 1f };               // loc, quat (w, x, y, z), size
        Assert.Equal(new[] { 1f, 3f, -2f }, BlenderChannelExport.Transform("translation", sample, 0, basis));
        var q = BlenderChannelExport.Transform("rotation", sample, 3, basis);        // a -90 degree turn about X, written (x, y, z, w)
        Assert.InRange(q[0], -0.70710681f, -0.70710675f); Assert.Equal(0f, q[1]); Assert.Equal(0f, q[2]); Assert.InRange(q[3], 0.70710675f, 0.70710681f);
        Assert.Equal(new[] { 1f, 1f, 1f }, BlenderChannelExport.Transform("scale", sample, 7, basis));
        var identity = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        Assert.Equal(new[] { 0f, 0f, 0f, 1f }, BlenderChannelExport.Transform("rotation", sample, 3, identity));
    }
}
