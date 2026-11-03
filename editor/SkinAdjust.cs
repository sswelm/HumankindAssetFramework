// SkinAdjust.cs - the plugin's AdjustSkin pixel math in one place (2026-11-03): gamma FIRST, then the pull toward each
// pixel's luminance (desaturate), then the per-channel offset (tint), each channel clamped to 0..255 - the order the
// runtime applies to a retexture PNG before it replaces a unit's atlas. The Retexture window's live preview and the
// Animation Lab's "Skin" preview both run this, so what either shows is what the game draws. Pure (no UnityEngine): the
// unit tests hold it, and a Color32 array is passed as its interleaved RGBA bytes.
using System;

public static partial class SkinAdjust   // the Color32 overload the editor windows call is in SkinAdjust.Unity.cs
{
    /// <summary>The gamma lookup for a brightness, or null when the brightness changes nothing (|b - 1| &lt;= 0.001).
    /// An entry saved before the knob existed carries 0: treated as unchanged by the callers (RetextureWindow's rule).</summary>
    public static byte[] GammaLut(float brightness)
    {
        if (Math.Abs(brightness - 1f) <= 0.001f) return null;
        float inv = 1f / Clamp(brightness, 0.2f, 4f);
        var lut = new byte[256];
        for (int v = 0; v < 256; v++) lut[v] = (byte)ClampInt((int)Math.Round(255f * (float)Math.Pow(v / 255f, inv)), 0, 255);
        return lut;
    }

    /// <summary>In place on interleaved RGBA bytes (the alpha untouched): gamma, then desaturate (0 keeps the colours, 1 is
    /// the luminance alone), then the tint offsets (-255..255 per channel).</summary>
    public static void Apply(byte[] rgba, float brightness, float desaturate, float tintR, float tintG, float tintB)
    {
        if (rgba == null) throw new ArgumentNullException(nameof(rgba));
        byte[] lut = GammaLut(brightness);
        float s = Clamp(desaturate, 0f, 1f);
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            byte r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            if (lut != null) { r = lut[r]; g = lut[g]; b = lut[b]; }
            float lum = r * 0.299f + g * 0.587f + b * 0.114f;
            rgba[i] = (byte)Clamp((r + (lum - r) * s) + tintR, 0f, 255f);
            rgba[i + 1] = (byte)Clamp((g + (lum - g) * s) + tintG, 0f, 255f);
            rgba[i + 2] = (byte)Clamp((b + (lum - b) * s) + tintB, 0f, 255f);
        }
    }

    static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    static int ClampInt(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
