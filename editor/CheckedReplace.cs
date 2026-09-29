// CheckedReplace.cs — the registry source's write, checked by WHAT IT DISPLACED (outside review of PR #100, fifth
// round). Save reads and validates pack.json, then serializes and replaces it; another editor (a text editor, git, a
// sync tool, a second Unity window) replacing the file in between used to be overwritten unseen, and no check BEFORE
// the replace can close that: there is always an instant after it. So the write itself reports what it took the
// place of: File.Replace with a backup name moves the replaced file aside atomically, and if that is not the text the
// caller read, somebody else wrote in between — their version goes back in place and the write reports a conflict.
// No timing window for anything that lands before the replace: whatever landed is exactly what gets displaced.
// EVERY NAME IS UNIQUE, AND NOTHING THIS LEAVES IS EVER DELETED BY A LATER WRITE (sixth round): a displaced copy left
// by a write that stopped half way (the editor closed, the read of it failed) may be the only copy of the other
// writer's changes, and a fixed name that the next write cleared first would have deleted it. Leftovers are named
// on every write instead, until someone looks at them — the .corrupt-*.json convention. The temp file is unique too:
// two editors saving at once must not fill each other's.
// No Unity types: production compiles this in the editor assembly, Tests/HumankindAssetFramework.Tests.csproj
// compiles the same source Unity-free and drives it against real files (the QuadEstimate / EditorRules pattern).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class CheckedReplace
{
    // Unchanged: Apply only — the change found nothing to write (Write never returns it)
    public enum Outcome { Written, Conflict, Unresolved, Unchanged }

    /// <summary>
    /// READ, CHANGE, CHECKED WRITE — for a registry whose edits are OPERATIONS on one entry (a recipe added, replaced,
    /// removed), not snapshots of the whole file (outside review of PR #101). <paramref name="change"/> gets the file's
    /// current text (null = no file) and returns the new text, or null when there is nothing to write (it says why
    /// itself, if it refused). On a conflict the file is read again and the change applied to the OTHER writer's
    /// version, up to <paramref name="attempts"/> times, so an entry another writer added meanwhile is kept and this
    /// change still lands. A missing file is looked at twice, <paramref name="settleMs"/> apart, so an editor saving by
    /// rename usually has it back before the change sees it - but a wait PROVES nothing (review of PR #101, second
    /// round: a longer gap still read as "no file"). Whether a missing file may be created is the change's to decide,
    /// and ExistedBefore is the evidence. A failed read or change throws, and nothing of this change was written
    /// (after a conflict the file holds the other writer's version); the outcome of the last write is returned
    /// otherwise (Unresolved stops at once).
    /// </summary>
    public static Outcome Apply(string path, Func<string, string> change, int attempts, out string note, int settleMs = 250)
    {
        var notes = new List<string>();
        var outcome = Outcome.Conflict;
        for (int attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            if (!File.Exists(path) && settleMs > 0) System.Threading.Thread.Sleep(settleMs);
            string current = File.Exists(path) ? File.ReadAllText(path) : null;
            string next = change(current);
            if (next == null) { outcome = Outcome.Unchanged; break; }
            outcome = Write(path, current, next, out string n);
            if (n != null && !notes.Contains(n)) notes.Add(n);
            if (outcome != Outcome.Conflict) break;
        }
        note = notes.Count > 0 ? string.Join("; ", notes) : null;
        return outcome;
    }

    /// <summary>
    /// The file is missing, but Unity's .meta for it is still there: it EXISTED, and Unity has not seen it go - an editor
    /// saving by rename (the file is moved aside, then back), or a deletion outside Unity it has not refreshed yet. Not
    /// a new registry either way: creating one now could leave a one-entry file where the real one belongs (review of
    /// PR #101, second round). Unity removes the .meta of a file it saw deleted, so a deliberate deletion clears this.
    /// </summary>
    public static bool MissingButKnown(string path) => !File.Exists(path) && File.Exists(path + ".meta");

    /// <summary>
    /// Why a MISSING file is known to have existed, or null when nothing says so: its Unity .meta is still there, or git
    /// tracks it (review of PR #101, third round: a registry without a .meta, moved aside for longer than the settle,
    /// was still created over). Asks git, so it is for the write path only, never for a repaint. A deliberate deletion
    /// clears both: Unity removes the .meta of a file it saw go, and `git rm` takes it out of the index. When git can't
    /// be asked (not installed, no answer in time) that is no evidence either way, and only the .meta counts — the
    /// write path's caller asks the person in that case (the Prop Lab's "Create haf_props.json?").
    /// </summary>
    public static string ExistedBefore(string path)
    {
        if (File.Exists(path)) return null;
        if (File.Exists(path + ".meta")) return "Unity still has its .meta";
        return GitTracks(path) == true ? "git tracks it" : null;
    }

    /// <summary>
    /// Is <paramref name="path"/> in its git repository's index? true = yes; false = git ANSWERED no (not in the index,
    /// or no repository there, so nothing could have tracked it); null = git could not be asked or didn't answer in 5 s.
    /// Three answers, not two (review of PR #101, sixth round): "couldn't ask" is not "not tracked", and the window
    /// showed a tracked registry as an empty one while git was slow or down.
    /// </summary>
    public static bool? GitTracks(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"ls-files --error-unmatch -- \"{Path.GetFileName(full)}\"")
            {
                WorkingDirectory = Path.GetDirectoryName(full),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            // a git hook's environment points git at ITS repository, whatever the working directory says
            foreach (var v in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) psi.EnvironmentVariables.Remove(v);
            psi.EnvironmentVariables["LC_ALL"] = "C";   // "not a git repository" is read below; a translated git says it otherwise
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                p.StandardOutput.ReadToEndAsync();   // drain, so a full pipe can't hang it
                var err = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
                if (p.ExitCode == 0) return true;
                if (p.ExitCode == 1) return false;   // --error-unmatch: git answered, not in the index
                string said = err.Wait(1000) ? err.Result : "";
                if (said.IndexOf("not a git repository", StringComparison.OrdinalIgnoreCase) >= 0) return false;   // nothing there could track it
                return null;   // any other failure: git didn't say
            }
        }
        catch { return null; }   // git not installed, or couldn't start: it didn't say
    }

    /// <summary>
    /// Write <paramref name="text"/> to <paramref name="path"/> atomically (via a unique temp file), provided the file
    /// still holds <paramref name="readBefore"/> — the text the caller read and judged; null = the file did not exist.
    /// Conflict = somebody else wrote it in between and their version was restored. Unresolved = the restore failed;
    /// the active source may contain either version, so the caller must not claim that nothing was written.
    /// <paramref name="note"/> is non-null when something needs saying even so: a copy kept, and where — including
    /// copies earlier writes left, which may hold another editor's changes and are never deleted here.
    /// Throws ONLY when nothing was written and the file is as it was (a lock); every other end is an outcome.
    /// </summary>
    public static Outcome Write(string path, string readBefore, string text, out string note)
    {
        var notes = new List<string>();
        var outcome = WriteOnce(path, readBefore, text, notes);
        // AFTER the write: nothing from here may throw, or a committed write reads as a failed one (review of PR #101,
        // second round - a directory listing that failed turned a saved recipe into "not saved, the file is as it was")
        try
        {
            var left = ListPreserved(path);
            if (left.Length > 0)
                notes.Add("copies an interrupted or contested save kept beside it may hold another editor's changes — compare them with the source, then delete them: " +
                          string.Join(", ", left.Select(Path.GetFileName)));
        }
        catch (Exception e) { notes.Add($"could not look for copies earlier saves kept beside it ({e.Message})"); }
        note = notes.Count > 0 ? string.Join("; ", notes) : null;
        return outcome;
    }

    // Preserved, reachable by a test that makes the listing fail after a committed write
    internal static Func<string, string[]> ListPreserved = Preserved;

    /// <summary>The copies kept beside <paramref name="path"/> by writes that could not settle what they displaced.</summary>
    public static string[] Preserved(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)), name = Path.GetFileName(path);
        if (!Directory.Exists(dir)) return new string[0];
        return Directory.GetFiles(dir, name + ".displaced-*.json").Concat(Directory.GetFiles(dir, name + ".refused-*.json"))
                        .OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    static string Unique(string path, string kind) =>
        path + "." + kind + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json";

    static Outcome WriteOnce(string path, string readBefore, string text, List<string> notes)
    {
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";   // .tmp: Unity does not import it
        try { File.WriteAllText(tmp, text); }
        catch { TryDelete(tmp); throw; }   // a half-written temp is only ever this write's own text
        if (readBefore == null)
        {
            // Move refuses an existing destination, so a file that appeared since the read is never overwritten
            try { File.Move(tmp, path); return Outcome.Written; }
            catch (IOException) when (File.Exists(path)) { TryDelete(tmp); return Outcome.Conflict; }
            catch { TryDelete(tmp); throw; }
        }
        string displaced = Unique(path, "displaced");
        try { File.Replace(tmp, path, displaced); }
        catch (Exception e)
        {
            TryDelete(tmp);   // only ever this write's own text
            if (PutBackAfterFailedReplace(path, displaced, e, notes)) throw;   // as it was: a plain failed write
            return Outcome.Unresolved;                                         // it is NOT as it was - said, not thrown
        }
        // From here until it is settled, `displaced` may be the only copy of another writer's changes: it is deleted
        // only once it is PROVEN to be the text this caller read; if this stops half way, it stays (and is named).
        string was;
        try { was = File.ReadAllText(displaced); }
        catch (Exception e)
        {
            notes.Add($"could not check what this save replaced ({e.Message}); it is kept as '{Path.GetFileName(displaced)}'");
            return Outcome.Written;
        }
        if (was == readBefore) { TryDelete(displaced); return Outcome.Written; }

        // CONFLICT: the other writer's version goes back in place, and whatever that restore displaces is looked at too
        return RestoreDisplaced(path, displaced, text, notes);
    }

    // ReplaceFile can fail AFTER renaming the original to the backup name (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2): put it
    // back. true = the file is as it was, so the failure may be reported as a plain failed write; false = it is not
    // (the put-back failed too, or the file is gone), and the note says where it is. A separate step so a test can
    // make the put-back fail deterministically.
    internal static bool PutBackAfterFailedReplace(string path, string displaced, Exception why, List<string> notes)
    {
        if (File.Exists(path)) return true;
        if (!File.Exists(displaced))
        {
            notes.Add($"the write failed ({why.Message}) and the file is missing afterwards");
            return false;
        }
        try { File.Move(displaced, path); return true; }
        catch (Exception e)
        {
            notes.Add($"the write failed ({why.Message}) after moving the file aside, and it could not be put back ({e.Message}): " +
                      $"it is intact as '{Path.GetFileName(displaced)}' — rename it back to '{Path.GetFileName(path)}'");
            return false;
        }
    }

    // Kept as a separate step so a test can lock the active file after the first replace and exercise a failed
    // restore deterministically. A failed restore is not a settled conflict: the active source is unknown.
    internal static Outcome RestoreDisplaced(string path, string displaced, string text, List<string> notes)
    {
        string refused = Unique(path, "refused");
        try { File.Replace(displaced, path, refused); }
        catch (Exception e)
        {
            if (!File.Exists(path) && File.Exists(displaced)) { try { File.Move(displaced, path); } catch { } }
            notes.Add($"the other version could not be confirmed back in place ({e.Message}); inspect the source and any preserved copies before saving again");
            return Outcome.Unresolved;
        }
        string back = null;
        try { back = File.ReadAllText(refused); } catch { }
        if (back == text) TryDelete(refused);   // proven to be only this write's own text: nothing to keep
        else notes.Add($"yet another write landed while the other version was being put back; it is kept as '{Path.GetFileName(refused)}'");
        return Outcome.Conflict;
    }

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    /// <summary>
    /// ATOMIC create-if-absent for a folder (review of PR #102, sixth round): a private temp folder is renamed into
    /// place, and a rename fails when the name is taken — so two editors reserving the same name get exactly one winner.
    /// "Exists, then create" let both pick it, and one's refused remove then deleted the other's undo. true = the path is
    /// now this caller's folder, empty; false = the name was taken. Throws when the parent can't be written at all.
    /// </summary>
    public static bool TryReserveFolder(string path)
    {
        string full = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(full);
        Directory.CreateDirectory(parent);
        string temp = Path.Combine(parent, "_tmp_" + Guid.NewGuid().ToString("N"));   // "_tmp_": the Backup window lists no such folder
        Directory.CreateDirectory(temp);
        try { Directory.Move(temp, full); return true; }
        catch (IOException) when (Directory.Exists(full) || File.Exists(full)) { try { Directory.Delete(temp); } catch { } return false; }
        catch { try { Directory.Delete(temp); } catch { } throw; }
    }
}
