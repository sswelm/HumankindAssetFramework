using System;
using System.Linq;
using Xunit;

/// <summary>Every node's matrix_world as Blender holds it (VehicleProbe.BlenderWorld.cs). Each expectation is the matrix
/// Blender 5.1 printed for the same node (`matrix_world`, 16 floats, 2026-10-03: the re-fused Dragon's root and a part
/// under its translated group, the `insideout` fixture's turned and mirrored nodes), compared BIT FOR BIT: a 90-degree
/// permutation comes back from the importer's quaternion round trip as 1.0000001 and -1.3e-7, and that is what the
/// verdicts must read. The drill holds every part of 119 files to the same standard (MATRIX rows).</summary>
public class VehicleProbeBlenderWorldTests
{
    static HafModel Link(HafModel m) { for (int i = 0; i < m.Nodes.Count; i++) foreach (var c in m.Nodes[i].Children) m.Nodes[c].Parent = i; return m; }
    static float[] World(HafModel m, int node) => VehicleProbe.ToRowMajor(VehicleProbe.BlenderWorldMatrices(Link(m), null)[node]);
    static float F(double x) => (float)x;

    [Fact]
    public void A_matrix_node_is_decomposed_and_recomposed_as_the_importer_and_Blender_do()
    {
        // the Dragon's root: a 90-degree permutation with 2.2e-16 residues, as Sketchfab writes it
        var m = new HafModel();
        m.Nodes.Add(new HafNode { Name = "Sketchfab_model", Matrix = new[] { 1.0, 0, 0, 0, 0, 2.220446049250313e-16, -1, 0, 0, 1, 2.220446049250313e-16, 0, 0, 0, 0, 1 } });
        var w = World(m, 0);
        Assert.Equal(new[] { 1f, 0f, 0f, 0f, 0f, F(-1.3435885648505064e-07), F(1.0000001192092896), 0f, 0f, F(-1.0000001192092896), F(-1.3435885648505064e-07), 0f, 0f, 0f, 0f, 1f }, w);
    }

    [Fact]
    public void A_parent_chain_multiplies_in_float32_with_Blenders_association()
    {
        // the Dragon's Material3_114: root (above) > identity > identity > a translation-only matrix node > the part
        var m = new HafModel();
        m.Nodes.Add(new HafNode { Name = "Sketchfab_model", Matrix = new[] { 1.0, 0, 0, 0, 0, 2.220446049250313e-16, -1, 0, 0, 1, 2.220446049250313e-16, 0, 0, 0, 0, 1 } });
        m.Nodes.Add(new HafNode { Name = "Collada visual scene group" }); m.Nodes[0].Children.Add(1);
        m.Nodes.Add(new HafNode { Name = "SketchUp" }); m.Nodes[1].Children.Add(2);
        m.Nodes.Add(new HafNode { Name = "group_20", Matrix = new[] { 1.0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, (double)2149.789f, (double)452.7462f, (double)216.5354f, 1 } }); m.Nodes[2].Children.Add(3);
        m.Nodes.Add(new HafNode { Name = "Material3_114" }); m.Nodes[3].Children.Add(4);
        var w = World(m, 4);
        Assert.Equal(new[] { 1f, 0f, 0f, F(2149.7890625), 0f, F(-1.3435885648505064e-07), F(1.0000001192092896), F(452.74627685546875), 0f, F(-1.0000001192092896), F(-1.3435885648505064e-07), F(216.53536987304688), 0f, 0f, 0f, 1f }, w);
    }

    [Fact]
    public void A_quaternion_is_normalized_and_turned_into_a_matrix_in_double_then_float32()
    {
        // the insideout fixture's TurnedDown: 90 degrees about glTF X, moved 50 along X
        var m = new HafModel();
        m.Nodes.Add(new HafNode { Name = "TurnedDown", Rotation = new[] { 0.7071067811865476, 0, 0, 0.7071067811865476 }, Translation = new double[] { 50, 0, 0 } });
        var w = World(m, 0);
        Assert.Equal(new[] { 1f, 0f, 0f, 50f, 0f, F(-1.3435885648505064e-07), F(-1.0000001192092896), 0f, 0f, F(1.0000001192092896), F(-1.3435885648505064e-07), 0f, 0f, 0f, 0f, 1f }, w);
    }

    [Fact]
    public void A_mirrored_scale_and_an_unmoved_node_are_exact()
    {
        var m = new HafModel();
        m.Nodes.Add(new HafNode { Name = "MirroredDown", Scale = new double[] { -1, 1, 1 } });
        m.Nodes.Add(new HafNode { Name = "PlateDown" });
        Assert.Equal(new[] { -1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f }, World(m, 0));
        Assert.Equal(new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f }, World(m, 1));
    }
}
