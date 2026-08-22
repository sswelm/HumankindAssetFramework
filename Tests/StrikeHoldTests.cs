using HumankindAssetFramework;
using Xunit;

// StrikeHoldTests — the strike hold's "am I already armed?" decision.
//
// Written with the fix for the 2026-08-22 review's critical: TurnHoldForStrike reused ANY aim override found
// near the firing pawn, but an override outlives its strike by two minutes on purpose (it is also the facing
// long-stop). A second bombard from the same tile therefore inherited the first strike's expired clock and
// fired on the old target's bearing. The decision is now a pure predicate so it can be pinned here — this
// subsystem's holds had no unit tests at all, which is why a single-shot in-game drill was the only oracle.
public class StrikeHoldTests
{
    const float Now = 100f;

    [Fact]
    public void NoOverrideNearby_ArmsAFreshHold()
    {
        // nothing found -> fall through and compute a new bearing, whatever the (unused) release time says
        Assert.False(UniversalInject.ArmedHoldPending(false, 0f, Now));
        Assert.False(UniversalInject.ArmedHoldPending(false, Now + 5f, Now));
    }

    [Fact]
    public void ReleaseStillInTheFuture_ReusesTheSharedClock()
    {
        // THE REASON THE TEST EXISTS: one strike prefixes three times (visuals + the schedules) and every
        // caller must get the SAME remaining hold, or the bang desyncs from the recoil.
        Assert.True(UniversalInject.ArmedHoldPending(true, Now + 2.5f, Now));
        Assert.True(UniversalInject.ArmedHoldPending(true, Now + 0.01f, Now));
    }

    [Fact]
    public void ReleaseAlreadyPassed_ArmsAFreshHold_TheRegression()
    {
        // The critical, as a test: a 120 s override whose release time is long gone is a STALE facing marker,
        // not an armed strike. Before the fix this returned true and the second bombard fired without turning.
        Assert.False(UniversalInject.ArmedHoldPending(true, Now - 40f, Now));
        Assert.False(UniversalInject.ArmedHoldPending(true, Now - 0.5f, Now));
    }

    [Fact]
    public void ReleaseExactlyNow_ArmsAFreshHold()
    {
        // A hold of 0 (already aligned) stores releaseAt == now. Re-arming is correct and cheap: the same 0
        // comes back and the bearing is refreshed in place.
        Assert.False(UniversalInject.ArmedHoldPending(true, Now, Now));
    }

    [Fact]
    public void TheDecisionIgnoresHowOldTheOverrideIs_OnlyWhetherItIsStillPending()
    {
        // An override armed 119 s ago whose release is still ahead (a long extended hold) is legitimately
        // reusable; a fresh one whose release already passed is not. Age is not the signal — pendency is.
        Assert.True(UniversalInject.ArmedHoldPending(true, Now + 1f, Now));
        Assert.False(UniversalInject.ArmedHoldPending(true, Now - 1f, Now));
    }
}
