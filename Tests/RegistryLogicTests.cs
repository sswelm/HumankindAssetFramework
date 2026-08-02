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
    }
}
