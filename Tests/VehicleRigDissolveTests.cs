using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>The soup tiers' operators in C# (BlenderDissolve, BlenderPolyfill): the limited dissolve on flat strips, the n-gon face
/// normal, the ear clipping with its concave kd-tree, the beautify flip, the quad rule - the rules the drill holds against Blender,
/// pinned here on shapes a unit test can hold.</summary>
public class VehicleRigDissolveTests
{
    /// <summary>A flat (nx x ny)-vertex grid of two triangles per cell as a stage mesh, with UVs.</summary>
    static BlenderRigAnim.MeshData Grid(int nx, int ny, float bump = 0f)
    {
        int nv = nx * ny; var P = new float[3 * nv]; var faces = new List<int>();
        for (int j = 0; j < ny; j++) for (int i = 0; i < nx; i++) { int v = j * nx + i; P[3 * v] = i / (nx - 1f); P[3 * v + 1] = j / (ny - 1f); P[3 * v + 2] = bump * (float)Math.Sin(5.0 * i / (nx - 1f)); }
        for (int j = 0; j < ny - 1; j++) for (int i = 0; i < nx - 1; i++) { int a = j * nx + i, b = a + 1, c = a + nx + 1, d = a + nx; faces.AddRange(new[] { a, b, c, a, c, d }); }
        var d2 = new BlenderRigAnim.MeshData { Vertices = nv, Positions = P, Faces = faces.ToArray(), FaceMaterial = new int[faces.Count / 3], FaceSharp = new bool[faces.Count / 3] };
        var uv = new float[2 * faces.Count]; for (int c = 0; c < faces.Count; c++) { uv[2 * c] = P[3 * faces[c]]; uv[2 * c + 1] = P[3 * faces[c] + 1]; }
        d2.UvAll.Add(uv); d2.Uv = uv;
        var edges = new List<int>(); var seen = new HashSet<long>();
        for (int f = 0; f < faces.Count / 3; f++) for (int k = 0; k < 3; k++)
        {
            int a = faces[3 * f + k], b = faces[3 * f + (k + 1) % 3]; long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
            if (seen.Add(key)) { edges.Add(Math.Min(a, b)); edges.Add(Math.Max(a, b)); }
        }
        d2.Edges = edges.ToArray();
        return d2;
    }

    static BMesh Build(BlenderRigAnim.MeshData d, BlenderWeld.Layers layers)
    {
        var m = typeof(BlenderVehicleRig).GetMethod("FromMeshData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (BMesh)m.Invoke(null, new object[] { d, layers, true, false });
    }

    static List<int> Alive(bool[] alive, int count) { var l = new List<int>(); for (int i = 0; i < count; i++) if (alive[i]) l.Add(i); return l; }

    [Fact]
    public void A_flat_grid_dissolves_to_one_quad_and_triangulates_back_to_two_triangles()
    {
        var d = Grid(5, 5); var layers = new BlenderWeld.Layers(); var bm = Build(d, layers);
        Assert.Equal(32, bm.TotFace);
        BlenderDissolve.DissolveLimit(bm, layers, (float)(5.0 * Math.PI / 180.0), Alive(bm.VAlive, bm.VertCount), Alive(bm.EAlive, bm.EdgeCount));
        BlenderDissolve.FaceNormalsUpdateAll(bm);
        // every interior edge of a flat sheet is under the angle: one face; every straight-run vertex collapsed: the four corners
        Assert.Equal(1, bm.TotFace); Assert.Equal(4, bm.TotVert); Assert.Equal(4, bm.TotEdge);
        int f = Enumerable.Range(0, bm.FaceCount).First(i => bm.FAlive[i]);
        Assert.Equal(4, bm.FLen[f]);
        Assert.Equal(1f, Math.Abs(bm.FNo[3 * f + 2]), 5);   // the quad's normal along z (normal_quad_v3)
        int cut = BlenderPolyfill.TriangulateAll(bm, layers);
        Assert.Equal(1, cut); Assert.Equal(2, bm.TotFace); Assert.Equal(5, bm.TotEdge);
        for (int i = 0; i < bm.FaceCount; i++) if (bm.FAlive[i]) Assert.Equal(3, bm.FLen[i]);
        // the kept corners carry their UVs: each corner's UV equals its vertex's position in the unit square
        for (int i = 0; i < bm.FaceCount; i++)
        {
            if (!bm.FAlive[i]) continue;
            int l = bm.FLFirst[i], l0 = l;
            do { int v = bm.LV[l]; Assert.Equal(bm.VCo[3 * v], layers.Uv[0][2 * l]); Assert.Equal(bm.VCo[3 * v + 1], layers.Uv[0][2 * l + 1]); l = bm.LNext[l]; } while (l != l0);
        }
    }

    [Fact]
    public void A_bumpy_grid_keeps_its_faces_under_the_angle_limit()
    {
        var d = Grid(5, 5, 0.5f); var layers = new BlenderWeld.Layers(); var bm = Build(d, layers);
        BlenderDissolve.DissolveLimit(bm, layers, (float)(5.0 * Math.PI / 180.0), Alive(bm.VAlive, bm.VertCount), Alive(bm.EAlive, bm.EdgeCount));
        // the bump depends on x alone: each column of cells is one plane (4 strips), the edges between the strips bend over the
        // angle and stay; the vertices along those edges lie on a straight run and collapse: the strips' two ends per column
        Assert.Equal(4, bm.TotFace);
        Assert.Equal(10, bm.TotVert);
        for (int i = 0; i < bm.FaceCount; i++) if (bm.FAlive[i]) Assert.Equal(4, bm.FLen[i]);
    }

    [Fact]
    public void Newells_normal_of_a_flat_hexagon_points_along_z()
    {
        var bm = new BMesh(); var vs = new int[6]; var es = new int[6];
        for (int i = 0; i < 6; i++) { double a = i * Math.PI / 3; vs[i] = bm.VertCreate((float)Math.Cos(a), (float)Math.Sin(a), 0f); }
        for (int i = 0; i < 6; i++) es[i] = bm.EdgeCreate(vs[i], vs[(i + 1) % 6]);
        int f = bm.FaceCreate(vs, es, 6);
        BlenderDissolve.FaceNormalUpdate(bm, f);
        Assert.Equal(0f, bm.FNo[3 * f], 6); Assert.Equal(0f, bm.FNo[3 * f + 1], 6); Assert.Equal(1f, bm.FNo[3 * f + 2], 6);
    }

    static bool Inside(float[] co, int n, float px, float py)
    {
        // the ring is a simple polygon: a point is inside when a ray crosses it an odd number of times
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = co[2 * i], yi = co[2 * i + 1], xj = co[2 * j], yj = co[2 * j + 1];
            if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    [Theory]
    [InlineData(new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }, 2)]                                    // a square
    [InlineData(new float[] { 0, 0, 2, 0, 2, 1, 1, 1, 1, 2, 0, 2 }, 4)]                           // an L: one concave corner, the kd-tree path
    [InlineData(new float[] { 0, 0, 3, 0, 3, 3, 2, 3, 2, 1, 1, 1, 1, 3, 0, 3 }, 6)]                // a U: two concave corners
    public void The_ear_clipping_covers_the_polygon_with_n_minus_2_triangles_inside_it(float[] co, int tris)
    {
        int n = co.Length / 2;
        // these rings run counter-clockwise: Blender's sign is -1 for them (cross_poly_v2 > 0); 0 lets the port detect it
        var result = BlenderPolyfill.Calc(co, n, 0);
        Assert.Equal(result.Select(t => string.Join(" ", t)), BlenderPolyfill.Calc(co, n, -1).Select(t => string.Join(" ", t)));
        Assert.Equal(tris, result.Count);
        foreach (var t in result)
        {
            Assert.Equal(3, new HashSet<int>(t).Count);
            float cx = (co[2 * t[0]] + co[2 * t[1]] + co[2 * t[2]]) / 3f, cy = (co[2 * t[0] + 1] + co[2 * t[1] + 1] + co[2 * t[2] + 1]) / 3f;
            Assert.True(Inside(co, n, cx, cy), $"triangle {t[0]} {t[1]} {t[2]} lies outside");
        }
        // every triangle's area counts once: the sum is the polygon's
        float area = 0f; foreach (var t in result) area += Math.Abs((co[2 * t[1]] - co[2 * t[0]]) * (co[2 * t[2] + 1] - co[2 * t[0] + 1]) - (co[2 * t[2]] - co[2 * t[0]]) * (co[2 * t[1] + 1] - co[2 * t[0] + 1])) / 2f;
        float poly = 0f; for (int i = 0, j = n - 1; i < n; j = i++) poly += (co[2 * j] * co[2 * i + 1] - co[2 * i] * co[2 * j + 1]) / 2f;
        Assert.Equal(Math.Abs(poly), area, 4);
        BlenderPolyfill.Beautify(co, n, result);
        Assert.Equal(tris, result.Count);
        foreach (var t in result) Assert.Equal(3, new HashSet<int>(t).Count);
    }

    [Fact]
    public void The_beautify_rotates_a_thin_diagonal_to_the_short_one()
    {
        // a flat kite: the ear clipping may cut the long diagonal; beautify prefers the diagonal that gives the rounder triangles
        var co = new float[] { 0, 0, 4, -0.2f, 8, 0, 4, 0.2f };
        var tris = BlenderPolyfill.Calc(co, 4, 1);
        BlenderPolyfill.Beautify(co, 4, tris);
        Assert.Equal(2, tris.Count);
        bool hasShort = tris.All(t => t.Contains(1) && t.Contains(3));
        Assert.True(hasShort, "the short diagonal (1-3) splits the kite after beautify");
        // the rule's contract: the quad's current diagonal is (2-4); a negative value says (1-3) is the better one
        Assert.True(BlenderPolyfill.QuadRotateCalcXY(4, -0.2f, 0, 0, 4, 0.2f, 8, 0, false, out _) < 0f, "from the long diagonal, the short one is better");
        Assert.True(BlenderPolyfill.QuadRotateCalcXY(0, 0, 4, -0.2f, 8, 0, 4, 0.2f, false, out _) > 0f, "from the short diagonal, the long one is worse");
    }

    [Fact]
    public void A_chunk_cannot_reuse_an_edge_spliced_away_by_an_earlier_chunk()
    {
        // Blender 5.1.2: the right cell joins (0,1,3) and (1,2,3), then removes collinear vertex 1.
        // Its new (0,2) edge splices away the existing edge used by BOTH faces of the left cell.
        // The next Python operator rejects that removed BMEdge before it can change the mesh.
        var positions = new List<float> { 0,0,0, 1.5f,0,0, 3,0,0, 1.5f,2,0, -10,1,0, -10,-1,0 };
        var faces = new List<int> { 0,1,3, 1,2,3, 0,2,4, 0,5,2 };
        foreach (int side in new[] { -1, 1 }) for (int i = 0; i < 1999; i++)
        {
            float x = side * (10f + i * .002f); int v = positions.Count / 3;
            positions.AddRange(new[] { x,10f,0f, x+.001f,10f,0f, x,10.001f,0f });
            faces.AddRange(new[] { v, v+1, v+2 });
        }
        var edges = new List<int>(); var seen = new HashSet<long>();
        for (int c = 0; c < faces.Count; c++)
        {
            int a = faces[c], b = faces[c - c%3 + (c%3+1)%3];
            long key = ((long)Math.Min(a,b)<<32) | (uint)Math.Max(a,b);
            if (seen.Add(key)) edges.AddRange(new[] { a, b });
        }
        var mesh = new BlenderRigAnim.MeshData { Vertices = positions.Count/3, Positions = positions.ToArray(), Faces = faces.ToArray(), Edges = edges.ToArray(), FaceMaterial = new int[faces.Count/3], FaceSharp = new bool[faces.Count/3] };
        var scene = new BlenderRigAnim.Stage1();
        var obj = new BlenderRigAnim.SceneObject { Name = "Shroud", Type = "MESH", Mesh = mesh };
        scene.Objects.Add(obj);
        var args = new BlenderVehicleRig.Args(); args.Names["rigging"] = new List<string> { "Shroud" }; args.Reduce["rigging"] = 80;
        BlenderVehicleRig.RunStage2(scene, args);
        Assert.Equal("TypeError: edges: 0 BMEdge has been removed", scene.Dies);
        Assert.Same(mesh, obj.Mesh); // the script never reaches to_mesh on the failed object
        Assert.Empty(scene.Logs2);
    }
}
