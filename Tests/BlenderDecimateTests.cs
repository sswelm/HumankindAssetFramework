using System;
using System.Collections.Generic;
using Xunit;

/// <summary>Blender's Decimate COLLAPSE in C# (BlenderDecimate, BlenderReduce, BlenderColor): each rule on a case where the
/// rule alone decides the result, with the expected values walked by hand from Blender 5.1.2's source or measured on the
/// hardware Blender ran on. tools/decimate_drill.sh holds the whole collapse to Blender's own on real meshes.</summary>
public class BlenderDecimateTests
{
    static float F(uint bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
    static uint U(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);

    [Fact]
    public void The_heap_pops_equal_costs_last_inserted_first_because_heap_up_climbs_past_equal_parents()
    {
        // BLI_heap: heap_up stops only below a STRICTLY smaller parent, heap_down descends only into a strictly smaller child.
        // A(1) B(1) C(1): [A] -> B climbs past A -> [B, A] -> C climbs past B -> [C, A, B]; pop C, then [B, A], pop B, then A
        var h = new BlenderDecimate.Heap(4);
        h.Insert(1f, 100); h.Insert(1f, 101); h.Insert(1f, 102);
        Assert.Equal(102, h.PopMin()); Assert.Equal(101, h.PopMin()); Assert.Equal(100, h.PopMin());
        Assert.True(h.IsEmpty);
    }

    [Fact]
    public void The_heap_orders_by_value_and_removes_and_updates_nodes_in_place()
    {
        var h = new BlenderDecimate.Heap(1);   // grows past its reserve
        int a = h.Insert(5f, 0), b = h.Insert(3f, 1), c = h.Insert(4f, 2), d = h.Insert(1f, 3);
        Assert.Equal(1f, h.TopValue);
        h.Remove(d);                            // swapped up to the root, then popped
        Assert.Equal(3f, h.TopValue);
        h.UpdatePtr(a, 0.5f, 7);                // lowered: climbs to the root, carrying the new pointer
        Assert.Equal(7, h.PopMin());
        int n = -1; h.InsertOrUpdate(ref n, 3.5f, 9); Assert.NotEqual(-1, n);
        h.InsertOrUpdate(ref c, float.MaxValue, 2);   // raised to COST_INVALID: sinks
        Assert.Equal(1, h.PopMin()); Assert.Equal(9, h.PopMin()); Assert.Equal(2, h.PopMin());
        Assert.True(h.IsEmpty);
    }

    [Fact]
    public void The_quadric_of_three_planes_is_minimal_at_their_intersection()
    {
        // planes x = 1, y = 2, z = 3 (d = -1, -2, -3): the summed quadric's optimum is the point (1, 2, 3), its error there 0
        var q = BlenderDecimate.Quadric.FromPlane(1, 0, 0, -1);
        q.Add(BlenderDecimate.Quadric.FromPlane(0, 1, 0, -2));
        q.Add(BlenderDecimate.Quadric.FromPlane(0, 0, 1, -3));
        Assert.True(q.Optimize(out double x, out double y, out double z, 1e-8));
        Assert.Equal(1.0, x); Assert.Equal(2.0, y); Assert.Equal(3.0, z);
        Assert.Equal(0.0, q.Evaluate(x, y, z));
        Assert.Equal(1.0 + 4.0 + 9.0, q.Evaluate(0, 0, 0));
        // two parallel planes leave the tensor singular: no optimum (the collapse then takes the edge's midpoint)
        var flat = BlenderDecimate.Quadric.FromPlane(0, 0, 1, 0); flat.Add(BlenderDecimate.Quadric.FromPlane(0, 0, 1, -1));
        Assert.False(flat.Optimize(out _, out _, out _, 1e-8));
    }

    [Fact]
    public void The_stop_count_is_the_float32_product_truncated()
    {
        Assert.Equal(19245, BlenderDecimate.FaceTarget(57737, F(0x3eaaa927)));   // 19,244.9996 in double; Blender stops at 19,245
        Assert.Equal(50, BlenderDecimate.FaceTarget(100, 0.5f));
        Assert.False(BlenderDecimate.WouldRun(100, 1.0f)); Assert.False(BlenderDecimate.WouldRun(3, 0.5f)); Assert.True(BlenderDecimate.WouldRun(4, 0.5f));
    }

    [Fact]
    public void Apply_merges_the_uvs_of_a_vertex_within_twelve_ulps_onto_its_lowest_corner()
    {
        // four corners on vertex 0 (corners 0, 2, 4, 6) and four on vertex 1 (1, 3, 5, 7); one UV layer
        var uv = new float[16];
        void Set(int c, float u, float v) { uv[2 * c] = u; uv[2 * c + 1] = v; }
        float one = 1.0f;
        Set(0, 0.5f, one); Set(2, 0.5f, F(U(one) - 12)); Set(4, 0.5f, F(U(one) - 13)); Set(6, 0.25f, one);
        Set(1, 0.1f, 0.2f); Set(3, 0.1f, 0.2f); Set(5, F(U(0.1f) + 3), 0.2f); Set(7, 0.9f, 0.9f);
        BlenderReduce.MergeUvsForApply(2, new[] { 0, 1, 0, 1, 0, 1, 0, 1 }, new List<float[]> { uv });
        Assert.Equal(one, uv[5]);                 // corner 2: 12 ulps below 1.0 - snapped to corner 0
        Assert.Equal(F(U(one) - 13), uv[9]);      // corner 4: 13 ulps - apart, kept
        Assert.Equal(0.25f, uv[12]);              // corner 6: another island
        Assert.Equal(0.1f, uv[10]);               // corner 5: 3 ulps from corner 1 - snapped
        Assert.Equal(0.9f, uv[14]);
    }

    [Fact]
    public void The_rsqrtps_table_reproduces_the_measured_instruction()
    {
        // read off the AMD Ryzen 7 7800X3D (Zen 4) with .NET 8's Sse.ReciprocalSqrtScalar, 2026-10-03
        Assert.Equal(0x3f7ff800u, U(BlenderColor.Rsqrtps(1f)));
        Assert.Equal(0x3f350000u, U(BlenderColor.Rsqrtps(2f)));
        Assert.Equal(0x3efff800u, U(BlenderColor.Rsqrtps(4f)));
        Assert.Equal(0x3ffff800u, U(BlenderColor.Rsqrtps(0.25f)));
        Assert.Equal(0x3f000000u, U(BlenderColor.Rsqrtps(F(0x407fffff))));
        Assert.True(float.IsPositiveInfinity(BlenderColor.Rsqrtps(0f)));
        Assert.True(float.IsPositiveInfinity(BlenderColor.Rsqrtps(1e-40f)));   // a denormal is read as zero
    }

    [Fact]
    public void Linear_colours_become_the_srgb_floats_and_bytes_Blenders_sse_path_gives()
    {
        // linearrgb_to_srgb_v4_simd run with the real SSE instructions on the same CPU (.NET 8 intrinsics), 2026-10-03
        var cases = new (uint c, uint srgb, byte b)[] {
            (0x00000000, 0x00000000, 0), (0x3a83126f, 0x3c53ae69, 3),
            (0x3b4d2c6e, 0x3d25ad7a, 10),   // 0.0031307: the linear branch, 12.92 c
            (0x3b4d2e1c, 0x3d25acf4, 10),   // 0.0031308: the pow branch - and SMALLER than its neighbour's
            (0x3c23d70a, 0x3dcc7ca3, 25), (0x3e4ccccd, 0x3ef812fa, 124), (0x3f000000, 0x3f3c3f0e, 188),
            (0x3f4ccccd, 0x3f680307, 231), (0x3f7d70a4, 0x3f7edd27, 254), (0x3f800000, 0x3f7ffe25, 255), (0x3fc00000, 0x3f98dc24, 255) };
        foreach (var (c, srgb, b) in cases)
        {
            float s = BlenderColor.LinearToSrgb(F(c));
            Assert.Equal(srgb, U(s));
            Assert.Equal(b, BlenderColor.UnitToByte(s));
        }
        var bytes = new byte[4];
        BlenderColor.ToBytes(0.5f, 0f, 1f, 0.5f, bytes, 0);
        Assert.Equal(new byte[] { 188, 0, 255, 128 }, bytes);   // the alpha is stored linear
    }

    [Fact]
    public void Cosf_and_sinf_are_the_c_runtimes_own_where_it_has_them()
    {
        Assert.Equal(1f, BlenderTrig.Cosf(0f)); Assert.Equal(0f, BlenderTrig.Sinf(0f));
        float x = F(0x3afdfdfd);   // the 64-bit runtime's cosf gives 0x3f7fffe0 here, the rounded double cosine 0x3f7fffe1 (measured 2026-10-05)
        Assert.Equal(BlenderTrig.Exact ? 0x3f7fffe0u : 0x3f7fffe1u, U(BlenderTrig.Cosf(x)));
        if (!BlenderTrig.Exact)
        {
            var m = BentGrid(2, true, false);
            Assert.Contains("cosf", BlenderReduce.FallbackReason(m, 0));   // a mesh with normals is kept for Blender
            Assert.Null(BlenderReduce.FallbackReason(BentGrid(2, false, false), 0));
        }
    }

    static HafModel BentGrid(int n, bool normals, bool colours)
    {
        var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>(); var col = new List<float>(); var idx = new List<int>();
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                float x = (float)i / n, y = (float)j / n, z = (float)(0.3 * Math.Sin(2 * x) * Math.Cos(1.5 * y));
                pos.AddRange(new[] { x, z, -y }); nrm.AddRange(new[] { 0f, 1f, 0f }); uv.AddRange(new[] { x, y }); col.AddRange(new[] { x, 0.5f, y, 1f });
            }
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++) { int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1; idx.AddRange(new[] { a, d, b, a, c, d }); }
        var m = new HafModel(); var mesh = new HafMesh();
        mesh.Primitives.Add(new HafPrimitive { VertexCount = (n + 1) * (n + 1), Positions = pos.ToArray(), Normals = normals ? nrm.ToArray() : null, Uv0 = uv.ToArray(), Colors = colours ? col.ToArray() : null, Indices = idx.ToArray() });
        m.Meshes.Add(mesh);
        m.Nodes.Add(new HafNode { Name = "Grid", Mesh = 0 });
        return m;
    }

    [Fact]
    public void A_reduce_stops_at_the_target_keeps_every_face_a_triangle_of_live_vertices_and_is_deterministic()
    {
        var m = BentGrid(8, BlenderTrig.Exact, false);   // normals need the C runtime's cosf; without it the port declines them
        var names = BlenderNames.Compute(m);
        var r = BlenderReduce.Reduce(m, 0, 0.5f, names);
        Assert.Null(r.Fallback); Assert.True(r.Collapsed);
        Assert.InRange(r.FaceCount, 1, 64);           // 128 faces, target 64: a collapse removes one or two
        Assert.True(r.FaceCount >= 63);
        foreach (int v in r.Faces) Assert.InRange(v, 0, r.VertexCount - 1);
        for (int f = 0; f < r.FaceCount; f++) Assert.True(r.Faces[3 * f] != r.Faces[3 * f + 1] && r.Faces[3 * f + 1] != r.Faces[3 * f + 2] && r.Faces[3 * f] != r.Faces[3 * f + 2]);
        Assert.Single(r.Uv); Assert.Equal(6 * r.FaceCount, r.Uv[0].Length);
        Assert.Equal(BlenderTrig.Exact, r.CustomNormal != null);
        var again = BlenderReduce.Reduce(m, 0, 0.5f, names);
        Assert.Equal(r.Positions, again.Positions); Assert.Equal(r.Faces, again.Faces); Assert.Equal(r.Uv[0], again.Uv[0]);
        // ratio 1: the modifier returns the mesh as imported (UVs flipped to Blender's v, still merged as the apply merges them)
        var one = BlenderReduce.Reduce(m, 0, 1.0f, names);
        Assert.False(one.Collapsed); Assert.Equal(128, one.FaceCount); Assert.Equal(81, one.VertexCount);
    }

    [Fact]
    public void A_ninth_uv_or_colour_layer_is_not_imported_by_the_reduce()
    {
        var m = BentGrid(5, false, BlenderColor.TableKnown);
        var p = m.Meshes[0].Primitives[0];
        p.Uv1 = (float[])p.Uv0.Clone();
        p.UvMore = new List<float[]>();
        for (int i = 2; i < 9; i++) p.UvMore.Add((float[])p.Uv0.Clone());
        if (BlenderColor.TableKnown)
        {
            p.ColorMore = new List<float[]>();
            for (int i = 1; i < 9; i++) p.ColorMore.Add((float[])p.Colors.Clone());
        }
        var names = BlenderNames.Compute(m);
        foreach (float ratio in new[] { 1f, 0.5f, BlenderReduce.Ratio(50 / 3, 50) })
        {
            var nine = BlenderReduce.Reduce(m, 0, ratio, names);
            Assert.Null(nine.Fallback); Assert.Equal(8, nine.Uv.Count);
            if (BlenderColor.TableKnown) Assert.Equal(8, nine.Colors.Count);
            // Removing only the ignored ninth layers must leave geometry and every retained layer unchanged.
            var lastUv = p.UvMore[6]; p.UvMore.RemoveAt(6);
            float[] lastColor = null;
            if (BlenderColor.TableKnown) { lastColor = p.ColorMore[7]; p.ColorMore.RemoveAt(7); }
            var eight = BlenderReduce.Reduce(m, 0, ratio, names);
            Assert.Equal(eight.Positions, nine.Positions); Assert.Equal(eight.Faces, nine.Faces);
            for (int i = 0; i < 8; i++)
            {
                Assert.Equal(eight.Uv[i], nine.Uv[i]);
                if (BlenderColor.TableKnown) Assert.Equal(eight.Colors[i].bytes, nine.Colors[i].bytes);
            }
            p.UvMore.Add(lastUv);
            if (BlenderColor.TableKnown) p.ColorMore.Add(lastColor);
        }
    }

    [Fact]
    public void Default_threads_match_the_runtime_on_a_large_grid()
    {
        var m = BentGrid(25, false, false);
        var p = m.Meshes[0].Primitives[0];
        for (int v = 0; v < p.VertexCount; v++) p.Positions[3 * v + 1] = 0f;   // equal-cost edges expose order differences
        // the grid can show a wrong count: 4 and 8 buckets order its edges differently (Blender with -t 4 against its default
        // gave 350 against 351 vertices at a half on the decimate_limits fixture, verified 2026-10-05)
        Assert.Equal(4, BlenderMesh.ParallelMaps(1250, 4)); Assert.Equal(8, BlenderMesh.ParallelMaps(1250, 16));
        Assert.NotEqual(BlenderMesh.FromGltf(m, 0, 4).ValidEdges, BlenderMesh.FromGltf(m, 0, 16).ValidEdges);
        // on a machine of 8 or more threads the comparisons below cannot tell the runtime's count from a hard-coded 16 (the
        // defect they guard), so the defaults themselves are held: no entry point may carry a machine's count again
        foreach (var method in new[] { typeof(BlenderMesh).GetMethod("ParallelMaps"), typeof(BlenderMesh).GetMethod("FromGltf"), typeof(BlenderReduce).GetMethod("Reduce") })
        {
            var threads = Array.Find(method.GetParameters(), q => q.Name == "threads");
            Assert.NotNull(threads); Assert.True(threads.HasDefaultValue); Assert.Equal(0, threads.DefaultValue);
        }
        Assert.Equal(BlenderMesh.ParallelMaps(1250, Environment.ProcessorCount), BlenderMesh.ParallelMaps(1250));
        Assert.Equal(BlenderMesh.FromGltf(m, 0, Environment.ProcessorCount).ValidEdges, BlenderMesh.FromGltf(m, 0).ValidEdges);
        var names = BlenderNames.Compute(m);
        foreach (float ratio in new[] { 0.5f, BlenderReduce.Ratio(1250 / 3, 1250) })
        {
            var expected = BlenderReduce.Reduce(m, 0, ratio, names, Environment.ProcessorCount);
            var actual = BlenderReduce.Reduce(m, 0, ratio, names);
            Assert.Null(actual.Fallback);
            Assert.Equal(expected.Positions, actual.Positions); Assert.Equal(expected.Faces, actual.Faces);
            Assert.Equal(expected.Edges, actual.Edges);
        }
    }

    [Fact]
    public void The_reduce_names_what_it_keeps_for_Blender()
    {
        var m = BentGrid(2, false, false);
        m.Meshes[0].Primitives[0].MorphTargets = 1;
        Assert.Equal("morph targets", BlenderReduce.FallbackReason(m, 0));
        Assert.NotNull(BlenderReduce.Reduce(m, 0, 0.5f, BlenderNames.Compute(m)).Fallback);
        Assert.Equal(1.0f, BlenderReduce.Ratio(5000, 4000));    // prep_model never adds geometry
        Assert.Equal(0.001f, BlenderReduce.Ratio(1, 1000000));
    }
}
