// BlenderVehicleRig.Stage3.cs - vehicle_rig.py's stage 3, the RIG (lines 1515-2896), in C# (step (d) of the vehicle_rig port,
// 2026-10-16; docs/Review-Backlog.md item (5)). Part d1, the frame every rig shares:
//   - wheel clustering: per wheel name (find: a missing one is the script's exit) the part's world box (world_bbox: the local
//     bound_box corners through matrix_world, mathutils float32); sorted by the largest extent, biggest first (a stable sort);
//     a part joins the first cluster whose anchor centre lies within 0.75 of the anchor's largest extent (Vector.length: the
//     float32 products summed in double, the last component first), else it anchors a new one;
//   - the armature: VehicleRig (object and datablock, names made unique), the Root bone (0,0,0)-(0,0.25,0), under the wave
//     rock a RootHull bone (0,0,0)-(0,0.2,0) beneath it carrying the body; one Wheel_%02d bone per cluster at the anchor's
//     centre, its tail along the axle (axle_axis: X/Y/Z forced, else the anchor's thinnest extent, the first on a tie) by
//     max(0.05, largest extent * 0.25), parented to the body bone; leaving edit mode builds the rest (RestFromEditBones);
//   - the RUDDER, PRESERVE and FLIP name sets (find_opt: a missing name is a WARN) and their lines;
//   - rigid skinning: every mesh the script holds (the scene's meshes with vertices, in scene order) loses its vertex groups
//     and gets one group, its bone (its wheel cluster's, else the body bone), every vertex at weight 1, an Armature modifier
//     on VehicleRig, and VehicleRig as its parent (the local transform kept: the armature sits at the identity);
//   - the join per bone: the meshes grouped by their bone - a rudder, preserve or flip part keyed apart on its own -, each group
//     joined into its first member (BlenderRigAnim.JoinMeshes, bpy.ops.object.join) and renamed Mesh_<key>.
// Every other branch of stage 3 - the turret, the gun with its muzzle and cradle, the trails, the tracks, the rotors, the sail,
// the flag, the oars - is left to Blender by name (parts d2-d6), as is a scene this part does not expect (an armature still
// there, an animated mesh at the join, a mesh still carrying an Armature modifier).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public static partial class BlenderVehicleRig
{
    /// <summary>The synthetic node ids of VehicleRig's bones in the stage model (BoneOfJoint keys): clear of the glTF nodes and of
    /// rig_anim's own synthetic range.</summary>
    const int RigBoneBase = 2000000;

    /// <summary>The branches of stage 3 the C# does not model yet: the role list that takes each, and the part that will.</summary>
    static readonly (string role, string branch)[] Stage3Later =
    {
        ("turrets", "the turret bone (d3)"), ("guns", "the gun assembly (d3)"), ("muzzles", "the muzzle sockets (d3)"), ("cradles", "the gun cradle (d3)"),
        ("trails", "the trail bones (d3)"), ("rotors", "the main rotor (d4)"), ("tailrotors", "the tail rotor (d4)"), ("sails", "the sail bone (d2)"),
        ("flags", "the flag bone (d2)"), ("oars", "the oar bones (d5)"), ("tracks", "the tread rig (d6)"),
    };

    /// <summary>Stage 3 on the scene stage 2 left: the rig. Lines go to s.Logs3; a branch not modelled sets s.Fallback; the script's exit
    /// sets s.Exits.</summary>
    public static void RunStage3(BlenderRigAnim.Stage1 s, Args a)
    {
        if (s.Fallback != null || s.Exits || s.Dies != null) return;
        foreach (var (role, branch) in Stage3Later)
            if (a.Names.TryGetValue(role, out var l) && l.Count > 0) { s.Fallback = "stage 3: " + branch + " is not modelled yet"; return; }
        if (s.Armature != null || s.Objects.Any(o => o.Type == "ARMATURE")) { s.Fallback = "stage 3: an armature in the scene before the rig"; return; }
        var names = s.Names;
        // objs: mesh_objects() at the split, less the ignored parts - the scene's meshes with vertices, in scene order
        var objs = s.Objects.Where(o => o.Type == "MESH" && o.Mesh != null && o.Mesh.Vertices > 0).ToList();
        if (objs.Any(o => o.Action != null)) Note(s, "an animated part through the join (its world as stage 1 evaluated it)");
        if (objs.Any(o => o.Mesh.ArmatureModifier != null)) { s.Fallback = "stage 3: a part still carrying an Armature modifier"; return; }
        if (objs.Any(o => o.Parent != null)) { s.Fallback = "stage 3: a parented part (the flatten leaves roots)"; return; }

        BlenderRigAnim.SceneObject Find(string name)
        {
            foreach (var o in objs) if (o.Name == name) return o;
            s.Logs3.Add("VEHICLE ERROR: part " + PyStrRepr(name) + " not found. Parts: [" + string.Join(", ", objs.Select(o => PyStrRepr(o.Name))) + "]");
            s.Exits = true; Note(s, "a missing structural part: the script's exit in stage 3");
            return null;
        }
        BlenderRigAnim.SceneObject FindOpt(string name) => objs.FirstOrDefault(o => o.Name == name);
        void WorldBbox(BlenderRigAnim.SceneObject o, out float[] c, out float[] size)
        {
            LocalBox(o.Mesh, out var lmn, out var lmx);
            VehicleProbe.WorldBox(VehicleProbe.ToRowMajor(o.World), lmn, lmx, out c, out size);
        }
        double MaxOf(float[] v) { double m = v[0]; if (v[1] > m) m = v[1]; if (v[2] > m) m = v[2]; return m; }   // max(Vector): the first on a tie

        // ---- wheel clustering
        var wheelInfo = new List<(double m, float[] c, float[] sz, string name)>();
        foreach (var wn in a.Names["wheels"])
        {
            var o = Find(wn); if (o == null) return;
            WorldBbox(o, out var c, out var sz);
            wheelInfo.Add((MaxOf(sz), c, sz, wn));
        }
        wheelInfo = wheelInfo.Select((w, i) => (w, i)).OrderBy(x => -x.w.m).ThenBy(x => x.i).Select(x => x.w).ToList();   // list.sort(key=-m): stable
        var clusters = new List<(double m, float[] c, float[] sz, List<string> names)>();
        foreach (var (m, c, sz, wn) in wheelInfo)
        {
            int home = -1;
            for (int k = 0; k < clusters.Count; k++)
            {
                var d = new[] { (float)(c[0] - clusters[k].c[0]), (float)(c[1] - clusters[k].c[1]), (float)(c[2] - clusters[k].c[2]) };
                if (VecLength(d) <= 0.75 * clusters[k].m) { home = k; break; }
            }
            if (home < 0) clusters.Add((m, c, sz, new List<string> { wn }));
            else clusters[home].names.Add(wn);
        }
        if (clusters.Count > 0) Note(s, "wheel clusters");

        // ---- the armature
        var armObjName = names.ObjectPool.Unique("VehicleRig");
        var armDataName = names.ArmaturePool.Unique("VehicleRig");
        var boneNames = new List<string>(); var parentOf = new List<int>();
        var head = new Dictionary<int, float[]>(); var tail = new Dictionary<int, float[]>(); var roll = new Dictionary<int, float>();
        var bonePool = new HashSet<string>(StringComparer.Ordinal);
        int AddBone(string name, float[] h, float[] t, int parent)
        {
            int id = RigBoneBase + boneNames.Count;
            boneNames.Add(BlenderNames.UniqueBone(bonePool, name)); parentOf.Add(parent);
            head[id] = h; tail[id] = t; roll[id] = 0f;
            return id;
        }
        int root = AddBone("Root", new float[3], new[] { 0f, 0.25f, 0f }, -1);
        int body = root; string bodyBone = "Root";
        if (a.RockOn) { body = AddBone("RootHull", new float[3], new[] { 0f, 0.2f, 0f }, root); bodyBone = "RootHull"; Note(s, "the wave rock's RootHull bone"); }
        var boneOf = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < clusters.Count; i++)
        {
            var cl = clusters[i];
            var ax = AxleAxis(a.AxisArg, cl.sz);
            float len = (float)Math.Max(0.05, cl.m * 0.25);   // max(0.05, max(s) * 0.25) into the float multiply
            var t = new[] { (float)(cl.c[0] + (float)(ax[0] * len)), (float)(cl.c[1] + (float)(ax[1] * len)), (float)(cl.c[2] + (float)(ax[2] * len)) };
            int id = AddBone(string.Format(CultureInfo.InvariantCulture, "Wheel_{0:00}", i), (float[])cl.c.Clone(), t, body);
            foreach (var wn in cl.names) boneOf[wn] = boneNames[id - RigBoneBase];
        }
        // ED_armature_from_edit: the rest; armature.bones in depth-first order (children in creation order)
        var ids = Enumerable.Range(RigBoneBase, boneNames.Count).ToList();
        var children = ids.ToDictionary(b => b, b => new List<int>()); var roots = new List<int>();
        foreach (int b in ids) { int p = parentOf[b - RigBoneBase]; if (p >= 0) children[p].Add(b); else roots.Add(b); }
        var rest = new VehicleProbe.ArmatureResult();
        VehicleProbe.RestFromEditBones(ids, children, roots, head, tail, roll, (b, parent, arm, offs, len) =>
        {
            rest.Bones.Add(b); rest.Parent[b] = parent;
            rest.ArmMat[b] = arm; rest.OffsBone[b] = offs; rest.Length[b] = len; rest.MatrixLocal[b] = VehicleProbe.ToRowMajor(arm);
            rest.EditHead[b] = head[b]; rest.EditTail[b] = tail[b]; rest.EditRoll[b] = roll[b];
        });
        var order = new List<int>();
        void Walk(int b) { order.Add(b); foreach (int c in children[b]) Walk(c); }
        foreach (int r in roots) Walk(r);
        foreach (int b in ids) names.BoneOfJoint[b] = boneNames[b - RigBoneBase];
        var identity = new float[16]; identity[0] = identity[5] = identity[10] = identity[15] = 1f;
        var armObj = new BlenderRigAnim.SceneObject
        {
            Name = armObjName, Type = "ARMATURE", DataName = armDataName, World = (float[])identity.Clone(), Local = (float[])identity.Clone(),
            Props = new[] { new float[3], new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f } },
        };
        s.Objects.Add(armObj);
        var ad = new BlenderRigAnim.ArmatureData
        {
            Object = armObj, Rest = rest, BoneNodes = order, PoseProps = new Dictionary<int, float[][]>(), PoseMats = new Dictionary<int, float[]>(),
        };
        foreach (int b in order) ad.PoseProps[b] = new[] { new float[3], new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f } };
        s.Armature = ad;

        // ---- the RUDDER, PRESERVE and FLIP name sets
        HashSet<string> NameSet(string role, string label)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in a.Names.TryGetValue(role, out var l) ? l : new List<string>())
            {
                var o = FindOpt(n);
                if (o == null) { s.Logs3.Add("VEHICLE WARN: " + label + " part '" + n + "' not found — skipped"); Note(s, "a missing " + label + " part (a WARN)"); continue; }
                set.Add(o.Name);
            }
            return set;
        }
        var rudders = NameSet("rudders", "rudder");
        if (rudders.Count > 0) s.Logs3.Add("VEHICLE RUDDER: " + rudders.Count + " part(s) — double-sided at export, always visible, authored winding kept");
        var preserve = NameSet("preserve", "preserve");
        if (preserve.Count > 0)
        {
            double pr = a.Reduce.TryGetValue("preserve", out var prv) ? prv : 0.0;
            s.Logs3.Add("VEHICLE PRESERVE: " + preserve.Count + " part(s) — no winding fix, no doubling" +
                        (pr > 0.5 ? "; reduced by dial " + BlenderRigAnim.PyFixed(pr, 0) + "%" : "; shipped byte-identical (reduce dial 0)"));
        }
        var flip = NameSet("flip", "flip");
        if (flip.Count > 0) s.Logs3.Add("VEHICLE FLIP: " + flip.Count + " part(s) — winding reversed once at export (XOR with the inside-out fix)");

        // ---- rigid skinning: one group per part, its bone, every vertex at 1.0; the Armature modifier; parented to the rig
        foreach (var o in objs)
        {
            string bname = boneOf.TryGetValue(o.Name, out var wb) ? wb : bodyBone;
            var d = o.Mesh;
            d.Groups = new List<string> { bname };
            d.DefNr = new List<int>[d.Vertices]; d.DefWeight = new List<float>[d.Vertices];
            for (int v = 0; v < d.Vertices; v++) { d.DefNr[v] = new List<int> { 0 }; d.DefWeight[v] = new List<float> { 1f }; }
            d.ArmatureModifier = armObjName;
            o.Parent = armObj;   // `o.parent = arm`: the local transform and the parent inverse kept; the armature is the identity
        }

        // ---- the join per bone
        var groups = new List<(string key, List<BlenderRigAnim.SceneObject> members)>();   // dict insertion order
        var groupAt = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in objs)
        {
            string key = rudders.Contains(o.Name) ? "__rud__" + o.Name
                       : preserve.Contains(o.Name) ? "__keep__" + o.Name
                       : flip.Contains(o.Name) ? "__flip__" + o.Name
                       : boneOf.TryGetValue(o.Name, out var wb) ? wb : bodyBone;
            if (!groupAt.TryGetValue(key, out int gi)) { groupAt[key] = groups.Count; groups.Add((key, new List<BlenderRigAnim.SceneObject> { o })); }
            else groups[gi].members.Add(o);
        }
        foreach (var (key, members) in groups)
        {
            var active = members[0];
            if (members.Count > 1)
            {
                var why = BlenderRigAnim.JoinMeshes(s, active, members);
                if (why != null) { s.Fallback = "stage 3: " + why; return; }
                foreach (var m in members) if (m != active) names.ObjectPool.Remove(m.Name);   // the joined objects are gone: their names are free
                Note(s, "parts joined per bone");
            }
            string want = "Mesh_" + key;
            if (active.Name != want) { names.ObjectPool.Remove(active.Name); active.Name = names.ObjectPool.Unique(want); }
            if (active.Name != want) Note(s, "a joined mesh's name taken (Mesh_<bone>.NNN)");
        }
        s.Logs3.Add("VEHICLE join: " + groups.Count + " mesh(es), one per bone");
    }

    /// <summary>axle_axis(s): the forced axis (X, Y, Z), else the thinnest extent - min over range(3) keeps the first on a tie.</summary>
    static float[] AxleAxis(string axisArg, float[] s)
    {
        if (axisArg == "X") return new[] { 1f, 0f, 0f };
        if (axisArg == "Y") return new[] { 0f, 1f, 0f };
        if (axisArg == "Z") return new[] { 0f, 0f, 1f };
        int k = 0; for (int i = 1; i < 3; i++) if (s[i] < s[k]) k = i;
        var r = new float[3]; r[k] = 1f; return r;
    }

    /// <summary>mathutils Vector.length: the double square root of the float32 products summed in double, the last component first.</summary>
    static double VecLength(float[] v) { double d = 0.0; for (int i = v.Length - 1; i >= 0; i--) d += (double)(float)(v[i] * v[i]); return Math.Sqrt(d); }

    /// <summary>Python's repr() of a str, as `%s` of a list prints its items: single quotes unless the text holds a single quote and no
    /// double one; the backslash, the chosen quote, \t \n \r and the other control characters escaped. Non-ASCII printable text is
    /// kept (str.isprintable); the rare non-printable Unicode categories are not modelled.</summary>
    static string PyStrRepr(string t)
    {
        char q = t.Contains('\'') && !t.Contains('"') ? '"' : '\'';
        var b = new System.Text.StringBuilder(); b.Append(q);
        foreach (char ch in t)
        {
            if (ch == '\\') b.Append("\\\\");
            else if (ch == q) b.Append('\\').Append(ch);
            else if (ch == '\t') b.Append("\\t");
            else if (ch == '\n') b.Append("\\n");
            else if (ch == '\r') b.Append("\\r");
            else if (ch < 0x20 || ch == 0x7f) b.Append("\\x").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
            else b.Append(ch);
        }
        return b.Append(q).ToString();
    }
}
