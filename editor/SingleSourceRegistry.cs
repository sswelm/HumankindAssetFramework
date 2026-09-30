// SingleSourceRegistry.cs — the ONE-file registry engine, shared (2026-08-20).
//
// The 2026-08-19 collapse gave the UNIT registry this shape: the git-tracked PROJECT file is THE registry; the
// deployed copy under BepInEx/config is a BUILD ARTIFACT regenerated on every Save (recreated on Load if missing,
// a hand-edit there warned about once); a corrupt source is PINPOINTED (line/column via Newtonsoft), preserved
// timestamped, logged once, Save-locked, and recoverable in one click from the last deploy or the last commit.
// DistrictRegistry, FormationRegistry and SoundOverrideRegistry are thin typed shells over this engine; ModelRegistry
// keeps its own implementation (pack-header merge semantics) and is the reference.
//
// THE SAME SAFETY RULES AS THE MODEL REGISTRY (critical review 2026-09-30: the hardening of PRs #100-#102 reached
// ModelRegistry only, and this engine still read `{}` as an empty registry that one bake then wrote over the source
// and the deploy). Every rule reuses the model registry's own, tested pieces:
//   * SHAPE: an empty parse is a registry only when the raw text carries its list as an array (RegistryRules.HasArray);
//     `{}`, the wrong keys or a 0-byte file are a broken file, never "zero entries".
//   * READ FAILURE IS NOT CORRUPTION: a source another program holds is LastLoadLocked (a plain warning, no recovery
//     buttons - "Restore last commit" would replace a file with nothing wrong in it), and it supersedes an earlier
//     corrupt verdict (RegistryRules.ShowRecoveryControls).
//   * A MISSING SOURCE IS ONLY REBUILT FROM THE DEPLOY WHEN NOTHING SAYS IT STILL EXISTS: not when Unity's .meta or git
//     still knows it (another program may be saving it by rename), and not when an unsettled write left copies beside
//     it (they may hold the truth). Otherwise the deploy is adopted, or, with none, a change starts a first registry.
//   * EVERY CHANGE IS A CHECKED OPERATION on the file as it is at write time (CheckedReplace.Apply): it refuses an
//     unreadable or broken source, refuses to write over a source that is empty while the deploy still holds entries
//     (RegistryRules.JudgeEmptySource, with the editor's own last-write fingerprint), re-applies itself to another
//     writer's version on a conflict, and says what happened (RegistryRules.SaveOutcome).
//   * A DEPLOY THAT DIDN'T HAPPEN IS FINISHED LATER (PENDING fingerprint + RegistryRules.PendingRetryDue), never called
//     a hand-edit - including after a write that couldn't be settled, when the source may hold this change.
//   * RECOVERY JUDGES BEFORE IT WRITES: the committed text is read with `git show`, never checked out over the file;
//     a source that is readable again (fixed by hand since the banner appeared) is never recovered over; the deploy
//     follows a recovery.
// Plus the engine's own rules from 2026-08-20: migration never overwrites a NEWER source with an older deployed copy
// (the loser is preserved either way) - and never adopts a deploy that fails the shape rule - and content comparisons
// are CRLF-normalized.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public class SingleSourceRegistry<TFile> where TFile : class, new()
{
    readonly string tag, prefKey, gitRel, noun, arrayKey;
    readonly Func<string> sourcePath, artifactPath;
    readonly Func<TFile, int> count;          // entries in a parsed file — recovery/adoption candidates must hold >= 1
    bool corruptLogged, driftWarned, pendingRetryWarned;   // once per corruption / domain load / failing deploy
    double lastPendingAttempt = -1; int pendingFailures;   // RegistryRules.PendingRetryDue: 2, 4, 8, 16 s, then every 30 s

    public string SourcePath => sourcePath();
    public string ArtifactPath => artifactPath();

    // ---- what the last Load() could and couldn't do (each Load decides all of them afresh) ----
    public bool LastLoadCorrupt { get; private set; }     // READ, and broken: the recovery banner
    public string LastCorruptDetail { get; private set; } = "";
    public bool LastLoadLocked { get; private set; }      // could not be READ at all: a plain warning, no recovery
    public string LastLockDetail { get; private set; } = "";
    public string LastLockAdvice { get; private set; } = "";
    public bool LastLoadNoCopy { get; private set; }      // source missing, and nothing trustworthy to load in its place
    public string LastMissingDetail { get; private set; } = "";
    /// <summary>The last Load() returned an empty file for want of a read — its "no entries" proves nothing.</summary>
    public bool LastLoadFailed => LastLoadCorrupt || LastLoadLocked || LastLoadNoCopy;
    public string LastLoadProblem =>
        LastLoadCorrupt ? "is unreadable — " + LastCorruptDetail
      : LastLoadLocked ? "can't be read right now — " + LastLockDetail
      : LastLoadNoCopy ? "is missing — " + LastMissingDetail
      : "";

    /// <summary>A save wrote the source, but the deployed copy the game reads is not refreshed yet (a later load/save finishes it).</summary>
    public bool DeployPending => EditorPrefs.GetString(PrefPendingDeploy, "") != "";

    /// <summary>
    /// The VERSION of the source the last successful Load() returned (a CRLF-blind fingerprint, or NoFile when there
    /// was none); null after a failed load. A window that edits the WHOLE list keeps this and hands it to ReplaceAll,
    /// which writes only over that same version - so a git pull or another window's save in between is never
    /// overwritten by an older snapshot.
    /// </summary>
    public string LoadedVersion { get; private set; }
    public const string NoFile = "(no file)";

    // A self-healing action (artifact recreated, source adopted, deploy finished) is otherwise a Console-only event —
    // invisible to the person who just pressed Refresh (drill 2026-08-20: "nothing happens, proof it does not
    // work"). The window takes the notice and shows it in its status line.
    string notice = "";
    public string TakeNotice() { var n = notice; notice = ""; return n; }

    // tag "[District]"; sourcePath = the git-tracked project file; artifactPath = the deployed file the game reads
    // (lazy: ConfigDir is resolved at call time); prefKey = one-time migration marker; gitRel = repo-relative source
    // path (messages); noun = "district entries" for messages; arrayKey = the JSON key of the entry list.
    public SingleSourceRegistry(string tag, Func<string> sourcePath, Func<string> artifactPath, Func<TFile, int> count,
                                string prefKey, string gitRel, string noun, string arrayKey)
    {
        this.tag = tag; this.sourcePath = sourcePath; this.artifactPath = artifactPath; this.count = count;
        this.prefKey = prefKey; this.gitRel = gitRel; this.noun = noun; this.arrayKey = arrayKey;
    }

    // What the editor itself last wrote to the source, and a deploy that is still owed (see ModelRegistry.PrefLastWrite).
    string PrefLastWrite => "HAF.Registry.LastWrite|" + SourcePath;
    string PrefPendingDeploy => "HAF.Registry.PendingDeploy|" + SourcePath;

    // The checked read-change-write, reachable by the drill (tools/registry-engine-drill), which has to make a write
    // end UNSETTLED to test what follows - no real file system does that on demand.
    internal delegate CheckedReplace.Outcome ApplyFn(string path, Func<string, string> change, int attempts, out string note);
    internal static ApplyFn ApplyImpl = (string p, Func<string, string> c, int a, out string n) => CheckedReplace.Apply(p, c, a, out n);

    // ======================================================================================================== LOAD

    public TFile Load()
    {
        LastLoadLocked = false; LastLoadNoCopy = false; LastMissingDetail = "";   // every Load decides them afresh
        LoadedVersion = null;                                                      // set below only where a read succeeded
        try
        {
            MigrateOnce();
            if (!File.Exists(SourcePath))
            {
                // Don't declare the registry dead on ONE glance: an external editor's save-by-rename leaves a
                // milliseconds-wide window where the file doesn't exist. Re-check briefly.
                System.Threading.Thread.Sleep(250);
                if (!File.Exists(SourcePath))
                {
                    LastLoadCorrupt = false; corruptLogged = false;
                    // Something says the source still exists (or an unsettled write left its copies): nothing is adopted
                    // or presented as "no entries" - that is a failed load until it resolves.
                    string block = MissingSourceBlock();
                    if (block != null) { LastLoadNoCopy = true; LastMissingDetail = block; return new TFile(); }
                    // Source gone (fresh clone, a deletion Unity has seen) but a deployed artifact exists: ADOPT it — it
                    // is the only surviving copy of the data.
                    if (File.Exists(ArtifactPath))
                    {
                        try
                        {
                            var dep = File.ReadAllText(ArtifactPath);
                            var d = Parse(dep, out string depWhy);
                            if (d == null)
                            {
                                LastLoadNoCopy = true; LastMissingDetail = $"its deployed copy is unreadable too ({depWhy})";
                                Debug.LogWarning($"{tag} the project registry source is missing and the deployed artifact '{ArtifactPath}' is unreadable ({depWhy}) — nothing to adopt; changes refuse until one of them is readable.");
                                return new TFile();
                            }
                            if (count(d) > 0)
                            {
                                WriteAtomic(SourcePath, dep);
                                EditorPrefs.SetString(PrefLastWrite, Fingerprint(dep));
                                LoadedVersion = Fingerprint(dep);
                                notice = $"Project registry source was missing — adopted {count(d)} {noun} from the deployed artifact.";
                                Debug.Log($"{tag} project registry source was missing — adopted {count(d)} {noun} from the deployed artifact ({ArtifactPath}).");
                                return d;
                            }
                        }
                        catch (Exception be)
                        {
                            LastLoadNoCopy = true; LastMissingDetail = $"its deployed copy could not be read or adopted ({be.Message})";
                            Debug.LogWarning($"{tag} the deployed artifact '{ArtifactPath}' could not be read or adopted ({be.Message}) — nothing loaded.");
                            return new TFile();
                        }
                    }
                    LoadedVersion = NoFile;
                    return new TFile();
                }
            }
            string json;
            try { json = File.ReadAllText(SourcePath); }
            catch (Exception re) when (RegistryRules.ClassifyReadFailure(re) != RegistryRules.ReadFailure.NotARead)
            {
                // could not READ it: another program has it open, or is replacing it. Not the corrupt path below — and it
                // SUPERSEDES an earlier corrupt verdict, which was about bytes nobody can see now (see ModelRegistry).
                LastLoadLocked = true; LastLoadCorrupt = false;
                LastLockDetail = re.Message; LastLockAdvice = RegistryRules.ReadFailureAdvice(RegistryRules.ClassifyReadFailure(re));
                return new TFile();
            }
            var data = Parse(json, out string why);
            if (data == null) throw new Unreadable(why);
            LastLoadCorrupt = false; corruptLogged = false;
            LoadedVersion = Fingerprint(json);
            SyncArtifact(json);
            return data;
        }
        catch (Exception e)
        {
            // The source exists but won't parse. Preserve it, pinpoint the fault, flag it so no change can clobber it.
            LastLoadCorrupt = true;
            LastCorruptDetail = e is Unreadable ? e.Message : Pinpoint(SourcePath) ?? e.Message;
            if (!corruptLogged)   // one Console error per corruption, not per Load() poll
            {
                corruptLogged = true;
                string keep = SourcePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
                string kept;
                try { File.Copy(SourcePath, keep, true); kept = $"Preserved as '{Path.GetFileName(keep)}'."; }
                catch (Exception ce) { kept = $"It could NOT be preserved beside the source ({ce.Message}) — copy it by hand before recovering."; }
                Debug.LogError($"{tag} registry source '{SourcePath}' is unreadable — {LastCorruptDetail}. {kept} " +
                               "Recover it (restore the last deploy or the last commit — the window offers both) or fix it by hand. Changes are locked until then.");
            }
            return new TFile();
        }
    }

    // Why a MISSING source must not be rebuilt from the deploy or started afresh, or null when nothing stands in the way:
    // copies an unsettled write left beside it (compare them first - one may be the newest version), or evidence that
    // the file still exists (Unity's .meta, or git: another program may be saving it by rename).
    string MissingSourceBlock()
    {
        string[] kept;
        try { kept = CheckedReplace.Preserved(SourcePath); } catch { kept = new string[0]; }
        if (kept.Length > 0)
            return $"copies an unsettled save kept beside it ({string.Join(", ", Array.ConvertAll(kept, Path.GetFileName))}) may hold its newest version — compare them, rename the right one back to '{Path.GetFileName(SourcePath)}', then refresh";
        string existed = CheckedReplace.ExistedBefore(SourcePath);
        if (existed != null)
            return $"but {existed}, so it existed — another program may be saving it (it comes back by itself; refresh in a moment). If you deleted it on purpose: " +
                   (existed.StartsWith("git") ? "commit the deletion, or restore it with git" : "let Unity refresh (or delete its .meta too)");
        return null;
    }

    // A verdict Parse already worded — Load()'s catch must not replace it with Pinpoint's "Newtonsoft parses it".
    class Unreadable : Exception { public Unreadable(string why) : base(why) { } }

    // The verdict on one text: null = broken (why says how), never "an empty registry" for a file that isn't one.
    TFile Parse(string json, out string why)
    {
        why = null;
        TFile f;
        try { f = JsonUtility.FromJson<TFile>(json); }
        catch (Exception e) { why = PinpointText(json) ?? e.Message; return null; }
        if (f == null) { why = "the file is empty or not a JSON object"; return null; }
        if (count(f) == 0 && !RegistryRules.HasArray(json, arrayKey))
        {
            why = $"it has no \"{arrayKey}\" array (every file the editor writes carries one, even an empty registry)";
            return null;
        }
        return f;
    }

    // ===================================================================================================== CHANGES

    /// <summary>
    /// Change the registry by an OPERATION on the file as it is at write time: <paramref name="apply"/> gets the parsed
    /// file and returns whether it changed anything. The write is checked (CheckedReplace.Apply): a broken or empty-
    /// but-deployed source refuses, another writer's version is re-read and the operation re-applied to it, and the
    /// outcome says only what happened. <paramref name="changed"/> = the operation found something to change.
    /// </summary>
    public RegistryRules.SaveOutcome Change(Func<TFile, bool> apply, string what, out bool changed) =>
        ChangeCore(f => apply(f) ? f : null, what, null, out changed);

    /// <summary>
    /// Replace the whole list with <paramref name="file"/> — for a window that edits the WHOLE list — but only over the
    /// version that window loaded (<paramref name="loadedVersion"/> = LoadedVersion right after its Load). Anything
    /// else refuses and says to reload: another window, git or a hand edit changed the file since.
    /// </summary>
    public RegistryRules.SaveOutcome ReplaceAll(TFile file, string loadedVersion, string what)
    {
        if (loadedVersion == null)
        {
            Debug.LogWarning($"{tag} not saving {what}: the window never loaded the registry (its last load failed), so there is nothing to save over safely. Reload once it can be read.");
            return RegistryRules.SaveOutcome.Refused;
        }
        return ChangeCore(current => file, what, loadedVersion, out _);   // the caller's file, written as it is
    }

    // `produce` gets the file as read now and returns the file to write, or null when there is nothing to change.
    RegistryRules.SaveOutcome ChangeCore(Func<TFile, TFile> produce, string what, string mustStillBe, out bool changed)
    {
        changed = false;
        if (LastLoadCorrupt)
        {
            Debug.LogError($"{tag} not saving {what}: the registry source was unreadable — recover it first (restore the last deploy or the last commit, or fix it by hand and refresh). Refusing to overwrite it and lose your entries.");
            return RegistryRules.SaveOutcome.Refused;
        }
        // What the attempt found, decided afresh on every attempt (Apply re-runs the lambda after a conflict).
        string refusal = null; bool broken = false, didChange = false; string written = null;
        CheckedReplace.Outcome outcome; string note;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath));
            outcome = ApplyImpl(SourcePath, text =>
            {
                refusal = null; broken = false; didChange = false; written = null;
                if (mustStillBe != null && VersionOf(text) != mustStillBe)
                {
                    refusal = "the registry changed since this window loaded it (another window, git, or a hand edit) — writing now would put the older list over the newer one. Reload, redo the change, and save again";
                    return null;
                }
                TFile file;
                if (text == null)
                {
                    // NO SOURCE. Not while something says it still exists, or an unsettled write left its copies; otherwise
                    // the deployed copy is the only surviving one - build on it, as Load() adopts it; with none, start one.
                    string block = MissingSourceBlock();
                    if (block != null) { refusal = "the source is missing, " + block; return null; }
                    if (File.Exists(ArtifactPath))
                    {
                        string dep;
                        try { dep = File.ReadAllText(ArtifactPath); }
                        catch (Exception e) { refusal = $"the source is missing and the deployed copy '{ArtifactPath}' can't be read ({e.Message}) — it may be the only copy of this registry. Close whatever holds it (the game?) and try again"; return null; }
                        file = Parse(dep, out string depWhy);
                        if (file == null) { refusal = $"the source is missing and the deployed copy '{ArtifactPath}' is unreadable ({depWhy}) — it may be the only copy of this registry"; return null; }
                    }
                    else file = new TFile();   // a first-ever registry
                }
                else
                {
                    file = Parse(text, out string why);
                    if (file == null) { broken = true; refusal = why; return null; }
                    if (count(file) == 0)
                    {
                        // THE SOURCE HOLDS NO ENTRIES, and the editor didn't write it that way: a deployed copy that still has
                        // entries means it was emptied outside the editor, and no change may make that wipe permanent.
                        bool writtenByEditor = Fingerprint(text) == EditorPrefs.GetString(PrefLastWrite, "");
                        string deployedWhy = null;
                        int deployed = writtenByEditor ? 0 : DeployedCount(out deployedWhy);
                        var verdict = RegistryRules.JudgeEmptySource(true, 0, writtenByEditor, deployed);
                        if (verdict == RegistryRules.EmptySourceVerdict.RefuseDeployedHasModels)
                        { refusal = $"the source holds no {noun}, but the deployed copy still has {deployed}. Restore them (the window's recovery, or git) and try again. If the registry really is empty now, delete the deployed copy (the next Load recreates it)"; return null; }
                        if (verdict == RegistryRules.EmptySourceVerdict.RefuseDeployedUnreadable)
                        { refusal = $"the source holds no {noun}, and the deployed copy '{ArtifactPath}' can't be checked ({deployedWhy}), so it may still hold them. Close whatever holds it (the game?) and try again"; return null; }
                    }
                }
                var result = produce(file);
                if (result == null) return null;   // nothing to change
                didChange = true;
                return written = JsonUtility.ToJson(result, true);
            }, 3, out note);
        }
        catch (Exception e)
        {
            // CheckedReplace throws only when nothing of this change was written
            Debug.LogError($"{tag} not saving {what}: the registry source '{SourcePath}' could not be read or written ({e.Message}). Nothing of this save was written; close whatever holds it (AV, indexer) and try again.");
            return RegistryRules.SaveOutcome.Refused;
        }
        if (note != null) Debug.LogWarning($"{tag} registry source: {note}.");
        if (broken)
        {
            LastLoadCorrupt = true; LastCorruptDetail = refusal; LastLoadLocked = false;   // it was just read, so it is not a lock
            Debug.LogError($"{tag} not saving {what}: the registry source '{SourcePath}' is unreadable right now — {refusal}. Refusing to overwrite it; recover it (restore the last deploy or the last commit) or fix it by hand, refresh, and try again.");
            return RegistryRules.SaveOutcome.Refused;
        }
        if (refusal != null)
        {
            Debug.LogWarning($"{tag} not saving {what}: {refusal}. Nothing was written.");
            return RegistryRules.SaveOutcome.Refused;
        }
        switch (outcome)
        {
            case CheckedReplace.Outcome.Unchanged:
                return RegistryRules.SaveOutcome.Saved;   // the file already says what this change would have
            case CheckedReplace.Outcome.Conflict:
                Debug.LogWarning($"{tag} not saving {what}: the registry source kept changing while this save tried to apply it (another editor, git, a sync tool). That version is in place and nothing of this save was written. Try again.");
                return RegistryRules.SaveOutcome.Refused;
            case CheckedReplace.Outcome.Unresolved:
                // The source may hold THIS change: then the deploy is owed. Mark it PENDING with this change's fingerprint,
                // due at once - the next Load deploys it if the source is exactly this change, and drops it as moot if not
                // (critical review 2026-09-30: an unsettled write that had landed left the game on the old entry).
                if (written != null)
                {
                    EditorPrefs.SetString(PrefPendingDeploy, Fingerprint(written));
                    pendingFailures = 0; lastPendingAttempt = -1;
                }
                Debug.LogError($"{tag} saving {what} could not be settled at '{SourcePath}' (the note above says why). The source may hold this save, another version, or be missing; " +
                               "inspect the source and the preserved copies named above, then refresh before saving again (if this save landed, the refresh also brings the deployed copy up to date).");
                return RegistryRules.SaveOutcome.Unknown;
        }
        // WRITTEN. The editor's own write is fingerprinted (the empty-source guard tells it from a hand-emptying), and the
        // deployed copy follows; a deploy that fails is finished by a later load or save, not called a hand-edit.
        changed = didChange;
        EditorPrefs.SetString(PrefLastWrite, Fingerprint(written));
        Deploy(written);
        AssetDatabase.Refresh();
        return RegistryRules.SaveOutcome.Saved;
    }

    void Deploy(string json)
    {
        try
        {
            WriteAtomic(ArtifactPath, json);
            EditorPrefs.DeleteKey(PrefPendingDeploy);
            pendingFailures = 0; lastPendingAttempt = -1;
        }
        catch (Exception e)
        {
            EditorPrefs.SetString(PrefPendingDeploy, Fingerprint(json));
            pendingRetryWarned = false; pendingFailures = 0; lastPendingAttempt = EditorApplication.timeSinceStartup;   // the first retry is due in 2 s
            Debug.LogWarning($"{tag} deployed-artifact refresh FAILED ({e.Message}) — the registry SOURCE saved fine, but the running game keeps reading the stale " +
                             $"'{ArtifactPath}' until it can be written. The next load of this registry (Refresh in its window) or the next save retries it.");
        }
    }

    // Entries in the deployed copy: 0 only when it is ABSENT or provably empty; -1 when it exists but can't be read or
    // fails the shape rule — no evidence of an empty registry.
    int DeployedCount(out string why)
    {
        why = null;
        if (!File.Exists(ArtifactPath)) return 0;
        try { var d = Parse(File.ReadAllText(ArtifactPath), out why); return d == null ? -1 : count(d); }
        catch (Exception e) { why = e.Message; return -1; }
    }

    // ==================================================================================================== RECOVERY
    // Each candidate is VALIDATED (must parse and hold >= 1 entry) BEFORE it is written; the corrupt file is already
    // preserved timestamped; the write is checked, so a source someone changed meanwhile is not overwritten; and a
    // source that is readable NOW is never recovered over - the banner may be older than a hand fix.

    public string RecoverFromArtifact()
    {
        if (!File.Exists(ArtifactPath)) return "⚠ no deployed artifact exists to recover from.";
        try { return RecoverSourceFrom(File.ReadAllText(ArtifactPath), "the deployed artifact (last good deploy)"); }
        catch (Exception e) { return "⚠ could not read the deployed artifact: " + e.Message; }
    }

    // The last COMMITTED version, read with `git show` — never checked out over the file first (critical review
    // 2026-09-30: a checkout that the validation then refused had already replaced the working copy).
    public string RecoverFromGit()
    {
        string committed = CheckedReplace.GitCommittedText(SourcePath, out string error);
        if (committed == null) return $"⚠ git recovery FAILED: {error} (nothing was overwritten).";
        return RecoverSourceFrom(committed, "git (last committed version)");
    }

    string RecoverSourceFrom(string candidateJson, string label)
    {
        try
        {
            var r = Parse(candidateJson, out string why);
            if (r == null) return $"⚠ recovery from {label} REFUSED: the candidate is unreadable too ({why}); nothing was overwritten.";
            if (count(r) == 0) return $"⚠ recovery from {label} REFUSED: candidate holds no {noun} (nothing was overwritten).";
            string current = File.Exists(SourcePath) ? File.ReadAllText(SourcePath) : null;
            if (current != null && Parse(current, out _) != null)
            {
                // READABLE NOW (critical review 2026-09-30): the source was fixed by hand after the banner appeared - the
                // windows don't reload on focus. Recovering would overwrite that fix, and no copy of it would remain.
                LastLoadCorrupt = false; LastCorruptDetail = ""; corruptLogged = false;
                return $"⚠ recovery from {label} REFUSED: the source is readable now (fixed since the banner appeared?) — nothing was overwritten. Refresh to load it.";
            }
            var outcome = CheckedReplace.Write(SourcePath, current, candidateJson, out string note);
            if (note != null) Debug.LogWarning($"{tag} registry source: {note}.");
            if (outcome == CheckedReplace.Outcome.Conflict) return $"⚠ recovery from {label} REFUSED: the source changed while it was being restored — that version is in place; look at it first, then retry.";
            if (outcome == CheckedReplace.Outcome.Unresolved) return $"⚠ recovery from {label} could not be settled — inspect the source and the copies named in the Console.";
            EditorPrefs.SetString(PrefLastWrite, Fingerprint(candidateJson));
            LastLoadCorrupt = false; LastCorruptDetail = ""; corruptLogged = false;
            Deploy(candidateJson);   // the deployed copy follows the recovered source (else the next load calls the difference a hand-edit)
            AssetDatabase.Refresh();
            return $"Recovered {count(r)} {noun} from {label}. The corrupt copy is preserved beside the source for hand-merging.";
        }
        catch (Exception e) { return $"⚠ recovery from {label} FAILED: {e.Message} (source untouched)."; }
    }

    // =================================================================================================== INTERNALS
    // One-time migration: until the marker is set, the DEPLOYED copy was the historical authority — adopt it into the
    // project file if they differ in CONTENT, unless the source is the NEWER of the two (then it is what a human or git
    // touched last; never overwrite newer data with older). The loser is preserved beside the artifact. A copy that fails
    // the shape rule never wins (critical review 2026-09-30: a deployed `{}` went over a valid source). The marker is
    // per PROJECT source path - EditorPrefs span every project on the machine, and a machine-wide marker left a second
    // project's registry never migrated; the old machine-wide marker still counts, so nothing migrates twice.
    string MigrationMarker => prefKey + "|" + SourcePath;

    void MigrateOnce()
    {
        if (EditorPrefs.GetBool(MigrationMarker, false) || EditorPrefs.GetBool(prefKey, false)) return;
        try
        {
            if (File.Exists(ArtifactPath))
            {
                string dep = File.ReadAllText(ArtifactPath);
                bool depOk = Parse(dep, out string depWhy) != null;
                if (!File.Exists(SourcePath))
                {
                    if (depOk && MissingSourceBlock() == null)
                    {
                        WriteAtomic(SourcePath, dep);
                        EditorPrefs.SetString(PrefLastWrite, Fingerprint(dep));
                        Debug.Log($"{tag} registry collapse migration: adopted the deployed file into the project source (the deployed copy was authoritative until now; from now on it is a build artifact).");
                    }
                    else if (!depOk) Debug.LogWarning($"{tag} registry collapse migration: the deployed file is unreadable ({depWhy}) — not adopted; there is no project source yet.");
                }
                else
                {
                    string src = File.ReadAllText(SourcePath);
                    bool srcOk = Parse(src, out _) != null;
                    if (Norm(src) != Norm(dep))
                    {
                        // the shape rule first: a broken copy never wins; between two readable ones, the newer does
                        bool keepSource = !depOk || (srcOk && File.GetLastWriteTimeUtc(SourcePath) > File.GetLastWriteTimeUtc(ArtifactPath));
                        string loser = ArtifactPath + ".pre-collapse.json";
                        try { File.WriteAllText(loser, keepSource ? dep : src); } catch { }
                        if (keepSource)
                            Debug.LogWarning($"{tag} registry collapse migration: kept the project source ({(depOk ? "it is NEWER than the deployed copy" : "the deployed copy is unreadable")}); the deployed content is preserved as '{Path.GetFileName(loser)}'.");
                        else
                        {
                            WriteAtomic(SourcePath, dep);
                            EditorPrefs.SetString(PrefLastWrite, Fingerprint(dep));
                            Debug.Log($"{tag} registry collapse migration: adopted the deployed file into the project source ({(srcOk ? "it was newer" : "the source was unreadable")}; now a build artifact). The previous source content is preserved as '{Path.GetFileName(loser)}'.");
                        }
                    }
                }
            }
            EditorPrefs.SetBool(MigrationMarker, true);
        }
        catch (Exception e) { Debug.LogWarning($"{tag} registry collapse migration failed (will retry next load): " + e.Message); }
    }

    // Keep the deployed ARTIFACT in step: recreate it when missing, FINISH a deploy that is still owed (it is not a
    // hand-edit), and warn ONCE when it really was hand-edited.
    void SyncArtifact(string sourceJson)
    {
        try
        {
            if (!File.Exists(ArtifactPath))
            {
                WriteAtomic(ArtifactPath, sourceJson);
                EditorPrefs.DeleteKey(PrefPendingDeploy);
                notice = $"Deployed artifact was missing — recreated it from the project source ({Path.GetFileName(ArtifactPath)}).";
                Debug.Log($"{tag} deployed registry artifact recreated from the project source → {ArtifactPath}");
                return;
            }
            string pending = EditorPrefs.GetString(PrefPendingDeploy, "");
            if (pending != "")
            {
                double now = EditorApplication.timeSinceStartup;
                if (!RegistryRules.PendingRetryDue(now, lastPendingAttempt, pendingFailures)) return;   // not on every load
                lastPendingAttempt = now;
                if (pending == Fingerprint(sourceJson))
                {
                    // the source is exactly the change whose deploy is owed: finish it
                    try
                    {
                        WriteAtomic(ArtifactPath, sourceJson);
                        EditorPrefs.DeleteKey(PrefPendingDeploy);
                        pendingRetryWarned = false; pendingFailures = 0; lastPendingAttempt = -1;
                        notice = $"Finished a deploy an earlier save couldn't complete ({Path.GetFileName(ArtifactPath)}).";
                        Debug.Log($"{tag} finished a deploy an earlier save couldn't complete → {ArtifactPath}");
                    }
                    catch (Exception re)
                    {
                        pendingFailures++;
                        if (!pendingRetryWarned) { pendingRetryWarned = true; Debug.LogWarning($"{tag} the deployed copy is still out of date (the last save couldn't refresh it: {re.Message}); later loads keep retrying, backing off to every 30 s."); }
                    }
                    return;
                }
                EditorPrefs.DeleteKey(PrefPendingDeploy); pendingFailures = 0; lastPendingAttempt = -1;   // the source is something else (git, a hand-edit, an unsettled write that didn't land): that deploy is moot
            }
            if (!driftWarned && Norm(File.ReadAllText(ArtifactPath)) != Norm(sourceJson))
            {
                driftWarned = true;
                Debug.LogWarning($"{tag} the DEPLOYED file differs from the project source. The deployed copy is a BUILD ARTIFACT — a hand-edit there is ignored by the editor and overwritten on the next save. Edit the source instead: {SourcePath}");
            }
        }
        catch (Exception e) { Debug.LogWarning($"{tag} deployed-artifact sync: " + e.Message); }
    }

    /// <summary>The version of <paramref name="text"/> as LoadedVersion states it (NoFile for a missing file).</summary>
    public static string VersionOf(string text) => text == null ? NoFile : Fingerprint(text);

    static string Fingerprint(string json)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(RegistryRules.FingerprintText(json))));   // CRLF/LF-blind
    }

    static string Pinpoint(string path)
    {
        try { return PinpointText(File.ReadAllText(path)); }
        catch (Exception ex) { return ex.Message; }
    }

    // JsonUtility's exceptions carry no location; Newtonsoft's reader names the line and column.
    static string PinpointText(string json)
    {
        try { Newtonsoft.Json.Linq.JObject.Parse(json ?? ""); return "JsonUtility rejected it but Newtonsoft parses it (structure beyond JsonUtility's subset?)"; }
        catch (Newtonsoft.Json.JsonReaderException jre) { return $"line {jre.LineNumber}, position {jre.LinePosition}: {jre.Message}"; }
        catch (Exception ex) { return ex.Message; }
    }

    static string Norm(string s) => s?.Replace("\r\n", "\n");

    // Atomic write for the DEPLOYED copy and migration (the editor owns them; the source's changes go through
    // CheckedReplace). A unique temp name, removed on failure.
    static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }
}
