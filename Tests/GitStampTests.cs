using System;
using System.IO;
using System.Linq;
using Xunit;

// GitStamp (editor/GitStamp.cs): the commit a checkout is at, from git's files alone - a plain .git directory, a
// worktree's .git FILE with its common dir, packed refs, a detached HEAD, and no git at all. A golden's header
// names this commit (BakeGoldenRules.Compose), so a moved count is attributed to the commits since.
public class GitStampTests
{
    sealed class Temp : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "haf-gitstamp-" + Guid.NewGuid().ToString("N"));
        public Temp() { Directory.CreateDirectory(Dir); }
        public string Sub(params string[] p) { string d = Path.Combine(new[] { Dir }.Concat(p).ToArray()); Directory.CreateDirectory(d); return d; }
        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public void A_plain_checkout_reads_HEAD_then_the_branch_ref()
    {
        using (var t = new Temp())
        {
            string git = t.Sub(".git"); t.Sub(".git", "refs", "heads");
            File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/master\n");
            File.WriteAllText(Path.Combine(git, "refs", "heads", "master"), "1b5f9c5e0d4c3b2a1908f7e6d5c4b3a291807f6e\n");
            Assert.Equal("1b5f9c5", GitStamp.ShortHead(t.Sub("editor")));   // from a subdirectory, walking up
            Assert.Equal("1b5f9c5", GitStamp.ShortHead(t.Dir));
        }
    }

    [Fact]
    public void A_packed_ref_and_a_detached_head_are_read_too()
    {
        using (var t = new Temp())
        {
            string git = t.Sub(".git");
            File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/fix/x\n");
            File.WriteAllText(Path.Combine(git, "packed-refs"), "# pack-refs with: peeled fully-peeled sorted \nabcdef0123456789abcdef0123456789abcdef01 refs/heads/master\n0123456789abcdef0123456789abcdef01234567 refs/heads/fix/x\n");
            Assert.Equal("0123456", GitStamp.ShortHead(t.Dir));
            File.WriteAllText(Path.Combine(git, "HEAD"), "fedcba9876543210fedcba9876543210fedcba98\n");
            Assert.Equal("fedcba9", GitStamp.ShortHead(t.Dir));
        }
    }

    [Fact]
    public void A_worktree_reads_its_gitdir_file_and_the_common_dirs_refs()
    {
        using (var t = new Temp())
        {
            string main = t.Sub("main", ".git"); t.Sub("main", ".git", "refs", "heads", "fix");
            File.WriteAllText(Path.Combine(main, "refs", "heads", "fix", "y"), "9999999aaaaaaa000000000000000000000000ff\n");
            string wtGit = t.Sub("main", ".git", "worktrees", "y");
            File.WriteAllText(Path.Combine(wtGit, "HEAD"), "ref: refs/heads/fix/y\n");
            File.WriteAllText(Path.Combine(wtGit, "commondir"), "../..\n");
            string wt = t.Sub("wt-y");
            File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + wtGit + "\n");
            Assert.Equal("9999999", GitStamp.ShortHead(t.Sub("wt-y", "editor")));
        }
    }

    [Fact]
    public void No_git_or_a_broken_one_is_unknown_never_a_throw()
    {
        using (var t = new Temp())
        {
            Assert.Equal("unknown", GitStamp.ShortHead(t.Sub("plain")));
            string git = t.Sub("broken", ".git");
            File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/nowhere\n");
            Assert.Equal("unknown", GitStamp.ShortHead(t.Sub("broken")));
            File.WriteAllText(Path.Combine(t.Sub("file"), ".git"), "not a gitdir line\n");
            Assert.Equal("unknown", GitStamp.ShortHead(t.Sub("file")));
            Assert.Equal("unknown", GitStamp.ShortHead(Path.Combine(t.Dir, "does-not-exist")));
        }
    }
}
