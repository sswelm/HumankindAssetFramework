using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

// FbxMaterials.cs - the materials, textures and images of an FBX file as Blender 5.1's FBX importer builds them and as its
// glTF exporter writes them back (sub-step 6b of the native FBX reader, 2026-11-02). Read at Blender 5.1.2:
// io_scene_fbx/import_fbx.py (blen_read_material, blen_read_texture_image, the "Assign textures" pass),
// bpy_extras/node_shader_utils.py (PrincipledBSDFWrapper, ShaderImageTextureWrapper), bpy_extras/image_utils.py
// (load_image), the image kernel (rna_image.cc: depth, channels, pixels; imbuf's OpenImageIO reader) and the exporter's
// material modules (blender/exp/material/*.py, exp/sampler.py, extensions/specular.py, extensions/emission.py).
//
// THE IMPORTER, as modelled:
//   * a Material (every one, in table order) through PrincipledBSDFWrapper: base colour = DiffuseColor clamped to 0..1
//     (white when absent); Specular IOR Level = SpecularFactor * 2 clamped (0.25 -> 0.5); roughness = 1 - sqrt(max(Shininess,
//     0)) / 10 clamped (20 -> 0.5528); alpha = 1 - TransparencyFactor, and when that is 1 or 0 the material's OWN Opacity,
//     else 1 - TransparentColor.r; metallic = ReflectionFactor; the Normal Map node's strength = BumpFactor clamped to 0..10
//     (the node is made for every material); emission strength = EmissiveFactor (1 when absent), emission colour =
//     EmissiveColor (black when absent), both clamped to 0..1e6. Every value lands in a float32 socket. A property is read
//     from the Material's Properties70 then the KFbxSurfacePhong template's (a 'U' in its flags skips it; the first match by
//     name decides, and Blender ASSERTS its type: a Color/ColorRGB of three doubles, a double/Number double, an enum whose
//     subtype and flags are empty with an int32 value - a mismatch stops Blender's import, so this reader declines the file);
//   * images: every Video (table order), then every Texture, each through blen_read_texture_image: the path is
//     RelativeFilename (joined to the file's directory after leading separators are stripped), else FileName, else Filename,
//     else the element's name; separators made native; a path seen before shares the image (image_cache); load_image looks
//     for the file as given, under the FBX's directory, by its base name there, then RECURSIVELY under that directory by
//     case-folded base name, and makes a PLACEHOLDER (no pixels) when nothing is found; a Content block packs its bytes into
//     the image (the pixels Blender holds come from the disk file when one loaded, the exported bytes from the packed data);
//     the image is named after the element's name's base name through Blender's image pool;
//   * texture links (the "Assign textures" pass): for every Material, the Texture objects connected INTO it in connection
//     order, OP links only, by the destination property: DiffuseColor and 3dsMax|maps|texmap_diffuse -> base colour;
//     SpecularColor/SpecularFactor -> specular; ReflectionColor/ReflectionFactor/3dsMax|maps|texmap_reflection -> metallic;
//     TransparentColor/TransparencyFactor -> alpha (the image's Alpha output, or its Color output when the image has no
//     alpha: fewer than 4 channels or a depth of 24 or 8); ShininessExponent -> roughness; NormalMap/Bump/
//     3dsMax|maps|texmap_bump -> the Normal Map's colour; EmissiveColor -> emission colour; EmissiveFactor -> emission
//     strength; others ignored; a later link to the same slot replaces the image. texture_mapping_set reads the Texture's
//     Translation, Rotation (negated), Scaling (inverted, 1 for 0) and WrapModeU/V (the MATERIAL template behind them, as
//     Blender has it) and makes a Mapping node unless everything is default; a wrap mode sets the image node to EXTEND;
//   * then, per material, a DiffuseColor image of depth 32 (an alpha channel, 8 bits each) is copied into the alpha slot
//     (use_alpha; the base slot's mapping copied, the alpha slot's own removed when the base has none; the clamp untouched).
// THE EXPORTER, as modelled (BlenderMaterialExport.cs holds the same rules for the glTF round trip; here the tree is the
// FBX wrapper's): the base colour factor = the socket (clamped) with the alpha, omitted at [1, 1, 1, 1]; a linked Base
// Color reads as white; alphaMode from the Alpha socket - a constant 1 OPAQUE, 0 MASK (cutoff 0.5, omitted), else BLEND,
// a linked socket BLEND; metallic and roughness omitted when linked or 1; doubleSided always (no backface culling);
// emissive = colour * strength (a linked colour reads [1, 1, 1], a linked strength is not applied), dropped at black, a
// component over 1 moves the maximum into KHR_materials_emissive_strength; KHR_materials_specular: the socket * 2 below 1 as
// specularFactor, over 1 as specularColorFactor (f, f, f), a linked socket writes its texture alone; KHR_materials_ior is
// not written (default IOR); the normal texture with its scale when the strength is not 1; the textures: base colour (the
// base and alpha slots together: the base image whole, the alpha slot's channel on top - from its Alpha output, or the
// luminance of its Color output), metallic-roughness (metallic into B, roughness into G, the other filled white), emissive,
// specular (the image itself: a single alpha-to-alpha fill is the exporter's happy path); an image read whole passes its
// bytes through when their format is the MIME type (JPEG by the file's extension, PNG otherwise; PNG forced when the Alpha
// socket is among the readers), else Blender encodes it again (named fallback); a COMPOSED image is built from the pixels
// Blender holds (PngCodec), unfilled channels white, alpha only when an Alpha fill exists, a 1x1 white image when every
// source image lacks pixels, and written as a new PNG (the drill compares its pixels, not its bytes); image names: the
// file's stem for one source file with a .png/.jpg/.jpeg extension, else the Blender names' stems joined by '-', else
// "Image"; one written image per (MIME type, name, bytes); a sampler per value (Linear, mipmaps; CLAMP both ways under
// EXTEND); a texture per slot. A missing image file drops its texture (and feeds the compositions as if unlinked).
// LEFT TO BLENDER BY NAME (Scene.Fallback / Reasons): a Mapping node whose transform the exporter would write
// (KHR_texture_transform: the model carries none), an image Blender reads that this code cannot (a format other than PNG or
// JPEG; JPEG, 16-bit or interlaced PNG pixels inside a composition), an image the exporter encodes again, a composition over
// images of different sizes, a WebP image.
public static class FbxMaterials
{
    /// <summary>A Blender image datablock as the FBX importer leaves it.</summary>
    public sealed class Image
    {
        public string Name = "";            // the datablock name (bpy.data.images)
        public string FilePath = "";        // image.filepath: the file found, or the path given (a placeholder)
        public byte[] FileBytes;            // the file's bytes when one loaded
        public byte[] Packed;               // image.packed_file.data (a Content block), or null
        public int Width, Height;           // image.size (0 x 0 without pixels)
        public int Depth;                   // image.depth: the planes (8 grey, 24 RGB, 32 with alpha), x4 for a float buffer, 0 without pixels
        public int Channels;                // image.channels: 4 with pixels, 0 without
        public bool HasData => Width > 0 && Height > 0;
        public string Undecodable;          // non-null: Blender holds pixels this code cannot produce (a composition over it is a fallback)
        public string ColorSpace = "sRGB";  // image.colorspace_settings.name; is_data reads as (name == "Non-Color") (rna_color.cc)
        public int Users;                   // id.us: the image nodes holding it (a loaded, new or copied image starts at 0)
        public Image Original;              // the datablock this one was copied from (image.copy() in the wrapper's colourspace rule), or null
        public byte[] ExportBytes => Packed ?? FileBytes;   // __encode_from_image: the packed data, else the file's
        byte[] pixels;
        /// <summary>Four bytes per pixel, rows top-down, as Blender holds them (null with `why` when not decodable).</summary>
        public byte[] Pixels(out string why)
        {
            why = Undecodable;
            if (why != null || !HasData) return null;
            if (Original != null) return Original.Pixels(out why);
            if (pixels != null) return pixels;
            var src = Packed ?? FileBytes;   // the buffer Blender holds: the packed data once packed, else the disk file's
            pixels = PngCodec.Decode(src, out _, out why);
            if (pixels == null) Undecodable = why;
            return pixels;
        }
    }

    /// <summary>A ShaderImageTextureWrapper's state: the image node, its link into the socket, its Mapping node, its extension.</summary>
    public sealed class Slot
    {
        public Image Image;                 // null: no image node
        public bool AlphaOutput;            // the link into the socket comes from the image node's Alpha output (else Color)
        public bool HasMapping;             // a Mapping node (vector type TEXTURE) feeds the image node
        public float[] Loc = { 0, 0, 0 }, Rot = { 0, 0, 0 }, Scale = { 1, 1, 1 };   // the Mapping node's sockets (float32)
        public bool Clamp;                  // the image node's extension is EXTEND
    }

    /// <summary>A material's PrincipledBSDFWrapper state.</summary>
    public sealed class Material
    {
        public FbxObject Source;
        public float[] BaseColor = { 1, 1, 1 };
        public float Specular = 0.5f, Roughness = 0.5f, Alpha = 1f, Metallic = 0f, NormalStrength = 1f, EmissionStrength = 1f;
        public float[] EmissionColor = { 0, 0, 0 };
        public readonly Slot BaseColorTex = new Slot(), SpecularTex = new Slot(), MetallicTex = new Slot(), AlphaTex = new Slot(),
            RoughnessTex = new Slot(), NormalTex = new Slot(), EmissionColorTex = new Slot(), EmissionStrengthTex = new Slot();
        public Image DiffuseImage;          // material_images[material][b'DiffuseColor']
    }

    public sealed class Scene
    {
        public readonly List<Image> Images = new List<Image>();                  // creation order (copies included)
        public readonly Dictionary<FbxObject, Material> Materials = new Dictionary<FbxObject, Material>();
        public readonly Dictionary<FbxObject, Image> ImageOfTexture = new Dictionary<FbxObject, Image>();
        internal BlenderNames.NamePool Pool;

        /// <summary>image.copy(): a new datablock named after the original through the pool, with its file, bytes and pixels, no users.</summary>
        internal Image Copy(Image im)
        {
            var c = new Image { FilePath = im.FilePath, FileBytes = im.FileBytes, Packed = im.Packed, Width = im.Width, Height = im.Height, Depth = im.Depth, Channels = im.Channels,
                Undecodable = im.Undecodable, ColorSpace = im.ColorSpace, Original = im.Original ?? im };
            c.Name = Pool.Unique(im.Name);
            Images.Add(c);
            return c;
        }
    }

    sealed class DeclineException : Exception { public DeclineException(string why) : base(why) { } }

    // ---- the importer ---------------------------------------------------------------------------------------------------

    /// <summary>Blender's materials and images of the file; `decline` names what stops Blender's import (an assertion).</summary>
    public static Scene Read(FbxImport.Scene r, FbxScene s, IList<FbxObject> table, string baseDir, Action<string> decline, List<string> notes)
    {
        var o = new Scene();
        try
        {
            var doc = s.Document;
            var phongTmpl = Template(doc, "Material", "KFbxSurfacePhong");
            var texTmpl = Template(doc, "Texture", "KFbxFileTexture");
            var videoTmpl = Template(doc, "Video", "KFbxVideo");
            foreach (var m in r.Materials) o.Materials[m] = ReadMaterial(m, phongTmpl);
            var imageCache = new Dictionary<string, Image>(StringComparer.Ordinal);
            var pool = o.Pool = new BlenderNames.NamePool();
            foreach (var t in table) if (t.Class == "Video") o.ImageOfTexture[t] = ReadTextureImage(t, videoTmpl, baseDir, imageCache, pool, o, notes);
            foreach (var t in table) if (t.Class == "Texture") o.ImageOfTexture[t] = ReadTextureImage(t, texTmpl, baseDir, imageCache, pool, o, notes);
            AssignTextures(r, s, table, o, phongTmpl, notes);
        }
        catch (DeclineException e) { decline(e.Message); }
        return o;
    }

    /// <summary>fbx_template_get: the Definitions' PropertyTemplate of (ObjectType, name), or of the name without its K.</summary>
    static FbxNode Template(FbxDocument doc, string type, string name)
    {
        FbxNode found = null;
        var defs = doc.First("Definitions");
        if (defs == null) return null;
        foreach (string key in new[] { name, name.Substring(1) })
        {
            foreach (var ot in defs.All("ObjectType"))
            {
                if (ot.Properties.Count < 1 || !(ot.Properties[0] is string t) || t != type) continue;
                foreach (var pt in ot.All("PropertyTemplate")) if (pt.Properties.Count > 0 && pt.Properties[0] is string n && n == key) found = pt;   // a dict: the last one of the key
            }
            if (found != null) return found;
        }
        return null;
    }

    static FbxNode Props70(FbxNode rec) => rec?.First("Properties70");

    /// <summary>blen_read_custom_properties runs over an object's OWN Properties70 (materials, images; use_custom_props is on by
    /// default): every child must be a P with at least four fields, its flags a string - a short P anywhere in the block
    /// stops Blender's import (measured 2026-11-02; a template's short P of another name is harmless).</summary>
    static void CheckOwnBlock(FbxNode block, string what)
    {
        if (block == null) return;
        foreach (var p in block.Children)
        {
            if (p.Name != "P") throw new DeclineException(what + ": a Properties70 child that is not a P (Blender's importer asserts)");
            if (p.Properties.Count < 4 || !(p.Properties[3] is string)) throw new DeclineException(what + ": a P with fewer than four fields or non-string flags (Blender's custom-property pass fails on it)");
        }
    }

    /// <summary>elem_props_find_first over (the object's block, the template's): the first P of that name without 'U' in its flags.</summary>
    static FbxNode FindP(FbxNode[] blocks, string name)
    {
        foreach (var block in blocks)
        {
            if (block == null) continue;
            foreach (var p in block.Children)
            {
                if (p.Name != "P") throw new DeclineException("a Properties70 child that is not a P (Blender's importer asserts)");
                if (p.Properties.Count == 0) throw new DeclineException("a P without fields (Blender's importer fails on it)");
                if (!(p.Properties[0] is string pn) || pn != name) continue;
                if (p.Properties.Count < 4) throw new DeclineException("a P with fewer than four fields (Blender's importer fails on it)");
                string flags = p.Properties[3] as string;
                if (flags == null) throw new DeclineException("a P whose flags are not a string (Blender's importer fails on it)");
                if (flags.IndexOf('U') >= 0) continue;
                return p;
            }
        }
        return null;
    }

    static string S(FbxNode p, int i) => i < p.Properties.Count ? p.Properties[i] as string : null;

    static double[] GetColorRgb(FbxNode[] blocks, string name, double[] def)
    {
        var p = FindP(blocks, name); if (p == null) return def;
        string type = S(p, 1), sub = S(p, 2);
        if (type == "Color") { if (sub != "") throw new DeclineException($"property {name}: a Color with a subtype (Blender's importer asserts)"); }
        else if (!(type == "ColorRGB" && sub == "Color")) throw new DeclineException($"property {name}: type {type}/{sub} where Blender's importer asserts Color or ColorRGB");
        if (p.Properties.Count < 7 || !(p.Properties[4] is double) || !(p.Properties[5] is double) || !(p.Properties[6] is double)) throw new DeclineException($"property {name}: not three doubles (Blender's importer asserts)");
        return new[] { (double)p.Properties[4], (double)p.Properties[5], (double)p.Properties[6] };
    }

    static double[] GetVector3d(FbxNode[] blocks, string name, double[] def)
    {
        var p = FindP(blocks, name); if (p == null) return def;
        if (p.Properties.Count < 7 || !(p.Properties[4] is double) || !(p.Properties[5] is double) || !(p.Properties[6] is double)) throw new DeclineException($"property {name}: not three doubles (Blender's importer asserts)");
        return new[] { (double)p.Properties[4], (double)p.Properties[5], (double)p.Properties[6] };
    }

    static double? GetNumber(FbxNode[] blocks, string name, double? def)
    {
        var p = FindP(blocks, name); if (p == null) return def;
        string type = S(p, 1), sub = S(p, 2);
        if (type == "double") { if (sub != "Number") throw new DeclineException($"property {name}: a double whose subtype is not Number (Blender's importer asserts)"); }
        else if (!(type == "Number" && sub == "")) throw new DeclineException($"property {name}: type {type}/{sub} where Blender's importer asserts double or Number");
        if (p.Properties.Count < 5 || !(p.Properties[4] is double)) throw new DeclineException($"property {name}: not a double (Blender's importer asserts)");
        return (double)p.Properties[4];
    }

    static int GetEnum(FbxNode[] blocks, string name, int def)
    {
        var p = FindP(blocks, name); if (p == null) return def;
        if (S(p, 1) != "enum" || S(p, 2) != "" || S(p, 3) != "") throw new DeclineException($"property {name}: not a plain enum (Blender's importer asserts)");
        if (p.Properties.Count < 5 || !(p.Properties[4] is int)) throw new DeclineException($"property {name}: not an int32 (Blender's importer asserts)");
        return (int)p.Properties[4];
    }

    static float Clamp(double v, double lo, double hi) => (float)(v < lo ? lo : v > hi ? hi : v);

    static Material ReadMaterial(FbxObject m, FbxNode phongTmpl)
    {
        CheckOwnBlock(Props70(m.Record), "material '" + m.Name + "'");
        var blocks = new[] { Props70(m.Record), Props70(phongTmpl) };
        var own = new[] { Props70(m.Record) };
        var mat = new Material { Source = m };
        var bc = GetColorRgb(blocks, "DiffuseColor", new[] { 1.0, 1.0, 1.0 });
        mat.BaseColor = new[] { Clamp(bc[0], 0, 1), Clamp(bc[1], 0, 1), Clamp(bc[2], 0, 1) };
        mat.Specular = Clamp(GetNumber(blocks, "SpecularFactor", 0.25).Value * 2.0, 0, 1);
        double shininess = Math.Max(GetNumber(blocks, "Shininess", 20.0).Value, 0.0);
        mat.Roughness = Clamp(1.0 - (Math.Sqrt(shininess) / 10.0), 0, 1);
        double alpha = 1.0 - GetNumber(blocks, "TransparencyFactor", 0.0).Value;
        if (alpha == 1.0 || alpha == 0.0)
        {
            var opacity = GetNumber(own, "Opacity", null);
            alpha = opacity ?? 1.0 - GetColorRgb(blocks, "TransparentColor", new[] { 0.0, 0.0, 0.0 })[0];
        }
        mat.Alpha = Clamp(alpha, 0, 1);
        mat.Metallic = Clamp(GetNumber(blocks, "ReflectionFactor", 0.0).Value, 0, 1);
        mat.NormalStrength = Clamp(GetNumber(blocks, "BumpFactor", 1.0).Value, 0, 10);
        mat.EmissionStrength = Clamp(GetNumber(blocks, "EmissiveFactor", 1.0).Value, 0, 1000000.0);
        var ec = GetColorRgb(blocks, "EmissiveColor", new[] { 0.0, 0.0, 0.0 });
        mat.EmissionColor = new[] { Clamp(ec[0], 0, 1000000.0), Clamp(ec[1], 0, 1000000.0), Clamp(ec[2], 0, 1000000.0) };
        return mat;
    }

    /// <summary>elem_find_first_string: the first child of that name with a property; it must be ONE string.</summary>
    static string FirstString(FbxNode rec, string name)
    {
        var n = rec?.First(name);
        if (n == null || n.Properties.Count == 0) return null;
        if (n.Properties.Count != 1 || !(n.Properties[0] is string s)) throw new DeclineException($"{name}: not a single string (Blender's importer asserts)");
        return s;
    }

    static byte[] FirstBytes(FbxNode rec, string name)
    {
        var n = rec?.First(name);
        if (n == null || n.Properties.Count == 0) return null;
        if (n.Properties.Count != 1 || !(n.Properties[0] is FbxRaw raw)) throw new DeclineException($"{name}: not a single raw block (Blender's importer asserts)");
        return raw.Bytes;
    }

    static Image ReadTextureImage(FbxObject t, FbxNode tmpl, string baseDir, Dictionary<string, Image> cache, BlenderNames.NamePool pool, Scene o, List<string> notes)
    {
        string elemName = FbxImport.ValidateBlendName(t.Name);
        CheckOwnBlock(Props70(t.Record), t.Class + " '" + t.Name + "'");
        string filepath = FirstString(t.Record, "RelativeFilename");
        if (!string.IsNullOrEmpty(filepath))
        {
            filepath = filepath.TrimStart('\\').TrimStart('/');
            filepath = Join(baseDir ?? "", filepath);
        }
        else filepath = FirstString(t.Record, "FileName");
        if (string.IsNullOrEmpty(filepath)) filepath = FirstString(t.Record, "Filename");
        if (string.IsNullOrEmpty(filepath)) { filepath = elemName; notes.Add("texture '" + elemName + "': no file path, its name used"); }
        else filepath = filepath.Replace('/', '\\');
        byte[] content = FirstBytes(t.Record, "Content");
        if (cache.TryGetValue(filepath, out var cached))
        {
            // `if not image.has_data: pack` - has_data is False throughout this step (no ImBuf is cached until a later
            // property read; measured 2026-11-02), so a later element's Content always packs over the shared image
            if (content != null && content.Length > 0) Pack(cached, content);
            return cached;
        }
        var image = LoadImage(filepath, baseDir ?? "");
        if (content != null && content.Length > 0) Pack(image, content);
        cache[filepath] = image;
        image.Name = pool.Unique(DefaultName(Basename(elemName), "Image"));
        o.Images.Add(image);
        return image;
    }

    static string DefaultName(string name, string def) => name.Length == 0 ? def : name;

    /// <summary>ntpath.basename: the part after the last separator of either kind.</summary>
    internal static string Basename(string path)
    {
        int i = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return i < 0 ? path : path.Substring(i + 1);
    }

    /// <summary>ntpath.join of a directory and a path: the path alone when it is rooted (a drive or a leading separator).</summary>
    static string Join(string dir, string path)
    {
        if (path.Length >= 2 && path[1] == ':' || path.StartsWith("\\") || path.StartsWith("/")) return path;
        if (dir.Length == 0) return path;
        char last = dir[dir.Length - 1];
        return last == '\\' || last == '/' || last == ':' ? dir + path : dir + "\\" + path;
    }

    static string NativePathSep(string path) => path.StartsWith("//") ? "//" + path.Substring(2).Replace('/', '\\') : path.Replace('/', '\\');

    static bool Exists(string path) { try { return path.Length > 0 && (File.Exists(path) || Directory.Exists(path)); } catch { return false; } }

    static string Abs(string path) { try { return Path.GetFullPath(path); } catch { return path; } }

    /// <summary>image_utils.load_image(path, dirname=basedir, place_holder=True, recursive=True).</summary>
    static Image LoadImage(string filepath, string baseDir)
    {
        string imagepath = NativePathSep(filepath);
        if (Exists(imagepath)) return LoadFile(Abs(imagepath));
        foreach (string variant in new[] { imagepath, baseDir.Length > 0 ? Join(baseDir, imagepath) : null, baseDir.Length > 0 ? Join(baseDir, Basename(imagepath)) : null })
            if (variant != null && Exists(variant)) return LoadFile(Abs(variant));
        // recursive: the directories of the path and of the file, reduced (bpy.path.reduce_dirs), walked top-down for the base name, case folded
        var searchPaths = new List<string>();
        foreach (string d in new[] { Dirname(imagepath), baseDir }) if (d.Length > 0 && Directory.Exists(d)) searchPaths.Add(d);
        searchPaths = ReduceDirs(searchPaths);
        string wanted = Basename(imagepath).ToLowerInvariant();
        foreach (string root in searchPaths)
        {
            string found = Walk(root, wanted);
            if (found != null) return LoadFile(Abs(found));
        }
        return Placeholder(imagepath);
    }

    static string Dirname(string path)
    {
        int i = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return i < 0 ? "" : path.Substring(0, i);
    }

    /// <summary>bpy.path.reduce_dirs: absolute, normalised, unique, shortest first, a directory inside another dropped.</summary>
    static List<string> ReduceDirs(List<string> dirs)
    {
        var list = dirs.Select(Abs).Select(d => { string t = d.TrimEnd('\\', '/'); return t.EndsWith(":") ? t + "\\" : t; }).Distinct(StringComparer.Ordinal).ToList();
        list = list.OrderBy(d => d.Length).ToList();   // Python sorts a set by length: equal lengths in the set's order (not modelled beyond this)
        for (int i = list.Count - 1; i >= 0; i--)
            for (int j = 0; j < i; j++)
            {
                if (list[i].Length == list[j].Length) break;
                if (list[i].StartsWith(list[j] + "\\", StringComparison.OrdinalIgnoreCase)) { list.RemoveAt(i); break; }
            }
        return list;
    }

    /// <summary>os.walk top-down: the files of a directory in listing order, then its subdirectories in listing order.</summary>
    static string Walk(string dir, string wantedLower)
    {
        string[] files, dirs;
        try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); } catch { return null; }
        Comparison<string> listing = (a, b) => string.CompareOrdinal(Basename(a).ToUpperInvariant(), Basename(b).ToUpperInvariant());
        Array.Sort(files, listing); Array.Sort(dirs, listing);
        foreach (string f in files) if (Basename(f).ToLowerInvariant() == wantedLower) return f;
        foreach (string d in dirs) { string r = Walk(d, wantedLower); if (r != null) return r; }
        return null;
    }

    static Image Placeholder(string path) => new Image { FilePath = path };

    /// <summary>bpy.data.images.load: a PNG or JPEG gets its size and planes; another format Blender may read is marked
    /// undecodable (its pixels cannot be produced here); a file Blender cannot read is a placeholder.</summary>
    static Image LoadFile(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); } catch { return Placeholder(path); }
        if (!BlenderKnowsFormat(bytes)) return Placeholder(path);   // bpy.data.images.load raises: load_image makes a placeholder (its filepath the absolute path)
        var im = new Image { FilePath = path, FileBytes = bytes };
        Describe(im, bytes);
        return im;
    }

    /// <summary>IMB_ispic_type: the formats Blender's image reader recognises by their bytes (filetype.cc: PNG, JPEG, BMP, TGA,
    /// IRIS, DPX, Cineon, TIFF, Radiance HDR, OpenEXR, JPEG 2000, DDS, PSD, WebP, AVIF). Anything else fails to load.</summary>
    internal static bool BlenderKnowsFormat(byte[] d)
    {
        if (d == null || d.Length < 18) return false;
        bool At(int i, string ascii) { if (i + ascii.Length > d.Length) return false; for (int k = 0; k < ascii.Length; k++) if (d[i + k] != ascii[k]) return false; return true; }
        if (PngCodec.IsPng(d) || (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)) return true;
        if (At(0, "BM") || At(0, "DDS ") || At(0, "8BPS") || At(0, "#?RADIANCE") || At(0, "#?RGBE")) return true;
        if ((d[0] == 0x49 && d[1] == 0x49 && d[2] == 0x2A && d[3] == 0x00) || (d[0] == 0x4D && d[1] == 0x4D && d[2] == 0x00 && d[3] == 0x2A)) return true;   // TIFF
        if (At(0, "RIFF") && At(8, "WEBP") || At(4, "ftypavif") || At(4, "ftypavis")) return true;
        if (d[0] == 0x76 && d[1] == 0x2F && d[2] == 0x31 && d[3] == 0x01) return true;                        // OpenEXR
        if (At(0, "SDPX") || At(0, "XPDS") || (d[0] == 0x80 && d[1] == 0x2A && d[2] == 0x5F && d[3] == 0xD7) || (d[0] == 0xD7 && d[1] == 0x5F && d[2] == 0x2A && d[3] == 0x80)) return true;   // DPX, Cineon
        if ((d[0] == 0x01 && d[1] == 0xDA) || (d[0] == 0xDA && d[1] == 0x01)) return true;                      // IRIS
        if ((d[0] == 0 && d[1] == 0 && d[2] == 0 && d[3] == 0x0C && d[4] == 0x6A && d[5] == 0x50 && d[6] == 0x20 && d[7] == 0x20) || (d[0] == 0xFF && d[1] == 0x4F && d[2] == 0xFF && d[3] == 0x51)) return true;   // JPEG 2000
        // TGA (format_targa.cc imb_is_a_tga): no magic; the header's colour-map type, image type and pixel depth decide
        int cmapType = d[1], imgType = d[2], pixSize = d[16];
        if ((cmapType == 0 || cmapType == 1) && (imgType == 1 || imgType == 2 || imgType == 3 || imgType == 9 || imgType == 10 || imgType == 11) && (pixSize == 8 || pixSize == 15 || pixSize == 16 || pixSize == 24 || pixSize == 32)) return true;
        return false;
    }

    static void Describe(Image im, byte[] bytes)
    {
        var png = PngCodec.ReadInfo(bytes);
        if (png != null)
        {
            im.Width = png.Width; im.Height = png.Height; im.Channels = 4;
            int planes = png.HasAlpha ? 32 : 8 * png.Channels;
            im.Depth = png.BitDepth == 16 ? planes * 4 : planes;   // a 16-bit PNG is a float buffer: depth = planes * 4
            if (png.BitDepth == 16) im.Undecodable = "a 16-bit PNG (Blender keeps it as a float buffer)";
            else if (png.Interlaced) im.Undecodable = "an interlaced PNG";
            return;
        }
        if (JpegInfo(bytes, out int w, out int h, out int comps))
        {
            im.Width = w; im.Height = h; im.Channels = 4; im.Depth = comps == 4 ? 24 : 8 * comps;   // a CMYK JPEG reaches Blender as RGB through OpenImageIO (not measured: no sample)
            im.Undecodable = "a JPEG (its pixels are not decoded here)";
            return;
        }
        if (bytes.Length > 12 && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') { im.Width = im.Height = 1; im.Channels = 4; im.Depth = 32; im.Undecodable = "a WebP image"; return; }
        // another format: Blender may read it (TGA, BMP, TIFF, DDS, PSD, EXR...) or not; either way its texture is not modelled here
        im.Width = im.Height = 1; im.Channels = 4; im.Depth = 24; im.Undecodable = "an image format other than PNG or JPEG";
    }

    /// <summary>image.pack(data=...): the packed file replaces the loaded buffers - the pixels, size and depth Blender holds
    /// come from the packed data from then on (measured: an RGBA Content over an RGB file on disk reads as depth 32).</summary>
    static void Pack(Image im, byte[] content)
    {
        im.Packed = content;
        im.Width = im.Height = im.Depth = im.Channels = 0; im.Undecodable = null;
        if (!BlenderKnowsFormat(content)) return;   // bytes no reader takes: no pixels (measured: depth 0, size 0x0), the data still the exporter's
        Describe(im, content);
    }

    /// <summary>The JPEG's frame header: width, height and component count (1 grey, 3 colour, 4 CMYK).</summary>
    internal static bool JpegInfo(byte[] d, out int w, out int h, out int comps)
    {
        w = h = comps = 0;
        if (d == null || d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return false;
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte marker = d[i + 1];
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0xFF) { i += marker == 0xFF ? 1 : 2; continue; }
            int len = (d[i + 2] << 8) | d[i + 3];
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                h = (d[i + 5] << 8) | d[i + 6]; w = (d[i + 7] << 8) | d[i + 8]; comps = d[i + 9];
                return w > 0 && h > 0 && comps > 0;
            }
            if (marker == 0xDA || marker == 0xD9) return false;
            i += 2 + len;
        }
        return false;
    }

    static void AssignTextures(FbxImport.Scene r, FbxScene s, IList<FbxObject> table, Scene o, FbxNode phongTmpl, List<string> notes)
    {
        var byId = new Dictionary<long, FbxObject>(); foreach (var t in table) byId[t.Id] = t;
        var reverse = new Dictionary<long, List<FbxConnection>>();
        foreach (var c in s.Connections) { if (!reverse.TryGetValue(c.DstId, out var l)) reverse[c.DstId] = l = new List<FbxConnection>(); l.Add(c); }
        foreach (var m in r.Materials)
        {
            var mat = o.Materials[m];
            if (!reverse.TryGetValue(m.Id, out var into)) continue;
            foreach (var c in into)
            {
                if (c.SrcId == 0 || !byId.TryGetValue(c.SrcId, out var src) || src.Class != "Texture") continue;
                if (c.Kind != "OP") continue;
                if (c.PropertyCount < 4) throw new DeclineException("an OP connection without a property name (Blender's importer fails on it)");
                string link = c.DstProp ?? "";
                var image = o.ImageOfTexture[src];
                Slot slot = null; bool useAlpha = false;
                switch (link)
                {
                    case "DiffuseColor": case "3dsMax|maps|texmap_diffuse": slot = mat.BaseColorTex; break;
                    case "SpecularColor": case "SpecularFactor": slot = mat.SpecularTex; break;
                    case "ReflectionColor": case "ReflectionFactor": case "3dsMax|maps|texmap_reflection": slot = mat.MetallicTex; break;
                    case "TransparentColor": case "TransparencyFactor": slot = mat.AlphaTex; useAlpha = true; break;
                    case "ShininessExponent": slot = mat.RoughnessTex; break;
                    case "NormalMap": case "Bump": case "3dsMax|maps|texmap_bump": slot = mat.NormalTex; break;
                    case "EmissiveColor": slot = mat.EmissionColorTex; break;
                    case "EmissiveFactor": slot = mat.EmissionStrengthTex; break;
                    default: notes.Add("material link '" + link + "' ignored"); break;
                }
                if (slot != null)
                {
                    SetImage(o, slot, image, useAlpha, dataSpace: slot != mat.BaseColorTex && slot != mat.EmissionColorTex);
                    TextureMappingSet(src, phongTmpl, slot);
                }
                if (link == "DiffuseColor") mat.DiffuseImage = image;
            }
        }
        // the diffuse image's alpha channel: copied into the alpha slot
        foreach (var m in r.Materials)
        {
            var mat = o.Materials[m];
            var image = mat.DiffuseImage;
            if (image == null || image.Depth != 32) continue;
            var alpha = mat.AlphaTex; var b = mat.BaseColorTex;
            if (b.Image == null) continue;
            SetImage(o, alpha, b.Image, true, dataSpace: true);
            if (!b.HasMapping) alpha.HasMapping = false;   // the alpha slot's own Mapping node removed (its EXTEND stays)
            else { alpha.HasMapping = true; alpha.Loc = (float[])b.Loc.Clone(); alpha.Rot = (float[])b.Rot.Clone(); alpha.Scale = (float[])b.Scale.Clone(); }
        }
    }

    /// <summary>ShaderImageTextureWrapper.image = image: the node and its link made on first use (the Alpha output for the alpha
    /// slot); a slot that wants the data colourspace (specular, metallic, roughness, alpha, emission strength: 'Non-Color';
    /// the normal map: is_data, the same name) takes a COPY of an image another node already holds (users >= 1) unless the
    /// image is in that space already, and sets the space; the alpha slot relinks by the image's channels and depth; the node's
    /// user count moves from the old image to the new.</summary>
    static void SetImage(Scene o, Slot slot, Image image, bool useAlpha, bool dataSpace)
    {
        if (slot.Image == null) slot.AlphaOutput = useAlpha;
        if (dataSpace)
        {
            if (image.ColorSpace != "Non-Color" && image.Users >= 1) image = o.Copy(image);
            image.ColorSpace = "Non-Color";
        }
        if (useAlpha) slot.AlphaOutput = !(image.Channels < 4 || image.Depth == 24 || image.Depth == 8);
        if (slot.Image != null) slot.Image.Users--;
        image.Users++;
        slot.Image = image;
    }

    /// <summary>texture_mapping_set: the Texture's Translation / Rotation / Scaling / WrapModeU / WrapModeV with the MATERIAL template behind them.</summary>
    static void TextureMappingSet(FbxObject tex, FbxNode phongTmpl, Slot slot)
    {
        var blocks = new[] { Props70(tex.Record), Props70(phongTmpl) };
        var loc = GetVector3d(blocks, "Translation", new[] { 0.0, 0.0, 0.0 });
        var rot = GetVector3d(blocks, "Rotation", new[] { 0.0, 0.0, 0.0 }).Select(v => -v).ToArray();
        var scale = GetVector3d(blocks, "Scaling", new[] { 1.0, 1.0, 1.0 }).Select(v => v != 0.0 ? 1.0 / v : 1.0).ToArray();
        bool clamp = GetEnum(blocks, "WrapModeU", 0) != 0 || GetEnum(blocks, "WrapModeV", 0) != 0;
        if (loc[0] == 0 && loc[1] == 0 && loc[2] == 0 && rot[0] == 0 && rot[1] == 0 && rot[2] == 0 && scale[0] == 1 && scale[1] == 1 && scale[2] == 1 && !clamp) return;
        slot.HasMapping = true;
        slot.Loc = loc.Select(v => (float)v).ToArray(); slot.Rot = rot.Select(v => (float)v).ToArray(); slot.Scale = scale.Select(v => (float)v).ToArray();
        if (clamp) slot.Clamp = true;
    }

    // ---- the exporter ---------------------------------------------------------------------------------------------------

    /// <summary>The written textures, images and samplers of one HafModel, numbered as the exporter's traversal numbers them.</summary>
    public sealed class Writer
    {
        public readonly HafModel Model;
        public readonly HashSet<int> ComposedImages = new HashSet<int>();   // image indices whose bytes Blender encodes anew (compare pixels)
        public string Fallback;
        readonly Dictionary<string, int> imageOf = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, int> samplerOf = new Dictionary<string, int>(StringComparer.Ordinal);
        public Writer(HafModel model) { Model = model; }

        // extensionsUsed: every material registers an Extension object for each of clearcoat, transmission, emissive strength,
        // volume, specular, sheen, anisotropy and ior - empty or not - in that order on the FIRST material traversed, and
        // __manage_extension_declaration later drops the names no material filled (measured with a traced export, 2026-11-02):
        // the written list is that fixed order filtered, whatever the materials' order
        static readonly string[] CanonicalOrder = { "KHR_materials_clearcoat", "KHR_materials_transmission", "KHR_materials_emissive_strength", "KHR_materials_volume", "KHR_materials_specular", "KHR_materials_sheen", "KHR_materials_anisotropy", "KHR_materials_ior" };
        public void Extension(string name)
        {
            if (Model.ExtensionsUsed.Contains(name)) return;
            Model.ExtensionsUsed.Add(name);
            int Rank(string n) { int i = Array.IndexOf(CanonicalOrder, n); return i < 0 ? CanonicalOrder.Length : i; }
            var sorted = Model.ExtensionsUsed.OrderBy(Rank).ToList();   // stable: names outside the list keep their order after it
            Model.ExtensionsUsed.Clear(); Model.ExtensionsUsed.AddRange(sorted);
        }

        public int Sampler(bool clamp)
        {
            var j = new JObject { ["magFilter"] = 9729, ["minFilter"] = 9987 };
            if (clamp) { j["wrapS"] = 33071; j["wrapT"] = 33071; }
            string key = j.ToString(Newtonsoft.Json.Formatting.None);
            if (samplerOf.TryGetValue(key, out int done)) return done;
            Model.Samplers.Add(key); samplerOf[key] = Model.Samplers.Count - 1;
            return Model.Samplers.Count - 1;
        }

        public int ImageIndex(string name, string mime, byte[] bytes, bool composed)
        {
            string key;
            using (var sha = System.Security.Cryptography.SHA256.Create()) key = mime + "\t" + name + "\t" + BitConverter.ToString(sha.ComputeHash(bytes));
            if (imageOf.TryGetValue(key, out int done)) return done;
            Model.Images.Add(new HafImage { Name = name, MimeType = mime, Bytes = bytes });
            int idx = Model.Images.Count - 1; imageOf[key] = idx;
            if (composed) ComposedImages.Add(idx);
            return idx;
        }

        public int Texture(int image, int sampler)
        {
            Model.Textures.Add(new HafTexture { Source = image, Sampler = sampler });
            return Model.Textures.Count - 1;
        }
    }

    sealed class Fill { public Image Img; public int Src; public bool White, Bw; }

    /// <summary>gather_texture_info over the slots feeding the sockets named: the texture index, or -1 when the exporter writes
    /// none; `why` names a fallback.</summary>
    static int GatherTexture(Writer w, List<(Slot slot, string socket)> sockets, out string why)
    {
        why = null;
        var fills = new Dictionary<int, Fill>(); var order = new List<int>();
        void Put(int dst, Fill f) { if (!fills.ContainsKey(dst)) order.Add(dst); fills[dst] = f; }
        foreach (var (slot, socket) in sockets)
        {
            var img = slot.Image;
            int? src = slot.AlphaOutput ? 3 : (int?)null; bool bw = false;
            if (src == null)
            {
                if (socket == "Metallic") src = 2;
                else if (socket == "Roughness") src = 1;
                else if (socket == "Alpha") { bw = true; }   // the Color output (RGBA) into Alpha: its luminance
            }
            if (src == null && !bw && (socket == "Alpha" || socket == "Specular IOR Level")) src = 3;
            int? dst = socket == "Metallic" ? 2 : socket == "Roughness" ? 1 : socket == "Alpha" ? 3 : socket == "Specular IOR Level" ? 3 : (int?)null;
            if (dst != null)
            {
                Put(dst.Value, bw ? new Fill { Img = img, Bw = true } : new Fill { Img = img, Src = src ?? 0 });
                if (socket == "Metallic" && !fills.ContainsKey(1)) Put(1, new Fill { White = true });
                else if (socket == "Roughness" && !fills.ContainsKey(2)) Put(2, new Fill { White = true });
                if (socket == "Alpha") for (int c = 0; c < 3; c++) if (!fills.ContainsKey(c)) Put(c, new Fill { White = true });
            }
            else
            {
                fills.Clear(); order.Clear();
                for (int c = 0; c < img.Channels; c++) Put(c, new Fill { Img = img, Src = c });
            }
        }
        foreach (int k in order.ToList()) if (!fills[k].White && !fills[k].Img.HasData) { fills.Remove(k); order.Remove(k); }
        if (fills.Count == 0) return -1;
        bool happy = fills.Values.All(f => !f.White && !f.Bw) && order.All(k => fills[k].Src == k) && fills.Values.Select(f => f.Img).Distinct().Count() == 1;
        var imgs = new List<Image>(); foreach (int k in order) { var f = fills[k]; if (!f.White && !imgs.Contains(f.Img)) imgs.Add(f.Img); }
        // the MIME type: PNG when the Alpha socket reads it; else AUTO: a JPEG by the file's extension on the happy path, else PNG
        string mime;
        if (sockets.Any(x => x.socket == "Alpha")) { mime = "image/png"; if (happy && IsWebp(imgs[0])) { why = "a WebP image (the exporter writes WebP)"; return -1; } }
        else if (happy && IsJpegByBlender(imgs[0])) mime = "image/jpeg";
        else if (happy && IsWebp(imgs[0])) { why = "a WebP image (the exporter writes WebP)"; return -1; }
        else mime = "image/png";
        // the name
        string name = null;
        var paths = imgs.Select(i => i.FilePath).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 1)
        {
            string file = Basename(paths[0]); string stem = SplitExt(file); string ext = file.Substring(stem.Length).ToLowerInvariant();
            if ((ext == ".png" || ext == ".jpg" || ext == ".jpeg") && stem.Length > 0) name = stem;
        }
        if (name == null) { name = string.Join("-", imgs.Select(i => SplitExt(i.Name))); if (name.Length == 0) name = "Image"; }
        // the bytes
        byte[] bytes; bool composed = false;
        if (happy)
        {
            var img = imgs[0];
            if (img.Undecodable != null && !(img.Undecodable.StartsWith("a JPEG") || img.Undecodable.StartsWith("a 16-bit") || img.Undecodable.StartsWith("an interlaced"))) { why = img.Undecodable + " ('" + img.Name + "': the exporter encodes it again)"; return -1; }
            bytes = img.ExportBytes;
            bool ok = bytes != null && (mime == "image/png" ? PngCodec.IsPng(bytes) : bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF);
            if (!ok) { why = "the exporter encodes '" + img.Name + "' again (" + mime + " asked of other bytes)"; return -1; }
        }
        else
        {
            bool alpha = fills.ContainsKey(3);
            int width = 1, height = 1;
            foreach (var img in imgs) { width = Math.Max(width, img.Width); height = Math.Max(height, img.Height); }
            foreach (var img in imgs) if (img.Width != width || img.Height != height) { why = "a texture composed from images of different sizes (Blender scales '" + img.Name + "')"; return -1; }
            var outp = new byte[width * height * 4];
            var outF = new float[width * height * 4];
            for (int i = 0; i < outF.Length; i++) outF[i] = 1f;
            foreach (var img in imgs)
            {
                var px = img.Pixels(out string pwhy);
                if (px == null) { why = pwhy + " ('" + img.Name + "' inside a composed texture)"; return -1; }
                int n = width * height;
                foreach (int dst in order)
                {
                    var f = fills[dst];
                    if (f.White || f.Img != img) continue;
                    if (!f.Bw) for (int p = 0; p < n; p++) outF[4 * p + dst] = (float)(px[4 * p + f.Src] * (1.0f / 255.0f));
                    else
                    {
                        const float c1 = 0.2989f, c2 = 0.5870f, c3 = 0.1140f;
                        for (int p = 0; p < n; p++)
                        {
                            float r = (float)(px[4 * p] * (1.0f / 255.0f)), g = (float)(px[4 * p + 1] * (1.0f / 255.0f)), b = (float)(px[4 * p + 2] * (1.0f / 255.0f)), a = (float)(px[4 * p + 3] * (1.0f / 255.0f));
                            float lum = (float)((float)((float)(r * c1) + (float)(g * c2)) + (float)(b * c3));
                            outF[4 * p + dst] = (float)(lum * a);   // alpha_mode STRAIGHT: multiplied by the alpha
                        }
                    }
                }
            }
            for (int i = 0; i < outF.Length; i++) outp[i] = UnitFloatToUchar(outF[i]);
            bytes = PngCodec.Encode(width, height, outp, alpha);
            composed = true;
        }
        // the texture's transform comes from the PRIMARY socket's Mapping node (__gather_texture_transform_and_tex_coord)
        string mw = MappingWritten(sockets[0].slot);
        if (mw != null) { why = mw; return -1; }
        int image = w.ImageIndex(name, mime, bytes, composed);
        int sampler = w.Sampler(sockets[0].slot.Clamp);
        return w.Texture(image, sampler);
    }

    /// <summary>unit_float_to_uchar_clamp.</summary>
    internal static byte UnitFloatToUchar(float v) => v <= 0.0f ? (byte)0 : v > (float)(1.0f - (float)(0.5f / 255.0f)) ? (byte)255 : (byte)(float)((float)(255.0f * v) + 0.5f);

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

    static bool IsJpegByBlender(Image img)
    {
        string p = img.FilePath.ToLowerInvariant();
        return p.EndsWith(".jpg") || p.EndsWith(".jpeg") || p.EndsWith(".jpe");
    }
    static bool IsWebp(Image img) => img.FilePath.ToLowerInvariant().EndsWith(".webp");

    static double Wide(float f) => f;

    /// <summary>A Mapping node the exporter would turn into KHR_texture_transform (a fallback), or one it skips or finds empty.</summary>
    static string MappingWritten(Slot s)
    {
        if (!s.HasMapping) return null;
        if (s.Rot[0] != 0f || s.Rot[1] != 0f) return null;                         // only a Z rotation can be exported: skipped
        if (Math.Abs(s.Scale[0]) < 1e-5 || Math.Abs(s.Scale[1]) < 1e-5) return null;   // inverted_trs_mapping_node: None
        if (Math.Abs(s.Rot[2]) > 1e-5 && Math.Abs(s.Scale[0] - s.Scale[1]) > 1e-5) return null;
        if (s.Loc[0] == 0f && s.Loc[1] == 0f && s.Rot[2] == 0f && s.Scale[0] == 1f && s.Scale[1] == 1f) return null;
        return "KHR_texture_transform (a texture's Translation, Rotation or Scaling: the model carries no texture transform)";
    }

    /// <summary>One material as gather_material writes it; the textures, images and samplers it uses are appended to the writer's model.</summary>
    public static HafMaterial Export(Writer w, Material m, string name, out string why)
    {
        why = null;
        var hm = new HafMaterial { Name = name, NameAbsent = false, DoubleSided = true };
        // ---- the serializer's member order: emissiveTexture, extensions, normalTexture, pbrMetallicRoughness
        // emission: colour (a linked socket reads [1, 1, 1]) * strength (not applied when linked), dropped at black
        double[] factor = m.EmissionColorTex.Image != null ? new[] { 1.0, 1.0, 1.0 } : new[] { Wide(m.EmissionColor[0]), Wide(m.EmissionColor[1]), Wide(m.EmissionColor[2]) };
        if (m.EmissionStrengthTex.Image == null) { double st = Wide(m.EmissionStrength); factor = factor.Select(f => f * st).ToArray(); }
        bool emits = !(factor[0] == 0 && factor[1] == 0 && factor[2] == 0);
        double emissiveStrength = 0; bool strengthExt = false;
        if (emits && factor.Any(f => f > 1.0)) { emissiveStrength = factor.Max(); factor = factor.Select(f => f / emissiveStrength).ToArray(); strengthExt = true; }
        if (m.EmissionColorTex.Image != null)
        {
            int t = GatherTexture(w, new List<(Slot, string)> { (m.EmissionColorTex, "Emission Color") }, out why);
            if (why != null) return null;
            if (t >= 0) hm.EmissiveTexture = t;
        }
        var ext = new JObject();
        if (strengthExt) { ext["KHR_materials_emissive_strength"] = new JObject { ["emissiveStrength"] = emissiveStrength }; w.Extension("KHR_materials_emissive_strength"); }
        // KHR_materials_specular
        var spec = new JObject();
        double? fac = null;
        if (m.SpecularTex.Image == null)
        {
            fac = Wide(m.Specular) * 2.0;
            if (fac < 1.0) spec["specularFactor"] = fac.Value;
        }
        else
        {
            int t = GatherTexture(w, new List<(Slot, string)> { (m.SpecularTex, "Specular IOR Level") }, out why);
            if (why != null) return null;
            if (t >= 0) spec["specularTexture"] = new JObject { ["index"] = t };
        }
        if (fac.HasValue && fac.Value > 1.0) spec["specularColorFactor"] = new JArray(fac.Value, fac.Value, fac.Value);
        if (spec.Count > 0) { ext["KHR_materials_specular"] = spec; w.Extension("KHR_materials_specular"); }
        if (ext.Count > 0) hm.ExtensionsJson = ext.ToString(Newtonsoft.Json.Formatting.None);
        // the normal texture
        if (m.NormalTex.Image != null)
        {
            int t = GatherTexture(w, new List<(Slot, string)> { (m.NormalTex, "Normal") }, out why);
            if (why != null) return null;
            if (t >= 0) { hm.NormalTexture = t; hm.NormalScale = m.NormalStrength != 1f ? m.NormalStrength : 1f; }
        }
        // pbrMetallicRoughness: the base colour texture (base and alpha slots), the metallic-roughness texture, the factors
        var baseInputs = new List<(Slot, string)>();
        if (m.BaseColorTex.Image != null) baseInputs.Add((m.BaseColorTex, "Base Color"));
        if (m.AlphaTex.Image != null) baseInputs.Add((m.AlphaTex, "Alpha"));
        if (baseInputs.Count > 0)
        {
            int t = GatherTexture(w, baseInputs, out why);
            if (why != null) return null;
            if (t >= 0) hm.BaseColorTexture = t;
        }
        bool hasMetal = m.MetallicTex.Image != null, hasRough = m.RoughnessTex.Image != null;
        if (hasMetal || hasRough)
        {
            var inputs = new List<(Slot, string)>();
            if (hasMetal) inputs.Add((m.MetallicTex, "Metallic"));
            if (hasRough) inputs.Add((m.RoughnessTex, "Roughness"));
            int t = GatherTexture(w, inputs, out why);
            if (why != null) return null;
            if (t >= 0) hm.MetallicRoughnessTexture = t;
        }
        // the base colour factor and the alpha
        double[] rgb = m.BaseColorTex.Image != null ? new[] { 1.0, 1.0, 1.0 } : new[] { Wide(m.BaseColor[0]), Wide(m.BaseColor[1]), Wide(m.BaseColor[2]) };
        double alpha = 1.0; string mode = "OPAQUE";
        if (m.AlphaTex.Image != null) mode = "BLEND";
        else
        {
            double a = Wide(m.Alpha);
            if (a == 1.0) mode = "OPAQUE";
            else { alpha = a; mode = a == 0.0 ? "MASK" : "BLEND"; }
        }
        hm.AlphaMode = mode; hm.AlphaCutoff = 0.5;
        hm.BaseColorFactor = new[] { rgb[0], rgb[1], rgb[2], alpha };
        hm.MetallicFactor = hasMetal ? 1f : m.Metallic;
        hm.RoughnessFactor = hasRough ? 1f : m.Roughness;
        hm.EmissiveFactorJson = emits ? factor : new[] { 0.0, 0.0, 0.0 };
        hm.EmissiveFactor = hm.EmissiveFactorJson.Select(v => (float)v).ToArray();
        return hm;
    }
}
