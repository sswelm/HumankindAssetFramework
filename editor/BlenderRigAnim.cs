// BlenderRigAnim.cs - rig_anim.py without Blender, all four stages (step 4 of replacing Blender, steps (b) to (e),
// 2026-10-12/14): what the script's first stage leaves in Blender's scene - the arguments as it parses them, the import,
// the material-less drop, the armature lookup (and the exit without one), the contract marker, the object-animation clear
// - what its second stage makes of that (RunStage2: the bone-parent -> skin bind, the wrapper flatten), its third
// (RunStage3: the bone cap, the clip resolution and slicing, the action prune, the bone filter, the rest fold and
// rebake, the legacy translation rules, the strip and amplify, the frame range) and its fourth (RunStage4: the albedo
// image, the join, the material slots, the decimate, the rebind, the root collapse, the topological rename, the donor
// sockets, the rotation and scale fold, the placement - everything before the FBX export), row for row
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
// node, every mesh BlenderReduce declines, (stage 2) a bone-hung mesh whose datablock another object shares or
// staticParts matching against supplementary Unicode names, and (stage 4) the weld of a one-material, one-bone mesh
// (bmesh remove_doubles), a socket sorting before its parent (the script exits with a message read from a freed edit
// bone), a no-op root bone with an object hung from it. Held to Blender by tools/riganim_drill.sh (--stages 4), every
// row of every stage; the albedo PNG by its dimensions (its bytes are Blender's encoder's). BlenderRigExport.cs writes
// the stage 4 scene as the GLB Blender's glTF exporter would (step (f)): `riganim.exe --export`. BlenderRigAnimInProcess.cs
// runs it all for the Factory's GLB rig route (step (g)); the drill runs that wrapper too on every exported job.
// Stage 4 rules read from Blender's sources (5.1): mesh_join.cc (the active object first, then scene order; the
// vertices through the active's inverse world, blender::math::transform_points, which leaves them untouched when the
// matrix is within 1e-6 of the identity; materials by first appearance, vertex groups and UV layers by name, custom
// normals and sharp flags copied); object_transform.cc (transform_apply: parents first, each object's rotation and
// scale into its data - mesh_transform / armature_transform_recurse - its properties reset, its world recomputed from
// the UPDATED parent, every child's properties re-decomposed and its parentinv rebuilt from the updated parent, the
// ignore_parent_tx path); math_rotation_c.cc (Matrix.Rotation's angle wrapped to (-pi, pi] in float, then cosf/sinf);
// anim_data.cc (a bone rename fixes the paths of the assigned action AND the NLA strips' stashed ones); the operators
// evaluate the depsgraph before they run (the join, the modifier apply, the transform apply) and the scene is evaluated
// once more at the end (a bone-hung object follows the bones the placement moved). The importer's image names:
// MaterialTextureOrder.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

public static class BlenderRigAnim
{
    // Python's str.strip() whitespace (the ASCII information separators included, which .NET's Trim leaves alone)
    static readonly char[] PyWhitespace = new[] { 0x9, 0xa, 0xb, 0xc, 0xd, 0x1c, 0x1d, 0x1e, 0x1f, 0x20, 0x85, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000 }.Select(c => (char)c).ToArray();
    static string PyStrip(string s) => s.Trim(PyWhitespace);

    // Python str.lower() uses Unicode's full lowercase mapping. .NET's invariant mapping is simple: notably it leaves
    // U+0130 unchanged and maps every capital sigma to σ, including the word-final one Python maps to ς. These names
    // decide whether a bone-hung mesh is kept static or converted to a skin, so the distinction changes the rig.
    internal static string PyLower(string s)
    {
        bool Cased(char c)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            return cat == UnicodeCategory.UppercaseLetter || cat == UnicodeCategory.LowercaseLetter || cat == UnicodeCategory.TitlecaseLetter;
        }
        bool CaseIgnorable(char c)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            return cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.EnclosingMark || cat == UnicodeCategory.Format ||
                   cat == UnicodeCategory.ModifierLetter || cat == UnicodeCategory.ModifierSymbol || c == '\'' || c == '’';
        }
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsSurrogatePair(s, i)) { sb.Append(s.Substring(i, 2).ToLowerInvariant()); i++; continue; }
            if (c == 'İ') { sb.Append("i\u0307"); continue; }
            if (c == 'Σ')
            {
                int before = i - 1, after = i + 1;
                while (before >= 0 && CaseIgnorable(s[before])) before--;
                while (after < s.Length && CaseIgnorable(s[after])) after++;
                sb.Append(before >= 0 && Cased(s[before]) && (after >= s.Length || !Cased(s[after])) ? 'ς' : 'σ');
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

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
        // The script lowercases each staticParts substring before matching it against object and material names.
        a.StaticParts = At(13).Split(',').Select(PyStrip).Where(p => p.Length > 0).Select(PyLower).ToList();
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
        public float[][] Props;                   // the object's own location, rotation_quaternion and scale (Local is their matrix)
        public float[] ParentInverse;             // matrix_parent_inverse when a script set one (the fold's bone-parent fix), else null = the identity
        public bool EvalStale;                    // the evaluated copy was not refreshed after the original's properties changed (nothing tagged
        public float[] EvalLocal, EvalParentInverse;   // the object): an evaluation still uses these - the copy's local matrix and parentinv
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
        public int[] Edges;                       // pairs, Blender's order (the join concatenates them)
        public bool[] FaceSharp;                  // sharp_face per face
        public bool[] EdgeSharp;                  // the sharp_edge attribute per edge (Edges' order), or null when the mesh has none: the importer writes it (all false) beside the custom normals, so only on a mesh with file normals; a mesh-level operation keeps it; a BMesh round trip (the edit-mode split, the decimate, the weld) writes it only when an edge is marked
        public short[] CustomNormal;              // 2 per corner, or null (no normals)
        public List<float[]> UvAll = new List<float[]>();   // every UV layer: UVMap, UVMap.001, ... (Uv is the first)
        public List<(string name, bool point, byte[] bytes)> Colors = new List<(string, bool, byte[])>();   // the colour layers (BlenderReduce's), for the export
        public int ActiveColor = -1;              // color_attributes.render_color_index: 0 when the importer made a layer, kept from the active object through the join
    }

    public sealed class KeyData
    {
        public float Frame, Value, LeftX, LeftY, RightX, RightY;
        public string Interpolation;              // LINEAR or CONSTANT (the importer's), BEZIER (keyframe_insert's)
        public const string Easing = "AUTO", HandleType = "AUTO_CLAMPED";
    }

    public sealed class FCurveData
    {
        public string DataPath; public int Index; public List<KeyData> Keys = new List<KeyData>();
        public string Bone;                       // the pose bone the path names, or null for an object-level path
        public string Prop;                       // location, rotation_quaternion or scale
    }

    public sealed class SlotData { public string Identifier; public SceneObject Object; public List<FCurveData> FCurves = new List<FCurveData>(); }

    public sealed class ActionData
    {
        public string Name; public float FrameStart, FrameEnd;
        public bool Stashed;                      // an imported animation: the importer's NLA strip on each touched object holds it
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
        public HashSet<int> NoDeform = new HashSet<int>();                // use_deform False (the donor sockets)
    }

    public sealed class Stage1
    {
        public string Fallback;                                       // why this file is Blender's to prepare, or null
        public readonly List<string> Logs = new List<string>();       // the RIGANIM lines of stage 1, in order
        public readonly List<string> Logs2 = new List<string>();      // the RIGANIM lines of stage 2 (RunStage2), in order
        public readonly List<string> Logs3 = new List<string>();      // the RIGANIM lines of stage 3 (RunStage3), in order
        public string Deferred;                                       // stage 3: a branch a later step models (the rest fold, the bone cap), or null
        public bool Exits;                                            // the script's sys.exit(1) in this stage (after its ERROR line)
        public string Dies;                                           // the uncaught exception the script dies of in this stage, or null
        public int FrameStart = 1, FrameEnd = 250, FrameCurrent = 1;
        public readonly List<SceneObject> Objects = new List<SceneObject>();   // scene.objects after the stage, in order
        public ArmatureData Armature;                                 // the armature the script takes (the first one), or null
        public readonly List<ActionData> Actions = new List<ActionData>();     // bpy.data.actions, in its order
        public readonly List<string> Notes = new List<string>();      // the branches this file took (the drill's census)
        public BlenderNames.Result Names; public VehicleProbe.ImportRig Rig; public HafModel Model;
        public readonly List<string> Logs4 = new List<string>();      // the RIGANIM lines of stage 4 (RunStage4), in order
        public (string path, int w, int h) Albedo;                    // stage 4: the albedo written (its dimensions), or path null
        public int AlbedoImage = -1;                                  // stage 4: the glTF image the albedo came from
        public string AlbedoBlenderName;                              // ... and the Blender image's name (several glTF images can alias one)
        public List<ActionData> AllActs = new List<ActionData>();   // stage 3's all_acts: the primary and the role clips (rebaked when folded)
        public string PrimaryAction;                                 // stage 3: the primary clip's action as it ends (the GLB route's clip name)
        public List<(string role, string action)> RoleActions = new List<(string, string)>();   // stage 3: each state role's action (role_acts), in the script's order
    }

    static void Note(Stage1 s, string what) { if (!s.Notes.Contains(what)) s.Notes.Add(what); }

    /// <summary>Stage 1 of rig_anim.py on a glTF model: the scene the script's second stage starts from, or Fallback.</summary>
    public static Stage1 RunStage1(HafModel m, Args a)
    {
        var s = new Stage1 { Model = m };
        if (a.Problem != null) { s.Fallback = a.Problem; return s; }
        string ext = System.IO.Path.GetExtension(a.Input).ToLowerInvariant();
        if (ext == ".fbx" || ext == ".blend") { s.Fallback = "a " + ext + " source: Blender's own importer, until the native reader"; return s; }
        if (a.LocalNodeAnim) { s.Fallback = "localNodeAnim: the REST hold and the inspection round trip are Blender's"; return s; }
        return ImportScene(m, s, a.Logs, dropMaterialLess: true, requireArmature: true, convertRig: a.ConvertRig);
    }

    /// <summary>The scene Blender's glTF importer makes of `m` (import_scene.gltf), as the script finds it: the objects in creation
    /// order with the importer's properties posed at frame 1, every mesh in the importer's layout, the actions with a slot per
    /// touched object. rig_anim.py's stage 1 adds the material-less drop, its no-armature exit and the object-animation clear;
    /// vehicle_rig.py's (BlenderVehicleRig) takes the scene as imported.</summary>
    internal static Stage1 ImportScene(HafModel m, Stage1 s, List<string> argLogs, bool dropMaterialLess, bool requireArmature, bool convertRig, BlenderNames.Result seed = null, bool posed = true, IEnumerable<string> existingActions = null)
    {
        s.Logs.AddRange(argLogs);
        if (m.Scenes.Count > 1) { s.Fallback = "a file of several scenes: which one Blender makes active is not modelled"; return s; }
        foreach (var ext2 in new[] { "KHR_materials_variants", "KHR_animation_pointer", "KHR_lights_punctual", "EXT_mesh_gpu_instancing" })
            if (m.ExtensionsUsed.Contains(ext2)) { s.Fallback = ext2 + ": not modelled"; return s; }
        if (m.Nodes.Exists(n => n.Camera >= 0)) { s.Fallback = "a camera: the importer's camera correction is not modelled"; return s; }
        var names = BlenderNames.Compute(m, seed); s.Names = names;
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
            var d = new MeshData { Vertices = r.VertexCount, Faces = r.Faces, Positions = r.Positions, FaceMaterial = r.FaceMaterial, Uv = r.Uv.Count > 0 ? r.Uv[0] : null, DefNr = r.DefNr, DefWeight = r.DefWeight, Edges = r.Edges, EdgeSharp = r.CustomNormal != null ? new bool[r.Edges.Length / 2] : null, FaceSharp = r.FaceSharp, CustomNormal = r.CustomNormal, UvAll = r.Uv, Colors = r.Colors, ActiveColor = r.Colors.Count > 0 ? 0 : -1 };
            // mesh.py: slots only when a primitive has a material or COLOR_0; else none - and the script drops the object
            bool hasMaterials = mesh.Primitives.Exists(p => p.Material >= 0) || mesh.Primitives.Exists(p => p.Colors != null);
            if (hasMaterials)
                foreach (var (mat, vc) in r.Slots)
                {
                    if (mat < 0 && !vc) { d.Materials.Add(null); Note(s, "an empty material slot (a primitive without a material beside one with)"); }
                    else d.Materials.Add(names.MaterialOf[(mat < 0 ? names.MeshDatablockNode[o.MeshNode] : -1, mat, vc)]);
                    if (mat < 0 && vc) Note(s, "a COLOR_0 primitive without a material (DefaultMaterial, kept)");
                }
            if (d.Materials.Count == 0 && dropMaterialLess) { dropped.Add(o); Note(s, "a material-less mesh dropped"); continue; }
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
        if (armObj == null && requireArmature)
        {
            s.Logs.Add("RIGANIM ERROR: no armature found — the model is not rigged, use the normal (static) bake instead");
            s.Exits = true; Note(s, "no armature: the script's exit");
            return s;
        }
        if (requireArmature && armObj != null && armObj.Name.StartsWith("DeployArmV2", StringComparison.Ordinal)) { s.Logs.Add("RIGANIM contract: V2 (clean-unit deploy conversion)"); Note(s, "the V2 contract marker"); }
        if (s.Objects.Count(x => x.Type == "ARMATURE") > 1) Note(s, "several armatures (the first one is the script's)");

        // ---- the actions: one per animation, a slot per object touched, the fcurves in the order the importer makes them
        var rig = VehicleProbe.BuildImportRig(m, names); s.Rig = rig;
        var trackNames = BlenderNames.TrackNames(m);
        // NLA track names are local to this file, but actions share Blender's Main datablock namespace across imports.
        var actionPool = new BlenderNames.NamePool();
        if (existingActions != null) foreach (string name in existingActions) actionPool.Unique(name);
        var actionNames = new string[trackNames.Length];
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
            var act = new ActionData { Name = trackNames[ai], FrameStart = imp.FrameStart, FrameEnd = imp.FrameEnd, Stashed = true };
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
                    else Note(s, armObj != null && names.ObjectOfNode[n] == armObj.Name ? "the armature object's own curves" : "object-level curves");
                    string ipo = sampler.Interpolation == "STEP" ? "CONSTANT" : "LINEAR";
                    if (ipo == "CONSTANT") Note(s, "a STEP sampler (CONSTANT keys)");
                    for (int c = 0; c < comps; c++)
                    {
                        var fc = new FCurveData { DataPath = blenderPath, Index = c, Bone = names.IsBone[n] ? names.BoneOfJoint[n] : null, Prop = ch.Path == "translation" ? "location" : ch.Path == "rotation" ? "rotation_quaternion" : "scale" };
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
            act.Name = actionNames[ai] = actionPool.Unique(trackNames[ai]);
            s.Actions.Add(act); slotObjectsOf.Add(slotObjects);
        }
        if (m.Animations.Count > 1) Note(s, "several animations (the first one's action is the active one)");
        // bpy.data.actions: sorted by name, case-insensitively, a stable insertion
        BlenderExportTree.StableSort(s.Actions, (x, y) => BlenderDeploy.IdNameCmp(x.Name, y.Name));
        // the first animation's action is the active one on the objects it has a slot for
        if (slotObjectsOf.Count > 0 && m.Animations.Count > 0)
        {
            var first = s.Actions.FirstOrDefault(x => x.Name == actionNames[0]);
            if (first != null) foreach (var slot in first.Slots) { slot.Object.Action = first.Name; slot.Object.Slot = slot.Identifier; }
        }

        // ---- the object-animation clear (the conversion path): every non-armature object with animation_data
        if (convertRig)
        {
            var objanim = s.Objects.Where(x => x.Type != "ARMATURE" && x.HasAnimationData).ToList();
            foreach (var x in objanim) { x.Action = null; x.Slot = null; x.HasAnimationData = false; }
            if (objanim.Count > 0) { s.Logs.Add("RIGANIM cleared object-level animation on " + objanim.Count + " non-armature node(s) — rigid-part captures use the authored rest TRS"); Note(s, "object-level animation cleared"); }
        }
        else if (s.Objects.Exists(x => x.Type != "ARMATURE" && x.HasAnimationData)) Note(s, "object-level animation kept (the legacy path)");

        // ---- the pose at frame 1 (import_scene.gltf leaves every animated property at the scene's frame) and every matrix_world
        Func<int, float[][]> set = null;
        if (posed && imports.Count > 0 && s.Actions.Exists(x => x.Name == actionNames[0]))
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
            // the properties themselves, as BlenderWorldMatricesPosed composes them: a bone's child from the importer's
            // adjusted transform, a plain object from the node's, the animated channels over them
            if (o.GltfNode >= 0 && !skinnedMesh)
            {
                int n = o.GltfNode; float[] l, q, sc;
                if (rig.IsBone(n) || rig.ParentBone(n) >= 0) rig.StaticProperty(n, out l, out q, out sc); else VehicleProbe.BlenderTrs(m.Nodes[n], out l, out q, out sc);
                var p = set?.Invoke(n);
                so.Props = new[] { p?[0] ?? l, p?[1] ?? q, p?[2] ?? sc };
            }
            else so.Props = new[] { new float[3], new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f } };
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
        if (armObj != null)
        {
            int armNode = armObj.Source.GltfNode;
            var ad = new ArmatureData { Object = armObj, PoseProps = poseProps, PoseMats = poseMats };
            if (!rig.Armatures.TryGetValue(armNode, out ad.Rest)) { s.Fallback = "'" + armObj.Name + "': an armature without bones"; return s; }
            ad.BoneNodes = names.BoneNodesInOrder.Where(b => names.ArmatureNodeOfBone[b] == armNode).ToList();
            s.Armature = ad;
        }
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
        // The target runtime's char-wise casing has no supplementary-plane mapping. Python lowercases names in those
        // scripts too (for example Deseret), so leave that rare case to Blender instead of silently binding a static mesh.
        if (a.StaticParts.Count > 0 && (a.StaticParts.Any(sp => sp.Any(char.IsSurrogate)) ||
            s.Objects.Any(o => o.Type == "MESH" && Governing(o) != null &&
                (o.Name.Any(char.IsSurrogate) || o.Mesh.Materials.Any(mat => mat != null && mat.Any(char.IsSurrogate))))))
        {
            s.Fallback = "staticParts with a supplementary Unicode name: Python's lowercase mapping is not modelled";
            return;
        }
        // _is_static_part: a staticParts entry inside the Python-lowered object or material name
        bool IsStatic(SceneObject o)
        {
            if (a.StaticParts.Count == 0) return false;
            var ns = new List<string> { PyLower(o.Name) };
            foreach (var mat in o.Mesh.Materials) if (mat != null) ns.Add(PyLower(mat));
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
                if (o.ParentInverse != null) pm = VehicleProbe.MulM4(pm, o.ParentInverse);
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
                o.Parent = armObj; o.ParentBone = null; o.ParentBoneNode = -1; o.ParentInverse = null;
                ApplyMat4(mw, armWorld, out var loc, out var quat, out var scale);
                o.Props = new[] { loc, quat, scale }; o.Local = VehicleProbe.ObjectMatrix(loc, quat, scale);
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
        int wrapperCount = wrapped.Select(o => o.Parent).Distinct().Count();
        foreach (var o in wrapped)
        {
            var mw = o.World;
            o.Parent = null; o.ParentBone = null; o.ParentBoneNode = -1; o.ParentInverse = null;
            ApplyMat4(mw, null, out var loc, out var quat, out var scale);
            o.Props = new[] { loc, quat, scale }; o.Local = VehicleProbe.ObjectMatrix(loc, quat, scale);
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
            if (empties.Count > wrapperCount) Note(s, "an empty removed that wrapped nothing");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // stage 3: the bone cap, the clip resolution (a named action, or a slice sampled frame by frame into a new action),
    // the action prune, the bone filter, the rest fold and rebake, the translation rules, the frame range. Blender's
    // rules read for this stage:
    //   * assign_action: arm.animation_data.action = a and action_slot = a.slots[0] - the FIRST slot, whichever object
    //     it was made for; an evaluation applies that channelbag's curves to the armature object (its own transform for
    //     object-level paths, its bones for pose.bones paths of bones it has).
    //   * scene.frame_set(f) + view_layer.update(): every object with an active action evaluates its slot's curves
    //     (fcurve_eval_keyframes: an exact hit returns the key, LINEAR and CONSTANT segments as the importer made them,
    //     keyframe_insert's BEZIER ones; CONSTANT extrapolation past the ends), the values written back to the original
    //     object's properties by the active depsgraph (so a frame_set leaves the properties at that frame), then every
    //     pose and every matrix_world re-evaluated. The importer's stashed NLA tracks are MUTED and evaluate nothing.
    //   * PoseBone.matrix_basis.decompose(): BKE_pchan_to_mat4 of the properties, mat4_to_loc_rot_size, then
    //     mat3_normalized_to_quat_fast (mathutils' Matrix.decompose, no checks).
    //   * keyframe_insert(path, frame): the property's value keyed on that frame of the active action's slot - the
    //     curves made in channel order at the first insert (location 0..2, rotation_quaternion 0..3), a key on a frame
    //     already keyed MOVED by the difference (float32), BEZIER keys with AUTO_CLAMPED handles recalculated.
    //   * bpy.data.actions.remove(a): every object holding it loses its action (animation_data stays).
    //   * Action.frame_range: the first and the last key frame over every fcurve of every slot; (0, 0) without a key.
    //   * scene.frame_start/end = int(round(v)): Python's round of the float32 range (half to even).
    //   * The bone cap (over 240 bones): edit mode entered and left per pass (ED_armature_to_edit / from_edit) - the
    //     survivors' edit bones are the ones the importer made, so their rest matrices come out the same; a removed
    //     bone's curves and vertex groups stay behind.
    //   * modifier_apply on an Armature modifier: BKE_armature_deform_coords_with_mesh with the linear mixer and vertex
    //     groups only - the mesh's positions into the armature's space (armature_to_target = target.world_to_object *
    //     armature.object_to_world as blender::math multiplies, target_to_armature its Eigen inverse), every weighted
    //     bone's chan_mat = pose_mat * invert_m4_m4(arm_mat), the weighted deltas summed and scaled by 1/contrib, the point
    //     back; a multi-user mesh is copied first (its name from the ID name map). The modifier is then removed and a new
    //     "Armature" one bound to the script's armature.
    //   * pose.armature_apply: per bone, head = pose_mat[3], tail = head + pose_mat[1] * length (BKE_pose_where_is_bone_tail),
    //     roll = mat3_vec_to_roll(pose 3x3, tail - head); ED_armature_from_edit rebuilds the rest (RestFromEditBones); the
    //     pose channels are reset; applyarmature_fix_boneparents decomposes a bone-parented child's world into its
    //     properties and takes its parentinv from the EVALUATED armature's parent matrix (the old pose at frame 0), and
    //     tags nothing on it - the child's evaluated copy keeps its old properties and parentinv until something does
    //     (SceneObject.EvalStale: the next frame_set evaluates it with those).
    //   * the rebake: mathutils' Matrix.inverted() (the adjugate over the float32 determinant), Matrix @ Matrix (a double
    //     dot per item), Vector.length (a double sum from the last component), Matrix.to_quaternion() = mat4_to_quat,
    //     keyframe_insert per frame; the residual printed with Python's %.6f (PyFixed).
    //   * frame_set writes an evaluated value back to the ORIGINAL only where it differs (animsys_write_orig_anim_rna):
    //     a zero of the other sign the script set earlier keeps its sign (TankDestroyers' wheels).

    static readonly Regex SliceRe = new Regex(@"^(.*)\[([0-9]+)\.\.([0-9]+)(?:/([0-9]+))?\]$", RegexOptions.Singleline);

    internal static List<BlenderDeploy.ArmKey> ToArm(List<KeyData> keys) => keys.Select(k => new BlenderDeploy.ArmKey { Frame = k.Frame, Value = k.Value, LeftX = k.LeftX, LeftY = k.LeftY, RightX = k.RightX, RightY = k.RightY, Interpolation = k.Interpolation }).ToList();
    static void FromArm(List<BlenderDeploy.ArmKey> arm, List<KeyData> keys)
    {
        keys.Clear();
        foreach (var k in arm) keys.Add(new KeyData { Frame = k.Frame, Value = k.Value, LeftX = k.LeftX, LeftY = k.LeftY, RightX = k.RightX, RightY = k.RightY, Interpolation = k.Interpolation });
    }
    internal static float EvaluateCurve(List<KeyData> keys, float frame) => BlenderFCurve.Evaluate(ToArm(keys), frame, constant: true);
    // Python compares Unicode code points, while .NET ordinal comparison compares UTF-16 code units.
    internal static readonly IComparer<string> PyStringOrder = Comparer<string>.Create((a, b) =>
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            int ca = char.IsSurrogatePair(a, i) ? char.ConvertToUtf32(a, i) : a[i];
            int cb = char.IsSurrogatePair(b, j) ? char.ConvertToUtf32(b, j) : b[j];
            if (ca != cb) return ca.CompareTo(cb);
            i += ca > 0xffff ? 2 : 1; j += cb > 0xffff ? 2 : 1;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    });

    static string PyList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(BlenderDeploy.PyRepr)) + "]";
    static int ChannelIndex(string prop) => prop == "location" ? 0 : prop == "rotation_quaternion" ? 1 : 2;
    static float[][] CloneProps(float[][] p) => new[] { (float[])p[0].Clone(), (float[])p[1].Clone(), (float[])p[2].Clone() };

    /// <summary>Stage 3 of rig_anim.py on the scene stage 2 left: mutates `s` (actions, the armature's action, the pose,
    /// the frames, Logs3), or sets Exits (the script's exits of this stage), Deferred or Fallback.</summary>
    public static void RunStage3(Stage1 s, Args a)
    {
        if (s.Fallback != null || s.Exits) return;
        var ad = s.Armature; var armObj = ad.Object; var rig = s.Rig; var names = s.Names;
        armObj.HasAnimationData = true;   // animation_data_create()
        // ---- the bone cap: over 240 bones, zero-weight leaf bones removed pass by pass (an edit-mode round trip each; the
        // survivors' rest stands: the same edit bones give the same matrices), their curves and vertex groups left behind
        if (ad.BoneNodes.Count > 240)
        {
            var weighted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mo in s.Objects.Where(x => x.Type == "MESH" && x.Mesh.ArmatureModifier == armObj.Name))
                if (mo.Mesh.DefNr != null)
                    for (int v = 0; v < mo.Mesh.Vertices; v++)
                        for (int i = 0; i < mo.Mesh.DefNr[v].Count; i++)
                            // Python compares the stored float to a double: nominal 0.001f is above 0.001, not equal.
                            if (mo.Mesh.DefWeight[v][i] > 0.001 && mo.Mesh.DefNr[v][i] < mo.Mesh.Groups.Count) weighted.Add(mo.Mesh.Groups[mo.Mesh.DefNr[v][i]]);
            int removed = 0;
            while (ad.BoneNodes.Count > 240)
            {
                var leaves = ad.BoneNodes.Where(b => !ad.BoneNodes.Any(c => ad.Rest.Parent[c] == b) && !weighted.Contains(names.BoneOfJoint[b])).ToList();
                if (leaves.Count == 0) { Note(s, "the bone cap stopped at weighted bones and their ancestors"); break; }
                foreach (int b in leaves)
                {
                    if (s.Objects.Any(o => o.ParentBoneNode == b)) { s.Fallback = "'" + names.BoneOfJoint[b] + "': a zero-weight leaf bone removed by the cap with an object hung from it"; return; }
                    ad.BoneNodes.Remove(b); ad.Rest.Bones.Remove(b); ad.PoseProps.Remove(b); ad.PoseMats.Remove(b); removed++;
                }
            }
            s.Logs3.Add("RIGANIM bone-cap: removed " + removed + " zero-weight leaf bones -> " + ad.BoneNodes.Count + " bones (Amplitude 256-bone GPU limit; weighted bones untouched)");
            Note(s, "the bone cap: zero-weight leaves removed");
        }

        // ---- the evaluation at a frame
        int BoneNodeOf(SceneObject armature, string bone)
        {
            foreach (var kv in names.BoneOfJoint) if (kv.Value == bone && names.ArmatureNodeOfBone[kv.Key] == armature.Source.GltfNode) return kv.Key;
            return -1;
        }
        ActionData ActionOf(string name) => s.Actions.FirstOrDefault(x => x.Name == name);
        var actionNames = new BlenderNames.NamePool();
        foreach (var act in s.Actions) actionNames.Unique(act.Name);
        float LengthOf(int boneNode) => rig.ArmatureOfBone[boneNode].Length[boneNode];
        void FrameSet(int f) => EvaluateFrame(s, f);
        void AssignAction(ActionData act) { armObj.Action = act.Name; armObj.Slot = act.Slots.Count > 0 ? act.Slots[0].Identifier : null; if (act.Slots.Count > 0 && act.Slots[0].Object != armObj) Note(s, "assign_action: the action's first slot is another object's (the multi-slot trap)"); }
        // pb.keyframe_insert(prop, frame=frame) on the armature's active action and slot
        void Insert(ActionData act, SlotData slot, int boneNode, string prop, float frame)
        {
            string bone = names.BoneOfJoint[boneNode];
            string path = "pose.bones[\"" + EscapeIdentifier(bone) + "\"]." + prop;
            int comps = prop == "rotation_quaternion" ? 4 : 3;
            var vals = ad.PoseProps[boneNode][ChannelIndex(prop)];
            for (int c = 0; c < comps; c++)
            {
                var fc = slot.FCurves.FirstOrDefault(x => x.DataPath == path && x.Index == c);
                if (fc == null) { fc = new FCurveData { DataPath = path, Index = c, Bone = bone, Prop = prop }; slot.FCurves.Add(fc); }
                var keys = ToArm(fc.Keys);
                var key = keys.FirstOrDefault(k => k.Frame == frame);
                if (key == null) { keys.Add(new BlenderDeploy.ArmKey { Frame = frame, Value = vals[c] }); keys.Sort((x, y) => x.Frame.CompareTo(y.Frame)); }
                else { float dy = (float)(vals[c] - key.Value); key.Value = (float)(key.Value + dy); Note(s, "keyframe_insert on a frame already keyed (the key moved by the difference)"); }
                if (keys.Count >= 2) BlenderFCurve.RecalcHandles(keys, constant: true);
                FromArm(keys, fc.Keys);
            }
        }
        void FrameRange(ActionData act)
        {
            float min = 999999999.0f, max = -999999999.0f; bool found = false;
            foreach (var fc in act.FCurves) if (fc.Keys.Count > 0) { min = Math.Min(min, fc.Keys[0].Frame); max = Math.Max(max, fc.Keys[fc.Keys.Count - 1].Frame); found = true; }
            if (found) { act.FrameStart = Math.Max(min, -1048574f); act.FrameEnd = Math.Min(max, 1048574f); } else { act.FrameStart = 0f; act.FrameEnd = 0f; }
        }
        void RemoveAction(ActionData act)
        {
            s.Actions.Remove(act);
            foreach (var o in s.Objects) if (o.Action == act.Name) { o.Action = null; o.Slot = null; }
        }
        var available = s.Actions.Select(x => x.Name).ToList();   // bpy.data.actions' names, for the ERROR lines
        bool exitNow = false;

        // ---- resolve_clip
        ActionData Resolve(string spec, string tag)
        {
            var mt = SliceRe.Match(PyStrip(spec));
            if (!mt.Success)
            {
                var act = ActionOf(spec);
                if (act == null) { s.Logs3.Add("RIGANIM ERROR: clip '" + spec + "' (" + tag + ") not found. Available: " + PyList(s.Actions.Select(x => x.Name))); s.Exits = true; exitNow = true; Note(s, "a clip not found: the script's exit"); }
                return act;
            }
            string srcName = mt.Groups[1].Value; long f0 = long.Parse(mt.Groups[2].Value, CultureInfo.InvariantCulture), f1 = long.Parse(mt.Groups[3].Value, CultureInfo.InvariantCulture);
            long step = mt.Groups[4].Success ? Math.Max(1, long.Parse(mt.Groups[4].Value, CultureInfo.InvariantCulture)) : 1;
            var src = ActionOf(srcName);
            if (src == null) { s.Logs3.Add("RIGANIM ERROR: slice source '" + srcName + "' (" + tag + ") not found. Available: " + PyList(s.Actions.Select(x => x.Name))); s.Exits = true; exitNow = true; Note(s, "a slice source not found: the script's exit"); return null; }
            var frames = new List<long>();
            if (f1 >= f0) for (long f = f0; f <= f1; f += step) frames.Add(f); else for (long f = f0; f >= f1; f -= step) frames.Add(f);
            if (frames[frames.Count - 1] != f1) frames.Add(f1);
            if (frames.Count == 1) { frames.Add(frames[0]); Note(s, "a single-frame slice (two identical frames)"); }
            if (f1 < f0) Note(s, "a reversed slice");
            if (step > 1) Note(s, "a slice with a speed step");
            string newName = srcName + "_" + tag + "_" + f0 + "_" + f1 + "_" + step;
            var old = ActionOf(newName);
            if (old != null) { Note(s, "a slice shared by two roles"); return old; }
            if (frames.Any(f => f < -1048574 || f > 1048574)) { s.Fallback = "a slice frame past Blender's frame range (frame_set clamps it)"; return null; }
            var saved = ad.BoneNodes.ToDictionary(b => b, b => CloneProps(ad.PoseProps[b]));
            AssignAction(src);
            var snap = new Dictionary<long, Dictionary<int, float[][]>>();
            long lo = frames.Min(), hi = frames.Max();
            for (long f = lo; f <= hi; f++)
            {
                FrameSet((int)f);
                var at = new Dictionary<int, float[][]>();
                foreach (int b in ad.BoneNodes)
                {
                    var p = ad.PoseProps[b];
                    VehicleProbe.Mat4ToLocRotSize(VehicleProbe.ObjectMatrix(p[0], p[1], p[2], true), out var loc, out var rot, out var size);
                    at[b] = new[] { loc, VehicleProbe.Mat3NormalizedToQuatFast(rot), size };
                }
                snap[f] = at;
            }
            // bpy.data.actions.new uses the ID name map: truncate to 255 UTF-8 bytes and resolve collisions.
            // The sliced log below still prints newName, as the Python script does.
            var na = new ActionData { Name = actionNames.Unique(newName) };
            s.Actions.Add(na); BlenderExportTree.StableSort(s.Actions, (x, y) => BlenderDeploy.IdNameCmp(x.Name, y.Name));   // bpy.data.actions: id_sort_by_name
            var slot = new SlotData { Identifier = "OB" + armObj.Name, Object = armObj }; na.Slots.Add(slot);
            armObj.Action = na.Name; armObj.Slot = slot.Identifier;
            for (int i = 0; i < frames.Count; i++)
                foreach (int b in ad.BoneNodes)
                {
                    var sn = snap[frames[i]][b];
                    ad.PoseProps[b][0] = (float[])sn[0].Clone(); ad.PoseProps[b][1] = (float[])sn[1].Clone();
                    Insert(na, slot, b, "location", i); Insert(na, slot, b, "rotation_quaternion", i);
                }
            if (frames.Count >= 2 && frames.Distinct().Count() == 1)
            {
                int b0 = ad.BoneNodes[0];
                var r2 = (float[])snap[frames[0]][b0][1].Clone();
                r2[0] = (float)((double)r2[0] + 3e-4);   // _r2.w += 3e-4: Python's double, stored as float32
                VehicleProbe.NormalizeQt(r2);
                ad.PoseProps[b0][1] = r2;
                Insert(na, slot, b0, "rotation_quaternion", frames.Count - 1);
                s.Logs3.Add("RIGANIM held-stance pad: epsilon nudge on '" + names.BoneOfJoint[b0] + "' frame " + (frames.Count - 1) + " (defeats Unity's constant-curve collapse)");
                Note(s, "the held-stance epsilon nudge");
            }
            foreach (int b in ad.BoneNodes) ad.PoseProps[b] = saved[b];
            FrameRange(na);
            s.Logs3.Add("RIGANIM sliced '" + spec + "' -> '" + newName + "' (" + frames.Count + " frames" + (f1 < f0 ? ", reversed" : "") + ")");
            Note(s, "a clip sliced");
            return na;
        }

        // ---- the primary and the roles
        ActionData primary = null;
        if (a.ClipName.Length > 0) { primary = Resolve(a.ClipName, "primary"); if (exitNow || s.Fallback != null) return; }
        if (primary == null && armObj.Action != null) primary = ActionOf(armObj.Action);
        if (primary == null && s.Actions.Count > 0) { primary = s.Actions[0]; Note(s, "no clip named and no active action: the first action"); }
        if (primary == null) { s.Logs3.Add("RIGANIM ERROR: no animation action found in the model"); s.Exits = true; Note(s, "no action: the script's exit"); return; }
        AssignAction(primary);
        var roleActs = new List<(string role, ActionData act)>();
        foreach (var (role, clip) in a.RoleSpecs)
        {
            var ra = Resolve(clip, role);
            if (exitNow || s.Fallback != null) return;
            // Python's role_acts dictionary overwrites the value without moving the role's insertion position.
            int previous = roleActs.FindIndex(r => r.role == role);
            if (previous >= 0) roleActs[previous] = (role, ra); else roleActs.Add((role, ra));
        }
        var keep = new HashSet<ActionData>(new[] { primary }.Concat(roleActs.Select(r => r.act)));
        foreach (var act in s.Actions.ToList()) if (!keep.Contains(act)) { RemoveAction(act); Note(s, "an action pruned"); }
        var allActs = new List<ActionData> { primary };
        foreach (var (_, ra) in roleActs) if (ra != primary && !allActs.Contains(ra)) allActs.Add(ra);
        s.Logs3.Add("RIGANIM action '" + primary.Name + "'" + (allActs.Count > 1 ? " + " + (allActs.Count - 1) + " state clip(s)" : ""));

        // ---- the bone filter
        string FilterBone(FCurveData fc)
        {
            // rig_anim.py's bone_of extracts the escaped RNA path literally; it does not unescape the bone name.
            // This distinction controls both the prefix match and the available-bones error list.
            if (!fc.DataPath.Contains("pose.bones[")) return null;
            int start = fc.DataPath.IndexOf("[\"", StringComparison.Ordinal) + 2;
            int end = fc.DataPath.IndexOf("\"]", start, StringComparison.Ordinal);
            return end < 0 ? fc.DataPath.Substring(start) : fc.DataPath.Substring(start, end - start);
        }
        if (a.Prefixes.Count > 0)
        {
            foreach (var fa in allActs)
            {
                var owners = fa.Slots.SelectMany(sl => sl.FCurves.Select(fc => (sl, fc))).ToList();
                var avail = owners.Select(x => FilterBone(x.fc)).Where(b => b != null).Distinct().OrderBy(x => x, PyStringOrder).ToList();
                int kept = 0, rem = 0;
                foreach (var (sl, fc) in owners)
                {
                    string bone = FilterBone(fc);
                    if (bone != null && a.Prefixes.Any(p => bone.StartsWith(p, StringComparison.Ordinal))) kept++;
                    else { sl.FCurves.Remove(fc); rem++; }
                }
                s.Logs3.Add("RIGANIM bone-filter " + PyList(a.Prefixes) + " on '" + fa.Name + "': kept " + kept + " fcurves, removed " + rem);
                Note(s, "the bone filter");
                if (kept == 0) { s.Logs3.Add("RIGANIM ERROR: bone-filter " + PyList(a.Prefixes) + " matched no animated bone in '" + fa.Name + "' — every fcurve was stripped, the clip would be frozen. Animated bones: " + PyList(avail)); s.Exits = true; Note(s, "the bone filter matched nothing: the script's exit"); return; }
            }
        }

        var keepLocPaths = new HashSet<string>(StringComparer.Ordinal);   // _KEEP_LOC_PATHS
        // ---- the rest fold and rebake (the conversion path): any location curve on a pose bone of the primary
        bool loc0 = primary.FCurves.Any(fc => fc.Bone != null && fc.Prop == "location");
        if (loc0 && a.ConvertRig)
        {
            // SNAPSHOT every exported clip's pose matrices frame by frame (the original rig and actions)
            var snaps = new Dictionary<ActionData, SortedDictionary<long, Dictionary<int, float[]>>>();
            foreach (var sa in allActs)
            {
                AssignAction(sa);
                int sfs = (int)Math.Truncate((double)sa.FrameStart), sfe = (int)Math.Truncate((double)sa.FrameEnd);
                var per = new SortedDictionary<long, Dictionary<int, float[]>>();
                for (int f = sfs; f <= sfe; f++) { FrameSet(f); per[f] = ad.BoneNodes.ToDictionary(b => b, b => (float[])ad.PoseMats[b].Clone()); }
                snaps[sa] = per;
            }
            int fs0 = (int)Math.Truncate((double)primary.FrameStart);
            AssignAction(primary); FrameSet(fs0);
            // the armature modifiers applied at frame 0: Blender's linear deform on the stored positions, the mesh of a
            // shared datablock copied first (the copy takes the next free name)
            var meshNames = new BlenderNames.NamePool();
            foreach (var dn in names.Objects.Where(x => x.Kind == BlenderNames.ObjectKind.Mesh).Select(x => x.DataName).Distinct()) meshNames.Unique(dn);
            var rebind = new List<SceneObject>();
            foreach (var mo in s.Objects.Where(x => x.Type == "MESH").ToList())
            {
                if (mo.Mesh.ArmatureModifier == null) continue;
                var armO = s.Objects.FirstOrDefault(x => x.Name == mo.Mesh.ArmatureModifier && x.Type == "ARMATURE");
                if (armO == null) { s.Fallback = "'" + mo.Name + "': an Armature modifier without an armature object (modifier_apply's failure path)"; return; }
                if (s.Objects.Count(x => x.Type == "MESH" && x.DataName == mo.DataName) > 1) { mo.DataName = meshNames.Unique(mo.DataName); Note(s, "a multi-user mesh copied before the modifier is applied"); }
                ArmatureDeform(s, mo, armO, BoneNodeOf);
                mo.Mesh.ArmatureModifier = null; rebind.Add(mo);
            }
            // pose.armature_apply: every bone's edit position from its pose matrix (head, tail at the bone's length along
            // Y, the roll of the matrix), the rest rebuilt through the edit-bone round trip, the pose channels reset
            var head = new Dictionary<int, float[]>(); var tail = new Dictionary<int, float[]>(); var roll = new Dictionary<int, float>();
            var oldPose = ad.BoneNodes.ToDictionary(b => b, b => (float[])ad.PoseMats[b].Clone()); var oldLength = ad.BoneNodes.ToDictionary(b => b, b => ad.Rest.Length[b]);
            foreach (int b in ad.BoneNodes)
            {
                var pm = ad.PoseMats[b]; float len = ad.Rest.Length[b];
                var h = new[] { pm[12], pm[13], pm[14] };
                var t = new[] { (float)(h[0] + (float)(pm[4] * len)), (float)(h[1] + (float)(pm[5] * len)), (float)(h[2] + (float)(pm[6] * len)) };
                var delta = new[] { (float)(t[0] - h[0]), (float)(t[1] - h[1]), (float)(t[2] - h[2]) };
                var pmat = new[] { pm[0], pm[1], pm[2], pm[4], pm[5], pm[6], pm[8], pm[9], pm[10] };   // copy_m3_m4
                head[b] = h; tail[b] = t; roll[b] = Mat3VecToRoll(pmat, delta);
            }
            var children = ad.BoneNodes.ToDictionary(b => b, b => new List<int>()); var roots = new List<int>();
            foreach (int b in ad.BoneNodes) { int p = ad.Rest.Parent[b]; if (p >= 0) children[p].Add(b); else roots.Add(b); }
            VehicleProbe.RestFromEditBones(ad.BoneNodes, children, roots, head, tail, roll, (b, parent, arm, offs, len) =>
            {
                ad.Rest.ArmMat[b] = arm; ad.Rest.OffsBone[b] = offs; ad.Rest.Length[b] = len; ad.Rest.MatrixLocal[b] = VehicleProbe.ToRowMajor(arm);
                ad.Rest.EditHead[b] = head[b]; ad.Rest.EditTail[b] = tail[b]; ad.Rest.EditRoll[b] = roll[b];
            });
            foreach (int b in ad.BoneNodes) ad.PoseProps[b] = new[] { new float[3], new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f } };
            foreach (var kv in VehicleProbe.PoseMatrices(ad.Rest, b => ad.PoseProps[b])) ad.PoseMats[kv.Key] = kv.Value;   // BKE_pose_where_is
            // applyarmature_fix_boneparents: an object hung from a bone of this armature keeps its place - its world
            // decomposed into its properties without a parent, its parentinv the inverse of the parent matrix as the
            // EVALUATED armature still holds it (BKE_object_workob_calc_parent takes DEG_get_evaluated(ob->parent): the pose
            // at frame 0 of the old rest, the old bone length - the depsgraph is not evaluated again before this)
            foreach (var o in s.Objects)
            {
                if (o.Parent != armObj || o.ParentBone == null) continue;
                if (!o.EvalStale) { o.EvalStale = true; o.EvalLocal = o.Local; o.EvalParentInverse = o.ParentInverse; }   // the operator tags nothing on it
                ApplyMat4(o.World, null, out var loc, out var quat, out var scale);
                o.Props = new[] { loc, quat, scale }; o.Local = VehicleProbe.ObjectMatrix(loc, quat, scale);
                o.ParentInverse = BlenderEigen.InvertM4(VehicleProbe.MulM4(armObj.World, VehicleProbe.ParBone(oldPose[o.ParentBoneNode], oldLength[o.ParentBoneNode])));
                o.Dirty = true; Note(s, "a bone-parented object kept in place by the fold (its parentinv rewritten)");
            }
            foreach (var mo in rebind) mo.Mesh.ArmatureModifier = armObj.Name;
            // the visual rebake: per clip and frame, each bone's basis against the new rest from the snapshots
            var restLocal = new Dictionary<int, float[]>();
            foreach (int b in ad.BoneNodes)
            {
                int p = ad.Rest.Parent[b];
                var pinv = p >= 0 ? PyInv(ad.Rest.ArmMat[p]) : null;
                if (p >= 0 && pinv == null) { s.Fallback = "a rest matrix mathutils cannot invert (a zero determinant)"; return; }
                restLocal[b] = p >= 0 ? PyMul(pinv, ad.Rest.ArmMat[b]) : (float[])ad.Rest.ArmMat[b].Clone();
            }
            var rebaked = new Dictionary<ActionData, ActionData>();
            foreach (var oa in allActs)
            {
                var na = new ActionData { Name = actionNames.Unique(oa.Name + "_rebaked") };
                s.Actions.Add(na); BlenderExportTree.StableSort(s.Actions, (x, y) => BlenderDeploy.IdNameCmp(x.Name, y.Name));
                var slot = new SlotData { Identifier = "OB" + armObj.Name, Object = armObj }; na.Slots.Add(slot);
                armObj.Action = na.Name; armObj.Slot = slot.Identifier;
                var frames = snaps[oa].Keys.ToList();
                if (frames.Count == 1)
                {
                    snaps[oa][frames[0] + 1] = snaps[oa][frames[0]]; frames.Add(frames[0] + 1);
                    s.Logs3.Add("RIGANIM single-frame clip '" + oa.Name + "' padded to 2 identical frames (stance)"); Note(s, "a single-frame clip padded in the rebake");
                }
                float[] LocalF(Dictionary<int, float[]> world, int b) { int p = ad.Rest.Parent[b]; if (p < 0) return world[b]; var inv = PyInv(world[p]); return inv == null ? null : PyMul(inv, world[b]); }
                var transBones = new List<int>();
                foreach (int b in ad.BoneNodes)
                {
                    float[] lo = null, hi = null; double maxoff = 0.0;
                    var restInv = PyInv(restLocal[b]);
                    foreach (long f in frames)
                    {
                        var localf = LocalF(snaps[oa][f], b);
                        if (localf == null || restInv == null) { s.Fallback = "a pose matrix mathutils cannot invert (a zero determinant)"; return; }
                        var tm = PyMul(restInv, localf); var tv = new[] { tm[12], tm[13], tm[14] };
                        double tl = VecLengthPy(tv); if (tl > maxoff) maxoff = tl;
                        if (lo == null) { lo = (float[])tv.Clone(); hi = (float[])tv.Clone(); }
                        else for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], tv[k]); hi[k] = Math.Max(hi[k], tv[k]); }
                    }
                    if (lo != null && (VecLengthPy(new[] { (float)(hi[0] - lo[0]), (float)(hi[1] - lo[1]), (float)(hi[2] - lo[2]) }) > 1e-4 || (a.KeepTranslations && maxoff > 1e-3))) transBones.Add(b);
                }
                if (transBones.Count > 0)
                {
                    var tnames = transBones.Select(b => names.BoneOfJoint[b]).OrderBy(x => x, PyStringOrder).ToList();
                    s.Logs3.Add("RIGANIM TRANSLATION-animated bone(s) in '" + oa.Name + "': " + PyList(tnames) + (a.KeepTranslations ? " (location keys KEPT — keepTranslations)" : " (location keys stripped below — set keepTranslations to keep)"));
                    Note(s, a.KeepTranslations ? "translation-animated bones, kept" : "translation-animated bones, stripped");
                    // rig_anim.py builds this set with the literal bone name, unlike keyframe_insert's escaped RNA
                    // paths. Preserve that behavior here: quoted/backslashed names are stripped by the script below.
                    foreach (int b in transBones) keepLocPaths.Add("pose.bones[\"" + names.BoneOfJoint[b] + "\"].location");
                }
                var transSet = new HashSet<int>(transBones);
                foreach (long f in frames)
                    foreach (int b in ad.BoneNodes)
                    {
                        var basis = PyMul(PyInv(restLocal[b]), LocalF(snaps[oa][f], b));
                        ad.PoseProps[b][1] = Mat4ToQuat(basis);
                        Insert(na, slot, b, "rotation_quaternion", f);
                        if (transSet.Contains(b)) { ad.PoseProps[b][0] = new[] { basis[12], basis[13], basis[14] }; Insert(na, slot, b, "location", f); }
                    }
                rebaked[oa] = na;
            }
            primary = rebaked[primary];
            roleActs = roleActs.Select(r => (r.role, rebaked[r.act])).ToList();
            allActs = new List<ActionData> { primary };
            foreach (var (_, ra) in roleActs) if (ra != primary && !allActs.Contains(ra)) allActs.Add(ra);
            AssignAction(primary); FrameSet(fs0);
            // VERIFY: the primary's frame-0 pose against the rest (a kept-translation bone sits off it by design)
            double worst = 0.0, scaleref = 0.0;
            foreach (int b in ad.BoneNodes)
            {
                if (a.KeepTranslations && keepLocPaths.Contains("pose.bones[\"" + names.BoneOfJoint[b] + "\"].location")) continue;
                var pm = ad.PoseMats[b]; var ml = ad.Rest.ArmMat[b];
                double d = VecLengthPy(new[] { (float)(pm[12] - ml[12]), (float)(pm[13] - ml[13]), (float)(pm[14] - ml[14]) });
                if (d > worst) worst = d;
                double rl = VecLengthPy(new[] { ml[12], ml[13], ml[14] });
                if (rl > scaleref) scaleref = rl;
            }
            s.Logs3.Add("RIGANIM rest-normalized + rebaked " + allActs.Count + " clip(s) x " + ad.BoneNodes.Count + " bones (" + rebind.Count + " meshes re-bound); primary frame-0 residual = " + PyFixed(worst, 6) + " (should be ~0)");
            Note(s, "the rest fold and rebake");
            double thr = Math.Max(0.02, 0.25 * scaleref);
            if (double.IsNaN(worst) || worst > thr)
            {
                s.Logs3.Add("RIGANIM ERROR: rest-fold did NOT converge — frame-0 residual " + PyFixed(worst, 6) + " exceeds " + PyFixed(thr, 6) + " (rig bone-scale " + PyFixed(scaleref, 4) + "); a rigidly displaced bone would ship (the 'head off shoulders' bug). Aborting.");
                s.Exits = true; Note(s, "the rest fold did not converge: the script's exit"); return;
            }
        }
        else if (loc0) Note(s, "location keys on the legacy path (no fold)");

        // ---- legacy keepTranslations
        var keepActs = new List<ActionData>();
        bool cleanUnits = armObj.Name.StartsWith("DeployArmV2", StringComparison.Ordinal);
        if (a.KeepTranslations && !a.ConvertRig)
        {
            var roleFloor = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (role, clip) in a.RoleSpecs)
                if ((role == "attack" || role == "move") && PyStrip(clip).Length > 0)
                {
                    string bn = PyStrip(clip.Split('[')[0]);
                    double fl = role == "attack" ? 1e-4 : 0.5;
                    roleFloor[bn] = Math.Min(roleFloor.TryGetValue(bn, out var prev) ? prev : 1e9, fl);
                }
            string Base(string name) { int at = name.IndexOf("_rebaked", StringComparison.Ordinal); return at < 0 ? name : name.Substring(0, at); }
            keepActs = allActs.Where(x => roleFloor.ContainsKey(Base(x.Name)) || roleFloor.ContainsKey(x.Name)).ToList();
            if (keepActs.Count == 0) { s.Logs3.Add("RIGANIM legacy keepTranslations: no attack/move-role clip found — nothing kept (translations live in the fire cycle / drive cycle)"); Note(s, "legacy keepTranslations: nothing kept"); }
            var poison = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sa in keepActs)
                foreach (var fc in sa.FCurves)
                {
                    if (!(fc.Bone != null && fc.Prop == "location") || fc.Keys.Count == 0) continue;
                    var vals = fc.Keys.Select(k => (double)k.Value).ToList();
                    if (vals.Any(v => double.IsNaN(v) || Math.Abs(v) > 1e5)) { poison.Add(fc.DataPath); continue; }
                    double floor = roleFloor.TryGetValue(Base(sa.Name), out var f1) ? f1 : roleFloor.TryGetValue(sa.Name, out var f2) ? f2 : 1e-4;
                    double range = vals.Max() - vals.Min();
                    if (floor < range && range < 1e4) keepLocPaths.Add(fc.DataPath);
                }
            keepLocPaths.ExceptWith(poison);
            string BoneOfPath(string p) { int q = p.IndexOf('"'); return q < 0 ? null : p.Split('"')[1]; }
            if (poison.Count > 0) { s.Logs3.Add("RIGANIM legacy keepTranslations: " + poison.Count + " path(s) REJECTED as decomposition garbage (NaN/huge): " + PyList(poison.Where(p => p.IndexOf('"') >= 0).Select(BoneOfPath).Distinct().OrderBy(x => x, PyStringOrder))); Note(s, "legacy keepTranslations: garbage rejected"); }
            if (keepLocPaths.Count > 0) { s.Logs3.Add("RIGANIM legacy keepTranslations: " + keepLocPaths.Count + " varying location path(s) kept: " + PyList(keepLocPaths.Where(p => p.IndexOf('"') >= 0).Select(BoneOfPath).Distinct().OrderBy(x => x, PyStringOrder))); Note(s, "legacy keepTranslations: paths kept"); }
        }

        // ---- the strip
        int locs = 0, keptLocs = 0;
        foreach (var sa in allActs)
            foreach (var sl in sa.Slots)
                foreach (var fc in sl.FCurves.ToList())
                {
                    if (!(fc.Bone != null && fc.Prop == "location")) continue;
                    if (a.KeepTranslations && keepLocPaths.Contains(fc.DataPath)) { keptLocs++; continue; }
                    sl.FCurves.Remove(fc); locs++;
                }
        if (locs > 0 || keptLocs > 0)
        {
            s.Logs3.Add("RIGANIM stripped " + locs + " bone-LOCATION fcurves across " + allActs.Count + " clip(s)" + (keptLocs > 0 ? ", KEPT " + keptLocs + " translation curve(s) (keepTranslations)" : " (rotation-only; translations bake unscaled)"));
            Note(s, keptLocs > 0 ? "bone-location fcurves stripped, some kept" : "bone-location fcurves stripped");
        }

        // ---- the legacy sandwich compensation
        if (a.KeepTranslations && !a.ConvertRig && !cleanUnits && keepLocPaths.Count > 0)
        {
            int scaled = 0;
            foreach (var sa in keepActs)
                foreach (var fc in sa.FCurves)
                {
                    if (!keepLocPaths.Contains(fc.DataPath) || fc.Prop != "location") continue;
                    if (fc.Keys.Count == 0) continue;
                    double v0 = fc.Keys.OrderBy(k => k.Frame).First().Value;
                    foreach (var k in fc.Keys)
                    {
                        k.Value = (float)(((double)k.Value - v0) * 100.0);
                        k.LeftY = (float)(((double)k.LeftY - v0) * 100.0);
                        k.RightY = (float)(((double)k.RightY - v0) * 100.0);
                    }
                    scaled++;
                }
            s.Logs3.Add("RIGANIM legacy keepTranslations: " + scaled + " kept curve(s) delta-rebased + amplified x100 (attack clip only)");
            Note(s, "kept curves delta-rebased and amplified");
        }

        s.AllActs = allActs;
        s.PrimaryAction = primary.Name; s.RoleActions = roleActs.Select(r => (r.role, r.act.Name)).ToList();
        // ---- the frame range
        foreach (var act in s.Actions) FrameRange(act);
        s.FrameStart = (int)Math.Round((double)primary.FrameStart, MidpointRounding.ToEven);
        s.FrameEnd = (int)Math.Round((double)primary.FrameEnd, MidpointRounding.ToEven);
        s.Logs3.Add("RIGANIM frame range " + s.FrameStart + ".." + s.FrameEnd);
    }

    // ---- the fold's arithmetic
    /// <summary>mathutils Matrix @ Matrix on Blender's column-major floats (a double dot per item).</summary>
    internal static float[] PyMul(float[] colA, float[] colB) => FromRowMajor(VehicleProbe.MatMulMathutils(VehicleProbe.ToRowMajor(colA), VehicleProbe.ToRowMajor(colB)));
    /// <summary>mathutils Matrix.inverted() (the adjugate over the float32 determinant) on column-major floats; null when singular.</summary>
    static float[] PyInv(float[] col) { var r = VehicleProbe.Inverted(VehicleProbe.ToRowMajor(col)); return r == null ? null : FromRowMajor(r); }
    static float[] FromRowMajor(float[] it) { var bm = new float[16]; for (int c = 0; c < 4; c++) for (int w = 0; w < 4; w++) bm[c * 4 + w] = it[w * 4 + c]; return bm; }
    /// <summary>mathutils Vector.length: sqrt of dot_vn_vn - the float32 products summed in double, the last component first.</summary>
    static double VecLengthPy(float[] v) { double d = 0.0; for (int i = v.Length - 1; i >= 0; i--) d += (double)(float)(v[i] * v[i]); return Math.Sqrt(d); }
    /// <summary>Matrix.to_quaternion() of a 4x4: mat4_to_quat - the 3x3 copied, each axis normalized (normalize_m3), then
    /// mat3_normalized_to_quat_with_checks.</summary>
    static float[] Mat4ToQuat(float[] bm)
    {
        var rot = new float[3][];
        for (int c = 0; c < 3; c++) { float x = bm[c * 4], y = bm[c * 4 + 1], z = bm[c * 4 + 2]; VehicleProbe.NormalizeV3Blender(ref x, ref y, ref z); rot[c] = new[] { x, y, z }; }
        return VehicleProbe.Mat3NormalizedToQuat(rot);
    }
    /// <summary>mat3_vec_to_roll: the matrix against the zero-roll matrix of the vector (vec_roll_to_mat3, invert_m3_m3,
    /// mul_m3_m3m3), mat3_to_quat of that, the twist about Y (quat_split_swing_and_twist: the canonical quaternion's
    /// 2 * atan2f(y, w)). The 3x3s as Blender's float[3][3] (M[col][row], 9 floats).</summary>
    static float Mat3VecToRoll(float[] mat, float[] vec)
    {
        float x = vec[0], y = vec[1], z = vec[2]; VehicleProbe.NormalizeV3Blender(ref x, ref y, ref z);
        var vecmat = VehicleProbe.VecRollToMat3Normalized(x, y, z, 0f);
        var rollmat = VehicleProbe.MulM3(VehicleProbe.InvertM3(vecmat), mat);
        var rot = new float[3][];
        for (int c = 0; c < 3; c++) { float cx = rollmat[c * 3], cy = rollmat[c * 3 + 1], cz = rollmat[c * 3 + 2]; VehicleProbe.NormalizeV3Blender(ref cx, ref cy, ref cz); rot[c] = new[] { cx, cy, cz }; }
        var q = VehicleProbe.Mat3NormalizedToQuat(rot);
        if (q[0] < 0f) for (int i = 0; i < 4; i++) q[i] = -q[i];
        return (float)(2.0f * BlenderTrig.Atan2f(q[2], q[0]));
    }
    /// <summary>blender::math::transform_points: nothing moves when every entry of the transform lies within 1e-6 of the
    /// identity's (skip_transform); else transform_point on each.</summary>
    internal static void TransformPoints(float[] m, float[] positions, int count)
    {
        var id = VehicleProbe.IdentityF(); bool skip = true;
        for (int i = 0; i < 16 && skip; i++) if (Math.Abs((float)(m[i] - id[i])) > 1e-6f) skip = false;
        if (skip) return;
        for (int v = 0; v < count; v++) { var p = TransformPoint(m, new[] { positions[3 * v], positions[3 * v + 1], positions[3 * v + 2] }); positions[3 * v] = p[0]; positions[3 * v + 1] = p[1]; positions[3 * v + 2] = p[2]; }
    }
    /// <summary>blender::math::transform_point(float4x4, float3): the 3x3 times the point (per column, the products summed
    /// from zero), plus the location.</summary>
    static float[] TransformPoint(float[] m, float[] p)
    {
        var r = new float[3];
        for (int k = 0; k < 3; k++)
        {
            float acc = 0f;
            for (int c = 0; c < 3; c++) acc = (float)(acc + (float)(p[c] * m[c * 4 + k]));
            r[k] = (float)(acc + m[12 + k]);
        }
        return r;
    }
    /// <summary>The Armature modifier applied (BKE_armature_deform_coords_with_mesh, the linear mixer, vertex groups only): every
    /// vertex into the armature's space (the inverse of target.world_to_object times armature.object_to_world), each
    /// weighted bone's chan_mat (pose_mat times the inverse of arm_mat) moving it, the sum over the contributions, back
    /// into the mesh's space. The positions replace the mesh's.</summary>
    static void ArmatureDeform(Stage1 s, SceneObject mo, SceneObject armO, Func<SceneObject, string, int> boneNodeOf)
    {
        var ad = s.Armature; var d = mo.Mesh;
        var armatureToTarget = VehicleProbe.MulM4(BlenderEigen.InvertM4(mo.World), armO.World);
        var targetToArmature = BlenderEigen.InvertM4(armatureToTarget);
        var chan = new Dictionary<int, float[]>();
        int[] groupBone = new int[d.Groups.Count];
        for (int g = 0; g < d.Groups.Count; g++)
        {
            int b = boneNodeOf(armO, d.Groups[g]); groupBone[g] = b;
            if (b >= 0 && !chan.ContainsKey(b)) chan[b] = VehicleProbe.MulM4(ad.PoseMats[b], BlenderEigen.InvertM4(s.Rig.ArmatureOfBone[b].ArmMat[b]));
        }
        for (int v = 0; v < d.Vertices; v++)
        {
            var co = TransformPoint(targetToArmature, new[] { d.Positions[3 * v], d.Positions[3 * v + 1], d.Positions[3 * v + 2] });
            float contrib = 0f; var delta = new float[3];
            if (d.DefNr != null)
                for (int i = 0; i < d.DefNr[v].Count; i++)
                {
                    int g = d.DefNr[v][i]; if (g < 0 || g >= groupBone.Length || groupBone[g] < 0) continue;
                    float w = d.DefWeight[v][i];
                    if (w != 0f)
                    {
                        var tp = TransformPoint(chan[groupBone[g]], co);
                        for (int k = 0; k < 3; k++) delta[k] = (float)(delta[k] + (float)(w * (float)(tp[k] - co[k])));
                    }
                    contrib = (float)(contrib + w);
                }
            if (contrib > 0.0001f) { float sf = (float)(1.0f / contrib); for (int k = 0; k < 3; k++) co[k] = (float)(co[k] + (float)(delta[k] * sf)); }
            co = TransformPoint(armatureToTarget, co);
            d.Positions[3 * v] = co[0]; d.Positions[3 * v + 1] = co[1]; d.Positions[3 * v + 2] = co[2];
        }
        Note(s, "an Armature modifier applied (the linear deform)");
    }
    /// <summary>Python's '%.{digits}f' of a double: the exact binary value rounded half to even at that digit.</summary>
    internal static string PyFixed(double v, int digits)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        long bits = BitConverter.DoubleToInt64Bits(v); bool neg = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF); long frac = bits & 0xFFFFFFFFFFFFFL;
        System.Numerics.BigInteger m; int e;
        if (exp == 0) { m = frac; e = -1074; } else { m = frac | (1L << 52); e = exp - 1075; }
        var scale = System.Numerics.BigInteger.Pow(10, digits);
        System.Numerics.BigInteger num = m * scale, den = System.Numerics.BigInteger.One;
        if (e >= 0) num <<= e; else den <<= -e;
        var q = System.Numerics.BigInteger.DivRem(num, den, out var r);
        var twice = r * 2;
        if (twice > den || (twice == den && !q.IsEven)) q += 1;
        string digitsStr = q.ToString().PadLeft(digits + 1, '0');
        string text = digitsStr.Substring(0, digitsStr.Length - digits) + (digits > 0 ? "." + digitsStr.Substring(digitsStr.Length - digits) : "");
        return (neg ? "-" : "") + text;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // stage 4: the albedo, the join, the weld, the material slots, the decimate, the rebind, the root collapse, the
    // rename, the sockets, the rotation and scale fold, the placement. Blender's rules read for this stage:
    //   * base_color_image: the Principled BSDF's Base Color input followed upstream to the nearest image - the material's
    //     baseColorTexture (the importer's node chain) - else the first image node; the first MESH object in scene order
    //     with a material decides. The image's name as the importer made it (the glTF image's name, or Image_<index>, made
    //     unique among the images); img.save() writes a PNG whose bytes and pixels this model does not produce - the IMG
    //     row's dimensions and channels are compared, its hashes are the encoder's.
    //   * object.join (mesh_join.cc): the active object first, then the others in scene order; every vertex through
    //     world_to_active (Eigen's inverse of the active's world) times the object's world, as blender::math transforms
    //     a point; materials by first appearance (one slot per distinct material, the index attribute dropped when there
    //     is one at most), vertex groups by name in object order, UV layers by name (zeros where an object lacks one),
    //     the custom-normal shorts copied, the sharp-face flags copied; the other objects deleted.
    //   * the weld (bmesh remove_doubles) is not modelled: a one-material, one-bone rig is left to Blender by name.
    //   * modifier_apply on a DECIMATE modifier: BM_mesh_decimate_collapse as BlenderDecimate ports it, the vertex normals
    //     from the custom normals, merge_customdata on the UVs afterwards.
    //   * the root collapse, the sockets and the placement enter edit mode: a bone removed re-parents its children to its
    //     parent; an edit bone made gets head, tail, roll and parent; leaving edit mode rebuilds every rest from head,
    //     tail and roll (RestFromEditBones).
    //   * Bone.name = x (ED_armature_bone_rename): the pose channel, the vertex groups of the meshes the armature deforms,
    //     the parent_bone of its children, and the paths of the ASSIGNED action (with the names escaped) follow; the
    //     script's own patch of the other actions reads the path's literal segment.
    //   * Matrix.Rotation(angle, 4, axis) = axis_angle_to_mat3_single with cosf and sinf of the float angle; `rot @
    //     matrix_world` the double-dot product, assigned as BKE_object_apply_mat4 without a parent.
    //   * transform_apply(rotation, scale) (object_transform.cc apply_objects_internal): the selected objects parents
    //     first; each object's rotation-scale matrix (BKE_object_to_mat3) applied to its data (mesh_transform; for an
    //     armature BKE_armature_transform: every bone's roll matrix kept, arm_head and arm_tail transformed, the local
    //     head and tail against the parent's new tail, the roll re-derived, where_is_bone), its rotation and scale
    //     reset, its matrix recomputed from its parent's; then ignore_parent_tx on its children: each child's EVALUATED
    //     world decomposed into the child's properties and its parentinv set to the inverse of the parent matrix as the
    //     evaluated parent still holds it.
    //   * the placement: the joined mesh's vertices through its matrix_world (mathutils' double dot), the box's centre and
    //     floor, the move said in mesh-local and armature-local terms through the adjugate inverses' 3x3, added in float32.

    static float[] MatVecPy(float[] bm, float[] v)
    {
        var r = new float[3];
        for (int row = 0; row < 3; row++)
        {
            double dot = 0.0;
            for (int col = 0; col < 4; col++) dot += (double)(float)(bm[col * 4 + row] * (col == 3 ? 1f : v[col]));
            r[row] = (float)dot;
        }
        return r;
    }
    static float[] Mat3VecPy(float[] bm, float[] v)
    {
        var r = new float[3];
        for (int row = 0; row < 3; row++)
        {
            double dot = 0.0;
            for (int col = 0; col < 3; col++) dot += (double)(float)(bm[col * 4 + row] * v[col]);
            r[row] = (float)dot;
        }
        return r;
    }
    /// <summary>mul_m4_v3: the point through the matrix, the translation added last.</summary>
    static float[] MulM4V3(float[] m, float[] v)
    {
        var r = new float[3];
        for (int k = 0; k < 3; k++) r[k] = (float)((float)((float)((float)(v[0] * m[k]) + (float)(v[1] * m[4 + k])) + (float)(m[8 + k] * v[2])) + m[12 + k]);
        return r;
    }
    /// <summary>mathutils Matrix.Rotation(angle, 4, axis): the angle wrapped into [-pi, pi] in float32 (angle_wrap_rad:
    /// mod_inline(a + pi, 2 pi) - pi, mod_inline = a - b * floorf(a / b)), then axis_angle_to_mat3_single with cosf and
    /// sinf of it. 180 degrees comes out as -pi (its sine's residual flipped), 360 as an exact identity.</summary>
    static float[] RotationMatrix(char axis, double angleDouble)
    {
        const float pi = 3.14159274f, twoPi = 6.28318548f;
        float a = (float)((float)angleDouble + pi);
        float wrapped = (float)((float)(a - (float)(twoPi * (float)Math.Floor((float)(a / twoPi)))) - pi);
        float angle = wrapped;
        float c = BlenderTrig.Cosf(angle), si = BlenderTrig.Sinf(angle);
        var m = VehicleProbe.IdentityF();
        switch (axis)
        {
            case 'X': m[5] = c; m[6] = si; m[9] = -si; m[10] = c; break;
            case 'Y': m[0] = c; m[2] = -si; m[8] = si; m[10] = c; break;
            default: m[0] = c; m[1] = si; m[4] = -si; m[5] = c; break;
        }
        return m;
    }
    static float[] VecRollToMat3(float[] vec, float roll) { float x = vec[0], y = vec[1], z = vec[2]; VehicleProbe.NormalizeV3Blender(ref x, ref y, ref z); return VehicleProbe.VecRollToMat3Normalized(x, y, z, roll); }
    static float[] Sub3(float[] a, float[] b) => new[] { (float)(a[0] - b[0]), (float)(a[1] - b[1]), (float)(a[2] - b[2]) };
    static float[] Add3(float[] a, float[] b) => new[] { (float)(a[0] + b[0]), (float)(a[1] + b[1]), (float)(a[2] + b[2]) };

    /// <summary>The bones of an armature in `armature.bones` order from the parent map: depth first, a parent before its
    /// children, the children in the given order.</summary>
    internal static List<int> DepthFirst(IEnumerable<int> bones, Dictionary<int, int> parent)
    {
        var order = bones.ToList(); var children = order.ToDictionary(b => b, b => new List<int>()); var roots = new List<int>();
        foreach (int b in order) { if (parent.TryGetValue(b, out int p) && p >= 0 && children.ContainsKey(p)) children[p].Add(b); else roots.Add(b); }
        var result = new List<int>();
        void Walk(int b) { result.Add(b); foreach (int c in children[b]) Walk(c); }
        foreach (int r in roots) Walk(r);
        return result;
    }

    /// <summary>ED_armature_from_edit on the armature's current edit bones (EditHead, EditTail, EditRoll in armature space):
    /// every rest rebuilt; the local frames kept for the next transform.</summary>
    static void RebuildRest(ArmatureData ad, List<int> boneNodes, Dictionary<int, (float[] head, float[] tail, float roll)> localsOut = null)
    {
        var rest = ad.Rest;
        var children = boneNodes.ToDictionary(b => b, b => new List<int>()); var roots = new List<int>();
        foreach (int b in boneNodes) { int p = rest.Parent[b]; if (p >= 0 && children.ContainsKey(p)) children[p].Add(b); else roots.Add(b); }
        var head = boneNodes.ToDictionary(b => b, b => (float[])rest.EditHead[b].Clone()); var tail = boneNodes.ToDictionary(b => b, b => (float[])rest.EditTail[b].Clone()); var roll = boneNodes.ToDictionary(b => b, b => rest.EditRoll[b]);
        VehicleProbe.RestFromEditBones(boneNodes, children, roots, head, tail, roll, (b, parent, arm, offs, len) =>
        {
            rest.ArmMat[b] = arm; rest.OffsBone[b] = offs; rest.Length[b] = len; rest.MatrixLocal[b] = VehicleProbe.ToRowMajor(arm);
            rest.EditTail[b] = tail[b];   // a bone of no length had its tail moved
        }, localsOut);
    }

    /// <summary>BKE_armature_transform: armature_transform_recurse over the bones in `armature.bones` order.</summary>
    static void ArmatureTransform(VehicleProbe.ArmatureResult rest, List<int> boneNodes, float[] mat, Dictionary<int, (float[] head, float[] tail, float roll)> locals)
    {
        var mat3n = new float[9];
        for (int c = 0; c < 3; c++) { float x = mat[c * 4], y = mat[c * 4 + 1], z = mat[c * 4 + 2]; VehicleProbe.NormalizeV3Blender(ref x, ref y, ref z); mat3n[c * 3] = x; mat3n[c * 3 + 1] = y; mat3n[c * 3 + 2] = z; }
        var children = boneNodes.ToDictionary(b => b, b => new List<int>()); var roots = new List<int>();
        foreach (int b in boneNodes) { int p = rest.Parent[b]; if (p >= 0 && children.ContainsKey(p)) children[p].Add(b); else roots.Add(b); }
        void Rec(int b, int parent, float[] parentArmInv)
        {
            var (lh, lt, lroll) = locals[b];
            var pre = VecRollToMat3(Sub3(lt, lh), lroll);
            if (parent < 0) pre = VehicleProbe.MulM3(mat3n, pre);
            var armHead = MulM4V3(mat, rest.EditHead[b]); var armTail = MulM4V3(mat, rest.EditTail[b]);
            float[] head, tail;
            if (parent >= 0)
            {
                head = Sub3(armHead, rest.EditTail[parent]); tail = Sub3(armTail, rest.EditTail[parent]);
                MulMat3M4V3(parentArmInv, head); MulMat3M4V3(parentArmInv, tail);
            }
            else { head = (float[])armHead.Clone(); tail = (float[])armTail.Clone(); }
            var post = VecRollToMat3(Sub3(tail, head), 0f);
            var dm = VehicleProbe.MulM3(VehicleProbe.InvertM3(post), pre);
            float roll = BlenderTrig.Atan2f(dm[2 * 3 + 0], dm[2 * 3 + 2]);
            var arm = VehicleProbe.WhereIsBone(head, tail, roll, parent >= 0 ? rest.ArmMat[parent] : null, parent >= 0 ? rest.Length[parent] : 0f, out float len, out var offs);
            rest.EditHead[b] = armHead; rest.EditTail[b] = armTail;
            rest.ArmMat[b] = arm; rest.OffsBone[b] = offs; rest.Length[b] = len; rest.MatrixLocal[b] = VehicleProbe.ToRowMajor(arm);
            var arm3 = new[] { arm[0], arm[1], arm[2], arm[4], arm[5], arm[6], arm[8], arm[9], arm[10] };
            rest.EditRoll[b] = Mat3VecToRoll(arm3, new[] { arm[4], arm[5], arm[6] });
            locals[b] = (head, tail, roll);
            var armInv = BlenderEigen.InvertM4(arm);
            foreach (int c in children[b]) Rec(c, b, armInv);
        }
        foreach (int r in roots) Rec(r, -1, null);
    }
    static void MulMat3M4V3(float[] mat, float[] v)
    {
        float x = v[0], y = v[1];
        v[0] = (float)((float)((float)(x * mat[0]) + (float)(y * mat[4])) + (float)(mat[8] * v[2]));
        v[1] = (float)((float)((float)(x * mat[1]) + (float)(y * mat[5])) + (float)(mat[9] * v[2]));
        v[2] = (float)((float)((float)(x * mat[2]) + (float)(y * mat[6])) + (float)(mat[10] * v[2]));
    }

    /// <summary>Stage 4 of rig_anim.py on the scene stage 3 left: mutates `s` (one joined mesh, the armature's bones renamed
    /// and moved, the actions' paths, Logs4, Albedo), or sets Exits or Fallback.</summary>
    public static void RunStage4(Stage1 s, Args a)
    {
        if (s.Fallback != null || s.Exits || s.Deferred != null) return;
        var ad = s.Armature; var armObj = ad.Object; var rig = s.Rig; var names = s.Names; var m = s.Model;
        float LengthOf(int boneNode) => rig.ArmatureOfBone[boneNode].Length[boneNode];
        // the worlds as a depsgraph evaluation leaves them (the properties as they stand; no curve evaluated)
        void EvalWorlds()
        {
            var memo = new Dictionary<SceneObject, float[]>();
            float[] Eval(SceneObject o)
            {
                if (memo.TryGetValue(o, out var w)) return w;
                if (o.Parent == null) w = o.EvalStale ? o.EvalLocal : o.Local;
                else
                {
                    var pw = Eval(o.Parent);
                    var pm = o.ParentBone != null ? VehicleProbe.MulM4(pw, VehicleProbe.ParBone(ad.PoseMats[o.ParentBoneNode], LengthOf(o.ParentBoneNode))) : pw;
                    var pinv = o.EvalStale ? o.EvalParentInverse : o.ParentInverse;
                    if (pinv != null) pm = VehicleProbe.MulM4(pm, pinv);
                    w = VehicleProbe.MulM4(pm, o.EvalStale ? o.EvalLocal : o.Local);
                }
                memo[o] = w; return w;
            }
            foreach (var o in s.Objects) { o.World = Eval(o); o.Dirty = false; }
        }
        ActionData ActionOf(string name) => s.Actions.FirstOrDefault(x => x.Name == name);
        var primary = armObj.Action != null ? ActionOf(armObj.Action) : null;

        // ---- the albedo
        if (a.AlbedoOut.Length > 0)
        {
            var matIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in names.MaterialOf) if (kv.Key.material >= 0 && !matIndex.ContainsKey(kv.Value)) matIndex[kv.Value] = kv.Key.material;
            int image = -1; bool found = false;
            foreach (var o in s.Objects)
            {
                if (o.Type != "MESH" || o.Mesh.Materials.Count == 0) continue;
                string mat0 = o.Mesh.Materials[0];
                if (mat0 != null && matIndex.TryGetValue(mat0, out int gm))
                {
                    var gmat = m.Materials[gm];
                    // base_color_image: the image upstream of the Principled node's Base Color; without one, the FIRST image node of
                    // the tree (node_tree.nodes in creation order = MaterialTextureOrder), whatever it textures
                    int bc = BaseColourTexture(gmat);
                    if (bc >= 0 && bc < m.Textures.Count) image = m.Textures[bc].Source;
                    else foreach (int t in MaterialTextureOrder(gmat)) if (t >= 0 && t < m.Textures.Count && m.Textures[t].Source >= 0) { image = m.Textures[t].Source; Note(s, "the albedo from a material without a base colour texture (its first image node)"); break; }
                }
                if (image >= 0) { found = true; break; }
                // base_color_image returns None: the next object is tried
            }
            if (found)
            {
                var size = BlenderMaterialExport.ImageSize(m, image);
                if (size == null) { s.Fallback = "an albedo image whose size the header does not give"; return; }
                string imgName = ImageName(s, image, out string trouble);
                if (imgName == null) { s.Fallback = trouble; return; }
                s.Albedo = (a.AlbedoOut, size.Value.w, size.Value.h); s.AlbedoImage = image; s.AlbedoBlenderName = imgName;
                s.Logs4.Add("RIGANIM albedo -> " + a.AlbedoOut + " (" + imgName + ")");
                Note(s, "the albedo written");
            }
            else { s.Logs4.Add("RIGANIM no albedo image found (model may be untextured)"); Note(s, "no albedo image"); }
        }

        // ---- the join
        var meshes = s.Objects.Where(o => o.Type == "MESH").ToList();
        if (meshes.Count == 0) { s.Logs4.Add("RIGANIM ERROR: no mesh to export"); s.Exits = true; Note(s, "no mesh to export: the script's exit"); return; }
        var active = meshes.FirstOrDefault(o => o.Mesh.ArmatureModifier != null) ?? meshes[0];
        if (active.Mesh.ArmatureModifier == null) Note(s, "the join's active object has no Armature modifier (the rebind)");
        var joined = active;
        if (meshes.Count > 1)
        {
            var order = new List<SceneObject> { active }; order.AddRange(meshes.Where(o => o != active));
            if (order.Contains(active.Parent)) { s.Fallback = "the join's active object is parented to a joined mesh"; return; }
            EvaluateFrame(s, s.FrameCurrent);   // the operator's evaluated depsgraph
            var worldToActive = BlenderEigen.InvertM4(active.World);
            int nv = order.Sum(o => o.Mesh.Vertices), nf = order.Sum(o => o.Mesh.Faces.Length / 3);
            var d = new MeshData { Vertices = nv, Positions = new float[3 * nv], Faces = new int[3 * nf], FaceMaterial = new int[nf], FaceSharp = new bool[nf] };
            // the materials by first appearance
            var mats = new List<string>(); var matSeen = new HashSet<string>(StringComparer.Ordinal); bool hasNull = false;
            foreach (var o in order) foreach (var mt in o.Mesh.Materials) { if (mt == null) { if (!hasNull) { hasNull = true; mats.Add(null); } } else if (matSeen.Add(mt)) mats.Add(mt); }
            d.Materials = mats;
            // the vertex groups by name in object order
            var groups = new List<string>(); var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var o in order) foreach (var g in o.Mesh.Groups) if (!groupIndex.ContainsKey(g)) { groupIndex[g] = groups.Count; groups.Add(g); }
            d.Groups = groups;
            if (groups.Count > 0) { d.DefNr = new List<int>[nv]; d.DefWeight = new List<float>[nv]; for (int v = 0; v < nv; v++) { d.DefNr[v] = new List<int>(); d.DefWeight[v] = new List<float>(); } }
            // the UV layers by name
            int numUv = order.Max(o => o.Mesh.UvAll.Count);
            for (int i = 0; i < numUv; i++) d.UvAll.Add(new float[2 * 3 * nf]);
            // the colour layers: CustomData_merge_layout unites them by name (the active's first, then each other mesh's new
            // ones); a mesh without a layer gets zeros - {0, 0, 0, 0} per corner (measured on fx_groups: the result's layer is
            // allocated clear and a source's bytes copied only where it has the layer); a layer held per vertex by one mesh and
            // per corner by another is left by name
            if (order.Any(o => o.Mesh.Colors.Count > 0))
            {
                var layers = new List<(string name, bool point)>();
                foreach (var o in order) foreach (var c in o.Mesh.Colors) if (!layers.Any(l => l.name == c.name)) layers.Add((c.name, c.point));
                foreach (var (name, point) in layers)
                {
                    var bytes = new List<byte>();
                    foreach (var o in order)
                    {
                        var have = o.Mesh.Colors.FirstOrDefault(c => c.name == name);
                        if (have.name != null)
                        {
                            if (have.point != point) { s.Fallback = "the join unites a colour layer held per vertex with one held per corner"; return; }
                            bytes.AddRange(have.bytes);
                        }
                        else for (int i = 0, n = 4 * (point ? o.Mesh.Vertices : o.Mesh.Faces.Length); i < n; i++) bytes.Add(0);
                    }
                    d.Colors.Add((name, point, bytes.ToArray()));
                }
                Note(s, "colour layers through the join" + (layers.Count > 0 && order.Any(o => o.Mesh.Colors.Count < layers.Count) ? " (a mesh without one: zeros)" : ""));
            }
            bool anyNormals = order.Any(o => o.Mesh.CustomNormal != null);
            if (anyNormals) d.CustomNormal = new short[2 * 3 * nf];
            var edges = new List<int>();
            int vo = 0, fo = 0;
            foreach (var o in order)
            {
                var src = o.Mesh; int snf = src.Faces.Length / 3;
                var transform = VehicleProbe.MulM4(worldToActive, o.World);
                var moved = (float[])src.Positions.Clone(); TransformPoints(transform, moved, src.Vertices);   // join_positions: transform_points
                for (int v = 0; v < src.Vertices; v++)
                {
                    d.Positions[3 * (vo + v)] = moved[3 * v]; d.Positions[3 * (vo + v) + 1] = moved[3 * v + 1]; d.Positions[3 * (vo + v) + 2] = moved[3 * v + 2];
                    if (d.DefNr != null && src.DefNr != null)
                        for (int i = 0; i < src.DefNr[v].Count; i++) { d.DefNr[vo + v].Add(groupIndex[src.Groups[src.DefNr[v][i]]]); d.DefWeight[vo + v].Add(src.DefWeight[v][i]); }
                }
                int[] indexMap = null;
                if (mats.Count > 1)
                {
                    indexMap = new int[src.Materials.Count];
                    for (int k = 0; k < src.Materials.Count; k++) indexMap[k] = src.Materials[k] == null ? mats.IndexOf(null) : mats.IndexOf(src.Materials[k]);
                }
                for (int f = 0; f < snf; f++)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int c = 3 * (fo + f) + k, sc = 3 * f + k;
                        d.Faces[c] = src.Faces[sc] + vo;
                        for (int i = 0; i < numUv; i++) if (i < src.UvAll.Count) { d.UvAll[i][2 * c] = src.UvAll[i][2 * sc]; d.UvAll[i][2 * c + 1] = src.UvAll[i][2 * sc + 1]; }
                        if (anyNormals && src.CustomNormal != null) { d.CustomNormal[2 * c] = src.CustomNormal[2 * sc]; d.CustomNormal[2 * c + 1] = src.CustomNormal[2 * sc + 1]; }
                    }
                    d.FaceMaterial[fo + f] = indexMap == null ? 0 : (src.Materials.Count == 0 ? 0 : indexMap[Math.Max(0, Math.Min(src.FaceMaterial[f], src.Materials.Count - 1))]);
                    d.FaceSharp[fo + f] = src.FaceSharp != null && src.FaceSharp[f];
                }
                if (src.Edges != null) foreach (int e in src.Edges) edges.Add(e + vo);
                vo += src.Vertices; fo += snf;
            }
            d.Edges = edges.ToArray();
            // the sharp_edge attribute joins when any source has it (the importer's, all false), the others' edges reading false
            if (order.Any(o => o.Mesh.EdgeSharp != null))
            {
                var es = new List<bool>();
                foreach (var o in order) { int ne = o.Mesh.Edges == null ? 0 : o.Mesh.Edges.Length / 2; for (int e = 0; e < ne; e++) es.Add(o.Mesh.EdgeSharp != null && o.Mesh.EdgeSharp[e]); }
                d.EdgeSharp = es.ToArray();
            }
            d.Uv = active.Mesh.UvAll.Count > 0 ? d.UvAll[0] : null;   // the active's active UV layer, by its name
            if (active.Mesh.UvAll.Count == 0 && numUv > 0) Note(s, "the join's active object has no UV layer (no active layer on the result)");
            d.ArmatureModifier = active.Mesh.ArmatureModifier;
            d.ActiveColor = active.Mesh.ActiveColor;   // the result is the active's mesh: its render colour index stays, a merged layer or not
            joined.Mesh = d;
            foreach (var o in order) if (o != active) { foreach (var c in s.Objects) if (c.Parent == o) { c.Parent = null; c.Dirty = true; } s.Objects.Remove(o); }
            Note(s, "meshes joined");
        }
        var me = joined.Mesh;
        // ---- the weld
        int nvg = me.Groups.Count;
        if (me.Materials.Count <= 1 && nvg <= 1) { s.Fallback = "a one-material, one-bone mesh: the weld (bmesh remove_doubles) is not modelled"; return; }
        s.Logs4.Add("RIGANIM weld: SKIPPED (" + me.Materials.Count + " materials, " + nvg + " bones -> preserve seams)");
        // ---- the material slots
        if (!a.KeepMaterials)
        {
            if (me.Materials.Count > 1) { me.Materials = me.Materials.Take(1).ToList(); Note(s, "the materials collapsed to one"); }
            for (int f = 0; f < me.FaceMaterial.Length; f++) me.FaceMaterial[f] = 0;
        }
        else { s.Logs4.Add("RIGANIM keeping " + me.Materials.Count + " material slots (multi-material)"); Note(s, "the material slots kept"); }
        // ---- the decimate
        int total = me.Faces.Length / 3;
        if (a.Target <= 0 || total <= a.Target) { s.Logs4.Add("RIGANIM decimate SKIPPED (" + total + " tris, target " + (a.Target <= 0 ? "OFF" : a.Target.ToString(CultureInfo.InvariantCulture)) + ")"); Note(s, "the decimate skipped"); }
        else
        {
            double ratio = Math.Min(1.0, Math.Max(0.02, (double)a.Target / Math.Max(1, total)));
            EvaluateFrame(s, s.FrameCurrent);   // modifier_apply's evaluated depsgraph
            Decimate(me, (float)ratio);
            s.Logs4.Add("RIGANIM decimate " + total + " -> " + (me.Faces.Length / 3) + " tris (ratio " + PyFixed(ratio, 3) + ")");
            Note(s, "the decimate done");
        }
        // ---- the rebind
        if (me.ArmatureModifier == null) { me.ArmatureModifier = armObj.Name; s.Logs4.Add("RIGANIM re-bound armature modifier (join had dropped it)"); Note(s, "the armature modifier re-bound after the join"); }

        bool cleanUnits = armObj.Name.StartsWith("DeployArmV2", StringComparison.Ordinal);
        if (a.ConvertRig)
        {
            // ---- the root collapse
            var animated = new HashSet<string>(StringComparer.Ordinal);
            if (primary != null) foreach (var fc in primary.FCurves) if (fc.DataPath.StartsWith("pose.bones[", StringComparison.Ordinal)) animated.Add(fc.DataPath.Split(new[] { "[\"" }, 2, StringSplitOptions.None)[1].Split(new[] { "\"]" }, 2, StringSplitOptions.None)[0]);
            var weighted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in s.Objects.Where(x => x.Type == "MESH"))
                if (o.Mesh.DefNr != null)
                    for (int v = 0; v < o.Mesh.Vertices; v++)
                        for (int i = 0; i < o.Mesh.DefNr[v].Count; i++)
                            if (o.Mesh.DefWeight[v][i] > 1e-6) weighted.Add(o.Mesh.Groups[o.Mesh.DefNr[v][i]]);
            int removed = 0;
            while (true)
            {
                int victim = -1;
                foreach (int b in ad.BoneNodes)
                {
                    if (ad.Rest.Parent[b] >= 0 && ad.BoneNodes.Contains(ad.Rest.Parent[b])) continue;
                    string bn = names.BoneOfJoint[b];
                    if (ad.BoneNodes.Count(c => ad.Rest.Parent[c] == b) == 1 && !animated.Contains(bn) && !weighted.Contains(bn)) { victim = b; break; }
                }
                if (victim < 0) break;
                if (s.Objects.Any(o => o.ParentBoneNode == victim)) { s.Fallback = "'" + names.BoneOfJoint[victim] + "': a no-op root bone collapsed with an object hung from it"; return; }
                foreach (int c in ad.BoneNodes) if (ad.Rest.Parent[c] == victim) ad.Rest.Parent[c] = ad.Rest.Parent[victim];
                ad.BoneNodes.Remove(victim); ad.Rest.Bones.Remove(victim); ad.PoseProps.Remove(victim); ad.PoseMats.Remove(victim); removed++;
            }
            if (removed > 0)
            {
                RebuildRest(ad, ad.BoneNodes);
                foreach (var kv in VehicleProbe.PoseMatrices(ad.Rest, b => ad.PoseProps[b])) ad.PoseMats[kv.Key] = kv.Value;
                s.Logs4.Add("RIGANIM collapsed " + removed + " no-op root bone(s) — every chain is now " + removed + " level(s) shallower");
                Note(s, "no-op root bones collapsed");
            }
            // ---- the topological rename
            var order = new List<int>();
            void Walk(int b) { order.Add(b); foreach (int c in ad.BoneNodes.Where(c => ad.Rest.Parent[c] == b).OrderBy(c => names.BoneOfJoint[c], PyStringOrder)) Walk(c); }
            foreach (int r in ad.BoneNodes.Where(b => ad.Rest.Parent[b] < 0 || !ad.BoneNodes.Contains(ad.Rest.Parent[b]))) Walk(r);
            string prefix = a.SocketSpecs.Count > 0 ? "A" : "b";
            var newName = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < order.Count; i++) newName[names.BoneOfJoint[order[i]]] = prefix + i.ToString("000", CultureInfo.InvariantCulture) + "_" + names.BoneOfJoint[order[i]];
            // Bone RNA names are limited to 63 UTF-8 bytes and unique within this armature. Allocate in the same
            // sequence as the setters: a requested name can collide with a bone that has not been renamed yet.
            var boneNames = new HashSet<string>(ad.BoneNodes.Select(b => names.BoneOfJoint[b]), StringComparer.Ordinal);
            foreach (int b in order)
            {
                string old = names.BoneOfJoint[b]; boneNames.Remove(old);
                string nw = BlenderNames.UniqueBone(boneNames, newName[old]);
                names.BoneOfJoint[b] = nw;
                foreach (var o in s.Objects)
                {
                    if (o.Parent == armObj && o.ParentBone == old) o.ParentBone = nw;
                    if (o.Type == "MESH" && o.Mesh.ArmatureModifier == armObj.Name) { int gi = o.Mesh.Groups.IndexOf(old); if (gi >= 0 && !o.Mesh.Groups.Contains(nw)) o.Mesh.Groups[gi] = nw; }
                }
                // BKE_animdata_fix_paths_rename_all: the assigned action's paths and the NLA strips' (the importer's stashed
                // animations), the names escaped; a slice or a rebake no strip holds is left to the script's patch
                foreach (var pa in s.Actions)
                    if (pa == primary || pa.Stashed)
                        foreach (var fc in pa.FCurves) if (fc.Bone == old) { fc.Bone = nw; fc.DataPath = "pose.bones[\"" + EscapeIdentifier(nw) + "\"]." + fc.Prop; }
            }
            s.Logs4.Add("RIGANIM bones renamed with topological prefixes (" + order.Count + " bones, prefix " + prefix + "###_) — parents now sort before children");
            Note(s, "bones renamed with topological prefixes");
            // the script's patch of every kept action: the path's literal segment looked up among the old names
            int patched = 0;
            foreach (var pa in s.AllActs)
                foreach (var fc in pa.FCurves)
                {
                    if (!fc.DataPath.Contains("pose.bones[\"")) continue;
                    string seg = fc.DataPath.Split(new[] { "[\"" }, 2, StringSplitOptions.None)[1].Split(new[] { "\"]" }, 2, StringSplitOptions.None)[0];
                    if (newName.TryGetValue(seg, out var nn)) { fc.DataPath = fc.DataPath.Replace("pose.bones[\"" + seg + "\"]", "pose.bones[\"" + nn + "\"]"); fc.Bone = nn; patched++; }
                }
            if (patched > 0) { s.Logs4.Add("RIGANIM patched " + patched + " dormant fcurve path(s) onto the renamed bones (state-role clips)"); Note(s, "dormant fcurve paths patched"); }
        }

        // ---- the donor sockets
        if (a.SocketSpecs.Count > 0)
        {
            int made = 0; int nextNode = 1000000 + names.BoneOfJoint.Count;
            var newBones = new List<int>();
            var socketNames = new HashSet<string>(ad.BoneNodes.Select(b => names.BoneOfJoint[b]), StringComparer.Ordinal);
            foreach (var (dn, ps, off) in a.SocketSpecs)
            {
                string psl = PyLower(ps);
                int parent = -1;
                foreach (int b in ad.BoneNodes.Concat(newBones)) if (PyLower(names.BoneOfJoint[b]).Contains(psl)) { parent = b; break; }
                if (parent < 0)
                {
                    var all = ad.BoneNodes.Concat(newBones).Select(b => names.BoneOfJoint[b]).Take(40);
                    s.Logs4.Add("RIGANIM ERROR: socket '" + dn + "': parent substring '" + ps + "' matches no bone. Bones: " + PyList(all)); s.Exits = true; Note(s, "a socket without a parent: the script's exit"); return;
                }
                string pn = names.BoneOfJoint[parent];
                // the script exits here, but its message reads `_parent.name` AFTER mode_set('OBJECT') freed the edit bones: an
                // empty name, garbage, or a UnicodeDecodeError in the caller (both seen) - undefined, so left by name
                if (!(PyStringOrder.Compare(dn, pn) > 0)) { s.Fallback = "socket '" + dn + "' sorts before its parent '" + pn + "': the script's exit message reads a freed edit bone (undefined)"; Note(s, "a socket sorting before its parent: the script's exit (left by name)"); return; }
                // Lookup is literal, then new() bounds and uniquifies the name. Repeating an overlong request creates
                // a second suffixed bone because the first bone's bounded name does not equal that request.
                int sb = ad.BoneNodes.Concat(newBones).Where(b => names.BoneOfJoint[b] == dn).DefaultIfEmpty(-1).First();
                if (sb < 0) { sb = nextNode++; names.BoneOfJoint[sb] = BlenderNames.UniqueBone(socketNames, dn); newBones.Add(sb); } else Note(s, "a socket bone that already existed (its edit bone reused)");
                var ph = ad.Rest.EditHead[parent]; var pt = ad.Rest.EditTail[parent];
                var dir = Sub3(pt, ph);
                double len = VecLengthPy(dir);
                float[] dirv;
                if (len > 1e-6)
                {
                    float x = dir[0], y = dir[1], z = dir[2];   // normalized(): normalize_vn_vn - a double length, a float reciprocal
                    double d2 = (double)(float)(z * z) + (double)(float)(y * y) + (double)(float)(x * x); double ds = Math.Sqrt(d2); float fi = (float)(1.0f / (float)ds);
                    var nrm = new[] { (float)(x * fi), (float)(y * fi), (float)(z * fi) };
                    float sc = (float)Math.Max(0.02, len * 0.25);
                    dirv = new[] { (float)(nrm[0] * sc), (float)(nrm[1] * sc), (float)(nrm[2] * sc) };
                }
                else dirv = new[] { 0f, 0.05f, 0f };
                var head = Add3(ph, new[] { (float)off[0], (float)off[1], (float)off[2] });
                var tail = Add3(head, dirv);
                ad.Rest.EditHead[sb] = head; ad.Rest.EditTail[sb] = tail; ad.Rest.EditRoll[sb] = ad.Rest.EditRoll[parent]; ad.Rest.Parent[sb] = parent;
                ad.NoDeform.Add(sb); rig.ArmatureOfBone[sb] = ad.Rest;
                made++;
                s.Logs4.Add("RIGANIM socket '" + dn + "' -> parent '" + pn + "' head=(" + PyFixed(head[0], 3) + ", " + PyFixed(head[1], 3) + ", " + PyFixed(head[2], 3) + ")");
                Note(s, "a socket created");
            }
            // leaving edit mode: the new bones last in the edit list, so last among their parents' children
            var all2 = ad.BoneNodes.Concat(newBones).ToList();
            ad.BoneNodes = DepthFirst(all2, ad.Rest.Parent);
            foreach (int b in newBones) { ad.Rest.Bones.Add(b); ad.PoseProps[b] = new[] { new float[3], new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f } }; }
            ad.Rest.Bones = DepthFirst(ad.Rest.Bones, ad.Rest.Parent);
            RebuildRest(ad, ad.BoneNodes);
            foreach (var kv in VehicleProbe.PoseMatrices(ad.Rest, b => ad.PoseProps[b])) ad.PoseMats[kv.Key] = kv.Value;
            s.Logs4.Add("RIGANIM donor sockets: " + made + " created");
        }

        // ---- the rig rotation and the transform fold
        if (a.ConvertRig || cleanUnits)
        {
            var rz = RotationMatrix('Z', a.RigRot[1] * Math.PI / 180.0); var rx = RotationMatrix('X', a.RigRot[0] * Math.PI / 180.0); var ry = RotationMatrix('Y', a.RigRot[2] * Math.PI / 180.0);   // math.radians: x * pi / 180 in double
            var rot = PyMul(PyMul(rz, rx), ry);
            foreach (var o in s.Objects)
            {
                if (o.Parent != null) continue;
                ApplyMat4(PyMul(rot, o.World), null, out var loc, out var quat, out var scale);
                o.Props = new[] { loc, quat, scale }; o.Local = VehicleProbe.ObjectMatrix(loc, quat, scale); o.ParentInverse = null; o.EvalStale = false; o.Dirty = true;
            }
            s.Logs4.Add("RIGANIM rig rotation: x=" + PyFloatRepr.Repr(a.RigRot[0]) + " y=" + PyFloatRepr.Repr(a.RigRot[1]) + " z=" + PyFloatRepr.Repr(a.RigRot[2]) + " (deg, registry semantics)");
            if (a.RigRot.Any(v => v != 0.0)) Note(s, "a non-zero rotation");
            if (primary != null)
            {
                int rm = 0;
                foreach (var sl in primary.Slots) foreach (var fc in sl.FCurves.ToList()) if (!fc.DataPath.StartsWith("pose.bones", StringComparison.Ordinal)) { sl.FCurves.Remove(fc); rm++; }
                if (rm > 0) { s.Logs4.Add("RIGANIM stripped " + rm + " OBJECT-level fcurves (non-bone) so the rig orientation can bake"); Note(s, "object-level fcurves stripped for the fold"); }
            }
            // the operator's evaluated depsgraph: every world with the rotated properties
            EvaluateFrame(s, s.FrameCurrent);
            // transform_apply(rotation, scale): the objects parents first
            var sorted = new List<SceneObject>();
            void AddSorted(SceneObject o) { if (sorted.Contains(o)) return; if (o.Parent != null) AddSorted(o.Parent); sorted.Add(o); }
            foreach (var o in s.Objects) AddSorted(o);
            foreach (var o in sorted)
            {
                var rs = VehicleProbe.ObjectMatrix(o.Props[0], o.Props[1], o.Props[2]);
                var mat = (float[])rs.Clone(); mat[12] = mat[13] = mat[14] = 0f;
                if (o.Type == "MESH") TransformPoints(mat, o.Mesh.Positions, o.Mesh.Vertices);   // mesh_transform: transform_points
                else if (o.Type == "ARMATURE")
                {
                    var rest = rig.Armatures[o.Source.GltfNode];
                    var bonesOf = rest == ad.Rest ? ad.BoneNodes : DepthFirst(rest.Bones, rest.Parent);
                    var locals = new Dictionary<int, (float[] head, float[] tail, float roll)>();
                    { var tmp = new ArmatureData { Rest = rest }; RebuildRest(tmp, bonesOf, locals); }
                    ArmatureTransform(rest, bonesOf, mat, locals);
                    if (rest == ad.Rest) foreach (var kv in VehicleProbe.PoseMatrices(rest, b => ad.PoseProps[b])) ad.PoseMats[kv.Key] = kv.Value;
                    else foreach (var kv in VehicleProbe.PoseMatrices(rest, b => ad.PoseProps[b])) ad.PoseMats[kv.Key] = kv.Value;
                }
                o.Props = new[] { (float[])o.Props[0].Clone(), new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f } };
                o.Local = VehicleProbe.ObjectMatrix(o.Props[0], o.Props[1], o.Props[2]);
                // BKE_object_where_is_calc: this object's matrix from its parent's (updated before it)
                float[] pm = null;
                if (o.Parent != null)
                {
                    pm = o.ParentBone != null ? VehicleProbe.MulM4(o.Parent.World, VehicleProbe.ParBone(ad.PoseMats[o.ParentBoneNode], LengthOf(o.ParentBoneNode))) : o.Parent.World;
                    if (o.ParentInverse != null) pm = VehicleProbe.MulM4(pm, o.ParentInverse);
                }
                // BKE_object_transform_copy + BKE_object_where_is_calc on the EVALUATED object: its world from its (updated) parent
                o.World = pm == null ? o.Local : VehicleProbe.MulM4(pm, o.Local);
                // ignore_parent_tx: each child's evaluated world (as the operator's start left it) decomposed into its own
                // properties, its parentinv the inverse of the parent matrix the updated evaluated parent gives
                foreach (var c in s.Objects.Where(c => c.Parent == o))
                {
                    var cw = c.World;
                    ApplyMat4(cw, null, out var loc, out var quat, out var scale);
                    c.Props = new[] { loc, quat, scale }; c.Local = VehicleProbe.ObjectMatrix(loc, quat, scale);
                    var parentMat = c.ParentBone != null ? VehicleProbe.MulM4(o.World, VehicleProbe.ParBone(ad.PoseMats[c.ParentBoneNode], LengthOf(c.ParentBoneNode))) : o.World;
                    c.ParentInverse = BlenderEigen.InvertM4(parentMat);
                    c.EvalStale = false;
                    c.World = VehicleProbe.MulM4(VehicleProbe.MulM4(parentMat, c.ParentInverse), c.Local);
                }
            }
            s.Logs4.Add("RIGANIM rig rotation+scale APPLIED TO DATA (identity nodes)");
            Note(s, "the rotation and scale folded into the data");
        }

        // ---- the placement
        if (me.Vertices > 0)
        {
            var mw = joined.World;
            double minx = double.PositiveInfinity, maxx = double.NegativeInfinity, miny = double.PositiveInfinity, maxy = double.NegativeInfinity, minz = double.PositiveInfinity, maxz = double.NegativeInfinity;
            for (int v = 0; v < me.Vertices; v++)
            {
                var p = MatVecPy(mw, new[] { me.Positions[3 * v], me.Positions[3 * v + 1], me.Positions[3 * v + 2] });
                minx = Math.Min(minx, p[0]); maxx = Math.Max(maxx, p[0]); miny = Math.Min(miny, p[1]); maxy = Math.Max(maxy, p[1]); minz = Math.Min(minz, p[2]); maxz = Math.Max(maxz, p[2]);
            }
            double dx = -(minx + maxx) / 2.0, dy = -(miny + maxy) / 2.0, dz = -minz;
            if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) > 1e-3)
            {
                var dworld = new[] { (float)dx, (float)dy, (float)dz };
                var mwInv = PyInv(mw); var armInv = PyInv(armObj.World);
                if (mwInv == null || armInv == null) { s.Fallback = "a matrix mathutils cannot invert in the placement"; return; }
                var dmesh = Mat3VecPy(mwInv, dworld); var darm = Mat3VecPy(armInv, dworld);
                for (int v = 0; v < me.Vertices; v++) for (int k = 0; k < 3; k++) me.Positions[3 * v + k] = (float)(me.Positions[3 * v + k] + dmesh[k]);
                foreach (int b in ad.BoneNodes) { ad.Rest.EditHead[b] = Add3(ad.Rest.EditHead[b], darm); ad.Rest.EditTail[b] = Add3(ad.Rest.EditTail[b], darm); }
                RebuildRest(ad, ad.BoneNodes);
                foreach (var kv in VehicleProbe.PoseMatrices(ad.Rest, b => ad.PoseProps[b])) ad.PoseMats[kv.Key] = kv.Value;
                s.Logs4.Add("RIGANIM placement: world centre (" + PyFixed(-dx, 4) + ", " + PyFixed(-dy, 4) + ") -> origin, lowest point " + PyFixed(minz, 4) + " -> 0 (moved " + PyFixed(dx, 4) + ", " + PyFixed(dy, 4) + ", " + PyFixed(dz, 4) + ")");
                Note(s, "the placement moved the model");
                double h = maxz - minz;
                if (h > 1e-6 && dz > 0.25 * h)
                {
                    s.Logs4.Add("RIGANIM WARNING: grounding lifted this model by " + PyFixed(100.0 * dz / h, 1) + "% of its own height. That is the signature of a REFERENCE clip holding a struck/folded pose (a part hanging below the keel) — reference the DEPLOYED frame instead (Furl[0..0], not Spin[0..0] or Furl[N..N]) and re-bake.");
                    Note(s, "the grounding warning");
                }
            }
            else { s.Logs4.Add("RIGANIM placement: already centred and grounded"); Note(s, "the placement found it centred"); }
        }
        else { s.Logs4.Add("RIGANIM placement: SKIPPED (no armature or no vertices) — the file's own placement is kept"); Note(s, "the placement skipped"); }
        // the scene as the next evaluation leaves it: an object hung from a bone follows the bones the placement moved
        EvaluateFrame(s, s.FrameCurrent);
    }

    /// <summary>The material's `extensions` parsed, or null.</summary>
    static JObject MaterialExtensions(HafMaterial mat)
    {
        if (mat.ExtensionsJson == null) return null;
        try { return JObject.Parse(mat.ExtensionsJson); } catch (Exception) { return null; }
    }

    static int ExtensionTexture(JObject ext, string name, string tex)
    {
        var e = ext?[name] as JObject; var ti = e?[tex] as JObject; var idx = ti?["index"];
        return idx == null || (idx.Type != JTokenType.Integer && idx.Type != JTokenType.Float) ? -1 : (int)idx;
    }

    /// <summary>The texture the importer links to the Principled node's Base Color: the pbr block's, or a specular-glossiness
    /// material's diffuse texture (imp/KHR_materials_pbrSpecularGlossiness.py calls base_color with it).</summary>
    static int BaseColourTexture(HafMaterial mat)
    {
        var ext = MaterialExtensions(mat);
        if (ext != null && ext["KHR_materials_unlit"] == null && ext["KHR_materials_pbrSpecularGlossiness"] is JObject) return ExtensionTexture(ext, "KHR_materials_pbrSpecularGlossiness", "diffuseTexture");
        return mat.BaseColorTexture;
    }

    /// <summary>The material's textures (indices, -1 where absent) in the order the importer makes their nodes - and so their
    /// images. imp/material.py: an unlit material (KHR_materials_unlit) makes the base colour alone; a specular-glossiness
    /// material the diffuse, the emission, the normal, the occlusion and the specular-glossiness; every other
    /// pbr_metallic_roughness's order - the emission, the base colour, the metallic-roughness, the normal, the occlusion, then
    /// the extensions: clearcoat (its texture, its roughness texture; the clearcoat normal is not imported), transmission,
    /// volume (its thickness texture, only with a non-zero thicknessFactor), specular (its texture, its colour texture),
    /// anisotropy, sheen (its colour texture, its roughness texture).</summary>
    static IEnumerable<int> MaterialTextureOrder(HafMaterial mat)
    {
        var ext = MaterialExtensions(mat);
        if (ext != null && ext["KHR_materials_unlit"] != null) { yield return mat.BaseColorTexture; yield break; }
        if (ext != null && ext["KHR_materials_pbrSpecularGlossiness"] is JObject)
        {
            yield return ExtensionTexture(ext, "KHR_materials_pbrSpecularGlossiness", "diffuseTexture"); yield return mat.EmissiveTexture; yield return mat.NormalTexture; yield return mat.OcclusionTexture;
            yield return ExtensionTexture(ext, "KHR_materials_pbrSpecularGlossiness", "specularGlossinessTexture");
            yield break;
        }
        yield return mat.EmissiveTexture; yield return mat.BaseColorTexture; yield return mat.MetallicRoughnessTexture; yield return mat.NormalTexture; yield return mat.OcclusionTexture;
        if (ext == null) yield break;
        yield return ExtensionTexture(ext, "KHR_materials_clearcoat", "clearcoatTexture"); yield return ExtensionTexture(ext, "KHR_materials_clearcoat", "clearcoatRoughnessTexture");
        yield return ExtensionTexture(ext, "KHR_materials_transmission", "transmissionTexture");
        var vol = ext["KHR_materials_volume"] as JObject; double thickness = 0;
        if (vol != null && vol["thicknessFactor"] != null && (vol["thicknessFactor"].Type == JTokenType.Float || vol["thicknessFactor"].Type == JTokenType.Integer)) thickness = (double)vol["thicknessFactor"];
        if (thickness != 0) yield return ExtensionTexture(ext, "KHR_materials_volume", "thicknessTexture");
        yield return ExtensionTexture(ext, "KHR_materials_specular", "specularTexture"); yield return ExtensionTexture(ext, "KHR_materials_specular", "specularColorTexture");
        yield return ExtensionTexture(ext, "KHR_materials_anisotropy", "anisotropyTexture");
        yield return ExtensionTexture(ext, "KHR_materials_sheen", "sheenColorTexture"); yield return ExtensionTexture(ext, "KHR_materials_sheen", "sheenRoughnessTexture");
    }

    /// <summary>The importer's name for an image. Images are made on FIRST USE (imp/image.py: BlenderImage.create returns
    /// when the image already has a Blender name), in the order the materials are made - the node walk, each mesh's
    /// primitives (BlenderNames' MaterialsInOrder) - and within a material in pbr_metallic_roughness's order: emission, base
    /// colour, metallic-roughness, normal, occlusion (an unlit material: the base colour alone); an unused image is never
    /// made. Embedded data takes the glTF name or Image_&lt;index&gt;; a file uri the glTF name or the file's basename, one
    /// Blender image per path (images.load with check_existing). The textures of a material in MaterialTextureOrder's
    /// order (the extensions' after the standard ones). Null, with `trouble`, when no material the importer made uses the
    /// image.</summary>
    internal static string ImageName(Stage1 s, int image, out string trouble)
    {
        var m = s.Model; var pool = new BlenderNames.NamePool();
        var created = new Dictionary<int, string>(); var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        trouble = null;
        foreach (var matName in s.Names.MaterialsInOrder)
        {
            int mi = -1; foreach (var kv in s.Names.MaterialOf) if (kv.Value == matName) { mi = kv.Key.material; break; }
            if (mi < 0 || mi >= m.Materials.Count) continue;   // DefaultMaterial (a coloured primitive without one): no textures
            foreach (int t in MaterialTextureOrder(m.Materials[mi]))
            {
                if (t < 0 || t >= m.Textures.Count) continue;
                int img = m.Textures[t].Source;
                if (img < 0 || img >= m.Images.Count || created.ContainsKey(img)) continue;
                var hi = m.Images[img]; string n;
                if (!string.IsNullOrEmpty(hi.Uri) && !hi.Uri.StartsWith("data:", StringComparison.Ordinal))
                {
                    string file = Uri.UnescapeDataString(hi.Uri).Replace('\\', '/');
                    // images.load(check_existing=True) compares resolved file paths, so URI aliases share one image ID.
                    string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(m.SourcePath) ?? "", file));
                    if (byPath.TryGetValue(path, out var existing)) { created[img] = existing; if (img == image) return existing; continue; }
                    n = pool.Unique(!string.IsNullOrEmpty(hi.Name) ? hi.Name : file.Substring(file.LastIndexOf('/') + 1)); byPath[path] = n;
                }
                else n = pool.Unique(!string.IsNullOrEmpty(hi.Name) ? hi.Name : "Image_" + img.ToString(CultureInfo.InvariantCulture));
                created[img] = n;
                if (img == image) return n;
            }
        }
        trouble = "the albedo image is used by no material the importer made"; return null;
    }

    /// <summary>The DECIMATE modifier applied on the joined mesh: the BMesh with the vertex normals from the custom normals,
    /// BM_mesh_decimate_collapse, the elements back in creation order, merge_customdata on the UVs.</summary>
    static void Decimate(MeshData me, float ratio)
    {
        int nv = me.Vertices, nf = me.Faces.Length / 3;
        var layout = new BlenderMesh { VertexCount = nv, Faces = me.Faces, ValidEdges = me.Edges ?? new int[0] };
        var sharp = me.FaceSharp ?? new bool[nf];
        float[] vno;
        if (me.CustomNormal != null)
        {
            var d0 = new short[3 * nf]; var d1 = new short[3 * nf];
            for (int c = 0; c < 3 * nf; c++) { d0[c] = me.CustomNormal[2 * c]; d1[c] = me.CustomNormal[2 * c + 1]; }
            vno = VehicleProbe.DecodeCustomShorts(me.Positions, me.Faces, sharp, d0, d1);
        }
        else vno = VehicleProbe.BlenderVertexNormals(me.Positions, me.Faces, null);
        var bm = BMesh.FromMesh(layout, me.Positions, sharp, me.FaceMaterial);
        Array.Copy(vno, bm.VNo, vno.Length);
        BlenderDecimate.FaceNormalsUpdate(bm);
        var d = new BlenderDecimate.MeshData { CustomNormal = me.CustomNormal, DefNr = me.DefNr, DefWeight = me.DefWeight };
        foreach (var uv in me.UvAll) d.Uv.Add(uv);
        foreach (var c in me.Colors) if (c.point) d.PointColor.Add(c.bytes); else d.CornerColor.Add(c.bytes);
        if (!BlenderDecimate.WouldRun(bm.TotFace, ratio)) return;
        BlenderDecimate.Collapse(bm, ratio, d);
        var newIndex = new int[bm.VertCount]; int count = 0;
        for (int v = 0; v < bm.VertCount; v++) newIndex[v] = bm.VAlive[v] ? count++ : -1;
        var pos = new float[3 * count];
        List<int>[] defNr = me.DefNr != null ? new List<int>[count] : null; List<float>[] defW = me.DefNr != null ? new List<float>[count] : null;
        for (int v = 0; v < bm.VertCount; v++)
        {
            if (!bm.VAlive[v]) continue;
            int i = newIndex[v];
            pos[3 * i] = bm.VCo[3 * v]; pos[3 * i + 1] = bm.VCo[3 * v + 1]; pos[3 * i + 2] = bm.VCo[3 * v + 2];
            if (defNr != null) { defNr[i] = d.DefNr[v]; defW[i] = d.DefWeight[v]; }
        }
        var faces = new int[3 * bm.TotFace]; var fmat = new int[bm.TotFace]; var fsharp = new bool[bm.TotFace];
        var uvOut = me.UvAll.Select(_ => new float[2 * 3 * bm.TotFace]).ToList();
        var cnOut = me.CustomNormal != null ? new short[2 * 3 * bm.TotFace] : null;
        int fo = 0;
        for (int f = 0; f < bm.FaceCount; f++)
        {
            if (!bm.FAlive[f]) continue;
            int l = bm.FLFirst[f];
            for (int k = 0; k < 3; k++)
            {
                int c = 3 * fo + k;
                faces[c] = newIndex[bm.LV[l]];
                for (int i = 0; i < uvOut.Count; i++) { uvOut[i][2 * c] = d.Uv[i][2 * l]; uvOut[i][2 * c + 1] = d.Uv[i][2 * l + 1]; }
                if (cnOut != null) { cnOut[2 * c] = me.CustomNormal[2 * l]; cnOut[2 * c + 1] = me.CustomNormal[2 * l + 1]; }
                l = bm.LNext[l];
            }
            fmat[fo] = bm.FMat[f]; fsharp[fo] = (bm.FFlag[f] & BMesh.FlagSmooth) == 0;
            fo++;
        }
        BlenderReduce.MergeUvsForApply(count, faces, uvOut);
        var edges = new List<int>();
        for (int e = 0; e < bm.EdgeCount; e++) if (bm.EAlive[e]) { edges.Add(newIndex[bm.EV1[e]]); edges.Add(newIndex[bm.EV2[e]]); }
        var colorsOut = new List<(string name, bool point, byte[] bytes)>(); int cornerLayer = 0, pointLayer = 0;
        foreach (var c in me.Colors)
        {
            if (c.point)
            {
                var src = d.PointColor[pointLayer++]; var outb = new byte[4 * count];
                for (int v = 0; v < bm.VertCount; v++) if (bm.VAlive[v]) Array.Copy(src, 4 * v, outb, 4 * newIndex[v], 4);
                colorsOut.Add((c.name, true, outb));
            }
            else
            {
                var src = d.CornerColor[cornerLayer++]; var outb = new byte[4 * 3 * bm.TotFace]; int o = 0;
                for (int f = 0; f < bm.FaceCount; f++)
                {
                    if (!bm.FAlive[f]) continue;
                    int l = bm.FLFirst[f];
                    for (int k = 0; k < 3; k++) { Array.Copy(src, 4 * l, outb, 4 * o, 4); o++; l = bm.LNext[l]; }
                }
                colorsOut.Add((c.name, false, outb));
            }
        }
        me.Colors = colorsOut;
        me.EdgeSharp = null;   // BM_mesh_bm_to_me_for_eval writes sharp_edge only when an edge is marked; the collapse marks none
        me.Vertices = count; me.Positions = pos; me.Faces = faces; me.FaceMaterial = fmat; me.FaceSharp = fsharp; me.UvAll = uvOut; me.Uv = uvOut.Count > 0 ? uvOut[0] : null; me.CustomNormal = cnOut; me.DefNr = defNr; me.DefWeight = defW; me.Edges = edges.ToArray();
    }

    /// <summary>scene.frame_set(f) and a depsgraph evaluation: every object's active slot evaluated (the values written back to the
    /// original where they differ), every pose and every matrix_world recomputed, the tagged objects included.</summary>
    internal static void EvaluateFrame(Stage1 s, int f)
        {
            var ad = s.Armature; var armObj = ad.Object; var rig = s.Rig; var names = s.Names;
            int BoneNodeOf(SceneObject armature, string bone) { foreach (var kv in names.BoneOfJoint) if (kv.Value == bone && (kv.Key >= 1000000 ? rig.ArmatureOfBone.TryGetValue(kv.Key, out var ar) && ar == rig.Armatures[armature.Source.GltfNode] : names.ArmatureNodeOfBone[kv.Key] == armature.Source.GltfNode)) return kv.Key; return -1; }
            ActionData ActionOf(string name) => s.Actions.FirstOrDefault(x => x.Name == name);
            float LengthOf(int boneNode) => rig.ArmatureOfBone[boneNode].Length[boneNode];
            s.FrameCurrent = f;
            // the evaluated copy takes every curve's value; the ORIGINAL's property is written back only where the value
            // differs (animsys_write_orig_anim_rna), so a zero of the other sign the script set keeps its sign there
            var boneProps = ad.PoseProps.ToDictionary(kv => kv.Key, kv => CloneProps(kv.Value));      // the evaluated pose
            var boneStored = ad.PoseProps.ToDictionary(kv => kv.Key, kv => CloneProps(kv.Value));     // the original's properties
            foreach (var o in s.Objects)
            {
                var props = CloneProps(o.Props); var stored = CloneProps(o.Props);
                var act = o.Action != null ? ActionOf(o.Action) : null;
                var slot = act?.Slots.FirstOrDefault(sl => sl.Identifier == o.Slot);
                if (slot != null)
                    foreach (var fc in slot.FCurves)
                    {
                        if (fc.Keys.Count == 0) continue;
                        float v = EvaluateCurve(fc.Keys, f);
                        int ci = ChannelIndex(fc.Prop);
                        if (fc.Bone == null) { props[ci][fc.Index] = v; if (stored[ci][fc.Index] != v) stored[ci][fc.Index] = v; }
                        else if (o.Type == "ARMATURE")
                        {
                            int b = BoneNodeOf(o, fc.Bone);
                            if (b >= 0 && boneProps.ContainsKey(b)) { boneProps[b][ci][fc.Index] = v; if (boneStored[b][ci][fc.Index] != v) boneStored[b][ci][fc.Index] = v; }
                        }
                    }
                o.Props = stored; o.Local = VehicleProbe.ObjectMatrix(props[0], props[1], props[2]);
            }
            ad.PoseProps = boneStored;
            var pose = new Dictionary<int, float[]>();
            foreach (var arm in rig.Armatures.Values) foreach (var kv in VehicleProbe.PoseMatrices(arm, b => boneProps[b])) pose[kv.Key] = kv.Value;
            ad.PoseMats = pose;
            var memo = new Dictionary<SceneObject, float[]>();
            float[] Eval(SceneObject o)
            {
                if (memo.TryGetValue(o, out var w)) return w;
                if (o.Parent == null) w = o.Local;
                else
                {
                    var pw = Eval(o.Parent);
                    var pm = o.ParentBone != null ? VehicleProbe.MulM4(pw, VehicleProbe.ParBone(pose[o.ParentBoneNode], LengthOf(o.ParentBoneNode))) : pw;
                    var pinv = o.EvalStale ? o.EvalParentInverse : o.ParentInverse;
                    if (pinv != null) pm = VehicleProbe.MulM4(pm, pinv);
                    w = VehicleProbe.MulM4(pm, o.EvalStale ? o.EvalLocal : o.Local);
                }
                memo[o] = w; return w;
            }
            foreach (var o in s.Objects) { o.World = Eval(o); o.Dirty = false; }
        }

    /// <summary>BKE_object_apply_mat4 for an object in quaternion mode with the identity parentinv and no deltas: with a
    /// parent, Eigen's inverse of (the parent's matrix times the identity) times the matrix; mat4_to_loc_rot_size;
    /// mat3_normalized_to_quat times the inverse of the identity delta quaternion; dloc subtracted, dscale divided out.</summary>
    internal static void ApplyMat4(float[] mat, float[] parentWorld, out float[] loc, out float[] quat, out float[] scale)
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
        public string[] Scene, Image;   // Image: stage 4's IMG row (file bytes and hashes unknown here: '-')
        public readonly List<string[]> Objs = new List<string[]>(), Meshes = new List<string[]>(), Groups = new List<string[]>(), Bones = new List<string[]>(),
            PoseBones = new List<string[]>(), Actions = new List<string[]>(), FCurves = new List<string[]>(), Keys = new List<string[]>();
    }

    /// <summary>The slot identifier inserted after the action's name (the vehicle dump's FC and K rows carry it).</summary>
    static string[] WithSlot(bool on, string slot, string[] row) { if (!on) return row; var l = row.ToList(); l.Insert(3, slot); return l.ToArray(); }

    public static string Hex(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");
    static byte[] Bytes(short[] a) { var b = new byte[2 * a.Length]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
    static string Sha(byte[] b) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }
    static byte[] Bytes(float[] a) { var b = new byte[4 * a.Length]; for (int i = 0; i < a.Length; i++) Array.Copy(BitConverter.GetBytes(a[i]), 0, b, 4 * i, 4); return b; }
    static byte[] Bytes(int[] a) { var b = new byte[4 * a.Length]; for (int i = 0; i < a.Length; i++) Array.Copy(BitConverter.GetBytes(a[i]), 0, b, 4 * i, 4); return b; }
    /// <summary>mathutils' row-major items of a column-major matrix, as the dump prints `m[r][c]`.</summary>
    static IEnumerable<string> HexMatrix(float[] bm) { for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) yield return Hex(bm[c * 4 + r]); }
    static IEnumerable<string> HexRowMajor(float[] items) { for (int i = 0; i < 16; i++) yield return Hex(items[i]); }

    /// <summary>The rows the dump prints for the scene after stage `n`.</summary>
    public static Rows Render(Stage1 s, int n = 1, bool fcSlot = false)
    {
        var r = new Rows();
        string N = n.ToString(CultureInfo.InvariantCulture);
        r.Scene = new[] { "SCENE", N, s.FrameStart.ToString(CultureInfo.InvariantCulture), s.FrameEnd.ToString(CultureInfo.InvariantCulture), s.FrameCurrent.ToString(CultureInfo.InvariantCulture), s.Armature?.PosePosition ?? "-" };
        var identity = Enumerable.Range(0, 16).Select(i => Hex(i % 5 == 0 ? 1f : 0f)).ToList();
        foreach (var o in s.Objects)
        {
            var row = new List<string> { "OBJ", N, o.Name, o.Type, o.Parent?.Name ?? "-", o.ParentBone != null ? "BONE" : "OBJECT", o.ParentBone ?? "-", o.DataName ?? "-", o.Action ?? "-", o.Slot ?? "-" };
            row.AddRange(HexMatrix(o.World)); row.AddRange(o.ParentInverse != null ? HexMatrix(o.ParentInverse) : identity);
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
                // every UV layer by the importer's names (uv_layers.new('UVMap') per TEXCOORD_n: UVMap, UVMap.001, ...), the join uniting them by name
                d.UvAll.Count == 0 ? "-" : string.Join(";", d.UvAll.Select((uv, i) => (i == 0 ? "UVMap" : "UVMap." + i.ToString("000", CultureInfo.InvariantCulture)) + ":" + Sha(Bytes(uv)))),
                // the edges in order, the sharp_edge attribute when the mesh has one (the importer's all-false one included), the custom-normal shorts
                d.Edges == null ? "-" : Sha(Bytes(d.Edges)), d.EdgeSharp == null ? "-" : Sha(d.EdgeSharp.Select(x => x ? (byte)1 : (byte)0).ToArray()),
                d.CustomNormal == null ? "-" : Sha(Bytes(d.CustomNormal)),
                // every colour layer by name (ordinal), its domain and its bytes (4 per vertex, or 4 per corner in face order)
                d.Colors.Count == 0 ? "-" : string.Join(";", d.Colors.OrderBy(c => c.name, StringComparer.Ordinal).Select(c => c.name + ":" + (c.point ? "POINT" : "CORNER") + ":" + Sha(c.bytes))),
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
                var row = new List<string> { "BONE", N, i.ToString(CultureInfo.InvariantCulture), s.Names.BoneOfJoint[b], ad.Rest.Parent[b] >= 0 ? s.Names.BoneOfJoint[ad.Rest.Parent[b]] : "-", ad.NoDeform.Contains(b) ? "0" : "1", Hex(ad.Rest.Length[b]) };
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
        if (n == 4 && s.Albedo.path != null) r.Image = new[] { "IMG", N, s.Albedo.path, "-", "-", s.Albedo.w.ToString(CultureInfo.InvariantCulture), s.Albedo.h.ToString(CultureInfo.InvariantCulture), "4", "-" };
        foreach (var act in s.Actions)
        {
            r.Actions.Add(new[] { "ACT", N, act.Name, Hex(act.FrameStart), Hex(act.FrameEnd), act.FCurves.Count().ToString(CultureInfo.InvariantCulture), act.Slots.Count.ToString(CultureInfo.InvariantCulture) });
            foreach (var slot in act.Slots) foreach (var fc in slot.FCurves)
            {
                var co = new List<byte>(); var hl = new List<byte>(); var hr = new List<byte>(); var meta = new List<string>();
                foreach (var k in fc.Keys)
                {
                    co.AddRange(BitConverter.GetBytes(k.Frame)); co.AddRange(BitConverter.GetBytes(k.Value));
                    hl.AddRange(BitConverter.GetBytes(k.LeftX)); hl.AddRange(BitConverter.GetBytes(k.LeftY));
                    hr.AddRange(BitConverter.GetBytes(k.RightX)); hr.AddRange(BitConverter.GetBytes(k.RightY));
                    meta.Add(k.Interpolation + "/" + KeyData.Easing + "/" + KeyData.HandleType + "/" + KeyData.HandleType);
                    r.Keys.Add(WithSlot(fcSlot, slot.Identifier, new[] { "K", N, act.Name, fc.DataPath, fc.Index.ToString(CultureInfo.InvariantCulture), Hex(k.Frame), Hex(k.Value), Hex(k.LeftX), Hex(k.LeftY), Hex(k.RightX), Hex(k.RightY), k.Interpolation, KeyData.Easing, KeyData.HandleType, KeyData.HandleType }));
                }
                string idx = fc.Index.ToString(CultureInfo.InvariantCulture);
                r.FCurves.Add(WithSlot(fcSlot, slot.Identifier, new[] { "FC", N, act.Name, fc.DataPath, idx, fc.Keys.Count.ToString(CultureInfo.InvariantCulture), Sha(co.ToArray()), Sha(hl.Concat(hr).Concat(Encoding.UTF8.GetBytes(string.Join("|", meta))).ToArray()) }));
            }
        }
        return r;
    }
}
