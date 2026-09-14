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
