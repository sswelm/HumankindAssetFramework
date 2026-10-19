using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

public class ImageEncodingParityTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "haf-image-parity-" + Guid.NewGuid().ToString("N"));
    public ImageEncodingParityTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { Directory.Delete(dir, true); }
    byte[] Make(bool jpeg, Action<HafModel> change = null)
    {
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene()); m.Scenes[0].Nodes.Add(0);
        m.Nodes.Add(new HafNode { Name = "Hull", Mesh = 0 }); var mesh = new HafMesh { Name = "Hull" }; m.Meshes.Add(mesh);
        mesh.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[] { 0,0,0, 1,0,0, 0,1,0 }, Indices = new[] { 0,1,2 }, Material = 0 });
        m.Materials.Add(new HafMaterial { Name = "Glass", AlphaMode = "BLEND", BaseColorTexture = 0 });
        m.Textures.Add(new HafTexture { Source = 0 });
        // The normalization kernel assumes its caller has already verified decoded pixels. Native tests
        // exercise that decoding separately; arbitrary bytes here isolate layout and metadata checks.
        m.Images.Add(new HafImage { Name = "Color", MimeType = jpeg ? "image/jpeg" : "image/png", Bytes = jpeg ? new byte[] {255,216,255,1,2} : new byte[] {137,80,78,71,1,2,3,4,5} });
        change?.Invoke(m); string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".glb"); GlbWriter.Write(m,path); return File.ReadAllBytes(path);
    }
    [Fact]
    public void Verified_encoding_changes_can_move_later_views_without_hiding_their_bytes()
        => Assert.Null(GlbDisconnectedParts.ImageEncodingDifference(Make(false), Make(true), new HashSet<int> {0}));
    [Fact]
    public void A_mesh_change_including_the_sign_of_zero_is_still_rejected()
    {
        float negativeZero = BitConverter.ToSingle(new byte[] {0,0,0,128},0);
        Assert.NotNull(GlbDisconnectedParts.ImageEncodingDifference(Make(false), Make(true,m=>m.Meshes[0].Primitives[0].Positions[0]=negativeZero), new HashSet<int> {0}));
    }
    [Fact]
    public void Material_and_image_metadata_are_still_checked()
    {
        var a=Make(false);var verified=new HashSet<int>{0};
        Assert.NotNull(GlbDisconnectedParts.ImageEncodingDifference(a,Make(true,m=>m.Materials[0].DoubleSided=true),verified));
        Assert.NotNull(GlbDisconnectedParts.ImageEncodingDifference(a,Make(true,m=>m.Images[0].Name="changed"),verified));
    }
    [Fact]
    public void Missing_or_wrong_pixel_verification_does_not_relax_comparison()
    {
        var a=Make(false);var b=Make(true);
        Assert.NotNull(GlbDisconnectedParts.ImageEncodingDifference(a,b,new HashSet<int>()));
        Assert.NotNull(GlbDisconnectedParts.ImageEncodingDifference(a,b,new HashSet<int>{1}));
        Assert.NotNull(GlbDisconnectedParts.ImageEncodingDifference(b,a,new HashSet<int>{0}));
    }
    [Fact]
    public void A_changed_nonimage_attribute_is_not_masked_by_equal_model_values()
        => Assert.NotNull(GlbDisconnectedParts.ImageEncodingDifference(Make(false),Make(true,m=>m.Meshes[0].Primitives[0].Indices[0]=1),new HashSet<int>{0}));
    [Fact]
    public void Nonzero_binary_padding_is_not_ignored()
    {
        var a=Make(false);var b=Make(true);b[b.Length-1]=123;
        Assert.NotNull(GlbDisconnectedParts.ImageEncodingDifference(a,b,new HashSet<int>{0}));
    }
}
