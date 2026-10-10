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
        "a bake on the legacy path", "a bone re-keyed by the barrel retarget or the leg scale (5b, 5c)", "a frame set between two Bezier keys of a re-keyed bone", "a recoil tail keyed on a RecoilArm (5d)", "the recoil step's exit: no barrel or cannon to pick", "a fire-window snapshot (5a)", "no recoil range: no snapshot", "a custom property keyed by the bake (not modelled: it moves nothing)", "an imported armature baked with it (its bones keyed a frame, its own animation dropped)", "a clip of frame 0 alone (the bake keys frame 1 too)", "a bake on the contract path (scale curves stripped, delta-form rebase)",
        "no static mesh (StaticRoot has no anchor)", "StaticRoot anchored to a mesh that has no parent", "StaticRoot anchored to a static mesh's parent",
        "a root-motion anchor (the armature parented for the bake)", "the biggest part does not travel (no anchor)", "no mesh rides a bone (no travel measured)",
        "a bone under its part's parent's bone", "a part whose parent is no part (a root bone)",
        "a mesh bound to its own bone (6)", "a mesh bound to an ancestor's bone", "a static mesh bound to StaticRoot", "a shared mesh datablock copied at the bind",
        "a bound mesh with custom normals", "a bound mesh without normals", "no mesh to bind",
        "a vertex group named after a pair-merged part (no bone of that name)",
    };

    static uint Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
    static float F(string hex) => BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(hex, 16)), 0);
    static string H(float f) => Bits(f).ToString("x8");
    static string H64(double d) => BitConverter.DoubleToInt64Bits(d).ToString("x16");
    static string Sha(byte[] bytes) { using (var sha = System.Security.Cryptography.SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2"))); }
    static string ShaFloats(float[] v) { var b = new byte[4 * v.Length]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, 4 * i); return Sha(b); }   // little-endian float32, as the dump hashes them
    static string ShaShorts(short[] v) { var b = new byte[2 * v.Length]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, 2 * i); return Sha(b); }

    public static int Run(string[] args)
    {
        BlenderFCurve.Hits = new int[BlenderFCurve.Branches.Length];
        var jobs = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(args[0]))
        {
            if (line.Trim().Length == 0) continue;
            var t = line.TrimEnd('\r').Split('|');
            jobs[t[0]] = t;
        }
        var cover = new SortedDictionary<string, long>(); foreach (var k in CoverKeys) cover[k] = 0;
        int fails = 0, files = 0, left = 0; long matrices = 0, objects = 0, lines = 0, bones = 0, curves = 0, keysCompared = 0, after = 0, importedKeys = 0, held2 = 0, atLast = 0, snapped = 0, rekeyed = 0, swept = 0, recoilRows = 0, boundMeshes = 0, vertices = 0;
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
            var problems = new List<string>(); string bakeLeftReason = null; bool bakeClamped = false, rebakedArmature = false, fireSnap = false, retargeted = false, betweenKeys = false, recoiled = false, recoilExit = false;
            bool boundOwn = false, boundAncestor = false, boundStatic = false, copiedData = false, boundNormals = false, boundPlain = false, noMesh = false, groupNoBone = false;
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
                    void CompareBoneRows(string kind, string what, List<BlenderDeploy.Bone> bonesHere)
                    {
                        var rb = Of(kind);
                        foreach (var t in rb) if (t.Length != 26) throw new InvalidDataException($"the dump's bone row for '{t[1]}' has {t.Length} fields, expected 26");
                        if (rb.Select(t => t[1]).Distinct().Count() != rb.Count) throw new InvalidDataException("the dump has a bone twice");
                        // by name: arm.data.bones lists a bone after its parent, not in creation order (the order is the export's, part 5)
                        Compare(problems, what, bonesHere.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(), rb.Select(t => t[1]).OrderBy(n => n, StringComparer.Ordinal).ToList());
                        long wrongBones = 0; string firstBone = null;
                        foreach (var t in rb)
                        {
                            var b = bonesHere.FirstOrDefault(x => x.Name == t[1]);
                            if (b == null) continue;
                            var mine = new List<string> { b.Parent != null ? b.Parent.Name : "-" };
                            mine.AddRange(b.Head.Select(H)); mine.AddRange(b.Tail.Select(H)); mine.Add(H(b.Length));
                            for (int row = 0; row < 4; row++) for (int c = 0; c < 4; c++) mine.Add(H(b.MatrixLocal[c * 4 + row]));
                            bones++;
                            int at = -1; for (int i = 0; i < mine.Count && at < 0; i++) if (mine[i] != t[2 + i]) at = i;
                            if (at >= 0) { wrongBones++; firstBone = firstBone ?? $"'{b.Name}' field {at} ({(at == 0 ? "parent" : at < 4 ? "head" : at < 7 ? "tail" : at == 7 ? "length" : "matrix_local")}): here {mine[at]}, Blender {t[2 + at]}"; }
                        }
                        if (wrongBones > 0) problems.Add($"{wrongBones} of {rb.Count} {what} differ, first {firstBone}");
                    }
                    CompareBoneRows("RBONE", "bones", r.Bones);
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
                    // ... and the curves the bake gave every other armature that was selected (the imported ones that survived)
                    var fca = Of("FCA"); int customCurves = 0;
                    var seenImported = new HashSet<string>(StringComparer.Ordinal);
                    long importedExpected = baked.ImportedKeys.Values.Sum(a => (long)a.Count) * (baked.ScaleKeys ? 10 : 7);
                    foreach (var t in fca)
                    {
                        const string head = "pose.bones[\"";
                        int close = t[2].LastIndexOf("\"].", StringComparison.Ordinal);
                        // a CUSTOM PROPERTY (the node's `extras`): the bake keys it too (channel_types has PROPS). It moves nothing
                        // and the script clears the action later - not modelled, counted, and the comparison goes on
                        if (t[2].EndsWith("\"]", StringComparison.Ordinal)) { customCurves++; continue; }
                        if (!t[2].StartsWith(head, StringComparison.Ordinal) || close < 0) { problems.Add($"'{t[1]}' has a curve after the bake that is no pose bone's: {t[2]} (the armature object's own animation should be gone)"); break; }
                        string bone = t[2].Substring(head.Length, close - head.Length).Replace("\\\"", "\"").Replace("\\\\", "\\"), channel = t[2].Substring(close + 3);
                        int index = int.Parse(t[3]);
                        if (index < 0 || index >= (channel == "rotation_quaternion" ? 4 : 3)) throw new InvalidDataException($"the dump has a curve with a component its channel does not have: {t[2]}[{index}]");
                        int at = channel == "location" ? index : channel == "rotation_quaternion" ? 3 + index : channel == "scale" ? 7 + index : -1;
                        if (at < 0) throw new InvalidDataException($"the dump has a curve of an unknown channel: {t[2]}");
                        if (!seenImported.Add(t[1] + "\t" + bone + "\t" + at)) throw new InvalidDataException($"the dump has a curve twice: {t[1]} {t[2]}[{index}]");
                        curves++;
                        if (!baked.ImportedKeys.TryGetValue(t[1], out var ofArm) || !ofArm.TryGetValue(bone, out var keys) || (at >= 7 && !baked.ScaleKeys)) { wrongCurves++; firstKey = firstKey ?? $"Blender has the curve {t[2]}[{index}] on '{t[1]}', not here"; continue; }
                        if (t[4] != "LINEAR" || t.Length - 5 != nf) { wrongCurves++; firstKey = firstKey ?? $"the curve {t[2]}[{index}] of '{t[1]}' is {t[4]} with {t.Length - 5} keys in Blender, LINEAR with {nf} here"; continue; }
                        for (int k = 0; k < nf; k++)
                        {
                            keysCompared++; importedKeys++;
                            string mine = H((float)(baked.BakeFrameMin + k)) + ":" + H(keys[k][at]);
                            if (mine != t[5 + k])
                            {
                                wrongKeys++;
                                if (firstKey == null) { var th = t[5 + k].Split(':'); firstKey = $"'{t[1]}' bone '{bone}' {channel}[{index}] at frame {baked.BakeFrameMin + k}: here {Show(keys[k][at])} ({H(keys[k][at])}), Blender {Show(F(th[1]))} ({th[1]})"; }
                            }
                        }
                    }
                    if (customCurves > 0) cover["a custom property keyed by the bake (not modelled: it moves nothing)"]++;
                    if (fca.Count - customCurves != importedExpected) problems.Add($"{importedExpected} curves on the imported armatures after the bake here, {fca.Count} in Blender");
                    if (baked.ImportedKeys.Count > 0) rebakedArmature = true;
                    // what every imported pose bone holds after the bake
                    var pb2 = Of("PB2"); long wrongHeld = 0; string firstHeld = null;
                    ValidatePoseRows(pb2, baked.ImportedPose, "PB2");
                    foreach (var t in pb2)
                    {
                        var held = baked.ImportedPose[t[1]][t[2]];
                        held2++;
                        var mine = held == null ? null : held[0].Concat(held[1]).Concat(held[2]).Select(H).ToList();
                        if (mine == null || !mine.SequenceEqual(t.Skip(3))) { wrongHeld++; firstHeld = firstHeld ?? $"'{t[2]}' of '{t[1]}'"; }
                    }
                    if (wrongHeld > 0) problems.Add($"{wrongHeld} of {pb2.Count} imported pose bones hold other values after the bake, first {firstHeld}");
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
                        bool ok = true; var afterBake = baked.AfterBake[o.Name];
                        for (int rr = 0; rr < 4; rr++) for (int c = 0; c < 4; c++) if (H(afterBake[c * 4 + rr]) != row[2 + rr * 4 + c]) ok = false;
                        after++;
                        if (!ok) { wrongAfter++; firstAfter = firstAfter ?? $"'{o.Name}'"; }
                    }
                    if (wrongAfter > 0) problems.Add($"{wrongAfter} of {baked.Objects.Count} matrices after the bake differ, first {firstAfter}");
                    // ---- 5a, the fire-window snapshot (part 5a): its log line, every bone at every frame of the window, what
                    //      the new armature's pose bones hold afterwards and where the scene stands
                    var log3 = block.Where(l => l.StartsWith("LOG3\t")).Select(l => l.Substring(5)).ToList();
                    Compare(problems, "the fire window's log", baked.FireLog, log3);
                    lines += log3.Count;
                    var snap = Of("SNAP"); var seenSnap = new HashSet<(int, string)>(); long wrongSnap = 0; string firstSnap = null;
                    foreach (var t in snap)
                    {
                        if (t.Length != 10) throw new InvalidDataException($"the dump's snapshot row has {t.Length} fields, expected 10");
                        int f = int.Parse(t[1]);
                        if (!baked.FireSnap.TryGetValue(f, out var atFrame) || !atFrame.TryGetValue(t[2], out var mine)) throw new InvalidDataException($"the dump has an unknown snapshot row: frame {f}, bone '{t[2]}'");
                        if (!seenSnap.Add((f, t[2]))) throw new InvalidDataException($"the dump has a snapshot row twice: frame {f}, bone '{t[2]}'");
                        snapped++;
                        if (!mine.Select(H).SequenceEqual(t.Skip(3))) { wrongSnap++; firstSnap = firstSnap ?? $"'{t[2]}' at frame {f}: here [{string.Join(",", mine.Select(Show))}] Blender [{string.Join(",", t.Skip(3).Select(h => Show(F(h))))}]"; }
                    }
                    foreach (var kv in baked.FireSnap) foreach (var bone in kv.Value.Keys) if (!seenSnap.Contains((kv.Key, bone))) throw new InvalidDataException($"the dump has no snapshot row for frame {kv.Key}, bone '{bone}'");
                    if (wrongSnap > 0) problems.Add($"{wrongSnap} of {snap.Count} snapshot rows differ, first {firstSnap}");
                    if (baked.FireSnap.Count > 0) fireSnap = true;
                    var apb = Of("APB"); var seenApb = new HashSet<string>(StringComparer.Ordinal); long wrongApb = 0; string firstApb = null;
                    foreach (var t in apb)
                    {
                        if (t.Length != 12) throw new InvalidDataException($"the dump's armature pose row has {t.Length} fields, expected 12");
                        if (!baked.ArmPoseAfterFire.TryGetValue(t[1], out var mine)) throw new InvalidDataException($"the dump has an unknown armature pose row: bone '{t[1]}'");
                        if (!seenApb.Add(t[1])) throw new InvalidDataException($"the dump has an armature pose row twice: bone '{t[1]}'");
                        if (!mine.Select(H).SequenceEqual(t.Skip(2))) { wrongApb++; firstApb = firstApb ?? $"'{t[1]}': here [{string.Join(",", mine.Select(Show))}] Blender [{string.Join(",", t.Skip(2).Select(h => Show(F(h))))}]"; }
                    }
                    foreach (var bone in baked.ArmPoseAfterFire.Keys) if (!seenApb.Contains(bone)) throw new InvalidDataException($"the dump has no armature pose row for bone '{bone}'");
                    if (wrongApb > 0) problems.Add($"{wrongApb} of {apb.Count} pose bones of the new armature hold other values after the fire window, first {firstApb}");
                    var o4 = Of("O4"); var seenO4 = new HashSet<string>(StringComparer.Ordinal); long wrongO4 = 0; string firstO4 = null;
                    foreach (var t in o4)
                    {
                        if (t.Length != 18) throw new InvalidDataException($"the dump's object row after the fire window has {t.Length} fields, expected 18");
                        if (!baked.AfterFire.TryGetValue(t[1], out var mine)) throw new InvalidDataException($"the dump has an unknown object row after the fire window: '{t[1]}'");
                        if (!seenO4.Add(t[1])) throw new InvalidDataException($"the dump has an object row after the fire window twice: '{t[1]}'");
                        bool ok = true;
                        for (int rr = 0; rr < 4; rr++) for (int c = 0; c < 4; c++) if (H(mine[c * 4 + rr]) != t[2 + rr * 4 + c]) ok = false;
                        if (!ok) { wrongO4++; firstO4 = firstO4 ?? $"'{t[1]}'"; }
                    }
                    foreach (var name in baked.AfterFire.Keys) if (!seenO4.Contains(name)) throw new InvalidDataException($"the dump has no object row after the fire window for '{name}'");
                    if (wrongO4 > 0) problems.Add($"{wrongO4} of {o4.Count} matrices after the fire window differ, first {firstO4}");
                    // ---- 5b, 5c (the barrel retarget, the leg scale): their log lines; the action again, every key of every
                    //      curve - a baked key by frame and value, a Bezier key with its handles too; what the pose bones hold
                    //      afterwards; where the scene stands
                    var log4 = block.Where(l => l.StartsWith("LOG4\t")).Select(l => l.Substring(5)).ToList();
                    Compare(problems, "the retarget's log", baked.RetargetLog, log4);
                    lines += log4.Count;
                    // the curves of the action: a baked key by frame and value, a Bezier key with its handles too - every
                    // curve of every bone in `allowed` must be there, none of any other
                    void CompareCurveRows(string kind, string when, HashSet<string> allowed)
                    {
                        var fc4 = Of(kind); var seen4 = new HashSet<(string, int)>(); long wrong4 = 0, keys4 = 0; string first4 = null;
                        int nfB = baked.BakeFrameMax - baked.BakeFrameMin + 1;
                        foreach (var t in fc4)
                        {
                            const string head = "pose.bones[\"";
                            int close = t[1].LastIndexOf("\"].", StringComparison.Ordinal);
                            if (!t[1].StartsWith(head, StringComparison.Ordinal) || close < 0) throw new InvalidDataException($"the dump has a curve {when} that is no pose bone's: {t[1]}");
                            string bone = t[1].Substring(head.Length, close - head.Length).Replace("\\\"", "\"").Replace("\\\\", "\\"), channel = t[1].Substring(close + 3);
                            int index = int.Parse(t[2]);
                            if (index < 0 || index >= (channel == "rotation_quaternion" ? 4 : 3)) throw new InvalidDataException($"the dump has a curve with a component its channel does not have: {t[1]}[{index}]");
                            int at = channel == "location" ? index : channel == "rotation_quaternion" ? 3 + index : channel == "scale" ? 7 + index : -1;
                            if (at < 0) throw new InvalidDataException($"the dump has a curve of an unknown channel: {t[1]}");
                            if (!allowed.Contains(bone)) throw new InvalidDataException($"the dump has a curve {when} of an unknown bone: {t[1]}");
                            if (!seen4.Add((bone, at))) throw new InvalidDataException($"the dump has a curve {when} twice: {t[1]}[{index}]");
                            if (t[3] != "CONSTANT") { wrong4++; first4 = first4 ?? $"{t[1]}[{index}] extrapolates {t[3]}"; continue; }
                            // a new curve takes its smoothing from the user preference; the handles ported are CONT_ACCEL's
                            if (t.Length < 5 || (t[4] != "CONT_ACCEL" && t[4] != "NONE")) throw new InvalidDataException($"the dump has a curve without its smoothing: {t[1]}[{index}]");
                            if (baked.Rekeyed.ContainsKey(bone) && t[4] != "CONT_ACCEL") { wrong4++; first4 = first4 ?? $"{t[1]}[{index}] smooths {t[4]}"; continue; }
                            var theirs = t.Skip(5).Select(k => k.Split(':')).ToList();
                            if (theirs.Any(k => k.Length != 9)) throw new InvalidDataException($"the dump has a malformed key on {t[1]}[{index}]");
                            if (baked.Rekeyed.TryGetValue(bone, out var channels))
                            {
                                var mine = channels[at];
                                if (mine == null || mine.Count != theirs.Count) { wrong4++; first4 = first4 ?? $"{t[1]}[{index}] has {theirs.Count} keys in Blender, {(mine == null ? "no curve" : mine.Count + " keys")} here"; continue; }
                                for (int k = 0; k < mine.Count; k++)
                                {
                                    keys4++; rekeyed++;
                                    string mineKey = $"{H(mine[k].Frame)}:{H(mine[k].Value)}:BEZIER:AUTO_CLAMPED:AUTO_CLAMPED:{H(mine[k].LeftX)}:{H(mine[k].LeftY)}:{H(mine[k].RightX)}:{H(mine[k].RightY)}";
                                    if (mineKey != string.Join(":", theirs[k])) { wrong4++; first4 = first4 ?? $"'{bone}' {channel}[{index}] key {k}: here {mineKey}, Blender {string.Join(":", theirs[k])}"; break; }
                                }
                            }
                            else
                            {
                                var keys = baked.Keys[bone];
                                if ((at >= 7 && !baked.ScaleKeys) || theirs.Count != nfB) { wrong4++; first4 = first4 ?? $"{t[1]}[{index}] has {theirs.Count} keys in Blender, {(at >= 7 && !baked.ScaleKeys ? 0 : nfB)} here"; continue; }
                                for (int k = 0; k < nfB; k++)
                                {
                                    keys4++;
                                    if (theirs[k][0] != H((float)(baked.BakeFrameMin + k)) || theirs[k][1] != H(keys[k][at]) || theirs[k][2] != "LINEAR") { wrong4++; first4 = first4 ?? $"'{bone}' {channel}[{index}] at frame {baked.BakeFrameMin + k}"; break; }
                                }
                            }
                        }
                        // every curve the port holds must be in the dump: a re-keyed channel with keys, a baked bone's seven or ten
                        foreach (var b in allowed)
                            for (int at = 0; at < 10; at++)
                            {
                                bool has = baked.Rekeyed.TryGetValue(b, out var ch) ? ch[at] != null && ch[at].Count > 0 : at < 7 || baked.ScaleKeys;
                                if (has && !seen4.Contains((b, at))) throw new InvalidDataException($"the dump has no curve {when} for bone '{b}', channel {at}");
                                if (!has && seen4.Contains((b, at))) { wrong4++; first4 = first4 ?? $"Blender still has a curve on '{b}', channel {at}; it is cleared here"; }
                            }
                        if (wrong4 > 0) problems.Add($"{wrong4} curves {when} differ ({keys4} keys compared), first {first4}");
                        if (baked.Rekeyed.Count > 0) retargeted = true;
                    }
                    CompareCurveRows("FC4", "after the retarget", new HashSet<string>(baked.Keys.Keys, StringComparer.Ordinal));
                    ComparePoseRows(problems, Of("APB4"), baked.ArmPoseAfterRetarget, "after the retarget");
                    CompareObjectRows(problems, Of("O5"), baked.AfterRetarget, "after the retarget");
                    // ---- 5d (the recoil tail): its log; the script's own exit when no tube is there to pick; what it measured
                    //      on the way (every matrix, vector and angle its variables hold); the bones rebuilt from their edit
                    //      bones with the RecoilArm among them; the action with the arm's curves; what the pose bones hold,
                    //      their pose matrices; the scene
                    var log5 = block.Where(l => l.StartsWith("LOG5\t")).Select(l => l.Substring(5)).ToList();
                    Compare(problems, "the recoil step's log", baked.RecoilLog, log5);
                    lines += log5.Count;
                    var rec = baked.Recoil;
                    var exit5 = Of("EXIT5"); var dies5 = Of("DIES5");
                    // (an exception: nothing after it can be judged - the sweep the dump makes is the one without a tail)
                    if (dies5.Count > 0) throw new InvalidDataException($"the script died in the recoil step ({string.Join(" ", dies5[0].Skip(1))}) and the port went on");
                    if (exit5.Count > 1) throw new InvalidDataException("the dump has two exits for the recoil step");
                    if (exit5.Count == 1 && (exit5[0].Length != 2 || exit5[0][1] != "1")) throw new InvalidDataException("the dump's recoil exit is not the script's exit(1)");
                    if ((exit5.Count == 1) != baked.ExitAtRecoil) problems.Add(exit5.Count == 1 ? "the script exits in the recoil step (no tube to pick); the port goes on" : "the port exits in the recoil step (no tube to pick); the script goes on");
                    var r5 = Of("R5").Select(t => string.Join("\t", t.Skip(1))).ToList();
                    var mine5 = new List<string>();
                    if (rec != null && !baked.ExitAtRecoil)
                    {
                        string M16(float[] it) => string.Join("\t", it.Select(H));
                        string V3(float[] v) => string.Join("\t", v.Select(H));
                        mine5.Add($"frames\t{rec.Rs}\t{rec.Re}\t{rec.Step}\t{string.Join(",", rec.Frames)}");
                        mine5.Add($"names\t{rec.Driver}\t{rec.Cradle}\t{rec.TubeRoot}\t{rec.ArmName}\t{string.Join(",", rec.Ordered)}");
                        mine5.Add($"scalars\t{H64(rec.Mag)}\t{H64(rec.Dist)}\t{H64(rec.R)}\t{rec.DeployEnd}\t{rec.KickEnd}\t{rec.OutEnd}");
                        foreach (string bn in rec.Ordered) { mine5.Add($"home\t{bn}\t{M16(rec.Home[bn])}"); mine5.Add($"aim\t{bn}\t{M16(rec.Aim[bn])}"); }
                        foreach (string bn in rec.SrcOrder) foreach (int t in rec.Frames.Distinct()) mine5.Add($"src\t{bn}\t{t}\t{M16(rec.Src[bn][t])}");
                        foreach (int t in rec.SlideOrder) mine5.Add($"slide\t{t}\t{V3(rec.Slide[t])}");
                        mine5.Add($"vec\tpeak\t{V3(rec.Peak)}"); mine5.Add($"vec\td\t{V3(rec.D)}"); mine5.Add($"vec\tA\t{V3(rec.A)}"); mine5.Add($"vec\tradius\t{V3(rec.Radius)}");
                        mine5.Add($"vec\ttube_head\t{V3(rec.TubeHead)}"); mine5.Add($"vec\tpivot\t{V3(rec.Pivot)}"); mine5.Add($"vec\tA_local\t{V3(rec.ALocal)}");
                        mine5.Add($"cbar\t{string.Join("\t", rec.Cbar3.Select(H))}");
                        mine5.Add($"thetas\t{string.Join("\t", rec.Thetas.Select(H64))}");
                    }
                    Compare(problems, "the recoil step's measurements", mine5, r5);
                    recoilRows += r5.Count;
                    CompareBoneRows("RBONE5", "bones after the recoil step", baked.BonesAfterRecoil);
                    CompareCurveRows("FC5", "after the recoil", new HashSet<string>(baked.Keys.Keys.Concat(baked.Rekeyed.Keys), StringComparer.Ordinal));
                    ComparePoseRows(problems, Of("APB5"), baked.ArmPoseAfterRecoil, "after the recoil");
                    // pb.matrix is what the LAST evaluation left, not the properties (a bone whose scale curves the scale-free
                    // step dropped still shows the baked scale until a frame is set): the dump writes the pose matrices only
                    // where the recoil step evaluated last - after its frame_set and its view_layer.update()
                    if (rec != null && !baked.ExitAtRecoil) CompareObjectRows(problems, Of("PM5"), BlenderDeploy.PoseMatrices(baked.BonesAfterRecoil, baked.ArmPoseAfterRecoil), "(pose matrices) after the recoil");
                    else if (Of("PM5").Count > 0) throw new InvalidDataException("the dump has pose matrices for a job without a recoil tail (they would be stale)");
                    CompareObjectRows(problems, Of("O6"), baked.AfterRecoil, "after the recoil");
                    if (rec != null && !baked.ExitAtRecoil) recoiled = true;
                    if (baked.ExitAtRecoil) recoilExit = true;
                    // ---- 6 (the bind): its log; the script's death there; each bound mesh in the script's order (its bone, its
                    //      datablock and that one's users, its parent and own transform, its parent inverse), its vertex groups
                    //      with the weights' census, its modifiers, its vertices (every float32 hashed, a sample in the clear),
                    //      its custom normals (the attribute unchanged), every mesh datablock's users, the scene after an update
                    var log6 = block.Where(l => l.StartsWith("LOG6\t")).Select(l => l.Substring(5)).ToList();
                    Compare(problems, "the bind's log", baked.BindLog, log6);
                    lines += log6.Count;
                    var dies6 = Of("DIES6");
                    if (dies6.Count > 0) throw new InvalidDataException($"the script died in the bind ({string.Join(" ", dies6[0].Skip(1))}) and the port went on");
                    bool bindRan = exit5.Count == 0;
                    foreach (string k6 in new[] { "BIND", "VG6", "MOD6", "V6", "VX6", "N6", "DATA6", "O7" })
                        if (!bindRan && Of(k6).Count > 0) throw new InvalidDataException($"the dump has {k6} rows for a job whose script exited before the bind");
                    if (!bindRan && (baked.Bound.Count > 0 || baked.MeshData != null || baked.AfterBind != null)) problems.Add("the port binds after the script's exit in the recoil step");
                    if (bindRan)
                    {
                        string Row(string[] t) => string.Join("\t", t.Skip(1));
                        var bindRows = Of("BIND");
                        foreach (var t in bindRows) if (t.Length != 33) throw new InvalidDataException($"the dump's bind row for '{t[1]}' has {t.Length} fields, expected 33");
                        if (bindRows.Count != Of("VG6").Count || bindRows.Count != Of("MOD6").Count || bindRows.Count != Of("V6").Count || bindRows.Count != Of("N6").Count) throw new InvalidDataException("the dump's bind rows do not agree in number (BIND, VG6, MOD6, V6, N6)");
                        string identity16 = string.Join("\t", Enumerable.Range(0, 16).Select(i => H(i % 5 == 0 ? 1f : 0f)));
                        Compare(problems, "bound meshes (bone, datablock, users, parent, transform, parent inverse)",
                                baked.Bound.Select(b => $"{b.Mesh.Name}\t{b.Group}\t{b.DataName}\t1\t{(b.Mesh.Parent != null ? b.Mesh.Parent.Name : "-")}\t{(b.Mesh.BoneNode >= 0 ? "BONE" : "OBJECT")}\t{string.Join("\t", b.Mesh.Loc.Select(H))}\t{string.Join("\t", b.Mesh.Quat.Select(H))}\t{string.Join("\t", b.Mesh.Scale.Select(H))}\t{identity16}").ToList(),
                                bindRows.Select(Row).ToList());
                        Compare(problems, "vertex groups after the bind", baked.Bound.Select(b => $"{b.Mesh.Name}\t{b.Group}\t{b.Positions.Length / 3}\t{b.Positions.Length / 3}").ToList(), Of("VG6").Select(Row).ToList());
                        Compare(problems, "modifiers after the bind", baked.Bound.Select(b => $"{b.Mesh.Name}\tARMATURE:arm:{baked.Armature.Name}").ToList(), Of("MOD6").Select(Row).ToList());
                        Compare(problems, "vertices after the bind", baked.Bound.Select(b => $"{b.Mesh.Name}\t{b.Positions.Length / 3}\t{ShaFloats(b.Positions)}").ToList(), Of("V6").Select(Row).ToList());
                        var mineVx = new List<string>();
                        foreach (var b in baked.Bound)
                        {
                            int n = b.Positions.Length / 3, k = Math.Min(64, n); var idx = new SortedSet<int>();
                            for (int i = 0; i < k; i++) idx.Add((int)((long)i * n / k));
                            foreach (int i in idx) mineVx.Add($"{b.Mesh.Name}\t{i}\t{H(b.Positions[3 * i])}\t{H(b.Positions[3 * i + 1])}\t{H(b.Positions[3 * i + 2])}");
                        }
                        Compare(problems, "sampled vertices after the bind", mineVx, Of("VX6").Select(Row).ToList());
                        Compare(problems, "custom normals after the bind", baked.Bound.Select(b => b.CustomNormal == null ? $"{b.Mesh.Name}\t-\t-\t0\t-" : $"{b.Mesh.Name}\tINT16_2D\tCORNER\t{b.Corners}\t{ShaShorts(b.CustomNormal)}").ToList(), Of("N6").Select(Row).ToList());
                        // bpy.data.meshes: by name - the order is Blender's sort, which nothing later reads
                        Compare(problems, "mesh datablocks after the bind (name, users)", baked.MeshData.Select(x => $"{x.name}\t{x.users}").OrderBy(s => s, StringComparer.Ordinal).ToList(), Of("DATA6").Select(Row).OrderBy(s => s, StringComparer.Ordinal).ToList());
                        CompareObjectRows(problems, Of("O7"), baked.AfterBind, "after the bind");
                        boundMeshes += baked.Bound.Count; vertices += baked.Bound.Sum(b => (long)b.Positions.Length / 3);
                        string staticRoot = baked.Bones.First(b => b.Part == null).Name;
                        foreach (var b in baked.Bound)
                        {
                            if (b.Group == staticRoot) boundStatic = true; else if (b.Group == b.Mesh.Name) boundOwn = true; else boundAncestor = true;
                            if (b.Group != staticRoot && !baked.BonesAfterRecoil.Any(x => x.Name == b.Group)) groupNoBone = true;
                            if (b.Copied) copiedData = true;
                            if (b.CustomNormal != null) boundNormals = true; else boundPlain = true;
                        }
                        if (baked.Bound.Count == 0) noMesh = true;
                    }
                    // ... and the same scene at the LAST frame of the range (the dump set it last of all): a frozen armature
                    // object, a stripped scale curve and the re-baked keys show there, not at the bind frame
                    var o3 = Of("O3").ToDictionary(t => t[1], t => t, StringComparer.Ordinal); var pb3 = Of("PB3");
                    if (o3.Count != baked.Objects.Count) throw new InvalidDataException($"the dump has {o3.Count} object rows at the last frame, {baked.Objects.Count} objects here");
                    var (lastWorlds, lastPose) = baked.ProbeAt(r.FrameMax);
                    ValidatePoseRows(pb3, lastPose, "PB3");
                    long wrongLast = 0; string firstLast = null;
                    foreach (var kv in lastWorlds)
                    {
                        if (!o3.TryGetValue(kv.Key, out var row) || row.Length != 18) throw new InvalidDataException($"the dump has no complete matrix row at the last frame for '{kv.Key}'");
                        bool ok = true;
                        for (int rr = 0; rr < 4; rr++) for (int c = 0; c < 4; c++) if (H(kv.Value[c * 4 + rr]) != row[2 + rr * 4 + c]) ok = false;
                        atLast++;
                        if (!ok) { wrongLast++; firstLast = firstLast ?? $"'{kv.Key}'"; }
                    }
                    if (wrongLast > 0) problems.Add($"{wrongLast} of {lastWorlds.Count} matrices at the last frame after the bake differ, first {firstLast}");
                    long wrongHeldLast = 0; string firstHeldLast = null;
                    foreach (var t in pb3)
                    {
                        var held = lastPose[t[1]][t[2]];
                        if (held == null || !held[0].Concat(held[1]).Concat(held[2]).Select(H).SequenceEqual(t.Skip(3)))
                        { wrongHeldLast++; firstHeldLast = firstHeldLast ?? $"'{t[2]}' of '{t[1]}'"; }
                    }
                    if (wrongHeldLast > 0) problems.Add($"{wrongHeldLast} of {pb3.Count} imported pose bones hold other values at the last frame, first {firstHeldLast}");
                    // ---- the sweep (the dump's very last act): frame after frame, what the new armature's pose bones hold -
                    //      a re-keyed bone between two Bezier keys is the curve evaluation. Every frame the dump names is set
                    //      here in the dump's order (a held zero's sign depends on it), every bone exactly once a frame.
                    var sw = Of("SW");
                    bool recoilRan = rec != null && !baked.ExitAtRecoil && dies5.Count == 0;
                    if ((sw.Count > 0) != (baked.RetargetLog.Count > 0 || baked.RecoilLog.Count > 0)) throw new InvalidDataException(sw.Count > 0 ? "the dump has a frame sweep for a job without a retarget or a recoil" : "the dump has no frame sweep for a job with a retarget or a recoil");
                    long wrongSw = 0; string firstSw = null; int at0 = 0, sweptFrames = 0; bool insideSegment = false;
                    var framesSwept = new List<long>();
                    while (at0 < sw.Count)
                    {
                        if (sw[at0].Length != 13 || !int.TryParse(sw[at0][1], out int frame)) throw new InvalidDataException("the dump has a malformed sweep row");
                        int n = 0; while (at0 + n < sw.Count && sw[at0 + n][1] == sw[at0][1]) n++;
                        var mine = baked.ArmAt(frame); var seenSw = new HashSet<string>(StringComparer.Ordinal);
                        for (int i = at0; i < at0 + n; i++)
                        {
                            var t = sw[i];
                            if (t.Length != 13) throw new InvalidDataException($"the dump's sweep row at frame {frame} has {t.Length} fields, expected 13");
                            if (!mine.TryGetValue(t[2], out var held)) throw new InvalidDataException($"the dump has a sweep row of an unknown bone at frame {frame}: '{t[2]}'");
                            if (!seenSw.Add(t[2])) throw new InvalidDataException($"the dump has a sweep row twice at frame {frame}: bone '{t[2]}'");
                            swept++;
                            if (!held.Select(H).SequenceEqual(t.Skip(3))) { wrongSw++; firstSw = firstSw ?? $"'{t[2]}' at frame {frame}: here [{string.Join(",", held.Select(Show))}] Blender [{string.Join(",", t.Skip(3).Select(h => Show(F(h))))}]"; }
                        }
                        foreach (var bone in mine.Keys) if (!seenSw.Contains(bone)) throw new InvalidDataException($"the dump has no sweep row at frame {frame} for bone '{bone}'");
                        foreach (var ch in baked.Rekeyed.Values) foreach (var list in ch) if (list != null && list.Count > 1 && frame > list[0].Frame && frame < list[list.Count - 1].Frame && !list.Any(k => k.Frame == frame)) insideSegment = true;
                        at0 += n; sweptFrames++; framesSwept.Add(frame);
                    }
                    if (sw.Count > 0)
                    {
                        // the frames are the ones blender_decisions_dump.py sets, in its order: no group gone, moved, doubled or
                        // renumbered (the drill replays what the dump names - a dump that names less would be judged on less)
                        long Raw(string txt, long d) => long.TryParse((txt ?? "").Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long w) ? w : d;
                        long Whole(string txt, long d) => Math.Max(-3000, Math.Min(3000, Raw(txt, d)));
                        long fmn = r.FrameMin, fmx = r.FrameMax;
                        var e4 = new[] { fmn, fmx, Whole(job.Length > 3 ? job[3] : "", fmx), Whole(job.Length > 5 ? job[5] : "", fmn) };
                        long lo = e4.Min() - 3, hi = e4.Max() + 3, step = Math.Max(1, (hi - lo) / 600);
                        var all4 = new List<long>(); for (long fr = lo; fr <= hi; fr += step) all4.Add(fr);
                        all4.Add(lo + 1); all4.Add(hi + 1); all4.Add((long)Math.Floor((lo + hi) / 2.0));
                        long e_ = Raw(job.Length > 3 ? job[3] : "", fmx), m_ = Math.Max((long)Math.Truncate(e_ * 0.5), 1), r_ = Raw(job.Length > 5 ? job[5] : "", fmn);
                        foreach (long k_ in new[] { m_, e_, r_, 1048574L, -1048574L }) for (int d = -2; d <= 2; d++) all4.Add(k_ + d);
                        long a_ = Math.Min(m_, e_), b_ = Math.Max(m_, e_);
                        for (int j = 0; j < 98; j++) all4.Add(a_ + (b_ - a_) * j / 97);
                        for (int j = 0; j < 40; j++) all4.Add(a_ + j);
                        for (int j = 0; j < 40; j++) all4.Add(b_ - j);
                        // ... and the recoil tail: about the kick's end and the settle, and across the return
                        if (recoilRan)
                        {
                            foreach (long k_ in new[] { rec.KickEnd, rec.OutEnd }) for (int d = -2; d <= 2; d++) all4.Add(k_ + d);
                            for (int j = 0; j <= 8; j++) all4.Add(rec.KickEnd + (long)Math.Floor((rec.OutEnd - rec.KickEnd) * j / 8.0));   // Python's // floors (a return that runs backwards)
                            all4.Add(lo); all4.Add(hi);
                        }
                        all4.Add(lo); all4.Add(hi);
                        var expected = new List<long>();
                        foreach (long fr in all4)
                        {
                            long requested = Math.Max(int.MinValue, Math.Min(int.MaxValue, fr));
                            if (expected.Count == 0 || expected[expected.Count - 1] != requested) expected.Add(requested);
                        }
                        if (!expected.SequenceEqual(framesSwept)) throw new InvalidDataException($"the dump's frame sweep is not the sweep the dump script makes: {framesSwept.Count} frames, expected {expected.Count}");
                    }
                    if (sw.Count > 0 && sweptFrames < 6) throw new InvalidDataException($"the dump's frame sweep has {sweptFrames} frames only");
                    if (wrongSw > 0) problems.Add($"{wrongSw} of {sw.Count} pose bones of the frame sweep hold other values, first {firstSw}");
                    if (insideSegment) betweenKeys = true;
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
                if (bakeLeftReason != null) Console.WriteLine($"BAKELEFT {key}: {bakeLeftReason}");   // no job is so marked since part 4b; the mark stays for what the later steps may leave
                if (bakeLeftReason == null && !r.Exit) cover[fireSnap ? "a fire-window snapshot (5a)" : "no recoil range: no snapshot"]++;
                if (retargeted) cover["a bone re-keyed by the barrel retarget or the leg scale (5b, 5c)"]++;
                if (recoiled) cover["a recoil tail keyed on a RecoilArm (5d)"]++;
                if (recoilExit) cover["the recoil step's exit: no barrel or cannon to pick"]++;
                if (betweenKeys) cover["a frame set between two Bezier keys of a re-keyed bone"]++;
                if (boundOwn) cover["a mesh bound to its own bone (6)"]++;
                if (boundAncestor) cover["a mesh bound to an ancestor's bone"]++;
                if (boundStatic) cover["a static mesh bound to StaticRoot"]++;
                if (copiedData) cover["a shared mesh datablock copied at the bind"]++;
                if (boundNormals) cover["a bound mesh with custom normals"]++;
                if (boundPlain) cover["a bound mesh without normals"]++;
                if (noMesh) cover["no mesh to bind"]++;
                if (groupNoBone) cover["a vertex group named after a pair-merged part (no bone of that name)"]++;
                if (rebakedArmature) cover["an imported armature baked with it (its bones keyed a frame, its own animation dropped)"]++;
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
        for (int bi = 0; bi < BlenderFCurve.Branches.Length; bi++) Console.WriteLine($"BRANCH	{BlenderFCurve.Hits[bi]}	{BlenderFCurve.Branches[bi]}");
        Console.WriteLine($"TOTAL jobs {files} failed {fails} left {left} objects {objects} matrices {matrices} lines {lines} bones {bones} curves {curves} keys {keysCompared} after {after} imported {importedKeys} held {held2} last {atLast} snapshot {snapped} rekeyed {rekeyed} swept {swept} recoil {recoilRows} bound {boundMeshes} vertices {vertices}");
        return fails == 0 ? 0 : 1;
    }

    // a row kind is held when every expected identity is there exactly once, complete, and equal to the bit
    static void ComparePoseRows(List<string> problems, List<string[]> rows, Dictionary<string, float[]> expected, string when)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal); long wrong = 0; string first = null;
        foreach (var t in rows)
        {
            if (t.Length != 12) throw new InvalidDataException($"the dump's armature pose row {when} has {t.Length} fields, expected 12");
            if (!expected.TryGetValue(t[1], out var mine)) throw new InvalidDataException($"the dump has an unknown armature pose row {when}: bone '{t[1]}'");
            if (!seen.Add(t[1])) throw new InvalidDataException($"the dump has an armature pose row {when} twice: bone '{t[1]}'");
            if (!mine.Select(H).SequenceEqual(t.Skip(2))) { wrong++; first = first ?? $"'{t[1]}': here [{string.Join(",", mine.Select(Show))}] Blender [{string.Join(",", t.Skip(2).Select(h => Show(F(h))))}]"; }
        }
        foreach (var bone in expected.Keys) if (!seen.Contains(bone)) throw new InvalidDataException($"the dump has no armature pose row {when} for bone '{bone}'");
        if (wrong > 0) problems.Add($"{wrong} of {rows.Count} pose bones of the new armature hold other values {when}, first {first}");
    }

    static void CompareObjectRows(List<string> problems, List<string[]> rows, Dictionary<string, float[]> expected, string when)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal); long wrong = 0; string first = null;
        foreach (var t in rows)
        {
            if (t.Length != 18) throw new InvalidDataException($"the dump's object row {when} has {t.Length} fields, expected 18");
            if (!expected.TryGetValue(t[1], out var mine)) throw new InvalidDataException($"the dump has an unknown object row {when}: '{t[1]}'");
            if (!seen.Add(t[1])) throw new InvalidDataException($"the dump has an object row {when} twice: '{t[1]}'");
            bool ok = true;
            for (int rr = 0; rr < 4; rr++) for (int c = 0; c < 4; c++) if (H(mine[c * 4 + rr]) != t[2 + rr * 4 + c]) ok = false;
            if (!ok) { wrong++; first = first ?? $"'{t[1]}': here [{string.Join(",", Enumerable.Range(0, 16).Select(i => Show(mine[(i % 4) * 4 + i / 4])))}] Blender [{string.Join(",", t.Skip(2).Select(h => Show(F(h))))}]"; }
        }
        foreach (var name in expected.Keys) if (!seen.Contains(name)) throw new InvalidDataException($"the dump has no object row {when} for '{name}'");
        if (wrong > 0) problems.Add($"{wrong} of {rows.Count} matrices {when} differ, first {first}");
    }

    static string Show(float x) => x.ToString("R") + (x == 0f && Bits(x) != 0 ? "(-0)" : "");
    static string Cut(string s) => s.Length > 150 ? s.Substring(0, 150) + "..." : s;

    // A row count alone lets a duplicate replace an omitted bone. Require each expected identity once at each frame.
    static void ValidatePoseRows(List<string[]> rows, Dictionary<string, Dictionary<string, float[][]>> expected, string kind)
    {
        var seen = new HashSet<(string armature, string bone)>();
        foreach (var t in rows)
        {
            if (t.Length != 13) throw new InvalidDataException($"the dump's {kind} row has {t.Length} fields, expected 13");
            if (!expected.TryGetValue(t[1], out var armature) || !armature.ContainsKey(t[2]))
                throw new InvalidDataException($"the dump has an unknown {kind} bone '{t[2]}' of '{t[1]}'");
            if (!seen.Add((t[1], t[2]))) throw new InvalidDataException($"the dump has a duplicate {kind} bone '{t[2]}' of '{t[1]}'");
        }
        foreach (var armature in expected)
            foreach (var bone in armature.Value.Keys)
                if (!seen.Contains((armature.Key, bone))) throw new InvalidDataException($"the dump has no {kind} row for bone '{bone}' of '{armature.Key}'");
    }

    static void Compare(List<string> problems, string what, List<string> mine, List<string> theirs)
    {
        if (mine.SequenceEqual(theirs)) return;
        int i = 0; while (i < mine.Count && i < theirs.Count && mine[i] == theirs[i]) i++;
        problems.Add($"{what}: {mine.Count} here, {theirs.Count} in Blender, first difference at {i + 1}: here «{(i < mine.Count ? mine[i].Replace('\t', ' ') : "(none)")}», Blender «{(i < theirs.Count ? theirs[i].Replace('\t', ' ') : "(none)")}»");
    }
}
