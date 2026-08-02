using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;
using Pack = HumankindAssetFramework.UniversalInject.Pack;

namespace HumankindAssetFramework.Tests
{
    // Regression net for the multi-pack conflict resolution + the wrapper-parse recovery — the exact logic behind
    // several bugs fixed this session. All pure data (Pack lists / raw JSON text); no live-game reflection.
    public class PackResolveTests
    {
        public PackResolveTests()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
        }

        static Pack P(string id, IEnumerable<string> dependsOn = null, IEnumerable<string> loadAfter = null) =>
            new Pack
            {
                modId = id,
                file = id + ".json",
                dependsOn = (dependsOn ?? Enumerable.Empty<string>()).ToList(),
                loadAfter = (loadAfter ?? Enumerable.Empty<string>()).ToList(),
            };

        static List<string> Ids(List<Pack> packs) => packs.Select(p => p.modId).ToList();

        // The invariant that keeps a single-pack (today's) setup provably unchanged: no constraints -> seed order, verbatim.
        [Fact]
        public void ResolvePacks_NoConstraints_KeepsSeedOrder()
        {
            var packs = new List<Pack> { P("base"), P("a"), P("b") };
            var ordered = UniversalInject.ResolvePacks(packs, new List<string>());
            Assert.Equal(new[] { "base", "a", "b" }, Ids(ordered));
        }

        // loadAfter is a SOFT ordering edge: A declaring loadAfter B moves A behind B even though A was seeded first.
        [Fact]
        public void ResolvePacks_LoadAfter_ReordersBehindTarget()
        {
            var packs = new List<Pack> { P("a", loadAfter: new[] { "b" }), P("b") };
            var ordered = UniversalInject.ResolvePacks(packs, new List<string>());
            Assert.Equal(new[] { "b", "a" }, Ids(ordered));
        }

        // dependsOn is a HARD ordering edge too (B before A).
        [Fact]
        public void ResolvePacks_DependsOn_OrdersDependencyFirst()
        {
            var packs = new List<Pack> { P("a", dependsOn: new[] { "b" }), P("b") };
            var ordered = UniversalInject.ResolvePacks(packs, new List<string>());
            Assert.Equal(new[] { "b", "a" }, Ids(ordered));
        }

        // A missing hard dependency SKIPS the pack (and the fixpoint strands its dependents).
        [Fact]
        public void ResolvePacks_MissingDependency_SkipsPack()
        {
            var packs = new List<Pack> { P("a", dependsOn: new[] { "ghost" }), P("b") };
            var notes = new List<string>();
            var ordered = UniversalInject.ResolvePacks(packs, notes);
            Assert.Equal(new[] { "b" }, Ids(ordered));
            Assert.Contains(notes, n => n.Contains("SKIPPED") && n.Contains("ghost"));
        }

        // Duplicate modId: first file keeps the id, later one is dropped with a note.
        [Fact]
        public void ResolvePacks_DuplicateModId_KeepsFirstDropsSecond()
        {
            var first = P("dup"); first.file = "first.json";
            var second = P("dup"); second.file = "second.json";
            var notes = new List<string>();
            var ordered = UniversalInject.ResolvePacks(new List<Pack> { first, second, P("b") }, notes);
            Assert.Equal(new[] { "dup", "b" }, Ids(ordered));
            Assert.Same(first, ordered[0]);   // the FIRST file's pack survived
            Assert.Contains(notes, n => n.Contains("duplicate modId"));
        }

        // ---- RegexStrArray: the last-resort recovery of wrapper string-arrays (dependsOn/loadAfter/overrides)
        // when JObject.Parse fails — the fix behind "wrapper-parse drops overrides". ----
        [Fact]
        public void RegexStrArray_ExtractsQuotedItems()
        {
            var text = @"{ ""loadAfter"": [ ""alpha"", ""beta"" ], ""x"": 1 }";
            Assert.Equal(new[] { "alpha", "beta" }, UniversalInject.RegexStrArray(text, "loadAfter").ToArray());
        }

        [Fact]
        public void RegexStrArray_MissingField_ReturnsEmpty()
        {
            Assert.Empty(UniversalInject.RegexStrArray(@"{ ""models"": [] }", "loadAfter"));
        }
    }
}
