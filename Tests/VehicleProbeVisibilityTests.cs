using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>The PART row's visibility verdict (field 6, step 3b) and the vertex order it samples. Every expectation
/// here was READ OFF Blender's own probe (vehicle_rig.py probe, Blender 5.1, 2026-10-02) on the fixtures of the same
/// shape in tools/vehicle-probe-drill/naming_fixtures.py (`visibility`, `visibility_split`) or on the order fixture
/// described below; the drill holds the two together on the real files (10,498 parts, 1,171 of them interior).</summary>
public class VehicleProbeVisibilityTests
{
    static HafMesh Mesh(string name, float[] positions, int[] indices, float[] normals = null)
    {
        var me = new HafMesh { Name = name };
        me.Primitives.Add(new HafPrimitive { VertexCount = positions.Length / 3, Positions = positions, Indices = indices, Normals = normals, Mode = 4 });
        return me;
    }
    static float[] Box(float cx, float cy, float cz, float h)
    {
        var p = new List<float>();
        foreach (int sz in new[] { -1, 1 }) foreach (int sy in new[] { -1, 1 }) foreach (int sx in new[] { -1, 1 }) { p.Add(cx + sx * h); p.Add(cy + sy * h); p.Add(cz + sz * h); }
        return p.ToArray();
    }
    static readonly int[] BoxIndices = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5 };
    static (float[] pos, int[] idx) Grid(int n, float ox, float oy)
    {
        var pos = new List<float>(); var idx = new List<int>();
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) { pos.Add(ox + (float)i / n); pos.Add(oy + (float)j / n); pos.Add(0); }
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) { int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1; idx.AddRange(new[] { a, b, d, a, d, c }); }
        return (pos.ToArray(), idx.ToArray());
    }
    static HafModel Link(HafModel m) { for (int i = 0; i < m.Nodes.Count; i++) foreach (var c in m.Nodes[i].Children) m.Nodes[c].Parent = i; return m; }
    static string Vis(VehicleProbe.Result r, string name) => r.Parts.Single(p => p.Name == name).Vis.ToString();

    [Fact]
    public void A_part_every_ray_from_which_meets_geometry_is_interior_and_its_box_is_not()
    {
        // the "visibility" fixture, Blender: Box 1, Core 0, Outside 1
        var m = new HafModel();
        m.Meshes.Add(Mesh("box", Box(0, 0, 0, 2), BoxIndices));
        var (gp, gi) = Grid(8, -0.5f, -0.5f); m.Meshes.Add(Mesh("core", gp, gi));
        m.Meshes.Add(Mesh("outside", new float[] { 10, 0, 0, 11, 0, 0, 10, 1, 0 }, new[] { 0, 1, 2 }));
        m.Nodes.Add(new HafNode { Name = "Box", Mesh = 0 }); m.Nodes.Add(new HafNode { Name = "Core", Mesh = 1 }); m.Nodes.Add(new HafNode { Name = "Outside", Mesh = 2 });
        var r = VehicleProbe.Run(Link(m));
        Assert.Equal("1 0 1", Vis(r, "Box") + " " + Vis(r, "Core") + " " + Vis(r, "Outside"));
    }

    [Fact]
    public void The_normal_ray_is_the_files_normal_or_the_computed_one_and_can_be_the_only_escape()
    {
        // the "visibility" fixture's Normal, Sideways, Computed and ComputedFlipped: a triangle inside 15 shields - one across
        // each fixed direction, one opposite the gap - with the gap in the direction Blender (1, 2, 0) = glTF (1, 0, -2).
        // Blender: file normals along it 1; along +X 0; no NORMAL and wound so the computed face normal points into the gap 1;
        // wound the other way 0; the triangle twice with the second face wound against the first 1 (Blender's validate keeps
        // the first face only - external review of PR #115: both summed to no normal here), the flipped one first 0.
        var m = new HafModel();
        var dirs = new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1), (1, 1, 1), (1, 1, -1), (1, -1, 1), (1, -1, -1), (-1, 1, 1), (-1, 1, -1), (-1, -1, 1), (-1, -1, -1), (-1, -2, 0) };
        foreach (var (ox, oy, oz) in dirs)
        {
            double gx = ox, gy = oz, gz = -oy; double l = Math.Sqrt(gx * gx + gy * gy + gz * gz); gx /= l; gy /= l; gz /= l;
            double cx = 30 + gx * 5, cy = gy * 5, cz = gz * 5;
            double ax = Math.Abs(gy) < 0.9 ? 0 : 1, ay = Math.Abs(gy) < 0.9 ? 1 : 0, az = 0;
            double ux = gy * az - gz * ay, uy = gz * ax - gx * az, uz = gx * ay - gy * ax; l = Math.Sqrt(ux * ux + uy * uy + uz * uz); ux /= l; uy /= l; uz /= l;
            double vx = gy * uz - gz * uy, vy = gz * ux - gx * uz, vz = gx * uy - gy * ux;
            var q = new List<float>();
            foreach (var (s1, s2) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }) { q.Add((float)(cx + s1 * 1.5 * ux + s2 * 1.5 * vx)); q.Add((float)(cy + s1 * 1.5 * uy + s2 * 1.5 * vy)); q.Add((float)(cz + s1 * 1.5 * uz + s2 * 1.5 * vz)); }
            m.Meshes.Add(Mesh("shield", q.ToArray(), new[] { 0, 1, 2, 0, 2, 3 }));
            m.Nodes.Add(new HafNode { Name = "Shield" + m.Meshes.Count, Mesh = m.Meshes.Count - 1 });
        }
        float[] tri = { 30, 0, 0, 30.2f, 0, 0, 30, 0.2f, 0 };
        float s5 = (float)(1 / Math.Sqrt(5)), s5b = (float)(-2 / Math.Sqrt(5));
        m.Meshes.Add(Mesh("normal", tri, new[] { 0, 1, 2 }, new[] { s5, 0, s5b, s5, 0, s5b, s5, 0, s5b }));
        m.Nodes.Add(new HafNode { Name = "Normal", Mesh = m.Meshes.Count - 1 });
        m.Meshes.Add(Mesh("sideways", tri, new[] { 0, 1, 2 }, new float[] { 1, 0, 0, 1, 0, 0, 1, 0, 0 }));
        m.Nodes.Add(new HafNode { Name = "Sideways", Mesh = m.Meshes.Count - 1 });
        float[] tilted = { 30, 0, 0, 30, 0.2f, 0, 30 + 0.4f / (float)Math.Sqrt(5), 0, 0.2f / (float)Math.Sqrt(5) };   // (p1 - p0) x (p2 - p0) = (1, 0, -2) / sqrt 5: into the gap
        m.Meshes.Add(Mesh("computed", tilted, new[] { 0, 1, 2 }));
        m.Nodes.Add(new HafNode { Name = "Computed", Mesh = m.Meshes.Count - 1 });
        m.Meshes.Add(Mesh("computedflipped", tilted, new[] { 0, 2, 1 }));
        m.Nodes.Add(new HafNode { Name = "ComputedFlipped", Mesh = m.Meshes.Count - 1 });
        m.Meshes.Add(Mesh("computedtwin", tilted, new[] { 0, 1, 2, 0, 2, 1 }));          // Blender keeps the first of two faces over the same vertices
        m.Nodes.Add(new HafNode { Name = "ComputedTwin", Mesh = m.Meshes.Count - 1 });
        m.Meshes.Add(Mesh("computedtwinflipped", tilted, new[] { 0, 2, 1, 0, 1, 2 }));
        m.Nodes.Add(new HafNode { Name = "ComputedTwinFlipped", Mesh = m.Meshes.Count - 1 });
        var r = VehicleProbe.Run(Link(m));
        Assert.Equal("1", Vis(r, "Normal")); Assert.Equal("0", Vis(r, "Sideways"));
        Assert.Equal("1", Vis(r, "Computed")); Assert.Equal("0", Vis(r, "ComputedFlipped"));
        Assert.Equal("1", Vis(r, "ComputedTwin")); Assert.Equal("0", Vis(r, "ComputedTwinFlipped"));
        Assert.All(r.Parts.Where(p => p.Name.StartsWith("Shield")), p => Assert.Equal(1, p.Vis));
    }

    [Fact]
    public void A_skinned_vertexs_normal_ray_is_its_file_normal_skinned_into_the_bind_pose()
    {
        // the "visibility_skinned" fixture, Blender: TurnedMesh 1, StillMesh 0 - the same +X file normal, a joint turned
        // 63.43 degrees about glTF Y takes it to the gap (external review of PR #115: the normal was left as the file's)
        var m = new HafModel();
        var dirs = new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1), (1, 1, 1), (1, 1, -1), (1, -1, 1), (1, -1, -1), (-1, 1, 1), (-1, 1, -1), (-1, -1, 1), (-1, -1, -1), (-1, -2, 0) };
        foreach (var (ox, oy, oz) in dirs)
        {
            double gx = ox, gy = oz, gz = -oy; double l = Math.Sqrt(gx * gx + gy * gy + gz * gz); gx /= l; gy /= l; gz /= l;
            double cx = gx * 5, cy = gy * 5, cz = gz * 5;
            double ax = Math.Abs(gy) < 0.9 ? 0 : 1, ay = Math.Abs(gy) < 0.9 ? 1 : 0, az = 0;
            double ux = gy * az - gz * ay, uy = gz * ax - gx * az, uz = gx * ay - gy * ax; l = Math.Sqrt(ux * ux + uy * uy + uz * uz); ux /= l; uy /= l; uz /= l;
            double vx = gy * uz - gz * uy, vy = gz * ux - gx * uz, vz = gx * uy - gy * ux;
            var q = new List<float>();
            foreach (var (s1, s2) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }) { q.Add((float)(cx + s1 * 1.5 * ux + s2 * 1.5 * vx)); q.Add((float)(cy + s1 * 1.5 * uy + s2 * 1.5 * vy)); q.Add((float)(cz + s1 * 1.5 * uz + s2 * 1.5 * vz)); }
            m.Meshes.Add(Mesh("shield", q.ToArray(), new[] { 0, 1, 2, 0, 2, 3 }));
            m.Nodes.Add(new HafNode { Name = "Shield" + m.Meshes.Count, Mesh = m.Meshes.Count - 1 });
        }
        HafMesh Skinned(string name)
        {
            var me = Mesh(name, new float[] { 0, 0, 0, 0.2f, 0, 0, 0, 0.2f, 0 }, new[] { 0, 1, 2 }, new float[] { 1, 0, 0, 1, 0, 0, 1, 0, 0 });
            me.Primitives[0].Joints = new ushort[12]; me.Primitives[0].Weights = new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
            return me;
        }
        m.Meshes.Add(Skinned("turned")); m.Meshes.Add(Skinned("still"));
        int n0 = m.Nodes.Count; double th = Math.Atan2(2, 1);
        var rig = new HafNode { Name = "Rig" }; rig.Children.Add(n0 + 1); m.Nodes.Add(rig);
        m.Nodes.Add(new HafNode { Name = "Turned", Rotation = new[] { 0, Math.Sin(th / 2), 0, Math.Cos(th / 2) } });
        var rig2 = new HafNode { Name = "Rig2" }; rig2.Children.Add(n0 + 3); m.Nodes.Add(rig2);
        m.Nodes.Add(new HafNode { Name = "StillJoint" });
        m.Nodes.Add(new HafNode { Name = "TurnedMesh", Mesh = m.Meshes.Count - 2, Skin = 0 });
        m.Nodes.Add(new HafNode { Name = "StillMesh", Mesh = m.Meshes.Count - 1, Skin = 1 });
        m.Skins.Add(new HafSkin { Name = "Skin", Joints = new[] { n0 + 1 } }); m.Skins.Add(new HafSkin { Name = "Skin2", Joints = new[] { n0 + 3 } });
        var r = VehicleProbe.Run(Link(m));
        Assert.Equal("1", Vis(r, "TurnedMesh")); Assert.Equal("0", Vis(r, "StillMesh"));
    }

    [Fact]
    public void A_separated_island_holds_its_vertices_in_the_order_Blenders_edge_walk_finds_them()
    {
        // the order fixture (scratch, 2026-10-02): ONE mesh, island A = vertices 0..5 with faces (3,4,5), (0,1,2), (1,2,4),
        // island B = vertices 6..9 with faces (8,9,6), (6,7,8). Blender's separated parts: A keeps 0..5 (it stays in the
        // original object); B comes out as 6, 8, 9, 7 - the walk from 6 round its edges in creation order ((6,8) from
        // the first face's last corner, (9,6), then (6,7)). The Ehrhardt's sliver quads (faces (a,b,c), (a,c,d)) came
        // out a, c, b, d the same way.
        var pos = new float[30]; for (int i = 0; i < 10; i++) { pos[i * 3] = i; pos[i * 3 + 2] = (i % 3) * 0.5f; }
        var mesh = Mesh("m", pos, new[] { 3, 4, 5, 0, 1, 2, 1, 2, 4, 8, 9, 6, 6, 7, 8 });
        var islands = VehicleProbe.BlenderIslands(mesh);
        Assert.Equal(2, islands.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, islands[0].Verts);
        Assert.Equal(new[] { 6, 8, 9, 7 }, islands[1].Verts);
        var sliver = Mesh("s", new float[24], new[] { 0, 1, 2, 1, 2, 3, 4, 5, 6, 4, 6, 7 });   // a first island keeps the name; the second: (a,b,c), (a,c,d)
        Assert.Equal(new[] { 4, 6, 5, 7 }, VehicleProbe.BlenderIslands(sliver)[1].Verts);
    }

    [Fact]
    public void A_files_loose_edges_come_before_the_faces_edges_whatever_bucket_they_fall_in()
    {
        // from the source alone (mesh_calc_edges keeps a mesh's existing edges FIRST, in their order, and only buckets the
        // new ones; no file of the populations has a line primitive on a split mesh): 1,000 faces make eight buckets; a
        // line primitive's edges (5,7), (3,5), (4,5) - its vertices 3..7 - stay in that order, so vertex 5's edges come
        // (5,7), (3,5), (4,5) and the walk from 3 finds 3, 5, 7, 4. Bucketed by their lower vertex they would read
        // (3,5), (4,5), (5,7) and the walk 3, 5, 4, 7.
        var tris = new HafMesh { Name = "m" };
        var faces = new int[3000]; for (int i = 0; i < 1000; i++) { faces[i * 3] = 0; faces[i * 3 + 1] = 1; faces[i * 3 + 2] = 2; }
        tris.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[9], Indices = faces, Mode = 4 });
        tris.Primitives.Add(new HafPrimitive { VertexCount = 5, Positions = new float[15], Indices = new[] { 2, 4, 0, 2, 1, 2 }, Mode = 1 });
        var islands = VehicleProbe.BlenderIslands(tris);
        Assert.Equal(2, islands.Count);
        Assert.Equal(new[] { 0, 2, 4, 1 }, islands[1].Verts);
    }

    [Fact]
    public void A_split_grids_samples_come_from_Blenders_order_and_its_verdict_from_them()
    {
        // the "visibility_split" fixture, Blender: Hull 1 (the box), Hull.001 0 (a 40 x 40 grid inside: 1,681 vertices,
        // 3,200 faces - the 8-bucket edge array), Hull.002 1
        var box = Box(0, 0, 0, 2); var (gp, gi) = Grid(40, -0.5f, -0.5f);
        var pos = box.Concat(gp).Concat(new float[] { 10, 0, 0, 11, 0, 0, 10, 1, 0 }).ToArray();
        var idx = BoxIndices.Concat(gi.Select(i => i + 8)).Concat(new[] { 8 + gp.Length / 3, 9 + gp.Length / 3, 10 + gp.Length / 3 }).ToArray();
        var m = new HafModel(); m.Meshes.Add(Mesh("all", pos, idx)); m.Nodes.Add(new HafNode { Name = "Hull", Mesh = 0 });
        var r = VehicleProbe.Run(Link(m));
        Assert.True(r.Split);
        Assert.Equal(new[] { "Hull|8|1", "Hull.001|1681|0", "Hull.002|3|1" }, r.Parts.Select(p => p.Name + "|" + p.Verts + "|" + p.Vis));
    }

    [Fact]
    public void The_ray_test_counts_a_hit_on_an_edge_and_nothing_behind_the_ray()
    {
        var bvh = new VehicleProbe.TriangleBvh(new float[] { 0, 0, 5, 2, 0, 5, 0, 2, 5 }, new[] { 0, 1, 2 });
        Assert.True(bvh.AnyHit(0.5, 0.5, 0, 0, 0, 1));      // through the face
        Assert.True(bvh.AnyHit(1, 0, 0, 0, 0, 1));          // on an edge
        Assert.True(bvh.AnyHit(0, 0, 0, 0, 0, 1));          // on a vertex
        Assert.False(bvh.AnyHit(1.5, 1.5, 0, 0, 0, 1));     // past the hypotenuse
        Assert.False(bvh.AnyHit(0.5, 0.5, 6, 0, 0, 1));     // the triangle is behind the ray
        Assert.True(bvh.AnyHit(0.5, 0.5, 6, 0, 0, -1));     // and in front of its reverse, from either side
        Assert.True(bvh.AnyHit(0.5, 0.5, 5, 0, 0, 1));      // t = 0 counts
    }
}
