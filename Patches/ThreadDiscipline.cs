using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HumankindAssetFramework
{
    // THREAD DISCIPLINE, DECLARED — the per-instance companion to SessionState's lifetime attributes.
    //
    // Humankind runs its simulation on a thread of its own and several HAF hooks fire on it, so every mutable field on
    // a shared object needs an answer to "who touches this, and what makes that safe?". `ModelEntry` carries 23 mutable
    // collections next to ~90 immutable config members, and until 2026-08-22 the answer lived in a per-field comment —
    // on SIX of the 23. The other seventeen said nothing at all. Architecture.md §2 had to spell out the four locked
    // ones by name, a table a maintainer has to memorise, and §2 itself records why a comment is not enough: BOTH of
    // the 2026-08-21 data races hid behind one ("Main-thread only", "pure reference-nulling").
    //
    // A review proposed splitting ModelEntry into config/state objects to fix this by shape. That stays declined —
    // its stated trigger (Decisions.md: "a proven bug from the shape") was not met, since neither 08-21 race was in
    // ModelEntry: one was the reflection-cache statics, the other DistrictInject's collections. What the review was
    // right about is the memorisation, and that is fixed here the same way the session-state rule was: every mutable
    // field DECLARES its discipline and a test fails the build on any that doesn't.
    //
    //   [MainThread("who")]  — only Plugin.Update's polls, the pose hook, or a presentation hook touches it.
    //   [Locked("why")]      — every access takes `lock (field)`; use when more than one phase/thread can reach it.
    //   [Concurrent("why")]  — a System.Collections.Concurrent type, safe by construction. VERIFIED by the test: the
    //                          field's type really must live in that namespace, so this one can't be claimed falsely.
    //
    // What this does NOT prove (stated so nobody reads more into a green test than is there): that a `[Locked]` field's
    // every access site actually takes the lock, or that a `[MainThread]` claim is true. It proves every mutable field
    // has an owner who wrote down an answer — which is what turns Architecture.md §2 from a table of four names into
    // "config is immutable; every mutable field declares its discipline". A field whose declaration is wrong is now a
    // one-line diff to argue about in review, instead of silence.
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class MainThreadAttribute : Attribute
    {
        public readonly string Owner;
        public MainThreadAttribute(string owner) { Owner = owner; }
    }

    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class LockedAttribute : Attribute
    {
        public readonly string Reason;
        public LockedAttribute(string reason) { Reason = reason; }
    }

    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class ConcurrentAttribute : Attribute
    {
        public readonly string Reason;
        public ConcurrentAttribute(string reason) { Reason = reason; }
    }

    internal static class ThreadDiscipline
    {
        // A "mutable field" for the rule: an instance field whose type is a System.Collections container. Deliberately
        // namespace-based rather than "has a Clear()" — net471's ConcurrentQueue<T> has no Clear(), and it is exactly
        // the field (ModelEntry.fireGuidQueue) that the off-thread hooks DO touch, so a Clear()-based rule would have
        // skipped the one field that most needs a declaration.
        internal static bool IsMutableCollection(Type t) =>
            t != null && t != typeof(string) && t.Namespace != null && t.Namespace.StartsWith("System.Collections", StringComparison.Ordinal);

        internal static IEnumerable<FieldInfo> MutableFields(Type owner) =>
            owner.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 .Where(f => IsMutableCollection(f.FieldType));

        internal static string Describe(FieldInfo f) => (f.DeclaringType?.Name ?? "?") + "." + f.Name;

        internal static bool IsConcurrentType(Type t) =>
            t != null && t.Namespace == "System.Collections.Concurrent";
    }
}
