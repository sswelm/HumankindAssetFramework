// hafkernels.cpp - native ports of VehicleProbe.Visibility.cs kernels (HAF, editor/): the world transform of
// BuildPartMesh and the constructor of TriangleBvh, arithmetic step for step as the C# (float32 where it is float32,
// double sums where mathutils sums in double). Compile with -ffp-contract=off: no fused multiply-add anywhere.
#include <cmath>
#include <cstdint>
#include <thread>
#include <algorithm>
#include <limits>
#define EXPORT extern "C" __declspec(dllexport)

// ---------------------------------------------------------------- the world transform of BuildPartMesh (VertexRange)
static inline float MatRow(const float* mb, int row, int cols, float x, float y, float z, float w)
{
    double d = 0.0;
    d += (double)(float)(mb[row * 4 + 0] * x);
    d += (double)(float)(mb[row * 4 + 1] * y);
    d += (double)(float)(mb[row * 4 + 2] * z);
    if (cols == 4) d += (double)(float)(mb[row * 4 + 3] * w);
    return (float)d;
}
static inline void NormalizeVn(float& x, float& y, float& z)
{
    double d = 0.0;
    d += (double)z * z; d += (double)y * y; d += (double)x * x;
    if (d > 1.0e-35) { float s = (float)(1.0f / (float)std::sqrt(d)); x = (float)(x * s); y = (float)(y * s); z = (float)(z * s); }
    else { x = 0.f; y = 0.f; z = 0.f; }
}
EXPORT void haf_world_transform(const float* Pb, const float* blenderN, const float* mb, const float* inverse, int first, int last,
                                float* Local, float* World, float* Normal, float* PreviewNormal)
{
    for (int i = first; i < last; i++)
    {
        float bx = Pb[i * 3], by = Pb[i * 3 + 1], bz = Pb[i * 3 + 2];
        Local[i * 3] = bx; Local[i * 3 + 1] = bz; Local[i * 3 + 2] = -by;
        World[i * 3] = MatRow(mb, 0, 4, bx, by, bz, 1.f); World[i * 3 + 1] = MatRow(mb, 1, 4, bx, by, bz, 1.f); World[i * 3 + 2] = MatRow(mb, 2, 4, bx, by, bz, 1.f);
        float nbx = blenderN[i * 3], nby = blenderN[i * 3 + 1], nbz = blenderN[i * 3 + 2];
        float wx = MatRow(mb, 0, 3, nbx, nby, nbz, 0.f), wy = MatRow(mb, 1, 3, nbx, nby, nbz, 0.f), wz = MatRow(mb, 2, 3, nbx, nby, nbz, 0.f);
        NormalizeVn(wx, wy, wz);
        Normal[i * 3] = wx; Normal[i * 3 + 1] = wy; Normal[i * 3 + 2] = wz;
        wx = (float)((double)inverse[0] * nbx + (double)inverse[4] * nby + (double)inverse[8] * nbz);
        wy = (float)((double)inverse[1] * nbx + (double)inverse[5] * nby + (double)inverse[9] * nbz);
        wz = (float)((double)inverse[2] * nbx + (double)inverse[6] * nby + (double)inverse[10] * nbz);
        NormalizeVn(wx, wy, wz);
        PreviewNormal[i * 3] = wx; PreviewNormal[i * 3 + 1] = wy; PreviewNormal[i * 3 + 2] = wz;
    }
}

// ---------------------------------------------------------------- the constructor of TriangleBvh
struct BvhBuild
{
    const float* v; const int* tris; int nt; int workers, grain;
    float *cx, *cy, *cz; int* order;
    float *bmin, *bmax; int *left, *start, *count;

    static void Select(int* order, const float* key, int lo, int hi, int k)
    {
        while (lo < hi)
        {
            float pivot = key[order[(lo + hi) >> 1]];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (key[order[i]] < pivot) i++;
                while (key[order[j]] > pivot) j--;
                if (i <= j) { int tmp = order[i]; order[i] = order[j]; order[j] = tmp; i++; j--; }
            }
            if (k <= j) hi = j; else if (k >= i) lo = i; else return;
        }
    }
    bool Node(int node, int lo, int hi, int next, int& mid)
    {
        mid = -1;
        const float inf = std::numeric_limits<float>::infinity();
        float x0 = inf, y0 = inf, z0 = inf, x1 = -inf, y1 = -inf, z1 = -inf;
        float c0x = inf, c0y = inf, c0z = inf, c1x = -inf, c1y = -inf, c1z = -inf;
        for (int i = lo; i < hi; i++)
        {
            int tri = order[i];
            for (int k = 0; k < 3; k++)
            {
                int vi = tris[tri * 3 + k] * 3;
                if (v[vi] < x0) x0 = v[vi]; if (v[vi] > x1) x1 = v[vi];
                if (v[vi + 1] < y0) y0 = v[vi + 1]; if (v[vi + 1] > y1) y1 = v[vi + 1];
                if (v[vi + 2] < z0) z0 = v[vi + 2]; if (v[vi + 2] > z1) z1 = v[vi + 2];
            }
            if (cx[tri] < c0x) c0x = cx[tri]; if (cx[tri] > c1x) c1x = cx[tri];
            if (cy[tri] < c0y) c0y = cy[tri]; if (cy[tri] > c1y) c1y = cy[tri];
            if (cz[tri] < c0z) c0z = cz[tri]; if (cz[tri] > c1z) c1z = cz[tri];
        }
        const float padding = 1.1920929e-7f;
        bmin[node * 3] = (float)(x0 - padding); bmin[node * 3 + 1] = (float)(y0 - padding); bmin[node * 3 + 2] = (float)(z0 - padding);
        bmax[node * 3] = (float)(x1 + padding); bmax[node * 3 + 1] = (float)(y1 + padding); bmax[node * 3 + 2] = (float)(z1 + padding);
        int n = hi - lo;
        float ex = c1x - c0x, ey = c1y - c0y, ez = c1z - c0z;
        int axis = ex >= ey && ex >= ez ? 0 : ey >= ez ? 1 : 2;
        float extent = axis == 0 ? ex : axis == 1 ? ey : ez;
        if (n <= 4 || !(extent > 0)) { start[node] = lo; count[node] = n; return false; }
        mid = lo + n / 2;
        Select(order, axis == 0 ? cx : axis == 1 ? cy : cz, lo, hi - 1, mid);
        left[node] = next; count[node] = 0;
        return true;
    }
    void Build(int node, int lo, int hi, int next, int depth)
    {
        int mid;
        if (!Node(node, lo, hi, next, mid)) return;
        int l = left[node], nextLeft = next + 2, nextRight = next + 2 * (mid - lo);
        if (hi - lo >= grain && workers > 1 && depth < 4)
        {
            std::thread th([=] { Build(l, lo, mid, nextLeft, depth + 1); });
            Build(l + 1, mid, hi, nextRight, depth + 1);
            th.join();
        }
        else { Build(l, lo, mid, nextLeft, depth); Build(l + 1, mid, hi, nextRight, depth); }
    }
};

template <class F> static void PerRange(int n, int workers, F f)
{
    if (n >= 16384 && workers > 1)
    {
        std::thread* th = new std::thread[workers];
        for (int b = 0; b < workers; b++) th[b] = std::thread([=] { f((int)((int64_t)n * b / workers), (int)((int64_t)n * (b + 1) / workers)); });
        for (int b = 0; b < workers; b++) th[b].join();
        delete[] th;
    }
    else f(0, n);
}

// bmin/bmax: 3 * cap floats; left/start/count: cap ints, cap = max(1, 2 nt - 1); tOut: 3 nt ints.
EXPORT void haf_bvh_build(const float* v, const int* tris, int nt, int workers,
                          float* bmin, float* bmax, int* left, int* start, int* count, int* tOut)
{
    BvhBuild b; b.v = v; b.tris = tris; b.nt = nt; b.workers = workers;
    b.cx = new float[nt]; b.cy = new float[nt]; b.cz = new float[nt]; b.order = new int[nt];
    b.bmin = bmin; b.bmax = bmax; b.left = left; b.start = start; b.count = count;
    PerRange(nt, workers, [&](int first, int last) {
        for (int i = first; i < last; i++)
        {
            int a = tris[i * 3] * 3, bb = tris[i * 3 + 1] * 3, c = tris[i * 3 + 2] * 3;
            b.cx[i] = (v[a] + v[bb] + v[c]) / 3.f; b.cy[i] = (v[a + 1] + v[bb + 1] + v[c + 1]) / 3.f; b.cz[i] = (v[a + 2] + v[bb + 2] + v[c + 2]) / 3.f;
        }
    });
    for (int i = 0; i < nt; i++) b.order[i] = i;
    b.grain = std::max(4096, nt / (8 * workers));
    b.Build(0, 0, nt, 1, 0);
    PerRange(nt, workers, [&](int first, int last) {
        for (int i = first; i < last; i++) { int o = b.order[i]; tOut[i * 3] = tris[o * 3]; tOut[i * 3 + 1] = tris[o * 3 + 1]; tOut[i * 3 + 2] = tris[o * 3 + 2]; }
    });
    delete[] b.cx; delete[] b.cy; delete[] b.cz; delete[] b.order;
}
