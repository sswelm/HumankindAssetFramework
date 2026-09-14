using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The district mesh-density auto-boost decision (Patches/DistrictRules.cs). Its runtime caller reads the
    // triangle count off a live FxMesh, so the auto-size branch never ran in a test — and the one shipped bug
    // (auto-sizing overriding the documented 0/1 opt-out, review P2 2026-09-12) had to be found by reading.
    public class DistrictRulesTests
    {
        [Theory]
        [InlineData(3, 10000, 8, 14)]     // 10,000 tris / (255·3 = 765) = 13.07 -> 14, above the configured 8
        [InlineData(3, 6120, 8, 8)]       // exactly 8 × 765: the config already suffices
        [InlineData(3, 6121, 8, 9)]       // one triangle over: needs 9
        [InlineData(3, 10000, 20, 20)]    // config higher than needed: keep the config
        public void Raises_the_boost_to_what_the_mesh_needs(int ppc, long tris, int config, int expected)
            => Assert.Equal(expected, DistrictRules.NeededBoost(ppc, tris, config));

        [Theory]
        [InlineData(1)]
        [InlineData(0)]
        public void The_documented_opt_out_wins_over_any_heuristic(int config)
            => Assert.Equal(config, DistrictRules.NeededBoost(3, 10000, config));   // the review P2: the user's disable must win

        [Theory]
        [InlineData(3, 0, 8)]     // no triangle count read (FxMeshTriangles returned 0): no evidence, keep the config
        [InlineData(0, 10000, 8)] // no PPC: same
        [InlineData(-1, -5, 8)]
        public void No_evidence_keeps_the_configured_boost(int ppc, long tris, int config)
            => Assert.Equal(config, DistrictRules.NeededBoost(ppc, tris, config));
    }
}
