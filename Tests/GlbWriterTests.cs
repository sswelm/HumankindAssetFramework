using System;
using System.IO;
using System.Linq;
using Xunit;

// THE GLB WRITER (2026-10-01, step 2 of replacing Blender). A model read from the reader tests' full fixture, written,
// and read again is the same model, field by field; a hand-built model with uint indices and both influence sets
// survives the trip; what the writer cannot write as it is, it refuses by name; the written file is a valid GLB
// container (magic, version, aligned chunks).
public class GlbWriterTests
{
    static void Same(HafModel a, HafModel b)
    {
        Assert.Equal(a.Nodes.Count, b.Nodes.Count); Assert.Equal(a.Meshes.Count, b.Meshes.Count); Assert.Equal(a.Materials.Count, b.Materials.Count);
        Assert.Equal(a.Textures.Count, b.Textures.Count); Assert.Equal(a.Images.Count, b.Images.Count); Assert.Equal(a.Skins.Count, b.Skins.Count); Assert.Equal(a.Animations.Count, b.Animations.Count);
        Assert.Equal(a.Roots, b.Roots);
        for (int i = 0; i < a.Nodes.Count; i++)
        {
            HafNode x = a.Nodes[i], y = b.Nodes[i];
            Assert.Equal(x.Name, y.Name); Assert.Equal(x.Parent, y.Parent); Assert.Equal(x.Children, y.Children); Assert.Equal(x.Mesh, y.Mesh); Assert.Equal(x.Skin, y.Skin);
            Assert.Equal(x.HasMatrix, y.HasMatrix);
            if (x.HasMatrix) Assert.Equal(x.Matrix, y.Matrix);
            else { Assert.Equal(x.Translation, y.Translation); Assert.Equal(x.Rotation, y.Rotation); Assert.Equal(x.Scale, y.Scale); }
        }
        for (int i = 0; i < a.Meshes.Count; i++)
        {
            Assert.Equal(a.Meshes[i].Name, b.Meshes[i].Name); Assert.Equal(a.Meshes[i].Primitives.Count, b.Meshes[i].Primitives.Count);
            for (int k = 0; k < a.Meshes[i].Primitives.Count; k++)
            {
                HafPrimitive x = a.Meshes[i].Primitives[k], y = b.Meshes[i].Primitives[k];
                Assert.Equal(x.VertexCount, y.VertexCount); Assert.Equal(x.Mode, y.Mode); Assert.Equal(x.Material, y.Material);
                Assert.Equal(x.Positions, y.Positions); Assert.Equal(x.Normals, y.Normals); Assert.Equal(x.Tangents, y.Tangents); Assert.Equal(x.Uv0, y.Uv0); Assert.Equal(x.Uv1, y.Uv1);
                Assert.Equal(x.Colors, y.Colors); Assert.Equal(x.Joints, y.Joints); Assert.Equal(x.Weights, y.Weights); Assert.Equal(x.Joints1, y.Joints1); Assert.Equal(x.Weights1, y.Weights1);
                Assert.Equal(x.Indices, y.Indices);
            }
        }
        for (int i = 0; i < a.Materials.Count; i++)
        {
            HafMaterial x = a.Materials[i], y = b.Materials[i];
            Assert.Equal(x.Name, y.Name); Assert.Equal(x.BaseColorFactor, y.BaseColorFactor); Assert.Equal(x.BaseColorTexture, y.BaseColorTexture); Assert.Equal(x.BaseColorTexCoord, y.BaseColorTexCoord);
            Assert.Equal(x.MetallicFactor, y.MetallicFactor); Assert.Equal(x.RoughnessFactor, y.RoughnessFactor); Assert.Equal(x.MetallicRoughnessTexture, y.MetallicRoughnessTexture);
            Assert.Equal(x.NormalTexture, y.NormalTexture); Assert.Equal(x.NormalScale, y.NormalScale); Assert.Equal(x.OcclusionTexture, y.OcclusionTexture); Assert.Equal(x.OcclusionStrength, y.OcclusionStrength);
            Assert.Equal(x.EmissiveTexture, y.EmissiveTexture); Assert.Equal(x.EmissiveFactor, y.EmissiveFactor); Assert.Equal(x.AlphaMode, y.AlphaMode); Assert.Equal(x.AlphaCutoff, y.AlphaCutoff); Assert.Equal(x.DoubleSided, y.DoubleSided);
            Assert.Equal(x.ExtensionsJson, y.ExtensionsJson);
        }
        for (int i = 0; i < a.Textures.Count; i++) { Assert.Equal(a.Textures[i].Name, b.Textures[i].Name); Assert.Equal(a.Textures[i].Source, b.Textures[i].Source); Assert.Equal(a.Textures[i].Sampler, b.Textures[i].Sampler); }
        for (int i = 0; i < a.Images.Count; i++) { Assert.Equal(a.Images[i].Name, b.Images[i].Name); Assert.Equal(a.Images[i].MimeType, b.Images[i].MimeType); Assert.Equal(a.Images[i].Bytes, b.Images[i].Bytes); }
        for (int i = 0; i < a.Skins.Count; i++)
        {
            HafSkin x = a.Skins[i], y = b.Skins[i];
            Assert.Equal(x.Name, y.Name); Assert.Equal(x.Joints, y.Joints); Assert.Equal(x.Skeleton, y.Skeleton);
            Assert.Equal(x.InverseBindMatrices == null, y.InverseBindMatrices == null);
            if (x.InverseBindMatrices != null) for (int k = 0; k < x.InverseBindMatrices.Length; k++) Assert.Equal((float)x.InverseBindMatrices[k], (float)y.InverseBindMatrices[k]);   // stored as float32 in the file
        }
        for (int i = 0; i < a.Animations.Count; i++)
        {
            HafAnimation x = a.Animations[i], y = b.Animations[i];
            Assert.Equal(x.Name, y.Name); Assert.Equal(x.Duration, y.Duration, 6); Assert.Equal(x.Samplers.Count, y.Samplers.Count); Assert.Equal(x.Channels.Count, y.Channels.Count);
            for (int k = 0; k < x.Samplers.Count; k++) { Assert.Equal(x.Samplers[k].Times, y.Samplers[k].Times); Assert.Equal(x.Samplers[k].Values, y.Samplers[k].Values); Assert.Equal(x.Samplers[k].Components, y.Samplers[k].Components); Assert.Equal(x.Samplers[k].Interpolation, y.Samplers[k].Interpolation); }
            for (int k = 0; k < x.Channels.Count; k++) { Assert.Equal(x.Channels[k].Sampler, y.Channels[k].Sampler); Assert.Equal(x.Channels[k].Node, y.Channels[k].Node); Assert.Equal(x.Channels[k].Path, y.Channels[k].Path); }
        }
    }

    [Fact]
    public void The_full_fixture_survives_a_round_trip_field_by_field()
    {
        var original = GlbReader.Read(GlbReaderTests.FullGlb());
        var bytes = GlbWriter.Write(original);
        var again = GlbReader.Read(bytes);
        Same(original, again);
        Assert.StartsWith(GlbWriter.GeneratorTag, again.Generator);
        // an extension the fixture merely DECLARED (no payload anywhere) is not declared on the written file: nothing of it is written
        Assert.Equal(new[] { "KHR_texture_transform" }, original.ExtensionsUsed);
        Assert.Empty(again.ExtensionsUsed);
        // a material's extension payload IS carried, verbatim, and declared
        original.Materials[0].ExtensionsJson = "{\"KHR_materials_specular\":{\"specularTexture\":{\"index\":0},\"specularFactor\":0.5}}";
        var withExt = GlbReader.Read(GlbWriter.Write(original));
        Assert.Equal(original.Materials[0].ExtensionsJson, withExt.Materials[0].ExtensionsJson);
        Assert.Equal(new[] { "KHR_materials_specular" }, withExt.ExtensionsUsed);
        // and a second trip is byte-identical: the writer is deterministic
        Assert.Equal(bytes, GlbWriter.Write(again));
        // a valid container: magic, version 2, 4-byte aligned chunks, the declared length
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(bytes, 0)); Assert.Equal(2u, BitConverter.ToUInt32(bytes, 4)); Assert.Equal((uint)bytes.Length, BitConverter.ToUInt32(bytes, 8));
        Assert.Equal(0, bytes.Length % 4); Assert.Equal(0u, BitConverter.ToUInt32(bytes, 12) % 4u);
    }

    [Fact]
    public void Wide_indices_both_influence_sets_and_a_matrix_node_survive()
    {
        // 70,000 vertices need uint indices; the second influence set and a matrix node are written as given
        var m = new HafModel();
        int n = 70_000;
        var p = new HafPrimitive { VertexCount = n, Positions = new float[n * 3], Joints = new ushort[n * 4], Weights = new float[n * 4], Joints1 = new ushort[n * 4], Weights1 = new float[n * 4], Indices = new[] { 0, 1, 69_999 } };
        for (int i = 0; i < n; i++) { p.Positions[i * 3] = i; p.Weights[i * 4] = 0.5f; p.Weights1[i * 4] = 0.5f; p.Joints1[i * 4] = 1; }
        m.Meshes.Add(new HafMesh { Name = "big", Primitives = { p } });
        m.Nodes.Add(new HafNode { Name = "root", Matrix = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 5, 6, 7, 1 }, Mesh = 0, Skin = 0 });
        m.Nodes.Add(new HafNode { Name = "j0", Parent = 0 }); m.Nodes.Add(new HafNode { Name = "j1", Parent = 0 });
        m.Nodes[0].Children.AddRange(new[] { 1, 2 }); m.Roots.Add(0);
        m.Skins.Add(new HafSkin { Name = "rig", Joints = new[] { 1, 2 } });
        var again = GlbReader.Read(GlbWriter.Write(m));
        Same(m, again);
        Assert.Equal(69_999, again.Meshes[0].Primitives[0].Indices[2]);
    }

    [Theory]
    [InlineData("index-out-of-range", "index 9 is outside its 3 vertices")]
    [InlineData("joints-without-weights", "joints without weights")]
    [InlineData("missing-mesh", "references mesh 4, the model has 1")]
    [InlineData("image-without-bytes", "has no bytes")]
    [InlineData("sampler-mismatch", "a sampler has 3 keys but 3 values")]
    public void What_cannot_be_written_as_it_is_is_refused_by_name(string flaw, string message)
    {
        var m = GlbReader.Read(GlbReaderTests.FullGlb());
        switch (flaw)
        {
            case "index-out-of-range": m.Meshes[0].Primitives[0].Indices[1] = 9; break;
            case "joints-without-weights": m.Meshes[0].Primitives[0].Weights = null; break;
            case "missing-mesh": m.Nodes[0].Mesh = 4; break;
            case "image-without-bytes": m.Images[0].Bytes = null; break;
            case "sampler-mismatch": m.Animations[0].Samplers[0].Values = new float[] { 1, 2, 3 }; break;
        }
        var ex = Assert.Throws<InvalidDataException>(() => GlbWriter.Write(m));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void An_empty_model_is_a_valid_empty_file()
    {
        var again = GlbReader.Read(GlbWriter.Write(new HafModel()));
        Assert.Empty(again.Nodes); Assert.Empty(again.Meshes); Assert.Equal(0, again.TriangleCount);
    }
}
