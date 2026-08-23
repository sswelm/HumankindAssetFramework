using System.Linq;
using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // THE LAST-RESORT PARSER — the 2026-08-23 tier-2 finding.
    //
    // ParseModels' regex fallback runs when the document won't JSON-parse, which is precisely when a modder most
    // needs it to be right. It matched each field across the WHOLE document and paired them BY INDEX, so:
    //   * `overrides` precedes `models` in every pack the Factory writes (verified in the shipped example and the
    //     reference pack), and an override object carries a "pawnDescription" too — so the first model was handed
    //     the first OVERRIDE's pawn name, repointing the wrong unit;
    //   * an entry without `skel`/`atlas` (a runtime-only retexture/tint/sound entry, which Multi-Mod.md documents
    //     as legitimate) shifted every LATER entry's guids onto the wrong model;
    //   * and `n = min(pd, skel, atlas)` then silently DROPPED the tail.
    //
    // The fix reads each entry's fields from its OWN object text, so index alignment stops being a concept.
    public class RegexFallbackTests
    {
        public RegexFallbackTests()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
        }

        // Deliberately unparseable (a doubled comma), so ParseModels must take the regex path — and shaped like a
        // real pack: wrapper keys first, `overrides` BEFORE `models`, exactly as the Factory writes them.
        const string PackWithOverrides =
            @"{ ""schemaVersion"": 1, ""modId"": ""mymod"",,
                ""overrides"": [ { ""modId"": ""enc"", ""pawnDescription"": ""OVERRIDE_PAWN"" } ],
                ""models"": [
                  { ""resourceName"": ""MyModel"", ""pawnDescription"": ""REAL_PAWN"",
                    ""skel"": [1,2,3,4], ""atlas"": [5,6,7,8] } ] }";

        [Fact]
        public void TheFixtureReallyTakesTheRegexPath()
        {
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(PackWithOverrides));
        }

        // THE BUG: the model must get its OWN pawn, not the override's.
        [Fact]
        public void AnOverridesArray_DoesNotStealTheModelsPawn()
        {
            var e = Assert.Single(UniversalInject.ParseModels(PackWithOverrides));
            Assert.Equal("MyModel", e.resourceName);
            Assert.Equal("REAL_PAWN", e.pawnDescription);
            Assert.NotEqual("OVERRIDE_PAWN", e.pawnDescription);
        }

        // A runtime-only entry legitimately has no skel/atlas. It must neither be dropped nor shift its neighbours.
        const string MixedEntries =
            @"{ ""modId"": ""mymod"",,
                ""models"": [
                  { ""resourceName"": ""RetexOnly"", ""pawnDescription"": ""PAWN_A"", ""desaturate"": 1.0 },
                  { ""resourceName"": ""Baked"",     ""pawnDescription"": ""PAWN_B"", ""skel"": [1,2,3,4], ""atlas"": [5,6,7,8] } ] }";

        [Fact]
        public void ARuntimeOnlyEntry_IsNotDroppedAndDoesNotShiftTheNext()
        {
            var list = UniversalInject.ParseModels(MixedEntries);
            Assert.Equal(2, list.Count);                                  // min(pd,skel,atlas) used to yield 1
            Assert.Equal(new[] { "RetexOnly", "Baked" }, list.Select(m => m.resourceName).ToArray());
            Assert.Equal(new[] { "PAWN_A", "PAWN_B" }, list.Select(m => m.pawnDescription).ToArray());

            // the guids belong to the entry that declared them, not to the first entry in the file
            Assert.Equal(0, list[0].sa);                                  // RetexOnly declared no skel
            Assert.Equal(1, list[1].sa);                                  // Baked's skel is Baked's
            Assert.Equal(5, list[1].ta);
        }

        // Field defaults must not differ between the two paths — a fallback that invents a different default is a
        // fallback that quietly changes behaviour on the day it runs.
        [Fact]
        public void AMissingResourceName_DefaultsTheSameAsTheObjectPath()
        {
            var viaObject = Assert.Single(UniversalInject.ParseModels(
                @"{ ""models"": [ { ""pawnDescription"": ""PAWN_A"" } ] }"));
            var viaRegex = Assert.Single(UniversalInject.ParseModels(
                @"{ ""modId"": ""m"",, ""models"": [ { ""pawnDescription"": ""PAWN_A"" } ] }"));
            Assert.Equal(viaObject.resourceName, viaRegex.resourceName);
        }

        // The happy path stays intact: a well-formed pack still parses through the object path unchanged.
        [Fact]
        public void AWellFormedPack_StillParsesNormally()
        {
            var e = Assert.Single(UniversalInject.ParseModels(
                @"{ ""overrides"": [ { ""modId"": ""enc"", ""pawnDescription"": ""OVERRIDE_PAWN"" } ],
                    ""models"": [ { ""resourceName"": ""MyModel"", ""pawnDescription"": ""REAL_PAWN"" } ] }"));
            Assert.Equal("REAL_PAWN", e.pawnDescription);
        }
    }
}
