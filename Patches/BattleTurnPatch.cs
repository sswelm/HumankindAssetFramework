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
                float r = 0f, h = 0f;
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
                    }
                }
                rate = r; holdFire = h > 0f;
                Plugin.Log.LogInfo($"[BattleTurn] rate={rate} deg/s, hold={(holdFire ? 1 : 0)}");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[BattleTurn] " + ex.Message); }
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
        static void Postfix(ref float __result, float startingAngle, float wantedAngle, float deltaTime, UnityEngine.Transform transformToComputeFrom)
        {
            try
            {
                float rate = BattleTurn.rate;
                if (rate <= 0f) return;
                float total = Math.Abs(UnityEngine.Mathf.DeltaAngle(startingAngle, wantedAngle));
                if (total < 0.01f) return;   // vanilla already returned 1 (nothing to turn)
                float cur = UnityEngine.Mathf.Clamp01(Math.Abs(UnityEngine.Mathf.DeltaAngle(startingAngle, transformToComputeFrom.eulerAngles.y)) / total);
                __result = Math.Min(__result, Math.Min(cur + deltaTime * rate / total, 1f));
            }
            catch { }
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
                        fiReady.SetValue(__instance, false);   // un-latch so UpdatePawnAction retries next frame
                        return false;                          // defer the attack — still turning
                    }
                }
                // turn done (or failsafe): the attack starts NOW — arm our fire-on-attack clip at the real moment
                UniversalInject.OnPawnAttack(pawn, "ranged attack start (post-turn)");
            }
            catch { }
            return true;
        }
    }
}
