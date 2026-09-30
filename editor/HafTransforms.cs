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

    /// <summary>
    /// The space a skinned mesh's positions are in, as Blender's import shows them: the topmost joint's world transform
    /// times its inverse bind matrix - the bind-pose placement with the skeleton root trusted to be at its bind pose.
    /// Measured against Blender's EVALUATED import of every skinned registry model (2026-10-01): a drone whose
    /// inverse bind matrices are world-based (the root's 0.01 scale cancels: identity) and two Sketchfab models whose
    /// matrices are armature-relative (the root's 90° rotation stays) both land where Blender puts them; the plain
    /// node-pose blend (Σ wᵢ · jointWorldᵢ · IBMᵢ, what a viewer draws) deforms the two whose node pose is not their
    /// bind pose, and "the skeleton's parent" mis-scales the drone. The mesh stays undeformed: a node-TRS pose that
    /// differs from the bind pose is the animation's business downstream (the rest-fold).
    /// </summary>
    public static double[] SkinSpace(HafModel m, int skinIndex, double[][] world)
    {
        var skin = m.Skins[skinIndex];
        if (skin.Joints.Length == 0) return Identity;
        var joints = new HashSet<int>(skin.Joints);
        int top = skin.Skeleton >= 0 && skin.Skeleton < m.Nodes.Count && joints.Contains(skin.Skeleton) ? skin.Skeleton : skin.Joints[0];
        for (int guard = 0; guard < m.Nodes.Count; guard++)   // climb to the topmost joint
        {
            int parent = m.Nodes[top].Parent;
            if (parent < 0 || !joints.Contains(parent)) break;
            top = parent;
        }
        int j = Array.IndexOf(skin.Joints, top);
        double[] ibm = Identity;
        if (skin.InverseBindMatrices != null && j >= 0) { ibm = new double[16]; Array.Copy(skin.InverseBindMatrices, j * 16, ibm, 0, 16); }
        return Mul(world[top], ibm);
    }

    /// <summary>
    /// The matrix each joint would apply in the linear blend at the NODE-TRS pose (glTF: Σ wᵢ · jointWorldᵢ · IBMᵢ) -
    /// what a viewer draws; NOT what Blender's import shows when that pose differs from the bind pose (see SkinSpace).
    /// Kept for a consumer that needs the posed mesh.
    /// </summary>
    public static double[][] SkinMatrices(HafModel m, int skinIndex, double[][] world)
    {
        var skin = m.Skins[skinIndex];
        var r = new double[skin.Joints.Length][];
        for (int j = 0; j < skin.Joints.Length; j++)
        {
            double[] ibm = Identity;
            if (skin.InverseBindMatrices != null) { ibm = new double[16]; Array.Copy(skin.InverseBindMatrices, j * 16, ibm, 0, 16); }
            r[j] = Mul(world[skin.Joints[j]], ibm);
        }
        return r;
    }

    /// <summary>
    /// A primitive's positions in world space, as Blender shows them: through its node's world matrix, or - when it
    /// is skinned - undeformed in its skin's space (<see cref="SkinSpace"/>). One vertex = 3 doubles, glTF frame.
    /// </summary>
    public static double[] WorldPositions(HafModel m, int nodeIndex, HafPrimitive p, double[][] world)
    {
        int skin = m.Nodes[nodeIndex].Skin;
        var wm = p.Skinned && skin >= 0 && skin < m.Skins.Count ? SkinSpace(m, skin, world) : world[nodeIndex];
        var outp = new double[p.VertexCount * 3];
        for (int v = 0; v < p.VertexCount; v++)
        {
            var q = Apply(wm, p.Positions[v * 3], p.Positions[v * 3 + 1], p.Positions[v * 3 + 2], 1.0);
            outp[v * 3] = q[0]; outp[v * 3 + 1] = q[1]; outp[v * 3 + 2] = q[2];
        }
        return outp;
    }
}
