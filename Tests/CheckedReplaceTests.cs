using System;
using System.Collections.Generic;
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
    public void A_failed_conflict_restore_reports_an_unresolved_source_and_preserves_the_other_version()
    {
        // State immediately after the first replace: our C is active and the other editor's B was displaced.
        // A reader that opens C before the rollback blocks File.Replace on Windows.
        File.WriteAllText(P, "C");
        string displaced = P + ".displaced-20260928_223001-deadbeef.json";
        File.WriteAllText(displaced, "B");
        var notes = new List<string>();
        using (new FileStream(P, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(CheckedReplace.Outcome.Unresolved,
                         CheckedReplace.RestoreDisplaced(P, displaced, "C", notes));
        Assert.Equal("C", File.ReadAllText(P));
        Assert.Equal("B", File.ReadAllText(displaced));
        Assert.Contains(notes, n => n.Contains("could not be confirmed back in place"));
    }

    // ---- Apply: read, change, checked write (review of PR #101). The "other writer" writes INSIDE the change, i.e.
    // exactly between this apply's read and its replace - the race, made deterministic.

    void OtherWriterWrites(string text)
    {
        File.WriteAllText(P + ".other", text);
        if (File.Exists(P)) File.Replace(P + ".other", P, null); else File.Move(P + ".other", P);
    }

    [Fact]
    public void An_entry_another_writer_added_between_the_read_and_the_write_is_kept_and_the_change_still_lands()
    {
        File.WriteAllText(P, "a");
        int calls = 0;
        var outcome = CheckedReplace.Apply(P, text =>
        {
            if (calls++ == 0) OtherWriterWrites("a,b");   // another editor adds b after this apply read "a"
            return text + ",c";                          // this apply's operation: add c
        }, 3, out var note, settleMs: 0);
        Assert.Equal(CheckedReplace.Outcome.Written, outcome);
        Assert.Equal("a,b,c", File.ReadAllText(P));       // b kept, c applied to b's version
        Assert.Equal(2, calls);
        Assert.Null(note);
        Assert.Empty(Beside());
    }

    [Fact]
    public void A_file_that_appears_while_the_apply_creates_it_is_applied_to_not_overwritten()
    {
        // the save-by-rename gap: the file was missing when read, and back before the write
        int calls = 0;
        var outcome = CheckedReplace.Apply(P, text =>
        {
            if (calls++ == 0) { Assert.Null(text); OtherWriterWrites("a,b"); }
            return (text ?? "") + ",c";
        }, 3, out _, settleMs: 0);
        Assert.Equal(CheckedReplace.Outcome.Written, outcome);
        Assert.Equal("a,b,c", File.ReadAllText(P));
        Assert.Empty(Beside());
    }

    [Fact]
    public void A_missing_file_is_looked_at_twice_before_it_counts_as_absent()
    {
        // the other editor's rename lands during the settle: the change sees the file, not "no file"
        var back = new System.Threading.Thread(() => { System.Threading.Thread.Sleep(50); OtherWriterWrites("a,b"); });
        back.Start();
        string seen = "<not called>";
        var outcome = CheckedReplace.Apply(P, text => { seen = text; return text + ",c"; }, 3, out _, settleMs: 500);
        back.Join();
        Assert.Equal("a,b", seen);
        Assert.Equal(CheckedReplace.Outcome.Written, outcome);
        Assert.Equal("a,b,c", File.ReadAllText(P));
    }

    [Fact]
    public void A_file_that_keeps_changing_is_left_to_the_other_writer_after_the_attempts()
    {
        File.WriteAllText(P, "a");
        int calls = 0;
        var outcome = CheckedReplace.Apply(P, text => { OtherWriterWrites("other" + (++calls)); return "mine"; }, 3, out _, settleMs: 0);
        Assert.Equal(CheckedReplace.Outcome.Conflict, outcome);
        Assert.Equal(3, calls);
        Assert.Equal("other3", File.ReadAllText(P));
        Assert.Empty(Beside());
    }

    [Fact]
    public void A_change_with_nothing_to_write_leaves_the_file_alone()
    {
        File.WriteAllText(P, "a");
        var before = File.GetLastWriteTimeUtc(P);
        Assert.Equal(CheckedReplace.Outcome.Unchanged, CheckedReplace.Apply(P, _ => null, 3, out var note, settleMs: 0));
        Assert.Equal("a", File.ReadAllText(P));
        Assert.Equal(before, File.GetLastWriteTimeUtc(P));
        Assert.Null(note);
    }

    [Fact]
    public void A_change_that_throws_leaves_the_file_as_it_was()
    {
        File.WriteAllText(P, "a");
        Assert.Throws<InvalidOperationException>(() => CheckedReplace.Apply(P, _ => throw new InvalidOperationException(), 3, out _, settleMs: 0));
        Assert.Equal("a", File.ReadAllText(P));
        Assert.Empty(Beside());
    }

    // ---- second round of the PR #101 review: every end of a write says what is on disk

    [Fact]
    public void A_missing_file_whose_unity_meta_remains_is_known_not_new()
    {
        Assert.False(CheckedReplace.MissingButKnown(P));          // no file, no .meta: a registry that really is new
        File.WriteAllText(P + ".meta", "guid: x");
        Assert.True(CheckedReplace.MissingButKnown(P));           // moved aside by another program, or deleted outside Unity
        File.WriteAllText(P, "a");
        Assert.False(CheckedReplace.MissingButKnown(P));          // present: not missing at all
    }

    [Fact]
    public void A_committed_write_is_reported_written_even_when_listing_the_kept_copies_fails()
    {
        File.WriteAllText(P, "A");
        var list = CheckedReplace.ListPreserved;
        try
        {
            CheckedReplace.ListPreserved = _ => throw new UnauthorizedAccessException("listing denied");
            Assert.Equal(CheckedReplace.Outcome.Written, CheckedReplace.Write(P, "A", "C", out var note));
            Assert.Equal("C", File.ReadAllText(P));
            Assert.Contains("listing denied", note);
        }
        finally { CheckedReplace.ListPreserved = list; }
        Assert.Empty(Beside());
    }

    [Fact]
    public void A_replace_that_moved_the_file_aside_and_could_not_put_it_back_is_not_reported_as_it_was()
    {
        // the state after ReplaceFile failed half way: the original sits under the backup name. A directory where the
        // file belongs makes the put-back fail deterministically.
        string displaced = P + ".displaced-20260929_010000-deadbeef.json";
        File.WriteAllText(displaced, "A, the original");
        Directory.CreateDirectory(P);
        var notes = new List<string>();
        Assert.False(CheckedReplace.PutBackAfterFailedReplace(P, displaced, new IOException("replace failed"), notes));
        Assert.Equal("A, the original", File.ReadAllText(displaced));   // intact, and named
        Assert.Contains(notes, n => n.Contains(Path.GetFileName(displaced)) && n.Contains("rename it back"));
        Directory.Delete(P);

        // and when the put-back works, it IS as it was
        notes.Clear();
        Assert.True(CheckedReplace.PutBackAfterFailedReplace(P, displaced, new IOException("replace failed"), notes));
        Assert.Equal("A, the original", File.ReadAllText(P));
        Assert.Empty(notes);
        Assert.Empty(Beside());
    }

    // ---- third round: a registry without a .meta is still known when git tracks it

    void Git(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var v in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) psi.EnvironmentVariables.Remove(v);   // the push hook sets them
        using (var proc = System.Diagnostics.Process.Start(psi))
        {
            string err = proc.StandardError.ReadToEnd(); proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, "git " + args + ": " + err);
        }
    }

    [Fact]
    public void A_missing_file_git_tracks_existed_even_without_a_meta_and_git_rm_clears_it()
    {
        Git("init -q");
        File.WriteAllText(P, "{ \"props\": [] }");
        Assert.Null(CheckedReplace.ExistedBefore(P));                       // present: not missing
        Assert.Equal((bool?)false, CheckedReplace.GitTracks(P));            // untracked: git ANSWERED no (exit 1)
        Git("add pack.json");
        Assert.Equal((bool?)true, CheckedReplace.GitTracks(P));
        File.Delete(P);                                                     // moved aside by another program (no .meta at all)
        Assert.Equal("git tracks it", CheckedReplace.ExistedBefore(P));
        File.WriteAllText(P + ".meta", "guid: x");
        Assert.Equal("Unity still has its .meta", CheckedReplace.ExistedBefore(P));   // the cheaper evidence first
        File.Delete(P + ".meta");
        Git("rm -q --cached pack.json");                                    // a deliberate deletion, staged
        Assert.Null(CheckedReplace.ExistedBefore(P));                       // nothing says it existed: a bake may create it
    }

    [Fact]
    public void Outside_a_repository_git_answers_no_rather_than_not_knowing()
    {
        // the temp folder is in no repository: git exits 128 "not a git repository" - nothing there could have tracked
        // the file, which is an answer (false), not a failure to ask (null, sixth round)
        Assert.Equal((bool?)false, CheckedReplace.GitTracks(P));
        Assert.Null(CheckedReplace.ExistedBefore(P));
    }

    [Fact]
    public void The_committed_text_is_read_without_touching_the_file_on_disk()
    {
        // critical review 2026-09-30: "Restore last commit" checked the file out and validated AFTERWARDS, so a refused
        // candidate had already replaced the working copy. Reading the committed text leaves the working copy alone.
        Git("init -q");
        File.WriteAllText(P, "{ \"districts\": [ { \"district\": \"A\" } ] }");
        Git("add pack.json");
        Git("-c user.name=t -c user.email=t@t -c commit.gpgsign=false commit -q -m first");
        File.WriteAllText(P, "<<<<<<< the working copy, broken");
        Assert.Equal("{ \"districts\": [ { \"district\": \"A\" } ] }", CheckedReplace.GitCommittedText(P, out var error));
        Assert.Null(error);
        Assert.Equal("<<<<<<< the working copy, broken", File.ReadAllText(P));   // untouched
    }

    [Fact]
    public void A_file_with_no_committed_version_says_so_and_touches_nothing()
    {
        Assert.Null(CheckedReplace.GitCommittedText(P, out var outside));   // no repository at all
        Assert.False(string.IsNullOrEmpty(outside));
        Git("init -q");
        File.WriteAllText(P, "never committed");
        Assert.Null(CheckedReplace.GitCommittedText(P, out var uncommitted));   // a repository, but no commit of it
        Assert.False(string.IsNullOrEmpty(uncommitted));
        Assert.Equal("never committed", File.ReadAllText(P));
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
