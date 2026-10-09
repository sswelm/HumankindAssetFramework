// DecisionsDrill.cs - BlenderDeploy.Decide against deploy_convert.py itself (tools/deploy_drill.sh, part 2).
//   deploy.exe --decisions <jobs file> <dump>...
// A dump is what tools/deploy-drill/blender_decisions_dump.py printed for the jobs: the script's own log lines, its
// decisions, and the scene it left. Everything is compared: the log TEXT, the frame range, the normalization's numbers
// (the bits of each double), the path, the parts with their parents in the script's order, the culled parts, the
// pair-merges, every object left (order, type, parent, datablock name, action), each one's matrix_world, its location,
// rotation and scale, and a mesh object's bound_box - floats as bits, the sign of a zero included.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

static class DecisionsDrill
{
    static readonly string[] CoverKeys =
    {
        "a job compared", "the default strip list", "a strip list given", "stripExtra", "a stripped object's child left as a root", "a bone shape that survives the strip",
        "no object with an action left (frame range 1..1)", "normalization: x100", "normalization: recentered", "normalization: none",
        "bone slimming", "the legacy path", "the contract path", "a culled part", "a culled part's descendant", "a pair-merge", "the script stops (no animated part)",
        "the recoil step off with a recoil range given", "left to Blender (BlenderDeploy.Fallback)", "an armature that is a part (its bones are animated)",
    };

    static uint Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
    static float F(string hex) => BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(hex, 16)), 0);
    static string H(float f) => Bits(f).ToString("x8");
    static string H64(double d) => BitConverter.DoubleToInt64Bits(d).ToString("x16");

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
        int fails = 0, files = 0, left = 0; long matrices = 0, objects = 0, lines = 0;
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
                var log = block.Where(l => l.StartsWith("LOG\t")).Select(l => l.Substring(4)).ToList();
                bool exit = Of("EXIT").Count > 0;
                if (!exit) foreach (string k in new[] { "RANGE", "NORM", "FLAG" }) if (Of(k).Count != 1) throw new InvalidDataException($"the dump has {Of(k).Count} {k} rows");
                var bObj = Of("OBJ"); var bM = Of("M").ToDictionary(t => t[1], t => t, StringComparer.Ordinal); var bT = Of("T").ToDictionary(t => t[1], t => t, StringComparer.Ordinal);
                var bBox = Of("BOX").ToDictionary(t => t[1], t => t, StringComparer.Ordinal);
                if (!exit)
                {
                    if (bObj.Count == 0) throw new InvalidDataException("the dump has no object");
                    foreach (var o in bObj)
                    {
                        if (!bM.TryGetValue(o[1], out var mr) || mr.Length != 18) throw new InvalidDataException($"the dump has no complete matrix row for '{o[1]}'");
                        if (!bT.TryGetValue(o[1], out var tr) || tr.Length != 12) throw new InvalidDataException($"the dump has no complete transform row for '{o[1]}'");
                        if (o[2] == "MESH" && (!bBox.TryGetValue(o[1], out var br) || br.Length != 8)) throw new InvalidDataException($"the dump has no complete box row for '{o[1]}'");
                    }
                }

                var m = GlbReader.Read(job[1]);
                var argv = job.Skip(2).ToArray();
                var r = BlenderDeploy.Decide(m, argv);
                // a job is left to Blender only where the jobs file says so ("LEFT:" before its key): a wrong decision that
                // happens to end in a fallback must not pass as one
                bool expectLeft = key.StartsWith("LEFT:", StringComparison.Ordinal);
                if (r.Fallback != null)
                {
                    if (!expectLeft) { fails++; Console.WriteLine($"FAIL {key}: left to Blender, which the jobs file does not expect: {r.Fallback}"); continue; }
                    left++; cover["left to Blender (BlenderDeploy.Fallback)"]++;
                    Console.WriteLine($"LEFT {key}: {r.Fallback}");
                    continue;
                }
                if (expectLeft) problems.Add("the jobs file expects this left to Blender, and it is decided here (take the LEFT: mark away once it holds)");
                // the log, line for line
                for (int i = 0; i < Math.Max(log.Count, r.Log.Count); i++)
                {
                    string theirs = i < log.Count ? log[i] : "(no line)", mine = i < r.Log.Count ? r.Log[i] : "(no line)";
                    if (theirs != mine) { problems.Add($"log line {i + 1}: here «{Cut(mine)}», Blender «{Cut(theirs)}»"); break; }
                }
                lines += log.Count;
                if (exit != r.Exit) problems.Add(exit ? "the script stops itself, not here" : "stops here, the script goes on");
                if (!exit && !r.Exit)
                {
                    var range = Of("RANGE")[0]; var norm = Of("NORM")[0];
                    if (int.Parse(range[1]) != r.FrameMin || int.Parse(range[2]) != r.FrameMax) problems.Add($"frame range {r.FrameMin}..{r.FrameMax} here, {range[1]}..{range[2]} in Blender");
                    string mineNorm = $"{H64(r.NormDim)} {H64(r.NormScale)} {(r.Recenter ? 1 : 0)} {H64(r.OffsetH)} {H64(r.OffsetV)}", theirNorm = string.Join(" ", norm.Skip(1));
                    if (mineNorm != theirNorm) problems.Add($"normalization {mineNorm} here, {theirNorm} in Blender (dim, scale, recenter, offset h, offset v)");
                    if ((Of("FLAG")[0][1] == "1") != r.Legacy) problems.Add("the path differs (legacy or contract)");
                    string Seq(IEnumerable<string> s) => string.Join("\n", s);
                    Compare(problems, "parts", r.Parts.Select(p => p.Name + "\t" + (p.Parent != null ? p.Parent.Name : "-")).ToList(), Of("PART").Select(t => t[1] + "\t" + t[2]).ToList());
                    Compare(problems, "culled parts", r.Bad, Of("BAD").Select(t => t[1]).ToList());
                    Compare(problems, "pair-merges", r.Alias.Select(a => a.dropped.Name + "\t" + a.kept.Name).ToList(), Of("ALIAS").Select(t => t[1] + "\t" + t[2]).ToList());
                    Compare(problems, "objects", r.Objects.Select(o => $"{o.Name}\t{o.Type}\t{(o.Parent != null ? o.Parent.Name : "-")}\t{o.DataName ?? "-"}\t{(o.HasAction ? 1 : 0)}").ToList(), bObj.Select(t => string.Join("\t", t.Skip(1))).ToList());
                    long wrongM = 0, wrongT = 0, wrongB = 0; string firstM = null, firstT = null, firstB = null;
                    foreach (var o in r.Objects)
                    {
                        if (!bM.TryGetValue(o.Name, out var mr)) continue;
                        objects++;
                        bool ok = true;
                        for (int row = 0; row < 4; row++) for (int c = 0; c < 4; c++) if (H(o.World[c * 4 + row]) != mr[2 + row * 4 + c]) ok = false;
                        matrices++;
                        if (!ok) { wrongM++; firstM = firstM ?? $"'{o.Name}' here [{string.Join(" ", Enumerable.Range(0, 16).Select(i => o.World[(i % 4) * 4 + i / 4].ToString("R")))}] Blender [{string.Join(" ", mr.Skip(2).Select(h => F(h).ToString("R")))}]"; }
                        var tr = bT[o.Name];
                        // an object the importer did not make keeps Euler angles: its rotation_quaternion is not what turns it
                        var mineT = o.Loc.Select(H).Concat(o.Euler ? tr.Skip(5).Take(4) : o.Quat.Select(H)).Concat(o.Scale.Select(H)).ToList();
                        if (!mineT.SequenceEqual(tr.Skip(2))) { wrongT++; firstT = firstT ?? $"'{o.Name}' here loc[{string.Join(",", o.Loc.Select(Show))}] quat[{string.Join(",", o.Quat.Select(Show))}] scale[{string.Join(",", o.Scale.Select(Show))}] Blender [{string.Join(",", tr.Skip(2).Select(h => Show(F(h))))}]"; }
                        if (o.Type == "MESH" && bBox.TryGetValue(o.Name, out var br) && !o.BoxMin.Concat(o.BoxMax).Select(H).SequenceEqual(br.Skip(2))) { wrongB++; firstB = firstB ?? $"'{o.Name}'"; }
                    }
                    if (wrongM > 0) problems.Add($"{wrongM} of {r.Objects.Count} matrices differ, first {firstM}");
                    if (wrongT > 0) problems.Add($"{wrongT} of {r.Objects.Count} location/rotation/scale sets differ, first {firstT}");
                    if (wrongB > 0) problems.Add($"{wrongB} bound boxes differ, first {firstB}");
                }
                if (problems.Count > 0) { fails++; Console.WriteLine($"FAIL {key}: " + string.Join("; ", problems.Take(5))); continue; }
                // what the job exercised counts once it holds
                cover["a job compared"]++;
                string strip = argv.Length > 2 ? argv[2].Trim() : "";
                cover[strip.Length > 0 ? "a strip list given" : "the default strip list"]++;
                if (log.Any(l => l.StartsWith("DEPLOY stripExtra"))) cover["stripExtra"]++;
                if (log.Any(l => l.StartsWith("DEPLOY recoil step empty/0"))) cover["the recoil step off with a recoil range given"]++;
                if (r.Exit) cover["the script stops (no animated part)"]++;
                var names = BlenderNames.Compute(m); var before = names.Objects.ToDictionary(o => o.Name, o => o, StringComparer.Ordinal);
                var alive = new HashSet<string>(r.Objects.Select(o => o.Name), StringComparer.Ordinal);
                if (r.Objects.Any(o => before.TryGetValue(o.Name, out var b) && b.Parent != null && !alive.Contains(b.Parent) && (o.Parent == null || o.Parent.Node < 0 && o.Parent.Euler))) cover["a stripped object's child left as a root"]++;
                if (r.Objects.Any(o => names.BoneShapes.Contains(o.Name))) cover["a bone shape that survives the strip"]++;
                if (r.Objects.Any(o => o.Type == "ARMATURE" && o.HasAction)) cover["an armature that is a part (its bones are animated)"]++;
                if (!r.Objects.Any(o => o.HasAction) && r.FrameMin == 1 && r.FrameMax == 1) cover["no object with an action left (frame range 1..1)"]++;
                if (r.NormScale != 1.0) cover["normalization: x100"]++;
                if (r.Recenter) cover["normalization: recentered"]++;
                if (r.NormScale == 1.0 && !r.Recenter) cover["normalization: none"]++;
                if (log.Any(l => l.StartsWith("DEPLOY bone slimming: kept"))) cover["bone slimming"]++;
                cover[r.Legacy ? "the legacy path" : "the contract path"]++;
                if (r.Bad.Count > 0) cover["a culled part"]++;
                if (log.Any(l => l.StartsWith("DEPLOY culled") && !l.Contains("(+0 descendant"))) cover["a culled part's descendant"]++;
                if (r.Alias.Count > 0) cover["a pair-merge"]++;
                Console.WriteLine(r.Exit ? $"PASS {key}: {log.Count} log lines equal to Blender's, and the script stops there as it does here (no scene is compared)"
                                         : $"PASS {key}: {log.Count} log lines, {r.Parts.Count} parts, {r.Objects.Count} objects with their matrices, transforms and boxes equal to Blender's");
            }
            catch (Exception e) { fails++; Console.WriteLine($"FAIL {key}: {e.GetType().Name}: {e.Message}"); }
        }
        // a job the jobs file has and no dump holds was not judged at all
        var dumped = new HashSet<string>(blocks.Select(b => b[0].Split('\t')[1]), StringComparer.Ordinal);
        foreach (var k in jobs.Keys) if (!dumped.Contains(k)) { fails++; Console.WriteLine($"FAIL {k}: the dump holds no such job"); }
        foreach (var kv in cover) Console.WriteLine($"COVER {kv.Value} {kv.Key}");
        Console.WriteLine($"TOTAL jobs {files} failed {fails} left {left} objects {objects} matrices {matrices} lines {lines}");
        return fails == 0 ? 0 : 1;
    }

    static string Show(float x) => x.ToString("R") + (x == 0f && Bits(x) != 0 ? "(-0)" : "");
    static string Cut(string s) => s.Length > 150 ? s.Substring(0, 150) + "..." : s;

    static void Compare(List<string> problems, string what, List<string> mine, List<string> theirs)
    {
        if (mine.SequenceEqual(theirs)) return;
        int i = 0; while (i < mine.Count && i < theirs.Count && mine[i] == theirs[i]) i++;
        problems.Add($"{what}: {mine.Count} here, {theirs.Count} in Blender, first difference at {i + 1}: here «{(i < mine.Count ? mine[i].Replace('\t', ' ') : "(none)")}», Blender «{(i < theirs.Count ? theirs[i].Replace('\t', ' ') : "(none)")}»");
    }
}
