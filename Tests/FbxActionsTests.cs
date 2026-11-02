using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

// Sub-step 6d of the native FBX reader: editor/FbxActions.cs (with FbxImport, FbxArmature and FbxModel) on documents built in
// memory in the shapes of tools/fbx-drill/fbx_animation_fixtures.py. The expected values are Blender 5.1's own (its FBX
// import + glTF export of those scenes read back through GlbReader, and its mathutils and NumPy measured, 2026-11-02); the
// bit-level claim on every file is tools/fbx_model_drill.sh's.
public class FbxActionsTests
{
    // ---- builders -------------------------------------------------------------------------------------------------
    const long KtimeV7 = 46186158000;
    static FbxNode N(string name, params object[] props) { var n = new FbxNode { Name = name }; n.Properties.AddRange(props); return n; }
    static FbxNode With(FbxNode n, params FbxNode[] children) { n.Children.AddRange(children); return n; }
    static FbxNode P(string name, string kind, string sub, params object[] values) { var p = N("P", name, kind, sub, ""); p.Properties.AddRange(values); return p; }
    static FbxNode V(string name, double x, double y, double z) => P(name, "Vector3D", "Vector", x, y, z);
    static FbxNode Props70(params FbxNode[] ps) => With(N("Properties70"), ps);
    static FbxNode Object(string cls, long id, string name, string sub, params FbxNode[] children) => With(N(cls, id, name + "\0\u0001" + cls, sub), children);
    static FbxNode Anim(string cls, long id, string name, string blenderClass, params FbxNode[] children) => With(N(cls, id, name + "\0\u0001" + blenderClass, ""), children);
    static FbxNode C(long src, long dst) => N("C", "OO", src, dst);
    static FbxNode OP(long src, long dst, string prop) => N("C", "OP", src, dst, prop);
    static FbxNode Model(long id, string name, params FbxNode[] props) => props.Length > 0 ? Object("Model", id, name, "Mesh", Props70(props)) : Object("Model", id, name, "Mesh");
    static FbxNode Null(long id, string name, params FbxNode[] props) => props.Length > 0 ? Object("Model", id, name, "Null", Props70(props)) : Object("Model", id, name, "Null");
    static FbxNode Limb(long id, string name, params FbxNode[] props) => props.Length > 0 ? Object("Model", id, name, "LimbNode", Props70(props)) : Object("Model", id, name, "LimbNode");
    static FbxNode Material(long id, string name) => Object("Material", id, name, "", N("ShadingModel", "phong"));
    static FbxNode Skin(long id) => Object("Deformer", id, "Skin", "Skin", N("Version", 101));
    static double[] Mat16(double[][] rows) { var m = new double[16]; for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++) m[c * 4 + r] = rows[r][c]; return m; }
    static double[][] Translation(double x, double y, double z) => new[] { new[] { 1.0, 0, 0, x }, new[] { 0, 1.0, 0, y }, new[] { 0, 0, 1.0, z }, new[] { 0, 0, 0, 1.0 } };
    static double[][] RotZ(double deg, double x, double y, double z)
    {
        double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return new[] { new[] { c, -s, 0, x }, new[] { s, c, 0, y }, new[] { 0, 0, 1.0, z }, new[] { 0, 0, 0, 1.0 } };
    }
    static FbxNode Cluster(long id, string name, int[] indexes, double[] weights, double[][] transform, double[][] link)
        => Object("Deformer", id, name, "Cluster", N("Version", 100), N("Indexes", indexes), N("Weights", weights), N("Transform", Mat16(transform)), N("TransformLink", Mat16(link)));
    static FbxNode BindPose(long id, params (long node, double[][] m)[] nodes)
    {
        var children = new List<FbxNode> { N("Type", "BindPose"), N("Version", 100) };
        foreach (var (node, m) in nodes) children.Add(With(N("PoseNode"), N("Node", node), N("Matrix", Mat16(m))));
        return Object("Pose", id, "BindPose", "BindPose", children.ToArray());
    }
    static FbxNode Geometry(long id, string name, double[] vertices, int[][] faces)
    {
        var idx = new List<int>();
        foreach (var f in faces) { idx.AddRange(f.Take(f.Length - 1)); idx.Add(~f[f.Length - 1]); }
        return Object("Geometry", id, name, "Mesh", N("Vertices", vertices), N("PolygonVertexIndex", idx.ToArray()), N("GeometryVersion", 124), With(N("Layer", 0), N("Version", 100)));
    }
    static FbxNode Stack(long id, string name, string cls = "AnimStack") => Anim("AnimationStack", id, name, cls);
    static FbxNode Layer(long id, string name) => Anim("AnimationLayer", id, name, "AnimLayer");
    static FbxNode CurveNode(long id, string name, double x = 0, double y = 0, double z = 0) => Anim("AnimationCurveNode", id, name, "AnimCurveNode", Props70(P("d|X", "Number", "", x), P("d|Y", "Number", "", y), P("d|Z", "Number", "", z)));
    static FbxNode Curve(long id, long[] times, float[] values) => Anim("AnimationCurve", id, "", "AnimCurve", N("Default", 0.0), N("KeyVer", 4008), N("KeyTime", times), N("KeyValueFloat", values), N("KeyAttrFlags", new[] { 24840 }), N("KeyAttrDataFloat", new[] { 0f, 0f, 0f, 0f }), N("KeyAttrRefCount", new[] { times.Length }));
    static long[] Frames(double fps, params double[] fr) => fr.Select(f => (long)Math.Round(f * KtimeV7 / fps)).ToArray();
    /// <summary>One stack with one layer; curves and curve nodes numbered from `next`.</summary>
    sealed class Rig
    {
        public readonly List<FbxNode> Objects = new List<FbxNode>(); public readonly List<FbxNode> Conns = new List<FbxNode>();
        readonly long layer; long next;
        public Rig(long stackId, long layerId, long first, string stackName = "Take 001", string layerName = null, string cls = "AnimStack")
        { Objects.Add(Stack(stackId, stackName, cls)); Objects.Add(Layer(layerId, layerName ?? stackName)); Conns.Add(C(layerId, stackId)); layer = layerId; next = first; }
        public long Animate(long model, string prop, string channels, long[] times, float[][] values, double dx = 0, double dy = 0, double dz = 0)
        {
            long node = next++;
            Objects.Add(CurveNode(node, prop.Replace("Lcl ", ""), dx, dy, dz)); Conns.Add(OP(node, model, prop)); Conns.Add(C(node, layer));
            for (int i = 0; i < channels.Length; i++) { long cid = next++; Objects.Add(Curve(cid, times, values[i])); Conns.Add(OP(cid, node, "d|" + channels[i])); }
            return node;
        }
    }
    static List<FbxNode> Header(long timeMode = 11, double customFps = 24.0, long headerVersion = 1003, long? tcDefinition = null, long version = 7400)
    {
        var settings = Props70(P("UpAxis", "int", "Integer", 1), P("UpAxisSign", "int", "Integer", 1), P("FrontAxis", "int", "Integer", 2), P("FrontAxisSign", "int", "Integer", 1),
            P("CoordAxis", "int", "Integer", 0), P("CoordAxisSign", "int", "Integer", 1), P("UnitScaleFactor", "double", "Number", 1.0), P("TimeMode", "enum", "", timeMode), P("CustomFrameRate", "double", "Number", customFps));
        var template = With(N("PropertyTemplate", "FbxNode"), Props70(P("Lcl Translation", "Lcl Translation", "", 0.0, 0.0, 0.0), P("Lcl Rotation", "Lcl Rotation", "", 0.0, 0.0, 0.0),
            P("Lcl Scaling", "Lcl Scaling", "", 1.0, 1.0, 1.0), P("RotationActive", "bool", "", 0), P("RotationOrder", "enum", "", 0)));
        var header = With(N("FBXHeaderExtension"), N("FBXHeaderVersion", (int)headerVersion), N("FBXVersion", (int)version), N("Creator", "tests"));
        if (tcDefinition.HasValue) header.Children.Add(With(N("OtherFlags"), N("TCDefinition", (int)tcDefinition.Value)));
        return new List<FbxNode> { header, With(N("GlobalSettings"), N("Version", 1000), settings), With(N("Definitions"), With(N("ObjectType", "Model"), template)) };
    }
    static FbxImport.Scene Scene(IEnumerable<FbxNode> objects, IEnumerable<FbxNode> connections, List<FbxNode> head = null, long version = 7400)
    {
        var d = new FbxDocument { Version = (int)version };
        d.Nodes.AddRange(head ?? Header());
        d.Nodes.Add(With(N("Objects"), objects.ToArray())); d.Nodes.Add(With(N("Connections"), connections.ToArray()));
        return FbxImport.Build(d);
    }
    static HafModel Build(IEnumerable<FbxNode> objects, IEnumerable<FbxNode> connections, out string why, List<FbxNode> head = null, long version = 7400) => FbxModel.Build(Scene(objects, connections, head, version), out why);
    static HafModel Build(IEnumerable<FbxNode> objects, IEnumerable<FbxNode> connections) { var m = Build(objects, connections, out string why); Assert.Null(why); return m; }
    static string FB(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");
    static string DB(double d) => BitConverter.ToUInt64(BitConverter.GetBytes(d), 0).ToString("x16");
    /// <summary>A C99 hex float such as "-0x1.132ef8p+1" (Python's float.hex()) as a double.</summary>
    static double Hex(string s)
    {
        bool neg = s.StartsWith("-"); if (neg || s.StartsWith("+")) s = s.Substring(1);
        s = s.Substring(2); int p = s.IndexOf('p'); string mant = s.Substring(0, p); int exp = int.Parse(s.Substring(p + 1));
        int dot = mant.IndexOf('.'); string ip = dot < 0 ? mant : mant.Substring(0, dot), fp = dot < 0 ? "" : mant.Substring(dot + 1);
        double v = Convert.ToInt64(ip, 16);
        double scale = 1.0 / 16; foreach (char c in fp) { v += Convert.ToInt32(c.ToString(), 16) * scale; scale /= 16; }
        v *= Math.Pow(2, exp);
        return neg ? -v : v;
    }
    static string FBH(string hexFloat) => FB((float)Hex(hexFloat));
    static IEnumerable<string> FBs(IEnumerable<float> fs) => fs.Select(FB);
    static HafChannel Ch(HafAnimation a, string node, string path, HafModel m) => a.Channels.First(c => m.Nodes[c.Node].Name == node && c.Path == path);
    static HafSampler Sp(HafAnimation a, HafChannel c) => a.Samplers[c.Sampler];
    static readonly double[] TriV = { 0, 0, 0, 1, 0, 0, 0, 1, 0 }; static readonly int[][] TriF = { new[] { 0, 1, 2 } };

    /// <summary>The anim_objects fixture: a mover keyed on every property (curves of different times combined), a spinner past
    /// 180 degrees, a constant channel, a single key, two empties driven by one curve node.</summary>
    static (List<FbxNode> objects, List<FbxNode> conns) Objects()
    {
        var rig = new Rig(200, 201, 300);
        rig.Animate(1, "Lcl Translation", "XYZ", Frames(24, 0, 24, 48), new[] { new[] { 0f, 1f, 2.5f }, new[] { 0f, 0.5f, -1f }, new[] { 0f, 2f, 0.25f } });
        rig.Animate(1, "Lcl Rotation", "Z", Frames(24, 0, 12, 48), new[] { new[] { 0f, 30f, 75f } }, 10, 20, 30);
        rig.Animate(1, "Lcl Scaling", "X", Frames(24, 24, 48), new[] { new[] { 2f, 3f } }, 1, 1, 1);   // the static 1 before the first key
        rig.Animate(3, "Lcl Rotation", "Y", Frames(24, 0, 10, 20, 30), new[] { new[] { 0f, 170f, 340f, 350f } });
        rig.Animate(4, "Lcl Translation", "Z", Frames(24, 0, 20), new[] { new[] { 3f, 3f } });
        rig.Animate(5, "Lcl Rotation", "X", Frames(24, 7), new[] { new[] { 45f } });
        long node = rig.Animate(6, "Lcl Translation", "Y", Frames(24, 0, 12), new[] { new[] { 0f, 2f } });
        rig.Conns.Add(OP(node, 7, "Lcl Translation"));
        var objects = new List<FbxNode> { Model(1, "Mover", V("Lcl Rotation", 10, 20, 30)), Geometry(30, "MoverGeo", TriV, TriF), Model(2, "Rider", V("Lcl Translation", 0.5, 0, 0)), Geometry(31, "RiderGeo", TriV, TriF),
            Null(3, "Spinner"), Null(4, "Still"), Null(5, "OneKey"), Null(6, "Twin"), Null(7, "Twin2"), Material(100, "Mat") };
        objects.AddRange(rig.Objects);
        var conns = new List<FbxNode> { C(1, 0), C(2, 1), C(30, 1), C(31, 2), C(100, 1), C(100, 2), C(3, 0), C(4, 0), C(5, 0), C(6, 0), C(7, 0) };
        conns.AddRange(rig.Conns);
        return (objects, conns);
    }

    [Fact]
    public void Curves_become_one_action_per_object_and_stack_sampled_as_the_exporter_samples_them()
    {
        var (objects, conns) = Objects();
        var s = Scene(objects, conns); Assert.Null(s.Fallback);
        Assert.Equal(new[] { "Mover|Take 001", "Spinner|Take 001", "Still|Take 001", "OneKey|Take 001", "Twin|Take 001", "Twin2|Take 001" }, s.Actions.Select(a => a.Name));
        Assert.All(s.Actions, a => Assert.Same(a, a.IdData.ActiveAction));
        var mover = s.Actions[0];
        Assert.Equal(1f, mover.FrameStart); Assert.Equal(49f, mover.FrameEnd);   // frames 0..48 of the file, plus the offset of one
        Assert.Equal(new[] { 1f, 13f, 25f, 49f }, mover.Object.Loc[0].Frames);   // the rotation's key at frame 12 joined the translation's
        Assert.Equal(new[] { 8f, 9f }, s.Actions[3].FrameStart == 8f ? new[] { s.Actions[3].FrameStart, s.Actions[3].FrameEnd } : new[] { 0f, 0f });   // a single key: stretched by one
        var m = FbxModel.Build(s, out string why); Assert.Null(why);
        Assert.Equal(new[] { "Mover|Take 001", "Spinner|Take 001", "Still|Take 001", "OneKey|Take 001", "Twin|Take 001", "Twin2|Take 001" }, m.Animations.Select(a => a.Name));
        // the mover: every channel animated, sampled at the 49 whole frames (the single key found again from the curves)
        var am = m.Animations[0];
        Assert.Equal(new[] { "translation", "rotation", "scale" }, am.Channels.Select(c => c.Path));
        Assert.All(am.Channels, c => Assert.Equal(49, Sp(am, c).KeyCount));
        Assert.All(am.Channels, c => Assert.Equal("LINEAR", Sp(am, c).Interpolation));
        Assert.Equal(new[] { "3d2aaaab", "3daaaaab", "3e000000", "3e2aaaab" }, FBs(Sp(am, am.Channels[0]).Times.Take(4)));   // frame / 24
        Assert.Equal(new[] { "00000000", "00000000", "80000000", "39da740d", "395a740d", "3a5a740d" }, FBs(Sp(am, am.Channels[0]).Values.Take(6)));   // Y up: (x, z, -y) in metres
        Assert.Equal(new[] { "3f4120c9", "3de49873", "be0836fa", "3f220dbf" }, FBs(Sp(am, am.Channels[1]).Values.Take(4)));
        Assert.Equal(new[] { "3c23d709", "3c23d70a", "3c23d709" }, FBs(Sp(am, am.Channels[2]).Values.Take(3)));
        // the spinner: a rotation past 180 degrees stays continuous (the compatible Euler), its constant channels keep two samples
        var sp = m.Animations[1];
        Assert.Equal(31, Sp(sp, Ch(sp, "Spinner", "rotation", m)).KeyCount); Assert.Equal("STEP", Sp(sp, Ch(sp, "Spinner", "translation", m)).Interpolation);
        Assert.Equal(2, Sp(sp, Ch(sp, "Spinner", "scale", m)).KeyCount);
        Assert.Equal(new[] { "3f3307f0", "3dd60d06", "bdd60d0a", "3f3307ef" }, FBs(Sp(sp, Ch(sp, "Spinner", "rotation", m)).Values.Skip(4).Take(4)));
        // a constant keyed channel: its first and last samples, STEP (two equal samples)
        var st = m.Animations[2];
        Assert.Equal(new[] { "00000000", "00000000", "3cf5c28f", "00000000", "00000000", "3cf5c28f" }, FBs(Sp(st, Ch(st, "Still", "translation", m)).Values));
        Assert.Equal("STEP", Sp(st, Ch(st, "Still", "translation", m)).Interpolation);
        // a single key: one sample at frame 8, LINEAR (an animated channel)
        var ok = m.Animations[3];
        Assert.Equal(new[] { "3eaaaaab" }, FBs(Sp(ok, ok.Channels[0]).Times));
        Assert.Equal(new[] { "3f6c835f", "00000000", "80000000", "3ec3ef15" }, FBs(Sp(ok, Ch(ok, "OneKey", "rotation", m)).Values));
        // one curve node on two models: two actions alike
        Assert.Equal(FBs(Sp(m.Animations[4], m.Animations[4].Channels[0]).Values), FBs(Sp(m.Animations[5], m.Animations[5].Channels[0]).Values));
        Assert.Equal(13, Sp(m.Animations[4], m.Animations[4].Channels[0]).KeyCount);
        // the static nodes are the scene at frame 0: the mover's first key, with the Y-up turn
        Assert.Equal("3fe8241920000000", DB(m.Nodes[1].Rotation[0]));
    }

    [Fact]
    public void Frame_rate_and_ktime_follow_the_file()
    {
        var rig = new Rig(200, 201, 300);
        rig.Animate(1, "Lcl Translation", "X", Frames(30, 0, 15, 30), new[] { new[] { 0f, 1f, 0f } });
        var objects = new List<FbxNode> { Null(1, "Timed") }; objects.AddRange(rig.Objects);
        var conns = new List<FbxNode> { C(1, 0) }; conns.AddRange(rig.Conns);
        var s30 = Scene(objects, conns, Header(timeMode: 6)); Assert.Equal(30, s30.Fps); Assert.Equal(1f, s30.FpsBase);
        var s2997 = Scene(objects, conns, Header(timeMode: 9)); Assert.Equal(30, s2997.Fps); Assert.Equal((float)(30 / (30.0 / 1.001)), s2997.FpsBase);
        var custom = Scene(objects, conns, Header(timeMode: 14, customFps: 12)); Assert.Equal(12, custom.Fps);
        var missing = Scene(objects, conns, Header(timeMode: 14, customFps: -1)); Assert.Equal(25, missing.Fps);   // not positive: 25
        Assert.Equal(46186158000L, s30.Ktime);
        Assert.Equal(46186158000L, Scene(objects, conns, Header(version: 7700), 7700).Ktime);   // the new ktime is opt-in before version 8000
        Assert.Equal(46186158000L, Scene(objects, conns, Header(headerVersion: 1004, tcDefinition: 127, version: 7700), 7700).Ktime);
        Assert.Equal(141120000L, Scene(objects, conns, Header(headerVersion: 1004, tcDefinition: 0, version: 7700), 7700).Ktime);
        Assert.Equal(141120000L, Scene(objects, conns, Header(headerVersion: 1004, tcDefinition: 5, version: 7700), 7700).Ktime);
        // 29.97: the keys land on whole frames of 30 (fps), the times divide by 29.97 (fps * fps_base)
        var m = FbxModel.Build(s2997, out string why); Assert.Null(why);
        Assert.Equal(new[] { "3d08659d", "3d88659d", "3dcc986c" }, FBs(Sp(m.Animations[0], m.Animations[0].Channels[0]).Times.Take(3)));
    }

    [Fact]
    public void Numpy_interp_and_deg2rad_and_mathutils_euler_and_decompose_are_bit_equal()
    {
        var r = FbxActions.Interp(new[] { 0.0, 769769300, 1539538600, 3079077200, 4618615800, 5000000000 }, new[] { 0.0, 1539538600, 4618615800 }, new double[] { 0.1f, 0.7f, 0.25f }, 0.33);
        Assert.Equal(new[] { "3fb99999a0000000", "3fd9999994000000", "3fe6666660000000", "3fde666660000000", "3fd0000000000000", "3fd0000000000000" }, r.Select(DB));
        Assert.Equal(FBH("0x1.0c1524p-1"), FB(FbxActions.Deg2RadF(30f))); Assert.Equal(FBH("0x1.13cda8p+1"), FB(FbxActions.Deg2RadF(123.456f)));
        Assert.Equal("3fe0c152382d7365", DB(FbxActions.Deg2Rad(30))); Assert.Equal("40013cda86ccdd88", DB(FbxActions.Deg2Rad(123.456)));
        // Quaternion.to_euler('XYZ', compat)
        var e = FbxActions.QuatToCompatibleEulXyz(new[] { 0.3f, 0.5f, -0.4f, 0.7f }, new[] { 3.0f, -0.2f, 0.1f });
        Assert.Equal(new[] { FBH("0x1.132ef8p+1"), FBH("-0x1.e3d516p+0"), FBH("-0x1.07e478p-4") }, FBs(e));
        e = FbxActions.QuatToCompatibleEulXyz(new[] { 0.3f, 0.5f, -0.4f, 0.7f }, new[] { -3.0f, 2.9f, -3.1f });
        Assert.Equal(new[] { FBH("-0x1.fbc2f8p-1"), FBH("0x1.42052p+2"), FBH("-0x1.9a5edap+1") }, FBs(e));
        e = FbxActions.QuatToCompatibleEulXyz(new[] { 1f, 0f, 0f, 0f }, new[] { 3.1f, 0f, -6.2f });
        Assert.Equal(new[] { "00000000", "80000000", FBH("-0x1.921fb6p+2") }, FBs(e));
        // Matrix.decompose()
        var d = FbxActions.Decompose(new[] { 0.8f, -0.5f, 0.3f, 1.5f, 0.4f, 0.9f, -0.2f, -2.0f, -0.1f, 0.3f, 1.2f, 0.25f, 0f, 0f, 0f, 1f });
        Assert.Equal(new[] { FB(1.5f), FB(-2f), FB(0.25f), FBH("0x1.ebb19cp-1"), FBH("0x1.d4e566p-4"), FBH("0x1.761836p-4"), FBH("0x1.e5f314p-3"), FBH("0x1.ccccce p-1".Replace(" ", "")), FBH("0x1.128788p+0"), FBH("0x1.40c46p+0") }, FBs(d));
        // fcurve_eval_keyframes at whole frames: keys (1, 0.1), (1.8, 0.7), (3.4, 0.25)
        var c = new FbxActions.Curve { Frames = new[] { 1f, 1.8f, 3.4f }, Values = new[] { 0.1f, 0.7f, 0.25f } };
        Assert.Equal(new[] { FB(0.1f), FB(0.1f), FBH("0x1.499998p-1"), FBH("0x1.733334p-2"), FB(0.25f) }, FBs(Enumerable.Range(0, 5).Select(f => c.Evaluate(f))));
    }

    /// <summary>The 6c null rig as the animation fixtures use it: Root -> Spine -> Head, Spine -> Arm (its bind pose five degrees
    /// from its transform: posed), a skinned grid.</summary>
    static (List<FbxNode> objects, List<FbxNode> conns) BoneRig()
    {
        var grid = Enumerable.Range(0, 3).SelectMany(j => Enumerable.Range(0, 4).SelectMany(i => new[] { (double)i, j, 0.0 })).ToArray();
        var faces = Enumerable.Range(0, 2).SelectMany(j => Enumerable.Range(0, 3).Select(i => new[] { j * 4 + i, j * 4 + i + 1, (j + 1) * 4 + i + 1, (j + 1) * 4 + i })).ToArray();
        double[][] Mul(double[][] a, double[][] b) { var o = new double[4][]; for (int i = 0; i < 4; i++) { o[i] = new double[4]; for (int j = 0; j < 4; j++) for (int k = 0; k < 4; k++) o[i][j] += a[i][k] * b[k][j]; } return o; }
        var g = new Dictionary<long, double[][]> { [10] = Translation(0, 0, 0), [11] = RotZ(20, 1, 0, 0), [12] = Mul(RotZ(20, 1, 0, 0), Translation(1, 0, 0)), [13] = Mul(RotZ(20, 1, 0, 0), RotZ(-40, 0.5, 0.5, 0)) };
        var objects = new List<FbxNode> { Null(1, "Rig", V("Lcl Translation", 0, 0, 1)), Model(2, "Skinned"), Geometry(30, "Grid", grid, faces), Material(100, "Mat"), Skin(50),
            Limb(10, "Root"), Limb(11, "Spine", V("Lcl Translation", 1, 0, 0), V("Lcl Rotation", 0, 0, 20)), Limb(12, "Head", V("Lcl Translation", 1, 0, 0)), Limb(13, "Arm", V("Lcl Translation", 0.5, 0.5, 0), V("Lcl Rotation", 0, 0, -45)),
            Cluster(40, "RootCluster", new[] { 0, 1, 4, 5 }, new[] { 1.0, 0.5, 0.9, 0.2 }, Translation(0, 0, 0), g[10]), Cluster(41, "SpineCluster", new[] { 1, 2, 3, 4, 5 }, new[] { 0.5, 0.7, 0.3, 0.3, 0.3 }, Translation(0, 0, 0), g[11]),
            Cluster(42, "HeadCluster", new[] { 2, 3, 5, 8, 9, 10, 11 }, new[] { 0.3, 0.3, 0.25, 1, 1, 1, 1 }, Translation(0, 0, 0), g[12]), Cluster(43, "ArmCluster", new[] { 5, 6, 7 }, new[] { 0.15, 1.0, 0.8 }, Translation(0, 0, 0), g[13]),
            BindPose(60, (10, g[10]), (11, g[11]), (12, g[12]), (13, g[13])) };
        var conns = new List<FbxNode> { C(1, 0), C(2, 1), C(30, 2), C(100, 2), C(10, 1), C(11, 10), C(12, 11), C(13, 11), C(50, 30), C(40, 50), C(41, 50), C(42, 50), C(43, 50), C(10, 40), C(11, 41), C(12, 42), C(13, 43) };
        return (objects, conns);
    }

    [Fact]
    public void Two_stacks_on_one_armature_sort_by_name_reset_the_switched_bones_and_sample_the_rest_from_the_first_fill()
    {
        var (objects, conns) = BoneRig();
        objects.Add(Null(3, "Marker", V("Lcl Translation", 0, 0.5, 0))); conns.Add(C(3, 12));
        var walk = new Rig(200, 201, 300, "Walk");
        walk.Animate(11, "Lcl Rotation", "Z", Frames(24, 0, 24), new[] { new[] { 20f, 40f } }, 0, 0, 20);
        walk.Animate(3, "Lcl Translation", "Y", Frames(24, 0, 24), new[] { new[] { 0.5f, 1f } }, 0, 0.5, 0);
        var run = new Rig(210, 211, 400, "Run", "Base");
        run.Animate(11, "Lcl Rotation", "Z", Frames(24, 0, 12), new[] { new[] { 20f, -30f } }, 0, 0, 20);
        run.Animate(12, "Lcl Scaling", "XYZ", Frames(24, 0, 12), new[] { new[] { 1f, 1.5f }, new[] { 1f, 1.5f }, new[] { 1f, 1.5f } }, 1, 1, 1);
        run.Animate(3, "Lcl Translation", "Y", Frames(24, 0, 12), new[] { new[] { 0.5f, 2f } }, 0, 0.5, 0);
        objects.AddRange(walk.Objects); objects.AddRange(run.Objects); conns.AddRange(walk.Conns); conns.AddRange(run.Conns);
        var s = Scene(objects, conns); Assert.Null(s.Fallback);
        Assert.Equal(new[] { "Rig|Walk", "Marker|Walk", "Rig|Run|Base", "Marker|Run|Base" }, s.Actions.Select(a => a.Name));   // a layer named apart from its stack
        Assert.Equal("Rig|Walk", s.Objects.First(o => o.Name == "Rig").ActiveAction.Name);
        var m = FbxModel.Build(s, out string why); Assert.Null(why);
        // the lone armature takes every bone action, sorted by name; the empty takes its active action only
        Assert.Equal(new[] { "Rig|Run|Base", "Rig|Walk", "Marker|Walk" }, m.Animations.Select(a => a.Name));
        var runA = m.Animations[0]; var walkA = m.Animations[1];
        Assert.Equal(12, runA.Channels.Count); Assert.Equal(13, Sp(runA, Ch(runA, "Spine", "rotation", m)).KeyCount); Assert.Equal(25, Sp(walkA, Ch(walkA, "Spine", "rotation", m)).KeyCount);
        // the posed Arm: reset to the identity in both (the switch away from the active action resets it too) - the channel is its rest
        Assert.Equal(new[] { "00000000", "00000000", "beaf1d45", "3f708fb2" }, FBs(Sp(runA, Ch(runA, "Arm", "rotation", m)).Values.Take(4)));
        Assert.Equal(FBs(Sp(runA, Ch(runA, "Arm", "rotation", m)).Values.Take(4)), FBs(Sp(walkA, Ch(walkA, "Arm", "rotation", m)).Values.Take(4)));
        Assert.Equal("STEP", Sp(walkA, Ch(walkA, "Arm", "rotation", m)).Interpolation);
        // the Head's scale keyed in Run (13 samples), constant in Walk (two, STEP)
        Assert.Equal(13, Sp(runA, Ch(runA, "Head", "scale", m)).KeyCount); Assert.Equal(2, Sp(walkA, Ch(walkA, "Head", "scale", m)).KeyCount);
        // the Marker under the Head: its samples come from the first fill (the armature on Run, the Head scaled by its keys)
        var mk = m.Animations[2];
        Assert.Equal(new[] { "b3800000", "3f000001", "a842f6ff", "b3800000", "3f055558", "2874a508" }, FBs(Sp(mk, Ch(mk, "Marker", "translation", m)).Values.Take(6)));
    }

    [Fact]
    public void A_connected_bone_snaps_its_head_to_the_parents_tail_and_its_keyed_location_moves_nothing()
    {
        var g = new Dictionary<long, double[][]> { [10] = Translation(0, 0, 0), [11] = Translation(0, 1, 0), [12] = Translation(0, 2, 0) };
        var grid = Enumerable.Range(0, 3).SelectMany(j => Enumerable.Range(0, 4).SelectMany(i => new[] { (double)i, j, 0.0 })).ToArray();
        var faces = Enumerable.Range(0, 2).SelectMany(j => Enumerable.Range(0, 3).Select(i => new[] { j * 4 + i, j * 4 + i + 1, (j + 1) * 4 + i + 1, (j + 1) * 4 + i })).ToArray();
        var objects = new List<FbxNode> { Null(1, "Rig"), Null(3, "Marker", V("Lcl Translation", 0.1, 0, 0)), Model(2, "Skinned"), Geometry(30, "Grid", grid, faces), Material(100, "Mat"), Skin(50),
            Cluster(40, "RootCluster", new[] { 0, 1, 2, 3 }, new[] { 1.0, 1, 1, 1 }, Translation(0, 0, 0), g[10]), Cluster(41, "SpineCluster", new[] { 4, 5, 6, 7 }, new[] { 1.0, 1, 1, 1 }, Translation(0, 0, 0), g[11]),
            Cluster(42, "HeadCluster", new[] { 8, 9, 10, 11 }, new[] { 1.0, 1, 1, 1 }, Translation(0, 0, 0), g[12]),
            Limb(10, "Root"), Limb(11, "Spine", V("Lcl Translation", 0, 1, 0)), Limb(12, "Head", V("Lcl Translation", 0, 1, 0)) };
        var conns = new List<FbxNode> { C(1, 0), C(2, 1), C(30, 2), C(100, 2), C(10, 1), C(11, 10), C(12, 11), C(3, 11), C(50, 30), C(40, 50), C(41, 50), C(42, 50), C(10, 40), C(11, 41), C(12, 42) };
        var rig = new Rig(200, 201, 300);
        rig.Animate(11, "Lcl Translation", "XZ", Frames(24, 0, 24), new[] { new[] { 0f, 0.5f }, new[] { 0f, 0.25f } }, 0, 1, 0);
        rig.Animate(11, "Lcl Rotation", "X", Frames(24, 0, 24), new[] { new[] { 0f, 40f } });
        rig.Animate(12, "Lcl Translation", "Y", Frames(24, 0, 24), new[] { new[] { 1f, 1.5f } }, 0, 1, 0);
        rig.Animate(3, "Lcl Translation", "Z", Frames(24, 0, 24), new[] { new[] { 0f, 0.3f } });
        objects.AddRange(rig.Objects); conns.AddRange(rig.Conns);
        var s = Scene(objects, conns); Assert.Null(s.Fallback);
        var arm = s.Armatures[0];
        var root = arm.Bones.First(b => b.Name == "Root"); var spine = arm.Bones.First(b => b.Name == "Spine"); var head = arm.Bones.First(b => b.Name == "Head");
        Assert.True(spine.Connected); Assert.Equal(root.Tail, spine.Head);   // the Root's one bone child along its Y at the tail's distance
        Assert.True(head.Connected);                                          // the Spine's one bone child likewise
        var m = FbxModel.Build(s, out string why); Assert.Null(why);
        var a = m.Animations[0];
        // the Spine's keyed location is left out of its channel matrix: its translation channel is the rest, two STEP samples; its rotation keys count
        Assert.Equal("STEP", Sp(a, Ch(a, "Spine", "translation", m)).Interpolation); Assert.Equal(new[] { "00000000", "3f800000", "00000000" }, FBs(Sp(a, Ch(a, "Spine", "translation", m)).Values.Take(3)));
        Assert.Equal(25, Sp(a, Ch(a, "Spine", "rotation", m)).KeyCount); Assert.Equal(new[] { "3c6a0cd1", "00000000", "00000000", "3f7ff950" }, FBs(Sp(a, Ch(a, "Spine", "rotation", m)).Values.Skip(4).Take(4)));
        // the Head's keyed location moves nothing either (LINEAR: its two samples differ by float noise)
        Assert.Equal(new[] { "00000000", "3f800000", "00000000", "00000000", "3f800000", "33000000" }, FBs(Sp(a, Ch(a, "Head", "translation", m)).Values));
        // the Marker rides the Spine's pose
        var mk = m.Animations[1];
        Assert.Equal(new[] { "3dcccccd", "27d00000", "80000000", "3dcccccd", "3312c000", "3c4ccccf" }, FBs(Sp(mk, Ch(mk, "Marker", "translation", m)).Values.Take(6)));
    }

    [Fact]
    public void A_stack_without_its_class_and_a_curve_without_keys_are_left_to_blender_by_name()
    {
        var rig = new Rig(200, 201, 300, "Take 001", null, "AnimationStack");
        rig.Animate(1, "Lcl Translation", "X", Frames(24, 0, 24), new[] { new[] { 0f, 1f } });
        var objects = new List<FbxNode> { Null(1, "A") }; objects.AddRange(rig.Objects);
        var conns = new List<FbxNode> { C(1, 0) }; conns.AddRange(rig.Conns);
        Assert.Contains("AnimStack", Scene(objects, conns).Fallback);
        rig = new Rig(200, 201, 300);
        rig.Animate(1, "Lcl Translation", "X", new long[0], new[] { new float[0] });
        objects = new List<FbxNode> { Null(1, "A") }; objects.AddRange(rig.Objects);
        conns = new List<FbxNode> { C(1, 0) }; conns.AddRange(rig.Conns);
        Assert.Contains("without keys", Scene(objects, conns).Fallback);
        // a stack without any curve node makes no action: static
        objects = new List<FbxNode> { Null(1, "A"), Stack(200, "Take"), Layer(201, "Take") };
        var s = Scene(objects, new[] { C(1, 0), C(201, 200) }); Assert.Null(s.Fallback); Assert.Empty(s.Actions);
    }

    [Fact]
    public void Unsorted_key_times_follow_the_importers_invalid_curve_path()
    {
        var rig = new Rig(200, 201, 300);
        rig.Animate(1, "Lcl Translation", "X", Frames(24, 0, 48, 24, 24), new[] { new[] { 0f, 4f, 2f, 9f } });
        rig.Animate(1, "Lcl Translation", "Y", new[] { 0L, 1300000000L, 2600000000L }, new[] { new[] { 0f, 1f, 0f } });
        var objects = new List<FbxNode> { Null(1, "Odd") }; objects.AddRange(rig.Objects);
        var conns = new List<FbxNode> { C(1, 0) }; conns.AddRange(rig.Conns);
        var s = Scene(objects, conns); Assert.Null(s.Fallback);
        var x = s.Actions[0].Object.Loc[0];
        float Fr(long t) => (float)(t * (24.0 / 46186158000) + 1.0);
        Assert.Equal(new[] { 1f, Fr(1300000000), Fr(2600000000), 25f, 49f }, x.Frames);   // the combined times: the Y curve's fractional frames joined
        var m = FbxModel.Build(s, out string why); Assert.Null(why);
        Assert.Equal(49, Sp(m.Animations[0], m.Animations[0].Channels[0]).KeyCount);
        Assert.Equal(new[] { "00000000", "00000000", "80000000", "3a5a740d", "3baa4974", "80000000", "3ada740e", "00000000" }, FBs(Sp(m.Animations[0], m.Animations[0].Channels[0]).Values.Take(8)));
    }
}
