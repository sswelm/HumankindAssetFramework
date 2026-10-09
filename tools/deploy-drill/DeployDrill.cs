// DeployDrill.cs - the C# side of tools/deploy_drill.sh (replacing deploy_convert.py). Part 1, THE POSED STATE: for
// every file Blender dumped (tools/deploy-drill/blender_posed_dump.py), the importer's action is rebuilt
// (BlenderPosedState.Import) and every object's matrix_world at every dumped frame is set beside Blender's - the bits of
// all sixteen floats. Also held: the action's frame range, and WHICH objects the animation touches (deploy_convert.py
// takes those for its parts).
// Not compared, counted: an object under a bone (it follows the armature's pose) and what hangs below one; a skinned
// mesh (it sits under its armature with the identity).
// usage: DeployDrill.exe <dump>...     exit 0 when every compared matrix is equal
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

static class DeployDrill
{
    static uint Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
    static float FromHex(string h) => BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(h, 16)), 0);

    static readonly string[] CoverKeys =
    {
        "a frame on a key", "a frame between two keys", "a frame before the first key", "a frame after the last key",
        "an animated translation", "an animated rotation", "an animated scale", "a quaternion negated to run the short way", "keys closer than 0.01 frame merged (the last wins)",
        "several animations: the first one's action is the active one", "morph weights in the action (they count for its frame range)", "a camera and what hangs below it: the importer's camera correction is not modelled (not compared)", "left to Blender: KHR_animation_pointer (nothing of it is held)", "left to Blender: KHR_animation_pointer with a CUBICSPLINE sampler (nothing of it is held)", "a merged key on a whole frame (it keeps its own frame)", "a key within 0.0001 frame of the frame, not on it (its value, not an interpolation)", "a curve of one key", "a STEP sampler (constant keys)",
        "a second channel on one node and path (the first stands)", "an object only a later animation touches (no action on it)", "left to Blender: a CUBICSPLINE sampler (Bezier keys with automatic handles)",
        "an animated node given as a matrix", "an unanimated child of an animated node", "an armature whose bones are animated (an action, no object motion)",
    };

    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine($"RUNTIME	{(IntPtr.Size * 8)}-bit");
        int fails = 0, files = 0, left = 0; long matrices = 0, skipped = 0, properties = 0;
        var cover = new SortedDictionary<string, long>(); foreach (var k in CoverKeys) cover[k] = 0;
        var notes = new SortedDictionary<string, long>();
        // a dump holds one block per file: FILE ... DONE
        var blocks = new List<List<string>>();
        foreach (var dump in args)
            foreach (var line in File.ReadLines(dump))
            {
                if (line.StartsWith("FILE\t") || blocks.Count == 0) blocks.Add(new List<string>());
                blocks[blocks.Count - 1].Add(line);
            }
        foreach (var block in blocks)
        {
            string path = null; var objs = new List<string[]>(); var actions = new List<string[]>();
            var rows = new Dictionary<int, Dictionary<string, float[]>>(); var frames = new List<int>();
            var props = new Dictionary<(int, string), float[]>(); var icospheres = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in block)
            {
                var t = line.Split('\t');
                if (t[0] == "FILE") path = t[1];
                else if (t[0] == "ACTION") actions.Add(t);
                else if (t[0] == "OBJ") objs.Add(t);
                else if (t[0] == "FRAMES") frames.AddRange(t.Skip(1).Select(int.Parse));
                else if (t[0] == "M")
                {
                    int f = int.Parse(t[1]);
                    if (!rows.TryGetValue(f, out var d)) rows[f] = d = new Dictionary<string, float[]>(StringComparer.Ordinal);
                    d[t[2]] = t.Skip(3).Select(FromHex).ToArray();
                }
                else if (t[0] == "L") props[(int.Parse(t[1]), t[2])] = t.Skip(3).Select(FromHex).ToArray();
                else if (t[0] == "FAIL") { Console.WriteLine($"FAIL {t[1]}: Blender could not dump it: {t[2]}"); fails++; }
            }
            if (path == null) continue;
            files++;
            string shortKey = string.Join("/", path.Split('/').Reverse().Take(2).Reverse());
            var problems = new List<string>();
            try
            {
                // A skipped or unsupported object still has rows in the dump. Validate its contract before deciding
                // what we can compare: absent evidence must never be counted as equal output.
                if (frames.Count == 0) throw new InvalidDataException("dump has no frames");
                foreach (int f in frames)
                    foreach (var o in objs)
                    {
                        if (!rows.TryGetValue(f, out var frameRows) || !frameRows.TryGetValue(o[1], out var matrix))
                            throw new InvalidDataException($"dump is missing matrix row for '{o[1]}' at frame {f}");
                        if (matrix.Length != 16) throw new InvalidDataException($"dump matrix row for '{o[1]}' at frame {f} has {matrix.Length} values, expected 16");
                        if (!props.TryGetValue((f, o[1]), out var property))
                            throw new InvalidDataException($"dump is missing property row for '{o[1]}' at frame {f}");
                        if (property.Length != 10) throw new InvalidDataException($"dump property row for '{o[1]}' at frame {f} has {property.Length} values, expected 10");
                    }
                var m = GlbReader.Read(path);
                var names = BlenderNames.Compute(m);
                var action = BlenderPosedState.Import(m, 0);
                // Bezier keys with automatic handles are not modelled: the file is named and left, not compared
                // (its frame range and the objects it touches are held all the same: the keys' frames are the importer's)
                // KHR_animation_pointer: the importer animates through pointers the reader does not carry - nothing is held)
                bool unsupported = action.NotModelled != null, blind = m.ExtensionsUsed.Contains("KHR_animation_pointer");
                // Pointer channels are unread even when another channel is CUBICSPLINE. Their frame range and touched
                // objects cannot be compared; HasBezier describes an independent limitation.
                var later = new HashSet<int>();
                for (int ai = 1; ai < m.Animations.Count; ai++) foreach (var ch in m.Animations[ai].Channels) if (ch.Node >= 0 && !action.Animates(ch.Node)) later.Add(ch.Node);
                // the action: Blender activates the first animation; its range is the first and the last key
                if (m.Animations.Count > 0 && !blind)
                {
                    // the action Blender leaves ACTIVE is the first animation's, by its track name (bpy.data.actions is sorted
                    // by name - the first row is not it)
                    string track = BlenderNames.TrackNames(m)[0];
                    var act = actions.FirstOrDefault(x => x[1] == track);
                    if (act == null) problems.Add($"no action named '{track}' in Blender (it has: {string.Join(", ", actions.Select(x => x[1]))})");
                    else
                    {
                        foreach (var t in objs) if (t[6] != "-" && t[6] != track) { problems.Add($"'{t[1]}' has the action '{t[6]}' active, not the first animation's '{track}'"); break; }
                        if (actions.Count > 1) cover["several animations: the first one's action is the active one"]++;
                        if (Bits(FromHex(act[2])) != Bits(action.FrameStart) || Bits(FromHex(act[3])) != Bits(action.FrameEnd))
                            problems.Add($"frame range {action.FrameStart:R}..{action.FrameEnd:R} here, {FromHex(act[2]):R}..{FromHex(act[3]):R} in Blender");
                    }
                }
                // which objects carry the action: the nodes the animation touches; an armature for its animated bones
                var byName = names.Objects.ToDictionary(o => o.Name, o => o, StringComparer.Ordinal);
                var animatedArmatures = new HashSet<int>();
                foreach (int b in names.BoneNodesInOrder) if (action.Animates(b)) animatedArmatures.Add(names.ArmatureNodeOfBone[b]);
                int wrongAction = 0; string firstWrong = null;
                foreach (var t in objs)
                {
                    // the importer's bone shape: a mesh object named Icosphere that is no part of the file (BlenderNames leaves it out,
                    // prep_model.py and deploy_convert.py remove it)
                    if (!byName.ContainsKey(t[1]) && t[2] == "MESH" && t[1].StartsWith("Icosphere", StringComparison.Ordinal) && t[3] == "-") { icospheres.Add(t[1]); continue; }
                    if (!byName.TryGetValue(t[1], out var o)) { problems.Add($"Blender has an object '{t[1]}' the names do not"); continue; }
                    if (blind) continue;
                    bool theirs = t[6] != "-";
                    bool mine = o.Kind == BlenderNames.ObjectKind.Armature ? animatedArmatures.Contains(o.GltfNode) || (o.GltfNode >= 0 && action.Animates(o.GltfNode))
                              : o.GltfNode >= 0 && !(o.Kind == BlenderNames.ObjectKind.Mesh && o.Skin >= 0) && action.Animates(o.GltfNode);
                    if (!mine && !theirs && later.Contains(o.GltfNode)) cover["an object only a later animation touches (no action on it)"]++;
                    if (mine != theirs) { wrongAction++; firstWrong = firstWrong ?? $"'{t[1]}' ({(theirs ? "an action in Blender, none here" : "an action here, none in Blender")})"; }
                    if (mine && theirs && o.Kind == BlenderNames.ObjectKind.Armature && animatedArmatures.Contains(o.GltfNode)) cover["an armature whose bones are animated (an action, no object motion)"]++;
                }
                if (wrongAction > 0) problems.Add($"{wrongAction} objects differ in whether the animation touches them, first {firstWrong}");
                if (objs.Count - icospheres.Count != names.Objects.Count) problems.Add($"{names.Objects.Count} objects here, {objs.Count} in Blender");
                // every object's matrix_world at every frame
                // a camera: the importer turns it (camera_correction) and turns its children back - VehicleProbe.BlenderWorldMatrices
                // models neither (the backlog has it since the export tree named it). Skipped with what hangs below, counted
                var underCamera = new HashSet<string>(StringComparer.Ordinal);
                foreach (var o in names.Objects)
                    for (var q = o; q != null; q = q.Parent != null && byName.TryGetValue(q.Parent, out var up) ? up : null)
                        if (q.Kind == BlenderNames.ObjectKind.Camera) { underCamera.Add(o.Name); break; }
                if (underCamera.Count > 0) cover["a camera and what hangs below it: the importer's camera correction is not modelled (not compared)"]++;
                long propsCompared = 0, propsWrong = 0; string firstProp = null;
                var underBone = new HashSet<string>(StringComparer.Ordinal);
                foreach (var o in names.Objects)
                    for (var q = o; q != null; q = q.Parent != null && byName.TryGetValue(q.Parent, out var up) ? up : null)
                        if (q.ParentBone != null) { underBone.Add(o.Name); break; }
                long equal = 0, compared = 0; uint worst = 0; string worstWhere = null;
                // the frames in the order the dump set them: a property keeps the sign of the zero it held
                var pose = new BlenderPosedState.Pose(action, m);
                foreach (int f in unsupported ? new List<int>() : frames)
                {
                    var set = pose.FrameSet(f);
                    for (int n = 0; n < m.Nodes.Count; n++) set(n);
                    var world = VehicleProbe.BlenderWorldMatrices(m, set);
                    foreach (var o in names.Objects)
                    {
                        if (!rows[f].TryGetValue(o.Name, out var theirs)) continue;
                        if (underCamera.Contains(o.Name)) { skipped++; continue; }
                        if (underBone.Contains(o.Name) || o.GltfNode < 0 || (o.Kind == BlenderNames.ObjectKind.Mesh && o.Skin >= 0)) { skipped++; continue; }
                        var mine = world[o.GltfNode];
                        compared++;
                        uint d = 0;
                        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++)
                        {
                            uint a = Bits(mine[c * 4 + r]), b = Bits(theirs[r * 4 + c]);
                            if (a == b) continue;
                            // the BITS: a zero of the other sign is a difference too (distance 1 here)
                            long da = (int)a < 0 ? int.MinValue - (long)(int)a : (int)a, db = (int)b < 0 ? int.MinValue - (long)(int)b : (int)b;
                            d = Math.Max(d, Math.Max(1u, (uint)Math.Min(uint.MaxValue, Math.Abs(da - db))));
                        }
                        // the evaluated properties themselves (Blender's location, rotation_quaternion, scale after frame_set):
                        // BlenderPosedState.Curve.Evaluate held directly, each animated path's components bit for bit
                        var posed = set(o.GltfNode);
                        if (posed != null && props.TryGetValue((f, o.Name), out var evaluated))
                        {
                            bool ok = true;
                            if (posed[0] != null) for (int k = 0; k < 3; k++) if (Bits(posed[0][k]) != Bits(evaluated[k])) ok = false;
                            if (posed[1] != null) for (int k = 0; k < 4; k++) if (Bits(posed[1][k]) != Bits(evaluated[3 + k])) ok = false;
                            if (posed[2] != null) for (int k = 0; k < 3; k++) if (Bits(posed[2][k]) != Bits(evaluated[7 + k])) ok = false;
                            propsCompared++;
                            if (!ok)
                            {
                                propsWrong++;
                                string S(float[] a) => a == null ? "-" : string.Join(",", a.Select(x => x.ToString("R") + (x == 0f && Bits(x) != 0 ? "(-0)" : "")));
                                firstProp = firstProp ?? $"'{o.Name}' at frame {f}: here loc[{S(posed[0])}] quat[{S(posed[1])}] scale[{S(posed[2])}], Blender loc[{S(evaluated.Take(3).ToArray())}] quat[{S(evaluated.Skip(3).Take(4).ToArray())}] scale[{S(evaluated.Skip(7).ToArray())}]";
                            }
                        }
                        if (d == 0) equal++;
                        else if (d > worst)
                        {
                            worst = d; worstWhere = $"'{o.Name}' at frame {f}";
                            // the evaluated properties beside Blender's, to say WHICH channel is off
                            var p = set(o.GltfNode);
                            if (p != null && props.TryGetValue((f, o.Name), out var L))
                            {
                                string S(float[] a) => a == null ? "-" : string.Join(",", a.Select(x => x.ToString("R")));
                                worstWhere += $" | here loc[{S(p[0])}] quat wxyz[{S(p[1])}] scale[{S(p[2])}] | Blender loc[{string.Join(",", L.Take(3).Select(x => x.ToString("R")))}] quat wxyz[{string.Join(",", L.Skip(3).Take(4).Select(x => x.ToString("R")))}] scale[{string.Join(",", L.Skip(7).Select(x => x.ToString("R")))}]";
                            }
                        }
                        // what this comparison exercised
                        if (action.Animates(o.GltfNode))
                        {
                            var curve = (action.Rotation.TryGetValue(o.GltfNode, out var rc) ? rc : action.Translation.TryGetValue(o.GltfNode, out var tc) ? tc : action.Scale[o.GltfNode])[0];
                            if (f < curve.Frames[0]) cover["a frame before the first key"]++;
                            else if (f > curve.Frames[curve.Frames.Length - 1]) cover["a frame after the last key"]++;
                            else if (curve.Frames.Any(k => k != f && Math.Abs(k - f) <= 0.0001f)) cover["a key within 0.0001 frame of the frame, not on it (its value, not an interpolation)"]++;
                            else if (curve.Frames.Any(k => k == f)) cover["a frame on a key"]++;
                            else cover["a frame between two keys"]++;
                            if (m.Nodes[o.GltfNode].HasMatrix) cover["an animated node given as a matrix"]++;
                        }
                        else if (m.Nodes[o.GltfNode].Parent >= 0 && Enumerable.Range(0, 64).Aggregate((node: m.Nodes[o.GltfNode].Parent, hit: false), (s, _) => s.node < 0 || s.hit ? s : (m.Nodes[s.node].Parent, action.Animates(s.node))).hit)
                            cover["an unanimated child of an animated node"]++;
                    }
                }
                matrices += compared;
                if (equal != compared) problems.Add($"{compared - equal} of {compared} matrices differ, the worst by {worst} ulp: {worstWhere}");
                if (propsWrong > 0) problems.Add($"{propsWrong} of {propsCompared} evaluated location/rotation/scale sets differ, first {firstProp}");
                properties += propsCompared;
                if (problems.Count > 0) { fails++; Console.WriteLine($"FAIL {shortKey}: " + string.Join("; ", problems.Take(4))); }
                else if (unsupported)
                {
                    left++;
                    cover[blind ? "left to Blender: KHR_animation_pointer (nothing of it is held)" : "left to Blender: a CUBICSPLINE sampler (Bezier keys with automatic handles)"]++;
                    if (blind && action.HasBezier) cover["left to Blender: KHR_animation_pointer with a CUBICSPLINE sampler (nothing of it is held)"]++;
                    string reason = blind ? "KHR_animation_pointer - the importer animates through pointers this does not read" : action.NotModelled;
                    Console.WriteLine($"LEFT {shortKey}: {reason}, which BlenderPosedState does not model" + (blind ? "" : " (its frame range and the objects it touches equal Blender's)"));
                }
                else
                {
                    // what the action exercised counts once the file holds: every matrix and every evaluated property equal
                    foreach (var n in action.Notes) if (cover.ContainsKey(n)) cover[n]++; else notes[n] = notes.TryGetValue(n, out long c) ? c + 1 : 1;
                }
                if (problems.Count == 0 && !unsupported) Console.WriteLine($"PASS {shortKey}: {compared} matrices of {names.Objects.Count} objects at {frames.Count} frames equal to Blender's, bit for bit (frames {action.FrameStart:R}..{action.FrameEnd:R})");
            }
            catch (Exception e) { fails++; Console.WriteLine($"FAIL {shortKey}: {e.GetType().Name}: {e.Message}"); }
        }
        foreach (var kv in notes) Console.WriteLine($"NOTE {kv.Value} files: {kv.Key}");
        foreach (var kv in cover) Console.WriteLine($"COVER {kv.Value} {kv.Key}");
        Console.WriteLine($"TOTAL files {files} failed {fails} matrices {matrices} skipped {skipped} left {left} properties {properties}");
        return fails == 0 ? 0 : 1;
    }
}
