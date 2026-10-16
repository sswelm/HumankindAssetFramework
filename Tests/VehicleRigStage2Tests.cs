using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>vehicle_rig.py's stage 2 in C# (BlenderVehicleRig.Stage2 + BlenderWeld): the reduce dials, the kd-tree doubles, the
/// weld on a split box, the sharp marking and the pool's free list - the rules the drill holds against Blender, pinned on the
/// shapes a unit test can hold.</summary>
public class VehicleRigStage2Tests
{
    static List<string> Argv(Dictionary<int, string> overrides = null)
    {
        var argv = Enumerable.Repeat("", 76).ToList();
        argv[0] = "rig"; argv[1] = "D:/m/a.glb"; argv[2] = "D:/m/a_Spin.glb"; argv[6] = "AUTO"; argv[7] = "15"; argv[8] = "-360";
        if (overrides != null) foreach (var kv in overrides) argv[kv.Key] = kv.Value;
        return argv;
    }

    [Fact]
    public void The_reduce_dials_are_clamped_as_python_clamps_them_and_a_bad_one_is_a_death()
    {
        var a = BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [50] = "75", [54] = "120", [61] = "-5", [62] = "nan", [64] = "inf", [70] = " 12.5 " }));
        Assert.Equal(75.0, a.Reduce["rigging"]); Assert.Equal(95.0, a.Reduce["bodies"]); Assert.Equal(0.0, a.Reduce["oars"]);
        Assert.Equal(0.0, a.Reduce["sails"]); Assert.Equal(95.0, a.Reduce["rudders"]); Assert.Equal(12.5, a.Reduce["detail"]); Assert.Equal(0.0, a.Reduce["wheels"]);
        Assert.Null(a.NamesDie);
        var argv = Argv(); argv.Add("gunreduce=30"); argv.Add("windowreduce=|99.5"); argv.Add("shroudreduce=|7");
        var b = BlenderVehicleRig.ParseArgs(argv);
        Assert.Equal(30.0, b.Reduce["guns"]); Assert.Equal(95.0, b.Reduce["window"]); Assert.Equal(7.0, b.Reduce["shroud"]);
        Assert.Null(b.TagError);
        argv.Add("defaultreduce=|x");
        Assert.StartsWith("VEHICLE ERROR: malformed defaultreduce argument", BlenderVehicleRig.ParseArgs(argv).TagError);
        // turretreduce= (2026-10-16): percent-only like gunreduce, clamped the same way, absent = 0
        Assert.Equal(40.0, BlenderVehicleRig.ParseArgs(With("turretreduce=40")).Reduce["turrets"]);
        Assert.Equal(95.0, BlenderVehicleRig.ParseArgs(With("turretreduce=150")).Reduce["turrets"]);
        Assert.False(BlenderVehicleRig.ParseArgs(Argv()).Reduce.TryGetValue("turrets", out double none) && none != 0.0);
        Assert.StartsWith("ValueError", BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [52] = "half" })).NamesDie);
    }

    static List<string> With(string token) { var argv = Argv(); argv.Add(token); return argv; }

    /// <summary>A unit box as a glTF carries it: four vertices per face, 24 in all, twelve triangles; vertex v of face f at 4f + v.</summary>
    static BlenderRigAnim.MeshData SplitBox(int[] faceMaterial = null)
    {
        var corners = new[]
        {
            new[] { (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0) }, new[] { (0, 0, 1), (0, 1, 1), (1, 1, 1), (1, 0, 1) }, new[] { (0, 0, 0), (0, 0, 1), (1, 0, 1), (1, 0, 0) },
            new[] { (0, 1, 0), (1, 1, 0), (1, 1, 1), (0, 1, 1) }, new[] { (0, 0, 0), (0, 1, 0), (0, 1, 1), (0, 0, 1) }, new[] { (1, 0, 0), (1, 0, 1), (1, 1, 1), (1, 1, 0) },
        };
        var d = new BlenderRigAnim.MeshData { Vertices = 24, Positions = new float[72], Faces = new int[36], FaceMaterial = faceMaterial ?? new int[12], FaceSharp = new bool[12] };
        var uv = new float[72];
        for (int f = 0; f < 6; f++)
        {
            for (int v = 0; v < 4; v++) { var (x, y, z) = corners[f][v]; d.Positions[3 * (4 * f + v)] = x; d.Positions[3 * (4 * f + v) + 1] = y; d.Positions[3 * (4 * f + v) + 2] = z; }
            d.Faces[6 * f] = 4 * f; d.Faces[6 * f + 1] = 4 * f + 1; d.Faces[6 * f + 2] = 4 * f + 2;
            d.Faces[6 * f + 3] = 4 * f; d.Faces[6 * f + 4] = 4 * f + 2; d.Faces[6 * f + 5] = 4 * f + 3;
        }
        for (int c = 0; c < 36; c++) { uv[2 * c] = c * 0.01f; uv[2 * c + 1] = 1f - c * 0.01f; }
        d.UvAll.Add(uv); d.Uv = uv;
        // an edge list for the test (per face, each corner's edge once, low then high) - the importer's own order is mesh_calc_edges'
        var edges = new List<int>(); var seen = new HashSet<long>();
        for (int f = 0; f < 12; f++)
            for (int k = 0; k < 3; k++)
            {
                int a = d.Faces[3 * f + k], b = d.Faces[3 * f + (k + 1) % 3]; long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                if (seen.Add(key)) { edges.Add(Math.Min(a, b)); edges.Add(Math.Max(a, b)); }
            }
        d.Edges = edges.ToArray();
        return d;
    }

    static BMesh Build(BlenderRigAnim.MeshData d, BlenderWeld.Layers layers)
    {
        var m = typeof(BlenderVehicleRig).GetMethod("FromMeshData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (BMesh)m.Invoke(null, new object[] { d, layers, true, false });
    }

    [Fact]
    public void Doubles_within_range_map_to_the_lower_index_of_a_pair_and_the_most_central_of_a_cluster()
    {
        var d = SplitBox(); var layers = new BlenderWeld.Layers(); var bm = Build(d, layers);
        var all = Enumerable.Range(0, 24).ToList();
        var map = BlenderWeld.FindDoubles(bm, all, 1e-5f);
        // every corner of the box is held by three faces: 24 vertices in eight clusters of three, 16 merge into 8 kept
        Assert.Equal(16, map.Count);
        var kept = new HashSet<int>(map.Values);
        Assert.Equal(8, kept.Count);
        foreach (var kv in map) { Assert.DoesNotContain(kv.Key, kept); for (int k = 0; k < 3; k++) Assert.Equal(d.Positions[3 * kv.Key + k], d.Positions[3 * kv.Value + k]); }
        // out of range: nothing merges
        Assert.Empty(BlenderWeld.FindDoubles(bm, new List<int> { 0, 5 }, 1e-5f));
    }

    [Fact]
    public void The_weld_closes_a_split_box_to_eight_vertices_twelve_faces_and_marks_its_edges_sharp()
    {
        var d = SplitBox(); var layers = new BlenderWeld.Layers(); var bm = Build(d, layers);
        int merged = BlenderWeld.RemoveDoubles(bm, layers, Enumerable.Range(0, 24).ToList(), 1e-5f);
        Assert.Equal(16, merged);
        Assert.Equal(8, bm.TotVert); Assert.Equal(12, bm.TotFace); Assert.Equal(18, bm.TotEdge); Assert.Equal(36, bm.TotLoop);
        int sharp = BlenderWeld.MarkSharpOverAngle(bm, 40.0);
        Assert.Equal(12, sharp);   // the twelve box edges meet at 90 degrees; the six face diagonals are flat
        var m = typeof(BlenderVehicleRig).GetMethod("ToMeshData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var r = (BlenderRigAnim.MeshData)m.Invoke(null, new object[] { bm, layers, d });
        Assert.Equal(8, r.Vertices); Assert.Equal(36, r.Faces.Length); Assert.Equal(18, r.EdgeSharp.Length); Assert.Equal(12, r.EdgeSharp.Count(x => x));
        // the corner UVs travelled with their corners on every face - the kept ones and the re-made ones (a face with a merged corner
        // is re-made from the mapped corners and swapped into its old slot, each new loop taking the old corner's data)
        Assert.Equal(d.Uv, r.Uv);
        Assert.Equal(d.FaceMaterial, r.FaceMaterial);
        // a face's three corners name three distinct vertices, each within the unit box
        for (int f = 0; f < 12; f++) Assert.Equal(3, new HashSet<int> { r.Faces[3 * f], r.Faces[3 * f + 1], r.Faces[3 * f + 2] }.Count);
    }

    [Fact]
    public void The_weld_within_a_material_leaves_the_border_split()
    {
        var mats = new int[12]; for (int f = 6; f < 12; f++) mats[f] = 1;   // faces 0-2 (the first three box sides) on material 0, 3-5 on material 1
        var d = SplitBox(mats); var layers = new BlenderWeld.Layers(); var bm = Build(d, layers);
        // the script's grouping: a vertex whose corners all share one material; vertices on the border belong to one face each here
        var groups = new List<List<int>> { new List<int>(), new List<int>() };
        var loops = new List<int>();
        for (int v = 0; v < 24; v++) { bm.LoopsOfVert(v, loops); int m0 = bm.FMat[bm.LF[loops[0]]]; if (loops.All(l => bm.FMat[bm.LF[l]] == m0)) groups[m0].Add(v); }
        int merged = 0; foreach (var g in groups) merged += BlenderWeld.RemoveDoubles(bm, layers, g, 1e-5f);
        // each material's three sides share two box edges pairwise (two corners each) and no corner three ways: 4 merges a side,
        // 24 -> 16, as Blender's own run of the fx_c_weld fixture prints ("welded from 24 raw" to 16)
        Assert.Equal(8, merged); Assert.Equal(16, bm.TotVert);
    }

    [Fact]
    public void A_killed_slot_is_reused_by_the_next_creation_last_freed_first()
    {
        var bm = new BMesh();
        int v0 = bm.VertCreate(0, 0, 0), v1 = bm.VertCreate(1, 0, 0), v2 = bm.VertCreate(0, 1, 0);
        bm.VertKill(v1); bm.VertKill(v2);
        Assert.Equal(v2, bm.VertCreate(5, 5, 5));   // the last freed comes back first
        Assert.Equal(v1, bm.VertCreate(6, 6, 6));
        Assert.Equal(3, bm.VertCreate(7, 7, 7));    // then a new slot
        Assert.Equal(4, bm.TotVert); Assert.True(bm.VAlive[v0]);
    }

    [Fact]
    public void The_angle_between_unit_normals_is_blenders_two_asin_form()
    {
        Assert.Equal(0f, BlenderWeld.AngleNormalized(0, 0, 1, 0, 0, 1));
        Assert.Equal((float)Math.PI, BlenderWeld.AngleNormalized(0, 0, 1, 0, 0, -1), 6);
        Assert.Equal((float)(Math.PI / 2), BlenderWeld.AngleNormalized(1, 0, 0, 0, 1, 0), 6);
    }

    [Fact]
    public void A_bad_reduce_dial_dies_before_a_later_names_file_is_opened()
    {
        var a = BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [52] = "half", [69] = "@C:/no/such/detail.txt" }));
        Assert.Equal("ValueError: could not convert string to float: 'half'", a.NamesDie);
        var b = BlenderVehicleRig.ParseArgs(Argv(new Dictionary<int, string> { [51] = "@C:/no/such/structure.txt", [52] = "half" }));
        Assert.Equal("FileNotFoundError: [Errno 2] No such file or directory: 'C:/no/such/structure.txt'", b.NamesDie);
    }
}
