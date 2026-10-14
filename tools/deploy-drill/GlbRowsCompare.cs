// GlbRowsCompare.cs - a BlenderDeployExport.Result against the rows glb_rows.py reads back from the GLB Blender wrote:
// the scene, every node (index, name, children, mesh, skin, transform bits), the skins and their inverse bind
// matrices, the meshes and primitives (the material, the mode, the indices' count and hash, every attribute's count,
// type and hash), the materials, textures, samplers and images, the animations (name, channel and sampler counts),
// the channels (part 8b) and the GLB's lengths and SHA-256 (part 8c). Shared by the deploy export drill
// (deploy.exe --export, tools/deploy_drill.sh part 8) and the rig export drill (riganim.exe --export, step (f)).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

static class GlbRowsCompare
{
    public sealed class Stats
    {
        public long Nodes, Joints, Ibms, Meshes, Prims, Vertices, Anims, Materials, Images, Channels, GlbBytes;
        public bool GlbEqual;   // the GLB row (and the file, when given) equal
    }

    static uint Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
    static string H(float f) => Bits(f).ToString("x8");
    static string Sha(byte[] bytes) { using (var sha = System.Security.Cryptography.SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2"))); }
    static string ShaFloats(float[] v) { var b = new byte[4 * v.Length]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, 4 * i); return Sha(b); }
    static string ShaUshorts(ushort[] v) { var b = new byte[2 * v.Length]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, 2 * i); return Sha(b); }
    static string ShaBytes(IEnumerable<byte> v) => Sha(v.ToArray());
    static string Cut(string s) => s.Length > 150 ? s.Substring(0, 150) + "..." : s;
    static void Count(IDictionary<string, long> cover, string key, long n = 1) { cover[key] = (cover.TryGetValue(key, out long v) ? v : 0) + n; }

    public static void Compare(List<string> problems, string what, List<string> mine, List<string> theirs)
    {
        if (mine.SequenceEqual(theirs)) return;
        int i = 0; while (i < mine.Count && i < theirs.Count && mine[i] == theirs[i]) i++;
        problems.Add($"{what}: {mine.Count} here, {theirs.Count} in Blender, first difference at {i + 1}: here «{Cut(i < mine.Count ? mine[i].Replace('\t', ' ') : "(none)")}», Blender «{Cut(i < theirs.Count ? theirs[i].Replace('\t', ' ') : "(none)")}»");
    }

    /// <summary>Compares the export with the dump's rows (the row kind first in each; rows of other kinds are ignored).
    /// `channelsOf` gives an animation's channel count for the ANIM row; `channels` compares the CHAN rows
    /// (x.ChannelRows); `glb` the GLB row through BlenderGlbWriter, and `theirGlb` the file itself when at hand. An
    /// image whose bytes the export does not hold (null) is compared by name and MIME type alone.</summary>
    public static Stats Compare(BlenderDeployExport.Result x, List<string[]> rows, List<string> problems, IDictionary<string, long> cover, Func<string, int> channelsOf, bool channels, bool glb, byte[] theirGlb, List<string> mineOut = null)
    {
        var st = new Stats();
        void Keep(string kind, List<string> mine) { if (mineOut != null) foreach (var r in mine) mineOut.Add(kind + "\t" + r); }
        List<string[]> Of(string k) => rows.Where(t => t[0] == k).ToList();
        // ---- the scene and the nodes
        var scene = Of("SCENE"); if (scene.Count == 0) throw new InvalidDataException("the dump has no SCENE row");
        foreach (var t in scene) if (t.Length != 3) throw new InvalidDataException($"the dump's SCENE row has {t.Length} fields, expected 3");
        var mineScenes = x.Scenes.Select(s => $"{s.name}\t{string.Join(",", s.roots)}").ToList(); Keep("SCENE", mineScenes);
        Compare(problems, "the scenes (name, roots)", mineScenes, scene.Select(t => t[1] + "\t" + t[2]).ToList());
        string V(float[] v) => v == null ? "-" : string.Join("\t", v.Select(H));
        var nodeRows = Of("NODE");
        foreach (var t in nodeRows) if (t.Length < 9) throw new InvalidDataException($"the dump's node row {t[1]} is cut short ({t.Length} fields)");
        var mineNodes = x.Nodes.Select((n, i) => $"{i}\t{n.Name}\t{(n.Children.Count > 0 ? string.Join(",", n.Children) : "-")}\t{(n.Mesh >= 0 ? n.Mesh.ToString() : "-")}\t{(n.Skin >= 0 ? n.Skin.ToString() : "-")}\t{V(n.Translation)}\t{V(n.Rotation)}\t{V(n.Scale)}").ToList();
        Keep("NODE", mineNodes);
        Compare(problems, "the nodes (index, name, children, mesh, skin, translation, rotation, scale)", mineNodes, nodeRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
        st.Nodes = nodeRows.Count;
        // ---- the skins and their inverse bind matrices
        var skinRows = Of("SKIN");
        foreach (var t in skinRows) if (t.Length != 5) throw new InvalidDataException($"the dump's skin row has {t.Length} fields, expected 5");
        var mineSkins = x.Skins.Select((s, i) => $"{i}\t{s.Name}\t{string.Join(",", s.Joints)}\t-").ToList(); Keep("SKIN", mineSkins);
        Compare(problems, "the skins (index, name, joints, skeleton)", mineSkins, skinRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
        st.Joints = skinRows.Sum(t => t[3].Split(',').Length);
        var ibmRows = Of("IBM");
        foreach (var t in ibmRows) if (t.Length != 19) throw new InvalidDataException($"the dump's inverse bind matrix row has {t.Length} fields, expected 19");
        var mineIbm = new List<string>();
        for (int si = 0; si < x.Skins.Count; si++) for (int k = 0; k < x.Skins[si].InverseBind.Count; k++) mineIbm.Add($"{si}\t{k}\t{string.Join("\t", x.Skins[si].InverseBind[k].Select(H))}");
        Keep("IBM", mineIbm);
        Compare(problems, "the inverse bind matrices (skin, joint, 16 floats column by column)", mineIbm, ibmRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
        st.Ibms = ibmRows.Count;
        // ---- the meshes and their primitives: counts, the material, the bytes of every attribute and of the indices
        var meshRows = Of("MESH");
        foreach (var t in meshRows) if (t.Length != 4) throw new InvalidDataException($"the dump's mesh row has {t.Length} fields, expected 4");
        Compare(problems, "the meshes (index, name, primitives)", x.Meshes.Select((me, i) => $"{i}\t{me.Name}\t{me.Primitives.Count}").ToList(), meshRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
        st.Meshes = meshRows.Count;
        var primRows = Of("PRIM");
        foreach (var t in primRows) if (t.Length < 7) throw new InvalidDataException($"the dump's primitive row is cut short ({t.Length} fields)");
        var minePrims = new List<string>();
        for (int mi = 0; mi < x.Meshes.Count; mi++)
            for (int pi = 0; pi < x.Meshes[mi].Primitives.Count; pi++)
            {
                var p = x.Meshes[mi].Primitives[pi];
                var attrs = new SortedDictionary<string, string>(StringComparer.Ordinal);
                attrs["POSITION"] = $"{p.VertexCount}:VEC3/5126:{ShaFloats(p.Positions)}";
                attrs["NORMAL"] = $"{p.VertexCount}:VEC3/5126:{ShaFloats(p.Normals)}";
                for (int u = 0; u < p.Uv.Count; u++) attrs["TEXCOORD_" + u] = $"{p.VertexCount}:VEC2/5126:{ShaFloats(p.Uv[u])}";
                for (int c = 0; c < p.Colors.Count; c++)
                {
                    var set = p.Colors[c];
                    attrs["COLOR_" + c] = set.Forced ? $"{p.VertexCount}:VEC4/5121n:{ShaBytes(Enumerable.Repeat((byte)255, 4 * p.VertexCount))}"
                                        : set.Alpha ? $"{p.VertexCount}:VEC4/5123n:{ShaUshorts(set.Shorts)}" : $"{p.VertexCount}:VEC3/5126:{ShaFloats(set.Data)}";
                }
                if (p.Joints != null)
                {
                    bool bytes = p.Joints.Max() < 256;
                    attrs["JOINTS_0"] = bytes ? $"{p.VertexCount}:VEC4/5121:{ShaBytes(p.Joints.Select(j => (byte)j))}" : $"{p.VertexCount}:VEC4/5123:{ShaUshorts(p.Joints)}";
                    attrs["WEIGHTS_0"] = $"{p.VertexCount}:VEC4/5126:{ShaFloats(p.Weights)}";
                    if (bytes) Count(cover, "joints as bytes (fewer than 256 bones)");
                }
                int maxIndex = p.Indices.Length > 0 ? p.Indices.Max() : 0;
                string indexSha = maxIndex < 65535 ? ShaUshorts(p.Indices.Select(i => (ushort)i).ToArray()) : Sha(p.Indices.SelectMany(i => BitConverter.GetBytes((uint)i)).ToArray());
                int mat = x.Meshes[mi].Material[pi];
                minePrims.Add($"{mi}\t{pi}\t{(mat >= 0 ? mat.ToString() : "-")}\t4\t{p.Indices.Length}\t{indexSha}\t{string.Join("\t", attrs.Select(kv => kv.Key + ":" + kv.Value))}");
                st.Vertices += p.VertexCount;
                Count(cover, mat >= 0 ? "a primitive with a material" : "a primitive without a material");
                Count(cover, p.Uv.Count > 0 ? "a UV layer" : "a mesh without UVs");
            }
        Keep("PRIM", minePrims);
        Compare(problems, "the primitives (mesh, primitive, material, mode, indices, their hash, each attribute's count, type and hash)", minePrims, primRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
        st.Prims = primRows.Count;
        // ---- the materials, textures, samplers and images (8a-ii): structurally for the JSON rows, by name, type and hash for the images
        var mr = x.MaterialRows;
        var matRows = Of("MAT");
        foreach (var t in matRows) if (t.Length != 3) throw new InvalidDataException($"the dump's material row has {t.Length} fields, expected 3");
        Compare(problems, "the materials (index, json)", mr.Materials.Select((jm, i) => $"{i}\t{MaterialDrill.Canonical(jm).ToString(Newtonsoft.Json.Formatting.None)}").ToList(),
                matRows.Select(t => $"{t[1]}\t{MaterialDrill.Canonical(Newtonsoft.Json.Linq.JObject.Parse(t[2])).ToString(Newtonsoft.Json.Formatting.None)}").ToList());
        var texRows = Of("TEX");
        foreach (var t in texRows) if (t.Length != 5) throw new InvalidDataException($"the dump's texture row has {t.Length} fields, expected 5");
        Compare(problems, "the textures (index, source, sampler, other fields)", mr.Textures.Select((t, i) => $"{i}\t{t.image}\t{(t.sampler >= 0 ? t.sampler.ToString() : "-")}\t{{}}").ToList(), texRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
        var sampRows = Of("SAMP");
        foreach (var t in sampRows) if (t.Length != 3) throw new InvalidDataException($"the dump's sampler row has {t.Length} fields, expected 3");
        Compare(problems, "the samplers (index, json)", mr.Samplers.Select((s, i) => $"{i}\t{MaterialDrill.Canonical(s).ToString(Newtonsoft.Json.Formatting.None)}").ToList(),
                sampRows.Select(t => $"{t[1]}\t{MaterialDrill.Canonical(Newtonsoft.Json.Linq.JObject.Parse(t[2])).ToString(Newtonsoft.Json.Formatting.None)}").ToList());
        var imgRows = Of("IMG");
        foreach (var t in imgRows) if (t.Length != 5) throw new InvalidDataException($"the dump's image row has {t.Length} fields, expected 5");
        // an image whose bytes the export does not hold (the rig's albedo, Blender's own PNG) takes the dump's hash: name and MIME type compared
        var mineImages = mr.Images.Select((im, i) => $"{i}\t{im.name}\t{im.mime}\t{(im.bytes != null ? Sha(im.bytes) : i < imgRows.Count && imgRows[i].Length == 5 ? imgRows[i][4] : "-")}").ToList();
        Keep("IMG", mineImages);
        Compare(problems, "the images (index, name, mime, sha256)", mineImages, imgRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
        st.Materials = matRows.Count; st.Images = imgRows.Count;
        // ---- the animations: names and order, the channel and sampler counts
        var animRows = Of("ANIM");
        foreach (var t in animRows) if (t.Length != 5) throw new InvalidDataException($"the dump's animation row has {t.Length} fields, expected 5");
        var mineAnims = x.Animations.Select((a, i) => $"{i}\t{a}\t{channelsOf(a)}\t{channelsOf(a)}").ToList(); Keep("ANIM", mineAnims);
        Compare(problems, "the animations (name, channels, samplers)", mineAnims, animRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
        st.Anims = animRows.Count;
        // ---- the channels (8b): per animation and channel the target node, the path, the sampler, its interpolation,
        //      the input's count, first and last time (float32 hex) and the SHA-256 of the input and output bytes
        if (channels)
        {
            var chanRows = Of("CHAN");
            foreach (var t in chanRows) if (t.Length != 12) throw new InvalidDataException($"the dump's channel row has {t.Length} fields, expected 12");
            var mine = new List<string>();
            for (int ai = 0; ai < x.ChannelRows.Animations.Count; ai++)
            {
                var a = x.ChannelRows.Animations[ai];
                for (int ci = 0; ci < a.Channels.Count; ci++)
                {
                    var c = a.Channels[ci];
                    mine.Add($"{ai}\t{ci}\t{c.Node}\t{c.Path}\t{c.Sampler}\t{c.Interpolation}\t{c.Times.Length}\t{Bits(c.Times[0]):x8}\t{Bits(c.Times[c.Times.Length - 1]):x8}\t{ShaFloats(c.Times)}\t{ShaFloats(c.Values)}");
                }
            }
            Keep("CHAN", mine);
            Compare(problems, "the channels (animation, channel, node, path, sampler, interpolation, count, min, max, input sha, output sha)", mine, chanRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
            st.Channels = chanRows.Count;
        }
        // ---- the bytes (8c): the GLB row's lengths (the file, the JSON chunk padded, the BIN chunk padded), and the file itself when it is at hand
        var glbRow = Of("GLB");
        // The file still exists when its re-encoded image bytes cannot be compared. Require its evidence in either case.
        if (glb && (glbRow.Count != 1 || glbRow[0].Length != 5)) throw new InvalidDataException("the dump has no single valid GLB row");
        if (glb && x.MaterialRows != null && x.MaterialRows.Images.Any(im => im.bytes == null)) { Count(cover, "the GLB's bytes not compared (an image's bytes are Blender's encoder's)"); glb = false; }
        if (glb)
        {
            var written = BlenderGlbWriter.Build(x);
            if (written.Fallback != null) { problems.Add("the writer leaves it: " + written.Fallback); return st; }
            int jsonPadded = (System.Text.Encoding.UTF8.GetByteCount(written.Json) + 3) & ~3, binPadded = (written.Bin.Length + 3) & ~3;
            int before = problems.Count;
            Compare(problems, "the GLB (bytes, JSON chunk, BIN chunk, sha256)", new List<string> { $"{written.Glb.Length}\t{jsonPadded}\t{binPadded}\t{Sha(written.Glb)}" }, new List<string> { string.Join("\t", glbRow[0].Skip(1)) });
            if (theirGlb != null && !theirGlb.SequenceEqual(written.Glb))
            {
                int jl = BitConverter.ToInt32(theirGlb, 12); string theirJson = System.Text.Encoding.UTF8.GetString(theirGlb, 20, jl).TrimEnd(' ');
                if (theirJson != written.Json)
                {
                    int at = 0; while (at < theirJson.Length && at < written.Json.Length && theirJson[at] == written.Json[at]) at++;
                    int lo = Math.Max(0, at - 60);
                    problems.Add($"the JSON differs at {at}: here «{Cut(written.Json.Substring(lo, Math.Min(140, written.Json.Length - lo)))}», Blender «{Cut(theirJson.Substring(lo, Math.Min(140, theirJson.Length - lo)))}»");
                }
                else
                {
                    int off = 0; while (off < theirGlb.Length && off < written.Glb.Length && theirGlb[off] == written.Glb[off]) off++;
                    problems.Add($"the bytes differ at offset {off} of {theirGlb.Length} (the JSON is equal; the BIN chunk starts at {28 + jl})");
                }
            }
            st.GlbEqual = problems.Count == before; st.GlbBytes = written.Glb.Length;
        }
        return st;
    }
}
