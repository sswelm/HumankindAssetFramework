using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using R = HumankindAssetFramework.TechRequireAllRules;

namespace HumankindAssetFramework.Tests
{
    // The pure rules behind "this technology needs ALL its prerequisites" (Patches/TechRequireAllRules.cs). The game
    // side is a thin reflection shell over these; what is right or wrong about the feature is decided here.
    public class TechRequireAllRulesTests
    {
        // A small tech tree, by index. Every test states its own states and queue.
        sealed class Tree
        {
            public readonly Dictionary<int, int[]> Parents = new Dictionary<int, int[]>();
            public readonly Dictionary<int, R.State> States = new Dictionary<int, R.State>();
            public readonly HashSet<int> Queue = new HashSet<int>();
            public readonly HashSet<int> All = new HashSet<int>();
            public R.Plan Plan(int target) =>
                R.PlanQueue(target, i => Parents.TryGetValue(i, out var p) ? p : new int[0],
                            i => States.TryGetValue(i, out var s) ? s : R.State.LockedByTechnology,
                            Queue.Contains, All.Contains);
        }

        static string Edges(R.Plan p) => string.Join(" ", p.Edges.Select(e => e.Key + ">" + e.Value).OrderBy(s => s, StringComparer.Ordinal));

        // ---- the tech screen's hint ----

        [Fact]
        public void TheMarkGoesInFrontOfTheTitleAndNoMarkLeavesTheTitleAlone()
        {
            Assert.Equal("& Masonry", R.MarkedTitle("&", "Masonry"));
            Assert.Equal("[Technology] Masonry", R.MarkedTitle("  [Technology] ", "Masonry"));
            Assert.Equal("Masonry", R.MarkedTitle("", "Masonry"));
            Assert.Equal("Masonry", R.MarkedTitle("   ", "Masonry"));
            Assert.Equal("Masonry", R.MarkedTitle(null, "Masonry"));
            Assert.Equal("&", R.MarkedTitle("&", null).Trim());
        }

        [Fact]
        public void TheTooltipLineNamesEveryPrerequisiteAndSurvivesAStrayBrace()
        {
            // the user's wording (2026-11-21): "Requires both Multimedia Computer and Ethernet", in italics
            // ... and then with the title's icon in front of it (the mark fills {2})
            Assert.Equal("[SharedProject] <i>Requires both Multimedia Computer and Ethernet</i>",
                         R.TooltipLine(R.DefaultTooltip, R.DefaultTooltipMany, new[] { "Multimedia Computer", "Ethernet" }, R.DefaultMark));
            Assert.Equal("& <i>Requires both A and B</i>", R.TooltipLine(R.DefaultTooltip, R.DefaultTooltipMany, new[] { "A", "B" }, " & "));
            // no mark: the line starts at the sentence, with no stray space
            Assert.Equal("<i>Requires both A and B</i>", R.TooltipLine(R.DefaultTooltip, R.DefaultTooltipMany, new[] { "A", "B" }, ""));
            Assert.Equal("<i>Requires both A and B</i>", R.TooltipLine(R.DefaultTooltip, R.DefaultTooltipMany, new[] { "A", "B" }));
            Assert.Equal("[SharedProject] <i>Requires all of A, B and C</i>",
                         R.TooltipLine(R.DefaultTooltip, R.DefaultTooltipMany, new[] { "A", "B", "C" }, R.DefaultMark));
            // "both" is wrong for three: the other format, the last one joined by "and"
            Assert.Equal("<i>Requires all of A, B and C</i>", R.TooltipLine(R.DefaultTooltip, R.DefaultTooltipMany, new[] { "A", "B", "C" }));
            Assert.Equal("<i>Requires all of A, B, C and D</i>", R.TooltipLine(R.DefaultTooltip, R.DefaultTooltipMany, new[] { "A", "B", "C", "D" }));
            // each count uses its own format
            Assert.Equal("two: A+B", R.TooltipLine("two: {0}+{1}", "many: {0}+{1}", new[] { "A", "B" }));
            Assert.Equal("many: A, B+C", R.TooltipLine("two: {0}+{1}", "many: {0}+{1}", new[] { "A", "B", "C" }));
            // a format without {1} must not drop the last prerequisite: the whole list goes into {0}
            Assert.Equal("Needs A, B, C.", R.TooltipLine("unused", "Needs {0}.", new[] { "A", "B", "C" }));
            Assert.Equal("Requires ALL of: A, B", R.TooltipLine("Requires ALL of: {0}", "unused", new[] { "A", "B" }));
            // string.Format would throw on these; and a title that holds a slot is not filled in a second time
            Assert.Equal("{ A {3} B }", R.TooltipLine("{ {0} {3} {1} }", "unused", new[] { "A", "B" }));
            Assert.Equal("{1} then B", R.TooltipLine("{0} then {1}", "unused", new[] { "{1}", "B" }));
            Assert.Equal("ends {0", R.TooltipLine("ends {0", "unused", new[] { "A", "B" }));
            // no format, or fewer than two prerequisites (the rule changes nothing then): no line
            Assert.Equal("", R.TooltipLine("", R.DefaultTooltipMany, new[] { "A", "B" }));
            Assert.Equal("", R.TooltipLine(null, R.DefaultTooltipMany, new[] { "A", "B" }));
            Assert.Equal("", R.TooltipLine(R.DefaultTooltip, "", new[] { "A", "B", "C" }));
            Assert.Equal("", R.TooltipLine(R.DefaultTooltip, R.DefaultTooltipMany, new[] { "A" }));
            Assert.Equal("", R.TooltipLine(R.DefaultTooltip, R.DefaultTooltipMany, null));
        }

        [Fact]
        public void TheTooltipLineIsAddedOnce()
        {
            string line = "Requires ALL of: A, B";
            string once = R.WithTooltipLine("Unlocks the web. (1,200 [Science])", line);
            Assert.Equal("Unlocks the web. (1,200 [Science])\n" + line, once);
            Assert.Equal(once, R.WithTooltipLine(once, line));
            Assert.Equal(line, R.WithTooltipLine("", line));
            Assert.Equal(line, R.WithTooltipLine(null, line));
            Assert.Equal(line, R.WithTooltipLine(line, line));
            Assert.Equal("text", R.WithTooltipLine("text", ""));
            // a description that merely ENDS with the same words is not the line: it still gets its own
            Assert.Equal("see Requires ALL of: A, B\n" + line, R.WithTooltipLine("see " + line, line));
        }

        // ---- the list file ----

        [Fact]
        public void ParseList_ReadsTheNames_TrimmedAndWithoutBlanksOrDuplicates()
        {
            var names = R.ParseList("{\"technologies\":[\"Technology_A\",\" Technology_B \",\"\",\"Technology_A\"]}", out string problem);
            Assert.Equal("", problem);
            Assert.Equal(new[] { "Technology_A", "Technology_B" }, names.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        }

        [Theory]
        [InlineData("")]
        [InlineData("{}")]
        [InlineData("{\"technologies\":\"Technology_A\"}")]
        [InlineData("{\"technologies\":[\"Technology_A\"")]
        public void ParseList_ABrokenFileIsNoNamesAndAProblem_NeverAnEmptyList(string json)
        {
            var names = R.ParseList(json, out string problem);
            Assert.Empty(names);
            Assert.NotEqual("", problem);
        }

        [Fact]
        public void ParseList_AnEmptyListIsValid()
        {
            var names = R.ParseList("{\"technologies\":[]}", out string problem);
            Assert.Empty(names);
            Assert.Equal("", problem);
        }

        [Fact]
        public void ParseList_EntriesThatAreNotTextAreLeftOut()
        {
            var names = R.ParseList("{\"technologies\":[1,null,{\"name\":\"x\"},\"Technology_A\"]}", out string problem);
            Assert.Equal("", problem);
            Assert.Equal(new[] { "Technology_A" }, names.ToArray());
        }

        // ---- the ALL rule ----

        [Fact]
        public void AllCompleted_OnePrerequisiteDoneOfTwo_IsNotEnough()
        {
            var st = new Dictionary<int, R.State> { [1] = R.State.Completed, [2] = R.State.Available };
            Assert.False(R.AllCompleted(new[] { 1, 2 }, i => st[i]));
            st[2] = R.State.Completed;
            Assert.True(R.AllCompleted(new[] { 1, 2 }, i => st[i]));
        }

        [Fact]
        public void AllCompleted_ANameTheGameDoesNotHold_NeverLocksForGood()
        {
            var st = new Dictionary<int, R.State> { [1] = R.State.Completed };
            Assert.True(R.AllCompleted(new[] { 1, -1 }, i => st[i]));
            Assert.True(R.AllCompleted(new int[0], i => st[i]));
            Assert.True(R.AllCompleted(null, i => st[i]));
        }

        // ---- the queue plan ----

        [Fact]
        public void Plan_ListedTechnology_QueuesBothParents()
        {
            var t = new Tree();
            t.Parents[10] = new[] { 1, 2 };
            t.States[1] = R.State.Available; t.States[2] = R.State.Available;
            t.All.Add(10);
            var p = t.Plan(10);
            Assert.True(p.Ok); Assert.True(p.UsesRequireAll);
            Assert.Equal(new[] { 1, 2 }, p.Order.ToArray());
            Assert.Equal("1>10 2>10", Edges(p));
        }

        [Fact]
        public void Plan_ListedTechnology_ACompletedParentIsNotQueuedAndNotWaitedFor()
        {
            var t = new Tree();
            t.Parents[10] = new[] { 1, 2 };
            t.States[1] = R.State.Completed; t.States[2] = R.State.Available;
            t.All.Add(10);
            var p = t.Plan(10);
            Assert.True(p.Ok);
            Assert.Equal(new[] { 2 }, p.Order.ToArray());
            Assert.Equal("2>10", Edges(p));
        }

        [Fact]
        public void Plan_NoListedTechnologyInTheTree_IsLeftToTheGame()
        {
            var t = new Tree();
            t.Parents[10] = new[] { 1, 2 };
            t.States[1] = R.State.Available; t.States[2] = R.State.Available;
            var p = t.Plan(10);
            Assert.False(p.UsesRequireAll);
        }

        [Fact]
        public void Plan_UnlistedTechnology_PicksOneParentAsTheGameDoes_FirstListedOrTheLastAlreadyQueued()
        {
            // an unlisted child of a listed technology: the plan is ours, so the ANY choice must match the game's
            var t = new Tree();
            t.Parents[20] = new[] { 10, 11 };   // unlisted target, two locked parents
            t.Parents[10] = new[] { 1, 2 };     // listed
            t.Parents[11] = new[] { 3 };
            t.States[1] = R.State.Available; t.States[2] = R.State.Available; t.States[3] = R.State.Available;
            t.All.Add(10);
            var p = t.Plan(20);                 // nothing queued: the FIRST name (10)
            Assert.True(p.Ok); Assert.True(p.UsesRequireAll);
            Assert.Equal(new[] { 1, 2, 10 }, p.Order.ToArray());
            Assert.Equal("10>20 1>10 2>10", Edges(p));

            t.Queue.Add(11);                    // 11 already queued: the game takes that one, and so the tree holds no listed technology
            var q = t.Plan(20);
            Assert.False(q.UsesRequireAll);
            Assert.Equal(new[] { 11 }, q.Order.ToArray());
        }

        [Fact]
        public void Plan_ListedParentDeeperInTheTree_GetsAllItsParentsBeforeItself()
        {
            var t = new Tree();
            t.Parents[30] = new[] { 20 };
            t.Parents[20] = new[] { 10 };
            t.Parents[10] = new[] { 1, 2 };
            t.States[1] = R.State.Available; t.States[2] = R.State.Completed;
            t.All.Add(10);
            var p = t.Plan(30);
            Assert.True(p.Ok);
            Assert.Equal(new[] { 1, 10, 20 }, p.Order.ToArray());   // parents first, the target itself is not in the list
            Assert.Equal("10>20 1>10 20>30", Edges(p));
        }

        [Fact]
        public void Plan_ADiamond_QueuesTheSharedParentOnce_WithBothConstraints()
        {
            var t = new Tree();
            t.Parents[10] = new[] { 5, 6 };
            t.Parents[5] = new[] { 1 };
            t.Parents[6] = new[] { 1 };
            t.States[1] = R.State.Available;
            t.All.Add(10);
            var p = t.Plan(10);
            Assert.True(p.Ok);
            Assert.Equal(new[] { 1, 5, 6 }, p.Order.ToArray());
            Assert.Equal("1>5 1>6 5>10 6>10", Edges(p));
        }

        [Fact]
        public void Plan_StopsAtAParentAlreadyInTheQueue()
        {
            var t = new Tree();
            t.Parents[10] = new[] { 5, 6 };
            t.Parents[5] = new[] { 1 };
            t.States[6] = R.State.Available;
            t.Queue.Add(5);                      // locked but already queued: its own parents are the queue's business
            t.All.Add(10);
            var p = t.Plan(10);
            Assert.True(p.Ok);
            Assert.Equal(new[] { 5, 6 }, p.Order.ToArray());
            Assert.Equal("5>10 6>10", Edges(p));
        }

        [Fact]
        public void Plan_AParentLockedByItsEra_QueuesNothing()
        {
            var t = new Tree();
            t.Parents[10] = new[] { 1, 2 };
            t.States[1] = R.State.Available; t.States[2] = R.State.LockedByEra;
            t.All.Add(10);
            var p = t.Plan(10);
            Assert.True(p.UsesRequireAll);
            Assert.False(p.Ok);
            Assert.Empty(p.Order); Assert.Empty(p.Edges);
            Assert.Contains("era", p.Why);
        }

        [Fact]
        public void Plan_ACycleInTheData_EndsWithARefusal_NotAHang()
        {
            var t = new Tree();
            t.Parents[10] = new[] { 11, 1 };
            t.Parents[11] = new[] { 10 };
            t.States[1] = R.State.Available;
            t.All.Add(10); t.All.Add(11);
            var p = t.Plan(10);
            Assert.False(p.Ok);
            Assert.Empty(p.Order);
        }

        [Fact]
        public void Plan_AnUnknownPrerequisiteOfAListedTechnology_IsLeftOut()
        {
            var t = new Tree();
            t.Parents[10] = new[] { 1, -1 };
            t.States[1] = R.State.Available;
            t.All.Add(10);
            var p = t.Plan(10);
            Assert.True(p.Ok);
            Assert.Equal(new[] { 1 }, p.Order.ToArray());
        }

        [Fact]
        public void Plan_AllPrerequisiteNamesUnknown_DoesNotQueueTheLockedTechnology()
        {
            var t = new Tree();
            t.Parents[10] = new[] { -1, -1 };
            t.All.Add(10);
            var p = t.Plan(10);
            Assert.True(p.UsesRequireAll);
            Assert.False(p.Ok);
            Assert.Empty(p.Order);
        }

        [Fact]
        public void Plan_AListedTechnologyWithOnePrerequisite_UsesTheVanillaRoute()
        {
            var t = new Tree();
            t.Parents[10] = new[] { 1 };
            t.States[1] = R.State.Available;
            t.All.Add(10);
            Assert.False(t.Plan(10).UsesRequireAll);
        }

        [Fact]
        public void Plan_AnUnlistedLockedTechnologyWithNoKnownParent_IsRefused_AsTheGameRefusesIt()
        {
            var t = new Tree();
            t.Parents[10] = new[] { 5, 1 };
            t.Parents[5] = new[] { -1 };
            t.States[1] = R.State.Available;
            t.All.Add(10);
            var p = t.Plan(10);
            Assert.False(p.Ok);
        }

        [Fact]
        public void TheStateNumbersAreTheGames()
        {
            // TechnologyStates in Amplitude.Mercury.Firstpass (decompiled 2026-11-20): the patch converts by integer
            Assert.Equal(0, (int)R.State.LockedByEra);
            Assert.Equal(1, (int)R.State.LockedByTechnology);
            Assert.Equal(2, (int)R.State.Available);
            Assert.Equal(3, (int)R.State.Completed);
        }
    }
}
