using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

public class VehicleRigStage5Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Finishing_doubles_role_meshes_and_keeps_preserved_meshes_single(bool global)
    {
        var m = Model(); Box(m, "Hull", 0, 0, 0); Box(m, "Banner", 0, 3, 0); Box(m, "Keep", 2, 0, 0);
        var a = Args(); a.DoubleSided = global; Mark(a, "flags", "Banner"); Mark(a, "preserve", "Keep");
        var s = Stage4(m, a); var x = Export(s, a);
        Assert.Equal(global ? 16 : 8, Mesh(s, "Mesh_Root").Vertices); Assert.Equal(16, Mesh(s, "Mesh_Flag").Vertices); Assert.Equal(8, Mesh(s, "Mesh___keep__Keep").Vertices);
        Assert.All(s.Objects.Where(o => o.Type == "MESH"), o => Assert.All(o.Mesh.DefWeight, w => Assert.Equal(new[] { 1f }, w)));
        Assert.Contains(x.Animations, name => name == "Furl"); Assert.Contains(x.Animations, name => name == "Spin");
        Assert.All(x.Meshes.SelectMany(me => me.Primitives), p => Assert.All(Enumerable.Range(0, p.VertexCount), v => Assert.Equal(1f, p.Weights.Skip(4 * v).Take(4).Sum())));
    }

    [Fact]
    public void Authored_oars_are_excluded_from_global_doubling()
    {
        var m = Model(); Box(m, "Hull", 0, 0, 0); Box(m, "Oar", 0, 1, 2, .03f, .03f, 2);
        var a = Args(); a.DoubleSided = true; Mark(a, "oars", "Oar");
        var s = Stage4(m, a); int before = Mesh(s, "Mesh___oar__Oar").Vertices; Export(s, a);
        Assert.Equal(before, Mesh(s, "Mesh___oar__Oar").Vertices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Marked_flip_composes_as_XOR_with_the_inside_out_fix(bool fix)
    {
        var m = Model(); Box(m, "Hull", 0, 0, 0); Box(m, "Deck", 3, 0, 0);
        // Reverse an outward box in the source; both passes together restore its authored winding.
        var tri = m.Meshes[1].Primitives[0].Indices; for (int i = 0; i < tri.Length; i += 3) (tri[i + 1], tri[i + 2]) = (tri[i + 2], tri[i + 1]);
        var a = Args(); a.FixInsideOut = fix; Mark(a, "flip", "Deck"); var s = Stage4(m, a);
        var before = (int[])Mesh(s, "Mesh___flip__Deck").Faces.Clone(); Export(s, a); var after = Mesh(s, "Mesh___flip__Deck").Faces;
        if (fix) Assert.Equal(before, after); else Assert.NotEqual(before, after);
    }

    [Fact]
    public void Export_keeps_the_scene_range_and_writes_the_full_longer_Furl_action()
    {
        var m = Model(); Box(m, "Hull", 0, 0, 0); Box(m, "Banner", 0, 3, 0);
        var a = Args(); Mark(a, "flags", "Banner"); a.FlagFoldOn = true; a.FlagFoldDeg = 110; a.FlagFoldFrames = 100;
        var s = Stage4(m, a); int frame = s.FrameCurrent; var x = Export(s, a);
        Assert.Equal(15, s.FrameEnd); Assert.Equal(frame, s.FrameCurrent);
        var furl = Assert.Single(x.ChannelRows.Animations.Where(an => an.Name == "Furl")); Assert.All(furl.Channels, c => Assert.Equal(100.0 / 24.0, c.MaxTime));
    }

    [Fact]
    public void Export_clears_an_empty_Spin_when_switching_to_the_only_keyed_action()
    {
        var m = Model(); Box(m, "Hull", 0, 0, 0); Box(m, "Tube", 0, 1.5f, -2, .2f, .2f, 4);
        var a = Args(); Mark(a, "guns", "Tube"); a.RecoilDist = .3;
        var s = Stage4(m, a); Assert.Equal("Spin", ((BlenderVehicleRig.RigState)s.VehicleState).Arm.Action);
        var x = Export(s, a); Assert.Equal("Recoil", Assert.Single(x.Animations)); Assert.Null(((BlenderVehicleRig.RigState)s.VehicleState).Arm.Action);
    }

    [Fact]
    public void Sharp_edges_split_exported_normals_even_on_smooth_faces()
    {
        var m = Model(); Box(m, "Hull", 0, 0, 0); var a = Args(); var s = Stage4(m, a); var mesh = Mesh(s, "Mesh_Root");
        mesh.EdgeSharp = Enumerable.Repeat(true, mesh.Edges.Length / 2).ToArray();
        var x = Export(s, a); Assert.Equal(24, Assert.Single(Assert.Single(x.Meshes).Primitives).VertexCount);
    }

    [Fact]
    public void Export_applies_flat_brightness_without_mutating_the_source_material()
    {
        var m = Model(); m.Materials.Add(new HafMaterial { Name = "Paint", BaseColorFactor = new double[] { .2, .4, .6, 1 } }); Box(m, "Hull", 0, 0, 0); m.Meshes[0].Primitives[0].Material = 0;
        var a = Args(); a.Bright1 = 1.5; var x = Export(Stage4(m, a), a);
        var color = x.MaterialRows.Materials[0]["pbrMetallicRoughness"]["baseColorFactor"];
        Assert.Equal((double)(float)((double)(float).2 * 1.5), (double)color[0]); Assert.Equal(.2, m.Materials[0].BaseColorFactor[0]);
    }

    [Fact]
    public void FBX_preview_is_an_explicit_decline()
    {
        var m = Model(); Box(m, "Hull", 0, 0, 0); var a = Args(); a.PreviewFbx = "preview.fbx";
        var s = Stage4(m, a); Assert.Null(BlenderVehicleRig.RunStage5(s, a)); Assert.Contains("FBX preview", s.Fallback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sampled_RNA_writeback_distinguishes_constant_zero_from_a_nonzero_to_zero_transition(bool moves)
    {
        var m = Model(); Box(m, "Hull", 0, 0, 0); var a = Args(); var s = Stage4(m, a); var st = (BlenderVehicleRig.RigState)s.VehicleState;
        var action = new BlenderRigAnim.ActionData { Name = "Sample" }; var slot = new BlenderRigAnim.SlotData { Identifier = "OB" + st.Arm.Name, Object = st.Arm }; action.Slots.Add(slot);
        var curve = new BlenderRigAnim.FCurveData { Bone = "Root", Prop = "rotation_quaternion", Index = 1 };
        curve.Keys.Add(new BlenderRigAnim.KeyData { Frame = 0, Value = 0f, Interpolation = "LINEAR" }); curve.Keys.Add(new BlenderRigAnim.KeyData { Frame = 1, Value = moves ? .25f : 0f, Interpolation = "LINEAR" }); curve.Keys.Add(new BlenderRigAnim.KeyData { Frame = 2, Value = -0f, Interpolation = "LINEAR" });
        slot.FCurves.Add(curve); s.Actions.Add(action); st.Arm.Action = "Sample"; st.Arm.Slot = slot.Identifier;
        BlenderVehicleRig.EvaluateExportFrame(s, 2, 0);
        Assert.Equal(moves ? int.MinValue : 0, BitConverter.ToInt32(BitConverter.GetBytes(s.Armature.PoseProps[st.BoneId["Root"]][1][1]), 0));
    }

    [Fact]
    public void Brightness_that_requires_pixel_changes_declines_explicitly()
    {
        var m = Model(); m.Materials.Add(new HafMaterial { Name = "Paint", BaseColorTexture = 0 }); m.Textures.Add(new HafTexture { Source = 0 });
        m.Images.Add(new HafImage { Name = "Pixel", MimeType = "image/png", Bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=") });
        Box(m, "Hull", 0, 0, 0); m.Meshes[0].Primitives[0].Material = 0; var a = Args(); a.Bright1 = 1.5;
        var s = Stage4(m, a); Assert.Null(BlenderVehicleRig.RunStage5(s, a)); Assert.Contains("brightness-edited image bytes", s.Fallback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BMesh_finishing_activates_an_inactive_joined_UV_layer(bool finish)
    {
        var m = Model(); Box(m, "Hull", 0, 0, 0); Box(m, "Panel", 3, 0, 0);
        m.Meshes[1].Primitives[0].Uv0 = new float[16];
        var a = Args(); a.DoubleSided = finish; var s = Stage4(m, a); var mesh = Mesh(s, "Mesh_Root");
        Assert.Null(mesh.Uv); Assert.Single(mesh.UvAll); Export(s, a); mesh = Mesh(s, "Mesh_Root");
        if (finish) Assert.Same(mesh.UvAll[0], mesh.Uv); else Assert.Null(mesh.Uv);
    }

    [Fact]
    public void Finishing_switches_use_Pythons_strip_rules()
    {
        var argv = Enumerable.Repeat("", 76).ToList(); argv[0] = "rig"; argv[1] = "scene.glb"; argv[7] = "15"; argv[8] = "-360"; argv[41] = "\u001c1\u001f"; argv[46] = " 1 ";
        var a = BlenderVehicleRig.ParseArgs(argv); Assert.True(a.DoubleSided); Assert.True(a.FixInsideOut);
    }

    static BlenderDeployExport.Result Export(BlenderRigAnim.Stage1 s, BlenderVehicleRig.Args a)
    {
        var x = BlenderVehicleRig.RunStage5(s, a); Assert.Null(s.Fallback); Assert.NotNull(x); Assert.Null(x.Fallback); return x;
    }
    static BlenderRigAnim.MeshData Mesh(BlenderRigAnim.Stage1 s, string name) => Assert.Single(s.Objects.Where(o => o.Name == name)).Mesh;
    static HafModel Model() { var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene()); return m; }
    static BlenderVehicleRig.Args Args()
    {
        var argv = Enumerable.Repeat("", 76).ToList(); argv[0] = "rig"; argv[1] = "scene.glb"; argv[2] = "out.glb"; argv[7] = "15"; argv[8] = "-360"; argv[17] = "0"; argv[21] = "0";
        return BlenderVehicleRig.ParseArgs(argv);
    }
    static void Mark(BlenderVehicleRig.Args a, string role, params string[] names) { a.Names[role] = names.ToList(); foreach (string name in names) a.Marked.Add(name); }
    static BlenderRigAnim.Stage1 Stage4(HafModel m, BlenderVehicleRig.Args a)
    {
        var s = BlenderVehicleRig.RunStage1(m, a); BlenderVehicleRig.RunStage2(s, a); BlenderVehicleRig.RunStage3(s, a); BlenderVehicleRig.RunStage4(s, a);
        Assert.Null(s.Fallback); Assert.Null(s.Dies); return s;
    }
    static void Box(HafModel m, string name, float x, float y, float z, float sx = 1, float sy = 1, float sz = 1)
    {
        var points = new List<float>(); foreach (int dx in new[] { -1,1 }) foreach (int dy in new[] { -1,1 }) foreach (int dz in new[] { -1,1 }) points.AddRange(new[] { x + dx * sx / 2, y + dy * sy / 2, z + dz * sz / 2 });
        var mesh = new HafMesh { Name = name }; mesh.Primitives.Add(new HafPrimitive { Positions = points.ToArray(), Indices = new[] { 0,1,3,0,3,2,4,6,7,4,7,5,0,4,5,0,5,1,2,3,7,2,7,6,0,2,6,0,6,4,1,5,7,1,7,3 } });
        m.Nodes.Add(new HafNode { Name = name, Mesh = m.Meshes.Count }); m.Meshes.Add(mesh); m.Scenes[0].Nodes.Add(m.Nodes.Count - 1);
    }
}
