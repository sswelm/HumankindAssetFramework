using Xunit;

// The lists read out of the game's definition databases are kept for one generation (2026-11-24: a pawn definition
// made in the Unity editor reached the Model Factory's Pick list only after a restart, because the list was kept for
// the whole session). These tests hold the pure half: which asset paths move the generation. The Unity half (a saved
// database edit reaches the windows) is tools/definition_caches_unity_drill.sh.
public class DefinitionCachesTests
{
    [Theory]
    [InlineData("Assets/Databases/UnitPresentation/PresentationPawnDefinition_Era5_ENC.asset")]   // the reported file
    [InlineData("Assets/~References/UnitPresentation/PresentationPawnDefinition_Era0.asset")]
    [InlineData("Assets/Databases/_DLC01/UnitPresentation/PresentationPawnDefinition_DLC01.asset")]
    [InlineData("Assets/X/ConstructibleCommonExtensionDefinition.asset")]
    [InlineData("Assets/X/My_ConstructibleCommonExtensionDefinition_ENC.asset")]
    [InlineData("Assets/X/presentationpawndefinition_lower.ASSET")]                                // FindAssets ignores case
    [InlineData("Assets\\X\\PresentationPawnDefinition_Era1_ENC.asset")]
    [InlineData("Assets/PresentationPawnDefinition/Other.asset")]                                  // wider than the scans, on purpose
    public void A_container_the_scans_read_is_recognised(string path)
    {
        Assert.True(DefinitionCaches.IsContainer(path), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Assets/Resources/Tank_Skeleton.asset")]                                           // a bake's own asset
    [InlineData("Assets/Databases/UnitPresentation/PresentationUnitDefinition_Era5_ENC.asset")]    // read uncached elsewhere
    [InlineData("Assets/Databases/UnitPresentation/PresentationPawnDefinition_Era5_ENC.asset.meta")]
    [InlineData("Assets/PresentationPawnDefinition/Readme.txt")]
    [InlineData("Assets/Scripts/PresentationPawnDefinition.cs")]
    public void Any_other_path_is_not(string path)
    {
        Assert.False(DefinitionCaches.IsContainer(path), path ?? "(null)");
    }

    [Fact]
    public void A_pass_with_a_container_in_any_of_its_lists_moves_the_generation_once()
    {
        const string pawn = "Assets/Databases/UnitPresentation/PresentationPawnDefinition_Era5_ENC.asset";
        var none = new string[0];
        var other = new[] { "Assets/Resources/Tank_Skeleton.asset", "Assets/Tex.png" };
        // imported, deleted, moved to, moved from: each position on its own
        foreach (var pass in new[] {
            new[] { new[] { pawn }, none, none, none },
            new[] { none, new[] { pawn }, none, none },
            new[] { none, none, new[] { pawn }, none },
            new[] { none, none, none, new[] { pawn } },
            new[] { other, none, none, new[] { "Assets/a.png", pawn, pawn } },
        })
        {
            int before = DefinitionCaches.Generation;
            Assert.True(DefinitionCaches.OnAssetsChanged(pass));
            Assert.Equal(before + 1, DefinitionCaches.Generation);
        }
    }

    [Fact]
    public void A_pass_without_a_container_leaves_the_generation()
    {
        int before = DefinitionCaches.Generation;
        Assert.False(DefinitionCaches.OnAssetsChanged(new[] { "Assets/Resources/Tank_Skeleton.asset" }, new string[0], null, new[] { (string)null, "" }));
        Assert.False(DefinitionCaches.OnAssetsChanged());
        Assert.False(DefinitionCaches.OnAssetsChanged(null));
        Assert.Equal(before, DefinitionCaches.Generation);
    }

    [Fact]
    public void A_pick_click_moves_the_generation()
    {
        int before = DefinitionCaches.Generation;
        DefinitionCaches.Invalidate();
        Assert.Equal(before + 1, DefinitionCaches.Generation);
    }
}
