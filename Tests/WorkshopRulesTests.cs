using System.Collections.Generic;
using Xunit;

// The fuse-groupings sidecar must never mark a part the user did not mark (review of 82088d4: name-only restore
// selected every namesake). Rows are (node index, name).
public class WorkshopRulesTests
{
    static IList<KeyValuePair<int, string>> Rows(params (int, string)[] rows)
    {
        var l = new List<KeyValuePair<int, string>>();
        foreach (var (i, n) in rows) l.Add(new KeyValuePair<int, string>(i, n));
        return l;
    }

    [Fact]
    public void A_namesake_saved_alone_comes_back_alone()
    {
        var rows = Rows((3, "Panel"), (7, "Panel"), (9, "Hull"));
        var refused = new List<string>();
        var got = WorkshopRules.ResolveFuseSidecar(new[] { WorkshopRules.SidecarHeader, WorkshopRules.SidecarLine("A", "Panel", 3) }, rows, refused);
        Assert.Equal(new Dictionary<int, string> { [3] = "A" }, got);
        Assert.Empty(refused);
    }

    [Fact]
    public void Namesakes_in_different_groups_keep_their_own_letters()
    {
        var rows = Rows((3, "Panel"), (7, "Panel"));
        var got = WorkshopRules.ResolveFuseSidecar(new[] { "A|Panel|3", "B|Panel|7" }, rows, null);
        Assert.Equal("A", got[3]); Assert.Equal("B", got[7]); Assert.Equal(2, got.Count);
    }

    [Fact]
    public void An_old_name_only_line_applies_only_where_the_name_is_unique()
    {
        var rows = Rows((3, "Panel"), (7, "Panel"), (9, "Hull"));
        var refused = new List<string>();
        var got = WorkshopRules.ResolveFuseSidecar(new[] { "A|Panel", "B|Hull" }, rows, refused);
        Assert.Equal(new Dictionary<int, string> { [9] = "B" }, got);   // "Panel" refused, nothing guessed
        Assert.Single(refused); Assert.Contains("Panel", refused[0]); Assert.Contains("2 parts", refused[0]);
    }

    [Fact]
    public void A_moved_index_falls_back_to_a_unique_name_and_an_unknown_name_is_refused()
    {
        var rows = Rows((9, "Hull"), (10, "Deck"));
        var refused = new List<string>();
        var got = WorkshopRules.ResolveFuseSidecar(new[] { "A|Hull|5", "A|Mast|6", "C|Deck|9" }, rows, refused);   // re-export: Hull moved 5 -> 9; index 9 no longer "Deck"
        Assert.Equal("A", got[9]); Assert.Equal("C", got[10]); Assert.Equal(2, got.Count);
        Assert.Single(refused); Assert.Contains("Mast", refused[0]);
    }

    [Fact]
    public void A_name_containing_the_delimiter_never_lands_on_another_part()
    {
        // review of 0a8b56e: "A|Hull|Port|3" (old layout, name in the middle) was read as name "Hull" and marked Hull.
        // v2 puts the name LAST under a header line: nothing after it, nothing to guess.
        var rows = Rows((1, "Hull"), (3, "Hull|Port"), (4, "Deck|1"));
        var refused = new List<string>();
        var v2 = new[] { WorkshopRules.SidecarHeader, WorkshopRules.SidecarLine("A", "Hull|Port", 3), WorkshopRules.SidecarLine("C", "Deck|1", 4) };
        var got = WorkshopRules.ResolveFuseSidecar(v2, rows, refused);
        Assert.Equal(new Dictionary<int, string> { [3] = "A", [4] = "C" }, got);
        Assert.Empty(refused);
        Assert.Equal("A|3|Hull|Port", WorkshopRules.SidecarLine("A", "Hull|Port", 3));

        // the old layouts still read: "B|Hull|Port" (no index) is the whole remainder; "A|Hull|Port|3" resolves by index
        got = WorkshopRules.ResolveFuseSidecar(new[] { "B|Hull|Port" }, rows, refused);
        Assert.Equal(new Dictionary<int, string> { [3] = "B" }, got);
        got = WorkshopRules.ResolveFuseSidecar(new[] { "A|Hull|Port|3" }, rows, refused);
        Assert.Equal(new Dictionary<int, string> { [3] = "A" }, got);
        Assert.Empty(refused);
    }

    [Fact]
    public void A_re_export_that_moves_a_part_never_lands_its_group_on_a_namesake_with_a_numeric_suffix()
    {
        // review of 6db9a00: old-layout "A|Hull|3", Hull re-exported to node 5, another part called "Hull|3".
        // Reading 1 (name Hull, index 3): node 3 is not Hull, but Hull is unique -> 5. Reading 2 (name "Hull|3") -> 7.
        // Two different parts: refused, not chosen.
        var rows = Rows((5, "Hull"), (7, "Hull|3"), (3, "Keel"));
        var refused = new List<string>();
        var got = WorkshopRules.ResolveFuseSidecar(new[] { "A|Hull|3" }, rows, refused);
        Assert.Empty(got); Assert.Single(refused); Assert.Contains("2 different parts", refused[0]);

        // the v2 line for the same save is unambiguous: Hull moved, Hull is unique -> 5
        got = WorkshopRules.ResolveFuseSidecar(new[] { WorkshopRules.SidecarHeader, "A|3|Hull" }, rows, null);
        Assert.Equal(new Dictionary<int, string> { [5] = "A" }, got);
        // and if the index still matches, the old layout is settled by it even with the namesake around
        got = WorkshopRules.ResolveFuseSidecar(new[] { "A|Hull|3" }, Rows((3, "Hull"), (9, "Other")), null);
        Assert.Equal(new Dictionary<int, string> { [3] = "A" }, got);
        // but index-match plus a unique part named by the whole remainder is still two fits -> refused
        got = WorkshopRules.ResolveFuseSidecar(new[] { "A|Hull|3" }, Rows((3, "Hull"), (7, "Hull|3")), refused);
        Assert.Empty(got); Assert.Equal(2, refused.Count);
    }

    [Fact]
    public void Malformed_lines_and_bad_letters_are_ignored()
    {
        var rows = Rows((1, "Hull"));
        var got = WorkshopRules.ResolveFuseSidecar(new[] { "", "no bar", "[|Hull|1", "AB|Hull|1", "A||1", " a|Hull|1 " }, rows, null);
        Assert.Empty(got);
        got = WorkshopRules.ResolveFuseSidecar(new[] { WorkshopRules.SidecarHeader, "Z|1|Hull" }, rows, null);   // A–Z since 2026-09-16
        Assert.Equal(new Dictionary<int, string> { [1] = "Z" }, got);
        Assert.Equal("A|1|Hull", WorkshopRules.SidecarLine("A", "Hull", 1));
    }

    [Fact]
    public void The_fuse_report_puts_every_island_and_stitched_part_on_its_own_line()
    {
        var groups = new List<WorkshopRules.FuseGroupReport>
        {
            new WorkshopRules.FuseGroupReport { Letter = "A", Changed = true, PartNames = new[] { "Object_6", "Object_8" },
                Details = new[] { "Fused 2 part(s) -> 'Object_6_Fused': 10 -> 8 verts", "largest islands: 40 faces (6% boundary, kept, 3 rewound); 12 faces (50% boundary, kept, 0 rewound)", "stitched parts: Object_8 90% verts shared, 94% of its touching faces lying on them" },
                Warnings = new[] { "1 lap/trim strip(s) lying on the other parts' surface: 'Object_8'." } },
            new WorkshopRules.FuseGroupReport { Letter = "B", Changed = false, PartNames = new[] { "Object_54" }, Details = new string[0], Warnings = new[] { "Nothing to fuse: the chosen parts carry no triangles." } },
        };
        string text = WorkshopRules.FuseReport(@"D:\m\ship.glb", @"D:\m\ship_split.glb", 0.5, groups);
        var lines = text.Split('\n');
        Assert.Contains(@"source: D:\m\ship.glb", lines);
        Assert.Contains("weld: 0.5 permille of the model's length", lines);
        Assert.Contains("== group A — 2 part(s)", lines);
        Assert.Contains("parts: Object_6, Object_8", lines);
        Assert.Contains("WARNING: 1 lap/trim strip(s) lying on the other parts' surface: 'Object_8'.", lines);
        Assert.Contains("Fused 2 part(s) -> 'Object_6_Fused': 10 -> 8 verts", lines);
        Assert.Contains("largest islands:", lines);
        Assert.Contains("  40 faces (6% boundary, kept, 3 rewound)", lines);
        Assert.Contains("  12 faces (50% boundary, kept, 0 rewound)", lines);
        Assert.Contains("stitched parts:", lines);
        Assert.Contains("  Object_8 90% verts shared, 94% of its touching faces lying on them", lines);
        Assert.Contains("== group B — 1 part(s) — NOTHING FUSED", lines);
        Assert.Contains("WARNING: Nothing to fuse: the chosen parts carry no triangles.", lines);
    }

    [Fact]
    public void Output_names_chain_by_suffix()
    {
        Assert.Equal("ship_cut", WorkshopRules.NextOutputName("ship", "_cut"));
        Assert.Equal("ship_cut2", WorkshopRules.NextOutputName("ship_cut", "_cut"));
        Assert.Equal("ship_cut3", WorkshopRules.NextOutputName("ship_cut2", "_cut"));
        Assert.Equal("ship_cut10", WorkshopRules.NextOutputName("ship_cut9", "_cut"));
        Assert.Equal("ship_split", WorkshopRules.NextOutputName("ship", "_split"));
        Assert.Equal("ship_split2", WorkshopRules.NextOutputName("ship_split", "_split"));
        Assert.Equal("ship_split_cut", WorkshopRules.NextOutputName("ship_split", "_cut"));   // a different operation: appended, not counted
        Assert.Equal("ship_cut_split_cut", WorkshopRules.NextOutputName("ship_cut_split", "_cut"));
        Assert.Equal("ship_CUT2", WorkshopRules.NextOutputName("ship_CUT", "_cut"));   // case-insensitive match keeps the source spelling
    }

    [Fact]
    public void Letters_pass_down_to_the_children_a_split_or_cut_created()
    {
        // source: node 3 "Hull" marked A, node 5 "Deck" marked B. Output: 3 lost its mesh to children 10 and 11 (_Part_001/_002),
        // 5 was cut into 12 and 13; node 7 is an unmarked part; node 20 is a grandchild of 3 (a cut of a split)
        var letters = new Dictionary<int, string> { [3] = "A", [5] = "B" };
        var table = new List<KeyValuePair<int, int>> { new KeyValuePair<int, int>(3, -1), new KeyValuePair<int, int>(5, -1), new KeyValuePair<int, int>(7, -1),
            new KeyValuePair<int, int>(10, 3), new KeyValuePair<int, int>(11, 3), new KeyValuePair<int, int>(12, 5), new KeyValuePair<int, int>(13, 5), new KeyValuePair<int, int>(20, 10) };
        var got = WorkshopRules.TransferLetters(letters, table, new HashSet<int> { 7, 10, 11, 12, 13, 20 }, 8);   // mesh nodes only (3 and 5 are meshless now); the source had 8 nodes
        Assert.Equal(new Dictionary<int, string> { [10] = "A", [11] = "A", [20] = "A", [12] = "B", [13] = "B" }, got);
        // a marked node that still carries its mesh keeps its own letter
        var kept = WorkshopRules.TransferLetters(letters, table, new HashSet<int> { 3, 7 }, 8);
        Assert.Equal(new Dictionary<int, string> { [3] = "A" }, kept);
        // a child that EXISTED before the split (index below the source's node count) never inherits: the hull's unmarked
        // prop stays unmarked, and a marked pre-existing child keeps its own letter (review of 26b4571)
        var withProp = new List<KeyValuePair<int, int>>(table) { new KeyValuePair<int, int>(4, 3), new KeyValuePair<int, int>(6, 3) };
        var lettersProp = new Dictionary<int, string> { [3] = "A", [6] = "C" };
        var got2 = WorkshopRules.TransferLetters(lettersProp, withProp, new HashSet<int> { 4, 6, 10, 11 }, 8);
        Assert.Equal(new Dictionary<int, string> { [6] = "C", [10] = "A", [11] = "A" }, got2);
        // splitting the UNMARKED prop (node 4, child of the marked hull) gives its pieces nothing: inheritance stops at the
        // first original node, the prop, and takes its lack of a letter — never the grandparent's (review of 4caf027)
        var propSplit = new List<KeyValuePair<int, int>>(withProp) { new KeyValuePair<int, int>(30, 4), new KeyValuePair<int, int>(31, 4) };
        var got3 = WorkshopRules.TransferLetters(lettersProp, propSplit, new HashSet<int> { 6, 10, 11, 30, 31 }, 8);
        Assert.Equal(new Dictionary<int, string> { [6] = "C", [10] = "A", [11] = "A" }, got3);
        // while a cut of a split piece (new under new under the marked hull) still reaches the hull
        var cutOfSplit = new List<KeyValuePair<int, int>>(propSplit) { new KeyValuePair<int, int>(40, 10) };
        var got4 = WorkshopRules.TransferLetters(lettersProp, cutOfSplit, new HashSet<int> { 40 }, 8);
        Assert.Equal(new Dictionary<int, string> { [40] = "A" }, got4);
    }

    [Fact]
    public void The_highlight_moves_to_the_next_row_when_a_mark_hides_the_current_one()
    {
        Assert.Equal(3, WorkshopRules.NextHighlight(2, 5));    // the row after it
        Assert.Equal(3, WorkshopRules.NextHighlight(4, 5));    // at the end of the list: the row before it
        Assert.Equal(1, WorkshopRules.NextHighlight(0, 5));
        Assert.Equal(-1, WorkshopRules.NextHighlight(0, 1));   // it was the only row: nothing left to highlight
        Assert.Equal(-1, WorkshopRules.NextHighlight(-1, 5));  // nothing was highlighted
        Assert.Equal(-1, WorkshopRules.NextHighlight(5, 5));   // out of range
    }

    static float[] V(float x, float y, float z) => new[] { x, y, z };

    [Fact]
    public void The_mirror_is_the_part_whose_box_is_the_reflection_across_the_centreline()
    {
        // side axis 0; a hull half each side, a keel on the centreline, a stray chain link far to one side
        var mins = new List<float[]> { V(-9, 5.8f, 7), V(0, 5.8f, 7), V(-0.5f, -10, 14), V(30, 190, 3300), V(-1, 8, 100), V(0.6f, 8, 100), V(-1.2f, 8, 100), null };
        var maxs = new List<float[]> { V(0, 8.9f, 180), V(9.1f, 8.91f, 180), V(0.5f, -9, 180), V(31, 191, 3301), V(-0.6f, 9, 101), V(1, 9, 101), V(-0.8f, 9, 101), null };
        var tris = new List<int> { 1475, 1348, 300, 120, 50, 52, 50, 0 };
        float centre = WorkshopRules.MirrorCentre(mins, maxs, 0);
        Assert.InRange(centre, -0.05f, 0.05f);   // voted by the pairs: the stray link at 30 has no partner and no vote
        Assert.Equal(1, WorkshopRules.FindMirror(mins, maxs, tris, 0, 0, centre, out bool self)); Assert.False(self);   // Object_14 -> Object_1782: different triangle counts, same box
        Assert.Equal(0, WorkshopRules.FindMirror(mins, maxs, tris, 1, 0, centre, out self));
        Assert.Equal(-1, WorkshopRules.FindMirror(mins, maxs, tris, 2, 0, centre, out self)); Assert.True(self);   // the keel is its own mirror
        Assert.Equal(-1, WorkshopRules.FindMirror(mins, maxs, tris, 3, 0, centre, out self)); Assert.False(self);   // the stray link has none
        Assert.Equal(5, WorkshopRules.FindMirror(mins, maxs, tris, 4, 0, centre, out self));   // a small fitting and its twin
        Assert.Equal(4, WorkshopRules.FindMirror(mins, maxs, tris, 5, 0, centre, out self));   // the near copy at index 6 sits 0.2 off — outside 3 % of a 1 m part
        Assert.Equal(-1, WorkshopRules.FindMirror(mins, maxs, tris, 6, 0, centre, out self));
        Assert.Equal(-1, WorkshopRules.FindMirror(mins, maxs, tris, 7, 0, centre, out self));  // unmeasured
        // exact duplicates: the closer triangle count breaks the tie
        var dmins = new List<float[]> { V(-2, 0, 0), V(1, 0, 0), V(1, 0, 0) }; var dmaxs = new List<float[]> { V(-1, 1, 1), V(2, 1, 1), V(2, 1, 1) };
        Assert.Equal(2, WorkshopRules.FindMirror(dmins, dmaxs, new List<int> { 100, 80, 99 }, 0, 0, 0f, out self));
        // a review's four parts: an exact pair at -5/+5, a keel at 0, one unmatched fitting at 30.5 — the median said 2.5
        var rmins = new List<float[]> { V(-6, 0, 0), V(4, 0, 0), V(-0.5f, -1, 0), V(30, 5, 0) }; var rmaxs = new List<float[]> { V(-4, 1, 3), V(6, 1, 3), V(0.5f, 0, 3), V(31, 6, 1) };
        float rc = WorkshopRules.MirrorCentre(rmins, rmaxs, 0);
        Assert.Equal(0f, rc, 3);
        Assert.Equal(1, WorkshopRules.FindMirror(rmins, rmaxs, new List<int> { 10, 10, 5, 3 }, 0, 0, rc, out self));
        Assert.Equal(-1, WorkshopRules.FindMirror(rmins, rmaxs, new List<int> { 10, 10, 5, 3 }, 2, 0, rc, out self)); Assert.True(self);
        // no two parts alike: the median still serves (a lone hull half beside its keel)
        Assert.Equal(1.5f, WorkshopRules.MirrorCentre(new List<float[]> { V(0, 0, 0), V(1, 0, 0) }, new List<float[]> { V(1, 1, 1), V(4, 2, 2) }, 0), 3);
        // an off-centre model along the other side axis
        var omins = new List<float[]> { V(0, 0, 4), V(0, 0, 12) }; var omaxs = new List<float[]> { V(1, 1, 8), V(1, 1, 16) };
        float oc = WorkshopRules.MirrorCentre(omins, omaxs, 2);
        Assert.Equal(10f, oc, 3);
        Assert.Equal(1, WorkshopRules.FindMirror(omins, omaxs, new List<int> { 1, 1 }, 0, 2, oc, out self));
    }

    [Fact]
    public void The_fuse_report_lists_every_island_when_given_them()
    {
        var g = new WorkshopRules.FuseGroupReport { Letter = "A", Changed = true, PartNames = new[] { "P" }, Warnings = new string[0],
            Details = new[] { "Fused 1 part(s)", "largest islands: a; b; c; d; e; f" }, Islands = new[] { "a", "b", "c", "d", "e", "f", "g" } };
        var lines = WorkshopRules.FuseReport("s.glb", "o.glb", 0, new[] { g }).Split('\n');
        Assert.Contains("islands (7, largest first):", lines);
        Assert.Contains("  g", lines);
        Assert.DoesNotContain("largest islands:", lines);   // the abbreviated line is replaced by the complete list
    }
}
