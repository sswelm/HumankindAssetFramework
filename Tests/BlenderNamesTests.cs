using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>BlenderNames against the REAL importer's answers: every expected name below was read off Blender 5.1
/// (bpy.data.objects after import, and the Vehicle Lab probe's PART rows) on fixtures built for the rule in
/// question, 2026-10-02. A rule the importer does not have is not here.</summary>
public class BlenderNamesTests
{
    static HafNode Node(string name, int mesh = -1, int skin = -1, params int[] children) { var n = new HafNode { Name = name, Mesh = mesh, Skin = skin }; n.Children.AddRange(children); return n; }
    static HafMesh Mesh(string name) { var me = new HafMesh { Name = name }; me.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[9] }); return me; }
    static HafModel Model(IEnumerable<HafMesh> meshes, IEnumerable<HafNode> nodes)
    {
        var m = new HafModel(); m.Meshes.AddRange(meshes); m.Nodes.AddRange(nodes);
        for (int i = 0; i < m.Nodes.Count; i++) foreach (var c in m.Nodes[i].Children) m.Nodes[c].Parent = i;
        return m;
    }

    [Fact]
    public void A_nameless_node_takes_its_mesh_name_or_Mesh_index_and_duplicates_count_up()
    {
        // the "names" fixture: Blender listed Ärmel "x" \ 日本, Mesh_0, Ärmel "x" \ 日本.001
        var m = Model(new[] { Mesh(""), Mesh("") }, new[] { Node("Ärmel \"x\" \\ 日本", 0, -1, 1), Node("", 0, -1, 2), Node("Ärmel \"x\" \\ 日本", 1) });
        var r = BlenderNames.Compute(m);
        Assert.Equal(new[] { "Ärmel \"x\" \\ 日本", "Mesh_0", "Ärmel \"x\" \\ 日本.001" }, r.MeshObjectOfNode);
    }

    [Fact]
    public void A_taken_name_gets_the_smallest_free_number_of_its_base()
    {
        // the "naming" fixture: hull, hull.001, Mesh_1, Foo.001, Foo, Foo.002, L*70, L*70+X, hull.002, Mesh_1.001
        string l70 = new string('L', 70);
        var m = Model(new[] { Mesh("hull"), Mesh("") },
            new[] { Node("", 0), Node("", 0), Node("", 1), Node("Foo.001", 1), Node("Foo", 1), Node("Foo", 1), Node(l70, 1), Node(l70 + "X", 1), Node("hull", 1), Node("Mesh_1", 1) });
        var r = BlenderNames.Compute(m);
        Assert.Equal(new[] { "hull", "hull.001", "Mesh_1", "Foo.001", "Foo", "Foo.002", l70, l70 + "X", "hull.002", "Mesh_1.001" }, r.MeshObjectOfNode);
    }

    [Fact]
    public void Creation_is_depth_first_from_the_parentless_nodes_in_index_order_empties_included()
    {
        // the "order" fixture: scene lists [5, 0, 4] but Blender numbered N(0) N.001(1) N.002(2) N.003(3) N.004(4) N.005(5, an empty) N.006(6, an orphan)
        var m = Model(new[] { Mesh("m") }, new[] { Node("N", 0, -1, 1, 3), Node("N", 0, -1, 2), Node("N", 0), Node("N", 0), Node("N", 0), Node("N"), Node("N", 0) });
        var sc = new HafScene(); sc.Nodes.AddRange(new[] { 5, 0, 4 }); m.Scenes.Add(sc); m.Scene = 0;
        var r = BlenderNames.Compute(m);
        Assert.Equal(new[] { "N", "N.001", "N.002", "N.003", "N.004", "N.005", "N.006" }, r.ObjectOfNode);
        Assert.Equal(new[] { "N", "N.001", "N.002", "N.003", "N.004", null, "N.006" }, r.MeshObjectOfNode);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 6 }, r.MeshObjectsInOrder.Select(x => x.node));
        // the "order2" fixture: node 0 is node 2's child; the parentless nodes are 1 and 2 -> N(1) N.001(2) N.002(0)
        var m2 = Model(new[] { Mesh("m") }, new[] { Node("N", 0), Node("N", 0), Node("N", 0, -1, 0) });
        Assert.Equal(new[] { "N.002", "N", "N.001" }, BlenderNames.Compute(m2).MeshObjectOfNode);
    }

    [Fact]
    public void An_armature_at_the_root_takes_the_skin_name_first_then_its_bone_shape_then_the_meshes()
    {
        // the "order3" fixture: skin "Body" with joint 1 (parentless, so the dummy root becomes the armature "Body"), the
        // importer's Icosphere, then node 0 (skinned "Body" -> Body.001), node 2 ("Icosphere" -> Icosphere.001), node 3 ("Body" -> Body.002)
        var m = Model(new[] { Mesh("m"), Mesh("m2") }, new[] { Node("Body", 0, 0), Node("Body"), Node("Icosphere", 1), Node("Body", 1) });
        m.Skins.Add(new HafSkin { Name = "Body", Joints = new[] { 1 } });
        var r = BlenderNames.Compute(m);
        Assert.Equal("Body", r.ArmatureOfSkin[0]); Assert.Equal(-1, r.ArmatureNodeOfSkin[0]);
        Assert.Equal(new[] { "Icosphere" }, r.BoneShapes);
        Assert.Equal(new[] { "Body.001", null, "Icosphere.001", "Body.002" }, r.MeshObjectOfNode);
        Assert.Null(r.ObjectOfNode[1]);   // a joint is a bone, not an object
        Assert.Equal("Body", r.BoneOfJoint[1]);
        Assert.Equal(new[] { 0, 2, 3 }, r.MeshObjectsInOrder.Select(x => x.node));   // the probe listed Body.001, Icosphere.001, Body.002
    }

    [Fact]
    public void A_joint_chain_under_the_root_makes_the_root_the_armature_and_nameless_joints_Node_index()
    {
        // the "skin8" fixture: Holder(0) > Skinned(1, skinned); j0(2) > j1(3) > ... > j7(9); skin "chain" -> armature "chain", Icosphere, Holder, Skinned
        var nodes = new List<HafNode> { Node("Holder", -1, -1, 1), Node("Skinned", 0, 0) };
        for (int i = 0; i < 8; i++) nodes.Add(Node("j" + i, -1, -1, i < 7 ? new[] { 3 + i } : new int[0]));
        var m = Model(new[] { Mesh("quad") }, nodes);
        m.Skins.Add(new HafSkin { Name = "chain", Joints = Enumerable.Range(2, 8).ToArray(), Skeleton = 2 });
        var r = BlenderNames.Compute(m);
        Assert.Equal("chain", r.ArmatureOfSkin[0]);
        Assert.Equal("Skinned", r.MeshObjectOfNode[1]); Assert.Equal("Holder", r.ObjectOfNode[0]);
        for (int i = 0; i < 8; i++) Assert.Equal("j" + i, r.BoneOfJoint[2 + i]);
        // nameless joints: Node_<index> (the Khronos SimpleSkin: bones Node_1, Node_2)
        var m2 = Model(new[] { Mesh("") }, new[] { Node("", 0, 0), Node("", -1, -1, 2), Node("") });
        m2.Skins.Add(new HafSkin { Name = "", Joints = new[] { 1, 2 } });
        var r2 = BlenderNames.Compute(m2);
        Assert.Equal("Node_1", r2.BoneOfJoint[1]); Assert.Equal("Node_2", r2.BoneOfJoint[2]); Assert.Equal("Armature", r2.ArmatureOfSkin[0]);
    }

    [Fact]
    public void An_animated_or_parenting_skinned_node_stays_an_empty_and_its_mesh_object_is_named_after_the_mesh()
    {
        // node 0 "Tank" skinned by skin 0, with a child: not moved; a ".skinned" vnode appended to the armature carries the mesh "hull_mesh"
        var m = Model(new[] { Mesh("hull_mesh") }, new[] { Node("Tank", 0, 0, 1), Node("Light"), Node("Root", -1, -1, 3), Node("Bone") });
        m.Skins.Add(new HafSkin { Name = "Rig", Joints = new[] { 3 } });   // armature = node 2 "Root" (the joint's parent)
        var r = BlenderNames.Compute(m);
        Assert.Equal("Tank", r.ObjectOfNode[0]); Assert.Equal("hull_mesh", r.MeshObjectOfNode[0]);
        Assert.Equal("Root", r.ArmatureOfSkin[0]); Assert.Equal(2, r.ArmatureNodeOfSkin[0]);
        Assert.Equal(new[] { 0 }, r.MeshObjectsInOrder.Select(x => x.node));
        // the same node animated (a channel targets it) and childless: also not moved
        var m2 = Model(new[] { Mesh("hull_mesh") }, new[] { Node("Tank", 0, 0), Node("Root", -1, -1, 2), Node("Bone") });
        m2.Skins.Add(new HafSkin { Name = "Rig", Joints = new[] { 2 } });
        var an = new HafAnimation { Name = "spin" }; an.Samplers.Add(new HafSampler { Times = new[] { 0f }, Values = new float[3], Components = 3 }); an.Channels.Add(new HafChannel { Sampler = 0, Node = 0, Path = "translation" }); m2.Animations.Add(an);
        var r2 = BlenderNames.Compute(m2);
        Assert.Equal("Tank", r2.ObjectOfNode[0]); Assert.Equal("hull_mesh", r2.MeshObjectOfNode[0]);
        // and unanimated, childless: moved under the armature, keeps its own name
        var m3 = Model(new[] { Mesh("hull_mesh") }, new[] { Node("Tank", 0, 0), Node("Root", -1, -1, 2), Node("Bone") });
        m3.Skins.Add(new HafSkin { Name = "Rig", Joints = new[] { 2 } });
        Assert.Equal("Tank", BlenderNames.Compute(m3).MeshObjectOfNode[0]);
    }

    [Fact]
    public void A_mesh_on_a_bone_or_armature_node_moves_to_a_child_object_named_after_the_mesh()
    {
        var m = Model(new[] { Mesh("turret_mesh"), Mesh("base_mesh") }, new[] { Node("Root", 1, -1, 1), Node("Turret", 0) });
        m.Skins.Add(new HafSkin { Name = "Rig", Joints = new[] { 1 } });   // joint 1's parent, node 0, is the armature - and carries a mesh
        var r = BlenderNames.Compute(m);
        Assert.Equal("Root", r.ArmatureOfSkin[0]);
        Assert.Equal("base_mesh", r.MeshObjectOfNode[0]); Assert.Equal("turret_mesh", r.MeshObjectOfNode[1]);
        Assert.Null(r.ObjectOfNode[1]);
    }

    [Fact]
    public void The_first_armature_is_the_first_created_not_skin_zeros()
    {
        // the "two_armatures" fixture: Rig2(0) > BoneB(1), Rig1(2) > BoneA(3); skin 0 = SkinA over BoneA, skin 1 = SkinB over BoneB.
        // Blender creates Rig2 first; the Lab's rig report reads `arms[0]` and printed RIGBONE|BoneB
        var m = Model(new[] { Mesh("ma"), Mesh("mb") }, new[] { Node("Rig2", -1, -1, 1), Node("BoneB"), Node("Rig1", -1, -1, 3), Node("BoneA"), Node("MeshA", 0, 0), Node("MeshB", 1, 1) });
        m.Skins.Add(new HafSkin { Name = "SkinA", Joints = new[] { 3 } }); m.Skins.Add(new HafSkin { Name = "SkinB", Joints = new[] { 1 } });
        var r = BlenderNames.Compute(m);
        Assert.Equal(new[] { (0, "Rig2"), (2, "Rig1") }, r.ArmaturesInOrder);
        Assert.Equal(new[] { "Rig1", "Rig2" }, r.ArmatureOfSkin); Assert.Equal(new[] { 2, 0 }, r.ArmatureNodeOfSkin);
        Assert.Equal(0, r.ArmatureNodeOfBone[1]); Assert.Equal(2, r.ArmatureNodeOfBone[3]);
        Assert.Equal(new[] { 5, 4 }, r.MeshObjectsInOrder.Select(x => x.node));   // MeshB hangs under the first armature: Blender listed it first
    }

    [Fact]
    public void A_skin_inside_another_skins_chain_shares_its_armature()
    {
        // the "nested_skins" fixture: Rig(0) > J0(1) > J1(2) > J2(3); skin 0 over J0..J2, skin 1 over J1, J2 - whose would-be
        // armature, J0, is already a bone: one armature, both skins in it
        var m = Model(new[] { Mesh("ma"), Mesh("mb") }, new[] { Node("Rig", -1, -1, 1), Node("J0", -1, -1, 2), Node("J1", -1, -1, 3), Node("J2"), Node("MeshA", 0, 0), Node("MeshB", 1, 1) });
        m.Skins.Add(new HafSkin { Name = "Whole", Joints = new[] { 1, 2, 3 } }); m.Skins.Add(new HafSkin { Name = "Upper", Joints = new[] { 2, 3 } });
        var r = BlenderNames.Compute(m);
        Assert.Single(r.ArmaturesInOrder);
        Assert.Equal(new[] { "Rig", "Rig" }, r.ArmatureOfSkin); Assert.Equal(new[] { 0, 0 }, r.ArmatureNodeOfSkin);
    }

    [Fact]
    public void A_camera_takes_a_name_in_the_pool_before_a_mesh_of_that_name()
    {
        // the "cameras" fixture: a nameless camera node (camera 0, unnamed -> "Camera"), a mesh node named Camera, a node with a
        // mesh AND camera "Lens", a mesh node named Lens. Blender: Camera.001, Both, Lens.001
        var m = Model(new[] { Mesh("m"), Mesh("m2"), Mesh("m3") }, new[] { Node(""), Node("Camera", 0), Node("Both", 1), Node("Lens", 2) });
        m.Cameras.Add("{\"type\":\"perspective\"}"); m.Cameras.Add("{\"type\":\"perspective\",\"name\":\"Lens\"}");
        m.Nodes[0].Camera = 0; m.Nodes[2].Camera = 1;
        var r = BlenderNames.Compute(m);
        Assert.Equal(new[] { null, "Camera.001", "Both", "Lens.001" }, r.MeshObjectOfNode);
        Assert.Equal("Camera", r.ObjectOfNode[0]);
        Assert.True(r.ObjectPool.Contains("Lens"));   // the camera object split off the node that also carries a mesh
    }

    [Fact]
    public void A_camera_datablock_takes_its_name_even_when_its_node_has_one()
    {
        // the "camera_data_names" fixture: cameras Lens, Lens, Wide; nodes Eye(cam 0), nameless(cam 1), mesh Lens, First(cam 2),
        // nameless(cam 2), mesh Wide, mesh Wide.001. Blender's mesh objects: Lens, Wide, Wide.002 - the nameless camera nodes are
        // Lens.001 and Wide.001, after their datablocks (one datablock per camera OBJECT, even for one glTF camera)
        var m = Model(new[] { Mesh("m"), Mesh("m2"), Mesh("m3") }, new[] { Node("Eye"), Node(""), Node("Lens", 0), Node("First"), Node(""), Node("Wide", 1), Node("Wide.001", 2) });
        m.Cameras.Add("{\"name\":\"Lens\"}"); m.Cameras.Add("{\"name\":\"Lens\"}"); m.Cameras.Add("{\"name\":\"Wide\"}");
        m.Nodes[0].Camera = 0; m.Nodes[1].Camera = 1; m.Nodes[3].Camera = 2; m.Nodes[4].Camera = 2;
        var r = BlenderNames.Compute(m);
        Assert.Equal(new[] { null, null, "Lens", null, null, "Wide", "Wide.002" }, r.MeshObjectOfNode);
        Assert.Equal(new[] { "Eye", "Lens.001", "Lens", "First", "Wide.001", "Wide", "Wide.002" }, r.ObjectOfNode);
    }

    // ---- unique names: every expectation below is a row Blender 5.1 gave for the fixture named (tools/vehicle-probe-drill/naming_fixtures.py),
    // except where a line says it follows from Blender's source alone

    static string[] Named(BlenderNames.NamePool pool, params string[] names) => names.Select(pool.Unique).ToArray();

    [Fact]
    public void A_numeric_tail_is_ASCII_digits_that_fit_an_int_behind_the_last_dot()
    {
        Assert.Equal(("B", 7), BlenderNames.SplitNumber("B.7"));
        Assert.Equal(("T.1", 2), BlenderNames.SplitNumber("T.1.2"));
        Assert.Equal(("", 5), BlenderNames.SplitNumber(".5"));                       // a name may START with its dot
        Assert.Equal(("Zero", 0), BlenderNames.SplitNumber("Zero.000"));
        Assert.Equal(("Int", int.MaxValue), BlenderNames.SplitNumber("Int.2147483647"));
        Assert.Equal(("Int.2147483648", 0), BlenderNames.SplitNumber("Int.2147483648"));   // one past an int: part of the name
        Assert.Equal(("Big.99999999999999999999999999", 0), BlenderNames.SplitNumber("Big.99999999999999999999999999"));   // (source: std::stoi out of range; the fixture has 11 digits)
        Assert.Equal(("Hull.\u0661", 0), BlenderNames.SplitNumber("Hull.\u0661"));   // an Arabic-Indic digit is a digit to char.IsDigit, not to Blender
        Assert.Equal(("Deck.\uff11", 0), BlenderNames.SplitNumber("Deck.\uff11"));   // nor is a fullwidth one
        Assert.Equal(("Q.-1", 0), BlenderNames.SplitNumber("Q.-1"));
        Assert.Equal(("C.", 0), BlenderNames.SplitNumber("C."));
        Assert.Equal(("123", 0), BlenderNames.SplitNumber("123"));
    }

    [Fact]
    public void A_duplicate_datablock_takes_its_bases_smallest_free_number_not_its_own_tail_plus_one()
    {
        // the "name_tails" fixture
        var p = new BlenderNames.NamePool();
        Assert.Equal(new[] { "Hull.\u0661", "Hull.\u0661.001" }, Named(p, "Hull.\u0661", "Hull.\u0661"));   // threw FormatException before
        Assert.Equal(new[] { "Deck.\uff11", "Deck.\uff11.001" }, Named(p, "Deck.\uff11", "Deck.\uff11"));
        Assert.Equal(new[] { "Big.99999999999", "Big.99999999999.001" }, Named(p, "Big.99999999999", "Big.99999999999"));   // threw OverflowException before
        Assert.Equal(new[] { "Max.999999", "Max.001" }, Named(p, "Max.999999", "Max.999999"));
        Assert.Equal(new[] { "Over.1000000", "Over.001" }, Named(p, "Over.1000000", "Over.1000000"));
        Assert.Equal(new[] { ".5", ".001" }, Named(p, ".5", ".5"));
        Assert.Equal(new[] { "Zero.000", "Zero.001" }, Named(p, "Zero.000", "Zero.000"));
        Assert.Equal(new[] { "Int.2147483647", "Int.001" }, Named(p, "Int.2147483647", "Int.2147483647"));
        Assert.Equal(new[] { "Int.2147483648", "Int.2147483648.001" }, Named(p, "Int.2147483648", "Int.2147483648"));
        Assert.Equal(new[] { "B.7", "B.001" }, Named(p, "B.7", "B.7"));                    // was B.008
        Assert.Equal(new[] { "Y.009", "Y.002", "Y.001" }, Named(p, "Y.009", "Y.002", "Y.002"));
        Assert.Equal(new[] { "N.999", "N.001" }, Named(p, "N.999", "N.999"));
        Assert.Equal(new[] { "W.1023", "W.001", "W.1024", "W.002" }, Named(p, "W.1023", "W.1023", "W.1024", "W.1024"));
        Assert.Equal(new[] { "T.1.2", "T.1.001" }, Named(p, "T.1.2", "T.1.2"));
        Assert.Equal(new[] { "Q.-1", "Q.-1.001" }, Named(p, "Q.-1", "Q.-1"));
        Assert.Equal(new[] { "123", "123.001" }, Named(p, "123", "123"));
        Assert.Equal(new[] { "P.0", "P.001" }, Named(p, "P.0", "P.0"));
        Assert.Equal(new[] { "K", "K.001", "K.002" }, Named(p, "K", "K.001", "K.001"));
        Assert.Equal(new[] { "G.01", "G.002" }, Named(p, "G.01", "G.01"));                 // the number counts, not its spelling
        Assert.Equal(new[] { "V.005", "V.001", "V.002" }, Named(p, "V.005", "V.005", "V.005"));
        Assert.Equal(new[] { "C.", "C..001" }, Named(p, "C.", "C."));
    }

    [Fact]
    public void A_datablock_name_holds_255_bytes_and_a_duplicate_without_room_for_its_number_is_cut_instead()
    {
        // the "long_names" fixture
        var p = new BlenderNames.NamePool();
        string L(char c, int n) => new string(c, n);
        Assert.Equal(new[] { L('L', 255), L('L', 254) }, Named(p, L('L', 255), L('L', 255)));
        Assert.Equal(new[] { L('M', 255), L('M', 254) }, Named(p, L('M', 300), L('M', 300)));
        Assert.Equal(new[] { L('\u00e9', 127), L('\u00e9', 126) }, Named(p, L('\u00e9', 130), L('\u00e9', 130)));   // 2 bytes each: 254, then 252
        Assert.Equal(new[] { L('S', 255), L('S', 254) + "T" }, Named(p, L('S', 255), L('S', 254) + "T"));
        Assert.Equal(new[] { L('R', 252) + ".05", L('R', 251) }, Named(p, L('R', 252) + ".05", L('R', 252) + ".05"));
        Assert.Equal(new[] { "\U0001F600" + L('a', 251), "\U0001F600" + L('a', 250) }, Named(p, "\U0001F600" + L('a', 300), "\U0001F600" + L('a', 300)));   // a character outside the BMP is 4 bytes, two UTF-16 units
    }

    [Fact]
    public void Past_1023_numbers_a_duplicate_goes_one_above_the_highest_seen_and_with_none_left_gets_an_underscore_base()
    {
        // the "many_names" fixture
        var p = new BlenderNames.NamePool();
        p.Unique("E.5000");
        var e = Enumerable.Range(0, 1025).Select(_ => p.Unique("E")).ToArray();
        Assert.Equal("E", e[0]); Assert.Equal("E.001", e[1]); Assert.Equal("E.1023", e[1023]); Assert.Equal("E.5001", e[1024]);
        p.Unique("F.999999999");
        var f = Enumerable.Range(0, 1025).Select(_ => p.Unique("F")).ToArray();
        Assert.Equal("F.1023", f[1023]); Assert.Equal("F_001", f[1024]);
    }

    [Fact]
    public void A_removed_name_frees_its_number_and_a_copy_of_the_pool_is_its_own()
    {
        // the "split_purged" fixture: bone shapes Icosphere and Icosphere.001 purged, then Icosphere.005 split in three
        var p = new BlenderNames.NamePool();
        Named(p, "Rig1", "Icosphere", "Rig2", "Icosphere", "Icosphere.005");
        var split = p.Clone();
        split.Remove("Icosphere"); split.Remove("Icosphere.001");
        Assert.Equal(new[] { "Icosphere.001", "Icosphere.002" }, Named(split, "Icosphere.005", "Icosphere.005"));
        Assert.True(p.Contains("Icosphere.001")); Assert.False(p.Contains("Icosphere.002"));   // the original pool is untouched
        // two spellings of one number: removing one of them does not free the number (source only: Blender's own note on Mesh.1 / Mesh.001)
        var q = new BlenderNames.NamePool();
        Named(q, "G.1", "G.001");
        q.Remove("G.1");
        Assert.Equal("G.002", q.Unique("G.001"));
    }

    [Fact]
    public void A_duplicate_bone_counts_up_from_its_own_tail_within_63_bytes()
    {
        // the "bone_tails" fixture - the OLDER rule, BLI_uniquename_cb
        var pool = new HashSet<string>();
        string[] Bones(params string[] names) => names.Select(n => BlenderNames.UniqueBone(pool, n)).ToArray();
        Assert.Equal(new[] { "J.7", "J.008" }, Bones("J.7", "J.7"));
        Assert.Equal(new[] { "Bone.\u0661", "Bone.\u0661.001" }, Bones("Bone.\u0661", "Bone.\u0661"));
        Assert.Equal(new[] { "C.009", "C.002", "C.003" }, Bones("C.009", "C.002", "C.002"));
        Assert.Equal(new[] { new string('X', 63), new string('X', 59) + ".001" }, Bones(new string('X', 70), new string('X', 70)));
        Assert.Equal(new[] { new string('\u00e9', 31), new string('\u00e9', 29) + ".001" }, Bones(new string('\u00e9', 40), new string('\u00e9', 40)));
        Assert.Equal(new[] { "D", "D.001" }, Bones("D", "D"));
        Assert.Equal(new[] { "Huge.99999999999", "Huge.99999999999.001" }, Bones("Huge.99999999999", "Huge.99999999999"));
        Assert.Equal(new[] { ".5", ".006" }, Bones(".5", ".5"));
        Assert.Equal(new[] { "Bn.2147483647", "Bn.-2147483648" }, Bones("Bn.2147483647", "Bn.2147483647"));   // C's int wraps, and Blender prints it
    }

    [Fact]
    public void The_bone_shapes_mesh_datablock_takes_the_name_Icosphere_too()
    {
        // the "icosphere_mesh" fixture: Rig > Bone, Body (skinned), Buoy with a mesh named Icosphere, a NAMELESS node with
        // another mesh named Icosphere. Blender's objects: Body, Buoy, Icosphere.002
        var m = Model(new[] { Mesh("body"), Mesh("Icosphere"), Mesh("Icosphere") }, new[] { Node("Rig", -1, -1, 1), Node("Bone"), Node("Body", 0, 0), Node("Buoy", 1), Node("", 2) });
        m.Skins.Add(new HafSkin { Name = "Skin", Joints = new[] { 1 } });
        var r = BlenderNames.Compute(m);
        Assert.Equal(new[] { null, null, "Body", "Buoy", "Icosphere.002" }, r.MeshObjectOfNode);
    }
}
