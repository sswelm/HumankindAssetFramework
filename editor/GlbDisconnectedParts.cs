// GlbDisconnectedParts.cs — losslessly expose disconnected geometry islands as separate GLB scene parts.
// The original accessors/materials/skins/textures stay byte-for-byte in the BIN. Only filtered index accessors,
// meshes and child nodes are appended, so this does not incur Blender import/export reinterpretation.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif

public static class GlbDisconnectedParts
{
    const uint Magic = 0x46546C67;
    const uint JsonChunk = 0x4E4F534A;
    const uint BinChunk = 0x004E4942;

    public sealed class Result
    {
        public byte[] Bytes;
        public int MeshesSplit;
        public int NodesSplit;
        public int ChildPartsCreated;
        public int SourceTriangles;
        public int OutputTriangles;
        public readonly List<string> Details = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool Changed => NodesSplit > 0;
        // FUSE (2026-09-15) — what the weld and the winding pass did, so the Workshop can say it in one line
        public int VerticesBefore, VerticesAfter, IslandsBefore, IslandsAfter, FacesRewound;
        public readonly List<string> IslandLines = new List<string>();   // EVERY sheet's verdict, largest first (the report; Details keeps the largest six for the status)
        public int FusedNodeIndex = -1; public string FusedNodeName;   // the appended shell (a fuse only): the output sidecar names it with its group's letter
    }

    sealed class Chunk
    {
        public uint Type;
        public byte[] Data;
    }

    sealed class Document
    {
        public JObject Root;
        public readonly List<Chunk> Chunks = new List<Chunk>();
        public int JsonIndex;
        public int BinIndex;
    }

    struct VertexKey : IEquatable<VertexKey>
    {
        public int Accessor;
        public uint Index;
        public bool Equals(VertexKey other) => Accessor == other.Accessor && Index == other.Index;
        public override bool Equals(object obj) => obj is VertexKey && Equals((VertexKey)obj);
        public override int GetHashCode() => unchecked(Accessor * 397 ^ (int)Index);
    }

    // PACKED PAIR KEYS ((a << 32) | b: an edge by its two vertex classes, a face pair, a class+material) hash to a ^ b under
    // long.GetHashCode — adjacent classes give tiny, colliding hashes and every dictionary of edges degenerated to a chain
    // walk (2026-09-18: the Romanic's 99,000-face group Q spent 11 s building and reading its edge tables). Mixed here.
    sealed class PairKeyComparer : IEqualityComparer<long>
    {
        public static readonly PairKeyComparer Instance = new PairKeyComparer();
        public bool Equals(long x, long y) => x == y;
        public int GetHashCode(long x) { unchecked { ulong z = (ulong)x * 0x9E3779B97F4A7C15UL; z ^= z >> 29; return (int)(z >> 32) ^ (int)z; } }
    }

    struct PositionKey : IEquatable<PositionKey>
    {
        public long X, Y, Z;
        public bool Equals(PositionKey other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is PositionKey && Equals((PositionKey)obj);
        public override int GetHashCode()
        {
            unchecked { return ((X.GetHashCode() * 397) ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode(); }
        }
    }

    struct Vec3
    {
        public double X, Y, Z;
        public double this[int axis] => axis == 0 ? X : axis == 1 ? Y : Z;
    }

    sealed class Vertex
    {
        public VertexKey Key;
        public Vec3 Position;
    }

    sealed class Triangle
    {
        public int Primitive;
        public uint A, B, C;
        public int VA, VB, VC;
        public int Order;
    }

    sealed class Component
    {
        public readonly Dictionary<int, List<uint>> Indices = new Dictionary<int, List<uint>>();
        public readonly double[] Min = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        public readonly double[] Max = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        public int FirstTriangle = int.MaxValue;
        public int TriangleCount;
        public readonly List<Vec3> Points = new List<Vec3>();   // sample positions for the direction test (distance merge)

        public Vec3 Centroid()
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in Points) { x += p.X; y += p.Y; z += p.Z; }
            int n = Math.Max(1, Points.Count);
            return new Vec3 { X = x / n, Y = y / n, Z = z / n };
        }

        // DIRECTION + ELONGATION in one pass (the dashed-line problem): a rope/trim dash is ELONGATED, so the
        // merge may only reach ALONG its axis — a hull trim line and a sail luff line that pass close never
        // chain into one part through a crossing ("vertices must not be connected indirectly"). Blobby islands
        // (aspect < 3) merge on distance alone.
        // Elongation is judged in the island's OWN frame (review finding 7, 2026-09-07): the old axis-aligned
        // bbox aspect read a 45-degree dash as blobby (two equal extents), so parallel DIAGONAL dashed lines
        // bypassed the direction gate entirely and fused — the exact failure the gate was built to prevent.
        // PC1 by power iteration (the galley oar method), PC2 by iterating the SAME covariance while projecting
        // each step orthogonal to PC1 (no explicit deflation, so no deflation drift); aspect =
        // sqrt(var1/var2) is rotation-invariant and matches the old extent-ratio threshold for uniform dashes.
        // The PC1 seed leans on the bbox diagonal (plus small fixed offsets) so it cannot start exactly
        // perpendicular to the true axis.
        public void PrincipalAxes(out Vec3 pc1, out double aspect)
        {
            Vec3 m = Centroid();
            var cov = new double[3, 3];
            foreach (var p in Points)
            {
                double[] d = { p.X - m.X, p.Y - m.Y, p.Z - m.Z };
                for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) cov[a, b] += d[a] * d[b];
            }
            double v1x, v1y, v1z;
            double var1 = PowerIterate(cov, Max[0] - Min[0] + 0.017, Max[1] - Min[1] + 0.013, Max[2] - Min[2] + 0.011,
                                       0, 0, 0, out v1x, out v1y, out v1z);
            pc1 = new Vec3 { X = v1x, Y = v1y, Z = v1z };
            double var2 = PowerIterate(cov, 0.31, 0.68, 0.55, v1x, v1y, v1z, out _, out _, out _);
            aspect = Math.Sqrt((var1 + 1e-24) / Math.Max(var2, 1e-24));
        }

        // Power iteration on a 3x3 covariance; with a non-zero (ox,oy,oz) every step (seed included) is projected
        // orthogonal to it, so the iteration converges to the dominant eigenvector of the orthogonal subspace
        // (= PC2 when given PC1). Returns the variance along the converged direction (Rayleigh quotient).
        static double PowerIterate(double[,] c, double sx, double sy, double sz, double ox, double oy, double oz,
                                   out double vx, out double vy, out double vz)
        {
            bool ortho = ox != 0 || oy != 0 || oz != 0;
            vx = sx; vy = sy; vz = sz;
            if (ortho)
            {
                double sd = vx * ox + vy * oy + vz * oz;
                vx -= sd * ox; vy -= sd * oy; vz -= sd * oz;
                if (vx * vx + vy * vy + vz * vz < 1e-18)
                {   // seed was parallel to the excluded axis — build a guaranteed perpendicular via a cross product
                    if (Math.Abs(ox) <= Math.Abs(oy) && Math.Abs(ox) <= Math.Abs(oz)) { vx = 0; vy = -oz; vz = oy; }
                    else if (Math.Abs(oy) <= Math.Abs(oz)) { vx = oz; vy = 0; vz = -ox; }
                    else { vx = -oy; vy = ox; vz = 0; }
                }
            }
            double len = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            if (len < 1e-12) { vx = 1; vy = 0; vz = 0; len = 1; }
            vx /= len; vy /= len; vz /= len;
            for (int i = 0; i < 24; i++)
            {
                double nx = c[0, 0] * vx + c[0, 1] * vy + c[0, 2] * vz;
                double ny = c[1, 0] * vx + c[1, 1] * vy + c[1, 2] * vz;
                double nz = c[2, 0] * vx + c[2, 1] * vy + c[2, 2] * vz;
                if (ortho)
                {
                    double d = nx * ox + ny * oy + nz * oz;
                    nx -= d * ox; ny -= d * oy; nz -= d * oz;
                }
                double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (l < 1e-12) break;
                vx = nx / l; vy = ny / l; vz = nz / l;
            }
            double rx = c[0, 0] * vx + c[0, 1] * vy + c[0, 2] * vz;
            double ry = c[1, 0] * vx + c[1, 1] * vy + c[1, 2] * vz;
            double rz = c[2, 0] * vx + c[2, 1] * vy + c[2, 2] * vz;
            return Math.Max(0, vx * rx + vy * ry + vz * rz);
        }
    }

    sealed class MeshPlan
    {
        public int MeshIndex;
        public string Name;
        public JObject Mesh;
        public List<Component> Components;
        public int TriangleCount;
        public string TearNote;   // Tear's report line for this mesh (null for Split)
        public readonly List<int> NewMeshIndices = new List<int>();
    }

    sealed class DisjointSet
    {
        readonly List<int> parent = new List<int>();
        readonly List<byte> rank = new List<byte>();
        public int Add() { int i = parent.Count; parent.Add(i); rank.Add(0); return i; }
        public int Find(int value)
        {
            int root = value;
            while (parent[root] != root) root = parent[root];
            while (parent[value] != value) { int next = parent[value]; parent[value] = root; value = next; }
            return root;
        }
        public void Union(int left, int right)
        {
            int a = Find(left), b = Find(right);
            if (a == b) return;
            if (rank[a] < rank[b]) { int t = a; a = b; b = t; }
            parent[b] = a;
            if (rank[a] == rank[b]) rank[a]++;
        }
    }

    sealed class Accessors
    {
        readonly byte[] bin;
        readonly JArray accessors;
        readonly JArray views;

        // PER-ACCESSOR LAYOUT CACHE (2026-09-19, the Model Splitter's 86 s Probe on a 214 MB GLB): every element read
        // used to walk the accessor and its bufferView through the JSON again — string-keyed JObject lookups, boxed
        // ints and a fresh double[] per index and per position — 28 s of a 30 s Analyze. The layout is resolved ONCE
        // per accessor, with the same checks in the same order (the messages are the contract the tests lock; a
        // view problem is remembered and thrown on the first read, as before), and an element read is an offset
        // multiply and a BitConverter call. Results are byte-identical: nothing about the decode changed.
        sealed class Layout
        {
            public int Count, ComponentType, Bytes, Have, Stride; public long Start; public bool Normalized; public string Type;
            public string ViewError;   // the ReadElement exception this accessor's view would raise, thrown when a read is attempted
        }
        readonly Dictionary<int, Layout> layouts = new Dictionary<int, Layout>();

        public Accessors(JObject root, byte[] bin)
        {
            this.bin = bin;
            accessors = root["accessors"] as JArray ?? throw new InvalidDataException("GLB has no accessors array.");
            views = root["bufferViews"] as JArray ?? throw new InvalidDataException("GLB has no bufferViews array.");
        }

        public int Count(int accessorIndex) => Resolve(accessorIndex).Count;

        public Vec3 Position(int accessorIndex, uint index)
        {
            Layout a = Resolve(accessorIndex);
            if (a.Type != "VEC3") throw new InvalidDataException("POSITION accessor is not VEC3.");
            int at = ElementOffset(a, index, 3);
            return new Vec3 { X = ReadComponent(bin, at, a.ComponentType, a.Normalized), Y = ReadComponent(bin, at + a.Bytes, a.ComponentType, a.Normalized), Z = ReadComponent(bin, at + 2 * a.Bytes, a.ComponentType, a.Normalized) };
        }

        public uint Index(int accessorIndex, uint index)
        {
            Layout a = Resolve(accessorIndex);
            if (a.Type != "SCALAR") throw new InvalidDataException("Index accessor is not SCALAR.");
            if (a.ComponentType != 5121 && a.ComponentType != 5123 && a.ComponentType != 5125)
                throw new InvalidDataException("Index accessor must use unsigned byte, ushort, or uint.");
            return checked((uint)ReadComponent(bin, ElementOffset(a, index, 1), a.ComponentType, a.Normalized));
        }

        // Any vector attribute (NORMAL, TEXCOORD_n …): the element's leading `components`, normalized ints decoded.
        public double[] Vector(int accessorIndex, uint index, int components)
        {
            Layout a = Resolve(accessorIndex);
            if (a.Have < components) throw new InvalidDataException("Accessor " + a.Type + " holds fewer than " + components + " components.");
            int at = ElementOffset(a, index, a.Have);
            var result = new double[a.Have];
            for (int i = 0; i < a.Have; i++) result[i] = ReadComponent(bin, at + i * a.Bytes, a.ComponentType, a.Normalized);
            return result;
        }

        Layout Resolve(int index)
        {
            if (layouts.TryGetValue(index, out Layout cached)) return cached;
            if (index < 0 || index >= accessors.Count) throw new InvalidDataException("Accessor index is out of range.");
            var a = accessors[index] as JObject ?? throw new InvalidDataException("Accessor is not an object.");
            if (a["sparse"] != null) throw new InvalidDataException("Sparse accessors are not supported by this splitter.");
            var l = new Layout { Count = a.Value<int>("count"), Type = (string)a["type"], ComponentType = a.Value<int>("componentType"), Normalized = a.Value<bool?>("normalized") ?? false };
            l.Have = l.Type == "SCALAR" ? 1 : l.Type == "VEC2" ? 2 : l.Type == "VEC3" ? 3 : l.Type == "VEC4" ? 4 : 0;
            JObject view = null;
            if (a["bufferView"] == null) l.ViewError = "Accessor has no bufferView (compressed/implicit data is unsupported).";
            else
            {
                int viewIndex = a.Value<int>("bufferView");
                if (viewIndex < 0 || viewIndex >= views.Count) l.ViewError = "bufferView index is out of range.";
                else if ((view = views[viewIndex] as JObject) == null) l.ViewError = "bufferView is not an object.";
                else if (view.Value<int?>("buffer").GetValueOrDefault() != 0) l.ViewError = "Only GLB buffer 0 can be edited losslessly.";
                else if (view["extensions"]?["EXT_meshopt_compression"] != null) l.ViewError = "Meshopt-compressed bufferViews are not supported.";
            }
            if (l.ViewError == null)
            {
                l.Bytes = ComponentBytes(l.ComponentType);   // throws "Unsupported GLB component type" here, as the old read did after the view checks
                l.Stride = view.Value<int?>("byteStride") ?? -1;   // -1 = ABSENT (tightly packed, sized per read); a declared 0 stays 0 and is rejected below, as it was before the cache
                l.Start = (long)(view.Value<int?>("byteOffset") ?? 0) + (a.Value<int?>("byteOffset") ?? 0);
            }
            layouts[index] = l;
            return l;
        }

        // The BIN offset of element `index`, with the old per-read checks in the old order.
        int ElementOffset(Layout a, uint index, int components)
        {
            if (index >= a.Count) throw new InvalidDataException("Accessor element is out of range.");
            if (a.ViewError != null) throw new InvalidDataException(a.ViewError);
            int stride = a.Stride >= 0 ? a.Stride : a.Bytes * components;
            if (stride < a.Bytes * components) throw new InvalidDataException("bufferView byteStride is smaller than its element.");
            long start = a.Start + (long)index * stride;
            if (start < 0 || start + a.Bytes * components > bin.Length) throw new InvalidDataException("Accessor reads beyond the BIN chunk.");
            return checked((int)start);
        }
    }

    // The declared prefix of the BIN chunk as its own array (a copy, as the old LINQ Take().ToArray() was — the
    // callers append to it), without enumerating 200 MB byte by byte.
    static byte[] Prefix(byte[] source, int length)
    {
        int n = Math.Max(0, Math.Min(length, source.Length));
        var copy = new byte[n]; Buffer.BlockCopy(source, 0, copy, 0, n); return copy;
    }

    static int ComponentBytes(int type)
    {
        if (type == 5120 || type == 5121) return 1;
        if (type == 5122 || type == 5123) return 2;
        if (type == 5125 || type == 5126) return 4;
        throw new InvalidDataException("Unsupported GLB component type " + type + ".");
    }

    static double ReadComponent(byte[] data, int offset, int type, bool normalized)
    {
        switch (type)
        {
            case 5120: { sbyte v = unchecked((sbyte)data[offset]); return normalized ? Math.Max(v / 127.0, -1.0) : v; }
            case 5121: { byte v = data[offset]; return normalized ? v / 255.0 : v; }
            case 5122: { short v = BitConverter.ToInt16(data, offset); return normalized ? Math.Max(v / 32767.0, -1.0) : v; }
            case 5123: { ushort v = BitConverter.ToUInt16(data, offset); return normalized ? v / 65535.0 : v; }
            case 5125: return BitConverter.ToUInt32(data, offset);
            case 5126: return BitConverter.ToSingle(data, offset);
            default: throw new InvalidDataException("Unsupported GLB component type " + type + ".");
        }
    }

    // ONE ROW PER MESH-CARRYING NODE, for a picker UI (the Model Workshop): how many disconnected islands the
    // node's mesh holds (1 = nothing to split), its triangle count, and — when the analyzer must skip it — why.
    // Read-only: nothing is written, so probing a 500k-triangle ship is safe and fast.
    public sealed class PartInfo
    {
        public int NodeIndex;    // THE stable identity for selective splitting — names can be null or duplicated
        public string NodeName;
        public string MeshName;
        public int Triangles;
        public int Islands;
        public string Blocked;   // non-null = unsupported for splitting (compressed, instanced, non-triangle…)
        // For the Workshop's list filters (2026-09-16, the Vehicle Lab's sliders brought over): the node's WORLD-space
        // bounding box from the POSITION accessors' min/max (required by glTF, so no vertex is read) through the node's
        // world matrix, and its vertex count. Min/Max stay null where an accessor has no min/max — such a part is never hidden.
        public int Vertices;
        public double[] Min, Max;
        public int ParentIndex = -1;   // the node's parent (-1 at the root): a _Part_NNN / _CutA child inherits its parent's ⊕ letter (WorkshopRules.TransferLetters)
    }

    public static List<PartInfo> Analyze(byte[] source) => Analyze(source, 0);

    public static List<PartInfo> Analyze(byte[] source, double mergeFraction)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        Document document = Parse(source);
        JObject root = document.Root;
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        JArray meshes = root["meshes"] as JArray ?? throw new InvalidDataException("GLB has no meshes array.");
        JArray buffers = root["buffers"] as JArray ?? throw new InvalidDataException("GLB has no buffers array.");
        if (buffers.Count != 1 || buffers[0]?["uri"] != null)
            throw new InvalidDataException("Lossless splitting requires one embedded GLB buffer.");
        byte[] sourceBin = document.Chunks[document.BinIndex].Data;
        var reader = new Accessors(root, Prefix(sourceBin, buffers[0].Value<int>("byteLength")));

        var byMesh = new Dictionary<int, MeshPlan>();
        var blockedByMesh = new Dictionary<int, string>();
        var infos = new List<PartInfo>();
        var parentOf = new Dictionary<int, int>();
        for (int i = 0; i < nodes.Count; i++)
            if ((nodes[i] as JObject)?["children"] is JArray kids)
                foreach (JToken kid in kids) { int ci = kid.Value<int>(); if (!parentOf.ContainsKey(ci)) parentOf.Add(ci, i); }
        for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
        {
            var node = nodes[nodeIndex] as JObject;
            if (node?["mesh"] == null) continue;
            int meshIndex = node.Value<int>("mesh");
            var info = new PartInfo { NodeIndex = nodeIndex, NodeName = (string)node["name"] ?? ("node " + nodeIndex), MeshName = (string)meshes[meshIndex]?["name"] ?? ("mesh " + meshIndex) };
            MeasureNode(root, nodes, nodeIndex, meshes[meshIndex] as JObject, info);
            info.ParentIndex = parentOf.TryGetValue(nodeIndex, out int pi) ? pi : -1;
            if (node["extensions"]?["EXT_mesh_gpu_instancing"] != null)
                info.Blocked = "GPU-instanced node";
            else if (blockedByMesh.TryGetValue(meshIndex, out string why))
                info.Blocked = why;
            else if (!byMesh.TryGetValue(meshIndex, out MeshPlan plan))
            {
                try { byMesh[meshIndex] = plan = AnalyzeMesh(meshIndex, (JObject)meshes[meshIndex], reader, keepSingle: true, mergeFraction: mergeFraction); }
                catch (Exception ex) when (ex is InvalidDataException || ex is OverflowException)
                { blockedByMesh[meshIndex] = info.Blocked = ex.Message; }
            }
            if (info.Blocked == null && byMesh.TryGetValue(meshIndex, out MeshPlan p) && p != null)
            { info.Triangles = p.TriangleCount; info.Islands = p.Components.Count; }
            else if (info.Blocked == null) { info.Islands = 1; }   // empty/primitive-less mesh: nothing to split
            infos.Add(info);
        }
        return infos;
    }

    // world bbox + vertex count of a node's mesh from accessor min/max (see PartInfo); never throws — a part the
    // file does not describe simply carries no box
    static void MeasureNode(JObject root, JArray nodes, int nodeIndex, JObject mesh, PartInfo info)
    {
        try
        {
            var accessors = root["accessors"] as JArray; var primitives = mesh?["primitives"] as JArray;
            if (accessors == null || primitives == null) return;
            double[] world = NodeWorldMatrix(nodes, nodeIndex);
            double[] mn = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            double[] mx = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            bool any = false;
            foreach (JObject primitive in primitives.OfType<JObject>())
            {
                var attrs = primitive["attributes"] as JObject; if (attrs?["POSITION"] == null) continue;
                var acc = accessors[attrs.Value<int>("POSITION")] as JObject; if (acc == null) continue;
                info.Vertices += acc.Value<int?>("count") ?? 0;
                var amin = acc["min"] as JArray; var amax = acc["max"] as JArray;
                if (amin == null || amax == null || amin.Count < 3 || amax.Count < 3) continue;
                for (int corner = 0; corner < 8; corner++)
                {
                    var c = new Vec3 { X = ((corner & 1) == 0 ? amin[0] : amax[0]).Value<double>(), Y = ((corner & 2) == 0 ? amin[1] : amax[1]).Value<double>(), Z = ((corner & 4) == 0 ? amin[2] : amax[2]).Value<double>() };
                    UpdateBounds(mn, mx, XForm(world, c)); any = true;
                }
            }
            if (any) { info.Min = mn; info.Max = mx; }
        }
        catch (Exception) { info.Min = info.Max = null; }
    }

    // Every node's parent index (-1 at a root), meshless nodes included — a split parent has no mesh and is not a
    // PartInfo, yet its _Part_NNN children must find it to inherit its ⊕ letter (WorkshopRules.TransferLetters).
    // Every node's name as the FILE has it, by index (null when nameless) - the names a sidecar written before the
    // unique renaming carries (review of PR #85, P1).
    /// <summary>
    /// The rig a file carries, from its JSON alone: how many skins, the joint count of the largest, how many meshes,
    /// how many nodes render one, and how many of those are RIGGED - a node whose mesh has a primitive with
    /// non-empty POSITION, JOINTS_0 and WEIGHTS_0 accessors, bound to a skin that exists and has joints. The Bake
    /// Tests validate a Vehicle Lab output by the rigged count (review of PR #90, three rounds: the row read the bone
    /// count off the script's log; then counted skins and mesh nodes separately, so a static mesh beside an unused
    /// skin passed; then took positions alone, so a mesh that lost its vertex weights passed).
    /// </summary>
    public static void RigSummary(byte[] source, out int skins, out int joints, out int meshes, out int meshNodes, out int riggedMeshNodes)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        JObject root = Parse(source).Root;
        JArray skinArray = root["skins"] as JArray ?? new JArray();
        JArray meshArray = root["meshes"] as JArray ?? new JArray();
        JArray accessors = root["accessors"] as JArray ?? new JArray();
        skins = skinArray.Count; joints = 0;
        foreach (JToken skin in skinArray)
            joints = Math.Max(joints, ((skin as JObject)?["joints"] as JArray)?.Count ?? 0);
        meshes = meshArray.Count;
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        meshNodes = 0; riggedMeshNodes = 0;
        foreach (JObject node in nodes.OfType<JObject>())
        {
            if (node["mesh"] == null) continue;
            meshNodes++;
            int meshIndex = node["mesh"].Type == JTokenType.Integer ? (int)node["mesh"] : -1;
            int skinIndex = node["skin"] != null && node["skin"].Type == JTokenType.Integer ? (int)node["skin"] : -1;
            if (skinIndex < 0 || skinIndex >= skinArray.Count || ((((skinArray[skinIndex] as JObject)?["joints"]) as JArray)?.Count ?? 0) == 0) continue;
            if (meshIndex < 0 || meshIndex >= meshArray.Count) continue;
            bool NonEmpty(JObject attributes, string name)
            {
                JToken t = attributes?[name];
                int acc = t != null && t.Type == JTokenType.Integer ? (int)t : -1;
                return acc >= 0 && acc < accessors.Count && (((accessors[acc] as JObject)?["count"])?.Value<int>() ?? 0) > 0;
            }
            bool rigged = false;
            foreach (JObject prim in (((meshArray[meshIndex] as JObject)?["primitives"]) as JArray ?? new JArray()).OfType<JObject>())
            {
                var attributes = prim["attributes"] as JObject;
                if (NonEmpty(attributes, "POSITION") && NonEmpty(attributes, "JOINTS_0") && NonEmpty(attributes, "WEIGHTS_0")) { rigged = true; break; }
            }
            if (rigged) riggedMeshNodes++;
        }
    }

    public static List<KeyValuePair<int, string>> NodeNames(byte[] source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        JArray nodes = Parse(source).Root["nodes"] as JArray ?? new JArray();
        var table = new List<KeyValuePair<int, string>>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++) table.Add(new KeyValuePair<int, string>(i, (string)(nodes[i] as JObject)?["name"]));
        return table;
    }

    public static List<KeyValuePair<int, int>> NodeParents(byte[] source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        JArray nodes = Parse(source).Root["nodes"] as JArray ?? new JArray();
        var parentOf = new Dictionary<int, int>();
        for (int i = 0; i < nodes.Count; i++)
            if ((nodes[i] as JObject)?["children"] is JArray kids)
                foreach (JToken kid in kids) { int ci = kid.Value<int>(); if (!parentOf.ContainsKey(ci)) parentOf.Add(ci, i); }
        var table = new List<KeyValuePair<int, int>>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++) table.Add(new KeyValuePair<int, int>(i, parentOf.TryGetValue(i, out int pi) ? pi : -1));
        return table;
    }

    public static Result Split(byte[] source) => SplitCore(source, null, null, 0);

    public static Result Split(byte[] source, ISet<string> onlyNodeNames) => SplitCore(source, onlyNodeNames, null, 0);

    public static Result Split(byte[] source, ISet<string> onlyNodeNames, double mergeFraction) => SplitCore(source, onlyNodeNames, null, mergeFraction);

    // Selection by NODE INDEX — the stable identity (review find 2026-09-06: a null-named node could be listed
    // by Analyze but never matched by name, and duplicate names split every namesake). Names remain supported
    // for callers that have them; Analyze's PartInfo.NodeIndex feeds this overload.
    public static Result Split(byte[] source, ISet<int> onlyNodeIndices, double mergeFraction) => SplitCore(source, null, onlyNodeIndices, mergeFraction);

    // TEAR (2026-09-25, user: "the split command only really splits parts that are not connected; we need a method that
    // can separate them even when they are connected" - the Wespe's davits, welded into the deck part, intersect the
    // paddle wheels in the final model; and "it could look at mirror parts when available, because on the mirror side
    // it is separated correctly"; and, after a first cut that tore along sharp seams as well: "it created way too many
    // extra parts, at most I expected 33 extra parts" - the island count). So Tear is Split plus ONE more cut: the
    // islands are the same islands, under the same merge slider, and a welded island is cut only where the other side
    // of the ship has that object as a separate island. Every face is mirrored across the model's centreline and
    // labelled by the nearest island there within `MirrorTolerance` of the model - its own island first (a deck that
    // spans both sides mirrors onto itself and is never cut), else another island of any part, else nothing - and
    // the island is cut wherever the label changes, through welded vertices as well. Nothing else is cut: no seam
    // rule, no angle. Pieces smaller than `GlueFraction` of the island's surface are glued onto the neighbour they
    // share the longest cut with - the deck under a fitting, labelled by the fitting where the mirror deck has a hole
    // (measured: 159 pieces from 25 islands with those kept apart); a davit is 5 % of its island and stays. Lossless,
    // like Split: the pieces become _Part_NNN children of the part.
    public sealed class TearOptions { public double GlueFraction = 0.02; public double MirrorTolerance = 0.001; }   // fractions of the model's longest extent (tolerance) and of the island's surface (glue); 0.5 % tolerance pepper-labelled a deck round a davit's foot, 0.1 % gave the davit whole
    public static Result Tear(byte[] source, ISet<int> onlyNodeIndices, double mergeFraction, double glueFraction = 0.02, double mirrorTolerance = 0.001)
        => SplitCore(source, null, onlyNodeIndices, mergeFraction, new TearOptions { GlueFraction = glueFraction, MirrorTolerance = mirrorTolerance });

    // The mirror templates: every ISLAND of every mesh node (the torn part's own included), in world space, in a grid
    // for nearest-surface queries. Template ids are dense; (node, island) -> id lets a torn island find itself.
    sealed class TearTemplates
    {
        public readonly List<Vec3> Tris = new List<Vec3>(); public readonly List<int> IdOf = new List<int>();
        public readonly Dictionary<(int node, int island), int> IdOfIsland = new Dictionary<(int, int), int>();
        public readonly List<string> NameOf = new List<string>();
        public readonly Dictionary<long, List<int>> Cells = new Dictionary<long, List<int>>();
        public double Cell = 1, Tol = 0; public int WidthAxis = 0; public double CentreW = 0;
        static long Key(long x, long y, long z) => (x & 0x1FFFFF) | ((y & 0x1FFFFF) << 21) | ((z & 0x1FFFFF) << 42);
        public void Index()
        {
            for (int t = 0; t < IdOf.Count; t++)
            {
                Vec3 a = Tris[t * 3], b = Tris[t * 3 + 1], c = Tris[t * 3 + 2];
                long x0 = (long)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)) / Cell), x1 = (long)Math.Floor(Math.Max(a.X, Math.Max(b.X, c.X)) / Cell);
                long y0 = (long)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)) / Cell), y1 = (long)Math.Floor(Math.Max(a.Y, Math.Max(b.Y, c.Y)) / Cell);
                long z0 = (long)Math.Floor(Math.Min(a.Z, Math.Min(b.Z, c.Z)) / Cell), z1 = (long)Math.Floor(Math.Max(a.Z, Math.Max(b.Z, c.Z)) / Cell);
                if (x1 - x0 > 64 || y1 - y0 > 64 || z1 - z0 > 64) continue;   // a stray sliver spanning the model is no template
                for (long x = x0; x <= x1; x++) for (long y = y0; y <= y1; y++) for (long z = z0; z <= z1; z++)
                { long k = Key(x, y, z); if (!Cells.TryGetValue(k, out List<int> l)) Cells.Add(k, l = new List<int>()); l.Add(t); }
            }
        }
        // the island whose surface lies nearest the MIRROR of `p` within Tol; `self` wins a near-tie (a doubled deck of
        // another part must not cut a deck that mirrors onto itself) but not a clear loss (a separate davit on the far
        // side lies ON the mirror point, the own island's railing a unit away - measured on the Wespe's split file, where
        // "self first" kept the davit welded); -1 when none
        public int Label(Vec3 p, int self)
        {
            var m = new Vec3 { X = p.X, Y = p.Y, Z = p.Z };
            if (WidthAxis == 0) m.X = 2 * CentreW - m.X; else m.Z = 2 * CentreW - m.Z;
            long cx = (long)Math.Floor(m.X / Cell), cy = (long)Math.Floor(m.Y / Cell), cz = (long)Math.Floor(m.Z / Cell);
            int best = -1; double bestD = Tol * Tol, selfD = double.PositiveInfinity;   // squared
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
            {
                if (!Cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out List<int> l)) continue;
                foreach (int t in l)
                {
                    Vec3 q = ClosestPointOnTriangle(m, Tris[t * 3], Tris[t * 3 + 1], Tris[t * 3 + 2]);
                    double d = FDist2(q, m);
                    if (IdOf[t] == self) { if (d < selfD) selfD = d; }
                    else if (d < bestD) { bestD = d; best = IdOf[t]; }
                }
            }
            if (selfD > Tol * Tol) return best;
            if (best < 0) return self;
            return Math.Sqrt(selfD) <= Math.Sqrt(bestD) + 0.2 * Tol ? self : best;
        }
    }
    static TearTemplates BuildTearTemplates(JArray nodes, JArray meshes, Accessors reader, double mergeFraction, Result result, double tolFraction)
    {
        var tt = new TearTemplates();
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity }; var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        var planOfMesh = new Dictionary<int, MeshPlan>();
        for (int ni = 0; ni < nodes.Count; ni++)
        {
            var node = nodes[ni] as JObject; if (node?["mesh"] == null) continue;
            int mi = node.Value<int>("mesh"); if (mi < 0 || mi >= meshes.Count) continue;
            var mesh = meshes[mi] as JObject; if (mesh == null) continue;
            MeshPlan plan;
            if (!planOfMesh.TryGetValue(mi, out plan))
            {
                try { plan = AnalyzeMesh(mi, mesh, reader, keepSingle: true, mergeFraction: mergeFraction); }
                catch (Exception e) when (e is InvalidDataException || e is OverflowException) { plan = null; }
                planOfMesh.Add(mi, plan);
            }
            if (plan == null) continue;
            double[] world = NodeWorldMatrix(nodes, ni);
            var primitives = mesh["primitives"] as JArray;
            for (int ci = 0; ci < plan.Components.Count; ci++)
            {
                int id = tt.NameOf.Count; tt.NameOf.Add(((string)node["name"] ?? ("node " + ni)) + " island " + (ci + 1)); tt.IdOfIsland[(ni, ci)] = id;
                foreach (var kv in plan.Components[ci].Indices)
                {
                    var prim = primitives[kv.Key] as JObject; int posAcc = ((JObject)prim["attributes"]).Value<int>("POSITION");
                    List<uint> ix = kv.Value;
                    for (int t = 0; t + 2 < ix.Count; t += 3)
                    {
                        Vec3 a = XForm(world, reader.Position(posAcc, ix[t])), b = XForm(world, reader.Position(posAcc, ix[t + 1])), c = XForm(world, reader.Position(posAcc, ix[t + 2]));
                        tt.Tris.Add(a); tt.Tris.Add(b); tt.Tris.Add(c); tt.IdOf.Add(id);
                        UpdateBounds(lo, hi, a); UpdateBounds(lo, hi, b); UpdateBounds(lo, hi, c);
                    }
                }
            }
        }
        if (tt.IdOf.Count == 0) { result.Warnings.Add("tear: no mesh part to mirror; nothing cut beyond the islands"); return null; }
        ModelBelly(nodes, meshes, reader, new List<Vec3>(), out int lengthAxis, out int widthAxis, out double centreW, out double bellyY, out double floorY);
        tt.WidthAxis = widthAxis; tt.CentreW = centreW;
        double longest = Math.Max(hi[0] - lo[0], Math.Max(hi[1] - lo[1], hi[2] - lo[2]));
        tt.Tol = Math.Max(longest * tolFraction, 1e-9); tt.Cell = Math.Max(longest / 128.0, 2 * tt.Tol);
        tt.Index();
        return tt;
    }

    // THE CUT: every island of the plan is relabelled face by face against the mirror and cut where the label changes.
    static void TearComponents(MeshPlan plan, int nodeIndex, JArray primitives, Accessors reader, double[] world, TearTemplates tt, TearOptions tear)
    {
        var output = new List<Component>(); int cut = 0, labelledFaces = 0, faces = 0, glued = 0;
        var claims = new Dictionary<int, int>();   // template id -> pieces it claimed
        for (int ci = 0; ci < plan.Components.Count; ci++)
        {
            Component comp = plan.Components[ci];
            int self = tt.IdOfIsland.TryGetValue((nodeIndex, ci), out int sid) ? sid : -2;
            // the island's faces: (primitive, three indices, three positions), a label each
            var fPrim = new List<int>(); var fIdx = new List<uint[]>(); var fPos = new List<Vec3[]>(); var label = new List<int>();
            foreach (var kv in comp.Indices)
            {
                int posAcc = ((JObject)((JObject)primitives[kv.Key])["attributes"]).Value<int>("POSITION"); List<uint> ix = kv.Value;
                for (int t = 0; t + 2 < ix.Count; t += 3)
                {
                    var p = new[] { reader.Position(posAcc, ix[t]), reader.Position(posAcc, ix[t + 1]), reader.Position(posAcc, ix[t + 2]) };
                    fPrim.Add(kv.Key); fIdx.Add(new[] { ix[t], ix[t + 1], ix[t + 2] }); fPos.Add(p);
                    int lb = tt.Label(XForm(world, FScale(FAdd(FAdd(p[0], p[1]), p[2]), 1.0 / 3.0)), self);
                    label.Add(lb == self ? -1 : lb); if (lb >= 0 && lb != self) labelledFaces++;
                }
            }
            faces += label.Count;
            if (label.Count == 0 || label.All(l => l < 0)) { output.Add(comp); continue; }   // nothing the mirror names: the island as it is
            // edges by position, within the island; faces joined when their labels agree
            double diag = 0; for (int a = 0; a < 3; a++) diag += (comp.Max[a] - comp.Min[a]) * (comp.Max[a] - comp.Min[a]);
            double eps = Math.Max(Math.Sqrt(diag) * 1e-7, 1e-9);
            PositionKey KeyOf(Vec3 p) => new PositionKey { X = (long)Math.Round((p.X - comp.Min[0]) / eps), Y = (long)Math.Round((p.Y - comp.Min[1]) / eps), Z = (long)Math.Round((p.Z - comp.Min[2]) / eps) };
            var edges = new Dictionary<(PositionKey, PositionKey), List<int>>();
            var fd = new DisjointSet(); for (int f = 0; f < label.Count; f++) fd.Add();
            for (int f = 0; f < label.Count; f++)
                for (int e = 0; e < 3; e++)
                {
                    PositionKey ka = KeyOf(fPos[f][e]), kb = KeyOf(fPos[f][(e + 1) % 3]);
                    int cmp = ka.X != kb.X ? ka.X.CompareTo(kb.X) : ka.Y != kb.Y ? ka.Y.CompareTo(kb.Y) : ka.Z.CompareTo(kb.Z);
                    if (cmp == 0) continue;
                    var ek = cmp < 0 ? (ka, kb) : (kb, ka);
                    if (!edges.TryGetValue(ek, out List<int> l)) edges.Add(ek, l = new List<int>());
                    l.Add(f);
                }
            var torn = new List<(int fa, int fb, double length)>();
            foreach (var kv in edges)
            {
                List<int> l = kv.Value; if (l.Count < 2) continue;
                for (int i = 0; i < l.Count; i++) for (int j = i + 1; j < l.Count; j++)
                {
                    if (label[l[i]] == label[l[j]]) fd.Union(l[i], l[j]);
                    else torn.Add((l[i], l[j], Math.Sqrt(Math.Max(0, (kv.Key.Item2.X - kv.Key.Item1.X) * (kv.Key.Item2.X - kv.Key.Item1.X) + (kv.Key.Item2.Y - kv.Key.Item1.Y) * (kv.Key.Item2.Y - kv.Key.Item1.Y) + (kv.Key.Item2.Z - kv.Key.Item1.Z) * (kv.Key.Item2.Z - kv.Key.Item1.Z))) * eps));
                }
            }
            // pieces, their areas and labels; the glue
            var pieceOf = new Dictionary<int, int>(); var area = new List<double>(); var pieceLabel = new List<int>(); double total = 0;
            var pieceFaces = new List<List<int>>();   // a piece is SMALL when both its area and its face count are under the fraction: a davit is a thin arm of many small faces (4.6 % of the faces, under 2 % of the area) and was glued into the deck on area alone
            for (int f = 0; f < label.Count; f++)
            {
                int r = fd.Find(f);
                if (!pieceOf.TryGetValue(r, out int pi)) { pi = area.Count; pieceOf.Add(r, pi); area.Add(0); pieceLabel.Add(label[f]); pieceFaces.Add(new List<int>()); }
                double a = 0.5 * FLen(FCross(FSub(fPos[f][1], fPos[f][0]), FSub(fPos[f][2], fPos[f][0]))); area[pi] += a; total += a; pieceFaces[pi].Add(f);
            }
            var pd = new DisjointSet(); for (int i = 0; i < area.Count; i++) pd.Add();
            if (tear.GlueFraction > 0 && area.Count > 1)
                for (int round = 0; round < area.Count; round++)
                {
                    var rootArea = new Dictionary<int, double>(); var rootFaces = new Dictionary<int, int>(); var rootLabel = new Dictionary<int, int>(); var seam = new Dictionary<(int, int), double>();
                    for (int i = 0; i < area.Count; i++) { int r = pd.Find(i); rootArea[r] = rootArea.TryGetValue(r, out double ra) ? ra + area[i] : area[i]; rootFaces[r] = rootFaces.TryGetValue(r, out int rf) ? rf + pieceFaces[i].Count : pieceFaces[i].Count; if (!rootLabel.ContainsKey(r) || rootLabel[r] < 0) rootLabel[r] = pieceLabel[i]; }
                    foreach (var (fa, fb, length) in torn)
                    {
                        int ra = pd.Find(pieceOf[fd.Find(fa)]), rb = pd.Find(pieceOf[fd.Find(fb)]); if (ra == rb) continue;
                        var k = ra < rb ? (ra, rb) : (rb, ra); seam[k] = seam.TryGetValue(k, out double sl) ? sl + length : length;
                    }
                    int small = -1; double smallArea = double.PositiveInfinity;
                    foreach (var kv in rootArea) if (kv.Value < tear.GlueFraction * total && rootFaces[kv.Key] < tear.GlueFraction * label.Count && kv.Value < smallArea && seam.Keys.Any(k => k.Item1 == kv.Key || k.Item2 == kv.Key)) { small = kv.Key; smallArea = kv.Value; }
                    if (small < 0) break;
                    int into = -1; double best = -1;
                    foreach (var kv in seam) { int other = kv.Key.Item1 == small ? kv.Key.Item2 : kv.Key.Item2 == small ? kv.Key.Item1 : -1; if (other >= 0 && kv.Value > best) { best = kv.Value; into = other; } }
                    pd.Union(small, into); glued++;
                }
            // the output pieces of this island
            var outOf = new Dictionary<int, Component>();
            for (int i = 0; i < area.Count; i++)
            {
                int r = pd.Find(i);
                if (!outOf.TryGetValue(r, out Component oc)) { oc = new Component(); outOf.Add(r, oc); }
                foreach (int f in pieceFaces[i])
                {
                    if (!oc.Indices.TryGetValue(fPrim[f], out List<uint> list)) oc.Indices.Add(fPrim[f], list = new List<uint>());
                    list.AddRange(fIdx[f]); oc.TriangleCount++;
                    foreach (Vec3 p in fPos[f]) { UpdateBounds(oc.Min, oc.Max, p); oc.Points.Add(p); }
                }
            }
            foreach (Component oc in outOf.Values) { oc.FirstTriangle = comp.FirstTriangle; output.Add(oc); }
            if (outOf.Count > 1)
            {
                cut++;
                foreach (var kv in outOf) { int lb = rootLabelOf(kv.Key); if (lb >= 0) claims[lb] = claims.TryGetValue(lb, out int n) ? n + 1 : 1; }
            }
            int rootLabelOf(int root) { int lb = -1; for (int i = 0; i < area.Count; i++) if (pd.Find(i) == root && pieceLabel[i] >= 0) { lb = pieceLabel[i]; break; } return lb; }
        }
        plan.TearNote = string.Format(System.Globalization.CultureInfo.InvariantCulture, "tear '{0}': {1} island(s), {2} cut by the mirror into {3} piece(s) ({4} of {5} faces named by another island, {6} small piece(s) glued back){7}",
            plan.Name, plan.Components.Count, cut, output.Count - (plan.Components.Count - cut), labelledFaces, faces, glued,
            claims.Count > 0 ? "; by mirror island: " + string.Join(", ", claims.OrderByDescending(kv => kv.Value).Take(10).Select(kv => tt.NameOf[kv.Key] + " x" + kv.Value)) : "");
        plan.Components = output.OrderBy(c => c.Min[0]).ThenBy(c => c.Min[1]).ThenBy(c => c.Min[2]).ThenBy(c => c.FirstTriangle).ToList();
    }

    // onlyNodeNames/onlyNodeIndices: when non-null, ONLY matching nodes are split (the Model Workshop's
    // selective mode — a hull keeps its junk-free parts whole while the one island-soup part is exploded).
    // Original meshes are never removed, so a mesh shared with an unselected node keeps rendering there.
    // mergeFraction: islands within this fraction of a mesh's own diagonal fuse into one part (0 = topology only).
    static Result SplitCore(byte[] source, ISet<string> onlyNodeNames, ISet<int> onlyNodeIndices, double mergeFraction, TearOptions tear = null)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        Document document = Parse(source);
        JObject root = document.Root;
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        JArray meshes = root["meshes"] as JArray ?? throw new InvalidDataException("GLB has no meshes array.");
        JArray buffers = root["buffers"] as JArray ?? throw new InvalidDataException("GLB has no buffers array.");
        if (buffers.Count != 1 || buffers[0]?["uri"] != null)
            throw new InvalidDataException("Lossless splitting requires one embedded GLB buffer.");

        int declaredBinLength = buffers[0].Value<int>("byteLength");
        byte[] sourceBin = document.Chunks[document.BinIndex].Data;
        if (declaredBinLength < 0 || declaredBinLength > sourceBin.Length)
            throw new InvalidDataException("buffers[0].byteLength exceeds the BIN chunk.");
        byte[] originalData = Prefix(sourceBin, declaredBinLength);
        var bin = new List<byte>(originalData);
        var result = new Result();
        var reader = new Accessors(root, originalData);

        bool Selected(int idx, JObject nd) => onlyNodeIndices != null ? onlyNodeIndices.Contains(idx)
                                            : onlyNodeNames == null || onlyNodeNames.Contains((string)nd["name"]);
        var referencedMeshes = new SortedSet<int>();
        var instancedMeshes = new HashSet<int>();
        var firstNodeOfMesh = new Dictionary<int, int>();   // Tear: the node whose world matrix places the mesh for the mirror query
        for (int ni = 0; ni < nodes.Count; ni++)
        {
            var node = nodes[ni] as JObject;
            if (node?["mesh"] == null) continue;
            if (!Selected(ni, node)) continue;
            int meshIndex = node.Value<int>("mesh");
            referencedMeshes.Add(meshIndex);
            if (!firstNodeOfMesh.ContainsKey(meshIndex)) firstNodeOfMesh.Add(meshIndex, ni);
            if (node["extensions"]?["EXT_mesh_gpu_instancing"] != null) instancedMeshes.Add(meshIndex);
        }

        var plans = new Dictionary<int, MeshPlan>();
        TearTemplates templates = tear != null ? BuildTearTemplates(nodes, meshes, reader, mergeFraction, result, tear.MirrorTolerance) : null;
        foreach (int meshIndex in referencedMeshes)
        {
            if (meshIndex < 0 || meshIndex >= meshes.Count)
                throw new InvalidDataException("Node references an out-of-range mesh index.");
            if (instancedMeshes.Contains(meshIndex))
            {
                result.Warnings.Add("Skipped mesh " + meshIndex + ": EXT_mesh_gpu_instancing nodes require a mesh on the original node.");
                continue;
            }
            try
            {
                MeshPlan plan = AnalyzeMesh(meshIndex, (JObject)meshes[meshIndex], reader, keepSingle: tear != null, mergeFraction: mergeFraction);
                if (plan != null && tear != null && templates != null)
                {
                    TearComponents(plan, firstNodeOfMesh[meshIndex], (JArray)meshes[meshIndex]["primitives"], reader, NodeWorldMatrix(nodes, firstNodeOfMesh[meshIndex]), templates, tear);
                    result.Details.Add(plan.TearNote);
                }
                if (plan != null && tear != null && plan.Components.Count <= 1) plan = null;   // one island, nothing the mirror cut: nothing to do
                if (plan != null) plans.Add(meshIndex, plan);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is OverflowException)
            {
                string name = (string)meshes[meshIndex]?["name"] ?? ("mesh " + meshIndex);
                result.Warnings.Add("Skipped '" + name + "': " + ex.Message);
            }
        }

        JArray primitives;
        foreach (MeshPlan plan in plans.Values.OrderBy(p => p.MeshIndex))
        {
            primitives = plan.Mesh["primitives"] as JArray;
            for (int part = 0; part < plan.Components.Count; part++)
            {
                Component component = plan.Components[part];
                var partMesh = (JObject)plan.Mesh.DeepClone();
                partMesh["name"] = UniqueName(plan.Name + "_Part_" + (part + 1).ToString("D3"),
                    meshes.OfType<JObject>().Select(m => (string)m["name"]));
                var partPrimitives = new JArray();
                for (int primitiveIndex = 0; primitiveIndex < primitives.Count; primitiveIndex++)
                {
                    List<uint> partIndices;
                    if (!component.Indices.TryGetValue(primitiveIndex, out partIndices) || partIndices.Count == 0) continue;
                    var sourcePrimitive = (JObject)primitives[primitiveIndex];
                    var splitPrimitive = (JObject)sourcePrimitive.DeepClone();
                    int componentType = SourceIndexType(root, sourcePrimitive, reader.Count(sourcePrimitive["attributes"].Value<int>("POSITION")));
                    splitPrimitive["indices"] = AppendIndices(root, bin, partIndices, componentType);
                    partPrimitives.Add(splitPrimitive);
                    result.OutputTriangles += partIndices.Count / 3;
                }
                partMesh["primitives"] = partPrimitives;
                plan.NewMeshIndices.Add(meshes.Count);
                meshes.Add(partMesh);
            }
            result.MeshesSplit++;
            result.SourceTriangles += plan.TriangleCount;
            result.Details.Add(plan.Name + ": " + plan.Components.Count + " disconnected part(s), " + plan.TriangleCount + " triangles");
        }

        var nodeNames = new HashSet<string>(nodes.OfType<JObject>().Select(n => (string)n["name"]).Where(n => !string.IsNullOrEmpty(n)));
        var splitNodeChildren = new Dictionary<int, List<int>>();
        for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
        {
            var node = nodes[nodeIndex] as JObject;
            if (node?["mesh"] == null) continue;
            if (!Selected(nodeIndex, node)) continue;
            MeshPlan plan;
            if (!plans.TryGetValue(node.Value<int>("mesh"), out plan)) continue;

            JToken skin = node["skin"]?.DeepClone();
            JToken weights = node["weights"]?.DeepClone();
            node.Remove("mesh");
            node.Remove("skin");
            node.Remove("weights");
            JArray children = node["children"] as JArray;
            if (children == null) { children = new JArray(); node["children"] = children; }
            var newChildren = new List<int>();
            string baseName = (string)node["name"];
            if (string.IsNullOrEmpty(baseName)) baseName = plan.Name;
            for (int part = 0; part < plan.NewMeshIndices.Count; part++)
            {
                string childName = UniqueName(baseName + "_Part_" + (part + 1).ToString("D3"), nodeNames);
                nodeNames.Add(childName);
                var child = new JObject { ["name"] = childName, ["mesh"] = plan.NewMeshIndices[part] };
                if (skin != null) child["skin"] = skin.DeepClone();
                if (weights != null) child["weights"] = weights.DeepClone();
                int childIndex = nodes.Count;
                children.Add(childIndex);
                nodes.Add(child);
                newChildren.Add(childIndex);
                result.ChildPartsCreated++;
            }
            splitNodeChildren.Add(nodeIndex, newChildren);
            result.NodesSplit++;
        }
        RetargetWeightAnimations(root, splitNodeChildren);

        if (result.OutputTriangles != result.SourceTriangles)
            throw new InvalidDataException("Triangle preservation check failed: source " + result.SourceTriangles + ", output " + result.OutputTriangles + ".");

        buffers[0]["byteLength"] = bin.Count;
        document.Chunks[document.BinIndex].Data = bin.ToArray();
        result.Bytes = Write(document);
        ValidateOutput(result.Bytes);
        return result;
    }

    static void RetargetWeightAnimations(JObject root, Dictionary<int, List<int>> splitNodeChildren)
    {
        JArray animations = root["animations"] as JArray;
        if (animations == null) return;
        foreach (JObject animation in animations.OfType<JObject>())
        {
            JArray channels = animation["channels"] as JArray;
            if (channels == null) continue;
            foreach (JObject channel in channels.OfType<JObject>().ToList())
            {
                JObject target = channel["target"] as JObject;
                if ((string)target?["path"] != "weights" || target["node"] == null) continue;
                List<int> children;
                if (!splitNodeChildren.TryGetValue(target.Value<int>("node"), out children) || children.Count == 0) continue;

                // Transform channels stay on the original parent node. Weight channels must address every child
                // mesh because glTF morph weights are node properties and the parent no longer owns a mesh.
                target["node"] = children[0];
                for (int i = 1; i < children.Count; i++)
                {
                    var copy = (JObject)channel.DeepClone();
                    ((JObject)copy["target"])["node"] = children[i];
                    channels.Add(copy);
                }
            }
        }
    }

    public static Result SplitFile(string inputPath, string outputPath) => SplitFile(inputPath, outputPath, (ISet<string>)null, 0);

    public static Result SplitFile(string inputPath, string outputPath, ISet<string> onlyNodeNames) => SplitFile(inputPath, outputPath, onlyNodeNames, 0);

    public static Result SplitFile(string inputPath, string outputPath, ISet<string> onlyNodeNames, double mergeFraction)
    {
        GuardPaths(inputPath, outputPath);
        Result result = SplitCore(File.ReadAllBytes(inputPath), onlyNodeNames, null, mergeFraction);
        if (result.Changed) File.WriteAllBytes(outputPath, result.Bytes);
        return result;
    }

    public static Result SplitFile(string inputPath, string outputPath, ISet<int> onlyNodeIndices, double mergeFraction)
    {
        GuardPaths(inputPath, outputPath);
        Result result = SplitCore(File.ReadAllBytes(inputPath), null, onlyNodeIndices, mergeFraction);
        if (result.Changed) File.WriteAllBytes(outputPath, result.Bytes);
        return result;
    }

    // Public so a caller that writes the bytes itself (the Workshop's chained Fuse) refuses the same thing the file
    // entry points refuse: output == source. The Workshop's "Overwrite existing file?" dialog is an ordinary overwrite
    // prompt, not this guard — it would have let the source go (review of 4e748c1).
    public static void GuardPaths(string inputPath, string outputPath)
    {
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a new output path; the source GLB is never overwritten.");
    }

    // ---- PLANE / FACING CUT (2026-09-13, the Bremen deck): a CONNECTED part can never island-split — the
    // OceanLiner's hull and deck are one welded mesh, one island, one Vehicle Lab row, one role. Both cuts are
    // the island splitter's lossless mechanism with a different partition rule: WHOLE triangles are judged in
    // world space — plane cut by which side the centroid falls on, facing cut by whether the face lies flatter
    // than a tilt limit (deck vs bow plating, where no flat plane can trace the boundary) — and the two sides
    // become _CutA/_CutB children (vertex data byte-identical, only filtered index accessors appended).
    // No triangle is ever sliced: the boundary follows the existing triangulation, which is what role marking
    // and reduce dials need; visually nothing moves. Jagged-boundary honesty over interpolated new geometry.
    //
    // Coordinates are the node's WORLD space (the node chain's composed glTF transforms), the same space
    // ExtractPart reports — so a UI that previews ExtractPart geometry and cuts at a slider value taken from
    // its bounds shows exactly the triangles the cut will move. CutA = centroid at or above planeValue.

    /// <summary>One node's triangles in world space — the WYSIWYG data behind a plane-cut preview and the Workshop's turntable.</summary>
    public sealed class PartGeometry
    {
        public int NodeIndex;
        public string NodeName;
        public float[] Positions;    // xyz triplets, world space, per-primitive concatenated
        public int[] Triangles;      // vertex indices into Positions/3, every 3 = one triangle
        public int[] PrimitiveStart;       // where each primitive's run begins in Triangles (a preview submesh per primitive)
        public float[][] PrimitiveColour;  // each primitive's material base colour factor, RGB (1,1,1 when the material has none)
        public readonly double[] Min = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        public readonly double[] Max = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
    }

    public static PartGeometry ExtractPart(byte[] source, int nodeIndex)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        Document document = Parse(source);
        JObject root = document.Root;
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        JArray meshes = root["meshes"] as JArray ?? throw new InvalidDataException("GLB has no meshes array.");
        Accessors reader = BinReader(document, root);
        return ExtractNode(root, nodes, meshes, reader, nodeIndex);
    }

    // EVERY mesh-carrying node in one pass (2026-09-19, the Workshop's preview): the file is parsed once and each node's
    // geometry read through the cached accessors, one PartGeometry per node in node order — the preview objects then
    // meet the part rows on the NODE INDEX, which a file that names all 113 nodes "Material2" cannot do by name. A node
    // the extractor refuses (draco, GPU instancing, a primitive that is not triangles) is left out; Analyze lists it
    // blocked, and the preview simply lacks it.
    public static List<PartGeometry> ExtractAll(byte[] source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        Document document = Parse(source);
        JObject root = document.Root;
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        JArray meshes = root["meshes"] as JArray ?? throw new InvalidDataException("GLB has no meshes array.");
        Accessors reader = BinReader(document, root);
        var parts = new List<PartGeometry>();
        for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
        {
            if ((nodes[nodeIndex] as JObject)?["mesh"] == null) continue;
            try { parts.Add(ExtractNode(root, nodes, meshes, reader, nodeIndex)); }
            catch (Exception ex) when (ex is InvalidDataException || ex is OverflowException) { }
        }
        return parts;
    }

    static PartGeometry ExtractNode(JObject root, JArray nodes, JArray meshes, Accessors reader, int nodeIndex)
    {
        JObject node = NodeWithMesh(nodes, nodeIndex, out int meshIndex);
        var primitives = (meshes[meshIndex] as JObject)?["primitives"] as JArray ?? throw new InvalidDataException("Mesh has no primitives.");
        double[] world = NodeWorldMatrix(nodes, nodeIndex);
        var prims = TrianglePrimitives(primitives).ToList();
        // ONLY THE VERTICES THIS PART REFERENCES (2026-09-19, review of PR 66). A split writes its fragments as new
        // INDEX accessors over the parent's untouched POSITION accessor, so a fragment's primitive addresses a handful
        // of vertices inside a buffer holding the whole original part. Copying the buffer wholesale gave every fragment
        // the parent's vertices: measured on khelandion_2_split.glb, 5,202,111 vertices held to draw 393,646 (13.2x),
        // one 8-vertex fragment carrying 65,532. Two consequences, both real — the memory, and Unity's
        // RecalculateBounds over the unused vertices, which framed the whole parent when a fragment's row was clicked.
        // Each primitive is compacted to its referenced vertices with the indices remapped; the bounds were always
        // taken over referenced vertices only, so Min/Max are unchanged.
        int totalIndices = 0;
        var primIndices = new uint[prims.Count][];
        var primVertCount = new int[prims.Count];
        for (int pi = 0; pi < prims.Count; pi++)
        {
            JObject primitive = prims[pi];
            int posAcc = primitive["attributes"].Value<int>("POSITION");
            int vertCount = reader.Count(posAcc);
            int indexCount = primitive["indices"] == null ? vertCount : reader.Count(primitive.Value<int>("indices"));
            if (indexCount % 3 != 0) throw new InvalidDataException("Triangle primitive index count is not divisible by three.");
            var idx = new uint[indexCount];
            for (uint i = 0; i < indexCount; i++)
            {
                uint v = primitive["indices"] == null ? i : reader.Index(primitive.Value<int>("indices"), i);
                if (v >= vertCount) throw new InvalidDataException("Primitive index exceeds its POSITION accessor.");
                idx[i] = v;
            }
            primIndices[pi] = idx; primVertCount[pi] = vertCount;
            totalIndices = checked(totalIndices + indexCount);
        }
        var triangles = new int[totalIndices];
        var positions = new List<float>(Math.Min(totalIndices, 1 << 20) * 3);
        var geo = new PartGeometry { NodeIndex = nodeIndex, NodeName = (string)node["name"] ?? ("node " + nodeIndex), PrimitiveStart = new int[prims.Count], PrimitiveColour = new float[prims.Count][] };
        JArray materials = root["materials"] as JArray;
        int at = 0;
        for (int pi = 0; pi < prims.Count; pi++)
        {
            JObject primitive = prims[pi];
            int posAcc = primitive["attributes"].Value<int>("POSITION");
            geo.PrimitiveStart[pi] = at;
            geo.PrimitiveColour[pi] = BaseColour(materials, primitive);
            var remap = new int[primVertCount[pi]];        // source vertex -> compacted vertex, -1 until first use
            for (int k = 0; k < remap.Length; k++) remap[k] = -1;
            foreach (uint v in primIndices[pi])
            {
                int vi = remap[(int)v];
                if (vi < 0)
                {
                    Vec3 p = XForm(world, reader.Position(posAcc, v));
                    vi = positions.Count / 3;
                    positions.Add((float)p.X); positions.Add((float)p.Y); positions.Add((float)p.Z);
                    remap[(int)v] = vi;
                    UpdateBounds(geo.Min, geo.Max, new Vec3 { X = positions[vi * 3], Y = positions[vi * 3 + 1], Z = positions[vi * 3 + 2] });
                }
                triangles[at++] = vi;
            }
        }
        geo.Positions = positions.ToArray();
        geo.Triangles = triangles;
        return geo;
    }

    // The primitive's material base colour factor as RGB; white for a material without one. Never throws: the colour
    // is cosmetic (the preview's tint), the geometry is what the caller came for.
    static float[] BaseColour(JArray materials, JObject primitive)
    {
        var rgb = new[] { 1f, 1f, 1f };
        try
        {
            int mi = primitive.Value<int?>("material") ?? -1;
            var factor = (materials != null && mi >= 0 && mi < materials.Count ? materials[mi]?["pbrMetallicRoughness"]?["baseColorFactor"] : null) as JArray;
            if (factor != null && factor.Count >= 3) for (int k = 0; k < 3; k++) rgb[k] = (float)factor[k].Value<double>();
        }
        catch (Exception) { }
        return rgb;
    }

    public static Result CutNodeByPlane(byte[] source, int nodeIndex, int axis, double planeValue)
    {
        if (axis < 0 || axis > 2) throw new ArgumentOutOfRangeException(nameof(axis));
        return CutNode(source, nodeIndex,
            (pa, pb, pc) => (pa[axis] + pb[axis] + pc[axis]) / 3.0 >= planeValue,
            "plane cut on axis " + "XYZ"[axis] + " at " + planeValue.ToString("0.###"));
    }

    // FACING CUT (the Bremen deck, round 2): the deck is HORIZONTAL and the bow plating is not — no flat plane
    // can trace that boundary, the surface orientation can. A triangle goes to _CutA when its world-space face
    // normal tilts less than maxTiltDeg away from the up axis (|n·up| >= cos, so a deck's underside counts as
    // horizontal too) AND its centroid sits at or above floorValue — the floor keeps the equally-horizontal
    // hull BOTTOM out of the deck piece. floorValue at or below the part's minimum = pure facing cut.
    public static Result CutNodeByFacing(byte[] source, int nodeIndex, int upAxis, double maxTiltDeg, double floorValue)
    {
        if (upAxis < 0 || upAxis > 2) throw new ArgumentOutOfRangeException(nameof(upAxis));
        double cosLimit = Math.Cos(Math.Max(0.0, Math.Min(90.0, maxTiltDeg)) * Math.PI / 180.0);
        return CutNode(source, nodeIndex,
            (pa, pb, pc) => Math.Abs(TriangleNormalComponent(pa, pb, pc, upAxis)) >= cosLimit
                            && (pa[upAxis] + pb[upAxis] + pc[upAxis]) / 3.0 >= floorValue,
            "facing cut, up axis " + "XYZ"[upAxis] + ", max tilt " + maxTiltDeg.ToString("0.#") +
            " deg, floor " + floorValue.ToString("0.###"));
    }

    // The up-axis component of the triangle's unit normal; 0 for a degenerate triangle (lands in _CutB).
    static double TriangleNormalComponent(Vec3 a, Vec3 b, Vec3 c, int axis)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-30) return 0;
        return (axis == 0 ? nx : axis == 1 ? ny : nz) / len;
    }

    static Result CutNode(byte[] source, int nodeIndex, Func<Vec3, Vec3, Vec3, bool> sideA, string detail)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        Document document = Parse(source);
        JObject root = document.Root;
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        JArray meshes = root["meshes"] as JArray ?? throw new InvalidDataException("GLB has no meshes array.");
        JArray buffers = root["buffers"] as JArray;
        byte[] originalData;
        Accessors reader = BinReader(document, root, out originalData);
        var bin = new List<byte>(originalData);
        var result = new Result();

        JObject node = NodeWithMesh(nodes, nodeIndex, out int meshIndex);
        var mesh = (JObject)meshes[meshIndex];
        var primitives = mesh["primitives"] as JArray ?? throw new InvalidDataException("Mesh has no primitives.");
        double[] world = NodeWorldMatrix(nodes, nodeIndex);

        // Classify every triangle in world space: the sideA judge decides (plane side, or facing), side 1 = rest.
        var sides = new[] { new Dictionary<int, List<uint>>(), new Dictionary<int, List<uint>>() };
        int primitiveIndex = -1;
        foreach (JObject primitive in TrianglePrimitives(primitives))
        {
            primitiveIndex++;
            int posAcc = primitive["attributes"].Value<int>("POSITION");
            int indexCount = primitive["indices"] == null ? reader.Count(posAcc) : reader.Count(primitive.Value<int>("indices"));
            if (indexCount % 3 != 0) throw new InvalidDataException("Triangle primitive index count is not divisible by three.");
            for (uint i = 0; i < indexCount; i += 3)
            {
                uint a = primitive["indices"] == null ? i : reader.Index(primitive.Value<int>("indices"), i);
                uint b = primitive["indices"] == null ? i + 1 : reader.Index(primitive.Value<int>("indices"), i + 1);
                uint c = primitive["indices"] == null ? i + 2 : reader.Index(primitive.Value<int>("indices"), i + 2);
                // FLOAT-SNAP before judging (review P2, 2026-09-13): the preview classifies ExtractPart's FLOAT
                // positions while this writer held doubles — at a boundary (a face exactly at the plane, or a
                // 0.1 node translation whose float rounds above its double) the two disagreed: a grey face wrote
                // to _CutA, and an "Only above: 0%" floor excluded the bottom face. Judging the same
                // float-rounded coordinates the preview shows restores WYSIWYG exactly, boundaries included.
                Vec3 pa = Snap(XForm(world, reader.Position(posAcc, a)));
                Vec3 pb = Snap(XForm(world, reader.Position(posAcc, b)));
                Vec3 pc = Snap(XForm(world, reader.Position(posAcc, c)));
                var bucket = sides[sideA(pa, pb, pc) ? 0 : 1];
                if (!bucket.TryGetValue(primitiveIndex, out List<uint> list)) bucket.Add(primitiveIndex, list = new List<uint>());
                list.Add(a); list.Add(b); list.Add(c);
                result.SourceTriangles++;
            }
        }
        int trisA = sides[0].Values.Sum(l => l.Count) / 3, trisB = sides[1].Values.Sum(l => l.Count) / 3;
        if (trisA == 0 || trisB == 0)
        {
            result.Warnings.Add("The plane leaves every triangle on one side — move it into the part before cutting.");
            return result;   // Changed == false; source bytes untouched
        }

        string meshBase = (string)mesh["name"] ?? ("Mesh_" + meshIndex);
        string nodeBase = (string)node["name"];
        if (string.IsNullOrEmpty(nodeBase)) nodeBase = meshBase;
        var nodeNames = new HashSet<string>(nodes.OfType<JObject>().Select(n => (string)n["name"]).Where(n => !string.IsNullOrEmpty(n)));
        JToken skin = node["skin"]?.DeepClone();
        JToken weights = node["weights"]?.DeepClone();
        node.Remove("mesh"); node.Remove("skin"); node.Remove("weights");
        JArray children = node["children"] as JArray;
        if (children == null) { children = new JArray(); node["children"] = children; }
        var newChildren = new List<int>();
        var sideNames = new[] { "_CutA", "_CutB" };
        for (int side = 0; side < 2; side++)
        {
            var partMesh = (JObject)mesh.DeepClone();
            partMesh["name"] = UniqueName(meshBase + sideNames[side], meshes.OfType<JObject>().Select(m => (string)m["name"]));
            var partPrimitives = new JArray();
            for (int pi = 0; pi < primitives.Count; pi++)
            {
                if (!sides[side].TryGetValue(pi, out List<uint> partIndices) || partIndices.Count == 0) continue;
                var sourcePrimitive = (JObject)primitives[pi];
                var splitPrimitive = (JObject)sourcePrimitive.DeepClone();
                int componentType = SourceIndexType(root, sourcePrimitive, reader.Count(sourcePrimitive["attributes"].Value<int>("POSITION")));
                splitPrimitive["indices"] = AppendIndices(root, bin, partIndices, componentType);
                partPrimitives.Add(splitPrimitive);
                result.OutputTriangles += partIndices.Count / 3;
            }
            partMesh["primitives"] = partPrimitives;
            string childName = UniqueName(nodeBase + sideNames[side], nodeNames);
            nodeNames.Add(childName);
            var child = new JObject { ["name"] = childName, ["mesh"] = meshes.Count };
            if (skin != null) child["skin"] = skin.DeepClone();
            if (weights != null) child["weights"] = weights.DeepClone();
            meshes.Add(partMesh);
            int childIndex = nodes.Count;
            children.Add(childIndex);
            nodes.Add(child);
            newChildren.Add(childIndex);
            result.ChildPartsCreated++;
        }
        RetargetWeightAnimations(root, new Dictionary<int, List<int>> { { nodeIndex, newChildren } });
        result.MeshesSplit = 1;
        result.NodesSplit = 1;
        result.Details.Add(nodeBase + ": " + detail +
                           " -> " + nodeBase + "_CutA " + trisA + " tri(s) / " + nodeBase + "_CutB " + trisB + " tri(s)");
        foreach (int otherNode in Enumerable.Range(0, nodes.Count).Where(i => i != nodeIndex && (nodes[i] as JObject)?["mesh"]?.Value<int>() == meshIndex))
            result.Warnings.Add("Node " + otherNode + " shares the cut mesh and keeps the ORIGINAL (uncut) geometry.");

        if (result.OutputTriangles != result.SourceTriangles)
            throw new InvalidDataException("Triangle preservation check failed: source " + result.SourceTriangles + ", output " + result.OutputTriangles + ".");
        buffers[0]["byteLength"] = bin.Count;
        document.Chunks[document.BinIndex].Data = bin.ToArray();
        result.Bytes = Write(document);
        ValidateOutput(result.Bytes);
        return result;
    }

    public static Result CutFileByPlane(string inputPath, string outputPath, int nodeIndex, int axis, double planeValue)
    {
        GuardPaths(inputPath, outputPath);
        Result result = CutNodeByPlane(File.ReadAllBytes(inputPath), nodeIndex, axis, planeValue);
        if (result.Changed) File.WriteAllBytes(outputPath, result.Bytes);
        return result;
    }

    public static Result CutFileByFacing(string inputPath, string outputPath, int nodeIndex, int upAxis, double maxTiltDeg, double floorValue)
    {
        GuardPaths(inputPath, outputPath);
        Result result = CutNodeByFacing(File.ReadAllBytes(inputPath), nodeIndex, upAxis, maxTiltDeg, floorValue);
        if (result.Changed) File.WriteAllBytes(outputPath, result.Bytes);
        return result;
    }

    // ---- FUSE (2026-09-15): weld several parts into ONE shell with consistent winding ----
    //
    // The Teutonic's hull ships as dozens of separate plates per object (861 islands over four parts). No
    // per-island facing test can see that a 1,609-face plate region is glued on the wrong way round along a
    // 26-edge seam — the hole in the hull — and any reduction opens gaps because the plates share no vertices.
    // Fusing at the SOURCE fixes every pipeline downstream: the chosen parts become one mesh; positions within
    // `weldFraction` of the model's longest extent are one vertex (attributes permitting — a UV seam or a hard-edge
    // normal keeps its own vertex, while CONNECTIVITY is by position regardless); each welded island is made
    // consistent by MAJORITY (orientation parity propagated across every two-face edge, the minority reversed);
    // and direction is judged where it can be: an OPEN sheet (any boundary edge — a deck, a bulwark, a plating
    // region) by the inside-out score against an axis through the hull belly, a CLOSED shell (no boundary edge at
    // all) by its signed volume (negative = wound inward, reversed whole). A "mostly closed" threshold was tried
    // first (30 % boundary edges) and rejected in review: a densely triangulated deck is 23 % boundary, and the
    // signed volume of an open surface depends on where the origin is — a correct deck below y=0 came back reversed
    // whole. Two more rules were measured and rejected on the Blender prototype
    // the same day: a blind normal recalc flipped 40 % of a 99.6 %-edge-consistent island (overlapping plates are
    // not the manifold solid it assumes), and the radial score on a closed thin shell reads ~0 (inner faces cancel
    // outer). Vertex normals follow the final winding (negated where every incident face was reversed, recomputed
    // where mixed). Triangles are preserved exactly; the source nodes keep their transforms and children and lose
    // only their mesh; the fused mesh lands on a new root node in world space.
    // An open island's signed volume counts as a judgement of facing only when |volume| / area^1.5 (~ thickness over
    // sheet width for a thin solid) clears this. Measured on the Teutonic (2026-09-15): plating regions read 0.044,
    // 0.047, 0.137, 0.158 and 0.222; an 18-face strip 0.004; the lap-over-plate fixture 0.001. A flat sheet reads 0.
    const double VolumeThicknessGate = 0.01;

    public static Result FuseNodes(byte[] source, IList<int> nodeIndices, double weldFraction) => FuseNodes(source, nodeIndices, weldFraction, null);

    // FUSE = PLAN + APPLY (2026-09-18, user: "generating takes quite a substantial time, can't we process it in parallel?"):
    // planning a group (stages 1-7: gather, weld, islands, sheets, direction, normals, vertices) reads only the source
    // bytes and its own state, so every group of a Generate plans at once on the thread pool; applying (stage 8: the
    // mesh, the node, the stripped sources) appends to ONE document in letter order and writes once. The Romanic's 19
    // groups: 130 s chained → the longest group's 12 s plus the writes. FuseNodes keeps the one-group API.
    public static Result FuseNodes(byte[] source, IList<int> nodeIndices, double weldFraction, string fusedName) => FuseNodes(source, nodeIndices, weldFraction, fusedName, false);

    public static Result FuseNodes(byte[] source, IList<int> nodeIndices, double weldFraction, string fusedName, bool checkMirrored)
    {
        FusePlan plan = PlanFuse(source, nodeIndices, weldFraction, fusedName, checkMirrored);
        if (plan.Empty) return plan.Result;
        Document document = Parse(source);
        JObject root = document.Root;
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        JArray meshes = (JArray)root["meshes"];
        JArray buffers = root["buffers"] as JArray;
        Accessors reader = BinReader(document, root, out byte[] originalData);
        var bin = new List<byte>(originalData);
        ApplyPlan(root, nodes, meshes, bin, plan);
        buffers[0]["byteLength"] = bin.Count;
        document.Chunks[document.BinIndex].Data = bin.ToArray();
        plan.Result.Bytes = Write(document);
        ValidateOutput(plan.Result.Bytes);
        return plan.Result;
    }

    /// <summary>One group of a multi-group fuse: the node indices and the fused part's name (null = "&lt;first part&gt;_Fused").</summary>
    public sealed class FuseJob { public IList<int> NodeIndices; public string Name; public bool CheckMirrored; }

    /// <summary>
    /// PARITY REPAIR (2026-09-23, SMS Wespe). The parity walk gives every face of a sheet a colour by the first path
    /// that reaches it, and on a sheet with one contradiction that first path can be the wrong one: a BRIDGE face wound
    /// consistently with a reversed patch AND with the correct majority (a riser at the platform step, reached through
    /// the patch first) inherits the patch's colour, its two majority edges become the sheet's only unsatisfied edges,
    /// and the majority flip then turns a correct face over — see-through from the bow. A face's colour should be the
    /// one MOST of its own edges support: while any face has more unsatisfied partnered edges than satisfied ones,
    /// recolour it. Each recolouring strictly lowers the sheet's unsatisfied count, so this terminates; a pure kernel
    /// over the sheet's edge list so it can be pinned by a test.
    /// Edges: (a, b, same) — faces a and b share an edge and walk it the same way (inconsistent as authored) when
    /// `same`; an edge is satisfied when parity[a] ^ parity[b] == (same ? 1 : 0). Returns the number of recolourings.
    /// Runs until no face changes (PR #81 review: a correction that has to travel AGAINST the iteration order costs one
    /// round per step - a fixed 16-round cap left a 565-face sheet with six unsatisfied edges and a face still
    /// outvoted, and the winding pass then used the unfinished repair). Every round that changes anything lowers the
    /// unsatisfied count by at least one, so `edges.Count + 1` rounds is a bound that cannot bind; `maxRounds` above 0
    /// overrides it for a test.
    /// </summary>
    public static int RepairParity(int[] parity, IList<int> faces, IList<(int a, int b, bool same)> edges, int maxRounds = 0)
    {
        if (maxRounds <= 0) maxRounds = edges.Count + 1;
        var byFace = new Dictionary<int, List<int>>();
        for (int i = 0; i < edges.Count; i++)
        {
            if (!byFace.TryGetValue(edges[i].a, out var la)) byFace[edges[i].a] = la = new List<int>(); la.Add(i);
            if (!byFace.TryGetValue(edges[i].b, out var lb)) byFace[edges[i].b] = lb = new List<int>(); lb.Add(i);
        }
        int total = 0;
        for (int round = 0; round < maxRounds; round++)
        {
            int changed = 0;
            foreach (int f in faces)
            {
                if (!byFace.TryGetValue(f, out var le)) continue;
                int sat = 0, unsat = 0;
                foreach (int i in le) { var e = edges[i]; if (((parity[e.a] ^ parity[e.b]) == (e.same ? 1 : 0))) sat++; else unsat++; }
                if (unsat > sat) { parity[f] ^= 1; changed++; }
            }
            total += changed;
            if (changed == 0) break;
        }
        return total;
    }

    /// <summary>
    /// Fuse several groups from ONE source in one output: every group is planned in parallel against the same source
    /// bytes, then applied in the given order (so the fused parts are appended in that order, exactly as chaining
    /// FuseNodes group by group would). <paramref name="onTick"/> is called on the CALLING thread every ~100 ms while
    /// the plans run, with the number of groups planned so far — a progress bar's hook. One Result per job, in order;
    /// the bytes of the combined output are returned (each Result's Bytes is null).
    /// </summary>
    public static byte[] FuseGroups(byte[] source, IList<FuseJob> jobs, double weldFraction, out List<Result> results, Action<int> onTick = null)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (jobs == null || jobs.Count == 0) throw new ArgumentException("Nothing to fuse — no groups.", nameof(jobs));
        // a node in two jobs would be fused twice (the chain refused the second: its mesh was already gone) — refused up front
        var owner = new Dictionary<int, int>();
        for (int i = 0; i < jobs.Count; i++)
        {
            if (jobs[i] == null || jobs[i].NodeIndices == null || jobs[i].NodeIndices.Count == 0) throw new ArgumentException("Group " + i + " has no node indices.", nameof(jobs));
            foreach (int ni in jobs[i].NodeIndices)
                if (owner.TryGetValue(ni, out int first) && first != i) throw new ArgumentException("Node " + ni + " is in group " + first + " and group " + i + " — a part fuses once.", nameof(jobs));
                else owner[ni] = i;
        }
        var plans = new FusePlan[jobs.Count]; int done = 0;
        var tasks = new System.Threading.Tasks.Task[jobs.Count];
        for (int i = 0; i < jobs.Count; i++)
        {
            int at = i; FuseJob job = jobs[i];
            tasks[i] = System.Threading.Tasks.Task.Run(() => { plans[at] = PlanFuse(source, job.NodeIndices, weldFraction, job.Name, job.CheckMirrored); System.Threading.Interlocked.Increment(ref done); });
        }
        while (true)
        {
            bool all;
            try { all = System.Threading.Tasks.Task.WaitAll(tasks, 100); }
            catch (AggregateException) { all = true; }   // a faulted plan: surfaced below as its own exception, not wrapped
            onTick?.Invoke(done);
            if (all) break;
        }
        foreach (var t in tasks) if (t.Exception != null) throw t.Exception.InnerExceptions.Count == 1 ? t.Exception.InnerExceptions[0] : t.Exception;
        // THE RUN'S MIRRORED VERDICT (2026-09-20, the frigate's deck). Pool every group's evidence: if the file clearly
        // stores its mirrored parts already facing outward, the groups that had too little evidence of their own are
        // re-planned with that answer. Only those groups are re-planned, and only when they hold mirrored parts.
        {
            int undoAll = 0, keepAll = 0;
            foreach (FusePlan pl in plans) if (pl != null && pl.CheckMirrored) { undoAll += pl.MirroredJudgedUndo; keepAll += pl.MirroredJudgedKeep; }
            int judgedRun = undoAll + keepAll;
            if (judgedRun >= 3 && undoAll * 10 >= judgedRun * 9)
            {
                var again = new List<int>();
                for (int i = 0; i < plans.Length; i++)
                {
                    FusePlan pl = plans[i];
                    if (pl == null || !pl.CheckMirrored || pl.Empty || pl.MirroredParts == 0) continue;
                    int own = pl.MirroredJudgedUndo + pl.MirroredJudgedKeep;
                    if (own >= 3 && pl.MirroredJudgedUndo * 10 >= own * 9) continue;   // it saw the answer itself
                    // A group that has seen CONTRARY evidence keeps its own counsel: the Romanic's group H judged one
                    // part pre-flipped against two that need the glTF reversal, and handing it the file's verdict cost
                    // it 12.5 % of its beam view. Only groups that saw nothing against the verdict follow it.
                    if (pl.MirroredJudgedKeep > 0) continue;
                    again.Add(i);
                }
                if (again.Count > 0)
                {
                    var redo = new System.Threading.Tasks.Task[again.Count];
                    for (int k = 0; k < again.Count; k++)
                    {
                        int at = again[k]; FusePlan pl = plans[at];
                        redo[k] = System.Threading.Tasks.Task.Run(() => plans[at] = PlanFuse(source, pl.NodeIndices, weldFraction, pl.FusedName, true, true));
                    }
                    System.Threading.Tasks.Task.WaitAll(redo);
                    foreach (var t in redo) if (t.Exception != null) throw t.Exception.InnerExceptions.Count == 1 ? t.Exception.InnerExceptions[0] : t.Exception;
                }
            }
        }
        Document document = Parse(source);
        JObject root = document.Root;
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        JArray meshes = (JArray)root["meshes"];
        JArray buffers = root["buffers"] as JArray;
        Accessors reader = BinReader(document, root, out byte[] originalData);
        var bin = new List<byte>(originalData);
        results = new List<Result>();
        foreach (FusePlan plan in plans) { ApplyPlan(root, nodes, meshes, bin, plan); results.Add(plan.Result); }
        buffers[0]["byteLength"] = bin.Count;
        document.Chunks[document.BinIndex].Data = bin.ToArray();
        byte[] bytes = Write(document);
        ValidateOutput(bytes);
        return bytes;
    }

    sealed class FusePlan
    {
        public readonly Result Result = new Result();
        public Dictionary<int, FusePrimitive> Primitives; public List<string> ExtraNames; public List<int> ExtraComps;
        public List<int> Picked; public HashSet<int> FusedMeshes; public string BaseName;
        public double Weld, WeldFraction, Longest; public int MadeConsistent, OpenJudged, OpenReversed, ClosedReversed, FaceCount, CollapsedFaces, NotOrientable;
        public string LargestIslands, StitchedLine, RewoundByPart, Timing, FrameLine;
        public string MirroredLine;   // the mirrored-part check's verdicts (null unless the check ran with the option on)
        public string TwinsLine, TwinsRestoredLine, AsAuthoredLine, FromAboveLine, UndersideLine;   // doubled-face diagnostics (null unless found) - appended AFTER the summary, which the Workshop shows as Details[0] (review of PR #82, P3)
        public int MirroredJudgedUndo, MirroredJudgedKeep, MirroredParts;   // this group's evidence, pooled across the run by FuseGroups
        public IList<int> NodeIndices; public string FusedName; public bool CheckMirrored;   // enough to re-plan the group once the run's verdict is known
        public bool Empty;   // no triangles: nothing to append, the sources keep their meshes, Result.Changed stays false
    }

    static FusePlan PlanFuse(byte[] source, IList<int> nodeIndices, double weldFraction, string fusedName, bool checkMirrored) => PlanFuse(source, nodeIndices, weldFraction, fusedName, checkMirrored, null);

    // `runVerdict`: the whole fuse run's answer to "does this FILE store its mirrored parts already facing outward",
    // pooled by FuseGroups from every group's evidence and handed back to the groups that had none of their own.
    // null = decide from this group alone (a single-group fuse, where the group IS the run).
    static FusePlan PlanFuse(byte[] source, IList<int> nodeIndices, double weldFraction, string fusedName, bool checkMirrored, bool? runVerdict)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (nodeIndices == null || nodeIndices.Count == 0) throw new ArgumentException("Nothing to fuse — no node indices.", nameof(nodeIndices));
        if (double.IsNaN(weldFraction) || weldFraction < 0) throw new ArgumentOutOfRangeException(nameof(weldFraction));
        // stage timing for the report ("timing: parse 300 ms; gather 120 ms; weld 300 ms; …"): a 99,000-face group took 55 s on
        // 2026-09-18. Each mark is charged to the work since the previous one, the clock running from before the parse.
        var stageClock = System.Diagnostics.Stopwatch.StartNew(); var timing = new List<string>();
        void Mark(string what) { timing.Add(what + " " + stageClock.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms"); stageClock.Restart(); }
        Document document = Parse(source);
        JObject root = document.Root;
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        JArray meshes = root["meshes"] as JArray ?? throw new InvalidDataException("GLB has no meshes array.");
        byte[] originalData;
        Accessors reader = BinReader(document, root, out originalData);
        var plan = new FusePlan { NodeIndices = nodeIndices, FusedName = fusedName, CheckMirrored = checkMirrored }; Result result = plan.Result;
        Mark("parse");

        // 1) gather every triangle of every chosen part in WORLD space, with normal / UV / material per vertex
        var pos = new List<Vec3>(); var nrm = new List<Vec3?>(); var uv = new List<double[]>(); var mat = new List<int>();
        var partOf = new List<int>();   // which chosen part each vertex came from (the lap-strip warning below)
        // Every OTHER vertex attribute (COLOR_n, TEXCOORD_1.., custom _NAMEs) rides along untouched and takes part in
        // the merge decision — the first fuse silently dropped vertex colours and second UV sets (review of 3052ed0).
        // TANGENT is the one exception: it is derived from winding + UVs, both of which this pass may change, so it is
        // dropped with a warning and left for the importer to recompute. JOINTS/WEIGHTS cannot occur (skins refused).
        var extraNames = new List<string>(); var extraComps = new List<int>();
        var extra = new List<double[][]>();   // per vertex, per extra attribute (null where the part lacked it)
        bool tangentsDropped = false;
        var tris = new List<int>();
        var partNames = new List<string>();
        var partMirrored = new List<bool>();   // per part: its world transform mirrors (the gather reversed its winding)
        var picked = nodeIndices.Distinct().ToList();
        var fusedMeshes = new HashSet<int>();
        foreach (int ni in picked)
        {
            JObject node = NodeWithMesh(nodes, ni, out int mi);
            string nodeName = (string)node["name"] ?? ("node " + ni);
            if (node["skin"] != null) throw new InvalidDataException("'" + nodeName + "' is skinned — fuse static parts only.");
            var mesh = meshes[mi] as JObject ?? throw new InvalidDataException("Mesh is not an object.");
            var primitives = mesh["primitives"] as JArray ?? throw new InvalidDataException("Mesh has no primitives.");
            double[] world = NodeWorldMatrix(nodes, ni);
            // glTF: a node whose transform has a negative determinant (a MIRRORED instance) renders its triangles
            // with the front face reversed. Baked to world space on an identity root, that winding must be swapped
            // or the part arrives inside-out — the Teutonic's port half is the starboard meshes under a
            // (0.0254, -0.0254, 0.0254) node, and without this every port island came in inverted (2026-09-15).
            bool mirrored = Det3(world) < 0;
            partNames.Add(nodeName); partMirrored.Add(mirrored); fusedMeshes.Add(mi);
            foreach (JObject primitive in TrianglePrimitives(primitives))
            {
                if (primitive["targets"] != null) throw new InvalidDataException("'" + nodeName + "' has morph targets — fuse static parts only.");
                var attrs = primitive["attributes"] as JObject ?? throw new InvalidDataException("Primitive has no attributes.");
                int posAcc = attrs.Value<int>("POSITION");
                int nAcc = attrs["NORMAL"] == null ? -1 : attrs.Value<int>("NORMAL");
                int uvAcc = attrs["TEXCOORD_0"] == null ? -1 : attrs.Value<int>("TEXCOORD_0");
                int material = primitive["material"] == null ? -1 : primitive.Value<int>("material");
                int vertCount = reader.Count(posAcc);
                if (nAcc >= 0 && reader.Count(nAcc) != vertCount) nAcc = -1;
                if (uvAcc >= 0 && reader.Count(uvAcc) != vertCount) uvAcc = -1;
                var extraAcc = new int[extraNames.Count]; for (int k = 0; k < extraAcc.Length; k++) extraAcc[k] = -1;
                foreach (JProperty attr in attrs.Properties())
                {
                    string name = attr.Name;
                    if (name == "POSITION" || name == "NORMAL" || name == "TEXCOORD_0") continue;
                    if (name == "TANGENT") { tangentsDropped = true; continue; }
                    if (name.StartsWith("JOINTS_", StringComparison.Ordinal) || name.StartsWith("WEIGHTS_", StringComparison.Ordinal)) continue;
                    int acc = attr.Value.Value<int>();
                    if (vertCount == 0 || reader.Count(acc) != vertCount) continue;
                    int comps = reader.Vector(acc, 0, 1).Length;
                    int k = extraNames.IndexOf(name);
                    if (k < 0) { extraNames.Add(name); extraComps.Add(comps); Array.Resize(ref extraAcc, extraNames.Count); k = extraNames.Count - 1; }
                    else if (extraComps[k] != comps) throw new InvalidDataException("'" + nodeName + "' stores " + name + " with " + comps + " components where an earlier part had " + extraComps[k] + " — fuse parts that agree.");
                    extraAcc[k] = acc;
                }
                double[] normalMatrix = NormalMatrix(world);   // inverse transpose: a (2,1,1) scale turned a sloped normal 35° off the surface through the plain matrix (review of 3052ed0)
                int baseV = pos.Count;
                for (uint v = 0; v < vertCount; v++)
                {
                    pos.Add(XForm(world, reader.Position(posAcc, v)));
                    if (nAcc >= 0) { double[] n = reader.Vector(nAcc, v, 3); nrm.Add(XFormDir(normalMatrix, new Vec3 { X = n[0], Y = n[1], Z = n[2] })); }
                    else nrm.Add(null);
                    uv.Add(uvAcc >= 0 ? reader.Vector(uvAcc, v, 2) : null);
                    mat.Add(material);
                    partOf.Add(partNames.Count - 1);
                    var ex = new double[extraNames.Count][];
                    for (int k = 0; k < extraAcc.Length; k++) if (extraAcc[k] >= 0) ex[k] = reader.Vector(extraAcc[k], v, extraComps[k]);
                    extra.Add(ex);
                }
                int indexCount = primitive["indices"] == null ? vertCount : reader.Count(primitive.Value<int>("indices"));
                if (indexCount % 3 != 0) throw new InvalidDataException("Triangle primitive index count is not divisible by three.");
                int triStart = tris.Count;
                for (uint i = 0; i < indexCount; i++)
                {
                    uint idx = primitive["indices"] == null ? i : reader.Index(primitive.Value<int>("indices"), i);
                    if (idx >= vertCount) throw new InvalidDataException("Primitive index exceeds its POSITION accessor.");
                    tris.Add(baseV + (int)idx);
                }
                if (mirrored) for (int t = triStart; t + 2 < tris.Count; t += 3) { int tmp = tris[t + 1]; tris[t + 1] = tris[t + 2]; tris[t + 2] = tmp; }
            }
        }
        int faceCount = tris.Count / 3;
        result.SourceTriangles = faceCount;
        result.VerticesBefore = pos.Count;
        if (tangentsDropped) result.Warnings.Add("TANGENT dropped from the fused mesh: tangents follow winding and UVs, which this pass may change — the importer recomputes them.");
        for (int v = 0; v < pos.Count; v++) { double[][] ex = extra[v]; if (ex.Length < extraNames.Count) { Array.Resize(ref ex, extraNames.Count); extra[v] = ex; } }   // parts read before a later part introduced an attribute
        if (faceCount == 0) { result.Warnings.Add("Nothing to fuse: the chosen parts carry no triangles."); plan.Empty = true; return plan; }   // nothing to apply: Changed stays false

        double[] mn = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        double[] mx = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (Vec3 p in pos) UpdateBounds(mn, mx, p);
        double longest = Math.Max(mx[0] - mn[0], Math.Max(mx[1] - mn[1], mx[2] - mn[2]));
        if (longest <= 0) longest = 1e-9;
        // "0" means coincident within float rounding, never bit-identical: parts carry different node transforms, so
        // the same seam point computed through two matrices differs at the 1e-6 level — with exact equality the
        // Teutonic's eight hull parts stayed eight islands ("islands 73 -> 73", user: "still separated").
        double rounding = longest * 1e-6;
        double weld = Math.Max(longest * weldFraction, rounding);

        Mark("gather");
        // 2) weld classes — union-find over vertices within `weld` (a hash grid, the 27 neighbouring cells)
        int[] WeldClasses(double distance)
        {
            var parent = new int[pos.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            double cellSize = distance > 0 ? distance : longest * 1e-7;
            double thresh2 = distance > 0 ? distance * distance : 0.0;
            var grid = new Dictionary<PositionKey, List<int>>();
            for (int i = 0; i < pos.Count; i++)
            {
                PositionKey k = CellOf(pos[i], cellSize);
                if (!grid.TryGetValue(k, out List<int> l)) grid.Add(k, l = new List<int>());
                l.Add(i);
            }
            for (int i = 0; i < pos.Count; i++)
            {
                PositionKey k = CellOf(pos[i], cellSize);
                for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue(new PositionKey { X = k.X + dx, Y = k.Y + dy, Z = k.Z + dz }, out List<int> l)) continue;
                    foreach (int j in l)
                    {
                        if (j <= i) continue;
                        if (FDist2(pos[i], pos[j]) <= thresh2) { int a = Find(i), b = Find(j); if (a != b) parent[a] = b; }
                    }
                }
            }
            var cls = new int[pos.Count];
            for (int i = 0; i < cls.Length; i++) cls[i] = Find(i);
            return cls;
        }
        // 3) faces -> edges by welded class; islands by edge adjacency
        // A face whose three corners weld to ONE class has collapsed (a rivet smaller than the weld distance). It is
        // kept in the output — triangles are preserved exactly — but it has no edges, so it joins no island and is
        // never judged: the first run on the Teutonic counted 1,570 of them as "open sheets" and the island count
        // went UP after welding (151 -> 1,721), the opposite of the weld's purpose. Blender deletes them; we keep them.
        List<List<int>> Islands(int[] cls, out long[] edgeKeys, out bool[] edgeDir, out Dictionary<long, List<int>> edgeFaces, out int collapsed)
        {
            edgeKeys = new long[faceCount * 3]; edgeDir = new bool[faceCount * 3];
            edgeFaces = new Dictionary<long, List<int>>(PairKeyComparer.Instance);
            collapsed = 0;
            var degenerate = new bool[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                int ca = cls[tris[f * 3]], cb = cls[tris[f * 3 + 1]], cc = cls[tris[f * 3 + 2]];
                if (ca == cb && cb == cc) { degenerate[f] = true; collapsed++; edgeKeys[f * 3] = edgeKeys[f * 3 + 1] = edgeKeys[f * 3 + 2] = -1; continue; }
                for (int e = 0; e < 3; e++)
                {
                    int a = cls[tris[f * 3 + e]], b = cls[tris[f * 3 + (e + 1) % 3]];
                    if (a == b) { edgeKeys[f * 3 + e] = -1; continue; }   // one collapsed edge (a needle): no adjacency through it
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    edgeKeys[f * 3 + e] = key; edgeDir[f * 3 + e] = a < b;   // true = walked from the smaller class to the larger
                    if (!edgeFaces.TryGetValue(key, out List<int> lf)) edgeFaces.Add(key, lf = new List<int>());
                    lf.Add(f);
                }
            }
            var island = new int[faceCount];
            for (int f = 0; f < faceCount; f++) island[f] = -1;
            var islands = new List<List<int>>();
            for (int f0 = 0; f0 < faceCount; f0++)
            {
                if (island[f0] >= 0 || degenerate[f0]) continue;
                var members = new List<int>(); var stack = new Stack<int>();
                island[f0] = islands.Count; stack.Push(f0);
                while (stack.Count > 0)
                {
                    int f = stack.Pop(); members.Add(f);
                    for (int e = 0; e < 3; e++)
                    {
                        long key = edgeKeys[f * 3 + e]; if (key < 0) continue;
                        foreach (int g in edgeFaces[key]) if (island[g] < 0) { island[g] = islands.Count; stack.Push(g); }
                    }
                }
                islands.Add(members);
            }
            return islands;
        }
        result.IslandsBefore = Islands(WeldClasses(rounding), out _, out _, out _, out _).Count;   // coincident positions only: what the source already connects
        int[] classes = WeldClasses(weld);
        // DOUBLE-SIDED BY DUPLICATION (2026-09-24, SMS Wespe's gun). A part that carries every face TWICE, wound both
        // ways, each copy with its own vertices - 6,000 of the gun's 6,053 faces. Welded by position, the two copies
        // share every class, so every edge becomes a FOUR-face edge, no face has a partner, the sheet walk pairs nothing
        // (62,928 sheets for 68,974 faces), and each face is judged alone by the radial score - the copies come out with
        // scrambled windings. Then the emission shares output vertices where attributes agree, both copies land on one
        // index triple, and Blender's importer - every Lab probe and every bake runs through it - drops duplicate
        // polygons, keeping one copy at random. Renders: a clean solid before the fuse, holes through the reinforce and
        // breech after. The cure is to keep the copies apart, as the source had them: a face whose three classes match
        // another's WITH THE OPPOSITE ORIENTATION is the second copy, and its vertices get classes of their own (offset
        // past every real class; classes are only ever compared and used as keys). Each copy is then its own manifold
        // sheet, judged whole, emitted on its own vertices. Same-way duplicates (a z-fighting copy) are left welded: an
        // importer dropping one of those loses nothing. A vertex used by BOTH a second-copy face and an unpaired face
        // stays welded, so a copy that shares vertices with its neighbour is not torn from it.
        var twinFace = new bool[faceCount];   // BOTH copies of every opposite-wound coincident pair: authored two-sided, never turned (see the flip stage)
        {
            var byTriple = new Dictionary<(int, int, int), List<int>>();
            for (int f = 0; f < faceCount; f++)
            {
                int a = classes[tris[f * 3]], b = classes[tris[f * 3 + 1]], c = classes[tris[f * 3 + 2]];
                if (a == b || b == c || a == c) continue;   // collapsed or needle: no face to pair with
                var key = SortedTriple(a, b, c);
                if (!byTriple.TryGetValue(key, out var l)) byTriple.Add(key, l = new List<int>());
                l.Add(f);
            }
            // ...and COINCIDE (review of PR #82, P2): the classes say "within the weld radius", which at a non-zero weld
            // is not the same thing - a thin plate's two skins 0.05 apart under a 0.08 weld share every class, wound the
            // opposite way, and are not a doubled face but the very seam the weld was asked to close. A twin is a copy
            // at the SAME position: every corner of one within `rounding` (a millionth of the model) of a corner of the
            // other. `pos` still holds the authored positions here; the class centroids are applied below.
            double coincide2 = rounding * rounding;
            bool Coincident(int fa, int fb)
            {
                for (int cb = 0; cb < 3; cb++)
                {
                    Vec3 q = pos[tris[fb * 3 + cb]]; bool hit = false;
                    for (int ca = 0; ca < 3 && !hit; ca++) hit = FDist2(pos[tris[fa * 3 + ca]], q) <= coincide2;
                    if (!hit) return false;
                }
                return true;
            }
            // Pair by coincidence WITHIN the group, every face against every earlier one (second review of PR #82, P2):
            // comparing only against the group's first face let a nearby, non-coincident face in front hide the genuine
            // pair behind it - three quads, 12 vertices, welded to 4, one copy rewound, duplicate faces left for the
            // importer. A face that already is somebody's second copy is not paired again.
            var secondFace = new bool[faceCount]; int twinFaces = 0;
            foreach (var group in byTriple.Values)
            {
                if (group.Count < 2) continue;
                for (int gi = 1; gi < group.Count; gi++)
                {
                    int g = group[gi]; if (secondFace[g]) continue;
                    bool og = ClassOrientation(classes, tris, g);
                    for (int fi = 0; fi < gi; fi++)
                    {
                        int f = group[fi];
                        if (secondFace[f] || ClassOrientation(classes, tris, f) == og || !Coincident(f, g)) continue;
                        secondFace[g] = true; twinFace[g] = true; twinFace[f] = true; twinFaces++; break;
                    }
                }
            }
            if (twinFaces > 0)
            {
                var onlySecond = new bool[pos.Count]; var touched = new bool[pos.Count];
                for (int f = 0; f < faceCount; f++) for (int c = 0; c < 3; c++)
                {
                    int v = tris[f * 3 + c];
                    if (!touched[v]) { touched[v] = true; onlySecond[v] = secondFace[f]; }
                    else if (!secondFace[f]) onlySecond[v] = false;
                }
                int split = 0;
                for (int v = 0; v < pos.Count; v++) if (onlySecond[v]) { classes[v] += pos.Count; split++; }
                plan.TwinsLine = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "twins: {0} face(s) coincide with another face wound the other way (a part double-sided by duplication); the second copy keeps its own {1} vertices, so each copy is judged and emitted whole", twinFaces, split);
            }
        }
        Mark("weld");   // both weld passes (the coincident-only count above and the real one) are charged here, where they run
        // ONE position per welded class. Connectivity is by class, but output vertices are emitted separately wherever
        // UV, normal or material differ, and each kept its own authored position: two plates 0.01 apart across a UV
        // seam reported one island and still rendered the 0.01 gap (review of 4e748c1). Every vertex of a class now
        // sits at the class centroid — at weld 0 a move within float rounding, at 0.5‰ exactly what was asked for —
        // and every judgement below (winding, direction, vertex normals) sees the geometry that will be written.
        {
            var sum = new Dictionary<int, Vec3>(); var count = new Dictionary<int, int>();
            for (int v = 0; v < pos.Count; v++)
            {
                if (sum.TryGetValue(classes[v], out Vec3 s)) { sum[classes[v]] = FAdd(s, pos[v]); count[classes[v]]++; }
                else { sum.Add(classes[v], pos[v]); count.Add(classes[v], 1); }
            }
            for (int v = 0; v < pos.Count; v++) if (count[classes[v]] > 1) pos[v] = FScale(sum[classes[v]], 1.0 / count[classes[v]]);
        }
        List<List<int>> allIslands = Islands(classes, out long[] fEdgeKeys, out bool[] fEdgeDir, out Dictionary<long, List<int>> fEdgeFaces, out int collapsedFaces);
        result.IslandsAfter = allIslands.Count;

        // 3b) MIRRORED PARTS JUDGED BY THEIR PLAIN NEIGHBOURS (2026-09-19, the Confederate frigate; an OPTION, off by
        // default, user: "make it an option"). The gather reverses every mirrored part, as glTF requires of a
        // negative-determinant node, and that is what the Teutonic needed. The frigate's file stores its mirrored hull
        // half ALREADY facing outward, so the same reversal turned that whole side inward: 94.8 % of the fused hull
        // rendered back-facing from that side, 0.1 % for the same 60 parts unfused. Welded to the correct half in one
        // island, the two halves cancelled every direction signal (volume +0.00, inside-out +0.00, twins 3605 / 3514)
        // and the island was left as it stood. A PLAIN part's winding is never in doubt (no rule touched it), so it
        // is the reference: a mirrored part whose shared two-face edges run the SAME way as its plain neighbours' is
        // inside-out relative to them, and its reversal is undone. Parts that touch only other mirrored parts inherit
        // the verdict ring by ring; a part with too little evidence keeps the glTF rule. Evaluated always, so the
        // report can say when it would matter; applied only when asked.
        if (partMirrored.Contains(true))
        {
            int nParts = partNames.Count;
            var decided = new bool[nParts]; var undo = new bool[nParts];
            var conflictAll = new long[nParts];
            int Part(int face) => partOf[tris[face * 3]];
            bool Walk(int face, long key) { for (int e = 0; e < 3; e++) if (fEdgeKeys[face * 3 + e] == key) return fEdgeDir[face * 3 + e]; return false; }
            bool progress = true;
            while (progress)
            {
                progress = false;
                var agree = new long[nParts]; var conflict = new long[nParts];
                foreach (KeyValuePair<long, List<int>> kv in fEdgeFaces)
                {
                    List<int> lf = kv.Value; if (lf.Count != 2) continue;   // only a clean two-face seam is evidence
                    int f = lf[0], g = lf[1]; int pf = Part(f), pg = Part(g);
                    if (pf == pg) continue;
                    bool df = Walk(f, kv.Key) ^ (decided[pf] && undo[pf]), dg = Walk(g, kv.Key) ^ (decided[pg] && undo[pg]);
                    bool same = df == dg;   // both faces walk the shared edge the same way: one of them is inside-out…
                    // …unless they are a LAP, the consistency pass's own rule below (the Teutonic's Object_8, 671 strips): a strip
                    // lying ON its plate, stitched along one edge and facing the same way, walks that edge the same way as the
                    // plate on purpose. Same traversal AND surfaces already facing the same way = consistent as authored. Without
                    // it, three mirrored lap strips gave a unanimous "already facing outward" verdict and 18 correct faces were
                    // turned inward (review of PR #67). A decided part about to be undone is judged as it will be written.
                    if (same)
                    {
                        Vec3 na = FCross(FSub(pos[tris[f * 3 + 1]], pos[tris[f * 3]]), FSub(pos[tris[f * 3 + 2]], pos[tris[f * 3]]));
                        Vec3 nb = FCross(FSub(pos[tris[g * 3 + 1]], pos[tris[g * 3]]), FSub(pos[tris[g * 3 + 2]], pos[tris[g * 3]]));
                        if (decided[pf] && undo[pf]) na = FScale(na, -1.0);
                        if (decided[pg] && undo[pg]) nb = FScale(nb, -1.0);
                        double la = FLen(na), lb = FLen(nb);
                        if (la > 1e-12 && lb > 1e-12 && FDot(na, nb) / (la * lb) > 0.9) same = false;
                    }
                    bool refF = !partMirrored[pf] || decided[pf], refG = !partMirrored[pg] || decided[pg];
                    if (partMirrored[pf] && !decided[pf] && refG) { if (same) conflict[pf]++; else agree[pf]++; }
                    if (partMirrored[pg] && !decided[pg] && refF) { if (same) conflict[pg]++; else agree[pg]++; }
                }
                for (int p = 0; p < nParts; p++)
                    if (partMirrored[p] && !decided[p] && agree[p] + conflict[p] >= 3)
                    { decided[p] = true; undo[p] = conflict[p] > agree[p]; conflictAll[p] = conflict[p]; progress = true; }
            }
            // A VERDICT ONLY WHEN THE GROUP GIVES A CLEAR ONE. How mirrored parts are stored is a property of the program
            // that exported the file, not of each part: on the frigate all 5 mirrored parts with plain neighbours already
            // faced outward, and one of the 14 without neighbours (Object_961) then carried 2,443 of the 2,462 back-facing
            // cells left on the fused hull. But a single part's seam evidence is not enough on its own: the Romanic's
            // group H judged 1 pre-flipped against 2 not, and undoing that one part made the group WORSE (2,667 -> 2,955
            // back-facing cells from the beam). So nothing is undone unless at least three parts were judged and nine in
            // ten of them agree the file is pre-flipped. Then every mirrored part is undone, those without evidence
            // following the convention, except a part whose own evidence says it needs the glTF reversal. Mixed or thin
            // evidence changes nothing at all.
            int judgedUndo = 0, judgedKeep = 0;
            for (int p = 0; p < nParts; p++) if (decided[p]) { if (undo[p]) judgedUndo++; else judgedKeep++; }
            int judgedAll = judgedUndo + judgedKeep;
            plan.MirroredJudgedUndo = judgedUndo; plan.MirroredJudgedKeep = judgedKeep; plan.MirroredParts = partMirrored.Count(m => m);
            // A GROUP OFTEN CANNOT SEE THE ANSWER. How mirrored parts are stored is a property of the FILE, but the
            // evidence (a mirrored part welded to a plain one) lives wherever it happens to live: on the frigate, group
            // A has five such parts and the deck group has none, so the deck kept the glTF reversal and half of it
            // rendered see-through. FuseGroups pools the run's evidence and hands the verdict back here; a group fused
            // on its own still decides for itself.
            bool clear = runVerdict ?? (judgedAll >= 3 && judgedUndo * 10 >= judgedAll * 9);
            var undoSet = new HashSet<int>(); var undoNames = new List<string>(); int byConvention = 0; long undoConflicts = 0;
            if (clear)
                for (int p = 0; p < nParts; p++)
                {
                    if (!partMirrored[p] || (decided[p] && !undo[p])) continue;   // a judged dissenter keeps the glTF rule
                    undoSet.Add(p); undoNames.Add(partNames[p]);
                    if (decided[p]) undoConflicts += conflictAll[p]; else byConvention++;
                }
            int mirroredCount = partMirrored.Count(m => m);
            if (undoSet.Count > 0 && checkMirrored)
            {
                for (int f = 0; f < faceCount; f++)
                    if (undoSet.Contains(Part(f))) { int t = tris[f * 3 + 1]; tris[f * 3 + 1] = tris[f * 3 + 2]; tris[f * 3 + 2] = t; }
                allIslands = Islands(classes, out fEdgeKeys, out fEdgeDir, out fEdgeFaces, out collapsedFaces);   // winding changed: re-read edge directions
            }
            var inv3 = System.Globalization.CultureInfo.InvariantCulture;
            string names = string.Join(", ", undoNames.Take(8)) + (undoNames.Count > 8 ? ", ..." : "");
            if (checkMirrored)
                plan.MirroredLine = clear
                    ? string.Format(inv3,
                        "mirrored parts checked against their plain neighbours: {0} of {1} judged say this file already stores them facing outward{6}, so the glTF reversal is undone for {2} part(s) ({3}){4}{5}",
                        judgedUndo, judgedAll, undoSet.Count, names,
                        byConvention > 0 ? string.Format(inv3, "; {0} of them had no plain neighbour and follow that convention", byConvention) : "",
                        judgedKeep > 0 ? string.Format(inv3, "; {0} whose own seams disagree keep the reversal", judgedKeep) : "",
                        runVerdict.HasValue && judgedAll < 3 ? " (the verdict of the other groups in this fuse)" : "")
                    : judgedAll >= 3 && judgedKeep * 10 >= judgedAll * 9
                    ? string.Format(inv3,
                        "mirrored parts checked against their plain neighbours: {0} of {1} judged confirm the glTF reversal is right (a file stored the standard way), nothing changed",
                        judgedKeep, judgedAll)
                    : string.Format(inv3,
                        "mirrored parts checked against their plain neighbours: {0} of {1} mirrored part(s) judged ({2} say already facing outward, {3} say the glTF reversal is right), not enough agreement to act on, nothing changed",
                        judgedAll, mirroredCount, judgedUndo, judgedKeep);
            else if (clear)
                result.Warnings.Add(string.Format(inv3,
                    "{0} of {1} mirrored part(s) that touch plain parts disagree with them ({2} seam edges run the same way): this file stores its mirrored parts already facing outward, so the glTF reversal turns {3} part(s) inward (see-through). Tick 'Check mirrored parts' and fuse again.",
                    judgedUndo, judgedAll, undoConflicts, undoSet.Count));
        }

        // LAP / TRIM STRIPS (2026-09-15, the Teutonic's Object_8): a part most of whose vertices coincide with OTHER parts'
        // vertices AND whose faces lie flat on those parts' faces is stitched onto their surface — riveted strakes lying
        // on the plates, trim on a wall. Welded in, it becomes a flap attached along the middle of the plate, and a
        // later reduction creases the plate along every strip (the dark lines along the strakes). It is not wrong to
        // fuse it; it is wrong to reduce the result. Say so — ONCE per fuse, naming the parts. Vertex sharing alone was
        // the first test and it was over-eager (2026-09-16, the lifeboats): a gunwale rail or keel band attached along
        // its whole length shares 100 % of its vertices by construction and is not a lap — its faces stand off the hull.
        // A lap's faces are PARALLEL to the plate's faces at the shared vertices (measured: 533 of 656 Object_8 strips).
        Mark("islands");
        string stitchedLine = null;
        if (picked.Count > 1)
        {
            var partsInClass = new Dictionary<int, HashSet<int>>();
            for (int v = 0; v < pos.Count; v++) { if (!partsInClass.TryGetValue(classes[v], out HashSet<int> set)) partsInClass.Add(classes[v], set = new HashSet<int>()); set.Add(partOf[v]); }
            var vertsOf = new int[picked.Count]; var sharedOf = new int[picked.Count];
            for (int v = 0; v < pos.Count; v++) { vertsOf[partOf[v]]++; if (partsInClass[classes[v]].Count > 1) sharedOf[partOf[v]]++; }
            // faces with a corner in a shared class, grouped by that class, with unit normals (authored winding is fine: parallel is parallel)
            var facesAtClass = new Dictionary<int, List<int>>();
            var unit = new Vec3?[faceCount];
            for (int f = 0; f < faceCount; f++)
                for (int c = 0; c < 3; c++)
                {
                    int cls = classes[tris[f * 3 + c]];
                    if (partsInClass[cls].Count < 2) continue;
                    if (!facesAtClass.TryGetValue(cls, out List<int> lf)) facesAtClass.Add(cls, lf = new List<int>());
                    if (!lf.Contains(f)) lf.Add(f);
                    if (unit[f] == null) { Vec3 n = FCross(FSub(pos[tris[f * 3 + 1]], pos[tris[f * 3]]), FSub(pos[tris[f * 3 + 2]], pos[tris[f * 3]])); unit[f] = FLen(n) > 1e-18 ? FUnit(n) : new Vec3 { X = 0, Y = 0, Z = 0 }; }
                }
            var touching = new int[picked.Count]; var lying = new int[picked.Count];
            for (int f = 0; f < faceCount; f++)
            {
                if (unit[f] == null) continue;   // no shared corner
                int p = partOf[tris[f * 3]]; touching[p]++;
                // lying = parallel to a face of another part at a shared corner AND its centre sits on that face (within
                // 1e-3 of the model's length: Object_8's strips sit 1e-4 off their plates; a lifeboat's cover shares the
                // gunwale's vertices and is parallel to the rim faces there, but its centres are 0.5 m off them — measured 2026-09-16)
                Vec3 centre = FScale(FAdd(FAdd(pos[tris[f * 3]], pos[tris[f * 3 + 1]]), pos[tris[f * 3 + 2]]), 1.0 / 3.0);
                bool onSurface = false;
                for (int c = 0; c < 3 && !onSurface; c++)
                {
                    if (!facesAtClass.TryGetValue(classes[tris[f * 3 + c]], out List<int> others)) continue;
                    foreach (int g in others)
                        if (partOf[tris[g * 3]] != p && Math.Abs(FDot(unit[f].Value, unit[g].Value)) > 0.9   // parallel either way: a lap wound the other way is still a lap (review of ce91915)
                            && PointTriangleDistance(centre, pos[tris[g * 3]], pos[tris[g * 3 + 1]], pos[tris[g * 3 + 2]]) < longest * 1e-3) { onSurface = true; break; }
                }
                if (onSurface) lying[p]++;
            }
            var laps = new List<string>(); var stitching = new List<string>();
            for (int p = 0; p < picked.Count; p++)
            {
                if (vertsOf[p] == 0 || sharedOf[p] * 5 < vertsOf[p] * 4 || vertsOf[p] * 4 >= pos.Count) continue;   // >= 80 % of its vertices shared: abutting plates share 50-65 % along their seams and are NOT laps (measured on the Teutonic)
                double lie = touching[p] > 0 ? 100.0 * lying[p] / touching[p] : 0.0;
                stitching.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} {1:0}% verts shared, {2:0}% of its touching faces lying on them", partNames[p], 100.0 * sharedOf[p] / vertsOf[p], lie));
                if (lying[p] * 5 >= touching[p] * 4 && touching[p] > 0) laps.Add(partNames[p]);   // >= 80 % of its faces at the seam are parallel to the other part's: it lies ON the surface
            }
            if (stitching.Count > 0) stitchedLine = "stitched parts: " + string.Join("; ", stitching);   // added AFTER the summary and the islands line: Details[0] is what the Workshop status shows (review of ce91915)
            if (laps.Count > 0)
                result.Warnings.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0} lap/trim strip(s) lying on the other parts' surface: {1}. Fused in, each becomes a flap along the middle of the plate and a later reduction creases the plate along it (dark lines). Leave them out of the group, or keep the fused part unreduced.",
                    laps.Count, string.Join(", ", laps.Select(n => "'" + n + "'"))));
        }

        Mark("stitched");
        // 4) ORIENTATION SHEETS, and consistency by MAJORITY within each. Parity (which way a face is wound relative to
        // its neighbour) propagates across TWO-face edges only. An edge shared by three or more faces — a deck meeting
        // a hull side in the middle of the plate, a lap strip stitched onto plating — is a junction no two faces own, so
        // an island (stage 3, connectivity through ANY shared edge) can hold several parity-connected SHEETS joined only
        // at such junctions, each sheet's winding relative to the others being whatever the seeds assigned. Until
        // 2026-09-18 ONE majority flip and ONE direction verdict covered the whole island, and for the SS Romanic's
        // group D (hull shell + decks + bulwarks, one 25,000-face island of 19 % boundary) they fell on either side of a
        // coin: the starboard deck rendered transparent with 12 parts in the group and solid with 14. Sheets are the
        // units now: the majority is taken within a sheet (the minimal correction that makes it consistent), the
        // orientability test is the sheet's own, and stage 5 judges each sheet's direction by its own volume, twins
        // and score. A manifold island is one sheet, so hulls, boats and decks read exactly as before. Partnering the
        // two faces that geometrically CONTINUE each other at a junction was tried and rejected the same day: the
        // Romanic's Object_4 alone went from 0 to 3 non-orientable sheets (its inner and outer skins meet its decks at
        // three-face rims, and a continuation pair there closes odd cycles); a branch at a junction starts its own sheet.
        // FROM ABOVE (2026-09-24, the Wespe's deck plating round the hatch): is anything of the MODEL above a face's
        // centroid? Every mesh node of the file is an occluder, as the belly sampler reads every node - measured on
        // the ship, the group alone read 57 ceilings as "exposed" whose decks belong to other groups. The group's
        // OWN faces come from the welded geometry (`pos`, the class centroids), never from the file (review of PR
        // #83, P2): a weld moves a face, and its pre-weld copy read from the file sat above the welded face and
        // shaded it - a 0.005 seam under a 0.01 weld turned a well's floor. A column grid over (x, z), built on first
        // use; a vertical ray from the model's top, Möller-Trumbore per candidate; the face itself and a copy
        // coincident with it (a doubled surface) are not occluders. Entries >= 0 index `occluders` (other nodes,
        // three corners each); entries < 0 are the group's own faces, -1 - f.
        Dictionary<long, List<int>> columns = null; var occluders = new List<Vec3>(); double columnCell = 1, modelTop = 0, modelBottom = 0;
        void EnsureOccluders()
        {
            if (columns != null) return;
            columns = new Dictionary<long, List<int>>();
            var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity }; var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            var fusedNodes = new HashSet<int>(picked);
            try
            {
                for (int ni = 0; ni < nodes.Count; ni++)
                {
                    if (fusedNodes.Contains(ni)) continue;   // the group's faces are added below, welded
                    var node = nodes[ni] as JObject; if (node?["mesh"] == null) continue;
                    int mi = node.Value<int>("mesh"); if (mi < 0 || mi >= meshes.Count) continue;
                    var pl = (meshes[mi] as JObject)?["primitives"] as JArray; if (pl == null) continue;
                    double[] world = NodeWorldMatrix(nodes, ni);
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
                            occluders.Add(a); occluders.Add(b); occluders.Add(c);
                            UpdateBounds(lo, hi, a); UpdateBounds(lo, hi, b); UpdateBounds(lo, hi, c);
                        }
                    }
                }
            }
            catch (Exception) { occluders.Clear(); }
            for (int f = 0; f < faceCount; f++) for (int c = 0; c < 3; c++) UpdateBounds(lo, hi, P(f, c));
            modelTop = hi[1]; modelBottom = lo[1];
            columnCell = Math.Max(Math.Max(hi[0] - lo[0], hi[2] - lo[2]) / 256.0, 1e-9);
            void Register(int id, Vec3 a, Vec3 b, Vec3 c)
            {
                long x0 = (long)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)) / columnCell), x1 = (long)Math.Floor(Math.Max(a.X, Math.Max(b.X, c.X)) / columnCell);
                long z0 = (long)Math.Floor(Math.Min(a.Z, Math.Min(b.Z, c.Z)) / columnCell), z1 = (long)Math.Floor(Math.Max(a.Z, Math.Max(b.Z, c.Z)) / columnCell);
                if (x1 - x0 > 256 || z1 - z0 > 256) return;   // wider than the model: a stray sliver, not an occluder
                for (long x = x0; x <= x1; x++) for (long z = z0; z <= z1; z++)
                { long key = (x << 32) ^ (z & 0xffffffffL); if (!columns.TryGetValue(key, out List<int> l)) columns.Add(key, l = new List<int>()); l.Add(id); }
            }
            for (int t = 0; t < occluders.Count; t += 3) Register(t, occluders[t], occluders[t + 1], occluders[t + 2]);
            for (int f = 0; f < faceCount; f++) Register(-1 - f, P(f, 0), P(f, 1), P(f, 2));
        }
        // ...and from BELOW, the same column upward from the model's bottom: what tells a well's floor (the hull
        // beneath it) from an upturned boat's bottom (nothing beneath it) - both face up and both see the sky.
        bool ExposedFromAbove(int f) => Exposed(f, true);
        bool Exposed(int f, bool fromAbove)
        {
            EnsureOccluders();
            Vec3 c = FScale(FAdd(FAdd(P(f, 0), P(f, 1)), P(f, 2)), 1.0 / 3.0);
            double top = fromAbove ? modelTop + columnCell : modelBottom - columnCell, tf = fromAbove ? top - c.Y : c.Y - top; if (tf <= 0) return true;
            var o = new Vec3 { X = c.X, Y = top, Z = c.Z }; var d = new Vec3 { X = 0, Y = fromAbove ? -1 : 1, Z = 0 };
            long key = ((long)Math.Floor(c.X / columnCell) << 32) ^ ((long)Math.Floor(c.Z / columnCell) & 0xffffffffL);
            if (!columns.TryGetValue(key, out List<int> l)) return true;
            foreach (int t in l)
            {
                double hit;
                if (t >= 0) hit = RayTriangle(o, d, occluders[t], occluders[t + 1], occluders[t + 2]);
                else { int g = -1 - t; if (g == f) continue; hit = RayTriangle(o, d, P(g, 0), P(g, 1), P(g, 2)); }
                if (hit > 0 && hit < tf - 1e-6 * Math.Max(1.0, tf)) return false;
            }
            return true;
        }
        // HOW FAR IS THE NEAREST THING ABOVE OR BELOW A FACE, AND WHICH WAY DOES IT FACE (2026-09-26, HMS Svea's
        // fighting top): the same column, a ray from the face's own centroid straight up or down, the distance to the
        // first hit - +inf when nothing is there - and whether that hit shows its FRONT to the ray (its authored normal
        // points back at the face: a floor seen from above, a ceiling seen from below) or its back (the inside of a
        // hull's bottom plating seen from within). Coincident copies of the face (a surface doubled by duplication)
        // sit at distance zero and are skipped.
        double FreeRun(int f, bool upward, out bool hitFront)
        {
            EnsureOccluders(); hitFront = false;
            Vec3 c = FScale(FAdd(FAdd(P(f, 0), P(f, 1)), P(f, 2)), 1.0 / 3.0);
            var d = new Vec3 { X = 0, Y = upward ? 1 : -1, Z = 0 };
            long key = ((long)Math.Floor(c.X / columnCell) << 32) ^ ((long)Math.Floor(c.Z / columnCell) & 0xffffffffL);
            if (!columns.TryGetValue(key, out List<int> l)) return double.PositiveInfinity;
            double best = double.PositiveInfinity, skin = 1e-6 * Math.Max(1.0, modelTop - modelBottom);
            foreach (int t in l)
            {
                double hit; Vec3 n;
                if (t >= 0) { hit = RayTriangle(c, d, occluders[t], occluders[t + 1], occluders[t + 2]); n = FCross(FSub(occluders[t + 1], occluders[t]), FSub(occluders[t + 2], occluders[t])); }
                else { int g = -1 - t; if (g == f) continue; hit = RayTriangle(c, d, P(g, 0), P(g, 1), P(g, 2)); n = FaceNormal(g); }
                if (hit > skin && hit < best) { best = hit; hitFront = FDot(n, d) < 0; }
            }
            return best;
        }
        var flip = new bool[faceCount];
        int islandsMadeConsistent = 0, islandsNotOrientable = 0, asAuthoredSheets = 0, fromAboveKept = 0, reversalsVetoed = 0, undersidesKept = 0;
        bool DirOf(int face, long key) { for (int e = 0; e < 3; e++) if (fEdgeKeys[face * 3 + e] == key) return fEdgeDir[face * 3 + e]; return false; }
        long PairKey(int f, int g) => f < g ? ((long)f << 32) | (uint)g : ((long)g << 32) | (uint)f;
        Vec3 P(int f, int corner) => pos[tris[f * 3 + corner]];
        Vec3 FaceNormal(int f)   // area-weighted, with the CURRENT winding (authored while `flip` is still all false)
        {
            Vec3 n = FCross(FSub(P(f, 1), P(f, 0)), FSub(P(f, 2), P(f, 0)));
            return flip[f] ? FScale(n, -1.0) : n;
        }
        // the partner of every face-edge: the face across a two-face edge, -1 at a rim or a junction
        var partner = new int[faceCount * 3]; for (int k = 0; k < partner.Length; k++) partner[k] = -1;
        for (int f = 0; f < faceCount; f++) for (int e = 0; e < 3; e++)
        {
            long key = fEdgeKeys[f * 3 + e]; if (key < 0) continue;
            List<int> lf = fEdgeFaces[key]; if (lf.Count != 2) continue;
            partner[f * 3 + e] = lf[0] == f ? lf[1] : lf[0];
        }
        // Do two partnered faces walk their shared edge the SAME way (inconsistent neighbours)? Both walking it the
        // same way = inconsistent... unless they are a LAP: the Teutonic's Object_8 is 671 riveted lap strips lying ON
        // the plates, stitched to them along one edge and authored facing the SAME way as the plate beneath. In
        // manifold terms a face folded back over its neighbour must face the opposite way (a thin solid's lip), so the
        // plain rule "corrected" every strip to face inward and they rendered as dark lines (2026-09-15). Same
        // traversal AND authored normals already agreeing = the two faces sit on the same side of the edge on purpose:
        // consistent as authored, parity equal, nothing to correct. Applied on the walk's tree edges; the cycle-closing
        // edges keep the raw traversal (see the count below for why).
        bool SameWay(int fa, int e, int fb, long key)
        {
            bool same = fEdgeDir[fa * 3 + e] == DirOf(fb, key);
            if (same)
            {
                Vec3 na = FaceNormal(fa), nb = FaceNormal(fb);
                double la = FLen(na), lb = FLen(nb);
                if (la > 1e-12 && lb > 1e-12 && FDot(na, nb) / (la * lb) > 0.9) same = false;
            }
            return same;
        }
        var sheets = new List<List<int>>();
        var sheetOf = new int[faceCount]; for (int f = 0; f < faceCount; f++) sheetOf[f] = -1;
        var parityOf = new int[faceCount];
        var edgeSame = new Dictionary<long, bool>(PairKeyComparer.Instance);   // every partnered pair walked (keyed by the pair): were its faces walking the edge the same way (after the lap rule)?
        foreach (List<int> islandFaces in allIslands)
            foreach (int seed in islandFaces)
            {
                if (sheetOf[seed] >= 0) continue;
                int sid = sheets.Count; var members = new List<int>();
                sheetOf[seed] = sid; parityOf[seed] = 0; var stack = new Stack<int>(); stack.Push(seed);
                while (stack.Count > 0)
                {
                    int fa = stack.Pop(); members.Add(fa);
                    for (int e = 0; e < 3; e++)
                    {
                        long key = fEdgeKeys[fa * 3 + e]; if (key < 0) continue;
                        int fb = partner[fa * 3 + e];
                        if (fb < 0 || fb == fa || sheetOf[fb] >= 0) continue;
                        bool same = SameWay(fa, e, fb, key);
                        edgeSame[PairKey(fa, fb)] = same;
                        parityOf[fb] = parityOf[fa] ^ (same ? 1 : 0);
                        sheetOf[fb] = sid; stack.Push(fb);
                    }
                }
                sheets.Add(members);
            }
        var islandConflict = new string[sheets.Count];   // per sheet: same-traversal edges before, still unsatisfied after the flip (the "largest islands" line)
        var joinMinor = new int[sheets.Count]; for (int k = 0; k < joinMinor.Length; k++) joinMinor[k] = -1;   // the parity colour of a minority kept as a join (below), else -1: the direction pass leaves those faces alone
        var notOrientable = new bool[sheets.Count];      // a sheet the parity pass refused is left alone by the direction pass too (review of 0097bd5)
        for (int ii = 0; ii < sheets.Count; ii++)
        {
            List<int> isl = sheets[ii];
            int ones = 0; foreach (int f in isl) ones += parityOf[f];
            // every 2-face edge (tree or not), each once, with the lap-aware relation the walk used: same-traversal
            // count before, and after the WALK's parity assignment how many edges are still unsatisfied — a
            // 2-colourable sheet (the hull: one seam, one region) resolves to zero, a non-orientable construction (a
            // propeller blade with fins) cannot. The walk's count is what decides orientability below (PR #81 review):
            // the repair lowers it, and a genuinely non-orientable sheet pushed under the threshold by a repair would be
            // majority-flipped — the exact damage the threshold exists to prevent.
            var sheetEdges = new List<(int a, int b, bool same)>();
            int sameBefore = 0, unsatisfied = 0, twoFaceEdges = 0;
            var conflicts = new Dictionary<string, int>();   // "partA~partB (n-face edge)" -> unsatisfied pairs there: WHERE a sheet fails to orient
            {
                foreach (int f in isl) for (int e = 0; e < 3; e++)
                {
                    long key = fEdgeKeys[f * 3 + e]; int g = partner[f * 3 + e];
                    if (key < 0 || g < 0 || g < f) continue;   // each partnered pair once
                    twoFaceEdges++;
                    // a cycle-closing edge the walk never traversed reads the RAW traversal, without the lap exemption.
                    // Applying the exemption here was tried (PR #81 review) and measured: 35 faces moved on the Romanic
                    // and one turned see-through, because this count is what decides orientability and that decision
                    // was tuned on the Romanic's three-face rims. The repair votes with the same relation as the count.
                    bool sm; if (!edgeSame.TryGetValue(PairKey(f, g), out sm)) sm = fEdgeDir[f * 3 + e] == DirOf(g, key);
                    sheetEdges.Add((f, g, sm));
                    if (sm) sameBefore++;
                    if ((parityOf[f] ^ parityOf[g]) != (sm ? 1 : 0))
                    {
                        unsatisfied++;
                        int pa = partOf[tris[f * 3]], pb = partOf[tris[g * 3]];
                        string ck = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}~{1} ({2}-face edge)", partNames[Math.Min(pa, pb)], partNames[Math.Max(pa, pb)], fEdgeFaces[key].Count);
                        conflicts[ck] = conflicts.TryGetValue(ck, out int cc) ? cc + 1 : 1;
                    }
                }
            }
            bool orientable = !(unsatisfied > 0 && unsatisfied * 20 > sameBefore && ones > 0 && ones < isl.Count);
            // THE REPAIR, on accepted sheets only: a face's colour becomes the one most of its own edges support (the
            // bridge case, RepairParity). Each recolouring strictly lowers the sheet's unsatisfied count, so the count
            // reported after it can only be lower than the walk's.
            int repaired = 0, unsatisfiedAfter = unsatisfied;
            if (orientable && unsatisfied > 0 && ones > 0 && ones < isl.Count)
            {
                repaired = RepairParity(parityOf, isl, sheetEdges);
                if (repaired > 0)
                {
                    ones = 0; foreach (int f in isl) ones += parityOf[f];
                    unsatisfiedAfter = 0; foreach (var ed in sheetEdges) if ((parityOf[ed.a] ^ parityOf[ed.b]) != (ed.same ? 1 : 0)) unsatisfiedAfter++;
                }
            }
            islandConflict[ii] = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} of {1} edges same-way, {2} unsatisfied after{3}", sameBefore, twoFaceEdges, unsatisfied,
                repaired > 0 ? string.Format(System.Globalization.CultureInfo.InvariantCulture, " ({0} recoloured, {1} left)", repaired, unsatisfiedAfter) : "");
            if (conflicts.Count > 0)   // the reader's next question is WHERE: the four heaviest part pairs
                islandConflict[ii] += " at " + string.Join(", ", conflicts.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key + " ×" + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            // NOT ORIENTABLE BY TRAVERSAL (2026-09-17, the Teutonic's propellers): a blade renders right yet 32 of its
            // 1,287 edges are walked the same way by both faces, and after the parity assignment 138 edges are still
            // unsatisfied — the surface has odd cycles (fins, fillets, a twisted rim) and no winding satisfies it. The
            // majority rule then turned 198 faces per blade, jagged holes at every tip. Every real hull, deck and boat
            // sheet measured reaches exactly 0 unsatisfied edges; anything above is a guess, and a guess is not made.
            // "0 unsatisfied" was the first rule; the SS Romanic's 41,799-face hull reached 1 unsatisfied of 92 same-way
            // edges (one stray edge in 59,000) and the whole hull was refused. Tolerance: the pass must resolve at least
            // 95 % of the same-way edges — a blade (138 left of 32) is still refused, one stray edge is not.
            if (!orientable) { islandsNotOrientable++; notOrientable[ii] = true; islandConflict[ii] += " — not orientable, kept as authored"; }
            else if (ones > 0 && ones < isl.Count)
            {
                int minor = ones * 2 <= isl.Count ? 1 : 0;
                // SEEN FROM ABOVE BEFORE RECOLOURED (2026-09-24, the Wespe's deck plating round the hatch, after the
                // user's regrouping): the hull sheet, 10,713 faces, 2-colourable, walked to a 255-face minority that
                // the size rule turned whole - the deck strip round the hatch among them, 146 ray cells of deck
                // see-through from above. Measured from far outside the model, 22 of the 255 are exposed on their
                // authored front (the deck strip) and 6 on their back; the 89 majority faces across the seam are all
                // exposed on their authored front. Both sides are right as authored; what joins them is a game rip's
                // join (a deck welded to the bottom edge of the wall above it, a deck top abutting a ceiling in one
                // plane), which no winding satisfies and which the size rule "satisfied" by turning the smaller side.
                // No local reading of the seam tells a join from an error - a deck top meeting a ceiling in one plane
                // looks exactly like a reversed patch (a seam classifier was tried and measured wrong) - so the
                // evidence is the one the census uses: the view from above, against the WHOLE model. Every level face
                // of both classes is asked whether anything sits above it; the recolouring that leaves the most
                // exposed faces showing their front wins, and on a tie the size rule stands. A reversed patch in a
                // plate shows its back to the sky and is turned as before; a deck welded to the walls above it shows
                // its front and stays. Only the MINORITY is asked, and only ever to be kept: turning the majority on the
                // same evidence was tried and measured wrong on the frigate's boats - an open boat shows its backs to
                // the sky as authored, and the majority rule's assumption that the larger side is right stands.
                int minFront = 0, minBack = 0;
                foreach (int f in isl)
                {
                    if (parityOf[f] != minor) continue;
                    Vec3 nf = FaceNormal(f); double nl = FLen(nf); if (nl < 1e-12 || Math.Abs(nf.Y) < 0.5 * nl) continue;
                    if (!ExposedFromAbove(f)) continue;
                    if (nf.Y > 0) minFront++; else minBack++;
                }
                if (minBack >= minFront)
                {
                    foreach (int f in isl) if (parityOf[f] == minor) flip[f] = true;
                    islandsMadeConsistent++;
                }
                else
                {
                    fromAboveKept++; joinMinor[ii] = minor;
                    islandConflict[ii] += string.Format(System.Globalization.CultureInfo.InvariantCulture, " — seen from above the minority shows its front ({0} up-facing exposed, {1} down-facing): a join, not an error, left as authored", minFront, minBack);
                }
            }
        }

        Mark("sheets");
        // 5) direction. The signed volume about the ORIGIN was wrong for anything with a boundary: it is the volume of
        // a cone from the origin over the surface, so it reads where the surface sits, not which way it faces (a
        // correct deck under y=0 came back reversed whole — review of 4e748c1). Judged about the island's own
        // centroid instead, and trusted only where the per-face cones AGREE (|sum| / sum|v| > 0.5): a closed shell,
        // a thin slab with holes, a convex plating region all read ±1; a flat sheet reads 0/0 (the centroid lies in
        // its plane) and falls to the inside-out score against the hull's belly axis. Measured on the Teutonic the
        // same day: "closed = no boundary edge at all" was tried first and lost the 3,907-face plating island (21 %
        // boundary, a thin solid the radial score cannot see, authored inward by a 51 % majority) — its side plating
        // fell from 96 % outward to 78 %; the agreement rule keeps that call and still leaves the deck alone.
        // The belly axis is the MODEL's, never the group's (2026-09-16, the Teutonic's deck strips): a group made only
        // of deck strips has its own bounding box as its world, its belly line runs through the strips themselves,
        // and a deck facing down scores ~0 — undecidable, kept as authored. Sampled over every mesh node in the file
        // through its world matrix (masts and funnels do not move a percentile the way they move a bounding box);
        // the group's own vertices are the fallback for a file with nothing else in it.
        ModelBelly(nodes, meshes, reader, pos, out int lengthAxis, out int widthAxis, out double centreW, out double bellyY, out double floorY);
        int openJudged = 0, openReversed = 0, closedReversed = 0;
        var islandRule = new string[sheets.Count];   // per island, for the "largest islands" line: what was measured and what decided
        // DOUBLE-SKIN twin grid, built ONCE over every face of the group (the two skins of a thin solid are usually separate
        // islands, so a twin must be searched across islands): each face registered in every cell its bounding box
        // touches, the cell the larger of the reach and the median triangle size (a coarse mesh registers in a handful of
        // cells, a fine one in one), the 27-cell search around a face's centroid still covering the reach.
        // THE REACH IS 0.5 % OF THE LENGTH (was 1 %; 2026-09-19, the Romanic's bridge deck). Measured on the ship, every
        // genuine double skin has its twin within a third of the old reach — the hull skins at 0.03-0.34 of it, the
        // deckhouse skins at 0.05-0.13, the test fixture at 0.20 — while a 39-face roof region that is correct as
        // authored found 74 "twins in front" at 0.72 of it: the deckhouse's undersides a metre and more above, neighbours,
        // not a skin. The twin rule then turned the roof over (its own inside-out score read +0.55, plainly outward).
        // Half the reach keeps every skin and loses every neighbour; a deck and the ceiling below it sit at 1.4 %+.
        double twinReach = longest * 0.005, twinCell;
        var twinCells = new Dictionary<PositionKey, List<int>>();
        var twinCentres = new Vec3[faceCount];
        // per face, once: its unit normal (null when degenerate) and the radius of its corners about the centroid — a
        // candidate whose centroid lies farther than reach + its radius cannot hold a point within reach, and is skipped
        // before any cross product or closest-point test (the 27-cell search around a dense superstructure returned
        // thousands of candidates per face; group Q's direction pass took 44 s)
        var twinUnit = new Vec3?[faceCount]; var twinRadius = new double[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            Vec3 n = FaceNormal(f); double l = FLen(n); if (l >= 1e-12) twinUnit[f] = FScale(n, 1.0 / l);
            Vec3 a = P(f, 0), b = P(f, 1), cc = P(f, 2), cen = FScale(FAdd(FAdd(a, b), cc), 1.0 / 3.0);
            twinRadius[f] = Math.Max(FLen(FSub(a, cen)), Math.Max(FLen(FSub(b, cen)), FLen(FSub(cc, cen))));
        }
        var twinSeen = new int[faceCount];   // stamp: twinSeen[g] == f + 1 when g was already tested for face f
        {
            var sizes = new List<double>(faceCount);
            for (int f = 0; f < faceCount; f++)
            {
                Vec3 a = P(f, 0), b = P(f, 1), cc = P(f, 2);
                twinCentres[f] = FScale(FAdd(FAdd(a, b), cc), 1.0 / 3.0);
                sizes.Add(Math.Max(Math.Max(a.X, Math.Max(b.X, cc.X)) - Math.Min(a.X, Math.Min(b.X, cc.X)), Math.Max(Math.Max(a.Y, Math.Max(b.Y, cc.Y)) - Math.Min(a.Y, Math.Min(b.Y, cc.Y)), Math.Max(a.Z, Math.Max(b.Z, cc.Z)) - Math.Min(a.Z, Math.Min(b.Z, cc.Z)))));
            }
            sizes.Sort(); twinCell = Math.Max(twinReach, sizes.Count > 0 ? sizes[sizes.Count / 2] : twinReach);
            for (int f = 0; f < faceCount; f++)
            {
                Vec3 a = P(f, 0), b = P(f, 1), cc = P(f, 2);
                PositionKey lo = CellOf(new Vec3 { X = Math.Min(a.X, Math.Min(b.X, cc.X)), Y = Math.Min(a.Y, Math.Min(b.Y, cc.Y)), Z = Math.Min(a.Z, Math.Min(b.Z, cc.Z)) }, twinCell);
                PositionKey hi = CellOf(new Vec3 { X = Math.Max(a.X, Math.Max(b.X, cc.X)), Y = Math.Max(a.Y, Math.Max(b.Y, cc.Y)), Z = Math.Max(a.Z, Math.Max(b.Z, cc.Z)) }, twinCell);
                long x1 = Math.Min(hi.X, lo.X + 40), y1 = Math.Min(hi.Y, lo.Y + 40), z1 = Math.Min(hi.Z, lo.Z + 40);   // a triangle 40x the median size is capped (a stray sliver)
                for (long x = lo.X; x <= x1; x++) for (long y = lo.Y; y <= y1; y++) for (long z = lo.Z; z <= z1; z++)
                { var k = new PositionKey { X = x, Y = y, Z = z }; if (!twinCells.TryGetValue(k, out List<int> l)) twinCells.Add(k, l = new List<int>()); l.Add(f); }
            }
        }
        Mark("belly+twin grid");
        // the twin statistics of every island, measured BEFORE any direction flip (an island turned earlier in the loop
        // would present same-way normals to its twin island — the inner skin saw an already-turned outer skin)
        var twinStats = new int[sheets.Count][];
        var twinDist = new double[sheets.Count]; var twinStraight = new double[sheets.Count];   // for the sheet's report line: mean twin distance as a fraction of the reach, mean |along| / distance (1 = straight in front or behind) — a skin reads ≤ 0.35 and ~0.9, a neighbour 0.7+
        // ENCLOSURE evidence (review of 7307fe2: six inward cubes around a seventh across air gaps gave the centre a twin
        // behind every face): the twins behind a cavity shell all belong to ONE other island whose bounding box contains
        // the shell — six neighbours are six twin islands, none containing the centre. Recorded here per island.
        var islandOf = sheetOf;   // the twins' owner is a SHEET (the enclosing skin is one sheet even where it meets its decks)
        var islandLo = new double[sheets.Count][]; var islandHi = new double[sheets.Count][];
        for (int ii = 0; ii < sheets.Count; ii++)
        {
            islandLo[ii] = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity }; islandHi[ii] = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (int f in sheets[ii]) for (int c = 0; c < 3; c++) UpdateBounds(islandLo[ii], islandHi[ii], P(f, c));
        }
        var enclosingIsland = new int[sheets.Count];   // the island holding the twins behind, when one island holds ≥ 90 % of them and its box contains this one; else -1
        for (int ii = 0; ii < sheets.Count; ii++)
        {
            int partnered = 0, twinInFront = 0, twinBehind = 0;
            double distSum = 0, straightSum = 0;
            var behindBy = new Dictionary<int, int>();   // twin island -> twins-behind count
            foreach (int f in sheets[ii])
            {
                if (twinUnit[f] == null) continue; Vec3 nf = twinUnit[f].Value;
                Vec3 c = twinCentres[f]; PositionKey k = CellOf(c, twinCell); double best = double.PositiveInfinity; double proj = 0; int bestG = -1;
                int stamp = f + 1;
                for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                {
                    if (!twinCells.TryGetValue(new PositionKey { X = k.X + dx, Y = k.Y + dy, Z = k.Z + dz }, out List<int> l)) continue;
                    foreach (int g in l)
                    {
                        if (g == f || twinSeen[g] == stamp) continue;
                        twinSeen[g] = stamp;
                        if (twinUnit[g] == null) continue;
                        Vec3 gc = twinCentres[g]; double reachG = twinReach + twinRadius[g];
                        double ddx = gc.X - c.X, ddy = gc.Y - c.Y, ddz = gc.Z - c.Z;
                        if (ddx * ddx + ddy * ddy + ddz * ddz > reachG * reachG) continue;   // too far for any point of g to lie within reach
                        if (FDot(nf, twinUnit[g].Value) > -0.95) continue;
                        Vec3 q = ClosestPointOnTriangle(c, P(g, 0), P(g, 1), P(g, 2)); Vec3 d = FSub(q, c); double dist = FLen(d);
                        if (dist < 1e-9 || dist > twinReach) continue;
                        double along = FDot(d, nf);
                        if (Math.Abs(along) < 0.5 * dist) continue;   // the twin must lie along the normal, not beside the face
                        if (dist < best) { best = dist; proj = along; bestG = g; }
                    }
                }
                if (!double.IsPositiveInfinity(best))
                {
                    partnered++; distSum += best / twinReach; straightSum += Math.Abs(proj) / best;
                    if (proj > 0) twinInFront++;
                    else { twinBehind++; int ti = islandOf[bestG]; behindBy[ti] = behindBy.TryGetValue(ti, out int bc) ? bc + 1 : 1; }
                }
            }
            twinStats[ii] = new[] { partnered, twinInFront, twinBehind };
            twinDist[ii] = partnered > 0 ? distSum / partnered : 0; twinStraight[ii] = partnered > 0 ? straightSum / partnered : 0;
            enclosingIsland[ii] = -1;
            if (twinBehind > 0)
            {
                int dom = -1, domCount = 0; foreach (KeyValuePair<int, int> kv in behindBy) if (kv.Value > domCount) { dom = kv.Key; domCount = kv.Value; }
                if (dom >= 0 && dom != ii && domCount * 10 >= twinBehind * 9)
                {
                    double pad = twinReach;   // the box of the enclosing island must contain this island's box (a skin's thickness of slack)
                    bool contains = true;
                    for (int c = 0; c < 3; c++) if (islandLo[dom][c] > islandLo[ii][c] + pad || islandHi[dom][c] < islandHi[ii][c] - pad) contains = false;
                    if (contains) enclosingIsland[ii] = dom;
                }
            }
        }
        for (int ii = 0; ii < sheets.Count; ii++)
        {
            List<int> isl = sheets[ii];
            // a sheet's boundary is every face-edge without a partner: the rim, and the junctions where this sheet is the
            // branching face (a deck ends at the hull side; a hull side continues through it) — such a sheet is open
            var keys = new HashSet<long>(PairKeyComparer.Instance); int boundary = 0;
            foreach (int f in isl) for (int e = 0; e < 3; e++) { long key = fEdgeKeys[f * 3 + e]; if (key < 0) continue; keys.Add(key); if (partner[f * 3 + e] < 0) boundary++; }
            bool closed = boundary == 0 && keys.Count > 0;
            // signed volume about the island's own centroid, and how much the per-face cones agree on its sign
            var centroid = new Vec3 { X = 0, Y = 0, Z = 0 };
            foreach (int f in isl) centroid = FAdd(centroid, FAdd(FAdd(P(f, 0), P(f, 1)), P(f, 2)));
            centroid = FScale(centroid, 1.0 / (3.0 * isl.Count));
            double volume = 0, absVolume = 0, area = 0;
            foreach (int f in isl)
            {
                double v = FDot(FSub(P(f, 0), centroid), FCross(FSub(P(f, 1), centroid), FSub(P(f, 2), centroid))) / 6.0;
                if (flip[f]) v = -v;
                volume += v; absVolume += Math.Abs(v); area += 0.5 * FLen(FaceNormal(f));
            }
            double agreement = absVolume > 1e-12 * longest * longest * longest ? volume / absVolume : 0.0;
            // how much volume for its surface: a thin solid of thickness t over area A holds ~tA, so this is ~t/sqrt(A) —
            // a lap strip 0.01 over a 2x1 plate reads 0.001, a real plating region far more. Below the gate the
            // "volume" is a sheet's tiny curl and no judgement of facing.
            double thickness = area > 0 ? volume / Math.Pow(area, 1.5) : 0.0;
            // the inside-out score against the belly axis (what a flat sheet is judged by)
            double sum = 0; int n = 0;
            foreach (int f in isl)
            {
                Vec3 c = FScale(FAdd(FAdd(P(f, 0), P(f, 1)), P(f, 2)), 1.0 / 3.0);
                var radial = new Vec3 { X = 0, Y = c.Y - bellyY, Z = 0 };
                if (widthAxis == 0) radial.X = c.X - centreW; else radial.Z = c.Z - centreW;
                double rl = FLen(radial), nl = FLen(FaceNormal(f));
                if (rl < 1e-9 || nl < 1e-12) continue;
                sum += FDot(FaceNormal(f), radial) / (rl * nl); n++;
            }
            double score = n > 0 ? sum / n : 0.0;
            // HOW LEVEL, AND HOW HIGH (2026-09-20, the Confederate frigate's gun deck): area-weighted, so a big flat
            // surface decides and a few skirting faces do not.
            double upSum = 0, upArea = 0, ySum = 0, floorLowY = double.PositiveInfinity;
            foreach (int f in isl)
            {
                Vec3 nf = FaceNormal(f); double nlen = FLen(nf); if (nlen < 1e-12) continue;
                double a = 0.5 * nlen, up = nf.Y / nlen, cy = FScale(FAdd(FAdd(P(f, 0), P(f, 1)), P(f, 2)), 1.0 / 3.0).Y;
                upSum += up * a; upArea += a; ySum += cy * a;
                if (up > 0.5) floorLowY = Math.Min(floorLowY, Math.Min(P(f, 0).Y, Math.Min(P(f, 1).Y, P(f, 2).Y)));   // the FLOOR's lowest point
            }
            double upness = upArea > 0 ? upSum / upArea : 0.0;      // +1 = every face points up, -1 = down
            double islandY = upArea > 0 ? ySum / upArea : 0.0;
            // THE DECK'S HEIGHT IS WHERE ITS FLOOR REACHES DOWN TO (PR #80 review, two P2s). The whole sheet's mean
            // counts the rim in, and a rim lifts it: an inverted shallow hull averaged to just above floorY and read as
            // a deck. The floor faces' MEAN is no better: a V-bottom's panels slope up from the keel, their mean clears
            // the floor, and they are the hull bottom all the same. What a hull's floor does that a deck's never does
            // is reach the model's floor - so the evidence is the lowest VERTEX of the up-facing faces.
            double floorFaceY = double.IsPositiveInfinity(floorLowY) ? islandY : floorLowY;
            // DOUBLE-SKINNED SOLIDS (2026-09-17, the SS Romanic): the hull is two skins a few centimetres apart with
            // opposite normals, wound inside-out as a whole. Both rules above read ~0 on it (the cones of the two skins
            // cancel: agreement -0.01, thickness -0.0005; the radial score cancels the same way). What does not cancel:
            // for every face, on which side its twin lies. Material lies BEHIND an outward face, so a partnered face
            // whose twin sits in FRONT of it (along +normal, within `twinReach`) faces into the solid. The island is
            // double-skinned when at least half its faces have a twin; the majority of front-vs-behind decides.
            int partnered = twinStats[ii][0], twinInFront = twinStats[ii][1], twinBehind = twinStats[ii][2];
            bool doubleSkin = partnered * 2 >= isl.Count && partnered > 0 && Math.Abs(twinInFront - twinBehind) * 10 > partnered * 4;   // decided when 70/30 or clearer
            // ENCLOSED: a twin BEHIND at least 90 % of the faces — the containment a cavity shell has (the outer skin
            // surrounds it on every side) and a neighbouring solid never has (twins only on the sides that touch: four
            // inward cubes 5 mm apart gave the central one twins behind 6 of 12 faces, and the veto kept it inside out —
            // review of a043f8e). Only an enclosed island may veto a volume reversal.
            bool enclosed = twinBehind * 10 >= isl.Count * 9 && enclosingIsland[ii] >= 0;   // …AND one enclosing island holds those twins and boxes this one in
            bool reverse = false, asAuthored = false, reversalVetoed = false; string vetoNote = "";
            if (notOrientable[ii]) { }   // kept as authored means KEPT: no whole-island reversal either — a volume or score read off a surface with no consistent winding is noise (review of 0097bd5: the reversed Möbius band came back "6 of 6 rewound")
            // the twin rule is a TIE-BREAKER (review of 9cacd9f): from inside a gap, air between two solids looks exactly
            // like a skin of material, so four cubes 5 mm apart read "twin in front" on every facing side. A closed
            // island's volume and an open island's confident volume decide first; only where both are inconclusive
            // does the twin evidence speak, and after it the inside-out score.
            // …and the one thing twin evidence MAY do against a confident volume is VETO a reversal: a closed cavity
            // shell (the inner skin of a hollow solid) has a negative volume yet every twin BEHIND it — it already faces
            // away from the material, and turning it would point it into the wall (review of e595844).
            else if (closed) { reverse = volume < 0 && !enclosed; if (reverse) closedReversed++; }
            else
            {
                openJudged++;
                bool volumeConfident = Math.Abs(agreement) > 0.5 && Math.Abs(thickness) > VolumeThicknessGate;
                // THE LAST RESORT, and where a deck used to be lost (2026-09-20). The radial score asks "does this
                // point away from the hull's belly line", which is right for plating and wrong for a deck BELOW that
                // line: the frigate's belly sits at 2.59 (a quarter up a model whose sails own most of its height and
                // area) while its three gun-deck islands lie at 0.61, 2.06 and 2.53, every face pointing up — they
                // scored -0.67 and were reversed whole, 88 % of the deck see-through from above. A LEVEL sheet above
                // the hull's floor and ALREADY FACING UP is therefore not asked that question: it is a deck and needs no
                // correction. The rule is deliberately one-sided. Turning every level sheet up was tried first and was
                // too strong: the frigate's two 11.4 x 5.2 zero-thickness plates over the boat deck are authored facing
                // DOWN, to be seen from below, and facing them up draped a blank sheet over the deck (user: "the flat
                // blanket"). Keeping every level sheet as authored was tried next and was too weak: it left the sheets
                // that genuinely face a hull's interior uncorrected, which four of this file's own tests pin down.
                // Down-facing level sheets therefore keep going through the evidence that was there before.
                // Below the floor (bottom plating) and for anything not level, the radial score stands. Twin evidence
                // still decides first, so a double-skinned deck keeps its underside.
                //
                // A DECK WITH A RIM (2026-09-22, SMS Wespe's gun platform: 242 of the 251 see-through cells on the whole
                // fused deck came from this one 281-face sheet). Bulwark and coaming faces make a deck sheet a shallow
                // TRAY, and a tray seen from above is the same surface as an open box wound inside out: its floor faces
                // its own centroid, so the signed volume is confidently NEGATIVE ("volume agreement -1.00 thickness
                // -0.0533, inside-out score +0.51: reversed whole") and the rim drags the area-weighted levelness under
                // the 0.8 gate (0.57 here). What tells the two apart is HEIGHT: a hull wound inside out lies at the
                // model's floor, below the belly line by construction (the belly is the 25th height percentile); a
                // rimmed deck above the belly line is a deck. So above the belly, "facing up on balance" is enough, and
                // a deck by either reading is exempt from a NEGATIVE volume verdict - only that one. A confident
                // POSITIVE volume still settles a deck as kept, ahead of the twin rule: on the Romanic, seven decks
                // reading "agreement +1.00, score +0.63" were turned over the moment the volume stopped protecting
                // them, by a twin-in-front majority. And the exemption never reaches past the twin rule, which a
                // double-skinned slab's underside still needs (A_double_skinned_solid_is_judged_by_which_side...).
                // `floorY` is a percentile, not the minimum, so "above the floor" carries a margin of a twentieth
                // of the floor-to-belly rise: a hull's own bottom plating never clears it, the frigate's gun deck at
                // 0.61 over a floor of -5 clears it by a mile.
                double floorMargin = 0.05 * Math.Max(0.0, bellyY - floorY);
                bool deckFacingUp = (upness > 0.8 && floorFaceY > floorY + floorMargin) || (upness > 0.5 && floorFaceY > bellyY);
                bool doubleWall = partnered * 2 >= isl.Count && partnered > 0;   // twinned on most faces; doubleSkin is this AND a decided vote
                // the undecided double wall is asked FIRST, ahead of the volume: its volume is as much noise as its
                // score (the deck plating round the Wespe's hatch, 96 faces, 48 twins in front / 48 behind, read a
                // confident -0.70 and was reversed whole - see below)
                if (doubleWall && !doubleSkin) asAuthored = true;
                else if (volumeConfident && (volume >= 0 || !deckFacingUp)) reverse = volume < 0 && !enclosed;
                else if (doubleSkin) reverse = twinInFront > twinBehind;
                else if (deckFacingUp) reverse = false;         // a deck, already facing up: nothing to correct
                else
                {
                    reverse = score < -0.25;
                    // AN UNDERSIDE (2026-09-26, HMS Svea's fighting top, user: "the crow's nest is transparent from
                    // below after the fuse"). The radial score asks "does this face point away from the hull's belly
                    // line", and every face high above that line that points DOWN answers no: the flared underside of
                    // the mast top - 742 faces, all facing down, split into 660 sheets of one or two faces at the exact
                    // weld - scored -1.00 apiece and was reversed whole (351 faces turned up; measured on the fuse, and
                    // a wider weld turned more, not fewer). But what makes it an underside is plain in the geometry:
                    // the platform's floor lies a few units ABOVE it and the deck lies far BENEATH it, and a face is
                    // seen from the side with the room. So before a down-facing sheet above the belly is turned on the
                    // score alone, its down-facing faces are asked what is above and below them, along the column:
                    // a ceiling close above and at least three times that much air beneath is an underside, and it
                    // keeps its authored facing. AIR BENEATH MEANS A FLOOR BENEATH, ABOVE THE BELLY LINE (the Teutonic's
                    // promenade deck, the same day's drill): a deck authored facing down under a higher deck also has a
                    // ceiling close above and room below it - but that room is the hull's INSIDE. The ray down from it
                    // lands on the bottom plating, and that plating is read as authored: its back on a hull wound the
                    // right way, its FRONT on one wound inside out or on the inner skin of a double hull (the Teutonic's
                    // group U: 185 faces kept on a "floor 9.9 beneath" that was the hull's bottom). What a hull's bottom
                    // always is, is LOW: at the model's floor, below the belly line by construction. The deck under the
                    // Svea's flare lies far above it. So the thing beneath must show its front AND lie above the belly
                    // line, or the sheet is a deck and the score turns it, as master did (684 faces on the Teutonic's
                    // X, see-through from above with the distance test alone). Deliberately one-sided and narrow: a
                    // deck authored facing down has the sky above it (no ceiling), an inside-out hull bottom faces up,
                    // side plating is not level - all still go through the score exactly as before. Faces are sampled
                    // evenly, at most 64 of them.
                    if (reverse && upness < -0.3 && islandY > bellyY)
                    {
                        int keepVotes = 0, turnVotes = 0, insideBelow = 0, stride = Math.Max(1, isl.Count / 64);
                        double aboveSum = 0, belowSum = 0;
                        for (int k = 0; k < isl.Count; k += stride)
                        {
                            int f = isl[k]; Vec3 nf = FaceNormal(f); double nl = FLen(nf);
                            if (nl < 1e-12 || nf.Y > -0.3 * nl) continue;
                            double above = FreeRun(f, true, out bool _), below = FreeRun(f, false, out bool floorBelow);
                            if (double.IsPositiveInfinity(above)) { turnVotes++; continue; }   // nothing over it: a deck wound down, not an underside
                            double landingY = (P(f, 0).Y + P(f, 1).Y + P(f, 2).Y) / 3.0 - below;
                            if (!double.IsPositiveInfinity(below) && (!floorBelow || landingY <= bellyY)) { insideBelow++; turnVotes++; continue; }   // the hull's inside beneath (its bottom, or a back face): a deck, not an underside
                            if (below >= 3.0 * above) { keepVotes++; aboveSum += above; belowSum += Math.Min(below, modelTop - modelBottom); } else turnVotes++;
                        }
                        if (keepVotes > 0 && keepVotes >= 2 * turnVotes)
                        {
                            reverse = false; reversalVetoed = true; undersidesKept++;
                            vetoNote = string.Format(System.Globalization.CultureInfo.InvariantCulture, " (an underside: {0} of {1} down-facing faces have a ceiling {2:0.#} above and a floor {3:0.#} beneath)", keepVotes, keepVotes + turnVotes, aboveSum / keepVotes, belowSum / keepVotes);
                        }
                        else if (insideBelow > 0 && insideBelow * 2 >= keepVotes + turnVotes)
                            vetoNote = string.Format(System.Globalization.CultureInfo.InvariantCulture, " (not an underside: {0} of {1} down-facing faces have the hull's inside beneath them)", insideBelow, keepVotes + turnVotes);
                    }
                }
                // SEEN FROM ABOVE BEFORE REVERSED (2026-09-24, the Wespe's stern companionway, group G): a 47-face
                // open well - walls facing into the well, floor and steps facing up, as the source has it - whose
                // cones about its own centroid read a confident -0.85, and the volume rule reversed it whole: walls
                // into the deck, floor down, 3,733 ray cells see-through, the stairs visible through the wall (on
                // master too; the user's regrouping did not touch it). A well is judged by what it shows the sky, as
                // a deck is: as it stands it shows up-facing faces to the sky and no backs, and turning it would turn
                // every one of them down. So a reversal of an open sheet is vetoed when the sheet already shows more
                // fronts than backs from above (level faces, against the whole model). An up-facing face counts only
                // when something lies BELOW it as well: an upturned boat's bottom faces up and sees the sky exactly as a
                // well's floor does, and what tells them apart is the hull beneath the well (the tray-at-the-floor and
                // V-bottom fixtures: exposed from below, still turned). The deck exemption above stays - it needs no
                // exposure query - and a sheet showing its backs (a deck authored down) is still turned.
                // A JOIN MINORITY IS NOT TURNED WITH ITS SHEET (2026-09-25, the Teutonic's groups T and D): a minority
                // the parity walk kept because it shows its front to the sky is right by that evidence, and a reversal
                // the direction pass then applies to the whole sheet is judged on the majority - on master the two
                // wrongs cancelled (minority recoloured, then reversed back up); with the join kept, reversing it whole
                // turned the kept faces down (18 cells). So the reversal skips the kept minority, and the veto below
                // counts only the faces the reversal would turn.
                if (reverse)
                {
                    int upExposed = 0, downExposed = 0;
                    foreach (int f in isl)
                    {
                        if (joinMinor[ii] >= 0 && parityOf[f] == joinMinor[ii]) continue;
                        Vec3 nf = FaceNormal(f); double nl = FLen(nf); if (nl < 1e-12 || Math.Abs(nf.Y) < 0.5 * nl) continue;
                        if (!Exposed(f, true)) continue;
                        if (nf.Y > 0) { if (!Exposed(f, false)) upExposed++; } else downExposed++;
                    }
                    // DECISIVE, three to one: the frigate's 6,559-face gun-deck sheet read 358 up-facing against 332
                    // backs and the veto kept it where master's reversal had been the better call (measured, +5 cells);
                    // the well read 15 against 0. A near-even vote is no evidence and the verdict above stands.
                    if (upExposed >= 3 * downExposed && upExposed > 0) { reverse = false; reversalVetoed = true; reversalsVetoed++; vetoNote = string.Format(System.Globalization.CultureInfo.InvariantCulture, " (seen from above it shows {0} up-facing faces and {1} backs: a reversal would turn them down)", upExposed, downExposed); }
                }
                if (reverse) openReversed++;
            }
            // A DOUBLE WALL WITH AN UNDECIDED VOTE IS LEFT AS AUTHORED, parity flips included (2026-09-24, the Wespe's
            // companionway). The stairwell is doubled the way the gun was (every face has an opposite partner a
            // quarter of the reach away) but the copies are not coincident, so the coincidence rule does not see
            // them; and as on the gun's ring, the copies swap roles at the fold: the landing's up-facing faces belong
            // to the copy that faces the well on the walls. Two things then went wrong at once. The parity walk read
            // each copy as inconsistent at the fold and turned 38 of its 77 faces; and the twin vote (33 in front /
            // 44 behind, short of 70/30) fell through to the radial score, -0.27 against the -0.25 line, which turned
            // the sheet whole - walls in, landing back up: the stairwell showed the hull's insides from above. Keeping
            // the sheet but not the parity flips left the landing facing down instead (49 ray cells, measured).
            // The radial score judges a SINGLE skin, and parity a surface with ONE right side; a sheet the twin
            // evidence already describes as double-walled is neither. Its author saw the skin that is visible, and
            // the source, which is the reference, renders it. So every face keeps its authored winding.
            // And the VOLUME is no better a judge of it (the same day, the user's regrouping): the deck plating round
            // the hatch came out as a 96-face sheet, 48 twins in front / 48 behind, whose signed volume read a
            // confident -0.70 - half of it is one copy and half the other, wound opposite ways, so the cones agree
            // on a sign that means nothing - and the volume rule, which comes first for a single skin, reversed it
            // whole: 146 ray cells of deck see-through from above. An undecided double wall is asked before the
            // volume. A DECIDED vote (doubleSkin) still goes to the volume first, as the tie-breaker note says: from
            // inside a gap, air between two solids looks like a skin, and those all vote one way.
            if (asAuthored)
            {
                foreach (int f in isl) flip[f] = false;
                asAuthoredSheets++;
            }
            if (reverse) foreach (int f in isl) { if (joinMinor[ii] >= 0 && parityOf[f] == joinMinor[ii]) continue; flip[f] = !flip[f]; }
            islandRule[ii] = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}, volume agreement {1:+0.00;-0.00} thickness {2:+0.0000;-0.0000}, inside-out score {3:+0.00;-0.00}, level {6:+0.00;-0.00} at y {7:0.##}{5}: {4}",
                closed ? "closed" : "open", agreement, thickness, score, notOrientable[ii] ? "not judged" : reverse ? "reversed whole" + vetoNote : asAuthored ? "double wall, kept as authored" : reversalVetoed ? "kept" + vetoNote : "kept",
                partnered > 0 ? string.Format(System.Globalization.CultureInfo.InvariantCulture, ", double skin {0:0}% twinned ({1} twin in front / {2} behind, at {3:0.00} of reach, {4:0.00} straight)", 100.0 * partnered / isl.Count, twinInFront, twinBehind, twinDist[ii], twinStraight[ii]) : "", upness, islandY);
        }
        Mark("direction");
        // TWINS KEEP THEIR AUTHORED WINDING (2026-09-24, the Wespe's gun, second cut). Kept apart, the two copies were
        // still judged as two sheets - and each copy is only MOSTLY one way: at the reinforce ring, where the surface
        // folds back into the barrel, the outward face belongs to the other copy than everywhere else, and the parity
        // walk sees that fold as consistent. Reversing copy B whole (volume -0.92) turned its ring faces in beside copy
        // A's, and the ring's underside vanished from below. But an authored opposite pair IS already two-sided: one
        // face each way at every position, whichever copy holds which. No reversal can improve it and any reversal of
        // one copy alone breaks it. So a twin face keeps exactly the winding it was authored with - consistency pass
        // and direction verdict both undone - and the part renders as its source did, which is the reference.
        // Done HERE, before any count is taken (review of PR #82, P3): FacesRewound and the per-part line count what
        // is actually turned in the output.
        int twinsRestored = 0;
        for (int f = 0; f < faceCount; f++) if (twinFace[f] && flip[f]) { flip[f] = false; twinsRestored++; }
        if (fromAboveKept + reversalsVetoed > 0) plan.FromAboveLine = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "from above: {0} sheet(s) whose parity minority shows its authored front to the sky keep their authored winding - a join is not a winding error; {1} reversal(s) of an open sheet vetoed because the sheet already shows more fronts than backs to the sky", fromAboveKept, reversalsVetoed);
        if (undersidesKept > 0) plan.UndersideLine = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "undersides: {0} down-facing sheet(s) above the belly line keep their authored facing - a ceiling close above and air beneath is what an underside looks like, not an inside-out skin", undersidesKept);
        if (asAuthoredSheets > 0) plan.AsAuthoredLine = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "double walls: {0} sheet(s) twinned on most faces with an undecided vote keep their authored winding - the radial score and the parity walk judge single skins", asAuthoredSheets);
        if (twinsRestored > 0) plan.TwinsRestoredLine = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "twins: {0} face(s) of doubled pairs had been turned by the winding passes and keep their authored winding instead - a pair wound both ways is already two-sided", twinsRestored);
        foreach (bool b in flip) if (b) result.FacesRewound++;
        // per PART: how many of its faces were turned — the reader's question after "why does my port side still render
        // inside out" is which part the pass left alone (2026-09-18, the Romanic's group D)
        string rewoundByPart;
        {
            var facesOf = new int[picked.Count]; var turnedOf = new int[picked.Count];
            for (int f = 0; f < faceCount; f++) { int pp = partOf[tris[f * 3]]; facesOf[pp]++; if (flip[f]) turnedOf[pp]++; }
            rewoundByPart = "rewound by part: " + string.Join("; ", Enumerable.Range(0, picked.Count).Select(pp => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} {1}/{2}", partNames[pp], turnedOf[pp], facesOf[pp])));
        }
        // the largest islands, so a reader can see WHAT was judged (faces, boundary share, how many faces the majority
        // rule turned) — the Teutonic's hole was a 1,613-face minority inside a 4,013-face island. Details[0] stays
        // the one-line summary (the Workshop's status reads it); this is Details[1].
        string largestIslands;
        {
            var rows = new List<string>();
            foreach (int ii in Enumerable.Range(0, sheets.Count).OrderByDescending(i => sheets[i].Count))
            {
                List<int> isl = sheets[ii];
                var keys = new HashSet<long>(PairKeyComparer.Instance); int boundary = 0, turned = 0;
                foreach (int f in isl) { if (flip[f]) turned++; for (int e = 0; e < 3; e++) { long key = fEdgeKeys[f * 3 + e]; if (key < 0) continue; keys.Add(key); if (partner[f * 3 + e] < 0) boundary++; } }
                // WHICH PARTS make up the sheet (2026-09-19, the Romanic's bridge deck: a 39-face roof region welded into a
                // deckhouse sheet took the deckhouse's verdict — the reader needs to see the mixture): the heaviest four
                var byPart = new Dictionary<int, int>();
                foreach (int f in isl) { int pp = partOf[tris[f * 3]]; byPart[pp] = byPart.TryGetValue(pp, out int pc) ? pc + 1 : 1; }
                string parts = string.Join(", ", byPart.OrderByDescending(kv => kv.Value).Take(4).Select(kv => partNames[kv.Key] + " ×" + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                             + (byPart.Count > 4 ? ", +" + (byPart.Count - 4).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more" : "");
                string line = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} faces ({1:0}% boundary, {2}, {3} rewound; {4}) — parts: {5}", isl.Count, keys.Count > 0 ? 100.0 * boundary / keys.Count : 100.0, islandRule[ii], turned, islandConflict[ii], parts);
                result.IslandLines.Add(line);   // all of them, for the report (review of 0097bd5: the report promised every island and carried six)
                if (rows.Count < 6) rows.Add(line);
            }
            largestIslands = "largest islands: " + string.Join("; ", rows);
        }

        Mark("report");
        // 6) vertex normals follow the final winding
        var incident = new List<int>[pos.Count];
        for (int f = 0; f < faceCount; f++) for (int c = 0; c < 3; c++) { int v = tris[f * 3 + c]; (incident[v] ?? (incident[v] = new List<int>())).Add(f); }
        // The authored normal is kept (its smoothing is the artist's) with its SIGN chosen to agree with the final winding:
        // the sum of the incident faces' geometric normals. "Negate it when every incident face was flipped" was the
        // first rule, and it assumed the authored normal agreed with the authored winding — a mirrored instance (the
        // Romanic's whole port side) ships normals pointing UP over a winding that renders DOWN, so turning the winding
        // right and negating the normal left the deck lit from below (2026-09-18: visible from above at last, and dark).
        var finalNormal = new Vec3[pos.Count];
        for (int v = 0; v < pos.Count; v++)
        {
            List<int> faces = incident[v];
            var acc = new Vec3 { X = 0, Y = 0, Z = 0 };
            if (faces != null) foreach (int f in faces) acc = FAdd(acc, FaceNormal(f));
            Vec3? authored = nrm[v];
            if (authored.HasValue && FLen(acc) > 1e-18) finalNormal[v] = FDot(authored.Value, acc) < 0 ? FScale(authored.Value, -1.0) : authored.Value;
            else if (authored.HasValue) { int flipped = 0; if (faces != null) foreach (int f in faces) if (flip[f]) flipped++; finalNormal[v] = faces != null && flipped == faces.Count ? FScale(authored.Value, -1.0) : authored.Value; }   // degenerate fan: the old rule
            else finalNormal[v] = FLen(acc) > 1e-18 ? FUnit(acc) : new Vec3 { X = 0, Y = 1, Z = 0 };
        }

        Mark("normals");
        // 7) output vertices: one per welded class + material + matching UV + matching normal; one primitive per material
        var primitiveOf = new Dictionary<int, FusePrimitive>();
        var vertexMap = new int[pos.Count];
        var reps = new Dictionary<long, List<int>>(PairKeyComparer.Instance);   // (class, material) -> representative original vertices already emitted
        for (int v = 0; v < pos.Count; v++)
        {
            if (incident[v] == null) { vertexMap[v] = -1; continue; }   // unreferenced source vertex: dropped
            int material = mat[v];
            if (!primitiveOf.TryGetValue(material, out FusePrimitive prim)) primitiveOf.Add(material, prim = new FusePrimitive { Material = material });
            long groupKey = ((long)classes[v] << 32) | (uint)(material + 1);
            if (!reps.TryGetValue(groupKey, out List<int> group)) reps.Add(groupKey, group = new List<int>());
            int found = -1;
            foreach (int r in group)
            {
                bool uvSame = (uv[v] == null && uv[r] == null) || (uv[v] != null && uv[r] != null && Math.Abs(uv[v][0] - uv[r][0]) < 1e-4 && Math.Abs(uv[v][1] - uv[r][1]) < 1e-4);
                if (uvSame && FDot(finalNormal[v], finalNormal[r]) > 0.999 && ExtrasSame(extra[v], extra[r])) { found = r; break; }
            }
            if (found >= 0) { vertexMap[v] = vertexMap[found]; continue; }
            group.Add(v);
            vertexMap[v] = prim.Positions.Count / 3;
            Vec3 p = pos[v], nn = finalNormal[v];
            prim.Positions.Add((float)p.X); prim.Positions.Add((float)p.Y); prim.Positions.Add((float)p.Z);
            prim.Normals.Add((float)nn.X); prim.Normals.Add((float)nn.Y); prim.Normals.Add((float)nn.Z);
            // a vertex from a part without UVs is padded (0,0) — the primitive used to drop TEXCOORD_0 for EVERY part of the
            // material when one contributor lacked it (review of 82088d4); only a primitive no vertex of which had UVs ships without
            if (uv[v] != null) { prim.Uvs.Add((float)uv[v][0]); prim.Uvs.Add((float)uv[v][1]); prim.UvSeen = true; } else { prim.Uvs.Add(0f); prim.Uvs.Add(0f); }
            for (int k = 0; k < extraNames.Count; k++)
            {
                if (!prim.Extras.TryGetValue(extraNames[k], out List<float> list)) prim.Extras.Add(extraNames[k], list = new List<float>());
                double[] value = extra[v][k];
                if (value != null) prim.ExtrasSeen.Add(extraNames[k]);
                // a vertex from a part without the attribute: white for a colour, zero for anything else
                for (int c = 0; c < extraComps[k]; c++) list.Add(value != null ? (float)value[c] : extraNames[k].StartsWith("COLOR_", StringComparison.Ordinal) ? 1f : 0f);
            }
        }
        for (int f = 0; f < faceCount; f++)
        {
            FusePrimitive prim = primitiveOf[mat[tris[f * 3]]];
            int i0 = vertexMap[tris[f * 3]], i1 = vertexMap[tris[f * 3 + 1]], i2 = vertexMap[tris[f * 3 + 2]];
            if (flip[f]) { int t = i1; i1 = i2; i2 = t; }
            prim.Indices.Add((uint)i0); prim.Indices.Add((uint)i1); prim.Indices.Add((uint)i2);
            result.OutputTriangles++;
        }
        if (result.OutputTriangles != result.SourceTriangles)
            throw new InvalidDataException("Triangle preservation check failed: source " + result.SourceTriangles + ", output " + result.OutputTriangles + ".");
        foreach (FusePrimitive prim in primitiveOf.Values) result.VerticesAfter += prim.Positions.Count / 3;

        Mark("vertices");
        plan.Primitives = primitiveOf; plan.ExtraNames = extraNames; plan.ExtraComps = extraComps; plan.Picked = picked; plan.FusedMeshes = fusedMeshes;
        plan.BaseName = string.IsNullOrEmpty(fusedName) ? partNames[0] + "_Fused" : fusedName;
        plan.Weld = weld; plan.WeldFraction = weldFraction; plan.Longest = longest;
        plan.MadeConsistent = islandsMadeConsistent; plan.OpenJudged = openJudged; plan.OpenReversed = openReversed; plan.ClosedReversed = closedReversed;
        plan.FaceCount = faceCount; plan.CollapsedFaces = collapsedFaces; plan.NotOrientable = islandsNotOrientable;
        plan.LargestIslands = largestIslands; plan.StitchedLine = stitchedLine; plan.RewoundByPart = rewoundByPart;
        plan.Timing = string.Join("; ", timing);
        plan.FrameLine = string.Format(System.Globalization.CultureInfo.InvariantCulture, "frame: length along {0}, side centre {1:0.##}, belly height {2:0.##} (the model's, fused or not)", lengthAxis == 0 ? "X" : "Z", centreW, bellyY);
        return plan;
    }

    // 8) the fused mesh on a new root node; the source nodes keep transforms and children, lose their mesh — appended to
    // the document the caller holds (one group, or every group of a Generate in turn), the BIN growing in `bin`
    static void ApplyPlan(JObject root, JArray nodes, JArray meshes, List<byte> bin, FusePlan plan)
    {
        if (plan.Empty) return;
        var writeClock = System.Diagnostics.Stopwatch.StartNew();
        Result result = plan.Result; List<int> picked = plan.Picked; List<string> extraNames = plan.ExtraNames; List<int> extraComps = plan.ExtraComps;
        string baseName = plan.BaseName;
        var meshJson = new JObject { ["name"] = UniqueName(baseName, meshes.OfType<JObject>().Select(m => (string)m["name"])) };
        var primitivesJson = new JArray();
        foreach (FusePrimitive prim in plan.Primitives.Values.OrderBy(p => p.Material < 0 ? int.MaxValue : p.Material))
        {
            var attrs = new JObject
            {
                ["POSITION"] = AppendFloats(root, bin, prim.Positions, 3, "VEC3", true),
                ["NORMAL"] = AppendFloats(root, bin, prim.Normals, 3, "VEC3", false),
            };
            if (prim.UvSeen) attrs["TEXCOORD_0"] = AppendFloats(root, bin, prim.Uvs, 2, "VEC2", false);
            for (int k = 0; k < extraNames.Count; k++)
                if (prim.ExtrasSeen.Contains(extraNames[k]))
                    attrs[extraNames[k]] = AppendFloats(root, bin, prim.Extras[extraNames[k]], extraComps[k], extraComps[k] == 1 ? "SCALAR" : "VEC" + extraComps[k], false);
            var pj = new JObject { ["attributes"] = attrs, ["indices"] = AppendIndices(root, bin, prim.Indices, 5125), ["mode"] = 4 };
            if (prim.Material >= 0) pj["material"] = prim.Material;
            primitivesJson.Add(pj);
        }
        meshJson["primitives"] = primitivesJson;
        int newMeshIndex = meshes.Count; meshes.Add(meshJson);
        var nodeNames = new HashSet<string>(nodes.OfType<JObject>().Select(n => (string)n["name"]).Where(n => !string.IsNullOrEmpty(n)));
        string newNodeName = UniqueName(baseName, nodeNames);
        int newNodeIndex = nodes.Count;
        nodes.Add(new JObject { ["name"] = newNodeName, ["mesh"] = newMeshIndex });
        int sceneIndex = root["scene"] == null ? 0 : root.Value<int>("scene");
        if (root["scenes"] is JArray scenes && sceneIndex >= 0 && sceneIndex < scenes.Count && scenes[sceneIndex] is JObject scene)
        {
            JArray sceneNodes = scene["nodes"] as JArray;
            if (sceneNodes == null) scene["nodes"] = sceneNodes = new JArray();
            sceneNodes.Add(newNodeIndex);
        }
        foreach (int ni in picked) { var n = (JObject)nodes[ni]; n.Remove("mesh"); }
        foreach (int other in Enumerable.Range(0, nodes.Count))
            if (!picked.Contains(other) && other != newNodeIndex && (nodes[other] as JObject)?["mesh"] != null && plan.FusedMeshes.Contains(nodes[other].Value<int>("mesh")))
                result.Warnings.Add("Node " + other + " shares a fused part's mesh and keeps the ORIGINAL geometry (instanced part).");
        result.NodesSplit = picked.Count; result.MeshesSplit = plan.FusedMeshes.Count; result.ChildPartsCreated = 1;
        result.FusedNodeIndex = newNodeIndex; result.FusedNodeName = newNodeName;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        result.Details.Add(string.Format(inv,
            "Fused {0} part(s) -> '{1}': {2} -> {3} verts (seams welded within {4:0.####} = {5:0.##}‰ of {6:0.#}); islands {7} -> {8}; {9} made consistent{16}; {10} open sheet(s) judged, {11} reversed; {12} closed shell(s) reversed whole; {13} of {14} face(s) rewound; {15} face(s) smaller than the weld kept collapsed",
            picked.Count, newNodeName, result.VerticesBefore, result.VerticesAfter, plan.Weld, plan.WeldFraction * 1000.0, plan.Longest, result.IslandsBefore, result.IslandsAfter,
            plan.MadeConsistent, plan.OpenJudged, plan.OpenReversed, plan.ClosedReversed, result.FacesRewound, plan.FaceCount, plan.CollapsedFaces,
            plan.NotOrientable > 0 ? string.Format(inv, ", {0} not orientable by traversal (kept as authored)", plan.NotOrientable) : ""));
        result.Details.Add(plan.LargestIslands);
        if (plan.StitchedLine != null) result.Details.Add(plan.StitchedLine);   // Details[2] when present: the lap test pins it there
        result.Details.Add(plan.RewoundByPart);
        result.Details.Add("timing: " + plan.Timing + "; write " + writeClock.ElapsedMilliseconds.ToString(inv) + " ms");
        result.Details.Add(plan.FrameLine);
        if (plan.MirroredLine != null) result.Details.Add(plan.MirroredLine);   // last: tests pin the earlier lines by index
        if (plan.TwinsLine != null) result.Details.Add(plan.TwinsLine);                   // after everything the tests pin by index
        if (plan.TwinsRestoredLine != null) result.Details.Add(plan.TwinsRestoredLine);
        if (plan.AsAuthoredLine != null) result.Details.Add(plan.AsAuthoredLine);
        if (plan.FromAboveLine != null) result.Details.Add(plan.FromAboveLine);
        if (plan.UndersideLine != null) result.Details.Add(plan.UndersideLine);
    }

    // REMOVE (2026-09-18, user: "an easy way to mark a unit for removal with the Del key"): the marked nodes lose their
    // mesh — exactly what a fused source loses — and keep their transforms and children; nothing is renumbered, so every
    // other mark (a ⊕ letter, a Split check) still finds its node in the output. The mesh data itself stays in the file
    // as an orphan the bake never reads; no compaction is attempted. A node without a mesh is reported, not an error.
    // UNIQUE PART NAMES (2026-09-25, user: "in the model cutter all parts need to get a unique name so that after
    // cutting they don't start to conflict"): a game rip names every part after its material - the Wespe has ten
    // nodes called "Material2" - and everything downstream that names a part (the Lab's rows and roles, the sidecars'
    // name+index lines, a recipe) then fits several. Every mesh node leaves the Cutter with a name no other node has:
    // the first of a name keeps it, the next become Material2_2, Material2_3 ... in node order, never colliding with a
    // name already in the file. Names only; nothing else in the file moves, and a file already unique is untouched.
    public static Result UniqueNodeNames(byte[] source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        Document document = Parse(source);
        JArray nodes = document.Root["nodes"] as JArray ?? new JArray();
        var taken = new HashSet<string>(nodes.OfType<JObject>().Select(n => (string)n["name"]).Where(n => !string.IsNullOrEmpty(n)));
        var seen = new HashSet<string>(); var renamed = new List<string>(); var result = new Result();
        foreach (JObject node in nodes.OfType<JObject>())
        {
            string name = (string)node["name"];
            if (string.IsNullOrEmpty(name) || node["mesh"] == null) continue;   // only parts; a nameless or meshless node stays as it is
            if (seen.Add(name)) continue;                                        // the first of a name keeps it
            string fresh = UniqueName(name, taken); taken.Add(fresh); seen.Add(fresh);
            node["name"] = fresh; renamed.Add(name + " -> " + fresh);
        }
        result.NodesSplit = renamed.Count;
        if (renamed.Count == 0) return result;   // Changed == false, Bytes null: the caller keeps what it had
        result.Details.Add("Renamed " + renamed.Count + " part(s) to unique names: " + string.Join(", ", renamed.Take(12)) + (renamed.Count > 12 ? ", ..." : ""));
        result.Bytes = Write(document);
        return result;
    }

    public static Result RemoveMeshes(byte[] source, ISet<int> nodeIndices)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (nodeIndices == null || nodeIndices.Count == 0) throw new ArgumentException("Nothing to remove — no node indices.", nameof(nodeIndices));
        Document document = Parse(source);
        JObject root = document.Root;
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        var result = new Result(); var names = new List<string>();
        foreach (int ni in nodeIndices.OrderBy(i => i))
        {
            if (ni < 0 || ni >= nodes.Count) { result.Warnings.Add("Node " + ni + " does not exist — nothing removed there."); continue; }
            var n = nodes[ni] as JObject;
            if (n == null || n["mesh"] == null) { result.Warnings.Add("Node " + ni + " carries no mesh — nothing to remove."); continue; }
            n.Remove("mesh"); n.Remove("skin"); n.Remove("weights");   // a skin or morph weights without a mesh are invalid glTF: they go with it
            names.Add((string)n["name"] ?? ("node " + ni)); result.NodesSplit++;
        }
        if (result.NodesSplit == 0) return result;   // Changed == false, Bytes null: the caller keeps what it had
        result.Details.Add("Removed " + result.NodesSplit + " part(s): " + string.Join(", ", names));
        result.Bytes = Write(document);
        ValidateOutput(result.Bytes);
        return result;
    }

    public static Result FuseFile(string inputPath, string outputPath, IList<int> nodeIndices, double weldFraction)
    {
        GuardPaths(inputPath, outputPath);
        Result result = FuseNodes(File.ReadAllBytes(inputPath), nodeIndices, weldFraction);
        if (result.Changed) File.WriteAllBytes(outputPath, result.Bytes);
        return result;
    }

    // Möller-Trumbore: the distance along `d` from `o` to the triangle, or -1 when the ray misses it
    static double RayTriangle(Vec3 o, Vec3 d, Vec3 v0, Vec3 v1, Vec3 v2)
    {
        Vec3 e1 = FSub(v1, v0), e2 = FSub(v2, v0), p = FCross(d, e2); double det = FDot(e1, p);
        if (Math.Abs(det) < 1e-12) return -1; double inv = 1.0 / det;
        Vec3 tv = FSub(o, v0); double u = FDot(tv, p) * inv; if (u < -1e-9 || u > 1 + 1e-9) return -1;
        Vec3 q = FCross(tv, e1); double v = FDot(d, q) * inv; if (v < -1e-9 || u + v > 1 + 1e-9) return -1;
        return FDot(e2, q) * inv;
    }

    static (int, int, int) SortedTriple(int a, int b, int c)
    {
        if (a > b) { int t = a; a = b; b = t; }
        if (b > c) { int t = b; b = c; c = t; }
        if (a > b) { int t = a; a = b; b = t; }
        return (a, b, c);
    }

    // Is the face's class cycle an even permutation of its sorted triple? Two faces on the same three classes with
    // different answers are wound the opposite way round.
    static bool ClassOrientation(int[] classes, IList<int> tris, int f)
    {
        int a = classes[tris[f * 3]], b = classes[tris[f * 3 + 1]], c = classes[tris[f * 3 + 2]];
        int inversions = (a > b ? 1 : 0) + (a > c ? 1 : 0) + (b > c ? 1 : 0);
        return (inversions & 1) == 0;
    }

    sealed class FusePrimitive
    {
        public int Material;
        public readonly List<float> Positions = new List<float>();
        public readonly List<float> Normals = new List<float>();
        public readonly List<float> Uvs = new List<float>();
        public readonly List<uint> Indices = new List<uint>();
        public bool UvSeen;      // at least one vertex of this material carried TEXCOORD_0: the primitive ships with UVs (the rest padded 0,0)
        public readonly Dictionary<string, List<float>> Extras = new Dictionary<string, List<float>>();   // COLOR_n, TEXCOORD_1.., custom
        public readonly HashSet<string> ExtrasSeen = new HashSet<string>();                                // …that at least one vertex actually carried
    }

    static bool ExtrasSame(double[][] a, double[][] b)
    {
        for (int k = 0; k < a.Length; k++)
        {
            if (a[k] == null && b[k] == null) continue;
            if (a[k] == null || b[k] == null) return false;
            for (int c = 0; c < a[k].Length; c++) if (Math.Abs(a[k][c] - b[k][c]) >= 1e-4) return false;
        }
        return true;
    }

    static PositionKey CellOf(Vec3 p, double cell) => new PositionKey { X = (long)Math.Floor(p.X / cell), Y = (long)Math.Floor(p.Y / cell), Z = (long)Math.Floor(p.Z / cell) };
    static double FDist2(Vec3 a, Vec3 b) { double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z; return dx * dx + dy * dy + dz * dz; }
    static Vec3 FSub(Vec3 a, Vec3 b) => new Vec3 { X = a.X - b.X, Y = a.Y - b.Y, Z = a.Z - b.Z };
    static Vec3 FAdd(Vec3 a, Vec3 b) => new Vec3 { X = a.X + b.X, Y = a.Y + b.Y, Z = a.Z + b.Z };
    static Vec3 FScale(Vec3 a, double s) => new Vec3 { X = a.X * s, Y = a.Y * s, Z = a.Z * s };
    static Vec3 FCross(Vec3 a, Vec3 b) => new Vec3 { X = a.Y * b.Z - a.Z * b.Y, Y = a.Z * b.X - a.X * b.Z, Z = a.X * b.Y - a.Y * b.X };
    static double FDot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static double FLen(Vec3 a) => Math.Sqrt(FDot(a, a));
    static Vec3 FUnit(Vec3 a) { double l = FLen(a); return l > 1e-18 ? FScale(a, 1.0 / l) : a; }
    // a direction through the node's world matrix: the 3x3 part, re-normalized (rigid + uniform scale, which is what game rips carry)
    // distance from a point to a triangle (Ericson, Real-Time Collision Detection 5.1.5): the closest point on the
    // triangle by Voronoi region, then the plain distance — used to tell a lap strip (its face centres ON the plate)
    // from a cover or rail that merely shares the plate's edge vertices.
    static double PointTriangleDistance(Vec3 p, Vec3 a, Vec3 b, Vec3 c) => FLen(FSub(p, ClosestPointOnTriangle(p, a, b, c)));
    static Vec3 ClosestPointOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
    {
        Vec3 ab = FSub(b, a), ac = FSub(c, a), ap = FSub(p, a);
        double d1 = FDot(ab, ap), d2 = FDot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vec3 bp = FSub(p, b); double d3 = FDot(ab, bp), d4 = FDot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { double v = d1 / (d1 - d3); return FAdd(a, FScale(ab, v)); }
        Vec3 cp = FSub(p, c); double d5 = FDot(ab, cp), d6 = FDot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { double w = d2 / (d2 - d6); return FAdd(a, FScale(ac, w)); }
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { double w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return FAdd(b, FScale(FSub(c, b), w)); }
        double denom = 1.0 / (va + vb + vc); double vv = vb * denom, ww = vc * denom;
        return FAdd(a, FAdd(FScale(ab, vv), FScale(ac, ww)));
    }
    // The hull's frame for the inside-out score: length = the longer horizontal extent (glTF is Y-up), the side centre
    // = the middle of the width extent, the belly = the 25th percentile of height over sampled vertices of EVERY mesh
    // node (≤ ~50k samples), i.e. inside the hull mass, below the decks. Falls back to the given vertices.
    static void ModelBelly(JArray nodes, JArray meshes, Accessors reader, List<Vec3> fallback, out int lengthAxis, out int widthAxis, out double centreW, out double bellyY, out double floorY)
    {
        // AREA-WEIGHTED (review of 9cacd9f): the frame is measured on FACE samples weighted by their area, not on
        // vertices — a coarse hull of a few hundred triangles outweighs a dense cabin of thousands, and a chain of 144
        // tiny links weighs almost nothing however many vertices it carries. Vertex-density filtering had discarded a
        // coarse hull under a dense superstructure (belly 10.6 on a deck at 10).
        var pts = new List<Vec3>(); var wts = new List<double>();
        try
        {
            long total = 0; var prims = new List<KeyValuePair<int, JObject>>();   // (node, primitive)
            for (int ni = 0; ni < nodes.Count; ni++)
            {
                var node = nodes[ni] as JObject; if (node?["mesh"] == null) continue;
                int mi = node.Value<int>("mesh"); if (mi < 0 || mi >= meshes.Count) continue;
                var pl = (meshes[mi] as JObject)?["primitives"] as JArray; if (pl == null) continue;
                foreach (JObject prim in TrianglesOnly(pl))
                {
                    var attrs = prim["attributes"] as JObject; if (attrs?["POSITION"] == null) continue;
                    prims.Add(new KeyValuePair<int, JObject>(ni, prim));
                    total += (prim["indices"] == null ? reader.Count(attrs.Value<int>("POSITION")) : reader.Count(prim.Value<int>("indices"))) / 3;
                }
            }
            int stride = (int)Math.Max(1, total / 50000);
            foreach (KeyValuePair<int, JObject> np in prims)
            {
                double[] world = NodeWorldMatrix(nodes, np.Key); JObject prim = np.Value;
                int posAcc = ((JObject)prim["attributes"]).Value<int>("POSITION"); int vertCount = reader.Count(posAcc);
                int idxAcc = prim["indices"] == null ? -1 : prim.Value<int>("indices");
                int faceN = (idxAcc < 0 ? vertCount : reader.Count(idxAcc)) / 3;
                for (int f = 0; f < faceN; f += stride)
                {
                    uint i0 = idxAcc < 0 ? (uint)(f * 3) : reader.Index(idxAcc, (uint)(f * 3)), i1 = idxAcc < 0 ? (uint)(f * 3 + 1) : reader.Index(idxAcc, (uint)(f * 3 + 1)), i2 = idxAcc < 0 ? (uint)(f * 3 + 2) : reader.Index(idxAcc, (uint)(f * 3 + 2));
                    if (i0 >= vertCount || i1 >= vertCount || i2 >= vertCount) continue;
                    Vec3 a = XForm(world, reader.Position(posAcc, i0)), b = XForm(world, reader.Position(posAcc, i1)), c = XForm(world, reader.Position(posAcc, i2));
                    double area = 0.5 * FLen(FCross(FSub(b, a), FSub(c, a))) * stride;   // the stride stands in for the faces skipped
                    if (area <= 0) continue;
                    // the three corners, a third of the area each: a triangle's area lies across its whole extent, and one
                    // sample at its centre put a coarse 100-tall box's belly at 42 instead of 25 (review of e595844)
                    pts.Add(a); wts.Add(area / 3); pts.Add(b); wts.Add(area / 3); pts.Add(c); wts.Add(area / 3);
                }
            }
        }
        catch (Exception) { pts.Clear(); wts.Clear(); }
        if (pts.Count == 0) { pts = fallback; wts = new List<double>(fallback.Count); foreach (Vec3 _ in fallback) wts.Add(1.0); }
        // STRAY GEOMETRY (2026-09-17, the SS Romanic's anchor chain 3 km off): a sample farther from the area-weighted
        // median centre than 4x the area-weighted median distance is not the model, and is dropped
        if (pts.Count > 8)
        {
            var mid = new Vec3 { X = WeightedPercentile(pts, wts, 0, 0.5), Y = WeightedPercentile(pts, wts, 1, 0.5), Z = WeightedPercentile(pts, wts, 2, 0.5) };
            var dist = new List<double>(pts.Count); foreach (Vec3 q in pts) dist.Add(FLen(FSub(q, mid)));
            double cut = 4.0 * WeightedPercentileOf(dist, wts, 0.5);
            if (cut > 0)
            {
                var kp = new List<Vec3>(pts.Count); var kw = new List<double>(pts.Count);
                for (int i = 0; i < pts.Count; i++) if (dist[i] <= cut) { kp.Add(pts[i]); kw.Add(wts[i]); }
                if (kp.Count >= 8) { pts = kp; wts = kw; }
            }
        }
        if (pts.Count == 0) { lengthAxis = 0; widthAxis = 2; centreW = 0; bellyY = 0; floorY = 0; return; }
        // robust extents: the 1st..99th area-weighted percentiles per axis (a mast top is little area)
        double x1 = WeightedPercentile(pts, wts, 0, 0.01), x99 = WeightedPercentile(pts, wts, 0, 0.99);
        double z1 = WeightedPercentile(pts, wts, 2, 0.01), z99 = WeightedPercentile(pts, wts, 2, 0.99);
        double y1 = WeightedPercentile(pts, wts, 1, 0.01), y99 = WeightedPercentile(pts, wts, 1, 0.99);
        lengthAxis = (x99 - x1) >= (z99 - z1) ? 0 : 2; widthAxis = lengthAxis == 0 ? 2 : 0;
        centreW = widthAxis == 0 ? 0.5 * (x1 + x99) : 0.5 * (z1 + z99);
        // a quarter of the way up the model's HEIGHT RANGE (not a vertex percentile: a liner spends its vertices in rigging
        // and deckhouses, which put that percentile at deck level — the Teutonic's lower strips, 2026-09-16)
        bellyY = y1 + 0.25 * (y99 - y1);
        // and the hull's FLOOR, just above the lowest geometry: below it a level surface may honestly face down
        // (the bottom plating), above it a level surface is a deck and faces up (see the level-sheet rule).
        floorY = y1 + 0.05 * (y99 - y1);
    }

    static double WeightedPercentile(List<Vec3> pts, List<double> wts, int axis, double q)
    {
        var vals = new List<double>(pts.Count); foreach (Vec3 p in pts) vals.Add(axis == 0 ? p.X : axis == 1 ? p.Y : p.Z);
        return WeightedPercentileOf(vals, wts, q);
    }
    static double WeightedPercentileOf(List<double> vals, List<double> wts, double q)
    {
        int n = vals.Count; if (n == 0) return 0.0;
        var order = new int[n]; for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => vals[a].CompareTo(vals[b]));
        double total = 0; foreach (double w in wts) total += w;
        double acc = 0, target = q * total;
        foreach (int i in order) { acc += wts[i]; if (acc >= target) return vals[i]; }
        return vals[order[n - 1]];
    }

    // determinant of the upper-left 3x3 of a column-major glTF matrix: negative = a mirroring transform
    static double Det3(double[] m) =>
        m[0] * (m[5] * m[10] - m[9] * m[6]) - m[4] * (m[1] * m[10] - m[9] * m[2]) + m[8] * (m[1] * m[6] - m[5] * m[2]);
    // The matrix that carries NORMALS: the inverse transpose of the upper-left 3x3, in the same column-major slots so
    // XFormDir reads it like any other. Under a rotation it is the matrix itself; under a non-uniform scale it is not
    // (a normal is a covector — scale the surface 2x along X and its normal shrinks along X). A mirror keeps its sign.
    // Returns the plain matrix for a singular one (a flattened node): nothing better exists.
    static double[] NormalMatrix(double[] m)
    {
        double det = Det3(m);
        if (Math.Abs(det) < 1e-18) return m;
        double a = m[0], b = m[4], c = m[8], d = m[1], e = m[5], f = m[9], g = m[2], h = m[6], i = m[10];   // row-major view: [a b c; d e f; g h i]
        // inverse = adjugate / det; its transpose = cofactor matrix / det
        double[] r = Identity();
        r[0] = (e * i - f * h) / det; r[4] = (f * g - d * i) / det; r[8] = (d * h - e * g) / det;   // first ROW of the cofactor matrix -> slots of the first row
        r[1] = (c * h - b * i) / det; r[5] = (a * i - c * g) / det; r[9] = (b * g - a * h) / det;
        r[2] = (b * f - c * e) / det; r[6] = (c * d - a * f) / det; r[10] = (a * e - b * d) / det;
        return r;
    }
    static Vec3 XFormDir(double[] m, Vec3 v) => FUnit(new Vec3 {
        X = m[0] * v.X + m[4] * v.Y + m[8] * v.Z,
        Y = m[1] * v.X + m[5] * v.Y + m[9] * v.Z,
        Z = m[2] * v.X + m[6] * v.Y + m[10] * v.Z
    });

    static int AppendFloats(JObject root, List<byte> bin, List<float> data, int components, string type, bool withMinMax)
    {
        while ((bin.Count & 3) != 0) bin.Add(0);
        int byteOffset = bin.Count;
        var min = new double[components]; var max = new double[components];
        for (int c = 0; c < components; c++) { min[c] = double.PositiveInfinity; max[c] = double.NegativeInfinity; }
        for (int i = 0; i < data.Count; i++)
        {
            AddUInt32(bin, BitConverter.ToUInt32(BitConverter.GetBytes(data[i]), 0));
            int c = i % components;
            if (data[i] < min[c]) min[c] = data[i];
            if (data[i] > max[c]) max[c] = data[i];
        }
        var views = (JArray)root["bufferViews"];
        int viewIndex = views.Count;
        views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = byteOffset, ["byteLength"] = bin.Count - byteOffset, ["target"] = 34962 });
        var accessors = (JArray)root["accessors"];
        int accessorIndex = accessors.Count;
        var accessor = new JObject { ["bufferView"] = viewIndex, ["componentType"] = 5126, ["count"] = data.Count / components, ["type"] = type };
        if (withMinMax && data.Count > 0) { accessor["min"] = new JArray(min.Select(d => (float)d)); accessor["max"] = new JArray(max.Select(d => (float)d)); }
        accessors.Add(accessor);
        return accessorIndex;
    }

    // ---- plane-cut internals ----

    static Accessors BinReader(Document document, JObject root) => BinReader(document, root, out _);

    static Accessors BinReader(Document document, JObject root, out byte[] originalData)
    {
        JArray buffers = root["buffers"] as JArray ?? throw new InvalidDataException("GLB has no buffers array.");
        if (buffers.Count != 1 || buffers[0]?["uri"] != null)
            throw new InvalidDataException("Lossless splitting requires one embedded GLB buffer.");
        int declaredBinLength = buffers[0].Value<int>("byteLength");
        byte[] sourceBin = document.Chunks[document.BinIndex].Data;
        if (declaredBinLength < 0 || declaredBinLength > sourceBin.Length)
            throw new InvalidDataException("buffers[0].byteLength exceeds the BIN chunk.");
        originalData = Prefix(sourceBin, declaredBinLength);
        return new Accessors(root, originalData);
    }

    static JObject NodeWithMesh(JArray nodes, int nodeIndex, out int meshIndex)
    {
        if (nodeIndex < 0 || nodeIndex >= nodes.Count) throw new InvalidDataException("Node index is out of range.");
        var node = nodes[nodeIndex] as JObject ?? throw new InvalidDataException("Node is not an object.");
        if (node["mesh"] == null) throw new InvalidDataException("Node has no mesh.");
        if (node["extensions"]?["EXT_mesh_gpu_instancing"] != null)
            throw new InvalidDataException("GPU-instanced nodes cannot be cut.");
        meshIndex = node.Value<int>("mesh");
        return node;
    }

    // The primitive walk both plane-cut entry points share; also re-checks the splitter's safety gates.
    // The whole-file samplers (the belly frame, the fuse's occluders) read every mesh node and want the triangles of
    // a file that also carries lines or points: a rigging line is no reason to lose the model (2026-09-25, the Wespe's
    // source file: "Only TRIANGLES primitives" from a whole-file read had the belly sampler fall back to the group's
    // own vertices).
    static IEnumerable<JObject> TrianglesOnly(JArray primitives)
    {
        foreach (JToken token in primitives)
        {
            if (!(token is JObject primitive)) continue;
            if ((primitive.Value<int?>("mode") ?? 4) != 4) continue;
            if (primitive["extensions"]?["KHR_draco_mesh_compression"] != null) continue;
            if (!(primitive["attributes"] is JObject attributes) || attributes["POSITION"] == null) continue;
            yield return primitive;
        }
    }

    static IEnumerable<JObject> TrianglePrimitives(JArray primitives)
    {
        foreach (JToken token in primitives)
        {
            var primitive = token as JObject ?? throw new InvalidDataException("Primitive is not an object.");
            if ((primitive.Value<int?>("mode") ?? 4) != 4)
                throw new InvalidDataException("Only TRIANGLES primitives can be split safely.");
            if (primitive["extensions"]?["KHR_draco_mesh_compression"] != null)
                throw new InvalidDataException("Draco-compressed primitives are not supported.");
            var attributes = primitive["attributes"] as JObject ?? throw new InvalidDataException("Primitive has no attributes.");
            if (attributes["POSITION"] == null) throw new InvalidDataException("Primitive has no POSITION attribute.");
            yield return primitive;
        }
    }

    // glTF node-to-world: compose matrix-or-TRS up the parent chain. Column-major double[16], glTF storage order.
    static double[] NodeWorldMatrix(JArray nodes, int nodeIndex)
    {
        var parentOf = new Dictionary<int, int>();
        for (int i = 0; i < nodes.Count; i++)
            if ((nodes[i] as JObject)?["children"] is JArray kids)
                foreach (JToken kid in kids)
                {
                    int ci = kid.Value<int>();
                    if (!parentOf.ContainsKey(ci)) parentOf.Add(ci, i);
                }
        var chain = new List<int>();
        int walk = nodeIndex;
        var seen = new HashSet<int>();
        while (true)
        {
            if (!seen.Add(walk)) throw new InvalidDataException("Node parent chain contains a cycle.");
            chain.Add(walk);
            if (!parentOf.TryGetValue(walk, out walk)) break;
        }
        double[] world = Identity();
        for (int i = chain.Count - 1; i >= 0; i--)
            world = Mul(world, LocalMatrix((JObject)nodes[chain[i]]));
        return world;
    }

    static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    static double[] LocalMatrix(JObject node)
    {
        if (node?["matrix"] is JArray m16 && m16.Count == 16)
        {
            var m = new double[16];
            for (int i = 0; i < 16; i++) m[i] = m16[i].Value<double>();
            return m;
        }
        double[] t = ReadVec(node?["translation"] as JArray, 0, 0, 0);
        double[] r = ReadVec(node?["rotation"] as JArray, 0, 0, 0, 1);
        double[] s = ReadVec(node?["scale"] as JArray, 1, 1, 1);
        double x = r[0], y = r[1], z = r[2], w = r[3];
        var rot = new[,] {
            { 1 - 2 * (y * y + z * z), 2 * (x * y - z * w),     2 * (x * z + y * w) },
            { 2 * (x * y + z * w),     1 - 2 * (x * x + z * z), 2 * (y * z - x * w) },
            { 2 * (x * z - y * w),     2 * (y * z + x * w),     1 - 2 * (x * x + y * y) }
        };
        var result = Identity();
        for (int col = 0; col < 3; col++)
            for (int row = 0; row < 3; row++)
                result[col * 4 + row] = rot[row, col] * s[col];
        result[12] = t[0]; result[13] = t[1]; result[14] = t[2];
        return result;
    }

    static double[] ReadVec(JArray array, params double[] fallback)
    {
        if (array == null || array.Count != fallback.Length) return fallback;
        var v = new double[fallback.Length];
        for (int i = 0; i < v.Length; i++) v[i] = array[i].Value<double>();
        return v;
    }

    static double[] Mul(double[] a, double[] b)
    {
        var m = new double[16];
        for (int col = 0; col < 4; col++)
            for (int row = 0; row < 4; row++)
                for (int k = 0; k < 4; k++)
                    m[col * 4 + row] += a[k * 4 + row] * b[col * 4 + k];
        return m;
    }

    static Vec3 XForm(double[] m, Vec3 p) => new Vec3 {
        X = m[0] * p.X + m[4] * p.Y + m[8] * p.Z + m[12],
        Y = m[1] * p.X + m[5] * p.Y + m[9] * p.Z + m[13],
        Z = m[2] * p.X + m[6] * p.Y + m[10] * p.Z + m[14]
    };

    // The cut's shared coordinate precision: ExtractPart hands the preview FLOATS (a Unity Mesh holds nothing
    // finer), so the writer's judge must see the identical rounding or boundary faces flip sides between the
    // preview and the output GLB.
    static Vec3 Snap(Vec3 p) => new Vec3 { X = (float)p.X, Y = (float)p.Y, Z = (float)p.Z };

    static MeshPlan AnalyzeMesh(int meshIndex, JObject mesh, Accessors reader, bool keepSingle = false, double mergeFraction = 0)
    {
        JArray primitives = mesh["primitives"] as JArray;
        if (primitives == null || primitives.Count == 0) return null;
        var vertices = new List<Vertex>();
        var vertexIds = new Dictionary<VertexKey, int>();
        var triangles = new List<Triangle>();
        var dsu = new DisjointSet();
        int order = 0;

        for (int primitiveIndex = 0; primitiveIndex < primitives.Count; primitiveIndex++)
        {
            var primitive = primitives[primitiveIndex] as JObject ?? throw new InvalidDataException("Primitive is not an object.");
            if ((primitive.Value<int?>("mode") ?? 4) != 4)
                throw new InvalidDataException("Only TRIANGLES primitives can be split safely.");
            if (primitive["extensions"]?["KHR_draco_mesh_compression"] != null)
                throw new InvalidDataException("Draco-compressed primitives are not supported.");
            var attributes = primitive["attributes"] as JObject ?? throw new InvalidDataException("Primitive has no attributes.");
            if (attributes["POSITION"] == null) throw new InvalidDataException("Primitive has no POSITION attribute.");
            int positionAccessor = attributes.Value<int>("POSITION");
            int indexCount = primitive["indices"] == null ? reader.Count(positionAccessor) : reader.Count(primitive.Value<int>("indices"));
            if (indexCount % 3 != 0) throw new InvalidDataException("Triangle primitive index count is not divisible by three.");
            for (uint i = 0; i < indexCount; i += 3)
            {
                uint a = primitive["indices"] == null ? i : reader.Index(primitive.Value<int>("indices"), i);
                uint b = primitive["indices"] == null ? i + 1 : reader.Index(primitive.Value<int>("indices"), i + 1);
                uint c = primitive["indices"] == null ? i + 2 : reader.Index(primitive.Value<int>("indices"), i + 2);
                int va = VertexId(positionAccessor, a, reader, vertexIds, vertices, dsu);
                int vb = VertexId(positionAccessor, b, reader, vertexIds, vertices, dsu);
                int vc = VertexId(positionAccessor, c, reader, vertexIds, vertices, dsu);
                dsu.Union(va, vb); dsu.Union(vb, vc);
                triangles.Add(new Triangle { Primitive = primitiveIndex, A = a, B = b, C = c, VA = va, VB = vb, VC = vc, Order = order++ });
            }
        }
        if (triangles.Count == 0) return null;

        double[] min = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        double[] max = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (Vertex vertex in vertices) UpdateBounds(min, max, vertex.Position);
        double diagonal = Math.Sqrt(Math.Pow(max[0] - min[0], 2) + Math.Pow(max[1] - min[1], 2) + Math.Pow(max[2] - min[2], 2));
        double epsilon = Math.Max(diagonal * 1e-7, 1e-9);
        var positions = new Dictionary<PositionKey, int>();
        for (int i = 0; i < vertices.Count; i++)
        {
            Vec3 p = vertices[i].Position;
            var key = new PositionKey {
                X = checked((long)Math.Round((p.X - min[0]) / epsilon)),
                Y = checked((long)Math.Round((p.Y - min[1]) / epsilon)),
                Z = checked((long)Math.Round((p.Z - min[2]) / epsilon))
            };
            int previous;
            if (positions.TryGetValue(key, out previous)) dsu.Union(i, previous); else positions.Add(key, i);
        }

        var byRoot = new Dictionary<int, Component>();
        foreach (Triangle triangle in triangles)
        {
            int root = dsu.Find(triangle.VA);
            Component component;
            if (!byRoot.TryGetValue(root, out component)) { component = new Component(); byRoot.Add(root, component); }
            List<uint> indices;
            if (!component.Indices.TryGetValue(triangle.Primitive, out indices)) { indices = new List<uint>(); component.Indices.Add(triangle.Primitive, indices); }
            indices.Add(triangle.A); indices.Add(triangle.B); indices.Add(triangle.C);
            component.FirstTriangle = Math.Min(component.FirstTriangle, triangle.Order);
            component.TriangleCount++;
            UpdateBounds(component.Min, component.Max, vertices[triangle.VA].Position);
            UpdateBounds(component.Min, component.Max, vertices[triangle.VB].Position);
            UpdateBounds(component.Min, component.Max, vertices[triangle.VC].Position);
            component.Points.Add(vertices[triangle.VA].Position);
            component.Points.Add(vertices[triangle.VB].Position);
            component.Points.Add(vertices[triangle.VC].Position);
        }
        if (byRoot.Count <= 1 && !keepSingle) return null;   // keepSingle: Analyze wants the row (islands=1) anyway
        var components = byRoot.Values.OrderBy(c => c.Min[0]).ThenBy(c => c.Min[1]).ThenBy(c => c.Min[2]).ThenBy(c => c.FirstTriangle).ToList();
        // DISTANCE MERGE (2026-09-06, the 602-island rope): topology alone shreds segmented geometry into
        // hundreds of 3-vert parts that are millimetres apart — useless to review, worse to rig. Islands whose
        // bounding boxes lie within mergeFraction of THIS mesh's own diagonal fuse back into one part, so only
        // geometry that is genuinely far away (the floating junk this tool exists for) separates. 0 = pure
        // topology, byte-compatible with the original behavior.
        if (mergeFraction > 0 && components.Count > 1)
        {
            double eps = diagonal * mergeFraction;
            var cd = new DisjointSet();
            var elong = new bool[components.Count];
            var dir = new Vec3[components.Count];
            var ctr = new Vec3[components.Count];
            for (int i = 0; i < components.Count; i++)
            {
                cd.Add();
                components[i].PrincipalAxes(out var pc1, out double aspect);   // rotation-invariant (review finding 7): a 45° dash is as elongated as an axis-aligned one
                elong[i] = aspect > 3;
                if (elong[i]) { dir[i] = pc1; ctr[i] = components[i].Centroid(); }
            }
            // TWO ELONGATED islands only chain when they are the same LINE: parallel directions and a colinear
            // step (the perpendicular offset from either axis stays small). A hull trim dash and a sail luff
            // dash that pass close are near but off-axis — separate lines, never one indirect blob. Blobby
            // islands merge on distance alone.
            bool DirectionOk(int i, int j)
            {
                if (!elong[i] || !elong[j]) return true;
                double dd = Math.Abs(dir[i].X * dir[j].X + dir[i].Y * dir[j].Y + dir[i].Z * dir[j].Z);
                if (dd < 0.85) return false;
                double sx = ctr[j].X - ctr[i].X, sy = ctr[j].Y - ctr[i].Y, sz = ctr[j].Z - ctr[i].Z;
                double s2 = sx * sx + sy * sy + sz * sz;
                if (s2 <= 1e-18) return true;
                double alongI = sx * dir[i].X + sy * dir[i].Y + sz * dir[i].Z;
                double alongJ = sx * dir[j].X + sy * dir[j].Y + sz * dir[j].Z;
                double perp2 = Math.Min(s2 - alongI * alongI, s2 - alongJ * alongJ);
                double lateralTol = 0.35 * eps;
                return perp2 <= lateralTol * lateralTol;
            }
            // VERTEX distance via a spatial hash (review find 2026-09-06: a bounding-box gap reads ZERO for an
            // island floating anywhere INSIDE a big part's box — the default merge then hid exactly the junk
            // this tool exists to expose). Two components merge when vertices of theirs lie within eps — the
            // comparison is EXACT (review round 3: a per-cell representative produced false negatives — two
            // vertices 0.01 apart missed because neither was its cell's first). Fast because the cost drivers
            // are handled structurally instead of by dropping data:
            //   - exact duplicate positions (glTF seam splits) dedup on insert;
            //   - cells store vertices GROUPED BY COMPONENT, so the same-root skip is one check per component
            //     per cell — the dense same-component clusters that caused the earlier quadratic blowup cost
            //     O(1) per visit now — and a cross-component scan stops at its first hit (union).
            long CellKey(long cx, long cy, long cz) => (cx & 0x1FFFFF) | ((cy & 0x1FFFFF) << 21) | ((cz & 0x1FFFFF) << 42);
            var cellMap = new Dictionary<long, Dictionary<int, List<Vec3>>>();
            var seenVert = new HashSet<(int comp, double x, double y, double z)>();
            // PAIR BUDGET (review round 4): two dense components hovering NEAR each other without ever merging
            // (parallel shells just over eps, or direction-gated lines) re-scan each other's vertices for every
            // probe — quadratic again (benchmarked seconds at 20k triangles). Each unordered component pair gets
            // a bounded number of failed comparisons; past it the pair resolves as SEPARATE. The bias is the safe
            // direction for a junk-exposure tool: giving up shows MORE islands, never silently fuses junk away.
            const int PairComparisonBudget = 4096;
            var pairSpent = new Dictionary<long, int>();
            for (int i = 0; i < components.Count; i++)
                foreach (Vec3 p in components[i].Points)
                {
                    if (!seenVert.Add((i, p.X, p.Y, p.Z))) continue;   // seam-duplicate position — no new information
                    long cx = (long)Math.Floor(p.X / eps), cy = (long)Math.Floor(p.Y / eps), cz = (long)Math.Floor(p.Z / eps);
                    for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                    {
                        if (!cellMap.TryGetValue(CellKey(cx + dx, cy + dy, cz + dz), out Dictionary<int, List<Vec3>> byComp)) continue;
                        foreach (var kv in byComp)
                        {
                            int j = kv.Key;
                            if (cd.Find(j) == cd.Find(i) || !DirectionOk(i, j)) continue;
                            long pk = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
                            pairSpent.TryGetValue(pk, out int spent);
                            if (spent > PairComparisonBudget) continue;
                            int used = 0; bool fused = false;
                            foreach (Vec3 q in kv.Value)
                            {
                                used++;
                                double ddx = p.X - q.X, ddy = p.Y - q.Y, ddz = p.Z - q.Z;
                                if (ddx * ddx + ddy * ddy + ddz * ddz <= eps * eps) { cd.Union(i, j); fused = true; break; }
                            }
                            if (!fused) pairSpent[pk] = spent + used;
                        }
                    }
                    long key = CellKey(cx, cy, cz);
                    if (!cellMap.TryGetValue(key, out Dictionary<int, List<Vec3>> home)) cellMap.Add(key, home = new Dictionary<int, List<Vec3>>());
                    if (!home.TryGetValue(i, out List<Vec3> mine)) home.Add(i, mine = new List<Vec3>());
                    mine.Add(p);
                }
            var merged = new Dictionary<int, Component>();
            for (int i = 0; i < components.Count; i++)
            {
                int root = cd.Find(i);
                if (!merged.TryGetValue(root, out Component into)) { merged.Add(root, components[i]); continue; }
                foreach (var kv in components[i].Indices)
                {
                    if (!into.Indices.TryGetValue(kv.Key, out List<uint> list)) into.Indices.Add(kv.Key, kv.Value);
                    else list.AddRange(kv.Value);
                }
                for (int axis = 0; axis < 3; axis++)
                {
                    into.Min[axis] = Math.Min(into.Min[axis], components[i].Min[axis]);
                    into.Max[axis] = Math.Max(into.Max[axis], components[i].Max[axis]);
                }
                into.FirstTriangle = Math.Min(into.FirstTriangle, components[i].FirstTriangle);
                into.TriangleCount += components[i].TriangleCount;
            }
            components = merged.Values.OrderBy(c => c.Min[0]).ThenBy(c => c.Min[1]).ThenBy(c => c.Min[2]).ThenBy(c => c.FirstTriangle).ToList();
            if (components.Count <= 1 && !keepSingle) return null;   // everything within reach of everything: nothing to split
        }
        return new MeshPlan {
            MeshIndex = meshIndex,
            Name = (string)mesh["name"] ?? ("Mesh_" + meshIndex),
            Mesh = mesh,
            Components = components,
            TriangleCount = triangles.Count
        };
    }

    static int VertexId(int accessor, uint index, Accessors reader, Dictionary<VertexKey, int> ids, List<Vertex> vertices, DisjointSet dsu)
    {
        if (index >= reader.Count(accessor)) throw new InvalidDataException("Primitive index exceeds its POSITION accessor.");
        var key = new VertexKey { Accessor = accessor, Index = index };
        int id;
        if (ids.TryGetValue(key, out id)) return id;
        id = dsu.Add();
        ids.Add(key, id);
        vertices.Add(new Vertex { Key = key, Position = reader.Position(accessor, index) });
        return id;
    }

    static void UpdateBounds(double[] min, double[] max, Vec3 point)
    {
        for (int axis = 0; axis < 3; axis++) { min[axis] = Math.Min(min[axis], point[axis]); max[axis] = Math.Max(max[axis], point[axis]); }
    }

    static int SourceIndexType(JObject root, JObject primitive, int positionCount)
    {
        if (primitive["indices"] == null) return positionCount <= ushort.MaxValue ? 5123 : 5125;
        int accessorIndex = primitive.Value<int>("indices");
        return root["accessors"][accessorIndex].Value<int>("componentType");
    }

    static int AppendIndices(JObject root, List<byte> bin, List<uint> indices, int componentType)
    {
        while ((bin.Count & 3) != 0) bin.Add(0);
        int byteOffset = bin.Count;
        uint min = uint.MaxValue, max = 0;
        foreach (uint index in indices)
        {
            min = Math.Min(min, index); max = Math.Max(max, index);
            if (componentType == 5121) { if (index > byte.MaxValue) throw new OverflowException("Index does not fit its original byte component type."); bin.Add((byte)index); }
            else if (componentType == 5123) { if (index > ushort.MaxValue) throw new OverflowException("Index does not fit its original ushort component type."); AddUInt16(bin, (ushort)index); }
            else if (componentType == 5125) AddUInt32(bin, index);
            else throw new InvalidDataException("Unsupported index component type " + componentType + ".");
        }
        var views = (JArray)root["bufferViews"];
        int viewIndex = views.Count;
        views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = byteOffset, ["byteLength"] = bin.Count - byteOffset, ["target"] = 34963 });
        var accessors = (JArray)root["accessors"];
        int accessorIndex = accessors.Count;
        accessors.Add(new JObject { ["bufferView"] = viewIndex, ["componentType"] = componentType,
            ["count"] = indices.Count, ["type"] = "SCALAR", ["min"] = new JArray(min), ["max"] = new JArray(max) });
        return accessorIndex;
    }

    static void AddUInt16(List<byte> bytes, ushort value) { bytes.Add((byte)value); bytes.Add((byte)(value >> 8)); }
    static void AddUInt32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)value); bytes.Add((byte)(value >> 8)); bytes.Add((byte)(value >> 16)); bytes.Add((byte)(value >> 24));
    }

    static string UniqueName(string wanted, IEnumerable<string> existingNames)
    {
        var existing = new HashSet<string>(existingNames.Where(n => !string.IsNullOrEmpty(n)));
        if (!existing.Contains(wanted)) return wanted;
        int suffix = 2;
        while (existing.Contains(wanted + "_" + suffix)) suffix++;
        return wanted + "_" + suffix;
    }

    static Document Parse(byte[] bytes)
    {
        if (bytes.Length < 20 || ReadUInt32(bytes, 0) != Magic || ReadUInt32(bytes, 4) != 2)
            throw new InvalidDataException("Input is not a GLB 2.0 file.");
        uint total = ReadUInt32(bytes, 8);
        if (total != bytes.Length) throw new InvalidDataException("GLB header length does not match the file length.");
        var document = new Document { JsonIndex = -1, BinIndex = -1 };
        int offset = 12;
        while (offset < bytes.Length)
        {
            if (offset + 8 > bytes.Length) throw new InvalidDataException("Truncated GLB chunk header.");
            int length = checked((int)ReadUInt32(bytes, offset));
            uint type = ReadUInt32(bytes, offset + 4);
            offset += 8;
            if (length < 0 || offset + (long)length > bytes.Length) throw new InvalidDataException("Truncated GLB chunk.");
            var data = new byte[length]; Buffer.BlockCopy(bytes, offset, data, 0, length);
            int index = document.Chunks.Count;
            document.Chunks.Add(new Chunk { Type = type, Data = data });
            if (type == JsonChunk && document.JsonIndex < 0) document.JsonIndex = index;
            if (type == BinChunk && document.BinIndex < 0) document.BinIndex = index;
            offset += length;
        }
        if (document.JsonIndex != 0 || document.BinIndex < 0) throw new InvalidDataException("GLB must contain a leading JSON chunk and an embedded BIN chunk.");
        string json = Encoding.UTF8.GetString(document.Chunks[0].Data).TrimEnd(' ', '\0', '\t', '\r', '\n');
        document.Root = JObject.Parse(json);
        return document;
    }

    static byte[] Write(Document document)
    {
        document.Chunks[document.JsonIndex].Data = Pad(Encoding.UTF8.GetBytes(document.Root.ToString(Formatting.None)), 0x20);
        document.Chunks[document.BinIndex].Data = Pad(document.Chunks[document.BinIndex].Data, 0x00);
        long total = 12 + document.Chunks.Sum(c => 8L + c.Data.Length);
        if (total > uint.MaxValue) throw new InvalidDataException("Output GLB exceeds the 4 GiB container limit.");
        var output = new byte[(int)total];
        WriteUInt32(output, 0, Magic); WriteUInt32(output, 4, 2); WriteUInt32(output, 8, (uint)total);
        int offset = 12;
        foreach (Chunk chunk in document.Chunks)
        {
            WriteUInt32(output, offset, (uint)chunk.Data.Length); WriteUInt32(output, offset + 4, chunk.Type);
            Buffer.BlockCopy(chunk.Data, 0, output, offset + 8, chunk.Data.Length);
            offset += 8 + chunk.Data.Length;
        }
        return output;
    }

    static byte[] Pad(byte[] source, byte value)
    {
        int length = (source.Length + 3) & ~3;
        if (length == source.Length) return source;
        var padded = new byte[length]; Buffer.BlockCopy(source, 0, padded, 0, source.Length);
        for (int i = source.Length; i < length; i++) padded[i] = value;
        return padded;
    }

    static void ValidateOutput(byte[] output)
    {
        Document document = Parse(output);
        JArray buffers = document.Root["buffers"] as JArray;
        int length = buffers?[0]?.Value<int>("byteLength") ?? -1;
        if (length < 0 || length > document.Chunks[document.BinIndex].Data.Length)
            throw new InvalidDataException("Output validation failed: BIN length is inconsistent.");
        JArray views = document.Root["bufferViews"] as JArray ?? new JArray();
        foreach (JObject view in views.OfType<JObject>())
        {
            if ((view.Value<int?>("buffer") ?? 0) != 0) continue;
            long end = (long)(view.Value<int?>("byteOffset") ?? 0) + view.Value<int>("byteLength");
            if (end > length) throw new InvalidDataException("Output validation failed: a bufferView exceeds buffer 0.");
        }
    }

    static uint ReadUInt32(byte[] bytes, int offset)
    {
        return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
    }

    static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16); bytes[offset + 3] = (byte)(value >> 24);
    }
}

#if UNITY_EDITOR
public static class GlbDisconnectedPartsMenu
{
    [MenuItem("Tools/HAF/Model Tools/Split disconnected GLB parts…", false, 40)]
    static void SplitGlb()
    {
        string input = EditorUtility.OpenFilePanel("Choose a GLB to split into disconnected parts", "D:/3DModels", "glb");
        if (string.IsNullOrEmpty(input)) return;
        try
        {
            EditorUtility.DisplayProgressBar("HAF — Split disconnected GLB parts", "Reading topology…", 0.35f);
            GlbDisconnectedParts.Result result = GlbDisconnectedParts.Split(File.ReadAllBytes(input));
            if (!result.Changed)
            {
                string warnings = result.Warnings.Count == 0 ? "" : "\n\nSkipped:\n" + string.Join("\n", result.Warnings.Take(8));
                EditorUtility.DisplayDialog("No disconnected parts found", "Every supported mesh is already one attached geometry island." + warnings, "OK");
                return;
            }

            string output = EditorUtility.SaveFilePanel("Write split GLB", Path.GetDirectoryName(input),
                Path.GetFileNameWithoutExtension(input) + "_split_parts", "glb");
            if (string.IsNullOrEmpty(output)) return;
            if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("Source protected", "Choose a different output filename. This tool never overwrites the source GLB.", "OK");
                return;
            }
            if (File.Exists(output) && !EditorUtility.DisplayDialog("Overwrite existing split GLB?", output, "Overwrite", "Cancel")) return;

            EditorUtility.DisplayProgressBar("HAF — Split disconnected GLB parts", "Writing and validating…", 0.8f);
            File.WriteAllBytes(output, result.Bytes);
            ImportIfInsideProject(output);
            string detail = string.Join("\n", result.Details.Take(12));
            if (result.Details.Count > 12) detail += "\n… and " + (result.Details.Count - 12) + " more";
            string warningsText = result.Warnings.Count == 0 ? "" : "\n\nSkipped safely:\n" + string.Join("\n", result.Warnings.Take(8));
            EditorUtility.DisplayDialog("GLB split complete",
                result.NodesSplit + " node(s) split into " + result.ChildPartsCreated + " selectable parts.\n" +
                result.SourceTriangles.ToString("N0") + " triangles preserved.\n\n" + detail + warningsText + "\n\n" + output, "OK");
            EditorUtility.RevealInFinder(output);
            Debug.Log("[HAF GLB Splitter] " + result.NodesSplit + " node(s) -> " + result.ChildPartsCreated +
                " child parts; " + result.SourceTriangles + " triangles preserved. Output: " + output);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("GLB split failed", ex.Message + "\n\nThe source file was not changed.", "OK");
        }
        finally { EditorUtility.ClearProgressBar(); }
    }

    static void ImportIfInsideProject(string path)
    {
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string full = Path.GetFullPath(path);
        string prefix = project.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
        string relative = full.Substring(prefix.Length).Replace('\\', '/');
        AssetDatabase.ImportAsset(relative, ImportAssetOptions.ForceUpdate);
    }
}
#endif
