using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// The C# side of tools/fbx_model_drill.sh (step 6a of the native FBX reader): for every FBX file, FbxModel's HafModel against
// the GLB Blender wrote of the same file (fbx_to_glb.py: import, export), compared on what 6a models - the node tree (names,
// parents, children order, mesh references, the written translation / rotation / scale as bits), the meshes (names, the
// primitives' material NAMES, vertex counts, positions, normals, UV sets and indices as bits), the materials in order with
// every field (6b: the factors as bits, the texture references, the alpha mode, the extensions object structurally), the
// textures (image, sampler), the samplers (structurally), the images (name, MIME type, the bytes - or the decoded PIXELS of
// an image the exporter composes anew) and extensionsUsed. Exit 1 on any difference; a named fallback is a SKIP row. An
// oracle given as "-" means Blender's import failed: a SKIP when the reader declined the file too, else a DIFF.
static class FbxModelDrill
{
    static int Main(string[] args)
    {
        if (args.Length < 2 || args.Length % 2 != 0 || ((args[0] == "--dump" || args[0] == "--probe") && args.Length != 2))
        { Console.Error.WriteLine("usage: FbxModelDrill <file.fbx> <blender.glb> [...]  |  --dump <file.fbx>  |  --probe <file.fbx>"); return 2; }
        if (args[0] == "--dump")
        {
            var scene = FbxImport.Read(args[1]);
            var m = FbxModel.Build(scene, out string why);
            if (m == null) { Console.WriteLine("FALLBACK " + why); return 0; }
            Console.WriteLine(Describe(m));
            return 0;
        }
        if (args[0] == "--probe")
        {
            var scene = FbxImport.Read(args[1]);
            if (scene.Fallback != null) { Console.WriteLine("FALLBACK " + scene.Fallback); return 0; }
            string M4(float[] m) => string.Join(" ", m.Select(FBits));
            foreach (var ob in scene.Objects)
            {
                Console.WriteLine("OBJ '" + ob.Name + "' parent " + (ob.Parent != null ? "'" + ob.Parent.Name + "'" : "None"));
                Console.WriteLine("  loc " + string.Join(" ", ob.Loc.Select(FBits)) + " eul " + string.Join(" ", ob.Eul.Select(FBits)) + " size " + string.Join(" ", ob.Size.Select(FBits)));
                Console.WriteLine("  base " + M4(VehicleProbe.ToColumnMajor(ob.HelperBase))); Console.WriteLine("  geom " + M4(VehicleProbe.ToColumnMajor(ob.HelperGeom))); Console.WriteLine("  matrix " + M4(VehicleProbe.ToColumnMajor(ob.HelperMatrix))); Console.WriteLine("  lclrot " + M4(VehicleProbe.ToColumnMajor(ob.HelperLclRot)));
                Console.WriteLine("  basis " + M4(VehicleProbe.ToColumnMajor(ob.Basis)));
                Console.WriteLine("  world " + M4(ob.World));
            }
            Console.WriteLine("GLOBAL " + M4(VehicleProbe.ToColumnMajor(scene.GlobalMatrix)));
            return 0;
        }
        int failed = 0, compared = 0, skipped = 0;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            string fbx = args[i], glb = args[i + 1];
            try
            {
                var scene = FbxImport.Read(fbx);
                var mine = FbxModel.Build(scene, out string why, out var composed);
                if (mine == null) { Console.WriteLine("SKIP " + fbx + " : " + why); skipped++; continue; }
                if (glb == "-") { Console.WriteLine("DIFF " + fbx + " : Blender's import failed where the reader built a model"); failed++; continue; }
                var theirs = GlbReader.Read(glb);
                string d = Compare(mine, theirs, composed, ReadImageEvidence(glb, theirs));
                if (d == null) { Console.WriteLine("PASS " + fbx + " : " + mine.Nodes.Count + " nodes, " + mine.Meshes.Count + " meshes, " + mine.Materials.Count + " materials, " + mine.Textures.Count + " textures, " + mine.Images.Count + " images" + (composed.Count > 0 ? " (" + composed.Count + " composed: pixels compared)" : "") + (scene.Notes.Count > 0 ? " (notes: " + string.Join("; ", scene.Notes.Distinct().Take(6)) + ")" : "")); compared++; }
                else { Console.WriteLine("DIFF " + fbx + " : " + d); failed++; }
            }
            catch (Exception e) { Console.WriteLine("ERROR " + fbx + " : " + e.GetType().Name + ": " + e.Message); failed++; }
        }
        Console.WriteLine("RESULT compared=" + compared + " skipped=" + skipped + " failed=" + failed);
        return failed != 0 ? 1 : compared == 0 ? 3 : 0;
    }

    static string Bits(double d) => BitConverter.ToUInt64(BitConverter.GetBytes(d), 0).ToString("x16");
    static string FBits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");

    /// <summary>The first difference on the 6a and 6b fields, or null.</summary>
    static string Compare(HafModel a, HafModel b, HashSet<int> composed, int[] imageModes)
    {
        if (a.Nodes.Count != b.Nodes.Count) return $"nodes: {a.Nodes.Count} vs Blender's {b.Nodes.Count} ({Names(a.Nodes.Select(n => n.Name))} vs {Names(b.Nodes.Select(n => n.Name))})";
        if (a.Meshes.Count != b.Meshes.Count) return $"meshes: {a.Meshes.Count} vs Blender's {b.Meshes.Count}";
        if (a.Materials.Count != b.Materials.Count) return $"materials: {a.Materials.Count} vs Blender's {b.Materials.Count} ({Names(a.Materials.Select(m => m.Name))} vs {Names(b.Materials.Select(m => m.Name))})";
        for (int i = 0; i < a.Materials.Count; i++) if (a.Materials[i].Name != b.Materials[i].Name) return $"material {i}: '{a.Materials[i].Name}' vs '{b.Materials[i].Name}'";
        if (a.Scenes.Count != b.Scenes.Count || a.Scene != b.Scene) return "scenes";
        if (!a.Scenes[0].Nodes.SequenceEqual(b.Scenes[0].Nodes)) return $"scene roots: [{string.Join(",", a.Scenes[0].Nodes)}] vs [{string.Join(",", b.Scenes[0].Nodes)}]";
        for (int i = 0; i < a.Nodes.Count; i++)
        {
            var x = a.Nodes[i]; var y = b.Nodes[i];
            if (x.Name != y.Name) return $"node {i}: name '{x.Name}' vs '{y.Name}'";
            if (x.Parent != y.Parent) return $"node {i} '{x.Name}': parent {x.Parent} vs {y.Parent}";
            if (!x.Children.SequenceEqual(y.Children)) return $"node {i} '{x.Name}': children [{string.Join(",", x.Children)}] vs [{string.Join(",", y.Children)}]";
            if (x.Mesh != y.Mesh) return $"node {i} '{x.Name}': mesh {x.Mesh} vs {y.Mesh}";
            if (x.HasMatrix != y.HasMatrix) return $"node {i} '{x.Name}': matrix";
            string t = Trs(x.Translation, y.Translation, "translation", 3); if (t != null) return $"node {i} '{x.Name}': {t}";
            t = Trs(x.Rotation, y.Rotation, "rotation", 4); if (t != null) return $"node {i} '{x.Name}': {t}";
            t = Trs(x.Scale, y.Scale, "scale", 3); if (t != null) return $"node {i} '{x.Name}': {t}";
        }
        for (int i = 0; i < a.Meshes.Count; i++)
        {
            var x = a.Meshes[i]; var y = b.Meshes[i];
            if (x.Name != y.Name) return $"mesh {i}: name '{x.Name}' vs '{y.Name}'";
            if (x.Primitives.Count != y.Primitives.Count) return $"mesh {i} '{x.Name}': {x.Primitives.Count} primitives vs {y.Primitives.Count}";
            for (int p = 0; p < x.Primitives.Count; p++)
            {
                var u = x.Primitives[p]; var v = y.Primitives[p];
                string where = $"mesh {i} '{x.Name}' primitive {p}";
                string mu = u.Material >= 0 ? a.Materials[u.Material].Name : "", mv = v.Material >= 0 ? b.Materials[v.Material].Name : "";
                if (mu != mv) return $"{where}: material '{mu}' vs '{mv}'";
                if (u.VertexCount != v.VertexCount) return $"{where}: {u.VertexCount} vertices vs {v.VertexCount}";
                string d = Floats(u.Positions, v.Positions, "positions", 3); if (d != null) return where + ": " + d;
                d = Floats(u.Normals, v.Normals, "normals", 3); if (d != null) return where + ": " + d;
                d = Floats(u.Uv0, v.Uv0, "uv0", 2); if (d != null) return where + ": " + d;
                d = Floats(u.Uv1, v.Uv1, "uv1", 2); if (d != null) return where + ": " + d;
                int um = u.UvMore?.Count ?? 0, vm = v.UvMore?.Count ?? 0;
                if (um != vm) return $"{where}: {um + 2} uv sets vs {vm + 2}";
                for (int k = 0; k < um; k++) { d = Floats(u.UvMore[k], v.UvMore[k], "uv" + (k + 2), 2); if (d != null) return where + ": " + d; }
                if ((u.Indices == null) != (v.Indices == null)) return where + ": indexed vs not";
                if (u.Indices != null)
                {
                    if (u.Indices.Length != v.Indices.Length) return $"{where}: {u.Indices.Length} indices vs {v.Indices.Length}";
                    for (int k = 0; k < u.Indices.Length; k++) if (u.Indices[k] != v.Indices[k]) return $"{where}: index {k}: {u.Indices[k]} vs {v.Indices[k]}";
                }
            }
        }
        return CompareMaterials(a, b, composed, imageModes);
    }

    /// <summary>6b: every material field, the textures, the samplers, the images, extensionsUsed.</summary>
    static string CompareMaterials(HafModel a, HafModel b, HashSet<int> composed, int[] imageModes)
    {
        for (int i = 0; i < a.Materials.Count; i++)
        {
            var x = a.Materials[i]; var y = b.Materials[i];
            string where = $"material {i} '{x.Name}'";
            string t = Trs(x.BaseColorFactor, y.BaseColorFactor, "baseColorFactor", 4); if (t != null) return where + ": " + t;
            if (FBits(x.MetallicFactor) != FBits(y.MetallicFactor)) return $"{where}: metallicFactor {x.MetallicFactor} vs {y.MetallicFactor}";
            if (FBits(x.RoughnessFactor) != FBits(y.RoughnessFactor)) return $"{where}: roughnessFactor {x.RoughnessFactor} vs {y.RoughnessFactor}";
            if (x.BaseColorTexture != y.BaseColorTexture || x.BaseColorTexCoord != y.BaseColorTexCoord) return $"{where}: baseColorTexture {x.BaseColorTexture} vs {y.BaseColorTexture}";
            if (x.MetallicRoughnessTexture != y.MetallicRoughnessTexture || x.MetallicRoughnessTexCoord != y.MetallicRoughnessTexCoord) return $"{where}: metallicRoughnessTexture {x.MetallicRoughnessTexture} vs {y.MetallicRoughnessTexture}";
            if (x.NormalTexture != y.NormalTexture || x.NormalTexCoord != y.NormalTexCoord) return $"{where}: normalTexture {x.NormalTexture} vs {y.NormalTexture}";
            if (FBits(x.NormalScale) != FBits(y.NormalScale)) return $"{where}: normalTexture.scale {x.NormalScale} vs {y.NormalScale}";
            if (x.OcclusionTexture != y.OcclusionTexture) return $"{where}: occlusionTexture {x.OcclusionTexture} vs {y.OcclusionTexture}";
            if (x.EmissiveTexture != y.EmissiveTexture || x.EmissiveTexCoord != y.EmissiveTexCoord) return $"{where}: emissiveTexture {x.EmissiveTexture} vs {y.EmissiveTexture}";
            t = Trs(x.EmissiveFactorJson, y.EmissiveFactorJson, "emissiveFactor", 3); if (t != null) return where + ": " + t;
            if (x.AlphaMode != y.AlphaMode) return $"{where}: alphaMode {x.AlphaMode} vs {y.AlphaMode}";
            if (Bits(x.AlphaCutoff) != Bits(y.AlphaCutoff)) return $"{where}: alphaCutoff {x.AlphaCutoff} vs {y.AlphaCutoff}";
            if (x.DoubleSided != y.DoubleSided) return $"{where}: doubleSided {x.DoubleSided} vs {y.DoubleSided}";
            string e = Json(x.ExtensionsJson, y.ExtensionsJson); if (e != null) return $"{where}: extensions {e}";
            if ((x.ExtrasJson ?? "") != (y.ExtrasJson ?? "")) return $"{where}: extras";
        }
        if (a.Textures.Count != b.Textures.Count) return $"textures: {a.Textures.Count} vs Blender's {b.Textures.Count}";
        for (int i = 0; i < a.Textures.Count; i++)
        {
            var x = a.Textures[i]; var y = b.Textures[i];
            if (x.Source != y.Source || x.Sampler != y.Sampler || x.Name != y.Name) return $"texture {i}: source {x.Source} sampler {x.Sampler} '{x.Name}' vs source {y.Source} sampler {y.Sampler} '{y.Name}'";
        }
        if (a.Samplers.Count != b.Samplers.Count) return $"samplers: {a.Samplers.Count} vs Blender's {b.Samplers.Count}";
        for (int i = 0; i < a.Samplers.Count; i++) { string e = Json(a.Samplers[i], b.Samplers[i]); if (e != null) return $"sampler {i}: {e}"; }
        if (a.Images.Count != b.Images.Count) return $"images: {a.Images.Count} vs Blender's {b.Images.Count} ({Names(a.Images.Select(m => m.Name))} vs {Names(b.Images.Select(m => m.Name))})";
        for (int i = 0; i < a.Images.Count; i++)
        {
            var x = a.Images[i]; var y = b.Images[i];
            if (x.Name != y.Name) return $"image {i}: name '{x.Name}' vs '{y.Name}'";
            if (x.MimeType != y.MimeType) return $"image {i} '{x.Name}': mimeType {x.MimeType} vs {y.MimeType}";
            if (x.Bytes == null || y.Bytes == null) return $"image {i} '{x.Name}': bytes missing";
            if ((imageModes[i] & (composed.Contains(i) ? 2 : 1)) == 0) return $"image {i} '{x.Name}': composition decision differs from Blender's exporter";
            if (composed.Contains(i))
            {
                var px = PngCodec.Decode(x.Bytes, out var ix, out string wx); var py = PngCodec.Decode(y.Bytes, out var iy, out string wy);
                if (px == null) return $"image {i} '{x.Name}': the reader's PNG does not decode ({wx})";
                if (py == null) return $"image {i} '{x.Name}': Blender's PNG does not decode ({wy})";
                if (ix.Width != iy.Width || ix.Height != iy.Height) return $"image {i} '{x.Name}': {ix.Width}x{ix.Height} vs Blender's {iy.Width}x{iy.Height}";
                if (ix.HasAlpha != iy.HasAlpha || ix.ColorType != iy.ColorType) return $"image {i} '{x.Name}': colour type {ix.ColorType} vs Blender's {iy.ColorType}";
                for (int k = 0; k < px.Length; k++) if (px[k] != py[k]) return $"image {i} '{x.Name}': pixel {k / 4} channel {k % 4}: {px[k]} vs Blender's {py[k]} (composed, pixels compared)";
            }
            else
            {
                if (x.Bytes.Length != y.Bytes.Length) return $"image {i} '{x.Name}': {x.Bytes.Length} bytes vs Blender's {y.Bytes.Length}";
                for (int k = 0; k < x.Bytes.Length; k++) if (x.Bytes[k] != y.Bytes[k]) return $"image {i} '{x.Name}': byte {k} differs";
            }
        }
        if (!a.ExtensionsUsed.SequenceEqual(b.ExtensionsUsed)) return $"extensionsUsed [{string.Join(",", a.ExtensionsUsed)}] vs [{string.Join(",", b.ExtensionsUsed)}]";
        return null;
    }

    // The observer records the actual exporter path, bound to each written image. Missing/stale evidence cannot pass.
    static int[] ReadImageEvidence(string glb, HafModel model)
    {
        var doc = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(glb + ".images.json"));
        var rows = doc["images"] as Newtonsoft.Json.Linq.JArray;
        if ((int?)doc["version"] != 1 || rows == null || rows.Count != model.Images.Count) throw new InvalidDataException("image oracle evidence: version/count mismatch");
        var modes = new int[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var image = model.Images[i]; string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(image.Bytes)).Replace("-", "").ToLowerInvariant();
            if ((string)rows[i]["name"] != image.Name || (string)rows[i]["mimeType"] != image.MimeType || (string)rows[i]["sha256"] != hash)
                throw new InvalidDataException("image oracle evidence: image identity mismatch at " + i);
            var values = rows[i]["composed"] as Newtonsoft.Json.Linq.JArray;
            if (values == null || values.Count == 0 || values.Count > 2) throw new InvalidDataException("image oracle evidence: missing composition decision");
            foreach (var value in values)
            {
                if (value.Type != Newtonsoft.Json.Linq.JTokenType.Boolean) throw new InvalidDataException("image oracle evidence: invalid composition decision");
                int mode = (bool)value ? 2 : 1;
                if ((modes[i] & mode) != 0) throw new InvalidDataException("image oracle evidence: duplicate composition decision");
                modes[i] |= mode;
            }
        }
        return modes;
    }

    /// <summary>Two JSON texts compared structurally: members by name, numbers as double bits, null and absent alike.</summary>
    static string Json(string x, string y)
    {
        var jx = string.IsNullOrEmpty(x) ? null : Newtonsoft.Json.Linq.JToken.Parse(x); var jy = string.IsNullOrEmpty(y) ? null : Newtonsoft.Json.Linq.JToken.Parse(y);
        return JsonDiff(jx, jy, "");
    }

    static string JsonDiff(Newtonsoft.Json.Linq.JToken x, Newtonsoft.Json.Linq.JToken y, string path)
    {
        if (x == null && y == null) return null;
        if (x == null || y == null) return $"{path}: {(x == null ? "absent" : x.ToString(Newtonsoft.Json.Formatting.None))} vs Blender's {(y == null ? "absent" : y.ToString(Newtonsoft.Json.Formatting.None))}";
        if (x is Newtonsoft.Json.Linq.JObject ox && y is Newtonsoft.Json.Linq.JObject oy)
        {
            foreach (var k in ox.Properties().Select(p => p.Name).Union(oy.Properties().Select(p => p.Name)))
            { string d = JsonDiff(ox[k], oy[k], path + "/" + k); if (d != null) return d; }
            return null;
        }
        if (x is Newtonsoft.Json.Linq.JArray ax && y is Newtonsoft.Json.Linq.JArray ay)
        {
            if (ax.Count != ay.Count) return $"{path}: {ax.Count} items vs {ay.Count}";
            for (int i = 0; i < ax.Count; i++) { string d = JsonDiff(ax[i], ay[i], path + "[" + i + "]"); if (d != null) return d; }
            return null;
        }
        if (x is Newtonsoft.Json.Linq.JValue vx && y is Newtonsoft.Json.Linq.JValue vy)
        {
            if ((vx.Type == Newtonsoft.Json.Linq.JTokenType.Integer || vx.Type == Newtonsoft.Json.Linq.JTokenType.Float) && (vy.Type == Newtonsoft.Json.Linq.JTokenType.Integer || vy.Type == Newtonsoft.Json.Linq.JTokenType.Float))
                return Bits(Convert.ToDouble(vx.Value)) == Bits(Convert.ToDouble(vy.Value)) ? null : $"{path}: {vx} vs Blender's {vy}";
            return Equals(vx.Value, vy.Value) ? null : $"{path}: {vx} vs Blender's {vy}";
        }
        return $"{path}: {x.Type} vs Blender's {y.Type}";
    }

    static string Names(IEnumerable<string> n) => "[" + string.Join(",", n.Take(12)) + "]";

    static string Trs(double[] x, double[] y, string what, int n)
    {
        for (int k = 0; k < n; k++) if (Bits(x[k]) != Bits(y[k])) return $"{what}[{k}] {Bits(x[k])} ({x[k]}) vs {Bits(y[k])} ({y[k]})";
        return null;
    }

    static string Floats(float[] x, float[] y, string what, int per)
    {
        if ((x == null) != (y == null)) return what + (x == null ? " missing here" : " missing in Blender's");
        if (x == null) return null;
        if (x.Length != y.Length) return $"{what}: {x.Length / per} values vs {y.Length / per}";
        for (int k = 0; k < x.Length; k++) if (FBits(x[k]) != FBits(y[k])) return $"{what} {k / per}[{k % per}]: {FBits(x[k])} ({x[k]}) vs {FBits(y[k])} ({y[k]})";
        return null;
    }

    static string Describe(HafModel m)
    {
        var sb = new StringBuilder();
        sb.Append("nodes ").Append(m.Nodes.Count).Append(" meshes ").Append(m.Meshes.Count).Append(" materials ").Append(m.Materials.Count).Append('\n');
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            var n = m.Nodes[i];
            sb.Append("node ").Append(i).Append(" \"").Append(n.Name).Append("\" parent=").Append(n.Parent).Append(" mesh=").Append(n.Mesh).Append(" children=[").Append(string.Join(",", n.Children)).Append("] t=").Append(string.Join(",", n.Translation.Select(Bits)))
              .Append(" r=").Append(string.Join(",", n.Rotation.Select(Bits))).Append(" s=").Append(string.Join(",", n.Scale.Select(Bits))).Append('\n');
        }
        for (int i = 0; i < m.Meshes.Count; i++)
        {
            var me = m.Meshes[i];
            sb.Append("mesh ").Append(i).Append(" \"").Append(me.Name).Append("\" primitives=").Append(me.Primitives.Count);
            foreach (var p in me.Primitives) sb.Append(" [mat=").Append(p.Material).Append(" v=").Append(p.VertexCount).Append(" i=").Append(p.Indices?.Length ?? 0).Append(" uv=").Append((p.Uv0 != null ? 1 : 0) + (p.Uv1 != null ? 1 : 0) + (p.UvMore?.Count ?? 0)).Append(']');
            sb.Append('\n');
        }
        for (int i = 0; i < m.Materials.Count; i++)
        {
            var mt = m.Materials[i];
            sb.Append("material ").Append(i).Append(" \"").Append(mt.Name).Append("\" base=[").Append(string.Join(",", mt.BaseColorFactor)).Append("] metallic=").Append(mt.MetallicFactor).Append(" roughness=").Append(mt.RoughnessFactor)
              .Append(" alpha=").Append(mt.AlphaMode).Append(" tex base=").Append(mt.BaseColorTexture).Append(" mr=").Append(mt.MetallicRoughnessTexture).Append(" normal=").Append(mt.NormalTexture).Append('/').Append(mt.NormalScale)
              .Append(" emissive=").Append(mt.EmissiveTexture).Append(" [").Append(string.Join(",", mt.EmissiveFactorJson)).Append("] ext=").Append(mt.ExtensionsJson ?? "-").Append('\n');
        }
        for (int i = 0; i < m.Textures.Count; i++) sb.Append("texture ").Append(i).Append(" source=").Append(m.Textures[i].Source).Append(" sampler=").Append(m.Textures[i].Sampler).Append('\n');
        for (int i = 0; i < m.Samplers.Count; i++) sb.Append("sampler ").Append(i).Append(' ').Append(m.Samplers[i]).Append('\n');
        for (int i = 0; i < m.Images.Count; i++) sb.Append("image ").Append(i).Append(" \"").Append(m.Images[i].Name).Append("\" ").Append(m.Images[i].MimeType).Append(' ').Append(m.Images[i].Bytes?.Length ?? 0).Append(" bytes\n");
        sb.Append("extensionsUsed [").Append(string.Join(",", m.ExtensionsUsed)).Append("]\n");
        return sb.ToString();
    }
}
