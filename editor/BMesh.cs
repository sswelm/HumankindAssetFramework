// BMesh.cs - Blender's BMesh kernel, as far as the Decimate modifier's COLLAPSE walks it (step 5 of replacing Blender,
// milestone b, 2026-10-03). Read at Blender 5.1.2 (ec6e62d4): bmesh_core.cc (create, kill, splice), bmesh_structure.cc
// (the disk and radial cycles), bmesh_iterators.cc (LOOPS_OF_VERT and friends), bmesh_query.cc / bmesh_query_inline.hh
// (the edge queries, the fan step), bmesh_mesh_convert.cc (BM_mesh_bm_from_me).
//   * An element is an integer into arrays (Blender's pointer); its number is its CREATION order, which is the order
//     BM_ITER_MESH walks (the element pool, freed slots skipped) as long as nothing is created after a kill - the collapse
//     creates nothing, and Create throws once anything has died rather than model the pool's free list.
//   * The DISK cycle of a vertex (the edges around it) is a circular doubly linked list with its head at v->e; a new edge
//     is appended at the END (before the head); removing the head moves it to the next edge. Each edge carries two links,
//     one per end: here EDiskNext/EDiskPrev[2e] for v1's and [2e + 1] for v2's.
//   * The RADIAL cycle of an edge (its face corners) is another circular list; a new loop goes in right after the head and
//     BECOMES the head (so the LAST face created on an edge is e->l, and the first follows it).
//   * The order matters: the collapse re-costs the kept vertex's edges in disk order and its fan in LOOPS_OF_VERT order,
//     and BLI_heap breaks equal costs by its insertion order - a port with the right links in the wrong order collapses
//     different edges on a mirror-exact hull. tools/decimate-drill holds every link list to Blender's own (bmesh.from_mesh,
//     then scripted kills and splices), per object, in order.
using System;
using System.Collections.Generic;

public sealed class BMesh
{
    public const int None = -1;
    // BM_ELEM_* header flags (bmesh_class.hh); only SMOOTH and TAG matter to the collapse
    public const byte FlagSelect = 1, FlagHidden = 2, FlagSeam = 4, FlagSmooth = 8, FlagTag = 16;

    // vertices
    public int[] VE = new int[0];           // v->e: an edge of the disk cycle (its head), or None
    public bool[] VAlive = new bool[0];
    public float[] VCo = new float[0];      // 3 per vertex
    public float[] VNo = new float[0];
    public byte[] VFlag = new byte[0];
    public int VertCount, TotVert;           // created ever; alive now (bm->totvert)
    // edges
    public int[] EV1 = new int[0], EV2 = new int[0], EL = new int[0];
    public int[] EDiskNext = new int[0], EDiskPrev = new int[0];   // 2 per edge: [2e] = v1_disk_link, [2e + 1] = v2_disk_link
    public bool[] EAlive = new bool[0];
    public byte[] EFlag = new byte[0];
    public int EdgeCount, TotEdge;
    // loops (face corners)
    public int[] LV = new int[0], LE = new int[0], LF = new int[0], LNext = new int[0], LPrev = new int[0], LRadialNext = new int[0], LRadialPrev = new int[0];
    public bool[] LAlive = new bool[0];
    public int LoopCount, TotLoop;
    // faces
    public int[] FLFirst = new int[0], FLen = new int[0], FMat = new int[0];
    public float[] FNo = new float[0];
    public bool[] FAlive = new bool[0];
    public byte[] FFlag = new byte[0];
    public int FaceCount, TotFace;

    bool anyKilled;

    // ---------------------------------------------------------------- BM_mesh_bm_from_me

    /// <summary>BM_mesh_bm_from_me on a laid-out mesh (BlenderMesh, validated): the vertices in order, the edges in order
    /// (each appended to both disk cycles), the faces in order (each corner's loop appended to its edge's radial cycle;
    /// corner k's edge joins corner k and k + 1, as mesh_calc_edges writes corner_edges). co is Blender's vertex
    /// positions (3 per vertex) or null for zeros; sharpFace per face (null = all smooth) and material per face (null =
    /// 0) set the face flags and mat_nr as the conversion does; face normals are left for the caller (BM_face_normal_update
    /// is normal_tri_v3 on the positions; vertex normals are copied from the mesh's own).</summary>
    public static BMesh FromMesh(BlenderMesh m, float[] co, bool[] sharpFace = null, int[] material = null)
    {
        int nv = m.VertexCount, ne = m.ValidEdges.Length / 2, nf = m.Faces.Length / 3;
        var bm = new BMesh();
        bm.Reserve(nv, ne, nf, 3 * nf);
        for (int v = 0; v < nv; v++)
        {
            int i = bm.VertCreate(co == null ? 0 : co[3 * v], co == null ? 0 : co[3 * v + 1], co == null ? 0 : co[3 * v + 2]);
            bm.VFlag[i] = 0;
        }
        var edgeOf = new Dictionary<long, int>(ne, BlenderMesh.EdgeKeyComparer.Instance);
        for (int e = 0; e < ne; e++)
        {
            int a = m.ValidEdges[2 * e], b = m.ValidEdges[2 * e + 1];
            int i = bm.EdgeCreate(a, b);
            bm.EFlag[i] = FlagSmooth;   // hflag = 0, then BM_ELEM_SMOOTH unless sharp_edge (the importer writes none)
            edgeOf[Key(a, b)] = i;
        }
        var verts = new int[3]; var edges = new int[3];
        for (int f = 0; f < nf; f++)
        {
            for (int k = 0; k < 3; k++) verts[k] = m.Faces[3 * f + k];
            for (int k = 0; k < 3; k++)
            {
                if (!edgeOf.TryGetValue(Key(verts[k], verts[(k + 1) % 3]), out edges[k]))
                    throw new InvalidOperationException($"face {f}: no edge joins corners {verts[k]} and {verts[(k + 1) % 3]}");
            }
            int i = bm.FaceCreate(verts, edges, 3);
            bm.FFlag[i] = (sharpFace != null && sharpFace[f]) ? (byte)0 : FlagSmooth;
            bm.FMat[i] = material == null ? 0 : material[f];
        }
        return bm;
    }

    static long Key(int a, int b) { int lo = Math.Min(a, b), hi = Math.Max(a, b); return ((long)lo << 32) | (uint)hi; }

    // ---------------------------------------------------------------- create

    void Reserve(int nv, int ne, int nf, int nl)
    {
        Grow(ref VE, nv); Grow(ref VAlive, nv); Grow(ref VCo, 3 * nv); Grow(ref VNo, 3 * nv); Grow(ref VFlag, nv);
        Grow(ref EV1, ne); Grow(ref EV2, ne); Grow(ref EL, ne); Grow(ref EDiskNext, 2 * ne); Grow(ref EDiskPrev, 2 * ne); Grow(ref EAlive, ne); Grow(ref EFlag, ne);
        Grow(ref LV, nl); Grow(ref LE, nl); Grow(ref LF, nl); Grow(ref LNext, nl); Grow(ref LPrev, nl); Grow(ref LRadialNext, nl); Grow(ref LRadialPrev, nl); Grow(ref LAlive, nl);
        Grow(ref FLFirst, nf); Grow(ref FLen, nf); Grow(ref FMat, nf); Grow(ref FNo, 3 * nf); Grow(ref FAlive, nf); Grow(ref FFlag, nf);
    }

    static void Grow<T>(ref T[] a, int need) { if (a.Length >= need) return; int n = Math.Max(need, Math.Max(8, a.Length * 2)); Array.Resize(ref a, n); }

    void NoCreateAfterKill() { if (anyKilled) throw new InvalidOperationException("BMesh: creating an element after a kill would land in the pool's free list, which this port does not model"); }

    /// <summary>BM_vert_create: no edges, flags 0.</summary>
    public int VertCreate(float x, float y, float z)
    {
        NoCreateAfterKill();
        int v = VertCount++;
        Grow(ref VE, VertCount); Grow(ref VAlive, VertCount); Grow(ref VCo, 3 * VertCount); Grow(ref VNo, 3 * VertCount); Grow(ref VFlag, VertCount);
        VE[v] = None; VAlive[v] = true; VCo[3 * v] = x; VCo[3 * v + 1] = y; VCo[3 * v + 2] = z; VNo[3 * v] = VNo[3 * v + 1] = VNo[3 * v + 2] = 0; VFlag[v] = 0;
        TotVert++;
        return v;
    }

    /// <summary>BM_edge_create: v1, v2, no loops, appended to both disk cycles; flags SMOOTH.</summary>
    public int EdgeCreate(int v1, int v2)
    {
        NoCreateAfterKill();
        int e = EdgeCount++;
        Grow(ref EV1, EdgeCount); Grow(ref EV2, EdgeCount); Grow(ref EL, EdgeCount); Grow(ref EDiskNext, 2 * EdgeCount); Grow(ref EDiskPrev, 2 * EdgeCount); Grow(ref EAlive, EdgeCount); Grow(ref EFlag, EdgeCount);
        EV1[e] = v1; EV2[e] = v2; EL[e] = None; EFlag[e] = FlagSmooth; EAlive[e] = true;
        EDiskNext[2 * e] = EDiskPrev[2 * e] = EDiskNext[2 * e + 1] = EDiskPrev[2 * e + 1] = None;
        DiskEdgeAppend(e, v1);
        DiskEdgeAppend(e, v2);
        TotEdge++;
        return e;
    }

    int LoopCreate(int v, int e, int f)
    {
        int l = LoopCount++;
        Grow(ref LV, LoopCount); Grow(ref LE, LoopCount); Grow(ref LF, LoopCount); Grow(ref LNext, LoopCount); Grow(ref LPrev, LoopCount); Grow(ref LRadialNext, LoopCount); Grow(ref LRadialPrev, LoopCount); Grow(ref LAlive, LoopCount);
        LV[l] = v; LE[l] = e; LF[l] = f; LNext[l] = LPrev[l] = LRadialNext[l] = LRadialPrev[l] = None; LAlive[l] = true;
        TotLoop++;
        return l;
    }

    /// <summary>BM_face_create(verts, edges, len): corner k is verts[k] on edges[k] (which joins verts[k] and verts[k + 1]);
    /// each loop is appended to its edge's radial cycle in corner order; flags 0, normal zero.</summary>
    public int FaceCreate(int[] verts, int[] edges, int len)
    {
        NoCreateAfterKill();
        if (len == 0) return None;
        int f = FaceCount++;
        Grow(ref FLFirst, FaceCount); Grow(ref FLen, FaceCount); Grow(ref FMat, FaceCount); Grow(ref FNo, 3 * FaceCount); Grow(ref FAlive, FaceCount); Grow(ref FFlag, FaceCount);
        FLen[f] = len; FMat[f] = 0; FNo[3 * f] = FNo[3 * f + 1] = FNo[3 * f + 2] = 0; FAlive[f] = true; FFlag[f] = 0;
        TotFace++;
        int startl = LoopCreate(verts[0], edges[0], f);   // bm_face_boundary_add
        RadialLoopAppend(edges[0], startl);
        FLFirst[f] = startl;
        int lastl = startl;
        for (int i = 1; i < len; i++)
        {
            int l = LoopCreate(verts[i], edges[i], f);
            RadialLoopAppend(edges[i], l);
            LPrev[l] = lastl; LNext[lastl] = l; lastl = l;
        }
        LPrev[startl] = lastl; LNext[lastl] = startl;
        return f;
    }

    // ---------------------------------------------------------------- the disk cycle (bmesh_structure.cc)

    /// <summary>bmesh_disk_edge_link_from_vert: the index of e's link for its end v (v1's when e->v1 == v, else v2's).</summary>
    public int DiskLink(int e, int v) => 2 * e + (EV1[e] == v ? 0 : 1);

    public int DiskEdgeNext(int e, int v) => EDiskNext[DiskLink(e, v)];   // BM_DISK_EDGE_NEXT
    public int DiskEdgePrev(int e, int v) => EDiskPrev[DiskLink(e, v)];

    /// <summary>bmesh_disk_edge_append: e becomes the last edge of v's cycle (the one before the head v->e).</summary>
    public void DiskEdgeAppend(int e, int v)
    {
        if (VE[v] == None)
        {
            int dl1 = DiskLink(e, v);
            VE[v] = e;
            EDiskNext[dl1] = EDiskPrev[dl1] = e;
        }
        else
        {
            int dl1 = DiskLink(e, v);
            int dl2 = DiskLink(VE[v], v);
            int dl3 = EDiskPrev[dl2] != None ? DiskLink(EDiskPrev[dl2], v) : -1;
            EDiskNext[dl1] = VE[v];
            EDiskPrev[dl1] = EDiskPrev[dl2];
            EDiskPrev[dl2] = e;
            if (dl3 >= 0) EDiskNext[dl3] = e;
        }
    }

    /// <summary>bmesh_disk_edge_remove: unlink e from v's cycle; a removed head passes to its next edge.</summary>
    public void DiskEdgeRemove(int e, int v)
    {
        int dl1 = DiskLink(e, v);
        if (EDiskPrev[dl1] != None) { int dl2 = DiskLink(EDiskPrev[dl1], v); EDiskNext[dl2] = EDiskNext[dl1]; }
        if (EDiskNext[dl1] != None) { int dl2 = DiskLink(EDiskNext[dl1], v); EDiskPrev[dl2] = EDiskPrev[dl1]; }
        if (VE[v] == e) VE[v] = (e != EDiskNext[dl1]) ? EDiskNext[dl1] : None;
        EDiskNext[dl1] = EDiskPrev[dl1] = None;
    }

    void DiskVertSwap(int e, int vDst, int vSrc)
    {
        if (EV1[e] == vSrc) { EV1[e] = vDst; EDiskNext[2 * e] = EDiskPrev[2 * e] = None; }
        else if (EV2[e] == vSrc) { EV2[e] = vDst; EDiskNext[2 * e + 1] = EDiskPrev[2 * e + 1] = None; }
        else throw new InvalidOperationException("bmesh_disk_vert_swap: the vertex is not an end of the edge");
    }

    /// <summary>bmesh_disk_vert_replace: e leaves vSrc's cycle, points at vDst and is appended to vDst's cycle.</summary>
    public void DiskVertReplace(int e, int vDst, int vSrc)
    {
        DiskEdgeRemove(e, vSrc);
        DiskVertSwap(e, vDst, vSrc);
        DiskEdgeAppend(e, vDst);
    }

    /// <summary>bmesh_edge_vert_swap: the loops on e that sit at vSrc (their own or their next's vertex) move to vDst, then
    /// the edge itself.</summary>
    public void EdgeVertSwap(int e, int vDst, int vSrc)
    {
        if (EL[e] != None)
        {
            int lIter = EL[e], lFirst = lIter;
            do
            {
                if (LV[lIter] == vSrc) LV[lIter] = vDst;
                else if (LV[LNext[lIter]] == vSrc) LV[LNext[lIter]] = vDst;
            } while ((lIter = LRadialNext[lIter]) != lFirst);
        }
        DiskVertReplace(e, vDst, vSrc);
    }

    /// <summary>bmesh_disk_edge_exists / BM_edge_exists: an edge joining v1 and v2, walking v1's cycle, or None.</summary>
    public int EdgeExists(int v1, int v2)
    {
        if (VE[v1] == None) return None;
        int eFirst = VE[v1], eIter = eFirst;
        do { if (EV1[eIter] == v2 || EV2[eIter] == v2) return eIter; } while ((eIter = DiskEdgeNext(eIter, v1)) != eFirst);
        return None;
    }

    /// <summary>The edges of v in disk order (BM_EDGES_OF_VERT), from the head v->e.</summary>
    public void EdgesOfVert(int v, List<int> into)
    {
        into.Clear();
        if (VE[v] == None) return;
        int eFirst = VE[v], eIter = eFirst;
        do { into.Add(eIter); } while ((eIter = DiskEdgeNext(eIter, v)) != eFirst);
    }

    // ---------------------------------------------------------------- the radial cycle

    /// <summary>bmesh_radial_loop_append: l goes in after the head and becomes the head e->l.</summary>
    public void RadialLoopAppend(int e, int l)
    {
        if (EL[e] == None) { EL[e] = l; LRadialNext[l] = LRadialPrev[l] = l; }
        else
        {
            LRadialPrev[l] = EL[e];
            LRadialNext[l] = LRadialNext[EL[e]];
            LRadialPrev[LRadialNext[EL[e]]] = l;
            LRadialNext[EL[e]] = l;
            EL[e] = l;
        }
        LE[l] = e;
    }

    /// <summary>bmesh_radial_loop_remove: a removed head passes to its radial next; the loop's edge link is cleared.</summary>
    public void RadialLoopRemove(int e, int l)
    {
        if (LE[l] != e) throw new InvalidOperationException("bmesh_radial_loop_remove: the loop is not on the edge");
        if (LRadialNext[l] != l)
        {
            if (l == EL[e]) EL[e] = LRadialNext[l];
            LRadialPrev[LRadialNext[l]] = LRadialPrev[l];
            LRadialNext[LRadialPrev[l]] = LRadialNext[l];
        }
        else
        {
            if (l == EL[e]) EL[e] = None;
            else throw new InvalidOperationException("bmesh_radial_loop_remove: a lone loop that is not the head");
        }
        LRadialNext[l] = LRadialPrev[l] = None;
        LE[l] = None;
    }

    /// <summary>The loops of e in radial order (BM_LOOPS_OF_EDGE), from the head e->l.</summary>
    public void LoopsOfEdge(int e, List<int> into)
    {
        into.Clear();
        if (EL[e] == None) return;
        int lFirst = EL[e], lIter = lFirst;
        do { into.Add(lIter); } while ((lIter = LRadialNext[lIter]) != lFirst);
    }

    /// <summary>The loops of f from l_first (BM_LOOPS_OF_FACE).</summary>
    public void LoopsOfFace(int f, List<int> into)
    {
        into.Clear();
        int lFirst = FLFirst[f], lIter = lFirst;
        do { into.Add(lIter); } while ((lIter = LNext[lIter]) != lFirst);
    }

    int RadialFaceVertCount(int l, int v)
    {
        int count = 0, lIter = l;
        do { if (LV[lIter] == v) count++; } while ((lIter = LRadialNext[lIter]) != l);
        return count;
    }

    public bool RadialFaceVertCheck(int l, int v)
    {
        int lIter = l;
        do { if (LV[lIter] == v) return true; } while ((lIter = LRadialNext[lIter]) != l);
        return false;
    }

    public int RadialFaceLoopFindFirst(int l, int v)
    {
        int lIter = l;
        do { if (LV[lIter] == v) return lIter; } while ((lIter = LRadialNext[lIter]) != l);
        return None;
    }

    public int RadialFaceLoopFindNext(int l, int v)
    {
        int lIter = LRadialNext[l];
        do { if (LV[lIter] == v) return lIter; } while ((lIter = LRadialNext[lIter]) != l);
        return l;
    }

    /// <summary>bmesh_disk_facevert_count: how many face corners sit at v.</summary>
    public int DiskFaceVertCount(int v)
    {
        int count = 0;
        if (VE[v] != None)
        {
            int eFirst = VE[v], eIter = eFirst;
            do { if (EL[eIter] != None) count += RadialFaceVertCount(EL[eIter], v); } while ((eIter = DiskEdgeNext(eIter, v)) != eFirst);
        }
        return count;
    }

    /// <summary>bmesh_disk_faceloop_find_first: from edge e around v, the first edge with a face gives its loop at v.</summary>
    public int DiskFaceLoopFindFirst(int e, int v)
    {
        int eIter = e;
        do { if (EL[eIter] != None) return LV[EL[eIter]] == v ? EL[eIter] : LNext[EL[eIter]]; } while ((eIter = DiskEdgeNext(eIter, v)) != e);
        return None;
    }

    /// <summary>bmesh_disk_faceedge_find_next: the next edge after e around v that has a corner at v (e itself if none).</summary>
    public int DiskFaceEdgeFindNext(int e, int v)
    {
        int eFind = DiskEdgeNext(e, v);
        do { if (EL[eFind] != None && RadialFaceVertCheck(EL[eFind], v)) return eFind; } while ((eFind = DiskEdgeNext(eFind, v)) != e);
        return e;
    }

    /// <summary>BM_LOOPS_OF_VERT (bmiter__loop_of_vert_begin/step): the corners at v, edge by edge around the disk from
    /// v->e, within an edge along its radial cycle - exactly as many as DiskFaceVertCount says.</summary>
    public void LoopsOfVert(int v, List<int> into)
    {
        into.Clear();
        int count = DiskFaceVertCount(v);
        if (count == 0) return;
        int lFirst = DiskFaceLoopFindFirst(VE[v], v);
        int eNext = LE[lFirst];
        int lNext = lFirst;
        while (count > 0)
        {
            int lCurr = lNext;
            count--;
            lNext = RadialFaceLoopFindNext(lNext, v);
            if (lNext == lFirst)
            {
                eNext = DiskFaceEdgeFindNext(eNext, v);
                lFirst = RadialFaceLoopFindFirst(EL[eNext], v);
                lNext = lFirst;
            }
            into.Add(lCurr);
        }
    }

    // ---------------------------------------------------------------- queries (bmesh_query_inline.hh, bmesh_query.cc)

    public bool VertInEdge(int e, int v) => EV1[e] == v || EV2[e] == v;
    public int EdgeOtherVert(int e, int v) => EV1[e] == v ? EV2[e] : EV2[e] == v ? EV1[e] : None;
    public bool EdgeIsWire(int e) => EL[e] == None;
    public bool EdgeIsBoundary(int e) { int l = EL[e]; return l != None && LRadialNext[l] == l; }
    public bool EdgeIsManifold(int e) { int l = EL[e]; return l != None && LRadialNext[l] != l && LRadialNext[LRadialNext[l]] == l; }
    public bool EdgeShareVertCheck(int a, int b) => EV1[a] == EV1[b] || EV1[a] == EV2[b] || EV2[a] == EV1[b] || EV2[a] == EV2[b];

    /// <summary>BM_edge_loop_pair: the two loops of a manifold edge, e->l first.</summary>
    public bool EdgeLoopPair(int e, out int la, out int lb)
    {
        la = EL[e];
        if (la != None) { lb = LRadialNext[la]; if (lb != la && LRadialNext[lb] == la) return true; }
        la = lb = None;
        return false;
    }

    /// <summary>BM_edge_other_loop: the loop of the OTHER face on e that sits at l's vertex.</summary>
    public int EdgeOtherLoop(int e, int l)
    {
        int lOther = LE[l] == e ? l : LPrev[l];
        lOther = LRadialNext[lOther];
        if (LV[lOther] == LV[l]) { }
        else if (LV[LNext[lOther]] == LV[l]) lOther = LNext[lOther];
        else throw new InvalidOperationException("BM_edge_other_loop: the radial neighbour does not touch the vertex");
        return lOther;
    }

    /// <summary>BM_vert_step_fan_loop: from loop l at a vertex, cross the face to its other edge at that vertex and step onto
    /// the neighbouring face's loop there; None when that edge is not manifold. eStep is the edge just crossed, updated.</summary>
    public int VertStepFanLoop(int l, ref int eStep)
    {
        int ePrev = eStep, eNext;
        if (LE[l] == ePrev) eNext = LE[LPrev[l]];
        else if (LE[LPrev[l]] == ePrev) eNext = LE[l];
        else throw new InvalidOperationException("BM_vert_step_fan_loop: the loop is not on the edge");
        if (EdgeIsManifold(eNext)) { eStep = eNext; return EdgeOtherLoop(eNext, l); }
        return None;
    }

    public bool VertInFace(int v, int f)
    {
        int lFirst = FLFirst[f], lIter = lFirst;
        do { if (LV[lIter] == v) return true; } while ((lIter = LNext[lIter]) != lFirst);
        return false;
    }

    /// <summary>BM_vert_pair_share_face_check: do a and b sit on a common face?</summary>
    public bool VertPairShareFaceCheck(int a, int b)
    {
        if (VE[a] == None || VE[b] == None) return false;
        var loops = new List<int>();
        LoopsOfVert(a, loops);   // BM_FACES_OF_VERT walks the same loops
        foreach (int l in loops) if (VertInFace(b, LF[l])) return true;
        return false;
    }

    // ---------------------------------------------------------------- kill and splice (bmesh_core.cc)

    void KillOnlyVert(int v) { VAlive[v] = false; TotVert--; anyKilled = true; }
    void KillOnlyEdge(int e) { EAlive[e] = false; TotEdge--; anyKilled = true; }
    void KillOnlyFace(int f) { FAlive[f] = false; TotFace--; anyKilled = true; }
    void KillOnlyLoop(int l) { LAlive[l] = false; TotLoop--; anyKilled = true; }

    /// <summary>BM_face_kill: each loop leaves its radial cycle (in face order), then the face dies.</summary>
    public void FaceKill(int f)
    {
        if (FLFirst[f] != None)
        {
            int lFirst = FLFirst[f], lIter = lFirst;
            do
            {
                int lNext = LNext[lIter];
                RadialLoopRemove(LE[lIter], lIter);
                KillOnlyLoop(lIter);
                lIter = lNext;
            } while (lIter != lFirst);
        }
        KillOnlyFace(f);
    }

    /// <summary>BM_edge_kill: the faces on e (the head's face, repeatedly), then e leaves both disks and dies.</summary>
    public void EdgeKill(int e)
    {
        while (EL[e] != None) FaceKill(LF[EL[e]]);
        DiskEdgeRemove(e, EV1[e]);
        DiskEdgeRemove(e, EV2[e]);
        KillOnlyEdge(e);
    }

    /// <summary>BM_vert_kill: the edges at v (the head, repeatedly), then v dies.</summary>
    public void VertKill(int v)
    {
        while (VE[v] != None) EdgeKill(VE[v]);
        KillOnlyVert(v);
    }

    /// <summary>BM_vert_splice: every edge of vSrc (its head, repeatedly) moves to vDst - appended to vDst's disk - then
    /// vSrc dies. Double edges are left for the caller (the collapse splices them).</summary>
    public bool VertSplice(int vDst, int vSrc)
    {
        if (vSrc == vDst) return false;
        int e;
        while ((e = VE[vSrc]) != None) EdgeVertSwap(e, vDst, vSrc);
        VertKill(vSrc);
        return true;
    }

    /// <summary>BM_edge_splice: eSrc's loops (its head, repeatedly) are appended to eDst's radial cycle, then eSrc dies;
    /// false when the two edges do not join the same vertices.</summary>
    public bool EdgeSplice(int eDst, int eSrc)
    {
        if (!VertInEdge(eSrc, EV1[eDst]) || !VertInEdge(eSrc, EV2[eDst])) return false;
        while (EL[eSrc] != None)
        {
            int l = EL[eSrc];
            RadialLoopRemove(eSrc, l);
            RadialLoopAppend(eDst, l);
        }
        EdgeKill(eSrc);
        return true;
    }
}
