// SingleSourceRegistry.cs — the ONE-file registry engine, shared (2026-08-20).
//
// The 2026-08-19 collapse gave the UNIT registry this shape: the git-tracked PROJECT file is THE registry; the
// deployed copy under BepInEx/config is a BUILD ARTIFACT regenerated on every Save (recreated on Load if missing,
// a hand-edit there warned about once); a corrupt source is PINPOINTED (line/column via Newtonsoft), preserved
// timestamped, logged once, Save-locked, and recoverable in one click from the last deploy or the last commit.
// DistrictRegistry and FormationRegistry still ran the OLD two-file pattern with none of that. Rather than two more
// hand-copies of ModelRegistry (which keeps its own implementation because of its pack-header merge semantics and
// stays the reference), the machinery lives HERE once and the two registries are thin typed shells over it.
//
// Two rules the shared engine adds over the unit registry's first cut:
//   * migration never overwrites a NEWER source with an older deployed copy (the loser is preserved either way);
//   * content comparisons are CRLF-normalized — a line-ending difference is not a hand-edit.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public class SingleSourceRegistry<TFile> where TFile : class, new()
{
    readonly string tag, prefKey, gitRel, noun;
    readonly Func<string> sourcePath, artifactPath;
    readonly Func<TFile, int> count;          // entries in a parsed file — recovery/adoption candidates must hold >= 1
    bool corruptLogged, driftWarned;          // once per corruption / once per domain load (windows poll Load())

    public string SourcePath => sourcePath();
    public string ArtifactPath => artifactPath();
    public bool LastLoadCorrupt { get; private set; }
    public string LastCorruptDetail { get; private set; } = "";

    // A self-healing action (artifact recreated, source adopted, migration) is otherwise a Console-only event —
    // invisible to the person who just pressed Refresh (drill 2026-08-20: "nothing happens, proof it does not
    // work"). The window takes the notice and shows it in its status line.
    string notice = "";
    public string TakeNotice() { var n = notice; notice = ""; return n; }

    // tag "[District]"; sourcePath = the git-tracked project file; artifactPath = the deployed file the game reads
    // (lazy: ConfigDir is resolved at call time); prefKey = one-time migration marker; gitRel = repo-relative source
    // path for `git checkout`; noun = "district entries" for messages.
    public SingleSourceRegistry(string tag, Func<string> sourcePath, Func<string> artifactPath, Func<TFile, int> count,
                                string prefKey, string gitRel, string noun)
    {
        this.tag = tag; this.sourcePath = sourcePath; this.artifactPath = artifactPath; this.count = count;
        this.prefKey = prefKey; this.gitRel = gitRel; this.noun = noun;
    }

    public TFile Load()
    {
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
                            var d = JsonUtility.FromJson<TFile>(dep);
                            if (d != null && count(d) > 0)
                            {
                                WriteAtomic(SourcePath, dep);
                                notice = $"Project registry source was missing — adopted {count(d)} {noun} from the deployed artifact.";
                                Debug.Log($"{tag} project registry source was missing — adopted {count(d)} {noun} from the deployed artifact ({ArtifactPath}).");
                                return d;
                            }
                        }
                        catch (Exception be) { Debug.LogWarning($"{tag} the deployed artifact '{ArtifactPath}' is unreadable ({be.Message}) — treating as absent."); }
                    }
                    return new TFile();
                }
            }
            var json = File.ReadAllText(SourcePath);
            var data = JsonUtility.FromJson<TFile>(json) ?? new TFile();
            LastLoadCorrupt = false; corruptLogged = false;
            SyncArtifact(json);
            return data;
        }
        catch (Exception e)
        {
            // The source exists but won't parse. Preserve it, pinpoint the fault, flag it so Save() won't clobber it.
            LastLoadCorrupt = true;
            LastCorruptDetail = Pinpoint(SourcePath) ?? e.Message;
            if (!corruptLogged)   // one Console error per corruption, not per Load() poll
            {
                corruptLogged = true;
                string keep = SourcePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
                try { File.Copy(SourcePath, keep, true); } catch { }
                Debug.LogError($"{tag} registry source '{SourcePath}' is unreadable — {LastCorruptDetail}. " +
                               $"Preserved as '{Path.GetFileName(keep)}'. The window shows one-click recovery " +
                               "(restore the last deploy, or the last git commit). Saving is locked until recovered.");
            }
            return new TFile();
        }
    }

    // True = the SOURCE was written (the artifact refresh is loud-but-non-fatal). False = nothing saved — surface it.
    public bool Save(TFile file, string whatFailed)
    {
        if (LastLoadCorrupt)
        {
            Debug.LogError($"{tag} not saving: the registry source was unreadable — recover it first (the window shows the buttons). Refusing to overwrite it and lose your entries.");
            return false;
        }
        var json = JsonUtility.ToJson(file, true);
        try { WriteAtomic(SourcePath, json); }
        catch (Exception e)
        {
            Debug.LogError($"{tag} registry write FAILED — {whatFailed} was NOT saved to '{SourcePath}' ({e.Message}). " +
                           "Close whatever's locking it (AV, indexer) and try again; the previous source is intact (git has every committed version).");
            return false;
        }
        try { WriteAtomic(ArtifactPath, json); }
        catch (Exception e)
        {
            Debug.LogWarning($"{tag} deployed-artifact refresh FAILED ({e.Message}) — the registry SOURCE saved fine, but the " +
                             $"running game keeps reading the stale '{ArtifactPath}' until the next successful Save/Load regenerates it.");
        }
        AssetDatabase.Refresh();
        return true;
    }

    // ---- recovery (each candidate VALIDATED — must parse and hold >= 1 entry — before it overwrites the source) ----
    public string RecoverFromArtifact()
    {
        if (!File.Exists(ArtifactPath)) return "⚠ no deployed artifact exists to recover from.";
        try { return RecoverSourceFrom(File.ReadAllText(ArtifactPath), "the deployed artifact (last good deploy)"); }
        catch (Exception e) { return "⚠ could not read the deployed artifact: " + e.Message; }
    }

    public string RecoverFromGit()
    {
        try
        {
            string projRoot = Directory.GetParent(Application.dataPath).FullName;
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"-C \"{projRoot}\" checkout -- \"{gitRel}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                if (p.ExitCode != 0) return "⚠ git recovery FAILED: " + (string.IsNullOrWhiteSpace(err) ? ("exit " + p.ExitCode) : err.Trim());
            }
            return RecoverSourceFrom(File.ReadAllText(SourcePath), "git (last committed version)");
        }
        catch (Exception e) { return "⚠ git recovery FAILED: " + e.Message + " (is git installed?)"; }
    }

    string RecoverSourceFrom(string candidateJson, string label)
    {
        try
        {
            var r = JsonUtility.FromJson<TFile>(candidateJson);
            if (r == null || count(r) == 0) return $"⚠ recovery from {label} REFUSED: candidate holds no {noun} (nothing was overwritten).";
            WriteAtomic(SourcePath, candidateJson);
            LastLoadCorrupt = false; LastCorruptDetail = ""; corruptLogged = false;
            AssetDatabase.Refresh();
            return $"Recovered {count(r)} {noun} from {label}. The corrupt copy is preserved beside the source for hand-merging.";
        }
        catch (Exception e) { return $"⚠ recovery from {label} FAILED: {e.Message} (source untouched)."; }
    }

    // ---- internals ----
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
                    Debug.Log($"{tag} registry collapse migration: adopted the deployed file into the project source (the deployed copy was authoritative until 2026-08-20; from now on it is a build artifact).");
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
                            Debug.Log($"{tag} registry collapse migration: adopted the deployed file into the project source (authoritative until 2026-08-20; now a build artifact). The previous source content is preserved as '{Path.GetFileName(loser)}'.");
                        }
                    }
                }
            }
            EditorPrefs.SetBool(prefKey, true);
        }
        catch (Exception e) { Debug.LogWarning($"{tag} registry collapse migration failed (will retry next load): " + e.Message); }
    }

    // Keep the deployed ARTIFACT in step: recreate it when missing, warn ONCE when it was hand-edited.
    void SyncArtifact(string sourceJson)
    {
        try
        {
            if (!File.Exists(ArtifactPath))
            {
                WriteAtomic(ArtifactPath, sourceJson);
                notice = $"Deployed artifact was missing — recreated it from the project source ({Path.GetFileName(ArtifactPath)}).";
                Debug.Log($"{tag} deployed registry artifact recreated from the project source → {ArtifactPath}");
                return;
            }
            if (!driftWarned && Norm(File.ReadAllText(ArtifactPath)) != Norm(sourceJson))
            {
                driftWarned = true;
                Debug.LogWarning($"{tag} the DEPLOYED file differs from the project source. The deployed copy is a BUILD ARTIFACT — a hand-edit there is ignored by the editor and overwritten on the next Save. Edit the source instead: {SourcePath}");
            }
        }
        catch (Exception e) { Debug.LogWarning($"{tag} deployed-artifact sync: " + e.Message); }
    }

    static string Pinpoint(string path)
    {
        try { Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)); return "JsonUtility rejected it but Newtonsoft parses it (structure beyond JsonUtility's subset?)"; }
        catch (Newtonsoft.Json.JsonReaderException jre) { return $"line {jre.LineNumber}, position {jre.LinePosition}: {jre.Message}"; }
        catch (Exception ex) { return ex.Message; }
    }

    static string Norm(string s) => s?.Replace("\r\n", "\n");

    static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }
}
