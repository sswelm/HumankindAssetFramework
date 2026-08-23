using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // A DISTRICT THAT NEVER BINDS USED TO SAY NOTHING — the 2026-08-23 review finding.
    //
    // The scoped bind retries ~1/s while a district is unbound, which is correct (selectors load async). What was
    // wrong was everything around it: the one-shot log key was the REASON, not the DISTRICT — so the first district
    // to stall claimed the key and every other district went permanently silent for that reason — and the line was
    // Plugin.Diag, gated behind VerboseLog, which is OFF by default. A district could therefore fail to render for a
    // whole session and emit nothing at any severity, forever.
    //
    // These pin the two properties that fix it: the stall count is PER DISTRICT, and a stall that outlives "still
    // loading" escalates exactly ONCE to a real warning that names the district and the consequence.
    public class BindStallTests
    {
        public BindStallTests()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
            SessionState.Reset(SessionScope.District);   // bindLog + bindAttempts are [SessionScoped(District)]
        }

        // FILTERED to this test's own subject. Plugin.Log is process-wide and xUnit runs test classes in parallel,
        // so an unfiltered capture collects other classes' warnings too — which is exactly how
        // LongStall_WarnsExactlyOnce started failing the moment another class was added. A capture that can see
        // someone else's output is a capture that asserts on noise.
        static List<string> Warnings(Action a, string about)
        {
            var got = new List<string>();
            EventHandler<LogEventArgs> h = (s, e) =>
            {
                if ((e.Level & LogLevel.Warning) == 0) return;
                var msg = e.Data?.ToString() ?? "";
                if (msg.Contains(about)) got.Add(msg);
            };
            Plugin.Log.LogEvent += h;
            try { a(); } finally { Plugin.Log.LogEvent -= h; }
            return got;
        }

        const string Reason = "test reason";

        static void Stall(string district, int times)
        {
            for (int i = 0; i < times; i++) DistrictInject.NoteBindStall(district, Reason);
        }

        // ---- the escalation policy, pure ----

        [Fact]
        public void ShouldEscalate_OnlyOnTheThresholdAttempt()
        {
            int n = DistrictInject.BindEscalateAfter;
            for (int i = 1; i < n; i++) Assert.False(DistrictInject.ShouldEscalateBind(i), $"attempt {i} must not escalate");
            Assert.True(DistrictInject.ShouldEscalateBind(n));
            for (int i = n + 1; i < n + 10; i++) Assert.False(DistrictInject.ShouldEscalateBind(i), $"attempt {i} must not re-escalate");
        }

        // An unrecoverable stall must not become log spam — one warning, not one per second forever.
        [Fact]
        public void LongStall_WarnsExactlyOnce()
        {
            var w = Warnings(() => Stall("DistrictA", DistrictInject.BindEscalateAfter * 3), "DistrictA");
            Assert.Single(w);
        }

        [Fact]
        public void TheWarning_NamesTheDistrictTheReasonAndTheConsequence()
        {
            var msg = Assert.Single(Warnings(() => Stall("DistrictA", DistrictInject.BindEscalateAfter), "DistrictA"));
            Assert.Contains("DistrictA", msg);
            Assert.Contains(Reason, msg);
            Assert.Contains("NOT render", msg);   // the consequence, not just the fact
        }

        // ---- THE BUG: one district must not silence another ----

        // Under the old keying (`bindLog.Add("notgt")` — the reason, shared across districts) the first district to
        // stall claimed the key and the second could never report. Counting per district is what fixes it: A
        // exhausting its budget must leave B's untouched.
        [Fact]
        public void OneDistrictStalling_DoesNotConsumeAnothersBudget()
        {
            Stall("DistrictA", DistrictInject.BindEscalateAfter);           // A escalates
            var w = Warnings(() => Stall("DistrictB", 1), "DistrictB");                  // B's FIRST stall
            Assert.Empty(w);                                                // ...must not ride A's count
        }

        [Fact]
        public void EachDistrictEscalatesOnItsOwn()
        {
            var w = Warnings(() =>
            {
                Stall("DistrictA", DistrictInject.BindEscalateAfter);
                Stall("DistrictB", DistrictInject.BindEscalateAfter);
            }, "District");
            Assert.Equal(2, w.Count);
            Assert.Contains(w, m => m.Contains("DistrictA"));
            Assert.Contains(w, m => m.Contains("DistrictB"));
        }

        // The legacy shared path passes null; it must still be named rather than logged as an empty string.
        [Fact]
        public void TheSharedPath_IsNamed()
        {
            var msg = Assert.Single(Warnings(() => Stall(null, DistrictInject.BindEscalateAfter), "(all districts)"));
            Assert.Contains("(all districts)", msg);
        }

        // A stall short of the threshold is the NORMAL async-load case and must stay quiet at warning level.
        [Fact]
        public void ShortStall_IsSilent()
        {
            Assert.Empty(Warnings(() => Stall("DistrictA", DistrictInject.BindEscalateAfter - 1), "DistrictA"));
        }

        // The threshold has to be long enough that an honest async selector load on a slow machine never trips it.
        [Fact]
        public void Threshold_IsWellBeyondAnAsyncLoad()
        {
            Assert.True(DistrictInject.BindEscalateAfter >= 20,
                "the poll runs ~1/s while unbound, so a threshold under ~20 risks warning about a normal load");
        }
    }
}
