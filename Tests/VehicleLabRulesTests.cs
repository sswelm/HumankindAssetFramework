using System.Linq;
using System.Collections.Generic;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The Vehicle Lab's stdout contract with vehicle_rig.py (editor/EditorRules.cs: VehicleLabRules). Two silent
    // failures before 2026-09-14: a '|' in a part name shifted every field and the row vanished; the parser
    // hard-capped at 8 fields, so the NEXT field added to the script would have emptied the Lab with no log.
    public class VehicleLabRulesTests
    {
        [Fact]
        public void A_recipe_is_listed_under_the_first_folder_below_what_all_sources_share()
        {
            var got = VehicleLabRules.SourceFolders(new[] {
                "D:/3DModels/Bradley/FBX/Bredley_Low.fbx", "D:/3DModels/Rah66/rah66.glb", "d:\\3dmodels\\SS Romanic\\a.glb", "", null });
            Assert.Equal(new[] { "Bradley", "Rah66", "SS Romanic", "", "" }, got);
        }

        [Fact]
        public void A_lone_source_or_one_in_the_shared_folder_gets_its_own_folder_name()
        {
            Assert.Equal(new[] { "FBX" }, VehicleLabRules.SourceFolders(new[] { "D:/3DModels/Bradley/FBX/Bredley_Low.fbx" }));
            Assert.Equal(new[] { "3DModels", "Rah66" }, VehicleLabRules.SourceFolders(new[] { "D:/3DModels/loose.glb", "D:/3DModels/Rah66/rah66.glb" }));
            Assert.Equal(new[] { "" }, VehicleLabRules.SourceFolders(new[] { "bare.glb" }));
        }

        [Theory]
        [InlineData(false, false, false, true, false, true)]
        [InlineData(false, false, false, false, false, false)]
        [InlineData(false, true, false, false, false, true)]
        [InlineData(false, false, true, false, false, true)]
        [InlineData(false, false, false, false, true, true)]
        [InlineData(true, true, true, true, true, false)]
        public void Geometry_only_generation_includes_Two_sided_and_excludes_the_fast_path(
            bool fast, bool facing, bool flip, bool twoSided, bool reduce, bool expected)
            => Assert.Equal(expected, VehicleLabRules.HasGeometryWork(fast, facing, flip, twoSided, reduce));

        [Theory]
        [InlineData(false, false, false)][InlineData(false, true, false)]
        [InlineData(true, false, false)][InlineData(true, true, true)]
        public void A_Two_sided_fast_path_job_is_refused_instead_of_reporting_a_successful_no_op(bool fast, bool marked, bool refused)
            => Assert.Equal(refused, VehicleLabRules.TwoSidedFastPathRefusal(fast, marked) != null);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string link, string existing, System.IntPtr reserved);

        [Fact]
        public void Generate_refuses_filesystem_aliases_and_allows_distinct_files()
        {
            if (System.Environment.OSVersion.Platform != System.PlatformID.Win32NT) return;
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "haf-source-guard-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string source = System.IO.Path.Combine(dir, "source.glb"), alias = System.IO.Path.Combine(dir, "alias.glb"), other = System.IO.Path.Combine(dir, "other.glb");
            try
            {
                System.IO.File.WriteAllText(source, "SOURCE"); System.IO.File.WriteAllText(other, "OTHER");
                Assert.True(CreateHardLink(alias, source, System.IntPtr.Zero));
                Assert.Contains("raw model through a filesystem alias", VehicleLabSourceGuard.Refusal(source, "", alias));
                Assert.Contains("second model through a filesystem alias", VehicleLabSourceGuard.Refusal(other, source, alias));
                Assert.Null(VehicleLabSourceGuard.Refusal(source, "", other));
                var args = VehicleRigArgLine.RigArgv(new VehicleRigArgs { srcFile = source, outGlb = alias, frames = 24,
                    namesFiles = VehicleRigArgLine.NamesRoles.ToDictionary(role => role, role => "") }).Select(x => x.value).ToArray();
                var result = BlenderVehicleRigInProcess.Run(args);
                Assert.False(result.Written); Assert.Null(result.UseBlender);
                Assert.Contains("filesystem alias", result.Error);
                Assert.Equal("SOURCE", System.IO.File.ReadAllText(source));
            }
            finally
            {
                foreach (string file in new[] { alias, source, other }) if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
                System.IO.Directory.Delete(dir);
            }
        }
        // ---- per-part placement lines (2026-09-20): name|ox,oy,oz|sx,sy,sz, split from the RIGHT on both sides ----
        [Fact]
    public void A_cut_part_finds_its_parents_nearest_first()
    {
        Assert.Equal(new[] { "Material2_3" }, VehicleLabRules.ParentNames("Material2_3_Part_001").ToArray());
        Assert.Equal(new[] { "Hull" }, VehicleLabRules.ParentNames("Hull_CutA").ToArray());
        Assert.Equal(new[] { "Hull_CutB", "Hull" }, VehicleLabRules.ParentNames("Hull_CutB_Part_002").ToArray());
        Assert.Empty(VehicleLabRules.ParentNames("Material2"));     // no tail to peel: "2" is part of the name
        Assert.Empty(VehicleLabRules.ParentNames("Hull_2"));      // a bare number is no ancestry: Hull and Hull_2 may be two parts (review of PR #85, third round)
        Assert.Empty(VehicleLabRules.ParentNames(""));
    }

    [Fact]
    public void A_split_child_past_999_still_finds_its_parent()
    {
        // the Split's D3 numbering grows a fourth digit past 999 (the Sirius bow: 4,471 islands at no merge)
        Assert.Equal(new[] { "Bow" }, VehicleLabRules.ParentNames("Bow_Part_1000").ToArray());
        Assert.Equal(new[] { "Bow" }, VehicleLabRules.ParentNames("Bow_Part_4471").ToArray());
        Assert.Equal(new[] { "Bow_CutA", "Bow" }, VehicleLabRules.ParentNames("Bow_CutA_Part_1234").ToArray());
        Assert.Empty(VehicleLabRules.ParentNames("Bow_Part_12"));   // fewer than three digits was never a Split name
    }

    [Fact]
        public void Placement_line_round_trips_and_keeps_a_pipe_in_the_name()
        {
            string line = VehicleLabRules.PartPlacementLine("Fused_F|boat", 0f, -1.25f, 0.5f, 1f, 1f, 0.75f);
            Assert.Equal("Fused_F|boat|0,-1.25,0.5|1,1,0.75", line);
            Assert.True(VehicleLabRules.TryParsePartPlacementLine(line, out string name, out float[] off, out float[] scl));
            Assert.Equal("Fused_F|boat", name);
            Assert.Equal(new[] { 0f, -1.25f, 0.5f }, off);
            Assert.Equal(new[] { 1f, 1f, 0.75f }, scl);
        }

        [Fact]
        public void Placement_line_is_invariant_culture_and_rejects_a_broken_triple()
        {
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal("W|0.5,0,0|1,1,1", VehicleLabRules.PartPlacementLine("W", 0.5f, 0f, 0f, 1f, 1f, 1f));   // never "0,5"
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
            Assert.False(VehicleLabRules.TryParsePartPlacementLine("W|0.5,0|1,1,1", out _, out _, out _));    // a pair is not a triple
            Assert.False(VehicleLabRules.TryParsePartPlacementLine("no separators here", out _, out _, out _));
            Assert.False(VehicleLabRules.TryParsePartPlacementLine("|0,0,0|1,1,1", out _, out _, out _));      // empty name
        }

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
        // ---- second-model scale, per axis (2026-09-21; user: "what I meant by scale is Scale X, Scale Y, Scale Z") ----
        [Fact]
        public void Merge2_scale_field_carries_three_axes_invariantly()
        {
            Assert.Equal("0.5,2,1.25", VehicleLabRules.Merge2ScaleField(0.5f, 2f, 1.25f));
            // the unit-factor case "0.###" once rounded to a literal 0
            Assert.Equal("0.0004,0.0004,0.0004", VehicleLabRules.Merge2ScaleField(0.0004f, 0.0004f, 0.0004f));
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nl-NL");
                Assert.Equal("0.5,1.5,2.5", VehicleLabRules.Merge2ScaleField(0.5f, 1.5f, 2.5f));   // never "0,5" — the comma is the separator
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
        }

        [Fact]
        public void Merge2_scale_field_refuses_collapse_and_mirror_per_component()
        {
            // zero flattens the model, a negative component mirrors it (reversed winding, baked inside out);
            // only the bad component falls back, the others keep what was typed
            Assert.Equal("1,1,3", VehicleLabRules.Merge2ScaleField(0f, -2f, 3f));
            Assert.Equal("1,1,1", VehicleLabRules.Merge2ScaleField(float.NaN, float.PositiveInfinity, float.NegativeInfinity));
        }

        [Fact]
        public void An_old_recipe_keeps_its_uniform_scale_on_all_three_axes()
        {
            // written before the change: legacy 0.01, no per-axis key -> the DTO initializer (1,1,1)
            Assert.Equal(new[] { 0.01f, 0.01f, 0.01f }, VehicleLabRules.Model2ScaleOnLoad(0.01f, 1f, 1f, 1f));
        }

        [Fact]
        public void A_new_recipe_is_simply_its_per_axis_value()
        {
            // written after the change: legacy is always saved as 1
            Assert.Equal(new[] { 0.5f, 2f, 1.25f }, VehicleLabRules.Model2ScaleOnLoad(1f, 0.5f, 2f, 1.25f));
        }

        [Fact]
        public void A_hand_edited_zero_in_a_recipe_never_collapses_the_model()
        {
            Assert.Equal(new[] { 1f, 1f, 3f }, VehicleLabRules.Model2ScaleOnLoad(0f, 0f, -4f, 3f));
            // Vector3 absent from a JsonUtility recipe can also arrive as (0,0,0) if the initializer is ever lost
            Assert.Equal(new[] { 0.01f, 0.01f, 0.01f }, VehicleLabRules.Model2ScaleOnLoad(0.01f, 0f, 0f, 0f));
        }

        // ---- the source guard (review 2026-09-27): Generate never writes over a source model ----
        [Fact]
        public void Generate_refuses_an_output_that_is_a_source_model()
        {
            string raw = "C:/Models/tank.glb", second = "C:/Models/turret.glb";
            Assert.Contains("raw model itself", VehicleLabRules.SourceGuardRefusal(raw, second, "C:/Models/tank.glb"));
            Assert.Contains("raw model itself", VehicleLabRules.SourceGuardRefusal(raw, "", @"c:\models\TANK.glb"));   // the same file, spelled differently
            Assert.Contains("second model itself", VehicleLabRules.SourceGuardRefusal(raw, second, "C:/Models/sub/../turret.glb"));
            Assert.Contains("preview FBX", VehicleLabRules.SourceGuardRefusal("C:/Models/Tank_preview.fbx", "", "C:/Models/Tank_preview.fbx", "preview FBX"));
        }

        [Fact]
        public void Generate_lets_a_new_output_through()
        {
            Assert.Null(VehicleLabRules.SourceGuardRefusal("C:/Models/tank.glb", "", "C:/Models/tank_rig.glb"));
            Assert.Null(VehicleLabRules.SourceGuardRefusal("C:/Models/tank.glb", null, ""));                  // no output yet: the run says so
            Assert.Null(VehicleLabRules.SourceGuardRefusal("C:/Models/tank.glb", "  ", "C:/Models/tank_rig.glb"));   // no second model
            Assert.Null(VehicleLabRules.SourceGuardRefusal("C:/Models/ta\0nk.glb", "", "C:/Models/tank_rig.glb"));  // malformed: not this guard's verdict
        }

        // THE RE-FUSED GALLEY (2026-11-25): the report's own lines, cut from khelandion_2_split_fused.glb.fuse-report.txt.
        const string GalleyReport =
            "Model Workshop fuse report\r\nsource: D:/3DModels/Khalandion/khelandion_2_split.glb\r\noutput: D:/3DModels/Khalandion/khelandion_2_split_fused.glb\r\ngroups: 15\r\n\r\n" +
            "== group O — 3 part(s)\r\nparts: Object_24, Object_25, Object_26\r\n" +
            "Fused 3 part(s) -> 'Fused_O_Object_24': 26130 -> 25644 verts (seams welded within 0 = 0‰ of 10); islands 192 -> 192\r\n" +
            "rewound by part: Object_24 0/8640; Object_25 0/11328; Object_26 0/7680\r\n  1144 faces (parts: Object_24 ×9)\r\n\r\n" +
            "== group B — 3 part(s)\r\nparts: Object_13, Object_21, Object_20_Part_003\r\n" +
            "Fused 3 part(s) -> 'Fused_B_Object_13': 295026 -> 138357 verts\r\n\r\n" +
            "== group Q — 2 part(s)\r\nparts: Object_90, Object_91\r\n" +
            "Fused 2 part(s) -> 'Fused_Q_Object_90': 10 -> 8 verts\r\n";

        [Fact]
        public void A_fuse_report_names_each_fused_part_with_its_members()
        {
            var g = VehicleLabRules.FuseReportGroups(GalleyReport);
            Assert.Equal(new[] { "Fused_B_Object_13", "Fused_O_Object_24", "Fused_Q_Object_90" }, g.Keys.OrderBy(k => k).ToArray());
            Assert.Equal(new[] { "Object_24", "Object_25", "Object_26" }, g["Fused_O_Object_24"]);
            Assert.Equal(new[] { "Object_13", "Object_21", "Object_20_Part_003" }, g["Fused_B_Object_13"]);
            Assert.Empty(VehicleLabRules.FuseReportGroups("parts: a, b\nFused 2 part(s) -> 'X'\n"));   // no group header: not a group
            Assert.Empty(VehicleLabRules.FuseReportGroups(null));
        }

        static VehicleLabRules.RoleList L(int role, int minute, params string[] names)
            => new VehicleLabRules.RoleList { Role = role, Written = new System.DateTime(2026, 11, 25, 20, minute, 0, System.DateTimeKind.Utc), Names = names };
        static readonly System.DateTime OutputWritten = new System.DateTime(2026, 11, 25, 20, 30, 15, System.DateTimeKind.Utc);

        [Fact]
        public void The_last_Generate_s_lists_name_the_roles_and_a_name_in_two_lists_decides_nothing()
        {
            var got = VehicleLabRules.LastGenerateRoles(new[] {
                L(13, 0, "Fused_O_Object_24", "", "  "),
                L(14, 0, "Fused_Z_Object_6 ", "Torn"),
                L(3, 0, "Torn", "Torn"),
                L(3, 0, "Torn"),   // a third list cannot bring a torn name back
                new VehicleLabRules.RoleList { Role = 4, Written = new System.DateTime(2026, 11, 25, 20, 0, 0, System.DateTimeKind.Utc), Names = null },
            }, 4, OutputWritten);
            Assert.Equal(2, got.Count);
            Assert.Equal(13, got["Fused_O_Object_24"]);
            Assert.Equal(14, got["Fused_Z_Object_6"]);
            Assert.False(got.ContainsKey("Torn"));
        }

        [Fact]
        public void A_Default_list_an_earlier_Generate_left_is_not_read()
        {
            // Generate writes the Default list only at a Default dial above 0: a bake at dial 0 leaves the older one in place
            var stale = VehicleLabRules.LastGenerateRoles(new[] { L(14, 30, "Fused_Z_Object_6"), L(4, 10, "Fused_Z_Object_6", "Fused_B_Object_13") }, 4, OutputWritten);
            Assert.Equal(14, stale["Fused_Z_Object_6"]);       // not torn by the old list
            Assert.False(stale.ContainsKey("Fused_B_Object_13"));
            var same = VehicleLabRules.LastGenerateRoles(new[] { L(14, 30, "Fused_Z_Object_6"), L(4, 30, "Fused_B_Object_13") }, 4, OutputWritten);
            Assert.Equal(4, same["Fused_B_Object_13"]);        // written by the same Generate: read
            var alone = VehicleLabRules.LastGenerateRoles(new[] { L(4, 10, "Fused_B_Object_13") }, 4, OutputWritten);
            Assert.Equal(4, alone["Fused_B_Object_13"]);       // nothing newer to be stale against
        }

        [Fact]
        public void A_Default_list_from_a_quick_previous_Generate_is_stale()
        {
            const string part = "Fused_A_Deck";
            var current = L(14, 30, part);
            var stale = L(4, 30, part, "Only in the old bake");
            stale.Written = current.Written.AddSeconds(-10);

            var roles = VehicleLabRules.LastGenerateRoles(new[] { current, stale }, 4, OutputWritten);

            Assert.Equal(14, roles[part]);
            Assert.False(roles.ContainsKey("Only in the old bake"));
        }

        [Fact]
        public void Lists_newer_than_the_output_cannot_supply_roles_from_the_last_successful_Generate()
        {
            const string part = "Fused_A_Deck";
            var stale = L(14, 30, part);
            var failed = L(3, 31, part);
            Assert.Empty(VehicleLabRules.LastGenerateRoles(new[] { stale, failed }, 4, OutputWritten));
            Assert.Equal(14, VehicleLabRules.LastGenerateRoles(new[] { stale }, 4, OutputWritten)[part]);
            Assert.Equal(3, VehicleLabRules.LastGenerateRoles(new[] { failed }, 4, failed.Written)[part]);
        }

        [Fact]
        public void An_explicit_Default_survives_Probe_even_if_the_parent_has_a_marked_role()
        {
            const int Default = 4, Oar = 13;
            var roles = new Dictionary<string, int> { { "Oar", Oar }, { "Oar_Part_001", Default }, { "Sail", Default } };
            Assert.Equal(Default, VehicleLabRules.ProbeSessionRole("Oar_Part_001", roles, Default));
            Assert.Equal(Oar, VehicleLabRules.ProbeSessionRole("Oar_Part_002", roles, Default));
            Assert.Equal(Default, VehicleLabRules.ProbeSessionRole("Sail", roles, Default));
            Assert.Null(VehicleLabRules.ProbeSessionRole("Sail_Part_001", roles, Default));
        }

        [Fact]
        public void A_member_name_with_a_comma_or_a_fused_name_with_a_quote_is_read_whole()
        {
            const string report = "== group A — 3 part(s)\nparts: Rope, long, Mast, Hull\nFused 3 part(s) -> 'Fused_A_Captain's oar': 10 -> 8 verts\n";
            var known = new[] { "Rope, long", "Rope", "Mast", "Hull" };
            var g = VehicleLabRules.FuseReportGroups(report, known);
            Assert.Equal(new[] { "Rope, long", "Mast", "Hull" }, g["Fused_A_Captain's oar"]);
            Assert.Empty(VehicleLabRules.FuseReportGroups(report));   // four tokens cannot stand for three members unaided
        }

        [Fact]
        public void The_report_count_keeps_two_members_separate_even_if_the_session_has_a_comma_name()
        {
            const string fused = "Fused_A_Captain': 10 years";
            string report = "== group A — 2 part(s)\nparts: Deck, Mast\nFused 2 part(s) -> '" + fused + "': 20 -> 16 verts\n";
            var session = new Dictionary<string, int> { { "Deck", 16 }, { "Mast", 16 }, { "Deck, Mast", 3 } };
            var groups = VehicleLabRules.FuseReportGroups(report, session.Keys);
            Assert.Equal(new[] { "Deck", "Mast" }, groups[fused]);
            Assert.Equal(16, Assert.Single(VehicleLabRules.RecoverFreshRoles(new[] { fused }, null, groups, session, 4, out _)).Role);
        }

        [Fact]
        public void A_member_the_session_never_listed_votes_with_the_part_it_was_cut_from()
        {
            const int Default = 4, Ignore = 3, Structure = 16;
            var groups = new Dictionary<string, string[]> { { "Fused_B_Object_12_Part_005", new[] { "Object_12_Part_005", "Object_12_Part_006", "Object_21" } } };
            var session = new Dictionary<string, int> { { "Object_12", Ignore }, { "Object_21", Structure } };
            var got = VehicleLabRules.RecoverFreshRoles(new[] { "Fused_B_Object_12_Part_005" }, null, groups, session, Default, out var undecided);
            Assert.Empty(got);   // the Ignored parent's children disagree with Structure: not decided for the user
            Assert.Equal(2, Assert.Single(undecided).MemberRoles[Ignore]);
        }

        [Fact]
        public void A_part_new_to_the_session_takes_the_last_Generate_s_role_else_its_members_one_role()
        {
            const int Default = 4, Ignore = 3, Oar = 13, Structure = 16, Rigging = 15;
            var groups = VehicleLabRules.FuseReportGroups(GalleyReport);
            var session = new Dictionary<string, int> {
                { "Object_24", Oar }, { "Object_25", Oar }, { "Object_26", Oar },
                { "Object_13", Ignore }, { "Object_21", Structure }, { "Object_20_Part_003", Structure },
                { "Object_90", Default }, { "Object_91", Default },
            };
            // no Generate yet: the oars come back from their members, the mixed hull group is named, the all-Default group decides nothing
            var got = VehicleLabRules.RecoverFreshRoles(new[] { "Fused_O_Object_24", "Fused_B_Object_13", "Fused_Q_Object_90", "Unrelated" },
                null, groups, session, Default, out var undecided);
            Assert.Single(got);
            Assert.Equal(("Fused_O_Object_24", Oar, "fuse group"), (got[0].Name, got[0].Role, got[0].From));
            Assert.Single(undecided);
            Assert.Equal("Fused_B_Object_13", undecided[0].Name);
            Assert.Equal(1, undecided[0].MemberRoles[Ignore]);
            Assert.Equal(2, undecided[0].MemberRoles[Structure]);

            // a member the session never listed does not vote: one listed Structure member decides the group
            var partial = new Dictionary<string, int> { { "Object_21", Structure } };
            var got2 = VehicleLabRules.RecoverFreshRoles(new[] { "Fused_B_Object_13" }, null, groups, partial, Default, out var und2);
            Assert.Equal(Structure, Assert.Single(got2).Role);
            Assert.Empty(und2);

            // the last Generate's record wins over the members (a made-up bake: the group baked as Rigging)
            var last = new Dictionary<string, int> { { "Fused_B_Object_13", Rigging }, { "Fused_Q_Object_90", Default } };
            var got3 = VehicleLabRules.RecoverFreshRoles(new[] { "Fused_O_Object_24", "Fused_B_Object_13", "Fused_Q_Object_90" },
                last, groups, session, Default, out var und3);
            Assert.Equal(new[] { ("Fused_O_Object_24", Oar, "fuse group"), ("Fused_B_Object_13", Rigging, "last Generate"), ("Fused_Q_Object_90", Default, "last Generate") },
                got3.Select(r => (r.Name, r.Role, r.From)).ToArray());
            Assert.Empty(und3);
        }

        static VehicleLabRules.SourceFileState Src(string label, string path, long ticks, long len, bool exists = true)
            => new VehicleLabRules.SourceFileState { Label = label, Path = path, Exists = exists, WriteTicksUtc = ticks, Length = len };

        [Fact]
        public void An_unprobed_session_or_an_unchanged_source_is_not_stale()
        {
            string stamp = VehicleLabRules.SourceStamp(new[] { Src("Raw model", "D:/3DModels/T/skarv_fused.glb", 638_999_000_000_000_000, 1876228) });
            Assert.Null(VehicleLabRules.ProbeStaleReason("", stamp));     // a loaded recipe / new model: nothing was probed
            Assert.Null(VehicleLabRules.ProbeStaleReason(null, stamp));
            Assert.Null(VehicleLabRules.ProbeStaleReason(stamp, stamp));
            // the same file spelled with the other slash and case is the same file
            string respelled = VehicleLabRules.SourceStamp(new[] { Src("Raw model", "d:\\3dmodels\\T\\SKARV_fused.glb ", 638_999_000_000_000_000, 1876228) });
            Assert.Null(VehicleLabRules.ProbeStaleReason(stamp, respelled));
        }

        [Fact]
        public void A_rewritten_renamed_added_or_removed_source_is_stale_and_says_which()
        {
            var raw = Src("Raw model", "D:/m/a.glb", 638_999_000_000_000_000, 100);
            string probed = VehicleLabRules.SourceStamp(new[] { raw });
            // the Skarv: the Fuser rewrote the Raw model (a new write time; the size may or may not move)
            string rewritten = VehicleLabRules.SourceStamp(new[] { Src("Raw model", "D:/m/a.glb", 639_000_000_000_000_000, 100) });
            Assert.Contains("the Raw model was rewritten at", VehicleLabRules.ProbeStaleReason(probed, rewritten));
            string resized = VehicleLabRules.SourceStamp(new[] { Src("Raw model", "D:/m/a.glb", 638_999_000_000_000_000, 101) });
            Assert.Contains("the Raw model was rewritten", VehicleLabRules.ProbeStaleReason(probed, resized));
            Assert.Contains("the Raw model now names another file",
                VehicleLabRules.ProbeStaleReason(probed, VehicleLabRules.SourceStamp(new[] { Src("Raw model", "D:/m/b.glb", 638_999_000_000_000_000, 100) })));
            Assert.Contains("the Raw model is no longer on disk",
                VehicleLabRules.ProbeStaleReason(probed, VehicleLabRules.SourceStamp(new[] { Src("Raw model", "D:/m/a.glb", 0, 0, exists: false) })));
            string withSecond = VehicleLabRules.SourceStamp(new[] { raw, Src("Second model", "D:/m/s.glb", 638_999_000_000_000_000, 5) });
            Assert.Equal("the source changed since Probe: a Second model was added", VehicleLabRules.ProbeStaleReason(probed, withSecond));
            Assert.Equal("the source changed since Probe: the Second model was removed", VehicleLabRules.ProbeStaleReason(withSecond, probed));
        }

        [Fact]
        public void A_marking_carries_by_name_by_split_child_or_by_renamed_group_and_the_rest_is_named()
        {
            var roles = new Dictionary<string, string>
            {
                ["Fused_P_Material2_11"] = "Wheel",      // the Skarv: re-fused as Fused_P_Material2_11_Part_001, a split child inherits it
                ["Fused_Y_Object_1722"] = "Turret",      // a renamed fused group: Probe migrated it
                ["Hull"] = "Body",                       // still a part
                ["Fused_Q_Gone"] = "Rotor",              // nothing on the new file
            };
            var placed = new[] { "Fused_P_Material2_11", "Fused_Y_Object_1722", "Mast" };
            var now = new[] { "Fused_P_Material2_11_Part_001", "Fused_Y_Object_1740", "Hull" };
            var lost = VehicleLabRules.MarksNotCarried(roles, placed, now, new[] { "Fused_Y_Object_1722" });
            // a placement is not inherited by a split child (Probe restores it by exact name only), so the Skarv's would be named
            Assert.Equal(new[] { "Fused_P_Material2_11 (placement)", "Fused_Q_Gone (Rotor)", "Mast (placement)" }, lost);
            // the Skarv as it was: a Wheel and no placement - everything lands
            Assert.Empty(VehicleLabRules.MarksNotCarried(new Dictionary<string, string> { ["Fused_P_Material2_11"] = "Wheel" }, null, now, null));
            Assert.Equal(new[] { "X (Wheel + placement)" },
                VehicleLabRules.MarksNotCarried(new Dictionary<string, string> { ["X"] = "Wheel" }, new[] { "X" }, new string[0], null));
        }

        [Fact]
        public void The_stamp_is_one_tab_separated_line_per_file_and_a_missing_file_says_so()
        {
            Assert.Equal("Raw model\tD:/m/a.glb\t5\t7\nSecond model\tD:/m/s.glb\tmissing",
                VehicleLabRules.SourceStamp(new[] { Src("Raw model", " D:/m/a.glb ", 5, 7), Src("Second model", "D:/m/s.glb", 9, 9, exists: false) }));
            Assert.Equal("", VehicleLabRules.SourceStamp(null));
        }
    }
}
