using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

// Step 5 of the native FBX reader: animation stacks, layers, values, curves and their evaluation in editor/FbxAnimation.cs,
// on documents built in memory in the shapes of tools/fbx-drill/fbx_scene_fixtures.py's animation scenes. The expected
// values are ufbx's own (ufbxdump --full on those scenes, 2026-11-01); the bit-level claim on every file is
// tools/fbx_drill.sh's.
public class FbxAnimTests
{
    // ---- builders -------------------------------------------------------------------------------------------------
    const long KTime = 46186158000;
    static FbxNode N(string name, params object[] props) { var n = new FbxNode { Name = name }; n.Properties.AddRange(props); return n; }
    static FbxNode With(FbxNode n, params FbxNode[] children) { n.Children.AddRange(children); return n; }
    static FbxNode P(string name, params object[] values) { var p = N("P", name, "Vector3D", "", ""); p.Properties.AddRange(values); return p; }
    static FbxNode PL(string name, long value) => N("P", name, "KTime", "Time", "", value);
    static FbxNode Props70(params FbxNode[] ps) => With(N("Properties70"), ps);
    static FbxNode Object(string cls, long id, string name, string sub, params FbxNode[] children) => With(N(cls, id, name + "\0\u0001" + cls, sub), children);
    static FbxNode C(long src, long dst) => N("C", "OO", src, dst);
    static FbxNode OP(long src, long dst, string prop) => N("C", "OP", src, dst, prop);
    static FbxNode PP(long src, string srcProp, long dst, string dstProp) => N("C", "PP", src, srcProp, dst, dstProp);
    static FbxNode Model(long id, string name, params FbxNode[] props) => props.Length > 0 ? Object("Model", id, name, "Null", Props70(props)) : Object("Model", id, name, "Null");
    static long Ticks(double seconds) => (long)Math.Round(seconds * KTime);
    const int Auto = 0x108, Linear = 0x4, Const = 0x2, ConstNext = 0x102, User = 0x408, Tcb = 0x208, Clamp = 0x1000, TimeIndependent = 0x2000, ClampProgressive = 0x4000, Broken = 0x800, WeightedRight = 0x1000000, WeightedNextLeft = 0x2000000;
    static readonly float[] Zeros = { 0, 0, 0, 0 };
    static FbxNode Curve(long id, string name, double[] times, double[] values, int[] flags, float[] attrs, int[] refs, (char, int?)? pre = null, (char, int?)? post = null)
    {
        var children = new List<FbxNode>();
        foreach (var (tag, ext) in new[] { ("Pre-Extrapolation", pre), ("Post-Extrapolation", post) })
        {
            if (ext == null) continue;
            var kids = new List<FbxNode> { N("Type", (int)ext.Value.Item1) };
            if (ext.Value.Item2.HasValue) kids.Add(N("Repetition", ext.Value.Item2.Value));
            children.Add(With(N(tag), kids.ToArray()));
        }
        children.Add(N("KeyTime", times.Select(Ticks).ToArray())); children.Add(N("KeyValueFloat", values));
        children.Add(N("KeyAttrFlags", flags)); children.Add(N("KeyAttrDataFloat", attrs)); children.Add(N("KeyAttrRefCount", refs));
        return Object("AnimationCurve", id, name, "", children.ToArray());
    }
    static FbxNode CurveNode(long id, string name, params double[] defaults)
    {
        var ps = new List<FbxNode>(); string[] axes = { "d|X", "d|Y", "d|Z" };
        for (int i = 0; i < defaults.Length; i++) ps.Add(P(axes[i], defaults[i]));
        return ps.Count > 0 ? Object("AnimationCurveNode", id, name, "", Props70(ps.ToArray())) : Object("AnimationCurveNode", id, name, "");
    }
    static FbxNode Layer(long id, string name, params FbxNode[] props) => props.Length > 0 ? Object("AnimationLayer", id, name, "", Props70(props)) : Object("AnimationLayer", id, name, "");
    static FbxNode Stack(long id, string name, double? start = null, double? stop = null, bool reference = false)
    {
        if (start == null) return Object("AnimationStack", id, name, "");
        return Object("AnimationStack", id, name, "", Props70(PL(reference ? "ReferenceStart" : "LocalStart", Ticks(start.Value)), PL(reference ? "ReferenceStop" : "LocalStop", Ticks(stop.Value))));
    }
    static FbxScene Scene(FbxNode[] objects, FbxNode[] connections, FbxNode header = null, FbxNode definitions = null)
    {
        var d = new FbxDocument { Version = 7400 };
        if (header != null) d.Nodes.Add(header);
        if (definitions != null) d.Nodes.Add(definitions);
        d.Nodes.Add(With(N("Objects"), objects)); d.Nodes.Add(With(N("Connections"), connections));
        return FbxScene.Read(d);
    }
    static double Bits(string hex) => BitConverter.Int64BitsToDouble(unchecked((long)Convert.ToUInt64(hex, 16)));
    static string FBits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");
    static string Tangents(FbxKeyframe k) => FBits(k.LeftDx) + "," + FBits(k.LeftDy) + " " + FBits(k.RightDx) + "," + FBits(k.RightDy);
    static FbxSceneNode Node(FbxScene s, string name) => s.Nodes.First(n => n.Name == name);
    static float Packed(double right, double nextLeft) => BitConverter.ToSingle(BitConverter.GetBytes((uint)((int)Math.Round(right * 10000) & 0xffff | (int)Math.Round(nextLeft * 10000) << 16)), 0);

    // the anim_basic scene: one stack, one layer, a node with translation, rotation, scale and pivot values
    static FbxScene Basic()
    {
        var node = Model(1, "Anim", P("Lcl Translation", 1.0, 2.0, 3.0), P("Lcl Rotation", 10.0, 20.0, 30.0), P("Lcl Scaling", 1.0, 1.0, 1.0));
        var objects = new[] { node, Model(2, "Still", P("Lcl Translation", 4.0, 5.0, 6.0)), Stack(60, "Take", 0.0, 2.0), Layer(61, "Base"),
            CurveNode(70, "T", 1, 2, 3), CurveNode(71, "R", 0, 0, 0), CurveNode(72, "S", 1, 1, 1), CurveNode(73, "Empty", 7, 8, 9),
            Curve(80, "TX", new[] { 0.0, 1.0, 2.0 }, new[] { 0.0, 1.0, 3.0 }, new[] { Linear }, Zeros, new[] { 3 }),
            Curve(81, "TY", new[] { 0.0, 0.5, 1.5, 2.0 }, new[] { 0.0, 1.0, -1.0, 0.5 }, new[] { Auto }, Zeros, new[] { 4 }),
            Curve(82, "TZ", new[] { 0.0, 0.4, 0.8, 1.2, 1.6, 2.0 }, new[] { 0.0, 1.0, 0.5, 2.0, -1.0, 0.0 }, new[] { Const, ConstNext, Linear, Auto | Clamp, User, Auto },
                Zeros.Concat(Zeros).Concat(Zeros).Concat(Zeros).Concat(new float[] { 2, -1, 0, 0 }).Concat(Zeros).ToArray(), new[] { 1, 1, 1, 1, 1, 1 }),
            Curve(83, "RZ", new[] { 0.0, 1.0, 2.0, 3.0 }, new[] { 0.0, 90.0, 180.0, 45.0 }, new[] { Auto }, Zeros, new[] { 4 }),
            Curve(84, "SX", new[] { 1.0 }, new[] { 2.0 }, new[] { Auto }, Zeros, new[] { 1 }) };
        var conns = new[] { C(1, 0), C(2, 0), C(61, 60), C(70, 61), C(71, 61), C(72, 61), C(73, 61), OP(70, 1, "Lcl Translation"), OP(71, 1, "Lcl Rotation"), OP(72, 1, "Lcl Scaling"), OP(73, 1, "RotationPivot"),
            OP(80, 70, "d|X"), OP(81, 70, "d|Y"), OP(82, 70, "d|Z"), OP(83, 71, "d|Z"), OP(84, 72, "d|X") };
        return Scene(objects, conns);
    }

    [Fact]
    public void Linear_keys_take_third_weight_tangents_and_evaluate_straight()
    {
        var s = Basic();
        Assert.Equal(1, s.AnimStacks.Count); Assert.Equal(1, s.AnimLayers.Count); Assert.Equal(4, s.AnimValues.Count); Assert.Equal(5, s.AnimCurves.Count);
        var tx = s.AnimCurves[0];
        Assert.Equal(3, tx.Keyframes.Length);
        Assert.All(tx.Keyframes, k => Assert.Equal(2, k.Interpolation));
        Assert.Equal("00000000,00000000 3eaaaa9f,3eaaaa9f", Tangents(tx.Keyframes[0]));
        Assert.Equal("3eaaaa9f,3eaaaa9f 3eaaaa9f,3f2aaa9f", Tangents(tx.Keyframes[1]));
        Assert.Equal("3eaaaa9f,3f2aaa9f 00000000,00000000", Tangents(tx.Keyframes[2]));
        Assert.Equal(0.0, tx.MinValue); Assert.Equal(3.0, tx.MaxValue); Assert.Equal(0.0, tx.MinTime); Assert.Equal(2.0, tx.MaxTime);
        Assert.Equal(0.75, FbxAnimation.EvaluateCurve(tx, 0.75, 0.0));
        Assert.Equal(3.0, FbxAnimation.EvaluateCurve(tx, 2.0, 0.0));
        Assert.Equal(3.0, FbxAnimation.EvaluateCurve(tx, 6.6, 0.0));      // constant extrapolation
        Assert.Equal(0.0, FbxAnimation.EvaluateCurve(tx, -1.0, 0.0));
        Assert.Equal(0.0, s.AnimStacks[0].TimeBegin); Assert.Equal(2.0, s.AnimStacks[0].TimeEnd);
        Assert.Same(s.AnimStacks[0].Anim, s.Anim);
    }

    [Fact]
    public void Cubic_auto_tangents_and_the_bezier_solve_are_ufbx_s()
    {
        var ty = Basic().AnimCurves[1];
        Assert.All(ty.Keyframes, k => Assert.Equal(3, k.Interpolation));
        Assert.Equal("00000000,00000000 3e2aaa9f,3eaaaa9f", Tangents(ty.Keyframes[0]));
        Assert.Equal("3e2aaa9f,23aaaa9f 3eaaaa9f,242aaa9f", Tangents(ty.Keyframes[1]));   // a zero auto slope keeps a denormal dy
        Assert.Equal("3eaaaa9f,3e2aaa9f 3e2aaa9f,3daaaa9f", Tangents(ty.Keyframes[2]));
        Assert.Equal("3e2aaa9f,3effffee 00000000,00000000", Tangents(ty.Keyframes[3]));
        Assert.Equal(Bits("3fe53fff8ecc0097"), FbxAnimation.EvaluateCurve(ty, 0.75, 0.0));
        Assert.Equal(Bits("bfaffffdd0000000"), FbxAnimation.EvaluateCurve(ty, 1.0, 0.0));
        Assert.Equal(1.0, FbxAnimation.EvaluateCurve(ty, 0.5, 0.0));   // exactly on a key
    }

    [Fact]
    public void Constant_previous_and_next_linear_and_user_tangents_in_one_curve()
    {
        var tz = Basic().AnimCurves[2];
        Assert.Equal(new[] { 0, 1, 2, 3, 3, 3 }, tz.Keyframes.Select(k => k.Interpolation).ToArray());
        Assert.Equal("00000000,00000000 3e08887f,00000000", Tangents(tz.Keyframes[0]));   // constant: flat tangents
        Assert.Equal("3e08887f,00000000 3e08887f,3effffee", Tangents(tz.Keyframes[2]));   // linear: the slope to the next key
        Assert.Equal("3e08887f,be633323 3e08887f,be7fffee", Tangents(tz.Keyframes[3]));   // auto, clamped: left from the linear key, right solved
        Assert.Equal("3e08887f,00000000 3e08887f,3e88887f", Tangents(tz.Keyframes[4]));   // user: the stored slopes
        Assert.Equal(0.0, FbxAnimation.EvaluateCurve(tz, 0.2, 9.0));    // constant previous
        Assert.Equal(0.5, FbxAnimation.EvaluateCurve(tz, 0.75, 9.0));   // constant next
        Assert.Equal(1.25, FbxAnimation.EvaluateCurve(tz, 1.0, 9.0));   // linear
        var sx = Basic().AnimCurves[4];
        Assert.Equal(2.0, FbxAnimation.EvaluateCurve(sx, -5.0, 0.0));   // one key: its value everywhere
        Assert.Equal("3eaaaa9f,00000000 00000000,00000000", Tangents(sx.Keyframes[0]));
    }

    [Fact]
    public void A_node_evaluates_through_its_animated_properties_and_pivots()
    {
        var s = Basic();
        var anim = Node(s, "Anim");
        var t = FbxAnimation.EvaluateTransform(s.Anim, anim, 1.0);
        Assert.Equal((Bits("4030000000000001"), Bits("3fee000023000008"), Bits("3ff4000000000000")), (t.tx, t.ty, t.tz));   // the animated pivot (7,8,9) moves the translation
        Assert.Equal((0.0, 0.0, Bits("3fe6a09e667f3bcd"), Bits("3fe6a09e667f3bcd")), (t.qx, t.qy, t.qz, t.qw));
        Assert.Equal((2.0, 1.0, 1.0), (t.sx, t.sy, t.sz));
        var before = FbxAnimation.EvaluateTransform(s.Anim, anim, -1.0);
        Assert.Equal((0.0, 0.0, 0.0), (before.tx, before.ty, before.tz));   // every curve at its first key, the pivot cancels the rest
        var still = FbxAnimation.EvaluateTransform(s.Anim, Node(s, "Still"), 1.0);
        Assert.Equal((4.0, 5.0, 6.0), (still.tx, still.ty, still.tz));   // no animated property: the stored transform
        Assert.Equal(s.Nodes[0].LocalTransform.tx, FbxAnimation.EvaluateTransform(s.Anim, s.Nodes[0], 1.0).tx);   // the root is never evaluated
        // the layer's animated properties, sorted by element, then key and name
        var l = s.AnimLayers[0];
        Assert.Equal(new[] { "Lcl Rotation", "Lcl Scaling", "Lcl Translation", "RotationPivot" }, l.AnimProps.Select(a => a.PropName).ToArray());
        Assert.True(l.Blended && l.Additive && l.ComposeRotation && l.ComposeScale && !l.WeightIsAnimated && l.Weight == 1.0);
        Assert.True(anim.Object.Props.Own("Lcl Translation").Animated);
        Assert.True(anim.Object.Props.Own("RotationPivot").Synthetic && anim.Object.Props.Own("RotationPivot").Animated);
    }

    static FbxScene Extrapolation()
    {
        FbxNode K(long id, string name, (char, int?)? pre, (char, int?)? post) => Curve(id, name, new[] { 0.0, 0.5, 1.0, 2.0 }, new[] { 0.0, 1.0, 3.0, 2.0 }, new[] { Auto }, Zeros, new[] { 4 }, pre, post);
        var objects = new[] { Model(1, "Anim", P("Lcl Translation", 1.0, 2.0, 3.0)), Stack(60, "Take", 0.0, 2.0), Layer(61, "Base"), CurveNode(70, "T", 0, 0, 0), CurveNode(71, "R", 0, 0, 0), CurveNode(72, "S", 1, 1, 1),
            K(80, "Repeat", ('R', -1), ('R', 1)), K(81, "Mirror", ('M', 2), ('M', -1)), K(82, "Slope", ('K', null), ('K', 3)), K(83, "Relative", ('A', -1), ('A', 2)),
            K(84, "Constant", ('C', 5), ('R', 0)), K(85, "Unknown", ('Z', 1), ('M', 0)),
            Curve(86, "Point", new[] { 1.0, 1.0 }, new[] { 2.0, 3.0 }, new[] { Linear }, Zeros, new[] { 2 }, ('R', -1), ('M', -1)),
            Curve(87, "None", new double[0], new double[0], new int[0], new float[0], new int[0], ('R', -1), ('R', -1)) };
        var conns = new[] { C(1, 0), C(61, 60), C(70, 61), C(71, 61), C(72, 61), OP(70, 1, "Lcl Translation"), OP(71, 1, "Lcl Rotation"), OP(72, 1, "Lcl Scaling"),
            OP(80, 70, "d|X"), OP(81, 70, "d|Y"), OP(82, 70, "d|Z"), OP(83, 71, "d|X"), OP(84, 71, "d|Y"), OP(85, 71, "d|Z"), OP(86, 72, "d|X"), OP(87, 72, "d|Y") };
        return Scene(objects, conns);
    }

    [Fact]
    public void Extrapolation_modes_repeat_mirror_slope_and_relative_as_ufbx()
    {
        var s = Extrapolation();
        var c = s.AnimCurves;
        Assert.Equal((1, -1, 1, 1), (c[0].PreMode, c[0].PreRepeat, c[0].PostMode, c[0].PostRepeat));
        Assert.Equal((2, 2, 2, -1), (c[1].PreMode, c[1].PreRepeat, c[1].PostMode, c[1].PostRepeat));
        Assert.Equal((3, -1, 3, 3), (c[2].PreMode, c[2].PreRepeat, c[2].PostMode, c[2].PostRepeat));
        Assert.Equal((4, -1, 4, 2), (c[3].PreMode, c[3].PreRepeat, c[3].PostMode, c[3].PostRepeat));
        Assert.Equal((0, 5, 1, 0), (c[4].PreMode, c[4].PreRepeat, c[4].PostMode, c[4].PostRepeat));
        Assert.Equal((0, 1, 2, 0), (c[5].PreMode, c[5].PreRepeat, c[5].PostMode, c[5].PostRepeat));   // an unknown letter stays constant
        // repeat: a span before is the end of the curve; past the single allowed repetition the last key holds
        Assert.Equal(3.0, FbxAnimation.EvaluateCurve(c[0], -1.0, 0.0));
        Assert.Equal(Bits("4007ae14459999c0"), FbxAnimation.EvaluateCurve(c[0], -4.6, 0.0));
        Assert.Equal(3.0, FbxAnimation.EvaluateCurve(c[0], 3.0, 0.0));
        Assert.Equal(2.0, FbxAnimation.EvaluateCurve(c[0], 6.6, 0.0));
        // mirror: two repetitions before (the third clamps to the start), unbounded after
        Assert.Equal(3.0, FbxAnimation.EvaluateCurve(c[1], -1.0, 0.0));
        Assert.Equal(0.0, FbxAnimation.EvaluateCurve(c[1], -4.6, 0.0));
        Assert.Equal(Bits("4007ae14459999c1"), FbxAnimation.EvaluateCurve(c[1], 6.6, 0.0));
        // slope: along the outer tangent
        Assert.Equal(-2.0, FbxAnimation.EvaluateCurve(c[2], -1.0, 0.0));
        Assert.Equal(Bits("c022666666666666"), FbxAnimation.EvaluateCurve(c[2], -4.6, 0.0));
        Assert.Equal(1.0, FbxAnimation.EvaluateCurve(c[2], 3.0, 0.0));
        Assert.Equal(Bits("c004cccccccccccc"), FbxAnimation.EvaluateCurve(c[2], 6.6, 0.0));
        // relative repeat: each repetition adds the curve's rise
        Assert.Equal(1.0, FbxAnimation.EvaluateCurve(c[3], -1.0, 0.0));
        Assert.Equal(Bits("c00851ebba666640"), FbxAnimation.EvaluateCurve(c[3], -4.6, 0.0));
        Assert.Equal(5.0, FbxAnimation.EvaluateCurve(c[3], 3.0, 0.0));
        Assert.Equal(6.0, FbxAnimation.EvaluateCurve(c[3], 6.6, 0.0));
        Assert.Equal(0.0, FbxAnimation.EvaluateCurve(c[4], -1.0, 0.0)); Assert.Equal(2.0, FbxAnimation.EvaluateCurve(c[4], 3.0, 0.0));   // a repeat count of zero holds
        Assert.Equal(2.0, FbxAnimation.EvaluateCurve(c[6], -1.0, 0.0)); Assert.Equal(3.0, FbxAnimation.EvaluateCurve(c[6], 1.5, 0.0));   // a zero-length curve: no repetition
        Assert.Equal(0.0, FbxAnimation.EvaluateCurve(c[7], 0.5, 0.0)); Assert.Equal(0, c[7].Keyframes.Length);
        Assert.Equal(0.0, FbxAnimation.EvaluateCurve(null, 0.5, 0.0));
    }

    static FbxScene Layers()
    {
        var node = Model(1, "Anim", P("Lcl Translation", 1.0, 2.0, 3.0), P("Lcl Rotation", 10.0, 20.0, 30.0), P("Lcl Scaling", 2.0, 2.0, 2.0), P("RotationOrder", 2));
        var objects = new List<FbxNode> { node, Stack(60, "Take", 0.0, 2.0), Stack(66, "Reversed", 0.0, 2.0),
            Layer(61, "Base", P("BlendMode", 1)), Layer(62, "Add", P("BlendMode", 0), P("Weight", 50.0), P("RotationAccumulationMode", 0), P("ScaleAccumulationMode", 0)),
            Layer(63, "Pass", P("BlendMode", 2), P("Weight", 30.0), P("RotationAccumulationMode", 1), P("ScaleAccumulationMode", 1)),
            Layer(64, "Weighted", P("BlendMode", 2), P("Weight", 100.0)), Layer(65, "Odd", P("BlendMode", 7), P("Weight", 120.0)) };
        var conns = new List<FbxNode> { C(1, 0), C(61, 60), C(62, 60), C(63, 60), C(64, 60), C(65, 60), C(63, 66), C(62, 66), C(61, 66) };
        long id = 70;
        foreach (var (layer, scale) in new[] { (61L, 1.0), (62L, 2.0), (63L, 3.0), (64L, 4.0), (65L, 5.0) })
        {
            long t = id, r = id + 1, sc = id + 2;
            objects.AddRange(new[] { CurveNode(t, "T", 0, 0, 0), CurveNode(r, "R", 0, 0, 0), CurveNode(sc, "S", 1, 1, 1),
                Curve(t + 10, "TX", new[] { 0.0, 2.0 }, new[] { 0.0, scale }, new[] { Linear }, Zeros, new[] { 2 }), Curve(r + 10, "RY", new[] { 0.0, 2.0 }, new[] { 0.0, 45.0 * scale }, new[] { Linear }, Zeros, new[] { 2 }),
                Curve(sc + 10, "SZ", new[] { 0.0, 2.0 }, new[] { 1.0, scale }, new[] { Linear }, Zeros, new[] { 2 }) });
            conns.AddRange(new[] { C(t, layer), C(r, layer), C(sc, layer), OP(t, 1, "Lcl Translation"), OP(r, 1, "Lcl Rotation"), OP(sc, 1, "Lcl Scaling"), OP(t + 10, t, "d|X"), OP(r + 10, r, "d|Y"), OP(sc + 10, sc, "d|Z") });
            id += 20;
        }
        objects.AddRange(new[] { CurveNode(99, "W", 100.0), Curve(98, "Weight", new[] { 0.0, 2.0 }, new[] { 0.0, 100.0 }, new[] { Linear }, Zeros, new[] { 2 }) });
        conns.AddRange(new[] { C(99, 64), OP(99, 64, "Weight"), OP(98, 99, "d|X") });
        return Scene(objects.ToArray(), conns.ToArray());
    }

    [Fact]
    public void Layers_override_add_blend_and_weigh_as_ufbx_combines_them()
    {
        var s = Layers();
        var l = s.AnimLayers;
        Assert.Equal((false, false, 1.0), (l[0].Blended, l[0].Additive, l[0].Weight));
        Assert.Equal((true, true, 0.5, true, true), (l[1].Blended, l[1].Additive, l[1].Weight, l[1].ComposeRotation, l[1].ComposeScale));
        Assert.Equal((true, false, Bits("3fd3333333333333"), false, false), (l[2].Blended, l[2].Additive, l[2].Weight, l[2].ComposeRotation, l[2].ComposeScale));
        Assert.True(l[3].WeightIsAnimated && l[3].Weight == 1.0);
        Assert.Equal((false, false, 1.0), (l[4].Blended, l[4].Additive, l[4].Weight));   // an unknown blend mode overrides; 120% clamps to one
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, s.AnimStacks[0].Layers.Select(x => x.Index).ToArray());
        Assert.Equal(new[] { 2, 1, 0 }, s.AnimStacks[1].Layers.Select(x => x.Index).ToArray());
        var node = Node(s, "Anim");
        var t = FbxAnimation.EvaluateTransform(s.AnimStacks[0].Anim, node, 1.0);
        Assert.Equal((2.5, 0.0, 0.0), (t.tx, t.ty, t.tz));
        Assert.Equal((0.0, Bits("3fea9b66290ea1a3"), 0.0, Bits("3fe1c73b39ae68c9")), (t.qx, t.qy, t.qz, t.qw));   // rotations composed through quaternions, order YZX
        Assert.Equal((1.0, 1.0, 3.0), (t.sx, t.sy, t.sz));                                                            // scales composed through powers
        var r = FbxAnimation.EvaluateTransform(s.AnimStacks[1].Anim, node, 1.0);
        Assert.Equal((0.5, 0.0, 0.0), (r.tx, r.ty, r.tz));                                                            // the first layer of the stack assigns, whichever it is
        Assert.Equal((0.0, Bits("3fc8f8b83c69a60a"), 0.0, Bits("3fef6297cff75cb0")), (r.qx, r.qy, r.qz, r.qw));
        Assert.Equal((1.0, 1.0, 1.0), (r.sx, r.sy, r.sz));
        var start = FbxAnimation.EvaluateTransform(s.AnimStacks[0].Anim, node, 0.0);
        Assert.Equal((0.0, 0.0, 0.0, 1.0), (start.qx, start.qy, start.qz, start.qw));
    }

    static FbxScene Connected(bool template)
    {
        var objects = new List<FbxNode> { Model(1, "Driven", P("Lcl Rotation", 1.0, 1.0, 1.0)), Model(2, "Driver", P("Lcl Rotation", 5.0, 6.0, 7.0)),
            Model(3, "Cycle", P("Lcl Translation", 1.0, 1.0, 1.0)), Model(4, "Loop", P("Lcl Translation", 2.0, 2.0, 2.0)),
            Model(5, "Chain", P("Lcl Scaling", 1.0, 1.0, 1.0)), Model(6, "Link", P("Lcl Scaling", 2.0, 2.0, 2.0)), Model(7, "Source", P("Lcl Scaling", 3.0, 3.0, 3.0)),
            Model(8, "Missing"), Model(9, "Ordered", P("Lcl Rotation", 10.0, 20.0, 30.0)),
            Object("Material", 40, "Mat", "", N("ShadingModel", "phong")),
            Stack(60, "Take", 0.0, 2.0), Layer(61, "Base", P("BlendMode", 1)), Layer(62, "Add", P("BlendMode", 0), P("Weight", 50.0)) };
        var conns = new List<FbxNode>();
        for (long n = 1; n <= 9; n++) conns.Add(C(n, 0));
        conns.AddRange(new[] { C(61, 60), C(62, 60), PP(2, "Lcl Rotation", 1, "Lcl Rotation"), PP(4, "Lcl Translation", 3, "Lcl Translation"), PP(3, "Lcl Translation", 4, "Lcl Translation"),
            PP(6, "Lcl Scaling", 5, "Lcl Scaling"), PP(7, "Lcl Scaling", 6, "Lcl Scaling") });
        FbxNode Lin(long id, string name, double from, double to, int flag = Linear) => Curve(id, name, new[] { 0.0, 2.0 }, new[] { from, to }, new[] { flag }, Zeros, new[] { 2 });
        objects.AddRange(new[] { CurveNode(70, "R", 0, 0, 0), Lin(80, "RX", 0, 90), CurveNode(71, "T", 0, 0, 0), Lin(81, "TX", 0, 4), CurveNode(72, "S", 1, 1, 1), Lin(82, "SX", 1, 5),
            CurveNode(73, "S", 1, 1, 1), Lin(83, "SY", 1, 6), CurveNode(74, "V", 1), Lin(84, "VX", 1, 0), CurveNode(75, "O", 4), Lin(85, "OX", 4, 1, Const),
            CurveNode(76, "R", 0, 0, 0), Lin(86, "RZ", 0, 60), CurveNode(77, "R", 0, 0, 0), Lin(87, "RX", 0, 30), CurveNode(78, "D", 0.5, 0.5, 0.5), Lin(88, "DX", 0, 1), CurveNode(79, "R", 0, 0, 0), Lin(89, "RY", 0, 90) });
        conns.AddRange(new[] { C(70, 61), OP(70, 2, "Lcl Rotation"), OP(80, 70, "d|X"), C(71, 61), OP(71, 3, "Lcl Translation"), OP(81, 71, "d|X"),
            C(72, 61), OP(72, 7, "Lcl Scaling"), OP(82, 72, "d|X"), C(73, 61), OP(73, 8, "Lcl Scaling"), OP(83, 73, "d|Y"),
            C(74, 61), OP(74, 8, "Visibility"), OP(84, 74, "d|X"), C(75, 61), OP(75, 9, "RotationOrder"), OP(85, 75, "d|X"),
            C(76, 61), OP(76, 9, "Lcl Rotation"), OP(86, 76, "d|Z"), C(77, 62), OP(77, 9, "Lcl Rotation"), OP(87, 77, "d|X"),
            C(78, 61), OP(78, 40, "DiffuseColor"), OP(88, 78, "d|X"), C(79, 61), OP(79, 1, "Lcl Rotation"), OP(89, 79, "d|Y") });
        FbxNode definitions = template ? With(N("Definitions"), With(N("ObjectType", "Model"), With(N("PropertyTemplate", "FbxNode"),
            Props70(P("Lcl Scaling", 3.0, 3.0, 3.0), P("Visibility", 1.0), P("Lcl Translation", 0.0, 0.0, 0.0))))) : null;
        return Scene(objects.ToArray(), conns.ToArray(), null, definitions);
    }

    [Fact]
    public void Connected_properties_follow_their_source_or_fall_back_to_their_own_animation()
    {
        var s = Connected(false);
        var anim = s.Anim;
        var driven = FbxAnimation.EvaluateTransform(anim, Node(s, "Driven"), 1.0); var driver = FbxAnimation.EvaluateTransform(anim, Node(s, "Driver"), 1.0);
        Assert.Equal((Bits("3fd87de2a6aea963"), 0.0, 0.0, Bits("3fed906bcf328d46")), (driver.qx, driver.qy, driver.qz, driver.qw));
        Assert.Equal((driver.qx, driver.qy, driver.qz, driver.qw), (driven.qx, driven.qy, driven.qz, driven.qw));   // the connection wins over Driven's own curve
        Assert.True(Node(s, "Driven").Object.Props.Own("Lcl Rotation").Connected && Node(s, "Driven").Object.Props.Own("Lcl Rotation").Animated);
        var cycle = FbxAnimation.EvaluateTransform(anim, Node(s, "Cycle"), 1.0);
        Assert.Equal((2.0, 0.0, 0.0), (cycle.tx, cycle.ty, cycle.tz));                      // a cyclic connection leaves the node to its own animation
        var loop = FbxAnimation.EvaluateTransform(anim, Node(s, "Loop"), 1.0);
        Assert.Equal((2.0, 2.0, 2.0), (loop.tx, loop.ty, loop.tz));                         // and the unanimated partner to its stored value
        Assert.Equal((3.0, 1.0, 1.0), (FbxAnimation.EvaluateTransform(anim, Node(s, "Chain"), 1.0).sx, FbxAnimation.EvaluateTransform(anim, Node(s, "Chain"), 1.0).sy, FbxAnimation.EvaluateTransform(anim, Node(s, "Chain"), 1.0).sz));   // two hops
        var missing = Node(s, "Missing");
        Assert.False(missing.Visible);                                                      // a synthesised Visibility is zero without a template
        var ms = FbxAnimation.EvaluateTransform(anim, missing, 1.0);
        Assert.Equal((1.0, 3.5, 1.0), (ms.sx, ms.sy, ms.sz));                               // a synthesised Lcl Scaling starts at one, the value's defaults fill X and Z
        var ordered = FbxAnimation.EvaluateTransform(anim, Node(s, "Ordered"), 1.0);
        Assert.Equal((Bits("3fb02c356f25d086"), Bits("3f9155765be73050"), Bits("3fd0876979fe4724"), Bits("3feed7ec1d18232d")), (ordered.qx, ordered.qy, ordered.qz, ordered.qw));   // composed under the ANIMATED rotation order
        Assert.Equal(new[] { "Lcl Rotation", "Lcl Rotation", "Lcl Translation", "Lcl Scaling", "Lcl Scaling", "Visibility", "Lcl Rotation", "RotationOrder", "DiffuseColor" }, s.AnimLayers[0].AnimProps.Select(a => a.PropName).ToArray());
        var material = s.Objects.First(o => o.Class == "Material");
        Assert.Equal(0.75, FbxAnimation.EvaluateProp(anim, material, "DiffuseColor", 1.5).Reals[0]);   // any element's property evaluates
        Assert.True(FbxAnimation.EvaluateProp(anim, material, "Nothing", 1.5).NotFound);
        var withTemplate = Connected(true);
        Assert.True(Node(withTemplate, "Missing").Visible);                                 // the template's Visibility seeds the synthesised property
        var ts = FbxAnimation.EvaluateTransform(withTemplate.Anim, Node(withTemplate, "Missing"), 1.0);
        Assert.Equal((1.0, 3.5, 1.0), (ts.sx, ts.sy, ts.sz));
    }

    [Fact]
    public void Stacks_values_and_curves_in_odd_shapes()
    {
        var objects = new[] { Model(1, "Anim", P("Lcl Translation", 1.0, 2.0, 3.0)), Stack(60, "Reference", 0.5, 1.5, true), Stack(66, "Empty"), Stack(67, "NoLayers", 0.0, 1.0),
            Layer(61, "Twice"), Layer(62, "Bare"), CurveNode(70, "T", 1, 2, 3), CurveNode(71, "R"), CurveNode(72, "T2", 9, 9, 9),
            Curve(80, "Y", new[] { 0.0, 1.0 }, new[] { 0.0, 1.0 }, new[] { Linear }, Zeros, new[] { 2 }), Curve(81, "dY", new[] { 0.0, 1.0 }, new[] { 0.0, 2.0 }, new[] { Linear }, Zeros, new[] { 2 }),
            Object("AnimationCurve", 82, "W", "", N("KeyTime", new[] { 0, (int)Ticks(0.04) }), N("KeyValueFloat", new[] { 0f, 3f }), N("KeyAttrFlags", new[] { Linear }), N("KeyAttrDataFloat", new double[] { 0, 0, 0, 0 }), N("KeyAttrRefCount", new[] { 2 })) };
        var conns = new[] { C(1, 0), C(61, 60), C(61, 60), C(62, 66), C(61, 66), C(70, 61), C(70, 61), C(71, 61), C(72, 61),
            OP(70, 1, "Lcl Translation"), OP(71, 1, "Lcl Rotation"), OP(72, 1, "Lcl Translation"), OP(80, 70, "Y"), OP(81, 70, "d|Y"), OP(82, 70, "d|W") };
        var s = Scene(objects, conns);
        Assert.Equal((0.5, 1.5), (s.AnimStacks[0].TimeBegin, s.AnimStacks[0].TimeEnd));      // ReferenceStart/Stop when Local ones are absent
        Assert.Equal((0.0, 0.0), (s.AnimStacks[1].TimeBegin, s.AnimStacks[1].TimeEnd));
        Assert.Equal(new[] { 0 }, s.AnimStacks[0].Layers.Select(l => l.Index).ToArray());    // the duplicate connection is dropped
        Assert.Equal(new[] { 1, 0 }, s.AnimStacks[1].Layers.Select(l => l.Index).ToArray());
        Assert.Empty(s.AnimStacks[2].Layers);
        Assert.Equal(2, s.Warnings.Count(w => w.StartsWith("Duplicate connection")));
        Assert.Equal(new[] { 0, 1, 2 }, s.AnimLayers[0].Values.Select(v => v.Index).ToArray());
        Assert.Equal(new[] { (1, "Lcl Rotation", 1), (1, "Lcl Translation", 0), (1, "Lcl Translation", 2) }, s.AnimLayers[0].AnimProps.Select(a => (a.Element.ElementId, a.PropName, a.Value.Index)).ToArray());
        var t = s.AnimValues[0];
        Assert.Equal((2, 1, -1), (t.Curves[0].Index, t.Curves[1].Index, t.Curves[2] != null ? t.Curves[2].Index : -1));   // "d|W" lands on X; "d|Y" sorts after "Y" and wins
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, t.DefaultValue);
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, s.AnimValues[1].DefaultValue);
        Assert.Equal(Bits("3fa47ae147ae147b"), s.AnimCurves[2].MaxTime);                      // 32-bit ticks, float values, double attributes narrowed
        Assert.Equal(3.0, s.AnimCurves[2].MaxValue);
        var at0 = FbxAnimation.EvaluateTransform(s.AnimStacks[0].Anim, Node(s, "Anim"), 0.0);
        Assert.Equal((0.0, 0.0, 3.0), (at0.tx, at0.ty, at0.tz));                              // the FIRST value connected to the property drives it
    }

    [Fact]
    public void The_header_s_time_code_sets_the_ktime_base()
    {
        FbxNode Header(int tc) => With(N("FBXHeaderExtension"), N("FBXHeaderVersion", 1004), N("FBXVersion", 7400), With(N("OtherFlags"), N("TCDefinition", tc)));
        FbxNode[] Objects() => new[] { Model(1, "Anim", P("Lcl Translation", 1.0, 2.0, 3.0)), Stack(60, "Take", 0.0, 2.0), Layer(61, "Base"), CurveNode(70, "T", 1, 2, 3),
            Curve(80, "TX", new[] { 0.0, 1.0, 2.0 }, new[] { 0.0, 1.0, 3.0 }, new[] { Auto }, Zeros, new[] { 3 }, ('R', -1), ('R', -1)) };
        var conns = new[] { C(1, 0), C(61, 60), C(70, 61), OP(70, 1, "Lcl Translation"), OP(80, 70, "d|X") };
        var fresh = Scene(Objects(), conns, Header(128));
        Assert.Equal(141120000L, fresh.KTimeSecond);
        Assert.Equal(Bits("408474869e20bd7a"), fresh.AnimStacks[0].TimeEnd);                 // the same ticks, a longer second
        Assert.Equal(Bits("3fe0e5607416060e"), FbxAnimation.EvaluateCurve(fresh.AnimCurves[0], Bits("40a0e02242749c51"), 0.0));   // repeats computed in those ticks
        var old = Scene(Objects(), conns, Header(127));
        Assert.Equal(46186158000L, old.KTimeSecond);
        Assert.Equal(2.0, old.AnimStacks[0].TimeEnd);
        Assert.Equal(46186158000L, Scene(Objects(), conns).KTimeSecond);
    }

    [Fact]
    public void Tcb_weighted_user_and_biased_auto_tangents_as_ufbx_solves_them()
    {
        var tcb = new float[] { 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0.5f, -0.5f, 0, 0, -0.3f, 0.8f, 0, 0, 0, 0, 0 };
        double[] times = { 0.0, 0.5, 1.0, 1.5, 2.0 }, values = { 0.0, 2.0, 1.0, 3.0, 0.0 };
        var objects = new[] { Model(1, "Anim", P("Lcl Translation", 1.0, 2.0, 3.0)), Stack(60, "Take", 0.0, 2.0), Layer(61, "Base"), CurveNode(70, "T", 0, 0, 0),
            Curve(80, "TX", times, values, new[] { Tcb, Tcb, Tcb, Tcb, Tcb }, tcb, new[] { 1, 1, 1, 1, 1 }),
            Curve(81, "TY", times, values, new[] { Auto | TimeIndependent, Auto | ClampProgressive, Auto, Auto, Auto | Clamp }, Zeros.Concat(Zeros).Concat(new float[] { 650, 0, 0, 0 }).Concat(new float[] { -200, 0, 0, 0 }).Concat(Zeros).ToArray(), new[] { 1, 1, 1, 1, 1 }),
            Curve(82, "TZ", times, values, new[] { User | WeightedRight, User | WeightedNextLeft, User | Broken, User | WeightedRight | WeightedNextLeft, User },
                new[] { 1f, 0.5f, Packed(0.25, 0.75), 0f, -1f, 2f, Packed(0.1, 0.6), 0f, 3f, -3f, 0f, 0f, 0.5f, 0.5f, Packed(0.9, 0.05), 0f, 1f, 1f, 0f, 0f }, new[] { 1, 1, 1, 1, 1 }) };
        var s = Scene(objects, new[] { C(1, 0), C(61, 60), C(70, 61), OP(70, 1, "Lcl Translation"), OP(80, 70, "d|X"), OP(81, 70, "d|Y"), OP(82, 70, "d|Z") });
        var tx = s.AnimCurves[0];   // tension, continuity, bias
        Assert.Equal("3e2aaa9f,3daaaa9f 3e2aaa9f,3daaaa9f", Tangents(tx.Keyframes[1]));
        Assert.Equal("3e2aaa9f,3f355549 3e2aaa9f,3dffffee", Tangents(tx.Keyframes[2]));
        Assert.Equal("3e2aaa9f,3f35c284 3e2aaa9f,3e947ad7", Tangents(tx.Keyframes[3]));
        Assert.Equal("3e2aaa9f,bf7fffee 00000000,00000000", Tangents(tx.Keyframes[4]));
        var ty = s.AnimCurves[1];   // time-independent, clamp-progressive (flat), a bias past 500, a negative bias, clamp
        Assert.Equal("00000000,00000000 3e2aaa9f,3f2aaa9f", Tangents(ty.Keyframes[0]));
        Assert.Equal("3e2aaa9f,00000000 3e2aaa9f,00000000", Tangents(ty.Keyframes[1]));
        Assert.Equal("3e2aaa9f,3e2aaa9f 3e2aaa9f,4193554b", Tangents(ty.Keyframes[2]));
        Assert.Equal("3e2aaa9f,be2aaa9f 3e2aaa9f,3fbffff3", Tangents(ty.Keyframes[3]));
        var tz = s.AnimCurves[2];   // the packed decimal weights scale dx, the stored slopes give dy
        Assert.Equal("00000000,00000000 3e000000,3e000000", Tangents(tz.Keyframes[0]));
        Assert.Equal("3e2aaa9f,3daaaa9f 3e2aaa9f,be2aaa9f", Tangents(tz.Keyframes[1]));
        Assert.Equal("3e999999,3f199999 3e2aaa9f,3effffee", Tangents(tz.Keyframes[2]));
        Assert.Equal("3e2aaa9f,beffffee 3ee66666,3e666666", Tangents(tz.Keyframes[3]));
        Assert.Equal("3ccccccc,3c4ccccc 00000000,00000000", Tangents(tz.Keyframes[4]));
    }

    [Fact]
    public void Equal_and_unsorted_key_times_zero_the_tangents_between_them()
    {
        var objects = new[] { Model(1, "Anim"), Stack(60, "Take", 0.0, 2.0), Layer(61, "Base"), CurveNode(70, "R", 0, 0, 0),
            Curve(80, "RX", new[] { 0.0, 1.0, 1.0, 2.0 }, new[] { 0.0, 10.0, 20.0, 30.0 }, new[] { Auto }, Zeros, new[] { 4 }),
            Curve(81, "RY", new[] { 0.0, 2.0, 1.0, 3.0 }, new[] { 0.0, 10.0, 20.0, 30.0 }, new[] { Linear }, Zeros, new[] { 4 }) };
        var s = Scene(objects, new[] { C(1, 0), C(61, 60), C(70, 61), OP(70, 1, "Lcl Rotation"), OP(80, 70, "d|X"), OP(81, 70, "d|Y") });
        var rx = s.AnimCurves[0];
        Assert.Equal("3eaaaa9f,40555547 00000000,00000000", Tangents(rx.Keyframes[1]));
        Assert.Equal("00000000,00000000 3eaaaa9f,40555547", Tangents(rx.Keyframes[2]));
        Assert.Equal(20.0, FbxAnimation.EvaluateCurve(rx, 1.0, 0.0));   // the LAST of the equal keys is "exactly on a key": the scan stops at the first later key
        var ry = s.AnimCurves[1];
        Assert.Equal("3f2aaa9f,40555547 00000000,00000000", Tangents(ry.Keyframes[1]));   // a next key earlier in time: no right tangent
        Assert.Equal(3.0, ry.MaxTime); Assert.Equal(30.0, ry.MaxValue);
    }

    [Fact]
    public void Malformed_curves_are_refused()
    {
        FbxScene Read(FbxNode curve) => Scene(new[] { curve }, new FbxNode[0]);
        Assert.Throws<InvalidDataException>(() => Read(Curve(80, "C", new[] { 0.0, 1.0, 2.0 }, new[] { 0.0, 1.0 }, new[] { Linear }, Zeros, new[] { 3 })));
        Assert.Throws<InvalidDataException>(() => Read(Curve(80, "C", new[] { 0.0, 1.0 }, new[] { 0.0, 1.0 }, new[] { Linear }, new float[] { 0, 0, 0 }, new[] { 2 })));
        Assert.Throws<InvalidDataException>(() => Read(Curve(80, "C", new[] { 0.0, 1.0, 2.0 }, new[] { 0.0, 1.0, 2.0 }, new[] { Linear }, Zeros, new[] { 2 })));
        Assert.Throws<InvalidDataException>(() => Read(Curve(80, "C", new[] { 0.0, 1.0 }, new[] { 0.0, 1.0 }, new[] { Linear, Linear }, Zeros.Concat(Zeros).ToArray(), new[] { 0, 2 })));
        Assert.Throws<InvalidDataException>(() => Read(Object("AnimationCurve", 80, "C", "", N("KeyTime", new long[] { 0 }), N("KeyValueFloat", new[] { 1.0 }), N("KeyAttrFlags", new[] { Linear }), N("KeyAttrDataFloat", Zeros))));
        Assert.NotNull(Read(Curve(80, "C", new double[0], new double[0], new int[0], new float[0], new int[0])));
    }

    [Fact]
    public void Name_keys_slerp_and_euler_round_trips_are_ufbx_s()
    {
        Assert.Equal((uint)('L' << 24 | 'c' << 16 | 'l' << 8 | ' '), FbxAnimation.NameKey("Lcl Rotation"));
        Assert.Equal((uint)('a' << 24 | 'b' << 16), FbxAnimation.NameKey("ab"));
        Assert.Equal(0u, FbxAnimation.NameKey(""));
        Assert.Equal(0xC3u << 24 | 0xA9u << 16, FbxAnimation.NameKey("é"));   // UTF-8 bytes, not UTF-16 units
        var q = FbxMath.EulerToQuat(new[] { 10.0, 20.0, 30.0 }, 0);
        var full = FbxAnimation.Slerp(new[] { 0.0, 0.0, 0.0, 1.0 }, q, 1.0);
        for (int i = 0; i < 4; i++) Assert.Equal(q[i], full[i], 12);   // renormalised, an ulp or two off the input as in ufbx
        var none = FbxAnimation.Slerp(new[] { 0.0, 0.0, 0.0, 1.0 }, q, 0.0);
        Assert.Equal((0.0, 0.0, 0.0), (none[0], none[1], none[2])); Assert.Equal(1.0, none[3], 12);
        for (int order = 0; order < 6; order++)
        {
            var e = FbxAnimation.QuatToEuler(FbxMath.EulerToQuat(new[] { 10.0, 20.0, 30.0 }, order), order);
            Assert.Equal(10.0, e[0], 9); Assert.Equal(20.0, e[1], 9); Assert.Equal(30.0, e[2], 9);
        }
        var gimbal = FbxAnimation.QuatToEuler(FbxMath.EulerToQuat(new[] { 0.0, 90.0, 0.0 }, 0), 0);
        Assert.Equal(90.0, gimbal[1], 9); Assert.Equal(0.0, gimbal[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_template_missing_a_synthetic_property_keeps_zero_defaults_without_NoValue(bool connected)
    {
        var definitions = With(N("Definitions"), With(N("ObjectType", "Model"), With(N("PropertyTemplate", "FbxNode"), Props70(P("Other", 9.0)))));
        var objects = new[] { Model(1, "Driven"), Model(2, "Driver", P("Lcl Scaling", 2.0, 3.0, 4.0)),
            Stack(60, "Take", 0.0, 1.0), Layer(61, "Base"), CurveNode(70, "S", 2, 3, 4) };
        var edge = connected ? PP(2, "Lcl Scaling", 1, "Lcl Scaling") : OP(70, 1, "Lcl Scaling");
        var s = Scene(objects, new[] { C(1, 0), C(2, 0), C(61, 60), C(70, 61), edge }, null, definitions);
        var node = Node(s, "Driven"); var prop = node.Object.Props.Own("Lcl Scaling");
        Assert.True(prop.Synthetic);
        Assert.False(prop.NoValue);
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, prop.Reals.Take(3));
        Assert.Equal((0.0, 0.0, 0.0), (node.LocalTransform.sx, node.LocalTransform.sy, node.LocalTransform.sz));
        var t = FbxAnimation.EvaluateTransform(s.Anim, node, 0.5);
        Assert.Equal((2.0, 3.0, 4.0), (t.sx, t.sy, t.sz));
    }

    [Fact]
    public void A_property_connection_from_the_synthetic_root_is_resolved()
    {
        var s = Scene(new[] { Model(1, "Driven", P("Lcl Translation", 1.0, 2.0, 3.0)), Stack(60, "Take", 0.0, 1.0), Layer(61, "Base") },
            new[] { C(1, 0), C(61, 60), PP(0, "Nothing", 1, "Lcl Translation") });
        var node = Node(s, "Driven");
        Assert.True(node.Object.Props.Own("Lcl Translation").Connected);
        var t = FbxAnimation.EvaluateTransform(s.Anim, node, 0.5);
        Assert.Equal((0.0, 0.0, 0.0), (t.tx, t.ty, t.tz)); // ufbx evaluates the root's missing property to zero
    }

    [Fact]
    public void An_indirect_rotation_order_evaluation_cycle_is_a_named_refusal()
    {
        // This is not a direct connection cycle: RotationOrder is driven by Lcl Rotation, whose additive
        // composition evaluates RotationOrder again. The unguarded evaluator exhausted the runtime stack.
        var s = Scene(new[] { Model(1, "Node", P("Lcl Rotation", 0.0, 0.0, 0.0), P("RotationOrder", 0)),
            Stack(60, "Take", 0.0, 1.0), Layer(61, "Base", P("BlendMode", 1)), Layer(62, "Add", P("BlendMode", 0), P("Weight", 50.0)),
            CurveNode(70, "R", 30, 0, 0), CurveNode(71, "R", 20, 0, 0) },
            new[] { C(1, 0), C(61, 60), C(62, 60), C(70, 61), C(71, 62), OP(70, 1, "Lcl Rotation"), OP(71, 1, "Lcl Rotation"), PP(1, "Lcl Rotation", 1, "RotationOrder") });
        Assert.Contains("Cyclic animation property", Assert.Throws<InvalidDataException>(() => FbxAnimation.EvaluateTransform(s.Anim, Node(s, "Node"), 0.5)).Message);
        Assert.Contains("Cyclic animation property", Assert.Throws<InvalidDataException>(() => FbxAnimation.EvaluateProp(s.Anim, Node(s, "Node").Object, "Lcl Rotation", 0.5)).Message);
    }

    [Fact]
    public void Deep_acyclic_rotation_dependencies_are_bounded_before_stack_exhaustion()
    {
        var objects = new List<FbxNode> { Stack(1000, "Take", 0.0, 1.0), Layer(1001, "Base", P("BlendMode", 1)),
            Layer(1002, "Add", P("BlendMode", 0)), CurveNode(1003, "BaseR", 10, 0, 0), CurveNode(1004, "AddR", 20, 0, 0) };
        var edges = new List<FbxNode> { C(1001, 1000), C(1002, 1000), C(1003, 1001), C(1004, 1002) };
        for (int i = 1; i <= 70; i++)
        {
            objects.Add(Model(i, "Node" + i, P("Lcl Rotation", 0.0, 0.0, 0.0), P("RotationOrder", 0)));
            edges.Add(C(i, 0)); edges.Add(OP(1003, i, "Lcl Rotation")); edges.Add(OP(1004, i, "Lcl Rotation"));
            if (i < 70) edges.Add(PP(i + 1, "Lcl Rotation", i, "RotationOrder"));
        }
        var s = Scene(objects.ToArray(), edges.ToArray());
        Assert.Contains("128 dependent properties", Assert.Throws<InvalidDataException>(() => FbxAnimation.EvaluateTransform(s.Anim, Node(s, "Node1"), 0.5)).Message);
    }
}
