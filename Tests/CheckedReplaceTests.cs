using System;
using System.IO;
using System.Linq;
using Xunit;

// The registry source's write, checked by what it displaced (outside review of PR #100, fifth and sixth rounds): a
// file another editor replaced between Save's read and its replace used to be overwritten unseen, and a copy kept by
// an interrupted write must never be deleted by the next. Driven against real files.
public class CheckedReplaceTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "haf_checkedreplace_" + Guid.NewGuid().ToString("N"));
    string P => Path.Combine(dir, "pack.json");

    public CheckedReplaceTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    string[] Beside() => Directory.GetFiles(dir).Select(Path.GetFileName).Where(n => n != "pack.json").OrderBy(n => n).ToArray();

    [Fact]
    public void An_unchanged_file_is_replaced_and_nothing_is_left_beside_it()
    {
        File.WriteAllText(P, "A");
        string read = File.ReadAllText(P);
        Assert.Equal(CheckedReplace.Outcome.Written, CheckedReplace.Write(P, read, "C", out var note));
        Assert.Equal("C", File.ReadAllText(P));
        Assert.Null(note);
        Assert.Empty(Beside());
    }

    [Fact]
    public void A_file_replaced_after_the_read_is_not_overwritten_and_the_other_version_stays()
    {
        // the reported race: Save read A and judged it; another editor replaced it with B; Save now writes C
        File.WriteAllText(P, "A");
        string read = File.ReadAllText(P);
        File.WriteAllText(P + ".other", "B");
        File.Replace(P + ".other", P, null);   // the other editor's save-by-replace
        Assert.Equal(CheckedReplace.Outcome.Conflict, CheckedReplace.Write(P, read, "C", out var note));
        Assert.Equal("B", File.ReadAllText(P));
        Assert.Null(note);
        Assert.Empty(Beside());
    }

    [Fact]
    public void A_file_that_appeared_after_a_read_found_none_is_not_overwritten()
    {
        Assert.Equal(CheckedReplace.Outcome.Written, CheckedReplace.Write(P, null, "C", out _));   // still absent: created
        Assert.Equal("C", File.ReadAllText(P));
        File.Delete(P);
        // read found none; then somebody created it
        File.WriteAllText(P, "B");
        Assert.Equal(CheckedReplace.Outcome.Conflict, CheckedReplace.Write(P, null, "C", out _));
        Assert.Equal("B", File.ReadAllText(P));
        Assert.Empty(Beside());
    }

    [Fact]
    public void A_copy_an_interrupted_write_kept_survives_the_next_write_and_is_named()
    {
        // sixth round: the editor stopped after the replace but before the comparison, so the displaced copy may be the
        // only copy of another writer's changes. The next write used to clear that (fixed) name first.
        File.WriteAllText(P, "C0");
        string kept = P + ".displaced-20260928_223000-deadbeef.json";
        File.WriteAllText(kept, "B, the other editor's changes");
        Assert.Equal(CheckedReplace.Outcome.Written, CheckedReplace.Write(P, "C0", "C1", out var note));
        Assert.Equal("C1", File.ReadAllText(P));
        Assert.Equal("B, the other editor's changes", File.ReadAllText(kept));   // never deleted by a later write
        Assert.Contains(Path.GetFileName(kept), note);                           // and said, on every write, until someone looks
        Assert.Equal(new[] { Path.GetFileName(kept) }, Beside());
        Assert.Equal(new[] { kept }, CheckedReplace.Preserved(P));
    }

    [Fact]
    public void A_write_that_fails_leaves_the_file_as_it_was()
    {
        // a plain reader (share Read) blocks the replace — measured earlier in this PR
        File.WriteAllText(P, "A");
        using (new FileStream(P, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => CheckedReplace.Write(P, "A", "C", out _));
        Assert.Equal("A", File.ReadAllText(P));
        Assert.Empty(Beside());
    }
}
