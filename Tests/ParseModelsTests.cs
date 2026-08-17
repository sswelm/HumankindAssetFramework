using System.Linq;
using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // Robustness of the registry model parse — the case the testing-strategy note called out for third-party
    // haf_models.json: malformed/partial input must NOT throw, and must yield sane defaults. Also proves the
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

        // The generic ToObject path deserializes the `position` Vector3 from its {x,y,z} object (the parse then re-pins it
        // from the same read, so it's immune to any struct-deserialization quirk). Proves the Vector3 field round-trips.
        [Fact]
        public void ParseModels_ParsesPositionVector()
        {
            var e = Assert.Single(UniversalInject.ParseModels(
                @"{ ""models"": [ { ""resourceName"": ""P"", ""position"": { ""x"": 1.5, ""y"": -2.0, ""z"": 0.25 } } ] }"));
            Assert.Equal(1.5f, e.position.x);
            Assert.Equal(-2f, e.position.y);
            Assert.Equal(0.25f, e.position.z);
        }

        // A field omitted from the JSON falls to the SHARED HafModelSchema initializer (not a hand-listed parse default):
        // idleAltInterval -> 25f (the editor default), turretAxis -> -1. A present value still wins. Locks the generic
        // deserialize's default behavior to the one authoritative declaration.
        [Fact]
        public void ParseModels_OmittedFields_UseSharedSchemaDefaults()
        {
            var def = Assert.Single(UniversalInject.ParseModels(@"{ ""models"": [ { ""resourceName"": ""D"" } ] }"));
            Assert.Equal(25f, def.idleAltInterval);
            Assert.Equal(-1, def.turretAxis);

            var set = Assert.Single(UniversalInject.ParseModels(
                @"{ ""models"": [ { ""resourceName"": ""S"", ""idleAltInterval"": 4.0, ""turretAxis"": 2 } ] }"));
            Assert.Equal(4f, set.idleAltInterval);
            Assert.Equal(2, set.turretAxis);
        }

        // VERIFIED-REVIEW GUARD (2026-08-17): runtime-STATE fields are public on ModelEntry, and the generic
        // ToObject would bind ANY name-matching key — a third-party pack carrying "repointed": true (or a typo'd
        // key landing on state) would poison the session before the re-arm ever runs. The parse now strips every
        // non-whitelisted key first; this pins that hostile state keys land on DEFAULTS, not the JSON values.
        [Fact]
        public void ParseModels_RuntimeStateKeys_AreStripped_NotBound()
        {
            var e = Assert.Single(UniversalInject.ParseModels(@"{ ""models"": [ { ""resourceName"": ""H"",
                ""repointed"": true, ""descId"": 999, ""animId"": 7, ""skeletonId"": 5, ""assetDir"": ""evil"" } ] }"));
            Assert.False(e.repointed);
            Assert.Equal(-1, e.descId);
            Assert.Equal(-1, e.animId);
            Assert.Equal(-1, e.skeletonId);
            Assert.Equal("", e.assetDir);
        }

        // The nastier corner of the same finding: a key colliding with a READONLY collection (phaseTracks) made
        // ToObject THROW, silently demoting the whole pack to the index-aligned regex fallback. With the strip the
        // object parse survives — proven by the per-model isolation ONLY the object parse provides (the fallback
        // would zip the single `scale` onto model A, and would not even yield two entries for this document).
        [Fact]
        public void ParseModels_ReadonlyCollectionCollision_StaysOnObjectParse()
        {
            var json = @"{ ""models"": [
                { ""resourceName"": ""A"", ""phaseTracks"": [ { ""phase"": 1.0 } ], ""stateSamples"": [ 1, 2 ] },
                { ""resourceName"": ""B"", ""scale"": 3.0 }
            ] }";
            var e = UniversalInject.ParseModels(json);
            Assert.Equal(2, e.Count);
            Assert.Empty(e[0].phaseTracks);   // the colliding key was stripped, never bound
            Assert.Equal(1f, e[0].scale);     // A omitted scale -> shared default, NOT B's value
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
