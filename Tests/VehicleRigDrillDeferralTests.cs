using Xunit;

// The drill's stage 5 "left by name" gate (review of #187): the named deferrals pass, a refusal of the model's own consistency fails.
public class VehicleRigDrillDeferralTests
{
    [Theory]
    [InlineData("stage 5: the export of brightness-edited image bytes is not modelled yet")]
    [InlineData("stage 5: the FBX preview export is not modelled yet")]
    [InlineData("stage 5: the GLB export (image 3: a JPEG whose alpha is read (the exporter writes it again as PNG)) is not modelled yet")]
    [InlineData("stage 5: the GLB export (image 0: a .webp file (the exporter writes WebP)) is not modelled yet")]
    [InlineData("stage 5: the GLB export (KHR_materials_sheen (not measured yet)) is not modelled yet")]
    [InlineData("stage 5: the GLB export (a specular-glossiness texture (not modelled)) is not modelled yet")]
    [InlineData("stage 5: the GLB export ('Mesh_Flag': a child of a skinned mesh node (its transform against the armature is not modelled)) is not modelled yet")]
    [InlineData("stage 5: the GLB export (an action of 200000 frames ('Spin': the sampler would run for hours)) is not modelled yet")]
    [InlineData("stage 5: the GLB export (a bone in rotation mode ZXY ('Wheel_0': only QUATERNION and XYZ are modelled)) is not modelled yet")]
    public void Named_deferrals_are_left_to_Blender(string fallback) => Assert.True(StageDrill.NamedStage5Deferral(fallback));

    [Theory]
    [InlineData("stage 5: the GLB export ('Mesh_Hull': a mesh without an Armature modifier at the export) is not modelled yet")]
    [InlineData("stage 5: the GLB export ('Mesh_Hull': its Armature modifier names no armature of the scene) is not modelled yet")]
    [InlineData("stage 5: the GLB export ('Mesh_Hull': a mesh that is not under its armature at the export) is not modelled yet")]
    [InlineData("stage 5: the GLB export ('Mesh_Hull': the material 'Paint' is none the importer made) is not modelled yet")]
    [InlineData("stage 5: the GLB export ('Mesh_Flag': hung from a bone the export has no node for) is not modelled yet")]
    [InlineData("stage 5: the GLB export (the bone 'Root' has no node) is not modelled yet")]
    [InlineData("stage 5: the GLB export (the animation 'Spin' names no action the port holds) is not modelled yet")]
    [InlineData("stage 5: the GLB export (the script exits before any export) is not modelled yet")]
    [InlineData("stage 5: the GLB export () is not modelled yet")]
    [InlineData("stage 5: the something else is not modelled yet")]
    [InlineData("stage 4: the clip of more than 100,000 frames is not modelled yet")]
    [InlineData("")]
    [InlineData(null)]
    public void Consistency_refusals_fail_the_job(string fallback) => Assert.False(StageDrill.NamedStage5Deferral(fallback));
}
