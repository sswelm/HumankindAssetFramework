// VehicleProbe.Islands.cs - a single mesh's loose parts in the order Blender's separate(type='LOOSE') gives them, with
// the VERTEX ORDER each separated part comes out in (step 3b of replacing Blender, 2026-10-02).
//
// The order matters because the visibility verdict samples every n-th vertex. The island that stays in the original
// object keeps the imported order (the used vertices of each primitive, unique and ascending, primitive after
// primitive). Every other island is rebuilt from the arrays BM_mesh_calc_edge_groups_as_arrays collects: a walk that
// starts at the island's lowest vertex, runs round that vertex's edges in the order they were CREATED, discovers the
// vertex at the other end of each, pushes it on a stack, and continues from the vertex pushed last (bmesh_query.cc).
// The edges were created by mesh_calc_edges (mesh_calc_edges.cc) from the faces in order, each face's edges as
// (previous corner, corner) starting from the last corner - into ONE insertion-ordered set when the mesh has fewer
// than 1,000 faces, else into 8 sets chosen by the lower vertex index & 7, concatenated. (8 = min(8, threads); a
// machine with fewer than 8 threads would order them differently - said, not handled; every machine this runs on has 16.) Loose edges the file gives
// (line primitives) exist before the faces' are computed and stay FIRST, in their order, whatever bucket they would fall in. mesh.validate() then drops degenerate faces and edges, which does
// not reorder what remains.
using System;
using System.Collections.Generic;
using System.Linq;

public static partial class VehicleProbe
{
    /// <summary>One loose part: its primitive, its vertices (primitive-local indices) in the order Blender holds them.</summary>
    internal sealed class Island { public int Prim; public int[] Verts; }

    internal static List<Island> BlenderIslands(HafMesh mesh)
    {
        // ---- the merged mesh: global vertex ids, triangles and loose edges in the importer's order
        int n = 0; var offsets = new int[mesh.Primitives.Count]; var ranks = new int[mesh.Primitives.Count][];
        for (int pi = 0; pi < mesh.Primitives.Count; pi++)
        {
            var p = mesh.Primitives[pi]; var used = Used(p);
            offsets[pi] = n; ranks[pi] = new int[p.VertexCount]; for (int i = 0; i < ranks[pi].Length; i++) ranks[pi][i] = -1;
            for (int k = 0; k < used.Length; k++) ranks[pi][used[k]] = n + k;
            n += used.Length;
        }
        var faces = new List<int>(); var loose = new List<int>();
        for (int pi = 0; pi < mesh.Primitives.Count; pi++)
        {
            var p = mesh.Primitives[pi]; var rank = ranks[pi];
            int count = p.Indices != null ? p.Indices.Length : p.VertexCount;
            int At(int i) { int v = p.Indices != null ? p.Indices[i] : i; return v >= 0 && v < rank.Length ? rank[v] : -1; }
            void Tri(int a, int b, int c) { if (a < 0 || b < 0 || c < 0) return; faces.Add(a); faces.Add(b); faces.Add(c); }
            void Edge(int a, int b) { if (a < 0 || b < 0) return; loose.Add(a); loose.Add(b); }
            switch (p.Mode)
            {
                case 4: for (int t = 0; t + 2 < count; t += 3) Tri(At(t), At(t + 1), At(t + 2)); break;
                case 5: for (int t = 0; t + 2 < count; t++) { if ((t & 1) == 0) Tri(At(t), At(t + 1), At(t + 2)); else Tri(At(t), At(t + 2), At(t + 1)); } break;
                case 6: for (int t = 1; t + 1 < count; t++) Tri(At(0), At(t), At(t + 1)); break;
                case 1: for (int t = 0; t + 1 < count; t += 2) Edge(At(t), At(t + 1)); break;
                case 2: for (int t = 0; t < count; t++) Edge(At(t), At((t + 1) % count)); break;
                case 3: for (int t = 0; t + 1 < count; t++) Edge(At(t), At(t + 1)); break;
            }
        }
        // ---- mesh_calc_edges: the edge array, then each vertex's edges in creation order (its BMesh disk cycle)
        int faceCount = faces.Count / 3;
        int maps = faceCount < 1000 ? 1 : 8; int mask = maps - 1;
        var seen = new HashSet<long>(); var perMap = new List<(int a, int b)>[maps + 1];   // [0]: the file's loose edges, then the buckets
        for (int i = 0; i <= maps; i++) perMap[i] = new List<(int, int)>();
        void Add(int a, int b, int map)
        {
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            if (lo == hi) return;   // validate drops it
            long key = (long)lo << 32 | (uint)hi;
            if (seen.Add(key)) perMap[map].Add((lo, hi));
        }
        for (int i = 0; i + 1 < loose.Count; i += 2) Add(loose[i], loose[i + 1], 0);
        for (int f = 0; f < faceCount; f++) { int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2]; Add(c, a, 1 + (Math.Min(c, a) & mask)); Add(a, b, 1 + (Math.Min(a, b) & mask)); Add(b, c, 1 + (Math.Min(b, c) & mask)); }
        var disk = new List<int>[n]; var edges = new List<(int a, int b)>();
        foreach (var map in perMap)
            foreach (var e in map)
            {
                int id = edges.Count; edges.Add(e);
                (disk[e.a] ?? (disk[e.a] = new List<int>())).Add(id);
                (disk[e.b] ?? (disk[e.b] = new List<int>())).Add(id);
            }
        // ---- BM_mesh_calc_edge_groups_as_arrays
        var tagged = new bool[n]; var edgeTagged = new bool[edges.Count];
        var groups = new List<List<int>>();
        var stack = new Stack<int>();
        for (int seed = 0; seed < n; seed++)
        {
            if (tagged[seed]) continue;
            var verts = new List<int>(); tagged[seed] = true; verts.Add(seed);
            if (disk[seed] != null)
            {
                int v = seed;
                while (true)
                {
                    if (disk[v] != null)
                        foreach (int e in disk[v])
                        {
                            if (edgeTagged[e]) continue;
                            edgeTagged[e] = true;
                            int other = edges[e].a == v ? edges[e].b : edges[e].a;
                            if (!tagged[other]) { tagged[other] = true; verts.Add(other); stack.Push(other); }
                        }
                    if (stack.Count == 0) break;
                    v = stack.Pop();
                }
            }
            groups.Add(verts);
        }
        // ---- the first group keeps the imported order; the others come out in the walk's. Back to primitive-local indices.
        var result = new List<Island>();
        for (int g = 0; g < groups.Count; g++)
        {
            var verts = groups[g];
            if (g == 0) verts.Sort();
            int pi = Array.FindLastIndex(offsets, o => o <= verts[0]);
            var used = Used(mesh.Primitives[pi]);
            result.Add(new Island { Prim = pi, Verts = verts.Select(gv => used[gv - offsets[pi]]).ToArray() });
        }
        return result;
    }
}
