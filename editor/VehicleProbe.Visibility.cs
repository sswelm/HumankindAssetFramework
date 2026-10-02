// VehicleProbe.Visibility.cs - the PART row's visibility verdict (field 6), as vehicle_rig.py's probe computes it
// (step 3b of replacing Blender, 2026-10-02). A part is EXTERNAL (1) when any sampled vertex can shoot a straight ray
// to infinity without meeting other geometry; INTERIOR (0) when every sample is blocked in every direction - cockpit
// gear, engine guts, provably never seen - which the Lab strips for the triangle budget. The script's rules, kept:
//   * ONE BVH over every part's polygons in world space, the mesh DATA vertices (`matrix_world @ v.co`) - for a
//     skinned part the importer's bind pose, not the posed vertices the PART box is read from;
//   * samples: every n-th vertex of the part, n = max(1, vertices / 30), in Blender's vertex order (BlenderIslands
//     for a loose part; the importer's order otherwise); the first sample that escapes decides;
//   * directions, in order: the vertex's NORMAL through matrix_world's 3x3 (normalized; skipped when shorter than
//     0.5), then 6 axes and 8 cube diagonals in Blender's Z-up frame;
//   * the ray starts eps along its direction - eps = max(1e-4, 1e-3 x the largest box size of any part) - so a point
//     clears its own surface; any hit at t >= 0 blocks it (BVHTree.ray_cast: the watertight ray-triangle test, a hit
//     on an edge or a vertex counts, no back-face culling).
// The vertex NORMAL is the one Blender holds for the vertex after import. A glTF mesh with a NORMAL attribute gets it as
// custom corner normals, and `vertex.normal` is then the angle-weighted mix of the vertex's corner normals - which for
// a glTF vertex, whose corners all carry the file's normal, IS the file's normal (measured on the Ehrhardt: 13 sliver
// quads differed with computed normals, none with the file's) - for a skinned vertex the file's normal skinned into the
// bind pose as the importer skins it (skin_into_bind_pose: the skinning matrix's 3x3, normalized). Without a NORMAL attribute Blender computes the vertex
// normal from the faces (angle-weighted face normals; a vertex without faces: its position's direction).
// Measured: 10,498 parts on 118 files agree with Blender's probe, 1,171 of them interior.
using System;
using System.Collections.Generic;
using System.Linq;

public static partial class VehicleProbe
{
    /// <summary>One part as Blender holds its mesh DATA: the vertices in Blender's order, in world space and Blender's
    /// frame, the direction each one's normal ray takes there, and its triangles (into those vertices).</summary>
    sealed class PartMesh { public float[] World; public float[] Normal; public float[] Local; public int[] Tris; public int Count; }   // Local: the mesh data as Blender holds it (float32, glTF frame; the bind pose for a skinned part) - the inside-out verdict reads it (step 3c)

    static readonly double[][] FixedDirections = MakeFixedDirections();
    /// <summary>The fixed directions as mathutils normalizes them (Vector((1, 1, 1)).normalized(): double length, 1.0f / float(sqrt)).</summary>
    static readonly float[][] FixedF32 = MakeFixedF32();
    static float[][] MakeFixedF32()
    {
        var raw = new[] { new float[] { 1, 0, 0 }, new float[] { -1, 0, 0 }, new float[] { 0, 1, 0 }, new float[] { 0, -1, 0 }, new float[] { 0, 0, 1 }, new float[] { 0, 0, -1 },
            new float[] { 1, 1, 1 }, new float[] { 1, 1, -1 }, new float[] { 1, -1, 1 }, new float[] { 1, -1, -1 }, new float[] { -1, 1, 1 }, new float[] { -1, 1, -1 }, new float[] { -1, -1, 1 }, new float[] { -1, -1, -1 } };
        foreach (var d in raw) NormalizeVn(ref d[0], ref d[1], ref d[2]);
        return raw;
    }
    static double[][] MakeFixedDirections()
    {
        var raw = new[] { new double[] { 1, 0, 0 }, new double[] { -1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, -1, 0 }, new double[] { 0, 0, 1 }, new double[] { 0, 0, -1 },
            new double[] { 1, 1, 1 }, new double[] { 1, 1, -1 }, new double[] { 1, -1, 1 }, new double[] { 1, -1, -1 }, new double[] { -1, 1, 1 }, new double[] { -1, 1, -1 }, new double[] { -1, -1, 1 }, new double[] { -1, -1, -1 } };
        foreach (var d in raw) { double l = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]); d[0] /= l; d[1] /= l; d[2] /= l; }
        return raw;
    }

    /// <summary>The data vertices of one part (the node's whole mesh, or one island of it) in Blender's order, its
    /// triangles, and its normal rays - from the object's local space through `objWorld` into Blender's frame.
    /// `dataPosition` gives a primitive's vertex in the object's local space (the file's position, or the bind pose);
    /// `dataNormal` the file's normal there (skinned into the bind pose for a skinned vertex), or null without one.</summary>
    static PartMesh BuildPartMesh(HafModel m, int node, int onlyPrim, int[] onlyVerts, Func<HafPrimitive, int, double[]> dataPosition, Func<HafPrimitive, int, double[]> dataNormal, float[] mb)
    {
        var mesh = m.Meshes[m.Nodes[node].Mesh];
        var local = new List<double>(); var fileNormal = new List<double>(); var tris = new List<int>();
        var faces = new HashSet<(int, int, int)>();   // mesh.validate() drops a second face over the same three vertices, whichever way it winds: the FIRST stays
        int offset = 0;
        for (int pi = 0; pi < mesh.Primitives.Count; pi++)
        {
            if (onlyPrim >= 0 && pi != onlyPrim) continue;
            var p = mesh.Primitives[pi];
            int[] verts = onlyVerts ?? Used(p);
            var rank = new int[p.VertexCount]; for (int i = 0; i < rank.Length; i++) rank[i] = -1;
            for (int k = 0; k < verts.Length; k++)
            {
                rank[verts[k]] = offset + k;
                var co = dataPosition(p, verts[k]);
                local.Add(co[0]); local.Add(co[1]); local.Add(co[2]);
                var fn = dataNormal(p, verts[k]);
                if (fn != null) { fileNormal.Add(fn[0]); fileNormal.Add(fn[1]); fileNormal.Add(fn[2]); }
                else { fileNormal.Add(double.NaN); fileNormal.Add(double.NaN); fileNormal.Add(double.NaN); }
            }
            int count = p.Indices != null ? p.Indices.Length : p.VertexCount;
            int At(int i) => p.Indices != null ? p.Indices[i] : i;
            void Tri(int a, int b, int c)
            {
                if (a < 0 || b < 0 || c < 0 || a >= rank.Length || b >= rank.Length || c >= rank.Length) return;
                int ra = rank[a], rb = rank[b], rc = rank[c];
                if (ra < 0 || rb < 0 || rc < 0 || ra == rb || rb == rc || ra == rc) return;   // another island's, or degenerate (mesh.validate drops it)
                int lo = Math.Min(ra, Math.Min(rb, rc)), hi = Math.Max(ra, Math.Max(rb, rc));
                if (!faces.Add((lo, ra + rb + rc - lo - hi, hi))) return;   // a duplicate (external review of PR #115: two twins wound against each other summed to no normal at all)
                tris.Add(ra); tris.Add(rb); tris.Add(rc);
            }
            switch (p.Mode)   // the importer's points_edges_tris: strips and fans become triangles, lines and points no polygon
            {
                case 4: for (int t = 0; t + 2 < count; t += 3) Tri(At(t), At(t + 1), At(t + 2)); break;
                case 5: for (int t = 0; t + 2 < count; t++) { if ((t & 1) == 0) Tri(At(t), At(t + 1), At(t + 2)); else Tri(At(t), At(t + 2), At(t + 1)); } break;
                case 6: for (int t = 1; t + 1 < count; t++) Tri(At(0), At(t), At(t + 1)); break;
            }
            offset += verts.Length;
        }
        int n = offset;
        var pm = new PartMesh { Count = n, World = new float[n * 3], Normal = new float[n * 3], Local = new float[n * 3], Tris = tris.ToArray() };
        // a mesh with file normals: Blender's vertex.normal is the file's normal through its two-short custom-normal encoding and the
        // corner-angle mix (VehicleProbe.CustomNormals.cs), not the file's normal itself; without file normals it is the computed one
        bool anyFileNormal = false; for (int i = 0; i < n && !anyFileNormal; i++) anyFileNormal = !double.IsNaN(fileNormal[i * 3]);
        var Pb = new float[n * 3]; var Nb = anyFileNormal ? new float[n * 3] : null;
        for (int i = 0; i < n; i++)
        {
            Pb[i * 3] = (float)local[i * 3]; Pb[i * 3 + 1] = (float)-local[i * 3 + 2]; Pb[i * 3 + 2] = (float)local[i * 3 + 1];
            if (Nb == null) continue;
            if (double.IsNaN(fileNormal[i * 3])) { Nb[i * 3] = Nb[i * 3 + 1] = Nb[i * 3 + 2] = float.NaN; }
            else { Nb[i * 3] = (float)fileNormal[i * 3]; Nb[i * 3 + 1] = (float)-fileNormal[i * 3 + 2]; Nb[i * 3 + 2] = (float)fileNormal[i * 3 + 1]; }
        }
        float[] blenderN = BlenderVertexNormals(Pb, pm.Tris, Nb);
        for (int i = 0; i < n; i++)
        {
            double lx = local[i * 3], ly = local[i * 3 + 1], lz = local[i * 3 + 2];
            pm.Local[i * 3] = (float)lx; pm.Local[i * 3 + 1] = (float)ly; pm.Local[i * 3 + 2] = (float)lz;
            // the script: p = matrix_world @ co, d = (matrix_world.to_3x3() @ normal).normalized() - mathutils' float32 products summed
            // in double (MatRow), NOT the inverse transpose; Blender's frame (x, -z, y) of the glTF data
            float bx = (float)lx, by = (float)-lz, bz = (float)ly;
            pm.World[i * 3] = MatRow(mb, 0, 4, bx, by, bz, 1f); pm.World[i * 3 + 1] = MatRow(mb, 1, 4, bx, by, bz, 1f); pm.World[i * 3 + 2] = MatRow(mb, 2, 4, bx, by, bz, 1f);
            float nbx = blenderN[i * 3], nby = blenderN[i * 3 + 1], nbz = blenderN[i * 3 + 2];
            float wx = MatRow(mb, 0, 3, nbx, nby, nbz, 0f), wy = MatRow(mb, 1, 3, nbx, nby, nbz, 0f), wz = MatRow(mb, 2, 3, nbx, nby, nbz, 0f);
            NormalizeVn(ref wx, ref wy, ref wz);
            pm.Normal[i * 3] = wx; pm.Normal[i * 3 + 1] = wy; pm.Normal[i * 3 + 2] = wz;
        }
        return pm;
    }


    // ---------------------------------------------------------------- one BVH over every part's triangles

    /// <summary>A bounding-volume tree over triangles, for one question: does a ray meet any of them?</summary>
    internal sealed class TriangleBvh
    {
        readonly float[] v;    // world vertices, 3 per vertex
        readonly int[] t;      // triangles, 3 vertex indices each, in leaf order
        readonly float[] bmin, bmax; readonly int[] left, start, count;   // per node: bounds; an inner node's left child (right = left + 1); a leaf's triangles

        public TriangleBvh(float[] vertices, int[] triangles)
        {
            v = vertices;
            int nt = triangles.Length / 3;
            var cx = new float[nt]; var cy = new float[nt]; var cz = new float[nt];
            for (int i = 0; i < nt; i++)
            {
                int a = triangles[i * 3] * 3, b = triangles[i * 3 + 1] * 3, c = triangles[i * 3 + 2] * 3;
                cx[i] = (v[a] + v[b] + v[c]) / 3f; cy[i] = (v[a + 1] + v[b + 1] + v[c + 1]) / 3f; cz[i] = (v[a + 2] + v[b + 2] + v[c + 2]) / 3f;
            }
            var order = new int[nt]; for (int i = 0; i < nt; i++) order[i] = i;
            int cap = Math.Max(1, 2 * nt);
            bmin = new float[cap * 3]; bmax = new float[cap * 3]; left = new int[cap]; start = new int[cap]; count = new int[cap];
            int nodes = 1;
            var stack = new Stack<(int node, int lo, int hi)>();
            stack.Push((0, 0, nt));
            var centroid = new[] { cx, cy, cz };
            while (stack.Count > 0)
            {
                var (node, lo, hi) = stack.Pop();
                float x0 = float.PositiveInfinity, y0 = x0, z0 = x0, x1 = float.NegativeInfinity, y1 = x1, z1 = x1;
                float c0x = x0, c0y = x0, c0z = x0, c1x = x1, c1y = x1, c1z = x1;
                for (int i = lo; i < hi; i++)
                {
                    int tri = order[i];
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = triangles[tri * 3 + k] * 3;
                        if (v[vi] < x0) x0 = v[vi]; if (v[vi] > x1) x1 = v[vi];
                        if (v[vi + 1] < y0) y0 = v[vi + 1]; if (v[vi + 1] > y1) y1 = v[vi + 1];
                        if (v[vi + 2] < z0) z0 = v[vi + 2]; if (v[vi + 2] > z1) z1 = v[vi + 2];
                    }
                    if (cx[tri] < c0x) c0x = cx[tri]; if (cx[tri] > c1x) c1x = cx[tri];
                    if (cy[tri] < c0y) c0y = cy[tri]; if (cy[tri] > c1y) c1y = cy[tri];
                    if (cz[tri] < c0z) c0z = cz[tri]; if (cz[tri] > c1z) c1z = cz[tri];
                }
                bmin[node * 3] = x0; bmin[node * 3 + 1] = y0; bmin[node * 3 + 2] = z0; bmax[node * 3] = x1; bmax[node * 3 + 1] = y1; bmax[node * 3 + 2] = z1;
                int n = hi - lo;
                float ex = c1x - c0x, ey = c1y - c0y, ez = c1z - c0z;
                int axis = ex >= ey && ex >= ez ? 0 : ey >= ez ? 1 : 2;
                float extent = axis == 0 ? ex : axis == 1 ? ey : ez;
                if (n <= 4 || !(extent > 0)) { start[node] = lo; count[node] = n; continue; }
                int mid = lo + n / 2;
                Select(order, centroid[axis], lo, hi - 1, mid);   // median split on the widest axis of the centroids
                int l = nodes; nodes += 2;
                left[node] = l; count[node] = 0;
                stack.Push((l, lo, mid)); stack.Push((l + 1, mid, hi));
            }
            t = new int[triangles.Length];
            for (int i = 0; i < nt; i++) { t[i * 3] = triangles[order[i] * 3]; t[i * 3 + 1] = triangles[order[i] * 3 + 1]; t[i * 3 + 2] = triangles[order[i] * 3 + 2]; }
        }

        static void Select(int[] order, float[] key, int lo, int hi, int k)   // quickselect: order[k] takes the k-th smallest key, the rest partition around it
        {
            while (lo < hi)
            {
                float pivot = key[order[(lo + hi) >> 1]];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (key[order[i]] < pivot) i++;
                    while (key[order[j]] > pivot) j--;
                    if (i <= j) { int tmp = order[i]; order[i] = order[j]; order[j] = tmp; i++; j--; }
                }
                if (k <= j) hi = j; else if (k >= i) lo = i; else return;
            }
        }

        /// <summary>Does the ray from (ox, oy, oz) along (dx, dy, dz) meet any triangle, as BLI_bvhtree_ray_cast decides it? Its
        /// arithmetic is Blender's, float32 step for step: normalize_v3 on the direction (float32 dot, sqrtf, times 1.0f / length),
        /// the watertight precalc (the dominant axis - ties go to x, then y - kx/ky swapped under a negative direction, the shear
        /// constants from 1.0f / dir[kz]), per leaf the box test fast_ray_nearest_hit (bounds minus origin times 1 / dir, NaN
        /// never culls) and isect_ray_tri_watertight_v3 (Woop, Benthin, Wald 2013: an edge or vertex hit counts, from either
        /// side; a parallel triangle - determinant zero - does not). This tree's own double slab test, lenient by 1e-6, only
        /// finds the candidates; Blender's tests decide each one. A grazing ray along a face edge is decided by the last
        /// float32 bit, so nothing here may be computed in double (the re-fused Dragon, 2026-10-02).</summary>
        public bool AnyHit(float ox, float oy, float oz, float dx, float dy, float dz)
        {
            float dd = (float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz));
            if (dd > 1.0e-35f) { dd = (float)Math.Sqrt(dd); float s = (float)(1.0f / dd); dx = (float)(dx * s); dy = (float)(dy * s); dz = (float)(dz * s); }
            else { dx = 0f; dy = 0f; dz = 0f; }
            float[] dir = { dx, dy, dz };
            float xn = Math.Abs(dx), yn = Math.Abs(dy), zn = Math.Abs(dz);
            int kz = (xn >= yn && xn >= zn) ? 0 : (yn >= xn && yn >= zn) ? 1 : 2;
            int kx = kz != 2 ? kz + 1 : 0; int ky = kx != 2 ? kx + 1 : 0;
            if (dir[kz] < 0f) { int tmp = kx; kx = ky; ky = tmp; }
            float inv = (float)(1.0f / dir[kz]);
            float sx = (float)(dir[kx] * inv), sy = (float)(dir[ky] * inv), sz = inv;
            var idot = new float[3]; var near = new int[3];
            // bvhtree_ray_cast_data_precalc: a direction component below FLT_EPSILON makes the reciprocal FLT_MAX, not infinity - so
            // an origin exactly on a box's far plane gives t = 0 there and the box is culled (the Dragon's decal corners under deck
            // vertices; two of its verdicts). Blender's binary does not follow this at every such origin (docs/Review-Backlog.md).
            for (int i = 0; i < 3; i++) { idot[i] = Math.Abs(dir[i]) < 1.1920929e-7f ? float.MaxValue : (float)(1.0f / dir[i]); near[i] = idot[i] < 0f ? 1 : 0; }
            float[] o = { ox, oy, oz };
            double ix = dx != 0 ? 1.0 / dx : double.PositiveInfinity, iy = dy != 0 ? 1.0 / dy : double.PositiveInfinity, iz = dz != 0 ? 1.0 / dz : double.PositiveInfinity;
            var stack = new int[128]; int sp = 0; stack[sp++] = 0;
            var A = new float[3]; var B = new float[3]; var C = new float[3]; var t1s = new float[3]; var t2s = new float[3];
            while (sp > 0)
            {
                int node = stack[--sp];
                double t0 = 0, t1 = double.PositiveInfinity;
                double a = (bmin[node * 3] - ox) * ix, b = (bmax[node * 3] - ox) * ix;
                if (dx == 0) { if (ox < bmin[node * 3] || ox > bmax[node * 3]) continue; } else { if (a > b) { double tmp = a; a = b; b = tmp; } if (a > t0) t0 = a; if (b < t1) t1 = b; }
                a = (bmin[node * 3 + 1] - oy) * iy; b = (bmax[node * 3 + 1] - oy) * iy;
                if (dy == 0) { if (oy < bmin[node * 3 + 1] || oy > bmax[node * 3 + 1]) continue; } else { if (a > b) { double tmp = a; a = b; b = tmp; } if (a > t0) t0 = a; if (b < t1) t1 = b; }
                a = (bmin[node * 3 + 2] - oz) * iz; b = (bmax[node * 3 + 2] - oz) * iz;
                if (dz == 0) { if (oz < bmin[node * 3 + 2] || oz > bmax[node * 3 + 2]) continue; } else { if (a > b) { double tmp = a; a = b; b = tmp; } if (a > t0) t0 = a; if (b < t1) t1 = b; }
                if (t0 > t1 * (1 + 1e-6) + 1e-6) continue;
                if (count[node] == 0) { stack[sp++] = left[node]; stack[sp++] = left[node] + 1; continue; }
                for (int i = start[node]; i < start[node] + count[node]; i++)
                {
                    int ia = t[i * 3] * 3, ib = t[i * 3 + 1] * 3, ic = t[i * 3 + 2] * 3;
                    // the leaf's box (BLI_bvhtree_insert, epsilon 0) through fast_ray_nearest_hit
                    bool culled = false;
                    for (int ax = 0; ax < 3; ax++)
                    {
                        float mn = Math.Min(v[ia + ax], Math.Min(v[ib + ax], v[ic + ax])), mx = Math.Max(v[ia + ax], Math.Max(v[ib + ax], v[ic + ax]));
                        float bvNear = near[ax] == 0 ? mn : mx, bvFar = near[ax] == 0 ? mx : mn;
                        t1s[ax] = (float)((float)(bvNear - o[ax]) * idot[ax]); t2s[ax] = (float)((float)(bvFar - o[ax]) * idot[ax]);
                    }
                    if ((t1s[0] > t2s[1] || t2s[0] < t1s[1] || t1s[0] > t2s[2] || t2s[0] < t1s[2] || t1s[1] > t2s[2] || t2s[1] < t1s[2]) || (t2s[0] < 0f || t2s[1] < 0f || t2s[2] < 0f)) culled = true;
                    if (culled) continue;
                    if (Math.Max(t1s[0], Math.Max(t1s[1], t1s[2])) >= float.MaxValue) continue;
                    // isect_ray_tri_watertight_v3
                    A[0] = (float)(v[ia] - ox); A[1] = (float)(v[ia + 1] - oy); A[2] = (float)(v[ia + 2] - oz);
                    B[0] = (float)(v[ib] - ox); B[1] = (float)(v[ib + 1] - oy); B[2] = (float)(v[ib + 2] - oz);
                    C[0] = (float)(v[ic] - ox); C[1] = (float)(v[ic + 1] - oy); C[2] = (float)(v[ic + 2] - oz);
                    float Ax = (float)(A[kx] - (float)(sx * A[kz])), Ay = (float)(A[ky] - (float)(sy * A[kz]));
                    float Bx = (float)(B[kx] - (float)(sx * B[kz])), By = (float)(B[ky] - (float)(sy * B[kz]));
                    float Cx = (float)(C[kx] - (float)(sx * C[kz])), Cy = (float)(C[ky] - (float)(sy * C[kz]));
                    float U = (float)((float)(Cx * By) - (float)(Cy * Bx)), V = (float)((float)(Ax * Cy) - (float)(Ay * Cx)), W = (float)((float)(Bx * Ay) - (float)(By * Ax));
                    if ((U < 0f || V < 0f || W < 0f) && (U > 0f || V > 0f || W > 0f)) continue;
                    float det = (float)((float)(U + V) + W);
                    if (det == 0f || float.IsNaN(det) || float.IsInfinity(det)) continue;
                    float T = (float)((float)((float)((float)(U * A[kz]) + (float)(V * B[kz])) + (float)(W * C[kz])) * sz);
                    bool detNeg = det < 0f || (det == 0f && 1f / det < 0f);
                    float signT = detNeg ? -T : T;
                    if (signT < 0f) continue;
                    float dist = (float)(T * (float)(1.0f / det));
                    if (dist >= 0f && dist < float.MaxValue) return true;
                }
            }
            return false;
        }
    }

    /// <summary>Every part's verdict, in place: 1 when any sampled vertex has a ray that escapes everything, else 0.</summary>
    static void Visibility(IList<Part> parts, IList<PartMesh> meshes)
    {
        int totalVerts = meshes.Sum(pm => pm.Count), totalTris = meshes.Sum(pm => pm.Tris.Length);
        var v = new float[totalVerts * 3]; var t = new int[totalTris];
        int vo = 0, to = 0;
        foreach (var pm in meshes)
        {
            Array.Copy(pm.World, 0, v, vo * 3, pm.Count * 3);
            for (int i = 0; i < pm.Tris.Length; i++) t[to + i] = pm.Tris[i] + vo;
            vo += pm.Count; to += pm.Tris.Length;
        }
        var bvh = new TriangleBvh(v, t);
        double largest = 0; foreach (var p in parts) largest = Math.Max(largest, Math.Max(p.Size[0], Math.Max(p.Size[1], p.Size[2])));
        double eps = Math.Max(1e-4, largest * 1e-3);
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var pm = meshes[pi];
            int step = Math.Max(1, pm.Count / 30);
            bool seen = false;
            for (int i = 0; i < pm.Count && !seen; i += step)
            {
                double px = pm.World[i * 3], py = pm.World[i * 3 + 1], pz = pm.World[i * 3 + 2];
                for (int k = -1; k < FixedF32.Length && !seen; k++)
                {
                    float dx, dy, dz;
                    if (k < 0) { dx = pm.Normal[i * 3]; dy = pm.Normal[i * 3 + 1]; dz = pm.Normal[i * 3 + 2]; if (Math.Sqrt(DotVn(dx, dy, dz, dx, dy, dz)) < 0.5) continue; }
                    else { dx = FixedF32[k][0]; dy = FixedF32[k][1]; dz = FixedF32[k][2]; }
                    // the script: _bvh.ray_cast(_p + _d * _eps, _d) - the offset in float32 (eps goes through a float), the cast in Blender's
                    float e = (float)eps;
                    float ox = (float)((float)px + (float)(dx * e)), oy = (float)((float)py + (float)(dy * e)), oz = (float)((float)pz + (float)(dz * e));
                    if (!bvh.AnyHit(ox, oy, oz, dx, dy, dz)) seen = true;
                }
            }
            parts[pi].Vis = seen ? 1 : 0;
        }
    }
}
