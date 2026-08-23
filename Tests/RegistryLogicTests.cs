using System.Collections.Generic;
using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // Spike: prove the pure registry/parse/match logic is testable outside the running game.
    // These functions touch NO Amplitude/Unity runtime — just strings, JSON, and ModelEntry data.
    public class RegistryLogicTests
    {
        public RegistryLogicTests()
        {
            // The registry logic logs via Plugin.Log (null outside the game). A listener-less ManualLogSource
            // makes every LogXxx a safe no-op, so parse/match code can run in the test host.
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
        }

        // ---- CoreDesc: strip a trailing "_NN" variant suffix so a pawn description matches a unit-definition name ----
        [Theory]
        [InlineData("Era6_Common_StealthHelicopters_01", "Era6_Common_StealthHelicopters")]
        [InlineData("Era6_Common_StealthHelicopters", "Era6_Common_StealthHelicopters")]  // no suffix -> unchanged
        [InlineData("Foo_12_Bar_03", "Foo_12_Bar")]                                        // only the LAST group stripped
        [InlineData("", "")]
        public void CoreDesc_StripsTrailingVariantSuffix(string input, string expected)
        {
            Assert.Equal(expected, UniversalInject.CoreDesc(input));
        }

        // ---- LongestMatch: a unit gets the MOST-SPECIFIC entry whose key is a substring of the unit name ----
        [Fact]
        public void LongestMatch_PrefersLongestSubstringKey()
        {
            var general = new ModelEntry { resourceName = "gen", pawnDescription = "ManOWar" };
            var elite   = new ModelEntry { resourceName = "eli", pawnDescription = "ManOWar_Elite" };
            var list = new List<ModelEntry> { general, elite };  // general listed FIRST (old FirstOrDefault would pick it)

            var hit = UniversalInject.LongestMatch(list, "LandUnit_Era4_ManOWar_Elite", x => x.pawnDescription);
            Assert.Same(elite, hit);   // longest key wins, not first-in-order
        }

        [Fact]
        public void LongestMatch_FallsBackToTheOnlyMatch()
        {
            var general = new ModelEntry { resourceName = "gen", pawnDescription = "ManOWar" };
            var elite   = new ModelEntry { resourceName = "eli", pawnDescription = "ManOWar_Elite" };
            var list = new List<ModelEntry> { general, elite };

            var hit = UniversalInject.LongestMatch(list, "LandUnit_Era4_ManOWar", x => x.pawnDescription);
            Assert.Same(general, hit);   // "ManOWar_Elite" is NOT a substring of this name -> only "ManOWar" matches
        }

        // THE GAP THE PAWN-NAME FALLBACK EXISTS FOR (characterised 2026-08-23).
        // A pack can target a pawn SLOT on a unit named for something else — ENC's drones ride a pawn slot whose
        // donor description asset is `Unit_Era6_Australia_AllTerrainAPCs_01`, and the hovercraft's is
        // `Unit_Era6_Common_LandingCrafts_01` while its pawnDescription is `Era6_Common_Hovercrafts_01`. When the
        // unit-definition name does not contain the pawnDescription, BOTH keys miss: the full description AND the
        // coreDesc fallback. This pins that miss as real rather than theoretical, because everything routed through
        // ResolveUnitEntry (animStateDriven, fireOnAttack, deployOnStop, gun elevation) then silently does nothing
        // for that unit — no exception, no log line. ResolveUnitEntry now falls back to the PAWN's own name, which
        // is the criterion the sub-pawn walk already used, so the two resolvers cannot disagree.
        [Fact]
        public void LongestMatch_MissesWhenTheUnitNameDoesNotCarryThePawnDescription()
        {
            var drone = new ModelEntry { resourceName = "DroneSquadFPV", pawnDescription = "Era6_Common_DroneSquadFPV_01", coreDesc = "Era6_Common_DroneSquadFPV" };
            var list = new List<ModelEntry> { drone };

            // the unit it actually rides is named for the APC, not the drone
            const string unitDef = "LandUnit_Era6_Australia_AllTerrainAPCs";
            Assert.Null(UniversalInject.LongestMatch(list, unitDef, x => x.pawnDescription));
            Assert.Null(UniversalInject.LongestMatch(list, unitDef, x => x.coreDesc.Length > 4 ? x.coreDesc : ""));

            // ...while the PAWN's own GameObject name carries it — the fallback's criterion
            Assert.Same(drone, UniversalInject.LongestMatch(list, "Era6_Common_DroneSquadFPV_01", x => x.pawnDescription));
        }

        // The counter-case, so the fallback is not assumed to be needed everywhere: DugoutCanoe's unit definition
        // DOES carry its coreDesc, so the primary matcher resolves it and the fallback never runs.
        [Fact]
        public void LongestMatch_StillResolvesWhenTheUnitNameCarriesTheCoreDesc()
        {
            var canoe = new ModelEntry { resourceName = "DugoutCanoe", pawnDescription = "Era1_Common_DugoutCanoe_01", coreDesc = "Era1_Common_DugoutCanoe" };
            var list = new List<ModelEntry> { canoe };

            Assert.Same(canoe, UniversalInject.LongestMatch(list, "NavalTransport_Era1_Common_DugoutCanoe_Default", x => x.coreDesc.Length > 4 ? x.coreDesc : ""));
        }

        [Fact]
        public void LongestMatch_ReturnsNullOnNoMatch()
        {
            var list = new List<ModelEntry> { new ModelEntry { pawnDescription = "ManOWar" } };
            Assert.Null(UniversalInject.LongestMatch(list, "LandUnit_Catapult", x => x.pawnDescription));
        }

        // ---- ParseModels: JSON -> ModelEntry field mapping + defaults ----
        [Fact]
        public void ParseModels_MapsFieldsAndGuidArrays()
        {
            var json = @"{ ""models"": [
                { ""resourceName"": ""Zeppelin"", ""pawnDescription"": ""Era3_Zeppelin"", ""skel"": [1,2,3,4], ""scale"": 2.5 }
            ] }";
            var e = Assert.Single(UniversalInject.ParseModels(json));
            Assert.Equal("Zeppelin", e.resourceName);
            Assert.Equal("Era3_Zeppelin", e.pawnDescription);
            Assert.Equal(1, e.sa); Assert.Equal(2, e.sb); Assert.Equal(3, e.sc); Assert.Equal(4, e.sd);
            Assert.Equal(2.5f, e.scale);
        }

        [Fact]
        public void ParseModels_AppliesDefaultsForOmittedFields()
        {
            var json = @"{ ""models"": [ { ""resourceName"": ""Bare"" } ] }";
            var e = Assert.Single(UniversalInject.ParseModels(json));
            Assert.Equal(0.5f, e.animPhaseSpread);  // the 2026-07-31 default that governs registries written before the field existed
            Assert.Equal(1f, e.scale);
            Assert.Equal(1f, e.brightness);
            Assert.Equal(0f, e.desaturate);
        }

        [Fact]
        public void ParseModels_EmptyModelsArrayYieldsNoEntries()
        {
            Assert.Empty(UniversalInject.ParseModels(@"{ ""models"": [] }"));
        }

        // ---- GuidToLong: the per-attack/per-pawn dedup key derivation. Null or non-numeric -> 0 (no throw). ----
        [Fact]
        public void GuidToLong_NullReturnsZero() => Assert.Equal(0L, UniversalInject.GuidToLong(null));

        [Fact]
        public void GuidToLong_NonNumericReturnsZero() => Assert.Equal(0L, UniversalInject.GuidToLong("not-a-number"));

        [Fact]
        public void GuidToLong_NumericStringParses() => Assert.Equal(123456789L, UniversalInject.GuidToLong("123456789"));
    }
}
