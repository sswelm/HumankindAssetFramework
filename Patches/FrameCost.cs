using System;
using System.Diagnostics;
using System.Text;

namespace HumankindAssetFramework
{
    // PER-FRAME COST METER (2026-08-21). "Should be under 1%" is an estimate; this is the number. Every HAF entry point
    // that runs per frame — the Plugin.Update fan-out, bucket by bucket, and the per-pawn pose hook — is timed with
    // Stopwatch ticks and averaged over a 5-second window. The latest window is shown in the F8 panel and logged once
    // a minute, so a cost regression (or an improvement) is a line in the log, not a feeling. Main-thread only
    // (Update + the pose hook both run there); the counters are plain longs on purpose — no locks on the hot path.
    // The cost of the meter itself: two Stopwatch.GetTimestamp calls per bucket per frame (~25 ns each).
    // First run (2026-08-21): 5.6 ms/frame = 16.7% of a 30 fps frame — 15x the estimate. The buckets named it.
    internal static class FrameCost
    {
        // Buckets — one per Update call group + the pose hook (split: vanilla pawns' early-out vs OUR pawns' full path).
        public const int UpdateTotal = 0, PoseHook = 1, TickTexture = 2, RespawnPostLoad = 3, FireQueues = 4, DeployState = 5,
                         AnimStates = 6, EngineAudio = 7, SubPawnVisuals = 8, BattleCries = 9, Dials = 10, ClassScan = 11,
                         DistrictMeshSwap = 12, DistrictPolls = 13, BattleTurn = 14, FacingPersist = 15, PropRegister = 16,
                         Formation = 17, Rearm = 18, PoseOurs = 19, SelectorTile = 20, MainRows = 21, WonderRows = 22, HexDial = 23,
                         SelTileCfg = 24, SelTileLoop = 25, SelTileBind = 26, SelTileAlbedo = 27, SelTileFlat = 28,   // SelectorTile sub-buckets
                         PoseSweep = 29, PoseAdjust = 30, PoseAnim = 31, PoseAim = 32, PoseDonor = 33;   // PoseOurs sub-buckets (nested inside PoseOurs); PoseDonor = the donor-clip branch (helicopters) (nested inside SelectorTile, so they double-count in a sum — read them as a breakdown)
        [ProcessLived("literal bucket label table")] static readonly string[] names =
        {
            "Update(total)", "PoseVanilla", "TickTexture", "RespawnPostLoad", "FireQueues", "DeployState", "AnimStates", "EngineAudio",
            "SubPawnVisuals", "BattleCries", "Dials", "ClassScan", "DistrictMeshSwap", "DistrictDbg", "BattleTurn", "FacingPersist",
            "PropRegister", "Formation", "Rearm", "PoseOurs", "SelectorTile", "MainRows", "WonderRows", "HexDial",
            "SelTileCfg", "SelTileLoop", "SelTileBind", "SelTileAlbedo", "SelTileFlat", "PoseSweep", "PoseAdjust", "PoseAnim", "PoseAim", "PoseDonor",
        };
        public static int Count => names.Length;
        public static string Name(int bucket) => names[bucket];

        [ProcessLived("per-MEASUREMENT-WINDOW counters, reset by EndFrame every 5s - not session state")] static readonly long[] ticks = new long[names.Length];
        [ProcessLived("per-measurement-window counters, reset by EndFrame - not session state")] static readonly long[] calls = new long[names.Length];
        static int frames;
        static long windowStart;
        static float nextLogAt;
        public const float WindowSeconds = 5f;

        // The latest completed window, formatted. Empty until the first window closes.
        public static string Summary { get; private set; } = "";
        public static string Detail  { get; private set; } = "";

        public static long Begin() => Stopwatch.GetTimestamp();
        public static void End(int bucket, long t0) { ticks[bucket] += Stopwatch.GetTimestamp() - t0; calls[bucket]++; }

        // Called once per Update after all buckets; closes the window every WindowSeconds.
        public static void EndFrame(float realtime)
        {
            frames++;
            if (windowStart == 0) { windowStart = Stopwatch.GetTimestamp(); return; }
            double elapsed = (Stopwatch.GetTimestamp() - windowStart) / (double)Stopwatch.Frequency;
            if (elapsed < WindowSeconds) return;
            var snap = Format(ticks, calls, frames, elapsed, Stopwatch.Frequency, out string detail);
            Summary = snap; Detail = detail;
            if (realtime >= nextLogAt) { nextLogAt = realtime + 60f; Plugin.Log.LogInfo("[FrameCost] " + snap + " | " + detail); }
            Array.Clear(ticks, 0, ticks.Length); Array.Clear(calls, 0, calls.Length); frames = 0;
            windowStart = Stopwatch.GetTimestamp();
        }

        // PURE: turns one window's counters into the two report lines. Unit-tested.
        //   summary: "HAF 142 µs/frame (0.9% @ 60 fps) | Update 64 µs | pose vanilla 38 µs = 312 adds × 122 ns | pose ours 40 µs = 8 adds × 5000 ns"
        //   detail : "TickTexture 61 µs, PoseOurs 40 µs, ..." (top 6 buckets by cost, µs per frame, Update(total) excluded)
        internal static string Format(long[] tk, long[] cl, int frameCount, double elapsedSeconds, long frequency, out string detail)
        {
            if (frameCount <= 0) { detail = ""; return "no frames"; }
            double usPerTick = 1e6 / frequency;
            double fps = frameCount / Math.Max(elapsedSeconds, 1e-6);
            double frameUs = 1e6 / Math.Max(fps, 1e-6);
            double updateUs = tk[UpdateTotal] * usPerTick / frameCount;
            double poseVanUs = tk[PoseHook] * usPerTick / frameCount;
            double poseOurUs = tk[PoseOurs] * usPerTick / frameCount;
            double totalUs = updateUs + poseVanUs + poseOurUs;
            double vanAdds = cl[PoseHook] / (double)frameCount, ourAdds = cl[PoseOurs] / (double)frameCount;
            double vanNs = cl[PoseHook] > 0 ? tk[PoseHook] * usPerTick * 1000.0 / cl[PoseHook] : 0;
            double ourNs = cl[PoseOurs] > 0 ? tk[PoseOurs] * usPerTick * 1000.0 / cl[PoseOurs] : 0;
            var summary = Inv($"HAF {totalUs:0} µs/frame ({100.0 * totalUs / frameUs:0.0}% @ {fps:0} fps) | Update {updateUs:0} µs | pose vanilla {poseVanUs:0} µs = {vanAdds:0} adds × {vanNs:0} ns | pose ours {poseOurUs:0} µs = {ourAdds:0} adds × {ourNs:0} ns");
            // detail: every bucket except the total, sorted by cost, top 6, only those that ran
            var idx = new int[tk.Length - 1]; for (int i = 1; i < tk.Length; i++) idx[i - 1] = i;
            Array.Sort(idx, (a, b) => tk[b].CompareTo(tk[a]));
            var sb = new StringBuilder(); int shown = 0;
            foreach (var i in idx)
            {
                if (cl[i] == 0 || shown >= 6) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(names[i]).Append(' ').Append(Inv($"{tk[i] * usPerTick / frameCount:0.#}")).Append(" µs");
                shown++;
            }
            detail = sb.ToString();
            return summary;
        }

        static string Inv(FormattableString s) => FormattableString.Invariant(s);
    }
}
