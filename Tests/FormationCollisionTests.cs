using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HumankindAssetFramework;
using UnityEngine;
using Xunit;
using Entry = HumankindAssetFramework.FormationOverride.Entry;
using Dummy = HumankindAssetFramework.FormationOverride.Dummy;
using Cell = HumankindAssetFramework.FormationOverride.Cell;

namespace HumankindAssetFramework.Tests
{
    // TWO LINKS, ONE FORMATION NAME — the 2026-08-23 review finding.
    //
    // Formation data lives in the database under a NAME, so every link naming the same formation resolves to the
    // same object and the LAST write wins for all of them — and nothing checked whether two links agreed.
    // `created` only remembered formations HAF INJECTED, never ones it OVERWROTE, so each link re-did the write
    // and re-emitted the same warning, whose text blamed a VANILLA collision for what would really be a collision
    // in the author's own registry. These pin the detection, and pin that the benign case stays quiet.
    public class FormationCollisionTests
    {
        public FormationCollisionTests()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
        }

        // A minimal valid link: n dummies at the given positions, each with 6 orientation coords, one row of n.
        static Entry E(string unit, string formation, params Vector3[] positions)
        {
            var e = new Entry { unit = unit, formation = formation };
            for (int i = 0; i < positions.Length; i++)
            {
                var d = new Dummy { pos = positions[i] };
                for (int o = 0; o < 6; o++) d.coords.Add(new Cell { x = 0, y = i });
                e.dummies.Add(d);
            }
            for (int o = 0; o < 6; o++) e.columns[o] = new[] { positions.Length };
            return e;
        }

        static List<string> Capture(Action a, LogLevel level)
        {
            var got = new List<string>();
            EventHandler<LogEventArgs> h = (s, ev) => { if ((ev.Level & level) != 0) got.Add(ev.Data?.ToString() ?? ""); };
            Plugin.Log.LogEvent += h;
            try { a(); } finally { Plugin.Log.LogEvent -= h; }
            return got;
        }

        // ---- the signature: what actually distinguishes two writes to one name ----

        [Fact]
        public void Signature_IsEqualForIdenticalData()
        {
            Assert.Equal(FormationOverride.FormationSignature(E("a", "F", Vector3.zero)),
                         FormationOverride.FormationSignature(E("b", "F", Vector3.zero)));
        }

        [Fact]
        public void Signature_DiffersOnDummyCount()
        {
            Assert.NotEqual(FormationOverride.FormationSignature(E("a", "F", Vector3.zero)),
                            FormationOverride.FormationSignature(E("b", "F", Vector3.zero, Vector3.one)));
        }

        [Fact]
        public void Signature_DiffersOnPosition()
        {
            Assert.NotEqual(FormationOverride.FormationSignature(E("a", "F", Vector3.zero)),
                            FormationOverride.FormationSignature(E("b", "F", new Vector3(1f, 0f, 0f))));
        }

        // layoutScale MULTIPLIES the positions at injection, so two links with the same authored dummies but a
        // different layout scale produce genuinely different formations. The signature has to see through that or
        // the conflict reads as benign.
        [Fact]
        public void Signature_DiffersOnLayoutScale()
        {
            var a = E("a", "F", new Vector3(1f, 0f, 0f));
            var b = E("b", "F", new Vector3(1f, 0f, 0f)); b.layoutScale = 2f;
            Assert.NotEqual(FormationOverride.FormationSignature(a), FormationOverride.FormationSignature(b));
        }

        // ...and through `scale`, which layoutScale falls back to (same rule as FillFormationFields).
        [Fact]
        public void Signature_DiffersOnScaleFallback()
        {
            var a = E("a", "F", new Vector3(1f, 0f, 0f));
            var b = E("b", "F", new Vector3(1f, 0f, 0f)); b.scale = 0.5f;
            Assert.NotEqual(FormationOverride.FormationSignature(a), FormationOverride.FormationSignature(b));
        }

        [Fact]
        public void Signature_DiffersOnLowSpec()
        {
            var a = E("a", "F", Vector3.zero);
            var b = E("b", "F", Vector3.zero); b.lowSpec = "Formation_Wedge_3";
            Assert.NotEqual(FormationOverride.FormationSignature(a), FormationOverride.FormationSignature(b));
        }

        // ---- the detection ----

        // TWO LINKS, IDENTICAL DATA: both write Formation_1, one dummy at the origin each. Harmless —
        // and it must stay QUIET, or the error means nothing the first time it fires for real.
        [Fact]
        public void IdenticalWritesToOneName_ProduceNoError()
        {
            var errors = Capture(() => FormationOverride.ReportFormationCollisions(new List<Entry>
            {
                E("UnitA", "Formation_1", Vector3.zero),
                E("UnitB",  "Formation_1", Vector3.zero),
            }), LogLevel.Error);
            Assert.Empty(errors);
        }

        // THE CASE THAT SILENTLY BREAKS A UNIT: same name, different layout. The last write applies to BOTH.
        [Fact]
        public void DifferingWritesToOneName_AreAnError_NamingBothLinks()
        {
            var errors = Capture(() => FormationOverride.ReportFormationCollisions(new List<Entry>
            {
                E("UnitA", "Formation_1", Vector3.zero),
                E("UnitB",  "Formation_1", Vector3.zero, Vector3.one),
            }), LogLevel.Error);

            var msg = Assert.Single(errors);
            Assert.Contains("UnitA", msg);
            Assert.Contains("UnitB", msg);
            Assert.Contains("Formation_1", msg);
        }

        // A macro link (no unit) is named as such rather than as an empty string.
        [Fact]
        public void MacroLinkIsNamedInTheConflict()
        {
            var errors = Capture(() => FormationOverride.ReportFormationCollisions(new List<Entry>
            {
                E("", "Formation_Close_9", Vector3.zero),
                E("UnitB", "Formation_Close_9", Vector3.zero, Vector3.one),
            }), LogLevel.Error);
            Assert.Contains("(macro)", Assert.Single(errors));
        }

        // A PURE REPOINT writes nothing, so it can never collide — flagging one would fire on any link that
        // legitimately shares a formation name with the links that DO write it.
        [Fact]
        public void PureRepoint_NeverCollides()
        {
            var errors = Capture(() => FormationOverride.ReportFormationCollisions(new List<Entry>
            {
                E("UnitA", "Formation_1", Vector3.zero),
                new Entry { unit = "UnitC", formation = "Formation_1" },   // no dummy data
            }), LogLevel.Error);
            Assert.Empty(errors);
        }

        [Fact]
        public void DistinctNames_DoNotCollide()
        {
            var errors = Capture(() => FormationOverride.ReportFormationCollisions(new List<Entry>
            {
                E("a", "Formation_1", Vector3.zero),
                E("b", "Formation_Wedge_3", Vector3.zero, Vector3.one, Vector3.up),
            }), LogLevel.Error);
            Assert.Empty(errors);
        }

        // Three links on one name with one odd man out: the conflict is still reported.
        [Fact]
        public void ThreeLinks_OneDiffering_IsReported()
        {
            var errors = Capture(() => FormationOverride.ReportFormationCollisions(new List<Entry>
            {
                E("a", "F", Vector3.zero),
                E("b", "F", Vector3.zero),
                E("c", "F", Vector3.one),
            }), LogLevel.Error);
            Assert.Single(errors);
        }
    }
}
