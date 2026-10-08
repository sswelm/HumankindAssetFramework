using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>BlenderPrep assembles the ports into the file prep_model.py writes. tools/prep_drill.sh holds that file to
/// Blender's through the Factory's converter; here: the shape of what is assembled, the reasons a file is left to
/// Blender, and the two material rules (the alpha that comes back out of Blender, specular-glossiness). The meshes
/// carry no normals and no colours: a test process has neither the 64-bit C runtime's cosf nor, on CI, the CPU's
/// rsqrtps table, and the reduce declines what it cannot do exactly.</summary>
public class BlenderPrepTests
{
    static HafNode Node(string name, int mesh = -1, int skin = -1, params int[] children) { var n = new HafNode { Name = name, Mesh = mesh, Skin = skin }; n.Children.AddRange(children); return n; }

    /// <summary>A bent 3x3 grid (18 triangles): faces a collapse has real costs for.</summary>
    static HafMesh Grid(string name, int material = -1)
    {
        var pos = new List<float>();
        for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) { pos.Add(i); pos.Add(0.2f * (float)Math.Sin(i + 0.7 * j)); pos.Add(-j); }
        var idx = new List<int>();
        for (int j = 0; j < 3; j++) for (int i = 0; i < 3; i++) { int a = j * 4 + i, b = a + 1, c = a + 4, d = c + 1; idx.AddRange(new[] { a, c, b, b, c, d }); }
        var me = new HafMesh { Name = name };
        me.Primitives.Add(new HafPrimitive { VertexCount = 16, Positions = pos.ToArray(), Indices = idx.ToArray(), Material = material });
        return me;
    }

    static HafModel Model(IEnumerable<HafMesh> meshes, IEnumerable<HafNode> nodes, params int[] roots)
    {
        var m = new HafModel(); m.Meshes.AddRange(meshes); m.Nodes.AddRange(nodes);
        for (int i = 0; i < m.Nodes.Count; i++) foreach (var c in m.Nodes[i].Children) m.Nodes[c].Parent = i;
        var sc = new HafScene(); sc.Nodes.AddRange(roots.Length > 0 ? roots : Enumerable.Range(0, m.Nodes.Count).Where(i => m.Nodes[i].Parent < 0)); m.Scenes.Add(sc); m.Scene = 0;
        return m;
    }

    [Fact]
    public void A_plain_file_is_assembled_in_the_exporters_order_and_reads_back_from_the_writer()
    {
        var nodes = new List<HafNode> { Node("Root", -1, -1, 1, 2), Node("Zed", 0), Node("Alpha", 1) };
        nodes[1].Translation = new double[] { 0, 0, 2 };
        var m = Model(new[] { Grid("z", 0), Grid("a", 1) }, nodes, 0);
        m.Materials.Add(new HafMaterial { Name = "first", DoubleSided = true, ExtrasJson = "{\"k\":1}" }); m.Materials.Add(new HafMaterial { Name = "second" });
        var r = BlenderPrep.Prepare(m, 1000);
        Assert.Null(r.Fallback);
        var o = r.Model;
        Assert.Equal(new[] { "Alpha", "Zed", "Root" }, o.Nodes.Select(n => n.Name));       // children by name, each before its parent
        Assert.Equal(new[] { 0, 1 }, o.Nodes.Take(2).Select(n => n.Mesh));                  // a mesh per node, in the order they are reached
        Assert.Equal(new[] { "a", "z" }, o.Meshes.Select(x => x.Name));
        Assert.Equal(new[] { "second", "first" }, o.Materials.Select(x => x.Name));         // materials at first use
        Assert.Equal(new[] { 0, 1 }, o.Meshes.Select(x => x.Primitives[0].Material));
        Assert.True(o.Materials[1].DoubleSided); Assert.Equal("{\"k\":1}", o.Materials[1].ExtrasJson);   // every field of the source material is carried
        Assert.Equal(new[] { 2 }, o.Scenes[0].Nodes);
        Assert.Equal(new double[] { 0, 0, 2 }, o.Nodes[1].Translation);
        Assert.Equal(36, r.SourceTriangles); Assert.Equal(36, r.Triangles);                 // the target is a ceiling
        var back = GlbReader.Read(GlbWriter.Write(o));
        Assert.Null(HafModelDiff.FirstDifference(o, back));
        // a budget under the total collapses
        var less = BlenderPrep.Prepare(m, 18);
        Assert.Null(less.Fallback);
        Assert.True(less.Triangles < 36 && less.Triangles > 0, "triangles " + less.Triangles);
    }

    [Fact]
    public void A_skinned_file_gets_one_skin_per_armature_with_its_joints_and_inverse_bind_matrices()
    {
        var nodes = new List<HafNode> { Node("Rig", -1, -1, 1, 2, 4), Node("Body", 0, 0), Node("Root", -1, -1, 3), Node("Tip"), Node("Second", 0, 0) };
        nodes[3].Translation = new double[] { 0, 1, 0 };
        var mesh = Grid("body");
        mesh.Primitives[0].Joints = new ushort[64]; mesh.Primitives[0].Weights = Enumerable.Range(0, 64).Select(i => i % 4 == 0 ? 1f : 0f).ToArray();
        var m = Model(new[] { mesh }, nodes, 0);
        m.Skins.Add(new HafSkin { Joints = new[] { 2, 3 } });
        var r = BlenderPrep.Prepare(m, 1000);
        Assert.Null(r.Fallback);
        var o = r.Model;
        Assert.Single(o.Skins);
        Assert.Equal(new[] { "Root", "Tip" }, o.Skins[0].Joints.Select(j => o.Nodes[j].Name));
        Assert.Equal(32, o.Skins[0].InverseBindMatrices.Length);
        var bodies = o.Nodes.Where(n => n.Mesh >= 0).ToList();
        Assert.Equal(2, bodies.Count); Assert.All(bodies, n => Assert.Equal(0, n.Skin));
        Assert.All(o.Meshes, me => Assert.NotNull(me.Primitives[0].Joints));
        Assert.Null(HafModelDiff.FirstDifference(o, GlbReader.Read(GlbWriter.Write(o))));
    }

    [Fact]
    public void A_file_this_cannot_prep_as_Blender_does_is_named_and_gets_no_model()
    {
        HafModel Plain() => Model(new[] { Grid("g") }, new List<HafNode> { Node("A", 0) });
        string Why(HafModel m, long target = 100) { var r = BlenderPrep.Prepare(m, target); Assert.Null(r.Model); Assert.NotNull(r.Fallback); return r.Fallback; }

        Assert.StartsWith("no reduce asked", Why(Plain(), 0));
        var scenes = Plain(); scenes.Scenes.Add(new HafScene());
        Assert.StartsWith("a file of several scenes", Why(scenes));
        var animated = Plain(); animated.Animations.Add(new HafAnimation());
        Assert.StartsWith("an animated file", Why(animated));
        Assert.StartsWith("no mesh to reduce", Why(Model(new HafMesh[0], new List<HafNode> { Node("Empty") })));
        // an object the reduce declines: a skin on a mesh without weights
        var noWeights = Model(new[] { Grid("g") }, new List<HafNode> { Node("Rig", -1, -1, 1, 2), Node("Body", 0, 0), Node("Joint") }, 0);
        noWeights.Skins.Add(new HafSkin { Joints = new[] { 2 } });
        Assert.Contains("a skin on a mesh without weights", Why(noWeights));
        // a shape the tree names: the child of a camera
        var camera = Model(new[] { Grid("g") }, new List<HafNode> { Node("Root", -1, -1, 1), Node("Cam", -1, -1, 2), Node("Kid", 0) }, 0);
        camera.Nodes[1].Camera = 0; camera.Cameras.Add("{}");
        Assert.StartsWith("camera-children:", Why(camera));
        // a JPEG whose alpha is read: Blender writes it again as PNG. The same image on an OPAQUE material, or at a MASK
        // cutoff that reads no alpha, passes through
        HafModel Jpeg(string mode, double cutoff, byte second)
        {
            var j = Model(new[] { Grid("g", 0) }, new List<HafNode> { Node("A", 0) });
            j.Meshes[0].Primitives[0].Uv0 = new float[32];
            j.Images.Add(new HafImage { Name = "i", MimeType = "image/jpeg", Bytes = new byte[] { 0xFF, second, 0xFF, 0xE0 } }); j.Textures.Add(new HafTexture { Source = 0 });
            j.Materials.Add(new HafMaterial { Name = "glass", AlphaMode = mode, AlphaCutoff = cutoff, BaseColorTexture = 0 });
            return j;
        }
        Assert.StartsWith("jpeg-alpha:", Why(Jpeg("BLEND", 0.5, 0xD8)));
        Assert.StartsWith("jpeg-alpha:", Why(Jpeg("MASK", 0.5, 0xD8)));
        Assert.Null(BlenderPrep.Prepare(Jpeg("OPAQUE", 0.5, 0xD8), 100).Fallback);
        Assert.Null(BlenderPrep.Prepare(Jpeg("MASK", 0.0, 0xD8), 100).Fallback);
        Assert.Null(BlenderPrep.Prepare(Jpeg("MASK", 1.5, 0xD8), 100).Fallback);
        Assert.StartsWith("image-format:", Why(Jpeg("BLEND", 0.5, 0x50)));          // not a JPEG's signature, nor a PNG's
        // prep_model.py removes every unskinned mesh object named Icosphere..., a real part too
        Assert.StartsWith("icosphere:", Why(Model(new[] { Grid("g"), Grid("h") }, new List<HafNode> { Node("Icosphere.001", 0), Node("Hull", 1) })));
        Assert.Null(BlenderPrep.Prepare(Model(new[] { Grid("g") }, new List<HafNode> { Node("Sphere", 0) }), 100).Fallback);
        // material variants: the importer keeps a slot per primitive
        var variants = Plain(); variants.ExtensionsUsed.Add("KHR_materials_variants");
        Assert.StartsWith("variants:", Why(variants));
        // a factor Blender clamps
        HafModel With(HafMaterial mat) { var w = Model(new[] { Grid("g", 0) }, new List<HafNode> { Node("A", 0) }); w.Materials.Add(mat); return w; }
        Assert.StartsWith("factor-range:", Why(With(new HafMaterial { Name = "hot", BaseColorFactor = new[] { 1.5, 1, 1, 1 } })));
        Assert.StartsWith("factor-range:", Why(With(new HafMaterial { Name = "neg", BaseColorFactor = new[] { 1, 1, 1, -0.1 } })));
        Assert.StartsWith("factor-range:", Why(With(new HafMaterial { Name = "metal", MetallicFactor = 2f })));
        Assert.StartsWith("factor-range:", Why(With(new HafMaterial { Name = "rough", RoughnessFactor = -1f })));
        // a base colour image the exporter does not pass through, or the writer cannot embed
        HafModel Textured(byte[] bytes, string uri)
        {
            var t = With(new HafMaterial { Name = "tex", BaseColorTexture = 0 });
            t.Meshes[0].Primitives[0].Uv0 = new float[32];
            t.Images.Add(new HafImage { Name = "i", Bytes = bytes, Uri = uri }); t.Textures.Add(new HafTexture { Source = 0 });
            return t;
        }
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D }, jpg = { 0xFF, 0xD8, 0xFF, 0xE0 }, webp = { 0x52, 0x49, 0x46, 0x46 };
        Assert.Null(BlenderPrep.Prepare(Textured(png, ""), 100).Fallback);
        Assert.Null(BlenderPrep.Prepare(Textured(png, "skin.PNG"), 100).Fallback);
        Assert.Null(BlenderPrep.Prepare(Textured(jpg, "skin.jpeg"), 100).Fallback);
        Assert.Null(BlenderPrep.Prepare(Textured(jpg, "data:image/png;base64,AAAA…"), 100).Fallback);   // embedded: the importer goes by the bytes
        Assert.StartsWith("image-format:", Why(Textured(webp, "")));
        Assert.StartsWith("image-uri:", Why(Textured(jpg, "skin.png")));
        Assert.StartsWith("image-uri:", Why(Textured(png, "skin.jpg")));
        var noImage = With(new HafMaterial { Name = "tex", BaseColorTexture = 0 }); noImage.Textures.Add(new HafTexture { Source = -1 });
        Assert.StartsWith("image-format:", Why(noImage));
        var unusedWebp = Plain(); unusedWebp.Images.Add(new HafImage { Name = "spare", Bytes = webp });
        Assert.StartsWith("image-format:", Why(unusedWebp));
        // an object under a bone
        var mesh = Grid("body"); mesh.Primitives[0].Joints = new ushort[64]; mesh.Primitives[0].Weights = Enumerable.Range(0, 64).Select(i => i % 4 == 0 ? 1f : 0f).ToArray();
        var underBone = Model(new[] { mesh, Grid("lamp") }, new List<HafNode> { Node("Rig", -1, -1, 1, 2), Node("Body", 0, 0), Node("Joint", -1, -1, 3), Node("Lamp", 1) }, 0);
        underBone.Skins.Add(new HafSkin { Joints = new[] { 2 } });
        Assert.StartsWith("an object under a bone", Why(underBone));
        // with `diagnose` the work goes on, the reason stays and there is still no model
        var d = BlenderPrep.Prepare(animated, 100, diagnose: true);
        Assert.Null(d.Model); Assert.StartsWith("an animated file", d.Fallback); Assert.Single(d.Objects); Assert.NotNull(d.Tree);
    }

    [Theory]
    // mode, cutoff, alpha, textured -> the alpha Blender writes back (measured: export_layout, export_flat_alpha)
    [InlineData("OPAQUE", 0.5, 0.5, false, 1.0)]
    [InlineData("MASK", 0.5, 0.3, false, 0.0)]      // a plain material: the importer sets the socket to 1 or 0 by alpha >= cutoff
    [InlineData("MASK", 0.3, 0.3, false, 1.0)]
    [InlineData("MASK", 0.0, 0.0, false, 1.0)]
    [InlineData("BLEND", 0.5, 0.4, false, 0.4000000059604645)]
    [InlineData("Blend", 0.5, 0.7, false, 0.699999988079071)]   // a mode the specification does not know blends
    [InlineData("OPAQUE", 0.5, 0.5, true, 1.0)]
    [InlineData("MASK", 0.0, 0.2, true, 1.0)]       // with a texture: a cutoff of 0 is opaque,
    [InlineData("MASK", 1.5, 1.0, true, 0.0)]       // over 1 discards everything,
    [InlineData("MASK", 0.5, 0.3, true, 0.30000001192092896)]   // and anything else multiplies by the factor
    [InlineData("BLEND", 0.5, 0.8, true, 0.800000011920929)]
    public void The_base_colour_factor_is_the_one_that_comes_back_out_of_Blender(string mode, double cutoff, double alpha, bool textured, double written)
    {
        var m = Model(new[] { Grid("g", 0) }, new List<HafNode> { Node("A", 0) });
        var mat = new HafMaterial { Name = "m", AlphaMode = mode, AlphaCutoff = cutoff, BaseColorFactor = new[] { 0.8, 0.9, 1.0, alpha } };
        if (textured)
        {
            m.Images.Add(new HafImage { Name = "i", MimeType = "image/png", Bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 } }); m.Textures.Add(new HafTexture { Source = 0 });
            mat.BaseColorTexture = 0; m.Meshes[0].Primitives[0].Uv0 = new float[32];
        }
        m.Materials.Add(mat);
        var r = BlenderPrep.Prepare(m, 1000);
        Assert.Null(r.Fallback);
        Assert.Equal(new[] { (double)0.8f, (double)0.9f, 1.0, written }, r.Model.Materials[0].BaseColorFactor);
        Assert.Equal(new[] { 0.8, 0.9, 1.0, alpha }, mat.BaseColorFactor);   // the source is not touched
    }

    [Theory]
    [InlineData("", "OPAQUE", 1.0)]          // the importer: `alpha_mode or 'OPAQUE'`
    [InlineData(null, "OPAQUE", 1.0)]
    [InlineData("Blend", "BLEND", 0.5)]      // anything but OPAQUE and MASK blends; the schema knows three values, and the converter's reader refuses a file with a fourth
    [InlineData("MASK", "MASK", 1.0)]
    public void The_written_alpha_mode_is_one_the_schema_knows(string mode, string written, double alpha)
    {
        var m = Model(new[] { Grid("g", 0) }, new List<HafNode> { Node("A", 0) });
        m.Materials.Add(new HafMaterial { Name = "m", AlphaMode = mode, BaseColorFactor = new[] { 1.0, 1.0, 1.0, 0.5 } });
        var r = BlenderPrep.Prepare(m, 1000);
        Assert.Null(r.Fallback);
        Assert.Equal(written, r.Model.Materials[0].AlphaMode);
        Assert.Equal(alpha, r.Model.Materials[0].BaseColorFactor[3]);
    }

    [Fact]
    public void An_unlit_material_is_written_with_metallic_0_and_roughness_0_9_whatever_the_source_says()
    {
        var m = Model(new[] { Grid("g", 0) }, new List<HafNode> { Node("A", 0) });
        m.Materials.Add(new HafMaterial { Name = "u", MetallicFactor = 1f, RoughnessFactor = 1f, ExtensionsJson = "{\"KHR_materials_unlit\":{}}" });
        var r = BlenderPrep.Prepare(m, 1000);
        Assert.Null(r.Fallback);
        Assert.Equal(0f, r.Model.Materials[0].MetallicFactor); Assert.Equal(0.9f, r.Model.Materials[0].RoughnessFactor);
        Assert.Equal("{\"KHR_materials_unlit\":{}}", r.Model.Materials[0].ExtensionsJson);
        Assert.Equal(1f, m.Materials[0].MetallicFactor);
    }

    [Fact]
    public void A_specular_glossiness_materials_diffuse_colour_and_texture_become_the_base_colour_and_the_extension_goes()
    {
        var m = Model(new[] { Grid("g", 0), Grid("h", 1) }, new List<HafNode> { Node("A", 0), Node("B", 1) });
        m.Meshes[0].Primitives[0].Uv0 = new float[32];
        m.Images.Add(new HafImage { Name = "i", MimeType = "image/png", Bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 } }); m.Textures.Add(new HafTexture { Source = 0 });
        m.Materials.Add(new HafMaterial { Name = "sg", ExtensionsJson = "{\"KHR_materials_pbrSpecularGlossiness\":{\"diffuseFactor\":[0.5,0.25,0.125,1.0],\"diffuseTexture\":{\"index\":0},\"glossinessFactor\":0.5},\"KHR_materials_unlit\":{}}" });
        m.Materials.Add(new HafMaterial { Name = "bare", ExtensionsJson = "{\"KHR_materials_pbrSpecularGlossiness\":{}}" });
        m.Meshes.Add(Grid("k", 2)); m.Nodes.Add(Node("C", 2)); m.Scenes[0].Nodes.Add(2); m.Meshes[2].Primitives[0].Uv0 = new float[32];
        m.Materials.Add(new HafMaterial { Name = "taken", ExtensionsJson = "{\"KHR_materials_pbrSpecularGlossiness\":{\"diffuseFactor\":[0.5,0.25,0.125,1.0],\"diffuseTexture\":{\"index\":0},\"glossinessFactor\":0.5}}" });
        m.ExtensionsUsed.Add("KHR_materials_pbrSpecularGlossiness"); m.ExtensionsRequired.Add("KHR_materials_pbrSpecularGlossiness"); m.ExtensionsUsed.Add("KHR_materials_unlit");
        var r = BlenderPrep.Prepare(m, 1000);
        Assert.Null(r.Fallback);
        var sg = r.Model.Materials.Single(x => x.Name == "sg"); var bare = r.Model.Materials.Single(x => x.Name == "bare");
        var taken = r.Model.Materials.Single(x => x.Name == "taken");
        Assert.Equal(new[] { 0.5, 0.25, 0.125, 1.0 }, taken.BaseColorFactor); Assert.Equal(0, taken.BaseColorTexture);
        Assert.Equal(0f, taken.MetallicFactor); Assert.Equal(0.5f, taken.RoughnessFactor); Assert.Null(taken.ExtensionsJson);
        Assert.Equal(new[] { 1.0, 1.0, 1.0, 1.0 }, sg.BaseColorFactor); Assert.Equal(-1, sg.BaseColorTexture);
        Assert.Contains("unlit", r.Notes);
        Assert.Equal("{\"KHR_materials_unlit\":{}}", sg.ExtensionsJson);
        Assert.Equal(new[] { 1.0, 1.0, 1.0, 1.0 }, bare.BaseColorFactor); Assert.Equal(-1, bare.BaseColorTexture); Assert.Null(bare.ExtensionsJson);
        // the importer's Principled: metallic 0, roughness 1 - glossiness (1 when the extension names none) - which also gives
        // the written material a pbrMetallicRoughness object, and the converter's swatch its white
        Assert.Equal(0f, bare.MetallicFactor); Assert.Equal(0f, bare.RoughnessFactor);
        // 'sg' is unlit as well: the importer takes the unlit path and never looks at the diffuse values - the core ones stay,
        // the exporter's unlit material has metallic 0 and roughness 0.9, and the extension still goes
        Assert.Equal(0f, sg.MetallicFactor); Assert.Equal(0.9f, sg.RoughnessFactor);
        Assert.Contains("KHR_materials_pbrSpecularGlossiness", m.Materials[0].ExtensionsJson);   // the source is not touched
        // the written file declares what it carries and requires nothing: Blender marks no material extension required
        var back = GlbReader.Read(GlbWriter.Write(r.Model));
        Assert.Equal(new[] { "KHR_materials_unlit" }, back.ExtensionsUsed); Assert.Empty(back.ExtensionsRequired);
        Assert.Contains("specular-glossiness", r.Notes);
    }
}
