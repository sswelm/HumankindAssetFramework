using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

// The native kernels (editor/HafNativeKernels.cs over editor/Plugins/x86_64/hafkernels.dll) against their managed twins,
// bit for bit, on meshes built to reach every branch the C++ ports: file normals and none (the true-normal fallback of the
// encode), sharp faces, cyclic and open fans, lone vertices, the clamp of an over-unit file normal, a mirrored matrix, and
// a mesh large enough for both sides' parallel ranges. The DLL is copied beside the test assembly by the csproj; a test
// host that cannot load it FAILS here (Available false), never skips: the managed path is the one every other test
// exercises, and a silently managed run would prove nothing about the DLL (tools/native_kernels_drill.sh holds it to the
// recipe sources the same way).
// Both native-kernel test classes flip the static HafNativeKernels.Available switch; xunit runs classes in parallel, so they share
// one collection and run one after the other (a loader assertion met the other class's managed window in a push gate, 2026-10-21).
[Collection("native kernels switch")]
public class NativeKernelsTests
{
    // The managed twins, reached by switching Available off for the call: the production methods read it once per call,
    // so both sides run in one host on the same arrays (HAF_NATIVE_KERNELS=0 in a second process is what the drill does).
    static void WithKernels(bool on, Action body)
    {
        bool was = HafNativeKernels.Available;
        HafNativeKernels.Available = on;
        try { body(); } finally { HafNativeKernels.Available = was; }
    }

    static (float[] P, int[] tris, float[] N) Grid(int w, int h, bool normals, int seed)
    {
        var rnd = new Random(seed);
        int nv = w * h; var P = new float[nv * 3]; var N = normals ? new float[nv * 3] : null;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int v = y * w + x;
            P[v * 3] = x * 0.37f + (float)rnd.NextDouble() * 0.05f; P[v * 3 + 1] = y * 0.41f - (float)rnd.NextDouble() * 0.05f;
            P[v * 3 + 2] = (float)Math.Sin(x * 0.3) * (float)Math.Cos(y * 0.2) + (float)rnd.NextDouble() * 0.02f;
            if (normals)
            {
                float nx = (float)rnd.NextDouble() - 0.5f, ny = (float)rnd.NextDouble() - 0.5f, nz = 1f + (float)rnd.NextDouble();
                if (v % 97 == 0) { nx *= 4f; nz *= 4f; }            // beyond [-1, 1]: the RNA clamp changes its direction
                if (v % 131 == 0) nx = ny = nz = 0f;                // a zero file normal: the true-normal fallback
                if (v % 173 == 0) { nx = float.NaN; }               // a vertex without a file normal (a primitive without normals)
                N[v * 3] = nx; N[v * 3 + 1] = ny; N[v * 3 + 2] = nz;
            }
        }
        var tris = new List<int>();
        for (int y = 0; y + 1 < h; y++) for (int x = 0; x + 1 < w; x++)
        {
            int a = y * w + x, b = a + 1, c = a + w, d = c + 1;
            if ((x + y) % 7 == 3) continue;                          // holes: open fans beside cyclic ones
            tris.Add(a); tris.Add(b); tris.Add(d);
            if ((x * y) % 5 == 2) { tris.Add(a); tris.Add(c); tris.Add(d); }   // a winding mismatch: a sharp edge kind
            else { tris.Add(a); tris.Add(d); tris.Add(c); }
        }
        return (P, tris.ToArray(), N);
    }

    static bool[] Sharp(float[] P, int[] tris, float[] N, int seed)
    {
        var sharp = N != null ? VehicleProbe.SharpFaces(P, tris, N) : new bool[tris.Length / 3];
        var rnd = new Random(seed);
        for (int f = 0; f < sharp.Length; f++) if (rnd.Next(11) == 0) sharp[f] = true;   // sharp faces break fans
        return sharp;
    }

    [Fact]
    public void The_dll_loads_in_the_test_host()
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        Assert.StartsWith("native (hafkernels.dll version " + HafNativeKernels.Version, HafNativeKernels.Status);
    }

    [Fact]
    public void Twin_chunks_match_serial_with_pairs_across_chunks_and_a_partial_last_chunk()
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        const int pairs = 8501, n = pairs * 2 + 1; // enters the native parallel path; neither half ends on a 2048-face boundary
        var faces = new double[n * 9]; var flipped = new bool[n];
        for (int f = 0; f < n; f++)
        {
            int pair = f == n - 1 ? pairs : f % pairs;
            double x = pair * .5, z = f >= pairs ? .01 : 0;
            bool reverse = f >= pairs;
            int at = f * 9;
            faces[at] = x; faces[at + 2] = z;
            faces[at + 3] = x + (reverse ? 0 : .1); faces[at + 4] = reverse ? .1 : 0; faces[at + 5] = z;
            faces[at + 6] = x + (reverse ? .1 : 0); faces[at + 7] = reverse ? 0 : .1; faces[at + 8] = z;
            flipped[f] = pair % 3 == 0;
        }
        var serial = new double[n]; var serialProj = new double[n]; var serialId = new int[n];
        HafNativeKernels.TwinCensus(faces, flipped, .25, .1, .00001f, 1, serial, serialProj, serialId);
        for (int f = 0; f < n - 1; f++)
        {
            Assert.Equal(f < pairs ? f + pairs : f - pairs, serialId[f]);
            Assert.Equal(.01, serial[f], 9);
            Assert.Equal(flipped[f] ? -.01 : .01, serialProj[f], 9);
        }
        Assert.Equal(-1, serialId[n - 1]); Assert.True(double.IsPositiveInfinity(serial[n - 1]));
        foreach (int workers in new[] { 2, 4, 16 })
        {
            var best = new double[n]; var proj = new double[n]; var ids = new int[n];
            HafNativeKernels.TwinCensus(faces, flipped, .25, .1, .00001f, workers, best, proj, ids);
            Assert.Equal(serial, best); Assert.Equal(serialProj, proj); Assert.Equal(serialId, ids);
        }
    }

    [Theory]
    [InlineData(23, 17, true, 1)]      // small: both sides serial
    [InlineData(23, 17, false, 2)]     // without file normals: the fallback normals, every face sharp at import
    [InlineData(260, 70, true, 3)]     // 18,200 vertices, 35,000 faces: both sides' parallel fan walk (nv >= 16384, corners >= 49152)
    [InlineData(260, 70, false, 4)]
    public void Encode_and_decode_match_the_managed_twins(int w, int h, bool normals, int seed)
    {
        var (P, tris, N) = Grid(w, h, normals, seed);
        var Nn = N ?? Enumerable.Repeat(float.NaN, P.Length).ToArray();
        var sharp = Sharp(P, tris, N, seed);
        short[] m0 = null, m1 = null, n0 = null, n1 = null; float[] md = null, nd = null, mv = null, nv = null;
        WithKernels(false, () => { (m0, m1) = VehicleProbe.EncodeCustomShorts(P, tris, Nn, sharp); md = VehicleProbe.DecodeCustomShorts(P, tris, sharp, m0, m1); mv = VehicleProbe.BlenderVertexNormals(P, tris, N); });
        WithKernels(true, () => { (n0, n1) = VehicleProbe.EncodeCustomShorts(P, tris, Nn, sharp); nd = VehicleProbe.DecodeCustomShorts(P, tris, sharp, n0, n1); nv = VehicleProbe.BlenderVertexNormals(P, tris, N); });
        Assert.Equal(m0, n0); Assert.Equal(m1, n1);
        Assert.True(m0.Any(s => s != 0) || !normals, "the fixture encodes nothing but zeros");
        Assert.Equal(Bits(md), Bits(nd));
        Assert.Equal(Bits(mv), Bits(nv));
        Assert.Contains(md, x => float.IsNaN(x) == false && x != 0f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Partial_NaN_normals_keep_the_managed_geometry_fallback(int component)
    {
        // GlbReader accepts non-finite attributes. NaN only in Y/Z must survive the RNA clamp so Normalize3
        // selects the geometry normal, just as the managed path does (NaN in X is the missing-normal sentinel).
        var P = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        var tris = new[] { 0, 1, 2 };
        var N = new float[] { .5f, .25f, 1f, .5f, .25f, 1f, .5f, .25f, 1f };
        for (int v = 0; v < 3; v++) N[3 * v + component] = float.NaN;
        var sharp = VehicleProbe.SharpFaces(P, tris, N);
        short[] m0 = null, m1 = null, n0 = null, n1 = null;
        WithKernels(false, () => (m0, m1) = VehicleProbe.EncodeCustomShorts(P, tris, N, sharp));
        WithKernels(true, () => (n0, n1) = VehicleProbe.EncodeCustomShorts(P, tris, N, sharp));
        Assert.All(m0, x => Assert.Equal((short)0, x));
        Assert.All(m1, x => Assert.Equal((short)0, x));
        Assert.Equal(m0, n0); Assert.Equal(m1, n1);

        var model = new HafModel(); var mesh = new HafMesh();
        mesh.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = P, Normals = N, Indices = tris });
        model.Meshes.Add(mesh); model.Nodes.Add(new HafNode { Mesh = 0 });
        var identity = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        VehicleProbe.PartMesh managed = null, native = null;
        WithKernels(false, () => managed = VehicleProbe.BuildPartMesh(model, 0, -1, null, null, identity, null));
        WithKernels(true, () => native = VehicleProbe.BuildPartMesh(model, 0, -1, null, null, identity, null));
        Assert.Equal(Bits(managed.Normal), Bits(native.Normal));
        Assert.Equal(Bits(managed.PreviewNormal), Bits(native.PreviewNormal));
    }

    [Fact]
    public void Decode_with_sharp_edges_stays_managed_and_equal()
    {
        var (P, tris, N) = Grid(31, 29, true, 5);
        var sharp = Sharp(P, tris, N, 5);
        var edges = new HashSet<long>(VehicleProbe.PairKeyComparer.Instance);
        for (int c = 0; c < tris.Length; c += 9) edges.Add(VehicleProbe.SharpEdgeKey(tris[c], tris[c + 1]));
        float[] m = null, n = null; short[] d0 = null, d1 = null;
        WithKernels(false, () => { (d0, d1) = VehicleProbe.EncodeCustomShorts(P, tris, N, sharp); m = VehicleProbe.DecodeCustomShorts(P, tris, sharp, d0, d1, edges); });
        WithKernels(true, () => n = VehicleProbe.DecodeCustomShorts(P, tris, sharp, d0, d1, edges));
        Assert.Equal(Bits(m), Bits(n));
    }

    [Theory]
    [InlineData(40, 30, 6)]
    [InlineData(300, 120, 7)]   // 71,000 faces: the parallel build on both sides
    public void Bvh_any_hit_matches_the_managed_tree(int w, int h, int seed)
    {
        var (P, tris, _) = Grid(w, h, false, seed);
        var rnd = new Random(seed); var rays = new List<float[]>();
        for (int i = 0; i < 400; i++)
        {
            int v = rnd.Next(P.Length / 3);
            rays.Add(new[] { P[v * 3] + (float)(rnd.NextDouble() - 0.5) * 0.3f, P[v * 3 + 1] + (float)(rnd.NextDouble() - 0.5) * 0.3f, P[v * 3 + 2] + (i % 2 == 0 ? 3f : -3f),
                (float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5), i % 3 == 0 ? -1f : (float)rnd.NextDouble() - 0.5f });
        }
        bool[] m = null, n = null;
        WithKernels(false, () => { var bvh = new VehicleProbe.TriangleBvh(P, tris); m = rays.Select(r => bvh.AnyHit(r[0], r[1], r[2], r[3], r[4], r[5])).ToArray(); });
        WithKernels(true, () => { var bvh = new VehicleProbe.TriangleBvh(P, tris); n = rays.Select(r => bvh.AnyHit(r[0], r[1], r[2], r[3], r[4], r[5])).ToArray(); });
        Assert.Equal(m, n);
        Assert.Contains(true, m); Assert.Contains(false, m);
        // the tree itself: the same slots, bounds and leaf triangles (the numbering depends on the data alone)
        VehicleProbe.TriangleBvh tm = null, tn = null;
        WithKernels(false, () => tm = new VehicleProbe.TriangleBvh(P, tris));
        WithKernels(true, () => tn = new VehicleProbe.TriangleBvh(P, tris));
        foreach (var name in new[] { "bmin", "bmax", "left", "start", "count", "t" })
        {
            var field = typeof(VehicleProbe.TriangleBvh).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.Equal((Array)field.GetValue(tm), (Array)field.GetValue(tn));
        }
    }

    [Theory]
    [InlineData(2000, 8)]
    [InlineData(40000, 9)]   // the parallel ranges
    public void World_transform_matches_the_managed_loop(int count, int seed)
    {
        var rnd = new Random(seed);
        var m = new HafModel(); var mesh = new HafMesh();
        var p = new HafPrimitive { VertexCount = count, Positions = new float[3 * count], Normals = new float[3 * count], Indices = new int[3 * (count - 2)] };
        for (int v = 0; v < count; v++)
        {
            p.Positions[3 * v] = (float)rnd.NextDouble() * 40f - 20f; p.Positions[3 * v + 1] = (float)rnd.NextDouble() * 9f; p.Positions[3 * v + 2] = (float)rnd.NextDouble() * 5f - 2f;
            float nx = (float)rnd.NextDouble() - 0.5f, ny = (float)rnd.NextDouble() - 0.5f, nz = (float)rnd.NextDouble() - 0.5f; float l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            p.Normals[3 * v] = nx / l; p.Normals[3 * v + 1] = ny / l; p.Normals[3 * v + 2] = nz / l;
        }
        for (int t = 0; t + 2 < count; t++) { p.Indices[3 * t] = t; p.Indices[3 * t + 1] = t + 1; p.Indices[3 * t + 2] = t + 2; }
        mesh.Primitives.Add(p); m.Meshes.Add(mesh); m.Nodes.Add(new HafNode { Mesh = 0 });
        // a mirrored, sheared, translated matrix_world: the forward normal and the inverse-transpose preview normal differ
        var mb = new float[] { -1.3f, 0.2f, 0f, 4f, 0.1f, 0.9f, 0f, -2f, 0f, 0.3f, 1.7f, 0.5f, 0f, 0f, 0f, 1f };
        VehicleProbe.PartMesh a = null, b = null;
        WithKernels(false, () => a = VehicleProbe.BuildPartMesh(m, 0, -1, null, null, mb, null));
        WithKernels(true, () => b = VehicleProbe.BuildPartMesh(m, 0, -1, null, null, mb, null));
        Assert.Equal(Bits(a.World), Bits(b.World)); Assert.Equal(Bits(a.Normal), Bits(b.Normal));
        Assert.Equal(Bits(a.PreviewNormal), Bits(b.PreviewNormal)); Assert.Equal(Bits(a.Local), Bits(b.Local));
        Assert.Equal(a.Tris, b.Tris);
        Assert.NotEqual(Bits(a.Normal), Bits(a.PreviewNormal));
    }

    static int[] Bits(float[] v) => v.Select(x => BitConverter.ToInt32(BitConverter.GetBytes(x), 0)).ToArray();

    // Native boundary guards: reject impossible buffers before allocation and keep the side query free of per-face garbage.
    [Theory]
    [InlineData(715827883)]
    [InlineData(1431655766)]
    public void Weld_rejects_vertex_counts_whose_component_product_would_overflow(int count)
    {
        // The test host's default CLR array-size limit also makes the old implementation fail immediately without
        // consuming RAM, but it must reject the short input before even attempting to reserve the result array.
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.WeldClasses(new double[3], count, 1, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Side_queries_do_not_allocate_per_face(int width)
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        var face = new double[] { 0, 0, 0, 0, 1, 0, 0, 0, 1 };
        var blocker = new double[] { 1, 0, 0, 1, 1, 0, 1, 0, 1 };
        if (width == 2) for (int i = 0; i < 9; i += 3)
        {
            double x = face[i]; face[i] = face[i + 2]; face[i + 2] = x;
            x = blocker[i]; blocker[i] = blocker[i + 2]; blocker[i + 2] = x;
        }
        using (var index = new HafNativeKernels.SideIndex(new[] { 2.0 }, new[] { blocker }, face, 1, 0, width))
        {
            for (int i = 0; i < 100; i++) index.Query(0, i % 2 == 0);
            var allocated = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread"));
            long before = allocated(); int exposed = 0;
            for (int i = 0; i < 10000; i++) if (index.Query(0, i % 2 == 0)) exposed++;
            long bytes = allocated() - before;
            Assert.Equal(5000, exposed);   // the blocker is ahead; the opposite ray is open
            Assert.InRange(bytes, 0, 1024);
        }
    }

    // The reference is the original Dictionary walk: i ascending, neighbours in dx/dy/dz order, j ascending, and
    // parent[Find(i)] = Find(j). Shuffled chains and duplicates make each representative depend on that union order.
    [Theory]
    [InlineData(1), InlineData(2), InlineData(3)]
    public void Weld_classes_match_the_managed_walk_union_for_union(int seed)
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        var rnd = new Random(seed); var pts = new List<double>();
        double weld = 0.05;
        for (int chain = 0; chain < 400; chain++)
        {
            double x = rnd.NextDouble() * 10, y = rnd.NextDouble() * 10, z = rnd.NextDouble() * 10;
            int links = 1 + rnd.Next(6);
            for (int l = 0; l < links; l++)
            {
                pts.Add(x); pts.Add(y); pts.Add(z);
                if (rnd.Next(4) == 0) { pts.Add(x); pts.Add(y); pts.Add(z); }   // an exact duplicate
                int axis = rnd.Next(3); double step = weld * (0.3 + 0.65 * rnd.NextDouble()) * (rnd.Next(2) == 0 ? 1 : -1);
                if (axis == 0) x += step; else if (axis == 1) y += step; else z += step;
            }
        }
        // shuffle, so that a chain's vertices are scattered through the index order
        int n = pts.Count / 3; var order = Enumerable.Range(0, n).OrderBy(_ => rnd.Next()).ToArray();
        var pos = new double[3 * n];
        for (int i = 0; i < n; i++) for (int a = 0; a < 3; a++) pos[3 * i + a] = pts[3 * order[i] + a];
        foreach (double distance in new[] { weld, 1e-6 })
        {
            double cell = distance, thresh2 = distance * distance;
            int[] native = HafNativeKernels.WeldClasses(pos, n, cell, thresh2);
            // the reference
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            (long, long, long) Cell(int i) => ((long)Math.Floor(pos[3 * i] / cell), (long)Math.Floor(pos[3 * i + 1] / cell), (long)Math.Floor(pos[3 * i + 2] / cell));
            var grid = new Dictionary<(long, long, long), List<int>>();
            for (int i = 0; i < n; i++) { var k = Cell(i); if (!grid.TryGetValue(k, out var l)) grid.Add(k, l = new List<int>()); l.Add(i); }
            for (int i = 0; i < n; i++)
            {
                var k = Cell(i);
                for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((k.Item1 + dx, k.Item2 + dy, k.Item3 + dz), out var l)) continue;
                    foreach (int j in l)
                    {
                        if (j <= i) continue;
                        double ddx = pos[3 * i] - pos[3 * j], ddy = pos[3 * i + 1] - pos[3 * j + 1], ddz = pos[3 * i + 2] - pos[3 * j + 2];
                        if (ddx * ddx + ddy * ddy + ddz * ddz <= thresh2) { int a = Find(i), b = Find(j); if (a != b) parent[a] = b; }
                    }
                }
            }
            var expected = new int[n]; for (int i = 0; i < n; i++) expected[i] = Find(i);
            Assert.Equal(expected, native);
            int classes = expected.Distinct().Count();
            Assert.True(classes < n && classes > 100, "the fixture welds nothing or everything: " + classes + " classes of " + n);
        }
    }
}
