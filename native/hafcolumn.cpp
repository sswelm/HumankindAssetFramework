// hafcolumn.cpp - the Workshop fuse's column ray query (GlbDisconnectedParts.cs, Column and ExposedFromSide) over flat
// grids the C# builds: per rung a sorted key table with CSR candidate lists, the candidates in registration order.
// The arithmetic is the C#'s, double step for step: the centroid as (a + b) + c times 1/3, InColumn's rejections, and
// Moeller-Trumbore with the 1e-12 determinant floor and the 1e-9 barycentric slack. Compile with -ffp-contract=off.
#include <cmath>
#include <cstdint>
#include <limits>
#define EXPORT extern "C" __declspec(dllexport)

struct V3 { double x, y, z; };
static inline V3 sub(V3 a, V3 b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
static inline V3 cross(V3 a, V3 b) { return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x }; }
static inline double dot(V3 a, V3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }

static double RayTriangle(V3 o, V3 d, V3 v0, V3 v1, V3 v2)
{
    V3 e1 = sub(v1, v0), e2 = sub(v2, v0), p = cross(d, e2); double det = dot(e1, p);
    if (std::fabs(det) < 1e-12) return -1; double inv = 1.0 / det;
    V3 tv = sub(o, v0); double u = dot(tv, p) * inv; if (u < -1e-9 || u > 1 + 1e-9) return -1;
    V3 q = cross(tv, e1); double v = dot(d, q) * inv; if (v < -1e-9 || u + v > 1 + 1e-9) return -1;
    return dot(e2, q) * inv;
}

static inline double min3(double a, double b, double c) { return std::fmin(a, std::fmin(b, c)); }
static inline double max3(double a, double b, double c) { return std::fmax(a, std::fmax(b, c)); }

// C# Math.Min/Max on doubles: NaN-propagating; fmin/fmax ignore NaN. The fuse never feeds NaN here (positions are finite,
// checked at parse), so the two agree on every input the C# sees.
static bool InColumn(V3 a, V3 b, V3 cc, V3 c, bool upward, double skin, double best)
{
    if (c.x < min3(a.x, b.x, cc.x) - skin || c.x > max3(a.x, b.x, cc.x) + skin) return false;
    if (c.z < min3(a.z, b.z, cc.z) - skin || c.z > max3(a.z, b.z, cc.z) + skin) return false;
    double loY = min3(a.y, b.y, cc.y), hiY = max3(a.y, b.y, cc.y);
    bool inf = best == std::numeric_limits<double>::infinity();
    if (upward) return hiY > c.y && (inf || loY - c.y <= best + skin);
    return loY < c.y && (inf || c.y - hiY <= best + skin);
}

// One rung of the ladder: nKeys sorted int64 keys, offsets[nKeys + 1] into ids. A key is (x << 32) ^ (z & 0xffffffff).
struct Rung { const int64_t* keys; const int* offsets; const int* ids; int nKeys; double cell; };

#include <vector>
#include <algorithm>
// The ladder built here from the corners, as BuildOccluders registers it: a triangle goes to the finest rung whose cell it
// fits inside (rung 0 takes the rest, and drops a sliver wider than 256 cells), into every cell its x/z box covers; the
// occluders in triangle order, then the group's faces. A stable sort of (key, id) pairs by key gives each cell its
// candidates in that registration order - the order the managed dictionaries' lists hold, which the walk reads.
// THE SAME LADDER SEEN FROM THE SIDE (2026-10-27): ExposedFromSide's grid is this one over (length, height) instead of
// (x, z), without the sliver rule, asked along the width axis and answered "anything there past the skin" instead of the
// nearest distance. u and v name the two grid components (0 = x, 1 = y, 2 = z).
static inline double comp(V3 p, int a) { return a == 0 ? p.x : a == 1 ? p.y : p.z; }
struct Ladder
{
    int levels; std::vector<double> cells; int u, v; bool sliver;
    std::vector<std::vector<int64_t>> keys; std::vector<std::vector<int>> offsets, ids;
    std::vector<const double*> segs; std::vector<int> first; int nOcc;   // the occluder segments and each one's first triangle number
    const double* P; int nFaces; double top, bottom;
};
static void Register(Ladder& L, std::vector<std::vector<std::pair<int64_t, int>>>& pairs, int id, V3 a, V3 b, V3 c)
{
    double mnX = min3(comp(a, L.u), comp(b, L.u), comp(c, L.u)), mxX = max3(comp(a, L.u), comp(b, L.u), comp(c, L.u));
    double mnZ = min3(comp(a, L.v), comp(b, L.v), comp(c, L.v)), mxZ = max3(comp(a, L.v), comp(b, L.v), comp(c, L.v));
    int lev = L.levels - 1;
    while (lev > 0 && (mxX - mnX > L.cells[lev] || mxZ - mnZ > L.cells[lev])) lev--;
    double cell = L.cells[lev];
    int64_t x0 = (int64_t)std::floor(mnX / cell), x1 = (int64_t)std::floor(mxX / cell);
    int64_t z0 = (int64_t)std::floor(mnZ / cell), z1 = (int64_t)std::floor(mxZ / cell);
    if (L.sliver && lev == 0 && (x1 - x0 > 256 || z1 - z0 > 256)) return;
    for (int64_t x = x0; x <= x1; x++) for (int64_t z = z0; z <= z1; z++)
        pairs[lev].push_back({ (x << 32) ^ (z & 0xffffffffLL), id });
}
// The occluders come as SEGMENTS - one per file node the fuse reads once and shares between its groups - in the order the
// managed walk numbers them: triangle t of the whole set is the t-th across the segments in sequence, its corners at
// segs[s] + 9 (t - first[s]). A triangle's corners are found through the prefix table, never copied.
static inline const double* Corners(const Ladder& L, int t)
{
    int lo = 0, hi = (int)L.first.size() - 1;   // the last segment whose first triangle is <= t
    while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (L.first[mid] <= t) lo = mid; else hi = mid - 1; }
    return L.segs[lo] + 9 * (t - L.first[lo]);
}
// segs: nSegs segment pointers, segTris triangles each (9 doubles a triangle); P: nFaces group faces; cells: one size per rung.
static Ladder* Build(int levels, const double* cells, int nSegs, const double** segs, const int* segTris, const double* P, int nFaces, double top, double bottom, int u, int v, bool sliver)
{
    Ladder* L = new Ladder();
    L->levels = levels; L->cells.assign(cells, cells + levels); L->P = P; L->nFaces = nFaces; L->top = top; L->bottom = bottom;
    L->u = u; L->v = v; L->sliver = sliver;
    int nOcc = 0;
    for (int s = 0; s < nSegs; s++) { if (segTris[s] <= 0) continue; L->segs.push_back(segs[s]); L->first.push_back(nOcc); nOcc += segTris[s]; }
    L->nOcc = nOcc;
    std::vector<std::vector<std::pair<int64_t, int>>> pairs(levels);
    for (int t = 0; t < nOcc; t++)
    {
        const double* o = Corners(*L, t);
        Register(*L, pairs, 3 * t, { o[0], o[1], o[2] }, { o[3], o[4], o[5] }, { o[6], o[7], o[8] });
    }
    for (int f = 0; f < nFaces; f++)
    {
        const double* p = P + 9 * f;
        Register(*L, pairs, -1 - f, { p[0], p[1], p[2] }, { p[3], p[4], p[5] }, { p[6], p[7], p[8] });
    }
    L->keys.resize(levels); L->offsets.resize(levels); L->ids.resize(levels);
    for (int k = 0; k < levels; k++)
    {
        auto& v = pairs[k];
        std::stable_sort(v.begin(), v.end(), [](const std::pair<int64_t, int>& a, const std::pair<int64_t, int>& b) { return a.first < b.first; });
        auto& ks = L->keys[k]; auto& off = L->offsets[k]; auto& id = L->ids[k];
        id.reserve(v.size());
        for (size_t i = 0; i < v.size(); i++)
        {
            if (i == 0 || v[i].first != v[i - 1].first) { ks.push_back(v[i].first); off.push_back((int)i); }
            id.push_back(v[i].second);
        }
        off.push_back((int)v.size());
        if (ks.empty()) { ks.push_back(0); off.assign(2, 0); id.push_back(0); }
    }
    return L;
}
EXPORT void* haf_column_build(int levels, const double* cells, int nSegs, const double** segs, const int* segTris, const double* P, int nFaces, double top, double bottom)
{
    return Build(levels, cells, nSegs, segs, segTris, P, nFaces, top, bottom, 0, 2, true);
}
// The side ladder: over (length, height), the length axis being z when the width axis is x and x otherwise; no sliver rule.
EXPORT void* haf_side_build(int levels, const double* cells, int nSegs, const double** segs, const int* segTris, const double* P, int nFaces, double top, double bottom, int width)
{
    return Build(levels, cells, nSegs, segs, segTris, P, nFaces, top, bottom, width == 0 ? 2 : 0, 1, false);
}
EXPORT void haf_column_free(void* handle) { delete (Ladder*)handle; }

static const int* Find(const Rung& r, int64_t key, int& count)
{
    int lo = 0, hi = r.nKeys - 1;
    while (lo <= hi)
    {
        int mid = (lo + hi) >> 1;
        if (r.keys[mid] < key) lo = mid + 1; else if (r.keys[mid] > key) hi = mid - 1;
        else { count = r.offsets[mid + 1] - r.offsets[mid]; return r.ids + r.offsets[mid]; }
    }
    count = 0; return nullptr;
}

static inline int64_t Key(double x, double z, double cell)
{
    return ((int64_t)std::floor(x / cell) << 32) ^ ((int64_t)std::floor(z / cell) & 0xffffffffLL);
}

// occ: occluder corners, 3 doubles each, triangle t at occ[3 * t .. 3 * t + 8]; P: the group's face corners, face g at
// P[9 g ..]; an id >= 0 is an occluder triangle's first corner index t (a multiple of 3), an id < 0 is -1 - g.
// For each query i: the origin is face faces[i]'s centroid as the C# computes it from P, upward per flag; skip is the
// sheet being judged as nSkip sorted group-face indices (0 = none); best[i] receives the distance, +inf for nothing.
static inline bool Skipped(const int* skip, int nSkip, int g)
{
    int lo = 0, hi = nSkip - 1;
    while (lo <= hi) { int mid = (lo + hi) >> 1; if (skip[mid] < g) lo = mid + 1; else if (skip[mid] > g) hi = mid - 1; else return true; }
    return false;
}
EXPORT void haf_column_query(void* handle, int nQueries, const int* faces, const uint8_t* upward, const int* skip, int nSkip, double* best)
{
    const Ladder& L = *(const Ladder*)handle;
    int levels = L.levels; const double* P = L.P;
    const double inf = std::numeric_limits<double>::infinity();
    double skin = 1e-6 * std::fmax(1.0, L.top - L.bottom);
    for (int i = 0; i < nQueries; i++)
    {
        int f = faces[i];
        V3 p0 = { P[9 * f], P[9 * f + 1], P[9 * f + 2] }, p1 = { P[9 * f + 3], P[9 * f + 4], P[9 * f + 5] }, p2 = { P[9 * f + 6], P[9 * f + 7], P[9 * f + 8] };
        V3 s = { (p0.x + p1.x) + p2.x, (p0.y + p1.y) + p2.y, (p0.z + p1.z) + p2.z };
        const double third = 1.0 / 3.0;
        V3 c = { s.x * third, s.y * third, s.z * third };
        bool up = upward[i] != 0;
        V3 d = { 0, up ? 1.0 : -1.0, 0 };
        double b = inf;
        for (int k = 0; k < levels; k++)
        {
            Rung r = { L.keys[k].data(), L.offsets[k].data(), L.ids[k].data(), (int)L.keys[k].size(), L.cells[k] };
            int count; const int* list = Find(r, Key(c.x, c.z, r.cell), count);
            for (int j = 0; j < count; j++)
            {
                int t = list[j]; V3 a, bb, cc;
                if (t >= 0) { const double* o = Corners(L, t / 3); a = { o[0], o[1], o[2] }; bb = { o[3], o[4], o[5] }; cc = { o[6], o[7], o[8] }; }
                else
                {
                    int g = -1 - t; if (g == f || (nSkip > 0 && Skipped(skip, nSkip, g))) continue;
                    a = { P[9 * g], P[9 * g + 1], P[9 * g + 2] }; bb = { P[9 * g + 3], P[9 * g + 4], P[9 * g + 5] }; cc = { P[9 * g + 6], P[9 * g + 7], P[9 * g + 8] };
                }
                if (!InColumn(a, bb, cc, c, up, skin, b)) continue;
                double hit = RayTriangle(c, d, a, bb, cc);
                if (hit > skin && hit < b) b = hit;
            }
        }
        best[i] = b;
    }
}

// ExposedFromSide(f, width, positive) for each query: from face faces[i]'s centroid along the width axis (positive[i] = towards
// +width), is anything of the model there past the skin? The candidates of the (length, height) cell on every rung; the face
// itself is not an occluder; the first hit decides, as the managed walk returns on it. exposed[i] = 1 when nothing was hit.
EXPORT void haf_side_query(void* handle, int nQueries, const int* faces, const uint8_t* positive, int width, uint8_t* exposed)
{
    const Ladder& L = *(const Ladder*)handle;
    int levels = L.levels; const double* P = L.P;
    int length = width == 0 ? 2 : 0;
    double skin = 1e-6 * std::fmax(1.0, L.top - L.bottom);
    for (int i = 0; i < nQueries; i++)
    {
        int f = faces[i];
        V3 p0 = { P[9 * f], P[9 * f + 1], P[9 * f + 2] }, p1 = { P[9 * f + 3], P[9 * f + 4], P[9 * f + 5] }, p2 = { P[9 * f + 6], P[9 * f + 7], P[9 * f + 8] };
        V3 s = { (p0.x + p1.x) + p2.x, (p0.y + p1.y) + p2.y, (p0.z + p1.z) + p2.z };
        const double third = 1.0 / 3.0;
        V3 c = { s.x * third, s.y * third, s.z * third };
        double sign = positive[i] != 0 ? 1.0 : -1.0;
        V3 d = width == 0 ? V3{ sign, 0, 0 } : V3{ 0, 0, sign };
        double along = comp(c, length);
        bool open = true;
        for (int k = 0; k < levels && open; k++)
        {
            Rung r = { L.keys[k].data(), L.offsets[k].data(), L.ids[k].data(), (int)L.keys[k].size(), L.cells[k] };
            int count; const int* list = Find(r, Key(along, c.y, r.cell), count);
            for (int j = 0; j < count; j++)
            {
                int t = list[j]; V3 a, bb, cc;
                if (t >= 0) { const double* o = Corners(L, t / 3); a = { o[0], o[1], o[2] }; bb = { o[3], o[4], o[5] }; cc = { o[6], o[7], o[8] }; }
                else
                {
                    int g = -1 - t; if (g == f) continue;
                    a = { P[9 * g], P[9 * g + 1], P[9 * g + 2] }; bb = { P[9 * g + 3], P[9 * g + 4], P[9 * g + 5] }; cc = { P[9 * g + 6], P[9 * g + 7], P[9 * g + 8] };
                }
                if (along < min3(comp(a, length), comp(bb, length), comp(cc, length)) - skin ||
                    along > max3(comp(a, length), comp(bb, length), comp(cc, length)) + skin ||
                    c.y < min3(a.y, bb.y, cc.y) - skin || c.y > max3(a.y, bb.y, cc.y) + skin) continue;
                if (RayTriangle(c, d, a, bb, cc) > skin) { open = false; break; }
            }
        }
        exposed[i] = open ? 1 : 0;
    }
}
