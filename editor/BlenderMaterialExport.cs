// BlenderMaterialExport.cs - a glTF material as it comes back out of Blender (part 8a-ii of replacing deploy_convert.py,
// 2026-10-11): the importer builds a node tree from the source material (io_scene_gltf2/blender/imp/pbrMetallicRoughness.py,
// texture.py, image.py), the exporter reads that tree back and writes a material (blender/exp/material/materials.py,
// pbr_metallic_roughness.py, texture_info.py, texture.py, image.py, encode_image.py, search_node_tree.py gather_alpha_info,
// exp/sampler.py, exp/export.py __fix_json). The same round trip in prep_model.py and in deploy_convert.py. Measured on
// 57 registry, recipe and deploy sources and 44 prep fixtures (tools/deploy-drill/blender_material_dump.py), then read:
//   * every factor is the node socket's float32: metallic and roughness omitted at 1, the base colour at [1, 1, 1, 1]
//     (its RGB clamped to 0..1 by the exporter, the alpha by the importer's wiring: BlenderPrep.WrittenBaseColour),
//     doubleSided written only when true, alphaMode only when not OPAQUE, alphaCutoff only when not 0.5;
//   * the alpha wiring (imp base_color): a material without a base colour texture and without vertex colours gets a
//     CONSTANT alpha (1 for OPAQUE, 1 or 0 for MASK by `alpha >= cutoff` on the JSON doubles, the factor for BLEND) which
//     the exporter reads back as OPAQUE at 1, MASK with the default cutoff at 0, BLEND otherwise (exp gather_alpha_info);
//     with a texture or vertex colours a MASK at a cutoff of 0 is opaque, over 1 is a constant 0 (MASK), else the clip
//     nodes carry the cutoff (float32; 0.5 omitted), a BLEND with an alpha factor rounding to 1 in float32 is OPAQUE;
//   * specular-glossiness becomes metallic-roughness (the diffuse colour and texture, metallic 0, roughness 1 - glossiness
//     in float32) with KHR_materials_specular's specularColorFactor = the specular factor (omitted at [1, 1, 1]) and
//     KHR_materials_ior 1000 beside it; unlit keeps KHR_materials_unlit with metallic 0 and roughness 0.9;
//     KHR_materials_specular's specularFactor is omitted at 1, its colour at white; a clearcoat's factors are float32;
//     KHR_materials_ior is kept only beside specular, transmission or volume (export.py __check_ior);
//   * textures: one per material slot (the occlusion and metallic-roughness slots share one when they read the same image
//     of the same size: the ORM composition), in the serializer's order of the material's members (emissive, the
//     extensions, normal, occlusion, base colour, metallic-roughness); a sampler per distinct value - mag 9728 / min 9984
//     when both source filters are NEAREST (mipmaps ignored; one given alone as NEAREST too), else 9729 / 9987; wrap
//     omitted when both REPEAT, else both written (the importer's mixed-wrap math nodes read back per axis);
//   * images: one per source image used, in order of first use, the bytes the source's own when the exporter's happy path
//     holds (every channel from one image, each on its own channel) AND the packed bytes have the MIME type asked -
//     image/jpeg by the bytes' magic, else image/png, PNG FORCED when the material reads the texture's alpha -, named
//     after the Blender image (the source's name, or "Image_<index>", numbered) WITHOUT its extension
//     (os.path.splitext), or after the file's stem when the image came from a .png/.jpg/.jpeg file;
//   * two readers went through the sources after the measurement (2026-10-11): a specular factor over 1 folds into the
//     colour; beside a specular colour TEXTURE the colour factor is written whenever the source's is not white (the
//     exporter compares a list to a tuple: always different) or the factor is over 1 (then (fac, fac, fac)); a texCoord
//     the mesh has no UV layer for is written as 0 (materials.py get_final_material); a uri image's type is its file
//     EXTENSION (.jpg/.jpeg/.jpe: image.py __is_blender_image_a_jpeg), bytes of another magic are encoded again; an
//     unknown wrap value goes the mixed-wrap way and reads back as CLAMP; a colour socket clamps a negative component to
//     0 on the way in; the importer folds a grey emissive factor into the strength on the JSON doubles; the Blender
//     image names are numbered in the importer's creation order (materials as the meshes use them, the slots emissive,
//     base colour, metallic-roughness, normal, occlusion, specular).
// Left by name (Result.Fallback): an image the exporter would encode again (a JPEG whose alpha is read, a uri image whose
// bytes are not its extension's, a channel composition over two images - occlusion beside a metallic-roughness image
// of the same size -, a WebP or an unreadable image), a specular-glossiness texture, a material read with two sets of
// UV indices, KHR_texture_transform (the reader does not carry a texture's transform), clearcoat textures,
// transmission, volume, sheen, anisotropy.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

public static class BlenderMaterialExport
{
    public sealed class Result
    {
        public string Fallback;
        public readonly HashSet<int> RetainedJpegAlphaImages = new HashSet<int>();
        public readonly List<string> Names = new List<string>();            // the Blender material names, in the written order
        public readonly List<JObject> Materials = new List<JObject>();
        public readonly List<(int image, int sampler)> Textures = new List<(int, int)>();
        public readonly List<(string name, string mime, byte[] bytes, int source)> Images = new List<(string, string, byte[], int)>();
        public readonly List<JObject> Samplers = new List<JObject>();
        public int SpecularGlossiness, Unnamed, VertexColourVariants;
    }

    /// <summary>The materials of a file as prep_model.py's export writes them: in the order the exporter's tree lists them
    /// (first use along the serializer's walk), a texture per slot, an image per source image, a sampler per value.
    /// Production callers can preserve alpha-reading JPEGs (glTF alpha=1); the default keeps the strict Blender oracle.</summary>
    public static Result Build(HafModel m, BlenderNames.Result names, bool preserveJpegAlpha = false)
    {
        var world = VehicleProbe.BlenderWorldMatrices(m, null);
        var materialsOfMesh = new Dictionary<int, List<string>>();
        var withFaces = new HashSet<int>();
        for (int n = 0; n < m.Nodes.Count; n++)
        {
            int mi = m.Nodes[n].Mesh; if (mi < 0) continue;
            var list = new List<string>(); var slots = new List<(int, bool)>();
            foreach (var p in m.Meshes[mi].Primitives)
            {
                if (!(p.Mode == 4 || p.Mode == 5 || p.Mode == 6) || p.Positions == null) continue;
                int corners = p.Indices != null ? p.Indices.Length : p.VertexCount;
                if (corners < 3) continue;
                var key = (p.Material, p.Colors != null);
                if (slots.Contains(key)) continue;
                slots.Add(key);
                list.Add(key.Item1 < 0 && !key.Item2 ? null : names.MaterialOf[(key.Item1 < 0 ? names.MeshDatablockNode[n] : -1, key.Item1, key.Item2)]);
            }
            if (slots.Count > 0) withFaces.Add(n);
            materialsOfMesh[n] = list;
        }
        var tree = BlenderExportTree.Build(m, names, world, withFaces, null, mn => materialsOfMesh.TryGetValue(mn, out var l) ? l : new List<string>(), bones: false);
        return Build(m, names, tree.Materials, preserveJpegAlpha: preserveJpegAlpha);
    }

    /// <summary>The materials named, in that order (the Blender material names BlenderNames gives). meshUvSets, when
    /// supplied, describes the final meshes by material name instead of the imported meshes (the rig join).</summary>
    public static Result Build(HafModel m, BlenderNames.Result names, IList<string> blenderNames, IDictionary<string, int> meshUvSets = null, bool preserveJpegAlpha = false)
    {
        var r = new Result();
        var problems = new List<string>(); if (meshUvSets == null) BlenderExportTree.MaterialUvProblems(m, problems);
        if (problems.Count > 0) { r.Fallback = problems[0]; return r; }
        if (m.ExtensionsUsed.Contains("KHR_texture_transform")) { r.Fallback = "KHR_texture_transform (the reader does not carry a texture's transform, which the importer turns into a mapping node)"; return r; }
        var sourceOf = new Dictionary<string, (int material, bool vertexColor)>(StringComparer.Ordinal);
        foreach (var kv in names.MaterialOf) if (!sourceOf.ContainsKey(kv.Value)) sourceOf[kv.Value] = (kv.Key.material, kv.Key.vertexColor);
        // the UV layers of the meshes that use each material (the importer makes one per TEXCOORD_n, eight at most): a
        // texCoord beyond them is written as 0 (every mesh of a material agrees, or MaterialUvProblems named it)
        var uvSets = new Dictionary<(int material, bool vertexColor), int>();
        foreach (int meshIndex in m.Nodes.Where(n => n.Mesh >= 0).Select(n => n.Mesh).Distinct())
        {
            var mesh = m.Meshes[meshIndex];
            int sets = 0;
            foreach (var p in mesh.Primitives) sets = Math.Max(sets, p.Uv0 == null ? 0 : p.Uv1 == null ? 1 : 2 + (p.UvMore?.Count ?? 0));
            sets = Math.Min(sets, 8);
            foreach (var p in mesh.Primitives) if (p.Material >= 0)
            {
                var variant = (p.Material, p.Colors != null);
                uvSets[variant] = Math.Max(uvSets.TryGetValue(variant, out int s) ? s : 0, sets);
            }
        }
        var ctx = new Context(m, r, preserveJpegAlpha);
        ctx.PreNumber(names.MaterialsInOrder, sourceOf);
        foreach (string name in blenderNames)
        {
            if (!sourceOf.TryGetValue(name, out var src)) { r.Fallback = $"the material '{name}' has no source material"; return r; }
            if (src.vertexColor) r.VertexColourVariants++;
            int sets;
            if (meshUvSets != null) meshUvSets.TryGetValue(name, out sets);
            else uvSets.TryGetValue((src.material, src.vertexColor), out sets);
            JObject j;
            try { j = src.material < 0 ? DefaultMaterial(name) : One(ctx, m.Materials[src.material], name, src.vertexColor, sets); }
            catch (LeftException e) { r.Fallback = $"material '{name}': {e.Message}"; return r; }
            if (src.material >= 0 && (m.Materials[src.material].NameAbsent || m.Materials[src.material].Name.Length == 0)) r.Unnamed++;
            r.Names.Add(name); r.Materials.Add(j);
        }
        return r;
    }

    sealed class LeftException : Exception { public LeftException(string why) : base(why) { } }

    /// <summary>The importer's material for a coloured primitive without one, as the exporter writes it (measured in the
    /// prep: no base colour, metallic 0, roughness 0.5, double sided).</summary>
    static JObject DefaultMaterial(string name) =>
        new JObject { ["name"] = name, ["doubleSided"] = true, ["pbrMetallicRoughness"] = new JObject { ["metallicFactor"] = 0.0, ["roughnessFactor"] = 0.5 } };

    /// <summary>The textures, images and samplers of one file as they are numbered: first use, each once.</summary>
    sealed class Context
    {
        public readonly HafModel M; public readonly Result R;
        readonly Dictionary<string, int> imageOf = new Dictionary<string, int>(StringComparer.Ordinal);   // (mime, name, bytes) -> written image
        static string Sha(byte[] b) { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(b)); }
        readonly Dictionary<string, int> samplerOf = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly BlenderNames.NamePool imagePool = new BlenderNames.NamePool();
        readonly Dictionary<int, string> blenderImageName = new Dictionary<int, string>();
        readonly bool preserveJpegAlpha;
        public Context(HafModel m, Result r, bool preserveJpegAlpha) { M = m; R = r; this.preserveJpegAlpha = preserveJpegAlpha; }

        /// <summary>The importer makes the images as it builds the materials - in the order the meshes use them, each
        /// material's slots emissive, base colour (or the diffuse), metallic-roughness, normal, occlusion, then the
        /// specular ones -, so a name taken twice is numbered in that order, not the exporter's.</summary>
        public void PreNumber(IList<string> materialsInOrder, Dictionary<string, (int material, bool vertexColor)> sourceOf)
        {
            foreach (string name in materialsInOrder)
            {
                if (!sourceOf.TryGetValue(name, out var src) || src.material < 0) continue;
                var mat = M.Materials[src.material];
                var ext = ParseExtensions(mat);
                var sg = ext["KHR_materials_unlit"] == null ? ext["KHR_materials_pbrSpecularGlossiness"] as JObject : null;
                bool unlit = ext["KHR_materials_unlit"] != null;
                var slots = new List<int> { unlit ? -1 : mat.EmissiveTexture };
                if (sg != null) { slots.Add(sg["diffuseTexture"] is JObject dt ? (int)dt["index"] : -1); slots.Add(sg["specularGlossinessTexture"] is JObject sgt ? (int)sgt["index"] : -1); }
                else slots.Add(mat.BaseColorTexture);
                if (!unlit)
                {
                    slots.Add(sg != null ? -1 : mat.MetallicRoughnessTexture); slots.Add(mat.NormalTexture); slots.Add(mat.OcclusionTexture);
                }
                if (!unlit && ext["KHR_materials_specular"] is JObject sp) { slots.Add(sp["specularTexture"] is JObject st ? (int)st["index"] : -1); slots.Add(sp["specularColorTexture"] is JObject sct ? (int)sct["index"] : -1); }
                foreach (int t in slots) if (t >= 0 && t < M.Textures.Count && M.Textures[t].Source >= 0 && M.Textures[t].Source < M.Images.Count) BlenderImageName(M.Textures[t].Source);
            }
        }

        /// <summary>The Blender image's name: the source's, or the uri's file name, or "Image_&lt;index&gt;" (imp/image.py),
        /// numbered in the pool in the order the importer makes them.</summary>
        string BlenderImageName(int image)
        {
            if (blenderImageName.TryGetValue(image, out var done)) return done;
            var im = M.Images[image];
            string name = im.Name.Length > 0 ? im.Name : (im.Uri.Length > 0 && !im.Uri.StartsWith("data:", StringComparison.Ordinal) ? System.IO.Path.GetFileName(UriPath(im.Uri)) : "Image_" + image);
            name = imagePool.Unique(name);
            blenderImageName[image] = name;
            return name;
        }

        static string UriPath(string uri) { try { return Uri.UnescapeDataString(uri.Replace('\\', '/')); } catch { return uri; } }

        /// <summary>A texture for a material slot: its image (shared across textures) and its sampler (by value).</summary>
        public int Texture(int texture, bool alphaRead)
        {
            if (texture < 0 || texture >= M.Textures.Count) throw new LeftException($"a texture index outside the file ({texture})");
            var t = M.Textures[texture];
            int img = Image(t.Source, alphaRead);
            int samp = Sampler(t.Sampler);
            R.Textures.Add((img, samp));
            return R.Textures.Count - 1;
        }

        int Image(int source, bool alphaRead)
        {
            if (source < 0 || source >= M.Images.Count) throw new LeftException($"an image index outside the file ({source})");
            var im = M.Images[source];
            if (im.Bytes == null || im.Bytes.Length == 0) throw new LeftException($"image {source}: its bytes could not be read");
            bool jpeg = im.Bytes.Length > 3 && im.Bytes[0] == 0xFF && im.Bytes[1] == 0xD8 && im.Bytes[2] == 0xFF;
            bool png = im.Bytes.Length > 4 && im.Bytes[0] == 0x89 && im.Bytes[1] == 0x50 && im.Bytes[2] == 0x4E && im.Bytes[3] == 0x47;
            if (!jpeg && !png) throw new LeftException($"image {source}: neither a PNG nor a JPEG (the exporter encodes it again)");
            // __gather_mime_type: PNG forced when the material reads the texture's alpha; else AUTO - a packed image by its
            // bytes' magic, an image loaded from a FILE by its extension (.jpg, .jpeg, .jpe). __encode_from_image returns
            // the bytes only when their magic matches the type chosen: anything else is encoded again by Blender
            bool fromFile = im.Uri.Length > 0 && !im.Uri.StartsWith("data:", StringComparison.Ordinal);
            bool saysJpeg = jpeg;
            if (fromFile)
            {
                string lower = UriPath(im.Uri).ToLowerInvariant();
                if (lower.EndsWith(".webp")) throw new LeftException($"image {source}: a .webp file (the exporter writes WebP)");
                saysJpeg = lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".jpe");
            }
            string mime = alphaRead ? "image/png" : saysJpeg ? "image/jpeg" : "image/png";
            // A JPEG has alpha=1 in glTF. Production Generate may keep those source bytes rather than
            // recompress RGB into Blender's PNG. Retain the requested MIME in the dedup key so an opaque
            // and an alpha-reading use still have the same separate image indices as Blender's export.
            string requestedMime = mime;
            if (preserveJpegAlpha && alphaRead && jpeg && PortableJpegHeader(im.Bytes))
                mime = "image/jpeg";
            if (mime == "image/png" && !png) throw new LeftException($"image {source}: " + (alphaRead ? "a JPEG whose alpha is read (the exporter writes it again as PNG)" : "a file named as a PNG that holds JPEG bytes (encoded again)"));
            if (mime == "image/jpeg" && !jpeg) throw new LeftException($"image {source}: a file named as a JPEG that holds PNG bytes (encoded again)");
            // __gather_name: the file's stem when the image came from a .png/.jpg/.jpeg file, else the Blender name without its extension
            string blenderName = BlenderImageName(source);
            string name = null;
            if (im.Uri.Length > 0 && !im.Uri.StartsWith("data:", StringComparison.Ordinal))
            {
                string file = System.IO.Path.GetFileName(UriPath(im.Uri));
                string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if ((ext == ".png" || ext == ".jpg" || ext == ".jpeg") && file.Length > ext.Length) name = file.Substring(0, file.Length - ext.Length);
            }
            if (name == null) { name = SplitExt(blenderName); if (name.Length == 0) name = "Image"; }
            // __make_image is cached on the bytes, the MIME type and the name: two source images alike in all three are one
            // written image (BinaryData compares its bytes); alike in bytes alone they are two (the T-62: 112 twins)
            string key = requestedMime + "\t" + name + "\t" + Sha(im.Bytes);
            if (imageOf.TryGetValue(key, out int done)) { if (requestedMime != mime) R.RetainedJpegAlphaImages.Add(done); return done; }
            R.Images.Add((name, mime, im.Bytes, source));
            if (requestedMime != mime) R.RetainedJpegAlphaImages.Add(R.Images.Count - 1);
            imageOf[key] = R.Images.Count - 1;
            return R.Images.Count - 1;
        }

        /// <summary>os.path.splitext(name)[0]: the part before the last dot, unless the dot leads the last path component.</summary>
        internal static string SplitExt(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0) return name;
            int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (dot <= slash + 1) return name;
            int i = slash + 1; while (i < dot && name[i] == '.') i++;
            return i == dot ? name : name.Substring(0, dot);
        }

        /// <summary>The written sampler (exp/sampler.py) of a source sampler (imp/texture.py set_filtering, the wrap nodes):
        /// -1 or an index into the file's samplers (verbatim JSON).</summary>
        int Sampler(int sampler)
        {
            int mag = -1, min = -1, wrapS = 10497, wrapT = 10497;
            if (sampler >= 0 && sampler < M.Samplers.Count)
            {
                var s = JObject.Parse(M.Samplers[sampler]);
                mag = (int?)s["magFilter"] ?? -1; min = (int?)s["minFilter"] ?? -1;
                wrapS = (int?)s["wrapS"] ?? 10497; wrapT = (int?)s["wrapT"] ?? 10497;
            }
            // set_filtering: mipmaps ignored; Closest when both are NEAREST or the only one given is
            if (min == 9984 || min == 9986) min = 9728; else if (min == 9985 || min == 9987) min = 9729;
            bool closest = (min == 9728 && mag == 9728) || (min == 9728 && mag < 0) || (min < 0 && mag == 9728);
            var j = new JObject { ["magFilter"] = closest ? 9728 : 9729, ["minFilter"] = closest ? 9984 : 9987 };
            // the wrap: both REPEAT omitted; both CLAMP or both MIRROR from the node's extension; any other pair - mixed, or a
            // value the importer does not know - goes through its math nodes on an EXTEND node: a REPEAT axis reads back
            // as 10497, a MIRROR axis as 33648, anything else as the node's CLAMP (33071)
            bool known(int w) => w == 10497 || w == 33071 || w == 33648;
            int ws, wt;
            if (wrapS == wrapT && known(wrapS)) { ws = wt = wrapS; }
            else { ws = wrapS == 10497 ? 10497 : wrapS == 33648 ? 33648 : 33071; wt = wrapT == 10497 ? 10497 : wrapT == 33648 ? 33648 : 33071; }
            if (!(ws == 10497 && wt == 10497)) { j["wrapS"] = ws; j["wrapT"] = wt; }
            string key = j.ToString(Newtonsoft.Json.Formatting.None);
            if (samplerOf.TryGetValue(key, out int done)) return done;
            R.Samplers.Add(j); samplerOf[key] = R.Samplers.Count - 1;
            return R.Samplers.Count - 1;
        }
    }

    static double F(double v) => (double)(float)v;
    /// <summary>A colour or strength socket's value: float32, a negative clamped to 0 by the socket's hard range.</summary>
    static float Socket(double v) { float x = (float)v; return x < 0f ? 0f : x; }
    internal static string SplitExtForTest(string name) => Context.SplitExt(name);
    static bool Unit(float[] c) => c.Length == 3 && c[0] == 1f && c[1] == 1f && c[2] == 1f;

    /// <summary>A TextureInfo: the texCoord is the UV layer the importer's UVMap node names, found on the mesh - a layer
    /// the mesh does not have falls back to the active one, 0 (materials.py get_final_material).</summary>
    static JObject TextureInfo(int index, int texCoord, int uvSets)
    {
        var j = new JObject { ["index"] = index };
        if (texCoord > 0 && texCoord < uvSets) j["texCoord"] = texCoord;
        return j;
    }

    static JObject ParseExtensions(HafMaterial mat) => mat.ExtensionsJson != null ? JObject.Parse(mat.ExtensionsJson) : new JObject();

    /// <summary>One material. The textures are taken in the order the serializer numbers them: the material's members in
    /// alphabetical order - emissiveTexture, extensions (the exporter's dict order: clearcoat, transmission, emissive
    /// strength, volume, specular, sheen, anisotropy, ior), normalTexture, occlusionTexture, pbrMetallicRoughness
    /// (baseColorTexture, metallicRoughnessTexture).</summary>
    static JObject One(Context c, HafMaterial mat, string name, bool vertexColor, int uvSets)
    {
        var ext = ParseExtensions(mat);
        bool unlit = ext["KHR_materials_unlit"] != null;
        var sg = !unlit ? ext["KHR_materials_pbrSpecularGlossiness"] as JObject : null;
        string mode = string.IsNullOrEmpty(mat.AlphaMode) ? "OPAQUE" : mat.AlphaMode;
        if (mode != "OPAQUE" && mode != "MASK") mode = "BLEND";   // the importer reads anything else as a blend
        // ---- the base colour: the factor (BlenderPrep's rule), the texture, the alpha as wired
        double[] bc = mat.BaseColorFactor ?? new double[] { 1, 1, 1, 1 };
        int baseTex = mat.BaseColorTexture, baseTexCoord = mat.BaseColorTexCoord;
        if (sg != null)
        {
            c.R.SpecularGlossiness++;
            bc = sg["diffuseFactor"] is JArray df && df.Count == 4 ? df.Select(x => (double)x).ToArray() : new double[] { 1, 1, 1, 1 };
            if (sg["diffuseTexture"] is JObject dt) { baseTex = (int)dt["index"]; baseTexCoord = dt["texCoord"] != null ? (int)dt["texCoord"] : 0; } else { baseTex = -1; baseTexCoord = 0; }
            if (sg["specularGlossinessTexture"] != null) throw new LeftException("a specular-glossiness texture (not modelled)");
        }
        double cutoff = mat.AlphaCutoff;
        bool textured = baseTex >= 0 || vertexColor;
        // the alpha socket as the importer leaves it: a constant, or wired (the texture's alpha, the vertex colour) with
        // the clip nodes under MASK and the factor node when the factor is not 1
        string alphaMode; double? alphaCutoff = null; double alphaOut;
        bool alphaRead = false;   // whether the base colour texture's Alpha output is linked: PNG forced for its image
        if (!textured)
        {
            double a = mode == "OPAQUE" ? 1 : mode == "MASK" ? (bc[3] >= cutoff ? 1 : 0) : F(bc[3]);
            if (a == 1) { alphaMode = "OPAQUE"; alphaOut = 1; }
            else if (a == 0) { alphaMode = "MASK"; alphaOut = 0; }
            else { alphaMode = "BLEND"; alphaOut = a; }
        }
        else if (mode == "OPAQUE") { alphaMode = "OPAQUE"; alphaOut = 1; }
        else if (mode == "MASK" && cutoff == 0) { alphaMode = "OPAQUE"; alphaOut = 1; }
        else if (mode == "MASK" && cutoff > 1) { alphaMode = "MASK"; alphaOut = 0; }
        else
        {
            alphaRead = baseTex >= 0;
            double factor = F(bc[3]);   // the Alpha Factor node's float32, or none at 1
            bool factorNode = bc[3] != 1;
            if (mode == "MASK")
            {
                alphaMode = "MASK"; alphaCutoff = F(cutoff);
                alphaOut = factorNode ? factor : 1;
            }
            else
            {
                // gather_alpha_info: a factor read back as 1 (in float32) is OPAQUE; 0 is MASK at 0.5; the texture or the
                // vertex colour behind it makes neither, so the mode stays BLEND without a factor
                if (factorNode && factor == 1) { alphaMode = "OPAQUE"; alphaOut = 1; }
                else if (factorNode && factor == 0) { alphaMode = "MASK"; alphaOut = 0; }
                else { alphaMode = "BLEND"; alphaOut = factorNode ? factor : 1; }
            }
        }
        if (unlit) alphaRead = false;   // an unlit material's alpha feeds a mix factor, not the Alpha socket
        var j = new JObject { ["name"] = name };
        if (alphaMode != "OPAQUE") j["alphaMode"] = alphaMode;
        if (alphaCutoff.HasValue && alphaCutoff.Value != 0.5) j["alphaCutoff"] = alphaCutoff.Value;
        if (mat.DoubleSided) j["doubleSided"] = true;
        // ---- the members in the serializer's order: emissive, extensions, normal, occlusion, pbr
        var pbr = new JObject();
        if (!unlit)
        {
            // emission (imp emission(), exp extensions/emission.py): a grey factor is folded into the strength on the JSON
            // doubles; the sockets hold float32; the exporter multiplies colour by strength in double, drops [0, 0, 0], and
            // a component over 1 moves the maximum into KHR_materials_emissive_strength with the colour divided by it
            double[] efd = mat.EmissiveFactorJson != null && mat.EmissiveFactorJson.Length == 3 ? (double[])mat.EmissiveFactorJson.Clone() : new double[3];
            double strength = ext["KHR_materials_emissive_strength"] is JObject es ? ((double?)es["emissiveStrength"] ?? 1.0) : 1.0;
            if (efd[0] == efd[1] && efd[1] == efd[2]) { strength *= efd[0]; efd = new[] { 1.0, 1.0, 1.0 }; }
            // the sockets: a colour component below 0 is clamped by the socket on the way in, and so is the strength
            float[] ec = { Socket(efd[0]), Socket(efd[1]), Socket(efd[2]) }; float es32 = Socket(strength);
            // with a texture the colour comes from the Mix node's constant, or [1, 1, 1] without one (the factor was white)
            double[] emissive = { ec[0] * (double)es32, ec[1] * (double)es32, ec[2] * (double)es32 };
            if (mat.EmissiveTexture >= 0 && efd[0] == 1 && efd[1] == 1 && efd[2] == 1) emissive = new[] { 1.0 * es32, 1.0 * es32, 1.0 * es32 };
            bool emits = !(emissive[0] == 0 && emissive[1] == 0 && emissive[2] == 0);
            double emissiveStrength = 1;
            if (emits && emissive.Any(v => v > 1.0)) { emissiveStrength = emissive.Max(); emissive = emissive.Select(v => v / emissiveStrength).ToArray(); }
            if (emits) j["emissiveFactor"] = new JArray(emissive);
            if (mat.EmissiveTexture >= 0) j["emissiveTexture"] = TextureInfo(c.Texture(mat.EmissiveTexture, false), mat.EmissiveTexCoord, uvSets);
            var outExt = new JObject();
            if (ext["KHR_materials_clearcoat"] is JObject cc)
            {
                // exp/extensions/clearcoat.py: the weight written when not 0 (always with a texture: the multiply node's
                // constant, 0 included); the roughness when it is not Blender's 0.03 within 1e-5 (the importer's value without
                // the extension; 0 with a texture and no factor); the textures are not measured on any file: left by name
                if (cc["clearcoatTexture"] != null || cc["clearcoatRoughnessTexture"] != null || cc["clearcoatNormalTexture"] != null) throw new LeftException("a clearcoat texture (not measured)");
                var o = new JObject();
                float cf = (float)((double?)cc["clearcoatFactor"] ?? 0.0), cr = (float)((double?)cc["clearcoatRoughnessFactor"] ?? 0.03);
                if (cf != 0f) o["clearcoatFactor"] = (double)cf;
                if (Math.Abs((double)cr - 0.03) > 1e-5) o["clearcoatRoughnessFactor"] = (double)cr;
                if (o.Count > 0) outExt["KHR_materials_clearcoat"] = o;
            }
            if (ext["KHR_materials_transmission"] != null) throw new LeftException("KHR_materials_transmission (not measured yet)");
            if (emissiveStrength != 1) outExt["KHR_materials_emissive_strength"] = new JObject { ["emissiveStrength"] = emissiveStrength };
            if (ext["KHR_materials_volume"] != null) throw new LeftException("KHR_materials_volume (not measured yet)");
            // KHR_materials_specular (imp specular(), exp extensions/specular.py): the Specular IOR Level socket holds
            // float32(0.5 * factor) and the exporter doubles it back - below 1 written as the factor, at 1 nothing, over 1
            // folded into the colour; a specular texture puts the constant in a multiply node unless the factor is 2 (then
            // nothing); the colour omitted at white; beside a specular colour texture written whenever the source's is not
            // white (the exporter compares its list to a tuple), or as (fac, fac, fac) over 1; from a specular-glossiness
            // material the specular factor is the colour
            JObject spec = null;
            if (sg != null)
            {
                var sf = sg["specularFactor"] is JArray a3 && a3.Count == 3 ? a3.Select(x => Socket((double)x)).ToArray() : new[] { 1f, 1f, 1f };
                spec = new JObject();
                if (!Unit(sf)) spec["specularColorFactor"] = new JArray(sf.Select(x => (double)x));
            }
            else if (ext["KHR_materials_specular"] is JObject sp)
            {
                spec = new JObject();
                double s = (double?)sp["specularFactor"] ?? 1.0;
                float half = (float)(0.5 * s);
                double? fac = sp["specularTexture"] != null && s == 2.0 ? (double?)null : (double)half * 2.0;
                if (fac.HasValue && fac.Value < 1.0) spec["specularFactor"] = fac.Value;
                if (sp["specularTexture"] is JObject st) spec["specularTexture"] = TextureInfo(c.Texture((int)st["index"], false), (int?)st["texCoord"] ?? 0, uvSets);
                var scfJson = sp["specularColorFactor"] is JArray a3 && a3.Count == 3 ? a3.Select(x => (double)x).ToArray() : new[] { 1.0, 1.0, 1.0 };
                bool whiteJson = scfJson[0] == 1.0 && scfJson[1] == 1.0 && scfJson[2] == 1.0;
                var scf = scfJson.Select(x => (double)Socket(x)).ToArray();
                if (fac.HasValue && fac.Value > 1.0) scf = scf.Select(v => v * fac.Value).ToArray();
                if (sp["specularColorTexture"] is JObject sct)
                {
                    spec["specularColorTexture"] = TextureInfo(c.Texture((int)sct["index"], false), (int?)sct["texCoord"] ?? 0, uvSets);
                    // the Mix node exists only when the JSON colour is not white: its constant (a list) never equals the tuple
                    // (1, 1, 1), so it is written whatever its float32; without the node, (fac, fac, fac) when the factor is over 1
                    if (!whiteJson) spec["specularColorFactor"] = new JArray(scf);
                    else if (fac.HasValue && fac.Value > 1.0) spec["specularColorFactor"] = new JArray(fac.Value, fac.Value, fac.Value);
                }
                else if (!(scf[0] == 1.0 && scf[1] == 1.0 && scf[2] == 1.0)) spec["specularColorFactor"] = new JArray(scf);
            }
            if (spec != null && spec.Count > 0) outExt["KHR_materials_specular"] = spec;
            if (ext["KHR_materials_sheen"] != null) throw new LeftException("KHR_materials_sheen (not measured yet)");
            if (ext["KHR_materials_anisotropy"] != null) throw new LeftException("KHR_materials_anisotropy (not measured yet)");
            // KHR_materials_ior: only beside specular, transmission or volume; a specular-glossiness material gets 1000
            // ... as the Python int 1000 the exporter writes there (an Extension's values keep their type: BlenderGlbWriter)
            if (sg != null) { if (outExt["KHR_materials_specular"] != null) outExt["KHR_materials_ior"] = new JObject { ["ior"] = 1000L }; }
            else if (ext["KHR_materials_ior"] is JObject io && outExt["KHR_materials_specular"] != null)
            {
                float ior = (float)((double?)io["ior"] ?? 1.5);
                if (ior != 1.5f) outExt["KHR_materials_ior"] = new JObject { ["ior"] = (double)ior };
            }
            if (outExt.Count > 0) j["extensions"] = outExt;
            if (mat.NormalTexture >= 0)
            {
                var n = TextureInfo(c.Texture(mat.NormalTexture, false), mat.NormalTexCoord, uvSets);
                float s = (float)mat.NormalScale; if (s != 1f) n["scale"] = (double)s;
                j["normalTexture"] = n;
            }
            // the occlusion and the metallic-roughness slot: one texture when they read one image of one size (ORM), else
            // the occlusion's own; an occlusion from another image of the same size as the metallic-roughness image would
            // be composed into a new one (not modelled)
            int mrTex = sg != null ? -1 : mat.MetallicRoughnessTexture, mrCoord = mat.MetallicRoughnessTexCoord;
            int occTexIndex = -1;
            if (mat.OcclusionTexture >= 0)
            {
                bool orm = mrTex >= 0 && c.M.Textures[mat.OcclusionTexture].Source == c.M.Textures[mrTex].Source;
                if (!orm && mrTex >= 0 && SameSize(c.M, mat.OcclusionTexture, mrTex)) throw new LeftException("an occlusion image beside a metallic-roughness image of the same size (the exporter composes a new image)");
                occTexIndex = c.Texture(mat.OcclusionTexture, false);
                var o = TextureInfo(occTexIndex, mat.OcclusionTexCoord, uvSets);
                // the importer makes the strength's Mix node when the JSON double is not 1; the exporter writes its float32 factor whatever it is
                if (mat.OcclusionStrengthJson != 1.0) o["strength"] = (double)(float)mat.OcclusionStrengthJson;
                j["occlusionTexture"] = o;
                if (orm) mrTex = -2;   // shares the texture just made
            }
            // pbrMetallicRoughness
            var wbc = WrittenBaseColour(bc, alphaOut, unlit: false);
            if (wbc != null) pbr["baseColorFactor"] = new JArray(wbc);
            if (baseTex >= 0) pbr["baseColorTexture"] = TextureInfo(c.Texture(baseTex, alphaRead), baseTexCoord, uvSets);
            float metallic = sg != null ? 0f : (float)mat.MetallicFactor;
            float roughness = sg != null ? (float)(1.0 - ((double?)sg["glossinessFactor"] ?? 1.0)) : (float)mat.RoughnessFactor;
            if (metallic != 1f) pbr["metallicFactor"] = (double)metallic;
            if (mrTex == -2) pbr["metallicRoughnessTexture"] = TextureInfo(occTexIndex, mrCoord, uvSets);
            else if (mrTex >= 0) pbr["metallicRoughnessTexture"] = TextureInfo(c.Texture(mrTex, false), mrCoord, uvSets);
            if (roughness != 1f) pbr["roughnessFactor"] = (double)roughness;
        }
        else
        {
            // unlit (materials.py __export_unlit): the base colour and its texture, metallic 0, roughness 0.9, the extension empty
            j["extensions"] = new JObject { ["KHR_materials_unlit"] = new JObject() };
            var wbc = WrittenBaseColour(bc, alphaOut, unlit: true);
            if (wbc != null) pbr["baseColorFactor"] = new JArray(wbc);
            if (baseTex >= 0) pbr["baseColorTexture"] = TextureInfo(c.Texture(baseTex, false), baseTexCoord, uvSets);
            pbr["metallicFactor"] = 0.0; pbr["roughnessFactor"] = 0.9;   // the exporter's own Python doubles (__export_unlit), not a socket's float32
        }
        if (pbr.Count > 0) j["pbrMetallicRoughness"] = pbr;   // __fix_json drops an empty object
        return j;
    }

    /// <summary>pbr_metallic_roughness.py __gather_base_color_factor: the RGB clamped to 0..1 (unlit keeps what is over 1:
    /// unlit.py), the alpha as the wiring left it, the whole omitted at [1, 1, 1, 1].</summary>
    static double[] WrittenBaseColour(double[] bc, double alpha, bool unlit)
    {
        double C(double v) { float x = (float)v; return x < 0f ? 0f : x > 1f && !unlit ? 1f : x; }
        var rgba = new[] { C(bc[0]), C(bc[1]), C(bc[2]), alpha };
        return rgba[0] == 1 && rgba[1] == 1 && rgba[2] == 1 && rgba[3] == 1 ? null : rgba;
    }

    /// <summary>check_same_size_images: the two textures' images decode to the same width and height (PNG IHDR, JPEG SOF).</summary>
    static bool SameSize(HafModel m, int textureA, int textureB)
    {
        var a = ImageSize(m, m.Textures[textureA].Source); var b = ImageSize(m, m.Textures[textureB].Source);
        return a.HasValue && b.HasValue && a.Value == b.Value;
    }

    // The glTF JPEG subset: 8-bit grayscale/YCbCr with SOF0, SOF1 or SOF2 and declared dimensions.
    // In particular, do not replace Blender's CMYK/high-precision conversion with a nonportable JPEG.
    static bool PortableJpegHeader(byte[] d)
    {
        if (d == null || d.Length < 4 || d[0] != 0xff || d[1] != 0xd8) return false;
        int p = 2;
        while (p + 4 <= d.Length)
        {
            if (d[p++] != 0xff) return false;
            while (p < d.Length && d[p] == 0xff) p++;
            if (p >= d.Length) return false;
            int marker = d[p++];
            if (marker == 0xd8 || marker == 0x01 || (marker >= 0xd0 && marker <= 0xd7)) continue;
            if (marker == 0xda || marker == 0xd9 || p + 2 > d.Length) return false;
            int length = (d[p] << 8) | d[p + 1];
            if (length < 2 || length > d.Length - p) return false;
            if (marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc)
            {
                if (length < 8) return false;
                int components = d[p + 7];
                return marker <= 0xc2 && d[p + 2] == 8 && (components == 1 || components == 3) && length == 8 + 3 * components &&
                    ((d[p + 3] << 8) | d[p + 4]) > 0 && ((d[p + 5] << 8) | d[p + 6]) > 0;
            }
            p += length;
        }
        return false;
    }

    internal static (int w, int h)? ImageSize(HafModel m, int image)
    {
        if (image < 0 || image >= m.Images.Count) return null;
        var d = m.Images[image].Bytes; if (d == null || d.Length < 24) return null;
        if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
            return ((d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19], (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23]);
        if (d[0] == 0xFF && d[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                byte marker = d[i + 1];
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                int len = (d[i + 2] << 8) | d[i + 3];
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
                i += 2 + len;
            }
        }
        return null;
    }
}
