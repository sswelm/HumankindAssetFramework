using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Haf.Schema;
using HumankindAssetFramework;
using Xunit;
using Pack = HumankindAssetFramework.UniversalInject.Pack;

namespace HumankindAssetFramework.Tests
{
    // THE REGISTRY SCHEMA CONTRACT — the regression net for the 2026-08-23 review finding that `schemaVersion` was
    // decorative: parsed on both paths, printed into haf_load_report.txt, and read back by nobody, so a pack could
    // declare any version at all and load identically.
    //
    // The contract these pin is the one docs/Multi-Mod.md has stated since the pack format shipped — ADDITIVE
    // evolution, old files keep loading — so the rule under test is "say something useful", never "refuse".
    public class SchemaVersionTests : System.IDisposable
    {
        readonly string dir;

        public SchemaVersionTests()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
            dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "haf_schema_tests_" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
        }

        public void Dispose() { try { System.IO.Directory.Delete(dir, true); } catch { } }

        Pack Parse(string name, string json)
        {
            var f = System.IO.Path.Combine(dir, name);
            System.IO.File.WriteAllText(f, json);
            return UniversalInject.ParsePack(f, isBase: false);
        }

        // ---- the constants have to be coherent with each other ----

        [Fact]
        public void Constants_AreOrdered()
        {
            Assert.True(HafSchema.MinReadable <= HafSchema.Version,
                "the oldest readable schema cannot be newer than the one this build implements");
            Assert.True(HafSchema.Unversioned < HafSchema.MinReadable,
                "the 'no schemaVersion declared' sentinel must sit below every real version, or a legacy pack reads as a real one");
            Assert.True(HafSchema.Version >= 1);
        }

        // ---- the ordinary pack says nothing: the report already prints its number ----

        [Fact]
        public void CurrentVersion_IsSilent()
        {
            Assert.Null(UniversalInject.CheckSchema("mymod", HafSchema.Version));
        }

        [Fact]
        public void EveryReadableVersion_IsSilent()
        {
            for (int v = HafSchema.MinReadable; v <= HafSchema.Version; v++)
                Assert.Null(UniversalInject.CheckSchema("mymod", v));
        }

        // ---- absent: a legacy bare pack. Worth a line, not a warning ----

        [Fact]
        public void Unversioned_NotesButDoesNotWarn()
        {
            var n = UniversalInject.CheckSchema("legacy", HafSchema.Unversioned);
            Assert.NotNull(n);
            Assert.False(n.Warn);
            Assert.Contains("legacy", n.Text);
        }

        // ---- from the future: THE case the field exists for, and the one that was silent ----

        [Theory]
        [InlineData(2)]
        [InlineData(7)]
        [InlineData(99)]
        public void FutureVersion_Warns(int declared)
        {
            var n = UniversalInject.CheckSchema("newmod", declared);
            Assert.NotNull(n);
            Assert.True(n.Warn);
            Assert.Contains("newmod", n.Text);
            Assert.Contains(declared.ToString(), n.Text);              // the version the pack wants
            Assert.Contains(HafSchema.Version.ToString(), n.Text);     // ...against the one it gets
            Assert.Contains("IGNORED", n.Text);                        // and the actual consequence, named
        }

        // The advisory has to say what to DO about it, or it is just another number in a log.
        [Fact]
        public void FutureVersion_NamesTheRemedy()
        {
            Assert.Contains("Update HAF", UniversalInject.CheckSchema("newmod", HafSchema.Version + 1).Text);
        }

        // ---- below the floor: today only a malformed negative can reach this, but the lever must work ----

        [Theory]
        [InlineData(-1)]
        [InlineData(-99)]
        public void BelowMinReadable_Warns(int declared)
        {
            var n = UniversalInject.CheckSchema("ancient", declared);
            Assert.NotNull(n);
            Assert.True(n.Warn);
            Assert.Contains("older", n.Text);
        }

        // ---- FAIL-SOFT IS THE CONTRACT: no verdict may ever drop a pack ----

        // The advisory is a message, not a gate. ResolvePacks is what decides who loads, and it does not consult
        // the schema at all — so a pack from the future, or from before the floor, still loads. If this ever fails,
        // someone has turned an advisory into a refusal and broken the additive promise in Multi-Mod.md.
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(99)]
        [InlineData(-5)]
        public void NoSchemaVersion_EverCostsAPackItsPlace(int declared)
        {
            var packs = new List<Pack>
            {
                new Pack { modId = "enc",   file = "haf_models.json", schemaVersion = HafSchema.Version },
                new Pack { modId = "other", file = "other.json",      schemaVersion = declared },
            };
            var ordered = UniversalInject.ResolvePacks(packs, new List<string>());
            Assert.Equal(new[] { "enc", "other" }, ordered.Select(p => p.modId).ToArray());
        }

        // ---- INTEGRATION: the declared version has to survive PARSING to reach the advisory at all ----

        // The number is read on two independent paths, and an advisory that only fires on one of them is a coin
        // flip: a pack with a JSON typo recovers through the regex path, and that is exactly the pack most likely
        // to have been hand-written against a newer HAF.
        [Fact]
        public void FutureVersion_SurvivesTheJsonPath()
        {
            var pk = Parse("newmod.json", @"{ ""modId"": ""newmod"", ""schemaVersion"": 99, ""models"": [] }");
            Assert.Equal(99, pk.schemaVersion);
            Assert.True(UniversalInject.CheckSchema(pk.modId, pk.schemaVersion).Warn);
        }

        [Fact]
        public void FutureVersion_SurvivesTheRegexRecoveryPath()
        {
            // the doubled comma breaks JObject.Parse -> the whole header falls to regex recovery
            var pk = Parse("newmod.json", @"{ ""modId"": ""newmod"", ""schemaVersion"": 99, ""models"": [],, }");
            Assert.Equal(99, pk.schemaVersion);
            Assert.True(UniversalInject.CheckSchema(pk.modId, pk.schemaVersion).Warn);
        }

        // The shipped example pack and the reference pack's own shape must be silent — if the ordinary pack warns,
        // the warning stops meaning anything within one session.
        [Fact]
        public void AnOrdinaryPack_ProducesNoAdvisory()
        {
            var pk = Parse("yourmod.json", @"{ ""modId"": ""yourmod"", ""schemaVersion"": 1, ""models"": [] }");
            Assert.Null(UniversalInject.CheckSchema(pk.modId, pk.schemaVersion));
        }

        // A legacy bare pack really does arrive as Unversioned rather than as some accidental real version.
        [Fact]
        public void LegacyBarePack_ArrivesUnversioned()
        {
            var pk = Parse("legacy.json", @"{ ""models"": [] }");
            Assert.Equal(HafSchema.Unversioned, pk.schemaVersion);
            var n = UniversalInject.CheckSchema(pk.modId, pk.schemaVersion);
            Assert.NotNull(n);
            Assert.False(n.Warn);
        }
    }
}
