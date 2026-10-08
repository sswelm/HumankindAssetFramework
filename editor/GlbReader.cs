using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

// THE GLB / glTF READER (2026-09-30, step 1 of replacing Blender). Fills a HafModel from a .glb (the binary container:
// a JSON chunk and a BIN chunk) or a .gltf (JSON, with buffers and images embedded as data URIs or beside the file).
// Pure C# over Newtonsoft (the one JSON library the editor already ships).
//
// THE CONTRACT: everything a file carries that the model has a place for is read exactly (every component type,
// normalized integers, interleaved buffer views); anything the reader does not implement is REFUSED BY NAME, never
// dropped in silence - sparse accessors, Draco or any other required extension, a buffer that is not where the file
// says, an accessor that reaches past its view. A model that reads is a model that was read whole; the tests lock the
// messages. Morph targets are the one thing declared but not read, and the primitive says how many it had.
//
// Measured on 2026-09-30 for the plan: Blender's glTF import of the 50.9 MB UniversalTanks source takes 1.2 s after a
// 1.6 s boot; the parity drill (tools/glb_reader_drill.sh) times this reader on every registry GLB beside it.
public static class GlbReader
{
    const uint Magic = 0x46546C67;          // "glTF"
    const uint ChunkJson = 0x4E4F534A;      // "JSON"
    const uint ChunkBin = 0x004E4942;       // "BIN\0"

    // No required extension is implemented, so every one is a refusal (review of PR #109: a whitelist of "material-only"
    // extensions was accepted while their payloads were never read - a specular-glossiness material has no base colour
    // to read, a texture transform moves the texture). A file that merely USES an extension reads; what the extension
    // adds is not modelled and the ExtensionsUsed list says so.

    public static HafModel Read(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("no path");
        byte[] bytes = File.ReadAllBytes(path);
        bool isGlb = bytes.Length >= 12 && BitConverter.ToUInt32(bytes, 0) == Magic;
        var model = isGlb ? ReadGlb(bytes, Path.GetDirectoryName(path)) : ReadGltf(Encoding.UTF8.GetString(bytes), Path.GetDirectoryName(path));
        model.SourcePath = path;
        return model;
    }

    /// <summary>A .glb held in memory (the tests' path; a .gltf is text and goes through <see cref="ReadGltf"/>).</summary>
    public static HafModel Read(byte[] glb) => ReadGlb(glb, null);

    public static HafModel ReadGlb(byte[] bytes, string baseDir)
    {
        if (bytes == null || bytes.Length < 12 || BitConverter.ToUInt32(bytes, 0) != Magic) throw new InvalidDataException("not a GLB: the file does not start with the glTF magic");
        uint version = BitConverter.ToUInt32(bytes, 4);
        if (version != 2) throw new InvalidDataException($"GLB version {version} is not supported (only version 2)");
        uint length = BitConverter.ToUInt32(bytes, 8);
        if (length > bytes.Length) throw new InvalidDataException($"GLB header says {length} bytes, the file has {bytes.Length}");
        string json = null; Segment bin = null;
        int at = 12;
        while (at + 8 <= length)
        {
            uint chunkLen = BitConverter.ToUInt32(bytes, at), chunkType = BitConverter.ToUInt32(bytes, at + 4);
            at += 8;
            if (at + chunkLen > length) throw new InvalidDataException($"GLB chunk 0x{chunkType:X8} of {chunkLen} bytes runs past the file");
            if (chunkType == ChunkJson && json == null) json = Encoding.UTF8.GetString(bytes, at, (int)chunkLen);
            else if (chunkType == ChunkBin && bin == null) bin = new Segment { Data = bytes, Base = at, Length = (int)chunkLen };   // a VIEW of the file's bytes, not a copy: a 398 MB source held twice ran Mono out of memory
            at += (int)chunkLen;
        }
        if (json == null) throw new InvalidDataException("GLB has no JSON chunk");
        return Build(ParseObject(json), bin, baseDir);
    }

    public static HafModel ReadGltf(string jsonText, string baseDir) => Build(ParseObject(jsonText), null, baseDir);

    /// <summary>A buffer: bytes [Base, Base + Length) of Data - the GLB's BIN chunk inside the file's own array, or a whole sidecar.</summary>
    sealed class Segment { public byte[] Data; public int Base, Length; }

    /// <summary>JSON text to a JObject with the text's strings kept as strings: Newtonsoft's default turns a string
    /// that looks like a date into a DateTime and writes it back in its own form (an extras value
    /// "2024-05-06T07:08:09.123+09:00" came back "2024-05-06T00:08:09.123+02:00", the machine's zone - review of PR #110). Used for the file
    /// and for every verbatim fragment the model carries (extensions, extras, samplers).</summary>
    public static JObject ParseObject(string json) => ParseToken(json) as JObject ?? throw new InvalidDataException("the JSON is not an object");

    /// <summary>Any JSON value, strings kept as strings (see <see cref="ParseObject"/>).</summary>
    public static JToken ParseToken(string json)
    {
        using (var r = new Newtonsoft.Json.JsonTextReader(new StringReader(json)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
        {
            var t = JToken.ReadFrom(r);
            if (r.Read()) throw new InvalidDataException("the JSON has text after its value");
            return t;
        }
    }

    /// <summary>An object's `extras` as verbatim JSON text, whatever its type (the schema allows any: an array, a
    /// string, a number, an empty object are all valid and all kept), or null when there is none.</summary>
    static string Extras(JToken owner)
    {
        var t = owner?["extras"];
        return t == null || t.Type == JTokenType.None ? null : t.ToString(Newtonsoft.Json.Formatting.None);
    }

    // ---------------------------------------------------------------- the build

    static HafModel Build(JObject root, Segment glbBin, string baseDir)
    {
        var model = new HafModel { Generator = root["asset"]?["generator"]?.ToString() ?? "", Copyright = root["asset"]?["copyright"]?.ToString() ?? "" };
        model.AssetExtrasJson = Extras(root["asset"] as JObject);
        foreach (var e in root["extensionsUsed"] as JArray ?? new JArray()) model.ExtensionsUsed.Add(e.ToString());
        foreach (var e in root["extensionsRequired"] as JArray ?? new JArray())
        {
            // an extension whose whole effect is a MATERIAL's payload is read and carried: the geometry, skins and
            // animations are core glTF, and the payload travels verbatim in HafMaterial.ExtensionsJson for whoever draws
            // the material (a Lab source requires KHR_materials_pbrSpecularGlossiness: its colours are in that payload,
            // not in pbrMetallicRoughness - the preview shows such a material untextured). Everything else changes how
            // the DATA is decoded or mapped and is refused.
            string name = e.ToString();
            if (name.StartsWith("KHR_materials_", StringComparison.Ordinal) && name != "KHR_materials_variants") { model.ExtensionsRequired.Add(name); continue; }
            throw new InvalidDataException($"the file requires the extension '{name}', which this reader does not implement (KHR_draco_mesh_compression needs a Draco decoder; KHR_texture_transform moves the textures; KHR_materials_variants lives on primitives) - refused rather than read wrong");
        }

        var buffers = LoadBuffers(root, glbBin, baseDir);
        var acc = new Accessors(root, buffers);

        // images, textures, materials
        foreach (var im in root["images"] as JArray ?? new JArray())
        {
            var image = new HafImage { Name = im["name"]?.ToString() ?? "", MimeType = im["mimeType"]?.ToString() ?? "" };
            if (im["bufferView"] != null)
            {
                var view = acc.View(im.Value<int>("bufferView"));
                image.Bytes = new byte[view.Length]; Buffer.BlockCopy(view.Buffer, view.Offset, image.Bytes, 0, view.Length);
            }
            else if (im["uri"] != null)
            {
                string uri = im["uri"].ToString();
                image.Uri = uri.StartsWith("data:", StringComparison.Ordinal) ? uri.Substring(0, Math.Min(uri.Length, 40)) + "…" : uri;
                image.Bytes = ResolveUri(uri, baseDir, out string mime) ?? throw new InvalidDataException($"image {model.Images.Count} '{image.Name}': '{uri}' could not be found beside the file (review of PR #109: a model without its texture is not a model that was read whole)");
                if (image.MimeType.Length == 0 && mime != null) image.MimeType = mime;
            }
            model.Images.Add(image);
        }
        foreach (var cam in root["cameras"] as JArray ?? new JArray()) model.Cameras.Add(cam is JObject co ? co.ToString(Newtonsoft.Json.Formatting.None) : "{}");
        foreach (var sp in root["samplers"] as JArray ?? new JArray()) model.Samplers.Add(sp is JObject so ? so.ToString(Newtonsoft.Json.Formatting.None) : "{}");
        foreach (var tx in root["textures"] as JArray ?? new JArray())
            model.Textures.Add(new HafTexture { Name = tx["name"]?.ToString() ?? "", Source = tx["source"]?.Value<int>() ?? -1, Sampler = tx["sampler"]?.Value<int>() ?? -1 });
        foreach (var mt in root["materials"] as JArray ?? new JArray())
        {
            var m = new HafMaterial { Name = mt["name"]?.ToString() ?? "", NameAbsent = mt["name"] == null || mt["name"].Type == JTokenType.Null };   // a JSON null is no name to the importer (from_none): Material_<index>
            var pbr = mt["pbrMetallicRoughness"] as JObject;
            if (pbr != null)
            {
                if (pbr["baseColorFactor"] is JArray bcf) m.BaseColorFactor = Doubles(bcf, 4, "baseColorFactor");
                TexRef(pbr["baseColorTexture"], out m.BaseColorTexture, out m.BaseColorTexCoord);
                m.MetallicFactor = pbr["metallicFactor"]?.Value<float>() ?? 1f;
                m.RoughnessFactor = pbr["roughnessFactor"]?.Value<float>() ?? 1f;
                TexRef(pbr["metallicRoughnessTexture"], out m.MetallicRoughnessTexture, out m.MetallicRoughnessTexCoord);
            }
            TexRef(mt["normalTexture"], out m.NormalTexture, out m.NormalTexCoord);
            m.NormalScale = mt["normalTexture"]?["scale"]?.Value<float>() ?? 1f;
            TexRef(mt["occlusionTexture"], out m.OcclusionTexture, out m.OcclusionTexCoord);
            m.OcclusionStrength = mt["occlusionTexture"]?["strength"]?.Value<float>() ?? 1f;
            TexRef(mt["emissiveTexture"], out m.EmissiveTexture, out m.EmissiveTexCoord);
            if (mt["emissiveFactor"] is JArray ef) m.EmissiveFactor = Floats(ef, 3, "emissiveFactor");
            m.AlphaMode = mt["alphaMode"]?.ToString() ?? "OPAQUE";
            m.AlphaCutoff = mt["alphaCutoff"]?.Value<double>() ?? 0.5;
            m.DoubleSided = mt["doubleSided"]?.Value<bool>() ?? false;
            if (mt["extensions"] is JObject ext && ext.Count > 0) m.ExtensionsJson = ext.ToString(Newtonsoft.Json.Formatting.None);
            m.ExtrasJson = Extras(mt);
            model.Materials.Add(m);
        }

        // meshes
        int meshIndex = 0;
        foreach (var me in root["meshes"] as JArray ?? new JArray())
        {
            var mesh = new HafMesh { Name = me["name"]?.ToString() ?? "" };
            mesh.ExtrasJson = Extras(me);
            int primIndex = 0;
            foreach (var pr in me["primitives"] as JArray ?? new JArray())
            {
                string where = $"mesh {meshIndex} '{mesh.Name}' primitive {primIndex}";
                var attrs = pr["attributes"] as JObject ?? throw new InvalidDataException($"{where} has no attributes");
                if (attrs["POSITION"] == null) throw new InvalidDataException($"{where} has no POSITION");
                var p = new HafPrimitive { Mode = pr["mode"]?.Value<int>() ?? 4, Material = pr["material"]?.Value<int>() ?? -1 };
                p.Positions = acc.Floats(attrs.Value<int>("POSITION"), 3, where + " POSITION");
                p.VertexCount = p.Positions.Length / 3;
                if (attrs["NORMAL"] != null) p.Normals = Sized(acc.Floats(attrs.Value<int>("NORMAL"), 3, where + " NORMAL"), p.VertexCount * 3, where + " NORMAL");
                if (attrs["TANGENT"] != null) p.Tangents = Sized(acc.Floats(attrs.Value<int>("TANGENT"), 4, where + " TANGENT"), p.VertexCount * 4, where + " TANGENT");
                if (attrs["TEXCOORD_0"] != null) p.Uv0 = Sized(acc.Floats(attrs.Value<int>("TEXCOORD_0"), 2, where + " TEXCOORD_0"), p.VertexCount * 2, where + " TEXCOORD_0");
                if (attrs["TEXCOORD_1"] != null) p.Uv1 = Sized(acc.Floats(attrs.Value<int>("TEXCOORD_1"), 2, where + " TEXCOORD_1"), p.VertexCount * 2, where + " TEXCOORD_1");
                if (attrs["COLOR_0"] != null) p.Colors = Sized(acc.Colors(attrs.Value<int>("COLOR_0"), where + " COLOR_0"), p.VertexCount * 4, where + " COLOR_0");
                // the further sets, consecutive from 2 (UVs) and 1 (colours) as Blender's importer counts them (step 5 c: the
                // Decimate port needs every layer Blender holds; 7 registry files carry TEXCOORD_2, the Ehrhardt COLOR_1)
                for (int t = 2; attrs["TEXCOORD_" + t] != null; t++) (p.UvMore ?? (p.UvMore = new List<float[]>())).Add(Sized(acc.Floats(attrs.Value<int>("TEXCOORD_" + t), 2, where + " TEXCOORD_" + t), p.VertexCount * 2, where + " TEXCOORD_" + t));
                for (int t = 1; attrs["COLOR_" + t] != null; t++) (p.ColorMore ?? (p.ColorMore = new List<float[]>())).Add(Sized(acc.Colors(attrs.Value<int>("COLOR_" + t), where + " COLOR_" + t), p.VertexCount * 4, where + " COLOR_" + t));
                if (attrs["JOINTS_0"] != null) p.Joints = Sized(acc.Ushorts(attrs.Value<int>("JOINTS_0"), 4, where + " JOINTS_0"), p.VertexCount * 4, where + " JOINTS_0");
                if (attrs["WEIGHTS_0"] != null) p.Weights = Sized(acc.Floats(attrs.Value<int>("WEIGHTS_0"), 4, where + " WEIGHTS_0"), p.VertexCount * 4, where + " WEIGHTS_0");
                if (attrs["JOINTS_1"] != null) p.Joints1 = Sized(acc.Ushorts(attrs.Value<int>("JOINTS_1"), 4, where + " JOINTS_1"), p.VertexCount * 4, where + " JOINTS_1");
                if (attrs["WEIGHTS_1"] != null) p.Weights1 = Sized(acc.Floats(attrs.Value<int>("WEIGHTS_1"), 4, where + " WEIGHTS_1"), p.VertexCount * 4, where + " WEIGHTS_1");
                if ((p.Joints1 == null) != (p.Weights1 == null)) throw new InvalidDataException($"{where} has one of JOINTS_1 / WEIGHTS_1 without the other");
                if (attrs["JOINTS_2"] != null || attrs["WEIGHTS_2"] != null) throw new InvalidDataException($"{where} has more than eight influences per vertex (JOINTS_2), which this reader does not carry - refused rather than dropped");
                if (pr["indices"] != null)
                {
                    p.Indices = acc.Ints(pr.Value<int>("indices"), where + " indices");
                    foreach (int i in p.Indices) if (i < 0 || i >= p.VertexCount) throw new InvalidDataException($"{where}: index {i} is outside its {p.VertexCount} vertices");
                }
                p.MorphTargets = (pr["targets"] as JArray)?.Count ?? 0;
                mesh.Primitives.Add(p);
                primIndex++;
            }
            model.Meshes.Add(mesh);
            meshIndex++;
        }

        // nodes and the hierarchy
        var nodes = root["nodes"] as JArray ?? new JArray();
        foreach (var nd in nodes)
        {
            var n = new HafNode { Name = nd["name"]?.ToString() ?? "", Mesh = nd["mesh"]?.Value<int>() ?? -1, Skin = nd["skin"]?.Value<int>() ?? -1 };
            if (nd["matrix"] is JArray mx) n.Matrix = Doubles(mx, 16, "node matrix");
            if (nd["translation"] is JArray t) n.Translation = Doubles(t, 3, "node translation");
            if (nd["rotation"] is JArray r) n.Rotation = Doubles(r, 4, "node rotation");
            if (nd["scale"] is JArray s) n.Scale = Doubles(s, 3, "node scale");
            n.ExtrasJson = Extras(nd);
            if (nd["camera"] != null)
            {
                n.Camera = nd.Value<int>("camera");
                if (n.Camera < 0 || n.Camera >= model.Cameras.Count) throw new InvalidDataException($"node '{n.Name}' uses camera {n.Camera}, the file has {model.Cameras.Count}");
            }
            if (n.Mesh >= model.Meshes.Count) throw new InvalidDataException($"node '{n.Name}' references mesh {n.Mesh}, the file has {model.Meshes.Count}");
            model.Nodes.Add(n);
        }
        for (int i = 0; i < nodes.Count; i++)
            foreach (var c in nodes[i]["children"] as JArray ?? new JArray())
            {
                int ci = c.Value<int>();
                if (ci < 0 || ci >= model.Nodes.Count) throw new InvalidDataException($"node {i} lists child {ci}, the file has {model.Nodes.Count} nodes");
                if (model.Nodes[ci].Parent != -1) throw new InvalidDataException($"node {ci} has two parents ({model.Nodes[ci].Parent} and {i})");
                model.Nodes[ci].Parent = i;
                model.Nodes[i].Children.Add(ci);
            }
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            int at = i, steps = 0;
            while (model.Nodes[at].Parent >= 0) { at = model.Nodes[at].Parent; if (++steps > model.Nodes.Count) throw new InvalidDataException($"node {i} '{model.Nodes[i].Name}' is its own ancestor - the hierarchy has a cycle"); }
        }
        // every scene, verbatim; the default by index; a file with none gets one of its parentless nodes
        foreach (var sc in root["scenes"] as JArray ?? new JArray())
        {
            var scene = new HafScene { Name = sc["name"]?.ToString() ?? "", ExtrasJson = Extras(sc) };
            foreach (var sn in sc["nodes"] as JArray ?? new JArray())
            {
                int ni = sn.Value<int>();
                if (ni < 0 || ni >= model.Nodes.Count) throw new InvalidDataException($"scene {model.Scenes.Count} '{scene.Name}' lists node {ni}, the file has {model.Nodes.Count}");
                scene.Nodes.Add(ni);
            }
            model.Scenes.Add(scene);
        }
        model.Scene = root["scene"]?.Value<int>() ?? -1;   // absent is absent: a file that names no default scene shows nothing at load, by the specification
        if (model.Scene != -1 && (model.Scene < 0 || model.Scene >= model.Scenes.Count)) throw new InvalidDataException($"the default scene is {model.Scene}, the file has {model.Scenes.Count}");

        // skins
        foreach (var sk in root["skins"] as JArray ?? new JArray())
        {
            var skin = new HafSkin { Name = sk["name"]?.ToString() ?? "", Skeleton = sk["skeleton"]?.Value<int>() ?? -1 };
            var joints = sk["joints"] as JArray ?? throw new InvalidDataException($"skin '{skin.Name}' has no joints");
            skin.Joints = new int[joints.Count];
            for (int i = 0; i < joints.Count; i++)
            {
                skin.Joints[i] = joints[i].Value<int>();
                if (skin.Joints[i] < 0 || skin.Joints[i] >= model.Nodes.Count) throw new InvalidDataException($"skin '{skin.Name}' joint {i} is node {skin.Joints[i]}, the file has {model.Nodes.Count} nodes");
            }
            if (sk["inverseBindMatrices"] != null)
            {
                skin.InverseBindMatrices = acc.Doubles(sk.Value<int>("inverseBindMatrices"), 16, $"skin '{skin.Name}' inverseBindMatrices");
                if (skin.InverseBindMatrices.Length != 16 * joints.Count) throw new InvalidDataException($"skin '{skin.Name}' has {joints.Count} joints but {skin.InverseBindMatrices.Length / 16} inverse bind matrices");
            }
            model.Skins.Add(skin);
        }
        foreach (var n in model.Nodes) if (n.Skin >= model.Skins.Count) throw new InvalidDataException($"node '{n.Name}' references skin {n.Skin}, the file has {model.Skins.Count}");

        // animations
        foreach (var an in root["animations"] as JArray ?? new JArray())
        {
            var anim = new HafAnimation { Name = an["name"]?.ToString() ?? "" };
            var samplers = an["samplers"] as JArray ?? new JArray();
            var channels = an["channels"] as JArray ?? new JArray();
            var paths = new string[samplers.Count];   // a sampler's output width comes from the channel that uses it
            foreach (var ch in channels)
            {
                int si = ch.Value<int>("sampler");
                if (si < 0 || si >= samplers.Count) throw new InvalidDataException($"animation '{anim.Name}' channel uses sampler {si}, it has {samplers.Count}");
                string path = ch["target"]?["path"]?.ToString() ?? "";
                if (paths[si] != null && paths[si] != path) throw new InvalidDataException($"animation '{anim.Name}' sampler {si} drives both '{paths[si]}' and '{path}'");
                paths[si] = path;
            }
            for (int i = 0; i < samplers.Count; i++)
            {
                var sp = samplers[i];
                var s = new HafSampler { Interpolation = sp["interpolation"]?.ToString() ?? "LINEAR" };
                string where = $"animation '{anim.Name}' sampler {i}";
                s.Times = acc.Floats(sp.Value<int>("input"), 1, where + " input");
                for (int k = 1; k < s.Times.Length; k++) if (s.Times[k] < s.Times[k - 1]) throw new InvalidDataException($"{where}: input times are not ascending at key {k}");
                int outAcc = sp.Value<int>("output");
                string type = acc.TypeOf(outAcc);
                s.Components = type == "SCALAR" ? 1 : type == "VEC3" ? 3 : type == "VEC4" ? 4 : throw new InvalidDataException($"{where}: output type {type} is not one an animation carries");
                s.Values = acc.Floats(outAcc, s.Components, where + " output");
                int perKey = s.Interpolation == "CUBICSPLINE" ? 3 : 1;
                if (paths[i] == "weights")
                {
                    // morph weights: SCALAR output, (targets × keys × perKey) values - the width is whatever divides
                    if (s.Times.Length > 0 && s.Values.Length % (s.Times.Length * perKey) != 0) throw new InvalidDataException($"{where}: {s.Values.Length} weight values do not divide over {s.Times.Length} keys");
                    s.Components = s.Times.Length == 0 ? 0 : s.Values.Length / (s.Times.Length * perKey);
                }
                else if (s.Values.Length != s.Times.Length * perKey * s.Components)
                    throw new InvalidDataException($"{where}: {s.Times.Length} keys but {s.Values.Length / Math.Max(1, s.Components)} values ({s.Interpolation})");
                if (s.Times.Length > 0) anim.Duration = Math.Max(anim.Duration, s.Times[s.Times.Length - 1]);
                anim.Samplers.Add(s);
            }
            foreach (var ch in channels)
            {
                var c = new HafChannel { Sampler = ch.Value<int>("sampler"), Node = ch["target"]?["node"]?.Value<int>() ?? -1, Path = ch["target"]?["path"]?.ToString() ?? "" };
                if (c.Node >= model.Nodes.Count) throw new InvalidDataException($"animation '{anim.Name}' targets node {c.Node}, the file has {model.Nodes.Count}");
                anim.Channels.Add(c);
            }
            model.Animations.Add(anim);
        }
        return model;
    }

    // ---------------------------------------------------------------- buffers and URIs

    sealed class BufferView { public byte[] Buffer; public int Offset, Length, Stride; }

    static List<Segment> LoadBuffers(JObject root, Segment glbBin, string baseDir)
    {
        var list = new List<Segment>();
        int i = 0;
        foreach (var b in root["buffers"] as JArray ?? new JArray())
        {
            long declared = b["byteLength"]?.Value<long>() ?? 0;
            Segment data;
            if (b["uri"] == null)
            {
                data = glbBin ?? throw new InvalidDataException($"buffer {i} has no uri and there is no BIN chunk");
            }
            else
            {
                var whole = ResolveUri(b["uri"].ToString(), baseDir, out _) ?? throw new InvalidDataException($"buffer {i}: '{b["uri"]}' could not be found beside the file");
                data = new Segment { Data = whole, Base = 0, Length = whole.Length };
            }
            if (data.Length < declared) throw new InvalidDataException($"buffer {i} declares {declared} bytes but holds {data.Length}");
            list.Add(data);
            i++;
        }
        return list;
    }

    static byte[] ResolveUri(string uri, string baseDir, out string mime)
    {
        mime = null;
        if (uri.StartsWith("data:", StringComparison.Ordinal))
        {
            int comma = uri.IndexOf(',');
            if (comma < 0) throw new InvalidDataException("malformed data URI");
            string header = uri.Substring(5, comma - 5);
            if (!header.EndsWith(";base64", StringComparison.Ordinal)) throw new InvalidDataException("a data URI that is not base64 is not supported");
            mime = header.Substring(0, header.Length - ";base64".Length);
            return Convert.FromBase64String(uri.Substring(comma + 1));
        }
        if (baseDir == null) return null;
        string path = Path.Combine(baseDir, Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    // ---------------------------------------------------------------- accessors (the per-accessor layout, resolved once)

    sealed class Accessors
    {
        readonly JArray accessors, views;
        readonly List<Segment> buffers;
        sealed class Layout { public int Count, ComponentType, ComponentBytes, Components, Stride, Offset; public byte[] Buffer; public bool Normalized; public string Type; }
        readonly Dictionary<int, Layout> layouts = new Dictionary<int, Layout>();

        public Accessors(JObject root, List<Segment> buffers)
        {
            accessors = root["accessors"] as JArray ?? new JArray();
            views = root["bufferViews"] as JArray ?? new JArray();
            this.buffers = buffers;
        }

        public BufferView View(int index)
        {
            if (index < 0 || index >= views.Count) throw new InvalidDataException($"bufferView {index} does not exist ({views.Count} views)");
            var v = views[index];
            int bi = v.Value<int>("buffer");
            if (bi < 0 || bi >= buffers.Count) throw new InvalidDataException($"bufferView {index} uses buffer {bi}, the file has {buffers.Count}");
            var seg = buffers[bi];
            int viewOffset = v["byteOffset"]?.Value<int>() ?? 0;
            var bv = new BufferView { Buffer = seg.Data, Offset = seg.Base + viewOffset, Length = v.Value<int>("byteLength"), Stride = v["byteStride"]?.Value<int>() ?? 0 };
            if (viewOffset < 0 || bv.Length < 0 || (long)viewOffset + bv.Length > seg.Length) throw new InvalidDataException($"bufferView {index} ({viewOffset}+{bv.Length}) runs past buffer {bi} ({seg.Length} bytes)");
            return bv;
        }

        public string TypeOf(int accessorIndex) => Resolve(accessorIndex).Type;

        Layout Resolve(int index)
        {
            if (layouts.TryGetValue(index, out var l)) return l;
            if (index < 0 || index >= accessors.Count) throw new InvalidDataException($"accessor {index} does not exist ({accessors.Count} accessors)");
            var a = accessors[index];
            if (a["sparse"] != null) throw new InvalidDataException($"accessor {index} is sparse, which this reader does not implement - refused rather than read wrong");
            l = new Layout { Count = a.Value<int>("count"), ComponentType = a.Value<int>("componentType"), Type = a["type"]?.ToString() ?? "", Normalized = a["normalized"]?.Value<bool>() ?? false };
            l.ComponentBytes = l.ComponentType == 5120 || l.ComponentType == 5121 ? 1 : l.ComponentType == 5122 || l.ComponentType == 5123 ? 2 : l.ComponentType == 5125 || l.ComponentType == 5126 ? 4
                             : throw new InvalidDataException($"accessor {index}: component type {l.ComponentType} is not one glTF defines");
            l.Components = l.Type == "SCALAR" ? 1 : l.Type == "VEC2" ? 2 : l.Type == "VEC3" ? 3 : l.Type == "VEC4" ? 4 : l.Type == "MAT2" ? 4 : l.Type == "MAT3" ? 9 : l.Type == "MAT4" ? 16
                           : throw new InvalidDataException($"accessor {index}: type '{l.Type}' is not one glTF defines");
            if (a["bufferView"] == null)
            {
                // allowed by the spec: all zeros (used with sparse, which we refuse above, or as a zero-filled attribute)
                l.Buffer = new byte[l.Count * l.Components * l.ComponentBytes]; l.Offset = 0; l.Stride = l.Components * l.ComponentBytes;
            }
            else
            {
                var v = View(a.Value<int>("bufferView"));
                l.Buffer = v.Buffer; l.Offset = v.Offset + (a["byteOffset"]?.Value<int>() ?? 0);
                l.Stride = v.Stride > 0 ? v.Stride : l.Components * l.ComponentBytes;
                long last = (long)l.Offset + (l.Count == 0 ? 0 : (long)(l.Count - 1) * l.Stride + l.Components * l.ComponentBytes);
                if (l.Count > 0 && (l.Offset < v.Offset || last > (long)v.Offset + v.Length)) throw new InvalidDataException($"accessor {index} ({l.Count} × {l.Type}) reaches past its bufferView");
            }
            layouts[index] = l;
            return l;
        }

        double Component(Layout l, int elementIndex, int component)
        {
            int at = l.Offset + elementIndex * l.Stride + component * l.ComponentBytes;
            byte[] d = l.Buffer;
            switch (l.ComponentType)
            {
                case 5120: { sbyte v = unchecked((sbyte)d[at]); return l.Normalized ? Math.Max(v / 127.0, -1.0) : v; }
                case 5121: { byte v = d[at]; return l.Normalized ? v / 255.0 : v; }
                case 5122: { short v = BitConverter.ToInt16(d, at); return l.Normalized ? Math.Max(v / 32767.0, -1.0) : v; }
                case 5123: { ushort v = BitConverter.ToUInt16(d, at); return l.Normalized ? v / 65535.0 : v; }
                case 5125: return BitConverter.ToUInt32(d, at);
                default: return BitConverter.ToSingle(d, at);
            }
        }

        public float[] Floats(int index, int expectedComponents, string where)
        {
            var l = Resolve(index);
            if (expectedComponents > 0 && l.Components != expectedComponents) throw new InvalidDataException($"{where}: accessor {index} is {l.Type}, {expectedComponents} components were expected");
            return Decoded(index, 0, () =>
            {
                var outp = new float[l.Count * l.Components];
                for (int e = 0, o = 0; e < l.Count; e++) for (int c = 0; c < l.Components; c++) outp[o++] = (float)Component(l, e, c);
                return outp;
            });
        }

        // ONE array per accessor: primitives (and animation samplers) that reference the same accessor share it. A Workshop
        // split writes every part over its parent's vertex accessor - a 398 MB Lab source has 2,133 primitives over 162
        // accessors, 8.2 M vertices that decoded one copy per primitive came to 125 M (4 GB, "Insufficient memory").
        // Consumers treat a primitive's arrays as read-only, or copy before editing (HafModel says so).
        readonly Dictionary<long, Array> decoded = new Dictionary<long, Array>();
        T[] Decoded<T>(int index, int kind, Func<T[]> decode)
        {
            long key = (long)index * 8 + kind;
            if (decoded.TryGetValue(key, out var have)) return (T[])have;
            var d = decode(); decoded[key] = d; return d;
        }

        public double[] Doubles(int index, int expectedComponents, string where)
        {
            var l = Resolve(index);
            if (l.Components != expectedComponents) throw new InvalidDataException($"{where}: accessor {index} is {l.Type}, {expectedComponents} components were expected");
            var outp = new double[l.Count * l.Components];
            for (int e = 0, o = 0; e < l.Count; e++) for (int c = 0; c < l.Components; c++) outp[o++] = Component(l, e, c);
            return outp;
        }

        public ushort[] Ushorts(int index, int expectedComponents, string where)
        {
            var l = Resolve(index);
            if (l.Components != expectedComponents) throw new InvalidDataException($"{where}: accessor {index} is {l.Type}, {expectedComponents} components were expected");
            if (l.ComponentType != 5121 && l.ComponentType != 5123) throw new InvalidDataException($"{where}: joints must be unsigned byte or short, accessor {index} is component type {l.ComponentType}");
            return Decoded(index, 1, () =>
            {
                var outp = new ushort[l.Count * l.Components];
                for (int e = 0, o = 0; e < l.Count; e++) for (int c = 0; c < l.Components; c++) outp[o++] = (ushort)Component(l, e, c);
                return outp;
            });
        }

        public int[] Ints(int index, string where)
        {
            var l = Resolve(index);
            if (l.Components != 1) throw new InvalidDataException($"{where}: accessor {index} is {l.Type}, indices are SCALAR");
            if (l.ComponentType != 5121 && l.ComponentType != 5123 && l.ComponentType != 5125) throw new InvalidDataException($"{where}: indices must be unsigned byte, short or int, accessor {index} is component type {l.ComponentType}");
            return Decoded(index, 2, () =>
            {
                var outp = new int[l.Count];
                for (int e = 0; e < l.Count; e++) { double v = Component(l, e, 0); if (v > int.MaxValue) throw new InvalidDataException($"{where}: index {v} does not fit"); outp[e] = (int)v; }
                return outp;
            });
        }

        /// <summary>COLOR_0 is VEC3 or VEC4, float or normalized; the model holds RGBA.</summary>
        public float[] Colors(int index, string where)
        {
            var l = Resolve(index);
            if (l.Components != 3 && l.Components != 4) throw new InvalidDataException($"{where}: accessor {index} is {l.Type}, colours are VEC3 or VEC4");
            return Decoded(index, 3, () =>
            {
                var outp = new float[l.Count * 4];
                for (int e = 0, o = 0; e < l.Count; e++)
                {
                    for (int c = 0; c < l.Components; c++) outp[o++] = (float)Component(l, e, c);
                    if (l.Components == 3) outp[o++] = 1f;
                }
                return outp;
            });
        }
    }

    // ---------------------------------------------------------------- small helpers

    static void TexRef(JToken t, out int index, out int texCoord)
    {
        index = t?["index"]?.Value<int>() ?? -1;
        texCoord = t?["texCoord"]?.Value<int>() ?? 0;
    }

    static float[] Floats(JArray a, int n, string what)
    {
        if (a.Count != n) throw new InvalidDataException($"{what} has {a.Count} values, {n} expected");
        var r = new float[n]; for (int i = 0; i < n; i++) r[i] = a[i].Value<float>(); return r;
    }

    static double[] Doubles(JArray a, int n, string what)
    {
        if (a.Count != n) throw new InvalidDataException($"{what} has {a.Count} values, {n} expected");
        var r = new double[n]; for (int i = 0; i < n; i++) r[i] = a[i].Value<double>(); return r;
    }

    static T[] Sized<T>(T[] data, int expected, string where)
    {
        if (data.Length != expected) throw new InvalidDataException($"{where} has {data.Length} values, {expected} expected for this primitive's vertex count");
        return data;
    }
}
