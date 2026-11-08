using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

public class GlbRowsCompareTests
{
    // Blender re-encodes the rig's JPEG albedo. Unknown image bytes must not make file evidence optional.
    static BlenderDeployExport.Result Export()
    {
        var x = new BlenderDeployExport.Result();
        x.Scenes.Add(("Scene", new List<int>()));
        x.MaterialRows = new BlenderMaterialExport.Result();
        x.MaterialRows.Images.Add(("albedo", "image/png", null, 0));
        x.ChannelRows = new BlenderChannelExport.Result();
        return x;
    }

    static List<string[]> Rows() => new List<string[]>
    {
        new[] { "SCENE", "Scene", "" },
        new[] { "IMG", "0", "albedo", "image/png", new string('a', 64) },
        new[] { "GLB", "100", "40", "32", new string('b', 64) },
    };

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("short")]
    public void Unknown_image_bytes_do_not_allow_missing_or_invalid_file_evidence(string damage)
    {
        var rows = Rows();
        if (damage == "missing") rows.RemoveAt(2);
        else if (damage == "duplicate") rows.Add(rows[2]);
        else rows[2] = new[] { "GLB", "100" };
        Assert.Throws<InvalidDataException>(() => GlbRowsCompare.Compare(Export(), rows, new List<string>(),
            new Dictionary<string, long>(), _ => 0, true, true, null));
    }

    [Fact]
    public void Intact_file_evidence_compares_structure_and_reports_uncompared_image_bytes()
    {
        var problems = new List<string>(); var cover = new Dictionary<string, long>();
        var stats = GlbRowsCompare.Compare(Export(), Rows(), problems, cover, _ => 0, true, true, null);
        Assert.Empty(problems);
        Assert.False(stats.GlbEqual);
        Assert.Equal(1L, cover["the GLB's bytes not compared (an image's bytes are Blender's encoder's, and its file is not at hand)"]);
    }

    // ---- Blender's file at hand (2026-11-07): its own bytes stand in for the image the port does not know, the rest compared whole
    static BlenderDeployExport.Result Textured(byte[] imageBytes)
    {
        var x = new BlenderDeployExport.Result();
        x.Scenes.Add(("Scene", new List<int> { 0 }));
        var mesh = new BlenderDeployExport.Mesh { Name = "tri", Primitives = new List<BlenderExport.Primitive>() };
        mesh.Primitives.Add(new BlenderExport.Primitive { VertexCount = 3, Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, Indices = new[] { 0, 1, 2 } });
        mesh.Material.Add(0);
        x.Meshes.Add(mesh);
        x.Nodes.Add(new BlenderDeployExport.Node { Name = "Part", Mesh = 0 });
        x.MaterialRows = new BlenderMaterialExport.Result();
        x.MaterialRows.Materials.Add(Newtonsoft.Json.Linq.JObject.Parse("{\"name\":\"paint\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}"));
        x.MaterialRows.Names.Add("paint");
        x.MaterialRows.Images.Add(("albedo", "image/png", imageBytes, 0));
        x.MaterialRows.Samplers.Add(new Newtonsoft.Json.Linq.JObject { ["magFilter"] = 9729, ["minFilter"] = 9987 });
        x.MaterialRows.Textures.Add((0, 0));
        x.ChannelRows = new BlenderChannelExport.Result();
        return x;
    }

    static string Sha(byte[] b) { using (var h = System.Security.Cryptography.SHA256.Create()) return string.Concat(h.ComputeHash(b).Select(v => v.ToString("x2"))); }

    /// <summary>Blender's file and the dump's rows for it: the port's own rows over the full export (every kind the comparison
    /// reads), plus the GLB row (lengths and sha) of the written file.</summary>
    static (byte[] glb, List<string[]> rows) Blenders(byte[] imageBytes)
    {
        var full = Textured(imageBytes);
        var w = BlenderGlbWriter.Build(full); Assert.Null(w.Fallback);
        var mine = new List<string>();
        var problems = new List<string>();
        GlbRowsCompare.Compare(full, new List<string[]> { new[] { "SCENE", "Scene", "0" } }, problems, new Dictionary<string, long>(), _ => 0, true, false, null, mine);
        var rows = mine.Select(l => l.Split('\t')).ToList();
        // the material, texture and sampler rows the comparison reads but does not keep: as glb_rows.py writes them
        rows.Add(new[] { "MESH", "0", "tri", "1" });
        rows.Add(new[] { "MAT", "0", full.MaterialRows.Materials[0].ToString(Newtonsoft.Json.Formatting.None) });
        rows.Add(new[] { "TEX", "0", "0", "0", "{}" });
        rows.Add(new[] { "SAMP", "0", full.MaterialRows.Samplers[0].ToString(Newtonsoft.Json.Formatting.None) });
        int jsonPadded = (System.Text.Encoding.UTF8.GetByteCount(w.Json) + 3) & ~3, binPadded = (w.Bin.Length + 3) & ~3;
        rows.Add(new[] { "GLB", w.Glb.Length.ToString(), jsonPadded.ToString(), binPadded.ToString(), Sha(w.Glb) });
        return (w.Glb, rows);
    }

    [Fact]
    public void Blenders_own_image_bytes_stand_in_and_the_rest_of_the_file_is_compared_whole()
    {
        var (glb, rows) = Blenders(new byte[] { 9, 8, 7, 6, 5 });
        var x = Textured(null);
        var problems = new List<string>(); var cover = new Dictionary<string, long>();
        var stats = GlbRowsCompare.Compare(x, rows, problems, cover, _ => 0, true, true, glb);
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
        Assert.True(stats.GlbEqual); Assert.Equal(glb.Length, stats.GlbBytes);
        Assert.Equal(1L, cover["the GLB's bytes compared with Blender's own bytes standing in for its re-encoded image(s)"]);
        Assert.Equal(1L, cover["a re-encoded image's bytes taken from Blender's file"]);
        Assert.Null(x.MaterialRows.Images[0].bytes);   // the stand-in is undone: the export's own rows say what the port knew
        // the file at hand is the evidence for the image alone: a flipped byte anywhere else in Blender's file is a difference
        var flipped = (byte[])glb.Clone(); flipped[flipped.Length - 1] ^= 0x40;   // the last byte: the padded BIN's tail, outside the image
        problems.Clear(); cover.Clear();
        stats = GlbRowsCompare.Compare(Textured(null), rows, problems, cover, _ => 0, true, true, flipped);
        Assert.Contains(problems, p => p.StartsWith("the bytes differ at offset") || p.StartsWith("the GLB (bytes"));
        Assert.False(stats.GlbEqual);
    }

    [Fact]
    public void A_file_whose_images_do_not_match_the_export_is_a_problem_not_a_skip()
    {
        var (glb, rows) = Blenders(new byte[] { 9, 8, 7, 6, 5 });
        var x = Textured(null); x.MaterialRows.Images.Add(("second", "image/png", null, 1));   // one more image than Blender's file holds
        var problems = new List<string>(); var cover = new Dictionary<string, long>();
        GlbRowsCompare.Compare(x, rows, problems, cover, _ => 0, true, true, glb);
        Assert.Contains(problems, p => p.Contains("Blender's file holds 1 image(s), the export 2"));
        Assert.DoesNotContain(cover.Keys, k => k.StartsWith("the GLB's bytes not compared"));
    }

    [Fact]
    public void Reencoded_image_hash_is_checked_against_the_file_instead_of_copied_from_the_row()
    {
        var (glb, rows) = Blenders(new byte[] { 9, 8, 7, 6, 5 });
        rows.Single(r => r[0] == "IMG")[4] = new string('0', 64);
        var x = Textured(null);
        var problems = new List<string>();
        var stats = GlbRowsCompare.Compare(x, rows, problems, new Dictionary<string, long>(), _ => 0, true, true, glb);
        Assert.Contains(problems, p => p.Contains("image") && p.Contains("sha"));
        Assert.False(stats.GlbEqual);
        Assert.Null(x.MaterialRows.Images[0].bytes);
    }

    [Fact]
    public void A_later_missing_image_restores_every_earlier_temporary_image()
    {
        var (glb, rows) = Blenders(new byte[] { 9, 8, 7, 6, 5 });
        int oldLength = System.BitConverter.ToInt32(glb, 12);
        var root = GlbReader.ParseObject(System.Text.Encoding.UTF8.GetString(glb, 20, oldLength));
        ((Newtonsoft.Json.Linq.JArray)root["images"]).Add(new Newtonsoft.Json.Linq.JObject { ["name"] = "second", ["mimeType"] = "image/png" });
        var json = System.Text.Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                int padded = (json.Length + 3) & ~3;
                writer.Write(0x46546c67); writer.Write(2); writer.Write(20 + padded + glb.Length - 20 - oldLength);
                writer.Write(padded); writer.Write(0x4e4f534a); writer.Write(json);
                for (int i = json.Length; i < padded; i++) writer.Write((byte)' ');
                writer.Write(glb, 20 + oldLength, glb.Length - 20 - oldLength);
            }
            glb = stream.ToArray();
        }
        rows.Add(new[] { "IMG", "1", "second", "image/png", new string('0', 64) });
        var x = Textured(null); x.MaterialRows.Images.Add(("second", "image/png", null, 1));
        var problems = new List<string>();
        GlbRowsCompare.Compare(x, rows, problems, new Dictionary<string, long>(), _ => 0, true, true, glb);
        Assert.Contains(problems, p => p.Contains("no bytes for image 1"));
        Assert.All(x.MaterialRows.Images, im => Assert.Null(im.bytes));
    }
}
