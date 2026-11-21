// MaxModel.cs - a 3ds Max scene as the model every HAF tool reads (2026-11-21): MaxScene -> HafModel, and from there a
// .glb through GlbWriter. The four tools that take a .max (Model Cutter, Model Fuser, Vehicle Lab, Model Factory) convert
// it at the door (MaxImport) and work on that .glb, so every later step - and every Blender fallback - sees a plain GLB.
//
// THE MAPPING, each rule held on the user's four models (renders compared with the artists' own):
// - axes: 3ds Max is Z-up, a GLB Y-up: (x, y, z) -> (x, z, -y). Units are left as the file has them.
// - a node keeps its name, its parent and the PLACE of its pivot, but not its turn or scale: a part's vertices are
//   written in world axes relative to its pivot. A .max scale may be negative (a mirrored half), unequal, or unequal
//   along a turned axis system, none of which a GLB node's translation/rotation/scale can say without shear; the place
//   is what the tools use (a wheel's pivot is its axle).
// - a mirrored part (negative determinant) has its triangles re-wound, so every face still faces out.
// - normals come from the smoothing groups: a corner's normal sums the area-weighted normals of the faces around its
//   vertex that share a smoothing group with its face; a face without a group is flat.
// - one primitive per material a part uses. A material's colour is the Standard material's diffuse colour (the file's
//   display values, written as the linear numbers a GLB holds).
// - the diffuse slot's picture is embedded: a PNG or JPEG as it is; anything that needs its pixels - a TGA or BMP, a map
//   mixed with the colour (amount below 100 %), a decal (a Mask map, or tiling switched off) - is decoded, composed and
//   written as a PNG. A decal is shown ONCE in 3ds Max, with the plain colour around it: its picture is set in the
//   middle of a larger canvas of that colour and the UVs are scaled to match, so no sampler setting has to be honoured
//   downstream. A map's own offset and tiling are folded into the UVs.
// What is not carried is NAMED in the report (MaxReport.Notes), never dropped silently: see MaxScene.cs for the list.
//
// Pure C#: no Unity (PngCodec for PNG; a TGA and a BMP decoder below; JPEG pixels come from a decoder the caller hands in -
// Unity's in the editor - and without one a JPEG is embedded as it is and a decal over it is named as left).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

/// <summary>Decodes an encoded image the reader has no decoder of its own for (JPEG): four bytes per pixel, rows top-down. False = cannot.</summary>
public delegate bool MaxImageDecoder(byte[] file, string name, out int width, out int height, out byte[] rgba);

public sealed class MaxReport
{
    public string Source = "";
    public int Nodes, Parts, Triangles, PartsWithUv, HiddenFaces, Materials, Images, ModifiersSkipped;
    public bool Compressed;
    public double[] Size = { 0, 0, 0 };                                  // the model's extent in GLB axes (x, up, depth)
    public readonly List<string> MissingTextures = new List<string>();
    public readonly List<string> PartsLeft = new List<string>();        // "name: why" - a geometry object that gave no mesh
    public readonly SortedDictionary<string, int> NotGeometry = new SortedDictionary<string, int>(StringComparer.Ordinal);   // lights, cameras, helpers by class
    public readonly List<string> Notes = new List<string>();

    internal void Note(string s) { if (!Notes.Contains(s)) Notes.Add(s); }

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append(Parts).Append(" part").Append(Parts == 1 ? "" : "s").Append(", ").Append(Triangles.ToString("N0", CultureInfo.InvariantCulture)).Append(" triangles, ")
          .Append(PartsWithUv).Append(" with UVs, ").Append(Materials).Append(" material").Append(Materials == 1 ? "" : "s").Append(", ").Append(Images).Append(" texture").Append(Images == 1 ? "" : "s").Append(" embedded");
        sb.Append(string.Format(CultureInfo.InvariantCulture, "; size {0:0.###} x {1:0.###} x {2:0.###} (wide, high, long)", Size[0], Size[1], Size[2]));
        if (PartsLeft.Count > 0) sb.Append("\nNOT READ (").Append(PartsLeft.Count).Append("): ").Append(string.Join("; ", PartsLeft.Take(12))).Append(PartsLeft.Count > 12 ? "; ..." : "");
        if (MissingTextures.Count > 0) sb.Append("\nTextures not found beside the file: ").Append(string.Join(", ", MissingTextures));
        if (NotGeometry.Count > 0) sb.Append("\nLeft out (nothing to draw): ").Append(string.Join(", ", NotGeometry.Select(k => k.Value + " x " + k.Key)));
        foreach (var n in Notes) sb.Append("\n- ").Append(n);
        return sb.ToString();
    }
}

public static class MaxModel
{
    public const string GeneratorTag = "HAF MaxModel";
    public const string Revision = "2026-11-21.1";
    public const int MaxPad = 4;                 // a decal's canvas reaches at most this many pictures to each side
    public const int MaxCanvas = 8192;           // and stays within this many pixels a side

    public static bool IsMax(string path) => path != null && path.Trim().EndsWith(".max", StringComparison.OrdinalIgnoreCase);

    public static HafModel Read(string path, MaxImageDecoder decoder, out MaxReport report)
        => Build(MaxScene.Read(path), path, decoder, out report);

    /// <summary>Where a .max file's converted copy is written: `name_max.glb` beside it.</summary>
    public static string DefaultGlbPath(string maxPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(maxPath)), Path.GetFileNameWithoutExtension(maxPath) + "_max.glb").Replace('\\', '/');

    /// <summary>True for a .glb this reader wrote (its generator line says so): the only kind a conversion overwrites unasked.</summary>
    public static bool IsOurs(string glbPath)
    {
        try { return File.Exists(glbPath) && GlbReader.Read(glbPath).Generator.Contains(GeneratorTag); }
        catch (Exception) { return false; }
    }

    /// <summary>Reads the .max and writes it as a .glb (through a temporary name, moved into place). A file that cannot be
    /// read throws InvalidDataException with the reason; the target is untouched then.</summary>
    public static MaxReport ConvertToGlb(string maxPath, string glbPath, MaxImageDecoder decoder)
    {
        var model = Read(maxPath, decoder, out var report);
        GlbWriter.Write(model, glbPath);
        return report;
    }

    // ------------------------------------------------------------------------------------------ the conversion
    sealed class Part
    {
        public MaxNode Node; public int NodeIndex;
        public double[] Pos;              // 3 per vertex, GLB axes, relative to the pivot
        public int[] Faces, TFaces;       // re-wound where the part is mirrored
        public uint[] Sg; public int[] Slot;   // per face: smoothing group, index into `slots`
        public double[] Tv;               // 3 per UV vertex or null
    }
    sealed class Slot                      // one Standard material (or none) as the faces name it
    {
        public MaxMaterial Mat; public string Why;
        public bool Textured;             // the map's file was found
        public string File;               // its full path
        public double MinU = double.MaxValue, MaxU = double.MinValue, MinV = double.MaxValue, MaxV = double.MinValue;   // over the textured faces kept on the picture
        public int Ku, Kv;                // the decal canvas: pictures added to each side
        public bool Canvas;               // the picture was composed onto that canvas (false: embedded as it is, UVs untouched)
        public int Image = -1;            // HafModel.Images index
        public int Haf = -1, HafPlain = -1;   // HafModel.Materials: with the picture, and the plain-colour twin
    }

    public static HafModel Build(MaxScene scene, string sourcePath, MaxImageDecoder decoder, out MaxReport report)
    {
        var rep = report = new MaxReport { Source = sourcePath ?? "", Nodes = scene.Nodes.Count, Compressed = scene.Compressed };
        var model = new HafModel { Generator = GeneratorTag + " " + Revision, SourcePath = sourcePath ?? "" };
        string dir = string.IsNullOrEmpty(sourcePath) ? null : Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        var files = new FileFinder(dir);

        // ---- pass 1: every part's geometry in world axes, and which material each face names
        var slots = new List<Slot>(); var slotOf = new Dictionary<int, int>(); int noMaterial = -1;
        var parts = new List<Part>();
        for (int ni = 0; ni < scene.Nodes.Count; ni++)
        {
            var n = scene.Nodes[ni];
            if (n.Mesh == null)
            {
                if (n.MeshLeft != null) rep.PartsLeft.Add(Label(n) + ": " + n.MeshLeft);
                else if (n.BaseClass != "") { rep.NotGeometry.TryGetValue(n.BaseClass, out int c); rep.NotGeometry[n.BaseClass] = c + 1; }
                continue;
            }
            rep.HiddenFaces += n.Mesh.Hidden;
            if (n.Mesh.FaceCount == 0) { rep.PartsLeft.Add(Label(n) + ": no visible faces"); continue; }
            var w = MaxScene.Mul(n.World, n.Offset);
            bool finite = true;
            foreach (double v in w) if (double.IsNaN(v) || double.IsInfinity(v)) finite = false;
            if (!finite) { rep.PartsLeft.Add(Label(n) + ": its transform is not a finite number"); continue; }
            double det = MaxScene.Det3(w);
            if (det == 0) { rep.PartsLeft.Add(Label(n) + ": its transform flattens it (a scale of 0)"); continue; }
            if (n.TransformNote != null) rep.Note("part '" + n.Name + "' may be misplaced: " + n.TransformNote);
            rep.ModifiersSkipped += n.Modifiers;
            var m = n.Mesh;
            var p = new Part { Node = n, NodeIndex = ni, Sg = m.Smoothing, Tv = m.TVerts };
            double px = n.World[3], py = n.World[7], pz = n.World[11];
            int nv = m.Verts.Length / 3;
            p.Pos = new double[nv * 3];
            bool fits = true;
            for (int i = 0; i < nv; i++)
            {
                double x = m.Verts[i * 3], y = m.Verts[i * 3 + 1], z = m.Verts[i * 3 + 2];
                double wx = w[0] * x + w[1] * y + w[2] * z + w[3] - px;
                double wy = w[4] * x + w[5] * y + w[6] * z + w[7] - py;
                double wz = w[8] * x + w[9] * y + w[10] * z + w[11] - pz;
                p.Pos[i * 3] = wx; p.Pos[i * 3 + 1] = wz; p.Pos[i * 3 + 2] = -wy;
                if (Math.Abs(wx) > 1e30 || Math.Abs(wy) > 1e30 || Math.Abs(wz) > 1e30) fits = false;
            }
            if (!fits) { rep.PartsLeft.Add(Label(n) + ": its coordinates do not fit a 32-bit float"); continue; }
            int nf = m.FaceCount;
            p.Faces = (int[])m.Faces.Clone(); p.TFaces = m.TFaces == null ? null : (int[])m.TFaces.Clone();
            if (det < 0)
                for (int f = 0; f < nf; f++)
                {
                    int t = p.Faces[f * 3]; p.Faces[f * 3] = p.Faces[f * 3 + 2]; p.Faces[f * 3 + 2] = t;
                    if (p.TFaces != null) { t = p.TFaces[f * 3]; p.TFaces[f * 3] = p.TFaces[f * 3 + 2]; p.TFaces[f * 3 + 2] = t; }
                }
            p.Slot = new int[nf];
            var byId = new Dictionary<int, int>();
            for (int f = 0; f < nf; f++)
            {
                if (!byId.TryGetValue(m.MatId[f], out int si))
                {
                    var mat = scene.MaterialFor(n, m.MatId[f], out string why);
                    if (mat == null)
                    {
                        if (why != "no material") rep.Note("part '" + n.Name + "': " + why + " (drawn in plain grey)");
                        if (noMaterial < 0) { noMaterial = slots.Count; slots.Add(new Slot { Why = why }); }
                        si = noMaterial;
                    }
                    else if (!slotOf.TryGetValue(mat.Object, out si)) { si = slots.Count; slotOf[mat.Object] = si; slots.Add(new Slot { Mat = mat }); }
                    byId[m.MatId[f]] = si;
                }
                p.Slot[f] = si;
            }
            parts.Add(p);
        }
        if (parts.Count == 0)
            throw new InvalidDataException("the .max file holds no mesh this reader can read" + (rep.PartsLeft.Count > 0 ? ": " + string.Join("; ", rep.PartsLeft.Take(6)) + (rep.PartsLeft.Count > 6 ? "; ..." : "") : " (no geometry object in it)"));
        if (rep.ModifiersSkipped > 0) rep.Note(rep.ModifiersSkipped + " modifier(s) sit on parts of this scene; a .max stores a modifier's settings, not its result, so those parts are read as they were BEFORE the modifier");
        if (rep.HiddenFaces > 0) rep.Note(rep.HiddenFaces + " face(s) are marked hidden in the file (3ds Max does not draw them): left out");

        // ---- pass 2: per material, where its picture is and how far the faces reach outside it
        foreach (var s in slots)
        {
            if (s.Mat == null) continue;
            if (s.Mat.MapLeft != null) rep.Note("material '" + s.Mat.Name + "': " + s.Mat.MapLeft);
            if (s.Mat.Opacity < 1) rep.Note("material '" + s.Mat.Name + "' is " + Math.Round(s.Mat.Opacity * 100).ToString(CultureInfo.InvariantCulture) + " % opaque in 3ds Max: written opaque");
            if (s.Mat.Map == null) continue;
            s.File = files.Find(s.Mat.Map.File);
            if (s.File == null) { if (!rep.MissingTextures.Contains(s.Mat.Map.File)) rep.MissingTextures.Add(s.Mat.Map.File); continue; }
            s.Textured = true;
            if (s.Mat.Map.Angle != 0) rep.Note("material '" + s.Mat.Name + "': its map is turned by an angle, which is not applied");
            if (s.Mat.Map.HasPlacement) rep.Note("material '" + s.Mat.Name + "': the map's offset/tiling (" + Num(s.Mat.Map.OffsetU) + ", " + Num(s.Mat.Map.OffsetV) + " / " + Num(s.Mat.Map.TilingU) + ", " + Num(s.Mat.Map.TilingV) + ") is folded into the UVs by the documented rule - check its placement");
        }
        foreach (var p in parts)
        {
            if (p.TFaces == null) continue;
            for (int f = 0; f < p.Slot.Length; f++)
            {
                var s = slots[p.Slot[f]];
                if (!s.Textured || !OnPicture(p, f, s.Mat.Map, out double lu, out double hu, out double lv, out double hv)) continue;
                if (lu < s.MinU) s.MinU = lu; if (hu > s.MaxU) s.MaxU = hu; if (lv < s.MinV) s.MinV = lv; if (hv > s.MaxV) s.MaxV = hv;
            }
        }

        // ---- pass 3: the pictures
        var imageOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in slots)
        {
            if (!s.Textured || s.MinU > s.MaxU) { s.Textured = s.Textured && s.MinU <= s.MaxU; continue; }   // no face with UVs uses it: nothing to embed
            BuildImage(model, s, files, decoder, rep, imageOf);
        }

        // ---- pass 4: materials, meshes, nodes
        int MaterialOf(Slot s, bool textured)
        {
            if (textured && s.Image >= 0)
            {
                if (s.Haf < 0)
                {
                    model.Textures.Add(new HafTexture { Source = s.Image });
                    s.Haf = model.Materials.Count;
                    model.Materials.Add(new HafMaterial { Name = s.Mat.Name, BaseColorTexture = model.Textures.Count - 1, MetallicFactor = 0, RoughnessFactor = 0.5f, DoubleSided = s.Mat.TwoSided });
                }
                return s.Haf;
            }
            if (s.HafPlain < 0)
            {
                s.HafPlain = model.Materials.Count;
                var c = s.Mat == null ? new[] { 0.6, 0.6, 0.6 } : s.Mat.Diffuse;
                model.Materials.Add(new HafMaterial
                {
                    Name = s.Mat == null ? "NoMaterial" : s.Image >= 0 ? s.Mat.Name + "_plain" : s.Mat.Name,
                    BaseColorFactor = new[] { Linear(c[0]), Linear(c[1]), Linear(c[2]), 1.0 },
                    MetallicFactor = 0, RoughnessFactor = 0.5f, DoubleSided = s.Mat != null && s.Mat.TwoSided,
                });
            }
            return s.HafPlain;
        }

        var hafNode = new int[scene.Nodes.Count];
        for (int i = 0; i < hafNode.Length; i++) hafNode[i] = -2;       // -2 = not needed
        foreach (var p in parts) for (int at = p.NodeIndex; at >= 0 && hafNode[at] == -2; at = scene.Nodes[at].Parent) hafNode[at] = -1;
        for (int i = 0; i < scene.Nodes.Count; i++)
        {
            if (hafNode[i] != -1) continue;
            hafNode[i] = model.Nodes.Count;
            model.Nodes.Add(new HafNode { Name = scene.Nodes[i].Name == "" ? "Object" : scene.Nodes[i].Name });
        }
        var scn = new HafScene { Name = "Scene" };
        for (int i = 0; i < scene.Nodes.Count; i++)
        {
            if (hafNode[i] < 0) continue;
            var n = scene.Nodes[i]; var hn = model.Nodes[hafNode[i]];
            double x = n.World[3], y = n.World[7], z = n.World[11];
            if (n.Parent >= 0)
            {
                var q = scene.Nodes[n.Parent];
                x -= q.World[3]; y -= q.World[7]; z -= q.World[11];
                hn.Parent = hafNode[n.Parent]; model.Nodes[hn.Parent].Children.Add(hafNode[i]);
            }
            else scn.Nodes.Add(hafNode[i]);
            hn.Translation = new[] { (double)(float)x, (double)(float)z, (double)(float)-y };
        }
        model.Scenes.Add(scn); model.Scene = 0;

        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        foreach (var p in parts)
        {
            var mesh = BuildMesh(p, slots, MaterialOf);
            model.Nodes[hafNode[p.NodeIndex]].Mesh = model.Meshes.Count;
            model.Meshes.Add(mesh);
            rep.Parts++; rep.Triangles += p.Slot.Length;
            if (p.TFaces != null) rep.PartsWithUv++;
            double px = p.Node.World[3], py = p.Node.World[11], pz = -p.Node.World[7];
            for (int i = 0; i < p.Pos.Length; i += 3)
            {
                double a = p.Pos[i] + px, b = p.Pos[i + 1] + py, c = p.Pos[i + 2] + pz;
                if (a < lo[0]) lo[0] = a; if (a > hi[0]) hi[0] = a; if (b < lo[1]) lo[1] = b; if (b > hi[1]) hi[1] = b; if (c < lo[2]) lo[2] = c; if (c > hi[2]) hi[2] = c;
            }
        }
        rep.Size = new[] { hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2] };
        rep.Materials = model.Materials.Count; rep.Images = model.Images.Count;
        return model;
    }

    static string Label(MaxNode n) => "'" + n.Name + "'" + (n.BaseClass == "" ? "" : " (" + n.BaseClass + ")");
    static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A display value 0..1 as the linear number a GLB's colour factor holds (the sRGB curve).</summary>
    public static double Linear(double c)
    {
        if (double.IsNaN(c)) return 0;
        c = c < 0 ? 0 : c > 1 ? 1 : c;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>The place on the picture a UV lands, with the map's own offset and tiling applied (about the centre, as 3ds Max does).</summary>
    static void Place(MaxBitmap b, double u, double v, out double pu, out double pv)
    {
        pu = (u - b.OffsetU - 0.5) * b.TilingU + 0.5;
        pv = (v - b.OffsetV - 0.5) * b.TilingV + 0.5;
    }

    /// <summary>False for a face that lies wholly beside a picture that does not repeat (it shows the plain colour only);
    /// else the face's extent on the picture.</summary>
    static bool OnPicture(Part p, int f, MaxBitmap b, out double lu, out double hu, out double lv, out double hv)
    {
        lu = lv = double.MaxValue; hu = hv = double.MinValue;
        for (int k = 0; k < 3; k++)
        {
            int t = p.TFaces[f * 3 + k];
            Place(b, p.Tv[t * 3], p.Tv[t * 3 + 1], out double u, out double v);
            if (u < lu) lu = u; if (u > hu) hu = u; if (v < lv) lv = v; if (v > hv) hv = v;
        }
        if (!b.TileU && (hu <= 0 || lu >= 1)) return false;
        if (!b.TileV && (hv <= 0 || lv >= 1)) return false;
        return true;
    }

    static int PadFor(double min, double max, int size)
    {
        double reach = Math.Max(Math.Max(-min, max - 1), 0);
        int k = (int)Math.Min(MaxPad, Math.Ceiling(reach - 1e-4));   // a hair over the edge is held at the edge, not given a whole picture
        while (k > 0 && (long)size * (2 * k + 1) > MaxCanvas) k--;
        return k;
    }

    static void BuildImage(HafModel model, Slot s, FileFinder files, MaxImageDecoder decoder, MaxReport rep, Dictionary<string, int> imageOf)
    {
        var mat = s.Mat; var map = mat.Map;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(s.File); }
        catch (Exception e) { rep.Note("texture '" + map.File + "' could not be read: " + e.Message); s.Textured = false; return; }
        string kind = Kind(bytes, s.File);
        bool decal = !map.TileU || !map.TileV;
        string maskFile = mat.Mask == null ? null : files.Find(mat.Mask.File);
        if (mat.Mask != null && maskFile == null) { if (!rep.MissingTextures.Contains(mat.Mask.File)) rep.MissingTextures.Add(mat.Mask.File); rep.Note("material '" + mat.Name + "': its mask picture is missing, the map is shown whole"); }
        bool mix = Math.Abs(mat.Amount - 1) > 1e-6;
        bool needsPixels = decal || maskFile != null || mix || (kind != "png" && kind != "jpeg");
        if (!needsPixels)
        {
            string key = "raw|" + s.File.ToLowerInvariant();
            if (!imageOf.TryGetValue(key, out s.Image))
            {
                s.Image = model.Images.Count; imageOf[key] = s.Image;
                model.Images.Add(new HafImage { Name = Path.GetFileNameWithoutExtension(s.File), MimeType = kind == "png" ? "image/png" : "image/jpeg", Bytes = bytes });
            }
            return;
        }
        if (!MaxImages.Decode(bytes, s.File, decoder, out int w, out int h, out byte[] rgba, out string why))
        {
            if (kind == "png" || kind == "jpeg")
            {
                // the picture itself can still be shown: embedded as it is, its decal / mix left by name
                rep.Note("material '" + mat.Name + "': '" + map.File + "' could not be decoded (" + why + "), so it is embedded as it is" + (decal ? " and REPEATS where 3ds Max shows it once" : "") + (maskFile != null ? ", without its mask" : "") + (mix ? ", at 100 % instead of " + Math.Round(mat.Amount * 100).ToString(CultureInfo.InvariantCulture) + " %" : ""));
                string key = "raw|" + s.File.ToLowerInvariant();
                if (!imageOf.TryGetValue(key, out s.Image))
                {
                    s.Image = model.Images.Count; imageOf[key] = s.Image;
                    model.Images.Add(new HafImage { Name = Path.GetFileNameWithoutExtension(s.File), MimeType = kind == "png" ? "image/png" : "image/jpeg", Bytes = bytes });
                }
            }
            else { rep.Note("texture '" + map.File + "' could not be decoded (" + why + "): the plain colour is used"); s.Textured = false; }
            return;
        }
        byte[] mask = null; int mw = 0, mh = 0;
        if (maskFile != null)
        {
            byte[] mb = null; string mwhy = null;
            try { mb = File.ReadAllBytes(maskFile); } catch (Exception e) { mwhy = e.Message; }
            if (mb == null || !MaxImages.Decode(mb, maskFile, decoder, out mw, out mh, out mask, out mwhy))
            { mask = null; rep.Note("material '" + mat.Name + "': its mask '" + mat.Mask.File + "' could not be decoded (" + mwhy + "), the map is shown whole"); }
            else if (mat.Mask.TileU != map.TileU || mat.Mask.TileV != map.TileV || mat.Mask.OffsetU != map.OffsetU || mat.Mask.OffsetV != map.OffsetV || mat.Mask.TilingU != map.TilingU || mat.Mask.TilingV != map.TilingV)
                rep.Note("material '" + mat.Name + "': its mask is placed differently from its map; the map's placement is used for both");
        }
        s.Ku = map.TileU ? 0 : PadFor(s.MinU, s.MaxU, w);
        s.Kv = map.TileV ? 0 : PadFor(s.MinV, s.MaxV, h);
        if ((!map.TileU && Math.Max(-s.MinU, s.MaxU - 1) > s.Ku + 1e-4) || (!map.TileV && Math.Max(-s.MinV, s.MaxV - 1) > s.Kv + 1e-4))
            rep.Note("material '" + mat.Name + "': faces reach further beside the decal than its canvas (" + s.Ku + " x " + s.Kv + " pictures to each side); their UVs are held at the canvas edge");
        string ckey = string.Join("|", "c", s.File.ToLowerInvariant(), mask == null ? "" : maskFile.ToLowerInvariant(), mat.Amount.ToString("R", CultureInfo.InvariantCulture),
            mat.Diffuse[0].ToString("R", CultureInfo.InvariantCulture), mat.Diffuse[1].ToString("R", CultureInfo.InvariantCulture), mat.Diffuse[2].ToString("R", CultureInfo.InvariantCulture), s.Ku, s.Kv);
        if (imageOf.TryGetValue(ckey, out s.Image)) { s.Canvas = true; return; }
        var canvas = Compose(rgba, w, h, mask, mw, mh, mat.Amount, mat.Diffuse, s.Ku, s.Kv, out int cw, out int ch);
        s.Image = model.Images.Count; imageOf[ckey] = s.Image;
        model.Images.Add(new HafImage { Name = Path.GetFileNameWithoutExtension(s.File) + (mask != null || decal ? "_decal" : mix ? "_mixed" : ""), MimeType = "image/png", Bytes = PngCodec.Encode(cw, ch, canvas, false) });
        s.Canvas = true;
    }

    static string Kind(byte[] b, string path)
    {
        if (PngCodec.IsPng(b)) return "png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpeg";
        if (b.Length > 2 && b[0] == (byte)'B' && b[1] == (byte)'M') return "bmp";
        return Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    }

    /// <summary>The picture a material shows: the map, through its mask (white shows the map) and its amount, over the
    /// material's colour - set in the middle of a canvas of that colour `ku` / `kv` pictures wider to each side.
    /// Four bytes per pixel, rows top-down, opaque.</summary>
    public static byte[] Compose(byte[] map, int w, int h, byte[] mask, int mw, int mh, double amount, double[] diffuse, int ku, int kv, out int cw, out int ch)
    {
        cw = w * (2 * ku + 1); ch = h * (2 * kv + 1);
        var d = new byte[3];
        for (int i = 0; i < 3; i++) { double c = double.IsNaN(diffuse[i]) ? 0 : diffuse[i] < 0 ? 0 : diffuse[i] > 1 ? 1 : diffuse[i]; d[i] = (byte)Math.Floor(c * 255 + 0.5); }
        if (double.IsNaN(amount)) amount = 1;
        amount = amount < 0 ? 0 : amount > 1 ? 1 : amount;
        var o = new byte[(long)cw * ch * 4];
        for (int i = 0; i < o.Length; i += 4) { o[i] = d[0]; o[i + 1] = d[1]; o[i + 2] = d[2]; o[i + 3] = 255; }
        for (int y = 0; y < h; y++)
        {
            int row = ((kv * h + y) * cw + ku * w) * 4;
            for (int x = 0; x < w; x++)
            {
                int at = (y * w + x) * 4;
                double a = amount;
                if (mask != null)
                {
                    int ma = ((int)((long)y * mh / h) * mw + (int)((long)x * mw / w)) * 4;
                    a *= (mask[ma] + mask[ma + 1] + mask[ma + 2]) / 765.0;
                }
                for (int k = 0; k < 3; k++) o[row + x * 4 + k] = (byte)Math.Floor(map[at + k] * a + d[k] * (1 - a) + 0.5);
            }
        }
        return o;
    }

    // ------------------------------------------------------------------------------------------ a part's mesh
    static HafMesh BuildMesh(Part p, List<Slot> slots, Func<Slot, bool, int> materialOf)
    {
        int nf = p.Slot.Length, nv = p.Pos.Length / 3;
        // face normals (area-weighted: the cross product, not normalized) from the final winding
        var fn = new double[nf * 3];
        for (int f = 0; f < nf; f++)
        {
            int a = p.Faces[f * 3] * 3, b = p.Faces[f * 3 + 1] * 3, c = p.Faces[f * 3 + 2] * 3;
            double ux = p.Pos[b] - p.Pos[a], uy = p.Pos[b + 1] - p.Pos[a + 1], uz = p.Pos[b + 2] - p.Pos[a + 2];
            double vx = p.Pos[c] - p.Pos[a], vy = p.Pos[c + 1] - p.Pos[a + 1], vz = p.Pos[c + 2] - p.Pos[a + 2];
            fn[f * 3] = uy * vz - uz * vy; fn[f * 3 + 1] = uz * vx - ux * vz; fn[f * 3 + 2] = ux * vy - uy * vx;
        }
        // the faces around each vertex, in face order
        var start = new int[nv + 1];
        for (int i = 0; i < nf * 3; i++) start[p.Faces[i] + 1]++;
        for (int i = 0; i < nv; i++) start[i + 1] += start[i];
        var fill = (int[])start.Clone(); var around = new int[nf * 3];
        for (int f = 0; f < nf; f++) for (int k = 0; k < 3; k++) around[fill[p.Faces[f * 3 + k]]++] = f;

        // a corner's normal, shared by VALUE at its vertex: two corners whose groups differ (3 and 2) but smooth over the
        // same faces are one vertex, and so are the corners of two flat faces lying in one plane
        var normals = new List<double>(); var smoothKey = new Dictionary<long, int>(); var byValue = new Dictionary<(int, double, double, double), int>();
        int CornerNormal(int f, int v)
        {
            uint sg = p.Sg[f];
            long key = sg != 0 ? ((long)v << 32) | sg : -1;
            if (sg != 0 && smoothKey.TryGetValue(key, out int have)) return have;
            double x = 0, y = 0, z = 0;
            if (sg == 0) { x = fn[f * 3]; y = fn[f * 3 + 1]; z = fn[f * 3 + 2]; }
            else
            {
                int last = -1;
                for (int i = start[v]; i < start[v + 1]; i++)
                {
                    int g = around[i];
                    if (g == last) continue;          // a face naming the vertex twice counts once
                    last = g;
                    if ((p.Sg[g] & sg) == 0) continue;
                    x += fn[g * 3]; y += fn[g * 3 + 1]; z += fn[g * 3 + 2];
                }
            }
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (len == 0) { x = fn[f * 3]; y = fn[f * 3 + 1]; z = fn[f * 3 + 2]; len = Math.Sqrt(x * x + y * y + z * z); }
            if (len == 0) { x = 0; y = 1; z = 0; len = 1; }
            x = x / len; y = y / len; z = z / len;
            if (!byValue.TryGetValue((v, x, y, z), out int id))
            {
                id = normals.Count / 3; byValue[(v, x, y, z)] = id;
                normals.Add(x); normals.Add(y); normals.Add(z);
            }
            if (sg != 0) smoothKey[key] = id;
            return id;
        }

        // one primitive per material, in the order the faces first name them
        var mesh = new HafMesh { Name = p.Node.Name == "" ? "Object" : p.Node.Name };
        var order = new List<int>(); var facesOf = new Dictionary<int, List<int>>();
        for (int f = 0; f < nf; f++)
        {
            var s = slots[p.Slot[f]];
            bool textured = p.TFaces != null && s.Textured && s.Image >= 0 && OnPicture(p, f, s.Mat.Map, out _, out _, out _, out _);
            int m = materialOf(s, textured);
            if (!facesOf.TryGetValue(m, out var list)) { facesOf[m] = list = new List<int>(); order.Add(m); }
            list.Add(textured ? f : ~f);
        }
        foreach (int m in order)
        {
            var list = facesOf[m];
            bool textured = list[0] >= 0;
            Slot slot = slots[p.Slot[textured ? list[0] : ~list[0]]];
            var pos = new List<float>(); var nor = new List<float>(); var uv = textured ? new List<float>() : null;
            var index = new int[list.Count * 3];
            var seen = new Dictionary<(int v, int t, int n), int>();
            for (int i = 0; i < list.Count; i++)
            {
                int f = textured ? list[i] : ~list[i];
                for (int k = 0; k < 3; k++)
                {
                    int v = p.Faces[f * 3 + k], t = textured ? p.TFaces[f * 3 + k] : -1, n = CornerNormal(f, v);
                    if (!seen.TryGetValue((v, t, n), out int at))
                    {
                        at = pos.Count / 3; seen[(v, t, n)] = at;
                        pos.Add((float)p.Pos[v * 3]); pos.Add((float)p.Pos[v * 3 + 1]); pos.Add((float)p.Pos[v * 3 + 2]);
                        nor.Add((float)normals[n * 3]); nor.Add((float)normals[n * 3 + 1]); nor.Add((float)normals[n * 3 + 2]);
                        if (textured)
                        {
                            var b = slot.Mat.Map;
                            Place(b, p.Tv[t * 3], p.Tv[t * 3 + 1], out double pu, out double pv);
                            if (slot.Canvas && !b.TileU) { pu = pu < -slot.Ku ? -slot.Ku : pu > slot.Ku + 1 ? slot.Ku + 1 : pu; pu = (pu + slot.Ku) / (2 * slot.Ku + 1); }
                            double down = 1 - pv;                 // a picture's rows run top-down, 3ds Max's V bottom-up
                            if (slot.Canvas && !b.TileV) { down = down < -slot.Kv ? -slot.Kv : down > slot.Kv + 1 ? slot.Kv + 1 : down; down = (down + slot.Kv) / (2 * slot.Kv + 1); }
                            uv.Add((float)pu); uv.Add((float)down);
                        }
                    }
                    index[i * 3 + k] = at;
                }
            }
            mesh.Primitives.Add(new HafPrimitive { Mode = 4, Material = m, VertexCount = pos.Count / 3, Positions = pos.ToArray(), Normals = nor.ToArray(), Uv0 = uv?.ToArray(), Indices = index });
        }
        return mesh;
    }

    // ------------------------------------------------------------------------------------------ finding textures
    sealed class FileFinder
    {
        readonly Dictionary<string, string> byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The files beside the .max, then one folder down, then one folder up; the first of a name wins.</summary>
        public FileFinder(string dir)
        {
            if (dir == null || !Directory.Exists(dir)) return;
            void Add(string d) { try { foreach (var f in Directory.GetFiles(d).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) { string n = Path.GetFileName(f); if (!byName.ContainsKey(n)) byName[n] = f; } } catch (Exception) { } }
            Add(dir);
            try { foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) Add(d); } catch (Exception) { }
            var up = Path.GetDirectoryName(dir);
            if (up != null) Add(up);
        }
        public string Find(string name) => !string.IsNullOrEmpty(name) && byName.TryGetValue(name, out var p) ? p : null;
    }
}

/// <summary>Pixels of the pictures old 3ds Max scenes use: PNG (PngCodec), TGA and BMP here, anything else through the caller's decoder.</summary>
public static class MaxImages
{
    public const long MaxPixels = 1L << 26;      // 64 megapixels

    public static bool Decode(byte[] b, string name, MaxImageDecoder external, out int w, out int h, out byte[] rgba, out string why)
    {
        w = h = 0; rgba = null; why = null;
        try
        {
            if (PngCodec.IsPng(b))
            {
                rgba = PngCodec.Decode(b, out var info, out why);
                if (rgba == null) return false;
                w = info.Width; h = info.Height; return true;
            }
            if (b.Length > 2 && b[0] == (byte)'B' && b[1] == (byte)'M') return Bmp(b, out w, out h, out rgba, out why);
            if ((name ?? "").EndsWith(".tga", StringComparison.OrdinalIgnoreCase)) return Tga(b, out w, out h, out rgba, out why);
            if (external != null && external(b, name, out w, out h, out rgba) && rgba != null && w > 0 && h > 0 && rgba.Length == (long)w * h * 4) return true;
            why = external == null ? "no decoder for this picture format here" : "the picture decoder refused it";
            rgba = null; return false;
        }
        catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is InvalidDataException || e is OverflowException)
        { rgba = null; why = "the picture file is damaged (" + e.Message + ")"; return false; }
    }

    /// <summary>Truevision TGA: true colour (15/16/24/32 bit), grey (8 bit) and colour-mapped (8-bit index), plain or
    /// run-length packed. The alpha channel is dropped: 3ds Max does not use it for a diffuse map.</summary>
    public static bool Tga(byte[] b, out int w, out int h, out byte[] rgba, out string why)
    {
        w = h = 0; rgba = null; why = null;
        if (b.Length < 18) { why = "shorter than a TGA header"; return false; }
        int idLen = b[0], mapType = b[1], type = b[2], mapLen = b[5] | b[6] << 8, mapBits = b[7];
        w = b[12] | b[13] << 8; h = b[14] | b[15] << 8;
        int bpp = b[16], desc = b[17];
        bool rle = type >= 9; int kind = rle ? type - 8 : type;
        if (kind < 1 || kind > 3 || type > 11) { why = "TGA image type " + type; return false; }
        if (w <= 0 || h <= 0 || (long)w * h > MaxPixels) { why = "TGA size " + w + " x " + h; return false; }
        if (kind == 1 && (mapType != 1 || bpp != 8 || (mapBits != 15 && mapBits != 16 && mapBits != 24 && mapBits != 32))) { why = "a colour-mapped TGA of " + bpp + " bits over " + mapBits + "-bit entries"; return false; }
        if (kind == 2 && bpp != 15 && bpp != 16 && bpp != 24 && bpp != 32) { why = "a true-colour TGA of " + bpp + " bits"; return false; }
        if (kind == 3 && bpp != 8) { why = "a grey TGA of " + bpp + " bits"; return false; }
        int at = 18 + idLen, mapBytes = (mapBits + 7) / 8;
        int mapAt = at;
        if (mapType == 1) at += mapLen * mapBytes;
        if (at > b.Length) { why = "the TGA colour map runs past the file"; return false; }
        int bytes = (bpp + 7) / 8;
        var px = new byte[(long)w * h * bytes];
        if (!rle)
        {
            if (b.Length - at < px.Length) { why = "the TGA pixels run past the file"; return false; }
            Buffer.BlockCopy(b, at, px, 0, px.Length);
        }
        else
        {
            long o = 0;
            while (o < px.Length)
            {
                if (at >= b.Length) { why = "the TGA packets run past the file"; return false; }
                int head = b[at++], n = (head & 0x7F) + 1;
                if (o + (long)n * bytes > px.Length) n = (int)((px.Length - o) / bytes);
                if ((head & 0x80) != 0)
                {
                    if (at + bytes > b.Length) { why = "the TGA packets run past the file"; return false; }
                    for (int i = 0; i < n; i++, o += bytes) Buffer.BlockCopy(b, at, px, (int)o, bytes);
                    at += bytes;
                }
                else
                {
                    if (at + n * bytes > b.Length) { why = "the TGA packets run past the file"; return false; }
                    Buffer.BlockCopy(b, at, px, (int)o, n * bytes); at += n * bytes; o += (long)n * bytes;
                }
                if (n == 0) break;
            }
        }
        var o4 = new byte[(long)w * h * 4];
        bool top = (desc & 0x20) != 0, right = (desc & 0x10) != 0;
        void Colour(byte[] src, int s, int bits, int o)
        {
            if (bits == 8) { o4[o] = o4[o + 1] = o4[o + 2] = src[s]; }
            else if (bits == 15 || bits == 16)
            {
                int v = src[s] | src[s + 1] << 8;
                o4[o] = (byte)(((v >> 10) & 31) * 255 / 31); o4[o + 1] = (byte)(((v >> 5) & 31) * 255 / 31); o4[o + 2] = (byte)((v & 31) * 255 / 31);
            }
            else { o4[o] = src[s + 2]; o4[o + 1] = src[s + 1]; o4[o + 2] = src[s]; }
            o4[o + 3] = 255;
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * bytes;
                int o = ((top ? y : h - 1 - y) * w + (right ? w - 1 - x : x)) * 4;
                if (kind == 1)
                {
                    int ix = px[s] - (b[3] | b[4] << 8);
                    if (ix < 0 || ix >= mapLen) { why = "a TGA pixel names colour " + px[s] + " outside its map"; return false; }
                    Colour(b, mapAt + ix * mapBytes, mapBits, o);
                }
                else Colour(px, s, kind == 3 ? 8 : bpp, o);
            }
        rgba = o4;
        return true;
    }

    /// <summary>Windows BMP: 8-bit with a palette, 24-bit and 32-bit, not packed. Alpha is dropped.</summary>
    public static bool Bmp(byte[] b, out int w, out int h, out byte[] rgba, out string why)
    {
        w = h = 0; rgba = null; why = null;
        if (b.Length < 54) { why = "shorter than a BMP header"; return false; }
        int dataAt = BitConverter.ToInt32(b, 10), headSize = BitConverter.ToInt32(b, 14);
        if (headSize < 40) { why = "a BMP header of " + headSize + " bytes"; return false; }
        w = BitConverter.ToInt32(b, 18); int rawH = BitConverter.ToInt32(b, 22);
        int bpp = BitConverter.ToUInt16(b, 28), comp = BitConverter.ToInt32(b, 30);
        bool top = rawH < 0; h = top ? -rawH : rawH;
        if (w <= 0 || h <= 0 || (long)w * h > MaxPixels) { why = "BMP size " + w + " x " + h; return false; }
        if (comp != 0 || (bpp != 8 && bpp != 24 && bpp != 32)) { why = "a BMP of " + bpp + " bits, packing " + comp; return false; }
        int palAt = 14 + headSize, colours = BitConverter.ToInt32(b, 46);
        if (bpp == 8 && colours == 0) colours = 256;
        long stride = ((long)w * bpp + 31) / 32 * 4;
        if (dataAt < 0 || dataAt + stride * h > b.Length) { why = "the BMP pixels run past the file"; return false; }
        if (bpp == 8 && palAt + colours * 4 > b.Length) { why = "the BMP palette runs past the file"; return false; }
        rgba = new byte[(long)w * h * 4];
        for (int y = 0; y < h; y++)
        {
            long row = dataAt + stride * (top ? y : h - 1 - y);
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4; long s = row + (long)x * bpp / 8;
                if (bpp == 8)
                {
                    int ix = b[s];
                    if (ix >= colours) { why = "a BMP pixel names colour " + ix + " outside its palette"; rgba = null; return false; }
                    rgba[o] = b[palAt + ix * 4 + 2]; rgba[o + 1] = b[palAt + ix * 4 + 1]; rgba[o + 2] = b[palAt + ix * 4];
                }
                else { rgba[o] = b[s + 2]; rgba[o + 1] = b[s + 1]; rgba[o + 2] = b[s]; }
                rgba[o + 3] = 255;
            }
        }
        return true;
    }
}
