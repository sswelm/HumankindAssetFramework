// HafUnityFrame.cs - glTF's frame (right-handed, Y up) into the frame the editor's previews draw in (step 4 of replacing
// Blender, 2026-10-03): X mirrored - Unity (-x, y, z) of glTF (x, y, z) - which is what Unity's FBX importer made of Blender's
// export of the same file (Blender (X, Y, Z) is Unity (-X, Z, -Y), measured for the Vehicle Lab's preview), and what
// ModelPreview's Unmirror option draws. Every transform is conjugated by that mirror, so a hierarchy, its skins' inverse bind
// matrices and its animations keep their products: a vertex skinned in Unity by these matrices lands where the reader's
// HafTransforms puts it, mirrored (HafUnityFrameTests holds this; the headless row holds Unity's own skinning to it).
// Pure math over the reader's doubles, no UnityEngine: the tests run it without Unity.
using System;

public static class HafUnityFrame
{
    /// <summary>A point or direction: (-x, y, z).</summary>
    public static double[] Point(double x, double y, double z) => new[] { -x, y, z };

    /// <summary>A quaternion (x, y, z, w) under the X mirror: the axis mirrored and the angle reversed - (x, -y, -z, w).</summary>
    public static double[] Rotation(double[] q) => new[] { q[0], -q[1], -q[2], q[3] };

    /// <summary>A column-major 4x4 under the mirror, Mx M Mx: row 0 and column 0 negated, their crossing twice (unchanged).</summary>
    public static double[] Matrix(double[] m)
    {
        var r = (double[])m.Clone();
        for (int c = 0; c < 4; c++) for (int w = 0; w < 4; w++) if ((c == 0) != (w == 0)) r[c * 4 + w] = -r[c * 4 + w];
        return r;
    }

    /// <summary>A node's local translation, rotation and scale in the mirrored frame: a TRS node's as given, a matrix node's
    /// by decomposition (the columns' lengths are the scale, negated together when the matrix mirrors, the normalized columns
    /// the rotation - shear dropped, as Blender's importer drops it).</summary>
    public static void LocalTrs(HafNode n, out double[] t, out double[] q, out double[] s)
    {
        if (!n.HasMatrix) { t = Point(n.Translation[0], n.Translation[1], n.Translation[2]); q = Rotation(Unit(n.Rotation)); s = (double[])n.Scale.Clone(); return; }
        Decompose(Matrix(n.Matrix), out t, out q, out s);
    }

    /// <summary>A node's local matrix in glTF's frame AS THE RIG HOLDS IT: a TRS node's with its rotation normalized (a Transform
    /// holds a unit quaternion; Blender normalizes too), a matrix node's decomposed and recomposed (the shear a Transform cannot
    /// hold is gone - the rah66's root is 0.13 degrees off square, and its parts sit 0.48 units from where the matrix as given
    /// puts them). The reader's pose the rig is judged against is built from these, not from the matrices as given.</summary>
    public static double[] ReaderLocal(HafNode n)
    {
        if (!n.HasMatrix) return HafTransforms.Trs(n.Translation, Unit(n.Rotation), n.Scale);
        Decompose(n.Matrix, out var t, out var q, out var s);
        return HafTransforms.Trs(t, q, s);
    }

    static double[] Unit(double[] q)
    {
        double l = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
        return l == 0 || Math.Abs(l - 1) < 1e-15 ? (double[])q.Clone() : new[] { q[0] / l, q[1] / l, q[2] / l, q[3] / l };
    }

    /// <summary>T, R (x, y, z, w), S of a column-major 4x4 that is a product of them.</summary>
    public static void Decompose(double[] m, out double[] t, out double[] q, out double[] s)
    {
        t = new[] { m[12], m[13], m[14] };
        s = new double[3]; var c = new double[3][];
        for (int i = 0; i < 3; i++)
        {
            double x = m[i * 4], y = m[i * 4 + 1], z = m[i * 4 + 2], len = Math.Sqrt(x * x + y * y + z * z);
            s[i] = len;
            c[i] = len > 1e-30 ? new[] { x / len, y / len, z / len } : new double[] { i == 0 ? 1 : 0, i == 1 ? 1 : 0, i == 2 ? 1 : 0 };
        }
        if (HafTransforms.Determinant3(m) < 0) { for (int i = 0; i < 3; i++) { s[i] = -s[i]; for (int k = 0; k < 3; k++) c[i][k] = -c[i][k]; } }
        // the columns are the rotated axes: R[row][col] = c[col][row]
        double m00 = c[0][0], m01 = c[1][0], m02 = c[2][0], m10 = c[0][1], m11 = c[1][1], m12 = c[2][1], m20 = c[0][2], m21 = c[1][2], m22 = c[2][2];
        double tr = m00 + m11 + m22; double qx, qy, qz, qw;
        if (tr > 0) { double S = Math.Sqrt(tr + 1.0) * 2; qw = 0.25 * S; qx = (m21 - m12) / S; qy = (m02 - m20) / S; qz = (m10 - m01) / S; }
        else if (m00 > m11 && m00 > m22) { double S = Math.Sqrt(1.0 + m00 - m11 - m22) * 2; qw = (m21 - m12) / S; qx = 0.25 * S; qy = (m01 + m10) / S; qz = (m02 + m20) / S; }
        else if (m11 > m22) { double S = Math.Sqrt(1.0 + m11 - m00 - m22) * 2; qw = (m02 - m20) / S; qx = (m01 + m10) / S; qy = 0.25 * S; qz = (m12 + m21) / S; }
        else { double S = Math.Sqrt(1.0 + m22 - m00 - m11) * 2; qw = (m10 - m01) / S; qx = (m02 + m20) / S; qy = (m12 + m21) / S; qz = 0.25 * S; }
        q = new[] { qx, qy, qz, qw };
    }

    /// <summary>What Unity's skinning computes for one vertex from what the rig hands it: sum over the influences of weight x
    /// (the bone's world matrix x its bindpose) x the vertex, every matrix in the mirrored frame. The headless row has Unity do
    /// this; the unit test does it here and sets it beside the reader's WorldPositions, mirrored.</summary>
    public static double[] Skin(double[][] boneWorld, double[][] bindposes, int[] joints, double[] weights, double[] v)
    {
        double wsum = 0; foreach (var w in weights) wsum += w;
        var r = new double[3];
        for (int k = 0; k < joints.Length; k++)
        {
            double w = wsum > 0 ? weights[k] / wsum : (k == 0 ? 1 : 0);
            if (w == 0) continue;
            var p = HafTransforms.Apply(HafTransforms.Mul(boneWorld[joints[k]], bindposes[joints[k]]), v[0], v[1], v[2], 1.0);
            for (int i = 0; i < 3; i++) r[i] += w * p[i];
        }
        return r;
    }
}
