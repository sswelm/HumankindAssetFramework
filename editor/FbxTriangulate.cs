// The native FBX reader, step 3: triangulation. ufbx_catch_triangulate_face (ufbx 0.23.1, tools/fbx-drill/ufbx/ufbx.c
// ~32400) ported formula for formula: a triangle passes through, a quad splits along its shorter diagonal unless a
// vertex crosses that diagonal's plane, and an n-gon is ear-clipped in the plane of its Newell normal with the reflex
// corners held in a KD tree (ufbxi_triangulate_ngon ~28497, ufbxi_kd_build / ufbxi_kd_check ~28300). Every rounding
// step is the same as ufbx's in double: tools/fbx_drill.sh compares every triangle of every face with ufbx's own.
//
// The KD tree is kept rather than replaced by a scan of the reflex corners: it decides which corners are TESTED
// against a candidate ear, and the closed-triangle test it runs can answer "inside" for a corner a scan would also
// test but the tree's bounding-interval pruning never reaches (near-degenerate triangles). Same tree, same answer.
using System;
using System.IO;

public static class FbxTriangulate
{
    const double Epsilon = 1.4916681462400413e-154;   // UFBX_EPSILON for a double ufbx_real: squares stay normal

    struct V3 { public double X, Y, Z; public V3(double x, double y, double z) { X = x; Y = y; Z = z; } }
    struct V2 { public double X, Y; }

    static V3 Sub(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    static V3 Mul(V3 a, double b) => new V3(a.X * b, a.Y * b, a.Z * b);
    static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static V3 Cross(V3 a, V3 b) => new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    static double Length(V3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    static V3 Normalize(V3 a) { double len = Math.Sqrt(Dot(a, a)); return len > Epsilon ? Mul(a, 1.0 / len) : new V3(0, 0, 0); }
    static double DistSq(V2 a, V2 b) { double dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }
    static double Orient2d(V2 a, V2 b, V2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    /// <summary>C's fmax: a NaN operand loses to the other, unlike Math.Max.</summary>
    static double FMax(double a, double b) => FbxMath.FMax(a, b);

    /// <summary>Every face of the mesh as corner indices (into the mesh's index space, as ufbx), three per triangle,
    /// faces in order; NumTriangles * 3 entries. A face under three corners yields nothing.</summary>
    public static int[] Mesh(FbxMeshData m)
    {
        var dst = new int[m.NumTriangles * 3];
        int at = 0;
        for (int f = 0; f < m.NumFaces; f++) at += 3 * Face(m, f, dst, at);
        if (at != dst.Length) throw new InvalidDataException($"FBX mesh '{m.Name}': triangulated {at / 3} triangles, {m.NumTriangles} expected");
        return dst;
    }

    /// <summary>ufbx_catch_triangulate_face: the face's triangles into dst at the offset; returns their number.</summary>
    public static int Face(FbxMeshData m, int face, int[] dst, int at)
    {
        int begin = m.FaceBegin[face], n = m.FaceSize[face];
        if (n < 3) return 0;
        if (n == 3)
        {
            dst[at] = begin; dst[at + 1] = begin + 1; dst[at + 2] = begin + 2;
            return 1;
        }
        if (n == 4)
        {
            // Quad: split along the shortest axis unless a vertex crosses the axis
            V3 v0 = Pos(m, begin), v1 = Pos(m, begin + 1), v2 = Pos(m, begin + 2), v3 = Pos(m, begin + 3);
            V3 a = Sub(v2, v0), b = Sub(v3, v1);
            V3 na1 = Normalize(Cross(a, Sub(v1, v0)));
            V3 na3 = Normalize(Cross(a, Sub(v0, v3)));
            V3 nb0 = Normalize(Cross(b, Sub(v1, v0)));
            V3 nb2 = Normalize(Cross(b, Sub(v2, v1)));
            double dotAa = Dot(a, a), dotBb = Dot(b, b), dotNa = Dot(na1, na3), dotNb = Dot(nb0, nb2);
            bool splitA = dotAa <= dotBb;
            if (dotNa < 0.0 || dotNb < 0.0) splitA = dotNa >= dotNb;
            if (splitA) { dst[at] = begin; dst[at + 1] = begin + 1; dst[at + 2] = begin + 2; dst[at + 3] = begin + 2; dst[at + 4] = begin + 3; dst[at + 5] = begin; }
            else { dst[at] = begin + 1; dst[at + 1] = begin + 2; dst[at + 2] = begin + 3; dst[at + 3] = begin + 3; dst[at + 4] = begin; dst[at + 5] = begin + 1; }
            return 2;
        }
        return new Ngon(m, begin, n).Run(dst, at);
    }

    static V3 Pos(FbxMeshData m, int index) => new V3(m.Position.Get(index, 0), m.Position.Get(index, 1), m.Position.Get(index, 2));

    /// <summary>ufbx_get_weighted_face_normal: the cross of a triangle's edges, of a quad's diagonals, Newell's method past four.</summary>
    static V3 WeightedFaceNormal(FbxMeshData m, int begin, int n)
    {
        if (n < 3) return new V3(0, 0, 0);
        if (n == 3) { V3 a = Pos(m, begin), b = Pos(m, begin + 1), c = Pos(m, begin + 2); return Cross(Sub(b, a), Sub(c, a)); }
        if (n == 4) { V3 a = Pos(m, begin), b = Pos(m, begin + 1), c = Pos(m, begin + 2), d = Pos(m, begin + 3); return Cross(Sub(c, a), Sub(d, b)); }
        var result = new V3(0, 0, 0);
        for (int i = 0; i < n; i++)
        {
            int next = i + 1 < n ? i + 1 : 0;
            V3 a = Pos(m, begin + i), b = Pos(m, begin + next);
            result.X += (a.Y - b.Y) * (a.Z + b.Z);
            result.Y += (a.Z - b.Z) * (a.X + b.X);
            result.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        return result;
    }

    /// <summary>ufbxi_ngon_context and ufbxi_triangulate_ngon for one face of five corners or more.</summary>
    sealed class Ngon
    {
        readonly FbxMeshData m; readonly int begin, n;
        readonly V3[] axes = new V3[3];
        int[] kd;              // the reflex corners, arranged as ufbxi_kd_build leaves them (a median tree in a flat list)

        public Ngon(FbxMeshData mesh, int indexBegin, int numIndices) { m = mesh; begin = indexBegin; n = numIndices; }

        V3 Point(int index) => Pos(m, begin + index);
        V2 Project(int index) { V3 p = Point(index); return new V2 { X = Dot(axes[0], p), Y = Dot(axes[1], p) }; }

        /// <summary>ufbxi_ngon_tri_weight: the ear's quality, -1 for a corner turning the wrong way.</summary>
        static double TriWeight(V2 p0, V2 p1, V2 p2)
        {
            double orient = Orient2d(p0, p1, p2);
            if (orient <= 0.0) return -1.0;
            double a = DistSq(p0, p1), b = DistSq(p1, p2), c = DistSq(p2, p0);
            double ab = (a + b - c) / Math.Sqrt(4.0 * a * b);
            double bc = (b + c - a) / Math.Sqrt(4.0 * b * c);
            double ca = (c + a - b) / Math.Sqrt(4.0 * c * a);
            return FMax(Epsilon, 2.0 - FMax(FMax(ab, bc), ca));
        }

        public int Run(int[] dst, int at)
        {
            // Form an orthonormal basis to project the polygon into a 2D plane
            V3 normal = WeightedFaceNormal(m, begin, n);
            double len = Length(normal);
            if (len > Epsilon) normal = Mul(normal, 1.0 / len);
            else normal = new V3(1, 0, 0);
            V3 axis = normal.X * normal.X < 0.5 ? new V3(1, 0, 0) : new V3(0, 1, 0);
            axes[0] = Normalize(Cross(axis, normal));
            axes[1] = Normalize(Cross(normal, axes[0]));
            axes[2] = normal;

            // Collect all the reflex corners for intersection testing
            var reflex = new int[n]; int numReflex = 0;
            {
                V2 a = Project(n - 1), b = Project(0);
                for (int i = 0; i < n; i++)
                {
                    int next = i + 1 < n ? i + 1 : 0;
                    V2 c = Project(next);
                    if (Orient2d(a, b, c) <= 0.0) reflex[numReflex++] = i;
                    a = b; b = c;
                }
            }
            kd = new int[numReflex]; Array.Copy(reflex, kd, numReflex);
            KdBuild(0, numReflex, 0);

            // edges[2i] = the previous corner of i, edges[2i+1] = the next; the high bit marks a clipped corner
            var edges = new uint[n * 2];
            for (int i = 0; i < n; i++)
            {
                edges[i * 2] = (uint)(i > 0 ? i - 1 : n - 1);
                edges[i * 2 + 1] = (uint)(i + 1 < n ? i + 1 : 0);
            }

            // Core of the ear clipping algorithm: walk the corners, clip the better of the two ears at hand when
            // no reflex corner lies in it, step back one on a clip; walking the polygon twice without a clip ends it.
            int indicesLeft = n;
            {
                var pi = new uint[] { 0, 1, 2, 3 };
                var points = new V2[4]; var weights = new double[2];
                int numSteps = 0;
                while (indicesLeft > 3)
                {
                    points[0] = Project((int)pi[0]); points[1] = Project((int)pi[1]); points[2] = Project((int)pi[2]); points[3] = Project((int)pi[3]);
                    weights[0] = TriWeight(points[0], points[1], points[2]);
                    weights[1] = TriWeight(points[1], points[2], points[3]);
                    int firstSide = weights[1] > weights[0] ? 1 : 0;
                    bool clipped = false;
                    for (int sideIx = 0; sideIx < 2; sideIx++)
                    {
                        int side = sideIx ^ firstSide;
                        if (!(weights[side] >= 0.0)) break;
                        if (!KdCheck(points[side], points[side + 1], points[side + 2], pi[side], pi[side + 1], pi[side + 2]))
                        {
                            uint ia = pi[side], ib = pi[side + 1], ic = pi[side + 2];
                            edges[ib * 2] |= 0x80000000u; edges[ib * 2 + 1] |= 0x80000000u;
                            edges[ic * 2] = ia; edges[ia * 2 + 1] = ic;
                            indicesLeft -= 1;
                            numSteps = 0;
                            if (side == 1) { pi[2] = pi[3]; pi[3] = edges[pi[3] * 2 + 1]; }
                            else { pi[1] = pi[0]; pi[0] = edges[pi[0] * 2]; }
                            clipped = true;
                            break;
                        }
                    }
                    if (clipped) continue;
                    pi[0] = pi[1]; pi[1] = pi[2]; pi[2] = pi[3]; pi[3] = edges[pi[3] * 2 + 1];
                    numSteps++;
                    if (numSteps >= n * 2) break;
                }
                // Fallback: cut non-ears until the polygon is completed
                uint ix = pi[1];
                while (indicesLeft > 3)
                {
                    uint prev = edges[ix * 2], next = edges[ix * 2 + 1];
                    edges[ix * 2] |= 0x80000000u; edges[ix * 2 + 1] |= 0x80000000u;
                    edges[prev * 2 + 1] = next; edges[next * 2] = prev;
                    indicesLeft -= 1;
                    ix = next;
                }
                edges[ix * 2] |= 0x80000000u; edges[ix * 2 + 1] |= 0x80000000u;
            }

            // Expand the adjacency into triangles, corners in order
            int numTriangles = 0;
            for (int ix = 0; ix < n; ix++)
            {
                uint prev = edges[ix * 2], next = edges[ix * 2 + 1];
                if ((prev & 0x80000000u) == 0) continue;
                int d = at + numTriangles * 3;
                dst[d] = begin + (int)(prev & 0x7fffffff);
                dst[d + 1] = begin + ix;
                dst[d + 2] = begin + (int)(next & 0x7fffffff);
                numTriangles++;
            }
            if (numTriangles != n - 2) throw new InvalidDataException($"FBX mesh '{m.Name}': a {n}-gon clipped into {numTriangles} triangles");
            return numTriangles;
        }

        // ---- the KD tree over the reflex corners (ufbxi_kd_build / ufbxi_kd_check_fast / ufbxi_kd_check_slow) -------------
        // The tree is the list itself: a range [b, b+count) is a node whose median element kd[b + count/2] is the split,
        // the range before it the left child, the range after it the right, axes alternating with depth. ufbx caches
        // the first levels in a node array and compacts the rest; the same ranges, the same medians, the same visits.

        double Coord(int index, int axis) => Dot(axes[axis], Point(index));

        void KdBuild(int b, int count, int axis)
        {
            while (count > 0)
            {
                StableSort(b, count, axis);
                int numLeft = count / 2, beginRight = b + numLeft + 1, numRight = count - (numLeft + 1);
                KdBuild(b, numLeft, axis ^ 1);
                b = beginRight; count = numRight; axis ^= 1;
            }
        }

        /// <summary>ufbxi_stable_sort over kd[b, b+count) by the projected coordinate: insertion sort in blocks of 16,
        /// then a bottom-up merge taking from the right block only when strictly less.</summary>
        void StableSort(int b, int count, int axis)
        {
            var keys = new double[count];
            for (int i = 0; i < count; i++) keys[i] = Coord(kd[b + i], axis);
            var data = new int[count]; Array.Copy(kd, b, data, 0, count);
            const int block = 16;
            for (int bs = 0; bs < count; bs += block)
            {
                int end = Math.Min(bs + block, count);
                for (int i = bs + 1; i < end; i++)
                {
                    if (!(keys[i] < keys[i - 1])) continue;
                    double k = keys[i]; int v = data[i];
                    int j = i - 1;
                    keys[i] = keys[j]; data[i] = data[j];
                    for (; j != bs; --j)
                    {
                        if (!(k < keys[j - 1])) break;
                        keys[j] = keys[j - 1]; data[j] = data[j - 1];
                    }
                    keys[j] = k; data[j] = v;
                }
            }
            // ping-pong between two buffers: each pass merges src's blocks into dst, then dst becomes the next src
            var srcK = keys; var srcD = data; var dstK = new double[count]; var dstD = new int[count];
            for (int size = block; size < count; size *= 2)
            {
                for (int bs = 0; bs < count; bs += size * 2)
                {
                    int i = bs, iEnd = Math.Min(bs + size, count), j = iEnd, jEnd = Math.Min(j + size, count), k = bs;
                    while (i < iEnd && j < jEnd)
                    {
                        if (srcK[j] < srcK[i]) { dstK[k] = srcK[j]; dstD[k] = srcD[j]; j++; }
                        else { dstK[k] = srcK[i]; dstD[k] = srcD[i]; i++; }
                        k++;
                    }
                    for (; i < iEnd; i++, k++) { dstK[k] = srcK[i]; dstD[k] = srcD[i]; }
                    for (; j < jEnd; j++, k++) { dstK[k] = srcK[j]; dstD[k] = srcD[j]; }
                }
                var sk = srcK; srcK = dstK; dstK = sk; var sd = srcD; srcD = dstD; dstD = sd;
            }
            Array.Copy(srcD, 0, kd, b, count);
        }

        bool KdCheck(V2 p0, V2 p1, V2 p2, uint i0, uint i1, uint i2)
        {
            double minX = Math.Min(Math.Min(p0.X, p1.X), p2.X), minY = Math.Min(Math.Min(p0.Y, p1.Y), p2.Y);
            double maxX = Math.Max(Math.Max(p0.X, p1.X), p2.X), maxY = Math.Max(Math.Max(p0.Y, p1.Y), p2.Y);
            return KdCheckRange(0, kd.Length, 0, p0, p1, p2, i0, i1, i2, minX, minY, maxX, maxY);
        }

        bool KdCheckPoint(int index, V2 p0, V2 p1, V2 p2, uint i0, uint i1, uint i2)
        {
            if (index == i0 || index == i1 || index == i2) return false;
            V2 p = Project(index);
            double u = Orient2d(p, p0, p1), v = Orient2d(p, p1, p2), w = Orient2d(p, p2, p0);
            if (u <= 0.0 && v <= 0.0 && w <= 0.0) return true;
            if (u >= 0.0 && v >= 0.0 && w >= 0.0) return true;
            return false;
        }

        bool KdCheckRange(int b, int count, int axis, V2 p0, V2 p1, V2 p2, uint i0, uint i1, uint i2, double minX, double minY, double maxX, double maxY)
        {
            while (count > 0)
            {
                int numLeft = count / 2, beginRight = b + numLeft + 1, numRight = count - (numLeft + 1);
                int index = kd[b + numLeft];
                double split = Coord(index, axis);
                bool hitLeft = (axis == 0 ? minX : minY) <= split;
                bool hitRight = (axis == 0 ? maxX : maxY) >= split;
                if (hitLeft && hitRight)
                {
                    if (KdCheckPoint(index, p0, p1, p2, i0, i1, i2)) return true;
                    if (KdCheckRange(beginRight, numRight, axis ^ 1, p0, p1, p2, i0, i1, i2, minX, minY, maxX, maxY)) return true;
                }
                axis ^= 1;
                if (hitLeft) count = numLeft;
                else { b = beginRight; count = numRight; }
            }
            return false;
        }
    }
}
