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
}
