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
// that matches it (the script's rule, kept); the separated parts' VERTEX ORDER is the one Blender's edge walk gives
// (VehicleProbe.Islands.cs). The visibility verdict (field 6) is computed as the script computes it - escape rays
// over one BVH, VehicleProbe.Visibility.cs - and the drill compares it. The inside-out verdict (field 8) comes in
// its own step; until then every part "keeps as authored" and the drill does not compare that field.
// Proof: tools/vehicle_probe_drill.sh runs Blender's probe and this on every registry source and compares the rows.
using System;
using System.Collections.Generic;
using System.Linq;

public static partial class VehicleProbe
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
        public float[] BlenderMatrix;       // matrix_world as Blender holds it (float32, row-major, Blender's frame) - the verdicts read it
        public bool UnderBone;              // the node hangs under a joint: Blender parents the object to the BONE, whose pose chain is not modelled yet
        public float[] FirstVertex;         // vertex 0 as the verdicts see it: world position (3) and normalized world normal (3), Blender's frame
        public int Source;                  // 0 the first model, 1 the second (its name carries the Lab's B_ prefix)
        public bool Placed;                 // a per-part placement moved it (its box is the placed one)
        public PartMesh Mesh;               // the vertices and faces the verdicts read - the Lab draws its preview from them
        public string Row => string.Join("|", "PART", Name, Verts.ToString(), F(Center), F(Size), Vis.ToString(), Bone, Flip.ToString());
        /// <summary>The drill's bit-for-bit check of the matrix against Blender's own: 16 floats, row-major, shortest round-trip form;
        /// a part under a bone says `under-bone` after them and is counted, not held (docs/Review-Backlog.md).</summary>
        public string MatrixRow => "MATRIX|" + Name + "|" + string.Join(" ", BlenderMatrix.Select(v => ((double)v).ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + (UnderBone ? "|under-bone" : "");
        /// <summary>The drill's bit-for-bit check of the whole chain - matrix, skinning, custom normals - on one vertex: `matrix_world @
        /// co` and `(matrix_world.to_3x3() @ normal).normalized()` of vertex 0, as Blender's probe computes them.</summary>
        public string VertexRow => "VERTEX|" + Name + "|" + string.Join(" ", FirstVertex.Take(3).Select(v => ((double)v).ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "|" + string.Join(" ", FirstVertex.Skip(3).Select(v => ((double)v).ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + (UnderBone ? "|under-bone" : "");
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

    /// <summary>A Blender-frame vector (x, y, z) back in glTF's frame (x, z, -y): exact.</summary>
    static double[] GltfOf(float[] b) => new double[] { b[0], b[2], -b[1] };

    static string F(double[] v) => string.Join(",", v.Select(x => x.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>The probe of one source model, in the pose Blender's import shows: the first animation's start (the
    /// parity reference since PR #109), or the static transforms when there is none.</summary>
    public static Result Run(HafModel m) => Run(new Input { Model = m });

    /// <summary>One model in the probe's scene: the first, or the second merged in with its placement baked into its meshes.</summary>
    sealed class Source
    {
        public HafModel M; public BlenderNames.Result Names; public int Index;   // 0 the first model, 1 the second
        public float[][] BWorld, BLocal, BArma;   // Blender's float32 matrices (column-major): per node its matrix_world as imported and its local, per skin the armature object's
        public double[][] World, ArmaWorld;       // the double chain the boxes read
        public float[] Bake;                      // the second model's T2 (row-major item layout), null for the first
        public Dictionary<int, float[]> Target = new Dictionary<int, float[]>();   // second model: per mesh node, T2 @ matrix_world baked into its vertices
        public Dictionary<int, NodeGeometry> Geo = new Dictionary<int, NodeGeometry>();
        public Dictionary<int, BlenderSkinner> Skinners = new Dictionary<int, BlenderSkinner>();
        public HashSet<int> Detached = new HashSet<int>();   // nodes a placement detached from their parent (their matrix no longer follows it)
    }

    /// <summary>A part before its mesh is built: the vertices it is made of, the matrix the verdicts read it through.</summary>
    sealed class PartSpec
    {
        public Source Src; public int Node, Prim = -1; public int[] Verts; public string Name; public bool IslandCopy;   // IslandCopy: a loose part other than the first - a new object, no children of its own
        public float[] Override;   // matrix_world after a placement touched it (row-major item layout), else null = the source's matrix
        public bool MatrixChanged; // a detached ancestor made this node's world matrix change
        public Part Part;
        public bool Skinned => Src.M.Nodes[Node].Skin >= 0 && Src.M.Nodes[Node].Skin < Src.M.Skins.Count && Src.M.Meshes[Src.M.Nodes[Node].Mesh].Primitives.Any(p => p.Skinned);
    }

    public static Result Run(Input input)
    {
        var r = new Result();
        foreach (var w in input.Warnings) r.Notes.Add("WARN: " + w);
        input.Progress?.Invoke("naming");
        var A = MakeSource(input.Model, 0, null, posed: true);
        var objsA = MeshObjects(A, r, "");
        var pool = A.Names.ObjectPool;   // every object name in the scene, as Blender's name map holds them
        foreach (var shape in A.Names.BoneShapes) pool.Remove(shape);   // imp() purges the importer's bone shapes (42-vertex icospheres) with the artefacts: their names are free again before anything else is named
        Source B = null; var objsB = new List<(int node, string name)>();
        if (input.Second != null)
        {
            // ---- the second model: imported into the same scene (its names made unique against the first model's), its bone
            //      shapes purged, every object prefixed "B_", each mesh's target matrix baked into its vertices, its helpers dropped
            input.Progress?.Invoke("second model");
            B = MakeSource(input.Second, 1, A.Names, posed: false);
            pool = B.Names.ObjectPool;
            objsB = MeshObjects(B, r, " (second model)");
            foreach (var shape in B.Names.BoneShapes) pool.Remove(shape);   // purged before the prefix, like the first model's
            var created = B.Names.ObjectsInOrder.Where(pool.Contains).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();   // bpy.data.objects: sorted by name
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var old in created) { pool.Remove(old); renamed[old] = pool.Unique("B_" + old); }
            for (int i = 0; i < objsB.Count; i++) objsB[i] = (objsB[i].node, renamed[objsB[i].name]);
            B.Bake = Merge2Matrix(input.SecondOffset, input.SecondRotation, input.SecondScale);
            if (input.SecondScaleBad.Count > 0) r.Notes.Add("WARN: second-model scale component(s) " + string.Join(", ", input.SecondScaleBad.Select(t => "'" + t + "'")) + " not positive - using 1.0 there");
            var meshNames = new HashSet<string>(objsB.Select(o => o.name), StringComparer.Ordinal);
            int helpers = 0;
            foreach (var old in created) if (!meshNames.Contains(renamed[old])) { pool.Remove(renamed[old]); helpers++; }
            float[] bmn = null, bmx = null;
            foreach (var (node, name) in objsB)
            {
                int skin = B.M.Nodes[node].Skin; bool skinned = skin >= 0 && skin < B.M.Skins.Count && B.M.Meshes[B.M.Nodes[node].Mesh].Primitives.Any(p => p.Skinned);
                B.Target[node] = MatMulMathutils(B.Bake, ToRowMajor(skinned ? B.BArma[skin] : B.BWorld[node]));
                var g = GeometryOfSecond(B, node);   // the transformed vertices: the box and the rays read these
                B.Geo[node] = g;
                LocalBox(B.M, node, g, null, null, out var mn, out var mx);
                if (bmn == null) { bmn = mn; bmx = mx; } else for (int i = 0; i < 3; i++) { bmn[i] = Math.Min(bmn[i], mn[i]); bmx[i] = Math.Max(bmx[i], mx[i]); }
            }
            if (helpers > 0) r.Notes.Add($"MERGE: {helpers} helper object(s) of the second model dropped (empties/armatures — placement baked into the meshes)");
            r.Notes.Add($"MERGE: second model '{input.SecondFile}' -> {objsB.Count} part(s) prefixed B_ | offset ({input.SecondOffsetText}) rot ({input.SecondRotationText}) scale ({G5(input.SecondScale[0])}, {G5(input.SecondScale[1])}, {G5(input.SecondScale[2])})");
            if (bmn != null) r.Notes.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "MERGE: B bbox min ({0:0.00}, {1:0.00}, {2:0.00}) max ({3:0.00}, {4:0.00}, {5:0.00})", bmn[0], bmn[1], bmn[2], bmx[0], bmx[1], bmx[2]));
        }
        r.Armature = A.Names.ArmaturesInOrder.Count > 0 ? A.Names.ArmaturesInOrder[0].name : null;   // the first CREATED, which is what `arms[0]` is - not skin 0's; the second model's armatures are dropped before rig_report

        // ---- the loose-part split, per source: a source with ONE mesh object is separated into its islands, named after it in
        //      the pool of every object the scene holds by then (the second model's B_ names included), less the bone shapes
        var namePool = pool.Clone();
        foreach (var shape in A.Names.BoneShapes) namePool.Remove(shape);
        var specs = new List<PartSpec>(); var extras = new List<PartSpec>();
        foreach (var (src, objs, tag) in new[] { (A, objsA, ""), (B, objsB, "second-model ") })
        {
            if (src == null) continue;
            if (objs.Count == 1)
            {
                var (node, name) = objs[0];
                var islands = BlenderIslands(src.M.Meshes[src.M.Nodes[node].Mesh]);
                r.Split = true;
                r.Notes.Add($"single {tag}mesh split into {islands.Count} loose parts (names are synthetic)");
                // each new object is a copy of the first, asking for ITS name: the smallest number its base has free
                // (Hull.005 beside an empty Hull.002 splits into Hull.005, Hull.001, Hull.003, Hull.004 - measured); the copies
                // are linked after every object the scene already has - after the second model's, when there is one
                for (int k = 0; k < islands.Count; k++)
                    (k == 0 ? specs : extras).Add(new PartSpec { Src = src, Node = node, Prim = islands[k].Prim, Verts = islands[k].Verts, Name = k == 0 ? name : namePool.Unique(name), IslandCopy = k > 0 });
            }
            else foreach (var (node, name) in objs) specs.Add(new PartSpec { Src = src, Node = node, Name = name });
        }
        specs.AddRange(extras);

        // ---- the parts: name, vertex count, world box, dominant bone - the box through the double chain (tolerance in the drill)
        input.Progress?.Invoke("parts");
        var boneNamesA = new HashSet<string>(A.Names.BoneOfJoint.Values, StringComparer.Ordinal);   // _bone_names: the bones of the armatures left in the scene - the first model's
        foreach (var s in specs)
        {
            if (!s.Src.Geo.TryGetValue(s.Node, out var g)) s.Src.Geo[s.Node] = g = Geometry(s.Src.M, s.Node, s.Src.World, s.Src.ArmaWorld);   // ONCE per node: 3,350 islands each re-posing the whole mesh took 18 s on the Ehrhardt
            s.Part = MakePart(s.Src.M, s.Node, s.Name, g, s.Prim, s.Verts, s.Src.Names, s.Src.Index == 1 ? boneNamesA : null);
            s.Part.Source = s.Src.Index;
            r.Parts.Add(s.Part);
        }

        // ---- the placements: the part (and its direct children) detached keeping its world matrix, then T onto matrix_world
        input.Progress?.Invoke("placements");
        var byName = new Dictionary<string, PartSpec>(StringComparer.Ordinal);
        var byNode = new Dictionary<(Source, int), PartSpec>();   // the object a node became (an island copy is a new object: not it)
        foreach (var s in specs) { if (!byName.ContainsKey(s.Name)) byName[s.Name] = s; if (!s.IslandCopy) byNode[(s.Src, s.Node)] = s; }
        foreach (var pl in input.Placements)
        {
            if (!byName.TryGetValue(pl.Name, out var s)) s = null;
            if (s == null) { r.Notes.Add($"WARN: placement for '{pl.Name}' skipped — no such part after the split (re-Probe, then place it again)"); continue; }
            var mw = ApplyMat4(MatrixOf(s));   // rel.matrix_world = mw.copy() after rel.parent = None: the assignment's round trip
            s.Src.Detached.Add(s.Node);
            if (!s.IslandCopy && s.Src.Index == 0) DetachChildren(s.Src, s.Node, byNode);   // a second-model mesh has no children left: the merge unparented every mesh and dropped the helpers
            LocalBox(s.Src.M, s.Node, s.Src.Geo[s.Node], s.Prim, s.Verts, out var lmn, out var lmx);
            WorldBox(mw, lmn, lmx, out var c0, out var s0);
            s.Override = PlacementMatrix(mw, c0, pl.Offset, pl.Scale);
            WorldBox(s.Override, lmn, lmx, out var c1, out var s1);
            s.Part.Center = new double[] { c1[0], c1[1], c1[2] }; s.Part.Size = new double[] { s1[0], s1[1], s1[2] }; s.Part.Placed = true;
            r.Notes.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "PLACED (probe): {0} | offset ({1}) scale ({2}) | centre ({3:0.000},{4:0.000},{5:0.000}) -> ({6:0.000},{7:0.000},{8:0.000}) | size ({9:0.000},{10:0.000},{11:0.000}) -> ({12:0.000},{13:0.000},{14:0.000})",
                s.Name, string.Join(",", pl.Offset.Select(v => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))), string.Join(",", pl.Scale.Select(v => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))),
                c0[0], c0[1], c0[2], c1[0], c1[1], c1[2], s0[0], s0[1], s0[2], s1[0], s1[1], s1[2]));
        }

        // Detaching a child can remove shear; descendants follow the rebuilt matrix. Their PART boxes must follow too,
        // even when they have no placement of their own (the initial double-chain boxes predate the detach).
        foreach (var s in specs.Where(s => s.Override != null || s.MatrixChanged))
        {
            LocalBox(s.Src.M, s.Node, s.Src.Geo[s.Node], s.Prim, s.Verts, out var mn, out var mx);
            WorldBox(MatrixOf(s), mn, mx, out var center, out var size);
            s.Part.Center = center.Select(v => (double)v).ToArray();
            s.Part.Size = size.Select(v => (double)v).ToArray();
        }

        // ---- the visibility verdict: every part's mesh DATA in world space (the importer's bind pose for a skinned part - `matrix_world
        //      @ v.co`, not the posed vertices the box is read from; the second model's vertices as its bake left them), one BVH,
        //      escape rays; then the inside-out verdict in the Lab's orientation
        if (specs.Count > 0)
        {
            var partMeshes = new List<PartMesh>(); var mats = new List<float[]>();   // per part: Blender's matrix_world (the armature's for a skinned part; identity for a baked one)
            float[] orient = input.ProbeRotation != null ? ProbeOrientMatrix(input.ProbeRotation) : null;
            foreach (var s in specs)
            {
                var m = s.Src.M; var mesh = m.Meshes[m.Nodes[s.Node].Mesh]; int skin = m.Nodes[s.Node].Skin; bool skinned = s.Skinned;
                Func<HafPrimitive, int, double[]> position = (p, v) => new double[] { p.Positions[v * 3], p.Positions[v * 3 + 1], p.Positions[v * 3 + 2] };
                Func<HafPrimitive, int, double[]> normal = (p, v) => p.Normals == null ? null : new double[] { p.Normals[v * 3], p.Normals[v * 3 + 1], p.Normals[v * 3 + 2] };
                if (skinned)
                {
                    // the mesh data as the importer stores it: positions and normals skinned into the bind pose in numpy float32, with
                    // the joint matrices mathutils gave it (VehicleProbe.BlenderSkin.cs) - a zero-area triangle's verdict hangs on these
                    // bits (3 skinned parts of 14,023 read differently from the double chain; external review of PR #115 had first
                    // found the normals left unskinned)
                    var sknr = SkinnerOf(s.Src, skin);
                    position = (p, v) => p.Skinned ? GltfOf(sknr.Position(p, v)) : new double[] { p.Positions[v * 3], p.Positions[v * 3 + 1], p.Positions[v * 3 + 2] };
                    normal = (p, v) => p.Normals == null ? null : p.Skinned ? GltfOf(sknr.Normal(p, v)) : new double[] { p.Normals[v * 3], p.Normals[v * 3 + 1], p.Normals[v * 3 + 2] };
                }
                var mb = MatrixOf(s);
                var pm = BuildPartMesh(m, s.Node, s.Prim, s.Verts, position, normal, mb, s.Src.Index == 1 ? s.Src.Target[s.Node] : null);
                pm.Source = s.Src.Index;
                partMeshes.Add(pm); mats.Add(orient == null ? mb : MatMulMathutils(orient, mb));
                s.Part.BlenderMatrix = mb; s.Part.Mesh = pm;
                if (pm.Count > 0) s.Part.FirstVertex = new[] { pm.World[0], pm.World[1], pm.World[2], pm.Normal[0], pm.Normal[1], pm.Normal[2] };
                // under a bone when ANY ancestor is one: the importer parents the first object below a joint to the BONE, and
                // the objects below that to it in turn (the dug-out canoe's cloth hangs two plain nodes under a joint)
                var names = s.Src.Names;
                bool underBone = names.IsBone[s.Node];   // a mesh ON a joint becomes a child object under that bone
                for (int a = m.Nodes[s.Node].Parent; a >= 0 && !underBone; a = m.Nodes[a].Parent) underBone = names.IsBone[a];
                s.Part.UnderBone = !skinned && underBone;
            }
            input.Progress?.Invoke("visibility");
            Visibility(r.Parts, partMeshes);
            r.Notes.Add($"visibility: {r.Parts.Count(p => p.Vis == 1)} external / {r.Parts.Count(p => p.Vis != 1)} interior part(s)");
            input.Progress?.Invoke("inside-out");
            InsideOut(r.Parts, partMeshes, mats);
            r.Notes.Add($"inside-out verdicts: {r.Parts.Count(p => p.Flip > 0)} part(s) hold interior-facing islands (the fix would reverse them)");
        }

        // ---- rig_report: the first armature's bones, from the undeformed vertices (v.co = the bind pose). The first model's
        //      armatures only - the second model's are dropped by the merge - but a second-model mesh whose vertex groups carry
        //      the first armature's bone NAMES counts in Blender (rig_report reads every mesh object's groups by name), and does here
        input.Progress?.Invoke("rig report");
        if (A.Names.ArmaturesInOrder.Count > 0) RigReport(A.M, A.Names, A.ArmaWorld, BindArmatureMatrices(A.M, A.Names), r, B, objsB);
        return r;
    }

    /// <summary>A model's matrices as Blender holds them after import: the first model posed at its first clip's start (the parity
    /// reference), the second NOT posed - the merge bakes its matrices before the probe poses anything, at Blender's untouched
    /// import state, which for an animated file is a blend no file states (docs/Review-Backlog.md); the static transforms here.</summary>
    static Source MakeSource(HafModel m, int index, BlenderNames.Result seed, bool posed)
    {
        var s = new Source { M = m, Index = index, Names = BlenderNames.Compute(m, seed) };
        var pose = posed && m.Animations.Count > 0 ? HafTransforms.PoseAt(m, 0, 0.0) : null;
        // a node given by a MATRIX is, in Blender, that matrix decomposed to translation, rotation and scale and recomposed
        // (the importer's get_node_trs): whatever shear it holds is gone. The Lab's own rah66.glb has such a node (axes 0.13
        // degrees off square) and four parts' boxes read 0.15 % differently through the matrix as given.
        s.World = HafTransforms.WorldMatrices(m, i => pose?.Invoke(i) ?? (m.Nodes[i].HasMatrix ? AsBlenderDecomposes(m.Nodes[i].Matrix) : UnitRotation(m.Nodes[i])));
        s.ArmaWorld = new double[m.Skins.Count][];
        for (int si = 0; si < m.Skins.Count; si++) s.ArmaWorld[si] = s.Names.ArmatureNodeOfSkin[si] >= 0 ? s.World[s.Names.ArmatureNodeOfSkin[si]] : HafTransforms.Identity;
        // the same matrices as Blender holds them: float32, composed as Blender composes them (the verdicts read these; a
        // mesh parented to a BONE is not modelled yet - its matrix is the node chain's, said in docs/Review-Backlog.md)
        s.BWorld = BlenderWorldMatrices(m, posed && m.Animations.Count > 0 ? HafTransforms.PoseTrsAt(m, 0, 0.0) : null, out s.BLocal);
        s.BArma = new float[m.Skins.Count][];
        for (int si = 0; si < m.Skins.Count; si++) s.BArma[si] = s.Names.ArmatureNodeOfSkin[si] >= 0 ? s.BWorld[s.Names.ArmatureNodeOfSkin[si]] : IdentityF();
        return s;
    }

    /// <summary>mesh_objects() of one source after the importer's bone-shape purge: the mesh objects with vertices, in Blender's
    /// creation order, each one object per node (a mesh two nodes share is two objects). A purged artefact frees its name.</summary>
    static List<(int node, string name)> MeshObjects(Source s, Result r, string tag)
    {
        var objects = new List<(int node, string name)>();
        foreach (var (node, name) in s.Names.MeshObjectsInOrder)
        {
            var mesh = s.M.Meshes[s.M.Nodes[node].Mesh];
            if (mesh.Primitives.Sum(p => Used(p).Length) == 0) continue;            // mesh_objects(): len(vertices) > 0
            if (IsIcosphereArtifact(s.M, node, name)) { r.Notes.Add("purged glTF importer bone-shape artifact" + tag + ": " + name); s.Names.ObjectPool.Remove(name); continue; }   // only what was PURGED frees its name: a mesh object without vertices is not listed, but still there
            objects.Add((node, name));
        }
        return objects;
    }

    static BlenderSkinner SkinnerOf(Source s, int skin)
    {
        if (!s.Skinners.TryGetValue(skin, out var skinner)) s.Skinners[skin] = skinner = BlenderSkinner.Build(s.M, skin, s.Names);
        return skinner;
    }

    /// <summary>The matrix_world a part's verdicts read (row-major item layout): what a placement left it, else the node's
    /// (the armature object's for a skinned part; identity for a second-model part, whose placement is in its vertices).</summary>
    static float[] MatrixOf(PartSpec s)
    {
        if (s.Override != null) return s.Override;
        if (s.Src.Index == 1) return IdentityRow();
        return ToRowMajor(s.Skinned ? s.Src.BArma[s.Src.M.Nodes[s.Node].Skin] : s.Src.BWorld[s.Node]);
    }

    /// <summary>`for rel in [o] + list(o.children): mw = rel.matrix_world.copy(); rel.parent = None; rel.matrix_world = mw` for the
    /// children: every object Blender parents to this node's object is detached with its world matrix through the assignment's
    /// round trip, and what hangs below each child is recomputed down the chain (BKE_object_where_is_calc: parent @ local).</summary>
    static void DetachChildren(Source src, int node, Dictionary<(Source, int), PartSpec> byNode)
    {
        var names = src.Names; var m = src.M;
        for (int c = 0; c < m.Nodes.Count; c++)
        {
            if (names.ObjectParentNode[c] != node || src.Detached.Contains(c)) continue;
            byNode.TryGetValue((src, c), out var spec);
            float[] now = spec != null ? MatrixOf(spec) : ToRowMajor(src.BWorld[c]);
            var kept = ApplyMat4(now);
            src.BWorld[c] = FromRowMajor(kept);
            if (spec != null) spec.Override = kept;
            src.Detached.Add(c);
            Recompute(src, c, byNode);
        }
    }

    /// <summary>The objects below `node` take parent @ local again (mul_m4_m4m4) after their parent's matrix changed; a detached one does not.</summary>
    static void Recompute(Source src, int node, Dictionary<(Source, int), PartSpec> byNode)
    {
        var m = src.M;
        for (int d = 0; d < m.Nodes.Count; d++)
        {
            if (src.Names.ObjectParentNode[d] != node || src.Detached.Contains(d) || src.Names.IsBone[d]) continue;
            src.BWorld[d] = MulM4(src.BWorld[node], src.BLocal[d]);
            if (byNode.TryGetValue((src, d), out var spec)) { spec.Override = null; spec.MatrixChanged = true; }   // reads the source's matrix again, and its box follows (the detach may have removed a shear)
            Recompute(src, d, byNode);
        }
    }

    static float[] FromRowMajor(float[] it) { var bm = new float[16]; for (int c = 0; c < 4; c++) for (int w = 0; w < 4; w++) bm[c * 4 + w] = it[w * 4 + c]; return bm; }

    /// <summary>A second-model mesh as its bake leaves it: every vertex (the importer's bind pose for a skinned one) through the
    /// node's target matrix in float32, kept in glTF's frame as doubles (exact) for the box; the object at identity.</summary>
    static NodeGeometry GeometryOfSecond(Source B, int node)
    {
        var m = B.M; var mesh = m.Meshes[m.Nodes[node].Mesh]; int skin = m.Nodes[node].Skin;
        bool skinned = skin >= 0 && skin < m.Skins.Count && mesh.Primitives.Any(p => p.Skinned);
        var target = B.Target[node];
        var g = new NodeGeometry { Skin = skin, Skinned = false, ObjWorld = HafTransforms.Identity, Local = new double[mesh.Primitives.Count][] };
        for (int pi = 0; pi < mesh.Primitives.Count; pi++)
        {
            var p = mesh.Primitives[pi];
            var local = new double[p.VertexCount * 3];
            var sknr = skinned && p.Skinned ? SkinnerOf(B, skin) : null;
            foreach (int v in Used(p))
            {
                float bx, by, bz;
                if (sknr != null) { var q = sknr.Position(p, v); bx = q[0]; by = q[1]; bz = q[2]; }
                else { bx = p.Positions[v * 3]; by = -p.Positions[v * 3 + 2]; bz = p.Positions[v * 3 + 1]; }
                TransformPoint(target, bx, by, bz, out float tx, out float ty, out float tz);
                local[v * 3] = tx; local[v * 3 + 1] = tz; local[v * 3 + 2] = -ty;   // back to glTF's frame: (x, z, -y) of Blender's
            }
            g.Local[pi] = local;
        }
        return g;
    }

    /// <summary>Blender's `bound_box` of a part: the float32 extremes of its vertices in the object's local space, Blender's
    /// frame - the file's positions for a static part (exact), the double chain's posed positions for a skinned one (close).</summary>
    static void LocalBox(HafModel m, int node, NodeGeometry g, int? onlyPrim, int[] onlyVerts, out float[] mn, out float[] mx)
    {
        var mesh = m.Meshes[m.Nodes[node].Mesh];
        mn = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity }; mx = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        for (int pi = 0; pi < mesh.Primitives.Count; pi++)
        {
            if (onlyPrim.HasValue && onlyPrim.Value >= 0 && pi != onlyPrim.Value) continue;
            var p = mesh.Primitives[pi]; var local = g.Local[pi];
            foreach (int v in onlyVerts ?? Used(p))
            {
                float x, y, z;
                if (local != null) { x = (float)local[v * 3]; y = (float)local[v * 3 + 1]; z = (float)local[v * 3 + 2]; }
                else { x = p.Positions[v * 3]; y = p.Positions[v * 3 + 1]; z = p.Positions[v * 3 + 2]; }
                float bx = x, by = -z, bz = y;
                mn[0] = Math.Min(mn[0], bx); mn[1] = Math.Min(mn[1], by); mn[2] = Math.Min(mn[2], bz);
                mx[0] = Math.Max(mx[0], bx); mx[1] = Math.Max(mx[1], by); mx[2] = Math.Max(mx[2], bz);
            }
        }
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

    /// <summary>A part: a node's whole mesh (onlyPrim -1), or one island of it (the vertices of one primitive). boneFilter: the
    /// bone names the dominant-bone tally may count (the second model's parts count only names the first model's armatures
    /// have - `_bone_names` is read off the armatures left in the scene), or null for the node's own armatures' bones.</summary>
    static Part MakePart(HafModel m, int node, string name, NodeGeometry g, int onlyPrim, int[] onlyVerts, BlenderNames.Result names, ISet<string> boneFilter)
    {
        var mesh = m.Meshes[m.Nodes[node].Mesh];
        int skin = g.Skin; bool skinned = skin >= 0 && skin < m.Skins.Count && mesh.Primitives.Any(p => p.Skinned);
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
                bool zeroWeights = skinned && p.Skinned && HasZeroWeights(p, v);
                if (skinned && p.Skinned)
                    for (int set = 0; set < 2; set++)
                    {
                        var joints = set == 0 ? p.Joints : p.Joints1; var weights = set == 0 ? p.Weights : p.Weights1;
                        if (joints == null || weights == null) continue;
                        for (int k = 0; k < 4; k++)
                        {
                            float w = zeroWeights && set == 0 && k == 0 ? 1f : weights[v * 4 + k];
                            if (w <= 0) continue;
                            int joint = joints[v * 4 + k];
                            if (joint >= m.Skins[skin].Joints.Length || !names.BoneOfJoint.TryGetValue(m.Skins[skin].Joints[joint], out var bone)) continue;
                            if (boneFilter != null && !boneFilter.Contains(bone)) continue;
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

    // ---------------------------------------------------------------- the importer's bind pose (skin_into_bind_pose)

    /// <summary>Per joint of a skin: (the bone's bind matrix in armature space) x (its inverse bind matrix).</summary>
    static double[][] BindJointMatrices(HafModel m, HafSkin sk, double[][] bindArma)
    {
        var jointMats = new double[sk.Joints.Length][];
        for (int j = 0; j < sk.Joints.Length; j++)
        {
            double[] ibm = HafTransforms.Identity;
            if (sk.InverseBindMatrices != null) { ibm = new double[16]; Array.Copy(sk.InverseBindMatrices, j * 16, ibm, 0, 16); }
            jointMats[j] = HafTransforms.Mul(bindArma[sk.Joints[j]] ?? HafTransforms.Identity, ibm);
        }
        return jointMats;
    }

    /// <summary>`v.co` of a skinned vertex: skinned into the importer's bind pose, in the armature's space.</summary>
    static double[] BindPosition(HafPrimitive p, int v, double[][] jointMats)
        => HafTransforms.Apply(BindMatrix(p, v, jointMats), p.Positions[v * 3], p.Positions[v * 3 + 1], p.Positions[v * 3 + 2], 1.0);

    // skin_into_bind_pose also updates the weights used for vertex groups: a zero sum gives the first influence 1.
    static bool HasZeroWeights(HafPrimitive p, int v)
    {
        if (!p.Skinned) return false;
        for (int k = 0; k < 4; k++)
            if (p.Weights[v * 4 + k] != 0f || (p.Weights1 != null && p.Weights1[v * 4 + k] != 0f)) return false;
        return true;
    }

    /// <summary>A skinned vertex's skinning matrix into the importer's bind pose: the weighted joint matrices, the weights
    /// normalized by their sum (a zero sum = all on the first influence). Positions go through it with w = 1, normals with
    /// w = 0 (skin_into_bind_pose: the 3x3, no translation).</summary>
    static double[] BindMatrix(HafPrimitive p, int v, double[][] jointMats)
    {
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
        return acc;
    }

    // ---------------------------------------------------------------- rig_report

    static void RigReport(HafModel m, BlenderNames.Result names, double[][] armaWorld, double[][] bindArma, Result r, Source second, List<(int node, string name)> secondObjects)
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
            var jointMats = BindJointMatrices(m, sk, bindArma);
            foreach (var p in m.Meshes[m.Nodes[node].Mesh].Primitives)
            {
                if (!p.Skinned) { total += Used(p).Length; continue; }
                foreach (int v in Used(p))
                {
                    total++;
                    string best = null;
                    bool zeroWeights = HasZeroWeights(p, v);
                    for (int set = 0; set < 2 && best == null; set++)
                    {
                        var joints = set == 0 ? p.Joints : p.Joints1; var weights = set == 0 ? p.Weights : p.Weights1;
                        if (joints == null || weights == null) continue;
                        for (int k = 0; k < 4; k++)
                            if ((zeroWeights && set == 0 && k == 0 ? 1f : weights[v * 4 + k]) > 0.5f && joints[v * 4 + k] < sk.Joints.Length && names.BoneOfJoint.TryGetValue(sk.Joints[joints[v * 4 + k]], out var bn) && boneNames.Contains(bn)) { best = bn; break; }
                    }
                    if (best == null) continue;
                    weighted++;
                    // `mw @ v.co`: v.co is the vertex skinned into the importer's BIND pose, in the armature's space (the weighted
                    // joint matrices above; weights normalized by their sum, a zero sum = all on the first joint), and mw the
                    // armature object's world matrix. Two files taught this: combine_soldier, whose root node carries a quarter
                    // turn the inverse bind matrices do not (Y and Z came out swapped from the raw vertices), and drone_clean,
                    // whose armature carries a 0.01 scale they DO account for (the raw vertices through it came out 100x small)
                    var co = BindPosition(p, v, jointMats);
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
        // the second model's meshes, by vertex-group NAME: `gidx = {g.index: g.name for g in o.vertex_groups if g.name in bone_names}` -
        // a group named like one of the first armature's bones counts, with the vertex where the bake left it (matrix_world identity)
        if (second != null)
            foreach (var (node, _) in secondObjects)
            {
                var m2 = second.M; int skin = m2.Nodes[node].Skin;
                if (skin < 0 || skin >= m2.Skins.Count) continue;
                var sk = m2.Skins[skin];
                if (!sk.Joints.Any(j => second.Names.BoneOfJoint.TryGetValue(j, out var bn) && boneNames.Contains(bn))) continue;
                var mesh2 = m2.Meshes[m2.Nodes[node].Mesh];
                for (int pi = 0; pi < mesh2.Primitives.Count; pi++)
                {
                    var p = mesh2.Primitives[pi];
                    if (!p.Skinned) { total += Used(p).Length; continue; }
                    var local = second.Geo[node].Local[pi];
                    foreach (int v in Used(p))
                    {
                        total++;
                        string best = null;
                        bool zeroWeights = HasZeroWeights(p, v);
                        for (int set = 0; set < 2 && best == null; set++)
                        {
                            var joints = set == 0 ? p.Joints : p.Joints1; var weights = set == 0 ? p.Weights : p.Weights1;
                            if (joints == null || weights == null) continue;
                            for (int k = 0; k < 4; k++)
                                if ((zeroWeights && set == 0 && k == 0 ? 1f : weights[v * 4 + k]) > 0.5f && joints[v * 4 + k] < sk.Joints.Length && second.Names.BoneOfJoint.TryGetValue(sk.Joints[joints[v * 4 + k]], out var bn) && boneNames.Contains(bn)) { best = bn; break; }
                        }
                        if (best == null) continue;
                        weighted++;
                        double x = local[v * 3], y = -local[v * 3 + 2], z = local[v * 3 + 1];   // Blender frame, through the identity matrix_world
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
