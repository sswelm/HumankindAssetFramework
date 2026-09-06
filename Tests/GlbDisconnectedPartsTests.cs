using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

public class GlbDisconnectedPartsTests
{
    [Fact]
    public void Splits_disconnected_triangles_into_child_nodes_and_preserves_materials()
    {
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f,
            10f, 0f, 0f, 11f, 0f, 0f, 10f, 1f, 0f
        });

        GlbDisconnectedParts.Result result = GlbDisconnectedParts.Split(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.MeshesSplit);
        Assert.Equal(1, result.NodesSplit);
        Assert.Equal(2, result.ChildPartsCreated);
        Assert.Equal(2, result.SourceTriangles);
        Assert.Equal(2, result.OutputTriangles);

        JObject root = ReadJson(result.Bytes);
        var nodes = (JArray)root["nodes"];
        var parent = (JObject)nodes[0];
        Assert.Null(parent["mesh"]);
        Assert.Equal(new[] { 1, 2, 3 }, ((JArray)parent["children"]).Values<int>().ToArray());
        Assert.Equal("Hull_Part_001", (string)nodes[2]["name"]);
        Assert.Equal("Hull_Part_002", (string)nodes[3]["name"]);

        var meshes = (JArray)root["meshes"];
        var accessors = (JArray)root["accessors"];
        int splitTriangleCount = 0;
        foreach (JObject child in nodes.Skip(2).OfType<JObject>())
        {
            JObject mesh = (JObject)meshes[child.Value<int>("mesh")];
            JObject primitive = (JObject)((JArray)mesh["primitives"])[0];
            Assert.Equal(7, primitive.Value<int>("material"));
            splitTriangleCount += accessors[primitive.Value<int>("indices")].Value<int>("count") / 3;
        }
        Assert.Equal(2, splitTriangleCount);
    }

    [Fact]
    public void Keeps_an_attached_surface_whose_seam_vertices_are_duplicated()
    {
        // Two triangles form one square, but the shared edge uses duplicate indices as happens at UV/normal seams.
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f,
            1f, 0f, 0f,  1f, 1f, 0f,  0f, 1f, 0f
        });

        GlbDisconnectedParts.Result result = GlbDisconnectedParts.Split(source);

        Assert.False(result.Changed);
        Assert.Equal(0, result.NodesSplit);
        Assert.Equal(0, result.ChildPartsCreated);
    }

    [Fact]
    public void Retargets_morph_weight_animation_to_every_split_child()
    {
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f,
            10f, 0f, 0f, 11f, 0f, 0f, 10f, 1f, 0f
        }, true);

        JObject root = ReadJson(GlbDisconnectedParts.Split(source).Bytes);
        var channels = (JArray)root["animations"][0]["channels"];

        Assert.Equal(new[] { 2, 3 }, channels.Select(c => c["target"].Value<int>("node")).ToArray());
        Assert.All(channels, c => Assert.Equal("weights", (string)c["target"]["path"]));
        Assert.All(channels, c => Assert.Equal(0, c.Value<int>("sampler")));
    }

    [Fact]
    public void Selective_split_only_touches_named_nodes_and_analyze_reports_islands()
    {
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f,
            10f, 0f, 0f, 11f, 0f, 0f, 10f, 1f, 0f
        });

        // Analyze: read-only picker data — one row for the Hull node, two islands, two triangles.
        var infos = GlbDisconnectedParts.Analyze(source);
        var hull = Assert.Single(infos, i => i.NodeName == "Hull");
        Assert.Equal(2, hull.Islands);
        Assert.Equal(2, hull.Triangles);
        Assert.Null(hull.Blocked);

        // A filter that names no present node splits nothing — the unchecked part stays whole.
        var none = GlbDisconnectedParts.Split(source, new HashSet<string> { "SomethingElse" });
        Assert.False(none.Changed);
        Assert.Equal(0, none.ChildPartsCreated);

        // Naming the node reproduces the full split exactly.
        var chosen = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" });
        Assert.True(chosen.Changed);
        Assert.Equal(2, chosen.ChildPartsCreated);
        Assert.Equal(2, chosen.SourceTriangles);
        Assert.Equal(2, chosen.OutputTriangles);
    }

    [Fact]
    public void Distance_merge_fuses_near_islands_and_keeps_far_junk_separate()
    {
        // Three disconnected triangles: two 0.5 apart (a segmented rope), one 50 away (floating junk).
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,    1f, 0f, 0f,    0f, 1f, 0f,
            1.5f, 0f, 0f,  2.5f, 0f, 0f,  1.5f, 1f, 0f,
            50f, 0f, 0f,   51f, 0f, 0f,   50f, 1f, 0f
        });

        // Pure topology: three islands.
        Assert.Equal(3, GlbDisconnectedParts.Analyze(source).Single(i => i.NodeName == "Hull").Islands);

        // 5% of the ~51-unit diagonal ≈ 2.5 merge reach: the rope halves fuse, the junk stays its own part.
        Assert.Equal(2, GlbDisconnectedParts.Analyze(source, 0.05).Single(i => i.NodeName == "Hull").Islands);
        var result = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0.05);
        Assert.Equal(2, result.ChildPartsCreated);
        Assert.Equal(3, result.SourceTriangles);
        Assert.Equal(3, result.OutputTriangles);
    }

    static byte[] BuildGlb(float[] positions, bool withWeightAnimation = false)
    {
        byte[] positionBytes = new byte[positions.Length * 4];
        for (int i = 0; i < positions.Length; i++)
            Buffer.BlockCopy(BitConverter.GetBytes(positions[i]), 0, positionBytes, i * 4, 4);
        ushort[] sourceIndices = Enumerable.Range(0, positions.Length / 3).Select(i => (ushort)i).ToArray();
        byte[] indexBytes = new byte[sourceIndices.Length * 2];
        for (int i = 0; i < sourceIndices.Length; i++)
            Buffer.BlockCopy(BitConverter.GetBytes(sourceIndices[i]), 0, indexBytes, i * 2, 2);
        int indexOffset = (positionBytes.Length + 3) & ~3;
        byte[] bin = new byte[indexOffset + indexBytes.Length];
        Buffer.BlockCopy(positionBytes, 0, bin, 0, positionBytes.Length);
        Buffer.BlockCopy(indexBytes, 0, bin, indexOffset, indexBytes.Length);

        var root = new JObject {
            ["asset"] = new JObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0) }),
            ["nodes"] = new JArray(
                new JObject { ["name"] = "Hull", ["mesh"] = 0, ["children"] = new JArray(1), ["translation"] = new JArray(2, 3, 4) },
                new JObject { ["name"] = "ExistingChild" }),
            ["meshes"] = new JArray(new JObject {
                ["name"] = "HullMesh",
                ["primitives"] = new JArray(new JObject {
                    ["attributes"] = new JObject { ["POSITION"] = 0 }, ["indices"] = 1, ["material"] = 7
                })
            }),
            ["buffers"] = new JArray(new JObject { ["byteLength"] = bin.Length }),
            ["bufferViews"] = new JArray(
                new JObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = positionBytes.Length, ["target"] = 34962 },
                new JObject { ["buffer"] = 0, ["byteOffset"] = indexOffset, ["byteLength"] = indexBytes.Length, ["target"] = 34963 }),
            ["accessors"] = new JArray(
                new JObject { ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = positions.Length / 3, ["type"] = "VEC3" },
                new JObject { ["bufferView"] = 1, ["componentType"] = 5123, ["count"] = sourceIndices.Length, ["type"] = "SCALAR" })
        };
        if (withWeightAnimation)
        {
            root["animations"] = new JArray(new JObject {
                ["samplers"] = new JArray(new JObject { ["input"] = 0, ["output"] = 0 }),
                ["channels"] = new JArray(new JObject {
                    ["sampler"] = 0,
                    ["target"] = new JObject { ["node"] = 0, ["path"] = "weights" }
                })
            });
        }
        return WriteGlb(root, bin);
    }

    static byte[] WriteGlb(JObject root, byte[] bin)
    {
        byte[] json = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
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

    static JObject ReadJson(byte[] glb)
    {
        int length = (int)ReadUInt(glb, 12);
        return JObject.Parse(Encoding.UTF8.GetString(glb, 20, length).TrimEnd(' ', '\0'));
    }

    static uint ReadUInt(byte[] bytes, int offset) =>
        (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);

    static void WriteUInt(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16); bytes[offset + 3] = (byte)(value >> 24);
    }
}
