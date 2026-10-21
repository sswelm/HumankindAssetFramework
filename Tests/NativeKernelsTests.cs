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
        Assert.StartsWith("native (hafkernels.dll version 1", HafNativeKernels.Status);
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
}
