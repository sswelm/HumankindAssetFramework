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
    [InlineData("Assets/X/ConstructibleCommonExtensionDefinition.asset")]
    [InlineData("Assets/X/presentationpawndefinition_lower.ASSET")]
    [InlineData("Assets\\X\\PresentationPawnDefinition_Era1_ENC.asset")]
    // The review's finding: FindAssets matches SUB-asset names, so a database need not carry the word in its file name.
    [InlineData("Assets/Other/UnitPresentation_Extra.asset")]
    [InlineData("Assets/Resources/Tank_Skeleton.asset")]                                           // any .asset: the rule cannot tell
    public void An_asset_file_may_hold_definitions(string path)
    {
        Assert.True(DefinitionCaches.MayHoldDefinitions(path), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Assets/Databases/UnitPresentation/PresentationPawnDefinition_Era5_ENC.asset.meta")]
    [InlineData("Assets/PresentationPawnDefinition/Readme.txt")]
    [InlineData("Assets/Scripts/PresentationPawnDefinition.cs")]
    [InlineData("Assets/Textures/Tank_Atlas.png")]
    [InlineData("Assets/Models/Tank.fbx")]
    [InlineData("Assets/PresentationPawnDefinition.asset/inside.txt")]
    public void Any_other_path_does_not(string path)
    {
        Assert.False(DefinitionCaches.MayHoldDefinitions(path), path ?? "(null)");
    }

    [Fact]
    public void A_pass_with_an_asset_in_any_of_its_lists_moves_the_generation_once()
    {
        const string pawn = "Assets/Databases/UnitPresentation/PresentationPawnDefinition_Era5_ENC.asset";
        var none = new string[0];
        var other = new[] { "Assets/Models/Tank.fbx", "Assets/Tex.png" };
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
    public void A_pass_without_an_asset_file_leaves_the_generation()
    {
        int before = DefinitionCaches.Generation;
        Assert.False(DefinitionCaches.OnAssetsChanged(new[] { "Assets/Models/Tank.fbx", "Assets/Tex.png" }, new string[0], null, new[] { (string)null, "" }));
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
