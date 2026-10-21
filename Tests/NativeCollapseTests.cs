using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

// The native collapse (editor/HafNativeKernels.Collapse.cs over native/hafdecimate.cpp) against the managed
// BlenderDecimate.Collapse, through BlenderReduce.Reduce so the whole round trip is compared: the BMesh after the
// collapse, the UV and colour layers blended along it, the vertex groups through their CSR crossing, and the reduce's
// own output (positions, faces, edges, materials, sharpness). Two meshes: a textured grid with corner colours and two
// materials, and a skinned grid with two joints (vertex groups merge and reorder as vertices collapse). The DLL must
// load (NativeKernelsTests asserts it); here a managed fallback would compare the managed path with itself, so each
// case also asserts Available.
public class NativeCollapseTests
{
    static void WithKernels(bool on, Action body)
    {
        bool was = HafNativeKernels.Available;
        HafNativeKernels.Available = on;
        try { body(); } finally { HafNativeKernels.Available = was; }
    }

    static HafModel Grid(int w, int h, bool skinned, int seed, bool regular = false)
    {
        // regular: no jitter - a lattice of identical quads, where many edges cost exactly the same and the heap's
        // insertion-order ties decide which collapses first (the plant on the heap's tie-break shows only here)
        var rnd = regular ? new Random(0) : new Random(seed);
        double J(double amount) => regular ? 0.0 : rnd.NextDouble() * amount;
        var m = new HafModel();
        int nv = w * h;
        var p = new HafPrimitive { VertexCount = nv, Positions = new float[3 * nv], Normals = new float[3 * nv], Uv0 = new float[2 * nv], Colors = new float[4 * nv], Material = 0 };
        var q = new HafPrimitive { VertexCount = nv, Positions = new float[3 * nv], Normals = new float[3 * nv], Uv0 = new float[2 * nv], Material = 1 };
        foreach (var prim in new[] { p, q })
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int v = y * w + x; float dz = prim == q ? 5f : 0f;
                prim.Positions[3 * v] = x * 0.5f + (float)J(0.1); prim.Positions[3 * v + 1] = y * 0.4f + (float)J(0.1);
                prim.Positions[3 * v + 2] = dz + (regular ? 0f : (float)Math.Sin(x * 0.7) * 0.3f) + (float)J(0.05);
                prim.Normals[3 * v] = 0f; prim.Normals[3 * v + 1] = 0f; prim.Normals[3 * v + 2] = 1f;
                prim.Uv0[2 * v] = x / (float)w + (v % 7 == 0 ? 0.3f : 0f); prim.Uv0[2 * v + 1] = y / (float)h;   // a seam every seventh vertex
                if (prim.Colors != null) { prim.Colors[4 * v] = (float)rnd.NextDouble(); prim.Colors[4 * v + 1] = x / (float)w; prim.Colors[4 * v + 2] = 0.5f; prim.Colors[4 * v + 3] = 1f; }
            }
        var tris = new List<int>();
        for (int y = 0; y + 1 < h; y++) for (int x = 0; x + 1 < w; x++)
        {
            int a = y * w + x, b = a + 1, c = a + w, d = c + 1;
            if ((x * 3 + y) % 11 == 5) continue;   // holes: boundary edges inside the grid
            tris.Add(a); tris.Add(b); tris.Add(d); tris.Add(a); tris.Add(d); tris.Add(c);
        }
        p.Indices = tris.ToArray(); q.Indices = tris.ToArray();
        if (skinned)
        {
            foreach (var prim in new[] { p, q })
            {
                prim.Joints = new ushort[4 * nv]; prim.Weights = new float[4 * nv];
                for (int v = 0; v < nv; v++)
                {
                    float t = (v % w) / (float)w;
                    prim.Joints[4 * v] = 0; prim.Joints[4 * v + 1] = 1;
                    prim.Weights[4 * v] = v % 5 == 0 ? 1f : 1f - t; prim.Weights[4 * v + 1] = v % 5 == 0 ? 0f : t;
                }
            }
        }
        var mesh = new HafMesh { Name = "grid" }; mesh.Primitives.Add(p); mesh.Primitives.Add(q); m.Meshes.Add(mesh);
        m.Materials.Add(new HafMaterial { Name = "A" }); m.Materials.Add(new HafMaterial { Name = "B" });
        m.Nodes.Add(new HafNode { Name = "grid", Mesh = 0, Skin = skinned ? 0 : -1 });
        if (skinned)
        {
            m.Nodes.Add(new HafNode { Name = "root" }); m.Nodes.Add(new HafNode { Name = "tip" });
            m.Nodes[1].Children.Add(2); m.Nodes[2].Parent = 1;
            m.Skins.Add(new HafSkin { Name = "rig", Joints = new[] { 1, 2 }, Skeleton = 1 });
        }
        var scene = new HafScene(); scene.Nodes.Add(0); if (skinned) scene.Nodes.Add(1);
        m.Scenes.Add(scene); m.Scene = 0;
        return m;
    }

    static int[] Bits(float[] v) => v == null ? null : v.Select(x => BitConverter.ToInt32(BitConverter.GetBytes(x), 0)).ToArray();

    static void SameResult(BlenderReduce.Result a, BlenderReduce.Result b)
    {
        Assert.Null(a.Fallback); Assert.Null(b.Fallback);
        Assert.True(a.Collapsed); Assert.True(b.Collapsed);
        Assert.Equal(a.VertexCount, b.VertexCount); Assert.Equal(a.FaceCount, b.FaceCount);
        Assert.Equal(Bits(a.Positions), Bits(b.Positions));
        Assert.Equal(a.Faces, b.Faces); Assert.Equal(a.Edges, b.Edges);
        Assert.Equal(a.FaceMaterial, b.FaceMaterial); Assert.Equal(a.FaceSharp, b.FaceSharp);
        Assert.Equal(a.Uv.Count, b.Uv.Count);
        for (int i = 0; i < a.Uv.Count; i++) Assert.Equal(Bits(a.Uv[i]), Bits(b.Uv[i]));
        Assert.Equal(a.Colors.Count, b.Colors.Count);
        for (int i = 0; i < a.Colors.Count; i++) { Assert.Equal(a.Colors[i].Item1, b.Colors[i].Item1); Assert.Equal(a.Colors[i].Item2, b.Colors[i].Item2); Assert.Equal(a.Colors[i].Item3, b.Colors[i].Item3); }
        Assert.Equal(a.DefNr == null, b.DefNr == null);
        if (a.DefNr != null)
        {
            Assert.Equal(a.DefNr.Length, b.DefNr.Length);
            for (int v = 0; v < a.DefNr.Length; v++)
            {
                Assert.Equal(a.DefNr[v] == null, b.DefNr[v] == null);
                if (a.DefNr[v] == null) continue;
                Assert.Equal(a.DefNr[v], b.DefNr[v]); Assert.Equal(Bits(a.DefWeight[v].ToArray()), Bits(b.DefWeight[v].ToArray()));
            }
        }
    }

    [Theory]
    [InlineData(false, 0.5f, 1, false)]
    [InlineData(false, 0.2f, 2, false)]
    [InlineData(true, 0.5f, 3, false)]
    [InlineData(true, 0.3f, 4, false)]
    [InlineData(false, 0.4f, 5, true)]    // the regular lattice: equal costs, the heap's tie-break decides
    [InlineData(true, 0.25f, 6, true)]
    public void Reduce_through_the_native_collapse_equals_the_managed_one(bool skinned, float ratio, int seed, bool regular)
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        var m = Grid(40, 30, skinned, seed, regular);
        var names = BlenderNames.Compute(m);
        BlenderReduce.Result managed = null, native = null;
        WithKernels(false, () => managed = BlenderReduce.Reduce(Grid(40, 30, skinned, seed, regular), 0, ratio, BlenderNames.Compute(Grid(40, 30, skinned, seed, regular))));
        WithKernels(true, () => native = BlenderReduce.Reduce(m, 0, ratio, names));
        SameResult(managed, native);
        Assert.InRange(native.FaceCount, 1, (int)(2 * 2 * 39 * 29 * ratio) + 2);   // the target was reached, not skipped
        Assert.Contains(native.Uv[0], x => x != 0f);
        if (skinned) Assert.Contains(native.DefNr, l => l != null && l.Count == 2);   // merged groups survive the crossing
    }

    [Fact]
    public void A_corrupt_link_throws_before_the_native_collapse_runs()
    {
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);
        var m = Grid(12, 10, false, 5);
        var layout = BlenderMesh.FromGltf(m, 0);
        var bm = BMesh.FromMesh(layout, new float[3 * layout.VertexCount]);
        bm.LV[3] = bm.VertCount + 7;   // a corner naming a vertex that does not exist
        var d = new BlenderDecimate.MeshData();
        Assert.Throws<IndexOutOfRangeException>(() => HafNativeKernels.Collapse(bm, 0.5f, d));
    }
}
