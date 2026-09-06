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
    }

    sealed class MeshPlan
    {
        public int MeshIndex;
        public string Name;
        public JObject Mesh;
        public List<Component> Components;
        public int TriangleCount;
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
        readonly JObject root;
        readonly byte[] bin;
        readonly JArray accessors;
        readonly JArray views;

        public Accessors(JObject root, byte[] bin)
        {
            this.root = root;
            this.bin = bin;
            accessors = root["accessors"] as JArray ?? throw new InvalidDataException("GLB has no accessors array.");
            views = root["bufferViews"] as JArray ?? throw new InvalidDataException("GLB has no bufferViews array.");
        }

        public int Count(int accessorIndex) => Accessor(accessorIndex).Value<int>("count");

        public Vec3 Position(int accessorIndex, uint index)
        {
            JObject a = Accessor(accessorIndex);
            if ((string)a["type"] != "VEC3") throw new InvalidDataException("POSITION accessor is not VEC3.");
            var values = ReadElement(a, index, 3);
            return new Vec3 { X = values[0], Y = values[1], Z = values[2] };
        }

        public uint Index(int accessorIndex, uint index)
        {
            JObject a = Accessor(accessorIndex);
            if ((string)a["type"] != "SCALAR") throw new InvalidDataException("Index accessor is not SCALAR.");
            int componentType = a.Value<int>("componentType");
            if (componentType != 5121 && componentType != 5123 && componentType != 5125)
                throw new InvalidDataException("Index accessor must use unsigned byte, ushort, or uint.");
            return checked((uint)ReadElement(a, index, 1)[0]);
        }

        JObject Accessor(int index)
        {
            if (index < 0 || index >= accessors.Count) throw new InvalidDataException("Accessor index is out of range.");
            var a = accessors[index] as JObject ?? throw new InvalidDataException("Accessor is not an object.");
            if (a["sparse"] != null) throw new InvalidDataException("Sparse accessors are not supported by this splitter.");
            return a;
        }

        double[] ReadElement(JObject accessor, uint index, int components)
        {
            int count = accessor.Value<int>("count");
            if (index >= count) throw new InvalidDataException("Accessor element is out of range.");
            if (accessor["bufferView"] == null) throw new InvalidDataException("Accessor has no bufferView (compressed/implicit data is unsupported).");
            int viewIndex = accessor.Value<int>("bufferView");
            if (viewIndex < 0 || viewIndex >= views.Count) throw new InvalidDataException("bufferView index is out of range.");
            var view = views[viewIndex] as JObject ?? throw new InvalidDataException("bufferView is not an object.");
            if (view.Value<int?>("buffer").GetValueOrDefault() != 0)
                throw new InvalidDataException("Only GLB buffer 0 can be edited losslessly.");
            if (view["extensions"]?["EXT_meshopt_compression"] != null)
                throw new InvalidDataException("Meshopt-compressed bufferViews are not supported.");

            int componentType = accessor.Value<int>("componentType");
            int bytes = ComponentBytes(componentType);
            int stride = view.Value<int?>("byteStride") ?? bytes * components;
            if (stride < bytes * components) throw new InvalidDataException("bufferView byteStride is smaller than its element.");
            long start = (long)(view.Value<int?>("byteOffset") ?? 0) + (accessor.Value<int?>("byteOffset") ?? 0) + (long)index * stride;
            if (start < 0 || start + bytes * components > bin.Length) throw new InvalidDataException("Accessor reads beyond the BIN chunk.");
            bool normalized = accessor.Value<bool?>("normalized") ?? false;
            var result = new double[components];
            for (int i = 0; i < components; i++) result[i] = ReadComponent(bin, checked((int)start + i * bytes), componentType, normalized);
            return result;
        }
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
        public string NodeName;
        public string MeshName;
        public int Triangles;
        public int Islands;
        public string Blocked;   // non-null = unsupported for splitting (compressed, instanced, non-triangle…)
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
        var reader = new Accessors(root, sourceBin.Take(buffers[0].Value<int>("byteLength")).ToArray());

        var byMesh = new Dictionary<int, MeshPlan>();
        var blockedByMesh = new Dictionary<int, string>();
        var infos = new List<PartInfo>();
        foreach (JObject node in nodes.OfType<JObject>())
        {
            if (node["mesh"] == null) continue;
            int meshIndex = node.Value<int>("mesh");
            var info = new PartInfo { NodeName = (string)node["name"] ?? ("node " + infos.Count), MeshName = (string)meshes[meshIndex]?["name"] ?? ("mesh " + meshIndex) };
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

    public static Result Split(byte[] source) => Split(source, null, 0);

    public static Result Split(byte[] source, ISet<string> onlyNodeNames) => Split(source, onlyNodeNames, 0);

    // onlyNodeNames: when non-null, ONLY nodes whose name is in the set are split (the Model Workshop's
    // selective mode — a hull keeps its junk-free parts whole while the one island-soup part is exploded).
    // Original meshes are never removed, so a mesh shared with an unselected node keeps rendering there.
    // mergeFraction: islands within this fraction of a mesh's own diagonal fuse into one part (0 = topology only).
    public static Result Split(byte[] source, ISet<string> onlyNodeNames, double mergeFraction)
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
        byte[] originalData = sourceBin.Take(declaredBinLength).ToArray();
        var bin = new List<byte>(originalData);
        var result = new Result();
        var reader = new Accessors(root, originalData);

        var referencedMeshes = new SortedSet<int>();
        var instancedMeshes = new HashSet<int>();
        foreach (JObject node in nodes.OfType<JObject>())
        {
            if (node["mesh"] == null) continue;
            if (onlyNodeNames != null && !onlyNodeNames.Contains((string)node["name"])) continue;
            int meshIndex = node.Value<int>("mesh");
            referencedMeshes.Add(meshIndex);
            if (node["extensions"]?["EXT_mesh_gpu_instancing"] != null) instancedMeshes.Add(meshIndex);
        }

        var plans = new Dictionary<int, MeshPlan>();
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
                MeshPlan plan = AnalyzeMesh(meshIndex, (JObject)meshes[meshIndex], reader, keepSingle: false, mergeFraction: mergeFraction);
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
            if (onlyNodeNames != null && !onlyNodeNames.Contains((string)node["name"])) continue;
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

    public static Result SplitFile(string inputPath, string outputPath) => SplitFile(inputPath, outputPath, null, 0);

    public static Result SplitFile(string inputPath, string outputPath, ISet<string> onlyNodeNames) => SplitFile(inputPath, outputPath, onlyNodeNames, 0);

    public static Result SplitFile(string inputPath, string outputPath, ISet<string> onlyNodeNames, double mergeFraction)
    {
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a new output path; the source GLB is never overwritten.");
        Result result = Split(File.ReadAllBytes(inputPath), onlyNodeNames, mergeFraction);
        if (result.Changed) File.WriteAllBytes(outputPath, result.Bytes);
        return result;
    }

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
            for (int i = 0; i < components.Count; i++) cd.Add();
            for (int i = 0; i < components.Count; i++)
                for (int j = i + 1; j < components.Count; j++)
                {
                    double gap = 0;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        double g = Math.Max(components[j].Min[axis] - components[i].Max[axis],
                                            components[i].Min[axis] - components[j].Max[axis]);
                        if (g > 0) gap += g * g;
                    }
                    if (gap <= eps * eps) cd.Union(i, j);
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
