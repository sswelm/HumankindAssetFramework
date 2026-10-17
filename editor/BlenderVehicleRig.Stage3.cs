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
// Parts d2 to d4 add their branches between the wheels and the armature's rest:
//   - d2: the sail (its SailF1..F3 fold chain and the canvas skinned by height band), rigging on the sail, the flag;
//   - d3: the turret, the gun (the muzzle pin, the pivot along the tube, the recoil arm and barrel), the trails;
//   - d4: the main and tail rotors - one cluster per group after the wheels, its bones named on from them: the main hub (the part
//     nearest the group's mean) with its vertices' least-variance axle (np.cov and np.linalg.eigh as Blender's numpy computes them:
//     BlenderNumpy), the tail at the blades' mean with its axle 90 degrees about the vertical from the boom; the forced axes
//     (argv[6] for the main, argv[28] for the tail), the tail trim (Vector.rotate by Quaternion(axis, radians(deg)): yaw, then
//     pitch), the main bone along -axle, the tail bone along the in-disc direction nearest up with align_roll(axle x it).
// The oars (d5) and the tread rig (d6) are left to Blender by name, as is a scene this part does not expect (an armature still
// there, a parented part, a mesh still carrying an Armature modifier, a main rotor hub numpy's eigh takes a branch of not ported).
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
        ("oars", "the oar bones (d5)"), ("tracks", "the tread rig (d6)"),
    };

    /// <summary>Stage 3 on the scene stage 2 left: the rig. Lines go to s.Logs3; a branch not modelled sets s.Fallback; the script's exit
    /// sets s.Exits.</summary>
    public static void RunStage3(BlenderRigAnim.Stage1 s, Args a)
    {
        if (s.Fallback != null || s.Exits || s.Dies != null) return;
        foreach (var (role, branch) in Stage3Later)
        {
            if (role == "tracks" && a.TracksStatic) continue;   // tracks_static empties track_names: the treads rig as plain body parts
            if (a.Names.TryGetValue(role, out var l) && l.Count > 0) { s.Fallback = "stage 3: " + branch + " is not modelled yet"; return; }
        }
        if (a.TracksStatic && a.Names.TryGetValue("tracks", out var st) && st.Count > 0) Note(s, "static tracks rigged as body parts");
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
            s.Logs3.Add("VEHICLE ERROR: part '" + name + "' not found. Parts: [" + string.Join(", ", objs.Select(o => PyStrRepr(o.Name))) + "]");   // '%s' of the name, repr of the list
            s.Exits = true; Note(s, "a missing structural part: the script's exit in stage 3");
            return null;
        }
        BlenderRigAnim.SceneObject FindOpt(string name) => objs.FirstOrDefault(o => o.Name == name);
        void WorldBbox(BlenderRigAnim.SceneObject o, out float[] c, out float[] size)
        {
            // Object.bound_box: the bounds as last evaluated - stage 2's last tier can leave a part's stale (no operator since its to_mesh)
            float[] lmn, lmx;
            if (o.EvalMin != null) { lmn = o.EvalMin; lmx = o.EvalMax; Note(s, "a wheel measured on its stale bound_box"); } else LocalBox(o.Mesh, out lmn, out lmx);
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
        var clusters = new List<(double m, float[] c, float[] sz, List<string> names, float[] axle, bool rotor, bool tail)>();
        foreach (var (m, c, sz, wn) in wheelInfo)
        {
            int home = -1;
            for (int k = 0; k < clusters.Count; k++)
            {
                var d = new[] { (float)(c[0] - clusters[k].c[0]), (float)(c[1] - clusters[k].c[1]), (float)(c[2] - clusters[k].c[2]) };
                if (VecLength(d) <= 0.75 * clusters[k].m) { home = k; break; }
            }
            if (home < 0) clusters.Add((m, c, sz, new List<string> { wn }, null, false, false));
            else clusters[home].names.Add(wn);
        }
        if (clusters.Count > 0) Note(s, "wheel clusters");

        // ---- ROTOR / TAIL ROTOR (d4, vehicle_rig.py 1545-1608): each group ONE cluster after the wheels, its axle precomputed
        foreach (var (role, isTail) in new[] { ("rotors", false), ("tailrotors", true) })
        {
            if (!a.Names.TryGetValue(role, out var grp) || grp.Count == 0) continue;
            var boxes = new List<(float[] c, float[] sz)>();
            foreach (var n in grp) { var o = Find(n); if (o == null) return; WorldBbox(o, out var bc, out var bs); boxes.Add((bc, bs)); }
            var mean = SumOver(boxes.Select(b => b.c), boxes.Count);   // sum(centers, Vector((0, 0, 0))) / len(centers)
            string over = isTail ? a.TailAxisArg : a.AxisArg;
            float[] hubC, autoAxle; string axleSrc;
            if (isTail)
            {
                // the pivot is the blades' mean centre; the axle 90 degrees about the vertical from the boom (fan -> the model's centre)
                hubC = mean;
                var centres = new List<float[]>();
                foreach (var o in objs) { WorldBbox(o, out var oc, out _); centres.Add(oc); }
                var modelC = SumOver(centres, Math.Max(1, objs.Count));
                var dd = Sub(modelC, mean);
                var dxy = new[] { dd[0], dd[1], 0f };
                autoAxle = VecLength(dxy) > 1e-6 ? Normalized(Cross(new[] { 0f, 0f, 1f }, dxy)) : new[] { 0f, 1f, 0f };
                axleSrc = "90 deg to the boom ring (lateral)";
            }
            else
            {
                // the hub: the part whose centre lies nearest the mean (min: the first of a tie); the axle its vertices' least-variance
                // direction - np.cov / np.linalg.eigh as Blender's numpy computes them (BlenderNumpy), the first eigenvector normalized
                string hubName = null; double best = 0.0;
                foreach (var n in grp)
                {
                    WorldBbox(Find(n), out var hc, out _);
                    double k = VecLength(Sub(hc, mean));
                    if (hubName == null || k < best) { hubName = n; best = k; }
                }
                var hub = Find(hubName);
                WorldBbox(hub, out hubC, out _);
                if (over == "X" || over == "Y" || over == "Z") { autoAxle = null; axleSrc = null; }   // forced: the eigh's result (or its caught failure) is discarded
                else
                {
                    // hub_obj.matrix_world @ v.co: the identity on every input that reaches here (transform_apply leaves every mesh's world
                    // at the identity, an animated one too: Blender's dump), kept as the script writes it
                    var it = VehicleProbe.ToRowMajor(hub.World); var pts = new List<double[]>(hub.Mesh.Vertices);
                    for (int v = 0; v < hub.Mesh.Vertices; v++)
                    {
                        float x = hub.Mesh.Positions[3 * v], y = hub.Mesh.Positions[3 * v + 1], z = hub.Mesh.Positions[3 * v + 2];
                        pts.Add(new double[] { MulPointRow(it, 0, x, y, z), MulPointRow(it, 1, x, y, z), MulPointRow(it, 2, x, y, z) });
                    }
                    var cov = BlenderNumpy.CovOfCentred(pts);
                    if (cov == null || cov.Any(x => double.IsNaN(x) || double.IsInfinity(x)))
                    { s.Fallback = "stage 3: the main rotor hub's covariance of " + pts.Count + " vertex(es) or a non-finite one is not modelled yet"; return; }
                    var eig = BlenderNumpy.Eigh3(cov);
                    if (eig == null) { s.Fallback = "stage 3: the main rotor hub's eigh (a rescaled or unconverged LAPACK branch) is not modelled yet"; return; }
                    var ev = eig.Value.v;
                    autoAxle = Normalized(new[] { (float)ev[0], (float)ev[3], (float)ev[6] });
                    axleSrc = "hub pole-to-pole";
                    Note(s, "a main rotor's hub axle (numpy's eigh)");
                }
            }
            float[] axle;
            if (over == "X" || over == "Y" || over == "Z") { axle = AxleAxis(over, null); axleSrc = "forced " + over; }
            else axle = autoAxle;
            // the tail trim: yaw about the vertical, then pitch about (0, 0, 1) x axle; Vector.rotate(Quaternion(axis, radians(deg)))
            if (isTail && (Math.Abs(a.TailYaw) > 1e-6 || Math.Abs(a.TailPitch) > 1e-6))
            {
                axle = RotateQt(axle, BlenderDeploy.QuaternionAxisAngle(new[] { 0f, 0f, 1f }, a.TailYaw * (Math.PI / 180.0)));
                var pax = Cross(new[] { 0f, 0f, 1f }, axle);
                if (VecLength(pax) > 1e-6) axle = RotateQt(axle, BlenderDeploy.QuaternionAxisAngle(Normalized(pax), a.TailPitch * (Math.PI / 180.0)));
                axle = Normalized(axle);
                axleSrc += " + trim yaw " + BlenderRigAnim.PyFixed(a.TailYaw, 1) + " pitch " + BlenderRigAnim.PyFixed(a.TailPitch, 1);
                Note(s, "a trimmed tail rotor axle");
            }
            // the group's combined box: min(c - s / 2) and max(c + s / 2) in Python floats (min/max keep the first of a tie or NaN), as Vectors
            var gmn = new float[3]; var gmx = new float[3];
            for (int k = 0; k < 3; k++)
            {
                double lo = 0.0, hi = 0.0;
                for (int b = 0; b < boxes.Count; b++)
                {
                    double l = boxes[b].c[k] - boxes[b].sz[k] / 2.0, h = boxes[b].c[k] + boxes[b].sz[k] / 2.0;
                    if (b == 0 || l < lo) lo = l;
                    if (b == 0 || h > hi) hi = h;
                }
                gmn[k] = (float)lo; gmx[k] = (float)hi;
            }
            var gs = Sub(gmx, gmn);
            clusters.Add((MaxOf(gs), hubC, gs, new List<string>(grp), axle, true, isTail));
            s.Logs3.Add("VEHICLE " + (isTail ? "tail" : "main") + " rotor: " + grp.Count + " part(s), pivot (" + P2(hubC[0]) + "," + P2(hubC[1]) + "," + P2(hubC[2])
                + "), axle (" + P3(axle[0]) + "," + P3(axle[1]) + "," + P3(axle[2]) + ") [" + axleSrc + "]");
            s.Logs3.Add("VEHICLE rotor hub: " + grp.Count + " part(s) -> bone at hub (" + P2(hubC[0]) + "," + P2(hubC[1]) + "," + P2(hubC[2]) + "), axle=("
                + PyRepr(axle[0]) + ", " + PyRepr(axle[1]) + ", " + PyRepr(axle[2]) + ") (least-spread of centres)");
            Note(s, isTail ? "a tail rotor" : "a main rotor");
        }

        // ---- the armature (mode_set(EDIT) first: the view layer updated - every bound_box current again)
        foreach (var so in s.Objects) { so.EvalMin = null; so.EvalMax = null; }
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
            var ax = cl.axle ?? AxleAxis(a.AxisArg, cl.sz);   // a rotor carries its axle; a wheel infers it
            double q = cl.m * 0.25; float len = (float)(q > 0.05 ? q : 0.05);   // max(0.05, max(s) * 0.25) into the float multiply (Python: 0.05 on a NaN)
            float[] t; float tailRoll = 0f;
            if (cl.rotor && cl.tail)
            {
                // the TAIL FAN: the bone runs along the in-disc direction nearest world up (else nearest +Y), align_roll(axle x that)
                var nax = Normalized(ax);
                var yb = Sub(new[] { 0f, 0f, 1f }, Scale(nax, nax[2]));
                if (VecLength(yb) < 1e-3) yb = Sub(new[] { 0f, 1f, 0f }, Scale(nax, nax[1]));
                yb = Normalized(yb);
                var zb = Normalized(Cross(nax, yb));
                t = new[] { (float)(cl.c[0] + (float)(yb[0] * len)), (float)(cl.c[1] + (float)(yb[1] * len)), (float)(cl.c[2] + (float)(yb[2] * len)) };
                tailRoll = VehicleProbe.RollToVector(cl.c, t, zb);   // EditBone.align_roll: ED_armature_ebone_roll_to_vector(bone, vector, false)
            }
            else
            {
                // wheels along +axle; the MAIN rotor along -axle (ax * -1.0 first, then the length)
                float dirn = cl.rotor ? -1f : 1f;
                t = new[] { (float)(cl.c[0] + (float)((float)(ax[0] * dirn) * len)), (float)(cl.c[1] + (float)((float)(ax[1] * dirn) * len)), (float)(cl.c[2] + (float)((float)(ax[2] * dirn) * len)) };
            }
            int id = AddBone(string.Format(CultureInfo.InvariantCulture, "Wheel_{0:00}", i), (float[])cl.c.Clone(), t, body);
            roll[id] = tailRoll;
            foreach (var wn in cl.names) boneOf[wn] = boneNames[id - RigBoneBase];
        }
        // ---- the TURRET bone: the turret parts' combined box (_combined_bbox: each part's world_bbox, min(c - s/2) and max(c + s/2) in
        // Python floats, the Vectors' (mn + mx) / 2 and mx - mn in float32), the tail up by max(0.05, its largest extent * 0.25)
        bool CombinedBox(List<string> partNames, out float[] cc, out float[] cs)
        {
            cc = cs = null;
            double[] mn = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity }, mx = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (var pn in partNames)
            {
                var o = Find(pn); if (o == null) return false;
                WorldBbox(o, out var c, out var sz);
                for (int k = 0; k < 3; k++) { double lo = c[k] - sz[k] / 2.0, hi = c[k] + sz[k] / 2.0; if (lo < mn[k]) mn[k] = lo; if (hi > mx[k]) mx[k] = hi; }
            }
            var fmn = new[] { (float)mn[0], (float)mn[1], (float)mn[2] }; var fmx = new[] { (float)mx[0], (float)mx[1], (float)mx[2] };
            cc = new float[3]; cs = new float[3];
            for (int k = 0; k < 3; k++) { cc[k] = (float)((float)(fmn[k] + fmx[k]) * 0.5f); cs[k] = (float)(fmx[k] - fmn[k]); }
            return true;
        }
        float[] Up(float[] h, float[] size) { double q = MaxOf(size) * 0.25; float r = (float)(q > 0.05 ? q : 0.05); return new[] { (float)(h[0] + 0f), (float)(h[1] + 0f), (float)(h[2] + r) }; }
        var turretNames = a.Names.TryGetValue("turrets", out var tnl) ? tnl : new List<string>();
        int turretBone = -1;
        if (turretNames.Count > 0)
        {
            if (!CombinedBox(turretNames, out var tc, out var ts)) return;
            turretBone = AddBone("Turret", tc, Up(tc, ts), body);
            foreach (var tn in turretNames) boneOf[tn] = "Turret";
            Note(s, "the turret bone");
        }
        // ---- the GUN assembly: ONE Gun bone for barrel + brake + cradle, its head slid along the TUBE's breech->muzzle span by gun_pivot
        // (the breech the end nearer the body bone's head, a marked brake pinning the tip), parented to the Turret when there is one;
        // under recoil the tube moves to a Barrel bone hung under a far RecoilArm pivot (the slide faked as a shallow arc)
        var gunNames0 = a.Names.TryGetValue("guns", out var gl0) ? gl0 : new List<string>();
        var muzzleNames = a.Names.TryGetValue("muzzles", out var ml0) ? ml0 : new List<string>();
        var cradleNames = a.Names.TryGetValue("cradles", out var cl0) ? cl0 : new List<string>();
        if (gunNames0.Count > 0 || muzzleNames.Count > 0 || cradleNames.Count > 0)
        {
            var tubeNames = gunNames0.Concat(muzzleNames.Where(m => !gunNames0.Contains(m))).ToList();
            var gunNames = tubeNames.Concat(cradleNames.Where(c => !tubeNames.Contains(c))).ToList();
            if (!CombinedBox(gunNames, out var gc, out var gs)) return;
            float[][] gunAxis = null, gunBore = null;
            List<float[]> Points(List<string> partNames)
            {
                var pts = new List<float[]>();
                foreach (var pn in partNames)
                {
                    var o = Find(pn); if (o == null) return null;
                    var it = VehicleProbe.ToRowMajor(o.World); var P = o.Mesh.Positions;
                    for (int v = 0; v < o.Mesh.Vertices; v++)
                        pts.Add(new[] { MulPointRow(it, 0, P[3 * v], P[3 * v + 1], P[3 * v + 2]), MulPointRow(it, 1, P[3 * v], P[3 * v + 1], P[3 * v + 2]), MulPointRow(it, 2, P[3 * v], P[3 * v + 1], P[3 * v + 2]) });
                }
                return pts;
            }
            float[] Sub(float[] p, float[] q) => new[] { (float)(p[0] - q[0]), (float)(p[1] - q[1]), (float)(p[2] - q[2]) };
            var gpts = Points(tubeNames.Count > 0 ? tubeNames : gunNames); if (gpts == null) return;
            var bodyHead = head[body];
            if (gpts.Count >= 4)
            {
                int glong = LongAxis(gpts);
                float[] breech = gpts[0], muzzle = gpts[0];
                foreach (var p in gpts) { if (p[glong] > breech[glong]) breech = p; if (p[glong] < muzzle[glong]) muzzle = p; }   // max / min: the first of a tie
                if (VecLength(Sub(muzzle, bodyHead)) < VecLength(Sub(breech, bodyHead))) { var sw = breech; breech = muzzle; muzzle = sw; }
                if (muzzleNames.Count > 0)
                {
                    var mpts = Points(muzzleNames); if (mpts == null) return;
                    if (mpts.Count > 0)
                    {
                        float[] far = mpts[0]; double fd = VecLength(Sub(far, breech));
                        foreach (var p in mpts) { double d = VecLength(Sub(p, breech)); if (d > fd) { fd = d; far = p; } }
                        muzzle = far;
                        s.Logs3.Add("VEHICLE MUZZLE " + muzzleNames.Count + " part(s) -> tip=(" + P2(muzzle[0]) + ", " + P2(muzzle[1]) + ", " + P2(muzzle[2]) + ") (welded to the Gun bone: a brake elevates and recoils with the tube)");
                        Note(s, "a muzzle brake pinning the tip");
                    }
                }
                float piv = (float)a.GunPivot; var span = Sub(muzzle, breech);
                gc = new[] { (float)(breech[0] + (float)(span[0] * piv)), (float)(breech[1] + (float)(span[1] * piv)), (float)(breech[2] + (float)(span[2] * piv)) };
                gunAxis = new[] { (float[])gc.Clone(), (float[])muzzle.Clone() }; gunBore = new[] { (float[])breech.Clone(), (float[])muzzle.Clone() };
                s.Logs3.Add("VEHICLE gun pivot " + BlenderRigAnim.PyFixed(a.GunPivot, 2) + " -> head=(" + P2(gc[0]) + ", " + P2(gc[1]) + ", " + P2(gc[2]) + ") (breech (" +
                            P2(breech[0]) + ", " + P2(breech[1]) + ", " + P2(breech[2]) + ") .. muzzle (" + P2(muzzle[0]) + ", " + P2(muzzle[1]) + ", " + P2(muzzle[2]) + "))");
                var off = Sub(muzzle, gc);
                s.Logs3.Add("VEHICLE MUZZLE fire origin (gun-local, SOURCE units — scale by the bake's size): " + BlenderRigAnim.PyFixed(off[0], 3) + ", " + BlenderRigAnim.PyFixed(off[1], 3) + ", " + BlenderRigAnim.PyFixed(off[2], 3));
                if (cradleNames.Count > 0) s.Logs3.Add("VEHICLE CRADLE " + cradleNames.Count + " part(s) welded to the Gun bone (elevates with the tube; excluded from the breech->muzzle span, and it is what will STAY when the barrel recoils)");
                var asm = Points(gunNames); if (asm == null) return;
                var bl = Sub(breech, gc); var ml = Sub(muzzle, gc); var dvec = Sub(ml, bl);
                if (VecLength(dvec) > 1e-9 && asm.Count > 0)
                {
                    var dn = Normalized(dvec);
                    double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                    foreach (var p in asm) { double pr = Dot(Sub(p, gc), dn); if (pr < lo) lo = pr; if (pr > hi) hi = pr; }
                    s.Logs3.Add("VEHICLE GUNSPAN bone=Gun breech=" + P6(bl[0]) + "," + P6(bl[1]) + "," + P6(bl[2]) + " muzzle=" + P6(ml[0]) + "," + P6(ml[1]) + "," + P6(ml[2]) + " extent=" + BlenderRigAnim.PyFixed(hi - lo, 6));
                }
            }
            int gunBone = AddBone("Gun", gc, Up(gc, gs), turretBone >= 0 ? turretBone : body);
            foreach (var gn in gunNames) boneOf[gn] = "Gun";
            Note(s, turretBone >= 0 ? "the gun bone on the turret" : "the gun bone");
            if (a.RecoilDist > 0.0 && tubeNames.Count > 0)
            {
                if (!CombinedBox(tubeNames, out var tc2, out var ts2)) return;
                float[] boreD = gunAxis != null ? Normalized(Sub(gunAxis[1], gunAxis[0])) : new[] { 0f, -1f, 0f };
                double slide = gunBore != null ? VecLength(Sub(gunBore[1], gunBore[0])) * a.RecoilDist : 1.0;
                double R = slide / BitConverter.Int64BitsToDouble(0x3faad53144273e73L);   // math.tan(math.radians(3.0)), as Blender's Python computes it
                var axis = Cross(boreD, new[] { 0f, 0f, 1f });
                if (VecLength(axis) < 1e-4) axis = Cross(boreD, new[] { 0f, 1f, 0f });
                axis = Normalized(axis);
                var offd = Normalized(Cross(axis, boreD));
                float Rf = (float)R;
                var pivot = new[] { (float)(tc2[0] + (float)(offd[0] * Rf)), (float)(tc2[1] + (float)(offd[1] * Rf)), (float)(tc2[2] + (float)(offd[2] * Rf)) };
                int arm = AddBone("RecoilArm", pivot, Up(pivot, ts2), gunBone);
                s.Logs3.Add("VEHICLE RECOIL ARM: pivot (" + P2(pivot[0]) + ", " + P2(pivot[1]) + ", " + P2(pivot[2]) + "), R=" + BlenderRigAnim.PyFixed(R, 1) + " for a " + BlenderRigAnim.PyFixed(slide, 2) +
                            "-unit slide (residual tilt 3.0 deg) — a bone's own translation does not render; an ancestor's rotation does");
                AddBone("Barrel", tc2, Up(tc2, ts2), arm);
                foreach (var tn in tubeNames) boneOf[tn] = "Barrel";
                s.Logs3.Add("VEHICLE BARREL bone split off for recoil: " + tubeNames.Count + " tube part(s) slide, " + cradleNames.Count + " cradle part(s) stay");
                Note(s, "recoil: the RecoilArm and Barrel bones");
            }
        }
        // ---- the TRAIL bones: one per trail name (find: a missing one is the exit), hinged at the arm's end nearer the body bone's head,
        // the tail half way down the arm (at least 0.05); fewer than 4 vertices, or ends closer than 1e-5, take no bone
        var trailNames = a.Names.TryGetValue("trails", out var trl) ? trl : new List<string>();
        for (int ti = 0; ti < trailNames.Count; ti++)
        {
            var o = Find(trailNames[ti]); if (o == null) return;
            var it = VehicleProbe.ToRowMajor(o.World); var P = o.Mesh.Positions; var pts = new List<float[]>();
            for (int v = 0; v < o.Mesh.Vertices; v++)
                pts.Add(new[] { MulPointRow(it, 0, P[3 * v], P[3 * v + 1], P[3 * v + 2]), MulPointRow(it, 1, P[3 * v], P[3 * v + 1], P[3 * v + 2]), MulPointRow(it, 2, P[3 * v], P[3 * v + 1], P[3 * v + 2]) });
            if (pts.Count < 4) { Note(s, "a trail of fewer than 4 vertices (no bone)"); continue; }
            int lng = LongAxis(pts);
            float[] lo = pts[0], hi = pts[0];
            foreach (var p in pts) { if (p[lng] < lo[lng]) lo = p; if (p[lng] > hi[lng]) hi = p; }
            var refH = head[body];
            float[] hinge, spade;
            var dl = new[] { (float)(lo[0] - refH[0]), (float)(lo[1] - refH[1]), (float)(lo[2] - refH[2]) };
            var dh = new[] { (float)(hi[0] - refH[0]), (float)(hi[1] - refH[1]), (float)(hi[2] - refH[2]) };
            if (VecLength(dl) <= VecLength(dh)) { hinge = lo; spade = hi; } else { hinge = hi; spade = lo; }
            var dir = new[] { (float)(spade[0] - hinge[0]), (float)(spade[1] - hinge[1]), (float)(spade[2] - hinge[2]) };
            double dlen = VecLength(dir);
            if (dlen < 1e-5) { Note(s, "a trail whose ends meet (no bone)"); continue; }
            var dn = Normalized(dir); double tl = dlen * 0.5; float tlf = (float)(tl > 0.05 ? tl : 0.05);
            string bn = string.Format(CultureInfo.InvariantCulture, "Trail_{0:00}", ti);
            int tb = AddBone(bn, hinge, new[] { (float)(hinge[0] + (float)(dn[0] * tlf)), (float)(hinge[1] + (float)(dn[1] * tlf)), (float)(hinge[2] + (float)(dn[2] * tlf)) }, body);
            boneOf[trailNames[ti]] = boneNames[tb - RigBoneBase];
            s.Logs3.Add("VEHICLE trail '" + trailNames[ti] + "' -> " + boneNames[tb - RigBoneBase] + " hinge=(" + P2(hinge[0]) + ", " + P2(hinge[1]) + ", " + P2(hinge[2]) + ") spade=(" + P2(spade[0]) + ", " + P2(spade[1]) + ", " + P2(spade[2]) + ")");
            Note(s, "a trail bone");
        }

        // ---- the SAIL bone: every found sail part on it; the head at the keel line (the lowest world bound_box corner of every mesh
        // with vertices) less 0.05 under the sails' box centre, the tail up by max(0.3, a quarter of the sails' height); with the fold,
        // SailF1..F3 chained below it with their heads on the fold lines (a quarter, half, three quarters down from the top)
        var sailFound = new List<string>(); double[] sailCuts = null;
        foreach (var n in a.Names.TryGetValue("sails", out var sl) ? sl : new List<string>())
        {
            var o = FindOpt(n);
            if (o == null) { s.Logs3.Add("VEHICLE WARN: sail part '" + n + "' not found — skipped"); Note(s, "a missing sail part (a WARN)"); continue; }
            sailFound.Add(o.Name); boneOf[o.Name] = "Sail";
        }
        double Keel() { double k = double.PositiveInfinity; foreach (var o in objs) foreach (var p in WorldCorners(o)) if (p[2] < k) k = p[2]; return k; }
        void BoxOf(List<string> partNames, out double[] mn, out double[] mx)
        {
            mn = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity }; mx = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (var pn in partNames) foreach (var p in WorldCorners(objs.First(o => o.Name == pn)))
                for (int k = 0; k < 3; k++) { if (p[k] < mn[k]) mn[k] = p[k]; if (p[k] > mx[k]) mx[k] = p[k]; }
        }
        if (sailFound.Count > 0)
        {
            BoxOf(sailFound, out var smn, out var smx);
            double keel = Keel();
            var sh0 = new[] { (float)(0.5 * (smn[0] + smx[0])), (float)(0.5 * (smn[1] + smx[1])), (float)(keel - 0.05) };
            float rise = (float)PyMax(0.3, 0.25 * (smx[2] - smn[2]));
            int sailBone = AddBone("Sail", sh0, new[] { (float)(sh0[0] + 0f), (float)(sh0[1] + 0f), (float)(sh0[2] + rise) }, body);
            if (a.SailFold)
            {
                double sh = PyMax(1e-6, smx[2] - smn[2]);
                sailCuts = new[] { smx[2] - sh * 1 / 4.0, smx[2] - sh * 2 / 4.0, smx[2] - sh * 3 / 4.0 };
                double scx = 0.5 * (smn[0] + smx[0]), scy = 0.5 * (smn[1] + smx[1]);
                float drop = (float)(-PyMax(0.1, 0.25 * sh / 4.0));
                int prev = sailBone;
                for (int fi = 0; fi < 3; fi++)
                {
                    var fh = new[] { (float)scx, (float)scy, (float)sailCuts[fi] };
                    prev = AddBone("SailF" + (fi + 1).ToString(CultureInfo.InvariantCulture), fh, new[] { (float)(fh[0] + 0f), (float)(fh[1] + 0f), (float)(fh[2] + drop) }, prev);
                }
                Note(s, "the sail's fold chain");
            }
            s.Logs3.Add("VEHICLE SAIL: " + sailFound.Count + " part(s) on one Sail bone" + (a.SailFold ? " + SailF1..F3 fold chain" : "") +
                        " (double-sided at export; " + (a.SailFold ? "FOLDED at the yard" : "struck/raised") + " by the 'Furl' clip)");
            Note(s, "the sail bone");
        }
        // ---- RIGGING rides the Sail bone when there is one: its own mesh, never Mesh_Sail (find_opt: a missing name is skipped silently)
        var rigOnSail = new HashSet<string>(StringComparer.Ordinal);
        if (sailFound.Count > 0)
            foreach (var n in a.Names.TryGetValue("rigging", out var rl) ? rl : new List<string>())
            {
                var o = FindOpt(n); if (o == null) continue;
                boneOf[o.Name] = "Sail"; rigOnSail.Add(o.Name);
            }
        if (rigOnSail.Count > 0) { s.Logs3.Add("VEHICLE RIGGING: " + rigOnSail.Count + " part(s) ride the Sail bone — struck/raised with the canvas, SINGLE-sided (own mesh, never doubled with Mesh_Sail)"); Note(s, "rigging on the sail bone"); }

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

        // ---- the FLAG bone: every found flag part on it; struck (the head at the keel line less 0.05) or, under flagfold=, hinged at
        // the flags' top; the tail up by max(0.3, a quarter of the flags' height)
        var flagFound = new List<string>();
        foreach (var n in a.Names.TryGetValue("flags", out var fl) ? fl : new List<string>())
        {
            var o = FindOpt(n);
            if (o == null) { s.Logs3.Add("VEHICLE WARN: flag part '" + n + "' not found — skipped"); Note(s, "a missing flag part (a WARN)"); continue; }
            flagFound.Add(o.Name); boneOf[o.Name] = "Flag";
        }
        if (flagFound.Count > 0)
        {
            BoxOf(flagFound, out var gmn, out var gmx);
            double keel = Keel();
            var fh = new[] { (float)(0.5 * (gmn[0] + gmx[0])), (float)(0.5 * (gmn[1] + gmx[1])), (float)(a.FlagFoldOn ? gmx[2] : keel - 0.05) };
            float rise = (float)PyMax(0.3, 0.25 * (gmx[2] - gmn[2]));
            AddBone("Flag", fh, new[] { (float)(fh[0] + 0f), (float)(fh[1] + 0f), (float)(fh[2] + rise) }, body);
            s.Logs3.Add("VEHICLE FLAG: " + flagFound.Count + " part(s) on one Flag bone — " +
                        (a.FlagFoldOn ? "FOLDS " + BlenderRigAnim.PyFixed(a.FlagFoldDeg, 0) + " deg at the top hinge over " + a.FlagFoldFrames + " frame(s) (deployed at idle, folded underway)"
                                      : "flies at anchor, struck below the keel through Spin (the opposite of sails)") + "; double-sided at export");
            Note(s, a.FlagFoldOn ? "the flag bone (folding)" : "the flag bone (struck)");
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
        s.Objects.Add(armObj);   // scene.collection.objects.link: the scene's own collection, after its objects, before any child collection's
        BlenderRigAnim.SortByCollection(s.Objects);
        var ad = new BlenderRigAnim.ArmatureData
        {
            Object = armObj, Rest = rest, BoneNodes = order, PoseProps = new Dictionary<int, float[][]>(), PoseMats = new Dictionary<int, float[]>(),
        };
        foreach (int b in order) ad.PoseProps[b] = new[] { new float[3], new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f } };
        s.Armature = ad;

        // ---- rigid skinning: one group per part, its bone, every vertex at 1.0; the Armature modifier; parented to the rig
        foreach (var o in objs)
        {
            string bname = boneOf.TryGetValue(o.Name, out var wb) ? wb : bodyBone;
            var d = o.Mesh;
            d.DefNr = new List<int>[d.Vertices]; d.DefWeight = new List<float>[d.Vertices];
            if (a.SailFold && sailFound.Contains(o.Name))
            {
                // the CANVAS bands: each vertex to the bone owning its height band (its world z against the fold cuts), a group per
                // non-empty band in band order
                var bandNames = new[] { "Sail", "SailF1", "SailF2", "SailF3" };
                var band = new int[d.Vertices]; var used = new bool[4];
                var it = VehicleProbe.ToRowMajor(o.World);
                for (int v = 0; v < d.Vertices; v++)
                {
                    float wz = MulPointRow(it, 2, d.Positions[3 * v], d.Positions[3 * v + 1], d.Positions[3 * v + 2]);
                    int bi = 0; foreach (double cz in sailCuts) if (wz < cz) bi++;
                    band[v] = bi; used[bi] = true;
                }
                var groupOf = new int[4]; d.Groups = new List<string>();
                for (int k = 0; k < 4; k++) if (used[k]) { groupOf[k] = d.Groups.Count; d.Groups.Add(bandNames[k]); }
                for (int v = 0; v < d.Vertices; v++) { d.DefNr[v] = new List<int> { groupOf[band[v]] }; d.DefWeight[v] = new List<float> { 1f }; }
                s.Logs3.Add("VEHICLE SAIL fold bands '" + o.Name + "': " + string.Join(" ", Enumerable.Range(0, 4).Select(k => bandNames[k] + "=" + band.Count(x => x == k))) + " vert(s)");
            }
            else
            {
                d.Groups = new List<string> { bname };
                for (int v = 0; v < d.Vertices; v++) { d.DefNr[v] = new List<int> { 0 }; d.DefWeight[v] = new List<float> { 1f }; }
            }
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
                       : rigOnSail.Contains(o.Name) ? "__rigsail__"
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

    /// <summary>`o.matrix_world @ Vector(c)` for the 8 corners of o.bound_box (the bounds as last evaluated): mathutils' Matrix @ Vector,
    /// the float32 products summed in double, each component rounded to float. Only the corners' min and max are ever taken.</summary>
    static List<float[]> WorldCorners(BlenderRigAnim.SceneObject o)
    {
        float[] mn, mx;
        if (o.EvalMin != null) { mn = o.EvalMin; mx = o.EvalMax; } else LocalBox(o.Mesh, out mn, out mx);
        var it = VehicleProbe.ToRowMajor(o.World); var r = new List<float[]>(8);
        for (int c = 0; c < 8; c++)
        {
            float x = (c & 4) == 0 ? mn[0] : mx[0], y = (c & 2) == 0 ? mn[1] : mx[1], z = (c & 1) == 0 ? mn[2] : mx[2];
            r.Add(new[] { MulPointRow(it, 0, x, y, z), MulPointRow(it, 1, x, y, z), MulPointRow(it, 2, x, y, z) });
        }
        return r;
    }

    /// <summary>One row of mathutils' Matrix(4x4) @ Vector(3): the float32 products and the translation summed in double, rounded to float.</summary>
    static float MulPointRow(float[] it, int row, float x, float y, float z)
    {
        double d = 0.0;
        d += (double)(float)(it[row * 4 + 0] * x); d += (double)(float)(it[row * 4 + 1] * y); d += (double)(float)(it[row * 4 + 2] * z); d += (double)(float)(it[row * 4 + 3] * 1f);
        return (float)d;
    }

    static string P2(float v) => BlenderRigAnim.PyFixed(v, 2);
    static string P3(float v) => BlenderRigAnim.PyFixed(v, 3);

    /// <summary>repr() of a Python float (a mathutils component): nan and inf as Python writes them.</summary>
    static string PyRepr(double v) => double.IsNaN(v) ? "nan" : double.IsInfinity(v) ? (v > 0 ? "inf" : "-inf") : PyFloatRepr.Repr(v);

    /// <summary>sum(vectors, Vector((0, 0, 0))) / n: float32 sums from zero in order, then times 1.0f / n (Vector division).</summary>
    static float[] SumOver(IEnumerable<float[]> vs, int n)
    {
        var r = new float[3];
        foreach (var v in vs) for (int k = 0; k < 3; k++) r[k] = (float)(r[k] + v[k]);
        float f = (float)(1.0f / (float)n);
        return new[] { (float)(r[0] * f), (float)(r[1] * f), (float)(r[2] * f) };
    }

    static float[] Sub(float[] a, float[] b) => new[] { (float)(a[0] - b[0]), (float)(a[1] - b[1]), (float)(a[2] - b[2]) };

    /// <summary>Vector * float: each component times the float32 scalar.</summary>
    static float[] Scale(float[] v, float f) => new[] { (float)(v[0] * f), (float)(v[1] * f), (float)(v[2] * f) };

    /// <summary>Vector.rotate(Quaternion): mathutils_any_to_rotmat (normalize_qt_qt, quat_to_mat3), then mul_m3_v3 in float32.</summary>
    static float[] RotateQt(float[] v, float[] quat)
    {
        var q = (float[])quat.Clone(); VehicleProbe.NormalizeQt(q);
        var m = BlenderChannelExport.QuatToMat3(q);   // m[col][row]
        float[] r = new float[3];
        for (int k = 0; k < 3; k++) r[k] = (float)((float)((float)(m[0][k] * v[0]) + (float)(m[1][k] * v[1])) + (float)(m[2][k] * v[2]));
        return r;
    }
    static string P6(float v) => BlenderRigAnim.PyFixed(v, 6);

    /// <summary>max(range(3), key=lambda i: mx[i] - mn[i]) over the points' component extremes (float32 values, the difference in
    /// double): the first of a tie.</summary>
    static int LongAxis(List<float[]> pts)
    {
        var mn = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity }; var mx = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        foreach (var p in pts) for (int k = 0; k < 3; k++) { if (p[k] < mn[k]) mn[k] = p[k]; if (p[k] > mx[k]) mx[k] = p[k]; }
        int best = 0; for (int k = 1; k < 3; k++) if ((double)mx[k] - mn[k] > (double)mx[best] - mn[best]) best = k;
        return best;
    }

    /// <summary>Vector.normalized(): normalize_vn_vn - the squares summed in DOUBLE from the last component, sqrt to float, each component
    /// times 1.0f / that; zero below 1e-35.</summary>
    static float[] Normalized(float[] v)
    {
        double d = 0.0; for (int i = v.Length - 1; i >= 0; i--) d += (double)v[i] * (double)v[i];
        if (!(d > 1.0e-35)) return new float[v.Length];
        float s = (float)Math.Sqrt(d), f = (float)(1.0f / s); var r = new float[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = (float)(v[i] * f);
        return r;
    }

    /// <summary>Vector.cross(): cross_v3_v3v3 in float32.</summary>
    static float[] Cross(float[] a, float[] b) => new[]
    {
        (float)((float)(a[1] * b[2]) - (float)(a[2] * b[1])), (float)((float)(a[2] * b[0]) - (float)(a[0] * b[2])), (float)((float)(a[0] * b[1]) - (float)(a[1] * b[0])),
    };

    /// <summary>Vector.dot(): dot_vn_vn - the float32 products summed in double from the last component.</summary>
    static double Dot(float[] a, float[] b) { double d = 0.0; for (int i = a.Length - 1; i >= 0; i--) d += (double)(float)(a[i] * b[i]); return d; }

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

    // str.isprintable() ranges generated from Blender 5.1.2's Python 3.13.9 (Unicode 15.1):
    // [(start, end) for maximal runs where not chr(codepoint).isprintable()], over 0..0x10ffff.
    // .NET Framework/Mono use older Unicode data and would escape newer printable names differently.
    static readonly int[] PyNonPrintable =
    {
        0x0, 0x1f, 0x7f, 0xa0, 0xad, 0xad, 0x378, 0x379, 0x380, 0x383, 0x38b, 0x38b, 0x38d, 0x38d, 0x3a2, 0x3a2,
        0x530, 0x530, 0x557, 0x558, 0x58b, 0x58c, 0x590, 0x590, 0x5c8, 0x5cf, 0x5eb, 0x5ee, 0x5f5, 0x605, 0x61c, 0x61c,
        0x6dd, 0x6dd, 0x70e, 0x70f, 0x74b, 0x74c, 0x7b2, 0x7bf, 0x7fb, 0x7fc, 0x82e, 0x82f, 0x83f, 0x83f, 0x85c, 0x85d,
        0x85f, 0x85f, 0x86b, 0x86f, 0x88f, 0x897, 0x8e2, 0x8e2, 0x984, 0x984, 0x98d, 0x98e, 0x991, 0x992, 0x9a9, 0x9a9,
        0x9b1, 0x9b1, 0x9b3, 0x9b5, 0x9ba, 0x9bb, 0x9c5, 0x9c6, 0x9c9, 0x9ca, 0x9cf, 0x9d6, 0x9d8, 0x9db, 0x9de, 0x9de,
        0x9e4, 0x9e5, 0x9ff, 0xa00, 0xa04, 0xa04, 0xa0b, 0xa0e, 0xa11, 0xa12, 0xa29, 0xa29, 0xa31, 0xa31, 0xa34, 0xa34,
        0xa37, 0xa37, 0xa3a, 0xa3b, 0xa3d, 0xa3d, 0xa43, 0xa46, 0xa49, 0xa4a, 0xa4e, 0xa50, 0xa52, 0xa58, 0xa5d, 0xa5d,
        0xa5f, 0xa65, 0xa77, 0xa80, 0xa84, 0xa84, 0xa8e, 0xa8e, 0xa92, 0xa92, 0xaa9, 0xaa9, 0xab1, 0xab1, 0xab4, 0xab4,
        0xaba, 0xabb, 0xac6, 0xac6, 0xaca, 0xaca, 0xace, 0xacf, 0xad1, 0xadf, 0xae4, 0xae5, 0xaf2, 0xaf8, 0xb00, 0xb00,
        0xb04, 0xb04, 0xb0d, 0xb0e, 0xb11, 0xb12, 0xb29, 0xb29, 0xb31, 0xb31, 0xb34, 0xb34, 0xb3a, 0xb3b, 0xb45, 0xb46,
        0xb49, 0xb4a, 0xb4e, 0xb54, 0xb58, 0xb5b, 0xb5e, 0xb5e, 0xb64, 0xb65, 0xb78, 0xb81, 0xb84, 0xb84, 0xb8b, 0xb8d,
        0xb91, 0xb91, 0xb96, 0xb98, 0xb9b, 0xb9b, 0xb9d, 0xb9d, 0xba0, 0xba2, 0xba5, 0xba7, 0xbab, 0xbad, 0xbba, 0xbbd,
        0xbc3, 0xbc5, 0xbc9, 0xbc9, 0xbce, 0xbcf, 0xbd1, 0xbd6, 0xbd8, 0xbe5, 0xbfb, 0xbff, 0xc0d, 0xc0d, 0xc11, 0xc11,
        0xc29, 0xc29, 0xc3a, 0xc3b, 0xc45, 0xc45, 0xc49, 0xc49, 0xc4e, 0xc54, 0xc57, 0xc57, 0xc5b, 0xc5c, 0xc5e, 0xc5f,
        0xc64, 0xc65, 0xc70, 0xc76, 0xc8d, 0xc8d, 0xc91, 0xc91, 0xca9, 0xca9, 0xcb4, 0xcb4, 0xcba, 0xcbb, 0xcc5, 0xcc5,
        0xcc9, 0xcc9, 0xcce, 0xcd4, 0xcd7, 0xcdc, 0xcdf, 0xcdf, 0xce4, 0xce5, 0xcf0, 0xcf0, 0xcf4, 0xcff, 0xd0d, 0xd0d,
        0xd11, 0xd11, 0xd45, 0xd45, 0xd49, 0xd49, 0xd50, 0xd53, 0xd64, 0xd65, 0xd80, 0xd80, 0xd84, 0xd84, 0xd97, 0xd99,
        0xdb2, 0xdb2, 0xdbc, 0xdbc, 0xdbe, 0xdbf, 0xdc7, 0xdc9, 0xdcb, 0xdce, 0xdd5, 0xdd5, 0xdd7, 0xdd7, 0xde0, 0xde5,
        0xdf0, 0xdf1, 0xdf5, 0xe00, 0xe3b, 0xe3e, 0xe5c, 0xe80, 0xe83, 0xe83, 0xe85, 0xe85, 0xe8b, 0xe8b, 0xea4, 0xea4,
        0xea6, 0xea6, 0xebe, 0xebf, 0xec5, 0xec5, 0xec7, 0xec7, 0xecf, 0xecf, 0xeda, 0xedb, 0xee0, 0xeff, 0xf48, 0xf48,
        0xf6d, 0xf70, 0xf98, 0xf98, 0xfbd, 0xfbd, 0xfcd, 0xfcd, 0xfdb, 0xfff, 0x10c6, 0x10c6, 0x10c8, 0x10cc, 0x10ce, 0x10cf,
        0x1249, 0x1249, 0x124e, 0x124f, 0x1257, 0x1257, 0x1259, 0x1259, 0x125e, 0x125f, 0x1289, 0x1289, 0x128e, 0x128f, 0x12b1, 0x12b1,
        0x12b6, 0x12b7, 0x12bf, 0x12bf, 0x12c1, 0x12c1, 0x12c6, 0x12c7, 0x12d7, 0x12d7, 0x1311, 0x1311, 0x1316, 0x1317, 0x135b, 0x135c,
        0x137d, 0x137f, 0x139a, 0x139f, 0x13f6, 0x13f7, 0x13fe, 0x13ff, 0x1680, 0x1680, 0x169d, 0x169f, 0x16f9, 0x16ff, 0x1716, 0x171e,
        0x1737, 0x173f, 0x1754, 0x175f, 0x176d, 0x176d, 0x1771, 0x1771, 0x1774, 0x177f, 0x17de, 0x17df, 0x17ea, 0x17ef, 0x17fa, 0x17ff,
        0x180e, 0x180e, 0x181a, 0x181f, 0x1879, 0x187f, 0x18ab, 0x18af, 0x18f6, 0x18ff, 0x191f, 0x191f, 0x192c, 0x192f, 0x193c, 0x193f,
        0x1941, 0x1943, 0x196e, 0x196f, 0x1975, 0x197f, 0x19ac, 0x19af, 0x19ca, 0x19cf, 0x19db, 0x19dd, 0x1a1c, 0x1a1d, 0x1a5f, 0x1a5f,
        0x1a7d, 0x1a7e, 0x1a8a, 0x1a8f, 0x1a9a, 0x1a9f, 0x1aae, 0x1aaf, 0x1acf, 0x1aff, 0x1b4d, 0x1b4f, 0x1b7f, 0x1b7f, 0x1bf4, 0x1bfb,
        0x1c38, 0x1c3a, 0x1c4a, 0x1c4c, 0x1c89, 0x1c8f, 0x1cbb, 0x1cbc, 0x1cc8, 0x1ccf, 0x1cfb, 0x1cff, 0x1f16, 0x1f17, 0x1f1e, 0x1f1f,
        0x1f46, 0x1f47, 0x1f4e, 0x1f4f, 0x1f58, 0x1f58, 0x1f5a, 0x1f5a, 0x1f5c, 0x1f5c, 0x1f5e, 0x1f5e, 0x1f7e, 0x1f7f, 0x1fb5, 0x1fb5,
        0x1fc5, 0x1fc5, 0x1fd4, 0x1fd5, 0x1fdc, 0x1fdc, 0x1ff0, 0x1ff1, 0x1ff5, 0x1ff5, 0x1fff, 0x200f, 0x2028, 0x202f, 0x205f, 0x206f,
        0x2072, 0x2073, 0x208f, 0x208f, 0x209d, 0x209f, 0x20c1, 0x20cf, 0x20f1, 0x20ff, 0x218c, 0x218f, 0x2427, 0x243f, 0x244b, 0x245f,
        0x2b74, 0x2b75, 0x2b96, 0x2b96, 0x2cf4, 0x2cf8, 0x2d26, 0x2d26, 0x2d28, 0x2d2c, 0x2d2e, 0x2d2f, 0x2d68, 0x2d6e, 0x2d71, 0x2d7e,
        0x2d97, 0x2d9f, 0x2da7, 0x2da7, 0x2daf, 0x2daf, 0x2db7, 0x2db7, 0x2dbf, 0x2dbf, 0x2dc7, 0x2dc7, 0x2dcf, 0x2dcf, 0x2dd7, 0x2dd7,
        0x2ddf, 0x2ddf, 0x2e5e, 0x2e7f, 0x2e9a, 0x2e9a, 0x2ef4, 0x2eff, 0x2fd6, 0x2fef, 0x3000, 0x3000, 0x3040, 0x3040, 0x3097, 0x3098,
        0x3100, 0x3104, 0x3130, 0x3130, 0x318f, 0x318f, 0x31e4, 0x31ee, 0x321f, 0x321f, 0xa48d, 0xa48f, 0xa4c7, 0xa4cf, 0xa62c, 0xa63f,
        0xa6f8, 0xa6ff, 0xa7cb, 0xa7cf, 0xa7d2, 0xa7d2, 0xa7d4, 0xa7d4, 0xa7da, 0xa7f1, 0xa82d, 0xa82f, 0xa83a, 0xa83f, 0xa878, 0xa87f,
        0xa8c6, 0xa8cd, 0xa8da, 0xa8df, 0xa954, 0xa95e, 0xa97d, 0xa97f, 0xa9ce, 0xa9ce, 0xa9da, 0xa9dd, 0xa9ff, 0xa9ff, 0xaa37, 0xaa3f,
        0xaa4e, 0xaa4f, 0xaa5a, 0xaa5b, 0xaac3, 0xaada, 0xaaf7, 0xab00, 0xab07, 0xab08, 0xab0f, 0xab10, 0xab17, 0xab1f, 0xab27, 0xab27,
        0xab2f, 0xab2f, 0xab6c, 0xab6f, 0xabee, 0xabef, 0xabfa, 0xabff, 0xd7a4, 0xd7af, 0xd7c7, 0xd7ca, 0xd7fc, 0xf8ff, 0xfa6e, 0xfa6f,
        0xfada, 0xfaff, 0xfb07, 0xfb12, 0xfb18, 0xfb1c, 0xfb37, 0xfb37, 0xfb3d, 0xfb3d, 0xfb3f, 0xfb3f, 0xfb42, 0xfb42, 0xfb45, 0xfb45,
        0xfbc3, 0xfbd2, 0xfd90, 0xfd91, 0xfdc8, 0xfdce, 0xfdd0, 0xfdef, 0xfe1a, 0xfe1f, 0xfe53, 0xfe53, 0xfe67, 0xfe67, 0xfe6c, 0xfe6f,
        0xfe75, 0xfe75, 0xfefd, 0xff00, 0xffbf, 0xffc1, 0xffc8, 0xffc9, 0xffd0, 0xffd1, 0xffd8, 0xffd9, 0xffdd, 0xffdf, 0xffe7, 0xffe7,
        0xffef, 0xfffb, 0xfffe, 0xffff, 0x1000c, 0x1000c, 0x10027, 0x10027, 0x1003b, 0x1003b, 0x1003e, 0x1003e, 0x1004e, 0x1004f, 0x1005e, 0x1007f,
        0x100fb, 0x100ff, 0x10103, 0x10106, 0x10134, 0x10136, 0x1018f, 0x1018f, 0x1019d, 0x1019f, 0x101a1, 0x101cf, 0x101fe, 0x1027f, 0x1029d, 0x1029f,
        0x102d1, 0x102df, 0x102fc, 0x102ff, 0x10324, 0x1032c, 0x1034b, 0x1034f, 0x1037b, 0x1037f, 0x1039e, 0x1039e, 0x103c4, 0x103c7, 0x103d6, 0x103ff,
        0x1049e, 0x1049f, 0x104aa, 0x104af, 0x104d4, 0x104d7, 0x104fc, 0x104ff, 0x10528, 0x1052f, 0x10564, 0x1056e, 0x1057b, 0x1057b, 0x1058b, 0x1058b,
        0x10593, 0x10593, 0x10596, 0x10596, 0x105a2, 0x105a2, 0x105b2, 0x105b2, 0x105ba, 0x105ba, 0x105bd, 0x105ff, 0x10737, 0x1073f, 0x10756, 0x1075f,
        0x10768, 0x1077f, 0x10786, 0x10786, 0x107b1, 0x107b1, 0x107bb, 0x107ff, 0x10806, 0x10807, 0x10809, 0x10809, 0x10836, 0x10836, 0x10839, 0x1083b,
        0x1083d, 0x1083e, 0x10856, 0x10856, 0x1089f, 0x108a6, 0x108b0, 0x108df, 0x108f3, 0x108f3, 0x108f6, 0x108fa, 0x1091c, 0x1091e, 0x1093a, 0x1093e,
        0x10940, 0x1097f, 0x109b8, 0x109bb, 0x109d0, 0x109d1, 0x10a04, 0x10a04, 0x10a07, 0x10a0b, 0x10a14, 0x10a14, 0x10a18, 0x10a18, 0x10a36, 0x10a37,
        0x10a3b, 0x10a3e, 0x10a49, 0x10a4f, 0x10a59, 0x10a5f, 0x10aa0, 0x10abf, 0x10ae7, 0x10aea, 0x10af7, 0x10aff, 0x10b36, 0x10b38, 0x10b56, 0x10b57,
        0x10b73, 0x10b77, 0x10b92, 0x10b98, 0x10b9d, 0x10ba8, 0x10bb0, 0x10bff, 0x10c49, 0x10c7f, 0x10cb3, 0x10cbf, 0x10cf3, 0x10cf9, 0x10d28, 0x10d2f,
        0x10d3a, 0x10e5f, 0x10e7f, 0x10e7f, 0x10eaa, 0x10eaa, 0x10eae, 0x10eaf, 0x10eb2, 0x10efc, 0x10f28, 0x10f2f, 0x10f5a, 0x10f6f, 0x10f8a, 0x10faf,
        0x10fcc, 0x10fdf, 0x10ff7, 0x10fff, 0x1104e, 0x11051, 0x11076, 0x1107e, 0x110bd, 0x110bd, 0x110c3, 0x110cf, 0x110e9, 0x110ef, 0x110fa, 0x110ff,
        0x11135, 0x11135, 0x11148, 0x1114f, 0x11177, 0x1117f, 0x111e0, 0x111e0, 0x111f5, 0x111ff, 0x11212, 0x11212, 0x11242, 0x1127f, 0x11287, 0x11287,
        0x11289, 0x11289, 0x1128e, 0x1128e, 0x1129e, 0x1129e, 0x112aa, 0x112af, 0x112eb, 0x112ef, 0x112fa, 0x112ff, 0x11304, 0x11304, 0x1130d, 0x1130e,
        0x11311, 0x11312, 0x11329, 0x11329, 0x11331, 0x11331, 0x11334, 0x11334, 0x1133a, 0x1133a, 0x11345, 0x11346, 0x11349, 0x1134a, 0x1134e, 0x1134f,
        0x11351, 0x11356, 0x11358, 0x1135c, 0x11364, 0x11365, 0x1136d, 0x1136f, 0x11375, 0x113ff, 0x1145c, 0x1145c, 0x11462, 0x1147f, 0x114c8, 0x114cf,
        0x114da, 0x1157f, 0x115b6, 0x115b7, 0x115de, 0x115ff, 0x11645, 0x1164f, 0x1165a, 0x1165f, 0x1166d, 0x1167f, 0x116ba, 0x116bf, 0x116ca, 0x116ff,
        0x1171b, 0x1171c, 0x1172c, 0x1172f, 0x11747, 0x117ff, 0x1183c, 0x1189f, 0x118f3, 0x118fe, 0x11907, 0x11908, 0x1190a, 0x1190b, 0x11914, 0x11914,
        0x11917, 0x11917, 0x11936, 0x11936, 0x11939, 0x1193a, 0x11947, 0x1194f, 0x1195a, 0x1199f, 0x119a8, 0x119a9, 0x119d8, 0x119d9, 0x119e5, 0x119ff,
        0x11a48, 0x11a4f, 0x11aa3, 0x11aaf, 0x11af9, 0x11aff, 0x11b0a, 0x11bff, 0x11c09, 0x11c09, 0x11c37, 0x11c37, 0x11c46, 0x11c4f, 0x11c6d, 0x11c6f,
        0x11c90, 0x11c91, 0x11ca8, 0x11ca8, 0x11cb7, 0x11cff, 0x11d07, 0x11d07, 0x11d0a, 0x11d0a, 0x11d37, 0x11d39, 0x11d3b, 0x11d3b, 0x11d3e, 0x11d3e,
        0x11d48, 0x11d4f, 0x11d5a, 0x11d5f, 0x11d66, 0x11d66, 0x11d69, 0x11d69, 0x11d8f, 0x11d8f, 0x11d92, 0x11d92, 0x11d99, 0x11d9f, 0x11daa, 0x11edf,
        0x11ef9, 0x11eff, 0x11f11, 0x11f11, 0x11f3b, 0x11f3d, 0x11f5a, 0x11faf, 0x11fb1, 0x11fbf, 0x11ff2, 0x11ffe, 0x1239a, 0x123ff, 0x1246f, 0x1246f,
        0x12475, 0x1247f, 0x12544, 0x12f8f, 0x12ff3, 0x12fff, 0x13430, 0x1343f, 0x13456, 0x143ff, 0x14647, 0x167ff, 0x16a39, 0x16a3f, 0x16a5f, 0x16a5f,
        0x16a6a, 0x16a6d, 0x16abf, 0x16abf, 0x16aca, 0x16acf, 0x16aee, 0x16aef, 0x16af6, 0x16aff, 0x16b46, 0x16b4f, 0x16b5a, 0x16b5a, 0x16b62, 0x16b62,
        0x16b78, 0x16b7c, 0x16b90, 0x16e3f, 0x16e9b, 0x16eff, 0x16f4b, 0x16f4e, 0x16f88, 0x16f8e, 0x16fa0, 0x16fdf, 0x16fe5, 0x16fef, 0x16ff2, 0x16fff,
        0x187f8, 0x187ff, 0x18cd6, 0x18cff, 0x18d09, 0x1afef, 0x1aff4, 0x1aff4, 0x1affc, 0x1affc, 0x1afff, 0x1afff, 0x1b123, 0x1b131, 0x1b133, 0x1b14f,
        0x1b153, 0x1b154, 0x1b156, 0x1b163, 0x1b168, 0x1b16f, 0x1b2fc, 0x1bbff, 0x1bc6b, 0x1bc6f, 0x1bc7d, 0x1bc7f, 0x1bc89, 0x1bc8f, 0x1bc9a, 0x1bc9b,
        0x1bca0, 0x1ceff, 0x1cf2e, 0x1cf2f, 0x1cf47, 0x1cf4f, 0x1cfc4, 0x1cfff, 0x1d0f6, 0x1d0ff, 0x1d127, 0x1d128, 0x1d173, 0x1d17a, 0x1d1eb, 0x1d1ff,
        0x1d246, 0x1d2bf, 0x1d2d4, 0x1d2df, 0x1d2f4, 0x1d2ff, 0x1d357, 0x1d35f, 0x1d379, 0x1d3ff, 0x1d455, 0x1d455, 0x1d49d, 0x1d49d, 0x1d4a0, 0x1d4a1,
        0x1d4a3, 0x1d4a4, 0x1d4a7, 0x1d4a8, 0x1d4ad, 0x1d4ad, 0x1d4ba, 0x1d4ba, 0x1d4bc, 0x1d4bc, 0x1d4c4, 0x1d4c4, 0x1d506, 0x1d506, 0x1d50b, 0x1d50c,
        0x1d515, 0x1d515, 0x1d51d, 0x1d51d, 0x1d53a, 0x1d53a, 0x1d53f, 0x1d53f, 0x1d545, 0x1d545, 0x1d547, 0x1d549, 0x1d551, 0x1d551, 0x1d6a6, 0x1d6a7,
        0x1d7cc, 0x1d7cd, 0x1da8c, 0x1da9a, 0x1daa0, 0x1daa0, 0x1dab0, 0x1deff, 0x1df1f, 0x1df24, 0x1df2b, 0x1dfff, 0x1e007, 0x1e007, 0x1e019, 0x1e01a,
        0x1e022, 0x1e022, 0x1e025, 0x1e025, 0x1e02b, 0x1e02f, 0x1e06e, 0x1e08e, 0x1e090, 0x1e0ff, 0x1e12d, 0x1e12f, 0x1e13e, 0x1e13f, 0x1e14a, 0x1e14d,
        0x1e150, 0x1e28f, 0x1e2af, 0x1e2bf, 0x1e2fa, 0x1e2fe, 0x1e300, 0x1e4cf, 0x1e4fa, 0x1e7df, 0x1e7e7, 0x1e7e7, 0x1e7ec, 0x1e7ec, 0x1e7ef, 0x1e7ef,
        0x1e7ff, 0x1e7ff, 0x1e8c5, 0x1e8c6, 0x1e8d7, 0x1e8ff, 0x1e94c, 0x1e94f, 0x1e95a, 0x1e95d, 0x1e960, 0x1ec70, 0x1ecb5, 0x1ed00, 0x1ed3e, 0x1edff,
        0x1ee04, 0x1ee04, 0x1ee20, 0x1ee20, 0x1ee23, 0x1ee23, 0x1ee25, 0x1ee26, 0x1ee28, 0x1ee28, 0x1ee33, 0x1ee33, 0x1ee38, 0x1ee38, 0x1ee3a, 0x1ee3a,
        0x1ee3c, 0x1ee41, 0x1ee43, 0x1ee46, 0x1ee48, 0x1ee48, 0x1ee4a, 0x1ee4a, 0x1ee4c, 0x1ee4c, 0x1ee50, 0x1ee50, 0x1ee53, 0x1ee53, 0x1ee55, 0x1ee56,
        0x1ee58, 0x1ee58, 0x1ee5a, 0x1ee5a, 0x1ee5c, 0x1ee5c, 0x1ee5e, 0x1ee5e, 0x1ee60, 0x1ee60, 0x1ee63, 0x1ee63, 0x1ee65, 0x1ee66, 0x1ee6b, 0x1ee6b,
        0x1ee73, 0x1ee73, 0x1ee78, 0x1ee78, 0x1ee7d, 0x1ee7d, 0x1ee7f, 0x1ee7f, 0x1ee8a, 0x1ee8a, 0x1ee9c, 0x1eea0, 0x1eea4, 0x1eea4, 0x1eeaa, 0x1eeaa,
        0x1eebc, 0x1eeef, 0x1eef2, 0x1efff, 0x1f02c, 0x1f02f, 0x1f094, 0x1f09f, 0x1f0af, 0x1f0b0, 0x1f0c0, 0x1f0c0, 0x1f0d0, 0x1f0d0, 0x1f0f6, 0x1f0ff,
        0x1f1ae, 0x1f1e5, 0x1f203, 0x1f20f, 0x1f23c, 0x1f23f, 0x1f249, 0x1f24f, 0x1f252, 0x1f25f, 0x1f266, 0x1f2ff, 0x1f6d8, 0x1f6db, 0x1f6ed, 0x1f6ef,
        0x1f6fd, 0x1f6ff, 0x1f777, 0x1f77a, 0x1f7da, 0x1f7df, 0x1f7ec, 0x1f7ef, 0x1f7f1, 0x1f7ff, 0x1f80c, 0x1f80f, 0x1f848, 0x1f84f, 0x1f85a, 0x1f85f,
        0x1f888, 0x1f88f, 0x1f8ae, 0x1f8af, 0x1f8b2, 0x1f8ff, 0x1fa54, 0x1fa5f, 0x1fa6e, 0x1fa6f, 0x1fa7d, 0x1fa7f, 0x1fa89, 0x1fa8f, 0x1fabe, 0x1fabe,
        0x1fac6, 0x1facd, 0x1fadc, 0x1fadf, 0x1fae9, 0x1faef, 0x1faf9, 0x1faff, 0x1fb93, 0x1fb93, 0x1fbcb, 0x1fbef, 0x1fbfa, 0x1ffff, 0x2a6e0, 0x2a6ff,
        0x2b73a, 0x2b73f, 0x2b81e, 0x2b81f, 0x2cea2, 0x2ceaf, 0x2ebe1, 0x2ebef, 0x2ee5e, 0x2f7ff, 0x2fa1e, 0x2ffff, 0x3134b, 0x3134f, 0x323b0, 0xe00ff,
        0xe01f0, 0x10ffff,
    };

    static bool PyPrintable(int codepoint)
    {
        int lo = 0, hi = PyNonPrintable.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (codepoint < PyNonPrintable[2 * mid]) hi = mid - 1;
            else if (codepoint > PyNonPrintable[2 * mid + 1]) lo = mid + 1;
            else return false;
        }
        return true;
    }

    /// <summary>Python repr(str): quote choice, ASCII escapes, and Unicode printability from the pinned Blender runtime.</summary>
    static string PyStrRepr(string t)
    {
        char q = t.Contains('\'') && !t.Contains('"') ? '"' : '\'';
        var b = new System.Text.StringBuilder(); b.Append(q);
        for (int i = 0; i < t.Length; i++)
        {
            char ch = t[i];
            if (ch == '\\') b.Append("\\\\");
            else if (ch == q) b.Append('\\').Append(ch);
            else if (ch == '\t') b.Append("\\t");
            else if (ch == '\n') b.Append("\\n");
            else if (ch == '\r') b.Append("\\r");
            else
            {
                int cp = ch, width = 1;
                if (char.IsHighSurrogate(ch) && i + 1 < t.Length && char.IsLowSurrogate(t[i + 1]))
                { cp = char.ConvertToUtf32(ch, t[i + 1]); width = 2; }
                if (!PyPrintable(cp)) b.Append(cp <= 0xff ? "\\x" : cp <= 0xffff ? "\\u" : "\\U")
                    .Append(cp.ToString(cp <= 0xff ? "x2" : cp <= 0xffff ? "x4" : "x8", CultureInfo.InvariantCulture));
                else b.Append(t, i, width);
                i += width - 1;
            }
        }
        return b.Append(q).ToString();
    }
}
