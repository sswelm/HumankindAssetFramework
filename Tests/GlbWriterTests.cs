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
        Assert.Equal(a.Roots, b.Roots); Assert.Equal(a.Scene, b.Scene); Assert.Equal(a.Scenes.Count, b.Scenes.Count); Assert.Equal(a.Copyright, b.Copyright); Assert.Equal(a.AssetExtrasJson, b.AssetExtrasJson);
        for (int i = 0; i < a.Scenes.Count; i++) { Assert.Equal(a.Scenes[i].Name, b.Scenes[i].Name); Assert.Equal(a.Scenes[i].Nodes, b.Scenes[i].Nodes); Assert.Equal(a.Scenes[i].ExtrasJson, b.Scenes[i].ExtrasJson); }
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
            Assert.Equal(x.ExtensionsJson, y.ExtensionsJson); Assert.Equal(x.ExtrasJson, y.ExtrasJson);
        }
        for (int i = 0; i < a.Textures.Count; i++) { Assert.Equal(a.Textures[i].Name, b.Textures[i].Name); Assert.Equal(a.Textures[i].Source, b.Textures[i].Source); Assert.Equal(a.Textures[i].Sampler, b.Textures[i].Sampler); }
        Assert.Equal(a.Samplers, b.Samplers);
        for (int i = 0; i < a.Nodes.Count; i++) Assert.Equal(a.Nodes[i].ExtrasJson, b.Nodes[i].ExtrasJson);
        for (int i = 0; i < a.Meshes.Count; i++) Assert.Equal(a.Meshes[i].ExtrasJson, b.Meshes[i].ExtrasJson);
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
        original.Meshes[0].Primitives[0].MorphTargets = 0;   // the fixture declares one; the writer refuses it as it is (asserted below), so the round trip is taken without
        // what the second review found dropped: the asset's copyright and extras (Sketchfab attribution), the scene's name, an alphaCutoff outside MASK
        original.Copyright = "CC-BY 4.0 Someone"; original.AssetExtrasJson = "{\"author\":\"someone (https://sketchfab.com/someone)\",\"license\":\"CC-BY-4.0\",\"when\":\"2024-05-06T07:08:09.123+09:00\"}"; original.Scenes[0].Name = "Scene";
        // a second scene (sharing the root, with extras) and the default being the second: every scene survives, the default by index (review of PR #111)
        var second = new HafScene { Name = "Second", ExtrasJson = "{\"camera\":\"front\"}" }; second.Nodes.Add(0); original.Scenes.Add(second); original.Scene = 1;
        original.Materials[0].AlphaCutoff = 0.3f;
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
        // an extension used INSIDE the payload is declared too
        original.Materials[0].ExtensionsJson = "{\"KHR_materials_specular\":{\"specularTexture\":{\"index\":0,\"extensions\":{\"KHR_texture_transform\":{\"scale\":[2.0,2.0]}}}}}";
        Assert.Equal(new[] { "KHR_materials_specular", "KHR_texture_transform" }, GlbReader.Read(GlbWriter.Write(original)).ExtensionsUsed);
        // ... and one a SAMPLER carries (review round 6: the payload was written, the name was not)
        original.Samplers[0] = "{\"magFilter\":9729,\"extensions\":{\"EXT_probe\":{\"on\":true}}}";
        var withSamplerExt = GlbReader.Read(GlbWriter.Write(original));
        Assert.Equal(original.Samplers, withSamplerExt.Samplers);
        Assert.Equal(new[] { "EXT_probe", "KHR_materials_specular", "KHR_texture_transform" }, withSamplerExt.ExtensionsUsed);
        original.Samplers[0] = "{\"magFilter\":9729}";   // back to the fixture's sampler for the assertions below
        // the sampler's settings and every extras object are carried verbatim too (review of PR #110)
        Assert.Equal(new[] { "{\"magFilter\":9729}" }, original.Samplers);
        Assert.Equal(original.Samplers, withExt.Samplers);
        original.Nodes[0].ExtrasJson = "{\"author\":\"HAF\",\"tier\":2}"; original.Meshes[0].ExtrasJson = "{\"lod\":0}"; original.Materials[0].ExtrasJson = "{\"paint\":\"olive\"}";
        var withExtras = GlbReader.Read(GlbWriter.Write(original));
        Assert.Equal("{\"author\":\"HAF\",\"tier\":2}", withExtras.Nodes[0].ExtrasJson); Assert.Equal("{\"lod\":0}", withExtras.Meshes[0].ExtrasJson); Assert.Equal("{\"paint\":\"olive\"}", withExtras.Materials[0].ExtrasJson);
        // extras of ANY JSON type survive - the schema allows any, and an empty object is a value too (review round 4)
        original.Nodes[0].ExtrasJson = "[1,\"two\",null]"; original.Meshes[0].ExtrasJson = "\"a note\""; original.Materials[0].ExtrasJson = "3.5"; original.AssetExtrasJson = "{}";
        var anyType = GlbReader.Read(GlbWriter.Write(original));
        Assert.Equal("[1,\"two\",null]", anyType.Nodes[0].ExtrasJson); Assert.Equal("\"a note\"", anyType.Meshes[0].ExtrasJson); Assert.Equal("3.5", anyType.Materials[0].ExtrasJson); Assert.Equal("{}", anyType.AssetExtrasJson);
        Assert.Null(anyType.Nodes[1].ExtrasJson);   // absent stays absent
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
    [InlineData("morph-targets", "morph target(s), whose data the model does not carry")]
    [InlineData("nan-position", "not a finite number")]
    [InlineData("sampler-not-json", "sampler 0 is not a JSON object")]
    [InlineData("skeleton-out-of-range", "names skeleton node 99")]
    [InlineData("no-vertices", "has no vertices")]
    [InlineData("no-keys", "a sampler has no keys")]
    [InlineData("nan-time", "key times: a value is not a finite number")]
    [InlineData("nan-translation", "translation: a value is not a finite number")]
    [InlineData("short-factor", "baseColorFactor has 3 values, 4 expected")]
    [InlineData("unknown-image", "is of an unrecognised format; glTF embeds only PNG and JPEG")]
    [InlineData("webp-image", "is image/webp; glTF embeds only PNG and JPEG")]
    [InlineData("asset-extras-not-json", "asset extras is not JSON")]
    [InlineData("nan-normal", "NORMAL: a value is not a finite number")]
    [InlineData("nan-weight", "WEIGHTS_0: a value is not a finite number")]
    [InlineData("nan-ibm", "inverse bind matrices: a value is not a finite number")]
    [InlineData("nan-key-value", "key values: a value is not a finite number")]
    [InlineData("parent-stale", "node 1 'Root' has Parent -1 but node 0 lists it as a child")]
    [InlineData("child-of-two", "is a child of both node 0 and node 1")]
    [InlineData("root-is-child", "scene 0: root 1 'Root' is a child of node 0")]
    [InlineData("hierarchy-cycle", "is its own ancestor")]
    [InlineData("root-twice", "scene 0: root 0 is listed twice")]
    [InlineData("scene-node-out-of-range", "scene 0 '' lists node 7, the model has 3")]
    [InlineData("default-scene-out-of-range", "the default scene is 3, the model has 1")]
    [InlineData("ibm-too-big", "inverse bind matrices: 1E+100 does not fit a 32-bit float")]
    [InlineData("translation-too-big", "translation: -1E+39 does not fit a 32-bit float")]
    [InlineData("negative-first-key", "key times start at -0.5 - glTF starts them at or after 0")]
    [InlineData("repeated-key", "key times must strictly increase (key 1 is 0 after 0)")]
    public void What_cannot_be_written_as_it_is_is_refused_by_name(string flaw, string message)
    {
        var m = GlbReader.Read(GlbReaderTests.FullGlb());
        if (flaw != "morph-targets") m.Meshes[0].Primitives[0].MorphTargets = 0;   // every other flaw is tested on a primitive the writer would otherwise accept
        switch (flaw)
        {
            case "index-out-of-range": m.Meshes[0].Primitives[0].Indices[1] = 9; break;
            case "joints-without-weights": m.Meshes[0].Primitives[0].Weights = null; break;
            case "missing-mesh": m.Nodes[0].Mesh = 4; break;
            case "image-without-bytes": m.Images[0].Bytes = null; break;
            case "sampler-mismatch": m.Animations[0].Samplers[0].Values = new float[] { 1, 2, 3 }; break;
            case "morph-targets": break;   // the fixture's primitive declares one morph target: the writer refuses it as it is
            case "nan-position": m.Meshes[0].Primitives[0].Positions[4] = float.NaN; break;
            case "sampler-not-json": m.Samplers[0] = "[9729]"; break;
            case "skeleton-out-of-range": m.Skins[0].Skeleton = 99; break;
            case "no-vertices": m.Meshes[0].Primitives[0].VertexCount = 0; m.Meshes[0].Primitives[0].Positions = new float[0]; break;
            case "no-keys": m.Animations[0].Samplers[0].Times = new float[0]; m.Animations[0].Samplers[0].Values = new float[0]; break;
            case "nan-time": m.Animations[0].Samplers[0].Times[0] = float.NaN; break;
            case "nan-translation": m.Nodes[0].Matrix = null; m.Nodes[0].Translation[1] = double.PositiveInfinity; break;
            case "short-factor": m.Materials[0].BaseColorFactor = new float[] { 1, 1, 1 }; break;
            case "unknown-image": m.Images[0].MimeType = ""; m.Images[0].Bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }; break;
            case "webp-image": m.Images[0].MimeType = "image/webp"; break;
            case "asset-extras-not-json": m.AssetExtrasJson = "{not json"; break;
            case "nan-normal": m.Meshes[0].Primitives[0].Normals[2] = float.NaN; break;
            case "nan-weight": m.Meshes[0].Primitives[0].Weights[0] = float.NegativeInfinity; break;
            case "nan-ibm": m.Skins[0].InverseBindMatrices[5] = double.NaN; break;
            case "nan-key-value": m.Animations[0].Samplers[0].Values[1] = float.PositiveInfinity; break;
            case "parent-stale": m.Nodes[1].Parent = -1; break;                                  // node 0 still lists node 1
            case "child-of-two": m.Nodes[0].Children.Add(2); break;                              // node 2 is node 1's child already
            case "root-is-child": m.Roots.Add(1); break;
            case "hierarchy-cycle": m.Roots.Clear(); m.Nodes[2].Children.Add(0); m.Nodes[0].Parent = 2; break;   // 0 -> 1 -> 2 -> 0, every Parent consistent
            case "root-twice": m.Roots.Add(0); break;
            case "scene-node-out-of-range": m.Scenes[0].Nodes.Add(7); break;
            case "default-scene-out-of-range": m.Scene = 3; break;
            case "ibm-too-big": m.Skins[0].InverseBindMatrices[3] = 1e100; break;          // finite, and infinity once cast
            case "translation-too-big": m.Nodes[0].Translation[0] = -1e39; break;
            case "negative-first-key": m.Animations[0].Samplers[0].Times[0] = -0.5f; break;
            case "repeated-key": m.Animations[0].Samplers[0].Times[1] = m.Animations[0].Samplers[0].Times[0]; break;
        }
        var ex = Assert.Throws<InvalidDataException>(() => GlbWriter.Write(m));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void The_file_overload_lands_whole_and_replaces_an_existing_file_without_leaving_a_temporary()
    {
        string dir = Path.Combine(Path.GetTempPath(), "haf-glbwriter-" + Guid.NewGuid().ToString("N"));
        try
        {
            string target = Path.Combine(dir, "sub", "model.glb");   // the directory does not exist yet
            var m = GlbReader.Read(GlbReaderTests.FullGlb()); m.Meshes[0].Primitives[0].MorphTargets = 0;
            GlbWriter.Write(m, target);
            Assert.Equal(GlbWriter.Write(m), File.ReadAllBytes(target));
            File.WriteAllText(target, "stale");                     // an existing file is replaced, not appended to or left
            GlbWriter.Write(m, target);
            Assert.Equal(GlbWriter.Write(m), File.ReadAllBytes(target));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)).Where(f => !f.EndsWith("model.glb")));   // no .writing-* left behind
            // a model the writer refuses leaves the existing file untouched and no temporary beside it
            var good = File.ReadAllBytes(target);
            m.Meshes[0].Primitives[0].Positions[0] = float.NaN;
            Assert.Throws<InvalidDataException>(() => GlbWriter.Write(m, target));
            Assert.Equal(good, File.ReadAllBytes(target));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)));
            // a landing that fails (here: a directory stands where the file should go) leaves the directory as it was and no temporary beside it
            m.Meshes[0].Primitives[0].Positions[0] = 0;
            string blocked = Path.Combine(dir, "blocked.glb"); Directory.CreateDirectory(blocked);
            Assert.ThrowsAny<IOException>(() => GlbWriter.Write(m, blocked));
            Assert.Empty(Directory.GetFiles(dir));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)).Where(f => f.EndsWith(".tmp")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_empty_model_is_a_valid_empty_file()
    {
        var again = GlbReader.Read(GlbWriter.Write(new HafModel()));
        Assert.Empty(again.Nodes); Assert.Empty(again.Meshes); Assert.Equal(0, again.TriangleCount);
    }
}
