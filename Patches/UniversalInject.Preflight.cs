using System;
using System.IO;
using System.Text;
using BepInEx;
using Haf.Schema;

namespace HumankindAssetFramework
{
    // BOOT-TIME PACK PRE-FLIGHT (docs/Pack-Validator-Design.md Phase 2, built 2026-08-18). Runs ONCE per process,
    // right after registration (UniRegisterHook), when the answers exist: skeletons are loaded (bone checks), clip
    // collections have resolved (dead-GUID checks), and the disk being checked is the END USER's. The shared rule
    // core (Haf.Schema.PackValidator — unit-tested) supplies the schema/file/bone rules; this pass adds the
    // plugin-only checks (authored asset GUIDs that didn't resolve) and writes a `## Pre-flight` section into
    // haf_load_report.txt plus one summary log line. It EXPLAINS silent failures — fail-soft stands, nothing is
    // ever blocked (severity Warning), exactly per the design note.
    internal static partial class UniversalInject
    {
        static bool preflightDone;   // once per process — the load report it appends to is also written once

        sealed class PreflightCtx : IValidationContext
        {
            public ModelEntry E;
            public bool? PawnExists(string p) => null;   // no cheap unit-catalog enumeration at runtime; the editor's Pick list + Validate button cover this pre-ship
            public bool? SoundFileExists(string f) => LooseFileExists(E, f, "sounds", Path.Combine(Paths.ConfigPath, "haf_sounds"));
            public bool? SkinFileExists(string f) => LooseFileExists(E, f, "skins", Path.Combine(Paths.ConfigPath, "haf_skins"));
            public bool? BoneExists(string sub)
            {
                if (E.skeleton == null) return null;   // no skeleton (static borrow / retex-only) — nothing to check against
                if (!(GetMember(E.skeleton, "BoneInfos") is Array bones)) return null;
                for (int i = 0; i < bones.Length; i++)
                {
                    var n = GetMember(bones.GetValue(i), "Name")?.ToString() ?? "";
                    if (n.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                return false;   // really looked, really absent — the classic silent typo, now named
            }
        }

        static bool LooseFileExists(ModelEntry e, string file, string sub, string shared)
        {
            if (string.IsNullOrEmpty(file)) return true;
            return (!string.IsNullOrEmpty(e.assetDir) && File.Exists(Path.Combine(e.assetDir, sub, file)))
                || File.Exists(Path.Combine(shared, file));
        }

        internal static void RunPreflight()
        {
            if (preflightDone) return;
            preflightDone = true;
            try
            {
                var snapshot = entries;
                if (snapshot == null || snapshot.Count == 0) return;
                var detail = new StringBuilder();
                int warns = 0, errors = 0;
                var ctx = new PreflightCtx();
                foreach (var e in snapshot)
                {
                    ctx.E = e;
                    var issues = PackValidator.ValidateEntry(e, ctx);

                    // Plugin-only rules: authored asset GUIDs that never resolved (missing from the shipped bundle,
                    // or never baked). Same authored-gate as the smoke test — but at LOAD, for ALL entries, explained.
                    void Role(int a, int b, int c, int d, int animId, string role)
                    {
                        if ((a | b | c | d) != 0 && animId < 0)
                            issues.Add(new ValidationIssue { Severity = ValidationSeverity.Warning, Field = role + "Clip", Message = "authored GUID did not resolve to a registered ClipCollection (was it baked and shipped?)" });
                    }
                    foreach (var r in ClipRoles.All) { var b = e.Role(r); Role(b.a, b.b, b.c, b.d, b.animId, ClipRoles.Name(r)); }
                    if ((e.sa | e.sb | e.sc | e.sd) != 0 && e.skeleton == null)
                        issues.Add(new ValidationIssue { Severity = ValidationSeverity.Warning, Field = "skeleton", Message = "authored GUID did not resolve to a Skeleton asset (was it baked and shipped?)" });

                    if (issues.Count == 0) continue;
                    detail.AppendLine($"entry '{e.resourceName}' (pawn '{e.pawnDescription}'):");
                    foreach (var i in issues)
                    {
                        detail.AppendLine("    " + i);
                        if (i.Severity == ValidationSeverity.Error) errors++; else warns++;
                    }
                }

                string summary = $"[Preflight] {snapshot.Count} entr(y/ies) checked: {warns} warning(s), {errors} error(s)" +
                                 (warns + errors > 0 ? " — see haf_load_report.txt" : "");
                if (warns + errors > 0) Plugin.Log.LogWarning(summary); else Plugin.Log.LogInfo(summary);

                var sec = new StringBuilder();
                sec.AppendLine().AppendLine("## Pre-flight (content validation — explains silent failures; nothing is blocked)");
                sec.AppendLine(summary);
                if (detail.Length > 0) sec.Append(detail);
                File.AppendAllText(Path.Combine(Paths.ConfigPath, "haf_load_report.txt"), sec.ToString());
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Preflight] pass failed (load unaffected): " + ex.Message); }
        }
    }
}
