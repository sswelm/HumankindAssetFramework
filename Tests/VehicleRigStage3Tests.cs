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
    [InlineData("Hull\u00a0Gap\u200bX", "'Hull\\xa0Gap\\u200bX'")]
    [InlineData("\ue000", "'\\ue000'")]
    [InlineData("\U000e0001", "'\\U000e0001'")]
    [InlineData("\U0001fae0", "'\U0001fae0'")]
    [InlineData("\u0085", "'\\x85'")]
    [InlineData("\u2028\u202f", "'\\u2028\\u202f'")]
    [InlineData("\u0378", "'\\u0378'")]
    [InlineData("\U0001fae9", "'\\U0001fae9'")] // unassigned in Blender's Unicode 15.1
    [InlineData("Größe", "'Größe'")]                   // printable non-ASCII stays
    public void A_part_name_prints_as_Python_repr_prints_it(string name, string repr)
        => Assert.Equal(repr, Call<string>("PyStrRepr", name));

    [Fact]
    public void Repr_of_all_unicode_scalars_matches_Blenders_pinned_runtime()
    {
        // Independent checksum from repr(''.join(chr(cp) ...)) in Blender 5.1.2 / Python 3.13.9 / Unicode 15.1.
        // Include newer printable characters that the .NET Framework Unicode tables still call unassigned.
        var text = new System.Text.StringBuilder();
        for (int cp = 0; cp <= 0x10ffff; cp++)
        {
            if (cp >= 0xd800 && cp <= 0xdfff) continue; // surrogates are not Unicode scalars
            if (cp <= 0xffff) text.Append((char)cp);
            else { int v = cp - 0x10000; text.Append((char)(0xd800 + (v >> 10))).Append((char)(0xdc00 + (v & 1023))); }
        }
        string result = Call<string>("PyStrRepr", text.ToString());
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            string hash = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(result))).Replace("-", "").ToLowerInvariant();
            Assert.Equal("b738ace0b56f2f00f9eb0b97302341d44dbf16e44d9519e05e5cd0697230e4a8", hash);
        }
    }
}
