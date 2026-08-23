using System.Linq;
using System.Reflection;
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

        // THE DISTRICT SCAN SEGMENT (2026-08-23). SelectorTile is 36% of HAF's per-frame cost and its two halves need
        // opposite fixes — too MANY districts walked, or too much work per match. The summary states both counts and
        // the per-district cost so that is readable rather than inferable; these pin it so it can't quietly stop.
        [Fact]
        public void Format_StatesTheDistrictScanSplit()
        {
            var tk = Ticks(); var cl = Calls();
            int frames = 300; double elapsed = 5.0;
            tk[FrameCost.UpdateTotal] = 1000 * frames; cl[FrameCost.UpdateTotal] = frames;
            tk[FrameCost.SelTileSkip] = 470 * frames; cl[FrameCost.SelTileSkip] = 47L * frames;   // 47 µs/frame over 47 skips = 1000 ns each
            tk[FrameCost.SelTileOurs] = 1800 * frames; cl[FrameCost.SelTileOurs] = 1L * frames;   // 180 µs/frame on ONE district
            var s = FrameCost.Format(tk, cl, frames, elapsed, Freq, out _);
            Assert.Contains("districts 47 skipped 47 µs (1000 ns ea)", s);
            Assert.Contains("1 ours 180 µs (180000 ns ea)", s);
        }

        // Silent when the district axis isn't running — an empty segment on every line would be noise, and the
        // summary is read every minute in the log.
        [Fact]
        public void Format_OmitsTheDistrictSegmentWhenNoDistrictsWereWalked()
        {
            var tk = Ticks(); var cl = Calls();
            tk[FrameCost.UpdateTotal] = 1000 * 300; cl[FrameCost.UpdateTotal] = 300;
            var s = FrameCost.Format(tk, cl, 300, 5.0, Freq, out _);
            Assert.DoesNotContain("districts", s);
        }

        // EVERY DECLARED BUCKET ID MUST HAVE A LABEL. `Count` is `names.Length`, and the arrays are sized from it, so
        // a const id added without its name entry is not a compile error — it is an IndexOutOfRangeException the
        // first time that bucket is timed, i.e. in-game, on whatever path the new bucket was added to measure.
        // Seven ids were added on 2026-08-23 alone (four Formation, three donor), which is exactly when a table like
        // this drifts. Reflection over the consts so the test cannot itself go stale.
        [Fact]
        public void EveryBucketConstant_HasAName()
        {
            var consts = typeof(FrameCost).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                                          .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(int))
                                          .ToArray();
            Assert.NotEmpty(consts);
            foreach (var f in consts)
            {
                int id = (int)f.GetRawConstantValue();
                Assert.True(id >= 0 && id < FrameCost.Count,
                            $"bucket '{f.Name}' = {id} is outside the label table (Count={FrameCost.Count}) — add its entry to `names`");
                Assert.False(string.IsNullOrWhiteSpace(FrameCost.Name(id)), $"bucket '{f.Name}' has a blank label");
            }
            // and no two ids collide, which would silently merge two measurements into one
            var ids = consts.Select(f => (int)f.GetRawConstantValue()).ToArray();
            Assert.Equal(ids.Length, ids.Distinct().Count());
        }
    }
}
