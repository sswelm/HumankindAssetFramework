using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

// Step 4 of the native FBX reader: skins, clusters, bind poses and bones in editor/FbxScene.cs, on documents built in
// memory in the shapes of tools/fbx-drill/fbx_scene_fixtures.py's skin scenes. The expected values are ufbx's own
// (ufbxdump --full on those scenes, 2026-11-01); the bit-level claim on every file is tools/fbx_drill.sh's.
public class FbxSkinTests
{
    // ---- builders -------------------------------------------------------------------------------------------------
    static FbxNode N(string name, params object[] props) { var n = new FbxNode { Name = name }; n.Properties.AddRange(props); return n; }
    static FbxNode With(FbxNode n, params FbxNode[] children) { n.Children.AddRange(children); return n; }
    static FbxNode P(string name, params object[] values) { var p = N("P", name, "Vector3D", "", ""); p.Properties.AddRange(values); return p; }
    static FbxNode Props70(params FbxNode[] ps) => With(N("Properties70"), ps);
    static FbxNode Object(string cls, long id, string name, string sub, params FbxNode[] children) => With(N(cls, id, name + "\0\u0001" + cls, sub), children);
    static FbxNode C(long src, long dst) => N("C", "OO", src, dst);
    static FbxNode Model(long id, string name, string sub, params FbxNode[] props) => props.Length > 0 ? Object("Model", id, name, sub, Props70(props)) : Object("Model", id, name, sub);
    static FbxNode Limb(long id, string name, string sub = "LimbNode", params FbxNode[] props) =>
        props.Length > 0 ? Object("NodeAttribute", id, name, sub, N("TypeFlags", "Skeleton"), Props70(props)) : Object("NodeAttribute", id, name, sub, N("TypeFlags", "Skeleton"));
    static FbxNode Quad() => Object("Geometry", 30, "Quad", "Mesh", N("Vertices", new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }), N("PolygonVertexIndex", new[] { 0, 1, ~2, 0, 2, ~3 }), N("GeometryVersion", 124));
    static FbxNode Cluster(long id, string name, int[] indices, double[] weights, double[] transform = null, double[] link = null)
    {
        var children = new List<FbxNode>();
        if (indices != null) { children.Add(N("Indexes", indices)); children.Add(N("Weights", weights)); }
        if (transform != null) children.Add(N("Transform", transform));
        if (link != null) children.Add(N("TransformLink", link));
        return Object("Deformer", id, name, "Cluster", children.ToArray());
    }
    static FbxNode Skin(long id, string name, string method = null, int[] dqIndices = null, double[] dqWeights = null)
    {
        var children = new List<FbxNode> { N("Version", 101) };
        if (method != null) children.Add(N("SkinningType", method));
        if (dqIndices != null) { children.Add(N("Indexes", dqIndices)); children.Add(N("BlendWeights", dqWeights)); }
        return Object("Deformer", id, name, "Skin", children.ToArray());
    }
    static FbxNode PoseNode(long? id, double[] matrix)
    {
        var children = new List<FbxNode>();
        if (id.HasValue) children.Add(N("Node", id.Value));
        if (matrix != null) children.Add(N("Matrix", matrix));
        return With(N("PoseNode"), children.ToArray());
    }
    static FbxNode Pose(long id, string name, string sub, params FbxNode[] entries) =>
        Object("Pose", id, name, sub, new[] { N("Type", sub), N("NbPoseNodes", entries.Length) }.Concat(entries).ToArray());
    static FbxNode Header(string creator) => With(N("FBXHeaderExtension"), N("FBXHeaderVersion", 1003), N("FBXVersion", 7400), N("Creator", creator));
    static double[] T(double x, double y, double z) => new[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1 };
    static double[] D(double x, double y, double z, double tx = 0, double ty = 0, double tz = 0) => new[] { x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, tx, ty, tz, 1 };
    static FbxScene Scene(FbxNode[] objects, FbxNode[] connections, FbxNode header = null)
    {
        var d = new FbxDocument { Version = 7400 };
        if (header != null) d.Nodes.Add(header);
        d.Nodes.Add(With(N("Objects"), objects)); d.Nodes.Add(With(N("Connections"), connections));
        return FbxScene.Read(d);
    }
    static double Bits(string hex) => BitConverter.Int64BitsToDouble(unchecked((long)Convert.ToUInt64(hex, 16)));
    static double[] Translation(double[] m) => new[] { m[9], m[10], m[11] };
    static FbxSceneNode Node(FbxScene s, string name) => s.Nodes.First(n => n.Name == name);

    static readonly FbxNode[] Rig = {
        Model(1, "Mesh", "Mesh", P("Lcl Translation", 1.0, 2.0, 3.0)), Quad(),
        Model(10, "Root", "LimbNode", P("Lcl Translation", 0.0, 1.0, 0.0)), Model(11, "Child", "LimbNode", P("Lcl Translation", 0.0, 1.0, 0.0), P("Lcl Rotation", 0.0, 0.0, 90.0)),
        Limb(20, "RootAttr", "LimbNode", P("Size", 1.0)), Limb(21, "ChildAttr", "LimbNode", P("Size", 2.0)) };
    static readonly FbxNode[] RigLinks = { C(1, 0), C(10, 0), C(11, 10), C(30, 1), C(20, 10), C(21, 11) };

    // ---- skins ----------------------------------------------------------------------------------------------------
    [Fact]
    public void Weights_are_grouped_per_vertex_sorted_descending_and_stable_on_ties()
    {
        var s = Scene(Rig.Concat(new[] { Skin(40, "Skin"),
            Cluster(41, "RootCluster", new[] { 0, 1, 2, 3 }, new[] { 0.5, 0.25, 1.0, 0.0 }, T(-1, -2, -3), T(0, 1, 0)),
            Cluster(42, "ChildCluster", new[] { 1, 0, 3, 7 }, new[] { 0.5, 0.5, 0.25, 0.9 }, T(-1, -3, -3), D(0, -1, 0, 0, 2, 0)),
            Pose(50, "BindPose", "BindPose", PoseNode(10, T(0, 1, 0)), PoseNode(11, D(0, -1, 0, 0, 2, 0)), PoseNode(1, T(1, 2, 3)), PoseNode(0, T(0, 0, 0)), PoseNode(999, T(9, 9, 9)), PoseNode(30, T(3, 3, 3))) }).ToArray(),
            RigLinks.Concat(new[] { C(41, 40), C(42, 40), C(40, 30), C(10, 41), C(11, 42) }).ToArray());
        var skin = Assert.Single(s.Skins);
        Assert.Equal(new[] { 0, 1 }, skin.Clusters.Select(c => c.Index));
        Assert.Equal(new[] { 0, 2, 4, 5 }, skin.Vertices.Select(v => v.WeightBegin));
        Assert.Equal(new[] { 2, 2, 1, 2 }, skin.Vertices.Select(v => v.NumWeights));
        Assert.Equal(2, skin.MaxWeightsPerVertex);
        // the space counts the entry for vertex 7 (past the mesh) and leaves it zero, as ufbx does
        Assert.Equal(8, skin.Weights.Length);
        Assert.Equal(new[] { (0, 0.5), (1, 0.5), (1, 0.5), (0, 0.25), (0, 1.0), (1, 0.25), (0, 0.0), (0, 0.0) }, skin.Weights.Select(w => (w.ClusterIndex, w.Weight)));
        Assert.Same(Node(s, "Child"), s.Clusters[1].BoneNode);
        Assert.Same(skin, Assert.Single(s.Meshes[0].SkinDeformers));
        // the pose: the root (id 0) is an entry, the unknown id and the Geometry are not; sorted by node index
        var pose = Assert.Single(s.Poses);
        Assert.Equal(new[] { 0, 1, 2, 3 }, pose.BonePoses.Select(b => b.BoneNode.Index));
        Assert.Same(pose, s.Nodes[0].BindPose);
        Assert.Equal(new[] { 0.0, 1.0, 0.0 }, Translation(s.Clusters[0].BindToWorld));
        Assert.Equal(new[] { 1.0, 1.0, 1.0 }, new[] { s.Clusters[0].GeometryToWorldTransform.sx, s.Clusters[0].GeometryToWorldTransform.sy, s.Clusters[0].GeometryToWorldTransform.sz });
    }

    [Fact]
    public void Missing_cluster_matrices_come_from_the_bind_pose()
    {
        var s = Scene(Rig.Concat(new[] { Skin(40, "Skin"),
            Cluster(41, "RootCluster", new[] { 0, 1, 2, 3 }, new[] { 0.5, 0.25, 1.0, 0.0 }),
            Cluster(42, "ChildCluster", new[] { 1, 0, 3 }, new[] { 0.5, 0.5, 0.25 }),
            Pose(50, "BindPose", "BindPose", PoseNode(10, D(2, 2, 2, 0, 1, 0)), PoseNode(1, T(1, 2, 3))) }).ToArray(),
            RigLinks.Concat(new[] { C(41, 40), C(42, 40), C(40, 1), C(10, 41), C(11, 42) }).ToArray());   // the skin hangs on the MODEL here
        var c0 = s.Clusters[0];
        Assert.Equal(new[] { 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 1, 0.0 }, c0.BindToWorld);
        Assert.Equal(new[] { 0.5, 0, 0, 0, 0.5, 0, 0, 0, 0.5, 0.5, 0.5, 1.5 }, c0.MeshNodeToBone);   // inverse(bind) * mesh node to world
        Assert.Same(c0.MeshNodeToBone, c0.GeometryToBone);
        Assert.True(FbxMath.AllZero(s.Clusters[1].BindToWorld));   // Child is not in the pose: zero stays, its inverse is zero
        Assert.True(FbxMath.AllZero(s.Clusters[1].MeshNodeToBone));
        Assert.Equal(4, s.Skins[0].Vertices.Length);   // sized from the node's mesh
        Assert.Null(Node(s, "Child").BindPose);
    }

    [Fact]
    public void Broken_connections_duplicates_and_nans_follow_ufbx()
    {
        double nan = double.NaN;
        var objects = new[] {
            Model(1, "Mesh", "Mesh", P("Lcl Translation", 1.0, 2.0, 3.0), P("GeometricTranslation", 0.5, 0.0, 0.0)), Quad(),
            Model(10, "Root", "LimbNode", P("Lcl Translation", 0.0, 1.0, 0.0)), Model(11, "Child", "LimbNode", P("Lcl Translation", 0.0, 1.0, 0.0)), Limb(20, "RootAttr"), Limb(21, "ChildAttr"),
            Skin(40, "Skin"), Skin(45, "EmptySkin"),
            Cluster(41, "RootCluster", new[] { 0, 1, 2, 3 }, new[] { nan, 0.25, nan, 0.0 }, T(-1, -2, -3), T(0, 1, 0)),
            Cluster(42, "ChildCluster", new[] { 2, 1, 3 }, new[] { 0.5, 0.5, 0.25 }, T(-1, -3, -3), T(0, 2, 0)),
            Cluster(43, "NoBone", new[] { 2, 0 }, new[] { 0.75, 0.5 }, T(0, 0, 0), T(0, 0, 0)),
            Cluster(44, "SecondRoot", new[] { 2, 1 }, new[] { nan, 1.0 }, T(-1, -2, -3), T(0, 1, 0)),
            Cluster(46, "Loose", new[] { 0 }, new[] { 1.0 }, T(4, 4, 4), T(0, 0, 1)),
            Pose(50, "BindPose", "BindPose", PoseNode(10, T(0, 1, 0)), PoseNode(11, T(0, 2, 0))) };
        var s = Scene(objects, new[] { C(1, 0), C(10, 0), C(11, 10), C(30, 1), C(20, 10), C(21, 11),
            C(41, 40), C(42, 40), C(41, 40), C(43, 40), C(44, 40), C(40, 30), C(10, 41), C(11, 42), C(10, 44), C(10, 46) });
        Assert.Equal(5, s.Clusters.Count);
        Assert.Equal(new[] { 0, 1, 3 }, s.Skins[0].Clusters.Select(c => c.Index));   // the duplicate once, NoBone dropped, Loose never connected
        Assert.Null(s.Clusters[2].BoneNode);
        Assert.Contains(s.Warnings, w => w.StartsWith("Duplicate connection"));
        // vertex 2's three weights: NaN never moves in the stable sort, as in ufbx's
        var v2 = s.Skins[0].Weights.Skip(s.Skins[0].Vertices[2].WeightBegin).Take(3).Select(w => (w.ClusterIndex, w.Weight)).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, v2.Select(w => w.Item1));
        Assert.True(double.IsNaN(v2[0].Item2)); Assert.Equal(0.5, v2[1].Item2); Assert.True(double.IsNaN(v2[2].Item2));
        Assert.Equal(new[] { (2, 1.0), (1, 0.5), (0, 0.25) }, s.Skins[0].Weights.Skip(1).Take(3).Select(w => (w.ClusterIndex, w.Weight)));   // vertex 1
        Assert.Empty(s.Skins[1].Vertices);
        // the geometric translation folds into geometry_to_bone; a cluster outside every skin is not patched
        Assert.Equal(new[] { -0.5, -2.0, -3.0 }, Translation(s.Clusters[0].GeometryToBone));
        Assert.Same(s.Clusters[4].MeshNodeToBone, s.Clusters[4].GeometryToBone);
        Assert.Equal(new[] { 4.0, 5.0, 4.0 }, Translation(s.Clusters[4].GeometryToWorld));   // the bone at (0,1,0) times the binding
    }

    [Fact]
    public void Skinning_methods_and_dual_quaternion_weights()
    {
        var s = Scene(new[] { Model(1, "Mesh", "Mesh"), Quad(), Model(10, "Root", "LimbNode"), Limb(20, "RootAttr"),
            Skin(40, "Blend", "Blend", new[] { 1, 9, 3 }, new[] { 0.3, 0.4, 0.5, 0.6 }), Skin(45, "DQ", "DualQuaternion"), Skin(46, "Rigid", "Rigid"), Skin(47, "Linear", "Linear"), Skin(48, "Odd", "Spline"),
            Cluster(41, "C", new[] { 0, 1, 2, 3 }, new[] { 1.0, 1.0, 1.0, 1.0 }, T(0, 0, 0), T(0, 0, 0)) },
            new[] { C(1, 0), C(10, 0), C(30, 1), C(20, 10), C(41, 40), C(41, 45), C(41, 46), C(41, 47), C(41, 48), C(40, 30), C(45, 30), C(46, 30), C(47, 30), C(48, 30), C(10, 41), C(40, 30) });
        Assert.Equal(new[] { 3, 2, 1, 0, 0 }, s.Skins.Select(k => k.SkinningMethod));
        Assert.Equal(3, s.Skins[0].NumDqWeights);   // the shorter of the two arrays
        Assert.Equal(new[] { 0.0, 0.3, 0.0, 0.5 }, s.Skins[0].Vertices.Select(v => v.DqWeight));   // vertex 9 is past the mesh
        Assert.Equal(new[] { 1.0, 1.0, 1.0, 1.0 }, s.Skins[1].Vertices.Select(v => v.DqWeight));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, s.Meshes[0].SkinDeformers.Select(k => k.Index));   // the duplicate connection ignored
    }

    [Fact]
    public void Matrix_to_transform_takes_every_branch_as_ufbx()
    {
        var s = Scene(new[] { Model(1, "Mesh", "Mesh"), Quad(), Model(10, "Root", "LimbNode", P("Lcl Translation", 0.0, 1.0, 0.0), P("Lcl Rotation", 30.0, 0.0, 0.0)), Limb(20, "RootAttr"), Skin(40, "Skin"),
            Cluster(41, "Trace", new[] { 0 }, new[] { 1.0 }, T(1, 0, 0), T(0, 1, 0)),
            Cluster(42, "X", new[] { 0 }, new[] { 1.0 }, D(1, -1, -1, 1, 2, 3), T(0, 1, 0)),
            Cluster(43, "Y", new[] { 0 }, new[] { 1.0 }, D(-1, 1, -1), T(0, 1, 0)),
            Cluster(44, "Z", new[] { 0 }, new[] { 1.0 }, D(-1, -1, 1), T(0, 1, 0)),
            Cluster(45, "Mirror", new[] { 0 }, new[] { 1.0 }, D(-2, 3, 4, 1, 1, 1), T(0, 1, 0)),
            Cluster(46, "ZeroColumn", new[] { 0 }, new[] { 1.0 }, D(0, 2, 2), T(0, 1, 0)),
            Cluster(47, "Singular", new[] { 0 }, new[] { 1.0 }),
            Pose(50, "BindPose", "BindPose", PoseNode(10, D(1, 1, 0, 0, 1, 0))) },
            new[] { C(1, 0), C(10, 0), C(30, 1), C(20, 10), C(40, 30) }.Concat(new long[] { 41, 42, 43, 44, 45, 46, 47 }.SelectMany(c => new[] { C(c, 40), C(10, c) })).ToArray());
        double s15 = Bits("3fd0907dc193068f"), c15 = Bits("3feee8dd4748bf15");   // the bone's 30 degrees about X, halved
        void Q(int cluster, double x, double y, double z, double w) { var t = s.Clusters[cluster].GeometryToWorldTransform; Assert.Equal(new[] { x, y, z, w }, new[] { t.qx, t.qy, t.qz, t.qw }); }
        Q(0, s15, 0, 0, c15);
        Q(1, c15, 0, 0, -s15);
        Q(2, 0, c15, s15, 0);
        Q(3, 0, -s15, c15, 0);
        Q(4, s15, 0, -0.0, c15);
        var m = s.Clusters[4].GeometryToWorldTransform; Assert.Equal(new[] { -2.0, 3.0, 4.0 }, new[] { m.sx, m.sy, m.sz });   // the negative determinant flips x
        Q(5, Bits("3fd8ff050fd8cef3"), 0, 0, Bits("3ff1129c9deb50c1"));   // a zero column: ufbx's renormalisation divides by the squared length
        var z = s.Clusters[5].GeometryToWorldTransform; Assert.Equal(new[] { 0.0, 2.0, 2.0 }, new[] { z.sx, z.sy, z.sz });
        Assert.True(FbxMath.AllZero(s.Clusters[6].MeshNodeToBone));   // the singular bind pose inverts to zero
        Q(6, 0, 0, 2, 0);
    }

    [Fact]
    public void Exporter_detection_and_bone_units()
    {
        var bones = new[] { Model(10, "A", "LimbNode"), Model(11, "B", "LimbNode"), Model(12, "Two", "LimbNode"), Model(13, "BoneAndMesh", "LimbNode"), Quad(),
            Limb(20, "Shared", "LimbNode", P("Size", 2.0), P("LimbLength", 0.5)), Limb(21, "First", "Limb", P("Size", 3.0)), Limb(22, "Second", "Root"), Limb(23, "Plain") };
        var links = new[] { C(10, 0), C(11, 0), C(12, 0), C(13, 0), C(20, 10), C(20, 11), C(20, 11), C(21, 12), C(22, 12), C(23, 13), C(30, 13) };
        var cases = new (string creator, int exporter, uint version, double radius, double rel)[] {
            ("Blender (stable FBX IO) - 4.2.3 - 5.12.4", 2, 5012004, 2.0 / 33.0, 0.5),
            ("Blender - 2.79", 2, 2079000, 2.0 / 33.0, 0.5),
            ("Blender version 2.79 (sub 0)", 3, 2079000, 2.0, 1.0),
            ("FBX SDK/FBX Plugins version 2020.3.4", 1, 2020003000, 2.0 / (100.0 / 3.0), 0.5),
            ("FBX SDK/FBX Plugins build 20200312", 1, 2020003012, 2.0 / (100.0 / 3.0), 0.5),
            ("MotionBuilder/Mocap/Online version 7.0", 4, 7000000, 2.0 / (100.0 / 3.0), 0.5),
            ("Some Exporter 1.0", 0, 0, 2.0 / (100.0 / 3.0), 0.5) };
        foreach (var (creator, exporter, version, radius, rel) in cases)
        {
            var s = Scene(bones, links, Header(creator));
            Assert.Equal((exporter, version), (s.Exporter, s.ExporterVersion));
            Assert.Equal(radius, s.Bones[0].Radius);
            Assert.Equal(rel, s.Bones[0].RelativeLength);
        }
        var plain = Scene(bones, links);
        Assert.Equal(0, plain.Exporter);
        Assert.Equal(2, plain.Bones[0].Instances.Count);                    // the duplicate connection counts once
        Assert.Same(plain.Bones[2], Node(plain, "Two").Bone);               // the last bone attribute is the node's
        Assert.True(plain.Bones[2].IsRoot);
        Assert.Equal("bone", Node(plain, "BoneAndMesh").AttribType);        // the first attribute names the type
        Assert.NotNull(Node(plain, "BoneAndMesh").Mesh);
        Assert.Same(plain.Bones[3], Node(plain, "BoneAndMesh").Bone);
    }

    [Fact]
    public void Poses_bind_or_not_duplicates_and_parents_outside_the_pose()
    {
        var s = Scene(new[] { Model(10, "Root", "LimbNode", P("Lcl Translation", 0.0, 1.0, 0.0)), Model(11, "Child", "LimbNode", P("Lcl Translation", 0.0, 1.0, 0.0)),
            Model(12, "Grandchild", "LimbNode", P("Lcl Translation", 0.0, 1.0, 0.0)), Limb(20, "Attr"),
            Model(1, "Mesh", "Mesh"), Quad(), Skin(40, "Skin"), Cluster(41, "C", new[] { 0 }, new[] { 1.0 }),
            Pose(50, "Rest", "Rest", PoseNode(10, T(5, 5, 5)), PoseNode(12, T(6, 6, 6))),
            Pose(51, "First", "BindPose", PoseNode(12, T(0, 3, 0)), PoseNode(10, T(0, 1, 0)), PoseNode(12, T(0, 4, 0)), PoseNode(null, T(7, 7, 7)), PoseNode(11, null), PoseNode(11, T(0, 2, 0).Concat(new[] { 9.0, 9.0, 9.0, 9.0 }).ToArray())),
            Pose(52, "Second", "BindPose", PoseNode(10, T(1, 1, 1)), PoseNode(11, T(2, 2, 2))) },
            new[] { C(10, 0), C(11, 10), C(12, 11), C(20, 10), C(20, 11), C(20, 12), C(1, 0), C(30, 1), C(40, 30), C(41, 40), C(12, 41) });
        Assert.Equal(new[] { false, true, true }, s.Poses.Select(p => p.IsBindPose));
        Assert.Same(s.Poses[1], Node(s, "Root").BindPose);                           // the first bind pose naming it
        Assert.Equal(new[] { 0.0, 3.0, 0.0 }, Translation(s.Clusters[0].BindToWorld));   // the first entry of the first bind pose fills the cluster
        var first = s.Poses[1].BonePoses;
        Assert.Equal(new[] { "Root", "Child", "Grandchild", "Grandchild" }, first.Select(b => b.BoneNode.Name));
        Assert.Equal(new[] { 1.0, 2.0, 3.0, 4.0 }, first.Select(b => b.BoneToWorld[10]));
        Assert.Equal(new[] { 1.0, 1.0, 1.0, 2.0 }, first.Select(b => b.BoneToParent[10]));   // each against the pose's own parent entry (the first of two)
        Assert.Equal(new[] { 5.0, 6.0 }, s.Poses[0].BonePoses.Select(b => b.BoneToWorld[9]));
        Assert.Equal(new[] { 5.0, 4.0 }, s.Poses[0].BonePoses.Select(b => b.BoneToParent[10]));   // Grandchild's parent is not in the pose: its world (0,2,0) is used
        Assert.Equal(new[] { 1.0, 1.0 }, s.Poses[2].BonePoses.Select(b => b.BoneToParent[9]));
    }

    [Fact]
    public void A_mesh_lists_its_instances_in_connection_order_and_a_node_keeps_its_last_mesh()
    {
        var s = Scene(new[] { Model(1, "A", "Mesh"), Model(2, "B", "Mesh"), Quad(),
            Object("Geometry", 31, "Tri", "Mesh", N("Vertices", new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }), N("PolygonVertexIndex", new[] { 0, 1, ~2 }), N("GeometryVersion", 124)) },
            new[] { C(1, 0), C(2, 0), C(30, 2), C(30, 1), C(31, 1) });
        Assert.Equal(new[] { "B", "A" }, s.Meshes[0].Instances.Select(n => n.Name));
        Assert.Same(s.Meshes[1], Node(s, "A").Mesh);
        Assert.Equal(new[] { "A" }, s.Meshes[1].Instances.Select(n => n.Name));
    }

    [Fact]
    public void Malformed_skins_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => Scene(new[] { Model(1, "Mesh", "Mesh"), Quad(), Skin(40, "Skin"), Cluster(41, "C", new[] { 0, 1, 2 }, new[] { 1.0, 1.0 }) },
            new[] { C(1, 0), C(30, 1), C(40, 30), C(41, 40) }));
        Assert.Throws<InvalidDataException>(() => Scene(new[] { Model(10, "Root", "LimbNode"), Pose(50, "BindPose", "BindPose", PoseNode(10, Enumerable.Repeat(1.0, 12).ToArray())) }, new[] { C(10, 0) }));
        Assert.Throws<InvalidDataException>(() => Scene(new[] { Model(1, "Mesh", "Mesh"), Quad(), Skin(40, "Skin"), Cluster(41, "C", new[] { 0 }, new[] { 1.0 }, Enumerable.Repeat(1.0, 9).ToArray(), T(0, 0, 0)) },
            new[] { C(1, 0), C(30, 1), C(40, 30), C(41, 40) }));
    }

    // ---- the math ---------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("OP")]
    [InlineData("PO")]
    [InlineData("PP")]
    public void Property_free_connections_keep_the_object_graph(string kind)
    {
        FbxNode Edge(long src, long dst) => kind == "OP" ? N("C", kind, src, dst, "") :
            kind == "PO" ? N("C", kind, src, "", dst) : N("C", kind, src, "", dst, "");
        var objects = Rig.Concat(new[] { Skin(40, "Skin"), Cluster(41, "C", new[] { 0 }, new[] { 1.0 }),
            Pose(50, "Pose", "BindPose", PoseNode(10, T(0, 1, 0))) }).ToArray();
        var links = RigLinks.Select(c => Edge((long)c.Properties[1], (long)c.Properties[2]))
            .Concat(new[] { Edge(41, 40), Edge(40, 30), Edge(10, 41) }).ToArray();
        var s = Scene(objects, links);
        Assert.Same(Node(s, "Root"), Node(s, "Child").Parent);
        Assert.Same(s.Meshes[0], Node(s, "Mesh").Mesh);
        Assert.Same(Node(s, "Root"), s.Clusters[0].BoneNode);
        Assert.Same(s.Bones[0], Node(s, "Root").Bone);
        Assert.Equal(1, Assert.Single(s.Skins).Vertices[0].NumWeights);
        Assert.Single(s.Meshes[0].SkinDeformers);
        Assert.Equal(new[] { 0.0, 1.0, 0.0 }, Translation(s.Clusters[0].BindToWorld));
    }

    [Fact]
    public void A_root_bound_cluster_keeps_its_weights_and_root_pose()
    {
        var s = Scene(new[] { Model(1, "Mesh", "Mesh", P("Lcl Translation", 1.0, 2.0, 3.0)), Quad(),
            Skin(40, "Skin"), Cluster(41, "C", new[] { 0 }, new[] { 1.0 }),
            Pose(50, "Pose", "BindPose", PoseNode(0, T(0, 2, 0))) },
            new[] { C(1, 0), C(30, 1), C(41, 40), C(40, 30), C(0, 41) });
        Assert.Same(s.Nodes[0], s.Clusters[0].BoneNode);
        Assert.Single(s.Skins[0].Clusters);
        Assert.Equal(1, s.Skins[0].Vertices[0].NumWeights);
        Assert.Equal(new[] { 0.0, 2.0, 0.0 }, Translation(s.Clusters[0].BindToWorld));
        Assert.Equal(new[] { 1.0, 0.0, 3.0 }, Translation(s.Clusters[0].MeshNodeToBone));
    }

    [Theory]
    [InlineData("PO")]
    [InlineData("PP")]
    public void Texture_connections_use_the_decoded_source_and_destination_properties(string kind)
    {
        var edge = kind == "PO" ? N("C", kind, 2L, "", 1L) : N("C", kind, 2L, "", 1L, "DiffuseColor");
        var s = Scene(new[] { Object("Material", 1, "Mat", "", N("ShadingModel", "phong")),
            Object("Texture", 2, "Tex", "", N("RelativeFilename", "tex.png")) }, new[] { edge });
        var t = Assert.Single(Assert.Single(s.Materials).Textures);
        Assert.Equal(kind == "PO" ? "" : "DiffuseColor", t.prop);
        Assert.Equal("tex.png", t.relativeFilename);
    }

    [Fact]
    public void Named_property_connections_do_not_become_skin_bindings()
    {
        var s = Scene(Rig.Concat(new[] { Skin(40, "Skin"), Cluster(41, "C", new[] { 0 }, new[] { 1.0 }) }).ToArray(),
            RigLinks.Concat(new[] { C(41, 40), N("C", "OP", 40L, 30L, "OtherProperty"), C(10, 41) }).ToArray());
        Assert.Empty(s.Skins[0].Vertices);
        Assert.Empty(s.Meshes[0].SkinDeformers);
    }

    [Theory]
    [InlineData("OP")]
    [InlineData("PO")]
    [InlineData("PP")]
    public void Nonstring_property_fields_do_not_create_object_bindings(string kind)
    {
        var edge = kind == "OP" ? N("C", kind, 10L, 41L, 17) :
            kind == "PO" ? N("C", kind, 10L, 17, 41L) : N("C", kind, 10L, 17, 41L, 17);
        var s = Scene(Rig.Concat(new[] { Skin(40, "Skin"), Cluster(41, "C", new[] { 0 }, new[] { 1.0 }) }).ToArray(),
            RigLinks.Concat(new[] { C(41, 40), C(40, 30), edge }).ToArray());
        Assert.Null(s.Clusters[0].BoneNode);
        Assert.Empty(s.Skins[0].Clusters);
    }

    [Fact]
    public void Sorting_ordinary_vertex_weights_has_no_per_vertex_scratch_arrays()
    {
        var allocated = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread"));
        var weights = new FbxSkinWeight[4];
        Func<FbxSkinWeight, FbxSkinWeight, bool> less = (a, b) => a.Weight > b.Weight;
        FbxMath.StableSort(weights, 0, 4, 32, less); // warm up outside the measured region
        long before = allocated();
        for (int i = 0; i < 100000; i++)
        {
            for (int j = 0; j < 4; j++) weights[j].Weight = j;
            FbxMath.StableSort(weights, 0, 4, 32, less);
        }
        Assert.True(allocated() - before < 1000000, "Sorting four influences must not allocate scratch arrays per vertex");
        Assert.Equal(new[] { 3.0, 2.0, 1.0, 0.0 }, weights.Select(w => w.Weight));
    }

    [Fact]
    public void Invert_is_ufbx_matrix_invert()
    {
        Assert.Equal(new[] { 0.5, 0, 0, 0, 0.5, 0, 0, 0, 0.5, 0, -0.5, 0 }, FbxMath.Invert(new[] { 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 1, 0.0 }));
        Assert.True(FbxMath.AllZero(FbxMath.Invert(new[] { 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0.0 })));
        Assert.Equal(8.0, FbxMath.Determinant(new[] { 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 1, 0.0 }));
    }

    [Fact]
    public void Version_strings_match_as_ufbx_matches_them()
    {
        var v = new uint[3];
        Assert.True(FbxScene.MatchVersion("blender-- ?.?.?", "BLENDER (x) - 4.2.3 - 5.12.4", v)); Assert.Equal(new uint[] { 5, 12, 4 }, v);
        Assert.False(FbxScene.MatchVersion("blender-- ?.?.?", "Blender - 2.79", v));            // only one dash
        Assert.True(FbxScene.MatchVersion("blender- ?.?", "Blender - 2.79", v)); Assert.Equal(new uint[] { 2, 79, 4 }, v);
        Assert.False(FbxScene.MatchVersion("fbx sdk/fbx plugins version ?.?", "FBX SDK/FBX Plugins version x", v));
        Assert.False(FbxScene.MatchVersion("blender version ?.?", "Blender", v));
    }

    [Fact]
    public void StableSort_is_ufbx_s_insertion_plus_merge()
    {
        var rng = new Random(7);
        var data = Enumerable.Range(0, 200).Select(i => (key: rng.Next(0, 9), i)).ToArray();
        var expected = data.OrderByDescending(x => x.key).ToArray();   // LINQ's OrderBy is stable
        FbxMath.StableSort(data, 0, data.Length, 32, (a, b) => a.key > b.key);
        Assert.Equal(expected, data);
        var part = new[] { 5, 9, 1, 8, 2, 7 };
        FbxMath.StableSort(part, 1, 4, 2, (a, b) => a < b);
        Assert.Equal(new[] { 5, 1, 2, 8, 9, 7 }, part);
    }
}
