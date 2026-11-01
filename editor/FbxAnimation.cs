using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// Step 5 of the native FBX reader: ANIMATION as ufbx 0.23.1 reads and evaluates it (tools/fbx-drill/ufbx/ufbx.c), formula
// for formula. The AnimationStack, AnimationLayer, AnimationCurveNode and AnimationCurve objects become FbxAnimStack,
// FbxAnimLayer, FbxAnimValue and FbxAnimCurve (ufbxi_read_animation_curve, the finalize block ~22179 and
// ufbxi_update_anim_stack); every element's properties learn which of them are ANIMATED or CONNECTED (the pass at
// ufbx.c ~18805, which also synthesises the animated properties an element does not store); the evaluation is
// ufbx_evaluate_curve (constant, linear and cubic Bezier spans with the Newton solve, every extrapolation mode),
// ufbx_evaluate_anim_value, ufbxi_evaluate_props with its layer weights and ufbxi_combine_anim_layer (additive and
// blended layers, the composed rotation through quaternions and the composed scale through powers), and
// ufbx_evaluate_transform. Tangents are floats as in ufbx (cast at every float operation: Mono evaluates an uncast
// float product in double). tools/fbx_drill.sh compares every curve's keys, every curve's samples and every node's
// sampled transform per stack with ufbxdump.c's, as bits.

public sealed class FbxKeyframe
{
    public double Time, Value;
    public int Interpolation;                        // ufbx_interpolation: 0 constant prev, 1 constant next, 2 linear, 3 cubic
    public float LeftDx, LeftDy, RightDx, RightDy;   // ufbx_tangent: dx along time, dy along the value, floats
}

public sealed class FbxAnimCurve
{
    public FbxObject Object; public int Index; public string Name = "";
    public FbxKeyframe[] Keyframes = new FbxKeyframe[0];
    public int PreMode, PostMode;                    // ufbx_extrapolation_mode: 0 constant, 1 repeat, 2 mirror, 3 slope, 4 repeat relative
    public int PreRepeat = -1, PostRepeat = -1;      // repeat counts, negative = infinite
    public double MinValue, MaxValue, MinTime, MaxTime;
    public long KTimeSecond;                         // the scene's, for the frame-perfect repeat arithmetic
}

public sealed class FbxAnimValue
{
    public FbxObject Object; public int Index; public string Name = "";
    public double[] DefaultValue = new double[3];
    public FbxAnimCurve[] Curves = new FbxAnimCurve[3];
}

/// <summary>ufbx_anim_prop: one animated property of one element, owned by a layer.</summary>
public sealed class FbxAnimProp
{
    public FbxObject Element; public uint Key; public string PropName = ""; public FbxAnimValue Value;
}

public sealed class FbxAnimLayer
{
    public FbxObject Object; public int Index; public string Name = "";
    public double Weight = 1.0; public bool WeightIsAnimated, Blended, Additive, ComposeRotation, ComposeScale;
    public readonly List<FbxAnimValue> Values = new List<FbxAnimValue>();
    public FbxAnimProp[] AnimProps = new FbxAnimProp[0];   // sorted by element, key, name (ufbxi_cmp_anim_prop_less)
    public FbxAnim Anim;
    internal Dictionary<(int, string), FbxAnimProp> firstProp;   // the FIRST sorted entry per (element, property)
    internal Dictionary<int, FbxAnimProp> firstOfElement;        // the first sorted entry per element (ufbxi_find_anim_prop_start)
}

/// <summary>ufbx_anim: the layers an evaluation blends, with the time range of the stack it came from.</summary>
public sealed class FbxAnim
{
    public double TimeBegin, TimeEnd;
    public readonly List<FbxAnimLayer> Layers = new List<FbxAnimLayer>();
}

public sealed class FbxAnimStack
{
    public FbxObject Object; public int Index; public string Name = "";
    public double TimeBegin, TimeEnd;
    public readonly List<FbxAnimLayer> Layers = new List<FbxAnimLayer>();
    public FbxAnim Anim;
}

public static class FbxAnimation
{
    // ufbxi_key_flags
    const uint KeyConstant = 0x2, KeyLinear = 0x4, KeyCubic = 0x8, KeyTangentAuto = 0x100, KeyTangentTcb = 0x200, KeyTangentUser = 0x400,
        KeyTangentBroken = 0x800, KeyConstantNext = 0x100, KeyClamp = 0x1000, KeyTimeIndependent = 0x2000, KeyClampProgressive = 0x4000,
        KeyWeightedRight = 0x1000000, KeyWeightedNextLeft = 0x2000000;
    const double KeyClampThreshold = 0.0;   // ufbx_load_opts.key_clamp_threshold, the default

    [System.Runtime.InteropServices.DllImport("ucrtbase.dll", EntryPoint = "pow", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, ExactSpelling = true)]
    static extern double ucrt_pow(double x, double y);
    [System.Runtime.InteropServices.DllImport("ucrtbase.dll", EntryPoint = "rint", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, ExactSpelling = true)]
    static extern double ucrt_rint(double x);
    [System.Runtime.InteropServices.DllImport("ucrtbase.dll", EntryPoint = "acos", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, ExactSpelling = true)]
    static extern double ucrt_acos(double x);
    [System.Runtime.InteropServices.DllImport("ucrtbase.dll", EntryPoint = "asin", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, ExactSpelling = true)]
    static extern double ucrt_asin(double x);
    [System.Runtime.InteropServices.DllImport("ucrtbase.dll", EntryPoint = "atan2", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, ExactSpelling = true)]
    static extern double ucrt_atan2(double y, double x);
    [System.Runtime.InteropServices.DllImport("ucrtbase.dll", EntryPoint = "sin", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, ExactSpelling = true)]
    static extern double ucrt_sin(double x);

    /// <summary>ufbxi_get_name_key: the first four UTF-8 bytes of a name, big-endian, zero-padded.</summary>
    public static uint NameKey(string name)
    {
        var b = Encoding.UTF8.GetBytes(name);
        uint key = 0;
        if (b.Length >= 4) key = (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
        else for (int i = 0; i < 4; i++) { key <<= 8; if (i < b.Length) key |= b[i]; }
        return key;
    }

    // ---- The animated and connected flags of every element's properties (ufbx.c ~18805, before the nodes are built) ----

    /// <summary>For each element, the properties named by a connection INTO it: CONNECTED when a source property is named
    /// (PO/PP), ANIMATED when the source is an AnimationCurveNode without one (OP). A named property the element does not
    /// store is synthesised: a template's copy when present, or zeros when the template omits it. With no template,
    /// animation supplies zeros (ones for Lcl Scaling), and connection-only properties get NO_VALUE placeholders.
    /// Connections with an unresolved end do not exist to ufbx.</summary>
    public static void FlagProperties(FbxScene s)
    {
        foreach (var o in s.Objects)
        {
            var named = new List<FbxConnection>();
            foreach (var c in o.Sources) if (c.Src != null && c.Dst != null && c.DstProp.Length > 0) named.Add(c);
            if (named.Count == 0) continue;
            // connections_dst is sorted by (dst_prop, src_prop), stable on the file order
            var sorted = named.OrderBy(c => c.DstProp, FbxScene.Utf8Order).ThenBy(c => c.SrcProp, FbxScene.Utf8Order).ToList();
            int i = 0;
            while (i < sorted.Count)
            {
                // scan to the next animation connection: a named source property, or an anim value source
                for (; i < sorted.Count; i++) if (sorted[i].SrcProp.Length > 0 || sorted[i].Src.Class == "AnimationCurveNode") break;
                if (i >= sorted.Count) break;
                string name = sorted[i].DstProp;
                bool connected = false, animated = false;
                for (; i < sorted.Count && sorted[i].DstProp == name; i++)
                {
                    if (sorted[i].SrcProp.Length > 0) connected = true;
                    else if (sorted[i].Src.Class == "AnimationCurveNode") animated = true;
                }
                var prop = o.Props.Own(name);
                if (prop != null) { prop.Animated |= animated; prop.Connected |= connected; continue; }
                // an animated property the element does not store: the template's, else a default, else no value
                FbxProp def = null;
                if (o.Props.Defaults != null) def = o.Props.Defaults.Find(name);
                var synthetic = new FbxProp { Name = name, Synthetic = true, Animated = animated, Connected = connected };
                if (def != null) { synthetic.Type = def.Type; synthetic.SubType = def.SubType; synthetic.Flags = def.Flags; synthetic.ValueInt = def.ValueInt; synthetic.HasInt = def.HasInt; Array.Copy(def.Reals, synthetic.Reals, 4); synthetic.RealCount = def.RealCount; synthetic.Animated |= def.Animated; synthetic.Connected |= def.Connected; }
                // ufbx only supplies built-in defaults/NO_VALUE when there is no template at all. If a
                // template exists but omits this property, the synthetic property keeps zero values and flags.
                else if (o.Props.Defaults == null)
                {
                    if (animated) { if (name == "Lcl Scaling") { synthetic.Reals[0] = synthetic.Reals[1] = synthetic.Reals[2] = 1.0; synthetic.RealCount = 3; } }
                    else synthetic.NoValue = true;
                }
                synthetic.ValueStr = ""; synthetic.HasStr = false;
                o.Props.Add(synthetic);
            }
        }
    }

    // ---- Building: ufbxi_read_animation_curve, the finalize block, ufbxi_update_anim_stack ---------------------------------

    public static void Build(FbxScene s)
    {
        var curveOf = new Dictionary<FbxObject, FbxAnimCurve>(); var valueOf = new Dictionary<FbxObject, FbxAnimValue>();
        var layerOf = new Dictionary<FbxObject, FbxAnimLayer>();
        foreach (var o in s.Objects)
        {
            switch (o.Class)
            {
                case "AnimationCurve": { var c = ReadCurve(s, o, s.AnimCurves.Count); s.AnimCurves.Add(c); curveOf[o] = c; break; }
                case "AnimationCurveNode": { var v = new FbxAnimValue { Object = o, Index = s.AnimValues.Count, Name = o.Name }; s.AnimValues.Add(v); valueOf[o] = v; break; }
                case "AnimationLayer": { var l = new FbxAnimLayer { Object = o, Index = s.AnimLayers.Count, Name = o.Name }; s.AnimLayers.Add(l); layerOf[o] = l; break; }
                case "AnimationStack": s.AnimStacks.Add(new FbxAnimStack { Object = o, Index = s.AnimStacks.Count, Name = o.Name }); break;
            }
        }
        // stacks: the layers connected into them, once each (ufbxi_fetch_dst_elements, duplicates warned and dropped)
        foreach (var st in s.AnimStacks)
        {
            var seen = new HashSet<FbxAnimLayer>();
            foreach (var c in ObjectSources(st.Object))
            {
                if (!layerOf.TryGetValue(c.Src, out var l)) continue;
                if (!seen.Add(l)) { s.Warnings.Add($"Duplicate connection to {st.Object.ElementId}"); continue; }
                st.Layers.Add(l);
            }
            st.Anim = new FbxAnim(); st.Anim.Layers.AddRange(st.Layers);
        }
        foreach (var l in s.AnimLayers)
        {
            var seen = new HashSet<FbxAnimValue>();
            foreach (var c in ObjectSources(l.Object))
            {
                if (!valueOf.TryGetValue(c.Src, out var v)) continue;
                if (!seen.Add(v)) { s.Warnings.Add($"Duplicate connection to {l.Object.ElementId}"); continue; }
                l.Values.Add(v);
            }
            l.Anim = new FbxAnim(); l.Anim.Layers.Add(l);
            // the animated properties: every value's named destinations, in the value's connection order (sorted by the
            // destination property, stable on the file order), then stably sorted by element, key and name
            var aprops = new List<FbxAnimProp>();
            foreach (var v in l.Values)
            {
                var conns = v.Object.Targets.Where(c => c.Src != null && c.Dst != null && c.SrcProp.Length == 0 && c.DstProp.Length > 0)
                    .OrderBy(c => c.DstProp, FbxScene.Utf8Order).ToList();
                foreach (var c in conns) aprops.Add(new FbxAnimProp { Element = c.Dst, Key = NameKey(c.DstProp), PropName = c.DstProp, Value = v });
            }
            var arr = aprops.ToArray();
            FbxMath.StableSort(arr, 0, arr.Length, 32, AnimPropLess);
            l.AnimProps = arr;
            l.firstProp = new Dictionary<(int, string), FbxAnimProp>(); l.firstOfElement = new Dictionary<int, FbxAnimProp>();
            foreach (var ap in arr)
            {
                if (!l.firstProp.ContainsKey((ap.Element.ElementId, ap.PropName))) l.firstProp[(ap.Element.ElementId, ap.PropName)] = ap;
                if (!l.firstOfElement.ContainsKey(ap.Element.ElementId)) l.firstOfElement[ap.Element.ElementId] = ap;
            }
            switch (l.Object.Props.Int("BlendMode", 0))
            {
                case 0: l.Blended = true; l.Additive = true; break;    // Additive
                case 1: l.Blended = false; l.Additive = false; break;  // Override
                case 2: l.Blended = true; l.Additive = false; break;   // Override Passthrough
                default: l.Blended = false; l.Additive = false; break;
            }
            var weight = l.Object.Props.Find("Weight");
            if (weight != null)
            {
                l.Weight = weight.Real / 100.0;
                if (l.Weight < 0.0) l.Weight = 0.0;
                if (l.Weight > (double)0.99999f) l.Weight = 1.0;
                l.WeightIsAnimated = weight.Animated;
            }
            else { l.Weight = 1.0; l.WeightIsAnimated = false; }
            l.ComposeRotation = l.Object.Props.Int("RotationAccumulationMode", 0) == 0;
            l.ComposeScale = l.Object.Props.Int("ScaleAccumulationMode", 0) == 0;
        }
        foreach (var v in s.AnimValues)
        {
            var props = v.Object.Props;
            v.DefaultValue[0] = props.Real("X", v.DefaultValue[0]); v.DefaultValue[0] = props.Real("d|X", v.DefaultValue[0]);
            v.DefaultValue[1] = props.Real("Y", v.DefaultValue[1]); v.DefaultValue[1] = props.Real("d|Y", v.DefaultValue[1]);
            v.DefaultValue[2] = props.Real("Z", v.DefaultValue[2]); v.DefaultValue[2] = props.Real("d|Z", v.DefaultValue[2]);
            // the curves connected into the value, in connections_dst order (by destination property, then source property, then file order)
            var conns = v.Object.Sources.Where(c => c.Src != null && c.Dst != null).OrderBy(c => c.DstProp, FbxScene.Utf8Order).ThenBy(c => c.SrcProp, FbxScene.Utf8Order);
            foreach (var c in conns)
            {
                if (c.SrcProp.Length > 0 || !curveOf.TryGetValue(c.Src, out var curve)) continue;
                int index = 0;
                if (c.DstProp == "Y" || c.DstProp == "d|Y") index = 1;
                if (c.DstProp == "Z" || c.DstProp == "d|Z") index = 2;
                var prop = props.FindAny(c.DstProp);
                if (prop != null) v.DefaultValue[index] = prop.Real;
                v.Curves[index] = curve;
            }
        }
        foreach (var c in s.AnimCurves)
            if (c.Keyframes.Length > 0) { c.MinTime = c.Keyframes[0].Time; c.MaxTime = c.Keyframes[c.Keyframes.Length - 1].Time; }
        // ufbxi_update_anim_stack: the time range from LocalStart/LocalStop, else ReferenceStart/ReferenceStop, in KTime ticks
        foreach (var st in s.AnimStacks)
        {
            var props = st.Object.Props;
            FbxProp begin = props.Find("LocalStart"), end = props.Find("LocalStop");
            if (begin == null || end == null) { begin = props.Find("ReferenceStart"); end = props.Find("ReferenceStop"); }
            if (begin != null && end != null)
            {
                st.TimeBegin = (double)begin.ValueInt / (double)s.KTimeSecond;
                st.TimeEnd = (double)end.ValueInt / (double)s.KTimeSecond;
            }
            st.Anim.TimeBegin = st.TimeBegin; st.Anim.TimeEnd = st.TimeEnd;
        }
        s.Anim = s.AnimStacks.Count > 0 ? s.AnimStacks[0].Anim : new FbxAnim();
    }

    static IEnumerable<FbxConnection> ObjectSources(FbxObject o)
    {
        foreach (var c in o.Sources) if (c.IsObjectConnection && c.Src != null && c.Dst != null) yield return c;
    }

    static bool AnimPropLess(FbxAnimProp a, FbxAnimProp b)
    {
        if (a.Element != b.Element) return a.Element.ElementId < b.Element.ElementId;
        if (a.Key != b.Key) return a.Key < b.Key;
        return FbxScene.Utf8Order.Compare(a.PropName, b.PropName) < 0;
    }

    static FbxAnimCurve ReadCurve(FbxScene s, FbxObject o, int index)
    {
        var rec = o.Record;
        var c = new FbxAnimCurve { Object = o, Index = index, Name = o.Name, KTimeSecond = s.KTimeSecond };
        ReadExtrapolation(rec.First("Pre-Extrapolation"), out c.PreMode, out c.PreRepeat);
        ReadExtrapolation(rec.First("Post-Extrapolation"), out c.PostMode, out c.PostRepeat);
        var times = ArrayChild(rec, "KeyTime"); var values = ArrayChild(rec, "KeyValueFloat"); var attrFlags = ArrayChild(rec, "KeyAttrFlags");
        var attrs = ArrayChild(rec, "KeyAttrDataFloat"); var refCounts = ArrayChild(rec, "KeyAttrRefCount");
        long[] t = Longs(times); double[] v = FbxScene.Reals(values, "KeyValueFloat"); int[] flags = FbxScene.Integers(attrFlags, "KeyAttrFlags");
        float[] attr = Floats(attrs); int[] refs = FbxScene.Integers(refCounts, "KeyAttrRefCount");
        if (t.Length != v.Length) throw new InvalidDataException($"FBX: animation curve '{o.Name}': KeyTime and KeyValueFloat differ in length");
        if (flags.Length != refs.Length) throw new InvalidDataException($"FBX: animation curve '{o.Name}': KeyAttrFlags and KeyAttrRefCount differ in length");
        if (attr.Length != refs.Length * 4) throw new InvalidDataException($"FBX: animation curve '{o.Name}': KeyAttrDataFloat is not four floats per attribute");
        int n = t.Length;
        var keys = new FbxKeyframe[n];
        double ktime = (double)s.KTimeSecond;
        // the previous key defines the weight and slope of the left tangent
        float slopeLeft = 0.0f, weightLeft = 0.333333f;
        double prevTime = 0.0, nextTime = 0.0;
        int refsLeft = 0, pf = 0, pa = 0, pr = 0;
        if (n > 0) { nextTime = (double)t[0] / ktime; if (refs.Length > 0) refsLeft = refs[0]; }
        for (int i = 0; i < n; i++)
        {
            var key = new FbxKeyframe(); keys[i] = key;
            if (!(refsLeft > 0)) throw new InvalidDataException($"FBX: animation curve '{o.Name}': KeyAttrRefCount runs out at key {i}");
            double value = v[i];
            if (i == 0) { c.MinValue = value; c.MaxValue = value; }
            else { c.MinValue = c.MinValue < value ? c.MinValue : value; c.MaxValue = c.MaxValue < value ? value : c.MaxValue; }   // ufbxi_min_real (a < b ? a : b) / ufbxi_max_real (a < b ? b : a): a tie keeps the LAST for min and the FIRST for max (signed zeros compare equal)
            key.Time = nextTime; key.Value = value;
            if (i + 1 < n) nextTime = (double)t[i + 1] / ktime;
            uint fl = unchecked((uint)flags[pf]);
            float slopeRight = attr[pa], weightRight = 0.333333f, nextSlopeLeft = attr[pa + 1], nextWeightLeft = 0.333333f;
            if ((fl & (KeyWeightedRight | KeyWeightedNextLeft)) != 0)
            {
                // two 0.4 DECIMAL fixed-point weights packed into the bits of one float
                uint packed = BitConverter.ToUInt32(BitConverter.GetBytes(attr[pa + 2]), 0);
                if ((fl & KeyWeightedRight) != 0) weightRight = (float)((float)(packed & 0xffff) * 0.0001f);
                if ((fl & KeyWeightedNextLeft) != 0) nextWeightLeft = (float)((float)(packed >> 16) * 0.0001f);
            }
            if ((fl & KeyConstant) != 0)
            {
                key.Interpolation = (fl & KeyConstantNext) != 0 ? 1 : 0;
                weightRight = nextWeightLeft = 0.333333f;
                slopeRight = nextSlopeLeft = 0.0f;
            }
            else if ((fl & KeyCubic) != 0)
            {
                key.Interpolation = 3;
                if ((fl & KeyTangentTcb) != 0)
                {
                    double tcbSlopeLeft = 0.0, tcbSlopeRight = 0.0; bool tcbEdge = false;
                    if (i > 0 && key.Time > prevTime) tcbSlopeLeft = (key.Value - v[i - 1]) / (key.Time - prevTime); else tcbEdge = true;
                    if (i + 1 < n && nextTime > key.Time) tcbSlopeRight = (v[i + 1] - key.Value) / (nextTime - key.Time); else tcbEdge = true;
                    SolveTcb(out slopeLeft, out slopeRight, attr[pa], attr[pa + 1], attr[pa + 2], tcbSlopeLeft, tcbSlopeRight, tcbEdge);
                    nextSlopeLeft = 0.0f; nextWeightLeft = 0.333333f;
                }
                else if ((fl & KeyTangentUser) != 0)
                {
                    // user tangents: broken or unified, the slopes stay as stored
                }
                else
                {
                    if (i > 0 && i + 1 < n && key.Time > prevTime && nextTime > key.Time)
                    {
                        if (Math.Abs((double)(float)(slopeLeft + slopeRight)) <= (double)0.0001f)
                            slopeLeft = slopeRight = SolveAutoTangent(prevTime, key.Time, nextTime, v[i - 1], key.Value, v[i + 1], weightLeft, weightRight, slopeRight, fl);
                        else
                        {
                            float left = SolveAutoTangent(prevTime, key.Time, nextTime, v[i - 1], key.Value, v[i + 1], weightLeft, weightRight, -slopeLeft, fl);
                            float right = SolveAutoTangent(prevTime, key.Time, nextTime, v[i - 1], key.Value, v[i + 1], weightLeft, weightRight, slopeRight, fl);
                            slopeLeft = left; slopeRight = right;
                        }
                    }
                    else if (i > 0 && key.Time > prevTime)
                        slopeLeft = slopeRight = SolveAutoTangentLeft(prevTime, key.Time, v[i - 1], key.Value, -slopeLeft, fl);
                    else if (i + 1 < n && nextTime > key.Time)
                        slopeLeft = slopeRight = SolveAutoTangentRight(key.Time, nextTime, key.Value, v[i + 1], slopeRight, fl);
                    else slopeLeft = slopeRight = 0.0f;   // the only or an invalid key
                }
            }
            else
            {
                // linear or unknown: cubic tangents that match the linear span, weights of a third
                key.Interpolation = 2;
                weightRight = 0.333333f; nextWeightLeft = 0.333333f;
                if (nextTime > key.Time)
                {
                    double deltaTime = nextTime - key.Time;
                    if (deltaTime > 0.0) { double slope = (v[i + 1] - key.Value) / deltaTime; slopeRight = nextSlopeLeft = (float)slope; }
                    else slopeRight = nextSlopeLeft = 0.0f;
                }
                else slopeRight = nextSlopeLeft = 0.0f;
            }
            // dx from the weight of the time span to the neighbour, dy = slope * dx
            if (key.Time > prevTime) { double delta = key.Time - prevTime; key.LeftDx = (float)(weightLeft * delta); key.LeftDy = (float)(key.LeftDx * slopeLeft); }
            else { key.LeftDx = 0.0f; key.LeftDy = 0.0f; }
            if (nextTime > key.Time) { double delta = nextTime - key.Time; key.RightDx = (float)(weightRight * delta); key.RightDy = (float)(key.RightDx * slopeRight); }
            else { key.RightDx = 0.0f; key.RightDy = 0.0f; }
            slopeLeft = nextSlopeLeft; weightLeft = nextWeightLeft;
            prevTime = key.Time;
            if (--refsLeft == 0) { pf++; pa += 4; pr++; if (pr < refs.Length) refsLeft = refs[pr]; }
        }
        c.Keyframes = keys;
        return c;
    }

    static void ReadExtrapolation(FbxNode child, out int mode, out int repeat)
    {
        mode = 0; repeat = -1;
        if (child == null) return;
        var type = child.First("Type");
        if (type == null || type.Properties.Count == 0 || !FbxProps.IsNumber(type.Properties[0])) return;
        switch (FbxProps.AsInt(type.Properties[0]))
        {
            case 'A': mode = 4; break;   // repeat relative
            case 'C': mode = 0; break;   // constant
            case 'K': mode = 3; break;   // slope
            case 'M': mode = 2; break;   // mirror
            case 'R': mode = 1; break;   // repeat
            default: break;
        }
        var rep = child.First("Repetition");
        if (rep != null && rep.Properties.Count > 0 && FbxProps.IsNumber(rep.Properties[0]))
        {
            repeat = unchecked((int)FbxProps.AsInt(rep.Properties[0]));
            if (repeat < 0) repeat = -1;
        }
    }

    static FbxNode ArrayChild(FbxNode rec, string name)
    {
        var n = rec.First(name);
        if (n == null || n.Properties.Count == 0 || !(n.Properties[0] is System.Array)) throw new InvalidDataException($"FBX: animation curve without {name}");
        return n;
    }
    static long[] Longs(FbxNode n)
    {
        switch (n.Properties[0])
        {
            case long[] l: return l;
            case int[] a: { var d = new long[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = a[i]; return d; }
            case byte[] a: { var d = new long[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = a[i]; return d; }
            case bool[] a: { var d = new long[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = a[i] ? 1 : 0; return d; }
            case float[] a: { var d = new long[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = FbxProps.AsInt((double)a[i]); return d; }
            case double[] a: { var d = new long[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = FbxProps.AsInt(a[i]); return d; }
            default: throw new InvalidDataException("FBX: KeyTime is not a number array");
        }
    }
    /// <summary>The parser's 'f' destination: every stored number converted to float (a double array is NARROWED).</summary>
    static float[] Floats(FbxNode n)
    {
        switch (n.Properties[0])
        {
            case float[] f: return f;
            case double[] a: { var d = new float[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = (float)a[i]; return d; }
            case int[] a: { var d = new float[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = (float)a[i]; return d; }
            case long[] a: { var d = new float[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = (float)a[i]; return d; }
            case byte[] a: { var d = new float[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = a[i]; return d; }
            case bool[] a: { var d = new float[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = a[i] ? 1 : 0; return d; }
            default: throw new InvalidDataException("FBX: KeyAttrDataFloat is not a number array");
        }
    }

    // ---- Tangent solvers (ufbxi_solve_auto_tangent and company), floats where ufbx has floats ------------------------------

    static float SolveAutoTangent(double prevTime, double time, double nextTime, double prevValue, double value, double nextValue, float weightLeft, float weightRight, float autoBias, uint flags)
    {
        // clamp the tangent to zero near either neighbour
        if ((flags & KeyClamp) != 0)
            if (FbxMath.FMin(Math.Abs(prevValue - value), Math.Abs(nextValue - value)) <= KeyClampThreshold) return 0.0f;
        // time-independent: the slope between the two neighbours
        double slope = (nextValue - prevValue) / (nextTime - prevTime);
        // otherwise a blend of the left and right slopes and that difference
        if ((flags & KeyTimeIndependent) == 0)
        {
            double slopeLeft = (value - prevValue) / (time - prevTime);
            double slopeRight = (nextValue - value) / (nextTime - time);
            double delta = (time - prevTime) / (nextTime - prevTime);
            slope = slope * 0.5 + (slopeLeft * (1.0 - delta) + slopeRight * delta) * 0.5;
            double biasWeight = Math.Abs((double)autoBias) / 100.0;
            if (biasWeight > 0.0001)
            {
                double biasTarget = autoBias > 0.0 ? slopeRight : slopeLeft;
                double biasDelta = biasTarget - slope;
                slope = slope * (1.0 - biasWeight) + biasTarget * biasWeight;
                // a bias past 500 adds ((bias-500)/100)^2 * 40 to the slope
                double absBiasWeight = biasWeight - 5.0;
                if (absBiasWeight > 0.0)
                {
                    double biasSign = Math.Abs(biasDelta) > 0.00001 ? biasDelta : autoBias;
                    biasSign = biasSign > 0.0 ? 1.0 : -1.0;
                    slope += absBiasWeight * absBiasWeight * biasSign * 40.0;
                }
            }
        }
        // no overshoot: the slope clamped where either tangent would pass the endpoints
        if ((flags & KeyClampProgressive) != 0)
        {
            double slopeSign = slope >= 0.0 ? 1.0 : -1.0;
            double absSlope = slopeSign * slope;
            double rangeLeft = weightLeft * (time - prevTime);
            double rangeRight = weightRight * (nextTime - time);
            double maxLeft = rangeLeft > 0.0 ? slopeSign * (value - prevValue) / rangeLeft : 0.0;
            double maxRight = rangeRight > 0.0 ? slopeSign * (nextValue - value) / rangeRight : 0.0;
            if (!(maxLeft > 0.0)) maxLeft = 0.0;
            if (!(maxRight > 0.0)) maxRight = 0.0;
            if (absSlope > maxLeft) absSlope = maxLeft;
            if (absSlope > maxRight) absSlope = maxRight;
            slope = slopeSign * absSlope;
        }
        return (float)slope;
    }

    static float SolveAutoTangentLeft(double prevTime, double time, double prevValue, double value, float autoBias, uint flags)
    {
        if ((flags & KeyClampProgressive) != 0) return 0.0f;
        if ((flags & KeyClamp) != 0 && Math.Abs(prevValue - value) <= KeyClampThreshold) return 0.0f;
        double slope = (value - prevValue) / (time - prevTime);
        if ((flags & KeyTimeIndependent) == 0)
        {
            double absBiasWeight = Math.Abs((double)autoBias) / 100.0 - 5.0;
            if (absBiasWeight > 0.0) { double biasSign = autoBias > 0.0 ? 1.0 : -1.0; slope += absBiasWeight * absBiasWeight * biasSign * 40.0; }
        }
        return (float)slope;
    }

    static float SolveAutoTangentRight(double time, double nextTime, double value, double nextValue, float autoBias, uint flags)
    {
        if ((flags & KeyClampProgressive) != 0) return 0.0f;
        if ((flags & KeyClamp) != 0 && Math.Abs(nextValue - value) <= KeyClampThreshold) return 0.0f;
        double slope = (nextValue - value) / (nextTime - time);
        if ((flags & KeyTimeIndependent) == 0)
        {
            double absBiasWeight = Math.Abs((double)autoBias) / 100.0 - 5.0;
            if (absBiasWeight > 0.0) { double biasSign = autoBias > 0.0 ? 1.0 : -1.0; slope += absBiasWeight * absBiasWeight * biasSign * 40.0; }
        }
        return (float)slope;
    }

    static void SolveTcb(out float slopeLeftOut, out float slopeRightOut, double tension, double continuity, double bias, double slopeLeft, double slopeRight, bool edge)
    {
        double factor = edge ? 1.0 : 0.5;
        double d00 = factor * (1.0 - tension) * (1.0 + bias) * (1.0 - continuity);
        double d01 = factor * (1.0 - tension) * (1.0 - bias) * (1.0 + continuity);
        double d10 = factor * (1.0 - tension) * (1.0 + bias) * (1.0 + continuity);
        double d11 = factor * (1.0 - tension) * (1.0 - bias) * (1.0 - continuity);
        slopeLeftOut = (float)(d00 * slopeLeft + d01 * slopeRight);
        slopeRightOut = (float)(d10 * slopeLeft + d11 * slopeRight);
    }

    // ---- Evaluation: ufbx_evaluate_curve, ufbx_evaluate_anim_value, ufbxi_evaluate_props, ufbx_evaluate_transform ------------

    public static double EvaluateCurve(FbxAnimCurve curve, double time, double defaultValue) => EvaluateCurve(curve, time, defaultValue, false);

    static double EvaluateCurve(FbxAnimCurve curve, double time, double defaultValue, bool noExtrapolation)
    {
        if (curve == null) return defaultValue;
        var keys = curve.Keyframes;
        int count = keys.Length;
        if (count <= 1) return count == 1 ? keys[0].Value : defaultValue;
        if (!noExtrapolation && (time < curve.MinTime || time > curve.MaxTime)) return Extrapolate(curve, time);
        int begin = 0, end = count;
        while (end - begin >= 8)
        {
            int mid = (begin + end) >> 1;
            if (keys[mid].Time <= time) begin = mid + 1; else end = mid;
        }
        end = count;
        for (; begin < end; begin++)
        {
            var next = keys[begin];
            if (next.Time <= time) continue;
            if (begin == 0) return next.Value;                 // before the first key
            var prev = keys[begin - 1];
            if (prev.Time == time) return prev.Value;          // exactly on a key
            double rcpDelta = 1.0 / (next.Time - prev.Time);
            double t = (time - prev.Time) * rcpDelta;
            switch (prev.Interpolation)
            {
                case 0: return prev.Value;
                case 1: return next.Value;
                case 2: return prev.Value * (1.0 - t) + next.Value * t;
                case 3:
                {
                    double x1 = prev.RightDx * rcpDelta;
                    double x2 = 1.0 - next.LeftDx * rcpDelta;
                    t = FindCubicBezierT(x1, x2, t);
                    double t2 = t * t, t3 = t2 * t;
                    double u = 1.0 - t, u2 = u * u, u3 = u2 * u;
                    double y0 = prev.Value, y3 = next.Value;
                    double y1 = y0 + prev.RightDy, y2 = y3 - next.LeftDy;
                    return u3 * y0 + 3.0 * (u2 * t * y1 + u * t2 * y2) + t3 * y3;
                }
                default: return 0.0;
            }
        }
        return keys[count - 1].Value;   // after the last key
    }

    /// <summary>ufbxi_find_cubic_bezier_t: three unrolled Newton-Raphson steps, then up to eight more to 4 ulp of exact.</summary>
    static double FindCubicBezierT(double p1, double p2, double x0)
    {
        double p1_3 = p1 * 3.0, p2_3 = p2 * 3.0;
        double a = p1_3 - p2_3 + 1.0;
        double b = p2_3 - p1_3 - p1_3;
        double c = p1_3;
        double a_3 = 3.0 * a, b_2 = 2.0 * b;
        double t = x0;
        double x1, t2, t3;
        t2 = t * t; t3 = t2 * t; x1 = a * t3 + b * t2 + c * t - x0;
        t -= x1 / (a_3 * t2 + b_2 * t + c);
        t2 = t * t; t3 = t2 * t; x1 = a * t3 + b * t2 + c * t - x0;
        t -= x1 / (a_3 * t2 + b_2 * t + c);
        t2 = t * t; t3 = t2 * t; x1 = a * t3 + b * t2 + c * t - x0;
        t -= x1 / (a_3 * t2 + b_2 * t + c);
        const double eps = 8.881784197001252e-16;
        if (Math.Abs(x1) <= eps) return t;
        for (int i = 0; i < 4; i++)
        {
            t2 = t * t; t3 = t2 * t; x1 = a * t3 + b * t2 + c * t - x0;
            t -= x1 / (a_3 * t2 + b_2 * t + c);
            t2 = t * t; t3 = t2 * t; x1 = a * t3 + b * t2 + c * t - x0;
            t -= x1 / (a_3 * t2 + b_2 * t + c);
            if (Math.Abs(x1) <= eps) return t;
        }
        return t;
    }

    /// <summary>ufbxi_extrapolate_curve: constant, slope, or the repeating modes in KTime ticks (frame perfect).</summary>
    static double Extrapolate(FbxAnimCurve curve, double realTime)
    {
        var keys = curve.Keyframes;
        bool pre = realTime < curve.MinTime;
        var key = pre ? keys[0] : keys[keys.Length - 1];
        int mode = pre ? curve.PreMode : curve.PostMode, repeatCount = pre ? curve.PreRepeat : curve.PostRepeat;
        if (mode == 0) return key.Value;
        if (mode == 3)
        {
            float dx = pre ? key.RightDx : key.LeftDx, dy = pre ? key.RightDy : key.LeftDy;
            return key.Value + (dy * ((realTime - key.Time) / dx));
        }
        if (repeatCount == 0) return key.Value;
        double scale = (double)curve.KTimeSecond;
        double minTime = ucrt_rint(curve.MinTime * scale);
        double maxTime = ucrt_rint(curve.MaxTime * scale);
        double time = realTime * scale;
        double delta = pre ? minTime - time : time - maxTime;
        double duration = maxTime - minTime;
        if (!(duration >= 1.0)) return key.Value;   // at least one tick
        double rep = delta / duration;
        double repN = Math.Floor(rep);
        double repD = delta - repN * duration;
        if (repeatCount > 0 && repN >= (double)repeatCount) { repN = (double)(repeatCount - 1); repD = duration; }
        if (mode == 2)
        {
            double parity = repN * 0.5 - Math.Floor(repN * 0.5);
            if (parity <= 0.25) repD = duration - repD;
        }
        if (pre) repD = duration - repD;
        double newTime = (minTime + repD) / scale;
        double value = EvaluateCurve(curve, newTime, key.Value, true);
        if (mode == 4)
        {
            double valDelta = keys[keys.Length - 1].Value - keys[0].Value;
            if (pre) valDelta = -valDelta;
            value += valDelta * (repN + 1.0);
        }
        return value;
    }

    public static double EvaluateReal(FbxAnimValue value, double time)
    {
        if (value == null) return 0.0;
        double res = value.DefaultValue[0];
        if (value.Curves[0] != null) res = EvaluateCurve(value.Curves[0], time, res, false);
        return res;
    }

    public static double[] EvaluateVec3(FbxAnimValue value, double time)
    {
        if (value == null) return new double[3];
        var res = new[] { value.DefaultValue[0], value.DefaultValue[1], value.DefaultValue[2] };
        for (int i = 0; i < 3; i++) if (value.Curves[i] != null) res[i] = EvaluateCurve(value.Curves[i], time, res[i], false);
        return res;
    }

    static readonly string[] TransformProps = { "Lcl Rotation", "Lcl Scaling", "Lcl Translation", "PostRotation", "PreRotation", "RotationOffset", "RotationOrder", "RotationPivot", "ScalingOffset", "ScalingPivot" };

    /// <summary>ufbx_evaluate_transform with the default flags: the node's transform properties evaluated through the anim's
    /// layers at the time, composed by ufbxi_get_transform over the node's own properties and templates.</summary>
    public static FbxTransform EvaluateTransform(FbxAnim anim, FbxSceneNode node, double time)
    {
        if (anim == null) return node.LocalTransform;
        if (node.IsRoot) return node.LocalTransform;
        var props = SelectedProps(anim, node.Object, time, TransformProps, new EvaluationState());
        var evaluated = new FbxProps(props, node.Object.Props);
        int order = (int)evaluated.Enum("RotationOrder", 0, 6);
        return FbxScene.GetTransform(evaluated, order, node);
    }

    /// <summary>ufbxi_evaluate_selected_props: the element's OWN properties among the names that are animated or connected,
    /// copied, the connected ones resolved through their connections, then evaluated.</summary>
    static List<FbxProp> SelectedProps(FbxAnim anim, FbxObject element, double time, string[] names, EvaluationState state)
    {
        var props = new List<FbxProp>();
        foreach (string name in names)
        {
            var prop = element.Props.Own(name);
            if (prop == null) continue;
            if (prop.Connected) { var copy = Copy(prop); EvaluateConnectedProp(copy, anim, element, name, time, state); props.Add(copy); }
            else if (prop.Animated) props.Add(Copy(prop));
        }
        EvaluateProps(anim, element, time, props, state);
        return props;
    }

    /// <summary>ufbx_evaluate_prop_flags_len: one property of an element at a time (the public lookup includes templates).</summary>
    public static FbxProp EvaluateProp(FbxAnim anim, FbxObject element, string name, double time) => EvaluateProp(anim, element, name, time, new EvaluationState());

    // Direct connection cycles are handled by the reference's 1000-hop walk. Indirect cycles can also pass
    // through composed rotation -> RotationOrder -> a connected rotation. Keep this state local to one evaluation
    // so concurrent evaluations do not share it, and bound acyclic dependency depth before the runtime stack fails.
    sealed class EvaluationState
    {
        public HashSet<(FbxObject, string)> Active;
    }

    static FbxProp EvaluateProp(FbxAnim anim, FbxObject element, string name, double time, EvaluationState state)
    {
        var active = state.Active ?? (state.Active = new HashSet<(FbxObject, string)>());
        if (!active.Add((element, name))) throw new InvalidDataException($"FBX: Cyclic animation property evaluation at '{element.Name}.{name}'");
        try
        {
            if (active.Count > 128) throw new InvalidDataException($"FBX: animation property evaluation exceeds 128 dependent properties at '{element.Name}.{name}'");
            var prop = element.Props.FindAny(name);
            FbxProp result;
            if (prop != null) result = Copy(prop);
            else result = new FbxProp { Name = name, NotFound = true, ValueStr = "" };
            if (!(result.Animated || result.Connected)) return result;
            if (prop.Connected) EvaluateConnectedProp(result, anim, element, prop.Name, time, state);
            EvaluateProps(anim, element, time, new List<FbxProp> { result }, state);
            return result;
        }
        finally { active.Remove((element, name)); }
    }

    static FbxProp Copy(FbxProp p)
    {
        var c = new FbxProp { Name = p.Name, Type = p.Type, SubType = p.SubType, Flags = p.Flags, ValueInt = p.ValueInt, HasInt = p.HasInt, RealCount = p.RealCount, ValueStr = p.ValueStr, HasStr = p.HasStr,
            Animated = p.Animated, Connected = p.Connected, NoValue = p.NoValue, Synthetic = p.Synthetic, NotFound = p.NotFound };
        System.Array.Copy(p.Reals, c.Reals, 4);
        return c;
    }

    /// <summary>ufbxi_find_prop_connection: the first connection into the element naming this destination property from a
    /// SOURCE property (connections_dst sorted by destination then source property, stable on the file order).</summary>
    static FbxConnection FindPropConnection(FbxObject element, string prop)
    {
        FbxConnection best = null;
        foreach (var c in element.Sources)
        {
            if (c.Src == null || c.Dst == null || c.SrcProp.Length == 0 || c.DstProp != prop) continue;
            if (best == null || FbxScene.Utf8Order.Compare(c.SrcProp, best.SrcProp) < 0) best = c;
        }
        return best;
    }

    /// <summary>ufbxi_evaluate_connected_prop: follow the property connections to their source (at most a thousand hops) and
    /// take that property's evaluated value; a cycle leaves the property to its own animation.</summary>
    static void EvaluateConnectedProp(FbxProp prop, FbxAnim anim, FbxObject element, string name, double time, EvaluationState state)
    {
        var conn = FindPropConnection(element, name);
        for (int i = 0; i < 1000 && conn != null; i++)
        {
            var next = FindPropConnection(conn.Src, conn.SrcProp);
            if (next == null) break;
            conn = next;
        }
        if (conn != null && FindPropConnection(conn.Src, conn.SrcProp) == null)
        {
            var ep = EvaluateProp(anim, conn.Src, conn.SrcProp, time, state);
            System.Array.Copy(ep.Reals, prop.Reals, 4); prop.RealCount = ep.RealCount;
            prop.ValueInt = ep.ValueInt; prop.HasInt = ep.HasInt;
            prop.ValueStr = ep.ValueStr; prop.HasStr = ep.HasStr;
        }
        else prop.Connected = false;
    }

    sealed class CombineContext { public FbxAnim Anim; public FbxObject Element; public double Time; public bool HasRotationOrder; public int RotationOrder; public EvaluationState State; }

    /// <summary>ufbxi_evaluate_props: layer by layer, the first layer assigns and the others combine by weight.</summary>
    static void EvaluateProps(FbxAnim anim, FbxObject element, double time, List<FbxProp> props, EvaluationState state)
    {
        var ctx = new CombineContext { Anim = anim, Element = element, Time = time, State = state };
        for (int layerIx = 0; layerIx < anim.Layers.Count; layerIx++)
        {
            var layer = anim.Layers[layerIx];
            if (!layer.firstOfElement.ContainsKey(element.ElementId)) continue;
            double weight = layer.Weight;
            if (layer.WeightIsAnimated && layer.Blended)
            {
                if (layer.firstOfElement.TryGetValue(layer.Object.ElementId, out var weightProp))
                {
                    weight = EvaluateReal(weightProp.Value, time) / 100.0;
                    if (weight < 0.0) weight = 0.0;
                    if (weight > (double)0.99999f) weight = 1.0;
                }
            }
            foreach (var prop in props)
            {
                if (prop.Connected) continue;   // connections override animation
                if (!layer.firstProp.TryGetValue((element.ElementId, prop.Name), out var aprop)) continue;
                var v = EvaluateVec3(aprop.Value, time);
                if (layerIx == 0) { prop.Reals[0] = v[0]; prop.Reals[1] = v[1]; prop.Reals[2] = v[2]; }
                else CombineLayer(ctx, layer, weight, prop.Name, prop.Reals, v);
            }
        }
        foreach (var prop in props) prop.ValueInt = FbxProps.F64ToI64(prop.Reals[0]);
    }

    /// <summary>ufbxi_combine_anim_layer: additive layers add (rotations composed through quaternions, scales through powers
    /// when the layer composes them), blended layers interpolate, override layers replace.</summary>
    static void CombineLayer(CombineContext ctx, FbxAnimLayer layer, double weight, string propName, double[] result, double[] value)
    {
        if (layer.ComposeRotation && layer.Blended && propName == "Lcl Rotation" && !ctx.HasRotationOrder)
        {
            var rp = EvaluateProp(ctx.Anim, ctx.Element, "RotationOrder", ctx.Time, ctx.State);
            ctx.RotationOrder = rp.ValueInt >= 0 && rp.ValueInt <= 6 ? (int)rp.ValueInt : 0;
            ctx.HasRotationOrder = true;
        }
        if (layer.Additive)
        {
            if (layer.ComposeScale && propName == "Lcl Scaling")
            {
                result[0] *= PowAbs(value[0], weight); result[1] *= PowAbs(value[1], weight); result[2] *= PowAbs(value[2], weight);
            }
            else if (layer.ComposeRotation && propName == "Lcl Rotation")
            {
                var a = FbxMath.EulerToQuat(result, ctx.RotationOrder);
                var b = FbxMath.EulerToQuat(value, ctx.RotationOrder);
                b = Slerp(new[] { 0.0, 0.0, 0.0, 1.0 }, b, weight);
                var res = FbxMath.MulQuat(a, b);
                var e = QuatToEuler(res, ctx.RotationOrder);
                result[0] = e[0]; result[1] = e[1]; result[2] = e[2];
            }
            else { result[0] += value[0] * weight; result[1] += value[1] * weight; result[2] += value[2] * weight; }
        }
        else if (layer.Blended)
        {
            double resWeight = 1.0 - weight;
            if (layer.ComposeScale && propName == "Lcl Scaling")
            {
                result[0] = PowAbs(result[0], resWeight) * PowAbs(value[0], weight);
                result[1] = PowAbs(result[1], resWeight) * PowAbs(value[1], weight);
                result[2] = PowAbs(result[2], resWeight) * PowAbs(value[2], weight);
            }
            else if (layer.ComposeRotation && propName == "Lcl Rotation")
            {
                var a = FbxMath.EulerToQuat(result, ctx.RotationOrder);
                var b = FbxMath.EulerToQuat(value, ctx.RotationOrder);
                var res = Slerp(a, b, weight);
                var e = QuatToEuler(res, ctx.RotationOrder);
                result[0] = e[0]; result[1] = e[1]; result[2] = e[2];
            }
            else
            {
                result[0] = result[0] * resWeight + value[0] * weight;
                result[1] = result[1] * resWeight + value[1] * weight;
                result[2] = result[2] * resWeight + value[2] * weight;
            }
        }
        else { result[0] = value[0]; result[1] = value[1]; result[2] = value[2]; }
    }

    static double PowAbs(double v, double e)
    {
        if (e <= 0.0) return 1.0;
        if (e >= 1.0) return v;
        double sign = v < 0.0 ? -1.0 : 1.0;
        return sign * ucrt_pow(v * sign, e);
    }

    /// <summary>ufbx_quat_slerp.</summary>
    public static double[] Slerp(double[] a, double[] b, double t)
    {
        double dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
        if (dot < 0.0) { dot = -dot; b = new[] { -b[0], -b[1], -b[2], -b[3] }; }
        double omega = ucrt_acos(FbxMath.FMin(FbxMath.FMax(dot, 0.0), 1.0));
        if (omega <= (double)1.175494351e-38f) return a;
        double rcpSo = 1.0 / ucrt_sin(omega);
        double af = ucrt_sin((1.0 - t) * omega) * rcpSo;
        double bf = ucrt_sin(t * omega) * rcpSo;
        double x = af * a[0] + bf * b[0];
        double y = af * a[1] + bf * b[1];
        double z = af * a[2] + bf * b[2];
        double w = af * a[3] + bf * b[3];
        double rcpLen = 1.0 / Math.Sqrt(x * x + y * y + z * z + w * w);
        return new[] { x * rcpLen, y * rcpLen, z * rcpLen, w * rcpLen };
    }

    const double HalfPi = 3.14159265358979323846 * 0.5, RadToDeg = 180.0 / 3.14159265358979323846;

    /// <summary>ufbx_quat_to_euler for the six orders; the gimbal case past 0.999999999 pins the middle axis.</summary>
    public static double[] QuatToEuler(double[] q, int order)
    {
        const double eps = 0.999999999;
        double vx, vy, vz, t;
        double qx = q[0], qy = q[1], qz = q[2], qw = q[3];
        switch (order)
        {
            case 0:   // XYZ
                t = 2.0 * (qw * qy - qx * qz);
                if (Math.Abs(t) < eps) { vy = ucrt_asin(t); vz = ucrt_atan2(2.0 * (qw * qz + qx * qy), 2.0 * (qw * qw + qx * qx) - 1.0); vx = -ucrt_atan2(-2.0 * (qw * qx + qy * qz), 2.0 * (qw * qw + qz * qz) - 1.0); }
                else { vy = CopySign(HalfPi, t); vz = ucrt_atan2(-2.0 * t * (qw * qx - qy * qz), t * (2.0 * qw * qy + 2.0 * qx * qz)); vx = 0.0; }
                break;
            case 1:   // XZY
                t = 2.0 * (qw * qz + qx * qy);
                if (Math.Abs(t) < eps) { vz = ucrt_asin(t); vy = ucrt_atan2(2.0 * (qw * qy - qx * qz), 2.0 * (qw * qw + qx * qx) - 1.0); vx = -ucrt_atan2(-2.0 * (qw * qx - qy * qz), 2.0 * (qw * qw + qy * qy) - 1.0); }
                else { vz = CopySign(HalfPi, t); vy = ucrt_atan2(2.0 * t * (qw * qx + qy * qz), -t * (2.0 * qx * qy - 2.0 * qw * qz)); vx = 0.0; }
                break;
            case 2:   // YZX
                t = 2.0 * (qw * qz - qx * qy);
                if (Math.Abs(t) < eps) { vz = ucrt_asin(t); vx = ucrt_atan2(2.0 * (qw * qx + qy * qz), 2.0 * (qw * qw + qy * qy) - 1.0); vy = -ucrt_atan2(-2.0 * (qw * qy + qx * qz), 2.0 * (qw * qw + qx * qx) - 1.0); }
                else { vz = CopySign(HalfPi, t); vx = ucrt_atan2(-2.0 * t * (qw * qy - qx * qz), t * (2.0 * qw * qz + 2.0 * qx * qy)); vy = 0.0; }
                break;
            case 3:   // YXZ
                t = 2.0 * (qw * qx + qy * qz);
                if (Math.Abs(t) < eps) { vx = ucrt_asin(t); vz = ucrt_atan2(2.0 * (qw * qz - qx * qy), 2.0 * (qw * qw + qy * qy) - 1.0); vy = -ucrt_atan2(-2.0 * (qw * qy - qx * qz), 2.0 * (qw * qw + qz * qz) - 1.0); }
                else { vx = CopySign(HalfPi, t); vz = ucrt_atan2(2.0 * t * (qw * qy + qx * qz), -t * (2.0 * qy * qz - 2.0 * qw * qx)); vy = 0.0; }
                break;
            case 4:   // ZXY
                t = 2.0 * (qw * qx - qy * qz);
                if (Math.Abs(t) < eps) { vx = ucrt_asin(t); vy = ucrt_atan2(2.0 * (qw * qy + qx * qz), 2.0 * (qw * qw + qz * qz) - 1.0); vz = -ucrt_atan2(-2.0 * (qw * qz + qx * qy), 2.0 * (qw * qw + qy * qy) - 1.0); }
                else { vx = CopySign(HalfPi, t); vy = ucrt_atan2(-2.0 * t * (qw * qz - qx * qy), t * (2.0 * qw * qx + 2.0 * qy * qz)); vz = 0.0; }
                break;
            case 5:   // ZYX
                t = 2.0 * (qw * qy + qx * qz);
                if (Math.Abs(t) < eps) { vy = ucrt_asin(t); vx = ucrt_atan2(2.0 * (qw * qx - qy * qz), 2.0 * (qw * qw + qz * qz) - 1.0); vz = -ucrt_atan2(-2.0 * (qw * qz - qx * qy), 2.0 * (qw * qw + qx * qx) - 1.0); }
                else { vy = CopySign(HalfPi, t); vx = ucrt_atan2(2.0 * t * (qw * qz + qx * qy), -t * (2.0 * qx * qz - 2.0 * qw * qy)); vz = 0.0; }
                break;
            default: vx = vy = vz = 0.0; break;
        }
        vx *= RadToDeg; vy *= RadToDeg; vz *= RadToDeg;
        return new[] { vx, vy, vz };
    }

    static double CopySign(double x, double y)
    {
        long bx = BitConverter.DoubleToInt64Bits(x), by = BitConverter.DoubleToInt64Bits(y);
        return BitConverter.Int64BitsToDouble((bx & long.MaxValue) | (by & long.MinValue));
    }
}
