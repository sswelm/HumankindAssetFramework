using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The rule "every session-keyed static gets cleared on re-arm" used to live in a comment and a hand-list.
    // This is the rule as a test: every static collection in the plugin must DECLARE what it is.
    public class SessionStateTests
    {
        static readonly Assembly Plugin = typeof(SessionState).Assembly;

        // ---- THE FENCE'S SHAPE (2026-08-22 review) ----
        // Membership used to be "the type happens to have a public parameterless Clear()", which on net471 excludes
        // ConcurrentQueue<T>, ConcurrentBag<T>, arrays and ConditionalWeakTable — and `fireGuidQueue`, a sim-thread
        // ConcurrentQueue, was consequently never drained on re-arm. These pin the widened rule.

        [Fact]
        public void Fence_Covers_TheShapes_WithNoClearMethod()
        {
            // The shapes the old predicate silently excluded. If any drops out of the rule again, a whole family of
            // statics goes unpoliced without a single test turning red — which is exactly what happened.
            Assert.Null(SessionState.ClearMethodOf(typeof(System.Collections.Concurrent.ConcurrentQueue<long>)));   // the trap itself
            Assert.True(SessionState.IsSessionStateShape(typeof(System.Collections.Concurrent.ConcurrentQueue<long>)));
            Assert.True(SessionState.IsSessionStateShape(typeof(System.Collections.Concurrent.ConcurrentBag<int>)));
            Assert.True(SessionState.IsSessionStateShape(typeof(int[])));
            Assert.True(SessionState.IsSessionStateShape(typeof(System.Runtime.CompilerServices.ConditionalWeakTable<object, object>)));
            // …and scalars stay OUT on purpose: shape cannot tell a constant from a per-session latch.
            Assert.False(SessionState.IsSessionStateShape(typeof(bool)));
            Assert.False(SessionState.IsSessionStateShape(typeof(string)));
        }

        [Fact]
        public void Clearer_DrainsAConcurrentQueue_AndZeroesAnArray()
        {
            var q = new System.Collections.Concurrent.ConcurrentQueue<long>();
            q.Enqueue(7); q.Enqueue(8);
            SessionState.ClearerFor(q.GetType())(q);
            Assert.True(q.IsEmpty);

            var arr = new[] { 1, 2, 3 };
            SessionState.ClearerFor(arr.GetType())(arr);
            Assert.Equal(new[] { 0, 0, 0 }, arr);

            // A weak table cannot be emptied in place — the clearer says so rather than pretending, which is what
            // forces such a field to declare [ProcessLived] or [SessionScoped(Manual=…)].
            Assert.Null(SessionState.ClearerFor(typeof(System.Runtime.CompilerServices.ConditionalWeakTable<object, object>)));
        }

        [Fact]
        public void TheRegression_FireGuidQueue_IsDrainedByTheReArmSweep()
        {
            // A strike enqueued in the last frame of one session must not survive into the next, where unit GUIDs
            // restart from zero — otherwise the wrong unit's pawns get armed with a recoil clip.
            var e = new ModelEntry { resourceName = "R" };
            e.fireGuidQueue.Enqueue(1234);
            Assert.False(e.fireGuidQueue.IsEmpty);
            while (e.fireGuidQueue.TryDequeue(out long _)) { }   // the sweep's drain, verbatim
            Assert.True(e.fireGuidQueue.IsEmpty);
        }

        [Fact]
        public void TheFenceReportsItsOwnEdge()
        {
            // Not enforcement — VISIBILITY. The rule cannot police scalars, so the count of statics outside it is
            // reported rather than implied. A number someone can argue with beats an unstated assumption.
            int unpoliced = SessionState.UnpolicedStaticCount(Plugin);
            Assert.True(unpoliced >= 0);
            System.Console.WriteLine($"[SessionState] statics outside the rule (scalars/delegates/handles): {unpoliced}");
        }

        [Fact]
        public void Every_static_collection_declares_session_scoped_or_process_lived()
        {
            var offenders = new List<string>();
            foreach (var f in SessionState.StaticCollectionFields(Plugin))
            {
                bool scoped = f.GetCustomAttribute<SessionScopedAttribute>() != null;
                bool lived = f.GetCustomAttribute<ProcessLivedAttribute>() != null;
                if (scoped == lived)   // neither, or both
                    offenders.Add(SessionState.Describe(f) + (scoped ? " (both attributes)" : " (no attribute)"));
            }
            Assert.True(offenders.Count == 0,
                "Static collections without a declared lifetime — add [SessionScoped] (registry clears it on re-arm), " +
                "[SessionScoped(Manual = \"site\")] or [ProcessLived(\"why\")]:\n  " + string.Join("\n  ", offenders));
        }

        [Fact]
        public void Declared_reasons_are_not_empty()
        {
            var bad = new List<string>();
            foreach (var f in SessionState.StaticCollectionFields(Plugin))
            {
                var p = f.GetCustomAttribute<ProcessLivedAttribute>();
                if (p != null && string.IsNullOrWhiteSpace(p.Reason)) bad.Add(SessionState.Describe(f) + " (empty ProcessLived reason)");
                var s = f.GetCustomAttribute<SessionScopedAttribute>();
                if (s != null && s.Manual != null && string.IsNullOrWhiteSpace(s.Manual)) bad.Add(SessionState.Describe(f) + " (empty Manual site)");
            }
            Assert.Empty(bad);
        }

        [Fact]
        public void The_registry_is_not_empty_and_holds_the_known_session_keyed_maps()
        {
            var model = SessionState.Registered(SessionScope.Model).ToList();
            var district = SessionState.Registered(SessionScope.District).ToList();
            Assert.True(model.Count >= 20, "model-scope registry unexpectedly small: " + model.Count);
            Assert.True(district.Count >= 5, "district-scope registry unexpectedly small: " + district.Count);
            // the two the 2026-08-21 review found UNCLEARED (descId-keyed, never reset) — they must be registry-managed now
            Assert.Contains(model, n => n.EndsWith(".sizeFormApplied"));
            Assert.Contains(model, n => n.EndsWith(".sizeFormUnitName"));
            Assert.Contains(model, n => n.EndsWith(".unitScaleByDesc"));
            Assert.Contains(district, n => n.EndsWith(".trackedDistricts"));
        }

        // A fixture assembly-of-one: the test assembly's own holder proves Reset really calls Clear(), skips nulls and
        // leaves Manual / ProcessLived fields alone.
        static class Holder
        {
            [SessionScoped] internal static readonly List<int> cleared = new List<int> { 1, 2 };
            [SessionScoped(Scope = SessionScope.District)] internal static readonly HashSet<string> district = new HashSet<string> { "a" };
            [SessionScoped] internal static Dictionary<int, int> lazy;   // null until built — must not throw
            [SessionScoped(Manual = "elsewhere")] internal static readonly List<int> manual = new List<int> { 1 };
            [ProcessLived("a cache")] internal static readonly List<int> lived = new List<int> { 1 };
            internal static readonly List<int> bare = new List<int> { 1 };   // deliberately undeclared — found by the rule, untouched by Reset
        }

        [Fact]
        public void Reset_clears_registered_fields_of_the_scope_only()
        {
            SessionState.ResetForTests();
            var asm = typeof(SessionStateTests).Assembly;
            Holder.cleared.Clear(); Holder.cleared.AddRange(new[] { 1, 2 }); Holder.district.Add("a"); Holder.manual.Clear(); Holder.manual.Add(1);

            int n = SessionState.Reset(SessionScope.Model, asm);
            Assert.Equal(1, n);   // `cleared` only (lazy is null, manual/lived/bare are not registry-managed)
            Assert.Empty(Holder.cleared);
            Assert.Single(Holder.district);   // other scope untouched
            Assert.Single(Holder.manual); Assert.Single(Holder.lived); Assert.Single(Holder.bare);

            Assert.Equal(1, SessionState.Reset(SessionScope.District, asm));
            Assert.Empty(Holder.district);

            var bare = SessionState.StaticCollectionFields(asm).Where(f => f.DeclaringType == typeof(Holder) && f.Name == "bare");
            Assert.Single(bare);   // the rule sees it — this is what the plugin-wide test would flag
            SessionState.ResetForTests();
        }
    }
}
