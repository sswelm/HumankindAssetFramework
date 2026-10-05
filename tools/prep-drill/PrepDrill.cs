using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// THE MODEL PREP IN C#, DRILLED AGAINST BLENDER'S (step 5 milestone d): blender_prep_many.py ran the real prep_model.py
// and wrote GLBs; this reads its PREP rows, reduces every mesh object of the SOURCE as prep_model does (BlenderReduce at
// prep's ratio), lays each out as the glTF exporter does (BlenderExport), and holds the result to the mesh Blender wrote
// for the node of the same name - per primitive: the vertex count, positions, normals, every UV set and the indices, bit
// for bit. One line per file and run:
//   PASS <key> <tag>: <n> objects, <p> primitives equal            FAIL <key> <tag>: <the first differences>
// and a TOTAL line. An object the port declines (BlenderReduce.FallbackReason) or does not lay out yet is counted, named.
static class PrepDrill
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine($"RUNTIME\t{(IntPtr.Size * 8)}-bit\ttrig {(BlenderTrig.Exact ? "exact" : "rounded")}\tcolour table {(BlenderColor.TableKnown ? "known" : "unknown")}");
        int fails = 0, runs = 0, objects = 0, prims = 0, declined = 0, expectedFailures = 0; long verts = 0;
        var declinedWhy = new Dictionary<string, int>();
        var cover = new SortedDictionary<string, long>();
        foreach (var k in CoverKeys) cover[k] = 0;
        HafModel source = null; string sourcePath = null; BlenderNames.Result names = null;
        foreach (var line in File.ReadAllLines(args[0]))
        {
            var t = line.Split('\t');
            if (t.Length >= 6 && t[0] == "PREPFAIL")
            {
                // prep_model.py itself failed. Known: a file of several scenes ("Object ... is not in View Layer") - the
                // Factory's bake of such a file fails today; anything else is a finding
                runs++;
                string sk = string.Join("/", t[1].Split('/').Reverse().Take(2).Reverse());
                try
                {
                    var failedModel = GlbReader.Read(t[1]);
                    if (failedModel.Scenes.Count > 1 && t[5].Contains("View Layer")) { expectedFailures++; Console.WriteLine($"PASS {sk} {t[2]}: prep_model.py fails on a file of {failedModel.Scenes.Count} scenes, as known ({t[5]})"); }
                    else { fails++; Console.WriteLine($"FAIL {sk} {t[2]}: prep_model.py failed for a reason this drill does not know: {t[5]}"); }
                }
                catch (Exception e) { fails++; Console.WriteLine($"FAIL {sk} {t[2]}: {e.GetType().Name}: {e.Message}"); }
                continue;
            }
            if (t.Length < 6 || t[0] != "PREP") continue;
            string key = t[1], tag = t[2]; long total = long.Parse(t[3]), target = long.Parse(t[4]); string outGlb = t[5];
            string shortKey = string.Join("/", key.Split('/').Reverse().Take(2).Reverse());
            runs++;
            try
            {
                if (sourcePath != key) { source = GlbReader.Read(key); sourcePath = key; names = BlenderNames.Compute(source); GC.Collect(); }   // the key is the path, lower-cased
                var m = source;
                var meshObjects = new List<(int node, string name, int faces)>();
                long myTotal = 0;
                foreach (var (node, name) in names.MeshObjectsInOrder)
                {
                    var layout = BlenderMesh.FromGltf(m, m.Nodes[node].Mesh);
                    if (layout.VertexCount == 0) continue;
                    meshObjects.Add((node, name, layout.Faces.Length / 3)); myTotal += layout.Faces.Length / 3;
                }
                var problems = new List<string>();
                if (myTotal != total) problems.Add($"triangle total C# {myTotal} vs Blender {total}");
                float ratio = BlenderReduce.Ratio(target, myTotal);
                var written = GlbReader.Read(outGlb);
                var nodeByName = new Dictionary<string, int>();
                for (int i = 0; i < written.Nodes.Count; i++) if (written.Nodes[i].Mesh >= 0 && !nodeByName.ContainsKey(written.Nodes[i].Name)) nodeByName[written.Nodes[i].Name] = i;
                int okObjects = 0, okPrims = 0;
                foreach (var (node, name, faces) in meshObjects)
                {
                    bool skinned = m.Nodes[node].Skin >= 0 && m.Nodes[node].Skin < m.Skins.Count && m.Meshes[m.Nodes[node].Mesh].Primitives.Exists(p => p.Skinned);
                    string why = BlenderReduce.FallbackReason(m, node);
                    if (why == null && skinned) why = "skinned (not laid out yet)";
                    if (why != null) { declined++; declinedWhy[why] = declinedWhy.TryGetValue(why, out int c) ? c + 1 : 1; continue; }
                    var r = BlenderReduce.Reduce(m, node, ratio, names);
                    why = BlenderExport.NotLaidOut(r, m);
                    if (why != null) { declined++; declinedWhy[why] = declinedWhy.TryGetValue(why, out int c2) ? c2 + 1 : 1; continue; }
                    var mine = BlenderExport.MeshPrimitives(r);
                    Cover(r, BlenderExport.Validated(r), mine, cover);
                    if (mine.Count == 0)
                    {
                        // an object without faces (lines, points): the exporter writes its node WITHOUT a mesh
                        if (nodeByName.ContainsKey(name)) problems.Add($"{name}: no faces, yet Blender's file has a mesh node of that name"); else okObjects++;
                        continue;
                    }
                    if (!nodeByName.TryGetValue(name, out int wn)) { problems.Add($"{name}: no mesh node of that name in Blender's file"); continue; }
                    var theirs = written.Meshes[written.Nodes[wn].Mesh].Primitives.Where(p => p.Mode == 4).ToList();
                    if (theirs.Count != mine.Count) { problems.Add($"{name}: {mine.Count} primitives vs Blender's {theirs.Count}"); continue; }
                    bool objectOk = true;
                    for (int i = 0; i < mine.Count && objectOk; i++)
                    {
                        string d = Differ(mine[i], theirs[i]);
                        if (d != null) { problems.Add($"{name} primitive {i}: {d}"); objectOk = false; }
                        else { okPrims++; verts += mine[i].VertexCount; }
                    }
                    if (objectOk) okObjects++;
                }
                objects += okObjects; prims += okPrims;
                if (problems.Count > 0) { fails++; Console.WriteLine($"FAIL {shortKey} {tag}: " + string.Join("; ", problems.Take(4)) + (problems.Count > 4 ? $"; ... {problems.Count - 4} more" : "")); }
                else Console.WriteLine($"PASS {shortKey} {tag}: {okObjects} objects, {okPrims} primitives equal (ratio {ratio:R})");
            }
            catch (Exception e) { fails++; Console.WriteLine($"FAIL {shortKey} {tag}: {e.GetType().Name}: {e.Message}"); }
        }
        foreach (var kv in declinedWhy) Console.WriteLine($"NOTE {kv.Value} object runs not compared: {kv.Key}");
        // a rule no compared object exercised was not held to Blender by this run: the script fails on a zero it expects filled
        foreach (var kv in cover) Console.WriteLine($"COVER {kv.Value} {kv.Key}");
        Console.WriteLine($"TOTAL runs {runs} failed {fails} objects {objects} primitives {prims} vertices {verts} declined {declined} prepfails {expectedFailures}");
        return fails == 0 ? 0 : 1;
    }

    static readonly string[] CoverKeys =
    {
        "normals from the file (custom normals)", "no normals (every face flat)", "a normal that rounds to zero, made up", "a zero normal on a fan that does not point up", "an object of several primitives",
        "an object without faces", "two or more UV sets", "COLOR_0 as RGB (the material's colour)", "COLOR_0 with alpha (a face without material)",
        "COLOR_0 forced (255s)", "two or more colour sets", "a colour layer on the vertices (point domain)", "a twin face the exporter's validate removes",
    };

    /// <summary>Which of the layout's rules this object exercised (counted per object run).</summary>
    static void Cover(BlenderReduce.Result reduced, BlenderReduce.Result r, List<BlenderExport.Primitive> mine, SortedDictionary<string, long> cover)
    {
        void Hit(string k, bool yes) { if (yes) cover[k]++; }
        Hit("a twin face the exporter's validate removes", r.Faces.Length < reduced.Faces.Length);
        Hit("an object without faces", mine.Count == 0);
        if (mine.Count == 0) return;
        Hit("normals from the file (custom normals)", r.CustomNormal != null);
        Hit("no normals (every face flat)", r.CustomNormal == null && r.FaceSharp.All(s => s));
        Hit("an object of several primitives", mine.Count > 1);
        Hit("two or more UV sets", r.Uv.Count > 1);
        var sets = mine[0].Colors;
        Hit("COLOR_0 as RGB (the material's colour)", sets.Count > 0 && !sets[0].Alpha);
        Hit("COLOR_0 with alpha (a face without material)", sets.Count > 0 && sets[0].Alpha && !sets[0].Forced);
        Hit("COLOR_0 forced (255s)", sets.Count > 0 && sets[0].Forced);
        Hit("two or more colour sets", sets.Count > 1);
        Hit("a colour layer on the vertices (point domain)", r.Colors.Exists(c => c.point));
        // the corner normals once more, before the exporter's rounding: does any round to the zero vector
        int nc = r.Faces.Length; short[] d0 = null, d1 = null;
        if (r.CustomNormal != null) { d0 = new short[nc]; d1 = new short[nc]; for (int c = 0; c < nc; c++) { d0[c] = r.CustomNormal[2 * c]; d1[c] = r.CustomNormal[2 * c + 1]; } }
        var cn = VehicleProbe.BlenderCornerNormals(r.Positions, r.Faces, r.FaceSharp, d0, d1);
        // ... and is one of those on a fan whose OWN normal is not +Z: only there does "the exporter makes a zero up" differ
        // from "an invalid space keeps the fan's normal" (a triangle of no area has the normal +Z, so it cannot tell)
        bool zero = false, zeroAway = false; float[] fan = null;
        for (int c = 0; c < nc && !zeroAway; c++)
        {
            if (!(BlenderExport.Round4(cn[3 * c]) == 0f && BlenderExport.Round4(cn[3 * c + 1]) == 0f && BlenderExport.Round4(cn[3 * c + 2]) == 0f)) continue;
            zero = true;
            if (fan == null) fan = VehicleProbe.BlenderFanNormals(r.Positions, r.Faces, r.FaceSharp);
            zeroAway = !(BlenderExport.Round4(fan[3 * c]) == 0f && BlenderExport.Round4(fan[3 * c + 1]) == 0f && BlenderExport.Round4(fan[3 * c + 2]) == 1f)
                    && !(BlenderExport.Round4(fan[3 * c]) == 0f && BlenderExport.Round4(fan[3 * c + 1]) == 0f && BlenderExport.Round4(fan[3 * c + 2]) == 0f);
        }
        Hit("a normal that rounds to zero, made up", zero);
        Hit("a zero normal on a fan that does not point up", zeroAway);
    }

    static string Differ(BlenderExport.Primitive a, HafPrimitive b)
    {
        if (a.VertexCount != b.VertexCount) return $"{a.VertexCount} vertices vs Blender's {b.VertexCount}";
        string d;
        if ((d = Arr("positions", a.Positions, b.Positions, 3)) != null) return d;
        if ((d = Arr("normals", a.Normals, b.Normals, 3)) != null) return d;
        var uvs = new List<float[]>(); if (b.Uv0 != null) uvs.Add(b.Uv0); if (b.Uv1 != null) uvs.Add(b.Uv1); if (b.UvMore != null) uvs.AddRange(b.UvMore);
        if (uvs.Count != a.Uv.Count) return $"{a.Uv.Count} UV sets vs Blender's {uvs.Count}";
        for (int u = 0; u < uvs.Count; u++) if ((d = Arr("UV set " + u, a.Uv[u], uvs[u], 2)) != null) return d;
        // the colour sets as the reader decodes Blender's: RGB floats padded with alpha 1; a set with alpha arrives as
        // normalized shorts (short / 65535), the forced set as bytes of 255 (all ones)
        var theirColors = new List<float[]>(); if (b.Colors != null) theirColors.Add(b.Colors); if (b.ColorMore != null) theirColors.AddRange(b.ColorMore);
        if (theirColors.Count != a.Colors.Count) return $"{a.Colors.Count} colour sets vs Blender's {theirColors.Count}";
        for (int k = 0; k < a.Colors.Count; k++)
        {
            var set = a.Colors[k]; var expect = new float[4 * a.VertexCount];
            for (int i = 0; i < a.VertexCount; i++)
                for (int j = 0; j < 4; j++)
                    expect[4 * i + j] = set.Forced ? 1f : set.Alpha ? (float)(set.Shorts[4 * i + j] / 65535.0) : j < 3 ? set.Data[3 * i + j] : 1f;
            if ((d = Arr("colour set " + k, expect, theirColors[k], 4)) != null) return d;
        }
        int[] bi = b.Indices ?? Enumerable.Range(0, b.VertexCount).ToArray();
        if (a.Indices.Length != bi.Length) return $"{a.Indices.Length} indices vs Blender's {bi.Length}";
        for (int i = 0; i < bi.Length; i++) if (a.Indices[i] != bi[i]) return $"index {i} is {a.Indices[i]} vs Blender's {bi[i]}";
        return null;
    }

    static string Arr(string what, float[] a, float[] b, int per)
    {
        if (b == null) return what + ": Blender wrote none";
        if (a.Length != b.Length) return $"{what}: {a.Length / per} vs Blender's {b.Length / per}";
        int differ = 0, first = -1;
        for (int i = 0; i < a.Length; i++)
            if (BitConverter.ToUInt32(BitConverter.GetBytes(a[i]), 0) != BitConverter.ToUInt32(BitConverter.GetBytes(b[i]), 0)) { differ++; if (first < 0) first = i; }
        if (differ == 0) return null;
        int v = first / per;
        string Row(float[] x) => string.Join(" ", Enumerable.Range(0, per).Select(k => x[v * per + k].ToString("R")));
        return $"{what} differ in {differ} of {a.Length} values, first at vertex {v}: {Row(a)} vs Blender's {Row(b)}";
    }
}
