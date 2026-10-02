// VehicleProbe.InsideOut.cs - the PART row's inside-out verdict (field 8) as vehicle_rig.py's probe computes it, step 3c of
// replacing Blender. Per part, the number of FACE islands (faces joined by a shared edge, bmesh's e.link_faces) whose faces on
// average point INTO the hull: for each face, the dot of its world normal with the radial from the hull's length axis to
// the face's centre; an island averaging below -0.25 counts. The hull axis is Blender's X axis through the mid-Y and the
// lower-quartile Z of every part's vertices, every max(1, n / 2000)-th in Blender's order, through matrix_world.
//
// The arithmetic is Blender's, step for step, because a sliver or collinear triangle is decided by float32 rounding there
// (its normal is rounding noise, normalized): bmesh's normal_tri_v3 from the LOCAL corners ((v1 - v2) x (v2 - v3), zero
// below 1e-35) and calc_center_median in float32, mathutils' Matrix @ Vector and Vector.dot (float32 products summed in
// double) and Vector.normalized (1.0f / float(sqrt)), all in Blender's Z-up frame. The face normal is the local one
// through matrix_world's 3x3 - NOT the cross product of the world corners: a mirrored node (negative scale) keeps bmesh's
// sign (negativescaletest's Shiny1 read 1 against Blender's 0 before this).
//
// Said, not solved: a SKINNED part's vertex positions are what Blender's importer skinned into the bind pose in numpy
// float32 (joint matrices from the inverse bind matrices through Eigen's inverse and a decompose, then a float32
// multiply-add chain); the double arithmetic here lands within one float32 ulp of them, and on a zero-area triangle that
// ulp decides the normal. 3 parts of 11,516 (all skinned, all on collinear triangles) differ by 1-2 islands for it -
// docs/Review-Backlog.md.
using System;
using System.Collections.Generic;
using System.Linq;

public static partial class VehicleProbe
{
    /// <summary>Blender's matrix_world of a part as mathutils holds it: float32, row-major (item[row, col]), Blender's Z-up
    /// frame. Built from the glTF-frame matrix through the axis change C(x, y, z) = (x, -z, y): column j = C M Cinv e_j.</summary>
    static float[] BlenderMatrix(double[] M)
    {
        var mb = new float[16];
        for (int col = 0; col < 4; col++)
        {
            double bx = col == 0 ? 1 : 0, by = col == 1 ? 1 : 0, bz = col == 2 ? 1 : 0, w = col == 3 ? 1 : 0;
            var g = HafTransforms.Apply(M, bx, bz, -by, w);   // Cinv(b) = (bx, bz, -by)
            mb[0 * 4 + col] = (float)g[0]; mb[1 * 4 + col] = (float)(-g[2]); mb[2 * 4 + col] = (float)g[1]; mb[3 * 4 + col] = col == 3 ? 1f : 0f;
        }
        return mb;
    }

    /// <summary>mathutils Matrix @ Vector (column_vector_multiplication): one row - float32 products summed in double,
    /// stored as float32; a 3-vector through a 4x4 gets w = 1, a 3x3 (to_3x3) takes three columns.</summary>
    static float MatRow(float[] mb, int row, int cols, float x, float y, float z, float w)
    {
        double d = 0.0;
        d += (double)(float)(mb[row * 4 + 0] * x);
        d += (double)(float)(mb[row * 4 + 1] * y);
        d += (double)(float)(mb[row * 4 + 2] * z);
        if (cols == 4) d += (double)(float)(mb[row * 4 + 3] * w);
        return (float)d;
    }

    /// <summary>mathutils dot_vn_vn: float32 products summed in double from the LAST component down.</summary>
    static double DotVn(float ax, float ay, float az, float bx, float by, float bz)
    {
        double d = 0.0;
        d += (double)(float)(az * bz); d += (double)(float)(ay * by); d += (double)(float)(ax * bx);
        return d;
    }

    /// <summary>mathutils len_squared_vn: the squares in double, summed from the last component down.</summary>
    static double LenSquaredVn(float x, float y, float z)
    {
        double d = 0.0;
        d += (double)z * z; d += (double)y * y; d += (double)x * x;
        return d;
    }

    /// <summary>mathutils Vector.normalized (normalize_vn): length squared in double, 1.0f / float(sqrt) as the float32 scale;
    /// zero below 1e-35.</summary>
    static void NormalizeVn(ref float x, ref float y, ref float z)
    {
        double d = LenSquaredVn(x, y, z);
        if (d > 1.0e-35) { float s = (float)(1.0f / (float)Math.Sqrt(d)); x = (float)(x * s); y = (float)(y * s); z = (float)(z * s); }
        else { x = 0f; y = 0f; z = 0f; }
    }

    /// <summary>Sets every part's Flip: the islands the inside-out fix would reverse. objWorlds[i] is part i's matrix_world
    /// in the glTF frame (the armature's for a skinned part, whose Local is then the bind pose).</summary>
    static void InsideOut(IList<Part> parts, IList<PartMesh> meshes, IList<double[]> objWorlds)
    {
        var mats = objWorlds.Select(BlenderMatrix).ToList();
        // the hull axis: every part, every max(1, n / 2000)-th vertex in Blender's order, through matrix_world
        var ys = new List<float>(); var zs = new List<float>();
        for (int pi = 0; pi < meshes.Count; pi++)
        {
            var pm = meshes[pi]; var mb = mats[pi];
            int step = Math.Max(1, pm.Count / 2000);
            for (int i = 0; i < pm.Count; i += step)
            {
                float lx = pm.Local[i * 3], ly = -pm.Local[i * 3 + 2], lz = pm.Local[i * 3 + 1];   // Blender's frame
                ys.Add(MatRow(mb, 1, 4, lx, ly, lz, 1f)); zs.Add(MatRow(mb, 2, 4, lx, ly, lz, 1f));
            }
        }
        if (ys.Count == 0) return;
        double cy = 0.5 * ((double)ys.Min() + (double)ys.Max());
        zs.Sort(); double cz = zs[zs.Count / 4];
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var pm = meshes[pi]; var mb = mats[pi];
            int nf = pm.Tris.Length / 3;
            if (nf == 0) { parts[pi].Flip = 0; continue; }
            // faces sharing an edge are one island: union-find over the faces, the edges keyed by their vertex pair and sorted
            var keys = new long[nf * 3]; var owner = new int[nf * 3];
            for (int f = 0; f < nf; f++)
                for (int k = 0; k < 3; k++)
                {
                    int a = pm.Tris[f * 3 + k], b = pm.Tris[f * 3 + (k + 1) % 3];
                    keys[f * 3 + k] = (long)Math.Min(a, b) << 32 | (uint)Math.Max(a, b); owner[f * 3 + k] = f;
                }
            Array.Sort(keys, owner);
            var parent = new int[nf]; for (int i = 0; i < nf; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int i = 1; i < keys.Length; i++)
                if (keys[i] == keys[i - 1]) { int ra = Find(owner[i]), rb = Find(owner[i - 1]); if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb); }
            var dsum = new Dictionary<int, double>(); var dn = new Dictionary<int, int>();
            for (int f = 0; f < nf; f++)
            {
                int root = Find(f);
                if (!dn.ContainsKey(root)) { dsum[root] = 0; dn[root] = 0; }
                int ia = pm.Tris[f * 3], ib = pm.Tris[f * 3 + 1], ic = pm.Tris[f * 3 + 2];
                // the corners in Blender's frame, float32 as the mesh holds them
                float ax = pm.Local[ia * 3], ay = -pm.Local[ia * 3 + 2], az = pm.Local[ia * 3 + 1];
                float bx = pm.Local[ib * 3], by = -pm.Local[ib * 3 + 2], bz = pm.Local[ib * 3 + 1];
                float cx = pm.Local[ic * 3], cyy = -pm.Local[ic * 3 + 2], czz = pm.Local[ic * 3 + 1];
                // bmesh normal_tri_v3: n = (v1 - v2) x (v2 - v3), normalize_v3 (zero below 1e-35)
                float n1x = (float)(ax - bx), n1y = (float)(ay - by), n1z = (float)(az - bz);
                float n2x = (float)(bx - cx), n2y = (float)(by - cyy), n2z = (float)(bz - czz);
                float nx = (float)((float)(n1y * n2z) - (float)(n1z * n2y));
                float ny = (float)((float)(n1z * n2x) - (float)(n1x * n2z));
                float nz = (float)((float)(n1x * n2y) - (float)(n1y * n2x));
                float d = (float)((float)((float)(nx * nx) + (float)(ny * ny)) + (float)(nz * nz));
                if (d > 1.0e-35f) { d = (float)Math.Sqrt(d); float s = (float)(1.0f / d); nx = (float)(nx * s); ny = (float)(ny * s); nz = (float)(nz * s); }
                else { nx = 0f; ny = 0f; nz = 0f; }
                // bmesh calc_center_median: the corners summed in loop order, times 1.0f / 3
                float mx = (float)((float)((float)(0f + ax) + bx) + cx), my = (float)((float)((float)(0f + ay) + by) + cyy), mz = (float)((float)((float)(0f + az) + bz) + czz);
                const float third = 1.0f / 3f;
                mx = (float)(mx * third); my = (float)(my * third); mz = (float)(mz * third);
                // the script: ctr2 = mw0 @ centre; rad = (0, ctr2.y - cy, ctr2.z - cz); wn = nm0 @ normal
                float c2y = MatRow(mb, 1, 4, mx, my, mz, 1f), c2z = MatRow(mb, 2, 4, mx, my, mz, 1f);
                float ry = (float)((double)c2y - cy), rz = (float)((double)c2z - cz);
                float wx = MatRow(mb, 0, 3, nx, ny, nz, 0f), wy = MatRow(mb, 1, 3, nx, ny, nz, 0f), wz = MatRow(mb, 2, 3, nx, ny, nz, 0f);
                double radLen = Math.Sqrt(DotVn(0f, ry, rz, 0f, ry, rz)), wnLen = Math.Sqrt(DotVn(wx, wy, wz, wx, wy, wz));
                if (radLen > 1e-6 && wnLen > 1e-9)
                {
                    float rx = 0f; NormalizeVn(ref wx, ref wy, ref wz); NormalizeVn(ref rx, ref ry, ref rz);
                    dsum[root] += DotVn(wx, wy, wz, rx, ry, rz); dn[root]++;
                }
            }
            int reversed = 0;
            foreach (var root in dn.Keys) if (dn[root] > 0 && dsum[root] / dn[root] < -0.25) reversed++;
            parts[pi].Flip = reversed;
        }
    }
}
