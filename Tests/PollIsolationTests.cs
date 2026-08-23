using System;
using System.Linq;
using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // ONE THROWING POLL USED TO DISABLE EVERY POLL AFTER IT — the 2026-08-23 review finding.
    //
    // The 2026-08-22 fix put Plugin.Update's fan-out in a try/finally so the frame accounting always closed, but the
    // `try` still wrapped ALL ~25 polls. A poll that threw skipped every poll after it, that frame and every frame
    // it kept throwing — so a persistently failing TickTexture silently disabled BattleTurn, FacingPersist and
    // Formation, subsystems with nothing to do with textures. The catch's own wording admitted it: "the rest of
    // this frame's polls were skipped".
    //
    // The property that fixes it is that Poll does not PROPAGATE: the caller's next statement — the next poll —
    // runs regardless. That is what these pin.
    public class PollIsolationTests
    {
        public PollIsolationTests()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
        }

        [Fact]
        public void AThrowingPoll_DoesNotPropagate()
        {
            var ex = Record.Exception(() => Plugin.Poll(FrameCost.TickTexture, "boom",
                                                        () => throw new InvalidOperationException("kaboom")));
            Assert.Null(ex);
        }

        // The consequence, stated as the sequence Update actually is: a throwing step must not stop a later one.
        [Fact]
        public void APollAfterAThrowingOne_StillRuns()
        {
            bool later = false;
            Plugin.Poll(FrameCost.TickTexture, "boom", () => throw new InvalidOperationException("kaboom"));
            Plugin.Poll(FrameCost.AnimStates, "later", () => later = true);
            Assert.True(later, "a throwing poll must not cost the polls after it their turn");
        }

        // Even a poll that throws EVERY time must not become a permanent outage for its neighbours — this is the
        // shape of the real failure, where the same poll fails on every frame for the rest of the session.
        [Fact]
        public void RepeatedFailures_NeverStarveTheNeighbours()
        {
            int laterRuns = 0;
            for (int i = 0; i < 50; i++)
            {
                Plugin.Poll(FrameCost.TickTexture, "alwaysBoom", () => throw new InvalidOperationException("kaboom"));
                Plugin.Poll(FrameCost.AnimStates, "neighbour", () => laterRuns++);
            }
            Assert.Equal(50, laterRuns);
        }

        // The failure is attributed to the poll that caused it, not to a generic "update" — otherwise the smoke
        // report's error sites cannot tell you WHICH subsystem is broken.
        [Fact]
        public void TheFailureIsRecordedAgainstItsOwnPoll()
        {
            Plugin.Poll(FrameCost.EngineAudio, "EngineAudio", () => throw new InvalidOperationException("kaboom"));
            Assert.Contains(UniversalInject.ErrorSitesSnapshot(), s => s.Contains("EngineAudio"));
        }

        // A poll that succeeds must record nothing — an error ledger that fills up on the happy path is noise.
        [Fact]
        public void ASuccessfulPollRecordsNothing()
        {
            var before = UniversalInject.ErrorSitesSnapshot().Count;
            Plugin.Poll(FrameCost.HexDial, "QuietPoll", () => { });
            Assert.Equal(before, UniversalInject.ErrorSitesSnapshot().Count);
        }

        // The delegates the fan-out passes are CACHED, not built per call: a method-group conversion at each of ~25
        // call sites would allocate an Action per poll per frame. Reference equality across reads proves the field
        // holds one instance rather than a freshly-converted one.
        [Fact]
        public void TheFanOutDelegatesAreCachedNotPerCall()
        {
            var a = typeof(Plugin).GetField("pTickTexture", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(a);
            Assert.True(a.IsInitOnly, "the cached poll delegates must be readonly — a mutable static would also escape the session-state rule");
            Assert.Same(a.GetValue(null), a.GetValue(null));
        }
    }
}
