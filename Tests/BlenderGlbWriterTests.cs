using System;
using System.Linq;
using System.Text;
using Xunit;

/// <summary>BlenderGlbWriter and PyFloatRepr: the GLB's bytes as the glTF exporter writes them (part 8c).
/// tools/deploy_drill.sh holds whole jobs to Blender's files, byte for byte; here each rule on something small.</summary>
public class BlenderGlbWriterTests
{
    [Theory]
    [InlineData(0.25, "0.25")]
    [InlineData(1.0 / 24.0, "0.041666666666666664")]
    [InlineData(5.0 / 24.0, "0.20833333333333334")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(1.5e16, "1.5e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(0.00001234, "1.234e-05")]
    [InlineData(5.9604644775390625e-08, "5.960464477539063e-08")]   // 2^-24: the interval below a power of two is half as wide
    [InlineData(6.189700196426902e+26, "6.189700196426902e+26")]    // 2^89
    [InlineData(5.684341886080802e-14, "5.684341886080802e-14")]    // 2^-44
    [InlineData(123456789012345.0, "123456789012345.0")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(-2.5, "-2.5")]
    public void Repr_writes_the_shortest_round_trip_as_Python_does(double v, string expected)
    {
        Assert.Equal(expected, PyFloatRepr.Repr(v));
    }

    [Fact]
    public void Repr_of_a_float32_value_is_the_doubles_shortest_digits()
    {
        Assert.Equal("0.10000000149011612", PyFloatRepr.Repr((double)0.1f));
        Assert.Equal("0.3565211892127991", PyFloatRepr.Repr(0.3565211892127991));   // 16 digits: .NET's "R" would write 17
    }

    [Fact]
    public void Json_writes_an_integral_float_as_an_int_and_a_negative_zero_as_0()
    {
        Assert.Equal("1", PyFloatRepr.Json(1.0));
        Assert.Equal("0", PyFloatRepr.Json(-0.0));
        Assert.Equal("-3", PyFloatRepr.Json(-3.0));
        Assert.Equal("0.5", PyFloatRepr.Json(0.5));
        Assert.Equal("100000000000000000000", PyFloatRepr.Json(1e20));                                    // int(obj), however long
        Assert.Equal("340282346638528859811704183484516925440", PyFloatRepr.Json(3.4028234663852886e+38));   // float32's max, as np.nan_to_num leaves an infinity
        Assert.Throws<ArgumentException>(() => PyFloatRepr.Repr(double.NaN));
    }

    static BlenderDeployExport.Result Small()
    {
        var x = new BlenderDeployExport.Result();
        x.Scenes.Add(("Scene", new System.Collections.Generic.List<int> { 1 }));
        var mesh = new BlenderDeployExport.Mesh { Name = "tri", Primitives = new System.Collections.Generic.List<BlenderExport.Primitive>() };
        mesh.Primitives.Add(new BlenderExport.Primitive { VertexCount = 3, Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, Indices = new[] { 0, 1, 2 } });
        mesh.Material.Add(-1);
        x.Meshes.Add(mesh);
        var child = new BlenderDeployExport.Node { Name = "Ch\"ild \u00e9", Mesh = 0, Translation = new[] { 1f, 0f, 0f } };
        var root = new BlenderDeployExport.Node { Name = "Root" }; root.Children.Add(0);
        x.Nodes.Add(child); x.Nodes.Add(root);
        x.MaterialRows = new BlenderMaterialExport.Result();
        x.ChannelRows = new BlenderChannelExport.Result();
        return x;
    }

    [Fact]
    public void The_json_keeps_the_exporters_key_orders_drops_the_empty_and_escapes_as_Python_does()
    {
        var w = BlenderGlbWriter.Build(Small());
        Assert.Null(w.Fallback);
        Assert.StartsWith("{\"asset\":{\"generator\":\"Khronos glTF Blender I/O v5.1.20\",\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"name\":\"Scene\",\"nodes\":[1]}],\"nodes\":[", w.Json);
        // a node without children has no children key; translation after name (alphabetical); the name escaped, non-ASCII as \u00e9
        Assert.Contains("{\"mesh\":0,\"name\":\"Ch\\\"ild \\u00e9\",\"translation\":[1,0,0]},{\"children\":[0],\"name\":\"Root\"}", w.Json);
        // POSITION carries min and max; the indices accessor is the fourth view, with the element target; no materials, no buffers key beyond the one buffer
        Assert.Contains("\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"max\":[1,1,0],\"min\":[0,0,0],\"type\":\"VEC3\"}", w.Json);
        Assert.Contains("{\"buffer\":0,\"byteLength\":6,\"byteOffset\":72,\"target\":34963}", w.Json);
        Assert.DoesNotContain("materials", w.Json); Assert.DoesNotContain("extensionsUsed", w.Json);
        Assert.EndsWith("\"buffers\":[{\"byteLength\":80}]}", w.Json);   // 36 + 36 + 6 padded to 8
    }

    [Fact]
    public void The_glb_is_the_header_the_json_padded_with_spaces_and_the_bin_padded_with_zeros()
    {
        var w = BlenderGlbWriter.Build(Small());
        var g = w.Glb;
        Assert.Equal("glTF", Encoding.ASCII.GetString(g, 0, 4));
        Assert.Equal(2, BitConverter.ToInt32(g, 4));
        Assert.Equal(g.Length, BitConverter.ToInt32(g, 8));
        int jl = BitConverter.ToInt32(g, 12);
        Assert.Equal(0, jl % 4); Assert.Equal("JSON", Encoding.ASCII.GetString(g, 16, 4));
        Assert.Equal(w.Json, Encoding.UTF8.GetString(g, 20, jl).TrimEnd(' '));
        Assert.True(jl - Encoding.UTF8.GetByteCount(w.Json) < 4);
        int bl = BitConverter.ToInt32(g, 20 + jl);
        Assert.Equal("BIN\0", Encoding.ASCII.GetString(g, 24 + jl, 4));
        Assert.Equal(80, bl); Assert.Equal(28 + jl + bl, g.Length);
    }

    [Fact]
    public void An_extensions_keys_follow_its_gather_order_not_the_alphabet()
    {
        var x = Small();
        var mat = Newtonsoft.Json.Linq.JObject.Parse("{\"name\":\"both\",\"extensions\":{\"KHR_materials_specular\":{\"specularColorFactor\":[0.5,0.5,0.5],\"specularColorTexture\":{\"index\":1},\"specularFactor\":0.5,\"specularTexture\":{\"index\":0}}}}");
        x.MaterialRows.Materials.Add(mat); x.MaterialRows.Names.Add("both");
        x.MaterialRows.Images.Add(("specA", "image/png", new byte[] { 1, 2, 3 }, 0)); x.MaterialRows.Images.Add(("specB", "image/png", new byte[] { 4, 5, 6, 7 }, 1));
        x.MaterialRows.Samplers.Add(new Newtonsoft.Json.Linq.JObject { ["magFilter"] = 9729, ["minFilter"] = 9987 });
        x.MaterialRows.Textures.Add((0, 0)); x.MaterialRows.Textures.Add((1, 0));
        x.Meshes[0].Material[0] = 0;
        var w = BlenderGlbWriter.Build(x);
        Assert.Null(w.Fallback);
        // measured in Blender 2026-10-11: specularFactor, specularTexture, specularColorFactor, specularColorTexture, the images' views in that order
        Assert.Contains("\"extensions\":{\"KHR_materials_specular\":{\"specularFactor\":0.5,\"specularTexture\":{\"index\":0},\"specularColorFactor\":[0.5,0.5,0.5],\"specularColorTexture\":{\"index\":1}}}", w.Json);
        Assert.Contains("{\"buffer\":0,\"byteLength\":6,\"byteOffset\":72,\"target\":34963},{\"buffer\":0,\"byteLength\":3,\"byteOffset\":80},{\"buffer\":0,\"byteLength\":4,\"byteOffset\":84}", w.Json);
        Assert.Contains("\"extensionsUsed\":[\"KHR_materials_specular\"]", w.Json);
    }

    [Fact]
    public void Two_primitives_with_the_same_index_bytes_share_one_accessor_but_never_their_positions()
    {
        var x = Small();
        var mesh2 = new BlenderDeployExport.Mesh { Name = "tri2", Primitives = new System.Collections.Generic.List<BlenderExport.Primitive>() };
        mesh2.Primitives.Add(new BlenderExport.Primitive { VertexCount = 3, Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, Indices = new[] { 0, 1, 2 } });
        mesh2.Material.Add(-1);
        x.Meshes.Add(mesh2);
        x.Nodes.Insert(1, new BlenderDeployExport.Node { Name = "Second", Mesh = 1 });
        x.Nodes[2].Children.Add(1); x.Scenes[0].roots[0] = 2;
        var w = BlenderGlbWriter.Build(x);
        Assert.Null(w.Fallback);
        Assert.Equal(5, w.Accessors); Assert.Equal(5, w.BufferViews); Assert.Equal(1, w.SharedAccessors);
        Assert.Contains("\"primitives\":[{\"attributes\":{\"POSITION\":3,\"NORMAL\":4},\"indices\":2}]", w.Json);
    }
}
