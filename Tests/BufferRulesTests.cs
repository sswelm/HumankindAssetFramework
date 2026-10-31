using System.Collections.Generic;
using HumankindAssetFramework;
using Xunit;

// [Buffers] BufferOverrides (Patches/BufferRules.cs). Until 2026-10-31 the key was a substring: "MeshWithSkeleton"
// enlarged the pawn layer AND the unrelated 'MeshWithSkeleton' layer, and a typo matched nothing, silently.
public class BufferRulesTests
{
    static readonly string[] Layers = { "Visual", "MeshWithSkeleton", "MeshWithSkeletonParticleIndexBuffer", "VisualParticleIndexBuffer" };

    [Fact]
    public void A_key_names_one_layer_exactly()
    {
        var problems = new List<string>();
        var o = BufferRules.Parse("MeshWithSkeleton:verts=+3000000,idx=+3000000,meshes=+1000,maxtris=0", problems);
        Assert.Empty(problems);
        var one = Assert.Single(o);
        Assert.Equal(3_000_000, one.Verts); Assert.Equal(3_000_000, one.Idx); Assert.Equal(1000, one.Meshes); Assert.Equal(0, one.MaxTris);
        Assert.True(BufferRules.Matches(one, "MeshWithSkeleton"));
        Assert.True(BufferRules.Matches(one, "meshwithskeleton"));                         // case does not matter
        Assert.False(BufferRules.Matches(one, "MeshWithSkeletonParticleIndexBuffer"));   // the substring match of before: gone
    }

    [Fact]
    public void A_trailing_star_is_a_prefix()
    {
        var o = BufferRules.Parse("MeshWithSkeleton*:verts=+1", new List<string>());
        Assert.True(o[0].Prefix); Assert.Equal("MeshWithSkeleton*", o[0].Key);
        Assert.True(BufferRules.Matches(o[0], "MeshWithSkeletonParticleIndexBuffer"));
        Assert.True(BufferRules.Matches(o[0], "MeshWithSkeleton"));
        Assert.False(BufferRules.Matches(o[0], "Visual"));
    }

    [Fact]
    public void Problems_are_reported_not_skipped()
    {
        var problems = new List<string>();
        var o = BufferRules.Parse("MeshWithSkeletonParticleIndexBuffer:vert=+5,idx=lots,meshes=+5; nocolon ; :verts=+1; Visual:", problems);
        Assert.Single(o);                                       // the first key survives with its one good field
        Assert.Equal(5, o[0].Meshes); Assert.Equal(0, o[0].Verts);
        Assert.Contains(problems, p => p.Contains("unknown field 'vert'"));
        Assert.Contains(problems, p => p.Contains("'idx=lots' is not <field>=<number>"));
        Assert.Contains(problems, p => p.Contains("'nocolon'"));
        Assert.Contains(problems, p => p.Contains("layer name is empty"));
        Assert.Contains(problems, p => p.Contains("'Visual': nothing to apply"));
        Assert.Equal(5, problems.Count);
    }

    [Fact]
    public void A_key_no_layer_carries_is_named()
    {
        var o = BufferRules.Parse("MeshWithSkeletonParticleIndexBuffer:verts=+1;MeshWithSkelton:verts=+1;Vis*:verts=+1", new List<string>());
        Assert.Equal(new[] { "MeshWithSkelton" }, BufferRules.Unmatched(o, Layers));
        Assert.Empty(BufferRules.Unmatched(o.GetRange(0, 1), Layers));
    }

    [Fact]
    public void Blank_and_untouched_fields_apply_nothing()
    {
        var problems = new List<string>();
        Assert.Empty(BufferRules.Parse("", problems)); Assert.Empty(BufferRules.Parse(null, problems)); Assert.Empty(BufferRules.Parse(" ; ", problems));
        Assert.Empty(problems);
        var o = BufferRules.Parse("Visual:verts=+10", problems)[0];
        Assert.Equal(-1, o.MaxTris); Assert.Equal(0, o.Idx); Assert.Equal(0, o.Meshes);
    }

    [Fact]
    public void The_index_buffer_stops_at_the_24_bit_ceiling()
    {
        Assert.Equal(9_500_000, BufferRules.ClampedIndexSize(6_500_000, 3_000_000, out string note)); Assert.Null(note);
        Assert.Equal(BufferRules.IndexCeiling, BufferRules.ClampedIndexSize(6_500_000, 12_000_000, out note));
        Assert.Contains("24-bit", note);
        Assert.Equal(16_777_216, BufferRules.IndexCeiling);
    }
}
