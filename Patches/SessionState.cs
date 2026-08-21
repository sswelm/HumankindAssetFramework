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

        // Every static field in the assembly whose type is a collection (incl. nested + compiler-generated holders).
        internal static IEnumerable<FieldInfo> StaticCollectionFields(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            foreach (var t in types)
                foreach (var f in t.GetFields(Statics))
                    if (ClearMethodOf(f.FieldType) != null) yield return f;
        }

        internal static string Describe(FieldInfo f) => (f.DeclaringType?.FullName ?? "?") + "." + f.Name;

        sealed class Entry { public FieldInfo Field; public MethodInfo Clear; }
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
                    reg[a.Scope].Add(new Entry { Field = f, Clear = ClearMethodOf(f.FieldType) });
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
                    e.Clear.Invoke(v, null); n++;
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
