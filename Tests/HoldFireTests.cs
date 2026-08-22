using System;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // FAIL OPEN, NOT CLOSED (2026-08-22 review).
    //
    // Hk_BattleHoldFire is the one hold in the plugin that can SUPPRESS a game action on a bound read by reflection.
    // Its test was `ct == null || elapsed < deadline`, so a null — which GetMember returns on ANY resolution failure —
    // selected the HOLD branch with no deadline at all: the ranged attack never starts and the choreography action
    // never completes. Every sibling hold fails open by explicit policy ("any failure = vanilla, never a stuck army").
    //
    // The decision is now a pure function so the policy is pinned rather than asserted in a comment: returning FALSE
    // means "no bound available", and the caller must release.
    public class HoldFireTests
    {
        [Fact]
        public void NullClock_IsUnusable_SoTheHoldCannotBeBounded()
        {
            // THE REGRESSION: before the fix this shape held the attack forever.
            Assert.False(Hk_BattleHoldFire.TryElapsedSince(null, 100f, out float s));
            Assert.Equal(0f, s);
        }

        [Fact]
        public void NonNumericClock_IsAlsoUnusable_NotAnException()
        {
            // A renamed field can resolve to something that is not a number. Convert.ToSingle would throw; the
            // surrounding try/catch would swallow it and return true (vanilla) — but only by accident, and any
            // future caller would inherit the throw. Rejecting it explicitly is the honest contract.
            Assert.False(Hk_BattleHoldFire.TryElapsedSince("not a time", 100f, out _));
            Assert.False(Hk_BattleHoldFire.TryElapsedSince(new object(), 100f, out _));
        }

        [Fact]
        public void ANumericClock_IsUsable_AndElapsedGrowsAsTheClockRecedes()
        {
            // A clock further in the past yields a larger elapsed.
            Assert.True(Hk_BattleHoldFire.TryElapsedSince(90f, 100f, out float older));
            Assert.True(Hk_BattleHoldFire.TryElapsedSince(99f, 100f, out float newer));
            Assert.True(older > newer);
        }

        [Fact]
        public void IntegerClock_IsAccepted_NotJustFloat()
        {
            // The engine field is a float today, but Convert.ToSingle handles the integral kinds and a future
            // int-typed clock should keep the hold working rather than silently disabling it.
            Assert.True(Hk_BattleHoldFire.TryElapsedSince(95, 100f, out float a));
            Assert.True(Hk_BattleHoldFire.TryElapsedSince((double)95, 100f, out float b));
            Assert.Equal(a, b, 3);
        }
    }
}
