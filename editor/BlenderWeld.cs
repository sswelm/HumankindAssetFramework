// BlenderWeld.cs - bmesh.ops.remove_doubles and the sharp-edge marking of vehicle_rig.py's weld tiers, in C# (step (c) of the
// vehicle_rig port, 2026-10-15; docs/Review-Backlog.md item (5)). Read at Blender 5.1.2: bmo_removedoubles.cc (find_doubles
// through BLI_kdtree, weld_verts), BLI_kdtree.hh (insert, the median balance, the stack range search, calc_duplicates_cb),
// bmesh_delete.cc (the tagged delete: faces, then edges, then verts), bmesh_construct.cc (BM_elem_attrs_copy), bmesh_query.cc
// (BM_face_exists, BM_edge_is_manifold, BM_edge_calc_face_angle), math_vector.cc (angle_normalized_v3v3, saasin).
//   * find_doubles: the given vertices go into a kd-tree in their order (their index = their place in the list), the tree is
//     balanced by a median quick-select per axis, then - in the BALANCED node order - every vertex not yet decided searches its
//     range: the hits in traversal order (itself among them) plus itself last are the cluster; two members keep the lower index,
//     more keep the one nearest their float32 centroid (the search's origin first, then the lowest index on a tie); every
//     member maps to the kept one, which is marked kept. All float32, Blender's own order of operations.
//   * weld_verts: the mapped vertices are tagged for deletion; a face with two corners mapping to one vertex that are not
//     neighbours would be split (never on a triangle: this port stops there); each edge with a tagged end is re-made between
//     the targets (an edge that already exists is reused, else created with the old edge's flags) or tagged collapsed when
//     both ends land on one vertex; each face with a tagged corner is re-made from its mapped corners (its edges are the new
//     ones) unless fewer than three remain - an identical face already there is reused, else the new face takes the old
//     face's attributes and loops take the old corners' data, then the two faces SWAP their contents so the old slot lives on
//     with the new corners and the new slot dies with the old; finally the tagged faces, edges and vertices die in that order.
//   * The sharp marking: `e.calc_face_angle(None)` is None off a manifold edge; the angle between the two faces' stored
//     normals (unit, so angle_normalized_v3v3: 2 asinf(|a - b| / 2), or pi minus that of -b when they face apart) over 40
//     degrees clears the edge's SMOOTH flag. The remove_doubles operator's NORMALS_CALC finish recomputes the normals
//     after welding, before this sharp marking reads them.
using System;
using System.Collections.Generic;

public static class BlenderWeld
{
    /// <summary>The custom data the weld moves with BM_elem_attrs_copy: per loop slot (UV layers, corner colours, the custom
    /// normal shorts) and per vertex slot (point colours). Arrays grow as the pool does.</summary>
    public sealed class Layers
    {
        public List<float[]> Uv = new List<float[]>();
        public List<byte[]> CornerColor = new List<byte[]>();
        public short[] CustomNormal;
        public List<byte[]> PointColor = new List<byte[]>();

        public void EnsureLoop(int slot)
        {
            for (int i = 0; i < Uv.Count; i++) if (2 * slot + 1 >= Uv[i].Length) { var a = Uv[i]; Array.Resize(ref a, Math.Max(2 * slot + 2, a.Length * 2)); Uv[i] = a; }
            for (int i = 0; i < CornerColor.Count; i++) if (4 * slot + 3 >= CornerColor[i].Length) { var a = CornerColor[i]; Array.Resize(ref a, Math.Max(4 * slot + 4, a.Length * 2)); CornerColor[i] = a; }
            if (CustomNormal != null && 2 * slot + 1 >= CustomNormal.Length) Array.Resize(ref CustomNormal, Math.Max(2 * slot + 2, CustomNormal.Length * 2));
        }

        /// <summary>CustomData_bmesh_interp on a corner with weights {0, 1}: the layers that interpolate (UVs, byte colours) take the
        /// second source's values exactly (0 a + 1 b); the custom-normal shorts have no interpolation and stay.</summary>
        public void InterpLoopAtOne(int dst, int src)
        {
            EnsureLoop(dst);
            foreach (var u in Uv) { u[2 * dst] = u[2 * src]; u[2 * dst + 1] = u[2 * src + 1]; }
            foreach (var c in CornerColor) for (int k = 0; k < 4; k++) c[4 * dst + k] = c[4 * src + k];
        }

        public void CopyLoop(int src, int dst)
        {
            EnsureLoop(dst);
            foreach (var u in Uv) { u[2 * dst] = u[2 * src]; u[2 * dst + 1] = u[2 * src + 1]; }
            foreach (var c in CornerColor) for (int k = 0; k < 4; k++) c[4 * dst + k] = c[4 * src + k];
            if (CustomNormal != null) { CustomNormal[2 * dst] = CustomNormal[2 * src]; CustomNormal[2 * dst + 1] = CustomNormal[2 * src + 1]; }
        }
    }

    // ---------------------------------------------------------------- BLI_kdtree<float3>

    sealed class KdTree
    {
        public const int Unset = -1;
        public float[] Co; public int[] Index, Left, Right, D; public int Len, Root = Unset;
        public KdTree(int capacity) { Co = new float[3 * capacity]; Index = new int[capacity]; Left = new int[capacity]; Right = new int[capacity]; D = new int[capacity]; }

        public void Insert(int index, float x, float y, float z)
        {
            int n = Len++;
            Left[n] = Right[n] = Unset; Co[3 * n] = x; Co[3 * n + 1] = y; Co[3 * n + 2] = z; Index[n] = index; D[n] = 0;
        }

        void Swap(int a, int b)
        {
            for (int k = 0; k < 3; k++) (Co[3 * a + k], Co[3 * b + k]) = (Co[3 * b + k], Co[3 * a + k]);
            (Index[a], Index[b]) = (Index[b], Index[a]);
        }

        /// <summary>kdtree_balance over the nodes [start, start + len): a quick-select around the median on the axis, then the
        /// halves on the next axis.</summary>
        int Balance(int start, int len, int axis)
        {
            if (len <= 0) return Unset;
            if (len == 1) return start;
            int left = 0, right = len - 1, median = len / 2;
            while (right > left)
            {
                float co = Co[3 * (start + right) + axis];
                int i = left - 1, j = right;
                while (true)
                {
                    while (Co[3 * (start + (++i)) + axis] < co) { }
                    while (Co[3 * (start + (--j)) + axis] > co && j > left) { }
                    if (i >= j) break;
                    Swap(start + i, start + j);
                }
                Swap(start + i, start + right);
                if (i >= median) right = i - 1;
                if (i <= median) left = i + 1;
            }
            int node = start + median;
            D[node] = axis;
            axis = (axis + 1) % 3;
            Left[node] = Balance(start, median, axis);
            Right[node] = Balance(start + median + 1, len - (median + 1), axis);
            return node;
        }

        public void BalanceAll() { Root = Balance(0, Len, 0); }

        /// <summary>kdtree_range_search_cb: a stack walk from the root; a node outside the slab on its axis sends the walk to one
        /// side, a node inside is reported when within range (float32 distance squared) and both sides are pushed, left first.</summary>
        public void RangeSearch(float x, float y, float z, float range, Action<int, float> found)
        {
            if (Root == Unset) return;
            float rangeSq = (float)(range * range);
            var stack = new List<int> { Root };
            var co = new[] { x, y, z };
            while (stack.Count > 0)
            {
                int node = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1);
                int d = D[node];
                if ((float)(co[d] + range) < Co[3 * node + d]) { if (Left[node] != Unset) stack.Add(Left[node]); }
                else if ((float)(co[d] - range) > Co[3 * node + d]) { if (Right[node] != Unset) stack.Add(Right[node]); }
                else
                {
                    float dx = (float)(Co[3 * node] - x), dy = (float)(Co[3 * node + 1] - y), dz = (float)(Co[3 * node + 2] - z);
                    float distSq = (float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz));
                    if (distSq <= rangeSq) found(Index[node], distSq);
                    if (Left[node] != Unset) stack.Add(Left[node]);
                    if (Right[node] != Unset) stack.Add(Right[node]);
                }
            }
        }
    }

    /// <summary>bmesh_find_doubles_by_distance_impl + kdtree_calc_duplicates_cb (no kept vertices): the target map, each listed
    /// vertex that merges mapped to the vertex it merges into.</summary>
    public static Dictionary<int, int> FindDoubles(BMesh bm, IReadOnlyList<int> verts, float dist)
    {
        int n = verts.Count;
        var tree = new KdTree(n);
        for (int i = 0; i < n; i++) { int v = verts[i]; tree.Insert(i, bm.VCo[3 * v], bm.VCo[3 * v + 1], bm.VCo[3 * v + 2]); }
        tree.BalanceAll();
        var duplicates = new int[n];
        for (int i = 0; i < n; i++) duplicates[i] = -1;
        var cluster = new List<int>();
        if (tree.Root != KdTree.Unset)
            for (int ni = 0; ni < tree.Len; ni++)
            {
                int nodeIndex = tree.Index[ni];
                if (duplicates[nodeIndex] != -1) continue;
                cluster.Clear();
                tree.RangeSearch(tree.Co[3 * ni], tree.Co[3 * ni + 1], tree.Co[3 * ni + 2], dist, (index, distSq) => { if (duplicates[index] == -1) cluster.Add(index); });
                if (cluster.Count == 0) continue;
                cluster.Add(nodeIndex);
                int target = cluster[PickTarget(tree, verts, bm, cluster)];
                foreach (int c in cluster) duplicates[c] = target;
            }
        var map = new Dictionary<int, int>();
        for (int i = 0; i < n; i++) if (duplicates[i] != -1 && duplicates[i] != i) map[verts[i]] = verts[duplicates[i]];
        return map;
    }

    /// <summary>deduplicate_target_calc_fn: two members keep the lower index; more keep the one nearest the float32 centroid,
    /// starting from the last (the search's origin), a tie going to the lower index.</summary>
    static int PickTarget(KdTree tree, IReadOnlyList<int> verts, BMesh bm, List<int> cluster)
    {
        int num = cluster.Count;
        if (num == 2) return cluster[0] < cluster[1] ? 0 : 1;
        float cx = 0f, cy = 0f, cz = 0f;
        for (int i = 0; i < num; i++) { int v = verts[cluster[i]]; cx = (float)(cx + bm.VCo[3 * v]); cy = (float)(cy + bm.VCo[3 * v + 1]); cz = (float)(cz + bm.VCo[3 * v + 2]); }
        float fn = (float)num; cx = (float)(cx / fn); cy = (float)(cy / fn); cz = (float)(cz / fn);
        float DistSq(int i) { int v = verts[cluster[i]]; float dx = (float)(cx - bm.VCo[3 * v]), dy = (float)(cy - bm.VCo[3 * v + 1]), dz = (float)(cz - bm.VCo[3 * v + 2]); return (float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz)); }
        int end = num - 1, best = end; float bestSq = DistSq(best);
        for (int i = 0; i < end; i++)
        {
            float test = DistSq(i);
            if (test > bestSq) continue;
            if (test == bestSq && cluster[i] > cluster[best]) continue;
            best = i; bestSq = test;
        }
        return best;
    }

    // ---------------------------------------------------------------- bmo_weld_verts_exec

    /// <summary>bmesh.ops.remove_doubles(bm, verts, dist): the doubles found, then welded.</summary>
    public static int RemoveDoubles(BMesh bm, Layers layers, IReadOnlyList<int> verts, float dist)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var map = FindDoubles(bm, verts, dist);
        long t1 = sw.ElapsedMilliseconds;
        if (map.Count > 0) WeldVerts(bm, layers, map);
        // BMO_op_finish: the operator is flagged NORMALS_CALC, so every face normal is recomputed from the welded corners
        // (BM_mesh_normals_update), the copied normals of the re-made faces included
        BlenderDecimate.FaceNormalsUpdate(bm);
        if (Environment.GetEnvironmentVariable("VEHICLERIG_DEBUG_TIME") == "1") Console.Error.WriteLine($"TIME   remove_doubles on {verts.Count} verts: find {t1} ms, weld {sw.ElapsedMilliseconds - t1} ms ({map.Count} merged)");
        return map.Count;
    }

    public static void WeldVerts(BMesh bm, Layers layers, Dictionary<int, int> targetmap)
    {
        var delV = new bool[bm.VertCount]; var inFace = new bool[bm.VertCount];
        var delE = new List<bool>(); var colE = new List<bool>(); var delF = new List<bool>();
        bool DelE(int e) => e < delE.Count && delE[e]; bool ColE(int e) => e < colE.Count && colE[e];
        void SetE(List<bool> a, int e) { while (a.Count <= e) a.Add(false); a[e] = true; }
        // the merge verts, tagged for deletion (their hidden flags merged: none here)
        for (int v = 0; v < bm.VertCount; v++) if (bm.VAlive[v] && targetmap.ContainsKey(v)) delV[v] = true;
        // a face whose corners merge onto one of its own vertices, not adjacent: BM_face_split - never on a triangle
        for (int f = 0; f < bm.FaceCount; f++)
        {
            if (!bm.FAlive[f]) continue;
            int l = bm.FLFirst[f], l0 = l;
            do
            {
                if (targetmap.TryGetValue(bm.LV[l], out int vTar))
                {
                    int lTar = bm.FaceVertShareLoop(f, vTar);
                    if (lTar != BMesh.None && lTar != l && !bm.LoopIsAdjacent(lTar, l)) throw new NotSupportedException("remove_doubles: a face with two corners welding onto one of its own vertices would be split (remdoubles_splitface), which this port does not model");
                }
                l = bm.LNext[l];
            } while (l != l0);
        }
        // the edges: re-made between the targets, or collapsed
        int edgeCount0 = bm.EdgeCount;
        for (int e = 0; e < edgeCount0; e++)
        {
            if (!bm.EAlive[e]) continue;
            int v1 = bm.EV1[e], v2 = bm.EV2[e];
            bool d1 = delV[v1], d2 = delV[v2];
            if (!(d1 || d2)) continue;
            if (d1) v1 = targetmap[v1];
            if (d2) v2 = targetmap[v2];
            if (v1 == v2) SetE(colE, e);
            else
            {
                int eNew = bm.EdgeExists(v1, v2);
                if (eNew == BMesh.None) eNew = bm.EdgeCreate(v1, v2, e);
                // BM_elem_flag_merge_ex(e_new, e, HIDDEN): neither hidden, so just the union of the flags
                byte both = (byte)(bm.EFlag[eNew] | bm.EFlag[e]); bm.EFlag[eNew] = both; bm.EFlag[e] = both;
            }
            SetE(delE, e);
        }
        // the faces: a face with a tagged corner is re-made, the old slot keeping the new corners (the swap), the old corners dying
        int faceCount0 = bm.FaceCount;
        var verts = new List<int>(); var edges = new List<int>(); var loops = new List<int>();
        for (int f = 0; f < faceCount0; f++)
        {
            if (!bm.FAlive[f]) continue;
            bool vertDelete = false; int edgeCollapse = 0;
            int l = bm.FLFirst[f], l0 = l;
            do { if (delV[bm.LV[l]]) vertDelete = true; if (ColE(bm.LE[l])) edgeCollapse++; l = bm.LNext[l]; } while (l != l0);
            if (!vertDelete) continue;
            SetE(delF, f);
            if (bm.FLen[f] - edgeCollapse < 3) continue;
            // remdoubles_createface
            verts.Clear(); edges.Clear(); loops.Clear();
            bool bail = false;
            int lCurr = bm.FLFirst[f], lFirst = lCurr;
            int vCurr = bm.LV[lCurr]; bool isDelCurr = delV[vCurr]; if (isDelCurr) vCurr = targetmap[vCurr];
            do
            {
                int lNext = bm.LNext[lCurr];
                int vNext = bm.LV[lNext]; bool isDelNext = delV[vNext]; if (isDelNext) vNext = targetmap[vNext];
                int eNew;
                if (!(isDelCurr || isDelNext)) eNew = bm.LE[lCurr];
                else if (vCurr == vNext) eNew = BMesh.None;
                else { eNew = bm.EdgeExists(vCurr, vNext); if (eNew == BMesh.None) throw new InvalidOperationException("remove_doubles: the welded edge was not made"); }
                if (eNew != BMesh.None)
                {
                    if (inFace[vCurr]) { edges.Clear(); bail = true; break; }
                    inFace[vCurr] = true;
                    edges.Add(eNew); loops.Add(lCurr); verts.Add(vCurr);
                }
                vCurr = vNext; isDelCurr = isDelNext;
                lCurr = lNext;
            } while (lCurr != lFirst);
            foreach (int v in verts) inFace[v] = false;
            if (bail || edges.Count < 3) continue;
            var varr = verts.ToArray();
            int fNew = bm.FaceExists(varr, varr.Length);
            if (fNew != BMesh.None)
            {
                // not created: the flags merged into the existing face (f dies below)
                byte both = (byte)(bm.FFlag[fNew] | bm.FFlag[f]); bm.FFlag[fNew] = both; bm.FFlag[f] = both;
                continue;
            }
            var slots = new int[varr.Length];
            fNew = bm.FaceCreate(varr, edges.ToArray(), varr.Length, f, slots);
            for (int i = 0; i < slots.Length; i++) layers.CopyLoop(loops[i], slots[i]);
            // bmesh_face_swap_data + the op flags swapped: the old slot f now holds the new corners and is no longer tagged; the
            // new slot holds the old corners and dies at once
            bm.FaceSwapData(fNew, f);
            delF[f] = false;
            bm.FaceKill(fNew);
        }
        // BMO_mesh_delete_oflag_context(ELE_DEL, DEL_ONLYTAGGED): faces, then edges, then verts, each in slot order
        for (int f = 0; f < delF.Count; f++) if (delF[f] && bm.FAlive[f]) bm.FaceKill(f);
        for (int e = 0; e < delE.Count; e++) if (delE[e] && bm.EAlive[e]) bm.EdgeKill(e);
        for (int v = 0; v < delV.Length; v++) if (delV[v] && bm.VAlive[v]) bm.VertKill(v);
    }

    // ---------------------------------------------------------------- the sharp marking

    /// <summary>angle_normalized_v3v3 on two unit vectors, Blender's float32 arithmetic and the C runtime's asinf.</summary>
    public static float AngleNormalized(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dot = (float)((float)((float)(ax * bx) + (float)(ay * by)) + (float)(az * bz));
        if (dot >= 0f)
        {
            float dx = (float)(ax - bx), dy = (float)(ay - by), dz = (float)(az - bz);
            float len = (float)Math.Sqrt((float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz)));
            return (float)(2.0f * Saasin((float)(len / 2.0f)));
        }
        float ex = (float)(ax - (-bx)), ey = (float)(ay - (-by)), ez = (float)(az - (-bz));
        float len2 = (float)Math.Sqrt((float)((float)((float)(ex * ex) + (float)(ey * ey)) + (float)(ez * ez)));
        return (float)((float)Math.PI - (float)(2.0f * Saasin((float)(len2 / 2.0f))));
    }

    static float Saasin(float fac) => fac <= -1.0f ? (float)(-Math.PI / 2) : fac >= 1.0f ? (float)(Math.PI / 2) : BlenderTrig.Asinf(fac);

    /// <summary>The script's loop: every manifold edge whose faces' stored normals meet over the angle loses its SMOOTH flag
    /// (`e.smooth = False`); the comparison is Python's, the float32 angle against math.radians(deg).</summary>
    public static int MarkSharpOverAngle(BMesh bm, double degrees)
    {
        double limit = degrees * (Math.PI / 180.0); int marked = 0;
        for (int e = 0; e < bm.EdgeCount; e++)
        {
            if (!bm.EAlive[e] || !bm.EdgeIsManifold(e)) continue;
            int f1 = bm.LF[bm.EL[e]], f2 = bm.LF[bm.LRadialNext[bm.EL[e]]];
            float angle = AngleNormalized(bm.FNo[3 * f1], bm.FNo[3 * f1 + 1], bm.FNo[3 * f1 + 2], bm.FNo[3 * f2], bm.FNo[3 * f2 + 1], bm.FNo[3 * f2 + 2]);
            if ((double)angle > limit) { bm.EFlag[e] = (byte)(bm.EFlag[e] & ~BMesh.FlagSmooth); marked++; }
        }
        return marked;
    }
}
