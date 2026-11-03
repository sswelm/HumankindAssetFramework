using System;
using System.IO;
using System.Linq;
using Xunit;

// Step 6e of the native FBX reader: editor/HafModelLoader.cs - the one loader of a model source for the in-process ports - and
// the routes that read through it (BlenderVehicleRig.SourceFallback, BlenderVehicleRigInProcess.ExpectedFallback,
// BlenderRigAnim.RunStage1). The FBX reader itself is held to Blender by tools/fbx_model_drill.sh and the Fbx*Tests.
public class HafModelLoaderTests
{
    [Fact]
    public void The_loader_reads_gltf_and_fbx_and_names_the_rest()
    {
        Assert.True(HafModelLoader.CanRead("a.glb")); Assert.True(HafModelLoader.CanRead("A.GLTF")); Assert.True(HafModelLoader.CanRead(" x.fbx "));
        Assert.False(HafModelLoader.CanRead("a.obj")); Assert.False(HafModelLoader.CanRead("a.blend")); Assert.False(HafModelLoader.CanRead(null)); Assert.False(HafModelLoader.CanRead(""));
        Assert.True(HafModelLoader.IsFbx("D:/3DModels/robo.FBX")); Assert.False(HafModelLoader.IsFbx("robo.glb"));
        Assert.Throws<NotSupportedException>(() => HafModelLoader.Read("model.obj", out _));
        // a missing file is the reader's own exception, not a named leave
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".fbx");
        Assert.ThrowsAny<IOException>(() => HafModelLoader.Read(missing, out _));
        Assert.True(HafModelLoader.IsNamedFbxLeave(HafModelLoader.FbxLeavePrefix + "a camera or light"));
        Assert.False(HafModelLoader.IsNamedFbxLeave("the C# reader cannot handle this input: x")); Assert.False(HafModelLoader.IsNamedFbxLeave(null));
    }

    [Fact]
    public void An_fbx_source_is_no_longer_left_by_extension_and_a_named_fbx_leave_is_an_expected_fallback()
    {
        Assert.Null(BlenderVehicleRig.SourceFallback(new BlenderVehicleRig.Args { Input = "tank.fbx" }));
        Assert.Null(BlenderVehicleRig.SourceFallback(new BlenderVehicleRig.Args { Input = "tank.glb" }));
        Assert.Equal("a .obj source: Blender's own importer", BlenderVehicleRig.SourceFallback(new BlenderVehicleRig.Args { Input = "tank.obj" }));
        Assert.Equal("a .blend source: Blender's own importer", BlenderVehicleRig.SourceFallback(new BlenderVehicleRig.Args { Input = "tank.blend" }));
        // the vehicle acceptance expects a source the C# does not read, and an .fbx the FBX reader leaves by name; nothing else
        Assert.True(BlenderVehicleRigInProcess.ExpectedFallback("tank.obj", "a .obj source: Blender's own importer"));
        Assert.True(BlenderVehicleRigInProcess.ExpectedFallback("tank.fbx", HafModelLoader.FbxLeavePrefix + "shape keys (not modelled)"));
        Assert.False(BlenderVehicleRigInProcess.ExpectedFallback("tank.fbx", "a .fbx source: Blender's own importer, until the native reader"));
        Assert.False(BlenderVehicleRigInProcess.ExpectedFallback("tank.glb", "the C# reader cannot handle this input: Draco"));
        // rig_anim's stage 1 declines a .blend source by its extension still, an .fbx no longer (the model decides)
        var m = new HafModel { Generator = "test" };
        var blend = BlenderRigAnim.RunStage1(m, BlenderRigAnim.ParseArgs(new[] { "rig.blend", "out.glb", "0", "", "", "0", "0", "0,0,0", "0", "", "0", "", "0", "", "0" }));
        Assert.Equal("a .blend source: Blender's own importer", blend.Fallback);
        var fbx = BlenderRigAnim.RunStage1(m, BlenderRigAnim.ParseArgs(new[] { "rig.fbx", "out.glb", "0", "", "", "0", "0", "0,0,0", "0", "", "0", "", "0", "", "0" }));
        Assert.DoesNotContain(".fbx source", fbx.Fallback ?? "");
    }
    static string Fixture(string name) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "fbx_loader", name + ".fbx");

    [Fact]
    public void Static_fbx_loads_for_the_Factory_and_the_vehicle_probe()
    {
        var model = HafModelLoader.ReadForVehicle(Fixture("static"), out string leave);
        Assert.Null(leave); Assert.NotNull(model); Assert.Single(model.Meshes);
        Assert.Equal("Mesh", Assert.Single(VehicleProbe.Run(model).Parts).Name);
    }

    [Fact]
    public void A_skinned_animated_fbx_still_loads_for_the_Factory_but_the_vehicle_route_names_its_pose_limit()
    {
        string path = Fixture("animated_skin");
        var model = HafModelLoader.Read(path, out string leave);
        Assert.Null(leave); Assert.NotEmpty(model.Skins); Assert.NotEmpty(model.Animations);
        Assert.Null(HafModelLoader.ReadForVehicle(path, out leave));
        Assert.StartsWith(HafModelLoader.FbxVehicleLeavePrefix, leave);
        Assert.True(BlenderVehicleRigInProcess.ExpectedFallback(path, leave));
        Assert.Throws<NotSupportedException>(() => HafModelLoader.ReadForVehicleOrThrow(path));
        string output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".glb");
        var argv = Enumerable.Repeat("", 76).ToArray();
        argv[0] = "rig"; argv[1] = path; argv[2] = output; argv[7] = "15"; argv[8] = "-360"; argv[17] = "0"; argv[21] = "0";
        var run = BlenderVehicleRigInProcess.Run(argv);
        Assert.Equal(leave, run.UseBlender); Assert.False(run.Written); Assert.False(File.Exists(output));
        // The same context guard applies to merge2: the primary can be glTF while the declined second input is FBX.
        Assert.True(BlenderVehicleRigInProcess.ExpectedFallback("primary.glb", leave));
    }

    [Theory]
    [InlineData("source.fbx", true, false, true)]
    [InlineData("source.FBX", false, true, true)]
    [InlineData("source.fbx", false, false, false)]
    [InlineData("source.glb", true, true, false)]
    [InlineData("source.gltf", true, true, false)]
    public void The_vehicle_limit_is_specific_to_FBX_skin_or_animation(string source, bool skin, bool animation, bool declines)
    {
        var model = new HafModel();
        if (skin) model.Skins.Add(new HafSkin());
        if (animation) model.Animations.Add(new HafAnimation());
        Assert.Equal(declines, HafModelLoader.VehicleImportFallback(source, model) != null);
    }

    // The rig route's direct-import limit (2026-11-03, the light assault mech): rig_anim.py on the FBX route imports the .fbx
    // directly, the reader is Blender's glTF round trip of it, and the two rigs are not the same - the GLB route baked scattered
    // pieces. A skinned or animated .fbx leaves to the FBX route by name; a static one is read as before.
    [Theory]
    [InlineData("source.fbx", true, false, true)]
    [InlineData("source.FBX", false, true, true)]
    [InlineData("source.fbx", false, false, false)]
    [InlineData("source.glb", true, true, false)]
    [InlineData("source.gltf", true, true, false)]
    public void The_rig_limit_is_specific_to_FBX_skin_or_animation(string source, bool skin, bool animation, bool declines)
    {
        var model = new HafModel();
        if (skin) model.Skins.Add(new HafSkin());
        if (animation) model.Animations.Add(new HafAnimation());
        string why = HafModelLoader.RigImportFallback(source, model);
        Assert.Equal(declines, why != null);
        if (declines) { Assert.StartsWith(HafModelLoader.FbxRigLeavePrefix, why); Assert.True(HafModelLoader.IsNamedFbxLeave(why)); }
    }

    [Fact]
    public void A_skinned_animated_fbx_leaves_the_rig_route_to_Blender_by_name_and_a_static_one_does_not()
    {
        Assert.NotNull(HafModelLoader.ReadForRig(Fixture("static"), out string leave)); Assert.Null(leave);
        string path = Fixture("animated_skin");
        Assert.NotNull(HafModelLoader.Read(path, out leave)); Assert.Null(leave);   // the reader itself takes it
        Assert.Null(HafModelLoader.ReadForRig(path, out leave));
        Assert.StartsWith(HafModelLoader.FbxRigLeavePrefix, leave);
        Assert.True(HafModelLoader.IsNamedFbxLeave(leave));   // an EXPECTED fallback for the Factory's route bookkeeping, not a crash
        // the in-process run names it before any stage runs: no file, no RIGANIM line, the Factory takes its FBX route
        string output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".glb");
        var argv = BlenderRigAnimInProcess.Argv(0, "", "", "", true, "0,0,0", true, "", false, "", false, "", false);
        var run = BlenderRigAnimInProcess.Run(path, output, argv);
        Assert.Equal(leave, run.UseBlender); Assert.False(run.Written); Assert.False(run.ScriptExit); Assert.Empty(run.Log); Assert.False(File.Exists(output));
        // deploy_convert.py imports an .fbx directly too: the same leave, before any step
        var deploy = BlenderDeployInProcess.Run(path, output, BlenderDeployInProcess.Argv("0", "24", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", ""));
        Assert.Equal(leave, deploy.UseBlender); Assert.False(deploy.Written); Assert.False(File.Exists(output));
    }
}
