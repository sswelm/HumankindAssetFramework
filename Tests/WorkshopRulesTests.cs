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
}
