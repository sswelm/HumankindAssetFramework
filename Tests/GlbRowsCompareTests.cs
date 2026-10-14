using System.Collections.Generic;
using System.IO;
using Xunit;

public class GlbRowsCompareTests
{
    // Blender re-encodes the rig's JPEG albedo. Unknown image bytes must not make file evidence optional.
    static BlenderDeployExport.Result Export()
    {
        var x = new BlenderDeployExport.Result();
        x.Scenes.Add(("Scene", new List<int>()));
        x.MaterialRows = new BlenderMaterialExport.Result();
        x.MaterialRows.Images.Add(("albedo", "image/png", null, 0));
        x.ChannelRows = new BlenderChannelExport.Result();
        return x;
    }

    static List<string[]> Rows() => new List<string[]>
    {
        new[] { "SCENE", "Scene", "" },
        new[] { "IMG", "0", "albedo", "image/png", new string('a', 64) },
        new[] { "GLB", "100", "40", "32", new string('b', 64) },
    };

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("short")]
    public void Unknown_image_bytes_do_not_allow_missing_or_invalid_file_evidence(string damage)
    {
        var rows = Rows();
        if (damage == "missing") rows.RemoveAt(2);
        else if (damage == "duplicate") rows.Add(rows[2]);
        else rows[2] = new[] { "GLB", "100" };
        Assert.Throws<InvalidDataException>(() => GlbRowsCompare.Compare(Export(), rows, new List<string>(),
            new Dictionary<string, long>(), _ => 0, true, true, null));
    }

    [Fact]
    public void Intact_file_evidence_compares_structure_and_reports_uncompared_image_bytes()
    {
        var problems = new List<string>(); var cover = new Dictionary<string, long>();
        var stats = GlbRowsCompare.Compare(Export(), Rows(), problems, cover, _ => 0, true, true, null);
        Assert.Empty(problems);
        Assert.False(stats.GlbEqual);
        Assert.Equal(1L, cover["the GLB's bytes not compared (an image's bytes are Blender's encoder's)"]);
    }
}
