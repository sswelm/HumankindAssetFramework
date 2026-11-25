using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>bmesh.ops.dissolve_limit called WITHOUT a delimit, as vehicle_rig.py calls it, runs with delimit = {'NORMAL'}: an
/// operator's unset flag slot takes the first value of its enum (bmo_op_slots_init), and the dissolve's enum starts with NORMAL.
/// So an edge whose two faces are wound against each other is never joined, and a vertex on such an edge is judged by its plain
/// edge angle. The port ran with delimit 0 until 2026-11-25 (the Roberts monitor's railing: 123,842 vertices where Blender left
/// 124,369). Every expected count below is Blender 5.1.2's on the same fixture with the slot unset; the comment beside it is what
/// delimit=set() gives there - the result the port had.</summary>
[Collection("native kernels switch")]
public class DissolveDelimitTests
{
    static readonly float Angle = (float)(5.0 * (Math.PI / 180.0));

    static BMesh Build(float[] co, int[][] faces)
    {
        var bm = new BMesh();
        for (int v = 0; v < co.Length / 3; v++) bm.VertCreate(co[3 * v], co[3 * v + 1], co[3 * v + 2]);
        foreach (var f in faces)
        {
            var es = new int[f.Length];
            for (int i = 0; i < f.Length; i++)
            {
                int a = f[i], b = f[(i + 1) % f.Length];
                es[i] = bm.EdgeExists(a, b); if (es[i] == BMesh.None) es[i] = bm.EdgeCreate(a, b);
            }
            BlenderDissolve.FaceNormalUpdate(bm, bm.FaceCreate(f, es, f.Length));
        }
        return bm;
    }

    static List<int> Alive(bool[] alive, int count) { var l = new List<int>(); for (int i = 0; i < count; i++) if (alive[i]) l.Add(i); return l; }

    /// <summary>One dissolve over every vertex and, when asked, every edge - through the managed code or the native kernel.</summary>
    static BMesh Dissolve(float[] co, int[][] faces, bool edges, bool native)
    {
        var bm = Build(co, faces); var layers = new BlenderWeld.Layers();
        var vs = Alive(bm.VAlive, bm.VertCount); var es = edges ? Alive(bm.EAlive, bm.EdgeCount) : new List<int>();
        if (!native)
        {
            BlenderDissolve.DissolveLimit(bm, layers, Angle, vs, es);
            BlenderDissolve.FaceNormalsUpdateAll(bm);
            return bm;
        }
        Assert.True(HafNativeKernels.Available, HafNativeKernels.Status);   // a managed fallback must not pass as the kernel
        string death = HafNativeKernels.DissolveLimit(bm, layers.Uv, layers.CornerColor, ref layers.CustomNormal, Angle, new[] { (es, vs) }, skipEmpty: false);
        Assert.Null(death);
        return bm;
    }

    static int[] FaceLengths(BMesh bm) => Enumerable.Range(0, bm.FaceCount).Where(f => bm.FAlive[f]).Select(f => bm.FLen[f]).OrderBy(n => n).ToArray();

    static readonly float[] Square = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_coplanar_triangles_wound_against_each_other_stay_two_faces(bool native)
    {
        // the control: wound alike, the pair is one quad
        var same = Dissolve(Square, new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } }, true, native);
        Assert.Equal((4, 4, 1), (same.TotVert, same.TotEdge, same.TotFace));
        // the second triangle the other way round: the shared edge is a NORMAL delimiter (delimit=set(): 4 verts, 4 edges, 1 face)
        var bm = Dissolve(Square, new[] { new[] { 0, 1, 2 }, new[] { 0, 3, 2 } }, true, native);
        Assert.Equal((4, 5, 2), (bm.TotVert, bm.TotEdge, bm.TotFace));
        Assert.Equal(new[] { 3, 3 }, FaceLengths(bm));
        // neither face was turned over on the way: the second still runs 0 -> 3 -> 2
        int l = bm.FLFirst[1]; var ring = new List<int>(); int l0 = l; do { ring.Add(bm.LV[l]); l = bm.LNext[l]; } while (l != l0);
        Assert.Equal(new[] { 0, 3, 2 }, ring);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_strip_joins_only_across_the_edge_whose_faces_are_wound_alike(bool native)
    {
        // four coplanar triangles wound -, +, +, -: the middle pair is one quad, its two neighbours stay (delimit=set(): one face
        // of 4 verts and 4 edges)
        var co = new float[] { 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, 1, 0, 2, 0, 0, 2, 1, 0 };
        var bm = Dissolve(co, new[] { new[] { 0, 2, 1 }, new[] { 2, 1, 3 }, new[] { 2, 3, 4 }, new[] { 4, 5, 3 } }, true, native);
        Assert.Equal((6, 8, 3), (bm.TotVert, bm.TotEdge, bm.TotFace));
        Assert.Equal(new[] { 3, 3, 4 }, FaceLengths(bm));
    }

    // a = 0, v = 1, b = 2: two quads on the edges a-v and v-b, both running a -> v -> b (wound against each other across them)

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_vertex_on_a_delimiter_edge_is_judged_by_its_plain_edge_angle_and_collapses_under_the_limit(bool edges, bool native)
    {
        // the quads lie flat on either side, normals opposed (180 degrees apart); v bends the run a-v-b by 3.4 degrees. Blender:
        // v collapses (its cost is the 3.4 degrees) and the two faces stay. delimit=set() scales the angle by the face angle
        // (3.4 * 180/90 = 6.9 degrees): v stays with the vertices alone (5 verts, 6 edges, 2 faces), and with the edges the pair
        // is joined into one face (4 verts, 4 edges, 1 face).
        var co = new float[] { 0, 0, 0, 1, 0.03f, 0, 2, 0, 0, 1, 1, 0, 1, -1, 0 };
        var bm = Dissolve(co, new[] { new[] { 0, 1, 2, 3 }, new[] { 0, 1, 2, 4 } }, edges, native);
        Assert.Equal((4, 5, 2), (bm.TotVert, bm.TotEdge, bm.TotFace));
        Assert.False(bm.VAlive[1], "the bend vertex collapsed");
        Assert.Equal(new[] { 3, 3 }, FaceLengths(bm));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_vertex_on_a_delimiter_edge_between_folded_faces_keeps_a_bend_over_the_limit(bool edges, bool native)
    {
        // the quads lie on the SAME side, folded onto each other: their normals are equal (0 degrees apart) and v bends the run
        // by 33 degrees. Blender: v stays (its cost is the 33 degrees). delimit=set() scales the angle by the face angle (zero):
        // v collapses (4 verts, 5 edges, 2 faces).
        var co = new float[] { 0, 0, 0, 1, 0.3f, 0, 2, 0, 0, 1, 1, 0, 1, 1.5f, 0 };
        var bm = Dissolve(co, new[] { new[] { 0, 1, 2, 3 }, new[] { 0, 1, 2, 4 } }, edges, native);
        Assert.Equal((5, 6, 2), (bm.TotVert, bm.TotEdge, bm.TotFace));
        Assert.True(bm.VAlive[1], "the bend vertex stayed");
        Assert.Equal(new[] { 4, 4 }, FaceLengths(bm));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_vertex_between_faces_wound_alike_keeps_the_scaled_rule(bool native)
    {
        // the control for the two cases above: the same small bend between two flat quads wound alike - the vertex collapses
        // and, with the edges, the pair becomes one face (Blender, with the slot unset or empty: 4 verts, 4 edges, 1 face)
        var co = new float[] { 0, 0, 0, 1, 0.03f, 0, 2, 0, 0, 1, 1, 0, 1, -1, 0 };
        var bm = Dissolve(co, new[] { new[] { 0, 1, 2, 3 }, new[] { 0, 4, 2, 1 } }, true, native);
        Assert.Equal((4, 4, 1), (bm.TotVert, bm.TotEdge, bm.TotFace));
        var verts = Dissolve(co, new[] { new[] { 0, 1, 2, 3 }, new[] { 0, 4, 2, 1 } }, false, native);
        Assert.Equal((4, 5, 2), (verts.TotVert, verts.TotEdge, verts.TotFace));
    }
}
