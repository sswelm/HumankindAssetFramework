using System;
using System.Linq;
using Xunit;

/// <summary>Blender's mesh layout of a glTF mesh (BlenderMesh): the importer's vertex order, points_edges_tris, the
/// kernel's edge order (mesh_calc_edges: buckets by the lower vertex, insertion-ordered VectorSets, the loose edges first in
/// their own orientation) and validate's face and edge removal. The drill holds every registry mesh to Blender's own list
/// (tools/decimate_drill.sh: 137 files, 6,912 objects, 21.1 million edges on 2026-10-03); these hold each rule on a
/// hand-made case where the rule alone decides the order.</summary>
public class BlenderMeshTests
{
    static HafPrimitive Prim(int mode, int vertexCount, int[] indices = null) => new HafPrimitive { Mode = mode, VertexCount = vertexCount, Positions = new float[vertexCount * 3], Indices = indices };

    [Fact]
    public void Vertices_are_each_primitives_used_indices_ascending_then_the_next_primitives()
    {
        var m = new HafModel(); var mesh = new HafMesh();
        mesh.Primitives.Add(Prim(4, 8, new[] { 7, 2, 5 }));         // uses 2, 5, 7 -> ranks 0, 1, 2
        mesh.Primitives.Add(Prim(4, 8, new[] { 1, 0, 3, 3, 0, 6 }));   // uses 0, 1, 3, 6 -> ranks 3, 4, 5, 6
        m.Meshes.Add(mesh);
        var bm = BlenderMesh.FromGltf(m, 0);
        Assert.Equal(7, bm.VertexCount);
        Assert.Equal(new[] { 2, 5, 7, 0, 1, 3, 6 }, bm.RankIndex);
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 1, 1 }, bm.RankPrimitive);
        Assert.Equal(new[] { 2, 0, 1, 4, 3, 5, 5, 3, 6 }, bm.Corners);   // 7->2, 2->0, 5->1; 1->4, 0->3, 3->5, 6->6
    }

    [Fact]
    public void Face_edges_come_per_face_per_corner_as_previous_then_current_low_first()
    {
        // one triangle (0, 1, 2): corner 0's edge is (2, 0), then (0, 1), then (1, 2)
        Assert.Equal(new[] { 0, 2, 0, 1, 1, 2 }, BlenderMesh.CalcEdges(new int[0], new[] { 0, 1, 2 }, 1));
        // a second triangle (2, 1, 3) sharing an edge adds only its new edges, in its own corner order: (3,2)->(2,3), (2,1) known, (1,3)
        Assert.Equal(new[] { 0, 2, 0, 1, 1, 2, 2, 3, 1, 3 }, BlenderMesh.CalcEdges(new int[0], new[] { 0, 1, 2, 2, 1, 3 }, 1));
    }

    [Fact]
    public void Loose_edges_come_first_in_their_own_orientation_and_a_face_edge_equal_to_one_is_not_repeated()
    {
        // the importer's line (3, 0) stays (3, 0); the face edge (0, 3) is the same edge and is not added again
        Assert.Equal(new[] { 3, 0, 5, 6, 0, 1, 1, 3 }, BlenderMesh.CalcEdges(new[] { 3, 0, 5, 6, 3, 0 }, new[] { 0, 1, 3 }, 1));
    }

    [Fact]
    public void Eight_buckets_group_the_edges_by_their_lower_vertex_each_in_insertion_order()
    {
        // triangles (0,1,2) (8,9,10) (1,9,17): bucket = low & 7
        var edges = BlenderMesh.CalcEdges(new int[0], new[] { 0, 1, 2, 8, 9, 10, 1, 9, 17 }, 8);
        // insertion: (2,0)->(0,2) b0; (0,1) b0; (1,2) b1; (10,8)->(8,10) b0; (8,9) b0; (9,10) b1; (17,1)->(1,17) b1; (1,9) b1; (9,17) b1
        Assert.Equal(new[] { 0, 2, 0, 1, 8, 10, 8, 9, 1, 2, 9, 10, 1, 17, 1, 9, 9, 17 }, edges);
        Assert.Equal(1, BlenderMesh.ParallelMaps(999, 16)); Assert.Equal(8, BlenderMesh.ParallelMaps(1000, 16)); Assert.Equal(4, BlenderMesh.ParallelMaps(1000, 6));
    }

    [Fact]
    public void Validate_drops_repeated_vertex_faces_then_duplicate_faces_either_winding_and_equal_ended_edges()
    {
        var m = new HafModel(); var mesh = new HafMesh();
        mesh.Primitives.Add(Prim(4, 4, new[] { 0, 1, 2, 0, 0, 3, 2, 1, 0, 1, 2, 3 }));   // (0,0,3) repeated vertex; (2,1,0) duplicates (0,1,2) wound the other way
        m.Meshes.Add(mesh);
        var bm = BlenderMesh.FromGltf(m, 0);
        Assert.Equal(new[] { 0, 1, 2, 1, 2, 3 }, bm.Faces);
        // before validate the repeated-vertex face put a (0, 0) edge in the list; validate drops it and keeps the rest in order:
        // (0,2) (0,1) (1,2) from the first face, (0,3) [(0,0)] from the second, nothing new from the duplicate, (1,3) (2,3) from the last
        Assert.Equal(new[] { 0, 2, 0, 1, 1, 2, 0, 3, 0, 0, 1, 3, 2, 3 }, bm.Edges);
        Assert.Equal(new[] { 0, 2, 0, 1, 1, 2, 0, 3, 1, 3, 2, 3 }, bm.ValidEdges);
    }

    [Fact]
    public void Strips_fans_loops_and_lines_unroll_as_the_importer_does()
    {
        BlenderMesh.PointsEdgesTris(5, new[] { 0, 1, 2, 3, 4 }, out _, out _, out var strip);
        Assert.Equal(new[] { 0, 1, 2, 1, 3, 2, 2, 3, 4 }, strip);
        BlenderMesh.PointsEdgesTris(6, new[] { 0, 1, 2, 3 }, out _, out _, out var fan);
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, fan);
        BlenderMesh.PointsEdgesTris(2, new[] { 0, 1, 2, 3 }, out _, out var loop, out _);
        Assert.Equal(new[] { 0, 1, 1, 2, 2, 3, 3, 0 }, loop);
        BlenderMesh.PointsEdgesTris(3, new[] { 0, 1, 2, 3 }, out _, out var lines, out _);
        Assert.Equal(new[] { 0, 1, 1, 2, 2, 3 }, lines);
    }
}
