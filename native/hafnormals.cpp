// hafnormals.cpp - native port of VehicleProbe.CustomNormals.cs (HAF, editor/): FaceNormals, the fan walk (WalkFans), EncodeCustomShorts and
// DecodeCustomShorts, arithmetic step for step as the C# (all float32, -ffp-contract=off). cosf/sinf are resolved
// from ucrtbase.dll at load, as BlenderTrig does: Blender's own C runtime, never mingw's libm.
#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>
#include <thread>
#include <algorithm>
#include <windows.h>
#define EXPORT extern "C" __declspec(dllexport)

typedef float (__cdecl *F1)(float);
static F1 ucrt_cosf = nullptr, ucrt_sinf = nullptr;
static void LoadTrig()
{
    if (ucrt_cosf) return;
    HMODULE h = LoadLibraryA("ucrtbase.dll");
    ucrt_cosf = (F1)GetProcAddress(h, "cosf"); ucrt_sinf = (F1)GetProcAddress(h, "sinf");
}
// The contract the C# side checks before using any kernel: the matching version, with Blender's C runtime trig resolved;
// -1 = ucrtbase.dll did not give cosf/sinf (then nothing here may run: DecodeCustom would compute with the wrong library).
#define HAF_KERNELS_VERSION 7
EXPORT int haf_kernels_version() { LoadTrig(); return ucrt_cosf && ucrt_sinf ? HAF_KERNELS_VERSION : -1; }

static const float PI2F = (float)(3.14159265358979323846 * 2.0);
static const float LNOR_TRIGO = 1.0f - 1e-4f;
static const float PIF = (float)3.14159265358979323846;

static inline float Sqrtf(float d) { return (float)std::sqrt((double)d); }
static inline float Dot3(float ax, float ay, float az, float bx, float by, float bz) { return (float)((float)((float)(ax * bx) + (float)(ay * by)) + (float)(az * bz)); }
static inline void Normalize3(float& x, float& y, float& z)
{
    float len2 = Dot3(x, y, z, x, y, z);
    if (len2 > 1.0e-35f) { float len = Sqrtf(len2); x = (float)(x / len); y = (float)(y / len); z = (float)(z / len); }
    else { x = 0.f; y = 0.f; z = 0.f; }
}
static inline void NormalizeV3(float& x, float& y, float& z)
{
    float d = Dot3(x, y, z, x, y, z);
    if (d > 1.0e-35f) { d = Sqrtf(d); float s = (float)(1.0f / d); x = (float)(x * s); y = (float)(y * s); z = (float)(z * s); }
    else { x = 0.f; y = 0.f; z = 0.f; }
}
static inline float SafeAcosApprox(float x)
{
    float f = std::fabs(x);
    float m = f < 1.0f ? (float)(1.0f - (float)(1.0f - f)) : 1.0f;
    float t = (float)(m * -0.02164095f);
    t = (float)(0.077980478f + t);
    t = (float)(m * t);
    t = (float)(-0.213300989f + t);
    t = (float)(m * t);
    t = (float)(1.5707963267f + t);
    float a = (float)(Sqrtf((float)(1.0f - m)) * t);
    return x < 0.0f ? (float)(PIF - a) : a;
}
static inline short UnitFloatToShort(float v) { return (short)std::floor((double)(float)((float)(v * 32767.f) + 0.5f)); }

// Match Math.Max(-1, Math.Min(1, v)), including NaN: std::min(1, NaN) returns 1 instead.
// Preserve a NaN component so Normalize3 selects the managed twin's geometry-normal fallback.
static inline float ClampNormal(float v) { return v < -1.f ? -1.f : v > 1.f ? 1.f : v; }

struct FanSpace { float lx, ly, lz, rx, ry, rz, ox, oy, oz, refAlpha, refBeta; float fnx, fny, fnz; };

static FanSpace DefineSpace(float lx, float ly, float lz, float vrx, float vry, float vrz, float vox, float voy, float voz, const std::vector<const float*>& edgeVectors)
{
    FanSpace s; std::memset(&s, 0, sizeof s);
    float dtpRef = Dot3(vrx, vry, vrz, lx, ly, lz), dtpOther = Dot3(vox, voy, voz, lx, ly, lz);
    if (std::fabs(dtpRef) >= LNOR_TRIGO || std::fabs(dtpOther) >= LNOR_TRIGO) { s.refAlpha = s.refBeta = 0.f; return s; }
    s.lx = lx; s.ly = ly; s.lz = lz;
    if (!edgeVectors.empty())
    {
        float alpha = 0.f;
        for (const float* e : edgeVectors) alpha = (float)(alpha + SafeAcosApprox(Dot3(e[0], e[1], e[2], lx, ly, lz)));
        s.refAlpha = (float)(alpha / (float)edgeVectors.size());
    }
    else s.refAlpha = (float)((float)(SafeAcosApprox(Dot3(vrx, vry, vrz, lx, ly, lz)) + SafeAcosApprox(Dot3(vox, voy, voz, lx, ly, lz))) / 2.0f);
    float rx = (float)(vrx - (float)(lx * dtpRef)), ry = (float)(vry - (float)(ly * dtpRef)), rz = (float)(vrz - (float)(lz * dtpRef));
    Normalize3(rx, ry, rz);
    s.rx = rx; s.ry = ry; s.rz = rz;
    float ox = (float)((float)(ly * rz) - (float)(lz * ry)), oy = (float)((float)(lz * rx) - (float)(lx * rz)), oz = (float)((float)(lx * ry) - (float)(ly * rx));
    Normalize3(ox, oy, oz);
    s.ox = ox; s.oy = oy; s.oz = oz;
    float px = (float)(vox - (float)(lx * dtpOther)), py = (float)(voy - (float)(ly * dtpOther)), pz = (float)(voz - (float)(lz * dtpOther));
    Normalize3(px, py, pz);
    float dtp = Dot3(rx, ry, rz, px, py, pz);
    if (dtp < LNOR_TRIGO)
    {
        float beta = SafeAcosApprox(dtp);
        s.refBeta = Dot3(ox, oy, oz, px, py, pz) < 0.0f ? (float)(PI2F - beta) : beta;
    }
    else s.refBeta = PI2F;
    return s;
}

static void EncodeCustom(const FanSpace& s, float cx, float cy, float cz, short& d0, short& d1)
{
    d0 = 0; d1 = 0;
    if ((cx == 0.f && cy == 0.f && cz == 0.f) || (std::fabs((float)(s.lx - cx)) <= 1e-4f && std::fabs((float)(s.ly - cy)) <= 1e-4f && std::fabs((float)(s.lz - cz)) <= 1e-4f)) return;
    float cosAlpha = Dot3(s.lx, s.ly, s.lz, cx, cy, cz);
    float alpha = SafeAcosApprox(cosAlpha);
    if (alpha > s.refAlpha) d0 = UnitFloatToShort((float)(-(float)(PI2F - alpha) / (float)(PI2F - s.refAlpha)));
    else d0 = UnitFloatToShort((float)(alpha / s.refAlpha));
    float nc = -cosAlpha;
    float vx = (float)((float)(s.lx * nc) + cx), vy = (float)((float)(s.ly * nc) + cy), vz = (float)((float)(s.lz * nc) + cz);
    Normalize3(vx, vy, vz);
    float cosBeta = Dot3(s.rx, s.ry, s.rz, vx, vy, vz);
    if (cosBeta < LNOR_TRIGO)
    {
        float beta = SafeAcosApprox(cosBeta);
        if (Dot3(s.ox, s.oy, s.oz, vx, vy, vz) < 0.0f) beta = (float)(PI2F - beta);
        if (beta > s.refBeta) d1 = UnitFloatToShort((float)(-(float)(PI2F - beta) / (float)(PI2F - s.refBeta)));
        else d1 = UnitFloatToShort((float)(beta / s.refBeta));
    }
    else d1 = 0;
}

static void DecodeCustom(const FanSpace& s, short d0, short d1, float& x, float& y, float& z)
{
    if (d0 == 0 || s.refAlpha == 0.0f || s.refBeta == 0.0f) { x = s.lx; y = s.ly; z = s.lz; return; }
    float alphafac = (float)((float)d0 / 32767.f);
    float alpha = (float)((alphafac > 0.0f ? s.refAlpha : (float)(PI2F - s.refAlpha)) * alphafac);
    float betafac = (float)((float)d1 / 32767.f);
    float ca = ucrt_cosf(alpha);
    x = (float)(s.lx * ca); y = (float)(s.ly * ca); z = (float)(s.lz * ca);
    if (betafac == 0.0f)
    {
        float sa = ucrt_sinf(alpha);
        x = (float)(x + (float)(s.rx * sa)); y = (float)(y + (float)(s.ry * sa)); z = (float)(z + (float)(s.rz * sa));
    }
    else
    {
        float sinalpha = ucrt_sinf(alpha);
        float beta = (float)((betafac > 0.0f ? s.refBeta : (float)(PI2F - s.refBeta)) * betafac);
        float f1 = (float)(sinalpha * ucrt_cosf(beta)), f2 = (float)(sinalpha * ucrt_sinf(beta));
        x = (float)(x + (float)(s.rx * f1)); y = (float)(y + (float)(s.ry * f1)); z = (float)(z + (float)(s.rz * f1));
        x = (float)(x + (float)(s.ox * f2)); y = (float)(y + (float)(s.oy * f2)); z = (float)(z + (float)(s.oz * f2));
    }
}

template <class F> static void PerRange(int n, int workers, int threshold, F f)
{
    if (n >= threshold && workers > 1)
    {
        std::vector<std::thread> th;
        for (int b = 0; b < workers; b++) th.emplace_back([=] { f((int)((int64_t)n * b / workers), (int)((int64_t)n * (b + 1) / workers)); });
        for (auto& t : th) t.join();
    }
    else f(0, n);
}

static int Workers(int threads) { int hw = (int)std::thread::hardware_concurrency(); if (hw <= 0) hw = 1; return std::min(8, threads > 0 ? threads : hw); }

// Mesh::face_normals: Newell's sum, normalize_v3, (0,0,1) for a zero one.
EXPORT void haf_face_normals(const float* P, const int* tris, int nf, float* FN, int threads)
{
    PerRange(nf, Workers(threads), 16384, [=](int first, int last) {
        for (int f = first; f < last; f++)
        {
            float nx = 0.f, ny = 0.f, nz = 0.f;
            int prev = tris[f * 3 + 2] * 3;
            for (int k = 0; k < 3; k++)
            {
                int curr = tris[f * 3 + k] * 3;
                nx = (float)(nx + (float)((float)(P[prev + 1] - P[curr + 1]) * (float)(P[prev + 2] + P[curr + 2])));
                ny = (float)(ny + (float)((float)(P[prev + 2] - P[curr + 2]) * (float)(P[prev] + P[curr])));
                nz = (float)(nz + (float)((float)(P[prev] - P[curr]) * (float)(P[prev + 1] + P[curr + 1])));
                prev = curr;
            }
            NormalizeV3(nx, ny, nz);
            if (nx == 0.f && ny == 0.f && nz == 0.f) nz = 1.0f;
            FN[f * 3] = nx; FN[f * 3 + 1] = ny; FN[f * 3 + 2] = nz;
        }
    });
}

// Blender's normals_calc_verts for a mesh without file normals (BlenderVertexNormals with N == null), serial as the C#.
static void TrueVertexNormals(const float* P, int nv, const int* tris, int nf, float* res, int threads)
{
    std::vector<float> FN0(nf * 3); haf_face_normals(P, tris, nf, FN0.data(), threads);
    std::vector<int> offsets(nv + 1, 0);
    for (int c = 0; c < nf * 3; c++) offsets[tris[c] + 1]++;
    for (int v = 0; v < nv; v++) offsets[v + 1] += offsets[v];
    std::vector<int> fill(offsets); std::vector<int> vf(nf * 3);
    for (int c = 0; c < nf * 3; c++) vf[fill[tris[c]]++] = c / 3;
    for (int v = 0; v < nv; v++)
    {
        float sx = 0.f, sy = 0.f, sz = 0.f;
        if (offsets[v] == offsets[v + 1]) { sx = P[v * 3]; sy = P[v * 3 + 1]; sz = P[v * 3 + 2]; }
        else for (int at = offsets[v]; at < offsets[v + 1]; at++)
        {
            int f = vf[at];
            int k = tris[f * 3] == v ? 0 : tris[f * 3 + 1] == v ? 1 : 2;
            int vp = tris[f * 3 + (k + 2) % 3], vn = tris[f * 3 + (k + 1) % 3];
            float pdx = (float)(P[vp * 3] - P[v * 3]), pdy = (float)(P[vp * 3 + 1] - P[v * 3 + 1]), pdz = (float)(P[vp * 3 + 2] - P[v * 3 + 2]);
            float ndx = (float)(P[vn * 3] - P[v * 3]), ndy = (float)(P[vn * 3 + 1] - P[v * 3 + 1]), ndz = (float)(P[vn * 3 + 2] - P[v * 3 + 2]);
            Normalize3(pdx, pdy, pdz); Normalize3(ndx, ndy, ndz);
            float factor = SafeAcosApprox(Dot3(pdx, pdy, pdz, ndx, ndy, ndz));
            sx = (float)(sx + (float)(FN0[f * 3] * factor)); sy = (float)(sy + (float)(FN0[f * 3 + 1] * factor)); sz = (float)(sz + (float)(FN0[f * 3 + 2] * factor));
        }
        Normalize3(sx, sy, sz);
        res[v * 3] = sx; res[v * 3 + 1] = sy; res[v * 3 + 2] = sz;
    }
}

struct CornerInfo { int face, corner, cornerPrev, cornerNext, vertPrev, vertNext, edgePrev, edgeNext; };

// WalkFans without sharp edges and without the bmesh flavour (the probe's path). onFan(v, infos, fan, space), onVertex(v, infos), onLone(v).
template <class OnFan, class OnVertex, class OnLone>
static void WalkFans(const float* P, int nv, const int* tris, int nf, const uint8_t* sharpFace, OnFan onFan, OnVertex onVertex, OnLone onLone, bool haveVertex, bool haveLone, int threads)
{
    std::vector<float> FN(nf * 3); haf_face_normals(P, tris, nf, FN.data(), threads);
    std::vector<int> offsets(nv + 1, 0);
    for (int c = 0; c < nf * 3; c++) offsets[tris[c] + 1]++;
    for (int v = 0; v < nv; v++) offsets[v + 1] += offsets[v];
    std::vector<int> fill(offsets); std::vector<int> vertFaces(nf * 3);
    for (int c = 0; c < nf * 3; c++) vertFaces[fill[tris[c]]++] = c / 3;
    auto Range = [&](int first, int last)
    {
        std::vector<CornerInfo> infos; std::vector<int> localEdgeVerts, fan, rotated;
        std::vector<const float*> fanEdgeDirs;
        std::vector<int> kind, c1, c2; std::vector<uint8_t> towards, visited; std::vector<float> edgeDirs;
        for (int v = first; v < last; v++)
        {
            if (offsets[v] == offsets[v + 1]) { if (haveLone) onLone(v); continue; }
            infos.clear();
            for (int at = offsets[v]; at < offsets[v + 1]; at++)
            {
                int f = vertFaces[at];
                int k = tris[f * 3] == v ? 0 : tris[f * 3 + 1] == v ? 1 : 2;
                CornerInfo ci; ci.face = f; ci.corner = f * 3 + k; ci.cornerPrev = f * 3 + (k + 2) % 3; ci.cornerNext = f * 3 + (k + 1) % 3;
                ci.vertPrev = tris[ci.cornerPrev]; ci.vertNext = tris[ci.cornerNext]; ci.edgePrev = ci.edgeNext = -1;
                infos.push_back(ci);
            }
            localEdgeVerts.clear();
            auto IndexOf = [&](int o) { for (size_t i = 0; i < localEdgeVerts.size(); i++) if (localEdgeVerts[i] == o) return (int)i; return -1; };
            for (auto& ci : infos)
            {
                ci.edgePrev = IndexOf(ci.vertPrev); if (ci.edgePrev < 0) { localEdgeVerts.push_back(ci.vertPrev); ci.edgePrev = (int)localEdgeVerts.size() - 1; }
                ci.edgeNext = IndexOf(ci.vertNext); if (ci.edgeNext < 0) { localEdgeVerts.push_back(ci.vertNext); ci.edgeNext = (int)localEdgeVerts.size() - 1; }
            }
            int ne = (int)localEdgeVerts.size();
            if (ne > (int)kind.size()) { kind.resize(ne); c1.resize(ne); c2.resize(ne); towards.resize(ne); edgeDirs.resize(ne * 3); }
            std::fill(kind.begin(), kind.begin() + ne, 0);
            auto AddCornerToEdge = [&](int e, int lc, bool windingTowardsVert)
            {
                if (kind[e] == 0) { kind[e] = 1; c1[e] = lc; towards[e] = windingTowardsVert; }
                else if (kind[e] == 1) { if ((towards[e] != 0) == windingTowardsVert) kind[e] = 3; else { kind[e] = 2; c2[e] = lc; } }
                else kind[e] = 3;
            };
            for (int lc = 0; lc < (int)infos.size(); lc++)
            {
                const auto& ci = infos[lc];
                if (sharpFace[ci.face]) { kind[ci.edgePrev] = 3; kind[ci.edgeNext] = 3; continue; }
                AddCornerToEdge(ci.edgePrev, lc, true);
                AddCornerToEdge(ci.edgeNext, lc, false);
            }
            for (int e = 0; e < ne; e++)
            {
                int o = localEdgeVerts[e];
                float dx = (float)(P[o * 3] - P[v * 3]), dy = (float)(P[o * 3 + 1] - P[v * 3 + 1]), dz = (float)(P[o * 3 + 2] - P[v * 3 + 2]);
                Normalize3(dx, dy, dz);
                edgeDirs[e * 3] = dx; edgeDirs[e * 3 + 1] = dy; edgeDirs[e * 3 + 2] = dz;
            }
            if (infos.size() > visited.size()) visited.resize(infos.size());
            std::fill(visited.begin(), visited.begin() + infos.size(), 0);
            int visitedCount = 0, start = 0;
            while (true)
            {
                fan.clear(); fan.push_back(start);
                bool cyclic = false;
                int current = start, le = infos[current].edgeNext;
                while (kind[le] == 2)
                {
                    current = c1[le] == current ? c2[le] : c1[le];
                    if (current == start) { cyclic = true; break; }
                    fan.push_back(current); le = infos[current].edgeNext;
                }
                std::reverse(fan.begin(), fan.end());
                if (cyclic)
                {
                    int best = 0;
                    for (int i = 1; i < (int)fan.size(); i++) if (infos[fan[i]].corner < infos[fan[best]].corner) best = i;
                    rotated.clear(); for (int i = 0; i < (int)fan.size(); i++) rotated.push_back(fan[(best + i) % fan.size()]);
                    fan = rotated;
                }
                else
                {
                    current = start; le = infos[current].edgePrev;
                    while (kind[le] == 2)
                    {
                        current = current == c1[le] ? c2[le] : c1[le];
                        fan.push_back(current); le = infos[current].edgePrev;
                    }
                }
                float fx, fy, fz;
                if (fan.size() == 1) { int f0 = infos[fan[0]].face; fx = FN[f0 * 3]; fy = FN[f0 * 3 + 1]; fz = FN[f0 * 3 + 2]; }
                else
                {
                    fx = fy = fz = 0.f;
                    for (int lc : fan)
                    {
                        const auto& ci = infos[lc]; const float* dp = &edgeDirs[ci.edgePrev * 3]; const float* dn = &edgeDirs[ci.edgeNext * 3];
                        float factor = SafeAcosApprox(Dot3(dp[0], dp[1], dp[2], dn[0], dn[1], dn[2]));
                        fx = (float)(fx + (float)(FN[ci.face * 3] * factor)); fy = (float)(fy + (float)(FN[ci.face * 3 + 1] * factor)); fz = (float)(fz + (float)(FN[ci.face * 3 + 2] * factor));
                    }
                    Normalize3(fx, fy, fz);
                }
                int edgeFirst = infos[fan[0]].edgeNext, edgeLast = infos[fan[fan.size() - 1]].edgePrev;
                fanEdgeDirs.clear();
                if (fan.size() > 1)
                {
                    for (int lc : fan) fanEdgeDirs.push_back(&edgeDirs[infos[lc].edgeNext * 3]);
                    if (edgeLast != edgeFirst) fanEdgeDirs.push_back(&edgeDirs[edgeLast * 3]);
                }
                const float* ef = &edgeDirs[edgeFirst * 3]; const float* el = &edgeDirs[edgeLast * 3];
                FanSpace space = DefineSpace(fx, fy, fz, ef[0], ef[1], ef[2], el[0], el[1], el[2], fanEdgeDirs);
                space.fnx = fx; space.fny = fy; space.fnz = fz;
                onFan(v, infos, fan, space);
                visitedCount += (int)fan.size();
                if (visitedCount == (int)infos.size()) break;
                for (int lc : fan) visited[lc] = 1;
                while (visited[start]) start++;
            }
            if (haveVertex) onVertex(v, infos);
        }
    };
    int hw = (int)std::thread::hardware_concurrency(); if (hw <= 0) hw = 1;
    int workers = std::min(nv, std::min(8, threads > 0 ? threads : hw));
    if (workers <= 1 || (threads == 0 && (nv < 16384 || nf * 3 < 49152))) Range(0, nv);
    else
    {
        std::vector<std::thread> th;
        for (int b = 0; b < workers; b++) th.emplace_back([=] { Range((int)((int64_t)nv * b / workers), (int)((int64_t)nv * (b + 1) / workers)); });
        for (auto& t : th) t.join();
    }
}

// EncodeCustomShorts: N holds 3 floats per vertex (NaN x for a vertex without a file normal); d0/d1 hold 3 nf shorts.
EXPORT void haf_encode_custom(const float* P, int nv, const int* tris, int nf, const float* N, const uint8_t* sharpFace, short* d0, short* d1, int threads)
{
    LoadTrig();
    std::vector<float> C(nv * 3); std::vector<float> trueNormals; bool haveTrue = false;
    for (int v = 0; v < nv; v++)
    {
        bool none = std::isnan(N[v * 3]);
        float x = none ? 0.f : N[v * 3], y = none ? 0.f : N[v * 3 + 1], z = none ? 0.f : N[v * 3 + 2];
        x = ClampNormal(x); y = ClampNormal(y); z = ClampNormal(z);
        Normalize3(x, y, z);
        if (x == 0.f && y == 0.f && z == 0.f)
        {
            if (!haveTrue) { trueNormals.resize(nv * 3); TrueVertexNormals(P, nv, tris, nf, trueNormals.data(), threads); haveTrue = true; }
            x = trueNormals[v * 3]; y = trueNormals[v * 3 + 1]; z = trueNormals[v * 3 + 2];
        }
        C[v * 3] = x; C[v * 3 + 1] = y; C[v * 3 + 2] = z;
    }
    const float* NN = C.data();
    WalkFans(P, nv, tris, nf, sharpFace, [&](int v, const std::vector<CornerInfo>& infos, const std::vector<int>& fan, const FanSpace& space)
    {
        float cx = NN[v * 3], cy = NN[v * 3 + 1], cz = NN[v * 3 + 2];
        if (fan.size() >= 2)
        {
            float ax = 0.f, ay = 0.f, az = 0.f;
            for (size_t i = 0; i < fan.size(); i++) { ax = (float)(ax + cx); ay = (float)(ay + cy); az = (float)(az + cz); }
            float inv = (float)(1.0f / (float)fan.size());
            cx = (float)(ax * inv); cy = (float)(ay * inv); cz = (float)(az * inv);
        }
        short e0, e1; EncodeCustom(space, cx, cy, cz, e0, e1);
        for (int lc : fan) { int c = infos[lc].corner; d0[c] = e0; d1[c] = e1; }
    }, [](int, const std::vector<CornerInfo>&) {}, [](int) {}, false, false, threads);
}

// DecodeCustomShorts without sharp edges: result holds 3 floats per vertex.
EXPORT void haf_decode_custom(const float* P, int nv, const int* tris, int nf, const uint8_t* sharpFace, const short* d0, const short* d1, float* result, int threads)
{
    LoadTrig();
    std::vector<float> cornerNormal((size_t)nf * 9);
    WalkFans(P, nv, tris, nf, sharpFace, [&](int v, const std::vector<CornerInfo>& infos, const std::vector<int>& fan, const FanSpace& space)
    {
        int s0 = 0, s1 = 0;
        for (int lc : fan) { int c = infos[lc].corner; s0 += d0[c]; s1 += d1[c]; }
        short a0 = (short)(s0 / (int)fan.size()), a1 = (short)(s1 / (int)fan.size());
        float dx, dy, dz; DecodeCustom(space, a0, a1, dx, dy, dz);
        for (int lc : fan) { int c = infos[lc].corner; cornerNormal[c * 3] = dx; cornerNormal[c * 3 + 1] = dy; cornerNormal[c * 3 + 2] = dz; }
    }, [&](int v, const std::vector<CornerInfo>& infos)
    {
        float sx = 0.f, sy = 0.f, sz = 0.f;
        for (const auto& ci : infos)
        {
            float pdx = (float)(P[ci.vertPrev * 3] - P[v * 3]), pdy = (float)(P[ci.vertPrev * 3 + 1] - P[v * 3 + 1]), pdz = (float)(P[ci.vertPrev * 3 + 2] - P[v * 3 + 2]);
            float ndx = (float)(P[ci.vertNext * 3] - P[v * 3]), ndy = (float)(P[ci.vertNext * 3 + 1] - P[v * 3 + 1]), ndz = (float)(P[ci.vertNext * 3 + 2] - P[v * 3 + 2]);
            Normalize3(pdx, pdy, pdz); Normalize3(ndx, ndy, ndz);
            float factor = SafeAcosApprox(Dot3(pdx, pdy, pdz, ndx, ndy, ndz));
            int c = ci.corner;
            sx = (float)(sx + (float)(cornerNormal[c * 3] * factor)); sy = (float)(sy + (float)(cornerNormal[c * 3 + 1] * factor)); sz = (float)(sz + (float)(cornerNormal[c * 3 + 2] * factor));
        }
        Normalize3(sx, sy, sz);
        result[v * 3] = sx; result[v * 3 + 1] = sy; result[v * 3 + 2] = sz;
    }, [&](int v)
    {
        float px = P[v * 3], py = P[v * 3 + 1], pz = P[v * 3 + 2]; Normalize3(px, py, pz);
        result[v * 3] = px; result[v * 3 + 1] = py; result[v * 3 + 2] = pz;
    }, true, true, threads);
}
