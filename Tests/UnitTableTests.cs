using System;
using System.Collections.Generic;
using System.Linq;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The F8 window's unit table (2026-11-19). The rows are read from the running game; what a row SAYS about an entry
    // is pure and held here. The rule under most of these: a value that was not read is an empty cell, never a 0.
    public class UnitTableTests
    {
        static ModelEntry Baked(string name, bool clip = false)
        {
            var e = new ModelEntry { resourceName = name, sa = 1 };
            if (clip) e.Roles[0].Set(1, 2, 3, 4);
            return e;
        }
        static KeyValuePair<string, int> Mesh(string n, int quads) => new KeyValuePair<string, int>(n, quads);

        [Theory]
        [InlineData(500, 250)]
        [InlineData(1000, 700)]
        public void Window_layout_retains_a_callback_resize_and_the_title_bars_returned_position(float width, float height)
        {
            var drawn = new UnityEngine.Rect(40, 50, 600, 350);
            var requested = new UnityEngine.Rect(10, 20, width, height);
            var result = UnitTableRules.WindowResult(drawn, requested);
            Assert.Equal((40f, 50f, width, height), (result.x, result.y, result.width, result.height));
        }

        [Fact]
        public void The_type_follows_what_the_entry_owns()
        {
            Assert.Equal("not baked", UnitTableRules.TypeOf(new ModelEntry()));
            Assert.Equal("retexture", UnitTableRules.TypeOf(new ModelEntry { textureFile = "skin.png" }));
            Assert.Equal("static", UnitTableRules.TypeOf(Baked("a")));
            Assert.Equal("animated", UnitTableRules.TypeOf(Baked("a", clip: true)));
            var sd = Baked("a", clip: true); sd.animStateDriven = true;
            Assert.Equal("state-driven", UnitTableRules.TypeOf(sd));
        }

        [Theory]
        [InlineData("desaturate", 0.5f)]
        [InlineData("brightness", 1.5f)]
        [InlineData("tintR", 0.2f)]
        [InlineData("tintG", -0.2f)]
        [InlineData("tintB", 0.2f)]
        public void An_adjustment_only_retexture_does_not_need_a_png_or_a_baked_skeleton(string field, float value)
        {
            var e = new ModelEntry();
            typeof(ModelEntry).GetField(field).SetValue(e, value);
            Assert.Equal("retexture", UnitTableRules.TypeOf(e));
            Assert.Equal("waiting", UnitTableRules.StatusOf(e, skeletonLoaded: false));
            e.repointed = true;
            Assert.Equal("in use", UnitTableRules.StatusOf(e, skeletonLoaded: false));
            e.sa = 1;
            Assert.Equal("static", UnitTableRules.TypeOf(e));
        }

        [Fact]
        public void The_status_names_the_strongest_fact()
        {
            var e = Baked("a");
            Assert.Equal("not loaded", UnitTableRules.StatusOf(e, skeletonLoaded: false));
            Assert.Equal("loaded", UnitTableRules.StatusOf(e, skeletonLoaded: true));
            e.repointed = true;
            Assert.Equal("in use", UnitTableRules.StatusOf(e, skeletonLoaded: true));
            e.disabled = true;
            Assert.Equal("disabled", UnitTableRules.StatusOf(e, skeletonLoaded: true));
            Assert.Equal("waiting", UnitTableRules.StatusOf(new ModelEntry { textureFile = "skin.png" }, skeletonLoaded: false));
        }

        [Fact]
        public void Default_settings_say_nothing_and_the_others_come_in_a_fixed_order()
        {
            Assert.Equal("", UnitTableRules.PropertiesOf(Baked("a")));
            var e = Baked("a");
            e.soundFile = "engine.wav"; e.scale = 1.25f; e.turretBone = "Turret"; e.deployOnStop = true; e.textureFile = "own.png";
            Assert.Equal("scale 1.25, turret, deploys, sound, own skin", UnitTableRules.PropertiesOf(e));
            Assert.Equal("", UnitTableRules.PropertiesOf(new ModelEntry { textureFile = "skin.png" }));   // a retexture IS its skin
        }

        [Fact]
        public void The_bake_date_is_shown_in_local_time_and_no_date_is_an_empty_cell()
        {
            var plusTwo = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(2), "t", "t");
            var t = UnitTableRules.ParseBaked("2026-11-19T23:30:00Z");
            Assert.Equal(new DateTime(2026, 11, 19, 23, 30, 0, DateTimeKind.Utc), t);
            Assert.Equal("2026-11-20 01:30", UnitTableRules.BakedText(t, plusTwo));
            foreach (string none in new[] { "", "  ", null, "yesterday" })
            {
                Assert.Equal(DateTime.MinValue, UnitTableRules.ParseBaked(none));
                Assert.Equal("", UnitTableRules.BakedText(UnitTableRules.ParseBaked(none), plusTwo));
            }
        }

        [Fact]
        public void Mesh_sums_stay_unread_when_one_mesh_gave_no_number()
        {
            var r = new UnitRow();
            UnitTableRules.AddMeshes(r, new[] { Mesh("a", 100), Mesh("b", 16316) }, new[] { 300, 27084 });
            Assert.Equal((2, 16416, 16316, "b", 27384), (r.Meshes, r.Quads, r.MaxQuads, r.MaxQuadMesh, r.Verts));
            Assert.Equal("16,316 (99%)", UnitTableRules.LargestText(r, 16320));

            var bad = new UnitRow();
            UnitTableRules.AddMeshes(bad, new[] { Mesh("a", 100), Mesh("b", 0) }, new[] { 300, 0 });
            Assert.Equal((2, -1, -1, -1), (bad.Meshes, bad.Quads, bad.MaxQuads, bad.Verts));
            Assert.Equal("", UnitTableRules.LargestText(bad, 16320));
            Assert.Equal("", UnitTableRules.Num(bad.Quads));
            Assert.Equal("", UnitTableRules.AtlasText(bad));
        }

        [Theory]
        [InlineData(-1, 0)][InlineData(14687, 0)][InlineData(14688, 1)][InlineData(16320, 1)][InlineData(16321, 2)]
        public void The_alarm_starts_at_ninety_percent_and_turns_red_past_the_ceiling(int largest, int alarm)
            => Assert.Equal(alarm, UnitTableRules.QuadAlarm(new UnitRow { MaxQuads = largest }, 16320));

        [Fact]
        public void A_number_column_sorts_by_value_with_unread_rows_last_in_both_directions()
        {
            var rows = new[] {
                new UnitRow { Name = "b", Quads = 5 }, new UnitRow { Name = "unread" }, new UnitRow { Name = "a", Quads = 5 }, new UnitRow { Name = "c", Quads = 900 } };
            Assert.Equal(new[] { "c", "a", "b", "unread" }, UnitTableRules.Sorted(rows, 5, descending: true).Select(r => r.Name));
            Assert.Equal(new[] { "a", "b", "c", "unread" }, UnitTableRules.Sorted(rows, 5, descending: false).Select(r => r.Name));
            Assert.Equal(new[] { "a", "b", "c", "unread" }, UnitTableRules.Sorted(rows, 0, descending: false).Select(r => r.Name));
        }

        [Fact]
        public void The_bake_date_column_sorts_on_the_time_not_on_the_text()
        {
            var rows = new[] {
                new UnitRow { Name = "old", BakedUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) },
                new UnitRow { Name = "none" },
                new UnitRow { Name = "new", BakedUtc = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc) } };
            Assert.Equal(new[] { "new", "old", "none" }, UnitTableRules.Sorted(rows, 11, descending: true).Select(r => r.Name));
        }

        [Fact]
        public void Every_column_has_a_cell()
        {
            var r = new UnitRow { Name = "n", Status = "loaded", Type = "static", Meshes = 1, MaxQuads = 10, Quads = 10, Verts = 40, Bones = 3, AtlasW = 2048, AtlasH = 1024, Clips = 0, Properties = "turret", Baked = "2026-11-19 00:02" };
            var cells = Enumerable.Range(0, UnitTableRules.Columns.Length).Select(c => UnitTableRules.Cell(r, c, 16320)).ToArray();
            Assert.Equal(new[] { "n", "loaded", "static", "1", "10 (0%)", "10", "40", "3", "2048 x 1024", "0", "turret", "2026-11-19 00:02" }, cells);
        }
    }
}
