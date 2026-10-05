using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

// BLENDER'S BMESH LINKS IN C#, DRILLED AGAINST BLENDER'S (step 5 milestone b): for a laid-out mesh (BlenderMesh) the BMesh
// is built as BM_mesh_bm_from_me builds it and every link list is written down - per vertex its edges in disk order and its
// corners in LOOPS_OF_VERT order, per edge its ends and its corners in radial order, per face its corners and vertices -
// hashed, as blender_edges_many.py writes the same lists from bmesh.from_mesh:
//   BM\t<key>\t<object>\t<verts>\t<edges>\t<loops>\t<faces>\t<sha1 of the lists>
// Then, on objects of at most 3,000 edges, a scripted sequence of kills and splices (a fixed generator seeded per object, the
// same on both sides: kill a vertex, or kill an edge and splice its second vertex into its first when Blender's Python allows
// it) with the lists hashed after every step, elements in iteration order (creation order, the dead skipped):
//   OP\t<key>\t<object>\t<step>\t<what>\t<verts>\t<edges>\t<faces>\t<sha1>
static class BMeshDrill
{
    public const int OpsMaxEdges = 3000;

    public static void Rows(BlenderMesh layout, string key, string name)
    {
        var bm = BMesh.FromMesh(layout, null);
        Console.WriteLine($"BM\t{key}\t{name}\t{bm.TotVert}\t{bm.TotEdge}\t{bm.TotLoop}\t{bm.TotFace}\t{Hash(bm)}");
        int E0 = bm.EdgeCount, V0 = bm.VertCount;
        if (E0 == 0 || E0 > OpsMaxEdges) return;
        long rng = 1 + E0;   // per object, so small objects do not all start with the same operation
        int Rnd() { rng = (rng * 1103515245L + 12345L) & 0x7fffffff; return (int)rng; }
        int steps = Math.Min(24, Math.Max(3, E0 / 4));
        for (int step = 0; step < steps; step++)
        {
            string what;
            if (Rnd() % 3 == 0)
            {
                int v = -1;
                for (int t = 0; t < 64; t++) { int c = Rnd() % V0; if (bm.VAlive[c]) { v = c; break; } }
                if (v < 0) what = "skip";
                else { what = $"kill-vert {v}"; bm.VertKill(v); }
            }
            else
            {
                int e = -1;
                for (int t = 0; t < 64; t++) { int c = Rnd() % E0; if (bm.EAlive[c]) { e = c; break; } }
                if (e < 0) what = "skip";
                else
                {
                    int v1 = bm.EV1[e], v2 = bm.EV2[e];
                    what = $"kill-edge {e}";
                    bm.EdgeKill(e);
                    if (bm.VAlive[v1] && bm.VAlive[v2] && v1 != v2 && bm.EdgeExists(v2, v1) == BMesh.None && !bm.VertPairShareFaceCheck(v2, v1))
                    {
                        bm.VertSplice(v1, v2);   // bmesh.utils.vert_splice(v2, v1) = BM_vert_splice(bm, v_dst = v1, v_src = v2)
                        what += $" splice {v1}<-{v2}";
                    }
                }
            }
            Console.WriteLine($"OP\t{key}\t{name}\t{step}\t{what}\t{bm.TotVert}\t{bm.TotEdge}\t{bm.TotFace}\t{Hash(bm)}");
        }
    }

    /// <summary>The link lists, one element per line, elements in iteration order, identified by their numbers - hashed
    /// line by line (a registry hull is millions of lines; the text is never held whole).</summary>
    public static string Hash(BMesh bm)
    {
        var sb = new StringBuilder(); var list = new List<int>();
        using (var sha = SHA1.Create())
        {
            void Flush()
            {
                if (sb.Length == 0) return;
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                sb.Clear();
            }
            for (int v = 0; v < bm.VertCount; v++)
            {
                if (!bm.VAlive[v]) continue;
                sb.Append('v').Append(v).Append(':');
                bm.EdgesOfVert(v, list); Join(sb, list); sb.Append('|');
                bm.LoopsOfVert(v, list); Join(sb, list); sb.Append('\n');
                if (sb.Length > 8192) Flush();
            }
            for (int e = 0; e < bm.EdgeCount; e++)
            {
                if (!bm.EAlive[e]) continue;
                sb.Append('e').Append(e).Append(':').Append(bm.EV1[e]).Append(' ').Append(bm.EV2[e]).Append('|');
                bm.LoopsOfEdge(e, list); Join(sb, list); sb.Append('\n');
                if (sb.Length > 8192) Flush();
            }
            for (int f = 0; f < bm.FaceCount; f++)
            {
                if (!bm.FAlive[f]) continue;
                sb.Append('f').Append(f).Append(':');
                bm.LoopsOfFace(f, list); Join(sb, list); sb.Append('|');
                for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(bm.LV[list[i]]); }
                sb.Append('\n');
                if (sb.Length > 8192) Flush();
            }
            Flush();
            sha.TransformFinalBlock(new byte[0], 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
    }

    static void Join(StringBuilder sb, List<int> list) { for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(list[i]); } }
}
