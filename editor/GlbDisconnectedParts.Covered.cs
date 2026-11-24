// GlbDisconnectedParts.Covered.cs - the fuse's HIDDEN TWINS (2026-11-24, the RSS Centurion's upper deck).
//
// The ship's wood deck (one part, facing up) lies in the very same plane as the superstructure's own top face (another
// part, wound DOWN, 1.09 million square units of the deck's 1.15 million): two faces in one place, one of which no
// camera outside the ship can ever see. Every stage carried both to the game, and the game showed the hidden one in
// polygon-shaped patches across the deck (the two are triangulated differently, so each wins in places) and wholly on
// the aft mast, whose grey skin has a brown copy wound inward on the same triangles. The main deck has no such twin
// and was clean. Measured in docs/Testing.md ("Hidden twins").
//
// So a fuse leaves ONE surface where two coincide and only one can be seen:
//
//   * CANDIDATE - a face most of whose sample points (its centroid and at least four of six more) lie ON a face of the
//     same group wound the other way: within the fuse's own rounding distance of its plane, inside its outline, normals
//     opposed. Triangulation and material do not matter; position and winding do.
//   * HIDDEN - a face is seen when a ray from one of those points reaches open air along one of the directions the
//     game's cameras look from (CoveredViews: straight above, and eight ways round at 15, 40 and 65 degrees above the
//     horizon), that camera being in front of the face. A candidate none of whose rays get out is left out when the
//     other side of its plane IS seen: the front of a face it lies on.
//   * FROM ABOVE THE HORIZON, not from everywhere. Rays over the face's whole hemisphere were the second cut, and the
//     ship refused that too: of the part whose ceiling this was written for, 36 faces went where 59 go now - the rest
//     let a ray out sideways from under the deck, and stayed. No camera of the game is down there. The fuse's other
//     exposure tests ask the same thing the same way (from above, from the side). It follows that the UNDERSIDE of a
//     level sheet doubled in the open (an awning, a wing made of two coincident skins) is left out: nothing above the
//     horizon faces it.
//   * PER FACE, not per patch. A vote over a connected patch was the first cut and the drill refused it: over 26 culled
//     views of the Centurion, 284 pixels of 2.4 million lost a grey face that had plainly been visible, the deck showing
//     behind it. The inside of a tube open at the top is the shape of that mistake: hidden over most of its height,
//     seen at the rim, and a patch that votes takes the rim with it (the funnel fixture).
//
// What is KEPT, on purpose: a pair both of whose sides see the outside (a sheet made two-sided by duplication - a
// flag, a boat's shell with a painted inside - needs both under back-face culling), and a pair neither side of which
// does (buried: whatever the renderer does with it, nobody sees it). Removing a face whose front no camera can see
// changes nothing a culling renderer draws for those cameras; that is the rule's whole safety, and why it needs no switch.
//
// Rays are stopped by every triangle of the FILE (the group's own welded faces and every other node, as the exposure
// tests of the winding pass read them), except glass: a face of a BLEND material, or a node made only of them, hides
// nothing. Two trees of boxes (TriBvh): one per group over its own faces, for the coincidence test and its share of the
// rays, and ONE for the fuse over every other node of the file (OccluderCache.RayTree), built when the first group with
// a candidate asks and shared by them all.
//   A UNIFORM GRID was the first structure, and HMS Roberts refused it: one of its groups is 961,668 faces of dense
//   detail, nearly every one doubled, and a grid cell there holds thousands of triangles - 31 s to find the coincident
//   faces and 82 s of rays in that group, where the whole fuse had taken 43 s. A tree does not care how the triangles
//   are spread. A grid of the whole file per GROUP was dropped for the same ship's sake: a copy of the file's index for
//   every group planned at once.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

public static partial class GlbDisconnectedParts
{
    internal sealed class CoveredFaces
    {
        public bool[] Drop;                 // per face: left out of the fused mesh
        public int Candidates, Removed, TwoSidedKept, BuriedKept, OtherKept;
        public long CoincideMs, RayMs;      // finding the coincident faces (the group's tree included); the rays (the file's tree included, for the first group to ask)
    }

    /// <summary>Does anything stop a ray from (ox, oy, oz) along the unit direction (dx, dy, dz)?</summary>
    internal delegate bool RayStop(double ox, double oy, double oz, double dx, double dy, double dz);

    const int CoveredSamples = 7;
    // WHERE THE GAME LOOKS FROM: straight above, and eight ways round at 15, 40 and 65 degrees above the horizon (Y is up,
    // as everywhere in the fuse). A unit is never seen from below the horizon, and a camera sees a face only from in front.
    internal const double CoveredLowestCamera = 15.0;
    const double CoveredFacing = 0.05;   // a camera within three degrees of edge-on sees nothing of a face
    static readonly double[] CoveredViews = BuildCoveredViews();
    static double[] BuildCoveredViews()
    {
        var v = new List<double> { 0, 1, 0 };
        double[] rings = { 65.0, 40.0, CoveredLowestCamera };   // highest first: most of what is seen at all is seen from high up, and the first ray out ends the question
        for (int r = 0; r < rings.Length; r++)
            for (int k = 0; k < 8; k++)
            {
                double up = rings[r] * Math.PI / 180.0, round = (k + 0.5 * (r % 2)) * Math.PI / 4.0;
                v.Add(Math.Cos(up) * Math.Cos(round)); v.Add(Math.Sin(up)); v.Add(Math.Cos(up) * Math.Sin(round));
            }
        return v.ToArray();
    }

    /// <summary>Triangles in a tree of boxes, flat arrays. A triangle is `segment` + index (9 doubles a triangle); the
    /// tree is split at the middle of its triangles' centroids along their longest spread, down to `leafSize`.</summary>
    internal sealed class TriBvh
    {
        const int MaxDepth = 56;
        readonly double[][] segs;
        internal readonly int[] RefSeg, RefTri;     // the triangles, ordered so that every leaf is one run
        internal readonly double[] Box;             // six a node: min x y z, max x y z, widened by the pad
        internal readonly int[] First, Count;       // a leaf: its run (Count > 0). An inner node: First = its left child, the right is First + 1, Count = 0
        public readonly bool Empty; public readonly double Extent;   // the longest side of the whole
        [ThreadStatic] static int[] stack;
        internal static int[] Stack() => stack ?? (stack = new int[2 * MaxDepth + 16]);

        public TriBvh(double[][] segments, bool[] skipFirst, double pad, int leafSize = 8)
        {
            segs = segments;
            int total = 0;
            for (int s = 0; s < segs.Length; s++) total += segs[s].Length / 9;
            var rs = new int[total]; var rt = new int[total]; int n = 0;
            for (int s = 0; s < segs.Length; s++)
            {
                double[] seg = segs[s]; int count = seg.Length / 9;
                for (int t = 0; t < count; t++)
                {
                    if (s == 0 && skipFirst != null && skipFirst[t]) continue;
                    if (!Finite(seg, t * 9)) continue;
                    rs[n] = s; rt[n] = t; n++;
                }
            }
            RefSeg = rs; RefTri = rt; Empty = n == 0;
            if (Empty) { Box = new double[6]; First = new int[1]; Count = new int[1]; return; }
            var box = new List<double>(); var first = new List<int>(); var cnt = new List<int>();
            var todo = new Stack<int>();   // node, start, count, depth
            void Node(int start, int count, int depth) { first.Add(start); cnt.Add(count); for (int i = 0; i < 6; i++) box.Add(0); todo.Push(depth); todo.Push(count); todo.Push(start); todo.Push(first.Count - 1); }
            Node(0, n, 0);
            var lo = new double[3]; var hi = new double[3]; var clo = new double[3]; var chi = new double[3];
            while (todo.Count > 0)
            {
                int node = todo.Pop(), start = todo.Pop(), count = todo.Pop(), depth = todo.Pop();
                for (int a = 0; a < 3; a++) { lo[a] = clo[a] = double.PositiveInfinity; hi[a] = chi[a] = double.NegativeInfinity; }
                for (int i = start; i < start + count; i++)
                {
                    double[] seg = segs[rs[i]]; int o = rt[i] * 9;
                    for (int a = 0; a < 3; a++)
                    {
                        double p = seg[o + a], q = seg[o + 3 + a], r = seg[o + 6 + a];
                        double mn = p < q ? (p < r ? p : r) : (q < r ? q : r), mx = p > q ? (p > r ? p : r) : (q > r ? q : r), c = (p + q + r) / 3.0;
                        if (mn < lo[a]) lo[a] = mn; if (mx > hi[a]) hi[a] = mx; if (c < clo[a]) clo[a] = c; if (c > chi[a]) chi[a] = c;
                    }
                }
                for (int a = 0; a < 3; a++) { box[6 * node + a] = lo[a] - pad; box[6 * node + 3 + a] = hi[a] + pad; }
                int axis = chi[0] - clo[0] >= chi[1] - clo[1] && chi[0] - clo[0] >= chi[2] - clo[2] ? 0 : chi[1] - clo[1] >= chi[2] - clo[2] ? 1 : 2;
                if (count <= leafSize || depth >= MaxDepth || !(chi[axis] > clo[axis])) continue;   // a leaf: first/cnt already hold its run
                double mid = 0.5 * (clo[axis] + chi[axis]);
                int l = start, h = start + count - 1;
                while (l <= h)
                {
                    double[] seg = segs[rs[l]]; int o = rt[l] * 9;
                    if ((seg[o + axis] + seg[o + 3 + axis] + seg[o + 6 + axis]) / 3.0 < mid) l++;
                    else { int ts = rs[l], tt = rt[l]; rs[l] = rs[h]; rt[l] = rt[h]; rs[h] = ts; rt[h] = tt; h--; }
                }
                int left = l - start;
                if (left == 0 || left == count) left = count / 2;   // every centroid on one side of the middle (rounding): halve the run as it stands
                cnt[node] = 0; first[node] = first.Count;
                Node(start, left, depth + 1); Node(start + left, count - left, depth + 1);
            }
            Box = box.ToArray(); First = first.ToArray(); Count = cnt.ToArray();
            Extent = Math.Max(Box[3] - Box[0], Math.Max(Box[4] - Box[1], Box[5] - Box[2]));
        }

        static bool Finite(double[] seg, int o)
        {
            for (int i = 0; i < 9; i++) { double v = seg[o + i]; if (double.IsNaN(v) || double.IsInfinity(v)) return false; }
            return true;
        }

        // Möller-Trumbore on the flat corners: does the ray meet this triangle beyond its origin?
        static bool Hits(double[] seg, int o, double ox, double oy, double oz, double dx, double dy, double dz)
        {
            double e1x = seg[o + 3] - seg[o], e1y = seg[o + 4] - seg[o + 1], e1z = seg[o + 5] - seg[o + 2];
            double e2x = seg[o + 6] - seg[o], e2y = seg[o + 7] - seg[o + 1], e2z = seg[o + 8] - seg[o + 2];
            double px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
            double det = e1x * px + e1y * py + e1z * pz;
            if (Math.Abs(det) < 1e-18) return false;
            double inv = 1.0 / det;
            double tx = ox - seg[o], ty = oy - seg[o + 1], tz = oz - seg[o + 2];
            double u = (tx * px + ty * py + tz * pz) * inv; if (u < -1e-9 || u > 1 + 1e-9) return false;
            double qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x;
            double v = (dx * qx + dy * qy + dz * qz) * inv; if (v < -1e-9 || u + v > 1 + 1e-9) return false;
            return (e2x * qx + e2y * qy + e2z * qz) * inv > 0;
        }

        /// <summary>Does anything stop a ray from `o` along `d`? `skipSeg` leaves whole segments out (per segment),
        /// `skipTri` triangles of the FIRST segment (per triangle); null = none.</summary>
        public bool Blocked(double ox, double oy, double oz, double dx, double dy, double dz, bool[] skipSeg, bool[] skipTri)
        {
            if (Empty) return false;
            int[] st = Stack(); int sp = 0; st[sp++] = 0;
            bool flatX = Math.Abs(dx) < 1e-300, flatY = Math.Abs(dy) < 1e-300, flatZ = Math.Abs(dz) < 1e-300;
            double ix = flatX ? 0 : 1.0 / dx, iy = flatY ? 0 : 1.0 / dy, iz = flatZ ? 0 : 1.0 / dz;
            while (sp > 0)
            {
                int node = st[--sp], b = 6 * node;
                // the ray against the node's box (slabs), from its origin onwards
                double t0 = 0, t1 = double.PositiveInfinity;
                if (flatX) { if (ox < Box[b] || ox > Box[b + 3]) continue; }
                else { double ta = (Box[b] - ox) * ix, tb = (Box[b + 3] - ox) * ix; if (ta > tb) { double s = ta; ta = tb; tb = s; } if (ta > t0) t0 = ta; if (tb < t1) t1 = tb; if (t0 > t1) continue; }
                if (flatY) { if (oy < Box[b + 1] || oy > Box[b + 4]) continue; }
                else { double ta = (Box[b + 1] - oy) * iy, tb = (Box[b + 4] - oy) * iy; if (ta > tb) { double s = ta; ta = tb; tb = s; } if (ta > t0) t0 = ta; if (tb < t1) t1 = tb; if (t0 > t1) continue; }
                if (flatZ) { if (oz < Box[b + 2] || oz > Box[b + 5]) continue; }
                else { double ta = (Box[b + 2] - oz) * iz, tb = (Box[b + 5] - oz) * iz; if (ta > tb) { double s = ta; ta = tb; tb = s; } if (ta > t0) t0 = ta; if (tb < t1) t1 = tb; if (t0 > t1) continue; }
                int count = Count[node], first = First[node];
                if (count == 0) { st[sp++] = first; st[sp++] = first + 1; continue; }
                for (int i = first; i < first + count; i++)
                {
                    int s = RefSeg[i], t = RefTri[i];
                    if ((skipSeg != null && skipSeg[s]) || (skipTri != null && s == 0 && skipTri[t])) continue;
                    if (Hits(segs[s], t * 9, ox, oy, oz, dx, dy, dz)) return true;
                }
            }
            return false;
        }
        public bool Blocked(double ox, double oy, double oz, double dx, double dy, double dz) => Blocked(ox, oy, oz, dx, dy, dz, null, null);
    }

    /// <summary>The faces a fuse leaves out because they lie exactly on a face wound the other way and no camera sees
    /// them (see the head of this file).</summary>
    /// <param name="corners">nine doubles a face, in its FINAL winding</param>
    /// <param name="tol">how far apart "the same plane" may be: the fuse's rounding distance</param>
    /// <param name="glass">per face: it stops no ray (a BLEND material); null = none</param>
    /// <param name="others">what else of the file stops a ray; asked for only when a candidate exists (null = nothing)</param>
    internal static CoveredFaces FindCoveredFaces(double[] corners, double tol, bool[] glass, Func<RayStop> others, int maxParallel = -1)
    {
        int faceCount = corners.Length / 9;
        var found = new CoveredFaces { Drop = new bool[faceCount] };
        if (faceCount < 2) return found;
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxParallel };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // unit normals; a face without one (collapsed, a needle) is neither covered nor a cover
        var unit = new double[3 * faceCount]; var valid = new bool[faceCount]; var skip = new bool[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            int o = f * 9;
            double ax = corners[o + 3] - corners[o], ay = corners[o + 4] - corners[o + 1], az = corners[o + 5] - corners[o + 2];
            double bx = corners[o + 6] - corners[o], by = corners[o + 7] - corners[o + 1], bz = corners[o + 8] - corners[o + 2];
            double nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!(l > 1e-18) || double.IsInfinity(l)) { skip[f] = true; continue; }
            unit[3 * f] = nx / l; unit[3 * f + 1] = ny / l; unit[3 * f + 2] = nz / l; valid[f] = true;
        }
        var own = new TriBvh(new[] { corners }, skip, tol);
        if (own.Empty) return found;

        // the face's sample points: its centroid, three points towards its corners, three towards its edges
        void Sample(int f, int k, out double x, out double y, out double z)
        {
            int o = f * 9;
            double cx = (corners[o] + corners[o + 3] + corners[o + 6]) / 3.0, cy = (corners[o + 1] + corners[o + 4] + corners[o + 7]) / 3.0, cz = (corners[o + 2] + corners[o + 5] + corners[o + 8]) / 3.0;
            if (k == 0) { x = cx; y = cy; z = cz; return; }
            double tx, ty, tz;
            if (k <= 3) { int c = (k - 1) * 3; tx = corners[o + c]; ty = corners[o + c + 1]; tz = corners[o + c + 2]; }
            else { int c = (k - 4) * 3, n = ((k - 4 + 1) % 3) * 3; tx = (corners[o + c] + corners[o + n]) * 0.5; ty = (corners[o + c + 1] + corners[o + n + 1]) * 0.5; tz = (corners[o + c + 2] + corners[o + n + 2]) * 0.5; }
            x = cx + 0.7 * (tx - cx); y = cy + 0.7 * (ty - cy); z = cz + 0.7 * (tz - cz);
        }
        // does face g, wound the other way than f, hold the point: in its plane within the rounding, inside its outline
        bool Holds(int f, int g, double x, double y, double z)
        {
            if (g == f) return false;
            double gx = unit[3 * g], gy = unit[3 * g + 1], gz = unit[3 * g + 2];
            if (unit[3 * f] * gx + unit[3 * f + 1] * gy + unit[3 * f + 2] * gz > -0.999) return false;
            int o = g * 9;
            double px = x - corners[o], py = y - corners[o + 1], pz = z - corners[o + 2];
            if (Math.Abs(px * gx + py * gy + pz * gz) > tol) return false;
            // inside the outline (barycentric, in the cover's plane)
            double e1x = corners[o + 3] - corners[o], e1y = corners[o + 4] - corners[o + 1], e1z = corners[o + 5] - corners[o + 2];
            double e2x = corners[o + 6] - corners[o], e2y = corners[o + 7] - corners[o + 1], e2z = corners[o + 8] - corners[o + 2];
            double d11 = e1x * e1x + e1y * e1y + e1z * e1z, d12 = e1x * e2x + e1y * e2y + e1z * e2z, d22 = e2x * e2x + e2y * e2y + e2z * e2z;
            double dp1 = px * e1x + py * e1y + pz * e1z, dp2 = px * e2x + py * e2y + pz * e2z;
            double den = d11 * d22 - d12 * d12; if (Math.Abs(den) < 1e-300) return false;
            double u = (d22 * dp1 - d12 * dp2) / den, v = (d11 * dp2 - d12 * dp1) / den;
            return u >= -1e-6 && v >= -1e-6 && u + v <= 1 + 1e-6;
        }
        // the face of the group, wound the other way, that the point lies on: -1 when there is none. Every leaf of the
        // group's tree whose box (widened by the rounding) holds the point is read; the tree holds only faces with a normal.
        int CoverAt(int f, double x, double y, double z)
        {
            int[] st = TriBvh.Stack(); int sp = 0; st[sp++] = 0;
            double[] box = own.Box;
            while (sp > 0)
            {
                int node = st[--sp], b = 6 * node;
                if (x < box[b] || x > box[b + 3] || y < box[b + 1] || y > box[b + 4] || z < box[b + 2] || z > box[b + 5]) continue;
                int count = own.Count[node], first = own.First[node];
                if (count == 0) { st[sp++] = first; st[sp++] = first + 1; continue; }
                for (int i = first; i < first + count; i++) { int g = own.RefTri[i]; if (Holds(f, g, x, y, z)) return g; }
            }
            return -1;
        }
        var candidateFaces = new List<int>(); var coversOf = new Dictionary<int, int[]>();
        var gate = new object();
        Parallel.For(0, faceCount, options, f =>
        {
            if (!valid[f]) return;
            Sample(f, 0, out double x, out double y, out double z);
            int first = CoverAt(f, x, y, z);
            if (first < 0) return;
            var covers = new int[CoveredSamples]; covers[0] = first; int covered = 1;
            for (int k = 1; k < CoveredSamples; k++) { Sample(f, k, out x, out y, out z); covers[k] = CoverAt(f, x, y, z); if (covers[k] >= 0) covered++; }
            if (covered < 5) return;
            lock (gate) { candidateFaces.Add(f); coversOf.Add(f, covers); }
        });
        found.Candidates = candidateFaces.Count; found.CoincideMs = clock.ElapsedMilliseconds; clock.Restart();
        if (candidateFaces.Count == 0) return found;
        candidateFaces.Sort();

        // rays, against the whole file: the group's own faces (the tree above; glass stops nothing) and whatever else the
        // caller has. Is the face's FRONT seen by a camera? Stops at the first ray that gets out.
        RayStop rest = others?.Invoke();
        bool[] noStop = null;
        if (glass != null) for (int f = 0; f < faceCount; f++) if (glass[f]) { noStop = glass; break; }
        double lift = 8 * tol;
        // HOW MANY POINTS OF A FACE ASK (measured on the Centurion, 2026-11-24: seven points of every face cost 36 s of
        // rays over 22 groups, most of it on faces so small that every point of them sees what the centroid sees): the
        // centroid alone for a face shorter than a 256th of the group, four points up to eight times that, all seven beyond.
        var askPoints = new byte[faceCount];
        {
            double small = own.Extent / 256.0, large = 8 * small;
            foreach (int f in candidateFaces)
            {
                int o = f * 9; double longest2 = 0;
                for (int e = 0; e < 3; e++)
                {
                    int a = o + 3 * e, b = o + 3 * ((e + 1) % 3);
                    double ex = corners[a] - corners[b], ey = corners[a + 1] - corners[b + 1], ez = corners[a + 2] - corners[b + 2];
                    longest2 = Math.Max(longest2, ex * ex + ey * ey + ez * ez);
                }
                askPoints[f] = (byte)(longest2 < small * small ? 1 : longest2 < large * large ? 4 : CoveredSamples);
            }
        }
        bool SeenFrom(int f, double sgn)
        {
            double nx = sgn * unit[3 * f], ny = sgn * unit[3 * f + 1], nz = sgn * unit[3 * f + 2];
            for (int d = 0; d < CoveredViews.Length; d += 3)
            {
                double dx = CoveredViews[d], dy = CoveredViews[d + 1], dz = CoveredViews[d + 2];
                if (nx * dx + ny * dy + nz * dz < CoveredFacing) continue;   // that camera looks at the back of this side
                for (int k = 0; k < askPoints[f]; k++)
                {
                    Sample(f, k, out double x, out double y, out double z);
                    double ox = x + nx * lift, oy = y + ny * lift, oz = z + nz * lift;
                    if (!own.Blocked(ox, oy, oz, dx, dy, dz, null, noStop) && (rest == null || !rest(ox, oy, oz, dx, dy, dz))) return true;
                }
            }
            return false;
        }
        var indexOf = new Dictionary<int, int>(candidateFaces.Count);
        for (int i = 0; i < candidateFaces.Count; i++) indexOf[candidateFaces[i]] = i;
        var frontSeen = new bool[candidateFaces.Count];
        Parallel.For(0, candidateFaces.Count, options, i => frontSeen[i] = SeenFrom(candidateFaces[i], 1));
        // A face goes when its own front is seen from nowhere and the other side of its plane is seen from somewhere: the
        // front of a face it lies on (already asked, when that face is a candidate itself - the usual case, a pair covers
        // each other) or, where one of the faces it lies on is not a candidate and so was never asked (a small plate on a
        // large deck), rays from its own points the other way.
        var verdict = new byte[candidateFaces.Count];   // 1 = left out, 2 = both sides seen, 3 = neither side seen
        Parallel.For(0, candidateFaces.Count, options, i =>
        {
            int f = candidateFaces[i]; int[] covers = coversOf[f];
            bool otherSeen = false, unasked = false;
            foreach (int g in covers)
            {
                if (g < 0) continue;
                if (!indexOf.TryGetValue(g, out int gi)) { unasked = true; continue; }
                if (frontSeen[gi]) { otherSeen = true; break; }
            }
            if (!otherSeen && unasked) otherSeen = SeenFrom(f, -1);
            verdict[i] = frontSeen[i] ? (otherSeen ? (byte)2 : (byte)0) : otherSeen ? (byte)1 : (byte)3;
        });
        found.RayMs = clock.ElapsedMilliseconds;
        for (int i = 0; i < candidateFaces.Count; i++)
        {
            if (verdict[i] == 1) { found.Drop[candidateFaces[i]] = true; found.Removed++; }
            else if (verdict[i] == 2) found.TwoSidedKept++;
            else if (verdict[i] == 3) found.BuriedKept++;
            else found.OtherKept++;   // its own front is seen and nothing sees the other side: the face that shows
        }
        return found;
    }

    // a node all of whose triangle primitives use a BLEND material: glass, which hides nothing behind it
    static bool MaterialIsBlend(JArray materials, int material) =>
        materials != null && material >= 0 && material < materials.Count && (string)(materials[material] as JObject)?["alphaMode"] == "BLEND";

    static bool NodeIsGlass(JArray nodes, JArray meshes, JArray materials, int nodeIndex)
    {
        var node = nodeIndex >= 0 && nodeIndex < nodes.Count ? nodes[nodeIndex] as JObject : null;
        int mi = node?["mesh"] == null ? -1 : node.Value<int>("mesh");
        var primitives = mi < 0 || mi >= meshes.Count ? null : (meshes[mi] as JObject)?["primitives"] as JArray;
        if (primitives == null) return false;
        int seen = 0;
        foreach (JObject primitive in TrianglesOnly(primitives))
        {
            if (!MaterialIsBlend(materials, primitive["material"] == null ? -1 : primitive.Value<int>("material"))) return false;
            seen++;
        }
        return seen > 0;
    }
}
