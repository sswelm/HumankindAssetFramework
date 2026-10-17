using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

public class VehicleRigStage4Tests
{
    // A ten-unit hull and three wheels. Blender's last frame_set evaluates the preceding rock key; assigning
    // the final sample afterwards restores the original pose, but leaves the evaluated hull below the wheels.
    [Theory]
    [InlineData(15, true)]
    [InlineData(2, true)]  // several rock samples replace the key at the last frame
    [InlineData(15, false)]
    public void Ground_contact_uses_the_evaluated_pose_and_the_final_pose_keeps_the_last_sample(int frames, bool rock)
    {
        var m = new HafModel { Scene = 0 }; m.Scenes.Add(new HafScene());
        Box(m, "Hull", 0, .5f, 0, 10, 1, 2);
        Box(m, "Big0", 3, .4f, 1.2f, .8f, .8f, .2f);
        Box(m, "Big1", -3, .4f, 1.2f, .8f, .8f, .2f);
        Box(m, "Small", 0, .22f, 1.2f, .3f, .3f, .2f);
        var argv = Enumerable.Repeat("", 76).ToList();
        argv[0] = "rig"; argv[1] = "scene.glb"; argv[2] = "scene_Spin.glb";
        argv[6] = "AUTO"; argv[7] = frames.ToString(); argv[8] = "-360";
        argv[17] = "0"; argv[18] = frames.ToString(); argv[21] = rock ? "20" : "0"; argv[24] = "0";
        var a = BlenderVehicleRig.ParseArgs(argv);
        a.Names["wheels"] = new List<string> { "Big0", "Big1", "Small" };
        foreach (var n in a.Names["wheels"]) a.Marked.Add(n);
        var s = BlenderVehicleRig.RunStage1(m, a);
        BlenderVehicleRig.RunStage2(s, a); BlenderVehicleRig.RunStage3(s, a); BlenderVehicleRig.RunStage4(s, a);
        Assert.Null(s.Fallback); Assert.Null(s.Dies); Assert.False(s.Exits);
        var spin = Assert.Single(s.Actions);
        var small = Assert.Single(Assert.Single(spin.Slots).FCurves.Where(fc => fc.Bone == "Wheel_02" && fc.Prop == "rotation_euler" && fc.Index == 1));
        // Blender-measured native fixtures fx_e_groundeval{,_dense,_off}: rock leaves every wheel airborne;
        // without rock the small wheel rolls 0.8 / 0.3 times as fast as the largest wheels.
        float last = small.Keys.Last().Value;
        Assert.InRange(Math.Abs(last - (rock ? -2 * Math.PI : -960 * (Math.PI / 180))), 0, 1e-4);
        if (rock)
        {
            Assert.Contains(s.Logs4, l => l.StartsWith("VEHICLE rolling-contact: 3 wheel(s) above"));
            int body = ((BlenderVehicleRig.RigState)s.VehicleState).BoneId["RootHull"];
            Assert.All(s.Armature.PoseEuler[body], v => Assert.InRange(Math.Abs(v), 0f, 1e-6f));
        }
    }

    static void Box(HafModel m, string name, float x, float y, float z, float sx, float sy, float sz)
    {
        var p = new List<float>();
        foreach (int dx in new[] { -1, 1 }) foreach (int dy in new[] { -1, 1 }) foreach (int dz in new[] { -1, 1 })
            p.AddRange(new[] { x + dx * sx / 2, y + dy * sy / 2, z + dz * sz / 2 });
        var mesh = new HafMesh { Name = name };
        mesh.Primitives.Add(new HafPrimitive { Positions = p.ToArray(), Indices = new[] { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 } });
        m.Nodes.Add(new HafNode { Name = name, Mesh = m.Meshes.Count }); m.Meshes.Add(mesh); m.Scenes[0].Nodes.Add(m.Nodes.Count - 1);
    }
}
