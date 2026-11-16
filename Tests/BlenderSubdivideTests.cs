using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

/// <summary>BlenderSubdivide against Blender 5.1's own bmesh.ops.subdivide_edges (2026-11-16): blender_subdivide.txt is what
/// tools/vehiclerig-drill/blender_subdivide_oracle.py printed - four meshes with a UV layer and a point colour layer, each
/// cut in passes exactly as vehicle_rig.py's tread code cuts (the edges over a threshold, one cut, grid fill) on ONE BMesh,
/// with the full state after every pass: every vertex position (bits), every edge's two vertices in order, every face's
/// corners in order, every corner's UV (bits), every vertex's colour. The C# must give the same state after each pass,
/// element for element: the tread code that follows walks vertices and edges by number.</summary>
public class BlenderSubdivideTests
{
    static float F(string hex) => BitConverter.ToSingle(BitConverter.GetBytes(uint.Parse(hex, NumberStyles.HexNumber)), 0);
    static string Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");
    static string[] Lines() => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "blender_subdivide.txt"));

    static string Section(string line, string name, string next)
    {
        int a = line.IndexOf(" " + name + " ", StringComparison.Ordinal) + name.Length + 2;
        int b = next == null ? line.Length : line.IndexOf(" " + next + " ", a - 1, StringComparison.Ordinal);
        return b < 0 ? line.Substring(a) : line.Substring(a, b - a);
    }

    /// <summary>The BMesh's state as the oracle prints it: elements in slot order, renumbered densely.</summary>
    static string State(BMesh bm, BlenderWeld.Layers layers)
    {
        var vi = new Dictionary<int, int>(); var sb = new StringBuilder("V");
        for (int v = 0; v < bm.VertCount; v++) if (bm.VAlive[v]) { vi[v] = vi.Count; for (int k = 0; k < 3; k++) sb.Append(' ').Append(Bits(bm.VCo[3 * v + k])); }
        sb.Append(" E");
        for (int e = 0; e < bm.EdgeCount; e++) if (bm.EAlive[e]) sb.Append(' ').Append(vi[bm.EV1[e]]).Append(' ').Append(vi[bm.EV2[e]]);
        sb.Append(" F "); var uv = new StringBuilder(); bool first = true;
        for (int f = 0; f < bm.FaceCount; f++)
        {
            if (!bm.FAlive[f]) continue;
            if (!first) sb.Append(';'); first = false;
            int l = bm.FLFirst[f], l0 = l, n = 0;
            do
            {
                if (n++ > 0) sb.Append(',');
                sb.Append(vi[bm.LV[l]]);
                uv.Append(' ').Append(Bits(layers.Uv[0][2 * l])).Append(' ').Append(Bits(layers.Uv[0][2 * l + 1]));
            } while ((l = bm.LNext[l]) != l0);
            Assert.Equal(bm.FLen[f], n);
        }
        sb.Append(" UV").Append(uv).Append(" COL");
        for (int v = 0; v < bm.VertCount; v++) if (bm.VAlive[v]) for (int k = 0; k < 4; k++) sb.Append(' ').Append(layers.PointColor[0][4 * v + k]);
        return sb.ToString();
    }

    [Theory]
    [InlineData("grid5", 2)]
    [InlineData("grid6", 2)]
    [InlineData("band", 2)]
    [InlineData("fan", 2)]
    [InlineData("quads", 3)]   // edges picked at random: quads and n-gons cut every way
    [InlineData("fins", 1)]    // a doubled face: the join across it is refused
    public void Every_pass_leaves_Blenders_mesh(string mesh, int passes) => Run(mesh, passes, null);

    /// <summary>The meshes together must take every branch of the operator the tread code can reach - a branch at zero is a
    /// mesh to add to the oracle before it counts as held.</summary>
    [Fact]
    public void The_meshes_take_every_branch()
    {
        var census = new int[BlenderSubdivide.CensusSlots];
        foreach (var (mesh, passes) in new[] { ("grid5", 2), ("grid6", 2), ("band", 2), ("fan", 2), ("quads", 3), ("fins", 1) }) Run(mesh, passes, census);
        var names = new[] { "the triangle pattern", "the quad grid", "three quad edges", "two cut edges joined", "two cut edges refused (the pair shares another face)", "two parallel edges at a corner", "an n-gon left with its new vertices" };
        var missing = Enumerable.Range(0, census.Length).Where(i => census[i] == 0).Select(i => names[i]).ToList();
        Assert.True(missing.Count == 0, "no mesh takes: " + string.Join("; ", missing) + " (census " + string.Join(",", census) + ")");
    }

    static void Run(string mesh, int passes, int[] census)
    {
        var lines = Lines();
        string start = lines.Single(l => l.StartsWith("STATE " + mesh + " 0 ", StringComparison.Ordinal));
        var co = Section(start, "V", "E").Split(' ').Select(F).ToArray();
        var edges = Section(start, "E", "F").Split(' ').Select(int.Parse).ToArray();
        var faces = Section(start, "F", "UV").Split(';').SelectMany(f => f.Split(',').Select(int.Parse)).ToArray();
        var uv = Section(start, "UV", "COL").Split(' ').Select(F).ToArray();
        var col = Section(start, "COL", null).Split(' ').Select(byte.Parse).ToArray();
        var bm = BMesh.FromMesh(new BlenderMesh { VertexCount = co.Length / 3, Faces = faces, ValidEdges = edges }, co);
        var layers = new BlenderWeld.Layers(); layers.Uv.Add(uv); layers.PointColor.Add(col);
        Assert.Equal(start.Substring(start.IndexOf(" V ", StringComparison.Ordinal) + 1), State(bm, layers));   // the reading itself
        int done = 0;
        for (int p = 1; p <= 3; p++)
        {
            string longLine = lines.SingleOrDefault(l => l.StartsWith("LONG " + mesh + " " + p, StringComparison.Ordinal));
            if (longLine == null) break;
            var cut = longLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Skip(3).Select(int.Parse).ToList();
            if (cut.Count == 0) break;
            // the oracle numbers edges densely; no element has died, so a slot is its number
            BlenderSubdivide.SubdivideEdges(bm, layers, cut, census);
            string want = lines.Single(l => l.StartsWith("STATE " + mesh + " " + p + " ", StringComparison.Ordinal));
            want = want.Substring(want.IndexOf(" V ", StringComparison.Ordinal) + 1);
            string got = State(bm, layers);
            if (want != got)
            {
                foreach (var (name, next) in new[] { ("V", "E"), ("E", "F"), ("F", "UV"), ("UV", "COL"), ("COL", (string)null) })
                {
                    string w = Section(" " + want, name, next), g = Section(" " + got, name, next);
                    if (w == g) continue;
                    char sep = name == "F" ? ';' : ' '; var ws = w.Split(sep); var gs = g.Split(sep);
                    int i = 0; while (i < ws.Length && i < gs.Length && ws[i] == gs[i]) i++;
                    Assert.Fail(mesh + " pass " + p + ": " + name + " differs at item " + i + " of " + ws.Length + " (here " + gs.Length + "): Blender '" + (i < ws.Length ? ws[i] : "-") + "', here '" + (i < gs.Length ? gs[i] : "-") + "'");
                }
            }
            done++;
        }
        Assert.Equal(passes, done);
    }
}
