// VehicleProbe.Merge.cs - what the Lab's probe does to the scene BEFORE it measures, in Blender's arithmetic (step 3d of
// replacing Blender, 2026-10-03): the SECOND MODEL merged in (vehicle_rig.py's merge2= block: imported into the same scene,
// its parts prefixed "B_", each mesh's target world matrix T2 @ matrix_world baked into its vertices by Mesh.transform and
// the object left at identity, its helpers dropped), the PER-PART PLACEMENTS (parttx=: the part and its direct children
// detached keeping their world matrices, then T = Translation(off) @ Translation(c0) @ Diagonal(scale) @ Translation(-c0)
// onto matrix_world, c0 the part's world box centre), and the ORIENTATION the inside-out verdicts are judged in (proberot=:
// Rx @ Ry @ Rz applied to every part's matrix for that math alone). Every operation is the one Blender runs, read from its
// source (mathutils_Matrix.cc, object.cc, math_matrix_c.cc, math_rotation_c.cc, BLI_math_matrix_types.hh) and held bit for
// bit by the drill's MATRIX and VERTEX rows on fixtures that exercise each (tools/vehicle-probe-drill/probe_jobs.py):
//   * mathutils Matrix @ Matrix: float32 products summed in double, rounded (MatMulMathutils); Matrix.Translation(v),
//     Matrix.Diagonal(v): the floats as given; Matrix.Rotation(angle, 4, axis): the double angle to float, angle_wrap_rad in
//     float (a + pi, mod 2 pi, - pi: NOT the identity for small angles), then cosf/sinf - here the double functions rounded
//     to float, the one place this port can differ from the C runtime by an ulp;
//   * assigning `matrix_world` runs BKE_object_apply_mat4: mat4_to_loc_rot_size (columns normalized with 1/len, negated
//     together when the matrix mirrors), mat3_normalized_to_quat, and the object's matrix is REBUILT from loc, quat and
//     size at the next update (BKE_object_to_mat4) - a round trip that moves the last bit of a rotation (ApplyMat4);
//   * Mesh.transform: math::transform_point, ((x c0 + y c1) + z c2) + loc in float32, no FMA (the build is clang-cl at
//     x86-64-v2 with fp-contract off); the custom-normal shorts are left as they are (transform_custom_normal_attribute acts
//     on a float3 attribute only, and the importer's are shorts), so `vertex.normal` is decoded against the moved geometry;
//   * a negative determinant (a mirrored node in the second file) flips every face: corners 1 and 2 swapped, each corner's
//     shorts travelling with it (mesh_flip_faces);
//   * the arguments as the script parses them: `float()` of the Lab's formatted text, a non-positive scale component made 1
//     with a warning, a malformed placement line ignored with a warning.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public static partial class VehicleProbe
{
    /// <summary>What the Lab hands the probe besides the model: the second model and its placement, the per-part placements,
    /// the orientation the inside-out verdicts are judged in. Built from the SAME text the Lab would hand vehicle_rig.py
    /// (FromArguments), so both paths read the same numbers.</summary>
    public sealed class Input
    {
        public HafModel Model;
        public HafModel Second;                                   // null = no second model
        public string SecondFile = "";                            // for the MERGE note: the file's name
        public string SecondOffsetText = "0,0,0", SecondRotationText = "0,0,0";   // the merge2 fields as given, for the MERGE note
        public double[] SecondOffset = { 0, 0, 0 }, SecondRotation = { 0, 0, 0 }, SecondScale = { 1, 1, 1 };   // Blender's frame, degrees - as `float()` read them
        public List<string> SecondScaleBad = new List<string>();  // scale components that were not positive finite numbers (1 is used, and said)
        public List<Placement> Placements = new List<Placement>();
        public double[] ProbeRotation;                            // degrees about Blender's X, Y, Z, or null
        public List<string> Warnings = new List<string>();        // what the script prints as "VEHICLE WARN: ..." while parsing
        public Action<string> Progress;                           // told each stage as the probe reaches it (the Lab's progress bar), or null

        public sealed class Placement { public string Name; public double[] Offset, Scale; }

        /// <summary>Geometry the in-process probe or its static preview cannot reproduce yet. The Lab keeps the
        /// existing Blender path for these inputs; the parity drill can still exercise the kernel separately.</summary>
        public string InProcessFallbackReason()
        {
            if (Second != null && Second.Animations.Count > 0) return "the second model is animated";
            foreach (var model in new[] { Model, Second }.Where(m => m != null))
            {
                if (model.Meshes.Any(m => m.Primitives.Any(p => p.MorphTargets > 0))) return "the model has morph targets";
                if (model.Skins.Count == 0) continue;
                var names = BlenderNames.Compute(model);
                for (int n = 0; n < model.Nodes.Count; n++)
                {
                    var node = model.Nodes[n];
                    if (node.Mesh < 0) continue;
                    bool skinned = node.Skin >= 0 && model.Meshes[node.Mesh].Primitives.Any(p => p.Skinned);
                    if (skinned)
                    {
                        if (model == Model && (model.Animations.Count > 0 || Placements.Count > 0))
                            return "an animated or placed skinned mesh needs Blender's evaluated pose";
                        continue;
                    }
                    for (int a = n; a >= 0; a = model.Nodes[a].Parent)
                        if (names.IsBone[a]) return "a mesh is parented to a bone";
                }
            }
            return null;
        }

        /// <summary>The merge2 argument's tail, "path|ox,oy,oz|rx,ry,rz|sx,sy,sz", parsed as the script parses it (_merge2_scale
        /// included: one number is a uniform scale; a component that is not a positive finite number becomes 1 and is reported).
        /// False with the script's VEHICLE ERROR text when the text is malformed.</summary>
        public static bool TryParseMerge2(string text, out string path, out double[] offset, out double[] rotation, out double[] scale, out List<string> bad, out string error)
        {
            path = null; offset = rotation = scale = null; bad = new List<string>(); error = null;
            var f = (text ?? "").Split('|');
            if (f.Length != 4) { error = "malformed merge2 argument: merge2=" + text; return false; }
            path = f[0];
            try { offset = f[1].Split(',').Select(PyFloat).ToArray(); rotation = f[2].Split(',').Select(PyFloat).ToArray(); }
            catch (FormatException e) { error = "second model: " + e.Message; return false; }
            var raw = f[3].Split(',').Select(t => t.Trim()).ToList();
            if (raw.Count == 1) raw = new List<string> { raw[0], raw[0], raw[0] };
            if (raw.Count != 3) { error = $"malformed merge2 scale '{f[3]}' (expected one number or three, got {raw.Count})"; return false; }
            scale = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double v;
                if (raw[i].Length == 0) v = 1.0;
                else if (!double.TryParse(raw[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) v = double.NaN;
                if (!(v > 0.0 && v != double.PositiveInfinity)) { bad.Add(raw[i]); v = 1.0; }   // <= 0, inf and nan (nan fails every comparison)
                scale[i] = v;
            }
            return true;
        }

        /// <summary>The second model from the merge2 argument's tail, read with `load` (the Lab reads a .glb/.gltf; a path Blender would
        /// take and this cannot - FBX, OBJ - is the caller's to refuse first). False with the script's VEHICLE ERROR text.</summary>
        public bool SetSecond(string merge2Text, Func<string, HafModel> load, out string error)
        {
            if (!TryParseMerge2(merge2Text, out string path, out var off, out var rot, out var scl, out var bad, out error)) return false;
            if (!System.IO.File.Exists(path)) { error = "second model not found: " + path; return false; }
            Second = load(path); SecondFile = path.Replace('\\', '/').Split('/').Last();
            var f = merge2Text.Split('|'); SecondOffsetText = f[1]; SecondRotationText = f[2];
            SecondOffset = off; SecondRotation = rot; SecondScale = scl; SecondScaleBad = bad;
            return true;
        }

        /// <summary>part_placements: one line per placed part, name|ox,oy,oz|sx,sy,sz, split from the RIGHT (a name may hold the
        /// separator); a line that does not parse is ignored with a warning, a non-positive scale component makes the whole scale 1
        /// with a warning; a name given twice keeps its first place with its last numbers (a Python dict).</summary>
        public void AddPlacementLines(IEnumerable<string> lines)
        {
            foreach (var line0 in lines)
            {
                string line = (line0 ?? "").TrimEnd('\n', '\r');
                if (line.Trim().Length == 0) continue;
                string name; double[] off, scl;
                try
                {
                    int cut2 = line.LastIndexOf('|'); int cut1 = cut2 > 0 ? line.LastIndexOf('|', cut2 - 1) : -1;
                    if (cut1 < 0) throw new FormatException("not a triple");
                    name = line.Substring(0, cut1);
                    off = line.Substring(cut1 + 1, cut2 - cut1 - 1).Split(',').Select(PyFloat).ToArray();
                    scl = line.Substring(cut2 + 1).Split(',').Select(PyFloat).ToArray();
                    if (off.Length != 3 || scl.Length != 3) throw new FormatException("not a triple");
                }
                catch (FormatException) { Warnings.Add("bad placement line ignored: " + PyRepr(line)); continue; }
                if (scl.Any(v => v <= 0.0))
                {
                    Warnings.Add($"placement for '{name}' has a non-positive scale [{string.Join(", ", scl.Select(PyFloatRepr))}] — using 1");
                    scl = new[] { 1.0, 1.0, 1.0 };
                }
                var have = Placements.FirstOrDefault(p => p.Name == name);
                if (have != null) { have.Offset = off; have.Scale = scl; }
                else Placements.Add(new Placement { Name = name, Offset = off, Scale = scl });
            }
        }

        /// <summary>The proberot argument's tail, "x,y,z" degrees: fewer than three numbers are padded with 0, more are cut; text
        /// that is not numbers leaves the verdicts in import orientation with the script's warning.</summary>
        public void SetProbeRotation(string text)
        {
            try { ProbeRotation = text.Split(',').Select(PyFloat).Concat(new[] { 0.0, 0.0, 0.0 }).Take(3).ToArray(); }
            catch (FormatException) { Warnings.Add($"bad proberot argument 'proberot={text}' — verdicts run in import orientation"); ProbeRotation = null; }
            if (ProbeRotation != null && ProbeRotation.All(v => v == 0.0)) ProbeRotation = null;
        }

        /// <summary>Python's float(text): decimal, exponent, inf/nan, surrounding whitespace; anything else raises.</summary>
        internal static double PyFloat(string t)
        {
            string s = (t ?? "").Trim();
            if (s.Length == 0) throw new FormatException($"could not convert string to float: {PyRepr(t)}");
            string low = s.ToLowerInvariant().TrimStart('+', '-');
            if (low == "inf" || low == "infinity") return s.StartsWith("-") ? double.NegativeInfinity : double.PositiveInfinity;
            if (low == "nan") return double.NaN;
            if (s.Contains("_") || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) throw new FormatException($"could not convert string to float: {PyRepr(t)}");
            return v;
        }

        static string PyRepr(string s) => "'" + (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        static string PyFloatRepr(double v) => v == Math.Floor(v) && Math.Abs(v) < 1e16 ? v.ToString("0.0", CultureInfo.InvariantCulture) : v.ToString("R", CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- mathutils constructors (row-major item layout)

    /// <summary>angle_wrap_rad: mod_inline(angle + pi, 2 pi) - pi in float32, mod_inline(a, b) = a - b floorf(a / b).</summary>
    internal static float AngleWrapRad(float angle)
    {
        const float PIf = (float)Math.PI;
        float twoPi = (float)(PIf * 2.0f);
        float a = (float)(angle + PIf);
        float q = (float)Math.Floor((double)(float)(a / twoPi));   // floorf of a float32 quotient
        float mod = (float)(a - (float)(twoPi * q));
        return (float)(mod - PIf);
    }

    /// <summary>Matrix.Rotation(angle, 4, axis) for axis 'X', 'Y' or 'Z': the double angle to float, wrapped, then
    /// axis_angle_to_mat3_single with cosf/sinf (here the double functions rounded to float).</summary>
    internal static float[] MatRotation(double angleRad, char axis)
    {
        float angle = AngleWrapRad((float)angleRad);
        float c = (float)Math.Cos((double)angle), s = (float)Math.Sin((double)angle);
        // R[col][row] as the C writes it; item(row, col) = R[col][row]
        var r = IdentityRow();
        switch (axis)
        {
            case 'X': r[1 * 4 + 1] = c; r[2 * 4 + 1] = s; r[1 * 4 + 2] = -s; r[2 * 4 + 2] = c; break;   // R[1][1]=c R[1][2]=s R[2][1]=-s R[2][2]=c
            case 'Y': r[0 * 4 + 0] = c; r[2 * 4 + 0] = -s; r[0 * 4 + 2] = s; r[2 * 4 + 2] = c; break;   // R[0][0]=c R[0][2]=-s R[2][0]=s R[2][2]=c
            case 'Z': r[0 * 4 + 0] = c; r[1 * 4 + 0] = s; r[0 * 4 + 1] = -s; r[1 * 4 + 1] = c; break;   // R[0][0]=c R[0][1]=s R[1][0]=-s R[1][1]=c
            default: throw new ArgumentException("axis");
        }
        return r;
    }

    /// <summary>Matrix.Translation((x, y, z)): the doubles as float32.</summary>
    internal static float[] MatTranslation(double x, double y, double z) { var r = IdentityRow(); r[3] = (float)x; r[7] = (float)y; r[11] = (float)z; return r; }

    /// <summary>Matrix.Diagonal((sx, sy, sz, 1.0)).</summary>
    internal static float[] MatDiagonal(double sx, double sy, double sz) { var r = new float[16]; r[0] = (float)sx; r[5] = (float)sy; r[10] = (float)sz; r[15] = 1f; return r; }

    /// <summary>The second model's T2: Translation(off) @ Rotation(rz, Z) @ Rotation(ry, Y) @ Rotation(rx, X) @ Diagonal(scale) -
    /// scale along B's own axes, then the rotation, then the offset - multiplied left to right as Python evaluates it.</summary>
    internal static float[] Merge2Matrix(double[] offset, double[] rotationDeg, double[] scale)
    {
        var t = MatTranslation(offset[0], offset[1], offset[2]);
        var m = MatMulMathutils(t, MatRotation(Radians(rotationDeg[2]), 'Z'));
        m = MatMulMathutils(m, MatRotation(Radians(rotationDeg[1]), 'Y'));
        m = MatMulMathutils(m, MatRotation(Radians(rotationDeg[0]), 'X'));
        return MatMulMathutils(m, MatDiagonal(scale[0], scale[1], scale[2]));
    }

    /// <summary>The probe's _porient: Rotation(x, X) @ Rotation(y, Y) @ Rotation(z, Z) - the composition Generate straightens with.</summary>
    internal static float[] ProbeOrientMatrix(double[] deg) => MatMulMathutils(MatMulMathutils(MatRotation(Radians(deg[0]), 'X'), MatRotation(Radians(deg[1]), 'Y')), MatRotation(Radians(deg[2]), 'Z'));

    /// <summary>math.radians: x * (pi / 180) in double.</summary>
    static double Radians(double deg) => deg * (Math.PI / 180.0);

    /// <summary>Matrix.determinant() of a 4x4: determinant_m4 in float32 (the sign is what the merge reads off it).</summary>
    internal static float Determinant4(float[] it)
    {
        float a1 = it[0 * 4 + 0], b1 = it[1 * 4 + 0], c1 = it[2 * 4 + 0], d1 = it[3 * 4 + 0];
        float a2 = it[0 * 4 + 1], b2 = it[1 * 4 + 1], c2 = it[2 * 4 + 1], d2 = it[3 * 4 + 1];
        float a3 = it[0 * 4 + 2], b3 = it[1 * 4 + 2], c3 = it[2 * 4 + 2], d3 = it[3 * 4 + 2];
        float a4 = it[0 * 4 + 3], b4 = it[1 * 4 + 3], c4 = it[2 * 4 + 3], d4 = it[3 * 4 + 3];
        return (float)((float)((float)((float)(a1 * Det3(b2, b3, b4, c2, c3, c4, d2, d3, d4)) - (float)(b1 * Det3(a2, a3, a4, c2, c3, c4, d2, d3, d4))) + (float)(c1 * Det3(a2, a3, a4, b2, b3, b4, d2, d3, d4))) - (float)(d1 * Det3(a2, a3, a4, b2, b3, b4, c2, c3, c4)));
    }

    // ---------------------------------------------------------------- what Blender does with a matrix the script assigns

    /// <summary>`obj.matrix_world = M`: BKE_object_apply_mat4 decomposes M into loc, rot and size (mat4_to_loc_rot_size,
    /// mat3_normalized_to_quat; the delta transforms are the defaults and change nothing) and the next update rebuilds the
    /// matrix from them (BKE_object_to_mat4). The parent is None by then on every path the probe takes (the placement
    /// detaches first; a merged mesh is at the root). Row-major item layout in and out.</summary>
    internal static float[] ApplyMat4(float[] it)
    {
        DecomposeBlenderMatrix(it, out var loc, out var quat, out var size);
        return ToRowMajor(ObjectMatrix(loc, quat, size));
    }

    /// <summary>math::transform_point(float4x4, float3): ((x c0 + y c1) + z c2) + location, per component in float32.</summary>
    internal static void TransformPoint(float[] it, float x, float y, float z, out float rx, out float ry, out float rz)
    {
        rx = (float)((float)((float)((float)(x * it[0]) + (float)(y * it[1])) + (float)(z * it[2])) + it[3]);
        ry = (float)((float)((float)((float)(x * it[4]) + (float)(y * it[5])) + (float)(z * it[6])) + it[7]);
        rz = (float)((float)((float)((float)(x * it[8]) + (float)(y * it[9])) + (float)(z * it[10])) + it[11]);
    }

    /// <summary>world_bbox(o) as the script computes it: the 8 corners of the object's local box through matrix_world (mathutils
    /// Matrix @ Vector: float32 products summed in double), the component-wise min and max, centre (mn + mx) / 2 and size mx - mn
    /// in float32. The local box is Blender's `bound_box`: the mesh's float32 position extremes.</summary>
    internal static void WorldBox(float[] it, float[] localMin, float[] localMax, out float[] centre, out float[] size)
    {
        float[] mn = { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity }, mx = { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        for (int c = 0; c < 8; c++)
        {
            float x = (c & 1) == 0 ? localMin[0] : localMax[0], y = (c & 2) == 0 ? localMin[1] : localMax[1], z = (c & 4) == 0 ? localMin[2] : localMax[2];
            float px = MatRow(it, 0, 4, x, y, z, 1f), py = MatRow(it, 1, 4, x, y, z, 1f), pz = MatRow(it, 2, 4, x, y, z, 1f);
            mn[0] = Math.Min(mn[0], px); mn[1] = Math.Min(mn[1], py); mn[2] = Math.Min(mn[2], pz);
            mx[0] = Math.Max(mx[0], px); mx[1] = Math.Max(mx[1], py); mx[2] = Math.Max(mx[2], pz);
        }
        centre = new float[3]; size = new float[3];
        for (int i = 0; i < 3; i++) { centre[i] = (float)((float)(mn[i] + mx[i]) / 2.0f); size[i] = (float)(mx[i] - mn[i]); }
    }

    /// <summary>apply_part_placements' matrix for one part: T = Translation(off) @ Translation(c0) @ Diagonal(scale) @ Translation(-c0),
    /// then `o.matrix_world = T @ o.matrix_world` - the product and the assignment's round trip (ApplyMat4).</summary>
    internal static float[] PlacementMatrix(float[] matrixWorld, float[] c0, double[] offset, double[] scale)
    {
        var T = MatMulMathutils(MatTranslation(offset[0], offset[1], offset[2]), MatTranslation(c0[0], c0[1], c0[2]));
        T = MatMulMathutils(T, MatDiagonal(scale[0], scale[1], scale[2]));
        T = MatMulMathutils(T, MatTranslation(-c0[0], -c0[1], -c0[2]));
        return ApplyMat4(MatMulMathutils(T, matrixWorld));
    }

    /// <summary>mesh_flip_faces on triangles: corners 1 and 2 swap (a = face[1], b = face.last(0)); per-corner data goes with its
    /// corner. Returns the flipped faces; the corner-indexed arrays given are permuted in place.</summary>
    internal static int[] FlipFaces(int[] tris, params short[][] cornerData)
    {
        var r = (int[])tris.Clone();
        for (int f = 0; f < tris.Length / 3; f++)
        {
            int a = f * 3 + 1, b = f * 3 + 2;
            r[a] = tris[b]; r[b] = tris[a];
            foreach (var d in cornerData) if (d != null) { short t = d[a]; d[a] = d[b]; d[b] = t; }
        }
        return r;
    }

    /// <summary>The Lab's "%.5g" of a scale component, as Python prints it in the MERGE note.</summary>
    internal static string G5(double v)
    {
        if (v == 0) return "0";
        int exp = (int)Math.Floor(Math.Log10(Math.Abs(v)));
        if (exp < -4 || exp >= 5) { string e = v.ToString("0.####e+00", CultureInfo.InvariantCulture); return e; }
        string s = Math.Round(v, Math.Max(0, 4 - exp), MidpointRounding.ToEven).ToString("0.##########", CultureInfo.InvariantCulture);
        return s;
    }
}
