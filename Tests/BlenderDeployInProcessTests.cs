using System;
using System.IO;
using System.Linq;
using Xunit;

/// <summary>BlenderDeployInProcess: deploy_convert.py in this process, from the reader to the written file. The parts
/// are held to the script by tools/deploy_drill.sh; here the seam the Factory calls.</summary>
public class BlenderDeployInProcessTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "haf-deploy-inproc-" + Guid.NewGuid().ToString("N"));
    public BlenderDeployInProcessTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void Argv_is_the_scripts_argv_from_2_on_trimmed_with_nulls_as_empty()
    {
        var a = BlenderDeployInProcess.Argv(" 0 ", "24", null, "ready", "leg", "barrel", "443", "530/2", "step", "mag", "arcR", "4", "slam", "settle", "extra", " L_Wheel,R_Wheel ", "AUTO", "8", "-180");
        Assert.Equal(19, a.Length);
        Assert.Equal("0", a[0]); Assert.Equal("24", a[1]); Assert.Equal("", a[2]);
        // the port's Arg(n) reads argv[n]: 13 the return, 15 the settle, 16 the extra strip, 17..20 the wheels
        Assert.Equal("4", a[13 - 2]); Assert.Equal("settle", a[15 - 2]); Assert.Equal("extra", a[16 - 2]);
        Assert.Equal("L_Wheel,R_Wheel", a[17 - 2]); Assert.Equal("AUTO", a[18 - 2]); Assert.Equal("8", a[19 - 2]); Assert.Equal("-180", a[20 - 2]);
    }

    static HafModel Tank()
    {
        var m = new HafModel(); var anim = new HafAnimation { Name = "Deploy" };
        int Node(string name, int parent, bool mesh, double[] t)
        {
            var n = new HafNode { Name = name, Parent = parent }; if (t != null) n.Translation = t;
            if (mesh)
            {
                var p = new HafPrimitive { VertexCount = 3, Positions = new float[] { -1, 0, 0, 1, 0, 0, 0, 1, 0.5f }, Indices = new[] { 0, 1, 2 } };
                var hm = new HafMesh { Name = name.ToLowerInvariant() + "_mesh" }; hm.Primitives.Add(p); m.Meshes.Add(hm); n.Mesh = m.Meshes.Count - 1;
            }
            m.Nodes.Add(n); if (parent >= 0) m.Nodes[parent].Children.Add(m.Nodes.Count - 1); return m.Nodes.Count - 1;
        }
        int hull = Node("Hull", -1, true, null);
        int gun = Node("Gun", hull, true, new double[] { 1, 0.5, 0 });
        anim.Samplers.Add(new HafSampler { Times = new[] { 0f, 1f }, Values = new[] { 1f, 0.5f, 0f, 2.5f, 0.5f, 0f }, Components = 3, Interpolation = "LINEAR" });
        anim.Channels.Add(new HafChannel { Node = gun, Path = "translation", Sampler = 0 });
        var sc = new HafScene(); sc.Nodes.Add(hull); m.Scenes.Add(sc); m.Scene = 0; m.Animations.Add(anim);
        return m;
    }

    [Fact]
    public void A_small_rig_is_converted_in_process_and_the_file_reads_back_with_its_roles()
    {
        string src = Path.Combine(dir, "tank.glb"), dst = Path.Combine(dir, "deploy_converted.glb");
        GlbWriter.Write(Tank(), src);
        int ticks = 0;
        var o = BlenderDeployInProcess.Run(src, dst, BlenderDeployInProcess.Argv("0", "24", "", "", "", "", "", "", "", "", "", "4", "", "", "", "", "", "", ""), () => ticks++);
        Assert.Null(o.UseBlender);
        Assert.True(o.Written); Assert.False(o.ScriptExit);
        Assert.True(ticks >= 4);
        Assert.Equal("DEPLOY wrote: " + dst, o.Log.Last());
        Assert.All(o.Log, l => Assert.StartsWith("DEPLOY", l));
        Assert.Contains(o.Log, l => l.StartsWith("DEPLOY frame range:"));
        Assert.Contains(o.Log, l => l.StartsWith("DEPLOY role clips:"));
        Assert.False(File.Exists(dst + ".tmp"));
        var back = GlbReader.Read(dst);
        Assert.Equal(new[] { "deploy", "deployed", "fold", "folded", "unfold" }, back.Animations.Select(a => a.Name).OrderBy(s => s, StringComparer.Ordinal));
        Assert.Single(back.Skins);
    }

    [Fact]
    public void An_unreadable_or_missing_source_is_left_to_Blender_not_failed()
    {
        var gone = BlenderDeployInProcess.Run(Path.Combine(dir, "nope.glb"), Path.Combine(dir, "out.glb"), BlenderDeployInProcess.Argv("0", "24", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", ""));
        Assert.False(gone.Written); Assert.False(gone.ScriptExit); Assert.Equal("the model file is not there", gone.UseBlender);
        string fbx = Path.Combine(dir, "model.fbx"); File.WriteAllBytes(fbx, new byte[] { 1, 2, 3 });
        var other = BlenderDeployInProcess.Run(fbx, Path.Combine(dir, "out.glb"), BlenderDeployInProcess.Argv("0", "24", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", ""));
        Assert.Equal("the source is not a .glb/.gltf file", other.UseBlender);
        string junk = Path.Combine(dir, "junk.glb"); File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4 });
        var bad = BlenderDeployInProcess.Run(junk, Path.Combine(dir, "out.glb"), BlenderDeployInProcess.Argv("0", "24", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", ""));
        Assert.False(bad.Written); Assert.NotNull(bad.UseBlender);
        Assert.False(File.Exists(Path.Combine(dir, "out.glb")));
    }

    [Fact]
    public void The_scripts_own_exit_is_reported_as_such_with_its_log_and_no_file()
    {
        // a model without an animated part: the script's no-parts guard exits (SystemExit), nothing is written
        var m = new HafModel();
        var hm = new HafMesh { Name = "hull_mesh" }; hm.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[] { -1, 0, 0, 1, 0, 0, 0, 1, 0.5f }, Indices = new[] { 0, 1, 2 } }); m.Meshes.Add(hm);
        m.Nodes.Add(new HafNode { Name = "Hull", Mesh = 0 });
        var sc = new HafScene(); sc.Nodes.Add(0); m.Scenes.Add(sc); m.Scene = 0;
        string src = Path.Combine(dir, "still.glb"), dst = Path.Combine(dir, "out.glb");
        GlbWriter.Write(m, src);
        var o = BlenderDeployInProcess.Run(src, dst, BlenderDeployInProcess.Argv("0", "24", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", ""));
        Assert.False(o.Written);
        Assert.True(o.ScriptExit || o.UseBlender != null);   // the guard, or a shape the port leaves: never a written file
        Assert.False(File.Exists(dst));
    }
}
