using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

/// <summary>BlenderMaterialExport: a material as it comes back out of Blender (part 8a-ii). tools/deploy_drill.sh holds whole
/// files to the materials Blender writes; here each rule on a material small enough to read.</summary>
public class BlenderMaterialExportTests
{
    static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 4, 0, 0, 0, 2, 8, 6, 0, 0, 0, 1, 2, 3, 4 };
    static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xC0, 0, 17, 8, 0, 2, 0, 4, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xD9 };

    sealed class File
    {
        public readonly HafModel M = new HafModel();
        public int Image(string name, byte[] bytes) { M.Images.Add(new HafImage { Name = name, MimeType = "", Bytes = bytes }); return M.Images.Count - 1; }
        public int Texture(int image, int sampler = -1) { M.Textures.Add(new HafTexture { Source = image, Sampler = sampler }); return M.Textures.Count - 1; }
        public int Sampler(string json) { M.Samplers.Add(json); return M.Samplers.Count - 1; }
        public HafMaterial Material(string name, bool absent = false)
        {
            var mat = new HafMaterial { Name = name, NameAbsent = absent };
            M.Materials.Add(mat);
            // a triangle that uses it, so the walk lists the material
            var p = new HafPrimitive { VertexCount = 3, Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Material = M.Materials.Count - 1 };
            var mesh = new HafMesh { Name = "m" + M.Meshes.Count }; mesh.Primitives.Add(p); M.Meshes.Add(mesh);
            M.Nodes.Add(new HafNode { Name = "N" + M.Nodes.Count, Mesh = M.Meshes.Count - 1 });
            return mat;
        }
        public BlenderMaterialExport.Result Build()
        {
            if (M.Scenes.Count == 0) { var sc = new HafScene(); sc.Nodes.AddRange(Enumerable.Range(0, M.Nodes.Count)); M.Scenes.Add(sc); M.Scene = 0; }
            return BlenderMaterialExport.Build(M, BlenderNames.Compute(M));
        }
    }

    static string Json(JObject j) => MaterialDrillCanonical(j);
    /// <summary>The drill's comparison: both sides through one canonical form and one formatter (the runtime's double
    /// formatting is not Python's shortest repr; the drill never compares digits across formatters).</summary>
    static void AssertJson(string expected, JObject actual) => Assert.Equal(Json(JObject.Parse(expected)), Json(actual));
    static void AssertJson(string expected, JToken actual) => Assert.Equal(MaterialDrillCanonical(new JObject { ["v"] = JToken.Parse(expected) }), MaterialDrillCanonical(new JObject { ["v"] = actual }));
    static string MaterialDrillCanonical(JObject j)
    {
        // the drill's canonical form: integral numbers as integers, keys sorted
        JToken C(JToken t)
        {
            switch (t)
            {
                case JObject o: { var r = new JObject(); foreach (var p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)) r.Add(p.Name, C(p.Value)); return r; }
                case JArray a: return new JArray(a.Select(C));
                case JValue v when v.Type == JTokenType.Float: { double d = (double)v; return d == Math.Floor(d) ? new JValue((long)d) : new JValue(d); }
                default: return t.DeepClone();
            }
        }
        return C(j).ToString(Newtonsoft.Json.Formatting.None);
    }

    [Fact]
    public void Factors_come_back_as_float32_and_their_defaults_are_left_out()
    {
        var f = new File();
        var mat = f.Material("paint"); mat.BaseColorFactor = new double[] { 0.8, 0.1, 0.1, 1 }; mat.MetallicFactor = 1f; mat.RoughnessFactor = 0.3f; mat.DoubleSided = true;
        var all = f.Material("defaults", absent: true);
        var x = f.Build();
        Assert.Null(x.Fallback);
        AssertJson("{\"doubleSided\":true,\"name\":\"paint\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.800000011920929,0.10000000149011612,0.10000000149011612,1],\"roughnessFactor\":0.30000001192092896}}", (JObject)(x.Materials[0]));
        AssertJson("{\"name\":\"Material_1\"}", (JObject)(x.Materials[1]));   // metallic 1, roughness 1, white: nothing but the name; an empty pbr object is dropped
        Assert.Equal(1, x.Unnamed);
    }

    [Theory]
    [InlineData("OPAQUE", 0.5, 0.5, false, null, null, 1.0)]       // a flat material: a constant alpha, OPAQUE at 1
    [InlineData("MASK", 0.3, 0.3, false, null, null, 1.0)]         // alpha >= cutoff on the JSON doubles: 1, so OPAQUE
    [InlineData("MASK", 0.3, 0.25, false, null, null, 1.0)]        // over the cutoff too: 1, OPAQUE
    [InlineData("MASK", 0.2, 0.5, false, "MASK", null, 0.0)]       // below the cutoff: 0, MASK at the default cutoff
    [InlineData("BLEND", 0.4, 0.5, false, "BLEND", null, 0.4000000059604645)]
    [InlineData("BLEND", 0.99999999, 0.5, false, null, null, 1.0)]  // the factor rounds to 1 in float32: OPAQUE
    [InlineData("BLEND", 1, 0.5, true, "BLEND", null, 1.0)]        // with a texture the alpha is wired: BLEND, no factor node
    [InlineData("MASK", 1, 0.3, true, "MASK", 0.30000001192092896, 1.0)]
    [InlineData("MASK", 1, 0.5, true, "MASK", null, 1.0)]          // the default cutoff is left out
    [InlineData("MASK", 1, 0, true, null, null, 1.0)]              // a cutoff of 0 with a texture: the socket is a constant 1
    [InlineData("MASK", 1, 1.5, true, "MASK", null, 0.0)]          // over 1: a constant 0
    public void The_alpha_as_the_importer_wires_it_and_the_exporter_reads_it(string mode, double alpha, double cutoff, bool textured, string outMode, double? outCutoff, double outAlpha)
    {
        var f = new File();
        var mat = f.Material("m"); mat.AlphaMode = mode; mat.AlphaCutoff = cutoff; mat.BaseColorFactor = new[] { 1, 1, 1, alpha };
        if (textured) mat.BaseColorTexture = f.Texture(f.Image("tex", Png));
        var j = f.Build().Materials[0];
        Assert.Equal(outMode, (string)j["alphaMode"]);
        Assert.Equal(outCutoff, (double?)j["alphaCutoff"]);
        var bc = j["pbrMetallicRoughness"]?["baseColorFactor"];
        Assert.Equal(outAlpha, bc == null ? 1.0 : (double)bc[3]);
    }

    [Fact]
    public void A_jpeg_whose_alpha_is_read_is_left_to_Blender_and_an_opaque_one_passes_through()
    {
        var f = new File();
        var mat = f.Material("glass"); mat.AlphaMode = "BLEND"; mat.BaseColorFactor = new[] { 1, 1, 1, 0.5 }; mat.BaseColorTexture = f.Texture(f.Image("photo.jpg", Jpeg));
        Assert.Contains("a JPEG whose alpha is read", f.Build().Fallback);
        var g = new File();
        var opaque = g.Material("wall"); opaque.BaseColorTexture = g.Texture(g.Image("photo.jpg", Jpeg));
        var x = g.Build();
        Assert.Null(x.Fallback);
        Assert.Equal(("photo", "image/jpeg"), (x.Images[0].name, x.Images[0].mime));   // the name without its extension, the bytes' own type
        Assert.Same(Jpeg, x.Images[0].bytes);
    }

    [Theory]
    [InlineData(null, "{\"magFilter\":9729,\"minFilter\":9987}")]
    [InlineData("{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":10497,\"wrapT\":10497}", "{\"magFilter\":9729,\"minFilter\":9987}")]
    [InlineData("{\"magFilter\":9728,\"minFilter\":9728}", "{\"magFilter\":9728,\"minFilter\":9984}")]
    [InlineData("{\"minFilter\":9984}", "{\"magFilter\":9728,\"minFilter\":9984}")]                  // the only filter given is NEAREST (a mipmap variant)
    [InlineData("{\"magFilter\":9729,\"minFilter\":9728}", "{\"magFilter\":9729,\"minFilter\":9987}")]   // one of two NEAREST: linear
    [InlineData("{\"wrapS\":33071,\"wrapT\":33071}", "{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":33071,\"wrapT\":33071}")]
    [InlineData("{\"wrapS\":10497,\"wrapT\":33648}", "{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":10497,\"wrapT\":33648}")]   // mixed: both written, the REPEAT axis too
    public void A_sampler_as_the_importer_sets_the_node_and_the_exporter_reads_it(string source, string written)
    {
        var f = new File();
        var mat = f.Material("m"); mat.BaseColorTexture = f.Texture(f.Image("t", Png), source == null ? -1 : f.Sampler(source));
        var x = f.Build();
        Assert.Single(x.Samplers);
        AssertJson(written, x.Samplers[0]);
        Assert.Equal((0, 0), x.Textures[0]);
    }

    [Fact]
    public void Specular_glossiness_becomes_metallic_roughness_with_a_specular_colour_and_an_ior_of_1000()
    {
        var f = new File();
        var mat = f.Material("old"); mat.ExtensionsJson = "{\"KHR_materials_pbrSpecularGlossiness\":{\"diffuseFactor\":[0.8,0.8,0.8,1],\"specularFactor\":[0.2,0.2,0.2],\"glossinessFactor\":0.4}}";
        var x = f.Build();
        AssertJson("{\"extensions\":{\"KHR_materials_ior\":{\"ior\":1000},\"KHR_materials_specular\":{\"specularColorFactor\":[0.20000000298023224,0.20000000298023224,0.20000000298023224]}},\"name\":\"old\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.800000011920929,0.800000011920929,0.800000011920929,1],\"metallicFactor\":0,\"roughnessFactor\":0.6000000238418579}}", (JObject)(x.Materials[0]));
        Assert.Equal(1, x.SpecularGlossiness);
        // a white specular factor leaves the extension empty, and the ior goes with it; a glossiness of 1 is a roughness of 0 (written: 0 is not the default)
        var g = new File();
        g.Material("plain").ExtensionsJson = "{\"KHR_materials_pbrSpecularGlossiness\":{\"glossinessFactor\":1.0}}";
        AssertJson("{\"name\":\"plain\",\"pbrMetallicRoughness\":{\"metallicFactor\":0,\"roughnessFactor\":0}}", (JObject)(g.Build().Materials[0]));
    }

    [Fact]
    public void Specular_and_emission_fold_as_the_exporter_folds_them()
    {
        var f = new File();
        var mat = f.Material("glow"); mat.EmissiveFactor = new[] { 1f, 0.5f, 0f }; mat.EmissiveFactorJson = new[] { 1.0, 0.5, 0.0 }; mat.ExtensionsJson = "{\"KHR_materials_emissive_strength\":{\"emissiveStrength\":4.0},\"KHR_materials_specular\":{\"specularFactor\":0.5}}";
        var j = f.Build().Materials[0];
        AssertJson("[1,0.5,0]", j["emissiveFactor"]);
        Assert.Equal(4.0, (double)j["extensions"]["KHR_materials_emissive_strength"]["emissiveStrength"]);
        Assert.Equal(0.5, (double)j["extensions"]["KHR_materials_specular"]["specularFactor"]);
        // a grey emissive factor folds into the strength (and back): [0.3, 0.3, 0.3] in float32; a specular factor over 1 moves into the colour
        var g = new File();
        var grey = g.Material("grey"); grey.EmissiveFactor = new[] { 0.3f, 0.3f, 0.3f }; grey.EmissiveFactorJson = new[] { 0.3, 0.3, 0.3 }; grey.ExtensionsJson = "{\"KHR_materials_specular\":{\"specularFactor\":1.5,\"specularColorFactor\":[0.5,0.5,0.5]}}";
        var k = g.Build().Materials[0];
        AssertJson("[0.30000001192092896,0.30000001192092896,0.30000001192092896]", k["emissiveFactor"]);
        Assert.Null(k["extensions"]["KHR_materials_specular"]["specularFactor"]);
        AssertJson("[0.75,0.75,0.75]", k["extensions"]["KHR_materials_specular"]["specularColorFactor"]);
    }

    [Fact]
    public void An_unlit_material_keeps_its_extension_with_the_exporters_own_constants()
    {
        var f = new File();
        var mat = f.Material("flat"); mat.ExtensionsJson = "{\"KHR_materials_unlit\":{}}"; mat.BaseColorFactor = new double[] { 1.5, 0.5, 0.5, 1 }; mat.MetallicFactor = 0.7f;
        AssertJson("{\"extensions\":{\"KHR_materials_unlit\":{}},\"name\":\"flat\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[1.5,0.5,0.5,1],\"metallicFactor\":0,\"roughnessFactor\":0.9}}", (JObject)(f.Build().Materials[0]));
    }

    [Fact]
    public void Images_are_shared_by_bytes_type_and_name_and_a_texture_is_made_per_slot()
    {
        var f = new File();
        int img = f.Image("shared", Png);
        var a = f.Material("a"); a.BaseColorTexture = f.Texture(img); a.NormalTexture = f.Texture(img); a.NormalScale = 0.8f;
        var b = f.Material("b"); b.BaseColorTexture = f.Texture(f.Image("shared", Png));   // another source image, the same bytes and name: one written image
        var x = f.Build();
        Assert.Equal(3, x.Textures.Count); Assert.Single(x.Images);
        Assert.Equal(0.800000011920929, (double)x.Materials[0]["normalTexture"]["scale"]);
        Assert.Equal("my.texture", BlenderMaterialExport.SplitExtForTest("my.texture.v2"));
        Assert.Equal(".hidden", BlenderMaterialExport.SplitExtForTest(".hidden"));
    }
    [Fact]
    public void Unreferenced_meshes_cannot_choose_a_materials_UV_layer()
    {
        var f = new File(); var mat = f.Material("m");
        mat.BaseColorTexture = f.Texture(f.Image("t", Png)); mat.BaseColorTexCoord = 1;
        f.M.Meshes[0].Primitives[0].Uv0 = new float[6];
        var unused = new HafMesh();
        unused.Primitives.Add(new HafPrimitive { Material = 0, Uv0 = new float[6], Uv1 = new float[6] });
        f.M.Meshes.Add(unused);
        var x = f.Build(); Assert.Null(x.Fallback);
        Assert.Null(x.Materials[0]["pbrMetallicRoughness"]["baseColorTexture"]["texCoord"]);
    }

    [Fact]
    public void Unlit_ignored_slots_do_not_reserve_image_names()
    {
        var f = new File(); var mat = f.Material("m");
        mat.ExtensionsJson = "{\"KHR_materials_unlit\":{}}";
        mat.EmissiveTexture = f.Texture(f.Image("shared.png", Png));
        mat.BaseColorTexture = f.Texture(f.Image("shared.png", Png));
        var x = f.Build(); Assert.Null(x.Fallback);
        Assert.Equal("shared", Assert.Single(x.Images).name);
    }

    [Fact]
    public void An_ORM_image_is_shared_even_when_the_slots_read_different_UV_layers()
    {
        var f = new File(); var mat = f.Material("m");
        int image = f.Image("orm.png", Png);
        mat.OcclusionTexture = f.Texture(image, f.Sampler("{\"magFilter\":9728,\"minFilter\":9728}"));
        mat.MetallicRoughnessTexture = f.Texture(image); mat.MetallicRoughnessTexCoord = 1;
        f.M.Meshes[0].Primitives[0].Uv0 = new float[6]; f.M.Meshes[0].Primitives[0].Uv1 = new float[6];
        var x = f.Build(); Assert.Null(x.Fallback);
        Assert.Single(x.Textures);
        Assert.Equal(9984, (int)x.Samplers[0]["minFilter"]);
        Assert.Null(x.Materials[0]["occlusionTexture"]["texCoord"]);
        Assert.Equal(1, (int)x.Materials[0]["pbrMetallicRoughness"]["metallicRoughnessTexture"]["texCoord"]);
    }

    [Fact]
    public void Vertex_colour_variants_resolve_their_own_UV_layers()
    {
        var f = new File(); var mat = f.Material("paint");
        mat.BaseColorTexture = f.Texture(f.Image("t", Png)); mat.BaseColorTexCoord = 1;
        f.M.Meshes[0].Primitives[0].Uv0 = new float[6];
        f.Material("unused");
        var coloured = f.M.Meshes[1].Primitives[0]; coloured.Material = 0;
        coloured.Colors = new float[12]; coloured.Uv0 = new float[6]; coloured.Uv1 = new float[6];
        var x = f.Build(); Assert.Null(x.Fallback); Assert.Equal(2, x.Materials.Count);
        Assert.Null(x.Materials[0]["pbrMetallicRoughness"]["baseColorTexture"]["texCoord"]);
        Assert.Equal(1, (int)x.Materials[1]["pbrMetallicRoughness"]["baseColorTexture"]["texCoord"]);
    }

}
