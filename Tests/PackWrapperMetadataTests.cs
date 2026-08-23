using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;
using Pack = HumankindAssetFramework.UniversalInject.Pack;

namespace HumankindAssetFramework.Tests
{
    // PACK WRAPPER METADATA — the regression net for the 2026-08-23 review finding.
    //
    // `(string)root["modId"]` returns C# null for `"modId": null` WITHOUT throwing, so a null id escaped ParsePack's
    // catch, reached ResolvePacks' Dictionary as a null KEY, and threw ArgumentNullException. Its only handler resets
    // `entries` and latches `loaded` after three tries — so ONE malformed third-party pack disabled injection for the
    // entire session, the reference pack included, and the failure named no file (WriteLoadReport sits past the throw).
    //
    // These tests pin both halves of the fix: ParsePack never emits an unusable id, and ResolvePacks never throws on
    // one if it somehow gets one anyway. The blast radius of a broken pack must be exactly that pack.
    public class PackWrapperMetadataTests : IDisposable
    {
        readonly string dir;

        public PackWrapperMetadataTests()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
            dir = Path.Combine(Path.GetTempPath(), "haf_wrapper_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        // Writes <name> into the temp dir and parses it as a third-party (non-base) pack.
        Pack Parse(string name, string json)
        {
            var f = Path.Combine(dir, name);
            File.WriteAllText(f, json);
            return UniversalInject.ParsePack(f, isBase: false);
        }

        static Pack P(string id, string file = null) => new Pack { modId = id, file = file ?? (id + ".json") };

        // ---- ParsePack: an unusable wrapper key must leave the computed file-name default standing ----

        // THE BUG, at its source. Before the fix this returned a Pack whose modId was null.
        [Fact]
        public void ParsePack_JsonNullModId_FallsBackToFileName()
        {
            var pk = Parse("evilmod.json", @"{ ""modId"": null, ""models"": [] }");
            Assert.Equal("evilmod", pk.modId);
            Assert.False(string.IsNullOrWhiteSpace(pk.modId));
        }

        [Theory]
        [InlineData(@"{ ""modId"": 3, ""models"": [] }")]                 // a number
        [InlineData(@"{ ""modId"": """", ""models"": [] }")]              // empty string
        [InlineData(@"{ ""modId"": ""   "", ""models"": [] }")]           // whitespace only
        [InlineData(@"{ ""modId"": { ""a"": 1 }, ""models"": [] }")]      // an object
        [InlineData(@"{ ""modId"": [ ""a"" ], ""models"": [] }")]         // an array
        [InlineData(@"{ ""modId"": true, ""models"": [] }")]              // a bool
        public void ParsePack_UnusableModId_FallsBackToFileName(string json)
        {
            Assert.Equal("evilmod", Parse("evilmod.json", json).modId);
        }

        // The happy path is untouched — an explicit id still overrides the file name, and is trimmed.
        [Fact]
        public void ParsePack_ValidModId_Wins()
        {
            Assert.Equal("mymod", Parse("whatever.json", @"{ ""modId"": ""mymod"", ""models"": [] }").modId);
            Assert.Equal("mymod", Parse("whatever2.json", @"{ ""modId"": ""  mymod  "", ""models"": [] }").modId);
        }

        // An absent key is silent and keeps the file-name default (the legacy bare-{models} pack).
        [Fact]
        public void ParsePack_NoWrapperAtAll_UsesFileName()
        {
            Assert.Equal("legacy", Parse("legacy.json", @"{ ""models"": [] }").modId);
        }

        // The same null-cast hazard on the other two wrapper strings: they feed the HK module-order match.
        [Fact]
        public void ParsePack_UnusableModuleKeys_KeepComputedDefaults()
        {
            var pk = Parse("mymod.json", @"{ ""module"": null, ""moduleGuid"": 7, ""models"": [] }");
            Assert.Equal("mymod", pk.moduleName);      // the file-name auto-match, not null
            Assert.Equal("", pk.moduleGuid);           // the field default, not null
        }

        // ---- schemaVersion: a bad value is ignored, and must NOT drag the header into regex recovery ----

        // Before the fix `(int)root["schemaVersion"]` threw on a JSON null, so the whole header fell to the regex
        // path and logged "header didn't JSON-parse" against a file whose JSON was perfectly well-formed.
        [Fact]
        public void ParsePack_NullSchemaVersion_KeepsTheJsonHeaderPath()
        {
            var pk = Parse("mymod.json",
                @"{ ""modId"": ""mymod"", ""schemaVersion"": null, ""dependsOn"": [ ""enc"" ],
                    ""overrides"": [ { ""modId"": ""enc"", ""pawnDescription"": ""Unit_01"" } ], ""models"": [] }");
            Assert.Equal("mymod", pk.modId);
            Assert.Equal(0, pk.schemaVersion);
            Assert.Equal(new[] { "enc" }, pk.dependsOn.ToArray());
            Assert.Single(pk.overrides);                       // the declared override SURVIVED the bad key
            Assert.Equal("enc", pk.overrides[0].modId);
            Assert.Equal("Unit_01", pk.overrides[0].pawn);
        }

        [Fact]
        public void ParsePack_ValidSchemaVersion_StillRead()
        {
            Assert.Equal(2, Parse("mymod.json", @"{ ""modId"": ""mymod"", ""schemaVersion"": 2, ""models"": [] }").schemaVersion);
        }

        // ---- ResolvePacks: defence in depth. A null id must be SKIPPED, never thrown on ----

        // The exact crash, at the exact call site. Before the fix this was an ArgumentNullException.
        [Fact]
        public void ResolvePacks_NullModId_SkipsThatPackOnly()
        {
            var packs = new List<Pack> { P("enc", "haf_models.json"), P(null, "evil/pack.json"), P("other") };
            var notes = new List<string>();

            var ordered = UniversalInject.ResolvePacks(packs, notes);

            Assert.Equal(new[] { "enc", "other" }, ordered.Select(p => p.modId).ToArray());   // everyone else loaded
            Assert.Contains(notes, n => n.Contains("SKIPPED") && n.Contains("pack.json") && n.Contains("modId"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ResolvePacks_UnusableModId_NeverThrows(string id)
        {
            var packs = new List<Pack> { P("enc", "haf_models.json"), P(id, "evil.json") };
            var notes = new List<string>();

            var ordered = UniversalInject.ResolvePacks(packs, notes);   // must not throw

            Assert.Equal(new[] { "enc" }, ordered.Select(p => p.modId).ToArray());
        }

        // A skipped pack must not take its would-be dependants' slot either: dependsOn on a dropped id is the
        // existing "not loaded" rule, and it stays a per-pack skip rather than an exception.
        [Fact]
        public void ResolvePacks_NullModId_DependantIsSkippedNotCrashed()
        {
            var packs = new List<Pack>
            {
                P("enc", "haf_models.json"),
                P(null, "evil.json"),
                new Pack { modId = "needy", file = "needy.json", dependsOn = new List<string> { "evil" } },
            };
            var notes = new List<string>();

            var ordered = UniversalInject.ResolvePacks(packs, notes);

            Assert.Equal(new[] { "enc" }, ordered.Select(p => p.modId).ToArray());
            Assert.Contains(notes, n => n.Contains("SKIPPED") && n.Contains("modId"));
            Assert.Contains(notes, n => n.Contains("needy") && n.Contains("dependsOn"));
        }

        // ---- BLANK IS NOT BROKEN: the reference pack's own shape must load SILENTLY ----

        // Captures warnings this test's own pack file produced. Filtered by the GUID-unique temp file name, so a
        // test class running in parallel cannot leak its log lines into the assertion.
        // Subscribes to the log SOURCE directly rather than to BepInEx's global Logger: a bare
        // `new ManualLogSource(...)` is not attached to Logger.Listeners, so a listener-based capture silently
        // records nothing — and a "no warnings were logged" assertion that cannot observe a warning is a test that
        // cannot fail. The two negative controls below exist to keep that honest.
        List<string> WarningsFor(string name, string json)
        {
            var got = new List<string>();
            EventHandler<LogEventArgs> h = (s, e) =>
            {
                if ((e.Level & LogLevel.Warning) != 0 && (e.Data?.ToString() ?? "").Contains(name)) got.Add(e.Data.ToString());
            };
            Plugin.Log.LogEvent += h;
            try { Parse(name, json); } finally { Plugin.Log.LogEvent -= h; }
            return got;
        }

        // THE REGRESSION, caught by an in-game drill on 2026-08-23. The first cut of WrapperStr warned on anything
        // unusable — including the empty string the editor bakes into `module`/`moduleGuid` on EVERY pack — so a
        // perfectly healthy reference pack logged two warnings on every single load. A warning that fires on the
        // reference pack is worth exactly as much as the silence it replaced.
        [Fact]
        public void ReferencePackShape_LoadsWithoutWarnings()
        {
            var warnings = WarningsFor("ENCReload.json",
                @"{ ""schemaVersion"": 1, ""modId"": ""enc"", ""module"": """", ""moduleGuid"": """",
                    ""dependsOn"": [], ""loadAfter"": [], ""overrides"": [], ""models"": [] }");
            Assert.Empty(warnings);
        }

        // ...and the defaults it falls back to are still the right ones.
        [Fact]
        public void BlankModuleKeys_KeepTheFileNameAutoMatch()
        {
            var pk = Parse("ENCReload.json", @"{ ""modId"": ""enc"", ""module"": """", ""moduleGuid"": """", ""models"": [] }");
            Assert.Equal("enc", pk.modId);
            Assert.Equal("ENCReload", pk.moduleName);   // the auto match that orders the pack against its HK module
            Assert.Equal("", pk.moduleGuid);
        }

        // A blank modId is the one blank that DOES deserve noise: it silently renames the pack to its file name,
        // and that name is the identity other packs write dependsOn/overrides against.
        [Fact]
        public void BlankModId_StillWarns()
        {
            Assert.NotEmpty(WarningsFor("evilmod.json", @"{ ""modId"": """", ""models"": [] }"));
        }

        // A wrong TYPE is an authoring mistake on any key, blank-normal or not.
        [Fact]
        public void WrongTypeModuleKey_StillWarns()
        {
            Assert.NotEmpty(WarningsFor("evilmod.json", @"{ ""modId"": ""x"", ""module"": 7, ""models"": [] }"));
        }

        // ---- TRIM SYMMETRY: an id is trimmed everywhere it is written or compared, or nowhere ----

        // WrapperStr trims the id a pack declares for itself, so `dependsOn` / `overrides` — the places another pack
        // NAMES that id — must trim too. Untrimmed on one side only turns a harmless trailing-space typo into a
        // dependant that resolves today and is skipped tomorrow.
        [Fact]
        public void ParsePack_IdsAreTrimmedOnBothSidesOfAReference()
        {
            var owner = Parse("owner.json", @"{ ""modId"": ""  mymod  "", ""models"": [] }");
            var dep = Parse("dep.json",
                @"{ ""modId"": ""dep"", ""dependsOn"": [ ""  mymod  "" ], ""loadAfter"": [ "" mymod "" ],
                    ""overrides"": [ { ""modId"": "" mymod "", ""pawnDescription"": "" Unit_01 "" } ], ""models"": [] }");

            Assert.Equal("mymod", owner.modId);
            Assert.Equal(new[] { "mymod" }, dep.dependsOn.ToArray());
            Assert.Equal(new[] { "mymod" }, dep.loadAfter.ToArray());
            Assert.Equal("mymod", dep.overrides[0].modId);
            Assert.Equal("Unit_01", dep.overrides[0].pawn);

            // ...and the reference therefore still resolves: dep is ordered behind its dependency, not skipped.
            var notes = new List<string>();
            var ordered = UniversalInject.ResolvePacks(new List<Pack> { dep, owner }, notes);
            Assert.Equal(new[] { "mymod", "dep" }, ordered.Select(p => p.modId).ToArray());
            Assert.DoesNotContain(notes, n => n.Contains("SKIPPED"));
        }

        // The regex RECOVERY twin has to agree with the primary path, or a syntax typo silently changes the ids.
        [Fact]
        public void ParsePack_RegexRecovery_TrimsIdsLikeThePrimaryPath()
        {
            // the trailing comma before `}` breaks JObject.Parse -> the whole header falls to regex recovery
            var pk = Parse("recover.json",
                @"{ ""modId"": ""  mymod  "", ""dependsOn"": [ ""  enc  "" ],
                    ""overrides"": [ { ""modId"": "" enc "", ""pawnDescription"": "" Unit_01 "" } ], ""models"": [],, }");
            Assert.Equal("mymod", pk.modId);
            Assert.Equal(new[] { "enc" }, pk.dependsOn.ToArray());
            Assert.Equal("enc", pk.overrides[0].modId);
            Assert.Equal("Unit_01", pk.overrides[0].pawn);
        }

        // ---- END TO END: the shipped scenario. A stranger drops in a broken pack; ENC still loads. ----
        [Fact]
        public void BrokenThirdPartyPack_DoesNotSinkTheOthers()
        {
            var good = Parse("ENCReload.json", @"{ ""modId"": ""enc"", ""models"": [] }");
            var evil = Parse("evilmod.json",   @"{ ""modId"": null,  ""models"": [] }");

            var notes = new List<string>();
            var ordered = UniversalInject.ResolvePacks(new List<Pack> { good, evil }, notes);

            // Both survive: ParsePack gave the broken one a usable id from its file name, so it is not even dropped.
            Assert.Equal(new[] { "enc", "evilmod" }, ordered.Select(p => p.modId).ToArray());
            Assert.DoesNotContain(notes, n => n.Contains("SKIPPED"));
        }
    }
}
