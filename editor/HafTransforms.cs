using System;
using System.Collections.Generic;

// THE NODE HIERARCHY'S TRANSFORMS (2026-09-30, step 1 of replacing Blender): glTF's column-major 4x4 matrices, a node's
// local transform as T * R * S (or the matrix the file gave), the world transform as parent * local down the hierarchy,
// and the two things a consumer applies them to - positions (the full matrix) and normals (the inverse transpose of the
// 3x3, right under non-uniform scale and mirrors). Pure C#; HafTransformsTests locks the conventions against known
// rotations, so a preview or a probe that looks wrong cannot blame this.
public static class HafTransforms
{
    public static readonly double[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    /// <summary>One world matrix per node: parent * local, roots first. A node the scene does not reach still gets one.</summary>
    public static double[][] WorldMatrices(HafModel m)
    {
        var local = new double[m.Nodes.Count][]; var world = new double[m.Nodes.Count][];
        for (int i = 0; i < m.Nodes.Count; i++) local[i] = m.Nodes[i].HasMatrix ? m.Nodes[i].Matrix : Trs(m.Nodes[i]);
        var order = new List<int>(); var stack = new Stack<int>();
        for (int i = 0; i < m.Nodes.Count; i++) if (m.Nodes[i].Parent < 0) stack.Push(i);
        var seen = new bool[m.Nodes.Count];
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            if (seen[n]) continue;
            seen[n] = true; order.Add(n);
            foreach (var c in m.Nodes[n].Children) stack.Push(c);
        }
        foreach (int n in order) world[n] = m.Nodes[n].Parent < 0 ? local[n] : Mul(world[m.Nodes[n].Parent], local[n]);
        for (int i = 0; i < world.Length; i++) if (world[i] == null) world[i] = local[i];   // unreachable through a cycle the reader would have refused; belt and braces
        return world;
    }

    /// <summary>T * R * S as a column-major 4x4: the rotation's columns scaled, the translation in the last column.</summary>
    public static double[] Trs(HafNode n) => Trs(n.Translation, n.Rotation, n.Scale);

    public static double[] Trs(double[] t, double[] q, double[] s)
    {
        double x = q[0], y = q[1], z = q[2], w = q[3];
        return new[]
        {
            (1 - 2 * (y * y + z * z)) * s[0], (2 * (x * y + z * w)) * s[0], (2 * (x * z - y * w)) * s[0], 0,
            (2 * (x * y - z * w)) * s[1], (1 - 2 * (x * x + z * z)) * s[1], (2 * (y * z + x * w)) * s[1], 0,
            (2 * (x * z + y * w)) * s[2], (2 * (y * z - x * w)) * s[2], (1 - 2 * (x * x + y * y)) * s[2], 0,
            t[0], t[1], t[2], 1,
        };
    }

    /// <summary>a * b, column-major (element [col*4 + row]).</summary>
    public static double[] Mul(double[] a, double[] b)
    {
        var r = new double[16];
        for (int c = 0; c < 4; c++)
            for (int row = 0; row < 4; row++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++) sum += a[k * 4 + row] * b[c * 4 + k];
                r[c * 4 + row] = sum;
            }
        return r;
    }

    /// <summary>The matrix applied to a point (w = 1) or a direction (w = 0).</summary>
    public static double[] Apply(double[] m, double x, double y, double z, double w) =>
        new[] { m[0] * x + m[4] * y + m[8] * z + m[12] * w, m[1] * x + m[5] * y + m[9] * z + m[13] * w, m[2] * x + m[6] * y + m[10] * z + m[14] * w };

    /// <summary>The 3x3's determinant: negative = the node mirrors (a symmetric hull's other side).</summary>
    public static double Determinant3(double[] m)
    {
        double a = m[0], b = m[4], c = m[8], d = m[1], e = m[5], f = m[9], g = m[2], h = m[6], i = m[10];
        return a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
    }

    /// <summary>A normal through the inverse transpose of the 3x3, renormalized (right under non-uniform scale and mirrors).</summary>
    public static double[] ApplyNormal(double[] m, double x, double y, double z)
    {
        double a = m[0], b = m[4], c = m[8], d = m[1], e = m[5], f = m[9], g = m[2], h = m[6], i = m[10];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        double[] n;
        if (Math.Abs(det) < 1e-18) n = new[] { x, y, z };
        else
        {
            // inv (row-major, rows of the inverse); n' = inv^T * n = each column of inv dotted with n
            double[] inv = { (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
                             (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
                             (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det };
            n = new[] { inv[0] * x + inv[3] * y + inv[6] * z, inv[1] * x + inv[4] * y + inv[7] * z, inv[2] * x + inv[5] * y + inv[8] * z };
        }
        double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
        return len > 1e-12 ? new[] { n[0] / len, n[1] / len, n[2] / len } : new[] { 0.0, 1.0, 0.0 };
    }
}
