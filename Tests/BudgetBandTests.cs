using HumankindAssetFramework;
using Xunit;

// The pawn-layer capacity classifier behind the every-injection buffer check (PR #20). Pure math, so the
// band boundaries and the review-driven semantics are locked here: fullness is judged per capacity
// INDEPENDENTLY (mesh-slot exhaustion trips band 3 even at low vertex percentages), and a cursor short of
// the end is never reported as full (a rejected oversize mesh can leave it there).
public class BudgetBandTests
{
    [Fact]
    public void Healthy_buffers_are_band_zero()
    {
        Assert.Equal(0, UniversalInject.BudgetBand(500_000, 1_000_000, 800_000, 2_000_000, 700, 3_500));
        Assert.Equal(0, UniversalInject.BudgetBand(840_000, 1_000_000, 0, 0, 0, 0));   // 84% — just under the warn line
    }

    [Fact]
    public void Warn_at_85_error_at_95_on_any_metric()
    {
        Assert.Equal(1, UniversalInject.BudgetBand(850_000, 1_000_000, 0, 2_000_000, 0, 3_500));   // verts 85%
        Assert.Equal(1, UniversalInject.BudgetBand(0, 1_000_000, 1_700_000, 2_000_000, 0, 3_500)); // idx 85%
        Assert.Equal(1, UniversalInject.BudgetBand(0, 1_000_000, 0, 2_000_000, 3_000, 3_500));     // meshes 85%
        Assert.Equal(2, UniversalInject.BudgetBand(950_000, 1_000_000, 0, 2_000_000, 0, 3_500));   // verts 95%
        Assert.Equal(2, UniversalInject.BudgetBand(0, 1_000_000, 0, 2_000_000, 3_400, 3_500));     // meshes 97%
    }

    [Fact]
    public void Mesh_slot_exhaustion_is_full_even_at_low_vertex_usage()
    {
        // Review find: a layer can run out of mesh slots while verts/idx sit far below 85%.
        Assert.Equal(3, UniversalInject.BudgetBand(100_000, 1_000_000, 100_000, 2_000_000, 3_500, 3_500));
    }

    [Fact]
    public void At_or_past_any_capacity_is_full()
    {
        Assert.Equal(3, UniversalInject.BudgetBand(1_000_000, 1_000_000, 0, 2_000_000, 0, 3_500));
        Assert.Equal(3, UniversalInject.BudgetBand(1_000_001, 1_000_000, 0, 2_000_000, 0, 3_500));
        Assert.Equal(3, UniversalInject.BudgetBand(0, 1_000_000, 2_000_000, 2_000_000, 0, 3_500));
    }

    [Fact]
    public void A_cursor_short_of_the_end_is_never_reported_full()
    {
        // Review find: a rejected oversize mesh can leave the cursor below the limit — that state must
        // classify by percentage, not as full.
        Assert.Equal(2, UniversalInject.BudgetBand(999_999, 1_000_000, 0, 2_000_000, 0, 3_500));
    }

    [Fact]
    public void Unset_capacities_never_divide_or_trip()
    {
        Assert.Equal(0, UniversalInject.BudgetBand(0, 0, 0, 0, 0, 0));
        Assert.Equal(0, UniversalInject.BudgetBand(123, 0, 456, 0, 7, 0));
    }
}
