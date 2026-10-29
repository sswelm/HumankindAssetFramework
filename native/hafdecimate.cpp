// hafdecimate.cpp - native port of BlenderDecimate.Collapse (HAF, editor/BlenderDecimate.cs) over the BMesh arrays
// (editor/BMesh.cs), step for step: the double quadrics, BLI_heap with its insertion-order ties, the degenerate checks,
// bm_edge_collapse with the disk/radial cycle edits, and the custom-data walk (UVs, byte colours, vertex groups).
// The C# arrays are pinned and edited IN PLACE; the collapse creates no element, so the free lists never matter and the
// arrays never grow. Compile with -ffp-contract=off (no fused multiply-add: the C# and Blender round every product).
#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>
#include <algorithm>
#define EXPORT extern "C" __declspec(dllexport)

static const int None = -1;
static const uint8_t FlagSelect = 1, FlagSmooth = 8, FlagTag = 16;
static const float CostInvalid = 3.402823466e+38f;   // FLT_MAX
static const double OptimizeEps = 1e-8;
static const float BoundaryPreserveWeight = 100.0f;
static const float TopologyFallbackEps = 1e-12f;
static const float FltEpsilon = 1.1920929e-7f;

static inline float Sqrtf(float d) { return (float)std::sqrt((double)d); }
static inline float Dot(float ax, float ay, float az, float bx, float by, float bz) { return (float)((float)((float)(ax * bx) + (float)(ay * by)) + (float)(az * bz)); }
static inline float LenSquared3(float x, float y, float z) { return Dot(x, y, z, x, y, z); }
static float NormalizeC(float& x, float& y, float& z)
{
    float d = Dot(x, y, z, x, y, z);
    if (d > 1.0e-35f) { d = Sqrtf(d); float f = (float)(1.0f / d); x = (float)(x * f); y = (float)(y * f); z = (float)(z * f); }
    else { x = y = z = 0.f; d = 0.f; }
    return d;
}

// ---------------------------------------------------------------- the BMesh over the pinned arrays
struct BM
{
    int* VE; uint8_t* VAlive; float* VCo; float* VNo; uint8_t* VFlag; int VertCount, TotVert;
    int *EV1, *EV2, *EL, *EDiskNext, *EDiskPrev; uint8_t* EAlive; uint8_t* EFlag; int EdgeCount, TotEdge;
    int *LV, *LE, *LF, *LNext, *LPrev, *LRadialNext, *LRadialPrev; uint8_t* LAlive; int LoopCount, TotLoop;
    int *FLFirst, *FLen, *FMat; float* FNo; uint8_t* FAlive; uint8_t* FFlag; int FaceCount, TotFace;
    std::vector<int>* killed;   // per-pool death order, returned for the managed BMesh's free lists

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
    void EdgeVertSwap(int e, int vDst, int vSrc)
    {
        if (EL[e] != None)
        {
            int lIter = EL[e], lFirst = lIter;
            do
            {
                if (LV[lIter] == vSrc) LV[lIter] = vDst;
                else if (LV[LNext[lIter]] == vSrc) LV[LNext[lIter]] = vDst;
            } while ((lIter = LRadialNext[lIter]) != lFirst);
        }
        DiskVertReplace(e, vDst, vSrc);
    }
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
    int RadialFaceVertCount(int l, int v) const { int count = 0, lIter = l; do { if (LV[lIter] == v) count++; } while ((lIter = LRadialNext[lIter]) != l); return count; }
    bool RadialFaceVertCheck(int l, int v) const { int lIter = l; do { if (LV[lIter] == v) return true; } while ((lIter = LRadialNext[lIter]) != l); return false; }
    int RadialFaceLoopFindFirst(int l, int v) const { int lIter = l; do { if (LV[lIter] == v) return lIter; } while ((lIter = LRadialNext[lIter]) != l); return None; }
    int RadialFaceLoopFindNext(int l, int v) const { int lIter = LRadialNext[l]; do { if (LV[lIter] == v) return lIter; } while ((lIter = LRadialNext[lIter]) != l); return l; }
    int DiskFaceVertCount(int v) const
    {
        int count = 0;
        if (VE[v] != None) { int eFirst = VE[v], eIter = eFirst; do { if (EL[eIter] != None) count += RadialFaceVertCount(EL[eIter], v); } while ((eIter = DiskEdgeNext(eIter, v)) != eFirst); }
        return count;
    }
    int DiskFaceLoopFindFirst(int e, int v) const
    {
        int eIter = e;
        do { if (EL[eIter] != None) return LV[EL[eIter]] == v ? EL[eIter] : LNext[EL[eIter]]; } while ((eIter = DiskEdgeNext(eIter, v)) != e);
        return None;
    }
    int DiskFaceEdgeFindNext(int e, int v) const
    {
        int eFind = DiskEdgeNext(e, v);
        do { if (EL[eFind] != None && RadialFaceVertCheck(EL[eFind], v)) return eFind; } while ((eFind = DiskEdgeNext(eFind, v)) != e);
        return e;
    }
    void LoopsOfVert(int v, std::vector<int>& into) const
    {
        into.clear();
        int count = DiskFaceVertCount(v);
        if (count == 0) return;
        int lFirst = DiskFaceLoopFindFirst(VE[v], v);
        int eNext = LE[lFirst];
        int lNext = lFirst;
        while (count > 0)
        {
            int lCurr = lNext;
            count--;
            lNext = RadialFaceLoopFindNext(lNext, v);
            if (lNext == lFirst)
            {
                eNext = DiskFaceEdgeFindNext(eNext, v);
                lFirst = RadialFaceLoopFindFirst(EL[eNext], v);
                lNext = lFirst;
            }
            into.push_back(lCurr);
        }
    }
    bool VertInEdge(int e, int v) const { return EV1[e] == v || EV2[e] == v; }
    int EdgeOtherVert(int e, int v) const { return EV1[e] == v ? EV2[e] : EV2[e] == v ? EV1[e] : None; }
    bool EdgeIsBoundary(int e) const { int l = EL[e]; return l != None && LRadialNext[l] == l; }
    bool EdgeIsManifold(int e) const { int l = EL[e]; return l != None && LRadialNext[l] != l && LRadialNext[LRadialNext[l]] == l; }
    bool EdgeLoopPair(int e, int& la, int& lb) const
    {
        la = EL[e];
        if (la != None) { lb = LRadialNext[la]; if (lb != la && LRadialNext[lb] == la) return true; }
        la = lb = None;
        return false;
    }
    int EdgeOtherLoop(int e, int l) const
    {
        int lOther = LE[l] == e ? l : LPrev[l];
        lOther = LRadialNext[lOther];
        if (LV[lOther] == LV[l]) {}
        else if (LV[LNext[lOther]] == LV[l]) lOther = LNext[lOther];
        else return None;   // the C# throws here; the collapse never reaches it on a validated mesh
        return lOther;
    }
    int VertStepFanLoop(int l, int& eStep) const
    {
        int ePrev = eStep, eNext;
        if (LE[l] == ePrev) eNext = LE[LPrev[l]];
        else if (LE[LPrev[l]] == ePrev) eNext = LE[l];
        else return None;
        if (EdgeIsManifold(eNext)) { eStep = eNext; return EdgeOtherLoop(eNext, l); }
        return None;
    }
    void KillOnlyVert(int v) { VAlive[v] = 0; TotVert--; killed[0].push_back(v); }
    void KillOnlyEdge(int e) { EAlive[e] = 0; TotEdge--; killed[1].push_back(e); }
    void KillOnlyFace(int f) { FAlive[f] = 0; TotFace--; killed[3].push_back(f); }
    void KillOnlyLoop(int l) { LAlive[l] = 0; TotLoop--; killed[2].push_back(l); }
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
    bool VertSplice(int vDst, int vSrc)
    {
        if (vSrc == vDst) return false;
        int e;
        while ((e = VE[vSrc]) != None) EdgeVertSwap(e, vDst, vSrc);
        VertKill(vSrc);
        return true;
    }
    bool EdgeSplice(int eDst, int eSrc)
    {
        if (!VertInEdge(eSrc, EV1[eDst]) || !VertInEdge(eSrc, EV2[eDst])) return false;
        while (EL[eSrc] != None) { int l = EL[eSrc]; RadialLoopRemove(eSrc, l); RadialLoopAppend(eDst, l); }
        EdgeKill(eSrc);
        return true;
    }
};

// ---------------------------------------------------------------- the custom data
struct Def { std::vector<int> nrs; std::vector<float> ws; bool present = false; };
struct MeshData
{
    std::vector<float*> Uv;          // 2 per loop
    std::vector<uint8_t*> CornerColor;   // 4 per loop
    std::vector<uint8_t*> PointColor;    // 4 per vertex
    std::vector<Def>* Def = nullptr;     // per vertex, or null without groups
    bool LoopHasMath() const { return !Uv.empty() || !CornerColor.empty(); }
    bool VertHasInterp() const { return Def != nullptr || !PointColor.empty(); }
};

// ---------------------------------------------------------------- BLI_quadric (double)
struct Quadric
{
    double a2 = 0, ab = 0, ac = 0, ad = 0, b2 = 0, bc = 0, bd = 0, c2 = 0, cd = 0, d2 = 0;
    static Quadric FromPlane(double v0, double v1, double v2, double v3)
    {
        Quadric q;
        q.a2 = v0 * v0; q.b2 = v1 * v1; q.c2 = v2 * v2;
        q.ab = v0 * v1; q.ac = v0 * v2; q.bc = v1 * v2;
        q.ad = v0 * v3; q.bd = v1 * v3; q.cd = v2 * v3;
        q.d2 = v3 * v3;
        return q;
    }
    void Add(const Quadric& b) { a2 += b.a2; ab += b.ab; ac += b.ac; ad += b.ad; b2 += b.b2; bc += b.bc; bd += b.bd; c2 += b.c2; cd += b.cd; d2 += b.d2; }
    static Quadric Sum(const Quadric& a, const Quadric& b) { Quadric r = a; r.Add(b); return r; }
    void Mul(double s) { a2 *= s; ab *= s; ac *= s; ad *= s; b2 *= s; bc *= s; bd *= s; c2 *= s; cd *= s; d2 *= s; }
    double Evaluate(double v0, double v1, double v2) const
    {
        double v00 = v0 * v0, v01 = v0 * v1, v02 = v0 * v2;
        double v11 = v1 * v1, v12 = v1 * v2;
        double v22 = v2 * v2;
        return ((a2 * v00) + (ab * 2 * v01) + (ac * 2 * v02) + (ad * 2 * v0) +
                (b2 * v11) + (bc * 2 * v12) + (bd * 2 * v1) +
                (c2 * v22) + (cd * 2 * v2) +
                (d2));
    }
    bool Optimize(double& x, double& y, double& z, double epsilon) const
    {
        double det = (a2 * (b2 * c2 - bc * bc) -
                      ab * (ab * c2 - ac * bc) +
                      ac * (ab * bc - ac * b2));
        if (std::fabs(det) > epsilon)
        {
            double invdet = 1.0 / det;
            double m00 = (b2 * c2 - bc * bc) * invdet;
            double m10 = (bc * ac - ab * c2) * invdet;
            double m20 = (ab * bc - b2 * ac) * invdet;
            double m01 = (ac * bc - ab * c2) * invdet;
            double m11 = (a2 * c2 - ac * ac) * invdet;
            double m21 = (ab * ac - a2 * bc) * invdet;
            double m02 = (ab * bc - ac * b2) * invdet;
            double m12 = (ac * ab - a2 * bc) * invdet;
            double m22 = (a2 * b2 - ab * ab) * invdet;
            double t0 = ad, t1 = bd, t2 = cd;
            x = -(m00 * t0 + m10 * t1 + m20 * t2);
            y = -(m01 * t0 + m11 * t1 + m21 * t2);
            z = -(m02 * t0 + m12 * t1 + m22 * t2);
            return true;
        }
        x = y = z = 0;
        return false;
    }
};

// ---------------------------------------------------------------- BLI_heap
struct Heap
{
    std::vector<float> nodeValue; std::vector<int> nodeIndex, nodePtr, tree;
    int nodeCount = 0, freeHead = -1, size = 0;
    explicit Heap(int reserve) { int cap = std::max(1, reserve); tree.resize(cap); nodeValue.resize(cap); nodeIndex.resize(cap); nodePtr.resize(cap); }
    bool IsEmpty() const { return size == 0; }
    float TopValue() const { return nodeValue[tree[0]]; }
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
    void InsertOrUpdate(int& node, float value, int ptr) { if (node == -1) node = Insert(value, ptr); else UpdatePtr(node, value, ptr); }
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

// ---------------------------------------------------------------- the quadrics and the costs
template <class PollFn> static bool BuildQuadrics(BM& bm, std::vector<Quadric>& vq, PollFn poll)
{
    for (int f = 0; f < bm.FaceCount; f++)
    {
        if ((f & 1023) == 0 && poll()) return false;
        if (!bm.FAlive[f]) continue;
        float cx = 0.f, cy = 0.f, cz = 0.f;
        int lFirst = bm.FLFirst[f], l = lFirst;
        do { int v = bm.LV[l]; cx = (float)(cx + bm.VCo[3 * v]); cy = (float)(cy + bm.VCo[3 * v + 1]); cz = (float)(cz + bm.VCo[3 * v + 2]); } while ((l = bm.LNext[l]) != lFirst);
        float inv = (float)(1.0f / (float)bm.FLen[f]);
        cx = (float)(cx * inv); cy = (float)(cy * inv); cz = (float)(cz * inv);
        double p0 = bm.FNo[3 * f], p1 = bm.FNo[3 * f + 1], p2 = bm.FNo[3 * f + 2];
        double p3 = -(p0 * (double)cx + p1 * (double)cy + p2 * (double)cz);
        Quadric q = Quadric::FromPlane(p0, p1, p2, p3);
        l = lFirst;
        do { vq[bm.LV[l]].Add(q); } while ((l = bm.LNext[l]) != lFirst);
    }
    for (int e = 0; e < bm.EdgeCount; e++)
    {
        if ((e & 1023) == 0 && poll()) return false;
        if (!bm.EAlive[e] || !bm.EdgeIsBoundary(e)) continue;
        int v1 = bm.EV1[e], v2 = bm.EV2[e];
        float evx = (float)(bm.VCo[3 * v2] - bm.VCo[3 * v1]), evy = (float)(bm.VCo[3 * v2 + 1] - bm.VCo[3 * v1 + 1]), evz = (float)(bm.VCo[3 * v2 + 2] - bm.VCo[3 * v1 + 2]);
        int f = bm.LF[bm.EL[e]];
        float fnx = bm.FNo[3 * f], fny = bm.FNo[3 * f + 1], fnz = bm.FNo[3 * f + 2];
        float epx = (float)((float)(evy * fnz) - (float)(evz * fny));
        float epy = (float)((float)(evz * fnx) - (float)(evx * fnz));
        float epz = (float)((float)(evx * fny) - (float)(evy * fnx));
        double d0 = epx, d1 = epy, d2 = epz;
        double d = d0 * d0 + d1 * d1 + d2 * d2;
        if (d > 1.0e-35) { d = std::sqrt(d); double mul = 1.0 / d; d0 *= mul; d1 *= mul; d2 *= mul; }
        else { d0 = d1 = d2 = 0; d = 0.0; }
        if (d > (double)FltEpsilon)
        {
            float cx = (float)(0.5f * (float)(bm.VCo[3 * v1] + bm.VCo[3 * v2]));
            float cy = (float)(0.5f * (float)(bm.VCo[3 * v1 + 1] + bm.VCo[3 * v2 + 1]));
            float cz = (float)(0.5f * (float)(bm.VCo[3 * v1 + 2] + bm.VCo[3 * v2 + 2]));
            double d3 = -(d0 * (double)cx + d1 * (double)cy + d2 * (double)cz);
            Quadric q = Quadric::FromPlane(d0, d1, d2, d3);
            q.Mul((double)BoundaryPreserveWeight);
            vq[v1].Add(q);
            vq[v2].Add(q);
        }
    }
    return true;
}

static void CalcTargetCoDb(const BM& bm, int e, const std::vector<Quadric>& vq, double& x, double& y, double& z)
{
    Quadric q = Quadric::Sum(vq[bm.EV1[e]], vq[bm.EV2[e]]);
    if (q.Optimize(x, y, z, OptimizeEps)) return;
    int v1 = bm.EV1[e], v2 = bm.EV2[e];
    x = 0.5 * ((double)bm.VCo[3 * v1] + (double)bm.VCo[3 * v2]);
    y = 0.5 * ((double)bm.VCo[3 * v1 + 1] + (double)bm.VCo[3 * v2 + 1]);
    z = 0.5 * ((double)bm.VCo[3 * v1 + 2] + (double)bm.VCo[3 * v2 + 2]);
}

static float TopologyCostSquared(const BM& bm, int e)
{
    int v1 = bm.EV1[e], v2 = bm.EV2[e];
    float dot = Dot(bm.VNo[3 * v1], bm.VNo[3 * v1 + 1], bm.VNo[3 * v1 + 2], bm.VNo[3 * v2], bm.VNo[3 * v2 + 1], bm.VNo[3 * v2 + 2]);
    float dx = (float)(bm.VCo[3 * v2] - bm.VCo[3 * v1]), dy = (float)(bm.VCo[3 * v2 + 1] - bm.VCo[3 * v1 + 1]), dz = (float)(bm.VCo[3 * v2 + 2] - bm.VCo[3 * v1 + 2]);
    float negLen = -LenSquared3(dx, dy, dz);
    float denom = negLen < -FltEpsilon ? negLen : -FltEpsilon;
    return (float)(std::fabs(dot) / denom);
}

static void BuildEdgeCostSingle(const BM& bm, int e, const std::vector<Quadric>& vq, Heap& heap, std::vector<int>& table)
{
    bool ok;
    if (bm.EdgeIsBoundary(e)) ok = bm.FLen[bm.LF[bm.EL[e]]] == 3;
    else if (bm.EdgeIsManifold(e)) ok = bm.FLen[bm.LF[bm.EL[e]]] == 3 && bm.FLen[bm.LF[bm.LRadialNext[bm.EL[e]]]] == 3;
    else ok = false;
    if (!ok)
    {
        if (table[e] != -1) heap.Remove(table[e]);
        table[e] = -1;
        return;
    }
    double ox, oy, oz; CalcTargetCoDb(bm, e, vq, ox, oy, oz);
    float cost = (float)(vq[bm.EV1[e]].Evaluate(ox, oy, oz) + vq[bm.EV2[e]].Evaluate(ox, oy, oz));
    cost = std::fabs(cost);
    if (cost < TopologyFallbackEps) cost = (float)(TopologyCostSquared(bm, e) - cost);
    heap.InsertOrUpdate(table[e], cost, e);
}

static void InvalidEdgeCostSingle(int e, Heap& heap, std::vector<int>& table) { table[e] = heap.Insert(CostInvalid, e); }

// ---------------------------------------------------------------- the degenerate checks
static inline void SetTag(uint8_t* flags, int i, bool enable) { if (enable) flags[i] |= FlagTag; else flags[i] &= (uint8_t)~FlagTag; }
static void EdgeTag(BM& bm, int e, bool enable)
{
    SetTag(bm.VFlag, bm.EV1[e], enable); SetTag(bm.VFlag, bm.EV2[e], enable);
    int l = bm.EL[e];
    if (l != None)
    {
        SetTag(bm.FFlag, bm.LF[l], enable);
        if (l != bm.LRadialNext[l]) SetTag(bm.FFlag, bm.LF[bm.LRadialNext[l]], enable);
    }
}
static bool EdgeTagTest(const BM& bm, int e)
{
    if ((bm.VFlag[bm.EV1[e]] & FlagTag) != 0 || (bm.VFlag[bm.EV2[e]] & FlagTag) != 0) return true;
    int l = bm.EL[e];
    return l != None && ((bm.FFlag[bm.LF[l]] & FlagTag) != 0 || (l != bm.LRadialNext[l] && (bm.FFlag[bm.LF[bm.LRadialNext[l]]] & FlagTag) != 0));
}
static inline bool ManifoldOrBoundary(const BM& bm, int l) { return l != None && bm.LRadialNext[bm.LRadialNext[l]] == l; }

static bool IsDegenerateTopology(BM& bm, int eFirst)
{
    int v1 = bm.EV1[eFirst], v2 = bm.EV2[eFirst];
    int eIter = eFirst;
    do { if (!ManifoldOrBoundary(bm, bm.EL[eIter])) return true; EdgeTag(bm, eIter, false); } while ((eIter = bm.DiskEdgeNext(eIter, v1)) != eFirst);
    eIter = eFirst;
    do { if (!ManifoldOrBoundary(bm, bm.EL[eIter])) return true; EdgeTag(bm, eIter, false); } while ((eIter = bm.DiskEdgeNext(eIter, v2)) != eFirst);
    eIter = eFirst;
    do { EdgeTag(bm, eIter, true); } while ((eIter = bm.DiskEdgeNext(eIter, v1)) != eFirst);
    {
        int lRadial = bm.EL[eFirst];
        int lFace = lRadial;
        bm.FFlag[bm.LF[lFace]] &= (uint8_t)~FlagTag;
        bm.VFlag[bm.LV[lFace = lRadial]] &= (uint8_t)~FlagTag;
        bm.VFlag[bm.LV[lFace = bm.LNext[lFace]]] &= (uint8_t)~FlagTag;
        bm.VFlag[bm.LV[bm.LNext[lFace]]] &= (uint8_t)~FlagTag;
        lFace = bm.LRadialNext[lRadial];
        if (lRadial != lFace)
        {
            bm.FFlag[bm.LF[lFace]] &= (uint8_t)~FlagTag;
            bm.VFlag[bm.LV[lFace = bm.LRadialNext[lRadial]]] &= (uint8_t)~FlagTag;
            bm.VFlag[bm.LV[lFace = bm.LNext[lFace]]] &= (uint8_t)~FlagTag;
            bm.VFlag[bm.LV[bm.LNext[lFace]]] &= (uint8_t)~FlagTag;
        }
    }
    eIter = eFirst;
    do { if (EdgeTagTest(bm, eIter)) return true; } while ((eIter = bm.DiskEdgeNext(eIter, v2)) != eFirst);
    return false;
}

static bool IsDegenerateFlip(const BM& bm, int e, float ox, float oy, float oz, std::vector<int>& loops)
{
    for (int i = 0; i < 2; i++)
    {
        int v = i == 0 ? bm.EV1[e] : bm.EV2[e];
        bm.LoopsOfVert(v, loops);
        for (int l : loops)
        {
            if (bm.LE[l] == e || bm.LE[bm.LPrev[l]] == e) continue;
            int vp = bm.LV[bm.LPrev[l]], vn = bm.LV[bm.LNext[l]];
            float px = bm.VCo[3 * vp], py = bm.VCo[3 * vp + 1], pz = bm.VCo[3 * vp + 2];
            float otx = (float)(px - bm.VCo[3 * vn]), oty = (float)(py - bm.VCo[3 * vn + 1]), otz = (float)(pz - bm.VCo[3 * vn + 2]);
            float exx = (float)(px - bm.VCo[3 * v]), exy = (float)(py - bm.VCo[3 * v + 1]), exz = (float)(pz - bm.VCo[3 * v + 2]);
            float opx = (float)(px - ox), opy = (float)(py - oy), opz = (float)(pz - oz);
            float cex = (float)((float)(oty * exz) - (float)(otz * exy)), cey = (float)((float)(otz * exx) - (float)(otx * exz)), cez = (float)((float)(otx * exy) - (float)(oty * exx));
            float cox = (float)((float)(oty * opz) - (float)(otz * opy)), coy = (float)((float)(otz * opx) - (float)(otx * opz)), coz = (float)((float)(otx * opy) - (float)(oty * opx));
            float lhs = Dot(cex, cey, cez, cox, coy, coz);
            float rhs = (float)((float)(LenSquared3(cex, cey, cez) + LenSquared3(cox, coy, coz)) * 0.01f);
            if (lhs <= rhs) return true;
        }
    }
    return false;
}

// ---------------------------------------------------------------- the custom data
static bool UvEqual(const float* uv, int a, int b)
{
    float dx = (float)(uv[2 * a] - uv[2 * b]), dy = (float)(uv[2 * a + 1] - uv[2 * b + 1]);
    return (float)((float)(dx * dx) + (float)(dy * dy)) < 0.00001f;
}
static void UvInterp(float* uv, int src0, int src1, float w0, float w1, int dst)
{
    float rx = 0.f, ry = 0.f;
    rx = (float)(rx + (float)(uv[2 * src0] * w0)); ry = (float)(ry + (float)(uv[2 * src0 + 1] * w0));
    rx = (float)(rx + (float)(uv[2 * src1] * w1)); ry = (float)(ry + (float)(uv[2 * src1 + 1] * w1));
    uv[2 * dst] = rx; uv[2 * dst + 1] = ry;
}
static bool ColorEqual(const uint8_t* c, int a, int b)
{
    float r = (float)(c[4 * a] - c[4 * b]), g = (float)(c[4 * a + 1] - c[4 * b + 1]), bb = (float)(c[4 * a + 2] - c[4 * b + 2]), al = (float)(c[4 * a + 3] - c[4 * b + 3]);
    return (float)((float)((float)((float)(r * r) + (float)(g * g)) + (float)(bb * bb)) + (float)(al * al)) < 0.001f;
}
static uint8_t RoundToUcharClamp(float a)
{
    float r = (float)std::floor((double)(float)(a + 0.5f));
    if (r <= 0.f) return 0;
    if (r >= 255.f) return 255;
    return (uint8_t)r;
}
static void ColorInterp(uint8_t* c, int src0, int src1, float w0, float w1, int dst)
{
    float r = 0.f, g = 0.f, b = 0.f, a = 0.f;
    r = (float)(r + (float)(c[4 * src0] * w0)); g = (float)(g + (float)(c[4 * src0 + 1] * w0)); b = (float)(b + (float)(c[4 * src0 + 2] * w0)); a = (float)(a + (float)(c[4 * src0 + 3] * w0));
    r = (float)(r + (float)(c[4 * src1] * w1)); g = (float)(g + (float)(c[4 * src1 + 1] * w1)); b = (float)(b + (float)(c[4 * src1 + 2] * w1)); a = (float)(a + (float)(c[4 * src1 + 3] * w1));
    c[4 * dst] = RoundToUcharClamp(r); c[4 * dst + 1] = RoundToUcharClamp(g); c[4 * dst + 2] = RoundToUcharClamp(b); c[4 * dst + 3] = RoundToUcharClamp(a);
}
static void DeformInterp(MeshData& d, int src0, int src1, float w0, float w1, int dst)
{
    std::vector<int> nrs; std::vector<float> ws;
    auto Take = [&](int src, float iw)
    {
        const Def& s = (*d.Def)[src];
        if (!s.present) return;
        for (size_t j = 0; j < s.nrs.size(); j++)
        {
            float weight = (float)(s.ws[j] * iw);
            if (weight == 0.0f) continue;
            int k = -1; for (size_t i = 0; i < nrs.size(); i++) if (nrs[i] == s.nrs[j]) { k = (int)i; break; }
            if (k >= 0) ws[k] = (float)(ws[k] + weight);
            else { nrs.insert(nrs.begin(), s.nrs[j]); ws.insert(ws.begin(), weight); }
        }
    };
    Take(src0, w0); Take(src1, w1);
    for (size_t i = 0; i < ws.size(); i++) ws[i] = std::min(ws[i], 1.0f);
    Def& o = (*d.Def)[dst]; o.nrs = nrs; o.ws = ws; o.present = true;
}
static void CopyVertData(MeshData& d, int src, int dst)
{
    if (d.Def != nullptr) (*d.Def)[dst] = (*d.Def)[src];
    for (uint8_t* c : d.PointColor) for (int k = 0; k < 4; k++) c[4 * dst + k] = c[4 * src + k];
}
static void InterpFromVerts(MeshData& d, int vSrc1, int vSrc2, int vDst, float fac)
{
    if (fac <= 0.0f) { if (vSrc1 != vDst) CopyVertData(d, vSrc1, vDst); }
    else if (fac >= 1.0f) { if (vSrc2 != vDst) CopyVertData(d, vSrc2, vDst); }
    else
    {
        float w0 = (float)(1.0f - fac), w1 = fac;
        if (d.Def != nullptr) DeformInterp(d, vSrc1, vSrc2, w0, w1, vDst);
        for (uint8_t* c : d.PointColor) ColorInterp(c, vSrc1, vSrc2, w0, w1, vDst);
    }
}
static void CollapseLoopCustomData(const BM& bm, MeshData& d, int l, int vClear, int vOther, float fac)
{
    bool isManifold = bm.EdgeIsManifold(bm.LE[l]);
    int lClear, lOther;
    if (bm.LV[l] == vClear) { lClear = l; lOther = bm.LNext[l]; }
    else { lClear = bm.LNext[l]; lOther = l; }
    for (int side = 0; side < 2; side++)
    {
        int fExit = isManifold ? bm.LF[bm.LRadialNext[l]] : None;
        int ePrev = bm.LE[l];
        int lFirst, src0, src1; float w0, w1;
        if (side == 0) { lFirst = lClear; src0 = lClear; src1 = lOther; w0 = fac; w1 = (float)(1.0f - fac); }
        else { lFirst = lOther; src0 = lOther; src1 = lClear; w0 = (float)(1.0f - fac); w1 = fac; }
        int lIter = lFirst;
        while ((lIter = bm.VertStepFanLoop(lIter, ePrev)) != lFirst && lIter != None)
        {
            if (fExit != None && fExit == bm.LF[lIter]) break;
            for (float* uv : d.Uv) if (UvEqual(uv, src0, lIter)) UvInterp(uv, src0, src1, w0, w1, lIter);
            for (uint8_t* c : d.CornerColor) if (ColorEqual(c, src0, lIter)) ColorInterp(c, src0, src1, w0, w1, lIter);
        }
    }
}

// ---------------------------------------------------------------- the collapse
static bool EdgeCollapse(BM& bm, MeshData& d, int eClear, int vClear, int* rEClearOther, float fac)
{
    int vOther = bm.EdgeOtherVert(eClear, vClear);
    if (bm.EdgeIsManifold(eClear))
    {
        int la, lb; bm.EdgeLoopPair(eClear, la, lb);
        int ea0, ea1, eb0, eb1;
        if (bm.VertInEdge(bm.LE[bm.LPrev[la]], vClear)) { ea0 = bm.LE[bm.LPrev[la]]; ea1 = bm.LE[bm.LNext[la]]; }
        else { ea1 = bm.LE[bm.LPrev[la]]; ea0 = bm.LE[bm.LNext[la]]; }
        if (bm.VertInEdge(bm.LE[bm.LPrev[lb]], vClear)) { eb0 = bm.LE[bm.LPrev[lb]]; eb1 = bm.LE[bm.LNext[lb]]; }
        else { eb1 = bm.LE[bm.LPrev[lb]]; eb0 = bm.LE[bm.LNext[lb]]; }
        if (ea0 == eb0 || ea0 == eb1 || ea1 == eb0 || ea1 == eb1) return false;
        rEClearOther[0] = ea0; rEClearOther[1] = eb0;
        if (d.VertHasInterp()) InterpFromVerts(d, vOther, vClear, vOther, fac);
        if (d.LoopHasMath())
        {
            CollapseLoopCustomData(bm, d, bm.EL[eClear], vClear, vOther, fac);
            CollapseLoopCustomData(bm, d, bm.LRadialNext[bm.EL[eClear]], vClear, vOther, fac);
        }
        bm.EdgeKill(eClear);
        bm.VFlag[vOther] |= bm.VFlag[vClear];
        bm.VertSplice(vOther, vClear);
        bm.EFlag[ea1] |= bm.EFlag[ea0];
        bm.EFlag[eb1] |= bm.EFlag[eb0];
        bm.EdgeSplice(ea1, ea0);
        bm.EdgeSplice(eb1, eb0);
        return true;
    }
    if (bm.EdgeIsBoundary(eClear))
    {
        int la = bm.EL[eClear];
        int ea0, ea1;
        if (bm.VertInEdge(bm.LE[bm.LPrev[la]], vClear)) { ea0 = bm.LE[bm.LPrev[la]]; ea1 = bm.LE[bm.LNext[la]]; }
        else { ea1 = bm.LE[bm.LPrev[la]]; ea0 = bm.LE[bm.LNext[la]]; }
        rEClearOther[0] = ea0; rEClearOther[1] = -1;
        if (d.VertHasInterp()) InterpFromVerts(d, vOther, vClear, vOther, fac);
        if (d.LoopHasMath()) CollapseLoopCustomData(bm, d, bm.EL[eClear], vClear, vOther, fac);
        bm.EdgeKill(eClear);
        bm.VFlag[vOther] |= bm.VFlag[vClear];
        bm.VertSplice(vOther, vClear);
        bm.EFlag[ea1] |= bm.EFlag[ea0];
        bm.EdgeSplice(ea1, ea0);
        return true;
    }
    return false;
}

static bool DecimEdgeCollapse(BM& bm, MeshData& d, int e, std::vector<Quadric>& vq, Heap& heap, std::vector<int>& table, std::vector<int>& loops, int* eClearOther)
{
    int vOther = bm.EV1[e];
    int vOtherIndex = bm.EV1[e], vClearIndex = bm.EV2[e];
    float vcnx = bm.VNo[3 * vClearIndex], vcny = bm.VNo[3 * vClearIndex + 1], vcnz = bm.VNo[3 * vClearIndex + 2];
    if (IsDegenerateTopology(bm, e)) { InvalidEdgeCostSingle(e, heap, table); return false; }
    double odx, ody, odz; CalcTargetCoDb(bm, e, vq, odx, ody, odz);
    float ox = (float)odx, oy = (float)ody, oz = (float)odz;
    if (IsDegenerateFlip(bm, e, ox, oy, oz, loops)) { InvalidEdgeCostSingle(e, heap, table); return false; }
    int v1 = bm.EV1[e], v2 = bm.EV2[e];
    float fac;
    bool same = std::fabs((float)(bm.VCo[3 * v1] - bm.VCo[3 * v2])) <= FltEpsilon && std::fabs((float)(bm.VCo[3 * v1 + 1] - bm.VCo[3 * v2 + 1])) <= FltEpsilon && std::fabs((float)(bm.VCo[3 * v1 + 2] - bm.VCo[3 * v2 + 2])) <= FltEpsilon;
    if (!same)
    {
        float ux = (float)(bm.VCo[3 * v2] - bm.VCo[3 * v1]), uy = (float)(bm.VCo[3 * v2 + 1] - bm.VCo[3 * v1 + 1]), uz = (float)(bm.VCo[3 * v2 + 2] - bm.VCo[3 * v1 + 2]);
        float hx = (float)(ox - bm.VCo[3 * v1]), hy = (float)(oy - bm.VCo[3 * v1 + 1]), hz = (float)(oz - bm.VCo[3 * v1 + 2]);
        float dot = LenSquared3(ux, uy, uz);
        fac = dot > 0.0f ? (float)(Dot(ux, uy, uz, hx, hy, hz) / dot) : 0.0f;
    }
    else fac = 0.5f;
    if (EdgeCollapse(bm, d, e, bm.EV2[e], eClearOther, fac))
    {
        bm.VCo[3 * vOther] = ox; bm.VCo[3 * vOther + 1] = oy; bm.VCo[3 * vOther + 2] = oz;
        for (int i = 0; i < 2; i++)
            if (eClearOther[i] != -1 && table[eClearOther[i]] != -1) { heap.Remove(table[eClearOther[i]]); table[eClearOther[i]] = -1; }
        vq[vOtherIndex].Add(vq[vClearIndex]);
        float s = (float)(1.0f - fac);
        float nx = (float)((float)(s * bm.VNo[3 * vOther]) + (float)(fac * vcnx));
        float ny = (float)((float)(s * bm.VNo[3 * vOther + 1]) + (float)(fac * vcny));
        float nz = (float)((float)(s * bm.VNo[3 * vOther + 2]) + (float)(fac * vcnz));
        NormalizeC(nx, ny, nz);
        bm.VNo[3 * vOther] = nx; bm.VNo[3 * vOther + 1] = ny; bm.VNo[3 * vOther + 2] = nz;
        if (bm.VE[vOther] != None)
        {
            int eFirst = bm.VE[vOther], eIter = eFirst;
            do { BuildEdgeCostSingle(bm, eIter, vq, heap, table); } while ((eIter = bm.DiskEdgeNext(eIter, vOther)) != eFirst);
        }
        bm.LoopsOfVert(vOther, loops);
        for (int l : loops)
        {
            if (bm.FLen[bm.LF[l]] != 3) continue;
            int eOuter = bm.VertInEdge(bm.LE[bm.LPrev[l]], bm.LV[l]) ? bm.LE[bm.LNext[l]] : bm.LE[bm.LPrev[l]];
            BuildEdgeCostSingle(bm, eOuter, vq, heap, table);
        }
        return true;
    }
    InvalidEdgeCostSingle(e, heap, table);
    return false;
}

static int FaceTarget(int totFace, float factor) { return (int)(float)((float)totFace * factor); }

// ---------------------------------------------------------------- the entry
// counts: VertCount, TotVert, EdgeCount, TotEdge, LoopCount, TotLoop, FaceCount, TotFace (in; the Tot* written back, and
// kept current before every checkpoint call). The deform groups come in as CSR (defOffsets: nv + 1, defNr/defWeight:
// defOffsets[nv] entries, defPresent per vertex: a vertex with a list, possibly empty) and go out through a handle:
// haf_collapse_def_size / haf_collapse_def_copy / haf_collapse_free. checkpoint (may be null) is the managed loop's poll - the
// Bake Tests' cancel, PrepInProcess's deadline: called at the start, every 1024 faces and edges of the cost build, and every
// `every` collapses; a non-zero return stops the collapse where it is (the C# side rethrows what its checkpoint threw).
struct DefResult { std::vector<Def> def; std::vector<int> killed[4]; };
typedef int (__cdecl *CheckpointFn)();

static inline void Publish(const BM& bm, int* counts) { counts[1] = bm.TotVert; counts[3] = bm.TotEdge; counts[5] = bm.TotLoop; counts[7] = bm.TotFace; }

EXPORT void* haf_collapse(
    int* VE, uint8_t* VAlive, float* VCo, float* VNo, uint8_t* VFlag,
    int* EV1, int* EV2, int* EL, int* EDiskNext, int* EDiskPrev, uint8_t* EAlive, uint8_t* EFlag,
    int* LV, int* LE, int* LF, int* LNext, int* LPrev, int* LRadialNext, int* LRadialPrev, uint8_t* LAlive,
    int* FLFirst, int* FLen, int* FMat, float* FNo, uint8_t* FAlive, uint8_t* FFlag,
    int* counts, float factor,
    int nUv, float** uv, int nCornerColor, uint8_t** cornerColor, int nPointColor, uint8_t** pointColor,
    int hasDef, const int* defOffsets, const int* defNr, const float* defWeight, const uint8_t* defPresent,
    CheckpointFn checkpoint, int every)
{
    BM bm;
    bm.VE = VE; bm.VAlive = VAlive; bm.VCo = VCo; bm.VNo = VNo; bm.VFlag = VFlag;
    bm.EV1 = EV1; bm.EV2 = EV2; bm.EL = EL; bm.EDiskNext = EDiskNext; bm.EDiskPrev = EDiskPrev; bm.EAlive = EAlive; bm.EFlag = EFlag;
    bm.LV = LV; bm.LE = LE; bm.LF = LF; bm.LNext = LNext; bm.LPrev = LPrev; bm.LRadialNext = LRadialNext; bm.LRadialPrev = LRadialPrev; bm.LAlive = LAlive;
    bm.FLFirst = FLFirst; bm.FLen = FLen; bm.FMat = FMat; bm.FNo = FNo; bm.FAlive = FAlive; bm.FFlag = FFlag;
    bm.VertCount = counts[0]; bm.TotVert = counts[1]; bm.EdgeCount = counts[2]; bm.TotEdge = counts[3];
    bm.LoopCount = counts[4]; bm.TotLoop = counts[5]; bm.FaceCount = counts[6]; bm.TotFace = counts[7];
    MeshData d;
    for (int i = 0; i < nUv; i++) d.Uv.push_back(uv[i]);
    for (int i = 0; i < nCornerColor; i++) d.CornerColor.push_back(cornerColor[i]);
    for (int i = 0; i < nPointColor; i++) d.PointColor.push_back(pointColor[i]);
    DefResult* result = new DefResult();
    bm.killed = result->killed;
    if (hasDef)
    {
        result->def.resize(bm.VertCount);
        for (int v = 0; v < bm.VertCount; v++)
        {
            Def& o = result->def[v]; o.present = defPresent[v] != 0;
            for (int at = defOffsets[v]; at < defOffsets[v + 1]; at++) { o.nrs.push_back(defNr[at]); o.ws.push_back(defWeight[at]); }
        }
        d.Def = &result->def;
    }

    auto Poll = [&]() -> bool { if (!checkpoint) return false; Publish(bm, counts); return checkpoint() != 0; };
    if (every < 1) every = 1;
    std::vector<Quadric> vq(bm.VertCount);
    Heap heap(bm.TotEdge);
    std::vector<int> table(bm.EdgeCount, -1);
    if (Poll()) return result;
    if (!BuildQuadrics(bm, vq, Poll)) return result;
    for (int e = 0; e < bm.EdgeCount; e++)
    {
        if ((e & 1023) == 0 && Poll()) return result;
        if (!bm.EAlive[e]) continue; table[e] = -1; BuildEdgeCostSingle(bm, e, vq, heap, table);
    }
    int faceTotTarget = FaceTarget(bm.TotFace, factor);
    std::vector<int> loops; int eClearOther[2]; int since = 0;
    while (bm.TotFace > faceTotTarget && !heap.IsEmpty() && heap.TopValue() != CostInvalid)
    {
        if (++since >= every) { since = 0; if (Poll()) break; }
        int e = heap.PopMin();
        table[e] = -1;
        DecimEdgeCollapse(bm, d, e, vq, heap, table, loops, eClearOther);
    }
    Publish(bm, counts);
    return result;
}

// The deform groups after the collapse: total entries, then the CSR copy (offsets nv + 1, nr/weight entries, present per vertex).
EXPORT int haf_collapse_def_size(void* handle)
{
    DefResult* r = (DefResult*)handle; int64_t total = 0;
    for (const Def& d : r->def) total += (int64_t)d.nrs.size();
    return (int)total;
}
EXPORT void haf_collapse_def_copy(void* handle, int* offsets, int* nr, float* weight, uint8_t* present)
{
    DefResult* r = (DefResult*)handle; int at = 0;
    for (size_t v = 0; v < r->def.size(); v++)
    {
        offsets[v] = at;
        const Def& d = r->def[v]; present[v] = d.present ? 1 : 0;
        for (size_t j = 0; j < d.nrs.size(); j++) { nr[at] = d.nrs[j]; weight[at] = d.ws[j]; at++; }
    }
    offsets[r->def.size()] = at;
}
EXPORT void haf_collapse_free(void* handle) { delete (DefResult*)handle; }

// Each pool keeps the same last-freed-first reuse order as the managed BMesh, including after cancellation.
EXPORT int haf_collapse_killed_size(void* handle, int kind) { return (int)((DefResult*)handle)->killed[kind].size(); }
EXPORT void haf_collapse_killed_copy(void* handle, int kind, int* indices)
{
    const auto& killed = ((DefResult*)handle)->killed[kind];
    std::copy(killed.begin(), killed.end(), indices);
}
