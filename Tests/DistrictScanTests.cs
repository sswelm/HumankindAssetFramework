using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // THE DISTRICT SCAN — the 2026-08-23 measurement and the fix it justified.
    //
    // `SelectorTile` was 219 µs/frame, 36% of HAF's whole per-frame cost, and twice written off as "diffuse".
    // Splitting the loop into scan-vs-work gave the number that ended the guessing:
    //
    //     districts 2668 skipped 237.3 µs (89 ns ea), 1 ours 5.6 µs (5592 ns ea)
    //
    // 2,668 districts walked every frame to find ONE. Two causes, both in the tracking list: nothing ever removed a
    // destroyed district, and the dedup on Add was a LINEAR scan (O(n) per add, O(n²) over a session).
    //
    // These cover the two pure pieces. The per-frame loop itself needs the live game, so it is drilled in-game via
    // the FrameCost line, not here.
    // Serialised against BindStallTests: both reset District session state, which xUnit would otherwise let them do
    // concurrently.
    [Collection("haf-district-session-state")]
    public class DistrictScanTests
    {
        public DistrictScanTests()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
            SessionState.Reset(SessionScope.District);   // trackedDistricts/trackedSet/matchedDistricts are [SessionScoped(District)]
        }

        // A plain object stands in for a live PresentationDistrict: not a UnityEngine.Object, so it is never
        // fake-null, which is exactly the "still alive" case.
        static object Live(string name) => new LiveDistrict(name);
        sealed class LiveDistrict { public readonly string ConstructibleDefinitionName; public LiveDistrict(string n) { ConstructibleDefinitionName = n; } }

        static Dictionary<string, object> Wanted(params string[] names)
        {
            var d = new Dictionary<string, object>();
            foreach (var n in names) d[n] = new object();
            return d;
        }

        // ---- the filter: the per-frame loop must walk the matches, not the world ----

        [Fact]
        public void OnlyMatchingDistrictsAreWalked()
        {
            for (int i = 0; i < 500; i++) DistrictInject.TrackForTests(Live("Extension_Irrelevant_" + i));
            var ours = Live("Extension_Base_BreederReactor");
            DistrictInject.TrackForTests(ours);

            DistrictInject.EnsureMatchedDistricts(Wanted("Extension_Base_BreederReactor"));

            Assert.Single(DistrictInject.matchedDistricts);
            Assert.Same(ours, DistrictInject.matchedDistricts[0]);
        }

        [Fact]
        public void NoMatches_MeansAnEmptyWalk()
        {
            for (int i = 0; i < 50; i++) DistrictInject.TrackForTests(Live("Extension_Irrelevant_" + i));
            DistrictInject.EnsureMatchedDistricts(Wanted("Extension_NotPresent"));
            Assert.Empty(DistrictInject.matchedDistricts);
        }

        [Fact]
        public void EveryMatchIsKept_NotJustTheFirst()
        {
            var a = Live("Extension_A"); var b = Live("Extension_B");
            DistrictInject.TrackForTests(a); DistrictInject.TrackForTests(Live("Extension_Other")); DistrictInject.TrackForTests(b);
            DistrictInject.EnsureMatchedDistricts(Wanted("Extension_A", "Extension_B"));
            Assert.Equal(2, DistrictInject.matchedDistricts.Count);
            Assert.Contains(a, DistrictInject.matchedDistricts);
            Assert.Contains(b, DistrictInject.matchedDistricts);
        }

        // A new district appearing must be picked up — the filter is a cache, and a cache that never refreshes is
        // a district that silently never renders. Starts NON-empty so that a "refresh only while empty" bug cannot
        // pass this: the first drill of it did exactly that, and the mutation survived.
        [Fact]
        public void ADistrictAddedLater_IsPickedUp()
        {
            DistrictInject.TrackForTests(Live("Extension_Early"));
            DistrictInject.EnsureMatchedDistricts(Wanted("Extension_Early", "Extension_Late"));
            Assert.Single(DistrictInject.matchedDistricts);

            DistrictInject.TrackForTests(Live("Extension_Late"));
            DistrictInject.EnsureMatchedDistricts(Wanted("Extension_Early", "Extension_Late"));
            Assert.Equal(2, DistrictInject.matchedDistricts.Count);
        }

        // ...and so must a district becoming wanted, which is what happens when the registry parses after the
        // districts are already on the map.
        [Fact]
        public void ADistrictBecomingWantedLater_IsPickedUp()
        {
            DistrictInject.TrackForTests(Live("Extension_Reactor"));
            DistrictInject.EnsureMatchedDistricts(Wanted());
            Assert.Empty(DistrictInject.matchedDistricts);

            DistrictInject.EnsureMatchedDistricts(Wanted("Extension_Reactor"));
            Assert.Single(DistrictInject.matchedDistricts);
        }

        // ---- the dedup: O(1), and still actually deduping ----

        [Fact]
        public void TheSameDistrictIsTrackedOnce()
        {
            var d = Live("Extension_A");
            DistrictInject.TrackForTests(d); DistrictInject.TrackForTests(d); DistrictInject.TrackForTests(d);
            Assert.Equal(1, DistrictInject.TrackedCountForTests);
        }

        // REFERENCE identity, not Equals — and this needs a type that actually overrides Equals to prove anything.
        // A plain object's Equals IS reference equality, so a default HashSet would pass such a test while being
        // wrong for the real case: UnityEngine.Object overrides Equals to compare NATIVE pointers, so two DIFFERENT
        // destroyed districts compare equal and a value-based set would collapse them into one.
        sealed class AlwaysEqual { public override bool Equals(object o) => o is AlwaysEqual; public override int GetHashCode() => 1; }

        [Fact]
        public void TwoDistinctDistrictsThatCompareEqualAreBothTracked()
        {
            DistrictInject.TrackForTests(new AlwaysEqual());
            DistrictInject.TrackForTests(new AlwaysEqual());
            Assert.Equal(2, DistrictInject.TrackedCountForTests);
        }

        // ---- the prune: destroyed districts must leave, or the list only ever grows ----

        [Fact]
        public void NullEntriesArePruned()
        {
            DistrictInject.TrackForTests(Live("Extension_A"));
            DistrictInject.TrackForTests(null);
            DistrictInject.TrackForTests(Live("Extension_B"));
            Assert.Equal(1, DistrictInject.PruneDeadDistricts());
            Assert.Equal(2, DistrictInject.TrackedCountForTests);
        }

        // A pruned district must leave the SET too, or it can never be re-tracked if the engine hands it back.
        [Fact]
        public void APrunedDistrictCanBeTrackedAgain()
        {
            DistrictInject.TrackForTests(null);
            DistrictInject.PruneDeadDistricts();
            DistrictInject.TrackForTests(null);
            Assert.Equal(1, DistrictInject.TrackedCountForTests);
        }
    }
}
