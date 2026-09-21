using System.Linq;
using Xunit;

// The pure editor decision kernels (editor/EditorRules.cs). These lock the semantics that review finding 2
// (2026-09-07) proved can rot invisibly: the extraction predicate was wrong for a month — it demanded a file
// only multi-material sources have — and nothing crashed or logged; the keepTexture checkbox just silently
// failed to protect single-material hand-edits.
public class BakerRulesTests
{
    [Fact]
    public void Fresh_extraction_is_used_regardless_of_the_checkbox()
    {
        Assert.Equal(BakerRules.ExtractionAction.UseExisting, BakerRules.DecideExtraction(true, true, false));
        Assert.Equal(BakerRules.ExtractionAction.UseExisting, BakerRules.DecideExtraction(true, true, true));
    }

    [Fact]
    public void Single_material_shape_counts_as_existing()
    {
        // THE finding-2 regression: `extractedExists` is either shape — the single _albedo.png alone
        // (no MTL) must read as an existing extraction. With a matching stamp it is fresh: no re-extract,
        // no hygiene deletion of a hand-edited albedo on every bake.
        Assert.Equal(BakerRules.ExtractionAction.UseExisting, BakerRules.DecideExtraction(
            extractedExists: true /* albedo only, no MTL */, stampMatches: true, keepTexture: false));
    }

    [Fact]
    public void Stale_extraction_reextracts_unless_protected()
    {
        Assert.Equal(BakerRules.ExtractionAction.ReExtract, BakerRules.DecideExtraction(true, false, false));
        Assert.Equal(BakerRules.ExtractionAction.KeepProtected, BakerRules.DecideExtraction(true, false, true));
    }

    [Fact]
    public void KeepTexture_cannot_protect_files_that_do_not_exist()
    {
        Assert.Equal(BakerRules.ExtractionAction.ReExtract, BakerRules.DecideExtraction(false, false, true));
        // A stray stamp with no artifacts is not an extraction either.
        Assert.Equal(BakerRules.ExtractionAction.ReExtract, BakerRules.DecideExtraction(false, true, true));
        Assert.Equal(BakerRules.ExtractionAction.ReExtract, BakerRules.DecideExtraction(false, true, false));
    }

    // ---- placement: the static and animated bakes must land the same model in the same spot ----
    // (2026-09-20. Facing was unified on 2026-09-12; placement was not, and nothing noticed for eight days
    // because each path looks right on its own — the jump only shows when you bake the same model both ways.)

    [Fact]
    public void Placement_centres_the_footprint_and_grounds_the_lowest_point()
    {
        // The steam frigate's own file, measured from its bytes: box x -8.11..53.70, y 3.87..20.18, z -5.24..37.99.
        BakerRules.Placement(-8.11, 53.70, 3.87, 20.18, -5.237, out double sway, out double fore, out double raise);
        Assert.Equal(-22.795, sway, 3);   // the model hangs 22.8 rig units off its own origin...
        Assert.Equal(-12.025, fore, 3);   // ...and 12.0 across; ~1.4 game units at size 5, the offset the user saw
        // (the file's own centre is 22.794 x 12.024 — these are the same numbers through the box as written above,
        //  rounded to a hundredth of a rig unit, which is a ten-thousandth of a game unit at this model's scale)
        Assert.Equal(5.237, raise, 3);    // keel to the ground
    }

    [Fact]
    public void Placement_is_self_correcting_so_a_re_bake_can_never_double_apply_it()
    {
        // Place a box, then place the RESULT: an already-placed model must not move a second time. The
        // pre-2026-08 grounding measure ("wheels-on minus wheels-off" protrusion) failed exactly here — a
        // fixed lift that floated a file which was already on the ground.
        double x0 = -8.11, x1 = 53.70, y0 = 3.87, y1 = 20.18, z0 = -5.237;
        BakerRules.Placement(x0, x1, y0, y1, z0, out double sway, out double fore, out double raise);
        BakerRules.Placement(x0 + sway, x1 + sway, y0 + fore, y1 + fore, z0 + raise,
                             out double sway2, out double fore2, out double raise2);
        Assert.Equal(0.0, sway2, 9);
        Assert.Equal(0.0, fore2, 9);
        Assert.Equal(0.0, raise2, 9);
    }

    [Fact]
    public void Placement_grounds_a_flyer_too_because_the_static_path_always_has()
    {
        // The retired Auto-ground toggle was OFF for flyers, on the theory that grounding would pin them down.
        // The static path never had that escape hatch: it grounds everything, and the flying height is the
        // Position offset Z dial. One convention — so a helicopter whose file sits 40 units up comes down.
        BakerRules.Placement(-2, 2, -3, 3, 40.0, out double sway, out double fore, out double raise);
        Assert.Equal(0.0, sway, 9);
        Assert.Equal(0.0, fore, 9);
        Assert.Equal(-40.0, raise, 9);   // it descends to the ground; its altitude is the dial's job
    }

    [Fact]
    public void Placement_of_a_model_already_on_the_origin_is_a_no_op()
    {
        // Why no existing static bake moves: step 2 centres the box before rotating, so at every axis-aligned
        // rotation (0, +-90, 180) the rotated box is still centred and this adds nothing. Drilled on the frigate's
        // real cloud: 0.00000 at 0/90/-90/180, and 0.026 at 45 degrees — where the two paths genuinely disagreed.
        BakerRules.Placement(-30.9, 30.9, -8.2, 8.2, 0.0, out double sway, out double fore, out double raise);
        Assert.Equal(0.0, sway, 9);
        Assert.Equal(0.0, fore, 9);
        Assert.Equal(0.0, raise, 9);
    }

    // ---- district bake outputs (2026-09-21 review) -------------------------------------------------------------
    // These basenames drive a DELETE loop (the rollback wipes partial new outputs before copying the old ones back),
    // so both their exact spelling and the empty-name case are load-bearing.
    [Fact]
    public void District_outputs_name_all_four_assets_including_the_prefix_one()
    {
        var n = BakerRules.DistrictOutputBasenames("BreederReactor");
        Assert.Equal(4, n.Count);
        Assert.Contains("BreederReactor_DistrictMesh.asset", n);
        Assert.Contains("BreederReactor_FxMesh.asset", n);
        Assert.Contains("BreederReactor_Element.asset", n);
        // the one that is a PREFIX, not a suffix — the reason this is a basename list, and the reason these names
        // could not live in UniversalBaker.OutputSuffixes even if sharing that array were otherwise safe.
        Assert.Contains("CityMapSelector_BreederReactor.asset", n);
    }

    [Fact]
    public void A_blank_district_name_names_nothing()
    {
        // "" would otherwise yield "_FxMesh.asset" and "CityMapSelector_.asset" — real files, handed to a delete loop.
        Assert.Empty(BakerRules.DistrictOutputBasenames(""));
        Assert.Empty(BakerRules.DistrictOutputBasenames("   "));
        Assert.Empty(BakerRules.DistrictOutputBasenames(null));
    }

    [Fact]
    public void District_output_names_are_trimmed_like_the_window_trims_the_field()
    {
        // DoBake trims cur.resourceName on the entry itself before baking, so the rollback resolves the same names
        // the bake writes — a stray space would back up nothing and then restore nothing.
        Assert.Contains("Quarry_FxMesh.asset", BakerRules.DistrictOutputBasenames("  Quarry  "));
    }

    // The two lists must stay DISJOINT. The moment a district suffix appears in OutputSuffixes, the unit paths'
    // SweepAllOutputs — and the Factory's Remove — start deleting a same-named district's assets.
    [Fact]
    public void District_suffixes_are_not_in_the_unit_sweep_list()
    {
        var unit = UnitSuffixesFromSource();
        Assert.Contains("_ModelMesh.asset", unit);   // the read worked at all
        foreach (var s in BakerRules.DistrictOutputSuffixes) Assert.DoesNotContain(s, unit);
    }

    // UniversalBaker is Unity-bound and not compiled into this suite, so the guard above reads the array out of the
    // source. A literal copy here would keep passing while the real array drifted — exactly the failure this file
    // exists to catch.
    static string[] UnitSuffixesFromSource()
    {
        var d = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(
            new System.Uri(typeof(BakerRulesTests).Assembly.CodeBase).LocalPath));
        while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "editor", "UniversalBaker.cs")))
            d = d.Parent;
        Assert.True(d != null, "could not find editor/UniversalBaker.cs above the test assembly");
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "editor", "UniversalBaker.cs"));
        int i = src.IndexOf("OutputSuffixes = {");
        Assert.True(i > 0, "OutputSuffixes array not found in UniversalBaker.cs");
        int end = src.IndexOf("};", i);
        return System.Text.RegularExpressions.Regex.Matches(src.Substring(i, end - i), "\"([^\"]+)\"")
                   .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToArray();
    }

}

public class NaturalOrderTests
{
    static string[] Sorted(params string[] names) => names
        .OrderBy(NaturalOrder.Prefix, System.StringComparer.OrdinalIgnoreCase)
        .ThenBy(NaturalOrder.Number)
        .ThenBy(n => n, System.StringComparer.OrdinalIgnoreCase)
        .ToArray();   // the exact ordering chain the Model Workshop part list uses

    [Fact]
    public void Object_2_sorts_before_Object_10()
    {
        Assert.Equal(new[] { "Object_1", "Object_2", "Object_9", "Object_10", "Object_20" },
                     Sorted("Object_10", "Object_2", "Object_20", "Object_1", "Object_9"));
    }

    [Fact]
    public void Unnumbered_name_sorts_before_its_numbered_siblings()
    {
        Assert.Equal(new[] { "Hull", "Hull1", "Hull2" }, Sorted("Hull2", "Hull", "Hull1"));
    }

    [Fact]
    public void Different_prefixes_sort_by_prefix_case_insensitively()
    {
        Assert.Equal(new[] { "mast_1", "Object_1", "object_2", "Sail_1" },
                     Sorted("Sail_1", "object_2", "Object_1", "mast_1"));
    }

    [Fact]
    public void Oversized_digit_runs_fall_back_without_throwing()
    {
        // >19 trailing digits exceed long.TryParse — documented fallback is -1 (sorts with the unnumbered).
        string big = "Part_12345678901234567890";
        Assert.Equal(-1, NaturalOrder.Number(big));
        Assert.Equal("Part_", NaturalOrder.Prefix(big));   // the digit run still strips; only the NUMBER falls back
    }

    [Fact]
    public void Tiled_materials_repeat_as_authored_capped_by_the_texture_s_pixels()
    {
        Assert.Equal(1, BakerRules.TileRepeats(0.8, 769, 48));      // one tile: not tiled, the fold stays
        Assert.Equal(1, BakerRules.TileRepeats(1.5, 769, 48));      // the threshold is exclusive
        Assert.Equal(2, BakerRules.TileRepeats(1.6, 769, 48));
        Assert.Equal(13, BakerRules.TileRepeats(13.2, 769, 48));    // the Teutonic's hull skin: 13 repeats fit (769/48 = 16)
        Assert.Equal(16, BakerRules.TileRepeats(104.7, 769, 48));   // 105 authored, capped at 16 (48 px each)
        Assert.Equal(5, BakerRules.TileRepeats(29.0, 256, 48));     // a short axis caps sooner
        Assert.Equal(1, BakerRules.TileRepeats(1036.0, 8, 48));     // a swatch never repeats
        Assert.Equal(1, BakerRules.TileRepeats(double.NaN, 769, 48));
    }

    [Fact]
    public void Point_uv_material_is_the_one_texel_its_whole_span_fits_in()
    {
        Assert.True(BakerRules.PointUv(0.0, 0.0, 512, 1024));            // the Romanic's deck: every face at (0, 1)
        Assert.True(BakerRules.PointUv(1.0 / 512, 1.0 / 1024, 512, 1024)); // exactly one texel still counts
        Assert.False(BakerRules.PointUv(2.0 / 512, 0.0, 512, 1024));      // two texels across: a texture, keep the fold
        Assert.False(BakerRules.PointUv(0.0, 0.5, 512, 1024));            // a line of texels on one axis
        Assert.False(BakerRules.PointUv(53.4, 108.7, 256, 256));          // the hull plating: tiled, never a point
        Assert.False(BakerRules.PointUv(double.NaN, 0.0, 512, 1024));
        Assert.False(BakerRules.PointUv(double.PositiveInfinity, 0.0, 512, 1024));   // an unmeasured span (no UVs)
        Assert.False(BakerRules.PointUv(0.0, 0.0, 0, 1024));              // a texture with no pixels is not sampled
    }

}
