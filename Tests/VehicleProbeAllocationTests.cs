using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

public class VehicleProbeAllocationTests
{
    // The test host is .NET Framework 4.8; the API is absent from the project's 4.7.1 reference assemblies.
    static Func<long> Counter() => (Func<long>)Delegate.CreateDelegate(typeof(Func<long>),
        typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Static | BindingFlags.Public));

    [Fact]
    public void Posed_bounds_stream_skin_matrices_instead_of_allocating_one_per_vertex()
    {
        const int count = 20000;
        var m = new HafModel(); var mesh = new HafMesh();
        var p = new HafPrimitive { VertexCount = count, Positions = new float[3 * count],
            Joints = new ushort[4 * count], Weights = new float[4 * count], Joints1 = new ushort[4 * count], Weights1 = new float[4 * count] };
        for (int v = 0; v < count; v++)
        {
            p.Positions[3 * v] = v * .13f; p.Positions[3 * v + 1] = -v * .37f; p.Positions[3 * v + 2] = v * .019f;
            p.Joints[4 * v] = (ushort)(v % 2); p.Joints[4 * v + 1] = 7; // invalid influences are ignored here
            p.Weights[4 * v] = v % 4 == 0 ? 0f : v % 4 == 1 ? 1.0000005f : .3f;
            p.Weights[4 * v + 1] = .2f;
            p.Joints1[4 * v] = 1; p.Weights1[4 * v] = v % 4 < 2 ? 0f : .45f;
        }
        mesh.Primitives.Add(p);
        var plain = new HafPrimitive { VertexCount = 1, Positions = new[] { 1f, 2f, 3f } };
        mesh.Primitives.Add(plain); m.Meshes.Add(mesh);
        m.Nodes.Add(new HafNode { Mesh = 0, Skin = 0 }); m.Nodes.Add(new HafNode()); m.Nodes.Add(new HafNode());
        m.Skins.Add(new HafSkin { Joints = new[] { 1, 2 } });
        var world = new[] { HafTransforms.Identity, HafTransforms.Trs(new HafNode { Translation = new[] { 2.13, -3.71, 1.57 } }),
            HafTransforms.Trs(new HafNode { Scale = new[] { 1.3, .7, -.4 } }) };
        HafTransforms.WorldPositions(m, 0, p, world);
        var allocated = Counter(); long before = allocated();
        var actual = HafTransforms.WorldPositions(m, 0, p, world);
        Assert.InRange(allocated() - before, 24L * count, 24L * count + 8192); // output plus fixed matrix scratch
        var blends = HafTransforms.BlendMatrices(m, 0, p, world);
        for (int v = 0; v < count; v++)
        {
            var expected = HafTransforms.Apply(blends[v], p.Positions[3 * v], p.Positions[3 * v + 1], p.Positions[3 * v + 2], 1);
            for (int k = 0; k < 3; k++) Assert.Equal(BitConverter.DoubleToInt64Bits(expected[k]), BitConverter.DoubleToInt64Bits(actual[3 * v + k]));
        }
        Assert.Equal(HafTransforms.Apply(world[1], 1, 2, 3, 1), HafTransforms.WorldPositions(m, 0, plain, world));
        Assert.Equal(1d, world[2][15]); // the shared joint tables remain unchanged
    }

    [Fact]
    public void Skinning_reuses_private_scratch_with_zero_weights_and_mixed_primitives_on_multiple_workers()
    {
        // Blender-frame translation at two joints. Alternate weighted, zero-weight and unskinned vertices through the SAME scratch.
        var a = VehicleProbe.IdentityF(); a[3] = 10f;
        var b = VehicleProbe.IdentityF(); b[7] = 6f;
        var skinner = new VehicleProbe.BlenderSkinner { JointMats = new[] { a, b } };
        var p = new HafPrimitive { VertexCount = 2, Positions = new[] { 1f, 2f, 3f, 4f, 5f, 6f }, Normals = new[] { 0f, 1f, 0f, 0f, 1f, 0f },
            Joints = new ushort[] { 0,0,0,0, 1,0,0,0 }, Weights = new float[] { .5f,0,0,0, 0,0,0,0 },
            Joints1 = new ushort[] { 1,0,0,0, 0,0,0,0 }, Weights1 = new float[] { .5f,0,0,0, 0,0,0,0 } };
        var plain = new HafPrimitive { VertexCount = 1, Positions = new[] { 7f, 8f, 9f } };
        void Work()
        {
            var scratch = new float[16]; var positions = new float[9]; var normals = new float[9];
            skinner.Vertex(p, 0, scratch, positions, normals, 0); // warm before allocation counting
            var allocated = Counter(); long before = allocated();
            for (int repeat = 0; repeat < 1000; repeat++)
            {
                skinner.Vertex(p, 0, scratch, positions, normals, 0);
                skinner.Vertex(p, 1, scratch, positions, normals, 3);
                skinner.Vertex(plain, 0, scratch, positions, normals, 6);
            }
            Assert.InRange(allocated() - before, 0L, 1024L);
            Assert.Equal(new[] { 6f, 0f, 2f, 4f, 0f, 5f, 17f, -9f, 8f }, positions);
            Assert.Equal(new[] { 0f, 0f, 1f, 0f, 0f, 1f }, normals.Take(6));
            Assert.All(normals.Skip(6), value => Assert.True(float.IsNaN(value)));
        }
        Parallel.For(0, 8, _ => Work());
        Assert.Equal(10f, a[3]); Assert.Equal(6f, b[7]);
        Assert.All(p.Weights.Skip(4), w => Assert.Equal(0f, w));
    }

    [Fact]
    public void Generated_normals_use_flat_adjacency_and_preserve_a_lone_vertex_normal()
    {
        const int size = 100, vertices = (size + 1) * (size + 1) + 1;
        var positions = new float[3 * vertices]; var triangles = new List<int>();
        for (int y = 0; y <= size; y++) for (int x = 0; x <= size; x++)
        {
            int v = y * (size + 1) + x; positions[3 * v] = x; positions[3 * v + 1] = y;
            if (x == size || y == size) continue;
            int next = v + size + 1; triangles.AddRange(new[] { v, v + 1, next + 1, v, next + 1, next });
        }
        positions[3 * (vertices - 1)] = 2f;
        var faces = triangles.ToArray(); VehicleProbe.BlenderVertexNormals(positions, faces, null);
        var allocated = Counter(); long before = allocated();
        var normals = VehicleProbe.BlenderVertexNormals(positions, faces, null);
        Assert.InRange(allocated() - before, 0L, 80L * vertices + 16384);
        for (int v = 0; v < vertices - 1; v++) Assert.Equal(new[] { 0f, 0f, 1f }, normals.Skip(3 * v).Take(3));
        Assert.Equal(new[] { 1f, 0f, 0f }, normals.Skip(3 * (vertices - 1)));
    }
}
