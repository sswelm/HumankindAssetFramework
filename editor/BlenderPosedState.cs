// BlenderPosedState.cs - what Blender makes of an imported glTF animation at a frame (replacing deploy_convert.py,
// part 1): the importer's fcurves and Blender's evaluation of them, so that VehicleProbe.BlenderWorldMatrices can give
// every object's matrix_world as `scene.frame_set(f)` leaves it. Held to Blender by tools/deploy_drill.sh, bit for bit.
//
// The importer (io_scene_gltf2 blender/imp/animation_node.py, animation_utils.py make_fcurve):
//   - one fcurve per COMPONENT of a channel (location x, y, z; rotation_quaternion w, x, y, z; scale x, y, z);
//   - a key's frame is its time (a float32) times the scene's frames per second, in double, stored as a float32;
//   - rotations: each quaternion that points away from the one before it (a negative dot, in float32) is negated, so
//     the components run the short way - and are then interpolated COMPONENT BY COMPONENT, not on the sphere; the
//     object normalizes what comes out when it builds its matrix;
//   - fcurve.update() then sorts the keys by frame (the reader takes ascending times only) and MERGES those closer
//     than 0.01 frame (see Import);
//   - LINEAR keys are linear, STEP keys constant; of a CUBICSPLINE sampler only the VALUES are kept (the tangents are
//     dropped) and the keys become Bezier keys with automatic handles - NOT modelled here: Action.HasBezier says so,
//     and a caller must leave such a file to Blender;
//   - a second channel on the same node and path is refused by Blender (the first one stands).
// Blender (blenkernel/intern/fcurve.cc, v5.1.2): fcurve_eval_keyframes - at or before the first key and at or after the
// last, the end key's value (the importer's curves extrapolate constant); in between, a binary search with a threshold
// of 0.0001 frame for a key ON the frame, else `change * time / duration + begin` in float32 (BLI_easing_linear_ease).
// The curves hold BLENDER's components (location x, -z, y of glTF's; rotation w, x, -z, y; scale x, z, y), as the
// importer converts each key before it makes the curve. Evaluating in glTF's axes and converting afterwards gives the
// same VALUES and another ZERO: between two keys of -0 Blender's `change * time / duration + begin` is +0, and the
// negation of glTF's +0 is -0 (554 of 1,664 evaluated sets of the howitzer; review of the posed state, 2026-10-09).
// And the WRITE (anim_sys.cc BKE_animsys_write_to_rna_path): a value equal to the property's is not written, and -0
// equals +0 - so the sign of an evaluated zero is the sign of the zero that was there, back to the last value that
// was not zero or to the import. Action.Pose keeps that state; TrsAt alone gives the curves' values.
// Not modelled: an animated BONE moves the armature's pose, not an object - its channels are kept (they count for the
// action's frame range) but give no object transform; an object parented to a bone follows that pose (named by
// BlenderExportTree for the export as well).
using System;
using System.Collections.Generic;
using System.Linq;

public static class BlenderPosedState
{
    /// <summary>One fcurve: the keys in frame order.</summary>
    public sealed class Curve
    {
        public float[] Frames, Values;
        public bool Constant;     // STEP: BEZT_IPO_CONST on every key

        /// <summary>fcurve_eval_keyframes for a curve of linear or constant keys with constant extrapolation.</summary>
        public float Evaluate(float evaltime)
        {
            int n = Frames.Length;
            if (n == 0) return 0f;
            if (evaltime <= Frames[0]) return Values[0];
            if (Frames[n - 1] <= evaltime) return Values[n - 1];
            int a = BinarySearch(Frames, evaltime, 0.0001f, out bool exact);
            if (exact) return Values[a];
            int prev = a > 0 ? a - 1 : a;
            if (Math.Abs((float)(Frames[a] - evaltime)) < 1.0e-8f) return Values[a];
            if (evaltime < Frames[prev] || Frames[a] < evaltime) return 0f;
            float begin = Values[prev];
            float change = (float)(Values[a] - Values[prev]);
            float duration = (float)(Frames[a] - Frames[prev]);
            float time = (float)(evaltime - Frames[prev]);
            if (Constant || duration == 0f) return Values[prev];
            return (float)((float)((float)(change * time) / duration) + begin);
        }

        /// <summary>BKE_fcurve_bezt_binarysearch_index_ex: the index to insert at, or of the key within the threshold.</summary>
        static int BinarySearch(float[] frames, float frame, float threshold, out bool replace)
        {
            int start = 0, end = frames.Length;
            replace = false;
            float framenum = frames[0];
            if (IsEqt(frame, framenum, threshold)) { replace = true; return 0; }
            if (frame < framenum) return 0;
            framenum = frames[frames.Length - 1];
            if (IsEqt(frame, framenum, threshold)) { replace = true; return frames.Length - 1; }
            if (frame > framenum) return frames.Length;
            while (start <= end)
            {
                int mid = start + ((end - start) / 2);
                float midfra = frames[mid];
                if (IsEqt(frame, midfra, threshold)) { replace = true; return mid; }
                if (frame > midfra) start = mid + 1; else end = mid - 1;
            }
            return start;
        }

        static bool IsEqt(float a, float b, float c) => a > b ? (float)(a - b) <= c : (float)(b - a) <= c;
    }

    /// <summary>The action the importer makes of one glTF animation.</summary>
    public sealed class Action
    {
        // per glTF node: the curves of each path, in BLENDER's component order (location x y z, rotation_quaternion
        // w x y z, scale x y z) and convention
        public readonly Dictionary<int, Curve[]> Translation = new Dictionary<int, Curve[]>(), Rotation = new Dictionary<int, Curve[]>(), Scale = new Dictionary<int, Curve[]>();
        public float FrameStart, FrameEnd;     // Action.frame_range: the first and the last key over every curve
        public bool HasBezier;                 // a CUBICSPLINE sampler: Bezier keys with automatic handles, not modelled
        // why Blender's posed state of this file is NOT what TrsAt gives, or null: such a file is left to Blender by
        // whoever asks - a CUBICSPLINE sampler; KHR_animation_pointer (the importer takes pointers at a node's
        // translation, rotation and scale for channels, and other pointers' curves into the same action: neither is read)
        public string NotModelled;
        // false with KHR_animation_pointer: an unread pointer channel may extend the range and touch other objects, so
        // FrameStart, FrameEnd and Animates are not Blender's either - with a CUBICSPLINE sampler alone they are
        public bool RangeAndTouchedKnown = true;
        public readonly List<string> Notes = new List<string>();   // what this action exercised (the drill's coverage)
        public bool Animates(int node) => Translation.ContainsKey(node) || Rotation.ContainsKey(node) || Scale.ContainsKey(node);

        /// <summary>The animated values of every node at a frame, as VehicleProbe.BlenderWorldMatrices takes them:
        /// Blender's {location, rotation_quaternion (w, x, y, z), scale} after frame_set, null where the node has no
        /// such channel.</summary>
        public Func<int, float[][]> TrsAt(float frame)
        {
            return node =>
            {
                bool t = Translation.TryGetValue(node, out var tc), r = Rotation.TryGetValue(node, out var rc), s = Scale.TryGetValue(node, out var sc);
                if (!t && !r && !s) return null;
                return new[] { t ? tc.Select(c => c.Evaluate(frame)).ToArray() : null, r ? rc.Select(c => c.Evaluate(frame)).ToArray() : null, s ? sc.Select(c => c.Evaluate(frame)).ToArray() : null };
            };
        }
    }

    /// <summary>The animated properties of a file's objects as `scene.frame_set` leaves them, one frame after another:
    /// each evaluated value written over the one held, unless they are equal (a zero keeps the sign it had). Starts at
    /// what the importer set: the node's own transform through the same corrections as a key.</summary>
    public sealed class Pose
    {
        readonly Action action; readonly HafModel model;
        readonly Dictionary<int, float[][]> held = new Dictionary<int, float[][]>();
        public Pose(Action a, HafModel m) { action = a; model = m; }

        /// <summary>frame_set: what VehicleProbe.BlenderWorldMatrices takes for this frame. The frames must be given
        /// in the order Blender is given them.</summary>
        public Func<int, float[][]> FrameSet(float frame)
        {
            var curves = action.TrsAt(frame);
            var now = new Dictionary<int, float[][]>();
            return node =>
            {
                if (now.TryGetValue(node, out var done)) return done;
                var p = curves(node);
                if (p == null) return now[node] = null;
                if (!held.TryGetValue(node, out var h))
                {
                    VehicleProbe.BlenderTrs(model.Nodes[node], out var l, out var q, out var s);
                    held[node] = h = new[] { MulQtV3(IdentityQuat, l), MulQtQt(MulQtQt(IdentityQuat, q), IdentityQuat), new[] { IdentityRow(s[0], s[1], s[2], 0), IdentityRow(s[0], s[1], s[2], 1), IdentityRow(s[0], s[1], s[2], 2) } };
                }
                for (int k = 0; k < 3; k++)
                {
                    if (p[k] == null) continue;
                    for (int c = 0; c < p[k].Length; c++) { if (h[k][c] == p[k][c]) p[k][c] = h[k][c]; else h[k][c] = p[k][c]; }
                }
                return now[node] = p;
            };
        }
    }

    /// <summary>The importer's action for a glTF animation at the scene's frame rate (24 after read_factory_settings).</summary>
    public static Action Import(HafModel m, int animation, double fps = 24.0)
    {
        var a = new Action();
        if (animation < 0 || animation >= m.Animations.Count) return a;
        var anim = m.Animations[animation];
        bool any = false; float lo = 0f, hi = 0f;
        var taken = new HashSet<(int, string)>();
        foreach (var ch in anim.Channels)
        {
            if (ch.Node < 0 || ch.Node >= m.Nodes.Count || ch.Sampler < 0 || ch.Sampler >= anim.Samplers.Count) continue;
            var s = anim.Samplers[ch.Sampler];
            if (ch.Path == "weights")
            {
                // morph weights: curves on the mesh's shape keys, in the SAME action - they move no object, but the action's
                // frame range runs over them too (deploy_convert.py takes its range from there; review of the posed state)
                if (s.Times == null || s.Times.Length == 0 || !taken.Add((ch.Node, ch.Path))) continue;
                var wf = MergedFrames(s.Times, fps, out _, null);
                if (!any) { lo = wf[0]; hi = wf[wf.Length - 1]; any = true; } else { lo = Math.Min(lo, wf[0]); hi = Math.Max(hi, wf[wf.Length - 1]); }
                Note(a, "morph weights in the action (they count for its frame range)");
                continue;
            }
            int comps = ch.Path == "translation" || ch.Path == "scale" ? 3 : ch.Path == "rotation" ? 4 : 0;
            if (comps == 0) continue;
            // Blender makes the curve before it has keys: a second channel on the path is refused even after an empty first
            if (!taken.Add((ch.Node, ch.Path))) { Note(a, "a second channel on one node and path (the first stands)"); continue; }
            if (s.Times == null || s.Times.Length == 0 || s.Components != comps) continue;
            int keys = s.Times.Length;
            bool cubic = s.Interpolation == "CUBICSPLINE";
            if (cubic) { a.HasBezier = true; a.NotModelled = a.NotModelled ?? "a CUBICSPLINE sampler - Blender makes Bezier keys with automatic handles of it"; Note(a, "a CUBICSPLINE sampler (Bezier keys with automatic handles: not modelled)"); }
            // the values, key by key: of a CUBICSPLINE sampler the middle of each (in-tangent, value, out-tangent), as the
            // importer's values[1::3] - NOT held by the drill: such a file is left to Blender, only its keys' frames count
            var v = new float[keys][];
            for (int k = 0; k < keys; k++)
            {
                v[k] = new float[comps];
                for (int c = 0; c < comps; c++) v[k][c] = s.Values[(cubic ? (3 * k + 1) * comps : k * comps) + c];
            }
            // glTF's key in Blender's convention (io_scene_gltf2 conversion.py), then the vnode's corrections
            // (base_locs_to_final_locs: `rotation_after @ loc`; base_rots_to_final_rots: `rotation_after @ rot @
            // rotation_before`; base_scales_to_final_scales: a matrix @ scale) - identities for anything but a camera or a
            // light, which change no value and DO settle the sign of a zero: the -0 a negated 0 leaves comes out +0
            for (int k = 0; k < keys; k++)
            {
                var g = v[k];
                v[k] = ch.Path == "translation" ? MulQtV3(IdentityQuat, new[] { g[0], -g[2], g[1] })
                     : ch.Path == "rotation" ? MulQtQt(MulQtQt(IdentityQuat, new[] { g[3], g[0], -g[2], g[1] }), IdentityQuat)
                     : new[] { IdentityRow(g[0], g[2], g[1], 0), IdentityRow(g[0], g[2], g[1], 1), IdentityRow(g[0], g[2], g[1], 2) };
            }
            if (ch.Path == "rotation")
            {
                // "to ensure rotations always take the shortest path, we flip adjacent antipodal quaternions": mathutils'
                // dot (dot_qtqt, float32) over (w, x, y, z), AFTER the corrections - so a flipped key's zeros are -0
                for (int k = 1; k < keys; k++)
                {
                    float[] p = v[k - 1], q = v[k];
                    float dot = (float)((float)((float)((float)(q[0] * p[0]) + (float)(q[1] * p[1])) + (float)(q[2] * p[2])) + (float)(q[3] * p[3]));
                    if (dot < 0f) { for (int c = 0; c < 4; c++) q[c] = -q[c]; Note(a, "a quaternion negated to run the short way"); }
                }
            }
            var keptFrames = MergedFrames(s.Times, fps, out var keptKey, a);
            keys = keptFrames.Length;
            if (keys == 1) Note(a, "a curve of one key");
            var curves = new Curve[comps];
            for (int c = 0; c < comps; c++)
                curves[c] = new Curve { Frames = keptFrames, Values = keptKey.Select(k => v[k][c]).ToArray(), Constant = s.Interpolation == "STEP" };
            (ch.Path == "translation" ? a.Translation : ch.Path == "rotation" ? a.Rotation : a.Scale)[ch.Node] = curves;
            Note(a, ch.Path == "translation" ? "an animated translation" : ch.Path == "rotation" ? "an animated rotation" : "an animated scale");
            if (s.Interpolation == "STEP") Note(a, "a STEP sampler (constant keys)");
            float first = curves[0].Frames[0], last = curves[0].Frames[keys - 1];
            if (!any) { lo = first; hi = last; any = true; } else { lo = Math.Min(lo, first); hi = Math.Max(hi, last); }
        }
        a.FrameStart = lo; a.FrameEnd = hi;
        // the pointer's reason stands over a cubic sampler's: it takes the range and the touched objects with it
        if (m.ExtensionsUsed.Contains("KHR_animation_pointer")) { a.NotModelled = "KHR_animation_pointer - the importer animates through pointers this does not read"; a.RangeAndTouchedKnown = false; }
        return a;
    }

    /// <summary>The frames of a sampler's keys as the importer's curve ends up holding them, and which key each kept frame
    /// takes its value from. `key[0] * fps` in Python (double), stored in a BezTriple (float32). fcurve.update() then
    /// sorts the keys by frame - nothing to do, GlbReader refuses a sampler whose times are not ascending (such a file is
    /// Blender's) - and MERGES keys within 0.01 frame of the one kept before them (BKE_fcurve_deduplicate_keys: "the last
    /// one wins"): the later key replaces the earlier and takes its frame - unless it sits exactly on a frame, which it
    /// keeps. A 30-per-second source with times written both exact and rounded to the millisecond has hundreds of such
    /// pairs per curve (the T-62: 332 of 832 keys).</summary>
    static float[] MergedFrames(float[] times, double fps, out List<int> keptKey, Action notes)
    {
        var keptFrame = new List<float>(); keptKey = new List<int>();
        for (int i = 0; i < times.Length; i++)
        {
            float x = (float)((double)times[i] * fps);
            if (i > 0 && (float)(x - keptFrame[keptFrame.Count - 1]) <= 0.01f)
            {
                keptKey[keptKey.Count - 1] = i;
                if (Math.Floor((double)x) == x) { keptFrame[keptFrame.Count - 1] = x; if (notes != null) Note(notes, "a merged key on a whole frame (it keeps its own frame)"); }
                if (notes != null) Note(notes, "keys closer than 0.01 frame merged (the last wins)");
                continue;
            }
            keptFrame.Add(x); keptKey.Add(i);
        }
        return keptFrame.ToArray();
    }

    static readonly float[] IdentityQuat = { 1f, 0f, 0f, 0f };

    /// <summary>mul_qt_v3 (mathutils' Quaternion @ Vector), float32 term by term.</summary>
    static float[] MulQtV3(float[] q, float[] v)
    {
        float[] r = { v[0], v[1], v[2] };
        float t0 = (float)((float)((float)((float)(-q[1] * r[0]) - (float)(q[2] * r[1])) - (float)(q[3] * r[2])));
        float t1 = (float)((float)((float)(q[0] * r[0]) + (float)(q[2] * r[2])) - (float)(q[3] * r[1]));
        float t2 = (float)((float)((float)(q[0] * r[1]) + (float)(q[3] * r[0])) - (float)(q[1] * r[2]));
        r[2] = (float)((float)((float)(q[0] * r[2]) + (float)(q[1] * r[1])) - (float)(q[2] * r[0]));
        r[0] = t1; r[1] = t2;
        t1 = (float)((float)((float)((float)(t0 * -q[1]) + (float)(r[0] * q[0])) - (float)(r[1] * q[3])) + (float)(r[2] * q[2]));
        t2 = (float)((float)((float)((float)(t0 * -q[2]) + (float)(r[1] * q[0])) - (float)(r[2] * q[1])) + (float)(r[0] * q[3]));
        r[2] = (float)((float)((float)((float)(t0 * -q[3]) + (float)(r[2] * q[0])) - (float)(r[0] * q[2])) + (float)(r[1] * q[1]));
        r[0] = t1; r[1] = t2;
        return r;
    }

    /// <summary>mul_qt_qtqt (mathutils' Quaternion @ Quaternion), float32 term by term.</summary>
    static float[] MulQtQt(float[] a, float[] b)
    {
        return new[]
        {
            (float)((float)((float)((float)(a[0] * b[0]) - (float)(a[1] * b[1])) - (float)(a[2] * b[2])) - (float)(a[3] * b[3])),
            (float)((float)((float)((float)(a[0] * b[1]) + (float)(a[1] * b[0])) + (float)(a[2] * b[3])) - (float)(a[3] * b[2])),
            (float)((float)((float)((float)(a[0] * b[2]) + (float)(a[2] * b[0])) + (float)(a[3] * b[1])) - (float)(a[1] * b[3])),
            (float)((float)((float)((float)(a[0] * b[3]) + (float)(a[3] * b[0])) + (float)(a[1] * b[2])) - (float)(a[2] * b[1])),
        };
    }

    /// <summary>One row of mathutils' Matrix @ Vector with the identity (column_vector_multiplication): float32 products
    /// summed in a double that starts at +0.</summary>
    static float IdentityRow(float x, float y, float z, int row)
    {
        double dot = 0.0;
        dot += (double)(float)((row == 0 ? 1f : 0f) * x);
        dot += (double)(float)((row == 1 ? 1f : 0f) * y);
        dot += (double)(float)((row == 2 ? 1f : 0f) * z);
        return (float)dot;
    }

    static void Note(Action a, string what) { if (!a.Notes.Contains(what)) a.Notes.Add(what); }
}
