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

    /// <summary>One world matrix per node: parent * local, roots first, at the file's static transforms.</summary>
    public static double[][] WorldMatrices(HafModel m) => WorldMatrices(m, null);

    /// <summary>
    /// One world matrix per node at a POSE: <paramref name="local"/> returns a node's local matrix for this pose, or
    /// null for the file's static transform (see <see cref="PoseAt"/>). A node the scene does not reach still gets one.
    /// </summary>
    public static double[][] WorldMatrices(HafModel m, Func<int, double[]> local)
    {
        var loc = new double[m.Nodes.Count][]; var world = new double[m.Nodes.Count][];
        for (int i = 0; i < m.Nodes.Count; i++) loc[i] = local?.Invoke(i) ?? (m.Nodes[i].HasMatrix ? m.Nodes[i].Matrix : Trs(m.Nodes[i]));
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
        foreach (int n in order) world[n] = m.Nodes[n].Parent < 0 ? loc[n] : Mul(world[m.Nodes[n].Parent], loc[n]);
        for (int i = 0; i < world.Length; i++) if (world[i] == null) world[i] = loc[i];   // unreachable through a cycle the reader would have refused; belt and braces
        return world;
    }

    /// <summary>
    /// The pose an animation puts the nodes in at <paramref name="time"/> (seconds): each channel's sampler evaluated
    /// there (STEP holds the key before; LINEAR interpolates - a rotation by normalized lerp, exact at a key;
    /// CUBICSPLINE takes the key's value) over the node's static transform. The reference pose the parity drill and the
    /// preview use is animation 0 at time 0 - the clip start a viewer shows, and what Blender shows with that clip
    /// active at frame 0 (its untouched import blends every clip through the NLA, which no file defines).
    /// Returns null when the model has no such animation (the static transforms are the pose then).
    /// </summary>
    /// <summary>As <see cref="PoseAt"/>, but the animated channels' VALUES: {translation, rotation, scale}, each null when
    /// the node has no such channel; null for a node the animation does not touch. Blender's float32 matrix composition
    /// (VehicleProbe.BlenderWorld.cs) starts from these, as the importer's objects do.</summary>
    public static Func<int, double[][]> PoseTrsAt(HafModel m, int animationIndex, double time)
    {
        if (animationIndex < 0 || animationIndex >= m.Animations.Count) return null;
        var anim = m.Animations[animationIndex];
        var t = new Dictionary<int, double[]>(); var r = new Dictionary<int, double[]>(); var sc = new Dictionary<int, double[]>();
        foreach (var ch in anim.Channels)
        {
            if (ch.Node < 0 || ch.Sampler < 0 || ch.Sampler >= anim.Samplers.Count) continue;
            var value = Sample(anim.Samplers[ch.Sampler], time);
            if (value == null) continue;
            if (ch.Path == "translation" && value.Length == 3) t[ch.Node] = value;
            else if (ch.Path == "rotation" && value.Length == 4) r[ch.Node] = value;
            else if (ch.Path == "scale" && value.Length == 3) sc[ch.Node] = value;
        }
        return i =>
        {
            if (!t.ContainsKey(i) && !r.ContainsKey(i) && !sc.ContainsKey(i)) return null;
            return new[] { t.TryGetValue(i, out var tv) ? tv : null, r.TryGetValue(i, out var rv) ? rv : null, sc.TryGetValue(i, out var sv) ? sv : null };
        };
    }

    public static Func<int, double[]> PoseAt(HafModel m, int animationIndex, double time)
    {
        if (animationIndex < 0 || animationIndex >= m.Animations.Count) return null;
        var anim = m.Animations[animationIndex];
        var t = new Dictionary<int, double[]>(); var r = new Dictionary<int, double[]>(); var sc = new Dictionary<int, double[]>();
        foreach (var ch in anim.Channels)
        {
            if (ch.Node < 0 || ch.Sampler < 0 || ch.Sampler >= anim.Samplers.Count) continue;
            var value = Sample(anim.Samplers[ch.Sampler], time);
            if (value == null) continue;
            if (ch.Path == "translation" && value.Length == 3) t[ch.Node] = value;
            else if (ch.Path == "rotation" && value.Length == 4) r[ch.Node] = value;
            else if (ch.Path == "scale" && value.Length == 3) sc[ch.Node] = value;
        }
        return i =>
        {
            if (!t.ContainsKey(i) && !r.ContainsKey(i) && !sc.ContainsKey(i)) return null;
            var n = m.Nodes[i];
            return Trs(t.TryGetValue(i, out var tv) ? tv : n.HasMatrix ? new[] { n.Matrix[12], n.Matrix[13], n.Matrix[14] } : n.Translation,
                       r.TryGetValue(i, out var rv) ? rv : n.Rotation,
                       sc.TryGetValue(i, out var sv) ? sv : n.Scale);
        };
    }

    /// <summary>
    /// A sampler's value at <paramref name="time"/>, per its interpolation (glTF 3.7.3): STEP holds the key before;
    /// LINEAR interpolates, a rotation by SLERP (the short way round; the normalized lerp when the two are nearly
    /// equal); CUBICSPLINE is the cubic Hermite over the key's value and the stored tangents (in-tangent, value,
    /// out-tangent per key, tangents scaled by the interval), a rotation normalized afterwards. Before the first key
    /// the first value holds, after the last the last. Null for an empty sampler.
    /// </summary>
    public static double[] Sample(HafSampler s, double time)
    {
        int keys = s.KeyCount, c = s.Components;
        if (keys == 0 || c == 0) return null;
        bool cubic = s.Interpolation == "CUBICSPLINE";
        int per = cubic ? 3 : 1;
        int valueAt(int key) => (key * per + (cubic ? 1 : 0)) * c;
        int inTangentAt(int key) => key * per * c;
        int outTangentAt(int key) => (key * per + 2) * c;
        double[] take(int at) { var r = new double[c]; for (int i = 0; i < c; i++) r[i] = s.Values[at + i]; return r; }
        int k = 0;
        while (k + 1 < keys && s.Times[k + 1] <= time) k++;
        var a = take(valueAt(k));
        if (s.Interpolation == "STEP" || k + 1 >= keys || time <= s.Times[k]) return a;
        double t0 = s.Times[k], t1 = s.Times[k + 1];
        if (time >= t1) return take(valueAt(k + 1));
        var bv = take(valueAt(k + 1));
        double f = (time - t0) / (t1 - t0);
        var outp = new double[c];
        if (cubic)
        {
            double td = t1 - t0, f2 = f * f, f3 = f2 * f;
            double h00 = 2 * f3 - 3 * f2 + 1, h10 = f3 - 2 * f2 + f, h01 = -2 * f3 + 3 * f2, h11 = f3 - f2;
            var m0 = take(outTangentAt(k)); var m1 = take(inTangentAt(k + 1));
            for (int i = 0; i < c; i++) outp[i] = h00 * a[i] + h10 * td * m0[i] + h01 * bv[i] + h11 * td * m1[i];
            if (c == 4) Normalize4(outp);
            return outp;
        }
        if (c == 4) return Slerp(a, bv, f);
        for (int i = 0; i < c; i++) outp[i] = a[i] + (bv[i] - a[i]) * f;
        return outp;
    }

    static void Normalize4(double[] q)
    {
        double len = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
        if (len > 1e-12) for (int i = 0; i < 4; i++) q[i] /= len;
    }

    /// <summary>Spherical interpolation between two quaternions (x, y, z, w), the short way round.</summary>
    public static double[] Slerp(double[] a, double[] b, double f)
    {
        double dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
        var bb = (double[])b.Clone();
        if (dot < 0) { dot = -dot; for (int i = 0; i < 4; i++) bb[i] = -bb[i]; }
        var outp = new double[4];
        if (dot > 0.9995)   // nearly the same rotation: the normalized lerp is exact enough and has no division by a vanishing sine
        {
            for (int i = 0; i < 4; i++) outp[i] = a[i] + (bb[i] - a[i]) * f;
            Normalize4(outp);
            return outp;
        }
        double theta = Math.Acos(Math.Max(-1.0, Math.Min(1.0, dot))), sin = Math.Sin(theta);
        double wa = Math.Sin((1 - f) * theta) / sin, wb = Math.Sin(f * theta) / sin;
        for (int i = 0; i < 4; i++) outp[i] = wa * a[i] + wb * bb[i];
        return outp;
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

    /// <summary>The general 4x4 inverse (column-major, cofactors); null when singular (a zero scale).</summary>
    public static double[] Invert(double[] m)
    {
        var inv = new double[16];
        inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
        inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
        inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
        inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
        inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
        inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
        inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
        inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
        inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
        inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
        inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
        inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
        inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
        inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
        inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
        inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];
        double det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
        if (det == 0) return null;
        for (int i = 0; i < 16; i++) inv[i] /= det;
        return inv;
    }

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
    /// The matrix each joint applies in the linear blend at the pose <paramref name="world"/> describes (glTF:
    /// jointMatrixⱼ = jointWorldⱼ · IBMⱼ; a vertex is Σ wᵢ · jointMatrixⱼᵢ · v). This is what a viewer draws and what
    /// Blender's evaluated import shows (measured 2026-10-01 on scp-682: Blender's untouched import is the blend at its
    /// pose, area 384.6; the undeformed mesh is 394.6 and only appears once the pose is reset).
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
    /// A primitive's positions in world space at the pose <paramref name="world"/> describes: through its node's world
    /// matrix, or - when it is skinned - through the weighted blend of its joints over BOTH influence sets (the spec's
    /// per-vertex transform; the skinned node's own transform is ignored). One vertex = 3 doubles, glTF frame.
    /// </summary>
    public static double[] WorldPositions(HafModel m, int nodeIndex, HafPrimitive p, double[][] world)
    {
        var outp = new double[p.VertexCount * 3];
        var blend = BlendMatrices(m, nodeIndex, p, world);
        for (int v = 0; v < p.VertexCount; v++)
        {
            var mv = blend != null ? blend[v] : world[nodeIndex];
            var q = Apply(mv, p.Positions[v * 3], p.Positions[v * 3 + 1], p.Positions[v * 3 + 2], 1.0);
            outp[v * 3] = q[0]; outp[v * 3 + 1] = q[1]; outp[v * 3 + 2] = q[2];
        }
        return outp;
    }

    /// <summary>The primitive's normals in world space at the same pose (each through the inverse transpose of the matrix
    /// its vertex went through - the blended one for a skinned vertex); null when the primitive has no normals.</summary>
    public static double[] WorldNormals(HafModel m, int nodeIndex, HafPrimitive p, double[][] world)
    {
        if (p.Normals == null) return null;
        var outp = new double[p.VertexCount * 3];
        var blend = BlendMatrices(m, nodeIndex, p, world);
        for (int v = 0; v < p.VertexCount; v++)
        {
            var n = ApplyNormal(blend != null ? blend[v] : world[nodeIndex], p.Normals[v * 3], p.Normals[v * 3 + 1], p.Normals[v * 3 + 2]);
            outp[v * 3] = n[0]; outp[v * 3 + 1] = n[1]; outp[v * 3 + 2] = n[2];
        }
        return outp;
    }

    /// <summary>Per vertex, the weighted sum of its joints' matrices (both influence sets, weights normalized when they do
    /// not sum to 1; an unweighted vertex follows its first JOINTS_0 influence, as Blender recovers it); null for an unskinned primitive.</summary>
    public static double[][] BlendMatrices(HafModel m, int nodeIndex, HafPrimitive p, double[][] world)
    {
        int skin = m.Nodes[nodeIndex].Skin;
        if (!p.Skinned || skin < 0 || skin >= m.Skins.Count) return null;
        var jm = SkinMatrices(m, skin, world);
        var result = new double[p.VertexCount][];
        for (int v = 0; v < p.VertexCount; v++)
        {
            var acc = new double[16]; double wsum = 0;
            for (int set = 0; set < 2; set++)
            {
                var joints = set == 0 ? p.Joints : p.Joints1; var weights = set == 0 ? p.Weights : p.Weights1;
                if (joints == null || weights == null) continue;
                for (int k = 0; k < 4; k++)
                {
                    double w = weights[v * 4 + k];
                    if (w <= 0) continue;
                    int j = joints[v * 4 + k];
                    if (j >= jm.Length) continue;
                    for (int i = 0; i < 16; i++) acc[i] += w * jm[j][i];
                    wsum += w;
                }
            }
            // Blender recovers unweighted vertices by assigning their first JOINTS_0 influence a weight of 1.
            if (wsum <= 0) { int first = p.Joints[v * 4]; result[v] = jm[first < jm.Length ? first : 0]; }
            else { if (Math.Abs(wsum - 1.0) > 1e-6) for (int i = 0; i < 16; i++) acc[i] /= wsum; result[v] = acc; }
        }
        return result;
    }
}
