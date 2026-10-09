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
// Left to Blender, named (Result.Fallback): what BlenderPosedState does not model; a surviving object under a bone, under
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
        public int Node = -1, MeshNode = -1;       // the glTF node that became it / whose mesh it carries, or -1
        public bool HasAction;
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
    }

    const int BoneWall = 124, PartBudget = 124;
    static readonly string[] DefaultKill = { "solder", "soldier", "pole", "string", "shell", "dynam", "ammun", "pcylinder1", "pcylinder3", "icosphere", "basicgal", "polysurface" };
    // bpy.ops.mesh.primitive_ico_sphere_add(radius=1): its bound_box, measured (bf737887 bf7fffff bf800000 / 3f737887 3f7fffff 3f800000)
    static float Bits(uint b) => BitConverter.ToSingle(BitConverter.GetBytes(b), 0);
    static readonly float[] IcoMin = { Bits(0xbf737887), Bits(0xbf7fffff), -1f }, IcoMax = { Bits(0x3f737887), Bits(0x3f7fffff), 1f };

    /// <summary>The script's decisions for a model and the arguments the Factory gives it after the input and the output
    /// (args[0] is the script's argv[2]).</summary>
    public static Result Decide(HafModel m, string[] args, BlenderNames.Result names = null)
    {
        var r = new Result();
        names = names ?? BlenderNames.Compute(m);
        string Arg(int scriptIndex) => scriptIndex - 2 < args.Length ? args[scriptIndex - 2] : null;
        int argc = args.Length + 2;
        var action = BlenderPosedState.Import(m, 0);
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
            if (o.GltfNode >= 0) BlenderPosedState.ImportedTrs(m.Nodes[o.GltfNode], out x.Loc, out x.Quat, out x.Scale);
            else { x.Loc = new float[3]; x.Quat = new[] { 1f, 0f, 0f, 0f }; x.Scale = new[] { 1f, 1f, 1f }; }
            all.Add(x); byName[x.Name] = x; source[x] = o;
        }
        foreach (var x in all) if (source[x].Parent != null) x.Parent = byName[source[x].Parent];
        for (int i = 0; i < names.BoneShapes.Count; i++)
        {
            var x = new Obj { Name = names.BoneShapes[i], Type = "MESH", DataName = names.BoneShapeData[i], Euler = true, Loc = new float[3], Quat = new[] { 1f, 0f, 0f, 0f }, Scale = new[] { 1f, 1f, 1f }, BoxMin = IcoMin, BoxMax = IcoMax };
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
                if (q.ParentBone != null) { r.Fallback = $"an object under a bone survives the strip ('{x.Name}': it follows the armature's pose)"; return r; }
                if (q != o && q.Kind == BlenderNames.ObjectKind.Camera) { r.Fallback = $"an object under a camera survives the strip ('{x.Name}': the importer's camera correction is not modelled)"; return r; }
            }
        }
        all = all.Where(o => !gone.Contains(o)).ToList();
        foreach (var o in all) if (o.Parent != null && gone.Contains(o.Parent)) o.Parent = null;   // a removed object's children are roots
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
        var pose = new BlenderPosedState.Pose(action, m);
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
        if (scale != 1.0 || recenter)
        {
            var pool = names.ObjectPool.Clone();
            foreach (var g in gone) pool.Remove(g.Name);   // a removed object's name is free again
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
                var local = p.Parent != null ? VehicleProbe.MulM4(BlenderEigen.InvertM4(p.Parent.World), p.World) : p.World;
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
        }
        return r;
    }

    /// <summary>The depsgraph's pass: every object's matrix_world from its own transform and its parent's matrix.</summary>
    static void Update(List<Obj> all)
    {
        var done = new HashSet<Obj>();
        void Calc(Obj o)
        {
            if (!done.Add(o)) return;
            var local = VehicleProbe.ObjectMatrix(o.Loc, o.Euler ? new[] { 1f, 0f, 0f, 0f } : o.Quat, o.Scale);
            if (o.Parent == null) { o.World = local; return; }
            Calc(o.Parent);
            o.World = VehicleProbe.MulM4(o.Parent.World, local);
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
