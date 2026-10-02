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
    sealed class PartMesh { public float[] World; public float[] Normal; public int[] Tris; public int Count; }

    static readonly double[][] FixedDirections = MakeFixedDirections();
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
    static PartMesh BuildPartMesh(HafModel m, int node, int onlyPrim, int[] onlyVerts, double[] objWorld, Func<HafPrimitive, int, double[]> dataPosition, Func<HafPrimitive, int, double[]> dataNormal)
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
        // a vertex without a file normal: Blender's computed one - the faces' normals weighted by the corner angle; none to sum = the position's direction
        var computed = new double[n * 3];
        for (int t = 0; t < tris.Count; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            double ax = local[a * 3], ay = local[a * 3 + 1], az = local[a * 3 + 2], bx = local[b * 3], by = local[b * 3 + 1], bz = local[b * 3 + 2], cx = local[c * 3], cy = local[c * 3 + 1], cz = local[c * 3 + 2];
            double ux = bx - ax, uy = by - ay, uz = bz - az, vx = cx - ax, vy = cy - ay, vz = cz - az;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl == 0) continue;
            nx /= nl; ny /= nl; nz /= nl;
            double wa = CornerAngle(bx - ax, by - ay, bz - az, cx - ax, cy - ay, cz - az), wb = CornerAngle(ax - bx, ay - by, az - bz, cx - bx, cy - by, cz - bz), wc = CornerAngle(ax - cx, ay - cy, az - cz, bx - cx, by - cy, bz - cz);
            computed[a * 3] += nx * wa; computed[a * 3 + 1] += ny * wa; computed[a * 3 + 2] += nz * wa;
            computed[b * 3] += nx * wb; computed[b * 3 + 1] += ny * wb; computed[b * 3 + 2] += nz * wb;
            computed[c * 3] += nx * wc; computed[c * 3 + 1] += ny * wc; computed[c * 3 + 2] += nz * wc;
        }
        var pm = new PartMesh { Count = n, World = new float[n * 3], Normal = new float[n * 3], Tris = tris.ToArray() };
        for (int i = 0; i < n; i++)
        {
            double lx = local[i * 3], ly = local[i * 3 + 1], lz = local[i * 3 + 2];
            double nx, ny, nz;
            if (!double.IsNaN(fileNormal[i * 3])) { nx = fileNormal[i * 3]; ny = fileNormal[i * 3 + 1]; nz = fileNormal[i * 3 + 2]; }
            else
            {
                nx = computed[i * 3]; ny = computed[i * 3 + 1]; nz = computed[i * 3 + 2];
                if (nx == 0 && ny == 0 && nz == 0) { nx = lx; ny = ly; nz = lz; }
            }
            var w = HafTransforms.Apply(objWorld, lx, ly, lz, 1.0);
            pm.World[i * 3] = (float)w[0]; pm.World[i * 3 + 1] = (float)-w[2]; pm.World[i * 3 + 2] = (float)w[1];
            var d = HafTransforms.Apply(objWorld, nx, ny, nz, 0.0);   // matrix_world.to_3x3() @ normal, NOT the inverse transpose - the script normalizes what it gets
            double dl = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            if (dl > 0) { pm.Normal[i * 3] = (float)(d[0] / dl); pm.Normal[i * 3 + 1] = (float)(-d[2] / dl); pm.Normal[i * 3 + 2] = (float)(d[1] / dl); }
        }
        return pm;
    }

    static double CornerAngle(double ux, double uy, double uz, double vx, double vy, double vz)
    {
        double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz), vl = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        if (ul == 0 || vl == 0) return 0;
        double c = (ux * vx + uy * vy + uz * vz) / (ul * vl);
        return Math.Acos(c < -1 ? -1 : c > 1 ? 1 : c);
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

        /// <summary>Does the ray from (ox, oy, oz) along the unit direction (dx, dy, dz) meet any triangle at t >= 0? The
        /// watertight ray-triangle test Blender's BVHTree uses (Woop, Benthin, Wald 2013): the ray's largest axis
        /// becomes z, the triangle is sheared into that frame, the barycentric signs decide; a hit on an edge or a
        /// vertex counts, from either side.</summary>
        public bool AnyHit(double ox, double oy, double oz, double dx, double dy, double dz)
        {
            double adx = Math.Abs(dx), ady = Math.Abs(dy), adz = Math.Abs(dz);
            int kz = adx > ady ? (adx > adz ? 0 : 2) : (ady > adz ? 1 : 2);
            int kx = (kz + 1) % 3, ky = (kx + 1) % 3;
            double[] dir = { dx, dy, dz };
            if (dir[kz] < 0) { int tmp = kx; kx = ky; ky = tmp; }
            double sx = dir[kx] / dir[kz], sy = dir[ky] / dir[kz], sz = 1.0 / dir[kz];
            double ix = dx != 0 ? 1.0 / dx : double.PositiveInfinity, iy = dy != 0 ? 1.0 / dy : double.PositiveInfinity, iz = dz != 0 ? 1.0 / dz : double.PositiveInfinity;
            var stack = new int[128]; int sp = 0; stack[sp++] = 0;
            var A = new double[3]; var B = new double[3]; var C = new double[3];
            while (sp > 0)
            {
                int node = stack[--sp];
                double t0 = 0, t1 = double.PositiveInfinity;   // the slab test, t >= 0
                double a = (bmin[node * 3] - ox) * ix, b = (bmax[node * 3] - ox) * ix;
                if (dx == 0) { if (ox < bmin[node * 3] || ox > bmax[node * 3]) continue; } else { if (a > b) { double tmp = a; a = b; b = tmp; } if (a > t0) t0 = a; if (b < t1) t1 = b; }
                a = (bmin[node * 3 + 1] - oy) * iy; b = (bmax[node * 3 + 1] - oy) * iy;
                if (dy == 0) { if (oy < bmin[node * 3 + 1] || oy > bmax[node * 3 + 1]) continue; } else { if (a > b) { double tmp = a; a = b; b = tmp; } if (a > t0) t0 = a; if (b < t1) t1 = b; }
                a = (bmin[node * 3 + 2] - oz) * iz; b = (bmax[node * 3 + 2] - oz) * iz;
                if (dz == 0) { if (oz < bmin[node * 3 + 2] || oz > bmax[node * 3 + 2]) continue; } else { if (a > b) { double tmp = a; a = b; b = tmp; } if (a > t0) t0 = a; if (b < t1) t1 = b; }
                if (t0 > t1 * (1 + 1e-9) + 1e-12) continue;
                if (count[node] == 0) { stack[sp++] = left[node]; stack[sp++] = left[node] + 1; continue; }
                for (int i = start[node]; i < start[node] + count[node]; i++)
                {
                    int ia = t[i * 3] * 3, ib = t[i * 3 + 1] * 3, ic = t[i * 3 + 2] * 3;
                    A[0] = v[ia] - ox; A[1] = v[ia + 1] - oy; A[2] = v[ia + 2] - oz;
                    B[0] = v[ib] - ox; B[1] = v[ib + 1] - oy; B[2] = v[ib + 2] - oz;
                    C[0] = v[ic] - ox; C[1] = v[ic + 1] - oy; C[2] = v[ic + 2] - oz;
                    double Ax = A[kx] - sx * A[kz], Ay = A[ky] - sy * A[kz], Bx = B[kx] - sx * B[kz], By = B[ky] - sy * B[kz], Cx = C[kx] - sx * C[kz], Cy = C[ky] - sy * C[kz];
                    double U = Cx * By - Cy * Bx, V = Ax * Cy - Ay * Cx, W = Bx * Ay - By * Ax;
                    if ((U < 0 || V < 0 || W < 0) && (U > 0 || V > 0 || W > 0)) continue;
                    double det = U + V + W;
                    if (det == 0) continue;
                    double T = U * sz * A[kz] + V * sz * B[kz] + W * sz * C[kz];
                    if ((det < 0 && T > 0) || (det > 0 && T < 0)) continue;   // t = T / det would be negative: behind the ray
                    return true;
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
                for (int k = -1; k < FixedDirections.Length && !seen; k++)
                {
                    double dx, dy, dz;
                    if (k < 0) { dx = pm.Normal[i * 3]; dy = pm.Normal[i * 3 + 1]; dz = pm.Normal[i * 3 + 2]; if (dx * dx + dy * dy + dz * dz < 0.25) continue; }
                    else { dx = FixedDirections[k][0]; dy = FixedDirections[k][1]; dz = FixedDirections[k][2]; }
                    if (!bvh.AnyHit(px + dx * eps, py + dy * eps, pz + dz * eps, dx, dy, dz)) seen = true;
                }
            }
            parts[pi].Vis = seen ? 1 : 0;
        }
    }
}
