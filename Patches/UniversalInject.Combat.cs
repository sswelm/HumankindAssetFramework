using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json.Linq;             // provided by the game (mod.io); robust registry parse where JsonUtility no-ops in the game runtime

namespace HumankindAssetFramework
{
    internal static partial class UniversalInject
    {
        // ---- RE-SPAWN NEWLY-CREATED INJECTED UNITS: fix the first-instance rotor race ----
        // A custom pawn that borrows a donor's animated sub-part (e.g. a helicopter rotor) can render that part ~1 unit low
        // when it is first CREATED — the first of a batch on a save-load, a lone unit built in a city, or one spawned via
        // dev tools. Re-creating its pawns fixes it. So we watch the presentation each frame and, ~5s after ANY unit of an
        // opted-in model first appears, re-run the game's own pawn rebuild (PresentationUnit.UpdatePawns = ReleasePawns +
        // InstantiatePawns) on it — a presentation-only refresh, no simulation touched, no unit lost. Deliberately applied
        // to EVERY such unit (one brief flicker each): better to re-spawn one too many than miss a buggy one. Called from
        // Plugin.Update (per-frame, a SAFE point OUTSIDE the AddPawnEntry loop — calling UpdatePawns inside that loop hangs).
        static int respawnFrame;
        const int RespawnAttempts = 1;         // re-spawn each unit ONCE; a single pass fixes the rotor and holds (tested)
        // Delay (in frames, after the unit first renders) before the re-spawn is CONFIGURABLE via the plugin cfg
        // (Factory/RespawnDelayFrames, default 1 = near-instant). A modder on slower hardware can raise it if a unit needs
        // longer to settle. base = first LOADED frame — before the unit is rendered there's nothing to fix.
        [SessionScoped] static readonly Dictionary<object, int> respawnBase = new Dictionary<object, int>();   // opted-in unit -> frame it was first seen loaded
        [SessionScoped] static readonly Dictionary<object, int> respawnCount = new Dictionary<object, int>();  // opted-in unit -> re-spawns done so far
        // Strip a pawn description's trailing variant suffix ("Era6_Common_StealthHelicopters_01" -> "Era6_Common_StealthHelicopters")
        // so it matches the unit-definition name ("LandUnit_Era6_Common_StealthHelicopters").
        internal static string CoreDesc(string pd) => System.Text.RegularExpressions.Regex.Replace(pd ?? "", "_[0-9]+$", "");
        static bool respawnQuiet;   // adaptive cadence: true once every tracked unit has had its passes (see below)
        // ProcessAnimStates: PresentationUnit -> its entry (null = vanilla), so the name resolve runs once per unit, not per run.
        [SessionScoped] internal static readonly Dictionary<object, ModelEntry> _unitEntryCache = new Dictionary<object, ModelEntry>();
        static int _unitEntryCacheRun;
        // PresentationUnit -> its ONE entry (longest-match on the unit definition name), cached per unit object; null = vanilla.
        // Shared by the anim-state sampler and the sub-pawn walk. Main thread only; cleared on re-arm + every ~30 s.
        internal static ModelEntry ResolveUnitEntry(object unit)
        {
            if (unit == null) return null;
            if (!_unitEntryCache.TryGetValue(unit, out var e))
            {
                string uname = GetMember(GetMember(unit, "UnitDefinition"), "Name")?.ToString() ?? "";
                e = uname.Length == 0 ? null : FindEntryForUnitDefinition(uname);
                // PAWN-NAME FALLBACK (2026-08-23). The unit-definition name does not always contain the
                // pawnDescription: a pack can target a pawn SLOT on a unit named for something else entirely (the
                // 2026-08-21 sub-pawn drill found 'Era6_Common_Hovercrafts_01' pawns under a differently-named unit).
                // When that happens this returns null and every feature routed through it — animStateDriven,
                // fire-on-attack, deploy-on-stop, gun elevation — silently does nothing for that unit, with no error.
                // The sub-pawn walk already solved this by matching the PAWN's own name; do the same here so the two
                // resolvers cannot disagree about which entry drives a unit. Runs only when the name match failed,
                // and the result is cached per unit, so it costs nothing on the common path.
                // A null cached before the unit's pawns exist would pin the wrong answer — but this cache is cleared
                // on re-arm and every ~30 s (see its declaration), so it self-heals rather than latching for the
                // session. Not worth a "could not decide" flag; worth knowing it is the reason one is unnecessary.
                if (e == null) e = MatchByPawnName(unit);
                _unitEntryCache[unit] = e;
            }
            return e;
        }

        // The fallback resolver: match the unit's own PAWNS by GameObject name against pawnDescription — the same
        // criterion OurSubPawns/AddPawnSubPawns uses, deliberately, so the unit-level and pawn-level answers agree.
        // Returns null for a genuinely vanilla unit, which is the overwhelmingly common case and must stay silent.
        static ModelEntry MatchByPawnName(object unit)
        {
            try
            {
                var list = entries;
                if (list == null || !(GetMember(unit, "Pawns") is System.Collections.IEnumerable pawns)) return null;
                foreach (var p in pawns)
                {
                    if (!(p is UnityEngine.Component c) || !c) continue;
                    var m = LongestMatch(list, c.gameObject.name, x => x.pawnDescription);
                    if (m == null) continue;
                    // LOUD, ONCE PER UNIT DEFINITION. This is a real bind that the primary matcher could not make,
                    // so it is exactly the drift the project wants named rather than absorbed: it tells a pack author
                    // that their pawnDescription does not appear in the unit's definition name, which is why a
                    // feature "did nothing" before. Once-keyed, so it can never become per-frame noise.
                    string uname = GetMember(GetMember(unit, "UnitDefinition"), "Name")?.ToString() ?? "?";
                    Plugin.LogOnceWarning("pawnfallback:" + uname + ":" + m.pawnDescription,
                        "[Uni] unit '" + uname + "' does not contain pawnDescription '" + m.pawnDescription +
                        "' — matched '" + m.resourceName + "' by PAWN name instead. Unit-level features (animStateDriven, " +
                        "fireOnAttack, deployOnStop, gun elevation) would have been skipped for this unit before 2026-08-23.");
                    return m;
                }
            }
            catch { }
            return null;
        }

        internal static void MaybeRespawnPostLoad()
        {
            if (entries == null || !Plugin.UniversalInjectOn.Value) return;
            if (!entries.Any(x => x.respawnAfterLoad)) return;      // no model opted in — nothing to do
            // ADAPTIVE cadence (perf pass 2026-08-21): ~12x/s while any tracked unit still has respawn passes pending (the
            // rotor-race fix wants to land "shortly after it rendered"), else ~2x/s — the walk is every army on the map
            // with a per-unit name resolve, and it ran at 12 Hz forever after its one-shot job was done (~0.1 ms/frame).
            if (++respawnFrame % (respawnQuiet ? 30 : 5) != 0) return;   // (frame counter still advances every frame)
            try
            {
                var presType = GameBinding.Presentation;
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies == null) return;

                var present = new HashSet<object>();
                bool anyPending = false;
                foreach (var army in armies)
                {
                    if (army == null) continue;
                    var unit = GetMember(army, "PresentationUnit");
                    if (unit == null) continue;
                    string uname = GetMember(GetMember(unit, "UnitDefinition"), "Name")?.ToString() ?? "";
                    if (uname.Length == 0) continue;
                    // Is this unit one of our opted-in models? Resolve its ONE entry (longest-match) and check the flag on
                    // THAT entry — not "any respawn entry whose stem is a substring", which could match a wrong shorter entry.
                    if (FindEntryForUnitDefinition(uname)?.respawnAfterLoad != true) continue;
                    present.Add(unit);
                    // Only start the clock once the unit is actually rendered (IsLoaded) — before that there's nothing to fix
                    // and the 1s marks would be wasted during the load.
                    // Default TRUE = "assume rendered unless the game says otherwise". The old shape wrote that
                    // default into a variable Convert.ToBoolean(null) then overwrote with FALSE, so a renamed
                    // IsLoaded would have skipped every unit here forever — the respawn silently never running.
                    if (!MemberBool(unit, "IsLoaded", true)) continue;
                    if (!respawnBase.ContainsKey(unit)) { respawnBase[unit] = respawnFrame; respawnCount[unit] = 0; anyPending = true; continue; }
                    int done = respawnCount[unit];
                    if (done >= RespawnAttempts) continue;                                             // all passes done
                    anyPending = true;                                                                 // a pass is still owed -> keep the fast cadence
                    if (respawnFrame - respawnBase[unit] < (done + 1) * Math.Max(1, Plugin.RespawnDelayFrames.Value)) continue; // not time for the next pass yet
                    respawnCount[unit] = done + 1;                                                     // bump first so a throwing unit isn't stuck
                    // Correct-by-coincidence in the old shape (the default and the converted-null were both false).
                    // Converted anyway: that is the version that gets copied to a site where they differ.
                    bool naval = MemberBool(unit, "IsNaval", false);
                    AccessTools.Method(unit.GetType(), "UpdatePawns", new[] { typeof(bool) })?.Invoke(unit, new object[] { naval });
                    MarkSubPawnsDirty();                   // the respawn rebuilt this unit's sub-pawns -> the shared scan must refresh
                    FacingPersist.OnArmyRespawned(army);   // the rebuild just wiped this unit's heading — re-arm facing restore (respawnAfterLoad units otherwise lose their saved facing)
                    Plugin.Diag($"[Uni][RESPAWN] re-spawned '{uname}' shortly after it rendered (clears the first-instance rotor race)");
                }
                respawnQuiet = !anyPending;   // nothing owed -> back off to ~2x/s until a new instance appears
                // Drop bookkeeping for units that are gone (destroyed, or the previous game's units after a reload) so the
                // dicts don't grow and a genuinely new instance (a new object) is detected + fixed again.
                if (respawnBase.Count > 0) foreach (var k in respawnBase.Keys.Where(k => !present.Contains(k)).ToList()) { respawnBase.Remove(k); respawnCount.Remove(k); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Uni][RESPAWN] " + ex); }
        }

        // PER-INSTANCE fire targeting (main thread — Plugin.Update). The combat hook enqueues the firing unit/army GUID on the
        // sim thread; here we resolve the matching PresentationUnit and record each of its pawns' RENDER positions as active
        // fires, and prune fires whose clip has finished. The pose hook then plays the one-shot only on the pawn nearest an
        // active fire, so a single howitzer bombarding doesn't animate every howitzer of the type. Same presentation walk as
        // MaybeRespawnPostLoad (Presentation.PresentationEntityFactoryController.PresentationArmyEntities -> PresentationUnit).
        // Read a SimulationEntityGUID as a stable long. Convert.ToInt64/IConvertible.ToInt64 THROW InvalidCastException on
        // this struct, but ToString() returns its underlying ulong as a decimal string — parse that. Both the combat hook
        // (StrikerUnit/StrikerArmy.GUID) and ProcessFireQueues (PresentationUnit.GUID) use this so the values compare equal.
        internal static long GuidToLong(object guidBox)
            => guidBox != null && ulong.TryParse(guidBox.ToString(), out ulong g) ? unchecked((long)g) : 0L;

        // BATTLE-TURN spike (iteration 5): how long the MAP-BOMBARD FX should wait for the striker's eased turn.
        // Called from the artillery schedule prefixes, which run right AFTER TriggerBombardAnimation flipped the
        // formation (FlipPawnsGrid Teleport = the instant facing snap) — so the pawn TRANSFORM already faces the
        // target while the eased ObjectSpace yaw still lags. remaining = eased-vs-transform, no snap-ordering
        // race. Returns 0 when the striker has no easing (no entry AND no Formation Lab link) or is aligned.
        // The strike's target tile as a Unity-world point (the pawn Transforms' space, cyclicity handled by the
        // world controller). Vector3.zero on any resolution failure — callers fall back to the quantized facing.
        static object miToVector3Ext;   // cached MethodInfo (WorldPositionExtensions.ToVector3(WorldPosition, bool))
        static UnityEngine.Vector3 StrikeTargetWorldPos(object strike)
        {
            try
            {
                int tile = Convert.ToInt32(GetMember(strike, "TargetTileIndex"));
                if (tile < 0) return UnityEngine.Vector3.zero;
                var wpT = GameBinding.WorldPosition;
                var extT = GameBinding.WorldPositionExtensions;
                if (wpT == null || extT == null) return UnityEngine.Vector3.zero;
                if (miToVector3Ext == null) miToVector3Ext = AccessTools.Method(extT, "ToVector3");
                if (!(miToVector3Ext is System.Reflection.MethodInfo mi)) return UnityEngine.Vector3.zero;
                var wp = Activator.CreateInstance(wpT, tile);
                return mi.Invoke(null, new object[] { wp, false }) is UnityEngine.Vector3 v ? v : UnityEngine.Vector3.zero;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[BattleTurn] StrikeTargetWorldPos: " + ex.Message); return UnityEngine.Vector3.zero; }
        }

        // CLASS SCAN (category turn ease, docs/Turn-Ease.md): every ~3 s while any category rate is active,
        // sample each live army's first pawn for the two CHARACTERISTIC refinements of the land category —
        // HOVER = the sim UnitDefinition's own UnitTagAsAbility.Hover flag ("ignores terrain": helicopters,
        // hovercraft; user's identification), TURRET = extra azimuth rotation transforms on the pawn
        // (rotationTransformInfos.Length > 1, the array the azimuth audio keys off). The pose hook joins these
        // samples to descriptors by position and learns each land descriptor's refinement ONCE per session.
        static float classScanNext;
        static System.Reflection.FieldInfo fiRotInfos;
        internal static void PollClassScan()
        {
            if (!AnyCatRate) return;   // categories off — no scan cost
            if (UnityEngine.Time.realtimeSinceStartup < classScanNext) return;
            classScanNext = UnityEngine.Time.realtimeSinceStartup + 3f;
            try
            {
                var presType = GameBinding.Presentation;
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies == null) return;
                classSamples.Clear();
                foreach (var army in armies)
                {
                    if (army == null) continue;
                    var unit = GetMember(army, "PresentationUnit");
                    if (unit == null || !(GetMember(unit, "Pawns") is System.Collections.IEnumerable pawns)) continue;
                    bool hover = false;
                    try
                    {
                        if (GetMember(GetMember(unit, "UnitDefinition"), "TagAsAbilities") is Array tags && tags.Length > HoverAbilityIndex)
                            hover = Convert.ToBoolean(tags.GetValue(HoverAbilityIndex));
                    }
                    catch { }
                    foreach (var pawn in pawns)
                    {
                        if (!(GetMember(pawn, "Transform") is UnityEngine.Transform tr)) break;
                        if (fiRotInfos == null) fiRotInfos = AccessTools.Field(pawn.GetType(), "rotationTransformInfos");
                        bool turret = fiRotInfos?.GetValue(pawn) is Array infos && infos.Length > 1;
                        // base category off the LIVE pawn's Definition — classifies descriptors whose pawn
                        // definition never passes the addon hook (the mortar gun's route)
                        int baseCat = -1;
                        try { baseCat = CategoryFromProfile(Convert.ToInt32(GetMember(GetMember(pawn, "Definition"), "AnimationCapabilityProfile"))); } catch { }
                        classSamples.Add(new ClassSample { pos = tr.position, turret = turret, hover = hover, baseCat = baseCat });
                        break;   // the first pawn is representative for the unit's descriptor family
                    }
                }
                int hov = 0;
                for (int i = 0; i < classSamples.Count; i++) if (classSamples[i].hover) hov++;
                if (classSamples.Count != lastScanN || hov != lastScanH)
                {
                    lastScanN = classSamples.Count; lastScanH = hov;
                    Plugin.Log.LogInfo($"[TurnEase] class scan: {classSamples.Count} unit(s), {hov} with the Hover ability");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[TurnEase] class scan: " + ex.Message); }
        }
        static int lastScanN = -1, lastScanH = -1;

        // LAUNCH POSE REFRESH (shell/smoke position fix): vanilla captures the shell's spawn position +
        // direction (muzzle bone TRS) at SCHEDULE time — before our held pivot has even started — so the
        // shell and its launch smoke appeared at the PRE-TURN barrel pose. Prefixed onto
        // TriggerArtilleryStrikeFX (the delayed launch action): (1) re-run PrepareArtilleryStrikeFX so the
        // muzzle is re-read at FIRE time (the attack clip is at its fire frame by now), then (2) rotate the
        // captured pose from the pawn TRANSFORM's hex-quantized yaw onto the true-bearing aim — the transform
        // never turns with the eased GPU model, so a raw recapture would still be up to 30 deg off.
        internal static void RefreshStrikeLaunchPose(object strike)
        {
            try
            {
                var prep = AccessTools.Method(strike.GetType(), "PrepareArtilleryStrikeFX");
                if (prep == null) return;
                // Refresh projectileData off the LIVE pose. The bearing rotation itself now happens INSIDE this
                // call: PrepareArtilleryStrikeFX reads the muzzle via GetBoneTRS, whose postfix (AimRotateBoneTRS)
                // rotates every TRS onto the aim override while the strike is live — the same seam that fixes the
                // mecanim muzzle smoke. Rotating here TOO would double-rotate the shell.
                prep.Invoke(strike, new object[] { 0f, 0f });
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[BattleTurn] RefreshStrikeLaunchPose: " + ex.Message); }
        }

        // F8 "Turn Ease" census — the GENERALIZATION guarantee instrument (user: "what guarantee will I get it
        // will work with other vehicles?"): resolve EVERY live unit through the exact strike-chain logic and
        // print the verdict table. Any unit that would snap-and-fire shows up as NO RATE here, before a single
        // bombard is ever ordered with it.
        internal static List<string> TurnEaseCensusLines()
        {
            var lines = new List<string>();
            try
            {
                var presType = GameBinding.Presentation;
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies == null) { lines.Add("no army list (not in a game session?)"); return lines; }
                int easing = 0, resolved = 0, none = 0;
                var seen = new HashSet<string>();
                foreach (var army in armies)
                {
                    if (army == null) continue;
                    var unit = GetMember(army, "PresentationUnit");
                    if (unit == null) continue;
                    string unitDef = GetMember(unit, "UnitDefinition")?.ToString() ?? "";
                    string shortName = unitDef; int sp = shortName.IndexOf(' '); if (sp > 0) shortName = shortName.Substring(0, sp);
                    if (shortName.Length == 0 || !seen.Add(shortName)) continue;
                    float stateRate = 0f;
                    if (GetMember(unit, "Pawns") is System.Collections.IEnumerable pawns)
                        foreach (var pawn in pawns)
                        { if (GetMember(pawn, "Transform") is UnityEngine.Transform tr) TryTurnStateAt(tr.position, out _, out stateRate); break; }
                    float nameRate = TurnRateForUnitDef(unitDef);
                    if (stateRate > 0f) { easing++; lines.Add($"{shortName}: {stateRate:0} deg/s (EASING live)"); }
                    else if (nameRate > 0f) { resolved++; lines.Add($"{shortName}: {nameRate:0} deg/s (resolves; eases once seen/classified)"); }
                    else { none++; lines.Add($"{shortName}: NO RATE — will snap (excluded plane, human at 0, or unclassified)"); }
                }
                lines.Sort();
                lines.Insert(0, $"TURN EASE CENSUS: {seen.Count} unit type(s) — {easing} easing live, {resolved} resolved, {none} without a rate");
            }
            catch (Exception ex) { lines.Add("census failed: " + ex.Message); }
            return lines;
        }

        // BATTLE HULL AIM (2026-08-06): in deployed battles the engine NEVER rotates a vehicle's hull —
        // vehicles avoid facing rotation and are "aimed" by streaming the aim angle into a TURRET bone slot
        // (invalid on our rigs; the turretBone retarget exists for exactly that). A TURRETLESS vehicle (the
        // Jagdpanzer) therefore aims with NOTHING in vanilla battles. Called when a ranged fight sequence is
        // choreographed: for OUR turretless land/ship entries, register the same aim override the map bombard
        // uses — the eased hull turns to the target, the gun elevation rides the same data, and (hold=1) the
        // battle hold-fire gate waits for the alignment.
        internal static void BattleAimPrep(object fightSequence)
        {
            try
            {
                var shooter = GetMember(fightSequence, "Shooter");
                if (shooter == null || !(GetMember(shooter, "Transform") is UnityEngine.Transform tr)) return;
                var unit = GetMember(shooter, "PresentationUnit");
                string unitDef = GetMember(unit, "UnitDefinition")?.ToString() ?? "";
                var e = FindEntryForUnitDefinition(unitDef);
                if (e == null) return;                                          // v1: our entries only — vanilla battle behavior untouched
                if (!string.IsNullOrEmpty(e.turretBone)) return;                // a turreted model aims with its turret natively
                int cat = EntryBaseCat(e);
                if (cat != CatLand && cat != CatShip) return;                   // hull-aim is a land/ship behavior
                // target position: first target pawn, else the target unit's first pawn
                UnityEngine.Vector3 tp; object tgt = null;
                if (GetMember(fightSequence, "Targets") is Array targets && targets.Length > 0) tgt = targets.GetValue(0);
                if (tgt == null && GetMember(fightSequence, "TargetUnit") is object tu &&
                    GetMember(tu, "Pawns") is System.Collections.IList tps && tps.Count > 0) tgt = tps[0];
                if (tgt == null || !(GetMember(tgt, "Transform") is UnityEngine.Transform tt)) return;
                tp = tt.position;
                var dv = tp - tr.position; dv.y = 0f;
                if (dv.sqrMagnitude < 0.01f) return;
                float aim = UnityEngine.Mathf.Atan2(dv.x, dv.z) * UnityEngine.Mathf.Rad2Deg;
                // broadside units present the side in battle too
                int fao = MemberInt(GetMember(unit, "PresentationUnitDefinition"), "FacingAngleOffset", 0);
                float eased0 = TryTurnStateAt(tr.position, out float ey, out float srate) ? ey : tr.eulerAngles.y;
                if (fao != 0)
                {
                    float a1 = aim - fao, a2 = aim + fao;
                    aim = UnityEngine.Mathf.Abs(UnityEngine.Mathf.DeltaAngle(eased0, a1)) <= UnityEngine.Mathf.Abs(UnityEngine.Mathf.DeltaAngle(eased0, a2)) ? a1 : a2;
                }
                float rate = srate > 0f ? srate : TurnRateForUnitDef(unitDef);
                if (rate <= 0f) return;                                         // not eased — leave vanilla
                float miss = UnityEngine.Mathf.Abs(UnityEngine.Mathf.DeltaAngle(eased0, aim));
                float hold = miss >= 8f ? UnityEngine.Mathf.Min(miss / rate + 0.2f, 3f) : 0f;
                SetAimOverride(tr.position, aim, 120f, UnityEngine.Time.time + hold, dv.magnitude);   // long-stop only — facing persists until the game changes intent (AimMaintain)
                Plugin.Diag($"[BattleTurn] battle hull-aim '{e.resourceName}': aim={aim:F0} miss={miss:F0}deg hold=+{hold:F2}s");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[BattleTurn] BattleAimPrep: " + ex.Message); }
        }

        // PURE (tested: Tests/StrikeHoldTests.cs) — "is this strike ALREADY armed?", the test that keeps the
        // three prefixes of one strike on a single shared clock.
        //
        // An aim override deliberately OUTLIVES its strike: SetAimOverride writes a 120 s `until` because the
        // override doubles as the facing long-stop that keeps a unit pointed at what it shot. So "an override
        // exists near this pawn" does NOT mean "this strike is armed" — only a release time still in the FUTURE
        // does. Testing existence alone made the second bombard from the same tile within two minutes reuse the
        // FIRST strike's long-expired clock: hold 0, the fall-through that arms a new bearing never reached, and
        // every consumer (attack pose, shell schedule, recoil, elevation ramp) agreeing on the previous target's
        // yaw — the gun fired without turning. Found by review 2026-08-22; the drills had only ever fired once.
        //
        // `releaseAt > now` and not `>=`: a hold of 0 (already aligned) stores releaseAt == now, and re-arming
        // that case is correct — it recomputes the same 0 and refreshes the bearing, and SetAimOverride replaces
        // the entry in place rather than appending.
        internal static bool ArmedHoldPending(bool overrideFound, float releaseAt, float now)
            => overrideFound && releaseAt > now;

        internal static float TurnHoldForStrike(object strike)
        {
            try
            {
                long aguid = GuidToLong(GetMember(strike, "AttackerArmyGUID"));
                if (aguid == 0) { Plugin.Log.LogInfo("[BattleTurn] strike prep: no attacker army GUID"); return 0f; }
                var presType = GameBinding.Presentation;
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies == null) { Plugin.Log.LogInfo("[BattleTurn] strike prep: no army list"); return 0f; }
                foreach (var army in armies)
                {
                    if (army == null) continue;
                    var unit = GetMember(army, "PresentationUnit");
                    if (unit == null || GuidToLong(GetMember(unit, "GUID")) != aguid) continue;
                    string unitDef = GetMember(unit, "UnitDefinition")?.ToString() ?? "";
                    if (!(GetMember(unit, "Pawns") is System.Collections.IEnumerable pawns)) return 0f;
                    // ONE SHARED CLOCK (sync fix): if this strike is already armed, every caller — the
                    // Visuals prefix, the teleport defer, the launch AND the hit schedule — gets the SAME
                    // remaining hold off the stored release time. Computing per-caller desynced the bang
                    // from the recoil (~0.25 s: dynamic 8-deg release vs padded static delays).
                    float stateRate = 0f;
                    foreach (var pawn0 in pawns)
                    {
                        if (!(GetMember(pawn0, "Transform") is UnityEngine.Transform tr0)) continue;
                        if (ArmedHoldPending(TryAimRelease(tr0.position, out float rel), rel, UnityEngine.Time.time))
                            return UnityEngine.Mathf.Max(0f, rel - UnityEngine.Time.time);
                        TryTurnStateAt(tr0.position, out _, out stateRate);   // GROUND TRUTH: the rate this pawn is actually easing at
                        break;   // first pawn only — not armed yet, fall through to arm below
                    }
                    // the live ease state's rate is authoritative (whatever path resolved it — entry, link,
                    // category, scan-learned); name-resolution is only the fallback for a pruned/missing state.
                    float rate = stateRate > 0f ? stateRate : TurnRateForUnitDef(unitDef);
                    if (rate <= 0f) { Plugin.Log.LogInfo($"[BattleTurn] strike prep '{unitDef}': rate resolved 0 (no ease state, no name match) — no hold"); return 0f; }
                    // TRUE-BEARING AIM: the flip puts the unit on a HEX-QUANTIZED angle (up to 30 deg off the
                    // target); resolve the strike's target tile to a Unity-world point so each pawn can be
                    // steered to its REAL bearing instead. Vector3.zero = resolution failed -> quantized fallback.
                    var targetPos = StrikeTargetWorldPos(strike);
                    // BROADSIDE FACING (user catch): FacingAngleOffset units — ships, offset typically 90 —
                    // present their SIDE to the target, not the bow; the battle path rotates its look target
                    // by the same offset. Aim at bearing +/- offset, whichever side needs the smaller turn.
                    int fao = MemberInt(GetMember(unit, "PresentationUnitDefinition"), "FacingAngleOffset", 0);
                    float hold = 0f; bool first = true; float releaseAt = 0f;
                    foreach (var pawn in pawns)
                    {
                        if (!(GetMember(pawn, "Transform") is UnityEngine.Transform tr)) continue;
                        float target = tr.eulerAngles.y;                       // strike facing (quantized) when the tile fails to resolve
                        if (targetPos != UnityEngine.Vector3.zero)
                        {
                            target = UnityEngine.Mathf.Atan2(targetPos.x - tr.position.x, targetPos.z - tr.position.z) * UnityEngine.Mathf.Rad2Deg;
                            if (fao != 0)
                            {
                                float cur = TryTurnYawAt(tr.position, out float ce) ? ce : tr.eulerAngles.y;
                                float a1 = target - fao, a2 = target + fao;
                                target = UnityEngine.Mathf.Abs(UnityEngine.Mathf.DeltaAngle(cur, a1)) <= UnityEngine.Mathf.Abs(UnityEngine.Mathf.DeltaAngle(cur, a2)) ? a1 : a2;
                            }
                        }
                        if (first)
                        {
                            first = false;
                            if (!TryTurnYawAt(tr.position, out float eased))
                            { Plugin.Log.LogInfo($"[BattleTurn] strike prep '{unitDef}': rate={rate} but NO ease state near the pawn — no hold"); return 0f; }
                            float miss = UnityEngine.Mathf.Abs(UnityEngine.Mathf.DeltaAngle(eased, target));
                            hold = miss >= 8f ? UnityEngine.Mathf.Min(miss / rate + 0.2f, 3f) : 0f;
                            // A GUN THAT IS STILL BEING LAID MUST NOT FIRE (2026-08-22, user). When a model asks for
                            // a fixed raise time (gunElevRise > 0) rather than tracking the turn, the elevation can
                            // outlast the slew — and the shot would go off with the barrel still climbing. Extending
                            // THIS hold fixes it everywhere at once: it is the strike's one shared clock, so the
                            // attack pose (muzzle flash + sound), the shell schedules and our recoil all wait
                            // together. It also stretches `relEnd`, which the elevation ramp measures against, so
                            // the raise and the release stay in step by construction rather than by luck.
                            // No-op at the default rise of 0, where the raise already tracks the turn exactly.
                            var eElev = FindEntryForUnitDefinition(unitDef);
                            if (eElev != null && eElev.gunElevMax != 0f && eElev.gunElevRise > 0.01f && eElev.gunElevRise > hold)
                            {
                                // Capped below the 4 s failsafes that bound the recoil release and the attack-pose
                                // deferral — past those they would fire anyway, desyncing the very things this
                                // extension exists to keep together.
                                float want = UnityEngine.Mathf.Min(eElev.gunElevRise, 3.5f);
                                // Diag, not LogInfo: gunElevRise now DEFAULTS to 1s, so this fires on every
                                // artillery shot — per-shot detail belongs behind VerboseLog, not in normal play.
                                Plugin.Diag($"[BattleTurn] strike hold extended {hold:F2}s -> {want:F2}s: " +
                                            $"the gun is still elevating (Raise over {eElev.gunElevRise:F2}s)");
                                hold = want;
                            }
                            releaseAt = UnityEngine.Time.time + hold;
                            Plugin.Log.LogInfo($"[BattleTurn] strike hold '{unitDef}': eased={eased:F0} aim={target:F0}{(targetPos != UnityEngine.Vector3.zero ? fao != 0 ? $" (true bearing, broadside {fao}deg)" : " (true bearing)" : " (quantized)")} miss={miss:F0}deg -> +{hold:F2}s (shared clock)");
                        }
                        var dv = targetPos - tr.position; dv.y = 0f;   // horizontal range drives the gun-elevation envelope
                        SetAimOverride(tr.position, target, 120f, releaseAt, targetPos != UnityEngine.Vector3.zero ? dv.magnitude : 0f);   // long-stop only — facing persists until the game changes intent (AimMaintain)
                    }
                    return hold;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[BattleTurn] TurnHoldForStrike: " + ex.Message); return 0f; }
            Plugin.Log.LogInfo("[BattleTurn] strike prep: attacker army not found in the entity list");
            return 0f;
        }

        internal static void ProcessFireQueues()
        {
            if (entries == null || !Plugin.UniversalInjectOn.Value) return;
            bool anyQueued = false;
            // This prune loop is intentionally OUTSIDE the try below: it's pure non-throwing ops (List.RemoveAt on a
            // reverse index + ConcurrentQueue.IsEmpty + Time.time), so guarding it would be dead code. The reflection /
            // presentation walk that CAN throw is wrapped.
            foreach (var e in entries)
            {
                // Two consumers share activeFires: fireOnAttack (artillery one-shot, window = the main clip's duration)
                // and the STATE-DRIVEN attack clip (window = attackDur; armed directly by the ranged-fight hook).
                bool stateAttack = e.animStateDriven && e.attackAnimId >= 0;
                if (!e.fireOnAttack && !stateAttack) continue;
                float dur = stateAttack ? (e.attackDur > 0.001f ? e.attackDur : 1f) * (e.attackRepeats > 0 ? e.attackRepeats : 1)
                                        : (e.animDuration > 0.001f ? e.animDuration : 1f);
                // reverse for-loop, NOT RemoveAll: the dur-capturing lambda allocated a closure per entry per FRAME,
                // even with zero active fires (perf pass 2026-07-19)
                lock (e.activeFires)
                    for (int i = e.activeFires.Count - 1; i >= 0; i--)
                    {
                        var f = e.activeFires[i];
                        if (f.waitAlign)
                        {
                            // battle-turn: hold the clip's clock while the pawn is still turning. The strike's ONE
                            // shared release time is the primary signal — it is the clock the attack pose and the
                            // shell schedules use, and per-consumer checks desynced the recoil from the bang.
                            // BUT IT IS AN ESTIMATE: `miss/rate + 0.2`, capped at 3 s, computed at strike prep. When
                            // the pawn is genuinely still slewing at the deadline the gun recoils mid-turn — visibly
                            // firing off-target (user, 2026-08-22: a 173 deg miss held only 1.16 s). So the live
                            // misalignment is now an ADDITIONAL hold rather than a mere fallback: release needs the
                            // clock elapsed AND the pawn actually pointing at the target. The 4 s failsafe still caps
                            // both, so a stuck ease can never wedge a fire open.
                            float miss = TurnMisalignAt(f.pos);
                            bool aimKnown = TryAimRelease(f.pos, out float rel);
                            bool clockHeld = aimKnown && UnityEngine.Time.time < rel;
                            // THE RACE THAT MADE EVERY EARLIER FIX LOOK LIKE A NO-OP (2026-08-22, measured).
                            // The ranged-fight hook arms this fire BEFORE the strike registers its aim override —
                            // measured at 20 ms before. In that window the unit has not been told to turn yet, so
                            // `miss` is legitimately 0 and no release time exists: the hold released instantly on
                            // "aligned", and the strike then announced a 173 deg turn. "Aligned" is only meaningful
                            // once there is an aim to be aligned WITH, so an unknown aim keeps the fire held for a
                            // short grace. Bounded by the same 4 s failsafe, and by the grace itself for the case
                            // where no strike aim ever arrives (a melee-ish or dial-off path).
                            bool aimPending = !aimKnown && UnityEngine.Time.time - f.armTime < 0.5f;
                            bool still = UnityEngine.Time.time - f.armTime < 4f && (clockHeld || aimPending || miss > 8f);
                            if (still && !clockHeld && !f.lateAlignLogged)
                            {
                                f.lateAlignLogged = true;   // once per fire: how far the shared clock under-called it
                                Plugin.Diag($"[BattleTurn] recoil held past the strike clock: still {miss:F0}deg off " +
                                            $"after {UnityEngine.Time.time - f.armTime:F2}s — the clock's miss/rate estimate ran short");
                            }
                            if (still) { f.startTime = UnityEngine.Time.time; e.activeFires[i] = f; continue; }
                            // WHY IT RELEASED, measured (2026-08-22). "Still fires too early" kept being diagnosed by
                            // reasoning about which clock won; this states it outright — how long the hold lasted and
                            // how far off the gun still was at that instant. A release at miss>8 means the alignment
                            // test itself is wrong; a release at miss~0 means the hold worked and the visible lag is
                            // downstream (clip pacing, or the pawn's rendered facing trailing the eased yaw).
                            Plugin.Diag($"[Fire] '{e.resourceName}': recoil released after " +
                                        $"{UnityEngine.Time.time - f.armTime:F2}s — still {miss:F1}deg off " +
                                        $"({(UnityEngine.Time.time - f.armTime >= 4f ? "4s FAILSAFE" : !aimKnown ? "no strike aim arrived" : "alignment")})");
                            f.waitAlign = false; e.activeFires[i] = f;   // released (or timed out): clock runs from here
                        }
                        if (UnityEngine.Time.time - f.startTime >= dur) e.activeFires.RemoveAt(i);   // drop finished one-shots
                    }
                if (!e.fireGuidQueue.IsEmpty) anyQueued = true;
            }
            if (!anyQueued) return;
            try
            {
                var presType = GameBinding.Presentation;
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies == null) return;
                foreach (var e in entries)
                {
                    // Drain for state-attack entries too: a bombard by a state-driven unit arrives via the artillery
                    // hook's GUID queue (and an undrained queue would otherwise grow for the whole session).
                    if ((!e.fireOnAttack && !(e.animStateDriven && e.attackAnimId >= 0)) || e.fireGuidQueue.IsEmpty) continue;
                    var fired = new HashSet<long>();
                    while (e.fireGuidQueue.TryDequeue(out long g)) fired.Add(g);
                    if (fired.Count == 0) continue;
                    bool matched = false;
                    foreach (var army in armies)
                    {
                        if (army == null) continue;
                        var unit = GetMember(army, "PresentationUnit");
                        if (unit == null) continue;
                        long uguid = GuidToLong(GetMember(unit, "GUID"));
                        if (uguid == 0 || !fired.Contains(uguid)) continue;
                        var pawns = GetMember(unit, "Pawns") as System.Collections.IEnumerable;
                        if (pawns == null) continue;
                        int n = 0; string posDump = "";
                        // battle-turn spike: when turn ease is live for this entry, arm the fire HELD (waitAlign) so
                        // the recoil starts only once the pawn's eased yaw reaches the attack facing.
                        // Ask the SAME question ApplyTurnEase asks (EffectiveTurnRate): per-model, else CATEGORY,
                        // else global. This used to be `turnRate > 0f || e.turnRate > 0f`, which skipped the
                        // category dial — so a land unit eased entirely by `land=180` armed its recoil unheld and
                        // kicked the instant the order was given while the muzzle flash correctly waited for the turn.
                        bool hold = EffectiveTurnRate(e) > 0f;
                        lock (e.activeFires)
                            foreach (var pawn in pawns)
                            {
                                var tr = GetMember(pawn, "Transform") as UnityEngine.Transform;
                                if (tr == null) continue;
                                e.activeFires.Add(new FireInstance { pos = tr.position, startTime = UnityEngine.Time.time, waitAlign = hold, armTime = UnityEngine.Time.time });
                                posDump += $" {tr.position.ToString("0.0")}"; n++;
                            }
                        matched = true;
                        // Log the fire positions so they can be compared to the pose hook's 'ObjectSpace T=...' dump — if the
                        // two are in different spaces the nearest-match (radius 4u) won't fire; this shows it at a glance.
                        Plugin.Diag($"[Fire] '{e.resourceName}' unit/army {uguid}: armed {n} pawn(s) " +
                                    (hold ? $"HELD until aligned (turn {EffectiveTurnRate(e):0} deg/s)"
                                          : "UNHELD — no turn ease resolved, so the recoil fires at once") +
                                    $" at{posDump}");
                    }
                    if (!matched) Plugin.Log.LogWarning($"[Fire] '{e.resourceName}': fired GUID(s) [{string.Join(",", fired)}] matched no PresentationUnit — barrel won't animate (timing/GUID mismatch)");
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Fire] ProcessFireQueues: " + ex); }
        }

        // STATE-DRIVEN ATTACK trigger (Hk_PawnRangedFight postfix). Every pawn ranged shot — battle volley, unit
        // target, district bombard — funnels through PawnRangedFightSequence.InitializeCommon(shooter, ...), and the
        // sequence is built PRESENTATION-side (main thread), so unlike the artillery sim-thread hook we can read the
        // shooter's Transform directly and arm the fire window right here: no GUID queue, no Update-drain roundtrip.
        // Arm the state-driven ATTACK clip for a pawn that just attacked — RANGED or MELEE. Not ranged-specific: it reads
        // the attacker pawn's unit def + world position and, if it's one of our state-attack models, records a FireInstance
        // the pose hook plays the attack clip from. `how` is just for the log ("ranged shot" / "melee attack").
        // Play a one-shot attack cue that stays AUDIBLE regardless of camera distance. AudioSource.PlayClipAtPoint uses
        // minDistance=1 + logarithmic rolloff, so during a battle (camera pulled far back) the roar attenuated to silence —
        // the log showed it firing, but you couldn't hear it. Here: a temp AudioSource, mostly-2D blend + a large minDistance
        // + linear rolloff, so an attack cue reads loud and clear at any zoom (it's a dramatic focal moment, not ambience).
        static void PlayAttackOneShot(UnityEngine.AudioClip clip, UnityEngine.Vector3 pos, float vol, float offsetSec = 0f)
        {
            var go = new UnityEngine.GameObject("HAF_attackSfx");
            go.transform.position = pos;
            var src = go.AddComponent<UnityEngine.AudioSource>();
            src.clip = clip; src.volume = vol; src.spatialBlend = 0.35f;   // mostly 2D → always audible, slight directional flavour
            src.minDistance = 60f; src.maxDistance = 1200f; src.rolloffMode = UnityEngine.AudioRolloffMode.Linear;
            // start offset: skip a silent/windup lead-in so the impact lands on the swing (clamped inside the clip)
            float off = UnityEngine.Mathf.Clamp(offsetSec, 0f, UnityEngine.Mathf.Max(0f, clip.length - 0.05f));
            if (off > 0f) src.time = off;
            src.Play();
            UnityEngine.Object.Destroy(go, clip.length - off + 0.15f);
        }

        // Per-(entry, attacking-UNIT) key so the TWO attack-roar paths dedup against each other: the fight-hook path
        // (OnPawnAttack) and the early FaceEnemy path. A ranged attacker used to roar TWICE (each path used a disjoint
        // key). Keyed by the sim unit GUID — the state poll notes the army-stack and battle-tile PresentationUnit are
        // "same GUID, different objects", so BOTH paths resolve the identical GUID for the attacker and compute the same
        // key. Falls back to the PresentationUnit object identity if the GUID is unreadable (then it just won't cross-dedup).
        static long AttackSoundKey(ModelEntry e, object presentationUnit)
        {
            long g = GuidToLong(GetMember(presentationUnit, "GUID"));
            if (g == 0) g = presentationUnit?.GetHashCode() ?? 0;
            return unchecked(((long)e.resourceName.GetHashCode() << 32) ^ g);
        }

        // playSound/armAnim let a hook drive ONLY one channel: the melee attack SOUND fires from the EARLY fight-start hook
        // (StartPawnAction — the moment the attack begins), while the attack ANIMATION arms from the PER-SWING hook
        // (StartPairMeleeAttack). Firing the sound per-swing made the roar land near the END of the anim (too late);
        // fight-start is "the moment you attack". Ranged still does both from its one shot hook (timing is fine there).
        // RELEASE THE HELD RECOIL AT THE ATTACK ITSELF (2026-08-22). Called from the attack-pose replay — the moment
        // the game actually fires, muzzle flash and sound included. A held fire near that pawn starts its clip NOW,
        // instead of independently re-deriving the same moment from the strike clock's estimate. One event beats two
        // clocks: the kick can no longer lead or lag the bang, whatever the estimate said.
        internal static void ReleaseHeldFiresAt(UnityEngine.Vector3 pos)
        {
            var list = entries;
            if (list == null) return;
            foreach (var e in list)
            {
                if (e.activeFires == null) continue;
                lock (e.activeFires)
                    for (int i = 0; i < e.activeFires.Count; i++)
                    {
                        var f = e.activeFires[i];
                        if (!f.waitAlign || (f.pos - pos).sqrMagnitude >= PoseMath.FireMatchRadiusSq) continue;
                        f.waitAlign = false; f.startTime = UnityEngine.Time.time;
                        e.activeFires[i] = f;
                        Plugin.Diag($"[Fire] '{e.resourceName}': recoil released BY THE ATTACK EVENT " +
                                    $"({UnityEngine.Time.time - f.armTime:F2}s after arming)");
                    }
            }
        }

        internal static void OnPawnAttack(object attacker, string how, bool playSound = true, bool armAnim = true)
        {
            try
            {
                if (attacker == null || !Plugin.UniversalInjectOn.Value) return;
                var list = entries;
                if (list == null) return;
                bool any = false;
                foreach (var x in list) if ((x.animStateDriven && x.attackAnimId >= 0) || !string.IsNullOrEmpty(x.soundAttackFile)) { any = true; break; }
                if (!any) return;   // no state-attack model AND no attack-sound model registered — skip the reflection walk entirely
                var unit = GetMember(attacker, "PresentationUnit");
                string unitDef = GetMember(unit, "UnitDefinition")?.ToString() ?? "";
                var e = FindEntryForUnitDefinition(unitDef);
                if (e == null) return;
                bool wantAnim = armAnim && e.animStateDriven && e.attackAnimId >= 0;
                bool wantSound = playSound && e.customAttackClip != null;
                if (!wantAnim && !wantSound) return;
                if (!(GetMember(attacker, "Transform") is UnityEngine.Transform tr)) return;
                long pid = attacker.GetHashCode();
                long soundKey = AttackSoundKey(e, unit);   // per-UNIT (not per-pawn) so it dedups the early FaceEnemy roar too — one roar per unit-attack
                float now = UnityEngine.Time.time;

                // ATTACK SOUND: a DISTINCT, more violent one-shot at the attacker (vs the idle growl). Per-pawn min-gap so a
                // rapid multi-swing fight doesn't machine-gun it — each distinct swing past the gap still triggers. Plays
                // even when the model isn't state-driven (a unit can want an attack sound without our attack animation).
                if (wantSound && (!e.attackSoundNextAt.TryGetValue(soundKey, out var nextS) || now >= nextS))
                {
                    PlayAttackOneShot(e.customAttackClip, tr.position, e.soundAttackVolume, e.soundAttackOffset);
                    float audible = Math.Max(0.05f, e.customAttackClip.length - Math.Max(0f, e.soundAttackOffset));   // gap keys off what actually PLAYS (post-offset)
                    e.attackSoundNextAt[soundKey] = now + Math.Max(0.25f, Math.Min(audible, 1.2f));
                }

                // ATTACK ANIMATION: key the fire by the ATTACKING PAWN's identity, and if that pawn already has an active
                // fire, RESTART it (update start time + position) instead of stacking a second. Melee swings come faster
                // than one attack window, and the pose hook plays the FIRST/oldest matching fire for the whole window — so
                // without this a rapid second swing was swallowed by the first's window (looked like ONE long attack, or
                // nothing when the window was tiny). Per-pawn restart = each swing replays the bite from frame 0.
                if (wantAnim)
                {
                    var fi = new FireInstance { pos = tr.position, startTime = now, pawnId = pid };
                    lock (e.activeFires)
                    {
                        int at = -1;
                        for (int i = 0; i < e.activeFires.Count; i++) if (e.activeFires[i].pawnId == pid) { at = i; break; }
                        if (at >= 0) e.activeFires[at] = fi; else e.activeFires.Add(fi);   // restart this pawn's bite, or arm a new one
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[State] OnPawnAttack: " + ex); }
        }

        // EARLY ATTACK SOUND (functional — hooked from UnitActionFaceEnemy.StartUnitAction). FaceEnemy fires the moment OUR
        // unit commits to the strike: it turns to face the enemy BEFORE the melee swing (verified: precedes 'melee start'),
        // which is as close to "the moment you order the attack" as the presentation gives us — so the roar plays HERE,
        // ahead of the later fight-start hook. Gate: our unit is the ATTACKER and has an attack clip. Per-attacker min-gap,
        // keyed the SAME way OnPawnAttack keys it so the two roar paths dedup (no double) and a FaceEnemy re-fire can't
        // double either. Played mostly-2D at the camera (PlayAttackOneShot) so it's audible at any zoom.
        internal static void TryEarlyAttackSound(object action)
        {
            try
            {
                if (entries == null) return;
                var atkUnit = GetMember(GetMember(action, "AttackerBattleUnit"), "PresentationUnit");
                string au = GetMember(atkUnit, "UnitDefinition")?.ToString() ?? "";
                var ea = FindEntryForUnitDefinition(au);
                if (ea == null || ea.customAttackClip == null) return;
                string scope = GetMember(action, "actionScope")?.ToString() ?? "";
                if (scope.IndexOf("Attacker", StringComparison.OrdinalIgnoreCase) < 0) return;   // only when OUR unit is the attacker
                long key = AttackSoundKey(ea, atkUnit);   // SAME key OnPawnAttack computes -> the two roar paths dedup, no double
                float now = UnityEngine.Time.time;
                if (ea.attackSoundNextAt.TryGetValue(key, out var next) && now < next) return;
                var camPos = UnityEngine.Camera.main != null ? UnityEngine.Camera.main.transform.position : UnityEngine.Vector3.zero;
                PlayAttackOneShot(ea.customAttackClip, camPos, ea.soundAttackVolume, ea.soundAttackOffset);
                ea.attackSoundNextAt[key] = now + Math.Max(0.25f, Math.Min(ea.customAttackClip.length, 1.2f));
            }
            catch { }
        }

        // ---- DONOR VFX SUPPRESSION (2026-07-24, the AA-gun flashes): MecanimEvent VFX (muzzle flashes, animator
        // puffs) anchor to DONOR bone names that don't exist on our replaced skeleton, so inherited flashes render
        // misplaced. This is the audio-silence pattern at the VISUAL chokepoint (MecanimEventInterpreter.StartVFXEvent):
        // a prefix skips the launch for opted-in units. VFX-only — the donor's SOUNDS (StartSFXEvent/Wwise) are NOT
        // touched; silenceDonorAudio remains the separate knob for those. Unit match = sub-pawn GameObject name contains
        // the entry's pawnDescription (the proven audio-poll match); VFX events are rare, so the walk is cheap. ----
        internal static bool SuppressVfxFor(object interp)
        {
            var list = entries;
            if (list == null) return false;
            bool any = false;
            foreach (var x in list) if (x.silenceDonorVfx) { any = true; break; }
            if (!any) return false;
            try
            {
                string n = (interp as UnityEngine.Component)?.gameObject?.name;
                if (string.IsNullOrEmpty(n))
                    n = (GetMember(interp, "presentationSubPawn") as UnityEngine.Component)?.gameObject?.name;
                if (string.IsNullOrEmpty(n)) return false;
                foreach (var e in list)
                    if (e.silenceDonorVfx && !string.IsNullOrEmpty(e.pawnDescription) && n.IndexOf(e.pawnDescription, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!e.vfxSilencedLogged) { e.vfxSilencedLogged = true; Plugin.Diag($"[Vfx] '{e.resourceName}' donor VFX suppressed (first event on '{n}'; sounds untouched)"); }
                        return true;
                    }
            }
            catch { }
            return false;
        }

        // ---- MUZZLE OFFSET COMPENSATION (2026-07-24, v3). v1's GetBoneTRS redirect verifiably lands the right BONE
        // (log-proven), but AlterationFireProjectile.StartEvent then computes
        //     startPosition = boneTRS.Transform(PositionToLaunchVFX)
        // — the donor's barrel-length offset is added ON TOP of whatever TRS we return, and it dominates (why
        // "Turret" and "MW_T" looked identical, and why pinning the interpreter's StartVFXEvent changed nothing:
        // the VISIBLE flash rides the projectile-launch path, a different consumer of the same bone lookup).
        // The mecanim event is SHARED donor data (zeroing its field would corrupt the donor unit), so instead:
        // a stash prefix on StartEvent records the offset + position-socket name for the duration of the call, and
        // the v1 redirect returns a PRE-COMPENSATED TRS — Translation -= Rotation * (offset * Scale) — so the
        // caller's own "+ offset" lands exactly back on our muzzle bone. Only the POSITION socket is compensated
        // (matched by name); the rotation socket keeps the true bone TRS for the launch direction. Vanilla pawns
        // never reach the redirect, so they are untouched. ----
        static UnityEngine.Vector3 pendingMuzzleOffset;
        static string pendingMuzzlePosName;
        static bool pendingMuzzleActive;
        static System.Reflection.FieldInfo trsTranslation, trsRotation, trsScale;
        static bool trsFieldsResolved;
        static int fireProjLogCount;
        internal static void OnFireProjectileStart(object mecanimEvent)
        {
            try
            {
                pendingMuzzlePosName = GetMember(mecanimEvent, "ParentNameToLaunchVFXPosition")?.ToString();
                pendingMuzzleOffset = GetMember(mecanimEvent, "PositionToLaunchVFX") is UnityEngine.Vector3 v ? v : UnityEngine.Vector3.zero;
                pendingMuzzleActive = !string.IsNullOrEmpty(pendingMuzzlePosName);
                // invisible-shell hunt (2026-08-06): does the projectile event even FIRE in battle? First 8 per session.
                if (fireProjLogCount < 8)
                { fireProjLogCount++; Plugin.Log.LogInfo($"[Muzzle] FireProjectile event: posSocket='{pendingMuzzlePosName}' offset={pendingMuzzleOffset.ToString("0.00")}"); }
            }
            catch { pendingMuzzleActive = false; }
        }
        internal static void OnFireProjectileEnd() { pendingMuzzleActive = false; }

        // ---- DEATH CUE (2026-07-23): a one-shot when a pawn of ours starts dying. Seam: PresentationPawn.TriggerDeath —
        // presentation-side, fired exactly once per dying pawn as its death FSM starts (IsDead is set inside it). A wiped
        // stack dies pawn-by-pawn in a burst, so a short per-entry min-gap turns five simultaneous rattles into one. ----
        internal static void OnPawnDeath(object pawn)
        {
            try
            {
                if (pawn == null || !Plugin.UniversalInjectOn.Value) return;
                var list = entries;
                if (list == null) return;
                bool any = false;
                foreach (var x in list) if (!string.IsNullOrEmpty(x.soundDeathFile)) { any = true; break; }
                if (!any) return;
                string ud = GetMember(GetMember(pawn, "PresentationUnit"), "UnitDefinition")?.ToString() ?? "";
                var e = FindEntryForUnitDefinition(ud);
                if (e == null || e.customDeathClip == null) return;
                float now = UnityEngine.Time.time;
                if (now < e.deathSoundNextAt) return;
                e.deathSoundNextAt = now + 0.6f;
                var tr = GetMember(pawn, "Transform") as UnityEngine.Transform;
                PlayAttackOneShot(e.customDeathClip, tr != null ? tr.position : UnityEngine.Vector3.zero, e.soundDeathVolume, e.soundDeathOffset);
                Plugin.Diag($"[Sound] '{e.resourceName}' death cue");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Sound] OnPawnDeath: " + ex); }
        }

        // ---- BATTLE-START WAR CRY (2026-07-23). SimulationEvent_BattleStarted.Raise carries the Battle; walking
        // AttackerGroup/DefenderGroup -> Contenders -> Units -> Unit.UnitDefinition finds OUR units in it (pure managed
        // reads — safe on the sim thread). Matches are QUEUED and played on the main thread (Unity audio APIs), one cry
        // per entry per battle, camera-anchored like the attack roar so it opens the battle audibly at any zoom. ----
        [ProcessLived("drained every frame; holds registry entries")] static readonly System.Collections.Concurrent.ConcurrentQueue<ModelEntry> battleCryQueue = new System.Collections.Concurrent.ConcurrentQueue<ModelEntry>();
        internal static void OnBattleStarted(object battle)
        {
            try
            {
                if (battle == null || !Plugin.UniversalInjectOn.Value) return;
                var list = entries;
                if (list == null) return;
                bool any = false;
                foreach (var x in list) if (!string.IsNullOrEmpty(x.soundBattleFile)) { any = true; break; }
                if (!any) return;
                var found = new HashSet<ModelEntry>();
                foreach (var g in new[] { GetMember(battle, "AttackerGroup"), GetMember(battle, "DefenderGroup") })
                {
                    if (!(GetMember(g, "Contenders") is System.Collections.IEnumerable conts)) continue;
                    foreach (var c in conts)
                    {
                        if (!(GetMember(c, "Units") is System.Collections.IEnumerable units)) continue;
                        foreach (var bu in units)
                        {
                            string ud = GetMember(GetMember(bu, "Unit"), "UnitDefinition")?.ToString() ?? "";
                            var e = FindEntryForUnitDefinition(ud);
                            if (e != null && !string.IsNullOrEmpty(e.soundBattleFile)) found.Add(e);
                        }
                    }
                }
                foreach (var e in found)
                {
                    battleCryQueue.Enqueue(e);
                    Plugin.Diag($"[Sound] battle started with '{e.resourceName}' — war cry queued");
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Sound] OnBattleStarted: " + ex); }
        }

        // Main-thread drain (Plugin.Update). If an entry's clips haven't lazy-loaded yet (battle at load, before the
        // first audio poll), the cry is left in the queue for a later frame rather than dropped.
        internal static void ProcessBattleCries()
        {
            // Guarded like the other Update polls: PlayAttackOneShot does `new GameObject` + AddComponent + Camera.main —
            // an unhandled throw here would kill the whole Update chain for the rest of the session (high blast radius).
            try
            {
                while (battleCryQueue.TryDequeue(out var e))
                {
                    if (e.customBattleClip == null)
                    {
                        if (!e.customClipTried) { battleCryQueue.Enqueue(e); return; }   // clips not loaded yet — retry next frame
                        continue;                                                        // load ran and failed — drop, already logged
                    }
                    float now = UnityEngine.Time.time;
                    if (now < e.battleCryNextAt) continue;
                    e.battleCryNextAt = now + 2f;
                    var camPos = UnityEngine.Camera.main != null ? UnityEngine.Camera.main.transform.position : UnityEngine.Vector3.zero;
                    PlayAttackOneShot(e.customBattleClip, camPos, e.soundBattleVolume, e.soundBattleOffset);
                    Plugin.Diag($"[Sound] '{e.resourceName}' war cry");
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Sound] ProcessBattleCries: " + ex); }
        }

        // STATE-DRIVEN poll (main thread — Plugin.Update; Phase 2, 2026-07-19). For each animStateDriven model: per
        // unit, MOVEMENT = the render position actually changed since the last poll — the deploy poll's proven
        // settle-immune signal (wait-to-idle / turn-in-place after stopping does NOT move the tile position, so a
        // settling unit reads stopped and the after/idle clips play instead of the run). On a moving->stopped flip
        // the stop time is recorded for the AFTER one-shot window. Publishes one sample per PAWN under lock, so
        // every soldier of a squad animates (the pose hook matches by nearest sample).
        [ProcessLived("per-poll scratch")] static readonly List<long> scratchGoneKeys = new List<long>();   // reused by PruneGone — was a Keys.Where(...).ToList() alloc per dict per poll (6x/poll)
        // Remove every key not seen this poll (a unit that despawned). Main-thread only; the shared scratch is safe because
        // ProcessAnimStates/ProcessDeployState run sequentially from Plugin.Update and PruneGone completes before the next call.
        static void PruneGone<TV>(Dictionary<long, TV> dict, HashSet<long> seen)
        {
            scratchGoneKeys.Clear();
            foreach (var k in dict.Keys) if (!seen.Contains(k)) scratchGoneKeys.Add(k);   // Dictionary.KeyCollection enumerator is a struct — no alloc
            for (int i = 0; i < scratchGoneKeys.Count; i++) dict.Remove(scratchGoneKeys[i]);
        }

        static int stateFrame;
        internal static void ProcessAnimStates()
        {
            var list = entries;
            if (list == null || !Plugin.UniversalInjectOn.Value) return;
            bool any = false;
            // any state role, not just move — a move-less state-driven model still needs moving/stopped samples for
            // its attack/idle machine (#8). combatZ entries (2026-08-19: the diving submarine) need the samples too —
            // combat stance is per-pawn, so even a STATIC entry with a combat height offset joins the sampling.
            foreach (var e in list) if ((e.animStateDriven && e.AnyStateRole) || e.combatZ != 0f) { any = true; break; }
            if (!any) return;
            if (++stateFrame % 3 != 0) return;   // ~20x/s, like the deploy poll
            if ((++_unitEntryCacheRun % 200) == 0) _unitEntryCache.Clear();   // ~every 30 s: drop dead units' keys (cheap to re-resolve)
            try
            {
                float now = UnityEngine.Time.time;
                var presType = GameBinding.Presentation;
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies == null) return;
                var fresh = new Dictionary<ModelEntry, List<StateSample>>();
                var seen = new Dictionary<ModelEntry, HashSet<long>>();
                foreach (var e in list) if ((e.animStateDriven && e.AnyStateRole) || e.combatZ != 0f) { fresh[e] = new List<StateSample>(); seen[e] = new HashSet<long>(); }
                // One sampler for BOTH walks below. keySalt separates the map-army and battle bookkeeping for the
                // same sim unit: during a battle the army's PresentationUnit still exists at the STACK position while
                // the battle deploys a SECOND PresentationUnit on its combat tile — same GUID, different objects. A
                // shared key would ping-pong stateLastPos between the two positions and read as permanently "moving".
                void SampleUnit(object unit, bool combat, long keySalt)
                {
                    if (unit == null) return;
                    // unit -> entry resolved ONCE per PresentationUnit object (null cached too): the per-unit name read
                    // (two reflection hops + a StaticString ToString alloc) and the 22-entry longest-match ran for EVERY
                    // army on the map every 3 frames, vanilla included (perf pass 2026-08-21). Cleared on re-arm and
                    // every 200 runs (dead units would otherwise pin their entry forever).
                    var e = ResolveUnitEntry(unit);
                    if (e == null || !((e.animStateDriven && e.moveAnimId >= 0) || e.combatZ != 0f)) return;
                    if (!fresh.ContainsKey(e)) return;   // gate parity guard: only entries the dicts were built for
                    long guid = GuidToLong(GetMember(unit, "GUID"));
                    if (guid == 0) return;
                    guid = unchecked(guid ^ keySalt);
                    UnityEngine.Vector3 upos = UnityEngine.Vector3.zero; bool hasPos = false;
                    var pawnSeq = GetMember(unit, "Pawns") as System.Collections.IEnumerable;   // enumerated directly (twice) — no Cast().ToList() copy per unit per run
                    if (pawnSeq != null)
                        foreach (var pawn in pawnSeq)
                            if (GetMember(pawn, "Transform") is UnityEngine.Transform tr0) { upos = tr0.position; hasPos = true; break; }
                    bool moving = false;
                    if (hasPos)
                    {
                        if (e.stateLastPos.TryGetValue(guid, out var lastP)) moving = (upos - lastP).sqrMagnitude > 0.1f * 0.1f;
                        e.stateLastPos[guid] = upos;
                    }
                    // PIVOT IN PLACE (2026-08-22): a unit whose move start HAF is holding while it turns counts as moving
                    // from the moment the hold arms — so the PRE-MOVE one-shot (the howitzer folding) plays DURING the
                    // turn and the unit rolls off already folded, instead of folding on the first metre of travel
                    if (!moving && IsMoveHeld(unit)) moving = true;
                    if (!e.stateMoving.TryGetValue(guid, out bool wasMoving)) wasMoving = false;
                    if (wasMoving != moving)
                    {
                        if (wasMoving && !moving) e.stateStoppedAt[guid] = now;        // the AFTER one-shot window starts here
                        if (!wasMoving && moving) e.stateMoveStartedAt[guid] = now;    // the PRE-MOVEMENT one-shot window starts here
                    }
                    e.stateMoving[guid] = moving;
                    // combat FLIP timestamp (2026-08-19, combatZ): the ease ramp for the combat height offset starts
                    // when battle-lock changes state — either direction (dive at deployment, surface at resolution).
                    if (!e.stateCombat.TryGetValue(guid, out bool wasCombat)) wasCombat = false;
                    if (wasCombat != combat) e.stateCombatChangedAt[guid] = now;
                    e.stateCombat[guid] = combat;
                    float stoppedAt = e.stateStoppedAt.TryGetValue(guid, out var sAt) ? sAt : -1f;
                    float moveStartedAt = e.stateMoveStartedAt.TryGetValue(guid, out var mAt) ? mAt : -1f;
                    float combatChangedAt = e.stateCombatChangedAt.TryGetValue(guid, out var cAt) ? cAt : -1f;
                    seen[e].Add(guid);
                    if (pawnSeq != null)
                        foreach (var pawn in pawnSeq)
                            if (GetMember(pawn, "Transform") is UnityEngine.Transform tr)
                                fresh[e].Add(new StateSample { pos = tr.position, moving = moving, stoppedAt = stoppedAt, moveStartedAt = moveStartedAt, combat = combat, combatChangedAt = combatChangedAt });
                }
                foreach (var army in armies)
                {
                    if (army == null) continue;
                    // COMBAT stance: PresentationArmy.IsLockedByBattle (public bool on exactly the objects this walk
                    // iterates) is true from battle deployment until resolution — the pose hook swaps IDLE for the
                    // combat-idle clip while it holds. Reflection-safe: a missing member just reads false.
                    bool combat = false;
                    try { combat = GetMember(army, "IsLockedByBattle") is bool b && b; } catch { }
                    SampleUnit(GetMember(army, "PresentationUnit"), combat, 0L);
                }
                // BATTLE-DEPLOYED units: a battle spawns its own PresentationBattleUnit list whose PresentationUnits
                // live on the combat tiles — the army walk's sample sits at the STACK position (27u+ away in the
                // field report), so without this walk a deployed pawn never matches (no stance, no movement state).
                // Presentation.PresentationBattleReportController (a PresentationBattleController) -> Battles ->
                // AllUnits -> PresentationUnit (PresentationUnitHolder, same shape as the army's). Always combat=true.
                var bctl = CachedField(presType, "PresentationBattleReportController")?.GetValue(null);
                if (GetMember(bctl, "Battles") is System.Collections.IEnumerable battles)
                    foreach (var b in battles)
                        if (GetMember(b, "AllUnits") is System.Collections.IEnumerable allUnits)
                            foreach (var bu in allUnits)
                                SampleUnit(GetMember(bu, "PresentationUnit"), true, unchecked((long)0x5AAB5AAB5AAB5AABUL));
                foreach (var e in fresh.Keys)
                {
                    lock (e.stateSamples) { e.stateSamples.Clear(); e.stateSamples.AddRange(fresh[e]); }
                    var sn = seen[e];   // drop gone units from all four per-unit maps
                    PruneGone(e.stateLastPos, sn); PruneGone(e.stateMoving, sn); PruneGone(e.stateStoppedAt, sn); PruneGone(e.stateMoveStartedAt, sn);
                    PruneGone(e.stateCombat, sn); PruneGone(e.stateCombatChangedAt, sn);
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[State] ProcessAnimStates: " + ex); }
        }

        // DEPLOY-ON-STOP poll (main thread — Plugin.Update). For each deployOnStop model, record the render positions of the
        // pawns whose unit is currently MOVING (PresentationUnit.IsAnyPawnMoving). The pose hook then undeploys any pawn near
        // one of those and holds the deployed pose for the rest — an instant, per-pawn moving→pose mapping (no state machine).
        // Same presentation walk as MaybeRespawnPostLoad; scoped to VISIBLE our-model units, so AI/off-screen moves never reach it.
        static int deployFrame;
        [SessionScoped(Manual = "RearmModelRegistration nulls it")] static Dictionary<long, bool> deployMoveState;   // diagnostic: log each deploy unit's moving<->stopped transitions
        internal static void ProcessDeployState()
        {
            if (entries == null || !Plugin.UniversalInjectOn.Value) return;
            bool anyDeploy = false;
            foreach (var x in entries) if (x.deployOnStop) { anyDeploy = true; break; }   // manual foreach — entries.Any(closure) boxed the enumerator every frame at 60Hz
            if (!anyDeploy) return;
            if (++deployFrame % 3 != 0) return;   // ~20x/s; the ramp is dt-based so it stays smooth + framerate-independent
            try
            {
                // per-entry ramp step: dt (since last poll) / clip duration => normalized pose-time units this tick
                var now = UnityEngine.Time.time;
                var step = new Dictionary<ModelEntry, float>();
                var fresh = new Dictionary<ModelEntry, List<DeploySample>>();
                var seen = new Dictionary<ModelEntry, HashSet<long>>();
                foreach (var e in entries) if (e.deployOnStop)
                {
                    float dt = e.deployLastPoll > 0f ? Math.Min(now - e.deployLastPoll, 0.5f) : 0f;   // clamp a big first/stall gap
                    e.deployLastPoll = now;
                    float dur = e.animDuration > 0.001f ? e.animDuration : 1f;
                    step[e] = dt / dur * (e.deploySpeed > 0f ? e.deploySpeed : 1f);
                    fresh[e] = new List<DeploySample>();
                    seen[e] = new HashSet<long>();
                }
                var presType = GameBinding.Presentation;
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies != null)
                    foreach (var army in armies)
                    {
                        if (army == null) continue;
                        var unit = GetMember(army, "PresentationUnit");
                        if (unit == null) continue;
                        string uname = GetMember(GetMember(unit, "UnitDefinition"), "Name")?.ToString() ?? "";
                        if (uname.Length == 0) continue;
                        var e = FindEntryForUnitDefinition(uname);   // the unit's ONE entry (longest-match), then gate on deployOnStop
                        if (e == null || !e.deployOnStop) continue;
                        long guid = GuidToLong(GetMember(unit, "GUID"));
                        if (guid == 0) continue;
                        // Movement per PAWN via IsMoving(ignoreWaitToIdle: TRUE, isMovingAlongTilesOnly: TRUE). The unit-level
                        // IsAnyPawnMoving hardcodes ignoreWaitToIdle:false, so the wait-to-idle/turn settle after a unit stops
                        // reads as "moving" and the deploy snaps back to folded (barrel raises then drops to horizontal). We
                        // only want folded during ACTUAL tile-to-tile travel — ignoring the settle keeps the deployed pose held.
                        var pawnList = (GetMember(unit, "Pawns") as System.Collections.IEnumerable)?.Cast<object>().ToList();
                        // MOVEMENT = the unit's RENDER POSITION actually changed since the last poll (real tile traversal). This is
                        // INSTANT (no debounce lag) and settle-immune: the game's wait-to-idle / turn-in-place after stopping does
                        // NOT move the tile position, so a resting unit reads "not moving" and stays deployed, while a travelling one
                        // folds the moment it starts. (The deploy clip animates the SKELETON, not the pawn transform — no self-trigger.)
                        UnityEngine.Vector3 upos = UnityEngine.Vector3.zero; bool hasPos = false;
                        if (pawnList != null)
                            foreach (var pawn in pawnList)
                                if (GetMember(pawn, "Transform") is UnityEngine.Transform tr0) { upos = tr0.position; hasPos = true; break; }
                        bool moving = false;
                        if (hasPos)
                        {
                            if (e.deployLastPos.TryGetValue(guid, out var lastP)) moving = (upos - lastP).sqrMagnitude > 0.1f * 0.1f;
                            e.deployLastPos[guid] = upos;
                        }
                        if (!moving && IsMoveHeld(unit)) moving = true;   // pivot in place: fold while the held unit turns (see ProcessAnimStates)
                        // Clamp the deployed target just below 1.0: the pose sampler does Mathf.Repeat(Time,1), so a poseTime of
                        // EXACTLY 1.0 wraps to 0.0 = frame 0 = the FOLDED pose. Holding at 0.999 lands on the last real frame instead.
                        float target = UnityEngine.Mathf.Min(e.deployPoseTime, 0.999f);
                        float cur;
                        if (moving) cur = 0f;                                                          // travelling -> folded (instant)
                        else cur = e.deployProgress.TryGetValue(guid, out float p) ? UnityEngine.Mathf.MoveTowards(p, target, step[e]) : target;   // rest -> ramp to / HOLD fully deployed
                        e.deployProgress[guid] = cur;
                        seen[e].Add(guid);
                        if (deployMoveState == null) deployMoveState = new Dictionary<long, bool>();
                        if (!deployMoveState.TryGetValue(guid, out bool wasMoving) || wasMoving != moving)   // log on each moving<->stopped flip
                        { deployMoveState[guid] = moving; Plugin.Diag($"[Deploy] '{e.resourceName}' unit {guid} moving={moving} poseTime={cur:0.00}"); }
                        if (pawnList != null)
                            foreach (var pawn in pawnList)
                                if (GetMember(pawn, "Transform") is UnityEngine.Transform tr) fresh[e].Add(new DeploySample { pos = tr.position, poseTime = cur });
                    }
                foreach (var e in fresh.Keys)
                {
                    lock (e.deploySamples) { e.deploySamples.Clear(); e.deploySamples.AddRange(fresh[e]); }
                    var sn = seen[e];   // drop gone units (+ their last-pos entries — this map used to grow forever)
                    PruneGone(e.deployProgress, sn); PruneGone(e.deployLastPos, sn);
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Deploy] ProcessDeployState: " + ex); }
        }

        [ProcessLived("literal field-name table")] static readonly string[] RenderMatFields = { "currentRenderMaterial", "runTimeRenderMaterial" };   // hoisted — was a new[] per RenderOutput per FRAME
        static void TickOne(ModelEntry e)
        {
            // GREY retry: if the skin wasn't ready when ApplyGrey ran (build returned null), build it now from the
            // isolated layer's still-original _MainTex. Runs at most until the first successful build (then e.tex latches).
            if (NeedsAdjust(e) && e.tex == null && e.isolatedLayer != null)
                { e.tex = BuildAdjustedAtlas(e.isolatedLayer, e.brightness, e.desaturate, e.tintR, e.tintG, e.tintB, e.resourceName); e.texOwned = e.tex != null; }   // a texture we built — ours to Destroy on re-arm
            if (e.hostOutputLayer == null || e.tex == null) return;
            try
            {
                if (GetMember(e.hostOutputLayer, "RenderOutputs") is Array ros)
                    foreach (var ro in ros)
                        foreach (var fld in RenderMatFields)
                            if (GetMember(ro, fld) is UnityEngine.Material mat)
                            {
                                // Already ours -> skip the 7 texture sets. The re-set stays as the RECOVERY path (the
                                // game can recreate/reset the material, which this check detects by reference), but it
                                // no longer runs redundantly every frame on a stable material (perf pass 2026-07-19).
                                if (ReferenceEquals(mat.GetTexture("_MainTex"), e.tex)) continue;
                                if (_flatN == null) { _flatN = Solid(0.5f, 0.5f, 1f); _white = Solid(1f, 1f, 1f); _black = Solid(0f, 0f, 0f); _grey = Solid(0.5f, 0.5f, 0.5f); }
                                if (!stLogged) { stLogged = true; Plugin.Diag($"[Uni] {e.resourceName} host _MainTex_ST scale={mat.GetTextureScale("_MainTex")} offset={mat.GetTextureOffset("_MainTex")}"); }
                                mat.SetTexture("_MainTex", e.tex);
                                // Reset the atlas UV transform. The host's material crops _MainTex to its slice of a SHARED
                                // atlas (scale/offset != 1,0); left in place, our full atlas is sampled through that crop and
                                // the skin looks scrambled. Map our atlas 1:1 to the mesh UVs.
                                mat.SetTextureScale("_MainTex", UnityEngine.Vector2.one);
                                mat.SetTextureOffset("_MainTex", UnityEngine.Vector2.zero);
                                // neutralise the host's overlay maps so only OUR albedo shows (they're sampled with our
                                // UVs -> they'd smear the host's detail/camo across the model, worst at the stern).
                                mat.SetTexture("_NormalMap", _flatN);
                                mat.SetTexture("_AmbiantOcclusionMap", _white);
                                mat.SetTexture("_ColorMask", _black);
                                mat.SetTexture("_RoughnessMap", _grey);
                                mat.SetTexture("_MetallicMap", _black);
                            }
            }
            catch { }
        }

        static UnityEngine.Texture2D Solid(float r, float g, float b)
        { var t = new UnityEngine.Texture2D(1, 1); t.SetPixel(0, 0, new UnityEngine.Color(r, g, b, 1f)); t.Apply(); return t; }

        // Paint an atlas onto a (cloned) output layer's render materials — the TickOne recipe generalized for props:
        // _MainTex swapped, the atlas UV transform reset to 1:1 (the host cropped a slice of a SHARED atlas), and the
        // host's overlay maps neutralized so only our albedo shows.
        static void PaintLayer(object layer, UnityEngine.Texture2D tex, string tag)
        {
            try
            {
                if (!(GetMember(layer, "RenderOutputs") is Array ros)) return;
                int painted = 0;
                foreach (var ro in ros)
                    foreach (var fld in RenderMatFields)
                        if (GetMember(ro, fld) is UnityEngine.Material mat)
                        {
                            if (ReferenceEquals(mat.GetTexture("_MainTex"), tex)) continue;
                            if (_flatN == null) { _flatN = Solid(0.5f, 0.5f, 1f); _white = Solid(1f, 1f, 1f); _black = Solid(0f, 0f, 0f); _grey = Solid(0.5f, 0.5f, 0.5f); }
                            mat.SetTexture("_MainTex", tex);
                            mat.SetTextureScale("_MainTex", UnityEngine.Vector2.one);
                            mat.SetTextureOffset("_MainTex", UnityEngine.Vector2.zero);
                            mat.SetTexture("_NormalMap", _flatN);
                            mat.SetTexture("_AmbiantOcclusionMap", _white);
                            mat.SetTexture("_ColorMask", _black);
                            mat.SetTexture("_RoughnessMap", _grey);
                            mat.SetTexture("_MetallicMap", _black);
                            painted++;
                        }
                if (painted > 0)   // silent when stable (per-tick recovery path) — logs only actual (re)paints
                    Plugin.Diag($"[Props] '{tag}' prop layer painted ({painted} material(s), atlas {tex.width}x{tex.height})");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Props] PaintLayer: " + ex.Message); }
        }

        static UnityEngine.Texture2D LoadAtlas(int a, int b, int c, int d, string tag)
        {
            try
            {
                var guid = MakeGuid(a, b, c, d);
                var adb = GameBinding.AssetDatabase;
                if (guid == null || adb == null) return null;
                var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
                var g = load?.MakeGenericMethod(typeof(UnityEngine.Texture2D));
                if (g == null) { Plugin.Log.LogError($"[Uni] loadAtlas '{tag}': Amplitude LoadAsset/TryLoadAsset not resolved (game update?)"); return null; }
                var args = g.GetParameters().Length == 1 ? new object[] { guid } : new object[] { guid, null };
                var tex = g.Invoke(null, args) as UnityEngine.Texture2D;
                Plugin.Diag($"[Uni] loaded atlas '{tag}': " + (tex != null ? tex.name + " " + tex.width + "x" + tex.height : "NULL"));
                return tex;
            }
            catch (Exception e) { Plugin.Log.LogError("[Uni] atlas: " + e); return null; }
        }
    }
}
