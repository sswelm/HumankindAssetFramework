using System;
using System.IO;
using System.Linq;
using Xunit;

// The PNG codec's native kernels (editor/HafNativeKernels.Png.cs over native/hafpng.cpp) against their managed twins in
// editor/PngCodec.cs: the rows (every colour type, bit depth, filter, a short palette, a short tRNS), the packing, the two
// checksums (block boundaries, an offset), and the compressor - whose bytes are its own, so it is held to the pixels: what
// the native encoder writes, the managed decoder reads back, and the other way round. The DLL beside the test host must
// load (Available), as NativeKernelsTests demands; a managed-only run here would prove nothing about the kernels.
[Collection("native kernels switch")]
public class NativePngTests
{
    static void NeedNative() => Assert.True(HafNativeKernels.Available, "the native kernels must load for this test: " + HafNativeKernels.LoadStatus);

    static byte[] Chunk(string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type); uint crc = 0xffffffff;
        foreach (byte v in t.Concat(data)) { crc ^= v; for (int b = 0; b < 8; b++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1; }
        crc ^= 0xffffffff;
        return new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length }
            .Concat(t).Concat(data).Concat(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }).ToArray();
    }

    /// <summary>A PNG of the given scanlines (filter byte + stride per row, as written), any colour type and depth.</summary>
    static byte[] Png(int width, int height, int ct, int bits, byte[] scanlines, byte[] palette = null, byte[] trns = null)
    {
        var header = new[] { (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, (byte)bits, (byte)ct, (byte)0, (byte)0, (byte)0 };
        var z = new MemoryStream(); z.WriteByte(0x78); z.WriteByte(0x9c);
        using (var ds = new System.IO.Compression.DeflateStream(z, System.IO.Compression.CompressionMode.Compress, true)) ds.Write(scanlines, 0, scanlines.Length);
        uint a = 1, b = 0; foreach (byte v in scanlines) { a = (a + v) % 65521; b = (b + a) % 65521; } uint ad = (b << 16) | a;
        z.Write(new[] { (byte)(ad >> 24), (byte)(ad >> 16), (byte)(ad >> 8), (byte)ad }, 0, 4);
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.Concat(Chunk("IHDR", header));
        if (palette != null) png = png.Concat(Chunk("PLTE", palette));
        if (trns != null) png = png.Concat(Chunk("tRNS", trns));
        return png.Concat(Chunk("IDAT", z.ToArray())).Concat(Chunk("IEND", new byte[0])).ToArray();
    }

    static int Samples(int ct) => ct == 0 ? 1 : ct == 2 ? 3 : ct == 3 ? 1 : ct == 4 ? 2 : 4;

    /// <summary>Random scanlines of `height` rows, the filter byte cycling 0..4 (a Paeth and an Average row see a row above).</summary>
    static byte[] Scanlines(int width, int height, int ct, int bits, int seed, int badRow = -1, int badFilter = 7)
    {
        var rnd = new Random(seed);
        long stride = ((long)width * Samples(ct) * bits + 7) / 8;
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = (byte)(y == badRow ? badFilter : (y * 3 + seed) % 5);
            for (int i = 0; i < stride; i++) raw[y * (stride + 1) + 1 + i] = (byte)rnd.Next(256);
        }
        return raw;
    }

    public static TheoryData<int, int, bool, bool> Shapes()
    {
        var d = new TheoryData<int, int, bool, bool>();
        foreach (int bits in new[] { 1, 2, 4, 8 }) foreach (bool trns in new[] { false, true }) { d.Add(0, bits, false, trns); d.Add(3, bits, true, trns); }
        d.Add(2, 8, false, false); d.Add(2, 8, false, true); d.Add(4, 8, false, false); d.Add(6, 8, false, false);
        return d;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Rows_native_equal_managed_for_every_colour_type_depth_filter_palette_and_trns(int ct, int bits, bool palette, bool trns)
    {
        NeedNative();
        int width = 37, height = 11;   // an odd width: sub-byte rows end mid-byte
        byte[] pal = palette ? Enumerable.Range(0, 3 * 5).Select(i => (byte)(i * 17)).ToArray() : null;   // 5 entries: indices past it read zero
        byte[] tr = !trns ? null : ct == 3 ? new byte[] { 200, 0, 128 } : ct == 2 ? new byte[] { 0, 10, 0, 20, 0, 30 } : new byte[] { 0, 3 };
        var raw = Scanlines(width, height, ct, bits, ct * 10 + bits);
        if (ct == 2 && trns) { raw[1] = 10; raw[2] = 20; raw[3] = 30; }   // the first pixel is the transparent colour (filter 0 row)
        int samples = Samples(ct), bpp = Math.Max(1, samples * bits / 8); long stride = ((long)width * samples * bits + 7) / 8;
        var managed = new byte[width * height * 4]; var native = new byte[width * height * 4];
        Assert.Null(PngCodec.RowsManaged((byte[])raw.Clone(), width, height, bits, ct, samples, bpp, stride, pal, tr, managed));
        Assert.Equal(0, HafNativeKernels.PngRows((byte[])raw.Clone(), width, height, bits, ct, samples, bpp, stride, pal, tr, native));
        Assert.Equal(managed, native);
        // and through the public decoder on each path, from a real file
        var png = Png(width, height, ct, bits, raw, pal, tr);
        var a = PngCodec.Decode(png, false, out var ia, out string wa); var b = PngCodec.Decode(png, true, out var ib, out string wb);
        Assert.Null(wa); Assert.Null(wb);
        Assert.Equal(managed, a); Assert.Equal(managed, b);
        Assert.Equal(ia.Channels, ib.Channels);
    }

    [Fact]
    public void A_bad_filter_byte_is_named_by_both_paths()
    {
        NeedNative();
        int width = 9, height = 6;
        var raw = Scanlines(width, height, 2, 8, 5, badRow: 2, badFilter: 7);
        var outp = new byte[width * height * 4];
        Assert.Equal("PNG filter 7", PngCodec.RowsManaged((byte[])raw.Clone(), width, height, 8, 2, 3, 3, 27, null, null, outp));
        Assert.Equal(3, HafNativeKernels.PngRows((byte[])raw.Clone(), width, height, 8, 2, 3, 3, 27, null, null, outp));
        var png = Png(width, height, 2, 8, raw);
        Assert.Null(PngCodec.Decode(png, false, out _, out string wa)); Assert.Equal("PNG filter 7", wa);
        Assert.Null(PngCodec.Decode(png, true, out _, out string wb)); Assert.Equal("PNG filter 7", wb);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(5551)] [InlineData(5552)] [InlineData(65536)] [InlineData(200001)]
    public void Checksums_native_equal_managed_across_block_boundaries_and_offsets(int n)
    {
        NeedNative();
        var rnd = new Random(n); var d = new byte[n + 7]; rnd.NextBytes(d);
        var exact = new byte[n]; Array.Copy(d, exact, n);
        Assert.Equal(PngCodec.Adler32(exact, false), HafNativeKernels.Adler32(exact, n));
        Assert.Equal(PngCodec.Adler32(exact, false), PngCodec.Adler32(exact, true));
        Assert.Equal(PngCodec.Crc32(d, 3, n, 0xFFFFFFFFu, false), PngCodec.Crc32(d, 3, n, 0xFFFFFFFFu, true));
        uint running = PngCodec.Crc32(d, 0, 3, 0xFFFFFFFFu, false);   // a continued state, as Chunk continues the type's into the data's
        Assert.Equal(PngCodec.Crc32(d, 3, n, running, false), HafNativeKernels.Crc32(d, 3, n, running));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Native_encode_is_a_zlib_stream_of_the_same_pixels_read_by_either_decoder(bool alpha)
    {
        NeedNative();
        int width = 301, height = 97;
        var rnd = new Random(42); var rgba = new byte[width * height * 4]; rnd.NextBytes(rgba);
        for (int i = 0; i < rgba.Length; i += 4) if (i % 1000 < 400) { rgba[i] = 7; rgba[i + 1] = 7; rgba[i + 2] = 7; }   // runs: something to compress
        var nat = PngCodec.Encode(width, height, rgba, alpha, true);
        var man = PngCodec.Encode(width, height, rgba, alpha, false);
        Assert.NotEqual(nat, man);   // two compressors
        byte[] expect = (byte[])rgba.Clone();
        if (!alpha) for (int i = 3; i < expect.Length; i += 4) expect[i] = 255;
        foreach (var png in new[] { nat, man }) foreach (bool native in new[] { true, false })
        {
            var px = PngCodec.Decode(png, native, out var info, out string why);
            Assert.Null(why); Assert.Equal(width, info.Width); Assert.Equal(height, info.Height); Assert.Equal(alpha ? 4 : 3, info.Channels);
            Assert.Equal(expect, px);
        }
        // the packed rows are the twins' bytes
        var rawN = new byte[(width * (alpha ? 4 : 3) + 1) * height]; var rawM = new byte[rawN.Length];
        HafNativeKernels.PngPack(rgba, width, height, alpha, rawN); PngCodec.PackManaged(rgba, width, height, alpha, rawM);
        Assert.Equal(rawM, rawN);
        // the native zlib stream carries the raw rows' Adler-32 at its end, and a zlib header
        var z = HafNativeKernels.ZlibCompress(rawM, PngCodec.NativeZlibLevel);
        Assert.Equal(8, z[0] & 0x0F); Assert.Equal(0, ((z[0] << 8) | z[1]) % 31);
        uint adler = PngCodec.Adler32(rawM, false);
        Assert.Equal(adler, (uint)((z[z.Length - 4] << 24) | (z[z.Length - 3] << 16) | (z[z.Length - 2] << 8) | z[z.Length - 1]));
    }

    [Fact]
    public void Composition_fills_and_byte_rounding_native_equal_the_managed_loops_bit_for_bit()
    {
        NeedNative();
        // every byte value in every channel, in several combinations: the fill is byte / 255 or the luminance times the alpha
        int n = 256 * 7; var px = new byte[n * 4]; var rnd = new Random(3);
        for (int p = 0; p < n; p++) { px[4 * p] = (byte)p; px[4 * p + 1] = (byte)(p * 7); px[4 * p + 2] = (byte)(255 - p); px[4 * p + 3] = p < 256 ? (byte)p : (byte)rnd.Next(256); }
        foreach (int dst in new[] { 0, 1, 2, 3 }) foreach (int src in new[] { 0, 1, 2, 3 }) foreach (bool bw in new[] { false, true })
        {
            var managed = new float[n * 4]; var native = new float[n * 4];
            for (int i = 0; i < managed.Length; i++) managed[i] = native[i] = 1f;
            if (!bw) for (int p = 0; p < n; p++) managed[4 * p + dst] = (float)(px[4 * p + src] * (1.0f / 255.0f));
            else
            {
                const float c1 = 0.2989f, c2 = 0.5870f, c3 = 0.1140f;
                for (int p = 0; p < n; p++)
                {
                    float r = (float)(px[4 * p] * (1.0f / 255.0f)), g = (float)(px[4 * p + 1] * (1.0f / 255.0f)), b = (float)(px[4 * p + 2] * (1.0f / 255.0f)), a = (float)(px[4 * p + 3] * (1.0f / 255.0f));
                    float lum = (float)((float)((float)(r * c1) + (float)(g * c2)) + (float)(b * c3));
                    managed[4 * p + dst] = (float)(lum * a);
                }
            }
            HafNativeKernels.PngFill(px, n, dst, src, bw, native);
            Assert.Equal(managed.Select(v => BitConverter.ToInt32(BitConverter.GetBytes(v), 0)), native.Select(v => BitConverter.ToInt32(BitConverter.GetBytes(v), 0)));
            var bm = new byte[n * 4]; var bn = new byte[n * 4];
            for (int i = 0; i < managed.Length; i++) bm[i] = FbxMaterials.UnitFloatToUchar(managed[i]);
            HafNativeKernels.UnitToUchar(native, bn);
            Assert.Equal(bm, bn);
        }
        // the rounding's edges: at and around 0, the 255 threshold, every k/255 and the half steps between them
        var edges = new System.Collections.Generic.List<float> { -1f, -1e-30f, 0f, 1e-30f, 0.5f / 255f, 1f - 0.5f / 255f, 1f, 1.5f, 100f };
        for (int k = 0; k <= 255; k++) { edges.Add(k / 255f); edges.Add((k + 0.5f) / 255f); edges.Add((k + 0.499f) / 255f); edges.Add((k + 0.501f) / 255f); }
        var ef = edges.ToArray(); var em = new byte[ef.Length]; var en = new byte[ef.Length];
        for (int i = 0; i < ef.Length; i++) em[i] = FbxMaterials.UnitFloatToUchar(ef[i]);
        HafNativeKernels.UnitToUchar(ef, en);
        Assert.Equal(em, en);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(55)] [InlineData(56)] [InlineData(63)] [InlineData(64)] [InlineData(65)] [InlineData(1000)] [InlineData((1 << 20) + 3)]
    public void Sha256_through_cng_equals_the_managed_digest(int n)
    {
        NeedNative();
        var rnd = new Random(n); var d = new byte[n]; rnd.NextBytes(d);
        var h = new byte[32];
        Assert.True(HafNativeKernels.TrySha256(d, h), "CNG's one-shot SHA-256 must be there on this Windows");
        using (var sha = System.Security.Cryptography.SHA256.Create()) Assert.Equal(sha.ComputeHash(d), h);
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.TrySha256(d, new byte[31]));
    }

    [Fact]
    public void Kernel_entries_reject_what_the_managed_twins_would_throw_on()
    {
        NeedNative();
        var raw = new byte[(3 * 4 + 1) * 2]; var outp = new byte[4 * 2 * 4];
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.PngRows(raw, 4, 2, 8, 2, 3, 3, 12, null, null, new byte[10]));   // output short
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.PngRows(new byte[5], 4, 2, 8, 2, 3, 3, 12, null, null, outp));    // raw short
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.PngRows(raw, 4, 2, 8, 3, 1, 1, 4, null, null, outp));            // a palette image without a palette
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.PngRows(raw, 4, 2, 8, 2, 4, 3, 12, null, null, outp));           // samples off
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.PngPack(new byte[3], 4, 2, false, raw));
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.Adler32(raw, raw.Length + 1));
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.Crc32(raw, 2, raw.Length, 0));
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.ZlibCompress(raw, 0));
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.ZlibCompress(raw, 13));
    }
    [Fact]
    public void Kernel_dimensions_cannot_wrap_the_buffer_guards()
    {
        NeedNative();
        // width*height*4 overflows Int64, and (width*4+1)*height wraps to 17 in Int32.
        // The former guards accepted these tiny buffers and called C++ out of bounds.
        const int width = 1501975312, height = 1768226769;
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.PngPack(new byte[0], width, height, true, new byte[17]));
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.PngRows(new byte[17], width, height, 8, 6, 4, 4, (long)width * 4, null, null, new byte[0]));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Encode_validates_dimensions_and_input_before_either_backend(bool native)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PngCodec.Encode(1501975312, 1768226769, new byte[0], true, native));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngCodec.Encode(0, 1, new byte[4], true, native));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngCodec.Encode(1, -1, new byte[4], true, native));
        Assert.Throws<ArgumentNullException>(() => PngCodec.Encode(1, 1, null, true, native));
        Assert.Throws<ArgumentException>(() => PngCodec.Encode(2, 2, new byte[15], true, native));
    }

}
