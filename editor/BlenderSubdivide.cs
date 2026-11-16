// BlenderSubdivide.cs - bmesh.ops.subdivide_edges(bm, edges=..., cuts=1, use_grid_fill=True) as Blender 5.1 runs it
// (2026-11-16, for the Vehicle Lab's tread rig: vehicle_rig.py cuts a tread's long edges in up to three passes before it
// skins the band). Read from bmo_subdivide.cc (bmo_subdivide_edges_exec and its patterns), bmesh_mods.cc (BM_edge_split,
// BM_face_split), bmesh_core.cc (the two Euler splits, in BMesh.cs), bmesh_interp.cc (what a new corner and a new vertex
// inherit) and bmesh_query.cc (BM_vert_pair_share_face_by_len).
//
// What the operator does with one cut and the script's defaults (no smoothing, no fractal, corner type STRAIGHT_CUT,
// no single-edge patterns, grid fill on):
//   1. every face is looked at in mesh order BEFORE anything is cut: its corner edges and how many are marked. Two marked
//      edges that share a vertex and are parallel to within 0.00005 count as none. A triangle with all three marked takes
//      the tri pattern, a quad with all four the grid pattern, a quad with three the three-edge pattern; any other face with a marked edge is remembered without a pattern;
//   2. every marked edge, in the order given, is split at its middle (BM_edge_split from the edge's first vertex, factor
//      0.5): the new vertex's position, the corner data of the new corners (UVs and corner colours blended half and half;
//      the custom-normal shorts copied from the corner before, having no blend) and the vertex data (point colours);
//   3. the remembered faces are finished LAST FIRST (a stack): a pattern connects its new vertices; a face without one
//      and with exactly two marked edges gets one edge between its two new vertices, unless those two vertices also
//      share another face; any other face keeps its new vertices and becomes an n-gon.
// The result holds triangles, quads and n-gons; the element order is Blender's (new vertices, edges and faces at the end
// in creation order), which the tread code's per-vertex and per-edge loops depend on.
using System;
using System.Collections.Generic;

public static class BlenderSubdivide
{
    const float FaceSplitEpsilon = 0.00005f;   // FLT_FACE_SPLIT_EPSILON

    enum Pattern { None, Quad4Edge, Quad3Edge, Tri3Edge }

    sealed class FaceData { public int Start = BMesh.None, Face, TotEdgeSel; public Pattern Pat; }

    /// <summary>The census slots (a test passes an array of CensusSlots counters to see which branches its meshes take).</summary>
    public const int CensusTri3 = 0, CensusQuad4 = 1, CensusQuad3 = 2, CensusTwoJoined = 3, CensusTwoRefused = 4, CensusParallel = 5, CensusLeftNgon = 6, CensusSlots = 7;

    /// <summary>The operator on `edges` (BMesh edge numbers, in the caller's order). `layers` holds the corner data by loop slot
    /// and the point colours by vertex slot; both grow with the mesh.</summary>
    public static void SubdivideEdges(BMesh bm, BlenderWeld.Layers layers, IList<int> edges, int[] census = null)
    {
        var split = new HashSet<int>(edges);     // SUBD_SPLIT on the input edges
        var inner = new HashSet<int>();          // ELE_INNER on vertices
        var stack = new List<FaceData>();
        var fe = new List<int>(); var fv = new List<int>();
        int faceCount0 = bm.FaceCount;
        for (int f = 0; f < faceCount0; f++)
        {
            if (!bm.FAlive[f]) continue;
            fe.Clear(); fv.Clear();
            int e1 = BMesh.None, e2 = BMesh.None, totesel = 0, l = bm.FLFirst[f], l0 = l;
            do
            {
                fe.Add(bm.LE[l]); fv.Add(bm.LV[l]);
                if (split.Contains(bm.LE[l])) { if (e1 == BMesh.None) e1 = bm.LE[l]; else e2 = bm.LE[l]; totesel++; }
            } while ((l = bm.LNext[l]) != l0);
            int len = fe.Count;
            if (totesel == 2 && bm.EdgeShareVertCheck(e1, e2))
            {
                // the two edges' directions, each from its first to its second vertex, normalised: nearly parallel = not a corner
                float ax = (float)(bm.VCo[3 * bm.EV2[e1]] - bm.VCo[3 * bm.EV1[e1]]), ay = (float)(bm.VCo[3 * bm.EV2[e1] + 1] - bm.VCo[3 * bm.EV1[e1] + 1]), az = (float)(bm.VCo[3 * bm.EV2[e1] + 2] - bm.VCo[3 * bm.EV1[e1] + 2]);
                float bx = (float)(bm.VCo[3 * bm.EV2[e2]] - bm.VCo[3 * bm.EV1[e2]]), by = (float)(bm.VCo[3 * bm.EV2[e2] + 1] - bm.VCo[3 * bm.EV1[e2] + 1]), bz = (float)(bm.VCo[3 * bm.EV2[e2] + 2] - bm.VCo[3 * bm.EV1[e2] + 2]);
                Normalize(ref ax, ref ay, ref az); Normalize(ref bx, ref by, ref bz);
                float dot = (float)((float)((float)(ax * bx) + (float)(ay * by)) + (float)(az * bz));
                if (Math.Abs(dot) > (float)(1.0f - FaceSplitEpsilon)) { totesel = 0; if (census != null) census[CensusParallel]++; }
            }
            // the patterns in the table's order: [3] the quad grid, [4] three quad edges, [5] the triangle. Slot [1], the quad CORNER
            // pattern, is empty: measured on Blender 5.1.2 (the oracle's "quads" mesh), a quad with two adjacent cut edges gets the
            // plain join of its two new vertices and no inner vertex - the corner type the Python call leaves unset is STRAIGHT_CUT
            bool matched = false;
            foreach (var pat in new[] { Pattern.Quad4Edge, Pattern.Quad3Edge, Pattern.Tri3Edge })
            {
                var sel = Selected(pat);
                if (sel.Length != len) continue;
                int a = 0;
                for (; a < len; a++)
                {
                    matched = true;
                    for (int b = 0; b < len; b++) if (split.Contains(fe[(b + a) % len]) != sel[b]) { matched = false; break; }
                    if (matched) break;
                }
                if (matched) { stack.Add(new FaceData { Pat = pat, Start = fv[a], Face = f, TotEdgeSel = totesel }); break; }
            }
            if (!matched && totesel != 0) stack.Add(new FaceData { Pat = Pattern.None, Face = f, TotEdgeSel = totesel });
        }

        // the edges, in the slot's order: one cut each, from the edge's own first vertex
        foreach (int e in edges) { int v = EdgeSplit(bm, layers, e, bm.EV1[e], 0.5f, out _); inner.Add(v); }

        var loops = new List<int>(); var verts = new List<int>();
        for (int si = stack.Count - 1; si >= 0; si--)
        {
            var fd = stack[si]; int face = fd.Face;
            if (fd.Pat == Pattern.None)
            {
                if (fd.TotEdgeSel != 2) { if (census != null) census[CensusLeftNgon]++; continue; }
                // no pattern, two cut edges: join their two new vertices
                loops.Clear(); int l = bm.FLFirst[face], l0 = l;
                do { loops.Add(l); } while ((l = bm.LNext[l]) != l0);
                int vlen = loops.Count, a = 0, b;
                for (; a < vlen; a++)
                    if (!inner.Contains(bm.LV[loops[a != 0 ? a - 1 : vlen - 1]]) && inner.Contains(bm.LV[loops[a]])) break;
                if (a == vlen) continue;   // Blender asserts here; no such face can be on the stack
                if (inner.Contains(bm.LV[loops[(a + 2) % vlen]])) b = (a + 2) % vlen;
                else
                {
                    b = 0;
                    for (int j = 0; j < vlen; j++)
                    {
                        b = (j + a + 2) % vlen;
                        if (!inner.Contains(bm.LV[loops[b == 0 ? vlen - 1 : b - 1]]) && inner.Contains(bm.LV[loops[b]])) break;
                    }
                }
                // b += numcuts - 1 adds nothing with one cut
                bool ok = true; var around = new List<int>();
                bm.LoopsOfVert(bm.LV[loops[a]], around);
                foreach (int other in around)
                    if (bm.LF[other] != face && bm.VertInFace(bm.LV[loops[b]], bm.LF[other])) { ok = false; break; }
                if (ok) FaceSplit(bm, layers, face, loops[a], loops[b]);
                if (census != null) census[ok ? CensusTwoJoined : CensusTwoRefused]++;
                continue;
            }
            // the face's vertices from the one AFTER the pattern's start
            int len = bm.FLen[face], at = 0, idx = 0, lf = bm.FLFirst[face], lf0 = lf;
            do { if (bm.LV[lf] == fd.Start) { at = idx + 1; break; } idx++; } while ((lf = bm.LNext[lf]) != lf0);
            verts.Clear(); for (int k = 0; k < len; k++) verts.Add(BMesh.None);
            idx = 0; lf = lf0;
            do { verts[(idx - at + len) % len] = bm.LV[lf]; idx++; } while ((lf = bm.LNext[lf]) != lf0);
            if (census != null) census[fd.Pat == Pattern.Tri3Edge ? CensusTri3 : fd.Pat == Pattern.Quad4Edge ? CensusQuad4 : CensusQuad3]++;
            switch (fd.Pat)
            {
                case Pattern.Tri3Edge:
                {
                    // one cut: the three new vertices verts[0], verts[2], verts[4] joined pairwise
                    if (Connect(bm, layers, verts[4], verts[2]) == BMesh.None) break;
                    Connect(bm, layers, verts[4], verts[0]);
                    Connect(bm, layers, verts[2], verts[0]);
                    break;
                }
                case Pattern.Quad4Edge:
                {
                    // the grid: the bottom and top new vertices joined, that edge cut at its middle, the middle joined to the two sides
                    int e = Connect(bm, layers, verts[0], verts[4]);
                    if (e == BMesh.None) break;   // `continue` in the one-pass loop: the centre is never made, and joining to it is a null in Blender
                    int centre = EdgeSplit(bm, layers, e, bm.EV1[e], 0.5f, out _); inner.Add(centre);
                    Connect(bm, layers, centre, verts[6]);
                    Connect(bm, layers, verts[2], centre);
                    break;
                }
                case Pattern.Quad3Edge:
                {
                    // one cut: i = 0 is the middle - the odd-count join, then the same pair again after `add` moves
                    Connect(bm, layers, verts[0], verts[2]);
                    Connect(bm, layers, verts[4], verts[2]);
                    Connect(bm, layers, verts[0], verts[4]);
                    break;
                }
            }
        }
    }

    static bool[] Selected(Pattern p)
    {
        switch (p)
        {
            case Pattern.Quad4Edge: return new[] { true, true, true, true };
            case Pattern.Quad3Edge: return new[] { true, true, true, false };
            default: return new[] { true, true, true };
        }
    }

    static void Normalize(ref float x, ref float y, ref float z)   // normalize_v3
    {
        float d = (float)((float)((float)(x * x) + (float)(y * y)) + (float)(z * z));
        if (d > 1.0e-35f) { float s = (float)(1.0f / (float)Math.Sqrt(d)); x = (float)(x * s); y = (float)(y * s); z = (float)(z * s); }
        else { x = y = z = 0f; }
    }

    /// <summary>BM_edge_split(e, v, fac): the new vertex at v + (other - v) * fac; the new corners blend the corner at the other
    /// end (weight fac) and the corner at v's side (weight 1 - fac); the new vertex's own data blends v (1 - fac) and the other
    /// end (fac). Returns the new vertex; eNew joins v and it.</summary>
    public static int EdgeSplit(BMesh bm, BlenderWeld.Layers layers, int e, int v, float fac, out int eNew)
    {
        int vOther = bm.EdgeOtherVert(e, v);
        var pairs = new List<int>();
        int vNew = bm.SplitEdgeMakeVert(v, e, out eNew, pairs);
        for (int k = 0; k < 3; k++)
        {
            float d = (float)(bm.VCo[3 * vOther + k] - bm.VCo[3 * v + k]);
            bm.VCo[3 * vNew + k] = (float)(bm.VCo[3 * v + k] + (float)(d * fac));
        }
        bm.EFlag[eNew] = bm.EFlag[e];
        for (int i = 0; i < pairs.Count; i += 2) layers.CopyLoop(pairs[i], pairs[i + 1]);
        // the point layers: the new vertex starts as a copy of v (BM_vert_create's example)
        for (int i = 0; i < layers.PointColor.Count; i++)
        {
            var a = layers.PointColor[i];
            if (4 * vNew + 3 >= a.Length) { Array.Resize(ref a, Math.Max(4 * vNew + 4, a.Length * 2)); layers.PointColor[i] = a; }
            Array.Copy(a, 4 * v, a, 4 * vNew, 4);
        }
        // BM_data_interp_face_vert_edge(v_src_1 = vOther, v = vNew, e): per loop of e, the corner at vNew from its two neighbours
        if (bm.EL[e] != BMesh.None)
        {
            float w0 = fac, w1 = (float)(1.0f - fac);
            int lV1 = BMesh.None, lV = BMesh.None, lV2 = BMesh.None, lIter = bm.EL[e], lFirst = lIter;
            do
            {
                if (bm.LV[lIter] == vOther) { lV1 = lIter; lV = bm.LNext[lV1]; lV2 = bm.LNext[lV]; }
                else if (bm.LV[lIter] == vNew) { lV1 = bm.LNext[lIter]; lV = lIter; lV2 = bm.LPrev[lIter]; }
                if (lV1 == BMesh.None || lV2 == BMesh.None) break;
                layers.EnsureLoop(Math.Max(lV, Math.Max(lV1, lV2)));
                foreach (var uv in layers.Uv) BlenderDecimate.UvInterp(uv, lV1, lV2, w0, w1, lV);
                foreach (var c in layers.CornerColor) BlenderDecimate.ColorInterp(c, lV1, lV2, w0, w1, lV);
            } while ((lIter = bm.LRadialNext[lIter]) != lFirst);
        }
        // BM_data_interp_from_verts(v, vOther, vNew, fac)
        if (fac > 0.0f && fac < 1.0f)
            foreach (var c in layers.PointColor) BlenderDecimate.ColorInterp(c, v, vOther, (float)(1.0f - fac), fac, vNew);
        else if (fac >= 1.0f)
            foreach (var c in layers.PointColor) Array.Copy(c, 4 * vOther, c, 4 * vNew, 4);
        return vNew;
    }

    /// <summary>BM_face_split(f, la, lb): the kernel split, the two new corners taking the data of the corners they double.
    /// Returns the new edge (None when the two corners are neighbours or not both of f).</summary>
    public static int FaceSplit(BMesh bm, BlenderWeld.Layers layers, int f, int la, int lb)
    {
        if (bm.LoopIsAdjacent(la, lb) || bm.LF[la] != f || bm.LF[lb] != f) return BMesh.None;
        bm.SplitFaceMakeEdge(f, la, lb, out int eNew, out int lF1, out int lF2);
        layers.CopyLoop(lb, lF1); layers.CopyLoop(la, lF2);
        return eNew;
    }

    /// <summary>connect_smallest_face(a, b): the two vertices joined across the SMALLEST face both are on
    /// (BM_vert_pair_share_face_by_len with neighbours allowed: a's loops in LOOPS_OF_VERT order, a face taken when it is the
    /// first or has fewer corners than the one held). Returns the new edge or None.</summary>
    static int Connect(BMesh bm, BlenderWeld.Layers layers, int va, int vb)
    {
        if (bm.VE[va] == BMesh.None || bm.VE[vb] == BMesh.None) return BMesh.None;
        var around = new List<int>(); bm.LoopsOfVert(va, around);
        int fCur = BMesh.None, la = BMesh.None, lb = BMesh.None;
        foreach (int l in around)
        {
            if (fCur != BMesh.None && !(bm.FLen[bm.LF[l]] < bm.FLen[fCur])) continue;
            int other = bm.FaceVertShareLoop(bm.LF[l], vb);
            if (other != BMesh.None) { fCur = bm.LF[l]; la = l; lb = other; }
        }
        if (fCur == BMesh.None) return BMesh.None;
        return FaceSplit(bm, layers, fCur, la, lb);
    }
}
