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
    public static int EstimateQuads(int[] tris, IList<int> cell) => Greedy(tris, cell, null);

    // the estimate of every PREFIX of `cell` in one pass (review of PR #322: the balance search re-estimated the left
    // side from scratch at every probe). Exact, not an approximation: the greedy pairing of a prefix is the first steps
    // of the greedy pairing of the whole - a triangle only looks at the ones before it - so after m triangles the running
    // count (a quad per triangle that found no mate, none for one that did) IS EstimateQuads of those m.
    // prefix[m] = EstimateQuads(tris, cell[0..m)), m = 0..cell.Count.
    internal static int[] PrefixQuads(int[] tris, IList<int> cell)
    {
        var prefix = new int[cell.Count + 1];
        Greedy(tris, cell, prefix);
        return prefix;
    }

    static int Greedy(int[] tris, IList<int> cell, int[] prefix)
    {
        long Key(int x, int y) => x < y ? ((long)x << 32) | (uint)y : ((long)y << 32) | (uint)x;
        var owner = new Dictionary<long, int>(cell.Count * 2, EdgeKeys);
        var paired = new Dictionary<int, bool>(cell.Count);
        int quads = 0, running = 0, m = 0;
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
                running++;
            }
            if (prefix != null) prefix[++m] = running;
        }
        foreach (var kv in paired) if (!kv.Value) quads++;
        return quads;
    }

    // THE FEWEST CELLS THAT FIT, FILLED EVENLY (2026-11-25, user, the Heavy Monitor at 7.3 of 8 fragments: "I can never
    // seem to get more usage ... the spreading of the quads is not yet optimal").
    //   The first partition halved a cell at its MEDIAN TRIANGLE until every cell fitted. Two things followed. (1) The
    //   cells of one bake held the same number of triangles and different numbers of quads - how well triangles pair is a
    //   property of the region - so the cell that paired worst reached the budget while the others stood up to 12.6 % under
    //   it: that ship's eight cells of 25,207 triangles estimated 13,989 to 16,000 quads, and 50 triangles more were refused
    //   ("need more than 8 draw fragments"). (2) Halving only makes 1, 2, 4 or 8 cells: a mesh of five budgets was cut into
    //   eight.
    //   Now: k = the fewest cells the estimate can fit in; the cell is cut in two along its longest axis (whole triangles
    //   in centroid order, as before) where the two sides' estimated quads stand as floor(k/2) to ceil(k/2), and each side
    //   is cut on in the same way. A leaf can still come out over the budget, for three reasons (measured by the review):
    //   a cut separates pairs, a side re-sorted along its own longest axis estimates differently, and each cut balances
    //   only its own two sides, so the cost of the cuts below lands on one subtree. Then k + 1 is tried, up to `maxCells`.
    //   (The user's Heavy Monitor at 217,608 triangles: four leaves at 16,000, four at 15,864 to 15,908.)
    //   k STARTS from the root's estimate in the order the cuts see it - sorted along the longest axis - not in the
    //   mesh's own triangle order (review of PR #322: a mesh stored in scattered order estimates far higher in index
    //   order, and the first version started past the k that fits, or past maxCells and so at null where the halving
    //   fitted), and at most at maxCells: whether maxCells fit is the partition's to say, not the estimate's.
    // `centroids` is flat xyz per triangle. Cells come back ordered by their minimum centroid (x, then y, then z), so chunk
    // letters are stable across re-bakes. Returns null when the mesh needs more than `maxCells` chunks.
    public static List<List<int>> PartitionByQuadBudget(int[] tris, float[] centroids, int budget, int maxCells)
    {
        int total = tris.Length / 3;
        var all = new List<int>(total);
        for (int i = 0; i < total; i++) all.Add(i);
        List<List<int>> cells = null;
        if (total <= 1 || EstimateQuads(tris, all) <= budget) cells = new List<List<int>> { all };
        else if (budget <= 0) return null;   // nothing of more than one triangle fits (and no division by it below)
        else
        {
            var sorted = new List<int>(all);
            SortAlongLongestAxis(centroids, sorted);
            long whole = EstimateQuads(tris, sorted);
            int start = (int)System.Math.Min(maxCells, System.Math.Max(2L, (whole + budget - 1) / budget));
            for (int k = start; k <= maxCells && cells == null; k++)
            {
                var made = new List<List<int>>();
                if (SplitInto(tris, centroids, new List<int>(all), k, budget, made)) cells = made;
            }
        }
        if (cells == null) return null;
        cells.Sort((a, b) =>
        {
            float[] ma = MinCentroid(a, centroids), mb = MinCentroid(b, centroids);
            int c = ma[0].CompareTo(mb[0]); if (c != 0) return c;
            c = ma[1].CompareTo(mb[1]); if (c != 0) return c;
            return ma[2].CompareTo(mb[2]);
        });
        return cells;
    }

    // `cell` into k cells of about equal estimated quads, appended to `cells`; false when one of them is over the budget
    static bool SplitInto(int[] tris, float[] centroids, List<int> cell, int k, int budget, List<List<int>> cells)
    {
        if (k <= 1 || cell.Count <= 1)
        {
            if (cell.Count > 1 && EstimateQuads(tris, cell) > budget) return false;
            cells.Add(cell); return true;
        }
        SortAlongLongestAxis(centroids, cell);
        int kl = k / 2, kr = k - kl;
        // the cut: the first position where the left side's share of the quads reaches kl of k. The left estimate only
        // grows with the position (a triangle more is a quad more or a pair made); the right one shrinks on the whole.
        int[] left = PrefixQuads(tris, cell);   // the left side at every position, in one pass
        int lo = 1, hi = cell.Count - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            long l = left[mid], r = EstimateQuads(tris, cell.GetRange(mid, cell.Count - mid));
            if (l * kr >= r * kl) hi = mid; else lo = mid + 1;
        }
        return SplitInto(tris, centroids, cell.GetRange(0, lo), kl, budget, cells)
            && SplitInto(tris, centroids, cell.GetRange(lo, cell.Count - lo), kr, budget, cells);
    }

    // whole triangles in centroid order along the cell's longest axis (x, then y, then z on a tie of extents), the
    // triangle index breaking a tie of centroids: the order every cut and every k start is judged in
    static void SortAlongLongestAxis(float[] centroids, List<int> cell)
    {
        float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
        foreach (int ti in cell)
            for (int a = 0; a < 3; a++) { float c = centroids[ti * 3 + a]; if (c < mn[a]) mn[a] = c; if (c > mx[a]) mx[a] = c; }
        float sx = mx[0] - mn[0], sy = mx[1] - mn[1], sz = mx[2] - mn[2];
        int ax = sx >= sy && sx >= sz ? 0 : (sy >= sz ? 1 : 2);
        cell.Sort((p, q) => { int c = centroids[p * 3 + ax].CompareTo(centroids[q * 3 + ax]); return c != 0 ? c : p.CompareTo(q); });
    }

    static float[] MinCentroid(List<int> cell, float[] centroids)
    {
        float[] m = { float.MaxValue, float.MaxValue, float.MaxValue };
        foreach (int ti in cell)
            for (int a = 0; a < 3; a++) if (centroids[ti * 3 + a] < m[a]) m[a] = centroids[ti * 3 + a];
        return m;
    }
}
