using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

// Compaction (GlbDisconnectedParts.Compact.cs): what no node can reach any more is left out of a Workshop output, and
// what a node CAN reach reads exactly as before. The second half is judged by the GLB reader, which shares no code
// with the Workshop: the file as it was, less the meshes no node uses, must equal the compacted file field by field.
// One fixture per branch of the compaction - the Lab's real files (tools/workshop_compact_drill.sh) are all one
// exporter's shape.
public class GlbCompactTests
{
    // ---------------------------------------------------------------- a GLB built by hand, orphans included
    sealed class Builder
    {
        public readonly List<byte> Bin = new List<byte>();
        public readonly JArray Views = new JArray(), Accessors = new JArray(), Meshes = new JArray(), Nodes = new JArray();
        public readonly JObject Root = new JObject { ["asset"] = new JObject { ["version"] = "2.0" } };

        public int View(byte[] data, int? stride = null, int? target = null, int startRemainder = 0)
        {
            while ((Bin.Count & 3) != startRemainder) Bin.Add(0xEE);   // filler that is NOT zero: a byte copied from the wrong place shows
            var view = new JObject { ["buffer"] = 0, ["byteOffset"] = Bin.Count, ["byteLength"] = data.Length };
            if (stride.HasValue) view["byteStride"] = stride.Value;
            if (target.HasValue) view["target"] = target.Value;
            Bin.AddRange(data); Views.Add(view);
            return Views.Count - 1;
        }
        public int Accessor(int view, int byteOffset, int componentType, string type, int count, float[] min = null, float[] max = null)
        {
            var accessor = new JObject { ["bufferView"] = view, ["byteOffset"] = byteOffset, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
            if (min != null) { accessor["min"] = new JArray(min); accessor["max"] = new JArray(max); }
            Accessors.Add(accessor);
            return Accessors.Count - 1;
        }
        public int Mesh(string name, int position, int? indices = null, int? normal = null)
        {
            var attributes = new JObject { ["POSITION"] = position };
            if (normal.HasValue) attributes["NORMAL"] = normal.Value;
            var primitive = new JObject { ["attributes"] = attributes };
            if (indices.HasValue) primitive["indices"] = indices.Value;
            Meshes.Add(new JObject { ["name"] = name, ["primitives"] = new JArray(primitive) });
            return Meshes.Count - 1;
        }
        public int Node(string name, int? mesh)
        {
            var node = new JObject { ["name"] = name };
            if (mesh.HasValue) node["mesh"] = mesh.Value;
            Nodes.Add(node);
            return Nodes.Count - 1;
        }
        public byte[] Glb()
        {
            while ((Bin.Count & 3) != 0) Bin.Add(0);
            Root["buffers"] = new JArray(new JObject { ["byteLength"] = Bin.Count });
            Root["bufferViews"] = Views; Root["accessors"] = Accessors; Root["meshes"] = Meshes; Root["nodes"] = Nodes;
            Root["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(Enumerable.Range(0, Nodes.Count)) }); Root["scene"] = 0;
            return Pack(Root, Bin.ToArray());
        }
    }

    static byte[] Pack(JObject root, byte[] bin)
    {
        byte[] json = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
        int jsonPad = (4 - json.Length % 4) % 4, binPad = (4 - bin.Length % 4) % 4;
        var glb = new List<byte>();
        void U32(uint v) { glb.Add((byte)v); glb.Add((byte)(v >> 8)); glb.Add((byte)(v >> 16)); glb.Add((byte)(v >> 24)); }
        U32(0x46546C67); U32(2); U32((uint)(12 + 8 + json.Length + jsonPad + 8 + bin.Length + binPad));
        U32((uint)(json.Length + jsonPad)); U32(0x4E4F534A); glb.AddRange(json); for (int i = 0; i < jsonPad; i++) glb.Add(0x20);
        U32((uint)(bin.Length + binPad)); U32(0x004E4942); glb.AddRange(bin); for (int i = 0; i < binPad; i++) glb.Add(0);
        return glb.ToArray();
    }
    static JObject Json(byte[] glb) => JObject.Parse(Encoding.UTF8.GetString(glb, 20, BitConverter.ToInt32(glb, 12)));
    static byte[] BinOf(byte[] glb) { int at = 20 + BitConverter.ToInt32(glb, 12); int n = BitConverter.ToInt32(glb, at); var b = new byte[n]; Buffer.BlockCopy(glb, at + 8, b, 0, n); return b; }

    static byte[] Floats(params float[] v) { var b = new byte[v.Length * 4]; Buffer.BlockCopy(v, 0, b, 0, b.Length); return b; }
    static byte[] Shorts(params ushort[] v) { var b = new byte[v.Length * 2]; Buffer.BlockCopy(v, 0, b, 0, b.Length); return b; }
    static float[] Quad(float x) => new[] { x, 0f, 0f, x + 1, 0f, 0f, x + 1, 1f, 0f, x, 1f, 0f };
    static float[] Min(float x) => new[] { x, 0f, 0f };
    static float[] Max(float x) => new[] { x + 1, 1f, 0f };
    static readonly ushort[] QuadIndices = { 0, 1, 2, 0, 2, 3 };

    // ---------------------------------------------------------------- the two judgements
    /// <summary>The file as the reader sees it, less the meshes no node uses.</summary>
    static HafModel WithoutUnusedMeshes(HafModel m)
    {
        var used = new SortedSet<int>(m.Nodes.Where(n => n.Mesh >= 0).Select(n => n.Mesh));
        var map = new Dictionary<int, int>(); var kept = new List<HafMesh>();
        foreach (int i in used) { map[i] = kept.Count; kept.Add(m.Meshes[i]); }
        m.Meshes.Clear(); m.Meshes.AddRange(kept);
        foreach (var n in m.Nodes) if (n.Mesh >= 0) n.Mesh = map[n.Mesh];
        return m;
    }
    static void EveryNodeReadsAsBefore(byte[] before, byte[] after)
        => Assert.Null(HafModelDiff.FirstDifference(WithoutUnusedMeshes(GlbReader.Read(before)), GlbReader.Read(after)));

    /// <summary>Meshes no node uses, and accessors no live mesh, skin or animation uses.</summary>
    static (int meshes, int accessors) Unreachable(byte[] glb)
    {
        JObject root = Json(glb);
        var meshes = root["meshes"] as JArray ?? new JArray(); var accessors = root["accessors"] as JArray ?? new JArray();
        var liveMesh = new HashSet<int>((root["nodes"] as JArray ?? new JArray()).OfType<JObject>().Where(n => n["mesh"] != null).Select(n => (int)n["mesh"]));
        var live = new HashSet<int>();
        foreach (int mi in liveMesh)
            foreach (JObject p in (JArray)meshes[mi]["primitives"])
            {
                foreach (var a in ((JObject)p["attributes"]).Properties()) live.Add((int)a.Value);
                if (p["indices"] != null) live.Add((int)p["indices"]);
            }
        foreach (JObject s in (root["skins"] as JArray ?? new JArray()).OfType<JObject>()) if (s["inverseBindMatrices"] != null) live.Add((int)s["inverseBindMatrices"]);
        foreach (JObject an in (root["animations"] as JArray ?? new JArray()).OfType<JObject>())
            foreach (JObject sm in (JArray)an["samplers"]) { live.Add((int)sm["input"]); live.Add((int)sm["output"]); }
        return (meshes.Count - liveMesh.Count, accessors.Count - live.Count);
    }

    // ---------------------------------------------------------------- the shapes
    [Fact]
    public void An_orphan_mesh_goes_with_its_accessors_and_bytes_and_the_node_is_renumbered()
    {
        // the fuse shape: every mesh has its own views; mesh 0 lost its node, mesh 1 is drawn
        var b = new Builder();
        int deadPos = b.Accessor(b.View(Floats(Quad(100))), 0, 5126, "VEC3", 4, Min(100), Max(100)), deadIdx = b.Accessor(b.View(Shorts(QuadIndices)), 0, 5123, "SCALAR", 6);
        int livePos = b.Accessor(b.View(Floats(Quad(5))), 0, 5126, "VEC3", 4, Min(5), Max(5)), liveIdx = b.Accessor(b.View(Shorts(QuadIndices)), 0, 5123, "SCALAR", 6);
        b.Mesh("WasFused", deadPos, deadIdx); b.Node("Empty", null); b.Node("Hull", b.Mesh("Hull", livePos, liveIdx));
        byte[] source = b.Glb();
        var r = GlbDisconnectedParts.Compact(source);
        Assert.NotNull(r.Bytes); Assert.True(r.Compaction.Changed);
        Assert.Equal("Compacted: 1 unused mesh(es) and 2 accessor(s) left out; geometry and images 0.0 -> 0.0 MB.", r.Details[0]);
        JObject g = Json(r.Bytes);
        Assert.Equal(new[] { "Hull" }, g["meshes"].Select(m => (string)m["name"]).ToArray());
        Assert.Equal(0, (int)g["nodes"][1]["mesh"]);            // was 1
        Assert.Equal("Empty", (string)g["nodes"][0]["name"]);   // nodes: never touched
        Assert.Equal(2, ((JArray)g["accessors"]).Count); Assert.Equal(2, ((JArray)g["bufferViews"]).Count);
        Assert.Equal(48 + 12, BinOf(r.Bytes).Length);           // 4 positions + 6 ushort indices, nothing else
        Assert.Equal(60, (int)g["buffers"][0]["byteLength"]);
        Assert.Equal((0, 0), Unreachable(r.Bytes));
        EveryNodeReadsAsBefore(source, r.Bytes);
    }

    [Fact]
    public void A_view_two_meshes_share_is_cut_to_the_ranges_the_live_one_reads()
    {
        // the exporter's shape: ONE view for every mesh's positions, ONE for every mesh's indices. The dead mesh's
        // ranges sit inside views the live mesh keeps alive - dropping whole views would save nothing here.
        // The live indices start at byte 12 of a view that itself starts 2 past a multiple of 4.
        var b = new Builder();
        int positions = b.View(Floats(Quad(100).Concat(Quad(5)).ToArray()), target: 34962);
        int indices = b.View(Shorts(QuadIndices.Concat(QuadIndices).ToArray()), target: 34963, startRemainder: 2);
        int deadPos = b.Accessor(positions, 0, 5126, "VEC3", 4, Min(100), Max(100)), deadIdx = b.Accessor(indices, 0, 5123, "SCALAR", 6);
        int livePos = b.Accessor(positions, 48, 5126, "VEC3", 4, Min(5), Max(5)), liveIdx = b.Accessor(indices, 12, 5123, "SCALAR", 6);
        b.Node("Hull", b.Mesh("Hull", livePos, liveIdx)); b.Mesh("Original", deadPos, deadIdx);
        byte[] source = b.Glb();
        var r = GlbDisconnectedParts.Compact(source);
        JObject g = Json(r.Bytes);
        Assert.Equal((0, 0), Unreachable(r.Bytes));
        EveryNodeReadsAsBefore(source, r.Bytes);
        // the kept bytes are the live ranges and nothing more: 48 of positions, 12 of indices (+ the 2 that keep its remainder)
        var views = (JArray)g["bufferViews"];
        Assert.Equal(new[] { 48, 12 }, views.Select(v => (int)v["byteLength"]).ToArray());
        Assert.Equal(new[] { 34962, 34963 }, views.Select(v => (int)v["target"]).ToArray());
        foreach (JObject a in g["accessors"])   // every accessor: offset in its view and absolute offset both multiples of its component size
        {
            int size = (int)a["componentType"] == 5126 ? 4 : 2, inView = (int)a["byteOffset"], absolute = inView + (int)views[(int)a["bufferView"]]["byteOffset"];
            Assert.Equal(0, inView % size); Assert.Equal(0, absolute % size);
        }
        Assert.Equal(2, (int)views[1]["byteOffset"] % 4);       // the indices view keeps its remainder mod 4
        Assert.DoesNotContain((byte)0xEE, BinOf(r.Bytes));       // none of the old filler came along
    }

    [Fact]
    public void An_interleaved_view_keeps_its_stride_and_the_overlapping_ranges_stay_one_segment()
    {
        // position + normal interleaved, 24 bytes a vertex; vertices 0-3 belong to the orphan, 4-7 to the live mesh
        var data = new List<float>();
        foreach (float x in new[] { 100f, 5f })
            for (int v = 0; v < 4; v++) { data.AddRange(new[] { Quad(x)[v * 3], Quad(x)[v * 3 + 1], Quad(x)[v * 3 + 2] }); data.AddRange(new[] { 0f, 0f, 1f }); }
        var b = new Builder();
        int vertices = b.View(Floats(data.ToArray()), stride: 24, target: 34962);
        int indices = b.View(Shorts(QuadIndices));
        int deadPos = b.Accessor(vertices, 0, 5126, "VEC3", 4, Min(100), Max(100)), deadNrm = b.Accessor(vertices, 12, 5126, "VEC3", 4);
        int livePos = b.Accessor(vertices, 96, 5126, "VEC3", 4, Min(5), Max(5)), liveNrm = b.Accessor(vertices, 108, 5126, "VEC3", 4);
        int idx = b.Accessor(indices, 0, 5123, "SCALAR", 6);
        b.Mesh("Original", deadPos, idx, deadNrm); b.Node("Hull", b.Mesh("Hull", livePos, idx, liveNrm));
        byte[] source = b.Glb();
        var r = GlbDisconnectedParts.Compact(source);
        JObject g = Json(r.Bytes);
        EveryNodeReadsAsBefore(source, r.Bytes);
        var views = (JArray)g["bufferViews"];
        Assert.Equal(2, views.Count);
        Assert.Equal(24, (int)views[0]["byteStride"]); Assert.Equal(96, (int)views[0]["byteLength"]);   // ONE segment for position and normal
        Assert.Equal(new[] { 0, 12 }, g["accessors"].Where(a => (int)a["bufferView"] == 0).Select(a => (int)a["byteOffset"]).ToArray());
    }

    [Fact]
    public void A_vertex_accessor_a_live_part_still_shares_with_the_orphan_stays()
    {
        // the split shape: the parts index into the PARENT's vertex accessor; the parent mesh lost its node
        var b = new Builder();
        int pos = b.Accessor(b.View(Floats(Quad(5))), 0, 5126, "VEC3", 4, Min(5), Max(5));
        int parentIdx = b.Accessor(b.View(Shorts(QuadIndices)), 0, 5123, "SCALAR", 6);
        int partIdx = b.Accessor(b.View(Shorts(0, 1, 2)), 0, 5123, "SCALAR", 3);
        b.Mesh("Parent", pos, parentIdx); b.Node("Parent", null); b.Node("Parent_Part_001", b.Mesh("Part", pos, partIdx));
        byte[] source = b.Glb();
        var r = GlbDisconnectedParts.Compact(source);
        Assert.Equal((0, 0), Unreachable(r.Bytes));
        Assert.Equal(1, r.Compaction.MeshesBefore - r.Compaction.MeshesAfter); Assert.Equal(1, r.Compaction.AccessorsBefore - r.Compaction.AccessorsAfter);
        EveryNodeReadsAsBefore(source, r.Bytes);
    }

    [Fact]
    public void A_mesh_another_node_still_uses_is_not_an_orphan()
    {
        var b = new Builder();
        int mesh = b.Mesh("Shared", b.Accessor(b.View(Floats(Quad(5))), 0, 5126, "VEC3", 4, Min(5), Max(5)), b.Accessor(b.View(Shorts(QuadIndices)), 0, 5123, "SCALAR", 6));
        b.Node("A", mesh); b.Node("B", mesh);
        byte[] source = b.Glb();
        var removed = GlbDisconnectedParts.RemoveMeshes(source, new HashSet<int> { 0 });   // A loses the mesh; B still draws it
        Assert.False(removed.Compaction.Changed); Assert.Null(removed.Compaction.Skipped);
        Assert.Single(removed.Details);                                                    // no compaction line: nothing was left out
        Assert.Single((JArray)Json(removed.Bytes)["meshes"]);
        Assert.Equal(BinOf(source), BinOf(removed.Bytes));                                 // and the binary chunk is the source's, byte for byte
    }

    [Fact]
    public void What_a_skin_an_animation_or_an_image_reads_is_kept_though_no_mesh_names_it()
    {
        var b = new Builder();
        int deadPos = b.Accessor(b.View(Floats(Quad(100))), 0, 5126, "VEC3", 4, Min(100), Max(100));
        int image = b.View(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });   // 7 bytes: an odd length, kept whole
        int ibm = b.Accessor(b.View(Floats(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)), 0, 5126, "MAT4", 1);
        int times = b.Accessor(b.View(Floats(0, 1)), 0, 5126, "SCALAR", 2, new[] { 0f }, new[] { 1f });
        int moves = b.Accessor(b.View(Floats(0, 0, 0, 1, 0, 0)), 0, 5126, "VEC3", 2);
        int livePos = b.Accessor(b.View(Floats(Quad(5))), 0, 5126, "VEC3", 4, Min(5), Max(5));
        b.Mesh("Original", deadPos); b.Node("Hull", b.Mesh("Hull", livePos)); b.Node("Joint", null);
        b.Root["skins"] = new JArray(new JObject { ["joints"] = new JArray(1), ["inverseBindMatrices"] = ibm });
        b.Root["animations"] = new JArray(new JObject { ["samplers"] = new JArray(new JObject { ["input"] = times, ["output"] = moves }),
            ["channels"] = new JArray(new JObject { ["sampler"] = 0, ["target"] = new JObject { ["node"] = 1, ["path"] = "translation" } }) });
        b.Root["images"] = new JArray(new JObject { ["bufferView"] = image, ["mimeType"] = "image/png" });
        byte[] source = b.Glb();
        var r = GlbDisconnectedParts.Compact(source);
        JObject g = Json(r.Bytes);
        Assert.Equal((0, 0), Unreachable(r.Bytes));
        Assert.Equal(4, ((JArray)g["accessors"]).Count);   // the skin's, the animation's two, the live positions
        var view = g["bufferViews"][(int)g["images"][0]["bufferView"]];
        Assert.Equal(7, (int)view["byteLength"]);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }, BinOf(r.Bytes).Skip((int)view["byteOffset"]).Take(7).ToArray());
        EveryNodeReadsAsBefore(source, r.Bytes);
    }

    [Fact]
    public void A_sparse_accessor_keeps_its_views_whole()
    {
        var b = new Builder();
        int deadPos = b.Accessor(b.View(Floats(Quad(100))), 0, 5126, "VEC3", 4, Min(100), Max(100));
        int sparseIndices = b.View(Shorts(0, 9, 9)), sparseValues = b.View(Floats(7, 7, 7, 9, 9, 9));   // each longer than the one entry the block reads
        int livePos = b.Accessor(b.View(Floats(Quad(5))), 0, 5126, "VEC3", 4, Min(5), Max(7));
        ((JObject)b.Accessors[livePos])["sparse"] = new JObject { ["count"] = 1,
            ["indices"] = new JObject { ["bufferView"] = sparseIndices, ["componentType"] = 5123 }, ["values"] = new JObject { ["bufferView"] = sparseValues } };
        b.Mesh("Original", deadPos); b.Node("Hull", b.Mesh("Hull", livePos));
        byte[] source = b.Glb(); byte[] bin = BinOf(source);
        var r = GlbDisconnectedParts.Compact(source);
        JObject g = Json(r.Bytes); var sparse = g["accessors"][0]["sparse"]; byte[] after = BinOf(r.Bytes);
        foreach (var (token, was) in new[] { (sparse["indices"]["bufferView"], sparseIndices), (sparse["values"]["bufferView"], sparseValues) })
        {
            var now = g["bufferViews"][(int)token]; var old = b.Views[was];
            Assert.Equal((int)old["byteLength"], (int)now["byteLength"]);
            Assert.Equal(bin.Skip((int)old["byteOffset"]).Take((int)old["byteLength"]), after.Skip((int)now["byteOffset"]).Take((int)now["byteLength"]));
        }
        Assert.Equal(3, ((JArray)g["bufferViews"]).Count);
    }

    // ---------------------------------------------------------------- when it must not, and says so
    [Fact]
    public void An_extension_that_may_reference_accessors_refuses_the_compaction_and_the_operation_still_succeeds()
    {
        var b = new Builder();
        int mesh = b.Mesh("Hull", b.Accessor(b.View(Floats(Quad(5))), 0, 5126, "VEC3", 4, Min(5), Max(5)));
        b.Node("A", mesh); b.Node("B", b.Mesh("Other", b.Accessor(b.View(Floats(Quad(9))), 0, 5126, "VEC3", 4, Min(9), Max(9))));
        b.Root["extensionsUsed"] = new JArray("KHR_materials_specular", "EXT_mesh_gpu_instancing");
        byte[] source = b.Glb();
        var removed = GlbDisconnectedParts.RemoveMeshes(source, new HashSet<int> { 1 });
        Assert.True(removed.Changed); Assert.NotNull(removed.Bytes);
        Assert.False(removed.Compaction.Changed);
        Assert.Equal("Not compacted: the file uses the extension 'EXT_mesh_gpu_instancing', which may reference accessors or buffer views in ways this tool does not follow", removed.Details.Last());
        Assert.Equal(2, ((JArray)Json(removed.Bytes)["meshes"]).Count);   // the orphan is still there, as before this change
        Assert.Equal(BinOf(source), BinOf(removed.Bytes));
        var again = GlbDisconnectedParts.Compact(removed.Bytes);          // asked for by name: the same answer, and no bytes
        Assert.Null(again.Bytes); Assert.StartsWith("Not compacted: the file uses the extension", again.Details[0]);
    }

    [Theory]
    [InlineData("KHR_materials_pbrSpecularGlossiness", true)] [InlineData("KHR_materials_variants", true)] [InlineData("KHR_texture_transform", true)]
    [InlineData("KHR_mesh_quantization", true)] [InlineData("KHR_lights_punctual", true)]
    [InlineData("KHR_draco_mesh_compression", false)] [InlineData("EXT_meshopt_compression", false)] [InlineData("EXT_mesh_gpu_instancing", false)] [InlineData("ACME_unknown", false)]
    public void Only_extensions_that_cannot_reference_geometry_let_a_compaction_through(string name, bool lets)
        => Assert.Equal(lets, GlbDisconnectedParts.ExtensionCannotReachGeometry(name));

    [Fact]
    public void A_reference_that_points_nowhere_refuses_the_compaction_and_changes_nothing()
    {
        var b = new Builder();
        b.Mesh("Original", b.Accessor(b.View(Floats(Quad(100))), 0, 5126, "VEC3", 4, Min(100), Max(100)));
        b.Node("Hull", b.Mesh("Hull", b.Accessor(b.View(Floats(Quad(5))), 0, 5126, "VEC3", 4, Min(5), Max(5))));
        ((JObject)b.Accessors[1])["count"] = 400;   // runs far past its 48-byte view
        byte[] source = b.Glb();
        var r = GlbDisconnectedParts.Compact(source);
        Assert.Null(r.Bytes);
        Assert.Equal("Not compacted: accessor 1 runs past its buffer view", r.Details[0]);
    }

    [Fact]
    public void A_file_with_nothing_unreachable_is_not_rewritten_and_a_compacted_one_compacts_no_further()
    {
        var b = new Builder();
        b.Node("Hull", b.Mesh("Hull", b.Accessor(b.View(Floats(Quad(5))), 0, 5126, "VEC3", 4, Min(5), Max(5))));
        b.Mesh("Original", b.Accessor(b.View(Floats(Quad(100))), 0, 5126, "VEC3", 4, Min(100), Max(100)));
        byte[] source = b.Glb();
        var first = GlbDisconnectedParts.Compact(source);
        Assert.NotNull(first.Bytes);
        var second = GlbDisconnectedParts.Compact(first.Bytes);
        Assert.Null(second.Bytes); Assert.False(second.Compaction.Changed);
        Assert.Equal("Nothing to compact: every mesh is used by a node, every accessor by a mesh, a skin or an animation.", second.Details[0]);
        // an operation on a compact file that orphans nothing writes its binary chunk as it found it
        var renamed = new Builder();
        int mesh = renamed.Mesh("M", renamed.Accessor(renamed.View(Floats(Quad(5))), 0, 5126, "VEC3", 4, Min(5), Max(5)));
        renamed.Node("Same", mesh); renamed.Node("Same", mesh);
        byte[] twins = renamed.Glb();
        var unique = GlbDisconnectedParts.UniqueNodeNames(twins);
        Assert.True(unique.Changed); Assert.False(unique.Compaction.Changed); Assert.Single(unique.Details);
        Assert.Equal(BinOf(twins), BinOf(unique.Bytes));
    }

    [Fact]
    public void A_file_whose_every_part_was_removed_is_left_as_it_is()
    {
        var b = new Builder();
        b.Node("Hull", b.Mesh("Hull", b.Accessor(b.View(Floats(Quad(5))), 0, 5126, "VEC3", 4, Min(5), Max(5))));
        var removed = GlbDisconnectedParts.RemoveMeshes(b.Glb(), new HashSet<int> { 0 });
        Assert.True(removed.Changed);
        Assert.Equal("Not compacted: nothing in the binary chunk would remain", removed.Details.Last());
        Assert.Single((JArray)Json(removed.Bytes)["meshes"]);   // an empty "meshes" and a zero-length buffer are not valid glTF
    }
}
