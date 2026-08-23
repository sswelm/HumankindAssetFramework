using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // THE SUB-PAWN WALK DOUBLE-COUNT (2026-08-23).
    //
    // WalkSubPawns collects from four sources that are NOT disjoint: PresentationArmyEntities, presentationSquadronEntities,
    // the air formations' MainPawn, and each battle's AllUnits. A unit fighting a battle is reached through the army list
    // AND the battle list; a squadron through its holder subtree AND its air formation. Every consumer iterates the
    // result per poll, ProcessEngineAudio among them (a top-six FrameCost bucket), so a duplicate is paid repeatedly.
    //
    // WHAT THIS IS *NOT*: these tests were written believing the panel's `sub-pawn walk 56/46` was ten duplicates. The
    // drill collapsed ZERO. That gap is a legitimate superset — SceneScan only counts a sub-pawn whose own gameObject
    // NAME matches a pawnDescription, while the walk adds every sub-pawn of a unit that resolved to one of our entries.
    // The overlap guarded here is structurally real but was not exercised (0 battles, no air unit that session), so
    // treat these as pinning DEFENSIVE behaviour, not a reproduction of an observed defect.
    //
    // The dedupe is generic over the key with an INJECTED id function, and that is not gratuitous: a live
    // UnityEngine.Object cannot be constructed in this test host, and Unity's overloaded `==` makes a bare
    // `new UnityEngine.Object()` read as null — so a test written against the real type could not distinguish "deduped"
    // from "dropped everything". Injecting the id lets the logic be exercised on plain objects.
    public class SubPawnDedupeTests
    {
        sealed class Pawn
        {
            public readonly int Id; public readonly string Name;
            public Pawn(int id, string name) { Id = id; Name = name; }
        }

        static List<KeyValuePair<Pawn, string>> Pairs(params (int id, string entry)[] items) =>
            items.Select(i => new KeyValuePair<Pawn, string>(new Pawn(i.id, "p" + i.id), i.entry)).ToList();

        static List<KeyValuePair<Pawn, string>> Dedupe(List<KeyValuePair<Pawn, string>> src) =>
            UniversalInject.DedupeFirstWins(src, p => p.Id);

        // The shape that was actually on screen: 56 collected, 46 distinct.
        [Fact]
        public void TheRealShape_56Collected_46Distinct()
        {
            var src = new List<KeyValuePair<Pawn, string>>();
            for (int i = 0; i < 46; i++) src.Add(new KeyValuePair<Pawn, string>(new Pawn(i, "p" + i), "Reactor"));
            for (int i = 0; i < 10; i++) src.Add(new KeyValuePair<Pawn, string>(new Pawn(i, "p" + i), "Reactor"));   // re-reached
            Assert.Equal(56, src.Count);

            var got = Dedupe(src);

            Assert.Equal(46, got.Count);
            Assert.Equal(46, got.Select(p => p.Key.Id).Distinct().Count());
        }

        // IDENTITY is the key, not the reference — the same sub-pawn arrives as a DIFFERENT managed wrapper down each
        // path, so deduping by reference would collapse nothing at all.
        [Fact]
        public void DedupesByInstanceIdNotByReference()
        {
            var src = Pairs((7, "A"), (7, "A"));
            Assert.NotSame(src[0].Key, src[1].Key);   // two distinct wrappers, one identity
            Assert.Single(Dedupe(src));
        }

        [Fact]
        public void KeepsEveryDistinctPawn()
        {
            var got = Dedupe(Pairs((1, "A"), (2, "B"), (3, "C")));
            Assert.Equal(3, got.Count);
            Assert.Equal(new[] { 1, 2, 3 }, got.Select(p => p.Key.Id).ToArray());
        }

        // Order is preserved and the FIRST occurrence wins. Both matter: consumers iterate this list, and the army-list
        // reading of a pawn should not be displaced by the battle-list reading of the same pawn.
        [Fact]
        public void FirstOccurrenceWinsAndOrderIsPreserved()
        {
            var got = Dedupe(Pairs((1, "army"), (2, "other"), (1, "battle"), (3, "third"), (2, "battle")));
            Assert.Equal(new[] { 1, 2, 3 }, got.Select(p => p.Key.Id).ToArray());
            Assert.Equal(new[] { "army", "other", "third" }, got.Select(p => p.Value).ToArray());
        }

        [Fact]
        public void ANullKeyIsDropped()
        {
            var src = new List<KeyValuePair<Pawn, string>>
            {
                new KeyValuePair<Pawn, string>(new Pawn(1, "a"), "A"),
                new KeyValuePair<Pawn, string>(null, "ghost"),
                new KeyValuePair<Pawn, string>(new Pawn(2, "b"), "B"),
            };
            var got = Dedupe(src);
            Assert.Equal(2, got.Count);
            Assert.DoesNotContain(got, p => p.Value == "ghost");
        }

        // Cheap-exit paths must not change the answer.
        [Fact]
        public void EmptyNullAndSingleAreUnchanged()
        {
            Assert.Null(UniversalInject.DedupeFirstWins<Pawn, string>(null, p => p.Id));
            Assert.Empty(Dedupe(new List<KeyValuePair<Pawn, string>>()));
            Assert.Single(Dedupe(Pairs((9, "only"))));
        }

        // A list with NOTHING to collapse must come back whole — the guard against a dedupe that is really a truncation.
        [Fact]
        public void AWalkWithNoOverlapIsUntouched()
        {
            var src = Pairs((1, "A"), (2, "B"), (3, "C"), (4, "D"), (5, "E"));
            var got = Dedupe(src);
            Assert.Equal(src.Count, got.Count);
            Assert.Equal(src.Select(p => p.Key.Id), got.Select(p => p.Key.Id));
        }

        // The dedupe must live at the BOUNDARY of WalkSubPawns, not inside AddUnitSubPawns: that method decides whether
        // to fall back to a holder-subtree search by testing `result.Count == before`, so suppressing a duplicate
        // mid-walk would read as "the pawn list yielded nothing" and fire the fallback for a unit already collected.
        [Fact]
        public void TheDedupeIsAppliedAtTheWalkBoundaryNotInsideTheAdders()
        {
            var src = System.IO.File.ReadAllText(RepoFile("Patches/UniversalInject.SubPawnScan.cs"));
            Assert.Contains("DedupeFirstWins(result", Body(src, "WalkSubPawns(List<ModelEntry> list)"));
            foreach (var adder in new[] { "AddUnitSubPawns(object holder", "AddPawnSubPawns(object pawn" })
                Assert.DoesNotContain("DedupeFirstWins", Body(src, adder));
        }

        // Brace-match a method body from its signature — the methods here are not in a fixed order in the file, so
        // slicing between two IndexOf positions is not safe (a first attempt did exactly that and threw).
        static string Body(string src, string signature)
        {
            int i = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(i > 0, "method not found: " + signature);
            int open = src.IndexOf('{', i), depth = 0;
            for (int j = open; j < src.Length; j++)
            {
                if (src[j] == '{') depth++;
                else if (src[j] == '}' && --depth == 0) return src.Substring(open, j - open + 1);
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }

        static string RepoFile(string rel)
        {
            var d = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && d != null; i++, d = System.IO.Path.GetDirectoryName(d))
                if (System.IO.File.Exists(System.IO.Path.Combine(d, rel))) return System.IO.Path.Combine(d, rel);
            throw new System.IO.FileNotFoundException(rel);
        }
    }
}
