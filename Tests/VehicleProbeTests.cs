using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>VehicleProbe on hand-built models whose expected rows were READ OFF Blender's own probe
/// (vehicle_rig.py probe, Blender 5.1, 2026-10-02) on fixtures of the same shape; the drill
/// (tools/vehicle_probe_drill.sh) holds the two together on real files.</summary>
public class VehicleProbeTests
{
    static HafMesh Tri(string name, float dx = 0, float size = 1) { var me = new HafMesh { Name = name }; me.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new[] { dx, 0, 0, dx + size, 0, 0, dx, size, 0 }, Indices = new[] { 0, 1, 2 } }); return me; }
    static HafModel Link(HafModel m) { for (int i = 0; i < m.Nodes.Count; i++) foreach (var c in m.Nodes[i].Children) m.Nodes[c].Parent = i; return m; }

    [Fact]
    public void A_part_row_is_the_local_box_through_the_world_matrix_in_Blenders_frame()
    {
        // the "scenes" fixture: A at the origin, D under B by a matrix that mirrors X and lifts 2 in glTF Y (= Blender Z)
        // Blender: PART|A|3|0.5,0,0.5|1,0,1   PART|D|3|-0.5,0,2.5|1,0,1   PART|C|3|10.5,0,0.5|1,0,1 - and the order A, B, D, C
        var m = new HafModel(); m.Meshes.Add(Tri("tri"));
        m.Nodes.Add(new HafNode { Name = "A", Mesh = 0 });
        var b = new HafNode { Name = "B", Mesh = 0 }; b.Children.Add(3); m.Nodes.Add(b);
        m.Nodes.Add(new HafNode { Name = "C", Mesh = 0, Translation = new double[] { 10, 0, 0 } });
        m.Nodes.Add(new HafNode { Name = "D", Mesh = 0, Matrix = new double[] { -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 2, 0, 1 } });
        var r = VehicleProbe.Run(Link(m));
        Assert.Equal(new[] { "PART|A|3|0.5000,0.0000,0.5000|1.0000,0.0000,1.0000|1||0", "PART|B|3|0.5000,0.0000,0.5000|1.0000,0.0000,1.0000|1||0",
                             "PART|D|3|-0.5000,0.0000,2.5000|1.0000,0.0000,1.0000|1||0", "PART|C|3|10.5000,0.0000,0.5000|1.0000,0.0000,1.0000|1||0" }, r.Parts.Select(p => p.Row).Select(Clean));
        Assert.False(r.Split); Assert.Empty(r.RigBones);
    }

    [Fact]
    public void A_rotated_part_reports_the_box_of_its_local_box_not_of_its_vertices()
    {
        // the "names" fixture's first node: a quarter turn about glTF Y and a unit along X. Blender: 1.0,0.5,0.5 | 0,1,1
        var m = new HafModel(); m.Meshes.Add(Tri("")); m.Meshes.Add(Tri(""));
        var n0 = new HafNode { Name = "Turned", Mesh = 0, Translation = new double[] { 1, 0, 0 }, Rotation = new double[] { 0, 0.7071068, 0, 0.7071068 } };
        m.Nodes.Add(n0); m.Nodes.Add(new HafNode { Name = "Other", Mesh = 1 });
        var p = VehicleProbe.Run(m).Parts[0];
        Assert.Equal("PART|Turned|3|1.0000,0.5000,0.5000|0.0000,1.0000,1.0000|1||0", Clean(p.Row));
    }

    [Fact]
    public void A_single_mesh_object_is_split_into_its_loose_parts_named_after_it_by_lowest_vertex()
    {
        // the "islands2" fixture: vertices 0-2 at x = 20, 3-5 at x = 10, 6-8 at x = 0, faces listed in the opposite order.
        // Blender: Hull = the x=20 island (2 wide), Hull.001 = x=10 (3 wide), Hull.002 = x=0 (1 wide)
        var me = new HafMesh { Name = "boat" };
        me.Primitives.Add(new HafPrimitive { VertexCount = 9, Positions = new float[] { 20, 0, 0, 22, 0, 0, 20, 2, 0, 10, 0, 0, 13, 0, 0, 10, 3, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Indices = new[] { 6, 7, 8, 3, 4, 5, 0, 1, 2 } });
        var m = new HafModel(); m.Meshes.Add(me); m.Nodes.Add(new HafNode { Name = "Hull", Mesh = 0 });
        var r = VehicleProbe.Run(m);
        Assert.True(r.Split);
        Assert.Equal(new[] { "PART|Hull|3|21.0000,0.0000,1.0000|2.0000,0.0000,2.0000|1||0", "PART|Hull.001|3|11.5000,0.0000,1.5000|3.0000,0.0000,3.0000|1||0", "PART|Hull.002|3|0.5000,0.0000,0.5000|1.0000,0.0000,1.0000|1||0" }, r.Parts.Select(p => Clean(p.Row)));
        Assert.Contains("single mesh split into 3 loose parts (names are synthetic)", r.Notes);
    }

    [Fact]
    public void Only_the_vertices_a_primitive_uses_are_Blenders_vertices()
    {
        // two primitives over ONE vertex array of 8 (the "interleaved" fixture): each uses six. Blender listed two parts of 6 vertices
        // after the split, never the two unused corners as islands of their own.
        var pos = new float[] { 0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0, 0, 1, 0, 1, 1, 0, 2, 1, 0, 3, 1, 0 };
        var me = new HafMesh { Name = "strip" };
        me.Primitives.Add(new HafPrimitive { VertexCount = 8, Positions = pos, Indices = new[] { 0, 1, 5, 0, 5, 4, 1, 2, 6 } });
        me.Primitives.Add(new HafPrimitive { VertexCount = 8, Positions = pos, Indices = new[] { 1, 6, 5, 2, 3, 7, 2, 7, 6 } });
        var m = new HafModel(); m.Meshes.Add(me); m.Nodes.Add(new HafNode { Name = "Strip", Mesh = 0 });
        var r = VehicleProbe.Run(m);
        Assert.Equal(new[] { 6, 6 }, r.Parts.Select(p => p.Verts));
        Assert.Equal(new[] { "Strip", "Strip.001" }, r.Parts.Select(p => p.Name));
    }

    [Fact]
    public void A_skinned_part_reports_its_posed_box_in_the_armatures_space_and_its_dominant_bone()
    {
        // the "skin8" fixture: a unit quad weighted 1/8 to each of eight joints 0.25 apart in a chain, no inverse bind matrices,
        // the skinned node under a parent 100 away (ignored). Blender: PART|Skinned|4|0.5,0,1.375|1,0,1|1|j0|0
        var me = new HafMesh { Name = "quad" };
        var p = new HafPrimitive { VertexCount = 4, Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 }, Indices = new[] { 0, 1, 3, 0, 3, 2 },
            Joints = Enumerable.Repeat(new ushort[] { 0, 1, 2, 3 }, 4).SelectMany(x => x).ToArray(), Weights = Enumerable.Repeat(0.125f, 16).ToArray(),
            Joints1 = Enumerable.Repeat(new ushort[] { 4, 5, 6, 7 }, 4).SelectMany(x => x).ToArray(), Weights1 = Enumerable.Repeat(0.125f, 16).ToArray() };
        me.Primitives.Add(p);
        var m = new HafModel(); m.Meshes.Add(me);
        var holder = new HafNode { Name = "Holder", Translation = new double[] { 100, 0, 0 } }; holder.Children.Add(1); m.Nodes.Add(holder);
        m.Nodes.Add(new HafNode { Name = "Skinned", Mesh = 0, Skin = 0 });
        for (int i = 0; i < 8; i++) { var j = new HafNode { Name = "j" + i, Translation = new double[] { 0, i == 0 ? 0 : 0.25, 0 } }; if (i < 7) j.Children.Add(3 + i); m.Nodes.Add(j); }
        m.Skins.Add(new HafSkin { Name = "chain", Joints = Enumerable.Range(2, 8).ToArray(), Skeleton = 2 });
        var r = VehicleProbe.Run(Link(m));
        Assert.Equal("PART|Skinned|4|0.5000,0.0000,1.3750|1.0000,0.0000,1.0000|1|j0|0", Clean(r.Parts.Single().Row));
        Assert.Equal("chain", r.Armature);
        Assert.Empty(r.RigBones);   // no vertex has a bone over 0.5: not fast-path material (Blender printed no RIGBONE row)
    }

    [Fact]
    public void A_rigged_source_lists_its_bones_from_the_undeformed_vertices()
    {
        // the "order3" fixture: one triangle fully weighted to joint "Body". Blender: RIGBONE|Body|3|0.5,0,0.5|1,0,1 and the part's bone "Body"
        var me = new HafMesh { Name = "m" };
        me.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Joints = new ushort[12], Weights = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 } });
        var m = new HafModel(); m.Meshes.Add(me); m.Meshes.Add(Tri("m2"));
        m.Nodes.Add(new HafNode { Name = "Body", Mesh = 0, Skin = 0 }); m.Nodes.Add(new HafNode { Name = "Body" }); m.Nodes.Add(new HafNode { Name = "Icosphere", Mesh = 1 }); m.Nodes.Add(new HafNode { Name = "Body", Mesh = 1 });
        m.Skins.Add(new HafSkin { Name = "Body", Joints = new[] { 1 } });
        var r = VehicleProbe.Run(m);
        Assert.Equal("RIGBONE|Body|3|0.5000,0.0000,0.5000|1.0000,0.0000,1.0000", Clean(r.RigBones.Single().Row));
        Assert.Equal(new[] { "PART|Body.001|3|0.5000,0.0000,0.5000|1.0000,0.0000,1.0000|1|Body|0", "PART|Icosphere.001|3|0.5000,0.0000,0.5000|1.0000,0.0000,1.0000|1||0", "PART|Body.002|3|0.5000,0.0000,0.5000|1.0000,0.0000,1.0000|1||0" }, r.Parts.Select(p => Clean(p.Row)));
    }

    [Fact]
    public void A_real_part_with_the_bone_shapes_signature_is_purged_as_the_script_purges_it()
    {
        // vehicle_rig.py purges by SIGNATURE: named Icosphere*, no vertex groups, an icosphere's vertex count (12/42/162/642), near-cubic extents
        var ico = new HafMesh { Name = "ico" }; var pos = new float[36]; for (int i = 0; i < 12; i++) { pos[i * 3] = i % 2; pos[i * 3 + 1] = (i / 2) % 2; pos[i * 3 + 2] = (i / 4) % 2 * 0.9f; }
        ico.Primitives.Add(new HafPrimitive { VertexCount = 12, Positions = pos });
        var m = new HafModel(); m.Meshes.Add(ico); m.Meshes.Add(Tri("a")); m.Meshes.Add(Tri("b", 5));
        m.Nodes.Add(new HafNode { Name = "Icosphere", Mesh = 0 }); m.Nodes.Add(new HafNode { Name = "A", Mesh = 1 }); m.Nodes.Add(new HafNode { Name = "B", Mesh = 2 });
        var r = VehicleProbe.Run(m);
        Assert.Equal(new[] { "A", "B" }, r.Parts.Select(p => p.Name));
        Assert.Contains("purged glTF importer bone-shape artifact: Icosphere", r.Notes);
    }

    [Fact]
    public void Loose_parts_are_named_in_the_pool_of_every_object()
    {
        // the "split_collision" fixture: one mesh "Hull" of three islands, and EMPTIES named Hull.001 and Hull.003. Blender: Hull, Hull.002, Hull.004
        var me = new HafMesh { Name = "boat" };
        me.Primitives.Add(new HafPrimitive { VertexCount = 9, Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 10, 0, 0, 13, 0, 0, 10, 3, 0, 20, 0, 0, 22, 0, 0, 20, 2, 0 }, Indices = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 } });
        var m = new HafModel(); m.Meshes.Add(me);
        m.Nodes.Add(new HafNode { Name = "Hull", Mesh = 0 }); m.Nodes.Add(new HafNode { Name = "Hull.001" }); m.Nodes.Add(new HafNode { Name = "Hull.003" });
        Assert.Equal(new[] { "Hull", "Hull.002", "Hull.004" }, VehicleProbe.Run(m).Parts.Select(p => p.Name));
    }

    [Fact]
    public void A_rotation_that_is_not_a_unit_quaternion_is_normalized()
    {
        // the "nonunit_rotation" fixture: (0, 1, 0, 1) is a quarter turn about Y written 1.41 long. Blender: Turned 5,0.5,0.5 | 0,1,1;
        // taken as given the part came out twice its size (4.5,1,0.5 | 1,2,1)
        var m = new HafModel(); m.Meshes.Add(Tri("m")); m.Meshes.Add(Tri("m2"));
        m.Nodes.Add(new HafNode { Name = "Long", Mesh = 0, Rotation = new double[] { 0, 0, 0, 2 } });
        m.Nodes.Add(new HafNode { Name = "Turned", Mesh = 1, Rotation = new double[] { 0, 1, 0, 1 }, Translation = new double[] { 5, 0, 0 } });
        var r = VehicleProbe.Run(m);
        Assert.Equal("PART|Long|3|0.5000,0.0000,0.5000|1.0000,0.0000,1.0000|1||0", Clean(r.Parts[0].Row));
        Assert.Equal("PART|Turned|3|5.0000,0.5000,0.5000|0.0000,1.0000,1.0000|1||0", Clean(r.Parts[1].Row));
    }

    [Fact]
    public void The_rig_report_reads_the_first_created_armature()
    {
        // the "two_armatures" fixture: Blender printed RIGBONE|BoneB|3|5.5,0,0.5|1,0,1 and the parts MeshB (BoneB), MeshA (BoneA)
        HafMesh Skinned(string name, float dx) { var me = new HafMesh { Name = name }; me.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new[] { dx, 0, 0, dx + 1, 0, 0, dx, 1, 0 }, Joints = new ushort[12], Weights = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 } }); return me; }
        var m = new HafModel(); m.Meshes.Add(Skinned("ma", 0)); m.Meshes.Add(Skinned("mb", 5));
        var rig2 = new HafNode { Name = "Rig2" }; rig2.Children.Add(1); var rig1 = new HafNode { Name = "Rig1" }; rig1.Children.Add(3);
        m.Nodes.Add(rig2); m.Nodes.Add(new HafNode { Name = "BoneB" }); m.Nodes.Add(rig1); m.Nodes.Add(new HafNode { Name = "BoneA" });
        m.Nodes.Add(new HafNode { Name = "MeshA", Mesh = 0, Skin = 0 }); m.Nodes.Add(new HafNode { Name = "MeshB", Mesh = 1, Skin = 1 });
        m.Skins.Add(new HafSkin { Name = "SkinA", Joints = new[] { 3 } }); m.Skins.Add(new HafSkin { Name = "SkinB", Joints = new[] { 1 } });
        var r = VehicleProbe.Run(Link(m));
        Assert.Equal("Rig2", r.Armature);
        Assert.Equal("RIGBONE|BoneB|3|5.5000,0.0000,0.5000|1.0000,0.0000,1.0000", Clean(r.RigBones.Single().Row));
        Assert.Equal(new[] { "MeshB", "MeshA" }, r.Parts.Select(p => p.Name));
        Assert.Equal(new[] { "BoneB", "BoneA" }, r.Parts.Select(p => p.Bone));
    }

    [Fact]
    public void A_matrix_node_is_what_Blender_decomposes_it_to()
    {
        // an exactly decomposable matrix (translation, a rotation about Y, a non-uniform scale) comes back as it was
        double c = System.Math.Cos(0.7), sn = System.Math.Sin(0.7);
        var trs = new double[] { 2 * c, 0, -2 * sn, 0, 0, 3, 0, 0, 0.5 * sn, 0, 0.5 * c, 0, 4, 5, 6, 1 };
        var same = VehicleProbe.AsBlenderDecomposes(trs);
        for (int i = 0; i < 16; i++) Assert.Equal(trs[i], same[i], 9);
        // a mirror (negative determinant) too: Blender negates the axes and the scale together
        var mirror = new double[] { -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 2, 0, 1 };
        var m2 = VehicleProbe.AsBlenderDecomposes(mirror);
        for (int i = 0; i < 16; i++) Assert.Equal(mirror[i], m2[i], 9);
        // a sheared matrix loses its shear: the axes come back square, their lengths kept (rah66.glb, a Lab source: 0.13 degrees
        // off square, four parts' boxes 0.15 % different through the matrix as given - Blender's probe and the recipe had the decomposed ones)
        var sheared = new double[] { 1, 0, 0, 0, 0.2, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        var d = VehicleProbe.AsBlenderDecomposes(sheared);
        double dot = d[0] * d[4] + d[1] * d[5] + d[2] * d[6];
        Assert.Equal(0, dot, 9);
        Assert.Equal(System.Math.Sqrt(1.04), System.Math.Sqrt(d[4] * d[4] + d[5] * d[5] + d[6] * d[6]), 9);
    }

    static string Clean(string row) => row.Replace("-0.0000", "0.0000");
}
