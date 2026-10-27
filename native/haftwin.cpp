// haftwin.cpp - the Workshop fuse's twin census (GlbDisconnectedParts.cs, the twin grid and TwinOfFace): for every face,
// the nearest face within reach that faces back at it, by the closest point on the candidate's triangle. The grid is the
// C#'s: a cell per median face size, every face registered in the cells its box covers (capped at 40 cells an axis), the
// candidates of a cell in face order; a query walks the 27 cells around the face's centre in dx, dy, dz order and takes the
// first of equal distances, so the chosen twin is the C#'s. The float prefilter and the double tests are the C#'s step for
// step. Compile with -ffp-contract=off.
#include <cmath>
#include <cstdint>
#include <limits>
#include <vector>
#include <algorithm>
#include <thread>
#include <atomic>
#define EXPORT extern "C" __declspec(dllexport)

struct TV3 { double x, y, z; };
static inline TV3 tsub(TV3 a, TV3 b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
static inline TV3 tadd(TV3 a, TV3 b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
static inline TV3 tscale(TV3 a, double s) { return { a.x * s, a.y * s, a.z * s }; }
static inline double tdot(TV3 a, TV3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
static inline double tlen(TV3 a) { return std::sqrt(tdot(a, a)); }

static TV3 ClosestPointOnTriangle(TV3 p, TV3 a, TV3 b, TV3 c)
{
    TV3 ab = tsub(b, a), ac = tsub(c, a), ap = tsub(p, a);
    double d1 = tdot(ab, ap), d2 = tdot(ac, ap);
    if (d1 <= 0 && d2 <= 0) return a;
    TV3 bp = tsub(p, b); double d3 = tdot(ab, bp), d4 = tdot(ac, bp);
    if (d3 >= 0 && d4 <= d3) return b;
    double vc = d1 * d4 - d3 * d2;
    if (vc <= 0 && d1 >= 0 && d3 <= 0) { double v = d1 / (d1 - d3); return tadd(a, tscale(ab, v)); }
    TV3 cp = tsub(p, c); double d5 = tdot(ab, cp), d6 = tdot(ac, cp);
    if (d6 >= 0 && d5 <= d6) return c;
    double vb = d5 * d2 - d1 * d6;
    if (vb <= 0 && d2 >= 0 && d6 <= 0) { double w = d2 / (d2 - d6); return tadd(a, tscale(ac, w)); }
    double va = d3 * d6 - d5 * d4;
    if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { double w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return tadd(b, tscale(tsub(c, b), w)); }
    double denom = 1.0 / (va + vb + vc); double v = vb * denom, w = vc * denom;
    return tadd(a, tadd(tscale(ab, v), tscale(ac, w)));
}

struct TwinGrid
{
    int nFaces; const double* P; double cell, reach; float pad;
    std::vector<TV3> centre; std::vector<TV3> unit; std::vector<uint8_t> hasUnit; std::vector<double> radius;
    std::vector<int64_t> keyX, keyY, keyZ;   // sorted cell keys as three parallel arrays
    std::vector<int> offsets, ids; std::vector<float> packed;   // packed: 7 floats per id, in the cell's order
};
static inline int64_t Fl(double v, double cell) { return (int64_t)std::floor(v / cell); }
struct CellKey { int64_t x, y, z; bool operator<(const CellKey& o) const { return x != o.x ? x < o.x : y != o.y ? y < o.y : z < o.z; } bool operator==(const CellKey& o) const { return x == o.x && y == o.y && z == o.z; } };

static int FindCell(const TwinGrid& g, int64_t x, int64_t y, int64_t z)
{
    int lo = 0, hi = (int)g.keyX.size() - 1;
    while (lo <= hi)
    {
        int mid = (lo + hi) >> 1;
        CellKey m = { g.keyX[mid], g.keyY[mid], g.keyZ[mid] }, k = { x, y, z };
        if (m < k) lo = mid + 1; else if (k < m) hi = mid - 1; else return mid;
    }
    return -1;
}

// P: 9 doubles per face; flipped: a byte per face (the passes' winding so far); cell: the C#'s twinCell; reach: twinReach;
// pad: twinPad. The face normals, centres and radii are computed here as the C# computes them.
EXPORT void* haf_twin_build(const double* P, int nFaces, const uint8_t* flipped, double cell, double reach, float pad)
{
    TwinGrid* g = new TwinGrid();
    g->nFaces = nFaces; g->P = P; g->cell = cell; g->reach = reach; g->pad = pad;
    g->centre.resize(nFaces); g->unit.resize(nFaces); g->hasUnit.assign(nFaces, 0); g->radius.resize(nFaces);
    std::vector<std::pair<CellKey, int>> pairs;
    for (int f = 0; f < nFaces; f++)
    {
        const double* p = P + 9 * f;
        TV3 a = { p[0], p[1], p[2] }, b = { p[3], p[4], p[5] }, c = { p[6], p[7], p[8] };
        TV3 e1 = tsub(b, a), e2 = tsub(c, a);
        TV3 n = { e1.y * e2.z - e1.z * e2.y, e1.z * e2.x - e1.x * e2.z, e1.x * e2.y - e1.y * e2.x };
        if (flipped[f]) n = tscale(n, -1.0);
        double l = tlen(n); if (l >= 1e-12) { g->unit[f] = tscale(n, 1.0 / l); g->hasUnit[f] = 1; } else g->unit[f] = { 0, 0, 0 };
        TV3 cen = tscale(tadd(tadd(a, b), c), 1.0 / 3.0);
        g->centre[f] = cen;
        g->radius[f] = std::fmax(tlen(tsub(a, cen)), std::fmax(tlen(tsub(b, cen)), tlen(tsub(c, cen))));
        double mnx = std::fmin(a.x, std::fmin(b.x, c.x)), mny = std::fmin(a.y, std::fmin(b.y, c.y)), mnz = std::fmin(a.z, std::fmin(b.z, c.z));
        double mxx = std::fmax(a.x, std::fmax(b.x, c.x)), mxy = std::fmax(a.y, std::fmax(b.y, c.y)), mxz = std::fmax(a.z, std::fmax(b.z, c.z));
        int64_t lx = Fl(mnx, cell), ly = Fl(mny, cell), lz = Fl(mnz, cell);
        int64_t hx = std::min(Fl(mxx, cell), lx + 40), hy = std::min(Fl(mxy, cell), ly + 40), hz = std::min(Fl(mxz, cell), lz + 40);
        for (int64_t x = lx; x <= hx; x++) for (int64_t y = ly; y <= hy; y++) for (int64_t z = lz; z <= hz; z++) pairs.push_back({ { x, y, z }, f });
    }
    std::stable_sort(pairs.begin(), pairs.end(), [](const std::pair<CellKey, int>& a, const std::pair<CellKey, int>& b) { return a.first < b.first; });
    g->ids.reserve(pairs.size()); g->packed.reserve(pairs.size() * 7);
    for (size_t i = 0; i < pairs.size(); i++)
    {
        if (i == 0 || !(pairs[i].first == pairs[i - 1].first)) { g->keyX.push_back(pairs[i].first.x); g->keyY.push_back(pairs[i].first.y); g->keyZ.push_back(pairs[i].first.z); g->offsets.push_back((int)i); }
        int f = pairs[i].second; g->ids.push_back(f);
        TV3 tc = g->centre[f], tu = g->unit[f];
        g->packed.push_back((float)tc.x); g->packed.push_back((float)tc.y); g->packed.push_back((float)tc.z);
        g->packed.push_back((float)g->radius[f]);
        g->packed.push_back((float)tu.x); g->packed.push_back((float)tu.y); g->packed.push_back((float)tu.z);
    }
    g->offsets.push_back((int)pairs.size());
    return g;
}
EXPORT void haf_twin_free(void* handle) { delete (TwinGrid*)handle; }

static void TwinOfFace(const TwinGrid& g, int f, std::vector<int>& seen, double* best, double* proj, int* bestG)
{
    const double inf = std::numeric_limits<double>::infinity();
    bestG[f] = -1; best[f] = inf; proj[f] = 0;
    if (!g.hasUnit[f]) return;
    TV3 nf = g.unit[f], c = g.centre[f];
    int64_t kx = Fl(c.x, g.cell), ky = Fl(c.y, g.cell), kz = Fl(c.z, g.cell);
    double b = inf, pr = 0; int bg = -1; int stamp = f + 1;
    float cxF = (float)c.x, cyF = (float)c.y, czF = (float)c.z;
    float nxF = (float)nf.x, nyF = (float)nf.y, nzF = (float)nf.z, reachF = (float)g.reach;
    for (int64_t dx = -1; dx <= 1; dx++) for (int64_t dy = -1; dy <= 1; dy++) for (int64_t dz = -1; dz <= 1; dz++)
    {
        int cellAt = FindCell(g, kx + dx, ky + dy, kz + dz);
        if (cellAt < 0) continue;
        int from = g.offsets[cellAt], to = g.offsets[cellAt + 1];
        for (int j = from; j < to; j++)
        {
            const float* pk = g.packed.data() + (size_t)j * 7;
            float pdx = pk[0] - cxF, pdy = pk[1] - cyF, pdz = pk[2] - czF;
            float plim = reachF + pk[3] + g.pad;
            if (pdx * pdx + pdy * pdy + pdz * pdz > plim * plim) continue;
            if (nxF * pk[4] + nyF * pk[5] + nzF * pk[6] > -0.94f) continue;
            int o = g.ids[j];
            if (o == f || seen[o] == stamp) continue;
            seen[o] = stamp;
            if (!g.hasUnit[o]) continue;
            TV3 gc = g.centre[o]; double reachG = g.reach + g.radius[o];
            double ddx = gc.x - c.x, ddy = gc.y - c.y, ddz = gc.z - c.z;
            if (ddx * ddx + ddy * ddy + ddz * ddz > reachG * reachG) continue;
            if (tdot(nf, g.unit[o]) > -0.95) continue;
            const double* p = g.P + 9 * o;
            TV3 q = ClosestPointOnTriangle(c, { p[0], p[1], p[2] }, { p[3], p[4], p[5] }, { p[6], p[7], p[8] });
            TV3 d = tsub(q, c); double dist = tlen(d);
            if (dist < 1e-9 || dist > g.reach) continue;
            double along = tdot(d, nf);
            if (std::fabs(along) < 0.5 * dist) continue;
            if (dist < b) { b = dist; pr = along; bg = o; }
        }
    }
    best[f] = b; proj[f] = pr; bestG[f] = b == inf ? -1 : bg;
}

// Every face's twin: best (distance, +inf for none), proj (the twin's offset along the normal), bestG (the twin face or -1).
EXPORT void haf_twin_all(void* handle, int workers, double* best, double* proj, int* bestG)
{
    const TwinGrid& g = *(const TwinGrid*)handle;
    // Threads only where they pay: below the other kernels' 16,384 a group is answered on the caller's thread (review of
    // PR #202 - a handful of faces started a thread per core, each with a scratch array the size of the group). Each face's
    // answer is its own, so the worker count never changes the result.
    int n = g.nFaces; if (workers < 1 || n < 16384) workers = 1; if (workers > n) workers = std::max(1, n);
    auto range = [&](int first, int last) { std::vector<int> seen(n, 0); for (int f = first; f < last; f++) TwinOfFace(g, f, seen, best, proj, bestG); };
    if (workers == 1) { range(0, n); return; }
    // The faces are taken in chunks from one counter, not in one fixed slice per worker (2026-10-27): a ship is dense in
    // places, a worker whose slice held the superstructure answered thousands of candidates a face while the others sat
    // finished - the census of the Saleg's largest group took 7.4 s on 16 threads. Each face's answer is its own, so the
    // chunking never changes a result; one scratch array per worker, as before.
    std::atomic<int> next(0); const int chunk = 2048;
    auto worker = [&] {
        std::vector<int> seen(n, 0);
        for (;;)
        {
            int first = next.fetch_add(chunk); if (first >= n) return;
            int last = std::min(n, first + chunk);
            for (int f = first; f < last; f++) TwinOfFace(g, f, seen, best, proj, bestG);
        }
    };
    std::vector<std::thread> th;
    for (int w = 0; w < workers; w++) th.emplace_back(worker);
    for (auto& t : th) t.join();
}
