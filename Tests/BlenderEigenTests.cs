using System;
using System.IO;
using System.Linq;
using Xunit;

/// <summary>Eigen's 4x4 float inverse as Blender reaches it (BlenderEigen.InvertM4): five matrices inverted by the
/// binary's own invert_m4_m4 on 2026-10-07 - reached through bpy.ops.object.parent_set, which stores it in
/// matrix_parent_inverse - bit for bit: three affine, one dense with a non-trivial last row, one bone's matrix_local.
/// (mathutils' inverted() is the adjugate, not this.)</summary>
public class BlenderEigenTests
{
    static float F(string hex) => BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(hex, 16)), 0);
    static uint U(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);

    [Fact]
    public void The_inverse_equals_Blenders_inverted_bit_for_bit()
    {
        foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "eigen_inverses.txt")).Where(l => l.StartsWith("MAT")).Zip(
                 File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "eigen_inverses.txt")).Where(l => l.StartsWith("INV")), (a, b) => (a, b)))
        {
            var m = line.a.Substring(4).Split(' ').Select(F).ToArray();
            var expected = line.b.Substring(4).Split(' ').Select(F).Select(U).ToArray();
            Assert.Equal(expected, BlenderEigen.InvertM4(m).Select(U).ToArray());
        }
    }

    [Fact]
    public void A_singular_matrix_gives_the_zero_matrix()
    {
        var m = new float[16]; m[0] = 1f; m[5] = 1f;   // rank 2
        Assert.All(BlenderEigen.InvertM4(m), x => Assert.Equal(0f, x));
    }
}
