using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

public class ParallelNormalsTests
{
    static (float[] positions, int[] faces, float[] normals, bool[] sharp) Grid(int size, bool tiny)
    {
        var positions = new List<float>(); var normals = new List<float>(); var faces = new List<int>();
        float scale = tiny ? 1e-18f : 1f;
        for (int y = 0; y <= size; y++) for (int x = 0; x <= size; x++)
        {
            positions.AddRange(new[] { x * scale, y * scale, (float)((x % 3 - y % 4) * .125) * scale });
            normals.AddRange((x + y) % 11 == 0 ? new[] { float.NaN, float.NaN, float.NaN } : new[] { .25f, -.5f, 1f });
        }
        // Unreferenced vertices exercise the lone-vertex path as well as fans crossing every worker boundary.
        positions.AddRange(new[] { -0f, 0f, 0f, 2f, 3f, 4f }); normals.AddRange(new[] { 0f, 0f, 0f, 0f, 0f, 1f });
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            int a = y * (size + 1) + x, b = a + 1, c = a + size + 1, d = c + 1;
            faces.AddRange(new[] { a, b, d, a, d, c });
        }
        return (positions.ToArray(), faces.ToArray(), normals.ToArray(), Enumerable.Range(0, faces.Count / 3).Select(f => f % 7 == 0).ToArray());
    }
    static void Bits(float[] expected, float[] actual)
    {
        var left = new byte[expected.Length * 4]; var right = new byte[actual.Length * 4];
        Buffer.BlockCopy(expected, 0, left, 0, left.Length); Buffer.BlockCopy(actual, 0, right, 0, right.Length);
        Assert.Equal(left, right);
    }
    [Theory]
    [InlineData(2, false)] [InlineData(4, false)] [InlineData(8, false)]
    [InlineData(2, true)] [InlineData(4, true)] [InlineData(8, true)]
    public void Every_normal_path_keeps_serial_bits_across_worker_boundaries(int threads, bool tiny)
    {
        var g = Grid(21, tiny);
        var edges = new HashSet<long>(VehicleProbe.PairKeyComparer.Instance) { VehicleProbe.SharpEdgeKey(0, 1), VehicleProbe.SharpEdgeKey(23, 24) };
        var serial = VehicleProbe.EncodeCustomShorts(g.positions, g.faces, g.normals, g.sharp, 1);
        for (int run = 0; run < 3; run++)
        {
            var parallel = VehicleProbe.EncodeCustomShorts(g.positions, g.faces, g.normals, g.sharp, threads);
            Assert.Equal(serial.d0, parallel.d0); Assert.Equal(serial.d1, parallel.d1);
            Bits(VehicleProbe.DecodeCustomShorts(g.positions, g.faces, g.sharp, serial.d0, serial.d1, edges, 1),
                 VehicleProbe.DecodeCustomShorts(g.positions, g.faces, g.sharp, parallel.d0, parallel.d1, edges, threads));
            Bits(VehicleProbe.BlenderCornerNormals(g.positions, g.faces, g.sharp, serial.d0, serial.d1, edges, 1),
                 VehicleProbe.BlenderCornerNormals(g.positions, g.faces, g.sharp, parallel.d0, parallel.d1, edges, threads));
            Bits(VehicleProbe.BlenderCornerNormals(g.positions, g.faces, g.sharp, null, null, edges, 1),
                 VehicleProbe.BlenderCornerNormals(g.positions, g.faces, g.sharp, null, null, edges, threads));
            Bits(VehicleProbe.BlenderFanNormals(g.positions, g.faces, g.sharp, 1), VehicleProbe.BlenderFanNormals(g.positions, g.faces, g.sharp, threads));
            var data = new short[2 * g.faces.Length];
            for (int c = 0; c < g.faces.Length; c++) { data[2 * c] = serial.d0[c]; data[2 * c + 1] = serial.d1[c]; }
            var vectors = VehicleProbe.DecodeBMeshCustomNormals(g.positions, g.faces, g.sharp, data, 1);
            Bits(vectors, VehicleProbe.DecodeBMeshCustomNormals(g.positions, g.faces, g.sharp, data, threads));
            Assert.Equal(VehicleProbe.EncodeBMeshCustomNormals(g.positions, g.faces, g.sharp, vectors, 1),
                         VehicleProbe.EncodeBMeshCustomNormals(g.positions, g.faces, g.sharp, vectors, threads));
        }
    }
    [Fact]
    public void Automatic_large_mesh_processing_and_concurrent_calls_keep_serial_bits()
    {
        var g = Grid(129, false);
        var serial = VehicleProbe.EncodeCustomShorts(g.positions, g.faces, g.normals, g.sharp, 1);
        Parallel.For(0, 3, i =>
        {
            var actual = VehicleProbe.EncodeCustomShorts(g.positions, g.faces, g.normals, g.sharp);
            Assert.Equal(serial.d0, actual.d0); Assert.Equal(serial.d1, actual.d1);
        });
    }
}
