using System;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;

namespace HumankindAssetFramework
{
    // BATTLE TURN (spike/battle-turn 2026-08-05): make ranged attackers TURN believably before they fire.
    // Two vanilla facts (decompiled) make attacks look instant today:
    //   1. RotationPawnStateMachine.GetUnanimatedRotationProgress advances by `deltaTime / 0.5f` — every
    //      unanimated choreography turn (all vehicles: no Rotate animation capability) completes in HALF A
    //      SECOND flat, so a 180-degree pivot spins at 360 deg/s (the howitzer/facing "snap").
    //   2. AddPawnRangedFightSequence creates the pre-attack PawnActionLookAt as a NON-blocking action in the
    //      same group as PawnActionRangedStartAttack, so the attack starts the same frame the turn begins.
    // Fixes, both LIVE-tunable via BepInEx/config/enc_battleturn.txt (`rate=<deg/s>` `hold=<0|1>`, polled ~1/s,
    // missing file or rate=0/hold=0 = vanilla):
    //   A. cap the unanimated turn at `rate` deg/s (big turns take proportionally longer, small ones unchanged);
    //   B. hold the ranged attack until the shooter's rotation FSM finishes (the attack action already has a
    //      built-in isReadyToStart re-check loop — we just add "still turning" to its notion of not-ready).
    // Applies to ALL units (vanilla too) — that is the point: shakee's facing-snap complaint is a vanilla one.
    internal static class BattleTurn
    {
        internal static float rate = 0f;      // deg/s ceiling for unanimated battle turns; 0 = vanilla 0.5 s snap
        internal static bool holdFire = false; // delay PawnActionRangedStartAttack until the turn completes
        internal static bool diag = false;     // verbose probe logging (turn starts/routes) — spike forensics only
        internal const float HoldDeadline = 4f; // failsafe: never hold an attack longer than this (180 deg at 60 deg/s = 3 s)

        static string sig;
        static float nextPoll;
        internal static void Poll()
        {
            if (UnityEngine.Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = UnityEngine.Time.realtimeSinceStartup + 1f;
            try
            {
                var path = Path.Combine(Paths.ConfigPath, "enc_battleturn.txt");
                string txt = File.Exists(path) ? File.ReadAllText(path) : "";
                if (txt == sig) return;
                sig = txt;
                float r = 0f, h = 0f, dg = 0f;
                foreach (var raw in txt.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var eq = line.Split('=');
                    if (eq.Length != 2 || !float.TryParse(eq[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
                    switch (eq[0].Trim().ToLowerInvariant())
                    {
                        case "rate": r = v; break;
                        case "hold": h = v; break;
                        case "diag": dg = v; break;
                    }
                }
                rate = r; holdFire = h > 0f; diag = dg > 0f;
                Plugin.Log.LogInfo($"[BattleTurn] rate={rate} deg/s, hold={(holdFire ? 1 : 0)}, diag={(diag ? 1 : 0)}");
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

    // ---- Patch A: turn speed. Postfix keeps the vanilla progress formula (progress is recomputed each frame
    // from the transform's CURRENT angle, so it is self-correcting) but swaps the fixed `deltaTime / 0.5f`
    // increment for `deltaTime * rate / totalAngle` — a constant angular rate instead of a constant duration.
    // min() with the vanilla result means small turns (where vanilla is already slower than `rate`) keep their
    // vanilla feel and only the big snappy pivots slow down. ----
    [HarmonyPatch] internal static class Hk_BattleTurnRate
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.RotationPawnStateMachine;
            var m = t != null ? AccessTools.Method(t, "GetUnanimatedRotationProgress") : null;
            if (m != null) Plugin.Log.LogInfo("[BattleTurn] hooked RotationPawnStateMachine.GetUnanimatedRotationProgress (turn rate)");
            else Plugin.Log.LogWarning("[BattleTurn] NOT found: RotationPawnStateMachine.GetUnanimatedRotationProgress — battle turn rate off");
            return m;
        }
        static float nextLog;   // spike diagnostic throttle
        static void Postfix(ref float __result, float startingAngle, float wantedAngle, float deltaTime, UnityEngine.Transform transformToComputeFrom)
        {
            try
            {
                float rate = BattleTurn.rate;
                if (rate <= 0f) return;
                float total = Math.Abs(UnityEngine.Mathf.DeltaAngle(startingAngle, wantedAngle));
                if (total < 0.01f)
                {
                    // spike diagnostic: a ZERO-length turn — the transform was already AT the target when the
                    // turning step captured it, i.e. something snapped it there before the lerp could run.
                    if (BattleTurn.diag && UnityEngine.Time.realtimeSinceStartup > nextLog)
                    {
                        nextLog = UnityEngine.Time.realtimeSinceStartup + 0.5f;
                        Plugin.Log.LogInfo($"[BattleTurn] unanimated NO-OP (already at target): start={startingAngle:F0} wanted={wantedAngle:F0} dt={deltaTime:F3} ('{transformToComputeFrom.name}')");
                    }
                    return;   // vanilla already returned 1 (nothing to turn)
                }
                float cur = UnityEngine.Mathf.Clamp01(Math.Abs(UnityEngine.Mathf.DeltaAngle(startingAngle, transformToComputeFrom.eulerAngles.y)) / total);
                __result = Math.Min(__result, Math.Min(cur + deltaTime * rate / total, 1f));
                // spike diagnostic: prove this path actually runs during a turn (2/s max). If a turn visibly
                // snaps and this NEVER prints, the method was inlined by the JIT or the turn used another path.
                // dt is the smoking gun for INSTANT completes: GetAnimationDeltaTime >= 0.5 finishes in one call.
                if (BattleTurn.diag && UnityEngine.Time.realtimeSinceStartup > nextLog)
                {
                    nextLog = UnityEngine.Time.realtimeSinceStartup + 0.5f;
                    Plugin.Log.LogInfo($"[BattleTurn] unanimated turn: total={total:F0}deg progress={__result:P0} dt={deltaTime:F3} ('{transformToComputeFrom.name}')");
                }
            }
            catch { }
        }
    }

    // ---- SPIKE DIAGNOSTIC: log every RotationFSM turn start (the Vector3 StartDirectionToLook overload every
    // rotate/look-at funnels through) with the pawn and requested policy — tells us whether an attack's turn
    // enters this FSM at all, and as what. Remove once the spike verdict is in. ----
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
                if (!BattleTurn.diag) return;   // forensics only — silent in normal spike use
                var pawn = UniversalInject.GetMember(__instance, "ownerPawn");
                Plugin.Log.LogInfo($"[BattleTurn] FSM turn start: unit='{BattleTurn.UnitOf(pawn)}' dir=({__0.x:F2},{__0.z:F2}) policy={__4}");
            }
            catch { }
        }
    }

    // ---- SPIKE DIAGNOSTIC 2: which ROUTE does each turn take? StepTurning is the FSM's final step; its public
    // UseRotationAnimation field says animated (turn-anim cycle drives it — our rate cap does NOT apply) vs
    // unanimated (the 0.5 s lerp — our rate cap DOES apply). Big static method, safely patchable. ----
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
                if (!BattleTurn.diag) return;   // forensics only — silent in normal spike use
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

    // ---- Patch C: hold the ATTACK FSM for turn-easing units (2026-08-05, iteration 3). The MAP attack path
    // (UnitActionTriggerAttack, bombards) starts each pawn's AttackAnimationStateMachine directly with only a
    // small random stagger (delayDuration = GetAnimationRandomDelay) — the attack anim AND the shell (fired by
    // the anim's FireProjectile mecanim event) begin while the unit is still pivoting. Postfix on the 7-arg
    // Start extends the FSM's own delayDuration by the pawn's remaining turn-ease time — OUR entries only
    // (TurnHoldSeconds returns 0 for vanilla units and when easing is off), capped at 3 s. ----
    [HarmonyPatch] internal static class Hk_BattleAttackDelay
    {
        static FieldInfo fiDelay, fiOwner;
        static MethodBase TargetMethod()
        {
            var t = GameBinding.AttackAnimationStateMachine;
            MethodBase m = null;
            if (t != null)
                foreach (var mm in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (mm.Name == "Start" && mm.GetParameters().Length == 7) { m = mm; break; }
            fiDelay = t != null ? AccessTools.Field(t, "delayDuration") : null;
            fiOwner = t != null ? AccessTools.Field(t, "ownerPawn") : null;
            if (m != null && fiDelay != null && fiOwner != null) Plugin.Log.LogInfo("[BattleTurn] hooked AttackAnimationStateMachine.Start (attack waits for turn ease)");
            else Plugin.Log.LogWarning("[BattleTurn] NOT found: AttackAnimationStateMachine.Start/delayDuration/ownerPawn — attack won't wait for the turn");
            return (fiDelay != null && fiOwner != null) ? m : null;
        }
        static void Postfix(object __instance)
        {
            try
            {
                var pawn = fiOwner.GetValue(__instance);
                if (pawn == null) return;
                if (!(UniversalInject.GetMember(pawn, "Transform") is UnityEngine.Transform tr)) return;
                float extra = UniversalInject.TurnHoldSeconds(pawn, tr.position);
                if (extra <= 0f) return;
                fiDelay.SetValue(__instance, Convert.ToSingle(fiDelay.GetValue(__instance)) + extra);
                Plugin.Log.LogInfo($"[BattleTurn] attack FSM delayed {extra:F2}s (waiting for the turn)");
            }
            catch { }
        }
    }

    // ---- Patch E: MAP BOMBARD hold (iteration 5 — the real seam at last). A world-map bombard never touches
    // the battle attack actions OR AttackFSM.Start: PresentationArtilleryStrike.TriggerBombardAnimation does
    // FlipPawnsGrid(angle, Teleport) — THE instant facing snap — plus AttackFSM.TeleportToSimpleAttack(), and
    // the shell + impact are fired by PLAIN SCHEDULED DELAYS on PresentationArtilleryStrikeController. So:
    // prefix both Schedule calls and add the striker's remaining turn-ease time to the delay. Launch and hit
    // get the same hold (computed twice in the same frame, same value), so flight time is preserved. ----
    [HarmonyPatch] internal static class Hk_ArtilleryHold
    {
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
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

    // ---- Patch D: DYNAMIC attack gate (iteration 4). Patch C computes its delay ONCE at AttackFSM.Start —
    // but the facing snap can land AFTER the FSM starts (the log showed the delay computing 0: at that instant
    // the pawn still read as aligned), so the shell flew mid-pivot anyway. This gates the FSM's delay STEP
    // instead: StepWaitingDelay is the attack FSM's first step, ticked every frame and invoked through the
    // StaticSteps delegate array (detour-safe — no inlining, the lesson of the rotation-progress patch). While
    // the owner pawn is one of OUR turn-easing entries and still >8 deg off its target, keep returning
    // 'not done' — the attack anim + FireProjectile shell wait until the barrel is actually there, whenever the
    // snap lands. 4 s failsafe per FSM instance. Other FSM types (rotation, deploy, hit...) share this step;
    // the attack-type check keeps them untouched. ----
    [HarmonyPatch] internal static class Hk_BattleAttackGate
    {
        class HoldTag { public float since; }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, HoldTag> holds =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, HoldTag>();
        static MethodBase TargetMethod()
        {
            var t = GameBinding.AbstractAnimationStateMachine;
            var m = t != null ? AccessTools.Method(t, "StepWaitingDelay") : null;
            if (m != null) Plugin.Log.LogInfo("[BattleTurn] hooked AbstractAnimationStateMachine.StepWaitingDelay (dynamic attack gate)");
            else Plugin.Log.LogWarning("[BattleTurn] NOT found: AbstractAnimationStateMachine.StepWaitingDelay — attack won't wait for the turn");
            return m;
        }
        // StepWaitingDelay(AbstractAnimationStateMachine fsm, bool isFirstRun)
        static bool Prefix(object __0, ref bool __result)
        {
            try
            {
                if (__0 == null) return true;
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

    // ---- Patch B: hold fire while turning. PawnActionRangedStartAttack.OnReadyToStart is called from
    // StartPawnAction and re-tried from UpdatePawnAction every frame until isReadyToStart latches true — the
    // action's own wait loop. While the shooter's RotationFSM is running (the group's LookAt is mid-turn) we
    // skip the original AND un-latch isReadyToStart, so the loop naturally retries next frame; the frame the
    // turn completes, the attack (animation, projectile timing, everything downstream) starts unchanged.
    // A pawn already facing its target never defers: its LookAt ends on the same StartPawnAction call
    // (IsTurning false), so vanilla pacing is preserved for aligned attacks.
    // When holding, our own fireOnAttack clip arm moves HERE from the sequence-ctor hook (Hk_PawnRangedFight
    // skips its early arm) so a custom model's recoil clip stays in sync with the delayed real attack. ----
    [HarmonyPatch] internal static class Hk_BattleHoldFire
    {
        static FieldInfo fiReady;
        static MethodBase TargetMethod()
        {
            var t = GameBinding.PawnActionRangedStartAttack;
            var m = t != null ? AccessTools.Method(t, "OnReadyToStart") : null;
            fiReady = t != null ? AccessTools.Field(t, "isReadyToStart") : null;
            if (m != null && fiReady != null) Plugin.Log.LogInfo("[BattleTurn] hooked PawnActionRangedStartAttack.OnReadyToStart (hold fire while turning)");
            else Plugin.Log.LogWarning("[BattleTurn] NOT found: PawnActionRangedStartAttack.OnReadyToStart/isReadyToStart — hold-fire off");
            return fiReady != null ? m : null;   // no field, no patch — skipping without the un-latch would stall attacks
        }
        static float nextLog;   // spike diagnostic throttle
        static bool Prefix(object __instance)
        {
            try
            {
                if (!BattleTurn.holdFire) return true;
                var pawn = UniversalInject.GetMember(__instance, "pawn");
                if (pawn == null) return true;
                bool turning = UniversalInject.GetMember(pawn, "IsTurning") is bool b && b;
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
}
