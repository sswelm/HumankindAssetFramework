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
    static readonly HashSet<string> RowKinds = new HashSet<string>(new[]
    {
        "JOB", "LOG8", "DIES8", "EXIT8", "GLB", "SCENE", "NODE", "SKIN", "IBM", "MESH", "PRIM", "MAT", "IMG", "TEX", "SAMP", "ANIM", "CHAN", "DONE", "FAIL",
    }, StringComparer.Ordinal);

    static readonly string[] CoverKeys =
    {
        "a job's export compared", "a skinned mesh node", "a bound mesh without a triangle (a node without a mesh)", "the neutral bone (a group that names no bone)",
        "a trim to argv's frames", "the trim's end from the recoil tail", "no trim (two arguments)", "leftover objects purged", "nothing to purge",
        "no mesh with a triangle: the skin written unused", "a primitive with a material", "a primitive without a material", "a mesh of several primitives",
        "joints as bytes (fewer than 256 bones)", "a UV layer", "a mesh without UVs", "a recoil role among the animations", "a channel reduced to its first and last sample", "a channel with every sample", "a channel of one sample", "a STEP channel", "a LINEAR channel", "a bone in an Euler mode", "a bone holding the pose the script left",
        "the script stops before the export (its exit)", "several scenes (an empty one written)", "the export left to Blender by name (EXPORTLEFT)",
    };

    static uint Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
    static string H(float f) => Bits(f).ToString("x8");
    static string Sha(byte[] bytes) { using (var sha = System.Security.Cryptography.SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2"))); }
    static string Cut(string s) => s.Length > 150 ? s.Substring(0, 150) + "..." : s;

    public static int Run(string[] args)
    {
        // --glbdir <dir>: the files Blender wrote (the dump keeps them as <key>.glb): the port's bytes against them (8c)
        string glbDir = null;
        if (args.Length >= 2 && args[0] == "--glbdir") { glbDir = args[1]; args = args.Skip(2).ToArray(); }
        var jobs = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(args[0]))
        {
            if (line.Trim().Length == 0) continue;
            var t = line.TrimEnd('\r').Split('|');
            if (t.Length < 2 || t[0].Length == 0 || jobs.ContainsKey(t[0])) { Console.WriteLine("FAIL jobs: an invalid or duplicate job definition"); return 1; }
            jobs.Add(t[0], t);
        }
        if (jobs.Count == 0) { Console.WriteLine("FAIL jobs: no export jobs to compare"); return 1; }
        var cover = new SortedDictionary<string, long>(); foreach (var k in CoverKeys) cover[k] = 0;
        int fails = 0, files = 0, left = 0, exits = 0, rolesLeft = 0; long nodes = 0, joints = 0, ibms = 0, meshes = 0, prims = 0, vertices = 0, lines = 0, anims = 0, materials = 0, images = 0, channels = 0, glbs = 0, glbBytes = 0;
        var blocks = new List<List<string>>();
        foreach (var dump in args.Skip(1))
            foreach (var line in File.ReadLines(dump))
            {
                if (line.StartsWith("JOB\t")) blocks.Add(new List<string>());
                if (blocks.Count > 0) blocks[blocks.Count - 1].Add(line);
                else if (line.Length > 0) { fails++; Console.WriteLine("FAIL dump: a row before the first JOB"); }
            }
        var dumped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            string key = block[0].Split('\t')[1];
            files++;
            var problems = new List<string>();
            try
            {
                if (!jobs.TryGetValue(key, out var job)) throw new InvalidDataException("the dump has a job the jobs file does not");
                if (!dumped.Add(key)) throw new InvalidDataException("the dump repeats a job");
                var rows = block.Select(l => l.Split('\t')).ToList();
                List<string[]> Of(string k) => rows.Where(t => t[0] == k).ToList();
                if (rows.Any(t => t[0] == "FAIL")) throw new InvalidDataException("Blender could not run it: " + rows.First(t => t[0] == "FAIL").Last());
                if (rows.Any(t => !RowKinds.Contains(t[0]))) throw new InvalidDataException("the dump has an unknown row kind");
                if (rows[0].Length != 2) throw new InvalidDataException("the dump has an invalid JOB row");
                var done = Of("DONE");
                if (done.Count == 0) throw new InvalidDataException("the dump has no DONE row");
                if (done.Count != 1 || done[0].Length != 2 || done[0][1] != key || rows[rows.Count - 1] != done[0])
                    throw new InvalidDataException("the dump must end with exactly one matching DONE row");
                var exit8 = Of("EXIT8"); var dies8 = Of("DIES8");
                if (exit8.Count > 0 && (exit8.Count != 1 || exit8[0].Length != 2 || exit8[0][1] != "1")) throw new InvalidDataException("the dump has an invalid EXIT8 row (expected exactly one EXIT8 with code 1)");
                if (dies8.Count > 0 && (dies8.Count != 1 || dies8[0].Length != 2 || dies8[0][1].Length == 0)) throw new InvalidDataException("the dump has an invalid DIES8 row");
                if (exit8.Count > 0 && dies8.Count > 0) throw new InvalidDataException("the dump claims both an exit and a death");
                var glb = Of("GLB");
                if (exit8.Count > 0 || dies8.Count > 0)
                {
                    if (rows.Any(t => t[0] != "JOB" && t[0] != "LOG8" && t[0] != "EXIT8" && t[0] != "DIES8" && t[0] != "DONE"))
                        throw new InvalidDataException("the dump claims exported file rows after an exit or death");
                }
                else if (glb.Count != 1 || glb[0].Length != 5 || glb[0].Skip(1).Take(3).Any(v => !long.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)) || glb[0][4].Length != 64)
                    throw new InvalidDataException("the dump has no single valid GLB written-file row");
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
                    if (!key.StartsWith("EXPORTLEFT:", StringComparison.Ordinal)) throw new InvalidDataException("the export is left to Blender, which the jobs file does not expect: " + x.Fallback);
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
                // ---- the scene, the nodes, the skins, the meshes and primitives, the materials, the animations, the channels (8b) and the bytes (8c)
                byte[] theirGlb = glbDir != null && File.Exists(Path.Combine(glbDir, key + ".glb")) ? File.ReadAllBytes(Path.Combine(glbDir, key + ".glb")) : null;
                // every bone gets its three channels (optimize_animation_keep_anim_armature: a constant one is kept), one sampler each
                int channels3 = 3 * baked.BonesAfterRecoil.Count;
                var st = GlbRowsCompare.Compare(x, rows, problems, cover, _ => channels3, true, true, theirGlb);
                nodes += st.Nodes; joints += st.Joints; ibms += st.Ibms; meshes += st.Meshes; prims += st.Prims; vertices += st.Vertices; anims += st.Anims; materials += st.Materials; images += st.Images; channels += st.Channels;
                if (problems.Count == 0) { glbs++; glbBytes += st.GlbBytes; }
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
                var ch = x.ChannelRows;
                cover["a channel reduced to its first and last sample"] += ch.Reduced; cover["a channel with every sample"] += ch.Full; cover["a channel of one sample"] += ch.Single;
                cover["a STEP channel"] += ch.Stepped; cover["a LINEAR channel"] += ch.Linear; cover["a bone in an Euler mode"] += ch.EulerBones; cover["a bone holding the pose the script left"] += ch.HeldBones;
                if (x.Scenes.Count > 1) cover["several scenes (an empty one written)"]++;
                Console.WriteLine($"PASS {key}: {log8.Count} log lines, {st.Nodes} nodes, {Of("SKIN").Count} skin(s), {st.Meshes} meshes with {st.Prims} primitives, {st.Anims} animations with {st.Channels} channels equal to Blender's");
            }
            catch (Exception e) { fails++; Console.WriteLine($"FAIL {key}: {e.GetType().Name}: {e.Message}"); }
        }
        foreach (var k in jobs.Keys) if (!dumped.Contains(k)) { fails++; Console.WriteLine($"FAIL {k}: the dump holds no such job"); }
        foreach (var kv in cover) Console.WriteLine($"COVER {kv.Value} {kv.Key}");
        Console.WriteLine($"TOTAL jobs {files} failed {fails} left {left} exits {exits} rolesleft {rolesLeft} lines {lines} nodes {nodes} joints {joints} ibms {ibms} meshes {meshes} primitives {prims} vertices {vertices} animations {anims} materials {materials} images {images} channels {channels} glbs {glbs} glbbytes {glbBytes}");
        return fails == 0 ? 0 : 1;
    }

    static void Compare(List<string> problems, string what, List<string> mine, List<string> theirs) => GlbRowsCompare.Compare(problems, what, mine, theirs);
}
