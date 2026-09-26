using System;
using System.Collections.Generic;
using Xunit;

// The pure half of the Animation Lab's elevation preview (editor/EditorRules.cs: AnimationLabRules): which bone the
// runtime elevates, how the preview finds the SAME one, the angle it applies, and the gun span the rigger publishes.
public class AnimationLabRulesTests
{
    [Fact]
    public void The_turret_bone_wins_over_the_gun_bone_and_nothing_is_trimmed()
    {
        Assert.Equal("Turret", AnimationLabRules.ElevationBoneName("Turret", "Gun"));
        Assert.Equal("Gun", AnimationLabRules.ElevationBoneName("", "Gun"));
        Assert.Equal("Gun", AnimationLabRules.ElevationBoneName(null, "Gun"));
        Assert.Equal("", AnimationLabRules.ElevationBoneName(null, null));
        // the runtime reads these raw: a stray space must survive to here, or the preview hides the failure it exists
        // to catch (review of PR #92). A whitespace-only turret name is still "set" to IsNullOrEmpty, as in the game.
        Assert.Equal(" Turret ", AnimationLabRules.ElevationBoneName(" Turret ", "Gun"));
        Assert.Equal(" ", AnimationLabRules.ElevationBoneName(" ", "Gun"));
        Assert.True(AnimationLabRules.NameNeedsTrimming(" Gun"));
        Assert.True(AnimationLabRules.NameNeedsTrimming("Gun "));
        Assert.False(AnimationLabRules.NameNeedsTrimming("Gun"));
        Assert.False(AnimationLabRules.NameNeedsTrimming(""));
        Assert.False(AnimationLabRules.NameNeedsTrimming(null));
    }

    [Fact]
    public void The_bone_is_the_first_substring_match_in_the_rigs_own_order()
    {
        // The runtime breaks on the FIRST bone containing the name, walking the skeleton's name-sorted array. A
        // preview that preferred the exact match turned the barrel while the game turned the shield (review of PR #92).
        var rig = new[] { "Root", "b013_GunShield", "b005_GunMount", "b012_Gun", "b020_Turret" };
        Assert.Equal("b005_GunMount", AnimationLabRules.PickElevationBone(rig, "Gun"));
        Assert.Equal("b005_GunMount", AnimationLabRules.PickElevationBone(rig, "gun"));      // case-insensitive, as the runtime is
        Assert.Equal("b012_Gun", AnimationLabRules.PickElevationBone(new[] { "Root", "b012_Gun", "b013_GunShield" }, "Gun"));
        Assert.Equal("b020_Turret", AnimationLabRules.PickElevationBone(rig, "Turret"));
        Assert.Null(AnimationLabRules.PickElevationBone(rig, "Barrel"));
        Assert.Null(AnimationLabRules.PickElevationBone(rig, " Gun"));   // untrimmed, so it misses here exactly as it misses in the game
        Assert.Null(AnimationLabRules.PickElevationBone(rig, ""));
        Assert.Null(AnimationLabRules.PickElevationBone(null, "Gun"));
    }

    [Fact]
    public void The_riggers_bone_is_matched_by_identity_not_by_substring()
    {
        // The pivot advice is only valid for the bone the rig's Gun pivot places. A substring test let the runtime's
        // first match ("b005_GunMount") and a configured "GunTurret" pass as the Gun bone (second review of PR #92).
        Assert.True(AnimationLabRules.IsRigBone("Gun", "Gun"));
        Assert.True(AnimationLabRules.IsRigBone("b012_Gun", "Gun"));       // the bake's b###_<orig> rename
        Assert.True(AnimationLabRules.IsRigBone("b7_gun", "Gun"));
        Assert.True(AnimationLabRules.IsRigBone("A012_Gun", "Gun"));       // a model with donor sockets renames with 'A###_' so real bones sort first
        Assert.False(AnimationLabRules.IsRigBone("b005_GunMount", "Gun"));
        Assert.False(AnimationLabRules.IsRigBone("GunTurret", "Gun"));
        Assert.False(AnimationLabRules.IsRigBone("b013_GunShield", "Gun"));
        Assert.False(AnimationLabRules.IsRigBone("A005_GunMount", "Gun"));
        Assert.False(AnimationLabRules.IsRigBone("Turret", "Gun"));
        Assert.False(AnimationLabRules.IsRigBone("bxx_Gun", "Gun"));       // the prefix is digits, nothing else
        Assert.False(AnimationLabRules.IsRigBone("", "Gun"));
        Assert.False(AnimationLabRules.IsRigBone(null, "Gun"));
        Assert.False(AnimationLabRules.IsRigBone("Gun", null));
    }

    [Fact]
    public void The_angle_is_the_runtimes_negated_fraction_of_the_max()
    {
        Assert.Equal(-30f, AnimationLabRules.ElevationAngle(30f, 1f));
        Assert.Equal(-15f, AnimationLabRules.ElevationAngle(30f, 0.5f));
        Assert.Equal(0f, AnimationLabRules.ElevationAngle(30f, 0f));
        Assert.Equal(20f, AnimationLabRules.ElevationAngle(-20f, 1f));   // a negative max flips, as in the game
        Assert.Equal(-30f, AnimationLabRules.ElevationAngle(30f, 7f));   // the fraction is clamped
        Assert.Equal(0f, AnimationLabRules.ElevationAngle(30f, -1f));
    }

    [Fact]
    public void The_gun_span_is_read_from_the_riggers_own_line_not_re_derived()
    {
        string log = "Blender 5.1\r\n" +
                     "VEHICLE gun pivot 0.36 -> head=(1.00, 2.00, 3.00) (breech (0.50, 2.00, 3.00) .. muzzle (2.50, 2.00, 3.00))\r\n" +
                     "VEHICLE GUNSPAN bone=Gun breech=-0.500000,0.000000,0.000000 muzzle=1.500000,0.000000,0.000000 extent=2.400000\r\n" +
                     "VEHICLE RIG DONE: ...\r\n";
        Assert.True(AnimationLabRules.ParseGunSpan(log, out string bone, out double[] breech, out double[] muzzle, out double extent));
        Assert.Equal("Gun", bone);
        Assert.Equal(new[] { -0.5, 0.0, 0.0 }, breech);
        Assert.Equal(new[] { 1.5, 0.0, 0.0 }, muzzle);
        Assert.Equal(2.4, extent, 6);
        // the head IS the bone's origin in these coordinates, so the rig's own pivot falls straight out - which is how
        // the 0.5 default (head at the assembly bbox centre, not the span's midpoint) reports honestly
        Assert.Equal(0.25, AnimationLabRules.PivotFraction(new double[3], breech, muzzle), 6);

        foreach (string bad in new[]
        {
            "",
            "VEHICLE RIG DONE: no span here",
            "VEHICLE GUNSPAN bone=Gun breech=0,0,0 muzzle=1,0,0",                                  // no extent
            "VEHICLE GUNSPAN bone=Gun breech=0,0,0 muzzle=1,0,0 extent=0",                         // no assembly to scale against
            "VEHICLE GUNSPAN bone=Gun breech=0,0,0 muzzle=0,0,0 extent=2",                         // a span of zero length
            "VEHICLE GUNSPAN bone=Gun breech=0,0 muzzle=1,0,0 extent=2",                           // two components
            "VEHICLE GUNSPAN bone=Gun breech=nan,0,0 muzzle=1,0,0 extent=2",
            "VEHICLE GUNSPAN bone= breech=0,0,0 muzzle=1,0,0 extent=2",
        })
        {
            Assert.False(AnimationLabRules.ParseGunSpan(bad, out string b2, out double[] br2, out double[] mz2, out double ex2), bad);
            Assert.Null(b2); Assert.Null(br2); Assert.Null(mz2); Assert.Equal(0.0, ex2);
        }
        Assert.False(AnimationLabRules.ParseGunSpan(null, out _, out _, out _, out _));
    }

    [Fact]
    public void The_pivot_fraction_is_the_projection_on_the_span_and_the_point_is_its_inverse()
    {
        var breech = P(0, 0, -3); var muzzle = P(0, 0, 5);
        Assert.Equal(0.375, AnimationLabRules.PivotFraction(P(0, 0, 0), breech, muzzle), 6);      // the origin 3 of 8 along: the rig's pivot
        Assert.Equal(0.0, AnimationLabRules.PivotFraction(breech, breech, muzzle), 6);
        Assert.Equal(1.0, AnimationLabRules.PivotFraction(muzzle, breech, muzzle), 6);
        Assert.Equal(0.5, AnimationLabRules.PivotFraction(P(7, 7, 1), breech, muzzle), 6);          // off-axis parts do not count
        Assert.Equal(1.25, AnimationLabRules.PivotFraction(P(0, 0, 7), breech, muzzle), 6);         // beyond the muzzle reads as such
        Assert.Equal(0.5, AnimationLabRules.PivotFraction(P(1, 1, 1), P(2, 2, 2), P(2, 2, 2)), 6);  // a degenerate span: the centre
        Assert.Equal(new[] { 0.0, 0.0, -1.0 }, AnimationLabRules.PivotPoint(breech, muzzle, 0.25));
    }
    static double[] P(double x, double y, double z) => new[] { x, y, z };
}
