using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>The PART row's inside-out verdict (field 8, step 3c): the islands the fix would reverse. Every expectation
/// here was READ OFF Blender's own probe (vehicle_rig.py probe, Blender 5.1, 2026-10-02) on the `insideout` fixture of
/// the same shape in tools/vehicle-probe-drill/naming_fixtures.py; the drill holds the two together on the real files
/// (14,023 parts on 119 files; 3 skinned parts differ on zero-area triangles that Blender's float32 skinning perturbs - said in
/// VehicleProbe.InsideOut.cs and docs/Review-Backlog.md).</summary>
public class VehicleProbeInsideOutTests
{
    static HafMesh Mesh(string name, float[] positions, int[] indices)
    {
        var me = new HafMesh { Name = name };
        me.Primitives.Add(new HafPrimitive { VertexCount = positions.Length / 3, Positions = positions, Indices = indices, Mode = 4 });
        return me;
    }
    /// <summary>The fixture's keel: an 81-vertex grid 5 below the plates (glTF y = -5) that pins the hull axis there.</summary>
    static HafMesh Keel()
    {
        var pos = new List<float>(); var idx = new List<int>(); const int n = 8;
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) { pos.Add((float)i / n - 0.5f); pos.Add(-5f); pos.Add((float)j / n - 0.5f); }
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) { int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1; idx.AddRange(new[] { a, b, d, a, d, c }); }
        return Mesh("keel", pos.ToArray(), idx.ToArray());
    }
    /// <summary>Four quads sharing their corners (one island) from x0 to x0 + 4 at glTF y = 5, across glTF z in [-1, 1]; wound so
    /// the face normal is glTF -y (Blender -z, towards the axis) when down, +y when up.</summary>
    static (float[] pos, int[] idx) Strip(float x0, bool down)
    {
        var pos = new List<float>(); var idx = new List<int>();
        for (int q = 0; q < 5; q++) { pos.Add(x0 + q); pos.Add(5f); pos.Add(-1f); }
        for (int q = 0; q < 5; q++) { pos.Add(x0 + q); pos.Add(5f); pos.Add(1f); }
        for (int q = 0; q < 4; q++)
        {
            int p0 = q, p1 = q + 1, p2 = 5 + q + 1, p3 = 5 + q;
            idx.AddRange(down ? new[] { p0, p1, p2, p0, p2, p3 } : new[] { p0, p2, p1, p0, p3, p2 });
        }
        return (pos.ToArray(), idx.ToArray());
    }
    static HafModel Link(HafModel m) { for (int i = 0; i < m.Nodes.Count; i++) foreach (var c in m.Nodes[i].Children) m.Nodes[c].Parent = i; return m; }
    static int Flip(VehicleProbe.Result r, string name) => r.Parts.Single(p => p.Name == name).Flip;
    static HafModel KeelAndPlates()
    {
        var m = new HafModel();
        m.Meshes.Add(Keel()); m.Nodes.Add(new HafNode { Name = "Keel", Mesh = 0 });
        var (dp, di) = Strip(0, true); m.Meshes.Add(Mesh("platedown", dp, di)); m.Nodes.Add(new HafNode { Name = "PlateDown", Mesh = 1 });
        var (up, ui) = Strip(10, false); m.Meshes.Add(Mesh("plateup", up, ui)); m.Nodes.Add(new HafNode { Name = "PlateUp", Mesh = 2 });
        return m;
    }

    [Fact]
    public void A_strip_whose_faces_point_at_the_hull_axis_is_reversed_and_its_twin_is_not()
    {
        // the "insideout" fixture, Blender: Keel 0, PlateDown 1, PlateUp 0
        var r = VehicleProbe.Run(Link(KeelAndPlates()));
        Assert.Equal("0 1 0", Flip(r, "Keel") + " " + Flip(r, "PlateDown") + " " + Flip(r, "PlateUp"));
    }

    [Fact]
    public void A_mirrored_node_keeps_the_local_face_normal_as_bmesh_does()
    {
        // the fixture's MirroredDown / MirroredUp: the same strips under a node of scale (-1, 1, 1). Blender computes the face normal
        // from the LOCAL corners and takes it through matrix_world's 3x3, so the mirror does not turn it: 1 and 0 - where the cross
        // product of the WORLD corners says the opposite (negativescaletest's Shiny1 read 1 against Blender's 0 with that).
        var m = KeelAndPlates();
        var (dp, di) = Strip(0, true); m.Meshes.Add(Mesh("mirroreddown", dp, di)); m.Nodes.Add(new HafNode { Name = "MirroredDown", Mesh = 3, Scale = new double[] { -1, 1, 1 } });
        var (up, ui) = Strip(10, false); m.Meshes.Add(Mesh("mirroredup", up, ui)); m.Nodes.Add(new HafNode { Name = "MirroredUp", Mesh = 4, Scale = new double[] { -1, 1, 1 } });
        var r = VehicleProbe.Run(Link(m));
        Assert.Equal("1 0", Flip(r, "MirroredDown") + " " + Flip(r, "MirroredUp"));
    }

    [Fact]
    public void Islands_are_faces_joined_by_an_edge_and_each_votes_on_its_own()
    {
        // the fixture's TwoIslands: one mesh holding a down strip and an up strip that share no vertex - two islands, one reversed (1);
        // three strips down, up, down in one mesh: 2.
        var m = KeelAndPlates();
        var (dp, di) = Strip(20, true); var (up, ui) = Strip(30, false); var (d2, i2) = Strip(40, true);
        m.Meshes.Add(Mesh("twoislands", dp.Concat(up).ToArray(), di.Concat(ui.Select(i => i + 10)).ToArray())); m.Nodes.Add(new HafNode { Name = "TwoIslands", Mesh = 3 });
        m.Meshes.Add(Mesh("three", dp.Concat(up).Concat(d2).ToArray(), di.Concat(ui.Select(i => i + 10)).Concat(i2.Select(i => i + 20)).ToArray())); m.Nodes.Add(new HafNode { Name = "Three", Mesh = 4 });
        var r = VehicleProbe.Run(Link(m));
        Assert.Equal("1 2", Flip(r, "TwoIslands") + " " + Flip(r, "Three"));
    }

    [Fact]
    public void Faces_touching_at_only_a_vertex_vote_as_separate_islands()
    {
        // insideout fixture, Blender: VertexTouch 2. Sharing a corner does not join face islands.
        var m = KeelAndPlates();
        m.Meshes.Add(Mesh("vertextouch", new float[] { 0, 5, 0, 1, 5, 0, 0, 5, 1, -1, 5, 0, 0, 5, -1 }, new[] { 0, 1, 2, 0, 3, 4 }));
        m.Nodes.Add(new HafNode { Name = "VertexTouch", Mesh = 3 });
        Assert.Equal(2, Flip(VehicleProbe.Run(Link(m)), "VertexTouch"));
    }

    [Fact]
    public void All_faces_on_a_nonmanifold_edge_vote_as_one_island()
    {
        // insideout fixture, Blender: NonManifold 1. Two inward and one outward face share one edge;
        // their average is below -0.25. Every linked face joins the island, including the third.
        var m = KeelAndPlates();
        m.Meshes.Add(Mesh("nonmanifold", new float[] { 0, 5, 0, 1, 5, 0, 0, 5, 1, 0, 5, 2, 0, 5, -1 }, new[] { 0, 1, 2, 0, 1, 3, 0, 1, 4 }));
        m.Nodes.Add(new HafNode { Name = "NonManifold", Mesh = 3 });
        Assert.Equal(1, Flip(VehicleProbe.Run(Link(m)), "NonManifold"));
    }

    [Fact]
    public void A_zero_area_triangle_casts_no_vote_and_its_island_does_not_count()
    {
        // the fixture's Collinear: the down strip beside a triangle whose corners lie on one line - bmesh's normal for it is exactly
        // zero (below 1e-35), the script skips it, and an island nobody voted on is not reversed: 1, not 2.
        var m = KeelAndPlates();
        var (dp, di) = Strip(0, true);
        var pos = dp.Concat(new float[] { 40, 5, 0, 41, 5, 0, 42, 5, 0 }).ToArray(); var idx = di.Concat(new[] { 10, 11, 12 }).ToArray();
        m.Meshes.Add(Mesh("collinear", pos, idx)); m.Nodes.Add(new HafNode { Name = "Collinear", Mesh = 3 });
        var r = VehicleProbe.Run(Link(m));
        Assert.Equal(1, Flip(r, "Collinear"));
    }

    [Fact]
    public void A_skinned_strip_is_judged_in_its_bind_pose_under_the_armatures_matrix()
    {
        // the fixture's SkinnedDown: the down strip skinned to a joint at rest (1)
        var m = KeelAndPlates();
        var (dp, di) = Strip(0, true);
        var me = Mesh("skinneddown", dp, di);
        me.Primitives[0].Joints = Enumerable.Repeat(new ushort[] { 0, 0, 0, 0 }, 10).SelectMany(j => j).ToArray();
        me.Primitives[0].Weights = Enumerable.Repeat(new float[] { 1, 0, 0, 0 }, 10).SelectMany(w => w).ToArray();
        m.Meshes.Add(me);
        m.Nodes.Add(new HafNode { Name = "Rig" }); m.Nodes[3].Children.Add(4);
        m.Nodes.Add(new HafNode { Name = "Joint" });
        m.Nodes.Add(new HafNode { Name = "SkinnedDown", Mesh = 3, Skin = 0 });
        m.Skins.Add(new HafSkin { Name = "Skin", Joints = new[] { 4 } });
        var r = VehicleProbe.Run(Link(m));
        Assert.Equal(1, Flip(r, "SkinnedDown"));
    }
}
