// HafNativeKernels.Png.cs - the PNG codec's kernels (editor/PngCodec.cs) in native/hafpng.cpp: Decode's scanline unfilter
// and pixel expansion, Encode's row packing and zlib compression (libdeflate, vendored under native/libdeflate), and the
// two checksums. Measured 2026-11-02 on the Bradley's 4096x4096 textures in Unity's Mono: a decode 0.8 s (the inflate
// 0.13 s of it, the managed rows the rest), an encode 1.2-2.3 s (Mono's DeflateStream, whose level is fixed), an Adler-32
// over 48 MB 0.17 s - eight textures and two compositions per import of that file. The rows, the packing and the
// checksums give the managed twins' bytes; the compressor gives another valid zlib stream of the same bytes (a PNG's bytes
// were never held to anything, its pixels are - Tests/FbxMaterialsTests, the FBX model drill).
using System;
using System.Runtime.InteropServices;

public static partial class HafNativeKernels
{
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern int haf_png_rows(byte[] raw, long rawLen, int width, int height, int bits, int ct, int samples, int bpp, long stride, byte[] palette, int palLen, byte[] trns, int trnsLen, byte[] outp);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_png_pack(byte[] rgba, int width, int height, int alpha, byte[] raw);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern uint haf_adler32(byte[] d, long n);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern uint haf_crc32(byte[] d, long at, long n, uint running);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_png_fill(byte[] px, long n, int dst, int src, int bw, float[] outF);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_unit_to_uchar(float[] f, long n, byte[] outp);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern int haf_sha256(byte[] d, long n, byte[] outp);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern long haf_zlib_bound(long n);

    /// <summary>SHA-256 of d through Windows CNG (hafpng.cpp); false when CNG's one-shot hash is not there (the caller keeps
    /// the managed SHA256 - the same 32 bytes either way, only Mono's managed speed differs). Not a twin of anything managed
    /// here: the hash is the system's.</summary>
    internal static bool TrySha256(byte[] d, byte[] hash)
    {
        Need(hash.Length >= 32, "sha256: a " + hash.Length + "-byte output");
        return haf_sha256(d, d.Length, hash) == 0;
    }

    /// <summary>FbxMaterials' composition fill: channel `dst` of the n float pixels in outF from channel `src` of the RGBA bytes
    /// px (byte / 255), or the luminance times the alpha when `bw`. Float32 step for step as the managed loops.</summary>
    internal static void PngFill(byte[] px, int n, int dst, int src, bool bw, float[] outF)
    {
        Need(n >= 0 && (long)px.Length >= 4L * n && (long)outF.Length >= 4L * n, "png fill: " + px.Length + " bytes and " + outF.Length + " floats for " + n + " pixels");
        Need(dst >= 0 && dst < 4 && src >= 0 && src < 4, "png fill: channels " + src + " -> " + dst);
        haf_png_fill(px, n, dst, src, bw ? 1 : 0, outF);
    }

    /// <summary>FbxMaterials.UnitFloatToUchar over every float of f into outp.</summary>
    internal static void UnitToUchar(float[] f, byte[] outp)
    {
        Need(outp.Length >= f.Length, "unit to uchar: " + outp.Length + " bytes for " + f.Length + " floats");
        haf_unit_to_uchar(f, f.Length, outp);
    }
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern long haf_zlib_compress(byte[] inp, long n, int level, byte[] outp, long cap);

    /// <summary>PngCodec.RowsManaged: raw holds `height` rows of (1 filter byte + `stride` bytes) and is unfiltered IN PLACE;
    /// outp receives width*height*4 RGBA bytes. Returns 0, or y + 1 for the first row whose filter byte exceeds 4.</summary>
    internal static int PngRows(byte[] raw, int width, int height, int bits, int ct, int samples, int bpp, long stride, byte[] palette, byte[] trns, byte[] outp)
    {
        Need(width > 0 && height > 0, "png rows: " + width + " x " + height);
        // Two positive int dimensions fit in long; multiplying that product by four need not.
        long pixels = (long)width * height;
        Need(pixels <= outp.LongLength / 4, "png rows: " + outp.Length + " output bytes for " + width + " x " + height);
        Need(bits == 1 || bits == 2 || bits == 4 || bits == 8, "png rows: bit depth " + bits);
        Need(ct == 0 || ct == 2 || ct == 3 || ct == 4 || ct == 6, "png rows: colour type " + ct);
        Need(samples == (ct == 0 ? 1 : ct == 2 ? 3 : ct == 3 ? 1 : ct == 4 ? 2 : 4), "png rows: " + samples + " samples for colour type " + ct);
        Need(bpp == Math.Max(1, samples * bits / 8), "png rows: " + bpp + " bytes per pixel");
        Need(stride == ((long)width * samples * bits + 7) / 8, "png rows: stride " + stride);
        Need((stride + 1) * height <= raw.Length, "png rows: " + raw.Length + " raw bytes for " + height + " rows of " + (stride + 1));
        if (ct == 3) Need(palette != null, "png rows: a palette image without a palette");
        return haf_png_rows(raw, raw.Length, width, height, bits, ct, samples, bpp, stride, palette, palette == null ? 0 : palette.Length, trns, trns == null ? 0 : trns.Length, outp);
    }

    /// <summary>PngCodec.PackManaged: rgba holds width*height*4 bytes; raw receives height rows of (0 + width*samples bytes).</summary>
    internal static void PngPack(byte[] rgba, int width, int height, bool alpha, byte[] raw)
    {
        Need(width > 0 && height > 0, "png pack: " + width + " x " + height);
        long pixels = (long)width * height;
        Need(pixels <= rgba.LongLength / 4, "png pack: " + rgba.Length + " pixel bytes for " + width + " x " + height);
        Need((long)raw.Length >= ((long)width * (alpha ? 4 : 3) + 1) * height, "png pack: " + raw.Length + " raw bytes");
        haf_png_pack(rgba, width, height, alpha ? 1 : 0, raw);
    }

    internal static uint Adler32(byte[] d, long n)
    {
        Need(n >= 0 && n <= d.Length, "adler32: " + n + " of " + d.Length + " bytes");
        return haf_adler32(d, n);
    }

    /// <summary>PngCodec.Crc32's running state over d[at, at + n).</summary>
    internal static uint Crc32(byte[] d, int at, int n, uint running)
    {
        Need(at >= 0 && n >= 0 && (long)at + n <= d.Length, "crc32: [" + at + ", " + at + "+" + n + ") of " + d.Length + " bytes");
        return haf_crc32(d, at, n, running);
    }

    /// <summary>The zlib stream of `raw` at libdeflate's level (1..12, 6 the size class of zlib's default).</summary>
    internal static byte[] ZlibCompress(byte[] raw, int level)
    {
        Need(level >= 1 && level <= 12, "zlib level " + level);
        long cap = haf_zlib_bound(raw.Length);
        Need(cap > 0 && cap < int.MaxValue, "zlib bound " + cap + " for " + raw.Length + " bytes");
        var outp = new byte[cap];
        long n = haf_zlib_compress(raw, raw.Length, level, outp, cap);
        if (n <= 0) throw new InvalidOperationException("native kernels: zlib compression of " + raw.Length + " bytes gave nothing");
        var result = new byte[n]; Array.Copy(outp, result, n);
        return result;
    }
}
