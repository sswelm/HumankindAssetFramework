// ExportDrill.cs - BlenderDeployExport against the GLB deploy_convert.py writes (tools/deploy_drill.sh, part 8a).
//   deploy.exe --export <jobs file> <dump>...
// A dump is what tools/deploy-drill/blender_export_dump.py printed: the script run to its end for each job, its DEPLOY
// lines of the export step, and the written GLB read back row by row (the scene, every node with its children, mesh,
// skin and transform bits, the skins with their joints and inverse bind matrices, every mesh's primitives with the
// bytes of each attribute and of the indices hashed, the materials, images, textures and animations). Compared here:
// the log lines, the scene, every node row, every skin and inverse bind matrix row, every mesh and primitive row (the
// material index included), and the animations' names. NOT compared (part 8b, 8c): the materials' contents, the textures
// and images, the animations' channels, the GLB's sizes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

static class ExportDrill
{
    static readonly string[] CoverKeys =
    {
        "a job's export compared", "a skinned mesh node", "a bound mesh without a triangle (a node without a mesh)", "the neutral bone (a group that names no bone)",
        "a trim to argv's frames", "the trim's end from the recoil tail", "no trim (two arguments)", "leftover objects purged", "nothing to purge",
        "no mesh with a triangle: the skin written unused", "a primitive with a material", "a primitive without a material", "a mesh of several primitives",
        "joints as bytes (fewer than 256 bones)", "a UV layer", "a mesh without UVs", "a recoil role among the animations",
        "the script stops before the export (its exit)", "several scenes (an empty one written)", "the export left to Blender by name (EXPORTLEFT)",
    };

    static uint Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
    static string H(float f) => Bits(f).ToString("x8");
    static string Sha(byte[] bytes) { using (var sha = System.Security.Cryptography.SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2"))); }
    static string ShaFloats(float[] v) { var b = new byte[4 * v.Length]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, 4 * i); return Sha(b); }
    static string ShaUshorts(ushort[] v) { var b = new byte[2 * v.Length]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, 2 * i); return Sha(b); }
    static string ShaBytes(IEnumerable<byte> v) => Sha(v.ToArray());
    static string Cut(string s) => s.Length > 150 ? s.Substring(0, 150) + "..." : s;

    public static int Run(string[] args)
    {
        var jobs = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(args[0]))
        {
            if (line.Trim().Length == 0) continue;
            var t = line.TrimEnd('\r').Split('|');
            jobs[t[0]] = t;
        }
        var cover = new SortedDictionary<string, long>(); foreach (var k in CoverKeys) cover[k] = 0;
        int fails = 0, files = 0, left = 0, exits = 0, rolesLeft = 0; long nodes = 0, joints = 0, ibms = 0, meshes = 0, prims = 0, vertices = 0, lines = 0, anims = 0;
        var blocks = new List<List<string>>();
        foreach (var dump in args.Skip(1))
            foreach (var line in File.ReadLines(dump))
            {
                if (line.StartsWith("JOB\t")) blocks.Add(new List<string>());
                if (blocks.Count > 0) blocks[blocks.Count - 1].Add(line);
            }
        foreach (var block in blocks)
        {
            string key = block[0].Split('\t')[1];
            files++;
            var problems = new List<string>();
            try
            {
                if (!jobs.TryGetValue(key, out var job)) throw new InvalidDataException("the dump has a job the jobs file does not");
                var rows = block.Select(l => l.Split('\t')).ToList();
                if (!rows.Any(t => t[0] == "DONE")) throw new InvalidDataException(rows.Any(t => t[0] == "FAIL") ? "Blender could not run it: " + rows.First(t => t[0] == "FAIL").Last() : "the dump has no DONE row");
                List<string[]> Of(string k) => rows.Where(t => t[0] == k).ToList();
                var exit8 = Of("EXIT8"); var dies8 = Of("DIES8");
                var log8 = block.Where(l => l.StartsWith("LOG8\t")).Select(l => l.Substring(5)).ToList();

                var m = GlbReader.Read(job[1]);
                var argv = job.Skip(2).ToArray();
                var names = BlenderNames.Compute(m);
                var r = BlenderDeploy.Decide(m, argv, names);
                if (r.Fallback != null)
                {
                    if (!key.StartsWith("LEFT:", StringComparison.Ordinal)) { fails++; Console.WriteLine($"FAIL {key}: left to Blender, which the jobs file does not expect: {r.Fallback}"); continue; }
                    left++; Console.WriteLine($"LEFT {key}: {r.Fallback}"); continue;
                }
                var baked = r.Exit ? r : BlenderDeploy.Decide(m, argv, names, true);
                if (!r.Exit && baked.Fallback != null)
                {
                    if (!key.StartsWith("BAKELEFT:", StringComparison.Ordinal)) { fails++; Console.WriteLine($"FAIL {key}: left to Blender at the bake, which the jobs file does not expect: {baked.Fallback}"); continue; }
                    left++; Console.WriteLine($"BAKELEFT {key}: {baked.Fallback}"); continue;
                }
                // the script's own exits before the export: the no-parts guard, the recoil step without a tube - SystemExit(1), no file
                bool exits1 = r.Exit || baked.ExitAtRecoil;
                if (exit8.Count > 0 && (exit8.Count != 1 || exit8[0].Length != 2 || exit8[0][1] != "1")) throw new InvalidDataException("the dump has an invalid EXIT8 row (expected exactly one EXIT8 with code 1)");
                if (exits1 != (exit8.Count == 1)) { fails++; Console.WriteLine($"FAIL {key}: " + (exits1 ? "the port stops before the export (the script's exit), the script wrote a file" : "the script stops before the export, the port goes on")); continue; }
                if (exits1) { exits++; cover["the script stops before the export (its exit)"]++; Console.WriteLine($"PASS {key}: the script stops before the export, as it does here"); continue; }
                baked.Finish();
                if (baked.RoleFallback != null)
                {
                    if (!key.StartsWith("ROLELEFT:", StringComparison.Ordinal)) { fails++; Console.WriteLine($"FAIL {key}: the role clips are left to Blender, which the jobs file does not expect: {baked.RoleFallback}"); continue; }
                    rolesLeft++; Console.WriteLine($"ROLELEFT {key}: {baked.RoleFallback} (the export is not compared)"); continue;
                }
                var x = BlenderDeployExport.Build(m, names, baked, argv);
                if (dies8.Count > 0)
                {
                    if (x.Fallback == null) throw new InvalidDataException($"the script died in the export step ({string.Join(" ", dies8[0].Skip(1))}) and the port went on");
                    left++; Console.WriteLine($"EXPORTLEFT {key}: {x.Fallback} (the script died: {string.Join(" ", dies8[0].Skip(1))})"); continue;
                }
                if (x.Fallback != null)
                {
                    if (!key.StartsWith("EXPORTLEFT:", StringComparison.Ordinal)) { fails++; Console.WriteLine($"FAIL {key}: the export is left to Blender, which the jobs file does not expect: {x.Fallback}"); continue; }
                    left++; cover["the export left to Blender by name (EXPORTLEFT)"]++; Console.WriteLine($"EXPORTLEFT {key}: {x.Fallback}"); continue;
                }
                if (key.StartsWith("EXPORTLEFT:", StringComparison.Ordinal)) problems.Add("the jobs file expects the export left to Blender (EXPORTLEFT:), and it is made here (take the mark away once it holds)");

                // ---- the log: every DEPLOY line of the export step, the "wrote" line last with Blender's own path
                if (log8.Count == 0 || !log8[log8.Count - 1].StartsWith("DEPLOY wrote: ", StringComparison.Ordinal)) throw new InvalidDataException("the dump has no 'DEPLOY wrote:' line");
                Compare(problems, "the export's log (8)", x.Log, log8.Take(log8.Count - 1).ToList());
                lines += log8.Count;
                // ---- the scene and the nodes
                var scene = Of("SCENE"); if (scene.Count == 0) throw new InvalidDataException("the dump has no SCENE row");
                foreach (var t in scene) if (t.Length != 3) throw new InvalidDataException($"the dump's SCENE row has {t.Length} fields, expected 3");
                Compare(problems, "the scenes (name, roots)", x.Scenes.Select(s => $"{s.name}\t{string.Join(",", s.roots)}").ToList(), scene.Select(t => t[1] + "\t" + t[2]).ToList());
                string V(float[] v) => v == null ? "-" : string.Join("\t", v.Select(H));
                var nodeRows = Of("NODE");
                foreach (var t in nodeRows) if (t.Length < 9) throw new InvalidDataException($"the dump's node row {t[1]} is cut short ({t.Length} fields)");
                var mineNodes = x.Nodes.Select((n, i) => $"{i}\t{n.Name}\t{(n.Children.Count > 0 ? string.Join(",", n.Children) : "-")}\t{(n.Mesh >= 0 ? n.Mesh.ToString() : "-")}\t{(n.Skin >= 0 ? n.Skin.ToString() : "-")}\t{V(n.Translation)}\t{V(n.Rotation)}\t{V(n.Scale)}").ToList();
                Compare(problems, "the nodes (index, name, children, mesh, skin, translation, rotation, scale)", mineNodes, nodeRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
                nodes += nodeRows.Count;
                // ---- the skins and their inverse bind matrices
                var skinRows = Of("SKIN");
                foreach (var t in skinRows) if (t.Length != 5) throw new InvalidDataException($"the dump's skin row has {t.Length} fields, expected 5");
                Compare(problems, "the skins (index, name, joints, skeleton)", x.Skins.Select((s, i) => $"{i}\t{s.Name}\t{string.Join(",", s.Joints)}\t-").ToList(), skinRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
                joints += skinRows.Sum(t => t[3].Split(',').Length);
                var ibmRows = Of("IBM");
                foreach (var t in ibmRows) if (t.Length != 19) throw new InvalidDataException($"the dump's inverse bind matrix row has {t.Length} fields, expected 19");
                var mineIbm = new List<string>();
                for (int si = 0; si < x.Skins.Count; si++) for (int k = 0; k < x.Skins[si].InverseBind.Count; k++) mineIbm.Add($"{si}\t{k}\t{string.Join("\t", x.Skins[si].InverseBind[k].Select(H))}");
                Compare(problems, "the inverse bind matrices (skin, joint, 16 floats column by column)", mineIbm, ibmRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
                ibms += ibmRows.Count;
                // ---- the meshes and their primitives: counts, the material, the bytes of every attribute and of the indices
                var meshRows = Of("MESH");
                foreach (var t in meshRows) if (t.Length != 4) throw new InvalidDataException($"the dump's mesh row has {t.Length} fields, expected 4");
                Compare(problems, "the meshes (index, name, primitives)", x.Meshes.Select((me, i) => $"{i}\t{me.Name}\t{me.Primitives.Count}").ToList(), meshRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
                meshes += meshRows.Count;
                var primRows = Of("PRIM");
                foreach (var t in primRows) if (t.Length < 7) throw new InvalidDataException($"the dump's primitive row is cut short ({t.Length} fields)");
                var minePrims = new List<string>();
                for (int mi = 0; mi < x.Meshes.Count; mi++)
                    for (int pi = 0; pi < x.Meshes[mi].Primitives.Count; pi++)
                    {
                        var p = x.Meshes[mi].Primitives[pi];
                        var attrs = new SortedDictionary<string, string>(StringComparer.Ordinal);
                        attrs["POSITION"] = $"{p.VertexCount}:VEC3/5126:{ShaFloats(p.Positions)}";
                        attrs["NORMAL"] = $"{p.VertexCount}:VEC3/5126:{ShaFloats(p.Normals)}";
                        for (int u = 0; u < p.Uv.Count; u++) attrs["TEXCOORD_" + u] = $"{p.VertexCount}:VEC2/5126:{ShaFloats(p.Uv[u])}";
                        for (int c = 0; c < p.Colors.Count; c++)
                        {
                            var set = p.Colors[c];
                            attrs["COLOR_" + c] = set.Forced ? $"{p.VertexCount}:VEC4/5121n:{ShaBytes(Enumerable.Repeat((byte)255, 4 * p.VertexCount))}"
                                                : set.Alpha ? $"{p.VertexCount}:VEC4/5123n:{ShaUshorts(set.Shorts)}" : $"{p.VertexCount}:VEC3/5126:{ShaFloats(set.Data)}";
                        }
                        if (p.Joints != null)
                        {
                            bool bytes = p.Joints.Max() < 256;
                            attrs["JOINTS_0"] = bytes ? $"{p.VertexCount}:VEC4/5121:{ShaBytes(p.Joints.Select(j => (byte)j))}" : $"{p.VertexCount}:VEC4/5123:{ShaUshorts(p.Joints)}";
                            attrs["WEIGHTS_0"] = $"{p.VertexCount}:VEC4/5126:{ShaFloats(p.Weights)}";
                            if (bytes) cover["joints as bytes (fewer than 256 bones)"]++;
                        }
                        int maxIndex = p.Indices.Length > 0 ? p.Indices.Max() : 0;
                        string indexSha = maxIndex < 65535 ? ShaUshorts(p.Indices.Select(i => (ushort)i).ToArray()) : Sha(p.Indices.SelectMany(i => BitConverter.GetBytes((uint)i)).ToArray());
                        int mat = x.Meshes[mi].Material[pi];
                        minePrims.Add($"{mi}\t{pi}\t{(mat >= 0 ? mat.ToString() : "-")}\t4\t{p.Indices.Length}\t{indexSha}\t{string.Join("\t", attrs.Select(kv => kv.Key + ":" + kv.Value))}");
                        vertices += p.VertexCount;
                        cover[mat >= 0 ? "a primitive with a material" : "a primitive without a material"]++;
                        cover[p.Uv.Count > 0 ? "a UV layer" : "a mesh without UVs"]++;
                    }
                Compare(problems, "the primitives (mesh, primitive, material, mode, indices, their hash, each attribute's count, type and hash)", minePrims, primRows.Select(t => string.Join("\t", t.Skip(1))).ToList());
                prims += primRows.Count;
                // ---- the animations: names and order (their channels are part 8b's)
                var animRows = Of("ANIM");
                foreach (var t in animRows) if (t.Length != 5) throw new InvalidDataException($"the dump's animation row has {t.Length} fields, expected 5");
                // every bone gets its three channels (optimize_animation_keep_anim_armature: a constant one is kept), one sampler each
                int channels3 = 3 * baked.BonesAfterRecoil.Count;
                Compare(problems, "the animations (name, channels, samplers)", x.Animations.Select(a => $"{a}\t{channels3}\t{channels3}").ToList(), animRows.Select(t => $"{t[2]}\t{t[3]}\t{t[4]}").ToList());
                anims += animRows.Count;
                if (problems.Count > 0) { fails++; Console.WriteLine($"FAIL {key}: " + string.Join("; ", problems.Take(5))); continue; }
                cover["a job's export compared"]++;
                if (x.Nodes.Any(n => n.Skin >= 0)) cover["a skinned mesh node"]++;
                if (x.Nodes.Any(n => n.Object != null && n.Object.Type == "MESH" && n.Mesh < 0)) cover["a bound mesh without a triangle (a node without a mesh)"]++;
                if (x.NeutralBone) cover["the neutral bone (a group that names no bone)"]++;
                if (x.Trimmed) cover[baked.Recoil != null ? "the trim's end from the recoil tail" : "a trim to argv's frames"]++; else cover["no trim (two arguments)"]++;
                cover[x.Purged.Count > 0 ? "leftover objects purged" : "nothing to purge"]++;
                if (x.UnusedSkin) cover["no mesh with a triangle: the skin written unused"]++;
                if (x.Meshes.Any(me => me.Primitives.Count > 1)) cover["a mesh of several primitives"]++;
                if (x.Animations.Contains("recoil")) cover["a recoil role among the animations"]++;
                if (x.Scenes.Count > 1) cover["several scenes (an empty one written)"]++;
                Console.WriteLine($"PASS {key}: {log8.Count} log lines, {nodeRows.Count} nodes, {skinRows.Count} skin(s), {meshRows.Count} meshes with {primRows.Count} primitives and {animRows.Count} animations equal to Blender's");
            }
            catch (Exception e) { fails++; Console.WriteLine($"FAIL {key}: {e.GetType().Name}: {e.Message}"); }
        }
        var dumped = new HashSet<string>(blocks.Select(b => b[0].Split('\t')[1]), StringComparer.Ordinal);
        foreach (var k in jobs.Keys) if (!dumped.Contains(k)) { fails++; Console.WriteLine($"FAIL {k}: the dump holds no such job"); }
        foreach (var kv in cover) Console.WriteLine($"COVER {kv.Value} {kv.Key}");
        Console.WriteLine($"TOTAL jobs {files} failed {fails} left {left} exits {exits} rolesleft {rolesLeft} lines {lines} nodes {nodes} joints {joints} ibms {ibms} meshes {meshes} primitives {prims} vertices {vertices} animations {anims}");
        return fails == 0 ? 0 : 1;
    }

    static void Compare(List<string> problems, string what, List<string> mine, List<string> theirs)
    {
        if (mine.SequenceEqual(theirs)) return;
        int i = 0; while (i < mine.Count && i < theirs.Count && mine[i] == theirs[i]) i++;
        problems.Add($"{what}: {mine.Count} here, {theirs.Count} in Blender, first difference at {i + 1}: here «{Cut(i < mine.Count ? mine[i].Replace('\t', ' ') : "(none)")}», Blender «{Cut(i < theirs.Count ? theirs[i].Replace('\t', ' ') : "(none)")}»");
    }
}
