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
            var read = new List<double[]>(); var readLo = new List<double[]>(); var readHi = new List<double[]>();
            try
            {
                Parsed();   // the fuse's one parse (already done when a plan is reading; a cache asked first parses here)
                JObject root = document.Root;
                JArray fileNodes = root["nodes"] as JArray ?? new JArray();
                JArray meshes = root["meshes"] as JArray ?? new JArray();
                Accessors reader = new Accessors(root, bin);
                for (int ni = 0; ni < fileNodes.Count; ni++)
                {
                    if (excluded != null && excluded.Contains(ni))
                    {
                        read.Add(Array.Empty<double>()); readLo.Add(null); readHi.Add(null);
                        continue;
                    }
                    var corners = new List<double>();
                    var l = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity }; var h = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
                    var node = fileNodes[ni] as JObject;
                    int mi = node?["mesh"] == null ? -1 : node.Value<int>("mesh");
                    var pl = mi < 0 || mi >= meshes.Count ? null : (meshes[mi] as JObject)?["primitives"] as JArray;
                    if (pl != null)
                    {
                        double[] world = NodeWorldMatrix(fileNodes, ni);
                        foreach (JObject prim in TrianglesOnly(pl))
                        {
                            var attrs = prim["attributes"] as JObject; if (attrs?["POSITION"] == null) continue;
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
                        }
                    }
                    read.Add(corners.ToArray()); readLo.Add(l); readHi.Add(h);
                }
            }
            catch (Exception) { read.Clear(); readLo.Clear(); readHi.Clear(); }
            lo = readLo.ToArray(); hi = readHi.ToArray();
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
