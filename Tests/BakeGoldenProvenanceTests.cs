using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

// GOLDEN PROVENANCE (editor/EditorRules.cs: BakeGoldenRules, 2026-10-12): a golden carries a header - capture time,
// editor commit, input hashes, a checksum of its lines - and Judge reads it before the lines. The 2026-10-10 hand
// edit of eleven goldens to the failing run's values is the case these hold.
public class BakeGoldenProvenanceTests
{
    static readonly string[] Body = { "SOURCE parts=3 tris=10", "FUSE parts=2 tris=10->10 verts=9->8 islands=2->2 rewound=4" };
    static readonly Dictionary<string, string> Inputs = new Dictionary<string, string> { ["source"] = "0123456789abcdef" };

    [Fact]
    public void A_captured_golden_starts_with_one_header_line_that_parses_back()
    {
        var file = BakeGoldenRules.Compose(Body, new DateTime(2026, 10, 12, 14, 3, 0, DateTimeKind.Utc), "1b5f9c5", Inputs);
        Assert.Equal(3, file.Length);
        Assert.StartsWith(BakeGoldenRules.HeaderMark, file[0]);
        Assert.Equal(Body, file.Skip(1));
        var h = BakeGoldenRules.ParseHeader(file);
        Assert.NotNull(h);
        Assert.Equal("2026-10-12 14:03", h.Captured);
        Assert.Equal("1b5f9c5", h.Editor);
        Assert.Equal("0123456789abcdef", h.Inputs["source"]);
        Assert.Equal(BakeGoldenRules.Checksum(Body), h.Sum);
        // no editor known, no inputs: still a header, still parseable
        var bare = BakeGoldenRules.Compose(Body, DateTime.UtcNow, "", null);
        Assert.Equal("unknown", BakeGoldenRules.ParseHeader(bare).Editor);
        Assert.Empty(BakeGoldenRules.ParseHeader(bare).Inputs);
        // a legacy golden has no header
        Assert.Null(BakeGoldenRules.ParseHeader(Body));
    }

    [Fact]
    public void The_checksum_reads_the_lines_not_the_header_whitespace_or_empty_lines()
    {
        string sum = BakeGoldenRules.Checksum(Body);
        Assert.Equal(16, sum.Length);
        Assert.Equal(sum, BakeGoldenRules.Checksum(new[] { Body[0] + "   ", "", Body[1] + "\r" }));
        Assert.Equal(sum, BakeGoldenRules.Checksum(BakeGoldenRules.Compose(Body, DateTime.UtcNow, "x", null)));   // the header is not part of its own sum
        Assert.NotEqual(sum, BakeGoldenRules.Checksum(new[] { Body[0], Body[1].Replace("rewound=4", "rewound=5") }));
        Assert.NotEqual(sum, BakeGoldenRules.Checksum(new[] { Body[1], Body[0] }));   // order counts
    }

    [Fact]
    public void A_run_that_matches_is_a_match_and_a_legacy_golden_says_so()
    {
        var file = BakeGoldenRules.Compose(Body, DateTime.UtcNow, "1b5f9c5", Inputs);
        Assert.Equal(BakeGoldenRules.GoldenVerdict.Match, BakeGoldenRules.Judge(file, Body, Inputs, out string reason, out bool legacy));
        Assert.Null(reason); Assert.False(legacy);
        Assert.Equal(BakeGoldenRules.GoldenVerdict.Match, BakeGoldenRules.Judge(Body, Body, Inputs, out _, out legacy));
        Assert.True(legacy);
    }

    [Fact]
    public void A_golden_edited_outside_a_run_is_named_as_such_before_any_line_is_compared()
    {
        var file = BakeGoldenRules.Compose(Body, new DateTime(2026, 10, 10, 20, 11, 0, DateTimeKind.Utc), "ae20209", Inputs);
        file[2] = file[2].Replace("rewound=4", "rewound=3");   // the 2026-10-10 edit: the failing run's value written over the golden
        // even a run that now PRODUCES the edited value does not match an edited golden
        var edited = new[] { Body[0], file[2] };
        Assert.Equal(BakeGoldenRules.GoldenVerdict.Edited, BakeGoldenRules.Judge(file, edited, Inputs, out string reason, out _));
        Assert.Contains("edited outside a run", reason);
        Assert.Contains("captured 2026-10-10 20:11 at editor ae20209", reason);
        Assert.Contains("delete it and run again to re-bless", reason);
    }

    [Fact]
    public void A_changed_result_names_the_first_difference_and_the_capture_stamp()
    {
        var file = BakeGoldenRules.Compose(Body, new DateTime(2026, 9, 25, 16, 1, 0, DateTimeKind.Utc), "11951dd", Inputs);
        var got = new[] { Body[0], Body[1].Replace("rewound=4", "rewound=2") };
        Assert.Equal(BakeGoldenRules.GoldenVerdict.Changed, BakeGoldenRules.Judge(file, got, Inputs, out string reason, out bool legacy));
        Assert.False(legacy);
        Assert.StartsWith("line 2: expected 'FUSE parts=2 tris=10->10 verts=9->8 islands=2->2 rewound=4' but got 'FUSE parts=2 tris=10->10 verts=9->8 islands=2->2 rewound=2'", reason);
        Assert.Contains("(golden captured 2026-09-25 16:01 at editor 11951dd)", reason);
        // the same against a legacy golden: the difference, and that there is no stamp to attribute it to
        Assert.Equal(BakeGoldenRules.GoldenVerdict.Changed, BakeGoldenRules.Judge(Body, got, Inputs, out reason, out legacy));
        Assert.True(legacy);
        Assert.Contains("a legacy golden: no capture stamp", reason);
    }

    [Fact]
    public void A_source_that_moved_under_the_golden_is_stale_by_input_not_by_code()
    {
        var file = BakeGoldenRules.Compose(Body, DateTime.UtcNow, "11951dd", Inputs);
        var got = new[] { Body[0], Body[1].Replace("rewound=4", "rewound=2") };
        var now = new Dictionary<string, string> { ["source"] = "fedcba9876543210" };
        Assert.Equal(BakeGoldenRules.GoldenVerdict.InputChanged, BakeGoldenRules.Judge(file, got, now, out string reason, out _));
        Assert.Contains("the source changed since the golden was captured (0123456789abcdef then, fedcba9876543210 now)", reason);
        Assert.Contains("stale by input, not by code", reason);
        // the same input, the same result: a match, whatever else is in the header
        Assert.Equal(BakeGoldenRules.GoldenVerdict.Match, BakeGoldenRules.Judge(file, Body, now, out _, out _));
        // an input the golden never recorded ("-") cannot be the reason
        var noInput = BakeGoldenRules.Compose(Body, DateTime.UtcNow, "11951dd", new Dictionary<string, string> { ["source"] = null });
        Assert.Equal(BakeGoldenRules.GoldenVerdict.Changed, BakeGoldenRules.Judge(noInput, got, now, out _, out _));
    }

    [Fact]
    public void The_line_diff_ignores_header_and_comment_lines_on_either_side()
    {
        var file = BakeGoldenRules.Compose(Body, DateTime.UtcNow, "x", null);
        Assert.Null(BakeGoldenRules.Diff(BakeGoldenRules.Body(file), Body));
        Assert.Equal(Body, BakeGoldenRules.Body(new[] { "# a comment", Body[0], "  # another", Body[1] }));
    }

    [Fact]
    public void An_input_hash_is_sixteen_hex_digits_of_the_bytes_and_a_dash_for_none()
    {
        string a = BakeGoldenRules.InputHash(new byte[] { 1, 2, 3 });
        Assert.Equal(16, a.Length);
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.Equal(a, BakeGoldenRules.InputHash(new byte[] { 1, 2, 3 }));
        Assert.NotEqual(a, BakeGoldenRules.InputHash(new byte[] { 1, 2, 4 }));
        Assert.Equal("-", BakeGoldenRules.InputHash(null));
    }
}
