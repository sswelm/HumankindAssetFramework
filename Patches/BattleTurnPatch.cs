using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;

namespace HumankindAssetFramework
{
    // BATTLE TURN (spike/battle-turn 2026-08-05, VERIFIED on the TowedGunHowitzers map bombard): make a unit
    // TURN believably toward its target before the attack fires, instead of the vanilla instant snap.
    //
    // ARCHITECTURE (docs/Turn-Ease.md). Two different worlds needed two different treatments:
    //
    //  WORLD MAP (verified): the choreography rotation FSM is a NO-OP out here — StepTurning initializes 0->0
    //  and completes on its first call (measured); the visible facing is stamped straight into the GPU pawn
    //  data by PresentationArtilleryStrike.TriggerBombardAnimation via FlipPawnsGrid(angle, Teleport). So the
    //  smoothing lives at the ObjectSpace seam (UniversalInject.ApplyTurnEase, now for ALL our entries), and
    //  the attack effects are each held by the SAME remaining-turn time:
    //    - Hk_ArtilleryHold      shell launch + impact (the controller's plain scheduled delays)
    //    - Hk_BombardAnimHold    the attack-pose teleport (whose mecanim events emit muzzle flash + shot sound)
    //    - FireInstance.waitAlign our own recoil clip (armed held, released on alignment — Combat.cs)
    //  All three compute remaining-turn as eased-yaw vs the pawn TRANSFORM (already flipped to the target in
    //  that same frame) — TurnState.targetYaw only refreshes on the NEXT pose frame, the race that sank v3/v4.
    //
    //  BATTLE (EXPERIMENTAL, untested, off by default): deployed battles rotate pawn Transforms for real and
    //  start attacks through PawnActionRangedStartAttack. Hk_BattleHoldFire + Hk_BattleAttackGate hold those
    //  until the turn ends — enable with hold=1 in the dial to trial them.
    //
    // GRAVEYARD (lessons, do not re-attempt):
    //  - postfixing RotationPawnStateMachine.GetUnanimatedRotationProgress NEVER executed even while its
    //    unanimated branch demonstrably ran — the JIT inlines it into StepTurning. Patch methods that are
    //    invoked through the StaticSteps DELEGATE arrays instead (delegates never inline the callee).
    //  - bumping AttackFSM.Start's delayDuration once at Start computed 0: the facing snap lands AFTER Start,
    //    so at that instant the pawn still reads aligned. Holds must be dynamic or transform-based.
    //
    // Live dial BepInEx/config/haf_battleturn.txt (~1/s poll): hold= (battle experiment), diag= (forensics).
    // The map-bombard chain needs no dial: it keys off turn ease itself (per-model turnRate / haf_turnease.txt).
    internal static class BattleTurn
    {
        internal static bool holdFire = false;  // EXPERIMENTAL battle path: delay PawnActionRangedStartAttack until the turn completes
        internal static bool diag = false;      // verbose probe logging (turn starts/routes) — forensics only
        internal const float HoldDeadline = 4f; // failsafe: never hold an attack longer than this

        static string sig;
        static float nextPoll;
        internal static void Poll()
        {
            if (UnityEngine.Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = UnityEngine.Time.realtimeSinceStartup + 1f;
            try
            {
                var path = Path.Combine(Paths.ConfigPath, "haf_battleturn.txt");
                string txt = File.Exists(path) ? File.ReadAllText(path) : "";
                if (txt == sig) return;
                sig = txt;
                var problems = new List<string>();
                var d = BattleTurnDial.Parse(txt, problems);         // PURE parse — Patches/DialConfig.cs, unit-tested
                UniversalInject.LogDialProblems("haf_battleturn.txt", problems);
                holdFire = d.HoldFire; diag = d.Diag;
                Plugin.Log.LogInfo($"[BattleTurn] hold={(holdFire ? 1 : 0)} (battle experiment), diag={(diag ? 1 : 0)}");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[BattleTurn] " + ex.Message); }
        }

        // Diagnostic helper: a pawn's unit-definition name (readable identity — pawn ToString is just pawnId).
        internal static string UnitOf(object pawn)
        {
            try
            {
                var unit = UniversalInject.GetMember(pawn, "PresentationUnit");
                return UniversalInject.GetMember(unit, "UnitDefinition")?.ToString() ?? pawn?.ToString() ?? "?";
            }
            catch { return "?"; }
        }
    }

    // ---- STRIKE CLOCK PREP (sync fix 2026-08-05): arm the strike's aim overrides + its ONE shared release
    // time BEFORE anything inside TriggerArtilleryStrikeVisuals runs (the flip, the attack-pose teleport, the
    // launch/hit schedules). Every consumer then reads the same clock, so recoil, muzzle flash, shot sound,
    // smoke and shell stay in lockstep — computing holds per-consumer desynced the bang from the animation. ----
    [HarmonyPatch] internal static class Hk_ArtilleryAimPrep
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.PresentationArtilleryStrike;
            var m = t != null ? AccessTools.Method(t, "TriggerArtilleryStrikeVisuals") : null;
            if (m != null) Plugin.Log.LogInfo("[BattleTurn] hooked TriggerArtilleryStrikeVisuals (strike clock prep)");
            else Plugin.Log.LogWarning("[BattleTurn] NOT found: TriggerArtilleryStrikeVisuals — strike effects may drift ~0.25s apart");
            return m;
        }
        static void Prefix(object __instance)
        {
            try { UniversalInject.TurnHoldForStrike(__instance); } catch { }
        }
    }

    // ---- LAUNCH POSE REFRESH (shell/smoke position fix): re-capture + re-aim the shell's spawn pose at FIRE
    // time — vanilla captured it at schedule time, i.e. at the PRE-pivot barrel. Invoked through the strike's
    // launch-action delegate, which calls through the detour (delegate-invoked = inline-safe). ----
    [HarmonyPatch] internal static class Hk_ArtilleryLaunchPose
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.PresentationArtilleryStrike;
            var m = t != null ? AccessTools.Method(t, "TriggerArtilleryStrikeFX") : null;
            if (m != null) Plugin.Log.LogInfo("[BattleTurn] hooked TriggerArtilleryStrikeFX (launch pose refresh)");
            else Plugin.Log.LogWarning("[BattleTurn] NOT found: TriggerArtilleryStrikeFX — shell will spawn at the pre-turn pose");
            return m;
        }
        static void Prefix(object __instance)
        {
            try { UniversalInject.RefreshStrikeLaunchPose(__instance); } catch { }
        }
    }

    // ---- MAP BOMBARD hold (VERIFIED): a world-map bombard never touches the battle attack actions —
    // PresentationArtilleryStrike.TriggerBombardAnimation does FlipPawnsGrid(angle, Teleport) (THE instant
    // facing snap) plus AttackFSM.TeleportToSimpleAttack(), and the shell + impact are fired by PLAIN
    // SCHEDULED DELAYS on PresentationArtilleryStrikeController. Prefix both Schedule calls and add the
    // striker's remaining turn-ease time to the delay. Launch and hit get the same hold (computed twice in
    // the same frame, same value), so flight time is preserved. Vanilla strikers: +0, untouched. ----
    [HarmonyPatch] internal static class Hk_ArtilleryHold
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var t = GameBinding.PresentationArtilleryStrikeController;
            int n = 0;
            if (t != null)
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == "ScheduleArtilleryStrikeProjectileLaunch" || m.Name == "ScheduleArtilleryStrikeHit") { n++; yield return m; }
            if (n > 0) Plugin.Log.LogInfo($"[BattleTurn] hooked artillery strike schedule ({n} method(s) — map bombard waits for the turn)");
            else Plugin.Log.LogWarning("[BattleTurn] NOT found: PresentationArtilleryStrikeController schedule methods — map bombard won't wait");
        }
        // (PresentationArtilleryStrike artilleryStrike, float delay)
        static void Prefix(object __0, ref float __1)
        {
            try { __1 += UniversalInject.TurnHoldForStrike(__0); } catch { }
        }
    }

    // ---- MAP BOMBARD attack pose (VERIFIED): the scheduler hold above delays the SHELL, but
    // TriggerBombardAnimation also slams the animator into the attack state via TeleportToSimpleAttack() —
    // and that animator emits the MUZZLE FLASH and the SHOT SOUND on its own timeline, still mid-pivot.
    // Defer the teleport by the same transform-vs-eased hold; a pending queue ticked from Plugin.Update
    // replays it (re-entrancy flag lets the replay through the prefix). The mecanim clip then starts at
    // +hold, exactly matching the shifted launch schedule (hold + triggerDelay), so flash, sound, shell and
    // our held recoil clip all land together at alignment. Single caller = the map bombard. ----
    [HarmonyPatch] internal static class Hk_BombardAnimHold
    {
        class Pending { public object fsm; public float due; public float start; public bool fixedDue; }
        static readonly List<Pending> pending = new List<Pending>();
        static MethodInfo miTeleport;
        static bool replaying;
        static MethodBase TargetMethod()
        {
            var t = GameBinding.AttackAnimationStateMachine;
            miTeleport = t != null ? AccessTools.Method(t, "TeleportToSimpleAttack") : null;
            if (miTeleport != null) Plugin.Log.LogInfo("[BattleTurn] hooked AttackFSM.TeleportToSimpleAttack (muzzle/sound wait for the turn)");
            else Plugin.Log.LogWarning("[BattleTurn] NOT found: TeleportToSimpleAttack — bombard muzzle flash/sound won't wait");
            return miTeleport;
        }
        static bool Prefix(object __instance)
        {
            try
            {
                if (replaying) return true;
                var pawn = UniversalInject.GetMember(__instance, "ownerPawn");
                if (pawn == null) return true;
                // PREFERRED: the strike's ONE shared release time (armed by Hk_ArtilleryAimPrep before the
                // flip) — the launch/hit schedules use the same clock, so anim, sound, smoke and shell stay
                // in lockstep. Fallback: own estimate + deadline re-check (no strike context, e.g. dial off).
                float now = UnityEngine.Time.time;
                if (UniversalInject.GetMember(pawn, "Transform") is UnityEngine.Transform tr &&
                    UniversalInject.TryAimRelease(tr.position, out float rel))
                {
                    if (rel <= now) return true;   // clock already elapsed — fire now
                    pending.Add(new Pending { fsm = __instance, due = rel, start = now, fixedDue = true });
                    Plugin.Log.LogInfo($"[BattleTurn] bombard attack pose deferred +{rel - now:F2}s (strike clock)");
                    return false;
                }
                float hold = UniversalInject.TurnHoldTransformSeconds(pawn);
                if (hold <= 0f) return true;
                pending.Add(new Pending { fsm = __instance, due = now + hold, start = now });
                Plugin.Log.LogInfo($"[BattleTurn] bombard attack pose deferred +{hold:F2}s (muzzle/sound wait for the turn)");
                return false;
            }
            catch { return true; }
        }
        // Ticked from Plugin.Update: replay deferred teleports when their hold elapses. At the deadline the
        // remaining turn is RE-CHECKED (the true-bearing aim override registers a frame after the defer, so the
        // original hold was computed against the hex-quantized flip angle — up to 30 deg short); still-misaligned
        // pawns get pushed back in small steps until aligned or the 4 s cap from the ORIGINAL defer expires.
        internal static void Tick()
        {
            if (pending.Count == 0 || miTeleport == null) return;
            float now = UnityEngine.Time.time;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (now < pending[i].due) continue;
                var fsm = pending[i].fsm;
                if (!pending[i].fixedDue && now - pending[i].start < 4f)
                {
                    try
                    {
                        var pawn = UniversalInject.GetMember(fsm, "ownerPawn");
                        if (pawn != null && UniversalInject.TurnHoldTransformSeconds(pawn) > 0f)
                        { pending[i].due = now + 0.1f; continue; }   // aim not reached yet — check again shortly
                    }
                    catch { }
                }
                pending.RemoveAt(i);
                ReplayAligned(fsm);
            }
        }

        // Start the attack clip DETERMINISTICALLY at frame 0 (shell/smoke sync fix): the vanilla teleport plays
        // the state with randomOffset:true — a random clip phase — while the artillery scheduler times the shell
        // + launch smoke to the fire event's literal clip time. In vanilla the mismatch hid in the same-frame
        // chaos; on our shared clock it showed as a per-shot random shell/smoke drift (the sound + FLASH ride
        // the mecanim events, so they stayed with the anim). randomOffset:false makes the mecanim fire moment
        // land exactly on triggerDelay = NormalizedTime x clipDuration — the scheduler's own arithmetic.
        static MethodInfo miPlayState; static object simpleAttackId, capAttack; static bool playResolveFailed;
        static void ReplayAligned(object fsm)
        {
            try
            {
                var pawn = UniversalInject.GetMember(fsm, "ownerPawn");
                if (pawn != null && !playResolveFailed)
                {
                    if (miPlayState == null)
                    {
                        foreach (var m in pawn.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            if (m.Name == "PlayAnimationState" && m.GetParameters().Length == 7) { miPlayState = m; break; }
                        var avn = AccessTools.TypeByName("AnimationVariableNames")
                               ?? AccessTools.TypeByName("Amplitude.Mercury.Presentation.AnimationVariableNames")
                               ?? AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationVariableNames");
                        simpleAttackId = avn != null ? AccessTools.Field(avn, "SimpleAttackState")?.GetValue(null) : null;
                        if (miPlayState != null && simpleAttackId != null)
                            capAttack = Enum.Parse(miPlayState.GetParameters()[2].ParameterType, "Attack");
                        if (miPlayState == null || simpleAttackId == null || capAttack == null)
                        { playResolveFailed = true; Plugin.Log.LogWarning("[BattleTurn] PlayAnimationState/SimpleAttackState not resolvable — falling back to the random-offset teleport (shell may drift)"); }
                    }
                    if (!playResolveFailed)
                    {
                        var subs = UniversalInject.GetMember(pawn, "SubPawns");
                        int cnt = Convert.ToInt32(UniversalInject.GetMember(pawn, "SubPawnCount"));
                        miPlayState.Invoke(pawn, new object[] { simpleAttackId, 0f, capAttack, false, subs, cnt, true });
                        return;
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[BattleTurn] aligned replay failed (" + ex.Message + ") — using the vanilla teleport"); }
            try { replaying = true; miTeleport.Invoke(fsm, null); }
            catch (Exception ex) { Plugin.Log.LogWarning("[BattleTurn] deferred teleport: " + ex.Message); }
            finally { replaying = false; }
        }
    }

    // ---- BATTLE HULL AIM (2026-08-06): when a battle ranged fight is choreographed, arm the aim override
    // for OUR turretless land/ship models — vanilla NEVER rotates a vehicle hull in battle (vehicles aim by
    // turret slot only, invalid on our rigs), so a casemate gun aimed with NOTHING. The 5-arg
    // AddPawnRangedFightSequence is the funnel every battle volley passes through. ----
    [HarmonyPatch] internal static class Hk_BattleHullAim
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.UnitActionRangedFightSequence;
            if (t != null)
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == "AddPawnRangedFightSequence" && m.GetParameters().Length == 5)
                    { Plugin.Log.LogInfo("[BattleTurn] hooked AddPawnRangedFightSequence (battle hull aim)"); return m; }
            Plugin.Log.LogWarning("[BattleTurn] NOT found: AddPawnRangedFightSequence — turretless vehicles won't hull-aim in battles");
            return null;
        }
        static void Prefix(object __0)   // fightSequence — arm BEFORE the actions are built so the ease target leads
        {
            try { UniversalInject.BattleAimPrep(__0); } catch { }
        }
    }

    // ---- EXPERIMENTAL battle path (untested, hold=1 to trial): deployed battles start ranged attacks via
    // PawnActionRangedStartAttack. OnReadyToStart is called from StartPawnAction and re-tried from
    // UpdatePawnAction every frame until isReadyToStart latches true — the action's own wait loop. While the
    // shooter's RotationFSM is running (the group's LookAt is mid-turn) skip the original AND un-latch
    // isReadyToStart, so the loop retries next frame; the frame the turn completes, the attack (animation,
    // projectile timing, everything downstream) starts unchanged. A pawn already facing its target never
    // defers. When holding, our fireOnAttack clip arm moves HERE from the sequence-ctor hook (Hk_PawnRangedFight
    // skips its early arm) so a custom model's recoil clip stays in sync with the delayed real attack. ----
    [HarmonyPatch] internal static class Hk_BattleHoldFire
    {
        static FieldInfo fiReady;
        static MethodBase TargetMethod()
        {
            var t = GameBinding.PawnActionRangedStartAttack;
            var m = t != null ? AccessTools.Method(t, "OnReadyToStart") : null;
            fiReady = t != null ? AccessTools.Field(t, "isReadyToStart") : null;
            if (m != null && fiReady != null) Plugin.Log.LogInfo("[BattleTurn] hooked PawnActionRangedStartAttack.OnReadyToStart (battle hold-fire, experimental)");
            else Plugin.Log.LogWarning("[BattleTurn] NOT found: PawnActionRangedStartAttack.OnReadyToStart/isReadyToStart — battle hold-fire off");
            return fiReady != null ? m : null;   // no field, no patch — skipping without the un-latch would stall attacks
        }
        static float nextLog;
        static bool Prefix(object __instance)
        {
            try
            {
                if (!BattleTurn.holdFire) return true;
                var pawn = UniversalInject.GetMember(__instance, "pawn");
                if (pawn == null) return true;
                bool turning = UniversalInject.GetMember(pawn, "IsTurning") is bool b && b;
                // vehicles never rotate their transform in battle (IsTurning is meaningless for them) — the
                // hull-aim override + eased ObjectSpace yaw is their turn; wait on that alignment too
                if (!turning && UniversalInject.GetMember(pawn, "Transform") is UnityEngine.Transform htr)
                    turning = UniversalInject.TurnMisalignAt(htr.position) > 8f;
                if (turning)
                {
                    // failsafe: creationTime is set from Time.time when the action spawns (base PresentationChoreographyAction)
                    var ct = UniversalInject.GetMember(__instance, "creationTime");
                    if (ct == null || UnityEngine.Time.time - Convert.ToSingle(ct) < BattleTurn.HoldDeadline)
                    {
                        if (UnityEngine.Time.realtimeSinceStartup > nextLog)
                        {
                            nextLog = UnityEngine.Time.realtimeSinceStartup + 0.5f;
                            Plugin.Log.LogInfo("[BattleTurn] holding ranged attack — shooter still turning");
                        }
                        fiReady.SetValue(__instance, false);   // un-latch so UpdatePawnAction retries next frame
                        return false;                          // defer the attack — still turning
                    }
                }
                // turn done (or failsafe): the attack starts NOW — arm our fire-on-attack clip at the real moment
                Plugin.Log.LogInfo("[BattleTurn] ranged attack released (turn complete)");
                UniversalInject.OnPawnAttack(pawn, "ranged attack start (post-turn)");
            }
            catch { }
            return true;
        }
    }

    // ---- EXPERIMENTAL battle path #2 (untested, hold=1 to trial): a dynamic gate on the attack FSM's delay
    // step, for battle attack starts that bypass the choreography action (chained volleys). StepWaitingDelay
    // is the attack FSM's first step, ticked every frame and invoked through the StaticSteps delegate array
    // (detour-safe — the inlining lesson). While the owner pawn is one of OUR turn-easing entries and still
    // >8 deg off its target, keep returning 'not done'. 4 s failsafe per FSM instance. Other FSM types share
    // this step; the attack-type check keeps them untouched. ----
    [HarmonyPatch] internal static class Hk_BattleAttackGate
    {
        class HoldTag { public float since; }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, HoldTag> holds =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, HoldTag>();
        static MethodBase TargetMethod()
        {
            var t = GameBinding.AbstractAnimationStateMachine;
            var m = t != null ? AccessTools.Method(t, "StepWaitingDelay") : null;
            if (m != null) Plugin.Log.LogInfo("[BattleTurn] hooked AbstractAnimationStateMachine.StepWaitingDelay (battle attack gate, experimental)");
            else Plugin.Log.LogWarning("[BattleTurn] NOT found: AbstractAnimationStateMachine.StepWaitingDelay — battle attack gate off");
            return m;
        }
        // StepWaitingDelay(AbstractAnimationStateMachine fsm, bool isFirstRun)
        static bool Prefix(object __0, ref bool __result)
        {
            try
            {
                if (!BattleTurn.holdFire || __0 == null) return true;
                var at = GameBinding.AttackAnimationStateMachine;
                if (at == null || !at.IsInstanceOfType(__0)) return true;   // only the ATTACK FSM is gated
                var pawn = UniversalInject.GetMember(__0, "ownerPawn");
                if (pawn == null || !(UniversalInject.GetMember(pawn, "Transform") is UnityEngine.Transform tr)) return true;
                float hold = UniversalInject.TurnHoldSeconds(pawn, tr.position);
                if (hold <= 0f) { holds.Remove(__0); return true; }        // aligned (or not ours / easing off)
                var tag = holds.GetValue(__0, _ => new HoldTag { since = UnityEngine.Time.time });
                if (UnityEngine.Time.time - tag.since > 4f) return true;   // failsafe: fire anyway
                __result = false;                                          // still turning — stay in the delay step
                return false;
            }
            catch { return true; }
        }
    }

    // ---- FORENSICS (diag=1 only): log every RotationFSM turn start + which route StepTurning takes. These
    // probes produced the spike's verdicts (map turns are 0->0 no-ops; the unanimated method is inlined) and
    // stay for the next choreography investigation. Silent and near-free when diag is off. ----
    [HarmonyPatch] internal static class Hk_BattleTurnProbe
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.RotationPawnStateMachine;
            if (t != null)
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == "StartDirectionToLook" && m.GetParameters()[0].ParameterType == typeof(UnityEngine.Vector3))
                    { Plugin.Log.LogInfo("[BattleTurn] probe hooked StartDirectionToLook"); return m; }
            Plugin.Log.LogWarning("[BattleTurn] probe NOT hooked: StartDirectionToLook(Vector3, ...) not found");
            return null;
        }
        // (Vector3 direction, float angleEpsilon, bool addDelay, subPawns, policy, callback) — __4 = boxed policy enum
        static void Postfix(object __instance, UnityEngine.Vector3 __0, object __4)
        {
            try
            {
                if (!BattleTurn.diag) return;
                var pawn = UniversalInject.GetMember(__instance, "ownerPawn");
                Plugin.Log.LogInfo($"[BattleTurn] FSM turn start: unit='{BattleTurn.UnitOf(pawn)}' dir=({__0.x:F2},{__0.z:F2}) policy={__4}");
            }
            catch { }
        }
    }

    [HarmonyPatch] internal static class Hk_BattleTurnStep
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.RotationPawnStateMachine;
            var m = t != null ? AccessTools.Method(t, "StepTurning") : null;
            if (m != null) Plugin.Log.LogInfo("[BattleTurn] probe hooked StepTurning");
            else Plugin.Log.LogWarning("[BattleTurn] probe NOT hooked: StepTurning not found");
            return m;
        }
        static float nextLog;
        // StepTurning(RotationPawnStateMachine fsm, bool isFirstRun)
        static void Postfix(object __0, bool __1, bool __result)
        {
            try
            {
                if (!BattleTurn.diag) return;
                if (!__1 && UnityEngine.Time.realtimeSinceStartup < nextLog) return;   // always log first runs; throttle the rest
                nextLog = UnityEngine.Time.realtimeSinceStartup + 0.5f;
                var pawn = UniversalInject.GetMember(__0, "ownerPawn");
                bool anim = UniversalInject.GetMember(__0, "UseRotationAnimation") is bool a && a;
                string angles = "";
                if (__1 && UniversalInject.GetMember(__0, "rotationStart") is float[] rs &&
                    UniversalInject.GetMember(__0, "rotationEnd") is float[] re)
                {
                    for (int i = 0; i < rs.Length && i < re.Length; i++)
                        angles += $" [{i}] {rs[i]:F0}->{re[i]:F0}";
                }
                Plugin.Log.LogInfo($"[BattleTurn] StepTurning{(__1 ? " FIRST" : "")}: unit='{BattleTurn.UnitOf(pawn)}' animated={anim} done={__result}{angles}");
            }
            catch { }
        }
    }
}
