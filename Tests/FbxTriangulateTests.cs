using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

// Step 3 of the native FBX reader: editor/FbxTriangulate.cs, ufbx_catch_triangulate_face ported. The triangle lists
// here are ufbx's own answers (ufbxdump --full on tools/fbx-drill/fbx_scene_fixtures.py's quads, ngons,
// ngons_reversed and ngons_degenerate scenes, 2026-11-01); the bit-level claim on every file is tools/fbx_drill.sh's.
public class FbxTriangulateTests
{
    static FbxNode N(string name, params object[] props) { var n = new FbxNode { Name = name }; n.Properties.AddRange(props); return n; }
    static FbxNode With(FbxNode n, params FbxNode[] children) { n.Children.AddRange(children); return n; }
    static FbxNode Object(string cls, long id, string name, string sub, params FbxNode[] children) => With(N(cls, id, name + "\0\u0001" + cls, sub), children);
    static FbxNode C(string kind, long src, long dst) => N("C", kind, src, dst);

    /// <summary>One mesh of the given faces (vertex index lists) over flat xyz vertices, read through the scene layer.</summary>
    static FbxMeshData Mesh(double[] vertices, params int[][] faces)
    {
        var idx = new List<int>();
        foreach (var f in faces) { idx.AddRange(f.Take(f.Length - 1)); idx.Add(~f[f.Length - 1]); }
        var d = new FbxDocument { Version = 7400 };
        d.Nodes.Add(With(N("Objects"), Object("Model", 1, "Node", "Mesh"),
            Object("Geometry", 30, "Poly", "Mesh", N("Vertices", vertices), N("PolygonVertexIndex", idx.ToArray()), N("GeometryVersion", 124))));
        d.Nodes.Add(With(N("Connections"), C("OO", 1, 0), C("OO", 30, 1)));
        return FbxScene.Read(d).Meshes[0];
    }

    static int[] Range(int from, int count) => Enumerable.Range(from, count).ToArray();
    static int[] Tri(params int[] corners) => corners;
    static void AssertTriangles(FbxMeshData m, params int[] expected) => Assert.Equal(expected, m.Triangles);

    static double[] Star(int points, double outer, double inner, double phase = 0.0)
    {
        var xyz = new List<double>();
        for (int i = 0; i < points * 2; i++)
        {
            double r = i % 2 == 0 ? outer : inner, a = phase + Math.PI * i / points;
            xyz.Add(r * Math.Cos(a)); xyz.Add(r * Math.Sin(a)); xyz.Add(0.0);
        }
        return xyz.ToArray();
    }

    [Fact]
    public void Triangles_pass_through_and_short_faces_yield_nothing()
    {
        var m = Mesh(new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 }, new[] { 0, 1, 2 }, new[] { 3, 0 }, new[] { 1, 3, 2 });
        Assert.Equal(2, m.NumTriangles);
        AssertTriangles(m, 0, 1, 2, 5, 6, 7);   // corner indices, not vertex indices: the 2-gon's corners 3 and 4 are skipped
        Assert.Same(m.Triangles, m.Triangles);  // computed once
    }

    [Fact]
    public void Quads_split_along_the_shorter_diagonal_unless_a_corner_crosses_it()
    {
        var quads = new double[] {
            0, 0, 0, 2, 0, 0, 2, 1, 0, 0, 1, 0,             // a flat trapezoid: the shorter diagonal splits it
            0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1,             // a square: equal diagonals, the first wins (<=)
            0, 0, 2, 1, 0, 2, 1, 1, 2, 0, 1, 4,             // a square with one corner lifted
            0, 0, 3, 3, 0, 3, 3, 3, 3, 0, 0.1, 3,           // a near-fold the other way
            0, 0, 4, 1, 1, 4, 2, 2, 4, 3, 3, 4,             // four collinear points: zero normals, the dot tie
            0, 0, 5, 1, 3, 5, 0, 2.5, 5, -1, 3, 5,          // a dart: the SHORTER diagonal (v1-v3) is crossed by the reflex corner, the longer one splits
            0, 0, 6, 1, 0, 7, 1, 1, 6, 0, 1, 7,             // a twisted quad: both diagonals crossed equally, the first wins (>=)
            0, 0, 8, 1, 0, 9, 1, 1, 8, 0, 1, 9.5 };         // twisted unevenly: the less crossed diagonal wins
        var m = Mesh(quads, Range(0, 4), Range(4, 4), Range(8, 4), Range(12, 4), Range(16, 4), Range(20, 4), Range(24, 4), Range(28, 4));
        AssertTriangles(m,
            0, 1, 2, 2, 3, 0,
            4, 5, 6, 6, 7, 4,
            8, 9, 10, 10, 11, 8,
            13, 14, 15, 15, 12, 13,
            16, 17, 18, 18, 19, 16,
            20, 21, 22, 22, 23, 20,     // the dart: v0-v2, the longer diagonal, because v1-v3 is crossed
            24, 25, 26, 26, 27, 24,
            29, 30, 31, 31, 28, 29);
    }

    static readonly double[] L = { 0, 0, 0, 3, 0, 0, 3, 1, 0, 1, 1, 0, 1, 3, 0, 0, 3, 0 };
    static readonly double[] U = { 0, 0, 0, 3, 0, 0, 3, 3, 0, 2, 3, 0, 2, 1, 0, 1, 1, 0, 1, 3, 0, 0, 3, 0 };

    [Fact]
    public void Ngons_are_ear_clipped_as_ufbx_clips_them()
    {
        var verts = L.Concat(U).Concat(Star(8, 2.0, 0.8)).Concat(Star(4, 1.0, 1.0)).ToArray();
        var m = Mesh(verts, Range(0, 6), Range(6, 8), Range(14, 16), Range(30, 8));
        Assert.Equal(4 + 6 + 14 + 6, m.NumTriangles);
        AssertTriangles(m,
            5, 0, 3, 0, 1, 3, 1, 2, 3, 4, 5, 3,                                                   // the L: no triangle crosses the notch
            13, 6, 11, 6, 7, 10, 7, 8, 9, 7, 9, 10, 6, 10, 11, 12, 13, 11,                       // the U
            29, 14, 21, 14, 15, 21, 15, 16, 17, 15, 17, 19, 17, 18, 19, 15, 19, 21, 19, 20, 21,   // the star of 8
            27, 21, 23, 21, 22, 23, 27, 23, 25, 23, 24, 25, 26, 27, 25, 27, 28, 21, 28, 29, 21,
            37, 30, 33, 30, 31, 33, 31, 32, 33, 34, 35, 33, 35, 36, 33, 36, 37, 33);              // the convex octagon
    }

    [Fact]
    public void Reversed_ngons_flip_the_reflex_set()
    {
        var verts = L.Concat(U).ToArray();
        var m = Mesh(verts, Range(0, 6).Reverse().ToArray(), Range(6, 8).Reverse().ToArray());
        AssertTriangles(m,
            5, 0, 2, 0, 1, 2, 3, 4, 2, 4, 5, 2,
            13, 6, 8, 6, 7, 8, 12, 8, 9, 10, 11, 9, 11, 12, 9, 12, 13, 8);
    }

    [Fact]
    public void Degenerate_ngons_fall_back_to_a_fan()
    {
        var dup = new double[] { 0, 0, 0, 2, 0, 0, 2, 0, 0, 2, 2, 0, 0, 2, 0, 0, 2, 0, 0, 1, 0 };   // repeated corners: NaN ear weights
        var touch = new double[] { 0, 0, 0, 2, 0, 0, 1, 1, 0, 2, 2, 0, 0, 2, 0, 1, 1, 0 };          // a corner visited twice
        var m = Mesh(dup.Concat(touch).ToArray(), Range(0, 7), Range(7, 6));
        AssertTriangles(m,
            0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6,
            7, 8, 9, 7, 9, 10, 7, 10, 11, 7, 11, 12);
    }

    [Fact]
    public void A_star_of_160_covers_every_corner_once_per_adjacent_triangle()
    {
        // 160 reflex corners: more than the 127 ufbx's cached KD levels hold, so its slow ranges are walked too
        var m = Mesh(Star(160, 10.0, 4.0, 0.3), Range(0, 320));
        var t = m.Triangles;
        Assert.Equal(318 * 3, t.Length);
        Assert.Equal(Range(0, 320), t.Distinct().OrderBy(i => i));
        // every triangle turns the same way as the polygon (counter-clockwise), none is folded over a notch
        for (int i = 0; i < t.Length; i += 3)
        {
            double ax = m.Vertices[t[i] * 3], ay = m.Vertices[t[i] * 3 + 1], bx = m.Vertices[t[i + 1] * 3], by = m.Vertices[t[i + 1] * 3 + 1], cx = m.Vertices[t[i + 2] * 3], cy = m.Vertices[t[i + 2] * 3 + 1];
            Assert.True((bx - ax) * (cy - ay) - (by - ay) * (cx - ax) > 0, $"triangle {i / 3} is folded");
        }
    }
}
