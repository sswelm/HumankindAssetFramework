// BlenderChannelExport.cs - deploy_convert.py's export, part 8b: every animation's channels as the glTF exporter
// SAMPLES them (export_force_sampling, the default), bit for bit. Measured on the deploy drill's dump (2026-10-11) and
// read in io_scene_gltf2/blender/exp/animation (action.py, sampled/armature/*.py, sampled/sampling_cache.py, keyframes.py,
// anim_utils.py) and com/gltf2_blender_math.py:
//
//   * the actions (action.py __get_blender_actions, ACTIONS mode, one armature): the active action (the legacy "deploy")
//     and every other action with an OBJECT slot that animates a bone (the role clips), sorted by name lower-cased;
//     one glTF animation per action whose channels are not empty (BlenderDeployExport names them already)
//   * the range (prepare_actions_range): int(action.frame_range[0])..int(action.frame_range[1]) - the first and the last
//     key over the action's curves; a single frame's +1 (BKE_action_frame_range_calc) is undone by the exporter's own
//     bug-107030 check (end - start == 1: the curves' ranges again) -, cut to scene.frame_start..frame_end
//     (export_frame_range=True, as the script calls it); frame_step 1; no slide (no negative frame survives the cut)
//   * the switch (gather_action_animations): an action that is not the armature's current one is made current after
//     reset_bone_matrix - every pose bone's matrix_basis the identity (location 0, the quaternion (1, 0, 0, 0), the Euler
//     angles 0, scale 1). The switch runs for every action in the sorted list, one that ends without channels too, so
//     the active action escapes the reset only when it sorts FIRST (then nothing switched the armature away before it):
//     a property without a curve in it holds what the script left there (BlenderDeploy.Result.ArmPose7 - the garbage
//     bones the sanitize de-animated ride at the role clips' last pose). "deploy" sorts before every role today; a role
//     named below it would reset the deploy action too (the reviewer's note, 2026-10-11)
//   * the samples (sampling_cache.py armature_caching, scene.frame_set per frame): per bone, with a bone parent
//         rest_mat = parent.bone.matrix_local.inverted_safe() @ bone.matrix_local
//         matrix   = rest_mat.inverted_safe() @ parent.matrix.inverted_safe() @ bone.matrix
//     a root bone: matrix = bone.matrix_local.inverted_safe() @ bone.matrix - mathutils float32 products (MatMulMathutils),
//     the adjugate inverse (InvertedSafe), pose_bone.matrix the pose chain (BlenderDeploy.PoseMatrices) of the bone's
//     channel matrix (BKE_pchan_to_mat4: the quaternion normalized and quat_to_mat3, or eulO_to_mat3 for a bone in an
//     Euler mode - the spun wheels -, times the size, the location copied); then mat.decompose(): mat4_to_loc_rot_size
//     and mat3_normalized_to_quat_fast
//   * the keep (sampled/armature/keyframes.py, optimize_animation_size and keep_anim_armature both on): a channel whose
//     samples do not change - np.ptp of every component below 0.0001, in double - keeps its first and last sample
//     (two or more samples), animated or not; else every sample
//   * the interpolation (sampler.py __gather_interpolation): over two samples STEP only where the bone's curve for the
//     property starts with a CONSTANT key (never here), else LINEAR; one sample: STEP for a property without a curve,
//     else the curve's (a Bezier one's CUBICSPLINE falls back to LINEAR); two samples: STEP for a property without a
//     curve, or whose two samples are equal (mathutils ==, within one float step per component), else LINEAR. "Without a
//     curve" (fcurves/channels.py get_channel_groups): no curve of the property in the action - a rotation_quaternion
//     curve on a bone in an Euler mode, or a rotation_euler curve on a QUATERNION bone, does not count; a curve group
//     needs_baking (one key, mixed interpolations, unequal counts or key frames) counts as LINEAR
//   * the values (sampler.py __convert_keyframes, gltf2_blender_math.transform): with correction_matrix_local - the
//     Y-up basis change @ matrix_local for a root bone, parent.matrix_local.inverted_safe() @ matrix_local under a bone
//     parent -: location = (correction @ Matrix.Translation(loc)).to_translation(); rotation = (correction @ the
//     normalized quaternion's matrix).to_quaternion() (mat4_to_quat: the axes normalized, the checks, the fast
//     conversion), written (x, y, z, w); scale = (correction @ Matrix.Diagonal(scale)).to_scale() (mat3_to_rot_size); the
//     times frame / (fps * fps_base) = frame / 24 in double, each stored as float32 (BinaryData.from_list), the accessor's
//     min and max the first and the last time
//   * the order: the bones of the armature's vtree, depth first from the root bones (pose.bones without a parent, in
//     order) through each bone's children; per bone location, rotation_quaternion, scale; one sampler per channel in the
//     channel's order (link_samplers appends by identity: no sampler is shared). ACCESSORS are shared: gather_accessor is
//     cached on (bytes, component type, count, max, min, type) for the whole export, so channels with the same times (or
//     the same two kept samples) write one accessor and one buffer view, with the accessor's min and max the Python
//     doubles frame / 24 - part 8c's writer dedupes by that key; this result holds the floats per channel
//   * not modelled, impossible from the script: the armature OBJECT's own channels (an action with curves outside
//     pose.bones - nla.bake runs with bake_types={'POSE'} and the roles key pose bones alone), a connected bone
//     (BKE_pchan_to_mat4 drops the location of a bone with BONE_CONNECTED; edit_bones.new leaves use_connect off), a
//     bone constraint (clear_constraints=True at the bake); fps 24 holds for a glTF source (the glTF importer reads
//     render.fps, an FBX import would set it) - the only source the port reads
using System;
using System.Collections.Generic;
using System.Linq;

public static class BlenderChannelExport
{
    public sealed class Channel
    {
        public int Node;                     // the target node (the bone's)
        public string Path;                  // translation, rotation, scale
        public string Interpolation;         // LINEAR or STEP
        public float[] Times;                // the input accessor's floats (seconds)
        public double MinTime, MaxTime;      // the accessor's min and max: the first and last time as Python doubles (frame / 24)
        public float[] Values;               // the output accessor's floats, flattened
        public int Sampler;                  // its sampler's index (one per channel)
    }

    public sealed class Animation
    {
        public string Name;
        public readonly List<Channel> Channels = new List<Channel>();
    }

    public sealed class Result
    {
        public string Fallback;
        public readonly List<Animation> Animations = new List<Animation>();
        public int Reduced, Full, Single, Stepped, Linear, EulerBones, HeldBones;   // what the walk met (the drill's coverage)
    }

    const double Fps = 24.0;   // the startup scene's render.fps * fps_base
    const int LOC = 0, QUAT = 3, SCALE = 7, EUL = 10;   // BlenderDeploy.Role.Curves and ArmPose layout
    static readonly float[] AxisBasisChange = { 1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1 };   // row-major items: (x, y, z) -> (x, z, -y)

    sealed class LeftException : Exception { public LeftException(string what) : base(what) { } }

    /// <summary>One action's curves as the depsgraph evaluates them: `Has` tells whether a curve exists for a bone's
    /// channel (the Role layout), `Eval` its value at a frame, `Keys` the curve's keys (null for the bake's dense
    /// curves, which are Bezier keys on every frame of Lo..Hi).</summary>
    public sealed class Source
    {
        public string Name; public bool Reset;
        public Func<string, int, bool> Has;
        public Func<string, int, int, float> Eval;
        public Func<string, int, List<BlenderDeploy.ArmKey>> Keys;
        public Func<string, int, int> DenseCount;   // the bake's curve: its key count, 0 when the bone is not from the bake
        public int Lo, Hi; public bool Any;
    }

    public static Result Build(BlenderDeploy.Result r, BlenderDeployExport.Result x, int sfra, int efra)
    {
        var res = new Result();
        try { BuildInto(res, r, x, sfra, efra); }
        catch (LeftException e) { res.Fallback = e.Message; }
        return res;
    }

    /// <summary>What the sampler needs of a rig, whoever made it (the deploy port's Finish, or rig_anim's stage 4): the
    /// bones in the vtree's walk - root bones in pose order, each followed by its children -, each with its parent, its
    /// matrix_local and its offset matrix (BKE_bone_offset_matrix_get); a bone's rotation mode and the pose the script
    /// left on it (13 values: location, quaternion, scale, Euler); the actions the exporter visits (every action with a
    /// slot of the armature, sorted by name lower-cased) and the active one; the curves of an action by name.</summary>
    public sealed class BoneSpec { public string Name; public BoneSpec Parent; public float[] MatrixLocal, Offs; }
    public sealed class Spec
    {
        public readonly List<BoneSpec> Walk = new List<BoneSpec>();
        public Func<string, string> ModeOf; public Func<string, float[]> HeldOf;
        public string Active; public readonly List<string> Visited = new List<string>();
        public Func<string, Source> SourceOf;
    }

    public static Result Build(Spec spec, BlenderDeployExport.Result x, int sfra, int efra)
    {
        var res = new Result();
        try { BuildCore(res, spec, x, sfra, efra); }
        catch (LeftException e) { res.Fallback = e.Message; }
        catch (InvalidOperationException e) { res.Fallback = e.Message; }   // the Spec's own refusals (the rig's: curves it does not model)
        return res;
    }

    static void BuildInto(Result res, BlenderDeploy.Result r, BlenderDeployExport.Result x, int sfra, int efra)
    {
        if (x.Animations.Count == 0) return;
        var bones = r.BonesAfterRecoil ?? r.Bones;
        if (bones == null || bones.Count == 0) throw new LeftException("an animation without bones (the armature's object channels are not modelled)");
        var byName = bones.ToDictionary(b => b.Name, b => b, StringComparer.Ordinal);
        var order = r.BoneOrder ?? bones.Select(b => b.Name).ToList();
        // the vtree's walk: root bones in pose order, each followed by its children (recursive_node_traverse)
        var walk = new List<BlenderDeploy.Bone>();
        void Visit(BlenderDeploy.Bone parent) { foreach (string n in order) { var b = byName[n]; if (b.Parent == parent) { walk.Add(b); Visit(b); } } }
        Visit(null);
        if (walk.Count != bones.Count) throw new LeftException("a bone outside the pose order (the vtree's walk is not modelled)");
        var garbage = new HashSet<string>(x.GarbageBones ?? new List<string>(), StringComparer.Ordinal);

        // the pose the script left: (mode, 13 values: location, quaternion, Euler, scale) after the role clips, else the
        // bake's 10 (location, quaternion, scale) in QUATERNION mode
        string ModeOf(string bone) => r.ArmPose7 != null && r.ArmPose7.TryGetValue(bone, out var p) ? p.mode : "QUATERNION";
        float[] HeldOf(string bone)   // Role layout: loc 3, quat 4, scale 3, eul 3
        {
            if (r.ArmPose7 != null && r.ArmPose7.TryGetValue(bone, out var p))
            {
                var v = p.values;
                return new[] { v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[10], v[11], v[12], v[7], v[8], v[9] };
            }
            if (r.ArmPose != null && r.ArmPose.TryGetValue(bone, out var h))
                return new[] { h[0], h[1], h[2], h[3], h[4], h[5], h[6], h.Length > 9 ? h[7] : 1f, h.Length > 9 ? h[8] : 1f, h.Length > 9 ? h[9] : 1f, 0f, 0f, 0f };
            return Identity();
        }

        string active = r.Actions7 != null && r.Actions7.Count > 0 ? r.Actions7[0] : null;
        // every action the exporter visits, sorted by name lower-cased (ActionsData.get): the active one is left unreset only
        // when it comes first - a later one has been switched away from by then (the switch runs for an action that ends
        // without channels too, so the sort is over every action, not over the animations written)
        var visited = new List<string>();
        if (r.Actions7 != null) visited.AddRange(r.Actions7);
        foreach (var role in r.Roles) visited.Add(role.Name);
        BlenderExportTree.StableSort(visited, (a, b) => string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant()));
        var spec = new Spec { Active = active, ModeOf = ModeOf, HeldOf = HeldOf, SourceOf = name => SourceOf(r, name, active, garbage, byName.Keys) };
        spec.Visited.AddRange(visited);
        var specOf = new Dictionary<string, BoneSpec>(StringComparer.Ordinal);
        foreach (var b in walk) { var bs = new BoneSpec { Name = b.Name, MatrixLocal = b.MatrixLocal, Offs = b.Offs, Parent = b.Parent != null ? specOf[b.Parent.Name] : null }; specOf[b.Name] = bs; spec.Walk.Add(bs); }
        BuildCore(res, spec, x, sfra, efra);
    }

    static void BuildCore(Result res, Spec spec, BlenderDeployExport.Result x, int sfra, int efra)
    {
        if (x.Animations.Count == 0) return;
        var walk = spec.Walk; string active = spec.Active; var visited = spec.Visited;
        if (walk.Count == 0) throw new LeftException("an animation without bones (the armature's object channels are not modelled)");
        var nodeOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < x.Nodes.Count; i++) if (x.Nodes[i].Joint) nodeOf[x.Nodes[i].Name] = i;
        foreach (var b in walk) if (!nodeOf.ContainsKey(b.Name)) throw new LeftException($"the bone '{b.Name}' has no node");
        bool activeUnreset = active != null && visited.Count > 0 && visited[0] == active;
        foreach (string name in x.Animations)
        {
            var src = spec.SourceOf(name);
            if (src == null) throw new LeftException($"the animation '{name}' names no action the port holds");
            src.Reset = !(name == active && activeUnreset);
            var anim = new Animation { Name = name };
            res.Animations.Add(anim);
            // the range: the curves' first and last key, cut to the scene's (int() of each: whole frames here). An action
            // without a sample is not written (action.py appends an animation with channels only): BlenderDeployExport
            // named this one, so a contradiction here is a defect, not an empty animation
            if (!src.Any) throw new LeftException($"the animation '{name}' has no curve left (the export named it all the same)");
            int start = Math.Max(sfra, src.Lo), end = Math.Min(efra, src.Hi);
            if (start < 0) throw new LeftException($"an action starting below frame 0 ('{name}' at {start}: the SLIDE of negative frames is not modelled)");
            if (start > end) throw new LeftException($"the animation '{name}' reaches no frame of the scene's range (the export named it all the same)");
            int n = end - start + 1;
            if (n > 1 << 20) throw new LeftException($"an action of {n} frames ('{name}': the sampler would run for hours)");

            // every bone's pose per frame, decomposed into (loc, quat, size) in the exporter's bone-local frame
            var sampled = new Dictionary<string, float[][]>(StringComparer.Ordinal);   // bone -> per frame: 10 floats
            foreach (var b in walk) sampled[b.Name] = new float[n][];
            var pose = new Dictionary<string, float[]>(StringComparer.Ordinal);        // pose_bone.matrix, column-major
            var corr = new Dictionary<string, float[]>(StringComparer.Ordinal);        // correction_matrix_local, row-major
            var restInv = new Dictionary<string, float[]>(StringComparer.Ordinal);     // rest_mat.inverted_safe(), row-major
            foreach (var b in walk)
            {
                var ml = VehicleProbe.ToRowMajor(b.MatrixLocal);
                if (b.Parent != null)
                {
                    var rest = VehicleProbe.MatMulMathutils(VehicleProbe.InvertedSafe(VehicleProbe.ToRowMajor(b.Parent.MatrixLocal)), ml);
                    corr[b.Name] = rest; restInv[b.Name] = VehicleProbe.InvertedSafe(rest);
                }
                else { corr[b.Name] = VehicleProbe.MatMulMathutils(AxisBasisChange, ml); restInv[b.Name] = VehicleProbe.InvertedSafe(ml); }
            }
            for (int k = 0; k < n; k++)
            {
                int f = start + k;
                pose.Clear();
                foreach (var b in walk)
                {
                    var v = src.Reset ? Identity() : spec.HeldOf(b.Name);
                    for (int c = 0; c < 13; c++) if (src.Has(b.Name, c)) v[c] = src.Eval(b.Name, c, f);
                    string mode = spec.ModeOf(b.Name);
                    float[][] rot;
                    if (mode == "QUATERNION") rot = QuatToMat3Normalized(new[] { v[QUAT], v[QUAT + 1], v[QUAT + 2], v[QUAT + 3] });
                    else if (mode == "XYZ") rot = EulXyzToMat3(new[] { v[EUL], v[EUL + 1], v[EUL + 2] });
                    else throw new LeftException($"a bone in rotation mode {mode} ('{b.Name}': only QUATERNION and XYZ are modelled)");
                    var chan = PchanToMat4(new[] { v[LOC], v[LOC + 1], v[LOC + 2] }, rot, new[] { v[SCALE], v[SCALE + 1], v[SCALE + 2] });
                    var rs = b.Parent != null ? VehicleProbe.MulM4(pose[b.Parent.Name], b.Offs) : b.MatrixLocal;
                    pose[b.Name] = BlenderDeploy.PoseFromChannel(rs, chan);
                    // the exporter's matrix of the bone, in its parent's rest frame
                    var boneM = VehicleProbe.ToRowMajor(pose[b.Name]);
                    float[] mat = b.Parent != null
                        ? VehicleProbe.MatMulMathutils(VehicleProbe.MatMulMathutils(restInv[b.Name], VehicleProbe.InvertedSafe(VehicleProbe.ToRowMajor(pose[b.Parent.Name]))), boneM)
                        : VehicleProbe.MatMulMathutils(restInv[b.Name], boneM);
                    VehicleProbe.Mat4ToLocRotSize(VehicleProbe.ToColumnMajor(mat), out var loc, out var rot3, out var size);
                    var quat = VehicleProbe.Mat3NormalizedToQuatFast(rot3);
                    sampled[b.Name][k] = new[] { loc[0], loc[1], loc[2], quat[0], quat[1], quat[2], quat[3], size[0], size[1], size[2] };
                }
            }

            // per bone: location, rotation_quaternion, scale
            foreach (var b in walk)
            {
                string mode = spec.ModeOf(b.Name);
                if (mode != "QUATERNION") res.EulerBones++;
                if (!src.Reset && Enumerable.Range(0, 13).All(c => !src.Has(b.Name, c))) res.HeldBones++;
                foreach (var (path, from, count) in new[] { ("translation", LOC, 3), ("rotation", QUAT, 4), ("scale", SCALE, 3) })
                {
                    int curveFrom = from, curveCount = count;
                    if (path == "rotation" && mode != "QUATERNION") { curveFrom = EUL; curveCount = 3; }
                    bool animated = Enumerable.Range(curveFrom, curveCount).Any(c => src.Has(b.Name, c));
                    string curveInterp = animated ? GroupInterpolation(src, b.Name, curveFrom, curveCount) : "LINEAR";
                    var frames = new List<int>(); for (int k = 0; k < n; k++) frames.Add(k);
                    // fcurve_is_constant: np.ptp per component (in double) below 0.0001
                    bool cst = true;
                    for (int c = 0; c < count && cst; c++)
                    {
                        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                        for (int k = 0; k < n; k++) { double val = sampled[b.Name][k][from + c]; if (val < lo) lo = val; if (val > hi) hi = val; }
                        if (!(hi - lo < 0.0001)) cst = false;
                    }
                    if (cst && n >= 2) { frames = new List<int> { 0, n - 1 }; res.Reduced++; } else if (n == 1) res.Single++; else res.Full++;
                    string interp;
                    if (frames.Count > 2) interp = curveInterp == "STEP" ? "STEP" : "LINEAR";
                    else if (frames.Count == 1) interp = !animated ? "STEP" : curveInterp == "CUBICSPLINE" ? "LINEAR" : curveInterp;
                    else
                    {
                        if (!animated) interp = "STEP";
                        else
                        {
                            bool same = true;
                            for (int c = 0; c < count; c++) if (!FloatsAreEqual(sampled[b.Name][frames[0]][from + c], sampled[b.Name][frames[1]][from + c], 1)) same = false;
                            interp = same ? "STEP" : "LINEAR";
                        }
                    }
                    if (interp == "STEP") res.Stepped++; else res.Linear++;
                    var times = new float[frames.Count]; var values = new List<float>();
                    for (int i = 0; i < frames.Count; i++)
                    {
                        times[i] = (float)((start + frames[i]) / Fps);
                        var s = sampled[b.Name][frames[i]];
                        values.AddRange(Transform(path, s, from, corr[b.Name]));
                    }
                    anim.Channels.Add(new Channel { Node = nodeOf[b.Name], Path = path, Interpolation = interp, Times = times, Values = values.ToArray(), Sampler = anim.Channels.Count,
                                                    MinTime = (start + frames[0]) / Fps, MaxTime = (start + frames[frames.Count - 1]) / Fps });
                }
            }
        }
    }

    static float[] Identity() => new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 1f, 1f, 0f, 0f, 0f };

    /// <summary>The action behind an animation's name: the legacy deploy action (BlenderDeploy.Result.Keys and Rekeyed,
    /// the garbage bones' curves gone) or a role clip (Role.Curves).</summary>
    static Source SourceOf(BlenderDeploy.Result r, string name, string active, HashSet<string> garbage, IEnumerable<string> boneNames)
    {
        var s = new Source { Name = name, Lo = int.MaxValue, Hi = int.MinValue };
        void Span(float frame) { int f = (int)frame; if (f < s.Lo) s.Lo = f; if (f > s.Hi) s.Hi = f; s.Any = true; }
        if (r.Actions7 != null && r.Actions7.Contains(name))
        {
            int nf = r.BakeFrameMax - r.BakeFrameMin + 1;
            s.Has = (bone, c) =>
            {
                if (garbage.Contains(bone) || c >= 10) return false;
                if (r.Rekeyed.TryGetValue(bone, out var rk)) return rk[c] != null && rk[c].Count > 0;
                return r.Keys.TryGetValue(bone, out var keys) && keys.Length > 0 && c < (r.ScaleKeys ? 10 : 7);
            };
            s.Eval = (bone, c, f) =>
            {
                if (r.Rekeyed.TryGetValue(bone, out var rk)) return BlenderFCurve.Evaluate(rk[c], (float)f);
                var keys = r.Keys[bone]; return keys[Math.Max(0, Math.Min(keys.Length - 1, f - r.BakeFrameMin))][c];
            };
            s.Keys = (bone, c) => r.Rekeyed.TryGetValue(bone, out var rk) ? rk[c] : null;
            s.DenseCount = (bone, c) => !r.Rekeyed.ContainsKey(bone) && r.Keys.TryGetValue(bone, out var keys) ? keys.Length : 0;
            foreach (string bone in boneNames)
            {
                if (garbage.Contains(bone)) continue;
                if (r.Rekeyed.TryGetValue(bone, out var rk)) { foreach (var list in rk) if (list != null) foreach (var k in list) Span(k.Frame); }
                else if (r.Keys.TryGetValue(bone, out var keys) && keys.Length > 0) { Span(r.BakeFrameMin); Span(r.BakeFrameMax); }
            }
            return s;
        }
        var role = r.Roles.FirstOrDefault(ro => ro.Name == name);
        if (role == null) return null;
        s.Has = (bone, c) => !garbage.Contains(bone) && role.Curves.TryGetValue(bone, out var ch) && ch[c] != null && ch[c].Count > 0;
        s.Eval = (bone, c, f) => BlenderFCurve.Evaluate(role.Curves[bone][c], (float)f);
        s.Keys = (bone, c) => role.Curves.TryGetValue(bone, out var ch) ? ch[c] : null;
        s.DenseCount = (bone, c) => 0;
        foreach (var kv in role.Curves) { if (garbage.Contains(kv.Key)) continue; foreach (var list in kv.Value) if (list != null) foreach (var k in list) Span(k.Frame); }
        return s;
    }

    /// <summary>get_channel_groups + needs_baking for one property's curve group: the glTF interpolation of the group's
    /// first curve's first key (BEZIER -> CUBICSPLINE, LINEAR, CONSTANT -> STEP), or LINEAR (the sampling fallback)
    /// when the group has to be baked - one key, an interpolation the exporter does not know, mixed interpolations,
    /// unequal key counts or key frames across the group.</summary>
    static string GroupInterpolation(Source src, string bone, int from, int count)
    {
        var lists = new List<List<BlenderDeploy.ArmKey>>();
        int dense = 0;
        for (int c = from; c < from + count; c++)
        {
            if (!src.Has(bone, c)) continue;
            var keys = src.Keys(bone, c);
            if (keys == null) dense = Math.Max(dense, src.DenseCount(bone, c)); else lists.Add(keys);
        }
        if (lists.Count == 0) return dense <= 1 ? "LINEAR" : "CUBICSPLINE";   // the bake's keys: Bezier, one per frame, all alike
        string first = lists[0][0].Interpolation ?? "BEZIER";
        if (first != "BEZIER" && first != "LINEAR" && first != "CONSTANT") return "LINEAR";
        foreach (var l in lists) foreach (var k in l) if ((k.Interpolation ?? "BEZIER") != first) return "LINEAR";
        if (lists.Any(l => l.Count != lists[0].Count)) return "LINEAR";
        if (lists[0].Count <= 1) return "LINEAR";
        for (int i = 0; i < lists[0].Count; i++) foreach (var l in lists) if (l[i].Frame != lists[0][i].Frame) return "LINEAR";
        return first == "BEZIER" ? "CUBICSPLINE" : first == "LINEAR" ? "LINEAR" : "STEP";
    }

    /// <summary>gltf2_blender_math.transform of one sample (loc 3, quat 4 as (w, x, y, z), size 3 at `from`) through the
    /// correction matrix (row-major items), then mathutils_to_gltf.</summary>
    internal static float[] Transform(string path, float[] s, int from, float[] correction)
    {
        var m = new float[16]; m[0] = m[5] = m[10] = m[15] = 1f;
        if (path == "translation")
        {
            m[3] = s[from]; m[7] = s[from + 1]; m[11] = s[from + 2];   // Matrix.Translation: the fourth column
            var t = VehicleProbe.MatMulMathutils(correction, m);
            return new[] { t[3], t[7], t[11] };                        // to_translation
        }
        if (path == "rotation")
        {
            var q = new[] { s[from], s[from + 1], s[from + 2], s[from + 3] };
            VehicleProbe.NormalizeQt(q);                               // rotation.normalize()
            var r3 = QuatToMat3(q);                                    // to_matrix().to_4x4(): mat[col][row]
            for (int col = 0; col < 3; col++) for (int row = 0; row < 3; row++) m[row * 4 + col] = r3[col][row];
            var t = VehicleProbe.MatMulMathutils(correction, m);
            // to_quaternion() of a 4x4: mat4_to_quat - the three axes normalized, the sign checks, the fast conversion
            VehicleProbe.Mat4ToLocRotSize(VehicleProbe.ToColumnMajor(t), out _, out var rot, out _);
            var quat = VehicleProbe.Mat3NormalizedToQuat(rot);
            return new[] { quat[1], quat[2], quat[3], quat[0] };
        }
        m[0] = s[from]; m[5] = s[from + 1]; m[10] = s[from + 2];
        var u = VehicleProbe.MatMulMathutils(correction, m);
        VehicleProbe.Mat4ToLocRotSize(VehicleProbe.ToColumnMajor(u), out _, out _, out var size);   // to_scale: mat3_to_rot_size
        return size;
    }

    /// <summary>BKE_pchan_rot_to_mat3 for a QUATERNION bone: normalize_qt_qt then quat_to_mat3 (mat[col][row]).</summary>
    static float[][] QuatToMat3Normalized(float[] quat) { var q = (float[])quat.Clone(); VehicleProbe.NormalizeQt(q); return QuatToMat3(q); }

    /// <summary>quat_to_mat3 as Blender writes it: the components times sqrt(2) in double, the products in double, each
    /// entry rounded once.</summary>
    internal static float[][] QuatToMat3(float[] q)
    {
        const double M_SQRT2 = 1.4142135623730951;
        double q0 = M_SQRT2 * (double)q[0], q1 = M_SQRT2 * (double)q[1], q2 = M_SQRT2 * (double)q[2], q3 = M_SQRT2 * (double)q[3];
        double qda = q0 * q1, qdb = q0 * q2, qdc = q0 * q3, qaa = q1 * q1, qab = q1 * q2, qac = q1 * q3, qbb = q2 * q2, qbc = q2 * q3, qcc = q3 * q3;
        return new[]
        {
            new[] { (float)(1.0 - qbb - qcc), (float)(qdc + qab), (float)(-qdb + qac) },
            new[] { (float)(-qdc + qab), (float)(1.0 - qaa - qcc), (float)(qda + qbc) },
            new[] { (float)(qdb + qac), (float)(-qda + qbc), (float)(1.0 - qaa - qbb) },
        };
    }

    /// <summary>eulO_to_mat3 for the XYZ order (i, j, k = 0, 1, 2, no parity): cos and sin in double (the C runtime's),
    /// the products in double, each entry rounded once; mat[col][row].</summary>
    internal static float[][] EulXyzToMat3(float[] e)
    {
        double ti = e[0], tj = e[1], th = e[2];
        double ci = BlenderTrig.Cos(ti), cj = BlenderTrig.Cos(tj), ch = BlenderTrig.Cos(th), si = BlenderTrig.Sin(ti), sj = BlenderTrig.Sin(tj), sh = BlenderTrig.Sin(th);
        double cc = ci * ch, cs = ci * sh, sc = si * ch, ss = si * sh;
        var M = new[] { new float[3], new float[3], new float[3] };
        M[0][0] = (float)(cj * ch); M[1][0] = (float)(sj * sc - cs); M[2][0] = (float)(sj * cc + ss);
        M[0][1] = (float)(cj * sh); M[1][1] = (float)(sj * ss + cc); M[2][1] = (float)(sj * cs - sc);
        M[0][2] = (float)(-sj); M[1][2] = (float)(cj * si); M[2][2] = (float)(cj * ci);
        return M;
    }

    /// <summary>BKE_pchan_to_mat4: the rotation times the size matrix (mul_m3_m3m3, left to right in float32), copied into
    /// a 4x4 with the location in the fourth column; column-major as the probe holds matrices.</summary>
    static float[] PchanToMat4(float[] loc, float[][] rot, float[] size)
    {
        var bm = new float[16];
        for (int i = 0; i < 3; i++)
        {
            float s0 = i == 0 ? size[0] : 0f, s1 = i == 1 ? size[1] : 0f, s2 = i == 2 ? size[2] : 0f;
            for (int j = 0; j < 3; j++)
                bm[i * 4 + j] = (float)((float)((float)(s0 * rot[0][j]) + (float)(s1 * rot[1][j])) + (float)(s2 * rot[2][j]));
        }
        bm[12] = loc[0]; bm[13] = loc[1]; bm[14] = loc[2]; bm[15] = 1f;
        return bm;
    }

    /// <summary>EXPP_FloatsAreEqual: two floats within `steps` representable values of each other (signed zeros equal).</summary>
    internal static bool FloatsAreEqual(float af, float bf, int steps)
    {
        int ai = BitConverter.ToInt32(BitConverter.GetBytes(af), 0), bi = BitConverter.ToInt32(BitConverter.GetBytes(bf), 0);
        int test = (ai ^ bi) >> 31;                       // SIGNMASK: all ones when the signs differ
        int diff = (ai ^ (test & 0x7fffffff)) - bi;
        int v1 = steps + diff, v2 = steps - diff;
        return (v1 | v2) >= 0;
    }
}
