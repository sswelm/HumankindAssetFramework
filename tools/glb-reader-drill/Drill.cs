using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

// THE GLB READER, DRILLED ON THE REAL REGISTRY (2026-09-30, step 1 of replacing Blender). Reads every .glb the
// registry names with the real GlbReader (editor/GlbReader.cs, compiled with Unity's Roslyn, run on Unity's Mono),
// checks what a file cannot say about itself (skinned vertices weigh to 1 and point at joints the skin has), and
// prints one line per file that tools/glb_reader_drill.sh compares with Blender's import of the same file:
//   FILE\t<name>\ttris=<n>\tmaterials=<n>\timages=<n>\tjoints=<n>\tanimations=<n>\tms=<read time>
static class Drill
{
    // the file, as both sides name it: full path, forward slashes, lower case (several sources share a parent folder)
    static string Key(string path) => Path.GetFullPath(path).Replace(Path.DirectorySeparatorChar, '/').ToLowerInvariant();

    static int Main(string[] args)
    {
        // args: the model files to read
        int fails = 0; long totalBytes = 0; double totalMs = 0;
        foreach (var path in args)
        {
            var sw = Stopwatch.StartNew();
            HafModel m;
            try { m = GlbReader.Read(path); }
            catch (Exception e) { Console.WriteLine($"FAIL\t{Key(path)}\t{e.Message}"); fails++; continue; }
            sw.Stop();
            totalBytes += new FileInfo(path).Length; totalMs += sw.Elapsed.TotalMilliseconds;
            string why = Consistency(m);
            if (why != null) { Console.WriteLine($"FAIL\t{Key(path)}\t{why}"); fails++; continue; }
            int joints = m.Skins.Sum(s => s.Joints.Length);
            string durations = string.Join(",", m.Animations.Select(a => a.Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
            Console.WriteLine($"FILE\t{Key(path)}\ttris={m.TriangleCount}\tmaterials={m.Materials.Count}\timages={m.Images.Count}\tjoints={joints}\tanimations={m.Animations.Count}\tdurations={durations}\tnodes={m.Nodes.Count}\tmeshes={m.Meshes.Count}\tvertices={m.VertexCount}\tms={sw.Elapsed.TotalMilliseconds:0}");
        }
        Console.WriteLine($"TOTAL\tfiles={args.Length}\tMB={totalBytes / 1e6:0.0}\tms={totalMs:0}");
        return fails == 0 ? 0 : 1;
    }

    // What the reader's structural checks do not cover: a skinned vertex's weights sum to about 1 and its joints index
    // the skin the node uses (the file's contract with the game's skinning, which reads garbage otherwise).
    static string Consistency(HafModel m)
    {
        foreach (var n in m.Nodes)
        {
            if (n.Mesh < 0) continue;
            var mesh = m.Meshes[n.Mesh];
            foreach (var p in mesh.Primitives)
            {
                if (!p.Skinned) continue;
                if (n.Skin < 0) return $"node '{n.Name}' has a skinned mesh but no skin";
                int jointCount = m.Skins[n.Skin].Joints.Length;
                for (int v = 0; v < p.VertexCount; v++)
                {
                    float sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = p.Weights[v * 4 + k];
                        if (w > 0 && p.Joints[v * 4 + k] >= jointCount) return $"node '{n.Name}' vertex {v} weighs joint {p.Joints[v * 4 + k]}, the skin has {jointCount}";
                        sum += w;
                    }
                    if (Math.Abs(sum - 1f) > 0.02f) return $"node '{n.Name}' vertex {v} weights sum to {sum:0.###}";
                }
            }
        }
        foreach (var a in m.Animations)
            foreach (var c in a.Channels)
                if (c.Node < 0) return $"animation '{a.Name}' has a channel without a target node";
        return null;
    }
}
