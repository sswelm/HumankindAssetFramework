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
