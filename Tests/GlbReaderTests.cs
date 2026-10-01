using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

// THE GLB READER (2026-09-30, step 1 of replacing Blender). A synthetic file with every attribute the model holds - two
// nodes, a skinned mesh with normals, tangents, two UV sets, colours, joints and weights, an interleaved buffer view, a
// skin with inverse bind matrices, a material with textures, an embedded PNG, an animation with LINEAR, STEP and
// CUBICSPLINE samplers - is written by the test and read back field by field. Then every refusal, by message: sparse
// accessors, Draco, a chunk past the file, an index outside its vertices, an accessor past its view, two parents.
public class GlbReaderTests
{
    // ---- a tiny GLB writer for fixtures: accessors appended to one BIN chunk, layout under the test's control ----
    sealed class Fixture
    {
        public readonly JObject Root = new JObject { ["asset"] = new JObject { ["version"] = "2.0", ["generator"] = "GlbReaderTests" } };
        public readonly MemoryStream Bin = new MemoryStream();
        public JArray Accessors => Arr("accessors"); public JArray Views => Arr("bufferViews");
        JArray Arr(string k) { if (Root[k] == null) Root[k] = new JArray(); return (JArray)Root[k]; }
        void Pad() { while (Bin.Length % 4 != 0) Bin.WriteByte(0); }

        public int View(byte[] bytes, int stride = 0)
        {
            Pad();
            var v = new JObject { ["buffer"] = 0, ["byteOffset"] = (int)Bin.Length, ["byteLength"] = bytes.Length };
            if (stride > 0) v["byteStride"] = stride;
            Bin.Write(bytes, 0, bytes.Length);
            Views.Add(v); return Views.Count - 1;
        }
        public int Accessor(int view, int byteOffset, int componentType, string type, int count, bool normalized = false, JObject extra = null)
        {
            var a = new JObject { ["bufferView"] = view, ["byteOffset"] = byteOffset, ["componentType"] = componentType, ["type"] = type, ["count"] = count };
            if (normalized) a["normalized"] = true;
            if (extra != null) foreach (var p in extra) a[p.Key] = p.Value;
            Accessors.Add(a); return Accessors.Count - 1;
        }
        public int FloatAccessor(float[] data, string type, int components)
        {
            var b = new byte[data.Length * 4]; Buffer.BlockCopy(data, 0, b, 0, b.Length);
            return Accessor(View(b), 0, 5126, type, data.Length / components);
        }
        public byte[] Glb()
        {
            Root["buffers"] = new JArray { new JObject { ["byteLength"] = (int)Bin.Length } };
            var json = Encoding.UTF8.GetBytes(Root.ToString(Newtonsoft.Json.Formatting.None));
            var jsonPad = (4 - json.Length % 4) % 4;
            Pad();
            var bin = Bin.ToArray();
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                int total = 12 + 8 + json.Length + jsonPad + 8 + bin.Length;
                w.Write(0x46546C67u); w.Write(2u); w.Write((uint)total);
                w.Write((uint)(json.Length + jsonPad)); w.Write(0x4E4F534Au); w.Write(json); for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
                w.Write((uint)bin.Length); w.Write(0x004E4942u); w.Write(bin);
                return ms.ToArray();
            }
        }
    }

    static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };   // not a real image; the reader carries bytes, it does not decode them

    /// <summary>The full fixture: everything the model holds, in one file - the writer's round-trip subject too.</summary>
    internal static byte[] FullGlb() => Full().Glb();
    static Fixture Full()
    {
        var f = new Fixture();
        // an INTERLEAVED view: position (3 floats) + normal (3 floats) per vertex, stride 24 - the layout Blender's exporter does not use, and the reader must
        var inter = new byte[3 * 24];
        float[] pos = { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, nrm = { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
        for (int v = 0; v < 3; v++) { Buffer.BlockCopy(pos, v * 12, inter, v * 24, 12); Buffer.BlockCopy(nrm, v * 12, inter, v * 24 + 12, 12); }
        int interView = f.View(inter, 24);
        int posAcc = f.Accessor(interView, 0, 5126, "VEC3", 3, extra: new JObject { ["min"] = new JArray { 0, 0, 0 }, ["max"] = new JArray { 1, 1, 0 } });
        int nrmAcc = f.Accessor(interView, 12, 5126, "VEC3", 3);
        int tanAcc = f.FloatAccessor(new float[] { 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, -1 }, "VEC4", 4);
        int uv0Acc = f.FloatAccessor(new float[] { 0, 0, 1, 0, 0, 1 }, "VEC2", 2);
        // TEXCOORD_1 as NORMALIZED unsigned shorts (KHR_mesh_quantization-style, but plain glTF allows it for texcoords)
        int uv1Acc = f.Accessor(f.View(new byte[] { 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0, 0xFF, 0xFF }), 0, 5123, "VEC2", 3, normalized: true);
        // COLOR_0 as VEC3 normalized unsigned bytes: the model pads alpha to 1
        int colAcc = f.Accessor(f.View(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0 }), 0, 5121, "VEC3", 3, normalized: true);
        int jntAcc = f.Accessor(f.View(new byte[] { 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 }), 0, 5121, "VEC4", 3);
        // WEIGHTS_0 as normalized unsigned bytes: 0.5/0.5, 1/0, 1/0 (255 -> 1.0)
        int wgtAcc = f.Accessor(f.View(new byte[] { 128, 127, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0 }), 0, 5121, "VEC4", 3, normalized: true);
        int idxAcc = f.Accessor(f.View(new byte[] { 0, 0, 1, 0, 2, 0 }), 0, 5123, "SCALAR", 3);
        // inverse bind matrices: two identity MAT4s
        var ibm = new float[32]; ibm[0] = ibm[5] = ibm[10] = ibm[15] = 1; ibm[16] = ibm[21] = ibm[26] = ibm[31] = 1;
        int ibmAcc = f.FloatAccessor(ibm, "MAT4", 16);
        // animation: 3 keys; translation LINEAR, rotation STEP, scale CUBICSPLINE (3 values per key)
        int tAcc = f.FloatAccessor(new float[] { 0f, 0.5f, 1f }, "SCALAR", 1);
        int trAcc = f.FloatAccessor(new float[] { 0, 0, 0, 1, 0, 0, 2, 0, 0 }, "VEC3", 3);
        int rotAcc = f.FloatAccessor(new float[] { 0, 0, 0, 1, 0, 0.7071f, 0, 0.7071f, 0, 1, 0, 0 }, "VEC4", 4);
        int scAcc = f.FloatAccessor(Enumerable.Range(0, 27).Select(i => (float)i).ToArray(), "VEC3", 3);
        int imgView = f.View(Png);

        f.Root["images"] = new JArray { new JObject { ["name"] = "atlas", ["mimeType"] = "image/png", ["bufferView"] = imgView } };
        f.Root["samplers"] = new JArray { new JObject { ["magFilter"] = 9729 } };
        f.Root["textures"] = new JArray { new JObject { ["source"] = 0, ["sampler"] = 0, ["name"] = "atlasTex" } };
        f.Root["materials"] = new JArray { new JObject
        {
            ["name"] = "Hull", ["doubleSided"] = true, ["alphaMode"] = "MASK", ["alphaCutoff"] = 0.25,
            ["pbrMetallicRoughness"] = new JObject { ["baseColorFactor"] = new JArray { 0.5, 0.25, 1, 1 }, ["baseColorTexture"] = new JObject { ["index"] = 0, ["texCoord"] = 1 }, ["metallicFactor"] = 0, ["roughnessFactor"] = 0.8 },
            ["normalTexture"] = new JObject { ["index"] = 0, ["scale"] = 0.5 }, ["emissiveFactor"] = new JArray { 1, 0, 0 },
        } };
        f.Root["meshes"] = new JArray { new JObject { ["name"] = "Tank", ["primitives"] = new JArray { new JObject
        {
            ["attributes"] = new JObject { ["POSITION"] = posAcc, ["NORMAL"] = nrmAcc, ["TANGENT"] = tanAcc, ["TEXCOORD_0"] = uv0Acc, ["TEXCOORD_1"] = uv1Acc, ["COLOR_0"] = colAcc, ["JOINTS_0"] = jntAcc, ["WEIGHTS_0"] = wgtAcc },
            ["indices"] = idxAcc, ["material"] = 0, ["targets"] = new JArray { new JObject { ["POSITION"] = posAcc } },
        } } } };
        f.Root["skins"] = new JArray { new JObject { ["name"] = "Rig", ["joints"] = new JArray { 1, 2 }, ["inverseBindMatrices"] = ibmAcc, ["skeleton"] = 1 } };
        f.Root["nodes"] = new JArray
        {
            new JObject { ["name"] = "Body", ["mesh"] = 0, ["skin"] = 0, ["translation"] = new JArray { 1, 2, 3 }, ["children"] = new JArray { 1 } },
            new JObject { ["name"] = "Root", ["rotation"] = new JArray { 0, 0, 0.7071, 0.7071 }, ["scale"] = new JArray { 2, 2, 2 }, ["children"] = new JArray { 2 } },
            new JObject { ["name"] = "Turret", ["matrix"] = new JArray { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 5, 6, 7, 1 } },
        };
        f.Root["scenes"] = new JArray { new JObject { ["nodes"] = new JArray { 0 } } }; f.Root["scene"] = 0;
        f.Root["animations"] = new JArray { new JObject
        {
            ["name"] = "Spin",
            ["samplers"] = new JArray
            {
                new JObject { ["input"] = tAcc, ["output"] = trAcc },                                        // LINEAR by default
                new JObject { ["input"] = tAcc, ["output"] = rotAcc, ["interpolation"] = "STEP" },
                new JObject { ["input"] = tAcc, ["output"] = scAcc, ["interpolation"] = "CUBICSPLINE" },
            },
            ["channels"] = new JArray
            {
                new JObject { ["sampler"] = 0, ["target"] = new JObject { ["node"] = 2, ["path"] = "translation" } },
                new JObject { ["sampler"] = 1, ["target"] = new JObject { ["node"] = 2, ["path"] = "rotation" } },
                new JObject { ["sampler"] = 2, ["target"] = new JObject { ["node"] = 1, ["path"] = "scale" } },
            },
        } };
        f.Root["extensionsUsed"] = new JArray { "KHR_texture_transform" };
        return f;
    }

    [Fact]
    public void Every_field_of_the_full_fixture_reads_back()
    {
        var m = GlbReader.Read(Full().Glb());
        Assert.Equal("GlbReaderTests", m.Generator);
        Assert.Equal(new[] { "KHR_texture_transform" }, m.ExtensionsUsed);

        // nodes: transforms as given, the hierarchy both ways, the scene's roots
        Assert.Equal(3, m.Nodes.Count);
        Assert.Equal("Body", m.Nodes[0].Name); Assert.Equal(new double[] { 1, 2, 3 }, m.Nodes[0].Translation); Assert.Equal(0, m.Nodes[0].Mesh); Assert.Equal(0, m.Nodes[0].Skin);
        Assert.Equal(new double[] { 0, 0, 0.7071, 0.7071 }, m.Nodes[1].Rotation); Assert.Equal(new double[] { 2, 2, 2 }, m.Nodes[1].Scale); Assert.False(m.Nodes[1].HasMatrix);
        Assert.True(m.Nodes[2].HasMatrix); Assert.Equal(5, m.Nodes[2].Matrix[12]); Assert.Equal(7, m.Nodes[2].Matrix[14]);
        Assert.Equal(-1, m.Nodes[0].Parent); Assert.Equal(0, m.Nodes[1].Parent); Assert.Equal(1, m.Nodes[2].Parent);
        Assert.Equal(new[] { 1 }, m.Nodes[0].Children); Assert.Equal(new[] { 0 }, m.Roots); Assert.Single(m.Scenes); Assert.Equal(0, m.Scene);

        // the primitive: every attribute, the interleaved view decoded through its stride, normalized ints scaled
        var p = Assert.Single(Assert.Single(m.Meshes).Primitives);
        Assert.Equal("Tank", m.Meshes[0].Name);
        Assert.Equal(3, p.VertexCount); Assert.Equal(1, p.TriangleCount); Assert.Equal(0, p.Material); Assert.Equal(4, p.Mode);
        Assert.Equal(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, p.Positions);
        Assert.Equal(new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, p.Normals);
        Assert.Equal(-1f, p.Tangents[11]);
        Assert.Equal(new float[] { 0, 0, 1, 0, 0, 1 }, p.Uv0);
        Assert.Equal(new float[] { 0, 0, 1, 0, 0, 1 }, p.Uv1);                                   // 0xFFFF normalized = 1
        Assert.Equal(new float[] { 1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1 }, p.Colors);               // RGB padded with alpha 1
        Assert.Equal(new ushort[] { 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, p.Joints);
        Assert.Equal(128 / 255f, p.Weights[0], 5); Assert.Equal(127 / 255f, p.Weights[1], 5); Assert.Equal(1f, p.Weights[4]);
        Assert.Equal(new[] { 0, 1, 2 }, p.Indices);
        Assert.True(p.Skinned); Assert.Equal(1, p.MorphTargets);
        Assert.Equal(1, m.TriangleCount); Assert.Equal(3, m.VertexCount);

        // material, texture, image
        var mat = Assert.Single(m.Materials);
        Assert.Equal("Hull", mat.Name); Assert.True(mat.DoubleSided); Assert.Equal("MASK", mat.AlphaMode); Assert.Equal(0.25f, mat.AlphaCutoff);
        Assert.Equal(new float[] { 0.5f, 0.25f, 1, 1 }, mat.BaseColorFactor); Assert.Equal(0, mat.BaseColorTexture); Assert.Equal(1, mat.BaseColorTexCoord);
        Assert.Equal(0f, mat.MetallicFactor); Assert.Equal(0.8f, mat.RoughnessFactor); Assert.Equal(-1, mat.MetallicRoughnessTexture);
        Assert.Equal(0, mat.NormalTexture); Assert.Equal(0.5f, mat.NormalScale); Assert.Equal(new float[] { 1, 0, 0 }, mat.EmissiveFactor);
        Assert.Equal(0, Assert.Single(m.Textures).Source); Assert.Equal("atlasTex", m.Textures[0].Name);
        var img = Assert.Single(m.Images); Assert.Equal("image/png", img.MimeType); Assert.Equal(Png, img.Bytes);

        // skin
        var skin = Assert.Single(m.Skins);
        Assert.Equal("Rig", skin.Name); Assert.Equal(new[] { 1, 2 }, skin.Joints); Assert.Equal(1, skin.Skeleton);
        Assert.Equal(32, skin.InverseBindMatrices.Length); Assert.Equal(1, skin.InverseBindMatrices[0]); Assert.Equal(0, skin.InverseBindMatrices[1]); Assert.Equal(1, skin.InverseBindMatrices[31]);

        // animation: three samplers of three interpolations, the channels, the duration
        var anim = Assert.Single(m.Animations);
        Assert.Equal("Spin", anim.Name); Assert.Equal(1.0, anim.Duration, 6);
        Assert.Equal(3, anim.Samplers.Count); Assert.Equal(3, anim.Channels.Count);
        Assert.Equal("LINEAR", anim.Samplers[0].Interpolation); Assert.Equal(3, anim.Samplers[0].Components); Assert.Equal(new float[] { 0, 0.5f, 1 }, anim.Samplers[0].Times); Assert.Equal(9, anim.Samplers[0].Values.Length);
        Assert.Equal("STEP", anim.Samplers[1].Interpolation); Assert.Equal(4, anim.Samplers[1].Components); Assert.Equal(12, anim.Samplers[1].Values.Length);
        Assert.Equal("CUBICSPLINE", anim.Samplers[2].Interpolation); Assert.Equal(27, anim.Samplers[2].Values.Length);   // 3 keys × 3 (in, value, out) × VEC3
        Assert.Equal(2, anim.Channels[0].Node); Assert.Equal("translation", anim.Channels[0].Path); Assert.Equal("rotation", anim.Channels[1].Path); Assert.Equal(1, anim.Channels[2].Node);
    }

    [Fact]
    public void A_gltf_beside_its_bin_and_a_data_uri_image_read_the_same()
    {
        string d = Path.Combine(Path.GetTempPath(), "haf_gltf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        try
        {
            var f = Full();
            var bin = f.Bin.ToArray();
            f.Root["buffers"] = new JArray { new JObject { ["byteLength"] = bin.Length, ["uri"] = "model%20data.bin" } };   // an escaped uri, as exporters write
            File.WriteAllBytes(Path.Combine(d, "model data.bin"), bin);
            ((JObject)f.Root["images"][0]).Remove("bufferView");
            f.Root["images"][0]["uri"] = "data:image/png;base64," + Convert.ToBase64String(Png);
            File.WriteAllText(Path.Combine(d, "model.gltf"), f.Root.ToString());
            var m = GlbReader.Read(Path.Combine(d, "model.gltf"));
            Assert.Equal(1, m.TriangleCount); Assert.Equal(Png, m.Images[0].Bytes); Assert.Equal("image/png", m.Images[0].MimeType); Assert.StartsWith("data:image/png", m.Images[0].Uri);
            Assert.Equal(Path.Combine(d, "model.gltf"), m.SourcePath);
            // and a missing .bin is a refusal by name, not an empty model
            File.Delete(Path.Combine(d, "model data.bin"));
            var ex = Assert.Throws<InvalidDataException>(() => GlbReader.Read(Path.Combine(d, "model.gltf")));
            Assert.Contains("could not be found beside the file", ex.Message);
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Primitives_over_one_accessor_share_one_array_and_the_writer_writes_it_once()
    {
        // a Workshop split writes every part over its parent's vertex accessor: a 398 MB Lab source has 2,133 primitives over
        // 162 accessors. Decoded once per primitive that was 125 M vertices (4 GB); shared, it is the file's 8.2 M.
        var f = new Fixture();
        int pos = f.FloatAccessor(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 }, "VEC3", 3);
        var ib = new byte[12]; Buffer.BlockCopy(new ushort[] { 0, 1, 2, 1, 3, 2 }, 0, ib, 0, 12);
        int view = f.View(ib);
        int i0 = f.Accessor(view, 0, 5123, "SCALAR", 3), i1 = f.Accessor(view, 6, 5123, "SCALAR", 3);
        f.Root["meshes"] = new JArray { new JObject { ["primitives"] = new JArray { new JObject { ["attributes"] = new JObject { ["POSITION"] = pos }, ["indices"] = i0 }, new JObject { ["attributes"] = new JObject { ["POSITION"] = pos }, ["indices"] = i1 } } } };
        f.Root["nodes"] = new JArray { new JObject { ["mesh"] = 0 } };
        var m = GlbReader.Read(f.Glb());
        var p0 = m.Meshes[0].Primitives[0]; var p1 = m.Meshes[0].Primitives[1];
        Assert.Same(p0.Positions, p1.Positions); Assert.NotSame(p0.Indices, p1.Indices);
        var written = GlbWriter.Write(m);
        var json = JObject.Parse(Encoding.UTF8.GetString(written, 20, BitConverter.ToInt32(written, 12)));
        var prims = (JArray)json["meshes"][0]["primitives"];
        Assert.Equal(prims[0]["attributes"]["POSITION"].Value<int>(), prims[1]["attributes"]["POSITION"].Value<int>());   // one accessor, as in the source
        Assert.NotEqual(prims[0]["indices"].Value<int>(), prims[1]["indices"].Value<int>());
        Assert.Equal(3, ((JArray)json["accessors"]).Count);
        var back = GlbReader.Read(written);
        Assert.Null(HafModelDiff.FirstDifference(m, back));
        Assert.Same(back.Meshes[0].Primitives[0].Positions, back.Meshes[0].Primitives[1].Positions);
    }

    [Fact]
    public void A_required_extension_that_lives_in_a_material_is_read_carried_and_written_back_as_required()
    {
        // a Lab source requires KHR_materials_pbrSpecularGlossiness: its geometry is core glTF, its colours are in the payload
        var f = new Fixture();
        int pos = f.FloatAccessor(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, "VEC3", 3);
        f.Root["materials"] = new JArray { new JObject { ["name"] = "old", ["extensions"] = new JObject { ["KHR_materials_pbrSpecularGlossiness"] = new JObject { ["diffuseFactor"] = new JArray { 1, 0, 0, 1 } } } } };
        f.Root["meshes"] = new JArray { new JObject { ["primitives"] = new JArray { new JObject { ["attributes"] = new JObject { ["POSITION"] = pos }, ["material"] = 0 } } } };
        f.Root["nodes"] = new JArray { new JObject { ["mesh"] = 0 } };
        f.Root["extensionsUsed"] = new JArray { "KHR_materials_pbrSpecularGlossiness" }; f.Root["extensionsRequired"] = new JArray { "KHR_materials_pbrSpecularGlossiness" };
        var m = GlbReader.Read(f.Glb());
        Assert.Equal(new[] { "KHR_materials_pbrSpecularGlossiness" }, m.ExtensionsRequired);
        Assert.Contains("diffuseFactor", m.Materials[0].ExtensionsJson);
        var back = GlbReader.Read(GlbWriter.Write(m));
        Assert.Equal(new[] { "KHR_materials_pbrSpecularGlossiness" }, back.ExtensionsRequired);
        Assert.Null(HafModelDiff.FirstDifference(m, back));
        // once no material carries the payload any more, it is no longer required
        m.Materials[0].ExtensionsJson = null;
        Assert.Empty(GlbReader.Read(GlbWriter.Write(m)).ExtensionsRequired);
        // an extension that is NOT a material's payload is still refused, by name
        f.Root["extensionsRequired"] = new JArray { "KHR_materials_variants" };
        Assert.Contains("requires the extension 'KHR_materials_variants'", Assert.Throws<InvalidDataException>(() => GlbReader.Read(f.Glb())).Message);
    }

    [Fact]
    public void Defaults_apply_when_the_file_is_minimal()
    {
        var f = new Fixture();
        int pos = f.FloatAccessor(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 }, "VEC3", 3);
        f.Root["meshes"] = new JArray { new JObject { ["primitives"] = new JArray { new JObject { ["attributes"] = new JObject { ["POSITION"] = pos }, ["mode"] = 5 } } } };   // a non-indexed TRIANGLE_STRIP
        f.Root["nodes"] = new JArray { new JObject { ["mesh"] = 0 }, new JObject() };   // no scene: every parentless node is a root
        var m = GlbReader.Read(f.Glb());
        var p = m.Meshes[0].Primitives[0];
        Assert.Null(p.Indices); Assert.Null(p.Normals); Assert.Null(p.Uv0); Assert.False(p.Skinned); Assert.Equal(4, p.VertexCount); Assert.Equal(2, p.TriangleCount);
        Assert.Equal(new double[] { 0, 0, 0 }, m.Nodes[0].Translation); Assert.Equal(new double[] { 0, 0, 0, 1 }, m.Nodes[0].Rotation); Assert.Equal(new double[] { 1, 1, 1 }, m.Nodes[0].Scale);
        Assert.Empty(m.Roots); Assert.Empty(m.Scenes); Assert.Equal(-1, m.Scene); Assert.Equal("", m.Nodes[1].Name);   // no scene declared: none invented, no default, nothing a viewer shows
        Assert.Empty(m.Materials); Assert.Empty(m.Skins); Assert.Empty(m.Animations);
    }

    [Theory]
    [InlineData("sparse", "is sparse")]
    [InlineData("draco", "requires the extension 'KHR_draco_mesh_compression'")]
    [InlineData("index-out-of-range", "outside its 3 vertices")]
    [InlineData("accessor-past-view", "reaches past its bufferView")]
    [InlineData("two-parents", "has two parents")]
    [InlineData("bad-component-type", "component type 9999")]
    [InlineData("normal-count", "NORMAL has 6 values, 9 expected")]
    [InlineData("keys-vs-values", "3 keys but 2 values")]
    [InlineData("times-descending", "not ascending")]
    [InlineData("chunk-past-file", "runs past the file")]
    [InlineData("not-glb", "does not start with the glTF magic")]
    [InlineData("cycle", "is its own ancestor")]
    [InlineData("required-texture-transform", "requires the extension 'KHR_texture_transform'")]
    [InlineData("joints-2", "more than eight influences")]
    [InlineData("joints-1-alone", "without the other")]
    public void What_the_reader_does_not_implement_or_cannot_trust_is_refused_by_name(string flaw, string message)
    {
        var f = Full();
        byte[] glb = null;
        switch (flaw)
        {
            case "sparse": ((JObject)f.Accessors[0])["sparse"] = new JObject { ["count"] = 1 }; break;
            case "draco": f.Root["extensionsRequired"] = new JArray { "KHR_draco_mesh_compression" }; break;
            case "index-out-of-range": f.Root["meshes"][0]["primitives"][0]["indices"] = f.Accessor(f.View(new byte[] { 0, 0, 1, 0, 7, 0 }), 0, 5123, "SCALAR", 3); break;
            case "accessor-past-view": ((JObject)f.Accessors[0])["count"] = 4; break;       // the interleaved view holds 3
            case "two-parents": ((JArray)f.Root["nodes"][0]["children"]).Add(2); break;      // Turret is already Root's child
            case "bad-component-type": ((JObject)f.Accessors[0])["componentType"] = 9999; break;
            case "normal-count": f.Root["meshes"][0]["primitives"][0]["attributes"]["NORMAL"] = f.FloatAccessor(new float[] { 0, 0, 1, 0, 0, 1 }, "VEC3", 3); break;
            case "keys-vs-values": f.Root["animations"][0]["samplers"][0]["output"] = f.FloatAccessor(new float[] { 0, 0, 0, 1, 0, 0 }, "VEC3", 3); break;
            case "times-descending": f.Root["animations"][0]["samplers"][0]["input"] = f.FloatAccessor(new float[] { 0, 1, 0.5f }, "SCALAR", 1); break;
            case "chunk-past-file": glb = f.Glb(); Buffer.BlockCopy(BitConverter.GetBytes(0x7FFFFFF0u), 0, glb, 12, 4); break;   // the JSON chunk claims 2 GB
            case "not-glb": glb = Encoding.UTF8.GetBytes("{ \"asset\": { \"version\": \"2.0\" } }"); break;
            case "cycle": f.Root["nodes"][2]["children"] = new JArray { 1 }; ((JArray)f.Root["nodes"][0]["children"]).Clear(); break;   // Root -> Turret -> Root, each with one parent
            case "required-texture-transform": f.Root["extensionsRequired"] = new JArray { "KHR_texture_transform" }; break;
            case "joints-2": f.Root["meshes"][0]["primitives"][0]["attributes"]["JOINTS_2"] = f.Root["meshes"][0]["primitives"][0]["attributes"]["JOINTS_0"]; break;
            case "joints-1-alone": f.Root["meshes"][0]["primitives"][0]["attributes"]["JOINTS_1"] = f.Root["meshes"][0]["primitives"][0]["attributes"]["JOINTS_0"]; break;
        }
        var ex = Assert.Throws<InvalidDataException>(() => GlbReader.Read(glb ?? f.Glb()));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void A_second_influence_set_is_read_and_a_missing_image_is_refused()
    {
        var f = Full();
        var attrs = (JObject)f.Root["meshes"][0]["primitives"][0]["attributes"];
        attrs["JOINTS_1"] = f.Accessor(f.View(new byte[] { 1, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0 }), 0, 5121, "VEC4", 3);
        attrs["WEIGHTS_1"] = f.FloatAccessor(new float[] { 0.25f, 0, 0, 0, 0.5f, 0.5f, 0, 0, 0, 0, 0, 0 }, "VEC4", 4);
        var p = GlbReader.Read(f.Glb()).Meshes[0].Primitives[0];
        Assert.Equal(new ushort[] { 1, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0 }, p.Joints1);
        Assert.Equal(0.25f, p.Weights1[0]); Assert.Equal(0.5f, p.Weights1[5]);
        // a .gltf whose image file is not beside it is not read whole
        string d = Path.Combine(Path.GetTempPath(), "haf_img_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        try
        {
            var g = Full();
            var bin = g.Bin.ToArray();
            g.Root["buffers"] = new JArray { new JObject { ["byteLength"] = bin.Length, ["uri"] = "m.bin" } };
            File.WriteAllBytes(Path.Combine(d, "m.bin"), bin);
            ((JObject)g.Root["images"][0]).Remove("bufferView"); g.Root["images"][0]["uri"] = "atlas.png";   // never written
            File.WriteAllText(Path.Combine(d, "m.gltf"), g.Root.ToString());
            var ex = Assert.Throws<InvalidDataException>(() => GlbReader.Read(Path.Combine(d, "m.gltf")));
            Assert.Contains("'atlas.png' could not be found beside the file", ex.Message);
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void A_zero_filled_accessor_without_a_buffer_view_reads_as_zeros()
    {
        var f = Full();
        f.Accessors.Add(new JObject { ["componentType"] = 5126, ["type"] = "VEC2", ["count"] = 3 });   // the spec allows it: all zeros
        f.Root["meshes"][0]["primitives"][0]["attributes"]["TEXCOORD_0"] = f.Accessors.Count - 1;
        var m = GlbReader.Read(f.Glb());
        Assert.Equal(new float[6], m.Meshes[0].Primitives[0].Uv0);
    }
}
