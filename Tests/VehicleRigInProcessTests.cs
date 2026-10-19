using System;
using System.IO;
using System.Linq;
using Xunit;

public class VehicleRigInProcessTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "vehicle-wrapper-" + Guid.NewGuid().ToString("N"));
    public VehicleRigInProcessTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { Directory.Delete(dir, true); }
    string[] Args()
    {
        string src = Path.Combine(dir, "source.glb");
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene()); m.Scenes[0].Nodes.Add(0);
        m.Nodes.Add(new HafNode { Name = "Hull", Mesh = 0 }); var mesh = new HafMesh { Name = "Hull" }; m.Meshes.Add(mesh);
        mesh.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[] { -1,0,0, 1,0,0, 0,1,1 }, Indices = new[] { 0,1,2 } });
        GlbWriter.Write(m, src);
        var a = Enumerable.Repeat("",76).ToArray(); a[0]="rig"; a[1]=src; a[2]=Path.Combine(dir,"out.glb"); a[7]="15"; a[8]="-360"; a[17]="0"; a[21]="0"; return a;
    }
    [Theory]
    [InlineData(null,null,false)] [InlineData("", "csharp",true)] [InlineData("blender","csharp",false)]
    [InlineData("csharp","blender",true)] [InlineData(" CSharp ","blender",true)] [InlineData("unknown", "bad",false)]
    public void Route_overrides_default_without_changing_the_missing_preference_default(string recipe,string editor,bool expected)
        => Assert.Equal(expected,BlenderVehicleRigInProcess.UseCSharp(recipe,editor));
    [Fact]
    public void Writes_and_atomically_replaces_a_readable_rig_with_ordered_completion_log()
    {
        var a=Args(); File.WriteAllText(a[2],"old"); var o=BlenderVehicleRigInProcess.Run(a);
        Assert.True(o.Written,o.UseBlender); Assert.False(o.ScriptExit); Assert.Null(o.UseBlender);
        var back=GlbReader.Read(a[2]); Assert.Single(back.Skins); Assert.Single(back.Meshes);
        Assert.StartsWith("VEHICLE RIG DONE:",o.Log.Last()); Assert.Empty(Directory.GetFiles(dir,"*.tmp"));
    }
    [Fact]
    public void A_failed_final_write_does_not_claim_completion_or_leave_a_temporary_file()
    {
        var a=Args(); Directory.CreateDirectory(a[2]); var o=BlenderVehicleRigInProcess.Run(a);
        Assert.False(o.Written); Assert.NotNull(o.UseBlender); Assert.DoesNotContain(o.Log,l=>l.StartsWith("VEHICLE RIG DONE:"));
        Assert.True(Directory.Exists(a[2])); Assert.Empty(Directory.GetFiles(dir,"*.tmp"));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Cancellation_or_deadline_after_temp_write_preserves_the_old_output_and_cleans_up(bool cancel)
    {
        var a=Args(); File.WriteAllText(a[2],"old"); int ticks=0;
        Action checkpoint=()=> { if (++ticks==8) { if(cancel) throw new OperationCanceledException(); throw new TimeoutException("deadline"); } };
        if(cancel) Assert.Throws<OperationCanceledException>(()=>BlenderVehicleRigInProcess.Run(a,checkpoint));
        else { var o=BlenderVehicleRigInProcess.Run(a,checkpoint); Assert.False(o.Written); Assert.Contains("deadline",o.UseBlender); }
        Assert.Equal(8,ticks); Assert.Equal("old",File.ReadAllText(a[2])); Assert.Empty(Directory.GetFiles(dir,"*.tmp"));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Script_exit_and_known_death_do_not_fall_back_or_replace_output(bool dies)
    {
        var a=Args(); File.WriteAllText(a[2],"old"); if(dies)a[7]="15.0";else a[4]="Missing";
        var o=BlenderVehicleRigInProcess.Run(a); Assert.True(o.ScriptExit); Assert.False(o.Written); Assert.Null(o.UseBlender);
        if(dies)Assert.Contains("ValueError",o.ScriptError);else Assert.Contains(o.Log,l=>l.StartsWith("VEHICLE ERROR"));
        Assert.Equal("old",File.ReadAllText(a[2]));
    }
    [Theory] [InlineData("preview")] [InlineData("fast")] [InlineData("reader")]
    public void Named_limits_and_reader_failures_preserve_existing_output(string kind)
    {
        var a=Args(); File.WriteAllText(a[2],"old"); if(kind=="preview")a[3]="preview.fbx";else if(kind=="fast")a[0]="rigfast";else File.WriteAllText(a[1],"broken");
        var o=BlenderVehicleRigInProcess.Run(a); Assert.False(o.Written); Assert.False(o.ScriptExit); Assert.NotNull(o.UseBlender); Assert.Equal("old",File.ReadAllText(a[2]));
    }
    [Fact]
    public void Merge_uses_the_same_reader_and_a_missing_second_model_is_the_scripts_exit()
    {
        var a=Args(); var merged=a.Concat(new[]{"merge2="+a[1]+"|3,0,0|0,0,0|1"}).ToArray();
        var o=BlenderVehicleRigInProcess.Run(merged); Assert.True(o.Written,o.UseBlender); Assert.Contains(o.Log,l=>l.StartsWith("VEHICLE MERGE:"));
        merged[76]="merge2="+a[1]+".missing|0,0,0|0,0,0|1"; var bad=BlenderVehicleRigInProcess.Run(merged); Assert.True(bad.ScriptExit); Assert.Null(bad.UseBlender);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void JPEG_alpha_declines_before_reduction_and_preserves_the_previous_output(bool ignore)
    {
        var a = Args(); var m = GlbReader.Read(a[1]);
        m.Materials.Add(new HafMaterial { Name = "Glass", AlphaMode = "BLEND", BaseColorTexture = 0 });
        m.Textures.Add(new HafTexture { Source = 0 });
        m.Images.Add(new HafImage { Name = "Photo", MimeType = "image/jpeg", Bytes = new byte[] { 255, 216, 255, 224, 0, 0 } });
        m.Meshes[0].Primitives[0].Material = 0;
        if (ignore)
        {
            m.Nodes[0].Name = "Discard"; a[9] = "Discard";
            m.Nodes.Add(new HafNode { Name = "Hull", Mesh = 1 }); m.Scenes[0].Nodes.Add(1);
            var mesh = new HafMesh { Name = "Hull" }; m.Meshes.Add(mesh);
            mesh.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[] { -1,0,0, 1,0,0, 0,1,1 }, Indices = new[] { 0,1,2 } });
        }
        GlbWriter.Write(m, a[1]); File.WriteAllText(a[2], "old"); int ticks = 0;
        var result = BlenderVehicleRigInProcess.Run(a, () => ticks++);
        if (ignore) { Assert.True(result.Written, result.UseBlender); Assert.Null(result.UseBlender); }
        else
        {
            Assert.Contains("JPEG whose alpha is read", result.UseBlender); Assert.False(result.Written);
            Assert.Equal(2, ticks); Assert.Equal("old", File.ReadAllText(a[2]));
            Assert.DoesNotContain(result.Log, l => l.StartsWith("VEHICLE armature:") || l.StartsWith("VEHICLE RIG DONE:"));
        }
    }

    [Theory]
    [InlineData("BLEND")]
    [InlineData("MASK")]
    public void JPEG_alpha_generation_writes_a_native_rig_and_keeps_its_source_encoding(string mode)
    {
        var a = Args(); var m = GlbReader.Read(a[1]);
        m.Materials.Add(new HafMaterial { Name = "Glass", AlphaMode = mode, BaseColorTexture = 0, BaseColorFactor = new[] { 1d,1d,1d,.5 } });
        m.Textures.Add(new HafTexture { Source = 0 });
        byte[] jpeg = { 255,216,255,224,0,16,74,70,73,70,0,1,1,0,0,1,0,1,0,0,255,192,0,17,8,0,2,0,4,3,1,34,0,2,17,1,3,17,1,255,217 };
        m.Images.Add(new HafImage { Name = "Photo", MimeType = "image/jpeg", Bytes = jpeg });
        m.Meshes[0].Primitives[0].Material = 0;
        GlbWriter.Write(m,a[1]); File.WriteAllText(a[2],"old"); a[17] = "2";
        var result = BlenderVehicleRigInProcess.Run(a);
        Assert.True(result.Written,result.UseBlender); Assert.Null(result.UseBlender);
        var written = GlbReader.Read(a[2]);
        Assert.Equal(mode,written.Materials[0].AlphaMode);
        Assert.Equal(.5,written.Materials[0].BaseColorFactor[3]);
        Assert.Single(written.Images); Assert.Equal("image/jpeg",written.Images[0].MimeType);
        Assert.Equal(jpeg,written.Images[0].Bytes);
        Assert.NotEmpty(written.Meshes); Assert.NotEmpty(written.Nodes);
        var slim = BlenderRigAnimInProcess.Run(a[2], a[2] + ".slim.glb",
            BlenderRigAnimInProcess.Argv(0,"","Spin","",true,"0,0,0",true,"",false,"",true,"",false));
        Assert.True(slim.Written, slim.UseBlender ?? string.Join("\n",slim.Log));
        Assert.Single(slim.RetainedJpegAlphaImages);
        var prepared = GlbReader.Read(a[2] + ".slim.glb");
        Assert.Equal(jpeg, prepared.Images[0].Bytes); Assert.Equal("image/jpeg", prepared.Images[0].MimeType);
    }

    [Fact]
    public void Material_preflight_keeps_the_import_context_and_supported_output_unchanged()
    {
        var tokens = Args(); var m = GlbReader.Read(tokens[1]);
        m.Materials.Add(new HafMaterial { Name = "Paint", BaseColorFactor = new double[] { .2,.4,.6,1 } });
        m.Meshes[0].Primitives[0].Material = 0;
        var a = BlenderVehicleRig.ParseArgs(tokens.ToList()); a.Bright1 = 1.5;
        var s = BlenderVehicleRig.RunStage1(m, a); var names = s.Names.MaterialOf; var order = s.Names.MaterialsInOrder;
        Assert.Null(BlenderVehicleRig.ExportMaterialFallback(s, a));
        Assert.Same(m, s.Model); Assert.Same(names, s.Names.MaterialOf); Assert.Same(order, s.Names.MaterialsInOrder);
        Assert.Equal(.2, m.Materials[0].BaseColorFactor[0]);
        BlenderVehicleRig.RunStage2(s, a); BlenderVehicleRig.RunStage3(s, a); BlenderVehicleRig.RunStage4(s, a);
        var export = BlenderVehicleRig.RunStage5(s, a);
        Assert.Null(s.Fallback); Assert.Null(export.Fallback);
        Assert.Equal((double)(float)((double)(float).2 * 1.5), (double)export.MaterialRows.Materials[0]["pbrMetallicRoughness"]["baseColorFactor"][0]);
    }

}
