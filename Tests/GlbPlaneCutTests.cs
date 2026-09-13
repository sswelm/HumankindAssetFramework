// Plane-cut coverage: CutNodeByPlane partitions WHOLE triangles of one node into _CutA/_CutB children by a
// world-space axis plane, with the island splitter's lossless mechanics (vertex bytes untouched, appended
// index accessors, triangle-preservation check). ExtractPart is the WYSIWYG preview feed — world-space
// positions/triangles — so its bounds are what a UI slider maps onto.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

public class GlbPlaneCutTests
{
    // A connected 4-triangle strip along X: quad (0..1) + quad (1..2) sharing the x=1 edge, all one island.
    static readonly float[] Strip = {
        0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f,
        1f, 0f, 0f,  1f, 1f, 0f,  0f, 1f, 0f,
        1f, 0f, 0f,  2f, 0f, 0f,  1f, 1f, 0f,
        2f, 0f, 0f,  2f, 1f, 0f,  1f, 1f, 0f
    };

    [Fact]
    public void Cuts_a_connected_strip_into_two_children_by_centroid_side()
    {
        byte[] source = BuildGlb(Strip);
        Assert.False(GlbDisconnectedParts.Split(source).Changed);   // premise: one island, the splitter is helpless here

        var result = GlbDisconnectedParts.CutNodeByPlane(source, 0, 0, 1.0);

        Assert.True(result.Changed);
        Assert.Equal(1, result.NodesSplit);
        Assert.Equal(2, result.ChildPartsCreated);
        Assert.Equal(4, result.SourceTriangles);
        Assert.Equal(4, result.OutputTriangles);

        JObject root = ReadJson(result.Bytes);
        var nodes = (JArray)root["nodes"];
        var parent = (JObject)nodes[0];
        Assert.Null(parent["mesh"]);
        Assert.Equal("Hull_CutA", (string)nodes[1]["name"]);
        Assert.Equal("Hull_CutB", (string)nodes[2]["name"]);

        // Both strip quads have one triangle with centroid below x=1 and one at/above: 2 tris per side,
        // and every CutA triangle's centroid must actually sit at/above the plane.
        Assert.Equal(2, TriangleCount(root, (JObject)nodes[1]));
        Assert.Equal(2, TriangleCount(root, (JObject)nodes[2]));
        Assert.All(TriangleCentroids(root, result.Bytes, (JObject)nodes[1]), c => Assert.True(c >= 1.0));
        Assert.All(TriangleCentroids(root, result.Bytes, (JObject)nodes[2]), c => Assert.True(c < 1.0));

        // Material rides along on every side's primitive.
        var meshes = (JArray)root["meshes"];
        foreach (JObject child in new[] { (JObject)nodes[1], (JObject)nodes[2] })
            Assert.Equal(7, ((JObject)((JArray)((JObject)meshes[child.Value<int>("mesh")])["primitives"])[0]).Value<int>("material"));
    }

    [Fact]
    public void Cut_respects_the_node_world_transform()
    {
        // Same strip, but the node rotates +90 deg about Z: local +X becomes world +Y. A world-space Y plane
        // at 1 must therefore split it 2/2 — a local-space cut on Y would put ALL triangles below (y<=1).
        byte[] source = BuildGlb(Strip, rotationZ90: true);

        var result = GlbDisconnectedParts.CutNodeByPlane(source, 0, 1, 1.0);

        Assert.True(result.Changed);
        JObject root = ReadJson(result.Bytes);
        var nodes = (JArray)root["nodes"];
        Assert.Equal(2, TriangleCount(root, (JObject)nodes[1]));
        Assert.Equal(2, TriangleCount(root, (JObject)nodes[2]));
    }

    [Fact]
    public void A_plane_outside_the_part_changes_nothing()
    {
        byte[] source = BuildGlb(Strip);
        var result = GlbDisconnectedParts.CutNodeByPlane(source, 0, 0, 99.0);
        Assert.False(result.Changed);
        Assert.Equal(0, result.ChildPartsCreated);
        Assert.Contains(result.Warnings, w => w.Contains("every triangle on one side"));
    }

    [Fact]
    public void ExtractPart_reports_world_space_geometry()
    {
        var geo = GlbDisconnectedParts.ExtractPart(BuildGlb(Strip, rotationZ90: true), 0);
        Assert.Equal("Hull", geo.NodeName);
        Assert.Equal(12, geo.Triangles.Length);   // 4 triangles
        // Rotated +90 deg about Z, local X in [0,2] lands in world Y [0,2]; local Y in [0,1] lands in world X [-1,0].
        Assert.Equal(-1.0, geo.Min[0], 3);
        Assert.Equal(0.0, geo.Max[0], 3);
        Assert.Equal(0.0, geo.Min[1], 3);
        Assert.Equal(2.0, geo.Max[1], 3);
    }

    [Fact]
    public void Morph_weight_animation_retargets_to_both_cut_children()
    {
        byte[] source = BuildGlb(Strip, withWeightAnimation: true);
        JObject root = ReadJson(GlbDisconnectedParts.CutNodeByPlane(source, 0, 0, 1.0).Bytes);
        var channels = (JArray)root["animations"][0]["channels"];
        Assert.Equal(new[] { 1, 2 }, channels.Select(c => c["target"].Value<int>("node")).OrderBy(n => n).ToArray());
        Assert.All(channels, c => Assert.Equal("weights", (string)c["target"]["path"]));
    }

    [Fact]
    public void A_second_node_sharing_the_mesh_is_warned_about_and_keeps_the_original()
    {
        byte[] source = BuildGlb(Strip, secondNodeSharesMesh: true);
        var result = GlbDisconnectedParts.CutNodeByPlane(source, 0, 0, 1.0);
        Assert.True(result.Changed);
        Assert.Contains(result.Warnings, w => w.Contains("shares the cut mesh"));
        JObject root = ReadJson(result.Bytes);
        var nodes = (JArray)root["nodes"];
        var twin = nodes.OfType<JObject>().First(n => (string)n["name"] == "Twin");
        Assert.Equal(0, twin.Value<int>("mesh"));   // still the original, uncut mesh
    }

    // Facing-cut fixture: 7 disconnected triangles — 2 horizontal at y=0 (hull bottom), 2 horizontal at y=2
    // (deck), 2 vertical at x=5 (bow plating), 1 tilted exactly 30 deg from level at y=5 (cambered deck edge).
    static readonly float[] Facing = {
        0f, 0f, 0f,  1f, 0f, 0f,  0f, 0f, 1f,
        1f, 0f, 0f,  1f, 0f, 1f,  0f, 0f, 1f,
        0f, 2f, 0f,  1f, 2f, 0f,  0f, 2f, 1f,
        1f, 2f, 0f,  1f, 2f, 1f,  0f, 2f, 1f,
        5f, 0f, 0f,  5f, 1f, 0f,  5f, 0f, 1f,
        5f, 1f, 0f,  5f, 1f, 1f,  5f, 0f, 1f,
        0f, 5f, 0f,  1f, 5f, 0f,  0f, 5.57735f, 1f
    };

    [Fact]
    public void Facing_cut_partitions_by_surface_orientation()
    {
        byte[] source = BuildGlb(Facing);

        // Tilt limit 45: both horizontal quads AND the 30-deg face are "level"; the vertical wall is not.
        var result = GlbDisconnectedParts.CutNodeByFacing(source, 0, 1, 45.0, -10.0);
        Assert.True(result.Changed);
        JObject root = ReadJson(result.Bytes);
        var nodes = (JArray)root["nodes"];
        Assert.Equal(5, TriangleCount(root, (JObject)nodes[1]));   // _CutA: 2 bottom + 2 top + tilted
        Assert.Equal(2, TriangleCount(root, (JObject)nodes[2]));   // _CutB: the vertical wall

        // Tilt limit 20: the 30-deg face now counts as steep.
        root = ReadJson(GlbDisconnectedParts.CutNodeByFacing(source, 0, 1, 20.0, -10.0).Bytes);
        nodes = (JArray)root["nodes"];
        Assert.Equal(4, TriangleCount(root, (JObject)nodes[1]));
        Assert.Equal(3, TriangleCount(root, (JObject)nodes[2]));
    }

    [Fact]
    public void Facing_floor_keeps_low_horizontal_surfaces_out_of_CutA()
    {
        // Floor at y=1: the equally-horizontal bottom (y=0) stays in _CutB — the hull-bottom case.
        byte[] source = BuildGlb(Facing);
        var result = GlbDisconnectedParts.CutNodeByFacing(source, 0, 1, 45.0, 1.0);
        JObject root = ReadJson(result.Bytes);
        var nodes = (JArray)root["nodes"];
        Assert.Equal(3, TriangleCount(root, (JObject)nodes[1]));   // top quad + tilted face
        Assert.Equal(4, TriangleCount(root, (JObject)nodes[2]));   // bottom quad + wall
    }

    // ---- boundary precision (review P2, 2026-09-13): the preview classifies ExtractPart's FLOAT positions;
    // the writer must judge the SAME rounding, or boundary faces flip sides between screen and file. A node
    // translation of double 0.1 is the trap: its float rounds ABOVE it (0.10000000149 > 0.1). ----

    [Fact]
    public void Writer_agrees_with_the_float_preview_at_an_exact_boundary_plane()
    {
        // Everything shows yellow in the preview (all float centroids >= the float minimum), so the cut must
        // REFUSE as one-sided. The unsnapped writer put the bottom face's double centroid (0.1) below the
        // float plane (0.10000000149) and split what the preview showed as uncuttable. The fixture must have
        // a FLAT face at the exact minimum (Facing's bottom quad) — the Strip's sloped centroids never touch
        // the boundary and would pass either way.
        byte[] source = BuildGlb(Facing, translationY: 0.1);
        var geo = GlbDisconnectedParts.ExtractPart(source, 0);
        var result = GlbDisconnectedParts.CutNodeByPlane(source, 0, 1, geo.Min[1]);
        Assert.False(result.Changed);
        Assert.Contains(result.Warnings, w => w.Contains("every triangle on one side"));
    }

    [Fact]
    public void Facing_floor_at_zero_percent_includes_the_bottom_face()
    {
        // "Only above: 0%" = floor at the part's float minimum — the WHOLE part must be judged by facing
        // alone. The unsnapped writer excluded the lowest horizontal face (double 0.1 < float min).
        byte[] source = BuildGlb(Facing, translationY: 0.1);
        var geo = GlbDisconnectedParts.ExtractPart(source, 0);
        var result = GlbDisconnectedParts.CutNodeByFacing(source, 0, 1, 45.0, geo.Min[1]);
        Assert.True(result.Changed);
        JObject root = ReadJson(result.Bytes);
        var nodes = (JArray)root["nodes"];
        Assert.Equal(5, TriangleCount(root, (JObject)nodes[1]));   // 2 bottom + 2 top + tilted — bottom INCLUDED
        Assert.Equal(2, TriangleCount(root, (JObject)nodes[2]));   // the vertical wall only
    }

    // ---- helpers (the GlbDisconnectedPartsTests builder, plus rotation / shared-node options) ----

    static byte[] BuildGlb(float[] positions, bool withWeightAnimation = false, bool rotationZ90 = false, bool secondNodeSharesMesh = false, double translationY = 0)
    {
        var bin = new List<byte>();
        foreach (float f in positions) bin.AddRange(BitConverter.GetBytes(f));
        int vertexCount = positions.Length / 3;
        int indexOffset = bin.Count;
        for (ushort i = 0; i < vertexCount; i++) bin.AddRange(BitConverter.GetBytes(i));
        while ((bin.Count & 3) != 0) bin.Add(0);

        double[] mins = { double.MaxValue, double.MaxValue, double.MaxValue };
        double[] maxs = { double.MinValue, double.MinValue, double.MinValue };
        for (int i = 0; i < vertexCount; i++)
            for (int a = 0; a < 3; a++)
            { mins[a] = Math.Min(mins[a], positions[i * 3 + a]); maxs[a] = Math.Max(maxs[a], positions[i * 3 + a]); }

        var hull = new JObject { ["name"] = "Hull", ["mesh"] = 0 };
        if (rotationZ90)   // +90 deg about Z: quaternion (0, 0, sin45, cos45)
            hull["rotation"] = new JArray(0.0, 0.0, Math.Sqrt(0.5), Math.Sqrt(0.5));
        if (translationY != 0)
            hull["translation"] = new JArray(0.0, translationY, 0.0);
        var nodes = new JArray { hull };
        var sceneNodes = new JArray(0);
        if (secondNodeSharesMesh)
        {
            nodes.Add(new JObject { ["name"] = "Twin", ["mesh"] = 0, ["translation"] = new JArray(0.0, 0.0, 5.0) });
            sceneNodes.Add(1);
        }

        var root = new JObject
        {
            ["asset"] = new JObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JArray { new JObject { ["nodes"] = sceneNodes } },
            ["nodes"] = nodes,
            ["meshes"] = new JArray {
                new JObject {
                    ["name"] = "HullMesh",
                    ["primitives"] = new JArray {
                        new JObject {
                            ["attributes"] = new JObject { ["POSITION"] = 0 },
                            ["indices"] = 1,
                            ["material"] = 7
                        }
                    }
                }
            },
            ["accessors"] = new JArray {
                new JObject { ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = vertexCount, ["type"] = "VEC3",
                              ["min"] = new JArray(mins[0], mins[1], mins[2]), ["max"] = new JArray(maxs[0], maxs[1], maxs[2]) },
                new JObject { ["bufferView"] = 1, ["componentType"] = 5123, ["count"] = vertexCount, ["type"] = "SCALAR" }
            },
            ["bufferViews"] = new JArray {
                new JObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = indexOffset },
                new JObject { ["buffer"] = 0, ["byteOffset"] = indexOffset, ["byteLength"] = vertexCount * 2 }
            },
            ["buffers"] = new JArray { new JObject { ["byteLength"] = bin.Count } }
        };

        if (withWeightAnimation)
        {
            int timeOffset = bin.Count;
            bin.AddRange(BitConverter.GetBytes(0f)); bin.AddRange(BitConverter.GetBytes(1f));
            ((JArray)root["bufferViews"]).Add(new JObject { ["buffer"] = 0, ["byteOffset"] = timeOffset, ["byteLength"] = 8 });
            ((JArray)root["accessors"]).Add(new JObject { ["bufferView"] = 2, ["componentType"] = 5126, ["count"] = 2, ["type"] = "SCALAR", ["min"] = new JArray(0f), ["max"] = new JArray(1f) });
            ((JArray)root["accessors"]).Add(new JObject { ["bufferView"] = 2, ["componentType"] = 5126, ["count"] = 2, ["type"] = "SCALAR" });
            root["animations"] = new JArray {
                new JObject {
                    ["samplers"] = new JArray { new JObject { ["input"] = 2, ["output"] = 3, ["interpolation"] = "LINEAR" } },
                    ["channels"] = new JArray { new JObject { ["sampler"] = 0, ["target"] = new JObject { ["node"] = 0, ["path"] = "weights" } } }
                }
            };
            ((JObject)root["buffers"][0])["byteLength"] = bin.Count;
        }

        return WriteGlb(root, bin.ToArray());
    }

    static int TriangleCount(JObject root, JObject node)
    {
        var mesh = (JObject)((JArray)root["meshes"])[node.Value<int>("mesh")];
        int count = 0;
        foreach (JObject primitive in ((JArray)mesh["primitives"]).OfType<JObject>())
            count += ((JArray)root["accessors"])[primitive.Value<int>("indices")].Value<int>("count") / 3;
        return count;
    }

    // X-axis centroids of a cut child's triangles, read back from the OUTPUT bytes — proof the partition is real.
    static List<double> TriangleCentroids(JObject root, byte[] glb, JObject node)
    {
        byte[] bin = BinChunk(glb);
        var mesh = (JObject)((JArray)root["meshes"])[node.Value<int>("mesh")];
        var centroids = new List<double>();
        foreach (JObject primitive in ((JArray)mesh["primitives"]).OfType<JObject>())
        {
            var accessors = (JArray)root["accessors"];
            var views = (JArray)root["bufferViews"];
            var indexAcc = (JObject)accessors[primitive.Value<int>("indices")];
            var indexView = (JObject)views[indexAcc.Value<int>("bufferView")];
            int indexBase = (indexView.Value<int?>("byteOffset") ?? 0) + (indexAcc.Value<int?>("byteOffset") ?? 0);
            var posAcc = (JObject)accessors[((JObject)primitive["attributes"]).Value<int>("POSITION")];
            var posView = (JObject)views[posAcc.Value<int>("bufferView")];
            int posBase = (posView.Value<int?>("byteOffset") ?? 0) + (posAcc.Value<int?>("byteOffset") ?? 0);
            int n = indexAcc.Value<int>("count");
            for (int t = 0; t < n; t += 3)
            {
                double c = 0;
                for (int k = 0; k < 3; k++)
                {
                    int idx = BitConverter.ToUInt16(bin, indexBase + (t + k) * 2);
                    c += BitConverter.ToSingle(bin, posBase + idx * 12);
                }
                centroids.Add(c / 3.0);
            }
        }
        return centroids;
    }

    static byte[] BinChunk(byte[] glb)
    {
        int offset = 12;
        while (offset < glb.Length)
        {
            int length = BitConverter.ToInt32(glb, offset);
            uint type = BitConverter.ToUInt32(glb, offset + 4);
            offset += 8;
            if (type == 0x004E4942) return glb.Skip(offset).Take(length).ToArray();
            offset += length;
        }
        throw new InvalidOperationException("No BIN chunk.");
    }

    static JObject ReadJson(byte[] glb)
    {
        int jsonLength = BitConverter.ToInt32(glb, 12);
        return JObject.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength));
    }

    static byte[] WriteGlb(JObject root, byte[] bin)
    {
        byte[] json = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
        int jsonPadded = (json.Length + 3) & ~3;
        int binPadded = (bin.Length + 3) & ~3;
        var output = new byte[12 + 8 + jsonPadded + 8 + binPadded];
        WriteUInt(output, 0, 0x46546C67);
        WriteUInt(output, 4, 2);
        WriteUInt(output, 8, (uint)output.Length);
        WriteUInt(output, 12, (uint)jsonPadded);
        WriteUInt(output, 16, 0x4E4F534A);
        Buffer.BlockCopy(json, 0, output, 20, json.Length);
        for (int i = json.Length; i < jsonPadded; i++) output[20 + i] = 0x20;
        int binHeader = 20 + jsonPadded;
        WriteUInt(output, binHeader, (uint)binPadded);
        WriteUInt(output, binHeader + 4, 0x004E4942);
        Buffer.BlockCopy(bin, 0, output, binHeader + 8, bin.Length);
        return output;
    }

    static void WriteUInt(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16); bytes[offset + 3] = (byte)(value >> 24);
    }
}
