using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using SharpGLTF.Schema2;

// GLB/glTF -> OBJ converter with vertex-clustering decimation.
// usage: <glb> <outdir> [basename] [grid]
//   grid = cluster cells along the longest axis (higher = more detail/verts). Default 140.
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

        // ---- 1) collect all geometry in world space ----
        var V = new List<Vector3>(); var N = new List<Vector3>(); var U = new List<Vector2>();
        var Tri = new List<(int a, int b, int c)>();
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
                int b = V.Count;
                for (int i = 0; i < pos.Count; i++)
                {
                    V.Add(Vector3.Transform(pos[i], M));
                    N.Add(nrm != null ? SafeNorm(Vector3.TransformNormal(nrm[i], M)) : Vector3.UnitY);
                    U.Add(uv != null ? uv[i] : Vector2.Zero);
                }
                foreach (var t in p.GetTriangleIndices()) Tri.Add((t.A + b, t.B + b, t.C + b));
            }
        }
        Console.WriteLine($"collected: verts={V.Count} tris={Tri.Count}");

        // ---- 2) bounds + (optional) vertex-clustering decimation ----
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        foreach (var v in V) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
        Vector3 size = mx - mn;

        Vector3[] outV; Vector3[] outN; Vector2[] outU; List<(int, int, int)> outTri;
        if (grid <= 0)
        {
            // FAITHFUL mode: no merging -> every vertex and its EXACT uv survive (UV seams intact).
            // Use for low-poly textured models where clustering would average UVs across seams and scramble the skin.
            outV = V.ToArray(); outN = N.ToArray(); outU = U.ToArray();
            outTri = new List<(int, int, int)>(Tri);
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

        // ---- 3) write OBJ ----
        var sb = new StringBuilder();
        sb.AppendLine("# " + Path.GetFileName(glbPath) + "  decimated grid=" + grid);
        foreach (var v in outV) sb.AppendLine($"v {F(v.X)} {F(v.Y)} {F(v.Z)}");
        foreach (var t in outU) sb.AppendLine($"vt {F(t.X)} {F(t.Y)}");
        foreach (var n in outN) sb.AppendLine($"vn {F(n.X)} {F(n.Y)} {F(n.Z)}");
        sb.AppendLine("g " + baseName);
        foreach (var (a, b, c) in outTri)
        { int A = a + 1, B = b + 1, Cc = c + 1; sb.AppendLine($"f {A}/{A}/{A} {B}/{B}/{B} {Cc}/{Cc}/{Cc}"); }
        File.WriteAllText(Path.Combine(outDir, baseName + ".obj"), sb.ToString());

        // ---- textures (kept for models that have them; these CAD sketches have none) ----
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
        Console.WriteLine($"WROTE {baseName}.obj  ({outV.Length} verts, {outTri.Count} tris)  bbox=({F(size.X)},{F(size.Y)},{F(size.Z)})");
    }

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
