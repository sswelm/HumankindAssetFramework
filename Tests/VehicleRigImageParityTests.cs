using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

public class VehicleRigImageParityTests
{
    static string Sha(byte[] bytes)
    {
        using (var h = System.Security.Cryptography.SHA256.Create())
            return string.Concat(h.ComputeHash(bytes).Select(b => b.ToString("x2")));
    }

    [Theory]
    [InlineData(false, false, false)] // a source PNG must stay byte-identical
    [InlineData(true, false, true)]   // a composed image may use another PNG encoding
    [InlineData(true, true, true)]    // a composed image in a merged FBX beside a GLB primary source
    public void Only_proven_composed_images_can_borrow_pixel_equal_bytes(bool composed, bool glbPrimary, bool borrow)
    {
        Check(composed, glbPrimary, false, false, borrow);
    }

    [Theory]
    [InlineData(true, false)] // a single changed channel is a real export difference
    [InlineData(false, true)] // the supplied oracle image must hash to its evidence row
    public void Composed_image_pixel_and_evidence_damage_still_fail(bool changePixel, bool changeHash)
    {
        Check(true, false, changePixel, changeHash, false);
    }

    static void Check(bool composed, bool glbPrimary, bool changePixel, bool changeHash, bool borrow)
    {
        var rgba = new byte[] { 30, 60, 90, 255 };
        var mine = PngCodec.Encode(1, 1, rgba, false);
        if (changePixel) rgba[1] ^= 1;
        var theirs = PngCodec.Encode(1, 1, rgba, true);
        Assert.False(mine.SequenceEqual(theirs));
        var exported = new BlenderDeployExport.Result {
            MaterialRows = new BlenderMaterialExport.Result(), ChannelRows = new BlenderChannelExport.Result()
        };
        exported.MaterialRows.Images.Add(("texture", "image/png", mine, 0));
        var oracle = new BlenderDeployExport.Result {
            MaterialRows = new BlenderMaterialExport.Result(), ChannelRows = new BlenderChannelExport.Result()
        };
        oracle.MaterialRows.Images.Add(("texture", "image/png", theirs, 0));
        oracle.MaterialRows.Materials.Add(Newtonsoft.Json.Linq.JObject.Parse("{\"name\":\"paint\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}"));
        oracle.MaterialRows.Names.Add("paint");
        oracle.MaterialRows.Textures.Add((0, -1));
        var mesh = new BlenderDeployExport.Mesh { Name = "tri", Primitives = new List<BlenderExport.Primitive>() };
        mesh.Primitives.Add(new BlenderExport.Primitive { VertexCount = 3, Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, Indices = new[] { 0, 1, 2 } });
        mesh.Material.Add(0); oracle.Meshes.Add(mesh);
        oracle.Nodes.Add(new BlenderDeployExport.Node { Name = "Part", Mesh = 0 });
        oracle.Scenes.Add(("Scene", new List<int> { 0 }));
        var file = BlenderGlbWriter.Build(oracle); Assert.Null(file.Fallback);
        string key = "images-" + Guid.NewGuid().ToString("N"), path = Path.Combine(Path.GetTempPath(), key + ".glb");
        var dump = new VehicleRigDump { Argv = new[] { "rig", glbPrimary ? "primary.glb" : "primary.fbx" } };
        dump.ExportRows.Add(new[] { "IMG", "0", "texture", "image/png", changeHash ? new string('0', 64) : Sha(theirs) });
        var provenance = new HashSet<byte[]>(); if (composed) provenance.Add(mine);
        var problems = new List<string>();
        try
        {
            File.WriteAllBytes(path, file.Glb); StageDrill.BlenderFile[key] = path;
            var borrowedFile = StageDrill.BorrowPixelEqualImages(key, dump, exported, problems, out var saved, provenance);
            if (borrow)
            {
                Assert.Equal(file.Glb, borrowedFile);
                var savedImage = Assert.Single(saved); Assert.Same(mine, savedImage.mine);
                Assert.Equal(theirs, exported.MaterialRows.Images[0].bytes);
            }
            else
            {
                Assert.Null(borrowedFile); Assert.Empty(saved);
                Assert.Same(mine, exported.MaterialRows.Images[0].bytes);
            }
            if (changePixel) Assert.Contains(problems, p => p.Contains("PIXELS"));
            else if (changeHash) Assert.Contains(problems, p => p.Contains("hash to its IMG row"));
            else Assert.Empty(problems);
        }
        finally { StageDrill.BlenderFile.Remove(key); if (File.Exists(path)) File.Delete(path); }
    }
}
