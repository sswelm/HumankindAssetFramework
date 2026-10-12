// GlbDisconnectedParts.Explain.cs — why a part reads as the islands it reads (2026-10-12).
//
// The Bake Tests' Workshop row failed on salegs_revenge_split_fused with "23 split child(ren) are not one island": a
// child the split had just cut as ONE island read as two to seven when analysed on its own. A count cannot be
// investigated; this names the measure behind it. AnalyzeMesh welds positions on a grid whose cell is a ten-millionth
// of THE MESH'S OWN diagonal (and never under 1e-9): a child is smaller than the node it came from, so its cell is
// finer, and a part thousands of units from the origin has float32 coordinates whose ulp (the smallest step the
// format can hold) is LARGER than that cell — two vertices that are one surface, one ulp apart after a fuse's float32
// write, land in different cells and read as two islands. This measures the current, axis-specific weld cells and
// float32 ulps, and the nearest weld-scaled approach between each pair of islands.
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

    /// The island count of a node's mesh, explained: the weld cells and float32 ulps for each axis, and for every
    /// pair of islands (up to maxPairs) their nearest weld-scaled approach. Never throws; a problem is the text.
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
                    }
                }
                islands.Add(pts);
            }
            double diagonal = Math.Sqrt(Math.Pow(max[0] - min[0], 2) + Math.Pow(max[1] - min[1], 2) + Math.Pow(max[2] - min[2], 2));
            double[] cell = WeldCells(min, max, diagonal);
            double[] ulp = Enumerable.Range(0, 3).Select(axis => {
                double magnitude = Math.Max(Math.Abs(min[axis]), Math.Abs(max[axis]));
                return magnitude > 0 ? Math.Pow(2, Math.Floor(Math.Log(magnitude, 2)) - 23) : 0;
            }).ToArray();
            var sb = new System.Text.StringBuilder();
            sb.Append(FormattableString.Invariant($"{nodeName}: islands={islands.Count} tris={plan.TriangleCount} diagonal={diagonal:G6} weld-cell=({cell[0]:G3},{cell[1]:G3},{cell[2]:G3}) float32-ulp=({ulp[0]:G3},{ulp[1]:G3},{ulp[2]:G3})"));
            int pairs = 0;
            for (int i = 0; i < islands.Count && pairs < maxPairs; i++)
                for (int j = i + 1; j < islands.Count && pairs < maxPairs; j++, pairs++)
                {
                    var gap = Closest(islands[i], islands[j], cell);
                    string read = gap.Item2 <= 1 ? "within weld tolerance (unexpected separate islands)" : "above sampled weld tolerance";
                    sb.Append(FormattableString.Invariant($"\n    islands {i}+{j} ({islands[i].Count}+{islands[j].Count} verts): nearest weld-scaled approach {gap.Item1:G3}, {gap.Item2:0.#} cells — {read}"));
                }
            if (pairs < islands.Count * (islands.Count - 1) / 2) sb.Append(FormattableString.Invariant($"\n    … {islands.Count * (islands.Count - 1) / 2 - pairs} more pair(s)"));
            return sb.ToString();
        }
        catch (Exception ex) { return nodeName + ": explain failed (" + ex.GetType().Name + ": " + ex.Message + ")"; }
    }

    // The nearest approach in units of the axis-specific weld cells. Sample each side to at most 3,000 points to
    // bound the diagnostic cost; this is a hint for investigation, not an exact proof of separation.
    static Tuple<double, double> Closest(List<P3> a, List<P3> b, double[] cell)
    {
        const int cap = 3000;
        var sa = a.Count > cap ? a.Where((p, k) => k % (int)Math.Ceiling((double)a.Count / cap) == 0).ToList() : a;
        var sb = b.Count > cap ? b.Where((p, k) => k % (int)Math.Ceiling((double)b.Count / cap) == 0).ToList() : b;
        double bestScaled = double.PositiveInfinity, bestDistance = double.PositiveInfinity;
        foreach (P3 p in sa)
            foreach (P3 q in sb)
            {
                double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                double scaled = dx * dx / (cell[0] * cell[0]) + dy * dy / (cell[1] * cell[1]) + dz * dz / (cell[2] * cell[2]);
                if (scaled < bestScaled) { bestScaled = scaled; bestDistance = dx * dx + dy * dy + dz * dz; }
            }
        return Tuple.Create(Math.Sqrt(bestDistance), Math.Sqrt(bestScaled));
    }
}
