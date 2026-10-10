// BlenderDeploy.cs - the decisions deploy_convert.py takes before it builds anything (replacing deploy_convert.py,
// part 2): what the strip leaves, the frame range, the unit normalization, which objects are parts, the bone slimming,
// the path, the degenerate cull and the bone budget - on the scene as Blender holds it after the import, with every
// matrix composed and every transform assigned as Blender does. Held to the script itself by tools/deploy_drill.sh:
// tools/deploy-drill/blender_decisions_dump.py runs deploy_convert.py cut where it starts to build the armature and
// writes out what it printed, what it decided and the scene it left.
//
// The scene (measured 2026-10-10 on the registry's deploy sources, read from Blender 5.1.2):
//   - `bpy.data.objects` is sorted by name ignoring case (lib_id.cc id_sort_by_name, BLI_strcasecmp; equal names in
//     creation order) - the order of the survivors, the parts and the roots;
//   - `bpy.data.objects.remove` leaves a removed object's children WITHOUT a parent and with their own transform: they
//     are roots from then on, where their local transform puts them;
//   - the importer's bone shape ("Icosphere", one per armature, radius 1 at the origin, in a hidden collection) is an
//     object like any other: a mesh, counted, bounded, parented to the normalization root. A strip list that does not
//     name it leaves it in - the canoe's vertical offset of 1.00 is its radius;
//   - `o.matrix_world = m` is BKE_object_apply_mat4: the parent's inverse (Eigen's) times m, mat4_to_loc_rot_size,
//     mat3_normalized_to_quat - the object's location, rotation and scale are REPLACED by a decomposition;
//   - mathutils: Matrix @ Vector sums float32 products in a double; Vector +, -, * are float32; Vector.length is the
//     double square root of a double sum of float32 products taken from the LAST component to the first.
//   - the order is by the UTF-8 BYTES as unsigned values, upper-case ASCII folded: a name that starts with a letter
//     past ASCII comes after every ASCII name (measured; review of 2026-10-10 - BlenderExportTree.StrCaseCmp reads the
//     bytes as signed and puts it first, which the exporter's child order was never measured on);
//   - a removed object's NAME is free again: the normalization root is "UnitNormalize" even when the file had one
//     and the strip took it.
//   - the bone shape is evaluated only while its armature is there (the bones' custom shape; its collection is
//     hidden): with the armature stripped its matrix_world stays the identity, whatever it is parented to;
//   - an object that hangs from a BONE follows the armature's pose (VehicleProbe.BlenderPose.cs: the pose bones from
//     the importer's bone curves, BKE_pose_where_is, ob_parbone) - the dugout canoe's ropes and cloth.
// Left to Blender, named (Result.Fallback): what BlenderPosedState does not model; a surviving object under
// a camera in the file, or a skinned mesh (its bound_box is the deformed mesh's); a surviving mesh with morph targets
// (bound_box is the EVALUATED mesh: the shape keys move it); a node outside the scene the file names (the importer
// excludes its collection: such an object is frozen where the import left it, no frame_set reaches it);
// EXT_mesh_gpu_instancing (the importer makes an object per instance); a light; a mesh without vertices; strip names
// whose lower case Python and .NET may give differently.
// Not modelled, to know: of the culled names' repr only the C0 control characters are escaped, not every character
// Python holds unprintable; the parent's inverse in the cull takes BlenderEigen's determinant (a double cofactor) for
// "is it singular", where Eigen's own is float32.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public static class BlenderDeploy
{
    public sealed class Obj
    {
        public string Name, Type, DataName;       // Type as Blender prints it: MESH, EMPTY, ARMATURE, CAMERA
        public Obj Parent;
        public int BoneNode = -1;                  // the bone (its glTF node) of Parent - an armature - it hangs from, or -1
        public float[] BoneMatrix;                 // that bone's pose matrix moved to its tail (ob_parbone), armature space
        /// <summary>BKE_object_get_parent_matrix: the parent's matrix_world, through the bone when it hangs from one.</summary>
        public float[] ParentMatrix => BoneNode >= 0 && BoneMatrix != null ? VehicleProbe.MulM4(Parent.World, BoneMatrix) : Parent.World;
        public float[] ParentInverse;              // matrix_parent_inverse when a script set one (the root-motion anchor), else the identity
        public int Node = -1, MeshNode = -1;       // the glTF node that became it / whose mesh it carries, or -1
        public bool HasAction;
        public Obj ShapeOf;                        // a bone shape: the armature whose bones show it
        public bool Frozen;                        // no longer evaluated: matrix_world stays what it was (a bone shape whose armature is gone)
        public bool Euler;                         // made by an operator or by the script, not by the importer: no quaternion mode
        public float[] Loc, Quat, Scale;           // its own transform as the properties hold it
        public float[] World;                      // matrix_world, column-major
        public float[] BoxMin, BoxMax;             // a mesh object's bound_box
    }

    public sealed class Result
    {
        public string Fallback;                    // why this file is Blender's to convert, or null
        public bool Exit;                          // the script stops itself: no animated part is left
        public readonly List<string> Log = new List<string>();   // the DEPLOY lines and the part lines, as the script prints them
        public int FrameMin, FrameMax;
        public double NormDim, NormScale, OffsetH, OffsetV; public bool Recenter;
        public bool Legacy;
        public List<Obj> Objects = new List<Obj>();              // every object left, in bpy.data.objects order
        public List<Obj> Parts = new List<Obj>();                // the parts left, in the script's order
        public List<string> Bad = new List<string>();            // the culled parts, sorted as Python sorts them
        public List<(Obj dropped, Obj kept)> Alias = new List<(Obj, Obj)>();
        // ---- the armature the script builds (part 3): one bone per part at the part's place, StaticRoot, the anchors
        public Obj Armature;                                      // "DeployArm" (the legacy path) or "DeployArmV2"
        public readonly List<Bone> Bones = new List<Bone>();      // creation order: the parts', then StaticRoot
        public readonly List<(string part, string bone)> BoneOf = new List<(string, string)>();   // which bone each part rides, the merged ones last
        public Obj StaticAnchor;                                  // what StaticRoot is constrained to, or null
        public Obj Hull;                                          // the root-motion anchor the armature is parented to for the bake, or null
        public bool TravelMeasured; public double Travel, ModelSize;
        // ---- the bake (part 4): per bone, by name, a key per frame FrameMin..FrameMax - location 3, rotation_quaternion
        //      (w, x, y, z) 4, scale 3 - as the action holds them when the script reaches its step 5a
        public readonly Dictionary<string, float[][]> Keys = new Dictionary<string, float[][]>(StringComparer.Ordinal);
        public bool ScaleKeys = true;                             // false on the contract path: the scale curves are stripped
        // the frames the bake keyed: FrameMin..FrameMax as the bake operator takes them - its frame_end is at least 1,
        // so a clip whose whole range is frame 0 is baked on frames 0 AND 1
        public int BakeFrameMin, BakeFrameMax;
        public readonly List<string> BakeLog = new List<string>();
        // the bake takes every SELECTED armature, and the importer leaves all selected: an imported armature that
        // survived the strip is keyed too - per armature object, per bone, the keys as Keys holds them
        public readonly Dictionary<string, Dictionary<string, float[][]>> ImportedKeys = new Dictionary<string, Dictionary<string, float[][]>>(StringComparer.Ordinal);
        // ... and what each of those bones HOLDS when the conversion goes on from here (location, quaternion, scale):
        // the later steps start from this state
        public readonly Dictionary<string, Dictionary<string, float[][]>> ImportedPose = new Dictionary<string, Dictionary<string, float[][]>>(StringComparer.Ordinal);
        /// <summary>`scene.frame_set(frame)` on the scene the conversion has reached, for a drill: every object's
        /// matrix_world by name and what each imported pose bone holds. It MOVES the scene - nothing may follow it.</summary>
        public Func<int, (Dictionary<string, float[]> worlds, Dictionary<string, Dictionary<string, float[][]>> pose)> ProbeAt;
        /// <summary>scene.frame_set(frame) once more: what the new armature's pose bones hold then (location,
        /// quaternion, scale). A re-keyed bone follows its Bezier curves, BlenderFCurve.Evaluate.</summary>
        public Func<int, Dictionary<string, float[]>> ArmAt;
        // the scene as the BAKE left it (every object's matrix_world by name): the steps after it move it on
        public Dictionary<string, float[]> AfterBake;
        // ---- 5a, the fire-window snapshot: per source frame, per bone of the new armature, its location (3) and
        //      rotation_quaternion (4) as the baked action gives them there; the segments as the script reads them
        public readonly SortedDictionary<int, Dictionary<string, float[]>> FireSnap = new SortedDictionary<int, Dictionary<string, float[]>>();
        public readonly List<(int start, int end, int step)> Segments = new List<(int, int, int)>();
        public readonly List<string> FireLog = new List<string>();
        // what each pose bone of the new armature HOLDS now (location 3, quaternion 4, scale 3), and the scene after 5a
        public readonly Dictionary<string, float[]> ArmPose = new Dictionary<string, float[]>(StringComparer.Ordinal);
        public Dictionary<string, float[]> AfterFire;
        public Dictionary<string, float[]> ArmPoseAfterFire;
        // ---- 5b, 5c: the barrel retargeted to its ready frame, the leg spread scaled. A bone these steps touch has its
        //      curves CLEARED and a few Bezier keys in their place: per bone, per channel (location 3, quaternion 4,
        //      scale 3) the keys left, or null where the channel has no curve any more and the property just holds
        public readonly Dictionary<string, List<ArmKey>[]> Rekeyed = new Dictionary<string, List<ArmKey>[]>(StringComparer.Ordinal);
        public readonly List<string> RetargetLog = new List<string>();
        public Dictionary<string, float[]> AfterRetarget;
        /// <summary>What the pose bones held after 5b/5c, before the recoil step moved the scene.</summary>
        public Dictionary<string, float[]> ArmPoseAfterRetarget;
        // ---- 5d, the recoil tail
        public readonly List<string> RecoilLog = new List<string>();
        public bool ExitAtRecoil;                                   // the script's own exit (no barrel to pick)
        public RecoilResult Recoil;                                 // what the step measured on the way (null without one)
        /// <summary>The bones after the recoil step: every bone rebuilt from its edit bone, the RecoilArm among them -
        /// the same list as Bones when the step did not run.</summary>
        public List<Bone> BonesAfterRecoil;
        public Dictionary<string, float[]> AfterRecoil;
        /// <summary>What the pose bones held after the recoil step, before the bind set the bind frame.</summary>
        public Dictionary<string, float[]> ArmPoseAfterRecoil;
        // ---- 6, the bind: each mesh bound to the bone of its nearest animated ancestor (self included), else StaticRoot
        public readonly List<string> BindLog = new List<string>();
        public readonly List<Bound> Bound = new List<Bound>();                   // in the script's `meshes` order (bpy.data.objects')
        public List<(string name, int users)> MeshData;                           // every mesh datablock after the bind, with its users
        /// <summary>Every object's matrix_world after the bind, the scene updated at the bind frame: the meshes under
        /// the armature, each at the armature's world times the inverse the bind gave it.</summary>
        public Dictionary<string, float[]> AfterBind;
    }

    /// <summary>A mesh the bind bound: its object (now under the armature, at the identity), its one vertex group - named
    /// after the PART found (anim_ancestor returns the object's name, not its bone's: a pair-merged part names a group no
    /// bone has), cut to the 63 bytes a group name holds, or StaticRoot -, its datablock's name (a copy's, numbered as Blender numbers a copy, when the datablock was shared with
    /// another live object), the matrix folded into its vertices, and the vertices after it (Blender's frame, the
    /// importer's order, math::transform_point per vertex); the custom normals as the importer encoded them (INT16_2D per
    /// corner, relative to the face normals), which mesh_transform leaves alone - null without file normals.</summary>
    public sealed class Bound
    {
        public Obj Mesh; public string Group, DataName; public bool Copied;
        public float[] World;                                     // matrix_world at the bind frame, column-major
        public float[] Positions; public int Corners; public short[] CustomNormal;
    }

    /// <summary>The recoil step's measurements, as the script's own variables hold them (matrices in mathutils' item
    /// order, row-major; vectors float32; the lengths and angles Python doubles).</summary>
    public sealed class RecoilResult
    {
        public int Rs, Re, Step, DeployEnd; public long KickEnd, OutEnd;
        public List<int> Frames;
        public string Driver, Cradle, TubeRoot, ArmName; public List<string> Ordered;
        public double Mag, Dist, R;
        public Dictionary<string, float[]> Home, Aim;
        public Dictionary<string, Dictionary<int, float[]>> Src; public List<string> SrcOrder;
        public Dictionary<int, float[]> Slide; public List<int> SlideOrder;
        public float[] Peak, D, A, Radius, TubeHead, Pivot, ALocal, Cbar3;
        public readonly List<double> Thetas = new List<double>();
        public readonly Dictionary<int, double> ArcBySrc = new Dictionary<int, double>();
    }

    /// <summary>A key `keyframe_insert` made: Bezier, both handles AUTO_CLAMPED.</summary>
    public sealed class ArmKey
    {
        public float Frame, Value, LeftX, LeftY, RightX, RightY;
    }

    sealed class NotPortedException : Exception
    {
        public NotPortedException(string what) : base(what) { }
    }

    /// <summary>A bone of the armature at rest.</summary>
    public sealed class Bone
    {
        public string Name; public Bone Parent; public Obj Part;   // Part: the object it copies (null for StaticRoot)
        public float[] Head, Tail;                                 // head_local, tail_local: the edit bone's, armature space
        public float Length;
        public float[] MatrixLocal;                                // bone.matrix_local, column-major
        public float[] Offs;                                       // its offset matrix in the parent (BKE_bone_offset_matrix_get; a root's: matrix_local)
    }

    const int BoneWall = 124, PartBudget = 124;
    static readonly string[] DefaultKill = { "solder", "soldier", "pole", "string", "shell", "dynam", "ammun", "pcylinder1", "pcylinder3", "icosphere", "basicgal", "polysurface" };
    // bpy.ops.mesh.primitive_ico_sphere_add(radius=1): its bound_box, measured (bf737887 bf7fffff bf800000 / 3f737887 3f7fffff 3f800000)
    static float Bits(uint b) => BitConverter.ToSingle(BitConverter.GetBytes(b), 0);
    static readonly float[] IcoMin = { Bits(0xbf737887), Bits(0xbf7fffff), -1f }, IcoMax = { Bits(0x3f737887), Bits(0x3f7fffff), 1f };

    /// <summary>The script's decisions for a model and the arguments the Factory gives it after the input and the output
    /// (args[0] is the script's argv[2]).</summary>
    public static Result Decide(HafModel m, string[] args, BlenderNames.Result names = null) => Decide(m, args, names, false);

    /// <summary>... and with `bake` the conversion goes on through the bake, the scale-free step and the delta-form
    /// rebase: Result.Keys, as the action stands when the script reaches its step 5a.</summary>
    public static Result Decide(HafModel m, string[] args, BlenderNames.Result names, bool bake)
    {
        var r = new Result();
        names = names ?? BlenderNames.Compute(m);
        string Arg(int scriptIndex) => scriptIndex - 2 < args.Length ? args[scriptIndex - 2] : null;
        int argc = args.Length + 2;
        // The importer divides by each component of the armature's float32 scale (the bone shape's size): a zero
        // there fails, whether it came from TRS, matrix decomposition, or a nonzero double that underflowed.
        foreach (var (armNode, armName) in names.ArmaturesInOrder)
            if (armNode >= 0 && VehicleProbe.ArmatureScale(m, armNode).Any(c => c == 0f)) { r.Fallback = $"an armature with a zero scale ('{armName}': Blender's importer fails on it)"; return r; }
        var rig = VehicleProbe.BuildImportRig(m, names);
        var action = BlenderPosedState.Import(m, 0, 24.0, rig);
        if (action.NotModelled != null) { r.Fallback = action.NotModelled; return r; }
        if (m.ExtensionsUsed.Contains("KHR_lights_punctual")) { r.Fallback = "a light (the reader does not carry which node holds it)"; return r; }
        if (m.ExtensionsUsed.Contains("EXT_mesh_gpu_instancing")) { r.Fallback = "EXT_mesh_gpu_instancing (the importer makes an object of every instance)"; return r; }
        if (m.HasDefaultScene)
        {
            // the importer excludes every collection but the named scene's: what is outside it is frozen at the import
            var inScene = new bool[m.Nodes.Count]; var walk = new Stack<int>(m.Scenes[m.Scene].Nodes);
            while (walk.Count > 0) { int n = walk.Pop(); if (n < 0 || n >= inScene.Length || inScene[n]) continue; inScene[n] = true; foreach (int c in m.Nodes[n].Children) walk.Push(c); }
            int outside = Array.IndexOf(inScene, false);
            if (outside >= 0) { r.Fallback = $"a node outside the scene the file names ('{m.Nodes[outside].Name}': its collection is excluded, no frame reaches it)"; return r; }
        }

        bool recoilOff = argc <= 10 || PyStrip(Arg(10)) == "" || PyStrip(Arg(10)) == "0";
        bool hadRecoil = argc > 8 && PyStrip(Arg(8)) != "";
        if (recoilOff && hadRecoil) r.Log.Add("DEPLOY recoil step empty/0 — fire cycle DISABLED ('recoil' role = held stance)");

        // ---- the scene after the import
        var animatedArmatures = new HashSet<int>();
        foreach (int b in names.BoneNodesInOrder) if (action.Animates(b)) animatedArmatures.Add(names.ArmatureNodeOfBone[b]);
        var all = new List<Obj>(); var byName = new Dictionary<string, Obj>(StringComparer.Ordinal);
        var source = new Dictionary<Obj, BlenderNames.BlenderObject>();
        foreach (var o in names.Objects)
        {
            var x = new Obj { Name = o.Name, DataName = o.DataName, Node = o.GltfNode, MeshNode = o.MeshNode };
            x.Type = o.Kind == BlenderNames.ObjectKind.Mesh ? "MESH" : o.Kind == BlenderNames.ObjectKind.Armature ? "ARMATURE" : o.Kind == BlenderNames.ObjectKind.Camera ? "CAMERA" : "EMPTY";
            x.HasAction = o.Kind == BlenderNames.ObjectKind.Armature ? animatedArmatures.Contains(o.GltfNode) || (o.GltfNode >= 0 && action.Animates(o.GltfNode))
                        : o.GltfNode >= 0 && !(o.Kind == BlenderNames.ObjectKind.Mesh && o.Skin >= 0) && action.Animates(o.GltfNode);
            if (o.GltfNode >= 0 && o.ParentBone != null)
            {
                // under a bone: turned by the bone's prettify rotation and moved back by its length
                x.BoneNode = rig.ParentBone(o.GltfNode);
                if (x.BoneNode < 0) { r.Fallback = $"an object under a bone that is not its node's parent ('{o.Name}')"; return r; }
                rig.StaticProperty(o.GltfNode, out x.Loc, out x.Quat, out x.Scale);
            }
            else if (o.ParentBone != null) { r.Fallback = $"an object the importer made under a bone ('{o.Name}')"; return r; }
            else if (o.GltfNode >= 0) BlenderPosedState.ImportedTrs(m.Nodes[o.GltfNode], out x.Loc, out x.Quat, out x.Scale);
            else { x.Loc = new float[3]; x.Quat = new[] { 1f, 0f, 0f, 0f }; x.Scale = new[] { 1f, 1f, 1f }; }
            all.Add(x); byName[x.Name] = x; source[x] = o;
        }
        foreach (var x in all) if (source[x].Parent != null) x.Parent = byName[source[x].Parent];
        for (int i = 0; i < names.BoneShapes.Count; i++)
        {
            var x = new Obj { Name = names.BoneShapes[i], Type = "MESH", DataName = names.BoneShapeData[i], Euler = true, Loc = new float[3], Quat = new[] { 1f, 0f, 0f, 0f }, Scale = new[] { 1f, 1f, 1f }, BoxMin = IcoMin, BoxMax = IcoMax };
            // its armature's bones use it as their custom shape: that is all that keeps it evaluated (its collection is hidden)
            if (i < names.ArmaturesInOrder.Count && byName.TryGetValue(names.ArmaturesInOrder[i].name, out var owner)) x.ShapeOf = owner;
            all.Add(x); byName[x.Name] = x;
        }
        // bpy.data.objects: by name ignoring case, equal names in creation order (ObjectsInOrder has the bone shapes in place)
        var creation = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < names.ObjectsInOrder.Count; i++) creation[names.ObjectsInOrder[i]] = i;
        all = all.OrderBy(x => creation.TryGetValue(x.Name, out int c) ? c : int.MaxValue).ToList();
        BlenderExportTree.StableSort(all, (a, b) => IdNameCmp(a.Name, b.Name));

        // ---- 1. the strip: object and data names, lower case, containing a kill substring
        // the ASCII check BEFORE the lower case: .NET may fold a letter to ASCII that Python does not
        if ((Arg(4) ?? "").Concat(Arg(16) ?? "").Any(c => c > 127)) { r.Fallback = "strip: a strip substring that is not ASCII (Python's lower case and .NET's can differ there)"; return r; }
        string[] kill = argc > 4 && PyStrip(Arg(4)) != "" ? Arg(4).Split(',').Select(k => AsciiLower(PyStrip(k))).ToArray() : DefaultKill;
        string[] extra = argc > 16 && PyStrip(Arg(16)) != "" ? Arg(16).Split(',').Select(k => AsciiLower(PyStrip(k))).ToArray() : new string[0];
        if (extra.Length > 0)
        {
            kill = kill.Concat(extra).ToArray();
            r.Log.Add("DEPLOY stripExtra: also removing parts matching " + string.Join(", ", extra));
        }
        bool Special(string s) => s != null && (s.IndexOf('K') >= 0 || s.IndexOf('İ') >= 0);
        if (all.Exists(o => Special(o.Name) || Special(o.DataName))) { r.Fallback = "strip: a name with a letter whose lower case is ASCII in Python only"; return r; }
        bool IsKill(Obj o) => kill.Any(k => AsciiLower(o.Name).Contains(k) || (o.DataName != null && AsciiLower(o.DataName).Contains(k)));
        var gone = new HashSet<Obj>(all.Where(IsKill));
        // what survives must be something the matrices here model: no bone or camera above it in the file, no skin
        foreach (var x in all)
        {
            if (gone.Contains(x) || !source.TryGetValue(x, out var o)) continue;
            if (o.Kind == BlenderNames.ObjectKind.Mesh && o.Skin >= 0) { r.Fallback = $"a skinned mesh survives the strip ('{x.Name}': its bound_box is the deformed mesh's)"; return r; }
            if (o.Kind == BlenderNames.ObjectKind.Mesh && m.Meshes[m.Nodes[o.MeshNode].Mesh].Primitives.Any(p => p.MorphTargets > 0)) { r.Fallback = $"a mesh with morph targets survives the strip ('{x.Name}': its bound_box is the evaluated mesh's, shape keys applied)"; return r; }
            if (o.Kind == BlenderNames.ObjectKind.Camera) { r.Fallback = $"a camera survives the strip ('{x.Name}': the importer's camera correction is not modelled)"; return r; }
            for (var q = o; q != null; q = q.Parent != null && byName.TryGetValue(q.Parent, out var up) ? source[up] : null)
            {
                if (q != o && q.Kind == BlenderNames.ObjectKind.Camera) { r.Fallback = $"an object under a camera survives the strip ('{x.Name}': the importer's camera correction is not modelled)"; return r; }
            }
        }
        all = all.Where(o => !gone.Contains(o)).ToList();
        foreach (var o in all) if (o.Parent != null && gone.Contains(o.Parent)) { o.Parent = null; o.BoneNode = -1; }   // a removed object's children are roots
        // a bone shape whose armature is gone is evaluated no more: its matrix_world stays the identity of the import
        foreach (var o in all) if (o.ShapeOf != null && gone.Contains(o.ShapeOf)) { o.Frozen = true; o.World = VehicleProbe.IdentityF(); }
        var meshes = all.Where(o => o.Type == "MESH").ToList();
        r.Log.Add($"DEPLOY after strip: {all.Count} objects, {meshes.Count} meshes: {string.Join(", ", meshes.Select(o => o.Name))}");
        foreach (var o in meshes)
        {
            if (o.BoxMin != null) continue;
            if (!LocalBox(m, o.MeshNode, out o.BoxMin, out o.BoxMax)) { r.Fallback = $"a mesh without vertices survives the strip ('{o.Name}')"; return r; }
        }

        // ---- 2. the frame range: the surviving objects' action - ONE action per animation, so its range is the whole
        //         animation's (the curves of removed objects, of bones and of morph weights count), cut by int()
        double fminD = 1e9, fmaxD = -1e9;
        if (all.Exists(o => o.HasAction)) { fminD = action.FrameStart; fmaxD = action.FrameEnd; }
        if (fminD > fmaxD) { fminD = 1.0; fmaxD = 1.0; }
        int fmin = (int)Math.Truncate(fminD), fmax = (int)Math.Truncate(fmaxD);
        r.FrameMin = fmin; r.FrameMax = fmax;
        var pose = new BlenderPosedState.Pose(action, m, rig);
        void FrameSet(int f)
        {
            var set = pose.FrameSet(f);
            for (int n = 0; n < m.Nodes.Count; n++) set(n);   // every node: the pose keeps what each property held
            foreach (var o in all)
            {
                if (o.Node < 0) continue;
                var p = set(o.Node);
                if (p == null) continue;
                if (p[0] != null) o.Loc = p[0];
                if (p[1] != null) o.Quat = p[1];
                if (p[2] != null) o.Scale = p[2];
            }
            Repose(b => set(b));
        }
        // the armatures' poses: each bone's pose matrix from its pose bone's properties (`props`: a bone's animated
        // values, null where nothing animates them), for what hangs from it - and the scene's matrices after it
        Dictionary<int, float[]> poseNow = new Dictionary<int, float[]>();
        void Repose(Func<int, float[][]> props)
        {
            if (rig.Armatures.Count > 0)
            {
                var poseMats = new Dictionary<int, float[]>();
                foreach (var arm in rig.Armatures.Values)
                {
                    float[][] Props(int b) { var p = props(b); rig.StaticProperty(b, out var l, out var q, out var s); return new[] { p?[0] ?? l, p?[1] ?? q, p?[2] ?? s }; }
                    foreach (var kv in VehicleProbe.PoseMatrices(arm, Props)) poseMats[kv.Key] = kv.Value;
                }
                foreach (var o in all) if (o.BoneNode >= 0 && o.Parent != null) o.BoneMatrix = VehicleProbe.ParBone(poseMats[o.BoneNode], rig.ArmatureOfBone[o.BoneNode].Length[o.BoneNode]);
                poseNow = poseMats;
            }
            Update(all);
        }
        FrameSet(fmin);
        r.Log.Add($"DEPLOY frame range: {fmin}..{fmax}");

        // ---- 2b. unit normalization: the world box of every mesh object's bound_box
        var mn = new[] { 1e18f, 1e18f, 1e18f }; var mx = new[] { -1e18f, -1e18f, -1e18f };
        foreach (var o in meshes)
        {
            // bound_box: the eight corners, x outermost (BKE_boundbox_init_from_minmax)
            for (int c = 0; c < 8; c++)
            {
                float cx = (c & 4) != 0 ? o.BoxMax[0] : o.BoxMin[0], cy = (c == 1 || c == 2 || c == 5 || c == 6) ? o.BoxMax[1] : o.BoxMin[1], cz = (c == 2 || c == 3 || c == 6 || c == 7) ? o.BoxMax[2] : o.BoxMin[2];
                var w = MatVec(o.World, cx, cy, cz);
                for (int k = 0; k < 3; k++) { if (w[k] < mn[k]) mn[k] = w[k]; if (w[k] > mx[k]) mx[k] = w[k]; }
            }
        }
        double dim = 0.0;
        if (mx[0] > mn[0]) { var d = new[] { (float)(mx[0] - mn[0]), (float)(mx[1] - mn[1]), (float)(mx[2] - mn[2]) }; dim = d[0]; if (d[1] > dim) dim = d[1]; if (d[2] > dim) dim = d[2]; }
        double scale = 0.0 < dim && dim < 0.5 ? 100.0 : 1.0;
        var ctr = new[] { (float)((float)(mn[0] + mx[0]) * 0.5f), (float)((float)(mn[1] + mx[1]) * 0.5f), (float)((float)(mn[2] + mx[2]) * 0.5f) };
        // Vector((c.x, c.y, 0.0)).length: the double root of a double sum of float32 products, last component first
        double offH = Math.Sqrt(0.0 + (double)(float)(0f * 0f) + (double)(float)(ctr[1] * ctr[1]) + (double)(float)(ctr[0] * ctr[0])) * scale;
        double offV = Math.Abs((double)mn[2]) * scale;
        double dimScaled = dim * scale;
        bool recenter = dimScaled > 0.0 && (offH > 0.15 * dimScaled || offV > 0.15 * dimScaled);
        r.NormDim = dim; r.NormScale = scale; r.OffsetH = offH; r.OffsetV = offV; r.Recenter = recenter;
        var pool = names.ObjectPool.Clone();
        foreach (var g in gone) pool.Remove(g.Name);   // a removed object's name is free again
        if (scale != 1.0 || recenter)
        {
            var root = new Obj { Name = pool.Unique("UnitNormalize"), Type = "EMPTY", Euler = true, Loc = new float[3], Quat = new[] { 1f, 0f, 0f, 0f }, Scale = new[] { 1f, 1f, 1f }, World = VehicleProbe.IdentityF() };
            var roots = all.Where(o => o.Parent == null).ToList();
            // a new ID goes behind the last one whose name is not greater
            int at = all.Count; while (at > 0 && IdNameCmp(all[at - 1].Name, root.Name) > 0) at--;
            all.Insert(at, root);
            foreach (var o in roots)
            {
                var keep = o.World;
                o.Parent = root;
                ApplyMat4(o, keep, root.World);
                if (o.Node >= 0) pose.Write(o.Node, o.Loc, o.Quat, o.Scale);
            }
            root.Scale = new[] { (float)scale, (float)scale, (float)scale };
            if (recenter) root.Loc = new[] { (float)(-(double)ctr[0] * scale), (float)(-(double)ctr[1] * scale), (float)(-(double)mn[2] * scale) };
            Update(all);
            r.Log.Add($"DEPLOY normalization: dim {PyFormat.Fixed(dim, 3)} -> x{PyFormat.Fixed(scale, 0)} scale" + (recenter ? $", recentered (offset was h={PyFormat.Fixed(offH, 2)} v={PyFormat.Fixed(offV, 2)})" : ""));
        }

        // ---- 3. parts and meshes
        var parts = all.Where(o => o.HasAction).ToList();
        meshes = all.Where(o => o.Type == "MESH").ToList();
        r.Log.Add($"DEPLOY animated parts: {parts.Count}, meshes: {meshes.Count}");
        if (parts.Count > BoneWall)
        {
            var partSet = new HashSet<Obj>(parts); var needed = new HashSet<Obj>();
            foreach (var mo in meshes)
                for (var o = mo; o != null; o = o.Parent)
                    if (partSet.Contains(o)) { needed.Add(o); break; }
            int dropped = parts.Count - needed.Count;
            if (dropped > 0)
            {
                parts = parts.Where(needed.Contains).ToList();
                r.Log.Add($"DEPLOY bone slimming: kept {parts.Count} binding-target node(s), skipped {dropped} wrapper/ancestor node(s) — over the {BoneWall}-bone wall (world-space keys carry the ancestors' motion)");
            }
        }
        else r.Log.Add($"DEPLOY bone slimming: SKIPPED — {parts.Count} parts is under the {BoneWall}-bone wall; keeping every part bone (small rigs need their own bones, e.g. the m114 barrel/legs)");
        r.Legacy = parts.Count <= BoneWall;
        r.Log.Add("DEPLOY path: " + (r.Legacy ? "LEGACY (DeployArm — pre-contract, small rig e.g. m114: no delta-form/scale-free, Fix100x ON)" : "CONTRACT (DeployArmV2)"));
        foreach (var p in parts) r.Log.Add($"   part: {PyPad(p.Name, 40)} parent={(p.Parent != null ? p.Parent.Name : "None")}");

        // ---- the degenerate cull: every seventh frame and the last, by the part's basis, local and world matrix
        var bad = new HashSet<Obj>();
        var frames = new List<int>(); for (int f = fmin; f < fmax + 1; f += 7) frames.Add(f); frames.Add(fmax);
        foreach (int f in frames)
        {
            FrameSet(f);
            foreach (var p in parts)
            {
                if (bad.Contains(p)) continue;
                var basis = VehicleProbe.ObjectMatrix(p.Loc, p.Euler ? new[] { 1f, 0f, 0f, 0f } : p.Quat, p.Scale);
                var local = p.Parent != null ? VehicleProbe.MulM4(BlenderEigen.InvertM4(p.ParentMatrix), p.World) : p.World;
                if (MatBad(basis) || MatBad(local) || MatBad(p.World)) bad.Add(p);
            }
        }
        FrameSet(fmin);
        if (bad.Count > 0)
        {
            bool Doomed(Obj o) { for (var q = o; q != null; q = q.Parent) if (bad.Contains(q)) return true; return false; }
            var victims = all.Where(Doomed).ToList();
            r.Bad = bad.Select(o => o.Name).OrderBy(n => n, CodePointOrder).ToList();
            r.Log.Add($"DEPLOY culled {bad.Count} degenerate part(s) (garbage world matrix): [{string.Join(", ", r.Bad.Select(PyRepr))}]  (+{Math.Max(0, victims.Count - bad.Count)} descendant object(s))");
            parts = parts.Where(p => !Doomed(p)).ToList();
            var dead = new HashSet<Obj>(victims);
            foreach (var o in victims) pool.Remove(o.Name);
            foreach (var o in all) if (o.ShapeOf != null && dead.Contains(o.ShapeOf)) o.Frozen = true;
            all = all.Where(o => !dead.Contains(o)).ToList();
        }

        // ---- 3b. the bone budget: pair-merge the instanced part classes
        if (parts.Count > PartBudget)
        {
            var groups = new List<(string key, List<Obj> members)>(); var index = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in parts)
            {
                string key = p.Name.Split('.')[0];
                if (!index.TryGetValue(key, out int gi)) { index[key] = gi = groups.Count; groups.Add((key, new List<Obj>())); }
                groups[gi].members.Add(p);
            }
            int excess = parts.Count - PartBudget;
            var big = groups.Where(g => g.members.Count >= 8).Select(g => g.members.OrderBy(o => o.Name, CodePointOrder).ToList()).ToList();
            int totalBig = big.Sum(g => g.Count);
            var ordered = big.OrderByDescending(g => g.Count).ToList();   // stable, as Python's sorted
            for (int gi = 0; gi < ordered.Count; gi++)
            {
                if (excess <= 0) break;
                var members = ordered[gi];
                int quota = gi < big.Count - 1 ? Math.Min(excess, Math.Max(1, (int)Math.Round((double)(excess * (long)members.Count) / Math.Max(1, totalBig), MidpointRounding.ToEven))) : excess;
                var cand = new List<Obj>(); for (int i = 1; i < members.Count; i += 2) cand.Add(members[i]);
                int k = Math.Max(1, cand.Count / Math.Max(1, quota));
                var drops = new List<Obj>(); for (int i = 0; i < cand.Count && drops.Count < quota; i += k) drops.Add(cand[i]);
                foreach (var d in drops) r.Alias.Add((d, members[members.IndexOf(d) - 1]));
                excess -= drops.Count;
            }
            if (r.Alias.Count > 0)
            {
                var dropset = new HashSet<Obj>(r.Alias.Select(a => a.dropped));
                parts = parts.Where(p => !dropset.Contains(p)).ToList();
                r.Log.Add($"DEPLOY bone budget: {r.Alias.Count} instanced part(s) pair-merged onto neighbor bones (parts -> {parts.Count}; the 128-index GPU wall)");
            }
            if (parts.Count > PartBudget) r.Log.Add($"DEPLOY bone budget WARNING: still {parts.Count} parts after pair-merge — expect missing geometry past bone 127");
        }
        r.Objects = all; r.Parts = parts;
        if (parts.Count == 0)
        {
            r.Log.Add("DEPLOY ERROR: no animated parts to convert (0 after filtering) — the source has no per-part TRS animation the deploy conversion can use (node/matrix-level animation is unsupported), or every part was culled. Aborting instead of exporting a static single-bone rig.");
            r.Exit = true;
            return r;
        }

        // ---- 4. the armature: a bone per part at the part's place (translation only, 0.1 along Z), the hierarchy mirrored
        string deployArm = r.Legacy ? "DeployArm" : "DeployArmV2";
        var arm = new Obj { Name = pool.Unique(deployArm), Type = "ARMATURE", DataName = names.ArmaturePool.Clone().Unique(deployArm), Euler = true, Loc = new float[3], Quat = new[] { 1f, 0f, 0f, 0f }, Scale = new[] { 1f, 1f, 1f }, World = VehicleProbe.IdentityF() };
        { int at = all.Count; while (at > 0 && IdNameCmp(all[at - 1].Name, arm.Name) > 0) at--; all.Insert(at, arm); }
        r.Armature = arm;
        var boneNames = new HashSet<string>(StringComparer.Ordinal); var boneOf = new Dictionary<Obj, Bone>();
        var head = new Dictionary<int, float[]>(); var tail = new Dictionary<int, float[]>(); var roll = new Dictionary<int, float>();
        void AddBone(Obj part, string name, float[] h)
        {
            var b = new Bone { Name = BlenderNames.UniqueBone(boneNames, name), Part = part };
            // Vector + Vector((0, 0, 0.1)) in float32
            head[r.Bones.Count] = h; tail[r.Bones.Count] = new[] { (float)(h[0] + 0f), (float)(h[1] + 0f), (float)(h[2] + 0.1f) }; roll[r.Bones.Count] = 0f;
            r.Bones.Add(b);
            if (part != null) { boneOf[part] = b; r.BoneOf.Add((part.Name, b.Name)); }
        }
        foreach (var p in parts) AddBone(p, p.Name, new[] { p.World[12], p.World[13], p.World[14] });
        foreach (var p in parts) if (p.Parent != null && boneOf.TryGetValue(p.Parent, out var pb)) boneOf[p].Parent = pb;
        foreach (var (d, k) in r.Alias) if (boneOf.TryGetValue(k, out var kb)) { boneOf[d] = kb; r.BoneOf.Add((d.Name, kb.Name)); }
        AddBone(null, "StaticRoot", new float[3]);
        {
            var ids = Enumerable.Range(0, r.Bones.Count).ToList();
            var kids = ids.ToDictionary(i => i, i => new List<int>());
            foreach (int i in ids) if (r.Bones[i].Parent != null) kids[r.Bones.IndexOf(r.Bones[i].Parent)].Add(i);
            var rootIds = ids.Where(i => r.Bones[i].Parent == null).ToList();
            VehicleProbe.RestFromEditBones(ids, kids, rootIds, head, tail, roll, (b, parent, armMat, offs, len) =>
            {
                var bone = r.Bones[b];
                bone.MatrixLocal = armMat; bone.Length = len; bone.Offs = offs;
                // head_local and tail_local are the EDIT bone's (arm_head, arm_tail: copied, never recomputed) - a child's
                // matrix_local goes through its parent and back and may sit an ulp beside its own head
                bone.Head = head[b]; bone.Tail = tail[b];
            });
        }
        // StaticRoot is constrained to the first mesh no bone carries: its parent, or itself
        meshes = all.Where(o => o.Type == "MESH").ToList();
        bool Carried(Obj o, out Obj carrier) { for (carrier = o; carrier != null; carrier = carrier.Parent) if (boneOf.ContainsKey(carrier)) return true; return false; }
        foreach (var mo in meshes)
            if (!Carried(mo, out _)) { r.StaticAnchor = mo.Parent ?? mo; break; }
        if (r.StaticAnchor != null) r.Log.Add($"DEPLOY StaticRoot baked against '{r.StaticAnchor.Name}' (static geometry scale anchor)");

        // ---- the root-motion anchor: the biggest mesh's bone-carrying node; does it travel?
        Obj hull = null; double bigVol = -1.0;
        foreach (var mo in meshes)
        {
            // o.dimensions: the world matrix's axis lengths times the bound box's size, float32
            float[] sz = new float[3];
            for (int k = 0; k < 3; k++)
            {
                float x = mo.World[k * 4], y = mo.World[k * 4 + 1], z = mo.World[k * 4 + 2];
                float len = VehicleProbe.Sqrtf((float)((float)((float)(x * x) + (float)(y * y)) + (float)(z * z)));
                sz[k] = (float)(len * (float)(mo.BoxMax[k] - mo.BoxMin[k]));
            }
            double v = (double)sz[0] * (double)sz[1] * (double)sz[2];
            if (v > bigVol && Carried(mo, out var carrier)) { bigVol = v; hull = carrier; }
        }
        if (hull != null)
        {
            float[] lo = null, hi = null;
            foreach (int f in frames)
            {
                FrameSet(f);
                var t = new[] { hull.World[12], hull.World[13], hull.World[14] };
                if (lo == null) { lo = (float[])t.Clone(); hi = (float[])t.Clone(); }
                else for (int k = 0; k < 3; k++) { if (t[k] < lo[k]) lo[k] = t[k]; if (t[k] > hi[k]) hi[k] = t[k]; }
            }
            FrameSet(fmin);
            var d = new[] { (float)(hi[0] - lo[0]), (float)(hi[1] - lo[1]), (float)(hi[2] - lo[2]) };
            r.Travel = Math.Sqrt(0.0 + (double)(float)(d[2] * d[2]) + (double)(float)(d[1] * d[1]) + (double)(float)(d[0] * d[0]));
            // _dim_now = max(_nrm_mx - _nrm_mn) * _nrm_scale: the box again, WITHOUT the guard the normalization's size has
            // (a model flat in X has size 0 there and its Y/Z extent here; review of part 3)
            double dimNow = (double)(float)(mx[0] - mn[0]);
            for (int k = 1; k < 3; k++) { double e = (double)(float)(mx[k] - mn[k]); if (e > dimNow) dimNow = e; }
            r.ModelSize = dimNow * scale; r.TravelMeasured = true;
            if (r.Travel > 0.10 * Math.Max(r.ModelSize, 1e-6))
            {
                // arm.parent = hull; arm.matrix_parent_inverse = hull.matrix_world.inverted() (mathutils' own inverse)
                var inverse = VehicleProbe.Inverted(VehicleProbe.ToRowMajor(hull.World));
                // Matrix.inverted() RAISES on a float32 determinant of zero: the script dies there
                if (inverse == null) { r.Fallback = $"the root-motion anchor's matrix has no inverse ('{hull.Name}': the script fails on it)"; return r; }
                arm.Parent = hull;
                arm.ParentInverse = VehicleProbe.ToColumnMajor(inverse);
                r.Hull = hull;
                r.Log.Add($"DEPLOY root-motion anchor: '{hull.Name}' travels {PyFormat.Fixed(r.Travel, 2)} units (model {PyFormat.Fixed(r.ModelSize, 2)}) -> clip baked hull-relative (in-place)");
                Update(all);
            }
        }
        r.Objects = all;
        if (!bake) return r;

        // nla.bake with only_selected=False bakes every SELECTED object that has a pose as well - and the importer leaves
        // everything selected: an imported armature that survived the strip is baked too. Its bones get a key a frame
        // (visual, near what they had) and its own OBJECT animation is gone with the action the bake replaces. That is
        // not modelled: where it can show - the armature's node is itself animated, or something still hangs from
        // its bones - the job is Blender's from here (review of the bake, 2026-10-11).
        var imported = all.Where(o => o.Type == "ARMATURE" && o != arm).ToList();
        var importedBasis = new Dictionary<int, float[][]>();   // per bone node of a surviving imported armature: its basis a frame

        // ---- 5. the bake (bpy.ops.nla.bake, visual keying, bake_types POSE): every frame the pose each bone's Copy
        //      Transforms gives it, brought into the bone's own space; then, bone by bone, decomposed into keys
        // NLA_OT_bake's own limits: frame_start is an IntProperty of 0..300000, frame_end of 1..300000, and a value
        // outside is CLAMPED. A clip whose keys all sit within frame 0 has the range 0..0 and is baked on frames 0 and 1
        // (measured; review of PR #137). The rebase below still runs over fmin..fmax, so the extra key stays as baked.
        if (fmin < 0 || fmax > 300000) { r.Fallback = $"a frame range the bake operator clamps ({fmin}..{fmax}: its frame_start is 0..300000)"; return r; }
        int bakeEnd = Math.Max(fmax, 1);
        r.BakeFrameMin = fmin; r.BakeFrameMax = bakeEnd;
        int nf = bakeEnd - fmin + 1, nb = r.Bones.Count, nRebase = fmax - fmin + 1;
        var order = new List<int>();   // a parent before its children
        { var seen = new bool[nb]; void Visit(int i) { if (seen[i]) return; seen[i] = true; if (r.Bones[i].Parent != null) Visit(r.Bones.IndexOf(r.Bones[i].Parent)); order.Add(i); } for (int i = 0; i < nb; i++) Visit(i); }
        var parentIndex = r.Bones.Select(b => b.Parent != null ? r.Bones.IndexOf(b.Parent) : -1).ToArray();
        var bakedBasis = new float[nb][][]; for (int i = 0; i < nb; i++) bakedBasis[i] = new float[nf][];
        for (int f = fmin; f <= bakeEnd; f++)
        {
            FrameSet(f);
            var armInv = BlenderEigen.InvertM4(arm.World);   // world_to_object
            var poseMat = new float[nb][];
            foreach (int i in order)
            {
                var bone = r.Bones[i]; var target = bone.Part ?? r.StaticAnchor;
                // the constraint replaces the bone's matrix in WORLD space by its target's; back to pose space
                if (target != null) { poseMat[i] = VehicleProbe.MulM4(armInv, target.World); continue; }
                // no constraint (StaticRoot without a static mesh): the rest, through an identity channel
                var rs = parentIndex[i] >= 0 ? VehicleProbe.MulM4(poseMat[parentIndex[i]], bone.Offs) : bone.MatrixLocal;
                poseMat[i] = PoseFromChannel(rs, VehicleProbe.IdentityF());
            }
            // obj.convert_space(pose_bone, matrix, 'POSE', 'LOCAL'): BKE_armature_mat_pose_to_bone
            foreach (int i in order)
            {
                var bone = r.Bones[i];
                var rs = parentIndex[i] >= 0 ? VehicleProbe.MulM4(poseMat[parentIndex[i]], bone.Offs) : bone.MatrixLocal;
                bakedBasis[i][f - fmin] = PoseFromChannel(BlenderEigen.InvertM4(rs), poseMat[i]);
            }
            // ... and every surviving imported armature's bones the same way, from the pose the importer's action gives
            foreach (var a in imported)
            {
                var ia = rig.Armatures[a.Node];
                foreach (int b in ia.Bones)
                {
                    var rs = ia.Parent[b] >= 0 ? VehicleProbe.MulM4(poseNow[ia.Parent[b]], ia.OffsBone[b]) : ia.ArmMat[b];
                    if (!importedBasis.TryGetValue(b, out var perFrame)) importedBasis[b] = perFrame = new float[nf][];
                    perFrame[f - fmin] = PoseFromChannel(BlenderEigen.InvertM4(rs), poseNow[b]);
                }
            }
        }
        FrameSet(fmin);   // the bake puts the scene's frame back
        for (int i = 0; i < nb; i++) r.Keys[r.Bones[i].Name] = KeysFromBasis(bakedBasis[i]);
        // the new armature's pose bones: the bake left their properties at the LAST frame's values, and the script's
        // next operator evaluates the new action at the bind frame over them (an equal value is not written)
        foreach (var b in r.Bones)
        {
            var keys = r.Keys[b.Name];
            var held = (float[])keys[nf - 1].Clone();
            for (int c = 0; c < 10; c++) if (held[c] != keys[0][c]) held[c] = keys[0][c];
            r.ArmPose[b.Name] = held;
        }
        // the imported armatures: their bones are keyed a frame from here on, the properties left at the LAST frame's
        // values; the armature OBJECT's own curves went with the action the bake replaced - it stays where the bind
        // frame put it.
        foreach (var a in imported)
        {
            var ia = rig.Armatures[a.Node]; var byBone = new Dictionary<string, float[][]>(StringComparer.Ordinal);
            foreach (int b in ia.Bones)
            {
                var keys = KeysFromBasis(importedBasis[b]);
                byBone[names.BoneOfJoint[b]] = keys;
                pose.Rebake(b, fmin, keys);
            }
            if (a.Node >= 0) pose.Freeze(a.Node);
            r.ImportedKeys[a.Name] = byBone;
        }
        // the new actions are evaluated at once (the script's next operator updates the scene): the bones take their
        // keys of the bind frame, written over the last frame's values
        if (imported.Count > 0) FrameSet(fmin);
        if (r.Hull != null)
        {
            // arm.parent = None; arm.matrix_world = Matrix.Identity(4)
            arm.Parent = null; arm.ParentInverse = null;
            ApplyMat4Root(arm, VehicleProbe.IdentityF());
            Update(all);
        }

        // ---- the scale-free rig and the delta-form rebase (the contract path only)
        if (!r.Legacy)
        {
            r.ScaleKeys = false;
            foreach (var b in r.Bones) { var h = r.ArmPose[b.Name]; h[7] = h[8] = h[9] = 1f; }   // `_pb.scale = (1, 1, 1)`
            // every pose-bone scale curve of EVERY action goes: the new armature's, those the bake gave each surviving
            // imported armature (three a bone), and the importer's own - a bone's animated scale, in any animation,
            // whether its armature is still there or not
            int stripped = 3 * nb;
            foreach (var a in imported) stripped += 3 * rig.Armatures[a.Node].Bones.Count;
            for (int ai = 0; ai < m.Animations.Count; ai++)
                stripped += 3 * (ai == 0 ? action : BlenderPosedState.Import(m, ai, 24.0, rig)).Scale.Keys.Count(n => rig.IsBone(n));
            r.BakeLog.Add($"DEPLOY scale-free rig: {stripped} pose-scale fcurve(s) stripped (verts carry the unit scale)");
            foreach (var a in imported) foreach (int b in rig.Armatures[a.Node].Bones) pose.DropScale(b);   // an imported armature's bones keep the scale they hold
            int rebased = 0;
            foreach (var bone in r.Bones)
            {
                if (AsciiLower(bone.Name).Contains("leg")) continue;
                var keys = r.Keys[bone.Name];
                float[] l0 = { keys[0][0], keys[0][1], keys[0][2] }, q0 = { keys[0][3], keys[0][4], keys[0][5], keys[0][6] };
                if (Magnitude(q0) < 1e-6) continue;
                var n0 = (float[])q0.Clone(); VehicleProbe.NormalizeQt(n0);
                var m0i = VehicleProbe.Inverted(VehicleProbe.TranslationRotation(l0, n0));
                if (m0i == null) { r.Fallback = $"the delta-form rebase: bone '{bone.Name}' has no inverse at the bind frame (the script fails on it)"; return r; }
                float[] prevQ = null;
                var rebasedKeys = (float[][])keys.Clone();   // a key past fmax..: the bake's extra frame is not rebased
                for (int k = 0; k < nRebase; k++)
                {
                    float[] lf = { keys[k][0], keys[k][1], keys[k][2] }, qf = { keys[k][3], keys[k][4], keys[k][5], keys[k][6] };
                    if (Magnitude(qf) < 1e-6) qf = new[] { 1f, 0f, 0f, 0f };
                    var nq = (float[])qf.Clone(); VehicleProbe.NormalizeQt(nq);
                    var mn2 = VehicleProbe.MatMulMathutils(VehicleProbe.TranslationRotation(lf, nq), m0i);
                    // Matrix.decompose(): mat4_to_loc_rot_size and mat3_normalized_to_quat_fast
                    VehicleProbe.Mat4ToLocRotSize(VehicleProbe.ToColumnMajor(mn2), out var ln, out var rot, out _);
                    var qn = VehicleProbe.Mat3NormalizedToQuatFast(rot);
                    if (prevQ != null && VehicleProbe.DotQt(prevQ, qn) < 0f) qn = new[] { -qn[0], -qn[1], -qn[2], -qn[3] };
                    prevQ = qn;
                    // `_d = new - kp.co[1]; kp.co[1] += _d` in Python: a DOUBLE difference added back to the float32 key. Where
                    // the old key is some thirty bits bigger than the new one the difference does not hold the new value's
                    // last bits, and the key comes out an ulp or two beside it (1,103 keys of the T-62 and a chain fixture)
                    float Rekey(float old, float value) => (float)((double)old + ((double)value - (double)old));
                    rebasedKeys[k] = new[] { Rekey(keys[k][0], ln[0]), Rekey(keys[k][1], ln[1]), Rekey(keys[k][2], ln[2]),
                                             Rekey(keys[k][3], qn[0]), Rekey(keys[k][4], qn[1]), Rekey(keys[k][5], qn[2]), Rekey(keys[k][6], qn[3]), keys[k][7], keys[k][8], keys[k][9] };
                }
                r.Keys[bone.Name] = rebasedKeys;
                rebased++;
            }
            r.BakeLog.Add($"DEPLOY delta-form rebase: {rebased} bone(s) rebased to identity-at-f0 deltas (bind == frame 0)");
        }
        else
        {
            r.BakeLog.Add("DEPLOY scale-free rig: SKIPPED (legacy path keeps the cm-verts x0.01 pose scale)");
            r.BakeLog.Add("DEPLOY delta-form rebase: SKIPPED (legacy path — pre-contract engine handling renders absolute poses correctly; bind==f0 would fold the legs' rest and cross them)");
        }
        r.BakeLog.Add($"DEPLOY baked {r.BoneOf.Count} bones");
        foreach (var a in imported) r.ImportedPose[a.Name] = rig.Armatures[a.Node].Bones.ToDictionary(b => names.BoneOfJoint[b], b => pose.Current(b), StringComparer.Ordinal);
        r.AfterBake = all.ToDictionary(o => o.Name, o => (float[])o.World.Clone(), StringComparer.Ordinal);

        // ---- 5a. the fire-window snapshot: argv[8] and argv[9] are lists of starts and ends ("530,441/2": an end with a
        //      speed step); every frame of every segment is set and each bone's location and quaternion taken
        // the new armature's bones follow their baked curves: a key a frame, the ends held; an equal value is not written
        var poseBoneNames = r.Bones.Select(b => b.Name).ToList();
        void EvalArm(int f)
        {
            foreach (string bn in poseBoneNames)
            {
                if (r.Rekeyed.TryGetValue(bn, out var channels))
                {
                    // a re-keyed bone: Bezier keys with constant ends; a channel without a curve holds
                    var h = r.ArmPose[bn];
                    for (int c = 0; c < 10; c++)
                    {
                        var list = channels[c];
                        if (list == null || list.Count == 0) continue;
                        float v = BlenderFCurve.Evaluate(list, (float)f);
                        if (h[c] != v) h[c] = v;
                    }
                    continue;
                }
                if (!r.Keys.TryGetValue(bn, out var keys)) continue;   // a bone without curves (the RecoilArm before its keys)
                var key = keys[Math.Max(0, Math.Min(keys.Length - 1, f - r.BakeFrameMin))]; var held = r.ArmPose[bn];
                for (int c = 0; c < (r.ScaleKeys ? 10 : 7); c++) if (held[c] != key[c]) held[c] = key[c];
            }
        }
        string seg8 = recoilOff && hadRecoil ? "" : (argc > 8 ? Arg(8) : ""), seg9 = argc > 9 ? Arg(9) : "";
        var starts = new List<int>(); var ends = new List<int>(); var steps = new List<int>();
        // Python's int(): white space around the digits is fine - but NOT the separators U+001C..U+001F, which
        // str.strip() removes and int() refuses (the starts are read unstripped: such a start kills the script)
        bool PyInt(string s, out int v)
        {
            s = (s ?? "").Trim(IntWhitespace); v = 0;
            int first = s.Length > 0 && (s[0] == '+' || s[0] == '-') ? 1 : 0;
            // .NET accepts trailing NULs; Python int() refuses them. Unsupported Python forms still fall back.
            for (int i = first; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return false;
            return int.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out v);
        }
        if (PyStrip(seg8) != "")
            foreach (string tok in seg8.Split(','))
            {
                if (PyStrip(tok) == "") continue;
                if (!PyInt(tok, out int v)) { r.Fallback = $"a recoil start the script cannot read as a whole number ('{tok}': it fails there)"; return r; }
                starts.Add(v);
            }
        if (PyStrip(seg9) != "")
            foreach (string raw in seg9.Split(','))
            {
                string tok = PyStrip(raw);
                if (tok == "") continue;
                int slash = tok.IndexOf('/');
                string e = slash >= 0 ? tok.Substring(0, slash) : tok, st = slash >= 0 ? tok.Substring(slash + 1) : "1";
                if (!PyInt(e, out int ev) || !PyInt(st, out int sv)) { r.Fallback = $"a recoil end the script cannot read ('{tok}': it fails there)"; return r; }
                ends.Add(ev); steps.Add(Math.Max(1, sv));
            }
        for (int i = 0; i < Math.Min(starts.Count, ends.Count); i++) r.Segments.Add((starts[i], ends[i], steps[i]));
        int lastSet = fmin;
        if (r.Segments.Count > 0)
        {
            foreach (var (ss, se, _) in r.Segments)
                for (long frame = ss; frame <= se; frame++)   // int.MaxValue is a valid endpoint: do not wrap after it
                {
                    int f = (int)frame;
                    // scene.frame_set clamps its argument; the snapshot dictionary keeps the requested source frame.
                    // Clamp before indexing too: int.MinValue minus a positive bind frame would overflow.
                    int evaluatedFrame = Math.Max(-1048574, Math.Min(1048574, f));
                    FrameSet(evaluatedFrame); EvalArm(evaluatedFrame); lastSet = evaluatedFrame;
                    r.FireSnap[f] = r.Bones.ToDictionary(b => b.Name, b => r.ArmPose[b.Name].Take(7).ToArray(), StringComparer.Ordinal);
                }
            r.FireLog.Add($"DEPLOY fire-window snapshot: {r.FireSnap.Count} frames ({string.Join(", ", r.Segments.Select(s => $"{s.start}..{s.end}/{s.step}"))}) captured PRISTINE (pre-retarget)");
        }
        r.AfterFire = all.ToDictionary(o => o.Name, o => (float[])o.World.Clone(), StringComparer.Ordinal);
        r.ArmPoseAfterFire = r.ArmPose.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone(), StringComparer.Ordinal);

        int current = r.Segments.Count > 0 && r.FireSnap.Count > 0 ? lastSet : fmin;
        void Set(int f) { int e = Math.Max(-1048574, Math.Min(1048574, f)); FrameSet(e); EvalArm(e); current = e; }
        // pb.keyframe_insert(path, frame): the property's value keyed there - a Bezier key, the handles recalculated
        void Insert(string bone, int from, int count, int frame)
        {
            var channels = r.Rekeyed[bone]; var h = r.ArmPose[bone];
            for (int c = from; c < from + count; c++)
            {
                var list = channels[c] ?? (channels[c] = new List<ArmKey>());
                var key = list.FirstOrDefault(k => k.Frame == frame);
                if (key == null) { key = new ArmKey { Frame = frame, Value = h[c] }; list.Add(key); list.Sort((x, y) => x.Frame.CompareTo(y.Frame)); }
                else
                {
                    // a key already on that frame is MOVED by the difference (replace_bezt_keyframe_ypos: dy = new - old,
                    // value += dy, in float32): not the new value to the bit when the difference lies in a higher binade
                    float dy = (float)(h[c] - key.Value);
                    key.Value = (float)(key.Value + dy);
                }
                BlenderFCurve.RecalcHandles(list);
            }
        }
        bool PyFloat(string s, out double v) => ReadPythonFloat(s, out v);
        string Lower(string n) => AsciiLower(n);
        var boneOfValues = r.BoneOf.Select(x => x.bone).ToList();   // bone_of.values(): a merged part's bone comes again
        // ---- 5b, 5c
        try
        {
            // clear_bone_channels: every curve whose data path CONTAINS pose.bones["<name>"] - the name as it is, the
            // path with the name escaped: a name with a quote or a backslash is never found
            bool Escaped(string n) => n.IndexOf('"') >= 0 || n.IndexOf('\\') >= 0;
            void Clear(IEnumerable<string> bones)
            {
                foreach (string bn in bones.Distinct())
                    foreach (var b in r.Bones)
                        if (("pose.bones[\"" + b.Name + "\"]").Contains("pose.bones[\"" + bn + "\"]")) r.Rekeyed[b.Name] = new List<ArmKey>[10];
            }
            // acosf, sinf and the cubic solver's exp, log, acos and cos are the 64-bit Windows C runtime's: elsewhere the
            // rounded doubles are an ulp off now and then (2 of 19,078 values measured under a 32-bit Mono)
            if (!BlenderTrig.Exact && (PyStrip(argc > 5 ? Arg(5) : "") != "" || PyStrip(argc > 6 ? Arg(6) : "") != "" || PyStrip(seg8) != ""))
            { r.Fallback = "a barrel retarget, a leg scale or a recoil in a process without the 64-bit Windows C runtime's float functions (Blender's bits cannot be had)"; return r; }
            if (boneOfValues.Any(n => n.Any(ch => ch > 127)) && (PyStrip(argc > 5 ? Arg(5) : "") != "" || PyStrip(argc > 6 ? Arg(6) : "") != ""))
            { r.Fallback = "a bone name past ASCII with a barrel retarget or a leg scale (Python's lower case of it)"; return r; }

            if (argc > 5 && PyStrip(Arg(5)) != "")
            {
                if (!PyInt(Arg(5), out int readyFrame)) { r.Fallback = $"a ready frame the script cannot read ('{Arg(5)}')"; return r; }
                double barrelScale = 1.0;
                if (argc > 7 && PyStrip(Arg(7)) != "" && !PyFloat(Arg(7), out barrelScale)) { r.Fallback = $"a barrel scale the port does not read as Python's float() does ('{Arg(7)}')"; return r; }
                int endFrame = fmax;
                if (argc > 3 && !PyInt(Arg(3), out endFrame)) { r.Fallback = $"an end frame the script cannot read ('{Arg(3)}')"; return r; }
                int mid = Math.Max((int)Math.Truncate(endFrame * 0.5), 1);
                var barrelBones = boneOfValues.Where(n => Lower(n).Contains("barrel") || Lower(n).Contains("cannon")).ToList();
                if (barrelBones.Any(Escaped)) { r.Fallback = "a barrel bone with a quote or a backslash in its name (its channels are not cleared)"; return r; }
                Set(readyFrame);
                var ready = barrelBones.Distinct().ToDictionary(bn => bn, bn => (float[])r.ArmPose[bn].Clone(), StringComparer.Ordinal);
                Clear(barrelBones);
                // (mode_set does not evaluate the animation: what is assigned below stays held until the next frame_set)
                foreach (string bn in barrelBones)
                {
                    var h = r.ArmPose[bn];
                    h[3] = 1f; h[4] = 0f; h[5] = 0f; h[6] = 0f; h[0] = h[1] = h[2] = 0f;
                    Insert(bn, 3, 4, mid); Insert(bn, 0, 3, mid);
                    var rd = ready[bn];
                    ToAxisAngle(new[] { rd[3], rd[4], rd[5], rd[6] }, out var axis, out float angle);
                    var rq = QuaternionAxisAngle(axis, (double)angle * barrelScale);
                    float bs = (float)barrelScale;
                    h[3] = rq[0]; h[4] = rq[1]; h[5] = rq[2]; h[6] = rq[3];
                    h[0] = (float)(rd[0] * bs); h[1] = (float)(rd[1] * bs); h[2] = (float)(rd[2] * bs);
                    // the location's setter clamps to +-FLT_MAX and a quaternion of a non-finite angle is not modelled
                    for (int c = 0; c < 7; c++) if (float.IsNaN(h[c]) || float.IsInfinity(h[c])) throw new NotPortedException($"a barrel scale that leaves bone '{bn}' a value past a float ('{Arg(7)}')");
                    Insert(bn, 3, 4, endFrame); Insert(bn, 0, 3, endFrame);
                }
                r.RetargetLog.Add($"DEPLOY barrel retargeted to ready-frame {readyFrame} over {mid}..{endFrame} ({barrelBones.Count} bones)");
            }

            if (argc > 6 && PyStrip(Arg(6)) != "")
            {
                if (!PyFloat(Arg(6), out double legScale)) { r.Fallback = $"a leg scale the port does not read as Python's float() does ('{Arg(6)}')"; return r; }
                float fac = (float)legScale;
                if (fac > 1.0f || fac < 0.0f) { r.Fallback = $"a leg scale outside 0..1 ('{Arg(6)}': Quaternion.slerp refuses it and the script fails)"; return r; }
                int endFrame = fmax;
                if (argc > 3 && !PyInt(Arg(3), out endFrame)) { r.Fallback = $"an end frame the script cannot read ('{Arg(3)}')"; return r; }
                int spread = Math.Max((int)Math.Truncate(endFrame * 0.5), 1);
                var legBones = boneOfValues.Where(n => Lower(n).Contains("leg")).ToList();
                if (legBones.Any(Escaped)) { r.Fallback = "a leg bone with a quote or a backslash in its name (its channels are not cleared)"; return r; }
                Set(fmin);
                var folded = legBones.Distinct().ToDictionary(bn => bn, bn => r.ArmPose[bn].Skip(3).Take(4).ToArray(), StringComparer.Ordinal);
                Set(spread);
                var full = legBones.Distinct().ToDictionary(bn => bn, bn => r.ArmPose[bn].Skip(3).Take(4).ToArray(), StringComparer.Ordinal);
                var scaled = legBones.Distinct().ToDictionary(bn => bn, bn => Slerp(folded[bn], full[bn], fac), StringComparer.Ordinal);
                Clear(legBones);
                foreach (string bn in legBones)
                {
                    var h = r.ArmPose[bn];
                    Array.Copy(folded[bn], 0, h, 3, 4); Insert(bn, 3, 4, fmin);
                    Array.Copy(scaled[bn], 0, h, 3, 4); Insert(bn, 3, 4, spread); Insert(bn, 3, 4, endFrame);
                }
                r.RetargetLog.Add($"DEPLOY legs scaled x{PyFormat.Fixed(legScale, 2)} from initial ({legBones.Count} bones), spread by {spread} held to {endFrame}");
            }
        }
        catch (NotPortedException e) { r.Fallback = e.Message; return r; }
        r.AfterRetarget = all.ToDictionary(o => o.Name, o => (float[])o.World.Clone(), StringComparer.Ordinal);

        // ---- 5d. the recoil tail: the source's own kickback over the first fire segment, read as each tube node's world
        //      matrix frame by frame, becomes an arc on a new RecoilArm bone put between the tube and its parent (edit
        //      mode: every bone is rebuilt from its edit bone, an ulp moves) and keyed there - the identity through the
        //      deploy, the arc over the kick, the arc backwards and slower for the return, the identity to settle
        r.ArmPoseAfterRetarget = r.ArmPose.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone(), StringComparer.Ordinal);
        r.BonesAfterRecoil = r.Bones;
        if (PyStrip(seg8) != "")
        {
            try { RecoilStep(); }
            catch (NotPortedException e) { r.Fallback = e.Message; return r; }
        }
        void RecoilStep()
        {
            var rec = new RecoilResult(); r.Recoil = rec;
            float[] Trans(float[] it) => new[] { it[3], it[7], it[11] };
            float[] Sub3(float[] a, float[] b) => new[] { (float)(a[0] - b[0]), (float)(a[1] - b[1]), (float)(a[2] - b[2]) };
            float[] Add3(float[] a, float[] b) => new[] { (float)(a[0] + b[0]), (float)(a[1] + b[1]), (float)(a[2] + b[2]) };
            float[] MulF(float[] a, float s) => new[] { (float)(a[0] * s), (float)(a[1] * s), (float)(a[2] * s) };
            float[] Cross(float[] a, float[] b) => new[] { (float)((float)(a[1] * b[2]) - (float)(a[2] * b[1])), (float)((float)(a[2] * b[0]) - (float)(a[0] * b[2])), (float)((float)(a[0] * b[1]) - (float)(a[1] * b[0])) };
            // Vector.length and Vector.dot: float32 products summed in a double from the LAST component down (dot_vn_vn)
            double Dot3(float[] a, float[] b) { double d = 0.0; for (int i = 2; i >= 0; i--) d += (double)(float)(a[i] * b[i]); return d; }
            double PyLen(float[] a) => Math.Sqrt(Dot3(a, a));
            // Vector.normalized (normalize_vn): the SQUARES in double, summed from the last component (len_squared_vn - not
            // the float32 products .length takes), 1 / float(sqrt) as the float32 scale, zero under 1e-35
            float[] Normalized(float[] a) { double d = 0.0; for (int i = 2; i >= 0; i--) d += (double)a[i] * (double)a[i]; if (!(d > 1.0e-35)) return new[] { 0f, 0f, 0f }; float s = (float)(1.0f / (float)Math.Sqrt(d)); return MulF(a, s); }
            float[] To3x3(float[] it) => new[] { it[0], it[1], it[2], it[4], it[5], it[6], it[8], it[9], it[10] };
            // Matrix(3x3) @ Vector: per row a double sum of float32 products
            float[] Mat3Vec(float[] m, float[] v) { var o = new float[3]; for (int row = 0; row < 3; row++) { double d = 0.0; for (int col = 0; col < 3; col++) d += (double)(float)(m[row * 3 + col] * v[col]); o[row] = (float)d; } return o; }
            float[] RowMajor(float[] world) => VehicleProbe.ToRowMajor(world);

            int deployEnd;
            if (!PyInt(argc > 3 ? Arg(3) : "", out deployEnd)) throw new NotPortedException($"an end frame the script cannot read ('{(argc > 3 ? Arg(3) : "")}': the recoil step fails there)");
            if (r.Segments.Count == 0) throw new NotPortedException("a fire window without a segment (the recoil step fails on it)");
            int rs = r.Segments[0].start, re = r.Segments[0].end;
            int step = 2;
            if (argc > 10 && PyStrip(Arg(10)) != "" && !PyInt(Arg(10), out step)) throw new NotPortedException($"a recoil step the script cannot read ('{Arg(10)}')");
            var byName = r.Bones.ToDictionary(b => b.Name, b => b, StringComparer.Ordinal);
            var recoilBones = boneOfValues.Where(n => Lower(n).Contains("barrel") || Lower(n).Contains("cannon")).ToList();
            if (recoilBones.Any(n => n.Any(ch => ch > 127))) throw new NotPortedException("a bone name past ASCII in the recoil step (Python's lower case of it)");
            // bone_to_src = {bone_of[p.name]: p for p in parts if p.name in bone_of}: the LAST part on a bone wins
            var boneToSrc = new Dictionary<string, Obj>(StringComparer.Ordinal);
            foreach (var p in parts) if (boneOf.TryGetValue(p, out var pbn)) boneToSrc[pbn.Name] = p;
            int Depth(string bn) { int d = 0; for (var b = byName[bn].Parent; b != null; b = b.Parent) d++; return d; }
            var ordered = recoilBones.Where(bn => boneToSrc.ContainsKey(bn)).OrderBy(Depth).ToList();   // a stable sort, as Python's
            if (ordered.Count == 0)
            {
                r.RecoilLog.Add("DEPLOY ERROR: recoil requested but no animated part name contains 'barrel'/'cannon' — cannot pick the tube. Animated parts: " + string.Join(", ", boneOfValues.OrderBy(n => n, StringComparer.Ordinal)));
                r.ExitAtRecoil = true;
                return;
            }
            if (step == 0) throw new NotPortedException("a recoil step of 0 (range() refuses it: the script fails)");
            var frames = new List<int>();
            if (step > 0) for (long t = rs; t <= re; t += step) frames.Add((int)t);
            else for (long t = rs; t > (long)re + 1; t += step) frames.Add((int)t);
            if (frames.Count == 0) throw new NotPortedException("a fire segment that runs the wrong way for its step (no frame to read: the script fails)");
            if (frames[frames.Count - 1] != re) frames.Add(re);
            rec.Rs = rs; rec.Re = re; rec.Step = step; rec.Frames = frames; rec.Ordered = ordered;

            // Phase A: the source nodes' world matrices at the aim frame and across the recoil
            Set(rs);
            var mAim = new Dictionary<string, float[]>(StringComparer.Ordinal); foreach (string bn in ordered) mAim[bn] = RowMajor(boneToSrc[bn].World);
            var srcW = new Dictionary<string, Dictionary<int, float[]>>(StringComparer.Ordinal); var srcOrder = new List<string>();
            foreach (string bn in ordered) if (!srcW.ContainsKey(bn)) { srcW[bn] = new Dictionary<int, float[]>(); srcOrder.Add(bn); }
            foreach (int t in frames) { Set(t); foreach (string bn in ordered) srcW[bn][t] = RowMajor(boneToSrc[bn].World); }

            // Phase B: the deployed hold
            Set(deployEnd);
            var poseNow = PoseMatrices(r.Bones, r.ArmPose);
            var mHome = new Dictionary<string, float[]>(StringComparer.Ordinal); foreach (string bn in ordered) mHome[bn] = RowMajor(poseNow[bn]);
            // the tube that moves most over the window (max(): the first of equals; a NaN first stands)
            string driver = null; double best = 0.0; bool first = true;
            foreach (string bn in ordered)
            {
                double m = 0.0; bool f0 = true;
                foreach (int t in frames) { double len = PyLen(Sub3(Trans(srcW[bn][t]), Trans(mAim[bn]))); if (f0 || len > m) { m = len; f0 = false; } }
                if (first || m > best) { best = m; driver = bn; first = false; }
            }
            var parentBone = byName[driver].Parent;
            string cradle = parentBone != null && boneToSrc.ContainsKey(parentBone.Name) ? parentBone.Name : driver;
            string tubeRoot = mHome.ContainsKey(cradle) ? cradle : driver;
            double mag = 1.0;
            if (argc > 11 && PyStrip(Arg(11)) != "" && !PyFloat(Arg(11), out mag)) throw new NotPortedException($"a slide scale the port does not read as Python's float() does ('{Arg(11)}')");
            if (mag == 0.0) { mag = 1.0; r.RecoilLog.Add("DEPLOY slide scale 0 treated as 1 (zero would silently kill the Slam)"); }
            if (!srcW.ContainsKey(cradle))
            {
                srcW[cradle] = new Dictionary<int, float[]>(); srcOrder.Add(cradle);
                foreach (int t in frames) { Set(t); srcW[cradle][t] = RowMajor(boneToSrc[cradle].World); }
                Set(deployEnd);
            }
            var scAim = srcW[cradle][rs]; var sbAim = srcW[driver][rs];
            var sbInv = VehicleProbe.Inverted(sbAim);
            if (sbInv == null) throw new NotPortedException($"the tube's aim matrix has no inverse ('{driver}': Matrix.inverted() raises, the script fails)");
            var cbar3 = To3x3(VehicleProbe.MatMulMathutils(mHome[driver], sbInv));
            var slide = new Dictionary<int, float[]>(); var slideOrder = new List<int>();
            foreach (int t in frames)
            {
                var cInv = VehicleProbe.Inverted(srcW[cradle][t]);
                if (cInv == null) throw new NotPortedException($"the cradle's matrix at frame {t} has no inverse ('{cradle}': the script fails)");
                var bt = VehicleProbe.MatMulMathutils(scAim, VehicleProbe.MatMulMathutils(cInv, srcW[driver][t]));
                if (!slide.ContainsKey(t)) slideOrder.Add(t);
                slide[t] = MulF(Mat3Vec(cbar3, Sub3(Trans(bt), Trans(sbAim))), (float)mag);
            }
            float[] peak = null; double peakLen = 0.0; first = true;
            foreach (int t in slideOrder) { double l = PyLen(slide[t]); if (first || l > peakLen) { peakLen = l; peak = slide[t]; first = false; } }
            double dist = peakLen != 0.0 ? peakLen : 1.0;   // `or 1.0`: a zero (of either sign) is false, a NaN is true
            var d = Normalized(peak);
            var A = Cross(d, new[] { 0f, 0f, 1f });
            if (PyLen(A) < 1e-4) A = Cross(d, new[] { 0f, 1f, 0f });
            A = Normalized(A);
            double slamDeg = 0.0;
            if (argc > 14 && PyStrip(Arg(14)) != "" && !PyFloat(Arg(14), out slamDeg)) throw new NotPortedException($"a slam the port does not read as Python's float() does ('{Arg(14)}')");
            double R;
            if (Math.Abs(slamDeg) > 0.0)
            {
                R = dist * 57.2958 / slamDeg;
                r.RecoilLog.Add($"DEPLOY slam {PyFormat.Fixed(slamDeg, 1)} deg -> derived Arc R {PyFormat.Fixed(R, 1)} (peak slide {PyFormat.Fixed(dist, 1)}){(slamDeg < 0 ? " [REVERSED: muzzle-up]" : "")}");
            }
            else if (argc > 12 && PyStrip(Arg(12)) != "")
            {
                if (!PyFloat(Arg(12), out R)) throw new NotPortedException($"an arc radius the port does not read as Python's float() does ('{Arg(12)}')");
            }
            else { R = 1.0e9; r.RecoilLog.Add("DEPLOY slam 0 — no kick pitch (arm stays identity)"); }
            // theta = -length / R: Python divides by zero and dies (a legacy radius of 0, of either sign)
            if (R == 0.0) throw new NotPortedException($"an arc radius of zero ('{Arg(12)}': the script divides by it and fails)");
            // Positive radii are capped at 1000 for the edit bone's pivot; negative radii are not. Casting a very
            // negative Python double to float makes the pivot non-finite: Blender's rebuilt bone cannot be inverted,
            // while the port's NaN determinant can pass the zero-determinant guard. Leave that edit-mode case to Blender.
            if (R < -float.MaxValue) throw new NotPortedException("an arc radius below the finite float32 range (the recoil edit-bone pivot overflows)");
            var radius = Normalized(Cross(A, d));
            var tubeHead = Trans(mHome[tubeRoot]);
            var pivot = Sub3(tubeHead, MulF(radius, (float)Math.Min(R, 1000.0)));
            rec.Driver = driver; rec.Cradle = cradle; rec.TubeRoot = tubeRoot; rec.Mag = mag; rec.Dist = dist; rec.R = R; rec.DeployEnd = deployEnd;
            rec.Home = mHome; rec.Aim = mAim; rec.Src = srcW; rec.SrcOrder = srcOrder; rec.Slide = slide; rec.SlideOrder = slideOrder;
            rec.Peak = peak; rec.D = d; rec.A = A; rec.Radius = radius; rec.TubeHead = tubeHead; rec.Pivot = pivot; rec.Cbar3 = cbar3;

            // edit mode: a RecoilArm bone (head at the pivot, tail 10 along the arc axis) between the tube and its parent;
            // leaving edit mode rebuilds EVERY bone from its edit bone (head, tail, roll): the tube's subtree moves an ulp
            string raName = BlenderNames.UniqueBone(boneNames, "RecoilArm");
            rec.ArmName = raName;
            var clones = r.Bones.Select(b => new Bone { Name = b.Name, Part = b.Part, Head = (float[])b.Head.Clone(), Tail = (float[])b.Tail.Clone() }).ToList();
            var cloneOf = r.Bones.Zip(clones, (o, c) => (o, c)).ToDictionary(p => p.o, p => p.c);
            foreach (var b in r.Bones) if (b.Parent != null) cloneOf[b].Parent = cloneOf[b.Parent];
            var ra = new Bone { Name = raName, Head = pivot, Tail = Add3(pivot, MulF(A, 10f)) };
            var tube = cloneOf[byName[tubeRoot]];
            ra.Parent = tube.Parent; tube.Parent = ra;
            clones.Add(ra);
            {
                var ids = Enumerable.Range(0, clones.Count).ToList();
                var kids = ids.ToDictionary(i => i, i => new List<int>());
                foreach (int i in ids) if (clones[i].Parent != null) kids[clones.IndexOf(clones[i].Parent)].Add(i);
                var rootIds = ids.Where(i => clones[i].Parent == null).ToList();
                var eHead = ids.ToDictionary(i => i, i => (float[])clones[i].Head.Clone()); var eTail = ids.ToDictionary(i => i, i => (float[])clones[i].Tail.Clone()); var eRoll = ids.ToDictionary(i => i, i => 0f);
                VehicleProbe.RestFromEditBones(ids, kids, rootIds, eHead, eTail, eRoll, (b, parent, armMat, offs, len) =>
                {
                    var bone = clones[b];
                    bone.MatrixLocal = armMat; bone.Length = len; bone.Offs = offs; bone.Head = eHead[b]; bone.Tail = eTail[b];
                });
            }
            r.BonesAfterRecoil = clones;
            poseBoneNames.Add(raName);
            r.ArmPose[raName] = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 1f, 1f };
            r.Rekeyed[raName] = new List<ArmKey>[10];
            Set(deployEnd);   // scene.frame_set(deploy_end): parents held at their deployed pose
            void KeyIdentity(int f)
            {
                var h = r.ArmPose[raName]; h[3] = 1f; h[4] = 0f; h[5] = 0f; h[6] = 0f; h[0] = h[1] = h[2] = 0f;
                Insert(raName, 0, 3, f); Insert(raName, 3, 4, f);
            }
            KeyIdentity(0); KeyIdentity(deployEnd);
            EvalArm(current);   // bpy.context.view_layer.update(): the animation at the current frame
            var raM3 = To3x3(RowMajor(PoseMatrices(clones, r.ArmPose)[raName]));
            var inv3 = VehicleProbe.Inverted3(raM3);
            if (inv3 == null) throw new NotPortedException("the recoil arm's pose matrix has no inverse (Matrix.inverted() raises, the script fails)");
            var aLocal = Normalized(Mat3Vec(inv3, A));
            rec.ALocal = aLocal;
            float[] prevQ = null;
            void KeyTheta(int f, double theta)
            {
                var q = QuaternionAxisAngle(aLocal, theta);
                if (prevQ != null && VehicleProbe.DotQt(q, prevQ) < 0f) q = new[] { -q[0], -q[1], -q[2], -q[3] };
                var h = r.ArmPose[raName]; h[3] = q[0]; h[4] = q[1]; h[5] = q[2]; h[6] = q[3]; h[0] = h[1] = h[2] = 0f; prevQ = q;
                Insert(raName, 0, 3, f); Insert(raName, 3, 4, f);
            }
            int KeyFrame(long f) { if (f < int.MinValue || f > int.MaxValue) throw new NotPortedException("a recoil key past a whole number (keyframe_insert takes it as a float)"); return (int)f; }
            foreach (int t in frames)
            {
                double theta = -PyLen(slide[t]) / R * (Dot3(slide[t], d) >= 0 ? 1 : -1);
                KeyTheta(KeyFrame((long)deployEnd + ((long)t - rs)), theta);
                rec.Thetas.Add(theta); rec.ArcBySrc[t] = theta;
            }
            long kickEnd = (long)deployEnd + ((long)frames[frames.Count - 1] - rs);
            int retSlow = 4;
            if (argc > 13 && PyStrip(Arg(13)) != "" && !PyInt(Arg(13), out retSlow)) throw new NotPortedException($"a return slowness the script cannot read ('{Arg(13)}')");
            long fr = kickEnd;
            if (retSlow > 0)
                for (int i = rec.Thetas.Count - 2; i >= 0; i--) { fr += (long)step * retSlow; KeyTheta(KeyFrame(fr), rec.Thetas[i]); }
            long outEnd = fr;
            KeyIdentity(KeyFrame(outEnd + 1)); outEnd += 1;
            rec.KickEnd = kickEnd; rec.OutEnd = outEnd;
            r.RecoilLog.Add($"DEPLOY recoil return: {(retSlow > 0 ? $"x{retSlow} slow-back glide" : "none (hold + snap)")}");
            // mode_set(OBJECT): no evaluation - the arm holds its last key's value
            r.RecoilLog.Add($"DEPLOY recoil (ARC slide x{PyFormat.General(mag)}, R={PyFormat.General(R)}, peak={PyFormat.Fixed(dist, 1)}) tail {deployEnd}..{outEnd} via RecoilArm; tube '{tubeRoot}'");
        }
        r.AfterRecoil = all.ToDictionary(o => o.Name, o => (float[])o.World.Clone(), StringComparer.Ordinal);

        // ---- 6. the bind: the scene at the bind frame; every mesh detached (parent None: the parent inverse reset, the bone
        //      parenting dropped), its world matrix folded into its vertices (a datablock shared with another live object is
        //      copied first, and the copy takes the next free number), one vertex group of its bone over every vertex at 1,
        //      an Armature modifier, then under the armature at the identity (BKE_object_apply_mat4 through the armature's
        //      inverse: the object's own transform is what undoes the armature's world). The script exits before it when the
        //      recoil step found no tube.
        r.ArmPoseAfterRecoil = r.ArmPose.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone(), StringComparer.Ordinal);
        if (!r.ExitAtRecoil)
        {
            try { BindStep(); }
            catch (NotPortedException e) { r.Fallback = e.Message; return r; }
        }
        void BindStep()
        {
            var partNames = new HashSet<string>(r.BoneOf.Select(x => x.part), StringComparer.Ordinal);   // bone_of's keys: the parts, the merged ones too
            string staticRoot = r.Bones.First(b => b.Part == null).Name;
            Set(fmin);   // scene.frame_set(fmin): the bind at the rest frame
            var meshObjs = all.Where(o => o.Type == "MESH").ToList();
            // a datablock's users: the live objects that carry it (a stripped or culled object let go of its)
            var users = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var mo in meshObjs) users[mo.DataName] = (users.TryGetValue(mo.DataName, out int n) ? n : 0) + 1;
            var meshPool = names.MeshPool.Clone();   // every mesh datablock's name, the orphans' included: a copy is named against them all
            int bound = 0;
            foreach (var mo in meshObjs)
            {
                if (mo.MeshNode < 0) throw new NotPortedException($"a bone shape left in the scene at the bind ('{mo.Name}': its icosphere's vertices are Blender's)");
                string bname = null;
                for (var o = mo; o != null && bname == null; o = o.Parent) if (partNames.Contains(o.Name)) bname = o.Name;
                if (bname == null) { bname = staticRoot; r.BindLog.Add($"DEPLOY static mesh '{mo.Name}' -> StaticRoot (no animated ancestor)"); }
                string group = BlenderNames.TruncateUtf8(bname, 63);   // bDeformGroup.name: copy_utf8_truncated
                var mw = (float[])mo.World.Clone();
                // m.parent = None (parent_set: the parent inverse is the identity again, the type OBJECT); m.matrix_world = mw
                mo.Parent = null; mo.BoneNode = -1; mo.BoneMatrix = null; mo.ParentInverse = null;
                ApplyMat4Root(mo, mw);
                string data = mo.DataName; bool copied = false;
                if (users[data] > 1)
                {
                    // m.data = m.data.copy(): BKE_id_copy names the copy as the original and Blender numbers it (namemap_get_name)
                    users[data]--;
                    data = meshPool.Unique(mo.DataName); users[data] = 1; mo.DataName = data; copied = true;
                }
                ImportedMesh(m, mo.MeshNode, out var P, out int corners, out var cn);
                // m.data.transform(mw): math::transform_points SKIPS a matrix within 1e-6 of the identity on every entry
                // (skip_transform: is_equal with that epsilon, float32) - the vertices keep their bits, the importer's -0 among
                // them (measured: a static mesh at the identity keeps -0 where the product would give +0)
                var it = VehicleProbe.ToRowMajor(mw);
                if (!NearIdentity(it))
                    for (int v = 0; v < P.Length / 3; v++) VehicleProbe.TransformPoint(it, P[3 * v], P[3 * v + 1], P[3 * v + 2], out P[3 * v], out P[3 * v + 1], out P[3 * v + 2]);
                // m.matrix_world = Identity; m.parent = arm; m.matrix_world = Identity (the setter applies it through the parent)
                ApplyMat4Root(mo, VehicleProbe.IdentityF());
                mo.Parent = arm; mo.ParentInverse = null;
                ApplyMat4(mo, VehicleProbe.IdentityF(), arm.World);
                r.Bound.Add(new Bound { Mesh = mo, Group = group, DataName = data, Copied = copied, World = mw, Positions = P, Corners = corners, CustomNormal = cn });
                bound++;
            }
            r.BindLog.Add($"DEPLOY bound {bound} meshes");
            r.MeshData = meshPool.Names.Select(n => (n, users.TryGetValue(n, out int u) ? u : 0)).ToList();
            Update(all);
            r.AfterBind = all.ToDictionary(o => o.Name, o => (float[])o.World.Clone(), StringComparer.Ordinal);
        }
        r.ArmAt = frame =>
        {
            int e = Math.Max(-1048574, Math.Min(1048574, frame));
            FrameSet(e); EvalArm(e);
            return r.ArmPose.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone(), StringComparer.Ordinal);
        };
        r.ProbeAt = frame =>
        {
            int e = Math.Max(-1048574, Math.Min(1048574, frame));
            FrameSet(e); EvalArm(e);
            return (all.ToDictionary(o => o.Name, o => (float[])o.World.Clone(), StringComparer.Ordinal),
                    imported.ToDictionary(a => a.Name, a => rig.Armatures[a.Node].Bones.ToDictionary(b => names.BoneOfJoint[b], b => pose.Current(b), StringComparer.Ordinal), StringComparer.Ordinal));
        };
        return r;
    }

    // .NET Framework's decimal conversion can round a double one ulp away from Python (for example
    // 0.39499999999999999). Use it as an estimate, then compare the exact decimal input to the exact binary
    // midpoints around that estimate. This also holds under Unity's Mono, without a native parser dependency.
    internal static bool ReadPythonFloat(string s, out double v)
    {
        s = (s ?? "").Trim(IntWhitespace); v = 0;
        if (s.Length == 0 || s.Any(ch => !(ch >= '0' && ch <= '9') && ch != '+' && ch != '-' && ch != '.' && ch != 'e' && ch != 'E')) return false;
        if (!double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) || double.IsInfinity(v)) return false;
        bool neg = s[0] == '-';
        string token = s[0] == '+' || neg ? s.Substring(1) : s;
        int at = token.IndexOfAny(new[] { 'e', 'E' }); long power = 0;
        if (at >= 0)
        {
            string exp = token.Substring(at + 1); bool minus = exp[0] == '-';
            int first = exp[0] == '+' || minus ? 1 : 0;
            // Exponents beyond this bound cannot cancel a string's (Int32-sized) number of digits.
            for (int i = first; i < exp.Length; i++) power = Math.Min(10000000000L, power * 10 + exp[i] - '0');
            if (minus) power = -power;
            token = token.Substring(0, at);
        }
        int point = token.IndexOf('.');
        if (point >= 0) { power -= token.Length - point - 1; token = token.Remove(point, 1); }
        string digits = token.TrimStart('0');
        if (digits.Length == 0) { v = neg ? -0.0 : 0.0; return true; }
        var exact = Canonical(digits, power);
        long bits = BitConverter.DoubleToInt64Bits(v) & long.MaxValue;
        while (true)
        {
            int e = (int)(bits >> 52); ulong m = (ulong)bits & 0xFFFFFFFFFFFFFUL;
            int binaryPower = e == 0 ? -1074 : e - 1075;
            if (e != 0) m |= 1UL << 52;
            bool odd = (bits & 1) != 0;
            int upper = Compare(exact, BinaryDecimal(2 * m + 1, binaryPower - 1));
            if (upper > 0 || upper == 0 && odd)
            {
                if (++bits == 0x7ff0000000000000L) return false; // a scale overflowing a double stays Blender's
                continue;
            }
            if (bits != 0)
            {
                bool boundary = e > 1 && m == 1UL << 52;
                int lower = Compare(exact, BinaryDecimal(boundary ? 4 * m - 1 : 2 * m - 1, binaryPower - (boundary ? 2 : 1)));
                if (lower < 0 || lower == 0 && odd) { bits--; continue; }
            }
            v = BitConverter.Int64BitsToDouble(bits | (neg ? long.MinValue : 0)); return true;
        }

        (string digits, long power) Canonical(string d, long p)
        {
            string trimmed = d.TrimEnd('0'); return (trimmed, p + d.Length - trimmed.Length);
        }
        (string digits, long power) BinaryDecimal(ulong coefficient, int p)
        {
            var ds = coefficient.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(c => c - '0').ToList();
            int frac = 0;
            for (; p > 0; p--)
            {
                int carry = 0;
                for (int i = ds.Count - 1; i >= 0; i--) { int d = ds[i] * 2 + carry; ds[i] = d % 10; carry = d / 10; }
                if (carry > 0) ds.Insert(0, carry);
            }
            for (; p < 0; p++)
            {
                ds.Add(0); frac++; int rem = 0;
                for (int i = 0; i < ds.Count; i++) { int d = rem * 10 + ds[i]; ds[i] = d / 2; rem = d % 2; }
            }
            return Canonical(new string(ds.Select(d => (char)('0' + d)).ToArray()).TrimStart('0'), -frac);
        }
        int Compare((string digits, long power) a, (string digits, long power) b)
        {
            int order = (a.digits.Length + a.power).CompareTo(b.digits.Length + b.power);
            if (order != 0) return order;
            for (int i = 0; i < Math.Max(a.digits.Length, b.digits.Length); i++)
            {
                char ac = i < a.digits.Length ? a.digits[i] : '0', bc = i < b.digits.Length ? b.digits[i] : '0';
                if (ac != bc) return ac.CompareTo(bc);
            }
            return 0;
        }
    }

    /// <summary>BKE_pose_where_is for the armature the script made: every bone's pose matrix (armature space, column-major)
    /// from what its pose bone holds, a parent before its children whatever the list's order.</summary>
    internal static Dictionary<string, float[]> PoseMatrices(IList<Bone> bones, Dictionary<string, float[]> armPose)
    {
        var pose = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var pending = new List<Bone>(bones);
        while (pending.Count > 0)
        {
            var next = pending.Where(b => b.Parent == null || pose.ContainsKey(b.Parent.Name)).ToList();
            if (next.Count == 0) throw new InvalidOperationException("a bone whose parent is not in the list");
            foreach (var b in next)
            {
                var p = armPose[b.Name];
                var chan = VehicleProbe.ObjectMatrix(new[] { p[0], p[1], p[2] }, new[] { p[3], p[4], p[5], p[6] }, new[] { p[7], p[8], p[9] }, pchan: true);
                var rs = b.Parent != null ? VehicleProbe.MulM4(pose[b.Parent.Name], b.Offs) : b.MatrixLocal;
                pose[b.Name] = PoseFromChannel(rs, chan);
                pending.Remove(b);
            }
        }
        return pose;
    }

    /// <summary>mathutils' Quaternion.to_axis_angle(): the quaternion normalized, quat_to_axis_angle (acosf, sinf), and the
    /// axis made sane (a zero or non-finite one is X; one within ten float steps of zero on all three gets X = 1).</summary>
    static void ToAxisAngle(float[] q, out float[] axis, out float angle)
    {
        var t = (float[])q.Clone(); VehicleProbe.NormalizeQt(t);
        float ha = BlenderTrig.Acosf(t[0]), si = BlenderTrig.Sinf(ha);
        angle = (float)(ha * 2f);
        if (Math.Abs(si) < 0.0005f) si = 1.0f;
        axis = new[] { (float)(t[1] / si), (float)(t[2] / si), (float)(t[3] / si) };
        if (axis[0] == 0f && axis[1] == 0f && axis[2] == 0f) axis[1] = 1.0f;
        bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        if ((axis[0] == 0f && axis[1] == 0f && axis[2] == 0f) || !Finite(axis[0]) || !Finite(axis[1]) || !Finite(axis[2])) { axis[0] = 1f; axis[1] = 0f; axis[2] = 0f; }
        else if (NearZero(axis[0]) && NearZero(axis[1]) && NearZero(axis[2])) axis[0] = 1.0f;
        if (!Finite(angle)) angle = 0f;
    }

    /// <summary>EXPP_FloatsAreEqual(v, 0, 10): within ten representable floats of zero.</summary>
    static bool NearZero(float v)
    {
        unchecked
        {
            int ai = BitConverter.ToInt32(BitConverter.GetBytes(v), 0);
            int test = ai < 0 ? -1 : 0;
            int diff = ai ^ (test & 0x7fffffff);
            return ((10 + diff) | (10 - diff)) >= 0;
        }
    }

    /// <summary>mathutils' Quaternion(axis, angle): the angle wrapped into -pi..pi in float32 (angle_wrap_rad), the axis
    /// normalized, (cosf, axis sinf) of the half angle; the identity for an axis of no length.</summary>
    static float[] QuaternionAxisAngle(float[] axis, double angle)
    {
        const float pi = (float)Math.PI;
        float a = (float)angle;
        float b = (float)(pi * 2.0f), x = (float)(a + pi);
        a = (float)((float)(x - (float)(b * (float)Math.Floor((double)(float)(x / b)))) - pi);
        float d = (float)((float)((float)(axis[0] * axis[0]) + (float)(axis[1] * axis[1])) + (float)(axis[2] * axis[2]));
        if (!(d > 1.0e-35f)) return new[] { 1f, 0f, 0f, 0f };
        d = VehicleProbe.Sqrtf(d); float f = (float)(1.0f / d);
        float phi = (float)(0.5f * a), si = BlenderTrig.Sinf(phi), co = BlenderTrig.Cosf(phi);
        return new[] { co, (float)((float)(axis[0] * f) * si), (float)((float)(axis[1] * f) * si), (float)((float)(axis[2] * f) * si) };
    }

    /// <summary>interp_qt_qtqt (Quaternion.slerp): the short way round, interp_dot_slerp's weights (a plain lerp when the
    /// two are within 1e-4 of aligned), no normalization afterwards.</summary>
    static float[] Slerp(float[] a, float[] b, float t)
    {
        float cosom = VehicleProbe.DotQt(a, b);
        var quat = (float[])a.Clone();
        if (cosom < 0f) { cosom = -cosom; for (int i = 0; i < 4; i++) quat[i] = -a[i]; }
        float w0, w1;
        if (Math.Abs(cosom) < (float)(1.0f - 1e-4f))
        {
            float omega = BlenderTrig.Acosf(cosom), sinom = BlenderTrig.Sinf(omega);
            w0 = (float)(BlenderTrig.Sinf((float)((float)(1.0f - t) * omega)) / sinom);
            w1 = (float)(BlenderTrig.Sinf((float)(t * omega)) / sinom);
        }
        else { w0 = (float)(1.0f - t); w1 = t; }
        var q = new float[4];
        for (int i = 0; i < 4; i++) q[i] = (float)((float)(w0 * quat[i]) + (float)(w1 * b[i]));
        return q;
    }

    /// <summary>The bake's second half for one bone: each frame's basis set (`pbone.matrix_basis = m`: BKE_pchan_apply_mat4 -
    /// mat4_to_loc_rot_size, mat3_normalized_to_quat), the quaternion made compatible with the frame before, and a
    /// key of location 3, quaternion 4, scale 3.</summary>
    static float[][] KeysFromBasis(float[][] basis)
    {
        var keys = new float[basis.Length][]; float[] prev = null;
        for (int k = 0; k < basis.Length; k++)
        {
            VehicleProbe.Mat4ToLocRotSize(basis[k], out var loc, out var rot, out var size);
            var q = VehicleProbe.Mat3NormalizedToQuat(rot);
            if (prev != null) q = MakeCompatible(q, prev);
            prev = q;
            keys[k] = new[] { loc[0], loc[1], loc[2], q[0], q[1], q[2], q[3], size[0], size[1], size[2] };
        }
        return keys;
    }

    /// <summary>BKE_bone_parent_transform_apply with one matrix for rotation, scale and location: the matrix times the
    /// channel, the location column through the same matrix apart (mul_v3_m4v3), the axes times a post scale of 1.</summary>
    static float[] PoseFromChannel(float[] rotscale, float[] chan)
    {
        var o = VehicleProbe.MulM4(rotscale, chan);
        float x = chan[12], y = chan[13], z = chan[14];
        for (int k = 0; k < 3; k++)
            o[12 + k] = (float)((float)((float)((float)(x * rotscale[k]) + (float)(y * rotscale[4 + k])) + (float)(z * rotscale[8 + k])) + rotscale[12 + k]);
        for (int k = 0; k < 12; k++) if (k % 4 != 3) o[k] = (float)(o[k] * 1f);
        return o;
    }

    /// <summary>mathutils' Quaternion.make_compatible(other): the unit quaternion brought next to `old` through the
    /// rotation between them (quat_to_compatible_quat), its length put back. It does not return the quaternion it was
    /// given: a bone that stands still drifts by an ulp a frame.</summary>
    static float[] MakeCompatible(float[] self, float[] old)
    {
        var a = (float[])self.Clone();
        float len = VehicleProbe.Sqrtf(VehicleProbe.DotQt(a, a));
        if (len != 0f) { float f = (float)(1.0f / len); for (int i = 0; i < 4; i++) a[i] = (float)(a[i] * f); } else { a[1] = 1f; a[0] = a[2] = a[3] = 0f; }
        float[] q;
        var oldUnit = (float[])old.Clone();
        float oldLen = VehicleProbe.Sqrtf(VehicleProbe.DotQt(oldUnit, oldUnit));
        if (oldLen != 0f) { float f = (float)(1.0f / oldLen); for (int i = 0; i < 4; i++) oldUnit[i] = (float)(oldUnit[i] * f); } else { oldUnit[1] = 1f; oldUnit[0] = oldUnit[2] = oldUnit[3] = 0f; }
        if (oldLen > 1e-4f)
        {
            // rotation_between_quats_to_quat(delta, old_unit, a): the conjugate over its squared length, times a
            var t = new[] { oldUnit[0], -oldUnit[1], -oldUnit[2], -oldUnit[3] };
            float inv = (float)(1.0f / VehicleProbe.DotQt(t, t));
            for (int i = 0; i < 4; i++) t[i] = (float)(t[i] * inv);
            var delta = BlenderPosedState.MulQtQt(t, a);
            q = BlenderPosedState.MulQtQt(old, delta);
            var neg = new[] { -q[0], -q[1], -q[2], -q[3] };
            if (LenSquaredV4(neg, old) < LenSquaredV4(q, old)) q = neg;
        }
        else q = a;
        return new[] { (float)(q[0] * len), (float)(q[1] * len), (float)(q[2] * len), (float)(q[3] * len) };
    }

    static float LenSquaredV4(float[] a, float[] b)
    {
        float d0 = (float)(b[0] - a[0]), d1 = (float)(b[1] - a[1]), d2 = (float)(b[2] - a[2]), d3 = (float)(b[3] - a[3]);
        return (float)((float)((float)((float)(d0 * d0) + (float)(d1 * d1)) + (float)(d2 * d2)) + (float)(d3 * d3));
    }

    /// <summary>Quaternion.magnitude: the float32 root of the float32 dot, as a Python float.</summary>
    static double Magnitude(float[] q) => (double)VehicleProbe.Sqrtf(VehicleProbe.DotQt(q, q));

    /// <summary>A mesh object's vertices as the importer stores them (BlenderMesh's order, Blender's frame), its number of
    /// face corners, and the custom normals the importer set from the file's normals as the INT16_2D attribute holds them
    /// (null when no primitive has normals) - BlenderReduce's reading of an unskinned mesh.</summary>
    internal static void ImportedMesh(HafModel m, int meshNode, out float[] P, out int corners, out short[] customNormal)
    {
        if (m.Nodes[meshNode].Skin >= 0) throw new NotPortedException($"a skinned mesh at the bind ('{m.Nodes[meshNode].Name}')");
        int meshIndex = m.Nodes[meshNode].Mesh;
        var mesh = m.Meshes[meshIndex];
        var layout = BlenderMesh.FromGltf(m, meshIndex);
        int nv = layout.VertexCount; P = new float[nv * 3]; var N = new float[nv * 3]; bool hasNormals = false;
        for (int v = 0; v < nv; v++)
        {
            var p = mesh.Primitives[layout.RankPrimitive[v]]; int idx = layout.RankIndex[v];
            P[3 * v] = p.Positions[3 * idx]; P[3 * v + 1] = -p.Positions[3 * idx + 2]; P[3 * v + 2] = p.Positions[3 * idx + 1];
            if (p.Normals == null) { N[3 * v] = N[3 * v + 1] = N[3 * v + 2] = float.NaN; continue; }
            hasNormals = true;
            N[3 * v] = p.Normals[3 * idx]; N[3 * v + 1] = -p.Normals[3 * idx + 2]; N[3 * v + 2] = p.Normals[3 * idx + 1];
        }
        corners = layout.Faces.Length; customNormal = null;
        if (hasNormals)
        {
            if (!BlenderTrig.Exact) throw new NotPortedException("a mesh with normals at the bind in a process without the 64-bit Windows C runtime (the custom normals' cosf)");
            var sharp = VehicleProbe.SharpFaces(P, layout.Faces, N);
            var (d0, d1) = VehicleProbe.EncodeCustomShorts(P, layout.Faces, N, sharp);
            customNormal = new short[2 * corners];
            for (int c = 0; c < corners; c++) { customNormal[2 * c] = d0[c]; customNormal[2 * c + 1] = d1[c]; }
        }
    }

    /// <summary>skip_transform (math_matrix.cc): math::is_equal(transform, identity, 1e-6f) - no entry differs from the
    /// identity's by MORE than 1e-6 in float32 (a NaN entry does not: it is "equal", and the transform is skipped).</summary>
    internal static bool NearIdentity(float[] it)
    {
        for (int i = 0; i < 16; i++) { float d = (float)(it[i] - (i % 5 == 0 ? 1f : 0f)); if (Math.Abs(d) > 1e-6f) return false; }
        return true;
    }

    /// <summary>`o.matrix_world = m` for an object without a parent.</summary>
    static void ApplyMat4Root(Obj o, float[] mat)
    {
        VehicleProbe.Mat4ToLocRotSize(mat, out var loc, out var rot, out var size);
        if (!o.Euler) o.Quat = BlenderPosedState.MulQtQt(new[] { 1f, -0f, -0f, -0f }, VehicleProbe.Mat3NormalizedToQuat(rot));
        o.Loc = new[] { (float)(loc[0] - 0f), (float)(loc[1] - 0f), (float)(loc[2] - 0f) };
        o.Scale = new[] { (float)(size[0] / 1f), (float)(size[1] / 1f), (float)(size[2] / 1f) };
    }

    /// <summary>The depsgraph's pass: every object's matrix_world from its own transform and its parent's matrix.</summary>
    static void Update(List<Obj> all)
    {
        var done = new HashSet<Obj>();
        void Calc(Obj o)
        {
            if (!done.Add(o)) return;
            if (o.Frozen) return;
            var local = VehicleProbe.ObjectMatrix(o.Loc, o.Euler ? new[] { 1f, 0f, 0f, 0f } : o.Quat, o.Scale);
            if (o.Parent == null) { o.World = local; return; }
            Calc(o.Parent);
            o.World = VehicleProbe.MulM4(o.ParentInverse != null ? VehicleProbe.MulM4(o.ParentMatrix, o.ParentInverse) : o.ParentMatrix, local);
        }
        foreach (var o in all) Calc(o);
    }

    /// <summary>BKE_object_apply_mat4 with a parent (identity parentinv, no deltas): the parent's inverse times the
    /// matrix, mat4_to_loc_rot_size, and for an object in quaternion mode mat3_normalized_to_quat times the inverse of
    /// the (identity) delta quaternion.</summary>
    static void ApplyMat4(Obj o, float[] mat, float[] parentWorld)
    {
        var diff = VehicleProbe.MulM4(parentWorld, VehicleProbe.IdentityF());
        var rmat = VehicleProbe.MulM4(BlenderEigen.InvertM4(diff), mat);
        VehicleProbe.Mat4ToLocRotSize(rmat, out var loc, out var rot, out var size);
        if (!o.Euler)
        {
            var q = VehicleProbe.Mat3NormalizedToQuat(rot);
            o.Quat = BlenderPosedState.MulQtQt(new[] { 1f, -0f, -0f, -0f }, q);
        }
        o.Loc = new[] { (float)(loc[0] - 0f), (float)(loc[1] - 0f), (float)(loc[2] - 0f) };
        o.Scale = new[] { (float)(size[0] / 1f), (float)(size[1] / 1f), (float)(size[2] / 1f) };
    }

    /// <summary>mathutils' Matrix @ Vector for a 4x4 and a 3D vector: per row a double sum of float32 products (the
    /// vector's fourth component 1), stored as a float32.</summary>
    static float[] MatVec(float[] bm, float x, float y, float z)
    {
        var v = new[] { x, y, z, 1f }; var r = new float[3];
        for (int row = 0; row < 3; row++)
        {
            double dot = 0.0;
            for (int col = 0; col < 4; col++) dot += (double)(float)(bm[col * 4 + row] * v[col]);
            r[row] = (float)dot;
        }
        return r;
    }

    /// <summary>The script's _mat_bad: the translation and `to_scale()` (mat3_to_rot_size's sizes) out of range or NaN.</summary>
    static bool MatBad(float[] bm)
    {
        for (int k = 0; k < 3; k++) { double v = bm[12 + k]; if (v != v || Math.Abs(v) > 1e6) return true; }
        VehicleProbe.Mat4ToLocRotSize(bm, out _, out _, out var size);
        for (int k = 0; k < 3; k++) { double v = size[k]; if (v != v || Math.Abs(v) > 1e4 || Math.Abs(v) < 1e-6) return true; }
        return false;
    }

    /// <summary>A mesh object's bound_box: the float32 extremes of the vertices its primitives use, in Blender's frame.</summary>
    static bool LocalBox(HafModel m, int meshNode, out float[] mn, out float[] mx)
    {
        mn = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity }; mx = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        bool any = false;
        foreach (var p in m.Meshes[m.Nodes[meshNode].Mesh].Primitives)
            foreach (int v in VehicleProbe.Used(p))
            {
                float bx = p.Positions[v * 3], by = -p.Positions[v * 3 + 2], bz = p.Positions[v * 3 + 1];
                mn[0] = Math.Min(mn[0], bx); mn[1] = Math.Min(mn[1], by); mn[2] = Math.Min(mn[2], bz);
                mx[0] = Math.Max(mx[0], bx); mx[1] = Math.Max(mx[1], by); mx[2] = Math.Max(mx[2], bz);
                any = true;
            }
        return any;
    }

    // Python's str.strip() set (the ASCII separators U+001C..U+001F included, which .NET's Trim leaves)
    static readonly char[] PythonWhitespace = "\u0009\u000a\u000b\u000c\u000d\u001c\u001d\u001e\u001f\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000".ToCharArray();
    static string PyStrip(string s) => (s ?? "").Trim(PythonWhitespace);
    static readonly char[] IntWhitespace = PythonWhitespace.Where(c => c < '\u001c' || c > '\u001f').ToArray();

    static string AsciiLower(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (char c in s) b.Append(c >= 'A' && c <= 'Z' ? (char)(c + 32) : c);
        return b.ToString();
    }

    /// <summary>Python's repr of a str: single quotes, unless it holds one and no double quote; the backslash, the quote
    /// and the C0 control characters escaped (a tab as backslash-t). Other characters Python holds unprintable are not
    /// escaped here.</summary>
    internal static string PyRepr(string s)
    {
        bool dq = s.IndexOf('\'') >= 0 && s.IndexOf('"') < 0;
        char q = dq ? '"' : '\'';
        var b = new StringBuilder(); b.Append(q);
        foreach (char c in s)
        {
            if (c == '\\' || c == q) b.Append('\\').Append(c);
            else if (c == '\t') b.Append("\\t");
            else if (c == '\n') b.Append("\\n");
            else if (c == '\r') b.Append("\\r");
            else if (c < 0x20 || c == 0x7f) b.Append("\\x").Append(((int)c).ToString("x2"));
            else b.Append(c);
        }
        return b.Append(q).ToString();
    }

    /// <summary>Python's `%-Ns`: padded to N CODE POINTS (a letter past the BMP is one, and two UTF-16 units).</summary>
    internal static string PyPad(string s, int width)
    {
        int n = 0; for (int i = 0; i < s.Length; i++) if (!(char.IsLowSurrogate(s[i]) && i > 0 && char.IsHighSurrogate(s[i - 1]))) n++;
        return n >= width ? s : s + new string(' ', width - n);
    }

    /// <summary>The order of `bpy.data.objects`: the names' UTF-8 bytes as unsigned values, upper-case ASCII folded.</summary>
    internal static int IdNameCmp(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a); var y = Encoding.UTF8.GetBytes(b);
        for (int i = 0; ; i++)
        {
            int c1 = i < x.Length ? x[i] : 0, c2 = i < y.Length ? y[i] : 0;
            if (c1 >= 'A' && c1 <= 'Z') c1 += 32;
            if (c2 >= 'A' && c2 <= 'Z') c2 += 32;
            if (c1 != c2) return c1 < c2 ? -1 : 1;
            if (c1 == 0) return 0;
        }
    }

    static int CodePoint(string s, ref int i)
    {
        if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { int c = char.ConvertToUtf32(s[i], s[i + 1]); i += 2; return c; }
        return s[i++];
    }

    /// <summary>Python orders str by code point; .NET's ordinal order is by UTF-16 unit, which differs past the BMP.</summary>
    static readonly IComparer<string> CodePointOrder = Comparer<string>.Create((a, b) =>
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            int ca = CodePoint(a, ref i), cb = CodePoint(b, ref j);
            if (ca != cb) return ca < cb ? -1 : 1;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    });
}

/// <summary>Python's `%.Nf` of a float: the exact decimal value of the double, rounded half to even at N decimals -
/// .NET rounds a 15-digit rendering instead and differs on a value that sits near a half.</summary>
public static class PyFormat
{
    /// <summary>Python's `%g`: six significant digits, correctly rounded (half to even on the exact value), the exponent
    /// form below 1e-4 and from 1e6, trailing zeros dropped, the exponent at least two digits.</summary>
    public static string General(double v, int significant = 6)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        long bits = BitConverter.DoubleToInt64Bits(v);
        bool neg = bits < 0;
        if (v == 0.0) return neg ? "-0" : "0";
        var digits = ExactDigits(v, out int frac);   // the exact decimal expansion: an integer with `frac` decimals
        int lead = 0; while (lead < digits.Count - 1 && digits[lead] == 0) lead++;
        digits.RemoveRange(0, lead);
        int exp10 = digits.Count - frac - 1;        // the decimal exponent of the first significant digit
        if (digits.Count > significant)
        {
            int cut = significant; int firstDropped = digits[cut]; bool rest = false;
            for (int i = cut + 1; i < digits.Count; i++) if (digits[i] != 0) { rest = true; break; }
            bool up = firstDropped > 5 || (firstDropped == 5 && (rest || (digits[cut - 1] & 1) == 1));
            digits.RemoveRange(cut, digits.Count - cut);
            if (up)
            {
                int i = digits.Count - 1;
                for (; i >= 0; i--) { if (digits[i] == 9) digits[i] = 0; else { digits[i]++; break; } }
                if (i < 0) { digits.Insert(0, 1); digits.RemoveAt(digits.Count - 1); exp10++; }
            }
        }
        while (digits.Count < significant) digits.Add(0);
        int last = digits.Count - 1; while (last > 0 && digits[last] == 0) last--;   // trailing zeros go
        var s = new StringBuilder(); if (neg) s.Append('-');
        if (exp10 < -4 || exp10 >= significant)
        {
            s.Append((char)('0' + digits[0]));
            if (last > 0) { s.Append('.'); for (int i = 1; i <= last; i++) s.Append((char)('0' + digits[i])); }
            s.Append('e').Append(exp10 < 0 ? '-' : '+').Append(Math.Abs(exp10).ToString("00"));
        }
        else if (exp10 >= 0)
        {
            for (int i = 0; i <= exp10; i++) s.Append((char)('0' + (i < digits.Count ? digits[i] : 0)));
            if (last > exp10) { s.Append('.'); for (int i = exp10 + 1; i <= last; i++) s.Append((char)('0' + digits[i])); }
        }
        else
        {
            s.Append("0."); for (int i = exp10 + 1; i < 0; i++) s.Append('0');
            for (int i = 0; i <= last; i++) s.Append((char)('0' + digits[i]));
        }
        return s.ToString();
    }

    static List<int> ExactDigits(double v, out int frac)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        int e = (int)((bits >> 52) & 0x7FF); long man = bits & 0xFFFFFFFFFFFFFL;
        if (e == 0) e = 1; else man |= 1L << 52;
        e -= 1075;
        var digits = man.ToString().Select(c => c - '0').ToList(); frac = 0;
        for (; e > 0; e--)
        {
            int carry = 0;
            for (int i = digits.Count - 1; i >= 0; i--) { int d = digits[i] * 2 + carry; digits[i] = d % 10; carry = d / 10; }
            if (carry > 0) digits.Insert(0, carry);
        }
        for (; e < 0; e++)
        {
            digits.Add(0); frac++;
            int rem = 0;
            for (int i = 0; i < digits.Count; i++) { int d = rem * 10 + digits[i]; digits[i] = d / 2; rem = d % 2; }
        }
        while (digits.Count <= frac) digits.Insert(0, 0);
        return digits;
    }

    public static string Fixed(double v, int decimals)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        long bits = BitConverter.DoubleToInt64Bits(v);
        bool neg = bits < 0; int e = (int)((bits >> 52) & 0x7FF); long man = bits & 0xFFFFFFFFFFFFFL;
        if (e == 0) e = 1; else man |= 1L << 52;
        e -= 1075;   // v = man * 2^e
        // decimal digits of man, then times 2 or halved (each halving adds one decimal place)
        var digits = man.ToString().Select(c => c - '0').ToList(); int frac = 0;   // digits hold an integer; frac of them are decimals
        for (; e > 0; e--)
        {
            int carry = 0;
            for (int i = digits.Count - 1; i >= 0; i--) { int d = digits[i] * 2 + carry; digits[i] = d % 10; carry = d / 10; }
            if (carry > 0) digits.Insert(0, carry);
        }
        for (; e < 0; e++)
        {
            digits.Add(0); frac++;
            int rem = 0;
            for (int i = 0; i < digits.Count; i++) { int d = rem * 10 + digits[i]; digits[i] = d / 2; rem = d % 2; }
        }
        while (digits.Count <= frac) digits.Insert(0, 0);
        // round half to even at `decimals`
        if (frac > decimals)
        {
            int cut = digits.Count - (frac - decimals);
            int first = digits[cut]; bool rest = false; for (int i = cut + 1; i < digits.Count; i++) if (digits[i] != 0) { rest = true; break; }
            bool up = first > 5 || (first == 5 && (rest || (digits[cut - 1] & 1) == 1));
            digits.RemoveRange(cut, digits.Count - cut); frac = decimals;
            if (up)
            {
                int i = digits.Count - 1;
                for (; i >= 0; i--) { if (digits[i] == 9) digits[i] = 0; else { digits[i]++; break; } }
                if (i < 0) digits.Insert(0, 1);
            }
        }
        while (frac < decimals) { digits.Add(0); frac++; }
        int whole = digits.Count - frac;
        int lead = 0; while (lead < whole - 1 && digits[lead] == 0) lead++;
        var s = new StringBuilder();
        if (neg) s.Append('-');
        for (int i = lead; i < whole; i++) s.Append((char)('0' + digits[i]));
        if (decimals > 0) { s.Append('.'); for (int i = whole; i < digits.Count; i++) s.Append((char)('0' + digits[i])); }
        return s.ToString();
    }
}
