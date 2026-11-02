// FbxActions.cs - sub-step 6d of the native FBX reader: ANIMATION as Blender 5.1's FBX importer turns it into actions and
// as its glTF exporter samples them, formula for formula; the Blender-side state of the scene at any frame (the pose
// evaluation 6c left by name), which the exporter reads at frame 0 for every node's transform.
// THE IMPORTER (io_scene_fbx/import_fbx.py, Blender 5.1.2), as modelled:
//   * the frame rate (load): TimeMode -> FBX_FRAMERATES (a custom or unknown mode: CustomFrameRate, 25 when missing or
//     not positive); scene.render.fps = round(real_fps), fps_base = fps / real_fps (a float32 property);
//   * ktime: 141120000 per second from version 8000, 46186158000 before; a header of version 1004 or later with
//     OtherFlags/TCDefinition: 0 -> the new, 127 -> the old, anything else -> the new;
//   * the stacks (AnimationStack, empty subtype, table order), the layers (AnimationLayer) through their OO links to
//     stacks, the curve nodes (AnimationCurveNode) with their OP links to a Model's Lcl Translation/Rotation/Scaling
//     (a link to the root, to no helper, or to another property is ignored: DeformPercent needs shape keys, the cameras'
//     FocalLength and FocusDistance a camera - both left to Blender before this -, DiffuseColor makes a material action
//     the exporter never writes), then their OO links to layers; the curves (AnimationCurve) through their OP links to a
//     curve node's d|X, d|Y, d|Z (or d|DeformPercent, d|FocalLength, d|FocusDistance: channel 0) - the first curve per
//     channel of a property is the one used;
//   * blen_read_animations: per stack (its name's class must be AnimStack, the layer's AnimLayer: an assertion), per
//     layer, per animated item (a Model's helper: an object, or a bone - its id_data is the armature object) one action
//     named "<object>|<stack>" or "<object>|<stack>|<layer>" when they differ (bpy.data.actions.new: unique names, 255
//     bytes), assigned to the id_data when it has none yet; a mesh object whose parent is an armature is skipped;
//   * blen_read_animations_action_item: fcurves for the whole location, rotation (rotation_euler XYZ for an object,
//     rotation_quaternion for a pose bone) and scale; each transformation curve read (KeyTime int64, KeyValueFloat
//     float32, strictly increasing times, else blen_read_invalid_animation_curve: unique sorted times, the value
//     interpolated between the key at or after each time and the one before it, extrapolated times dropped); the
//     curves' times combined (np.unique of them all), a curve with fewer keys interpolated by np.interp in float64 with
//     the property's static value before its first key; then per combined time the local matrix as
//     _blen_read_object_transform_do_anim builds it from mathutils matrices (float32 products, the rotation an Euler of
//     the file's order to a matrix, pre/post rotations, pivots and offsets precomputed, the geometric transform and the
//     corrections last; extra_pre_matrix - a bone's bind.inverted_safe() [@ pre_matrix], an object's pre_matrix - only
//     multiplied when it is not the identity within one float step), decomposed (mat4_to_loc_rot_size,
//     mat3_normalized_to_quat_fast); a bone's quaternion flipped when its dot with the previous frame's is negative, an
//     object's turned into an XYZ Euler compatible with the previous frame's (quat_to_compatible_eulO); the values as
//     float32, the times (t * fps / ktime + 1.0 in float64) as float32, every key LINEAR.
// THE EXPORTER (io_scene_gltf2/blender/exp, ACTIONS mode, force sampling, optimize, keep bone channels), as modelled:
//   * export.py: the scene set to frame 0 before anything is gathered: every node's transform is the animated state
//     there (an object's properties with curves take the curves' values, a pose bone's its curves' or the import's
//     pose), the armature's pose evaluated (BKE_pose_where_is) for the objects hanging from bones;
//   * action.py __get_blender_actions: an object's active action (the first the importer made for it); a lone armature
//     with an action also gets every other action in bpy.data.actions order (names compared as Blender sorts IDs) whose
//     curves reach pose bones, the actions sorted by their lower-cased names (ActionsData.get); prepare_actions_range:
//     int() of the action's frame range (the first and last key over its curves, a single frame stretched by one and
//     found again from the curves); gather_action_animations: the actions in that order, an action that is not the
//     armature's current one switched to after reset_bone_matrix (every pose bone's basis the identity; the active
//     action escapes it only when it sorts first); the restore afterwards resets the bones again when a switch
//     happened; cache.py datacache: the first (object, action) sampled fills the cache for every object over the global
//     range in the scene's state of that moment, an object's active action is a hit afterwards (its samples are that
//     first fill's), another action of an object is recomputed for that object alone - so a switch's reset, or a
//     property an earlier action left behind, reaches only what is recomputed after it;
//   * sampling_cache.py: per frame scene.frame_set(frame) then every object's matrix against its parent's matrix_world
//     (a bone parent: armature.matrix_world @ pose_bone.matrix @ the Y-up basis change) and every bone's
//     rest_mat.inverted_safe() @ parent.matrix.inverted_safe() @ pose_bone.matrix (a root bone: matrix_local.inverted_safe()
//     @ matrix), decomposed; keyframes.py: a constant channel (np.ptp below 0.0001 per component) of an animated
//     property keeps its first and last sample, a bone's always, an object's unanimated constant channel is dropped;
//     sampler.py: the interpolation (LINEAR for the importer's curves; STEP for two equal samples or an unanimated
//     channel), the values through gltf2_blender_math.transform (a bone's with its correction matrix, an object's with
//     the identity then swizzle_yup), the times frame / (fps * fps_base) as float32;
//   * channels: a bone's location, rotation, scale in the vtree's bone order, then the armature object's own animated
//     properties; an object's location, rotation, scale; one sampler per channel; an action without channels makes no
//     animation; the animations in the vtree's object order; merge_tracks_perform changes nothing here (one action, one
//     slot, unique names).
// Left to Blender by name: a curve whose KeyValueFloat is not float32 or KeyTime not int64, a curve without keys (np.interp
// refuses it beside another curve; alone it makes an action of empty curves), a curve node without curves on an animated
// item (the same empty action), two keys on one float32 frame, two actions of one name, an action name over 255 bytes.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public static class FbxActions
{
    const long KtimeV7 = 46186158000, KtimeV8 = 141120000;
    static readonly (double fps, long mode)[] FrameRates = { (120.0, 1), (100.0, 2), (60.0, 3), (50.0, 4), (48.0, 5), (30.0, 6), (30.0, 7), (30.0 / 1.001, 9), (30.0 / 1.001, 8), (25.0, 10), (24.0, 11), (24.0 / 1.001, 13), (96.0, 15), (72.0, 16), (60.0 / 1.001, 17), (120.0 / 1.001, 18) };
    static readonly float[] AxisBasisChange = { 1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1 };   // row-major items: (x, y, z) -> (x, z, -y)

    /// <summary>The fcurves the importer made for one object or pose bone: location (3), rotation (an object's Euler, 3;
    /// a bone's quaternion, 4) and scale (3), every one keyed at the same frames.</summary>
    public sealed class ItemCurves
    {
        public Curve[] Loc = new Curve[3], Rot, Sca = new Curve[3];
        public bool IsBone;
    }

    /// <summary>One action (bpy.data.actions) with the slot the importer gave it.</summary>
    public sealed class Action
    {
        public string Name = "";
        public FbxImport.Obj IdData;                  // the object the action was made for (an armature object for bone items)
        public ItemCurves Object;                     // the object's own curves, or null
        public readonly List<(FbxArmature.Bone bone, ItemCurves curves)> Bones = new List<(FbxArmature.Bone, ItemCurves)>();   // insertion order
        public float FrameStart, FrameEnd;            // BKE_action_frame_range_calc
        public bool HasBoneCurves => Bones.Count > 0;
    }

    /// <summary>The scene as scene.frame_set leaves it: every object's matrix_world and every bone's pose matrix (armature
    /// space), column-major; the properties they were built from.</summary>
    public sealed class State
    {
        public readonly Dictionary<FbxImport.Obj, float[]> World = new Dictionary<FbxImport.Obj, float[]>();
        public readonly Dictionary<FbxArmature.Bone, float[]> Pose = new Dictionary<FbxArmature.Bone, float[]>();
        public readonly Dictionary<FbxImport.Obj, float[][]> ObjectProps = new Dictionary<FbxImport.Obj, float[][]>();   // loc, eul, size
        public readonly Dictionary<FbxArmature.Bone, float[][]> BoneProps = new Dictionary<FbxArmature.Bone, float[][]>();   // loc, quat (w x y z), size
    }

    sealed class DeclineException : Exception { public DeclineException(string why) : base(why) { } }

    // ---- the import ------------------------------------------------------------------------------------------------------

    /// <summary>The frame rate and ktime of the file, then every action: the scene's Actions and each object's ActiveAction.</summary>
    internal static void Read(FbxImport.Scene r, FbxScene s, IList<FbxObject> table, Dictionary<long, FbxObject> byId, Dictionary<long, FbxImport.Helper> helpers, Action<string> decline)
    {
        try { ReadInner(r, s, table, byId, helpers); }
        catch (DeclineException e) { decline(e.Message); }
    }

    static void ReadInner(FbxImport.Scene r, FbxScene s, IList<FbxObject> table, Dictionary<long, FbxObject> byId, Dictionary<long, FbxImport.Helper> helpers)
    {
        // ---- the frame rate
        double customFps = FbxImport.PropNumber(s.GlobalSettings, "CustomFrameRate", 25.0);
        long timeMode = FbxImport.PropEnum(s.GlobalSettings, "TimeMode", -1);
        double realFps = customFps;
        foreach (var (fps, mode) in FrameRates) if (mode == timeMode) { realFps = fps; break; }
        if (realFps <= 0.0) realFps = 25.0;
        // Python's round must produce an RNA int before RenderSettings clamps fps to 1..32767.
        // fps_base is stored as float32, then clamped to its RNA range as well.
        double roundedFps = Math.Round(realFps, MidpointRounding.ToEven);
        if (double.IsNaN(roundedFps) || double.IsInfinity(roundedFps) || roundedFps > int.MaxValue)
            throw new DeclineException("a frame rate outside Blender's integer range (CustomFrameRate: " + realFps + ")");
        r.Fps = Math.Max(1, Math.Min(32767, (int)roundedFps));
        r.FpsBase = Math.Max(0.00001f, Math.Min(1000000f, (float)(r.Fps / realFps)));
        // ---- ktime
        long ktime = s.Version >= 8000 ? KtimeV8 : KtimeV7;
        var header = s.Document.First("FBXHeaderExtension");
        if (header != null)
        {
            var hv = header.First("FBXHeaderVersion");
            long headerVersion = hv != null && hv.Properties.Count > 0 && FbxProps.IsNumber(hv.Properties[0]) ? FbxProps.AsInt(hv.Properties[0]) : 0;
            if (headerVersion >= 1004)
            {
                var tc = header.First("OtherFlags")?.First("TCDefinition");
                if (tc != null)
                {
                    object v = tc.Properties.Count > 0 ? tc.Properties[0] : null;
                    long code = v != null && FbxProps.IsNumber(v) ? FbxProps.AsInt(v) : -1;
                    ktime = code == 0 ? KtimeV8 : code == 127 ? KtimeV7 : KtimeV8;
                }
            }
        }
        r.Ktime = ktime;
        // ---- stacks, layers, curve nodes, curves (the load function's animation block)
        var stacks = new List<FbxObject>(); var stackIds = new HashSet<long>();
        foreach (var o in table) if (o.Class == "AnimationStack" && o.SubType == "") { stacks.Add(o); stackIds.Add(o.Id); }
        if (stacks.Count == 0) return;
        var forward = new Dictionary<long, List<FbxConnection>>();
        foreach (var c in s.Connections) { if (!forward.TryGetValue(c.SrcId, out var l)) forward[c.SrcId] = l = new List<FbxConnection>(); l.Add(c); }
        List<FbxConnection> Out(long id) => forward.TryGetValue(id, out var l) ? l : new List<FbxConnection>();
        FbxObject Obj(long id) => byId.TryGetValue(id, out var o) ? o : null;
        // per stack: its layers (insertion order) each with its items (insertion order): item -> curve node -> property
        var layersOf = new Dictionary<long, List<long>>(); var itemsOf = new Dictionary<(long stack, long layer), OrderedMap<FbxImport.Helper, OrderedMap<long, string>>>();
        foreach (var st in stacks) layersOf[st.Id] = new List<long>();
        IEnumerable<long> StacksOfLayer(long layerId)
        {
            foreach (var c in Out(layerId))
            {
                if (c.Kind != "OO") continue;
                var st = Obj(c.DstId);
                if (st == null || st.Class != "AnimationStack" || st.SubType != "" || !stackIds.Contains(st.Id)) continue;
                yield return st.Id;
            }
        }
        var layerIds = new HashSet<long>();
        foreach (var o in table)
        {
            if (o.Class != "AnimationLayer" || o.SubType != "") continue;
            layerIds.Add(o.Id);
            foreach (long st in StacksOfLayer(o.Id)) { if (!itemsOf.ContainsKey((st, o.Id))) { layersOf[st].Add(o.Id); itemsOf[(st, o.Id)] = new OrderedMap<FbxImport.Helper, OrderedMap<long, string>>(); } }
        }
        // curve nodes: id -> (curve id -> (curve, channel)), and their items
        var curveNodes = new Dictionary<long, OrderedMap<long, (FbxObject curve, int channel)>>();
        foreach (var o in table)
        {
            if (o.Class != "AnimationCurveNode" || o.SubType != "") continue;
            var cnode = new OrderedMap<long, (FbxObject, int)>(); curveNodes[o.Id] = cnode;
            var items = new List<(FbxImport.Helper helper, string prop)>();
            foreach (var c in Out(o.Id))
            {
                if (c.Kind != "OP") continue;
                string prop = c.DstProp;
                if (prop == "Lcl Translation" || prop == "Lcl Rotation" || prop == "Lcl Scaling")
                {
                    if (!helpers.TryGetValue(c.DstId, out var h) || h.IsRoot) continue;
                    items.Add((h, prop));
                }
                // DeformPercent (shape keys), FocalLength / FocusDistance (cameras) are left to Blender before this; DiffuseColor
                // makes a material action the exporter never writes
            }
            foreach (var c in Out(o.Id))
            {
                if (c.Kind != "OO") continue;
                var al = Obj(c.DstId);
                if (al == null || al.Class != "AnimationLayer" || al.SubType != "" || !layerIds.Contains(al.Id)) continue;
                foreach (long st in StacksOfLayer(al.Id))
                {
                    var animItems = itemsOf[(st, al.Id)];
                    foreach (var (h, prop) in items)
                    {
                        if (!animItems.TryGet(h, out var nodes)) { nodes = new OrderedMap<long, string>(); animItems.Set(h, nodes); }
                        nodes.Set(o.Id, prop);
                    }
                }
            }
        }
        foreach (var o in table)
        {
            if (o.Class != "AnimationCurve" || o.SubType != "") continue;
            foreach (var c in Out(o.Id))
            {
                if (c.Kind != "OP") continue;
                var acn = Obj(c.DstId);
                if (acn == null || acn.Class != "AnimationCurveNode" || acn.SubType != "" || !curveNodes.TryGetValue(acn.Id, out var cnode)) continue;
                int channel;
                switch (c.DstProp)
                {
                    case "d|X": channel = 0; break;
                    case "d|Y": channel = 1; break;
                    case "d|Z": channel = 2; break;
                    case "d|DeformPercent": case "d|FocalLength": case "d|FocusDistance": channel = 0; break;
                    default: continue;
                }
                cnode.Set(o.Id, (o, channel));
            }
        }
        // ---- blen_read_animations
        double timefac = (double)r.Fps / ktime;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var actions = new Dictionary<(long stack, long layer, FbxImport.Obj id), Action>();
        foreach (var st in stacks)
        {
            string stackName = NameEnsureClass(st, "AnimStack");
            foreach (long layerId in layersOf[st.Id])
            {
                var al = byId[layerId];
                string layerName = NameEnsureClass(al, "AnimLayer");
                foreach (var (item, cnodes) in itemsOf[(st.Id, layerId)].Items)
                {
                    FbxImport.Obj idData = item.IsBone ? item.ArmatureHelper?.Obj : item.Obj;
                    if (idData == null) continue;
                    // XXX Ignore rigged mesh animations (the importer's note): a mesh object under an armature object
                    if (idData.Kind == FbxImport.ObjectKind.Mesh && idData.Parent != null && idData.Parent.Kind == FbxImport.ObjectKind.Armature) continue;
                    var key = (st.Id, layerId, idData);
                    if (!actions.TryGetValue(key, out var action))
                    {
                        string name = stackName == layerName ? idData.Name + "|" + stackName : idData.Name + "|" + stackName + "|" + layerName;
                        if (Encoding.UTF8.GetByteCount(name) > 255) throw new DeclineException("an action name over 255 bytes ('" + name.Substring(0, 40) + "...': Blender's ID naming would cut it)");
                        if (!names.Add(name)) throw new DeclineException("two actions of one name '" + name + "' (two layers of one name in a stack: Blender numbers the second)");
                        action = new Action { Name = name, IdData = idData };
                        actions[key] = action; r.Actions.Add(action);
                        if (idData.ActiveAction == null) idData.ActiveAction = action;
                    }
                    ReadActionItem(r, action, item, cnodes, curveNodes, timefac);
                }
            }
        }
        foreach (var a in r.Actions) ActionFrameRange(a);
    }

    /// <summary>elem_name_ensure_class: the record's name with its class asserted.</summary>
    static string NameEnsureClass(FbxObject o, string cls)
    {
        string typeAndName = o.Record != null && o.Record.Properties.Count > 1 ? o.Record.Properties[1] as string ?? "" : "";
        int sep = typeAndName.IndexOf("\0\u0001", StringComparison.Ordinal);
        string name = sep >= 0 ? typeAndName.Substring(0, sep) : typeAndName, found = sep >= 0 ? typeAndName.Substring(sep + 2) : "";
        if (found != cls) throw new DeclineException("a " + o.Class + " named '" + name + "' with the class '" + found + "' (Blender's importer asserts " + cls + ")");
        return name;
    }

    /// <summary>blen_read_animations_action_item for an object or pose bone.</summary>
    static void ReadActionItem(FbxImport.Scene r, Action action, FbxImport.Helper item, OrderedMap<long, string> cnodes, Dictionary<long, OrderedMap<long, (FbxObject curve, int channel)>> curveNodes, double timefac)
    {
        // fbx_curves: property -> channel -> the first curve found for it
        var fbxCurves = new OrderedMap<string, OrderedMap<int, FbxObject>>();
        foreach (var (acn, prop) in cnodes.Items)
        {
            if (!fbxCurves.TryGet(prop, out var channels)) { channels = new OrderedMap<int, FbxObject>(); fbxCurves.Set(prop, channels); }
            foreach (var (_, cc) in curveNodes[acn].Items) if (!channels.TryGet(cc.channel, out _)) channels.Set(cc.channel, cc.curve);
        }
        if (fbxCurves.Count == 0) return;
        bool isBone = item.IsBone;
        var t = item.Transform;
        var curves = new ItemCurves { IsBone = isBone, Rot = new Curve[isBone ? 4 : 3] };
        // the transformation curves, each read, with the property's static value for the frames before its first key
        var timesAndValues = new List<(double[] times, float[] values)>(); var initial = new List<double>(); var channelKeys = new List<(string prop, int channel)>();
        foreach (var (prop, channels) in fbxCurves.Items)
        {
            double[] attr = prop == "Lcl Translation" ? t.Loc : prop == "Lcl Rotation" ? t.Rot : prop == "Lcl Scaling" ? t.Sca : null;
            if (attr == null) continue;
            foreach (var (channel, curve) in channels.Items)
            {
                if (channel < 0 || channel > 2) throw new DeclineException("a transformation curve on channel " + channel + " (Blender's importer asserts)");
                ReadCurve(curve, out var times, out var values);
                channelKeys.Add((prop, channel)); initial.Add(attr[channel]); timesAndValues.Add((times, values));
            }
        }
        if (timesAndValues.Count == 0) throw new DeclineException("a curve node without curves on '" + item.Name + "' (an action of empty curves: the exporter's range of it is not modelled)");
        // _combine_curve_keyframe_times
        double[] allTimes; var valueArrays = new List<double[]>(); var stayFloat = new List<bool>();
        if (timesAndValues.Count == 1)
        {
            allTimes = timesAndValues[0].times; valueArrays.Add(timesAndValues[0].values.Select(v => (double)v).ToArray()); stayFloat.Add(true);
        }
        else
        {
            allTimes = timesAndValues.SelectMany(tv => tv.times).Distinct().OrderBy(x => x).ToArray();
            for (int i = 0; i < timesAndValues.Count; i++)
            {
                var (times, values) = timesAndValues[i];
                if (allTimes.Length == times.Length) { valueArrays.Add(values.Select(v => (double)v).ToArray()); stayFloat.Add(true); }
                else
                {
                    if (times.Length == 0) throw new DeclineException("a curve without keys beside another curve on '" + item.Name + "' (np.interp refuses it: Blender's import fails)");
                    valueArrays.Add(Interp(allTimes, times, values.Select(v => (double)v).ToArray(), initial[i])); stayFloat.Add(false);
                }
            }
        }
        if (allTimes.Length == 0) throw new DeclineException("a curve without keys on '" + item.Name + "' (an action of empty curves: the exporter's range of it is not modelled)");
        // _transformation_curves_gen
        int nf = allTimes.Length;
        int rotCount = isBone ? 4 : 3;
        var flat = new float[nf * (3 + rotCount + 3)];
        TransformationCurves(item, valueArrays, stayFloat, channelKeys, r, flat, rotCount);
        // blen_store_keyframes_multi: the times as float32, strictly increasing still
        var frames = new float[nf];
        for (int i = 0; i < nf; i++)
        {
            double f = (allTimes[i] - 0.0) * timefac; f += 1.0;
            frames[i] = (float)f;
            if (i > 0 && !(frames[i] > frames[i - 1])) throw new DeclineException("two keys on one frame after the float32 rounding (" + frames[i].ToString("R") + " on '" + item.Name + "': the fcurve's order is not modelled)");
        }
        int stride = 3 + rotCount + 3;
        Curve Make(int c) { var cv = new Curve { Frames = frames, Values = new float[nf] }; for (int i = 0; i < nf; i++) cv.Values[i] = flat[i * stride + c]; return cv; }
        for (int c = 0; c < 3; c++) curves.Loc[c] = Make(c);
        for (int c = 0; c < rotCount; c++) curves.Rot[c] = Make(3 + c);
        for (int c = 0; c < 3; c++) curves.Sca[c] = Make(3 + rotCount + c);
        if (isBone) action.Bones.Add((item.Bone, curves)); else action.Object = curves;
    }

    /// <summary>blen_read_animation_curve: KeyTime (int64) and KeyValueFloat (float32), strictly increasing times, else
    /// blen_read_invalid_animation_curve.</summary>
    static void ReadCurve(FbxObject curve, out double[] times, out float[] values)
    {
        var tn = curve.Record?.First("KeyTime"); var vn = curve.Record?.First("KeyValueFloat");
        object tv = tn != null && tn.Properties.Count > 0 ? tn.Properties[0] : null, vv = vn != null && vn.Properties.Count > 0 ? vn.Properties[0] : null;
        if (tv == null || vv == null) throw new DeclineException("a curve '" + curve.Name + "' without KeyTime or KeyValueFloat (Blender's importer fails on it)");
        if (!(tv is long[] lt)) throw new DeclineException("a curve '" + curve.Name + "' whose KeyTime is not an int64 array (its NumPy arithmetic is not modelled)");
        if (!(vv is float[] fv)) throw new DeclineException("a curve '" + curve.Name + "' whose KeyValueFloat is not a float32 array (its NumPy arithmetic is not modelled)");
        if (lt.Length != fv.Length) throw new DeclineException("a curve '" + curve.Name + "' whose KeyTime and KeyValueFloat differ in length (Blender's importer asserts)");
        bool increasing = true;
        for (int i = 1; i < lt.Length; i++) if (!(lt[i] > lt[i - 1])) { increasing = false; break; }
        if (increasing) { times = lt.Select(x => (double)x).ToArray(); values = fv; return; }
        InvalidCurve(lt, fv, out times, out values);
    }

    /// <summary>blen_read_invalid_animation_curve.</summary>
    static void InvalidCurve(long[] keyTimes, float[] keyValues, out double[] outTimes, out float[] outValues)
    {
        var sorted = keyTimes.Distinct().OrderBy(x => x).ToArray();
        int n = sorted.Length;
        var indices = new long[n];
        {
            long idx = 0;
            for (int s = 0; s < n; s++)
            {
                if (keyTimes[idx < 0 ? keyTimes.Length - 1 : (int)idx] < sorted[s] && idx >= 0)
                {
                    // the generator: idx advances when the current key time is below the time; -1 once past the last
                    idx += 1;
                    if (idx >= keyTimes.Length) idx = -1;
                }
                indices[s] = idx;
            }
        }
        // NumPy indexing: -1 is the last element
        int At(long i) => i < 0 ? keyTimes.Length + (int)i : (int)i;
        var indexedTimes = new long[n]; var indexedValues = new double[n];
        for (int s = 0; s < n; s++) { indexedTimes[s] = keyTimes[At(indices[s])]; indexedValues[s] = keyValues[At(indices[s])]; }
        var interpolated = new double[n];
        int firstNonzero = 0; while (firstNonzero < n && indices[firstNonzero] == 0) firstNonzero++;   // searchsorted(indices, 0, 'right'): the indices ascend
        for (int s = 0; s < firstNonzero; s++) interpolated[s] = indexedValues[s];
        for (int s = firstNonzero; s < n; s++)
        {
            long prev = indices[s] - 1;
            double prevTime = keyTimes[At(prev)], prevValue = keyValues[At(prev)];
            double ifacA = (double)(sorted[s] - (long)prevTime), ifacB = (double)(indexedTimes[s] - (long)prevTime);
            double ifac = ifacA / ifacB;   // a zero divides to inf or nan as NumPy lets it
            // NumPy subtracts two float32 arrays before promotion by the float64 factor.
            float delta = (float)((float)indexedValues[s] - (float)prevValue);
            interpolated[s] = (double)delta * ifac + prevValue;
        }
        var t = new List<double>(); var v = new List<float>();
        for (int s = 0; s < n; s++) if (indexedTimes[s] >= sorted[s]) { t.Add(sorted[s]); v.Add((float)interpolated[s]); }
        outTimes = t.ToArray(); outValues = v.ToArray();
    }

    /// <summary>np.interp(x, xp, fp, left=left) for real inputs, in float64 (numpy/_core/src/multiarray/compiled_base.c arr_interp).</summary>
    internal static double[] Interp(double[] x, double[] xp, double[] fp, double left)
    {
        int n = xp.Length; var r = new double[x.Length];
        double right = fp[n - 1];
        for (int i = 0; i < x.Length; i++)
        {
            double xv = x[i];
            if (double.IsNaN(xv)) { r[i] = xv; continue; }
            if (xv > xp[n - 1]) { r[i] = right; continue; }
            if (xv < xp[0]) { r[i] = left; continue; }
            int j = Array.BinarySearch(xp, xv);
            if (j < 0) j = ~j - 1;
            else { while (j + 1 < n && xp[j + 1] == xv) j++; }   // binary_search_with_guess returns the last index at or below x
            if (j == n - 1) { r[i] = fp[j]; continue; }
            if (xp[j] == xv) { r[i] = fp[j]; continue; }
            double slope = (fp[j + 1] - fp[j]) / (xp[j + 1] - xp[j]);
            double res = slope * (xv - xp[j]) + fp[j];
            if (double.IsNaN(res))
            {
                res = slope * (xv - xp[j + 1]) + fp[j + 1];
                if (double.IsNaN(res) && fp[j] == fp[j + 1]) res = fp[j];
            }
            r[i] = res;
        }
        return r;
    }

    /// <summary>np.deg2rad of a float32 array: x * (NPY_PIf / 180.0f) in float32.</summary>
    internal static float Deg2RadF(float x) => (float)(x * (float)(3.14159265358979323846f / 180.0f));
    /// <summary>np.deg2rad of a float64 array, and math.radians: x * (pi / 180).</summary>
    internal static double Deg2Rad(double x) => x * (Math.PI / 180.0);

    /// <summary>_transformation_curves_gen: the flattened location, rotation and scale values per frame.</summary>
    static void TransformationCurves(FbxImport.Helper item, List<double[]> valueArrays, List<bool> stayFloat, List<(string prop, int channel)> channelKeys, FbxImport.Scene r, float[] flat, int rotCount)
    {
        bool isBone = item.IsBone;
        var t = item.Transform;
        // the previous frame's rotation: the pose bone's quaternion, the object's Euler, as the import left them
        float[] rotQuatPrev = isBone ? HeldQuaternion(item.Bone) : new[] { 1f, 0f, 0f, 0f };
        float[] rotEulPrev = isBone ? new float[3] : (float[])item.Obj.Eul.Clone();
        // combined pre matrix: a bone's bind.inverted_safe() [@ pre_matrix]; an object's pre_matrix
        float[] combinedPre = isBone ? VehicleProbe.InvertedSafe(item.GetBindMatrix()) : Identity();
        if (item.PreMatrix != null) combinedPre = FbxImport.MatMul(combinedPre, item.PreMatrix);
        // combined post matrix: anim_compensation_matrix (a mesh moved under an armature: skipped before this) @ post_matrix (none)
        float[] combinedPost = item.PostMatrix != null ? FbxImport.MatMul(Identity(), item.PostMatrix) : Identity();
        // the initial local matrices (mathutils float32 storage)
        var lclTranslation = Identity(); lclTranslation[3] = (float)t.Loc[0]; lclTranslation[7] = (float)t.Loc[1]; lclTranslation[11] = (float)t.Loc[2];
        var lclEul = new[] { (float)Deg2Rad(t.Rot[0]), (float)Deg2Rad(t.Rot[1]), (float)Deg2Rad(t.Rot[2]) };
        var lclScale = Identity(); lclScale[0] = (float)t.Sca[0]; lclScale[5] = (float)t.Sca[1]; lclScale[10] = (float)t.Sca[2];
        // the values converted: rotations to radians (float32 arrays stay float32, interpolated ones are float64)
        var converted = new List<double[]>();
        for (int i = 0; i < valueArrays.Count; i++)
        {
            var arr = valueArrays[i];
            if (channelKeys[i].prop == "Lcl Rotation") arr = stayFloat[i] ? arr.Select(v => (double)Deg2RadF((float)v)).ToArray() : arr.Select(Deg2Rad).ToArray();
            converted.Add(arr);
        }
        // _blen_read_object_transform_do_anim: the matrices that do not move, combined in advance (left to right)
        float[] RotXyz(double[] deg) => Mat4Of(FbxImport.EulOToMat3(new[] { (float)Deg2Rad(deg[0]), (float)Deg2Rad(deg[1]), (float)Deg2Rad(deg[2]) }, 0));
        var geomLoc = Translation(t.GeomLoc);
        var preRot = RotXyz(t.PreRot); var pstRotInv = VehicleProbe.InvertedSafe(RotXyz(t.PstRot)); var geomRot = RotXyz(t.GeomRot);
        var rotOfs = Translation(t.RotOfs); var rotPiv = Translation(t.RotPiv); var rotPivInv = VehicleProbe.InvertedSafe(rotPiv);
        var scaOfs = Translation(t.ScaOfs); var scaPiv = Translation(t.ScaPiv); var scaPivInv = VehicleProbe.InvertedSafe(scaPiv);
        var geomScale = Identity(); geomScale[0] = (float)t.GeomSca[0]; geomScale[5] = (float)t.GeomSca[1]; geomScale[10] = (float)t.GeomSca[2];
        var geomMat = FbxImport.MatMul(FbxImport.MatMul(geomLoc, geomRot), geomScale);
        var postLclTranslation = FbxImport.MatMul(FbxImport.MatMul(rotOfs, rotPiv), preRot);
        var postLclRotation = FbxImport.MatMul(FbxImport.MatMul(FbxImport.MatMul(FbxImport.MatMul(Identity(), pstRotInv), rotPivInv), scaOfs), scaPiv);   // rot_alt_mat is the identity
        var postLclScaling = FbxImport.MatMul(FbxImport.MatMul(scaPivInv, geomMat), combinedPost);
        bool usePre = !IsIdentity(combinedPre);
        int nf = converted.Count > 0 ? converted[0].Length : 0;
        int stride = 3 + rotCount + 3;
        for (int f = 0; f < nf; f++)
        {
            for (int i = 0; i < converted.Count; i++)
            {
                var (prop, channel) = channelKeys[i];
                float v = (float)converted[i][f];
                if (prop == "Lcl Translation") lclTranslation[channel * 4 + 3] = v;
                else if (prop == "Lcl Rotation") lclEul[channel] = v;
                else lclScale[channel * 5] = v;
            }
            var rot4 = Mat4Of(FbxImport.EulOToMat3(lclEul, t.RotOrd));
            float[] mat = usePre ? FbxImport.MatMul(combinedPre, lclTranslation) : lclTranslation;
            mat = FbxImport.MatMul(mat, postLclTranslation);
            mat = FbxImport.MatMul(mat, rot4);
            mat = FbxImport.MatMul(mat, postLclRotation);
            mat = FbxImport.MatMul(mat, lclScale);
            mat = FbxImport.MatMul(mat, postLclScaling);
            // mat.decompose()
            VehicleProbe.Mat4ToLocRotSize(VehicleProbe.ToColumnMajor(mat), out var loc, out var rot3, out var size);
            var quat = VehicleProbe.Mat3NormalizedToQuatFast(rot3);
            int at = f * stride;
            flat[at] = loc[0]; flat[at + 1] = loc[1]; flat[at + 2] = loc[2];
            if (isBone)
            {
                if (DotQt(rotQuatPrev, quat) < 0f) quat = new[] { -quat[0], -quat[1], -quat[2], -quat[3] };
                rotQuatPrev = quat;
                flat[at + 3] = quat[0]; flat[at + 4] = quat[1]; flat[at + 5] = quat[2]; flat[at + 6] = quat[3];
            }
            else
            {
                var eul = QuatToCompatibleEulXyz(quat, rotEulPrev);
                rotEulPrev = eul;
                flat[at + 3] = eul[0]; flat[at + 4] = eul[1]; flat[at + 5] = eul[2];
            }
            flat[at + 3 + rotCount] = size[0]; flat[at + 4 + rotCount] = size[1]; flat[at + 5 + rotCount] = size[2];
        }
    }

    /// <summary>The pose bone's rotation_quaternion after set_pose_matrix: BKE_pchan_apply_mat4 of the basis
    /// (mat4_to_loc_rot_size, mat3_normalized_to_quat).</summary>
    static float[] HeldQuaternion(FbxArmature.Bone b) => HeldProps(b)[1];

    /// <summary>The pose bone's location, quaternion and scale as the import's matrix_basis left them.</summary>
    internal static float[][] HeldProps(FbxArmature.Bone b)
    {
        if (b.PoseBasis == null) return new[] { new float[3], new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f } };
        VehicleProbe.Mat4ToLocRotSize(VehicleProbe.ToColumnMajor(b.PoseBasis), out var loc, out var rot, out var size);
        return new[] { loc, VehicleProbe.Mat3NormalizedToQuat(rot), size };
    }

    /// <summary>BKE_action_frame_range_calc: the first and the last key over the curves; a single frame stretched by one.</summary>
    static void ActionFrameRange(Action a)
    {
        float min = 999999999.0f, max = -999999999.0f; bool found = false;
        foreach (var c in AllCurves(a))
        {
            if (c.Frames.Length == 0) continue;
            min = Math.Min(min, c.Frames[0]); max = Math.Max(max, c.Frames[c.Frames.Length - 1]); found = true;
        }
        if (found) { if (min == max) max = (float)(max + 1.0f); a.FrameStart = Math.Max(min, -1048574f); a.FrameEnd = Math.Max(max, -1048574f); }
        else { a.FrameStart = 0f; a.FrameEnd = 1f; }
    }

    static IEnumerable<Curve> AllCurves(Action a)
    {
        if (a.Object != null) foreach (var c in a.Object.Loc.Concat(a.Object.Rot).Concat(a.Object.Sca)) yield return c;
        foreach (var (_, ic) in a.Bones) foreach (var c in ic.Loc.Concat(ic.Rot).Concat(ic.Sca)) yield return c;
    }

    // ---- the scene at a frame ----------------------------------------------------------------------------------------------

    /// <summary>scene.frame_set(frame): every object's properties from the action in effect for it (`actionOf`, null for
    /// none), every pose bone's from its armature's action - the import's pose, or the identity after reset_bone_matrix
    /// (`reset`) - under its curves; then BKE_pose_where_is per armature and BKE_object_where_is_calc per object.</summary>
    internal static State Evaluate(FbxImport.Scene s, float frame, Func<FbxImport.Obj, Action> actionOf, Func<FbxImport.Obj, bool> reset, Func<FbxImport.Obj, Action> lingeringOf = null)
    {
        var st = new State();
        foreach (var ob in s.Objects)
        {
            float[] loc = (float[])ob.Loc.Clone(), eul = (float[])ob.Eul.Clone(), size = (float[])ob.Size.Clone();
            var a = actionOf(ob);
            if (!(a != null && a.Object != null && a.IdData == ob) && lingeringOf != null) a = lingeringOf(ob);   // a property without a curve keeps what the last action that keyed it left (at this frame: every fill ends at the global last frame)
            if (a != null && a.Object != null && a.IdData == ob)
            {
                for (int c = 0; c < 3; c++) { loc[c] = a.Object.Loc[c].Evaluate(frame); eul[c] = a.Object.Rot[c].Evaluate(frame); size[c] = a.Object.Sca[c].Evaluate(frame); }
            }
            st.ObjectProps[ob] = new[] { loc, eul, size };
        }
        foreach (var arm in s.Armatures)
        {
            var a = actionOf(arm.Obj);
            bool rs = reset(arm.Obj);
            var curvesOf = new Dictionary<FbxArmature.Bone, ItemCurves>();
            if (a != null) foreach (var (b, ic) in a.Bones) curvesOf[b] = ic;
            foreach (var b in arm.Bones)   // creation order: a parent before its children
            {
                float[][] p = rs ? new[] { new float[3], new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f } } : HeldProps(b);
                var loc = (float[])p[0].Clone(); var quat = (float[])p[1].Clone(); var size = (float[])p[2].Clone();
                if (curvesOf.TryGetValue(b, out var ic))
                {
                    for (int c = 0; c < 3; c++) { loc[c] = ic.Loc[c].Evaluate(frame); size[c] = ic.Sca[c].Evaluate(frame); }
                    for (int c = 0; c < 4; c++) quat[c] = ic.Rot[c].Evaluate(frame);
                }
                st.BoneProps[b] = new[] { loc, quat, size };
                var chan = PchanToMat4(b.Connected ? new float[3] : loc, QuatToMat3Normalized(quat), size);   // BKE_pchan_to_mat4: a connected bone's location is not copied
                var rotscale = b.Parent != null ? VehicleProbe.MulM4(st.Pose[b.Parent], b.Offs) : b.MatrixLocal;
                st.Pose[b] = PoseFromChannel(rotscale, chan);
            }
        }
        // matrix_world: (parent @ parentinv) @ local; a bone child's parent matrix is the armature's world @ the bone's pose
        // matrix with its tail as translation (ob_parbone)
        float[] WorldOf(FbxImport.Obj o)
        {
            if (st.World.TryGetValue(o, out var done)) return done;
            var p = st.ObjectProps[o];
            var local = FbxImport.ObjectMatrixEuler(p[0], p[1], p[2]);
            float[] parentMat = null;
            if (o.Parent != null)
            {
                parentMat = WorldOf(o.Parent);
                if (o.ParentBone != null)
                {
                    var b = o.ParentBone;
                    var tmat = (float[])st.Pose[b].Clone();   // column-major: col 1 is the Y axis, col 3 the translation
                    tmat[12] = (float)(tmat[12] + (float)(tmat[4] * b.Length)); tmat[13] = (float)(tmat[13] + (float)(tmat[5] * b.Length)); tmat[14] = (float)(tmat[14] + (float)(tmat[6] * b.Length));
                    parentMat = VehicleProbe.MulM4(parentMat, tmat);
                }
            }
            float[] world;
            if (parentMat == null) world = local;
            else
            {
                var pm = o.ParentInverse != null ? VehicleProbe.MulM4(parentMat, VehicleProbe.ToColumnMajor(o.ParentInverse)) : parentMat;
                world = VehicleProbe.MulM4(pm, local);
            }
            st.World[o] = world;
            return world;
        }
        foreach (var o in s.Objects) WorldOf(o);
        return st;
    }

    // ---- the export ------------------------------------------------------------------------------------------------------

    /// <summary>The glTF animations of the scene: per object in the vtree's order, its actions, each sampled frame by frame.
    /// `objectOrder` is the vtree's object order, `nodeOf` and `jointOf` the node indices.</summary>
    internal static List<HafAnimation> Export(FbxImport.Scene s, IList<FbxImport.Obj> objectOrder, Dictionary<FbxImport.Obj, int> nodeOf, Dictionary<FbxArmature.Bone, int> jointOf, out string why) => Export(s, objectOrder, nodeOf, jointOf, out why, out _);

    /// <summary>... and `finalState`: the scene as the sampler leaves it - the global last frame under the last fill's or
    /// recompute's actions (an unused skin's inverse bind matrices are gathered from it, after the animations); null when
    /// nothing was sampled.</summary>
    internal static List<HafAnimation> Export(FbxImport.Scene s, IList<FbxImport.Obj> objectOrder, Dictionary<FbxImport.Obj, int> nodeOf, Dictionary<FbxArmature.Bone, int> jointOf, out string why, out State finalState)
    {
        why = null; finalState = null;
        var result = new List<HafAnimation>();
        if (s.Actions.Count == 0) return result;
        try { finalState = ExportInner(s, objectOrder, nodeOf, jointOf, result); }
        catch (DeclineException e) { why = e.Message; return null; }
        return result;
    }

    static State ExportInner(FbxImport.Scene s, IList<FbxImport.Obj> objectOrder, Dictionary<FbxImport.Obj, int> nodeOf, Dictionary<FbxArmature.Bone, int> jointOf, List<HafAnimation> result)
    {
        double fpsTotal = (double)s.Fps * (double)s.FpsBase;   // bpy.context.scene.render.fps * fps_base
        // bpy.data.actions as Blender keeps them: sorted by name
        var byName = new List<Action>(s.Actions);
        BlenderExportTree.StableSort(byName, (a, b) => IdNameCmp(a.Name, b.Name));
        int armatureCount = s.Armatures.Count;
        // the actions per object (__get_blender_actions), and the range per action (prepare_actions_range)
        var actionsOf = new Dictionary<FbxImport.Obj, List<Action>>();
        foreach (var ob in objectOrder)
        {
            var list = new List<Action>();
            if (ob.ActiveAction != null)
            {
                list.Add(ob.ActiveAction);
                if (ob.Kind == FbxImport.ObjectKind.Armature && armatureCount == 1)
                    foreach (var a in byName) if (a != ob.ActiveAction && a.HasBoneCurves && !list.Contains(a)) list.Add(a);
            }
            actionsOf[ob] = list;
        }
        int Start(Action a) { int st = (int)a.FrameStart, en = (int)a.FrameEnd; if (en - st == 1) st = (int)AllCurves(a).Where(c => c.Frames.Length > 0).Min(c => c.Frames[0]); return st; }
        int End(Action a) { int st = (int)a.FrameStart, en = (int)a.FrameEnd; if (en - st == 1) en = (int)AllCurves(a).Where(c => c.Frames.Length > 0).Max(c => c.Frames[c.Frames.Length - 1]); return en; }
        // the sampling cache (cache.py datacache, sampling_cache.py get_cache_data): the first (object, action) sampled fills it
        // for EVERY object over the global range, in the scene's state at that moment - that object on that action (switched
        // and reset when it is not its active one), every other object on its active action with the import's poses -; an
        // object's active action is then a hit (its samples are the first fill's, whatever happened since), another action of
        // an object is recomputed for that object alone, switched to it after reset_bone_matrix; an armature a switch has
        // reset stays reset (the restore resets it again) for the objects hanging from its bones that are recomputed later
        foreach (var ob in objectOrder) BlenderExportTree.StableSort(actionsOf[ob], (a, b) => string.CompareOrdinal(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant()));   // ActionsData.get
        // the first (object, action) with a keyframe fills the cache; the global range (get_range) is every action's
        FbxImport.Obj first = null; Action firstAction = null; int globalMax = int.MinValue;
        foreach (var ob in objectOrder) foreach (var a in actionsOf[ob])
        {
            int st0 = Start(a), en0 = End(a);
            if (en0 > globalMax) globalMax = en0;
            if (first == null && en0 - st0 + 1 > 0) { first = ob; firstAction = a; }
        }
        if (first == null) return null;
        var firstFill = new Dictionary<int, State>();
        Action FirstActionOf(FbxImport.Obj o) => o == first ? firstAction : o.ActiveAction;
        bool FirstResetOf(FbxImport.Obj o) => o == first && firstAction != first.ActiveAction;
        State FirstFill(int frame)
        {
            if (!firstFill.TryGetValue(frame, out var st)) firstFill[frame] = st = Evaluate(s, (float)frame, FirstActionOf, FirstResetOf);
            return st;
        }
        // the state the sampler leaves: the last fill's or recompute's actions (every one ends at the global last frame), and
        // per object the last sampled action that keyed it (its properties linger where the action in effect has no curve)
        Func<FbxImport.Obj, Action> lastActionOf = FirstActionOf; Func<FbxImport.Obj, bool> lastResetOf = FirstResetOf;
        var lingering = new Dictionary<FbxImport.Obj, Action>();
        foreach (var o in s.Objects) { var a = FirstActionOf(o); if (a != null && a.Object != null && a.IdData == o) lingering[o] = a; }
        var switched = new HashSet<FbxImport.Obj>();
        foreach (var ob in objectOrder)
        {
            var actions = actionsOf[ob];
            if (actions.Count == 0) continue;
            bool isArmature = ob.Kind == FbxImport.ObjectKind.Armature;
            var current = ob.ActiveAction;
            for (int ai = 0; ai < actions.Count; ai++)
            {
                var action = actions[ai];
                bool reset = action != current;   // a switch: reset_bone_matrix, then the action assigned
                current = action;
                int start = Start(action), end = End(action);
                if (start < 0) throw new DeclineException("an action starting below frame 0 ('" + action.Name + "' at " + start + ": the CROP and SLIDE of negative frames are not modelled)");
                int n = end - start + 1;
                if (n <= 0) { if (reset) switched.Add(ob); continue; }   // no keyframe: no channel, no animation
                if (n > 1 << 20) throw new DeclineException("an action of " + n + " frames ('" + action.Name + "': the sampler would run for hours)");
                bool inFirstFill = ob == first ? action == firstAction : action == ob.ActiveAction;
                var states = new State[n];
                if (inFirstFill) for (int k = 0; k < n; k++) states[k] = FirstFill(start + k);
                else
                {
                    var switchedNow = new HashSet<FbxImport.Obj>(switched);
                    Action ActionOf(FbxImport.Obj o) => o == ob ? action : o.ActiveAction;
                    bool ResetOf(FbxImport.Obj o) => o == ob ? reset : switchedNow.Contains(o);
                    for (int k = 0; k < n; k++) states[k] = Evaluate(s, (float)(start + k), ActionOf, ResetOf);
                    lastActionOf = ActionOf; lastResetOf = ResetOf;
                    if (action.Object != null && action.IdData == ob) lingering[ob] = action;
                }
                if (reset) switched.Add(ob);
                var anim = new HafAnimation { Name = action.Name };
                var channels = new List<(int node, string path, bool animated, float[][] samples, float[] correction, bool bone)>();
                if (isArmature)
                {
                    var arm = ob.Armature;
                    var walk = new List<FbxArmature.Bone>();
                    void Walk(FbxArmature.Bone b) { walk.Add(b); foreach (var c in b.Children) Walk(c); }
                    foreach (var b in arm.Roots) Walk(b);
                    var animatedBones = new HashSet<FbxArmature.Bone>(action.Bones.Select(x => x.bone));
                    foreach (var b in walk)
                    {
                        var ml = VehicleProbe.ToRowMajor(b.MatrixLocal);
                        float[] corr, restInv;
                        if (b.Parent != null) { var rest = FbxImport.MatMul(VehicleProbe.InvertedSafe(VehicleProbe.ToRowMajor(b.Parent.MatrixLocal)), ml); corr = rest; restInv = VehicleProbe.InvertedSafe(rest); }
                        else { corr = FbxImport.MatMul(AxisBasisChange, ml); restInv = VehicleProbe.InvertedSafe(ml); }
                        var samples = new float[n][];
                        for (int k = 0; k < n; k++)
                        {
                            var boneM = VehicleProbe.ToRowMajor(states[k].Pose[b]);
                            float[] mat = b.Parent != null
                                ? FbxImport.MatMul(FbxImport.MatMul(restInv, VehicleProbe.InvertedSafe(VehicleProbe.ToRowMajor(states[k].Pose[b.Parent]))), boneM)
                                : FbxImport.MatMul(restInv, boneM);
                            samples[k] = Decompose(mat);
                        }
                        bool animated = animatedBones.Contains(b);
                        foreach (string path in new[] { "translation", "rotation", "scale" }) channels.Add((jointOf[b], path, animated, samples, corr, true));
                    }
                    // the armature object's own channels: its animated properties in this action
                    if (action.Object != null && action.IdData == ob)
                    {
                        var samples = ObjectSamples(ob, states, n);
                        foreach (string path in new[] { "translation", "rotation", "scale" }) channels.Add((nodeOf[ob], path, true, samples, null, false));
                    }
                }
                else
                {
                    var samples = ObjectSamples(ob, states, n);
                    bool animated = action.Object != null;
                    foreach (string path in new[] { "translation", "rotation", "scale" }) channels.Add((nodeOf[ob], path, animated, samples, null, false));
                }
                foreach (var (node, path, animated, samples, correction, bone) in channels)
                {
                    int from = path == "translation" ? 0 : path == "rotation" ? 3 : 7, count = path == "rotation" ? 4 : 3;
                    // keyframes.py: fcurve_is_constant - np.ptp per component (in double) below 0.0001
                    bool cst = true;
                    for (int c = 0; c < count && cst; c++)
                    {
                        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                        for (int k = 0; k < n; k++) { double v = samples[k][from + c]; if (v < lo) lo = v; if (v > hi) hi = v; }
                        if (!(hi - lo < 0.0001)) cst = false;
                    }
                    List<int> kept;
                    if (bone) kept = cst && n >= 2 ? new List<int> { 0, n - 1 } : Enumerable.Range(0, n).ToList();
                    else if (animated) kept = cst && n >= 2 ? new List<int> { 0, n - 1 } : Enumerable.Range(0, n).ToList();
                    else { if (cst) continue; kept = Enumerable.Range(0, n).ToList(); }
                    // sampler.py __gather_interpolation: the importer's curves are LINEAR (a single key: baked to LINEAR too)
                    string interp;
                    if (kept.Count > 2) interp = "LINEAR";
                    else if (kept.Count == 1) interp = animated ? "LINEAR" : "STEP";
                    else if (!animated) interp = "STEP";
                    else
                    {
                        bool same = true;
                        for (int c = 0; c < count; c++) if (!FloatsAreEqual(samples[kept[0]][from + c], samples[kept[1]][from + c], 1)) same = false;
                        interp = same ? "STEP" : "LINEAR";
                    }
                    var times = new float[kept.Count]; var values = new List<float>();
                    for (int i = 0; i < kept.Count; i++)
                    {
                        times[i] = (float)((double)(start + kept[i]) / fpsTotal);
                        var sv = samples[kept[i]];
                        if (bone) values.AddRange(Transform(path, sv, from, correction));
                        else
                        {
                            var v = Transform(path, sv, from, Identity());
                            if (path == "translation") values.AddRange(new[] { v[0], v[2], (float)(-v[1]) });
                            else if (path == "rotation") values.AddRange(new[] { v[0], v[2], (float)(-v[1]), v[3] });   // (w, x, z, -y) written x y z w
                            else values.AddRange(new[] { v[0], v[2], v[1] });
                        }
                    }
                    anim.Samplers.Add(new HafSampler { Times = times, Values = values.ToArray(), Components = count, Interpolation = interp });
                    anim.Channels.Add(new HafChannel { Sampler = anim.Samplers.Count - 1, Node = node, Path = path });
                    if (times.Length > 0) anim.Duration = Math.Max(anim.Duration, times[times.Length - 1]);
                }
                if (anim.Channels.Count > 0) result.Add(anim);
            }
        }
        var la = lastActionOf; var lr = lastResetOf;
        return Evaluate(s, (float)globalMax, la, lr, o => lingering.TryGetValue(o, out var held) ? held : null);
    }

    /// <summary>sampling_cache.py object_caching per frame: parent_mat.inverted_safe() @ matrix_world, decomposed.</summary>
    static float[][] ObjectSamples(FbxImport.Obj ob, State[] states, int n)
    {
        var samples = new float[n][];
        for (int k = 0; k < n; k++)
        {
            var st = states[k];
            float[] parentMat;
            if (ob.Parent == null) parentMat = Identity();
            else if (ob.ParentBone == null) parentMat = VehicleProbe.ToRowMajor(st.World[ob.Parent]);
            else parentMat = FbxImport.MatMul(FbxImport.MatMul(VehicleProbe.ToRowMajor(st.World[ob.Parent]), VehicleProbe.ToRowMajor(st.Pose[ob.ParentBone])), AxisBasisChange);
            var mat = FbxImport.MatMul(VehicleProbe.InvertedSafe(parentMat), VehicleProbe.ToRowMajor(st.World[ob]));
            samples[k] = Decompose(mat);
        }
        return samples;
    }

    /// <summary>mat.decompose() of a row-major mathutils matrix: location, the quaternion (w, x, y, z), the scale - 10 floats.</summary>
    internal static float[] Decompose(float[] rowMajor)
    {
        VehicleProbe.Mat4ToLocRotSize(VehicleProbe.ToColumnMajor(rowMajor), out var loc, out var rot3, out var size);
        var quat = VehicleProbe.Mat3NormalizedToQuatFast(rot3);
        return new[] { loc[0], loc[1], loc[2], quat[0], quat[1], quat[2], quat[3], size[0], size[1], size[2] };
    }

    // ---- the maths -------------------------------------------------------------------------------------------------------

    static float[] Identity() { var m = new float[16]; m[0] = m[5] = m[10] = m[15] = 1f; return m; }
    static float[] Translation(double[] v) { var m = Identity(); m[3] = (float)v[0]; m[7] = (float)v[1]; m[11] = (float)v[2]; return m; }
    /// <summary>A 3x3 in Blender's mat[col][row] as a row-major 4x4 of mathutils items.</summary>
    static float[] Mat4Of(float[][] M) { var m = Identity(); for (int col = 0; col < 3; col++) for (int row = 0; row < 3; row++) m[row * 4 + col] = M[col][row]; return m; }
    /// <summary>mathutils Matrix == Matrix.Identity: every item within one float step (EXPP_FloatsAreEqual).</summary>
    static bool IsIdentity(float[] m) { var id = Identity(); for (int i = 0; i < 16; i++) if (!FloatsAreEqual(m[i], id[i], 1)) return false; return true; }
    /// <summary>dot_qtqt in float32, left to right.</summary>
    static float DotQt(float[] a, float[] b) => (float)((float)((float)((float)(a[0] * b[0]) + (float)(a[1] * b[1])) + (float)(a[2] * b[2])) + (float)(a[3] * b[3]));

    /// <summary>Quaternion.to_euler('XYZ', compat): normalize_qt_qt, quat_to_mat3, mat3_normalized_to_compatible_eulO.</summary>
    internal static float[] QuatToCompatibleEulXyz(float[] quat, float[] oldrot)
    {
        var q = (float[])quat.Clone(); VehicleProbe.NormalizeQt(q);
        var mat = QuatToMat3(q);   // mat[col][row]
        Mat3NormalizedToEulo2Xyz(mat, out var eul1, out var eul2);
        CompatibleEul(eul1, oldrot); CompatibleEul(eul2, oldrot);
        float d1 = (float)((float)(Math.Abs((float)(eul1[0] - oldrot[0])) + Math.Abs((float)(eul1[1] - oldrot[1]))) + Math.Abs((float)(eul1[2] - oldrot[2])));
        float d2 = (float)((float)(Math.Abs((float)(eul2[0] - oldrot[0])) + Math.Abs((float)(eul2[1] - oldrot[1]))) + Math.Abs((float)(eul2[2] - oldrot[2])));
        return d1 > d2 ? eul2 : eul1;
    }

    /// <summary>mat3_normalized_to_eulo2 for the XYZ order (i, j, k = 0, 1, 2; no parity): both Euler solutions.</summary>
    static void Mat3NormalizedToEulo2Xyz(float[][] mat, out float[] eul1, out float[] eul2)
    {
        eul1 = new float[3]; eul2 = new float[3];
        float cy = BlenderTrig.Hypotf(mat[0][0], mat[0][1]);
        if (cy > (float)0.0000375)
        {
            eul1[0] = BlenderTrig.Atan2f(mat[1][2], mat[2][2]);
            eul1[1] = BlenderTrig.Atan2f(-mat[0][2], cy);
            eul1[2] = BlenderTrig.Atan2f(mat[0][1], mat[0][0]);
            eul2[0] = BlenderTrig.Atan2f(-mat[1][2], -mat[2][2]);
            eul2[1] = BlenderTrig.Atan2f(-mat[0][2], -cy);
            eul2[2] = BlenderTrig.Atan2f(-mat[0][1], -mat[0][0]);
        }
        else
        {
            eul1[0] = BlenderTrig.Atan2f(-mat[2][1], mat[1][1]);
            eul1[1] = BlenderTrig.Atan2f(-mat[0][2], cy);
            eul1[2] = 0f;
            eul2[0] = eul1[0]; eul2[1] = eul1[1]; eul2[2] = eul1[2];
        }
    }

    /// <summary>compatible_eul: the Euler wrapped by turns to sit next to the old one.</summary>
    static void CompatibleEul(float[] eul, float[] oldrot)
    {
        const float piThresh = (float)Math.PI, piX2 = (float)(2.0f * (float)Math.PI);
        var deul = new float[3];
        for (int i = 0; i < 3; i++)
        {
            deul[i] = (float)(eul[i] - oldrot[i]);
            if (deul[i] > piThresh) { eul[i] = (float)(eul[i] - (float)((float)Math.Floor((float)((float)(deul[i] / piX2) + 0.5f)) * piX2)); deul[i] = (float)(eul[i] - oldrot[i]); }
            else if (deul[i] < -piThresh) { eul[i] = (float)(eul[i] + (float)((float)Math.Floor((float)((float)((float)(-deul[i]) / piX2) + 0.5f)) * piX2)); deul[i] = (float)(eul[i] - oldrot[i]); }
        }
        int j = 1, k = 2;
        for (int i = 0; i < 3; j = k, k = i++)
        {
            if (Math.Abs(deul[i]) > Math.PI && Math.Abs(deul[j]) < Math.PI / 2 && Math.Abs(deul[k]) < Math.PI / 2)
            {
                if (deul[i] > 0.0f) eul[i] = (float)(eul[i] - piX2); else eul[i] = (float)(eul[i] + piX2);
            }
        }
    }

    // ---- Blender's kernel and mathutils, as the deploy and rig_anim ports hold them (BlenderPosedState, BlenderChannelExport,
    // BlenderDeploy): kept here so the FBX drill compiles without those ports' files --------------------------------------

    /// <summary>One fcurve of LINEAR keys: the keys in frame order (BlenderPosedState.Curve).</summary>
    public sealed class Curve
    {
        public float[] Frames = new float[0], Values = new float[0];

        /// <summary>fcurve_eval_keyframes for a curve of linear keys with constant extrapolation.</summary>
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
            if (duration == 0f) return Values[prev];
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

    /// <summary>The order of bpy.data.actions (id_sort_by_name): the names' UTF-8 bytes as unsigned values, upper-case ASCII
    /// folded (BlenderDeploy.IdNameCmp).</summary>
    internal static int IdNameCmp(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a); var y = Encoding.UTF8.GetBytes(b);
        for (int i = 0; ; i++)
        {
            int c1 = i < x.Length ? x[i] : 0, c2 = i < y.Length ? y[i] : 0;
            if (c1 >= 'A' && c1 <= 'Z') c1 += 32;
            if (c2 >= 'A' && c2 <= 'Z') c2 += 32;
            if (c1 != c2) return c1 < c2 ? -1 : 1;
            if (c1 == 0) return 0;
        }
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

    /// <summary>quat_to_mat3 as Blender writes it: the components times sqrt(2) in double, the products in double, each
    /// entry rounded once; mat[col][row].</summary>
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

    /// <summary>BKE_pchan_rot_to_mat3 for a QUATERNION bone: normalize_qt_qt then quat_to_mat3.</summary>
    static float[][] QuatToMat3Normalized(float[] quat) { var q = (float[])quat.Clone(); VehicleProbe.NormalizeQt(q); return QuatToMat3(q); }

    /// <summary>BKE_pchan_to_mat4: the rotation times the size matrix (mul_m3_m3m3, left to right in float32), copied into
    /// a 4x4 with the location in the fourth column; column-major.</summary>
    internal static float[] PchanToMat4(float[] loc, float[][] rot, float[] size)
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

    /// <summary>BKE_bone_parent_transform_apply with one matrix for rotation, scale and location: the matrix times the
    /// channel, the location column through the same matrix apart (mul_v3_m4v3), the axes times a post scale of 1
    /// (BlenderDeploy.PoseFromChannel).</summary>
    internal static float[] PoseFromChannel(float[] rotscale, float[] chan)
    {
        var o = VehicleProbe.MulM4(rotscale, chan);
        float x = chan[12], y = chan[13], z = chan[14];
        for (int k = 0; k < 3; k++)
            o[12 + k] = (float)((float)((float)((float)(x * rotscale[k]) + (float)(y * rotscale[4 + k])) + (float)(z * rotscale[8 + k])) + rotscale[12 + k]);
        for (int k = 0; k < 12; k++) if (k % 4 != 3) o[k] = (float)(o[k] * 1f);
        return o;
    }

    /// <summary>gltf2_blender_math.transform of one sample (loc 3, quat 4 as (w, x, y, z), size 3 at `from`) through the
    /// correction matrix (row-major items), then mathutils_to_gltf (BlenderChannelExport.Transform).</summary>
    internal static float[] Transform(string path, float[] s, int from, float[] correction)
    {
        var m = Identity();
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

    /// <summary>A dictionary that iterates in insertion order (a Python dict).</summary>
    internal sealed class OrderedMap<K, V>
    {
        readonly Dictionary<K, V> map = new Dictionary<K, V>(); readonly List<K> keys = new List<K>();
        public int Count => keys.Count;
        public bool TryGet(K k, out V v) => map.TryGetValue(k, out v);
        public void Set(K k, V v) { if (!map.ContainsKey(k)) keys.Add(k); map[k] = v; }
        public IEnumerable<(K key, V value)> Items { get { foreach (var k in keys) yield return (k, map[k]); } }
    }
}
