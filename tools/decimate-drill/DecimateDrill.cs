using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

// BLENDER'S DECIMATE COLLAPSE IN C#, DRILLED AGAINST BLENDER'S (step 5 milestone c): for every mesh object of more than 3
// faces (the modifier leaves smaller ones alone), at three ratios (one: the mesh as imported and merged by the apply; half:
// 0.5; third: prep_model's ratio for a target of a third of the file's triangles, all objects counted), BlenderReduce's
// result written down as blender_decimate_many.py writes the modifier's:
//   DEC\t<key>\t<object>\t<tag>\t<ratio bits>\t<verts>\t<faces>\t<sha1 positions>\t<sha1 faces>\t<sha1 UVs>\t<sha1 custom normals>
//      \t<sha1 face material+sharp>\t<sha1 vertex groups>\t<sha1 colour layers>\t<first 3 vertices>
// or, for a mesh the port keeps Blender for:  DEC\t<key>\t<object>\t<tag>\t<ratio bits>\tSKIP\t<why>
static class DecimateDrill
{
    public static void Rows(HafModel m, BlenderNames.Result names, string key, int threads)
    {
        var objects = new List<(int node, string name, int faces)>();   // the layouts are not kept: a fused hull's would hold the 32-bit drill's memory
        long total = 0;
        foreach (var (node, name) in names.MeshObjectsInOrder)
        {
            var layout = BlenderMesh.FromGltf(m, m.Nodes[node].Mesh, threads);
            if (layout.VertexCount == 0) continue;
            objects.Add((node, name, layout.Faces.Length / 3));
            total += layout.Faces.Length / 3;
        }
        float third = BlenderReduce.Ratio(total / 3, total);
        foreach (var (node, name, faces) in objects)
        {
            if (faces <= 3) continue;   // MOD_decimate: "requires more than 3 input faces"
            foreach (var (tag, ratio) in new[] { ("one", 1.0f), ("half", 0.5f), ("third", third) })
            {
                string bits = BitConverter.ToUInt32(BitConverter.GetBytes(ratio), 0).ToString("x8");
                var r = BlenderReduce.Reduce(m, node, ratio, names, threads);
                if (r.Fallback != null) { Console.WriteLine($"DEC\t{key}\t{name}\t{tag}\t{bits}\tSKIP\t{r.Fallback}"); continue; }
                Console.WriteLine($"DEC\t{key}\t{name}\t{tag}\t{bits}\t{r.VertexCount}\t{r.FaceCount}\t{Sha(Bytes(r.Positions))}\t{Sha(Bytes(r.Faces))}\t{UvHash(r)}\t{CnHash(r)}\t{FaceHash(r)}\t{GroupHash(r)}\t{ColorHash(r)}\t{First(r)}");
            }
        }
    }

    static byte[] Bytes(float[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
    static byte[] Bytes(int[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
    static byte[] Bytes(short[] a) { var b = new byte[a.Length * 2]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }

    static string Sha(byte[] b) { using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }

    static string UvHash(BlenderReduce.Result r)
    {
        if (r.Uv.Count == 0) return "none";
        using (var sha = SHA1.Create())
        {
            foreach (var uv in r.Uv) { var b = Bytes(uv); sha.TransformBlock(b, 0, b.Length, null, 0); }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
    }

    static string CnHash(BlenderReduce.Result r) => r.CustomNormal == null ? "none" : Sha(Bytes(r.CustomNormal));

    static string FaceHash(BlenderReduce.Result r)
    {
        var sb = new StringBuilder();
        for (int f = 0; f < r.FaceCount; f++) sb.Append(r.FaceMaterial[f]).Append(',').Append(r.FaceSharp[f] ? 1 : 0).Append('\n');
        return Sha(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    static string GroupHash(BlenderReduce.Result r)
    {
        if (r.DefNr == null) return "none";
        var sb = new StringBuilder();
        for (int v = 0; v < r.VertexCount; v++)
        {
            var nrs = r.DefNr[v]; var ws = r.DefWeight[v];
            for (int i = 0; nrs != null && i < nrs.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(nrs[i]).Append(':').Append(BitConverter.ToUInt32(BitConverter.GetBytes(ws[i]), 0).ToString("x8"));
            }
            sb.Append('\n');
        }
        return Sha(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>Every colour layer, sorted by name: name, domain, then its bytes.</summary>
    static string ColorHash(BlenderReduce.Result r)
    {
        if (r.Colors.Count == 0) return "none";
        var layers = new List<(string name, bool point, byte[] bytes)>(r.Colors);
        layers.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        using (var sha = SHA1.Create())
        {
            foreach (var (name, point, bytes) in layers)
            {
                var head = Encoding.UTF8.GetBytes(name + "\n" + (point ? "POINT" : "CORNER") + "\n");
                sha.TransformBlock(head, 0, head.Length, null, 0);
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
    }

    static string First(BlenderReduce.Result r)
    {
        var parts = new List<string>();
        for (int i = 0; i < Math.Min(3, r.VertexCount); i++)
            parts.Add(string.Join(",", new[] { Hex(r.Positions[3 * i]), Hex(r.Positions[3 * i + 1]), Hex(r.Positions[3 * i + 2]) }));
        return string.Join(" ", parts);
    }

    static string Hex(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");
}
