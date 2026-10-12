// GlbDisconnectedParts.Explain.cs — why a part reads as the islands it reads (2026-10-12).
//
// The Bake Tests' Workshop row failed on salegs_revenge_split_fused with "23 split child(ren) are not one island": a
// child the split had just cut as ONE island read as two to seven when analysed on its own. A count cannot be
// investigated; this names the measure behind it. AnalyzeMesh welds positions on a grid whose cell is a ten-millionth
// of THE MESH'S OWN diagonal (and never under 1e-9): a child is smaller than the node it came from, so its cell is
// finer, and a part thousands of units from the origin has float32 coordinates whose ulp (the smallest step the
// format can hold) is LARGER than that cell — two vertices that are one surface, one ulp apart after a fuse's float32
// write, land in different cells and read as two islands. This measures exactly that for one node: the weld cell, the
// ulp at the mesh's coordinate magnitude, and the closest approach between every pair of islands.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

public static partial class GlbDisconnectedParts
{
    struct P3 : IEquatable<P3>
    {
        public double X, Y, Z;
        public bool Equals(P3 o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object o) => o is P3 p && Equals(p);
        public override int GetHashCode() => unchecked(X.GetHashCode() * 31 + Y.GetHashCode() * 17 + Z.GetHashCode());
    }

    /// The island count of a node's mesh, explained: the weld cell the count used, the float32 ulp at the mesh's
    /// coordinate magnitude, and for every pair of islands (up to maxPairs) their closest approach, read against both —
    /// a pair closer than a few ulps is one surface the cell was too fine to join. Never throws; a problem is the text.
    internal static string ExplainIslands(byte[] source, string nodeName, int maxPairs = 12)
    {
        try
        {
            Document document = Parse(source);
            JObject root = document.Root;
            JArray nodes = root["nodes"] as JArray ?? new JArray();
            JArray meshes = root["meshes"] as JArray ?? new JArray();
            JArray buffers = root["buffers"] as JArray;
            byte[] sourceBin = document.Chunks[document.BinIndex].Data;
            int declared = buffers?[0]?.Value<int?>("byteLength") ?? sourceBin.Length;
            var reader = new Accessors(root, Prefix(sourceBin, Math.Min(declared, sourceBin.Length)));
            JObject node = nodes.OfType<JObject>().FirstOrDefault(n => (string)n["name"] == nodeName);
            if (node == null || node["mesh"] == null) return nodeName + ": no mesh node of that name";
            int meshIndex = node.Value<int>("mesh");
            var mesh = meshes[meshIndex] as JObject;
            MeshPlan plan = AnalyzeMesh(meshIndex, mesh, reader, keepSingle: true, mergeFraction: 0);
            if (plan == null) return nodeName + ": no triangles";
            var primitives = mesh["primitives"] as JArray;
            var islands = new List<List<P3>>();
            double[] min = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            double[] max = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            double maxAbs = 0;
            foreach (Component c in plan.Components)
            {
                var seen = new HashSet<P3>(); var pts = new List<P3>();
                foreach (var kv in c.Indices)
                {
                    int acc = ((JObject)primitives[kv.Key])["attributes"].Value<int>("POSITION");
                    foreach (uint idx in kv.Value)
                    {
                        Vec3 v = reader.Position(acc, idx);
                        var p = new P3 { X = v.X, Y = v.Y, Z = v.Z };
                        if (!seen.Add(p)) continue;
                        pts.Add(p); UpdateBounds(min, max, v);
                        maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))));
                    }
                }
                islands.Add(pts);
            }
            double diagonal = Math.Sqrt(Math.Pow(max[0] - min[0], 2) + Math.Pow(max[1] - min[1], 2) + Math.Pow(max[2] - min[2], 2));
            double cell = Math.Max(diagonal * 1e-7, 1e-9);                                     // AnalyzeMesh's weld cell
            double ulp = maxAbs > 0 ? Math.Pow(2, Math.Floor(Math.Log(maxAbs, 2)) - 23) : 0;   // float32 at that magnitude
            var sb = new System.Text.StringBuilder();
            sb.Append(FormattableString.Invariant($"{nodeName}: islands={islands.Count} tris={plan.TriangleCount} diagonal={diagonal:G6} weld-cell={cell:G3} max|coord|={maxAbs:G6} float32-ulp={ulp:G3}"));
            if (ulp > cell) sb.Append(FormattableString.Invariant($" — the cell is FINER than the ulp ({ulp / cell:0.#}x): one-ulp noise reads as a gap"));
            int pairs = 0;
            for (int i = 0; i < islands.Count && pairs < maxPairs; i++)
                for (int j = i + 1; j < islands.Count && pairs < maxPairs; j++, pairs++)
                {
                    double d = Closest(islands[i], islands[j]);
                    string read = d <= cell ? "within the cell (grid rounding split it)" : ulp > 0 && d <= 4 * ulp ? "float noise - one surface" : "a real gap";
                    sb.Append(FormattableString.Invariant($"\n    islands {i}+{j} ({islands[i].Count}+{islands[j].Count} verts): closest {d:G3} = {(ulp > 0 ? d / ulp : 0):0.#} ulp, {d / cell:0.#} cells — {read}"));
                }
            if (pairs < islands.Count * (islands.Count - 1) / 2) sb.Append(FormattableString.Invariant($"\n    … {islands.Count * (islands.Count - 1) / 2 - pairs} more pair(s)"));
            return sb.ToString();
        }
        catch (Exception ex) { return nodeName + ": explain failed (" + ex.GetType().Name + ": " + ex.Message + ")"; }
    }

    // the closest approach between two point sets; each side sampled down to 3,000 points when larger (a bound on cost,
    // not on truth: the sample keeps the first points in index order, and the question is noise-sized distances)
    static double Closest(List<P3> a, List<P3> b)
    {
        const int cap = 3000;
        var sa = a.Count > cap ? a.Where((p, k) => k % Math.Max(1, a.Count / cap) == 0).ToList() : a;
        var sb = b.Count > cap ? b.Where((p, k) => k % Math.Max(1, b.Count / cap) == 0).ToList() : b;
        double best = double.PositiveInfinity;
        foreach (P3 p in sa)
            foreach (P3 q in sb)
            {
                double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z, d = dx * dx + dy * dy + dz * dz;
                if (d < best) best = d;
            }
        return Math.Sqrt(best);
    }
}
