using System;
using System.Collections.Generic;
using Xunit;

public class RigAnimIntegrationRulesTests
{
    [Theory]
    [InlineData(true, true, true, false, false, true)]
    [InlineData(false, true, true, false, false, false)] // explicit rebuild retries the C# route
    [InlineData(true, true, true, false, true, false)] // changed model can have a supported shape now
    [InlineData(true, true, true, true, false, false)]
    [InlineData(true, true, false, false, false, false)]
    [InlineData(true, false, true, false, false, false)]
    public void Fallback_reuse_requires_fresh_inputs_and_requested_reuse(bool reuse, bool exists, bool key, bool tool, bool source, bool expected)
        => Assert.Equal(expected, BakerRules.CanReuseRigFallback(reuse, exists, key, tool, source));

    [Theory]
    [InlineData("Spin")]
    [InlineData("_Spin")]
    [InlineData("Spin\tA")]
    [InlineData("Spin\nB")]
    [InlineData("Spin\r\nC")]
    [InlineData("\u0130\U00010000")]
    public void Sidecar_round_trips_complete_action_identifiers(string action)
    {
        var roles = new List<(string role, string action)> { ("move", action), ("attack", new string('a', 180)) };
        Assert.True(BlenderRigAnimInProcess.ParseRolesSidecar(BlenderRigAnimInProcess.RolesSidecar(action, roles), out var primary, out var back));
        Assert.Equal(action, primary); Assert.Equal(roles, back);
    }

    [Fact]
    public void Old_sidecars_remain_readable()
    {
        Assert.True(BlenderRigAnimInProcess.ParseRolesSidecar("primary\tSpin\nmove\tRun\n", out var primary, out var roles));
        Assert.Equal("Spin", primary); Assert.Equal(new[] { ("move", "Run") }, roles);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"primary\":\"Spin\",\"roles\":null}")]
    [InlineData("{\"primary\":\"Spin\",\"roles\":[{\"role\":\"move\",\"action\":\"Run\"},{\"role\":\"move\",\"action\":\"Walk\"}]}")]
    public void Invalid_sidecars_cannot_select_a_role(string text)
        => Assert.False(BlenderRigAnimInProcess.ParseRolesSidecar(text, out _, out _));

    [Fact]
    public void Image_encoding_is_inferred_from_the_bytes()
    {
        Assert.Equal("image/jpeg", BlenderRigAnimInProcess.ImageMime(new byte[] { 255, 216, 255, 0 }));
        Assert.Equal("image/png", BlenderRigAnimInProcess.ImageMime(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.Null(BlenderRigAnimInProcess.ImageMime(new byte[] { 1, 2, 3 }));
    }

    static HafModel Images(byte second = 3)
    {
        var m = new HafModel();
        m.Images.Add(new HafImage { Name = "albedo", MimeType = "image/jpeg", Bytes = new byte[] { 1, 2 } });
        m.Images.Add(new HafImage { Name = "normal", MimeType = "image/png", Bytes = new byte[] { second } });
        return m;
    }

    [Fact]
    public void Jpeg_exception_does_not_hide_damage_to_another_image()
    {
        var a = Images(); var b = Images(99);
        b.Images[0].MimeType = "image/png"; b.Images[0].Bytes = new byte[] { 8, 9 };
        string difference = HafModelDiff.FirstDifference(a, b, new HashSet<int> { 0 });
        Assert.Contains("image 1 (normal) bytes", difference);
        Assert.Equal(new byte[] { 1, 2 }, a.Images[0].Bytes); // the comparator never erases the inputs
        Assert.Equal(new byte[] { 8, 9 }, b.Images[0].Bytes);
    }

    [Fact]
    public void Jpeg_exception_keeps_names_and_geometry_in_the_comparison()
    {
        var a = Images(); var b = Images(); b.Images[0].MimeType = "image/png"; b.Images[0].Bytes = new byte[] { 8, 9 };
        Assert.Null(HafModelDiff.FirstDifference(a, b, new HashSet<int> { 0 }));
        b.Images[0].Name = "wrong";
        Assert.Contains("image 0 (albedo) name", HafModelDiff.FirstDifference(a, b, new HashSet<int> { 0 }));
    }
}
