using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The per-frame cost meter's FORMAT is pure: given a window's tick counters it must produce the µs/frame numbers,
    // the percent-of-frame, the per-pawn nanoseconds and a top-N detail line — invariant culture, no allocation traps.
    public class FrameCostTests
    {
        const long Freq = 10_000_000;   // 10 MHz: 1 tick = 0.1 µs, keeps the arithmetic exact

        static long[] Ticks() => new long[FrameCost.Count];
        static long[] Calls() => new long[FrameCost.Count];

        [Fact]
        public void Format_UpdateAndPose_MicrosPerFrame_PercentAndNsPerPawn()
        {
            var tk = Ticks(); var cl = Calls();
            int frames = 300; double elapsed = 5.0;                 // 60 fps
            tk[FrameCost.UpdateTotal] = 1000 * frames; cl[FrameCost.UpdateTotal] = frames;   // 100 µs/frame
            tk[FrameCost.PoseHook] = 400 * frames;  cl[FrameCost.PoseHook] = 200L * frames;  // vanilla: 40 µs/frame over 200 adds/frame = 200 ns each
            tk[FrameCost.PoseOurs] = 500 * frames;  cl[FrameCost.PoseOurs] = 10L * frames;   // ours: 50 µs/frame over 10 adds/frame = 5000 ns each
            tk[FrameCost.TickTexture] = 600 * frames; cl[FrameCost.TickTexture] = frames;
            var s = FrameCost.Format(tk, cl, frames, elapsed, Freq, out var detail);
            Assert.Contains("HAF 190 µs/frame", s);
            Assert.Contains("(1.1% @ 60 fps)", s);                  // 190 µs of a 16,667 µs frame
            Assert.Contains("Update 100 µs", s);
            Assert.Contains("pose vanilla 40 µs = 200 adds × 200 ns", s);
            Assert.Contains("pose ours 50 µs = 10 adds × 5000 ns", s);
            Assert.StartsWith("TickTexture 60 µs, PoseOurs 50 µs, PoseVanilla 40 µs", detail);   // sorted by cost, Update(total) excluded
            Assert.DoesNotContain("Update(total)", detail);
        }

        [Fact]
        public void Format_OnlyBucketsThatRan_TopSix_InvariantCulture()
        {
            var tk = Ticks(); var cl = Calls();
            int frames = 10;
            for (int i = 1; i < FrameCost.Count; i++) { tk[i] = (FrameCost.Count - i) * 5 * frames; cl[i] = frames; }   // descending cost
            cl[FrameCost.BattleCries] = 0; tk[FrameCost.BattleCries] = 0;   // never ran -> must not appear
            var s = FrameCost.Format(tk, cl, frames, 1.0, Freq, out var detail);
            Assert.Equal(6, detail.Split(new[] { ", " }, System.StringSplitOptions.None).Length);
            Assert.DoesNotContain("BattleCries", detail);
            Assert.DoesNotContain(",5", s); Assert.DoesNotContain(",5", detail);   // no comma decimals regardless of host locale
            Assert.Contains("µs", detail);
        }

        [Fact]
        public void Format_NoFrames_SaysSo_NoDivideByZero()
        {
            var s = FrameCost.Format(Ticks(), Calls(), 0, 5.0, Freq, out var detail);
            Assert.Equal("no frames", s);
            Assert.Equal("", detail);
        }

        [Fact]
        public void Format_NoPawnAdds_ZeroNsNotNaN()
        {
            var tk = Ticks(); var cl = Calls(); int frames = 60;
            tk[FrameCost.UpdateTotal] = 100 * frames; cl[FrameCost.UpdateTotal] = frames;
            var s = FrameCost.Format(tk, cl, frames, 1.0, Freq, out _);
            Assert.Contains("pose vanilla 0 µs = 0 adds × 0 ns", s);
            Assert.DoesNotContain("NaN", s);
        }
    }
}
