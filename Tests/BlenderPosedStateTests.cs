using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>BlenderPosedState: the importer's fcurves and Blender's evaluation of them. tools/deploy_drill.sh holds whole
/// files to Blender's matrices bit for bit; here each rule on a case where it alone decides, with the T-62's measured
/// value for the key merge.</summary>
public class BlenderPosedStateTests
{
    static HafModel Model(params (int node, string path, float[] times, float[] values, string interpolation)[] channels)
    {
        var m = new HafModel();
        for (int i = 0; i < 4; i++) m.Nodes.Add(new HafNode { Name = "N" + i });
        var sc = new HafScene(); sc.Nodes.AddRange(Enumerable.Range(0, 4)); m.Scenes.Add(sc); m.Scene = 0;
        var a = new HafAnimation { Name = "A" };
        foreach (var (node, path, times, values, interpolation) in channels)
        {
            a.Samplers.Add(new HafSampler { Times = times, Values = values, Components = path == "rotation" ? 4 : path == "weights" ? 1 : 3, Interpolation = interpolation ?? "LINEAR" });
            a.Channels.Add(new HafChannel { Node = node, Path = path, Sampler = a.Samplers.Count - 1 });
        }
        m.Animations.Add(a);
        return m;
    }

    static float[] X(params float[] xs) => xs.SelectMany(x => new[] { x, 0f, 0f }).ToArray();

    [Fact]
    public void A_key_sits_at_its_time_times_24_and_a_frame_between_two_keys_is_interpolated_in_float32()
    {
        var a = BlenderPosedState.Import(Model((0, "translation", new[] { 0f, 1f / 30f, 2f / 30f }, X(0f, 3f, 5f), null)), 0);
        var c = a.Translation[0][0];
        Assert.Equal(new[] { 0f, (float)((double)(1f / 30f) * 24.0), (float)((double)(2f / 30f) * 24.0) }, c.Frames);
        Assert.Equal(0f, c.Evaluate(0f));                       // on the first key
        Assert.Equal(5f, c.Evaluate(1.6f));                     // at the last
        // frame 1 lies between the keys at 0.8 and 1.6: change * time / duration + begin
        float begin = 3f, change = 5f - 3f, duration = (float)(c.Frames[2] - c.Frames[1]), time = (float)(1f - c.Frames[1]);
        Assert.Equal((float)((float)((float)(change * time) / duration) + begin), c.Evaluate(1f));
        // held outside the range (the importer's curves extrapolate constant)
        Assert.Equal(0f, c.Evaluate(-3f)); Assert.Equal(5f, c.Evaluate(40f));
        Assert.Equal(0f, a.FrameStart); Assert.Equal(c.Frames[2], a.FrameEnd);
    }

    [Fact]
    public void Keys_closer_than_a_hundredth_of_a_frame_merge_and_the_last_wins_at_the_earlier_frame()
    {
        // the T-62 (measured 2026-10-09): keys at 7.3666668 s and 7.367 s are frames 176.8 and 176.808; Blender's
        // fcurve.update() keeps ONE key there, with the later value at the earlier frame - and frame 177 reads 0.001068206,
        // not the 0.001068115 of the unmerged keys
        var a = BlenderPosedState.Import(Model((0, "translation", new[] { 7.3333335f, 7.3666668f, 7.367f, 7.4f }, X(0.0010529775f, 0.0010650876f, 0.0010652088f, 0.0010771978f), null)), 0);
        var c = a.Translation[0][0];
        Assert.Equal(3, c.Frames.Length);
        Assert.Equal((float)((double)7.3666668f * 24.0), c.Frames[1]);
        Assert.Equal(0.0010652088f, c.Values[1]);
        Assert.Equal(0.001068206f, c.Evaluate(177f));
        Assert.Contains("keys closer than 0.01 frame merged (the last wins)", a.Notes);
    }

    [Fact]
    public void A_merged_key_that_sits_on_a_whole_frame_keeps_its_own_frame_and_a_chain_merges_into_its_first()
    {
        // 0.2496 s is frame 5.9904, 0.25 s frame 6 exactly: the later key stays at 6
        var a = BlenderPosedState.Import(Model((0, "translation", new[] { 0f, 0.2496f, 0.25f, 0.5f }, X(0f, 2f, 2.5f, 9f), null)), 0);
        var c = a.Translation[0][0];
        Assert.Equal(new[] { 0f, 6f, 12f }, c.Frames); Assert.Equal(new[] { 0f, 2.5f, 9f }, c.Values);
        Assert.Contains("a merged key on a whole frame (it keeps its own frame)", a.Notes);
        // three keys within 0.01 frame of the first of them: one key, the first one's frame, the last one's value
        float t = 0.3f, step = 0.004f / 24f;
        var b = BlenderPosedState.Import(Model((0, "translation", new[] { 0f, t, t + step, t + 2 * step, 1f }, X(0f, 3f, 3.2f, 3.4f, 5f), null)), 0);
        var d = b.Translation[0][0];
        Assert.Equal(3, d.Frames.Length);
        Assert.Equal((float)((double)t * 24.0), d.Frames[1]); Assert.Equal(3.4f, d.Values[1]);
        // two keys at the same time merge like any pair
        var e = BlenderPosedState.Import(Model((0, "translation", new[] { 0f, 0.1f, 0.1f, 0.2f }, X(0f, 2f, 1f, 4f), null)), 0).Translation[0][0];
        Assert.Equal(new[] { 0f, 1f, 4f }, e.Values);
    }

    [Fact]
    public void A_key_within_a_ten_thousandth_of_the_frame_gives_its_value_not_an_interpolation()
    {
        float near = (float)(2.99995 / 24.0);
        var c = BlenderPosedState.Import(Model((0, "translation", new[] { 0f, near, 4f / 24f, 0.3f }, X(0f, 10f, 1000f, 0f), null)), 0).Translation[0][0];
        Assert.NotEqual(3f, c.Frames[1]); Assert.True(Math.Abs(c.Frames[1] - 3f) <= 0.0001f);
        Assert.Equal(10f, c.Evaluate(3f));
    }

    [Fact]
    public void A_quaternion_that_points_away_from_the_one_before_is_negated_and_the_components_are_interpolated_one_by_one()
    {
        var m = Model((1, "rotation", new[] { 0f, 1f, 2f }, new[] { 0f, 0f, 0f, 1f,   0f, -0.3826834f, 0f, -0.9238795f,   0f, 0.7071068f, 0f, 0.7071068f }, null));
        var a = BlenderPosedState.Import(m, 0);
        var r = a.Rotation[1];
        // Blender's components: w, x, y (glTF's -z), z (glTF's y)
        Assert.Equal(new[] { 0f, 0.3826834f, 0.7071068f }, r[3].Values);       // glTF's y: the second key negated, the third left
        Assert.Equal(new[] { 1f, 0.9238795f, 0.7071068f }, r[0].Values);       // w
        Assert.Contains("a quaternion negated to run the short way", a.Notes);
        // frame 12 is half way between the first two keys: each component halfway, NOT a point on the sphere
        var q = a.TrsAt(12f)(1)[1];
        Assert.Equal((float)(0.3826834f * 12f / 24f), q[3]);
        Assert.True(Math.Sqrt(q.Sum(x => (double)x * x)) < 1.0);                       // the object normalizes it when it builds its matrix
        Assert.Null(a.TrsAt(12f)(1)[0]); Assert.Null(a.TrsAt(12f)(1)[2]);      // no translation or scale channel on the node
        Assert.Null(a.TrsAt(12f)(0));                                          // a node the animation does not touch
    }

    [Fact]
    public void A_STEP_sampler_holds_and_a_curve_of_one_key_is_that_key_everywhere()
    {
        var a = BlenderPosedState.Import(Model((2, "scale", new[] { 0f, 0.5f, 1f }, X(1f, 2f, 4f), "STEP"), (3, "translation", new[] { 0.15f }, X(7f), null)), 0);
        var s = a.Scale[2][0];
        Assert.Equal(1f, s.Evaluate(5f)); Assert.Equal(1f, s.Evaluate(11.9f)); Assert.Equal(2f, s.Evaluate(12f)); Assert.Equal(2f, s.Evaluate(20f)); Assert.Equal(4f, s.Evaluate(24f));
        var one = a.Translation[3][0];
        Assert.Equal(7f, one.Evaluate(-5f)); Assert.Equal(7f, one.Evaluate(3.6f)); Assert.Equal(7f, one.Evaluate(100f));
        Assert.Contains("a curve of one key", a.Notes); Assert.Contains("a STEP sampler (constant keys)", a.Notes);
    }

    [Fact]
    public void A_second_channel_on_one_node_and_path_is_refused_and_a_cubic_sampler_is_named_as_not_modelled()
    {
        var a = BlenderPosedState.Import(Model((0, "translation", new[] { 0f, 1f }, X(0f, 2f), null), (0, "translation", new[] { 0f, 3f }, X(9f, 9f), null)), 0);
        Assert.Equal(new[] { 0f, 2f }, a.Translation[0][0].Values);
        Assert.Equal(24f, a.FrameEnd);                                         // the refused channel does not stretch the range
        Assert.False(a.HasBezier);
        // CUBICSPLINE: (in-tangent, value, out-tangent) per key - the values are the middle ones, the keys Bezier
        var cubic = BlenderPosedState.Import(Model((0, "translation", new[] { 0f, 1f }, new[] { 9f, 9f, 9f, 1f, 0f, 0f, 9f, 9f, 9f,   9f, 9f, 9f, 2f, 0f, 0f, 9f, 9f, 9f }, "CUBICSPLINE")), 0);
        Assert.True(cubic.HasBezier); Assert.NotNull(cubic.NotModelled); Assert.Null(a.NotModelled);
        Assert.Equal(new[] { 1f, 2f }, cubic.Translation[0][0].Values);
        Assert.Equal(24f, cubic.FrameEnd);
        // no such animation: an empty action
        Assert.False(BlenderPosedState.Import(Model(), 3).Animates(0));
        // KHR_animation_pointer: the importer animates through pointers the reader does not carry
        var pointed = Model((0, "translation", new[] { 0f, 1f }, X(0f, 2f), null)); pointed.ExtensionsUsed.Add("KHR_animation_pointer");
        Assert.Contains("KHR_animation_pointer", BlenderPosedState.Import(pointed, 0).NotModelled);
        Assert.False(BlenderPosedState.Import(pointed, 0).RangeAndTouchedKnown);
        // with a cubic sampler as well the pointer's reason stands: the range of such a file is not Blender's (a cubic
        // sampler alone leaves it known)
        Assert.True(cubic.RangeAndTouchedKnown);
        var both = Model((0, "translation", new[] { 0f, 1f }, new[] { 9f, 9f, 9f, 1f, 0f, 0f, 9f, 9f, 9f,   9f, 9f, 9f, 2f, 0f, 0f, 9f, 9f, 9f }, "CUBICSPLINE")); both.ExtensionsUsed.Add("KHR_animation_pointer");
        var ba = BlenderPosedState.Import(both, 0);
        Assert.True(ba.HasBezier); Assert.False(ba.RangeAndTouchedKnown); Assert.Contains("KHR_animation_pointer", ba.NotModelled);
    }

    [Fact]
    public void Morph_weights_move_no_object_and_count_for_the_actions_frame_range()
    {
        var a = BlenderPosedState.Import(Model((0, "translation", new[] { 0.25f, 0.5f }, X(0f, 2f), null), (1, "weights", new[] { 0f, 1.5f }, new[] { 0f, 1f }, null)), 0);
        Assert.Equal(0f, a.FrameStart); Assert.Equal(36f, a.FrameEnd);
        Assert.False(a.Animates(1));
        Assert.Contains("morph weights in the action (they count for its frame range)", a.Notes);
    }

    static bool Negative(float zero) => zero == 0f && BitConverter.GetBytes(zero)[3] == 0x80;

    [Fact]
    public void A_zero_has_the_sign_Blender_gives_it()
    {
        // glTF's z of +0 is Blender's -y: the importer's `rotation_after @ loc` (an identity) makes +0 of the -0
        var a = BlenderPosedState.Import(Model((0, "translation", new[] { 0f, 1f }, new[] { 1f, 0f, 0f, 2f, 0f, 0f }, null),
            (1, "rotation", new[] { 0f, 1f }, new[] { 0f, 0f, 0f, 1f,   0f, 0f, 0f, -1f }, null)), 0);
        var y = a.Translation[0][1];
        Assert.Equal(new[] { 0f, 0f }, y.Values); Assert.False(Negative(y.Values[0]));
        // a quaternion negated AFTER that keeps the -0 of its zeros: the key is (1, -0, -0, -0)
        var r = a.Rotation[1];
        Assert.Equal(1f, r[0].Values[1]);
        Assert.False(Negative(r[1].Values[0])); Assert.True(Negative(r[1].Values[1]));
        // ... and the PROPERTY keeps the zero it held: Blender does not write a value equal to the one there. At the last
        // key the curve gives -0 over the +0 of frame 0 - not written; TrsAt alone gives the curve's -0
        Assert.True(Negative(a.TrsAt(24f)(1)[1][1]));
        var pose = new BlenderPosedState.Pose(a, Model());
        Assert.False(Negative(pose.FrameSet(0f)(1)[1][1]));
        Assert.False(Negative(pose.FrameSet(24f)(1)[1][1]));
        // a pose that starts at the last key starts from the import's +0 as well
        Assert.False(Negative(new BlenderPosedState.Pose(a, Model()).FrameSet(24f)(1)[1][1]));
    }

    [Fact]
    public void A_zero_written_over_a_value_that_is_not_zero_is_the_curves_zero()
    {
        // the rotation's x: +0 at the first key, 0.6 at the second, and at the third the -0 of a negated key - which IS
        // written, the value held before it was not zero
        var a = BlenderPosedState.Import(Model((1, "rotation", new[] { 0f, 1f, 2f },
            new[] { 0f, 0f, 0f, 1f,   0.6f, 0f, 0f, 0.8f,   0f, 0f, 0f, -1f }, null)), 0);
        // the third key (x 0, w -1) points away from the second (dot -0.8): negated, w 1 and x -0
        var x = a.Rotation[1][1];
        Assert.True(Negative(x.Values[2]));
        var pose = new BlenderPosedState.Pose(a, Model());
        Assert.False(Negative(pose.FrameSet(0f)(1)[1][1]));
        Assert.Equal(0.6f, pose.FrameSet(24f)(1)[1][1]);
        Assert.True(Negative(pose.FrameSet(48f)(1)[1][1]));
    }
}
