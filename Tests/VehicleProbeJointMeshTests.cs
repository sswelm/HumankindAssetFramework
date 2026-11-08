using System;
using System.Linq;
using Xunit;

/// <summary>A mesh ON a joint node (the importer's '.mesh' child) and one BELOW it, through the probe's bone chain, against the rows
/// Blender 5.1.2's probe (vehicle_rig.py probe posestart=1, tools/vehicle-probe-drill/blender_probe_many.py) printed on 2026-11-08 for
/// the probe drill's own fixtures (tools/vehicle-probe-drill/naming_fixtures.py: mesh_on_bone, mesh_on_bone_animated - embedded here
/// byte for byte). The drill holds the same rows live; this holds them in the unit suite, where a regression surfaces first.</summary>
public class VehicleProbeJointMeshTests
{
    static HafModel Read(string b64) => GlbReader.Read(Convert.FromBase64String(b64));

    // mesh_on_bone.glb: the Turret joint carries turret_mesh; the rest pose
    const string MeshOnBone =
            "Z2xURgIAAACMBQAAvAQAAEpTT057ImFzc2V0Ijp7InZlcnNpb24iOiIyLjAiLCJnZW5lcmF0b3IiOiJmaXh0dXJlcy5weSBtZXNoX29uX2JvbmUifSwibWVzaGVzIjpbeyJuYW1lIjoidHVycmV0X2" +
            "1lc2giLCJwcmltaXRpdmVzIjpbeyJhdHRyaWJ1dGVzIjp7IlBPU0lUSU9OIjowfX1dfSx7Im5hbWUiOiJiYXNlX21lc2giLCJwcmltaXRpdmVzIjpbeyJhdHRyaWJ1dGVzIjp7IlBPU0lUSU9OIjox" +
            "fX1dfSx7Im5hbWUiOiJza2luX21lc2giLCJwcmltaXRpdmVzIjpbeyJhdHRyaWJ1dGVzIjp7IlBPU0lUSU9OIjoyLCJKT0lOVFNfMCI6MywiV0VJR0hUU18wIjo0fX1dfV0sIm5vZGVzIjpbeyJuYW" +
            "1lIjoiUm9vdCIsIm1lc2giOjEsImNoaWxkcmVuIjpbMV19LHsibmFtZSI6IlR1cnJldCIsIm1lc2giOjAsInRyYW5zbGF0aW9uIjpbMCwyLDBdfSx7Im5hbWUiOiJTa2lubmVkIiwibWVzaCI6Miwi" +
            "c2tpbiI6MH1dLCJza2lucyI6W3sibmFtZSI6IlJpZyIsImpvaW50cyI6WzFdfV0sInNjZW5lcyI6W3sibm9kZXMiOlswLDJdfV0sInNjZW5lIjowLCJidWZmZXJzIjpbeyJieXRlTGVuZ3RoIjoxOD" +
            "B9XSwiYnVmZmVyVmlld3MiOlt7ImJ1ZmZlciI6MCwiYnl0ZU9mZnNldCI6MCwiYnl0ZUxlbmd0aCI6MzZ9LHsiYnVmZmVyIjowLCJieXRlT2Zmc2V0IjozNiwiYnl0ZUxlbmd0aCI6MzZ9LHsiYnVm" +
            "ZmVyIjowLCJieXRlT2Zmc2V0Ijo3MiwiYnl0ZUxlbmd0aCI6MzZ9LHsiYnVmZmVyIjowLCJieXRlT2Zmc2V0IjoxMDgsImJ5dGVMZW5ndGgiOjI0fSx7ImJ1ZmZlciI6MCwiYnl0ZU9mZnNldCI6MT" +
            "MyLCJieXRlTGVuZ3RoIjo0OH1dLCJhY2Nlc3NvcnMiOlt7ImJ1ZmZlclZpZXciOjAsImNvbXBvbmVudFR5cGUiOjUxMjYsInR5cGUiOiJWRUMzIiwiY291bnQiOjMsIm1pbiI6WzMsMCwwXSwibWF4" +
            "IjpbNC4wLDEuMCwwXX0seyJidWZmZXJWaWV3IjoxLCJjb21wb25lbnRUeXBlIjo1MTI2LCJ0eXBlIjoiVkVDMyIsImNvdW50IjozLCJtaW4iOlswLjAsMCwwXSwibWF4IjpbMS4wLDEuMCwwXX0sey" +
            "JidWZmZXJWaWV3IjoyLCJjb21wb25lbnRUeXBlIjo1MTI2LCJ0eXBlIjoiVkVDMyIsImNvdW50IjozLCJtaW4iOlswLjAsMCwwXSwibWF4IjpbMS4wLDEuMCwwXX0seyJidWZmZXJWaWV3IjozLCJj" +
            "b21wb25lbnRUeXBlIjo1MTIzLCJ0eXBlIjoiVkVDNCIsImNvdW50IjozfSx7ImJ1ZmZlclZpZXciOjQsImNvbXBvbmVudFR5cGUiOjUxMjYsInR5cGUiOiJWRUM0IiwiY291bnQiOjN9XX20AAAAQk" +
            "lOAAAAQEAAAAAAAAAAAAAAgEAAAAAAAAAAAAAAQEAAAIA/AAAAAAAAAAAAAAAAAAAAAAAAgD8AAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAAAAAAAAgD8AAAAAAAAAAAAAAAAAAIA/AAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgD8AAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAA==";

    // mesh_on_bone_animated.glb: turret_mesh ON the animated Turret joint, Barrel BELOW it; the first clip ("aim") starts turned
    // 35 degrees and shifted, a second clip must not leak into frame 0
    const string MeshOnBoneAnimated =
            "Z2xURgIAAADMCQAAlAgAAEpTT057ImFzc2V0Ijp7InZlcnNpb24iOiIyLjAiLCJnZW5lcmF0b3IiOiJmaXh0dXJlcy5weSBtZXNoX29uX2JvbmVfYW5pbWF0ZWQifSwibWVzaGVzIjpbeyJuYW1lIj" +
            "oidHVycmV0X21lc2giLCJwcmltaXRpdmVzIjpbeyJhdHRyaWJ1dGVzIjp7IlBPU0lUSU9OIjo1fX1dfSx7Im5hbWUiOiJiYXJyZWxfbWVzaCIsInByaW1pdGl2ZXMiOlt7ImF0dHJpYnV0ZXMiOnsi" +
            "UE9TSVRJT04iOjZ9fV19LHsibmFtZSI6InNraW5fbWVzaCIsInByaW1pdGl2ZXMiOlt7ImF0dHJpYnV0ZXMiOnsiUE9TSVRJT04iOjcsIkpPSU5UU18wIjo4LCJXRUlHSFRTXzAiOjl9fV19XSwibm" +
            "9kZXMiOlt7Im5hbWUiOiJSb290IiwiY2hpbGRyZW4iOlsxXX0seyJuYW1lIjoiVHVycmV0IiwibWVzaCI6MCwidHJhbnNsYXRpb24iOlswLDIsMF0sImNoaWxkcmVuIjpbM119LHsibmFtZSI6IlNr" +
            "aW5uZWQiLCJtZXNoIjoyLCJza2luIjowfSx7Im5hbWUiOiJCYXJyZWwiLCJtZXNoIjoxLCJ0cmFuc2xhdGlvbiI6WzAsMC40LDEuMl19XSwic2tpbnMiOlt7Im5hbWUiOiJSaWciLCJqb2ludHMiOl" +
            "sxXX1dLCJzY2VuZXMiOlt7Im5vZGVzIjpbMCwyXX1dLCJzY2VuZSI6MCwiYW5pbWF0aW9ucyI6W3sibmFtZSI6ImFpbSIsInNhbXBsZXJzIjpbeyJpbnB1dCI6MCwib3V0cHV0IjoxfSx7ImlucHV0" +
            "IjowLCJvdXRwdXQiOjJ9XSwiY2hhbm5lbHMiOlt7InNhbXBsZXIiOjAsInRhcmdldCI6eyJub2RlIjoxLCJwYXRoIjoicm90YXRpb24ifX0seyJzYW1wbGVyIjoxLCJ0YXJnZXQiOnsibm9kZSI6MS" +
            "wicGF0aCI6InRyYW5zbGF0aW9uIn19XX0seyJuYW1lIjoic3dlZXAiLCJzYW1wbGVycyI6W3siaW5wdXQiOjMsIm91dHB1dCI6NH1dLCJjaGFubmVscyI6W3sic2FtcGxlciI6MCwidGFyZ2V0Ijp7" +
            "Im5vZGUiOjEsInBhdGgiOiJyb3RhdGlvbiJ9fV19XSwiYnVmZmVycyI6W3siYnl0ZUxlbmd0aCI6Mjg0fV0sImJ1ZmZlclZpZXdzIjpbeyJidWZmZXIiOjAsImJ5dGVPZmZzZXQiOjAsImJ5dGVMZW" +
            "5ndGgiOjh9LHsiYnVmZmVyIjowLCJieXRlT2Zmc2V0Ijo4LCJieXRlTGVuZ3RoIjozMn0seyJidWZmZXIiOjAsImJ5dGVPZmZzZXQiOjQwLCJieXRlTGVuZ3RoIjoyNH0seyJidWZmZXIiOjAsImJ5" +
            "dGVPZmZzZXQiOjY0LCJieXRlTGVuZ3RoIjo4fSx7ImJ1ZmZlciI6MCwiYnl0ZU9mZnNldCI6NzIsImJ5dGVMZW5ndGgiOjMyfSx7ImJ1ZmZlciI6MCwiYnl0ZU9mZnNldCI6MTA0LCJieXRlTGVuZ3" +
            "RoIjozNn0seyJidWZmZXIiOjAsImJ5dGVPZmZzZXQiOjE0MCwiYnl0ZUxlbmd0aCI6MzZ9LHsiYnVmZmVyIjowLCJieXRlT2Zmc2V0IjoxNzYsImJ5dGVMZW5ndGgiOjM2fSx7ImJ1ZmZlciI6MCwi" +
            "Ynl0ZU9mZnNldCI6MjEyLCJieXRlTGVuZ3RoIjoyNH0seyJidWZmZXIiOjAsImJ5dGVPZmZzZXQiOjIzNiwiYnl0ZUxlbmd0aCI6NDh9XSwiYWNjZXNzb3JzIjpbeyJidWZmZXJWaWV3IjowLCJjb2" +
            "1wb25lbnRUeXBlIjo1MTI2LCJ0eXBlIjoiU0NBTEFSIiwiY291bnQiOjIsIm1pbiI6WzAuMF0sIm1heCI6WzEuMF19LHsiYnVmZmVyVmlldyI6MSwiY29tcG9uZW50VHlwZSI6NTEyNiwidHlwZSI6" +
            "IlZFQzQiLCJjb3VudCI6Mn0seyJidWZmZXJWaWV3IjoyLCJjb21wb25lbnRUeXBlIjo1MTI2LCJ0eXBlIjoiVkVDMyIsImNvdW50IjoyfSx7ImJ1ZmZlclZpZXciOjMsImNvbXBvbmVudFR5cGUiOj" +
            "UxMjYsInR5cGUiOiJTQ0FMQVIiLCJjb3VudCI6MiwibWluIjpbMC4wXSwibWF4IjpbMi4wXX0seyJidWZmZXJWaWV3Ijo0LCJjb21wb25lbnRUeXBlIjo1MTI2LCJ0eXBlIjoiVkVDNCIsImNvdW50" +
            "IjoyfSx7ImJ1ZmZlclZpZXciOjUsImNvbXBvbmVudFR5cGUiOjUxMjYsInR5cGUiOiJWRUMzIiwiY291bnQiOjMsIm1pbiI6WzMsMCwwXSwibWF4IjpbNC4wLDEuMCwwXX0seyJidWZmZXJWaWV3Ij" +
            "o2LCJjb21wb25lbnRUeXBlIjo1MTI2LCJ0eXBlIjoiVkVDMyIsImNvdW50IjozLCJtaW4iOlsxLDAsMF0sIm1heCI6WzEuNSwwLjUsMF19LHsiYnVmZmVyVmlldyI6NywiY29tcG9uZW50VHlwZSI6" +
            "NTEyNiwidHlwZSI6IlZFQzMiLCJjb3VudCI6MywibWluIjpbMC4wLDAsMF0sIm1heCI6WzEuMCwxLjAsMF19LHsiYnVmZmVyVmlldyI6OCwiY29tcG9uZW50VHlwZSI6NTEyMywidHlwZSI6IlZFQz" +
            "QiLCJjb3VudCI6M30seyJidWZmZXJWaWV3Ijo5LCJjb21wb25lbnRUeXBlIjo1MTI2LCJ0eXBlIjoiVkVDNCIsImNvdW50IjozfV19ICAcAQAAQklOAAAAAAAAAIA/AAAAABz2mT4AAAAAyyZ0PwAA" +
            "AAC7jSQ/AAAAAH0bRD8AAIA+AAAAQAAAAL8AAAAAAAAgQAAAAAAAAAAAAAAAQAAAAAAAAAC/AAAAANezXT8AAAAAtn6yPQAAAACeBn8/AABAQAAAAAAAAAAAAACAQAAAAAAAAAAAAABAQAAAgD8AAA" +
            "AAAACAPwAAAAAAAAAAAADAPwAAAAAAAAAAAACAPwAAAD8AAAAAAAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAgD8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAA" +
            "AAAAgD8AAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAA";

    static string Row(VehicleProbe.Result r, string kind, string name)
    {
        var p = r.Parts.Single(x => x.Name == name);
        string row = kind == "MATRIX" ? p.MatrixRow : p.VertexRow;
        Assert.EndsWith("|bone", row);   // held, not counted
        return row.Substring(0, row.Length - "|bone".Length);
    }

    [Theory]
    [InlineData("static", "MATRIX|turret_mesh|1.0 -3.422854533141617e-08 -4.5989077888768104e-15 0.0 3.422854177870249e-08 1.0000001192092896 1.685874053691805e-07 0.0 -1.1715931027525724e-15 -1.3435885648505064e-07 1.0000001192092896 2.0 0.0 0.0 0.0 1.0")]
    [InlineData("static", "VERTEX|turret_mesh|3.0 1.0268562533610748e-07 2.0|3.422854177870249e-08 -1.0 1.3435884227419592e-07")]
    [InlineData("animated", "MATRIX|Barrel|0.8191520571708679 -0.5735764503479004 -7.706506721660844e-08 0.9382916688919067 0.5735763907432556 0.8191521763801575 1.4428887595840933e-07 -0.4829825162887573 -9.597128931945927e-16 -1.3435885648505064e-07 1.0000001192092896 2.4000000953674316 0.0 0.0 0.0 1.0")]
    [InlineData("animated", "MATRIX|turret_mesh|0.8191520571708679 -0.5735764503479004 -7.706506721660844e-08 0.2499999701976776 0.5735763907432556 0.8191521763801575 1.4428887595840933e-07 0.5 -9.597128931945927e-16 -1.3435885648505064e-07 1.0000001192092896 2.0 0.0 0.0 0.0 1.0")]
    [InlineData("animated", "VERTEX|Barrel|1.7574436664581299 0.09059387445449829 2.4000000953674316|0.5735763907432556 -0.8191520571708679 1.3435884227419592e-07")]
    [InlineData("animated", "VERTEX|turret_mesh|2.707456111907959 2.220729112625122 2.0|0.5735763907432556 -0.8191520571708679 1.3435884227419592e-07")]
    public void The_rows_equal_Blenders_bit_for_bit(string fixture, string blenderRow)
    {
        var r = VehicleProbe.Run(Read(fixture == "static" ? MeshOnBone : MeshOnBoneAnimated));
        string kind = blenderRow.Substring(0, blenderRow.IndexOf('|'));
        string name = blenderRow.Split('|')[1];
        // as compare_probe.py compares them: every value as float32 bits (C# prints 3 where Python prints 3.0)
        Assert.Equal(Bits(blenderRow), Bits(Row(r, kind, name)));
    }

    static uint[] Bits(string row) => row.Split('|').Skip(2).SelectMany(f => f.Split(' '))
        .Select(t => { Assert.True(GlbReader.ReadPythonFloat(t, out double d), t); return BitConverter.ToUInt32(BitConverter.GetBytes((float)d), 0); }).ToArray();
}
