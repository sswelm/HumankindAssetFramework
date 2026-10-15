using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

/// <summary>vehicle_rig.py's stage 1 in C# (BlenderVehicleRig): the argument reading, the bone-shape signature and the split's
/// radial order - the rules the drill holds against Blender, pinned here on the shapes a unit test can hold.</summary>
public class VehicleRigStage1Tests
{
    static List<string> Argv(Dictionary<int, string> overrides = null)
    {
        var argv = Enumerable.Repeat("", 76).ToList();
        argv[0] = "rig"; argv[1] = "D:/m/a.glb"; argv[2] = "D:/m/a_Spin.glb"; argv[3] = ""; argv[4] = ""; argv[5] = ""; argv[6] = "AUTO"; argv[7] = "15"; argv[8] = "-360";
        if (overrides != null) foreach (var kv in overrides) argv[kv.Key] = kv.Value;
        return argv;
    }

    [Fact]
    public void Names_come_from_the_files_and_the_tracks_static_line_is_printed_at_argument_time()
    {
        string f = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(f, new[] { "Tread_L", "  Tread_R  ", "" });
            var a = BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [10] = "@" + f, [16] = "1" }));
            Assert.Equal(new[] { "Tread_L", "Tread_R" }, a.Names["tracks"]);
            Assert.True(a.TracksStatic);
            Assert.Equal(new[] { "VEHICLE tracks STATIC (isolation): 2 tread part(s) rigged rigid to Root, no link bones" }, a.Logs);
            Assert.DoesNotContain("Tread_L", a.Marked);   // static: track_names is emptied before _role_marked is built
            var b = BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [10] = "@" + f, [16] = "0" }));
            Assert.Empty(b.Logs);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void A_bad_orientation_is_a_warning_and_a_good_one_is_read_padded_or_cut_to_three()
    {
        var a = BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [23] = "30,x,0" }));
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, a.ModelRot);
        Assert.Equal(new[] { "VEHICLE WARN: bad orientation arg '30,x,0' — ignoring" }, a.Logs);
        var b = BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [23] = "30,10,-20" }));
        Assert.Equal(new[] { 30.0, 10.0, -20.0 }, b.ModelRot); Assert.Empty(b.Logs);
        // [float(v) for v in split(",")] + [0, 0, 0], cut to three: two values are read, a fourth is dropped, a blank arg is nothing
        Assert.Equal(new[] { 30.0, 10.0, 0.0 }, BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [23] = "30,10" })).ModelRot);
        Assert.Equal(new[] { 30.0, 10.0, -20.0 }, BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [23] = "30,10,-20,5" })).ModelRot);
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [23] = "  " })).ModelRot);
        Assert.Equal(new[] { "VEHICLE WARN: bad orientation arg '30,10,-20,q' — ignoring" }, BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [23] = "30,10,-20,q" })).Logs);
    }

    [Fact]
    public void Static_tracks_leave_the_marked_set_as_the_script_empties_track_names_first()
    {
        string f = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(f, new[] { "Tread_L" });
            Assert.DoesNotContain("Tread_L", BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [10] = "@" + f, [16] = "1" })).Marked);
            Assert.Contains("Tread_L", BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [10] = "@" + f, [16] = "0" })).Marked);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void A_malformed_flagfold_is_the_scripts_exit_before_the_import_and_an_infinite_frame_count_its_death()
    {
        var argv = Argv(); argv.Add("flagfold=a|b");
        var a = BlenderVehicleRig.ParseArgs(argv);
        Assert.Equal("malformed flagfold argument", a.Problem); Assert.Equal(new[] { "VEHICLE ERROR: malformed flagfold argument: flagfold=a|b" }, a.Logs);
        argv = Argv(); argv.Add("flagfold=|");
        Assert.Null(BlenderVehicleRig.ParseArgs(argv).Problem);
        argv = Argv(); argv.Add("flagfold=10|inf");
        var d = BlenderVehicleRig.ParseArgs(argv);
        Assert.Null(d.Problem); Assert.StartsWith("OverflowError", d.PreDie);
        Assert.Equal(d.PreDie, BlenderVehicleRig.RunStage1(null, d).Dies);
    }

    [Theory]
    [InlineData("defaultreduce=nopipe", "VEHICLE ERROR: malformed defaultreduce argument: defaultreduce=nopipe (not enough values to unpack (expected 2, got 1))")]
    [InlineData("shroudreduce=|abc", "VEHICLE ERROR: malformed shroudreduce argument: shroudreduce=|abc (could not convert string to float: 'abc')")]
    [InlineData("gunreduce=abc", "VEHICLE ERROR: malformed gunreduce argument: could not convert string to float: 'abc'")]
    [InlineData("gunreduce=inf", "VEHICLE ERROR: malformed gunreduce argument: not a finite percent: 'gunreduce=inf'")]
    [InlineData("windowreduce=nopipe", "VEHICLE ERROR: malformed windowreduce argument: no '|' between the names and the percent: 'windowreduce=nopipe'")]
    [InlineData("windowreduce=|nan", "VEHICLE ERROR: malformed windowreduce argument: not a finite percent: 'windowreduce=|nan'")]
    public void A_malformed_reduce_tag_is_the_scripts_error_line_after_the_merge(string token, string line)
    {
        var argv = Argv(); argv.Add(token);
        var a = BlenderVehicleRig.ParseArgs(argv);
        Assert.Null(a.Problem); Assert.Equal(line, a.TagError);
        argv = Argv(); argv.Add("shroudreduce=|0.0"); argv.Add("windowreduce=|12.5"); argv.Add("gunreduce=30");
        Assert.Null(BlenderVehicleRig.ParseArgs(argv).TagError);
    }

    [Theory]
    [InlineData(7, "15.0")]     // int("15.0")
    [InlineData(7, "")]         // argv[7] missing
    [InlineData(8, "x")]        // float("x")
    [InlineData(13, "fast")]
    [InlineData(12, "inf")]     // int(float("inf"))
    [InlineData(22, "nan")]     // int(float("nan"))
    public void A_dial_python_cannot_parse_is_the_scripts_death_before_the_orientation(int index, string value)
    {
        var a = BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [index] = value }));
        Assert.NotNull(a.DialsDie); Assert.Null(a.Problem);
        Assert.Null(BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [12] = " 3 ", [13] = "1.5", [18] = "24.0", [22] = "2" })).DialsDie);
        Assert.True(BlenderVehicleRig.TryPyInt(" -1_000 ")); Assert.False(BlenderVehicleRig.TryPyInt("1__0")); Assert.False(BlenderVehicleRig.TryPyInt("0x10"));
    }

    [Fact]
    public void A_names_file_that_is_not_there_is_the_scripts_death_after_the_merge()
    {
        var a = BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [4] = "@C:/no/such/wheels.txt" }));
        Assert.Equal("FileNotFoundError: [Errno 2] No such file or directory: 'C:/no/such/wheels.txt'", a.NamesDie);
        Assert.Empty(a.Names["wheels"]);
    }

    [Fact]
    public void A_malformed_bright_argument_is_the_scripts_exit()
    {
        var argv = Argv(); argv.Add("bright=1.2|x");
        var a = BlenderVehicleRig.ParseArgs(argv);
        Assert.NotNull(a.Problem);
        Assert.Equal(new[] { "VEHICLE ERROR: malformed bright argument: bright=1.2|x" }, a.Logs);
        var s = BlenderVehicleRig.RunStage1(null, a);
        Assert.True(s.Exits); Assert.Equal(a.Logs, s.Logs);
    }

    [Fact]
    public void A_source_the_csharp_does_not_read_is_named_before_any_read()
    {
        var a = BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [1] = "D:/m/a.fbx" }));
        Assert.StartsWith("a .fbx source", BlenderVehicleRig.SourceFallback(a));
        Assert.Null(BlenderVehicleRig.SourceFallback(BlenderVehicleRig.ParseArgs(Argv())));
        var s = BlenderVehicleRig.RunStage1(null, a);
        Assert.StartsWith("a .fbx source", s.Fallback);
        var fast = BlenderVehicleRig.RunStage1(null, BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [0] = "rigfast" })));
        Assert.StartsWith("the source-skeleton fast path", fast.Fallback);
    }

    static BlenderRigAnim.MeshData Mesh(int vertices, float[] extents)
    {
        var d = new BlenderRigAnim.MeshData { Vertices = vertices, Positions = new float[3 * vertices], Faces = new int[0], FaceMaterial = new int[0] };
        for (int v = 0; v < vertices; v++) for (int k = 0; k < 3; k++) d.Positions[3 * v + k] = (v % 2 == 0 ? 0f : extents[k]);
        return d;
    }

    [Theory]
    [InlineData("Icosphere", 42, 1f, 1f, 0.85f, true)]
    [InlineData("B_Icosphere.003", 12, 1f, 1f, 1f, true)]
    [InlineData("Icosphere", 42, 1f, 1f, 0.3f, false)]   // a squashed disc: geometry, whatever its name
    [InlineData("Icosphere", 40, 1f, 1f, 1f, false)]     // not an icosphere's vertex count
    [InlineData("Ball", 42, 1f, 1f, 1f, false)]          // not the importer's name
    public void The_bone_shape_signature_is_name_count_and_extents(string name, int vertices, float ex, float ey, float ez, bool expected)
        => Assert.Equal(expected, BlenderVehicleRig.IsIcosphereArtifact(name, Mesh(vertices, new[] { ex, ey, ez })));

    [Fact]
    public void The_radial_cycle_gives_the_last_face_first_then_the_rest_in_creation_order()
    {
        Assert.Equal(new[] { 7 }, BlenderVehicleRig.RadialOrder(new List<int> { 7 }));
        Assert.Equal(new[] { 2, 1 }, BlenderVehicleRig.RadialOrder(new List<int> { 1, 2 }));
        Assert.Equal(new[] { 4, 1, 2, 3 }, BlenderVehicleRig.RadialOrder(new List<int> { 1, 2, 3, 4 }));
    }
}
