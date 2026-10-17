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
                (tag == "windowreduce" ? detail : tag + "=@" + path + "|20 (" + detail + ")"), args.TagError);
        }
        finally { Directory.Delete(path); }
    }

    [Theory]
    [InlineData("defaultreduce")]
    [InlineData("shroudreduce")]
    [InlineData("windowreduce")]
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
}
