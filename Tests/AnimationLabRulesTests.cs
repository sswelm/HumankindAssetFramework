using System.Collections.Generic;
using Xunit;

// The pure half of the Animation Lab's elevation preview (editor/EditorRules.cs: AnimationLabRules): which bone the
// runtime elevates, how the preview finds it in the rig, and the angle it applies.
public class AnimationLabRulesTests
{
    [Fact]
    public void The_turret_bone_wins_over_the_gun_bone_when_set()
    {
        Assert.Equal("Turret", AnimationLabRules.ElevationBoneName(" Turret ", "Gun"));
        Assert.Equal("Gun", AnimationLabRules.ElevationBoneName("", "Gun"));
        Assert.Equal("Gun", AnimationLabRules.ElevationBoneName(null, " Gun"));
        Assert.Equal("", AnimationLabRules.ElevationBoneName(" ", null));
    }

    [Fact]
    public void The_bone_is_picked_by_substring_exact_or_renamed_first()
    {
        var rig = new[] { "Root", "b013_GunShield", "b012_Gun", "b020_Turret", "Gun" };
        Assert.Equal("Gun", AnimationLabRules.PickElevationBone(rig, "Gun"));                       // an exact name first
        Assert.Equal("b012_Gun", AnimationLabRules.PickElevationBone(new[] { "b013_GunShield", "b012_Gun" }, "gun"));   // the bake's b###_<orig> rename next, case-insensitive
        Assert.Equal("b013_GunShield", AnimationLabRules.PickElevationBone(new[] { "Root", "b013_GunShield" }, "Gun")); // else the first that contains it, as the runtime does
        Assert.Equal("b020_Turret", AnimationLabRules.PickElevationBone(rig, "turret"));
        Assert.Null(AnimationLabRules.PickElevationBone(rig, "Barrel"));
        Assert.Null(AnimationLabRules.PickElevationBone(rig, ""));
        Assert.Null(AnimationLabRules.PickElevationBone(null, "Gun"));
    }

    [Fact]
    public void The_guns_span_is_its_longest_axis_with_the_breech_nearer_the_parent()
    {
        // a tube along the bone's Z from -3 (behind) to +5 (ahead), some width in X/Y; the parent (the mount) sits behind
        var tube = new List<double[]> { P(-1, 0, -3), P(1, 0, -3), P(0, 1, 0), P(0, -1, 2), P(0.5, 0, 5), P(-0.5, 0, 5) };
        Assert.True(AnimationLabRules.GunSpan(tube, P(0, -2, -6), out var breech, out var muzzle));
        Assert.Equal(-3.0, breech[2]); Assert.Equal(5.0, muzzle[2]);
        // the same tube with the mount AHEAD: the ends swap
        Assert.True(AnimationLabRules.GunSpan(tube, P(0, 0, 9), out breech, out muzzle));
        Assert.Equal(5.0, breech[2]); Assert.Equal(-3.0, muzzle[2]);
        // no parent known: the rig script's default (the higher end is the breech)
        Assert.True(AnimationLabRules.GunSpan(tube, null, out breech, out muzzle));
        Assert.Equal(5.0, breech[2]);
        Assert.False(AnimationLabRules.GunSpan(new List<double[]> { P(0, 0, 0) }, null, out _, out _));
        Assert.False(AnimationLabRules.GunSpan(null, null, out _, out _));
        Assert.False(AnimationLabRules.GunSpan(new List<double[]> { P(1, 1, 1), P(1, 1, 1) }, null, out _, out _));   // no extent
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
        var q = AnimationLabRules.PivotPoint(breech, muzzle, 0.25);
        Assert.Equal(new[] { 0.0, 0.0, -1.0 }, q);
    }
    static double[] P(double x, double y, double z) => new[] { x, y, z };

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
}
