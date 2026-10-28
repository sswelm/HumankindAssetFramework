// hafbmesh.cpp - native ports of the Vehicle Lab reduce stage's managed steps (editor/BlenderVehicleRig.Stage2.cs, 2026-10-27),
// each arithmetic step as the C# (float32, -ffp-contract=off; asinf and cosf from ucrtbase.dll as BlenderTrig):
//   haf_bmesh_build          BM_mesh_bm_from_me on a stage mesh (FromMeshData): the vertices, the edges in the mesh's order (a
//                            pair listed twice keeps the LAST edge for the lookup, as the managed dictionary did), the triangles
//                            with their corners' edges, the face normals when asked.
//   haf_bmesh_vert_material  the weld grouping's per-vertex material: the material every corner of the vertex shares, -1 when
//                            they differ, -2 for a vertex without corners.
//   haf_find_doubles         bmesh_find_doubles (BlenderWeld.FindDoubles): the BLI_kdtree balance and range search, the cluster's
//                            target (deduplicate_target_calc_fn), one target index per listed vertex (-1: none).
//   haf_dissolve_limit       bmesh.ops.dissolve_limit over the chunk jobs (BlenderDissolve.DissolveLimit then the NORMALS_CALC
//                            finish, per job, the Python wrapper's "has been removed" check before each), on a COPY of the BMesh
//                            that may grow (a face join creates an n-gon and its corners): the result comes back through a handle,
//                            the free lists along with it (the managed pool replays them: last freed, first reused).
// The managed twins (BlenderVehicleRig.Stage2.FromMeshData, BlenderWeld.FindDoubles, BlenderDissolve) remain the specification,
// the fallback and the oracle: tools/vehiclerig_drill.sh holds this DLL to Blender's own stage dumps on every saved recipe,
// Tests/NativeBMeshTests.cs to the twins.
#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>
#include <algorithm>
#include <utility>
#include <windows.h>
#define EXPORT extern "C" __declspec(dllexport)

namespace {

const int None = -1;
const uint8_t FlagSelect = 1, FlagSmooth = 8;
const float CostInvalid = 3.402823466e+38f;   // float.MaxValue
const float PiF = (float)3.14159265358979323846;
const float Deg90 = (float)(90.0f * (float)(3.14159265358979323846 / 180.0));
const float UnitToAngle = Deg90, AngleToUnit = (float)(1.0f / Deg90);
const float FltEpsilon = 1.1920929e-7f;

typedef float (__cdecl *F1)(float);
F1 ucrt_asinf = nullptr, ucrt_cosf = nullptr;
void LoadTrig()
{
    // C++11 local-static initialization waits for the entire lookup on every worker. Publishing a "tried" flag first
    // let concurrent kernel callers observe null function pointers while the first worker was still resolving them.
    static const bool initialized = [] {
        HMODULE h = LoadLibraryA("ucrtbase.dll");
        if (h) { ucrt_asinf = (F1)GetProcAddress(h, "asinf"); ucrt_cosf = (F1)GetProcAddress(h, "cosf"); }
        return true;
    }();
    (void)initialized;
}

inline float Sqrtf(float d) { return (float)std::sqrt((double)d); }
inline float Dot3(float ax, float ay, float az, float bx, float by, float bz) { return (float)((float)((float)(ax * bx) + (float)(ay * by)) + (float)(az * bz)); }
float NormalizeV3(float& x, float& y, float& z)
{
    float d = Dot3(x, y, z, x, y, z);
    if (d > 1.0e-35f) { d = Sqrtf(d); float f = (float)(1.0f / d); x = (float)(x * f); y = (float)(y * f); z = (float)(z * f); }
    else { x = y = z = 0.f; d = 0.f; }
    return d;
}
void NormalTri(const float* P, int v1, int v2, int v3, float& nx, float& ny, float& nz)
{
    float n1x = (float)(P[3 * v1] - P[3 * v2]), n1y = (float)(P[3 * v1 + 1] - P[3 * v2 + 1]), n1z = (float)(P[3 * v1 + 2] - P[3 * v2 + 2]);
    float n2x = (float)(P[3 * v2] - P[3 * v3]), n2y = (float)(P[3 * v2 + 1] - P[3 * v3 + 1]), n2z = (float)(P[3 * v2 + 2] - P[3 * v3 + 2]);
    nx = (float)((float)(n1y * n2z) - (float)(n1z * n2y));
    ny = (float)((float)(n1z * n2x) - (float)(n1x * n2z));
    nz = (float)((float)(n1x * n2y) - (float)(n1y * n2x));
    NormalizeV3(nx, ny, nz);
}
float Saasin(float fac) { return fac <= -1.0f ? (float)(-3.14159265358979323846 / 2) : fac >= 1.0f ? (float)(3.14159265358979323846 / 2) : ucrt_asinf(fac); }
// angle_normalized_v3v3 (BlenderWeld.AngleNormalized)
float AngleNormalized(float ax, float ay, float az, float bx, float by, float bz)
{
    float dot = Dot3(ax, ay, az, bx, by, bz);
    if (dot >= 0.f)
    {
        float dx = (float)(ax - bx), dy = (float)(ay - by), dz = (float)(az - bz);
        float len = Sqrtf((float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz)));
        return (float)(2.0f * Saasin((float)(len / 2.0f)));
    }
    float ex = (float)(ax - (-bx)), ey = (float)(ay - (-by)), ez = (float)(az - (-bz));
    float len2 = Sqrtf((float)((float)((float)(ex * ex) + (float)(ey * ey)) + (float)(ez * ez)));
    return (float)((float)3.14159265358979323846 - (float)(2.0f * Saasin((float)(len2 / 2.0f))));
}
// ortho_basis_v3v3_v3 transposed (BlenderPolyfill.AxisDominantRows) and mul_v2_m3v3 (ProjectV2)
void AxisDominantRows(float nx, float ny, float nz, float* n1, float* n2)
{
    float f = (float)((float)(nx * nx) + (float)(ny * ny));
    if (f > 1.1920929e-7f)
    {
        float d = (float)(1.0f / Sqrtf(f));
        n1[0] = (float)(ny * d); n1[1] = (float)(-nx * d); n1[2] = 0.f;
        n2[0] = (float)(-nz * n1[1]); n2[1] = (float)(nz * n1[0]); n2[2] = (float)((float)(nx * n1[1]) - (float)(ny * n1[0]));
    }
    else { n1[0] = nz < 0.f ? -1.f : 1.f; n1[1] = n1[2] = n2[0] = n2[2] = 0.f; n2[1] = 1.f; }
}
inline void ProjectV2(const float* n1, const float* n2, float x, float y, float z, float& rx, float& ry)
{
    rx = (float)((float)((float)(n1[0] * x) + (float)(n1[1] * y)) + (float)(n1[2] * z));
    ry = (float)((float)((float)(n2[0] * x) + (float)(n2[1] * y)) + (float)(n2[2] * z));
}

// ---------------------------------------------------------------- the BMesh (editor/BMesh.cs) over growable pools with free lists
// BLI_mempool: a freed element goes to the HEAD of the free list and the next creation takes it; with the list empty the next slot.
template <class T> void Grow(std::vector<T>& a, size_t need) { if (a.size() < need) a.resize(std::max(need, a.size() * 2)); }

struct BMV
{
    std::vector<int> VE; std::vector<uint8_t> VAlive; std::vector<float> VCo, VNo; std::vector<uint8_t> VFlag; int VertCount = 0, TotVert = 0;
    std::vector<int> EV1, EV2, EL, EDiskNext, EDiskPrev; std::vector<uint8_t> EAlive, EFlag; int EdgeCount = 0, TotEdge = 0;
    std::vector<int> LV, LE, LF, LNext, LPrev, LRadialNext, LRadialPrev; std::vector<uint8_t> LAlive; int LoopCount = 0, TotLoop = 0;
    std::vector<int> FLFirst, FLen, FMat; std::vector<float> FNo; std::vector<uint8_t> FAlive, FFlag; int FaceCount = 0, TotFace = 0;
    std::vector<int> freeV, freeE, freeL, freeF;   // bottom .. top (the top is the next reused)

    int VertCreate(float x, float y, float z)
    {
        int v; if (!freeV.empty()) { v = freeV.back(); freeV.pop_back(); } else v = VertCount++;
        Grow(VE, VertCount); Grow(VAlive, VertCount); Grow(VCo, 3 * (size_t)VertCount); Grow(VNo, 3 * (size_t)VertCount); Grow(VFlag, VertCount);
        VE[v] = None; VAlive[v] = 1; VCo[3 * v] = x; VCo[3 * v + 1] = y; VCo[3 * v + 2] = z; VNo[3 * v] = VNo[3 * v + 1] = VNo[3 * v + 2] = 0.f; VFlag[v] = 0;
        TotVert++;
        return v;
    }
    int EdgeCreate(int v1, int v2, int example = None)
    {
        int e; if (!freeE.empty()) { e = freeE.back(); freeE.pop_back(); } else e = EdgeCount++;
        Grow(EV1, EdgeCount); Grow(EV2, EdgeCount); Grow(EL, EdgeCount); Grow(EDiskNext, 2 * (size_t)EdgeCount); Grow(EDiskPrev, 2 * (size_t)EdgeCount); Grow(EAlive, EdgeCount); Grow(EFlag, EdgeCount);
        EV1[e] = v1; EV2[e] = v2; EL[e] = None; EFlag[e] = FlagSmooth; EAlive[e] = 1;
        EDiskNext[2 * e] = EDiskPrev[2 * e] = EDiskNext[2 * e + 1] = EDiskPrev[2 * e + 1] = None;
        DiskEdgeAppend(e, v1);
        DiskEdgeAppend(e, v2);
        TotEdge++;
        if (example != None) EFlag[e] = (uint8_t)((EFlag[e] & FlagSelect) | (EFlag[example] & ~FlagSelect));
        return e;
    }
    int LoopCreate(int v, int e, int f)
    {
        int l; if (!freeL.empty()) { l = freeL.back(); freeL.pop_back(); } else l = LoopCount++;
        Grow(LV, LoopCount); Grow(LE, LoopCount); Grow(LF, LoopCount); Grow(LNext, LoopCount); Grow(LPrev, LoopCount); Grow(LRadialNext, LoopCount); Grow(LRadialPrev, LoopCount); Grow(LAlive, LoopCount);
        LV[l] = v; LE[l] = e; LF[l] = f; LNext[l] = LPrev[l] = LRadialNext[l] = LRadialPrev[l] = None; LAlive[l] = 1;
        TotLoop++;
        return l;
    }
    int FaceCreate(const int* verts, const int* edges, int len, int example = None, int* loopSlots = nullptr)
    {
        if (len == 0) return None;
        int f; if (!freeF.empty()) { f = freeF.back(); freeF.pop_back(); } else f = FaceCount++;
        Grow(FLFirst, FaceCount); Grow(FLen, FaceCount); Grow(FMat, FaceCount); Grow(FNo, 3 * (size_t)FaceCount); Grow(FAlive, FaceCount); Grow(FFlag, FaceCount);
        FLen[f] = len; FMat[f] = 0; FNo[3 * f] = FNo[3 * f + 1] = FNo[3 * f + 2] = 0.f; FAlive[f] = 1; FFlag[f] = 0;
        TotFace++;
        int startl = LoopCreate(verts[0], edges[0], f);
        RadialLoopAppend(edges[0], startl);
        FLFirst[f] = startl;
        if (loopSlots) loopSlots[0] = startl;
        int lastl = startl;
        for (int i = 1; i < len; i++)
        {
            int l = LoopCreate(verts[i], edges[i], f);
            RadialLoopAppend(edges[i], l);
            LPrev[l] = lastl; LNext[lastl] = l; lastl = l;
            if (loopSlots) loopSlots[i] = l;
        }
        LPrev[startl] = lastl; LNext[lastl] = startl;
        if (example != None)
        {
            FFlag[f] = (uint8_t)((FFlag[f] & FlagSelect) | (FFlag[example] & ~FlagSelect));
            FNo[3 * f] = FNo[3 * example]; FNo[3 * f + 1] = FNo[3 * example + 1]; FNo[3 * f + 2] = FNo[3 * example + 2];
            FMat[f] = FMat[example];
        }
        return f;
    }

    int DiskLink(int e, int v) const { return 2 * e + (EV1[e] == v ? 0 : 1); }
    int DiskEdgeNext(int e, int v) const { return EDiskNext[DiskLink(e, v)]; }
    void DiskEdgeAppend(int e, int v)
    {
        if (VE[v] == None) { int dl1 = DiskLink(e, v); VE[v] = e; EDiskNext[dl1] = EDiskPrev[dl1] = e; }
        else
        {
            int dl1 = DiskLink(e, v), dl2 = DiskLink(VE[v], v);
            int dl3 = EDiskPrev[dl2] != None ? DiskLink(EDiskPrev[dl2], v) : -1;
            EDiskNext[dl1] = VE[v];
            EDiskPrev[dl1] = EDiskPrev[dl2];
            EDiskPrev[dl2] = e;
            if (dl3 >= 0) EDiskNext[dl3] = e;
        }
    }
    void DiskEdgeRemove(int e, int v)
    {
        int dl1 = DiskLink(e, v);
        if (EDiskPrev[dl1] != None) { int dl2 = DiskLink(EDiskPrev[dl1], v); EDiskNext[dl2] = EDiskNext[dl1]; }
        if (EDiskNext[dl1] != None) { int dl2 = DiskLink(EDiskNext[dl1], v); EDiskPrev[dl2] = EDiskPrev[dl1]; }
        if (VE[v] == e) VE[v] = (e != EDiskNext[dl1]) ? EDiskNext[dl1] : None;
        EDiskNext[dl1] = EDiskPrev[dl1] = None;
    }
    void DiskVertSwap(int e, int vDst, int vSrc)
    {
        if (EV1[e] == vSrc) { EV1[e] = vDst; EDiskNext[2 * e] = EDiskPrev[2 * e] = None; }
        else if (EV2[e] == vSrc) { EV2[e] = vDst; EDiskNext[2 * e + 1] = EDiskPrev[2 * e + 1] = None; }
    }
    void DiskVertReplace(int e, int vDst, int vSrc) { DiskEdgeRemove(e, vSrc); DiskVertSwap(e, vDst, vSrc); DiskEdgeAppend(e, vDst); }
    void RadialLoopAppend(int e, int l)
    {
        if (EL[e] == None) { EL[e] = l; LRadialNext[l] = LRadialPrev[l] = l; }
        else
        {
            LRadialPrev[l] = EL[e];
            LRadialNext[l] = LRadialNext[EL[e]];
            LRadialPrev[LRadialNext[EL[e]]] = l;
            LRadialNext[EL[e]] = l;
            EL[e] = l;
        }
        LE[l] = e;
    }
    void RadialLoopRemove(int e, int l)
    {
        if (LRadialNext[l] != l)
        {
            if (l == EL[e]) EL[e] = LRadialNext[l];
            LRadialPrev[LRadialNext[l]] = LRadialPrev[l];
            LRadialNext[LRadialPrev[l]] = LRadialNext[l];
        }
        else if (l == EL[e]) EL[e] = None;
        LRadialNext[l] = LRadialPrev[l] = None;
        LE[l] = None;
    }
    bool VertInEdge(int e, int v) const { return EV1[e] == v || EV2[e] == v; }
    int EdgeOtherVert(int e, int v) const { return EV1[e] == v ? EV2[e] : EV2[e] == v ? EV1[e] : None; }
    bool EdgeIsWire(int e) const { return EL[e] == None; }
    bool EdgeIsManifold(int e) const { int l = EL[e]; return l != None && LRadialNext[l] != l && LRadialNext[LRadialNext[l]] == l; }
    int EdgeExists(int v1, int v2) const
    {
        int ea = VE[v1], eb = VE[v2];
        if (ea == None || eb == None) return None;
        int eaIter = ea, ebIter = eb;
        do
        {
            if (EV1[eaIter] == v2 || EV2[eaIter] == v2) return eaIter;
            if (EV1[ebIter] == v1 || EV2[ebIter] == v1) return ebIter;
        } while ((eaIter = DiskEdgeNext(eaIter, v1)) != ea && (ebIter = DiskEdgeNext(ebIter, v2)) != eb);
        return None;
    }

    void KillOnlyVert(int v) { VAlive[v] = 0; TotVert--; freeV.push_back(v); }
    void KillOnlyEdge(int e) { EAlive[e] = 0; TotEdge--; freeE.push_back(e); }
    void KillOnlyFace(int f) { FAlive[f] = 0; TotFace--; freeF.push_back(f); }
    void KillOnlyLoop(int l) { LAlive[l] = 0; TotLoop--; freeL.push_back(l); }
    void FaceKill(int f)
    {
        if (FLFirst[f] != None)
        {
            int lFirst = FLFirst[f], lIter = lFirst;
            do { int lNext = LNext[lIter]; RadialLoopRemove(LE[lIter], lIter); KillOnlyLoop(lIter); lIter = lNext; } while (lIter != lFirst);
        }
        KillOnlyFace(f);
    }
    void EdgeKill(int e)
    {
        while (EL[e] != None) FaceKill(LF[EL[e]]);
        DiskEdgeRemove(e, EV1[e]);
        DiskEdgeRemove(e, EV2[e]);
        KillOnlyEdge(e);
    }
    void VertKill(int v) { while (VE[v] != None) EdgeKill(VE[v]); KillOnlyVert(v); }
    bool EdgeSplice(int eDst, int eSrc)
    {
        if (!VertInEdge(eSrc, EV1[eDst]) || !VertInEdge(eSrc, EV2[eDst])) return false;
        while (EL[eSrc] != None) { int l = EL[eSrc]; RadialLoopRemove(eSrc, l); RadialLoopAppend(eDst, l); }
        EdgeKill(eSrc);
        return true;
    }
};

// The walk queries of BMesh.cs over either storage (the growable pools, or the caller's pinned arrays).
template <class B> int RadialFaceVertCount(const B& bm, int l, int v) { int count = 0, lIter = l; do { if (bm.LV[lIter] == v) count++; } while ((lIter = bm.LRadialNext[lIter]) != l); return count; }
template <class B> bool RadialFaceVertCheck(const B& bm, int l, int v) { int lIter = l; do { if (bm.LV[lIter] == v) return true; } while ((lIter = bm.LRadialNext[lIter]) != l); return false; }
template <class B> int RadialFaceLoopFindFirst(const B& bm, int l, int v) { int lIter = l; do { if (bm.LV[lIter] == v) return lIter; } while ((lIter = bm.LRadialNext[lIter]) != l); return None; }
template <class B> int RadialFaceLoopFindNext(const B& bm, int l, int v) { int lIter = bm.LRadialNext[l]; do { if (bm.LV[lIter] == v) return lIter; } while ((lIter = bm.LRadialNext[lIter]) != l); return l; }
template <class B> int DiskEdgeNextOf(const B& bm, int e, int v) { return bm.EDiskNext[2 * e + (bm.EV1[e] == v ? 0 : 1)]; }
template <class B> int DiskFaceVertCount(const B& bm, int v)
{
    int count = 0;
    if (bm.VE[v] != None) { int eFirst = bm.VE[v], eIter = eFirst; do { if (bm.EL[eIter] != None) count += RadialFaceVertCount(bm, bm.EL[eIter], v); } while ((eIter = DiskEdgeNextOf(bm, eIter, v)) != eFirst); }
    return count;
}
template <class B> int DiskFaceLoopFindFirst(const B& bm, int e, int v)
{
    int eIter = e;
    do { if (bm.EL[eIter] != None) return bm.LV[bm.EL[eIter]] == v ? bm.EL[eIter] : bm.LNext[bm.EL[eIter]]; } while ((eIter = DiskEdgeNextOf(bm, eIter, v)) != e);
    return None;
}
template <class B> int DiskFaceEdgeFindNext(const B& bm, int e, int v)
{
    int eFind = DiskEdgeNextOf(bm, e, v);
    do { if (bm.EL[eFind] != None && RadialFaceVertCheck(bm, bm.EL[eFind], v)) return eFind; } while ((eFind = DiskEdgeNextOf(bm, eFind, v)) != e);
    return e;
}
template <class B> void LoopsOfVert(const B& bm, int v, std::vector<int>& into)
{
    into.clear();
    int count = DiskFaceVertCount(bm, v);
    if (count == 0) return;
    int lFirst = DiskFaceLoopFindFirst(bm, bm.VE[v], v);
    int eNext = bm.LE[lFirst];
    int lNext = lFirst;
    while (count > 0)
    {
        int lCurr = lNext;
        count--;
        lNext = RadialFaceLoopFindNext(bm, lNext, v);
        if (lNext == lFirst)
        {
            eNext = DiskFaceEdgeFindNext(bm, eNext, v);
            lFirst = RadialFaceLoopFindFirst(bm, bm.EL[eNext], v);
            lNext = lFirst;
        }
        into.push_back(lCurr);
    }
}

// the caller's pinned arrays, read only (haf_bmesh_vert_material)
struct BMView
{
    const int *VE, *EV1, *EV2, *EL, *EDiskNext, *LV, *LE, *LF, *LNext, *LRadialNext, *FMat; const uint8_t* VAlive; int VertCount;
};

// ---------------------------------------------------------------- the loop custom data (BlenderWeld.Layers): UVs, corner colours, custom-normal shorts
struct Layers
{
    std::vector<std::vector<float>> Uv; std::vector<std::vector<uint8_t>> CornerColor; std::vector<short> CustomNormal; bool hasCustomNormal = false;
    void EnsureLoop(int slot)
    {
        for (auto& u : Uv) if ((size_t)(2 * slot + 1) >= u.size()) u.resize(std::max((size_t)(2 * slot + 2), u.size() * 2));
        for (auto& c : CornerColor) if ((size_t)(4 * slot + 3) >= c.size()) c.resize(std::max((size_t)(4 * slot + 4), c.size() * 2));
        if (hasCustomNormal && (size_t)(2 * slot + 1) >= CustomNormal.size()) CustomNormal.resize(std::max((size_t)(2 * slot + 2), CustomNormal.size() * 2));
    }
    void InterpLoopAtOne(int dst, int src)
    {
        EnsureLoop(dst);
        for (auto& u : Uv) { u[2 * dst] = u[2 * src]; u[2 * dst + 1] = u[2 * src + 1]; }
        for (auto& c : CornerColor) for (int k = 0; k < 4; k++) c[4 * dst + k] = c[4 * src + k];
    }
    void CopyLoop(int src, int dst)
    {
        EnsureLoop(dst);
        for (auto& u : Uv) { u[2 * dst] = u[2 * src]; u[2 * dst + 1] = u[2 * src + 1]; }
        for (auto& c : CornerColor) for (int k = 0; k < 4; k++) c[4 * dst + k] = c[4 * src + k];
        if (hasCustomNormal) { CustomNormal[2 * dst] = CustomNormal[2 * src]; CustomNormal[2 * dst + 1] = CustomNormal[2 * src + 1]; }
    }
};

// ---------------------------------------------------------------- BLI_heap (BlenderDecimate.Heap)
struct Heap
{
    std::vector<float> nodeValue; std::vector<int> nodeIndex, nodePtr, tree;
    int nodeCount = 0, freeHead = -1, size = 0;
    explicit Heap(int reserve) { int cap = std::max(1, reserve); tree.resize(cap); nodeValue.resize(cap); nodeIndex.resize(cap); nodePtr.resize(cap); }
    bool IsEmpty() const { return size == 0; }
    float TopValue() const { return nodeValue[tree[0]]; }
    int TopNode() const { return tree[0]; }
    float NodeValue(int node) const { return nodeValue[node]; }
    int NodePtr(int node) const { return nodePtr[node]; }
    void Swap(int i, int j) { int pi = tree[i], pj = tree[j]; nodeIndex[pi] = j; tree[j] = pi; nodeIndex[pj] = i; tree[i] = pj; }
    void Down(int i)
    {
        while (true)
        {
            int l = (i << 1) + 1, r = (i << 1) + 2, smallest = i;
            if (l < size && nodeValue[tree[l]] < nodeValue[tree[smallest]]) smallest = l;
            if (r < size && nodeValue[tree[r]] < nodeValue[tree[smallest]]) smallest = r;
            if (smallest == i) break;
            Swap(i, smallest);
            i = smallest;
        }
    }
    void Up(int i)
    {
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (nodeValue[tree[p]] < nodeValue[tree[i]]) break;
            Swap(p, i);
            i = p;
        }
    }
    int AllocNode()
    {
        if (freeHead != -1) { int n = freeHead; freeHead = nodePtr[n]; return n; }
        if (nodeCount == (int)nodeValue.size()) { size_t cap = nodeValue.size() * 2; nodeValue.resize(cap); nodeIndex.resize(cap); nodePtr.resize(cap); }
        return nodeCount++;
    }
    void FreeNode(int n) { nodePtr[n] = freeHead; freeHead = n; }
    int Insert(float value, int ptr)
    {
        if (size >= (int)tree.size()) tree.resize(tree.size() * 2);
        int n = AllocNode();
        nodePtr[n] = ptr; nodeValue[n] = value; nodeIndex[n] = size;
        tree[size] = n; size++;
        Up(nodeIndex[n]);
        return n;
    }
    int PopMin()
    {
        int ptr = nodePtr[tree[0]];
        FreeNode(tree[0]);
        if (--size > 0) { Swap(0, size); Down(0); }
        return ptr;
    }
    void Remove(int node)
    {
        int i = nodeIndex[node];
        while (i > 0) { int p = (i - 1) >> 1; Swap(p, i); i = p; }
        PopMin();
    }
    void UpdatePtr(int node, float value, int ptr)
    {
        nodePtr[node] = ptr;
        if (value < nodeValue[node]) { nodeValue[node] = value; Up(nodeIndex[node]); }
        else if (value > nodeValue[node]) { nodeValue[node] = value; Down(nodeIndex[node]); }
    }
};

// ---------------------------------------------------------------- the result handle
struct Result { BMV bm; Layers layers; int status[4] = { 0, 0, 0, 0 }; };

// ---------------------------------------------------------------- BLI_kdtree<float3> (BlenderWeld.KdTree)
struct KdTree
{
    std::vector<float> Co; std::vector<int> Index, Left, Right, D; int Len = 0, Root = None;
    explicit KdTree(int capacity) : Co(3 * (size_t)capacity), Index(capacity), Left(capacity), Right(capacity), D(capacity) {}
    void Insert(int index, float x, float y, float z)
    {
        int n = Len++;
        Left[n] = Right[n] = None; Co[3 * n] = x; Co[3 * n + 1] = y; Co[3 * n + 2] = z; Index[n] = index; D[n] = 0;
    }
    void SwapNodes(int a, int b)
    {
        for (int k = 0; k < 3; k++) std::swap(Co[3 * a + k], Co[3 * b + k]);
        std::swap(Index[a], Index[b]);
    }
    int Balance(int start, int len, int axis)
    {
        if (len <= 0) return None;
        if (len == 1) return start;
        int left = 0, right = len - 1, median = len / 2;
        while (right > left)
        {
            float co = Co[3 * (start + right) + axis];
            int i = left - 1, j = right;
            while (true)
            {
                while (Co[3 * (start + (++i)) + axis] < co) { }
                while (Co[3 * (start + (--j)) + axis] > co && j > left) { }
                if (i >= j) break;
                SwapNodes(start + i, start + j);
            }
            SwapNodes(start + i, start + right);
            if (i >= median) right = i - 1;
            if (i <= median) left = i + 1;
        }
        int node = start + median;
        D[node] = axis;
        axis = (axis + 1) % 3;
        Left[node] = Balance(start, median, axis);
        Right[node] = Balance(start + median + 1, len - (median + 1), axis);
        return node;
    }
    void BalanceAll() { Root = Balance(0, Len, 0); }
    // kdtree_range_search_cb: the stack walk, left pushed before right (so the right side is visited first)
    template <class Fn> void RangeSearch(float x, float y, float z, float range, std::vector<int>& stack, Fn found) const
    {
        if (Root == None) return;
        float rangeSq = (float)(range * range);
        stack.clear(); stack.push_back(Root);
        float co[3] = { x, y, z };
        while (!stack.empty())
        {
            int node = stack.back(); stack.pop_back();
            int d = D[node];
            if ((float)(co[d] + range) < Co[3 * node + d]) { if (Left[node] != None) stack.push_back(Left[node]); }
            else if ((float)(co[d] - range) > Co[3 * node + d]) { if (Right[node] != None) stack.push_back(Right[node]); }
            else
            {
                float dx = (float)(Co[3 * node] - x), dy = (float)(Co[3 * node + 1] - y), dz = (float)(Co[3 * node + 2] - z);
                float distSq = (float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz));
                if (distSq <= rangeSq) found(Index[node], distSq);
                if (Left[node] != None) stack.push_back(Left[node]);
                if (Right[node] != None) stack.push_back(Right[node]);
            }
        }
    }
};

// deduplicate_target_calc_fn: two members keep the lower index; more keep the one nearest the float32 centroid, starting from the
// last (the search's origin), a tie going to the lower index.
int PickTarget(const float* VCo, const int* verts, const std::vector<int>& cluster)
{
    int num = (int)cluster.size();
    if (num == 2) return cluster[0] < cluster[1] ? 0 : 1;
    float cx = 0.f, cy = 0.f, cz = 0.f;
    for (int i = 0; i < num; i++) { int v = verts[cluster[i]]; cx = (float)(cx + VCo[3 * v]); cy = (float)(cy + VCo[3 * v + 1]); cz = (float)(cz + VCo[3 * v + 2]); }
    float fn = (float)num; cx = (float)(cx / fn); cy = (float)(cy / fn); cz = (float)(cz / fn);
    auto DistSq = [&](int i) { int v = verts[cluster[i]]; float dx = (float)(cx - VCo[3 * v]), dy = (float)(cy - VCo[3 * v + 1]), dz = (float)(cz - VCo[3 * v + 2]); return (float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz)); };
    int end = num - 1, best = end; float bestSq = DistSq(best);
    for (int i = 0; i < end; i++)
    {
        float test = DistSq(i);
        if (test > bestSq) continue;
        if (test == bestSq && cluster[i] > cluster[best]) continue;
        best = i; bestSq = test;
    }
    return best;
}

// ---------------------------------------------------------------- bmesh.ops.dissolve_limit (BlenderDissolve)
struct Dissolve
{
    BMV& bm; Layers& layers;
    std::vector<int> stampE, stampV; int gen = 0;
    std::vector<int> edgesScratch, loopsScratch, slotsScratch, edgesSort, vertsSort;
    Dissolve(BMV& b, Layers& l) : bm(b), layers(l) {}

    // BM_face_calc_normal into f->no: normal_tri_v3 on three corners, normal_quad_v3 on four, Newell's method past that
    void FaceNormalUpdate(int f)
    {
        int l0 = bm.FLFirst[f];
        const float* VCo = bm.VCo.data();
        if (bm.FLen[f] == 3)
        {
            int l1 = bm.LNext[l0], l2 = bm.LNext[l1];
            float nx, ny, nz; NormalTri(VCo, bm.LV[l0], bm.LV[l1], bm.LV[l2], nx, ny, nz);
            bm.FNo[3 * f] = nx; bm.FNo[3 * f + 1] = ny; bm.FNo[3 * f + 2] = nz;
            return;
        }
        if (bm.FLen[f] == 4)
        {
            int l1 = bm.LNext[l0], l2 = bm.LNext[l1], l3 = bm.LNext[l2];
            int v1 = bm.LV[l0], v2 = bm.LV[l1], v3 = bm.LV[l2], v4 = bm.LV[l3];
            float n1x = (float)(VCo[3 * v1] - VCo[3 * v3]), n1y = (float)(VCo[3 * v1 + 1] - VCo[3 * v3 + 1]), n1z = (float)(VCo[3 * v1 + 2] - VCo[3 * v3 + 2]);
            float n2x = (float)(VCo[3 * v2] - VCo[3 * v4]), n2y = (float)(VCo[3 * v2 + 1] - VCo[3 * v4 + 1]), n2z = (float)(VCo[3 * v2 + 2] - VCo[3 * v4 + 2]);
            float x = (float)((float)(n1y * n2z) - (float)(n1z * n2y)), y = (float)((float)(n1z * n2x) - (float)(n1x * n2z)), z = (float)((float)(n1x * n2y) - (float)(n1y * n2x));
            NormalizeV3(x, y, z);
            bm.FNo[3 * f] = x; bm.FNo[3 * f + 1] = y; bm.FNo[3 * f + 2] = z;
            return;
        }
        float nx2 = 0.f, ny2 = 0.f, nz2 = 0.f;
        int vPrev = bm.LV[bm.LPrev[l0]], vCurr = bm.LV[l0], l = l0;
        do
        {
            float px = VCo[3 * vPrev], py = VCo[3 * vPrev + 1], pz = VCo[3 * vPrev + 2];
            float cx = VCo[3 * vCurr], cy = VCo[3 * vCurr + 1], cz = VCo[3 * vCurr + 2];
            nx2 = (float)(nx2 + (float)((float)(py - cy) * (float)(pz + cz)));
            ny2 = (float)(ny2 + (float)((float)(pz - cz) * (float)(px + cx)));
            nz2 = (float)(nz2 + (float)((float)(px - cx) * (float)(py + cy)));
            l = bm.LNext[l]; vPrev = vCurr; vCurr = bm.LV[l];
        } while (l != l0);
        NormalizeV3(nx2, ny2, nz2);
        bm.FNo[3 * f] = nx2; bm.FNo[3 * f + 1] = ny2; bm.FNo[3 * f + 2] = nz2;
    }
    void FaceNormalsUpdateAll() { for (int f = 0; f < bm.FaceCount; f++) if (bm.FAlive[f]) FaceNormalUpdate(f); }

    float AngleV3V3V3(int a, int b, int c)
    {
        const float* P = bm.VCo.data();
        float v1x = (float)(P[3 * b] - P[3 * a]), v1y = (float)(P[3 * b + 1] - P[3 * a + 1]), v1z = (float)(P[3 * b + 2] - P[3 * a + 2]);
        float v2x = (float)(P[3 * b] - P[3 * c]), v2y = (float)(P[3 * b + 1] - P[3 * c + 1]), v2z = (float)(P[3 * b + 2] - P[3 * c + 2]);
        NormalizeV3(v1x, v1y, v1z); NormalizeV3(v2x, v2y, v2z);
        return AngleNormalized(v1x, v1y, v1z, v2x, v2y, v2z);
    }
    float CosV3V3V3(int p1, int p2, int p3)
    {
        const float* P = bm.VCo.data();
        float v1x = (float)(P[3 * p2] - P[3 * p1]), v1y = (float)(P[3 * p2 + 1] - P[3 * p1 + 1]), v1z = (float)(P[3 * p2 + 2] - P[3 * p1 + 2]);
        float v2x = (float)(P[3 * p2] - P[3 * p3]), v2y = (float)(P[3 * p2 + 1] - P[3 * p3 + 1]), v2z = (float)(P[3 * p2 + 2] - P[3 * p3 + 2]);
        NormalizeV3(v1x, v1y, v1z); NormalizeV3(v2x, v2y, v2z);
        return Dot3(v1x, v1y, v1z, v2x, v2y, v2z);
    }
    bool EdgeIsContiguous(int e)
    {
        int l = bm.EL[e]; if (l == None) return false;
        int lo = bm.LRadialNext[l];
        return lo != l && bm.LRadialNext[lo] == l && bm.LV[lo] != bm.LV[l];
    }
    float EdgeCalcFaceAngle(int e)
    {
        if (!bm.EdgeIsManifold(e)) return Deg90;
        int f1 = bm.LF[bm.EL[e]], f2 = bm.LF[bm.LRadialNext[bm.EL[e]]];
        return AngleNormalized(bm.FNo[3 * f1], bm.FNo[3 * f1 + 1], bm.FNo[3 * f1 + 2], bm.FNo[3 * f2], bm.FNo[3 * f2 + 1], bm.FNo[3 * f2 + 2]);
    }
    bool VertEdgePair(int v, int& ea, int& eb)
    {
        ea = bm.VE[v]; eb = None;
        if (ea == None) return false;
        int e2 = bm.DiskEdgeNext(ea, v);
        if (e2 != ea && bm.DiskEdgeNext(e2, v) == ea) { eb = e2; return true; }
        ea = None; return false;
    }
    float VertCalcEdgeAngle(int v)
    {
        int e1, e2;
        if (VertEdgePair(v, e1, e2))
        {
            int v1 = bm.EdgeOtherVert(e1, v), v2 = bm.EdgeOtherVert(e2, v);
            return (float)(PiF - AngleV3V3V3(v1, v, v2));
        }
        return Deg90;
    }
    float EdgeCalcDissolveError(int e)
    {
        if (!bm.EdgeIsManifold(e)) return CostInvalid;
        int f1 = bm.LF[bm.EL[e]], f2 = bm.LF[bm.LRadialNext[bm.EL[e]]];
        float cosNeg = Dot3(bm.FNo[3 * f1], bm.FNo[3 * f1 + 1], bm.FNo[3 * f1 + 2], bm.FNo[3 * f2], bm.FNo[3 * f2 + 1], bm.FNo[3 * f2 + 2]);
        if (EdgeIsContiguous(e)) cosNeg = (float)(cosNeg * -1.f);
        return cosNeg;
    }
    float VertEdgeFaceAngle(int v)
    {
        float angle = VertCalcEdgeAngle(v);
        if (bm.VE[v] != None && bm.EdgeIsManifold(bm.VE[v]))
            return (float)((float)((float)(angle * AngleToUnit) * (float)(EdgeCalcFaceAngle(bm.VE[v]) * AngleToUnit)) * UnitToAngle);
        return angle;
    }
    int DiskCountAtMost(int v, int max)
    {
        int e = bm.VE[v]; if (e == None) return 0;
        int count = 0, e0 = e;
        do { count++; if (count == max) break; e = bm.DiskEdgeNext(e, v); } while (e != e0);
        return count;
    }
    // BM_face_find_double: a face on the first corner's edge with the same corners' edges in either direction
    int FaceFindDouble(int f)
    {
        int lFirst = bm.FLFirst[f];
        for (int lIter = bm.LRadialNext[lFirst]; lIter != lFirst; lIter = bm.LRadialNext[lIter])
        {
            if (bm.FLen[bm.LF[lIter]] != bm.FLen[f]) continue;
            if (bm.LV[lIter] == bm.LV[lFirst])
            {
                int la = lFirst, lb = lIter, lbInit = lIter;
                do { if (bm.LE[la] != bm.LE[lb]) break; la = bm.LNext[la]; lb = bm.LNext[lb]; } while (lb != lbInit);
                if (lb == lbInit) return bm.LF[lIter];
            }
            else
            {
                int la = lFirst, lb = lIter, lbInit = lIter;
                do { if (bm.LE[la] != bm.LE[lb]) break; la = bm.LPrev[la]; lb = bm.LNext[lb]; } while (lb != lbInit);
                if (lb == lbInit) return bm.LF[lIter];
            }
        }
        return None;
    }
    void Mark()
    {
        if (++gen == 0x7fffffff) { std::fill(stampE.begin(), stampE.end(), 0); std::fill(stampV.begin(), stampV.end(), 0); gen = 1; }
        if (stampE.size() < (size_t)bm.EdgeCount) stampE.resize(bm.EdgeCount, 0);
        if (stampV.size() < (size_t)bm.VertCount) stampV.resize(bm.VertCount, 0);
    }
    // bm_edges_sort_winding then BM_face_create: the boundary edges walked from v1 through v2 around each vertex's disk
    int FaceCreateNgon(int v1, int v2, const std::vector<int>& edges, int example, int* loopSlots)
    {
        int len = (int)edges.size();
        Mark();
        for (int e : edges) { stampE[e] = gen; stampV[bm.EV1[e]] = gen; stampV[bm.EV2[e]] = gen; }
        edgesSort.assign(len, 0); vertsSort.assign(len, 0);
        int i = 0, vIter = v1, eIter = bm.VE[v1], eFirst = eIter;
        do
        {
            if (stampE[eIter] == gen && bm.EdgeOtherVert(eIter, vIter) == v2) { i = 1; break; }
            eIter = bm.DiskEdgeNext(eIter, vIter);
        } while (eIter != eFirst);
        if (i == 0) return None;
        i = 0;
        do
        {
            if (stampE[eIter] == gen)
            {
                if (stampV[vIter] != gen) return None;   // a vertex in the loop twice
                stampE[eIter] = 0; edgesSort[i] = eIter;
                stampV[vIter] = 0; vertsSort[i] = vIter;
                i += 1;
                vIter = bm.EdgeOtherVert(eIter, vIter);
                if (i == len) { if (vIter != vertsSort[0]) return None; break; }
                eFirst = eIter;
            }
            eIter = bm.DiskEdgeNext(eIter, vIter);
        } while (eIter != eFirst);
        if (i != len) return None;
        return bm.FaceCreate(vertsSort.data(), edgesSort.data(), len, example, loopSlots);
    }
    // BM_faces_join on two faces without do_del: the region's new face, or None; the two old faces die
    int FacesJoin(int fa, int fb)
    {
        std::vector<int>& edges = edgesScratch; edges.clear(); int v1 = None, v2 = None;
        const int pair[2] = { fa, fb };
        for (int f : pair)
        {
            int l = bm.FLFirst[f], l0 = l;
            do
            {
                int rlen = 0, lr = l;
                do { int lf = bm.LF[lr]; if (lf == fa || lf == fb) rlen++; lr = bm.LRadialNext[lr]; } while (lr != l);
                if (rlen > 2) return None;
                if (rlen == 1)
                {
                    edges.push_back(bm.LE[l]);
                    if (v1 == None) { v1 = bm.LV[l]; v2 = bm.EdgeOtherVert(bm.LE[l], bm.LV[l]); }
                }
                l = bm.LNext[l];
            } while (l != l0);
        }
        if (edges.empty()) return None;
        slotsScratch.assign(edges.size(), 0);
        int fNew = FaceCreateNgon(v1, v2, edges, fa, slotsScratch.data());
        if (fNew == None) return None;
        int fExisting = FaceFindDouble(fNew);
        bool reusing = false;
        if (fExisting != None) { bm.FaceKill(fNew); fNew = fExisting; reusing = true; }
        if (!reusing)
        {
            int l = bm.FLFirst[fNew], l0 = l;
            do
            {
                int l2 = bm.LRadialNext[l];
                while (l2 != l && !(bm.LF[l2] == fa || bm.LF[l2] == fb)) l2 = bm.LRadialNext[l2];
                if (l2 != l)
                {
                    if (bm.LV[l2] != bm.LV[l]) l2 = bm.LNext[l2];
                    layers.CopyLoop(l2, l);
                }
                l = bm.LNext[l];
            } while (l != l0);
        }
        bm.FaceKill(fa); bm.FaceKill(fb);
        return fNew;
    }
    // bmesh_kernel_loop_reverse
    void LoopReverse(int f)
    {
        int lFirst = bm.FLFirst[f];
        int ePrev = bm.LE[bm.LPrev[lFirst]];
        int lPrevRadNext = bm.LRadialNext[bm.LPrev[lFirst]], lPrevRadPrev = bm.LRadialPrev[bm.LPrev[lFirst]];
        bool isPrevBoundary = lPrevRadNext == bm.LRadialNext[lPrevRadNext];
        int l = lFirst;
        do
        {
            int eIter = bm.LE[l];
            int lIterRadNext = bm.LRadialNext[l], lIterRadPrev = bm.LRadialPrev[l];
            bool isIterBoundary = lIterRadNext == bm.LRadialNext[lIterRadNext];
            if (isPrevBoundary) { bm.LRadialNext[l] = l; bm.LRadialPrev[l] = l; }
            else { bm.LRadialNext[l] = lPrevRadNext; bm.LRadialPrev[l] = lPrevRadPrev; bm.LRadialPrev[lPrevRadNext] = l; bm.LRadialNext[lPrevRadPrev] = l; }
            if (bm.EL[eIter] == l) bm.EL[eIter] = bm.LNext[l];
            bm.LE[l] = ePrev;
            std::swap(bm.LNext[l], bm.LPrev[l]);
            ePrev = eIter; lPrevRadNext = lIterRadNext; lPrevRadPrev = lIterRadPrev; isPrevBoundary = isIterBoundary;
            l = bm.LPrev[l];
        } while (l != lFirst);
    }
    int FacesJoinPair(int la, int lb)
    {
        if (bm.LV[la] == bm.LV[lb]) LoopReverse(bm.LF[lb]);
        return FacesJoin(bm.LF[la], bm.LF[lb]);
    }
    int JoinEdgeKillVert(int eKill, int vKill, bool doDel, bool checkEdgeExists, bool killDegenerate, bool killDuplicate)
    {
        if (!bm.VertInEdge(eKill, vKill)) return None;
        if (DiskCountAtMost(vKill, 3) != 2) return None;
        int eOld = bm.DiskEdgeNext(eKill, vKill);
        int vTarget = bm.EdgeOtherVert(eKill, vKill), vOld = bm.EdgeOtherVert(eOld, vKill);
        if ((bm.EV1[eOld] == vKill && bm.EV2[eOld] == vTarget) || (bm.EV1[eOld] == vTarget && bm.EV2[eOld] == vKill)) return None;
        int eSplice = checkEdgeExists ? bm.EdgeExists(vTarget, vOld) : None;
        bm.DiskVertReplace(eOld, vTarget, vKill);
        bm.DiskEdgeRemove(eKill, vTarget);
        std::vector<int> degenerate, duplicate;
        if (bm.EL[eKill] != None)
        {
            int lKill = bm.EL[eKill], lHead = lKill;
            do
            {
                int lNext = bm.LNext[lKill], lPrev = bm.LPrev[lKill], f = bm.LF[lKill];
                if (bm.LV[lNext] == vKill) bm.LV[lNext] = vTarget;
                bm.LPrev[lNext] = lPrev; bm.LNext[lPrev] = lNext;
                if (bm.FLFirst[f] == lKill) bm.FLFirst[f] = lNext;
                bm.FLen[f]--;
                if (killDegenerate && bm.FLen[f] < 3) degenerate.push_back(f);
                else if (killDuplicate) duplicate.push_back(f);
                int lKillNext = bm.LRadialNext[lKill];
                bm.KillOnlyLoop(lKill);
                lKill = lKillNext;
            } while (lKill != lHead);
        }
        bm.KillOnlyEdge(eKill);
        if (doDel) bm.KillOnlyVert(vKill); else bm.VE[vKill] = None;
        if (checkEdgeExists && eSplice != None) bm.EdgeSplice(eOld, eSplice);
        if (killDegenerate) while (!degenerate.empty()) { int f = degenerate.back(); degenerate.pop_back(); if (bm.FAlive[f]) bm.FaceKill(f); }
        if (killDuplicate) while (!duplicate.empty()) { int f = duplicate.back(); duplicate.pop_back(); if (bm.FAlive[f] && FaceFindDouble(f) != None) bm.FaceKill(f); }
        return eOld;
    }
    int VertCollapseEdge(int eKill, int vKill)
    {
        int tv = bm.EdgeOtherVert(eKill, vKill);
        if (bm.EL[eKill] != None)
        {
            int l = bm.EL[eKill], l0 = l;
            do
            {
                if (bm.LV[l] == tv && bm.LV[bm.LNext[l]] == vKill) layers.InterpLoopAtOne(bm.LNext[l], l);
                l = bm.LRadialNext[l];
            } while (l != l0);
        }
        return JoinEdgeKillVert(eKill, vKill, true, true, true, true);
    }
    static float LinePointSideV2(float l1x, float l1y, float l2x, float l2y, float px, float py)
    { return (float)((float)((float)(l1x - px) * (float)(l2y - py)) - (float)((float)(l2x - px) * (float)(l1y - py))); }
    static bool IsectPointTriV2Cw(float px, float py, float x1, float y1, float x2, float y2, float x3, float y3)
    { return LinePointSideV2(x1, y1, x2, y2, px, py) >= 0.f && LinePointSideV2(x2, y2, x3, y3, px, py) >= 0.f && LinePointSideV2(x3, y3, x1, y1, px, py) >= 0.f; }
    static float CrossTriV2(float ax, float ay, float bx, float by, float cx, float cy) { return (float)((float)((float)(ax - bx) * (float)(by - cy)) + (float)((float)(ay - by) * (float)(cx - bx))); }
    static int SignumI(float a) { return a > 0.f ? 1 : a < 0.f ? -1 : 0; }
    bool LoopCollapseIsDegenerate(int lEar)
    {
        int f = bm.LF[lEar], vc = bm.LV[lEar];
        const float* P = bm.VCo.data();
        float cx = P[3 * vc], cy = P[3 * vc + 1], cz = P[3 * vc + 2];
        float n1[3], n2[3];
        AxisDominantRows(bm.FNo[3 * f], bm.FNo[3 * f + 1], bm.FNo[3 * f + 2], n1, n2);
        auto Proj = [&](int v, float& rx, float& ry) { ProjectV2(n1, n2, (float)(P[3 * v] - cx), (float)(P[3 * v + 1] - cy), (float)(P[3 * v + 2] - cz), rx, ry); };
        int lPrev = bm.LPrev[lEar], lNext = bm.LNext[lEar];
        float t0x, t0y, t2x, t2y; Proj(bm.LV[lPrev], t0x, t0y); float t1x = 0.f, t1y = 0.f; Proj(bm.LV[lNext], t2x, t2y);
        int ea, eb;
        if (!VertEdgePair(bm.LV[lPrev], ea, eb))
        {
            float ax, ay; Proj(bm.LV[bm.LPrev[lPrev]], ax, ay);
            if (SignumI(CrossTriV2(ax, ay, t0x, t0y, t1x, t1y)) != SignumI(CrossTriV2(ax, ay, t0x, t0y, t2x, t2y))) return true;
        }
        if (!VertEdgePair(bm.LV[lNext], ea, eb))
        {
            float ax, ay; Proj(bm.LV[bm.LNext[lNext]], ax, ay);
            if (SignumI(CrossTriV2(ax, ay, t2x, t2y, t1x, t1y)) != SignumI(CrossTriV2(ax, ay, t2x, t2y, t0x, t0y))) return true;
        }
        if (CrossTriV2(t0x, t0y, t1x, t1y, t2x, t2y) < 0.f) { std::swap(t1x, t2x); std::swap(t1y, t2y); }
        int lIter = bm.LNext[lNext], lFirst = lPrev;
        do
        {
            float px, py; Proj(bm.LV[lIter], px, py);
            if (IsectPointTriV2Cw(px, py, t0x, t0y, t1x, t1y, t2x, t2y)) return true;
            lIter = bm.LNext[lIter];
        } while (lIter != lFirst);
        return false;
    }
    bool VertCollapseIsDegenerate(int v)
    {
        int ea, eb;
        if (!VertEdgePair(v, ea, eb)) return true;
        if (bm.EdgeIsWire(ea) || bm.EdgeIsWire(eb)) return false;
        int vp0 = bm.EdgeOtherVert(ea, v), vp1 = bm.EdgeOtherVert(eb, v);
        if (std::fabs(CosV3V3V3(vp0, v, vp1)) < (float)(1.0f - FltEpsilon))
        {
            int l = bm.EL[eb]; if (l == None) return false;
            int l0 = l;
            do
            {
                if (bm.FLen[bm.LF[l]] > 3)
                {
                    int lPivot = bm.LV[l] == v ? l : bm.LNext[l];
                    if (LoopCollapseIsDegenerate(lPivot)) return true;
                }
                l = bm.LRadialNext[l];
            } while (l != l0);
        }
        return false;
    }

    // BM_mesh_decimate_dissolve_ex (do_dissolve_boundaries false, delimit 0) on the given elements in their order
    void DissolveLimit(float angleLimit, const int* vinput, int nv, const int* einput, int ne)
    {
        angleLimit = std::min((float)(3.14159265358979323846 / 2), angleLimit);
        float angleLimitCosNeg = (float)(-ucrt_cosf(angleLimit));
        // ---- edges
        std::vector<int> eIndex(bm.EdgeCount, -1); std::vector<uint8_t> wasWire(bm.EdgeCount, 0);
        for (int e = 0; e < bm.EdgeCount; e++) if (bm.EAlive[e]) wasWire[e] = bm.EdgeIsWire(e) ? 1 : 0;
        Heap heap(std::max(1, ne));
        std::vector<int> table(std::max(ne, nv), -1);
        for (int i = 0; i < ne; i++) { int e = einput[i]; table[i] = heap.Insert(EdgeCalcDissolveError(e), e); eIndex[e] = i; }
        while (!heap.IsEmpty() && heap.TopValue() < angleLimitCosNeg)
        {
            int node = heap.TopNode(), e = heap.NodePtr(node), i = eIndex[e], fNew = None;
            if (bm.EdgeIsManifold(e))
            {
                fNew = FacesJoinPair(bm.EL[e], bm.LRadialNext[bm.EL[e]]);
                if (fNew != None)
                {
                    heap.Remove(node); table[i] = -1;
                    FaceNormalUpdate(fNew);
                    int l = bm.FLFirst[fNew], l0 = l;
                    do
                    {
                        int j = eIndex[bm.LE[l]];
                        if (j != -1 && table[j] != -1) heap.UpdatePtr(table[j], EdgeCalcDissolveError(bm.LE[l]), bm.LE[l]);
                        l = bm.LNext[l];
                    } while (l != l0);
                }
            }
            if (fNew == None) heap.UpdatePtr(node, CostInvalid, e);
        }
        // ---- cleanup: the edges the joins left wire, in reverse slot order, and the vertices left alone
        std::vector<int> vIndex(bm.VertCount); int count = 0;
        for (int v = 0; v < bm.VertCount; v++) vIndex[v] = bm.VAlive[v] ? count++ : -1;
        std::vector<int> reverse(count, -1);
        std::vector<int> vin(nv);
        for (int i = 0; i < nv; i++) { vin[i] = vinput[i]; reverse[vIndex[vinput[i]]] = i; }
        std::vector<int> earray;
        for (int e = 0; e < bm.EdgeCount; e++) if (bm.EAlive[e]) earray.push_back(e);
        for (int i = (int)earray.size() - 1; i >= 0; i--)
        {
            int e = earray[i];
            if (!bm.EAlive[e] || !bm.EdgeIsWire(e) || wasWire[e]) continue;
            int v1 = bm.EV1[e], v2 = bm.EV2[e];
            bm.EdgeKill(e);
            if (bm.VE[v1] == None) { int r = reverse[vIndex[v1]]; if (r != -1) vin[r] = None; bm.VertKill(v1); }
            if (bm.VE[v2] == None) { int r = reverse[vIndex[v2]]; if (r != -1) vin[r] = None; bm.VertKill(v2); }
        }
        // ---- vertices
        std::vector<int> vIndex2(bm.VertCount, -1);
        Heap vheap(std::max(1, nv));
        std::fill(table.begin(), table.end(), -1);
        for (int i = 0; i < nv; i++)
        {
            int v = vin[i]; if (v == None) continue;
            table[i] = vheap.Insert(VertEdgeFaceAngle(v), v); vIndex2[v] = i;
        }
        while (!vheap.IsEmpty() && vheap.TopValue() < angleLimit)
        {
            int node = vheap.TopNode(), v = vheap.NodePtr(node), i = vIndex2[v], eNew = None;
            if (!VertCollapseIsDegenerate(v))
            {
                eNew = VertCollapseEdge(bm.VE[v], v);
                if (eNew != None)
                {
                    vheap.Remove(node); table[i] = -1;
                    if (bm.EL[eNew] != None)
                    {
                        int l = bm.EL[eNew], l0 = l;
                        do { FaceNormalUpdate(bm.LF[l]); l = bm.LRadialNext[l]; } while (l != l0);
                    }
                    const int ends[2] = { bm.EV1[eNew], bm.EV2[eNew] };
                    for (int vv : ends)
                    {
                        int j = vIndex2[vv];
                        if (j != -1 && table[j] != -1) vheap.UpdatePtr(table[j], VertEdgeFaceAngle(vv), vv);
                    }
                    if (bm.EL[eNew] != None)
                    {
                        int l = bm.EL[eNew], l0 = l;
                        do
                        {
                            int lc = bm.LNext[bm.LNext[l]], lcFirst = bm.LPrev[l];
                            do
                            {
                                int j = vIndex2[bm.LV[lc]];
                                if (j != -1 && table[j] != -1 && vheap.NodeValue(table[j]) == CostInvalid) vheap.UpdatePtr(table[j], VertEdgeFaceAngle(bm.LV[lc]), bm.LV[lc]);
                                lc = bm.LNext[lc];
                            } while (lc != lcFirst);
                            l = bm.LRadialNext[l];
                        } while (l != l0);
                    }
                }
            }
            if (eNew == None) vheap.UpdatePtr(node, CostInvalid, v);
        }
    }
};

void CopyIn(BMV& bm, const int* VE, const uint8_t* VAlive, const float* VCo, const float* VNo, const uint8_t* VFlag,
            const int* EV1, const int* EV2, const int* EL, const int* EDiskNext, const int* EDiskPrev, const uint8_t* EAlive, const uint8_t* EFlag,
            const int* LV, const int* LE, const int* LF, const int* LNext, const int* LPrev, const int* LRadialNext, const int* LRadialPrev, const uint8_t* LAlive,
            const int* FLFirst, const int* FLen, const int* FMat, const float* FNo, const uint8_t* FAlive, const uint8_t* FFlag, const int* counts)
{
    int nv = counts[0], ne = counts[2], nl = counts[4], nf = counts[6];
    bm.VertCount = nv; bm.TotVert = counts[1]; bm.EdgeCount = ne; bm.TotEdge = counts[3]; bm.LoopCount = nl; bm.TotLoop = counts[5]; bm.FaceCount = nf; bm.TotFace = counts[7];
    bm.VE.assign(VE, VE + nv); bm.VAlive.assign(VAlive, VAlive + nv); bm.VCo.assign(VCo, VCo + 3 * (size_t)nv); bm.VNo.assign(VNo, VNo + 3 * (size_t)nv); bm.VFlag.assign(VFlag, VFlag + nv);
    bm.EV1.assign(EV1, EV1 + ne); bm.EV2.assign(EV2, EV2 + ne); bm.EL.assign(EL, EL + ne); bm.EDiskNext.assign(EDiskNext, EDiskNext + 2 * (size_t)ne); bm.EDiskPrev.assign(EDiskPrev, EDiskPrev + 2 * (size_t)ne);
    bm.EAlive.assign(EAlive, EAlive + ne); bm.EFlag.assign(EFlag, EFlag + ne);
    bm.LV.assign(LV, LV + nl); bm.LE.assign(LE, LE + nl); bm.LF.assign(LF, LF + nl); bm.LNext.assign(LNext, LNext + nl); bm.LPrev.assign(LPrev, LPrev + nl);
    bm.LRadialNext.assign(LRadialNext, LRadialNext + nl); bm.LRadialPrev.assign(LRadialPrev, LRadialPrev + nl); bm.LAlive.assign(LAlive, LAlive + nl);
    bm.FLFirst.assign(FLFirst, FLFirst + nf); bm.FLen.assign(FLen, FLen + nf); bm.FMat.assign(FMat, FMat + nf); bm.FNo.assign(FNo, FNo + 3 * (size_t)nf); bm.FAlive.assign(FAlive, FAlive + nf); bm.FFlag.assign(FFlag, FFlag + nf);
}

} // namespace

// ---------------------------------------------------------------- exports
// The result handle: haf_bmesh_sizes says how big the arrays came out (counts[8]: VertCount, TotVert, EdgeCount, TotEdge, LoopCount,
// TotLoop, FaceCount, TotFace; freeSizes[4]: the free lists; layerLengths: each UV layer's floats, each corner colour layer's bytes,
// then the custom-normal shorts), haf_bmesh_copy fills arrays of exactly those sizes, haf_bmesh_status the status words, haf_bmesh_free releases it.
EXPORT void haf_bmesh_sizes(void* handle, int* counts, int* freeSizes, int* layerLengths)
{
    Result* r = (Result*)handle; const BMV& bm = r->bm;
    counts[0] = bm.VertCount; counts[1] = bm.TotVert; counts[2] = bm.EdgeCount; counts[3] = bm.TotEdge; counts[4] = bm.LoopCount; counts[5] = bm.TotLoop; counts[6] = bm.FaceCount; counts[7] = bm.TotFace;
    freeSizes[0] = (int)bm.freeV.size(); freeSizes[1] = (int)bm.freeE.size(); freeSizes[2] = (int)bm.freeL.size(); freeSizes[3] = (int)bm.freeF.size();
    int at = 0;
    for (const auto& u : r->layers.Uv) layerLengths[at++] = (int)u.size();
    for (const auto& c : r->layers.CornerColor) layerLengths[at++] = (int)c.size();
    layerLengths[at++] = r->layers.hasCustomNormal ? (int)r->layers.CustomNormal.size() : 0;
}
EXPORT void haf_bmesh_status(void* handle, int* status) { Result* r = (Result*)handle; for (int i = 0; i < 4; i++) status[i] = r->status[i]; }
EXPORT void haf_bmesh_copy(void* handle,
    int* VE, uint8_t* VAlive, float* VCo, float* VNo, uint8_t* VFlag,
    int* EV1, int* EV2, int* EL, int* EDiskNext, int* EDiskPrev, uint8_t* EAlive, uint8_t* EFlag,
    int* LV, int* LE, int* LF, int* LNext, int* LPrev, int* LRadialNext, int* LRadialPrev, uint8_t* LAlive,
    int* FLFirst, int* FLen, int* FMat, float* FNo, uint8_t* FAlive, uint8_t* FFlag,
    int* freeV, int* freeE, int* freeL, int* freeF,
    float** uv, uint8_t** cornerColor, short* customNormal)
{
    Result* r = (Result*)handle; const BMV& bm = r->bm;
    size_t nv = bm.VertCount, ne = bm.EdgeCount, nl = bm.LoopCount, nf = bm.FaceCount;
    auto Copy = [](auto* dst, const auto& src, size_t n) { if (n) std::memcpy(dst, src.data(), n * sizeof(*dst)); };
    Copy(VE, bm.VE, nv); Copy(VAlive, bm.VAlive, nv); Copy(VCo, bm.VCo, 3 * nv); Copy(VNo, bm.VNo, 3 * nv); Copy(VFlag, bm.VFlag, nv);
    Copy(EV1, bm.EV1, ne); Copy(EV2, bm.EV2, ne); Copy(EL, bm.EL, ne); Copy(EDiskNext, bm.EDiskNext, 2 * ne); Copy(EDiskPrev, bm.EDiskPrev, 2 * ne); Copy(EAlive, bm.EAlive, ne); Copy(EFlag, bm.EFlag, ne);
    Copy(LV, bm.LV, nl); Copy(LE, bm.LE, nl); Copy(LF, bm.LF, nl); Copy(LNext, bm.LNext, nl); Copy(LPrev, bm.LPrev, nl); Copy(LRadialNext, bm.LRadialNext, nl); Copy(LRadialPrev, bm.LRadialPrev, nl); Copy(LAlive, bm.LAlive, nl);
    Copy(FLFirst, bm.FLFirst, nf); Copy(FLen, bm.FLen, nf); Copy(FMat, bm.FMat, nf); Copy(FNo, bm.FNo, 3 * nf); Copy(FAlive, bm.FAlive, nf); Copy(FFlag, bm.FFlag, nf);
    Copy(freeV, bm.freeV, bm.freeV.size()); Copy(freeE, bm.freeE, bm.freeE.size()); Copy(freeL, bm.freeL, bm.freeL.size()); Copy(freeF, bm.freeF, bm.freeF.size());
    for (size_t i = 0; i < r->layers.Uv.size(); i++) Copy(uv[i], r->layers.Uv[i], r->layers.Uv[i].size());
    for (size_t i = 0; i < r->layers.CornerColor.size(); i++) Copy(cornerColor[i], r->layers.CornerColor[i], r->layers.CornerColor[i].size());
    if (r->layers.hasCustomNormal) Copy(customNormal, r->layers.CustomNormal, r->layers.CustomNormal.size());
}
EXPORT void haf_bmesh_free(void* handle) { delete (Result*)handle; }

// FromMeshData: the vertices in order, the edges in the mesh's order (SMOOTH unless sharp), the triangles with their corners' edges
// (SMOOTH unless sharp_face, the material), the face normals (normal_tri_v3) when asked. edgeSharp / faceSharp may be null.
// status: [0] 1 when a face names a corner pair no edge joins ([1] the face, [2] the corner) - the managed twin's exception.
EXPORT void* haf_bmesh_build(const float* positions, int nv, const int* edges, int ne, const uint8_t* edgeSharp, const int* faces, int nf, const uint8_t* faceSharp, const int* faceMaterial, int faceNormals)
{
    Result* r = new Result(); BMV& bm = r->bm;
    bm.VE.resize(nv); bm.VAlive.resize(nv); bm.VCo.resize(3 * (size_t)nv); bm.VNo.resize(3 * (size_t)nv); bm.VFlag.resize(nv);
    bm.EV1.resize(ne); bm.EV2.resize(ne); bm.EL.resize(ne); bm.EDiskNext.resize(2 * (size_t)ne); bm.EDiskPrev.resize(2 * (size_t)ne); bm.EAlive.resize(ne); bm.EFlag.resize(ne);
    size_t nl = 3 * (size_t)nf;
    bm.LV.resize(nl); bm.LE.resize(nl); bm.LF.resize(nl); bm.LNext.resize(nl); bm.LPrev.resize(nl); bm.LRadialNext.resize(nl); bm.LRadialPrev.resize(nl); bm.LAlive.resize(nl);
    bm.FLFirst.resize(nf); bm.FLen.resize(nf); bm.FMat.resize(nf); bm.FNo.resize(3 * (size_t)nf); bm.FAlive.resize(nf); bm.FFlag.resize(nf);
    for (int v = 0; v < nv; v++) bm.VertCreate(positions[3 * v], positions[3 * v + 1], positions[3 * v + 2]);
    // the edge lookup: per lower vertex, the (higher vertex, edge) pairs in edge order; a pair listed twice resolves to the LAST
    std::vector<int> bucketStart(nv + 1, 0);
    std::vector<std::pair<int, int>> bucket(ne); // (higher vertex, original edge ordinal)
    for (int e = 0; e < ne; e++) { int a = edges[2 * e], b = edges[2 * e + 1]; bucketStart[std::min(a, b) + 1]++; }
    for (int v = 0; v < nv; v++) bucketStart[v + 1] += bucketStart[v];
    { std::vector<int> fill(bucketStart.begin(), bucketStart.end() - 1);
      for (int e = 0; e < ne; e++) { int a = edges[2 * e], b = edges[2 * e + 1]; int lo = std::min(a, b); int at = fill[lo]++; bucket[at] = { std::max(a, b), e }; } }
    // A triangle fan can put every spoke in one bucket. Sorting permits logarithmic lookup instead of scanning that
    // whole bucket for every face; sorting equal pairs by edge ordinal retains the dictionary's last assignment.
    for (int v = 0; v < nv; v++) std::sort(bucket.begin() + bucketStart[v], bucket.begin() + bucketStart[v + 1]);
    for (int e = 0; e < ne; e++)
    {
        int a = edges[2 * e], b = edges[2 * e + 1];
        int i = bm.EdgeCreate(a, b);
        bm.EFlag[i] = edgeSharp != nullptr && edgeSharp[e] ? (uint8_t)0 : FlagSmooth;
    }
    auto Lookup = [&](int a, int b) -> int
    {
        int lo = std::min(a, b), hi = std::max(a, b);
        int first = bucketStart[lo], last = bucketStart[lo + 1];
        while (first < last)
        {
            int mid = first + (last - first) / 2;
            if (bucket[mid].first <= hi) first = mid + 1; else last = mid;
        }
        if (first > bucketStart[lo] && bucket[first - 1].first == hi) return bucket[first - 1].second;
        return None;
    };
    int verts[3], fe[3];
    for (int f = 0; f < nf; f++)
    {
        for (int k = 0; k < 3; k++) verts[k] = faces[3 * f + k];
        for (int k = 0; k < 3; k++)
        {
            fe[k] = Lookup(verts[k], verts[(k + 1) % 3]);
            if (fe[k] == None) { r->status[0] = 1; r->status[1] = f; r->status[2] = k; return r; }
        }
        int i = bm.FaceCreate(verts, fe, 3);
        bm.FFlag[i] = faceSharp != nullptr && faceSharp[f] ? (uint8_t)0 : FlagSmooth;
        bm.FMat[i] = faceMaterial[f];
    }
    if (faceNormals)
        for (int f = 0; f < bm.FaceCount; f++)
        {
            int l0 = bm.FLFirst[f], l1 = bm.LNext[l0], l2 = bm.LNext[l1];
            float nx, ny, nz; NormalTri(bm.VCo.data(), bm.LV[l0], bm.LV[l1], bm.LV[l2], nx, ny, nz);
            bm.FNo[3 * f] = nx; bm.FNo[3 * f + 1] = ny; bm.FNo[3 * f + 2] = nz;
        }
    return r;
}

// The weld grouping's test per vertex: uniform[v] = 1 when every corner of the vertex shares one material index (material[v]); a dead
// vertex, one without corners and one whose corners' faces differ are 0.
EXPORT void haf_bmesh_vert_material(const int* VE, const uint8_t* VAlive, const int* EV1, const int* EV2, const int* EL, const int* EDiskNext,
    const int* LV, const int* LE, const int* LF, const int* LNext, const int* LRadialNext, const int* FMat, int vertCount, int* material, uint8_t* uniform)
{
    BMView bm; bm.VE = VE; bm.VAlive = VAlive; bm.EV1 = EV1; bm.EV2 = EV2; bm.EL = EL; bm.EDiskNext = EDiskNext; bm.LV = LV; bm.LE = LE; bm.LF = LF; bm.LNext = LNext; bm.LRadialNext = LRadialNext; bm.FMat = FMat; bm.VertCount = vertCount;
    std::vector<int> loops;
    for (int v = 0; v < vertCount; v++)
    {
        material[v] = 0; uniform[v] = 0;
        if (!VAlive[v]) continue;
        LoopsOfVert(bm, v, loops);
        if (loops.empty()) continue;
        int m0 = FMat[LF[loops[0]]]; bool same = true;
        for (size_t i = 1; i < loops.size() && same; i++) same = FMat[LF[loops[i]]] == m0;
        material[v] = m0; uniform[v] = same ? 1 : 0;
    }
}

// bmesh_find_doubles_by_distance_impl + kdtree_calc_duplicates_cb: duplicates[i] = the index (into verts) of the vertex that verts[i]
// merges into, itself for a cluster's target, -1 for a vertex in no cluster - the managed twin's raw array before its map.
EXPORT void haf_find_doubles(const float* VCo, const int* verts, int n, float dist, int* duplicates)
{
    KdTree tree(std::max(1, n));
    for (int i = 0; i < n; i++) { int v = verts[i]; tree.Insert(i, VCo[3 * v], VCo[3 * v + 1], VCo[3 * v + 2]); }
    tree.BalanceAll();
    for (int i = 0; i < n; i++) duplicates[i] = -1;
    std::vector<int> cluster, stack;
    if (tree.Root != None)
        for (int ni = 0; ni < tree.Len; ni++)
        {
            int nodeIndex = tree.Index[ni];
            if (duplicates[nodeIndex] != -1) continue;
            cluster.clear();
            tree.RangeSearch(tree.Co[3 * ni], tree.Co[3 * ni + 1], tree.Co[3 * ni + 2], dist, stack, [&](int index, float) { if (duplicates[index] == -1) cluster.push_back(index); });
            if (cluster.empty()) continue;
            cluster.push_back(nodeIndex);
            int target = cluster[PickTarget(VCo, verts, cluster)];
            for (int c : cluster) duplicates[c] = target;
        }
}

// bmesh.ops.dissolve_limit over the jobs (vertex and edge index ranges into vs/es, CSR offsets of nJobs + 1), each followed by the
// NORMALS_CALC finish; with skipEmpty a job without elements is passed over (the chunked path). The Python wrapper validates every
// element before an op: a vertex or edge already removed by an earlier job stops the run - status[0] = 1 (vertex) or 2 (edge),
// [1] the job, [2] the position - and the result is NOT to be used. The free lists come in bottom to top and go out the same way.
EXPORT void* haf_dissolve_limit(
    const int* VE, const uint8_t* VAlive, const float* VCo, const float* VNo, const uint8_t* VFlag,
    const int* EV1, const int* EV2, const int* EL, const int* EDiskNext, const int* EDiskPrev, const uint8_t* EAlive, const uint8_t* EFlag,
    const int* LV, const int* LE, const int* LF, const int* LNext, const int* LPrev, const int* LRadialNext, const int* LRadialPrev, const uint8_t* LAlive,
    const int* FLFirst, const int* FLen, const int* FMat, const float* FNo, const uint8_t* FAlive, const uint8_t* FFlag, const int* counts,
    const int* freeV, int nFreeV, const int* freeE, int nFreeE, const int* freeL, int nFreeL, const int* freeF, int nFreeF,
    int nUv, float** uv, const int* uvLengths, int nCornerColor, uint8_t** cornerColor, const int* cornerColorLengths, const short* customNormal, int customNormalLength,
    float angleLimit, int nJobs, const int* vsOffsets, const int* vs, const int* esOffsets, const int* es, int skipEmpty)
{
    LoadTrig();
    Result* r = new Result();
    if (!ucrt_asinf || !ucrt_cosf) { r->status[0] = 3; return r; }
    BMV& bm = r->bm;
    CopyIn(bm, VE, VAlive, VCo, VNo, VFlag, EV1, EV2, EL, EDiskNext, EDiskPrev, EAlive, EFlag, LV, LE, LF, LNext, LPrev, LRadialNext, LRadialPrev, LAlive, FLFirst, FLen, FMat, FNo, FAlive, FFlag, counts);
    bm.freeV.assign(freeV, freeV + nFreeV); bm.freeE.assign(freeE, freeE + nFreeE); bm.freeL.assign(freeL, freeL + nFreeL); bm.freeF.assign(freeF, freeF + nFreeF);
    Layers& layers = r->layers;
    for (int i = 0; i < nUv; i++) layers.Uv.emplace_back(uv[i], uv[i] + uvLengths[i]);
    for (int i = 0; i < nCornerColor; i++) layers.CornerColor.emplace_back(cornerColor[i], cornerColor[i] + cornerColorLengths[i]);
    if (customNormal != nullptr) { layers.hasCustomNormal = true; layers.CustomNormal.assign(customNormal, customNormal + customNormalLength); }
    Dissolve d(bm, layers);
    for (int j = 0; j < nJobs; j++)
    {
        int nvs = vsOffsets[j + 1] - vsOffsets[j], nes = esOffsets[j + 1] - esOffsets[j];
        if (skipEmpty && nvs == 0 && nes == 0) continue;
        const int* jv = vs + vsOffsets[j]; const int* je = es + esOffsets[j];
        for (int i = 0; i < nvs; i++) if (!bm.VAlive[jv[i]]) { r->status[0] = 1; r->status[1] = j; r->status[2] = i; return r; }
        for (int i = 0; i < nes; i++) if (!bm.EAlive[je[i]]) { r->status[0] = 2; r->status[1] = j; r->status[2] = i; return r; }
        d.DissolveLimit(angleLimit, jv, nvs, je, nes);
        d.FaceNormalsUpdateAll();
    }
    return r;
}
