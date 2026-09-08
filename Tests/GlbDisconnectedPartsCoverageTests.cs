using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

// Coverage for the GLB splitter paths the 2026-09-07 review found untested: every fixture in the original
// suite was single-node, single-primitive, ushort-indexed, tightly packed, and all-positive — so the uint/ubyte
// index readers, the non-indexed branch, per-primitive bucketing, the spatial hash's negative-cell wrap, shared
// meshes, and duplicate-name/index selection all ran with zero tests. Fixtures here are hand-rolled per case.
public class GlbDisconnectedPartsCoverageTests
{
    // Two triangles ~10 units apart: always two topological islands.
    static readonly float[] TwoFarTriangles = {
        0f, 0f, 0f,   1f, 0f, 0f,   0f, 1f, 0f,
        10f, 0f, 0f,  11f, 0f, 0f,  10f, 1f, 0f
    };

    [Fact]
    public void Uint32_index_accessor_round_trips()
    {
        byte[] source = BuildIndexed(TwoFarTriangles, componentType: 5125);
        Assert.Equal(2, GlbDisconnectedParts.Analyze(source).Single(i => i.NodeName == "Hull").Islands);
        var result = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0);
        Assert.Equal(2, result.ChildPartsCreated);
        Assert.Equal(2, result.SourceTriangles);
        Assert.Equal(2, result.OutputTriangles);
    }

    [Fact]
    public void Ubyte_index_accessor_round_trips()
    {
        byte[] source = BuildIndexed(TwoFarTriangles, componentType: 5121);
        Assert.Equal(2, GlbDisconnectedParts.Analyze(source).Single(i => i.NodeName == "Hull").Islands);
        var result = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0);
        Assert.Equal(2, result.ChildPartsCreated);
        Assert.Equal(2, result.OutputTriangles);
    }

    [Fact]
    public void Non_indexed_primitive_splits()
    {
        // No "indices" property: every consecutive vertex triple is a triangle.
        var bin = new BinBuilder();
        int posOff = bin.Add(Floats(TwoFarTriangles), 4);
        var root = Skeleton("Hull");
        root["bufferViews"] = new JArray(View(posOff, TwoFarTriangles.Length * 4));
        root["accessors"] = new JArray(Vec3Accessor(0, TwoFarTriangles.Length / 3));
        root["meshes"] = new JArray(Mesh(Primitive(position: 0, indices: null)));
        byte[] source = WriteGlb(root, bin.Bytes);

        Assert.Equal(2, GlbDisconnectedParts.Analyze(source).Single(i => i.NodeName == "Hull").Islands);
        var result = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0);
        Assert.Equal(2, result.ChildPartsCreated);
        Assert.Equal(2, result.SourceTriangles);
        Assert.Equal(2, result.OutputTriangles);
    }

    [Fact]
    public void Multiple_primitives_per_mesh_bucket_correctly()
    {
        // One mesh, two primitives (own accessors each), geometrically far apart -> two islands, and the
        // split's per-primitive index bucketing must land each island in the right child with its material.
        float[] triA = { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f };
        float[] triB = { 10f, 0f, 0f, 11f, 0f, 0f, 10f, 1f, 0f };
        var bin = new BinBuilder();
        int posA = bin.Add(Floats(triA), 4);
        int idxA = bin.Add(Ushorts(0, 1, 2), 2);
        int posB = bin.Add(Floats(triB), 4);
        int idxB = bin.Add(Ushorts(0, 1, 2), 2);
        var root = Skeleton("Hull");
        root["bufferViews"] = new JArray(
            View(posA, triA.Length * 4), View(idxA, 6), View(posB, triB.Length * 4), View(idxB, 6));
        root["accessors"] = new JArray(
            Vec3Accessor(0, 3), ScalarAccessor(1, 5123, 3), Vec3Accessor(2, 3), ScalarAccessor(3, 5123, 3));
        root["meshes"] = new JArray(Mesh(
            Primitive(position: 0, indices: 1, material: 1),
            Primitive(position: 2, indices: 3, material: 2)));
        byte[] source = WriteGlb(root, bin.Bytes);

        Assert.Equal(2, GlbDisconnectedParts.Analyze(source).Single(i => i.NodeName == "Hull").Islands);
        var result = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0);
        Assert.Equal(2, result.ChildPartsCreated);
        Assert.Equal(2, result.SourceTriangles);
        Assert.Equal(2, result.OutputTriangles);
        // The bucketing claim needs more than counts (PR #26 self-review: a regression filing every island
        // under primitive 0 would keep all the totals): each child must carry its OWN primitive's material.
        var outJson = ParseGlbJson(result.Bytes);
        var childMats = ((JArray)outJson["nodes"])
            .Where(n => (string)n["name"] != null && ((string)n["name"]).Contains("_Part_"))
            .Select(n => (int)outJson["meshes"][(int)n["mesh"]]["primitives"][0]["material"])
            .OrderBy(m => m).ToArray();
        Assert.Equal(new[] { 1, 2 }, childMats);
    }

    [Fact]
    public void Distance_merge_works_across_the_negative_cell_boundary()
    {
        // Review question the original suite never covered: every fixture vertex was >= 0, so the spatial
        // hash's signed-cell wrap (cell -1 vs cell 0 sharing a face) ran untested. Two slivers straddle the
        // origin 0.1 apart (cells on both sides of zero in x AND y), one sliver sits 9 units away: at a merge
        // reach of ~0.5 the close pair must fuse across the sign boundary and the far one must stay separate.
        float[] verts = {
            -1.0f, -0.05f, 0f,  -0.05f, -0.05f, 0f,  -1.0f, -0.03f, 0f,
             0.05f, 0.05f, 0f,   1.0f,  0.05f, 0f,   0.05f, 0.07f, 0f,
             9.0f,  0f,    0f,  10.0f,  0f,    0f,   9.0f,  1f,    0f
        };
        byte[] source = BuildIndexed(verts, componentType: 5123);
        Assert.Equal(3, GlbDisconnectedParts.Analyze(source).Single(i => i.NodeName == "Hull").Islands);
        Assert.Equal(2, GlbDisconnectedParts.Analyze(source, 0.05).Single(i => i.NodeName == "Hull").Islands);
    }

    [Fact]
    public void Shared_mesh_splits_the_selected_node_and_keeps_the_other_rendering()
    {
        // Two nodes reference the SAME mesh; only node 0 is selected. The unselected node must keep showing
        // the complete original geometry while the selected one gains part children.
        var bin = new BinBuilder();
        int posOff = bin.Add(Floats(TwoFarTriangles), 4);
        int idxOff = bin.Add(Ushorts(0, 1, 2, 3, 4, 5), 2);
        var root = SkeletonTwoNodes("UserA", "UserB", meshForBoth: 0);
        root["bufferViews"] = new JArray(View(posOff, TwoFarTriangles.Length * 4), View(idxOff, 12));
        root["accessors"] = new JArray(Vec3Accessor(0, 6), ScalarAccessor(1, 5123, 6));
        root["meshes"] = new JArray(Mesh(Primitive(position: 0, indices: 1)));
        byte[] source = WriteGlb(root, bin.Bytes);

        var result = GlbDisconnectedParts.Split(source, new HashSet<int> { 0 }, 0);
        Assert.Equal(1, result.NodesSplit);
        Assert.Equal(2, result.ChildPartsCreated);

        var after = GlbDisconnectedParts.Analyze(result.Bytes);
        var userB = after.Single(i => i.NodeName == "UserB");
        Assert.Equal(2, userB.Islands);       // the untouched node still renders BOTH islands
        Assert.Equal(2, userB.Triangles);
        Assert.Equal(2, after.Count(i => i.NodeName != null && i.NodeName.Contains("_Part_")));
    }

    static void AssertPartChildrenCarry(JObject outJson, params string[] attributes)
    {
        var nodes = (JArray)outJson["nodes"];
        var parts = nodes.Where(n => (string)n["name"] != null && ((string)n["name"]).Contains("_Part_")).ToList();
        Assert.NotEmpty(parts);
        foreach (var p in parts)
        {
            var attrs = (JObject)outJson["meshes"][(int)p["mesh"]]["primitives"][0]["attributes"];
            foreach (var a in attributes) Assert.NotNull(attrs[a]);
        }
    }

    [Fact]
    public void Duplicate_node_names_split_only_the_selected_index()
    {
        // Two nodes both named "Part", each with its own two-island mesh — the exact case node-INDEX
        // selection exists for. Selecting index 1 must leave node 0 whole.
        var bin = new BinBuilder();
        int posOff = bin.Add(Floats(TwoFarTriangles), 4);
        int idxOff = bin.Add(Ushorts(0, 1, 2, 3, 4, 5), 2);
        var root = new JObject {
            ["asset"] = new JObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0, 1) }),
            ["nodes"] = new JArray(
                new JObject { ["name"] = "Part", ["mesh"] = 0 },
                new JObject { ["name"] = "Part", ["mesh"] = 1 }),
            ["meshes"] = new JArray(
                Mesh(Primitive(position: 0, indices: 1)),
                Mesh(Primitive(position: 0, indices: 1))),
            ["buffers"] = new JArray(new JObject { ["byteLength"] = 0 }),   // patched by WriteGlb caller below
            ["bufferViews"] = new JArray(View(posOff, TwoFarTriangles.Length * 4), View(idxOff, 12)),
            ["accessors"] = new JArray(Vec3Accessor(0, 6), ScalarAccessor(1, 5123, 6))
        };
        byte[] source = WriteGlb(root, bin.Bytes);

        var result = GlbDisconnectedParts.Split(source, new HashSet<int> { 1 }, 0);
        Assert.Equal(1, result.NodesSplit);
        Assert.Equal(2, result.ChildPartsCreated);

        var after = GlbDisconnectedParts.Analyze(result.Bytes);
        var untouched = after.Where(i => i.NodeName == "Part").ToList();   // node 0 keeps its own row
        Assert.Single(untouched);
        Assert.Equal(2, untouched[0].Islands);
        Assert.Equal(2, after.Count(i => i.NodeName != null && i.NodeName.Contains("_Part_")));
    }

    [Fact]
    public void Skinned_node_moves_its_skin_to_the_part_children()
    {
        // A skinned part is the Vehicle Lab fast-path's bread and butter: after a selective split the children
        // must inherit node.skin (and carry the JOINTS/WEIGHTS attributes per island) or the parts detach from
        // the armature. The parent keeps neither mesh nor skin — the children ARE the geometry now.
        var bin = new BinBuilder();
        int posOff = bin.Add(Floats(TwoFarTriangles), 4);
        int idxOff = bin.Add(Ushorts(0, 1, 2, 3, 4, 5), 2);
        var joints = new byte[6 * 4];                       // VEC4 ubyte, all joint 0
        int jntOff = bin.Add(joints, 4);
        var weights = new float[6 * 4];
        for (int i = 0; i < 6; i++) weights[i * 4] = 1f;    // VEC4 float, full weight on joint 0
        int wgtOff = bin.Add(Floats(weights), 4);

        var root = Skeleton("Hull");
        ((JArray)root["nodes"])[0]["skin"] = 0;
        root["skins"] = new JArray(new JObject { ["joints"] = new JArray(0) });
        root["bufferViews"] = new JArray(
            View(posOff, TwoFarTriangles.Length * 4), View(idxOff, 12), View(jntOff, joints.Length), View(wgtOff, weights.Length * 4));
        root["accessors"] = new JArray(
            Vec3Accessor(0, 6), ScalarAccessor(1, 5123, 6),
            new JObject { ["bufferView"] = 2, ["componentType"] = 5121, ["count"] = 6, ["type"] = "VEC4" },
            new JObject { ["bufferView"] = 3, ["componentType"] = 5126, ["count"] = 6, ["type"] = "VEC4" });
        var prim = Primitive(position: 0, indices: 1);
        ((JObject)prim["attributes"])["JOINTS_0"] = 2;
        ((JObject)prim["attributes"])["WEIGHTS_0"] = 3;
        root["meshes"] = new JArray(Mesh(prim));
        byte[] source = WriteGlb(root, bin.Bytes);

        var result = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0);
        Assert.Equal(2, result.ChildPartsCreated);

        var outJson = ParseGlbJson(result.Bytes);
        var outNodes = (JArray)outJson["nodes"];
        var parts = outNodes.Where(n => (string)n["name"] != null && ((string)n["name"]).Contains("_Part_")).ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(0, (int)p["skin"]));       // the skin followed the geometry
        Assert.All(parts, p => Assert.NotNull(p["mesh"]));
        var hull = outNodes.Single(n => (string)n["name"] == "Hull");
        Assert.Null(hull["mesh"]);                                     // the parent is a pure group now
        // …and the skinning DATA followed too (PR #26 self-review: the comment claimed this but nothing
        // asserted it) — each child primitive must still carry its JOINTS_0/WEIGHTS_0 attributes.
        AssertPartChildrenCarry(outJson, "JOINTS_0", "WEIGHTS_0");
    }

    [Fact]
    public void Malformed_glb_throws_instead_of_corrupting()
    {
        // Rejection must be an exception BEFORE any output is produced — never a half-written split.
        byte[] good = BuildIndexed(TwoFarTriangles, componentType: 5123);

        byte[] badMagic = (byte[])good.Clone();
        badMagic[0] = (byte)'X';
        Assert.ThrowsAny<Exception>(() => GlbDisconnectedParts.Split(badMagic, new HashSet<string> { "Hull" }, 0));

        // buffers[0].byteLength claims more than the BIN chunk holds — an accessor could read out of bounds.
        var bin = new BinBuilder();
        int posOff = bin.Add(Floats(TwoFarTriangles), 4);
        int idxOff = bin.Add(Ushorts(0, 1, 2, 3, 4, 5), 2);
        var root = Skeleton("Hull");
        root["bufferViews"] = new JArray(View(posOff, TwoFarTriangles.Length * 4), View(idxOff, 12));
        root["accessors"] = new JArray(Vec3Accessor(0, 6), ScalarAccessor(1, 5123, 6));
        root["meshes"] = new JArray(Mesh(Primitive(position: 0, indices: 1)));
        byte[] oversized = WriteGlb(root, bin.Bytes);
        var json = ParseGlbJson(oversized);
        json["buffers"][0]["byteLength"] = 1_000_000;                  // lies past the real BIN chunk
        byte[] lying = WriteGlb(json, ExtractBin(oversized), patchBufferLength: false);
        Assert.ThrowsAny<Exception>(() => GlbDisconnectedParts.Split(lying, new HashSet<string> { "Hull" }, 0));
    }

    [Fact]
    public void Dense_ribbons_chain_under_the_comparison_budget_and_the_gate_keeps_them_parallel()
    {
        // Two contracts at density. (1) 150 genuinely close slats per ribbon (hundreds of vertices,
        // exact-duplicate seam positions, dense cells) must still chain into ONE ribbon each — the merge's
        // dense-path structures (seam dedup, per-component cell grouping) under real load. (2) The direction
        // gate must keep the two parallel dashed ribbons apart even though the merge reach spans the gap
        // between them — the galley rule, now at density. NOTE the budget-EXHAUSTION path (a pair resolving
        // separate after 4096 failed comparisons) is still untested: gated pairs never spend budget here, and
        // building a deterministic exhaustion fixture is open work.
        const int slats = 150;                       // 2 tris per slat; slats connect only through the merge
        var verts = new List<float>();
        void Ribbon(float y)
        {
            for (int i = 0; i < slats; i++)
            {
                float x = i * 0.1f;
                verts.AddRange(new[] { x, y, 0f, x + 0.08f, y, 0f, x, y + 0.02f, 0f });
                verts.AddRange(new[] { x + 0.08f, y, 0f, x + 0.08f, y + 0.02f, 0f, x, y + 0.02f, 0f });
            }
        }
        Ribbon(0f);          // ribbon A along y = 0
        Ribbon(0.1f);        // ribbon B parallel, 0.08 between the facing edges
        byte[] source = BuildIndexed(verts.ToArray(), componentType: 5125);

        // Raw: each slat's two triangles attach through their exact-duplicate seam positions, slats do not.
        Assert.Equal(2 * slats, GlbDisconnectedParts.Analyze(source).Single(i => i.NodeName == "Hull").Islands);
        // Merged at 1% (~0.15 reach): the 0.02 slat gaps chain colinearly -> one island per ribbon; the 0.08
        // cross-ribbon gap is within reach but OFF-AXIS -> the direction gate keeps the ribbons separate.
        Assert.Equal(2, GlbDisconnectedParts.Analyze(source, 0.01).Single(i => i.NodeName == "Hull").Islands);
    }

    // ---- fixture plumbing ----

    static JObject ParseGlbJson(byte[] glb)
    {
        int jsonLength = BitConverter.ToInt32(glb, 12);
        return JObject.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength));
    }
    static byte[] ExtractBin(byte[] glb)
    {
        int jsonLength = BitConverter.ToInt32(glb, 12);
        int binHeader = 20 + jsonLength;
        int binLength = BitConverter.ToInt32(glb, binHeader);
        var bin = new byte[binLength];
        Buffer.BlockCopy(glb, binHeader + 8, bin, 0, binLength);
        return bin;
    }

    // Single node "name" -> mesh 0, one indexed primitive over `positions` with the given index component type.
    static byte[] BuildIndexed(float[] positions, int componentType)
    {
        int n = positions.Length / 3;
        var bin = new BinBuilder();
        int posOff = bin.Add(Floats(positions), 4);
        byte[] idx;
        int align;
        if (componentType == 5125) { idx = new byte[n * 4]; for (int i = 0; i < n; i++) Buffer.BlockCopy(BitConverter.GetBytes((uint)i), 0, idx, i * 4, 4); align = 4; }
        else if (componentType == 5123) { idx = new byte[n * 2]; for (int i = 0; i < n; i++) Buffer.BlockCopy(BitConverter.GetBytes((ushort)i), 0, idx, i * 2, 2); align = 2; }
        else { idx = new byte[n]; for (int i = 0; i < n; i++) idx[i] = (byte)i; align = 1; }
        int idxOff = bin.Add(idx, align);
        var root = Skeleton("Hull");
        root["bufferViews"] = new JArray(View(posOff, positions.Length * 4), View(idxOff, idx.Length));
        root["accessors"] = new JArray(Vec3Accessor(0, n), ScalarAccessor(1, componentType, n));
        root["meshes"] = new JArray(Mesh(Primitive(position: 0, indices: 1)));
        return WriteGlb(root, bin.Bytes);
    }

    static JObject Skeleton(string nodeName) => new JObject {
        ["asset"] = new JObject { ["version"] = "2.0" },
        ["scene"] = 0,
        ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0) }),
        ["nodes"] = new JArray(new JObject { ["name"] = nodeName, ["mesh"] = 0 }),
        ["buffers"] = new JArray(new JObject { ["byteLength"] = 0 })
    };

    static JObject SkeletonTwoNodes(string a, string b, int meshForBoth) => new JObject {
        ["asset"] = new JObject { ["version"] = "2.0" },
        ["scene"] = 0,
        ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0, 1) }),
        ["nodes"] = new JArray(
            new JObject { ["name"] = a, ["mesh"] = meshForBoth },
            new JObject { ["name"] = b, ["mesh"] = meshForBoth }),
        ["buffers"] = new JArray(new JObject { ["byteLength"] = 0 })
    };

    static JObject View(int offset, int length) =>
        new JObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length };
    static JObject Vec3Accessor(int view, int count) =>
        new JObject { ["bufferView"] = view, ["componentType"] = 5126, ["count"] = count, ["type"] = "VEC3" };
    static JObject ScalarAccessor(int view, int componentType, int count) =>
        new JObject { ["bufferView"] = view, ["componentType"] = componentType, ["count"] = count, ["type"] = "SCALAR" };
    static JObject Primitive(int position, int? indices, int? material = null)
    {
        var p = new JObject { ["attributes"] = new JObject { ["POSITION"] = position } };
        if (indices.HasValue) p["indices"] = indices.Value;
        if (material.HasValue) p["material"] = material.Value;
        return p;
    }
    static JObject Mesh(params JObject[] primitives) =>
        new JObject { ["name"] = "M", ["primitives"] = new JArray(primitives.Cast<object>().ToArray()) };

    static byte[] Floats(float[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) Buffer.BlockCopy(BitConverter.GetBytes(v[i]), 0, b, i * 4, 4);
        return b;
    }
    static byte[] Ushorts(params int[] v)
    {
        var b = new byte[v.Length * 2];
        for (int i = 0; i < v.Length; i++) Buffer.BlockCopy(BitConverter.GetBytes((ushort)v[i]), 0, b, i * 2, 2);
        return b;
    }

    sealed class BinBuilder
    {
        readonly List<byte> b = new List<byte>();
        public int Add(byte[] data, int align)
        {
            while (b.Count % align != 0) b.Add(0);
            int off = b.Count;
            b.AddRange(data);
            return off;
        }
        public byte[] Bytes => b.ToArray();
    }

    static byte[] WriteGlb(JObject root, byte[] bin, bool patchBufferLength = true)
    {
        if (patchBufferLength) root["buffers"] = new JArray(new JObject { ["byteLength"] = bin.Length });
        byte[] json = Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));
        int jsonLength = (json.Length + 3) & ~3;
        int binLength = (bin.Length + 3) & ~3;
        byte[] glb = new byte[12 + 8 + jsonLength + 8 + binLength];
        WriteUInt(glb, 0, 0x46546C67); WriteUInt(glb, 4, 2); WriteUInt(glb, 8, (uint)glb.Length);
        WriteUInt(glb, 12, (uint)jsonLength); WriteUInt(glb, 16, 0x4E4F534A);
        Buffer.BlockCopy(json, 0, glb, 20, json.Length);
        for (int i = json.Length; i < jsonLength; i++) glb[20 + i] = 0x20;
        int binHeader = 20 + jsonLength;
        WriteUInt(glb, binHeader, (uint)binLength); WriteUInt(glb, binHeader + 4, 0x004E4942);
        Buffer.BlockCopy(bin, 0, glb, binHeader + 8, bin.Length);
        return glb;
    }
    static void WriteUInt(byte[] b, int offset, uint v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, offset, 4);
}
