using System;
using System.Linq;
using Xunit;

/// <summary>The rig_anim port's '.mesh' child of a joint (2026-11-08): stage 1 gives the object the importer's properties
/// (ImportRig.JointMeshProperty) instead of the identity, so its matrix_world is the one Blender 5.1.2 printed for the same
/// object through the probe (VehicleProbeJointMeshTests' fixtures and rows). The live drill holds the whole scene on
/// riganim_fixtures.py's joint_mesh fixture on both paths; this holds the matrix in the unit suite.</summary>
public class RigAnimJointMeshTests
{
    // the static fixture only: rig_anim poses its scene at frame 1 of its own active action, the probe at its reference pose, so
    // the probe's animated row is no oracle here - the drill's joint_mesh fixture holds the animated joint against rig_anim.py
    [Theory]
    [InlineData("static", "1.0 -3.422854533141617e-08 -4.5989077888768104e-15 0.0 3.422854177870249e-08 1.0000001192092896 1.685874053691805e-07 0.0 -1.1715931027525724e-15 -1.3435885648505064e-07 1.0000001192092896 2.0 0.0 0.0 0.0 1.0")]
    public void A_mesh_on_a_joint_has_Blenders_matrix_world(string fixture, string blenderRowMajor)
    {
        var m = GlbReader.Read(Convert.FromBase64String(fixture == "static" ? VehicleProbeJointMeshTests.MeshOnBone : VehicleProbeJointMeshTests.MeshOnBoneAnimated));
        // the probe's fixtures carry no material, and rig_anim drops a material-less mesh: one material, which moves nothing
        m.Materials.Add(new HafMaterial { Name = "Paint" });
        foreach (var p in m.Meshes.SelectMany(x => x.Primitives)) p.Material = 0;
        var a = BlenderRigAnim.ParseArgs(new[] { "scene.glb", "scene.fbx", "0", "", "", "", "1", "0,0,0", "0", "", "0", "", "0", "", "0" });
        var s = BlenderRigAnim.RunStage1(m, a);
        Assert.Null(s.Fallback);
        Assert.Contains("a mesh on a joint node (a '.mesh' child under the bone)", s.Notes);
        var so = s.Objects.Single(o => o.Type == "MESH" && o.ParentBone != null && o.Source.GltfNode < 0);
        Assert.Equal(Bits(blenderRowMajor), RowMajorBits(so.World));
    }

    [Fact]
    public void Vehicle_rig_still_leaves_a_mesh_on_a_joint_to_Blender()
    {
        // the import is shared (BlenderRigAnim.ImportScene); vehicle_rig's drill has no fixture with a mesh on a joint, so the Lab's
        // Generate keeps such a file Blender's by name (review, 2026-11-09)
        var m = GlbReader.Read(Convert.FromBase64String(VehicleProbeJointMeshTests.MeshOnBone));
        m.Materials.Add(new HafMaterial { Name = "Paint" });
        foreach (var p in m.Meshes.SelectMany(x => x.Primitives)) p.Material = 0;
        var argv = Enumerable.Repeat("", 76).ToList();
        argv[0] = "rig"; argv[1] = "D:/m/a.glb"; argv[2] = "D:/m/a_Spin.glb"; argv[6] = "AUTO"; argv[7] = "15"; argv[8] = "-360";
        var s = BlenderVehicleRig.RunStage1(m, BlenderVehicleRig.ParseArgs(argv));
        Assert.NotNull(s.Fallback);
        Assert.Contains("a mesh on a bone node", s.Fallback);
    }

    static uint[] Bits(string row) => row.Split(' ')
        .Select(t => { Assert.True(GlbReader.ReadPythonFloat(t, out double d), t); return BitConverter.ToUInt32(BitConverter.GetBytes((float)d), 0); }).ToArray();
    static uint[] RowMajorBits(float[] colMajor) => Enumerable.Range(0, 16)
        .Select(i => BitConverter.ToUInt32(BitConverter.GetBytes(colMajor[(i % 4) * 4 + i / 4]), 0)).ToArray();
}
