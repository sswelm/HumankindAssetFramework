// MaxModel.cs - a 3ds Max scene as the model every HAF tool reads (2026-11-21): MaxScene -> HafModel, and from there a
// .glb through GlbWriter. The four tools that take a .max (Model Cutter, Model Fuser, Vehicle Lab, Model Factory) convert
// it at the door (MaxImport) and work on that .glb, so every later step - and every Blender fallback - sees a plain GLB.
//
// THE MAPPING (the four sample models hold most of it; what only built files hold is said in docs/Editor-Tools.md;
// nothing was compared with 3ds Max itself):
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
// What is not carried is said in the report (MaxReport) wherever the reader can tell it is there - named part by part
// or material by material, or counted (hidden faces, lights and cameras); what it cannot tell (animation, a hidden
// object, further UV channels) is in the list at the top of MaxScene.cs and in docs/Editor-Tools.md.
// A picture's own alpha (PNG, 32-bit TGA) works like a mask: the material's colour shows where it is not opaque.
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
    public int Nodes, Parts, Triangles, HiddenFaces, Materials, Images, ModifiersSkipped;
    public int PartsWithUv;                                              // parts WRITTEN with UVs (counted on the model, not on the file's chunks)
    public int PartsUnplaced;                                            // parts whose place hangs on a controller this reader does not read
    public bool Compressed;
    public double[] Size = { 0, 0, 0 };                                  // the model's extent in GLB axes (x, up, depth)
    public readonly List<string> MissingTextures = new List<string>();
    readonly Dictionary<string, List<string>> missingBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // picture -> the materials that name it
    internal void Missing(string file, string material)
    {
        if (!missingBy.TryGetValue(file, out var l)) { missingBy[file] = l = new List<string>(); MissingTextures.Add(file); }
        if (l.Count == 0 || l[l.Count - 1] != material) l.Add(material);
    }
    public int PartsWithFileUv;                                          // parts whose FILE holds UVs this reader could read
    public readonly List<string> PartsLeft = new List<string>();        // "name: why" - a geometry object that gave no mesh
    public readonly SortedDictionary<string, int> NotGeometry = new SortedDictionary<string, int>(StringComparer.Ordinal);   // lights, cameras, helpers by class
    public readonly List<string> Notes = new List<string>();
    readonly HashSet<string> said = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>A note, once (the set, not a search of the list: a search per note made many notes cost their square).</summary>
    internal void Note(string s) { if (said.Add(s)) Notes.Add(s); }

    /// <summary>The report as text. `full` (the Console's copy) names every part that was not read; the dialog's copy
    /// names the first twelve and says how many there are.</summary>
    public string Summary(bool full = false)
    {
        var sb = new StringBuilder();
        // first, because it decides whether the model is usable at all: a file of a newer 3ds Max keeps its parts'
        // places in controllers this reader does not read, and every such part then sits at its parent's origin
        if (PartsUnplaced > 0)
            sb.Append("WARNING: ").Append(PartsUnplaced).Append(" of ").Append(Parts).Append(" part(s) may stand in the wrong place (a controller this reader does not read, or reads in part) - ")
              .Append(PartsUnplaced * 2 > Parts ? "the model is most likely not assembled.\n" : "look at them before using the model.\n");
        sb.Append(Parts).Append(" part").Append(Parts == 1 ? "" : "s").Append(", ").Append(Triangles.ToString("N0", CultureInfo.InvariantCulture)).Append(" triangles, ")
          .Append(PartsWithUv).Append(" with UVs, ").Append(Materials).Append(" material").Append(Materials == 1 ? "" : "s").Append(", ").Append(Images).Append(" texture").Append(Images == 1 ? "" : "s").Append(" embedded");
        sb.Append(string.Format(CultureInfo.InvariantCulture, "; size {0:0.###} x {1:0.###} x {2:0.###} (wide, high, long)", Size[0], Size[1], Size[2]));
        if (PartsLeft.Count > 0) sb.Append("\nNOT READ (").Append(PartsLeft.Count).Append("): ").Append(string.Join("; ", full ? PartsLeft : PartsLeft.Take(12)))
            .Append(!full && PartsLeft.Count > 12 ? "; ... (" + (PartsLeft.Count - 12) + " more, all named in the Console)" : "");
        if (MissingTextures.Count > 0) sb.Append("\nTextures not found beside the file, one folder down or one up").Append(PartsWithUv > 0 ? " (faces that have UVs keep them, so the picture can be put on later)" : "").Append(": ")
            .Append(string.Join(", ", MissingTextures.Select(f => f + " (material " + string.Join(", ", missingBy[f].Select(m => "'" + m + "'")) + ")")));
        if (NotGeometry.Count > 0) sb.Append("\nLeft out (nothing to draw): ").Append(string.Join(", ", NotGeometry.Select(k => k.Value + " x " + k.Key)));
        foreach (var n in Notes) sb.Append("\n- ").Append(n);
        return sb.ToString();
    }
}

public static class MaxModel
{
    public const string GeneratorTag = "HAF MaxModel";
    // .2: the scale's axis system is applied, UVs are kept without a picture, a cancelled smoothing sum is not shared.
    // .3: a picture's alpha shows the material's colour; a smoothing sum that cancels to a rounding residue is not shared.
    // .4: UVs only on faces that show a picture or whose picture is missing; names without spaces at their ends.
    public const string Revision = "2026-11-21.4";
    public const int MaxPad = 4;                 // a decal's canvas reaches at most this many pictures to each side
    public const int MaxCanvas = 8192;           // and the padding stays within this many pixels a side
    public const long MaxCanvasPixels = (long)MaxCanvas * MaxCanvas;   // and the whole canvas within this many pixels: the padding goes first
    public const int MaxTriangles = 10000000;    // of the whole model, every instance of a mesh counted
    public const int MaxSmoothingSums = 64;      // per vertex: combinations of smoothing groups whose sum is worked out (see BuildMesh)

    public static bool IsMax(string path) => path != null && path.Trim().EndsWith(".max", StringComparison.OrdinalIgnoreCase);

    public static HafModel Read(string path, MaxImageDecoder decoder, out MaxReport report)
        => Build(MaxScene.Read(path), path, decoder, out report);

    /// <summary>Where a .max file's converted copy is written: `name_max.glb` beside it.</summary>
    public static string DefaultGlbPath(string maxPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(maxPath)), Path.GetFileNameWithoutExtension(maxPath) + "_max.glb").Replace('\\', '/');

    /// <summary>True for a .glb whose generator names this reader. GlbWriter preserves that history when a HAF tool
    /// edits the model, so it cannot establish whether an existing file is safe to overwrite.</summary>
    public static bool IsOurs(string glbPath)
    {
        try { return File.Exists(glbPath) && GlbReader.Read(glbPath).Generator.Contains(GeneratorTag); }
        catch (Exception) { return false; }
    }

    /// <summary>Reads the .max and writes it as a .glb (through a temporary name, moved into place). A file that cannot be
    /// read throws InvalidDataException with the reason; the target is untouched then.</summary>
    public static MaxReport ConvertToGlb(string maxPath, string glbPath, MaxImageDecoder decoder) => ConvertToGlb(maxPath, glbPath, decoder, out _);

    /// <summary>As above; `unchanged` = the target already held exactly these bytes and was NOT written again. The
    /// conversion is deterministic, and the tools watch a source's write time (the Model Cutter drops its unsaved marks
    /// when the file under it changes): picking the same .max twice must not touch the file.</summary>
    public static MaxReport ConvertToGlb(string maxPath, string glbPath, MaxImageDecoder decoder, out bool unchanged)
        => ConvertToGlb(maxPath, glbPath, decoder, out unchanged, null);

    /// <summary>Ask before replacing an existing file with different bytes. Returns null when replacement is declined.
    /// The overloads without a confirmation callback are for callers that explicitly supply the output path.</summary>
    internal static MaxReport ConvertToGlb(string maxPath, string glbPath, MaxImageDecoder decoder, out bool unchanged, Func<bool> confirmOverwrite)
    {
        var model = Read(maxPath, decoder, out var report);
        unchanged = false;
        string full = Path.GetFullPath(glbPath);
        var target = new FileInfo(full);
        bool replace = target.Exists;
        byte[] bytes = null;
        if (replace)
        {
            bytes = GlbWriter.Write(model);
            byte[] previous = null;
            try { if (target.Length == bytes.LongLength) previous = File.ReadAllBytes(full); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            unchanged = previous != null && previous.Length == bytes.Length;
            for (int i = 0; unchanged && i < bytes.Length; i++)
                unchanged = previous[i] == bytes[i];
            if (unchanged) return report;
            if (confirmOverwrite != null && !confirmOverwrite()) return null;
        }

        Directory.CreateDirectory(target.DirectoryName);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                if (bytes == null) GlbWriter.Write(model, stream);
                else stream.Write(bytes, 0, bytes.Length);
            }
            if (replace) File.Replace(temporary, full, null);
            else File.Move(temporary, full);  // Refuses a destination that appeared while the GLB was being written.
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
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
        public bool Lost;                 // the material names a picture that cannot be shown (not found, not readable, not decodable): its faces keep their UVs
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
        var parts = new List<Part>(); var unmaterialled = new List<string>(); var modified = new List<string>();
        var misplaced = new Dictionary<(int above, string why), List<string>>(); var misplacedOrder = new List<(int above, string why)>();
        // For every node, once: the nearest node at or above it whose controller is not read (-1: none), and whether a
        // node at or above it stands where a GLB's 32-bit float cannot hold it. Parents first, without recursion: found
        // by a climb to the root for every part, 60,000 chained parts took 5.6 s (review round 3).
        int count = scene.Nodes.Count;
        var noted = new int[count]; var far = new bool[count]; var seen = new bool[count];
        for (int i = 0; i < count; i++)
        {
            if (seen[i]) continue;
            var chain = new List<int>();
            for (int at = i; at >= 0 && !seen[at]; at = scene.Nodes[at].Parent) { seen[at] = true; chain.Add(at); }
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                int at = chain[k]; var a = scene.Nodes[at]; int up = a.Parent;
                noted[at] = a.TransformNote != null ? at : up >= 0 ? noted[up] : -1;
                bool here = false;
                foreach (int ix in new[] { 3, 7, 11 }) if (double.IsNaN(a.World[ix]) || Math.Abs(a.World[ix]) > 1e30) here = true;
                far[at] = here || (up >= 0 && far[up]);
            }
        }
        long triangles = 0;
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
            double px = n.World[3], py = n.World[7], pz = n.World[11];
            // its own place, or that of a node above it (which the model holds too): one far-away helper must not cost
            // the whole file - the writer refuses a number that is not finite
            if (far[ni]) { rep.PartsLeft.Add(Label(n) + ": its place, or that of a node above it, does not fit a 32-bit float"); continue; }
            var m = n.Mesh;
            // every instance of a mesh is written out, so the triangles are counted before they are made: one mesh of a
            // million faces named by 200 nodes is 52 KB of file and 16 GB of model (review round 3)
            triangles += m.FaceCount;
            if (triangles > MaxTriangles) throw new InvalidDataException("the scene holds more than " + MaxTriangles.ToString("N0", CultureInfo.InvariantCulture) + " triangles (every instance of a mesh counted); this reader stops there");
            var p = new Part { Node = n, NodeIndex = ni, Sg = m.Smoothing, Tv = m.TVerts };
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
            // From here the part is WRITTEN: what is said about it below is said about a part of the model (a part left
            // out above is named once, in PartsLeft, and counted nowhere else).
            rep.ModifiersSkipped += n.Modifiers;
            if (n.Modifiers > 0) modified.Add("'" + n.Name + "'");
            if (m.UvLeft != null) rep.Note("part '" + n.Name + "' is read without UVs: " + m.UvLeft);
            // Its own controller, or the nearest node above it whose controller is not read: a part under a helper that
            // could not be placed is as misplaced as the helper. ONE reason per part, and the parts of one reason in one
            // note - a note per part and ancestor grew with the square of a chain's length (review round 2: 800 chained
            // parts gave 320,401 notes and took 340 s).
            if (noted[ni] >= 0)
            {
                int at = noted[ni];
                var key = (at == ni ? -1 : at, scene.Nodes[at].TransformNote);
                if (!misplaced.TryGetValue(key, out var names)) { misplaced[key] = names = new List<string>(); misplacedOrder.Add(key); }
                names.Add("'" + n.Name + "'");
                rep.PartsUnplaced++;
            }
            bool bare = false;
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
                        else if (!bare) { bare = true; unmaterialled.Add("'" + n.Name + "'"); }
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
        foreach (var key in misplacedOrder)
        {
            var names = misplaced[key];
            // straight into the list: two nodes of one name above two groups of parts give two notes of one beginning
            rep.Notes.Add(names.Count + " part(s) may be misplaced - " + (key.above < 0 ? key.why : "the node above, '" + scene.Nodes[key.above].Name + "': " + key.why) + ": " + Names(names));
        }
        if (rep.ModifiersSkipped > 0) rep.Note(rep.ModifiersSkipped + " modifier(s) sit on " + modified.Count + " part(s); a .max stores a modifier's settings, not its result, so those parts are read as they were BEFORE the modifier: " + Names(modified));
        if (rep.HiddenFaces > 0) rep.Note(rep.HiddenFaces + " face(s) are marked hidden in the file (3ds Max does not draw them): left out");
        if (unmaterialled.Count > 0) rep.Note(unmaterialled.Count + " part(s) have no material and are drawn in plain grey: " + Names(unmaterialled));
        if (scene.DanglingReferences > 0) rep.Note(scene.DanglingReferences + " reference(s) in the file name an object the scene does not have (a damaged file?): read as empty");

        // ---- pass 2: per material, where its picture is and how far the faces reach outside it
        var otherMaps = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in slots)
        {
            if (s.Mat == null) continue;
            if (!s.Mat.DiffuseRead) rep.Note("material '" + s.Mat.Name + "': its colour was not found where this reader looks (a newer material layout?): drawn in plain grey");
            if (s.Mat.MapLeft != null) rep.Note("material '" + s.Mat.Name + "': " + s.Mat.MapLeft);
            if (s.Mat.Opacity < 1) rep.Note("material '" + s.Mat.Name + "' is " + Math.Round(s.Mat.Opacity * 100).ToString(CultureInfo.InvariantCulture) + " % opaque in 3ds Max: written opaque");
            foreach (var (slot, what) in s.Mat.OtherMaps)
            {
                if (!otherMaps.TryGetValue(slot, out var l)) otherMaps[slot] = l = new List<string>();
                l.Add("'" + s.Mat.Name + "' (" + what + ")");
            }
            if (s.Mat.Map == null) continue;
            if (s.Mat.Map.PlacementLeft || (s.Mat.Mask != null && s.Mat.Mask.PlacementLeft)) rep.Note("material '" + s.Mat.Name + "': its map's offset, tiling or angle holds a number that cannot be used; the picture is placed without them");
            // said whether or not the picture is found: the offset and tiling are folded into the UVs either way
            if (s.Mat.Map.MirrorU || s.Mat.Map.MirrorV) rep.Note("material '" + s.Mat.Name + "': its map is mirrored in 3ds Max (every second repeat turned round); it is written as plain tiling");
            if (s.Mat.Map.Angle != 0) rep.Note("material '" + s.Mat.Name + "': its map is turned by an angle, which is not applied");
            if (s.Mat.Map.HasPlacement) rep.Note("material '" + s.Mat.Name + "': the map's offset/tiling (" + Num(s.Mat.Map.OffsetU) + ", " + Num(s.Mat.Map.OffsetV) + " / " + Num(s.Mat.Map.TilingU) + ", " + Num(s.Mat.Map.TilingV) + ") is folded into the UVs by the documented rule - check its placement");
            s.File = files.Find(s.Mat.Map.File);
            if (s.File == null) { rep.Missing(s.Mat.Map.File, s.Mat.Name); s.Lost = true; continue; }
            s.Textured = true;
            if (!files.IsBeside(s.File)) rep.Note("texture '" + s.Mat.Map.File + "' is not beside the .max; the one in '" + files.Relative(s.File) + "' is used");
        }
        // a map in any other slot is not read: said once per slot, with the materials that have one. An opacity map is
        // the one that changes the SHAPE the eye sees (it cuts holes), so it is said in those words.
        foreach (var kv in otherMaps)
            rep.Note((kv.Key == "opacity" ? "an OPACITY map (it cuts holes in 3ds Max; here the faces are written whole) is not read on " : "a " + kv.Key + " map is not read on ")
                + kv.Value.Count + " material(s): " + Names(kv.Value));
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
            if (s.Textured && s.MinU > s.MaxU)
            {
                // no face with UVs lies on it: nothing to embed, and that is said (the picture exists and is not shown)
                rep.Note("material '" + s.Mat.Name + "': its picture '" + s.Mat.Map.File + "' is not embedded - no face that uses the material has UVs on it");
                s.Textured = false;
            }
            if (!s.Textured) continue;
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
        var unusedUv = new List<string>();
        foreach (var p in parts)
        {
            var mesh = BuildMesh(p, slots, MaterialOf);
            model.Nodes[hafNode[p.NodeIndex]].Mesh = model.Meshes.Count;
            model.Meshes.Add(mesh);
            rep.Parts++; rep.Triangles += p.Slot.Length;
            if (mesh.Primitives.Any(q => q.Uv0 != null)) rep.PartsWithUv++;     // counted on what is written
            if (p.TFaces != null)
            {
                rep.PartsWithFileUv++;
                if (mesh.Primitives.Any(q => q.Uv0 == null)) unusedUv.Add("'" + p.Node.Name + "'");
            }
            double px = p.Node.World[3], py = p.Node.World[11], pz = -p.Node.World[7];
            for (int i = 0; i < p.Pos.Length; i += 3)
            {
                double a = p.Pos[i] + px, b = p.Pos[i + 1] + py, c = p.Pos[i + 2] + pz;
                if (a < lo[0]) lo[0] = a; if (a > hi[0]) hi[0] = a; if (b < lo[1]) lo[1] = b; if (b > hi[1]) hi[1] = b; if (c < lo[2]) lo[2] = c; if (c > hi[2]) hi[2] = c;
            }
        }
        rep.Size = new[] { hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2] };
        rep.Materials = model.Materials.Count; rep.Images = model.Images.Count;
        if (unusedUv.Count > 0) rep.Note(unusedUv.Count + " part(s) have UVs in the file on faces that show no picture (a material without a diffuse picture, faces beside a decal): those faces are written without UVs: " + Names(unusedUv));
        return model;
    }

    static string Label(MaxNode n) => "'" + n.Name + "'" + (n.BaseClass == "" ? "" : " (" + n.BaseClass + ")");
    /// <summary>A list of names for a note: ALL of them. A note is the same text in the dialog and in the Console; the
    /// dialog cuts the whole report at a length (and says so), the Console holds it whole, so nothing is named nowhere.</summary>
    static string Names(List<string> names) => string.Join(", ", names);
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
        catch (Exception e) { rep.Note("texture '" + map.File + "' could not be read: " + e.Message); s.Textured = false; s.Lost = true; return; }
        string kind = Kind(bytes, s.File);
        bool decal = !map.TileU || !map.TileV;
        string maskFile = mat.Mask == null ? null : files.Find(mat.Mask.File);
        if (mat.Mask != null && maskFile == null) { rep.Missing(mat.Mask.File, mat.Name); rep.Note("material '" + mat.Name + "': its mask picture is missing, the map is shown whole"); }
        if (maskFile != null && !files.IsBeside(maskFile)) rep.Note("texture '" + mat.Mask.File + "' is not beside the .max; the one in '" + files.Relative(maskFile) + "' is used");
        bool mix = Math.Abs(mat.Amount - 1) >= 0.005;       // under half a percent the mix is not visible in 8 bits, and the note would read "100 % instead of 100 %"
        // a PNG with an alpha channel is decoded too: where it is not opaque the material's colour shows (below)
        bool pngAlpha = kind == "png" && (PngCodec.ReadInfo(bytes)?.HasAlpha ?? false);
        bool needsPixels = decal || maskFile != null || mix || pngAlpha || (kind != "png" && kind != "jpeg");
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
                bool beside = decal && ((!map.TileU && (s.MinU < -1e-4 || s.MaxU > 1 + 1e-4)) || (!map.TileV && (s.MinV < -1e-4 || s.MaxV > 1 + 1e-4)));   // faces do reach beside the picture
                rep.Note("material '" + mat.Name + "': '" + map.File + "' could not be decoded (" + why + "), so it is embedded as it is" + (beside ? " and REPEATS where 3ds Max shows it once" : "") + (maskFile != null ? ", without its mask" : "") + (mix ? ", at 100 % instead of " + Math.Round(mat.Amount * 100).ToString(CultureInfo.InvariantCulture) + " %" : ""));
                string key = "raw|" + s.File.ToLowerInvariant();
                if (!imageOf.TryGetValue(key, out s.Image))
                {
                    s.Image = model.Images.Count; imageOf[key] = s.Image;
                    model.Images.Add(new HafImage { Name = Path.GetFileNameWithoutExtension(s.File), MimeType = kind == "png" ? "image/png" : "image/jpeg", Bytes = bytes });
                }
            }
            else { rep.Note("texture '" + map.File + "' could not be decoded (" + why + "): the plain colour is used"); s.Textured = false; s.Lost = true; }
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
        // The picture's own alpha works like a mask: where the picture is not opaque the material's colour shows. That
        // is what 3ds Max's default for a bitmap ("Image Alpha") does, and what the torpedo boat's hull says: its
        // picture is a render of the hull's side with everything around it transparent and BLACK, 64 % of the surface
        // that uses the material lands on those pixels, and the material's own colour is the superstructure's
        // (136, 154, 159) - a hull two thirds black is not what its artist drew (review round 2). Not checked against
        // 3ds Max itself, so it is said in the report with the share of the picture it concerns.
        long clear = 0;
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] != 255) clear++;
        if (clear > 0) rep.Note("material '" + mat.Name + "': '" + map.File + "' has an alpha channel (" + Math.Round(100.0 * clear / (rgba.Length / 4)).ToString(CultureInfo.InvariantCulture) + " % of it is not opaque); the material's colour shows through there, as with 3ds Max's 'Image Alpha'");
        if (clear == 0 && !decal && maskFile == null && !mix && kind == "png")
        {
            // a PNG whose alpha channel is opaque everywhere, with nothing else to compose: embedded as it is after all
            string key = "raw|" + s.File.ToLowerInvariant();
            if (!imageOf.TryGetValue(key, out s.Image))
            {
                s.Image = model.Images.Count; imageOf[key] = s.Image;
                model.Images.Add(new HafImage { Name = Path.GetFileNameWithoutExtension(s.File), MimeType = "image/png", Bytes = bytes });
            }
            return;
        }
        s.Ku = map.TileU ? 0 : PadFor(s.MinU, s.MaxU, w);
        s.Kv = map.TileV ? 0 : PadFor(s.MinV, s.MaxV, h);
        // each side is held within MaxCanvas by PadFor; the canvas as a whole within MaxCanvasPixels here - a picture
        // 65,535 wide padded in V only made a canvas of 65,535 x 7,168 and 3.4 GB (review round 3)
        while ((s.Ku > 0 || s.Kv > 0) && (long)w * (2 * s.Ku + 1) * h * (2 * s.Kv + 1) > MaxCanvasPixels)
        { if ((long)h * (2 * s.Kv + 1) >= (long)w * (2 * s.Ku + 1) && s.Kv > 0 || s.Ku == 0) s.Kv--; else s.Ku--; }
        if ((!map.TileU && Math.Max(-s.MinU, s.MaxU - 1) > s.Ku + 1e-4) || (!map.TileV && Math.Max(-s.MinV, s.MaxV - 1) > s.Kv + 1e-4))
            rep.Note("material '" + mat.Name + "': faces reach further beside the decal than its canvas can hold (" + s.Ku + " x " + s.Kv + " pictures to each side, the limit for a picture of " + w + " x " + h + "); their UVs are held at the canvas edge, which draws the decal LARGER on those faces than 3ds Max does");
        string ckey = string.Join("|", "c", s.File.ToLowerInvariant(), mask == null ? "" : maskFile.ToLowerInvariant(), mat.Amount.ToString("R", CultureInfo.InvariantCulture),
            mat.Diffuse[0].ToString("R", CultureInfo.InvariantCulture), mat.Diffuse[1].ToString("R", CultureInfo.InvariantCulture), mat.Diffuse[2].ToString("R", CultureInfo.InvariantCulture), s.Ku, s.Kv);
        if (imageOf.TryGetValue(ckey, out s.Image)) { s.Canvas = true; return; }
        var canvas = Compose(rgba, w, h, mask, mw, mh, mat.Amount, mat.Diffuse, s.Ku, s.Kv, out int cw, out int ch);
        s.Image = model.Images.Count; imageOf[ckey] = s.Image;
        model.Images.Add(new HafImage { Name = Path.GetFileNameWithoutExtension(s.File) + (mask != null || decal ? "_decal" : mix || clear > 0 ? "_mixed" : ""), MimeType = "image/png", Bytes = PngCodec.Encode(cw, ch, canvas, false) });
        s.Canvas = true;
    }

    static string Kind(byte[] b, string path)
    {
        if (PngCodec.IsPng(b)) return "png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpeg";
        if (b.Length > 2 && b[0] == (byte)'B' && b[1] == (byte)'M') return "bmp";
        // by its name only what has no signature of its own (a TGA). A file NAMED .png or .jpeg whose bytes are neither
        // is not embedded as one: an empty file and a web page's error text reached the tools as pictures (review round 3)
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext == "png" || ext == "jpeg" || ext == "jpg" ? "not a " + ext : ext;
    }

    /// <summary>The picture a material shows: the map, through its own alpha, its mask (white shows the map) and its
    /// amount, over the material's colour - set in the middle of a canvas of that colour `ku` / `kv` pictures wider to
    /// each side. The map's colour is taken as it is stored (not as multiplied by its alpha beforehand).
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
                double a = amount * (map[at + 3] / 255.0);
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
        // The sum for a (vertex, group combination) is worked out ONCE and kept, whatever becomes of it - also when it
        // cancels and every face then takes its own normal. Worked out again for every corner, 100,000 faces on one
        // vertex took 124 s from a 7.7 KB file (review round 3). And a vertex gets at most MaxSmoothingSums of them: a
        // real vertex has a handful of group combinations around it; past the bound the corner takes its face's normal.
        var normals = new List<double>(); var smoothKey = new Dictionary<long, int>(); var byValue = new Dictionary<(int, double, double, double), int>();
        var sums = new Dictionary<long, (double x, double y, double z, double terms)>(); var sumsAt = new int[nv];
        int CornerNormal(int f, int v)
        {
            uint sg = p.Sg[f];
            long key = sg != 0 ? ((long)v << 32) | sg : -1;
            if (sg != 0 && smoothKey.TryGetValue(key, out int have)) return have;
            double x = 0, y = 0, z = 0, terms = 0; bool flat = false;
            if (sg == 0) { x = fn[f * 3]; y = fn[f * 3 + 1]; z = fn[f * 3 + 2]; }
            else if (sums.TryGetValue(key, out var kept)) { x = kept.x; y = kept.y; z = kept.z; terms = kept.terms; }
            else if (sumsAt[v] >= MaxSmoothingSums) flat = true;
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
                    terms += Math.Sqrt(fn[g * 3] * fn[g * 3] + fn[g * 3 + 1] * fn[g * 3 + 1] + fn[g * 3 + 2] * fn[g * 3 + 2]);
                }
                sums[key] = (x, y, z, terms); sumsAt[v]++;
            }
            double len = Math.Sqrt(x * x + y * y + z * z);
            // The sum cancels (a sheet with both its sides in one smoothing group): each face then keeps its OWN
            // normal, and that normal is not shared through the vertex's key - shared, the back face got the front
            // face's normal. "Cancels" is measured against the size of what was summed: with more than one triangle a
            // side the sum is a rounding residue, not zero. In the four sample models every sum is either below 1e-9 of
            // its terms (eight corners, four of them exactly zero) or above 1e-2 (review rounds 1 and 2).
            bool own = sg != 0 && (flat || len <= 1e-6 * terms);
            if (own || len == 0) { x = fn[f * 3]; y = fn[f * 3 + 1]; z = fn[f * 3 + 2]; len = Math.Sqrt(x * x + y * y + z * z); }
            if (len == 0) { x = 0; y = 1; z = 0; len = 1; }
            x = x / len; y = y / len; z = z / len;
            if (!byValue.TryGetValue((v, x, y, z), out int id))
            {
                id = normals.Count / 3; byValue[(v, x, y, z)] = id;
                normals.Add(x); normals.Add(y); normals.Add(z);
            }
            if (sg != 0 && !own) smoothKey[key] = id;
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
            // UVs are written on the faces that SHOW a picture, and on the faces of a material whose picture cannot be
            // shown (not found, not readable): those keep them so the picture can be put on later. Not on the rest - a
            // material without a diffuse picture, faces beside a decal, no material. The first version dropped them
            // with a missing picture too (and counted them); round 1 of the review kept them everywhere, and round 3
            // showed what that costs: the Model Factory's converter takes ONE whole-tile shift from the UVs of the
            // whole model, and the stealth ship's UVs, 0.1..1.0 on its decals, became -102..127 with those of its
            // plain hull - which no picture uses. The map's own offset and tiling are folded in; the decal canvas's
            // remap only where that canvas is what the faces show.
            bool hasUv = p.TFaces != null && (textured || slot.Lost);
            MaxBitmap placed = slot.Mat == null ? null : slot.Mat.Map;
            var pos = new List<float>(); var nor = new List<float>(); var uv = hasUv ? new List<float>() : null;
            var index = new int[list.Count * 3];
            var seen = new Dictionary<(int v, int t, int n), int>();
            for (int i = 0; i < list.Count; i++)
            {
                int f = textured ? list[i] : ~list[i];
                for (int k = 0; k < 3; k++)
                {
                    int v = p.Faces[f * 3 + k], t = hasUv ? p.TFaces[f * 3 + k] : -1, n = CornerNormal(f, v);
                    if (!seen.TryGetValue((v, t, n), out int at))
                    {
                        at = pos.Count / 3; seen[(v, t, n)] = at;
                        pos.Add((float)p.Pos[v * 3]); pos.Add((float)p.Pos[v * 3 + 1]); pos.Add((float)p.Pos[v * 3 + 2]);
                        nor.Add((float)normals[n * 3]); nor.Add((float)normals[n * 3 + 1]); nor.Add((float)normals[n * 3 + 2]);
                        if (hasUv)
                        {
                            double pu = p.Tv[t * 3], pv = p.Tv[t * 3 + 1];
                            if (placed != null) Place(placed, pu, pv, out pu, out pv);
                            bool canvas = textured && slot.Canvas;
                            if (canvas && !placed.TileU) { pu = pu < -slot.Ku ? -slot.Ku : pu > slot.Ku + 1 ? slot.Ku + 1 : pu; pu = (pu + slot.Ku) / (2 * slot.Ku + 1); }
                            double down = 1 - pv;                 // a picture's rows run top-down, 3ds Max's V bottom-up
                            if (canvas && !placed.TileV) { down = down < -slot.Kv ? -slot.Kv : down > slot.Kv + 1 ? slot.Kv + 1 : down; down = (down + slot.Kv) / (2 * slot.Kv + 1); }
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
        readonly string home;
        /// <summary>True for a file in the .max's own folder (a picture taken from another folder is said in the report:
        /// two models' folders can hold different pictures of one name).</summary>
        public bool IsBeside(string file) => home != null && string.Equals(Path.GetDirectoryName(file), home, StringComparison.OrdinalIgnoreCase);
        /// <summary>The folder a found file is in, as seen from the .max: "sub" for one folder down, ".." for the folder above.</summary>
        public string Relative(string file)
        {
            string d = Path.GetDirectoryName(file);
            return home != null && string.Equals(d, Path.GetDirectoryName(home), StringComparison.OrdinalIgnoreCase) ? ".." : Path.GetFileName(d);
        }
        /// <summary>The files beside the .max, then one folder down (every folder directly under it, by name), then one
        /// folder up; the first of a name wins.</summary>
        public FileFinder(string dir)
        {
            if (dir == null || !Directory.Exists(dir)) return;
            home = dir;
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
    /// run-length packed. A 32-bit file's alpha is kept when its header declares alpha bits (MaxModel lets the
    /// material's colour show through it); an alpha that is zero everywhere counts as none.</summary>
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
        // before the pixel array is made: a packed file holds at least one packet (1 + a pixel) per 128 pixels, a plain
        // one every pixel - a 20-byte file that claims 8192 x 8192 asked for 256 MB first (review 2026-11-21)
        long least = rle ? ((long)w * h + 127) / 128 * (1 + bytes) : (long)w * h * bytes;
        if (b.Length - at < least) { why = rle ? "the TGA packets run past the file" : "the TGA pixels run past the file"; return false; }
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
        bool alphaBits = (desc & 15) != 0;        // the header says how many of a pixel's bits are alpha; none = the fourth byte means nothing
        void Colour(byte[] src, int s, int bits, int o)
        {
            if (bits == 8) { o4[o] = o4[o + 1] = o4[o + 2] = src[s]; }
            else if (bits == 15 || bits == 16)
            {
                int v = src[s] | src[s + 1] << 8;
                o4[o] = (byte)(((v >> 10) & 31) * 255 / 31); o4[o + 1] = (byte)(((v >> 5) & 31) * 255 / 31); o4[o + 2] = (byte)((v & 31) * 255 / 31);
            }
            else { o4[o] = src[s + 2]; o4[o + 1] = src[s + 1]; o4[o + 2] = src[s]; }
            o4[o + 3] = bits == 32 && alphaBits ? src[s + 3] : (byte)255;
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
        // an alpha channel that is zero everywhere was never written (a common 32-bit TGA): the picture is opaque
        bool any = false;
        for (int i = 3; i < o4.Length && !any; i += 4) any = o4[i] != 0;
        if (!any) for (int i = 3; i < o4.Length; i += 4) o4[i] = 255;
        rgba = o4;
        return true;
    }

    /// <summary>Windows BMP: 8-bit with a palette, 24-bit and 32-bit, not packed. The fourth byte of a 32-bit pixel is
    /// reserved in this header form, not alpha: every pixel is opaque.</summary>
    public static bool Bmp(byte[] b, out int w, out int h, out byte[] rgba, out string why)
    {
        w = h = 0; rgba = null; why = null;
        if (b.Length < 54) { why = "shorter than a BMP header"; return false; }
        int dataAt = BitConverter.ToInt32(b, 10), headSize = BitConverter.ToInt32(b, 14);
        if (headSize < 40 || headSize > b.Length - 14) { why = "a BMP header of " + headSize + " bytes"; return false; }
        w = BitConverter.ToInt32(b, 18); int rawH = BitConverter.ToInt32(b, 22);
        int bpp = BitConverter.ToUInt16(b, 28), comp = BitConverter.ToInt32(b, 30);
        bool top = rawH < 0; h = top ? -rawH : rawH;
        if (w <= 0 || h <= 0 || (long)w * h > MaxPixels) { why = "BMP size " + w + " x " + h; return false; }
        if (comp != 0 || (bpp != 8 && bpp != 24 && bpp != 32)) { why = "a BMP of " + bpp + " bits, packing " + comp; return false; }
        int palAt = 14 + headSize, colours = BitConverter.ToInt32(b, 46);
        if (bpp == 8 && colours == 0) colours = 256;
        long stride = ((long)w * bpp + 31) / 32 * 4;
        if (dataAt < palAt) { why = "the BMP pixels overlap the header"; return false; }
        if (dataAt + stride * h > b.Length) { why = "the BMP pixels run past the file"; return false; }
        if (bpp == 8 && (colours < 1 || colours > 256 || colours > (dataAt - palAt) / 4))
        { why = "the BMP palette has an invalid size or overlaps the pixels"; return false; }
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
