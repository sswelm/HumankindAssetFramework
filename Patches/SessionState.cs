using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HumankindAssetFramework
{
    // SESSION-SCOPED STATE, DECLARED — not remembered.
    //
    // The plugin holds ~150 static collections across UniversalInject / DistrictInject / the patch classes. A large share
    // of them are keyed by something that dies with the game session — descriptor ids, skeleton ids, unit GUIDs, pawn
    // instance ids, Time.time stamps, presentation objects — and a NEW session can reuse every one of those keys. The
    // recurring bug class of this project (the Oracle incident, the _DRILL pack-data bug, the tank-destroyer donor skin)
    // was always the same: a static that survived re-arm. Until 2026-08-21 the rule "every session-keyed map gets
    // cleared on re-arm" lived in Architecture.md and in a hand-written list of ~60 `.Clear()` calls, so a new field
    // was only safe if its author remembered the rule across sixteen files.
    //
    // Now the rule is enforced by a test that needs neither the game nor Unity (Tests/SessionStateTests.cs): every
    // static collection field in this assembly must carry exactly one of
    //   [SessionScoped]                          — this registry clears it on the matching Reset (Model or District)
    //   [SessionScoped(Manual = "where")]        — session-scoped, reset by hand at the named site (lock-guarded, nulled,
    //                                              rebuilt rather than cleared, or owned by a different seam)
    //   [ProcessLived("why")]                    — deliberately outlives sessions (a type cache, a name-keyed once-log,
    //                                              per-tick scratch, the registry itself)
    // A bare static collection fails the build's test run. The registry finds its fields by reflection ONCE per process;
    // Reset() then calls each collection's Clear(). What the registry cannot prove is ORDER — the hand-written parts of
    // RearmModelRegistration / ResetDistrictSessionState still own the sequence around the bulk clear.
    internal enum SessionScope { Model, District }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    internal sealed class SessionScopedAttribute : Attribute
    {
        public SessionScope Scope = SessionScope.Model;
        public string Manual;   // set = the registry does NOT touch it; the named site resets it by hand
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    internal sealed class ProcessLivedAttribute : Attribute
    {
        public readonly string Reason;
        public ProcessLivedAttribute(string reason) { Reason = reason; }
    }

    internal static class SessionState
    {
        const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // A "collection" for the rule: a non-string type with a public, parameterless, instance Clear().
        internal static MethodInfo ClearMethodOf(Type t)
        {
            if (t == null || t == typeof(string) || t.IsPrimitive || t.IsEnum) return null;
            return t.GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        }

        // THE FENCE WAS DRAWN BY AN IMPLEMENTATION DETAIL (review 2026-08-22). Membership was "the type happens to
        // have a public parameterless Clear()", which on net471 silently excludes ConcurrentQueue<T>, ConcurrentBag<T>,
        // arrays and ConditionalWeakTable — 137 of 549 author-written statics were inside it. The rule is now "state
        // shaped like session state", and each shape brings its own clearer:
        //   * Clear()                 — every BCL collection, unchanged
        //   * ConcurrentQueue/Bag<T>  — drained via TryDequeue/TryTake (this framework has no Clear() on them)
        //   * arrays                  — Array.Clear over the whole length
        //   * ConditionalWeakTable    — CANNOT be emptied in place, so it must declare [ProcessLived] or
        //                               [SessionScoped(Manual=…)]; the test now forces that instead of never seeing it
        // Scalars stay outside on purpose: a static bool/int can be a constant, a cache, or a per-session latch, and
        // the difference is intent rather than shape. UnpolicedStaticCount() reports how many remain, so the edge of
        // the fence is a number in the test output instead of an implied "everything is covered".
        internal static bool IsConcurrentDrainable(Type t) =>
            t != null && t.IsGenericType
            && (t.GetGenericTypeDefinition() == typeof(System.Collections.Concurrent.ConcurrentQueue<>)
             || t.GetGenericTypeDefinition() == typeof(System.Collections.Concurrent.ConcurrentBag<>));

        internal static bool IsWeakTable(Type t) =>
            t != null && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>);

        internal static bool IsSessionStateShape(Type t) =>
            ClearMethodOf(t) != null || IsConcurrentDrainable(t) || IsWeakTable(t) || (t != null && t.IsArray);

        // The clearer for a field's type, or null when the shape cannot be emptied in place (weak tables).
        internal static Action<object> ClearerFor(Type t)
        {
            var m = ClearMethodOf(t);
            if (m != null) return v => m.Invoke(v, null);
            if (t != null && t.IsArray) return v => { var a = (Array)v; if (a.Length > 0) Array.Clear(a, 0, a.Length); };
            if (IsConcurrentDrainable(t))
            {
                var take = t.GetMethod("TryDequeue", BindingFlags.Public | BindingFlags.Instance)
                        ?? t.GetMethod("TryTake", BindingFlags.Public | BindingFlags.Instance);
                if (take == null) return null;
                return v => { var args = new object[1]; int guard = 0; while ((bool)take.Invoke(v, args) && ++guard < 1000000) { } };
            }
            return null;
        }

        // Every static field in the assembly holding session-state shape (incl. nested + compiler-generated holders).
        internal static IEnumerable<FieldInfo> StaticCollectionFields(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            foreach (var t in types)
                foreach (var f in t.GetFields(Statics))
                    if (IsSessionStateShape(f.FieldType)) yield return f;
        }

        // VISIBILITY, not enforcement: author-written mutable statics the rule does NOT police (scalars, delegates,
        // game-object handles), so the fence's edge is measured rather than implied.
        internal static int UnpolicedStaticCount(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            int n = 0;
            foreach (var t in types)
            {
                if (t.Name.IndexOf('<') >= 0) continue;
                foreach (var f in t.GetFields(Statics))
                {
                    if (f.IsLiteral || f.Name.IndexOf('<') >= 0 || IsSessionStateShape(f.FieldType)) continue;
                    if (f.IsInitOnly) continue;
                    n++;
                }
            }
            return n;
        }

        internal static string Describe(FieldInfo f) => (f.DeclaringType?.FullName ?? "?") + "." + f.Name;

        sealed class Entry { public FieldInfo Field; public Action<object> Clear; }
        [ProcessLived("the registry itself")] static readonly Dictionary<Assembly, Dictionary<SessionScope, List<Entry>>> _registry = new Dictionary<Assembly, Dictionary<SessionScope, List<Entry>>>();   // built once per assembly
        static readonly object _gate = new object();

        static Dictionary<SessionScope, List<Entry>> Registry(Assembly asm)
        {
            lock (_gate)
            {
                if (_registry.TryGetValue(asm, out var have)) return have;
                var reg = new Dictionary<SessionScope, List<Entry>> { { SessionScope.Model, new List<Entry>() }, { SessionScope.District, new List<Entry>() } };
                foreach (var f in StaticCollectionFields(asm))
                {
                    var a = f.GetCustomAttribute<SessionScopedAttribute>();
                    if (a == null || a.Manual != null) continue;
                    var clear = ClearerFor(f.FieldType);
                    if (clear == null)   // no in-place empty (a weak table): it must say Manual rather than sit here mute
                    {
                        Plugin.Log?.LogWarning($"[Session] {Describe(f)} is [SessionScoped] but its type has no in-place clear — declare it Manual and reset it by hand.");
                        continue;
                    }
                    reg[a.Scope].Add(new Entry { Field = f, Clear = clear });
                }
                return _registry[asm] = reg;
            }
        }

        // Clears every registry-managed collection of the scope. Null fields (lazily built) are skipped. Returns the
        // number cleared; a throwing Clear() is logged and does not stop the others.
        internal static int Reset(SessionScope scope, Assembly asm = null)
        {
            int n = 0;
            foreach (var e in Registry(asm ?? typeof(SessionState).Assembly)[scope])
            {
                try
                {
                    var v = e.Field.GetValue(null);
                    if (v == null) continue;
                    e.Clear(v); n++;
                }
                catch (Exception ex) { Plugin.Log?.LogError($"[Session] reset of {Describe(e.Field)} threw: {ex.InnerException ?? ex}"); }
            }
            return n;
        }

        // Test-facing: the registry-managed field names of a scope (so a test can assert a field IS registry-cleared).
        internal static IEnumerable<string> Registered(SessionScope scope, Assembly asm = null) =>
            Registry(asm ?? typeof(SessionState).Assembly)[scope].Select(e => Describe(e.Field));

        internal static void ResetForTests() { lock (_gate) _registry.Clear(); }
    }
}
