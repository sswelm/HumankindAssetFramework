using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The in-game smoke harness's runtime side can't be unit-tested (needs the game), but its VERDICT is a pure function.
    // These lock the PASS/FAIL rules so the harness's assertion stays trustworthy.
    public class SmokeVerdictTests
    {
        [Fact]
        public void Pass_WhenAllGood()
        {
            var r = UniversalInject.SmokeVerdict(gbMissing: 0, injectionErrors: 0, models: 22, repointed: 5);
            Assert.True(r.Pass);
            Assert.Contains("PASS", r.Summary);
        }

        [Fact]
        public void Fail_OnMissingBinding()
        {
            var r = UniversalInject.SmokeVerdict(gbMissing: 2, injectionErrors: 0, models: 22, repointed: 5);
            Assert.False(r.Pass);
            Assert.Contains("2 game type/member(s) missing", r.Summary);
        }

        [Fact]
        public void Fail_OnInjectionError()
        {
            var r = UniversalInject.SmokeVerdict(gbMissing: 0, injectionErrors: 3, models: 22, repointed: 5);
            Assert.False(r.Pass);
            Assert.Contains("3 injection error(s)", r.Summary);
        }

        [Fact]
        public void Fail_OnNoModelsLoaded()
        {
            var r = UniversalInject.SmokeVerdict(gbMissing: 0, injectionErrors: 0, models: 0, repointed: 0);
            Assert.False(r.Pass);
            Assert.Contains("no models loaded", r.Summary);
        }

        [Fact]
        public void Fail_ReportsAllReasonsAtOnce()
        {
            var r = UniversalInject.SmokeVerdict(gbMissing: 1, injectionErrors: 2, models: 0, repointed: 0);
            Assert.False(r.Pass);
            Assert.Contains("missing", r.Summary);
            Assert.Contains("injection error", r.Summary);
            Assert.Contains("no models loaded", r.Summary);
        }

        [Fact]
        public void RepointedZero_StillPasses_WhenNoUnitsPresent()
        {
            // repointed is informational (depends which units are on the map), so 0 injected is NOT a failure by itself
            var r = UniversalInject.SmokeVerdict(gbMissing: 0, injectionErrors: 0, models: 22, repointed: 0);
            Assert.True(r.Pass);
        }
    }
}
