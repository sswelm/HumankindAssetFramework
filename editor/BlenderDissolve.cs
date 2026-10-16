// BlenderDissolve.cs - bmesh.ops.dissolve_limit in C# (step (c) part 2 of the vehicle_rig port, 2026-10-16; docs/Review-Backlog.md
// item (5)). Read at Blender 5.1.2: bmesh_decimate_dissolve.cc (BM_mesh_decimate_dissolve_ex: the EDGE phase - a heap of the
// given edges by minus the cosine between their two faces' stored normals (contiguous winding; a non-manifold edge is never
// dissolved), the top joined while under minus the cosine of the angle limit, each join's new face normal recomputed and its
// edges re-costed; the cleanup killing every edge the joins left wire and every vertex left alone; the VERTEX phase - a heap of
// the given vertices by their edge angle (pi minus the angle between the two edges of a two-edge vertex, 90 degrees else)
// scaled by the face angle across one of them, the top collapsed while under the angle limit unless the collapse would
// fold a face (the degenerate checks in the face's plane), the normals of the faces around the kept edge recomputed, the
// edge's vertices re-costed and the vertices of those faces re-costed when they were marked invalid), bmesh_core.cc
// (BM_faces_join: the region's boundary edges in loop order, BM_face_create_ngon winding from the first boundary loop, a
// double reused, the corners' data copied from the old faces' matching corners, the two old faces killed; bmesh_kernel_loop_reverse
// when the pair's loops start at one vertex; bmesh_kernel_join_edge_kill_vert: the two-edge vertex removed, its other edge taking
// the kept vertex, the killed edge's corners unlinked and their faces shortened, a doubled edge spliced away, faces under three
// corners killed, doubles killed), bmesh_mods.cc (BM_vert_collapse_edge: the killed corner's interpolated data at fac 1 is the
// kept corner's, where a layer interpolates), bmesh_construct.cc (bm_edges_sort_winding), bmesh_query.cc (the pair, contiguity,
// edge angle queries, BM_face_find_double), bmesh_polygon.cc (BM_face_calc_normal: a triangle's, a quad's, Newell's), math_geom.cc
// (axis_dominant_v3_to_m3, isect_point_tri_v2_cw, cross_tri_v2). Every arithmetic step is float32 in Blender's order; the C
// runtime's cosf and asinf through BlenderTrig.
using System;
using System.Collections.Generic;

public static class BlenderDissolve
{
    const float CostInvalid = float.MaxValue;
    const float PiF = (float)Math.PI;
    static readonly float Deg90 = (float)(90.0f * (float)(Math.PI / 180.0));   // DEG2RADF(90.0f)
    static readonly float UnitToAngle = Deg90, AngleToUnit = (float)(1.0f / Deg90);
    const float FltEpsilon = 1.1920929e-7f;

    // ---------------------------------------------------------------- float helpers

    static float Sqrtf(float v) => (float)Math.Sqrt((double)v);
    static float Dot3(float ax, float ay, float az, float bx, float by, float bz) => (float)((float)((float)(ax * bx) + (float)(ay * by)) + (float)(az * bz));

    static float NormalizeV3(ref float x, ref float y, ref float z)
    {
        float d = Dot3(x, y, z, x, y, z);
        if (d > 1.0e-35f) { d = Sqrtf(d); float f = (float)(1.0f / d); x = (float)(x * f); y = (float)(y * f); z = (float)(z * f); }
        else { x = y = z = 0f; d = 0f; }
        return d;
    }

    /// <summary>BM_face_calc_normal into f->no: normal_tri_v3 on three corners, normal_quad_v3 on four, Newell's method past that.</summary>
    public static void FaceNormalUpdate(BMesh bm, int f)
    {
        int l0 = bm.FLFirst[f];
        if (bm.FLen[f] == 3)
        {
            int l1 = bm.LNext[l0], l2 = bm.LNext[l1];
            BlenderDecimate.NormalTri(bm.VCo, bm.LV[l0], bm.LV[l1], bm.LV[l2], out float nx, out float ny, out float nz);
            bm.FNo[3 * f] = nx; bm.FNo[3 * f + 1] = ny; bm.FNo[3 * f + 2] = nz;
            return;
        }
        if (bm.FLen[f] == 4)
        {
            int l1 = bm.LNext[l0], l2 = bm.LNext[l1], l3 = bm.LNext[l2];
            int v1 = bm.LV[l0], v2 = bm.LV[l1], v3 = bm.LV[l2], v4 = bm.LV[l3];
            float n1x = (float)(bm.VCo[3 * v1] - bm.VCo[3 * v3]), n1y = (float)(bm.VCo[3 * v1 + 1] - bm.VCo[3 * v3 + 1]), n1z = (float)(bm.VCo[3 * v1 + 2] - bm.VCo[3 * v3 + 2]);
            float n2x = (float)(bm.VCo[3 * v2] - bm.VCo[3 * v4]), n2y = (float)(bm.VCo[3 * v2 + 1] - bm.VCo[3 * v4 + 1]), n2z = (float)(bm.VCo[3 * v2 + 2] - bm.VCo[3 * v4 + 2]);
            float x = (float)((float)(n1y * n2z) - (float)(n1z * n2y)), y = (float)((float)(n1z * n2x) - (float)(n1x * n2z)), z = (float)((float)(n1x * n2y) - (float)(n1y * n2x));
            NormalizeV3(ref x, ref y, ref z);
            bm.FNo[3 * f] = x; bm.FNo[3 * f + 1] = y; bm.FNo[3 * f + 2] = z;
            return;
        }
        // bm_face_calc_poly_normal: Newell over the corners from the first, the previous corner first
        float nx2 = 0f, ny2 = 0f, nz2 = 0f;
        int vPrev = bm.LV[bm.LPrev[l0]], vCurr = bm.LV[l0], l = l0;
        do
        {
            float px = bm.VCo[3 * vPrev], py = bm.VCo[3 * vPrev + 1], pz = bm.VCo[3 * vPrev + 2];
            float cx = bm.VCo[3 * vCurr], cy = bm.VCo[3 * vCurr + 1], cz = bm.VCo[3 * vCurr + 2];
            nx2 = (float)(nx2 + (float)((float)(py - cy) * (float)(pz + cz)));
            ny2 = (float)(ny2 + (float)((float)(pz - cz) * (float)(px + cx)));
            nz2 = (float)(nz2 + (float)((float)(px - cx) * (float)(py + cy)));
            l = bm.LNext[l]; vPrev = vCurr; vCurr = bm.LV[l];
        } while (l != l0);
        NormalizeV3(ref nx2, ref ny2, ref nz2);
        bm.FNo[3 * f] = nx2; bm.FNo[3 * f + 1] = ny2; bm.FNo[3 * f + 2] = nz2;
    }

    /// <summary>BM_mesh_normals_update's face part (BMO_op_finish on an operator flagged NORMALS_CALC): every live face.</summary>
    public static void FaceNormalsUpdateAll(BMesh bm) { for (int f = 0; f < bm.FaceCount; f++) if (bm.FAlive[f]) FaceNormalUpdate(bm, f); }

    static float AngleV3V3V3(BMesh bm, int a, int b, int c)
    {
        float v1x = (float)(bm.VCo[3 * b] - bm.VCo[3 * a]), v1y = (float)(bm.VCo[3 * b + 1] - bm.VCo[3 * a + 1]), v1z = (float)(bm.VCo[3 * b + 2] - bm.VCo[3 * a + 2]);
        float v2x = (float)(bm.VCo[3 * b] - bm.VCo[3 * c]), v2y = (float)(bm.VCo[3 * b + 1] - bm.VCo[3 * c + 1]), v2z = (float)(bm.VCo[3 * b + 2] - bm.VCo[3 * c + 2]);
        NormalizeV3(ref v1x, ref v1y, ref v1z); NormalizeV3(ref v2x, ref v2y, ref v2z);
        return BlenderWeld.AngleNormalized(v1x, v1y, v1z, v2x, v2y, v2z);
    }

    static float CosV3V3V3(BMesh bm, int p1, int p2, int p3)
    {
        float v1x = (float)(bm.VCo[3 * p2] - bm.VCo[3 * p1]), v1y = (float)(bm.VCo[3 * p2 + 1] - bm.VCo[3 * p1 + 1]), v1z = (float)(bm.VCo[3 * p2 + 2] - bm.VCo[3 * p1 + 2]);
        float v2x = (float)(bm.VCo[3 * p2] - bm.VCo[3 * p3]), v2y = (float)(bm.VCo[3 * p2 + 1] - bm.VCo[3 * p3 + 1]), v2z = (float)(bm.VCo[3 * p2 + 2] - bm.VCo[3 * p3 + 2]);
        NormalizeV3(ref v1x, ref v1y, ref v1z); NormalizeV3(ref v2x, ref v2y, ref v2z);
        return Dot3(v1x, v1y, v1z, v2x, v2y, v2z);
    }

    // ---------------------------------------------------------------- queries (bmesh_query.cc)

    static bool EdgeIsContiguous(BMesh bm, int e)
    {
        int l = bm.EL[e]; if (l == BMesh.None) return false;
        int lo = bm.LRadialNext[l];
        return lo != l && bm.LRadialNext[lo] == l && bm.LV[lo] != bm.LV[l];
    }

    static float EdgeCalcFaceAngle(BMesh bm, int e)
    {
        if (!bm.EdgeIsManifold(e)) return Deg90;
        int f1 = bm.LF[bm.EL[e]], f2 = bm.LF[bm.LRadialNext[bm.EL[e]]];
        return BlenderWeld.AngleNormalized(bm.FNo[3 * f1], bm.FNo[3 * f1 + 1], bm.FNo[3 * f1 + 2], bm.FNo[3 * f2], bm.FNo[3 * f2 + 1], bm.FNo[3 * f2 + 2]);
    }

    static bool VertEdgePair(BMesh bm, int v, out int ea, out int eb)
    {
        ea = bm.VE[v]; eb = BMesh.None;
        if (ea == BMesh.None) return false;
        int e2 = bm.DiskEdgeNext(ea, v);
        if (e2 != ea && bm.DiskEdgeNext(e2, v) == ea) { eb = e2; return true; }
        ea = BMesh.None; return false;
    }

    static float VertCalcEdgeAngle(BMesh bm, int v)
    {
        if (VertEdgePair(bm, v, out int e1, out int e2))
        {
            int v1 = bm.EdgeOtherVert(e1, v), v2 = bm.EdgeOtherVert(e2, v);
            return (float)(PiF - AngleV3V3V3(bm, v1, v, v2));
        }
        return Deg90;
    }

    static float EdgeCalcDissolveError(BMesh bm, int e)
    {
        if (!bm.EdgeIsManifold(e)) return CostInvalid;   // delimit is 0: no delimiter
        int f1 = bm.LF[bm.EL[e]], f2 = bm.LF[bm.LRadialNext[bm.EL[e]]];
        float cosNeg = Dot3(bm.FNo[3 * f1], bm.FNo[3 * f1 + 1], bm.FNo[3 * f1 + 2], bm.FNo[3 * f2], bm.FNo[3 * f2 + 1], bm.FNo[3 * f2 + 2]);
        if (EdgeIsContiguous(bm, e)) cosNeg = (float)(cosNeg * -1f);
        return cosNeg;
    }

    static float VertEdgeFaceAngle(BMesh bm, int v)
    {
        float angle = VertCalcEdgeAngle(bm, v);
        if (bm.VE[v] != BMesh.None && bm.EdgeIsManifold(bm.VE[v]))
            return (float)((float)((float)(angle * AngleToUnit) * (float)(EdgeCalcFaceAngle(bm, bm.VE[v]) * AngleToUnit)) * UnitToAngle);
        return angle;
    }

    static int DiskCountAtMost(BMesh bm, int v, int max)
    {
        int e = bm.VE[v]; if (e == BMesh.None) return 0;
        int count = 0, e0 = e;
        do { count++; if (count == max) break; e = bm.DiskEdgeNext(e, v); } while (e != e0);
        return count;
    }

    static int FaceEdgeShareLoop(BMesh bm, int f, int e)
    {
        int l = bm.EL[e]; if (l == BMesh.None) return BMesh.None;
        int l0 = l;
        do { if (bm.LF[l] == f) return l; l = bm.LRadialNext[l]; } while (l != l0);
        return BMesh.None;
    }

    /// <summary>BM_face_find_double: a face on the first corner's edge with the same corners' edges in either direction.</summary>
    static int FaceFindDouble(BMesh bm, int f)
    {
        int lFirst = bm.FLFirst[f];
        for (int lIter = bm.LRadialNext[lFirst]; lIter != lFirst; lIter = bm.LRadialNext[lIter])
        {
            if (bm.FLen[bm.LF[lIter]] != bm.FLen[f]) continue;
            if (bm.LV[lIter] == bm.LV[lFirst])
            {
                int la = lFirst, lb = lIter, lbInit = lIter;
                do { if (bm.LE[la] != bm.LE[lb]) break; la = bm.LNext[la]; lb = bm.LNext[lb]; } while (lb != lbInit);
                if (lb == lbInit) return bm.LF[lIter];
            }
            else
            {
                int la = lFirst, lb = lIter, lbInit = lIter;
                do { if (bm.LE[la] != bm.LE[lb]) break; la = bm.LPrev[la]; lb = bm.LNext[lb]; } while (lb != lbInit);
                if (lb == lbInit) return bm.LF[lIter];
            }
        }
        return BMesh.None;
    }

    // ---------------------------------------------------------------- the face join (bmesh_core.cc BM_faces_join, do_del false)

    /// <summary>bm_edges_sort_winding then BM_face_create: the boundary edges walked from v1 through v2 around each vertex's disk.</summary>
    static int FaceCreateNgon(BMesh bm, int v1, int v2, List<int> edges, int example, int[] loopSlots)
    {
        int len = edges.Count;
        var mf = new HashSet<int>(edges); var mv = new HashSet<int>();
        foreach (int e in edges) { mv.Add(bm.EV1[e]); mv.Add(bm.EV2[e]); }
        var edgesSort = new int[len]; var vertsSort = new int[len];
        int i = 0, vIter = v1, eIter = bm.VE[v1], eFirst = eIter;
        do
        {
            if (mf.Contains(eIter) && bm.EdgeOtherVert(eIter, vIter) == v2) { i = 1; break; }
            eIter = bm.DiskEdgeNext(eIter, vIter);
        } while (eIter != eFirst);
        if (i == 0) return BMesh.None;
        i = 0;
        do
        {
            if (mf.Contains(eIter))
            {
                if (!mv.Contains(vIter)) return BMesh.None;   // a vertex in the loop twice
                mf.Remove(eIter); edgesSort[i] = eIter;
                mv.Remove(vIter); vertsSort[i] = vIter;
                i += 1;
                vIter = bm.EdgeOtherVert(eIter, vIter);
                if (i == len) { if (vIter != vertsSort[0]) return BMesh.None; break; }
                eFirst = eIter;
            }
            eIter = bm.DiskEdgeNext(eIter, vIter);
        } while (eIter != eFirst);
        if (i != len) return BMesh.None;
        return bm.FaceCreate(vertsSort, edgesSort, len, example, loopSlots);
    }

    /// <summary>BM_faces_join on two faces without do_del: the region's new face, or None when the pair does not form a manifold
    /// region; the shared edge is left in place (wire until the cleanup), the two old faces die.</summary>
    static int FacesJoin(BMesh bm, BlenderWeld.Layers layers, int fa, int fb)
    {
        var jf = new HashSet<int> { fa, fb };
        var edges = new List<int>(); int v1 = BMesh.None, v2 = BMesh.None;
        foreach (int f in new[] { fa, fb })
        {
            int l = bm.FLFirst[f], l0 = l;
            do
            {
                int rlen = 0, lr = l;
                do { if (jf.Contains(bm.LF[lr])) rlen++; lr = bm.LRadialNext[lr]; } while (lr != l);
                if (rlen > 2) return BMesh.None;
                if (rlen == 1)
                {
                    edges.Add(bm.LE[l]);
                    if (v1 == BMesh.None) { v1 = bm.LV[l]; v2 = bm.EdgeOtherVert(bm.LE[l], bm.LV[l]); }
                }
                l = bm.LNext[l];
            } while (l != l0);
        }
        if (edges.Count == 0) return BMesh.None;
        var slots = new int[edges.Count];
        int fNew = FaceCreateNgon(bm, v1, v2, edges, fa, slots);
        if (fNew == BMesh.None) return BMesh.None;
        int fExisting = FaceFindDouble(bm, fNew);
        bool reusing = false;
        if (fExisting != BMesh.None) { bm.FaceKill(fNew); fNew = fExisting; reusing = true; }
        if (!reusing)
        {
            // each new corner takes the data of the old corner on its edge at the same vertex
            int l = bm.FLFirst[fNew], l0 = l;
            do
            {
                int l2 = bm.LRadialNext[l];
                while (l2 != l && !jf.Contains(bm.LF[l2])) l2 = bm.LRadialNext[l2];
                if (l2 != l)
                {
                    if (bm.LV[l2] != bm.LV[l]) l2 = bm.LNext[l2];
                    layers.CopyLoop(l2, l);
                }
                l = bm.LNext[l];
            } while (l != l0);
        }
        bm.FaceKill(fa); bm.FaceKill(fb);
        return fNew;
    }

    /// <summary>bmesh_kernel_loop_reverse: the face's corners walked the other way, every corner moving onto the previous corner's
    /// edge and into that edge's radial cycle.</summary>
    static void LoopReverse(BMesh bm, int f)
    {
        int lFirst = bm.FLFirst[f];
        int ePrev = bm.LE[bm.LPrev[lFirst]];
        int lPrevRadNext = bm.LRadialNext[bm.LPrev[lFirst]], lPrevRadPrev = bm.LRadialPrev[bm.LPrev[lFirst]];
        bool isPrevBoundary = lPrevRadNext == bm.LRadialNext[lPrevRadNext];
        int l = lFirst;
        do
        {
            int eIter = bm.LE[l];
            int lIterRadNext = bm.LRadialNext[l], lIterRadPrev = bm.LRadialPrev[l];
            bool isIterBoundary = lIterRadNext == bm.LRadialNext[lIterRadNext];
            if (isPrevBoundary) { bm.LRadialNext[l] = l; bm.LRadialPrev[l] = l; }
            else { bm.LRadialNext[l] = lPrevRadNext; bm.LRadialPrev[l] = lPrevRadPrev; bm.LRadialPrev[lPrevRadNext] = l; bm.LRadialNext[lPrevRadPrev] = l; }
            if (bm.EL[eIter] == l) bm.EL[eIter] = bm.LNext[l];
            bm.LE[l] = ePrev;
            (bm.LNext[l], bm.LPrev[l]) = (bm.LPrev[l], bm.LNext[l]);
            ePrev = eIter; lPrevRadNext = lIterRadNext; lPrevRadPrev = lIterRadPrev; isPrevBoundary = isIterBoundary;
            l = bm.LPrev[l];   // the next corner, now swapped
        } while (l != lFirst);
    }

    /// <summary>BM_faces_join_pair(l_a, l_b, do_del = false): the second face reversed when both corners start at one vertex.</summary>
    static int FacesJoinPair(BMesh bm, BlenderWeld.Layers layers, int la, int lb)
    {
        if (bm.LV[la] == bm.LV[lb]) LoopReverse(bm, bm.LF[lb]);
        return FacesJoin(bm, layers, bm.LF[la], bm.LF[lb]);
    }

    // ---------------------------------------------------------------- the vertex collapse (bmesh_mods.cc, bmesh_core.cc)

    /// <summary>BM_vert_collapse_edge(e_kill, v_kill, do_del, kill_degenerate_faces, kill_duplicate_faces) = BM_vert_collapse_faces at
    /// fac 1 without joining: the killed corners on the edge take the kept corner's interpolating data, then
    /// bmesh_kernel_join_edge_kill_vert.</summary>
    static int VertCollapseEdge(BMesh bm, BlenderWeld.Layers layers, int eKill, int vKill)
    {
        int tv = bm.EdgeOtherVert(eKill, vKill);
        if (bm.EL[eKill] != BMesh.None)
        {
            int l = bm.EL[eKill], l0 = l;
            do
            {
                if (bm.LV[l] == tv && bm.LV[bm.LNext[l]] == vKill) layers.InterpLoopAtOne(bm.LNext[l], l);   // w = {0, 1}: the kept corner's
                l = bm.LRadialNext[l];
            } while (l != l0);
        }
        return JoinEdgeKillVert(bm, eKill, vKill, true, true, true, true);
    }

    static int JoinEdgeKillVert(BMesh bm, int eKill, int vKill, bool doDel, bool checkEdgeExists, bool killDegenerate, bool killDuplicate)
    {
        if (!bm.VertInEdge(eKill, vKill)) return BMesh.None;
        if (DiskCountAtMost(bm, vKill, 3) != 2) return BMesh.None;
        int eOld = bm.DiskEdgeNext(eKill, vKill);
        int vTarget = bm.EdgeOtherVert(eKill, vKill), vOld = bm.EdgeOtherVert(eOld, vKill);
        if ((bm.EV1[eOld] == vKill && bm.EV2[eOld] == vTarget) || (bm.EV1[eOld] == vTarget && bm.EV2[eOld] == vKill)) return BMesh.None;   // a double edge
        int eSplice = checkEdgeExists ? bm.EdgeExists(vTarget, vOld) : BMesh.None;
        bm.DiskVertReplace(eOld, vTarget, vKill);
        bm.DiskEdgeRemove(eKill, vTarget);
        var degenerate = new Stack<int>(); var duplicate = new Stack<int>();
        if (bm.EL[eKill] != BMesh.None)
        {
            int lKill = bm.EL[eKill], lHead = lKill;
            do
            {
                int lNext = bm.LNext[lKill], lPrev = bm.LPrev[lKill], f = bm.LF[lKill];
                if (bm.LV[lNext] == vKill) bm.LV[lNext] = vTarget;
                bm.LPrev[lNext] = lPrev; bm.LNext[lPrev] = lNext;
                if (bm.FLFirst[f] == lKill) bm.FLFirst[f] = lNext;
                bm.FLen[f]--;
                if (killDegenerate && bm.FLen[f] < 3) degenerate.Push(f);
                else if (killDuplicate) duplicate.Push(f);
                int lKillNext = bm.LRadialNext[lKill];
                bm.LoopKillOnly(lKill);
                lKill = lKillNext;
            } while (lKill != lHead);
        }
        bm.EdgeKillOnly(eKill);
        if (doDel) bm.VertKillOnly(vKill); else bm.VE[vKill] = BMesh.None;
        if (checkEdgeExists && eSplice != BMesh.None) bm.EdgeSplice(eOld, eSplice);
        if (killDegenerate) while (degenerate.Count > 0) { int f = degenerate.Pop(); if (bm.FAlive[f]) bm.FaceKill(f); }
        if (killDuplicate) while (duplicate.Count > 0) { int f = duplicate.Pop(); if (bm.FAlive[f] && FaceFindDouble(bm, f) != BMesh.None) bm.FaceKill(f); }
        return eOld;
    }

    // ---------------------------------------------------------------- the degenerate checks (bmesh_decimate_dissolve.cc)

    static float LinePointSideV2(float l1x, float l1y, float l2x, float l2y, float px, float py)
        => (float)((float)((float)(l1x - px) * (float)(l2y - py)) - (float)((float)(l2x - px) * (float)(l1y - py)));

    static bool IsectPointTriV2Cw(float px, float py, float x1, float y1, float x2, float y2, float x3, float y3)
        => LinePointSideV2(x1, y1, x2, y2, px, py) >= 0f && LinePointSideV2(x2, y2, x3, y3, px, py) >= 0f && LinePointSideV2(x3, y3, x1, y1, px, py) >= 0f;

    static float CrossTriV2(float ax, float ay, float bx, float by, float cx, float cy) => (float)((float)((float)(ax - bx) * (float)(by - cy)) + (float)((float)(ay - by) * (float)(cx - bx)));
    static int SignumI(float a) => a > 0f ? 1 : a < 0f ? -1 : 0;

    static bool LoopCollapseIsDegenerate(BMesh bm, int lEar)
    {
        int f = bm.LF[lEar], vc = bm.LV[lEar];
        float cx = bm.VCo[3 * vc], cy = bm.VCo[3 * vc + 1], cz = bm.VCo[3 * vc + 2];
        var n1 = new float[3]; var n2 = new float[3];
        BlenderPolyfill.AxisDominantRows(bm.FNo[3 * f], bm.FNo[3 * f + 1], bm.FNo[3 * f + 2], n1, n2);
        void Proj(int v, out float rx, out float ry) => BlenderPolyfill.ProjectV2(n1, n2, (float)(bm.VCo[3 * v] - cx), (float)(bm.VCo[3 * v + 1] - cy), (float)(bm.VCo[3 * v + 2] - cz), out rx, out ry);
        int lPrev = bm.LPrev[lEar], lNext = bm.LNext[lEar];
        Proj(bm.LV[lPrev], out float t0x, out float t0y); float t1x = 0f, t1y = 0f; Proj(bm.LV[lNext], out float t2x, out float t2y);
        if (!VertEdgePair(bm, bm.LV[lPrev], out _, out _))
        {
            Proj(bm.LV[bm.LPrev[lPrev]], out float ax, out float ay);
            if (SignumI(CrossTriV2(ax, ay, t0x, t0y, t1x, t1y)) != SignumI(CrossTriV2(ax, ay, t0x, t0y, t2x, t2y))) return true;
        }
        if (!VertEdgePair(bm, bm.LV[lNext], out _, out _))
        {
            Proj(bm.LV[bm.LNext[lNext]], out float ax, out float ay);
            if (SignumI(CrossTriV2(ax, ay, t2x, t2y, t1x, t1y)) != SignumI(CrossTriV2(ax, ay, t2x, t2y, t0x, t0y))) return true;
        }
        if (CrossTriV2(t0x, t0y, t1x, t1y, t2x, t2y) < 0f) { (t1x, t2x) = (t2x, t1x); (t1y, t2y) = (t2y, t1y); }
        int lIter = bm.LNext[lNext], lFirst = lPrev;
        do
        {
            Proj(bm.LV[lIter], out float px, out float py);
            if (IsectPointTriV2Cw(px, py, t0x, t0y, t1x, t1y, t2x, t2y)) return true;
            lIter = bm.LNext[lIter];
        } while (lIter != lFirst);
        return false;
    }

    static bool VertCollapseIsDegenerate(BMesh bm, int v)
    {
        if (!VertEdgePair(bm, v, out int ea, out int eb)) return true;
        if (bm.EdgeIsWire(ea) || bm.EdgeIsWire(eb)) return false;
        int vp0 = bm.EdgeOtherVert(ea, v), vp1 = bm.EdgeOtherVert(eb, v);
        if (Math.Abs(CosV3V3V3(bm, vp0, v, vp1)) < (float)(1.0f - FltEpsilon))
        {
            int l = bm.EL[eb]; if (l == BMesh.None) return false;
            int l0 = l;
            do
            {
                if (bm.FLen[bm.LF[l]] > 3)
                {
                    int lPivot = bm.LV[l] == v ? l : bm.LNext[l];
                    if (LoopCollapseIsDegenerate(bm, lPivot)) return true;
                }
                l = bm.LRadialNext[l];
            } while (l != l0);
        }
        return false;
    }

    // ---------------------------------------------------------------- BM_mesh_decimate_dissolve_ex (do_dissolve_boundaries false, delimit 0)

    /// <summary>bmesh.ops.dissolve_limit(bm, angle_limit, use_dissolve_boundaries = False, verts, edges): the edges joined and the
    /// vertices collapsed under the angle, as the C does, on the given elements in their order. The caller recomputes every face
    /// normal afterwards (the operator's NORMALS_CALC finish).</summary>
    public static void DissolveLimit(BMesh bm, BlenderWeld.Layers layers, float angleLimit, List<int> vinput, List<int> einput)
    {
        angleLimit = Math.Min((float)(Math.PI / 2), angleLimit);
        float angleLimitCosNeg = (float)(-BlenderTrig.Cosf(angleLimit));
        // ---- edges
        var eIndex = new int[bm.EdgeCount]; var wasWire = new bool[bm.EdgeCount];
        for (int e = 0; e < bm.EdgeCount; e++) { eIndex[e] = -1; if (bm.EAlive[e]) wasWire[e] = bm.EdgeIsWire(e); }
        var heap = new BlenderDecimate.Heap(Math.Max(1, einput.Count));
        var table = new int[Math.Max(einput.Count, vinput.Count)]; for (int i = 0; i < table.Length; i++) table[i] = -1;
        for (int i = 0; i < einput.Count; i++) { int e = einput[i]; table[i] = heap.Insert(EdgeCalcDissolveError(bm, e), e); eIndex[e] = i; }
        var loops = new List<int>();
        while (!heap.IsEmpty && heap.TopValue < angleLimitCosNeg)
        {
            int node = heap.TopNode, e = heap.NodePtr(node), i = eIndex[e], fNew = BMesh.None;
            if (bm.EdgeIsManifold(e))
            {
                fNew = FacesJoinPair(bm, layers, bm.EL[e], bm.LRadialNext[bm.EL[e]]);
                if (fNew != BMesh.None)
                {
                    heap.Remove(node); table[i] = -1;
                    FaceNormalUpdate(bm, fNew);
                    int l = bm.FLFirst[fNew], l0 = l;
                    do
                    {
                        int j = eIndex[bm.LE[l]];
                        if (j != -1 && table[j] != -1) heap.UpdatePtr(table[j], EdgeCalcDissolveError(bm, bm.LE[l]), bm.LE[l]);
                        l = bm.LNext[l];
                    } while (l != l0);
                }
            }
            if (fNew == BMesh.None) heap.UpdatePtr(node, CostInvalid, e);
        }
        // ---- cleanup: the edges the joins left wire, in reverse slot order, and the vertices left alone
        var vIndex = new int[bm.VertCount]; int count = 0;
        for (int v = 0; v < bm.VertCount; v++) vIndex[v] = bm.VAlive[v] ? count++ : -1;
        var reverse = new int[count]; for (int i = 0; i < count; i++) reverse[i] = -1;
        var vin = new int[vinput.Count];
        for (int i = 0; i < vinput.Count; i++) { vin[i] = vinput[i]; reverse[vIndex[vinput[i]]] = i; }
        var earray = new List<int>();
        for (int e = 0; e < bm.EdgeCount; e++) if (bm.EAlive[e]) earray.Add(e);
        for (int i = earray.Count - 1; i >= 0; i--)
        {
            int e = earray[i];
            if (!bm.EAlive[e] || !bm.EdgeIsWire(e) || wasWire[e]) continue;
            int v1 = bm.EV1[e], v2 = bm.EV2[e];
            bm.EdgeKill(e);
            if (bm.VE[v1] == BMesh.None) { int r = reverse[vIndex[v1]]; if (r != -1) vin[r] = BMesh.None; bm.VertKill(v1); }
            if (bm.VE[v2] == BMesh.None) { int r = reverse[vIndex[v2]]; if (r != -1) vin[r] = BMesh.None; bm.VertKill(v2); }
        }
        // ---- vertices
        var vIndex2 = new int[bm.VertCount]; for (int v = 0; v < vIndex2.Length; v++) vIndex2[v] = -1;
        var vheap = new BlenderDecimate.Heap(Math.Max(1, vinput.Count));
        for (int i = 0; i < table.Length; i++) table[i] = -1;
        for (int i = 0; i < vin.Length; i++)
        {
            int v = vin[i]; if (v == BMesh.None) continue;
            table[i] = vheap.Insert(VertEdgeFaceAngle(bm, v), v); vIndex2[v] = i;
        }
        while (!vheap.IsEmpty && vheap.TopValue < angleLimit)
        {
            int node = vheap.TopNode, v = vheap.NodePtr(node), i = vIndex2[v], eNew = BMesh.None;
            if (!VertCollapseIsDegenerate(bm, v))
            {
                eNew = VertCollapseEdge(bm, layers, bm.VE[v], v);
                if (eNew != BMesh.None)
                {
                    vheap.Remove(node); table[i] = -1;
                    if (bm.EL[eNew] != BMesh.None)
                    {
                        int l = bm.EL[eNew], l0 = l;
                        do { FaceNormalUpdate(bm, bm.LF[l]); l = bm.LRadialNext[l]; } while (l != l0);
                    }
                    foreach (int vv in new[] { bm.EV1[eNew], bm.EV2[eNew] })
                    {
                        int j = vIndex2[vv];
                        if (j != -1 && table[j] != -1) vheap.UpdatePtr(table[j], VertEdgeFaceAngle(bm, vv), vv);
                    }
                    if (bm.EL[eNew] != BMesh.None)
                    {
                        int l = bm.EL[eNew], l0 = l;
                        do
                        {
                            int lc = bm.LNext[bm.LNext[l]], lcFirst = bm.LPrev[l];
                            do
                            {
                                int j = vIndex2[bm.LV[lc]];
                                if (j != -1 && table[j] != -1 && vheap.NodeValue(table[j]) == CostInvalid) vheap.UpdatePtr(table[j], VertEdgeFaceAngle(bm, bm.LV[lc]), bm.LV[lc]);
                                lc = bm.LNext[lc];
                            } while (lc != lcFirst);
                            l = bm.LRadialNext[l];
                        } while (l != l0);
                    }
                }
            }
            if (eNew == BMesh.None) vheap.UpdatePtr(node, CostInvalid, v);
        }
    }
}
