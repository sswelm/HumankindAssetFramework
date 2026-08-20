using System.Collections.Generic;
using HumankindAssetFramework;
using UnityEngine;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The per-frame pose decisions: which clip a pawn plays and where in it. This is what the player actually
    // sees, and until 2026-08-20 none of it had a test — it lived inline in StatePose / DeployPoseTime /
    // FireOncePoseTime / RecoilOverlay, tangled with reflection, Time.time and the locks around the shared
    // sample lists. Extracted per the rule in docs/Decisions.md; the parity oracle lives in
    // PoseMathLegacyParityTests.cs.
    //
    // The invariants worth guarding are the ones a reasonable "cleanup" would break: the three DIFFERENT match
    // radii, the proximity-weighted majority (rather than nearest), first-match vs nearest-match, and the
    // never-quite-1.0 clamp that keeps a held frame from wrapping to the folded pose.
    public class PoseMathTests
    {
        static StateSample S(float x, bool moving, float stoppedAt = 0f, float moveStartedAt = 0f, bool combat = false) =>
            new StateSample { pos = new Vector3(x, 0f, 0f), moving = moving, stoppedAt = stoppedAt, moveStartedAt = moveStartedAt, combat = combat };
        static FireInstance F(float x, float startTime) =>
            new FireInstance { pos = new Vector3(x, 0f, 0f), startTime = startTime };
        static DeploySample D(float x, float poseTime) =>
            new DeploySample { pos = new Vector3(x, 0f, 0f), poseTime = poseTime };
        static readonly Vector3 Origin = Vector3.zero;

        // ------------------------------------------------------------------ PickState

        [Fact]
        public void PickState_NoSamples_IsUnmatched_SoTheCallerHoldsThePreviousPose()
        {
            Assert.False(PoseMath.PickState(new List<StateSample>(), Origin).Matched);
            Assert.False(PoseMath.PickState(null, Origin).Matched);
        }

        [Fact]
        public void PickState_SingleInRangeSample_CarriesAllOfItsFields()
        {
            var r = PoseMath.PickState(new List<StateSample> { S(1f, true, stoppedAt: 5f, moveStartedAt: 7f, combat: true) }, Origin);
            Assert.True(r.Matched);
            Assert.True(r.Moving);
            Assert.True(r.Combat);
            Assert.Equal(5f, r.StoppedAt);
            Assert.Equal(7f, r.MoveStartedAt);
        }

        [Theory]
        [InlineData(3.99f, true)]    // just inside the 4u radius
        [InlineData(4.0f, false)]    // exactly at it — excluded (d >= R2)
        [InlineData(4.01f, false)]
        public void PickState_RadiusIsFourUnits_Exclusive(float dist, bool expectMatch)
        {
            Assert.Equal(expectMatch, PoseMath.PickState(new List<StateSample> { S(dist, true) }, Origin).Matched);
        }

        // THE REASON THE WEIGHTING EXISTS. Samples are pooled per model TYPE, so a pawn can sit nearer to a
        // NEIGHBOUR unit's sample than to its own. One close moving neighbour must not flip a pawn that is
        // surrounded by its own idle formation — the naive "nearest sample wins" did exactly that.
        [Fact]
        public void PickState_OneCloseNeighbour_DoesNotOutvoteTheFormationAroundIt()
        {
            var samples = new List<StateSample>
            {
                S(0.5f, moving: true),                       // the interloper — nearest by far
                S(1.5f, false), S(1.6f, false), S(1.7f, false),   // this pawn's own idle mates
            };
            var r = PoseMath.PickState(samples, Origin);
            Assert.True(r.Matched);
            Assert.False(r.Moving);                          // carried by its mates, not flipped by the neighbour
        }

        // The vote is weighted by PROXIMITY, not a headcount. This is the case that tells the two apart: a pawn
        // sitting practically on top of one moving sample must not be outvoted by two idle samples loitering at
        // the edge of the radius. Caught by the mutation drill — replacing the weight with a constant 1 (i.e. a
        // plain majority) left every other test in this file green.
        [Fact]
        public void PickState_IsWeightedByProximity_NotAHeadcount()
        {
            var samples = new List<StateSample>
            {
                S(0.1f, moving: true),                       // essentially the pawn's own position
                S(3.9f, false), S(3.9f, false),              // two idle samples out at the radius edge
            };
            var r = PoseMath.PickState(samples, Origin);
            Assert.True(r.Matched);
            Assert.True(r.Moving);   // a headcount would say idle 2 : moving 1 and get this wrong
        }

        [Fact]
        public void PickState_WhenAllInRangeSamplesAgree_ItIsJustTheNearestPick()
        {
            var samples = new List<StateSample> { S(2f, true, stoppedAt: 1f), S(0.5f, true, stoppedAt: 9f), S(3f, true, stoppedAt: 2f) };
            var r = PoseMath.PickState(samples, Origin);
            Assert.True(r.Moving);
            Assert.Equal(9f, r.StoppedAt);                   // the nearest sample is the representative
        }

        // The representative must come from the WINNING side — taking the nearest sample overall would hand the
        // pawn a stoppedAt/moveStartedAt belonging to a unit in the opposite state.
        [Fact]
        public void PickState_RepresentativeComesFromTheWinningSide_NotTheNearestOverall()
        {
            var samples = new List<StateSample>
            {
                S(0.5f, moving: true, stoppedAt: 111f),          // nearest overall, but outvoted
                S(1.5f, false, stoppedAt: 222f), S(1.6f, false, stoppedAt: 333f), S(1.7f, false, stoppedAt: 444f),
            };
            var r = PoseMath.PickState(samples, Origin);
            Assert.False(r.Moving);
            Assert.Equal(222f, r.StoppedAt);                 // the idle side's NEAREST, not the moving interloper's
        }

        [Fact]
        public void PickState_ExactWeightTie_GoesToTheNearer_AndMovingWinsADeadHeat()
        {
            // symmetric distances => equal weights; the moving sample is nearer
            var nearerMoving = new List<StateSample> { S(1f, true), S(2f, false) };
            Assert.True(PoseMath.PickState(nearerMoving, Origin).Moving);

            // identical distances on both sides => dead heat => moving wins (dMove <= dIdle)
            var deadHeat = new List<StateSample> { S(2f, false), S(2f, true) };
            Assert.True(PoseMath.PickState(deadHeat, Origin).Moving);
        }

        // ------------------------------------------------------------------ OneShot (after-move / pre-move)

        [Theory]
        [InlineData(0f)]      // never happened
        [InlineData(-1f)]
        public void OneShot_NeverStarted_IsNotPlaying(float startedAt)
        {
            Assert.False(PoseMath.OneShot(startedAt, now: 10f, dur: 2f, t: out _));
        }

        [Fact]
        public void OneShot_BeforeItStarts_AndAfterItEnds_IsNotPlaying()
        {
            Assert.False(PoseMath.OneShot(startedAt: 10f, now: 9f, dur: 2f, t: out _));    // clock behind the start
            Assert.False(PoseMath.OneShot(startedAt: 10f, now: 12f, dur: 2f, t: out _));   // exactly elapsed
            Assert.False(PoseMath.OneShot(startedAt: 10f, now: 99f, dur: 2f, t: out _));
        }

        [Fact]
        public void OneShot_MidWindow_IsTheNormalizedPosition()
        {
            Assert.True(PoseMath.OneShot(startedAt: 10f, now: 10.5f, dur: 2f, t: out var t));
            Assert.Equal(0.25f, t, 4);
        }

        // The clamp is not cosmetic: the sampler runs Mathf.Repeat(t, 1), so 1.0 wraps to frame 0 — the FOLDED
        // pose. A settle clip that reached exactly 1.0 would snap the model inside-out on its last frame.
        [Fact]
        public void OneShot_NeverReachesOne_BecauseRepeatWouldWrapItToTheFoldedFrame()
        {
            Assert.True(PoseMath.OneShot(startedAt: 0f + 0.0001f, now: 1.9999f, dur: 2f, t: out var t));
            Assert.True(t < 1f);
            Assert.Equal(PoseMath.OneShotMax, t, 4);
        }

        [Fact]
        public void OneShot_ZeroDuration_IsTreatedAsOneSecond_NotADivideByZero()
        {
            Assert.True(PoseMath.OneShot(startedAt: 10f, now: 10.5f, dur: 0f, t: out var t));
            Assert.Equal(0.5f, t, 4);
        }

        // ------------------------------------------------------------------ AttackWindow

        [Fact]
        public void AttackWindow_NoFires_IsNotAttacking()
        {
            Assert.False(PoseMath.AttackWindow(new List<FireInstance>(), Origin, 10f, 1f, 1, out _));
            Assert.False(PoseMath.AttackWindow(null, Origin, 10f, 1f, 1, out _));
        }

        [Fact]
        public void AttackWindow_InRangeAndInsideTheWindow_Plays()
        {
            Assert.True(PoseMath.AttackWindow(new List<FireInstance> { F(1f, 10f) }, Origin, 10.5f, 1f, 1, out var t));
            Assert.Equal(0.5f, t, 4);
        }

        [Theory]
        [InlineData(5f, 10.5f)]    // out of range
        [InlineData(1f, 12f)]      // expired
        [InlineData(1f, 9f)]       // clock behind the fire
        public void AttackWindow_OutOfRangeOrOutOfWindow_DoesNotPlay(float dist, float now)
        {
            Assert.False(PoseMath.AttackWindow(new List<FireInstance> { F(dist, 10f) }, Origin, now, 1f, 1, out _));
        }

        // A deliberate difference from the fire-once / recoil matchers, which take the NEAREST fire. Here the
        // first in-range fire still inside its window wins. Cheap, and a pawn is rarely near two fires.
        [Fact]
        public void AttackWindow_TakesTheFirstMatch_NotTheNearest()
        {
            var fires = new List<FireInstance> { F(3f, 10f), F(0.1f, 10.75f) };   // far-but-first, then near
            Assert.True(PoseMath.AttackWindow(fires, Origin, 10.5f, 1f, 1, out var t));
            Assert.Equal(0.5f, t, 4);                                             // the FIRST fire's timing
        }

        // ...and "first" means list order, so two EQUALLY valid fires must resolve to the earlier entry. The
        // previous test alone did not pin this: its second fire was in the future, so iterating backwards
        // produced the same answer and a reversed-scan mutation slipped through the drill.
        [Fact]
        public void AttackWindow_TwoValidFires_ResolveToTheEarlierListEntry()
        {
            var fires = new List<FireInstance> { F(1f, 10f), F(2f, 10.25f) };     // both in range, both in window
            Assert.True(PoseMath.AttackWindow(fires, Origin, 10.5f, 1f, 1, out var t));
            Assert.Equal(0.5f, t, 4);                                             // fires[0], not fires[1] (0.25)
        }

        // repeats spans N passes and feeds Time UNCLAMPED so the sampler's Repeat(t,1) replays the clip each
        // pass — sustained fire from a single-pop source clip. t is allowed above 1.
        [Fact]
        public void AttackWindow_Repeats_SpanSeveralPasses_AndFeedTimeUnclamped()
        {
            var fires = new List<FireInstance> { F(1f, 10f) };
            Assert.True(PoseMath.AttackWindow(fires, Origin, 12.5f, 1f, 3, out var t));
            Assert.Equal(2.5f, t, 4);                                             // third pass, halfway
            Assert.False(PoseMath.AttackWindow(fires, Origin, 13.5f, 1f, 3, out _));  // window is 3 x 1s
        }

        [Fact]
        public void AttackWindow_Repeats_ClampJustBelowTheLastPass()
        {
            Assert.True(PoseMath.AttackWindow(new List<FireInstance> { F(1f, 10f) }, Origin, 12.9999f, 1f, 3, out var t));
            Assert.True(t < 3f);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(-5)]
        public void AttackWindow_RepeatsBelowOne_DegeneratesToASingleClampedPass(int repeats)
        {
            var fires = new List<FireInstance> { F(1f, 10f) };
            Assert.True(PoseMath.AttackWindow(fires, Origin, 10.5f, 1f, repeats, out var t));
            Assert.Equal(0.5f, t, 4);
            Assert.False(PoseMath.AttackWindow(fires, Origin, 11.5f, 1f, repeats, out _));
        }

        // ------------------------------------------------------------------ NearestFireStart / FireOncePose

        [Fact]
        public void NearestFireStart_NothingInRange_IsMinusOne()
        {
            Assert.Equal(-1f, PoseMath.NearestFireStart(new List<FireInstance> { F(9f, 10f) }, Origin, PoseMath.FireMatchRadiusSq));
            Assert.Equal(-1f, PoseMath.NearestFireStart(new List<FireInstance>(), Origin, PoseMath.FireMatchRadiusSq));
            Assert.Equal(-1f, PoseMath.NearestFireStart(null, Origin, PoseMath.FireMatchRadiusSq));
        }

        [Fact]
        public void NearestFireStart_PicksTheNearest_IgnoringOnesOutOfRange()
        {
            var fires = new List<FireInstance> { F(9f, 111f), F(3f, 222f), F(1f, 333f), F(2f, 444f) };
            Assert.Equal(333f, PoseMath.NearestFireStart(fires, Origin, PoseMath.FireMatchRadiusSq));
        }

        [Fact]
        public void FireOncePose_RestsAtZeroOnceThePassIsDone()
        {
            Assert.Equal(0f, PoseMath.FireOncePose(elapsed: 2f, dur: 2f));
            Assert.Equal(0f, PoseMath.FireOncePose(elapsed: 99f, dur: 2f));
        }

        [Fact]
        public void FireOncePose_MidPass_IsTheNormalizedPosition()
        {
            Assert.Equal(0.25f, PoseMath.FireOncePose(elapsed: 0.5f, dur: 2f), 4);
        }

        // ------------------------------------------------------------------ NearestDeployPose

        [Fact]
        public void NearestDeployPose_NothingInRange_IsTheAuthoredDefault()
        {
            Assert.Equal(0.8f, PoseMath.NearestDeployPose(new List<DeploySample> { D(5f, 0.2f) }, Origin, fallback: 0.8f));
            Assert.Equal(0.8f, PoseMath.NearestDeployPose(null, Origin, fallback: 0.8f));
        }

        // THE DEPLOY RADIUS IS 3u, NOT 4u. Unifying the radii is the obvious tidy-up and it is wrong: a pawn
        // 3.5u from a deploy sample would start inheriting a neighbour's ramp position.
        [Theory]
        [InlineData(2.99f, 0.2f)]
        [InlineData(3.0f, 0.8f)]     // exactly at the radius — excluded
        [InlineData(3.5f, 0.8f)]     // inside the FIRE radius, outside the DEPLOY radius
        public void NearestDeployPose_RadiusIsThreeUnits_NotTheFireRadius(float dist, float expected)
        {
            Assert.Equal(expected, PoseMath.NearestDeployPose(new List<DeploySample> { D(dist, 0.2f) }, Origin, fallback: 0.8f));
        }

        [Fact]
        public void NearestDeployPose_PicksTheNearestSample()
        {
            var samples = new List<DeploySample> { D(2.5f, 0.1f), D(0.5f, 0.6f), D(1.5f, 0.3f) };
            Assert.Equal(0.6f, PoseMath.NearestDeployPose(samples, Origin, fallback: 0f));
        }

        // ------------------------------------------------------------------ RecoilSweep

        [Fact]
        public void RecoilSweep_InsideTheTail_SweepsFromTheDeployedHoldUpward()
        {
            Assert.True(PoseMath.RecoilSweep(elapsed: 0f, clipDur: 2f, deployPoseTime: 0.5f, recoilSpeed: 1f, out var p0, out var tail));
            Assert.Equal(0.5f, p0, 4);                                   // starts at the hold
            Assert.Equal(2f * (0.999f - 0.5f) / 1f, tail, 4);

            Assert.True(PoseMath.RecoilSweep(tail * 0.5f, 2f, 0.5f, 1f, out var pMid, out _));
            Assert.Equal(0.5f + 0.5f * (0.999f - 0.5f), pMid, 4);        // halfway up the tail
        }

        [Fact]
        public void RecoilSweep_PastTheTail_HoldsTheDeployedPose()
        {
            Assert.False(PoseMath.RecoilSweep(elapsed: 99f, clipDur: 2f, deployPoseTime: 0.5f, recoilSpeed: 1f, out var p, out _));
            Assert.Equal(0.5f, p);
        }

        [Fact]
        public void RecoilSweep_NeverReachesOne_SameWrapTrapAsOneShot()
        {
            PoseMath.RecoilSweep(0.99999f * PoseMath.RecoilTailDuration(2f, 0.5f, 1f), 2f, 0.5f, 1f, out var p, out _);
            Assert.True(p < 1f);
        }

        [Fact]
        public void RecoilSweep_RecoilSpeed_ShortensTheTail()
        {
            var slow = PoseMath.RecoilTailDuration(2f, 0.5f, 1f);
            var fast = PoseMath.RecoilTailDuration(2f, 0.5f, 4f);
            Assert.Equal(slow / 4f, fast, 4);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        public void RecoilSweep_NonPositiveSpeed_FallsBackToAuthoredSpeed(float speed)
        {
            Assert.Equal(PoseMath.RecoilTailDuration(2f, 0.5f, 1f), PoseMath.RecoilTailDuration(2f, 0.5f, speed), 4);
        }

        // A deployPoseTime at or past the cap leaves no tail to sweep — hold, rather than divide into nonsense.
        [Fact]
        public void RecoilSweep_NoTailLeft_HoldsInsteadOfSweeping()
        {
            Assert.False(PoseMath.RecoilSweep(elapsed: 0f, clipDur: 2f, deployPoseTime: 0.999f, recoilSpeed: 1f, out var p, out _));
            Assert.Equal(0.999f, p, 4);
        }

        // ------------------------------------------------------------------ the radii, stated once

        // If someone "simplifies" these to one constant, this fails and says why.
        [Fact]
        public void TheThreeMatchRadii_AreNotAllTheSame()
        {
            Assert.Equal(4f, PoseMath.StateMatchRadius);
            Assert.Equal(4f, PoseMath.FireMatchRadius);
            Assert.Equal(3f, PoseMath.DeployMatchRadius);
            Assert.NotEqual(PoseMath.DeployMatchRadius, PoseMath.FireMatchRadius);
        }
    }
}
