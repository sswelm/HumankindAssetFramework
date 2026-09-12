using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using SharpGLTF.Schema2;

// GLB/glTF -> OBJ converter with vertex-clustering decimation.
// usage: <glb> <outdir> [basename] [grid] [zup]
//   grid = cluster cells along the longest axis (higher = more detail/verts). Default 140.
//   zup  = accepted for caller compatibility and IGNORED: since the 2026-09-12 single-convention rework the
//          converter ALWAYS emits the baker's Z-up frame ((x,y,z) -> (x,-z,y), normals too — a +90° rotation
//          about X, det +1, so winding is preserved). The old raw pass-through was the per-entry "legacy"
//          convention whose Rotation hand-compensation made static and animated bakes disagree; the user's
//          2026-09-12 ruling: ONE convention, rebake everything that disagrees.
//
// SKINNED sources (2026-09-12, the TOW tripod finding): a Vehicle Lab GLB stores vertices in BIND space with
// the axis conversion AND the part assembly living on the JOINTS (the exporter parks -90°X on the root joint;
// the Lab's Flag bone carries the tripod's offset). Reading positions with only node.WorldMatrix scatters such
// a model (unassembled parts, pitched frame). For skinned primitives the vertices are therefore evaluated at
// the BIND POSE: v' = v * blend(IBM_j * World_j) — deterministic, no animation sampling, and for unskinned
// primitives nothing changes.
//
// MULTI-MATERIAL (faithful mode only): if the model uses >1 material, the OBJ is written with a `mtllib` + one
// `usemtl` group per material, and a sibling `.mtl` wires each material to an albedo (its extracted BaseColor image,
// or an 8x8 solid swatch from its baseColorFactor for flat-colour parts). The Unity importer then keeps per-material
// submeshes so the baker's atlas packer can skin every part. Single-material / decimated models are UNCHANGED.
class Program
{
    static CultureInfo C = CultureInfo.InvariantCulture;

    static int Main(string[] args)
    {
        // CLI hardening (2026-08-17 review): a usage error must exit NON-ZERO — the baker's caller treats
        // ExitCode==0 as success, so the old `return` reported a bad invocation as a good conversion. Same for a
        // non-numeric grid arg: name the problem instead of a raw FormatException ("Attack Helicopter" once made
        // args[3]="Helicopter" — now rejected upstream by name validation, but the tool must hold its own).
        if (args.Length < 2) { Console.Error.WriteLine("usage: <glb> <outdir> [basename] [grid]"); return 2; }
        string glbPath = args[0], outDir = args[1];
        string baseName = args.Length > 2 ? args[2] : "model";
        int grid = 140;
        if (args.Length > 3 && !int.TryParse(args[3], NumberStyles.Integer, C, out grid))
        { Console.Error.WriteLine($"ERROR: grid must be an integer, got '{args[3]}' (usage: <glb> <outdir> [basename] [grid] [zup])"); return 2; }
        if (args.Length > 4 && args[4] != "zup")
        { Console.Error.WriteLine($"ERROR: unknown 5th argument '{args[4]}' (only literal 'zup' is accepted)"); return 2; }
        // "zup" is accepted for old callers but no longer gates anything — the Z-up conversion is ALWAYS applied
        // (single-convention rework 2026-09-12; see the header note).
        Directory.CreateDirectory(outDir);

        var model = ModelRoot.Load(glbPath);
        Console.WriteLine($"loaded: meshes={model.LogicalMeshes.Count} materials={model.LogicalMaterials.Count} images={model.LogicalImages.Count}");

        // ---- 1) collect all geometry in world space (tracking each triangle's material index) ----
        var V = new List<Vector3>(); var N = new List<Vector3>(); var U = new List<Vector2>();
        var Tri = new List<(int a, int b, int c)>();
        var TriMat = new List<int>();       // material LogicalIndex per triangle (-1 = no material)
        int skinnedNodes = 0;
        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh == null) continue;
            var M = node.WorldMatrix;
            // SKINNED node: the vertices live in BIND space and the node's own transform is IGNORED at render time
            // (glTF spec) — the real placement comes from the joints. Evaluate the BIND POSE: per joint j the matrix
            // IBM_j * World_j (row-vector convention: IBM first, then the joint's world), blended by the vertex
            // weights. For a Vehicle Lab GLB this is what applies the exporter's root-joint -90°X AND the Flag/part
            // bone offsets — skipping it shipped an unassembled, pitched model (the TOW tripod, 2026-09-12).
            var skin = node.Skin;
            Matrix4x4[] jointMats = null;
            if (skin != null)
            {
                jointMats = new Matrix4x4[skin.JointsCount];
                for (int j = 0; j < skin.JointsCount; j++)
                {
                    var (jointNode, ibm) = skin.GetJoint(j);
                    jointMats[j] = ibm * jointNode.WorldMatrix;
                }
                skinnedNodes++;
            }
            // MIRRORED geometry (negative-determinant transform, e.g. a symmetric vehicle whose right half is the left
            // half under scale (-1,1,1)) flips the geometry but NOT the triangle index order, so that half winds inward
            // and renders inside-out — invisible under backface culling. Swap two indices per triangle to rewind it
            // outward. The judgement is PER VERTEX from the determinant of the transform that actually moved it — the
            // node matrix for plain meshes, the blended joint matrix for skinned ones (PR #35 review P3: a per-node/
            // first-joint guess mis-winds a mesh whose vertices weight to a different, mirrored joint) — and each
            // triangle rewinds by its first vertex's sign. (Normals still use TransformNormal; inverse-transpose is a
            // separate low.)
            bool nodeMirrored = M.GetDeterminant() < 0f;
            foreach (var p in node.Mesh.Primitives)
            {
                var pos = p.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (pos == null || pos.Count == 0) continue;
                var uv = p.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                var nrm = p.GetVertexAccessor("NORMAL")?.AsVector3Array();
                // ALL influence sets, not only set 0 (PR #35 review P2): a source with >4 influences per vertex
                // carries JOINTS_1/WEIGHTS_1 (and beyond) — reading set 0 alone drops real weight and deforms the
                // bind evaluation. Sets are gathered while BOTH accessors of a set exist.
                var jsets = new List<(IList<Vector4> j, IList<Vector4> w)>();
                if (jointMats != null)
                    for (int s = 0; ; s++)
                    {
                        var js = p.GetVertexAccessor("JOINTS_" + s)?.AsVector4Array();
                        var ws = p.GetVertexAccessor("WEIGHTS_" + s)?.AsVector4Array();
                        if (js == null || ws == null) break;
                        jsets.Add((js, ws));
                    }
                bool skinned = jsets.Count > 0;
                int mi = p.Material != null ? p.Material.LogicalIndex : -1;
                int b = V.Count;
                var flip = new bool[pos.Count];
                for (int i = 0; i < pos.Count; i++)
                {
                    Vector3 wv, wn; bool vFlip;
                    if (skinned)
                    {
                        var blend = default(Matrix4x4); float wsum = 0f;
                        foreach (var (js, ws) in jsets)
                        {
                            var jv = js[i]; var wt = ws[i];
                            for (int k = 0; k < 4; k++)
                            {
                                float w = k == 0 ? wt.X : k == 1 ? wt.Y : k == 2 ? wt.Z : wt.W;
                                if (w <= 0f) continue;
                                int ji = (int)(k == 0 ? jv.X : k == 1 ? jv.Y : k == 2 ? jv.Z : jv.W);
                                if (ji < 0 || ji >= jointMats.Length) continue;
                                blend += jointMats[ji] * w;
                                wsum += w;
                            }
                        }
                        if (wsum > 1e-6f)
                        {
                            blend *= 1f / wsum;
                            wv = Vector3.Transform(pos[i], blend);
                            wn = nrm != null ? SafeNorm(Vector3.TransformNormal(nrm[i], blend)) : Vector3.UnitY;
                            vFlip = blend.GetDeterminant() < 0f;
                        }
                        else
                        {
                            wv = Vector3.Transform(pos[i], M);
                            wn = nrm != null ? SafeNorm(Vector3.TransformNormal(nrm[i], M)) : Vector3.UnitY;
                            vFlip = nodeMirrored;
                        }
                    }
                    else
                    {
                        wv = Vector3.Transform(pos[i], M);
                        wn = nrm != null ? SafeNorm(Vector3.TransformNormal(nrm[i], M)) : Vector3.UnitY;
                        vFlip = nodeMirrored;
                    }
                    flip[i] = vFlip;
                    // glTF Y-up -> baker Z-up, (x,y,z) -> (x,-z,y). A pure +90° rotation about X (det +1), so
                    // triangle winding is untouched; normals take the identical rotation. ALWAYS applied — the
                    // single static convention, matching the animated path's Blender-converted frame.
                    wv = new Vector3(wv.X, -wv.Z, wv.Y);
                    wn = new Vector3(wn.X, -wn.Z, wn.Y);
                    V.Add(wv);
                    N.Add(wn);
                    U.Add(uv != null ? uv[i] : Vector2.Zero);
                }
                foreach (var t in p.GetTriangleIndices())
                {
                    Tri.Add(flip[t.A] ? (t.A + b, t.C + b, t.B + b) : (t.A + b, t.B + b, t.C + b));   // swap B/C to rewind mirrored geometry outward
                    TriMat.Add(mi);
                }
            }
        }
        Console.WriteLine($"collected: verts={V.Count} tris={Tri.Count}  axis: Y-up -> Z-up (always)"
                          + (skinnedNodes > 0 ? $"  skinned nodes evaluated at bind pose: {skinnedNodes}" : ""));

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
        // Tile shift: some models UV-map into a non-[0,1] tile (e.g. the whole Zeppelin envelope sits in V 1..2) and
        // rely on texture WRAP to repeat the skin. The atlas baker packs each texture into a fixed rect and cannot
        // wrap, so out-of-[0,1] UVs sample OUTSIDE the rect and the skin vanishes. Shift U and V by their integer
        // tile offset so a single-tile island lands back in [0,1]. Integer shift preserves triangle continuity
        // (unlike per-vertex frac(), which would tear any triangle that straddles a tile boundary).
        float uOff = 0f, vOff = 0f;
        {
            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue; bool any = false;
            foreach (var t in outU) { any = true; if (t.X < minU) minU = t.X; if (t.Y < minV) minV = t.Y; if (t.X > maxU) maxU = t.X; if (t.Y > maxV) maxV = t.Y; }
            if (any)
            {
                uOff = MathF.Floor(minU); vOff = MathF.Floor(minV);
                // MULTI-TILE / UDIM DETECTION: a single integer shift only normalizes a ONE-tile island. If the UVs
                // still reach past [0,1] after the shift, the model tiles across >1 UV tile (e.g. a .1001-.1005 UDIM
                // set) — the single-tile atlas cannot wrap that, so those UVs sample OUTSIDE the rect and part of the
                // skin VANISHES. This was silent; warn on stderr (the Factory surfaces glbconv stderr as a Unity
                // warning) so the modder knows to texture-transfer-bake onto single-tile (0-1) UVs in Blender.
                if (maxU - uOff > 1.001f || maxV - vOff > 1.001f)
                    Console.Error.WriteLine($"WARNING: UVs span more than one tile after normalization " +
                        $"(U {minU:0.###}..{maxU:0.###}, V {minV:0.###}..{maxV:0.###}). This model uses multi-tile / UDIM " +
                        "UVs the single-tile atlas cannot wrap — part of the skin will sample outside the texture and " +
                        "vanish. Fix: bake the texture onto single-tile (0-1) UVs in Blender before converting.");
            }
        }
        // Flip V: glTF/GLB store texture coords with V=0 at the TOP; OBJ (and Unity) use V=0 at the BOTTOM. Without
        // this, the skin maps upside-down in V and lands on the wrong faces in-engine (deck markings on the
        // superstructure). This was THE bug behind the Stealth Cruiser's scrambled texture.
        foreach (var t in outU) sb.AppendLine($"vt {F(t.X - uOff)} {F(1f - (t.Y - vOff))}");
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
        return 0;
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
