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
        Assert.Equal(new[] { "Gun", "Hat", "Hull" }, r.Objects.Select(o => o.Name));
        Assert.Null(r.Objects[1].Parent);
        // the hat keeps its own transform: where its parent was no longer counts (glTF y 1 is Blender z 1)
        Assert.Equal(1f, r.Objects[1].World[14]); Assert.Equal(0f, r.Objects[1].World[13]);
        Assert.Equal("DEPLOY after strip: 3 objects, 3 meshes: Gun, Hat, Hull", r.Log[0]);
        Assert.Equal(new[] { "Gun" }, r.Parts.Select(p => p.Name));
        Assert.True(r.Legacy);
        Assert.Contains($"   part: {"Gun",-40} parent=Hull", r.Log);
    }

    [Fact]
    public void A_strip_list_given_replaces_the_default_one_and_an_empty_name_in_it_takes_everything()
    {
        Scene Make() { var s = new Scene(); int hull = s.Node("Hull", mesh: true); s.Node("Soldier", hull, mesh: true); s.Move(s.Node("Gun", hull, mesh: true)); return s; }
        Assert.Equal(new[] { "Gun", "Hull" }, Make().Decide().Objects.Select(o => o.Name));   // the default list takes the soldier
        Assert.Equal(new[] { "Hull", "Soldier" }, Make().Decide("0|24| GUN ||||||0|||4|0|1").Objects.Select(o => o.Name));
        // stripExtra (the script's argv[16]) comes on top of the default list
        Assert.Equal(new[] { "Hull" }, Make().Decide("0|24|||||||0|||4|0|1|gun").Objects.Select(o => o.Name));
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
        Assert.Equal(new[] { "Good", "Hull" }, r.Objects.Select(o => o.Name));
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
