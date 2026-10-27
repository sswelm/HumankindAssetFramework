using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

// The reduce stage's native kernels (editor/HafNativeKernels.BMesh.cs over native/hafbmesh.cpp) against their managed twins: the
// BMesh build of a stage mesh (BlenderVehicleRig.Stage2.FromMeshData's loops, repeated here), the weld grouping's per-vertex
// material, bmesh_find_doubles (BlenderWeld.FindDoubles) and bmesh.ops.dissolve_limit over chunk jobs (BlenderDissolve.DissolveLimit
// with the NORMALS_CALC finish and the Python wrapper's "has been removed" check). Every array is compared bit for bit up to the
// pool counts, the free lists too - and a face created on both meshes afterwards must land in the same slot. The DLL must load
// (NativeKernelsTests asserts it); each case asserts Available so a managed fallback cannot compare the managed path with itself.
[Collection("native kernels switch")]
public class NativeBMeshTests
{
    static void WithKernels(bool on, Action body)
    {
        bool was = HafNativeKernels.Available;
        HafNativeKernels.Available = on;
        try { body(); } finally { HafNativeKernels.Available = was; }
    }

    // a stage mesh: w x h vertices, both diagonals, z from a shape; the edge list in face order (first met), optionally a pair repeated
    sealed class Stage { public float[] Positions; public int[] Edges, Faces, FaceMaterial; public bool[] EdgeSharp, FaceSharp; public int Vertices; }
    static Stage Grid(int w, int h, int shape, int seed, bool sharp, bool repeatEdge, bool twoMaterials)
    {
        var rnd = new Random(seed);
        int nv = w * h; var s = new Stage { Vertices = nv, Positions = new float[3 * nv] };
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int v = y * w + x;
            double z = shape == 0 ? 0.0 : shape == 1 ? Math.Sin(x * 0.9) * 0.4 + Math.Cos(y * 0.7) * 0.3 : (x % 3 == 1 ? 0.2 : 0.0) + rnd.NextDouble() * 0.01;
            s.Positions[3 * v] = x * 0.5f + (shape == 2 ? (float)(rnd.NextDouble() * 0.05) : 0f); s.Positions[3 * v + 1] = y * 0.4f; s.Positions[3 * v + 2] = (float)z;
        }
        var faces = new List<int>(); var edges = new List<int>(); var seen = new HashSet<long>();
        void Edge(int a, int b) { long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b); if (seen.Add(key)) { edges.Add(a); edges.Add(b); } }
        for (int y = 0; y + 1 < h; y++) for (int x = 0; x + 1 < w; x++)
        {
            int a = y * w + x, b = a + 1, c = a + w, d = c + 1;
            if ((x * 5 + y * 3) % 13 == 7) continue;   // a hole: boundary edges inside
            if ((x + y) % 2 == 0) { faces.AddRange(new[] { a, b, d, a, d, c }); Edge(a, b); Edge(b, d); Edge(d, a); Edge(d, c); Edge(c, a); }
            else { faces.AddRange(new[] { a, b, c, b, d, c }); Edge(a, b); Edge(b, c); Edge(c, a); Edge(b, d); Edge(d, c); }
        }
        if (repeatEdge && edges.Count >= 4) { edges.Add(edges[2]); edges.Add(edges[3]); edges.Add(edges[1]); edges.Add(edges[0]); }   // the same pair twice more, once reversed
        s.Faces = faces.ToArray(); s.Edges = edges.ToArray();
        int nf = s.Faces.Length / 3, ne = s.Edges.Length / 2;
        s.FaceMaterial = new int[nf]; for (int f = 0; f < nf; f++) s.FaceMaterial[f] = twoMaterials && (f / 7) % 3 == 1 ? 1 : 0;
        if (sharp) { s.EdgeSharp = new bool[ne]; for (int e = 0; e < ne; e++) s.EdgeSharp[e] = e % 5 == 2; s.FaceSharp = new bool[nf]; for (int f = 0; f < nf; f++) s.FaceSharp[f] = f % 4 == 1; }
        return s;
    }

    // FromMeshData's managed loops (BlenderVehicleRig.Stage2): the specification of the native build
    static BMesh ManagedBuild(Stage d, bool faceNormals)
    {
        var bm = new BMesh();
        for (int v = 0; v < d.Vertices; v++) bm.VertCreate(d.Positions[3 * v], d.Positions[3 * v + 1], d.Positions[3 * v + 2]);
        int ne = d.Edges.Length / 2; var edgeOf = new Dictionary<long, int>();
        long Key(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
        for (int e = 0; e < ne; e++)
        {
            int a = d.Edges[2 * e], b = d.Edges[2 * e + 1];
            int i = bm.EdgeCreate(a, b);
            bm.EFlag[i] = d.EdgeSharp != null && d.EdgeSharp[e] ? (byte)0 : BMesh.FlagSmooth;
            edgeOf[Key(a, b)] = i;
        }
        var verts = new int[3]; var edges = new int[3]; int nf = d.Faces.Length / 3;
        for (int f = 0; f < nf; f++)
        {
            for (int k = 0; k < 3; k++) verts[k] = d.Faces[3 * f + k];
            for (int k = 0; k < 3; k++) if (!edgeOf.TryGetValue(Key(verts[k], verts[(k + 1) % 3]), out edges[k])) throw new InvalidOperationException($"face {f}: no edge joins corners {verts[k]} and {verts[(k + 1) % 3]}");
            int i = bm.FaceCreate(verts, edges, 3);
            bm.FFlag[i] = d.FaceSharp != null && d.FaceSharp[f] ? (byte)0 : BMesh.FlagSmooth;
            bm.FMat[i] = d.FaceMaterial[f];
        }
        if (faceNormals) BlenderDecimate.FaceNormalsUpdate(bm);
        return bm;
    }

    static void SameInts(string what, int[] a, int[] b, int n) { for (int i = 0; i < n; i++) Assert.True(a[i] == b[i], $"{what}[{i}]: managed {a[i]}, native {b[i]}"); }
    static void SameBytes(string what, byte[] a, byte[] b, int n) { for (int i = 0; i < n; i++) Assert.True(a[i] == b[i], $"{what}[{i}]: managed {a[i]}, native {b[i]}"); }
    static void SameFlags(string what, bool[] a, bool[] b, int n) { for (int i = 0; i < n; i++) Assert.True(a[i] == b[i], $"{what}[{i}]: managed {a[i]}, native {b[i]}"); }
    static int Bits(float v) => BitConverter.ToInt32(BitConverter.GetBytes(v), 0);   // net471: no SingleToInt32Bits
    static void SameFloats(string what, float[] a, float[] b, int n) { for (int i = 0; i < n; i++) Assert.True(Bits(a[i]) == Bits(b[i]), $"{what}[{i}]: managed {a[i]:R}, native {b[i]:R}"); }
    static void SameBMesh(BMesh a, BMesh b)
    {
        Assert.Equal((a.VertCount, a.TotVert, a.EdgeCount, a.TotEdge, a.LoopCount, a.TotLoop, a.FaceCount, a.TotFace), (b.VertCount, b.TotVert, b.EdgeCount, b.TotEdge, b.LoopCount, b.TotLoop, b.FaceCount, b.TotFace));
        int nv = a.VertCount, ne = a.EdgeCount, nl = a.LoopCount, nf = a.FaceCount;
        SameInts("VE", a.VE, b.VE, nv); SameFlags("VAlive", a.VAlive, b.VAlive, nv); SameFloats("VCo", a.VCo, b.VCo, 3 * nv); SameFloats("VNo", a.VNo, b.VNo, 3 * nv); SameBytes("VFlag", a.VFlag, b.VFlag, nv);
        SameInts("EV1", a.EV1, b.EV1, ne); SameInts("EV2", a.EV2, b.EV2, ne); SameInts("EL", a.EL, b.EL, ne); SameInts("EDiskNext", a.EDiskNext, b.EDiskNext, 2 * ne); SameInts("EDiskPrev", a.EDiskPrev, b.EDiskPrev, 2 * ne);
        SameFlags("EAlive", a.EAlive, b.EAlive, ne); SameBytes("EFlag", a.EFlag, b.EFlag, ne);
        SameInts("LV", a.LV, b.LV, nl); SameInts("LE", a.LE, b.LE, nl); SameInts("LF", a.LF, b.LF, nl); SameInts("LNext", a.LNext, b.LNext, nl); SameInts("LPrev", a.LPrev, b.LPrev, nl);
        SameInts("LRadialNext", a.LRadialNext, b.LRadialNext, nl); SameInts("LRadialPrev", a.LRadialPrev, b.LRadialPrev, nl); SameFlags("LAlive", a.LAlive, b.LAlive, nl);
        SameInts("FLFirst", a.FLFirst, b.FLFirst, nf); SameInts("FLen", a.FLen, b.FLen, nf); SameInts("FMat", a.FMat, b.FMat, nf); SameFloats("FNo", a.FNo, b.FNo, 3 * nf); SameFlags("FAlive", a.FAlive, b.FAlive, nf); SameBytes("FFlag", a.FFlag, b.FFlag, nf);
        for (int kind = 0; kind < 4; kind++) Assert.True(a.FreeList(kind).SequenceEqual(b.FreeList(kind)), "free list " + kind + ": managed [" + string.Join(",", a.FreeList(kind)) + "] native [" + string.Join(",", b.FreeList(kind)) + "]");
    }

    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(1, true, true, true)]
    [InlineData(2, true, false, true)]
    public void BuildEqualsManagedBuild(int shape, bool sharp, bool repeatEdge, bool twoMaterials)
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        var d = Grid(23, 17, shape, 11 + shape, sharp, repeatEdge, twoMaterials);
        foreach (bool normals in new[] { true, false })
        {
            var managed = ManagedBuild(d, normals);
            var native = HafNativeKernels.BuildFromMesh(d.Positions, d.Vertices, d.Edges, d.EdgeSharp, d.Faces, d.FaceSharp, d.FaceMaterial, normals);
            SameBMesh(managed, native);
            // the pools continue the same way: the next creations take the same slots
            int fm = managed.FaceCreate(new[] { 0, 1, 2 }, new[] { managed.EdgeCreate(0, 2), managed.EdgeCreate(2, 1), managed.EdgeCreate(1, 0) }, 3);
            int fn = native.FaceCreate(new[] { 0, 1, 2 }, new[] { native.EdgeCreate(0, 2), native.EdgeCreate(2, 1), native.EdgeCreate(1, 0) }, 3);
            Assert.Equal(fm, fn);
            SameBMesh(managed, native);
        }
    }

    [Fact]
    public void BuildThrowsTheTwinsMessageOnAMissingEdge()
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        var d = Grid(6, 5, 1, 3, false, false, false);
        var edges = d.Edges.ToList(); edges.RemoveRange(6, 2); d.Edges = edges.ToArray();   // the fourth pair gone
        string managed = null, native = null;
        try { ManagedBuild(d, true); } catch (InvalidOperationException e) { managed = e.Message; }
        try { HafNativeKernels.BuildFromMesh(d.Positions, d.Vertices, d.Edges, d.EdgeSharp, d.Faces, d.FaceSharp, d.FaceMaterial, true); } catch (InvalidOperationException e) { native = e.Message; }
        Assert.NotNull(managed);
        Assert.Equal(managed, native);
    }

    [Fact]
    public void VertMaterialEqualsTheCornerWalk()
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        var d = Grid(19, 13, 1, 5, false, false, true);
        var bm = ManagedBuild(d, true);
        bm.VertKill(7); bm.EdgeKill(bm.VE[40]);   // a dead vertex and one left without corners
        HafNativeKernels.VertMaterial(bm, out var material, out var uniform);
        var loops = new List<int>(); int uniformCount = 0;
        for (int v = 0; v < bm.VertCount; v++)
        {
            bool expect = false; int m0 = 0;
            if (bm.VAlive[v]) { bm.LoopsOfVert(v, loops); if (loops.Count > 0) { m0 = bm.FMat[bm.LF[loops[0]]]; expect = loops.All(l => bm.FMat[bm.LF[l]] == m0); } }
            Assert.True(expect == uniform[v], $"vertex {v}: managed uniform {expect}, native {uniform[v]}");
            if (expect) { Assert.Equal(m0, material[v]); uniformCount++; }
        }
        Assert.True(uniformCount > 0 && uniformCount < bm.TotVert, "the mesh must have vertices of one material and vertices on the seam");
    }

    [Fact]
    public void FindDoublesEqualsManaged()
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        // vertices in clusters of one to four within the weld distance, on a mesh so the BMesh has them
        var rnd = new Random(21); var d = Grid(14, 11, 1, 2, false, false, false);
        var bm = ManagedBuild(d, true);
        var verts = new List<int>();
        for (int v = 0; v < bm.VertCount; v++) verts.Add(v);
        for (int c = 0; c < 60; c++)
        {
            int src = rnd.Next(bm.VertCount); int extra = 1 + rnd.Next(3);
            for (int k = 0; k < extra; k++)
            {
                float jx = (float)(rnd.NextDouble() * 2e-4 - 1e-4), jy = (float)(rnd.NextDouble() * 2e-4 - 1e-4);
                verts.Add(bm.VertCreate((float)(bm.VCo[3 * src] + jx), (float)(bm.VCo[3 * src + 1] + jy), bm.VCo[3 * src + 2]));
            }
        }
        // tie-prone clusters: four vertices on the corners of an exact square in empty space (no vertex at the centre), every member
        // the same float32 distance from the centroid - the target is then the tie-break's choice (the lower cluster index), not
        // the geometry's; a plant that flips the comparison fails here and nowhere else in this fixture
        for (int c = 0; c < 12; c++)
        {
            float dd = 1f / 16384f, x = 100f + 2f * c, y = 50f, z = 0.25f;
            foreach (var (sx, sy) in new[] { (1f, 1f), (-1f, 1f), (1f, -1f), (-1f, -1f) }) verts.Add(bm.VertCreate((float)(x + sx * dd), (float)(y + sy * dd), z));
        }
        var order = verts.ToList();
        Dictionary<int, int> managed = null, native = null;
        WithKernels(false, () => managed = BlenderWeld.FindDoubles(bm, order, 1e-3f));
        WithKernels(true, () => native = BlenderWeld.FindDoubles(bm, order, 1e-3f));
        Assert.True(managed.Count > 40, "the clusters must weld: " + managed.Count);
        Assert.Equal(managed.Count, native.Count);
        foreach (var kv in managed) Assert.True(native.TryGetValue(kv.Key, out int t) && t == kv.Value, $"vertex {kv.Key}: managed -> {kv.Value}, native -> {(native.TryGetValue(kv.Key, out int tt) ? tt.ToString() : "none")}");
        // the subset form the weld tiers use: one material group, listed in slot order
        var part = order.Where(v => v % 3 != 1).ToList();
        WithKernels(false, () => managed = BlenderWeld.FindDoubles(bm, part, 1e-3f));
        WithKernels(true, () => native = BlenderWeld.FindDoubles(bm, part, 1e-3f));
        Assert.Equal(managed.OrderBy(kv => kv.Key).ToArray(), native.OrderBy(kv => kv.Key).ToArray());
    }

    static BlenderWeld.Layers LayersFor(BMesh bm, int seed, bool customNormal)
    {
        var rnd = new Random(seed); int nl = bm.LoopCount;
        var layers = new BlenderWeld.Layers();
        var uv = new float[2 * nl]; for (int i = 0; i < uv.Length; i++) uv[i] = (float)rnd.NextDouble(); layers.Uv.Add(uv);
        var uv2 = new float[2 * nl]; for (int i = 0; i < uv2.Length; i++) uv2[i] = (float)(rnd.NextDouble() * 3); layers.Uv.Add(uv2);
        var cc = new byte[4 * nl]; rnd.NextBytes(cc); layers.CornerColor.Add(cc);
        if (customNormal) { layers.CustomNormal = new short[2 * nl]; for (int i = 0; i < layers.CustomNormal.Length; i++) layers.CustomNormal[i] = (short)rnd.Next(-32768, 32768); }
        return layers;
    }
    static BlenderWeld.Layers Clone(BlenderWeld.Layers l)
    {
        var c = new BlenderWeld.Layers { CustomNormal = l.CustomNormal == null ? null : (short[])l.CustomNormal.Clone() };
        foreach (var u in l.Uv) c.Uv.Add((float[])u.Clone());
        foreach (var b in l.CornerColor) c.CornerColor.Add((byte[])b.Clone());
        foreach (var b in l.PointColor) c.PointColor.Add((byte[])b.Clone());
        return c;
    }
    static void SameLayers(BlenderWeld.Layers a, BlenderWeld.Layers b)
    {
        Assert.Equal(a.Uv.Count, b.Uv.Count);
        for (int i = 0; i < a.Uv.Count; i++) { Assert.Equal(a.Uv[i].Length, b.Uv[i].Length); SameFloats("uv" + i, a.Uv[i], b.Uv[i], a.Uv[i].Length); }
        Assert.Equal(a.CornerColor.Count, b.CornerColor.Count);
        for (int i = 0; i < a.CornerColor.Count; i++) { Assert.Equal(a.CornerColor[i].Length, b.CornerColor[i].Length); SameBytes("colour" + i, a.CornerColor[i], b.CornerColor[i], a.CornerColor[i].Length); }
        Assert.Equal(a.CustomNormal == null, b.CustomNormal == null);
        if (a.CustomNormal != null) { Assert.Equal(a.CustomNormal.Length, b.CustomNormal.Length); for (int i = 0; i < a.CustomNormal.Length; i++) Assert.True(a.CustomNormal[i] == b.CustomNormal[i], "custom normal short " + i); }
    }

    // the managed job loop of BlenderVehicleRig.Stage2.DissolveLimited
    static string ManagedDissolve(BMesh bm, BlenderWeld.Layers layers, float angle, List<(List<int> es, List<int> vs)> jobs, bool skipEmpty)
    {
        foreach (var (es, vs) in jobs)
        {
            if (skipEmpty && es.Count == 0 && vs.Count == 0) continue;
            for (int i = 0; i < vs.Count; i++) if (!bm.VAlive[vs[i]]) return "TypeError: verts: " + i + " BMVert has been removed";
            for (int i = 0; i < es.Count; i++) if (!bm.EAlive[es[i]]) return "TypeError: edges: " + i + " BMEdge has been removed";
            BlenderDissolve.DissolveLimit(bm, layers, angle, vs, es);
            BlenderDissolve.FaceNormalsUpdateAll(bm);
        }
        return null;
    }
    static List<(List<int> es, List<int> vs)> AllInOne(BMesh bm)
    {
        var verts = new List<int>(); var edges = new List<int>();
        for (int v = 0; v < bm.VertCount; v++) if (bm.VAlive[v]) verts.Add(v);
        for (int e = 0; e < bm.EdgeCount; e++) if (bm.EAlive[e]) edges.Add(e);
        return new List<(List<int> es, List<int> vs)> { (edges, verts) };
    }

    [Theory]
    [InlineData(0, true)]    // flat: every edge joins, every straight vertex collapses - one big n-gon and the Newell normal
    [InlineData(1, true)]    // waves: some joins under five degrees, some vertices kept by the degenerate checks
    [InlineData(2, false)]   // ridges with a sharp step and no custom normals
    public void DissolveLimitEqualsManaged(int shape, bool customNormal)
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        var d = Grid(21, 15, shape, 31 + shape, false, false, true);
        var a = ManagedBuild(d, true); var b = ManagedBuild(d, true);
        var la = LayersFor(a, 7, customNormal); var lb = Clone(la);
        float angle = (float)(5.0 * (Math.PI / 180.0));
        string deathA = ManagedDissolve(a, la, angle, AllInOne(a), false);
        string deathB = null;
        WithKernels(true, () => deathB = HafNativeKernels.DissolveLimit(b, lb.Uv, lb.CornerColor, ref lb.CustomNormal, angle, AllInOne(b), skipEmpty: false));
        Assert.Null(deathA); Assert.Null(deathB);
        Assert.True(a.TotFace < d.Faces.Length / 3, "the dissolve must have joined faces: " + a.TotFace + " of " + d.Faces.Length / 3);
        SameBMesh(a, b); SameLayers(la, lb);
        // the free lists replay: a face created on both now takes the same slot and corners
        int ea = a.EdgeExists(0, 1), eb = b.EdgeExists(0, 1);
        Assert.Equal(ea, eb);
        if (ea != BMesh.None && a.VAlive[0] && a.VAlive[1])
        {
            int v2 = a.EdgeOtherVert(a.VE[1], 1);
            if (v2 != BMesh.None && v2 != 0 && a.EdgeExists(v2, 0) == BMesh.None && a.VAlive[v2])
            {
                var sa = new int[3]; var sb = new int[3];
                int fa = a.FaceCreate(new[] { 0, 1, v2 }, new[] { ea, a.VE[1], a.EdgeCreate(v2, 0) }, 3, BMesh.None, sa);
                int fb = b.FaceCreate(new[] { 0, 1, v2 }, new[] { eb, b.VE[1], b.EdgeCreate(v2, 0) }, 3, BMesh.None, sb);
                Assert.Equal(fa, fb); Assert.Equal(sa, sb);
                SameBMesh(a, b);
            }
        }
    }

    [Fact]
    public void DissolveLimitChunkJobsEqualManaged()
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        var d = Grid(25, 19, 1, 44, true, false, true);
        var a = ManagedBuild(d, true); var b = ManagedBuild(d, true);
        var la = LayersFor(a, 9, true); var lb = Clone(la);
        float angle = (float)(5.0 * (Math.PI / 180.0));
        // two leaves by face slot: the interior edges (both faces in the leaf) and the interior vertices (every corner's face in it)
        var jobs = new List<(List<int> es, List<int> vs)>();
        int half = a.FaceCount / 2;
        foreach (var leaf in new[] { (0, half), (half, a.FaceCount) })
        {
            var es = new SortedSet<int>(); var vs = new SortedSet<int>(); var loops = new List<int>();
            for (int f = leaf.Item1; f < leaf.Item2; f++)
            {
                int l = a.FLFirst[f], l0 = l;
                do
                {
                    int e = a.LE[l]; int le = a.EL[e]; int nf = 0; bool both = true;
                    if (le != BMesh.None) { int lr = le; do { nf++; int lf = a.LF[lr]; if (lf < leaf.Item1 || lf >= leaf.Item2) both = false; lr = a.LRadialNext[lr]; } while (lr != le); }
                    if (nf == 2 && both) es.Add(e);
                    int v = a.LV[l]; loops.Clear(); a.LoopsOfVert(v, loops);
                    if (loops.All(lv => a.LF[lv] >= leaf.Item1 && a.LF[lv] < leaf.Item2)) vs.Add(v);
                    l = a.LNext[l];
                } while (l != l0);
            }
            jobs.Add((es.ToList(), vs.ToList()));
        }
        jobs.Insert(1, (new List<int>(), new List<int>()));   // an empty job, passed over
        string deathA = ManagedDissolve(a, la, angle, jobs, true), deathB = null;
        WithKernels(true, () => deathB = HafNativeKernels.DissolveLimit(b, lb.Uv, lb.CornerColor, ref lb.CustomNormal, angle, jobs, skipEmpty: true));
        Assert.Null(deathA); Assert.Null(deathB);
        SameBMesh(a, b); SameLayers(la, lb);
    }

    [Fact]
    public void DissolveLimitStopsOnARemovedElementLikeTheWrapper()
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        var d = Grid(12, 9, 0, 1, false, false, false);
        var a = ManagedBuild(d, true); var b = ManagedBuild(d, true);
        var la = LayersFor(a, 3, false); var lb = Clone(la);
        float angle = (float)(5.0 * (Math.PI / 180.0));
        var first = AllInOne(a)[0];
        // the second job names a vertex the first job's collapse removed, and an edge the first job's joins left dead
        var jobs = new List<(List<int> es, List<int> vs)> { first, (new List<int> { first.es[3] }, new List<int> { first.vs[first.vs.Count / 2] }) };
        string deathA = ManagedDissolve(a, la, angle, jobs, true), deathB = null;
        WithKernels(true, () => deathB = HafNativeKernels.DissolveLimit(b, lb.Uv, lb.CornerColor, ref lb.CustomNormal, angle, jobs, skipEmpty: true));
        Assert.NotNull(deathA);
        Assert.Equal(deathA, deathB);
    }
}
