using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>The Vehicle Lab's rig command line (VehicleRigArgLine): the token order is vehicle_rig.py's argv table, the quoting
/// and number formats are the window's, the tagged arguments come and go on their own terms, and a recipe's JSON is read
/// as the window loads it (absent keys keep the initializers; the load guards).</summary>
public class VehicleRigArgsTests
{
    static VehicleRigArgs Sample()
    {
        var a = new VehicleRigArgs { srcFile = "D:/m/ship.glb", outGlb = "D:/m/ship_Spin.glb", previewFbx = "C:/p/ship_preview.fbx", frames = 15, degrees = -360f };
        foreach (var role in VehicleRigArgLine.NamesRoles) a.namesFiles[role] = "C:/p/ship_" + role + ".txt";
        return a;
    }

    [Fact]
    public void The_positional_block_is_the_scripts_argv_table()
    {
        var t = VehicleRigArgLine.RigArgv(Sample()).Select(x => x.value).ToList();
        Assert.Equal("rig", t[0]); Assert.Equal("D:/m/ship.glb", t[1]); Assert.Equal("D:/m/ship_Spin.glb", t[2]); Assert.Equal("C:/p/ship_preview.fbx", t[3]);
        Assert.Equal("@C:/p/ship_wheels.txt", t[4]); Assert.Equal("@C:/p/ship_turrets.txt", t[5]); Assert.Equal("AUTO", t[6]); Assert.Equal("15", t[7]); Assert.Equal("-360", t[8]);
        Assert.Equal("@C:/p/ship_ignore.txt", t[9]); Assert.Equal("@C:/p/ship_tracks.txt", t[10]); Assert.Equal("@C:/p/ship_guns.txt", t[11]);
        Assert.Equal("3", t[12]); Assert.Equal("1", t[13]); Assert.Equal("1", t[14]); Assert.Equal("4", t[15]); Assert.Equal("0", t[16]);
        Assert.Equal("0", t[17]); Assert.Equal("120", t[18]); Assert.Equal("AUTO", t[19]); Assert.Equal("0", t[20]); Assert.Equal("0", t[21]); Assert.Equal("1", t[22]);
        Assert.Equal("0,0,0", t[23]); Assert.Equal("90", t[24]); Assert.Equal("1", t[25]);
        Assert.Equal("@C:/p/ship_rotors.txt", t[26]); Assert.Equal("@C:/p/ship_tailrotors.txt", t[27]); Assert.Equal("AUTO", t[28]); Assert.Equal("0", t[29]); Assert.Equal("0", t[30]);
        Assert.Equal("@C:/p/ship_trails.txt", t[31]); Assert.Equal("35", t[32]); Assert.Equal("12", t[33]); Assert.Equal("0.5", t[34]); Assert.Equal("0", t[35]);
        Assert.Equal("@C:/p/ship_muzzles.txt", t[36]); Assert.Equal("@C:/p/ship_cradles.txt", t[37]); Assert.Equal("0", t[38]); Assert.Equal("16", t[39]); Assert.Equal("0", t[40]); Assert.Equal("0", t[41]);
        Assert.Equal("@C:/p/ship_oars.txt", t[42]); Assert.Equal("24", t[43]); Assert.Equal("18", t[44]); Assert.Equal("24", t[45]); Assert.Equal("0", t[46]); Assert.Equal("0", t[47]);
        Assert.Equal("@C:/p/ship_sails.txt", t[48]); Assert.Equal("@C:/p/ship_rigging.txt", t[49]); Assert.Equal("75", t[50]); Assert.Equal("@C:/p/ship_structure.txt", t[51]); Assert.Equal("50", t[52]);
        Assert.Equal("@C:/p/ship_bodies.txt", t[53]); Assert.Equal("0", t[54]); Assert.Equal("@C:/p/ship_flags.txt", t[55]); Assert.Equal("0", t[56]); Assert.Equal("0", t[57]); Assert.Equal("30", t[58]); Assert.Equal("100", t[59]);
        Assert.Equal("@C:/p/ship_rudders.txt", t[60]); Assert.Equal("0", t[61]); Assert.Equal("0", t[62]); Assert.Equal("@C:/p/ship_preserve.txt", t[63]); Assert.Equal("0", t[64]); Assert.Equal("0", t[65]);
        Assert.Equal("@C:/p/ship_flip.txt", t[66]); Assert.Equal("0", t[67]); Assert.Equal("0", t[68]); Assert.Equal("@C:/p/ship_detail.txt", t[69]); Assert.Equal("0", t[70]);
        Assert.Equal("0", t[71]); Assert.Equal("12", t[72]); Assert.Equal("270", t[73]); Assert.Equal("0", t[74]); Assert.Equal("0", t[75]);
        // the tagged arguments an untouched recipe sends: shroudreduce and windowreduce only (sent at every dial setting)
        Assert.Equal(new[] { "shroudreduce=@C:/p/ship_shroud.txt|0", "windowreduce=@C:/p/ship_window.txt|0" }, t.Skip(76).ToArray());
    }

    [Fact]
    public void The_line_quotes_paths_names_files_the_rotation_and_tags_and_nothing_else()
    {
        string line = VehicleRigArgLine.RigArgLine(Sample());
        Assert.StartsWith("rig \"D:/m/ship.glb\" \"D:/m/ship_Spin.glb\" \"C:/p/ship_preview.fbx\" \"@C:/p/ship_wheels.txt\" \"@C:/p/ship_turrets.txt\" AUTO 15 -360 ", line);
        Assert.Contains(" 1 \"0,0,0\" 90 1 ", line);
        Assert.EndsWith(" 0 12 270 0 0 \"shroudreduce=@C:/p/ship_shroud.txt|0\" \"windowreduce=@C:/p/ship_window.txt|0\"", line);
    }

    [Fact]
    public void Switches_and_formats_follow_the_window()
    {
        var a = Sample();
        a.spinEnabled = false; a.degrees = -720f; a.tracksStatic = false; a.waveEnabled = true; a.rockDegrees = 3.456f; a.rockPitchDeg = 1.2345f; a.rockAxisChoice = 2;
        a.modelRotX = 90f; a.modelRotY = -12.345f; a.modelRotZ = 0.004f; a.axisChoice = 3; a.tailAxisChoice = 1; a.gunPivot = 0.3333f; a.recoilDist = 0.12345f;
        a.treadCellsPerLink = 0.333f; a.sailFoldFrames = 0; a.flagFoldOn = true; a.flagFoldDeg = 12.34f; a.flagFoldFrames = 0; a.gunReducePct = 42.26f; a.defaultReducePct = 75f;
        a.doubleSided = true; a.fixInsideOut = true; a.sailFoldIdle = true; a.sailFoldReverse = true; a.sailFoldSag = 0.456f;
        var t = VehicleRigArgLine.RigArgv(a).Select(x => x.value).ToList();
        Assert.Equal("Z", t[6]); Assert.Equal("0", t[8]);                       // spin off: degrees 0
        Assert.Equal("0.33", t[15]); Assert.Equal("1", t[16]);                   // tracks static when spin is off
        Assert.Equal("3.46", t[17]); Assert.Equal("Y", t[19]); Assert.Equal("1.23", t[21]);
        Assert.Equal("90,-12.35,0", t[23]); Assert.Equal("X", t[28]); Assert.Equal("0.333", t[34]); Assert.Equal("0.123", t[38]);
        Assert.Equal("1", t[41]); Assert.Equal("1", t[46]); Assert.Equal("1", t[71]); Assert.Equal("1", t[72]); Assert.Equal("1", t[74]); Assert.Equal("0.46", t[75]);
        Assert.Equal("flagfold=12.3|1", t[76]); Assert.Equal("defaultreduce=@C:/p/ship_default.txt|75", t[77]);
        Assert.Equal("shroudreduce=@C:/p/ship_shroud.txt|0", t[78]); Assert.Equal("gunreduce=42.3", t[79]); Assert.Equal("windowreduce=@C:/p/ship_window.txt|0", t[80]);
        Assert.Equal(81, t.Count);
    }

    [Fact]
    public void The_second_model_brightness_and_placement_tags_appear_in_the_windows_order()
    {
        var a = Sample();
        a.srcFile2 = " D:\\m\\gun.glb "; a.model2OffX = 1.5f; a.model2RotZ = 30f; a.model2ScaleX = 2f; a.model2ScaleY = 0.5f; a.model2ScaleZ = 1f;
        a.model1Bright = 1.2f; a.model2Bright = 0.8f; a.partTxFile = "C:/p/ship_parttx.txt";
        var t = VehicleRigArgLine.RigArgv(a).Select(x => x.value).ToList();
        Assert.Equal("merge2=D:/m/gun.glb|1.5,0,0|0,0,30|" + VehicleLabRules.Merge2ScaleField(2f, 0.5f, 1f), t[76]);
        Assert.Equal("bright=1.2|0.8", t[77]);
        Assert.Equal("shroudreduce=@C:/p/ship_shroud.txt|0", t[78]); Assert.Equal("windowreduce=@C:/p/ship_window.txt|0", t[79]); Assert.Equal("parttx=@C:/p/ship_parttx.txt", t[80]);
        // brightness within 0.005 of 1 is "untouched"; a second-model brightness without a second model is not sent
        var b = Sample(); b.model1Bright = 1.004f; b.model2Bright = 0.5f;
        Assert.DoesNotContain(VehicleRigArgLine.RigArgv(b), x => x.value.StartsWith("bright="));
        var c = Sample(); c.model1Bright = 1.2f; c.model2Bright = 0.5f;
        Assert.Equal("bright=1.2|1", VehicleRigArgLine.RigArgv(c).Select(x => x.value).First(v => v.StartsWith("bright=")));
    }

    [Fact]
    public void A_bare_recipe_reads_as_the_windows_defaults()
    {
        var r = VehicleRigArgLine.RigArgsFromRecipe("{\"srcFile\":\"D:/m/a.glb\",\"outGlb\":\"D:/m/a_Spin.glb\",\"frames\":15,\"axisChoice\":0,\"minVerts\":1,\"degrees\":-360.0,\"parts\":[{\"name\":\"P\",\"verts\":8,\"role\":1}]}");
        var a = r.args;
        Assert.False(a.fast); Assert.Equal(3, a.treadAdvCells); Assert.Equal(4f, a.treadCellsPerLink); Assert.True(a.spinEnabled); Assert.Equal(120, a.rockFrames);
        Assert.Equal(75f, a.riggingReducePct); Assert.Equal(50f, a.structureReducePct); Assert.Equal(30f, a.oarPivotPct); Assert.Equal(100f, a.oarLengthPct);
        Assert.Equal(12, a.sailFoldFrames); Assert.Equal(270f, a.sailFoldAngleDeg); Assert.Equal(0.5f, a.gunPivot); Assert.Equal(16, a.recoilFrames); Assert.Equal(90f, a.rockPitchPhase);
        Assert.Equal(1f, a.model2ScaleX); Assert.Equal(1f, a.model1Bright);
        Assert.Equal(new[] { "P" }, VehicleRigArgLine.NamesOf(r, "wheels")); Assert.Empty(VehicleRigArgLine.NamesOf(r, "bodies")); Assert.Empty(VehicleRigArgLine.PlacementLines(r));
    }

    [Fact]
    public void The_load_guards_and_the_fast_path_follow_the_window()
    {
        string json = "{\"srcFile\":\"a.glb\",\"outGlb\":\"a_Spin.glb\",\"frames\":10,\"degrees\":90.0,\"treadAdvCells\":0,\"treadCellsPerLink\":0,\"sailFoldFrames\":0,\"sailFoldAngleDeg\":0,\"sailFoldSag\":7,\"flagFoldDeg\":-500,\"flagFoldFrames\":-1," +
                      "\"model2Scale\":2.0,\"model1Bright\":0,\"rockFrames\":0,\"rockRollCycles\":0,\"rockPitchCycles\":0,\"useSourceRig\":true,\"boneParts\":[{\"name\":\"b1\",\"role\":1}]," +
                      "\"parts\":[{\"name\":\"P\",\"role\":0,\"offset\":{\"x\":0.5,\"y\":0,\"z\":0},\"scale\":{\"x\":1,\"y\":1,\"z\":1}},{\"name\":\"Q\",\"role\":0}],\"srcFile2\":\" D:\\\\x\\\\b.glb \"}";
        var r = VehicleRigArgLine.RigArgsFromRecipe(json); var a = r.args;
        Assert.Equal(3, a.treadAdvCells); Assert.Equal(4f, a.treadCellsPerLink); Assert.Equal(12, a.sailFoldFrames); Assert.Equal(270f, a.sailFoldAngleDeg); Assert.Equal(1f, a.sailFoldSag);
        Assert.Equal(-175f, a.flagFoldDeg); Assert.Equal(12, a.flagFoldFrames); Assert.Equal(2f, a.model2ScaleX); Assert.Equal(2f, a.model2ScaleZ); Assert.Equal(1f, a.model1Bright);
        Assert.Equal(120, a.rockFrames); Assert.Equal(1, a.rockRollCycles); Assert.Equal(1, a.rockPitchCycles); Assert.Equal("D:\\x\\b.glb", a.srcFile2);
        Assert.True(r.FastPath); Assert.True(a.fast); Assert.Equal("rigfast", VehicleRigArgLine.RigArgv(Fill(a)).First().value);
        Assert.Equal(new[] { "b1" }, VehicleRigArgLine.NamesOf(r, "wheels"));   // the fast path's active list is the bone parts
        Assert.Equal(new[] { VehicleLabRules.PartPlacementLine("P", 0.5f, 0, 0, 1, 1, 1) }, VehicleRigArgLine.PlacementLines(r));
    }

    static VehicleRigArgs Fill(VehicleRigArgs a)
    {
        foreach (var role in VehicleRigArgLine.NamesRoles) a.namesFiles[role] = "n_" + role;
        return a;
    }

    [Fact]
    public void A_missing_names_file_is_an_error_not_a_blank_argument()
    {
        var a = Sample(); a.namesFiles.Remove("cradles");
        Assert.Throws<ArgumentException>(() => VehicleRigArgLine.RigArgv(a));
    }
    [Theory]
    [InlineData("{}")][InlineData("{\"parts\":null}")][InlineData("{\"parts\":{}}")]
    [InlineData("{\"parts\":[null]}")][InlineData("{\"parts\":[42]}")]
    public void Invalid_part_lists_are_not_silently_changed_to_empty_recipes(string json)
        => Assert.Throws<System.IO.InvalidDataException>(() => VehicleRigArgLine.RigArgsFromRecipe(json));

}
