using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

// The pure half of the Bake Tests' Workshop and Vehicle Lab rows (editor/EditorRules.cs: BakeGoldenRules): what a rig
// run's snapshot is, how two snapshots compare, and which recipes are the representatives.
public class BakeGoldenRulesTests
{
    [Fact]
    public void A_rig_runs_snapshot_is_its_VEHICLE_lines_without_timings_or_the_output_path()
    {
        string stdout = "Blender 5.1\r\n" +
                        "VEHICLE timing: import        3.2s\r\n" +
                        "PART|Hull|120|0,0,0|1,1,1|1||0\r\n" +
                        "VEHICLE WHEEL: 4 part(s) reduced, 12000 -> 6000 verts total (50% cut)\r\n" +
                        "  VEHICLE indented lines are not summary lines\r\n" +
                        "VEHICLE armature: 7 bones total (Amplitude cap 256)\r\n" +
                        "VEHICLE timing: export        1.0s\r\n" +
                        "VEHICLE RIG DONE: 4 wheel part(s) clustered into 2 wheel(s) {}, 0 turret part(s) on one Turret bone, Spin 0..24 360 deg, exported 6000 verts / 9000 tris -> C:/x/Logs/bake_tests/lab/car_Spin.glb   \r\n";
        Assert.Equal(new[]
        {
            "VEHICLE WHEEL: 4 part(s) reduced, 12000 -> 6000 verts total (50% cut)",   // the reduce line's own arrow stays
            "VEHICLE armature: 7 bones total (Amplitude cap 256)",
            "VEHICLE RIG DONE: 4 wheel part(s) clustered into 2 wheel(s) {}, 0 turret part(s) on one Turret bone, Spin 0..24 360 deg, exported 6000 verts / 9000 tris",
        }, BakeGoldenRules.LabSnapshotLines(stdout));
        Assert.Empty(BakeGoldenRules.LabSnapshotLines(null));
        Assert.Empty(BakeGoldenRules.LabSnapshotLines("nothing\nVEHICLE timing: a 1.0s\n"));
    }

    [Fact]
    public void The_bone_total_comes_from_the_armature_line_or_is_minus_one()
    {
        Assert.Equal(7, BakeGoldenRules.ArmatureBones(new[] { "VEHICLE RIG DONE: x", "VEHICLE armature: 7 bones total (Amplitude cap 256)" }));
        Assert.Equal(312, BakeGoldenRules.ArmatureBones(new[] { "VEHICLE armature: 312 bones total (Amplitude cap 256)" }));
        Assert.Equal(-1, BakeGoldenRules.ArmatureBones(new[] { "VEHICLE RIG DONE: x" }));
        Assert.Equal(-1, BakeGoldenRules.ArmatureBones(null));
    }

    [Fact]
    public void Two_snapshots_compare_line_by_line_and_the_first_difference_is_named()
    {
        var golden = new[] { "A", "B", "C" };
        Assert.Null(BakeGoldenRules.Diff(golden, new[] { "A", "B", "C" }));
        Assert.Null(BakeGoldenRules.Diff(golden, new[] { "A\r", "B  ", "", "C", "" }));   // CR, trailing blanks and empty lines never differ
        Assert.Equal("line 2: expected 'B' but got 'X'", BakeGoldenRules.Diff(golden, new[] { "A", "X", "C" }));
        Assert.Equal("line 3: expected 'C' but the run ended (3 golden line(s), 2 now)", BakeGoldenRules.Diff(golden, new[] { "A", "B" }));
        Assert.Equal("line 4: unexpected 'D' (3 golden line(s), 4 now)", BakeGoldenRules.Diff(golden, new[] { "A", "B", "C", "D" }));
        Assert.Equal("line 1: expected 'A' but got 'Z' (3 golden line(s), 1 now)", BakeGoldenRules.Diff(golden, new[] { "Z" }));
        Assert.Null(BakeGoldenRules.Diff(null, new string[0]));
    }

    [Fact]
    public void The_representatives_are_the_first_recipe_per_feature_each_counted_once()
    {
        var roles = new Dictionary<string, ISet<string>>
        {
            ["bike"] = new HashSet<string> { "Wheel", "Body" },
            ["galley"] = new HashSet<string> { "Oar", "Sail", "Rigging" },   // oars AND sails: picked once, for the oars; the sails go to the next
            ["gunboat"] = new HashSet<string> { "Gun", "Wheel" },
            ["heli"] = new HashSet<string> { "Rotor", "TailRotor" },
            ["tank"] = new HashSet<string> { "Caterpillar", "Gun", "Turret" },
            ["frigate"] = new HashSet<string> { "Sail" },
        };
        var order = new[] { "bike", "frigate", "galley", "gunboat", "heli", "tank" };
        Assert.Equal(new[] { "galley", "gunboat", "bike", "frigate", "heli", "tank" },
            BakeGoldenRules.Representatives(order, n => roles[n]));
        // a tail rotor alone is a rotor; nothing marked = nothing picked
        Assert.Equal(new[] { "x" }, BakeGoldenRules.Representatives(new[] { "x", "y" }, n => n == "x" ? new HashSet<string> { "TailRotor" } : new HashSet<string>()));
        Assert.Empty(BakeGoldenRules.Representatives(new[] { "y" }, n => new HashSet<string> { "Body" }));
    }
}
