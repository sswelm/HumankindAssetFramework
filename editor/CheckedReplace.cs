// CheckedReplace.cs — the registry source's write, checked by WHAT IT DISPLACED (outside review of PR #100, fifth
// round). Save reads and validates pack.json, then serializes and replaces it; another editor (a text editor, git, a
// sync tool, a second Unity window) replacing the file in between used to be overwritten unseen, and no check BEFORE
// the replace can close that: there is always an instant after it. So the write itself reports what it took the
// place of: File.Replace with a backup name moves the replaced file aside atomically, and if that is not the text the
// caller read, somebody else wrote in between — their version goes back in place and the write reports a conflict.
// No timing window for anything that lands before the replace: whatever landed is exactly what gets displaced.
// No Unity types: production compiles this in the editor assembly, Tests/HumankindAssetFramework.Tests.csproj
// compiles the same source Unity-free and drives it against real files (the QuadEstimate / EditorRules pattern).
using System;
using System.IO;

public static class CheckedReplace
{
    public enum Outcome { Written, Conflict }

    /// <summary>
    /// Write <paramref name="text"/> to <paramref name="path"/> atomically (via path.tmp), provided the file still holds
    /// <paramref name="readBefore"/> — the text the caller read and judged; null = the file did not exist then.
    /// Conflict = somebody else wrote it in between: their version is back in place and nothing of this write remains.
    /// <paramref name="note"/> is non-null when something needs saying even so (a leftover copy kept, and where).
    /// Throws when the write itself fails (a lock); the file is then left as it was.
    /// </summary>
    public static Outcome Write(string path, string readBefore, string text, out string note)
    {
        note = null;
        string tmp = path + ".tmp", displaced = path + ".displaced", refused = path + ".refused";
        File.WriteAllText(tmp, text);
        if (readBefore == null)
        {
            // Move refuses an existing destination, so a file that appeared since the read is never overwritten
            try { File.Move(tmp, path); return Outcome.Written; }
            catch (IOException) when (File.Exists(path)) { TryDelete(tmp); return Outcome.Conflict; }
        }
        TryDelete(displaced);   // a leftover from an interrupted earlier write — Replace needs the name free
        try { File.Replace(tmp, path, displaced); }
        catch
        {
            // ReplaceFile can fail AFTER renaming the original to the backup name (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2):
            // put it back before reporting the failure, or "the previous source is intact" would be untrue.
            if (!File.Exists(path) && File.Exists(displaced)) File.Move(displaced, path);
            TryDelete(tmp);
            throw;
        }
        string was;
        try { was = File.ReadAllText(displaced); }
        catch (Exception e)
        {
            // can't tell what was replaced: the write stands, and the displaced file is kept - nothing is lost
            note = $"could not check what this save replaced ({e.Message}); it is kept as '{displaced}'";
            return Outcome.Written;
        }
        if (was == readBefore) { TryDelete(displaced); return Outcome.Written; }

        // CONFLICT: the other writer's version goes back in place, and whatever that restore displaces is looked at too
        TryDelete(refused);
        File.Replace(displaced, path, refused);
        string back = null;
        try { back = File.ReadAllText(refused); } catch { }
        if (back == text) TryDelete(refused);   // only this write was displaced: nothing to keep
        else note = $"yet another write landed while the other version was being put back; it is kept as '{refused}'";
        return Outcome.Conflict;
    }

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
