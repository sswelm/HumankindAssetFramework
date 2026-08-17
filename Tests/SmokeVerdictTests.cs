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

        // ---- depth pass (2026-08-17): the four per-entry deep checks, each earned by a shipped bug class ----

        static UniversalInject.SmokeFacts Healthy() =>
            new UniversalInject.SmokeFacts { GbMissing = 0, InjectionErrors = 0, Models = 22, Repointed = 5 };

        [Fact]
        public void Fail_OnDeadClipRole_NamesEntryAndRole()
        {
            var f = Healthy();
            f.DeadRoles.Add("Howitzer idleOverride");   // the "forgot to deploy" trap, now a named FAIL
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("1 dead clip role(s)", r.Summary);
            Assert.Contains("Howitzer idleOverride", r.Summary);
        }

        [Fact]
        public void Fail_OnMissingAsset()
        {
            var f = Healthy();
            f.MissingAssets.Add("OrganGun atlas");
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("missing asset", r.Summary);
            Assert.Contains("OrganGun atlas", r.Summary);
        }

        [Fact]
        public void Fail_OnFailedSound()
        {
            var f = Healthy();
            f.FailedSounds.Add("Zeppelin loop 'engine.wav'");
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("sound file(s) failed to load", r.Summary);
            Assert.Contains("engine.wav", r.Summary);
        }

        [Fact]
        public void Fail_OnBudgetAlarm()
        {
            var f = Healthy();
            f.BudgetAlarms.Add("L2 'MeshWithSkeleton' verts 97% / idx 61%");
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("near the wall", r.Summary);
            Assert.Contains("97%", r.Summary);
        }

        [Fact]
        public void DeepChecks_AllReasonsSurfaceAtOnce_WithTheShallowOnes()
        {
            var f = new UniversalInject.SmokeFacts { GbMissing = 1, InjectionErrors = 0, Models = 22, Repointed = 5 };
            f.DeadRoles.Add("A move");
            f.FailedSounds.Add("B loop 'x.wav'");
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("missing", r.Summary);
            Assert.Contains("dead clip role", r.Summary);
            Assert.Contains("x.wav", r.Summary);
        }

        [Fact]
        public void Pass_SaysDeepChecksClean()
        {
            // a PASS must claim only what was checked: the summary names the deep-check families explicitly
            var r = UniversalInject.SmokeVerdict(Healthy());
            Assert.True(r.Pass);
            Assert.Contains("deep checks clean", r.Summary);
        }
    }
}
