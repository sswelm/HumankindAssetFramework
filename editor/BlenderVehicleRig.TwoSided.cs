// vehicle_rig.py's Two-sided pass (_two_sided_pass, 2026-11-14), in C# (2026-11-16). Per face of a marked part: does its
// BACK show? From the face's centre and from 60 % toward each corner, rays leave the back side in 64 fixed directions
// against the WHOLE model; one that meets nothing says yes; when none escapes, the midpoint of the longest blocked ray
// casts the same directions (a pocket open to the outside). A face whose back shows gets a reversed copy on its own
// positions. The script's arithmetic, step for step:
//   - the world positions are mathutils' Matrix @ Vector (float32 products summed in double, rounded to float32);
//   - the tree is BVHTree.FromPolygons over every mesh's triangles in scene order (BlenderBvh: Blender's own tree and
//     walk, since a tie between two triangles at one distance is decided by which the walk reaches first);
//   - Vector +, -, * and / are float32 (a Python float operand is cast to float first), Vector.dot and Vector.length sum
//     float32 products in double, Vector == Vector allows one float step per component (EXPP_VectorsAreEqual), and the
//     step of the origin walk-back, the longest distance and every comparison against a literal are Python doubles;
//   - the directions are a Fibonacci sphere computed in Python doubles (math.cos and math.sin are the C runtime's) and
//     stored as float32 Vectors.
// The copy is bmesh.ops.duplicate on the chosen faces and reverse_faces on the copies: FinishMesh's own duplicate, without
// the normals and the inset of the whole-part doubling.
using System;
using System.Collections.Generic;
using System.Linq;

public static partial class BlenderVehicleRig
{
    /// <summary>_two_sided_dirs(n): a Fibonacci sphere, each component a Python double rounded into a float32 Vector.</summary>
    internal static float[][] TwoSidedDirs(int n)
    {
        var r = new float[n][];
        for (int k = 0; k < n; k++)
        {
            double z = 1.0 - 2.0 * (k + 0.5) / n;
            double rad = Math.Sqrt(Math.Max(0.0, 1.0 - z * z)), ph = k * Math.PI * (3.0 - Math.Sqrt(5.0));
            r[k] = new[] { (float)(rad * BlenderTrig.Cos(ph)), (float)(rad * BlenderTrig.Sin(ph)), (float)z };
        }
        return r;
    }

    /// <summary>_two_sided_newell(pw): per edge a float32 Vector of three products taken in double, added in float32.</summary>
    /// <summary>_two_sided_newell(pw) for a face of any size: per corner the Vector of the three products (Python doubles, each stored
    /// float32), added to the sum in float32.</summary>
    static float[] TwoSidedNewellOf(float[][] pw)
    {
        var n = new float[3];
        for (int i = 0; i < pw.Length; i++)
        {
            var p = pw[i]; var q = pw[(i + 1) % pw.Length];
            float x = (float)(((double)p[1] - q[1]) * ((double)p[2] + q[2])), y = (float)(((double)p[2] - q[2]) * ((double)p[0] + q[0])), z = (float)(((double)p[0] - q[0]) * ((double)p[1] + q[1]));
            n[0] = (float)(n[0] + x); n[1] = (float)(n[1] + y); n[2] = (float)(n[2] + z);
        }
        return n;
    }

    static float[] TwoSidedNewell(float[] a, float[] b, float[] c)
    {
        var n = new float[3];
        for (int i = 0; i < 3; i++)
        {
            var p = i == 0 ? a : i == 1 ? b : c; var q = i == 0 ? b : i == 1 ? c : a;
            float x = (float)(((double)p[1] - q[1]) * ((double)p[2] + q[2])), y = (float)(((double)p[2] - q[2]) * ((double)p[0] + q[0])), z = (float)(((double)p[0] - q[0]) * ((double)p[1] + q[1]));
            n[0] = (float)(n[0] + x); n[1] = (float)(n[1] + y); n[2] = (float)(n[2] + z);
        }
        return n;
    }

    /// <summary>frozenset(tuple(q) for q in pw): the face's DISTINCT corner positions, whatever their order. A tuple of
    /// Python floats compares by value, so -0.0 is 0.0.</summary>
    internal struct TwoSidedKey : IEquatable<TwoSidedKey>
    {
        readonly float x0, y0, z0, x1, y1, z1, x2, y2, z2; readonly int count;
        public TwoSidedKey(float[] a, float[] b, float[] c)
        {
            var p = new[] { a, b, c };
            Array.Sort(p, (u, v) => u[0] != v[0] ? u[0].CompareTo(v[0]) : u[1] != v[1] ? u[1].CompareTo(v[1]) : u[2] != v[2] ? u[2].CompareTo(v[2]) : 0);
            int n = 1;
            if (!Same(p[1], p[0])) p[n++] = p[1];
            if (!Same(p[2], p[n - 1])) p[n++] = p[2];
            count = n;
            x0 = p[0][0] + 0f; y0 = p[0][1] + 0f; z0 = p[0][2] + 0f;
            x1 = n > 1 ? p[1][0] + 0f : 0f; y1 = n > 1 ? p[1][1] + 0f : 0f; z1 = n > 1 ? p[1][2] + 0f : 0f;
            x2 = n > 2 ? p[2][0] + 0f : 0f; y2 = n > 2 ? p[2][1] + 0f : 0f; z2 = n > 2 ? p[2][2] + 0f : 0f;
        }
        static bool Same(float[] u, float[] v) => u[0] == v[0] && u[1] == v[1] && u[2] == v[2];
        public bool Equals(TwoSidedKey o) => count == o.count && x0 == o.x0 && y0 == o.y0 && z0 == o.z0 && x1 == o.x1 && y1 == o.y1 && z1 == o.z1 && x2 == o.x2 && y2 == o.y2 && z2 == o.z2;
        public override bool Equals(object o) => o is TwoSidedKey k && Equals(k);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = count;
                foreach (float f in new[] { x0, y0, z0, x1, y1, z1, x2, y2, z2 }) h = h * 486187739 + f.GetHashCode();
                return h;
            }
        }
    }

    /// <summary>EXPP_VectorsAreEqual(a, b, 3, 1): each component within one float step (the bit patterns compared as signed
    /// magnitudes), which is what Vector == Vector means.</summary>
    internal static bool PyVectorEquals(float[] a, float[] b)
    {
        for (int k = 0; k < 3; k++)
        {
            int ai = BitConverter.ToInt32(BitConverter.GetBytes(a[k]), 0), bi = BitConverter.ToInt32(BitConverter.GetBytes(b[k]), 0);
            int test = -(int)((uint)(ai ^ bi) >> 31);
            int diff = unchecked((ai ^ (test & 0x7fffffff)) - bi);
            if ((unchecked(1 + diff) | unchecked(1 - diff)) < 0) return false;
        }
        return true;
    }

    /// <summary>The whole model the pass measures against, and the tree over it.</summary>
    internal sealed class TwoSidedScene
    {
        public float[] World;            // every mesh's vertices in world space, in scene order
        public int[] Tris;               // every mesh's triangles on those vertices
        public int[] OwnerObject;        // per triangle: its mesh's place in the list (with the face: the script's owners)
        public int[] Base, FaceBase;     // per mesh: its first vertex and its first triangle
        public BlenderBvh Bvh;
        public Dictionary<TwoSidedKey, List<float[]>> Twin;
        public double Eps;
        public float[][] Dirs;

        public float[] Corner(int tri, int k) { int v = 3 * Tris[3 * tri + k]; return new[] { World[v], World[v + 1], World[v + 2] }; }
    }

    internal static TwoSidedScene BuildTwoSidedScene(IList<BlenderRigAnim.SceneObject> everything)
    {
        var sc = new TwoSidedScene { Base = new int[everything.Count], FaceBase = new int[everything.Count], Twin = new Dictionary<TwoSidedKey, List<float[]>>(), Dirs = TwoSidedDirs(64) };
        // a mesh of polygons (a subdivided tread) gives its LOOP TRIANGLES to the tree (FbxModel.Tessellate: Mesh.loop_triangles) and
        // one twin entry per POLYGON, as the script's loop over _m.polygons does
        var tess = everything.Select(o => o.Mesh.PolyStart != null ? FbxModel.Tessellate(o.Mesh.Positions, o.Mesh.Faces, o.Mesh.PolyStart) : null).ToList();
        int nv = everything.Sum(o => o.Mesh.Vertices), nt = Enumerable.Range(0, everything.Count).Sum(i => tess[i] != null ? tess[i].Count : everything[i].Mesh.Faces.Length / 3);
        sc.World = new float[3 * nv]; sc.Tris = new int[3 * nt]; sc.OwnerObject = new int[nt];
        int vo = 0, to = 0;
        for (int oi = 0; oi < everything.Count; oi++)
        {
            var o = everything[oi]; var me = o.Mesh; var it = VehicleProbe.ToRowMajor(o.World);
            sc.Base[oi] = vo; sc.FaceBase[oi] = to;
            for (int v = 0; v < me.Vertices; v++)
            {
                float x = me.Positions[3 * v], y = me.Positions[3 * v + 1], z = me.Positions[3 * v + 2];
                sc.World[3 * (vo + v)] = MulPointRow(it, 0, x, y, z); sc.World[3 * (vo + v) + 1] = MulPointRow(it, 1, x, y, z); sc.World[3 * (vo + v) + 2] = MulPointRow(it, 2, x, y, z);
            }
            if (tess[oi] != null)
            {
                var tl = tess[oi];
                for (int t = 0; t < tl.Count; t++)
                {
                    sc.Tris[3 * (to + t)] = vo + me.Faces[tl[t].C0]; sc.Tris[3 * (to + t) + 1] = vo + me.Faces[tl[t].C1]; sc.Tris[3 * (to + t) + 2] = vo + me.Faces[tl[t].C2];
                    sc.OwnerObject[to + t] = oi;
                }
                for (int pf = 0; pf < me.FaceCount; pf++)
                {
                    int c0 = me.CornerStart(pf), n = me.CornerCount(pf);
                    var pw = new float[n][]; for (int k = 0; k < n; k++) { int v = 3 * (vo + me.Faces[c0 + k]); pw[k] = new[] { sc.World[v], sc.World[v + 1], sc.World[v + 2] }; }
                    // the key is the SET of corner positions: a face with more than three distinct ones equals no triangle's set
                    var distinct = new List<float[]>();
                    foreach (var q in pw) if (!distinct.Any(u => u[0] == q[0] && u[1] == q[1] && u[2] == q[2])) distinct.Add(q);
                    if (distinct.Count > 3) continue;
                    var pkey = new TwoSidedKey(distinct[0], distinct[Math.Min(1, distinct.Count - 1)], distinct[distinct.Count - 1]);
                    if (!sc.Twin.TryGetValue(pkey, out var plist)) sc.Twin[pkey] = plist = new List<float[]>(1);
                    plist.Add(TwoSidedNewellOf(pw));
                }
                vo += me.Vertices; to += tl.Count;
                continue;
            }
            int nf = me.Faces.Length / 3;
            for (int f = 0; f < nf; f++)
            {
                for (int k = 0; k < 3; k++) sc.Tris[3 * (to + f) + k] = vo + me.Faces[3 * f + k];
                sc.OwnerObject[to + f] = oi;
                float[] a = sc.Corner(to + f, 0), b = sc.Corner(to + f, 1), c = sc.Corner(to + f, 2);
                var key = new TwoSidedKey(a, b, c);
                if (!sc.Twin.TryGetValue(key, out var list)) sc.Twin[key] = list = new List<float[]>(1);
                list.Add(TwoSidedNewell(a, b, c));
            }
            vo += me.Vertices; to += nf;
        }
        sc.Bvh = new BlenderBvh(sc.World, sc.Tris);
        double dim = 1.0;
        if (nv > 0)
        {
            dim = double.NegativeInfinity;
            for (int k = 0; k < 3; k++)
            {
                float mn = sc.World[k], mx = sc.World[k];
                for (int v = 1; v < nv; v++) { float c = sc.World[3 * v + k]; if (c < mn) mn = c; if (c > mx) mx = c; }
                dim = Math.Max(dim, (double)mx - mn);
            }
        }
        sc.Eps = Math.Max(1e-7, dim * 1e-5);
        return sc;
    }

    /// <summary>_two_sided_origin: the start of a back ray, a small step off the face along the direction - walked back
    /// until the segment from the face crosses no other surface (a step scaled to the whole model can span a small closed
    /// solid).</summary>
    static float[] TwoSidedOrigin(TwoSidedScene sc, float[] sample, float[] direction, int own)
    {
        double step = sc.Eps;
        while (true)
        {
            var origin = Add(sample, Scale(direction, (float)step));
            if (PyVectorEquals(origin, sample)) return sample;
            int hit = sc.Bvh.RayCast(origin[0], origin[1], origin[2], -direction[0], -direction[1], -direction[2], (float)(step * 1.01), out float dist);
            if (hit < 0 || hit == own) return origin;
            double a = step * 0.25, b = (step - (double)dist) * 0.25, m = b < a ? b : a;   // min(a, b), then max(0.0, m)
            step = m > 0.0 ? m : 0.0;
        }
    }

    /// <summary>Does the back of triangle `tri` (a face of a marked part) show? Null for a face the pass skips: +1 a
    /// degenerate face (no side), -1 a face with an authored twin.</summary>
    internal static bool TwoSidedBackShows(TwoSidedScene sc, int tri, out int skipped)
    {
        skipped = 0;
        float[] p0 = sc.Corner(tri, 0), p1 = sc.Corner(tri, 1), p2 = sc.Corner(tri, 2);
        var n = TwoSidedNewell(p0, p1, p2);
        if (VecLength(n) < 1e-20) { skipped = 1; return false; }
        foreach (var n2 in sc.Twin[new TwoSidedKey(p0, p1, p2)]) if (Dot(n2, n) < 0.0) { skipped = -1; return false; }
        n = Normalized(n);
        var c = Scale(Add(Add(Add(new float[3], p0), p1), p2), (float)(1.0f / 3.0f));
        var dirs = sc.Dirs;
        for (int si = 0; si < 4; si++)
        {
            var s = si == 0 ? c : Add(c, Scale(Sub(si == 1 ? p0 : si == 2 ? p1 : p2, c), (float)0.6));
            double far = 0.0; float[] mid = null;
            foreach (var d in dirs)
            {
                if (Dot(d, n) > -1e-3) continue;   // the back side's directions only
                var org = TwoSidedOrigin(sc, s, d, tri);
                int hit = sc.Bvh.RayCast(org[0], org[1], org[2], d[0], d[1], d[2], float.MaxValue, out float dist);
                if (hit < 0) return true;
                if ((double)dist > far) { far = dist; mid = Add(org, Scale(d, (float)(far * 0.5))); }
            }
            if (mid != null)
                foreach (var d2 in dirs)
                    if (sc.Bvh.RayCast(mid[0], mid[1], mid[2], d2[0], d2[1], d2[2], float.MaxValue, out _) < 0) return true;
        }
        return false;
    }

    /// <summary>_two_sided_pass(targets, everything): each target in turn - the faces whose back shows, doubled in place -
    /// and its line. Every target is judged against the model as it was BUILT (the tree is made once), while each
    /// target's own faces are read as they are when its turn comes - a target is its own mesh, so that is the same.</summary>
    static void TwoSidedPass(BlenderRigAnim.Stage1 s, List<BlenderRigAnim.SceneObject> targets, List<BlenderRigAnim.SceneObject> everything, Action checkpoint)
    {
        checkpoint?.Invoke();
        var sc = BuildTwoSidedScene(everything);
        foreach (var o in targets)
        {
            int oi = everything.IndexOf(o), nf = o.Mesh.Faces.Length / 3, first = sc.FaceBase[oi];
            var verdict = new sbyte[nf];   // 1 doubled, -1 an authored twin, 0 left
            // Poll on the calling thread between bounded batches. A poll inside Parallel.For would
            // wrap cancellation/deadline exceptions in AggregateException, turning them into a Blender fallback.
            for (int firstFace = 0; firstFace < nf; firstFace += 128)
            {
                checkpoint?.Invoke();
                System.Threading.Tasks.Parallel.For(firstFace, Math.Min(firstFace + 128, nf), f =>
                {
                    bool back = TwoSidedBackShows(sc, first + f, out int skipped);
                    verdict[f] = back ? (sbyte)1 : skipped < 0 ? (sbyte)-1 : (sbyte)0;
                });
            }
            checkpoint?.Invoke();
            var both = new List<int>(); int twinned = 0;
            for (int f = 0; f < nf; f++) { if (verdict[f] == 1) both.Add(f); else if (verdict[f] == -1) twinned++; }
            int v0 = o.Mesh.Vertices;
            if (both.Count > 0) o.Mesh = FinishMesh(o.Mesh, false, false, null, out _, out _, both, checkpoint);
            s.Logs5.Add("VEHICLE two-sided '" + o.Name + "': " + both.Count + " face(s) doubled (the back shows), " + (nf - both.Count) + " left; " + twinned + " authored twin(s) kept; " + v0 + " -> " + o.Mesh.Vertices + " verts");
        }
    }
}
