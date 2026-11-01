// VehicleProbe.PolygonNormals.cs - the corner-normal kernel of VehicleProbe.CustomNormals.cs for a mesh of POLYGONS (step 6
// of the native FBX reader, 2026-11-01): Blender's FBX importer keeps a file's quads and n-gons (blen_read_geom), sets the
// file's normals as custom normals PER CORNER (mesh.normals_split_custom_set -> mesh_set_custom_normals ->
// mesh_normals_corner_custom_set with use_vertices = false), and the glTF exporter reads Mesh.corner_normals of that
// polygon mesh before it tessellates. Read at Blender 5.1.2, mesh_normals.cc:
//   * normals_calc_corners over polygons: a corner's previous and next corners are its neighbours IN ITS FACE (any size), its
//     two local edges the vertex pairs (vertPrev, v) and (v, vertNext); the rest - the faces around a vertex ascending, the
//     local edge set in encounter order, the edge kinds, the fan traversal, the fan normal weighted by the corner angle, the
//     fan space - is the triangle walk's, repeated here over polygons;
//   * mesh_normals_corner_custom_set (per corner): the spaces computed once; a ZERO custom normal becomes the corner's auto
//     normal; then for every fan, walking its corners in REVERSE collection order against a running reference, a corner whose
//     normal's dot with the reference is below 1 - 1e-4 TAGS AN EDGE SHARP - the previous fan corner's next edge when it is this
//     corner's previous edge, else this corner's next edge - and becomes the new reference; the fan's last corner is checked
//     against the reference once more; the spaces are computed again with the new sharp edges; then each fan's custom normals
//     are averaged (float32, divided by the count) and encoded once as two shorts for all its corners; a one-corner fan encodes
//     its own normal;
//   * Mesh::corner_normals with custom data: every fan's shorts averaged as int2 (integer division) and decoded against its space.
// Face normals are Newell's sum over the polygon (normal_calc_ngon), normalized by the reciprocal length, (0, 0, 1) when zero.
// Proof: tools/fbx_model_drill.sh (every corner normal of every primitive Blender writes for a polygon mesh, bit for bit).
using System;
using System.Collections.Generic;

public static partial class VehicleProbe
{
    /// <summary>normal_calc_ngon over every polygon: corners in face order, Newell's sum starting from the last corner.</summary>
    internal static float[] PolygonFaceNormals(float[] P, int[] cornerVerts, int[] faceOffsets)
    {
        int nf = faceOffsets.Length - 1;
        var FN = new float[nf * 3];
        for (int f = 0; f < nf; f++)
        {
            int begin = faceOffsets[f], end = faceOffsets[f + 1];
            float nx = 0f, ny = 0f, nz = 0f;
            int prev = cornerVerts[end - 1] * 3;
            for (int c = begin; c < end; c++)
            {
                int curr = cornerVerts[c] * 3;
                nx = (float)(nx + (float)((float)(P[prev + 1] - P[curr + 1]) * (float)(P[prev + 2] + P[curr + 2])));
                ny = (float)(ny + (float)((float)(P[prev + 2] - P[curr + 2]) * (float)(P[prev] + P[curr])));
                nz = (float)(nz + (float)((float)(P[prev] - P[curr]) * (float)(P[prev + 1] + P[curr + 1])));
                prev = curr;
            }
            NormalizeV3(ref nx, ref ny, ref nz);
            if (nx == 0f && ny == 0f && nz == 0f) nz = 1.0f;
            FN[f * 3] = nx; FN[f * 3 + 1] = ny; FN[f * 3 + 2] = nz;
        }
        return FN;
    }

    /// <summary>normals_calc_corners over polygons (serial): for every vertex its corners, their fans and each fan's space,
    /// handed to onFan with the corners of the fan in the order the walk collected them (the order corners_by_space keeps).
    /// cornerVerts: every face's corners in order; faceOffsets: nf + 1 starts; sharpEdges keyed by SharpEdgeKey.</summary>
    static void WalkFansPolygon(float[] P, int[] cornerVerts, int[] faceOffsets, bool[] sharpFace, HashSet<long> sharpEdges, float[] FN, Action<int, List<VertCornerInfo>, List<int>, FanSpace> onFan)
    {
        int nv = P.Length / 3, nf = faceOffsets.Length - 1, nc = cornerVerts.Length;
        var faceOfCorner = new int[nc];
        for (int f = 0; f < nf; f++) for (int c = faceOffsets[f]; c < faceOffsets[f + 1]; c++) faceOfCorner[c] = f;
        // the faces around each vertex, ascending (build_vert_to_face_map sorts each group)
        var offsets = new int[nv + 1];
        foreach (int v in cornerVerts) offsets[v + 1]++;
        for (int v = 0; v < nv; v++) offsets[v + 1] += offsets[v];
        var fill = (int[])offsets.Clone(); var vertCorners = new int[nc];
        for (int c = 0; c < nc; c++) vertCorners[fill[cornerVerts[c]]++] = c;   // corners in face order = faces ascending (a vertex once per face)
        var infos = new List<VertCornerInfo>(); var localEdgeVerts = new List<int>(); var fan = new List<int>(); var rotated = new List<int>();
        var fanEdgeDirs = new List<float[]>();
        int[] kind = new int[8], c1 = new int[8], c2 = new int[8]; bool[] towards = new bool[8], visited = new bool[8]; float[][] edgeDirs = new float[8][];
        for (int v = 0; v < nv; v++)
        {
            if (offsets[v] == offsets[v + 1]) continue;
            infos.Clear();
            for (int at = offsets[v]; at < offsets[v + 1]; at++)
            {
                int c = vertCorners[at], f = faceOfCorner[c], begin = faceOffsets[f], end = faceOffsets[f + 1];
                var ci = new VertCornerInfo { face = f, corner = c, cornerPrev = c == begin ? end - 1 : c - 1, cornerNext = c + 1 == end ? begin : c + 1 };
                ci.vertPrev = cornerVerts[ci.cornerPrev]; ci.vertNext = cornerVerts[ci.cornerNext];
                infos.Add(ci);
            }
            localEdgeVerts.Clear();
            foreach (var ci in infos)
            {
                ci.edgePrev = localEdgeVerts.IndexOf(ci.vertPrev); if (ci.edgePrev < 0) { localEdgeVerts.Add(ci.vertPrev); ci.edgePrev = localEdgeVerts.Count - 1; }
                ci.edgeNext = localEdgeVerts.IndexOf(ci.vertNext); if (ci.edgeNext < 0) { localEdgeVerts.Add(ci.vertNext); ci.edgeNext = localEdgeVerts.Count - 1; }
            }
            int ne = localEdgeVerts.Count;
            if (ne > kind.Length)
            {
                int capacity = Math.Max(ne, kind.Length * 2);
                Array.Resize(ref kind, capacity); Array.Resize(ref c1, capacity); Array.Resize(ref c2, capacity); Array.Resize(ref towards, capacity); Array.Resize(ref edgeDirs, capacity);
            }
            Array.Clear(kind, 0, ne);
            void AddCornerToEdge(int e, int lc, bool windingTowardsVert)
            {
                if (kind[e] == 0) { kind[e] = 1; c1[e] = lc; towards[e] = windingTowardsVert; }
                else if (kind[e] == 1) { if (towards[e] == windingTowardsVert) kind[e] = 3; else { kind[e] = 2; c2[e] = lc; } }
                else kind[e] = 3;
            }
            for (int lc = 0; lc < infos.Count; lc++)
            {
                var ci = infos[lc];
                if (sharpFace[ci.face]) { kind[ci.edgePrev] = 3; kind[ci.edgeNext] = 3; continue; }
                AddCornerToEdge(ci.edgePrev, lc, true);
                AddCornerToEdge(ci.edgeNext, lc, false);
            }
            if (sharpEdges != null) for (int e = 0; e < ne; e++) if (sharpEdges.Contains(SharpEdgeKey(v, localEdgeVerts[e]))) kind[e] = 3;
            for (int e = 0; e < ne; e++)
            {
                int o = localEdgeVerts[e];
                float dx = (float)(P[o * 3] - P[v * 3]), dy = (float)(P[o * 3 + 1] - P[v * 3 + 1]), dz = (float)(P[o * 3 + 2] - P[v * 3 + 2]);
                Normalize3(ref dx, ref dy, ref dz);
                var dir = edgeDirs[e] ?? (edgeDirs[e] = new float[3]);
                dir[0] = dx; dir[1] = dy; dir[2] = dz;
            }
            if (infos.Count > visited.Length) Array.Resize(ref visited, Math.Max(infos.Count, visited.Length * 2));
            Array.Clear(visited, 0, infos.Count);
            int visitedCount = 0, start = 0;
            while (true)
            {
                fan.Clear(); fan.Add(start);
                bool cyclic = false;
                int current = start, le = infos[current].edgeNext;
                while (kind[le] == 2)
                {
                    current = c1[le] == current ? c2[le] : c1[le];
                    if (current == start) { cyclic = true; break; }
                    fan.Add(current); le = infos[current].edgeNext;
                }
                fan.Reverse();
                if (cyclic)
                {
                    int best = 0;
                    for (int i = 1; i < fan.Count; i++) if (infos[fan[i]].corner < infos[fan[best]].corner) best = i;
                    rotated.Clear(); for (int i = 0; i < fan.Count; i++) rotated.Add(fan[(best + i) % fan.Count]);
                    fan.Clear(); fan.AddRange(rotated);
                }
                else
                {
                    current = start; le = infos[current].edgePrev;
                    while (kind[le] == 2)
                    {
                        current = current == c1[le] ? c2[le] : c1[le];
                        fan.Add(current); le = infos[current].edgePrev;
                    }
                }
                float fx, fy, fz;
                if (fan.Count == 1) { int f0 = infos[fan[0]].face; fx = FN[f0 * 3]; fy = FN[f0 * 3 + 1]; fz = FN[f0 * 3 + 2]; }
                else
                {
                    fx = fy = fz = 0f;
                    foreach (int lc in fan)
                    {
                        var ci = infos[lc]; var dp = edgeDirs[ci.edgePrev]; var dn = edgeDirs[ci.edgeNext];
                        float factor = SafeAcosApprox(Dot3(dp[0], dp[1], dp[2], dn[0], dn[1], dn[2]));
                        fx = (float)(fx + (float)(FN[ci.face * 3] * factor)); fy = (float)(fy + (float)(FN[ci.face * 3 + 1] * factor)); fz = (float)(fz + (float)(FN[ci.face * 3 + 2] * factor));
                    }
                    Normalize3(ref fx, ref fy, ref fz);
                }
                int edgeFirst = infos[fan[0]].edgeNext, edgeLast = infos[fan[fan.Count - 1]].edgePrev;
                fanEdgeDirs.Clear();
                if (fan.Count > 1)
                {
                    foreach (int lc in fan) fanEdgeDirs.Add(edgeDirs[infos[lc].edgeNext]);
                    if (edgeLast != edgeFirst) fanEdgeDirs.Add(edgeDirs[edgeLast]);
                }
                var space = DefineSpace(fx, fy, fz, edgeDirs[edgeFirst][0], edgeDirs[edgeFirst][1], edgeDirs[edgeFirst][2], edgeDirs[edgeLast][0], edgeDirs[edgeLast][1], edgeDirs[edgeLast][2], fanEdgeDirs);
                space.fnx = fx; space.fny = fy; space.fnz = fz;
                onFan(v, infos, fan, space);
                visitedCount += fan.Count;
                if (visitedCount == infos.Count) break;
                foreach (int lc in fan) visited[lc] = true;
                while (visited[start]) start++;
            }
        }
    }

    /// <summary>mesh_set_custom_normals, per corner: the given corner normals (3 per corner, NOT yet normalized - math::normalize
    /// runs first) become the mesh's custom normals (two shorts per corner), and the sharp edges the fans needed are ADDED to
    /// sharpEdges (never removed). cornerNormals is modified in place.</summary>
    internal static short[] SetCustomNormalsPerCorner(float[] P, int[] cornerVerts, int[] faceOffsets, bool[] sharpFace, HashSet<long> sharpEdges, float[] cornerNormals)
    {
        int nc = cornerVerts.Length;
        for (int c = 0; c < nc; c++) Normalize3(ref cornerNormals[3 * c], ref cornerNormals[3 * c + 1], ref cornerNormals[3 * c + 2]);
        var FN = PolygonFaceNormals(P, cornerVerts, faceOffsets);
        // pass 1: the auto corner normals and the fans (corners_by_space, in collection order)
        var auto = new float[nc * 3];
        var fans = new List<int[]>(); var fanOfCorner = new int[nc]; for (int c = 0; c < nc; c++) fanOfCorner[c] = -1;
        WalkFansPolygon(P, cornerVerts, faceOffsets, sharpFace, sharpEdges, FN, (v, infos, fan, space) =>
        {
            var corners = new int[fan.Count];
            for (int i = 0; i < fan.Count; i++) { int c = infos[fan[i]].corner; corners[i] = c; auto[c * 3] = space.fnx; auto[c * 3 + 1] = space.fny; auto[c * 3 + 2] = space.fnz; }
            // handle_fan_result_and_custom_normals: every fan registers a space, an invalid one (zero reference vector) too - its
            // corners still take part in the sharp tagging, and encode shorts that decode to zero
            foreach (int c in corners) fanOfCorner[c] = fans.Count;
            fans.Add(corners);
        });
        for (int c = 0; c < nc; c++)
            if (cornerNormals[3 * c] == 0f && cornerNormals[3 * c + 1] == 0f && cornerNormals[3 * c + 2] == 0f) { cornerNormals[3 * c] = auto[c * 3]; cornerNormals[3 * c + 1] = auto[c * 3 + 1]; cornerNormals[3 * c + 2] = auto[c * 3 + 2]; }
        // the sharp edges the custom normals need: per fan, in reverse order, against a running reference
        var infoOfCorner = new VertCornerInfo[nc];
        {
            int nf = faceOffsets.Length - 1;
            for (int f = 0; f < nf; f++)
                for (int c = faceOffsets[f]; c < faceOffsets[f + 1]; c++)
                {
                    int begin = faceOffsets[f], end = faceOffsets[f + 1];
                    var ci = new VertCornerInfo { face = f, corner = c, cornerPrev = c == begin ? end - 1 : c - 1, cornerNext = c + 1 == end ? begin : c + 1 };
                    ci.vertPrev = cornerVerts[ci.cornerPrev]; ci.vertNext = cornerVerts[ci.cornerNext];
                    infoOfCorner[c] = ci;
                }
        }
        var tagged = sharpEdges ?? new HashSet<long>(PairKeyComparer.Instance);
        var done = new bool[nc];
        long EdgeNext(int c) => SharpEdgeKey(cornerVerts[c], infoOfCorner[c].vertNext);
        long EdgePrev(int c) => SharpEdgeKey(infoOfCorner[c].vertPrev, cornerVerts[c]);
        for (int i0 = 0; i0 < nc; i0++)
        {
            if (done[i0]) continue;
            int space = fanOfCorner[i0];
            if (space < 0) { done[i0] = true; continue; }
            var fanCorners = fans[space];
            int prevCorner = -1; int org = -1;
            for (int i = fanCorners.Length - 1; i >= 0; i--)
            {
                int corner = fanCorners[i];
                if (org < 0) org = corner;
                else if (Dot3(cornerNormals[3 * org], cornerNormals[3 * org + 1], cornerNormals[3 * org + 2], cornerNormals[3 * corner], cornerNormals[3 * corner + 1], cornerNormals[3 * corner + 2]) < LNOR_TRIGO)
                {
                    long edge = EdgeNext(corner), edgePrev = EdgePrev(corner), prevEdge = EdgeNext(prevCorner);
                    tagged.Add(prevEdge == edgePrev ? prevEdge : edge);
                    org = corner;
                }
                prevCorner = corner;
                done[corner] = true;
            }
            if (fanCorners.Length > 1 && org >= 0)
            {
                int corner = fanCorners[fanCorners.Length - 1];
                if (Dot3(cornerNormals[3 * org], cornerNormals[3 * org + 1], cornerNormals[3 * org + 2], cornerNormals[3 * corner], cornerNormals[3 * corner + 1], cornerNormals[3 * corner + 2]) < LNOR_TRIGO)
                {
                    long edge = EdgeNext(corner), edgePrev = EdgePrev(corner), prevEdge = EdgeNext(prevCorner);
                    tagged.Add(prevEdge == edgePrev ? prevEdge : edge);
                }
            }
        }
        if (sharpEdges == null && tagged.Count > 0) sharpEdges = tagged;
        // pass 2: the fans and spaces with the new sharp edges; the encode, each fan's normals averaged
        var data = new short[2 * nc];
        WalkFansPolygon(P, cornerVerts, faceOffsets, sharpFace, sharpEdges != null && sharpEdges.Count > 0 ? sharpEdges : null, FN, (v, infos, fan, space) =>
        {
            if (fan.Count < 2)
            {
                int c = infos[fan[0]].corner;
                EncodeCustom(space, cornerNormals[3 * c], cornerNormals[3 * c + 1], cornerNormals[3 * c + 2], out data[2 * c], out data[2 * c + 1]);
                return;
            }
            float ax = 0f, ay = 0f, az = 0f;
            foreach (int lc in fan) { int c = infos[lc].corner; ax = (float)(ax + cornerNormals[3 * c]); ay = (float)(ay + cornerNormals[3 * c + 1]); az = (float)(az + cornerNormals[3 * c + 2]); }
            float inv = (float)(1.0f / (float)fan.Count);
            ax = (float)(ax * inv); ay = (float)(ay * inv); az = (float)(az * inv);
            EncodeCustom(space, ax, ay, az, out short e0, out short e1);
            foreach (int lc in fan) { int c = infos[lc].corner; data[2 * c] = e0; data[2 * c + 1] = e1; }
        });
        return data;
    }

    /// <summary>Mesh::corner_normals() of a polygon mesh: with custom data every fan's shorts averaged and decoded against its
    /// space (an invalid space decodes to zero); without, every face sharp gives the face normals, none sharp the fan normals
    /// (= the vertex normals), a mix the fan walk. 3 floats per corner, Blender's frame.</summary>
    internal static float[] PolygonCornerNormals(float[] P, int[] cornerVerts, int[] faceOffsets, bool[] sharpFace, HashSet<long> sharpEdges, short[] custom)
    {
        int nc = cornerVerts.Length, nf = faceOffsets.Length - 1;
        var cn = new float[nc * 3];
        if (nc == 0) return cn;
        var FN = PolygonFaceNormals(P, cornerVerts, faceOffsets);
        if (custom == null)
        {
            bool all = true;
            for (int f = 0; f < nf; f++) if (!sharpFace[f]) { all = false; break; }
            if (all && (sharpEdges == null || sharpEdges.Count == 0))
            {
                for (int f = 0; f < nf; f++) for (int c = faceOffsets[f]; c < faceOffsets[f + 1]; c++) { cn[c * 3] = FN[f * 3]; cn[c * 3 + 1] = FN[f * 3 + 1]; cn[c * 3 + 2] = FN[f * 3 + 2]; }
                return cn;
            }
            WalkFansPolygon(P, cornerVerts, faceOffsets, sharpFace, sharpEdges, FN, (v, infos, fan, space) =>
            {
                foreach (int lc in fan) { int c = infos[lc].corner; cn[c * 3] = space.fnx; cn[c * 3 + 1] = space.fny; cn[c * 3 + 2] = space.fnz; }
            });
            return cn;
        }
        WalkFansPolygon(P, cornerVerts, faceOffsets, sharpFace, sharpEdges, FN, (v, infos, fan, space) =>
        {
            int s0 = 0, s1 = 0;
            foreach (int lc in fan) { int c = infos[lc].corner; s0 += custom[2 * c]; s1 += custom[2 * c + 1]; }
            short a0 = (short)(s0 / fan.Count), a1 = (short)(s1 / fan.Count);
            float dx, dy, dz; DecodeCustom(space, a0, a1, out dx, out dy, out dz);
            foreach (int lc in fan) { int c = infos[lc].corner; cn[c * 3] = dx; cn[c * 3 + 1] = dy; cn[c * 3 + 2] = dz; }
        });
        return cn;
    }
}
