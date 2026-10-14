using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

public class VehicleRigEvidenceTests
{
    static readonly string H = new string('0', 64);
    static string Obj(string name) => "OBJ\t1\t" + name + "\tEMPTY\t-\tOBJECT\t-\t-\t-\t-\t" + string.Join("\t", Enumerable.Repeat("00000000", 32));
    static Dictionary<string, (bool fast, string[] argv)> Jobs() => new Dictionary<string, (bool, string[])> { ["test"] = (false, new string[76]) };
    static List<string> Fixture()
    {
        var rows = new List<string> { "JOB\ttest" };
        for (int n = 1; n <= 5; n++)
        {
            rows.Add("LOG\t" + n + "\tVEHICLE stage " + n);
            rows.Add("STAGE\t" + n); rows.Add("SCENE\t" + n + "\t0\t1\t0\t-");
            rows.Add(Obj("Root").Replace("OBJ\t1\t", "OBJ\t" + n + "\t"));
            rows.Add(Obj("Unused").Replace("OBJ\t1\t", "OBJ\t" + n + "\t"));
            rows.Add("ACT\t" + n + "\tUnusedAction\t00000000\t3f800000\t0\t0");
        }
        rows.AddRange(new[] {
            "X\tGLB\t140\t100\t12\t" + H, "X\tSCENE\tScene\t0", "X\tNODE\t0\tRoot\t-\t0\t0\t-\t-\t-",
            "X\tSKIN\t0\tRig\t0\t0", "X\tIBM\t0\t0\t" + string.Join("\t", Enumerable.Repeat("00000000", 16)),
            "X\tMESH\t0\tMesh\t1", "X\tPRIM\t0\t0\t0\t4\t3\t" + H + "\tPOSITION:3:VEC3/5126:" + H,
            "X\tMAT\t0\t{}", "X\tIMG\t0\tTex\timage/png\t" + H, "X\tTEX\t0\t0\t0\t{}", "X\tSAMP\t0\t{}",
            "X\tANIM\t0\tSpin\t1\t1", "X\tCHAN\t0\t0\t0\trotation\t0\tLINEAR\t1\t00000000\t3f800000\t" + H + "\t" + H,
            "DONE\ttest" });
        return Seal(rows);
    }
    static List<string> Seal(List<string> rows)
    {
        rows = rows.Where(r => !r.StartsWith("EVIDENCE\t")).ToList();
        var recorded = rows.Take(rows.Count - 1).ToList();
        using (var sha = SHA256.Create())
        {
            string hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", recorded) + "\n"))).Replace("-", "").ToLowerInvariant();
            rows.Insert(rows.Count - 1, "EVIDENCE\ttest\t" + recorded.Count + "\t" + hash);
        }
        return rows;
    }
    static VehicleRigDump Parse(List<string> rows) => VehicleRigDump.Parse(rows, Jobs(), false);
    [Fact] public void Intact_evidence_passes() => Assert.True(Parse(Fixture()).Completed);
    [Theory]
    [InlineData("NODE")][InlineData("SKIN")][InlineData("IBM")][InlineData("MESH")][InlineData("PRIM")]
    [InlineData("MAT")][InlineData("IMG")][InlineData("TEX")][InlineData("SAMP")][InlineData("ANIM")][InlineData("CHAN")]
    public void Missing_export_rows_fail(string kind) => Assert.Throws<InvalidDataException>(() => Parse(Fixture().Where(l => !l.StartsWith("X\t" + kind + "\t")).ToList()));
    [Fact] public void Header_only_export_fails() => Assert.Throws<InvalidDataException>(() => Parse(Fixture().Where(l => !l.StartsWith("X\t") || l.StartsWith("X\tGLB\t")).ToList()));
    [Theory][InlineData("OBJ\t1\tUnused\t")][InlineData("ACT\t1\tUnusedAction\t")][InlineData("LOG\t1\t")]
    public void Missing_unreferenced_scene_or_log_rows_fail(string prefix) => Assert.Throws<InvalidDataException>(() => Parse(Fixture().Where(l => !l.StartsWith(prefix)).ToList()));
    [Theory]
    [InlineData("NODE", 4, "99")][InlineData("CHAN", 3, "99")][InlineData("PRIM", 3, "99")]
    [InlineData("GLB", 4, "bad-hash")][InlineData("MAT", 2, "[]")][InlineData("MESH", 3, "2")]
    [InlineData("ANIM", 3, "2")][InlineData("NODE", 1, "1")][InlineData("CHAN", 8, "not-bits")]
    public void Malformed_export_with_matching_envelope_fails(string kind, int field, string value)
    {
        var rows = Fixture(); int i = rows.FindIndex(l => l.StartsWith("X\t" + kind + "\t"));
        var fields = rows[i].Split('\t'); fields[field + 1] = value; rows[i] = string.Join("\t", fields);
        Assert.ThrowsAny<Exception>(() => Parse(Seal(rows)));
    }
    [Fact] public void Changed_unreferenced_object_bits_fail_checksum()
    {
        var rows = Fixture(); int i = rows.FindIndex(l => l.StartsWith("OBJ\t1\tUnused\t")); rows[i] = rows[i].Replace("00000000", "3f800000");
        Assert.Throws<InvalidDataException>(() => Parse(rows));
    }
    [Theory]
    [InlineData("probe", 1)][InlineData("", 1)][InlineData("rigfast", 1)][InlineData("rig", 0)]
    public void Job_mode_must_match_the_vehicle_rig_entry_point(string mode, int expected)
    {
        string jobs = Path.GetTempFileName(), dump = Path.GetTempFileName();
        try
        {
            var argv = Enumerable.Repeat("0", 76).ToArray(); argv[0] = mode;
            File.WriteAllText(jobs, "JOB\ttest\t" + string.Join("\t", argv) + "\n");
            File.WriteAllLines(dump, Fixture());
            Assert.Equal(expected, VehicleRigDrill.Main(new[] { jobs, dump }));
        }
        finally { File.Delete(jobs); File.Delete(dump); }
    }
    [Fact] public void Losing_an_optional_attribute_is_detected_by_integrity()
    {
        var rows = Fixture(); int i = rows.FindIndex(l => l.StartsWith("X\tPRIM\t"));
        rows[i] += "\tTEXCOORD_0:3:VEC2/5126:" + H; rows = Seal(rows);
        Assert.True(Parse(rows).Completed);
        rows[i] = rows[i].Substring(0, rows[i].LastIndexOf('\t'));
        Assert.Throws<InvalidDataException>(() => Parse(rows));
        Assert.True(Parse(Seal(rows)).Completed); // absence is valid GLB grammar, but not the original recorded evidence
    }
    [Fact] public void Short_export_row_fails()
    {
        var rows = Fixture(); int i = rows.FindIndex(l => l.StartsWith("X\tCHAN\t")); rows[i] = rows[i].Substring(0, rows[i].LastIndexOf('\t'));
        Assert.Throws<InvalidDataException>(() => Parse(Seal(rows)));
    }
}
