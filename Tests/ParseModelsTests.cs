using System.Linq;
using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // Robustness of the registry model parse — the case the testing-strategy note called out for third-party
    // enc_models.json: malformed/partial input must NOT throw, and must yield sane defaults. Also proves the
    // Newtonsoft per-object parse keeps each field with ITS model (the reason it's preferred over the index-aligned
    // regex fallback), and that the regex fallback still recovers when JObject.Parse rejects the document.
    public class ParseModelsTests
    {
        public ParseModelsTests()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
        }

        [Fact]
        public void ParseModels_GarbageInput_ReturnsEmptyWithoutThrowing()
        {
            var e = UniversalInject.ParseModels("this is not json {[ ,,");
            Assert.Empty(e);   // Newtonsoft throws -> caught -> regex finds nothing -> empty, no exception
        }

        [Fact]
        public void ParseModels_EmptyString_ReturnsEmptyWithoutThrowing()
        {
            Assert.Empty(UniversalInject.ParseModels(""));
        }

        [Fact]
        public void ParseModels_MissingSkel_DefaultsGuidToZero()
        {
            var e = Assert.Single(UniversalInject.ParseModels(@"{ ""models"": [ { ""resourceName"": ""NoSkel"" } ] }"));
            Assert.Equal(0, e.sa); Assert.Equal(0, e.sb); Assert.Equal(0, e.sc); Assert.Equal(0, e.sd);
        }

        [Fact]
        public void ParseModels_PreservesSignedGuidComponents()
        {
            var e = Assert.Single(UniversalInject.ParseModels(@"{ ""models"": [ { ""resourceName"": ""Signed"", ""skel"": [-1, 2, -3, 4] } ] }"));
            Assert.Equal(-1, e.sa); Assert.Equal(2, e.sb); Assert.Equal(-3, e.sc); Assert.Equal(4, e.sd);
        }

        // The headline property of the per-OBJECT parse: a field omitted on one model does not shift onto another —
        // each model keeps its own values / defaults. (The index-aligned regex fallback is what this avoids.)
        [Fact]
        public void ParseModels_PerModelFieldIsolation()
        {
            var json = @"{ ""models"": [
                { ""resourceName"": ""A"", ""pawnDescription"": ""pa"" },
                { ""resourceName"": ""B"", ""pawnDescription"": ""pb"", ""scale"": 3.0 }
            ] }";
            var e = UniversalInject.ParseModels(json);
            Assert.Equal(2, e.Count);
            Assert.Equal("A", e[0].resourceName);
            Assert.Equal("B", e[1].resourceName);
            Assert.Equal(1f, e[0].scale);   // A omitted scale -> default, NOT borrowed from B
            Assert.Equal(3f, e[1].scale);
        }

        // When JObject.Parse rejects the document (here: truncated), the regex fallback still recovers the fields.
        // NOTE: the fallback keys the entry count on Min(pawnDescription, skel, atlas) — a recoverable model needs all
        // three (line ~729). That's the fallback's documented shape, so the test carries an atlas too.
        [Fact]
        public void ParseModels_MalformedButRecoverable_UsesRegexFallback()
        {
            var truncated = @"{ ""models"": [ { ""resourceName"": ""X"", ""pawnDescription"": ""Y"", ""skel"": [1,2,3,4], ""atlas"": [5,6,7,8] }";  // missing ]}
            var e = Assert.Single(UniversalInject.ParseModels(truncated));
            Assert.Equal("X", e.resourceName);
            Assert.Equal("Y", e.pawnDescription);
            Assert.Equal(1, e.sa); Assert.Equal(4, e.sd);
            Assert.Equal(5, e.ta); Assert.Equal(8, e.td);
        }
    }
}
