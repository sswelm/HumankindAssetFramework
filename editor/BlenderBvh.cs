// BlenderBvh.cs - mathutils.bvhtree.BVHTree.FromPolygons over triangles, and its ray_cast, as Blender 5.1 builds and walks
// it (2026-11-16, for the Vehicle Lab's Two-sided role). VehicleProbe's TriangleBvh answers "does the ray meet anything?",
// which no tree shape can change; the Two-sided pass asks WHICH triangle a ray meets first and how far away, and on a tie
// (two triangles at the same distance: a plain duplicate face) the answer is the one Blender's walk reaches first. So this
// is BLI_kdopbvh.cc itself, for the tree FromPolygons asks for (tree_type 4, axis 6: four children, an axis-aligned box):
//   - BLI_bvhtree_insert: a leaf per triangle, its box the three corners' min and max, grown by FLT_EPSILON (the tree's
//     epsilon 0 is clamped up to it);
//   - BLI_bvhtree_balance: the implicit tree of non_recursive_bvh_div_nodes - level by level, a branch's box refitted
//     over its leaves, its widest axis the split axis, its leaves partitioned on their box's MAX along it
//     (partition_nth_element: the median of three, bvh_partition, an insertion sort under four);
//   - BLI_bvhtree_ray_cast with the watertight test: fast_ray_nearest_hit against every box (pruned by the nearest hit
//     so far), the children forward or backward by the ray's sign on the branch's axis, isect_ray_tri_watertight_v3 on a
//     leaf, a hit taken when 0 <= dist < the nearest so far.
// Every operation is float32, as the C is (Mono evaluates an uncast float product in double).
using System;

internal sealed class BlenderBvh
{
    const float FltEpsilon = 1.1920929e-7f;
    const int TreeType = 4;

    readonly float[] co;       // the vertices, 3 per vertex
    readonly int[] tris;       // the triangles, 3 vertex indices each; a hit's index is the triangle's
    readonly int leafNum;      // nodes 0 .. leafNum - 1 are the leaves (node i = triangle i), the branches follow
    readonly float[] bv;       // per node: min x, max x, min y, max y, min z, max z
    readonly int[] children;   // per node: four children, the first nodeNum of them set
    readonly byte[] nodeNum, mainAxis;
    readonly int root;         // -1 without a triangle: every cast misses

    public BlenderBvh(float[] vertices, int[] triangles)
    {
        co = vertices; tris = triangles; leafNum = triangles.Length / 3; root = -1;
        if (leafNum == 0) return;
        int branchNum = NeededBranches(leafNum), numNodes = leafNum + branchNum + TreeType;
        bv = new float[6 * numNodes]; children = new int[TreeType * numNodes]; nodeNum = new byte[numNodes]; mainAxis = new byte[numNodes];
        for (int i = 0; i < leafNum; i++)
        {
            // create_kdop_hull, then bvhtree_node_inflate by the tree's epsilon
            for (int ax = 0; ax < 3; ax++)
            {
                float mn = float.MaxValue, mx = -float.MaxValue;
                for (int k = 0; k < 3; k++) { float c = co[3 * tris[3 * i + k] + ax]; if (c < mn) mn = c; if (mx < c) mx = c; }
                bv[6 * i + 2 * ax] = (float)(mn - FltEpsilon); bv[6 * i + 2 * ax + 1] = (float)(mx + FltEpsilon);
            }
        }
        var leafs = new int[leafNum]; for (int i = 0; i < leafNum; i++) leafs[i] = i;
        int branches = leafNum - 1;   // branches_array: branch j is node branches + j, the root is branch 1
        root = branches + 1;
        if (leafNum == 1)
        {
            Refit(root, leafs, 0, 1); mainAxis[root] = (byte)(LargestAxis(root) / 2); nodeNum[root] = 1; children[TreeType * root] = leafs[0];
            return;
        }
        // build_implicit_tree_helper
        var leafsPerChild = new int[33]; var branchesOnLevel = new int[33];
        for (leafsPerChild[0] = 1; leafsPerChild[0] < leafNum; leafsPerChild[0] *= TreeType) { }
        branchesOnLevel[0] = 1;
        for (int d = 1; d < 32 && leafsPerChild[d - 1] != 0; d++) { branchesOnLevel[d] = unchecked(branchesOnLevel[d - 1] * TreeType); leafsPerChild[d] = leafsPerChild[d - 1] / TreeType; }
        int remain = leafNum - leafsPerChild[1], remainLeafs = remain + (remain + TreeType - 2) / (TreeType - 1);
        int LeafsIndex(int depth, int childIndex)   // implicit_leafs_index
        {
            long min = (long)childIndex * leafsPerChild[depth - 1];
            if (min <= remainLeafs) return (int)min;
            if (leafsPerChild[depth] != 0) return leafNum - (branchesOnLevel[depth - 1] - childIndex) * leafsPerChild[depth];
            return remainLeafs;
        }
        const int treeOffset = 2 - TreeType; var nth = new int[TreeType + 1];
        for (int i = 1, depth = 1; i <= branchNum; i = i * TreeType + treeOffset, depth++)
        {
            int firstOfNextLevel = i * TreeType + treeOffset, stop = Math.Min(firstOfNextLevel, branchNum + 1);
            for (int j = i; j < stop; j++)
            {
                // non_recursive_bvh_div_nodes_task_cb
                int parent = branches + j, begin = LeafsIndex(depth, j - i), end = LeafsIndex(depth, j - i + 1);
                Refit(parent, leafs, begin, end);
                int splitAxis = LargestAxis(parent); mainAxis[parent] = (byte)(splitAxis / 2);
                nth[0] = begin; nth[TreeType] = end;
                for (int k = 1; k < TreeType; k++) nth[k] = LeafsIndex(depth + 1, j * TreeType + treeOffset + k - firstOfNextLevel);
                for (int k = 0; k < TreeType - 1; k++)   // split_leafs
                {
                    if (nth[k] >= nth[TreeType]) break;
                    PartitionNth(leafs, nth[k], nth[TreeType], nth[k + 1], splitAxis);
                }
                int n = 0;
                for (; n < TreeType; n++)
                {
                    int childIndex = j * TreeType + treeOffset + n, level = childIndex - firstOfNextLevel;
                    int cb = LeafsIndex(depth + 1, level), ce = LeafsIndex(depth + 1, level + 1);
                    if (ce - cb > 1) children[TreeType * parent + n] = branches + childIndex;
                    else if (ce - cb == 1) children[TreeType * parent + n] = leafs[cb];
                    else break;
                }
                nodeNum[parent] = (byte)n;
            }
        }
    }

    static int NeededBranches(int leafs) => Math.Max(1, (leafs + TreeType - 3) / (TreeType - 1));   // implicit_needed_branches

    void Refit(int node, int[] leafs, int start, int end)   // refit_kdop_hull: std::min(new, old) keeps the new one on a tie
    {
        for (int ax = 0; ax < 3; ax++)
        {
            float mn = float.MaxValue, mx = -float.MaxValue;
            for (int j = start; j < end; j++)
            {
                float a = bv[6 * leafs[j] + 2 * ax], b = bv[6 * leafs[j] + 2 * ax + 1];
                if (!(mn < a)) mn = a;
                if (!(b < mx)) mx = b;
            }
            bv[6 * node + 2 * ax] = mn; bv[6 * node + 2 * ax + 1] = mx;
        }
    }

    int LargestAxis(int node)   // get_largest_axis: the place of the widest axis' MAX in the box
    {
        float x = (float)(bv[6 * node + 1] - bv[6 * node]), y = (float)(bv[6 * node + 3] - bv[6 * node + 2]), z = (float)(bv[6 * node + 5] - bv[6 * node + 4]);
        if (x > y) return x > z ? 1 : 5;
        return y > z ? 3 : 5;
    }

    void PartitionNth(int[] a, int begin, int end, int n, int axis)   // partition_nth_element
    {
        while (end - begin > 3)
        {
            int cut = Partition(a, begin, end, MedianOf3(a, begin, (begin + end) / 2, end - 1, axis), axis);
            if (cut <= n) begin = cut; else end = cut;
        }
        for (int i = begin; i < end; i++)   // bvh_insertionsort
        {
            int j = i, t = a[i]; float tv = bv[6 * t + axis];
            while (j != begin && tv < bv[6 * a[j - 1] + axis]) { a[j] = a[j - 1]; j--; }
            a[j] = t;
        }
    }

    int Partition(int[] a, int lo, int hi, float x, int axis)   // bvh_partition: x is the pivot node's value, which no swap changes
    {
        int i = lo, j = hi;
        while (true)
        {
            while (bv[6 * a[i] + axis] < x) i++;
            j--;
            while (x < bv[6 * a[j] + axis]) j--;
            if (!(i < j)) return i;
            int t = a[i]; a[i] = a[j]; a[j] = t;
            i++;
        }
    }

    float MedianOf3(int[] a, int lo, int mid, int hi, int axis)   // bvh_medianof3
    {
        float l = bv[6 * a[lo] + axis], m = bv[6 * a[mid] + axis], h = bv[6 * a[hi] + axis];
        if (m < l)
        {
            if (h < m) return m;
            if (h < l) return h;
            return l;
        }
        if (h < m)
        {
            if (h < l) return l;
            return h;
        }
        return m;
    }

    /// <summary>One cast's state: BVHRayCastData. A struct on the caller's stack, so casts run on any number of threads.</summary>
    struct Cast
    {
        public float Ox, Oy, Oz, I0, I1, I2, D0, D1, D2, Sx, Sy, Sz, Dist;
        public int N0, N1, N2, Kx, Ky, Kz, Index;
    }

    /// <summary>BVHTree.ray_cast(origin, direction, distance): the index of the nearest triangle the ray meets within
    /// maxDist (FLT_MAX: no limit) and its distance, or -1. The direction is normalised as the wrapper does it.</summary>
    public int RayCast(float ox, float oy, float oz, float dx, float dy, float dz, float maxDist, out float dist)
    {
        // normalize_v3
        float dd = (float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz));
        if (dd > 1.0e-35f) { dd = (float)Math.Sqrt(dd); float s = (float)(1.0f / dd); dx = (float)(dx * s); dy = (float)(dy * s); dz = (float)(dz * s); }
        else { dx = 0f; dy = 0f; dz = 0f; }
        var c = new Cast { Ox = ox, Oy = oy, Oz = oz, Dist = maxDist, Index = -1 };
        if (root < 0) { dist = maxDist; return -1; }
        // bvhtree_ray_cast_data_precalc
        c.D0 = Math.Abs(dx) < FltEpsilon ? 0f : dx; c.I0 = Math.Abs(dx) < FltEpsilon ? float.MaxValue : (float)(1.0f / dx); c.N0 = c.I0 < 0f ? 1 : 0;
        c.D1 = Math.Abs(dy) < FltEpsilon ? 0f : dy; c.I1 = Math.Abs(dy) < FltEpsilon ? float.MaxValue : (float)(1.0f / dy); c.N1 = c.I1 < 0f ? 1 : 0;
        c.D2 = Math.Abs(dz) < FltEpsilon ? 0f : dz; c.I2 = Math.Abs(dz) < FltEpsilon ? float.MaxValue : (float)(1.0f / dz); c.N2 = c.I2 < 0f ? 1 : 0;
        // isect_ray_tri_watertight_v3_precalc: axis_dominant_v3_single (ties go to x, then y)
        float xn = Math.Abs(dx), yn = Math.Abs(dy), zn = Math.Abs(dz);
        int kz = (xn >= yn && xn >= zn) ? 0 : (yn >= xn && yn >= zn) ? 1 : 2;
        int kx = kz != 2 ? kz + 1 : 0, ky = kx != 2 ? kx + 1 : 0;
        float dkz = kz == 0 ? dx : kz == 1 ? dy : dz;
        if (dkz < 0f) { int t = kx; kx = ky; ky = t; }
        float inv = (float)(1.0f / dkz);
        c.Sx = (float)((kx == 0 ? dx : kx == 1 ? dy : dz) * inv); c.Sy = (float)((ky == 0 ? dx : ky == 1 ? dy : dz) * inv); c.Sz = inv;
        c.Kx = kx; c.Ky = ky; c.Kz = kz;
        Walk(ref c, root);
        dist = c.Dist; return c.Index;
    }

    void Walk(ref Cast c, int node)   // dfs_raycast
    {
        // fast_ray_nearest_hit
        int b = 6 * node;
        float t1x = (float)((float)(bv[b + c.N0] - c.Ox) * c.I0), t2x = (float)((float)(bv[b + 1 - c.N0] - c.Ox) * c.I0);
        float t1y = (float)((float)(bv[b + 2 + c.N1] - c.Oy) * c.I1), t2y = (float)((float)(bv[b + 3 - c.N1] - c.Oy) * c.I1);
        float t1z = (float)((float)(bv[b + 4 + c.N2] - c.Oz) * c.I2), t2z = (float)((float)(bv[b + 5 - c.N2] - c.Oz) * c.I2);
        float near;
        if ((t1x > t2y || t2x < t1y || t1x > t2z || t2x < t1z || t1y > t2z || t2y < t1z) || (t2x < 0f || t2y < 0f || t2z < 0f) ||
            (t1x > c.Dist || t1y > c.Dist || t1z > c.Dist)) near = float.MaxValue;
        else { near = t1x > t1y ? t1x : t1y; near = near > t1z ? near : t1z; }   // max_fff
        if (near >= c.Dist) return;
        int n = nodeNum[node];
        if (n == 0)
        {
            // py_bvhtree_raycast_cb -> bvhtree_ray_tri_intersection -> isect_ray_tri_watertight_v3
            int ia = 3 * tris[3 * node], ib = 3 * tris[3 * node + 1], ic = 3 * tris[3 * node + 2];
            float a0 = (float)(co[ia] - c.Ox), a1 = (float)(co[ia + 1] - c.Oy), a2 = (float)(co[ia + 2] - c.Oz);
            float b0 = (float)(co[ib] - c.Ox), b1 = (float)(co[ib + 1] - c.Oy), b2 = (float)(co[ib + 2] - c.Oz);
            float c0 = (float)(co[ic] - c.Ox), c1 = (float)(co[ic + 1] - c.Oy), c2 = (float)(co[ic + 2] - c.Oz);
            float akx = c.Kx == 0 ? a0 : c.Kx == 1 ? a1 : a2, aky = c.Ky == 0 ? a0 : c.Ky == 1 ? a1 : a2, akz = c.Kz == 0 ? a0 : c.Kz == 1 ? a1 : a2;
            float bkx = c.Kx == 0 ? b0 : c.Kx == 1 ? b1 : b2, bky = c.Ky == 0 ? b0 : c.Ky == 1 ? b1 : b2, bkz = c.Kz == 0 ? b0 : c.Kz == 1 ? b1 : b2;
            float ckx = c.Kx == 0 ? c0 : c.Kx == 1 ? c1 : c2, cky = c.Ky == 0 ? c0 : c.Ky == 1 ? c1 : c2, ckz = c.Kz == 0 ? c0 : c.Kz == 1 ? c1 : c2;
            float ax = (float)(akx - (float)(c.Sx * akz)), ay = (float)(aky - (float)(c.Sy * akz));
            float bx = (float)(bkx - (float)(c.Sx * bkz)), by = (float)(bky - (float)(c.Sy * bkz));
            float cx = (float)(ckx - (float)(c.Sx * ckz)), cy = (float)(cky - (float)(c.Sy * ckz));
            float u = (float)((float)(cx * by) - (float)(cy * bx)), v = (float)((float)(ax * cy) - (float)(ay * cx)), w = (float)((float)(bx * ay) - (float)(by * ax));
            if ((u < 0f || v < 0f || w < 0f) && (u > 0f || v > 0f || w > 0f)) return;
            float det = (float)((float)(u + v) + w);
            if (det == 0f || float.IsNaN(det) || float.IsInfinity(det)) return;
            float t = (float)((float)((float)((float)(u * akz) + (float)(v * bkz)) + (float)(w * ckz)) * c.Sz);
            float signT = det < 0f ? -t : t;
            if (signT < 0f) return;
            float d = (float)(t * (float)(1.0f / det));
            if (d >= 0f && d < c.Dist) { c.Index = node; c.Dist = d; }
            return;
        }
        float dot = mainAxis[node] == 0 ? c.D0 : mainAxis[node] == 1 ? c.D1 : c.D2;
        if (dot > 0f) for (int i = 0; i != n; i++) Walk(ref c, children[TreeType * node + i]);
        else for (int i = n - 1; i >= 0; i--) Walk(ref c, children[TreeType * node + i]);
    }
}
