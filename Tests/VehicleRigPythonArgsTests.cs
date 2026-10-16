using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>The Python number and whitespace rules vehicle_rig.py's arguments go through (VehicleProbe.Input.PyFloat, PyNumberAscii,
/// PyStrip; BlenderVehicleRig.TryPyInt, ParseArgs): every expected value below is what Blender 5.1.2's Python 3.13.9 returned for
/// float(), int() and str.strip() of the same text (2026-10-16).</summary>
public class VehicleRigPythonArgsTests
{
    [Theory]
    [InlineData("1_0", 10.0)]
    [InlineData("1e1_0", 1e10)]
    [InlineData("-inf", double.NegativeInfinity)]
    [InlineData("+Infinity", double.PositiveInfinity)]
    [InlineData(" 2.5 ", 2.5)]
    [InlineData("\u0661\u0662", 12.0)]            // Arabic-Indic digits
    [InlineData("\u20031\u2003", 1.0)]            // em spaces
    [InlineData("1\u0085", 1.0)]                  // NEL
    [InlineData(".5", 0.5)]
    [InlineData("5.", 5.0)]
    [InlineData("1.5e-3", 0.0015)]
    [InlineData("\U0001D7CE", 0.0)]               // mathematical bold digit zero (a supplementary decimal digit)
    [InlineData("\u00a0 7", 7.0)]
    public void Python_float_accepts(string text, double value) => Assert.Equal(value, VehicleProbe.Input.PyFloat(text));

    [Fact]
    public void Python_float_keeps_a_negative_zero_and_nan()
    {
        Assert.True(BitConverter.DoubleToInt64Bits(VehicleProbe.Input.PyFloat("-0")) < 0);   // the sign bit: -0.0
        Assert.True(double.IsNaN(VehicleProbe.Input.PyFloat("nan")));
    }

    [Theory]
    [InlineData("1__0")] [InlineData("_1")] [InlineData("1_")] [InlineData("1_.5")] [InlineData("1._5")]
    [InlineData("--inf")] [InlineData("+-nan")] [InlineData("\u001c3\u001f")]   // \x1c-\x1f: str.strip() takes them, float() does not
    [InlineData("1e")] [InlineData("e1")] [InlineData("0x10")] [InlineData("")]
    public void Python_float_refuses(string text) => Assert.Throws<FormatException>(() => VehicleProbe.Input.PyFloat(text));

    [Theory]
    [InlineData("1_0", true)] [InlineData("\u0661\u0662", true)] [InlineData("\u20031\u2003", true)] [InlineData("1\u0085", true)]
    [InlineData("-0", true)] [InlineData("\U0001D7CE", true)] [InlineData("\u00a0 7", true)]
    [InlineData("1__0", false)] [InlineData("1e1_0", false)] [InlineData(" 2.5 ", false)] [InlineData("nan", false)] [InlineData("\u001c3\u001f", false)]
    public void Python_int_accepts_what_Python_accepts(string text, bool ok)
        => Assert.Equal(ok, (bool)typeof(BlenderVehicleRig).GetMethod("TryPyInt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { text }));

    [Theory]
    [InlineData("\u001c3\u001f", "3")] [InlineData("\u20031\u2003", "1")] [InlineData("1\u0085", "1")] [InlineData("\u00a0 7", "7")]
    [InlineData(" 2.5 ", "2.5")] [InlineData("\u180e1", "\u180e1")]   // U+180E is no longer a space (Unicode 6.3)
    public void Python_strip(string text, string stripped) => Assert.Equal(stripped, VehicleProbe.Input.PyStrip(text));

    static List<string> Argv(params (int i, string v)[] set)
    {
        var a = Enumerable.Repeat("", 76).ToList(); a[0] = "rig"; a[1] = "C:/x.glb"; a[2] = "C:/o.glb"; a[3] = "C:/p.fbx"; a[7] = "15"; a[8] = "0";
        foreach (var (i, v) in set) a[i] = v;
        return a;
    }

    [Fact]
    public void A_flag_fold_frame_count_past_int_max_stays_exact()
    {
        var argv = Argv(); argv.Add("flagfold=10|3e9");
        var a = BlenderVehicleRig.ParseArgs(argv);
        Assert.True(a.FlagFoldOn); Assert.Equal("3000000000", a.FlagFoldFrames.ToString());
    }

    [Theory]
    [InlineData(72, "x", "ValueError: could not convert string to float: 'x'")]
    [InlineData(72, "inf", "OverflowError: cannot convert float infinity to integer")]
    [InlineData(72, "nan", "ValueError: cannot convert float NaN to integer")]
    [InlineData(73, "x", "ValueError: could not convert string to float: 'x'")]
    [InlineData(75, "x", "ValueError: could not convert string to float: 'x'")]
    [InlineData(56, "1__0", "ValueError: could not convert string to float: '1__0'")]
    [InlineData(59, "y", "ValueError: could not convert string to float: 'y'")]
    public void A_sail_fold_or_oar_dial_Python_cannot_parse_kills_the_script(int index, string value, string death)
        => Assert.Equal(death, BlenderVehicleRig.ParseArgs(Argv((index, value))).FoldDie);

    [Fact]
    public void The_sail_fold_switch_strips_as_Python_does()
    {
        Assert.True(BlenderVehicleRig.ParseArgs(Argv((71, "1\u001f"))).SailFold);
        Assert.False(BlenderVehicleRig.ParseArgs(Argv((71, "0"))).SailFold);
        Assert.Null(BlenderVehicleRig.ParseArgs(Argv((56, "1_0"), (72, " 8 "))).FoldDie);
    }
}
