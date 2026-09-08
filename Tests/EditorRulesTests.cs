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
        Assert.Equal("Part_12345678901234567890", NaturalOrder.Prefix(big) + big.Substring(NaturalOrder.Prefix(big).Length));
    }
}
