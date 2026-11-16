using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

/// <summary>BlenderBvh against Blender 5.1's own mathutils.bvhtree.BVHTree (2026-11-16): blender_bvh_rays.txt is what
/// tools/vehiclerig-drill/blender_bvh_oracle.py printed - triangle soups (one triangle; five; forty; seven hundred), each with
/// plain duplicates, reversed twins on the same vertices and a grid of coplanar triangles sharing edges, and per soup 520
/// casts with Blender's answer: random rays with and without a distance limit, axis-aligned rays through the grid's vertices
/// and edges, rays that start on a corner or a centre. Every index and every distance must be Blender's to the bit: on a
/// tie between two triangles at one distance the index is the one Blender's walk reaches first, which only its own tree
/// (BLI_kdopbvh.cc's implicit build) reproduces. The same file holds the Two-sided pass's 64 directions as the script's own
/// _two_sided_dirs printed them.</summary>
public class BlenderBvhTests
{
    static float F(string hex) => BitConverter.ToSingle(BitConverter.GetBytes(uint.Parse(hex, NumberStyles.HexNumber)), 0);
    static string Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");
    static string[] Lines() => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "blender_bvh_rays.txt"));

    [Theory]
    [InlineData("one", 1 + 18)]
    [InlineData("five", 5 + 2 + 18)]
    [InlineData("small", 40 + 6 + 18)]
    [InlineData("mid", 700 + 40 + 18)]
    public void Every_cast_gives_Blenders_triangle_and_distance(string soup, int triangles)
    {
        var lines = Lines();
        var head = lines.Single(l => l.StartsWith("SOUP " + soup + " ", StringComparison.Ordinal)).Split(' ');
        int nv = int.Parse(head[2]), nt = int.Parse(head[3]);
        Assert.Equal(triangles, nt);
        var co = new float[3 * nv]; for (int i = 0; i < 3 * nv; i++) co[i] = F(head[4 + i]);
        var tris = new int[3 * nt]; for (int i = 0; i < 3 * nt; i++) tris[i] = int.Parse(head[4 + 3 * nv + i]);
        var bvh = new BlenderBvh(co, tris);
        int cast = 0, hits = 0, limited = 0; var wrong = new List<string>();
        foreach (var l in lines.Where(l => l.StartsWith("RAY " + soup + " ", StringComparison.Ordinal)))
        {
            var t = l.Split(' ');   // RAY name i ox oy oz dx dy dz maxdist -> index dist
            float max = t[9] == "none" ? float.MaxValue : F(t[9]);
            int index = bvh.RayCast(F(t[3]), F(t[4]), F(t[5]), F(t[6]), F(t[7]), F(t[8]), max, out float dist);
            string got = index + " " + (index < 0 ? "none" : Bits(dist)), want = t[11] + " " + t[12];
            if (got != want && wrong.Count < 5) wrong.Add("ray " + t[2] + ": Blender " + want + ", here " + got);
            cast++; if (t[11] != "-1") hits++; if (t[9] != "none") limited++;
        }
        Assert.Equal(520, cast);
        Assert.True(hits >= 60 && cast - hits >= 60 && limited >= 100, "the casts no longer cover hits, misses and limits (" + hits + " hits, " + limited + " limited)");
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public void A_tree_without_a_triangle_misses()
    {
        var bvh = new BlenderBvh(new float[0], new int[0]);
        Assert.Equal(-1, bvh.RayCast(0f, 0f, 0f, 0f, 0f, 1f, float.MaxValue, out _));
    }

    [Fact]
    public void The_two_sided_directions_are_the_scripts()
    {
        var want = Lines().Where(l => l.StartsWith("DIR ", StringComparison.Ordinal)).Select(l => l.Split(' ')).ToList();
        var got = BlenderVehicleRig.TwoSidedDirs(64);
        Assert.Equal(64, want.Count);
        for (int k = 0; k < 64; k++)
            Assert.Equal(want[k][2] + " " + want[k][3] + " " + want[k][4], Bits(got[k][0]) + " " + Bits(got[k][1]) + " " + Bits(got[k][2]));
    }

    [Theory]   // EXPP_FloatsAreEqual with one step: what Vector == Vector means
    [InlineData(1f, 1f, true)]
    [InlineData(1f, 1.0000001f, true)]      // one step up
    [InlineData(1f, 1.0000002f, false)]     // two steps
    [InlineData(0f, -0f, true)]
    [InlineData(1.4e-45f, -1.4e-45f, false)]   // one step either side of zero: two apart
    [InlineData(0f, 1.4e-45f, true)]
    [InlineData(1f, -1f, false)]
    public void A_vector_equals_another_within_one_float_step(float a, float b, bool equal)
    {
        Assert.Equal(equal, BlenderVehicleRig.PyVectorEquals(new[] { a, 2f, 3f }, new[] { b, 2f, 3f }));
        Assert.Equal(equal, BlenderVehicleRig.PyVectorEquals(new[] { 2f, 3f, b }, new[] { 2f, 3f, a }));
    }
}
