using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>The source-skeleton fast path in C# (rigfast, 2026-11-16). Its exports are held to Blender by the vehicle_rig drill
/// (fx_fast_wall_126 / _127 / fx_fast_two_x: every VEHICLE line and the file's bytes); these hold the refusals - each the
/// script's own line and exit, in the script's order - on a model that needs no skeleton to reach them.</summary>
public class VehicleRigFastPathTests
{
    const string Dash = "—";

    [Fact]
    public void A_source_without_an_armature_is_the_scripts_exit()
    {
        var s = Fast(Args());
        Assert.Null(s.Fallback); Assert.True(s.Exits); Assert.Null(s.FastExport);
        Assert.Equal("VEHICLE ERROR: rigfast requested but the source has no armature", s.Logs.Last());
    }

    public static IEnumerable<object[]> Refusals() => new[]
    {
        new object[] { "oars", "VEHICLE ERROR: Oar recovery needs mesh parts; disable the source-skeleton fast path and probe the merged oar meshes" },
        new object[] { "rotors", "VEHICLE ERROR: Rotor / Tail rotor roles are ignored by the source-skeleton fast path " + Dash + " mark the spinning source BONE as Wheel (fast-path bones spin about their own axis), or disable the fast path to rig the mesh parts" },
        new object[] { "tailrotors", "VEHICLE ERROR: Rotor / Tail rotor roles are ignored by the source-skeleton fast path " + Dash + " mark the spinning source BONE as Wheel (fast-path bones spin about their own axis), or disable the fast path to rig the mesh parts" },
        new object[] { "flip", "VEHICLE ERROR: Flip (winding surgery) is not supported on the source-skeleton fast path " + Dash + " rigfast exports the source geometry as-is; disable the fast path to flip mesh parts" },
        new object[] { "twosided", "VEHICLE ERROR: Two-sided needs mesh parts " + Dash + " disable the source-skeleton fast path to repair the marked faces" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void An_option_that_needs_mesh_parts_is_refused_with_the_scripts_line(string role, string line)
    {
        var a = Args(); a.Names[role] = new List<string> { "Hull" };
        var s = Fast(a);
        Assert.Null(s.Fallback); Assert.True(s.Exits); Assert.Null(s.FastExport);
        Assert.Equal(line, s.Logs.Last());
    }

    [Fact]
    public void The_refusals_come_in_the_scripts_order_and_a_placement_only_warns()
    {
        // oars before rotors before the wave rock before Flip before Two-sided before the sail fold; the placement's WARN first
        var a = Args(); a.PartTx = new List<string> { "Hull|0,0,0|0,0,0|1,1,1" };
        a.Names["flip"] = new List<string> { "Hull" }; a.Names["twosided"] = new List<string> { "Hull" }; a.SailFold = true; a.RockOn = true;
        var s = Fast(a);
        Assert.True(s.Exits);
        Assert.Equal(new[]
        {
            "VEHICLE WARN: per-part placement is a mesh-path feature " + Dash + " the source-skeleton fast path spins bones and leaves the meshes as authored; disable the fast path to apply it",
            "VEHICLE ERROR: Wave rock is not supported on the source-skeleton fast path " + Dash + " disable the fast path to rig the mesh with a rocking hull",
        }, s.Logs.Skip(s.Logs.Count - 2));
        a.RockOn = false; a.Names["flip"].Clear(); a.Names["twosided"].Clear();
        Assert.Equal("VEHICLE ERROR: Fold sail at idle needs the generated Sail fold chain (new bones + band skinning) " + Dash + " the source-skeleton fast path keeps the artist skeleton; disable the fast path or the fold option", Fast(a).Logs.Last());
    }

    [Fact]
    public void A_second_model_is_refused_before_the_fast_path_looks_at_anything_else()
    {
        var a = Args(); a.Merge2 = "second.glb|0,0,0|0,0,0|1"; a.Names["oars"] = new List<string> { "Hull" };
        var s = Fast(a);
        Assert.True(s.Exits);
        Assert.Equal("VEHICLE ERROR: a second model is not supported on the source-skeleton fast path " + Dash + " the merged pair needs the mesh path", s.Logs.Last());
    }

    [Theory]
    [InlineData("missing")] [InlineData("directory")] [InlineData("locked")] [InlineData("empty")]
    public void Ignored_placement_files_are_not_opened_before_fast_path_refusals(string kind)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rigfast-placement-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir); System.IO.FileStream held = null;
        try
        {
            string path = System.IO.Path.Combine(dir, "placements.txt");
            if (kind == "directory") path = dir;
            else if (kind == "empty") path = "";
            else if (kind == "locked")
            {
                System.IO.File.WriteAllText(path, "Hull|1,2,3|2,2,2");
                held = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None);
            }
            var a = Args("parttx=@" + path); a.Names["oars"].Add("Hull");
            var s = Fast(a);
            Assert.True(s.Exits); Assert.Null(s.Fallback); Assert.Null(s.FastExport);
            Assert.Contains(s.Logs, l => l.StartsWith("VEHICLE WARN: per-part placement is a mesh-path feature", StringComparison.Ordinal));
            Assert.Equal("VEHICLE ERROR: Oar recovery needs mesh parts; disable the source-skeleton fast path and probe the merged oar meshes", s.Logs.Last());
        }
        finally { held?.Dispose(); System.IO.Directory.Delete(dir, true); }
    }

    [Fact]
    public void Placement_files_are_still_read_for_the_mesh_path()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rig-placement-" + Guid.NewGuid().ToString("N") + ".txt");
        const string line = "Hull|1,2,3|2,2,2";
        try
        {
            System.IO.File.WriteAllText(path, line);
            var argv = Enumerable.Repeat("", 76).ToList(); argv[0] = "rig"; argv[1] = "scene.glb";
            argv[7] = "15"; argv[8] = "-360"; argv[17] = "0"; argv[21] = "0"; argv.Add("parttx=@" + path);
            Assert.Equal(new[] { line }, BlenderVehicleRig.ParseArgs(argv).PartTx);
        }
        finally { System.IO.File.Delete(path); }
    }

    static BlenderRigAnim.Stage1 Fast(BlenderVehicleRig.Args a)
    {
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene());
        var points = new List<float>(); foreach (int dx in new[] { -1, 1 }) foreach (int dy in new[] { -1, 1 }) foreach (int dz in new[] { -1, 1 }) points.AddRange(new[] { dx * .5f, dy * .5f, dz * .5f });
        var mesh = new HafMesh { Name = "Hull" }; mesh.Primitives.Add(new HafPrimitive { Positions = points.ToArray(), Indices = new[] { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 } });
        m.Nodes.Add(new HafNode { Name = "Hull", Mesh = 0 }); m.Meshes.Add(mesh); m.Scenes[0].Nodes.Add(0);
        var s = BlenderVehicleRig.RunStage1(m, a);
        Assert.Null(s.Dies); return s;
    }

    static BlenderVehicleRig.Args Args(params string[] tags)
    {
        var argv = Enumerable.Repeat("", 76).ToList(); argv[0] = "rigfast"; argv[1] = "scene.glb"; argv[2] = "out.glb"; argv[7] = "15"; argv[8] = "-360"; argv[17] = "0"; argv[21] = "0";
        argv.AddRange(tags);
        var a = BlenderVehicleRig.ParseArgs(argv); Assert.Equal("rigfast", a.Mode); return a;
    }
}
