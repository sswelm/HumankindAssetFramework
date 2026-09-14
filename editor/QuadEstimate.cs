// QuadEstimate.cs — the PURE half of the multi-mesh split (UniversalBaker.SplitForQuadCeiling): how many quads the
// SDK will encode for a set of triangles, and how to partition an over-ceiling mesh into chunks that each fit.
// No Unity types: production compiles this in the editor assembly, Tests/HumankindAssetFramework.Tests.csproj
// compiles the same source Unity-free (the EditorRules / GlbDisconnectedParts pattern). Until 2026-09-14 both
// routines were reachable only through the opt-in editor lane (tools/editor_tests.ps1), which neither CI nor the
// pre-push hook runs — and the estimator's one shipped bug (tris/2, review P1 of 2026-09-13) is exactly the kind a
// six-line unit test catches.
using System.Collections.Generic;

public static class QuadEstimate
{
    // The engine draws at most 255 × 64 quads per FRAGMENT (unit-mesh-render-clamp, 2026-09-12): the compiled pawn
    // shader's stride. A fragment over it silently drops its tail in-game.
    public const int EngineQuadCeiling = 255 * 64;

    // MEASURED, not assumed (2026-09-13, the 121-second bake): .NET's long hash is `low ^ high`, and a mesh
    // edge key packing (v, v+1) XOR-collapses to tiny values — half a hull's edges landed in a handful of
    // dictionary buckets, turning every lookup into a chain walk (11.2s vs 0.01s on a 179k-tri grid, same
    // result). A murmur-style finalizer on the same keys restores O(1); the key values are untouched.
    sealed class EdgeKeyComparer : IEqualityComparer<long>
    {
        public bool Equals(long x, long y) => x == y;
        public int GetHashCode(long k)
        {
            unchecked { ulong z = (ulong)k; z ^= z >> 33; z *= 0xFF51AFD7ED558CCDUL; z ^= z >> 33; return (int)z ^ (int)(z >> 32); }
        }
    }
    static readonly EdgeKeyComparer EdgeKeys = new EdgeKeyComparer();

    // QUAD-AWARE COUNT (review P1, 2026-09-13): the SDK does NOT turn every two triangles into one quad — it pairs
    // only triangles SHARING AN EDGE, and every unpaired triangle costs a full quad. A Faceted bake (unwelded: no
    // shared indices at all) encodes quads == tris, so the old tris/2 budget could ship chunks up to 2x over the
    // ceiling; even the welded Bremen paired at 0.6 quads/tri, not 0.5. Greedy pairing in triangle order — the
    // common quad triangulation emits its pair adjacently. `cell` lists the triangle indices to count.
    public static int EstimateQuads(int[] tris, IList<int> cell)
    {
        long Key(int x, int y) => x < y ? ((long)x << 32) | (uint)y : ((long)y << 32) | (uint)x;
        var owner = new Dictionary<long, int>(cell.Count * 2, EdgeKeys);
        var paired = new Dictionary<int, bool>(cell.Count);
        int quads = 0;
        foreach (int t in cell)
        {
            int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
            long e0 = Key(a, b), e1 = Key(b, c), e2 = Key(c, a);   // no per-triangle array — this runs at every BSP level
            int mate = -1;
            if (owner.TryGetValue(e0, out int o0) && !paired[o0]) mate = o0;
            else if (owner.TryGetValue(e1, out int o1) && !paired[o1]) mate = o1;
            else if (owner.TryGetValue(e2, out int o2) && !paired[o2]) mate = o2;
            if (mate >= 0) { paired[mate] = true; paired[t] = true; quads++; }
            else
            {
                paired[t] = false;
                owner[e0] = t; owner[e1] = t; owner[e2] = t;
            }
        }
        foreach (var kv in paired) if (!kv.Value) quads++;
        return quads;
    }

    // BSP partition at the triangle-centroid median on the longest axis (the Workshop plane cut's proven partition
    // — whole triangles, deterministic order) until every cell estimates at or under `budget` quads. `centroids`
    // is flat xyz per triangle. Cells come back ordered by their minimum centroid (x, then y, then z), so chunk
    // letters are stable across re-bakes. Returns null when the mesh needs more than `maxCells` chunks.
    public static List<List<int>> PartitionByQuadBudget(int[] tris, float[] centroids, int budget, int maxCells)
    {
        int total = tris.Length / 3;
        var all = new List<int>(total);
        for (int i = 0; i < total; i++) all.Add(i);
        var stack = new List<List<int>> { all };
        var cells = new List<List<int>>();
        while (stack.Count > 0)
        {
            var cell = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1);
            if (cell.Count <= 1 || EstimateQuads(tris, cell) <= budget) { cells.Add(cell); continue; }
            float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
            foreach (int ti in cell)
                for (int a = 0; a < 3; a++) { float c = centroids[ti * 3 + a]; if (c < mn[a]) mn[a] = c; if (c > mx[a]) mx[a] = c; }
            float sx = mx[0] - mn[0], sy = mx[1] - mn[1], sz = mx[2] - mn[2];
            int ax = sx >= sy && sx >= sz ? 0 : (sy >= sz ? 1 : 2);
            cell.Sort((p, q) => { int c = centroids[p * 3 + ax].CompareTo(centroids[q * 3 + ax]); return c != 0 ? c : p.CompareTo(q); });
            int mid = cell.Count / 2;
            stack.Add(cell.GetRange(0, mid)); stack.Add(cell.GetRange(mid, cell.Count - mid));
        }
        if (cells.Count > maxCells) return null;
        cells.Sort((a, b) =>
        {
            float[] ma = MinCentroid(a, centroids), mb = MinCentroid(b, centroids);
            int c = ma[0].CompareTo(mb[0]); if (c != 0) return c;
            c = ma[1].CompareTo(mb[1]); if (c != 0) return c;
            return ma[2].CompareTo(mb[2]);
        });
        return cells;
    }

    static float[] MinCentroid(List<int> cell, float[] centroids)
    {
        float[] m = { float.MaxValue, float.MaxValue, float.MaxValue };
        foreach (int ti in cell)
            for (int a = 0; a < 3; a++) if (centroids[ti * 3 + a] < m[a]) m[a] = centroids[ti * 3 + a];
        return m;
    }
}
