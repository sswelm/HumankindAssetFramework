using System;
using Xunit;

// editor/SkinAdjust.cs - the plugin's AdjustSkin pixel math (gamma, then the luminance pull, then the tint), shared by the
// Retexture window's live preview and the Animation Lab's Skin preview (2026-11-03).
public class SkinAdjustTests
{
    static byte[] Px(params byte[] rgba) => (byte[])rgba.Clone();

    [Fact]
    public void Unchanged_adjustments_leave_every_byte_alone_and_the_alpha_always()
    {
        var px = Px(10, 200, 77, 5, 255, 0, 128, 250);
        SkinAdjust.Apply(px, 1f, 0f, 0f, 0f, 0f);
        Assert.Equal(new byte[] { 10, 200, 77, 5, 255, 0, 128, 250 }, px);
        Assert.Null(SkinAdjust.GammaLut(1f)); Assert.Null(SkinAdjust.GammaLut(1.0005f));
        Assert.NotNull(SkinAdjust.GammaLut(0f));   // an entry saved before the knob carries 0: the CALLERS read it as 1 (RetextureWindow, the Lab); the math itself clamps it
        var alpha = Px(10, 200, 77, 5);
        SkinAdjust.Apply(alpha, 2f, 1f, 255f, -255f, 40f);
        Assert.Equal(5, alpha[3]);
    }

    [Fact]
    public void Full_desaturate_pulls_each_channel_to_the_luminance()
    {
        var px = Px(200, 100, 50, 255);
        SkinAdjust.Apply(px, 1f, 1f, 0f, 0f, 0f);
        byte lum = (byte)(200 * 0.299f + 100 * 0.587f + 50 * 0.114f);   // 124.2 -> 124
        Assert.Equal(new byte[] { lum, lum, lum, 255 }, px);
    }

    [Fact]
    public void Tint_offsets_add_per_channel_and_clamp()
    {
        var px = Px(200, 100, 50, 255);
        SkinAdjust.Apply(px, 1f, 0f, 100f, -150f, 50f);
        Assert.Equal(new byte[] { 255, 0, 100, 255 }, px);
    }

    [Fact]
    public void Gamma_lifts_the_midtones_first_and_a_brightness_is_clamped_to_its_range()
    {
        var lut = SkinAdjust.GammaLut(2f);
        Assert.Equal(0, lut[0]); Assert.Equal(255, lut[255]);
        Assert.Equal((byte)Math.Round(255f * (float)Math.Pow(128 / 255f, 0.5f)), lut[128]);   // 180.6 -> 181
        Assert.True(lut[64] > 64);
        Assert.Equal(SkinAdjust.GammaLut(4f)[128], SkinAdjust.GammaLut(40f)[128]);   // clamped at 4
        Assert.Equal(SkinAdjust.GammaLut(0.2f)[128], SkinAdjust.GammaLut(0.01f)[128]);   // clamped at 0.2
        // the order: gamma first, then the pull, then the tint - the mech's entry (1.5, 0.85, +50 +50 0) on a mid grey-orange
        var px = Px(180, 110, 40, 255);
        SkinAdjust.Apply(px, 1.5f, 0.85f, 50f, 50f, 0f);
        var g = SkinAdjust.GammaLut(1.5f); float r = g[180], gg = g[110], b = g[40];
        float lum = r * 0.299f + gg * 0.587f + b * 0.114f;
        Assert.Equal((byte)Math.Min(255f, (r + (lum - r) * 0.85f) + 50f), px[0]);
        Assert.Equal((byte)Math.Min(255f, (gg + (lum - gg) * 0.85f) + 50f), px[1]);
        Assert.Equal((byte)(b + (lum - b) * 0.85f), px[2]);
    }
}
