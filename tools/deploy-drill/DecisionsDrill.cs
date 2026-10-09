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
        "the recoil step off with a recoil range given", "left to Blender (BlenderDeploy.Fallback)", "an armature that is a part (its bones are animated)", "left to Blender at the bake (the scene before it is held)",
        "a bake on the legacy path", "a clip of frame 0 alone (the bake keys frame 1 too)", "a bake on the contract path (scale curves stripped, delta-form rebase)",
        "no static mesh (StaticRoot has no anchor)", "StaticRoot anchored to a mesh that has no parent", "StaticRoot anchored to a static mesh's parent",
        "a root-motion anchor (the armature parented for the bake)", "the biggest part does not travel (no anchor)", "no mesh rides a bone (no travel measured)",
        "a bone under its part's parent's bone", "a part whose parent is no part (a root bone)",
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
        int fails = 0, files = 0, left = 0; long matrices = 0, objects = 0, lines = 0, bones = 0, curves = 0, keysCompared = 0, after = 0;
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
            var problems = new List<string>(); string bakeLeftReason = null; bool bakeClamped = false;
            try
            {
                if (!jobs.TryGetValue(key, out var job)) throw new InvalidDataException("the dump has a job the jobs file does not");
                var rows = block.Select(l => l.Split('\t')).ToList();
                if (!rows.Any(t => t[0] == "DONE")) throw new InvalidDataException(rows.Any(t => t[0] == "FAIL") ? "Blender could not run it: " + rows.First(t => t[0] == "FAIL").Last() : "the dump has no DONE row");
                List<string[]> Of(string k) => rows.Where(t => t[0] == k).ToList();
                var log = block.Where(l => l.StartsWith("LOG\t")).Select(l => l.Substring(4)).ToList();
                var exits = Of("EXIT");
                bool exit = exits.Count > 0;
                // The no-parts guard aborts with failure: a successful SystemExit is not the same decision.
                if (exit && (exits.Count != 1 || exits[0].Length != 2 || exits[0][1] != "1"))
                    throw new InvalidDataException("the dump has an invalid EXIT row (expected exactly one EXIT with code 1)");
                if (!exit) foreach (string k in new[] { "RANGE", "NORM", "FLAG", "ARM", "ANCHOR", "HULL", "PINV" }) if (Of(k).Count != 1) throw new InvalidDataException($"the dump has {Of(k).Count} {k} rows");
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
                // twice: the scene the dump shows is the one BEFORE the bake (the script's state at `bpy.ops.nla.bake(`);
                // the baked action and the armature after it come from the run that goes on (`baked`)
                var r = BlenderDeploy.Decide(m, argv);
                var baked = r.Fallback == null && !r.Exit ? BlenderDeploy.Decide(m, argv, null, true) : r;
                // a job may be Blender's from the BAKE on (its key says "BAKELEFT:"): the scene before it is still held
                bool expectBakeLeft = key.StartsWith("BAKELEFT:", StringComparison.Ordinal), bakeLeft = r.Fallback == null && baked.Fallback != null;
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
                    // ---- the armature (part 3): its name, which bone each part rides, the bones at rest, the anchors
                    if (Of("ARM")[0][1] != r.Armature.Name) problems.Add($"the armature is '{r.Armature.Name}' here, '{Of("ARM")[0][1]}' in Blender");
                    Compare(problems, "bone of each part", r.BoneOf.Select(b => b.part + "\t" + b.bone).ToList(), Of("BONEOF").Select(t => t[1] + "\t" + t[2]).ToList());
                    var rb = Of("RBONE");
                    foreach (var t in rb) if (t.Length != 26) throw new InvalidDataException($"the dump's bone row for '{t[1]}' has {t.Length} fields, expected 26");
                    if (rb.Select(t => t[1]).Distinct().Count() != rb.Count) throw new InvalidDataException("the dump has a bone twice");
                    // by name: arm.data.bones lists a bone after its parent, not in creation order (the order is the export's, part 5)
                    Compare(problems, "bones", r.Bones.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(), rb.Select(t => t[1]).OrderBy(n => n, StringComparer.Ordinal).ToList());
                    long wrongBones = 0; string firstBone = null;
                    foreach (var t in rb)
                    {
                        var b = r.Bones.FirstOrDefault(x => x.Name == t[1]);
                        if (b == null) continue;
                        var mine = new List<string> { b.Parent != null ? b.Parent.Name : "-" };
                        mine.AddRange(b.Head.Select(H)); mine.AddRange(b.Tail.Select(H)); mine.Add(H(b.Length));
                        for (int row = 0; row < 4; row++) for (int c = 0; c < 4; c++) mine.Add(H(b.MatrixLocal[c * 4 + row]));
                        bones++;
                        int at = -1; for (int i = 0; i < mine.Count && at < 0; i++) if (mine[i] != t[2 + i]) at = i;
                        if (at >= 0) { wrongBones++; firstBone = firstBone ?? $"'{b.Name}' field {at} ({(at == 0 ? "parent" : at < 4 ? "head" : at < 7 ? "tail" : at == 7 ? "length" : "matrix_local")}): here {mine[at]}, Blender {t[2 + at]}"; }
                    }
                    if (wrongBones > 0) problems.Add($"{wrongBones} of {rb.Count} bones at rest differ, first {firstBone}");
                    string anchor = r.StaticAnchor != null ? r.StaticAnchor.Name : "-";
                    if (anchor != Of("ANCHOR")[0][1]) problems.Add($"StaticRoot's anchor is '{anchor}' here, '{Of("ANCHOR")[0][1]}' in Blender");
                    var hr = Of("HULL")[0];
                    string hullMine = (r.Hull != null ? r.Hull.Name : "-") + " " + (r.TravelMeasured ? H64(r.Travel) + " " + H64(r.ModelSize) : "- -");
                    if (hullMine != hr[1] + " " + hr[2] + " " + hr[3]) problems.Add($"the root-motion anchor: here {hullMine}, Blender {hr[1]} {hr[2]} {hr[3]} (anchor, travel, model size)");
                    var pinv = r.Armature.ParentInverse ?? new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
                    if (!Enumerable.Range(0, 16).Select(i => H(pinv[(i % 4) * 4 + i / 4])).SequenceEqual(Of("PINV")[0].Skip(1))) problems.Add("the armature's matrix_parent_inverse differs");
                    if (bakeLeft != expectBakeLeft) problems.Add(bakeLeft ? $"left to Blender at the bake, which the jobs file does not expect: {baked.Fallback}" : "the jobs file expects this left to Blender at the bake (BAKELEFT:), and it is baked here");
                    if (bakeLeft) bakeLeftReason = baked.Fallback;
                    // ---- the bake (part 4): the action the script holds at its step 5a - every fcurve, key by key
                    if (!bakeLeft)
                    {
                    var log2 = block.Where(l => l.StartsWith("LOG2\t")).Select(l => l.Substring(5)).ToList();
                    for (int i = 0; i < Math.Max(log2.Count, baked.BakeLog.Count); i++)
                    {
                        string theirs = i < log2.Count ? log2[i] : "(no line)", mine = i < baked.BakeLog.Count ? baked.BakeLog[i] : "(no line)";
                        if (theirs != mine) { problems.Add($"bake log line {i + 1}: here «{Cut(mine)}», Blender «{Cut(theirs)}»"); break; }
                    }
                    lines += log2.Count;
                    if (Of("ACT").Count != 1 || Of("M2").Count != 1) throw new InvalidDataException("the dump has no single ACT and M2 row");
                    var fcs = Of("FC");
                    var seenCurves = new HashSet<string>(StringComparer.Ordinal);
                    long wrongKeys = 0, wrongCurves = 0; string firstKey = null;
                    int nf = baked.BakeFrameMax - baked.BakeFrameMin + 1;
                    if (baked.BakeFrameMax != r.FrameMax) bakeClamped = true;
                    foreach (var t in fcs)
                    {
                        // pose.bones["<name, escaped>"].<channel>
                        const string head = "pose.bones[\"";
                        int close = t[1].LastIndexOf("\"].", StringComparison.Ordinal);
                        if (!t[1].StartsWith(head, StringComparison.Ordinal) || close < 0) throw new InvalidDataException($"the dump has a curve that is no pose bone's: {t[1]}");
                        string bone = t[1].Substring(head.Length, close - head.Length).Replace("\\\"", "\"").Replace("\\\\", "\\"), channel = t[1].Substring(close + 3);
                        int index = int.Parse(t[2]);
                        // the component index must be one the channel has: location[3] would otherwise land in the quaternion's slot
                        if (index < 0 || index >= (channel == "rotation_quaternion" ? 4 : 3)) throw new InvalidDataException($"the dump has a curve with a component its channel does not have: {t[1]}[{index}]");
                        int at = channel == "location" ? index : channel == "rotation_quaternion" ? 3 + index : channel == "scale" ? 7 + index : -1;
                        if (at < 0) throw new InvalidDataException($"the dump has a curve of an unknown channel: {t[1]}");
                        if (!seenCurves.Add(bone + "\t" + at)) throw new InvalidDataException($"the dump has a curve twice: {t[1]}[{index}]");
                        curves++;
                        if (!baked.Keys.TryGetValue(bone, out var keys) || (at >= 7 && !baked.ScaleKeys)) { wrongCurves++; firstKey = firstKey ?? $"Blender has the curve {t[1]}[{index}], not here"; continue; }
                        if (t[3] != "LINEAR") { wrongCurves++; firstKey = firstKey ?? $"the curve {t[1]}[{index}] is {t[3]}"; continue; }
                        if (t.Length - 4 != nf) { wrongCurves++; firstKey = firstKey ?? $"the curve {t[1]}[{index}] has {t.Length - 4} keys in Blender, {nf} here"; continue; }
                        for (int k = 0; k < nf; k++)
                        {
                            keysCompared++;
                            string mine = H((float)(baked.BakeFrameMin + k)) + ":" + H(keys[k][at]);
                            if (mine != t[4 + k])
                            {
                                wrongKeys++;
                                if (firstKey == null) { var th = t[4 + k].Split(':'); firstKey = $"'{bone}' {channel}[{index}] at frame {baked.BakeFrameMin + k}: here {Show(keys[k][at])} ({H(keys[k][at])}), Blender {Show(F(th[1]))} ({th[1]})"; }
                            }
                        }
                    }
                    int expectCurves = baked.Keys.Count * (baked.ScaleKeys ? 10 : 7);
                    if (fcs.Count != expectCurves) problems.Add($"{expectCurves} curves here, {fcs.Count} in Blender");
                    if (wrongCurves > 0 || wrongKeys > 0) problems.Add($"{wrongKeys} baked keys and {wrongCurves} curves differ, first {firstKey}");
                    if (!Enumerable.Range(0, 16).Select(i => H(baked.Armature.World[(i % 4) * 4 + i / 4])).SequenceEqual(Of("M2")[0].Skip(1))) problems.Add("the armature's matrix_world after the bake differs");
                    if ((baked.Armature.Parent != null ? baked.Armature.Parent.Name : "-") != Of("ACT")[0][2]) problems.Add("the armature's parent after the bake differs");
                    // the scene the bake leaves: every object's matrix_world again
                    var o2 = Of("O2").ToDictionary(t => t[1], t => t, StringComparer.Ordinal);
                    if (o2.Count != baked.Objects.Count) problems.Add($"{baked.Objects.Count} objects after the bake here, {o2.Count} in Blender");
                    long wrongAfter = 0; string firstAfter = null;
                    foreach (var o in baked.Objects)
                    {
                        if (!o2.TryGetValue(o.Name, out var row) || row.Length != 18) throw new InvalidDataException($"the dump has no complete matrix row after the bake for '{o.Name}'");
                        bool ok = true;
                        for (int rr = 0; rr < 4; rr++) for (int c = 0; c < 4; c++) if (H(o.World[c * 4 + rr]) != row[2 + rr * 4 + c]) ok = false;
                        after++;
                        if (!ok) { wrongAfter++; firstAfter = firstAfter ?? $"'{o.Name}'"; }
                    }
                    if (wrongAfter > 0) problems.Add($"{wrongAfter} of {baked.Objects.Count} matrices after the bake differ, first {firstAfter}");
                    }
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
                if (!r.Exit)
                {
                    if (r.StaticAnchor == null) cover["no static mesh (StaticRoot has no anchor)"]++;
                    else if (r.StaticAnchor.Type == "MESH") cover["StaticRoot anchored to a mesh that has no parent"]++;
                    else cover["StaticRoot anchored to a static mesh's parent"]++;
                    if (r.Hull != null) cover["a root-motion anchor (the armature parented for the bake)"]++;
                    else if (r.TravelMeasured) cover["the biggest part does not travel (no anchor)"]++;
                    else cover["no mesh rides a bone (no travel measured)"]++;
                    if (r.Bones.Any(b => b.Parent != null)) cover["a bone under its part's parent's bone"]++;
                    if (r.Bones.Any(b => b.Part != null && b.Part.Parent != null && b.Parent == null)) cover["a part whose parent is no part (a root bone)"]++;
                }
                if (bakeLeftReason != null) { cover["left to Blender at the bake (the scene before it is held)"]++; Console.WriteLine($"BAKELEFT {key}: {bakeLeftReason}"); }
                if (bakeClamped) cover["a clip of frame 0 alone (the bake keys frame 1 too)"]++;
                if (bakeLeftReason == null && !r.Exit) cover[r.Legacy ? "a bake on the legacy path" : "a bake on the contract path (scale curves stripped, delta-form rebase)"]++;
                Console.WriteLine(r.Exit ? $"PASS {key}: {log.Count} log lines equal to Blender's, and the script stops there as it does here (no scene is compared)"
                                         : $"PASS {key}: {log.Count} log lines, {r.Parts.Count} parts, {r.Objects.Count} objects with their matrices, transforms and boxes equal to Blender's");
            }
            catch (Exception e) { fails++; Console.WriteLine($"FAIL {key}: {e.GetType().Name}: {e.Message}"); }
        }
        // a job the jobs file has and no dump holds was not judged at all
        var dumped = new HashSet<string>(blocks.Select(b => b[0].Split('\t')[1]), StringComparer.Ordinal);
        foreach (var k in jobs.Keys) if (!dumped.Contains(k)) { fails++; Console.WriteLine($"FAIL {k}: the dump holds no such job"); }
        foreach (var kv in cover) Console.WriteLine($"COVER {kv.Value} {kv.Key}");
        Console.WriteLine($"TOTAL jobs {files} failed {fails} left {left} objects {objects} matrices {matrices} lines {lines} bones {bones} curves {curves} keys {keysCompared} after {after}");
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
