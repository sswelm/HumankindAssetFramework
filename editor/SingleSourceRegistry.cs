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
//     buttons - "Restore last commit" would discard uncommitted work in a file with nothing wrong in it), and it
//     supersedes an earlier corrupt verdict (RegistryRules.ShowRecoveryControls).
//   * NO COPY: a missing source whose deployed copy can't be read is LastLoadNoCopy - failed, not empty.
//   * EVERY CHANGE IS A CHECKED OPERATION on the file as it is at write time (CheckedReplace.Apply): it refuses an
//     unreadable or broken source, refuses to write over a source that is empty while the deploy still holds entries
//     (RegistryRules.JudgeEmptySource, with the editor's own last-write fingerprint), re-applies itself to another
//     writer's version on a conflict, and says what happened (RegistryRules.SaveOutcome).
//   * A FAILED DEPLOY IS FINISHED LATER (PENDING fingerprint + RegistryRules.PendingRetryDue), not called a hand-edit.
//   * RECOVERY JUDGES BEFORE IT WRITES: the committed text is read with `git show`, never checked out over the file.
// Plus the engine's own rules from 2026-08-20: migration never overwrites a NEWER source with an older deployed copy
// (the loser is preserved either way), and content comparisons are CRLF-normalized.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public class SingleSourceRegistry<TFile> where TFile : class, new()
{
    readonly string tag, prefKey, gitRel, noun, arrayKey;
    readonly Func<string> sourcePath, artifactPath;
    readonly Func<TFile, int> count;          // entries in a parsed file — recovery/adoption candidates must hold >= 1
    bool corruptLogged, driftWarned, pendingRetryWarned;   // once per corruption / domain load / failing deploy (windows poll Load())
    double lastPendingAttempt = -1; int pendingFailures;   // RegistryRules.PendingRetryDue: 2, 4, 8, 16 s, then every 30 s

    public string SourcePath => sourcePath();
    public string ArtifactPath => artifactPath();

    // ---- what the last Load() could and couldn't do (each Load decides all of them afresh) ----
    public bool LastLoadCorrupt { get; private set; }     // READ, and broken: the recovery banner
    public string LastCorruptDetail { get; private set; } = "";
    public bool LastLoadLocked { get; private set; }      // could not be READ at all: a plain warning, no recovery
    public string LastLockDetail { get; private set; } = "";
    public string LastLockAdvice { get; private set; } = "";
    public bool LastLoadNoCopy { get; private set; }      // source missing, deployed copy unreadable: nothing loaded
    /// <summary>The last Load() returned an empty file for want of a read — its "no entries" proves nothing.</summary>
    public bool LastLoadFailed => LastLoadCorrupt || LastLoadLocked || LastLoadNoCopy;
    public string LastLoadProblem =>
        LastLoadCorrupt ? "is unreadable — " + LastCorruptDetail
      : LastLoadLocked ? "can't be read right now — " + LastLockDetail
      : LastLoadNoCopy ? "is missing, and its deployed copy can't be read"
      : "";

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

    // What the editor itself last wrote to the source, and a deploy that failed (see ModelRegistry.PrefLastWrite).
    string PrefLastWrite => "HAF.Registry.LastWrite|" + SourcePath;
    string PrefPendingDeploy => "HAF.Registry.PendingDeploy|" + SourcePath;

    // ======================================================================================================== LOAD

    public TFile Load()
    {
        LastLoadLocked = false; LastLoadNoCopy = false;   // every Load decides them afresh
        LoadedVersion = null;                             // set below only where a read succeeded
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
                    // Source gone (fresh clone, hand-deletion) but a deployed artifact exists: ADOPT it — it is the
                    // only surviving copy of the data.
                    if (File.Exists(ArtifactPath))
                    {
                        try
                        {
                            var dep = File.ReadAllText(ArtifactPath);
                            var d = Parse(dep, out string depWhy);
                            if (d == null)
                            {
                                LastLoadNoCopy = true;   // the empty file returned below is for want of a copy, not an empty registry
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
                            LastLoadNoCopy = true;
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
                               "The window shows one-click recovery (restore the last deploy, or the last git commit). Changes are locked until recovered.");
            }
            return new TFile();
        }
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
        ChangeCore(apply, what, null, out changed);

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
        return ChangeCore(current => { CopyInto(file, current); return true; }, what, loadedVersion, out _);
    }

    RegistryRules.SaveOutcome ChangeCore(Func<TFile, bool> apply, string what, string mustStillBe, out bool changed)
    {
        changed = false;
        if (LastLoadCorrupt)
        {
            Debug.LogError($"{tag} not saving {what}: the registry source was unreadable — recover it first (the window shows the buttons). Refusing to overwrite it and lose your entries.");
            return RegistryRules.SaveOutcome.Refused;
        }
        // What the attempt found, decided afresh on every attempt (Apply re-runs the lambda after a conflict).
        string refusal = null; bool broken = false, didChange = false; string written = null;
        CheckedReplace.Outcome outcome; string note;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath));
            outcome = CheckedReplace.Apply(SourcePath, text =>
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
                    // NO SOURCE. The deployed copy is then the only surviving one: build on it, exactly as Load() adopts it.
                    if (File.Exists(ArtifactPath))
                    {
                        string dep;
                        try { dep = File.ReadAllText(ArtifactPath); }
                        catch (Exception e) { refusal = $"the source is missing and the deployed copy '{ArtifactPath}' can't be read ({e.Message}) — it may be the only copy of this registry. Close whatever holds it (the game?) and try again"; return null; }
                        file = Parse(dep, out string depWhy);
                        if (file == null) { refusal = $"the source is missing and the deployed copy '{ArtifactPath}' is unreadable ({depWhy}) — it may be the only copy of this registry"; return null; }
                    }
                    else
                    {
                        // Neither copy: a first-ever registry — unless something says the source existed (its Unity .meta,
                        // or git): then another program is replacing it, and a new file would stand where the real one
                        // belongs (the review of PR #101). Absent evidence, this is a first save.
                        string existed = CheckedReplace.ExistedBefore(SourcePath);
                        if (existed != null) { refusal = $"the source is missing, but {existed}, so it existed — another program may be saving it (it comes back by itself). Try again in a moment; if you deleted it on purpose, delete its .meta too (and commit the deletion) first"; return null; }
                        file = new TFile();
                    }
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
                if (!apply(file)) return null;   // nothing to change
                didChange = true;
                return written = JsonUtility.ToJson(file, true);
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
            Debug.LogError($"{tag} not saving {what}: the registry source '{SourcePath}' is unreadable right now — {refusal}. Refusing to overwrite it; recover it (the window shows the buttons) and try again.");
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
                Debug.LogError($"{tag} saving {what} could not be settled at '{SourcePath}' (the note above says why). The source may hold this save, another version, or be missing; " +
                               "the deployed copy was not changed. Inspect the source and the preserved copies named above, then reload before saving again.");
                return RegistryRules.SaveOutcome.Unknown;
        }
        // WRITTEN. The editor's own write is fingerprinted (the empty-source guard tells it from a hand-emptying), and the
        // deployed copy follows; a deploy that fails is finished by a later Load, not called a hand-edit.
        changed = didChange;
        EditorPrefs.SetString(PrefLastWrite, Fingerprint(written));
        Deploy(written);
        AssetDatabase.Refresh();
        return RegistryRules.SaveOutcome.Saved;
    }

    // Copy every serialized field of `from` into `into` (ReplaceAll on the file as read: its own shape, the caller's data).
    static void CopyInto(TFile from, TFile into) => JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(from), into);

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
                             $"'{ArtifactPath}' until it can be written; the editor retries by itself (every 2 s at first, backing off to every 30 s).");
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
    // preserved timestamped; and the write is checked, so a source someone changed meanwhile is not overwritten.

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
            var outcome = CheckedReplace.Write(SourcePath, current, candidateJson, out string note);
            if (note != null) Debug.LogWarning($"{tag} registry source: {note}.");
            if (outcome == CheckedReplace.Outcome.Conflict) return $"⚠ recovery from {label} REFUSED: the source changed while it was being restored — that version is in place; look at it first, then retry.";
            if (outcome == CheckedReplace.Outcome.Unresolved) return $"⚠ recovery from {label} could not be settled — inspect the source and the copies named in the Console.";
            EditorPrefs.SetString(PrefLastWrite, Fingerprint(candidateJson));
            LastLoadCorrupt = false; LastCorruptDetail = ""; corruptLogged = false;
            AssetDatabase.Refresh();
            return $"Recovered {count(r)} {noun} from {label}. The corrupt copy is preserved beside the source for hand-merging.";
        }
        catch (Exception e) { return $"⚠ recovery from {label} FAILED: {e.Message} (source untouched)."; }
    }

    // =================================================================================================== INTERNALS
    // One-time migration: until the marker is set, the DEPLOYED copy was the historical authority — adopt it into
    // the project file if they differ in CONTENT, unless the source is the NEWER of the two (then it is what a human
    // or git touched last; never overwrite newer data with older). The loser is preserved beside the artifact.
    void MigrateOnce()
    {
        if (EditorPrefs.GetBool(prefKey, false)) return;
        try
        {
            if (File.Exists(ArtifactPath))
            {
                string dep = File.ReadAllText(ArtifactPath);
                if (!File.Exists(SourcePath))
                {
                    WriteAtomic(SourcePath, dep);
                    Debug.Log($"{tag} registry collapse migration: adopted the deployed file into the project source (the deployed copy was authoritative until now; from now on it is a build artifact).");
                }
                else
                {
                    string src = File.ReadAllText(SourcePath);
                    if (Norm(src) != Norm(dep))
                    {
                        bool sourceNewer = File.GetLastWriteTimeUtc(SourcePath) > File.GetLastWriteTimeUtc(ArtifactPath);
                        string loser = ArtifactPath + ".pre-collapse.json";
                        try { File.WriteAllText(loser, sourceNewer ? dep : src); } catch { }
                        if (sourceNewer)
                            Debug.LogWarning($"{tag} registry collapse migration: the project source is NEWER than the deployed copy and differs — kept the source; the deployed content is preserved as '{Path.GetFileName(loser)}'.");
                        else
                        {
                            WriteAtomic(SourcePath, dep);
                            Debug.Log($"{tag} registry collapse migration: adopted the deployed file into the project source (authoritative until now; now a build artifact). The previous source content is preserved as '{Path.GetFileName(loser)}'.");
                        }
                    }
                }
            }
            EditorPrefs.SetBool(prefKey, true);
        }
        catch (Exception e) { Debug.LogWarning($"{tag} registry collapse migration failed (will retry next load): " + e.Message); }
    }

    // Keep the deployed ARTIFACT in step: recreate it when missing, FINISH a deploy a change couldn't complete (it is
    // not a hand-edit), and warn ONCE when it really was hand-edited.
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
                if (!RegistryRules.PendingRetryDue(now, lastPendingAttempt, pendingFailures)) return;   // not on every poll
                lastPendingAttempt = now;
                if (pending == Fingerprint(sourceJson))
                {
                    // the source is still exactly what the failed change wrote: this difference is OUR unfinished deploy
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
                        if (!pendingRetryWarned) { pendingRetryWarned = true; Debug.LogWarning($"{tag} the deployed copy is still out of date (the last save couldn't refresh it: {re.Message}); retrying — every 2 s at first, backing off to every 30 s — until it can."); }
                    }
                    return;
                }
                EditorPrefs.DeleteKey(PrefPendingDeploy); pendingFailures = 0; lastPendingAttempt = -1;   // the source changed since (git, a hand-edit): that deploy is moot
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
