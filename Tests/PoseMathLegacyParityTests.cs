using System;
using System.Collections.Generic;
using HumankindAssetFramework;
using UnityEngine;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // EXTRACTION PARITY for the pose decisions (2026-08-20), same discipline as DialLegacyParityTests: the
    // ORIGINAL inline bodies of StatePose / DeployPoseTime / FireOncePoseTime / RecoilOverlay are kept verbatim
    // here as oracles (copied from commit d3ee9ab) and compared against PoseMath over a generated corpus.
    //
    // This matters more than it did for the dials. The dials are read once per second from a file a human typed;
    // this code runs for every animated pawn every frame and decides what the player sees, so a subtle divergence
    // would show up as "the howitzer sometimes plays the wrong clip" — the hardest class of bug to pin down, and
    // exactly the sort this project has lost days to before.
    //
    // The corpus is generated rather than hand-listed: a deterministic LCG (no Random — the suite must be
    // repeatable) builds thousands of sample/fire layouts around a pawn, including the boundary distances where
    // the radii bite.
    public class PoseMathLegacyParityTests
    {
        // ------------------------------------------------------------------ deterministic corpus

        sealed class Lcg
        {
            uint s;
            public Lcg(uint seed) { s = seed; }
            public uint Next() { s = s * 1664525u + 1013904223u; return s; }
            public float Range(float lo, float hi) => lo + (Next() % 10000u) / 10000f * (hi - lo);
            public bool Bool() => (Next() & 1u) == 0u;
            public int Int(int loInc, int hiEx) => loInc + (int)(Next() % (uint)(hiEx - loInc));
        }

        // Distances straddle every radius boundary in the file (3u deploy, 4u state/fire) and lean towards the
        // radius, so most generated samples are actually in range — clustered same-type units within a few units
        // IS the real scenario, a formation.
        //
        // WHAT THIS CORPUS IS AND IS NOT FOR. It checks that the extraction TRANSCRIBED the original faithfully,
        // and it is good at that: it found the one real divergence (the exactly-4.0 boundary below) that reading
        // the two call sites had convinced me did not exist. It is a poor detector of a deliberate ALGORITHM
        // swap. The mutation drill showed why: replacing PickState's proximity weight with a headcount sails
        // past thousands of random layouts, because the two rules only disagree on small UNBALANCED in-range
        // splits (one very near sample against two distant ones) — and as the sample count grows the two
        // majorities converge, so a bigger corpus makes it *less* likely to fire, not more.
        // Algorithm choices are pinned by the adversarial hand-written cases in PoseMathTests instead
        // (PickState_IsWeightedByProximity_NotAHeadcount). Two tools, two jobs; neither substitutes.
        static float Dist(Lcg r)
        {
            int bucket = r.Int(0, 4);
            if (bucket == 0) return new[] { 0f, 2.99f, 3f, 3.01f, 3.99f, 4f, 4.01f }[r.Int(0, 7)];  // boundaries
            if (bucket == 1) return r.Range(0f, 6f);                                                 // the wide draw
            return r.Range(0f, 4.2f);                                                                // clustered, mostly in range
        }

        static Vector3 At(float d) => new Vector3(d, 0f, 0f);

        // ------------------------------------------------------------------ legacy oracles (verbatim)

        static void LegacyPickState(List<StateSample> samples, Vector3 pos,
                                    out bool matched, out bool moving, out float stoppedAt,
                                    out float moveStartedAt, out bool inCombat)
        {
            matched = false; moving = false; stoppedAt = -1f; moveStartedAt = -1f; inCombat = false;
            const float R2 = 4f * 4f;
            float wMove = 0f, wIdle = 0f, dMove = float.MaxValue, dIdle = float.MaxValue;
            StateSample sMove = default(StateSample), sIdle = default(StateSample);
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                float d = (s.pos - pos).sqrMagnitude;
                if (d >= R2) continue;
                float w = R2 - d;
                if (s.moving) { wMove += w; if (d < dMove) { dMove = d; sMove = s; } }
                else { wIdle += w; if (d < dIdle) { dIdle = d; sIdle = s; } }
            }
            if (wMove > 0f || wIdle > 0f)
            {
                matched = true;
                var pick = wMove > wIdle ? sMove : wIdle > wMove ? sIdle : (dMove <= dIdle ? sMove : sIdle);
                moving = pick.moving; stoppedAt = pick.stoppedAt; moveStartedAt = pick.moveStartedAt; inCombat = pick.combat;
            }
        }

        static void LegacyAttack(List<FireInstance> fires, Vector3 pos, float nowT, float attackDur, int attackRepeats,
                                 out bool inAttack, out float attackT)
        {
            inAttack = false; attackT = 0f;
            float atd = attackDur > 0.001f ? attackDur : 1f;
            int rep = attackRepeats > 0 ? attackRepeats : 1;
            float win = atd * rep;
            for (int i = 0; i < fires.Count; i++)
            {
                float dtF = nowT - fires[i].startTime;
                if (dtF < 0f || dtF >= win) continue;
                if ((fires[i].pos - pos).sqrMagnitude < 4f * 4f)
                { inAttack = true; attackT = Mathf.Min(dtF / atd, rep - 0.001f); break; }
            }
        }

        static void LegacyOneShot(float startedAt, float now, float dur, out bool active, out float t)
        {
            active = false; t = 0f;
            if (startedAt > 0f)
            {
                float ad = dur > 0.001f ? dur : 1f;
                float dt = now - startedAt;
                if (dt >= 0f && dt < ad) { active = true; t = Mathf.Min(dt / ad, 0.999f); }
            }
        }

        // The recoil overlay's spelling: seed `best` with the radius.
        static float LegacyNearestFireStart_RadiusSeeded(List<FireInstance> fires, Vector3 pos)
        {
            float bestSqF = 4f * 4f, bestStartF = -1f;
            for (int i = 0; i < fires.Count; i++)
            {
                float d = (fires[i].pos - pos).sqrMagnitude;
                if (d < bestSqF) { bestSqF = d; bestStartF = fires[i].startTime; }
            }
            return bestStartF;
        }

        // Fire-once's spelling: seed with MaxValue, range-check afterwards.
        static float LegacyNearestFireStart_MaxValueSeeded(List<FireInstance> fires, Vector3 pos)
        {
            float bestSq = float.MaxValue, bestStart = -1f;
            for (int i = 0; i < fires.Count; i++)
            {
                float d = (fires[i].pos - pos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; bestStart = fires[i].startTime; }
            }
            const float matchRadiusSq = 4f * 4f;
            return (bestStart >= 0f && bestSq <= matchRadiusSq) ? bestStart : -1f;
        }

        static float LegacyDeployPose(List<DeploySample> samples, Vector3 dpos, float deployPoseTime)
        {
            float poseTime = deployPoseTime;
            float bestSqD = 3f * 3f;
            for (int i = 0; i < samples.Count; i++)
            {
                float d = (samples[i].pos - dpos).sqrMagnitude;
                if (d < bestSqD) { bestSqD = d; poseTime = samples[i].poseTime; }
            }
            return poseTime;
        }

        static float LegacyRecoil(float elapsedF, float dur, float deployPoseTime, float recoilSpeed, float poseTime)
        {
            const float recoilMax = 0.999f;
            float rspd = recoilSpeed > 0f ? recoilSpeed : 1f;
            float recoilDur = dur * (recoilMax - deployPoseTime) / rspd;
            if (recoilDur > 0.0001f && elapsedF < recoilDur)
                poseTime = deployPoseTime + (elapsedF / recoilDur) * (recoilMax - deployPoseTime);
            return poseTime;
        }

        // ------------------------------------------------------------------ parity over the corpus

        [Fact]
        public void PickState_MatchesTheLegacyBody_OverThousandsOfLayouts()
        {
            var r = new Lcg(20260820);
            for (int iter = 0; iter < 4000; iter++)
            {
                var samples = new List<StateSample>();
                int n = r.Int(0, 9);   // formation-ish sizes (Formation_Close_9 fields 19 dummies)
                for (int i = 0; i < n; i++)
                    samples.Add(new StateSample
                    {
                        pos = At(Dist(r)),
                        moving = r.Bool(),
                        stoppedAt = r.Range(-1f, 50f),
                        moveStartedAt = r.Range(-1f, 50f),
                        combat = r.Bool(),
                    });

                LegacyPickState(samples, Vector3.zero, out var lMatched, out var lMoving,
                                out var lStopped, out var lMoveStarted, out var lCombat);
                var got = PoseMath.PickState(samples, Vector3.zero);

                Assert.Equal(lMatched, got.Matched);
                if (!lMatched) continue;                       // the caller returns early; other fields unused
                Assert.Equal(lMoving, got.Moving);
                Assert.Equal(lCombat, got.Combat);
                Assert.Equal(lStopped, got.StoppedAt);
                Assert.Equal(lMoveStarted, got.MoveStartedAt);
            }
        }

        [Fact]
        public void AttackWindow_MatchesTheLegacyBody_OverThousandsOfLayouts()
        {
            var r = new Lcg(7771);
            for (int iter = 0; iter < 4000; iter++)
            {
                var fires = new List<FireInstance>();
                int n = r.Int(0, 5);
                for (int i = 0; i < n; i++)
                    fires.Add(new FireInstance { pos = At(Dist(r)), startTime = r.Range(0f, 20f) });
                float now = r.Range(0f, 25f);
                float dur = r.Int(0, 5) == 0 ? 0f : r.Range(0.05f, 4f);   // include the 0-duration guard
                int rep = r.Int(-2, 5);

                LegacyAttack(fires, Vector3.zero, now, dur, rep, out var lIn, out var lT);
                var gotIn = PoseMath.AttackWindow(fires, Vector3.zero, now, dur, rep, out var gotT);
                Assert.Equal(lIn, gotIn);
                if (lIn) Assert.Equal(lT, gotT);
            }
        }

        [Fact]
        public void OneShot_MatchesTheLegacyBody_IncludingTheStartedAtGate()
        {
            var r = new Lcg(31337);
            for (int iter = 0; iter < 4000; iter++)
            {
                float startedAt = r.Int(0, 4) == 0 ? new[] { -1f, 0f }[r.Int(0, 2)] : r.Range(0.01f, 30f);
                float dur = r.Int(0, 5) == 0 ? 0f : r.Range(0.05f, 5f);
                // Bias half the corpus to the END of the window. A uniform `now` lands in the clamp region
                // (dt/dur > 0.999) about once in a thousand, so the first version of this corpus never exercised
                // the clamp at all — the mutation drill caught that: raising OneShotMax to 1.0 failed only the
                // one hand-written test and sailed past 4000 generated cases.
                float now = r.Bool()
                    ? r.Range(0f, 35f)
                    : startedAt + (dur > 0.001f ? dur : 1f) * r.Range(0.995f, 1.005f);

                LegacyOneShot(startedAt, now, dur, out var lActive, out var lT);
                var gotActive = PoseMath.OneShot(startedAt, now, dur, out var gotT);
                Assert.Equal(lActive, gotActive);
                if (lActive) Assert.Equal(lT, gotT);
            }
        }

        // THE ONE BEHAVIOUR CHANGE, and the oracle is what found it — reading the two call sites had convinced me
        // they were equivalent. They differ at a distance of EXACTLY 4.0: the recoil overlay's radius-seeded loop
        // is strictly-inside, fire-once's MaxValue-then-range-check is inclusive. Everywhere else they agree.
        // Unified to strictly-inside (see the comment on PoseMath.NearestFireStart).
        [Fact]
        public void NearestFireStart_TheTwoLegacySpellings_AgreeExceptExactlyOnTheRadius()
        {
            var r = new Lcg(90210);
            int diverged = 0;
            for (int iter = 0; iter < 4000; iter++)
            {
                var fires = new List<FireInstance>();
                int n = r.Int(0, 6);
                for (int i = 0; i < n; i++)
                    fires.Add(new FireInstance { pos = At(Dist(r)), startTime = r.Range(0f, 20f) });

                var strict = LegacyNearestFireStart_RadiusSeeded(fires, Vector3.zero);
                var inclusive = LegacyNearestFireStart_MaxValueSeeded(fires, Vector3.zero);
                var got = PoseMath.NearestFireStart(fires, Vector3.zero, PoseMath.FireMatchRadiusSq);

                // the shared implementation always follows the strictly-inside spelling
                Assert.Equal(strict, got);
                if (strict == inclusive) continue;

                diverged++;
                // the ONLY way they may differ: a fire sitting at exactly the radius, which the inclusive
                // spelling counts and the strict one does not.
                Assert.Equal(-1f, strict);
                Assert.NotEqual(-1f, inclusive);
                bool exactlyOnTheRadius = false;
                foreach (var f in fires)
                    if (f.pos.sqrMagnitude == PoseMath.FireMatchRadiusSq) exactlyOnTheRadius = true;
                Assert.True(exactlyOnTheRadius, "the spellings diverged for a reason other than a fire exactly on the radius");
            }
            Assert.True(diverged > 0, "the corpus never hit the boundary — it is not exercising the divergence");
        }

        // Stated as a plain, readable case as well as inside the corpus loop, since it is the change a future
        // reader is most likely to trip over.
        [Fact]
        public void NearestFireStart_AFireAtExactlyFourUnits_IsNoLongerCountedAsTheFirer()
        {
            var fires = new List<FireInstance> { new FireInstance { pos = new Vector3(4f, 0f, 0f), startTime = 12f } };
            Assert.Equal(12f, LegacyNearestFireStart_MaxValueSeeded(fires, Vector3.zero));   // fire-once used to count it
            Assert.Equal(-1f, LegacyNearestFireStart_RadiusSeeded(fires, Vector3.zero));     // recoil never did
            Assert.Equal(-1f, PoseMath.NearestFireStart(fires, Vector3.zero, PoseMath.FireMatchRadiusSq));

            // ...and 3.999 is still comfortably the firer, so the change really is only the boundary itself.
            var justInside = new List<FireInstance> { new FireInstance { pos = new Vector3(3.999f, 0f, 0f), startTime = 12f } };
            Assert.Equal(12f, PoseMath.NearestFireStart(justInside, Vector3.zero, PoseMath.FireMatchRadiusSq));
        }

        [Fact]
        public void NearestDeployPose_MatchesTheLegacyBody_IncludingTheTighterRadius()
        {
            var r = new Lcg(4242);
            for (int iter = 0; iter < 4000; iter++)
            {
                var samples = new List<DeploySample>();
                int n = r.Int(0, 6);
                for (int i = 0; i < n; i++)
                    samples.Add(new DeploySample { pos = At(Dist(r)), poseTime = r.Range(0f, 1f) });
                float fallback = r.Range(0f, 1f);
                Assert.Equal(LegacyDeployPose(samples, Vector3.zero, fallback),
                             PoseMath.NearestDeployPose(samples, Vector3.zero, fallback));
            }
        }

        [Fact]
        public void RecoilSweep_MatchesTheLegacyBody()
        {
            var r = new Lcg(5150);
            for (int iter = 0; iter < 4000; iter++)
            {
                float elapsed = r.Range(-1f, 6f);
                float dur = r.Range(0f, 5f);
                float deployPoseTime = r.Int(0, 5) == 0 ? new[] { 0f, 0.999f, 1f }[r.Int(0, 3)] : r.Range(0f, 1f);
                float speed = r.Int(0, 4) == 0 ? new[] { -1f, 0f }[r.Int(0, 2)] : r.Range(0.1f, 8f);

                float legacy = LegacyRecoil(elapsed, dur, deployPoseTime, speed, deployPoseTime);
                PoseMath.RecoilSweep(elapsed, dur, deployPoseTime, speed, out var got, out _);
                Assert.Equal(legacy, got);
            }
        }

        // FireOncePose gained a SafeDur() guard the original did not have. It is unreachable in production —
        // the caller computes `dur = animDuration > 0.001f ? animDuration : 1f` before ever calling — so the two
        // agree everywhere the code can actually go. Pinned rather than assumed, with the guarantee stated.
        [Fact]
        public void FireOncePose_MatchesTheLegacyBody_ForEveryDurationTheCallerCanProduce()
        {
            var r = new Lcg(6060);
            for (int iter = 0; iter < 4000; iter++)
            {
                float elapsed = r.Range(-1f, 8f);
                float dur = r.Range(0.0011f, 6f);            // the caller's guarantee: always > 0.001
                float legacy = elapsed >= dur ? 0f : elapsed / dur;
                Assert.Equal(legacy, PoseMath.FireOncePose(elapsed, dur));
            }
        }

        [Fact]
        public void FireOncePose_ZeroDuration_IsTheOneDivergence_AndIsUnreachable()
        {
            // legacy: elapsed >= 0 -> 0f. new: SafeDur treats 0 as 1s. Unreachable in production (see above).
            Assert.Equal(0f, 0.5f >= 0f ? 0f : 0.5f / 0f);
            Assert.Equal(0.5f, PoseMath.FireOncePose(0.5f, 0f));
        }
    }
}
