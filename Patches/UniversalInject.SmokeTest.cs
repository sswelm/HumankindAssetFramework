using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;

namespace HumankindAssetFramework
{
    // In-game smoke harness — a runtime INTEGRATION test of the reflection half (which can't be unit-tested; Humankind
    // has no headless mode). Run from the F8 window (or shortly after a load) in a loaded game: it asserts the plugin
    // came up and injected cleanly and logs a single PASS/FAIL line. Semi-automated (a human launches; the harness does
    // the checking) — the honest form of integration test for a Unity-game mod.
    //
    // The VERDICT is a pure function (SmokeVerdict) so it's unit-tested; RunSmokeTest just gathers the live numbers via
    // reflection/state and calls it. That keeps the quality (the assertion logic) testable and the untestable part thin.
    //
    // DEPTH PASS (2026-08-17, user: "add more tests to make it really meaningful"): beyond bindings/errors/counts, the
    // harness now asserts PER INJECTED ENTRY — each check earned by a shipped bug class:
    //   - dead clip roles: a role GUID authored in the registry whose animation never resolved (the howitzer's
    //     "shipped a dead idle-override GUID" — was invisible until the unit failed to deploy on screen)
    //   - missing assets: an injected entry without its skeleton, or with an authored atlas that didn't load
    //     (the organ-gun-goes-red class — a red/wrong skin has a named cause instead of a visual hunt)
    //   - failed sounds: a configured sound file that didn't load (checked once the audio poll has tried)
    //   - GPU budget: any mesh layer at >=95% verts/indices — the silent skin-vanish wall, alarmed BEFORE it hits
    internal static partial class UniversalInject
    {
        internal static int InjectionErrors;   // bumped in the injection-path catch blocks (RepointMatch / register / fragments / pose)

        internal struct SmokeResult { public bool Pass; public string Summary; }

        // Everything the verdict judges, gathered by the thin runtime side. Empty lists = healthy.
        internal class SmokeFacts
        {
            public int GbMissing, InjectionErrors, Models, Repointed;
            public List<string> DeadRoles = new List<string>();
            public List<string> MissingAssets = new List<string>();
            public List<string> FailedSounds = new List<string>();
            public List<string> BudgetAlarms = new List<string>();
            public List<string> DistrictIssues = new List<string>();
            public List<string> MissingFiles = new List<string>();   // referenced loose files absent from disk — checked for ALL entries, injected or not
            // Coverage counters — how many facts the deep pass actually verified, so a PASS line SHOWS its work
            // instead of asking to be believed ("checked 47 roles" is auditable; "clean" is not).
            public int RolesChecked, AssetsChecked, SoundsChecked, LayersChecked, DistrictsChecked, TilesActive, FilesChecked;
        }

        // Back-compat convenience: the original four-signal verdict (deep-check lists empty).
        internal static SmokeResult SmokeVerdict(int gbMissing, int injectionErrors, int models, int repointed)
            => SmokeVerdict(new SmokeFacts { GbMissing = gbMissing, InjectionErrors = injectionErrors, Models = models, Repointed = repointed });

        // PASS = every catalogued game binding resolved, no injection errors, the registry loaded models, AND the deep
        // checks are clean. `repointed` is informational only (how many entry types have injected so far — depends
        // which units are present). ALL fail reasons surface at once (test-pinned rule).
        internal static SmokeResult SmokeVerdict(SmokeFacts f)
        {
            var fails = new List<string>();
            if (f.GbMissing > 0) fails.Add($"{f.GbMissing} game type/member(s) missing");
            if (f.InjectionErrors > 0) fails.Add($"{f.InjectionErrors} injection error(s)");
            if (f.Models <= 0) fails.Add("no models loaded from the registry");
            if (f.DeadRoles.Count > 0) fails.Add($"{f.DeadRoles.Count} dead clip role(s): {string.Join(", ", f.DeadRoles)}");
            if (f.MissingAssets.Count > 0) fails.Add($"{f.MissingAssets.Count} missing asset(s): {string.Join(", ", f.MissingAssets)}");
            if (f.FailedSounds.Count > 0) fails.Add($"{f.FailedSounds.Count} sound file(s) failed to load: {string.Join(", ", f.FailedSounds)}");
            if (f.BudgetAlarms.Count > 0) fails.Add($"GPU mesh budget near the wall: {string.Join(", ", f.BudgetAlarms)}");
            if (f.DistrictIssues.Count > 0) fails.Add($"{f.DistrictIssues.Count} district issue(s): {string.Join(", ", f.DistrictIssues)}");
            if (f.MissingFiles.Count > 0) fails.Add($"{f.MissingFiles.Count} referenced file(s) missing on disk: {string.Join(", ", f.MissingFiles)}");
            bool pass = fails.Count == 0;
            string head = pass ? "PASS" : "FAIL (" + string.Join("; ", fails) + ")";
            return new SmokeResult
            {
                Pass = pass,
                Summary = $"{head} — bindings {(f.GbMissing == 0 ? "ok" : f.GbMissing + " MISSING")}, {f.Models} model(s) loaded " +
                          $"({f.Repointed} injected so far), {f.InjectionErrors} injection error(s)" +
                          (pass ? $"; deep checks clean on {f.Repointed} injected — verified {f.RolesChecked} clip role(s), " +
                                  $"{f.AssetsChecked} asset(s), {f.SoundsChecked} sound(s), {f.FilesChecked} file(s) on disk, {f.LayersChecked} GPU layer(s)" +
                                  (f.DistrictsChecked > 0 ? $", {f.DistrictsChecked} district(s) [{f.TilesActive} tile(s) live]" : "") : "")
            };
        }

        // Per-entry deep-check gathering — PURE over the entry's fields, so it's unit-testable (the first in-game run
        // proved why: the skeleton check fired on a RETEXTURE-ONLY entry, which legitimately has no skeleton — every
        // asset requirement must gate on "was one AUTHORED", exactly like the roles do).
        internal static void GatherEntryFacts(ModelEntry e, SmokeFacts f)
        {
            // Sounds: judgeable for EVERY entry once the audio poll has tried loading (skip = still pending).
            if (e.customClipTried)
            {
                void Snd(string file, UnityEngine.AudioClip clip, string role)
                {
                    if (string.IsNullOrEmpty(file)) return;
                    f.SoundsChecked++;
                    if (clip == null) f.FailedSounds.Add($"{e.resourceName} {role} '{file}'");
                }
                Snd(e.soundFile, e.customClip, "loop");     Snd(e.soundStartFile, e.customStartClip, "start");
                Snd(e.soundStopFile, e.customStopClip, "stop"); Snd(e.soundIdleFile, e.customIdleClip, "idle");
                Snd(e.soundAttackFile, e.customAttackClip, "attack"); Snd(e.soundDeathFile, e.customDeathClip, "death");
                Snd(e.soundBattleFile, e.customBattleClip, "battle");
            }

            if (!e.repointed) return;   // deep checks only where the full pipeline provably ran
            // Asset checks gate on AUTHORED config: a retexture-only entry has no skeleton/atlas of its own and is healthy without them.
            if ((e.sa | e.sb | e.sc | e.sd) != 0) { f.AssetsChecked++; if (e.skeleton == null) f.MissingAssets.Add($"{e.resourceName} skeleton"); }
            if ((e.ta | e.tb | e.tc | e.td) != 0) { f.AssetsChecked++; if (e.tex == null) f.MissingAssets.Add($"{e.resourceName} atlas"); }
            // Skin PNG (texture-only retexture): when authored, SOME texture must have landed. (If the PNG fails but the
            // entry also has a baked atlas, tex falls back to that — a wrong-but-present skin this check can't separate.)
            if (!string.IsNullOrEmpty(e.textureFile)) { f.AssetsChecked++; if (e.tex == null) f.MissingAssets.Add($"{e.resourceName} skin '{e.textureFile}'"); }
            // Hand prop: authored guid -> the constructed layer + its atlas must exist after repoint.
            if (!string.IsNullOrEmpty(e.handPropGuid))
            {
                f.AssetsChecked++;
                if (e.handPropLayer == null) f.MissingAssets.Add($"{e.resourceName} hand-prop layer");
                else if (e.propAtlasTex == null) f.MissingAssets.Add($"{e.resourceName} hand-prop atlas");
            }

            // Every role's animId resolves at registration (EnsureRegistered), which precedes any repoint —
            // so an authored GUID still at -1 on a repointed entry is genuinely dead (asset failed to load
            // or the collection didn't resolve), not merely "not yet".
            void Role(int a, int b, int c, int d, int animId, string role)
            {
                if ((a | b | c | d) == 0) return;
                f.RolesChecked++;
                if (animId < 0) f.DeadRoles.Add($"{e.resourceName} {role}");
            }
            Role(e.ca, e.cb, e.cc, e.cd, e.animId, "primary");
            Role(e.mca, e.mcb, e.mcc, e.mcd, e.moveAnimId, "move");
            Role(e.aca, e.acb, e.acc, e.acd, e.afterAnimId, "after");
            Role(e.ata, e.atb, e.atc, e.atd, e.attackAnimId, "attack");
            Role(e.cba, e.cbb, e.cbc, e.cbd, e.combatAnimId, "combat");
            Role(e.pva, e.pvb, e.pvc, e.pvd, e.preMoveAnimId, "preMove");
            Role(e.iea, e.ieb, e.iec, e.ied, e.idleAnimId, "idleOverride");
            Role(e.ala, e.alb, e.alc, e.ald, e.idleAltAnimId, "idleAlt");
            Role(e.a2a, e.a2b, e.a2c, e.a2d, e.idleAlt2AnimId, "idleAlt2");
        }

        // LOOSE-FILE sweep (2026-08-17, "basically any loose file"): every disk file a registry entry references must
        // EXIST — checked for ALL entries, injected or not, so a missing WAV/PNG for a unit that isn't in the current
        // save is still named. Search order mirrors the loaders exactly (LoadCustom / LoadSkinPng): the owning pack's
        // <assetDir>/sounds|skins/ first, then the legacy shared dir. Shared dirs are parameters for testability.
        internal static void CheckLooseFiles(ModelEntry e, SmokeFacts f, string soundsShared, string skinsShared)
        {
            void FileCk(string file, string sub, string shared, string role)
            {
                if (string.IsNullOrEmpty(file)) return;
                f.FilesChecked++;
                bool found = (!string.IsNullOrEmpty(e.assetDir) && File.Exists(Path.Combine(e.assetDir, sub, file)))
                          || (!string.IsNullOrEmpty(shared) && File.Exists(Path.Combine(shared, file)));
                if (!found) f.MissingFiles.Add($"{e.resourceName} {role} '{file}'");
            }
            FileCk(e.soundFile, "sounds", soundsShared, "loop");     FileCk(e.soundStartFile, "sounds", soundsShared, "start");
            FileCk(e.soundStopFile, "sounds", soundsShared, "stop"); FileCk(e.soundIdleFile, "sounds", soundsShared, "idle");
            FileCk(e.soundAttackFile, "sounds", soundsShared, "attack"); FileCk(e.soundDeathFile, "sounds", soundsShared, "death");
            FileCk(e.soundBattleFile, "sounds", soundsShared, "battle");
            FileCk(e.textureFile, "skins", skinsShared, "skin");
        }

        // Per-district deep checks — pure over the DistrictModel's fields (2026-08-17, smoke scale-out). Data-driven:
        // every district in haf_districts.json is covered the day it's added, like the unit checks.
        internal static void GatherDistrictFacts(DistrictModel d, SmokeFacts f)
        {
            f.DistrictsChecked++;
            f.TilesActive += d.tiles.Count;
            if (d.fxMeshGuid == null) f.DistrictIssues.Add($"'{d.district}' fxMesh GUID unparsed");
            // groundIdx: int.MinValue = not yet resolved (pending — the district may not be on screen), -1 = the
            // authored GroundMaterialDefinition NAME was looked up and NOT FOUND (a real authoring error).
            if (!string.IsNullOrEmpty(d.groundMaterial) && d.groundIdx == -1)
                f.DistrictIssues.Add($"'{d.district}' ground material '{d.groundMaterial}' not found");
        }

        internal static void RunSmokeTest()
        {
            try
            {
                var gb = GameBinding.Validate(GameBinding.Catalog);
                var f = new SmokeFacts
                {
                    GbMissing = gb.Count(r => !r.TypeFound) + gb.Where(r => r.TypeFound).Sum(r => r.MissingMembers.Count),
                    InjectionErrors = InjectionErrors,
                    Models = entries?.Count ?? 0,
                    Repointed = entries?.Count(e => e.repointed) ?? 0,
                };

                var snapshot = entries;   // published-once list; snapshot read like every other consumer
                if (snapshot != null)
                    foreach (var e in snapshot)
                    {
                        GatherEntryFacts(e, f);
                        CheckLooseFiles(e, f, Path.Combine(Paths.ConfigPath, "haf_sounds"), Path.Combine(Paths.ConfigPath, "haf_skins"));
                    }
                foreach (var d in distModels) GatherDistrictFacts(d, f);   // main-thread state, read on the main thread (F8)
                // A file that's missing on disk also shows as a failed load once tried — one cause, one report:
                // the missing-on-disk line wins, the derived load-failure line is dropped (same "<name> <role> '<file>'" key).
                f.FailedSounds.RemoveAll(s => f.MissingFiles.Contains(s));

                // GPU wall alarm — same structured read the F8 display uses. A read error (no game loaded yet) is not
                // a failure; the budget check simply has nothing to say.
                var layers = ReadMeshBudget(out string budgetErr, out int _);
                for (int i = 0; i < layers.Count; i++)
                {
                    var b = layers[i];
                    if (b.Name == null || b.VertsMax <= 0) continue;
                    f.LayersChecked++;
                    int vp = Pct(b.Verts, b.VertsMax), xp = Pct(b.Idx, b.IdxMax);
                    if (vp >= 95 || xp >= 95) f.BudgetAlarms.Add($"L{i} '{b.Name}' verts {vp}% / idx {xp}%");
                }

                var res = SmokeVerdict(f);
                if (res.Pass) Plugin.Log.LogInfo("[SmokeTest] " + res.Summary);
                else Plugin.Log.LogWarning("[SmokeTest] " + res.Summary);
                Prober.Report.Clear();
                Prober.Report.Add("Smoke Test — " + res.Summary);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[SmokeTest] " + ex);
                Prober.Report.Clear();
                Prober.Report.Add("Smoke Test — ERROR (see log): " + ex.Message);
            }
        }
    }
}
