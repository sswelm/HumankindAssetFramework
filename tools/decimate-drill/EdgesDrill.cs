using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// BLENDER'S MESH LAYOUT IN C#, DRILLED AGAINST BLENDER'S (step 5 of replacing Blender, milestone a - the edge order the
// Decimate modifier's heap sees): every file given is read by GlbReader; for every mesh object Blender's importer would
// make (BlenderNames), BlenderMesh lays the mesh out as the importer and mesh_calc_edges do, and prints
//   MESH\t<key>\t<object name>\t<verts>\t<edges>\t<faces>\t<sha1 of the edge pairs>\t<the first 8 edges>
// for tools/decimate-drill/compare_edges.py to set beside blender_edges_many.py's rows. The hash holds the whole list;
// the first edges say where a mismatch starts. Milestone b adds BMeshDrill's BM and OP rows (the BMesh link lists).
static class EdgesDrill
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        int fails = 0; int threads = 0;   // exercise the APIs' runtime default; --threads still overrides it
        bool decimateOnly = false;
        Console.WriteLine($"RUNTIME	{(IntPtr.Size * 8)}-bit	trig {(BlenderTrig.Exact ? "exact" : "rounded")}	colour table {(BlenderColor.TableKnown ? "known" : "unknown")}");
        foreach (var path in args)
        {
            if (path.StartsWith("--threads=")) { threads = int.Parse(path.Substring(10)); continue; }
            if (path == "--decimate-only") { decimateOnly = true; continue; }   // the second, 32-bit Mono pass: DEC rows alone
            string key = path.Replace('\\', '/').ToLowerInvariant();
            try
            {
                var m = GlbReader.Read(path);
                var names = BlenderNames.Compute(m);
                foreach (var (node, name) in names.MeshObjectsInOrder)
                {
                    if (decimateOnly) break;
                    var bm = BlenderMesh.FromGltf(m, m.Nodes[node].Mesh, threads);
                    if (bm.VertexCount == 0) continue;   // mesh_objects(): len(vertices) > 0
                    string hash;
                    using (var sha = SHA1.Create())
                    {
                        var bytes = new byte[bm.ValidEdges.Length * 4]; Buffer.BlockCopy(bm.ValidEdges, 0, bytes, 0, bytes.Length);
                        hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    }
                    var first = Enumerable.Range(0, Math.Min(8, bm.ValidEdges.Length / 2)).Select(i => bm.ValidEdges[i * 2] + ":" + bm.ValidEdges[i * 2 + 1]);
                    Console.WriteLine($"MESH\t{key}\t{name}\t{bm.VertexCount}\t{bm.ValidEdges.Length / 2}\t{bm.Faces.Length / 3}\t{hash}\t{string.Join(" ", first)}");
                    BMeshDrill.Rows(bm, key, name);   // milestone b: the BMesh links, then scripted kills and splices
                }
                DecimateDrill.Rows(m, names, key, threads);   // milestone c: every mesh object collapsed at three ratios
                Console.WriteLine($"FILE\t{key}\tok");
            }
            catch (Exception e) { Console.WriteLine($"FAIL\t{key}\t{e.GetType().Name}: {e.Message}"); fails++; }
            GC.Collect();
        }
        return fails == 0 ? 0 : 1;
    }
}
