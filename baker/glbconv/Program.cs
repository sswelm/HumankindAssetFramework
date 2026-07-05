using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using SharpGLTF.Schema2;

// GLB/glTF -> OBJ converter with vertex-clustering decimation.
// usage: <glb> <outdir> [basename] [grid]
//   grid = cluster cells along the longest axis (higher = more detail/verts). Default 140.
//
// MULTI-MATERIAL (faithful mode only): if the model uses >1 material, the OBJ is written with a `mtllib` + one
// `usemtl` group per material, and a sibling `.mtl` wires each material to an albedo (its extracted BaseColor image,
// or an 8x8 solid swatch from its baseColorFactor for flat-colour parts). The Unity importer then keeps per-material
// submeshes so the baker's atlas packer can skin every part. Single-material / decimated models are UNCHANGED.
class Program
{
    static CultureInfo C = CultureInfo.InvariantCulture;

    static void Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: <glb> <outdir> [basename] [grid]"); return; }
        string glbPath = args[0], outDir = args[1];
        string baseName = args.Length > 2 ? args[2] : "model";
        int grid = args.Length > 3 ? int.Parse(args[3]) : 140;
        Directory.CreateDirectory(outDir);

        var model = ModelRoot.Load(glbPath);
        Console.WriteLine($"loaded: meshes={model.LogicalMeshes.Count} materials={model.LogicalMaterials.Count} images={model.LogicalImages.Count}");

        // ---- 1) collect all geometry in world space (tracking each triangle's material index) ----
        var V = new List<Vector3>(); var N = new List<Vector3>(); var U = new List<Vector2>();
        var Tri = new List<(int a, int b, int c)>();
        var TriMat = new List<int>();       // material LogicalIndex per triangle (-1 = no material)
        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh == null) continue;
            var M = node.WorldMatrix;
            foreach (var p in node.Mesh.Primitives)
            {
                var pos = p.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (pos == null || pos.Count == 0) continue;
                var uv = p.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                var nrm = p.GetVertexAccessor("NORMAL")?.AsVector3Array();
                int mi = p.Material != null ? p.Material.LogicalIndex : -1;
                int b = V.Count;
                for (int i = 0; i < pos.Count; i++)
                {
                    V.Add(Vector3.Transform(pos[i], M));
                    N.Add(nrm != null ? SafeNorm(Vector3.TransformNormal(nrm[i], M)) : Vector3.UnitY);
                    U.Add(uv != null ? uv[i] : Vector2.Zero);
                }
                foreach (var t in p.GetTriangleIndices()) { Tri.Add((t.A + b, t.B + b, t.C + b)); TriMat.Add(mi); }
            }
        }
        Console.WriteLine($"collected: verts={V.Count} tris={Tri.Count}");

        // ---- 2) bounds + (optional) vertex-clustering decimation ----
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        foreach (var v in V) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
        Vector3 size = mx - mn;

        Vector3[] outV; Vector3[] outN; Vector2[] outU; List<(int, int, int)> outTri;
        List<int> outTriMat = null;         // parallel to outTri; populated in faithful mode only (material grouping)
        bool faithful = grid <= 0;
        if (faithful)
        {
            // FAITHFUL mode: no merging -> every vertex and its EXACT uv survive (UV seams intact).
            // Use for low-poly textured models where clustering would average UVs across seams and scramble the skin.
            outV = V.ToArray(); outN = N.ToArray(); outU = U.ToArray();
            outTri = new List<(int, int, int)>(Tri);
            outTriMat = new List<int>(TriMat);
            Console.WriteLine($"faithful (grid<=0): verts={outV.Length} tris={outTri.Count}  (UVs preserved)");
        }
        else
        {
            float cell = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / Math.Max(1, grid);
            if (cell <= 0) cell = 1;
            var cellOf = new Dictionary<long, int>(V.Count);
            var remap = new int[V.Count];
            var sumP = new List<Vector3>(); var sumN = new List<Vector3>(); var sumU = new List<Vector2>(); var cnt = new List<int>();
            for (int i = 0; i < V.Count; i++)
            {
                long key = CellKey(V[i], mn, cell);
                if (!cellOf.TryGetValue(key, out int idx))
                {
                    idx = sumP.Count; cellOf[key] = idx;
                    sumP.Add(Vector3.Zero); sumN.Add(Vector3.Zero); sumU.Add(Vector2.Zero); cnt.Add(0);
                }
                remap[i] = idx; sumP[idx] += V[i]; sumN[idx] += N[i]; sumU[idx] += U[i]; cnt[idx]++;
            }
            int nv = sumP.Count;
            outV = new Vector3[nv]; outN = new Vector3[nv]; outU = new Vector2[nv];
            for (int i = 0; i < nv; i++) { outV[i] = sumP[i] / cnt[i]; outN[i] = SafeNorm(sumN[i]); outU[i] = sumU[i] / cnt[i]; }

            outTri = new List<(int, int, int)>(Tri.Count);
            var seen = new HashSet<long>();
            foreach (var (a, b, c) in Tri)
            {
                int ra = remap[a], rb = remap[b], rc = remap[c];
                if (ra == rb || rb == rc || ra == rc) continue;              // collapsed -> degenerate
                long tk = TriKey(ra, rb, rc);
                if (!seen.Add(tk)) continue;                                  // dedupe
                outTri.Add((ra, rb, rc));
            }
            Console.WriteLine($"decimated (grid={grid}): verts {V.Count}->{nv}  tris {Tri.Count}->{outTri.Count}");
        }

        // Group by material only when faithful AND the model actually uses more than one. Single-material and decimated
        // models take the original single-group, no-.mtl path (byte-identical output to before this feature).
        var usedMats = faithful ? new HashSet<int>(outTriMat).OrderBy(x => x).ToList() : new List<int>();
        bool groupByMat = faithful && usedMats.Count > 1;

        // ---- 3) write OBJ ----
        var sb = new StringBuilder();
        sb.AppendLine("# " + Path.GetFileName(glbPath) + "  decimated grid=" + grid);
        if (groupByMat) sb.AppendLine("mtllib " + baseName + ".mtl");
        foreach (var v in outV) sb.AppendLine($"v {F(v.X)} {F(v.Y)} {F(v.Z)}");
        // Flip V: glTF/GLB store texture coords with V=0 at the TOP; OBJ (and Unity) use V=0 at the BOTTOM. Without
        // this, the skin maps upside-down in V and lands on the wrong faces in-engine (deck markings on the
        // superstructure). This was THE bug behind the Stealth Cruiser's scrambled texture.
        foreach (var t in outU) sb.AppendLine($"vt {F(t.X)} {F(1f - t.Y)}");
        foreach (var n in outN) sb.AppendLine($"vn {F(n.X)} {F(n.Y)} {F(n.Z)}");
        sb.AppendLine("g " + baseName);
        if (groupByMat)
        {
            // Bucket triangle indices per material, then emit one `usemtl` block per material. Vertices/UVs are shared
            // and written once above; only the face list is partitioned, so nothing is duplicated.
            var byMat = new Dictionary<int, List<int>>();
            for (int ti = 0; ti < outTri.Count; ti++)
            {
                int mi = outTriMat[ti];
                if (!byMat.TryGetValue(mi, out var l)) { l = new List<int>(); byMat[mi] = l; }
                l.Add(ti);
            }
            foreach (int mi in usedMats)
            {
                sb.AppendLine("usemtl " + MatName(model, mi));
                foreach (int ti in byMat[mi])
                {
                    var (a, b, c) = outTri[ti]; int A = a + 1, B = b + 1, Cc = c + 1;
                    sb.AppendLine($"f {A}/{A}/{A} {B}/{B}/{B} {Cc}/{Cc}/{Cc}");
                }
            }
        }
        else
        {
            foreach (var (a, b, c) in outTri)
            { int A = a + 1, B = b + 1, Cc = c + 1; sb.AppendLine($"f {A}/{A}/{A} {B}/{B}/{B} {Cc}/{Cc}/{Cc}"); }
        }
        File.WriteAllText(Path.Combine(outDir, baseName + ".obj"), sb.ToString());

        // ---- 4) textures / materials ----
        if (groupByMat)
        {
            // Per-material albedo + a .mtl that references it. Every used material gets an image: its extracted BaseColor
            // texture, or an 8x8 solid swatch from its baseColorFactor (so flat-colour parts keep their colour).
            var mtl = new StringBuilder();
            foreach (int mi in usedMats)
            {
                string matNm = MatName(model, mi);
                string tex = WriteAlbedo(model, mi, baseName, outDir);
                mtl.AppendLine("newmtl " + matNm);
                mtl.AppendLine("Kd 1 1 1");
                mtl.AppendLine("map_Kd " + tex);
                Console.WriteLine($"  material {matNm} -> {tex}");
            }
            File.WriteAllText(Path.Combine(outDir, baseName + ".mtl"), mtl.ToString());
            Console.WriteLine($"WROTE {baseName}.mtl  ({usedMats.Count} materials)");
        }
        else
        {
            // Original single-albedo extraction (single-material / decimated models). Unchanged.
            foreach (var m in model.LogicalMaterials)
            {
                var primImg = m.FindChannel("BaseColor")?.Texture?.PrimaryImage;
                if (primImg != null && primImg.Content.IsValid)
                {
                    var img = primImg.Content;
                    string tex = $"{baseName}_{Sanitize(m.Name ?? "mat")}_albedo.{img.FileExtension}";
                    File.WriteAllBytes(Path.Combine(outDir, tex), img.Content.ToArray());
                    Console.WriteLine($"  texture -> {tex}");
                }
            }
        }
        Console.WriteLine($"WROTE {baseName}.obj  ({outV.Length} verts, {outTri.Count} tris)  bbox=({F(size.X)},{F(size.Y)},{F(size.Z)})");
    }

    // usemtl / newmtl name for a material index. Index-prefixed so duplicate material names stay unique.
    static string MatName(ModelRoot model, int mi)
        => mi >= 0 && mi < model.LogicalMaterials.Count
            ? $"mat{mi}_{Sanitize(model.LogicalMaterials[mi].Name ?? "mat")}"
            : "mat_none";

    // Write material mi's albedo to outDir and return the bare filename: its extracted BaseColor image if it has one,
    // else an 8x8 solid swatch from its baseColorFactor. Names carry the material index so they never collide.
    static string WriteAlbedo(ModelRoot model, int mi, string baseName, string outDir)
    {
        var m = mi >= 0 && mi < model.LogicalMaterials.Count ? model.LogicalMaterials[mi] : null;
        string mn = m != null ? Sanitize(m.Name ?? "mat") : "none";
        string tag = mi >= 0 ? "mat" + mi : "matnone";
        var primImg = m?.FindChannel("BaseColor")?.Texture?.PrimaryImage;
        if (primImg != null && primImg.Content.IsValid)
        {
            var img = primImg.Content;
            string tex = $"{baseName}_{tag}_{mn}_albedo.{img.FileExtension}";
            File.WriteAllBytes(Path.Combine(outDir, tex), img.Content.ToArray());
            return tex;
        }
        string sw = $"{baseName}_{tag}_{mn}_albedo.tga";
        WriteSolidTga(Path.Combine(outDir, sw), BaseColorFactor(m));
        return sw;
    }

    static Vector4 BaseColorFactor(Material m)
    {
        if (m != null)
        {
            var ch = m.FindChannel("BaseColor");
            if (ch.HasValue) { try { return ch.Value.Color; } catch { } }
        }
        return new Vector4(0.75f, 0.75f, 0.75f, 1f);
    }

    // Minimal uncompressed 32-bit TGA (BGRA, top-left origin) filled with one colour. Unity imports TGA natively.
    // The glTF baseColorFactor is LINEAR, so encode to sRGB for a correct on-screen colour.
    static void WriteSolidTga(string path, Vector4 c)
    {
        const int size = 8;
        byte R = ToByte(LinToSrgb(c.X)), G = ToByte(LinToSrgb(c.Y)), B = ToByte(LinToSrgb(c.Z)), A = ToByte(c.W);
        var h = new byte[18];
        h[2] = 2;                                            // uncompressed true-colour
        h[12] = (byte)(size & 0xFF); h[13] = (byte)((size >> 8) & 0xFF);
        h[14] = (byte)(size & 0xFF); h[15] = (byte)((size >> 8) & 0xFF);
        h[16] = 32;                                          // bits per pixel
        h[17] = 0x20;                                        // top-left origin
        using var fs = File.Create(path);
        fs.Write(h, 0, 18);
        var px = new byte[] { B, G, R, A };
        for (int i = 0; i < size * size; i++) fs.Write(px, 0, 4);
    }
    static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
    static float LinToSrgb(float c) { if (c <= 0f) return 0f; if (c >= 1f) return 1f; return c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f; }

    static long CellKey(Vector3 v, Vector3 mn, float cell)
    {
        long ix = (long)MathF.Floor((v.X - mn.X) / cell);
        long iy = (long)MathF.Floor((v.Y - mn.Y) / cell);
        long iz = (long)MathF.Floor((v.Z - mn.Z) / cell);
        return (ix & 0x1FFFFF) | ((iy & 0x1FFFFF) << 21) | ((iz & 0x1FFFFF) << 42);
    }
    static long TriKey(int a, int b, int c)
    {
        int x = Math.Min(a, Math.Min(b, c)), z = Math.Max(a, Math.Max(b, c)), y = a + b + c - x - z;
        return ((long)x) | ((long)y << 21) | ((long)z << 42);
    }
    static Vector3 SafeNorm(Vector3 v) { float l = v.Length(); return l > 1e-9f ? v / l : Vector3.UnitY; }
    static string F(float v) => v.ToString("0.######", C);
    static string Sanitize(string s) { var sb = new StringBuilder(); foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_'); return sb.ToString(); }
}
