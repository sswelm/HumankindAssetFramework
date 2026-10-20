using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
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
    public void A_high_valence_cone_keeps_its_verdict_without_a_quadratic_edge_scan()
    {
        const int faces = 64000;
        var mesh = new VehicleProbe.PartMesh { Count = faces + 1, Local = new float[3 * (faces + 1)], Tris = new int[3 * faces] };
        for (int i = 0; i < faces; i++)
        {
            double angle = 2 * Math.PI * i / faces;
            mesh.Local[3 * (i + 1)] = 1f; mesh.Local[3 * (i + 1) + 1] = (float)Math.Cos(angle); mesh.Local[3 * (i + 1) + 2] = (float)Math.Sin(angle);
            mesh.Tris[3 * i] = 0; mesh.Tris[3 * i + 1] = i + 1; mesh.Tris[3 * i + 2] = (i + 1) % faces + 1;
        }
        var part = new VehicleProbe.Part();
        var inside = typeof(VehicleProbe).GetMethod("InsideOut", BindingFlags.Static | BindingFlags.NonPublic);
        var args = new object[] { new[] { part }, new[] { mesh }, new[] { Identity() } };
        var timer = Stopwatch.StartNew();
        inside.Invoke(null, args); Assert.Equal(1, part.Flip);
        for (int i = 0; i < faces; i++) { int b = mesh.Tris[3 * i + 1]; mesh.Tris[3 * i + 1] = mesh.Tris[3 * i + 2]; mesh.Tris[3 * i + 2] = b; }
        inside.Invoke(null, args); Assert.Equal(0, part.Flip);
        // Broad regression budget, not a throughput benchmark: pairwise matching of the hub's 128k edges takes tens of seconds.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "High-valence edge matching took " + timer.Elapsed);
    }

    static (VehicleProbe.PartMesh mesh, long bytes) AllocatePart(HafModel m, int[] selected)
    {
        VehicleProbe.PartMesh Build() => VehicleProbe.BuildPartMesh(m, 0, 0, selected, null, Identity(), null);
        Build(); // warm the path before counting; .NET Framework 4.8 supplies this API beyond the project's 4.7.1 reference surface
        var counterMethod = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(counterMethod);
        var allocated = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), counterMethod);
        long before = allocated(); var mesh = Build();
        return (mesh, allocated() - before);
    }

    [Fact]
    public void A_tiny_loose_part_does_not_reserve_its_entire_source_primitive_faces()
    {
        const int faces = 100000;
        var m = new HafModel(); var mesh = new HafMesh();
        var p = new HafPrimitive { VertexCount = 3 * faces, Positions = new float[9 * faces], Indices = Enumerable.Range(0, 3 * faces).ToArray(), Mode = 4 };
        for (int f = 0; f < faces; f++) { p.Positions[9 * f] = 2 * f; p.Positions[9 * f + 3] = 2 * f + 1; p.Positions[9 * f + 6] = 2 * f; p.Positions[9 * f + 7] = 1; p.Positions[9 * f + 8] = 1; }
        mesh.Primitives.Add(p); m.Meshes.Add(mesh); m.Nodes.Add(new HafNode { Mesh = 0 });
        var result = AllocatePart(m, new[] { 0, 1, 2 });
        Assert.Equal(3, result.mesh.Count); Assert.Equal(new[] { 0, 1, 2 }, result.mesh.Tris);
        // The existing full-vertex rank lookup is allowed; reserving triangle/material/hash buffers for every other island is not.
        Assert.InRange(result.bytes, 0L, 4L * p.VertexCount + 128 * 1024);
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Non_polygon_primitives_do_not_reserve_face_storage(int mode)
    {
        var m = new HafModel(); var mesh = new HafMesh();
        mesh.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[] { 0,0,0, 1,0,0, 0,1,1 }, Indices = Enumerable.Range(0, 100000).Select(i => i % 3).ToArray(), Mode = mode });
        m.Meshes.Add(mesh); m.Nodes.Add(new HafNode { Mesh = 0 });
        var result = AllocatePart(m, null);
        Assert.Equal(3, result.mesh.Count); Assert.Empty(result.mesh.Tris);
        Assert.InRange(result.bytes, 0L, 128 * 1024);
    }

    [Fact]
    public void A_dense_selected_part_grows_its_face_set_and_still_keeps_the_first_twin()
    {
        const int vertices = 16;
        var m = new HafModel(); var mesh = new HafMesh(); var faces = new List<int>();
        var p = new HafPrimitive { VertexCount = vertices, Positions = new float[3 * vertices], Mode = 4 };
        for (int i = 0; i < vertices; i++) { p.Positions[3 * i] = i; p.Positions[3 * i + 1] = i * i % 17; p.Positions[3 * i + 2] = i * i * i % 19; }
        for (int a = 0; a < vertices; a++) for (int b = a + 1; b < vertices; b++) for (int c = b + 1; c < vertices; c++) faces.AddRange(new[] { a, b, c, c, b, a });
        p.Indices = faces.ToArray(); mesh.Primitives.Add(p); m.Meshes.Add(mesh); m.Nodes.Add(new HafNode { Mesh = 0 });
        int limit = VehicleProbe.PackedFaceLimit;
        try
        {
            VehicleProbe.PackedFaceLimit = 0; var tuples = VehicleProbe.BuildPartMesh(m, 0, 0, null, null, Identity(), null);
            VehicleProbe.PackedFaceLimit = 1 << 21; var selected = VehicleProbe.BuildPartMesh(m, 0, 0, Enumerable.Range(0, vertices).ToArray(), null, Identity(), null);
            Assert.Equal(560 * 3, selected.Tris.Length); // far more faces than the selected vertex count estimates
            Assert.Equal(tuples.Tris, selected.Tris); Assert.Equal(tuples.World, selected.World); Assert.Equal(tuples.Normal, selected.Normal);
        }
        finally { VehicleProbe.PackedFaceLimit = limit; }
    }

    [Fact]
    public void The_packed_face_set_drops_the_same_twins_as_the_tuple_set()
    {
        var m = Grid(40, twins: true);
        int limit = VehicleProbe.PackedFaceLimit;
        try
        {
            VehicleProbe.PackedFaceLimit = 1 << 21; var packed = VehicleProbe.BuildPartMesh(m, 0, -1, null, null, Identity(), null);
            VehicleProbe.PackedFaceLimit = 0; var tuples = VehicleProbe.BuildPartMesh(m, 0, -1, null, null, Identity(), null);
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
