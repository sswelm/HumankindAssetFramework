using System;
using System.IO;
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
}
