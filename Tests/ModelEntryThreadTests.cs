using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // "Which of ModelEntry's fields need locking?" used to be answered by a four-name table in Architecture.md §2 that
    // a maintainer had to memorise — and by a comment on 6 of its 23 mutable collections. This is that question as a
    // test: every mutable field declares its discipline, or the build fails.
    public class ModelEntryThreadTests
    {
        static IEnumerable<FieldInfo> Fields => ThreadDiscipline.MutableFields(typeof(ModelEntry));

        [Fact]
        public void Every_mutable_field_on_ModelEntry_declares_its_thread_discipline()
        {
            var undeclared = new List<string>();
            foreach (var f in Fields)
            {
                int n = (f.GetCustomAttribute<MainThreadAttribute>() != null ? 1 : 0)
                      + (f.GetCustomAttribute<LockedAttribute>() != null ? 1 : 0)
                      + (f.GetCustomAttribute<ConcurrentAttribute>() != null ? 1 : 0);
                if (n != 1) undeclared.Add(ThreadDiscipline.Describe(f) + (n == 0 ? " (none)" : " (more than one)"));
            }
            Assert.True(undeclared.Count == 0,
                "Mutable fields on ModelEntry with no single declared thread discipline — add [MainThread(\"owner\")], " +
                "[Locked(\"why\")] or [Concurrent(\"why\")]:\n  " + string.Join("\n  ", undeclared));
        }

        // [Concurrent] is the one claim a machine can check outright: the field's type must really be a concurrent one.
        [Fact]
        public void A_Concurrent_declaration_must_be_backed_by_a_concurrent_type()
        {
            var liars = Fields.Where(f => f.GetCustomAttribute<ConcurrentAttribute>() != null
                                       && !ThreadDiscipline.IsConcurrentType(f.FieldType))
                              .Select(f => ThreadDiscipline.Describe(f) + " is " + f.FieldType.Name)
                              .ToList();
            Assert.True(liars.Count == 0, "[Concurrent] on a type that is not in System.Collections.Concurrent:\n  " + string.Join("\n  ", liars));
            // …and the converse: a genuinely concurrent field must not be filed as plain main-thread state.
            var mislabelled = Fields.Where(f => ThreadDiscipline.IsConcurrentType(f.FieldType)
                                             && f.GetCustomAttribute<ConcurrentAttribute>() == null)
                                    .Select(ThreadDiscipline.Describe).ToList();
            Assert.True(mislabelled.Count == 0, "concurrent-typed field(s) not declared [Concurrent]:\n  " + string.Join("\n  ", mislabelled));
        }

        [Fact]
        public void Declared_reasons_are_never_empty()
        {
            var blank = new List<string>();
            foreach (var f in Fields)
            {
                var m = f.GetCustomAttribute<MainThreadAttribute>(); if (m != null && string.IsNullOrWhiteSpace(m.Owner)) blank.Add(ThreadDiscipline.Describe(f));
                var l = f.GetCustomAttribute<LockedAttribute>();     if (l != null && string.IsNullOrWhiteSpace(l.Reason)) blank.Add(ThreadDiscipline.Describe(f));
                var c = f.GetCustomAttribute<ConcurrentAttribute>(); if (c != null && string.IsNullOrWhiteSpace(c.Reason)) blank.Add(ThreadDiscipline.Describe(f));
            }
            Assert.Empty(blank);
        }

        // The four Architecture.md §2 names, pinned: demoting one to [MainThread] is now a failing test, not a silent
        // edit. fireGuidQueue is pinned too — it is the ONE ModelEntry field the off-thread hooks touch (§2's table).
        [Fact]
        public void The_known_shared_fields_keep_their_discipline()
        {
            foreach (var name in new[] { "stateSamples", "activeFires", "deploySamples", "phaseTracks" })
            {
                var f = typeof(ModelEntry).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(f != null, name + " is gone from ModelEntry — if that is deliberate, update Architecture.md §2 and this test.");
                Assert.True(f.GetCustomAttribute<LockedAttribute>() != null, name + " must stay [Locked] (Architecture.md §2)");
            }
            var q = typeof(ModelEntry).GetField("fireGuidQueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(q);
            Assert.NotNull(q.GetCustomAttribute<ConcurrentAttribute>());
        }

        [Fact]
        public void The_rule_covers_the_whole_mutable_surface_not_a_sample()
        {
            Assert.True(Fields.Count() >= 20, "ModelEntry's mutable-field count collapsed (" + Fields.Count() + ") — did the detector break?");
            // config is the inherited half: the shared schema must contribute NO mutable collections, or "config is
            // immutable" stops being true by construction (see docs/Shared-Schema.md).
            var schemaMutable = ThreadDiscipline.MutableFields(typeof(ModelEntry))
                                                .Where(f => f.DeclaringType != typeof(ModelEntry))
                                                .Select(ThreadDiscipline.Describe).ToList();
            Assert.True(schemaMutable.Count == 0,
                "the shared schema half gained mutable collection(s) — config must stay immutable:\n  " + string.Join("\n  ", schemaMutable));
        }
    }
}
