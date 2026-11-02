using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

// PngCodec.cs - a PNG decoder and encoder without Unity (sub-step 6b of the native FBX reader, 2026-11-02). Blender reads a
// texture through OpenImageIO and keeps its pixels as four bytes per pixel (imbuf/intern/oiio/openimageio_support.cc: a
// one-channel file is replicated into R, G and B with an opaque alpha; a two-channel file is grey + alpha; a three-channel
// file gets an opaque alpha); its glTF exporter composes the metallic-roughness image (and an alpha read from a colour
// output) from those pixels and writes a new PNG. The FBX path needs the same pixels in C# (FbxMaterials): Decode gives
// them for the files Blender's reader would (8-bit, every colour type, a palette expanded, a tRNS chunk as an alpha channel
// as libpng's png_set_tRNS_to_alpha does), and names what it leaves alone (16-bit samples, which Blender keeps as a float
// buffer; Adam7 interlacing). Encode writes 8-bit RGB or RGBA, one IDAT, no filtering, a zlib stream - any
// reader decodes it; the bytes are not Blender's (it filters and compresses differently), so the drill compares PIXELS.
//
// The kernels (2026-11-02, HafNativeKernels.Png.cs over native/hafpng.cpp): with the native kernels available, the rows
// (unfilter + expansion), the row packing, the checksums and the compression run in C++; the managed code below is their
// twin and the fallback. Each public entry asks HafNativeKernels.Available once and takes one path whole. The native
// compressor is libdeflate at NativeZlibLevel; DeflateStream's level is fixed in Unity's Mono (measured: Fastest = Optimal
// there), so the native path is the only one with a choice, and a PNG's bytes are held to nothing but its pixels.
public static class PngCodec
{
    /// <summary>libdeflate's level for the native encoder (1..12): the size class of zlib's default at a fraction of its time.</summary>
    public const int NativeZlibLevel = 6;

    public sealed class Info
    {
        public int Width, Height, BitDepth, ColorType;
        public bool Interlaced, HasTrns;
        /// <summary>The channel count OpenImageIO reports after libpng's expansions: a palette is 3 (4 with tRNS), grey 1 (2 with tRNS), RGB 3 (4 with tRNS), grey+alpha 2, RGBA 4.</summary>
        public int Channels;
        public bool HasAlpha => Channels == 2 || Channels == 4;
    }

    static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static bool IsPng(byte[] d)
    {
        if (d == null || d.Length < 8) return false;
        for (int i = 0; i < 8; i++) if (d[i] != Signature[i]) return false;
        return true;
    }

    /// <summary>The header and critical-chunk integrity (including IDAT presence), or null for malformed PNG chunks.
    /// Scanline decoding is checked separately by Decode.</summary>
    public static Info ReadInfo(byte[] d) => ReadInfo(d, HafNativeKernels.Available);

    static Info ReadInfo(byte[] d, bool native)
    {
        if (!IsPng(d) || d.Length < 33) return null;
        int p = 8; bool hasData = false;
        Info info = null;
        while (p <= d.Length - 12)
        {
            int len = BE(d, p); string type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
            if (len < 0 || len > d.Length - p - 12) return null;
            bool crc = ValidCrc(d, p, len, native);
            if (!crc && (type == "IHDR" || type == "PLTE" || type == "IDAT")) return null;
            if (type == "IHDR")
            {
                if (p != 8 || len != 13 || info != null || d[p + 18] != 0 || d[p + 19] != 0 || d[p + 20] > 1) return null;
                info = new Info { Width = BE(d, p + 8), Height = BE(d, p + 12), BitDepth = d[p + 16], ColorType = d[p + 17], Interlaced = d[p + 20] != 0 };
            }
            else if (type == "tRNS" && info != null && crc) info.HasTrns = true;
            else if (type == "IDAT") hasData = true;
            else if (type == "IEND") break;
            p += 12 + len;
        }
        if (info == null || !hasData || info.Width <= 0 || info.Height <= 0) return null;
        bool greyOrPalette = info.ColorType == 0 || info.ColorType == 3;
        if (!(info.BitDepth == 8 || (info.BitDepth == 16 && info.ColorType != 3) ||
            (greyOrPalette && (info.BitDepth == 1 || info.BitDepth == 2 || info.BitDepth == 4)))) return null;
        switch (info.ColorType)
        {
            case 0: info.Channels = info.HasTrns ? 2 : 1; break;
            case 2: info.Channels = info.HasTrns ? 4 : 3; break;
            case 3: info.Channels = info.HasTrns ? 4 : 3; break;
            case 4: info.Channels = 2; break;
            case 6: info.Channels = 4; break;
            default: return null;
        }
        return info;
    }

    static bool ValidCrc(byte[] d, int p, int len, bool native) =>
        (Crc32(d, p + 4, len + 4, 0xFFFFFFFFu, native) ^ 0xFFFFFFFFu) == unchecked((uint)BE(d, p + 8 + len));

    /// <summary>The pixels as Blender holds them: four bytes per pixel, rows top-down. `why` names what could not be decoded
    /// (the result is null then).</summary>
    public static byte[] Decode(byte[] d, out Info info, out string why) => Decode(d, HafNativeKernels.Available, out info, out why);

    /// <summary>Decode on one path: `native` names it (the tests hold the two twins to each other through this).</summary>
    internal static byte[] Decode(byte[] d, bool native, out Info info, out string why)
    {
        why = null;
        info = ReadInfo(d, native);
        if (info == null) { why = "not a PNG"; return null; }
        if (info.BitDepth == 16) { why = "a 16-bit PNG (Blender keeps it as a float buffer)"; return null; }
        if (info.Interlaced) { why = "an interlaced PNG"; return null; }
        if (info.Width <= 0 || info.Height <= 0) { why = "an empty PNG"; return null; }
        int bits = info.BitDepth, ct = info.ColorType;
        if (!(bits == 1 || bits == 2 || bits == 4 || bits == 8)) { why = "bit depth " + bits; return null; }
        if ((ct == 2 || ct == 4 || ct == 6) && bits != 8) { why = "bit depth " + bits + " with colour type " + ct; return null; }
        byte[] palette = null, trns = null;
        var idat = new MemoryStream();
        int p = 8;
        while (p <= d.Length - 12)
        {
            int len = BE(d, p); string type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
            if (len < 0 || len > d.Length - p - 12) { why = "a truncated chunk"; return null; }
            if (type == "PLTE") { palette = new byte[len]; Array.Copy(d, p + 8, palette, 0, len); }
            else if (type == "tRNS" && ValidCrc(d, p, len, native)) { trns = new byte[len]; Array.Copy(d, p + 8, trns, 0, len); }
            else if (type == "IDAT") idat.Write(d, p + 8, len);
            else if (type == "IEND") break;
            p += 12 + len;
        }
        if (ct == 3 && palette == null) { why = "a palette PNG without a PLTE chunk"; return null; }
        int samples = ct == 0 ? 1 : ct == 2 ? 3 : ct == 3 ? 1 : ct == 4 ? 2 : 4;
        int bpp = Math.Max(1, samples * bits / 8);                    // bytes per complete pixel (for the filters)
        long stride = ((long)info.Width * samples * bits + 7) / 8;
        long pixels = (long)info.Width * info.Height;
        if (pixels > int.MaxValue / 4 || (stride + 1) * info.Height > int.MaxValue)
        { why = "PNG dimensions exceed managed buffer limits"; return null; }
        byte[] raw;
        try { raw = Inflate(idat.ToArray(), (int)((stride + 1) * info.Height), native); }
        catch (Exception e) when (e is InvalidDataException || e is IOException) { why = "zlib: " + e.Message; return null; }
        var outp = new byte[info.Width * info.Height * 4];
        if (native)
        {
            int bad = HafNativeKernels.PngRows(raw, info.Width, info.Height, bits, ct, samples, bpp, stride, palette, trns, outp);
            if (bad != 0) { why = "PNG filter " + raw[(stride + 1) * (bad - 1)]; return null; }
            return outp;
        }
        why = RowsManaged(raw, info.Width, info.Height, bits, ct, samples, bpp, stride, palette, trns, outp);
        return why == null ? outp : null;
    }

    /// <summary>The managed twin of haf_png_rows: raw's rows (1 filter byte + stride bytes each) into outp (RGBA, rows top-down);
    /// null, or "PNG filter N" for the first row whose filter byte exceeds 4.</summary>
    internal static string RowsManaged(byte[] raw, int width, int height, int bits, int ct, int samples, int bpp, long stride, byte[] palette, byte[] trns, byte[] outp)
    {
        var prev = new byte[stride]; var line = new byte[stride];
        int pos = 0;
        for (int y = 0; y < height; y++)
        {
            int filter = raw[pos++];
            if (filter > 4) return "PNG filter " + filter;
            Array.Copy(raw, pos, line, 0, stride); pos += (int)stride;
            Unfilter(filter, line, prev, bpp);
            int o = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                byte r, g, b, a = 255;
                if (bits == 8)
                {
                    int i = x * samples;
                    if (ct == 0) { r = g = b = line[i]; if (trns != null && trns.Length >= 2 && line[i] == trns[1]) a = 0; }
                    else if (ct == 2) { r = line[i]; g = line[i + 1]; b = line[i + 2]; if (trns != null && trns.Length >= 6 && r == trns[1] && g == trns[3] && b == trns[5]) a = 0; }
                    else if (ct == 3) { int k = line[i]; r = Pal(palette, 3 * k); g = Pal(palette, 3 * k + 1); b = Pal(palette, 3 * k + 2); if (trns != null) a = k < trns.Length ? trns[k] : (byte)255; }
                    else if (ct == 4) { r = g = b = line[i]; a = line[i + 1]; }
                    else { r = line[i]; g = line[i + 1]; b = line[i + 2]; a = line[i + 3]; }
                }
                else
                {
                    // sub-byte samples: grey or palette indices, packed high bits first; grey scaled to 8 bits (png_set_expand_gray_1_2_4_to_8)
                    int bitPos = x * bits; int v = (line[bitPos >> 3] >> (8 - bits - (bitPos & 7))) & ((1 << bits) - 1);
                    if (ct == 0) { byte s = (byte)(v * 255 / ((1 << bits) - 1)); r = g = b = s; if (trns != null && trns.Length >= 2 && v == trns[1]) a = 0; }
                    else { r = Pal(palette, 3 * v); g = Pal(palette, 3 * v + 1); b = Pal(palette, 3 * v + 2); if (trns != null) a = v < trns.Length ? trns[v] : (byte)255; }
                }
                outp[o + 4 * x] = r; outp[o + 4 * x + 1] = g; outp[o + 4 * x + 2] = b; outp[o + 4 * x + 3] = a;
            }
            var t = prev; prev = line; line = t;
        }
        return null;
    }

    static byte Pal(byte[] palette, int i) => i < palette.Length ? palette[i] : (byte)0;

    static void Unfilter(int filter, byte[] line, byte[] prev, int bpp)
    {
        int n = line.Length;
        switch (filter)
        {
            case 0: break;
            case 1: for (int i = bpp; i < n; i++) line[i] = (byte)(line[i] + line[i - bpp]); break;
            case 2: for (int i = 0; i < n; i++) line[i] = (byte)(line[i] + prev[i]); break;
            case 3: for (int i = 0; i < n; i++) line[i] = (byte)(line[i] + (((i >= bpp ? line[i - bpp] : 0) + prev[i]) >> 1)); break;
            case 4:
                for (int i = 0; i < n; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                    int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    line[i] = (byte)(line[i] + pred);
                }
                break;
            default: throw new InvalidDataException("PNG filter " + filter);
        }
    }

    static byte[] Inflate(byte[] z, int expected, bool native)
    {
        if (z.Length < 6) throw new InvalidDataException("no zlib stream");
        if ((z[0] & 0x0F) != 8 || (z[0] >> 4) > 7 || (z[1] & 0x20) != 0 || ((z[0] << 8) | z[1]) % 31 != 0) throw new InvalidDataException("not a zlib stream");
        using (var ms = new MemoryStream(z, 2, z.Length - 6))
        using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
        using (var outp = new MemoryStream(Math.Min(expected, 65536)))
        {
            // Read only the declared scanlines: an IDAT must not expand into an unbounded temporary buffer.
            // Grow with actual data, not a claimed huge header whose compressed stream may contain only one pixel.
            var buffer = new byte[Math.Min(expected, 4096)]; int pos = 0;
            while (pos < expected)
            {
                int n = ds.Read(buffer, 0, Math.Min(buffer.Length, expected - pos));
                if (n == 0) throw new InvalidDataException("too little image data");
                outp.Write(buffer, 0, n);
                pos += n;
            }
            var raw = outp.ToArray();
            // Blender accepts extra decompressed bytes after the image, and a missing IEND. With an exact payload,
            // validate zlib's checksum as libpng does. An extra-byte probe also finishes the deflate stream safely.
            if (ds.ReadByte() < 0 && Adler32(raw, native) != unchecked((uint)BE(z, z.Length - 4))) throw new InvalidDataException("Adler checksum mismatch");
            return raw;
        }
    }

    /// <summary>An 8-bit PNG of the given pixels (four bytes per pixel, rows top-down): RGBA when `alpha`, else RGB.</summary>
    public static byte[] Encode(int width, int height, byte[] rgba, bool alpha) => Encode(width, height, rgba, alpha, HafNativeKernels.Available);

    /// <summary>Encode on one path: `native` names it. The two paths' bytes differ (two compressors), their pixels do not.</summary>
    internal static byte[] Encode(int width, int height, byte[] rgba, bool alpha, bool native)
    {
        int samples = alpha ? 4 : 3;
        var raw = new byte[(width * samples + 1) * height];
        byte[] zlib;
        if (native)
        {
            HafNativeKernels.PngPack(rgba, width, height, alpha, raw);
            zlib = HafNativeKernels.ZlibCompress(raw, NativeZlibLevel);
        }
        else
        {
            PackManaged(rgba, width, height, alpha, raw);
            var z = new MemoryStream();
            z.WriteByte(0x78); z.WriteByte(0x9C);
            using (var ds = new DeflateStream(z, CompressionMode.Compress, true)) ds.Write(raw, 0, raw.Length);
            uint adler = Adler32(raw, false);
            z.WriteByte((byte)(adler >> 24)); z.WriteByte((byte)(adler >> 16)); z.WriteByte((byte)(adler >> 8)); z.WriteByte((byte)adler);
            zlib = z.ToArray();
        }
        var outp = new MemoryStream();
        outp.Write(Signature, 0, 8);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, width); WriteBE(ihdr, 4, height); ihdr[8] = 8; ihdr[9] = (byte)(alpha ? 6 : 2); ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(outp, "IHDR", ihdr, native);
        Chunk(outp, "IDAT", zlib, native);
        Chunk(outp, "IEND", new byte[0], native);
        return outp.ToArray();
    }

    /// <summary>The managed twin of haf_png_pack: the rows of Encode (a zero filter byte, then RGB or RGBA per pixel).</summary>
    internal static void PackManaged(byte[] rgba, int width, int height, bool alpha, byte[] raw)
    {
        int pos = 0;
        for (int y = 0; y < height; y++)
        {
            raw[pos++] = 0;
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                raw[pos++] = rgba[i]; raw[pos++] = rgba[i + 1]; raw[pos++] = rgba[i + 2];
                if (alpha) raw[pos++] = rgba[i + 3];
            }
        }
    }

    static void Chunk(Stream s, string type, byte[] data, bool native)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len, 0, 4);
        var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t, 0, 4);
        s.Write(data, 0, data.Length);
        uint crc = Crc32(t, 0, 4, 0xFFFFFFFFu, native); crc = Crc32(data, 0, data.Length, crc, native) ^ 0xFFFFFFFFu;
        var c = new byte[4]; WriteBE(c, 0, unchecked((int)crc)); s.Write(c, 0, 4);
    }

    static int BE(byte[] d, int p) => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
    static void WriteBE(byte[] d, int p, int v) { d[p] = (byte)(v >> 24); d[p + 1] = (byte)(v >> 16); d[p + 2] = (byte)(v >> 8); d[p + 3] = (byte)v; }

    static uint[] crcTable;
    /// <summary>The running CRC-32 state over d[at, at + n) (starts 0xFFFFFFFF; the caller inverts the end).</summary>
    internal static uint Crc32(byte[] d, int at, int n, uint crc, bool native)
    {
        if (native) return HafNativeKernels.Crc32(d, at, n, crc);
        if (crcTable == null)
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
            crcTable = t;
        }
        for (int i = at; i < at + n; i++) crc = crcTable[(crc ^ d[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    internal static uint Adler32(byte[] d, bool native)
    {
        if (native) return HafNativeKernels.Adler32(d, d.Length);
        uint a = 1, b = 0;
        for (int i = 0; i < d.Length; i++) { a = (a + d[i]) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }
}
