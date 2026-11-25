// GlbDisconnectedParts.Occluders.cs - the fuse's occluders read ONCE per fuse and shared between its groups (2026-10-26).
//
// Every group's exposure test (from above, from below, from the side) asks what else of the MODEL lies in the way: every
// mesh node of the file but the group's own, transformed to world space. Until this file each group read and transformed
// all of them for itself, and FuseGroups plans its groups at once - on the Saleg's Revenge (22 groups, 3.9 million
// triangles in the file) that was 84 million triangles read in 22 parallel copies of 700 MB each, 245 s of reading and
// 139 s of registering against 98 s of everything else, with the SMALL groups the slowest (a six-part group spends a
// minute on the rest of the ship). Measured in docs/Testing.md.
//
// Here the file's nodes are read once, lazily, under a lock, as one double[] of world-space corners per node in the same
// node, primitive and face order as before; a group then sees the nodes that are not its own as SEGMENTS, in node order,
// so triangle t of its occluder set is the t-th across those segments - the numbering, and with it the registration and
// candidate order every verdict reads, is exactly what the per-group read produced. Nothing is copied per group: the
// managed walk indexes the segments through a prefix table, the native ladder (native/hafcolumn.cpp) is handed the
// segment pointers. A read that fails leaves the cache empty, as the per-group read left its list.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

public static partial class GlbDisconnectedParts
{
    /// <summary>The file's mesh nodes as world-space triangle corners, 9 doubles a triangle, one array per node (empty
    /// for a node without triangles), read once on first use and shared by every group of one fuse.</summary>
    internal sealed class OccluderCache
    {
        readonly byte[] source; readonly object gate = new object();
        readonly HashSet<int> excluded;
        double[][] nodes;   // published last: a reader that sees it non-null sees every array
        int[][] alphaRuns;  // per node: ranges of corners from BLEND or MASK primitives
        bool[] mirroredNode; // per node: its world transform mirrors, so glTF renders its triangles the other way round
        double[][] lo, hi;  // per node: the bounds of its corners (a group's bounds are those of the nodes it sees, as the per-group read's were)

        // A cache private to one group never needs that group's source corners: its welded faces are registered separately.
        // Shared caches leave excluded null because every group's nodes can occlude another group.
        public OccluderCache(byte[] source, IEnumerable<int> excluded = null)
        {
            this.source = source;
            this.excluded = excluded == null ? null : new HashSet<int>(excluded);
        }

        // THE FILE PARSED ONCE (2026-10-27): the JSON and the BIN prefix every group used to parse and copy for itself (356 MB
        // a group on the Saleg's), read here on first use under the lock and shared read-only; a parse that fails throws to
        // every caller as the per-group parse did. Each group makes its own Accessors over the shared BIN.
        Document document; byte[] bin;
        internal Document Document { get { Parsed(); return document; } }
        internal byte[] Bin { get { Parsed(); return bin; } }
        void Parsed()
        {
            if (System.Threading.Volatile.Read(ref bin) != null) return;
            lock (gate)
            {
                if (bin != null) return;
                Document parsed = Parse(source);
                BinReader(parsed, parsed.Root, out byte[] prefix);
                document = parsed;
                System.Threading.Volatile.Write(ref bin, prefix);   // published last: a reader that sees it sees the document
            }
        }

        // THE MODEL'S FRAME ONCE (2026-10-27): ModelBelly samples every mesh node of the file, and every group did it for itself
        // (40 s summed over the Saleg's 22 groups). The frame read from the file is the same for every group, so it is read
        // once; a file that yields no samples is measured per group on the group's own vertices, as before.
        bool bellyRead, bellyFromFile; int bellyLength, bellyWidth; double bellyCentre, bellyY, bellyFloor;
        internal void Belly(JArray nodes, JArray meshes, Accessors reader, List<Vec3> fallback, out int lengthAxis, out int widthAxis, out double centreW, out double bellyYOut, out double floorY)
        {
            lock (gate)
            {
                if (!bellyRead)
                {
                    var pts = new List<Vec3>(); var wts = new List<double>();
                    ModelBellySamples(nodes, meshes, reader, pts, wts); System.Threading.Interlocked.Increment(ref CountsOf(source).BellyReads);
                    bellyFromFile = pts.Count > 0;
                    if (bellyFromFile) BellyFrame(pts, wts, out bellyLength, out bellyWidth, out bellyCentre, out bellyY, out bellyFloor);
                    bellyRead = true;
                }
                if (bellyFromFile) { lengthAxis = bellyLength; widthAxis = bellyWidth; centreW = bellyCentre; bellyYOut = bellyY; floorY = bellyFloor; return; }
            }
            FallbackBellyFrame(fallback, out lengthAxis, out widthAxis, out centreW, out bellyYOut, out floorY);
        }

        // THE FILE IN ONE RAY TREE (2026-11-24, the hidden twins - GlbDisconnectedParts.Covered.cs): every node read, one
        // segment a node (so a group leaves its own nodes out by index), shared read-only by every group of the fuse.
        //   * BLEND and MASK are filtered per primitive. A canopy can share a node with an opaque frame. Copy its
        //     corners without the alpha-tested or blended runs and keep the frame in the tree.
        //   * BUILT OFF THE CACHE LOCK (same review): the build is seconds on a file of millions of triangles, and under
        //     `gate` every other group's Belly and View waited for it. The nodes are read under `gate`, the tree is built
        //     under its own lock and published last.
        //   * A MIRRORED NODE IS WOUND AS IT RENDERS: a ray stops only on a triangle that shows its front to the camera
        //     (TriBvh.Hits), and glTF draws a node with a negative determinant the other way round - as the gather of
        //     a fuse group reads it. Those nodes get a rewound copy.
        //   * ITS BOXES ARE WIDENED by a ten-millionth of the file's size: with none, a ray through a corner lying exactly
        //     on a leaf box's face missed the box (10 of 500 corner-aimed rays got through a closed grid).
        TriBvh rayTree; readonly object rayGate = new object();
        internal TriBvh RayTree()
        {
            var built = System.Threading.Volatile.Read(ref rayTree);
            if (built != null) return built;
            double[][] all = System.Threading.Volatile.Read(ref nodes);
            if (all == null) lock (gate) { if (nodes == null) Build(); all = nodes; }
            lock (rayGate)
            {
                if (rayTree != null) return rayTree;
                var solid = new double[all.Length][]; double size = 0;
                for (int ni = 0; ni < all.Length; ni++)
                {
                    solid[ni] = WithoutRuns(all[ni], alphaRuns[ni]);
                    if (mirroredNode[ni] && solid[ni].Length > 0) solid[ni] = Rewound(solid[ni], ReferenceEquals(solid[ni], all[ni]));
                    if (lo[ni] != null) for (int a = 0; a < 3; a++) if (hi[ni][a] - lo[ni][a] > size) size = hi[ni][a] - lo[ni][a];
                }
                System.Threading.Volatile.Write(ref rayTree, new TriBvh(solid, null, 1e-7 * size));
                return rayTree;
            }
        }

        // every triangle wound the other way (its second and third corners exchanged), in a copy when the array is the shared one
        static double[] Rewound(double[] corners, bool copy)
        {
            double[] r = copy ? (double[])corners.Clone() : corners;
            for (int o = 0; o + 8 < r.Length; o += 9)
                for (int a = 0; a < 3; a++) { double t = r[o + 3 + a]; r[o + 3 + a] = r[o + 6 + a]; r[o + 6 + a] = t; }
            return r;
        }

        /// <summary>The longest side of the box round every node read (0 when nothing was read).</summary>
        internal double Size(double[] alsoLo, double[] alsoHi)
        {
            if (System.Threading.Volatile.Read(ref nodes) == null) lock (gate) { if (nodes == null) Build(); }
            var l = (double[])alsoLo.Clone(); var h = (double[])alsoHi.Clone();
            for (int ni = 0; ni < lo.Length; ni++)
                if (lo[ni] != null) for (int a = 0; a < 3; a++) { if (lo[ni][a] < l[a]) l[a] = lo[ni][a]; if (hi[ni][a] > h[a]) h[a] = hi[ni][a]; }
            double size = 0; for (int a = 0; a < 3; a++) if (h[a] - l[a] > size) size = h[a] - l[a];
            return double.IsInfinity(size) || double.IsNaN(size) ? 0 : size;
        }

        // the array without the runs [first, past) listed: itself when there are none
        static double[] WithoutRuns(double[] corners, int[] runs)
        {
            if (runs == null || runs.Length == 0) return corners;
            int gone = 0; for (int r = 0; r < runs.Length; r += 2) gone += runs[r + 1] - runs[r];
            var kept = new double[corners.Length - gone]; int at = 0, from = 0;
            for (int r = 0; r < runs.Length; r += 2)
            {
                Array.Copy(corners, from, kept, at, runs[r] - from); at += runs[r] - from; from = runs[r + 1];
            }
            Array.Copy(corners, from, kept, at, corners.Length - from);
            return kept;
        }

        /// <summary>The segments of every node not in `picked`, in node order; boundsLo/Hi widened by those nodes' corners.</summary>
        public OccluderView View(ISet<int> picked, double[] boundsLo, double[] boundsHi)
        {
            var all = System.Threading.Volatile.Read(ref nodes);
            if (all == null) lock (gate) { if (nodes == null) Build(); all = nodes; }
            var segments = new List<double[]>(); var nodeOf = new List<int>();
            for (int ni = 0; ni < all.Length; ni++)
            {
                if (picked.Contains(ni) || all[ni].Length == 0) continue;
                segments.Add(all[ni]); nodeOf.Add(ni);
                for (int axis = 0; axis < 3; axis++) { boundsLo[axis] = Math.Min(boundsLo[axis], lo[ni][axis]); boundsHi[axis] = Math.Max(boundsHi[axis], hi[ni][axis]); }
            }
            return new OccluderView(segments.ToArray(), nodeOf.ToArray());
        }

        void Build()
        {
            var read = new List<double[]>(); var readLo = new List<double[]>(); var readHi = new List<double[]>(); var readAlpha = new List<int[]>(); var readMirrored = new List<bool>();
            try
            {
                Parsed();   // the fuse's one parse (already done when a plan is reading; a cache asked first parses here)
                JObject root = document.Root;
                JArray fileNodes = root["nodes"] as JArray ?? new JArray();
                JArray meshes = root["meshes"] as JArray ?? new JArray();
                JArray materials = root["materials"] as JArray;
                Accessors reader = new Accessors(root, bin);
                for (int ni = 0; ni < fileNodes.Count; ni++)
                {
                    if (excluded != null && excluded.Contains(ni))
                    {
                        read.Add(Array.Empty<double>()); readLo.Add(null); readHi.Add(null); readAlpha.Add(null); readMirrored.Add(false);
                        continue;
                    }
                    var corners = new List<double>(); List<int> runs = null; bool mirrored = false;
                    var l = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity }; var h = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
                    var node = fileNodes[ni] as JObject;
                    int mi = node?["mesh"] == null ? -1 : node.Value<int>("mesh");
                    var pl = mi < 0 || mi >= meshes.Count ? null : (meshes[mi] as JObject)?["primitives"] as JArray;
                    if (pl != null)
                    {
                        double[] world = NodeWorldMatrix(fileNodes, ni);
                        mirrored = Det3(world) < 0;
                        foreach (JObject prim in TrianglesOnly(pl))
                        {
                            var attrs = prim["attributes"] as JObject; if (attrs?["POSITION"] == null) continue;
                            int runStart = corners.Count; bool usesAlpha = MaterialUsesAlpha(materials, prim["material"] == null ? -1 : prim.Value<int>("material"));
                            int posAcc = attrs.Value<int>("POSITION"); int vertCount = reader.Count(posAcc);
                            int idxAcc = prim["indices"] == null ? -1 : prim.Value<int>("indices");
                            int faceN = (idxAcc < 0 ? vertCount : reader.Count(idxAcc)) / 3;
                            for (int f = 0; f < faceN; f++)
                            {
                                uint i0 = idxAcc < 0 ? (uint)(f * 3) : reader.Index(idxAcc, (uint)(f * 3)), i1 = idxAcc < 0 ? (uint)(f * 3 + 1) : reader.Index(idxAcc, (uint)(f * 3 + 1)), i2 = idxAcc < 0 ? (uint)(f * 3 + 2) : reader.Index(idxAcc, (uint)(f * 3 + 2));
                                if (i0 >= vertCount || i1 >= vertCount || i2 >= vertCount) continue;
                                Vec3 a = XForm(world, reader.Position(posAcc, i0)), b = XForm(world, reader.Position(posAcc, i1)), c = XForm(world, reader.Position(posAcc, i2));
                                corners.Add(a.X); corners.Add(a.Y); corners.Add(a.Z); corners.Add(b.X); corners.Add(b.Y); corners.Add(b.Z); corners.Add(c.X); corners.Add(c.Y); corners.Add(c.Z);
                                UpdateBounds(l, h, a); UpdateBounds(l, h, b); UpdateBounds(l, h, c);
                            }
                            if (usesAlpha && corners.Count > runStart) { (runs ?? (runs = new List<int>())).Add(runStart); runs.Add(corners.Count); }
                        }
                    }
                    read.Add(corners.ToArray()); readLo.Add(l); readHi.Add(h); readAlpha.Add(runs?.ToArray()); readMirrored.Add(mirrored);
                }
            }
            catch (Exception) { read.Clear(); readLo.Clear(); readHi.Clear(); readAlpha.Clear(); readMirrored.Clear(); }
            lo = readLo.ToArray(); hi = readHi.ToArray(); alphaRuns = readAlpha.ToArray(); mirroredNode = readMirrored.ToArray();
            System.Threading.Volatile.Write(ref nodes, read.ToArray());
        }
    }

    /// <summary>One group's occluders: the shared segments of the nodes that are not its own, indexed by corner as the
    /// per-group list was (corner 3t, 3t + 1, 3t + 2 are triangle t's), without a copy.</summary>
    internal sealed class OccluderView
    {
        public readonly double[][] Segments; public readonly int[] NodeOf;   // per segment: the file node it is
        readonly int[] firstCorner;   // per segment: its first corner number
        public readonly int Count;  // corners, 3 per triangle

        public OccluderView(double[][] segments, int[] nodeOf)
        {
            Segments = segments; NodeOf = nodeOf; firstCorner = new int[segments.Length];
            int corners = 0;
            for (int s = 0; s < segments.Length; s++) { firstCorner[s] = corners; corners = checked(corners + segments[s].Length / 3); }
            Count = corners;
        }

        public Vec3 this[int corner]
        {
            get
            {
                int s = Array.BinarySearch(firstCorner, corner);
                if (s < 0) s = ~s - 1;
                int at = (corner - firstCorner[s]) * 3; var seg = Segments[s];
                return new Vec3 { X = seg[at], Y = seg[at + 1], Z = seg[at + 2] };
            }
        }
    }
}
