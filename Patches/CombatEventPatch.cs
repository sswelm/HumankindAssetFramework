using System;
using System.Reflection;
using HarmonyLib;

namespace HumankindAssetFramework
{
    // FIRING-ON-ATTACK (complete). A bombard raises SimulationEvent_ArtilleryStrikeStarted; we read the ArtilleryStrike's
    // StrikerUnit.UnitDefinition, match it to our registry entry, and enqueue the firing unit/army GUID so ONLY that pawn
    // plays the clip once (barrel elevates on the shot) — per-instance, resolved on the main thread. See docs/Firing-On-Attack.md.
    // Discovery history: this event was confirmed via a multi-event probe (BattleStarted/Ready/AirStrike/UnitDamage) —
    // only ArtilleryStrikeStarted fired for a unit bombard; those probes are removed now the hook is proven. To extend
    // firing-on-attack to bombers (AirStrikeStarted) or melee (BattleStarted), re-add a probe the same way and match the attacker.
    internal static class FireProbe
    {
        // Resolve a SimulationEvent's static Raise() and log whether the hook attached (so we know at patch time, not just
        // when it fires). All these events live in Amplitude.Mercury.Simulation.
        internal static MethodBase Resolve(string type, string label)
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Simulation." + type);
            var m = t != null ? AccessTools.Method(t, "Raise") : null;
            if (m != null) Plugin.Log.LogInfo("[Fire] hooked " + label);
            else Plugin.Log.LogWarning("[Fire] NOT found: " + type + ".Raise");
            return m;
        }
        // Reflection reads go through the shared UniversalInject.GetMember (cached, finds public + non-public). This local
        // copy used to be FIELD-first and uncached; consolidated onto the one implementation. GetMember is PROPERTY-first —
        // audited the call-sites (StrikerUnit/StrikerArmy/UnitDefinition/GUID/AttackerEmpireIndex/TargetTileIndex are
        // properties; 'striker' is a lowercase field with no competing property) so resolution is unchanged, and GUID
        // already resolves property-first successfully in UniversalInject. Int keeps its null -> int.MinValue sentinel
        // (GetMember returns null on a missing member, which Convert.ToInt32 would otherwise throw on).
        internal static object Member(object o, string name) => UniversalInject.GetMember(o, name);
        internal static int Int(object o, string name) { var v = Member(o, name); return v == null ? int.MinValue : Convert.ToInt32(v); }
    }

    [HarmonyPatch] internal static class Hk_ArtilleryStrike
    {
        static MethodBase TargetMethod() => FireProbe.Resolve("SimulationEvent_ArtilleryStrikeStarted", "ArtilleryStrikeStarted");
        // Raise(object sender, ArtilleryStrike strike) — __1 is the strike (StrikerUnit / StrikerArmy / TargetTileIndex).
        static void Postfix(object __1)
        {
            try
            {
                int emp = FireProbe.Int(__1, "AttackerEmpireIndex"), tile = FireProbe.Int(__1, "TargetTileIndex");
                object unit = FireProbe.Member(__1, "StrikerUnit");
                string unitDef = FireProbe.Member(unit, "UnitDefinition")?.ToString() ?? "";
                var entry = UniversalInject.FindEntryForUnitDefinition(unitDef);
                if (entry != null)
                {
                    // Enqueue the firer's GUID(s) so only THAT pawn animates (per-instance). We're on the sim thread here (no
                    // Unity access) — Plugin.Update drains the queue, resolves the PresentationUnit, and records its pawn
                    // positions. Enqueue BOTH the unit's and the army's GUID: the on-map presentation entity is an army, so
                    // PresentationUnit.GUID may be the army's — matching either covers it. (SimulationEntityGUID -> long.)
                    long uguid = UniversalInject.GuidToLong(FireProbe.Member(unit, "GUID"));
                    long aguid = UniversalInject.GuidToLong(FireProbe.Member(FireProbe.Member(__1, "StrikerArmy"), "GUID"));
                    if (uguid != 0) entry.fireGuidQueue.Enqueue(uguid);
                    if (aguid != 0 && aguid != uguid) entry.fireGuidQueue.Enqueue(aguid);
                    if (uguid != 0 || aguid != 0) Plugin.Diag($"[Fire] *** OUR MODEL '{entry.resourceName}' FIRED (unit {uguid}, army {aguid}, empire={emp} targetTile={tile}) — queued");
                    else Plugin.Log.LogWarning($"[Fire] '{entry.resourceName}' fired but GUIDs unreadable — can't target the instance");
                }
                else
                    Plugin.Diag($"[Fire] >>> ArtilleryStrikeStarted FIRED (not ours): {unitDef}");
            }
            catch (Exception e) { Plugin.Log.LogError("[Fire] artillery postfix: " + e); }
        }
    }

    // STATE-DRIVEN ATTACK trigger (Phase 2). Every pawn ranged shot funnels through
    // PawnRangedFightSequence.InitializeCommon(shooter, ...) — all five constructors call it (decompiled,
    // Assembly-CSharp) — so one postfix covers battle volleys, unit-target shots, and district bombards. The
    // sequence is built on the presentation/main thread, so the handler reads the shooter's Transform directly.
    [HarmonyPatch] internal static class Hk_PawnRangedFight
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PawnRangedFightSequence");
            var m = t != null ? AccessTools.Method(t, "InitializeCommon") : null;
            if (m != null) Plugin.Log.LogInfo("[Fire] hooked PawnRangedFightSequence (state-driven attack)");
            else Plugin.Log.LogWarning("[Fire] NOT found: PawnRangedFightSequence.InitializeCommon — state-driven attack clips won't trigger");
            return m;
        }
        // InitializeCommon(PresentationPawn shooter, bool dies, bool delay, bool miss, float projectileSpread)
        static void Postfix(object __0)
        {
            try { UniversalInject.OnPawnAttack(__0, "ranged shot"); }
            catch (Exception e) { Plugin.Log.LogError("[Fire] ranged-fight postfix: " + e); }
        }
    }

    // STATE-DRIVEN MELEE ATTACK trigger (2026-07-22): close-combat units (the Abomination animal) never fire a ranged
    // shot, so PawnRangedFightSequence never runs and their attack clip stayed silent (movement + the donor's maul/scratch
    // SOUND played, no bite animation). Two false starts before the right seam:
    //   1. PawnMeleeFightSequence's ctor — a STRUCT, Harmony caught it unreliably.
    //   2. PawnActionMeleeStartFight.StartPawnAction() — runs ONCE per fight START, not per swing (log: 1 fire, 1 ATTACK,
    //      while the fight had several swings/FX). The pose system was proven correct there (1 fire -> exactly 1 ATTACK).
    // The REAL per-swing method is PawnActionMeleeStartFight.StartPairMeleeAttack() — called once per fight SEQUENCE
    // (= per swing) from StartNewSequence, and it runs striker.AttackFSM.Start(...) (the actual bite). Read the private
    // `striker` field (the pawn swinging THIS sequence — it's set to pair.AttackerPawn OR pair.DefenderPawn for a
    // retaliation, NOT always this.Pawn), and arm that pawn's clip. Per-pawn restart in OnPawnAttack handles fast swings.
    [HarmonyPatch] internal static class Hk_PawnMeleeFight
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PawnActionMeleeStartFight");
            var m = t != null ? AccessTools.Method(t, "StartPairMeleeAttack") : null;
            if (m != null) Plugin.Log.LogInfo("[Fire] hooked PawnActionMeleeStartFight.StartPairMeleeAttack (per-swing melee attack)");
            else Plugin.Log.LogWarning("[Fire] NOT found: PawnActionMeleeStartFight.StartPairMeleeAttack — melee attack clips won't trigger");
            return m;
        }
        static void Postfix(object __instance)
        {
            try
            {
                var striker = FireProbe.Member(__instance, "striker");   // the pawn actually swinging this sequence
                if (striker != null) UniversalInject.OnPawnAttack(striker, "melee swing", playSound: false, armAnim: true);   // ANIMATION only; the SOUND fires from the earlier fight-start hook
            }
            catch (Exception e) { Plugin.Log.LogError("[Fire] melee-swing postfix: " + e); }
        }
    }

    // (The melee attack SOUND fires EARLIER than any pawn-fight hook — from UnitActionFaceEnemy.StartUnitAction, handled in
    // UniversalInject.TryEarlyAttackSound: the attacker turns to face the enemy before the strike.)

    // SILENCE DONOR AUDIO (2026-07-23): drop every Wwise post on an emitter we've marked silenced. A custom creature that
    // reuses a donor (the Abomination borrows a BEAR) inherits the donor's sounds — the idle GROWL and the combat
    // MAUL/SCRATCH — because they ride in on the reused animator/pawn-description, not on any nullable data field. Both
    // funnel through the SAME chokepoint: AudioEmitter.PostEvent(AudioEventHandle) — the idle loop from
    // PresentationSubPawn.InitializeAudio (decomp 75273) and the melee SFX from MecanimEvent.SFXEntry ->
    // audioEmitter.PostEvent (decomp 371876/371895/373278). So this one prefix silences both. Gated by emitter InstanceID
    // in UniversalInject._silencedEmitterIds (populated per-pawn by ProcessEngineAudio for silenceDonorAudio units);
    // returns false to skip the original post. Runs for EVERY emitter in the game, so the empty-set fast path keeps it
    // ~free until a unit opts in. Our OWN custom WAVs use Unity AudioSource (not this emitter), so they're unaffected.
    [HarmonyPatch] internal static class Hk_SilenceAudio
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.AudioEmitter;
            var m = t != null ? AccessTools.Method(t, "PostEvent") : null;   // instance PostEvent(AudioEventHandle) — the one overload
            if (m != null) Plugin.Log.LogInfo("[Audio] hooked AudioEmitter.PostEvent (donor-audio silence)");
            else Plugin.Log.LogWarning("[Audio] NOT found: AudioEmitter.PostEvent — silenceDonorAudio won't work");
            return m;
        }
        static bool Prefix(object __instance)
        {
            try
            {
                if (UniversalInject._silencedEmitterIds.Count == 0) return true;   // fast path: nobody opted in
                if (__instance is UnityEngine.Object o && UniversalInject._silencedEmitterIds.Contains(o.GetInstanceID())) return false;   // suppress this post
            }
            catch { }
            return true;
        }
    }

    // ---- EARLY ATTACK SOUND: UnitActionFaceEnemy.StartUnitAction is the earliest presentation seam for "our unit
    // commits to the strike" — it fires as the attacker turns to face the enemy, before the melee swing. The handler
    // plays the attack roar there (gated to our attacker + a per-attacker min-gap; see TryEarlyAttackSound). ----
    [HarmonyPatch] internal static class Hk_EarlyAttackSound
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Presentation.UnitActionFaceEnemy");
            var m = t != null ? AccessTools.Method(t, "StartUnitAction") : null;
            if (m == null) Plugin.Log.LogWarning("[Audio] NOT found: UnitActionFaceEnemy.StartUnitAction — early attack sound off");
            return m;
        }
        static void Postfix(object __instance)
        {
            try { UniversalInject.TryEarlyAttackSound(__instance); } catch { }
        }
    }

    // ---- DEATH CUE (2026-07-23): PresentationPawn.TriggerDeath fires exactly once per dying pawn, presentation-side,
    // as its death FSM starts — the clean seam for a death rattle/scream (per-entry min-gap in the handler keeps a
    // wiped stack from chorusing). ----
    [HarmonyPatch] internal static class Hk_PawnDeath
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PresentationPawn");
            var m = t != null ? AccessTools.Method(t, "TriggerDeath") : null;
            if (m != null) Plugin.Log.LogInfo("[Sound] hooked PresentationPawn.TriggerDeath (death cue)");
            else Plugin.Log.LogWarning("[Sound] NOT found: PresentationPawn.TriggerDeath — death sounds won't trigger");
            return m;
        }
        static void Postfix(object __instance)
        {
            try { UniversalInject.OnPawnDeath(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[Sound] death postfix: " + e); }
        }
    }

    // ---- DONOR VFX SUPPRESSION (2026-07-24): drop the donor's MecanimEvent VFX (misplaced muzzle flashes) for
    // opted-in units at the launch chokepoint. VFX only — StartSFXEvent/Wwise sounds are deliberately untouched. ----
    [HarmonyPatch] internal static class Hk_SilenceVfx
    {
        // StartVFXEvent has MULTIPLE overloads (AccessTools.Method by name threw AmbiguousMatchException — the
        // silent 18/19 failure); enumerate and patch them all.
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Animation.MecanimEventInterpreter");
            int n = 0;
            if (t != null)
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == "StartVFXEvent") { n++; yield return m; }
            if (n == 0) Plugin.Log.LogWarning("[Vfx] NOT found: MecanimEventInterpreter.StartVFXEvent — silenceDonorVfx won't work");
            else Plugin.Log.LogInfo($"[Vfx] hooked MecanimEventInterpreter.StartVFXEvent ({n} overload(s), donor VFX suppression)");
        }
        // Suppress-only (no argument injection): a ref-object TRS prefix IL-failed on the Vector3-only overload
        // (the 18/19 incident) — muzzle repositioning lives in the GetBoneTRS redirect + offset compensation instead.
        static bool Prefix(object __instance)
        {
            try { return !UniversalInject.SuppressVfxFor(__instance); }
            catch { return true; }
        }
    }

    // ---- MUZZLE OFFSET STASH (2026-07-24): while AlterationFireProjectile.StartEvent runs, record the donor's
    // socket-local offset + position-socket name so the GetBoneTRS redirect can return a pre-compensated TRS
    // (see UniversalInject.OnFireProjectileStart). Prefix/Postfix bracket the call; main-thread only. ----
    [HarmonyPatch] internal static class Hk_FireProjStash
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Animation.AlterationFireProjectile");
            var m = t != null ? AccessTools.Method(t, "StartEvent") : null;
            if (m != null) Plugin.Log.LogInfo("[Muzzle] hooked AlterationFireProjectile.StartEvent (offset stash)");
            else Plugin.Log.LogWarning("[Muzzle] NOT found: AlterationFireProjectile.StartEvent — muzzle offset compensation off");
            return m;
        }
        static void Prefix(object mecanimEvent)
        {
            try { UniversalInject.OnFireProjectileStart(mecanimEvent); } catch { }
        }
        static void Postfix()
        {
            try { UniversalInject.OnFireProjectileEnd(); } catch { }
        }
    }

    // ---- BATTLE-START WAR CRY (2026-07-23): SimulationEvent_BattleStarted.Raise(sender, battle) on the SIM thread.
    // The handler only does managed reads (walk the battle's groups for our unit definitions) and queues; the cry
    // plays on the main thread via UniversalInject.ProcessBattleCries. ----
    [HarmonyPatch] internal static class Hk_BattleStarted
    {
        static MethodBase TargetMethod() => FireProbe.Resolve("SimulationEvent_BattleStarted", "BattleStarted (war cry)");
        // Raise(object sender, Battle battle) — __1 is the battle
        static void Postfix(object __1)
        {
            try { UniversalInject.OnBattleStarted(__1); }
            catch (Exception e) { Plugin.Log.LogError("[Sound] battle-start postfix: " + e); }
        }
    }
}
