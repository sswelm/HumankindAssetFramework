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
//   * AN EMPTY SOURCE BESIDE A DEPLOY THAT STILL HOLDS ENTRIES is its own state (not written that way by this editor):
//     no change goes through until the person decides - restore the deploy, or confirm the registry is really empty.
//   * EVERY CHANGE IS A CHECKED OPERATION on the file as it is at write time (CheckedReplace.Apply): it refuses an
//     unreadable or broken source, refuses to write over a source that is empty while the deploy still holds entries
//     (RegistryRules.JudgeEmptySource, with the editor's own last-write fingerprint), re-applies itself to another
//     writer's version on a conflict, and says what happened (RegistryRules.SaveOutcome).
//   * A DEPLOY THAT IS OWED IS FINISHED LATER (PENDING fingerprint + RegistryRules.PendingRetryDue), including after a
//     write that couldn't be settled. A deploy that is merely OLDER than a source changed outside the editor (git, a
//     hand edit) is said as such - "the game still reads the older copy" - and deployed only on the person's word (the
//     old deploy is the "Restore last deploy" candidate, so it is never overwritten on a guess).
//   * RECOVERY JUDGES BEFORE IT WRITES: the committed text is read with `git show`, never checked out over the file;
//     a source that is readable NOW with entries (fixed by hand since the banner appeared) is never recovered over;
//     the deploy follows a recovery.
//   * MIGRATION (the one-time adoption of the pre-collapse deploy) happens once per DEPLOY - the deployed file sits in
//     the game's config and every project on the machine shares it, so it belongs to no one project - never adopts a
//     copy that fails the shape rule, keeps the newer of two readable copies, preserves the loser under a unique name
//     BEFORE anything is replaced (and replaces nothing if that fails), and deploys the source when the source wins.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// What one Load() found, frozen (review of PR #103): the engine's own flags change whenever ANY caller loads (a unit
/// bake, Ship Status, the Factory's Remove dialog), so a window keeps the verdict of ITS load and draws and gates on it.
/// </summary>
public sealed class RegistryLoadVerdict
{
    public bool Corrupt, Locked, NoCopy, MissingKnown, EmptyButDeployed, Stale, DeployHandEdited;
    public int DeployedCount;   // EmptyButDeployed: entries the deploy still holds (-1 = it can't be checked)
    public string LoadedVersion;   // the source's version as this load read it (null after a failed load): AcceptSource writes only over it
    public string CorruptDetail = "", LockDetail = "", LockAdvice = "", MissingDetail = "";
    /// <summary>The list this load returned can't be trusted as the registry's content: changes are refused.</summary>
    public bool Failed => Corrupt || Locked || NoCopy || EmptyButDeployed;
}

public class SingleSourceRegistry<TFile> where TFile : class, new()
{
    readonly string tag, prefKey, gitRel, noun, arrayKey;
    readonly Func<string> sourcePath, artifactPath;
    readonly Func<TFile, int> count;          // entries in a parsed file — recovery/adoption candidates must hold >= 1
    bool corruptLogged, staleWarned, handEditWarned, pendingRetryWarned, postponedWarned;   // once per corruption / domain load / failing deploy / postponement
    string migrationPostponed;   // why the one-time migration could not finish (the loser couldn't be preserved): saves refuse meanwhile
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
    public bool LastLoadMissingKnown { get; private set; }   // ...because its .meta or git says it exists (git can restore it)
    public string LastMissingDetail { get; private set; } = "";
    public bool LastLoadEmptyButDeployed { get; private set; }   // source empty (not by this editor), the deploy isn't
    public int LastDeployedCount { get; private set; }
    public bool LastLoadStale { get; private set; }       // the source changed outside the editor; the game reads an older deploy
    public bool LastLoadDeployHandEdited { get; private set; }
    /// <summary>The last Load()'s list can't be trusted as the registry's content — its "no entries" proves nothing.</summary>
    public bool LastLoadFailed => LastLoadCorrupt || LastLoadLocked || LastLoadNoCopy || LastLoadEmptyButDeployed;
    public string LastLoadProblem =>
        LastLoadCorrupt ? "is unreadable — " + LastCorruptDetail
      : LastLoadLocked ? "can't be read right now — " + LastLockDetail
      : LastLoadNoCopy ? "is missing — " + LastMissingDetail
      : LastLoadEmptyButDeployed ? EmptyText(LastDeployedCount)
      : "";

    string EmptyText(int deployed) => deployed > 0
        ? $"holds no {noun}, but its deployed copy (what the game reads) still has {deployed}"
        : $"holds no {noun}, and its deployed copy can't be checked, so it may still hold them";

    /// <summary>The last Load()'s findings, frozen — see RegistryLoadVerdict.</summary>
    public RegistryLoadVerdict Snapshot() => new RegistryLoadVerdict
    {
        Corrupt = LastLoadCorrupt, Locked = LastLoadLocked, NoCopy = LastLoadNoCopy, MissingKnown = LastLoadMissingKnown,
        EmptyButDeployed = LastLoadEmptyButDeployed, Stale = LastLoadStale, DeployHandEdited = LastLoadDeployHandEdited,
        DeployedCount = LastDeployedCount, LoadedVersion = LoadedVersion, CorruptDetail = LastCorruptDetail, LockDetail = LastLockDetail,
        LockAdvice = LastLockAdvice, MissingDetail = LastLoadNoCopy ? LastMissingDetail : LastLoadEmptyButDeployed ? EmptyText(LastDeployedCount) : "",
    };

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

    // What the editor itself last wrote (or the person accepted) as the source, and a deploy that is still owed.
    string PrefLastWrite => "HAF.Registry.LastWrite|" + SourcePath;
    string PrefPendingDeploy => "HAF.Registry.PendingDeploy|" + SourcePath;

    // Seams for the drill (tools/registry-engine-drill), which has to make a write end UNSETTLED and a preserving copy
    // FAIL to test what follows - no real file system does either on demand.
    internal delegate CheckedReplace.Outcome ApplyFn(string path, Func<string, string> change, int attempts, out string note);
    internal static ApplyFn ApplyImpl = (string p, Func<string, string> c, int a, out string n) => CheckedReplace.Apply(p, c, a, out n);
    internal static Action<string, string> WriteCopyImpl = File.WriteAllText;

    // ======================================================================================================== LOAD

    public TFile Load()
    {
        // every Load decides them afresh
        LastLoadLocked = false; LastLoadNoCopy = false; LastLoadMissingKnown = false; LastMissingDetail = "";
        LastLoadEmptyButDeployed = false; LastDeployedCount = 0; LastLoadStale = false; LastLoadDeployHandEdited = false;
        LoadedVersion = null;   // set below only where a read succeeded
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
                    string block = MissingSourceBlock(out bool known);
                    if (block != null) { LastLoadNoCopy = true; LastLoadMissingKnown = known; LastMissingDetail = block; return new TFile(); }
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
            if (count(data) == 0 && !OwnWrite(json))
            {
                // EMPTY, and not by this editor's hand (a teammate's commit, a pull, a hand edit): if the deploy still holds
                // entries, the person decides - restore them, or confirm it is really empty. No change goes through meanwhile.
                int deployed = DeployedCount(out _);
                if (deployed != 0) { LastLoadEmptyButDeployed = true; LastDeployedCount = deployed; return data; }
            }
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
    // the file still exists (Unity's .meta, or git: another program may be saving it by rename). known = the evidence
    // (git can restore it: the window offers "Restore last commit").
    string MissingSourceBlock(out bool known)
    {
        known = false;
        string[] kept;
        try { kept = CheckedReplace.Preserved(SourcePath); } catch { kept = new string[0]; }
        if (kept.Length > 0)
            return $"copies an unsettled save kept beside it ({string.Join(", ", Array.ConvertAll(kept, Path.GetFileName))}) may hold its newest version — compare them, rename the right one back to '{Path.GetFileName(SourcePath)}', then refresh";
        string existed = CheckedReplace.ExistedBefore(SourcePath);
        if (existed == null) return null;
        known = existed.StartsWith("git") || CheckedReplace.GitTracks(SourcePath) == true;   // "Restore last commit" only when git has it
        // The deletion advice must not go in a circle (review of PR #103): with the deploy still there, the next load
        // would simply adopt it back - so dropping a registry means dropping its deployed copy too.
        return $"but {existed}, so it existed — another program may be saving it (it comes back by itself; refresh in a moment){(known ? ", or git can restore it (Restore last commit)" : "")}. " +
               $"If you deleted it on purpose: delete its deployed copy '{ArtifactPath}' too (or it is adopted back), and " +
               (existed.StartsWith("git") ? "commit the deletion." : "let Unity refresh (or delete its .meta).");
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
        if (migrationPostponed != null)
        {
            Debug.LogError($"{tag} not saving {what}: the one-time migration of this registry is postponed ({migrationPostponed}) — a save now could overwrite the copy it hasn't preserved yet. Free the disk and refresh.");
            return RegistryRules.SaveOutcome.Refused;
        }
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
                    string block = MissingSourceBlock(out _);
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
                        bool writtenByEditor = OwnWrite(text);
                        string deployedWhy = null;
                        int deployed = writtenByEditor ? 0 : DeployedCount(out deployedWhy);
                        var verdict = RegistryRules.JudgeEmptySource(true, 0, writtenByEditor, deployed);
                        if (verdict == RegistryRules.EmptySourceVerdict.RefuseDeployedHasModels)
                        { refusal = $"the source holds no {noun}, but the deployed copy still has {deployed}. In the window: 'Restore last deploy' brings them back; 'Keep it empty' confirms the registry really is empty (the game's copy is emptied too)"; return null; }
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

    /// <summary>
    /// The person's word: the SOURCE as it is now is the registry — deploy it to the game (the replaced deployed copy is
    /// preserved beside it under a unique name) and record it as accepted, so an EMPTY source passes the empty-source
    /// guard from now on. The window's "Keep it empty" and "Deploy the source" buttons. Refuses a source it can't read.
    /// </summary>
    public string AcceptSource(string loadedVersion)
    {
        try
        {
            string text = File.ReadAllText(SourcePath);
            if (loadedVersion != null && VersionOf(text) != loadedVersion)
                return "⚠ REFUSED: the source changed since the window loaded it (a pull, another window) — what you saw is not what would be deployed. Refresh, look again, then decide.";
            var f = Parse(text, out string why);
            if (f == null) return $"⚠ REFUSED: the source is unreadable ({why}); nothing was deployed.";
            string kept = DeployPreserving(text);
            EditorPrefs.SetString(PrefLastWrite, Fingerprint(text));
            return $"Deployed the source ({count(f)} {noun}) to the game." + (kept != null ? $" The replaced deployed copy is kept as '{Path.GetFileName(kept)}'." : "");
        }
        catch (Exception e) { return $"⚠ FAILED: {e.Message} — nothing was deployed."; }
    }

    // THE DEPLOY IS OVERWRITTEN ONLY BY THE EDITOR'S OWN SAVE, OR AFTER A COPY IS KEPT (review of PR #103, round 3:
    // "Restore last commit" deployed the committed version over a deploy holding every bake since, and no file kept
    // them). Recovery, migration and "Deploy the source" all go through here: a differing deploy is preserved first
    // under a unique name; if that fails, nothing is deployed (throws). Returns the copy's path, or null.
    string DeployPreserving(string json)
    {
        string kept = null;
        if (File.Exists(ArtifactPath))
        {
            string dep = File.ReadAllText(ArtifactPath);
            if (Norm(dep) != Norm(json))
            {
                kept = UniqueCopy(ArtifactPath, "replaced");
                WriteCopyImpl(kept, dep);   // throws: nothing is deployed over an unpreserved copy
            }
        }
        WriteAtomic(ArtifactPath, json);
        EditorPrefs.DeleteKey(PrefPendingDeploy);
        pendingFailures = 0; lastPendingAttempt = -1;
        return kept;
    }

    // Is this text what the editor itself wrote (or the person accepted) — its last write, or the write whose deploy is
    // still owed (review of PR #103, round 3: an unsettled write that emptied the registry read as "emptied outside
    // the editor" and could never finish its deploy)?
    bool OwnWrite(string text)
    {
        string fp = Fingerprint(text);
        return fp == EditorPrefs.GetString(PrefLastWrite, "") || fp == EditorPrefs.GetString(PrefPendingDeploy, "");
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
    // source that is readable NOW with entries is never recovered over - the banner may be older than a hand fix.

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
            var now = current == null ? null : Parse(current, out _);
            if (current != null && now == null)
            {
                // a broken source no Load saw (a half-typed hand edit): the message promises a preserved copy - keep it
                string keep = SourcePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
                try { WriteCopyImpl(keep, current); }
                catch (Exception ce) { return $"⚠ recovery from {label} REFUSED: the current (broken) source could not be preserved first ({ce.Message}); nothing was overwritten."; }
            }
            if (now != null && count(now) > 0)
            {
                // READABLE NOW, WITH ENTRIES (critical review 2026-09-30): the source was fixed by hand after the banner
                // appeared - the windows don't reload on focus. Recovering would overwrite that fix, and no copy of it would
                // remain. (An EMPTY readable source is the one "Restore last deploy" exists for: see EmptyButDeployed.)
                LastLoadCorrupt = false; LastCorruptDetail = ""; corruptLogged = false;
                return $"⚠ recovery from {label} REFUSED: the source is readable now and holds {count(now)} {noun} (fixed since the banner appeared?) — nothing was overwritten. Refresh to load it.";
            }
            var outcome = CheckedReplace.Write(SourcePath, current, candidateJson, out string note);
            if (note != null) Debug.LogWarning($"{tag} registry source: {note}.");
            if (outcome == CheckedReplace.Outcome.Conflict) return $"⚠ recovery from {label} REFUSED: the source changed while it was being restored — that version is in place; look at it first, then retry.";
            if (outcome == CheckedReplace.Outcome.Unresolved) return $"⚠ recovery from {label} could not be settled — inspect the source and the copies named in the Console.";
            EditorPrefs.SetString(PrefLastWrite, Fingerprint(candidateJson));
            LastLoadCorrupt = false; LastCorruptDetail = ""; corruptLogged = false; LastLoadEmptyButDeployed = false;
            // the deployed copy follows the recovered source - preserved first: it may hold bakes made since the commit
            string kept = null;
            try { kept = DeployPreserving(candidateJson); }
            catch (Exception de) { EditorPrefs.SetString(PrefPendingDeploy, Fingerprint(candidateJson)); lastPendingAttempt = -1; pendingFailures = 0; Debug.LogWarning($"{tag} recovered the source, but the deployed copy couldn't be refreshed yet ({de.Message}); the next load retries."); }
            AssetDatabase.Refresh();
            return $"Recovered {count(r)} {noun} from {label}. The corrupt copy (if any) is preserved beside the source for hand-merging." +
                   (kept != null ? $" The replaced deployed copy is kept as '{Path.GetFileName(kept)}'." : "");
        }
        catch (Exception e) { return $"⚠ recovery from {label} FAILED: {e.Message} (source untouched)."; }
    }

    // =================================================================================================== INTERNALS
    // One-time migration: until the marker is set, the DEPLOYED copy was the historical authority. ONCE PER DEPLOY (review
    // of PR #103): the deployed file is in the game's config, shared by every project on the machine, so a marker per
    // project let project B take project A's registry over its own source. A project that has no source still gets the
    // deploy - Load() adopts it; one that has its own source keeps it. The old machine-wide marker counts too.
    string MigrationMarker => prefKey + "|" + ArtifactPath;

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
                    if (depOk && MissingSourceBlock(out _) == null)
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
                        // PRESERVE THE LOSER FIRST, under a unique name (review of PR #103: a fixed name was overwritten by the
                        // next project's migration, and a failed copy went unnoticed while the only copy was replaced). If it
                        // can't be preserved, nothing is replaced and no marker is set - the next load tries again.
                        string loser = UniqueCopy(ArtifactPath, "pre-collapse");
                        try { WriteCopyImpl(loser, keepSource ? dep : src); }
                        catch (Exception ce)
                        {
                            migrationPostponed = ce.Message;   // saves refuse until it goes through
                            if (!postponedWarned) { postponedWarned = true; Debug.LogWarning($"{tag} registry collapse migration postponed: the losing copy could not be preserved ({ce.Message}) — nothing was replaced and saves are refused; every load retries."); }
                            return;
                        }
                        migrationPostponed = null; postponedWarned = false;
                        if (keepSource)
                        {
                            bool emptyOverFull = srcOk && count(Parse(src, out _)) == 0 && depOk && count(Parse(dep, out _)) > 0;
                            if (srcOk && !emptyOverFull)
                            {
                                // the source won: the game gets it too (its old deploy is the loser, preserved above)
                                try { WriteAtomic(ArtifactPath, src); EditorPrefs.DeleteKey(PrefPendingDeploy); }
                                catch (Exception de) { EditorPrefs.SetString(PrefPendingDeploy, Fingerprint(src)); lastPendingAttempt = -1; pendingFailures = 0; Debug.LogWarning($"{tag} migration kept the source but couldn't deploy it yet ({de.Message}); the next load retries."); }
                                EditorPrefs.SetString(PrefLastWrite, Fingerprint(src));
                            }
                            Debug.LogWarning($"{tag} registry collapse migration: kept the project source ({(depOk ? "it is NEWER than the deployed copy" : "the deployed copy is unreadable")})" +
                                             (emptyOverFull ? " — it is EMPTY while the deployed copy has entries, so the game keeps the deployed copy until you decide (the window asks)" : srcOk ? " and deployed it" : "") +
                                             $"; the deployed content is preserved as '{Path.GetFileName(loser)}'.");
                        }
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

    // Keep the deployed ARTIFACT in step: recreate it when missing, FINISH a deploy that is still owed, and otherwise say
    // WHICH side moved - the source (git, a hand edit: the game still reads the older copy until a save or "Deploy the
    // source") or the deploy (hand-edited there: ignored, overwritten on the next save).
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
                        EditorPrefs.SetString(PrefLastWrite, Fingerprint(sourceJson));   // it is the editor's write, finished
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
                EditorPrefs.DeleteKey(PrefPendingDeploy); pendingFailures = 0; lastPendingAttempt = -1;   // the source is something else: that deploy is moot
            }
            if (Norm(File.ReadAllText(ArtifactPath)) == Norm(sourceJson)) return;
            if (!OwnWrite(sourceJson))
            {
                // THE SOURCE MOVED (git, a hand edit, another writer): the game still reads the older deploy. Said as that,
                // not as a hand-edit (review of PR #103), and deployed only on the person's word - the old deploy is the
                // "Restore last deploy" candidate, so a bad merge must not overwrite it on a guess.
                LastLoadStale = true;
                if (!staleWarned) { staleWarned = true; Debug.LogWarning($"{tag} the project source changed outside the editor (git, a hand edit): the GAME still reads the older deployed copy '{ArtifactPath}'. The next save that changes something deploys it, or use the window's 'Deploy the source'."); }
            }
            else
            {
                LastLoadDeployHandEdited = true;
                if (!handEditWarned) { handEditWarned = true; Debug.LogWarning($"{tag} the DEPLOYED file differs from the project source the editor wrote. The deployed copy is a BUILD ARTIFACT — a hand-edit there is ignored by the editor and overwritten on the next save. Edit the source instead: {SourcePath}"); }
            }
        }
        catch (Exception e) { Debug.LogWarning($"{tag} deployed-artifact sync: " + e.Message); }
    }

    /// <summary>The version of <paramref name="text"/> as LoadedVersion states it (NoFile for a missing file).</summary>
    public static string VersionOf(string text) => text == null ? NoFile : Fingerprint(text);

    static string UniqueCopy(string path, string kind) =>
        path + "." + kind + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json";

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
