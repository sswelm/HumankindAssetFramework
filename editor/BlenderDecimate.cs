// BlenderDecimate.cs - Blender's Decimate modifier, COLLAPSE, as prep_model.py applies it per object (step 5 of replacing
// Blender, milestone c, 2026-10-03). Read at Blender 5.1.2 (ec6e62d4): bmesh_decimate_collapse.cc (the driver loop, the
// costs, the degenerate checks, the collapse, the custom-data walk), quadric.cc (double quadrics), BLI_heap.cc (the binary
// min-heap whose INSERTION order breaks equal costs), MOD_decimate.cc (face normals and vertex normals computed on
// conversion; `face_tot_target = totface * factor` as a float product truncated), bmesh_interp.cc and customdata.cc (how a
// UV, a byte colour and a vertex group blend across a collapsed edge), bmesh_mesh_convert.cc (what comes back out).
//   * Every float32 operation is written out with a cast, as Blender's C does it; the quadrics are double, their sums
//     elementwise, the optimum through the 3x3 inverse in the written order; the cost is the DOUBLE sum of two evaluations
//     rounded once to float and taken absolute; below 1e-12 the topology fallback (minus the dot of the vertex normals over
//     the squared length) takes over, so the vertex normals - Blender's own, interpolated and renormalized at each collapse -
//     decide the order on flat ground.
//   * The heap is BLI_heap: heap_up swaps PAST equal values (it stops only below a strictly smaller parent), heap_down
//     descends only into a strictly smaller child, a removal swaps the node to the root unconditionally and pops. Ties
//     between equal costs are therefore settled by the order edges were inserted and re-costed - the BMesh's disk and
//     LOOPS_OF_VERT order (BMesh.cs), which is why the port begins with the layout.
//   * Custom data: a loop layer with "math" (a UV float2, a corner byte colour) is blended around the kept vertex's fan when
//     the neighbouring corner's value EQUALS the collapsing corner's (a seam test: UVs within 1e-5 squared, colours within
//     0.001 squared); a vertex layer with "interp" (a vertex group, a point byte colour) is blended from the two vertices;
//     the custom-normal shorts have neither and ride along untouched (decoded later against the moved geometry).
// Proof: tools/decimate-drill (every registry mesh collapsed at the Lab's ratios, Blender's result against this, exact).
using System;
using System.Collections.Generic;

public static class BlenderDecimate
{
    public const float CostInvalid = float.MaxValue;              // COST_INVALID = FLT_MAX
    const double OptimizeEps = 1e-8;                              // OPTIMIZE_EPS
    const float BoundaryPreserveWeight = 100.0f;                  // BOUNDARY_PRESERVE_WEIGHT
    const float TopologyFallbackEps = 1e-12f;                     // TOPOLOGY_FALLBACK_EPS
    const float FltEpsilon = 1.1920929e-7f;                       // FLT_EPSILON

    // ---------------------------------------------------------------- the data a mesh carries through the collapse

    /// <summary>The custom data of the mesh being collapsed, indexed by BMesh element number (loops and vertices die but
    /// are never created, so the arrays stay valid). Layers in Blender's layer order.</summary>
    public sealed class MeshData
    {
        public List<float[]> Uv = new List<float[]>();            // CD_PROP_FLOAT2 per corner: 2 per loop
        public List<byte[]> CornerColor = new List<byte[]>();     // CD_PROP_BYTE_COLOR per corner: r, g, b, a per loop
        public short[] CustomNormal;                              // "custom_normal" int16_2 per corner (2 per loop), carried only
        public List<byte[]> PointColor = new List<byte[]>();      // CD_PROP_BYTE_COLOR per point: 4 per vertex
        public List<int>[] DefNr;                                 // MDeformVert per vertex: group numbers in dw order, or null when the mesh has no groups
        public List<float>[] DefWeight;

        public bool LoopHasMath => Uv.Count > 0 || CornerColor.Count > 0;      // CustomData_has_math(&bm->ldata)
        public bool VertHasInterp => DefNr != null || PointColor.Count > 0;   // CustomData_has_interp(&bm->vdata)
    }

    // ---------------------------------------------------------------- BLI_quadric (double)

    public struct Quadric
    {
        public double a2, ab, ac, ad, b2, bc, bd, c2, cd, d2;

        public static Quadric FromPlane(double v0, double v1, double v2, double v3)
        {
            var q = new Quadric();
            q.a2 = v0 * v0; q.b2 = v1 * v1; q.c2 = v2 * v2;
            q.ab = v0 * v1; q.ac = v0 * v2; q.bc = v1 * v2;
            q.ad = v0 * v3; q.bd = v1 * v3; q.cd = v2 * v3;
            q.d2 = v3 * v3;
            return q;
        }

        public void Add(in Quadric b)   // BLI_quadric_add_qu_qu: elementwise
        {
            a2 += b.a2; ab += b.ab; ac += b.ac; ad += b.ad; b2 += b.b2; bc += b.bc; bd += b.bd; c2 += b.c2; cd += b.cd; d2 += b.d2;
        }

        public static Quadric Sum(in Quadric a, in Quadric b)   // BLI_quadric_add_qu_ququ
        {
            var r = a; r.Add(b); return r;
        }

        public void Mul(double s)   // BLI_quadric_mul
        {
            a2 *= s; ab *= s; ac *= s; ad *= s; b2 *= s; bc *= s; bd *= s; c2 *= s; cd *= s; d2 *= s;
        }

        /// <summary>BLI_quadric_evaluate, term for term.</summary>
        public double Evaluate(double v0, double v1, double v2)
        {
            double v00 = v0 * v0, v01 = v0 * v1, v02 = v0 * v2;
            double v11 = v1 * v1, v12 = v1 * v2;
            double v22 = v2 * v2;
            return ((a2 * v00) + (ab * 2 * v01) + (ac * 2 * v02) + (ad * 2 * v0) +
                    (b2 * v11) + (bc * 2 * v12) + (bd * 2 * v1) +
                    (c2 * v22) + (cd * 2 * v2) +
                    (d2));
        }

        /// <summary>BLI_quadric_optimize: the inverse of the tensor (when its determinant beats epsilon) applied to the
        /// (ad, bd, cd) vector, negated - mul_m3_v3_db's column order kept.</summary>
        public bool Optimize(out double x, out double y, out double z, double epsilon)
        {
            double det = (a2 * (b2 * c2 - bc * bc) -
                          ab * (ab * c2 - ac * bc) +
                          ac * (ab * bc - ac * b2));
            if (Math.Abs(det) > epsilon)
            {
                double invdet = 1.0 / det;
                double m00 = (b2 * c2 - bc * bc) * invdet;
                double m10 = (bc * ac - ab * c2) * invdet;
                double m20 = (ab * bc - b2 * ac) * invdet;
                double m01 = (ac * bc - ab * c2) * invdet;
                double m11 = (a2 * c2 - ac * ac) * invdet;
                double m21 = (ab * ac - a2 * bc) * invdet;
                double m02 = (ab * bc - ac * b2) * invdet;
                double m12 = (ac * ab - a2 * bc) * invdet;
                double m22 = (a2 * b2 - ab * ab) * invdet;
                double t0 = ad, t1 = bd, t2 = cd;   // BLI_quadric_to_vector_v3
                // mul_v3_m3v3_db: r[i] = M[0][i] t0 + M[1][i] t1 + M[2][i] t2
                x = -(m00 * t0 + m10 * t1 + m20 * t2);
                y = -(m01 * t0 + m11 * t1 + m21 * t2);
                z = -(m02 * t0 + m12 * t1 + m22 * t2);
                return true;
            }
            x = y = z = 0;
            return false;
        }
    }

    // ---------------------------------------------------------------- BLI_heap

    /// <summary>BLI_heap: a binary min-heap over float values with removable nodes. Node numbers stand for HeapNode
    /// pointers (recycled last-freed-first, as the chunk free list does); the tree holds node numbers.</summary>
    public sealed class Heap
    {
        float[] nodeValue; int[] nodeIndex; int[] nodePtr;   // nodePtr doubles as the free list's next
        int nodeCount, freeHead = -1;
        int[] tree; int size;

        public Heap(int reserve)
        {
            int cap = Math.Max(1, reserve);
            tree = new int[cap]; nodeValue = new float[cap]; nodeIndex = new int[cap]; nodePtr = new int[cap];
        }

        public bool IsEmpty => size == 0;
        public int Count => size;
        public float TopValue => nodeValue[tree[0]];
        public float NodeValue(int node) => nodeValue[node];
        public int NodePtr(int node) => nodePtr[node];

        void Swap(int i, int j)
        {
            int pi = tree[i], pj = tree[j];
            nodeIndex[pi] = j; tree[j] = pi;
            nodeIndex[pj] = i; tree[i] = pj;
        }

        void Down(int i)
        {
            while (true)
            {
                int l = (i << 1) + 1, r = (i << 1) + 2, smallest = i;
                if (l < size && nodeValue[tree[l]] < nodeValue[tree[smallest]]) smallest = l;
                if (r < size && nodeValue[tree[r]] < nodeValue[tree[smallest]]) smallest = r;
                if (smallest == i) break;
                Swap(i, smallest);
                i = smallest;
            }
        }

        void Up(int i)
        {
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (nodeValue[tree[p]] < nodeValue[tree[i]]) break;   // equal values keep climbing
                Swap(p, i);
                i = p;
            }
        }

        int AllocNode()
        {
            if (freeHead != -1) { int n = freeHead; freeHead = nodePtr[n]; return n; }
            if (nodeCount == nodeValue.Length)
            {
                int cap = nodeValue.Length * 2;
                Array.Resize(ref nodeValue, cap); Array.Resize(ref nodeIndex, cap); Array.Resize(ref nodePtr, cap);
            }
            return nodeCount++;
        }

        void FreeNode(int n) { nodePtr[n] = freeHead; freeHead = n; }

        public int Insert(float value, int ptr)
        {
            if (size >= tree.Length) Array.Resize(ref tree, tree.Length * 2);
            int n = AllocNode();
            nodePtr[n] = ptr; nodeValue[n] = value; nodeIndex[n] = size;
            tree[size] = n; size++;
            Up(nodeIndex[n]);
            return n;
        }

        public void InsertOrUpdate(ref int node, float value, int ptr)
        {
            if (node == -1) node = Insert(value, ptr);
            else UpdatePtr(node, value, ptr);
        }

        public int PopMin()
        {
            int ptr = nodePtr[tree[0]];
            FreeNode(tree[0]);
            if (--size > 0) { Swap(0, size); Down(0); }
            return ptr;
        }

        public void Remove(int node)
        {
            int i = nodeIndex[node];
            while (i > 0) { int p = (i - 1) >> 1; Swap(p, i); i = p; }
            PopMin();
        }

        public void UpdatePtr(int node, float value, int ptr)
        {
            nodePtr[node] = ptr;
            if (value < nodeValue[node]) { nodeValue[node] = value; Up(nodeIndex[node]); }
            else if (value > nodeValue[node]) { nodeValue[node] = value; Down(nodeIndex[node]); }
        }
    }

    // ---------------------------------------------------------------- float helpers (BLI_math, written out)

    static float Sqrtf(float d) => (float)Math.Sqrt((double)d);
    static float Dot(float ax, float ay, float az, float bx, float by, float bz) => (float)((float)((float)(ax * bx) + (float)(ay * by)) + (float)(az * bz));
    static float LenSquared3(float x, float y, float z) => Dot(x, y, z, x, y, z);

    /// <summary>normalize_v3: multiply by the reciprocal length (not a division per component); zero below 1e-35.</summary>
    static float NormalizeC(ref float x, ref float y, ref float z)
    {
        float d = Dot(x, y, z, x, y, z);
        if (d > 1.0e-35f)
        {
            d = Sqrtf(d);
            float f = (float)(1.0f / d);
            x = (float)(x * f); y = (float)(y * f); z = (float)(z * f);
        }
        else { x = y = z = 0f; d = 0f; }
        return d;
    }

    /// <summary>normal_tri_v3: (v1 - v2) x (v2 - v3), normalized.</summary>
    public static void NormalTri(float[] P, int v1, int v2, int v3, out float nx, out float ny, out float nz)
    {
        float n1x = (float)(P[3 * v1] - P[3 * v2]), n1y = (float)(P[3 * v1 + 1] - P[3 * v2 + 1]), n1z = (float)(P[3 * v1 + 2] - P[3 * v2 + 2]);
        float n2x = (float)(P[3 * v2] - P[3 * v3]), n2y = (float)(P[3 * v2 + 1] - P[3 * v3 + 1]), n2z = (float)(P[3 * v2 + 2] - P[3 * v3 + 2]);
        nx = (float)((float)(n1y * n2z) - (float)(n1z * n2y));
        ny = (float)((float)(n1z * n2x) - (float)(n1x * n2z));
        nz = (float)((float)(n1x * n2y) - (float)(n1y * n2x));
        NormalizeC(ref nx, ref ny, ref nz);
    }

    /// <summary>BM_face_normal_update on every face (triangles: normal_tri_v3 of the loops in order).</summary>
    public static void FaceNormalsUpdate(BMesh bm)
    {
        for (int f = 0; f < bm.FaceCount; f++)
        {
            if (!bm.FAlive[f]) continue;
            int l0 = bm.FLFirst[f], l1 = bm.LNext[l0], l2 = bm.LNext[l1];
            if (bm.FLen[f] != 3) throw new InvalidOperationException("BlenderDecimate: a face that is not a triangle (the importer makes none)");
            NormalTri(bm.VCo, bm.LV[l0], bm.LV[l1], bm.LV[l2], out float nx, out float ny, out float nz);
            bm.FNo[3 * f] = nx; bm.FNo[3 * f + 1] = ny; bm.FNo[3 * f + 2] = nz;
        }
    }

    // ---------------------------------------------------------------- the quadrics and the costs

    static void BuildQuadrics(BMesh bm, Quadric[] vq)
    {
        for (int f = 0; f < bm.FaceCount; f++)
        {
            if (!bm.FAlive[f]) continue;
            // BM_face_calc_center_median: the loops' positions summed in order, times 1 / len
            float cx = 0f, cy = 0f, cz = 0f;
            int lFirst = bm.FLFirst[f], l = lFirst;
            do { int v = bm.LV[l]; cx = (float)(cx + bm.VCo[3 * v]); cy = (float)(cy + bm.VCo[3 * v + 1]); cz = (float)(cz + bm.VCo[3 * v + 2]); } while ((l = bm.LNext[l]) != lFirst);
            float inv = (float)(1.0f / (float)bm.FLen[f]);
            cx = (float)(cx * inv); cy = (float)(cy * inv); cz = (float)(cz * inv);
            double p0 = bm.FNo[3 * f], p1 = bm.FNo[3 * f + 1], p2 = bm.FNo[3 * f + 2];
            double p3 = -(p0 * (double)cx + p1 * (double)cy + p2 * (double)cz);   // -dot_v3db_v3fl
            var q = Quadric.FromPlane(p0, p1, p2, p3);
            l = lFirst;
            do { vq[bm.LV[l]].Add(q); } while ((l = bm.LNext[l]) != lFirst);
        }
        for (int e = 0; e < bm.EdgeCount; e++)
        {
            if (!bm.EAlive[e] || !bm.EdgeIsBoundary(e)) continue;
            int v1 = bm.EV1[e], v2 = bm.EV2[e];
            float evx = (float)(bm.VCo[3 * v2] - bm.VCo[3 * v1]), evy = (float)(bm.VCo[3 * v2 + 1] - bm.VCo[3 * v1 + 1]), evz = (float)(bm.VCo[3 * v2 + 2] - bm.VCo[3 * v1 + 2]);
            int f = bm.LF[bm.EL[e]];
            float fnx = bm.FNo[3 * f], fny = bm.FNo[3 * f + 1], fnz = bm.FNo[3 * f + 2];
            // cross_v3_v3v3(edge_plane, edge_vector, f->no)
            float epx = (float)((float)(evy * fnz) - (float)(evz * fny));
            float epy = (float)((float)(evz * fnx) - (float)(evx * fnz));
            float epz = (float)((float)(evx * fny) - (float)(evy * fnx));
            double d0 = epx, d1 = epy, d2 = epz;
            // normalize_v3_db
            double d = d0 * d0 + d1 * d1 + d2 * d2;
            if (d > 1.0e-35) { d = Math.Sqrt(d); double mul = 1.0 / d; d0 *= mul; d1 *= mul; d2 *= mul; }
            else { d0 = d1 = d2 = 0; d = 0.0; }
            if (d > (double)FltEpsilon)
            {
                // mid_v3_v3v3: 0.5f * (a + b)
                float cx = (float)(0.5f * (float)(bm.VCo[3 * v1] + bm.VCo[3 * v2]));
                float cy = (float)(0.5f * (float)(bm.VCo[3 * v1 + 1] + bm.VCo[3 * v2 + 1]));
                float cz = (float)(0.5f * (float)(bm.VCo[3 * v1 + 2] + bm.VCo[3 * v2 + 2]));
                double d3 = -(d0 * (double)cx + d1 * (double)cy + d2 * (double)cz);
                var q = Quadric.FromPlane(d0, d1, d2, d3);
                q.Mul((double)BoundaryPreserveWeight);
                vq[v1].Add(q);
                vq[v2].Add(q);
            }
        }
    }

    /// <summary>bm_decim_calc_target_co_db: the optimum of the summed quadrics, else the midpoint in double.</summary>
    static void CalcTargetCoDb(BMesh bm, int e, Quadric[] vq, out double x, out double y, out double z)
    {
        var q = Quadric.Sum(vq[bm.EV1[e]], vq[bm.EV2[e]]);
        if (q.Optimize(out x, out y, out z, OptimizeEps)) return;
        int v1 = bm.EV1[e], v2 = bm.EV2[e];
        x = 0.5 * ((double)bm.VCo[3 * v1] + (double)bm.VCo[3 * v2]);
        y = 0.5 * ((double)bm.VCo[3 * v1 + 1] + (double)bm.VCo[3 * v2 + 1]);
        z = 0.5 * ((double)bm.VCo[3 * v1 + 2] + (double)bm.VCo[3 * v2 + 2]);
    }

    /// <summary>bm_decim_build_edge_cost_single_squared__topology (no vertex weights).</summary>
    static float TopologyCostSquared(BMesh bm, int e)
    {
        int v1 = bm.EV1[e], v2 = bm.EV2[e];
        float dot = Dot(bm.VNo[3 * v1], bm.VNo[3 * v1 + 1], bm.VNo[3 * v1 + 2], bm.VNo[3 * v2], bm.VNo[3 * v2 + 1], bm.VNo[3 * v2 + 2]);
        float dx = (float)(bm.VCo[3 * v2] - bm.VCo[3 * v1]), dy = (float)(bm.VCo[3 * v2 + 1] - bm.VCo[3 * v1 + 1]), dz = (float)(bm.VCo[3 * v2 + 2] - bm.VCo[3 * v1 + 2]);
        float negLen = -LenSquared3(dx, dy, dz);
        float denom = negLen < -FltEpsilon ? negLen : -FltEpsilon;   // min_ff
        return (float)(Math.Abs(dot) / denom);
    }

    /// <summary>bm_decim_build_edge_cost_single: only an edge between one or two triangles gets a cost; the cost is the
    /// double sum of both quadrics at the optimum, rounded to float, absolute; under 1e-12 the topology fallback.</summary>
    static void BuildEdgeCostSingle(BMesh bm, int e, Quadric[] vq, Heap heap, int[] table)
    {
        bool ok;
        if (bm.EdgeIsBoundary(e)) ok = bm.FLen[bm.LF[bm.EL[e]]] == 3;
        else if (bm.EdgeIsManifold(e)) ok = bm.FLen[bm.LF[bm.EL[e]]] == 3 && bm.FLen[bm.LF[bm.LRadialNext[bm.EL[e]]]] == 3;
        else ok = false;
        if (!ok)
        {
            if (table[e] != -1) heap.Remove(table[e]);
            table[e] = -1;
            return;
        }
        CalcTargetCoDb(bm, e, vq, out double ox, out double oy, out double oz);
        float cost = (float)(vq[bm.EV1[e]].Evaluate(ox, oy, oz) + vq[bm.EV2[e]].Evaluate(ox, oy, oz));
        cost = Math.Abs(cost);
        if (cost < TopologyFallbackEps) cost = (float)(TopologyCostSquared(bm, e) - cost);
        heap.InsertOrUpdate(ref table[e], cost, e);
    }

    static void InvalidEdgeCostSingle(BMesh bm, int e, Heap heap, int[] table)
    {
        if (table[e] != -1) throw new InvalidOperationException("bm_decim_invalid_edge_cost_single: the edge is still in the heap");
        table[e] = heap.Insert(CostInvalid, e);
    }

    // ---------------------------------------------------------------- the degenerate checks

    static void EdgeTag(BMesh bm, int e, bool enable)
    {
        void Set(byte[] flags, int i) { if (enable) flags[i] |= BMesh.FlagTag; else flags[i] &= unchecked((byte)~BMesh.FlagTag); }
        Set(bm.VFlag, bm.EV1[e]); Set(bm.VFlag, bm.EV2[e]);
        int l = bm.EL[e];
        if (l != BMesh.None)
        {
            Set(bm.FFlag, bm.LF[l]);
            if (l != bm.LRadialNext[l]) Set(bm.FFlag, bm.LF[bm.LRadialNext[l]]);
        }
    }

    static bool EdgeTagTest(BMesh bm, int e)
    {
        if ((bm.VFlag[bm.EV1[e]] & BMesh.FlagTag) != 0 || (bm.VFlag[bm.EV2[e]] & BMesh.FlagTag) != 0) return true;
        int l = bm.EL[e];
        return l != BMesh.None && ((bm.FFlag[bm.LF[l]] & BMesh.FlagTag) != 0 || (l != bm.LRadialNext[l] && (bm.FFlag[bm.LF[bm.LRadialNext[l]]] & BMesh.FlagTag) != 0));
    }

    static bool ManifoldOrBoundary(BMesh bm, int l) => l != BMesh.None && bm.LRadialNext[bm.LRadialNext[l]] == l;

    /// <summary>bm_edge_collapse_is_degenerate_topology: would the two vertices' fans overlap beyond the edge's own faces?</summary>
    static bool IsDegenerateTopology(BMesh bm, int eFirst)
    {
        int v1 = bm.EV1[eFirst], v2 = bm.EV2[eFirst];
        int eIter = eFirst;
        do { if (!ManifoldOrBoundary(bm, bm.EL[eIter])) return true; EdgeTag(bm, eIter, false); } while ((eIter = bm.DiskEdgeNext(eIter, v1)) != eFirst);
        eIter = eFirst;
        do { if (!ManifoldOrBoundary(bm, bm.EL[eIter])) return true; EdgeTag(bm, eIter, false); } while ((eIter = bm.DiskEdgeNext(eIter, v2)) != eFirst);
        eIter = eFirst;
        do { EdgeTag(bm, eIter, true); } while ((eIter = bm.DiskEdgeNext(eIter, v1)) != eFirst);
        {
            int lRadial = bm.EL[eFirst];
            int lFace = lRadial;
            bm.FFlag[bm.LF[lFace]] &= unchecked((byte)~BMesh.FlagTag);
            bm.VFlag[bm.LV[lFace = lRadial]] &= unchecked((byte)~BMesh.FlagTag);
            bm.VFlag[bm.LV[lFace = bm.LNext[lFace]]] &= unchecked((byte)~BMesh.FlagTag);
            bm.VFlag[bm.LV[bm.LNext[lFace]]] &= unchecked((byte)~BMesh.FlagTag);
            lFace = bm.LRadialNext[lRadial];
            if (lRadial != lFace)
            {
                bm.FFlag[bm.LF[lFace]] &= unchecked((byte)~BMesh.FlagTag);
                bm.VFlag[bm.LV[lFace = bm.LRadialNext[lRadial]]] &= unchecked((byte)~BMesh.FlagTag);
                bm.VFlag[bm.LV[lFace = bm.LNext[lFace]]] &= unchecked((byte)~BMesh.FlagTag);
                bm.VFlag[bm.LV[bm.LNext[lFace]]] &= unchecked((byte)~BMesh.FlagTag);
            }
        }
        eIter = eFirst;
        do { if (EdgeTagTest(bm, eIter)) return true; } while ((eIter = bm.DiskEdgeNext(eIter, v2)) != eFirst);
        return false;
    }

    /// <summary>bm_edge_collapse_is_degenerate_flip: would moving either vertex to the optimum turn a face over?</summary>
    static bool IsDegenerateFlip(BMesh bm, int e, float ox, float oy, float oz, List<int> loops)
    {
        for (int i = 0; i < 2; i++)
        {
            int v = i == 0 ? bm.EV1[e] : bm.EV2[e];
            bm.LoopsOfVert(v, loops);
            foreach (int l in loops)
            {
                if (bm.LE[l] == e || bm.LE[bm.LPrev[l]] == e) continue;
                int vp = bm.LV[bm.LPrev[l]], vn = bm.LV[bm.LNext[l]];
                float px = bm.VCo[3 * vp], py = bm.VCo[3 * vp + 1], pz = bm.VCo[3 * vp + 2];
                float otx = (float)(px - bm.VCo[3 * vn]), oty = (float)(py - bm.VCo[3 * vn + 1]), otz = (float)(pz - bm.VCo[3 * vn + 2]);
                float exx = (float)(px - bm.VCo[3 * v]), exy = (float)(py - bm.VCo[3 * v + 1]), exz = (float)(pz - bm.VCo[3 * v + 2]);
                float opx = (float)(px - ox), opy = (float)(py - oy), opz = (float)(pz - oz);
                float cex = (float)((float)(oty * exz) - (float)(otz * exy)), cey = (float)((float)(otz * exx) - (float)(otx * exz)), cez = (float)((float)(otx * exy) - (float)(oty * exx));
                float cox = (float)((float)(oty * opz) - (float)(otz * opy)), coy = (float)((float)(otz * opx) - (float)(otx * opz)), coz = (float)((float)(otx * opy) - (float)(oty * opx));
                float lhs = Dot(cex, cey, cez, cox, coy, coz);
                float rhs = (float)((float)(LenSquared3(cex, cey, cez) + LenSquared3(cox, coy, coz)) * 0.01f);
                if (lhs <= rhs) return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- the custom data (customdata.cc, bmesh_interp.cc)

    static bool UvEqual(float[] uv, int a, int b)
    {
        float dx = (float)(uv[2 * a] - uv[2 * b]), dy = (float)(uv[2 * a + 1] - uv[2 * b + 1]);
        return (float)((float)(dx * dx) + (float)(dy * dy)) < 0.00001f;   // layerEqual_propfloat2
    }

    static void UvInterp(float[] uv, int src0, int src1, float w0, float w1, int dst)   // layerInterp_propfloat2, two sources
    {
        float rx = 0f, ry = 0f;
        rx = (float)(rx + (float)(uv[2 * src0] * w0)); ry = (float)(ry + (float)(uv[2 * src0 + 1] * w0));
        rx = (float)(rx + (float)(uv[2 * src1] * w1)); ry = (float)(ry + (float)(uv[2 * src1 + 1] * w1));
        uv[2 * dst] = rx; uv[2 * dst + 1] = ry;
    }

    static bool ColorEqual(byte[] c, int a, int b)   // layerEqual_mloopcol
    {
        float r = c[4 * a] - c[4 * b], g = c[4 * a + 1] - c[4 * b + 1], bb = c[4 * a + 2] - c[4 * b + 2], al = c[4 * a + 3] - c[4 * b + 3];
        return (float)((float)((float)((float)(r * r) + (float)(g * g)) + (float)(bb * bb)) + (float)(al * al)) < 0.001f;
    }

    static byte RoundToUcharClamp(float a)   // round_fl_to_uchar_clamp
    {
        float r = (float)Math.Floor((double)(float)(a + 0.5f));
        if (r <= 0f) return 0;
        if (r >= 255f) return 255;
        return (byte)r;
    }

    static void ColorInterp(byte[] c, int src0, int src1, float w0, float w1, int dst)   // layerInterp_mloopcol, two sources
    {
        float r = 0f, g = 0f, b = 0f, a = 0f;
        r = (float)(r + (float)(c[4 * src0] * w0)); g = (float)(g + (float)(c[4 * src0 + 1] * w0)); b = (float)(b + (float)(c[4 * src0 + 2] * w0)); a = (float)(a + (float)(c[4 * src0 + 3] * w0));
        r = (float)(r + (float)(c[4 * src1] * w1)); g = (float)(g + (float)(c[4 * src1 + 1] * w1)); b = (float)(b + (float)(c[4 * src1 + 2] * w1)); a = (float)(a + (float)(c[4 * src1 + 3] * w1));
        c[4 * dst] = RoundToUcharClamp(r); c[4 * dst + 1] = RoundToUcharClamp(g); c[4 * dst + 2] = RoundToUcharClamp(b); c[4 * dst + 3] = RoundToUcharClamp(a);
    }

    /// <summary>layerInterp_mdeformvert over two sources: each weight scaled, zeros dropped, a group seen again summed, a new
    /// group PREPENDED (so the result lists groups in reverse order of first appearance), each clamped to 1.</summary>
    static void DeformInterp(MeshData d, int src0, int src1, float w0, float w1, int dst)
    {
        var nrs = new List<int>(); var ws = new List<float>();   // the linked list, head first
        void Take(int src, float iw)
        {
            var sn = d.DefNr[src]; var sw = d.DefWeight[src];
            if (sn == null) return;
            for (int j = 0; j < sn.Count; j++)
            {
                float weight = (float)(sw[j] * iw);
                if (weight == 0.0f) continue;
                int k = nrs.IndexOf(sn[j]);
                if (k >= 0) ws[k] = (float)(ws[k] + weight);
                else { nrs.Insert(0, sn[j]); ws.Insert(0, weight); }
            }
        }
        Take(src0, w0); Take(src1, w1);
        for (int i = 0; i < ws.Count; i++) ws[i] = Math.Min(ws[i], 1.0f);
        d.DefNr[dst] = nrs; d.DefWeight[dst] = ws;
    }

    /// <summary>bm_data_interp_from_elem on the vertex block: a factor at or below 0 keeps the destination's own data, at or
    /// above 1 copies the other vertex's, in between every interpolating layer blends with weights (1 - fac, fac).</summary>
    static void InterpFromVerts(MeshData d, int vSrc1, int vSrc2, int vDst, float fac)
    {
        if (fac <= 0.0f)
        {
            if (vSrc1 != vDst) CopyVertData(d, vSrc1, vDst);
        }
        else if (fac >= 1.0f)
        {
            if (vSrc2 != vDst) CopyVertData(d, vSrc2, vDst);
        }
        else
        {
            float w0 = (float)(1.0f - fac), w1 = fac;
            if (d.DefNr != null) DeformInterp(d, vSrc1, vSrc2, w0, w1, vDst);
            foreach (var c in d.PointColor) ColorInterp(c, vSrc1, vSrc2, w0, w1, vDst);
        }
    }

    static void CopyVertData(MeshData d, int src, int dst)
    {
        if (d.DefNr != null) { d.DefNr[dst] = d.DefNr[src] == null ? null : new List<int>(d.DefNr[src]); d.DefWeight[dst] = d.DefWeight[src] == null ? null : new List<float>(d.DefWeight[src]); }
        foreach (var c in d.PointColor) for (int k = 0; k < 4; k++) c[4 * dst + k] = c[4 * src + k];
    }

    /// <summary>bm_edge_collapse_loop_customdata: from the face of l, walk the fan around the collapsing vertex (then the kept
    /// one), stopping at the opposite face of a manifold edge; every corner whose value equals the collapsing corner's is
    /// blended between the two corners of l's face.</summary>
    static void CollapseLoopCustomData(BMesh bm, MeshData d, int l, int vClear, int vOther, float fac)
    {
        bool isManifold = bm.EdgeIsManifold(bm.LE[l]);
        int lClear, lOther;
        if (bm.LV[l] == vClear) { lClear = l; lOther = bm.LNext[l]; }
        else { lClear = bm.LNext[l]; lOther = l; }
        for (int side = 0; side < 2; side++)
        {
            int fExit = isManifold ? bm.LF[bm.LRadialNext[l]] : BMesh.None;
            int ePrev = bm.LE[l];
            int lFirst, src0, src1; float w0, w1;
            if (side == 0) { lFirst = lClear; src0 = lClear; src1 = lOther; w0 = fac; w1 = (float)(1.0f - fac); }
            else { lFirst = lOther; src0 = lOther; src1 = lClear; w0 = (float)(1.0f - fac); w1 = fac; }
            int lIter = lFirst;
            while ((lIter = bm.VertStepFanLoop(lIter, ref ePrev)) != lFirst && lIter != BMesh.None)
            {
                if (fExit != BMesh.None && fExit == bm.LF[lIter]) break;
                foreach (var uv in d.Uv) if (UvEqual(uv, src0, lIter)) UvInterp(uv, src0, src1, w0, w1, lIter);
                foreach (var c in d.CornerColor) if (ColorEqual(c, src0, lIter)) ColorInterp(c, src0, src1, w0, w1, lIter);
            }
        }
    }

    // ---------------------------------------------------------------- the collapse

    /// <summary>bm_edge_collapse: the limited collapse of an edge between one or two triangles - the custom data first, then
    /// the edge dies, the vertex is spliced, the doubled edges are spliced. rEClearOther: the two (or one) edges that vanish
    /// besides the collapsed one, -1 for none.</summary>
    static bool EdgeCollapse(BMesh bm, MeshData d, int eClear, int vClear, int[] rEClearOther, float fac)
    {
        int vOther = bm.EdgeOtherVert(eClear, vClear);
        if (bm.EdgeIsManifold(eClear))
        {
            bm.EdgeLoopPair(eClear, out int la, out int lb);
            int ea0, ea1, eb0, eb1;
            if (bm.VertInEdge(bm.LE[bm.LPrev[la]], vClear)) { ea0 = bm.LE[bm.LPrev[la]]; ea1 = bm.LE[bm.LNext[la]]; }
            else { ea1 = bm.LE[bm.LPrev[la]]; ea0 = bm.LE[bm.LNext[la]]; }
            if (bm.VertInEdge(bm.LE[bm.LPrev[lb]], vClear)) { eb0 = bm.LE[bm.LPrev[lb]]; eb1 = bm.LE[bm.LNext[lb]]; }
            else { eb1 = bm.LE[bm.LPrev[lb]]; eb0 = bm.LE[bm.LNext[lb]]; }
            if (ea0 == eb0 || ea0 == eb1 || ea1 == eb0 || ea1 == eb1) return false;
            rEClearOther[0] = ea0; rEClearOther[1] = eb0;
            if (d.VertHasInterp) InterpFromVerts(d, vOther, vClear, vOther, fac);
            if (d.LoopHasMath)
            {
                CollapseLoopCustomData(bm, d, bm.EL[eClear], vClear, vOther, fac);
                CollapseLoopCustomData(bm, d, bm.LRadialNext[bm.EL[eClear]], vClear, vOther, fac);
            }
            bm.EdgeKill(eClear);
            bm.VFlag[vOther] |= bm.VFlag[vClear];
            bm.VertSplice(vOther, vClear);
            bm.EFlag[ea1] |= bm.EFlag[ea0];
            bm.EFlag[eb1] |= bm.EFlag[eb0];
            bm.EdgeSplice(ea1, ea0);
            bm.EdgeSplice(eb1, eb0);
            return true;
        }
        if (bm.EdgeIsBoundary(eClear))
        {
            int la = bm.EL[eClear];
            int ea0, ea1;
            if (bm.VertInEdge(bm.LE[bm.LPrev[la]], vClear)) { ea0 = bm.LE[bm.LPrev[la]]; ea1 = bm.LE[bm.LNext[la]]; }
            else { ea1 = bm.LE[bm.LPrev[la]]; ea0 = bm.LE[bm.LNext[la]]; }
            rEClearOther[0] = ea0; rEClearOther[1] = -1;
            if (d.VertHasInterp) InterpFromVerts(d, vOther, vClear, vOther, fac);
            if (d.LoopHasMath) CollapseLoopCustomData(bm, d, bm.EL[eClear], vClear, vOther, fac);
            bm.EdgeKill(eClear);
            bm.VFlag[vOther] |= bm.VFlag[vClear];
            bm.VertSplice(vOther, vClear);
            bm.EFlag[ea1] |= bm.EFlag[ea0];
            bm.EdgeSplice(ea1, ea0);
            return true;
        }
        return false;
    }

    /// <summary>bm_decim_edge_collapse: e->v2 collapses into e->v1 at the quadric optimum unless the result would be
    /// degenerate (then the edge goes back with an invalid cost); afterwards the kept vertex's quadric, normal and the costs
    /// of its edges and of the edges across its fan are refreshed.</summary>
    static bool DecimEdgeCollapse(BMesh bm, MeshData d, int e, Quadric[] vq, Heap heap, int[] table, List<int> loops, int[] eClearOther)
    {
        int vOther = bm.EV1[e];
        int vOtherIndex = bm.EV1[e], vClearIndex = bm.EV2[e];
        float vcnx = bm.VNo[3 * vClearIndex], vcny = bm.VNo[3 * vClearIndex + 1], vcnz = bm.VNo[3 * vClearIndex + 2];
        if (IsDegenerateTopology(bm, e)) { InvalidEdgeCostSingle(bm, e, heap, table); return false; }
        CalcTargetCoDb(bm, e, vq, out double odx, out double ody, out double odz);
        float ox = (float)odx, oy = (float)ody, oz = (float)odz;   // bm_decim_calc_target_co_fl
        if (IsDegenerateFlip(bm, e, ox, oy, oz, loops)) { InvalidEdgeCostSingle(bm, e, heap, table); return false; }
        // the merge factor: where the optimum falls along the edge (line_point_factor_v3), or a half for coincident ends
        int v1 = bm.EV1[e], v2 = bm.EV2[e];
        float fac;
        bool same = Math.Abs((float)(bm.VCo[3 * v1] - bm.VCo[3 * v2])) <= FltEpsilon && Math.Abs((float)(bm.VCo[3 * v1 + 1] - bm.VCo[3 * v2 + 1])) <= FltEpsilon && Math.Abs((float)(bm.VCo[3 * v1 + 2] - bm.VCo[3 * v2 + 2])) <= FltEpsilon;
        if (!same)
        {
            float ux = (float)(bm.VCo[3 * v2] - bm.VCo[3 * v1]), uy = (float)(bm.VCo[3 * v2 + 1] - bm.VCo[3 * v1 + 1]), uz = (float)(bm.VCo[3 * v2 + 2] - bm.VCo[3 * v1 + 2]);
            float hx = (float)(ox - bm.VCo[3 * v1]), hy = (float)(oy - bm.VCo[3 * v1 + 1]), hz = (float)(oz - bm.VCo[3 * v1 + 2]);
            float dot = LenSquared3(ux, uy, uz);
            fac = dot > 0.0f ? (float)(Dot(ux, uy, uz, hx, hy, hz) / dot) : 0.0f;
        }
        else fac = 0.5f;
        if (EdgeCollapse(bm, d, e, bm.EV2[e], eClearOther, fac))
        {
            bm.VCo[3 * vOther] = ox; bm.VCo[3 * vOther + 1] = oy; bm.VCo[3 * vOther + 2] = oz;
            for (int i = 0; i < 2; i++)
                if (eClearOther[i] != -1 && table[eClearOther[i]] != -1) { heap.Remove(table[eClearOther[i]]); table[eClearOther[i]] = -1; }
            vq[vOtherIndex].Add(vq[vClearIndex]);
            // interp_v3_v3v3(v_other->no, v_other->no, v_clear_no, fac); normalize_v3
            float s = (float)(1.0f - fac);
            float nx = (float)((float)(s * bm.VNo[3 * vOther]) + (float)(fac * vcnx));
            float ny = (float)((float)(s * bm.VNo[3 * vOther + 1]) + (float)(fac * vcny));
            float nz = (float)((float)(s * bm.VNo[3 * vOther + 2]) + (float)(fac * vcnz));
            NormalizeC(ref nx, ref ny, ref nz);
            bm.VNo[3 * vOther] = nx; bm.VNo[3 * vOther + 1] = ny; bm.VNo[3 * vOther + 2] = nz;
            if (bm.VE[vOther] != BMesh.None)
            {
                int eFirst = bm.VE[vOther], eIter = eFirst;
                do { BuildEdgeCostSingle(bm, eIter, vq, heap, table); } while ((eIter = bm.DiskEdgeNext(eIter, vOther)) != eFirst);
            }
            bm.LoopsOfVert(vOther, loops);
            foreach (int l in loops)
            {
                if (bm.FLen[bm.LF[l]] != 3) continue;
                int eOuter = bm.VertInEdge(bm.LE[bm.LPrev[l]], bm.LV[l]) ? bm.LE[bm.LNext[l]] : bm.LE[bm.LPrev[l]];
                BuildEdgeCostSingle(bm, eOuter, vq, heap, table);
            }
            return true;
        }
        InvalidEdgeCostSingle(bm, e, heap, table);
        return false;
    }

    /// <summary>BM_mesh_decimate_collapse without symmetry, weights or triangulation (the modifier as prep_model sets it): the
    /// face normals and vertex normals must be in place (FaceNormalsUpdate; VNo from the mesh's own normals). factor is the
    /// modifier's ratio as the float32 it is stored as.</summary>
    public static void Collapse(BMesh bm, float factor, MeshData d)
    {
        var vq = new Quadric[bm.VertCount];
        var heap = new Heap(bm.TotEdge);
        var table = new int[bm.EdgeCount];
        for (int i = 0; i < table.Length; i++) table[i] = -1;
        BuildQuadrics(bm, vq);
        for (int e = 0; e < bm.EdgeCount; e++) { if (!bm.EAlive[e]) continue; table[e] = -1; BuildEdgeCostSingle(bm, e, vq, heap, table); }
        int faceTotTarget = FaceTarget(bm.TotFace, factor);
        var loops = new List<int>(); var eClearOther = new int[2];
        while (bm.TotFace > faceTotTarget && !heap.IsEmpty && heap.TopValue != CostInvalid)
        {
            int e = heap.PopMin();
            table[e] = -1;
            DecimEdgeCollapse(bm, d, e, vq, heap, table, loops, eClearOther);
        }
    }

    /// <summary>face_tot_target = bm->totface * factor: int times float in float32, truncated. The product is cast
    /// explicitly: Mono evaluates an uncast float product in double, and the Ehrhardt's 57,737 faces at prep_model's third
    /// ratio give 19,244.9996 there but 19,245.0 in float32 (one collapse too many, found by the drill).</summary>
    public static int FaceTarget(int totFace, float factor) => (int)(float)((float)totFace * factor);

    /// <summary>MOD_decimate: a ratio of exactly 1 returns the mesh untouched; a mesh of 3 faces or fewer too (the modifier
    /// reports "requires more than 3 input faces").</summary>
    public static bool WouldRun(int faceCount, float factor) => factor != 1.0f && faceCount > 3;
}
