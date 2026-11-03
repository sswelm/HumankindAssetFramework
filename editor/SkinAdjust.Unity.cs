// SkinAdjust.Unity.cs - the Color32 face of SkinAdjust for the editor windows (the pure math is in SkinAdjust.cs, which
// the unit tests compile without UnityEngine).
using UnityEngine;

public static partial class SkinAdjust
{
    /// <summary>A new array: the source pixels with the adjustments applied (the source untouched).</summary>
    public static Color32[] ApplyTo(Color32[] src, float brightness, float desaturate, float tintR, float tintG, float tintB)
    {
        var bytes = new byte[src.Length * 4];
        for (int i = 0; i < src.Length; i++) { bytes[4 * i] = src[i].r; bytes[4 * i + 1] = src[i].g; bytes[4 * i + 2] = src[i].b; bytes[4 * i + 3] = src[i].a; }
        Apply(bytes, brightness, desaturate, tintR, tintG, tintB);
        var dst = new Color32[src.Length];
        for (int i = 0; i < src.Length; i++) dst[i] = new Color32(bytes[4 * i], bytes[4 * i + 1], bytes[4 * i + 2], bytes[4 * i + 3]);
        return dst;
    }
}
