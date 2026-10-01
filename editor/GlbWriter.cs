using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

// THE GLB WRITER (2026-10-01, step 2 of replacing Blender). A HafModel back to a .glb: one BIN chunk, one tightly
// packed buffer view per attribute array, image bytes embedded, every field the model holds written as glTF 2.0
// defines it - so a file read by GlbReader, written here and read again is the same model (GlbWriterTests), and
// Blender reading the written file sees what it saw in the original (tools/glb_writer_drill.sh).
//
// THE CONTRACT: the writer writes what the model holds, and only that. A material's extension payload is carried
// verbatim (HafMaterial.ExtensionsJson) and the names are declared in extensionsUsed, so KHR_materials_specular,
// clearcoat and the like - and the textures they reference - survive a round trip. What the reader does not model is
// not here either, and the writer says so rather than pretending: sampler settings (a texture's sampler index is
// kept; the written samplers carry glTF's defaults), morph-target data (counted by the reader, not carried),
// extensions anywhere but on a material. A model that cannot be written as it is - an index that does not fit a
// uint, a joint that does not fit a ushort, a node outside the lists - is refused by name.
public static class GlbWriter
{
    public const string GeneratorTag = "HAF GlbWriter";

    public static void Write(HafModel m, string path)
    {
        var bytes = Write(m);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllBytes(path, bytes);
    }

    public static byte[] Write(HafModel m)
    {
        // the BIN chunk is sized ONCE from the model (every array's bytes, every image, 4-byte padding per view): a
        // growing stream doubled a 106 MB model through 128 and 256 MB arrays, and thirty such files in one Mono
        // process ran its large-object heap out ("Insufficient memory", the writer drill, 2026-10-01)
        var bin = new MemoryStream(checked((int)Math.Min(int.MaxValue, BinSizeUpperBound(m))));
        var views = new JArray(); var accessors = new JArray();
        var root = new JObject
        {
            // the generator names this writer once; a model that came through it already keeps its line, so a second write is byte-identical
            ["asset"] = new JObject { ["version"] = "2.0", ["generator"] = m.Generator.StartsWith(GeneratorTag, StringComparison.Ordinal) ? m.Generator : GeneratorTag + (m.Generator.Length > 0 ? " (read from " + m.Generator + ")" : "") },
        };
        Validate(m);
        var used = new SortedSet<string>(StringComparer.Ordinal);   // every extension the written file carries, declared below

        // ---- accessors: one tightly packed view each
        int View(byte[] data, int byteLength)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = (int)bin.Length, ["byteLength"] = byteLength });
            bin.Write(data, 0, byteLength);
            return views.Count - 1;
        }
        int FloatAccessor(float[] data, int components, string type, bool minMax)
        {
            var b = new byte[data.Length * 4]; Buffer.BlockCopy(data, 0, b, 0, b.Length);
            var a = new JObject { ["bufferView"] = View(b, b.Length), ["componentType"] = 5126, ["type"] = type, ["count"] = data.Length / components };
            if (minMax && data.Length > 0)
            {
                var mn = new float[components]; var mx = new float[components];
                for (int c = 0; c < components; c++) { mn[c] = float.PositiveInfinity; mx[c] = float.NegativeInfinity; }
                for (int i = 0; i < data.Length; i++) { int c = i % components; if (data[i] < mn[c]) mn[c] = data[i]; if (data[i] > mx[c]) mx[c] = data[i]; }
                a["min"] = new JArray(mn.Cast<object>()); a["max"] = new JArray(mx.Cast<object>());
            }
            accessors.Add(a); return accessors.Count - 1;
        }
        int DoubleAsFloatAccessor(double[] data, int components, string type) => FloatAccessor(data.Select(d => (float)d).ToArray(), components, type, false);
        int UshortAccessor(ushort[] data, int components, string type)
        {
            var b = new byte[data.Length * 2]; Buffer.BlockCopy(data, 0, b, 0, b.Length);
            accessors.Add(new JObject { ["bufferView"] = View(b, b.Length), ["componentType"] = 5123, ["type"] = type, ["count"] = data.Length / components });
            return accessors.Count - 1;
        }
        int IndexAccessor(int[] indices, int vertexCount)
        {
            bool wide = vertexCount > 65535;
            byte[] b;
            if (wide) { b = new byte[indices.Length * 4]; for (int i = 0; i < indices.Length; i++) BitConverter.GetBytes((uint)indices[i]).CopyTo(b, i * 4); }
            else { b = new byte[indices.Length * 2]; for (int i = 0; i < indices.Length; i++) BitConverter.GetBytes((ushort)indices[i]).CopyTo(b, i * 2); }
            accessors.Add(new JObject { ["bufferView"] = View(b, b.Length), ["componentType"] = wide ? 5125 : 5123, ["type"] = "SCALAR", ["count"] = indices.Length });
            return accessors.Count - 1;
        }

        // ---- images, samplers, textures, materials
        if (m.Images.Count > 0)
        {
            var images = new JArray();
            foreach (var im in m.Images)
            {
                var j = new JObject();
                if (im.Name.Length > 0) j["name"] = im.Name;
                if (im.Bytes == null) throw new InvalidDataException($"image '{im.Name}' has no bytes - a model whose image was never resolved cannot be written whole");
                j["mimeType"] = im.MimeType.Length > 0 ? im.MimeType : GuessMime(im.Bytes);
                j["bufferView"] = View(im.Bytes, im.Bytes.Length);
                images.Add(j);
            }
            root["images"] = images;
        }
        int samplerCount = m.Textures.Count == 0 ? 0 : m.Textures.Max(t => t.Sampler) + 1;
        if (samplerCount > 0) { var samplers = new JArray(); for (int i = 0; i < samplerCount; i++) samplers.Add(new JObject()); root["samplers"] = samplers; }   // defaults: the model does not carry sampler settings
        if (m.Textures.Count > 0)
        {
            var textures = new JArray();
            foreach (var t in m.Textures)
            {
                var j = new JObject();
                if (t.Name.Length > 0) j["name"] = t.Name;
                if (t.Source >= 0) j["source"] = t.Source;
                if (t.Sampler >= 0) j["sampler"] = t.Sampler;
                textures.Add(j);
            }
            root["textures"] = textures;
        }
        if (m.Materials.Count > 0)
        {
            var materials = new JArray();
            foreach (var mt in m.Materials)
            {
                var j = new JObject();
                if (mt.Name.Length > 0) j["name"] = mt.Name;
                var pbr = new JObject();
                if (!IsDefault(mt.BaseColorFactor, 1, 1, 1, 1)) pbr["baseColorFactor"] = Arr(mt.BaseColorFactor);
                if (mt.BaseColorTexture >= 0) pbr["baseColorTexture"] = TexRef(mt.BaseColorTexture, mt.BaseColorTexCoord);
                if (mt.MetallicFactor != 1f) pbr["metallicFactor"] = mt.MetallicFactor;
                if (mt.RoughnessFactor != 1f) pbr["roughnessFactor"] = mt.RoughnessFactor;
                if (mt.MetallicRoughnessTexture >= 0) pbr["metallicRoughnessTexture"] = TexRef(mt.MetallicRoughnessTexture, mt.MetallicRoughnessTexCoord);
                if (pbr.Count > 0) j["pbrMetallicRoughness"] = pbr;
                if (mt.NormalTexture >= 0) { var n = TexRef(mt.NormalTexture, mt.NormalTexCoord); if (mt.NormalScale != 1f) n["scale"] = mt.NormalScale; j["normalTexture"] = n; }
                if (mt.OcclusionTexture >= 0) { var o = TexRef(mt.OcclusionTexture, mt.OcclusionTexCoord); if (mt.OcclusionStrength != 1f) o["strength"] = mt.OcclusionStrength; j["occlusionTexture"] = o; }
                if (mt.EmissiveTexture >= 0) j["emissiveTexture"] = TexRef(mt.EmissiveTexture, mt.EmissiveTexCoord);
                if (!IsDefault(mt.EmissiveFactor, 0, 0, 0)) j["emissiveFactor"] = Arr(mt.EmissiveFactor);
                if (mt.AlphaMode != "OPAQUE") j["alphaMode"] = mt.AlphaMode;
                if (mt.AlphaMode == "MASK" && mt.AlphaCutoff != 0.5f) j["alphaCutoff"] = mt.AlphaCutoff;
                if (mt.DoubleSided) j["doubleSided"] = true;
                if (mt.ExtensionsJson != null) { var ext = JObject.Parse(mt.ExtensionsJson); j["extensions"] = ext; foreach (var prop in ext.Properties()) used.Add(prop.Name); }
                materials.Add(j);
            }
            root["materials"] = materials;
        }

        // ---- meshes
        if (m.Meshes.Count > 0)
        {
            var meshes = new JArray();
            foreach (var me in m.Meshes)
            {
                var j = new JObject();
                if (me.Name.Length > 0) j["name"] = me.Name;
                var prims = new JArray();
                foreach (var p in me.Primitives)
                {
                    var attrs = new JObject { ["POSITION"] = FloatAccessor(p.Positions, 3, "VEC3", true) };
                    if (p.Normals != null) attrs["NORMAL"] = FloatAccessor(p.Normals, 3, "VEC3", false);
                    if (p.Tangents != null) attrs["TANGENT"] = FloatAccessor(p.Tangents, 4, "VEC4", false);
                    if (p.Uv0 != null) attrs["TEXCOORD_0"] = FloatAccessor(p.Uv0, 2, "VEC2", false);
                    if (p.Uv1 != null) attrs["TEXCOORD_1"] = FloatAccessor(p.Uv1, 2, "VEC2", false);
                    if (p.Colors != null) attrs["COLOR_0"] = FloatAccessor(p.Colors, 4, "VEC4", false);
                    if (p.Joints != null) attrs["JOINTS_0"] = UshortAccessor(p.Joints, 4, "VEC4");
                    if (p.Weights != null) attrs["WEIGHTS_0"] = FloatAccessor(p.Weights, 4, "VEC4", false);
                    if (p.Joints1 != null) attrs["JOINTS_1"] = UshortAccessor(p.Joints1, 4, "VEC4");
                    if (p.Weights1 != null) attrs["WEIGHTS_1"] = FloatAccessor(p.Weights1, 4, "VEC4", false);
                    var pj = new JObject { ["attributes"] = attrs };
                    if (p.Indices != null) pj["indices"] = IndexAccessor(p.Indices, p.VertexCount);
                    if (p.Material >= 0) pj["material"] = p.Material;
                    if (p.Mode != 4) pj["mode"] = p.Mode;
                    prims.Add(pj);
                }
                j["primitives"] = prims;
                meshes.Add(j);
            }
            root["meshes"] = meshes;
        }

        // ---- nodes, scene
        if (m.Nodes.Count > 0)
        {
            var nodes = new JArray();
            foreach (var n in m.Nodes)
            {
                var j = new JObject();
                if (n.Name.Length > 0) j["name"] = n.Name;
                if (n.HasMatrix) j["matrix"] = Arr(n.Matrix);
                else
                {
                    if (!IsDefault(n.Translation, 0, 0, 0)) j["translation"] = Arr(n.Translation);
                    if (!IsDefault(n.Rotation, 0, 0, 0, 1)) j["rotation"] = Arr(n.Rotation);
                    if (!IsDefault(n.Scale, 1, 1, 1)) j["scale"] = Arr(n.Scale);
                }
                if (n.Mesh >= 0) j["mesh"] = n.Mesh;
                if (n.Skin >= 0) j["skin"] = n.Skin;
                if (n.Children.Count > 0) j["children"] = new JArray(n.Children.Cast<object>());
                nodes.Add(j);
            }
            root["nodes"] = nodes;
            root["scenes"] = new JArray { new JObject { ["nodes"] = new JArray(m.Roots.Cast<object>()) } };
            root["scene"] = 0;
        }

        // ---- skins
        if (m.Skins.Count > 0)
        {
            var skins = new JArray();
            foreach (var sk in m.Skins)
            {
                var j = new JObject { ["joints"] = new JArray(sk.Joints.Cast<object>()) };
                if (sk.Name.Length > 0) j["name"] = sk.Name;
                if (sk.Skeleton >= 0) j["skeleton"] = sk.Skeleton;
                if (sk.InverseBindMatrices != null) j["inverseBindMatrices"] = DoubleAsFloatAccessor(sk.InverseBindMatrices, 16, "MAT4");
                skins.Add(j);
            }
            root["skins"] = skins;
        }

        // ---- animations
        if (m.Animations.Count > 0)
        {
            var anims = new JArray();
            foreach (var an in m.Animations)
            {
                var j = new JObject();
                if (an.Name.Length > 0) j["name"] = an.Name;
                var samplers = new JArray();
                foreach (var s in an.Samplers)
                {
                    string type = s.Components == 1 ? "SCALAR" : s.Components == 3 ? "VEC3" : s.Components == 4 ? "VEC4" : throw new InvalidDataException($"animation '{an.Name}': a sampler with {s.Components} components is not one glTF carries");
                    var sj = new JObject { ["input"] = FloatAccessor(s.Times ?? new float[0], 1, "SCALAR", true), ["output"] = FloatAccessor(s.Values ?? new float[0], s.Components, type, false) };
                    if (s.Interpolation != "LINEAR") sj["interpolation"] = s.Interpolation;
                    samplers.Add(sj);
                }
                var channels = new JArray();
                foreach (var c in an.Channels)
                {
                    var target = new JObject { ["path"] = c.Path };
                    if (c.Node >= 0) target["node"] = c.Node;
                    channels.Add(new JObject { ["sampler"] = c.Sampler, ["target"] = target });
                }
                j["samplers"] = samplers; j["channels"] = channels;
                anims.Add(j);
            }
            root["animations"] = anims;
        }

        if (used.Count > 0) root["extensionsUsed"] = new JArray(used.Cast<object>());
        if (accessors.Count > 0) root["accessors"] = accessors;
        if (views.Count > 0) root["bufferViews"] = views;
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        if (bin.Length > 0) root["buffers"] = new JArray { new JObject { ["byteLength"] = (int)bin.Length } };

        // ---- the container, assembled into one exactly sized array
        var json = Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));
        int jsonPad = (4 - json.Length % 4) % 4;
        int binLength = (int)bin.Length;
        long total = 12 + 8 + json.Length + jsonPad + (binLength > 0 ? 8 + binLength : 0);
        if (total > uint.MaxValue) throw new InvalidDataException("the model does not fit a GLB (over 4 GB)");
        var outp = new byte[total];
        int at = 0;
        void U32(uint v) { outp[at] = (byte)v; outp[at + 1] = (byte)(v >> 8); outp[at + 2] = (byte)(v >> 16); outp[at + 3] = (byte)(v >> 24); at += 4; }
        U32(0x46546C67u); U32(2u); U32((uint)total);
        U32((uint)(json.Length + jsonPad)); U32(0x4E4F534Au); Buffer.BlockCopy(json, 0, outp, at, json.Length); at += json.Length;
        for (int i = 0; i < jsonPad; i++) outp[at++] = 0x20;
        if (binLength > 0)
        {
            U32((uint)binLength); U32(0x004E4942u);
            bin.Position = 0; int read = 0; while (read < binLength) { int n = bin.Read(outp, at + read, binLength - read); if (n <= 0) break; read += n; }
            at += binLength;
        }
        return outp;
    }

    /// <summary>Every byte the BIN chunk can hold for this model, plus padding: the stream's capacity, allocated once.</summary>
    static long BinSizeUpperBound(HafModel m)
    {
        long n = 0; int viewsGuess = 0;
        void Add(Array a, int bytesPer) { if (a != null) { n += (long)a.Length * bytesPer; viewsGuess++; } }
        foreach (var me in m.Meshes)
            foreach (var p in me.Primitives)
            {
                Add(p.Positions, 4); Add(p.Normals, 4); Add(p.Tangents, 4); Add(p.Uv0, 4); Add(p.Uv1, 4); Add(p.Colors, 4);
                Add(p.Joints, 2); Add(p.Weights, 4); Add(p.Joints1, 2); Add(p.Weights1, 4); Add(p.Indices, 4);
            }
        foreach (var im in m.Images) Add(im.Bytes, 1);
        foreach (var sk in m.Skins) Add(sk.InverseBindMatrices, 4);
        foreach (var an in m.Animations) foreach (var sp in an.Samplers) { Add(sp.Times, 4); Add(sp.Values, 4); }
        return n + 4L * (viewsGuess + 1);
    }

    // ---------------------------------------------------------------- what cannot be written as it is

    static void Validate(HafModel m)
    {
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            var n = m.Nodes[i];
            if (n.Mesh >= m.Meshes.Count) throw new InvalidDataException($"node {i} '{n.Name}' references mesh {n.Mesh}, the model has {m.Meshes.Count}");
            if (n.Skin >= m.Skins.Count) throw new InvalidDataException($"node {i} '{n.Name}' references skin {n.Skin}, the model has {m.Skins.Count}");
            foreach (var c in n.Children) if (c < 0 || c >= m.Nodes.Count) throw new InvalidDataException($"node {i} '{n.Name}' lists child {c}, the model has {m.Nodes.Count} nodes");
            if (n.HasMatrix && n.Matrix.Length != 16) throw new InvalidDataException($"node {i} '{n.Name}' has a matrix of {n.Matrix.Length} values");
        }
        foreach (var r in m.Roots) if (r < 0 || r >= m.Nodes.Count) throw new InvalidDataException($"root {r} is not a node of the model");
        for (int mi = 0; mi < m.Meshes.Count; mi++)
            for (int pi = 0; pi < m.Meshes[mi].Primitives.Count; pi++)
            {
                var p = m.Meshes[mi].Primitives[pi]; string where = $"mesh {mi} '{m.Meshes[mi].Name}' primitive {pi}";
                if (p.Positions == null || p.Positions.Length != p.VertexCount * 3) throw new InvalidDataException($"{where}: positions do not match the vertex count");
                void Check(Array a, int per, string name) { if (a != null && a.Length != p.VertexCount * per) throw new InvalidDataException($"{where}: {name} has {a.Length} values, {p.VertexCount * per} expected"); }
                Check(p.Normals, 3, "normals"); Check(p.Tangents, 4, "tangents"); Check(p.Uv0, 2, "UV0"); Check(p.Uv1, 2, "UV1"); Check(p.Colors, 4, "colours");
                Check(p.Joints, 4, "joints"); Check(p.Weights, 4, "weights"); Check(p.Joints1, 4, "joints (set 1)"); Check(p.Weights1, 4, "weights (set 1)");
                if ((p.Joints == null) != (p.Weights == null) || (p.Joints1 == null) != (p.Weights1 == null)) throw new InvalidDataException($"{where}: joints without weights (or the reverse)");
                if (p.Indices != null) foreach (int ix in p.Indices) if (ix < 0 || ix >= p.VertexCount) throw new InvalidDataException($"{where}: index {ix} is outside its {p.VertexCount} vertices");
                if (p.Material >= m.Materials.Count) throw new InvalidDataException($"{where} references material {p.Material}, the model has {m.Materials.Count}");
            }
        for (int si = 0; si < m.Skins.Count; si++)
        {
            var sk = m.Skins[si];
            if (sk.Joints == null) throw new InvalidDataException($"skin {si} '{sk.Name}' has no joints");
            foreach (var j in sk.Joints) if (j < 0 || j >= m.Nodes.Count) throw new InvalidDataException($"skin {si} '{sk.Name}' joint {j} is not a node of the model");
            if (sk.InverseBindMatrices != null && sk.InverseBindMatrices.Length != sk.Joints.Length * 16) throw new InvalidDataException($"skin {si} '{sk.Name}' has {sk.Joints.Length} joints but {sk.InverseBindMatrices.Length / 16} inverse bind matrices");
        }
        foreach (var t in m.Textures) if (t.Source >= m.Images.Count) throw new InvalidDataException($"texture '{t.Name}' references image {t.Source}, the model has {m.Images.Count}");
        foreach (var mt in m.Materials)
            foreach (var tx in new[] { mt.BaseColorTexture, mt.MetallicRoughnessTexture, mt.NormalTexture, mt.OcclusionTexture, mt.EmissiveTexture })
                if (tx >= m.Textures.Count) throw new InvalidDataException($"material '{mt.Name}' references texture {tx}, the model has {m.Textures.Count}");
        foreach (var an in m.Animations)
        {
            foreach (var c in an.Channels)
            {
                if (c.Sampler < 0 || c.Sampler >= an.Samplers.Count) throw new InvalidDataException($"animation '{an.Name}' channel uses sampler {c.Sampler}, it has {an.Samplers.Count}");
                if (c.Node >= m.Nodes.Count) throw new InvalidDataException($"animation '{an.Name}' targets node {c.Node}, the model has {m.Nodes.Count}");
            }
            foreach (var s in an.Samplers)
            {
                int per = s.Interpolation == "CUBICSPLINE" ? 3 : 1;
                int values = (s.Values?.Length ?? 0), keys = s.KeyCount;
                if (s.Components > 0 && values != keys * per * s.Components) throw new InvalidDataException($"animation '{an.Name}': a sampler has {keys} keys but {values} values ({s.Interpolation}, {s.Components} components)");
            }
        }
    }

    // ---------------------------------------------------------------- small helpers

    static JObject TexRef(int index, int texCoord) { var j = new JObject { ["index"] = index }; if (texCoord != 0) j["texCoord"] = texCoord; return j; }
    static JArray Arr(float[] v) => new JArray(v.Cast<object>());
    static JArray Arr(double[] v) => new JArray(v.Cast<object>());
    static bool IsDefault(float[] v, params float[] d) { if (v == null || v.Length != d.Length) return false; for (int i = 0; i < v.Length; i++) if (v[i] != d[i]) return false; return true; }
    static bool IsDefault(double[] v, params double[] d) { if (v == null || v.Length != d.Length) return false; for (int i = 0; i < v.Length; i++) if (v[i] != d[i]) return false; return true; }
    static string GuessMime(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 ? "image/png" : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ? "image/jpeg" : "image/png";
}
