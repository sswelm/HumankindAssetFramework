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
            // THE VALUES (review 2026-10-01: the counts could not tell a wrong matrix chain or a flipped winding). Everything is
            // computed in WORLD space through HafTransforms and expressed in Blender's frame (its importer turns glTF +Y up into
            // +Z up: x, y, z -> x, -z, y), order-independent so Blender's vertex merging cannot move it: the bounding box of every
            // vertex; the total triangle area; the area-weighted centroid; the area-weighted sum of face normals from each
            // triangle's own winding (a flipped winding or a mis-handled mirrored node shows here); the joint names; the durations.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var world = HafTransforms.WorldMatrices(m);
            double[] mn = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity }, mx = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            double area = 0; double[] cen = { 0, 0, 0 }, nsum = { 0, 0, 0 };
            long boxed = 0;
            for (int ni = 0; ni < m.Nodes.Count; ni++)
            {
                if (m.Nodes[ni].Mesh < 0) continue;
                foreach (var p in m.Meshes[m.Nodes[ni].Mesh].Primitives)
                {
                    // a skinned primitive is placed by its joints' bind-pose matrices, an unskinned one by its node (HafTransforms.WorldPositions)
                    var gl = HafTransforms.WorldPositions(m, ni, p, world);
                    var wv = new double[p.VertexCount * 3];
                    for (int v = 0; v < p.VertexCount; v++)
                    {
                        double bx = gl[v * 3], by = -gl[v * 3 + 2], bz = gl[v * 3 + 1];   // to Blender's Z-up
                        wv[v * 3] = bx; wv[v * 3 + 1] = by; wv[v * 3 + 2] = bz;
                        if (bx < mn[0]) mn[0] = bx; if (bx > mx[0]) mx[0] = bx;
                        if (by < mn[1]) mn[1] = by; if (by > mx[1]) mx[1] = by;
                        if (bz < mn[2]) mn[2] = bz; if (bz > mx[2]) mx[2] = bz;
                        boxed++;
                    }
                    if (p.Mode != 4) continue;
                    int count = p.Indices != null ? p.Indices.Length : p.VertexCount;
                    for (int t = 0; t + 2 < count; t += 3)
                    {
                        int ia = p.Indices != null ? p.Indices[t] : t, ib = p.Indices != null ? p.Indices[t + 1] : t + 1, ic = p.Indices != null ? p.Indices[t + 2] : t + 2;
                        double ax = wv[ia * 3], ay = wv[ia * 3 + 1], az = wv[ia * 3 + 2];
                        double ux = wv[ib * 3] - ax, uy = wv[ib * 3 + 1] - ay, uz = wv[ib * 3 + 2] - az;
                        double vx = wv[ic * 3] - ax, vy = wv[ic * 3 + 1] - ay, vz = wv[ic * 3 + 2] - az;
                        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;   // twice the area, along the face normal as wound
                        double ta = Math.Sqrt(nx * nx + ny * ny + nz * nz) * 0.5;
                        area += ta;
                        cen[0] += (ax + wv[ib * 3] + wv[ic * 3]) / 3.0 * ta; cen[1] += (ay + wv[ib * 3 + 1] + wv[ic * 3 + 1]) / 3.0 * ta; cen[2] += (az + wv[ib * 3 + 2] + wv[ic * 3 + 2]) / 3.0 * ta;
                        nsum[0] += nx * 0.5; nsum[1] += ny * 0.5; nsum[2] += nz * 0.5;
                    }
                }
            }
            if (area > 0) { cen[0] /= area; cen[1] /= area; cen[2] /= area; }
            string F(IEnumerable<double> xs) => string.Join(",", xs.Select(c => c.ToString("0.00000", inv)));
            string bbox = boxed == 0 ? "" : F(new[] { mn[0], mn[1], mn[2], mx[0], mx[1], mx[2] });
            string bboxIdentity = "";   // (the two skinned conventions were compared here on 2026-10-01; SkinSpace is Blender's, and stays)
            var bones = m.Skins.SelectMany(s => s.Joints).Distinct().Select(j => m.Nodes[j].Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var sortedDurations = m.Animations.Select(a => Math.Round(a.Duration, 3)).OrderBy(d => d).Select(d => d.ToString("0.000", inv));
            Console.WriteLine($"FILE\t{Key(path)}\ttris={m.TriangleCount}\tmaterials={m.Materials.Count}\timages={m.Images.Count}\tjoints={joints}\tanimations={m.Animations.Count}\tdurations={durations}\tnodes={m.Nodes.Count}\tmeshes={m.Meshes.Count}\tvertices={m.VertexCount}\tms={sw.Elapsed.TotalMilliseconds:0}\tbbox={bbox}\tbboxidentity={bboxIdentity}\tarea={area.ToString("0.00000", inv)}\tcentroid={F(cen)}\tnsum={F(nsum)}\tbones={string.Join("|", bones)}\tsorteddurations={string.Join(",", sortedDurations)}");
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
                        if (p.Weights1 != null) { float w1 = p.Weights1[v * 4 + k]; if (w1 > 0 && p.Joints1[v * 4 + k] >= jointCount) return $"node '{n.Name}' vertex {v} weighs joint {p.Joints1[v * 4 + k]} (set 1), the skin has {jointCount}"; sum += w1; }
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
