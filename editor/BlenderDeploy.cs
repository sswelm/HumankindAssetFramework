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
            // the armatures' poses: each bone's pose matrix from its pose bone's properties, for what hangs from it
            if (all.Exists(o => o.BoneNode >= 0 && o.Parent != null))
            {
                var poseMats = new Dictionary<int, float[]>();
                foreach (var arm in rig.Armatures.Values)
                {
                    float[][] Props(int b) { var p = set(b); rig.StaticProperty(b, out var l, out var q, out var s); return new[] { p?[0] ?? l, p?[1] ?? q, p?[2] ?? s }; }
                    foreach (var kv in VehicleProbe.PoseMatrices(arm, Props)) poseMats[kv.Key] = kv.Value;
                }
                foreach (var o in all) if (o.BoneNode >= 0 && o.Parent != null) o.BoneMatrix = VehicleProbe.ParBone(poseMats[o.BoneNode], rig.ArmatureOfBone[o.BoneNode].Length[o.BoneNode]);
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
        foreach (var a in imported)
        {
            if (a.Node >= 0 && action.Animates(a.Node)) { r.Fallback = $"the bake re-bakes the imported armature '{a.Name}', whose own animation it drops (not modelled)"; return r; }
            var rider = all.FirstOrDefault(o => o.Parent == a && o.BoneNode >= 0);
            if (rider != null) { r.Fallback = $"the bake re-bakes the imported armature '{a.Name}', from whose bones '{rider.Name}' hangs (not modelled)"; return r; }
        }

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
        }
        FrameSet(fmin);   // the bake puts the scene's frame back
        for (int i = 0; i < nb; i++)
        {
            var keys = new float[nf][]; float[] prev = null;
            for (int k = 0; k < nf; k++)
            {
                // pbone.matrix_basis = m: BKE_pchan_apply_mat4 - mat4_to_loc_rot_size, mat3_normalized_to_quat
                VehicleProbe.Mat4ToLocRotSize(bakedBasis[i][k], out var loc, out var rot, out var size);
                var q = VehicleProbe.Mat3NormalizedToQuat(rot);
                if (prev != null) q = MakeCompatible(q, prev);
                prev = q;
                keys[k] = new[] { loc[0], loc[1], loc[2], q[0], q[1], q[2], q[3], size[0], size[1], size[2] };
            }
            r.Keys[r.Bones[i].Name] = keys;
        }
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
            // every pose-bone scale curve of EVERY action goes: the new armature's, those the bake gave each surviving
            // imported armature (three a bone), and the importer's own - a bone's animated scale, in any animation,
            // whether its armature is still there or not
            int stripped = 3 * nb;
            foreach (var a in imported) stripped += 3 * rig.Armatures[a.Node].Bones.Count;
            for (int ai = 0; ai < m.Animations.Count; ai++)
                stripped += 3 * (ai == 0 ? action : BlenderPosedState.Import(m, ai, 24.0, rig)).Scale.Keys.Count(n => rig.IsBone(n));
            r.BakeLog.Add($"DEPLOY scale-free rig: {stripped} pose-scale fcurve(s) stripped (verts carry the unit scale)");
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
        return r;
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
