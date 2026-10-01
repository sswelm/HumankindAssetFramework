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
        Console.OutputEncoding = new UTF8Encoding(false);   // a file named with emoji (the Khronos Unicode sample) printed as "??" under the console's code page, and no longer keyed with Blender's line
        int fails = 0; long totalBytes = 0; double totalMs = 0;
        foreach (var path in args)
        {
            // the Lab's sources are the unreduced originals (one is 398 MB): collect before each file, or Mono's large-object
            // space fragments and fourteen later files fail with "Insufficient memory" (seen 2026-10-02; flaky between runs)
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
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
            // the pose both sides can state exactly: animation 0 at time 0 (Blender: that clip active, the NLA cleared, frame 0)
            var world = HafTransforms.WorldMatrices(m, HafTransforms.PoseAt(m, 0, 0.0));
            double[] mn = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity }, mx = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            double area = 0; double[] cen = { 0, 0, 0 }, nsum = { 0, 0, 0 };
            long boxed = 0, keptTris = 0;
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
                    // every triangle as drawn: TRIANGLES, and strips and fans unrolled with their winding (Blender imports those
                    // triangulated; the modes fixture has one of each); lines and points draw no face.
                    // AS BLENDER KEEPS THEM: a triangle that repeats a vertex is no face, and a second triangle over the same
                    // three vertices is dropped (a mesh holds one face per vertex set; the first stays) - a Workshop-fused Lab
                    // source, 2026-10-02: 6,003 of its 3.59 M drawn triangles, 0.2 % of the area, are such
                    var faces = new HashSet<(int, int, int)>();
                    foreach (var (ia, ib, ic) in p.Triangles())
                    {
                        if (ia == ib || ib == ic || ia == ic) continue;
                        int lo = Math.Min(ia, Math.Min(ib, ic)), hi = Math.Max(ia, Math.Max(ib, ic)), mid = ia + ib + ic - lo - hi;
                        if (!faces.Add((lo, mid, hi))) continue;
                        keptTris++;
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
            // triangles as DRAWN - per node instance, as Blender's scene has them (a mesh two nodes share counts twice; the model's
            // TriangleCount is the mesh total). Found by the two-target fixture; no registry file instances a mesh twice.
            long drawnTris = keptTris;   // counted in the loop above: the faces Blender keeps, per node instance
            string F(IEnumerable<double> xs) => string.Join(",", xs.Select(c => c.ToString("0.00000", inv)));
            string bbox = boxed == 0 ? "" : F(new[] { mn[0], mn[1], mn[2], mx[0], mx[1], mx[2] });
            string bboxIdentity = "";   // (kept in the line format; the skinned conventions were settled 2026-10-01: the weighted blend at animation 0, t = 0)
            // a joint with no name is "Node_<index>" in Blender (the Khronos SimpleSkin and BrainStem samples: nameless joints)
            var bones = m.Skins.SelectMany(s => s.Joints).Distinct().Select(j => m.Nodes[j].Name.Length > 0 ? m.Nodes[j].Name : "Node_" + j).OrderBy(n => n, StringComparer.Ordinal).ToList();
            // the materials Blender's importer ends up with: those a primitive uses (an unused one is never created), plus one it
            // invents PER MESH that has a COLOR_0 primitive without a material (measured 2026-10-02: two such primitives in one mesh
            // -> 1, in two meshes -> 2, one beside a materialed one -> 1 + 1; the Khronos BoxVertexColors and the normalized fixture)
            // ... and only for a mesh a NODE uses: a mesh nothing instances is never imported (the same Lab source carries
            // 2,084 of them beside the 23 its nodes use - 39 materials in the file, 22 in Blender)
            var usedMaterials = new HashSet<int>(); int invented = 0;
            var instanced = new HashSet<int>(m.Nodes.Where(n => n.Mesh >= 0).Select(n => n.Mesh));
            for (int mi = 0; mi < m.Meshes.Count; mi++)
            {
                if (!instanced.Contains(mi)) continue;
                var me = m.Meshes[mi];
                bool inventsOne = false;
                foreach (var pp in me.Primitives) { if (pp.Material >= 0) usedMaterials.Add(pp.Material); else if (pp.Colors != null) inventsOne = true; }
                if (inventsOne) invented++;
            }
            int blenderMaterials = usedMaterials.Count + invented;
            // the images Blender ends up with: those a texture of a CREATED material names, in its five core slots or anywhere
            // in its extension payload (a "...Texture": {"index": n} object)
            var usedTextures = new HashSet<int>();
            foreach (int mat in usedMaterials)
            {
                var hm = m.Materials[mat];
                foreach (int t in new[] { hm.BaseColorTexture, hm.MetallicRoughnessTexture, hm.NormalTexture, hm.OcclusionTexture, hm.EmissiveTexture }) if (t >= 0) usedTextures.Add(t);
                if (hm.ExtensionsJson != null) CollectTextures(GlbReader.ParseObject(hm.ExtensionsJson), usedTextures);
            }
            int blenderImages = usedTextures.Where(t => t < m.Textures.Count && m.Textures[t].Source >= 0).Select(t => m.Textures[t].Source).Distinct().Count();
            // what Blender 5.1 states about an animation: ONE action per glTF animation (slotted: every target a slot), its span
            // = the earliest first key to the latest last key over every channel (review of PR #109, round 5, measured on a
            // two-target fixture: channels ending at 1 s and 2 s, one starting at 0.5 s -> one action, 0..2 s). The same term
            // here: per animation, max last - min first over all its samplers. A channel starting after zero shortens it.
            var spans = new List<double>();
            foreach (var an in m.Animations)
            {
                double first = double.PositiveInfinity, last = double.NegativeInfinity;
                foreach (var ch in an.Channels)
                {
                    if (ch.Sampler < 0 || ch.Sampler >= an.Samplers.Count || an.Samplers[ch.Sampler].KeyCount == 0) continue;
                    var sp = an.Samplers[ch.Sampler];
                    first = Math.Min(first, sp.Times[0]); last = Math.Max(last, sp.Times[sp.KeyCount - 1]);
                }
                if (first <= last) spans.Add(last - first);
            }
            var sortedDurations = spans.Select(d => Math.Round(d, 3)).OrderBy(d => d).Select(d => d.ToString("0.000", inv));
            Console.WriteLine($"FILE\t{Key(path)}\ttris={drawnTris}\tmaterials={m.Materials.Count}\tblendermaterials={blenderMaterials}\tblenderimages={blenderImages}\timages={m.Images.Count}\tjoints={joints}\tanimations={m.Animations.Count}\tdurations={durations}\tnodes={m.Nodes.Count}\tmeshes={m.Meshes.Count}\tvertices={m.VertexCount}\tms={sw.Elapsed.TotalMilliseconds:0}\tbbox={bbox}\tbboxidentity={bboxIdentity}\tarea={area.ToString("0.00000", inv)}\tcentroid={F(cen)}\tnsum={F(nsum)}\tbones={string.Join("|", bones)}\tsorteddurations={string.Join(",", sortedDurations)}");
        }
        Console.WriteLine($"TOTAL\tfiles={args.Length}\tMB={totalBytes / 1e6:0.0}\tms={totalMs:0}");
        return fails == 0 ? 0 : 1;
    }

    // What the reader's structural checks do not cover: a skinned vertex's weights sum to about 1 and its joints index
    // the skin the node uses (the file's contract with the game's skinning, which reads garbage otherwise).
    /// <summary>Every texture index a payload names: any property ending in "Texture" (or named "texture") holding an object with an integer "index".</summary>
    static void CollectTextures(Newtonsoft.Json.Linq.JToken t, HashSet<int> into)
    {
        if (t is Newtonsoft.Json.Linq.JObject o)
            foreach (var prop in o.Properties())
            {
                if (prop.Name.EndsWith("Texture", StringComparison.OrdinalIgnoreCase) && prop.Value is Newtonsoft.Json.Linq.JObject info && info["index"] != null && info["index"].Type == Newtonsoft.Json.Linq.JTokenType.Integer) into.Add((int)info["index"]);
                CollectTextures(prop.Value, into);
            }
        else if (t is Newtonsoft.Json.Linq.JArray a) foreach (var e in a) CollectTextures(e, into);
    }

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
