// BlenderGlbWriter.cs - deploy_convert.py's export, part 8c: the GLB's bytes as the glTF exporter writes them, from
// the structure (8a), the materials (8a-ii) and the channels (8b) already held. Read in io_scene_gltf2 (Blender 5.1.2):
//
//   * exporter.py GlTF2Exporter.__traverse: the scene graph is flattened depth first through each object's members in
//     dir() order - alphabetical -, a child-of-root object appended to its root list when its members are done (so a
//     node's children, mesh and skin take lower indices than the node; a mesh's primitives' attributes come before
//     their indices, a primitive's material's images after both; a texture's sampler before its image); binary data is
//     appended to the one buffer as it is met, each view padded to four bytes (buffer.py); an object already in its
//     root list (the same object: the accessors gather_accessor cached on bytes, type, count, max and min - the
//     animations' and the inverse bind matrices' -, the materials, textures, images and samplers cached per source)
//     is referenced by its index; a scene's nodes come first, every animation after the last scene (channels, then
//     their samplers' input and output), an unused skin last (export.py __gather_gltf)
//   * primitives.py: POSITION with its min and max (array_to_accessor: never shared), the indices as unsigned shorts
//     below 65535 else unsigned ints through gather_accessor (shared between primitives with the same index bytes), the attributes as ARRAY_BUFFER views, the indices ELEMENT_ARRAY_BUFFER, the inverse bind
//     matrices, the animation data and the images without a target; a colour set with alpha normalized shorts
//   * gltf2_io.py to_dict: each object's keys in alphabetical order; export.py __fix_json: a None, an empty list or
//     dict (KHR_materials_unlit's excepted) dropped, an integral float written as an int; io/exp/export.py save_gltf:
//     the root keys in its sort order, json.dumps with (',', ':'), ensure_ascii (every non-ASCII character \uXXXX),
//     floats as repr (PyFloatRepr); the GLB header, the JSON chunk padded with spaces to four bytes, the BIN chunk with
//     zeros; the asset's generator "Khronos glTF Blender I/O v" + bl_info's version
//   * the material extensions' order is their gather order (materials.py __gather_extensions): clearcoat,
//     transmission, emissive_strength, volume, specular, sheen, anisotropy, ior - unlit alone on an unlit material;
//     an extension's OWN keys in its gather order too (specular.py: specularFactor, specularTexture, specularColorFactor,
//     specularColorTexture - not alphabetical; the reviewer's find, 2026-10-11, measured in Blender), and its images are
//     met in that order; extensionsUsed lists each extension at its first use along the walk
//   * contracts on what 8a, 8a-ii and 8b hold (not re-derived here): the nodes' index order is the traversal order -
//     every mesh hangs from the armature and no joint carries a mesh, so meeting each node's mesh and skin in index
//     order is the exporter's walk (a mesh under a bone-parented object would be met inside its parent joint's skin
//     otherwise); every texture and sampler in MaterialRows was traversed (the exporter writes only those); an
//     explicit null in a material's JSON is never emitted (a nested object left with only nulls would be written as
//     {} here, where __fix_json's second pass drops it)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

public static class BlenderGlbWriter
{
    public const string Generator = "Khronos glTF Blender I/O v5.1.20";
    static readonly string[] ExtensionOrder = { "KHR_materials_unlit", "KHR_materials_clearcoat", "KHR_materials_transmission", "KHR_materials_emissive_strength", "KHR_materials_volume", "KHR_materials_specular", "KHR_materials_sheen", "KHR_materials_anisotropy", "KHR_materials_ior" };
    // an extension's own keys in the order its gather inserts them (specular.py, clearcoat.py, anisotropy.py: a factor, its
    // texture, the colour factor, its texture - measured in Blender 2026-10-11); the other extensions' keys are alphabetical already
    static readonly Dictionary<string, string[]> ExtensionKeyOrder = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["KHR_materials_specular"] = new[] { "specularFactor", "specularTexture", "specularColorFactor", "specularColorTexture" },
        ["KHR_materials_clearcoat"] = new[] { "clearcoatFactor", "clearcoatRoughnessFactor", "clearcoatTexture", "clearcoatRoughnessTexture", "clearcoatNormalTexture" },
        ["KHR_materials_anisotropy"] = new[] { "anisotropyStrength", "anisotropyRotation", "anisotropyTexture" },
    };
    static readonly string[] RootOrder = { "asset", "extensionsUsed", "extensionsRequired", "extensions", "extras", "scene", "scenes", "nodes", "cameras", "animations", "materials", "meshes", "textures", "images", "skins", "accessors", "bufferViews", "samplers", "buffers" };

    public sealed class Result
    {
        public string Fallback;
        public byte[] Glb;                 // the whole file
        public string Json;                // the JSON chunk's text, unpadded
        public byte[] Bin;                 // the BIN chunk, unpadded
        public int Accessors, BufferViews, SharedAccessors;
    }

    sealed class View { public int Offset, Length; public int? Target; }
    sealed class Accessor { public int View; public int ComponentType, Count; public string Type; public double[] Min, Max; public bool Normalized; public string Key; }

    sealed class Builder
    {
        public readonly MemoryStream Bin = new MemoryStream();
        public readonly List<View> Views = new List<View>();
        public readonly List<Accessor> Accessors = new List<Accessor>();
        readonly Dictionary<string, int> cached = new Dictionary<string, int>(StringComparer.Ordinal);
        public int Shared;

        public int AddView(byte[] data, int? target)
        {
            var v = new View { Offset = (int)Bin.Length, Length = data.Length, Target = target };
            Bin.Write(data, 0, data.Length);
            int pad = (4 - data.Length % 4) % 4;
            for (int i = 0; i < pad; i++) Bin.WriteByte(0);
            Views.Add(v); return Views.Count - 1;
        }

        /// <summary>array_to_accessor: a new accessor and view every time.</summary>
        public int AddAccessor(byte[] data, int componentType, int count, string type, int? target, double[] min = null, double[] max = null, bool normalized = false)
        {
            int view = AddView(data, target);
            Accessors.Add(new Accessor { View = view, ComponentType = componentType, Count = count, Type = type, Min = min, Max = max, Normalized = normalized });
            return Accessors.Count - 1;
        }

        /// <summary>gather_accessor: cached on (bytes, component type, count, max, min, type) - the same object, so
        /// the same accessor and view, when the key was seen.</summary>
        public int GatherAccessor(byte[] data, int componentType, int count, string type, double[] min, double[] max, int? target = null)
        {
            string key = componentType + "\t" + count + "\t" + type + "\t" + Doubles(max) + "\t" + Doubles(min) + "\t" + Convert.ToBase64String(data);
            if (cached.TryGetValue(key, out int idx)) { Shared++; return idx; }
            idx = AddAccessor(data, componentType, count, type, target, min, max);
            cached[key] = idx; return idx;
        }

        static string Doubles(double[] v) => v == null ? "-" : string.Join(",", v.Select(d => BitConverter.DoubleToInt64Bits(d).ToString(CultureInfo.InvariantCulture)));
    }

    public static Result Build(BlenderDeployExport.Result x)
    {
        var res = new Result();
        try { BuildInto(res, x); }
        catch (NotSupportedException e) { res.Fallback = e.Message; }
        catch (ArgumentException e) { res.Fallback = e.Message; }   // a NaN or infinity: json.dumps(allow_nan=False) fails there too
        return res;
    }

    static void BuildInto(Result res, BlenderDeployExport.Result x)
    {
        var b = new Builder();
        var mr = x.MaterialRows;
        var ch = x.ChannelRows;
        // ---- the walk: nodes in index order (the exporter's post-order), each mesh and skin when first met
        var meshAccessors = new Dictionary<int, List<(JObject prim, int material)>>();   // mesh -> primitives' JSON
        var skinIbm = new Dictionary<int, int>();                                        // skin -> accessor
        var imageView = new Dictionary<int, int>();                                      // image -> view
        var extensionsUsed = new List<string>();
        var materialJson = new Dictionary<int, JObject>();
        void TouchMaterial(int material)
        {
            if (material < 0 || materialJson.ContainsKey(material)) return;
            var j = mr.Materials[material];
            // the material's extensions at first use, in the gather order
            if (j["extensions"] is JObject ex)
                foreach (string name in ExtensionOrder) if (ex[name] != null && !extensionsUsed.Contains(name)) extensionsUsed.Add(name);
            // the textures' images in the material's member order, the first time each image is met
            foreach (int tex in TexturesOf(j))
            {
                int image = mr.Textures[tex].image;
                if (!imageView.ContainsKey(image)) imageView[image] = b.AddView(mr.Images[image].bytes, null);
            }
            materialJson[material] = j;
        }
        for (int ni = 0; ni < x.Nodes.Count; ni++)
        {
            var n = x.Nodes[ni];
            if (n.Mesh >= 0 && !meshAccessors.ContainsKey(n.Mesh))
            {
                var mesh = x.Meshes[n.Mesh];
                var prims = new List<(JObject, int)>();
                for (int pi = 0; pi < mesh.Primitives.Count; pi++)
                {
                    var p = mesh.Primitives[pi];
                    var attrs = new JObject();
                    attrs["POSITION"] = b.AddAccessor(Floats(p.Positions), 5126, p.VertexCount, "VEC3", 34962, MinOf(p.Positions, 3), MaxOf(p.Positions, 3));
                    attrs["NORMAL"] = b.AddAccessor(Floats(p.Normals), 5126, p.VertexCount, "VEC3", 34962);
                    for (int u = 0; u < p.Uv.Count; u++) attrs["TEXCOORD_" + u] = b.AddAccessor(Floats(p.Uv[u]), 5126, p.VertexCount, "VEC2", 34962);
                    for (int c = 0; c < p.Colors.Count; c++)
                    {
                        var set = p.Colors[c];
                        if (set.Forced) attrs["COLOR_" + c] = b.AddAccessor(Enumerable.Repeat((byte)255, 4 * p.VertexCount).ToArray(), 5121, p.VertexCount, "VEC4", 34962, normalized: true);
                        else if (set.Alpha) attrs["COLOR_" + c] = b.AddAccessor(Ushorts(set.Shorts), 5123, p.VertexCount, "VEC4", 34962, normalized: true);
                        else attrs["COLOR_" + c] = b.AddAccessor(Floats(set.Data), 5126, p.VertexCount, "VEC3", 34962);
                    }
                    if (p.Joints != null)
                    {
                        bool bytes = p.Joints.Max() < 256;
                        attrs["JOINTS_0"] = bytes ? b.AddAccessor(p.Joints.Select(j => (byte)j).ToArray(), 5121, p.VertexCount, "VEC4", 34962) : b.AddAccessor(Ushorts(p.Joints), 5123, p.VertexCount, "VEC4", 34962);
                        attrs["WEIGHTS_0"] = b.AddAccessor(Floats(p.Weights), 5126, p.VertexCount, "VEC4", 34962);
                    }
                    int maxIndex = p.Indices.Length > 0 ? p.Indices.Max() : 0;
                    // __gather_indices goes through gather_accessor: two primitives with the same index bytes share one accessor and view
                    int indices = maxIndex < 65535 ? b.GatherAccessor(Ushorts(p.Indices.Select(i => (ushort)i).ToArray()), 5123, p.Indices.Length, "SCALAR", null, null, 34963)
                                                   : b.GatherAccessor(p.Indices.SelectMany(i => BitConverter.GetBytes((uint)i)).ToArray(), 5125, p.Indices.Length, "SCALAR", null, null, 34963);
                    var prim = new JObject { ["attributes"] = attrs, ["indices"] = indices };
                    int material = mesh.Material[pi];
                    if (material >= 0) { prim["material"] = material; TouchMaterial(material); }
                    prims.Add((prim, material));
                }
                meshAccessors[n.Mesh] = prims;
            }
            if (n.Skin >= 0 && !skinIbm.ContainsKey(n.Skin)) skinIbm[n.Skin] = Ibm(b, x.Skins[n.Skin]);
        }
        // ---- the animations: per channel the sampler's input then output (gather_accessor: shared on the same key)
        var animations = new JArray();
        if (ch != null)
            foreach (var a in ch.Animations)
            {
                var channels = new JArray(); var samplers = new JArray();
                foreach (var c in a.Channels)
                {
                    int input = b.GatherAccessor(Floats(c.Times), 5126, c.Times.Length, "SCALAR", new[] { c.MinTime }, new[] { c.MaxTime });
                    int output = b.GatherAccessor(Floats(c.Values), 5126, c.Times.Length, c.Path == "rotation" ? "VEC4" : "VEC3", null, null);
                    channels.Add(new JObject { ["sampler"] = c.Sampler, ["target"] = new JObject { ["node"] = c.Node, ["path"] = c.Path } });
                    samplers.Add(new JObject { ["input"] = input, ["interpolation"] = c.Interpolation, ["output"] = output });
                }
                animations.Add(new JObject { ["channels"] = channels, ["name"] = a.Name, ["samplers"] = samplers });
            }
        // ---- an unused skin: traversed after the animations
        for (int si = 0; si < x.Skins.Count; si++) if (!skinIbm.ContainsKey(si)) skinIbm[si] = Ibm(b, x.Skins[si]);

        // ---- the JSON
        var root = new JObject();
        root["asset"] = new JObject { ["generator"] = Generator, ["version"] = "2.0" };
        if (extensionsUsed.Count > 0) root["extensionsUsed"] = new JArray(extensionsUsed);
        // the active scene: the context's, "Scene" (the others sort around it by name; a source scene of that name was renamed)
        int active = x.Scenes.FindIndex(s => s.name == "Scene");
        if (active < 0) throw new NotSupportedException("no scene named Scene (the context scene the export names)");
        root["scene"] = active;
        root["scenes"] = new JArray(x.Scenes.Select(s => { var j = new JObject { ["name"] = s.name }; if (s.roots.Count > 0) j["nodes"] = new JArray(s.roots); return j; }));
        root["nodes"] = new JArray(x.Nodes.Select(n =>
        {
            var j = new JObject();
            if (n.Children.Count > 0) j["children"] = new JArray(n.Children);
            if (n.Mesh >= 0) j["mesh"] = n.Mesh;
            j["name"] = n.Name;
            if (n.Rotation != null) j["rotation"] = new JArray(n.Rotation.Select(v => (double)v));
            if (n.Scale != null) j["scale"] = new JArray(n.Scale.Select(v => (double)v));
            if (n.Skin >= 0) j["skin"] = n.Skin;
            if (n.Translation != null) j["translation"] = new JArray(n.Translation.Select(v => (double)v));
            return j;
        }));
        if (animations.Count > 0) root["animations"] = animations;
        if (materialJson.Count > 0) root["materials"] = new JArray(Enumerable.Range(0, mr.Materials.Count).Select(i => materialJson.ContainsKey(i) ? mr.Materials[i] : throw new NotSupportedException($"the material {i} is never used by a primitive (the exporter would not write it)")));
        root["meshes"] = new JArray(Enumerable.Range(0, x.Meshes.Count).Select(i =>
        {
            if (!meshAccessors.ContainsKey(i)) throw new NotSupportedException($"the mesh {i} hangs from no node");
            return new JObject { ["name"] = x.Meshes[i].Name, ["primitives"] = new JArray(meshAccessors[i].Select(t => t.prim)) };
        }));
        if (mr.Textures.Count > 0) root["textures"] = new JArray(mr.Textures.Select(t => new JObject { ["sampler"] = t.sampler, ["source"] = t.image }));
        if (mr.Images.Count > 0) root["images"] = new JArray(mr.Images.Select((im, i) =>
        {
            if (!imageView.ContainsKey(i)) throw new NotSupportedException($"the image {i} is used by no written material");
            return new JObject { ["bufferView"] = imageView[i], ["mimeType"] = im.mime, ["name"] = im.name };
        }));
        if (x.Skins.Count > 0) root["skins"] = new JArray(x.Skins.Select((s, i) => new JObject { ["inverseBindMatrices"] = skinIbm[i], ["joints"] = new JArray(s.Joints), ["name"] = s.Name }));
        root["accessors"] = new JArray(b.Accessors.Select(a =>
        {
            var j = new JObject { ["bufferView"] = a.View, ["componentType"] = a.ComponentType, ["count"] = a.Count };
            if (a.Max != null) j["max"] = new JArray(a.Max);
            if (a.Min != null) j["min"] = new JArray(a.Min);
            if (a.Normalized) j["normalized"] = true;
            j["type"] = a.Type;
            return j;
        }));
        root["bufferViews"] = new JArray(b.Views.Select(v => { var j = new JObject { ["buffer"] = 0, ["byteLength"] = v.Length, ["byteOffset"] = v.Offset }; if (v.Target != null) j["target"] = v.Target; return j; }));
        if (mr.Samplers.Count > 0) root["samplers"] = new JArray(mr.Samplers);
        if (b.Bin.Length > 0) root["buffers"] = new JArray(new JObject { ["byteLength"] = (int)b.Bin.Length });

        var sb = new StringBuilder();
        WriteValue(sb, root, "");
        res.Json = sb.ToString();
        res.Bin = b.Bin.ToArray();
        res.Accessors = b.Accessors.Count; res.BufferViews = b.Views.Count; res.SharedAccessors = b.Shared;
        // ---- the GLB: header, the JSON chunk padded with spaces, the BIN chunk padded with zeros
        var jsonBytes = Encoding.UTF8.GetBytes(res.Json);
        int spaces = (4 - (jsonBytes.Length & 3)) & 3, zeros = (4 - (res.Bin.Length & 3)) & 3;
        int length = 12 + 8 + jsonBytes.Length + spaces + (res.Bin.Length > 0 ? 8 + res.Bin.Length + zeros : 0);
        using (var ms = new MemoryStream(length))
        {
            void U32(uint v) { ms.Write(BitConverter.GetBytes(v), 0, 4); }
            ms.Write(Encoding.ASCII.GetBytes("glTF"), 0, 4); U32(2); U32((uint)length);
            U32((uint)(jsonBytes.Length + spaces)); ms.Write(Encoding.ASCII.GetBytes("JSON"), 0, 4); ms.Write(jsonBytes, 0, jsonBytes.Length); for (int i = 0; i < spaces; i++) ms.WriteByte((byte)' ');
            if (res.Bin.Length > 0) { U32((uint)(res.Bin.Length + zeros)); ms.Write(new byte[] { (byte)'B', (byte)'I', (byte)'N', 0 }, 0, 4); ms.Write(res.Bin, 0, res.Bin.Length); for (int i = 0; i < zeros; i++) ms.WriteByte(0); }
            res.Glb = ms.ToArray();
        }
    }

    static int Ibm(Builder b, BlenderDeployExport.Skin s)
    {
        var bytes = new byte[64 * s.InverseBind.Count];
        for (int j = 0; j < s.InverseBind.Count; j++) for (int k = 0; k < 16; k++) BitConverter.GetBytes(s.InverseBind[j][k]).CopyTo(bytes, 64 * j + 4 * k);
        return b.GatherAccessor(bytes, 5126, s.InverseBind.Count, "MAT4", null, null);
    }

    /// <summary>The texture indices a material's JSON refers to, in the material's member order (alphabetical keys,
    /// the extensions dict in its gather order): emissiveTexture, the extensions' textures, normalTexture,
    /// occlusionTexture, pbrMetallicRoughness's baseColorTexture then metallicRoughnessTexture.</summary>
    static IEnumerable<int> TexturesOf(JObject material)
    {
        foreach (var kv in Ordered(material, true))
        {
            if (kv.Key == "extensions" && kv.Value is JObject ex)
            {
                foreach (string name in ExtensionOrder) if (ex[name] is JObject e) foreach (var ekv in ExtensionProperties(name, e)) if (ekv.Value is JObject eo) foreach (int t in TexturesIn(eo)) yield return t;
            }
            else if (kv.Value is JObject o) foreach (int t in TexturesIn(o)) yield return t;
        }
    }

    static IEnumerable<int> TexturesIn(JObject o)
    {
        if (o["index"] != null && o["index"].Type == JTokenType.Integer) { yield return (int)o["index"]; yield break; }
        foreach (var kv in Ordered(o, false)) if (kv.Value is JObject inner) foreach (int t in TexturesIn(inner)) yield return t;
    }

    static IEnumerable<KeyValuePair<string, JToken>> Ordered(JObject o, bool root) => o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new KeyValuePair<string, JToken>(p.Name, p.Value));

    /// <summary>An extension's properties in its gather order (ExtensionKeyOrder), unknown keys alphabetical after them.</summary>
    static IEnumerable<KeyValuePair<string, JToken>> ExtensionProperties(string extension, JObject o)
    {
        string[] order = ExtensionKeyOrder.TryGetValue(extension, out var k) ? k : new string[0];
        return o.Properties().OrderBy(p => { int i = Array.IndexOf(order, p.Name); return i < 0 ? int.MaxValue : i; }).ThenBy(p => p.Name, StringComparer.Ordinal).Select(p => new KeyValuePair<string, JToken>(p.Name, p.Value));
    }

    /// <summary>The extension an object at `path` IS (".../extensions/KHR_x"), else null.</summary>
    static string ExtensionOf(string path)
    {
        int at = path.LastIndexOf("/extensions/", StringComparison.Ordinal);
        if (at < 0) return null;
        string rest = path.Substring(at + "/extensions/".Length);
        return rest.Contains("/") ? null : rest;
    }

    static byte[] Floats(float[] v) { var b = new byte[4 * v.Length]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, 4 * i); return b; }
    static byte[] Ushorts(ushort[] v) { var b = new byte[2 * v.Length]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, 2 * i); return b; }
    static double[] MinOf(float[] v, int dim) { var r = new double[dim]; for (int d = 0; d < dim; d++) { float m = float.PositiveInfinity; for (int i = d; i < v.Length; i += dim) if (v[i] < m) m = v[i]; r[d] = m; } return r; }
    static double[] MaxOf(float[] v, int dim) { var r = new double[dim]; for (int d = 0; d < dim; d++) { float m = float.NegativeInfinity; for (int i = d; i < v.Length; i += dim) if (v[i] > m) m = v[i]; r[d] = m; } return r; }

    // ---- json.dumps(separators=(',', ':'), ensure_ascii=True) after __fix_json and to_dict's alphabetical keys; the
    //      root's keys in save_gltf's sort order, a material extensions dict in its gather order, a primitive's
    //      attributes in insertion order
    static void WriteValue(StringBuilder sb, JToken t, string path)
    {
        switch (t.Type)
        {
            case JTokenType.Object:
                {
                    var o = (JObject)t;
                    IEnumerable<JProperty> props;
                    if (path == "") props = o.Properties().OrderBy(p => Array.IndexOf(RootOrder, p.Name));
                    else if (path.EndsWith("/extensions", StringComparison.Ordinal)) props = o.Properties().OrderBy(p => Array.IndexOf(ExtensionOrder, p.Name) < 0 ? int.MaxValue : Array.IndexOf(ExtensionOrder, p.Name));
                    else if (path.EndsWith("/attributes", StringComparison.Ordinal)) props = o.Properties();
                    else if (ExtensionOf(path) != null) props = ExtensionProperties(ExtensionOf(path), o).Select(kv => (JProperty)kv.Value.Parent);
                    else props = o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal);
                    sb.Append('{'); bool first = true;
                    foreach (var p in props)
                    {
                        if (Dropped(p.Name, p.Value)) continue;
                        if (!first) sb.Append(','); first = false;
                        WriteString(sb, p.Name); sb.Append(':'); WriteValue(sb, p.Value, path + "/" + p.Name);
                    }
                    sb.Append('}');
                    break;
                }
            case JTokenType.Array:
                sb.Append('['); bool f2 = true;
                foreach (var e in (JArray)t) { if (!f2) sb.Append(','); f2 = false; WriteValue(sb, e, path + "/[]"); }
                sb.Append(']'); break;
            case JTokenType.Integer: sb.Append(((long)t).ToString(CultureInfo.InvariantCulture)); break;
            // __fix_json turns an integral float into an int, walking dicts and LISTS - not tuples: a colour factor the exporter
            // builds as a tuple (specular.py: the socket's default_value[:3], or (r * f, g * f, b * f)) keeps every float's
            // repr - KHR_materials_specular's [0.0, 0.0, 0.0] beside an ior of 1000 (measured on three registry rigs, 2026-10-14)
            case JTokenType.Float: sb.Append(TupleFloat(path) ? PyFloatRepr.Repr((double)t) : PyFloatRepr.Json((double)t)); break;
            case JTokenType.Boolean: sb.Append((bool)t ? "true" : "false"); break;
            case JTokenType.String: WriteString(sb, (string)t); break;
            case JTokenType.Null: sb.Append("null"); break;
            default: throw new NotSupportedException("a JSON token of type " + t.Type);
        }
    }

    /// <summary>An item of an extension's tuple-valued factor (specularColorFactor; sheenColorFactor and attenuationColor are
    /// slices too, read in sheen.py and volume.py, unmeasured - the material export leaves sheen and volume by name).</summary>
    static bool TupleFloat(string path)
    {
        int i = path.LastIndexOf("/[]", StringComparison.Ordinal);
        if (i < 0 || path.IndexOf("/extensions/", StringComparison.Ordinal) < 0) return false;
        string key = path.Substring(0, i); key = key.Substring(key.LastIndexOf('/') + 1);
        return key == "specularColorFactor" || key == "sheenColorFactor" || key == "attenuationColor";
    }

    static bool Dropped(string key, JToken v)
    {
        if (v == null || v.Type == JTokenType.Null) return true;
        if (v.Type == JTokenType.Array && ((JArray)v).Count == 0) return true;
        if (v.Type == JTokenType.Object && ((JObject)v).Count == 0 && key != "KHR_materials_unlit") return true;
        return false;
    }

    static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
