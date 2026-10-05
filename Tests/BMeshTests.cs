using System;
using System.Collections.Generic;
using Xunit;

/// <summary>Blender's BMesh kernel (BMesh): the disk and radial cycle order, LOOPS_OF_VERT, the kills and splices, on
/// hand-made cases where one rule alone decides the order (each walked by hand from the kernel's code). The drill
/// (tools/decimate_drill.sh) holds every registry mesh's link lists, and a scripted sequence of kills and splices, to
/// Blender's own bmesh.</summary>
public class BMeshTests
{
    // one triangle primitive; BlenderMesh lays the edges out as the kernel does (see BlenderMeshTests)
    static BlenderMesh Layout(int vertexCount, params int[] tris)
    {
        var m = new HafModel(); var mesh = new HafMesh();
        mesh.Primitives.Add(new HafPrimitive { Mode = 4, VertexCount = vertexCount, Positions = new float[vertexCount * 3], Indices = tris });
        m.Meshes.Add(mesh);
        return BlenderMesh.FromGltf(m, 0);
    }

    static List<int> L(BMesh bm, Action<int, List<int>> walk, int elem) { var l = new List<int>(); walk(elem, l); return l; }

    // The fan used below: triangles (0, 1, 2) and (0, 2, 3).
    //   edges   e0 = (0,2)  e1 = (0,1)  e2 = (1,2)  e3 = (0,3)  e4 = (2,3)          (BlenderMesh's order)
    //   loops   l0 (v0 on e1)  l1 (v1 on e2)  l2 (v2 on e0)  |  l3 (v0 on e0)  l4 (v2 on e4)  l5 (v3 on e3)
    //   disks   v0: e0 e1 e3   v2: e0 e2 e4      radial of e0: l3 then l2 (the later face heads the cycle)
    static BMesh Fan() => BMesh.FromMesh(Layout(4, 0, 1, 2, 0, 2, 3), null);

    [Fact]
    public void A_new_edge_joins_the_end_of_the_disk_cycle_and_the_head_stays_the_first_edge()
    {
        var bm = new BMesh();
        for (int i = 0; i < 4; i++) bm.VertCreate(0, 0, 0);
        int e0 = bm.EdgeCreate(0, 1), e1 = bm.EdgeCreate(2, 0), e2 = bm.EdgeCreate(0, 3);
        Assert.Equal(e0, bm.VE[0]);
        Assert.Equal(new[] { e0, e1, e2 }, L(bm, bm.EdgesOfVert, 0));
        Assert.Equal(e2, bm.DiskEdgePrev(e0, 0));           // circular: the head's prev is the last
        Assert.Equal(new[] { e1 }, L(bm, bm.EdgesOfVert, 2));
        bm.EdgeKill(e0);                                     // the removed head passes to its next
        Assert.Equal(e1, bm.VE[0]);
        Assert.Equal(new[] { e1, e2 }, L(bm, bm.EdgesOfVert, 0));
        Assert.Equal(BMesh.None, bm.VE[1]);
        Assert.Equal(2, bm.TotEdge);
    }

    [Fact]
    public void The_last_face_on_an_edge_heads_its_radial_cycle_and_a_corners_edge_joins_it_to_the_next_corner()
    {
        // two triangles on the edge (1, 2): (0, 1, 2) then (2, 1, 3)
        var layout = Layout(4, 0, 1, 2, 2, 1, 3);
        Assert.Equal(new[] { 0, 2, 0, 1, 1, 2, 2, 3, 1, 3 }, layout.ValidEdges);   // e0..e4
        var bm = BMesh.FromMesh(layout, null);
        // loops l0 (v0) l1 (v1) l2 (v2) of face 0; l3 (v2) l4 (v1) l5 (v3) of face 1; l1 and l3 sit on e2 = (1,2)
        Assert.Equal(3, bm.EL[2]);                           // face 1's loop, appended later, is the head
        Assert.Equal(new[] { 3, 1 }, L(bm, bm.LoopsOfEdge, 2));
        Assert.True(bm.EdgeIsManifold(2)); Assert.True(bm.EdgeIsBoundary(0)); Assert.False(bm.EdgeIsWire(0));
        Assert.True(bm.EdgeLoopPair(2, out int la, out int lb)); Assert.Equal(3, la); Assert.Equal(1, lb);
        Assert.False(bm.EdgeLoopPair(0, out _, out _));
        // corner k's edge joins corners k and k + 1: face 0 (0,1) (1,2) (2,0) = e1 e2 e0; face 1 (2,1) (1,3) (3,2) = e2 e4 e3
        Assert.Equal(new[] { 1, 2, 0 }, new[] { bm.LE[0], bm.LE[1], bm.LE[2] });
        Assert.Equal(new[] { 2, 4, 3 }, new[] { bm.LE[3], bm.LE[4], bm.LE[5] });
        Assert.Equal(new[] { 0, 1, 2 }, L(bm, bm.LoopsOfFace, 0));
        Assert.Equal(new[] { 3, 4, 5 }, L(bm, bm.LoopsOfFace, 1));
    }

    [Fact]
    public void Loops_of_a_vertex_follow_its_disk_from_the_head_edges_corner_at_it_or_that_corners_next()
    {
        var bm = Fan();
        Assert.Equal(new[] { 0, 1, 3 }, L(bm, bm.EdgesOfVert, 0));
        Assert.Equal(new[] { 0, 2, 4 }, L(bm, bm.EdgesOfVert, 2));
        Assert.Equal(2, bm.DiskFaceVertCount(0));
        // vertex 0: the head e0's radial head l3 sits at 0, so it comes first; its radial next at 0 wraps, so the walk moves
        // to the next disk edge with a corner at 0, e1, and takes l0
        Assert.Equal(new[] { 3, 0 }, L(bm, bm.LoopsOfVert, 0));
        // vertex 2: e0's radial head l3 sits at 0, NOT 2, so the first loop is l3's NEXT in its face, l4 (on e4!); then the
        // walk continues from e4 around the disk (e0 next) and finds l2 - the order is 4, 2, not 2, 4
        Assert.Equal(new[] { 4, 2 }, L(bm, bm.LoopsOfVert, 2));
        Assert.Equal(new[] { 1 }, L(bm, bm.LoopsOfVert, 1));
        Assert.Equal(new int[0], L(bm, bm.LoopsOfVert, 1).FindAll(x => x < 0));
    }

    [Fact]
    public void A_vertex_splice_appends_the_moved_edges_to_the_destination_disk_and_relabels_the_corners()
    {
        // (0, 1, 2) and (3, 4, 5) - two separate triangles; splice vertex 3 into vertex 0
        var bm = BMesh.FromMesh(Layout(6, 0, 1, 2, 3, 4, 5), null);
        // edges e0 = (0,2) e1 = (0,1) e2 = (1,2) e3 = (3,5) e4 = (3,4) e5 = (4,5); loops l3 (v3 on e4) l4 (v4 on e5) l5 (v5 on e3)
        Assert.Equal(new[] { 3, 4 }, L(bm, bm.EdgesOfVert, 3));
        Assert.True(bm.VertSplice(0, 3));
        Assert.False(bm.VertSplice(0, 0));
        Assert.False(bm.VAlive[3]);
        Assert.Equal(new[] { 0, 1, 3, 4 }, L(bm, bm.EdgesOfVert, 0));   // 3's edges in 3's disk order, appended after 0's
        Assert.Equal(0, bm.EV1[3]); Assert.Equal(0, bm.EV1[4]);
        Assert.Equal(0, bm.LV[3]);                                       // face 1's corner at the old vertex 3 (found as l5's next on e3)
        Assert.Equal(5, bm.TotVert); Assert.Equal(6, bm.TotEdge); Assert.Equal(2, bm.TotFace);
        // loops at 0: e0's head l2 is at 2, so its next l0 comes first; then around the disk e1 (l0 again, skipped as the
        // first), e3 (l5 at 5, no), e4 (l3 at 0)
        Assert.Equal(new[] { 0, 3 }, L(bm, bm.LoopsOfVert, 0));
    }

    [Fact]
    public void An_edge_splice_moves_the_loops_onto_the_kept_edge_and_kills_the_other()
    {
        var bm = new BMesh();
        for (int i = 0; i < 4; i++) bm.VertCreate(0, 0, 0);
        int ea = bm.EdgeCreate(0, 1), eb = bm.EdgeCreate(1, 0), e02 = bm.EdgeCreate(0, 2), e12 = bm.EdgeCreate(1, 2), e03 = bm.EdgeCreate(0, 3), e13 = bm.EdgeCreate(1, 3);
        int fa = bm.FaceCreate(new[] { 0, 1, 2 }, new[] { ea, e12, e02 }, 3);   // loops 0 1 2
        int fb = bm.FaceCreate(new[] { 1, 0, 3 }, new[] { eb, e03, e13 }, 3);   // loops 3 4 5; l3 on eb
        Assert.False(bm.EdgeSplice(ea, e02));                            // not the same vertices
        Assert.True(bm.EdgeSplice(ea, eb));
        Assert.False(bm.EAlive[eb]); Assert.True(bm.EdgeIsManifold(ea));
        Assert.Equal(new[] { 3, 0 }, L(bm, bm.LoopsOfEdge, ea));         // fb's loop, appended, becomes the head
        Assert.Equal(ea, bm.LE[3]);
        Assert.Equal(new[] { ea, e02, e03 }, L(bm, bm.EdgesOfVert, 0));
        Assert.Equal(new[] { ea, e12, e13 }, L(bm, bm.EdgesOfVert, 1));
        Assert.True(bm.FAlive[fa] && bm.FAlive[fb]);
        Assert.Equal(5, bm.TotEdge);
    }

    [Fact]
    public void Kills_cascade_and_nothing_may_be_created_afterwards()
    {
        var bm = Fan();
        bm.VertKill(2);                                                  // both faces; edges (0,2) (1,2) (2,3)
        Assert.Equal(0, bm.TotFace); Assert.Equal(2, bm.TotEdge); Assert.Equal(3, bm.TotVert); Assert.Equal(0, bm.TotLoop);
        Assert.Equal(new[] { 1, 3 }, L(bm, bm.EdgesOfVert, 0));
        Assert.True(bm.EdgeIsWire(1));
        Assert.Equal(BMesh.None, bm.EL[1]);
        Assert.Throws<InvalidOperationException>(() => bm.VertCreate(0, 0, 0));
    }

    [Fact]
    public void The_fan_step_crosses_a_manifold_edge_onto_the_neighbouring_loop_at_the_same_vertex()
    {
        var bm = Fan();
        int eStep = bm.LE[0];                                            // e1 = (0,1), the edge "behind" loop 0
        int l = bm.VertStepFanLoop(0, ref eStep);
        Assert.Equal(3, l); Assert.Equal(0, eStep);                      // crossed e0 = (0,2) onto face 1's loop at vertex 0
        Assert.Equal(BMesh.None, bm.VertStepFanLoop(3, ref eStep));      // the next edge around, (0,3), is a boundary
        Assert.Equal(3, bm.EdgeOtherLoop(0, 0));
        Assert.True(bm.EdgeShareVertCheck(0, 1)); Assert.False(bm.EdgeShareVertCheck(1, 4));
        Assert.Equal(2, bm.EdgeOtherVert(0, 0)); Assert.Equal(BMesh.None, bm.EdgeOtherVert(0, 1));
        Assert.True(bm.VertInEdge(0, 2)); Assert.False(bm.VertInEdge(0, 1));
        Assert.True(bm.VertPairShareFaceCheck(1, 2)); Assert.False(bm.VertPairShareFaceCheck(1, 3));
        Assert.Equal(0, bm.EdgeExists(2, 0)); Assert.Equal(BMesh.None, bm.EdgeExists(1, 3));
    }
}
