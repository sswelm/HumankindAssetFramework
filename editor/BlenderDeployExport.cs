// BlenderDeployExport.cs - the GLB deploy_convert.py exports, part 8a (2026-10-11): the script's three steps before its
// `export_scene.gltf` (the trim, the purge, the sanitize) and the STRUCTURE of the file Blender's glTF exporter then
// writes of the scene the conversion left - which nodes in which order with which transform, the skin with its joints and
// inverse bind matrices, each mesh's primitives (vertices, normals, UVs, colours, joints, weights, indices - the bytes),
// which material each primitive names and the animations' names. Read at Blender 5.1.2 (io_scene_gltf2/blender/exp/
// export.py, gather.py, tree.py, nodes.py, joints.py, skins.py, exporter.py, primitive_extract.py):
//   * the scene after the purge is the armature with every bound mesh under it; the exporter's tree takes the scene's
//     roots (the armature) and an object's children in `bpy.data.objects` order (BlenderDeploy.IdNameCmp), then the
//     armature's root bones (`pose.bones` without a parent, the edit list's order after the recoil step) and a bone's
//     bone children in that order;
//   * the index order is the serializer's walk (exporter.py __traverse): a node's members in alphabetical order -
//     children, then mesh, then skin -, the node itself appended after them. So the first mesh node's skin reaches the
//     joints: each bone AFTER its bone children (post-order), the neutral bone after the last bone, then that mesh node;
//     every other mesh node follows; the armature last. The skin's joints are the bones depth-first, a parent before
//     its children (pre-order), the neutral bone last;
//   * a joint's matrix_world is armature.matrix_world @ bone.matrix_local @ the Z-up to Y-up basis change; its
//     transform decomposes parent.matrix_world.inverted_safe() @ matrix_world (the armature's for a root bone) WITHOUT
//     the normalization and the snapping object nodes get (VehicleProbe.ExporterTrs, joint); the inverse bind matrix is
//     (basis @ armature.matrix_world @ matrix_local).inverted_safe(), column by column;
//   * a skinned mesh node has no transform of its own (the skin carries it); a bound mesh without a triangle is a node
//     without a mesh and without a skin, its transform against the armature's;
//   * the neutral bone (tree.py add_neutral_bones): when a vertex of any bound mesh has no bone - its one vertex group
//     names a pair-merged PART no bone is named after - the exporter adds the joint "neutral_bone" (its transform the
//     basis change decomposed, its inverse bind matrix (basis @ armature.matrix_world).inverted_safe()) to the
//     armature's children and the skin's joints, once;
//   * a mesh's primitives are BlenderExport.MeshPrimitives over the mesh as the bind left it (the folded vertices, the
//     importer's faces, UV layers, colours, custom normals and sharp faces, ONE vertex group at weight 1) with the skin
//     (the object's and the armature's matrix_world as the scene holds them when the exporter runs: frame 0, where no
//     object but the armature has animation left); the exporter validates the mesh first (twin faces go);
//   * a skin is made once per armature (skins.py gather_skin is cached): every mesh node shares skin 0; when no mesh
//     node uses it (no bound mesh has a triangle) the skin is still written (tree.py get_unused_skins), without a
//     neutral bone;
//   * the materials are numbered at first use along the walk (a mesh's primitives in slot order); the animations are
//     `bpy.data.actions` - the deploy action and the role clips -, one glTF animation each (their contents: part 8b).
// What this holds is judged by tools/deploy_drill.sh against the GLB Blender writes (blender_export_dump.py): every row.
// NOT here yet (named): the materials' contents, the textures and images, the animations' channels, the bytes (8b, 8c).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public static class BlenderDeployExport
{
    public sealed class Node
    {
        public string Name;
        public int Parent = -1;
        public readonly List<int> Children = new List<int>();
        public int Mesh = -1, Skin = -1;
        public float[] Translation, Rotation, Scale;   // as written: null when left out
        public float[] World;                          // matrix_world, column-major (the exporter's, basis change included for a joint)
        public bool Joint, Neutral;
        public BlenderDeploy.Obj Object;               // the object, or null for a joint
    }

    public sealed class Skin
    {
        public string Name;
        public readonly List<int> Joints = new List<int>();
        public readonly List<float[]> InverseBind = new List<float[]>();   // per joint, 16 floats column by column as written
    }

    public sealed class Mesh
    {
        public string Name;                            // the datablock's name
        public BlenderDeploy.Obj Object;
        public List<BlenderExport.Primitive> Primitives;
        public readonly List<int> Material = new List<int>();   // per primitive: index into Result.Materials, or -1
        public List<(int material, bool vertexColor)> Slots;
    }

    public sealed class Result
    {
        public string Fallback;                        // why the export is Blender's, or null
        public readonly List<string> Log = new List<string>();   // the DEPLOY lines of the export step, as the script prints them (the "wrote" line excepted)
        public bool Trimmed; public int TrimStart; public long TrimEnd;
        public readonly List<string> Purged = new List<string>();
        public readonly List<string> GarbageBones = new List<string>();   // sorted as Python sorts them
        public int GarbageCurves;
        /// <summary>bpy.data.scenes as the exporter writes them, by name: the importer's context scene "Scene" (the glTF
        /// default scene's, or the first's), which holds the armature, and a scene per other glTF scene named after it -
        /// or "Scene &lt;index&gt;" - with Blender's numbering, which holds no root once the purge is through.</summary>
        public readonly List<(string name, List<int> roots)> Scenes = new List<(string, List<int>)>();
        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<Skin> Skins = new List<Skin>();
        public readonly List<Mesh> Meshes = new List<Mesh>();
        public readonly List<string> Materials = new List<string>();   // Blender material names, first use
        public readonly List<string> Animations = new List<string>();
        public bool NeutralBone, UnusedSkin;
    }

    static readonly float[] Basis = { 1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1 };   // row-major items: (x, y, z) -> (x, z, -y)

    static float[] ToColumnMajor(float[] rm) { var cm = new float[16]; for (int col = 0; col < 4; col++) for (int row = 0; row < 4; row++) cm[col * 4 + row] = rm[row * 4 + col]; return cm; }

    /// <summary>`'pose.bones["%s"]' % name` as Blender writes a data path (BLI_str_escape: the backslash, the quote and the C0
    /// controls escaped), then `data_path.split('"')[1]`: the escaped name up to its first quote.</summary>
    internal static string PathKey(string bone)
    {
        var b = new StringBuilder();
        foreach (char c in bone)
        {
            if (c == '"') break;   // the split ends the key at the first quote, escaped or not (`\"` splits at the quote)
            if (c == '\\') b.Append("\\\\");
            else if (c == '\n') b.Append("\\n"); else if (c == '\r') b.Append("\\r"); else if (c == '\t') b.Append("\\t");
            else if (c == '\a') b.Append("\\a"); else if (c == '\b') b.Append("\\b"); else if (c == '\f') b.Append("\\f"); else if (c == '\v') b.Append("\\v");
            else b.Append(c);
        }
        if (bone.IndexOf('"') >= 0) b.Append('\\');   // the backslash that escaped the quote stays on the key's side of the split
        return b.ToString();
    }

    /// <summary>The export of a conversion whose Finish ran. `args` are the script's arguments after its two paths.</summary>
    public static Result Build(HafModel m, BlenderNames.Result names, BlenderDeploy.Result r, string[] args)
    {
        if (r.Objects7 == null) throw new InvalidOperationException("the export comes after Finish");
        var x = new Result();
        if (r.RoleFallback != null) { x.Fallback = "the role clips are Blender's (" + r.RoleFallback + ")"; return x; }
        // a material read with two different sets of UV indices is written twice by the exporter (materials.py
        // get_final_material: a copy per set of indices its textures resolve to on each mesh) - named and left, as the prep does
        var uvProblems = new List<string>(); BlenderExportTree.MaterialUvProblems(m, uvProblems);
        if (uvProblems.Count > 0) { x.Fallback = uvProblems[0]; return x; }
        int argc = args.Length + 2;
        string Arg(int scriptIndex) => scriptIndex - 2 < args.Length ? args[scriptIndex - 2] : null;

        // ---- 8. the trim: scene.frame_start, scene.frame_end = int(argv[2]), recoil_out_end or int(argv[3]) - through the
        //      RNA setters (rna_scene.cc rna_Scene_start_frame_set / rna_Scene_end_frame_set: each value clamped to
        //      MINFRAME..MAXFRAME, the other end pulled along when crossed), as the script's own `scene.frame_start,
        //      scene.frame_end = fmin, fmax` set them first; the line prints what the scene holds afterwards
        const int MINFRAME = 0, MAXFRAME = 1048574;
        int sfra = 1, efra = 250;   // the startup scene's
        void SetStart(int v) { v = Math.Max(MINFRAME, Math.Min(MAXFRAME, v)); sfra = v; if (v > efra) efra = Math.Min(v, MAXFRAME); }
        void SetEnd(int v) { v = Math.Max(MINFRAME, Math.Min(MAXFRAME, v)); efra = v; if (sfra > v) sfra = Math.Max(v, MINFRAME); }
        SetStart(r.FrameMin); SetEnd(r.FrameMax);
        if (argc >= 4)
        {
            if (!BlenderDeploy.PyIntParse(Arg(2), out int start)) { x.Fallback = $"a trim start the script cannot read ('{Arg(2)}': int() fails before the export)"; return x; }
            long end;
            if (r.Recoil != null && !r.ExitAtRecoil) end = r.Recoil.OutEnd;
            else { if (!BlenderDeploy.PyIntParse(Arg(3), out int e)) { x.Fallback = $"a trim end the script cannot read ('{Arg(3)}': int() fails before the export)"; return x; } end = e; }
            if (end > int.MaxValue || end < int.MinValue) { x.Fallback = $"a trim end past the 32-bit range ({end}: the RNA setter raises)"; return x; }
            SetStart(start); SetEnd((int)end);
            x.Trimmed = true; x.TrimStart = sfra; x.TrimEnd = efra;
            x.Log.Add($"DEPLOY trim to frames {sfra}..{efra}");
        }

        // ---- the purge: everything but the meshes and the armature goes
        var arm = r.Objects7.FirstOrDefault(o => o.Type == "ARMATURE" && o.Name == r.Armature.Name);
        if (arm == null) throw new InvalidOperationException("the armature is not among the objects after the purge");
        foreach (var o in r.Objects7) if (o.Type != "MESH" && o != arm) x.Purged.Add(o.Name);
        if (x.Purged.Count > 0) x.Log.Add($"DEPLOY purged {x.Purged.Count} leftover source object(s) (empties/lights/cameras/locators/groups)");

        // ---- the sanitize: a bone with a key that is not finite or beyond 1e6 in ANY action loses every curve of its in every action
        bool Bad(float v) => float.IsNaN(v) || float.IsInfinity(v) || Math.Abs((double)v) > 1e6;
        // the deploy action's curves: a re-keyed bone's Bezier channels (the retarget, the leg scale, the recoil arm), else
        // the bake's keys per channel (location 3, quaternion 4, scale 3 - the scale curves stripped on the contract path)
        int channels = r.ScaleKeys ? 10 : 7;
        var deployCurves = new List<(string bone, IEnumerable<float> values)>();
        foreach (string bone in r.Keys.Keys.Concat(r.Rekeyed.Keys).Distinct())
        {
            if (r.Rekeyed.TryGetValue(bone, out var rk)) { foreach (var list in rk) if (list != null && list.Count > 0) deployCurves.Add((bone, list.Select(k => k.Value))); }
            else { var keys = r.Keys[bone]; for (int c = 0; c < channels; c++) { int at = c; deployCurves.Add((bone, keys.Select(k => k[at]))); } }
        }
        var curves = new List<(string bone, IEnumerable<float> values)>(deployCurves);
        foreach (var role in r.Roles)
            foreach (var kv in role.Curves)
                foreach (var list in kv.Value) if (list != null && list.Count > 0) curves.Add((kv.Key, list.Select(k => k.Value)));
        // the script keys the garbage by `data_path.split('"')[1]`: the bone's name as Blender escapes it in the path (a quote
        // as \", a backslash as \\), cut at its first quote - two names that share such a prefix share one key, and the key is
        // what the list prints (the review of part 8a)
        var garbageKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (bone, values) in curves) if (values.Any(Bad)) garbageKeys.Add(PathKey(bone));
        var garbage = new HashSet<string>(StringComparer.Ordinal);   // the bones whose curves go: every bone whose key is a garbage key
        foreach (var (bone, _) in curves) if (garbageKeys.Contains(PathKey(bone))) garbage.Add(bone);
        if (garbageKeys.Count > 0)
        {
            foreach (var (bone, _) in curves) if (garbage.Contains(bone)) x.GarbageCurves++;
            x.GarbageBones.AddRange(garbageKeys.OrderBy(s => s, BlenderDeploy.CodePointOrder));
        }
        x.Log.Add($"DEPLOY sanitized: {garbage.Count} garbage bone(s) de-animated ({x.GarbageCurves} curves) — rest-pose ride: "
                  + (garbage.Count > 0 ? "[" + string.Join(", ", x.GarbageBones.Select(BlenderDeploy.PyRepr)) + "]" : "none"));

        // ---- the bones as the exporter sees them: matrix_world with the basis change, the inverse bind matrix
        var bones = r.BonesAfterRecoil;
        var byName = bones.ToDictionary(b => b.Name, b => b, StringComparer.Ordinal);
        var preorder = r.BoneOrder;
        var armRM = VehicleProbe.ToRowMajor(arm.World);
        var boneWorld = new Dictionary<string, float[]>(StringComparer.Ordinal); var boneIbm = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var b in bones)
        {
            var ml = VehicleProbe.ToRowMajor(b.MatrixLocal);
            boneWorld[b.Name] = ToColumnMajor(VehicleProbe.MatMulMathutils(VehicleProbe.MatMulMathutils(armRM, ml), Basis));
            boneIbm[b.Name] = ToColumnMajor(VehicleProbe.InvertedSafe(VehicleProbe.MatMulMathutils(Basis, VehicleProbe.MatMulMathutils(armRM, ml))));
        }
        List<BlenderDeploy.Bone> KidsOf(BlenderDeploy.Bone parent) => preorder.Select(n => byName[n]).Where(b => b.Parent == parent).ToList();

        // ---- the meshes: each bound mesh's primitives, with its skin
        var boundOf = r.Bound.ToDictionary(b => b.Mesh.Name, b => b, StringComparer.Ordinal);
        var meshObjs = r.Objects7.Where(o => o.Type == "MESH").ToList();
        var prims = new Dictionary<BlenderDeploy.Obj, List<BlenderExport.Primitive>>();
        var slotsOf = new Dictionary<BlenderDeploy.Obj, List<(int, bool)>>();
        bool needNeutral = false;
        foreach (var o in meshObjs)
        {
            if (!boundOf.TryGetValue(o.Name, out var bd)) throw new InvalidOperationException($"the mesh '{o.Name}' was not bound");
            if (o.Parent != arm) { x.Fallback = $"a mesh that is not under the armature at the export ('{o.Name}')"; return x; }
            var src = BlenderReduce.Reduce(m, o.MeshNode, 1f, names, 0, null, mergeUvs: false);
            if (src.Fallback != null) { x.Fallback = $"'{o.Name}': {src.Fallback}"; return x; }
            if (src.Positions.Length != bd.Positions.Length) throw new InvalidOperationException($"'{o.Name}': the bind holds {bd.Positions.Length / 3} vertices, the mesh {src.Positions.Length / 3}");
            int nv = src.VertexCount;
            var mesh = new BlenderReduce.Result
            {
                VertexCount = nv, FaceCount = src.FaceCount, Positions = bd.Positions, Faces = src.Faces, Edges = src.Edges, FaceMaterial = src.FaceMaterial, FaceSharp = src.FaceSharp,
                Uv = src.Uv, CustomNormal = bd.CustomNormal, Colors = src.Colors, Slots = src.Slots, SlotAlpha = src.SlotAlpha, Ratio = 1f, Collapsed = false,
                DefNr = new List<int>[nv], DefWeight = new List<float>[nv],
            };
            // vg.add(range(len(vertices)), 1.0, 'REPLACE'): the one group, every vertex at 1
            for (int v = 0; v < nv; v++) { mesh.DefNr[v] = new List<int> { 0 }; mesh.DefWeight[v] = new List<float> { 1f }; }
            int joint = preorder.IndexOf(bd.Group);   // the bone the group names, or none (a pair-merged part: the neutral bone)
            var skin = new BlenderExport.Skin { ObjectWorld = o.World, ArmatureWorld = arm.World, GroupJoint = new[] { joint }, JointCount = preorder.Count };
            var p = BlenderExport.MeshPrimitives(mesh, skin);
            prims[o] = p; slotsOf[o] = src.Slots;
            // need_neutral_bone is decided over EVERY bound mesh's vertices (primitive_extract.py prepare_data, before any face is
            // looked at): a mesh without a triangle counts too - read in the source, no fixture reaches it (a pair-merged line mesh)
            if (p.Count > 0) { if (p[0].NeutralBone) needNeutral = true; }
            else if (nv > 0) { BlenderExport.VertexBones(mesh, skin, out _, out _, out bool neutral); if (neutral) needNeutral = true; }
        }
        // ... and the joint is added only to a mesh node that has a skin (tree.py add_neutral_bones): none when no mesh has a triangle
        if (!meshObjs.Any(o => prims[o].Count > 0)) needNeutral = false;
        x.NeutralBone = needNeutral;

        // ---- the serializer's walk
        var boneIndex = new Dictionary<string, int>(StringComparer.Ordinal); int neutralIndex = -1;
        int Add(Node n) { x.Nodes.Add(n); return x.Nodes.Count - 1; }
        int VisitBone(BlenderDeploy.Bone b)
        {
            if (boneIndex.TryGetValue(b.Name, out int done)) return done;
            var n = new Node { Name = b.Name, Joint = true, World = boneWorld[b.Name] };
            foreach (var c in KidsOf(b)) n.Children.Add(VisitBone(c));
            int idx = Add(n); boneIndex[b.Name] = idx;
            foreach (int c in n.Children) x.Nodes[c].Parent = idx;
            return idx;
        }
        int VisitNeutral()
        {
            if (neutralIndex >= 0) return neutralIndex;
            var n = new Node { Name = "neutral_bone", Neutral = true };
            VehicleProbe.NeutralBone(arm.World, out n.Translation, out n.Rotation, out n.Scale, out _);
            neutralIndex = Add(n);
            return neutralIndex;
        }
        var roots = KidsOf(null);
        Skin MakeSkin(bool neutral)
        {
            var s = new Skin { Name = arm.Name };
            foreach (string bn in preorder) { s.Joints.Add(boneIndex[bn]); s.InverseBind.Add(boneIbm[bn]); }
            if (neutral) { VehicleProbe.NeutralBone(arm.World, out _, out _, out _, out var ibm); s.Joints.Add(neutralIndex); s.InverseBind.Add(ibm); }
            return s;
        }
        var armChildren = new List<int>();
        foreach (var o in meshObjs)
        {
            var n = new Node { Name = o.Name, Object = o, World = o.World };
            var bd = boundOf[o.Name]; var p = prims[o];
            if (p.Count > 0)
            {
                var mesh = new Mesh { Name = bd.DataName, Object = o, Primitives = p, Slots = slotsOf[o] };
                foreach (var prim in p)
                {
                    var (mat, vc) = mesh.Slots[prim.MaterialSlot];
                    if (mat < 0 && !vc) { mesh.Material.Add(-1); continue; }
                    string matName = names.MaterialOf[(mat < 0 ? names.MeshDatablockNode[o.MeshNode] : -1, mat, vc)];
                    int mi = x.Materials.IndexOf(matName);
                    if (mi < 0) { mi = x.Materials.Count; x.Materials.Add(matName); }
                    mesh.Material.Add(mi);
                }
                n.Mesh = x.Meshes.Count; x.Meshes.Add(mesh);
                // the skin: its joints are reached here first - every bone after its children, the neutral bone last
                foreach (var b in roots) VisitBone(b);
                if (needNeutral) VisitNeutral();
                if (x.Skins.Count == 0) x.Skins.Add(MakeSkin(needNeutral));
                n.Skin = 0;
            }
            else VehicleProbe.ExporterTrs(arm.World, o.World, out n.Translation, out n.Rotation, out n.Scale);
            armChildren.Add(Add(n));
        }
        foreach (var b in roots) armChildren.Add(VisitBone(b));
        if (needNeutral && x.Skins.Count > 0) armChildren.Add(VisitNeutral());
        var armNode = new Node { Name = arm.Name, Object = arm, World = arm.World };
        VehicleProbe.ExporterTrs(null, arm.World, out armNode.Translation, out armNode.Rotation, out armNode.Scale);
        armNode.Children.AddRange(armChildren);
        int armIdx = Add(armNode);
        foreach (int c in armChildren) x.Nodes[c].Parent = armIdx;
        // the scenes (vnode.py: a new Blender scene for every glTF scene but the default one - the first without one -,
        // named `scene.name or "Scene %d"`; the exporter writes every bpy.data.scenes, by name): the context scene holds
        // the armature, the others hold no root after the purge (their meshes hang from the armature, their empties went)
        {
            var pool = new BlenderNames.NamePool(); pool.Unique("Scene");
            var scenes = new List<(string name, List<int> roots)> { ("Scene", new List<int> { armIdx }) };
            int def = m.HasDefaultScene ? m.Scene : 0;
            for (int i = 0; i < m.Scenes.Count; i++)
                if (i != def) scenes.Add((pool.Unique(string.IsNullOrEmpty(m.Scenes[i].Name) ? "Scene " + i : m.Scenes[i].Name), new List<int>()));
            BlenderExportTree.StableSort(scenes, (a, b) => BlenderDeploy.IdNameCmp(a.name, b.name));
            x.Scenes.AddRange(scenes);
        }
        // a joint's transform (joints.py): against its parent joint's matrix_world, or the armature's for a root bone -
        // decomposed without the normalization and the snapping, its float noise written
        foreach (var n in x.Nodes)
            if (n.Joint)
            {
                var parent = x.Nodes[n.Parent];
                VehicleProbe.ExporterTrs(parent.Joint ? parent.World : arm.World, n.World, out n.Translation, out n.Rotation, out n.Scale, joint: true);
            }
        // a skin no node uses is written all the same (tree.py get_unused_skins) - but only when NO kept mesh object carries
        // the armature modifier (every bound mesh does, a triangle or not: with meshes there is no skin at all), and only
        // from the vtree of the LAST scene the exporter gathered (export.py takes `export_settings['vtree']`, overwritten
        // per scene): the context scene's armature is found when that scene sorts last by name. Measured 2026-10-11:
        // no mesh + "Zoo" -> no skin, + "Alpha" -> the skin; a line mesh alone -> no skin
        if (x.Skins.Count == 0 && meshObjs.Count == 0 && x.Scenes[x.Scenes.Count - 1].roots.Count > 0) { x.Skins.Add(MakeSkin(false)); x.UnusedSkin = true; }

        // ---- the animations: bpy.data.actions, one glTF animation per action with a curve left whose frames reach the
        //      scene's range (action.py: the action's range - int(first key)..int(last key) over its curves - is cut to
        //      scene.frame_start..frame_end with export_frame_range; the sampler keys `while frame <= end`, and an action
        //      without a keyframe, or without a channel - a role keyed with no frame, "folded" at a wheel count below zero;
        //      an action whose every curve the sanitize took -, is not appended). Measured: a deploy from frame 5 drops
        //      "deployed" and "folded" (keyed at fmin and fmin + 1). Names only here; 8b holds their contents
        var animated = new List<string>();
        bool Reaches(IEnumerable<float> frames)
        {
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity; bool any = false;
            foreach (float f in frames) { any = true; if (f < lo) lo = f; if (f > hi) hi = f; }
            return any && Math.Max(sfra, (int)lo) <= Math.Min(efra, (int)hi);
        }
        var deployFrames = new List<float>();
        foreach (string bone in r.Keys.Keys.Concat(r.Rekeyed.Keys).Distinct())
        {
            if (garbage.Contains(bone)) continue;
            if (r.Rekeyed.TryGetValue(bone, out var rk)) { foreach (var list in rk) if (list != null && list.Count > 0) deployFrames.AddRange(list.Select(k => k.Frame)); }
            else { deployFrames.Add(r.BakeFrameMin); deployFrames.Add(r.BakeFrameMax); }
        }
        if (deployFrames.Count == 0 && garbage.Count > 0) { x.Fallback = "the sanitize took every curve of the deploy action (its range without curves is not modelled)"; return x; }
        if (Reaches(deployFrames)) animated.AddRange(r.Actions7);
        foreach (var role in r.Roles)
            if (Reaches(role.Curves.Where(kv => !garbage.Contains(kv.Key)).SelectMany(kv => kv.Value.Where(list => list != null)).SelectMany(list => list.Select(k => k.Frame)))) animated.Add(role.Name);
        // sorted by `action.name.lower()` (action.py): the names here are the script's, lower case already
        x.Animations.AddRange(animated.OrderBy(s => s, StringComparer.Ordinal));
        return x;
    }
}
