using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Real output snapshots, files, locks and .meta GUIDs; no SDK bake or Blender is needed. All outputs belong to the
// runner's disposable Unity project. The injected inner/commit delegates exercise the production Transact method.
public static class UnityBakeCommitDrill
{
    public static void Run()
    {
        var log = new System.Text.StringBuilder(); int failed = 0;
        void Check(string name, bool ok) { log.AppendLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }
        string res = Path.Combine(Application.dataPath, "Resources"); Directory.CreateDirectory(res);
        const string suffix = "_ClipsPoseData.bytes", extra = "_ClipsMovePoseData.bytes";
        BakeResult Transact(string name, Func<BakeResult, RegistryRules.SaveOutcome> commit, Func<BakeConfig, BakeResult> inner)
        {
            try { return (BakeResult)typeof(UniversalBaker).GetMethod("Transact", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new BakeConfig { resourceName = name }, commit, inner }); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }
        string Recovery(BakeResult result) => (string)typeof(BakeResult).GetField("rollbackBackup")?.GetValue(result);
        void RemoveRecovery(string path, string name)
        {
            if (path == null) return;
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "haf_rebake_backup")) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(path);
            if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith(name + "_", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup of an unexpected recovery path");
            Directory.Delete(resolved, true);
        }
        try
        {
            bool started = false;
            var invalid = Transact("../outside", null, cfg => { started = true; return new BakeResult { ok = true }; });
            Check("path-bearing name cannot start snapshot or bake", !invalid.ok && !started);
            foreach (var outcome in new[] { RegistryRules.SaveOutcome.Saved, RegistryRules.SaveOutcome.Refused, RegistryRules.SaveOutcome.Unknown })
            {
                string name = "commit_" + outcome, path = Path.Combine(res, name + suffix), newFile = Path.Combine(res, name + extra);
                File.WriteAllText(path, "OLD"); AssetDatabase.Refresh(); string meta = File.ReadAllText(path + ".meta");
                var result = Transact(name, r => outcome, cfg => { File.WriteAllText(path, "NEW"); File.WriteAllText(newFile, "NEW EXTRA"); return new BakeResult { ok = true }; });
                bool refused = outcome == RegistryRules.SaveOutcome.Refused;
                Check(outcome + " keeps the right asset bytes", File.ReadAllText(path) == (refused ? "OLD" : "NEW") && File.Exists(newFile) == !refused);
                Check(outcome + " reports the commit", result.ok == (outcome == RegistryRules.SaveOutcome.Saved) && result.commitUnsettled == (outcome == RegistryRules.SaveOutcome.Unknown));
                if (refused) Check("rollback restores the original meta GUID", File.ReadAllText(path + ".meta") == meta);
                if (outcome == RegistryRules.SaveOutcome.Unknown)
                {
                    string copy = Recovery(result);
                    Check("unsettled commit retains old assets for recovery", copy != null && File.Exists(Path.Combine(copy, name + suffix)) && File.ReadAllText(Path.Combine(copy, name + suffix)) == "OLD");
                    RemoveRecovery(copy, name);
                }
            }
            {
                string name = "commit_throw", path = Path.Combine(res, name + suffix), registry = Path.Combine(res, "commit_marker.txt");
                File.WriteAllText(path, "OLD");
                var result = Transact(name, r => { File.WriteAllText(registry, "NEW"); throw new IOException("after registry write"); }, cfg => { File.WriteAllText(path, "NEW"); return new BakeResult { ok = true }; });
                Check("throw after registry write cannot roll back named assets", result.commitUnsettled && File.ReadAllText(registry) == "NEW" && File.ReadAllText(path) == "NEW");
                string copy = Recovery(result); RemoveRecovery(copy, name);
            }
            {
                string name = "first_refused", path = Path.Combine(res, name + suffix);
                var result = Transact(name, r => RegistryRules.SaveOutcome.Refused, cfg => { File.WriteAllText(path, "NEW"); return new BakeResult { ok = true }; });
                Check("first bake refusal restores the empty output set", !result.ok && !File.Exists(path) && !File.Exists(path + ".meta"));
            }
            {
                string name = "snapshot_lock", path = Path.Combine(res, name + suffix); File.WriteAllText(path, "OLD"); bool called = false;
                using (var held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var result = Transact(name, r => RegistryRules.SaveOutcome.Saved, cfg => { called = true; return new BakeResult { ok = true }; });
                    Check("failed snapshot prevents starting a destructive bake", !result.ok && !called);
                }
                Check("failed snapshot leaves old assets untouched", File.ReadAllText(path) == "OLD");
            }
            {
                string name = "restore_lock", path = Path.Combine(res, name + suffix); File.WriteAllText(path, "OLD"); FileStream held = null;
                try
                {
                    var result = Transact(name, r => { held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return RegistryRules.SaveOutcome.Refused; },
                        cfg => { File.WriteAllText(path, "NEW"); return new BakeResult { ok = true }; });
                    Check("failed restore never claims the previous model is intact", !result.ok && result.error.Contains("restoring") && !result.error.Contains("previous model is untouched"));
                    string copy = Recovery(result);
                    Check("failed restore retains a recoverable snapshot", copy != null && File.ReadAllText(Path.Combine(copy, name + suffix)) == "OLD");
                    RemoveRecovery(copy, name);
                }
                finally { held?.Dispose(); }
            }
            {
                string name = "cancel_inner", path = Path.Combine(res, name + suffix); File.WriteAllText(path, "OLD"); bool cancelled = false;
                try { Transact(name, r => RegistryRules.SaveOutcome.Saved, cfg => { File.WriteAllText(path, "NEW"); throw new OperationCanceledException(); }); }
                catch (OperationCanceledException) { cancelled = true; }
                Check("cancel during bake restores old assets and propagates", cancelled && File.ReadAllText(path) == "OLD");
            }
            {
                string name = "cancel_commit_" + Guid.NewGuid().ToString("N"), path = Path.Combine(res, name + suffix); File.WriteAllText(path, "OLD"); bool cancelled = false;
                try { Transact(name, r => { throw new OperationCanceledException(); }, cfg => { File.WriteAllText(path, "NEW"); return new BakeResult { ok = true }; }); }
                catch (OperationCanceledException) { cancelled = true; }
                Check("cancel during commit preserves possibly referenced new assets", cancelled && File.ReadAllText(path) == "NEW");
                string[] copies = Directory.GetDirectories(Path.Combine(Path.GetTempPath(), "haf_rebake_backup"), name + "_*");
                Check("cancel during commit retains the old recovery snapshot", copies.Length == 1 && File.ReadAllText(Path.Combine(copies[0], name + suffix)) == "OLD");
                foreach (string copy in copies) RemoveRecovery(copy, name);
            }
        }
        catch (Exception ex) { failed++; log.AppendLine("FAIL " + ex); }
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-hafParityOutput") File.WriteAllText(args[i + 1], log.ToString());
        Debug.Log(log.ToString()); EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}
