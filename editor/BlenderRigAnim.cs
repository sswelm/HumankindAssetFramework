// BlenderRigAnim.cs - rig_anim.py without Blender, stages 1 and 2 (step 4 of replacing Blender, steps (b) and (c),
// 2026-10-12): what the script's first stage leaves in Blender's scene - the arguments as it parses them, the import, the
// material-less drop, the armature lookup (and the exit without one), the contract marker, the object-animation clear -
// and what its second stage makes of that (RunStage2: the bone-parent -> skin bind, the wrapper flatten), row for row
// and bit for bit as tools/riganim_drill.sh's dump records it (tools/riganim-drill/blender_riganim_dump.py: the RIGANIM
// lines, the scene frame, every object with its matrices, every mesh's geometry and weights hashed, the bones at rest,
// the pose at frame 1, every action's fcurves with their keys and handles). Built from the deploy and prep ports -
// BlenderNames, BlenderPosedState, VehicleProbe.BlenderWorld/Pose/Armature/Skin, BlenderReduce at a ratio of 1,
// BlenderEigen - and these rules of the importer (io_scene_gltf2/blender/imp, Blender 5.1.2) read for stage 1:
//   * mesh.py: a mesh gets material slots only when a primitive has a material or COLOR_0 (one slot per distinct Blender
//     material in order of first use, one EMPTY slot shared by the primitives without one); with neither there are no
//     slots at all, and the script drops the object (`len(o.data.materials) == 0`) - its children become scene roots and
//     keep their local transform (matrix_parent_inverse is the identity after an import). A vertex group per joint of
//     the skin, in joint order, the empty ones included; every non-zero weight added with REPLACE, set by set, slot by
//     slot (BlenderReduce's DefNr/DefWeight); the modifier is named "Armature".
//   * animation.py, animation_node.py, animation_utils.py: ONE action per glTF animation, named by its track name
//     (BlenderNames.TrackNames); a slot per object the animation touches - "OB" + the object's name, a bone's curves on
//     its armature's slot - in the order the nodes are met (node index order, a node's channels in channel order), the
//     fcurves in that same order, three or four per channel (`location`, `rotation_quaternion`, `scale`, under
//     `pose.bones["<name>"].` for a bone; a second channel on one node and path is refused). A key's handles are what
//     fcurve.update() leaves: deduplicate, then BKE_fcurve_handles_recalc - which returns before a lone key and leaves
//     its handles at the zeros keyframe_points.add gave it; interpolation LINEAR or CONSTANT, easing AUTO, both handle
//     types AUTO_CLAMPED. Every action is stashed on an NLA track; the FIRST animation's action is then restored as the
//     active one on every object it has a slot for (restore_animation_on_object), the others keep animation_data
//     without an action - which the script's object-animation clear counts all the same (`animation_data is not None`).
//   * node.py / vnode.py: an object under a bone has parent_type BONE and the bone's armature as its parent; a skinned
//     mesh hangs from its armature with the identity transform (move_skinned_meshes).
//   * bpy.data.actions lists actions sorted by name (id_sort_by_name: BLI_strcasecmp, a stable insertion); scene.objects
//     holds objects in creation order; the scene stays at frames 1..250, frame 1 (read_factory_settings).
// What this does not model is NAMED (Stage1.Fallback), and such a file is Blender's to prepare: a .fbx or .blend source,
// localNodeAnim (the REST hold and the round-trip sampling), several scenes, cameras, lights, mesh instancing,
// KHR_materials_variants, KHR_animation_pointer, a CUBICSPLINE sampler, morph weights, a sampler without keys or with the
// wrong component count, a skin whose joints are under no armature or that lists a joint twice, a mesh placed on a bone
// node, every mesh BlenderReduce declines, and (stage 2) a bone-hung mesh whose datablock another object shares. Held to
// Blender by tools/riganim_drill.sh (--stages 2), every row of both stages.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

public static class BlenderRigAnim
{
    // Python's str.strip() whitespace (the ASCII information separators included, which .NET's Trim leaves alone)
    static readonly char[] PyWhitespace = new[] { 0x9, 0xa, 0xb, 0xc, 0xd, 0x1c, 0x1d, 0x1e, 0x1f, 0x20, 0x85, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000 }.Select(c => (char)c).ToArray();
    static string PyStrip(string s) => s.Trim(PyWhitespace);

    /// <summary>The script's arguments as it parses them (`argv = sys.argv[sys.argv.index("--") + 1:]`): argv[0] the input,
    /// argv[1] the output, argv[2] the target, then the optional ones to argv[14]. Logs holds the RIGANIM lines the parsing
    /// prints, in order (the conversion path, the roles, the sockets, the warnings).</summary>
    public sealed class Args
    {
        public string Input = "", Output = ""; public long Target;
        public List<string> Prefixes = new List<string>();
        public string ClipName = "", AlbedoOut = "";
        public bool KeepMaterials;
        public double[] RigRot = { 0.0, 0.0, 0.0 };
        public bool ConvertRig;
        public List<(string role, string clip)> RoleSpecs = new List<(string, string)>();
        public bool KeepTranslations;
        public List<string> StaticParts = new List<string>();
        public bool LocalNodeAnim;
        public List<(string donor, string parent, double[] offset)> SocketSpecs = new List<(string, string, double[])>();
        public readonly List<string> Logs = new List<string>();
        public string Problem;   // the script dies on its arguments (int() of the target), or null
    }

    /// <summary>float(s) as Python reads a string: whitespace around, a sign, digits with single underscores between them, a
    /// point, an exponent, or inf/infinity/nan in any case. Digits of other scripts (which Python accepts) are not read.</summary>
    static bool PyFloat(string s, out double v)
    {
        v = 0.0;
        string t = PyStrip(s).ToLowerInvariant();
        if (t == "inf" || t == "+inf" || t == "infinity" || t == "+infinity") { v = double.PositiveInfinity; return true; }
        if (t == "-inf" || t == "-infinity") { v = double.NegativeInfinity; return true; }
        if (t == "nan" || t == "+nan" || t == "-nan") { v = double.NaN; return true; }
        if (!Regex.IsMatch(t, @"^[+-]?([0-9](_?[0-9])*(\.([0-9](_?[0-9])*)?)?|\.[0-9](_?[0-9])*)(e[+-]?[0-9](_?[0-9])*)?$")) return false;
        return double.TryParse(t.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>int(s) as Python reads a string (ASCII digits).</summary>
    static bool PyInt(string s, out long v)
    {
        v = 0;
        string t = PyStrip(s);
        if (!Regex.IsMatch(t, @"^[+-]?[0-9](_?[0-9])*$")) return false;
        return long.TryParse(t.Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);
    }

    public static Args ParseArgs(IList<string> argv)
    {
        var a = new Args();
        string At(int i) => i < argv.Count ? argv[i] : "";
        bool Has(int i) => argv.Count > i;
        a.Input = At(0); a.Output = At(1);
        if (!PyInt(At(2), out a.Target)) a.Problem = "the script dies on int(argv[2]): '" + At(2) + "'";
        a.Prefixes = At(3).Split(',').Select(PyStrip).Where(p => p.Length > 0).ToList();
        a.ClipName = PyStrip(At(4)); a.AlbedoOut = At(5);
        a.KeepMaterials = Has(6) && PyStrip(At(6)) == "1";
        if (Has(7) && PyStrip(At(7)).Length > 0)
        {
            var parts = At(7).Split(','); var vals = new List<double>(); bool ok = true;
            foreach (var p in parts) { if (!PyFloat(p, out double v)) { ok = false; break; } vals.Add(v); }
            if (ok) a.RigRot = vals.Take(3).Concat(Enumerable.Repeat(0.0, Math.Max(0, 3 - parts.Length))).ToArray();
            else a.Logs.Add("RIGANIM WARN: bad rotation arg '" + At(7) + "' — ignoring");
        }
        string c8 = PyStrip(At(8));
        a.ConvertRig = Has(8) && (c8 == "0" || c8 == "1") ? c8 == "1" : a.RigRot.Any(v => Math.Abs(v) > 1e-4);
        a.Logs.Add("RIGANIM conversion path: " + (a.ConvertRig ? "ON (raw-rig convert)" : "off (legacy byte-identical)"));
        a.KeepTranslations = Has(12) && PyStrip(At(12)) == "1";
        // `.lower()` on ASCII is .NET's; this stage reads the list for nothing but the record
        a.StaticParts = At(13).Split(',').Select(PyStrip).Where(p => p.Length > 0).Select(p => p.ToLowerInvariant()).ToList();
        a.LocalNodeAnim = Has(14) && PyStrip(At(14)) == "1";
        if (Has(9) && PyStrip(At(9)).Length > 0)
            foreach (var raw in At(9).Split(';'))
            {
                var pair = PyStrip(raw); int eq = pair.IndexOf('=');
                if (pair.Length == 0 || eq < 0) continue;
                string r = PyStrip(pair.Substring(0, eq)), cn = PyStrip(pair.Substring(eq + 1));
                if (r.Length > 0 && cn.Length > 0) a.RoleSpecs.Add((r, cn));
            }
        if (a.RoleSpecs.Count > 0) a.Logs.Add("RIGANIM state roles: " + string.Join(", ", a.RoleSpecs.Select(rc => rc.role + "='" + rc.clip + "'")));
        // (the stale role outputs beside the output file are removed here by the script - a file, not a scene state: the
        // Factory wiring's concern, nothing in the dump)
        if (Has(11) && PyStrip(At(11)).Length > 0)
            foreach (var raw in At(11).Split(';'))
            {
                var pair = PyStrip(raw); int eq = pair.IndexOf('=');
                if (pair.Length == 0 || eq < 0) continue;
                string dn = pair.Substring(0, eq), rest = pair.Substring(eq + 1), ps;
                double[] off = { 0.0, 0.0, 0.0 };
                int at = rest.IndexOf('@');
                if (at >= 0)
                {
                    ps = rest.Substring(0, at); string os = rest.Substring(at + 1);
                    var vals = new List<double>(); bool ok = true;
                    foreach (var p in os.Split(',')) { if (!PyFloat(p, out double v)) { ok = false; break; } vals.Add(v); }
                    if (ok) off = vals.Concat(new[] { 0.0, 0.0, 0.0 }).Take(3).ToArray();
                    else a.Logs.Add("RIGANIM WARN: bad socket offset '" + os + "' — using 0,0,0");
                }
                else ps = rest;
                if (PyStrip(dn).Length > 0 && PyStrip(ps).Length > 0) a.SocketSpecs.Add((PyStrip(dn), PyStrip(ps), off));
            }
        if (a.SocketSpecs.Count > 0) a.Logs.Add("RIGANIM donor sockets requested: " + string.Join(", ", a.SocketSpecs.Select(s => s.donor + "->" + s.parent)));
        return a;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // the scene as Blender holds it after stage 1 - what the later stages continue from

    public sealed class SceneObject
    {
        public string Name, Type;                 // MESH, EMPTY, ARMATURE
        public SceneObject Parent;                // null for a scene root (a dropped parent leaves its children roots)
        public string ParentBone;                 // parent_type BONE: the bone's name, the parent the armature
        public int ParentBoneNode = -1;
        public string DataName;                   // the mesh or armature datablock, null for an empty
        public string Action, Slot;               // the active action and its slot identifier, or null
        public bool HasAnimationData;             // animation_data is not None (an NLA track from any animation)
        public float[] World, Local;              // column-major float32, as VehicleProbe holds matrices: matrix_world as the
                                                  // original object holds it (the last evaluation), and BKE_object_to_mat4 of
                                                  // the object's own location, rotation and scale
        public bool Dirty;                        // tagged for the next depsgraph update: World is stale against Local (a parent
                                                  // changed under it, or a matrix_world assigned, with no view_layer.update since)
        public BlenderNames.BlenderObject Source;
        public MeshData Mesh;                     // a mesh object's data
    }

    public sealed class MeshData
    {
        public int Vertices; public int[] Faces;  // 3 per face
        public float[] Positions;                 // 3 per vertex, Blender's frame (skinned into the bind pose for a skinned mesh)
        public int[] FaceMaterial;                // material slot per face
        public float[] Uv;                        // the active UV layer, 2 per corner, or null
        public List<string> Materials = new List<string>();   // the slots: a Blender material's name, or null for an empty slot
        public List<string> Groups = new List<string>();      // the vertex groups, in order
        public List<int>[] DefNr; public List<float>[] DefWeight;   // per vertex: group index and weight, or null without groups
        public string ArmatureModifier;           // the armature object's name, or null
    }

    public sealed class KeyData
    {
        public float Frame, Value, LeftX, LeftY, RightX, RightY;
        public string Interpolation;              // LINEAR or CONSTANT
        public const string Easing = "AUTO", HandleType = "AUTO_CLAMPED";
    }

    public sealed class FCurveData { public string DataPath; public int Index; public List<KeyData> Keys = new List<KeyData>(); }

    public sealed class SlotData { public string Identifier; public SceneObject Object; public List<FCurveData> FCurves = new List<FCurveData>(); }

    public sealed class ActionData
    {
        public string Name; public float FrameStart, FrameEnd;
        public List<SlotData> Slots = new List<SlotData>();   // in creation order (the channelbags)
        public IEnumerable<FCurveData> FCurves => Slots.SelectMany(s => s.FCurves);
    }

    public sealed class ArmatureData
    {
        public SceneObject Object;
        public VehicleProbe.ArmatureResult Rest;
        public List<int> BoneNodes;                                   // `armature.bones` order: creation order, depth first
        public Dictionary<int, float[][]> PoseProps;                  // per bone node: the pose bone's location, rotation_quaternion, scale
        public Dictionary<int, float[]> PoseMats;                     // per bone node: pose_mat (armature space, column-major)
        public string PosePosition = "POSE";
    }

    public sealed class Stage1
    {
        public string Fallback;                                       // why this file is Blender's to prepare, or null
        public readonly List<string> Logs = new List<string>();       // the RIGANIM lines of stage 1, in order
        public readonly List<string> Logs2 = new List<string>();      // the RIGANIM lines of stage 2 (RunStage2), in order
        public bool Exits;                                            // the script's sys.exit(1) in this stage (after its ERROR line)
        public int FrameStart = 1, FrameEnd = 250, FrameCurrent = 1;
        public readonly List<SceneObject> Objects = new List<SceneObject>();   // scene.objects after the stage, in order
        public ArmatureData Armature;                                 // the armature the script takes (the first one), or null
        public readonly List<ActionData> Actions = new List<ActionData>();     // bpy.data.actions, in its order
        public readonly List<string> Notes = new List<string>();      // the branches this file took (the drill's census)
        public BlenderNames.Result Names; public VehicleProbe.ImportRig Rig;
    }

    static void Note(Stage1 s, string what) { if (!s.Notes.Contains(what)) s.Notes.Add(what); }

    /// <summary>Stage 1 of rig_anim.py on a glTF model: the scene the script's second stage starts from, or Fallback.</summary>
    public static Stage1 RunStage1(HafModel m, Args a)
    {
        var s = new Stage1();
        if (a.Problem != null) { s.Fallback = a.Problem; return s; }
        string ext = System.IO.Path.GetExtension(a.Input).ToLowerInvariant();
        if (ext == ".fbx" || ext == ".blend") { s.Fallback = "a " + ext + " source: Blender's own importer, until the native reader"; return s; }
        if (a.LocalNodeAnim) { s.Fallback = "localNodeAnim: the REST hold and the inspection round trip are Blender's"; return s; }
        s.Logs.AddRange(a.Logs);
        if (m.Scenes.Count > 1) { s.Fallback = "a file of several scenes: which one Blender makes active is not modelled"; return s; }
        foreach (var ext2 in new[] { "KHR_materials_variants", "KHR_animation_pointer", "KHR_lights_punctual", "EXT_mesh_gpu_instancing" })
            if (m.ExtensionsUsed.Contains(ext2)) { s.Fallback = ext2 + ": not modelled"; return s; }
        if (m.Nodes.Exists(n => n.Camera >= 0)) { s.Fallback = "a camera: the importer's camera correction is not modelled"; return s; }
        var names = BlenderNames.Compute(m); s.Names = names;
        // a skin's joints must be bones of an armature (the importer fails otherwise), each listed once (a vertex group per joint)
        for (int si = 0; si < m.Skins.Count; si++)
        {
            if (m.Skins[si].Joints.Length == 0) { s.Fallback = "a skin without joints"; return s; }
            if (m.Skins[si].Joints.Distinct().Count() != m.Skins[si].Joints.Length) { s.Fallback = "a skin that lists a joint twice (two vertex groups of one name)"; return s; }
        }
        foreach (var o in names.Objects)
        {
            if (o.Kind == BlenderNames.ObjectKind.Camera) { s.Fallback = "a camera object"; return s; }
            if (o.Kind == BlenderNames.ObjectKind.Mesh && o.Skin >= 0 && (o.Skin >= m.Skins.Count || names.ArmatureOfSkin[o.Skin] == null)) { s.Fallback = "'" + o.Name + "': a skin whose joints are under no armature"; return s; }
            if (o.GltfNode < 0 && o.ParentBone != null) { s.Fallback = "'" + o.Name + "': a mesh on a bone node (the importer's '.mesh' child under a bone)"; return s; }
        }

        // ---- the meshes as imported, and the material-less drop
        var dropped = new HashSet<BlenderNames.BlenderObject>();
        var meshOf = new Dictionary<BlenderNames.BlenderObject, MeshData>();
        foreach (var o in names.Objects)
        {
            if (o.Kind != BlenderNames.ObjectKind.Mesh) continue;
            var mesh = m.Meshes[m.Nodes[o.MeshNode].Mesh];
            string why = BlenderReduce.FallbackReason(m, o.MeshNode);
            if (why != null) { s.Fallback = "'" + o.Name + "': " + why; return s; }
            var r = BlenderReduce.Reduce(m, o.MeshNode, 1f, names, 0, null, mergeUvs: false);
            if (r.Fallback != null) { s.Fallback = "'" + o.Name + "': " + r.Fallback; return s; }
            var d = new MeshData { Vertices = r.VertexCount, Faces = r.Faces, Positions = r.Positions, FaceMaterial = r.FaceMaterial, Uv = r.Uv.Count > 0 ? r.Uv[0] : null, DefNr = r.DefNr, DefWeight = r.DefWeight };
            // mesh.py: slots only when a primitive has a material or COLOR_0; else none - and the script drops the object
            bool hasMaterials = mesh.Primitives.Exists(p => p.Material >= 0) || mesh.Primitives.Exists(p => p.Colors != null);
            if (hasMaterials)
                foreach (var (mat, vc) in r.Slots)
                {
                    if (mat < 0 && !vc) { d.Materials.Add(null); Note(s, "an empty material slot (a primitive without a material beside one with)"); }
                    else d.Materials.Add(names.MaterialOf[(mat < 0 ? names.MeshDatablockNode[o.MeshNode] : -1, mat, vc)]);
                    if (mat < 0 && vc) Note(s, "a COLOR_0 primitive without a material (DefaultMaterial, kept)");
                }
            if (d.Materials.Count == 0) { dropped.Add(o); Note(s, "a material-less mesh dropped"); continue; }
            if (r.DefNr != null)
            {
                foreach (int j in m.Skins[o.Skin].Joints) d.Groups.Add(names.BoneOfJoint[j]);
                d.ArmatureModifier = names.ArmatureOfSkin[o.Skin];
                if (Enumerable.Range(0, d.Groups.Count).Any(g => !Enumerable.Range(0, d.Vertices).Any(v => r.DefNr[v].Contains(g)))) Note(s, "a joint no vertex weights (an empty vertex group)");
            }
            if (d.Uv == null) Note(s, "a mesh without a UV layer");
            meshOf[o] = d;
        }

        // ---- the objects left, in creation order; the armature the script takes
        var byName = new Dictionary<string, SceneObject>(StringComparer.Ordinal);
        foreach (var o in names.Objects)
        {
            if (dropped.Contains(o)) continue;
            var so = new SceneObject { Name = o.Name, Source = o, DataName = o.DataName, ParentBone = o.ParentBone };
            so.Type = o.Kind == BlenderNames.ObjectKind.Mesh ? "MESH" : o.Kind == BlenderNames.ObjectKind.Armature ? "ARMATURE" : "EMPTY";
            if (o.Kind == BlenderNames.ObjectKind.Mesh) so.Mesh = meshOf[o];
            s.Objects.Add(so); byName[o.Name] = so;
        }
        foreach (var so in s.Objects)
        {
            var o = so.Source;
            if (o.Parent != null)
            {
                if (byName.TryGetValue(o.Parent, out var p)) so.Parent = p;
                else { so.Dirty = true; Note(s, "a dropped mesh's child, a scene root now (its matrix_world kept until the next update)"); }
            }
            if (so.ParentBone != null)
            {
                so.ParentBoneNode = names.BoneOfJoint.First(kv => kv.Value == so.ParentBone && names.ArmatureNodeOfBone[kv.Key] == so.Parent.Source.GltfNode).Key;
                Note(s, "an object parented to a bone");
            }
        }
        var armObj = s.Objects.FirstOrDefault(x => x.Type == "ARMATURE");
        if (armObj == null)
        {
            s.Logs.Add("RIGANIM ERROR: no armature found — the model is not rigged, use the normal (static) bake instead");
            s.Exits = true; Note(s, "no armature: the script's exit");
            return s;
        }
        if (armObj.Name.StartsWith("DeployArmV2", StringComparison.Ordinal)) { s.Logs.Add("RIGANIM contract: V2 (clean-unit deploy conversion)"); Note(s, "the V2 contract marker"); }
        if (s.Objects.Count(x => x.Type == "ARMATURE") > 1) Note(s, "several armatures (the first one is the script's)");

        // ---- the actions: one per animation, a slot per object touched, the fcurves in the order the importer makes them
        var rig = VehicleProbe.BuildImportRig(m, names); s.Rig = rig;
        var trackNames = BlenderNames.TrackNames(m);
        var imports = new List<BlenderPosedState.Action>();
        var slotObjectsOf = new List<List<SceneObject>>();   // per animation: the objects with a slot, in creation order
        SceneObject ObjectOfNode(int n)
        {
            string name = names.IsBone[n] ? names.ArmaturesInOrder.First(x => x.node == names.ArmatureNodeOfBone[n]).name : names.ObjectOfNode[n];
            return name != null && byName.TryGetValue(name, out var so) ? so : null;
        }
        for (int ai = 0; ai < m.Animations.Count; ai++)
        {
            var anim = m.Animations[ai];
            var imp = BlenderPosedState.Import(m, ai, 24.0, rig);
            if (imp.NotModelled != null) { s.Fallback = "animation " + ai + ": " + imp.NotModelled; return s; }
            imports.Add(imp);
            var act = new ActionData { Name = trackNames[ai], FrameStart = imp.FrameStart, FrameEnd = imp.FrameEnd };
            var slotOf = new Dictionary<SceneObject, SlotData>();
            var taken = new HashSet<(int, string)>();
            var slotObjects = new List<SceneObject>();
            // BlenderAnimation.anim: the nodes in index order, each node's channels in channel order (node.animations[anim_idx])
            for (int n = 0; n < m.Nodes.Count; n++)
                for (int ci = 0; ci < anim.Channels.Count; ci++)
                {
                    var ch = anim.Channels[ci];
                    if (ch.Node != n) continue;
                    if (ch.Path == "weights") { s.Fallback = "animation " + ai + ": morph weights (shape key curves) are not modelled"; return s; }
                    int comps = ch.Path == "translation" || ch.Path == "scale" ? 3 : ch.Path == "rotation" ? 4 : 0;
                    if (comps == 0) continue;
                    if (ch.Sampler < 0 || ch.Sampler >= anim.Samplers.Count) { s.Fallback = "animation " + ai + ": a channel without a sampler"; return s; }
                    var sampler = anim.Samplers[ch.Sampler];
                    if (sampler.Times == null || sampler.Times.Length == 0) { s.Fallback = "animation " + ai + ": a sampler without keys (the importer makes empty fcurves of it)"; return s; }
                    if (sampler.Components != comps) { s.Fallback = "animation " + ai + ": a sampler whose values do not fit its path"; return s; }
                    var target = ObjectOfNode(n);
                    if (target == null) { s.Fallback = "animation " + ai + ": a channel on a node that became no object (dropped with its mesh)"; return s; }
                    // get_or_create_action_and_slot: a slot per object, at the first channel that reaches it
                    if (!slotOf.TryGetValue(target, out var slot))
                    {
                        slot = new SlotData { Identifier = "OB" + target.Name, Object = target };
                        slotOf[target] = slot; act.Slots.Add(slot); slotObjects.Add(target);
                        target.HasAnimationData = true;
                    }
                    // a second channel on one node and path: channelbag.fcurves.new raises, make_fcurve returns None
                    if (!taken.Add((n, ch.Path))) { Note(s, "a second channel on one node and path (refused)"); continue; }
                    var curves = (ch.Path == "translation" ? imp.Translation : ch.Path == "rotation" ? imp.Rotation : imp.Scale)[n];
                    string blenderPath = ch.Path == "translation" ? "location" : ch.Path == "rotation" ? "rotation_quaternion" : "scale";
                    if (names.IsBone[n]) blenderPath = "pose.bones[\"" + EscapeIdentifier(names.BoneOfJoint[n]) + "\"]." + blenderPath;
                    else Note(s, names.ObjectOfNode[n] == armObj.Name ? "the armature object's own curves" : "object-level curves");
                    string ipo = sampler.Interpolation == "STEP" ? "CONSTANT" : "LINEAR";
                    if (ipo == "CONSTANT") Note(s, "a STEP sampler (CONSTANT keys)");
                    for (int c = 0; c < comps; c++)
                    {
                        var fc = new FCurveData { DataPath = blenderPath, Index = c };
                        var cv = curves[c];
                        var keys = new List<BlenderDeploy.ArmKey>();
                        for (int k = 0; k < cv.Frames.Length; k++) keys.Add(new BlenderDeploy.ArmKey { Frame = cv.Frames[k], Value = cv.Values[k] });
                        // fcurve.update(): BKE_fcurve_handles_recalc returns before a lone key; its handles stay the zeros of keyframe_points.add
                        if (keys.Count >= 2) BlenderFCurve.RecalcHandles(keys, constant: true);
                        else Note(s, "a lone key (its handles left at zero)");
                        if (keys.Count < sampler.Times.Length) Note(s, "keys closer than 0.01 frame merged");
                        foreach (var k in keys) fc.Keys.Add(new KeyData { Frame = k.Frame, Value = k.Value, LeftX = k.LeftX, LeftY = k.LeftY, RightX = k.RightX, RightY = k.RightY, Interpolation = ipo });
                        slot.FCurves.Add(fc);
                    }
                }
            if (act.Slots.Count == 0) { Note(s, "an animation without a channel on a node (no action)"); slotObjectsOf.Add(slotObjects); continue; }
            if (act.Slots.Count > 1) Note(s, "an action with several slots");
            s.Actions.Add(act); slotObjectsOf.Add(slotObjects);
        }
        if (m.Animations.Count > 1) Note(s, "several animations (the first one's action is the active one)");
        // bpy.data.actions: sorted by name, case-insensitively, a stable insertion
        BlenderExportTree.StableSort(s.Actions, (x, y) => BlenderExportTree.StrCaseCmp(x.Name, y.Name));
        // the first animation's action is the active one on the objects it has a slot for
        if (slotObjectsOf.Count > 0 && m.Animations.Count > 0)
        {
            var first = s.Actions.FirstOrDefault(x => x.Name == trackNames[0]);
            if (first != null) foreach (var slot in first.Slots) { slot.Object.Action = first.Name; slot.Object.Slot = slot.Identifier; }
        }

        // ---- the object-animation clear (the conversion path): every non-armature object with animation_data
        if (a.ConvertRig)
        {
            var objanim = s.Objects.Where(x => x.Type != "ARMATURE" && x.HasAnimationData).ToList();
            foreach (var x in objanim) { x.Action = null; x.Slot = null; x.HasAnimationData = false; }
            if (objanim.Count > 0) { s.Logs.Add("RIGANIM cleared object-level animation on " + objanim.Count + " non-armature node(s) — rigid-part captures use the authored rest TRS"); Note(s, "object-level animation cleared"); }
        }
        else if (s.Objects.Exists(x => x.Type != "ARMATURE" && x.HasAnimationData)) Note(s, "object-level animation kept (the legacy path)");

        // ---- the pose at frame 1 (import_scene.gltf leaves every animated property at the scene's frame) and every matrix_world
        Func<int, float[][]> set = null;
        if (imports.Count > 0 && s.Actions.Exists(x => x.Name == trackNames[0]))
        {
            var pose = new BlenderPosedState.Pose(imports[0], m, rig);
            set = pose.FrameSet(1f);
        }
        var world = VehicleProbe.BlenderWorldMatricesPosed(m, rig, set, out var poseMats, out var poseProps, out var locals);
        // the identity transform the importer gives a moved skinned mesh and a '.skinned'/'.mesh' vnode, as BKE_object_to_mat4 builds it
        var identity = VehicleProbe.ObjectMatrix(new float[3], new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f });
        foreach (var so in s.Objects)
        {
            var o = so.Source;
            bool skinnedMesh = o.Kind == BlenderNames.ObjectKind.Mesh && o.Skin >= 0 && so.Mesh.DefNr != null;
            so.Local = o.GltfNode >= 0 && !skinnedMesh ? locals[o.GltfNode] : identity;   // an object with its own node has the importer's properties (posed at frame 1); the others hang at the identity
            if (skinnedMesh) Note(s, o.GltfNode >= 0 ? "a skinned mesh moved under its armature" : "an animated skinned mesh node (its mesh on a '.skinned' child of the armature)");
            else if (o.GltfNode < 0) Note(s, "a mesh on an armature node (a '.mesh' child)");
        }
        float[] WorldOf(SceneObject so)
        {
            if (so.World != null) return so.World;
            var o = so.Source;
            // a dropped mesh's child keeps the matrix_world it had under it (measured on the junk fixture: the same matrix at
            // stages 1 to 3 when nothing evaluates the scene, the parent's scale in it) - the importer's chain stands, with
            // the pose; the first depsgraph update after the drop re-evaluates it as a root (Dirty; fx_s2_junk_hung)
            bool chained = o.GltfNode < 0 || (o.Kind == BlenderNames.ObjectKind.Mesh && o.Skin >= 0 && so.Mesh.DefNr != null);
            if (!chained) return so.World = world[o.GltfNode];
            if (so.ParentBone != null)
            {
                var arm = rig.ArmatureOfBone[so.ParentBoneNode];
                var parentMat = VehicleProbe.MulM4(WorldOf(so.Parent), VehicleProbe.ParBone(poseMats[so.ParentBoneNode], arm.Length[so.ParentBoneNode]));
                return so.World = VehicleProbe.MulM4(parentMat, so.Local);
            }
            return so.World = so.Parent == null ? so.Local : VehicleProbe.MulM4(WorldOf(so.Parent), so.Local);
        }
        foreach (var so in s.Objects) WorldOf(so);

        // ---- the armature's bones and pose bones
        int armNode = armObj.Source.GltfNode;
        var ad = new ArmatureData { Object = armObj, PoseProps = poseProps, PoseMats = poseMats };
        if (!rig.Armatures.TryGetValue(armNode, out ad.Rest)) { s.Fallback = "'" + armObj.Name + "': an armature without bones"; return s; }
        ad.BoneNodes = names.BoneNodesInOrder.Where(b => names.ArmatureNodeOfBone[b] == armNode).ToList();
        s.Armature = ad;
        return s;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // stage 2: the bone-parent -> skin conversion and the wrapper flatten (both under convertRig); localNodeAnim's
    // transplant is a Fallback of stage 1. Blender's rules read for this stage:
    //   * `o.matrix_world = m` is rna_Object_matrix_world_set -> BKE_object_apply_mat4(ob, m, use_compat=false,
    //     use_parent=true): with a parent, the parent's matrix (BKE_object_get_parent_matrix: its object_to_world, the
    //     bone's pose matrix moved to its tail for a bone parent) times parentinv, Eigen's inverse of that, times m;
    //     mat4_to_loc_rot_size; the object is in QUATERNION mode (the importer's set_transforms), so
    //     mat3_normalized_to_quat times the inverse of the identity delta quaternion; dloc subtracted, dscale divided
    //     out. The setter writes the PROPERTIES only: object_to_world stays what the last evaluation left until the next
    //     view_layer.update() (the flattened objects' rows of this stage still show the old matrix).
    //   * `o.parent = x`, `o.parent_type = ...`, `o.parent_bone = ...` are ED_object_parent: parentinv reset to the
    //     identity each time, and a transform tag on the object.
    //   * view_layer.update() re-evaluates what is tagged and what depends on it: the armature's pose when
    //     pose_position changes (REST: every pose matrix is the bone's arm_mat), with every object hanging from one of
    //     its bones and their descendants; a re-homed object and its descendants. An object that nothing tags keeps
    //     its matrix_world - a dropped mesh's child among them (measured, fx_s2_junk_hung).
    //   * VertexGroup.add(indices, 1.0, 'REPLACE'): the weight set where the vertex has the group, appended where not;
    //     vertex_groups.new appends the group; the modifier "Armature" is added when none of type ARMATURE is there.
    //   * The flatten unparents every MESH and ARMATURE whose parent is an EMPTY (its matrix_world assigned without a
    //     parent: a plain decomposition) and removes every EMPTY of the file - the animated skinned mesh node the importer
    //     left as an empty among them (fx_skinned_anim).

    /// <summary>Stage 2 of rig_anim.py on the scene stage 1 left: mutates `s` (objects, meshes, Logs2), or sets Fallback.</summary>
    public static void RunStage2(Stage1 s, Args a)
    {
        if (s.Fallback != null || s.Exits) return;
        if (!a.ConvertRig) { Note(s, "stage 2: the legacy path (nothing bound, nothing flattened)"); return; }
        var ad = s.Armature; var armObj = ad.Object; var rig = s.Rig;
        // _governing_bone: up the parent chain to the first object hung from a bone of an armature
        string Governing(SceneObject o)
        {
            for (var cur = o; cur != null && cur.Parent != null; cur = cur.Parent)
                if (cur.ParentBone != null && cur.Parent.Type == "ARMATURE") return cur.ParentBone;
            return null;
        }
        // _is_static_part: a staticParts entry inside the lowered object name or a lowered material name (str.lower() on
        // the names this reaches is the invariant lowering; the one-to-many lowerings Python knows are not modelled)
        bool IsStatic(SceneObject o)
        {
            if (a.StaticParts.Count == 0) return false;
            var ns = new List<string> { o.Name.ToLowerInvariant() };
            foreach (var mat in o.Mesh.Materials) if (mat != null) ns.Add(mat.ToLowerInvariant());
            return a.StaticParts.Any(sp => ns.Any(n => n.Contains(sp)));
        }
        float LengthOf(int boneNode) => rig.ArmatureOfBone[boneNode].Length[boneNode];
        // the parent matrix (BKE_object_get_parent_matrix) and the world of an object, evaluated with the given pose
        // matrices; `keep` holds the objects nothing tags, whose matrix_world stands
        float[] Eval(SceneObject o, Func<int, float[]> poseOf, Dictionary<SceneObject, float[]> memo)
        {
            if (memo.TryGetValue(o, out var w)) return w;
            if (o.Parent == null) w = o.Local;
            else
            {
                var pw = Eval(o.Parent, poseOf, memo);
                var pm = o.ParentBone != null ? VehicleProbe.MulM4(pw, VehicleProbe.ParBone(poseOf(o.ParentBoneNode), LengthOf(o.ParentBoneNode))) : pw;
                w = VehicleProbe.MulM4(pm, o.Local);
            }
            memo[o] = w; return w;
        }
        bool UnderBone(SceneObject o) { for (var cur = o; cur != null; cur = cur.Parent) if (cur.ParentBone != null) return true; return false; }
        bool DirtyChain(SceneObject o) { for (var cur = o; cur != null; cur = cur.Parent) if (cur.Dirty) return true; return false; }

        // ---- the bind
        var bp = s.Objects.Where(o => o.Type == "MESH" && Governing(o) != null && !IsStatic(o)).ToList();
        var skipped = s.Objects.Where(o => o.Type == "MESH" && Governing(o) != null && IsStatic(o)).Select(o => o.Name).ToList();
        if (skipped.Count > 0)
        {
            skipped.Sort(StringComparer.Ordinal);   // sorted(): by code point
            s.Logs2.Add("RIGANIM staticParts: " + skipped.Count + " bone-parented mesh(es) kept WEIGHTLESS (authored position, no bind): " + string.Join(", ", skipped.Take(8)) + (skipped.Count > 8 ? "…" : ""));
            Note(s, "staticParts: bone-hung meshes kept weightless");
            if (skipped.Count > 8) Note(s, "staticParts: more than eight skipped (the ellipsis)");
        }
        if (bp.Count > 0)
        {
            // one vertex-group list and one weight layer serve every object of a mesh datablock: two binds on one mesh,
            // or a bind the other object's rows then show, are not modelled
            foreach (var o in bp)
                if (s.Objects.Any(x => x != o && x.Type == "MESH" && x.DataName == o.DataName)) { s.Fallback = "'" + o.Name + "': a bone-hung mesh whose datablock another object shares (one vertex-group list, one weight layer)"; return; }
            var armBones = new HashSet<string>(ad.BoneNodes.Select(b => s.Names.BoneOfJoint[b]), StringComparer.Ordinal);
            string rootBone = ad.BoneNodes.Where(b => ad.Rest.Parent[b] < 0).Select(b => s.Names.BoneOfJoint[b]).FirstOrDefault();
            // pose_position = 'REST' + update: the script's armature at rest (pose_mat = arm_mat), every other one as posed;
            // what hangs from no bone and carries no tag is not re-evaluated (a dropped mesh's child is tagged: its first
            // update makes it a root at its own transform)
            float[] RestPose(int b) => rig.ArmatureOfBone[b] == ad.Rest ? ad.Rest.ArmMat[b] : ad.PoseMats[b];
            var restMemo = new Dictionary<SceneObject, float[]>();
            foreach (var o in s.Objects) if (!UnderBone(o) && !DirtyChain(o)) restMemo[o] = o.World;
            foreach (var o in s.Objects) if (o.Dirty) Note(s, "a tagged object re-evaluated by the bind's update (a dropped mesh's child at its own transform)");
            var plan = bp.Select(o => (o, bone: Governing(o), mw: Eval(o, RestPose, restMemo))).ToList();
            // the parent matrix of the assignment is the armature object's matrix_world as that update left it (re-rooted
            // at its own transform when it was a dropped mesh's child)
            var armWorld = Eval(armObj, RestPose, restMemo);
            int bound = 0;
            var rehomed = new HashSet<SceneObject>();
            foreach (var (o, bone, mw) in plan)
            {
                string tgt = armBones.Contains(bone) ? bone : rootBone;
                if (tgt == null) continue;
                if (tgt != bone) Note(s, "a governing bone the armature does not have: bound to its root bone");
                if (o.ParentBone == null) Note(s, "a bound mesh governed through its parent chain (not hung from the bone itself)");
                o.Parent = armObj; o.ParentBone = null; o.ParentBoneNode = -1;
                ApplyMat4(mw, armWorld, out var loc, out var quat, out var scale);
                o.Local = VehicleProbe.ObjectMatrix(loc, quat, scale);
                rehomed.Add(o);
                var d = o.Mesh;
                int gi = d.Groups.IndexOf(tgt);
                if (gi < 0) { d.Groups.Add(tgt); gi = d.Groups.Count - 1; }
                else Note(s, "a bound mesh already having a vertex group of its bone's name");
                if (d.DefNr == null)
                {
                    d.DefNr = new List<int>[d.Vertices]; d.DefWeight = new List<float>[d.Vertices];
                    for (int v = 0; v < d.Vertices; v++) { d.DefNr[v] = new List<int>(); d.DefWeight[v] = new List<float>(); }
                }
                for (int v = 0; v < d.Vertices; v++)
                {
                    int at = d.DefNr[v].IndexOf(gi);
                    if (at >= 0) d.DefWeight[v][at] = 1f; else { d.DefNr[v].Add(gi); d.DefWeight[v].Add(1f); }
                }
                if (d.ArmatureModifier == null) d.ArmatureModifier = armObj.Name;
                else Note(s, "a bound mesh already having an Armature modifier");
                bound++;
            }
            // pose_position back + update: the re-homed objects and their descendants, and everything under the
            // armature's bones again, evaluated in the pose; the rest keep their matrix_world
            bool Touched(SceneObject o) { for (var cur = o; cur != null; cur = cur.Parent) if (rehomed.Contains(cur)) return true; return false; }
            var poseMemo = new Dictionary<SceneObject, float[]>();
            foreach (var o in s.Objects) if (!UnderBone(o) && !Touched(o) && !DirtyChain(o)) poseMemo[o] = o.World;
            foreach (var o in s.Objects) { o.World = Eval(o, b => ad.PoseMats[b], poseMemo); o.Dirty = false; }
            s.Logs2.Add("RIGANIM bone-parent->skin: bound " + bound + " rigidly-hung mesh(es) full-weight to their governing bones (was: no weights -> bone #0)");
            Note(s, "bone-parent -> skin: meshes bound");
            if (s.Objects.Any(o => o.Type == "MESH" && Governing(o) != null && !rehomed.Contains(o))) Note(s, "a bone-hung mesh left hung (static) beside the bind");
        }

        // ---- the flatten
        var wrapped = s.Objects.Where(o => (o.Type == "MESH" || o.Type == "ARMATURE") && o.Parent != null && o.Parent.Type == "EMPTY").ToList();
        foreach (var o in wrapped)
        {
            var mw = o.World;
            o.Parent = null; o.ParentBone = null; o.ParentBoneNode = -1;
            ApplyMat4(mw, null, out var loc, out var quat, out var scale);
            o.Local = VehicleProbe.ObjectMatrix(loc, quat, scale);
            o.Dirty = true;   // no update follows in this stage: matrix_world stays the old one
            Note(s, o.Type == "ARMATURE" ? "the armature unparented from a wrapper empty" : "a mesh unparented from a wrapper empty");
        }
        var empties = s.Objects.Where(o => o.Type == "EMPTY").ToList();
        foreach (var e in empties)
        {
            foreach (var c in s.Objects.Where(x => x.Parent == e)) { c.Parent = null; c.ParentBone = null; c.ParentBoneNode = -1; }   // only empties are left under it
            s.Objects.Remove(e);
        }
        if (empties.Count > 0)
        {
            s.Logs2.Add("RIGANIM flattened " + empties.Count + " wrapper empt" + (empties.Count == 1 ? "y" : "ies") + "; rig reparented to root for identity export nodes");
            Note(s, "wrapper empties flattened");
            if (empties.Count > wrapped.Select(o => o.Parent).Distinct().Count()) Note(s, "an empty removed that wrapped nothing");
        }
    }

    /// <summary>BKE_object_apply_mat4 for an object in quaternion mode with the identity parentinv and no deltas: with a
    /// parent, Eigen's inverse of (the parent's matrix times the identity) times the matrix; mat4_to_loc_rot_size;
    /// mat3_normalized_to_quat times the inverse of the identity delta quaternion; dloc subtracted, dscale divided out.</summary>
    static void ApplyMat4(float[] mat, float[] parentWorld, out float[] loc, out float[] quat, out float[] scale)
    {
        var rmat = mat;
        if (parentWorld != null)
        {
            var diff = VehicleProbe.MulM4(parentWorld, VehicleProbe.IdentityF());
            rmat = VehicleProbe.MulM4(BlenderEigen.InvertM4(diff), mat);
        }
        VehicleProbe.Mat4ToLocRotSize(rmat, out var l, out var rot, out var size);
        var q = VehicleProbe.Mat3NormalizedToQuat(rot);
        quat = BlenderPosedState.MulQtQt(new[] { 1f, -0f, -0f, -0f }, q);
        loc = new[] { (float)(l[0] - 0f), (float)(l[1] - 0f), (float)(l[2] - 0f) };
        scale = new[] { (float)(size[0] / 1f), (float)(size[1] / 1f), (float)(size[2] / 1f) };
    }

    /// <summary>bpy.utils.escape_identifier (BLI_str_escape): a backslash before a quote or a backslash, the C escapes for
    /// the control characters it names.</summary>
    static string EscapeIdentifier(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\v': sb.Append("\\v"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // the dump's rows (blender_riganim_dump.py), rendered from the scene

    public sealed class Rows
    {
        public string[] Scene;
        public readonly List<string[]> Objs = new List<string[]>(), Meshes = new List<string[]>(), Groups = new List<string[]>(), Bones = new List<string[]>(),
            PoseBones = new List<string[]>(), Actions = new List<string[]>(), FCurves = new List<string[]>(), Keys = new List<string[]>();
    }

    public static string Hex(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");
    static string Sha(byte[] b) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }
    static byte[] Bytes(float[] a) { var b = new byte[4 * a.Length]; for (int i = 0; i < a.Length; i++) Array.Copy(BitConverter.GetBytes(a[i]), 0, b, 4 * i, 4); return b; }
    static byte[] Bytes(int[] a) { var b = new byte[4 * a.Length]; for (int i = 0; i < a.Length; i++) Array.Copy(BitConverter.GetBytes(a[i]), 0, b, 4 * i, 4); return b; }
    /// <summary>mathutils' row-major items of a column-major matrix, as the dump prints `m[r][c]`.</summary>
    static IEnumerable<string> HexMatrix(float[] bm) { for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) yield return Hex(bm[c * 4 + r]); }
    static IEnumerable<string> HexRowMajor(float[] items) { for (int i = 0; i < 16; i++) yield return Hex(items[i]); }

    /// <summary>The rows the dump prints for the scene after stage `n`.</summary>
    public static Rows Render(Stage1 s, int n = 1)
    {
        var r = new Rows();
        string N = n.ToString(CultureInfo.InvariantCulture);
        r.Scene = new[] { "SCENE", N, s.FrameStart.ToString(CultureInfo.InvariantCulture), s.FrameEnd.ToString(CultureInfo.InvariantCulture), s.FrameCurrent.ToString(CultureInfo.InvariantCulture), s.Armature?.PosePosition ?? "-" };
        var identity = Enumerable.Range(0, 16).Select(i => Hex(i % 5 == 0 ? 1f : 0f)).ToList();
        foreach (var o in s.Objects)
        {
            var row = new List<string> { "OBJ", N, o.Name, o.Type, o.Parent?.Name ?? "-", o.ParentBone != null ? "BONE" : "OBJECT", o.ParentBone ?? "-", o.DataName ?? "-", o.Action ?? "-", o.Slot ?? "-" };
            row.AddRange(HexMatrix(o.World)); row.AddRange(identity);
            r.Objs.Add(row.ToArray());
            if (o.Mesh == null) continue;
            var d = o.Mesh; int nf = d.Faces.Length / 3;
            var starts = new int[nf]; var totals = new int[nf];
            for (int f = 0; f < nf; f++) { starts[f] = 3 * f; totals[f] = 3; }
            r.Meshes.Add(new[]
            {
                "MESH", N, o.Name, d.Vertices.ToString(CultureInfo.InvariantCulture), nf.ToString(CultureInfo.InvariantCulture), (3 * nf).ToString(CultureInfo.InvariantCulture),
                Sha(Bytes(d.Positions)), Sha(Bytes(d.Faces)), Sha(Bytes(starts).Concat(Bytes(totals)).ToArray()), Sha(Bytes(d.FaceMaterial)), d.Uv == null ? "-" : Sha(Bytes(d.Uv)),
                d.Materials.Count == 0 ? "-" : string.Join(";", d.Materials.Select(x => x ?? "(none)")), d.Groups.Count == 0 ? "-" : string.Join(";", d.Groups),
                d.ArmatureModifier == null ? "-" : "ARMATURE:Armature:" + d.ArmatureModifier,
            });
            for (int g = 0; g < d.Groups.Count; g++)
            {
                var b = new List<byte>(); int count = 0;
                for (int v = 0; v < d.Vertices; v++)
                {
                    int at = d.DefNr[v].IndexOf(g);
                    if (at < 0) continue;
                    count++; b.AddRange(BitConverter.GetBytes((uint)v)); b.AddRange(BitConverter.GetBytes(d.DefWeight[v][at]));
                }
                r.Groups.Add(new[] { "VG", N, o.Name, d.Groups[g], count.ToString(CultureInfo.InvariantCulture), Sha(b.ToArray()) });
            }
        }
        if (s.Armature != null)
        {
            var ad = s.Armature;
            for (int i = 0; i < ad.BoneNodes.Count; i++)
            {
                int b = ad.BoneNodes[i];
                var row = new List<string> { "BONE", N, i.ToString(CultureInfo.InvariantCulture), s.Names.BoneOfJoint[b], ad.Rest.Parent[b] >= 0 ? s.Names.BoneOfJoint[ad.Rest.Parent[b]] : "-", "1", Hex(ad.Rest.Length[b]) };
                row.AddRange(HexRowMajor(ad.Rest.MatrixLocal[b]));
                r.Bones.Add(row.ToArray());
            }
            foreach (int b in ad.BoneNodes)
            {
                var p = ad.PoseProps[b];
                var row = new List<string> { "PB", N, s.Names.BoneOfJoint[b], "QUATERNION" };
                row.AddRange(p[0].Select(Hex)); row.AddRange(p[1].Select(Hex)); row.AddRange(new[] { Hex(0f), Hex(0f), Hex(0f) }); row.AddRange(p[2].Select(Hex));
                row.AddRange(HexMatrix(VehicleProbe.ObjectMatrix(p[0], p[1], p[2], true)));   // matrix_basis: BKE_pchan_to_mat4 of the properties
                r.PoseBones.Add(row.ToArray());
            }
        }
        foreach (var act in s.Actions)
        {
            r.Actions.Add(new[] { "ACT", N, act.Name, Hex(act.FrameStart), Hex(act.FrameEnd), act.FCurves.Count().ToString(CultureInfo.InvariantCulture), act.Slots.Count.ToString(CultureInfo.InvariantCulture) });
            foreach (var fc in act.FCurves)
            {
                var co = new List<byte>(); var hl = new List<byte>(); var hr = new List<byte>(); var meta = new List<string>();
                foreach (var k in fc.Keys)
                {
                    co.AddRange(BitConverter.GetBytes(k.Frame)); co.AddRange(BitConverter.GetBytes(k.Value));
                    hl.AddRange(BitConverter.GetBytes(k.LeftX)); hl.AddRange(BitConverter.GetBytes(k.LeftY));
                    hr.AddRange(BitConverter.GetBytes(k.RightX)); hr.AddRange(BitConverter.GetBytes(k.RightY));
                    meta.Add(k.Interpolation + "/" + KeyData.Easing + "/" + KeyData.HandleType + "/" + KeyData.HandleType);
                    r.Keys.Add(new[] { "K", N, act.Name, fc.DataPath, fc.Index.ToString(CultureInfo.InvariantCulture), Hex(k.Frame), Hex(k.Value), Hex(k.LeftX), Hex(k.LeftY), Hex(k.RightX), Hex(k.RightY), k.Interpolation, KeyData.Easing, KeyData.HandleType, KeyData.HandleType });
                }
                string idx = fc.Index.ToString(CultureInfo.InvariantCulture);
                r.FCurves.Add(new[] { "FC", N, act.Name, fc.DataPath, idx, fc.Keys.Count.ToString(CultureInfo.InvariantCulture), Sha(co.ToArray()), Sha(hl.Concat(hr).Concat(Encoding.UTF8.GetBytes(string.Join("|", meta))).ToArray()) });
            }
        }
        return r;
    }
}
