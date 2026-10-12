// GitStamp.cs — the commit a directory's git checkout is at, read from git's own files, no git process (2026-10-12).
// A golden's header names the editor commit it was captured at (BakeGoldenRules.Compose), so a moved count can be
// attributed to the commits since. The package lives in a plain checkout or in a worktree (whose .git is a FILE naming
// the gitdir, with refs in the common dir); either is read here. Never throws, never launches anything.
using System;
using System.IO;
using System.Linq;

public static class GitStamp
{
    /// The short (7) commit hash of the checkout containing `startDir`, or "unknown".
    public static string ShortHead(string startDir)
    {
        try
        {
            string dir = Path.GetFullPath(startDir);
            while (!string.IsNullOrEmpty(dir))
            {
                string dotGit = Path.Combine(dir, ".git");
                if (Directory.Exists(dotGit)) return Resolve(dotGit, dotGit);
                if (File.Exists(dotGit))
                {
                    string first = File.ReadLines(dotGit).FirstOrDefault() ?? "";
                    if (!first.StartsWith("gitdir:", StringComparison.Ordinal)) return "unknown";
                    string gitdir = first.Substring(7).Trim();
                    if (!Path.IsPathRooted(gitdir)) gitdir = Path.GetFullPath(Path.Combine(dir, gitdir));
                    string common = gitdir, commonFile = Path.Combine(gitdir, "commondir");
                    if (File.Exists(commonFile))
                    {
                        string c = (File.ReadLines(commonFile).FirstOrDefault() ?? "").Trim();
                        if (c.Length > 0) common = Path.IsPathRooted(c) ? c : Path.GetFullPath(Path.Combine(gitdir, c));
                    }
                    return Resolve(gitdir, common);
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        return "unknown";
    }

    static string Resolve(string gitdir, string common)
    {
        string head = (File.ReadLines(Path.Combine(gitdir, "HEAD")).FirstOrDefault() ?? "").Trim();
        if (!head.StartsWith("ref:", StringComparison.Ordinal)) return Short(head);
        string refName = head.Substring(4).Trim();
        foreach (string root in new[] { gitdir, common })
        {
            string refFile = Path.Combine(root, refName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refFile)) return Short((File.ReadLines(refFile).FirstOrDefault() ?? "").Trim());
        }
        string packed = Path.Combine(common, "packed-refs");
        if (File.Exists(packed))
            foreach (string line in File.ReadLines(packed))
                if (line.EndsWith(" " + refName, StringComparison.Ordinal) && line.IndexOf(' ') > 0)
                    return Short(line.Substring(0, line.IndexOf(' ')));
        return "unknown";
    }

    static string Short(string sha) => sha.Length >= 7 && sha.All(Uri.IsHexDigit) ? sha.Substring(0, 7).ToLowerInvariant() : "unknown";
}
