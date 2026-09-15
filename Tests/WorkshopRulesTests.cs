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
        var got = WorkshopRules.ResolveFuseSidecar(new[] { WorkshopRules.SidecarLine("A", "Panel", 3) }, rows, refused);
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
        // review of 0a8b56e: "A|Hull|Port|3" was read as name "Hull" and marked the part called Hull
        var rows = Rows((1, "Hull"), (3, "Hull|Port"), (4, "Deck|1"));
        var refused = new List<string>();
        var got = WorkshopRules.ResolveFuseSidecar(new[] { WorkshopRules.SidecarLine("A", "Hull|Port", 3) }, rows, refused);
        Assert.Equal(new Dictionary<int, string> { [3] = "A" }, got);   // by index, the name read from between the bars; "Hull" untouched
        got = WorkshopRules.ResolveFuseSidecar(new[] { "B|Hull|Port" }, rows, refused);   // a legacy line: the whole remainder is the name
        Assert.Equal(new Dictionary<int, string> { [3] = "B" }, got);
        Assert.Empty(refused);
        // "C|Deck|1": index 1 is "Hull", so not by index; the whole remainder "Deck|1" is a unique row → that one
        got = WorkshopRules.ResolveFuseSidecar(new[] { "C|Deck|1" }, rows, refused);
        Assert.Equal(new Dictionary<int, string> { [4] = "C" }, got);
        Assert.Empty(refused);
        // a line that fits two different parts is refused
        var two = Rows((3, "Hull"), (7, "Hull|3"));
        got = WorkshopRules.ResolveFuseSidecar(new[] { "A|Hull|3" }, two, refused);
        Assert.Empty(got); Assert.Single(refused); Assert.Contains("fits both", refused[0]);
    }

    [Fact]
    public void Malformed_lines_and_bad_letters_are_ignored()
    {
        var rows = Rows((1, "Hull"));
        var got = WorkshopRules.ResolveFuseSidecar(new[] { "", "no bar", "Z|Hull|1", "AB|Hull|1", "A||1", " a|Hull|1 " }, rows, null);
        Assert.Empty(got);
        Assert.Equal("A|Hull|1", WorkshopRules.SidecarLine("A", "Hull", 1));
    }
}
