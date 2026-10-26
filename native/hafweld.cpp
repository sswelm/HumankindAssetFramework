// hafweld.cpp - the Workshop fuse's weld classes (GlbDisconnectedParts.cs, WeldClasses): union-find over the vertices
// within `distance` of each other, found through a hash grid of 27 neighbouring cells. The walk is the C#'s step for
// step: vertex i ascending, the cells in dx, dy, dz order, a cell's vertices ascending, only j > i, the squared distance
// as (dx*dx + dy*dy) + dz*dz against thresh2, the union parent[Find(i)] = Find(j) with path halving. The order decides
// which vertex ends up as a class's representative, and the fused bytes follow it; nothing here may reorder it.
// Measured 2026-10-27: the two weld passes of the Saleg's Revenge (22 groups) took 97 s summed in Unity's Mono, the
// walk itself, no longer its allocations. Compile with -ffp-contract=off.
#include <cmath>
#include <cstdint>
#include <vector>
#define EXPORT extern "C" __declspec(dllexport)

namespace
{
    // How many bits a coordinate in [lo, hi] takes relative to lo; 64 when the span does not fit.
    int Bits(int64_t lo, int64_t hi)
    {
        if (hi < lo) return 0;
        uint64_t span = (uint64_t)hi - (uint64_t)lo;
        int bits = 0; while (bits < 64 && (span >> bits) != 0) bits++;
        return bits;
    }
    inline uint64_t Mix(uint64_t h) { h *= 0x9E3779B97F4A7C15ULL; h ^= h >> 29; h *= 0xC2B2AE3D27D4EB4FULL; return h; }
    inline uint64_t Hash3(int64_t x, int64_t y, int64_t z)
    {
        uint64_t h = (uint64_t)x * 0x9E3779B97F4A7C15ULL;
        h ^= (uint64_t)y * 0xC2B2AE3D27D4EB4FULL;
        h ^= (uint64_t)z * 0x165667B19E3779F9ULL;
        h ^= h >> 31; h *= 0x9E3779B97F4A7C15ULL;
        return h;
    }
    inline int Find(std::vector<int>& parent, int i)
    {
        while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
        return i;
    }
}

// pos: 3 doubles per vertex; cellSize > 0; thresh2: the squared weld distance; cls: n ints, the class (a vertex index) per vertex.
EXPORT void haf_weld_classes(int n, const double* pos, double cellSize, double thresh2, int* cls)
{
    if (n <= 0) return;
    // the cells, as (long)Math.Floor(p / cell) per axis
    std::vector<int64_t> cell(3 * (size_t)n);
    int64_t mn[3] = { INT64_MAX, INT64_MAX, INT64_MAX }, mx[3] = { INT64_MIN, INT64_MIN, INT64_MIN };
    for (int i = 0; i < n; i++)
        for (int a = 0; a < 3; a++)
        {
            int64_t c = (int64_t)std::floor(pos[3 * i + a] / cellSize);
            cell[3 * i + a] = c;
            if (c < mn[a]) mn[a] = c; if (c > mx[a]) mx[a] = c;
        }
    int bits0 = Bits(mn[0], mx[0]), bits1 = Bits(mn[1], mx[1]), bits2 = Bits(mn[2], mx[2]);
    bool wide = bits0 + bits1 + bits2 > 63;   // a model whose cells do not pack into one long: compare the three coordinates
    auto pack = [&](int64_t x, int64_t y, int64_t z) -> int64_t { return (x - mn[0]) | ((y - mn[1]) << bits0) | ((z - mn[2]) << (bits0 + bits1)); };
    std::vector<int64_t> keys((size_t)n);
    for (int i = 0; i < n; i++)
        keys[i] = wide ? (int64_t)Hash3(cell[3 * i], cell[3 * i + 1], cell[3 * i + 2]) : pack(cell[3 * i], cell[3 * i + 1], cell[3 * i + 2]);
    // a counting sort by bucket: every bucket's members ascend
    int buckets = 16; while (buckets < n) buckets <<= 1;
    int mask = buckets - 1;
    auto bucketOf = [&](int64_t key) -> int { return (int)(Mix((uint64_t)key) >> 32) & mask; };
    std::vector<int> start((size_t)buckets + 1, 0), members((size_t)n);
    for (int i = 0; i < n; i++) start[bucketOf(keys[i]) + 1]++;
    for (int b = 0; b < buckets; b++) start[b + 1] += start[b];
    std::vector<int> fill(start.begin(), start.begin() + buckets);
    for (int i = 0; i < n; i++) members[fill[bucketOf(keys[i])]++] = i;

    std::vector<int> parent((size_t)n);
    for (int i = 0; i < n; i++) parent[i] = i;
    for (int i = 0; i < n; i++)
    {
        int64_t kx = cell[3 * i], ky = cell[3 * i + 1], kz = cell[3 * i + 2];
        double px = pos[3 * i], py = pos[3 * i + 1], pz = pos[3 * i + 2];
        for (int64_t dx = -1; dx <= 1; dx++) for (int64_t dy = -1; dy <= 1; dy++) for (int64_t dz = -1; dz <= 1; dz++)
        {
            int64_t x = kx + dx, y = ky + dy, z = kz + dz;
            if (!wide && (x < mn[0] || x > mx[0] || y < mn[1] || y > mx[1] || z < mn[2] || z > mx[2])) continue;   // outside the model's cells: empty
            int64_t key = wide ? (int64_t)Hash3(x, y, z) : pack(x, y, z);
            int b = bucketOf(key);
            for (int s = start[b], to = start[b + 1]; s < to; s++)
            {
                int j = members[s];
                if (j <= i || keys[j] != key) continue;
                if (wide && (cell[3 * j] != x || cell[3 * j + 1] != y || cell[3 * j + 2] != z)) continue;
                double ddx = px - pos[3 * j], ddy = py - pos[3 * j + 1], ddz = pz - pos[3 * j + 2];
                if (ddx * ddx + ddy * ddy + ddz * ddz <= thresh2)
                {
                    int a = Find(parent, i), c = Find(parent, j);
                    if (a != c) parent[a] = c;
                }
            }
        }
    }
    for (int i = 0; i < n; i++) cls[i] = Find(parent, i);
}
