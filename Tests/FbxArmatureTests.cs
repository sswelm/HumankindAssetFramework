using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

// Sub-step 6c of the native FBX reader: editor/FbxArmature.cs (with FbxImport and FbxModel) on documents built in memory in
// the shapes of tools/fbx-drill/fbx_armature_fixtures.py. The expected values are Blender 5.1's own (its FBX import + glTF
// export of those scenes, read back through GlbReader, 2026-11-02); the bit-level claim on every file is tools/fbx_model_drill.sh's.
public class FbxArmatureTests
{
    // ---- builders -------------------------------------------------------------------------------------------------
    static FbxNode N(string name, params object[] props) { var n = new FbxNode { Name = name }; n.Properties.AddRange(props); return n; }
    static FbxNode With(FbxNode n, params FbxNode[] children) { n.Children.AddRange(children); return n; }
    static FbxNode P(string name, string kind, string sub, params object[] values) { var p = N("P", name, kind, sub, ""); p.Properties.AddRange(values); return p; }
    static FbxNode V(string name, double x, double y, double z) => P(name, "Vector3D", "Vector", x, y, z);
    static FbxNode Props70(params FbxNode[] ps) => With(N("Properties70"), ps);
    static FbxNode Object(string cls, long id, string name, string sub, params FbxNode[] children) => With(N(cls, id, name + "\0\u0001" + cls, sub), children);
    static FbxNode C(long src, long dst) => N("C", "OO", src, dst);
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
    static double[][] Mul(double[][] a, double[][] b)
    {
        var r = new double[4][];
        for (int i = 0; i < 4; i++) { r[i] = new double[4]; for (int j = 0; j < 4; j++) for (int k = 0; k < 4; k++) r[i][j] += a[i][k] * b[k][j]; }
        return r;
    }
    static FbxNode Cluster(long id, string name, int[] indexes, double[] weights, double[][] transform = null, double[][] link = null)
    {
        var children = new List<FbxNode> { N("Version", 100) };
        if (indexes != null) { children.Add(N("Indexes", indexes)); children.Add(N("Weights", weights)); }
        if (transform != null) children.Add(N("Transform", Mat16(transform)));
        if (link != null) children.Add(N("TransformLink", Mat16(link)));
        return Object("Deformer", id, name, "Cluster", children.ToArray());
    }
    static FbxNode BindPose(long id, params (long node, double[][] m)[] nodes)
    {
        var children = new List<FbxNode> { N("Type", "BindPose"), N("Version", 100) };
        foreach (var (node, m) in nodes) children.Add(With(N("PoseNode"), N("Node", node), N("Matrix", Mat16(m))));
        return Object("Pose", id, "BindPose", "BindPose", children.ToArray());
    }
    static FbxNode Layer(string kind, string data, string mapping, string reference, object values, string name = "", object index = null, string indexName = null)
    {
        var l = With(N("LayerElement" + kind, 0), N("Name", name), N("MappingInformationType", mapping), N("ReferenceInformationType", reference), N(data, values));
        if (index != null) l.Children.Add(N(indexName, index));
        return l;
    }
    static FbxNode Geometry(long id, string name, double[] vertices, int[][] faces, params FbxNode[] layers)
    {
        var idx = new List<int>();
        foreach (var f in faces) { idx.AddRange(f.Take(f.Length - 1)); idx.Add(~f[f.Length - 1]); }
        var children = new List<FbxNode> { N("Vertices", vertices), N("PolygonVertexIndex", idx.ToArray()), N("GeometryVersion", 124) };
        children.AddRange(layers);
        var layerNode = With(N("Layer", 0), N("Version", 100));
        foreach (var l in layers) layerNode.Children.Add(With(N("LayerElement"), N("Type", l.Name), N("TypedIndex", 0)));
        children.Add(layerNode);
        return Object("Geometry", id, name, "Mesh", children.ToArray());
    }
    static List<FbxNode> Header()
    {
        var settings = Props70(P("UpAxis", "int", "Integer", 1), P("UpAxisSign", "int", "Integer", 1), P("FrontAxis", "int", "Integer", 2), P("FrontAxisSign", "int", "Integer", 1),
            P("CoordAxis", "int", "Integer", 0), P("CoordAxisSign", "int", "Integer", 1), P("UnitScaleFactor", "double", "Number", 1.0));
        var template = With(N("PropertyTemplate", "FbxNode"), Props70(P("Lcl Translation", "Lcl Translation", "", 0.0, 0.0, 0.0), P("Lcl Rotation", "Lcl Rotation", "", 0.0, 0.0, 0.0),
            P("Lcl Scaling", "Lcl Scaling", "", 1.0, 1.0, 1.0), P("RotationActive", "bool", "", 0), P("RotationOrder", "enum", "", 0)));
        return new List<FbxNode> { With(N("FBXHeaderExtension"), N("FBXHeaderVersion", 1003), N("FBXVersion", 7400), N("Creator", "tests")),
            With(N("GlobalSettings"), N("Version", 1000), settings), With(N("Definitions"), With(N("ObjectType", "Model"), template)) };
    }
    static HafModel Build(FbxNode[] objects, FbxNode[] connections, out string why)
    {
        var d = new FbxDocument { Version = 7400 };
        d.Nodes.AddRange(Header());
        d.Nodes.Add(With(N("Objects"), objects)); d.Nodes.Add(With(N("Connections"), connections));
        return FbxModel.Build(FbxImport.Build(d), out why);
    }
    static HafModel Build(FbxNode[] objects, FbxNode[] connections) { var m = Build(objects, connections, out string why); Assert.Null(why); return m; }
    static string Bits(double d) => BitConverter.ToUInt64(BitConverter.GetBytes(d), 0).ToString("x16");
    static readonly double[] GridV = Enumerable.Range(0, 3).SelectMany(j => Enumerable.Range(0, 4).SelectMany(i => new[] { (double)i, j, 0.0 })).ToArray();
    static readonly int[][] GridF = Enumerable.Range(0, 2).SelectMany(j => Enumerable.Range(0, 3).Select(i => new[] { j * 4 + i, j * 4 + i + 1, (j + 1) * 4 + i + 1, (j + 1) * 4 + i })).ToArray();
    static FbxNode GridUv => Layer("UV", "UV", "ByVertice", "Direct", Enumerable.Range(0, 3).SelectMany(j => Enumerable.Range(0, 4).SelectMany(i => new[] { i / 3.0, j / 3.0 })).ToArray(), "map1");
    static int NodeNamed(HafModel m, string name) => m.Nodes.FindIndex(n => n.Name == name);
    /// <summary>The primitive's vertex at a position (the layout sorts the dots: positions are the way to a vertex).</summary>
    static int VertexAt(HafPrimitive p, double x, double y, double z, double tol = 1e-5)
    {
        for (int v = 0; v < p.VertexCount; v++) if (Math.Abs(p.Positions[3 * v] - x) < tol && Math.Abs(p.Positions[3 * v + 1] - y) < tol && Math.Abs(p.Positions[3 * v + 2] - z) < tol) return v;
        throw new InvalidOperationException($"no vertex at ({x}, {y}, {z}); positions: " + string.Join(" ", Enumerable.Range(0, p.VertexCount).Select(i => $"({p.Positions[3 * i]}, {p.Positions[3 * i + 1]}, {p.Positions[3 * i + 2]})")));
    }

    /// <summary>The armature_null fixture: a Null turned armature, Root -> Spine -> Head and Spine -> Arm, a skinned grid with every weight shape.</summary>
    static HafModel NullRig(out string why)
    {
        var g = new Dictionary<long, double[][]> { [10] = Translation(0, 0, 0), [11] = RotZ(20, 1, 0, 0), [12] = Mul(RotZ(20, 1, 0, 0), Translation(1, 0, 0)), [13] = Mul(RotZ(20, 1, 0, 0), RotZ(-45, 0.5, 0.5, 0)) };
        var objects = new[] {
            Null(1, "Rig", V("Lcl Scaling", 2, 2, 2)), Model(2, "Skinned"), Model(3, "Loose", V("Lcl Translation", 0, 5, 0)), Geometry(30, "Grid", GridV, GridF, GridUv),
            Geometry(31, "LooseGeo", new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { new[] { 0, 1, 2 } }), Material(100, "Mat"), Skin(50),
            Limb(10, "Root", V("Lcl Translation", 0, 0, 0)), Limb(11, "Spine", V("Lcl Translation", 1, 0, 0), V("Lcl Rotation", 0, 0, 20)),
            Limb(12, "Head", V("Lcl Translation", 1, 0, 0)), Limb(13, "Arm", V("Lcl Translation", 0.5, 0.5, 0), V("Lcl Rotation", 0, 0, -45)),
            Cluster(40, "RootCluster", new[] { 0, 1, 4, 5 }, new[] { 1.0, 0.5, 0.9, 0.2 }, Translation(0, 0, 0), g[10]),
            Cluster(41, "SpineCluster", new[] { 1, 2, 3, 4, 5 }, new[] { 0.5, 0.7, 0.3, 0.00005, 0.3 }, Translation(0, 0, 0), g[11]),
            Cluster(42, "HeadCluster", new[] { 2, 3, 5, 7, -1 }, new[] { 0.3, 0.3, 0.25, 1.5, 0.7 }, Translation(0, 0, 0), g[12]),
            Cluster(43, "ArmCluster", new[] { 5, 6, 7, 12 }, new[] { 0.15, 1.0, 0.8, 1.0 }, Translation(0, 0, 0), g[13]),
            Cluster(44, "RootCluster2", new[] { 5 }, new[] { 0.1 }, Translation(0, 0, 0), g[10]),
            BindPose(60, (1, new[] { new[] { 2.0, 0, 0, 0 }, new[] { 0, 2.0, 0, 0 }, new[] { 0, 0, 2.0, 0 }, new[] { 0, 0, 0, 1.0 } }), (10, g[10]), (11, g[11]), (12, g[12]), (13, g[13])) };
        var conns = new List<FbxNode> { C(1, 0), C(2, 1), C(3, 1), C(30, 2), C(31, 3), C(100, 2), C(100, 3), C(10, 1), C(11, 10), C(12, 11), C(13, 11), C(50, 30) };
        for (int i = 0; i < 5; i++) conns.Add(C(40 + i, 50));
        conns.AddRange(new[] { C(10, 40), C(11, 41), C(12, 42), C(13, 43), C(10, 44) });
        return Build(objects, conns.ToArray(), out why);
    }

    [Fact]
    public void A_null_with_bones_becomes_the_armature_and_its_joints_skin_and_neutral_bone_are_written_as_blender_writes_them()
    {
        var m = NullRig(out string why); Assert.Null(why);
        // the exporter's traversal: the Loose mesh, the joints (children before parents), the neutral bone, the skinned mesh, the armature
        Assert.Equal(new[] { "Loose", "Head", "Arm", "Spine", "Root", "neutral_bone", "Skinned", "Rig" }, m.Nodes.Select(n => n.Name));
        Assert.Equal(new[] { 0, 6, 4, 5 }, m.Nodes[7].Children);   // objects by name, then the root bone, then the neutral bone
        Assert.Single(m.Skins); Assert.Equal("Rig", m.Skins[0].Name); Assert.Equal(new[] { 4, 3, 1, 2, 5 }, m.Skins[0].Joints);
        Assert.Equal(0, m.Nodes[6].Skin);   // the skinned mesh carries no transform of its own (the reader's identity)
        Assert.Equal(new[] { 0.0, 0, 0 }, m.Nodes[6].Translation); Assert.Equal(new[] { 0.0, 0, 0, 1 }, m.Nodes[6].Rotation); Assert.Equal(new[] { 1.0, 1, 1 }, m.Nodes[6].Scale);
        // a root bone's transform against the armature is the Z-up to Y-up basis change alone; a joint keeps its float noise
        Assert.Equal(new[] { -0.7071067690849304, 0, 0, 0.7071067690849304 }, m.Nodes[4].Rotation);
        Assert.Equal(new[] { -0.7071067690849304, 0, 0, 0.7071067690849304 }, m.Nodes[5].Rotation);
        Assert.Equal(new[] { 1.0, 0, 0 }, m.Nodes[3].Translation);
        Assert.Equal(Bits(0.1736481934785843) + Bits(0.9848077893257141), Bits(m.Nodes[3].Rotation[2]) + Bits(m.Nodes[3].Rotation[3]));
        Assert.Equal(new[] { 0.9999999403953552, 0.9999999403953552, 1 }, m.Nodes[3].Scale);
        // the inverse bind matrices: the armature's scale of 2 under the file's 0.01, column by column; the neutral bone's last
        var ibm = m.Skins[0].InverseBindMatrices;
        Assert.Equal(5 * 16, ibm.Length); Assert.Equal(50.0, ibm[0]); Assert.Equal(50.0, ibm[5]); Assert.Equal(1.0, ibm[15]); Assert.Equal(50.0, ibm[4 * 16]);
        Assert.Equal(46.9846344, ibm[16], 6);   // Spine: cos 20 degrees times 50
        // weights: vertex 1 Root + Spine; vertex 4's tiny Spine weight dropped; vertex 5's five bones (the second Root cluster
        // replaced the first's weight) sorted by weight, four kept and normalized; vertices 8-11 go to the neutral bone
        var p = m.Meshes[m.Nodes[6].Mesh].Primitives[0];
        Assert.Equal(12, p.VertexCount);   // one dot per vertex (planar, ByVertice UVs): the exporter's unique sorts the dots by vertex index first
        int v1 = 1, v4 = 4, v5 = 5, v8 = 8;
        Assert.Equal(new ushort[] { 0, 1, 0, 0 }, p.Joints.Skip(4 * v1).Take(4)); Assert.Equal(new[] { 0.5f, 0.5f, 0f, 0f }, p.Weights.Skip(4 * v1).Take(4));
        Assert.Equal(new ushort[] { 0, 0, 0, 0 }, p.Joints.Skip(4 * v4).Take(4)); Assert.Equal(new[] { 1f, 0f, 0f, 0f }, p.Weights.Skip(4 * v4).Take(4));
        Assert.Equal(new ushort[] { 1, 2, 3, 0 }, p.Joints.Skip(4 * v5).Take(4)); Assert.Equal(new[] { 0.37499997f, 0.31249997f, 0.187499985f, 0.124999993f }, p.Weights.Skip(4 * v5).Take(4));   // 0.3, 0.25, 0.15, 0.1 over 0.8 in float, as Blender's bytes
        Assert.Equal(new ushort[] { 4, 0, 0, 0 }, p.Joints.Skip(4 * v8).Take(4)); Assert.Equal(new[] { 1f, 0f, 0f, 0f }, p.Weights.Skip(4 * v8).Take(4));
        // vertex 7: the Head weight of 1.5 clamped to 1 beside Arm 0.8; the index past the mesh and the negative one ignored
        Assert.Equal(new ushort[] { 2, 3, 0, 0 }, p.Joints.Skip(4 * 7).Take(4)); Assert.Equal(new[] { 0.555555582f, 0.444444478f, 0f, 0f }, p.Weights.Skip(4 * 7).Take(4));   // 1 and 0.8 over 1.8 in float, as Blender's bytes
    }

    [Fact]
    public void Bones_under_the_scene_root_get_an_inserted_armature_and_empty_and_twin_bone_names_follow_blenders_pools()
    {
        var objects = new[] {
            Model(2, "Skinned", V("Lcl Translation", 0, 0, -1)), Geometry(30, "Grid", GridV, GridF, GridUv), Material(100, "Mat"), Skin(50),
            Limb(10, "A", V("Lcl Translation", 0, 0, 1)), Object("Model", 11, "B", "Limb", Props70(V("Lcl Translation", 0, 2, 0), V("Lcl Rotation", 30, 0, 0))),
            Limb(12, "", V("Lcl Translation", 1, 0, 0)), Limb(13, "B", V("Lcl Translation", 0, 0, 3)), Limb(14, "B", V("Lcl Translation", 0, 0, 4)),
            Cluster(40, "ACluster", new[] { 0, 1, 2, 3 }, new[] { 1.0, 1.0, 0.5, 0.5 }),
            Cluster(41, "BCluster", new[] { 2, 3, 4, 5 }, new[] { 0.5, 0.5, 1.0, 1.0 }, Translation(0, 0, 0), Translation(0, 2, 0.25)),
            Cluster(42, "CCluster", new[] { 6, 7, 8, 9, 10, 11 }, new[] { 1.0, 1, 1, 1, 1, 1 }, Translation(0.1, 0, 0)),
            BindPose(60, (10, Translation(0, 0, 1.5))) };
        var m = Build(objects, new[] { C(2, 0), C(30, 2), C(100, 2), C(10, 0), C(11, 10), C(12, 11), C(13, 10), C(14, 13), C(50, 30), C(40, 50), C(41, 50), C(42, 50), C(10, 40), C(11, 41), C(12, 42) });
        Assert.Equal("Armature", m.Nodes.Last().Name);
        Assert.Equal(new[] { "A", "B", "Bone", "B.001", "B.002" }, m.Skins[0].Joints.Select(j => m.Nodes[j].Name));   // depth first; "" is Bone; twins by BLI_uniquename
        int skinned = NodeNamed(m, "Skinned");
        Assert.Equal(m.Nodes.Count - 1, m.Nodes[skinned].Parent); Assert.Equal(0, m.Nodes[skinned].Skin);
    }

    [Fact]
    public void Objects_under_bones_hang_from_their_joints_a_mesh_with_a_bone_child_is_a_fake_bone_and_an_unused_armature_still_writes_its_skin()
    {
        var tri = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }; var triF = new[] { new[] { 0, 1, 2 } };
        var objects = new[] {
            Null(1, "RigA"), Null(5, "RigB"), Model(2, "Body"), Geometry(30, "BodyGeo", GridV, GridF, GridUv), Material(100, "Mat"),
            Null(3, "Marker", V("Lcl Translation", 0, 0, 0.5)), Model(4, "Shoe", V("Lcl Translation", 0.2, 0, 0)), Geometry(31, "ShoeGeo", tri, triF),
            Model(6, "Lamp", V("Lcl Translation", 0, 0, 2)), Geometry(32, "LampGeo", tri, triF),
            Model(7, "Holder", V("Lcl Translation", 0, 1, 0)), Geometry(33, "HolderGeo", tri, triF), Limb(12, "Antenna", V("Lcl Translation", 0, 0, 1)),
            Limb(20, "Other", V("Lcl Translation", 3, 0, 0)), Limb(21, "OtherChild", V("Lcl Translation", 0, 0, 1)),
            Skin(50), Cluster(40, "HipCluster", Enumerable.Range(0, 12).ToArray(), Enumerable.Repeat(1.0, 12).ToArray(), Translation(0, 0, 0), Translation(0, 0, 1)),
            Cluster(41, "LegCluster", new[] { 0, 1, 2, 3 }, new[] { 0.5, 0.5, 0.5, 0.5 }, Translation(0, 0, 0), Translation(0, 0, 0.9921875)),
            Limb(10, "Hip", V("Lcl Translation", 0, 0, 1)), Limb(11, "Leg", V("Lcl Translation", 0, 0, -0.0078125)), Limb(22, "Aux", V("Lcl Translation", 2, 0, 0)), Limb(23, "Toe", V("Lcl Translation", 0, 0, -0.0078125)) };
        var conns = new[] { C(1, 0), C(5, 0), C(2, 1), C(30, 2), C(100, 2), C(10, 1), C(11, 10), C(22, 1), C(23, 11), C(3, 11), C(4, 11), C(31, 4), C(6, 4), C(32, 6), C(7, 10), C(33, 7), C(12, 7),
            C(20, 5), C(21, 20), C(50, 30), C(40, 50), C(41, 50), C(10, 40), C(11, 41) };
        var m = Build(objects, conns);
        int hip = NodeNamed(m, "Hip"), leg = NodeNamed(m, "Leg"), holderJoint = m.Nodes.FindIndex(n => n.Name == "Holder" && n.Mesh < 0), holderMesh = m.Nodes.FindIndex(n => n.Name == "Holder" && n.Mesh >= 0);
        Assert.Equal(new[] { "Toe", "Marker", "Shoe" }, m.Nodes[leg].Children.Select(c => m.Nodes[c].Name));     // the bone child, then the objects parented to the bone by name
        Assert.Equal(new[] { "Lamp" }, m.Nodes[NodeNamed(m, "Shoe")].Children.Select(c => m.Nodes[c].Name));     // their own children follow
        Assert.Equal(new[] { "Leg", "Holder" }, m.Nodes[hip].Children.Select(c => m.Nodes[c].Name));             // bone children in creation order (Holder the fake bone)
        Assert.Equal(new[] { "Antenna", "Holder" }, m.Nodes[holderJoint].Children.Select(c => m.Nodes[c].Name)); // the fake bone's bone child, then the mesh object hung from it
        Assert.True(holderMesh >= 0 && m.Nodes[holderMesh].Parent == holderJoint);
        Assert.Equal(2, m.Skins.Count); Assert.Equal("RigA", m.Skins[0].Name); Assert.Equal("RigB", m.Skins[1].Name);   // RigB's bones drive nothing: its skin comes last
        Assert.Equal(new[] { "Hip", "Leg", "Toe", "Holder", "Antenna", "Aux" }, m.Skins[0].Joints.Select(j => m.Nodes[j].Name));   // depth first, the second root last
        Assert.Equal(new[] { "Body", "Hip", "Aux" }, m.Nodes[NodeNamed(m, "RigA")].Children.Select(c => m.Nodes[c].Name));   // objects by name, then the root bones in order
        Assert.Equal(new[] { "Other", "OtherChild" }, m.Skins[1].Joints.Select(j => m.Nodes[j].Name));
        // Marker against the Leg's tail: the Leg's size (the Toe at 1/128; bone matrices are in the armature's space, the file's
        // units) is floored to 0.01
        Assert.Equal(-0.0078125, m.Nodes[leg].Translation[2]);
        Assert.Equal(new[] { 0.0, -9.3384073807101231E-09, 0.50000017881393433 }, m.Nodes[NodeNamed(m, "Marker")].Translation);   // the tail noise is Blender's
    }

    [Fact]
    public void An_object_under_a_bone_whose_bind_pose_differs_from_its_transform_is_left_to_blender_by_name()
    {
        var objects = new[] { Null(1, "Rig"), Null(3, "Marker", V("Lcl Translation", 1, 0, 0)), Model(2, "Skinned"), Geometry(30, "Grid", GridV, GridF, GridUv), Skin(50),
            Cluster(40, "PivotCluster", Enumerable.Range(0, 12).ToArray(), Enumerable.Repeat(1.0, 12).ToArray(), Translation(0, 0, 0), Translation(0, 0, 1)),
            Limb(10, "Pivot", V("Lcl Translation", 0, 0, 1), V("Lcl Rotation", 0, 0, 30)) };
        Assert.Null(Build(objects, new[] { C(1, 0), C(2, 1), C(30, 2), C(10, 1), C(3, 10), C(50, 30), C(40, 50), C(10, 40) }, out string why));
        Assert.Contains("posed bone", why);
    }

    [Fact]
    public void Colour_layers_follow_the_exporters_plan_and_blenders_byte_rounding()
    {
        double[] v = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 2, 0, 0, 2, 1, 0 }; var f = new[] { new[] { 0, 1, 2, 3 }, new[] { 1, 4, 5, 2 } };
        var corner = Enumerable.Range(0, 8).SelectMany(k => new[] { k / 8.0, 1.0 - k / 8.0, (k % 3) / 2.0, 0.25 + k / 16.0 }).ToArray();
        FbxNode[] LayersA() => new[] { Layer("Color", "Colors", "ByPolygonVertex", "Direct", corner, "corner"),
            Layer("Color", "Colors", "ByVertice", "IndexToDirect", new[] { 0.1, 0.2, 0.3, 1.0, 0.9, 0.8, 0.7, 0.5 }, "byvert", new[] { 0, 1, 1, 0, 1, 0 }, "ColorIndex"),
            Layer("Color", "Colors", "AllSame", "IndexToDirect", new[] { 0.2, 0.4, 0.6, 0.8 }, "same"),
            Layer("Material", "Materials", "ByPolygon", "IndexToDirect", new[] { 0, 1 }) };
        var objects = new[] { Model(1, "TwoSlots"), Geometry(30, "TwoSlotsGeo", v, f, LayersA()), Model(2, "NoMaterial"), Geometry(31, "NoMaterialGeo", v, f, Layer("Color", "Colors", "ByPolygonVertex", "Direct", corner, "corner")),
            Model(3, "WithMaterial"), Geometry(32, "WithMaterialGeo", v, f, Layer("Color", "Colors", "ByPolygonVertex", "Direct", corner, "corner")), Material(100, "Mat") };
        var m = Build(objects, new[] { C(1, 0), C(2, 0), C(3, 0), C(30, 1), C(31, 2), C(32, 3), C(100, 1), C(100, 3) });
        // with a material: a forced COLOR_0 of ones, then every layer with alpha (sRGB bytes -> linear -> normalized shorts); the
        // corner (0, 0, 0) of the first face carries colour 0: (0, 1, 0, 0.25) -> bytes (0, 255, 0, 64) -> shorts (0, 65535, 0, 16448)
        var two = m.Meshes[m.Nodes[NodeNamed(m, "TwoSlots")].Mesh];
        var p0 = two.Primitives[0]; int c0 = VertexAt(p0, 0, 0, 0);
        Assert.Equal(new[] { 1f, 1f, 1f, 1f }, p0.Colors.Skip(4 * c0).Take(4));
        Assert.Equal(3, p0.ColorMore.Count);
        Assert.Equal(new[] { 0f, 1f, 0f, (float)(16448 / 65535.0) }, p0.ColorMore[0].Skip(4 * c0).Take(4));
        Assert.Equal(new[] { 1f, 1f, 1f, 1f }, p0.ColorMore[1].Skip(4 * c0).Take(4));   // the ByVertice layer through an index is left at its default: Blender skips it
        // the material-less mesh: the active layer itself is COLOR_0, with alpha
        var none = m.Meshes[m.Nodes[NodeNamed(m, "NoMaterial")].Mesh].Primitives[0]; int n0 = VertexAt(none, 0, 0, 0);
        Assert.Equal(new[] { 0f, 1f, 0f, (float)(16448 / 65535.0) }, none.Colors.Skip(4 * n0).Take(4)); Assert.Null(none.ColorMore);
        var with = m.Meshes[m.Nodes[NodeNamed(m, "WithMaterial")].Mesh].Primitives[0];
        Assert.Equal(new[] { 1f, 1f, 1f, 1f }, with.Colors.Take(4)); Assert.Single(with.ColorMore);
        // round_fl_to_uchar_clamp
        Assert.Equal(128, FbxImport.RoundFlToUcharClamp(127.5f)); Assert.Equal(0, FbxImport.RoundFlToUcharClamp(-0.2f)); Assert.Equal(255, FbxImport.RoundFlToUcharClamp(254.6f)); Assert.Equal(255, FbxImport.RoundFlToUcharClamp(300f));
        // an AllSame layer carrying an index array stops Blender's import
        var bad = new[] { Model(1, "Bad"), Geometry(30, "BadGeo", v, f, Layer("Color", "Colors", "AllSame", "IndexToDirect", new[] { 0.2, 0.4, 0.6, 0.8 }, "same", new[] { 0 }, "ColorIndex")) };
        Assert.Null(Build(bad, new[] { C(1, 0), C(30, 1) }, out string why)); Assert.Contains("asserts", why);
    }

    [Fact]
    public void The_roll_of_a_bone_matrix_is_the_full_twist_about_its_axis()
    {
        float[] identity = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        Assert.Equal(0f, FbxArmature.Mat3VecToRoll(identity, 0f, 1f, 0f));
        // a rotation about Y by 30 degrees (M[col][row]): the twist is 30 degrees, not the 15 of the half angle
        float c = (float)Math.Cos(Math.PI / 6), s = (float)Math.Sin(Math.PI / 6);
        float[] rotY = { c, 0, -s, 0, 1, 0, s, 0, c };
        Assert.Equal(Math.PI / 6, FbxArmature.Mat3VecToRoll(rotY, 0f, 1f, 0f), 5);
    }

    [Theory]
    [InlineData("Direct")]
    [InlineData("IndexToDirect")]
    public void Polygon_mapped_colours_keep_the_polyloop_attributes_default(string reference)
    {
        var m = Build(new[] { Model(1, "Mesh"), Geometry(30, "Geo", new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { new[] { 0, 1, 2 } },
            Layer("Color", "Colors", "ByPolygon", reference, new[] { 1.0, 0, 0, 1 }, "Color", reference == "Direct" ? null : new[] { 0 }, "ColorIndex")) }, new[] { C(1, 0), C(30, 1) });
        Assert.All(m.Meshes[0].Primitives[0].Colors, c => Assert.Equal(1f, c));
    }

    [Fact]
    public void Removing_a_face_compacts_every_colour_layer_with_its_corners()
    {
        var first = Enumerable.Repeat(new double[] { 1, 0, 0, 1 }, 3).Concat(Enumerable.Repeat(new double[] { 0, 0, 1, 1 }, 3)).SelectMany(c => c).ToArray();
        var second = Enumerable.Repeat(new double[] { 1, 1, 0, 1 }, 3).Concat(Enumerable.Repeat(new double[] { 0, 1, 0, 1 }, 3)).SelectMany(c => c).ToArray();
        var m = Build(new[] { Model(1, "Mesh"), Geometry(30, "Geo", new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { new[] { 0, 0, 1 }, new[] { 0, 1, 2 } },
            Layer("Color", "Colors", "ByPolygonVertex", "Direct", first, "Blue"), Layer("Color", "Colors", "ByPolygonVertex", "Direct", second, "Green")) }, new[] { C(1, 0), C(30, 1) });
        var p = m.Meshes[0].Primitives[0];
        Assert.Equal(3, p.VertexCount); Assert.Single(p.ColorMore);
        for (int v = 0; v < p.VertexCount; v++)
        {
            Assert.Equal(new[] { 0f, 0f, 1f, 1f }, p.Colors.Skip(4 * v).Take(4));
            Assert.Equal(new[] { 0f, 1f, 0f, 1f }, p.ColorMore[0].Skip(4 * v).Take(4));
        }
    }

    [Theory]
    [InlineData(true, "shared skinned mesh")]
    [InlineData(false, "several skinned meshes")]
    public void Declared_skin_limits_are_enforced_by_the_reader(bool shared, string reason)
    {
        double[] v = { 0, 0, 0, 1, 0, 0, 0, 1, 0 }; int[][] f = { new[] { 0, 1, 2 } };
        var objects = new List<FbxNode> { Null(1, "Rig"), Limb(10, "Bone"), Model(2, "A"), Model(3, "B"), Geometry(30, "Geo", v, f), Skin(50),
            Cluster(40, "ACluster", new[] { 0, 1, 2 }, new[] { 1.0, 1, 1 }) };
        var links = new List<FbxNode> { C(1, 0), C(10, 1), C(2, 0), C(3, 0), C(30, 2), C(50, 30), C(40, 50), C(10, 40) };
        if (shared) links.Add(C(30, 3));
        else
        {
            objects.AddRange(new[] { Geometry(31, "GeoB", v, f), Skin(51), Cluster(41, "BCluster", new[] { 0 }, new[] { 1.0 }) });
            links.AddRange(new[] { C(31, 3), C(51, 31), C(41, 51), C(10, 41) });
        }
        Assert.Null(Build(objects.ToArray(), links.ToArray(), out string why));
        Assert.Contains(reason, why);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bind_pose_node_ids_must_be_int64_as_blender_requires(bool real)
    {
        var pose = BindPose(60, (10, Translation(0, 0, 0)));
        pose.First("PoseNode").First("Node").Properties[0] = real ? (object)10.0 : 10;
        Assert.Null(Build(new[] { Null(1, "Rig"), Limb(10, "Bone"), pose }, new[] { C(1, 0), C(10, 1) }, out string why));
        Assert.Contains("int64", why);
    }
}
