// hafpng.cpp - the PNG codec's kernels (editor/PngCodec.cs, 2026-11-02): the scanline unfilter and pixel expansion of
// Decode, the row packing of Encode, the two checksums, and the zlib compression of Encode through libdeflate
// (native/libdeflate, vendored). Measured on the Bradley's 4096x4096 textures in Unity's Mono before this: a decode took
// 0.8 s of which the zlib inflate (native already, through DeflateStream) was 0.13 s and the managed unfilter + expansion
// the rest; an encode took 1.2-2.3 s, nearly all of it Mono's DeflateStream, whose compression level is fixed whatever the
// caller asks; the managed Adler-32 over a 48 MB buffer took 0.17 s and runs twice per texture. Every function here is
// the managed twin step for step in byte arithmetic (PngCodec.cs: RowsManaged, PackManaged, Adler32, Crc32), except the
// compressor: its bytes are libdeflate's, not DeflateStream's - a PNG's bytes were never held to anything (the drills
// compare pixels), only its pixels, and any inflater reads both.
#include <cstdint>
#include <cstring>
#include <vector>
#include <windows.h>
#include "libdeflate/libdeflate.h"
#define EXPORT extern "C" __declspec(dllexport)

namespace
{
    // SHA-256 through Windows CNG's one-shot BCryptHash (Windows 10 1703+; SHA extensions where the CPU has them): the image
    // key FbxModel derives from every exported image's bytes (FbxMaterials.ImageIndex), which Unity's Mono computes in managed
    // code at ~140 MB/s. Resolved at first use, as hafnormals.cpp resolves the C runtime's trig; absent, the caller keeps
    // the managed hash.
    typedef LONG (WINAPI* BCryptHashFn)(void* hAlgorithm, unsigned char* pbSecret, ULONG cbSecret, unsigned char* pbInput, ULONG cbInput, unsigned char* pbOutput, ULONG cbOutput);
    BCryptHashFn Sha256Function()
    {
        // C++11 static initialization publishes the pointer only after resolution completes. A separate
        // tried flag let concurrent callers observe "tried" with a null pointer and race on both fields.
        static BCryptHashFn function = []() -> BCryptHashFn
        {
            HMODULE module = LoadLibraryW(L"bcrypt.dll");
            return module != nullptr ? (BCryptHashFn)(void*)GetProcAddress(module, "BCryptHash") : nullptr;
        }();
        return function;
    }
    const void* SHA256_PSEUDO_HANDLE = (const void*)(uintptr_t)0x00000041;   // BCRYPT_SHA256_ALG_HANDLE
}

// SHA-256 of n bytes into out (32 bytes). Returns 0, -1 when CNG's one-shot hash is not there, else CNG's status.
EXPORT int haf_sha256(const uint8_t* d, int64_t n, uint8_t* out)
{
    if (n < 0 || n > 0xFFFFFFFFLL) return -1;
    BCryptHashFn bcryptHash = Sha256Function();
    if (bcryptHash == nullptr) return -1;
    LONG s = bcryptHash((void*)SHA256_PSEUDO_HANDLE, nullptr, 0, (unsigned char*)d, (ULONG)n, out, 32);
    return s == 0 ? 0 : (int)s;
}

namespace
{
    inline int Abs(int v) { return v < 0 ? -v : v; }

    // PngCodec.Unfilter: in place over `line` (n bytes), `prev` the row above already unfiltered (zeros for the first row)
    void Unfilter(int filter, uint8_t* line, const uint8_t* prev, int64_t n, int bpp)
    {
        switch (filter)
        {
            case 0: break;
            case 1: for (int64_t i = bpp; i < n; i++) line[i] = (uint8_t)(line[i] + line[i - bpp]); break;
            case 2: for (int64_t i = 0; i < n; i++) line[i] = (uint8_t)(line[i] + prev[i]); break;
            case 3: for (int64_t i = 0; i < n; i++) line[i] = (uint8_t)(line[i] + (((i >= bpp ? line[i - bpp] : 0) + prev[i]) >> 1)); break;
            case 4:
                for (int64_t i = 0; i < n; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int pp = a + b - c, pa = Abs(pp - a), pb = Abs(pp - b), pc = Abs(pp - c);
                    int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    line[i] = (uint8_t)(line[i] + pred);
                }
                break;
            default: break;   // the caller rejected it
        }
    }

    inline uint8_t Pal(const uint8_t* palette, int palLen, int i) { return i < palLen ? palette[i] : (uint8_t)0; }
}

// Decode's rows: raw holds height rows of (1 filter byte + stride bytes), unfiltered IN PLACE here; out gets width*height*4
// bytes (RGBA, rows top-down) as PngCodec.RowsManaged writes them. bits 1/2/4/8, ct 0/2/3/4/6, samples and bpp as the caller
// derived them, palette/trns may be null (length 0). Returns 0, or y + 1 for the first row whose filter byte exceeds 4
// (the rows before it are unfiltered, as the managed twin leaves them when it returns null).
EXPORT int haf_png_rows(uint8_t* raw, int64_t rawLen, int width, int height, int bits, int ct, int samples, int bpp, int64_t stride,
                        const uint8_t* palette, int palLen, const uint8_t* trns, int trnsLen, uint8_t* out)
{
    (void)rawLen;
    std::vector<uint8_t> zero((size_t)stride, 0);
    int64_t pos = 0;
    for (int y = 0; y < height; y++)
    {
        int filter = raw[pos++];
        if (filter > 4) return y + 1;
        uint8_t* line = raw + pos;
        const uint8_t* prev = y == 0 ? zero.data() : raw + pos - stride - 1;
        pos += stride;
        Unfilter(filter, line, prev, stride, bpp);
        uint8_t* o = out + (int64_t)y * width * 4;
        for (int x = 0; x < width; x++)
        {
            uint8_t r, g, b, a = 255;
            if (bits == 8)
            {
                int64_t i = (int64_t)x * samples;
                if (ct == 0) { r = g = b = line[i]; if (trns != nullptr && trnsLen >= 2 && line[i] == trns[1]) a = 0; }
                else if (ct == 2) { r = line[i]; g = line[i + 1]; b = line[i + 2]; if (trns != nullptr && trnsLen >= 6 && r == trns[1] && g == trns[3] && b == trns[5]) a = 0; }
                else if (ct == 3) { int k = line[i]; r = Pal(palette, palLen, 3 * k); g = Pal(palette, palLen, 3 * k + 1); b = Pal(palette, palLen, 3 * k + 2); if (trns != nullptr) a = k < trnsLen ? trns[k] : (uint8_t)255; }
                else if (ct == 4) { r = g = b = line[i]; a = line[i + 1]; }
                else { r = line[i]; g = line[i + 1]; b = line[i + 2]; a = line[i + 3]; }
            }
            else
            {
                int64_t bitPos = (int64_t)x * bits; int v = (line[bitPos >> 3] >> (8 - bits - (int)(bitPos & 7))) & ((1 << bits) - 1);
                if (ct == 0) { uint8_t s = (uint8_t)(v * 255 / ((1 << bits) - 1)); r = g = b = s; if (trns != nullptr && trnsLen >= 2 && v == trns[1]) a = 0; }
                else { r = Pal(palette, palLen, 3 * v); g = Pal(palette, palLen, 3 * v + 1); b = Pal(palette, palLen, 3 * v + 2); if (trns != nullptr) a = v < trnsLen ? trns[v] : (uint8_t)255; }
            }
            o[4 * x] = r; o[4 * x + 1] = g; o[4 * x + 2] = b; o[4 * x + 3] = a;
        }
    }
    return 0;
}

// Encode's rows: rgba holds width*height*4 bytes; raw gets height rows of (filter byte 0 + width*samples bytes), RGB or RGBA.
EXPORT void haf_png_pack(const uint8_t* rgba, int width, int height, int alpha, uint8_t* raw)
{
    int64_t pos = 0;
    for (int y = 0; y < height; y++)
    {
        raw[pos++] = 0;
        for (int x = 0; x < width; x++)
        {
            int64_t i = ((int64_t)y * width + x) * 4;
            raw[pos++] = rgba[i]; raw[pos++] = rgba[i + 1]; raw[pos++] = rgba[i + 2];
            if (alpha) raw[pos++] = rgba[i + 3];
        }
    }
}

// FbxMaterials' composition fills (the exporter's metallic-roughness and alpha images): channel dst of n float pixels from
// channel src of the RGBA bytes as byte * (1/255) (bw == 0), or the luminance 0.2989 r + 0.5870 g + 0.1140 b times the alpha
// (bw != 0), each operation rounded to float32 as the C# casts it (-ffp-contract=off, SSE: no fused multiply-add, no excess
// precision). The other three channels of a pixel are left alone.
EXPORT void haf_png_fill(const uint8_t* px, int64_t n, int dst, int src, int bw, float* outF)
{
    const float k = 1.0f / 255.0f;
    if (!bw) { for (int64_t p = 0; p < n; p++) outF[4 * p + dst] = (float)px[4 * p + src] * k; return; }
    const float c1 = 0.2989f, c2 = 0.5870f, c3 = 0.1140f;
    for (int64_t p = 0; p < n; p++)
    {
        float r = (float)px[4 * p] * k, g = (float)px[4 * p + 1] * k, b = (float)px[4 * p + 2] * k, a = (float)px[4 * p + 3] * k;
        float rc = r * c1, gc = g * c2, bc = b * c3;
        float lum = (rc + gc) + bc;
        outF[4 * p + dst] = lum * a;
    }
}

// FbxMaterials.UnitFloatToUchar over n floats: unit_float_to_uchar_clamp - 0 at or below 0, 255 above 1 - 0.5/255, else
// (uchar)(255 v + 0.5) truncated.
EXPORT void haf_unit_to_uchar(const float* f, int64_t n, uint8_t* out)
{
    const float thr = 1.0f - (0.5f / 255.0f);
    for (int64_t i = 0; i < n; i++)
    {
        float v = f[i];
        if (v <= 0.0f) out[i] = 0;
        else if (v > thr) out[i] = 255;
        else { float s = 255.0f * v; float t = s + 0.5f; out[i] = (uint8_t)t; }
    }
}

// Adler-32 of n bytes (PngCodec.Adler32: a = 1, b = 0, per byte mod 65521).
EXPORT uint32_t haf_adler32(const uint8_t* d, int64_t n) { return libdeflate_adler32(1, d, (size_t)n); }

// PngCodec.Crc32's running state (starts 0xFFFFFFFF, the caller inverts at the end) over d[at, at + n): the standard
// CRC-32 is the inverted state, which is what libdeflate_crc32 takes and gives.
EXPORT uint32_t haf_crc32(const uint8_t* d, int64_t at, int64_t n, uint32_t running) { return ~libdeflate_crc32(~running, d + at, (size_t)n); }

// The zlib stream (header, deflate, Adler-32) of n bytes at libdeflate's `level` (1..12), into out (cap bytes, at least
// haf_zlib_bound(n)). Returns the stream's length, 0 when it did not fit or the level is out of range.
EXPORT int64_t haf_zlib_bound(int64_t n) { return (int64_t)libdeflate_zlib_compress_bound(nullptr, (size_t)n); }
EXPORT int64_t haf_zlib_compress(const uint8_t* in, int64_t n, int level, uint8_t* out, int64_t cap)
{
    if (level < 1 || level > 12) return 0;
    libdeflate_compressor* c = libdeflate_alloc_compressor(level);
    if (c == nullptr) return 0;
    size_t written = libdeflate_zlib_compress(c, in, (size_t)n, out, (size_t)cap);
    libdeflate_free_compressor(c);
    return (int64_t)written;
}
