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
        // Injection errors are a per-SESSION ledger of named SITES, not a frame counter (2026-08-21 review finding: the
        // pose hook bumped a never-reset int per pawn per frame, so one throwing model FAILed the smoke with a five-digit
        // count for the rest of the process). NoteInjectionError counts once per distinct site ("register", "repoint",
        // "fragments", "pose:<model>"); RearmModelRegistration resets both so a clean reload gets a clean verdict.
        [SessionScoped(Manual = "RearmModelRegistration, under the ledger lock")] internal static readonly HashSet<string> InjectionErrorSites = new HashSet<string>();
        internal static int InjectionErrors;   // = InjectionErrorSites.Count, kept as a plain int for the panel/facts
        internal static void NoteInjectionError(string site) { lock (InjectionErrorSites) { if (InjectionErrorSites.Add(site ?? "?")) InjectionErrors = InjectionErrorSites.Count; } }
        internal static List<string> ErrorSitesSnapshot() { lock (InjectionErrorSites) return InjectionErrorSites.OrderBy(s => s).ToList(); }

        internal struct SmokeResult { public bool Pass; public string Summary; }

        // Everything the verdict judges, gathered by the thin runtime side. Empty lists = healthy.
        internal class SmokeFacts
        {
            public string Tier = "";                                 // "load" / "full" — which tier gathered these facts (prefixes the verdict line); "" = untagged
            public int GbMissing, InjectionErrors, Models, Repointed;
            public List<string> ErrorSites = new List<string>();     // the named injection-error sites behind InjectionErrors
            public List<string> DeadRoles = new List<string>();
            public List<string> MissingAssets = new List<string>();
            public List<string> FailedSounds = new List<string>();
            public List<string> BudgetAlarms = new List<string>();
            public List<string> DistrictIssues = new List<string>();
            public List<string> MissingFiles = new List<string>();   // referenced loose files absent from disk — checked for ALL entries, injected or not
            public List<string> SharedSeams = new List<string>();    // OUR patched methods that another mod ALSO patches — informational, never a FAIL (a neighbor isn't an error), but the suspect list for interaction bugs
            public int SeamsChecked;
            // Coverage counters — how many facts the deep pass actually verified, so a PASS line SHOWS its work
            // instead of asking to be believed ("checked 47 roles" is auditable; "clean" is not).
            public int RolesChecked, AssetsChecked, SoundsChecked, LayersChecked, DistrictsChecked, TilesActive, FilesChecked;
            public int ScopedTilesActive;                            // of TilesActive, how many came from the SCOPED path (data-authored selector, e.g. the reactor) — it keeps its tiles in ScopedState.refreshPlbcs, not DistrictModel.tiles
            // TEXTURE HEALTH (2026-08-21): a live tile proves the MESH bound, not that OUR albedo landed on it. Both paths
            // retry the apply and, after 3 exceptions, GIVE UP by latching texApplied=true (so the poll stops) — which means
            // texApplied alone reads as success on a district that is rendering untextured. Judge texErrors first.
            public int TexturedChecked, TexturedApplied;             // textured districts with live tiles judged / of those, albedo actually applied (no give-up)
            public List<string> DistrictNotes = new List<string>();  // informational: texture still pending (asset not resolved yet) — never a FAIL
            public List<string> MatchIssues = new List<string>();    // FAIL: an entry whose pawnDescription can never match, although the game loaded its obvious target (the _DRILL class)
            // 2026-08-19 five-point upgrade (user: "can't we apply all?"):
            public string SeamWriteBack = "";                        // "" = not run; "ok"; "skipped (…)"; "FAILED (…)" — the ObjectSpace round-trip (the combatZ died-in-the-box class), FAILED fails the smoke
            public List<string> Uninjected = new List<string>();     // "loaded but not injected" entries WITH the reason — the silent 19-of-22 delta, named (informational)
            public List<string> SamplerNotes = new List<string>();   // state/combat sampler starvation per entry — informational (samples legitimately empty when the unit left the map)
            // LIVE-PAWN TRUTH (2026-08-21): what the engine is rendering, not what the registry says
            public int LivePawnsChecked, EntriesWithLivePawns;      // live pawn slots carrying one of our descriptor ids / entries that have at least one
            public List<string> PawnSkinIssues = new List<string>(); // FAIL: a live pawn of ours on a skeleton that is not ours (rendering the donor)
            public List<string> PoseIdle = new List<string>();       // FAIL: an entry with live pawns the pose hook has not touched for PoseIdleSeconds
            public int SubPawnWalk = -1, SubPawnScene = -1;          // -1 = audit not run; the targeted walk vs the full scene scan, right now
            public List<string> SubPawnMissed = new List<string>();  // FAIL: sub-pawns the scene scan sees and the walk does not (silent engine audio / visual)
            // THE DETECTOR'S OWN LIVENESS (2026-08-22). The live-pawn checks read `knownManagers`, which is written
            // at exactly ONE place — inside the pose hook's pawn-added path. So a pose hook that never ran leaves the
            // list empty, examines zero pawns, and PASSES, with the coverage clause omitted rather than printed as
            // zero: the detector was fed by the thing it certifies and could only catch a hook alive enough to
            // register a manager. These two fields give it an INDEPENDENT oracle — the live army count, read straight
            // from the presentation entity factory, which no HAF hook touches.
            public int PawnManagers = -1;                            // -1 = not sampled; registered pawn managers (pose-hook evidence)
            public int ArmiesLive = -1;                              // -1 = not sampled; armies the engine has on the map right now
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
            if (f.InjectionErrors > 0) fails.Add($"{f.InjectionErrors} injection error(s)" + (f.ErrorSites.Count > 0 ? " at " + string.Join(", ", f.ErrorSites) : ""));
            if (f.Models <= 0) fails.Add("no models loaded from the registry");
            if (f.DeadRoles.Count > 0) fails.Add($"{f.DeadRoles.Count} dead clip role(s): {string.Join(", ", f.DeadRoles)}");
            if (f.MissingAssets.Count > 0) fails.Add($"{f.MissingAssets.Count} missing asset(s): {string.Join(", ", f.MissingAssets)}");
            if (f.FailedSounds.Count > 0) fails.Add($"{f.FailedSounds.Count} sound file(s) failed to load: {string.Join(", ", f.FailedSounds)}");
            if (f.BudgetAlarms.Count > 0) fails.Add($"GPU mesh budget near the wall: {string.Join(", ", f.BudgetAlarms)}");
            if (f.DistrictIssues.Count > 0) fails.Add($"{f.DistrictIssues.Count} district issue(s): {string.Join(", ", f.DistrictIssues)}");
            if (f.MissingFiles.Count > 0) fails.Add($"{f.MissingFiles.Count} referenced file(s) missing on disk: {string.Join(", ", f.MissingFiles)}");
            if (f.MatchIssues.Count > 0) fails.Add($"{f.MatchIssues.Count} entry(ies) whose pawnDescription cannot match the loaded unit: {string.Join("; ", f.MatchIssues)}");
            // The seam self-test: a computed-but-never-written offset (the combatZ box bug) is a hard FAIL — it means
            // EVERY runtime offset feature is silently dead. "skipped" is not a failure (no pawns to probe).
            if (f.SeamWriteBack.StartsWith("FAILED")) fails.Add("ObjectSpace write-back self-test " + f.SeamWriteBack);
            if (f.PawnSkinIssues.Count > 0) fails.Add($"{f.PawnSkinIssues.Count} entry(ies) rendering the donor: {string.Join("; ", f.PawnSkinIssues)}");
            if (f.PoseIdle.Count > 0) fails.Add($"pose hook idle on {f.PoseIdle.Count} entry(ies): {string.Join("; ", f.PoseIdle)}");
            if (f.SubPawnMissed.Count > 0) fails.Add($"sub-pawn walk missed {f.SubPawnMissed.Count} of {f.SubPawnScene}: {string.Join(", ", f.SubPawnMissed)}");
            // THE CONTRADICTION THAT PROVES THE POSE HOOK IS DEAD (2026-08-22). Armies are on the map and entries are
            // injected, so the pawn-added path must have run and registered a manager — every applicable entry makes
            // the hook proceed for every pawn add. Zero managers with live armies is not "nothing to check", it is the
            // hook not running: the patch failed to apply after a game update, its reflection broke, or the master
            // toggle is off. Previously this state produced a silent PASS, because the only thing that noticed was a
            // counter fed by the very hook in question.
            if (f.PawnManagers == 0 && f.ArmiesLive > 0 && f.Repointed > 0)
                fails.Add($"the pose hook has registered NO pawn manager while {f.ArmiesLive} army(ies) are live and " +
                          $"{f.Repointed} entry(ies) are injected — the pawn-added patch is not running, so every " +
                          "per-frame offset (pose, muzzle, elevation, turn ease) is silently dead");
            // VACUOUS-COVERAGE notes (silence is not success): a green segment that verified NOTHING says so out loud.
            // Notes never fail the smoke — they keep the PASS honest about what it did NOT test this session.
            var notes = new List<string>();
            if (f.DistrictsChecked > 0 && f.TilesActive == 0) notes.Add("districts authored but 0 tiles live — district path UNTESTED this session");
            if (f.Models > 0 && f.Repointed == 0) notes.Add("no entries injected — deep checks vacuous (load a save containing your units)");
            // A live-pawn segment that examined NOTHING says so, instead of dropping its clause and reading clean.
            // Only the benign shapes reach here — the damning one (managers 0, armies live) already FAILED above.
            if (f.PawnManagers >= 0 && f.LivePawnsChecked == 0 && f.Repointed > 0)
                notes.Add(f.PawnManagers == 0
                    ? $"0 live pawn(s) examined — no pawn manager registered yet and {f.ArmiesLive} army(ies) live; " +
                      "the live-pawn checks (skeleton truth, pose-hook liveness) are UNTESTED this session"
                    : $"0 live pawn(s) examined — {f.PawnManagers} manager(s) registered but no slot carries one of our " +
                      "descriptor ids; the live-pawn checks are UNTESTED this session");
            notes.AddRange(f.SamplerNotes);
            notes.AddRange(f.DistrictNotes);
            bool pass = fails.Count == 0;
            string head = (f.Tier.Length > 0 ? "[" + f.Tier + "] " : "") + (pass ? "PASS" : "FAIL (" + string.Join("; ", fails) + ")");
            return new SmokeResult
            {
                Pass = pass,
                Summary = $"{head} — bindings {(f.GbMissing == 0 ? "ok" : f.GbMissing + " MISSING")}, {f.Models} model(s) loaded " +
                          $"({f.Repointed} injected so far), {f.InjectionErrors} injection error(s)" +
                          (pass ? $"; deep checks clean on {f.Repointed} injected — verified {f.RolesChecked} clip role(s), " +
                                  $"{f.AssetsChecked} asset(s), {f.SoundsChecked} sound(s), {f.FilesChecked} file(s) on disk, {f.LayersChecked} GPU layer(s)" +
                                  // NEVER suppressed at zero: an omitted clause reads as "fine", a printed 0 reads as
                                  // "nothing was examined" — and the note above says why. (Review 2026-08-22.)
                                  (f.PawnManagers >= 0 && f.LivePawnsChecked == 0
                                     ? $", 0 live pawn(s) examined [{f.PawnManagers} pawn manager(s), {f.ArmiesLive} army(ies) live]"
                                     : f.LivePawnsChecked > 0 ? $", {f.LivePawnsChecked} live pawn(s) on our skeletons across {f.EntriesWithLivePawns} entry(ies) [pose hook fresh]" : "") +
                                  (f.SubPawnWalk >= 0 ? $", sub-pawn walk {f.SubPawnWalk}/{f.SubPawnScene}" : "") +
                                  (f.DistrictsChecked > 0 ? $", {f.DistrictsChecked} district(s) [{f.TilesActive} tile(s) live{(f.ScopedTilesActive > 0 ? $", {f.ScopedTilesActive} scoped" : "")}{(f.TexturedChecked > 0 ? $", {f.TexturedApplied}/{f.TexturedChecked} textured" : "")}]" : "") +
                                  (f.SeamsChecked > 0 ? $", {f.SeamsChecked} patched seam(s) [{f.SharedSeams.Count} shared]" : "") : "") +
                          (f.SeamWriteBack.Length > 0 && !f.SeamWriteBack.StartsWith("FAILED") ? $"; seam write-back {f.SeamWriteBack}" : "") +
                          // The 19-of-22 delta, NAMED: which entries loaded but haven't injected, and why — informational
                          // ("no unit on map" is normal; a name here you EXPECTED on the map is your lead).
                          (f.Uninjected.Count > 0 ? $"; awaiting injection: {string.Join(", ", f.Uninjected)}" : "") +
                          // Shared seams are informational on PASS and FAIL alike: another mod on our method isn't an
                          // error, but it IS the first place to look when an interaction bug appears — so name it.
                          (f.SharedSeams.Count > 0 ? $"; shared with other mods: {string.Join(", ", f.SharedSeams)}" : "") +
                          (notes.Count > 0 ? $"; NOTE: {string.Join("; NOTE: ", notes)}" : "")
            };
        }

        // Per-entry deep-check gathering — PURE over the entry's fields, so it's unit-testable (the first in-game run
        // proved why: the skeleton check fired on a RETEXTURE-ONLY entry, which legitimately has no skeleton — every
        // asset requirement must gate on "was one AUTHORED", exactly like the roles do).
        // PURE: why an entry hasn't injected, given the unit-definition names the game loaded this session (the addon
        // hook records every one). `mismatch` = the pawnDescription can never match although its obvious target DID load.
        internal static string UninjectedReason(ModelEntry e, ICollection<string> seenAddonNames, out bool mismatch)
        {
            mismatch = false;
            if (e.disabled) return "disabled";
            var pd = e.pawnDescription ?? "";
            if (seenAddonNames == null || seenAddonNames.Count == 0 || pd.Length == 0) return "no unit on the map this session";
            string nearest = null;
            foreach (var n in seenAddonNames)
            {
                if (n.IndexOf(pd, StringComparison.OrdinalIgnoreCase) >= 0) return "its addon loaded but the repoint did not run — see the log";   // matchable: should have repointed
                // the reverse containment: the game loaded a name the pawnDescription merely EXTENDS (a stray suffix)
                if (pd.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 && (nearest == null || n.Length > nearest.Length)) nearest = n;
            }
            if (nearest != null)
            {
                mismatch = true;
                return $"pawnDescription '{pd}' matches NOTHING the game loaded — it loaded '{nearest}', which '{pd}' only extends (stray suffix '{pd.Substring(nearest.Length)}'?) — fix pawnDescription; the unit renders as its donor";
            }
            return "no unit on the map this session";
        }

        internal static void GatherEntryFacts(ModelEntry e, SmokeFacts f) => GatherEntryFacts(e, f, null);
        internal static void GatherEntryFacts(ModelEntry e, SmokeFacts f, ICollection<string> seenAddonNames)
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

            // STATE-SAMPLER health (informational): an entry whose features need the battle-lock/movement sampler
            // (state clips, combatZ) should have samples while its units are on the map. Empty is legitimate when the
            // unit left the map (PruneGone clears), so this is a NOTE, not a failure — but a note on a unit you can
            // SEE on screen means the sampler gate regressed (the combatZ gate-widening class).
            if (e.repointed && ((e.animStateDriven && e.AnyStateRole) || e.combatZ != 0f))
            {
                int sc; lock (e.stateSamples) sc = e.stateSamples.Count;
                if (sc == 0) f.SamplerNotes.Add($"state sampler has no samples for '{e.resourceName}' (fine if its units left the map; a regression if one is on screen)");
            }

            if (!e.repointed)
            {
                // The silent 19-of-22 delta, named per entry with its diagnosis. "No unit on the map" is informational;
                // a pawnDescription that matches NOTHING the game loaded while the game DID load its obvious target is a
                // FAIL (2026-08-21: TankDestroyers carried a _DRILL suffix for weeks, rendered as its donor, and this line
                // said "no unit on the map this session" every time — the harness had the addon list to know better).
                var reason = UninjectedReason(e, seenAddonNames, out bool mismatch);
                if (mismatch) f.MatchIssues.Add($"{e.resourceName}: {reason}");
                else f.Uninjected.Add($"{e.resourceName} ({reason})");
                return;   // deep checks only where the full pipeline provably ran
            }
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
            foreach (var r in ClipRoles.All) { var b = e.Role(r); Role(b.a, b.b, b.c, b.d, b.animId, ClipRoles.Name(r)); }   // the table — the `alc` wiring typo this check once shipped cannot recur
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

        // SHARED-SEAM census (2026-08-17, "are there any guards for conflicts?"): walk every method Harmony knows is
        // patched, keep the ones WE patch, and name any that another owner also patches. Informational by design —
        // Harmony stacks patches safely and a neighbor isn't an error — but when a mod-interaction bug appears, this
        // is the pre-printed suspect list. Testable for real: the suite patches a dummy method with two Harmony ids
        // and asserts the foreign owner is named.
        internal static void GatherSharedSeams(SmokeFacts f, string ownId)
        {
            try
            {
                foreach (var m in HarmonyLib.Harmony.GetAllPatchedMethods().ToList())
                {
                    var info = HarmonyLib.Harmony.GetPatchInfo(m);
                    if (info == null || !info.Owners.Contains(ownId)) continue;   // not one of OUR seams
                    f.SeamsChecked++;
                    var foreign = info.Owners.Where(o => o != ownId).Distinct().ToList();
                    if (foreign.Count > 0)
                        f.SharedSeams.Add($"{m.DeclaringType?.Name}.{m.Name} (also {string.Join("+", foreign)})");
                }
            }
            catch (Exception ex) { Plugin.Diag("[SmokeTest] shared-seam census failed: " + ex.Message); }
        }

        // Per-district deep checks — pure over the DistrictModel's fields (2026-08-17, smoke scale-out). Data-driven:
        // every district in haf_districts.json is covered the day it's added, like the unit checks.
        // TWO render paths, two tile ledgers (drill 2026-08-21): the ISOLATE path tracks its live tiles in
        // DistrictModel.tiles; the SCOPED path (selectorGuid / DistrictSelectorTile — the reactor) binds through
        // ScopedState.refreshPlbcs and never touches d.tiles. Counting only d.tiles made the smoke print
        // "0 tiles live — district path UNTESTED" in the same session the log showed the reactor bound across 1 tile —
        // the honesty note was itself dishonest. The caller passes the scoped ledger in; this stays pure.
        // Texture state of ONE district, lifted off whichever ledger owns it (DistrictModel for isolate, ScopedState for
        // scoped) so the judgement below is pure and identical for both. Textured = the registry authored an albedo
        // atlas at all (pre-2.0 entries have none — nothing to judge).
        internal struct DistrictTexState { public bool Textured, Applied; public int Errors, Wait; }
        internal const int TexGiveUpErrors = 3;   // both apply paths latch texApplied=true after this many exceptions — the "gave up" signature
        internal const int TexPendingPolls = 300; // both paths Diag "not loadable yet" every 300 polls; past that, pending is worth a note

        internal static void GatherDistrictFacts(DistrictInject.DistrictModel d, SmokeFacts f) => GatherDistrictFacts(d, f, scoped: false, scopedTiles: 0);
        internal static void GatherDistrictFacts(DistrictInject.DistrictModel d, SmokeFacts f, bool scoped, int scopedTiles)
            => GatherDistrictFacts(d, f, scoped, scopedTiles, new DistrictTexState { Textured = d.atlasGuid != null, Applied = d.texApplied, Errors = d.texErrors, Wait = d.texWait });
        internal static void GatherDistrictFacts(DistrictInject.DistrictModel d, SmokeFacts f, bool scoped, int scopedTiles, DistrictTexState tex)
        {
            f.DistrictsChecked++;
            int live = scoped ? scopedTiles : d.tiles.Count;
            f.TilesActive += live;
            if (scoped) f.ScopedTilesActive += live;
            // Texture health is judged only where there is something to judge: an authored atlas AND a live tile
            // (an off-screen district has not tried yet; an untextured one never will).
            if (live > 0 && tex.Textured)
            {
                f.TexturedChecked++;
                if (tex.Errors >= TexGiveUpErrors)
                    f.DistrictIssues.Add($"'{d.district}' texture apply GAVE UP after {tex.Errors} error(s) — renders untextured (see [DistrictTex]/[DistrictTile] errors in the log)");
                else if (tex.Applied) f.TexturedApplied++;
                else if (tex.Wait >= TexPendingPolls)
                    f.DistrictNotes.Add($"'{d.district}' texture still pending after {tex.Wait} polls (atlas/layer not resolved yet)");
                else f.DistrictNotes.Add($"'{d.district}' texture pending (just bound; re-run the smoke in a few seconds)");
            }
            if (d.fxMeshGuid == null) f.DistrictIssues.Add($"'{d.district}' fxMesh GUID unparsed");
            // groundIdx: int.MinValue = not yet resolved (pending — the district may not be on screen), -1 = the
            // authored GroundMaterialDefinition NAME was looked up and NOT FOUND (a real authoring error).
            if (!string.IsNullOrEmpty(d.groundMaterial) && d.groundIdx == -1)
                f.DistrictIssues.Add($"'{d.district}' ground material '{d.groundMaterial}' not found");
        }

        // SEAM WRITE-BACK SELF-TEST (2026-08-19): mutate-and-read-back one live pawn entry's ObjectSpace through the
        // EXACT boxed-struct chain every runtime offset uses (GetMember os → SetMember Translation → SetMember
        // entry.ObjectSpace → array SetValue → re-read). The combatZ dive shipped computing AND logging its offset
        // while the write died in the box — a class no unit test can reach (game structs) and only a battle drill
        // caught. This makes that class one F8 press. The probe is +1mm on one entry, restored immediately — and the
        // game rewrites every pawn entry every frame regardless.
        internal static void GatherWriteBackFact(SmokeFacts f)
        {
            try
            {
                var pmType = GameBinding.PawnManager;
                var pm = pmType != null ? HarmonyLib.AccessTools.Property(pmType, "Instance")?.GetValue(null) : null;
                var arr = pm != null ? GetMember(pm, "pawnEntries") as Array : null;
                int n = 0; try { if (pm != null) n = Convert.ToInt32(GetMember(pm, "pawnCount")); } catch { }
                if (arr == null || n <= 0 || arr.Length == 0) { f.SeamWriteBack = "skipped (no live pawns)"; return; }
                int idx = Math.Min(n, arr.Length) - 1;
                var entry = arr.GetValue(idx);
                var os = GetMember(entry, "ObjectSpace");
                if (os == null) { f.SeamWriteBack = "skipped (no ObjectSpace)"; return; }
                var orig = (UnityEngine.Vector3)GetMember(os, "Translation");
                var probe = new UnityEngine.Vector3(orig.x, orig.y + 0.001f, orig.z);
                SetMember(os, "Translation", probe); SetMember(entry, "ObjectSpace", os); arr.SetValue(entry, idx);
                var read = (UnityEngine.Vector3)GetMember(GetMember(arr.GetValue(idx), "ObjectSpace"), "Translation");
                SetMember(os, "Translation", orig); SetMember(entry, "ObjectSpace", os); arr.SetValue(entry, idx);   // restore BEFORE judging
                f.SeamWriteBack = UnityEngine.Mathf.Abs(read.y - probe.y) < 1e-5f
                    ? "ok"
                    : "FAILED (mutation did not persist through the boxed-struct chain — every runtime offset is dead)";
            }
            catch (Exception ex) { f.SeamWriteBack = "FAILED (" + ex.Message + ")"; }
        }


        // ---------------------------------------------------------------------------------------------------------
        // LIVE-PAWN TRUTH (2026-08-21, user: "make more or better smoke tests"). Every check above judges CONFIG-level
        // facts — the registry, the assets, the roles, the tiles. The bugs a drill still caught by EYE this week were
        // all one level lower: what the engine is actually rendering. Three on-demand checks read that level:
        //   1. SKELETON TRUTH — every live pawn slot carrying one of our descriptor ids must sit on OUR skeleton id.
        //      A slot on another skeleton is the unit wearing its DONOR's skin (the tank-destroyer-shows-donor class,
        //      and the "later instances spawn on a different vanilla skeleton" class the sweep exists for).
        //   2. POSE-HOOK LIVENESS — an entry with live slots must have been touched by the pose hook within the last
        //      few seconds (the engine re-adds every rendered pawn each frame). Idle = the hook is dead for that
        //      model ("models just stopped animating, no clue why").
        //   3. SUB-PAWN WALK COVERAGE — the targeted walk that feeds engine audio + sub-pawn visuals is re-audited
        //      against the full scene scan RIGHT NOW (the runtime self-verifies once per session; a unit type that
        //      spawned later — zeppelins, hovercraft, drones this week — can still be missed). A miss = that unit's
        //      engine sound / sub-pawn visual is silently absent.
        // The classification is PURE over (descId, skelId) slots + entries + the clock, so it is unit-tested; the
        // runtime side only collects the slots from the pawn managers — the same loop the stray-slot sweep uses.
        // Cost: a walk over the live pawn slots + one FindObjectsOfType — on demand only, never per frame.
        internal struct LiveSlot { public int Desc, Skel; public LiveSlot(int d, int s) { Desc = d; Skel = s; } }
        internal const float PoseIdleSeconds = 5f;

        internal static void GatherLivePawnFacts(IEnumerable<LiveSlot> slots, IList<ModelEntry> list, float now, SmokeFacts f)
        {
            if (slots == null || list == null) return;
            var byDesc = new Dictionary<int, ModelEntry>();
            // RETEXTURE-ONLY entries (no skeleton of their own, skeletonId -1) ride the vanilla skeleton and are never matched by
            // the pose hook — neither check applies (first in-game run, 2026-08-21: the stealth corvette FAILed both). Same gate
            // as the asset check: judge only what the entry AUTHORED.
            foreach (var e in list) if (e.repointed && e.descId >= 0 && e.skeletonId >= 0 && !byDesc.ContainsKey(e.descId)) byDesc[e.descId] = e;
            var live = new Dictionary<ModelEntry, int>(); var wrong = new Dictionary<ModelEntry, KeyValuePair<int, int>>();   // entry -> (count, a wrong skel)
            foreach (var s in slots)
            {
                if (!byDesc.TryGetValue(s.Desc, out var e)) continue;
                f.LivePawnsChecked++;
                live[e] = (live.TryGetValue(e, out var n) ? n : 0) + 1;
                if (s.Skel != e.skeletonId)
                    wrong[e] = new KeyValuePair<int, int>((wrong.TryGetValue(e, out var w) ? w.Key : 0) + 1, s.Skel);
            }
            foreach (var kv in wrong)
                f.PawnSkinIssues.Add($"{kv.Key.resourceName}: {kv.Value.Key} of {live[kv.Key]} live pawn(s) on skeleton {kv.Value.Value}, ours is {kv.Key.skeletonId} (rendering the donor)");
            foreach (var kv in live)
            {
                f.EntriesWithLivePawns++;
                float idle = kv.Key.lastPoseHookAt < 0f ? float.PositiveInfinity : now - kv.Key.lastPoseHookAt;
                if (idle > PoseIdleSeconds)
                    f.PoseIdle.Add(kv.Key.lastPoseHookAt < 0f
                        ? $"{kv.Key.resourceName}: {kv.Value} live pawn(s) but the pose hook has never run for it this session"
                        : $"{kv.Key.resourceName}: {kv.Value} live pawn(s) but the pose hook last ran {idle:0}s ago");
            }
        }

        // THE INDEPENDENT ORACLE (2026-08-22): how many armies the engine has on the map, read straight from the
        // presentation entity factory. Nothing in HAF writes this — it is the game's own list — which is exactly the
        // point: the live-pawn checks are fed by `knownManagers`, and `knownManagers` is fed by the pose hook they
        // certify. This is the second, unrelated source that lets the verdict tell "nothing to examine" apart from
        // "the hook that would have told us is dead". Returns -1 when the surface itself cannot be read, so an
        // unreadable oracle can never masquerade as "no armies".
        internal static int CountLiveArmies()
        {
            try
            {
                var presType = GameBinding.Presentation;
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies == null) return -1;
                int n = 0;
                for (int i = 0; i < armies.Length; i++) if (armies.GetValue(i) != null) n++;
                return n;
            }
            catch { return -1; }
        }

        // Runtime collector: every (descriptor, skeleton) pair in every known pawn manager's live slots.
        static List<LiveSlot> CollectLiveSlots()
        {
            var r = new List<LiveSlot>();
            for (int m = 0; m < knownManagers.Count; m++)
            {
                var arr = GetMember(knownManagers[m], "pawnEntries") as Array;
                if (arr == null) continue;
                int cnt;
                try { cnt = Convert.ToInt32(GetMember(knownManagers[m], "pawnCount")); } catch { continue; }
                if (cnt <= 0 || cnt > arr.Length) continue;
                for (int i = 0; i < cnt; i++)
                {
                    var slot = arr.GetValue(i);
                    try { r.Add(new LiveSlot(ReadDescId(slot), ReadSkelId(slot))); } catch { }
                }
            }
            return r;
        }


        // ---------------------------------------------------------------------------------------------------------
        // TWO TIERS (2026-08-21, user: "move part of these tests to the end of the loading screen — as long as the
        // game is not loaded yet, I'm fine with performing tests"):
        //   Load — runs ONCE per session, on the first Update after the game's loading screen hides
        //          (Amplitude.Mercury.LoadingScreen.VisibilityChanged(false) — the same static event the game's own
        //          cursor/windows managers subscribe to). Everything that needs only the loaded registry + world
        //          assets: bindings, models, roles, assets, sounds, files on disk, GPU budget, district tiles, seams.
        //   Full — the F8 button. Load + the LIVE checks (skeleton truth, pose-hook liveness, sub-pawn walk audit,
        //          the ObjectSpace write-back), which need pawns on the map and a few frames of the pose hook.
        // The load tier costs a few ms once, at the boundary the player is already waiting on; nothing runs per frame.
        // Disable with SmokeOnLoad=false. Both tiers write haf_smoke_report.txt and the F8 panel, tagged [load]/[full].
        internal enum SmokeTier { Load, Full }
        static volatile bool loadSmokePending;
        static bool loadingScreenHooked;

        internal static void HookLoadingScreen()
        {
            if (loadingScreenHooked) return;
            try
            {
                var t = GameBinding.LoadingScreen;
                var ev = t?.GetEvent("VisibilityChanged", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (ev == null) { Plugin.Log.LogWarning("[SmokeTest] LoadingScreen.VisibilityChanged not found — the load-tier smoke is off (F8 button still works)"); return; }
                Action<bool> handler = visible => { if (!visible) loadSmokePending = true; };   // the hide transition = end of loading
                ev.AddEventHandler(null, Delegate.CreateDelegate(ev.EventHandlerType, handler.Target, handler.Method));
                loadingScreenHooked = true;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[SmokeTest] could not subscribe to LoadingScreen.VisibilityChanged: " + ex.Message); }
        }

        // Main thread (Plugin.Update): the event fires from the UI; the smoke reads main-thread state, so it runs here.
        internal static void ConsumePendingLoadSmoke()
        {
            if (!loadSmokePending) return;
            loadSmokePending = false;
            if (Plugin.SmokeOnLoad == null || !Plugin.SmokeOnLoad.Value) return;
            if (entries == null || !registered) { Plugin.Diag("[SmokeTest][load] skipped — no registry session (main menu?)"); return; }
            RunSmokeTest(SmokeTier.Load);
        }

        internal static void RunSmokeTest() => RunSmokeTest(SmokeTier.Full);
        internal static void RunSmokeTest(SmokeTier tier)
        {
            try
            {
                var gb = GameBinding.Validate(GameBinding.Catalog);
                var f = new SmokeFacts
                {
                    Tier = tier == SmokeTier.Load ? "load" : "full",
                    GbMissing = gb.Count(r => !r.TypeFound) + gb.Where(r => r.TypeFound).Sum(r => r.MissingMembers.Count),
                    InjectionErrors = InjectionErrors,
                    ErrorSites = ErrorSitesSnapshot(),
                    Models = entries?.Count ?? 0,
                    Repointed = entries?.Count(e => e.repointed) ?? 0,
                };

                var snapshot = entries;   // published-once list; snapshot read like every other consumer
                if (snapshot != null)
                    foreach (var e in snapshot)
                    {
                        GatherEntryFacts(e, f, addonDefIds.Keys);   // every unit-definition name the addon hook saw this session
                        CheckLooseFiles(e, f, Path.Combine(Paths.ConfigPath, "haf_sounds"), Path.Combine(Paths.ConfigPath, "haf_skins"));
                    }
                foreach (var d in DistrictInject.distModels)   // main-thread state, read on the main thread (F8)
                {
                    // scoped districts keep their live tiles in scopedStates[name].refreshPlbcs (TryGetValue, not
                    // ScopedFor: the smoke must never CREATE state). Isolate districts: d.tiles.
                    bool scoped = DistrictInject.IsScopedDistrict(d.district);
                    DistrictInject.ScopedState ss = null;
                    int scopedTiles = scoped && DistrictInject.scopedStates.TryGetValue(d.district, out ss) ? ss.refreshPlbcs.Count : 0;
                    // texture ledger follows the path: scoped keeps it on ScopedState (the atlas guid is copied there from
                    // the registry when the district is first scoped), isolate on the DistrictModel itself
                    var tex = scoped
                        ? new DistrictTexState { Textured = d.atlasGuid != null, Applied = ss != null && ss.texApplied, Errors = ss?.texErrors ?? 0, Wait = ss?.texWait ?? 0 }
                        : new DistrictTexState { Textured = d.atlasGuid != null, Applied = d.texApplied, Errors = d.texErrors, Wait = d.texWait };
                    GatherDistrictFacts(d, f, scoped, scopedTiles, tex);
                }
                GatherSharedSeams(f, Plugin.GUID);
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

                if (tier == SmokeTier.Full)
                {   // LIVE tier — needs pawns on the map and a few frames of the pose hook; the load tier runs before either exists
                    GatherWriteBackFact(f);   // the seam self-test — after the reads, before the verdict
                    // Sample BOTH sides before judging: the hook's own evidence (registered managers) and the engine's
                    // (live armies). Either alone is unfalsifiable; together they separate "nothing on the map" from
                    // "the pawn-added patch is not running".
                    f.PawnManagers = knownManagers.Count;
                    f.ArmiesLive = CountLiveArmies();
                    GatherLivePawnFacts(CollectLiveSlots(), snapshot, UnityEngine.Time.time, f);   // live-pawn truth: skeleton + pose-hook liveness
                    if (snapshot != null && f.Repointed > 0) { AuditSubPawnWalk(snapshot, out int w, out int sc, f.SubPawnMissed); f.SubPawnWalk = w; f.SubPawnScene = sc; }
                }

                var res = SmokeVerdict(f);
                if (res.Pass) Plugin.Log.LogInfo("[SmokeTest] " + res.Summary);
                else Plugin.Log.LogWarning("[SmokeTest] " + res.Summary);
                Prober.Report.Clear();
                Prober.Report.Add("Smoke Test — " + res.Summary);
                WriteSmokeReport(res.Summary);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[SmokeTest] " + ex);
                Prober.Report.Clear();
                Prober.Report.Add("Smoke Test — ERROR (see log): " + ex.Message);
                WriteSmokeReport("ERROR: " + ex);
            }
        }

        // Machine-readable sibling of the F8 verdict (2026-08-19) — the smoke result as a file next to
        // haf_load_report.txt and haf_bindings_report.txt, so a headless/CI launch can assert all three clean
        // and a human can diff runs. Regenerated on every smoke run.
        static void WriteSmokeReport(string summary)
        {
            try
            {
                File.WriteAllText(Path.Combine(Paths.ConfigPath, "haf_smoke_report.txt"),
                    "HAF smoke report  (regenerated every Smoke Test run)\n" +
                    "ranAt=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "\n\n" +
                    summary + "\n");
            }
            catch (Exception ex) { Plugin.Diag("[SmokeTest] report write failed: " + ex.Message); }
        }
    }
}
