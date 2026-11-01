using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

// Sub-step 6b of the native FBX reader: editor/FbxMaterials.cs and editor/PngCodec.cs on documents built in memory in the
// shapes of tools/fbx-drill/fbx_material_fixtures.py. The expected values are Blender 5.1's own (its FBX import + glTF
// export of those scenes, read back through GlbReader, 2026-11-02); the claim on every file is tools/fbx_model_drill.sh's.
// The texture tests write files under the temp directory and use Windows paths (the port is ntpath, like the drills).
public class FbxMaterialsTests : IDisposable
{
    // ---- builders -------------------------------------------------------------------------------------------------
    static FbxNode N(string name, params object[] props) { var n = new FbxNode { Name = name }; n.Properties.AddRange(props); return n; }
    static FbxNode With(FbxNode n, params FbxNode[] children) { n.Children.AddRange(children); return n; }
    static FbxNode P(string name, string kind, string sub, params object[] values) { var p = N("P", name, kind, sub, ""); p.Properties.AddRange(values); return p; }
    static FbxNode Color(string name, double r, double g, double b) => P(name, "Color", "", r, g, b);
    static FbxNode Number(string name, double v) => P(name, "Number", "", v);
    static FbxNode Props70(params FbxNode[] ps) => With(N("Properties70"), ps);
    static FbxNode Object(string cls, long id, string name, string sub, params FbxNode[] children) => With(N(cls, id, name + "\0\u0001" + cls, sub), children);
    static FbxNode C(long src, long dst) => N("C", "OO", src, dst);
    static FbxNode Link(long src, long dst, string prop) => N("C", "OP", src, dst, prop);
    static FbxNode Material(long id, string name, params FbxNode[] props) => props.Length > 0 ? Object("Material", id, name, "", N("ShadingModel", "phong"), Props70(props)) : Object("Material", id, name, "", N("ShadingModel", "phong"));
    static FbxNode Texture(long id, string name, string relative, params FbxNode[] props)
    {
        var children = new List<FbxNode> { N("Type", "TextureVideoClip") };
        if (props.Length > 0) children.Add(Props70(props));
        if (relative != null) children.Add(N("RelativeFilename", relative));
        return Object("Texture", id, name, "", children.ToArray());
    }
    static FbxNode Video(long id, string name, string relative, byte[] content = null)
    {
        var children = new List<FbxNode> { N("Type", "Clip"), N("RelativeFilename", relative) };
        if (content != null) children.Add(N("Content", new FbxRaw(content)));
        return Object("Video", id, name, "", children.ToArray());
    }
    static readonly double[] QuadV = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 };
    static FbxNode Quad(long id) => Object("Geometry", id, "Geo" + id, "Mesh", N("Vertices", QuadV), N("PolygonVertexIndex", new[] { 0, 1, 2, ~3 }), N("GeometryVersion", 124));
    static List<FbxNode> Header(FbxNode materialTemplate = null)
    {
        var settings = Props70(P("UpAxis", "int", "Integer", 1), P("UpAxisSign", "int", "Integer", 1), P("FrontAxis", "int", "Integer", 2), P("FrontAxisSign", "int", "Integer", 1),
            P("CoordAxis", "int", "Integer", 0), P("CoordAxisSign", "int", "Integer", 1), P("UnitScaleFactor", "double", "Number", 1.0));
        var template = With(N("PropertyTemplate", "FbxNode"), Props70(P("Lcl Translation", "Lcl Translation", "", 0.0, 0.0, 0.0), P("Lcl Rotation", "Lcl Rotation", "", 0.0, 0.0, 0.0),
            P("Lcl Scaling", "Lcl Scaling", "", 1.0, 1.0, 1.0), P("RotationActive", "bool", "", 0), P("RotationOrder", "enum", "", 0)));
        var defs = With(N("Definitions"), With(N("ObjectType", "Model"), template));
        if (materialTemplate != null) defs.Children.Add(With(N("ObjectType", "Material"), With(N("PropertyTemplate", "FbxSurfacePhong"), materialTemplate)));
        return new List<FbxNode> { With(N("FBXHeaderExtension"), N("FBXHeaderVersion", 1003), N("FBXVersion", 7400), N("Creator", "tests")),
            With(N("GlobalSettings"), N("Version", 1000), settings), defs };
    }
    /// <summary>One quad object per material (ids 1.., geometries 30..) plus the extra objects and links; the model Blender would write.</summary>
    HafModel Model(FbxNode[] materials, FbxNode[] extras, FbxNode[] links, out string why, out HashSet<int> composed, FbxNode materialTemplate = null)
    {
        var objects = new List<FbxNode>(materials); objects.AddRange(extras);
        var conns = new List<FbxNode>(links);
        for (int i = 0; i < materials.Length; i++)
        {
            long mat = (long)materials[i].Properties[0];
            objects.Add(Object("Model", 1 + i, "Obj" + i, "Mesh")); objects.Add(Quad(30 + i));
            conns.Add(C(1 + i, 0)); conns.Add(C(30 + i, 1 + i)); conns.Add(C(mat, 1 + i));
        }
        var d = new FbxDocument { Version = 7400 };
        d.Nodes.AddRange(Header(materialTemplate));
        d.Nodes.Add(With(N("Objects"), objects.ToArray())); d.Nodes.Add(With(N("Connections"), conns.ToArray()));
        var scene = FbxImport.Build(d, dir);
        return FbxModel.Build(scene, out why, out composed);
    }
    HafModel Model(FbxNode[] materials, FbxNode[] extras, FbxNode[] links) { var m = Model(materials, extras, links, out string why, out _); Assert.Null(why); return m; }

    // ---- image files beside the scene ----------------------------------------------------------------------------
    readonly string dir = Path.Combine(Path.GetTempPath(), "haf-fbxmat-" + Guid.NewGuid().ToString("N"));
    public FbxMaterialsTests() { Directory.CreateDirectory(Path.Combine(dir, "tex")); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }
    const int W = 4, H = 4;
    static byte[] Rgba(int seed, bool alpha) { var p = new byte[W * H * 4]; for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = 4 * (y * W + x); p[i] = (byte)(17 * x + 3 + seed); p[i + 1] = (byte)(40 * y + 9); p[i + 2] = (byte)((77 * (x + y) + seed) % 256); p[i + 3] = alpha ? (byte)(255 - 60 * y - 7 * x) : (byte)255; } return p; }
    static byte[] Gray(int seed) { var p = new byte[W * H * 4]; for (int i = 0; i < W * H; i++) { byte g = (byte)((13 * i + 5 + seed) % 256); p[4 * i] = p[4 * i + 1] = p[4 * i + 2] = g; p[4 * i + 3] = 255; } return p; }
    string Write(string name, byte[] png) { string p = Path.Combine(dir, "tex", name); File.WriteAllBytes(p, png); return p; }
    static string Bits(double d) => BitConverter.ToUInt64(BitConverter.GetBytes(d), 0).ToString("x16");
    static byte[] Pixels(HafModel m, int image, out PngCodec.Info info) { var px = PngCodec.Decode(m.Images[image].Bytes, out info, out string why); Assert.Null(why); return px; }

    static byte[] PngChunk(string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type); uint crc = 0xffffffff;
        foreach (byte v in t.Concat(data)) { crc ^= v; for (int b = 0; b < 8; b++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1; }
        crc ^= 0xffffffff;
        return new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length }
            .Concat(t).Concat(data).Concat(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }).ToArray();
    }
    static byte[] RawRgbPng(int width, int height, byte[] scanlines)
    {
        var header = new[] { (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, (byte)8, (byte)2, (byte)0, (byte)0, (byte)0 };
        var z = new MemoryStream(); z.WriteByte(0x78); z.WriteByte(0x9c);
        using (var ds = new System.IO.Compression.DeflateStream(z, System.IO.Compression.CompressionMode.Compress, true)) ds.Write(scanlines, 0, scanlines.Length);
        uint a = 1, b = 0; foreach (byte v in scanlines) { a = (a + v) % 65521; b = (b + a) % 65521; } uint ad = (b << 16) | a;
        z.Write(new[] { (byte)(ad >> 24), (byte)(ad >> 16), (byte)(ad >> 8), (byte)ad }, 0, 4);
        return new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.Concat(PngChunk("IHDR", header)).Concat(PngChunk("IDAT", z.ToArray())).Concat(PngChunk("IEND", new byte[0])).ToArray();
    }

    // ---- PngCodec -------------------------------------------------------------------------------------------------
    [Fact]
    public void Png_round_trips_rgba_and_rgb_through_every_filter_of_a_foreign_encoder()
    {
        var px = Rgba(11, true);
        var rgba = PngCodec.Decode(PngCodec.Encode(W, H, px, true), out var info, out string why);
        Assert.Null(why); Assert.Equal(6, info.ColorType); Assert.Equal(px, rgba);
        var rgb = PngCodec.Decode(PngCodec.Encode(W, H, px, false), out info, out why);
        Assert.Null(why); Assert.Equal(2, info.ColorType); Assert.False(info.HasAlpha);
        for (int i = 0; i < px.Length; i++) Assert.Equal(i % 4 == 3 ? (byte)255 : px[i], rgb[i]);
        // a palette with tRNS, grey + alpha, a 2-bit grey: the expansions libpng makes for OpenImageIO
        byte[] Png(byte ct, byte bd, byte[] rows, byte[] plte = null, byte[] trns = null)
        {
            var ms = new MemoryStream(); void Chunk(string t, byte[] d) { var chunk = PngChunk(t, d); ms.Write(chunk, 0, chunk.Length); }
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            Chunk("IHDR", new byte[] { 0, 0, 0, 2, 0, 0, 0, 1, bd, ct, 0, 0, 0 });
            if (plte != null) Chunk("PLTE", plte); if (trns != null) Chunk("tRNS", trns);
            var z = new MemoryStream(); z.WriteByte(0x78); z.WriteByte(0x9C); using (var ds = new System.IO.Compression.DeflateStream(z, System.IO.Compression.CompressionMode.Compress, true)) ds.Write(rows, 0, rows.Length);
            uint a = 1, b = 0; foreach (byte v in rows) { a = (a + v) % 65521; b = (b + a) % 65521; } uint ad = (b << 16) | a; z.Write(new[] { (byte)(ad >> 24), (byte)(ad >> 16), (byte)(ad >> 8), (byte)ad }, 0, 4);
            Chunk("IDAT", z.ToArray()); Chunk("IEND", new byte[0]); return ms.ToArray();
        }
        var pal = PngCodec.Decode(Png(3, 8, new byte[] { 0, 1, 0 }, new byte[] { 10, 20, 30, 40, 50, 60 }, new byte[] { 255, 128 }), out info, out why);
        Assert.Null(why); Assert.Equal(4, info.Channels); Assert.Equal(new byte[] { 40, 50, 60, 128, 10, 20, 30, 255 }, pal);
        var ga = PngCodec.Decode(Png(4, 8, new byte[] { 0, 70, 200, 90, 10 }), out info, out why);
        Assert.Null(why); Assert.Equal(2, info.Channels); Assert.Equal(new byte[] { 70, 70, 70, 200, 90, 90, 90, 10 }, ga);
        var g2 = PngCodec.Decode(Png(0, 2, new byte[] { 0, 0b11_01_0000 }), out info, out why);
        Assert.Null(why); Assert.Equal(new byte[] { 255, 255, 255, 255, 85, 85, 85, 255 }, g2);
        Assert.Null(PngCodec.Decode(Png(0, 16, new byte[] { 0, 1, 2, 3, 4 }), out info, out why)); Assert.Contains("16-bit", why);
        // the filters: a 2x1 RGB image, each row written with Sub (1), Up (2), Average (3) and Paeth (4) by hand
        var sub = PngCodec.Decode(Png(2, 8, new byte[] { 1, 10, 20, 30, 5, 5, 5 }), out info, out why);   // Sub: second pixel = first + (5, 5, 5)
        Assert.Null(why); Assert.Equal(new byte[] { 10, 20, 30, 255, 15, 25, 35, 255 }, sub);
        var up = PngCodec.Decode(Png(2, 8, new byte[] { 2, 10, 20, 30, 40, 50, 60 }), out info, out why);    // Up with no row above: unchanged
        Assert.Null(why); Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, up);
        var avg = PngCodec.Decode(Png(2, 8, new byte[] { 3, 10, 20, 30, 1, 2, 3 }), out info, out why);     // Average of left and (absent) above: left / 2
        Assert.Null(why); Assert.Equal(new byte[] { 10, 20, 30, 255, 6, 12, 18, 255 }, avg);
        var paeth = PngCodec.Decode(Png(2, 8, new byte[] { 4, 10, 20, 30, 1, 2, 3 }), out info, out why);   // Paeth with no row above predicts the left pixel
        Assert.Null(why); Assert.Equal(new byte[] { 10, 20, 30, 255, 11, 22, 33, 255 }, paeth);
    }

    [Fact]
    public void Blenders_byte_to_float_to_byte_round_trip_is_the_identity()
    {
        for (int b = 0; b < 256; b++) Assert.Equal((byte)b, FbxMaterials.UnitFloatToUchar((float)(b * (1.0f / 255.0f))));
        Assert.Equal(255, FbxMaterials.UnitFloatToUchar(1f)); Assert.Equal(0, FbxMaterials.UnitFloatToUchar(-0.1f));
    }

    // ---- the material properties ----------------------------------------------------------------------------------
    [Fact]
    public void Factors_are_the_wrappers_float32_sockets_as_the_exporter_writes_them()
    {
        var m = Model(new[] {
            Material(100, "Defaults"),
            Material(101, "Factors", Color("DiffuseColor", 0.2, 0.4, 0.6), Number("SpecularFactor", 0.5), Number("Shininess", 100.0), Number("ReflectionFactor", 0.3), Number("BumpFactor", 2.0)),
            Material(102, "Clamped", Color("DiffuseColor", 1.5, -0.2, 0.5), Number("SpecularFactor", 0.1), Number("Shininess", -5.0), Number("ReflectionFactor", 1.7)),
            Material(103, "Masked", Number("TransparencyFactor", 1.0), Number("Opacity", 0.0)),
            Material(104, "Specular0", Number("SpecularFactor", 0.0)) }, new FbxNode[0], new FbxNode[0]);
        var d = m.Materials[0];
        Assert.Equal(new double[] { 1, 1, 1, 1 }, d.BaseColorFactor); Assert.Equal(0f, d.MetallicFactor); Assert.Equal(Bits(0.5527864098548889), Bits(d.RoughnessFactor));
        Assert.True(d.DoubleSided); Assert.Equal("OPAQUE", d.AlphaMode); Assert.Null(d.ExtensionsJson);
        var f = m.Materials[1];
        Assert.Equal(Bits((double)(float)0.2) + Bits((double)(float)0.4) + Bits((double)(float)0.6), Bits(f.BaseColorFactor[0]) + Bits(f.BaseColorFactor[1]) + Bits(f.BaseColorFactor[2]));
        Assert.Equal(0f, f.RoughnessFactor); Assert.Equal(0.3f, f.MetallicFactor);
        Assert.Equal("{\"KHR_materials_specular\":{\"specularColorFactor\":[2.0,2.0,2.0]}}", f.ExtensionsJson);
        var c = m.Materials[2];
        Assert.Equal(new double[] { 1, 0, 0.5, 1 }, c.BaseColorFactor); Assert.Equal(1f, c.RoughnessFactor); Assert.Equal(1f, c.MetallicFactor);
        Assert.Equal(Bits((double)(float)0.2 * 2.0), Bits((double)Newtonsoft.Json.Linq.JObject.Parse(c.ExtensionsJson)["KHR_materials_specular"]["specularFactor"]));
        var k = m.Materials[3];
        Assert.Equal("MASK", k.AlphaMode); Assert.Equal(0.0, k.BaseColorFactor[3]); Assert.Equal(0.5, k.AlphaCutoff);
        Assert.Equal("{\"KHR_materials_specular\":{\"specularFactor\":0.0}}", m.Materials[4].ExtensionsJson);
        Assert.Equal(new[] { "KHR_materials_specular" }, m.ExtensionsUsed);
    }

    [Fact]
    public void Alpha_comes_from_the_transparency_factor_then_the_own_opacity_then_the_transparent_colour()
    {
        var m = Model(new[] {
            Material(100, "Blend", Number("TransparencyFactor", 0.3)),
            Material(101, "Opacity", Number("TransparencyFactor", 1.0), Number("Opacity", 0.25)),
            Material(102, "TransparentColour", Number("TransparencyFactor", 0.0), Color("TransparentColor", 0.6, 0.1, 0.1)),
            Material(103, "TemplateOpacity", Number("TransparencyFactor", 1.0)) },
            new FbxNode[0], new FbxNode[0], out string why, out _, Props70(Number("Opacity", 0.0), Number("Shininess", 50.0)));
        Assert.Null(why);
        Assert.Equal("BLEND", m.Materials[0].AlphaMode); Assert.Equal(Bits((double)(float)0.7), Bits(m.Materials[0].BaseColorFactor[3]));
        Assert.Equal("BLEND", m.Materials[1].AlphaMode); Assert.Equal(0.25, m.Materials[1].BaseColorFactor[3]);
        Assert.Equal("BLEND", m.Materials[2].AlphaMode); Assert.Equal(Bits((double)(float)0.4), Bits(m.Materials[2].BaseColorFactor[3]));
        // the template's Opacity is not the material's own: alpha falls to the default TransparentColor (black) and stays 1
        Assert.Equal("OPAQUE", m.Materials[3].AlphaMode);
        // the template's Shininess reaches every material: 1 - sqrt(50) / 10
        Assert.Equal(Bits(0.2928932309150696), Bits(m.Materials[3].RoughnessFactor));
    }

    [Fact]
    public void Emission_is_colour_times_strength_with_the_maximum_moved_into_the_strength_extension()
    {
        var m = Model(new[] {
            Material(100, "Emissive", Color("EmissiveColor", 0.5, 0.25, 0.0), Number("EmissiveFactor", 4.0)),
            Material(101, "EmissiveGrey", Color("EmissiveColor", 1.0, 1.0, 1.0), Number("EmissiveFactor", 0.5)),
            Material(102, "EmissiveBlack", Color("EmissiveColor", 0.0, 0.0, 0.0), Number("EmissiveFactor", 3.0)),
            Material(103, "Spec", Number("SpecularFactor", 0.5)) }, new FbxNode[0], new FbxNode[0]);
        Assert.Equal(new[] { 1.0, 0.5, 0.0 }, m.Materials[0].EmissiveFactorJson);
        Assert.Equal("{\"KHR_materials_emissive_strength\":{\"emissiveStrength\":2.0}}", m.Materials[0].ExtensionsJson);
        Assert.Equal(new[] { 0.5, 0.5, 0.5 }, m.Materials[1].EmissiveFactorJson); Assert.Null(m.Materials[1].ExtensionsJson);
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, m.Materials[2].EmissiveFactorJson);
        // extensionsUsed in the exporter's fixed order, whatever the materials' order
        Assert.Equal(new[] { "KHR_materials_emissive_strength", "KHR_materials_specular" }, m.ExtensionsUsed);
    }

    [Fact]
    public void A_user_flagged_property_is_skipped_and_a_wrong_type_stops_the_import()
    {
        var flagged = N("P", "DiffuseColor", "Color", "", "AU", 0.1, 0.1, 0.1);
        var m = Model(new[] { Object("Material", 100, "UserFlag", "", N("ShadingModel", "phong"), Props70(flagged, P("DiffuseColor", "ColorRGB", "Color", 0.9, 0.3, 0.3))) }, new FbxNode[0], new FbxNode[0]);
        Assert.Equal(Bits((double)(float)0.9), Bits(m.Materials[0].BaseColorFactor[0]));
        Assert.Null(Model(new[] { Material(100, "Bad", P("DiffuseColor", "Vector3D", "Vector", 0.5, 0.5, 0.5)) }, new FbxNode[0], new FbxNode[0], out string why, out _));
        Assert.Contains("asserts", why);
        Assert.Null(Model(new[] { Material(100, "BadEnum") }, new[] { Texture(60, "t", "tex\\rgb.png", N("P", "WrapModeU", "enum", "", "A", 1)) }, new[] { Link(60, 100, "DiffuseColor") }, out why, out _));
        Assert.Contains("enum", why);
    }

    // ---- textures and images --------------------------------------------------------------------------------------
    [Fact]
    public void A_diffuse_normal_roughness_and_metallic_set_writes_two_passthroughs_and_a_composition()
    {
        var rgb = PngCodec.Encode(W, H, Rgba(0, false), false); Write("rgb.png", rgb);
        var normal = PngCodec.Encode(W, H, Rgba(90, false), false); Write("rgb2.png", normal);
        Write("gray.png", PngCodec.Encode(W, H, Gray(0), false)); Write("gray2.png", PngCodec.Encode(W, H, Gray(120), false));
        var m = Model(new[] { Material(100, "Full", Number("BumpFactor", 0.5), Number("ReflectionFactor", 0.8), Number("Shininess", 0.0)) },
            new[] { Texture(60, "diffuse", "tex\\rgb.png"), Video(70, "rgb.png", "tex\\rgb.png"), Texture(61, "normal", "tex\\rgb2.png"), Texture(62, "rough", "tex\\gray.png"), Texture(63, "metal", "tex\\gray2.png") },
            new[] { Link(60, 100, "DiffuseColor"), Link(61, 100, "NormalMap"), Link(62, 100, "ShininessExponent"), Link(63, 100, "ReflectionFactor") }, out string why, out var composed);
        Assert.Null(why);
        var mat = m.Materials[0];
        // the exporter's member order: normal, base colour, metallic-roughness; the factors omitted (1) when linked
        Assert.Equal(0, mat.NormalTexture); Assert.Equal(0.5f, mat.NormalScale); Assert.Equal(1, mat.BaseColorTexture); Assert.Equal(2, mat.MetallicRoughnessTexture);
        Assert.Equal(1f, mat.MetallicFactor); Assert.Equal(1f, mat.RoughnessFactor); Assert.Equal("OPAQUE", mat.AlphaMode);
        Assert.Equal(3, m.Textures.Count); Assert.Equal(1, m.Samplers.Count); Assert.Equal("{\"magFilter\":9729,\"minFilter\":9987}", m.Samplers[0]);
        Assert.Equal(new[] { "rgb2", "rgb", "metal-rough" }, m.Images.Select(i => i.Name));   // two files: the Blender names' stems (the Textures', no Video made them)
        Assert.Equal(normal, m.Images[0].Bytes); Assert.Equal(rgb, m.Images[1].Bytes); Assert.Equal("image/png", m.Images[1].MimeType);
        Assert.Equal(new HashSet<int> { 2 }, composed);
        // the composition: metallic into B, roughness into G, R white, no alpha
        var px = Pixels(m, 2, out var info); Assert.Equal(2, info.ColorType);
        var metal = Gray(120); var rough = Gray(0);
        for (int i = 0; i < W * H; i++) { Assert.Equal(255, px[4 * i]); Assert.Equal(rough[4 * i + 1], px[4 * i + 1]); Assert.Equal(metal[4 * i + 2], px[4 * i + 2]); }
    }

    [Fact]
    public void A_diffuse_with_an_alpha_channel_is_copied_into_the_alpha_slot_and_composed_anew()
    {
        var rgba = PngCodec.Encode(W, H, Rgba(0, true), true); Write("rgba.png", rgba);
        var rgba2 = PngCodec.Encode(W, H, Rgba(5, true), true); Write("rgba2.png", rgba2);
        // Blender: the DiffuseColor link holds the image (one user), so the alpha slot's Non-Color rule COPIES the datablock:
        // two images behind the base colour texture - the exporter composes a new PNG of the same pixels (BLEND)
        var m = Model(new[] { Material(100, "DiffuseAlpha"), Material(101, "AlphaFirst") },
            new[] { Texture(60, "d", "tex\\rgba.png"), Texture(61, "a", "tex\\rgba2.png"), Texture(62, "d2", "tex\\rgba2.png") },
            new[] { Link(60, 100, "DiffuseColor"), Link(61, 101, "TransparencyFactor"), Link(62, 101, "DiffuseColor") }, out string why, out var composed);
        Assert.Null(why);
        Assert.Equal("BLEND", m.Materials[0].AlphaMode); Assert.Equal(0, m.Materials[0].BaseColorTexture); Assert.Equal(new double[] { 1, 1, 1, 1 }, m.Materials[0].BaseColorFactor);
        Assert.Contains(0, composed); Assert.Equal("rgba", m.Images[0].Name);
        Assert.Equal(Rgba(0, true), Pixels(m, 0, out var info)); Assert.Equal(6, info.ColorType);
        // the alpha link FIRST: the image had no user, so it is made Non-Color in place; the base colour node shares it,
        // and the diffuse-alpha pass finds it in that space already: the file's bytes pass through
        Assert.Equal("BLEND", m.Materials[1].AlphaMode); Assert.Equal(1, m.Materials[1].BaseColorTexture);
        Assert.DoesNotContain(1, composed); Assert.Equal(rgba2, m.Images[1].Bytes);
    }

    [Fact]
    public void An_alpha_read_from_a_colour_output_is_the_luminance_over_white()
    {
        var rgb = Rgba(0, false); Write("rgb.png", PngCodec.Encode(W, H, rgb, false)); Write("gray2.png", PngCodec.Encode(W, H, Gray(120), false));
        var m = Model(new[] { Material(100, "MetalLum") }, new[] { Texture(60, "m", "tex\\gray2.png"), Texture(61, "a", "tex\\rgb.png") },
            new[] { Link(60, 100, "ReflectionFactor"), Link(61, 100, "TransparencyFactor") }, out string why, out var composed);
        Assert.Null(why);
        var mat = m.Materials[0];
        Assert.Equal("BLEND", mat.AlphaMode); Assert.Equal(0, mat.BaseColorTexture); Assert.Equal(1, mat.MetallicRoughnessTexture);
        var px = Pixels(m, 0, out var info); Assert.Equal(6, info.ColorType); Assert.Equal("rgb", m.Images[0].Name);
        for (int i = 0; i < W * H; i++)
        {
            float r = (float)(rgb[4 * i] * (1.0f / 255.0f)), g = (float)(rgb[4 * i + 1] * (1.0f / 255.0f)), b = (float)(rgb[4 * i + 2] * (1.0f / 255.0f));
            float lum = (float)((float)((float)(r * 0.2989f) + (float)(g * 0.5870f)) + (float)(b * 0.1140f));
            Assert.Equal(255, px[4 * i]); Assert.Equal(255, px[4 * i + 1]); Assert.Equal(255, px[4 * i + 2]); Assert.Equal(FbxMaterials.UnitFloatToUchar(lum), px[4 * i + 3]);
        }
        // metallic alone: B from the image, G and R white
        var mr = Pixels(m, 1, out info); Assert.Equal(2, info.ColorType); Assert.Equal("gray2", m.Images[1].Name);
        var metal = Gray(120);
        for (int i = 0; i < W * H; i++) { Assert.Equal(255, mr[4 * i]); Assert.Equal(255, mr[4 * i + 1]); Assert.Equal(metal[4 * i + 2], mr[4 * i + 2]); }
    }

    [Fact]
    public void Missing_files_drop_their_texture_and_feed_compositions_as_white()
    {
        Write("gray.png", PngCodec.Encode(W, H, Gray(0), false)); Write("rgb.png", PngCodec.Encode(W, H, Rgba(0, false), false));
        File.WriteAllText(Path.Combine(dir, "tex", "notimage.png"), "this is not an image at all");
        var m = Model(new[] { Material(100, "MissingDiffuse", Color("DiffuseColor", 0.2, 0.3, 0.4)), Material(101, "MissingMetal"), Material(102, "MissingBoth"), Material(103, "MissingAlpha"), Material(104, "NotImage"), Material(105, "MissingMetalAlone") },
            new[] { Texture(60, "missing", "tex\\missing.png"), Texture(61, "rough", "tex\\gray.png"), Texture(62, "missing2", "tex\\missing2.png"), Texture(63, "base", "tex\\rgb.png"), Texture(64, "notimage", "tex\\notimage.png") },
            new[] { Link(60, 100, "DiffuseColor"), Link(60, 101, "ReflectionFactor"), Link(61, 101, "ShininessExponent"), Link(60, 102, "ReflectionFactor"), Link(62, 102, "ShininessExponent"),
                    Link(63, 103, "DiffuseColor"), Link(60, 103, "TransparencyFactor"), Link(64, 104, "DiffuseColor"), Link(60, 105, "ReflectionFactor") }, out string why, out var composed);
        Assert.Null(why);
        // a linked Base Color reads as white even when its image is gone; the texture itself is not written
        Assert.Equal(-1, m.Materials[0].BaseColorTexture); Assert.Equal(new double[] { 1, 1, 1, 1 }, m.Materials[0].BaseColorFactor);
        // a missing metallic beside a roughness: the roughness alone, white elsewhere
        Assert.Equal(0, m.Materials[1].MetallicRoughnessTexture); Assert.Equal("gray", m.Images[0].Name);
        // both missing: the roughness fill replaced the metallic's white before both were dropped - nothing is written
        Assert.Equal(-1, m.Materials[2].MetallicRoughnessTexture);
        // a missing alpha beside a base: the base passes through (PNG, the Alpha socket reads it) and the mode is BLEND
        Assert.Equal("BLEND", m.Materials[3].AlphaMode); Assert.Equal(1, m.Materials[3].BaseColorTexture); Assert.DoesNotContain(1, composed); Assert.Equal("rgb", m.Images[1].Name);
        // bytes Blender cannot read as an image make a placeholder: nothing written
        Assert.Equal(-1, m.Materials[4].BaseColorTexture);
        // a missing metallic alone: its white G fill stays, so the exporter writes a 1x1 white image named "Image"
        Assert.Equal(2, m.Materials[5].MetallicRoughnessTexture); Assert.Equal("Image", m.Images[2].Name);
        var px = Pixels(m, 2, out var info); Assert.Equal(1, info.Width); Assert.Equal(2, info.ColorType); Assert.Equal(new byte[] { 255, 255, 255, 255 }, px);
        Assert.Equal(3, m.Images.Count);
    }

    [Fact]
    public void Image_names_follow_the_file_stem_or_the_blender_names_and_twins_are_numbered()
    {
        Write("gray.png", PngCodec.Encode(W, H, Gray(0), false)); Directory.CreateDirectory(Path.Combine(dir, "tex", "other")); File.WriteAllBytes(Path.Combine(dir, "tex", "other", "gray.png"), PngCodec.Encode(W, H, Gray(77), false));
        var pngAsTga = PngCodec.Encode(W, H, Rgba(33, false), false); Write("png_named_tga.tga", pngAsTga);
        Directory.CreateDirectory(Path.Combine(dir, "sub", "deep")); File.WriteAllBytes(Path.Combine(dir, "sub", "deep", "found.png"), PngCodec.Encode(W, H, Rgba(60, false), false));
        var m = Model(new[] { Material(100, "Twins"), Material(101, "TgaNamed"), Material(102, "WrongDir") },
            new[] { Texture(60, "twin1", "tex\\gray.png"), Video(70, "same.png", "tex\\gray.png"), Texture(61, "twin2", "tex\\other\\gray.png"), Video(71, "dir/same.png", "tex\\other\\gray.png"),
                    Texture(62, "tganamed", "tex\\png_named_tga.tga"), Texture(63, "wrongdir", "nowhere\\FOUND.png") },
            new[] { Link(60, 100, "ReflectionFactor"), Link(61, 100, "ShininessExponent"), Link(62, 101, "DiffuseColor"), Link(63, 102, "DiffuseColor") }, out string why, out _);
        Assert.Null(why);
        // two files, so the Blender names' stems joined: the Videos named "same.png" and "same.png.001" (the pool's twin)
        Assert.Equal("same-same.png", m.Images[0].Name);
        // a .tga holding PNG bytes passes through (the bytes are what the MIME type says) under the Blender name's stem: the
        // Texture's own name, since no Video made the image
        Assert.Equal("tganamed", m.Images[1].Name); Assert.Equal(pngAsTga, m.Images[1].Bytes);
        // the recursive search finds the file by case-folded base name under the file's directory
        Assert.Equal("found", m.Images[2].Name);
    }

    [Fact]
    public void A_content_block_packs_the_image_and_its_pixels_replace_the_files()
    {
        var rgba = PngCodec.Encode(W, H, Rgba(0, true), true); Write("rgb.png", PngCodec.Encode(W, H, Rgba(0, false), false));
        var m = Model(new[] { Material(100, "Packed"), Material(101, "OverFile") },
            new[] { Video(70, "packed.png", "nowhere\\packed.png", rgba), Texture(60, "packed", "nowhere\\packed.png"), Video(71, "overfile.png", "tex\\rgb.png", rgba), Texture(61, "overfile", "tex\\rgb.png") },
            new[] { Link(60, 100, "DiffuseColor"), Link(61, 101, "DiffuseColor") }, out string why, out var composed);
        Assert.Null(why);
        // the packed RGBA has depth 32: the alpha rule applies to both, and the pixels are the packed data's (measured: a packed
        // RGBA over an RGB file on disk reads as depth 32, so image.pack reloads the buffers)
        Assert.Equal("BLEND", m.Materials[0].AlphaMode); Assert.Equal("BLEND", m.Materials[1].AlphaMode);
        Assert.Equal(new[] { "packed", "rgb" }, m.Images.Select(i => i.Name));
        Assert.Equal(Rgba(0, true), Pixels(m, 1, out _));
    }

    [Fact]
    public void A_jpeg_passes_through_by_its_extension_and_is_written_once_for_two_slots()
    {
        var jpg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwC3RRRX1p8wf//Z");
        Write("tiny.jpg", jpg);
        var m = Model(new[] { Material(100, "Jpeg") }, new[] { Texture(60, "jpg", "tex\\tiny.jpg"), Texture(61, "jpgnormal", "tex\\tiny.jpg") },
            new[] { Link(60, 100, "DiffuseColor"), Link(61, 100, "NormalMap") }, out string why, out _);
        Assert.Null(why);
        Assert.Equal(2, m.Textures.Count); Assert.Equal(1, m.Images.Count); Assert.Equal("image/jpeg", m.Images[0].MimeType); Assert.Equal(jpg, m.Images[0].Bytes); Assert.Equal("tiny", m.Images[0].Name);
        // the JPEG's pixels are not decoded here: inside a composition it is a named fallback
        Write("gray8.png", PngCodec.Encode(8, 8, Enumerable.Range(0, 8 * 8 * 4).Select(i => (byte)(i % 4 == 3 ? 255 : 29 * i % 256)).ToArray(), false));
        Assert.Null(Model(new[] { Material(100, "JpegMetal") }, new[] { Texture(60, "jpg", "tex\\tiny.jpg"), Texture(61, "rough", "tex\\gray8.png") }, new[] { Link(60, 100, "ReflectionFactor"), Link(61, 100, "ShininessExponent") }, out why, out _));
        Assert.Contains("JPEG", why);
    }

    [Fact]
    public void Wrap_modes_make_a_clamping_sampler_and_a_real_transform_is_a_named_fallback()
    {
        Write("rgb.png", PngCodec.Encode(W, H, Rgba(0, false), false));
        var m = Model(new[] { Material(100, "ClampU"), Material(101, "RotX") },
            new[] { Texture(60, "c", "tex\\rgb.png", P("WrapModeU", "enum", "", 1)), Texture(61, "r", "tex\\rgb.png", P("Rotation", "Vector3D", "Vector", 10.0, 0.0, 0.0)) },
            new[] { Link(60, 100, "DiffuseColor"), Link(61, 101, "DiffuseColor") }, out string why, out _);
        Assert.Null(why);
        Assert.Equal("{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":33071,\"wrapT\":33071}", m.Samplers[m.Textures[0].Sampler]);
        Assert.Equal("{\"magFilter\":9729,\"minFilter\":9987}", m.Samplers[m.Textures[1].Sampler]);   // a rotation about X: the exporter skips the transform
        Assert.Empty(m.ExtensionsUsed);
        Assert.Null(Model(new[] { Material(100, "Moved") }, new[] { Texture(60, "t", "tex\\rgb.png", P("Translation", "Vector3D", "Vector", 0.5, 0.0, 0.0)) }, new[] { Link(60, 100, "DiffuseColor") }, out why, out _));
        Assert.Contains("KHR_texture_transform", why);
    }

    [Theory]
    [InlineData(2147483647)]
    [InlineData(2147483636)]
    [InlineData(-1)]
    public void Png_chunk_lengths_cannot_wrap_the_bounds_check(int length)
    {
        var png = RawRgbPng(1, 1, new byte[] { 0, 20, 40, 60 });
        for (int b = 0; b < 4; b++) png[8 + b] = (byte)(length >> (24 - 8 * b));
        Assert.Null(PngCodec.ReadInfo(png));
        Assert.Null(PngCodec.Decode(png, out _, out string why)); Assert.NotNull(why);
    }

    [Theory]
    [InlineData(2147483647, 2147483647)]
    [InlineData(65536, 65536)]
    public void Png_dimensions_decline_before_allocating_unrepresentable_buffers(int width, int height)
    {
        Assert.Null(PngCodec.Decode(RawRgbPng(width, height, new byte[] { 0, 1, 2, 3 }), out _, out string why));
        Assert.Contains("buffer limits", why);
    }

    [Fact]
    public void Bad_scanlines_and_critical_checksums_return_a_reason_instead_of_throwing()
    {
        foreach (var raw in new[] { new byte[] { 5, 20, 40, 60 }, new byte[] { 0, 20, 40 } })
        { Assert.Null(PngCodec.Decode(RawRgbPng(1, 1, raw), out _, out string why)); Assert.NotNull(why); }
        var png = RawRgbPng(1, 1, new byte[] { 0, 20, 40, 60 }); png[29] ^= 1;
        Assert.Null(PngCodec.Decode(png, out _, out string crcWhy)); Assert.NotNull(crcWhy);
    }

    [Fact]
    public void Png_inflation_is_bounded_by_the_image_even_with_a_large_unused_tail()
    {
        var raw = new byte[8 * 1024 * 1024]; raw[1] = 20; raw[2] = 40; raw[3] = 60;
        var png = RawRgbPng(1, 1, raw);
        var allocated = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread"));
        PngCodec.Decode(png, out _, out _); // warm runtime/deflate allocation
        long before = allocated(); var px = PngCodec.Decode(png, out _, out string why); long bytes = allocated() - before;
        Assert.Null(why); Assert.Equal(new byte[] { 20, 40, 60, 255 }, px);
        Assert.True(bytes < 128 * 1024, "A 1-pixel PNG allocated " + bytes + " bytes");
    }

    [Fact]
    public void A_large_claimed_header_does_not_allocate_its_buffer_before_scanlines_exist()
    {
        var png = RawRgbPng(10000000, 1, new byte[] { 0, 20, 40, 60 });
        var allocated = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread"));
        PngCodec.Decode(png, out _, out _);
        long before = allocated(); var px = PngCodec.Decode(png, out _, out string why); long bytes = allocated() - before;
        Assert.Null(px); Assert.Contains("too little image data", why);
        Assert.True(bytes < 128 * 1024, "An incomplete PNG allocated " + bytes + " bytes");
    }

    [Fact]
    public void Broken_pngs_become_placeholders_and_do_not_export_invalid_image_bytes()
    {
        var png = RawRgbPng(1, 1, new byte[] { 0, 20, 40, 60 });
        var crc = (byte[])png.Clone(); crc[29] ^= 1;
        var inputs = new[] { png.Take(33).ToArray(), crc, RawRgbPng(1, 1, new byte[] { 5, 20, 40, 60 }), RawRgbPng(1, 1, new byte[] { 0, 20, 40 }) };
        foreach (var input in inputs)
        {
            Write("broken.png", input);
            var m = Model(new[] { Material(100, "Mat") }, new[] { Texture(60, "Broken", "tex\\broken.png") }, new[] { Link(60, 100, "DiffuseColor") });
            Assert.Empty(m.Images); Assert.Empty(m.Textures);
        }
        Write("no_end.png", png.Take(png.Length - 12).ToArray());
        Assert.Single(Model(new[] { Material(100, "Mat") }, new[] { Texture(60, "Valid", "tex\\no_end.png") }, new[] { Link(60, 100, "DiffuseColor") }).Images);
    }

    [Fact]
    public void Jpeg_frame_without_scan_data_is_a_placeholder()
    {
        byte[] header = { 255, 216, 255, 192, 0, 17, 8, 0, 1, 0, 1, 3, 1, 17, 0, 2, 17, 0, 3, 17, 0 };
        Write("header.jpg", header);
        var m = Model(new[] { Material(100, "Mat") }, new[] { Texture(60, "Broken", "tex\\header.jpg") }, new[] { Link(60, 100, "DiffuseColor") });
        Assert.Empty(m.Images); Assert.Empty(m.Textures);
    }

    [Fact]
    public void Cached_images_skip_the_custom_property_pass_as_blender_does()
    {
        Write("rgb.png", PngCodec.Encode(W, H, Rgba(0, false), false));
        var tex = Texture(60, "Cached", "tex\\rgb.png"); tex.Children.Add(Props70(N("P", "IgnoredShortProperty")));
        var m = Model(new[] { Material(100, "Mat") }, new[] { Video(70, "Video", "tex\\rgb.png"), tex }, new[] { Link(60, 100, "DiffuseColor") });
        Assert.Single(m.Images); Assert.Single(m.Textures);
        // The same block on a newly created image does reach Blender's custom-property pass and must still decline.
        Assert.Null(Model(new[] { Material(100, "Mat") }, new[] { tex }, new[] { Link(60, 100, "DiffuseColor") }, out string why, out _));
        Assert.Contains("fewer than four", why);
    }
}
