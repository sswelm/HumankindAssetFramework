using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

// The probe's parallel paths (2026-10-19): the packed face set against the tuple set it replaces on small parts, the BVH built
// subtree by subtree on several cores, the inside-out islands bucketed by their lower vertex. Each must give what the serial code gave.
public class VehicleProbeParallelTests
{
    // a grid of (size+1)^2 vertices and 2 size^2 faces in the XY plane at z = 0, plus the same faces again (twins, some reversed)
    static HafModel Grid(int size, bool twins)
    {
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene()); m.Scenes[0].Nodes.Add(0);
        var positions = new List<float>(); var faces = new List<int>();
        for (int y = 0; y <= size; y++) for (int x = 0; x <= size; x++) positions.AddRange(new[] { (float)x, (float)y, (float)((x * 7 + y * 3) % 5) * .01f });
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            int a = y * (size + 1) + x, b = a + 1, c = a + size + 1, d = c + 1;
            faces.AddRange(new[] { a, b, d, a, d, c });
            if (twins) faces.AddRange((x + y) % 2 == 0 ? new[] { a, b, d } : new[] { d, b, a });   // a twin, half of them wound the other way: mesh.validate keeps the FIRST
        }
        var mesh = new HafMesh { Name = "Grid" };
        mesh.Primitives.Add(new HafPrimitive { VertexCount = (size + 1) * (size + 1), Positions = positions.ToArray(), Indices = faces.ToArray(), Mode = 4 });
        m.Meshes.Add(mesh); m.Nodes.Add(new HafNode { Name = "Grid", Mesh = 0 });
        return m;
    }
    static float[] Identity() { var r = new float[16]; r[0] = r[5] = r[10] = r[15] = 1f; return r; }

    [Fact]
    public void The_packed_face_set_drops_the_same_twins_as_the_tuple_set()
    {
        var m = Grid(40, twins: true);
        Func<HafPrimitive, int, double[]> position = (p, v) => new double[] { p.Positions[v * 3], p.Positions[v * 3 + 1], p.Positions[v * 3 + 2] };
        Func<HafPrimitive, int, double[]> normal = (p, v) => null;
        int limit = VehicleProbe.PackedFaceLimit;
        try
        {
            VehicleProbe.PackedFaceLimit = 1 << 21; var packed = VehicleProbe.BuildPartMesh(m, 0, -1, null, position, normal, Identity(), null);
            VehicleProbe.PackedFaceLimit = 0; var tuples = VehicleProbe.BuildPartMesh(m, 0, -1, null, position, normal, Identity(), null);
            Assert.Equal(2 * 40 * 40, packed.Tris.Length / 3);   // the twins are gone, the first of each pair stays
            Assert.Equal(tuples.Tris, packed.Tris); Assert.Equal(tuples.World, packed.World); Assert.Equal(tuples.Normal, packed.Normal);
        }
        finally { VehicleProbe.PackedFaceLimit = limit; }
    }

    [Fact]
    public void The_parallel_BVH_answers_rays_like_the_geometry()
    {
        // 2 x 200 x 200 = 80,000 triangles: past the grain at which the subtrees build on several cores
        var m = Grid(200, twins: false); var p = m.Meshes[0].Primitives[0];
        var bvh = new VehicleProbe.TriangleBvh(p.Positions, p.Indices);
        var rnd = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            float x = (float)(rnd.NextDouble() * 200), y = (float)(rnd.NextDouble() * 200);
            Assert.True(bvh.AnyHit(x, y, 5f, 0f, 0f, -1f), "a ray down onto the plate hits it");
            Assert.False(bvh.AnyHit(x, y, 5f, 0f, 0f, 1f), "a ray up from above the plate escapes");
            Assert.False(bvh.AnyHit(x, y, -5f, 0f, 0f, -1f), "a ray down from below the plate escapes");
            Assert.True(bvh.AnyHit(x, y, -5f, 0f, 0f, 1f), "a ray up from below the plate hits it");
        }
        Assert.False(bvh.AnyHit(250f, 250f, 5f, 0f, 0f, -1f), "beside the plate: nothing");
        Assert.False(bvh.AnyHit(-1f, 100f, 0f, -1f, 0f, 0f), "away from the plate along its plane: nothing");
    }

    [Fact]
    public void Inside_out_islands_are_found_through_the_bucketed_edges()
    {
        // two closed boxes side by side, the second wound inwards: two islands, one of them interior-facing
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene()); m.Scenes[0].Nodes.Add(0);
        var positions = new List<float>(); var faces = new List<int>();
        void Box(float ox, bool inward)
        {
            int b = positions.Count / 3;
            foreach (var (x, y, z) in new[] { (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1) }) positions.AddRange(new[] { ox + x, (float)y, (float)z });
            int[][] quads = { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 } };
            foreach (var q in quads)
            {
                int[] t = inward ? new[] { q[0], q[2], q[1], q[0], q[3], q[2] } : new[] { q[0], q[1], q[2], q[0], q[2], q[3] };
                faces.AddRange(t.Select(v => b + v));
            }
        }
        Box(0f, false); Box(10f, true);
        var mesh = new HafMesh { Name = "Boxes" };
        mesh.Primitives.Add(new HafPrimitive { VertexCount = 16, Positions = positions.ToArray(), Indices = faces.ToArray(), Mode = 4 });
        m.Meshes.Add(mesh); m.Nodes.Add(new HafNode { Name = "Boxes", Mesh = 0 });
        // a second object, so the boxes stay ONE part of two islands (a single-mesh source is split into its loose parts)
        var other = new HafMesh { Name = "Other" };
        other.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[] { 20, 0, 0, 21, 0, 0, 20, 0, 1 }, Indices = new[] { 0, 1, 2 }, Mode = 4 });
        m.Meshes.Add(other); m.Nodes.Add(new HafNode { Name = "Other", Mesh = 1 }); m.Scenes[0].Nodes.Add(1);
        var r = VehicleProbe.Run(m);
        Assert.Equal(2, r.Parts.Count);
        Assert.Equal(1, r.Parts.Single(x => x.Name == "Boxes").Flip);
    }
}
