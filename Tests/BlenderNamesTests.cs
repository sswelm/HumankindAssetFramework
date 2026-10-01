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
    public void Uniquename_splits_a_numeric_tail_and_counts_from_it()
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
    public void Unique_is_BLI_uniquename()
    {
        var pool = new HashSet<string>();
        Assert.Equal("A", BlenderNames.Unique(pool, "A")); Assert.Equal("A.001", BlenderNames.Unique(pool, "A")); Assert.Equal("A.002", BlenderNames.Unique(pool, "A"));
        Assert.Equal("A.003", BlenderNames.Unique(pool, "A.001"));   // the tail is split off and counted up from
        Assert.Equal("B.7", BlenderNames.Unique(pool, "B.7")); Assert.Equal("B.008", BlenderNames.Unique(pool, "B.7"));
        Assert.Equal("C.", BlenderNames.Unique(pool, "C.")); Assert.Equal("C..001", BlenderNames.Unique(pool, "C."));   // no digits after the dot: not a numeric tail
    }
}
