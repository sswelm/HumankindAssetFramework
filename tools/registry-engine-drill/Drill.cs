// Drill of the REAL SingleSourceRegistry engine: one scenario per row of its exit table (see the PR #103 description),
// each written to FAIL without the rule it names. Run by tools/registry_engine_drill.sh (a gate step).
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
    static readonly List<string> roots = new List<string>();   // every scenario's folder, all removed at the end
    const string LegacyMarker = "drill.migrated";

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (ok) return;
        fails++;
        foreach (var l in UnityEngine.Debug.Lines.Skip(Math.Max(0, UnityEngine.Debug.Lines.Count - 4)))
            Console.WriteLine("       log: " + (l.Length > 200 ? l.Substring(0, 200) : l));
    }

    // A fresh project + game config. migrated = the one-time migration counts as done (the legacy machine-wide marker).
    static SingleSourceRegistry<DrillFile> Fresh(bool migrated = true, bool clearPrefs = true, string sharedArt = null)
    {
        root = Path.Combine(Path.GetTempPath(), "haf_ssrdrill_" + Guid.NewGuid().ToString("N"));
        roots.Add(root);
        src = Path.Combine(root, "Assets", "Databases", "reg.backup.json");
        art = sharedArt ?? Path.Combine(root, "config", "reg.json");
        Directory.CreateDirectory(Path.GetDirectoryName(src)); Directory.CreateDirectory(Path.GetDirectoryName(art));
        if (clearPrefs) EditorPrefs.P.Clear();
        UnityEngine.Debug.Lines.Clear(); EditorApplication.timeSinceStartup = 1000;
        if (migrated) EditorPrefs.SetBool(LegacyMarker, true);
        string s = src, a = art;   // this project's paths, fixed for this registry instance
        return new SingleSourceRegistry<DrillFile>("[Drill]", () => s, () => a, f => f?.items?.Count ?? 0,
                                                   LegacyMarker, "Assets/Databases/reg.backup.json", "entries", "items");
    }

    static string Reg(params string[] keys) => UnityEngine.JsonUtility.ToJson(new DrillFile { items = keys.Select(k => new DrillEntry { key = k }).ToList() }, true);
    static string[] Keys(string path) => UnityEngine.JsonUtility.FromJson<DrillFile>(File.ReadAllText(path)).items.Select(e => e.key).OrderBy(k => k).ToArray();
    static Func<DrillFile, bool> Add(string k) => f => { f.items.Add(new DrillEntry { key = k }); return true; };
    static bool Logged(string s) => UnityEngine.Debug.Lines.Any(l => l.Contains(s));
    static string[] Beside(string path, string pattern) => Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + pattern);

    static void Git(string args)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = Path.GetDirectoryName(src), UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var v in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) psi.EnvironmentVariables.Remove(v);
        using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); }
    }
    static void Commit(string msg) { Git("add reg.backup.json"); Git("-c user.name=t -c user.email=t@t -c commit.gpgsign=false commit -q -m " + msg); }

    // git writes its object files READ-ONLY, and Directory.Delete refuses those on Windows: clear the flag first.
    static void RemoveTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
        catch { }
    }

    static int Main()
    {
        try { Run(); }
        catch (Exception e) { Console.WriteLine("FAIL the drill itself threw: " + e); fails++; }
        foreach (var r in roots) RemoveTree(r);
        bool leftovers = roots.Any(Directory.Exists);
        if (leftovers) { Console.WriteLine("FAIL the drill could not remove its temp folders"); fails++; }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }

    static void Run()
    {
        bool changed;
        var S = RegistryRules.SaveOutcome.Saved; var R = RegistryRules.SaveOutcome.Refused; var U = RegistryRules.SaveOutcome.Unknown;
        RegistryRules.SaveOutcome o;

        // ---- LOAD: broken shapes are failures, never "zero entries"
        var reg = Fresh(); File.WriteAllText(src, "{}"); File.WriteAllText(art, Reg("a", "b", "c"));
        var loaded = reg.Load();
        Check(loaded.items.Count == 0 && reg.LastLoadCorrupt && reg.LastLoadFailed, "D1 `{}` loads as CORRUPT, not as an empty registry");
        Check(Directory.GetFiles(Path.GetDirectoryName(src), "*.corrupt-*.json").Length == 1, "D1 the broken source is preserved");
        Check(reg.Change(Add("x"), "drill", out changed) == R && File.ReadAllText(src) == "{}" && Keys(art).Length == 3, "D1 a change refuses; source and deploy untouched");
        reg = Fresh(); File.WriteAllText(src, ""); reg.Load();
        Check(reg.LastLoadCorrupt, "D2 a 0-byte source is corrupt, not empty");
        reg = Fresh(); File.WriteAllText(src, "{ \"wrong\": [] }"); reg.Load();
        Check(reg.LastLoadCorrupt, "D2 a source with the wrong key is corrupt, not empty");
        reg = Fresh(); File.WriteAllText(src, "{ \"Items\": [ { \"key\": \"a\" } ] }"); reg.Load();
        Check(reg.LastLoadCorrupt, "D2 keys are CASE-SENSITIVE like Unity's JsonUtility: \"Items\" is not \"items\"");

        // ---- CHANGE: judged by its own read
        reg = Fresh(); File.WriteAllText(src, "{}"); File.WriteAllText(art, Reg("a"));
        Check(reg.Change(Add("x"), "drill", out changed) == R && File.ReadAllText(src) == "{}" && reg.LastLoadCorrupt, "D3 a change's own read refuses `{}` and raises the recovery banner");
        reg = Fresh(); File.WriteAllText(src, Reg()); File.WriteAllText(art, Reg("a", "b", "c"));
        Check(reg.Change(Add("x"), "drill", out changed) == R && Keys(src).Length == 0 && Keys(art).Length == 3, "D4 an empty source beside a deploy with entries refuses (no wipe made permanent)");
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        using (new FileStream(art, FileMode.Open, FileAccess.Read, FileShare.Read))   // blocks the deploy's replace
            o = reg.Change(f => f.items.RemoveAll(e => e.key == "a") > 0, "drill", out changed);
        Check(o == S && changed && Keys(src).Length == 0 && Keys(art).Length == 1 && reg.DeployPending, "D5 removing the last entry is saved; its deploy failed and is marked pending");
        reg.Load();
        Check(!reg.Snapshot().EmptyButDeployed && !reg.Snapshot().Failed, "D5 ...a Load of the editor's own empty source (deploy still held the entry) is not 'emptied outside the editor'");
        EditorApplication.timeSinceStartup += 3; reg.Load();
        Check(Keys(art).Length == 0 && !reg.DeployPending, "D5 ...and the owed deploy finishes on a later load");
        Check(reg.Change(Add("b"), "drill", out changed) == S && Keys(src).SequenceEqual(new[] { "b" }), "D5 the editor's OWN empty source passes the guard by its fingerprint");

        // ---- LOCK vs CORRUPT
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            reg.Load();
            Check(reg.LastLoadLocked && !reg.LastLoadCorrupt && reg.LastLoadFailed && !RegistryRules.ShowRecoveryControls(reg.LastLoadCorrupt, reg.LastLoadLocked), "D6 a locked source is LOCKED, not corrupt; no recovery buttons");
            Check(reg.Change(Add("x"), "drill", out changed) == R, "D6 a change refuses while it can't be read");
        }
        Check(Keys(src).SequenceEqual(new[] { "a" }), "D6 the source is untouched");
        reg = Fresh(); File.WriteAllText(src, "{}"); reg.Load();
        File.WriteAllText(src, Reg("fixed"));
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None)) reg.Load();
        Check(reg.LastLoadLocked && !reg.LastLoadCorrupt, "D7 a lock clears the earlier corrupt verdict (the file may be repaired)");

        // ---- MISSING SOURCE
        reg = Fresh(); File.WriteAllText(art, "<<<<<<< broken");
        var l8 = reg.Load();
        Check(l8.items.Count == 0 && reg.LastLoadNoCopy && reg.LastLoadFailed && !File.Exists(src), "D8 missing source + unreadable deploy = failed, not empty");
        Check(reg.Change(Add("x"), "drill", out changed) == R && !File.Exists(src), "D8 a change refuses; no one-entry source is created");
        reg = Fresh(); string dep9 = Reg("a", "b"); File.WriteAllText(art, dep9);
        var l9 = reg.Load();
        Check(l9.items.Count == 2 && File.Exists(src) && reg.LoadedVersion == SingleSourceRegistry<DrillFile>.VersionOf(dep9) && !reg.LastLoadFailed && reg.TakeNotice().Contains("adopted"), "D9 a missing source adopts the deploy (and says so); LoadedVersion is the adopted text's");
        reg = Fresh(); File.WriteAllText(src + ".meta", "guid: x"); File.WriteAllText(art, Reg("old"));
        var k1 = reg.Load();
        Check(k1.items.Count == 0 && reg.LastLoadNoCopy && reg.LastLoadProblem.Contains(".meta") && !File.Exists(src), "K1 missing source Unity still knows (.meta): NOT adopted from the deploy, reported");
        Check(!reg.Snapshot().MissingKnown && !reg.LastLoadProblem.Contains("Restore last commit"), "K1 ...but 'Restore last commit' is NOT offered: git doesn't have it (only the .meta does)");
        Check(reg.Change(Add("x"), "drill", out changed) == R && !File.Exists(src), "K1 ...and a change neither builds on the deploy nor creates it");
        reg = Fresh(); File.WriteAllText(src + ".displaced-20260930_120000-deadbeef.json", Reg("newest")); File.WriteAllText(art, Reg("old"));
        reg.Load();
        Check(reg.LastLoadNoCopy && reg.LastLoadProblem.Contains("displaced") && !File.Exists(src), "K2 missing source beside an unsettled write's copy: NOT adopted, the copy is named");
        Check(reg.Change(Add("x"), "drill", out changed) == R && !File.Exists(src), "K2 ...and a change refuses");
        reg = Fresh();
        Check(reg.Change(Add("first"), "drill", out changed) == S && Keys(src).SequenceEqual(new[] { "first" }) && Keys(art).SequenceEqual(new[] { "first" }), "D13 a first-ever change creates source and deploy");
        reg = Fresh(); File.WriteAllText(src + ".meta", "guid: x");
        Check(reg.Change(Add("x"), "drill", out changed) == R && !File.Exists(src), "D14 a missing source Unity still knows is not recreated (no deploy either)");

        // ---- CONCURRENT WRITERS
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        int calls = 0;
        o = reg.Change(f => { if (calls++ == 0) { File.WriteAllText(src + ".o", Reg("a", "other")); File.Replace(src + ".o", src, null); } f.items.Add(new DrillEntry { key = "mine" }); return true; }, "drill", out changed);
        Check(o == S && Keys(src).SequenceEqual(new[] { "a", "mine", "other" }) && Keys(art).SequenceEqual(new[] { "a", "mine", "other" }), "D10 a concurrent entry is kept; the change is re-applied to it; the deploy follows");
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        int n = 0;
        o = reg.Change(f => { n++; File.WriteAllText(src + ".o", Reg("a", "other" + n)); File.Replace(src + ".o", src, null); f.items.Add(new DrillEntry { key = "mine" }); return true; }, "drill", out changed);
        Check(o == R && n == 3 && Keys(src).SequenceEqual(new[] { "a", "other3" }) && Keys(art).SequenceEqual(new[] { "a" }), "C1 a file that keeps changing: refused after 3 attempts, the other writer's version stays, nothing deployed");

        // ---- WHOLE-LIST SAVES
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

        // ---- DEPLOY OWED: finished later, never a "hand-edit"
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        using (new FileStream(art, FileMode.Open, FileAccess.Read, FileShare.Read))
            o = reg.Change(Add("b"), "drill", out changed);
        Check(o == S && Keys(art).Length == 1 && reg.DeployPending, "D12 the change is saved; its deploy failed and is pending");
        EditorApplication.timeSinceStartup += 0.5; reg.Load();
        Check(Keys(art).Length == 1, "D12 not retried before the backoff is due");
        EditorApplication.timeSinceStartup += 3; reg.Load();
        Check(Keys(art).SequenceEqual(new[] { "a", "b" }) && !reg.DeployPending && reg.TakeNotice().Contains("Finished a deploy") && !Logged("hand-edit"), "D12 a later Load finishes the deploy; no hand-edit warning");
        File.WriteAllText(art, Reg("a", "b", "hand-edited-deploy")); UnityEngine.Debug.Lines.Clear(); reg.Load();
        Check(reg.Snapshot().DeployHandEdited && !reg.Snapshot().Stale, "D12 ...the finished deploy counts as the editor's own write from then on (a later deploy edit is a hand-edit, not stale)");

        // ---- UNSETTLED WRITES (the checked replace can't settle; forced through the engine's own seam)
        var real = SingleSourceRegistry<DrillFile>.ApplyImpl;
        try
        {
            reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
            SingleSourceRegistry<DrillFile>.ApplyImpl = (string p, Func<string, string> c, int a, out string note) =>
            { note = null; var next = c(File.ReadAllText(p)); File.WriteAllText(p, next); return CheckedReplace.Outcome.Unresolved; };   // OUR write landed
            o = reg.Change(Add("landed"), "drill", out changed);
            SingleSourceRegistry<DrillFile>.ApplyImpl = real;
            Check(o == U && reg.DeployPending && Keys(art).Length == 1, "U1 an unsettled write is UNKNOWN (not Saved), its deploy owed");
            reg.Load();   // due at once: the read-back a caller does next
            Check(Keys(art).SequenceEqual(new[] { "a", "landed" }) && !reg.DeployPending && !Logged("hand-edit"), "U1 it landed: the next Load deploys it (the game gets it, no hand-edit warning)");
            File.WriteAllText(art, Reg("a", "landed", "hand-edited-deploy")); UnityEngine.Debug.Lines.Clear(); reg.Load();
            Check(reg.Snapshot().DeployHandEdited && !reg.Snapshot().Stale, "U1 ...and the finished deploy counts as the editor's own write (a later deploy edit is a hand-edit, not stale)");

            reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
            SingleSourceRegistry<DrillFile>.ApplyImpl = (string p, Func<string, string> c, int a, out string note) =>
            { note = null; c(File.ReadAllText(p)); File.WriteAllText(p, Reg("a", "theirs")); return CheckedReplace.Outcome.Unresolved; };   // THEIRS stayed
            o = reg.Change(Add("lost"), "drill", out changed);
            SingleSourceRegistry<DrillFile>.ApplyImpl = real;
            reg.Load();
            Check(o == U && !reg.DeployPending && !Keys(art).Contains("lost"), "U2 it did not land: the owed deploy is dropped as moot, our change never deployed");
            Check(reg.LastLoadStale && !Logged("hand-edit"), "U2 ...and the other writer's source is said as STALE for the game, not as a hand-edit");

            reg = Fresh(); File.WriteAllText(src, Reg("only")); File.WriteAllText(art, Reg("only"));
            SingleSourceRegistry<DrillFile>.ApplyImpl = (string p, Func<string, string> c, int a, out string note) =>
            { note = null; var next = c(File.ReadAllText(p)); File.WriteAllText(p, next); return CheckedReplace.Outcome.Unresolved; };   // landed, unsettled
            o = reg.Change(f => f.items.RemoveAll(e => e.key == "only") > 0, "drill", out changed);   // removes the LAST entry
            SingleSourceRegistry<DrillFile>.ApplyImpl = real;
            reg.Load();
            Check(o == U && !reg.Snapshot().EmptyButDeployed && Keys(art).Length == 0 && !reg.DeployPending, "U3 an unsettled write that emptied the registry (landed): the editor's own by its pending fingerprint - not stuck, the deploy finishes");
        }
        finally { SingleSourceRegistry<DrillFile>.ApplyImpl = real; }

        // ---- RECOVERY
        reg = Fresh(); File.WriteAllText(src, "{}"); File.WriteAllText(art, Reg("a")); reg.Load();
        File.WriteAllText(src, Reg("a", "b", "handfix"));   // fixed by hand; the window didn't reload, the banner is still up
        string msg = reg.RecoverFromArtifact();
        Check(msg.Contains("REFUSED") && msg.Contains("readable now") && Keys(src).SequenceEqual(new[] { "a", "b", "handfix" }) && !reg.LastLoadCorrupt, "R1 recovery over a source fixed by hand since the banner: REFUSED, the fix stays");
        reg = Fresh(); File.WriteAllText(src, "{}"); File.WriteAllText(art, Reg("a")); reg.Load();
        var before = reg.Snapshot();
        File.WriteAllText(src, Reg("a", "fixed-by-hand"));   // the file is fine now - but no Load ran
        Check(reg.Change(Add("x"), "drill", out changed) == R && Keys(src).SequenceEqual(new[] { "a", "fixed-by-hand" }), "MK a change after a corrupt load refuses until a Load (recovery first), even over a file fixed meanwhile");
        reg.Load();
        Check(before.Corrupt && !reg.Snapshot().Corrupt, "V1 a window's verdict is FROZEN: another Load changes the engine's flags, not the snapshot it took");
        reg = Fresh(); File.WriteAllText(src, Reg("committed")); Git("init -q"); Commit("c");
        File.WriteAllText(art, Reg("committed", "baked-since-commit")); File.WriteAllText(src, "{}"); reg.Load();
        msg = reg.RecoverFromGit();
        Check(msg.StartsWith("Recovered") && Keys(src).SequenceEqual(new[] { "committed" }) && !reg.LastLoadCorrupt, "R2 git recovery restores the committed version");
        Check(Keys(art).SequenceEqual(new[] { "committed" }), "R2 ...and the deploy follows it");
        var replaced = Beside(art, ".replaced-*.json");
        Check(replaced.Length == 1 && File.ReadAllText(replaced[0]).Contains("baked-since-commit"), "R2 ...with the old deploy (bakes since the commit) PRESERVED beside it");
        UnityEngine.Debug.Lines.Clear(); reg.Load();
        Check(!Logged("differs"), "R2 ...so the next Load sees no 'hand-edit' drift");
        File.WriteAllText(src, Reg()); Commit("empty");
        File.WriteAllText(src, "<<<<<<< my uncommitted, broken but precious edit"); reg.Load();
        msg = reg.RecoverFromGit();
        Check(msg.Contains("REFUSED") && File.ReadAllText(src) == "<<<<<<< my uncommitted, broken but precious edit", "R3 a refused git candidate leaves the working copy untouched");
        reg = Fresh(); File.WriteAllText(src, "{}"); File.WriteAllText(art, "{}"); reg.Load();
        msg = reg.RecoverFromArtifact();
        Check(msg.Contains("REFUSED") && File.ReadAllText(src) == "{}", "R4 an unreadable deploy is not recovered from");
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a")); reg.Load();
        File.WriteAllText(src, "{ \"items\": [ half-typed");   // broken by a hand edit no Load saw
        msg = reg.RecoverFromArtifact();
        var corrupts = Beside(src, ".corrupt-*.json");
        Check(msg.StartsWith("Recovered") && corrupts.Length == 1 && File.ReadAllText(corrupts[0]).Contains("half-typed"), "R5 recovery over a broken source no Load saw keeps a copy of it, as the message promises");

        // ---- round 4: a failed preservation must not leak into the (unpreserving) pending-deploy retry; the deploy replace is checked
        var realCopy4 = SingleSourceRegistry<DrillFile>.WriteCopyImpl;
        try
        {
            reg = Fresh(); File.WriteAllText(src, Reg("committed")); Git("init -q"); Commit("c");
            File.WriteAllText(art, Reg("committed", "baked-since-commit")); File.WriteAllText(src, "{}"); reg.Load();
            string cfgDir = Path.GetDirectoryName(art);
            SingleSourceRegistry<DrillFile>.WriteCopyImpl = (path, text) => { if (Path.GetDirectoryName(path) == cfgDir) throw new IOException("disk full"); File.WriteAllText(path, text); };   // only the DEPLOY's copy fails
            msg = reg.RecoverFromGit();
            Check(msg.StartsWith("Recovered") && msg.Contains("NOT refreshed") && Keys(art).SequenceEqual(new[] { "baked-since-commit", "committed" }) && !reg.DeployPending, "R6 recovery whose preservation fails: source recovered, deploy left as is, NOT marked pending");
            SingleSourceRegistry<DrillFile>.WriteCopyImpl = realCopy4;
            EditorApplication.timeSinceStartup += 5; reg.Load();
            Check(Keys(art).SequenceEqual(new[] { "baked-since-commit", "committed" }) && reg.Snapshot().Stale, "R6 ...the next Load does NOT deploy it unpreserved (no retry bypass); it says the game reads an older copy");
            msg = reg.AcceptSource(reg.LoadedVersion);
            replaced = Beside(art, ".replaced-*.json");
            Check(msg.StartsWith("Deployed") && Keys(art).SequenceEqual(new[] { "committed" }) && replaced.Length == 1 && File.ReadAllText(replaced[0]).Contains("baked-since-commit"), "R6 ...'Deploy the source' then deploys it, preserving the bakes");

            reg = Fresh(); File.WriteAllText(src, Reg("a", "pulled")); File.WriteAllText(art, Reg("a")); reg.Load();
            string other = Reg("a", "theirs-newer");
            SingleSourceRegistry<DrillFile>.WriteCopyImpl = (path, text) => { File.WriteAllText(path, text); File.WriteAllText(art, other); };   // another editor deploys BETWEEN the read and the replace
            msg = reg.AcceptSource(reg.LoadedVersion);
            SingleSourceRegistry<DrillFile>.WriteCopyImpl = realCopy4;
            Check(msg.Contains("NOT deployed") && Keys(art).SequenceEqual(new[] { "a", "theirs-newer" }) && Beside(art, ".replaced-*.json").Length == 0, "R7 a deploy written by another editor between the read and the replace is NOT overwritten (checked replace); nothing of ours deployed");
        }
        finally { SingleSourceRegistry<DrillFile>.WriteCopyImpl = realCopy4; }

        // ---- MIGRATION (off in every scenario above; on here)
        reg = Fresh(migrated: false); File.WriteAllText(src, Reg("valid-source")); File.WriteAllText(art, "{}");
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddHours(-2));   // the source is OLDER - the shape rule must still win
        reg.Load();
        Check(Keys(src).SequenceEqual(new[] { "valid-source" }) && Beside(art, ".pre-collapse-*.json").Length == 1, "M1 a deployed `{}` never wins the migration, even when newer; it is preserved beside the deploy");
        Check(Keys(art).SequenceEqual(new[] { "valid-source" }), "M1 ...and the kept source is DEPLOYED (the game doesn't keep reading the loser)");
        reg = Fresh(migrated: false); File.WriteAllText(src, Reg("older")); File.WriteAllText(art, Reg("newer"));
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddHours(-2));
        reg.Load();
        var losers = Beside(art, ".pre-collapse-*.json");
        Check(Keys(src).SequenceEqual(new[] { "newer" }) && losers.Length == 1 && File.ReadAllText(losers[0]).Contains("older"), "M2 a newer readable deploy is adopted; the older source is preserved");
        reg = Fresh(migrated: false); File.WriteAllText(src, Reg("newer")); File.WriteAllText(art, Reg("older"));
        File.SetLastWriteTimeUtc(art, DateTime.UtcNow.AddHours(-2));
        reg.Load();
        Check(Keys(src).SequenceEqual(new[] { "newer" }) && Keys(art).SequenceEqual(new[] { "newer" }), "M3 a newer source is kept AND deployed");
        File.WriteAllText(art, Reg("hand-edited-deploy")); UnityEngine.Debug.Lines.Clear(); reg.Load();
        Check(reg.Snapshot().DeployHandEdited && !reg.Snapshot().Stale, "M3 ...and counts as the editor's own write from then on (a later deploy edit is a hand-edit, not stale)");
        reg = Fresh(migrated: false); File.WriteAllText(src, Reg()); File.WriteAllText(art, Reg("a", "b", "c"));   // a pulled EMPTY source, newer
        File.SetLastWriteTimeUtc(art, DateTime.UtcNow.AddHours(-2)); reg.Load();
        Check(Keys(art).Length == 3 && reg.Snapshot().EmptyButDeployed, "M8 the first migration never deploys an EMPTY source over a deploy with entries: the person decides (EmptyButDeployed)");
        // THE DEPLOY IS SHARED by every project on the machine (one game config): project A migrates it once; project B,
        // sharing it, keeps ITS OWN source - it must not take A's registry over it
        reg = Fresh(migrated: false); File.WriteAllText(art, Reg("project-A")); reg.Load();
        string sharedArt = art;
        Check(Keys(src).SequenceEqual(new[] { "project-A" }), "M4 project A migrates the deploy");
        reg = Fresh(migrated: false, clearPrefs: false, sharedArt: sharedArt); File.WriteAllText(src, Reg("B-own"));
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddHours(-2)); reg.Load();   // B's source is even OLDER than the shared deploy
        Check(Keys(src).SequenceEqual(new[] { "B-own" }), "M4 project B, sharing the deploy, keeps its own source (the migration ran once, for the deploy)");
        reg = Fresh(migrated: false, clearPrefs: false, sharedArt: sharedArt); reg.Load();   // project C has no source at all
        Check(File.Exists(src) && Keys(src).SequenceEqual(new[] { "project-A" }), "M4 project C with no source still gets the deploy (Load adopts it)");
        reg = Fresh(migrated: true); File.WriteAllText(src, Reg("mine")); File.WriteAllText(art, Reg("deployed"));
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddHours(-2)); reg.Load();
        Check(Keys(src).SequenceEqual(new[] { "mine" }), "M5 the old machine-wide marker still counts: nothing migrates twice");
        // the losing copy can't be preserved -> NOTHING is replaced, and the next load tries again
        var realCopy = SingleSourceRegistry<DrillFile>.WriteCopyImpl;
        try
        {
            reg = Fresh(migrated: false); File.WriteAllText(src, Reg("uncommitted-only-copy")); File.WriteAllText(art, Reg("deploy-newer"));
            File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddHours(-2));
            SingleSourceRegistry<DrillFile>.WriteCopyImpl = (path, text) => throw new IOException("disk full");
            reg.Load();
            Check(Keys(src).SequenceEqual(new[] { "uncommitted-only-copy" }) && Logged("postponed"), "M6 a loser that can't be preserved: the source is NOT replaced (migration postponed)");
            Check(reg.Change(Add("x"), "drill", out changed) == R && Keys(art).SequenceEqual(new[] { "deploy-newer" }), "M6 ...saves refuse meanwhile (a save would overwrite the unpreserved deploy)");
            int warned = UnityEngine.Debug.Lines.Count(l => l.Contains("postponed"));
            reg.Load(); reg.Load(); reg.Load();
            Check(UnityEngine.Debug.Lines.Count(l => l.Contains("postponed")) == warned, "M6 ...and the postponement is warned once, not per load");
            SingleSourceRegistry<DrillFile>.WriteCopyImpl = realCopy;
            reg.Load();
            losers = Beside(art, ".pre-collapse-*.json");
            Check(Keys(src).SequenceEqual(new[] { "deploy-newer" }) && losers.Length == 1 && File.ReadAllText(losers[0]).Contains("uncommitted-only-copy"), "M6 ...the next load migrates, with the loser preserved");
            // two migrations never overwrite each other's loser (unique names)
            reg = Fresh(migrated: false); File.WriteAllText(src, Reg("x1")); File.WriteAllText(art, Reg("y1"));
            File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddHours(-2)); reg.Load();
            EditorPrefs.P.Clear(); File.WriteAllText(src, Reg("x2")); File.WriteAllText(art, Reg("y2"));
            File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddHours(-2)); reg.Load();
            Check(Beside(art, ".pre-collapse-*.json").Length == 2, "M7 a second migration's loser does not overwrite the first's");
        }
        finally { SingleSourceRegistry<DrillFile>.WriteCopyImpl = realCopy; }
        reg = Fresh(migrated: false); File.WriteAllText(src + ".meta", "guid: x"); File.WriteAllText(art, Reg("deployed"));
        reg.Load();
        Check(!File.Exists(src) && reg.LastLoadNoCopy, "MB the migration respects the missing-source block too (.meta says it exists: not adopted)");

        // ---- EMPTY SOURCE BESIDE A FULL DEPLOY (a pull of a teammate's empty commit): the person decides
        reg = Fresh(); File.WriteAllText(src, Reg()); File.WriteAllText(art, Reg("a", "b", "c"));
        reg.Load();
        var e1 = reg.Snapshot();
        Check(e1.EmptyButDeployed && e1.DeployedCount == 3 && e1.Failed && reg.LastLoadFailed && !e1.Stale && !Logged("GAME still reads"), "E1 an empty source (not this editor's) beside a deploy with 3: its own state (not 'stale'), the list can't be trusted");
        Check(reg.Change(Add("x"), "drill", out changed) == R && Keys(art).Length == 3, "E1 ...a change is refused, the deploy untouched");
        msg = reg.RecoverFromArtifact();
        Check(msg.StartsWith("Recovered") && Keys(src).SequenceEqual(new[] { "a", "b", "c" }), "E1 'Restore last deploy' works on an EMPTY readable source (the case it exists for)");
        reg = Fresh(); File.WriteAllText(src, Reg()); File.WriteAllText(art, Reg("a", "b", "c")); reg.Load();
        msg = reg.AcceptSource(reg.LoadedVersion);
        Check(msg.StartsWith("Deployed") && Keys(art).Length == 0 && Beside(art, ".replaced-*.json").Length == 1, "E2 'Keep it empty' empties the game's copy, keeping the replaced one beside it");
        File.WriteAllText(art, Reg("hand-edited-after-accept"));   // then someone edits the DEPLOY: said as that, not as a moved source
        UnityEngine.Debug.Lines.Clear(); reg.Load();
        Check(reg.Snapshot().DeployHandEdited && !reg.Snapshot().Stale, "E2 ...the accepted source counts as the editor's own write from then on (a later deploy edit is a hand-edit, not stale)");
        reg = Fresh(); File.WriteAllText(src, Reg()); File.WriteAllText(art, Reg("a", "b", "c")); reg.Load(); v = reg.LoadedVersion;
        File.WriteAllText(src, Reg("refilled-by-a-pull"));   // the banner is up, but a pull refilled the source since
        msg = reg.AcceptSource(v);
        Check(msg.Contains("REFUSED") && Keys(art).SequenceEqual(new[] { "a", "b", "c" }), "E3 'Keep it empty' over a source that changed since the window loaded it: REFUSED, nothing deployed");
        reg.Load();
        Check(!reg.Snapshot().Failed && reg.Change(Add("fresh"), "drill", out changed) == S, "E2 ...and from then on changes go through");
        reg = Fresh(); File.WriteAllText(src, Reg()); File.WriteAllText(art, "<<<<<<< broken"); reg.Load();
        Check(reg.Snapshot().EmptyButDeployed && reg.Snapshot().DeployedCount == -1, "MA an empty source beside an UNREADABLE deploy: the same state (it may still hold entries)");
        Check(reg.Change(Add("x"), "drill", out changed) == R && Logged("can't be checked"), "MA ...a change refuses: the deploy can't be checked");

        // ---- A SOURCE CHANGED OUTSIDE THE EDITOR: the game reads an older deploy - said, and deployed on the person's word
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        reg.Change(Add("b"), "drill", out changed);          // the editor's own write: source == deploy == [a,b]
        File.WriteAllText(src, Reg("a", "b", "pulled"));    // git pull
        UnityEngine.Debug.Lines.Clear(); reg.Load();
        Check(reg.Snapshot().Stale && !reg.Snapshot().Failed && Logged("GAME still reads") && !Logged("hand-edit"), "S1 a pulled source: STALE for the game, not a hand-edit, not a failure");
        Check(Keys(art).SequenceEqual(new[] { "a", "b" }), "S1 ...the deploy is NOT overwritten on a guess (it is the last-deploy recovery candidate)");
        msg = reg.AcceptSource(reg.LoadedVersion);
        Check(msg.StartsWith("Deployed") && Keys(art).SequenceEqual(new[] { "a", "b", "pulled" }) && Beside(art, ".replaced-*.json").Length == 1, "S1 'Deploy the source' deploys it, keeping the replaced copy");
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        reg.Change(Add("b"), "drill", out changed);
        File.WriteAllText(art, Reg("a", "b", "hand-edited-deploy"));
        UnityEngine.Debug.Lines.Clear(); reg.Load();
        Check(reg.Snapshot().DeployHandEdited && !reg.Snapshot().Stale && Logged("hand-edit"), "S2 a hand-edited DEPLOY (the source is what the editor wrote) is said as such");

        // ---- GIT AS EVIDENCE that a missing source exists
        reg = Fresh(); File.WriteAllText(src, Reg("tracked")); Git("init -q"); Commit("t");
        File.Delete(src); File.WriteAllText(art, Reg("deployed-older"));
        reg.Load();
        Check(reg.Snapshot().NoCopy && reg.Snapshot().MissingKnown && reg.LastLoadProblem.Contains("git tracks it") && !File.Exists(src), "MC a missing source git still tracks: not adopted from the deploy; 'Restore last commit' is offered");
        Check(reg.Change(Add("x"), "drill", out changed) == R && !File.Exists(src), "MC ...a change refuses");
        msg = reg.RecoverFromGit();
        Check(msg.StartsWith("Recovered") && Keys(src).SequenceEqual(new[] { "tracked" }), "MC ...and 'Restore last commit' brings it back");

        // ---- HYGIENE
        reg = Fresh(); File.WriteAllText(src, Reg("a")); File.WriteAllText(art, Reg("a"));
        reg.Change(Add("b"), "drill", out changed);
        Check(!Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "D17 no temp files left");
    }
}
