using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // REVIEW OF PR #322, ROUND 2 (planted defects): one fixture for every defect planted in the partition that the first
    // tests let through. Each comment names the plant it catches (K2, L3, ... - the planting script is in the review's
    // scratch folder); the plants that change nothing observable are left without one.
    public class QuadPartitionReviewTests
    {
        // K2 (the k start not capped at maxCells). Two columns of 124 quads, 200 apart along x; each triangle's x centroid
        // is scattered inside its column, so in the root's order (sorted along x) the greedy estimate is 286, over 4 x 63 -
        // yet each column re-sorted along its own longest axis (y) pairs every quad and halves into 62 + 62. The capped
        // start tries k = 4 and fits; an uncapped start begins at ceil(286 / 63) = 5 > 4 and returns null.
        [Fact]
        public void The_cap_is_tried_even_when_the_roots_estimate_is_past_it()
        {
            int[] tris = TwoScatteredColumns(124, 7, out float[] cent);
            var byX = Enumerable.Range(0, tris.Length / 3).OrderBy(i => cent[i * 3]).ThenBy(i => i).ToList();
            Assert.Equal(286, QuadEstimate.EstimateQuads(tris, byX));                 // the fixture: over 4 x 63 in the root's order
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 63, maxCells: 4);
            Assert.NotNull(cells);
            Assert.Equal(4, cells.Count);
            Assert.All(cells, c => Assert.True(QuadEstimate.EstimateQuads(tris, c) <= 63));
            Assert.Equal(Enumerable.Range(0, tris.Length / 3), cells.SelectMany(c => c).OrderBy(i => i));
        }

        // L3 (k += 2) - and it pins the existing adaptive assertion `sum > whole ? 3 : 2`, which accepts whatever came out.
        // A 20 x 20 grid at a budget of 80: 400 quads start at k = 5, five cells do not fit (the cuts cost quads), six do.
        [Fact]
        public void When_the_first_k_does_not_fit_the_next_one_is_tried()
        {
            int[] tris = Grid(20, 20, out float[] cent);
            Assert.Equal(400, QuadEstimate.EstimateQuads(tris, Enumerable.Range(0, 800).ToList()));   // start = ceil(400 / 80) = 5
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 80, maxCells: 8);
            Assert.Equal(6, cells.Count);
            Assert.All(cells, c => Assert.True(QuadEstimate.EstimateQuads(tris, c) <= 80));
            Assert.Null(QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 80, maxCells: 5));      // five really do not fit
        }

        // B5 (the larger share of an odd k to the left), B6 (the right side as whole - left): the exact cuts of a 3-way and a
        // 5-way split of a 100-quad strip. floor(k/2) cells go left, and the cut is the first position where the left side's
        // estimate reaches kl/k of the two sides'.
        [Theory]
        [InlineData(36, new[] { 67, 67, 66 })]
        // B5, derived by hand: 6 = 3 + 3, and each 100-triangle half cut 1:2 at the first p with ceil(p/2) x 2 >= quads of
        // the rest - p = 33 (17 x 2 >= 34; p = 32: 16 x 2 < 34) - then the 67 cut 1:1 at 33 | 34. With the larger share
        // on the left each half comes out 33, 34, 33.
        [InlineData(19, new[] { 33, 33, 34, 33, 33, 34 })]
        [InlineData(80, new[] { 100, 100 })]
        [InlineData(50, new[] { 100, 100 })]   // F2 (leaf >= budget): two cells of EXACTLY the budget fit
        [InlineData(25, new[] { 50, 50, 50, 50 })]
        public void The_cuts_of_an_odd_k_put_the_smaller_share_on_the_left(int budget, int[] sizes)
        {
            int[] tris = Grid(100, 1, out float[] cent);
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget, maxCells: 8);
            Assert.Equal(sizes, cells.Select(c => c.Count).ToArray());
        }

        // B6: a cut between the two triangles of a grid column separates up to `ny` pairs, so the right side's real
        // estimate is more than (whole - left). A 10 x 10 grid cut in two: the cut lands where the two sides' FRESH estimates
        // balance.
        [Fact]
        public void The_right_side_is_estimated_afresh_not_as_the_whole_minus_the_left()
        {
            int[] tris = Grid(10, 10, out float[] cent);
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 55, maxCells: 2);
            Assert.NotNull(cells);
            var q = cells.Select(c => QuadEstimate.EstimateQuads(tris, c)).ToArray();
            Assert.True(Math.Abs(q[0] - q[1]) <= 1, $"{q[0]} vs {q[1]}");
        }

        // S3 (hi = Count - 2): three lone triangles at a budget of 2 - the first position where the left reaches half the
        // quads is the LAST one (left 2, right 1); hi = Count - 2 cuts left 1, right 2
        [Fact]
        public void The_cut_can_land_on_the_last_position()
        {
            int[] tris = Enumerable.Range(0, 9).ToArray();
            float[] cent = { 0, 0, 0,  1, 0, 0,  2, 0, 0 };
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 2, maxCells: 8);
            Assert.Equal(new[] { "0,1", "2" }, cells.Select(c => string.Join(",", c)).ToArray());
        }

        // O1 (the final sort removed), O3 (y ignored): the cells come back by minimum centroid x, THEN y. Thirty lone
        // triangles at x = 0 with y falling as the index rises (y = 30..1), and ten at x = 50 (y = 0..9). Sorted along x
        // (index breaking the tie at x = 0), the cut at 20 leaves the left cell {0..19} at min (0, 11) and the right cell at
        // min (0, 0): equal min x, so only y orders them - right cell first. The recursion emits the left one first.
        [Fact]
        public void Cells_with_the_same_min_x_are_ordered_by_min_y()
        {
            var t = new List<int>(); var c = new List<float>();
            for (int i = 0; i < 30; i++) { t.AddRange(new[] { 3 * i, 3 * i + 1, 3 * i + 2 }); c.AddRange(new[] { 0f, 30f - i, 0f }); }
            for (int i = 0; i < 10; i++) { int b = 90 + 3 * i; t.AddRange(new[] { b, b + 1, b + 2 }); c.AddRange(new[] { 50f, i, 0f }); }
            int[] tris = t.ToArray(); float[] cent = c.ToArray();
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 20, maxCells: 8);
            Assert.Equal(2, cells.Count);
            Assert.Equal(Enumerable.Range(20, 20), cells[0].OrderBy(i => i));   // min (0, 0)
            Assert.Equal(Enumerable.Range(0, 20), cells[1].OrderBy(i => i));    // min (0, 11)
        }

        // O1 on geometry: a 10 x 12 grid at a budget of 31 is cut along y first (12 > 10), then each half along x: the
        // recursion emits bottom-left, bottom-right, top-left, top-right; the cells come back bottom-left, top-left,
        // bottom-right, top-right (min x, then min y)
        [Fact]
        public void Cells_come_back_by_min_x_then_min_y_not_in_the_order_the_cuts_made_them()
        {
            int[] tris = Grid(10, 12, out float[] cent);
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 31, maxCells: 8);
            var mins = cells.Select(cl => (cl.Min(ti => cent[ti * 3]), cl.Min(ti => cent[ti * 3 + 1]))).ToArray();
            Assert.Equal(new[] { (0.33f, 0.33f), (0.33f, 6.33f), (5.33f, 0.33f), (5.33f, 6.33f) }, mins);
        }

        // G3 (the budget guard ahead of the fit check), R3 (total <= 1 dropped): the PR keeps master's answer for a mesh of
        // at most one triangle or one that already fits, even at a budget of nothing - one cell, not null
        [Fact]
        public void A_budget_of_nothing_still_returns_a_mesh_of_one_triangle_or_none_whole()
        {
            var one = QuadEstimate.PartitionByQuadBudget(new[] { 0, 1, 2 }, new[] { 0f, 0f, 0f }, budget: 0, maxCells: 8);
            Assert.Single(one); Assert.Equal(new[] { 0 }, one[0]);
            var none = QuadEstimate.PartitionByQuadBudget(new int[0], new float[0], budget: 0, maxCells: 8);
            Assert.Single(none); Assert.Empty(none[0]);
        }

        // R4 (the fit judged in sorted order): the one cell that comes back is the mesh in its STORED order, so that is the
        // order its fit must be judged in. The scattered strip estimates 140 stored, 124 sorted; at a budget of 130 it must
        // be split, or the single cell ships 10 over the budget.
        [Fact]
        public void The_single_cell_fits_in_the_order_it_is_returned_in()
        {
            int[] grid = Grid(124, 1, out float[] gc);
            var order = Enumerable.Range(0, 248).ToList();
            var rng = new Random(7);
            for (int i = order.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); int x = order[i]; order[i] = order[j]; order[j] = x; }
            var tris = new int[grid.Length]; var cent = new float[gc.Length];
            for (int i = 0; i < 248; i++) { Array.Copy(grid, order[i] * 3, tris, i * 3, 3); Array.Copy(gc, order[i] * 3, cent, i * 3, 3); }
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 130, maxCells: 8);
            Assert.All(cells, cl => Assert.True(QuadEstimate.EstimateQuads(tris, cl) <= 130));
        }

        // N3 (one cell past maxCells allowed): the strip needs three cells at a budget of 36, so two is null - the existing
        // null cases need four or nine, which one more cell does not reach either
        [Fact]
        public void One_cell_past_the_cap_is_still_null()
        {
            int[] tris = Grid(100, 1, out float[] cent);
            Assert.Equal(3, QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 36, maxCells: 3).Count);
            Assert.Null(QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 36, maxCells: 2));
        }

        // A1 (always x): a grid tall in y is cut ACROSS y, every cell keeps the full width. Every existing fixture is longest
        // in x, so an axis choice that ignored y and z passed them all.
        [Fact]
        public void A_tall_mesh_is_cut_along_its_longest_axis()
        {
            int[] tris = Grid(2, 50, out float[] cent);
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 30, maxCells: 8);
            Assert.Equal(4, cells.Count);
            Assert.All(cells, cl => { Assert.Equal(0.33f, cl.Min(ti => cent[ti * 3])); Assert.Equal(1 + 0.66f, cl.Max(ti => cent[ti * 3])); });
        }

        // A3 (a tie of extents goes to z, then y): a square grid's x and y extents are equal - the cut is along x
        [Fact]
        public void A_tie_of_extents_is_cut_along_x()
        {
            int[] tris = Grid(10, 10, out float[] cent);
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 51, maxCells: 8);
            Assert.Equal(new[] { 0.33f, 5.33f }, cells.Select(cl => cl.Min(ti => cent[ti * 3])).ToArray());
        }

        // T1/T2 (the tie-break), derived by hand: a 10 x 3 grid (30 quads) at a budget of 12 starts at k = 3, so the first cut
        // gives the left side a third. Along x, column i holds its three first triangles (x = i + .33, indices 2(10j + i))
        // then its three second ones. Columns 0-2 are 9 quads against 21; the first triangles of column 3 add a quad each:
        // after two of them the left holds 11 and the right 21 (11 x 2 >= 21). WHICH two is the tie-break's: the lower
        // indices, rows 0 and 1 - triangles 6 and 26.
        [Fact]
        public void A_tie_of_centroids_goes_to_the_lower_triangle_index()
        {
            int[] tris = Grid(10, 3, out float[] cent);
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 12, maxCells: 8);
            var expected = new List<int> { 6, 26 };
            for (int j = 0; j < 3; j++) for (int i = 0; i < 3; i++) { expected.Add(2 * (10 * j + i)); expected.Add(2 * (10 * j + i) + 1); }
            Assert.Equal(expected.OrderBy(x => x), cells[0].OrderBy(x => x));
        }

        internal static int[] TwoScatteredColumns(int n, int seed, out float[] cent)
        {
            int[] g = Grid(1, n, out float[] gc);
            int nv = 2 * (n + 1);
            var rng = new Random(seed);
            var xs = Enumerable.Range(0, 2 * n).Select(i => (float)rng.NextDouble()).ToArray();
            var t = new List<int>(); var c = new List<float>();
            for (int col = 0; col < 2; col++)
                for (int i = 0; i < 2 * n; i++)
                {
                    for (int k = 0; k < 3; k++) t.Add(g[i * 3 + k] + col * nv);
                    c.Add(col * 200 + xs[i]); c.Add(gc[i * 3 + 1]); c.Add(0f);
                }
            cent = c.ToArray();
            return t.ToArray();
        }

        // nx × ny welded quad grid on the XY plane (a copy of QuadEstimateTests.Grid)
        internal static int[] Grid(int nx, int ny, out float[] centroids)
        {
            int V(int i, int j) => j * (nx + 1) + i;
            var t = new List<int>(); var c = new List<float>();
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int a = V(i, j), b = V(i + 1, j), cc = V(i, j + 1), d = V(i + 1, j + 1);
                    t.AddRange(new[] { a, b, cc,  cc, b, d });
                    c.AddRange(new[] { i + 0.33f, j + 0.33f, 0f,  i + 0.66f, j + 0.66f, 0f });
                }
            centroids = c.ToArray();
            return t.ToArray();
        }
    }

    // THE START IS NOT A LOWER BOUND (review round 2): sides re-sorted along their own axes can pair better than the root's
    // order did, so the first k tried can be past one that fits. With room for eight the first round of fixes made six
    // where four fit (and where maxCells = 4 gets four). The halving is run when the balance needed a k past its start,
    // and the fewer cells win: more room never makes more cells.
    public class QuadPartitionFindingTests
    {
        [Fact]
        public void More_room_never_makes_more_cells()
        {
            int[] tris = QuadPartitionReviewTests.TwoScatteredColumns(124, 7, out float[] cent);
            int four = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 63, maxCells: 4).Count;
            int eight = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 63, maxCells: 8).Count;
            Assert.Equal(4, four);
            Assert.True(eight <= four, $"maxCells 8 made {eight} cells, maxCells 4 made {four}");
        }

        // NEVER MORE PIECES THAN THE HALVING, NEVER NULL WHERE IT FITTED (review round 2: on a mesh whose density changes
        // sharply the balance alone needed 7 where the halving made 6). Random meshes of welded strips and lone triangles in
        // scattered order, at budgets from tight to loose: the partition is a partition, every cell fits, and it has at most
        // the halving's count.
        [Fact]
        public void The_partition_never_needs_more_cells_than_the_halving()
        {
            var rng = new Random(11);
            int runs = 0, fewer = 0;
            for (int trial = 0; trial < 120; trial++)
            {
                var t = new List<int>(); var c = new List<float>(); int v = 0;
                int strips = 1 + rng.Next(6);
                for (int st = 0; st < strips; st++)
                {
                    int len = 5 + rng.Next(60); float y = rng.Next(40), x0 = rng.Next(200); bool welded = rng.Next(3) > 0;
                    for (int i = 0; i < len; i++)
                    {
                        int a, b2, cc, d;
                        if (welded) { a = v + 2 * i; b2 = a + 2; cc = a + 1; d = a + 3; }
                        else { a = v; b2 = v + 1; cc = v + 2; d = v + 3; v += 4; }
                        t.AddRange(new[] { a, b2, cc, cc, b2, d });
                        c.AddRange(new[] { x0 + i + 0.33f, y + 0.33f, 0f, x0 + i + 0.66f, y + 0.66f, 0f });
                    }
                    if (welded) v += 2 * len + 2;
                }
                int n = t.Count / 3;
                var order = Enumerable.Range(0, n).ToList();
                if (rng.Next(2) == 0) for (int i = n - 1; i > 0; i--) { int j = rng.Next(i + 1); int q = order[i]; order[i] = order[j]; order[j] = q; }
                var tris = new int[t.Count]; var cent = new float[c.Count];
                for (int i = 0; i < n; i++) { for (int k = 0; k < 3; k++) { tris[i * 3 + k] = t[order[i] * 3 + k]; cent[i * 3 + k] = c[order[i] * 3 + k]; } }
                int whole = QuadEstimate.EstimateQuads(tris, Enumerable.Range(0, n).ToList());
                foreach (int div in new[] { 2, 3, 5, 7 })
                {
                    int budget = System.Math.Max(2, whole / div + rng.Next(3));
                    var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget, maxCells: 8);
                    var halved = QuadEstimate.Halving(tris, cent, Enumerable.Range(0, n).ToList(), budget, 8);
                    runs++;
                    if (halved != null) Assert.NotNull(cells);
                    if (cells == null) continue;
                    Assert.Equal(Enumerable.Range(0, n), cells.SelectMany(x => x).OrderBy(i => i));
                    Assert.All(cells, x => Assert.True(x.Count <= 1 || QuadEstimate.EstimateQuads(tris, x) <= budget));
                    if (halved != null) { Assert.True(cells.Count <= halved.Count); if (cells.Count < halved.Count) fewer++; }
                }
            }
            Assert.Equal(480, runs);
            Assert.True(fewer > 0);   // the fixture can tell: the balance does beat the halving somewhere
        }
    }
}
