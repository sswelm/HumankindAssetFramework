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
