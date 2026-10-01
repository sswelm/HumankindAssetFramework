// VehicleProbe.cs - the Vehicle Lab's "probe" in C# (step 3 of replacing Blender, 2026-10-02): what vehicle_rig.py's
// `probe` mode prints for a source model, computed from the HafModel the reader gives - no Blender boot, import,
// FBX export, no Python (25 s a probe, measured; here about a tenth of a second). The rows are the SAME rows, in
// Blender's terms, because every saved recipe is keyed by them:
//   PART     one per mesh object Blender's importer would make (BlenderNames: the node's name as Blender dedups it,
//            in Blender's creation order), its vertex count - the vertices its primitives USE, as the importer keeps
//            only those (np.unique over the indices) -, its WORLD bounding box in Blender's Z-up frame computed
//            the way `world_bbox` computes it (the object's LOCAL bbox corners through its world matrix - larger than
//            the vertices' own box when the part is rotated), the dominant bone of a skinned part;
//   RIGBONE  one per bone of the first armature when 90 % of the skinned vertices have a bone over 0.5 weight: the
//            vertices' bounding box at the importer's BIND pose (vehicle_rig.py's rig_report reads the undeformed
//            mesh: the vertices skinned into the bind pose the importer guesses from the inverse bind matrices, in
//            the armature's space, through the armature object's world matrix).
// What Blender does that this reproduces on purpose: a source with ONE mesh object is split into its loose parts
// (islands by shared vertex index; the island with the lowest vertex index keeps the name, the rest count up in
// that order - measured); the importer's bone-shape "Icosphere" is purged by signature, and so is any real part
// that matches it (the script's rule, kept). The visibility and inside-out verdicts (fields 6 and 8) come in their
// own steps; until then every part is "visible" and "keeps as authored", and the drill does not compare them.
// Proof: tools/vehicle_probe_drill.sh runs Blender's probe and this on every registry source and compares the rows.
using System;
using System.Collections.Generic;
using System.Linq;

public static class VehicleProbe
{
    public sealed class Part
    {
        public string Name;                 // Blender's object name for it
        public int Node;                    // the glTF node whose mesh it is (an island shares its node)
        public int Verts;
        public double[] Center, Size;       // world bbox, Blender frame (x, -z, y of glTF)
        public int Vis = 1;                 // 1 external / 0 interior (step 3b)
        public string Bone = "";            // dominant bone of a skinned part, else ""
        public int Flip;                    // islands the inside-out fix would reverse (step 3c)
        public string Row => string.Join("|", "PART", Name, Verts.ToString(), F(Center), F(Size), Vis.ToString(), Bone, Flip.ToString());
    }

    public sealed class RigBone
    {
        public string Name; public int Count; public double[] Center, Size;
        public string Row => string.Join("|", "RIGBONE", Name, Count.ToString(), F(Center), F(Size));
    }

    public sealed class Result
    {
        public readonly List<Part> Parts = new List<Part>();
        public readonly List<RigBone> RigBones = new List<RigBone>();
        public string Armature;             // the first armature's name, or null
        public readonly List<string> Notes = new List<string>();   // what the script prints as "VEHICLE ..." lines
        public bool Split;                  // a single mesh object was split into loose parts (names synthetic)
    }

    static string F(double[] v) => string.Join(",", v.Select(x => x.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>The probe of one source model, in the pose Blender's import shows: the first animation's start (the
    /// parity reference since PR #109), or the static transforms when there is none.</summary>
    public static Result Run(HafModel m)
    {
        var r = new Result();
        var names = BlenderNames.Compute(m);
        // a node given by a MATRIX is, in Blender, that matrix decomposed to translation, rotation and scale and recomposed
        // (the importer's get_node_trs): whatever shear it holds is gone. The Lab's own rah66.glb has such a node (axes 0.13
        // degrees off square) and four parts' boxes read 0.15 % differently through the matrix as given.
        var pose = m.Animations.Count > 0 ? HafTransforms.PoseAt(m, 0, 0.0) : null;
        var world = HafTransforms.WorldMatrices(m, i => pose?.Invoke(i) ?? (m.Nodes[i].HasMatrix ? AsBlenderDecomposes(m.Nodes[i].Matrix) : UnitRotation(m.Nodes[i])));
        // which node's world matrix is each skin's armature OBJECT: the node that became it, or identity for the dummy root
        var armaWorld = new double[m.Skins.Count][];
        for (int si = 0; si < m.Skins.Count; si++) armaWorld[si] = names.ArmatureNodeOfSkin[si] >= 0 ? world[names.ArmatureNodeOfSkin[si]] : HafTransforms.Identity;
        r.Armature = names.ArmaturesInOrder.Count > 0 ? names.ArmaturesInOrder[0].name : null;   // the first CREATED, which is what `arms[0]` is - not skin 0's

        // ---- the mesh objects, in Blender's order, each one object per node (a mesh two nodes share is two objects)
        var objects = new List<(int node, string name)>(); var purgedNames = new List<string>();
        foreach (var (node, name) in names.MeshObjectsInOrder)
        {
            var mesh = m.Meshes[m.Nodes[node].Mesh];
            if (mesh.Primitives.Sum(p => Used(p).Length) == 0) continue;            // mesh_objects(): len(vertices) > 0
            if (IsIcosphereArtifact(m, node, name)) { r.Notes.Add("purged glTF importer bone-shape artifact: " + name); purgedNames.Add(name); continue; }
            objects.Add((node, name));
        }

        // ---- a single mesh object: Blender separates its loose parts and names them after it
        if (objects.Count == 1)
        {
            var (node, name) = objects[0];
            var islands = Islands(m.Meshes[m.Nodes[node].Mesh]);
            r.Split = true;
            r.Notes.Add($"single mesh split into {islands.Count} loose parts (names are synthetic)");
            // the loose parts are named in the pool of EVERY object the import made (an empty called Hull.001 pushes the
            // second island to Hull.002), less the bone shapes and the artefacts the script purged before it split
            var pool = names.ObjectPool.Clone();
            foreach (var shape in names.BoneShapes) pool.Remove(shape);
            foreach (var gone in purgedNames) pool.Remove(gone);   // only what was PURGED frees its name: a mesh object without vertices is not listed, but still there
            var geo = Geometry(m, node, world, armaWorld);   // ONCE: 3,350 islands each re-posing the whole mesh took 18 s on the Ehrhardt
            // each new object is a copy of the first, asking for ITS name: the smallest number its base has free
            // (Hull.005 beside an empty Hull.002 splits into Hull.005, Hull.001, Hull.003, Hull.004 - measured)
            for (int k = 0; k < islands.Count; k++)
                r.Parts.Add(MakePart(m, node, k == 0 ? name : pool.Unique(name), geo, islands[k].prim, islands[k].verts, names));
        }
        else
            foreach (var (node, name) in objects) r.Parts.Add(MakePart(m, node, name, Geometry(m, node, world, armaWorld), -1, null, names));

        // ---- rig_report: the first armature's bones, from the undeformed vertices (v.co = the bind pose)
        if (names.ArmaturesInOrder.Count > 0) RigReport(m, names, armaWorld, BindArmatureMatrices(m, names), r);
        return r;
    }

    // ---------------------------------------------------------------- one part

    /// <summary>A node's mesh as Blender holds it: per primitive the vertices in the OBJECT's local space - the file's
    /// for a static part; for a skinned one the posed positions brought into the armature's space (the mesh is the
    /// armature's child and is deformed there) - and the object's world matrix.</summary>
    sealed class NodeGeometry { public double[][] Local; public double[] ObjWorld; public bool Skinned; public int Skin; }   // Local[pi] is null for a static part: its positions are the file's, read in place

    static NodeGeometry Geometry(HafModel m, int node, double[][] world, double[][] armaWorld)
    {
        var mesh = m.Meshes[m.Nodes[node].Mesh];
        int skin = m.Nodes[node].Skin;
        var g = new NodeGeometry { Skin = skin, Local = new double[mesh.Primitives.Count][] };
        g.Skinned = skin >= 0 && skin < m.Skins.Count && mesh.Primitives.Any(p => p.Skinned);
        g.ObjWorld = g.Skinned ? armaWorld[skin] : world[node];
        double[] toLocal = g.Skinned ? HafTransforms.Invert(armaWorld[skin]) ?? HafTransforms.Identity : null;
        for (int pi = 0; pi < mesh.Primitives.Count; pi++)
        {
            var p = mesh.Primitives[pi];
            if (!g.Skinned) continue;   // no copy: 925 MB of Lab sources as doubles is what ran the drill out of memory
            var local = new double[p.VertexCount * 3];
            var posed = HafTransforms.WorldPositions(m, node, p, world);
            for (int v = 0; v < p.VertexCount; v++) { var q = HafTransforms.Apply(toLocal, posed[v * 3], posed[v * 3 + 1], posed[v * 3 + 2], 1.0); local[v * 3] = q[0]; local[v * 3 + 1] = q[1]; local[v * 3 + 2] = q[2]; }
            g.Local[pi] = local;
        }
        return g;
    }

    /// <summary>A part: a node's whole mesh (onlyPrim -1), or one island of it (the vertices of one primitive).</summary>
    static Part MakePart(HafModel m, int node, string name, NodeGeometry g, int onlyPrim, int[] onlyVerts, BlenderNames.Result names)
    {
        var mesh = m.Meshes[m.Nodes[node].Mesh];
        int skin = g.Skin; bool skinned = g.Skinned;
        var objWorld = g.ObjWorld;
        double[] mn = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity }, mx = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        int verts = 0;
        var tally = new Dictionary<string, double>(); var tallyOrder = new List<string>();
        for (int pi = 0; pi < mesh.Primitives.Count; pi++)
        {
            if (onlyPrim >= 0 && pi != onlyPrim) continue;
            var p = mesh.Primitives[pi];
            var local = g.Local[pi];
            foreach (int v in onlyVerts ?? Used(p))
            {
                verts++;
                double x, y, z;
                if (local != null) { x = local[v * 3]; y = local[v * 3 + 1]; z = local[v * 3 + 2]; }
                else { x = p.Positions[v * 3]; y = p.Positions[v * 3 + 1]; z = p.Positions[v * 3 + 2]; }
                if (x < mn[0]) mn[0] = x; if (x > mx[0]) mx[0] = x; if (y < mn[1]) mn[1] = y; if (y > mx[1]) mx[1] = y; if (z < mn[2]) mn[2] = z; if (z > mx[2]) mx[2] = z;
                if (skinned)
                    for (int set = 0; set < 2; set++)
                    {
                        var joints = set == 0 ? p.Joints : p.Joints1; var weights = set == 0 ? p.Weights : p.Weights1;
                        if (joints == null || weights == null) continue;
                        for (int k = 0; k < 4; k++)
                        {
                            float w = weights[v * 4 + k];
                            if (w <= 0) continue;
                            int joint = joints[v * 4 + k];
                            if (joint >= m.Skins[skin].Joints.Length || !names.BoneOfJoint.TryGetValue(m.Skins[skin].Joints[joint], out var bone)) continue;
                            if (!tally.ContainsKey(bone)) { tally[bone] = 0; tallyOrder.Add(bone); }
                            tally[bone] += w;
                        }
                    }
            }
        }
        // world_bbox: the 8 corners of the local box through the object's world matrix, into Blender's frame, min/max
        double[] wmn = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity }, wmx = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        for (int c = 0; c < 8; c++)
        {
            var q = HafTransforms.Apply(objWorld, (c & 1) == 0 ? mn[0] : mx[0], (c & 2) == 0 ? mn[1] : mx[1], (c & 4) == 0 ? mn[2] : mx[2], 1.0);
            double bx = q[0], by = -q[2], bz = q[1];
            if (bx < wmn[0]) wmn[0] = bx; if (bx > wmx[0]) wmx[0] = bx; if (by < wmn[1]) wmn[1] = by; if (by > wmx[1]) wmx[1] = by; if (bz < wmn[2]) wmn[2] = bz; if (bz > wmx[2]) wmx[2] = bz;
        }
        string dominant = "";
        if (tallyOrder.Count > 0) { double best = double.NegativeInfinity; foreach (var b in tallyOrder) if (tally[b] > best) { best = tally[b]; dominant = b; } }   // the first of equals wins, as Python's max does
        return new Part
        {
            Name = name, Node = node, Verts = verts,
            Center = new[] { (wmn[0] + wmx[0]) / 2, (wmn[1] + wmx[1]) / 2, (wmn[2] + wmx[2]) / 2 },
            Size = new[] { wmx[0] - wmn[0], wmx[1] - wmn[1], wmx[2] - wmn[2] },
            Bone = dominant,
        };
    }

    // ---------------------------------------------------------------- the vertices Blender has

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HafPrimitive, int[]> usedCache = new System.Runtime.CompilerServices.ConditionalWeakTable<HafPrimitive, int[]>();

    /// <summary>The vertices of a primitive as Blender's importer keeps them: the indices its faces, lines or points
    /// USE, unique and ascending (np.unique over the indices; a vertex nothing uses is never imported - the interleaved
    /// fixture's two primitives share one vertex array and each keeps its own six of eight). Non-indexed: all of them.</summary>
    static int[] Used(HafPrimitive p)
    {
        return usedCache.GetValue(p, q =>
        {
            if (q.Indices == null) { var all = new int[q.VertexCount]; for (int i = 0; i < all.Length; i++) all[i] = i; return all; }
            var seen = new bool[q.VertexCount];
            foreach (int ix in q.Indices) if (ix >= 0 && ix < seen.Length) seen[ix] = true;
            var list = new List<int>(); for (int i = 0; i < seen.Length; i++) if (seen[i]) list.Add(i);
            return list.ToArray();
        });
    }

    // ---------------------------------------------------------------- loose parts, as Blender separates them

    /// <summary>The mesh's islands by shared vertex INDEX (a glTF primitive's vertices are its own; two primitives never
    /// connect), each the vertices of one primitive, ordered by their lowest vertex - Blender's `separate(type='LOOSE')`
    /// order (measured on fixtures whose islands were laid out against it).</summary>
    static List<(int prim, int[] verts)> Islands(HafMesh mesh)
    {
        var result = new List<(int, int[])>();
        for (int pi = 0; pi < mesh.Primitives.Count; pi++)
        {
            var p = mesh.Primitives[pi];
            var parent = new int[p.VertexCount]; for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
            int count = p.Indices != null ? p.Indices.Length : p.VertexCount;
            int At(int i) => p.Indices != null ? p.Indices[i] : i;
            switch (p.Mode)
            {
                case 4: for (int t = 0; t + 2 < count; t += 3) { Union(At(t), At(t + 1)); Union(At(t + 1), At(t + 2)); } break;
                case 5: case 6: for (int t = 0; t + 2 < count; t++) { Union(At(t), At(t + 1)); Union(At(t + 1), At(t + 2)); } break;
                case 1: for (int t = 0; t + 1 < count; t += 2) Union(At(t), At(t + 1)); break;
                case 2: for (int t = 0; t < count; t++) Union(At(t), At((t + 1) % count)); break;
                case 3: for (int t = 0; t + 1 < count; t++) Union(At(t), At(t + 1)); break;
            }
            var byRoot = new Dictionary<int, List<int>>(); var order = new List<int>();
            foreach (int v in Used(p))   // the vertices Blender has: an unused one was never imported, so it is no island
            {
                int root = Find(v);
                if (!byRoot.TryGetValue(root, out var list)) { list = new List<int>(); byRoot[root] = list; order.Add(root); }   // roots are the lowest vertex of each island, met in vertex order
                list.Add(v);
            }
            foreach (var root in order) result.Add((pi, byRoot[root].ToArray()));
        }
        return result;
    }

    // ---------------------------------------------------------------- the importer's bone-shape artefact, by signature

    static bool IsIcosphereArtifact(HafModel m, int node, string name)
    {
        if (!name.StartsWith("Icosphere", StringComparison.Ordinal) && !name.StartsWith("B_Icosphere", StringComparison.Ordinal)) return false;
        var mesh = m.Meshes[m.Nodes[node].Mesh];
        if (m.Nodes[node].Skin >= 0 && mesh.Primitives.Any(p => p.Skinned)) return false;   // it has vertex groups
        int n = mesh.Primitives.Sum(p => Used(p).Length);
        if (n != 12 && n != 42 && n != 162 && n != 642) return false;
        double[] mn = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity }, mx = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var p in mesh.Primitives) foreach (int v in Used(p)) for (int a = 0; a < 3; a++) { double c = p.Positions[v * 3 + a]; if (c < mn[a]) mn[a] = c; if (c > mx[a]) mx[a] = c; }
        double ex = mx[0] - mn[0], ey = mx[1] - mn[1], ez = mx[2] - mn[2], big = Math.Max(ex, Math.Max(ey, ez)), small = Math.Min(ex, Math.Min(ey, ez));
        return big > 0 && small > 0.8 * big;
    }

    // ---------------------------------------------------------------- a rotation as Blender holds it

    /// <summary>A TRS node's local matrix with its rotation NORMALIZED, or null when it already is: a quaternion that is
    /// not of unit length (invalid by the letter, written by real exporters) scales the part by its squared length when
    /// taken as given - (0, 1, 0, 1) made a part twice its size; Blender normalizes and so does this.</summary>
    static double[] UnitRotation(HafNode n)
    {
        var q = n.Rotation;
        double len = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
        if (len == 0 || Math.Abs(len - 1) < 1e-12) return null;
        return HafTransforms.Trs(n.Translation, new[] { q[0] / len, q[1] / len, q[2] / len, q[3] / len }, n.Scale);
    }

    // ---------------------------------------------------------------- a matrix as Blender holds it

    /// <summary>A glTF node matrix as Blender's importer keeps it: converted to Blender's frame, decomposed
    /// (`Matrix.decompose()`: the columns' lengths are the scale, negated together when the matrix mirrors; the
    /// normalized columns go through mat3_normalized_to_quat_fast, which reads a rotation off them WITHOUT
    /// orthogonalizing - shear is dropped, not distributed), recomposed as T * R * S and brought back to glTF's frame.
    /// An exactly decomposable matrix comes back unchanged to rounding.</summary>
    public static double[] AsBlenderDecomposes(double[] g)
    {
        // Blender frame: (x, y, z)_b = (x, -z, y)_g, so M_b = C M_g C^-1 with C the rows [1,0,0], [0,0,-1], [0,1,0]
        double[] b = ToBlender(g);
        double[] size = new double[3]; var rot = new double[3][];
        for (int c = 0; c < 3; c++)
        {
            double x = b[c * 4], y = b[c * 4 + 1], z = b[c * 4 + 2];
            size[c] = Math.Sqrt(x * x + y * y + z * z);
            rot[c] = size[c] > 0 ? new[] { x / size[c], y / size[c], z / size[c] } : new double[3];
        }
        double det = rot[0][0] * (rot[1][1] * rot[2][2] - rot[2][1] * rot[1][2]) - rot[1][0] * (rot[0][1] * rot[2][2] - rot[2][1] * rot[0][2]) + rot[2][0] * (rot[0][1] * rot[1][2] - rot[1][1] * rot[0][2]);
        if (det < 0) for (int c = 0; c < 3; c++) { size[c] = -size[c]; for (int k = 0; k < 3; k++) rot[c][k] = -rot[c][k]; }
        // mat3_normalized_to_quat_fast (math_rotation.c; mat[col][row]) -> (w, x, y, z), normalized
        double qw, qx, qy, qz;
        if (rot[2][2] < 0)
        {
            if (rot[0][0] > rot[1][1])
            {
                double trace = 1 + rot[0][0] - rot[1][1] - rot[2][2], sq = 2 * Math.Sqrt(trace);
                if (rot[1][2] < rot[2][1]) sq = -sq;
                qx = 0.25 * sq; sq = 1 / sq; qw = (rot[1][2] - rot[2][1]) * sq; qy = (rot[0][1] + rot[1][0]) * sq; qz = (rot[2][0] + rot[0][2]) * sq;
            }
            else
            {
                double trace = 1 - rot[0][0] + rot[1][1] - rot[2][2], sq = 2 * Math.Sqrt(trace);
                if (rot[2][0] < rot[0][2]) sq = -sq;
                qy = 0.25 * sq; sq = 1 / sq; qw = (rot[2][0] - rot[0][2]) * sq; qx = (rot[0][1] + rot[1][0]) * sq; qz = (rot[1][2] + rot[2][1]) * sq;
            }
        }
        else
        {
            if (rot[0][0] < -rot[1][1])
            {
                double trace = 1 - rot[0][0] - rot[1][1] + rot[2][2], sq = 2 * Math.Sqrt(trace);
                if (rot[0][1] < rot[1][0]) sq = -sq;
                qz = 0.25 * sq; sq = 1 / sq; qw = (rot[0][1] - rot[1][0]) * sq; qx = (rot[2][0] + rot[0][2]) * sq; qy = (rot[1][2] + rot[2][1]) * sq;
            }
            else
            {
                double trace = 1 + rot[0][0] + rot[1][1] + rot[2][2], sq = 2 * Math.Sqrt(trace);
                qw = 0.25 * sq; sq = 1 / sq; qx = (rot[1][2] - rot[2][1]) * sq; qy = (rot[2][0] - rot[0][2]) * sq; qz = (rot[0][1] - rot[1][0]) * sq;
            }
        }
        double len = Math.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
        if (len > 0) { qw /= len; qx /= len; qy /= len; qz /= len; } else { qw = 1; qx = qy = qz = 0; }
        var recomposed = HafTransforms.Trs(new[] { b[12], b[13], b[14] }, new[] { qx, qy, qz, qw }, size);
        return FromBlender(recomposed);
    }

    // M_b = C M_g C^-1 and back; C maps glTF (x, y, z) to Blender (x, -z, y)
    static readonly int[] gOfB = { 0, 2, 1 }; static readonly double[] sgn = { 1, -1, 1 };   // Blender axis i = sgn[i] * glTF axis gOfB[i]
    static double[] ToBlender(double[] g)
    {
        var b = new double[16]; b[15] = 1;
        for (int c = 0; c < 3; c++) for (int r = 0; r < 3; r++) b[c * 4 + r] = sgn[r] * sgn[c] * g[gOfB[c] * 4 + gOfB[r]];
        for (int r = 0; r < 3; r++) b[12 + r] = sgn[r] * g[12 + gOfB[r]];
        return b;
    }
    static double[] FromBlender(double[] b)
    {
        var g = new double[16]; g[15] = 1;
        for (int c = 0; c < 3; c++) for (int r = 0; r < 3; r++) g[gOfB[c] * 4 + gOfB[r]] = sgn[r] * sgn[c] * b[c * 4 + r];
        for (int r = 0; r < 3; r++) g[12 + gOfB[r]] = sgn[r] * b[12 + r];
        return g;
    }

    // ---------------------------------------------------------------- the importer's bind pose

    /// <summary>Per bone node: its BIND matrix in its armature's space, as Blender's importer picks it (pick_bind_pose +
    /// calc_bone_matrices, guess_original_bind_pose on - the default): a bone's local bind is translation and rotation
    /// ONLY (scale dropped); it is the node's own, unless the bone AND its parent both have an inverse bind matrix
    /// (the dummy root and a skin's skeleton node count as having the identity), in which case it is
    /// inverseBind(parent) x inverseBind(bone)^-1, decomposed. The chain starts at identity under the armature.
    /// Null for a node that is not a bone.</summary>
    static double[][] BindArmatureMatrices(HafModel m, BlenderNames.Result names)
    {
        const int RootKey = -1;
        var invBinds = new Dictionary<int, double[]> { [RootKey] = HafTransforms.Identity };
        foreach (var sk in m.Skins)
        {
            if (sk.InverseBindMatrices == null) continue;
            int skel = sk.Skeleton;
            if (skel >= 0)
            {
                if (sk.Joints.Contains(skel)) skel = names.BoneParent[skel];
                if (!invBinds.ContainsKey(skel)) invBinds[skel] = HafTransforms.Identity;
            }
            for (int j = 0; j < sk.Joints.Length; j++) { var ibm = new double[16]; Array.Copy(sk.InverseBindMatrices, j * 16, ibm, 0, 16); invBinds[sk.Joints[j]] = ibm; }
        }
        var bind = new double[m.Nodes.Count][]; var done = new bool[m.Nodes.Count];
        double[] Visit(int node)
        {
            if (done[node]) return bind[node];
            done[node] = true;
            if (!names.IsBone[node]) return bind[node] = null;
            var n = m.Nodes[node];
            double[] local = TranslationRotation(n.HasMatrix ? n.Matrix : HafTransforms.Trs(n));
            int parent = names.BoneParent[node];
            if (invBinds.TryGetValue(node, out var own) && invBinds.TryGetValue(parent, out var parentInv))
            {
                var inv = HafTransforms.Invert(own);
                if (inv != null) local = TranslationRotation(HafTransforms.Mul(parentInv, inv));
            }
            double[] parentBind = parent >= 0 && names.IsBone[parent] ? Visit(parent) : HafTransforms.Identity;
            return bind[node] = HafTransforms.Mul(parentBind ?? HafTransforms.Identity, local);
        }
        for (int i = 0; i < m.Nodes.Count; i++) Visit(i);
        return bind;
    }

    /// <summary>A matrix with its scale dropped: Blender's `decompose()` translation and rotation, recomposed (the axes
    /// normalized; a mirroring 3x3 is negated first, as mat4_to_loc_rot_size does).</summary>
    static double[] TranslationRotation(double[] mtx)
    {
        var r = new double[16]; r[15] = 1; r[12] = mtx[12]; r[13] = mtx[13]; r[14] = mtx[14];
        double sign = HafTransforms.Determinant3(mtx) < 0 ? -1 : 1;
        for (int c = 0; c < 3; c++)
        {
            double x = mtx[c * 4], y = mtx[c * 4 + 1], z = mtx[c * 4 + 2], len = Math.Sqrt(x * x + y * y + z * z);
            if (len < 1e-30) { r[c * 4 + c] = 1; continue; }
            r[c * 4] = sign * x / len; r[c * 4 + 1] = sign * y / len; r[c * 4 + 2] = sign * z / len;
        }
        return r;
    }

    // ---------------------------------------------------------------- rig_report

    static void RigReport(HafModel m, BlenderNames.Result names, double[][] armaWorld, double[][] bindArma, Result r)
    {
        // the FIRST armature's bones - `arms[0]`, the first in creation order, which need not be skin 0's (the two_armatures
        // fixture: Rig2 sits at a lower node index than skin 0's Rig1 and Blender reports BoneB) - EVERY bone of it, whichever
        // skin's joints they are (two skins can share an armature)
        int firstArmature = names.ArmaturesInOrder[0].node;
        var boneNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < m.Nodes.Count; i++) if (names.IsBone[i] && names.ArmatureNodeOfBone[i] == firstArmature && names.BoneOfJoint.TryGetValue(i, out var b)) boneNames.Add(b);
        var stats = new Dictionary<string, (int count, double[] mn, double[] mx)>(); var order = new List<string>();
        long total = 0, weighted = 0;
        foreach (var (node, _) in names.MeshObjectsInOrder)
        {
            int skin = m.Nodes[node].Skin;
            if (skin < 0 || skin >= m.Skins.Count) continue;
            var sk = m.Skins[skin];
            if (!sk.Joints.Any(j => names.BoneOfJoint.TryGetValue(j, out var bn) && boneNames.Contains(bn))) continue;   // no vertex group of the first armature's bones
            // skin_into_bind_pose: joint matrix = (the bone's bind matrix in armature space) x (its inverse bind matrix)
            var jointMats = new double[sk.Joints.Length][];
            for (int j = 0; j < sk.Joints.Length; j++)
            {
                double[] ibm = HafTransforms.Identity;
                if (sk.InverseBindMatrices != null) { ibm = new double[16]; Array.Copy(sk.InverseBindMatrices, j * 16, ibm, 0, 16); }
                jointMats[j] = HafTransforms.Mul(bindArma[sk.Joints[j]] ?? HafTransforms.Identity, ibm);
            }
            foreach (var p in m.Meshes[m.Nodes[node].Mesh].Primitives)
            {
                if (!p.Skinned) { total += Used(p).Length; continue; }
                foreach (int v in Used(p))
                {
                    total++;
                    string best = null;
                    for (int set = 0; set < 2 && best == null; set++)
                    {
                        var joints = set == 0 ? p.Joints : p.Joints1; var weights = set == 0 ? p.Weights : p.Weights1;
                        if (joints == null || weights == null) continue;
                        for (int k = 0; k < 4; k++)
                            if (weights[v * 4 + k] > 0.5f && joints[v * 4 + k] < sk.Joints.Length && names.BoneOfJoint.TryGetValue(sk.Joints[joints[v * 4 + k]], out var bn) && boneNames.Contains(bn)) { best = bn; break; }
                    }
                    if (best == null) continue;
                    weighted++;
                    // `mw @ v.co`: v.co is the vertex skinned into the importer's BIND pose, in the armature's space (the weighted
                    // joint matrices above; weights normalized by their sum, a zero sum = all on the first joint), and mw the
                    // armature object's world matrix. Two files taught this: combine_soldier, whose root node carries a quarter
                    // turn the inverse bind matrices do not (Y and Z came out swapped from the raw vertices), and drone_clean,
                    // whose armature carries a 0.01 scale they DO account for (the raw vertices through it came out 100x small)
                    var acc = new double[16]; double wsum = 0;
                    for (int set = 0; set < 2; set++)
                    {
                        var js = set == 0 ? p.Joints : p.Joints1; var ws = set == 0 ? p.Weights : p.Weights1;
                        if (js == null || ws == null) continue;
                        for (int k = 0; k < 4; k++)
                        {
                            double w = ws[v * 4 + k]; int jj = js[v * 4 + k];
                            if (w == 0 || jj >= jointMats.Length) continue;
                            for (int e = 0; e < 16; e++) acc[e] += w * jointMats[jj][e];
                            wsum += w;
                        }
                    }
                    if (wsum == 0) { int j0 = p.Joints[v * 4]; acc = (double[])jointMats[j0 < jointMats.Length ? j0 : 0].Clone(); wsum = 1; }
                    for (int e = 0; e < 16; e++) acc[e] /= wsum;
                    var co = HafTransforms.Apply(acc, p.Positions[v * 3], p.Positions[v * 3 + 1], p.Positions[v * 3 + 2], 1.0);
                    var q = HafTransforms.Apply(armaWorld[skin], co[0], co[1], co[2], 1.0);
                    double x = q[0], y = -q[2], z = q[1];   // Blender frame
                    if (!stats.TryGetValue(best, out var st)) { st = (0, new[] { x, y, z }, new[] { x, y, z }); order.Add(best); }
                    st.count++;
                    st.mn[0] = Math.Min(st.mn[0], x); st.mn[1] = Math.Min(st.mn[1], y); st.mn[2] = Math.Min(st.mn[2], z);
                    st.mx[0] = Math.Max(st.mx[0], x); st.mx[1] = Math.Max(st.mx[1], y); st.mx[2] = Math.Max(st.mx[2], z);
                    stats[best] = st;
                }
            }
        }
        if (stats.Count == 0 || total == 0 || weighted < total * 0.9) return;   // partially/un-skinned: not fast-path material
        r.Notes.Add($"rigged source: armature '{r.Armature}', {stats.Count} bones carry weights, {weighted}/{total} verts weighted");
        foreach (var bn in order)
        {
            var (count, mn, mx) = stats[bn];
            r.RigBones.Add(new RigBone { Name = bn, Count = count, Center = new[] { (mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2 }, Size = new[] { mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2] } });
        }
    }
}
