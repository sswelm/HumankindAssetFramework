using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The multi-mesh split's two pure routines (editor/QuadEstimate.cs). The engine draws 255×64 quads per fragment
    // and the SDK pairs only EDGE-SHARING triangles into quads — tris/2 shipped as the budget once (review P1,
    // 2026-09-13) and could have sent a Faceted chunk 2x over the ceiling. Until 2026-09-14 the only assertions were
    // in the opt-in Unity lane, which neither CI nor the pre-push hook runs.
    public class QuadEstimateTests
    {
        static List<int> All(int[] tris) => Enumerable.Range(0, tris.Length / 3).ToList();

        [Fact]
        public void Two_triangles_sharing_an_edge_are_one_quad()
        {
            int[] tris = { 0, 1, 2,  2, 1, 3 };   // share edge (1,2)
            Assert.Equal(1, QuadEstimate.EstimateQuads(tris, All(tris)));
        }

        [Fact]
        public void Two_disjoint_triangles_are_two_quads()
        {
            int[] tris = { 0, 1, 2,  3, 4, 5 };
            Assert.Equal(2, QuadEstimate.EstimateQuads(tris, All(tris)));
        }

        [Fact]
        public void A_three_fan_pairs_two_and_leaves_one()
        {
            int[] tris = { 0, 1, 2,  0, 2, 3,  0, 3, 4 };   // t0–t1 share (0,2); t2 shares (0,3) with t1, already paired
            Assert.Equal(2, QuadEstimate.EstimateQuads(tris, All(tris)));
        }

        [Fact]
        public void Faceted_geometry_costs_a_quad_per_triangle_the_tris_over_two_trap()
        {
            int n = 10;
            int[] tris = Enumerable.Range(0, n * 3).ToArray();   // no shared indices at all
            Assert.Equal(n, QuadEstimate.EstimateQuads(tris, All(tris)));
            Assert.NotEqual(n / 2, QuadEstimate.EstimateQuads(tris, All(tris)));
        }

        [Fact]
        public void A_welded_quad_grid_pairs_every_cell()
        {
            int[] tris = Grid(4, 4, out _);   // 16 cells, 32 tris, adjacent pair per cell
            Assert.Equal(16, QuadEstimate.EstimateQuads(tris, All(tris)));
        }

        [Fact]
        public void Counts_only_the_cell_it_is_given()
        {
            int[] tris = Grid(4, 4, out _);
            Assert.Equal(1, QuadEstimate.EstimateQuads(tris, new List<int> { 0, 1 }));   // one cell's pair
            Assert.Equal(2, QuadEstimate.EstimateQuads(tris, new List<int> { 0, 2 }));   // two lone triangles from different cells
            Assert.Equal(0, QuadEstimate.EstimateQuads(tris, new List<int>()));
        }

        [Fact]
        public void Partition_covers_every_triangle_once_with_every_cell_under_budget_in_a_stable_order()
        {
            int[] tris = Grid(20, 5, out float[] cent);   // 100 quads / 200 tris, 20 along x
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 30, maxCells: 8);

            Assert.NotNull(cells);
            Assert.Equal(4, cells.Count);                                   // 100 -> 50 -> 25 (<= 30): two BSP levels
            Assert.All(cells, c => Assert.True(QuadEstimate.EstimateQuads(tris, c) <= 30));
            var union = cells.SelectMany(c => c).OrderBy(i => i).ToList();
            Assert.Equal(Enumerable.Range(0, 200), union);                  // a partition: every triangle exactly once
            var minX = cells.Select(c => c.Min(ti => cent[ti * 3])).ToList();
            Assert.Equal(minX.OrderBy(x => x), minX);                       // ordered by minimum centroid -> stable chunk letters

            var again = QuadEstimate.PartitionByQuadBudget(tris, cent, 30, 8);
            Assert.Equal(cells.Select(c => string.Join(",", c)), again.Select(c => string.Join(",", c)));   // deterministic
        }

        [Fact]
        public void Partition_returns_a_single_cell_when_it_fits_and_null_past_the_chunk_cap()
        {
            int[] tris = Grid(20, 5, out float[] cent);
            var one = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 100, maxCells: 8);
            Assert.Single(one);
            Assert.Equal(200, one[0].Count);
            Assert.Null(QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 30, maxCells: 2));   // needs 4
        }

        // THE CELLS FILL EVENLY (2026-11-25, the Heavy Monitor: eight cells of 25,207 triangles each, 13,989 to 16,000 quads -
        // the cell that pairs worst capped the bake at 7.3 of 8 fragments). A mesh whose left half is a welded grid (a quad
        // per two triangles) and whose right half is faceted (a quad per triangle): cut at the median TRIANGLE, the two
        // sides hold 100 and 200 quads; cut where the quads balance, 150 each.
        [Fact]
        public void A_cut_balances_the_quads_not_the_triangles()
        {
            int[] tris = HalfWeldedHalfFaceted(out float[] cent);                    // 200 + 200 triangles, 100 + 200 quads
            Assert.Equal(300, QuadEstimate.EstimateQuads(tris, Enumerable.Range(0, 400).ToList()));
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 160, maxCells: 8);
            Assert.Equal(2, cells.Count);                                            // 300 quads fit in two cells of 160...
            var quads = cells.Select(c => QuadEstimate.EstimateQuads(tris, c)).ToList();
            Assert.All(quads, q => Assert.InRange(q, 148, 152));                     // ...because each holds half the QUADS
            Assert.Equal(new[] { 250, 150 }, cells.Select(c => c.Count).ToArray());   // the grid's 200 triangles and 50 faceted ones | 150 faceted ones
            Assert.Equal(Enumerable.Range(0, 400), cells.SelectMany(c => c).OrderBy(i => i));
        }

        // THE FEWEST CELLS THAT FIT: halving made 1, 2, 4 or 8 cells, so a mesh of five budgets was cut into eight
        [Theory]
        [InlineData(36, 3)][InlineData(27, 4)][InlineData(22, 5)][InlineData(19, 6)]
        public void A_mesh_is_cut_into_as_few_cells_as_fit(int budget, int expected)
        {
            int[] tris = Grid(100, 1, out float[] cent);                             // 100 quads in a row: a cut separates one pair at most
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget, maxCells: 8);
            Assert.Equal(expected, cells.Count);
            Assert.All(cells, c => Assert.True(QuadEstimate.EstimateQuads(tris, c) <= budget));
            Assert.Equal(Enumerable.Range(0, 200), cells.SelectMany(c => c).OrderBy(i => i));
            var minX = cells.Select(c => c.Min(ti => cent[ti * 3])).ToList();
            Assert.Equal(minX.OrderBy(x => x), minX);
        }

        // a cut separates pairs, so the cells together hold more quads than the whole: when that puts a cell over the
        // budget the next count is tried - and past the cap the answer is still null
        [Fact]
        public void A_cell_over_the_budget_after_the_cut_takes_one_cell_more_and_the_cap_still_holds()
        {
            int[] tris = Grid(20, 5, out float[] cent);                              // 100 quads: two cells of 50 would do...
            int whole = QuadEstimate.EstimateQuads(tris, Enumerable.Range(0, 200).ToList());
            Assert.Equal(100, whole);
            var two = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 50, maxCells: 8);
            int sum = two.Sum(c => QuadEstimate.EstimateQuads(tris, c));
            Assert.True(sum >= whole);
            Assert.All(two, c => Assert.True(QuadEstimate.EstimateQuads(tris, c) <= 50));
            Assert.Equal(sum > whole ? 3 : 2, two.Count);                            // ...unless the cut cost a quad
            Assert.Null(QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 12, maxCells: 8));   // 100 quads need nine cells of 12
        }

        // THE STARTING k IS JUDGED IN THE ORDER THE CUTS SEE (review of PR #322): a strip of 124 quads whose triangles are
        // stored in scattered order. In that order the greedy estimate is 140, over 2 x 63, so the first version started at
        // k = 3 and made three cells of 42; sorted along the strip it pairs every quad, and two cells of 62 do.
        [Fact]
        public void The_first_k_is_estimated_in_centroid_order_not_in_the_stored_order()
        {
            int[] grid = Grid(124, 1, out float[] gc);
            var order = Enumerable.Range(0, 248).ToList();
            var rng = new Random(7);
            for (int i = order.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); int t = order[i]; order[i] = order[j]; order[j] = t; }
            var tris = new int[grid.Length]; var cent = new float[gc.Length];
            for (int i = 0; i < 248; i++) { Array.Copy(grid, order[i] * 3, tris, i * 3, 3); Array.Copy(gc, order[i] * 3, cent, i * 3, 3); }
            Assert.Equal(140, QuadEstimate.EstimateQuads(tris, Enumerable.Range(0, 248).ToList()));   // the fixture: scattered (124 in order)
            var cells = QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 63, maxCells: 8);
            Assert.Equal(2, cells.Count);
            Assert.All(cells, c => Assert.True(QuadEstimate.EstimateQuads(tris, c) <= 63));
            // ...and when the stored order's estimate is past the cap, the cap is still tried: two cells, not null
            Assert.Equal(2, QuadEstimate.PartitionByQuadBudget(tris, cent, budget: 63, maxCells: 2).Count);
        }

        // a budget of nothing: null, as before the PR (the first version divided by it)
        [Theory]
        [InlineData(0)][InlineData(-5)]
        public void A_budget_of_nothing_fits_nothing(int budget)
        {
            int[] tris = Grid(10, 4, out float[] cent);
            Assert.Null(QuadEstimate.PartitionByQuadBudget(tris, cent, budget, maxCells: 8));
        }

        // the left side of every cut comes from one pass: its count at every position equals a fresh estimate of that prefix
        [Fact]
        public void The_prefix_counts_equal_a_fresh_estimate_of_every_prefix()
        {
            int[] tris = HalfWeldedHalfFaceted(out float[] cent);
            var order = Enumerable.Range(0, tris.Length / 3).ToList();
            var rng = new Random(3);
            for (int i = order.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); int t = order[i]; order[i] = order[j]; order[j] = t; }
            foreach (var cell in new[] { Enumerable.Range(0, tris.Length / 3).ToList(), order })
            {
                int[] prefix = QuadEstimate.PrefixQuads(tris, cell);
                Assert.Equal(cell.Count + 1, prefix.Length);
                for (int m = 0; m <= cell.Count; m++) Assert.Equal(QuadEstimate.EstimateQuads(tris, cell.GetRange(0, m)), prefix[m]);
            }
        }

        // x 0..10: a welded 10 x 10 grid (200 triangles, 100 quads); x 10..20: the same grid with no shared vertex (200
        // triangles, 200 quads)
        static int[] HalfWeldedHalfFaceted(out float[] centroids)
        {
            var t = new List<int>(Grid(10, 10, out float[] c0)); var c = new List<float>(c0);
            int next = 11 * 11;
            for (int j = 0; j < 10; j++)
                for (int i = 0; i < 10; i++)
                {
                    t.AddRange(new[] { next, next + 1, next + 2,  next + 3, next + 4, next + 5 }); next += 6;
                    c.AddRange(new[] { 10 + i + 0.33f, j + 0.33f, 0f,  10 + i + 0.66f, j + 0.66f, 0f });
                }
            centroids = c.ToArray();
            return t.ToArray();
        }

        // nx × ny welded quad grid on the XY plane: cell (i,j) -> tris (a,b,c),(c,b,d) sharing edge (b,c)
        static int[] Grid(int nx, int ny, out float[] centroids)
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
}
