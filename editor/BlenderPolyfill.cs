// BlenderPolyfill.cs - bmesh.ops.triangulate on quads and n-gons, in C# (step (c) part 2 of the vehicle_rig port, 2026-10-16;
// docs/Review-Backlog.md item (5)). Read at Blender 5.1.2: bmesh_triangulate.cc (BM_mesh_triangulate: every tagged face of
// four corners or more, in slot order; the mapping variant the Python operator takes leaves a double face in place),
// bmesh_polygon.cc (BM_face_triangulate: a quad by the BEAUTY rule - is_quad_flip_v3, then BM_verts_calc_rotate_beauty -, an
// n-gon projected by axis_dominant_v3_to_m3_negate onto its normal's plane, BLI_polyfill_calc then BLI_polyfill_beautify; the
// triangles created with BM_face_create_verts (the three edges ensured in the order (v2 v0), (v0 v1), (v1 v2)) copying the
// face's attributes and each corner's loop data, the LAST triangle swapped into the original face's slot and its own slot
// killed), polyfill_2d.cc (ear clipping: the signs of the corners, the ear search alternating direction from the last cut, a
// kd-tree of the concave corners answering "is any corner inside this ear" with the precomputed edge test, the convex
// corners never tested), polyfill_2d_beautify.cc (the half-edge rotation by the area-over-perimeter rule through BLI_heap),
// bmesh_beautify.cc (the quad's rotate-beauty cost through the same rule in the plane of both triangles), math_geom.cc.
// Every arithmetic step is float32 in Blender's order; the kd-tree's index cache is left out (it only short-cuts to the same
// answer).
using System;
using System.Collections.Generic;

public static class BlenderPolyfill
{
    const int Unset = -1;
    const sbyte Concave = -1, Tangential = 0, Convex = 1;

    // ---------------------------------------------------------------- float helpers (BLI_math, written out)

    static float Sqrtf(float v) => (float)Math.Sqrt((double)v);
    static float CrossTriV2(float ax, float ay, float bx, float by, float cx, float cy) => (float)((float)((float)(ax - bx) * (float)(by - cy)) + (float)((float)(ay - by) * (float)(cx - bx)));
    static float LenV2V2(float ax, float ay, float bx, float by) { float x = (float)(ax - bx), y = (float)(ay - by); return Sqrtf((float)((float)(x * x) + (float)(y * y))); }
    static int SignumIEx(float a, float eps) => a > eps ? 1 : a < -eps ? -1 : 0;
    static sbyte SignumEnum(float a) => a > 0f ? Convex : a == 0f ? Tangential : Concave;

    /// <summary>area_tri_signed_v2_alt_2x(v3, v2, v1) as span_tri_v2_sign(v1, v2, v3) calls it.</summary>
    static sbyte SpanTriSign(float[] co, int v1, int v2, int v3)
    {
        // d2 = v2 - v3... the call is area(v3, v2, v1): d2 = v2 - v3, d3 = v1 - v3; (d2.x * d3.y) - (d3.x * d2.y)
        float d2x = (float)(co[2 * v2] - co[2 * v3]), d2y = (float)(co[2 * v2 + 1] - co[2 * v3 + 1]);
        float d3x = (float)(co[2 * v1] - co[2 * v3]), d3y = (float)(co[2 * v1 + 1] - co[2 * v3 + 1]);
        return SignumEnum((float)((float)(d2x * d3y) - (float)(d3x * d2y)));
    }

    /// <summary>normalize_v3: the reciprocal of the length, multiplied in; zero below 1e-35. Returns the length.</summary>
    static float NormalizeV3(ref float x, ref float y, ref float z)
    {
        float d = (float)((float)((float)(x * x) + (float)(y * y)) + (float)(z * z));
        if (d > 1.0e-35f) { d = Sqrtf(d); float f = (float)(1.0f / d); x = (float)(x * f); y = (float)(y * f); z = (float)(z * f); }
        else { x = y = z = 0f; d = 0f; }
        return d;
    }

    /// <summary>ortho_basis_v3v3_v3 then the transpose: the rows n1, n2 of the projection (mul_v2_m3v3 reads them as columns).</summary>
    internal static void AxisDominantRows(float nx, float ny, float nz, float[] n1, float[] n2)
    {
        float f = (float)((float)(nx * nx) + (float)(ny * ny));
        if (f > 1.1920929e-7f)
        {
            float d = (float)(1.0f / Sqrtf(f));
            n1[0] = (float)(ny * d); n1[1] = (float)(-nx * d); n1[2] = 0f;
            n2[0] = (float)(-nz * n1[1]); n2[1] = (float)(nz * n1[0]); n2[2] = (float)((float)(nx * n1[1]) - (float)(ny * n1[0]));
        }
        else
        {
            n1[0] = nz < 0f ? -1f : 1f; n1[1] = n1[2] = n2[0] = n2[2] = 0f; n2[1] = 1f;
        }
    }

    /// <summary>mul_v2_m3v3 with the transposed basis: r = (n1 . co, n2 . co), each a sequential float32 sum.</summary>
    internal static void ProjectV2(float[] n1, float[] n2, float x, float y, float z, out float rx, out float ry)
    {
        rx = (float)((float)((float)(n1[0] * x) + (float)(n1[1] * y)) + (float)(n1[2] * z));
        ry = (float)((float)((float)(n2[0] * x) + (float)(n2[1] * y)) + (float)(n2[2] * z));
    }

    // ---------------------------------------------------------------- BLI_polyfill_calc (ear clipping over a 2D ring)

    sealed class PolyIndex { public int Next, Prev, Index; public sbyte Sign; }

    sealed class KdTree2D
    {
        public int[] Neg, Pos, Index, Parent; public bool[] Axis, Removed; public int Count, Root = Unset;
        public int[] NodesMap;   // coord index -> node, or Unset
        public float[] Co;       // the ring's coordinates (2 per index)
    }

    sealed class PolyFill
    {
        public float[] Co; public int CoordsNum, CoordsNumConcave; public PolyIndex[] Indices; public int First;   // First: pf->indices (the ring's head)
        public List<int[]> Tris = new List<int[]>(); public KdTree2D Tree;
    }

    static void CoordSignCalc(PolyFill pf, PolyIndex pi) => pi.Sign = SpanTriSign(pf.Co, pf.Indices[pi.Prev].Index, pi.Index, pf.Indices[pi.Next].Index);

    static float CrossPolyV2(float[] co, int n)
    {
        float cross = 0f; int prev = n - 1;
        for (int a = 0; a < n; a++) { cross = (float)(cross + (float)((float)(co[2 * prev] - co[2 * a]) * (float)(co[2 * a + 1] + co[2 * prev + 1]))); prev = a; }
        return cross;
    }

    /// <summary>BLI_polyfill_calc(coords, n, sign = 1, tris): the triangles as index triples, in the order the ears are cut.</summary>
    public static List<int[]> Calc(float[] co, int n, int coordsSign)
    {
        var pf = new PolyFill { Co = co, CoordsNum = n, Indices = new PolyIndex[n] };
        if (coordsSign == 0) coordsSign = CrossPolyV2(co, n) <= 0f ? 1 : -1;
        for (int i = 0; i < n; i++) pf.Indices[i] = new PolyIndex { Next = i + 1, Prev = i - 1, Index = coordsSign == 1 ? i : (n - 1) - i };
        pf.Indices[0].Prev = n - 1; pf.Indices[n - 1].Next = 0; pf.First = 0;
        for (int i = 0; i < n; i++) { CoordSignCalc(pf, pf.Indices[i]); if (pf.Indices[i].Sign != Convex) pf.CoordsNumConcave++; }
        if (pf.CoordsNumConcave > 0)
        {
            // kdtree2d_new/init/balance/init_mapping over the concave and tangential corners, in ring order
            var t = new KdTree2D { Count = pf.CoordsNumConcave, Co = co, NodesMap = new int[n] };
            for (int i = 0; i < n; i++) t.NodesMap[i] = Unset;
            t.Neg = new int[t.Count]; t.Pos = new int[t.Count]; t.Index = new int[t.Count]; t.Parent = new int[t.Count]; t.Axis = new bool[t.Count]; t.Removed = new bool[t.Count];
            int k = 0;
            for (int i = 0; i < n; i++) if (pf.Indices[i].Sign != Convex) { t.Neg[k] = t.Pos[k] = Unset; t.Index[k] = pf.Indices[i].Index; t.Axis[k] = false; k++; }
            t.Root = KdBalance(t, 0, t.Count, false);
            for (int i = 0; i < t.Count; i++)
            {
                if (t.Neg[i] != Unset) t.Parent[t.Neg[i]] = i;
                if (t.Pos[i] != Unset) t.Parent[t.Pos[i]] = i;
                t.NodesMap[t.Index[i]] = i;
            }
            t.Parent[t.Root] = Unset;
            pf.Tree = t;
        }
        Triangulate(pf);
        return pf.Tris;
    }

    static int KdBalance(KdTree2D t, int start, int num, bool axis)
    {
        if (num <= 0) return Unset;
        if (num == 1) return start;
        int neg = 0, pos = num - 1, median = num / 2; int ax = axis ? 1 : 0;
        while (pos > neg)
        {
            float co = t.Co[2 * t.Index[start + pos] + ax];
            int i = neg - 1, j = pos;
            while (true)
            {
                while (t.Co[2 * t.Index[start + (++i)] + ax] < co) { }
                while (t.Co[2 * t.Index[start + (--j)] + ax] > co && j > neg) { }
                if (i >= j) break;
                (t.Index[start + i], t.Index[start + j]) = (t.Index[start + j], t.Index[start + i]);   // the head: neg, pos (unset) and index
            }
            (t.Index[start + i], t.Index[start + pos]) = (t.Index[start + pos], t.Index[start + i]);
            if (i >= median) pos = i - 1;
            if (i <= median) neg = i + 1;
        }
        int node = start + median;
        t.Axis[node] = axis;
        t.Neg[node] = KdBalance(t, start, median, !axis);
        t.Pos[node] = KdBalance(t, start + median + 1, num - (median + 1), !axis);
        return node;
    }

    static void KdNodeRemove(KdTree2D t, int index)
    {
        int nodeIndex = t.NodesMap[index];
        if (nodeIndex == Unset) return;
        t.NodesMap[index] = Unset;
        int node = nodeIndex;
        t.Count -= 1;
        t.Removed[node] = true;
        while (t.Parent[node] != Unset)
        {
            int child;
            if (t.Neg[node] == Unset) child = t.Pos[node];
            else if (t.Pos[node] == Unset) child = t.Neg[node];
            else break;
            int parent = t.Parent[node];
            if (t.Neg[parent] == nodeIndex) t.Neg[parent] = child; else t.Pos[parent] = child;
            if (child != Unset) t.Parent[child] = parent;
            if (!t.Removed[parent]) break;
            nodeIndex = parent; node = parent;
        }
    }

    sealed class TriIsect { public float[] E0 = new float[2], E1 = new float[2], E2 = new float[2]; public float C0, C1, C2; }

    static TriIsect PrecomputeIsect(float[] co, int v0, int v1, int v2)
    {
        var t = new TriIsect();
        t.E0[0] = (float)(co[2 * v1] - co[2 * v0]); t.E0[1] = (float)(co[2 * v1 + 1] - co[2 * v0 + 1]);
        t.E1[0] = (float)(co[2 * v2] - co[2 * v1]); t.E1[1] = (float)(co[2 * v2 + 1] - co[2 * v1 + 1]);
        t.E2[0] = (float)(co[2 * v0] - co[2 * v2]); t.E2[1] = (float)(co[2 * v0 + 1] - co[2 * v2 + 1]);
        t.C0 = (float)((float)(t.E0[0] * co[2 * v0 + 1]) - (float)(co[2 * v0] * t.E0[1]));
        t.C1 = (float)((float)(t.E1[0] * co[2 * v1 + 1]) - (float)(co[2 * v1] * t.E1[1]));
        t.C2 = (float)((float)(t.E2[0] * co[2 * v2 + 1]) - (float)(co[2 * v2] * t.E2[1]));
        return t;
    }

    static bool IsectTest(TriIsect t, float x, float y)
    {
        return (float)((float)((float)(t.E1[1] * x) - (float)(t.E1[0] * y)) + t.C1) >= 0f
            && (float)((float)((float)(t.E2[1] * x) - (float)(t.E2[0] * y)) + t.C2) >= 0f
            && (float)((float)((float)(t.E0[1] * x) - (float)(t.E0[0] * y)) + t.C0) >= 0f;
    }

    static int KdIsectTriRecursive(KdTree2D t, int i0, int i1, int i2, TriIsect isect, float cx, float cy, float minX, float maxX, float minY, float maxY, int node)
    {
        float x = t.Co[2 * t.Index[node]], y = t.Co[2 * t.Index[node] + 1];
        if (!t.Removed[node] && IsectTest(isect, x, y) && t.Index[node] != i0 && t.Index[node] != i1 && t.Index[node] != i2) return t.Index[node];
        bool axisY = t.Axis[node]; float coAx = axisY ? y : x, cAx = axisY ? cy : cx, mn = axisY ? minY : minX, mx = axisY ? maxY : maxX;
        int Neg() => t.Neg[node] != Unset && coAx >= mn ? KdIsectTriRecursive(t, i0, i1, i2, isect, cx, cy, minX, maxX, minY, maxY, t.Neg[node]) : Unset;
        int Pos() => t.Pos[node] != Unset && coAx <= mx ? KdIsectTriRecursive(t, i0, i1, i2, isect, cx, cy, minX, maxX, minY, maxY, t.Pos[node]) : Unset;
        int result;
        if (cAx > coAx) { result = Pos(); if (result == Unset) result = Neg(); }
        else { result = Neg(); if (result == Unset) result = Pos(); }
        return result;
    }

    static int KdIsectTri(KdTree2D t, int i0, int i1, int i2)
    {
        float x0 = t.Co[2 * i0], y0 = t.Co[2 * i0 + 1], x1 = t.Co[2 * i1], y1 = t.Co[2 * i1 + 1], x2 = t.Co[2 * i2], y2 = t.Co[2 * i2 + 1];
        float minX = Math.Min(x0, Math.Min(x1, x2)), maxX = Math.Max(x0, Math.Max(x1, x2)), minY = Math.Min(y0, Math.Min(y1, y2)), maxY = Math.Max(y0, Math.Max(y1, y2));
        float cx = (float)((float)((float)(x0 + x1) + x2) * (1.0f / 3.0f)), cy = (float)((float)((float)(y0 + y1) + y2) * (1.0f / 3.0f));
        return KdIsectTriRecursive(t, i0, i1, i2, PrecomputeIsect(t.Co, i0, i1, i2), cx, cy, minX, maxX, minY, maxY, t.Root);
    }

    static bool EarTipCheck(PolyFill pf, PolyIndex ear, sbyte signAccept)
    {
        if (pf.CoordsNumConcave == 0) return true;
        if (ear.Sign != signAccept) return false;
        int hit = KdIsectTri(pf.Tree, ear.Index, pf.Indices[ear.Next].Index, pf.Indices[ear.Prev].Index);
        return hit == Unset;
    }

    static int EarTipFind(PolyFill pf, int earInit, bool reverse)
    {
        int n = pf.CoordsNum;
        for (sbyte signAccept = Convex; signAccept >= Tangential; signAccept--)
        {
            int ear = earInit;
            for (int i = n; i-- > 0;)
            {
                if (EarTipCheck(pf, pf.Indices[ear], signAccept)) return ear;
                ear = reverse ? pf.Indices[ear].Prev : pf.Indices[ear].Next;
            }
        }
        int e2 = earInit;
        for (int i = n; i-- > 0;)
        {
            if (pf.Indices[e2].Sign != Concave) return e2;
            e2 = pf.Indices[e2].Next;
        }
        return e2;
    }

    static void CoordRemove(PolyFill pf, int pi)
    {
        if (pf.Tree != null && pf.Tree.Count > 0) KdNodeRemove(pf.Tree, pf.Indices[pi].Index);
        var p = pf.Indices[pi];
        pf.Indices[p.Next].Prev = p.Prev; pf.Indices[p.Prev].Next = p.Next;
        if (pf.First == pi) pf.First = p.Next;
        pf.CoordsNum -= 1;
    }

    static void Triangulate(PolyFill pf)
    {
        int earInit = pf.First; bool reverse = false;
        while (pf.CoordsNum > 3)
        {
            int ear = EarTipFind(pf, earInit, reverse);
            var pe = pf.Indices[ear];
            if (pe.Sign != Convex) pf.CoordsNumConcave -= 1;
            int prev = pe.Prev, next = pe.Next;
            pf.Tris.Add(new[] { pf.Indices[prev].Index, pe.Index, pf.Indices[next].Index });
            CoordRemove(pf, ear);
            sbyte signPrev = pf.Indices[prev].Sign, signNext = pf.Indices[next].Sign;
            if (signPrev != Convex)
            {
                CoordSignCalc(pf, pf.Indices[prev]);
                if (pf.Indices[prev].Sign == Convex) { pf.CoordsNumConcave -= 1; if (pf.Tree != null) KdNodeRemove(pf.Tree, pf.Indices[prev].Index); }
            }
            if (signNext != Convex)
            {
                CoordSignCalc(pf, pf.Indices[next]);
                if (pf.Indices[next].Sign == Convex) { pf.CoordsNumConcave -= 1; if (pf.Tree != null) KdNodeRemove(pf.Tree, pf.Indices[next].Index); }
            }
            earInit = reverse ? pf.Indices[prev].Prev : pf.Indices[next].Next;
            if (pf.Indices[earInit].Sign != Convex)
            {
                earInit = reverse ? pf.Indices[earInit].Prev : pf.Indices[earInit].Next;
                reverse = !reverse;
            }
        }
        if (pf.CoordsNum == 3)
        {
            int a = pf.First, b = pf.Indices[a].Next, c = pf.Indices[b].Next;
            pf.Tris.Add(new[] { pf.Indices[a].Index, pf.Indices[b].Index, pf.Indices[c].Index });
        }
    }

    // ---------------------------------------------------------------- BLI_polyfill_beautify (edge rotation by the area/perimeter rule)

    /// <summary>BLI_polyfill_beautify_quad_rotate_calc_ex: negative when the (1-3) diagonal beats the (2-4) one; FLT_MAX when the quad
    /// cannot rotate, -FLT_MAX when it must.</summary>
    internal static float QuadRotateCalc(float[] co, int v1, int v2, int v3, int v4, bool lockDegenerate, out float area)
    {
        area = 0f;
        float x1 = co[2 * v1], y1 = co[2 * v1 + 1], x2 = co[2 * v2], y2 = co[2 * v2 + 1], x3 = co[2 * v3], y3 = co[2 * v3 + 1], x4 = co[2 * v4], y4 = co[2 * v4 + 1];
        return QuadRotateCalcXY(x1, y1, x2, y2, x3, y3, x4, y4, lockDegenerate, out area);
    }

    internal static float QuadRotateCalcXY(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4, bool lockDegenerate, out float area)
    {
        const float epsZeroArea = 1e-12f;
        float a234 = CrossTriV2(x2, y2, x3, y3, x4, y4), a241 = CrossTriV2(x2, y2, x4, y4, x1, y1), a123 = CrossTriV2(x1, y1, x2, y2, x3, y3), a134 = CrossTriV2(x1, y1, x3, y3, x4, y4);
        area = (float)((float)((float)((float)(Math.Abs(a234) + Math.Abs(a241)) + Math.Abs(a123)) + Math.Abs(a134)) / 8.0f);
        if ((a123 >= 0f) != (a134 >= 0f)) return float.MaxValue;
        if (Math.Abs(a123) <= epsZeroArea || Math.Abs(a134) <= epsZeroArea) return float.MaxValue;
        if ((a234 >= 0f) != (a241 >= 0f)) { if (lockDegenerate) return float.MaxValue; return -float.MaxValue; }
        if (Math.Abs(a234) <= epsZeroArea || Math.Abs(a241) <= epsZeroArea) return -float.MaxValue;
        float len12 = LenV2V2(x1, y1, x2, y2), len23 = LenV2V2(x2, y2, x3, y3), len34 = LenV2V2(x3, y3, x4, y4), len41 = LenV2V2(x4, y4, x1, y1);
        float len13 = LenV2V2(x1, y1, x3, y3), len24 = LenV2V2(x2, y2, x4, y4);
        float areaA = Math.Abs(a234), areaB = Math.Abs(a241);
        float primA = (float)((float)(len23 + len34) + len24), primB = (float)((float)(len41 + len12) + len24);
        float fac24 = (float)((float)(areaA / primA) + (float)(areaB / primB));
        areaA = Math.Abs(a123); areaB = Math.Abs(a134);
        primA = (float)((float)(len12 + len23) + len13); primB = (float)((float)(len34 + len41) + len13);
        float fac13 = (float)((float)(areaA / primA) + (float)(areaB / primB));
        return (float)(fac24 - fac13);
    }

    sealed class HalfEdge { public int V, ENext, ERadial = -1, BaseIndex = -1; }

    static float EdgeRotateBeautyCalc(float[] co, HalfEdge[] edges, HalfEdge ea, out float area)
    {
        var eb = edges[ea.ERadial];
        var eaOther = edges[edges[ea.ENext].ENext];
        var ebOther = edges[edges[eb.ENext].ENext];
        return QuadRotateCalc(co, eaOther.V, ea.V, ebOther.V, eb.V, false, out area);
    }

    /// <summary>BLI_polyfill_beautify(coords, n, tris): rotates the internal edges of the triangulation to the area/perimeter optimum,
    /// the triangles rewritten in half-edge order.</summary>
    public static void Beautify(float[] co, int n, List<int[]> tris)
    {
        int coordLast = n - 1, trisLen = n - 2, edgesLen = trisLen - 1, halfLen = 3 * trisLen;
        var half = new HalfEdge[halfLen];
        var order = new List<(int v0, int v1, int eHalf)>();
        for (int i = 0; i < trisLen; i++)
            for (int jCurr = 0, jPrev = 2; jCurr < 3; jPrev = jCurr++)
            {
                int ePrev = i * 3 + jPrev, eCurr = i * 3 + jCurr;
                half[ePrev] = new HalfEdge { V = tris[i][jPrev], ENext = eCurr };
                int a = tris[i][jPrev], b = tris[i][jCurr];
                if (a > b) (a, b) = (b, a);
                bool boundary = a + 1 == b || (a == 0 && b == coordLast);
                if (!boundary) order.Add((a, b, ePrev));
            }
        order.Sort((x, y) => x.v0 != y.v0 ? x.v0.CompareTo(y.v0) : x.v1 != y.v1 ? x.v1.CompareTo(y.v1) : x.eHalf.CompareTo(y.eHalf));
        for (int i = 0, baseIndex = 0; i < order.Count; baseIndex++)
        {
            var oa = order[i++]; var ob = order[i++];
            half[oa.eHalf].ERadial = ob.eHalf; half[ob.eHalf].ERadial = oa.eHalf;
            half[oa.eHalf].BaseIndex = baseIndex; half[ob.eHalf].BaseIndex = baseIndex;
        }
        var heap = new BlenderDecimate.Heap(Math.Max(1, edgesLen));
        var table = new int[Math.Max(1, edgesLen)]; for (int i = 0; i < table.Length; i++) table[i] = -1;
        for (int i = 0; i < halfLen; i++)
        {
            var e = half[i];
            if (e.ERadial >= 0 && e.ERadial < i)
            {
                float cost = EdgeRotateBeautyCalc(co, half, e, out _);
                if (cost < 0f) table[e.BaseIndex] = heap.Insert(cost, i); else table[e.BaseIndex] = -1;
            }
        }
        while (!heap.IsEmpty)
        {
            int ei = heap.PopMin(); var e = half[ei];
            table[e.BaseIndex] = -1;
            // polyedge_rotate
            int i0 = ei, i1 = half[i0].ENext, i2 = half[i1].ENext, i3 = e.ERadial, i4 = half[i3].ENext, i5 = half[i4].ENext;
            half[i0].ENext = i2; half[i1].ENext = i3; half[i2].ENext = i4; half[i3].ENext = i5; half[i4].ENext = i0; half[i5].ENext = i1;
            half[i0].V = half[i5].V; half[i3].V = half[i2].V;
            // polyedge_beauty_cost_update: the four surrounding edges
            int[] arr = { half[i0].ENext, half[half[i0].ENext].ENext, half[i3].ENext, half[half[i3].ENext].ENext };
            foreach (int ai in arr)
            {
                var ae = half[ai];
                if (ae.BaseIndex == -1) continue;
                float cost = EdgeRotateBeautyCalc(co, half, ae, out float area);
                if (cost < (float)(-1e-6f * Math.Max(area, 1.0f)))
                {
                    if (table[ae.BaseIndex] == -1) table[ae.BaseIndex] = heap.Insert(cost, ai); else heap.UpdatePtr(table[ae.BaseIndex], cost, ai);
                }
                else if (table[ae.BaseIndex] != -1) { heap.Remove(table[ae.BaseIndex]); table[ae.BaseIndex] = -1; }
            }
        }
        var used = new bool[halfLen]; int t = 0;
        for (int i = 0; i < halfLen; i++)
        {
            if (used[i]) continue;
            var e = half[i]; int a = i, b = e.ENext, c = half[b].ENext;
            tris[t][0] = half[a].V; tris[t][1] = half[b].V; tris[t][2] = half[c].V; t++;
            used[a] = used[b] = used[c] = true;
        }
    }

    // ---------------------------------------------------------------- the quad rule (bmesh_beautify.cc, math_geom.cc)

    static int IsQuadFlipV3(float[] P, int v1, int v2, int v3, int v4)
    {
        float[] D(int a, int b) => new[] { (float)(P[3 * a] - P[3 * b]), (float)(P[3 * a + 1] - P[3 * b + 1]), (float)(P[3 * a + 2] - P[3 * b + 2]) };
        float[] Cross(float[] a, float[] b) => new[] { (float)((float)(a[1] * b[2]) - (float)(a[2] * b[1])), (float)((float)(a[2] * b[0]) - (float)(a[0] * b[2])), (float)((float)(a[0] * b[1]) - (float)(a[1] * b[0])) };
        float Dot(float[] a, float[] b) => (float)((float)((float)(a[0] * b[0]) + (float)(a[1] * b[1])) + (float)(a[2] * b[2]));
        var d12 = D(v1, v2); var d23 = D(v2, v3); var d34 = D(v3, v4); var d41 = D(v4, v1);
        int ret = 0;
        if (Dot(Cross(d12, d23), Cross(d34, d41)) < 0f) ret |= 1;
        if (Dot(Cross(d23, d34), Cross(d41, d12)) < 0f) ret |= 2;
        return ret;
    }

    /// <summary>BLI_polyfill_edge_calc_rotate_beauty__area(v1..v4, lock_degenerate = false): both triangles projected onto the plane of
    /// their summed normals, then the quad rule.</summary>
    static float EdgeCalcRotateBeautyArea(float[] P, int v1, int v2, int v3, int v4)
    {
        float[] CrossTriV3(int a, int b, int c)
        {
            float n1x = (float)(P[3 * a] - P[3 * b]), n1y = (float)(P[3 * a + 1] - P[3 * b + 1]), n1z = (float)(P[3 * a + 2] - P[3 * b + 2]);
            float n2x = (float)(P[3 * b] - P[3 * c]), n2y = (float)(P[3 * b + 1] - P[3 * c + 1]), n2z = (float)(P[3 * b + 2] - P[3 * c + 2]);
            return new[] { (float)((float)(n1y * n2z) - (float)(n1z * n2y)), (float)((float)(n1z * n2x) - (float)(n1x * n2z)), (float)((float)(n1x * n2y) - (float)(n1y * n2x)) };
        }
        const float eps = 1e-5f;
        var noA = CrossTriV3(v2, v3, v4); var noB = CrossTriV3(v2, v4, v1);
        float nx = (float)(noA[0] + noB[0]), ny = (float)(noA[1] + noB[1]), nz = (float)(noA[2] + noB[2]);
        float noScale = NormalizeV3(ref nx, ref ny, ref nz);
        if (noScale == 0f) return float.MaxValue;
        var n1 = new float[3]; var n2 = new float[3]; AxisDominantRows(nx, ny, nz, n1, n2);
        ProjectV2(n1, n2, P[3 * v1], P[3 * v1 + 1], P[3 * v1 + 2], out float x1, out float y1);
        ProjectV2(n1, n2, P[3 * v2], P[3 * v2 + 1], P[3 * v2 + 2], out float x2, out float y2);
        ProjectV2(n1, n2, P[3 * v3], P[3 * v3 + 1], P[3 * v3 + 2], out float x3, out float y3);
        ProjectV2(n1, n2, P[3 * v4], P[3 * v4 + 1], P[3 * v4 + 2], out float x4, out float y4);
        if (SignumIEx((float)(CrossTriV2(x2, y2, x3, y3, x4, y4) / noScale), eps) + SignumIEx((float)(CrossTriV2(x2, y2, x4, y4, x1, y1) / noScale), eps) == 0) return float.MaxValue;
        return QuadRotateCalcXY(x1, y1, x2, y2, x3, y3, x4, y4, false, out _);
    }

    // ---------------------------------------------------------------- BM_face_triangulate and BM_mesh_triangulate

    /// <summary>BM_mesh_triangulate(bm, BEAUTY, BEAUTY, 4, tag_only) over the faces tagged - here every face, in slot order - with the
    /// Python operator's mapping variant (a double face stays). Face normals must be current (the n-gon's plane is its normal's).</summary>
    public static int TriangulateAll(BMesh bm, BlenderWeld.Layers layers)
    {
        int faceCount0 = bm.FaceCount, done = 0;
        for (int f = 0; f < faceCount0; f++)
        {
            if (!bm.FAlive[f] || bm.FLen[f] < 4) continue;
            TriangulateFace(bm, layers, f); done++;
        }
        return done;
    }

    public static void TriangulateFace(BMesh bm, BlenderWeld.Layers layers, int f)
    {
        int len = bm.FLen[f];
        var loops = new int[len]; var tris = new List<int[]>();
        if (len == 4)
        {
            int lFirst = bm.FLFirst[f];
            int lv1 = bm.LNext[lFirst], lv2 = bm.LNext[bm.LNext[lFirst]], lv3 = bm.LPrev[lFirst], lv4 = lFirst;
            int flip = IsQuadFlipV3(bm.VCo, bm.LV[lv1], bm.LV[lv2], bm.LV[lv3], bm.LV[lv4]);
            bool split24;
            if ((flip & 1) != 0) split24 = true;
            else if ((flip & 2) != 0) split24 = false;
            else
            {
                // BM_verts_calc_rotate_beauty(v1, v2, v3, v4, 0, 0): FLT_MAX when v1 == v3
                float cost = bm.LV[lv1] == bm.LV[lv3] ? float.MaxValue : EdgeCalcRotateBeautyArea(bm.VCo, bm.LV[lv1], bm.LV[lv2], bm.LV[lv3], bm.LV[lv4]);
                split24 = cost > 0f;
            }
            if (split24) lv1 = lv4; else lv2 = lv3;
            loops[0] = lv1; loops[1] = bm.LNext[lv1]; loops[2] = lv2; loops[3] = bm.LNext[lv2];
            tris.Add(new[] { 0, 1, 2 }); tris.Add(new[] { 0, 2, 3 });
        }
        else
        {
            var n1 = new float[3]; var n2 = new float[3];
            AxisDominantRows(-bm.FNo[3 * f], -bm.FNo[3 * f + 1], -bm.FNo[3 * f + 2], n1, n2);   // axis_dominant_v3_to_m3_negate
            var proj = new float[2 * len];
            int l = bm.FLFirst[f];
            for (int i = 0; i < len; i++)
            {
                loops[i] = l; int v = bm.LV[l];
                ProjectV2(n1, n2, bm.VCo[3 * v], bm.VCo[3 * v + 1], bm.VCo[3 * v + 2], out proj[2 * i], out proj[2 * i + 1]);
                l = bm.LNext[l];
            }
            tris = Calc(proj, len, 1);
            Beautify(proj, len, tris);
        }
        int totfilltri = len - 2, lastTri = len - 3; int fNew = BMesh.None;
        var vtri = new int[3]; var etri = new int[3]; var slots = new int[3];
        for (int i = 0; i < totfilltri; i++)
        {
            int l0 = loops[tris[i][0]], l1 = loops[tris[i][1]], l2 = loops[tris[i][2]];
            vtri[0] = bm.LV[l0]; vtri[1] = bm.LV[l1]; vtri[2] = bm.LV[l2];
            // BM_edges_from_verts_ensure: edge[i_prev] joins v[i_prev] and v[i], created (NO_DOUBLE) in the order (v2 v0), (v0 v1), (v1 v2)
            for (int k = 0, kPrev = 2; k < 3; kPrev = k++)
            {
                int e = bm.EdgeExists(vtri[kPrev], vtri[k]);
                if (e == BMesh.None) e = bm.EdgeCreate(vtri[kPrev], vtri[k]);
                etri[kPrev] = e;
            }
            fNew = bm.FaceCreate(vtri, etri, 3, f, slots);
            // a double: left in place by the operator's mapping variant (recorded, not killed)
            layers.CopyLoop(l0, slots[0]); layers.CopyLoop(l1, slots[1]); layers.CopyLoop(l2, slots[2]);
        }
        // the last triangle swapped into the original face's slot, its own slot killed
        bm.FaceSwapData(f, fNew);
        bm.FaceKill(fNew);
    }
}
