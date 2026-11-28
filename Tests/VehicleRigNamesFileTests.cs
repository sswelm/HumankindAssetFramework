using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Xunit;

public class VehicleRigNamesFileTests
{
    static List<string> Argv(params string[] tags)
    {
        var argv = Enumerable.Repeat("", 76).ToList();
        argv[0] = "rig"; argv[1] = "model.glb"; argv[7] = "15"; argv[8] = "0";
        argv.AddRange(tags); return argv;
    }

    [Theory]
    [InlineData("defaultreduce")]
    [InlineData("shroudreduce")]
    [InlineData("windowreduce")]
    [InlineData("railingreduce")]
    [InlineData("chainreduce")]
    [InlineData("deckreduce")]
    public void A_tagged_names_directory_is_the_scripts_error_instead_of_an_unhandled_exception(string tag)
    {
        string path = Path.Combine(Path.GetTempPath(), "haf-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var args = BlenderVehicleRig.ParseArgs(Argv(tag + "=@" + path + "|20"));
            Assert.Null(args.Unsupported);
            string detail = "[Errno 13] Permission denied: '" + path.Replace("\\", "\\\\") + "'";
            Assert.Equal("VEHICLE ERROR: malformed " + tag + " argument: " +
                (tag == "windowreduce" || tag == "railingreduce" || tag == "chainreduce" || tag == "deckreduce" ? detail : tag + "=@" + path + "|20 (" + detail + ")"), args.TagError);
        }
        finally { Directory.Delete(path); }
    }

    [Theory]
    [InlineData("defaultreduce")]
    [InlineData("shroudreduce")]
    [InlineData("windowreduce")]
    [InlineData("railingreduce")]
    [InlineData("chainreduce")]
    [InlineData("deckreduce")]
    public void A_tagged_non_UTF8_names_file_is_left_to_Blender_and_earlier_errors_still_win(string tag)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[] { 0xff });
            string token = tag + "=@" + path + "|20";
            var args = BlenderVehicleRig.ParseArgs(Argv(token));
            Assert.Contains("not UTF-8", args.Unsupported); Assert.Null(args.TagError);

            var argv = Argv(token); argv[34] = "bad pivot";
            args = BlenderVehicleRig.ParseArgs(argv);
            Assert.Null(args.Unsupported); Assert.Contains("bad pivot", args.NamesDie);

            argv = Argv("defaultreduce=nopipe", token);
            args = BlenderVehicleRig.ParseArgs(argv);
            Assert.Null(args.Unsupported); Assert.Contains("not enough values to unpack", args.TagError);
        }
        finally { File.Delete(path); }
    }

    // 2026-11-16: the Two-sided role is modelled - the tag's names file is read like any role's (2026-11-14 to 11-16 a job
    // carrying the tag was left to Blender by name). A file that cannot be read is still left: the script dies of Python's
    // own error there. An earlier death or malformed tag is the script's own stop, and a blank tag marks nothing.
    [Fact]
    public void A_twosided_tag_names_the_parts_and_marks_them()
    {
        string path = Path.Combine(Path.GetTempPath(), "haf_twosided_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "Tower\r\n  Sheet  \n\n");
        BlenderVehicleRig.Args args;
        try { args = BlenderVehicleRig.ParseArgs(Argv("twosided=@" + path)); }
        finally { File.Delete(path); }
        Assert.Null(args.Unsupported); Assert.Null(args.TagError); Assert.Null(args.NamesDie);
        Assert.Equal(new[] { "Tower", "Sheet" }, args.Names["twosided"]);
        Assert.Contains("Tower", args.Marked); Assert.Contains("Sheet", args.Marked);   // _role_marked: never purged as an artifact

        args = BlenderVehicleRig.ParseArgs(Argv("twosided=Tower;Sheet"));   // namelist's other form
        Assert.Equal(new[] { "Tower", "Sheet" }, args.Names["twosided"]);

        args = BlenderVehicleRig.ParseArgs(Argv("twosided=@C:/p/no_such_folder/x_twosided.txt"));
        Assert.Equal(BlenderVehicleRig.TwoSidedLeave, args.Unsupported); Assert.Empty(args.Names["twosided"]);

        Assert.Null(BlenderVehicleRig.ParseArgs(Argv()).Unsupported); Assert.Empty(BlenderVehicleRig.ParseArgs(Argv()).Names["twosided"]);
        Assert.Null(BlenderVehicleRig.ParseArgs(Argv("twosided=")).Unsupported);
        Assert.Null(BlenderVehicleRig.ParseArgs(Argv("twosided=  ")).Unsupported);

        args = BlenderVehicleRig.ParseArgs(Argv("defaultreduce=nopipe", "twosided=@C:/p/x_twosided.txt"));
        Assert.Null(args.Unsupported); Assert.Contains("not enough values to unpack", args.TagError);

        var argv = Argv("twosided=@C:/p/x_twosided.txt"); argv[34] = "bad pivot";
        args = BlenderVehicleRig.ParseArgs(argv);
        Assert.Null(args.Unsupported); Assert.Contains("bad pivot", args.NamesDie);
    }
}
