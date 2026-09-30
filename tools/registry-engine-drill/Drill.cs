// Drill of the REAL SingleSourceRegistry engine: one scenario per row of the exit table.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;

[Serializable] public class DrillEntry { public string key = ""; public int value; }
[Serializable] public class DrillFile { public List<DrillEntry> items = new List<DrillEntry>(); }

static class Drill
{
    static int fails;
    static string root, src, art;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) { fails++; foreach (var l in UnityEngine.Debug.Lines.Skip(Math.Max(0, UnityEngine.Debug.Lines.Count - 4))) Console.WriteLine("       log: " + (l.Length > 180 ? l.Substring(0, 180) : l)); } }

    static SingleSourceRegistry<DrillFile> Fresh()
    {
        root = Path.Combine(Path.GetTempPath(), "haf_ssrdrill_" + Guid.NewGuid().ToString("N"));
        src = Path.Combine(root, "Assets", "Databases", "reg.backup.json");
        art = Path.Combine(root, "config", "reg.json");
        Directory.CreateDirectory(Path.GetDirectoryName(src)); Directory.CreateDirectory(Path.GetDirectoryName(art));
        EditorPrefs.P.Clear(); UnityEngine.Debug.Lines.Clear(); EditorApplication.timeSinceStartup = 1000;
        EditorPrefs.SetBool("drill.migrated", true);   // the one-time migration is its own concern
        return new SingleSourceRegistry<DrillFile>("[Drill]", () => src, () => art, f => f?.items?.Count ?? 0,
                                                   "drill.migrated", "Assets/Databases/reg.backup.json", "entries", "items");
    }

    static string Reg(params string[] keys) => UnityEngine.JsonUtility.ToJson(new DrillFile { items = keys.Select(k => new DrillEntry { key = k }).ToList() }, true);
    static string[] Keys(string path) => UnityEngine.JsonUtility.FromJson<DrillFile>(File.ReadAllText(path)).items.Select(e => e.key).OrderBy(k => k).ToArray();
    static Func<DrillFile, bool> Add(string k) => f => { f.items.Add(new DrillEntry { key = k }); return true; };

    static void Git(string args)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = Path.GetDirectoryName(src), UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var v in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) psi.EnvironmentVariables.Remove(v);
        using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); }
    }

    static int Main()
    {
        bool changed;
        var S = RegistryRules.SaveOutcome.Saved; var R = RegistryRules.SaveOutcome.Refused;

        // D1 `{}` source, deploy holds 3: Load is a FAILURE (corrupt), a change refuses, both copies untouched
        var reg = Fresh(); File.WriteAllText(src, "{}"); File.WriteAllText(art, Reg("a", "b", "c"));
        var loaded = reg.Load();
        Check(loaded.items.Count == 0 && reg.LastLoadCorrupt && reg.LastLoadFailed, "D1 `{}` loads as CORRUPT, not as an empty registry");
        Check(Directory.GetFiles(Path.GetDirectoryName(src), "*.corrupt-*.json").Length == 1, "D1 the broken source is preserved");
        Check(reg.Change(Add("x"), "drill", out changed) == R && File.ReadAllText(src) == "{}" && Keys(art).Length == 3, "D1 a change refuses; source and deploy untouched");

        // D2 0-byte and garbage sources are broken too
        reg = Fresh(); File.WriteAllText(src, ""); reg.Load();
        Check(reg.LastLoadCorrupt, "D2 a 0-byte source is corrupt, not empty");
        reg = Fresh(); File.WriteAllText(src, "{ \"wrong\": [] }"); reg.Load();
        Check(reg.LastLoadCorrupt, "D2 a source with the wrong key is corrupt, not empty");

        // D3 a change reaching a `{}` source WITHOUT a prior Load: judged by its own read
        reg = Fresh(); File.WriteAllText(src, "{}"); File.WriteAllText(art, Reg("a"));
        Check(reg.Change(Add("x"), "drill", out changed) == R && File.ReadAllText(src) == "{}" && reg.LastLoadCorrupt, "D3 a change's own read refuses `{}` and raises the recovery banner");

        // D4 hand-emptied source (valid, empty), deploy holds 3: refused
        reg = Fresh(); File.WriteAllText(src, Reg()); File.WriteAllText(art, Reg("a", "b", "c"));
        Check(reg.Change(Add("x"), "drill", out changed) == R && Keys(src).Length == 0 && Keys(art).Length == 3, "D4 an empty source beside a deploy with entries refuses (no wipe made permanent)");

        // D5 the editor emptied it itself, and that deploy FAILED (deploy still has the entry): the next change is allowed
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        RegistryRules.SaveOutcome o;
        using (new FileStream(art, FileMode.Open, FileAccess.Read, FileShare.Read))   // blocks the deploy's replace
            o = reg.Change(f => f.items.RemoveAll(e => e.key == "a") > 0, "drill", out changed);
        Check(o == S && changed && Keys(src).Length == 0 && Keys(art).Length == 1, "D5 removing the last entry is saved; its deploy failed (deploy still holds 1)");
        Check(reg.Change(Add("b"), "drill", out changed) == S && Keys(src).SequenceEqual(new[] { "b" }), "D5 the editor's OWN empty source passes the guard by its fingerprint");

        // D6 a locked source: a LOCK, not corruption; a change refuses and writes nothing
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            reg.Load();
            Check(reg.LastLoadLocked && !reg.LastLoadCorrupt && reg.LastLoadFailed && !RegistryRules.ShowRecoveryControls(reg.LastLoadCorrupt, reg.LastLoadLocked), "D6 a locked source is LOCKED, not corrupt; no recovery buttons");
            Check(reg.Change(Add("x"), "drill", out changed) == R, "D6 a change refuses while it can't be read");
        }
        Check(Keys(src).SequenceEqual(new[] { "a" }), "D6 the source is untouched");

        // D7 a lock supersedes an earlier corrupt verdict
        reg = Fresh(); File.WriteAllText(src, "{}"); reg.Load();
        File.WriteAllText(src, Reg("fixed"));
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None)) reg.Load();
        Check(reg.LastLoadLocked && !reg.LastLoadCorrupt, "D7 a lock clears the earlier corrupt verdict (the file may be repaired)");

        // D8 source missing, deploy unreadable: FAILED (no copy), a change refuses
        reg = Fresh(); File.WriteAllText(art, "<<<<<<< broken");
        var l8 = reg.Load();
        Check(l8.items.Count == 0 && reg.LastLoadNoCopy && reg.LastLoadFailed && !File.Exists(src), "D8 missing source + unreadable deploy = failed, not empty");
        Check(reg.Change(Add("x"), "drill", out changed) == R && !File.Exists(src), "D8 a change refuses; no one-entry source is created");

        // D9 source missing, deploy with entries: adopted
        reg = Fresh(); File.WriteAllText(art, Reg("a", "b"));
        var l9 = reg.Load();
        Check(l9.items.Count == 2 && File.Exists(src) && reg.LoadedVersion != null && !reg.LastLoadFailed, "D9 a missing source adopts the deploy");

        // D10 another writer adds an entry between the change's read and its write: kept, and the change still lands
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        int calls = 0;
        o = reg.Change(f => { if (calls++ == 0) { File.WriteAllText(src + ".o", Reg("a", "other")); File.Replace(src + ".o", src, null); } f.items.Add(new DrillEntry { key = "mine" }); return true; }, "drill", out changed);
        Check(o == S && Keys(src).SequenceEqual(new[] { "a", "mine", "other" }) && Keys(art).SequenceEqual(new[] { "a", "mine", "other" }), "D10 a concurrent entry is kept; the change is re-applied to it; the deploy follows");

        // D11 ReplaceAll only over the version the window loaded
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        reg.Load(); string v = reg.LoadedVersion;
        File.WriteAllText(src, Reg("a", "pulled"));   // git pull while the window is open
        Check(reg.ReplaceAll(new DrillFile { items = { new DrillEntry { key = "window" } } }, v, "drill") == R && Keys(src).SequenceEqual(new[] { "a", "pulled" }), "D11 a stale snapshot refuses; the newer file stays");
        reg.Load(); v = reg.LoadedVersion;
        Check(reg.ReplaceAll(new DrillFile { items = { new DrillEntry { key = "window" } } }, v, "drill") == S && Keys(src).SequenceEqual(new[] { "window" }), "D11 over the version it loaded, the snapshot is saved");
        Check(reg.ReplaceAll(new DrillFile(), null, "drill") == R, "D11 a window whose load failed (null version) never saves");
        string lf = Reg("a", "a2").Replace("\r\n", "\n"), crlf = lf.Replace("\n", "\r\n");
        File.WriteAllText(src, crlf); reg.Load(); v = reg.LoadedVersion;
        File.WriteAllText(src, lf);   // git rewrote the line endings only
        Check(reg.ReplaceAll(new DrillFile { items = { new DrillEntry { key = "z" } } }, v, "drill") == S, "D11 a line-ending rewrite is not a change (CRLF-blind version)");

        // D12 a failed deploy is FINISHED by a later Load, on the backoff, and is not called a hand-edit
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        using (new FileStream(art, FileMode.Open, FileAccess.Read, FileShare.Read))
            o = reg.Change(Add("b"), "drill", out changed);
        Check(o == S && Keys(art).Length == 1, "D12 the change is saved; its deploy failed");
        EditorApplication.timeSinceStartup += 0.5; reg.Load();
        Check(Keys(art).Length == 1, "D12 not retried before the backoff is due");
        EditorApplication.timeSinceStartup += 3; reg.Load();
        Check(Keys(art).SequenceEqual(new[] { "a", "b" }) && reg.TakeNotice().Contains("Finished a deploy") && !UnityEngine.Debug.Lines.Any(l => l.Contains("hand-edit")), "D12 a later Load finishes the deploy; no hand-edit warning");

        // D13 first-ever registry: no source, no deploy, no evidence it existed -> created
        reg = Fresh();
        Check(reg.Change(Add("first"), "drill", out changed) == S && Keys(src).SequenceEqual(new[] { "first" }) && Keys(art).SequenceEqual(new[] { "first" }), "D13 a first-ever change creates source and deploy");

        // D14 source missing but its .meta says it existed, no deploy: refused (another program may be saving it)
        reg = Fresh(); File.WriteAllText(src + ".meta", "guid: x");
        Check(reg.Change(Add("x"), "drill", out changed) == R && !File.Exists(src), "D14 a missing source Unity still knows is not recreated");

        // D15 recovery from git reads the COMMITTED text; a refused candidate leaves the working copy alone
        reg = Fresh(); File.WriteAllText(src, Reg("committed")); Git("init -q"); Git("add reg.backup.json"); Git("-c user.name=t -c user.email=t@t -c commit.gpgsign=false commit -q -m c");
        File.WriteAllText(src, "{}"); reg.Load();
        string msg = reg.RecoverFromGit();
        Check(msg.StartsWith("Recovered") && Keys(src).SequenceEqual(new[] { "committed" }) && !reg.LastLoadCorrupt, "D15 git recovery restores the committed version");
        File.WriteAllText(src, Reg()); Git("add reg.backup.json"); Git("-c user.name=t -c user.email=t@t -c commit.gpgsign=false commit -q -m empty");
        File.WriteAllText(src, "<<<<<<< my uncommitted, broken but precious edit"); reg.Load();
        msg = reg.RecoverFromGit();
        Check(msg.Contains("REFUSED") && File.ReadAllText(src) == "<<<<<<< my uncommitted, broken but precious edit", "D15 a refused git candidate leaves the working copy untouched (" + msg.Substring(0, Math.Min(60, msg.Length)) + ")");

        // D16 recovery from an unreadable deploy refuses and leaves the source
        reg = Fresh(); File.WriteAllText(src, "{}"); File.WriteAllText(art, "{}"); reg.Load();
        msg = reg.RecoverFromArtifact();
        Check(msg.Contains("REFUSED") && File.ReadAllText(src) == "{}", "D16 an unreadable deploy is not recovered from");

        // D17 nothing is left beside the files by any scenario's writes (temp names)
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        reg.Change(Add("b"), "drill", out changed);
        Check(!Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "D17 no temp files left");

        try { Directory.Delete(root, true); } catch { }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }
}
