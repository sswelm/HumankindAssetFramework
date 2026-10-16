using System;
using System.Reflection;
using Xunit;

/// <summary>Stage 3 of vehicle_rig.py in C# (BlenderVehicleRig.Stage3.cs): the rules a unit test can hold beside the drill - the
/// axle choice on a tie and Python's repr() of a part name in find()'s exit line (no fixture names a part with a quote).</summary>
public class VehicleRigStage3Tests
{
    static T Call<T>(string name, params object[] args)
        => (T)typeof(BlenderVehicleRig).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);

    [Theory]
    [InlineData("AUTO", 1f, 0.3f, 1f, 1)]      // the thinnest extent
    [InlineData("AUTO", 0.5f, 0.5f, 0.5f, 0)]  // a tie: min over range(3) keeps the first
    [InlineData("AUTO", 2f, 1f, 1f, 1)]        // a tie of the two thinnest: the first of them
    [InlineData("X", 1f, 0.3f, 1f, 0)]
    [InlineData("Z", 1f, 0.3f, 1f, 2)]
    [InlineData("x", 1f, 0.3f, 1f, 1)]         // the caller upper-cases argv[6]; a lower-case token here is AUTO
    public void The_axle_is_the_forced_axis_or_the_first_thinnest_extent(string arg, float sx, float sy, float sz, int axis)
    {
        var ax = Call<float[]>("AxleAxis", arg, new[] { sx, sy, sz });
        for (int i = 0; i < 3; i++) Assert.Equal(i == axis ? 1f : 0f, ax[i]);
    }

    [Theory]
    [InlineData("Hull", "'Hull'")]
    [InlineData("Bob's", "\"Bob's\"")]                 // a single quote and no double one: double quotes
    [InlineData("a'b\"c", "'a\\'b\"c'")]               // both: single quotes, the single one escaped
    [InlineData("back\\slash", "'back\\\\slash'")]
    [InlineData("tab\there", "'tab\\there'")]
    [InlineData("bell\u0007", "'bell\\x07'")]
    [InlineData("Größe", "'Größe'")]                   // printable non-ASCII stays
    public void A_part_name_prints_as_Python_repr_prints_it(string name, string repr)
        => Assert.Equal(repr, Call<string>("PyStrRepr", name));
}
