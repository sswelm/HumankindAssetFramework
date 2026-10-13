using Xunit;

// BakerRules.DeployRecoilRanges (editor/EditorRules.cs, 2026-10-13): the Factory's recoil field as deploy_convert.py's
// CSV starts and ends - lifted out of UniversalBaker.EnsureDeployConverted so the Bake Tests' deploy row builds the
// same argv the Factory hands both the in-process port and the script.
public class DeployRecoilRangesTests
{
    [Fact]
    public void One_window_is_its_start_and_end()
    {
        Assert.True(BakerRules.DeployRecoilRanges("440..510", out string s, out string e, out string err));
        Assert.Equal("440", s); Assert.Equal("510", e); Assert.Null(err);
    }

    [Fact]
    public void Several_windows_become_csv_starts_and_ends_and_a_speed_step_rides_on_the_end()
    {
        Assert.True(BakerRules.DeployRecoilRanges(" 443..530 , 305 .. 441/2 ", out string s, out string e, out _));
        Assert.Equal("443,305", s); Assert.Equal("530,441/2", e);
        Assert.True(BakerRules.DeployRecoilRanges("1..2 / 3", out s, out e, out _));
        Assert.Equal("1", s); Assert.Equal("2/3", e);
    }

    [Fact]
    public void An_empty_field_is_no_recoil()
    {
        foreach (string f in new[] { null, "", "   " })
        {
            Assert.True(BakerRules.DeployRecoilRanges(f, out string s, out string e, out string err));
            Assert.Equal("", s); Assert.Equal("", e); Assert.Null(err);
        }
    }

    [Fact]
    public void A_malformed_or_reversed_window_is_refused_with_the_Factorys_own_words()
    {
        Assert.False(BakerRules.DeployRecoilRanges("440-510", out _, out _, out string err));
        Assert.Contains("recoil range must look like 440..510", err);
        Assert.Contains("got '440-510'", err);
        Assert.False(BakerRules.DeployRecoilRanges("443..530,510..440", out _, out _, out err));
        Assert.Contains("start must be BEFORE end", err);
        Assert.Contains("510..440", err);
        Assert.False(BakerRules.DeployRecoilRanges("5..5", out _, out _, out err));   // equal is reversed too: no frame runs forward
    }
}
