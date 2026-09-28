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
    public enum Outcome { Written, Conflict, Unresolved }

    /// <summary>
    /// Write <paramref name="text"/> to <paramref name="path"/> atomically (via a unique temp file), provided the file
    /// still holds <paramref name="readBefore"/> — the text the caller read and judged; null = the file did not exist.
    /// Conflict = somebody else wrote it in between and their version was restored. Unresolved = the restore failed;
    /// the active source may contain either version, so the caller must not claim that nothing was written.
    /// <paramref name="note"/> is non-null when something needs saying even so: a copy kept, and where — including
    /// copies earlier writes left, which may hold another editor's changes and are never deleted here.
    /// Throws when the write itself fails (a lock); the file is then left as it was.
    /// </summary>
    public static Outcome Write(string path, string readBefore, string text, out string note)
    {
        var notes = new List<string>();
        var outcome = WriteOnce(path, readBefore, text, notes);
        var left = Preserved(path);
        if (left.Length > 0)
            notes.Add("copies an interrupted or contested save kept beside it may hold another editor's changes — compare them with the source, then delete them: " +
                      string.Join(", ", left.Select(Path.GetFileName)));
        note = notes.Count > 0 ? string.Join("; ", notes) : null;
        return outcome;
    }

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
        File.WriteAllText(tmp, text);
        if (readBefore == null)
        {
            // Move refuses an existing destination, so a file that appeared since the read is never overwritten
            try { File.Move(tmp, path); return Outcome.Written; }
            catch (IOException) when (File.Exists(path)) { TryDelete(tmp); return Outcome.Conflict; }
            catch { TryDelete(tmp); throw; }
        }
        string displaced = Unique(path, "displaced");
        try { File.Replace(tmp, path, displaced); }
        catch
        {
            // ReplaceFile can fail AFTER renaming the original to the backup name (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2):
            // put it back before reporting the failure, or "the previous source is intact" would be untrue.
            if (!File.Exists(path) && File.Exists(displaced)) File.Move(displaced, path);
            TryDelete(tmp);   // only ever this write's own text
            throw;
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
}
