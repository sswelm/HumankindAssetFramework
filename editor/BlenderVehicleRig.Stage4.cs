// BlenderVehicleRig.Stage4.cs - vehicle_rig.py's stage 4, the ACTIONS (lines 2905-3711), in C# (step (e) of the vehicle_rig port,
// 2026-10-17; docs/Review-Backlog.md item (5)). Part e1, the frame every animated rig shares:
//   - the Spin action (bpy.data.actions.new: a name the existing actions take is made unique; bpy.data.actions stays sorted by name),
//     the armature's active action; its slot (OB<armature>) appears with the first key;
//   - the shared clip: frame_start 0, frame_end the longest period (the Spin frames, the rock's when it is on), each subsystem
//     repeating a whole number of times over it (_fitted_cycle_count: max(1, int(round(clip / period))));
//   - the WAVE ROCK on the body bone: the roll axis (forced X/Y, else the meshes' longer horizontal extent), turned by the heading,
//     the pitch axis across it; per sample Quaternion(roll axis, A sin) @ Quaternion(pitch axis, B sin(... + phase)), to_euler('XYZ'),
//     keyed at int(round(t * clip)) (a sample on a frame already keyed moves that key);
//   - the WHEELS: rolling contact - a ground wheel turns at degrees x (the largest wheel / its own), snapped to its spoke symmetry
//     (the angular autocorrelation of its mesh's rim vertices: CPython's cos/sin sums, compensated, and hypot), an airborne wheel
//     keeps the dial, the idler (static tracks: the rearmost cluster per side) the dial x the idler speed, snapped; keys at 0 and the
//     clip on rotation_euler (XYZ); a ROTOR keyed every frame on rotation_quaternion about its local axle (Y, a tail X);
//   - every Spin key made LINEAR; the objects that are neither the rig's meshes nor the armature removed; the actions not named
//     Spin/Deploy/Recoil/Furl purged (a line each); every non-armature object's animation data cleared.
// The pose at the end: each keyed channel holds its last key (the last frame_set evaluates at the clip's end, where every curve has a
// key), the rest as stage 3 left them.
// Left to Blender by name (parts e2, e3): the rowing stroke, the Furl clip with the sail and flag holds, the Deploy clip, the Recoil
// clip; a clip longer than 100,000 frames.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

public static partial class BlenderVehicleRig
{
    /// <summary>What stage 3 hands stage 4: the wheel clusters and their bones, the joined meshes (objs), the body bone, and the branches
    /// a later part of step (e) ports.</summary>
    public sealed class RigState
    {
        public List<(double m, float[] c, float[] sz, List<string> names, float[] axle, bool rotor, bool tail)> Clusters;
        public List<string> ClusterBones = new List<string>();
        public List<BlenderRigAnim.SceneObject> Objs;
        public BlenderRigAnim.SceneObject Arm;
        public string BodyBone;
        public Dictionary<string, int> BoneId = new Dictionary<string, int>(StringComparer.Ordinal);
        public int OarBones, TrailBones;
        public bool SailClip, FlagClip, Recoil;
    }

    const int MaxClipFrames = 100000;

    public static void RunStage4(BlenderRigAnim.Stage1 s, Args a)
    {
        if (s.Fallback != null || s.Exits || s.Dies != null) return;
        var st = s.VehicleState as RigState;
        if (st == null) { s.Fallback = "stage 4: no rig from stage 3"; return; }
        if (st.OarBones > 0) { s.Fallback = "stage 4: the rowing stroke (e2) is not modelled yet"; return; }
        if (st.SailClip || st.FlagClip) { s.Fallback = "stage 4: the Furl clip and the sail and flag holds (e2) is not modelled yet"; return; }
        if (st.TrailBones > 0) { s.Fallback = "stage 4: the Deploy clip (e3) is not modelled yet"; return; }
        if (st.Recoil) { s.Fallback = "stage 4: the Recoil clip (e3) is not modelled yet"; return; }
        if (double.IsNaN(a.Degrees) || double.IsInfinity(a.Degrees) || double.IsNaN(a.RockHeading) || double.IsInfinity(a.RockHeading)
            || double.IsNaN(a.RockPitchPhase) || double.IsInfinity(a.RockPitchPhase))
        { s.Fallback = "stage 4: the clip of a non-finite Spin angle, rock heading or rock phase is not modelled yet"; return; }   // round()/sin() raise in Python
        if (s.Objects.Any(o => o.EvalStale)) { s.Fallback = "stage 4: the evaluation of an object with a stale evaluated copy is not modelled yet"; return; }
        var ad = s.Armature; var arm = st.Arm;
        // the shared clip
        BigInteger frames = a.Frames;
        var periods = new List<BigInteger> { frames };
        if (a.RockOn) periods.Add(a.RockFrames);
        BigInteger clipBig = periods.Max();
        if (clipBig > MaxClipFrames) { s.Fallback = "stage 4: the clip of more than 100,000 frames is not modelled yet"; return; }
        int clip = (int)clipBig;
        int Fitted(BigInteger period) { double r = PyRound((double)clip / (double)BigInteger.Max(BigInteger.One, period)); return (int)Math.Max(1.0, r); }
        int spinRepeats = Fitted(frames);
        int rockRepeats = a.RockOn ? Fitted(a.RockFrames) : 0;

        // bpy.data.actions.new("Spin"): unique among the actions there are; the armature's active action
        var pool = new BlenderNames.NamePool();
        foreach (var x in s.Actions) pool.Unique(x.Name);
        var act = new BlenderRigAnim.ActionData { Name = pool.Unique("Spin") };
        s.Actions.Add(act); BlenderExportTree.StableSort(s.Actions, (x, y) => BlenderDeploy.IdNameCmp(x.Name, y.Name));
        arm.Action = act.Name; arm.Slot = null;
        s.FrameStart = 0; s.FrameEnd = clip;
        var curves = new Dictionary<(string bone, string prop, int index), List<BlenderDeploy.ArmKey>>();
        var keyAt = new Dictionary<(string bone, string prop, int index), Dictionary<int, BlenderDeploy.ArmKey>>();
        var curveOrder = new List<(string bone, string prop, int index)>();
        var dirty = new HashSet<(string bone, string prop, int index)>();
        bool anyFrameSet = false;
        void Insert(string bone, string prop, float[] values, int frame)
        {
            if (arm.Slot == null) { act.Slots.Add(new BlenderRigAnim.SlotData { Identifier = "OB" + arm.Name, Object = arm }); arm.Slot = "OB" + arm.Name; }
            for (int i = 0; i < values.Length; i++)
            {
                var k3 = (bone, prop, i);
                if (!curves.TryGetValue(k3, out var list)) { curves[k3] = list = new List<BlenderDeploy.ArmKey>(); keyAt[k3] = new Dictionary<int, BlenderDeploy.ArmKey>(); curveOrder.Add(k3); }
                // insert_vert_fcurve: a key within BEZT_BINARYSEARCH_THRESH of the frame (the frames are whole: the same frame) is REPLACED -
                // its value moved by the float delta (replace_bezt_keyframe_ypos); else a new BEZIER key in frame order
                if (keyAt[k3].TryGetValue(frame, out var key)) { float dy = (float)(values[i] - key.Value); key.Value = (float)(key.Value + dy); }
                else
                {
                    key = new BlenderDeploy.ArmKey { Frame = frame, Value = values[i] }; keyAt[k3][frame] = key;
                    if (list.Count == 0 || list[list.Count - 1].Frame < frame) list.Add(key); else { list.Add(key); list.Sort((p, q) => p.Frame.CompareTo(q.Frame)); }
                }
                dirty.Add(k3);
            }
        }
        // frame_set(f) writes every keyed channel's curve value into the pose; a property the script assigns afterwards holds the assigned
        // value until the next frame_set. Only the states something reads are materialised: the channels assigned since the last frame_set
        // are kept, the rest take their curves at that frame (any key added since went to an assigned channel)
        var assignedSince = new HashSet<(string bone, string prop)>();
        void FrameSet(int f) { anyFrameSet = true; s.FrameCurrent = f; assignedSince.Clear(); }
        // frame_set(f) for the armature: every keyed channel takes its curve's value at f (BKE_fcurve evaluation of the BEZIER keys)
        void EvaluatePose(int f)
        {
            foreach (var k3 in dirty) BlenderFCurve.RecalcHandles(curves[k3]);
            dirty.Clear();
            foreach (var k3 in curveOrder)
            {
                float v = BlenderFCurve.Evaluate(curves[k3], f, constant: true);
                int id = st.BoneId[k3.bone];
                if (assignedSince.Contains((k3.bone, k3.prop))) continue;
                if (k3.prop == "rotation_euler") { var e0 = ad.PoseEuler.TryGetValue(id, out var pe) ? (float[])pe.Clone() : new float[3]; e0[k3.index] = v; ad.PoseEuler[id] = e0; }
                else if (k3.prop == "rotation_quaternion") { var q0 = (float[])ad.PoseProps[id][1].Clone(); q0[k3.index] = v; ad.PoseProps[id][1] = q0; }
            }
        }
        void SetPose(string bone, string mode, float[] eul, float[] quat)
        {
            int id = st.BoneId[bone];
            if (eul != null) assignedSince.Add((bone, "rotation_euler"));
            if (quat != null) assignedSince.Add((bone, "rotation_quaternion"));
            ad.RotMode[id] = mode;
            if (eul != null) ad.PoseEuler[id] = eul;
            if (quat != null) ad.PoseProps[id][1] = quat;
        }

        // ---- the WAVE ROCK on the body bone
        if (a.RockOn)
        {
            int rollI; string why;
            if (a.RockAxisArg == "X" || a.RockAxisArg == "Y") { rollI = a.RockAxisArg == "X" ? 0 : 1; why = "forced " + a.RockAxisArg; Note(s, "the rock's axis forced"); }
            else
            {
                var rmn = new[] { 1e18f, 1e18f, 1e18f }; var rmx = new[] { -1e18f, -1e18f, -1e18f };
                foreach (var ro in st.Objs)
                {
                    LocalBox(ro.Mesh, out var lmn, out var lmx);
                    VehicleProbe.WorldBox(VehicleProbe.ToRowMajor(ro.World), lmn, lmx, out var rc, out var rs);
                    for (int k = 0; k < 3; k++)
                    {
                        double lo = rc[k] - rs[k] / 2.0, hi = rc[k] + rs[k] / 2.0;
                        if (lo < rmn[k]) rmn[k] = (float)lo;   // min(_rmn[k], ...) into the float32 Vector
                        if (hi > rmx[k]) rmx[k] = (float)hi;
                    }
                }
                var span = Sub(rmx, rmn);
                rollI = span[0] >= span[1] ? 0 : 1;
                double hiS = span[1] > span[0] ? span[1] : span[0], loS = span[1] < span[0] ? span[1] : span[0];
                why = "auto (" + "XYZ"[rollI] + " longer: " + BlenderRigAnim.PyFixed(hiS, 2) + " vs " + BlenderRigAnim.PyFixed(loS, 2) + ")";
            }
            var rollAx = new float[3]; rollAx[rollI] = 1f;
            if (Math.Abs(a.RockHeading) > 1e-6) Note(s, "the rock turned by a heading");
            if (Math.Abs(a.RockHeading) > 1e-6) rollAx = RotateQt(rollAx, BlenderDeploy.QuaternionAxisAngle(new[] { 0f, 0f, 1f }, a.RockHeading * (Math.PI / 180.0)));
            var pitchAx = Normalized(Cross(new[] { 0f, 0f, 1f }, rollAx));
            string hull = st.BodyBone;
            SetPose(hull, "XYZ", null, null);
            BigInteger stepsBig = BigInteger.Max(BigInteger.Max(clip, 24 * rockRepeats), 16 * BigInteger.Max(a.RockRollCycles, a.RockPitchCycles) * rockRepeats);
            if (stepsBig > 10 * MaxClipFrames) { s.Fallback = "stage 4: the rock of more than 1,000,000 samples is not modelled yet"; return; }
            int steps = (int)stepsBig;
            if (steps > clip) Note(s, "the rock sampled past one key per frame (keys replaced)");
            if (rockRepeats > 1 || spinRepeats > 1) Note(s, "a subsystem repeating over the shared clip");
            double rollAmp = a.RockDeg * (Math.PI / 180.0), pitchAmp = a.RockPitchDeg * (Math.PI / 180.0), phase = a.RockPitchPhase * (Math.PI / 180.0);
            double rc2 = (double)a.RockRollCycles, pc2 = (double)a.RockPitchCycles;
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps;
                int f = (int)PyRound(t * clip);
                FrameSet(f);
                var q1 = BlenderDeploy.QuaternionAxisAngle(rollAx, rollAmp * BlenderTrig.Sin(2.0 * Math.PI * rc2 * rockRepeats * t));
                var q2 = BlenderDeploy.QuaternionAxisAngle(pitchAx, pitchAmp * BlenderTrig.Sin(2.0 * Math.PI * pc2 * rockRepeats * t + phase));
                var eul = BlenderDeploy.QuatToEulXYZ((float[])BlenderPosedState.MulQtQt(q1, q2).Clone());   // to_euler('XYZ'): normalize_qt_qt, quat_to_eul
                SetPose(hull, "XYZ", eul, null);
                Insert(hull, "rotation_euler", eul, f);
            }
            s.Logs4.Add("VEHICLE wave rock: roll " + BlenderRigAnim.PyFixed(a.RockDeg, 1) + " deg x" + a.RockRollCycles + " about (" + P2(rollAx[0]) + "," + P2(rollAx[1]) + "," + P2(rollAx[2])
                + ") | pitch " + BlenderRigAnim.PyFixed(a.RockPitchDeg, 1) + " deg x" + a.RockPitchCycles + " phase " + BlenderRigAnim.PyFixed(a.RockPitchPhase, 0) + " about (" + P2(pitchAx[0]) + ","
                + P2(pitchAx[1]) + "," + P2(pitchAx[2]) + ") | " + clip + " frames, " + (steps + 1) + " keys on '" + hull + "' — " + why
                + (Math.Abs(a.RockHeading) > 1e-6 ? ", heading " + PySigned(BlenderRigAnim.PyFixed(a.RockHeading, 0)) + " deg" : ""));
            Note(s, "the wave rock keys");
            if (!EvaluateObjects(s, s.FrameCurrent)) return;
        }

        // ---- the WHEELS: rolling contact, the spoke-symmetry snap, the rotors keyed every frame
        var clusters = st.Clusters;
        double ddRef = 0.0; bool firstM = true;
        foreach (var cl in clusters) { if (firstM || cl.m > ddRef) ddRef = cl.m; firstM = false; }   // max((cl["m"] ...), default=0.0)
        var carrier = new HashSet<int>(); var idler = new HashSet<int>();
        bool hadStatic = a.TracksStatic && a.Names.TryGetValue("tracks", out var tl) && tl.Count > 0;
        if (hadStatic && clusters.Count > 0)
        {
            foreach (bool sidePos in new[] { true, false })
            {
                var scls = Enumerable.Range(0, clusters.Count).Where(i => (clusters[i].c[1] >= 0) == sidePos).ToList();
                if (scls.Count == 0) continue;
                int fcl = scls[0], rcl = scls[0];
                foreach (int i in scls) { if (clusters[i].c[0] > clusters[fcl].c[0]) fcl = i; if (clusters[i].c[0] < clusters[rcl].c[0]) rcl = i; }
                carrier.Add(fcl); carrier.Add(rcl); idler.Add(rcl);
            }
            s.Logs4.Add("VEHICLE wrap carriers classified geometrically (static tracks): " + carrier.Count + " carrier(s), " + idler.Count + " idler(s)");
            Note(s, "static tracks: the wrap carriers classified");
        }
        // the ground band: every scene mesh's bound_box corners in the world - Object.bound_box is the last evaluation's: after the rock's
        // frame_set the meshes skinned to the rig are deformed by the posed armature (the evaluated mesh's bounds)
        Dictionary<int, float[]> poseMats = null;
        if (anyFrameSet) { EvaluatePose(s.FrameCurrent); poseMats = VehiclePoseMats(ad); }
        double gzMin = 0.0, gzH = 1.0; bool anyG = false; double gzMax = 0.0;
        foreach (var o in s.Objects)
        {
            if (o.Type != "MESH" || o.Mesh == null || o.Mesh.Vertices == 0) continue;
            var corners = poseMats != null && o.Mesh.ArmatureModifier == arm.Name ? DeformedCorners(o, arm, ad, poseMats, st) : WorldCorners(o);
            if (poseMats != null && o.Mesh.ArmatureModifier == arm.Name) Note(s, "the ground band on the rock-deformed meshes");
            foreach (var p in corners)
            {
                if (!anyG || p[2] < gzMin) gzMin = p[2];
                if (!anyG || p[2] > gzMax) gzMax = p[2];
                anyG = true;
            }
        }
        if (anyG) gzH = gzMax - gzMin;
        bool Grounded(int ci) => (clusters[ci].c[2] - 0.5 * clusters[ci].sz[2]) <= gzMin + 0.15 * PyMax(gzH, 1e-6);
        var finalDeg = new Dictionary<string, double>(StringComparer.Ordinal); var finalOrder = new List<string>();
        var airborne = new List<string>();
        double degrees = a.Degrees;
        for (int bi = 0; bi < st.ClusterBones.Count; bi++)
        {
            string bname = st.ClusterBones[bi];
            double degI = degrees;
            if (idler.Contains(bi))
            {
                degI = degrees * a.IdlerSpeedMul;
                int nsym = WheelSymmetry(s, st, bname);
                if (nsym > 0)
                {
                    double stepd = 360.0 / nsym, snap = PyRound(degI / stepd) * stepd;
                    if (Math.Abs(snap) < 1e-6) snap = stepd * (degI >= 0 ? 1.0 : -1.0);
                    s.Logs4.Add("VEHICLE idler " + bname + ": symmetry " + nsym + "-fold (" + BlenderRigAnim.PyFixed(stepd, 1) + " deg steps), auto pop-free " + BlenderRigAnim.PyFixed(snap, 1)
                                + " deg (target " + BlenderRigAnim.PyFixed(degI, 1) + ")");
                    degI = snap; Note(s, "an idler snapped to its spoke symmetry");
                }
            }
            else if (ddRef > 1e-6 && bi < clusters.Count && clusters[bi].m > 1e-6 && !carrier.Contains(bi) && !clusters[bi].rotor && !Grounded(bi))
                airborne.Add(bname);
            else if (ddRef > 1e-6 && bi < clusters.Count && clusters[bi].m > 1e-6 && !carrier.Contains(bi) && !clusters[bi].rotor)
            {
                degI = degrees * (ddRef / clusters[bi].m);   // no belt: the drive wheel's rim ratio
                degI *= a.RoadSpeedMul;
                if (Math.Abs(a.RoadSpeedMul - 1.0) < 1e-6)
                {
                    int nsym = WheelSymmetry(s, st, bname);
                    if (nsym > 0)
                    {
                        double stepd = 360.0 / nsym, snap = PyRound(degI / stepd) * stepd;
                        if (Math.Abs(snap) < 1e-6) snap = stepd * (degI >= 0 ? 1.0 : -1.0);
                        degI = snap; Note(s, "a ground wheel snapped to its spoke symmetry");
                    }
                    else Note(s, "a ground wheel without a spoke symmetry");
                }
                else Note(s, "a ground wheel at a road speed off 1 (no snap)");
            }
            if (!finalDeg.ContainsKey(bname)) finalOrder.Add(bname);
            finalDeg[bname] = degI;
        }
        foreach (string bname in st.ClusterBones)
        {
            int ci = int.Parse(bname.Split('_')[1], CultureInfo.InvariantCulture);
            double dg = finalDeg.TryGetValue(bname, out var fdv) ? fdv : degrees;
            if (ci < clusters.Count && clusters[ci].rotor)
            {
                double tot = (dg * spinRepeats) * (Math.PI / 180.0);
                var lax = clusters[ci].tail ? new[] { 1f, 0f, 0f } : new[] { 0f, 1f, 0f };
                SetPose(bname, "QUATERNION", null, null);
                for (int f = 0; f <= clip; f++)
                {
                    FrameSet(f);
                    var q = BlenderDeploy.QuaternionAxisAngle(lax, tot * (f / (double)clip));
                    SetPose(bname, "QUATERNION", null, q);
                    Insert(bname, "rotation_quaternion", q, f);
                }
                Note(s, "a rotor keyed every frame");
                continue;
            }
            SetPose(bname, "XYZ", new float[3], null);
            FrameSet(0);
            SetPose(bname, "XYZ", new float[3], null);
            Insert(bname, "rotation_euler", new float[3], 0);
            FrameSet(clip);
            var e = new[] { 0f, (float)((dg * spinRepeats) * (Math.PI / 180.0)), 0f };
            SetPose(bname, "XYZ", e, null);
            Insert(bname, "rotation_euler", e, clip);
        }
        if (st.ClusterBones.Count > 0) Note(s, "the wheels keyed");
        var chg = finalOrder.Where(b => Math.Abs(finalDeg[b] - degrees) > 0.5).OrderBy(b => b, StringComparer.Ordinal).ToList();
        if (finalOrder.Count > 0 && chg.Count > 0)
            s.Logs4.Add("VEHICLE rolling-contact speeds (largest wheel keeps " + BlenderRigAnim.PyFixed(degrees, 0) + " deg): " + string.Join(", ", chg.Select(b => b + "=" + BlenderRigAnim.PyFixed(finalDeg[b], 1))));
        if (airborne.Count > 0)
        {
            s.Logs4.Add("VEHICLE rolling-contact: " + airborne.Count + " wheel(s) above the ground band keep the dialed " + BlenderRigAnim.PyFixed(degrees, 0) + " deg (propellers/fans do not roll): "
                        + string.Join(", ", airborne.OrderBy(b => b, StringComparer.Ordinal)));
            Note(s, "an airborne wheel");
        }

        if (anyFrameSet && !EvaluateObjects(s, s.FrameCurrent)) return;
        if (anyFrameSet) { EvaluatePose(s.FrameCurrent); ad.PoseMats = VehiclePoseMats(ad); }
        foreach (var k3 in dirty) BlenderFCurve.RecalcHandles(curves[k3]);

        // the curves into the action, every Spin key LINEAR (the handles stay where the auto-clamped recalculation put them)
        var slot = act.Slots.FirstOrDefault();
        foreach (var k3 in curveOrder)
        {
            var fc = new BlenderRigAnim.FCurveData { DataPath = "pose.bones[\"" + k3.bone + "\"]." + k3.prop, Index = k3.index, Bone = k3.bone, Prop = k3.prop };
            foreach (var k in curves[k3]) fc.Keys.Add(new BlenderRigAnim.KeyData { Frame = k.Frame, Value = k.Value, LeftX = k.LeftX, LeftY = k.LeftY, RightX = k.RightX, RightY = k.RightY, Interpolation = "LINEAR" });
            slot.FCurves.Add(fc);
        }
        // Action.frame_range: the keys' extent, (0, 0) for an action without keys (Blender's dump)
        if (curveOrder.Count > 0) { act.FrameStart = curves.Values.Min(l => l[0].Frame); act.FrameEnd = curves.Values.Max(l => l[l.Count - 1].Frame); }
        else { act.FrameStart = 0f; act.FrameEnd = 0f; }

        // strip the leftovers: every object that is neither a kept mesh nor the armature
        var keep = new HashSet<BlenderRigAnim.SceneObject>(st.Objs) { arm };
        foreach (var o in s.Objects.ToList())
            if (!keep.Contains(o)) { s.Objects.Remove(o); s.Names.ObjectPool.Remove(o.Name); Note(s, "a leftover object removed before export"); }
        // the actions not authored here
        foreach (var x in s.Actions.ToList())
        {
            if (x.Name == "Spin" || x.Name == "Deploy" || x.Name == "Recoil" || x.Name == "Furl") continue;
            s.Logs4.Add("VEHICLE purged leftover source clip '" + x.Name + "' (only 'Spin'/'Deploy'/'Recoil'/'Furl' are authored here)");
            s.Actions.Remove(x); Note(s, "a leftover source clip purged");
            foreach (var o in s.Objects) if (o.Action == x.Name) { o.Action = null; o.Slot = null; }   // bpy.data.actions.remove unlinks its users
        }
        foreach (var o in s.Objects) if (o.Type != "ARMATURE" && o.Action != null) { o.Action = null; o.Slot = null; Note(s, "an object's animation data cleared"); }
        if (!anyFrameSet) Note(s, "no frame set in stage 4 (the scene's current frame kept)");
    }

    /// <summary>scene.frame_set(f)'s depsgraph evaluation for the objects (the armature's pose: EvaluatePose): every
    /// non-armature object's active slot evaluated at f - the original's property written only where the value differs
    /// (animsys_write_orig_anim_rna) -, its local matrix and its world (its parent's world, the parent inverse, the local). False, with the
    /// job left to Blender, for a shape this does not model (a bone parent, an evaluated copy left stale).</summary>
    static bool EvaluateObjects(BlenderRigAnim.Stage1 s, int f)
    {
        bool any = false;
        foreach (var o in s.Objects)
        {
            if (o.Type == "ARMATURE" || o.Action == null) continue;
            var act = s.Actions.FirstOrDefault(x => x.Name == o.Action);
            var slot = act?.Slots.FirstOrDefault(sl => sl.Identifier == o.Slot);
            if (slot == null) continue;
            if (o.ParentBone != null || o.EvalStale) { s.Fallback = "stage 4: the evaluation of an animated object under a bone or with a stale copy is not modelled yet"; return false; }
            var props = BlenderRigAnim.CloneProps(o.Props); var stored = BlenderRigAnim.CloneProps(o.Props);
            foreach (var fc in slot.FCurves)
            {
                if (fc.Keys.Count == 0 || fc.Bone != null) continue;
                float v = BlenderRigAnim.EvaluateCurve(fc.Keys, f);
                int ci = BlenderRigAnim.ChannelIndex(fc.Prop);
                props[ci][fc.Index] = v; if (stored[ci][fc.Index] != v) stored[ci][fc.Index] = v;
                any = true;
            }
            o.Props = stored; o.Local = VehicleProbe.ObjectMatrix(props[0], props[1], props[2]);
        }
        var memo = new Dictionary<BlenderRigAnim.SceneObject, float[]>();
        float[] Eval(BlenderRigAnim.SceneObject o)
        {
            if (memo.TryGetValue(o, out var w)) return w;
            if (o.Parent == null) w = o.Local;
            else { var pm = Eval(o.Parent); if (o.ParentInverse != null) pm = VehicleProbe.MulM4(pm, o.ParentInverse); w = VehicleProbe.MulM4(pm, o.Local); }
            memo[o] = w; return w;
        }
        foreach (var o in s.Objects) { o.World = Eval(o); o.Dirty = false; }
        if (any) Note(s, "an animated object evaluated by frame_set");
        return true;
    }

    /// <summary>BKE_pose_where_is for the vehicle armature: each bone's channel matrix from its location, rotation (the Euler's
    /// eulO_to_mat3 for an XYZ bone, the quaternion's otherwise) and scale, composed under its parent as VehicleProbe.PoseMatrices does.</summary>
    static Dictionary<int, float[]> VehiclePoseMats(BlenderRigAnim.ArmatureData ad)
    {
        var arm = ad.Rest; var pose = new Dictionary<int, float[]>();
        foreach (int b in arm.Bones)
        {
            var p = ad.PoseProps[b];
            var chan = ad.RotMode.TryGetValue(b, out var rm) && rm == "XYZ"
                ? BlenderChannelExport.PchanToMat4(p[0], BlenderChannelExport.EulXyzToMat3(ad.PoseEuler.TryGetValue(b, out var e) ? e : new float[3]), p[2])
                : VehicleProbe.ObjectMatrix(p[0], p[1], p[2], true);
            int parent = arm.Parent[b];
            var rotscale = parent >= 0 ? VehicleProbe.MulM4(pose[parent], arm.OffsBone[b]) : arm.ArmMat[b];
            var outm = VehicleProbe.MulM4(rotscale, chan);
            float x = chan[12], y = chan[13], z = chan[14];
            for (int k = 0; k < 3; k++)
                outm[12 + k] = (float)((float)((float)((float)(x * rotscale[k]) + (float)(y * rotscale[4 + k])) + (float)(z * rotscale[8 + k])) + rotscale[12 + k]);
            for (int k = 0; k < 12; k++) if (k % 4 != 3) outm[k] = (float)(outm[k] * 1f);
            pose[b] = outm;
        }
        return pose;
    }

    /// <summary>The evaluated mesh's bound_box corners in the world: the Armature modifier's linear deform (BKE_armature_deform_coords:
    /// each weighted bone's pose_mat times the inverse of its arm_mat, the vertex in the armature's space and back, the weighted sum over
    /// the contribution), the positions' min and max, the eight corners through matrix_world.</summary>
    static List<float[]> DeformedCorners(BlenderRigAnim.SceneObject mo, BlenderRigAnim.SceneObject armO, BlenderRigAnim.ArmatureData ad, Dictionary<int, float[]> poseMats, RigState st)
    {
        var d = mo.Mesh;
        var armatureToTarget = VehicleProbe.MulM4(BlenderEigen.InvertM4(mo.World), armO.World);
        var targetToArmature = BlenderEigen.InvertM4(armatureToTarget);
        var chan = new Dictionary<int, float[]>(); var groupBone = new int[d.Groups.Count];
        for (int g = 0; g < d.Groups.Count; g++)
        {
            int b = st.BoneId.TryGetValue(d.Groups[g], out var id) ? id : -1; groupBone[g] = b;
            if (b >= 0 && !chan.ContainsKey(b)) chan[b] = VehicleProbe.MulM4(poseMats[b], BlenderEigen.InvertM4(ad.Rest.ArmMat[b]));
        }
        var mn = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity }; var mx = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        for (int v = 0; v < d.Vertices; v++)
        {
            var co = BlenderRigAnim.TransformPoint(targetToArmature, new[] { d.Positions[3 * v], d.Positions[3 * v + 1], d.Positions[3 * v + 2] });
            float contrib = 0f; var delta = new float[3];
            if (d.DefNr != null && d.DefNr[v] != null)
                for (int i = 0; i < d.DefNr[v].Count; i++)
                {
                    int g = d.DefNr[v][i]; if (g < 0 || g >= groupBone.Length || groupBone[g] < 0) continue;
                    float w = d.DefWeight[v][i];
                    if (w != 0f)
                    {
                        var tp = BlenderRigAnim.TransformPoint(chan[groupBone[g]], co);
                        for (int k = 0; k < 3; k++) delta[k] = (float)(delta[k] + (float)(w * (float)(tp[k] - co[k])));
                    }
                    contrib = (float)(contrib + w);
                }
            if (contrib > 0.0001f) { float sf = (float)(1.0f / contrib); for (int k = 0; k < 3; k++) co[k] = (float)(co[k] + (float)(delta[k] * sf)); }
            co = BlenderRigAnim.TransformPoint(armatureToTarget, co);
            for (int k = 0; k < 3; k++) { if (co[k] < mn[k]) mn[k] = co[k]; if (co[k] > mx[k]) mx[k] = co[k]; }
        }
        var it = VehicleProbe.ToRowMajor(mo.World); var r = new List<float[]>(8);
        for (int c = 0; c < 8; c++)
        {
            float x = (c & 4) == 0 ? mn[0] : mx[0], y = (c & 2) == 0 ? mn[1] : mx[1], z = (c & 1) == 0 ? mn[2] : mx[2];
            r.Add(new[] { MulPointRow(it, 0, x, y, z), MulPointRow(it, 1, x, y, z), MulPointRow(it, 2, x, y, z) });
        }
        return r;
    }

    /// <summary>Python's round() of a float to an int: half to even.</summary>
    static double PyRound(double x) => Math.Round(x, MidpointRounding.ToEven);

    /// <summary>'%+.0f': the sign always shown.</summary>
    static string PySigned(string fixedText) => fixedText.StartsWith("-", StringComparison.Ordinal) ? fixedText : "+" + fixedText;

    /// <summary>_wheel_symmetry: the spoke count of the wheel bone's joined mesh (Mesh_<bone>, by name - bpy.data.objects; the model's
    /// objects are all of them) from the
    /// angular autocorrelation of its rim vertices about the bone's axle: every max(1, n // 1500)-th vertex in the plane across the
    /// axle (math.hypot, math.atan2), those beyond 0.45 of the largest radius; for n = 2..24 the mean resultant of cos/sin(n theta)
    /// (sum(): compensated); 0 below 24 rim vertices or a best score under 0.4, else the smallest n scoring 0.8 of the best.</summary>
    static int WheelSymmetry(BlenderRigAnim.Stage1 s, RigState st, string bname)
    {
        var o2 = s.Objects.FirstOrDefault(o => o.Name == "Mesh_" + bname);
        if (o2 == null || o2.Type != "MESH" || o2.Mesh == null || o2.Mesh.Vertices == 0) return 0;
        var ad = s.Armature; int b = st.BoneId[bname];
        var ml = ad.Rest.MatrixLocal[b];   // row-major item layout
        var am = VehicleProbe.ToRowMajor(st.Arm.World);
        var w = MulMat4Row(am, ml);        // arm.matrix_world @ bone.matrix_local
        var ctr = new[] { w[3], w[7], w[11] };
        var ax = Normalized(new[] { MulM3Row(w, 0, 0f, 1f, 0f), MulM3Row(w, 1, 0f, 1f, 0f), MulM3Row(w, 2, 0f, 1f, 0f) });
        var u = Cross(ax, new[] { 0f, 0f, 1f });
        if (VecLength(u) < 1e-3) u = Cross(ax, new[] { 1f, 0f, 0f });
        u = Normalized(u);
        var w2 = Cross(ax, u);
        var d = o2.Mesh; int n = d.Vertices, stp = Math.Max(1, n / 1500);
        var it = VehicleProbe.ToRowMajor(o2.World);
        double rmax = 0.0; var tmp = new List<(double t, double r)>();
        for (int i = 0; i < n; i += stp)
        {
            float px = d.Positions[3 * i], py = d.Positions[3 * i + 1], pz = d.Positions[3 * i + 2];
            var wp = new[] { MulPointRow(it, 0, px, py, pz), MulPointRow(it, 1, px, py, pz), MulPointRow(it, 2, px, py, pz) };
            var rel = Sub(wp, ctr);
            double xr = Dot(rel, u), yr = Dot(rel, w2), rr = PyHypot(xr, yr);
            if (rr > rmax) rmax = rr;
            tmp.Add((PyAtan2(yr, xr), rr));
        }
        var pts = tmp.Where(x => x.r > 0.45 * rmax).Select(x => x.t).ToList();
        if (pts.Count < 24) return 0;
        double bestR = 0.0; var scores = new List<(int n, double R)>();
        for (int k = 2; k < 25; k++)
        {
            double sr = PySum(pts.Select(t => BlenderTrig.Cos(k * t))), si = PySum(pts.Select(t => BlenderTrig.Sin(k * t)));
            double R = PyHypot(sr, si) / pts.Count;
            scores.Add((k, R));
            if (R > bestR) bestR = R;
        }
        if (bestR < 0.4) return 0;
        foreach (var (k, R) in scores) if (R >= 0.8 * bestR) return k;
        return 0;
    }

    /// <summary>mathutils Matrix(4x4) @ Matrix(4x4), row-major items: per entry the float32 products summed in double, rounded once.</summary>
    static float[] MulMat4Row(float[] A, float[] B)
    {
        var r = new float[16];
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 4; col++)
            {
                double dot = 0.0;
                for (int k = 0; k < 4; k++) dot += (double)(float)(A[row * 4 + k] * B[k * 4 + col]);
                r[row * 4 + col] = (float)dot;
            }
        return r;
    }

    /// <summary>One row of Matrix.to_3x3() @ Vector: the float32 products summed in double.</summary>
    static float MulM3Row(float[] m, int row, float x, float y, float z)
    {
        double d = 0.0; d += (double)(float)(m[row * 4] * x); d += (double)(float)(m[row * 4 + 1] * y); d += (double)(float)(m[row * 4 + 2] * z);
        return (float)d;
    }

    /// <summary>CPython's math.atan2 (m_atan2): NaN in, NaN out; the infinities and the zeros by C99 Annex F; else the C runtime's.</summary>
    static double PyAtan2(double y, double x)
    {
        if (double.IsNaN(x) || double.IsNaN(y)) return BitConverter.Int64BitsToDouble(0x7ff8000000000000L);
        if (double.IsInfinity(y))
        {
            if (double.IsInfinity(x)) return CopySign(x > 0 ? 0.25 * Math.PI : 0.75 * Math.PI, y);
            return CopySign(0.5 * Math.PI, y);
        }
        if (double.IsInfinity(x) || y == 0.0)
        {
            if (CopySign(1.0, x) == 1.0) return CopySign(0.0, y);
            return CopySign(Math.PI, y);
        }
        return BlenderTrig.Atan2(y, x);
    }

    static double CopySign(double v, double s) => (BitConverter.DoubleToInt64Bits(s) < 0) == (BitConverter.DoubleToInt64Bits(v) < 0) ? v : -v;
}
