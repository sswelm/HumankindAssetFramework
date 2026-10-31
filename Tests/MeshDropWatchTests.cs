using System;
using System.Linq;
using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;

public class MeshDropWatchTests
{
    public struct Mesh { public int VertexCount; }
    public sealed class Buffer { public int Size; public Mesh[] WriteContent; }
    public sealed class Layer
    {
        public string name = "test-buffer";
        public int currentMeshAddedCount = 1, currentVertexIndex = 90, currentIndexIndex = 1;
        public Buffer vertexBuffer = new Buffer { Size = 100 }, indexBuffer = new Buffer { Size = 1000 };
        public Buffer hxFxOneMeshComputeBuffer = new Buffer { WriteContent = new Mesh[3] };
    }
    public struct Input { public string Guid; public int VertexCount, QuadCount; }
    public MeshDropWatchTests() { if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test"); }
    static Input Source(string guid = "asset", int verts = 20, int quads = 1) => new Input { Guid = guid, VertexCount = verts, QuadCount = quads };
    static void Drop(Layer layer, Input input)
    {
        var before = MeshDropWatch.Before(layer, input);
        MeshDropWatch.After(layer.currentMeshAddedCount, layer, "Body", before);
    }
    [Fact]
    public void Vertex_capacity_refusal_is_visible_even_when_the_fill_cursor_does_not_move()
    {
        var layer = new Layer(); Drop(layer, Source());
        var drop = Assert.Single(MeshDropWatch.Pending(layer)); Assert.True(drop.Capacity); Assert.Contains("vertex buffer", drop.Reason);
        Assert.Equal(90, layer.currentVertexIndex);
    }
    [Fact]
    public void Index_padding_is_included_in_the_capacity_check()
    {
        var layer = new Layer { currentVertexIndex = 50, currentIndexIndex = 950 };
        var before = MeshDropWatch.Before(layer, Source(verts: 40));
        layer.currentVertexIndex = 90;   // the engine writes vertices BEFORE it discovers insufficient index space
        MeshDropWatch.After(1, layer, "Body", before);
        var drop = Assert.Single(MeshDropWatch.Pending(layer)); Assert.True(drop.Capacity); Assert.Contains("index buffer", drop.Reason);
    }
    [Fact]
    public void A_thrown_table_overflow_is_recorded_and_deduplicated()
    {
        var layer = new Layer { currentMeshAddedCount = 3 };
        var before = MeshDropWatch.Before(layer, Source());
        for (int i = 0; i < 2; i++) MeshDropWatch.After(-1, layer, "Body", before, new IndexOutOfRangeException());
        var drop = Assert.Single(MeshDropWatch.Pending(layer)); Assert.True(drop.Capacity); Assert.Contains("mesh table", drop.Reason);
    }
    [Fact]
    public void Corrupt_or_empty_geometry_does_not_falsely_declare_the_buffer_full()
    {
        var layer = new Layer { currentVertexIndex = 1 };
        Drop(layer, Source(verts: 2));
        var drop = Assert.Single(MeshDropWatch.Pending(layer)); Assert.False(drop.Capacity); Assert.Contains("encoder", drop.Reason);
    }
    [Fact]
    public void Equal_layer_names_and_rebuilt_buffers_have_independent_failure_records()
    {
        var first = new Layer(); var second = new Layer(); Drop(first, Source());
        Assert.Empty(MeshDropWatch.Pending(second));
        first.hxFxOneMeshComputeBuffer = new Buffer { WriteContent = new Mesh[3] };
        Assert.Empty(MeshDropWatch.Pending(first));
    }
    [Fact]
    public void Copying_the_CPU_array_keeps_failures_and_repairing_a_slot_resolves_them()
    {
        var layer = new Layer(); Drop(layer, Source());
        layer.hxFxOneMeshComputeBuffer.WriteContent = (Mesh[])layer.hxFxOneMeshComputeBuffer.WriteContent.Clone();
        Assert.Single(MeshDropWatch.Pending(layer));
        layer.hxFxOneMeshComputeBuffer.WriteContent[1].VertexCount = 20;
        Assert.Empty(MeshDropWatch.Pending(layer));
    }
    [Fact]
    public void Retrying_one_asset_after_table_growth_does_not_clear_a_different_refused_asset()
    {
        var layer = new Layer { currentMeshAddedCount = 3 };
        foreach (string id in new[] { "a", "b" }) MeshDropWatch.After(-1, layer, "Body", MeshDropWatch.Before(layer, Source(id)), new IndexOutOfRangeException());
        Assert.Equal(2, MeshDropWatch.Pending(layer).Length);
        layer.hxFxOneMeshComputeBuffer.WriteContent = new Mesh[5]; layer.hxFxOneMeshComputeBuffer.WriteContent[3].VertexCount = 20;
        MeshDropWatch.After(3, layer, "Body", MeshDropWatch.Before(layer, Source("a")));
        Assert.Equal("b", Assert.Single(MeshDropWatch.Pending(layer)).Asset);
    }
    [Fact]
    public void Successful_registration_and_unknown_record_layout_are_not_reported_as_drops()
    {
        var layer = new Layer(); layer.hxFxOneMeshComputeBuffer.WriteContent[1].VertexCount = 20;
        Drop(layer, Source()); Assert.Empty(MeshDropWatch.Pending(layer));
        var unknown = new { name = "unknown", hxFxOneMeshComputeBuffer = new { WriteContent = new object[2] } };
        MeshDropWatch.After(1, unknown, "Body", MeshDropWatch.Before(unknown, Source()));
        Assert.Empty(MeshDropWatch.Pending(unknown));
    }
    [Fact]
    public void Rejected_meshes_alarm_the_smoke_test_without_claiming_full_capacity()
    {
        var budget = new UniversalInject.LayerBudget { Name = "test", Verts = 10, VertsMax = 1000, Dropped = 1 };
        Assert.Equal(0, UniversalInject.BudgetBand(budget));
        Assert.Contains("1 mesh(es) refused (0 for capacity)", UniversalInject.BudgetAlarm(budget));
        budget.CapacityDrops = 1;
        Assert.Equal(3, UniversalInject.BudgetBand(budget));
        budget.Dropped = 0; budget.CapacityDrops = 0;
        Assert.Null(UniversalInject.BudgetAlarm(budget));
    }
}
