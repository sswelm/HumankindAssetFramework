using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>The node list Blender's glTF exporter writes (BlenderExportTree): each ordering rule on a case where it alone
/// decides, and the transform rule on values read off Blender. tools/prep_drill.sh holds whole files to Blender's own.</summary>
public class BlenderExportTreeTests
{
    static HafNode Node(string name, int mesh = -1, int skin = -1, params int[] children) { var n = new HafNode { Name = name, Mesh = mesh, Skin = skin }; n.Children.AddRange(children); return n; }
    static HafMesh Mesh(string name) { var me = new HafMesh { Name = name }; me.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[9], Indices = new[] { 0, 1, 2 } }); return me; }
    static HafModel Model(IEnumerable<HafMesh> meshes, IEnumerable<HafNode> nodes, params int[] roots)
    {
        var m = new HafModel(); m.Meshes.AddRange(meshes); m.Nodes.AddRange(nodes);
        for (int i = 0; i < m.Nodes.Count; i++) foreach (var c in m.Nodes[i].Children) m.Nodes[c].Parent = i;
        var sc = new HafScene(); sc.Nodes.AddRange(roots.Length > 0 ? roots : Enumerable.Range(0, m.Nodes.Count).Where(i => m.Nodes[i].Parent < 0)); m.Scenes.Add(sc); m.Scene = 0;
        return m;
    }
    static float[][] Identities(int n) => Enumerable.Range(0, n).Select(_ => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }).ToArray();
    static BlenderExportTree.Result Tree(HafModel m) => BlenderExportTree.Build(m, BlenderNames.Compute(m), VehicleProbe.BlenderWorldMatrices(m, null));

    [Theory]
    [InlineData("a", "B", -1)]        // case does not count
    [InlineData("a10", "a9", -1)]     // byte by byte, not natural
    [InlineData("A.001", "a10", -1)]  // '.' before '1'
    [InlineData("b", "B", 0)]
    [InlineData("Ärmel", "zz", 1)]    // a non-ASCII byte is a negative char: it sorts BEFORE every letter... as the FIRST operand here it is greater? no: 0xC3 as sbyte is -61 < 'z'
    public void Blenders_id_sort_compares_signed_bytes_after_an_ascii_tolower(string a, string b, int expected)
    {
        if (a == "Ärmel") { Assert.Equal(-1, BlenderExportTree.StrCaseCmp(a, b)); Assert.Equal(1, BlenderExportTree.StrCaseCmp(b, a)); return; }
        Assert.Equal(expected, BlenderExportTree.StrCaseCmp(a, b));
        Assert.Equal(-expected, BlenderExportTree.StrCaseCmp(b, a));
    }

    [Fact]
    public void Scene_roots_keep_creation_order_and_children_are_sorted_by_name_each_subtree_before_its_parent()
    {
        // the order_try measurement of 2026-10-07: roots Zulu, alpha, Bravo, b2 stay as created; children c2, C1, b, A,
        // a10, a9 come out A, a10, a9, b, C1, c2; every child before its parent; a root after its whole subtree
        var meshes = new List<HafMesh>(); var nodes = new List<HafNode>(); var roots = new List<int>();
        foreach (var r in new[] { "Zulu", "alpha", "Bravo", "b2" })
        {
            int ri = nodes.Count; roots.Add(ri); nodes.Add(Node(r));
            foreach (var c in new[] { "c2", "C1", "b", "A", "a10", "a9" })
            {
                meshes.Add(Mesh(r + "_" + c)); nodes[ri].Children.Add(nodes.Count); nodes.Add(Node(c, meshes.Count - 1));
            }
        }
        var t = Tree(Model(meshes, nodes, roots.ToArray()));
        var names = t.Nodes.Select(n => n.Name).ToList();
        Assert.Equal(new[] { "A", "a10", "a9", "b", "C1", "c2", "Zulu", "A.001", "a10.001", "a9.001", "b.001", "C1.001", "c2.001", "alpha" }, names.Take(14));
        Assert.Equal("b2", names[27]);
        Assert.Equal(new[] { 6, 13, 20, 27 }, t.SceneRoots);
        Assert.All(Enumerable.Range(0, 6), i => Assert.Equal(6, t.Nodes[i].Parent));
        Assert.True(t.Nodes.Take(6).All(n => n.HasMesh) && !t.Nodes[6].HasMesh);
        Assert.Equal(28, t.Nodes.Count);
    }

    [Fact]
    public void A_skins_joints_are_indexed_through_the_mesh_before_it_deepest_first_and_the_armature_last()
    {
        // the skin8 fixture as Blender wrote it: Holder, j7 ... j0, Skinned, chain (children [0, 9, 8]) - the armature made
        // from the dummy root holds every top-level object; the mesh node's skin reaches the bones before the mesh node
        // itself is appended; the armature closes its subtree
        var nodes = new List<HafNode> { Node("Holder", -1, -1, 1), Node("Skinned", 0, 0) };
        for (int i = 0; i < 8; i++) nodes.Add(Node("j" + i));
        for (int i = 0; i < 7; i++) nodes[2 + i].Children.Add(3 + i);
        var m = Model(new[] { Mesh("quad") }, nodes, 0, 2);
        var sk = new HafSkin { Name = "chain", Joints = Enumerable.Range(2, 8).ToArray(), Skeleton = 2 }; m.Skins.Add(sk);
        m.Meshes[0].Primitives[0].Joints = new ushort[12]; m.Meshes[0].Primitives[0].Weights = new float[12]; for (int v = 0; v < 3; v++) m.Meshes[0].Primitives[0].Weights[4 * v] = 1f;
        var t = Tree(m);
        Assert.Equal(new[] { "Holder", "j7", "j6", "j5", "j4", "j3", "j2", "j1", "j0", "Skinned", "chain" }, t.Nodes.Select(n => n.Name));
        Assert.Equal(new[] { 10 }, t.SceneRoots);   // the dummy root became the armature: every top-level node hangs from it
        Assert.Equal(10, t.Nodes[0].Parent); Assert.Equal(10, t.Nodes[9].Parent); Assert.Equal(10, t.Nodes[8].Parent); Assert.Equal(8, t.Nodes[7].Parent);
        Assert.All(t.Nodes.Where(n => n.BoneNode >= 0), n => { Assert.True(n.TransformKnown); Assert.NotNull(n.InverseBind); Assert.Equal(10, n.ArmatureNode); });
        // the neutral bone, when a mesh of the armature needs it: after the bones, before the mesh
        var tn = BlenderExportTree.Build(m, BlenderNames.Compute(m), VehicleProbe.BlenderWorldMatrices(m, null), null, new HashSet<int> { -1 });   // the dummy root's armature is -1
        Assert.Equal(new[] { "j7", "j6", "j5", "j4", "j3", "j2", "j1", "j0", "neutral_bone", "Skinned", "chain" }, tn.Nodes.Skip(1).Select(n => n.Name));
        Assert.True(tn.Nodes[9].NeutralBone); Assert.Equal(11, tn.Nodes[9].ArmatureNode);
    }

    [Fact]
    public void An_armatures_bones_are_indexed_before_it_even_when_no_mesh_uses_its_skin()
    {
        // the dug-out canoe: a skin no mesh node uses, so the joints are reached only as the armature's own members - they
        // still come before the armature (the first version appended the armature first; the full run found it)
        var nodes = new List<HafNode> { Node("Arm", -1, -1, 1, 3), Node("Part", 0), Node("Root", -1, -1, 4), Node("Lone", 1), Node("Tip") };
        nodes[0].Children.Clear(); nodes[0].Children.AddRange(new[] { 1, 2, 3 });
        var m = Model(new[] { Mesh("part"), Mesh("lone") }, nodes, 0);
        m.Skins.Add(new HafSkin { Joints = new[] { 2, 4 }, Skeleton = 2 });   // used by nobody
        var t = Tree(m);
        Assert.Equal(new[] { "Lone", "Part", "Tip", "Root", "Arm" }, t.Nodes.Select(n => n.Name));
        Assert.Equal(4, t.Nodes[3].Parent); Assert.Equal(3, t.Nodes[2].Parent); Assert.Equal(4, t.Nodes[2].ArmatureNode);
    }

    [Fact]
    public void A_skinned_mesh_hangs_from_its_armature_with_the_identity_and_a_faceless_mesh_is_a_node_without_one()
    {
        var nodes = new List<HafNode> { Node("Holder", -1, -1, 1, 2), Node("Body", 0, 0), Node("Joint") };
        nodes[1].Rotation = new double[] { 0, 0.3826834, 0, 0.9238795 };
        var m = Model(new[] { Mesh("tri") }, nodes, 0);
        m.Skins.Add(new HafSkin { Joints = new[] { 2 }, Skeleton = 2 });
        m.Meshes[0].Primitives[0].Joints = new ushort[12]; m.Meshes[0].Primitives[0].Weights = new float[12];
        var t = Tree(m);
        var mixed = t.Nodes.Single(n => n.Name == "Body");
        Assert.Null(mixed.Rotation); Assert.Null(mixed.Translation); Assert.Null(mixed.Scale);   // moved under the armature: no transform of its own
        var withoutFaces = BlenderExportTree.Build(m, BlenderNames.Compute(m), VehicleProbe.BlenderWorldMatrices(m, null), new HashSet<int>(), null);
        Assert.False(withoutFaces.Nodes.Single(n => n.Name == "Body").HasMesh);
        Assert.Equal(new[] { "Joint", "Body", "Holder" }, t.Nodes.Select(n => n.Name));
        Assert.Equal(new[] { "Body", "Joint", "Holder" }, withoutFaces.Nodes.Select(n => n.Name));
    }

    [Fact]
    public void A_faceless_skinned_mesh_still_hangs_from_its_armature_with_the_identity_only_its_index_moves_before_the_joints()
    {
        // measured 2026-10-07 (the export_skin_lines fixture, Body under a turned Holder with a transform of its own): the
        // armature modifier moves the object under the armature, faces or not; without a mesh the node has no skin, so the
        // serializer does not reach the joints through it and Body precedes them
        var nodes = new List<HafNode> { Node("Rig", -1, -1, 2), Node("Body", 0, 0), Node("Joint"), Node("Holder", -1, -1, 1) };
        nodes[1].Translation = new double[] { 0.5, 0, 0 }; nodes[1].Rotation = new double[] { 0, 0.3826834, 0, 0.9238795 };
        nodes[3].Translation = new double[] { 0, 3, 0 }; nodes[3].Rotation = new double[] { 0.3826834, 0, 0, 0.9238795 };
        var m = Model(new[] { Mesh("lines") }, nodes, 0, 3);
        m.Skins.Add(new HafSkin { Joints = new[] { 2 }, Skeleton = 2 });
        m.Meshes[0].Primitives[0].Joints = new ushort[12]; m.Meshes[0].Primitives[0].Weights = new float[12];
        var t = BlenderExportTree.Build(m, BlenderNames.Compute(m), VehicleProbe.BlenderWorldMatrices(m, null), new HashSet<int>(), null);
        Assert.Equal(new[] { "Body", "Joint", "Rig", "Holder" }, t.Nodes.Select(n => n.Name));
        var body = t.Nodes.Single(n => n.Name == "Body");
        Assert.Equal("Rig", t.Nodes[body.Parent].Name);
        Assert.False(body.HasMesh);
        Assert.Null(body.Translation); Assert.Null(body.Rotation); Assert.Null(body.Scale);
        Assert.Equal(new[] { 2, 3 }, t.SceneRoots);
    }

    [Fact]
    public void Materials_are_listed_at_first_use_along_the_walk_and_named_as_the_importer_names_them()
    {
        // the names fixture: mesh 0 (material 2 "same") on the root and its grandchild, mesh 1 (an EMPTY name) on the child;
        // the grandchild's mesh is reached first (deepest first), so "same" is first... no: Blender wrote Material, same -
        // the deepest node (Ärmel.001) carries mesh 1 (material 0, named ""): "Material" first
        var m = Model(new[] { Mesh("m0"), Mesh("m1") }, new[] { Node("Ärmel", 0, -1, 1), Node("", 0, -1, 2), Node("Ärmel", 1) }, 0);
        m.Materials.Add(new HafMaterial { Name = "" }); m.Materials.Add(new HafMaterial { Name = "same" }); m.Materials.Add(new HafMaterial { Name = "same" });
        m.Meshes[0].Primitives[0].Material = 2; m.Meshes[1].Primitives[0].Material = 0;
        var names = BlenderNames.Compute(m);
        Assert.Equal("Material", names.MaterialOf[(-1, 0, false)]);     // an empty name: Blender's default for the datablock
        Assert.Equal("same", names.MaterialOf[(-1, 2, false)]);         // the second "same" is the only one used, so no suffix
        Assert.False(names.MaterialOf.ContainsKey((-1, 1, false)));     // never used: never made
        var t = BlenderExportTree.Build(m, names, VehicleProbe.BlenderWorldMatrices(m, null), null, null, mn => new[] { names.MaterialOf[(-1, m.Meshes[m.Nodes[mn].Mesh].Primitives[0].Material, false)] });
        Assert.Equal(new[] { "Material", "same" }, t.Materials);
        // an absent name: Material_<index>; a coloured primitive without a material: DefaultMaterial, one per mesh
        var m2 = Model(new[] { Mesh("a"), Mesh("b") }, new[] { Node("A", 0), Node("B", 1) });
        m2.Materials.Add(new HafMaterial { NameAbsent = true });
        m2.Meshes[0].Primitives[0].Material = 0; m2.Meshes[0].Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[9], Colors = new float[12] });
        m2.Meshes[1].Primitives[0].Colors = new float[12];
        var n2 = BlenderNames.Compute(m2);
        Assert.Equal(new[] { "Material_0", "DefaultMaterial", "DefaultMaterial.001" }, n2.MaterialsInOrder);
        Assert.Equal("DefaultMaterial", n2.MaterialOf[(0, -1, true)]); Assert.Equal("DefaultMaterial.001", n2.MaterialOf[(1, -1, true)]);
    }

    [Fact]
    public void A_transform_is_the_local_matrix_decomposed_turned_Y_up_and_snapped_within_2e_6()
    {
        // the names fixture as Blender wrote it: the child of a root scaled by 2, placed at (0, 0, 1) in its parent's space,
        // comes out at 0.99999994 (parent.matrix_world.inverted_safe() @ matrix_world in float32), the root's quaternion
        // renormalized to (0, 0.707106829, 0, 0.707106709)
        var m = Model(new[] { Mesh("m") }, new[] { Node("Root", 0, -1, 1), Node("Mid", 0, -1, 2), Node("Leaf", 0) }, 0);
        m.Nodes[0].Translation = new double[] { 1, 0, 0 }; m.Nodes[0].Rotation = new double[] { 0, 0.7071068, 0, 0.7071068 };
        m.Nodes[1].Scale = new double[] { 2, 2, 2 }; m.Nodes[2].Translation = new double[] { 0, 0, 1 };
        var t = Tree(m);
        var leaf = t.Nodes[0]; var mid = t.Nodes[1]; var root = t.Nodes[2];
        Assert.Equal("Leaf", leaf.Name);
        Assert.Equal(new[] { 0f, 0f, 0.99999994f }, leaf.Translation); Assert.Null(leaf.Rotation); Assert.Null(leaf.Scale);
        Assert.Equal(new[] { 2f, 2f, 2f }, mid.Scale); Assert.Null(mid.Translation);
        Assert.Equal(new[] { 1f, 0f, 0f }, root.Translation);
        Assert.Equal(new[] { 0f, 0.707106829f, 0f, 0.707106709f }, root.Rotation);
        // snapping: a component within 2e-6 of its identity value is that value, and an all-identity property is left out
        VehicleProbe.ExporterTrs(null, new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 1e-6f, -1.5e-6f, 3e-6f, 1 }, out var tr, out var ro, out var sc);
        Assert.Equal(new[] { 0f, 3e-6f, 0f }, tr); Assert.Null(ro); Assert.Null(sc);
    }
}
