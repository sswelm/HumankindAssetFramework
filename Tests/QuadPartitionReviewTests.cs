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
        // Two columns of 124 quads, 200 apart along x; each triangle's x centroid is scattered inside its column, so sorted
        // along x the greedy estimate is 286, over 4 x 63 - yet each column re-sorted along its own longest axis (y) pairs
        // every quad and halves into 62 + 62: four cells fit. (Written for K2, the start not capped at maxCells; review
        // round 3 found it no longer tells K2 apart - the start takes the lower, stored-order estimate, 248, and the
        // halving would give the four anyway. Kept as a fixture of a mesh that fits only after its sides are re-sorted.)
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

        // N3 (one cell past maxCells allowed), in the balance AND in the halving. Review round 3: the first version's null
        // came from the early refusal (200 triangles > 2 x 36 x 2), not from either cap. A strip of 100 quads stored with
        // its first triangle last estimates 101 in its stored order and 100 sorted: at a budget of 100 it does not fit as
        // it stands, two cells of 50 do, and with room for one cell the answer is null - the early refusal does not fire
        // (200 is not over 2 x 100 x 1), so the caps decide.
        [Fact]
        public void One_cell_past_the_cap_is_still_null()
        {
            int[] grid = Grid(100, 1, out float[] gc);
            var tris = new int[grid.Length]; var cent = new float[gc.Length];
            for (int i = 0; i < 200; i++) { int from = (i + 1) % 200; System.Array.Copy(grid, from * 3, tris, i * 3, 3); System.Array.Copy(gc, from * 3, cent, i * 3, 3); }
            Assert.Equal(101, QuadEstimate.EstimateQuads(tris, Enumerable.Range(0, 200).ToList()));   // the fixture: over 100 as stored
            Assert.Equal(2, QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 100, maxCells: 2).Count);
            Assert.Null(QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 100, maxCells: 1));
            Assert.Null(QuadEstimate.Halving(tris, cent, Enumerable.Range(0, 200).ToList(), 100, 1));
            Assert.Null(QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 100, maxCells: 0));
            Assert.Null(QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 200, maxCells: 0));   // no cell allowed, even for a mesh that fits whole
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

        // THE HALVING WINS WHERE IT IS BETTER (review round 2, a reviewer's "chains" mesh, scaled down): 50 strips of four
        // triangles a-b-c-d along x, the two middle ones low and the two ends high in y. Sorted along x a strip pairs a-b
        // and c-d (2 quads); sorted along y the middles come first and pair b-c, the ends stay alone (3 quads). At a budget
        // of 25 the balance alone needs 7 pieces and the halving makes 6: the partition returns the halving's 6.
        [Fact]
        public void Where_the_halving_makes_fewer_pieces_its_pieces_are_returned()
        {
            int C = 50, N = 4 * C; var tris = new int[3 * N]; var cent = new float[3 * N];
            for (int c = 0; c < C; c++)
                for (int i = 0; i < 4; i++)
                {
                    int t = c * 4 + i, v = c * 6 + i;
                    tris[t * 3] = v; tris[t * 3 + 1] = v + 1; tris[t * 3 + 2] = v + 2;
                    cent[t * 3] = c * 10f + i; cent[t * 3 + 1] = (i == 0 || i == 3) ? 0.5f * 10f * C : 0f;
                }
            var halved = QuadEstimate.Halving(tris, cent, Enumerable.Range(0, N).ToList(), 25, 8);
            Assert.Equal(6, halved.Count);
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 25, maxCells: 8);
            Assert.Equal(6, cells.Count);
            Assert.All(cells, c => Assert.True(QuadEstimate.EstimateQuads(tris, c) <= 25));
        }

        // THE BALANCE FITS AT ITS START AND THE HALVING FITS IN FEWER (review round 3, a reviewer's random search): a welded
        // 6 x 6 grid stored in shuffled order with jittered centroids, at a budget of 9. Stored 42, sorted 38: the balance
        // starts at 5 and fits there, the halving makes 4. The first fallback ran the halving only after a failed start
        // and returned 5; with room for 4 cells it returned 4 - more room made more cells.
        [Fact]
        public void A_balance_that_fits_at_its_start_still_loses_to_fewer_halving_pieces()
        {
            int[] tris = { 45, 39, 46, 39, 33, 40, 37, 31, 38, 11, 12, 18, 1, 2, 8, 22, 16, 23, 3, 4, 10, 25, 19, 26, 39, 40, 46, 12, 6, 13, 26, 27, 33, 33, 27, 34, 10, 4, 11, 18, 19, 25, 31, 32, 38, 21, 15, 22, 14, 8, 15, 17, 11, 18, 26, 20, 27, 0, 1, 7, 10, 11, 17, 19, 13, 20, 28, 22, 29, 9, 10, 16, 32, 33, 39, 8, 9, 15, 32, 26, 33, 11, 5, 12, 29, 23, 30, 46, 40, 47, 35, 36, 42, 29, 30, 36, 40, 41, 47, 16, 10, 17, 47, 41, 48, 24, 25, 31, 31, 25, 32, 8, 2, 9, 30, 31, 37, 4, 5, 11, 25, 26, 32, 43, 37, 44, 37, 38, 44, 24, 18, 25, 28, 29, 35, 30, 24, 31, 15, 9, 16, 22, 23, 29, 14, 15, 21, 38, 39, 45, 17, 18, 24, 40, 34, 41, 15, 16, 22, 36, 30, 37, 44, 38, 45, 18, 12, 19, 38, 32, 39, 23, 24, 30, 12, 13, 19, 2, 3, 9, 35, 29, 36, 16, 17, 23, 19, 20, 26, 33, 34, 40, 7, 1, 8, 9, 3, 10, 7, 8, 14, 42, 36, 43, 21, 22, 28, 36, 37, 43, 5, 6, 12, 23, 17, 24 };
            float[] cent = { 7.806282043457031f, 272.6600036621094f, 0.0f, 9.203704833984375f, 271.6600036621094f, 0.0f, 7.750278472900391f, 271.6600036621094f, 0.0f, 8.518179893493652f, 268.3299865722656f, 0.0f, 6.228750228881836f, 267.3299865722656f, 0.0f, 5.698413848876953f, 269.6600036621094f, 0.0f, 8.287252426147461f, 267.3299865722656f, 0.0f, 9.647658348083496f, 269.6600036621094f, 0.0f, 8.45277214050293f, 272.3299865722656f, 0.0f, 9.751138687133789f, 267.6600036621094f, 0.0f, 10.250988960266113f, 270.3299865722656f, 0.0f, 9.833520889282227f, 270.6600036621094f, 0.0f, 8.56151294708252f, 267.6600036621094f, 0.0f, 9.09514045715332f, 269.3299865722656f, 0.0f, 8.33945369720459f, 271.3299865722656f, 0.0f, 5.61317777633667f, 269.6600036621094f, 0.0f, 5.604391098022461f, 268.6600036621094f, 0.0f, 7.920406341552734f, 268.6600036621094f, 0.0f, 10.225042343139648f, 269.6600036621094f, 0.0f, 4.675367832183838f, 267.3299865722656f, 0.0f, 8.32166576385498f, 268.3299865722656f, 0.0f, 10.015045166015625f, 268.6600036621094f, 0.0f, 5.255587100982666f, 270.6600036621094f, 0.0f, 6.450579643249512f, 268.3299865722656f, 0.0f, 8.793296813964844f, 271.3299865722656f, 0.0f, 5.846185207366943f, 268.3299865722656f, 0.0f, 9.678712844848633f, 270.6600036621094f, 0.0f, 9.233859062194824f, 267.6600036621094f, 0.0f, 6.208495140075684f, 270.6600036621094f, 0.0f, 9.7084379196167f, 272.6600036621094f, 0.0f, 5.317890644073486f, 272.3299865722656f, 0.0f, 6.186430931091309f, 271.3299865722656f, 0.0f, 10.41987133026123f, 272.3299865722656f, 0.0f, 7.391970634460449f, 268.6600036621094f, 0.0f, 10.282288551330566f, 272.6600036621094f, 0.0f, 7.79788875579834f, 270.3299865722656f, 0.0f, 8.719381332397461f, 270.6600036621094f, 0.0f, 5.716484069824219f, 267.6600036621094f, 0.0f, 6.8837504386901855f, 271.3299865722656f, 0.0f, 8.576620101928711f, 267.3299865722656f, 0.0f, 8.482831001281738f, 270.3299865722656f, 0.0f, 6.351219654083252f, 272.6600036621094f, 0.0f, 7.037464141845703f, 272.3299865722656f, 0.0f, 8.634153366088867f, 269.6600036621094f, 0.0f, 4.863198757171631f, 271.3299865722656f, 0.0f, 6.968275547027588f, 270.6600036621094f, 0.0f, 6.138350486755371f, 268.6600036621094f, 0.0f, 5.912505149841309f, 270.3299865722656f, 0.0f, 4.341063022613525f, 269.3299865722656f, 0.0f, 7.753293514251709f, 272.3299865722656f, 0.0f, 8.29369068145752f, 269.3299865722656f, 0.0f, 10.14802074432373f, 271.6600036621094f, 0.0f, 5.4007344245910645f, 269.3299865722656f, 0.0f, 6.544422626495361f, 271.6600036621094f, 0.0f, 6.916506290435791f, 272.6600036621094f, 0.0f, 9.701112747192383f, 268.6600036621094f, 0.0f, 8.06155014038086f, 271.6600036621094f, 0.0f, 6.435831546783447f, 270.3299865722656f, 0.0f, 10.279679298400879f, 268.3299865722656f, 0.0f, 6.673902988433838f, 267.3299865722656f, 0.0f, 5.296314716339111f, 271.6600036621094f, 0.0f, 6.3691582679748535f, 269.3299865722656f, 0.0f, 10.151618957519531f, 269.3299865722656f, 0.0f, 9.375421524047852f, 271.3299865722656f, 0.0f, 4.975680351257324f, 267.6600036621094f, 0.0f, 7.718625068664551f, 267.6600036621094f, 0.0f, 4.366644859313965f, 268.3299865722656f, 0.0f, 5.578838348388672f, 272.6600036621094f, 0.0f, 4.815774917602539f, 270.3299865722656f, 0.0f, 6.192354679107666f, 272.3299865722656f, 0.0f, 9.463364601135254f, 267.3299865722656f, 0.0f, 7.312695503234863f, 269.6600036621094f, 0.0f };
            Assert.Equal(72, tris.Length / 3);
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 9, maxCells: 8);
            Assert.Equal(4, cells.Count);
            Assert.Equal(4, QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 9, maxCells: 4).Count);
            Assert.All(cells, c => Assert.True(QuadEstimate.EstimateQuads(tris, c) <= 9));
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
