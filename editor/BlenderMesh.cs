// BlenderMesh.cs - a glTF mesh as Blender's importer and mesh kernel lay it out (step 5 of replacing Blender, 2026-10-03:
// the Decimate modifier's input, whose ORDER decides which of two equal-cost edges collapses first). Read from the importer
// (io_scene_gltf2/blender/imp/mesh.py) and the kernel (blenkernel/intern/mesh_calc_edges.cc, mesh_validate.cc) at Blender 5.1.2:
//   * VERTICES: per primitive with a POSITION, the indices its mode uses (points, lines, line loop/strip unrolled, triangles,
//     strips and fans unrolled) made unique and ASCENDING (np.unique), appended after the previous primitives' vertices;
//   * LOOSE EDGES: the line primitives' pairs, in primitive order, as `mesh.edges` before any face edge exists;
//   * FACE CORNERS: the triangles in primitive order, every corner's vertex;
//   * EDGES (`mesh.update(calc_edges=True)` -> mesh_calc_edges with keep_existing_edges): every edge belongs to one of N hash
//     buckets by its LOWER vertex index (N = 1 under 1,000 faces, else the machine's threads as a power of two capped at 8 -
//     8 on the development machine); each bucket is a VectorSet, so it keeps INSERTION order and ignores a repeat: the loose
//     edges go in first (in their order), then for every face, every corner in order, the edge (previous corner, this corner)
//     as (low, high). The result lists the distinct loose edges in their original order, then bucket 0's new edges, bucket
//     1's, ... each in insertion order. A degenerate corner pair (v, v) is an edge too, until validation drops it;
//   * VALIDATE (`mesh.validate()`): faces with a repeated vertex, then faces equal to an earlier face as a vertex set (either
//     winding) are removed in order; edges with both ends equal are removed; the rest keep their order and their numbers.
// Proof: tools/decimate-drill (every registry mesh's edge list, Blender's own against this, exact).
using System;
using System.Collections.Generic;

public sealed class BlenderMesh
{
    public int VertexCount;
    public int[] VertexOfRank;            // per Blender vertex: (primitive index << 32 | file index) packed below as two arrays
    public int[] RankPrimitive, RankIndex;
    public int[] LooseEdges = new int[0]; // pairs, as the importer adds them (file order, the importer's vertex numbers)
    public int[] Corners = new int[0];    // 3 per face, pre-validate
    public int[] CornerPrimitive = new int[0];   // per face: its primitive
    public int[] Edges = new int[0];      // pairs (low, high), Blender's order, pre-validate
    public int[] Faces = new int[0];      // 3 per face, post-validate (the kept faces, in order)
    public int[] FacePrimitive = new int[0];
    public int[] ValidEdges = new int[0]; // pairs, post-validate (edges with equal ends dropped)

    /// <summary>The buckets mesh_calc_edges uses on this machine: one under 1,000 faces, else the thread count as a power of
    /// two, at most 8. Zero threads uses the current runtime's processor count. The development machine has 16 threads;
    /// a machine with fewer changes the order of a large mesh's edges.</summary>
    public static int ParallelMaps(int faces, int threads = 0)
    {
        if (threads == 0) threads = Environment.ProcessorCount;
        return faces < 1000 ? 1 : Math.Min(8, PowerOfTwoMin(Math.Min(8, threads)));
    }

    static int PowerOfTwoMin(int n) { int p = 1; while (p * 2 <= n) p *= 2; return p; }   // power_of_2_min_i

    public static BlenderMesh FromGltf(HafModel m, int meshIndex, int threads = 0)
    {
        var mesh = m.Meshes[meshIndex];
        var r = new BlenderMesh();
        var rankPrim = new List<int>(); var rankIndex = new List<int>();
        var loose = new List<int>(); var corners = new List<int>(); var cornerPrim = new List<int>();
        for (int pi = 0; pi < mesh.Primitives.Count; pi++)
        {
            var p = mesh.Primitives[pi];
            if (p.Positions == null) continue;   // 'POSITION' not in prim.attributes
            int[] indices = p.Indices ?? Range(p.VertexCount);
            PointsEdgesTris(p.Mode, indices, out int[] points, out int[] edges, out int[] tris);
            int[] used = points ?? edges ?? tris;
            if (used == null) continue;
            // np.unique(indices, return_inverse=True): the unique values ascending, each index's rank among them
            int maxIndex = -1; foreach (int v in used) if (v > maxIndex) maxIndex = v;
            var mark = new bool[maxIndex + 1]; foreach (int v in used) if (v >= 0) mark[v] = true;
            var rank = new int[maxIndex + 1];
            for (int v = 0; v <= maxIndex; v++) if (mark[v]) { rank[v] = rankPrim.Count; rankPrim.Add(pi); rankIndex.Add(v); }
            if (edges != null) foreach (int v in edges) loose.Add(rank[v]);
            if (tris != null)
            {
                for (int t = 0; t + 2 < tris.Length; t += 3) { corners.Add(rank[tris[t]]); corners.Add(rank[tris[t + 1]]); corners.Add(rank[tris[t + 2]]); cornerPrim.Add(pi); }
            }
        }
        r.VertexCount = rankPrim.Count; r.RankPrimitive = rankPrim.ToArray(); r.RankIndex = rankIndex.ToArray();
        r.LooseEdges = loose.ToArray(); r.Corners = corners.ToArray(); r.CornerPrimitive = cornerPrim.ToArray();
        r.Edges = CalcEdges(r.LooseEdges, r.Corners, ParallelMaps(r.Corners.Length / 3, threads));
        r.Validate();
        return r;
    }

    static int[] Range(int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = i; return a; }

    /// <summary>points_edges_tris: what the importer makes of a primitive's indices by its mode.</summary>
    public static void PointsEdgesTris(int mode, int[] indices, out int[] points, out int[] edges, out int[] tris)
    {
        points = edges = tris = null;
        int n = indices.Length;
        switch (mode)
        {
            case 0: points = indices; break;
            case 1: edges = indices; break;
            case 2:   // line loop: 0 1 1 2 2 3 3 0
                if (n == 0) { edges = new int[0]; break; }
                edges = new int[2 * n]; edges[0] = indices[0]; edges[2 * n - 1] = indices[0];
                for (int i = 1; i < n; i++) { edges[2 * i - 1] = indices[i]; edges[2 * i] = indices[i]; }
                break;
            case 3:   // line strip: 0 1 1 2 2 3
                if (n < 2) { edges = new int[0]; break; }
                edges = new int[2 * n - 2]; edges[0] = indices[0]; edges[2 * n - 3] = indices[n - 1];
                for (int i = 1; i < n - 1; i++) { edges[2 * i - 1] = indices[i]; edges[2 * i] = indices[i]; }
                break;
            case 4: tris = indices; break;
            case 5:   // triangle strip: (0 1 2), (2 1 3), (2 3 4), ... - the odd ones flipped so the winding holds
                {
                    var t = new List<int>();
                    for (int i = 0; i + 2 < n; i++) { if ((i & 1) == 0) { t.Add(indices[i]); t.Add(indices[i + 1]); t.Add(indices[i + 2]); } else { t.Add(indices[i]); t.Add(indices[i + 2]); t.Add(indices[i + 1]); } }
                    tris = t.ToArray(); break;
                }
            case 6:   // triangle fan: (0 1 2), (0 2 3), ...
                {
                    var t = new List<int>();
                    for (int i = 1; i + 1 < n; i++) { t.Add(indices[0]); t.Add(indices[i]); t.Add(indices[i + 1]); }
                    tris = t.ToArray(); break;
                }
        }
    }

    /// <summary>mesh_calc_edges with keep_existing_edges: the buckets, the VectorSets, the serialization (see the header).</summary>
    public static int[] CalcEdges(int[] looseEdges, int[] corners, int parallelMaps)
    {
        uint mask = (uint)parallelMaps - 1;
        var sets = new List<long>[parallelMaps]; var seen = new HashSet<long>[parallelMaps];
        for (int i = 0; i < parallelMaps; i++) { sets[i] = new List<long>(); seen[i] = new HashSet<long>(EdgeKeyComparer.Instance); }
        long Key(int a, int b) { int lo = Math.Min(a, b), hi = Math.Max(a, b); return ((long)lo << 32) | (uint)hi; }
        int MapOf(long key) => (int)(mask & (uint)(key >> 32));   // edge_hash_2 = v_low
        void Add(int a, int b) { long k = Key(a, b); int mi = MapOf(k); if (seen[mi].Add(k)) sets[mi].Add(k); }
        // the existing (loose) edges first - each bucket walks every existing edge in order and takes its own
        var prefix = new int[parallelMaps];
        for (int i = 0; i + 1 < looseEdges.Length; i += 2) Add(looseEdges[i], looseEdges[i + 1]);
        for (int i = 0; i < parallelMaps; i++) prefix[i] = sets[i].Count;
        // then the face edges: per face, per corner, (previous corner's vertex, this corner's vertex)
        for (int f = 0; f + 2 < corners.Length; f += 3)
            for (int c = 0; c < 3; c++) Add(corners[f + (c + 2) % 3], corners[f + c]);
        // serialize: the distinct existing edges in their ORIGINAL order (mask_first_distinct_edges + gather), then each bucket's
        // new edges (its entries past the prefix) bucket by bucket
        var result = new List<int>();
        var emitted = new HashSet<long>(EdgeKeyComparer.Instance);
        // an existing edge keeps the ORIENTATION the importer gave it (gather of the original int2) - only the new ones are (low, high)
        for (int i = 0; i + 1 < looseEdges.Length; i += 2) { long k = Key(looseEdges[i], looseEdges[i + 1]); if (emitted.Add(k)) { result.Add(looseEdges[i]); result.Add(looseEdges[i + 1]); } }
        for (int mi = 0; mi < parallelMaps; mi++)
            for (int j = prefix[mi]; j < sets[mi].Count; j++) { long k = sets[mi][j]; result.Add((int)(k >> 32)); result.Add((int)(k & 0xffffffff)); }
        return result.ToArray();
    }

    /// <summary>The hash of a packed (low, high) edge key. The default hash of a long is its halves XORed, which sends every
    /// edge between neighbouring vertex numbers - most of a triangle soup or a grid - to a handful of buckets: the BMesh build
    /// of the Wespe took 64 of 78 seconds in one dictionary until the halves were mixed (measured 2026-10-05).</summary>
    public sealed class EdgeKeyComparer : IEqualityComparer<long>
    {
        public static readonly EdgeKeyComparer Instance = new EdgeKeyComparer();
        public bool Equals(long a, long b) => a == b;
        public int GetHashCode(long k) => unchecked((int)(((ulong)k * 0x9E3779B97F4A7C15UL) >> 32));
    }

    /// <summary>mesh.validate(): drop faces with a repeated vertex, then faces equal to an earlier kept face as a vertex set,
    /// then edges whose ends are equal; everything else keeps its order.</summary>
    void Validate()
    {
        var faces = new List<int>(); var facePrim = new List<int>();
        var seenFace = new HashSet<(int, int, int)>();
        for (int f = 0; f + 2 < Corners.Length; f += 3)
        {
            int a = Corners[f], b = Corners[f + 1], c = Corners[f + 2];
            if (a == b || b == c || a == c) continue;
            int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c));
            if (!seenFace.Add((lo, a + b + c - lo - hi, hi))) continue;
            faces.Add(a); faces.Add(b); faces.Add(c); facePrim.Add(CornerPrimitive[f / 3]);
        }
        Faces = faces.ToArray(); FacePrimitive = facePrim.ToArray();
        var edges = new List<int>();
        for (int e = 0; e + 1 < Edges.Length; e += 2) if (Edges[e] != Edges[e + 1]) { edges.Add(Edges[e]); edges.Add(Edges[e + 1]); }
        ValidEdges = edges.ToArray();
    }
}
