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
// clearcoat and the like - and the textures they reference - survive a round trip; so are the texture samplers
// (wrap and filters), the `extras` of the asset (Sketchfab's author and license), nodes, meshes and materials, the
// asset's copyright and the scene's name. What the reader does not model is not here
// either, and the writer REFUSES rather than drops: a primitive with morph targets (counted by the reader, data not
// carried) is refused by name; extensions anywhere but on a material, and extras elsewhere, are not carried and said
// here. A model that cannot be written as it is - an index that does not fit a uint, a joint that does not fit a
// ushort, a node outside the lists, a value that is not a finite number (anywhere: glTF forbids NaN and infinity in
// accessor data too), a double that does not fit the float32 the file holds, key times that do not start at or after
// 0 and strictly increase, a hierarchy whose Parent and Children disagree, a node with two parents, a cycle, a root
// that is somebody's child - is refused by name. The file is written to a
// temporary name beside the target and moved into place, so a consumer never reads a half-written .glb.
public static class GlbWriter
{
    public const string GeneratorTag = "HAF GlbWriter";

    public static void Write(HafModel m, string path)
    {
        var built = Build(m);
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        // a .tmp name: Unity's asset pipeline ignores *.tmp, so a target under Assets/ never gets a stray .meta for the half-written file
        string tmp = full + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16)) built.WriteTo(f);   // straight from the BIN buffer: one copy of the model's bytes in memory, not two
            if (File.Exists(full)) File.Replace(tmp, full, null); else File.Move(tmp, full);
        }
        catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } throw; }   // the landing failed (locked target, a directory in the way): the target is as it was, the temporary is gone
    }

    /// <summary>The whole file as one array (the tests' path; a file goes through <see cref="Write(HafModel, string)"/>, a stream through <see cref="Write(HafModel, Stream)"/>).</summary>
    public static byte[] Write(HafModel m)
    {
        var built = Build(m);
        var outp = new byte[built.Total];
        using (var ms = new MemoryStream(outp)) built.WriteTo(ms);
        return outp;
    }

    /// <summary>The whole file onto a stream, from the BIN buffer directly: no second copy of the model's bytes.</summary>
    public static void Write(HafModel m, Stream to) => Build(m).WriteTo(to);

    /// <summary>A built file: the JSON chunk's bytes and the BIN chunk's buffer, written out in one pass by <see cref="WriteTo"/>.</summary>
    sealed class Built
    {
        public byte[] Json; public int JsonPad; public MemoryStream Bin; public int BinLength;
        public long Total => 12 + 8 + Json.Length + JsonPad + (BinLength > 0 ? 8 + BinLength : 0);
        public void WriteTo(Stream s)
        {
            var h = new byte[4];
            void U32(uint v) { h[0] = (byte)v; h[1] = (byte)(v >> 8); h[2] = (byte)(v >> 16); h[3] = (byte)(v >> 24); s.Write(h, 0, 4); }
            U32(0x46546C67u); U32(2u); U32((uint)Total);
            U32((uint)(Json.Length + JsonPad)); U32(0x4E4F534Au); s.Write(Json, 0, Json.Length);
            for (int i = 0; i < JsonPad; i++) s.WriteByte(0x20);
            if (BinLength > 0) { U32((uint)BinLength); U32(0x004E4942u); s.Write(Bin.GetBuffer(), 0, BinLength); }
        }
    }

    static Built Build(HafModel m)
    {
        // the BIN chunk is sized ONCE from the model (every array's bytes, every image, 4-byte padding per view): a
        // growing stream doubled a 106 MB model through 128 and 256 MB arrays, and thirty such files in one Mono
        // process ran its large-object heap out ("Insufficient memory", the writer drill, 2026-10-01)
        Validate(m);
        long bound = BinSizeUpperBound(m);
        if (bound > int.MaxValue - (64L << 20)) throw new InvalidDataException($"the model's binary data is {bound / 1e9:0.0} GB; this writer holds the file in one array and stops at 2 GB");
        var bin = new MemoryStream((int)bound);
        var views = new JArray(); var accessors = new JArray();
        // the generator names this writer once; a model that came through it already keeps its line, so a second write is byte-identical
        var asset = new JObject { ["version"] = "2.0", ["generator"] = m.Generator.StartsWith(GeneratorTag, StringComparison.Ordinal) ? m.Generator : GeneratorTag + (m.Generator.Length > 0 ? " (read from " + m.Generator + ")" : "") };
        if (m.Copyright.Length > 0) asset["copyright"] = m.Copyright;
        if (m.AssetExtrasJson != null) asset["extras"] = GlbReader.ParseToken(m.AssetExtrasJson);
        var root = new JObject { ["asset"] = asset };
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
                j["mimeType"] = Mime(im);
                j["bufferView"] = View(im.Bytes, im.Bytes.Length);
                images.Add(j);
            }
            root["images"] = images;
        }
        // the samplers as the model carries them (verbatim); a texture that points past them gets a default one
        int samplerCount = Math.Max(m.Samplers.Count, m.Textures.Count == 0 ? 0 : m.Textures.Max(t => t.Sampler) + 1);
        if (samplerCount > 0)
        {
            var samplers = new JArray();
            for (int i = 0; i < samplerCount; i++) samplers.Add(i < m.Samplers.Count ? GlbReader.ParseObject(m.Samplers[i]) : new JObject());
            root["samplers"] = samplers;
        }
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
                if (mt.AlphaCutoff != 0.5f) j["alphaCutoff"] = mt.AlphaCutoff;
                if (mt.DoubleSided) j["doubleSided"] = true;
                if (mt.ExtensionsJson != null) { var ext = GlbReader.ParseObject(mt.ExtensionsJson); j["extensions"] = ext; foreach (var prop in ext.Properties()) used.Add(prop.Name); CollectNestedExtensions(ext, used); }
                if (mt.ExtrasJson != null) j["extras"] = GlbReader.ParseToken(mt.ExtrasJson);
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
                if (me.ExtrasJson != null) j["extras"] = GlbReader.ParseToken(me.ExtrasJson);
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
                if (n.ExtrasJson != null) j["extras"] = GlbReader.ParseToken(n.ExtrasJson);
                nodes.Add(j);
            }
            root["nodes"] = nodes;
            var scene = new JObject { ["nodes"] = new JArray(m.Roots.Cast<object>()) };
            if (m.SceneName.Length > 0) scene["name"] = m.SceneName;
            root["scenes"] = new JArray { scene };
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

        // ---- the container: the JSON chunk's bytes, the BIN buffer as it stands
        var json = Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));
        var built = new Built { Json = json, JsonPad = (4 - json.Length % 4) % 4, Bin = bin, BinLength = (int)bin.Length };
        if (built.Total > int.MaxValue) throw new InvalidDataException("the model does not fit this writer (over 2 GB)");
        return built;
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
            if (n.HasMatrix) FitsFloat(n.Matrix, $"node {i} '{n.Name}' matrix");
            else
            {
                Len(n.Translation, 3, $"node {i} '{n.Name}' translation"); Len(n.Rotation, 4, $"node {i} '{n.Name}' rotation"); Len(n.Scale, 3, $"node {i} '{n.Name}' scale");
                FitsFloat(n.Translation, $"node {i} '{n.Name}' translation"); FitsFloat(n.Rotation, $"node {i} '{n.Name}' rotation"); FitsFloat(n.Scale, $"node {i} '{n.Name}' scale");
            }
        }
        foreach (var r in m.Roots) if (r < 0 || r >= m.Nodes.Count) throw new InvalidDataException($"root {r} is not a node of the model");
        // the hierarchy is written from Children; Parent is what a reader derives from it - a model built by hand must tell one story
        var parentOf = new int[m.Nodes.Count]; for (int i = 0; i < parentOf.Length; i++) parentOf[i] = -1;
        for (int i = 0; i < m.Nodes.Count; i++)
            foreach (var c in m.Nodes[i].Children)
            {
                if (c == i) throw new InvalidDataException($"node {i} '{m.Nodes[i].Name}' lists itself as a child");
                if (parentOf[c] != -1) throw new InvalidDataException($"node {c} '{m.Nodes[c].Name}' is a child of both node {parentOf[c]} and node {i} (a node has one parent)");
                parentOf[c] = i;
            }
        for (int i = 0; i < m.Nodes.Count; i++)
            if (m.Nodes[i].Parent != parentOf[i]) throw new InvalidDataException($"node {i} '{m.Nodes[i].Name}' has Parent {m.Nodes[i].Parent} but {(parentOf[i] == -1 ? "no node lists it as a child" : $"node {parentOf[i]} lists it as a child")} - set Parent and Children together");
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            int at = i, steps = 0;
            while (parentOf[at] != -1) { at = parentOf[at]; if (++steps > m.Nodes.Count) throw new InvalidDataException($"node {i} '{m.Nodes[i].Name}' is its own ancestor - the hierarchy has a cycle"); }
        }
        for (int i = 0; i < m.Roots.Count; i++)
        {
            if (parentOf[m.Roots[i]] != -1) throw new InvalidDataException($"root {m.Roots[i]} '{m.Nodes[m.Roots[i]].Name}' is a child of node {parentOf[m.Roots[i]]} (a scene lists root nodes only)");
            if (m.Roots.IndexOf(m.Roots[i]) != i) throw new InvalidDataException($"root {m.Roots[i]} is listed twice");
        }
        for (int mi = 0; mi < m.Meshes.Count; mi++)
            for (int pi = 0; pi < m.Meshes[mi].Primitives.Count; pi++)
            {
                var p = m.Meshes[mi].Primitives[pi]; string where = $"mesh {mi} '{m.Meshes[mi].Name}' primitive {pi}";
                if (p.VertexCount <= 0) throw new InvalidDataException($"{where} has no vertices (glTF has no empty accessor)");
                if (p.Positions == null || p.Positions.Length != p.VertexCount * 3) throw new InvalidDataException($"{where}: positions do not match the vertex count");
                if (p.MorphTargets > 0) throw new InvalidDataException($"{where} has {p.MorphTargets} morph target(s), whose data the model does not carry - refused rather than written without them");
                Finite(p.Positions, $"{where} POSITION"); Finite(p.Normals, $"{where} NORMAL"); Finite(p.Tangents, $"{where} TANGENT"); Finite(p.Uv0, $"{where} TEXCOORD_0"); Finite(p.Uv1, $"{where} TEXCOORD_1");
                Finite(p.Colors, $"{where} COLOR_0"); Finite(p.Weights, $"{where} WEIGHTS_0"); Finite(p.Weights1, $"{where} WEIGHTS_1");
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
            if (sk.Skeleton >= m.Nodes.Count) throw new InvalidDataException($"skin {si} '{sk.Name}' names skeleton node {sk.Skeleton}, the model has {m.Nodes.Count} nodes");
            FitsFloat(sk.InverseBindMatrices, $"skin {si} '{sk.Name}' inverse bind matrices");
        }
        // the verbatim JSON the model carries must be JSON objects, or the file would not parse (said here, not as a parser's exception from the middle of the write)
        for (int i = 0; i < m.Samplers.Count; i++) if (!IsJsonObject(m.Samplers[i])) throw new InvalidDataException($"sampler {i} is not a JSON object: {m.Samplers[i]}");
        for (int i = 0; i < m.Nodes.Count; i++) if (m.Nodes[i].ExtrasJson != null && !IsJson(m.Nodes[i].ExtrasJson)) throw new InvalidDataException($"node {i} '{m.Nodes[i].Name}': extras is not JSON");
        for (int i = 0; i < m.Meshes.Count; i++) if (m.Meshes[i].ExtrasJson != null && !IsJson(m.Meshes[i].ExtrasJson)) throw new InvalidDataException($"mesh {i} '{m.Meshes[i].Name}': extras is not JSON");
        for (int i = 0; i < m.Materials.Count; i++)
        {
            if (m.Materials[i].ExtrasJson != null && !IsJson(m.Materials[i].ExtrasJson)) throw new InvalidDataException($"material {i} '{m.Materials[i].Name}': extras is not JSON");
            if (m.Materials[i].ExtensionsJson != null && !IsJsonObject(m.Materials[i].ExtensionsJson)) throw new InvalidDataException($"material {i} '{m.Materials[i].Name}': extensions is not a JSON object");
        }
        foreach (var t in m.Textures) if (t.Source >= m.Images.Count) throw new InvalidDataException($"texture '{t.Name}' references image {t.Source}, the model has {m.Images.Count}");
        for (int i = 0; i < m.Images.Count; i++)
        {
            var im = m.Images[i];
            if (im.Bytes == null) throw new InvalidDataException($"image {i} '{im.Name}' has no bytes - a model whose image was never resolved cannot be written whole");
            if (Mime(im) == null) throw new InvalidDataException($"image {i} '{im.Name}' is {(im.MimeType.Length > 0 ? im.MimeType : "of an unrecognised format")}; glTF embeds only PNG and JPEG");
        }
        if (m.AssetExtrasJson != null && !IsJson(m.AssetExtrasJson)) throw new InvalidDataException("asset extras is not JSON");
        foreach (var mt in m.Materials)
        {
            foreach (var tx in new[] { mt.BaseColorTexture, mt.MetallicRoughnessTexture, mt.NormalTexture, mt.OcclusionTexture, mt.EmissiveTexture })
                if (tx >= m.Textures.Count) throw new InvalidDataException($"material '{mt.Name}' references texture {tx}, the model has {m.Textures.Count}");
            Len(mt.BaseColorFactor, 4, $"material '{mt.Name}' baseColorFactor"); Len(mt.EmissiveFactor, 3, $"material '{mt.Name}' emissiveFactor");
            Finite(mt.BaseColorFactor, $"material '{mt.Name}' baseColorFactor"); Finite(mt.EmissiveFactor, $"material '{mt.Name}' emissiveFactor");
            Finite(new[] { mt.MetallicFactor, mt.RoughnessFactor, mt.NormalScale, mt.OcclusionStrength, mt.AlphaCutoff }, $"material '{mt.Name}' factors");
        }
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
                if (keys == 0) throw new InvalidDataException($"animation '{an.Name}': a sampler has no keys (glTF has no empty accessor)");
                if (s.Components > 0 && values != keys * per * s.Components) throw new InvalidDataException($"animation '{an.Name}': a sampler has {keys} keys but {values} values ({s.Interpolation}, {s.Components} components)");
                Finite(s.Times, $"animation '{an.Name}' key times"); Finite(s.Values, $"animation '{an.Name}' key values");
                if (s.Times[0] < 0) throw new InvalidDataException(FormattableString.Invariant($"animation '{an.Name}': key times start at {s.Times[0]} - glTF starts them at or after 0"));
                for (int k = 1; k < s.Times.Length; k++) if (s.Times[k] <= s.Times[k - 1]) throw new InvalidDataException(FormattableString.Invariant($"animation '{an.Name}': key times must strictly increase (key {k} is {s.Times[k]} after {s.Times[k - 1]})"));
            }
        }
    }

    // ---------------------------------------------------------------- small helpers

    static void Len(Array a, int n, string what) { if (a == null || a.Length != n) throw new InvalidDataException($"{what} has {a?.Length ?? 0} values, {n} expected"); }
    // glTF forbids NaN and infinity in accessor data as well as in JSON; null arrays are absent attributes, fine
    static void Finite(float[] a, string what) { if (a == null) return; foreach (float v in a) if (float.IsNaN(v) || float.IsInfinity(v)) throw new InvalidDataException($"{what}: a value is not a finite number (glTF forbids NaN and infinity)"); }
    static void Finite(double[] a, string what) { if (a == null) return; foreach (double v in a) if (double.IsNaN(v) || double.IsInfinity(v)) throw new InvalidDataException($"{what}: a value is not a finite number (glTF forbids NaN and infinity)"); }
    // the model holds these as doubles; the file holds them as float32 (inverse bind matrices are cast; a node's transform is a JSON number every reader parses into a float) - 1e100 would cast to infinity past the finite check
    static void FitsFloat(double[] a, string what) { Finite(a, what); if (a == null) return; foreach (double v in a) if (Math.Abs(v) > float.MaxValue) throw new InvalidDataException(FormattableString.Invariant($"{what}: {v:R} does not fit a 32-bit float")); }   // invariant: the message is read on a Dutch machine too
    /// <summary>Every extension name used anywhere INSIDE a carried payload (a specularTexture's KHR_texture_transform), so extensionsUsed declares it too.</summary>
    static void CollectNestedExtensions(JToken t, ISet<string> used)
    {
        if (t is JObject o) { if (o["extensions"] is JObject ext) foreach (var p in ext.Properties()) used.Add(p.Name); foreach (var p in o.Properties()) CollectNestedExtensions(p.Value, used); }
        else if (t is JArray a) foreach (var e in a) CollectNestedExtensions(e, used);
    }
    /// <summary>The MIME type the image is written with: the declared one when it is one glTF embeds, else what the bytes say; null when neither is PNG or JPEG.</summary>
    static string Mime(HafImage im)
    {
        if (im.MimeType == "image/png" || im.MimeType == "image/jpeg") return im.MimeType;
        if (im.MimeType.Length > 0 || im.Bytes == null) return null;
        return GuessMime(im.Bytes);
    }

    static JObject TexRef(int index, int texCoord) { var j = new JObject { ["index"] = index }; if (texCoord != 0) j["texCoord"] = texCoord; return j; }
    static JArray Arr(float[] v) => new JArray(v.Cast<object>());
    static JArray Arr(double[] v) => new JArray(v.Cast<object>());
    static bool IsDefault(float[] v, params float[] d) { if (v == null || v.Length != d.Length) return false; for (int i = 0; i < v.Length; i++) if (v[i] != d[i]) return false; return true; }
    static bool IsDefault(double[] v, params double[] d) { if (v == null || v.Length != d.Length) return false; for (int i = 0; i < v.Length; i++) if (v[i] != d[i]) return false; return true; }
    static bool IsJsonObject(string s)
    {
        try { GlbReader.ParseObject(s); return true; } catch (Newtonsoft.Json.JsonException) { return false; } catch (InvalidDataException) { return false; }
    }
    static bool IsJson(string s)
    {
        try { GlbReader.ParseToken(s); return true; } catch (Newtonsoft.Json.JsonException) { return false; } catch (InvalidDataException) { return false; }
    }

    static string GuessMime(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "image/png" : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg" : null;
}
