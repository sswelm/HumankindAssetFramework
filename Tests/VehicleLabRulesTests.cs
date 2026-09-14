using System.Collections.Generic;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The Vehicle Lab's stdout contract with vehicle_rig.py (editor/EditorRules.cs: VehicleLabRules). Two silent
    // failures before 2026-09-14: a '|' in a part name shifted every field and the row vanished; the parser
    // hard-capped at 8 fields, so the NEXT field added to the script would have emptied the Lab with no log.
    public class VehicleLabRulesTests
    {
        static VehicleLabRules.PartRow Parse(string line)
        {
            Assert.True(VehicleLabRules.TryParsePartLine(line, out var row, out string why), why);
            return row;
        }

        [Fact]
        public void Five_field_RIGBONE_and_the_growing_PART_shapes_all_parse()
        {
            var rb = Parse("RIGBONE|Bone_L|40|1,2,3|4,5,6");
            Assert.Equal("RIGBONE", rb.Kind); Assert.Equal("Bone_L", rb.Name); Assert.Equal(40, rb.Verts);
            Assert.Equal(new[] { 1f, 2f, 3f }, rb.Center); Assert.Equal(new[] { 4f, 5f, 6f }, rb.Size);
            Assert.Equal(-1, rb.Vis); Assert.Equal("", rb.Bone); Assert.Equal(-1, rb.Flip);

            var p5 = Parse("PART|Deck|120|1,2,3|4,5,6");
            Assert.Equal(-1, p5.Vis);
            var p6 = Parse("PART|Deck|120|1,2,3|4,5,6|1");
            Assert.Equal(1, p6.Vis);
            var p7 = Parse("PART|Deck|120|1,2,3|4,5,6|0|Hull_bone ");
            Assert.Equal(0, p7.Vis); Assert.Equal("Hull_bone", p7.Bone);
            var p8 = Parse("PART|Deck|120|1,2,3|4,5,6|1|Hull_bone|1");
            Assert.Equal(1, p8.Flip);
        }

        [Fact]
        public void A_pipe_inside_the_part_name_folds_back_into_the_name()
        {
            var p = Parse("PART|Deck|Aft|120|1,2,3|4,5,6|1|Hull_bone|0");   // 9 tokens: the name is "Deck|Aft"
            Assert.Equal("Deck|Aft", p.Name);
            Assert.Equal(120, p.Verts); Assert.Equal(1, p.Vis); Assert.Equal("Hull_bone", p.Bone); Assert.Equal(0, p.Flip);

            var rb = Parse("RIGBONE|L|R|40|1,2,3|4,5,6");
            Assert.Equal("L|R", rb.Name); Assert.Equal(40, rb.Verts);
        }

        [Fact]
        public void A_ninth_genuine_field_is_rejected_loudly_not_swallowed()
        {
            // the script grows a column: the fold puts "Deck|120" in the name and "1,2,3" where verts should be
            Assert.False(VehicleLabRules.TryParsePartLine("PART|Deck|120|1,2,3|4,5,6|1|Hull_bone|0|7", out _, out string why));
            Assert.NotNull(why);
            Assert.Contains("verts", why);
            Assert.Contains("new column", why);
        }

        [Fact]
        public void Malformed_rows_are_rejected_with_a_reason_and_non_rows_are_skipped_silently()
        {
            Assert.False(VehicleLabRules.TryParsePartLine("PART|Deck|120|1,2,3", out _, out string tooShort));
            Assert.Contains("expected 5..8", tooShort);
            Assert.False(VehicleLabRules.TryParsePartLine("PART|Deck|120|1,2|4,5,6", out _, out string badCentre));
            Assert.Contains("centre", badCentre);
            Assert.False(VehicleLabRules.TryParsePartLine("PART|Deck|x|1,2,3|4,5,6", out _, out string badVerts));
            Assert.Contains("verts", badVerts);

            Assert.False(VehicleLabRules.TryParsePartLine("VEHICLE timing: reduce 3.1s", out _, out string notARow));
            Assert.Null(notARow);
            Assert.False(VehicleLabRules.TryParsePartLine("", out _, out string blank));
            Assert.Null(blank);
        }

        [Fact]
        public void Nan_in_a_coordinate_becomes_zero_instead_of_killing_the_row()
        {
            var p = Parse("PART|Shard|3|nan,1.5,2|nan,nan,nan");
            Assert.Equal(0f, p.Center[0]); Assert.Equal(1.5f, p.Center[1]);
            Assert.Equal(new[] { 0f, 0f, 0f }, p.Size);
        }

        // ---- FlatShare: exact name first, then digits-only ".NNN" aliases, else unmeasurable ----

        static readonly Dictionary<string, double> Area = new Dictionary<string, double>
        {
            ["Hull"] = 10, ["Hull.001"] = 10, ["Hull.002"] = 30, ["Hull.abc"] = 100, ["Hullx.001"] = 100, ["Deck.001"] = 4, ["Deck.002"] = 4,
        };
        static readonly Dictionary<string, double> Level = new Dictionary<string, double>
        {
            ["Hull"] = 5, ["Hull.001"] = 10, ["Hull.002"] = 0, ["Hull.abc"] = 100, ["Hullx.001"] = 100, ["Deck.001"] = 4, ["Deck.002"] = 2,
        };

        [Fact]
        public void Exact_name_wins_over_its_aliases()
            => Assert.Equal(0.5f, VehicleLabRules.FlatShare("Hull", Area, Level));   // 5/10, NOT (5+10+0)/(10+10+30)

        [Fact]
        public void Digits_only_aliases_merge_when_the_exact_name_is_absent()
            => Assert.Equal(0.75f, VehicleLabRules.FlatShare("Deck", Area, Level));   // (4+2)/(4+4)

        [Fact]
        public void Non_digit_suffixes_and_longer_stems_do_not_merge()
        {
            Assert.Equal(-1f, VehicleLabRules.FlatShare("Hul", Area, Level));        // "Hull" is not "Hul.<digits>": the '.' must follow the whole stem
            Assert.Equal(1f, VehicleLabRules.FlatShare("Hullx", Area, Level));       // "Hullx.001" belongs to "Hullx" (100/100) — and never leaked into "Hull" above
            var onlyAbc = new Dictionary<string, double> { ["Hull.abc"] = 100 };
            Assert.Equal(-1f, VehicleLabRules.FlatShare("Hull", onlyAbc, onlyAbc));   // ".abc" never merges (review P2: the old strip ate it)
        }

        [Fact]
        public void Unknown_or_unmeasured_parts_are_minus_one_so_the_filter_passes_them()
        {
            Assert.Equal(-1f, VehicleLabRules.FlatShare("Mast", Area, Level));
            Assert.Equal(-1f, VehicleLabRules.FlatShare("Hull", null, null));
            var zeroArea = new Dictionary<string, double> { ["Hull"] = 0 };
            Assert.Equal(-1f, VehicleLabRules.FlatShare("Hull", zeroArea, zeroArea));
        }
    }
}
