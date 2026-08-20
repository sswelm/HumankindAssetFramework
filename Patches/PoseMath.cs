using System.Collections.Generic;
using UnityEngine;

namespace HumankindAssetFramework
{
    // ---------------------------------------------------------------------------------------------------------
    // POSE DECISIONS — the pure half of the per-frame pose state machine (2026-08-20).
    //
    // `StatePose`, `DeployPoseTime`, `FireOncePoseTime` and `RecoilOverlay` decide, every frame for every pawn,
    // WHICH clip plays and WHERE in it — the thing the player actually sees. They did that inline, tangled with
    // `GetMember` reflection, `UnityEngine.Time.time` and the locks around the shared sample lists, so none of
    // the decision logic could be tested. The second application of the rule in docs/Decisions.md (2026-08-20):
    // move the DECISION out of the method that does the I/O; leave the I/O where it is.
    //
    // Everything here takes plain data — a list, a position, a clock reading — and returns plain data. No
    // reflection, no `Time.time`, no entry mutation, no locking (the CALLER still takes the lock and passes the
    // list in, exactly as before). Unit-tested in Tests/PoseMathTests.cs against a legacy oracle.
    //
    // NOT here: the idle-alt cadence. It draws from `UnityEngine.Random` and mutates scheduling state on the
    // ModelEntry, so it is a scheduler rather than a decision; extracting it needs an injected clock + RNG and is
    // a separate job. Its *window* arithmetic is `OneShot`, which is here.
    //
    // THE RADII ARE NOT ALL THE SAME and that is deliberate — read the constants before touching a call site.
    // ---------------------------------------------------------------------------------------------------------
    internal static class PoseMath
    {
        internal const float StateMatchRadius = 4f;    // proximity-weighted state vote (same class as the fire hooks)
        internal const float FireMatchRadius = 4f;     // "this pawn is the one that fired" (tiles are spaced wider)
        internal const float DeployMatchRadius = 3f;   // deploy ramp match — TIGHTER than the fire radius
        internal const float StateMatchRadiusSq = StateMatchRadius * StateMatchRadius;
        internal const float FireMatchRadiusSq = FireMatchRadius * FireMatchRadius;
        internal const float DeployMatchRadiusSq = DeployMatchRadius * DeployMatchRadius;

        // Stay strictly below 1.0: the sampler runs Mathf.Repeat(t, 1) and 1.0 wraps to frame 0 — the folded pose.
        internal const float OneShotMax = 0.999f;
        internal const float RecoilMax = 0.999f;

        // A clip duration of 0 (unbaked / unresolved) would divide by zero; the shipped guard is "treat as 1s".
        internal static float SafeDur(float dur) => dur > 0.001f ? dur : 1f;

        internal struct StatePick
        {
            public bool Matched;        // false = no sample within the radius; the caller holds the previous pose
            public bool Moving, Combat;
            public float StoppedAt, MoveStartedAt;
        }

        // PROXIMITY-WEIGHTED MAJORITY over the samples inside the radius — NOT the single nearest.
        // Samples are pooled per model TYPE (there is no per-unit id: the pawn entry's array slot reshuffles on
        // LOD), so two same-type units in range (one moving, one idle) could have a pawn match the NEIGHBOUR's
        // nearest sample and play the wrong clip. Weighting by proximity (w = R^2 - d^2) lets a pawn deep in its
        // own formation be carried by its mates instead of flipped by one closer neighbour.
        // Identical to a nearest-sample pick whenever the in-radius samples AGREE (the common case).
        // The winner's NEAREST sample is the representative — it carries that unit's stoppedAt/moveStartedAt/combat.
        // An exact weight tie falls back to the nearest sample overall, moving winning a dead heat.
        internal static StatePick PickState(IList<StateSample> samples, Vector3 pos)
        {
            var r = new StatePick();
            if (samples == null) return r;
            float wMove = 0f, wIdle = 0f, dMove = float.MaxValue, dIdle = float.MaxValue;
            StateSample sMove = default(StateSample), sIdle = default(StateSample);
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                float d = (s.pos - pos).sqrMagnitude;
                if (d >= StateMatchRadiusSq) continue;
                float w = StateMatchRadiusSq - d;   // 0 at the radius edge, heaviest at the pawn's own position
                if (s.moving) { wMove += w; if (d < dMove) { dMove = d; sMove = s; } }
                else { wIdle += w; if (d < dIdle) { dIdle = d; sIdle = s; } }
            }
            if (wMove <= 0f && wIdle <= 0f) return r;
            var pick = wMove > wIdle ? sMove : wIdle > wMove ? sIdle : (dMove <= dIdle ? sMove : sIdle);
            r.Matched = true;
            r.Moving = pick.moving;
            r.StoppedAt = pick.stoppedAt;
            r.MoveStartedAt = pick.moveStartedAt;
            r.Combat = pick.combat;
            return r;
        }

        // A one-shot clip window: after-move settle, pre-move fold. Returns the normalized time, held just below
        // 1.0 so the last frame sticks until the window elapses rather than wrapping to frame 0.
        // `startedAt <= 0` means "never happened" — the shipped call sites gate on `> 0f` before entering.
        internal static bool OneShot(float startedAt, float now, float dur, out float t)
        {
            t = 0f;
            if (startedAt <= 0f) return false;
            float d = SafeDur(dur);
            float dt = now - startedAt;
            if (dt < 0f || dt >= d) return false;
            t = Mathf.Min(dt / d, OneShotMax);
            return true;
        }

        // THE ATTACK WINDOW takes the FIRST in-range fire still inside its window, not the nearest — a deliberate
        // difference from the fire-once/recoil matchers below, and cheap because a pawn is rarely near two fires.
        // `repeats` spans N passes of the clip: Time is fed UNCLAMPED and the sampler's Repeat(t,1) replays the
        // clip each pass, giving sustained fire from a single-pop source clip. repeats <= 1 degenerates to the
        // original clamped one-shot.
        internal static bool AttackWindow(IList<FireInstance> fires, Vector3 pos, float now,
                                          float clipDur, int repeats, out float t)
        {
            t = 0f;
            if (fires == null) return false;
            float d = SafeDur(clipDur);
            int rep = repeats > 0 ? repeats : 1;
            float win = d * rep;
            for (int i = 0; i < fires.Count; i++)
            {
                float dt = now - fires[i].startTime;
                if (dt < 0f || dt >= win) continue;
                if ((fires[i].pos - pos).sqrMagnitude >= FireMatchRadiusSq) continue;
                t = Mathf.Min(dt / d, rep - 0.001f);
                return true;
            }
            return false;
        }

        // The start time of the NEAREST active fire within the radius, or -1 when this pawn did not fire.
        // Shared by fire-once and the recoil overlay.
        //
        // THE ONE DELIBERATE BEHAVIOUR CHANGE OF THIS EXTRACTION (2026-08-20), found by the parity oracle rather
        // than by reading. The two call sites were spelled differently — the recoil overlay seeded `best` with
        // the radius (so `d < r²`, strictly inside), fire-once seeded it with float.MaxValue and range-checked
        // afterwards (`d <= r²`, inclusive). They agree everywhere except at a distance of EXACTLY 4.0, where
        // fire-once counted the pawn as the firer and recoil did not.
        //
        // Unified to **strictly inside**, because that is what the other two matchers already do (`PickState`
        // skips `d >= R²`; `NearestDeployPose` takes `d < r²`), and because the inclusive form was an artifact of
        // the MaxValue-then-check spelling rather than a decision anyone made. The affected case is a pawn
        // sitting at exactly 4.000000 units from a fire. Pinned in PoseMathLegacyParityTests.
        internal static float NearestFireStart(IList<FireInstance> fires, Vector3 pos, float radiusSq)
        {
            float bestSq = radiusSq, bestStart = -1f;
            if (fires == null) return bestStart;
            for (int i = 0; i < fires.Count; i++)
            {
                float d = (fires[i].pos - pos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; bestStart = fires[i].startTime; }
            }
            return bestStart;
        }

        // FIRE-ONCE: one 0->1 pass from the firing pawn's own fire, then rest at frame 0. Only the firer animates.
        // A negative elapsed (a fire stamped slightly in the future) yields a negative pose time, as shipped —
        // Update prunes finished fires and the sampler's Repeat() tolerates it.
        internal static float FireOncePose(float elapsed, float dur)
        {
            float d = SafeDur(dur);
            return elapsed >= d ? 0f : elapsed / d;
        }

        // DEPLOY: hold the ramped pose time recorded for the nearest pawn of this unit, else the authored default.
        // NOTE the 3u radius — tighter than the 4u fire match.
        internal static float NearestDeployPose(IList<DeploySample> samples, Vector3 pos, float fallback)
        {
            float bestSq = DeployMatchRadiusSq, poseTime = fallback;
            if (samples == null) return poseTime;
            for (int i = 0; i < samples.Count; i++)
            {
                float d = (samples[i].pos - pos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; poseTime = samples[i].poseTime; }
            }
            return poseTime;
        }

        // RECOIL TAIL: sweep the pose time once from the deployed hold up through [deployPoseTime, RecoilMax],
        // then fall back to the hold. `recoilSpeed` shortens the tail; the tail's own duration is the authored
        // clip length scaled by how much of the clip the tail occupies.
        internal static float RecoilTailDuration(float clipDur, float deployPoseTime, float recoilSpeed)
        {
            float spd = recoilSpeed > 0f ? recoilSpeed : 1f;
            return clipDur * (RecoilMax - deployPoseTime) / spd;
        }

        // Returns true (and the swept pose time) while inside the tail; false = hold the deployed pose.
        internal static bool RecoilSweep(float elapsed, float clipDur, float deployPoseTime, float recoilSpeed,
                                         out float poseTime, out float tailDur)
        {
            poseTime = deployPoseTime;
            tailDur = RecoilTailDuration(clipDur, deployPoseTime, recoilSpeed);
            if (tailDur <= 0.0001f || elapsed >= tailDur) return false;
            poseTime = deployPoseTime + (elapsed / tailDur) * (RecoilMax - deployPoseTime);
            return true;
        }
    }
}
