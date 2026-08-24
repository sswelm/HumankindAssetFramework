// BackupAuto.cs — the AUTOMATIC half of Backup & Restore (2026-08-17, user: "auto backup, especially when I
// remove assets… assets but also configuration… go back versions"). Two independent guards, both optional
// (toggles in the Backup & Restore window), both writing into the SAME versioned, restorable backup list:
//
//   1) DELETE GUARD (default ON): before ANY asset under a protected root (FactorySource, Resources, Databases,
//      Scripts/Editor) is deleted — the Factory's Remove flow, a Project-window delete, a script — the file or
//      folder (+ .meta) is first copied to <backup root>/_deleted_<timestamp>_<name>/ with a manifest naming the
//      original path. The delete then proceeds normally; the guard NEVER blocks it, it only makes it undoable.
//
//   2) DAILY AUTO-VERSION (default ON): on the first editor load of a day (>24h since the last), a full silent
//      backup of ALL groups — assets AND configuration (registry + skins + sounds + Databases) — runs through the
//      same core the "Back up now" button uses, so it appears in the window's list with a Restore button like any
//      manual version. The offsite zip rides along if configured. RETENTION: only the newest 3 _auto_ versions are
//      kept (rotation is logged loudly); manual backups and _deleted_ snapshots are NEVER auto-deleted.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

class HafDeleteGuard : UnityEditor.AssetModificationProcessor
{
    internal const string PrefOn = "HAF.Backup.DeleteGuard";
    // Protected roots = the IRREPLACEABLE classes only. Assets/Resources is deliberately NOT here (critical-review
    // finding): the bake pipeline delete-firsts atlases/skeletons/meshes on EVERY re-bake (~30 AssetDatabase.DeleteAsset
    // sites) — guarding Resources would flood the backup root with churn folders within days, and baked assets are
    // regenerable anyway (bake again). The daily auto-version still snapshots Resources for go-back-a-version.
    static readonly string[] roots = { "Assets/FactorySource", "Assets/Databases", "Assets/Scripts/Editor" };

    static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)
    {
        try
        {
            // Default is CONTEXTUAL (see HafPackageContext): on in the home project, off in an installed package —
            // hooking a stranger's deletes is not something an authoring tool should switch on for itself.
            if (!EditorPrefs.GetBool(PrefOn, HafPackageContext.AutoDefault)) return AssetDeleteResult.DidNotDelete;
            string p = assetPath.Replace('\\', '/');
            if (!roots.Any(r => p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) || p.Equals(r, StringComparison.OrdinalIgnoreCase)))
                return AssetDeleteResult.DidNotDelete;
            // PREVIEW-SCRATCH exclusion (2026-08-17, "WTF it mentions TowedGunHowitzers_PropFit"): the Lab/Factory
            // fit-preview prefabs are delete-first REBUILT constantly ("NOT shipped; preview-only") — guarding them
            // fills the log with alarming churn for files that regenerate on the next preview. Same lesson as
            // excluding Assets/Resources: the guard protects what can't be rebuilt, not what rebuilds itself.
            string leafLower = Path.GetFileName(p).ToLowerInvariant();
            if (leafLower.EndsWith("_propfit.prefab") || leafLower.EndsWith("_preview.prefab") ||
                leafLower.EndsWith("_previewmesh.asset") || leafLower.EndsWith("_previewmat.mat"))
                return AssetDeleteResult.DidNotDelete;
            // TEST-FIXTURE exclusion (2026-08-23) — the same rule as the preview scratch above, applied to the other
            // thing that deletes constantly and rebuilds itself: the bake test suites. Every run bakes assets under a
            // throwaway prefix and deletes them again, and the guard dutifully snapshotted each one. Measured: 362
            // guard folders totalling 6.4 GB across four days, 128 of them on one day, and `__smoketest__ReconDrone`
            // alone accounted for 1.9 GB as eight copies of the same 232 MB fixture. None of it could ever be worth
            // restoring — these assets exist to be deleted — and the volume made the window's restorable list
            // unreadable, which is the real cost: a safety net nobody can see into is not a safety net.
            // The prefixes are the tests' OWN constants, not copies of them: a duplicated literal here would drift
            // silently the day a suite renames its fixtures, and the guard would quietly start hoarding again.
            string leafRaw = Path.GetFileName(p);
            if (leafRaw.StartsWith(BakeSmokeTest.PREFIX, StringComparison.Ordinal) ||
                leafRaw.StartsWith(BakeFeatureTest.Prefix, StringComparison.Ordinal) ||
                leafRaw.StartsWith(ConversionGateTest.PREFIX, StringComparison.Ordinal))
                return AssetDeleteResult.DidNotDelete;
            string dest = EditorPrefs.GetString("HAF.Backup.Dest", "D:/HAF_Backups");
            string projRoot = Directory.GetParent(Application.dataPath).FullName;
            string abs = Path.Combine(projRoot, p);
            // Folder name keeps the EXTENSION (Tank.png vs Tank.mat deleted in the same second must not merge) and is
            // uniquified with a counter if it still collides — a collision silently overwrote the first manifest.
            string baseName = Path.Combine(dest, "_deleted_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + "_" + Path.GetFileName(p.TrimEnd('/')));
            string dir = baseName;
            for (int i = 2; Directory.Exists(dir); i++) dir = baseName + "_" + i;
            string leaf = Path.GetFileName(abs.TrimEnd('/', '\\'));
            int n = 0; long bytes = 0;
            if (File.Exists(abs)) { n = BackupWindow.CopyFile(abs, Path.Combine(dir, leaf)); bytes = new FileInfo(abs).Length; }
            else if (Directory.Exists(abs)) { n = BackupWindow.CopyTree(abs, Path.Combine(dir, leaf)); bytes = BackupWindow.TreeBytes(Path.Combine(dir, leaf)); }
            bool meta = File.Exists(abs + ".meta");
            if (meta) n += BackupWindow.CopyFile(abs + ".meta", Path.Combine(dir, leaf + ".meta"));
            if (n > 0)
            {
                // A REAL manifest (SRC lines), so the window's Restore button works on delete-guard snapshots too —
                // one click puts the deleted asset back (with the usual pre-restore safety snapshot). The .meta gets
                // its OWN line: restoring without it would regenerate the GUID and break asset references.
                var mf = new List<string> { "# HAF delete-guard snapshot", "# original: " + abs.Replace('\\', '/'), "",
                    $"SRC\t{leaf}\t{abs.Replace('\\', '/')}\t{(meta ? n - 1 : n)}\t{bytes}" };
                if (meta) mf.Add($"SRC\t{leaf}.meta\t{abs.Replace('\\', '/')}.meta\t1\t{new FileInfo(abs + ".meta").Length}");
                File.WriteAllLines(Path.Combine(dir, "manifest.txt"), mf);
                Debug.Log($"[HAF Backup] delete guard: {n} file(s) of '{p}' snapshotted → {dir} (the delete proceeded normally; restorable from the Backup window)");
            }
        }
        catch (Exception e) { Debug.LogWarning("[HAF Backup] delete guard could not snapshot '" + assetPath + "' (the delete still proceeded): " + e.Message); }
        return AssetDeleteResult.DidNotDelete;   // NEVER block or fail the delete — the guard only copies first
    }
}

[InitializeOnLoad]
static class HafAutoBackup
{
    internal const string PrefOn = "HAF.Backup.AutoDaily";
    internal const string PrefLast = "HAF.Backup.AutoLastTicks";
    internal const int Keep = 3;   // newest N _auto_ versions retained; older ones rotate out (logged)
    // Delete-guard retention, in days — set in the Backup & Restore window (0 = keep forever, the old behaviour).
    // Default 14 is deliberately generous for a safety net whose real value lasts minutes: it covers "I broke
    // something before the holiday weekend" while still bounding a layer that had grown to 362 folders / 6.4 GB
    // with no retention at all. A PREF rather than a constant because the right number depends on how much of a
    // pack-rat the author is, and because 0 must remain reachable — someone relying on the old never-delete
    // promise should be able to keep it.
    internal const string PrefGuardDays = "HAF.Backup.GuardDays";
    internal const int DefaultGuardDays = 14;
    internal static int KeepGuardDays => Math.Max(0, EditorPrefs.GetInt(PrefGuardDays, DefaultGuardDays));

    static HafAutoBackup() { EditorApplication.delayCall += RecoverOffsitePartials; EditorApplication.delayCall += MaybeRun; }   // recovery FIRST: stale partials are gone before any new zip starts

    // OFFSITE PARTIAL RECOVERY (2026-08-19, backup-verify drill finding): a DOMAIN RELOAD — any recompile —
    // kills the background zip thread mid-write, leaving '<zip>.partial' and NO final zip, silently: the atomic
    // design protected against a corrupt zip, but nothing retried, so a backup could quietly lack its offsite
    // copy (the 21:01 daily auto's zip died exactly this way while the editor recompiled). On every reload:
    // delete stale partials, and re-zip any backup folder that still exists without its final zip. After a
    // reload NO zip can still be running (its thread died with the domain), so this never races a live writer.
    static void RecoverOffsitePartials()
    {
        try
        {
            string off = EditorPrefs.GetString("HAF.Backup.OffsiteDest", "");
            string dest = EditorPrefs.GetString("HAF.Backup.Dest", "D:/HAF_Backups");
            if (string.IsNullOrEmpty(off) || !Directory.Exists(off)) return;
            foreach (var p in Directory.GetFiles(off, "HAF_*.zip.partial"))
            {
                string zipName = Path.GetFileName(p);
                zipName = zipName.Substring(0, zipName.Length - ".partial".Length);          // HAF_<backup>.zip
                string backupName = zipName.Substring(4, zipName.Length - 4 - 4);            // <backup>
                try { File.Delete(p); } catch (Exception e) { Debug.LogWarning("[HAF Backup] offsite: could not delete stale partial '" + Path.GetFileName(p) + "': " + e.Message); continue; }
                string dir = Path.Combine(dest, backupName);
                if (Directory.Exists(dir) && !File.Exists(Path.Combine(off, zipName)))
                {
                    Debug.Log($"[HAF Backup] offsite: interrupted zip found for '{backupName}' (a recompile killed the background thread) — re-zipping.");
                    System.Threading.Tasks.Task.Run(() => Debug.Log("[HAF Backup] offsite retry: " + BackupWindow.OffsiteZipCore(dir, off)));
                }
                else
                    Debug.Log($"[HAF Backup] offsite: removed stale partial '{zipName}.partial' (backup folder gone or final zip already present).");
            }
        }
        catch (Exception e) { Debug.LogWarning("[HAF Backup] offsite partial recovery: " + e.Message); }
    }

    static void MaybeRun()
    {
        try
        {
            // CONTEXTUAL DEFAULT (HafPackageContext): a full silent backup of somebody else's assets AND
            // configuration, to a "D:/HAF_Backups" default their machine may not even have, must never be
            // something an installed package decides for them. On here, off as a guest, one toggle either way.
            if (!EditorPrefs.GetBool(PrefOn, HafPackageContext.AutoDefault)) return;
            long last = long.TryParse(EditorPrefs.GetString(PrefLast, "0"), out var t) ? t : 0;
            if ((DateTime.Now - new DateTime(last)).TotalHours < 24) return;
            string dest = EditorPrefs.GetString("HAF.Backup.Dest", "D:/HAF_Backups");
            var groups = BackupWindow.BuildGroups();   // ALL groups — assets AND configuration; the auto net is deliberately complete
            if (groups.Count == 0) return;
            string dir = Path.Combine(dest, "_auto_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
            string off = EditorPrefs.GetString("HAF.Backup.OffsiteDest", "");
            bool offAuto = EditorPrefs.GetBool("HAF.Backup.OffsiteAuto", HafPackageContext.AutoDefault);
            EditorPrefs.SetString(PrefLast, DateTime.Now.Ticks.ToString());   // main thread, before the worker starts
            Debug.Log("[HAF Backup] daily auto-version started in the background → " + Path.GetFileName(dir));

            // The whole snapshot runs on a WORKER thread (critical-review fix: 1+ GB copied synchronously froze the
            // editor for ~30-60 s on load, looking like a hang). SnapshotInto + rotation + zip are pure file IO —
            // no Unity APIs — and Debug.Log is thread-safe. Group paths were resolved on the main thread above.
            System.Threading.Tasks.Task.Run(() =>
            {
                var r = BackupWindow.SnapshotInto(dir, groups, "daily auto-version");
                Debug.Log("[HAF Backup] daily auto-version: " + r.report);
                if (!r.ok) return;

                if (offAuto && !string.IsNullOrEmpty(off))
                    Debug.Log("[HAF Backup] auto offsite: " + BackupWindow.OffsiteZipCore(dir, off));

                // RETENTION: rotate _auto_ versions only — keep the newest N. Manual backups, _prerestore and _deleted
                // snapshots are never touched (the never-auto-delete rule holds for everything a human made or lost).
                var autos = Directory.GetDirectories(dest).Where(d => Path.GetFileName(d).StartsWith("_auto_")).OrderByDescending(d => d).ToList();
                foreach (var old in autos.Skip(Keep))
                {
                    try { Directory.Delete(old, true); Debug.Log("[HAF Backup] rotated out old auto-version '" + Path.GetFileName(old) + "' (keeping the newest " + Keep + ")"); }
                    catch (Exception e) { Debug.LogWarning("[HAF Backup] could not rotate '" + Path.GetFileName(old) + "': " + e.Message); }
                }

                // DELETE-GUARD RETENTION (2026-08-23) — the one layer that had none. Its value is measured in
                // minutes ("I just deleted the wrong asset"), but nothing ever pruned it, so 362 folders / 6.4 GB
                // accumulated over four days and the window's restorable list became unreadable. The fixture
                // exclusion above stops the flood at source; this clears what a real day still leaves behind.
                // AGE, not count: a guard's worth is how recently it was made, and a burst of thirty deletions in
                // one afternoon must not evict the single one from yesterday that someone actually needs.
                int cut = 0, keepDays = KeepGuardDays;   // read once: the pref must not change mid-sweep
                foreach (var g in keepDays <= 0 ? new string[0]   // 0 = keep forever, the pre-2026-08-23 behaviour
                                                : Directory.GetDirectories(dest).Where(d => Path.GetFileName(d).StartsWith("_deleted_")).ToArray())
                {
                    try
                    {
                        if (Directory.GetCreationTime(g) > DateTime.Now.AddDays(-keepDays)) continue;
                        Directory.Delete(g, true); cut++;
                    }
                    catch (Exception e) { Debug.LogWarning("[HAF Backup] could not prune guard '" + Path.GetFileName(g) + "': " + e.Message); }
                }
                if (cut > 0) Debug.Log($"[HAF Backup] pruned {cut} delete-guard snapshot(s) older than {keepDays} days. " +
                                       $"Manual backups, _prerestore and _removed_ snapshots are never auto-deleted.");
            });
        }
        catch (Exception e) { Debug.LogWarning("[HAF Backup] daily auto-version failed: " + e.Message); }
    }
}
