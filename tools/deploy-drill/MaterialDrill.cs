// MaterialDrill.cs - BlenderMaterialExport against the materials Blender writes after an import (tools/deploy_drill.sh,
// part 8a-ii). deploy.exe --materials <dump>...
// A dump is what tools/deploy-drill/blender_material_dump.py printed per file: the MAT, IMG, TEX and SAMP rows of the GLB
// the exporter wrote from a factory-fresh import of that file. Compared here: every material's JSON (structurally: the
// writer turns an integral float into an int, so numbers are compared as doubles; keys as written), every texture's
// image and sampler index, every sampler, every image's name, MIME type and bytes - in the order the serializer numbers
// them (first use along the walk, materials in the order the prep's export tree lists them).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

static class MaterialDrill
{
    static readonly HashSet<string> RowKinds = new HashSet<string>(new[]
    {
        "FILE", "DIES", "MAT", "IMG", "TEX", "SAMP", "MATS", "DONE", "FAIL",
    }, StringComparer.Ordinal);

    static readonly string[] CoverKeys =
    {
        "a file compared", "a material with a base colour texture", "a material with a metallic-roughness texture", "a normal texture", "an occlusion texture",
        "a specular extension", "a specular-glossiness material converted", "a clearcoat extension", "an unlit material", "a MASK material", "a BLEND material",
        "a sampler beyond the default", "a JPEG image", "a PNG image", "a material without a name", "a vertex-colour variant", "a file left by name",
    };

    static string Sha(byte[] bytes) { using (var sha = System.Security.Cryptography.SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2"))); }

    /// <summary>The JSON as the drill compares it: an integral number as an integer (export.py __fix_json), keys sorted.</summary>
    internal static JToken Canonical(JToken t)
    {
        switch (t)
        {
            case JObject o: { var r = new JObject(); foreach (var p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)) r.Add(p.Name, Canonical(p.Value)); return r; }
            case JArray a: return new JArray(a.Select(Canonical));
            case JValue v when v.Type == JTokenType.Float: { double d = (double)v; return d == Math.Floor(d) && Math.Abs(d) < 1e15 ? new JValue((long)d) : new JValue(d); }
            default: return t.DeepClone();
        }
    }

    public static int Run(string[] args)
    {
        HashSet<string> expected = null;
        if (args.Length >= 2 && args[0] == "--expected")
        {
            expected = new HashSet<string>(File.ReadLines(args[1]), StringComparer.Ordinal);
            if (expected.Count == 0) { Console.WriteLine("FAIL sources: no expected material files"); return 1; }
            args = args.Skip(2).ToArray();
        }
        var cover = new SortedDictionary<string, long>(); foreach (var k in CoverKeys) cover[k] = 0;
        int fails = 0, files = 0, left = 0, notes = 0; long mats = 0, texs = 0, imgs = 0, samps = 0;
        var blocks = new List<List<string>>();
        foreach (var dump in args)
            foreach (var line in File.ReadLines(dump))
            {
                if (line.StartsWith("FILE\t")) blocks.Add(new List<string>());
                if (blocks.Count > 0) blocks[blocks.Count - 1].Add(line);
                else if (line.Length > 0) { fails++; Console.WriteLine("FAIL dump: a row before the first FILE"); }
            }
        if (blocks.Count == 0) { Console.WriteLine("FAIL dump: no material files to compare"); return 1; }
        var dumped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            string file = block[0].Substring(5);
            files++;
            var problems = new List<string>();
            try
            {
                var rows = block.Select(l => l.Split('\t')).ToList();
                List<string[]> Of(string k) => rows.Where(t => t[0] == k).ToList();
                if (rows.Any(t => t[0] == "FAIL")) throw new InvalidDataException("Blender could not run it: " + rows.First(t => t[0] == "FAIL").Last());
                if (rows.Any(t => !RowKinds.Contains(t[0]))) throw new InvalidDataException("the dump has an unknown row kind");
                if (rows[0].Length != 2 || file.Length == 0) throw new InvalidDataException("the dump has an invalid FILE row");
                if (expected != null && !expected.Contains(file)) throw new InvalidDataException("the dump has an unexpected source file");
                if (!dumped.Add(file)) throw new InvalidDataException("the dump repeats a file");
                var done = Of("DONE");
                if (done.Count == 0) throw new InvalidDataException("the dump has no DONE row");
                if (done.Count != 1 || done[0].Length != 2 || done[0][1] != file || rows[rows.Count - 1] != done[0])
                    throw new InvalidDataException("the dump must end with exactly one matching DONE row");
                var dies = Of("DIES");
                if (dies.Count > 0)
                {
                    if (dies.Count != 1 || dies[0].Length != 2 || dies[0][1].Length == 0) throw new InvalidDataException("the dump has an invalid DIES row");
                    if (rows.Any(t => t[0] != "FILE" && t[0] != "DIES" && t[0] != "DONE"))
                        throw new InvalidDataException("the dump claims exported material rows after a death");
                    notes++; fails++; Console.WriteLine($"FAIL {file}: Blender could not import or export it ({dies[0][1]}); no material comparison was possible"); continue;
                }
                if (Of("MATS").Count != 1 || Of("MATS")[0].Length < 2) throw new InvalidDataException("the dump has no single MATS completion row");
                if (!File.Exists(file)) throw new FileNotFoundException("the source is gone", file);
                foreach (var t in Of("MAT")) if (t.Length != 3) throw new InvalidDataException($"the dump's material row has {t.Length} fields, expected 3");
                foreach (var t in Of("TEX")) if (t.Length != 5) throw new InvalidDataException($"the dump's texture row has {t.Length} fields, expected 5");
                foreach (var t in Of("SAMP")) if (t.Length != 3) throw new InvalidDataException($"the dump's sampler row has {t.Length} fields, expected 3");
                foreach (var t in Of("IMG")) if (t.Length != 6) throw new InvalidDataException($"the dump's image row has {t.Length} fields, expected 6");
                var m = GlbReader.Read(file);
                var names = BlenderNames.Compute(m);
                var x = BlenderMaterialExport.Build(m, names);
                if (x.Fallback != null) { left++; cover["a file left by name"]++; Console.WriteLine($"LEFT {file}: {x.Fallback}"); continue; }
                // ---- the materials, structurally
                var matRows = Of("MAT");
                var mine = x.Materials.Select((j, i) => $"{i}\t{Canonical(j).ToString(Newtonsoft.Json.Formatting.None)}").ToList();
                var theirs = matRows.Select(t => $"{t[1]}\t{Canonical(JObject.Parse(t[2])).ToString(Newtonsoft.Json.Formatting.None)}").ToList();
                Compare(problems, "the materials (index, json)", mine, theirs);
                mats += matRows.Count;
                // ---- the textures, samplers and images
                var texRows = Of("TEX");
                Compare(problems, "the textures (index, source, sampler, other fields)", x.Textures.Select((t, i) => $"{i}\t{t.image}\t{(t.sampler >= 0 ? t.sampler.ToString() : "-")}\t{{}}").ToList(), texRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
                texs += texRows.Count;
                var sampRows = Of("SAMP");
                Compare(problems, "the samplers (index, json)", x.Samplers.Select((s, i) => $"{i}\t{Canonical(s).ToString(Newtonsoft.Json.Formatting.None)}").ToList(), sampRows.Select(t => $"{t[1]}\t{Canonical(JObject.Parse(t[2])).ToString(Newtonsoft.Json.Formatting.None)}").ToList());
                samps += sampRows.Count;
                var imgRows = Of("IMG");
                Compare(problems, "the images (index, name, mime, bytes, sha256)", x.Images.Select((im, i) => $"{i}\t{im.name}\t{im.mime}\t{im.bytes.Length}\t{Sha(im.bytes)}").ToList(), imgRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
                imgs += imgRows.Count;
                if (problems.Count > 0) { fails++; Console.WriteLine($"FAIL {file}: " + string.Join("; ", problems.Take(4))); continue; }
                cover["a file compared"]++;
                foreach (var j in x.Materials)
                {
                    var pbr = j["pbrMetallicRoughness"] as JObject; var ext = j["extensions"] as JObject;
                    if (pbr?["baseColorTexture"] != null) cover["a material with a base colour texture"]++;
                    if (pbr?["metallicRoughnessTexture"] != null) cover["a material with a metallic-roughness texture"]++;
                    if (j["normalTexture"] != null) cover["a normal texture"]++;
                    if (j["occlusionTexture"] != null) cover["an occlusion texture"]++;
                    if (ext?["KHR_materials_specular"] != null) cover["a specular extension"]++;
                    if (ext?["KHR_materials_clearcoat"] != null) cover["a clearcoat extension"]++;
                    if (ext?["KHR_materials_unlit"] != null) cover["an unlit material"]++;
                    if ((string)j["alphaMode"] == "MASK") cover["a MASK material"]++;
                    if ((string)j["alphaMode"] == "BLEND") cover["a BLEND material"]++;
                }
                cover["a specular-glossiness material converted"] += x.SpecularGlossiness;
                cover["a material without a name"] += x.Unnamed;
                cover["a vertex-colour variant"] += x.VertexColourVariants;
                foreach (var s in x.Samplers) if (s.Count > 2) cover["a sampler beyond the default"]++;
                foreach (var im in x.Images) cover[im.mime == "image/jpeg" ? "a JPEG image" : "a PNG image"]++;
                Console.WriteLine($"PASS {file}: {matRows.Count} materials, {texRows.Count} textures, {sampRows.Count} samplers, {imgRows.Count} images equal to Blender's");
            }
            catch (Exception e) { fails++; Console.WriteLine($"FAIL {file}: {e.GetType().Name}: {e.Message}"); }
        }
        if (expected != null) foreach (string file in expected)
            if (!dumped.Contains(file)) { fails++; Console.WriteLine($"FAIL {file}: the dump holds no such source file"); }
        foreach (var kv in cover) Console.WriteLine($"COVER {kv.Value} {kv.Key}");
        Console.WriteLine($"TOTAL files {files} failed {fails} left {left} notes {notes} materials {mats} textures {texs} samplers {samps} images {imgs}");
        return fails == 0 ? 0 : 1;
    }

    static void Compare(List<string> problems, string what, List<string> mine, List<string> theirs)
    {
        if (mine.SequenceEqual(theirs)) return;
        int i = 0; while (i < mine.Count && i < theirs.Count && mine[i] == theirs[i]) i++;
        string Cut(string s) => s.Length > 260 ? s.Substring(0, 260) + "..." : s;
        problems.Add($"{what}: {mine.Count} here, {theirs.Count} in Blender, first difference at {i + 1}: here «{Cut(i < mine.Count ? mine[i].Replace('\t', ' ') : "(none)")}», Blender «{Cut(i < theirs.Count ? theirs[i].Replace('\t', ' ') : "(none)")}»");
    }
}
