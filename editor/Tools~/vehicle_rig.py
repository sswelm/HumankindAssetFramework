# vehicle_rig.py — "VEHICLEIZE" a STATIC vehicle model (2026-07-25): create the rigged, Spin-animated GLB that the
# animated bake path consumes (the hand-made Ehrhardt_Spin.glb recipe, automated — see Animated-Models.md).
#
# Two modes (argv after "--"):
#   probe <input>
#       Lists the model's mesh parts for the Vehicle Lab UI:  PART|name|verts|cx,cy,cz|sx,sy,sz
#   rig <input> <outGlb> <previewFbx> <wheelParts ;-sep> <turretParts ;-sep> <axis X|Y|Z|AUTO> <frames> <degrees>
#       Builds: armature (Root at origin; one bone per wheel part at its bbox center, tail along the AXLE axis so
#       spinning = local-Y rotation; a Turret bone per turret part), rigid full-weight skinning (wheel verts -> their
#       bone, turret verts -> Turret, everything else -> Root), and a LINEAR "Spin" action (frame 0 = rest identity,
#       frame N = <degrees> about each wheel's axle). Exports the GLB (+ a preview FBX for the Unity-side turntable).
#       AXLE AUTO = the axis of each wheel's SMALLEST bbox extent (a wheel is thin along its axle) — per wheel, so
#       mirrored side wheels resolve independently.
# Frame 0 deliberately equals the rest pose: `Spin[0..0]` is the motionless Idle (see Factory-Manual / Law 2 notes).
import bpy, sys, math, time
_T0 = time.time()
def _lap(label):
    global _T0
    now = time.time(); print("VEHICLE timing: %-12s %6.1fs" % (label, now - _T0)); _T0 = now
from mathutils import Vector, Quaternion, Matrix

argv = sys.argv[sys.argv.index("--") + 1:]
mode = argv[0]
inp = argv[1]

def imp(path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    ext = path.lower().rsplit(".", 1)[-1]
    if ext in ("glb", "gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif ext == "fbx":
        bpy.ops.import_scene.fbx(filepath=path)
    elif ext == "obj":
        (bpy.ops.wm.obj_import if hasattr(bpy.ops.wm, "obj_import") else bpy.ops.import_scene.obj)(filepath=path)
    elif ext == "blend":
        bpy.ops.wm.open_mainfile(filepath=path)
    else:
        print("VEHICLE ERROR: unsupported extension .%s" % ext); sys.exit(1)
    # purge the Blender glTF importer's bone-shape placeholder (unskinned "Icosphere" mesh, not in the file itself)
    for _ico in [o for o in bpy.data.objects if o.type == 'MESH' and o.name.startswith('Icosphere') and not o.vertex_groups]:
        print("VEHICLE purged glTF importer bone-shape artifact: %s" % _ico.name)
        bpy.data.objects.remove(_ico, do_unlink=True)

def mesh_objects():
    return [o for o in bpy.context.scene.objects if o.type == 'MESH' and len(o.data.vertices) > 0]

def world_bbox(o):
    pts = [o.matrix_world @ Vector(c) for c in o.bound_box]
    mn = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    mx = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return (mn + mx) / 2.0, mx - mn

imp(inp)
_lap("import")

# ---- rigged-source detection (the SKM fast path's foundation) ----
# A game-rip often ships FULLY skinned (SKM_ prefix): its artist skeleton has perfect axle pivots and extra
# weapon bones. Report each DEFORM bone with its weighted-vert count + bbox so the caller can offer bone-level
# marking instead of the shard flow. Computed on the ORIGINAL meshes, before any loose-split.
def rig_report():
    arms = [o for o in bpy.context.scene.objects if o.type == 'ARMATURE']
    if not arms:
        return
    arm0 = arms[0]
    bone_names = {b.name for b in arm0.data.bones}
    stats = {}   # bone -> [count, min Vector, max Vector]
    total = weighted = 0
    for o in mesh_objects():
        gidx = {g.index: g.name for g in o.vertex_groups if g.name in bone_names}
        if not gidx:
            continue
        mw = o.matrix_world
        for v in o.data.vertices:
            total += 1
            best = None
            for g in v.groups:
                if g.group in gidx and g.weight > 0.5:
                    best = gidx[g.group]; break
            if best is None:
                continue
            weighted += 1
            p = mw @ v.co
            st = stats.get(best)
            if st is None:
                stats[best] = [1, p.copy(), p.copy()]
            else:
                st[0] += 1
                st[1].x = min(st[1].x, p.x); st[1].y = min(st[1].y, p.y); st[1].z = min(st[1].z, p.z)
                st[2].x = max(st[2].x, p.x); st[2].y = max(st[2].y, p.y); st[2].z = max(st[2].z, p.z)
    if not stats or total == 0 or weighted < total * 0.9:
        return   # partially/un-skinned: not fast-path material
    print("VEHICLE rigged source: armature '%s', %d bones carry weights, %d/%d verts weighted" % (arm0.name, len(stats), weighted, total))
    for bn, (cnt, mn, mx) in stats.items():
        c = (mn + mx) / 2.0; s = mx - mn
        print("RIGBONE|%s|%d|%.4f,%.4f,%.4f|%.4f,%.4f,%.4f" % (bn, cnt, c.x, c.y, c.z, s.x, s.y, s.z))

if mode == "probe":
    rig_report()
    _lap("rig_report")
    objs = mesh_objects()
    if len(objs) == 1:
        # a single combined mesh can't be role-assigned — try splitting into loose parts for the caller
        bpy.context.view_layer.objects.active = objs[0]
        objs[0].select_set(True)
        bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
        bpy.ops.mesh.separate(type='LOOSE'); bpy.ops.object.mode_set(mode='OBJECT')
        objs = mesh_objects()
        print("VEHICLE note: single mesh split into %d loose parts (names are synthetic)" % len(objs))
    _lap("split")
    # ---- visibility classification: EXTERNAL vs INTERIOR ----
    # A part is EXTERNAL if any sampled surface point can shoot a straight "escape ray" to infinity without hitting
    # other geometry; a part blocked from every sample in every direction is INTERIOR (cockpit gear, engine guts) —
    # provably never visible, safe to strip for triangle budget. Directions: the vertex normal first (the likeliest
    # escape), then 14 fixed dirs (axes + cube diagonals). Up to 30 samples/part, early-out on the first escape.
    # PERF (2026-08-20): the rays go through ONE combined BVHTree of every part's world-space polygons, built once.
    # scene.ray_cast per ray walked all 3,350 objects each time — 31 s on the Ehrhardt; the BVH does it in 0.2 s
    # with the same verdicts (±3 parts of 3,350 at the eps boundary).
    from mathutils.bvhtree import BVHTree
    _dirs = [Vector(_v).normalized() for _v in ((1,0,0),(-1,0,0),(0,1,0),(0,-1,0),(0,0,1),(0,0,-1),
             (1,1,1),(1,1,-1),(1,-1,1),(1,-1,-1),(-1,1,1),(-1,1,-1),(-1,-1,1),(-1,-1,-1))]
    _bv = []; _bp = []; _base = 0
    _mx_ext = 0.0
    for _o in objs:
        _mw = _o.matrix_world
        _bv.extend([_mw @ _v.co for _v in _o.data.vertices])
        _bp.extend([tuple(_base + _i for _i in _p.vertices) for _p in _o.data.polygons])
        _base += len(_o.data.vertices)
        _c2, _s2 = world_bbox(_o)
        _mx_ext = max(_mx_ext, _s2.x, _s2.y, _s2.z)
    _bvh = BVHTree.FromPolygons(_bv, _bp)
    _eps = max(1e-4, _mx_ext * 1e-3)   # ray start offset so a point clears its own surface
    _vis = {}
    for _o in objs:
        _vs = _o.data.vertices
        _stp = max(1, len(_vs) // 30)
        _nm = _o.matrix_world.to_3x3()
        _seen = False
        for _i in range(0, len(_vs), _stp):
            _p = _o.matrix_world @ _vs[_i].co
            for _d in [(_nm @ _vs[_i].normal).normalized()] + _dirs:
                if _d.length < 0.5:
                    continue
                if _bvh.ray_cast(_p + _d * _eps, _d)[0] is None:
                    _seen = True; break
            if _seen:
                break
        _vis[_o.name] = 1 if _seen else 0
    print("VEHICLE visibility: %d external / %d interior part(s)" % (sum(_vis.values()), len(objs) - sum(_vis.values())))
    _lap("visibility")
    # ---- dominant bone per shard (rigged sources) — so the Lab can highlight a BONE row's shards WITHOUT the
    # preview carrying skin weights (the skinned preview export was the 84 s hog; see below). ----
    _bone_names = set()
    for _a in [o for o in bpy.context.scene.objects if o.type == 'ARMATURE']:
        _bone_names.update(b.name for b in _a.data.bones)
    _bone = {}
    for _o in objs:
        _g = {g.index: g.name for g in _o.vertex_groups if g.name in _bone_names}
        if not _g:
            continue
        _tally = {}
        for _v in _o.data.vertices:
            for _vg in _v.groups:
                if _vg.group in _g and _vg.weight > 0.0:
                    _tally[_g[_vg.group]] = _tally.get(_g[_vg.group], 0.0) + _vg.weight
        if _tally:
            _bone[_o.name] = max(_tally.items(), key=lambda kv: kv[1])[0]
    for o in objs:
        c, s = world_bbox(o)
        print("PART|%s|%d|%.4f,%.4f,%.4f|%.4f,%.4f,%.4f|%d|%s" % (o.name, len(o.data.vertices), c.x, c.y, c.z, s.x, s.y, s.z, _vis.get(o.name, 1), _bone.get(o.name, "")))
    print("VEHICLE parts listed"); sys.stdout.flush()   # sentinel + flush: Blender's C-level banner flushes AFTER Python's buffer and would otherwise glue onto the last PART line
    # optional argv[2]: export the SPLIT scene as a preview FBX so the Lab can show/zoom/highlight each part by name.
    # PERF (2026-08-20): exported UNSKINNED — plain meshes, world transforms baked, no armature. The FBX exporter
    # took 84 s to write 3,350 skinned objects (58 MB) and 1.9 s to write them as plain meshes (11 MB). The preview
    # only needs names + geometry; bone membership travels in the PART line above.
    if len(argv) > 2 and argv[2].strip():
        for _o in objs:
            _mw = _o.matrix_world.copy()
            _o.vertex_groups.clear()
            for _m in list(_o.modifiers):
                _o.modifiers.remove(_m)
            _o.parent = None
            _o.matrix_world = _mw
        for _a in [o for o in bpy.context.scene.objects if o.type == 'ARMATURE']:
            bpy.data.objects.remove(_a, do_unlink=True)
        bpy.ops.export_scene.fbx(filepath=argv[2], object_types={'MESH'}, use_mesh_modifiers=False, add_leaf_bones=False, bake_anim=False)
        print("VEHICLE probe preview: %s" % argv[2])
        _lap("export")
    sys.exit(0)

# ---- rig mode ----
# The whole rig path runs inside a guard: Blender EXITS 0 even when a python script crashes (the documented baker
# trap), so an unhandled traceback must become a loud VEHICLE ERROR line the Lab can detect — the Lab additionally
# requires the final "VEHICLE RIG DONE" marker before believing anything.
import traceback as _tb
def _guard(fn):
    try:
        fn()
    except SystemExit:
        raise
    except Exception:
        print("VEHICLE ERROR: rig step crashed:")
        _tb.print_exc()
        sys.exit(1)

out_glb, preview_fbx = argv[2], argv[3]
def namelist(arg):
    # "@<path>" = read names from a file (one per line): a thorough marking session is hundreds of shards,
    # far past the ~32k Windows command-line limit.
    if arg.startswith("@"):
        with open(arg[1:], "r", encoding="utf-8") as f:
            return [l.strip() for l in f if l.strip()]
    return [n for n in arg.split(";") if n.strip()]
wheel_names = namelist(argv[4])
turret_names = namelist(argv[5])
ignore_names = set(namelist(argv[9])) if len(argv) > 9 and argv[9].strip() else set()   # parts to DELETE (unused option meshes etc.)
track_names = namelist(argv[10]) if len(argv) > 10 and argv[10].strip() else []          # tread loops: static, but each on its OWN bone
gun_names = namelist(argv[11]) if len(argv) > 11 and argv[11].strip() else []            # gun assembly: ONE Gun bone (muzzle/socket anchor), rides the Turret if present
# ROTOR / TAIL ROTOR (helicopters): unlike wheels, every part of one rotor fuses into ONE hub bone at the group's
# CENTROID (the mast) regardless of how far the blades spread — a rotor spins as a single disc, not per-blade.
rotor_names = namelist(argv[26]) if len(argv) > 26 and argv[26].strip() else []
tailrotor_names = namelist(argv[27]) if len(argv) > 27 and argv[27].strip() else []
tail_axis_arg = argv[28].upper() if len(argv) > 28 and argv[28].strip() else "AUTO"   # tail fan spins on a DIFFERENT axis than the main rotor; own override
# manual trim on the tail axle, degrees: yaw = swing about vertical, pitch = tilt up/down. Applied ON TOP of the
# auto/forced axle, so the user can dial the last few degrees by eye when the heuristic is close but not exact.
tail_yaw_adj = float(argv[29]) if len(argv) > 29 and argv[29].strip() else 0.0
tail_pitch_adj = float(argv[30]) if len(argv) > 30 and argv[30].strip() else 0.0
# TRAILS (argv[31..33], 2026-08-22): a split-trail gun's arms. Each marked part gets a bone HINGED at its body
# end, and a separate "Deploy" action swings them open about the vertical, mirrored per side — so the state
# machine can hold Deploy[N..N] as the deployed stance, play Deploy to unfold and Deploy[N..0] to fold, while
# `Spin` keeps the wheels rolling with the arms at their folded rest. ("Trail" is the artillery term: these are
# the arms of a SPLIT-TRAIL carriage, each ending in a spade. Not "legs" — that name is reserved for mech limbs.)
trail_names = namelist(argv[31]) if len(argv) > 31 and argv[31].strip() else []
trail_spread = float(argv[32]) if len(argv) > 32 and argv[32].strip() else 35.0
trail_frames = max(2, int(float(argv[33]))) if len(argv) > 33 and argv[33].strip() else 12
# GUN PIVOT (argv[34], 2026-08-22): where along the gun assembly its elevation bone sits — 0 = breech, 1 = muzzle,
# 0.5 = the bbox centre (the historical placement, and the default so existing rigs regenerate unchanged).
gun_pivot = min(1.0, max(0.0, float(argv[34]))) if len(argv) > 34 and argv[34].strip() else 0.5
# GUN DEPLOY ELEVATION (argv[35]): degrees the gun raises across the Deploy clip, on the same frames as the trail
# spread — a towed gun travels clamped level and only elevates once the trails are planted. 0 = leave it level.
gun_deploy_elev = float(argv[35]) if len(argv) > 35 and argv[35].strip() else 0.0
gun_axis = None                  # (trunnion, muzzle tip) in world space, filled when the Gun bone is built
# MUZZLE parts (argv[36]): a separately-modelled muzzle brake / flash hider. NO bone of its own — a brake is bolted
# to the tube, so it must elevate and recoil with it; these weld to the Gun bone exactly as the gun parts do. What
# marking them buys is a PRECISE muzzle tip, which pins the breech→muzzle span `gun_pivot` measures against and lets
# the rig report the exact fire origin instead of the gun bbox's far extreme.
muzzle_names = namelist(argv[36]) if len(argv) > 36 and argv[36].strip() else []
# CRADLE parts (argv[37]): the frame that HOLDS the tube — trunnions, recoil cylinders, the trough the barrel slides
# in. It welds to the Gun bone like the tube does, because cradle and tube elevate together about the trunnions; what
# it must NOT do is count toward the breech→muzzle span (a cradle stops well short of the muzzle) — and, once recoil
# exists, it is the part that STAYS while the barrel kicks back. That is the whole reason it is its own role.
cradle_names = namelist(argv[37]) if len(argv) > 37 and argv[37].strip() else []
# RECOIL (argv[38..39]): how far the tube kicks back, as a FRACTION OF ITS OWN LENGTH, and over how many frames.
# A fraction rather than absolute units so the dial means the same thing on any model at any scale. 0 = off, and
# off means the Barrel bone is never created — a gun that does not recoil costs no bone and regenerates unchanged.
recoil_dist = min(1.0, max(0.0, float(argv[38]))) if len(argv) > 38 and argv[38].strip() else 0.0
recoil_frames = max(3, int(float(argv[39]))) if len(argv) > 39 and argv[39].strip() else 16
# RECOIL LEAD-IN (argv[40]): frames of HELD deployed pose before the kick starts. The engine begins the attack clip
# when its own strike clock says so, and that clock is an estimate that can fire while the gun is still slewing —
# padding the front of the clip is the one part of the timing we control outright. 0 = kick immediately.
recoil_lead = max(0, int(float(argv[40]))) if len(argv) > 40 and argv[40].strip() else 0
recoil_bone = None               # set to "RecoilArm" when the split actually happens — the bone the clip ROTATES
recoil_geom = None               # (pivot, axis, bore_dir, slide, R) for the arc that fakes the slide
# Residual tilt the arc leaves on the tube. The slide is faked by swinging the barrel on a long arm, so some pitch
# is unavoidable; 3 deg is small enough to read as a straight slide and large enough to keep R finite (a pivot at
# infinity collapses the bone chain through float32 cancellation — the documented 1e9 sentinel bug).
RECOIL_PITCH_DEG = 3.0
deployed_pose = {}               # bone -> quaternion at the END of Deploy: the pose a firing gun must be holding
gun_bore = None                  # (breech, muzzle) — the full tube, the basis for the recoil fraction
axis_arg = argv[6].upper()
frames = max(2, int(argv[7]))
degrees = float(argv[8])
# tread advance in QUARTER-LINK cells per loop (user speed dial): 4 = one full link (syncs the sprocket),
# 3 = near-syncs the road wheels (their symmetry snap runs them slower), 2 = half speed (strong half-link
# restart grid). Clamped 1..8.
tread_adv_cells = max(1, min(8, int(float(argv[12])))) if len(argv) > 12 and argv[12].strip() else 3
# road-wheel/roller speed multiplier over the automatic belt-continuity speed (user eye-tuning dial).
# At exactly 1.0 speeds snap to each wheel's spoke-symmetry grid (invisible restarts); any other value is
# applied RAW so the dial always visibly responds (restart pops accepted while tuning).
road_speed_mul = max(0.1, min(4.0, float(argv[13]))) if len(argv) > 13 and argv[13].strip() else 1.0
# rear-idler speed multiplier over the user's Spin degrees (x1.0 = exactly the drive sprocket's speed)
idler_speed_mul = max(0.1, min(4.0, float(argv[14]))) if len(argv) > 14 and argv[14].strip() else 1.0
# tread cells PER LINK (the BONES dial): 4 = quarter-link (smoothest wraps, most bones), 2 = half-link,
# 1 = one bone per molded link, 0.5 = one bone per TWO links, 0.25 = one per FOUR (coarsest — for finely
# molded tracks like the Bradley's 0.186 pitch, where even one-per-link is 75 bones a side).
tread_cells_per_link = max(0.25, min(4.0, float(argv[15]))) if len(argv) > 15 and argv[15].strip() else 4.0
# 1 = rig the tread loops STATIC (rigid with the hull, no link bones, no conveyor — the isolation switch:
# wheels still spin, the tracks just don't run). Geometry stays visible.
tracks_static = len(argv) > 16 and argv[16].strip() == "1"
# WAVE ROCK (argv[17] amplitude in degrees, argv[18] cycle length in frames; canoe finding 2026-07-31): a slow
# idle sway for FLOATING units. Authored on a dedicated "Hull" bone (child of Root, carrying every part that
# would otherwise skin to Root) so the engine's root anchor stays identity. Rotation-only by construction —
# no keepTranslations needed, and it obeys the bind==frame0 contract (both endpoints are identity).
# Roll about the LONGITUDINAL axis (X, the rig's forward) at the cycle frequency; pitch about Y at DOUBLE the
# frequency and 40% of the amplitude — a figure-8 that reads as riding swells rather than a metronome.
rock_deg = max(0.0, min(45.0, float(argv[17]))) if len(argv) > 17 and argv[17].strip() else 0.0
rock_frames = max(0, int(float(argv[18]))) if len(argv) > 18 and argv[18].strip() else 0
if rock_deg > 0.0 and rock_frames <= 0:
    rock_frames = frames
rock_on = rock_deg > 0.0 and rock_frames >= 2
# Which axis is the hull's LENGTH (= the roll axis; pitch is the other horizontal one). "AUTO" picks the longer
# horizontal extent, which is a boat's length. Not inferable from the wheel Axle-axis knob: that describes a
# wheel's thinness, and a hull has no wheels. Vehicles built here run along X, but a glTF authored Y-up lands
# with its length on Blender's Y (the dug-out canoe) — hence the override.
rock_axis_arg = argv[19].strip().upper() if len(argv) > 19 and argv[19].strip() else "AUTO"
# Heading OFFSET from that base axis, in degrees, about the vertical — for a hull that isn't axis-aligned, or to
# angle the swell so the vessel takes it on the quarter rather than square on the beam. 0 = the base axis exactly.
rock_heading = float(argv[20]) if len(argv) > 20 and argv[20].strip() else 0.0
# The two swings are fully INDEPENDENT (2026-07-31, user request): each has its own amplitude in DEGREES and its
# own whole number of cycles per clip. Ratios/multipliers coupled them and made the result hard to predict — this
# is just two sine waves, stated plainly. Cycle counts are integers so the clip always loops without a pop.
rock_pitch_deg = max(0.0, min(45.0, float(argv[21]))) if len(argv) > 21 and argv[21].strip() else 2.4
rock_pitch_cycles = max(1, int(float(argv[22]))) if len(argv) > 22 and argv[22].strip() else 1
rock_roll_cycles = max(1, int(float(argv[25]))) if len(argv) > 25 and argv[25].strip() else 1
# re-decide now that BOTH amplitudes are known: a pitch-only rock (roll 0) is legitimate
if rock_pitch_deg > 0.0 and rock_frames <= 0:
    rock_frames = frames
rock_on = (rock_deg > 0.0 or rock_pitch_deg > 0.0) and rock_frames >= 2
# PHASE between roll and pitch, degrees. At EQUAL speed (freq 1) this is what keeps the motion two-dimensional:
# in phase (0) the two swings stay in lockstep and the hull just tilts along one fixed diagonal — visually a single
# axis again. At 90 the hull traces an ELLIPSE, both axes moving at the same rate, which is the natural buoy-like
# bob. Frame 0 is then a slightly heeled pose rather than dead level; the loop still closes exactly (t=0 and t=1
# evaluate identically) and the bake's rest-normalize adopts frame 0 as the rest, so the contract still holds.
rock_pitch_phase = float(argv[24]) if len(argv) > 24 and argv[24].strip() else 90.0
# MODEL ORIENTATION (argv[23] "rx,ry,rz" degrees): straighten a source that imports crooked, on its side or
# facing the wrong way. Applied to the DATA before anything measures the model, so every downstream inference
# reads the corrected pose — wheel axle axes, tread side/front detection, and the rock's auto hull-length axis.
# Distinct from the Factory's Rotation knob, which turns the finished bake; this makes the RIG itself straight.
model_rot = [0.0, 0.0, 0.0]
if len(argv) > 23 and argv[23].strip():
    try:
        _mr = [float(v) for v in argv[23].split(",")]
        model_rot = (_mr + [0.0, 0.0, 0.0])[:3]
    except ValueError:
        print("VEHICLE WARN: bad orientation arg '%s' — ignoring" % argv[23])
had_static_tracks = False   # remembers this IS a tracked vehicle even though the tread pipeline is skipped
if tracks_static and track_names:
    print("VEHICLE tracks STATIC (isolation): %d tread part(s) rigged rigid to Root, no link bones" % len(track_names))
    track_names = []
    had_static_tracks = True

# ---- rigfast mode: the SKM fast path ----
# The source already carries an artist skeleton with full weights (see rig_report) — REUSE it: author the Spin
# action directly on the named source bones and keep skinning/pivots/weapon bones untouched. Per bone, the spin
# axis is the LOCAL basis axis closest to the world axle direction, SIGNED so every wheel turns the same world
# way (artist rigs mirror left/right bones — an unsigned shared channel would counter-rotate one side).
if mode == "rigfast":
    def _fast():
        global objs
        arms = [o for o in bpy.context.scene.objects if o.type == 'ARMATURE']
        if not arms:
            print("VEHICLE ERROR: rigfast requested but the source has no armature"); sys.exit(1)
        arm = arms[0]
        objs = mesh_objects()
        ref = {"X": Vector((1, 0, 0)), "Y": Vector((0, 1, 0)), "Z": Vector((0, 0, 1))}.get(axis_arg, Vector((0, 1, 0)))
        arm.animation_data_create()
        act = bpy.data.actions.new("Spin")
        arm.animation_data.action = act
        try:
            if getattr(act, "slots", None):
                arm.animation_data.action_slot = act.slots[0]
        except Exception:
            pass
        bpy.context.scene.frame_start = 0
        bpy.context.scene.frame_end = frames
        spun = {}
        for bn in wheel_names:
            db, pb = arm.data.bones.get(bn), arm.pose.bones.get(bn)
            if db is None or pb is None:
                print("VEHICLE ERROR: spin bone '%s' not found. Bones: %s" % (bn, [b.name for b in arm.data.bones])); sys.exit(1)
            m3 = (arm.matrix_world @ db.matrix_local).to_3x3()
            best_i, best_d = 0, 0.0
            for i in range(3):
                v = Vector((0.0, 0.0, 0.0)); v[i] = 1.0
                d = (m3 @ v).normalized().dot(ref)
                if abs(d) > abs(best_d):
                    best_i, best_d = i, d
            sign = 1.0 if best_d >= 0 else -1.0
            pb.rotation_mode = 'XYZ'
            bpy.context.scene.frame_set(0)
            pb.rotation_euler = (0, 0, 0)
            pb.keyframe_insert("rotation_euler", frame=0)
            eul = [0.0, 0.0, 0.0]; eul[best_i] = math.radians(degrees) * sign
            bpy.context.scene.frame_set(frames)
            pb.rotation_euler = tuple(eul)
            pb.keyframe_insert("rotation_euler", frame=frames)
            spun[bn] = ("+" if sign > 0 else "-") + "XYZ"[best_i]
        try:
            fcs = list(act.fcurves)
        except AttributeError:
            fcs = [fc for layer in act.layers for strip in layer.strips for cb in strip.channelbags for fc in cb.fcurves]
        for fc in fcs:
            for kp in fc.keyframe_points:
                kp.interpolation = 'LINEAR'
        # strip helper objects (empties etc.); a kept child of a removed parent must keep its WORLD transform
        keep = set(objs); keep.add(arm)
        for o in keep:
            if o.parent is not None and o.parent not in keep:
                mw = o.matrix_world.copy(); o.parent = None; o.matrix_world = mw
        for o in list(bpy.data.objects):
            if o not in keep:
                bpy.data.objects.remove(o, do_unlink=True)
        bpy.ops.export_scene.gltf(filepath=out_glb, export_animations=True)
        if preview_fbx:
            bpy.ops.export_scene.fbx(filepath=preview_fbx, add_leaf_bones=False, bake_anim=True)
        print("VEHICLE RIG DONE: FAST PATH — %d source bone(s) spun %s on the artist skeleton (%d bones, weights untouched), Spin 0..%d %.0f deg -> %s"
              % (len(wheel_names), spun, len(arm.data.bones), frames, degrees, out_glb))
    _guard(_fast)
    sys.exit(0)

objs = mesh_objects()
if len(objs) == 1 and (wheel_names or turret_names):
    bpy.context.view_layer.objects.active = objs[0]
    objs[0].select_set(True)
    bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.separate(type='LOOSE'); bpy.ops.object.mode_set(mode='OBJECT')
    objs = mesh_objects()

# Ignore-marked parts are DELETED from the output — Sketchfab "options" models stack alternative versions of
# the same part (four skirt sets on the Jagdpanzer); rendering them all is z-fighting soup.
if ignore_names:
    _rem = [o for o in objs if o.name in ignore_names]
    objs = [o for o in objs if o.name not in ignore_names]   # filter BEFORE removing — removed objects are dead references
    for _o in _rem:
        bpy.data.objects.remove(_o, do_unlink=True)
    print("VEHICLE ignored: %d part(s) deleted from the output" % len(_rem))

# STRAIGHTEN (before anything measures the model): rotate every parent-less object in world space; the
# transform_apply below then bakes it into the vertex data, so the rig is built on the corrected pose.
if any(abs(v) > 1e-6 for v in model_rot):
    # ORDER: yaw Z first, THEN pitch Y, THEN roll X (Rx@Ry@Rz, world axes). The old Rz@Ry@Rx made pitch/roll
    # act about the model's carried pre-yaw axes — on a model that needs yaw to face the grid (the helicopter's
    # diagonal import), Roll X / Pitch Y then pulled DIAGONALLY. With yaw applied first, pitch and roll always
    # act about the world/screen horizontals on the model as the user currently sees it.
    _orient = (Matrix.Rotation(math.radians(model_rot[0]), 4, 'X') @
               Matrix.Rotation(math.radians(model_rot[1]), 4, 'Y') @
               Matrix.Rotation(math.radians(model_rot[2]), 4, 'Z'))
    for _oo in bpy.context.scene.objects:
        if _oo.parent is None:
            _oo.matrix_world = _orient @ _oo.matrix_world
    bpy.context.view_layer.update()
    print("VEHICLE orientation: straightened by (%.1f, %.1f, %.1f) deg before rigging" % tuple(model_rot))

# clean object transforms so bbox centers/axes are honest model-space.
# transform_apply REFUSES multi-user mesh data (instanced shards are common in game-rip FBX) — make every mesh
# single-user first, or the operator raises and (Blender exiting 0 regardless) the whole rig silently dies.
def _apply_transforms():
    for o in objs:
        if o.data.users > 1:
            o.data = o.data.copy()
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
_guard(_apply_transforms)

def find(name):
    for o in objs:
        if o.name == name:
            return o
    print("VEHICLE ERROR: part '%s' not found. Parts: %s" % (name, [o.name for o in objs])); sys.exit(1)

def axle_axis(s):
    if axis_arg in ("X", "Y", "Z"):
        return {"X": Vector((1, 0, 0)), "Y": Vector((0, 1, 0)), "Z": Vector((0, 0, 1))}[axis_arg]
    # AUTO: a wheel is THIN along its axle -> smallest extent
    return [Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))][min(range(3), key=lambda i: s[i])]

# ---- wheel clustering ----
# A wheel is usually MANY shards (tire, rim, spokes, bolts...). Bones must NOT be per-shard: a spoke spinning
# about its own bbox center pinwheels in place and the wheel shreds. Cluster the wheel parts by proximity —
# the BIGGEST part of each cluster (the tire) anchors the hub: its bbox center is the axle point, its
# thinnest extent the axle direction — and every member shard skins to that ONE bone, so spokes revolve
# around the hub like spokes. (Off-center shards are safe to mark Wheel because of this.)
wheel_info = []
for wn in wheel_names:
    c, s = world_bbox(find(wn))
    wheel_info.append((max(s), c, s, wn))
wheel_info.sort(key=lambda t: -t[0])   # biggest first, so anchors are tires, not bolts
clusters = []
for m, c, s, wn in wheel_info:
    home = None
    for cl in clusters:
        if (c - cl["c"]).length <= 0.75 * cl["m"]:   # within 3/4 of the anchor's diameter = same hub
            home = cl; break
    if home is None:
        clusters.append({"m": m, "c": c, "s": s, "names": [wn]})
    else:
        home["names"].append(wn)

# ROTOR / TAIL ROTOR: each group fuses to ONE hub bone at its COMBINED-bbox centre (the mast), NOT by proximity —
# a rotor's blades are far apart but must revolve as one disc. Axle = the group's thinnest combined extent, which is
# the vertical mast for a flat main rotor and the lateral axis for a tail-rotor disc (so AUTO axle is correct for both).
def _pca_min_axis(pts):
    # the smallest-variance direction of a point cloud = a flat disc's NORMAL (or a cylinder's axis). numpy required.
    import numpy as np
    P = np.array([[p[0], p[1], p[2]] for p in pts], dtype=float); P = P - P.mean(0)
    _, vec = np.linalg.eigh(np.cov(P.T))
    return Vector((float(vec[0, 0]), float(vec[1, 0]), float(vec[2, 0]))).normalized()
for grp, is_tail in ((rotor_names, False), (tailrotor_names, True)):
    if not grp:
        continue
    boxes = [world_bbox(find(n)) for n in grp]
    centers = [c for c, s in boxes]
    mean = sum(centers, Vector((0, 0, 0))) / len(centers)
    override = tail_axis_arg if is_tail else axis_arg
    if is_tail:
        # TAIL: PIVOT = the fan blades' centroid (the fan centre — user-confirmed correct). AXLE (user's hint): the fan
        # spins about the boom, ALIGNED WITH THE CENTRE OF THE HELICOPTER — so aim the axle from the fan centre straight
        # at the whole model's centre (the body). Robust and unambiguous; no noisy plane-fit on the boxy blades.
        hub_c = mean
        # The duct RING around the fan lies in the vertical plane running ALONG the boom; the spin axis is 90° to that
        # ring (user's correction — "toward the centre" pointed the axle ALONG the boom, parallel to the ring, exactly
        # 90° off). So: boom direction (fan centre -> body centre), flattened to horizontal, rotated 90° about vertical.
        model_c = sum((world_bbox(o)[0] for o in objs), Vector((0, 0, 0))) / max(1, len(objs))
        _d = model_c - mean
        _dxy = Vector((_d.x, _d.y, 0.0))
        auto_axle = Vector((0, 0, 1)).cross(_dxy).normalized() if _dxy.length > 1e-6 else Vector((0, 1, 0))
        axle_src = "90 deg to the boom ring (lateral)"
    else:
        # MAIN: the hub (Cylinder09) is a clean disc, so use ITS pole-to-pole axis: PIVOT = hub centre, AXLE = the hub
        # cylinder's own symmetry axis from its vertices (tilts with the mast if it's "Earth-tilted").
        hub_name = min(grp, key=lambda n: (world_bbox(find(n))[0] - mean).length)
        hub_obj = find(hub_name)
        hub_c = world_bbox(hub_obj)[0]
        try:
            auto_axle = _pca_min_axis([hub_obj.matrix_world @ v.co for v in hub_obj.data.vertices])
            axle_src = "hub pole-to-pole"
        except Exception:
            var = [sum((c[i] - mean[i]) ** 2 for c in centers) for i in range(3)]
            auto_axle = [Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))][min(range(3), key=lambda i: var[i])]
            axle_src = "least-spread (numpy missing)"
    if override in ("X", "Y", "Z"):
        axle = {"X": Vector((1, 0, 0)), "Y": Vector((0, 1, 0)), "Z": Vector((0, 0, 1))}[override]
        axle_src = "forced " + override
    else:
        axle = auto_axle
    # TAIL TRIM: user-dialled yaw (about vertical) then pitch (about the horizontal axis perpendicular to the axle),
    # rotating the axle itself. Lets the last few degrees be set by eye when the heuristic is close but not exact.
    if is_tail and (abs(tail_yaw_adj) > 1e-6 or abs(tail_pitch_adj) > 1e-6):
        axle = axle.copy()
        axle.rotate(Quaternion(Vector((0.0, 0.0, 1.0)), math.radians(tail_yaw_adj)))
        _pax = Vector((0.0, 0.0, 1.0)).cross(axle)
        if _pax.length > 1e-6:
            axle.rotate(Quaternion(_pax.normalized(), math.radians(tail_pitch_adj)))
        axle.normalize()
        axle_src += " + trim yaw %.1f pitch %.1f" % (tail_yaw_adj, tail_pitch_adj)
    mn = Vector(tuple(min(c[i] - s[i] / 2 for c, s in boxes) for i in range(3)))
    mx = Vector(tuple(max(c[i] + s[i] / 2 for c, s in boxes) for i in range(3)))
    gs = mx - mn
    clusters.append({"m": max(gs), "c": hub_c, "s": gs, "axle": axle, "names": list(grp), "is_rotor": True, "is_tail": is_tail})
    print("VEHICLE %s rotor: %d part(s), pivot (%.2f,%.2f,%.2f), axle (%.3f,%.3f,%.3f) [%s]"
          % ("tail" if is_tail else "main", len(grp), hub_c.x, hub_c.y, hub_c.z, axle.x, axle.y, axle.z, axle_src))
    print("VEHICLE rotor hub: %d part(s) -> bone at hub (%.2f,%.2f,%.2f), axle=%s (least-spread of centres)" % (len(grp), hub_c.x, hub_c.y, hub_c.z, tuple(axle)))

# armature: Root at origin + ONE bone per wheel cluster (tail along the axle => local Y IS the axle) + Turret
arm_data = bpy.data.armatures.new("VehicleRig")
arm = bpy.data.objects.new("VehicleRig", arm_data)
bpy.context.scene.collection.objects.link(arm)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='EDIT')
eb_root = arm_data.edit_bones.new("Root")
eb_root.head = (0, 0, 0); eb_root.tail = (0, 0.25, 0)
# WAVE ROCK: everything that would hang off Root hangs off Hull instead, and Hull carries the sway — so the
# whole vessel (body, wheels, turret, gun, tracks) rocks as one rigid unit while Root stays identity.
# Named to sort AFTER Root alphabetically (Amplitude needs parents-first ordering).
if rock_on:
    eb_hull = arm_data.edit_bones.new("RootHull")
    eb_hull.head = (0, 0, 0); eb_hull.tail = (0, 0.2, 0)
    eb_hull.parent = eb_root
    eb_body = eb_hull
    body_bone = "RootHull"
else:
    eb_body = eb_root
    body_bone = "Root"
bone_of = {}
wheel_axes = {}
cluster_bones = []
for i, cl in enumerate(clusters):
    ax = cl["axle"] if "axle" in cl else axle_axis(cl["s"])   # rotors carry a precomputed disc-normal axle; wheels infer it
    eb = arm_data.edit_bones.new("Wheel_%02d" % i)
    eb.head = cl["c"]
    if cl.get("is_rotor") and cl.get("is_tail"):
        # TAIL FAN (donor-clip contract, [DonorAxis]-measured 2026-08-04): the donor's tail channel spins about
        # LOCAL X, so orient this bone's frame with X = the fan's (canted) axle: bone direction (local Y) = the
        # in-disc direction closest to world-up, roll set so Z completes the right-handed frame (X = Y x Z = axle).
        # The plugin's rebase preserves LEAF rest orientations (ancestors flatten to identity), so this frame
        # survives into the game and CONJUGATES the donor's X-spin into the fan's real ring.
        _ax = ax.normalized()
        _yb = Vector((0, 0, 1)) - _ax * _ax.z
        if _yb.length < 1e-3:
            _yb = Vector((0, 1, 0)) - _ax * _ax.y
        _yb.normalize()
        _zb = _ax.cross(_yb).normalized()
        eb.tail = cl["c"] + _yb * max(0.05, max(cl["s"]) * 0.25)
        eb.align_roll(_zb)
    else:
        # wheels AND the MAIN rotor: tail along the axle => local Y IS the axle. The donor's main-rotor channel
        # spins about LOCAL Y ([DonorAxis]), so with the leaf rest preserved the spin lands on the mast axis even
        # when the mast is not perfectly vertical — the lean wobble dies at the source.
        # MAIN-ROTOR SIGN: the donor's Y-spin renders CLOCKWISE from above on a +axle bone; real (American)
        # helicopters turn COUNTER-clockwise, so the main-rotor bone takes the NEGATED axle — flips the donor
        # spin (and the own-clip spin with it). Statics unaffected (binds compensate). Wheels keep +axle.
        _dirn = -1.0 if cl.get("is_rotor") else 1.0
        eb.tail = cl["c"] + ax * _dirn * max(0.05, max(cl["s"]) * 0.25)
    eb.parent = eb_body
    cluster_bones.append(eb.name)
    wheel_axes[eb.name] = tuple(ax)
    for wn in cl["names"]:
        bone_of[wn] = eb.name
# ONE Turret bone shared by every turret part (dome plates, gun shield, barrel...) so the whole assembly is a
# single unit — for future rotation and as the muzzle-socket anchor — placed at the parts' combined bbox center.
def _combined_bbox(names):
    boxes = [world_bbox(find(n)) for n in names]
    mn = Vector(tuple(min(c[i] - s[i] / 2 for c, s in boxes) for i in range(3)))
    mx = Vector(tuple(max(c[i] + s[i] / 2 for c, s in boxes) for i in range(3)))
    return (mn + mx) / 2.0, mx - mn
eb_turret = None
if turret_names:
    tc, ts = _combined_bbox(turret_names)
    eb = arm_data.edit_bones.new("Turret")
    eb.head = tc
    eb.tail = tc + Vector((0, 0, max(0.05, max(ts) * 0.25)))
    eb.parent = eb_body
    eb_turret = eb
    for tn in turret_names:
        bone_of[tn] = "Turret"

# ONE Gun bone for the whole gun assembly (barrel, mantlet, mount) — the natural muzzleBone/socket anchor.
# Parented to the Turret when there is one (the gun must ride the aiming turret); casemate guns (Jagdpanzer)
# hang off Root.
if gun_names or muzzle_names or cradle_names:
    # THE TUBE vs THE WHOLE ASSEMBLY. Everything here welds to the one Gun bone, because everything here elevates
    # together about the trunnions — but the breech→muzzle span `gun_pivot` slides along is a property of the TUBE
    # alone. A cradle stops well short of the muzzle (26 units short on the M114), so folding it into the span would
    # shrink it and make the fraction lie about where along the barrel the trunnion sits.
    tube_names = gun_names + [m for m in muzzle_names if m not in gun_names]        # barrel + its brake
    gun_names = tube_names + [c for c in cradle_names if c not in tube_names]       # ...+ the cradle: one bone
    gc, gs = _combined_bbox(gun_names)
    # GUN PIVOT (2026-08-22): the runtime elevation (gunElevMax) rotates this bone about ITS OWN ORIGIN, so where
    # the head sits IS the trunnion. At the bbox centre — the historical placement, still the default — the tube
    # see-saws about its middle and the breech swings down through the carriage (measured on the M114: 76-unit
    # tube, breech 38 units behind the centre, ~16 units of dip at 25°). `gun_pivot` slides the head along the
    # assembly's LONG axis: 0 = the breech end, 1 = the muzzle, 0.5 = the bbox centre (unchanged behaviour).
    # The M114's trunnion measures ~0.4. Only the head moves; the tail stays as it was, so a rig that already
    # dials muzzleOffset/socketBones against this bone keeps its frame.
    _gpts = []
    for _gn2 in (tube_names or gun_names):        # span from the TUBE; fall back if only a cradle was marked
        _go = find(_gn2)
        if _go is not None:
            _gpts += [_go.matrix_world @ _v.co for _v in _go.data.vertices]
    if len(_gpts) >= 4:
        _gmn = Vector(tuple(min(p[i] for p in _gpts) for i in range(3)))
        _gmx = Vector(tuple(max(p[i] for p in _gpts) for i in range(3)))
        _glong = max(range(3), key=lambda i: _gmx[i] - _gmn[i])
        _breech = max(_gpts, key=lambda p: p[_glong])     # the assembly's two ends along its own length
        _muzzle = min(_gpts, key=lambda p: p[_glong])
        # which end is the BREECH? the one nearer the carriage — a barrel points away from its own mount
        if (_muzzle - eb_body.head).length < (_breech - eb_body.head).length:
            _breech, _muzzle = _muzzle, _breech
        # A MARKED BRAKE PINS THE TIP EXACTLY. Without one the muzzle end is the gun bbox's far extreme, which a
        # wide brake or a bracket hanging off the front skews; with one it is the marked geometry's farthest point
        # from the breech — no inference. This is the fire origin, and it is what `gun_pivot` measures against.
        if muzzle_names:
            _mpts = []
            for _mn2 in muzzle_names:
                _mo = find(_mn2)
                if _mo is not None:
                    _mpts += [_mo.matrix_world @ _v.co for _v in _mo.data.vertices]
            if _mpts:
                _muzzle = max(_mpts, key=lambda p: (p - _breech).length)
                print("VEHICLE MUZZLE %d part(s) -> tip=(%.2f, %.2f, %.2f) (welded to the Gun bone: a brake elevates and recoils with the tube)"
                      % (len(muzzle_names), _muzzle.x, _muzzle.y, _muzzle.z))
        if abs(gun_pivot - 0.5) > 1e-4:
            gc = _breech + (_muzzle - _breech) * gun_pivot
        gun_axis = (gc.copy(), _muzzle.copy())            # (trunnion, muzzle tip) — the deploy elevation needs both
        gun_bore = (_breech.copy(), _muzzle.copy())       # the WHOLE tube: what "fraction of tube length" measures
        print("VEHICLE gun pivot %.2f -> head=(%.2f, %.2f, %.2f) (breech (%.2f, %.2f, %.2f) .. muzzle (%.2f, %.2f, %.2f))"
              % (gun_pivot, gc.x, gc.y, gc.z, _breech.x, _breech.y, _breech.z, _muzzle.x, _muzzle.y, _muzzle.z))
        # THE FIRE ORIGIN, gun-bone-local — what HAF's `muzzleOffset` dial wants, in SOURCE units. The Animation
        # Lab's comment describes finding this by iterate-value-then-relaunch; with a brake marked it is measured.
        # Scale it by the bake's `size` before pasting: this is the raw model's scale, not the baked one.
        _off = _muzzle - gc
        print("VEHICLE MUZZLE fire origin (gun-local, SOURCE units — scale by the bake's size): %.3f, %.3f, %.3f"
              % (_off.x, _off.y, _off.z))
        if cradle_names:
            print("VEHICLE CRADLE %d part(s) welded to the Gun bone (elevates with the tube; excluded from the "
                  "breech->muzzle span, and it is what will STAY when the barrel recoils)" % len(cradle_names))
    eb = arm_data.edit_bones.new("Gun")
    eb.head = gc
    eb.tail = gc + Vector((0, 0, max(0.05, max(gs) * 0.25)))
    eb.parent = eb_turret if eb_turret is not None else eb_body
    eb_gun = eb
    for gn in gun_names:
        bone_of[gn] = "Gun"
    # THE BARREL SPLITS OFF — but only when recoil is asked for. Elevation wants tube and cradle on ONE bone (they
    # rotate together about the trunnions); recoil wants them apart (the tube slides, the cradle stays). Rather than
    # always pay a bone for a motion most guns never use, the split is conditional: with recoil off this block does
    # nothing and the rig is exactly what it was, so every gun already baked regenerates unchanged.
    # IDENTITY REST, DELIBERATELY. The tempting rig is a bone pointing down the bore, so recoil is -Y in bone space —
    # but a non-identity rest rotation is mangled by the skeleton bake (measured: an FBX local T of (21.096,0,0)
    # came back as (-0.00932,0,-0.00466)), which is why every bone this rigger makes is axis-aligned. So Barrel is
    # axis-aligned too and recoils by a LOCAL TRANSLATION along the bore direction taken in the rest frame. Because
    # rests are identity, bone-local == world at rest; because Barrel is a CHILD of Gun, that same local vector
    # rotates with the elevation, so the tube always slides along its own bore and never through the cradle.
    if recoil_dist > 0.0 and tube_names:
        _tc, _ts = _combined_bbox(tube_names)
        # THE RECOIL ARM (2026-08-22, in-game measured). A bone's OWN local translation does not render: the engine
        # keeps every bone at its bind position and plays only orientations through the hierarchy (Law 5). Measured
        # end-to-end on the first attempt — the clip baked the slide correctly, and the engine's own GetPoseTRS
        # decoded it correctly (`SLID 0,3 (0,0,-0.001)->(-0.001,-0.013,-0.301)`, matching the authored frame 8 to
        # three decimals) — and the barrel still did not move on screen. What DOES render is a position derived from
        # an ANCESTOR's rotation, because that is rebuilt by forward kinematics.
        # So the slide is faked as a long shallow arc: a hidden pivot placed far off the bore, with the Barrel
        # hanging under it. Rotating the arm by theta = dist/R swings the tube R*theta along its own bore — a
        # near-straight slide — while tilting it only theta. Same trick deploy_convert uses, and the reason its
        # howitzer visibly slides in-game while this one did not.
        _bore_d = (gun_axis[1] - gun_axis[0]).normalized() if gun_axis is not None else Vector((0.0, -1.0, 0.0))
        _slide = (gun_bore[1] - gun_bore[0]).length * recoil_dist if gun_bore is not None else 1.0
        _R = _slide / math.tan(math.radians(RECOIL_PITCH_DEG))     # radius for the chosen residual tilt
        _axis = _bore_d.cross(Vector((0.0, 0.0, 1.0)))
        if _axis.length < 1e-4:
            _axis = _bore_d.cross(Vector((0.0, 1.0, 0.0)))
        _axis.normalize()                                          # arc axis: perpendicular to the bore
        _off = _axis.cross(_bore_d).normalized()                   # offset direction: perpendicular to both
        _pivot = _tc + _off * _R
        eb = arm_data.edit_bones.new("RecoilArm")
        eb.head = _pivot
        eb.tail = _pivot + Vector((0, 0, max(0.05, max(_ts) * 0.25)))
        eb.parent = eb_gun                                         # rides the elevation, like the tube it carries
        eb_recoil_arm = eb
        recoil_geom = (_pivot.copy(), _axis.copy(), _bore_d.copy(), _slide, _R)
        print("VEHICLE RECOIL ARM: pivot (%.2f, %.2f, %.2f), R=%.1f for a %.2f-unit slide (residual tilt %.1f deg) "
              "— a bone's own translation does not render; an ancestor's rotation does"
              % (_pivot.x, _pivot.y, _pivot.z, _R, _slide, RECOIL_PITCH_DEG))
        eb = arm_data.edit_bones.new("Barrel")
        eb.head = _tc
        eb.tail = _tc + Vector((0, 0, max(0.05, max(_ts) * 0.25)))
        eb.parent = eb_recoil_arm
        for gn in tube_names:
            bone_of[gn] = "Barrel"      # the tube (and its brake) ride Barrel; the cradle stays on Gun
        recoil_bone = "RecoilArm"   # the clip ROTATES the arm; Barrel only carries the tube geometry
        print("VEHICLE BARREL bone split off for recoil: %d tube part(s) slide, %d cradle part(s) stay"
              % (len(tube_names), len(cradle_names)))

# TRAIL bones — one per arm, HINGED at the body end (not the bbox centre, which is what wheels use): a trail
# swings about where it meets the carriage. The hinge is picked geometrically as the arm's extreme END nearest
# the body bone, so it works whichever way a source points its arms. The bone runs down the arm to its spade,
# which makes the arm's own direction its local Y — the same convention the wheels use, and one that survives
# the bake (a bone's X/Z can be re-derived downstream; its head->tail direction cannot).
trail_bones = []   # (bone name, hinge world pos, spade world pos)
for _ti, _tn in enumerate(trail_names):
    _to = find(_tn)
    if _to is None:
        print("VEHICLE WARN: trail part '%s' not found — skipped" % _tn)
        continue
    _pts = [_to.matrix_world @ _v.co for _v in _to.data.vertices]
    if len(_pts) < 4:
        continue
    # the arm's long axis, then its two ends along it; the end NEAREST the body is the hinge
    _mn = Vector(tuple(min(p[i] for p in _pts) for i in range(3)))
    _mx = Vector(tuple(max(p[i] for p in _pts) for i in range(3)))
    _long = max(range(3), key=lambda i: _mx[i] - _mn[i])
    _end_lo = min(_pts, key=lambda p: p[_long])
    _end_hi = max(_pts, key=lambda p: p[_long])
    _ref = eb_body.head
    _hinge, _spade = (_end_lo, _end_hi) if (_end_lo - _ref).length <= (_end_hi - _ref).length else (_end_hi, _end_lo)
    _dir = (_spade - _hinge)
    if _dir.length < 1e-5:
        continue
    eb = arm_data.edit_bones.new("Trail_%02d" % _ti)
    eb.head = _hinge
    eb.tail = _hinge + _dir.normalized() * max(_dir.length * 0.5, 0.05)
    eb.parent = eb_body
    bone_of[_tn] = eb.name
    trail_bones.append((eb.name, _hinge.copy(), _spade.copy()))
    print("VEHICLE trail '%s' -> %s hinge=(%.2f, %.2f, %.2f) spade=(%.2f, %.2f, %.2f)"
          % (_tn, eb.name, _hinge.x, _hinge.y, _hinge.z, _spade.x, _spade.y, _spade.z))
# Track bones — TREADIZE v2 (2026-07-26, user-designed surfaces): a tread loop is FOUR motion regions, each on
# its own carrier: the FRONT/REAR wrap arcs skin to the SPROCKET/IDLER wheel bones (they rotate with the wheel —
# wrapping is free and spokes never penetrate), the BOTTOM run rides a bone translating backward and the TOP run
# one translating forward. All four advance one link-pitch per Spin loop and snap together at the restart — the
# vanilla pair/impair recipe in full. Use SMALL Spin degrees (~one sprocket tooth, 30°) so the advance ≈ one
# link pitch. Requires the animated bake with `Keep bone translations` ON (conversion path).
track_infos = []   # (partName, sideBotBone, sideTopBone, frontCluster, rearCluster)
for i, tn in enumerate(track_names):
    o = find(tn)
    c, s = world_bbox(o)
    side = "L" if c.y >= 0 else "R"
    # this side's wheel clusters -> frontmost = sprocket, rearmost = idler (wrap carriers)
    side_cls = [cl for cl in clusters if (cl["c"].y >= 0) == (c.y >= 0)]
    if not side_cls:
        side_cls = clusters
    front_cl = max(side_cls, key=lambda cl: cl["c"].x) if side_cls else None
    rear_cl = min(side_cls, key=lambda cl: cl["c"].x) if side_cls else None
    names = []
    for suffix in ("Bot", "Top", "RampF", "RampR", "RampRT"):
        eb = arm_data.edit_bones.new("Track_%02d_%s_%s" % (i, side, suffix))
        eb.head = c
        eb.tail = c + Vector((0, 0, max(0.05, max(s) * 0.25)))
        eb.parent = eb_body
        names.append(eb.name)
    # DEDICATED wrap bones co-located with the sprocket/idler (copied head/tail/roll = same axle axis).
    # The tread system runs its OWN smaller quantum than the visible wheels (fold-finder verdict: at 60 deg
    # the 0.42 advance exceeded the front ramp's ~0.34 span — ramp verts overshot their slope and folded the
    # panel inside-out). Wheels keep the user's spoke-symmetric degrees; wraps+shuttles run degrees/3.
    # The FIRST/LAST ROAD WHEEL get wrap bones too (user field finding: the ramp bends AROUND the first road
    # wheel — a straight ramp translation must cut into it; give it the sprocket treatment).
    low_cls = [cl for cl in side_cls if cl["c"].z <= c.z and cl is not front_cl and cl is not rear_cl]
    roadF_cl = max(low_cls, key=lambda cl: cl["c"].x) if low_cls else front_cl
    roadR_cl = min(low_cls, key=lambda cl: cl["c"].x) if low_cls else rear_cl
    for suffix, wcl in (("WrapF", front_cl), ("WrapR", rear_cl), ("WrapGF", roadF_cl), ("WrapGR", roadR_cl)):
        eb = arm_data.edit_bones.new("Track_%02d_%s_%s" % (i, side, suffix))
        if wcl is not None:
            wb = arm_data.edit_bones[cluster_bones[clusters.index(wcl)]]
            eb.head = wb.head.copy(); eb.tail = wb.tail.copy(); eb.roll = wb.roll
        else:
            eb.head = c; eb.tail = c + Vector((0, 0, max(0.05, max(s) * 0.25)))
        eb.parent = eb_body
        names.append(eb.name)
    track_infos.append((tn, names, front_cl, rear_cl, c.copy(), roadF_cl, roadR_cl))
bpy.ops.object.mode_set(mode='OBJECT')

# rigid skinning: each part full-weight on its bone (wheels/turret) or Root (body). TREAD parts skin
# PER-VERTEX into four regions: beyond the sprocket/idler centers -> that WHEEL's bone (the wrap arcs rotate
# with the wheel — no spoke penetration, wrapping for free), else top half -> Top bone, bottom half -> Bot bone.
_track_by_name = {t[0]: t for t in track_infos}
_tread_dirs = {}   # part -> (frontRampFlowDir, rearRampFlowDir) for degrees>0, filled at skinning
_band_rot = {}     # wrap bone -> radius the tread band rides at (for conveyor-pace rotation keying)
_link_pitch = {}   # part -> measured track-link pitch (conveyor advance = one pitch -> invisible restart)
_link_fund = {}    # part -> physical link length (autocorrelation fundamental) for rigid link cells
_link_jobs = {}    # part -> path-instanced rigid-link job (cells, ring path, rest transforms)
for o in objs:
    if o.name in _track_by_name:
        _tn, _tnames, _fcl, _rcl, _tc, _rfcl, _rrcl = _track_by_name[o.name]
        _botb, _topb, _rampfb, _ramprb, _ramprtb, _wrapfb, _wraprb, _wrapgfb, _wrapgrb = _tnames
        for g in list(o.vertex_groups):
            o.vertex_groups.remove(g)
        # SUBDIVIDE long tread edges first (tear-finder verdict on the low-poly Jagdpanzer tread: one edge
        # spanned ~70 deg of idler wrap arc, so wrap/shuttle boundaries jumped across a single edge no matter
        # where they were placed). Midpoint cuts are shape-preserving; target edge <= ~1/3 wrap radius so the
        # blend annulus actually contains vertices.
        # MEASURE THE LINK PITCH before subdividing (midpoint verts would pollute the period): the tread teeth
        # repeat along the bottom run — circular autocorrelation of vert x's finds the period. The conveyor
        # advance is then set to EXACTLY one link pitch, so the loop-restart snap maps the pattern onto itself
        # (the vanilla recipe) instead of jerking by a fraction of a link.
        _zs = [_v.co.z for _v in o.data.vertices]
        _xs_all = [_v.co.x for _v in o.data.vertices]
        _zlo, _zhi = min(_zs), max(_zs)
        _xlo, _xhi = min(_xs_all), max(_xs_all)
        _xspan = _xhi - _xlo
        _xs = [_v.co.x for _v in o.data.vertices
               if _v.co.z < _zlo + 0.12 * (_zhi - _zlo)
               and _xlo + 0.25 * _xspan < _v.co.x < _xhi - 0.25 * _xspan]
        _pitch, _fund, _best = 0.0, 0.0, 0.0
        if len(_xs) >= 24:
            _best, _scores = 0.0, []
            _pc = 0.04
            while _pc <= 0.5:
                _sr = sum(math.cos(2 * math.pi * _x / _pc) for _x in _xs)
                _si = sum(math.sin(2 * math.pi * _x / _pc) for _x in _xs)
                _R = math.sqrt(_sr * _sr + _si * _si) / len(_xs)
                _scores.append((_pc, _R))
                _best = max(_best, _R)
                _pc += 0.002
            # take the SMALLEST period still scoring near the max: sub-harmonics of the link (cleat+gap
            # features) map the pattern almost onto itself at a fraction of the motion — the Jagdpanzer's
            # 0.512 link has a near-perfect 0.256 half-repeat (R=0.976), which halves the loop deformation
            # vs full-pitch while keeping the restart invisible
            if _best > 0.3:
                _cands = [_pc for _pc, _R in _scores if _R >= 0.95 * _best]
                if _cands:
                    _pitch = min(_cands)
                # the FUNDAMENTAL (largest strong period) = the physical link length, used to cut the mesh
                # into rigid link cells; the advance uses the smallest sub-grid (least motion) instead
                for _pc, _R in reversed(_scores):
                    if _R >= 0.85 * _best:
                        _fund = _pc
                        break
        _link_pitch[_tn] = _pitch
        _link_fund[_tn] = _fund
        print("VEHICLE tread '%s' link pitch: %.3f, physical link %.3f (from %d bottom-run verts)"
              % (o.name, _pitch, _fund, len(_xs)))
        import bmesh
        _wrap_rs = [(_c["m"] * 0.5) for _c in (_fcl, _rcl) if _c is not None and _c.get("m", 0.0) > 1e-6]
        _thr = max(0.06, 0.35 * min(_wrap_rs)) if _wrap_rs else 0.15
        _nv0 = len(o.data.vertices)
        _bm = bmesh.new(); _bm.from_mesh(o.data)
        for _pass in range(3):
            _long = [e for e in _bm.edges if (e.verts[0].co - e.verts[1].co).length > _thr]
            if not _long:
                break
            bmesh.ops.subdivide_edges(_bm, edges=_long, cuts=1, use_grid_fill=True)
        _bm.to_mesh(o.data); _bm.free()
        print("VEHICLE tread '%s' subdivided: %d -> %d verts (edge target %.3f)"
              % (o.name, _nv0, len(o.data.vertices), _thr))
        # v4 (field-tuned): SIX regions with boundaries at the wheel TANGENT points, where surface velocities
        # naturally match — sprocket/idler wraps rotate with those wheels; the DIAGONAL RAMPS between them and
        # the first/last ROAD wheel slide along their own slope; top/bottom straights shuttle horizontally.
        # (v3's every-wheel carriers created shear boundaries mid-run — reverted.)
        _side_cls = [cl for cl in clusters if (cl["c"].y >= 0) == (_tc.y >= 0)] or clusters
        # wrap arcs ride the DEDICATED wrap bones (small tread quantum), never the visible wheel bones
        _sprb = _wrapfb if _fcl is not None else _botb
        _idlb = _wraprb if _rcl is not None else _botb
        # first/last road wheel: the SAME clusters the wrap bones were created against (stored at bone time)
        _roadF, _roadR = _rfcl, _rrcl
        # flow directions for degrees>0 (bottom runs backward): front ramp = sprocket -> first road wheel,
        # rear ramp = last road wheel -> idler (continuing the backward+up circulation). RIM-TO-RIM, not
        # center-to-center (fold-finder: center-based dirs made the front ramp flow 39 deg downhill when the
        # tread's actual slope — sprocket bottom rim to road-wheel ground rim — is ~24 deg; the spurious
        # vertical component stepped/folded the ramp<->bottom seam).
        def _rim_dir(_a, _b, _asign, _bsign):
            # direction from wheel _a's rim to wheel _b's rim (+1 = top rim, -1 = bottom rim), y flattened
            _az = _a["c"].z + _asign * _a["m"] * 0.5
            _bz = _b["c"].z + _bsign * _b["m"] * 0.5
            _v = Vector((_b["c"].x - _a["c"].x, 0.0, _bz - _az))
            return _v.normalized() if _v.length > 1e-6 else Vector((-1, 0, 0))
        _fdir = (_rim_dir(_fcl, _roadF, -1, -1) if (_fcl and _roadF and _roadF is not _fcl) else Vector((-1, 0, 0)))
        _rdir = (_rim_dir(_roadR, _rcl, -1, -1) if (_rcl and _roadR and _roadR is not _rcl) else Vector((-1, 0, 0)))
        # UPPER-REAR slope (field finding: "the track runs off at the upper back"): from the idler UP-FORWARD to
        # the rearmost return roller — part of the TOP circulation (flows forward), not the rear ramp's backward.
        _high_cls = [cl for cl in _side_cls if cl["c"].z > _tc.z and cl is not _fcl and cl is not _rcl]
        _rollR = min(_high_cls, key=lambda cl: cl["c"].x) if _high_cls else None
        _rtdir = (_rim_dir(_rcl, _rollR, 1, 1) if (_rcl and _rollR) else Vector((1, 0, 0)))
        _tread_dirs[_tn] = (_fdir, _rdir, _rtdir)
        _names = {_botb, _topb, _rampfb, _ramprb, _ramprtb, _sprb, _idlb, _wrapgfb, _wrapgrb}
        _vgs = {n: o.vertex_groups.new(name=n) for n in _names}
        _spr_c, _spr_r = (_fcl["c"], _fcl["m"] * 0.5) if _fcl else (Vector((1e9,) * 3), 0.0)
        _idl_c, _idl_r = (_rcl["c"], _rcl["m"] * 0.5) if _rcl else (Vector((1e9,) * 3), 0.0)

        # SELF-CALIBRATED wrap band (the idler crumple, seen in renders): the capture radii were expressed in
        # WHEEL radii, assuming the tread hugs the rim like it hugs the sprocket teeth — but the Jagdpanzer
        # idler is a small wheel with the track standing ~1.7 r off its rim, so most of its real wrap arc never
        # got wheel weight and was shredded between shuttle regions. Measure the tread's OWN radial band in the
        # wheel's pure-wrap sector (front half for the sprocket, rear half for the idler) and capture to that.
        def _wrap_band(_wc, _r0, _sgn):
            if _r0 <= 0.0:
                return (0.0, 0.0)
            _ds = []
            for _v in o.data.vertices:
                _p = Vector(_v.co)
                if _sgn * (_p.x - _wc.x) < 0.3 * _r0:
                    continue   # only the unambiguous wrap half — nothing else lives there
                if _p.z < _wc.z - _r0 * 2.2 or _p.z > _wc.z + _r0 * 2.2:
                    continue
                _d = (_p - _wc).length
                if _d < 2.4 * _r0:
                    _ds.append(_d)
            if len(_ds) < 8:
                return (_r0 * 1.15, _r0 * 1.4, _r0 * 1.1)
            _ds.sort()
            _lo = _ds[int(0.05 * (len(_ds) - 1))]   # inner face of the tread band
            _hi = _ds[int(0.95 * (len(_ds) - 1))]   # outer face of the tread band
            return (_hi * 1.02, _hi * 1.02 + 0.4 * _r0, 0.5 * (_lo + _hi))
        _full_f, _fade_f, _rot_f = _wrap_band(_spr_c, _spr_r, 1.0)
        _full_r, _fade_r, _rot_r = _wrap_band(_idl_c, _idl_r, -1.0)

        # road-wheel bend bands: measured DIRECTLY UNDER the wheel (its wrap is the bottom contact arc where
        # the ramp folds around it; the side-sector sampler would sweep up the ramp and overestimate)
        def _under_band(_cl):
            if _cl is None or _cl is _fcl or _cl is _rcl:
                return (Vector((1e9,) * 3), 0.0, 0.0, 0.0, 0.0)
            _c = _cl["c"]; _r = _cl["m"] * 0.5
            _ds = []
            for _v in o.data.vertices:
                _p = Vector(_v.co)
                if abs(_p.x - _c.x) < 0.6 * _r and _p.z < _c.z:
                    _d = (_p - _c).length
                    if _d < 2.4 * _r:
                        _ds.append(_d)
            if len(_ds) < 8:
                return (_c, _r, _r * 1.15, _r * 1.5, _r * 1.1)
            _ds.sort()
            _lo = _ds[int(0.05 * (len(_ds) - 1))]
            _hi = _ds[int(0.95 * (len(_ds) - 1))]
            # fade width must span MULTIPLE mesh edges (0.3 r was narrower than one edge — the blend corridor
            # between this wheel and the idler was jumped entirely by a single edge: 1.00<->1.00 seam)
            return (_c, _r, _hi * 1.02, _hi * 1.02 + 0.7 * _r, 0.5 * (_lo + _hi))
        _rf_c, _rf_r, _full_gf, _fade_gf, _rot_gf = _under_band(_roadF)
        _rr_c, _rr_r, _full_gr, _fade_gr, _rot_gr = _under_band(_roadR)
        # the advance this loop will use (pitch-matched when plausible) — exit fades must complete one
        # advance-length UPSTREAM of the exit tangent, or rotating verts get carried PAST the exit during
        # the loop (the tread visibly drooped BELOW the front road wheel and slacked off the idler top)
        _pm = _link_pitch.get(_tn, 0.0)
        _adv_est = _pm if 0.04 <= _pm <= 0.3 else math.pi * max(cl["m"] for cl in clusters) * (abs(degrees) / 3.0) / 360.0
        # speed match must use the radius the TREAD RIDES AT (the measured band), not the wheel rim — road
        # wheels/idler stand well off their rims, so rim-based rotation ran the wrap 20-60% faster than the
        # conveyor. Stored per wrap bone for the keying pass.
        _band_rot[_wrapfb] = _rot_f
        _band_rot[_wraprb] = _rot_r
        _band_rot[_wrapgfb] = _rot_gf
        _band_rot[_wrapgrb] = _rot_gr
        print("VEHICLE tread '%s' wrap bands: sprocket %.2f/%.2f (r=%.2f), idler %.2f/%.2f (r=%.2f), roadF %.2f/%.2f (r=%.2f), roadR %.2f/%.2f (r=%.2f)"
              % (o.name, _full_f, _fade_f, _spr_r, _full_r, _fade_r, _idl_r,
                 _full_gf, _fade_gf, _rf_r, _full_gr, _fade_gr, _rr_r))

        def _shuttle_region(_p):
            # RampF = ONLY the descending front ramp, BELOW the sprocket center (tear-finder verdict: the old
            # condition also swallowed the TOP RUN's front end, flowing it down-back against the sprocket's
            # forward top — 0.43-unit tears). Above the sprocket center the front column belongs to Top.
            if (_roadF is not None and _p.x > _roadF["c"].x and _p.z > _roadF["c"].z
                    and (_fcl is None or _p.z < _fcl["c"].z)):
                return _rampfb
            if _roadR is not None and _p.x < _roadR["c"].x and _rcl is not None and _p.z > _rcl["c"].z:
                return _ramprtb   # upper-rear slope: above the IDLER's center — flows forward with the top run
            if _roadR is not None and _p.x < _roadR["c"].x and _p.z > _roadR["c"].z:
                return _ramprb
            return _topb if _p.z > _tc.z else _botb

        # BLENDED boundaries (field finding: the rear kinked where wrap met ramp): inside the wheel radius =
        # full wheel; an annulus out to 1.6 r fades wheel -> shuttle linearly, so the wrap-to-run transition
        # interpolates smoothly instead of folding at a hard cut (real-rig smooth skinning, minimal form).
        _stats = {}
        _wmap = [dict() for _ in range(len(o.data.vertices))]
        for _v in o.data.vertices:   # transforms were applied — local == world
            _p = Vector(_v.co)
            _caps = []
            for _wc, _d0, _r0, _wb, _sd, _full, _fade, _road in (
                    (_spr_c, (_p - _spr_c).length, _spr_r, _sprb, 1.0, _full_f, _fade_f, False),
                    (_idl_c, (_p - _idl_c).length, _idl_r, _idlb, -1.0, _full_r, _fade_r, False),
                    (_rf_c, (_p - _rf_c).length, _rf_r, _wrapgfb, 1.0, _full_gf, _fade_gf, True),
                    (_rr_c, (_p - _rr_c).length, _rr_r, _wrapgrb, -1.0, _full_gr, _fade_gr, True)):
                if _r0 <= 0.0 or _full <= 0.0:
                    continue
                # tread that merely PASSES UNDER a raised wrap wheel is straight-run material, not wrap — radial
                # capture alone grabbed it and rotated it into a tear. A wheel only carries verts at wrap
                # height: above its band's lower edge minus a small margin. FEATHERED over 0.2 r (a binary cut
                # landed mid-tread-thickness under the sprocket: bottom face Bot 1.00, top face wheel 1.00 —
                # crisp tear between the tread's own faces).
                _hz = (_p.z - (_wc.z - _fade)) / (0.2 * _r0)
                if _hz <= 0.0:
                    continue
                _fh = min(1.0, _hz)
                _fa = _fh
                if _road:
                    # road-wheel bend: wrap only the BOTTOM contact arc — fade out above axle height where the
                    # tread is ramp/straight-run material
                    if _p.z > _wc.z + 0.4 * _r0:
                        continue
                    if _p.z > _wc.z:
                        _fa *= 1.0 - (_p.z - _wc.z) / (0.4 * _r0)
                    # ...and only on the wheel's BEND side (toward its ramp). FLOW-AWARE exit (user: "make the
                    # track tighter"): the front road wheel RELEASES tread at bottom-dead-center — a vert still
                    # wheel-weighted there gets rotated PAST BDC and dips BELOW the ground line (the droop under
                    # the wheels). Hard-cut at BDC, ramp the weight in over one advance-length upstream so every
                    # vert has handed off to Bot by the time the loop carries it to the exit. The rear road
                    # wheel's BDC is an ENTRY (flow runs backward into its bend) — no dip there, same gate is
                    # safe.
                    _s = _sd * (_p.x - _wc.x)
                    if _s <= 0.0:
                        continue
                    if _s < _adv_est:
                        _fa *= _s / _adv_est
                    if _fa <= 0.0:
                        continue
                elif _p.z > _wc.z:
                    # ANGULAR feather (tear-finder: the idler kept grabbing its upper-FRONT quadrant — tread
                    # that has already exited the wrap toward the return roller — and rotated it forward-DOWN
                    # against RampRT's forward-up flow). The wrap tops out at the sprocket's FRONT half / the
                    # idler's REAR half; only ABOVE center (below, both sides legitimately hold the bottom/ramp
                    # tangents). Fade over 0.5 r rather than hard-cut — a binary gate left 1.00<->1.00 crisp
                    # boundaries that tore. The IDLER's top boundary is an EXIT (its top surface moves forward,
                    # INTO the feather) — retreat its margin one advance-length upstream so verts hand off
                    # before the loop carries them past (the slack off the idler top). The sprocket's top
                    # boundary is an ENTRY (surface moves forward, AWAY from its rear feather) — full margin.
                    _mrg = 0.35 * _r0 if _sd > 0 else max(-0.4 * _r0, 0.35 * _r0 - _adv_est)
                    _ex = -(_sd * (_p.x - _wc.x)) - _mrg
                    if _ex > 0.0:
                        _fa *= 1.0 - _ex / (0.5 * _r0)
                        if _fa <= 0.0:
                            continue
                # the tread's wrap ARC must be FULL wheel weight or the rotating wheel penetrates it (the v6
                # regression). Full out to the MEASURED band's outer face, then fade into the shuttle region.
                _w = None
                if _d0 <= _full:
                    _w = _fa
                elif _d0 <= _fade:
                    # WHEEL-BIASED fade (v8: penetration at the bottom wrap): a linear rotation/translation blend
                    # takes the CHORD and dips INSIDE the wheel rim — quadratic falloff keeps blend verts hugging
                    # the arc longer (a slight outward bulge reads fine; an inward dip through the rim does not).
                    _t = (_d0 - _full) / max(1e-6, _fade - _full)
                    _w = (1.0 - _t * _t) * _fa
                if _w is not None and _w > 0.001:
                    _caps.append((_wb, _w))
            # COMBINE overlapping wheel claims instead of first-wins (tear-finder: the idler and the rear road
            # wheel both fully claimed adjacent verts in their overlap corridor — two different rotations met
            # at a hard 1.00<->1.00 handoff). Both are speed-matched, so an LBS average transitions smoothly
            # along the corridor; any weight left over goes to the shuttle region.
            if not _caps:
                _pairs = [(_shuttle_region(_p), 1.0)]
            else:
                _tot = sum(_w for _, _w in _caps)
                if _tot >= 0.999:
                    _pairs = [(_b, _w / _tot) for _b, _w in _caps]
                else:
                    _pairs = _caps + [(_shuttle_region(_p), 1.0 - _tot)]
            _wmap[_v.index] = {_gn: _w for _gn, _w in _pairs if _w > 0.001}
        # LAPLACIAN WEIGHT SMOOTHING — one gentle pass to iron capture noise before the cells lock in
        _nbr = [[] for _ in range(len(o.data.vertices))]
        for _e in o.data.edges:
            _a, _b = _e.vertices
            _nbr[_a].append(_b); _nbr[_b].append(_a)
        for _it in range(1):
            _new = []
            for _vi in range(len(_wmap)):
                if not _nbr[_vi]:
                    _new.append(_wmap[_vi]); continue
                _acc = {}
                for _g, _w in _wmap[_vi].items():
                    _acc[_g] = _acc.get(_g, 0.0) + 0.5 * _w
                _sh = 0.5 / len(_nbr[_vi])
                for _nb in _nbr[_vi]:
                    for _g, _w in _wmap[_nb].items():
                        _acc[_g] = _acc.get(_g, 0.0) + _sh * _w
                _tt = sum(_acc.values()) or 1.0
                _new.append({_g: _w / _tt for _g, _w in _acc.items() if _w / _tt > 0.005})
            _wmap = _new
        # LINK-RIGID CELLS (user verdict: continuous-band deformation reads as a LOOSE track no matter how
        # smooth). Real (and vanilla) tracks are RIGID LINKS articulating at pins: cut the loop into
        # link-length cells along its path and give every vert in a cell the cell's AVERAGE weights — each
        # molded link then moves rigidly (tight), and all deformation concentrates into the recessed gaps
        # between cleats where a real track hinges anyway.
        _fund_p = _link_fund.get(_tn, 0.0)
        if _fund_p > 0.03:
            import bisect as _bs
            # BELT-AROUND-PULLEYS path (link-probe verdict: the theta-around-centroid parameterization merges
            # distinct path sections at the CONCAVE rear — a radial ray crosses the band twice near the raised
            # idler — scattering links). We know every wheel center AND the tread-band radius at each; the true
            # path is the classic belt construction: CCW-ordered circles joined by external tangents + wrap
            # arcs. Exact straights, exact arcs, immune to concavity.
            _sto = max(0.03, (_rot_r - _idl_r) if (_idl_r > 0.0 and _rot_r > _idl_r) else 0.08)
            _circ = []
            if _fcl is not None and _rot_f > 0.02:
                _circ.append((_spr_c.x, _spr_c.z, _rot_f))
            for _cl2 in sorted(_high_cls, key=lambda _c2: -_c2["c"].x):   # top rollers, front -> rear
                _circ.append((_cl2["c"].x, _cl2["c"].z, _cl2["m"] * 0.5 + _sto))
            if _rcl is not None and _rot_r > 0.02:
                _circ.append((_idl_c.x, _idl_c.z, _rot_r))
            if _rr_r > 0.0 and _rot_gr > 0.02:
                _circ.append((_rr_c.x, _rr_c.z, _rot_gr))
            if _rf_r > 0.0 and _rot_gf > 0.02:
                _circ.append((_rf_c.x, _rf_c.z, _rot_gf))
            _ncr = len(_circ)
            _norms = []
            for _i in range(_ncr):
                _x1, _z1, _r1 = _circ[_i]; _x2, _z2, _r2 = _circ[(_i + 1) % _ncr]
                _dxb, _dzb = _x2 - _x1, _z2 - _z1
                _db = math.hypot(_dxb, _dzb) or 1e-9
                _exb, _ezb = _dxb / _db, _dzb / _db
                _cpb = max(-1.0, min(1.0, (_r1 - _r2) / _db))
                _spb = math.sqrt(1.0 - _cpb * _cpb)
                # unit normal to the external tangent, pointing outward (right of CCW travel)
                _norms.append((_exb * _cpb + _ezb * _spb, _ezb * _cpb - _exb * _spb))
            _raw = []
            _arc_ranges_raw = []   # (s_start, s_end) of each wheel-wrap ARC in raw path length (v2 hybrid)
            _cum = [0.0]

            def _rawadd(_p):
                if _raw:
                    _cum.append(_cum[-1] + (_p - _raw[-1]).length)
                _raw.append(_p)
            for _i in range(_ncr):
                _xc, _zc, _rc2 = _circ[_i]
                _na = _norms[(_i - 1) % _ncr]   # arrival normal
                _nd = _norms[_i]                # departure normal
                _a0b = math.atan2(_na[1], _na[0])
                _a1b = math.atan2(_nd[1], _nd[0])
                while _a1b < _a0b - 1e-9:
                    _a1b += 2 * math.pi
                _steps = max(1, int((_a1b - _a0b) * _rc2 / 0.02))
                _arc_s0 = None
                for _k in range(_steps):
                    _ab = _a0b + (_a1b - _a0b) * _k / _steps
                    _rawadd(Vector((_xc + _rc2 * math.cos(_ab), 0.0, _zc + _rc2 * math.sin(_ab))))
                    if _arc_s0 is None:
                        _arc_s0 = _cum[-1]   # AFTER the first sample: the preceding segment jump stays OUTSIDE the arc range
                _rawadd(Vector((_xc + _rc2 * _nd[0], 0.0, _zc + _rc2 * _nd[1])))
                _arc_ranges_raw.append((_arc_s0, _cum[-1]))
            _SP = [0.0]
            for _k in range(len(_raw)):
                _SP.append(_SP[-1] + (_raw[(_k + 1) % len(_raw)] - _raw[_k]).length)
            _LP = _SP[-1]
            # uniform arc-length resample (no smoothing needed — the belt is already exact)
            _M = 512
            _pts = []
            for _m in range(_M):
                _sT = _m / _M * _LP
                _k = min(len(_raw) - 1, max(0, _bs.bisect_right(_SP, _sT) - 1))
                _seg = _SP[_k + 1] - _SP[_k]
                _fr = ((_sT - _SP[_k]) / _seg) if _seg > 1e-9 else 0.0
                _pts.append(_raw[_k].lerp(_raw[(_k + 1) % len(_raw)], _fr))
            _S = [0.0] * (_M + 1)
            for _i in range(_M):
                _S[_i + 1] = _S[_i] + (_pts[(_i + 1) % _M] - _pts[_i]).length
            _L = _S[_M]
            # QUARTER-LINK cells: wraps around wheels render as polygons with one facet per cell — full-link
            # cells made the sprocket wrap read chunky, half-link was better, quarter-link (user request) makes
            # the wrap facets ~15 deg. Straight runs are unaffected (rigid transport shows no cell boundaries
            # on a straight). The conveyor advances CELLS_PER_LINK cells per loop = one full link, keeping
            # speed and exact restart. NOTE the Amplitude 256-bone budget: quarter-link = 216 link bones on the
            # Jagdpanzer — only affordable because link mode DELETES the unused legacy tread bones (18).
            _CPL = tread_cells_per_link   # cells per link — the Lab's "Tread detail" bones dial

            def _project(_ptsP, _SP2):
                # per-vert path parameter: nearest path sample (XZ plane)
                _out = []
                for _v in o.data.vertices:
                    _vx, _vz = _v.co[0], _v.co[2]
                    _bd, _bm = 1e18, 0
                    for _m in range(len(_ptsP)):
                        _pp = _ptsP[_m]
                        _dd = (_pp.x - _vx) * (_pp.x - _vx) + (_pp.z - _vz) * (_pp.z - _vz)
                        if _dd < _bd:
                            _bd, _bm = _dd, _m
                    _out.append(_SP2[_bm])
                return _out
            _s_of = _project(_pts, _S)
            # REFIT the path through the MESH'S OWN CENTERLINE (user: "make the track tighter"): the analytic
            # belt is idealized — real standoffs and top-run SAG deviate from it, and wherever they do the
            # links drift off the modeled line mid-loop and snap back at restart (reads as slack). The belt's
            # nearest-point projection is a concavity-safe parameterization, so refit: per-s-bin centroids of
            # the tread verts = the artist's actual line (sag included), lightly smoothed, then re-project.
            _cbx = [0.0] * _M; _cbz = [0.0] * _M; _cbc = [0] * _M
            for _vi, _v in enumerate(o.data.vertices):
                _bi = min(_M - 1, int(_s_of[_vi] / _L * _M))
                _cbx[_bi] += _v.co[0]; _cbz[_bi] += _v.co[2]; _cbc[_bi] += 1
            _ctr_pts = []
            for _i in range(_M):
                if _cbc[_i]:
                    _ctr_pts.append(Vector((_cbx[_i] / _cbc[_i], 0.0, _cbz[_i] / _cbc[_i])))
                else:
                    _ctr_pts.append(None)
            for _i in range(_M):   # fill gaps from circular neighbors
                if _ctr_pts[_i] is None:
                    _j = 1
                    while _ctr_pts[(_i + _j) % _M] is None:
                        _j += 1
                    _k2 = 1
                    while _ctr_pts[(_i - _k2) % _M] is None:
                        _k2 += 1
                    _pa, _pb = _ctr_pts[(_i - _k2) % _M], _ctr_pts[(_i + _j) % _M]
                    _ctr_pts[_i] = _pa.lerp(_pb, _k2 / (_k2 + _j))
            # DELTA formulation (narrow smoothing of the raw centerline crumpled the wraps — the per-bin
            # centroids wiggle at cleat frequency): keep the belt's EXACT arc geometry and add only the
            # WIDE-smoothed deviation of the mesh centerline from it. Sag/standoff corrections are long
            # wavelength and survive; cleat noise cancels inside the window (~one full link).
            _delta = [_ctr_pts[_m] - _pts[_m] for _m in range(_M)]
            _hw2 = max(4, int(round(_fund_p / (_L / _M))))   # one link of samples each side
            _dsm = [sum((_delta[(_m + _k) % _M] for _k in range(-_hw2, _hw2 + 1)), Vector((0, 0, 0))) / (2 * _hw2 + 1.0)
                    for _m in range(_M)]
            _pts = [_pts[_m] + _dsm[_m] for _m in range(_M)]
            _S = [0.0] * (_M + 1)
            for _i in range(_M):
                _S[_i + 1] = _S[_i] + (_pts[(_i + 1) % _M] - _pts[_i]).length
            _L = _S[_M]
            _s_of = _project(_pts, _S)
            _NC = max(8, int(round(_L / (_fund_p / _CPL))))
            # HARD BONE-BUDGET CLAMP (the Bradley incident: its finely-molded links at detail 2 exploded to
            # 278 bones — past the 256 GPU vertex-format wall, guaranteed spikes — and the rig let it happen
            # silently). Links get whatever remains of 240 after wheels/Root/Turret/Gun, split evenly across
            # tracks; cells grow to fit. 240 keeps headroom under the 256 wall.
            _non_link = len(cluster_bones) + 1 + (1 if turret_names else 0) + (1 if gun_names else 0)
            _link_budget = max(8, (240 - _non_link) // max(1, len(track_names)))
            if _NC > _link_budget:
                print("VEHICLE tread '%s' BONE BUDGET CLAMP: %d cells -> %d (%d wheels+aux leave %d links/track under the 256-bone wall) — cells grow accordingly; lower Tread detail to regain smoothness headroom"
                      % (o.name, _NC, _link_budget, len(cluster_bones), _link_budget))
                _NC = _link_budget
            _cellL = _L / _NC
            # cut PHASE: try offsets and keep the one crossed by the fewest edges, so hinge cuts land in the
            # cleat GAPS instead of through cleats (the gamedev.tv seam lesson, applied to cells)
            _edges_ab = [(_e.vertices[0], _e.vertices[1]) for _e in o.data.edges]
            _best_off, _best_cross = 0.0, None
            for _k in range(24):
                _off = _k / 24.0 * _cellL
                _cross = 0
                for _a, _b in _edges_ab:
                    if int(((_s_of[_a] + _off) % _L) / _cellL) != int(((_s_of[_b] + _off) % _L) / _cellL):
                        _cross += 1
                if _best_cross is None or _cross < _best_cross:
                    _best_off, _best_cross = _off, _cross
            _cell_of = [min(_NC - 1, int(((_s_of[_vi] + _best_off) % _L) / _cellL)) for _vi in range(len(_wmap))]
            # HYBRID LINK/SHUTTLE SPLIT (treadize v2, the user's "smart chains" design): on a STRAIGHT segment
            # every link moves identically, so ONE translating shuttle bone carries the whole run — per-link
            # bones only on the wheel-wrap ARCS plus a one-advance TRANSITION margin on each side (so links
            # hand over at the tangent points, where a wrap link's velocity equals the run direction). Cuts
            # the bone count ~3x at equal wrap smoothness.
            _arc_rngs = [((_a * _L / _LP), (_b * _L / _LP)) for _a, _b in _arc_ranges_raw]
            # tiny arcs (a return roller the belt merely grazes — span under one cell) ride the straights:
            # per-link treatment there wastes bones and needlessly fragments the shuttle runs
            _arc_rngs = [_r for _r in _arc_rngs if _r[1] - _r[0] >= _cellL]
            _margin = tread_adv_cells * _cellL

            def _in_arcs(_sq):
                for _a0r, _a1r in _arc_rngs:
                    _lo2, _hi2 = _a0r - _margin, _a1r + _margin
                    _sw = _sq
                    if _lo2 < 0 and _sw > _L + _lo2:
                        _sw -= _L
                    if _hi2 > _L and _sw < _hi2 - _L:
                        _sw += _L
                    if _lo2 <= _sw <= _hi2:
                        return True
                return False
            _link_cells, _shuttle_cells = [], []
            for _ci in sorted(set(_cell_of)):
                _sc = ((_ci + 0.5) * _cellL - _best_off) % _L
                (_link_cells if _in_arcs(_sc) else _shuttle_cells).append(_ci)
            # group shuttle cells into contiguous RUNS (each run = one straight stretch = one bone); cells of
            # one run share a single translation, so contiguity in cell index is the right grouping (arc cells
            # break the sequence). Handle the wrap-around join (last..first).
            _runs = []
            for _ci in _shuttle_cells:
                if _runs and _ci == _runs[-1][-1] + 1:
                    _runs[-1].append(_ci)
                else:
                    _runs.append([_ci])
            if len(_runs) > 1 and _runs[0][0] == 0 and _runs[-1][-1] == _NC - 1:
                _runs[0] = _runs[-1] + _runs[0]; _runs.pop()
            # PATH-INSTANCED RIGID LINKS (the industry recipe the user's modeling guide points at — curve/path
            # instancing — translated to bakeable skeletal form): every link cell gets its OWN BONE, keyed every
            # frame to ride the measured ring path. No skin blending at all: each molded link is transported
            # rigidly, the tread hugs the path by construction, and advance = exactly one cell period so the
            # loop restart maps link-onto-link.
            _link_jobs[_tn] = {
                "prefix": _botb[:-3], "NC": _NC, "cellL": _cellL, "L": _L, "S": _S, "pts": _pts,
                # ONLY cells that actually own vertices get bones (the side-skirt-hidden tread stretch has NO
                # modeled geometry -> its cells were ZERO-WEIGHT bones, silently dropped between Blender and
                # the Amplitude bake -> every bone index above the drop shifted -> the in-game scramble/spikes)
                "cells": _link_cells,          # per-link bones: wrap arcs + transition margin only (v2 hybrid)
                "runs": _runs,                 # straight runs: one shuttle bone each, cells listed per run
                "off": _best_off, "cell_of": _cell_of, "obj": o.name, "cpl": _CPL,
                # advance in CELLS per loop (CLI-tweakable). 4 (= one link) synced the SPROCKET but visibly
                # outran the eight ROAD WHEELS, whose symmetry snap (105.6 -> 90 deg) puts their rims at
                # ~0.37/loop (user field report: "the belt should go slightly slower"). Default 3 = 0.373
                # near-syncs the road wheels — the dominant visual — restarting on the quarter-link cleat grid.
                "adv_cells": tread_adv_cells,
                "aux": list(_tnames),   # legacy carrier bones — unused in link mode, deleted to fit the bone cap
                "s_rest": [((_ci + 0.5) * _cellL - _best_off) % _L for _ci in range(_NC)],
            }
            print("VEHICLE tread '%s' HYBRID v2: %d link bones on wraps + %d shuttle run(s) covering %d cells (cell %.3f, loop %.2f, cut phase %.3f)"
                  % (o.name, len(_link_cells), len(_runs), len(_shuttle_cells), _cellL, _L, _best_off))
        if _tn not in _link_jobs:
            for _vi, _ws in enumerate(_wmap):
                for _gn, _w in _ws.items():
                    _vgs[_gn].add([_vi], _w, 'REPLACE')
                    _stats[_gn] = _stats.get(_gn, 0) + 1
            _byg = {k: [0] * v for k, v in _stats.items()}   # counts only, for the report line
            print("VEHICLE tread '%s' skinned (carrier blend fallback): %s" % (o.name, ", ".join("%s=%d" % (g, len(ix)) for g, ix in sorted(_byg.items()))))
        md = o.modifiers.new("Armature", 'ARMATURE'); md.object = arm
        o.parent = arm
        continue
    bname = bone_of.get(o.name, body_bone)
    for g in list(o.vertex_groups):
        o.vertex_groups.remove(g)
    vg = o.vertex_groups.new(name=bname)
    vg.add(list(range(len(o.data.vertices))), 1.0, 'REPLACE')
    md = o.modifiers.new("Armature", 'ARMATURE'); md.object = arm
    o.parent = arm

# ---- path-instanced link bones (deferred: cells were only known after tread analysis) ----
import bisect
from mathutils import Matrix


def _path_eval(_job, _s):
    _S, _pts, _L = _job["S"], _job["pts"], _job["L"]
    _s = _s % _L
    _b = min(len(_pts) - 1, max(0, bisect.bisect_right(_S, _s) - 1))
    _b2 = (_b + 1) % len(_pts)
    _seg = _S[_b + 1] - _S[_b]
    _f = ((_s - _S[_b]) / _seg) if _seg > 1e-9 else 0.0
    _p = _pts[_b].lerp(_pts[_b2], _f)
    _t = _pts[_b2] - _pts[_b]
    if _t.length < 1e-9:
        _t = _pts[(_b2 + 1) % len(_pts)] - _pts[_b]
    return _p, _t.normalized()


if _link_jobs:
    bpy.ops.object.select_all(action='DESELECT')
    bpy.context.view_layer.objects.active = arm
    arm.select_set(True)
    bpy.ops.object.mode_set(mode='EDIT')
    for _tn, _job in _link_jobs.items():
        # the legacy carrier bones carry no weights in link mode — delete them (quarter-link cells need the
        # bone budget: Amplitude caps skeletons at 256 bones)
        for _an in _job["aux"]:
            _ab = arm_data.edit_bones.get(_an)
            if _ab is not None:
                arm_data.edit_bones.remove(_ab)
        for _ci in _job["cells"]:   # only vert-owning cells — a zero-weight bone gets DROPPED downstream, shifting every index above it
            eb = arm_data.edit_bones.new("%sL%02d" % (_job["prefix"], _ci))
            _P0, _ = _path_eval(_job, _job["s_rest"][_ci])
            eb.head = _P0
            eb.tail = _P0 + Vector((0, 0, 0.1))
            eb.parent = arm_data.edit_bones[body_bone]
        # v2 hybrid: ONE shuttle bone per straight run, at the run's midpoint cell
        for _ri, _run in enumerate(_job["runs"]):
            eb = arm_data.edit_bones.new("%sS%02d" % (_job["prefix"], _ri))
            _P0, _ = _path_eval(_job, _job["s_rest"][_run[len(_run) // 2]])
            eb.head = _P0
            eb.tail = _P0 + Vector((0, 0, 0.1))
            eb.parent = arm_data.edit_bones[body_bone]
    bpy.ops.object.mode_set(mode='OBJECT')
    print("VEHICLE armature: %d bones total (Amplitude cap 256)" % len(arm_data.bones))
    for _tn, _job in _link_jobs.items():
        _o = bpy.data.objects[_job["obj"]]
        for _g in list(_o.vertex_groups):   # carrier groups stayed empty in link mode — drop them
            _o.vertex_groups.remove(_g)
        _run_of = {}
        for _ri, _run in enumerate(_job["runs"]):
            for _ci in _run:
                _run_of[_ci] = _ri
        _by_bone = {}
        for _vi, _ci in enumerate(_job["cell_of"]):
            _bn = ("%sS%02d" % (_job["prefix"], _run_of[_ci])) if _ci in _run_of else ("%sL%02d" % (_job["prefix"], _ci))
            _by_bone.setdefault(_bn, []).append(_vi)
        for _bn, _vis in _by_bone.items():
            _vg = _o.vertex_groups.new(name=_bn)
            _vg.add(_vis, 1.0, 'REPLACE')
    print("VEHICLE hybrid bones: %s" % ", ".join("%s links x%d + shuttles x%d" % (_j["prefix"], len(_j["cells"]), len(_j["runs"])) for _j in _link_jobs.values()))

# ---- join shards per bone ----
# 3,350 tiny objects make every downstream step crawl (the animated bake's Blender sub-process TIMED OUT on
# the un-joined file). Rigid skinning is per-part anyway, so after weights are assigned the rig needs at most
# ONE mesh per bone: hull -> one, each wheel -> one, turret -> one. Vertex groups merge by name on join, the
# active object's armature modifier and parenting survive.
def _join_per_bone():
    global objs
    groups = {}
    for o in objs:
        # tread parts are multi-bone-skinned (four regions) — each stays its OWN mesh, never merged
        _k = ("__track__" + o.name) if o.name in _track_by_name else bone_of.get(o.name, body_bone)
        groups.setdefault(_k, []).append(o)
    joined = []
    for bname, members in groups.items():
        bpy.ops.object.select_all(action='DESELECT')
        for m in members:
            m.select_set(True)
        bpy.context.view_layer.objects.active = members[0]
        if len(members) > 1:
            bpy.ops.object.join()
        m0 = bpy.context.view_layer.objects.active
        m0.name = "Mesh_" + bname
        joined.append(m0)
    objs = joined
    print("VEHICLE join: %d mesh(es), one per bone" % len(objs))
_guard(_join_per_bone)

# the LINEAR "Spin" action: frame 0 = rest identity, frame N = <degrees> about each wheel's local Y (the axle)
arm.animation_data_create()
act = bpy.data.actions.new("Spin")
arm.animation_data.action = act
try:
    if getattr(act, "slots", None):
        arm.animation_data.action_slot = act.slots[0]   # Blender 5.x slotted actions
except Exception:
    pass
bpy.context.scene.frame_start = 0
# the clip must cover the SLOWEST authored motion — a wave cycle is typically far longer than a wheel loop
bpy.context.scene.frame_end = max(frames, rock_frames) if rock_on else frames

# WAVE ROCK keys on the Hull bone: roll = A·sin(2πt) about X (longitudinal), pitch = 0.4A·sin(4πt) about Y.
# Both vanish at t=0 and t=1, so frame 0 IS the rest pose (bind==frame0) and the loop restart is seamless.
# Sampled densely (24 steps) because the pipeline keys LINEAR — a sparse sine would read as a triangle wave.
if rock_on:
    # roll about the hull's LENGTH axis; pitch (bow up/down) about the other horizontal axis
    if rock_axis_arg in ("X", "Y"):
        _roll_i = 0 if rock_axis_arg == "X" else 1
        _axis_why = "forced " + rock_axis_arg
    else:
        _rmn = Vector((1e18, 1e18, 1e18)); _rmx = Vector((-1e18, -1e18, -1e18))
        for _ro in objs:
            _rc, _rs = world_bbox(_ro)
            for _k in range(3):
                _rmn[_k] = min(_rmn[_k], _rc[_k] - _rs[_k] / 2.0)
                _rmx[_k] = max(_rmx[_k], _rc[_k] + _rs[_k] / 2.0)
        _span = _rmx - _rmn
        _roll_i = 0 if _span.x >= _span.y else 1
        _axis_why = "auto (%s longer: %.2f vs %.2f)" % ("XYZ"[_roll_i], max(_span.x, _span.y), min(_span.x, _span.y))
    # Build the two swing axes as VECTORS (not euler components) so the heading offset can point them anywhere in
    # the horizontal plane: roll about the hull's length, pitch about the perpendicular. Composed as quaternions and
    # written back as euler, keeping the keying identical to the rest of the rig.
    _roll_ax = Vector((0.0, 0.0, 0.0)); _roll_ax[_roll_i] = 1.0
    if abs(rock_heading) > 1e-6:
        _roll_ax.rotate(Quaternion(Vector((0.0, 0.0, 1.0)), math.radians(rock_heading)))
    _pitch_ax = Vector((0.0, 0.0, 1.0)).cross(_roll_ax).normalized()
    _pb_hull = arm.pose.bones[body_bone]
    _pb_hull.rotation_mode = 'XYZ'
    # sample density scales with the pitch frequency — the fastest component needs the keys, and the pipeline
    # keys LINEAR (a sparse sine reads as a triangle wave)
    _steps = max(24, 16 * max(rock_roll_cycles, rock_pitch_cycles))   # keys enough for the FASTEST wave (LINEAR keying)
    _roll_amp = math.radians(rock_deg)
    _pitch_amp = math.radians(rock_pitch_deg)
    _phase = math.radians(rock_pitch_phase)
    for _i in range(_steps + 1):
        _t = _i / float(_steps)
        _f = int(round(_t * rock_frames))
        bpy.context.scene.frame_set(_f)
        _q = (Quaternion(_roll_ax, _roll_amp * math.sin(2.0 * math.pi * rock_roll_cycles * _t)) @
              Quaternion(_pitch_ax, _pitch_amp * math.sin(2.0 * math.pi * rock_pitch_cycles * _t + _phase)))
        _pb_hull.rotation_euler = _q.to_euler('XYZ')
        _pb_hull.keyframe_insert("rotation_euler", frame=_f)
    print("VEHICLE wave rock: roll %.1f deg x%d about (%.2f,%.2f,%.2f) | pitch %.1f deg x%d phase %.0f about (%.2f,%.2f,%.2f) | %d frames, %d keys on '%s' — %s%s"
          % (rock_deg, rock_roll_cycles, _roll_ax.x, _roll_ax.y, _roll_ax.z,
             rock_pitch_deg, rock_pitch_cycles, rock_pitch_phase, _pitch_ax.x, _pitch_ax.y, _pitch_ax.z,
             rock_frames, _steps + 1, body_bone, _axis_why,
             (", heading %+.0f deg" % rock_heading) if abs(rock_heading) > 1e-6 else ""))


# ROLLING-CONTACT wheel speeds (user field report: the small road wheels looked draggy — "they should be
# turning faster compared to the big wheel"): every wheel rolls on the same tread/ground, so angular speed
# scales as 1/diameter. The LARGEST wheel (drive sprocket) keeps the user's <degrees> exactly. To keep the
# loop restart invisible on the faster wheels, each wheel's SPOKE SYMMETRY is detected from its own mesh
# (angular autocorrelation of rim verts around the axle) and its rotation snapped to the nearest multiple of
# its symmetry angle.
def _wheel_symmetry(_bname):
    _o2 = bpy.data.objects.get("Mesh_" + _bname)
    if _o2 is None or _o2.type != 'MESH' or not _o2.data.vertices:
        return 0
    _db2 = arm.data.bones[_bname]
    _ctr = (arm.matrix_world @ _db2.matrix_local).translation
    _ax = ((arm.matrix_world @ _db2.matrix_local).to_3x3() @ Vector((0, 1, 0))).normalized()
    _u = _ax.cross(Vector((0, 0, 1)))
    if _u.length < 1e-3:
        _u = _ax.cross(Vector((1, 0, 0)))
    _u.normalize()
    _w2 = _ax.cross(_u)
    _vs2 = _o2.data.vertices
    _stp = max(1, len(_vs2) // 1500)
    _pts2 = []
    _rmax = 0.0
    _tmp = []
    for _i2 in range(0, len(_vs2), _stp):
        _rel = (_o2.matrix_world @ _vs2[_i2].co) - _ctr
        _xr, _yr = _rel.dot(_u), _rel.dot(_w2)
        _rr = math.hypot(_xr, _yr)
        _rmax = max(_rmax, _rr)
        _tmp.append((math.atan2(_yr, _xr), _rr))
    _pts2 = [_t2 for _t2, _r2 in _tmp if _r2 > 0.45 * _rmax]
    if len(_pts2) < 24:
        return 0
    _bestn, _bestR = 0, 0.0
    _scoresn = []
    for _n in range(2, 25):
        _sr2 = sum(math.cos(_n * _t2) for _t2 in _pts2)
        _si2 = sum(math.sin(_n * _t2) for _t2 in _pts2)
        _Rn = math.hypot(_sr2, _si2) / len(_pts2)
        _scoresn.append((_n, _Rn))
        _bestR = max(_bestR, _Rn)
    if _bestR < 0.4:
        return 0
    # the FUNDAMENTAL symmetry = the smallest strong n (higher strong n's are its harmonics — rotating by
    # THEIR step does not map the pattern onto itself)
    for _n, _Rn in _scoresn:
        if _Rn >= 0.8 * _bestR:
            return _n
    return 0


_dd_ref = max((cl["m"] for cl in clusters), default=0.0)
# WRAP CARRIERS (sprocket + idler) are NOT ground-rollers: they carry the belt wrapping them at nearly the
# SAME band radius (the track stands far off the small idler's rim), so a wrap-driven wheel turns at the
# wrap's angular speed — near-identical for both. Rim-ratio scaling made the idler visibly outpace the
# sprocket (user field report: "front upper and back upper wheel don't turn at the same speed"). Both get
# the user's degrees; only true ground-rollers get rolling-contact scaling.
_wrap_carrier_idx = set()
_idler_idx = set()
for _ti3 in track_infos:
    for _cl3 in (_ti3[2], _ti3[3]):
        if _cl3 is not None and _cl3 in clusters:
            _wrap_carrier_idx.add(clusters.index(_cl3))
            if _cl3 is _ti3[3]:   # the REAR wrap carrier = the idler (user's per-wheel speed dial)
                _idler_idx.add(clusters.index(_cl3))
# reference surface speed for ground wheels/rollers: with a tread, the wheels visually roll AGAINST THE
# BELT — surface speed = the belt's advance per loop (BELT-CONTINUITY; the old drive-wheel rim ratio is
# only right for belt-less vehicles, where the shared surface is the ground).
_belt_adv = 0.0
for _j5 in _link_jobs.values():
    _belt_adv = max(_belt_adv, _j5["adv_cells"] * _j5["cellL"])
# STATIC-TRACKS fallback (2026-07-27, the returning idler skip): with the tread pipeline skipped there are no
# track_infos, so the sprocket/idler were never classified — the idler fell into the rolling-contact branch,
# ran at drive-wheel speed and popped once per loop again. The classification is purely GEOMETRIC (frontmost/
# rearmost wheel cluster per side — same rule the tread path uses), so restore it belt-free. Gated to vehicles
# that actually HAVE (static) tracks: a wheeled car's front/rear wheels are ordinary rollers, not carriers.
if had_static_tracks and not _wrap_carrier_idx and clusters:
    for _side_pos in (True, False):
        _scls = [cl for cl in clusters if (cl["c"].y >= 0) == _side_pos]
        if not _scls:
            continue
        _fcl = max(_scls, key=lambda cl: cl["c"].x)
        _rcl = min(_scls, key=lambda cl: cl["c"].x)
        _wrap_carrier_idx.add(clusters.index(_fcl))
        _wrap_carrier_idx.add(clusters.index(_rcl))
        _idler_idx.add(clusters.index(_rcl))
    print("VEHICLE wrap carriers classified geometrically (static tracks): %d carrier(s), %d idler(s)"
          % (len(_wrap_carrier_idx), len(_idler_idx)))
_wheel_final_deg = {}
for _bi2, bname in enumerate(cluster_bones):
    _deg_i = degrees
    if _bi2 in _idler_idx:
        # AUTOMATIC pop-free idler speed (field-proven with the manual dial first, then automated at the
        # user's request): the idler's spoke ring only maps onto itself at multiples of its OWN symmetry
        # step — at the sprocket-matched speed it restarted mid-pattern and visibly jerked once per loop.
        # Target the sprocket's degrees, then ALWAYS snap to the idler's nearest grid point (Jagdpanzer:
        # 14-fold, 60 -> 51.4, ~14% slower — the quantization price of a jerk-free loop).
        _deg_i = degrees * idler_speed_mul
        _nsym = _wheel_symmetry(bname)
        if _nsym > 0:
            _stepd = 360.0 / _nsym
            _snap = round(_deg_i / _stepd) * _stepd
            if abs(_snap) < 1e-6:
                _snap = _stepd * (1.0 if _deg_i >= 0 else -1.0)
            print("VEHICLE idler %s: symmetry %d-fold (%.1f deg steps), auto pop-free %.1f deg (target %.1f)"
                  % (bname, _nsym, _stepd, _snap, _deg_i))
            _deg_i = _snap
    elif (_dd_ref > 1e-6 and _bi2 < len(clusters) and clusters[_bi2].get("m", 0.0) > 1e-6
            and _bi2 not in _wrap_carrier_idx and not clusters[_bi2].get("is_rotor")):
        # (ROTORS excluded: rolling-contact size-scaling makes a smaller wheel spin faster to match ground speed —
        #  right for wheels, wrong for a rotor. Rotors keep the base `degrees`, so main + tail spin at the SAME rate.)
        _r_i = clusters[_bi2]["m"] * 0.5
        if _belt_adv > 1e-6:
            _deg_i = math.degrees(_belt_adv / _r_i) * (1.0 if degrees >= 0 else -1.0)
        else:
            _deg_i = degrees * (_dd_ref / clusters[_bi2]["m"])
        _deg_i *= road_speed_mul
        if abs(road_speed_mul - 1.0) < 1e-6:   # snapping would eat small dial changes — only snap at x1.0
            _nsym = _wheel_symmetry(bname)
            if _nsym > 0:
                _stepd = 360.0 / _nsym
                _snap = round(_deg_i / _stepd) * _stepd
                if abs(_snap) < 1e-6:
                    _snap = _stepd * (1.0 if _deg_i >= 0 else -1.0)
                _deg_i = _snap
    _wheel_final_deg[bname] = _deg_i
for bname in cluster_bones:
    pb = arm.pose.bones[bname]
    _ci = int(bname.split("_")[1])
    if _ci < len(clusters) and clusters[_ci].get("is_rotor"):
        # ROTOR own-clip spin: the bone frame now embeds the axle (main: local Y = axle; tail: local X = axle,
        # matching the donor channels' axes), so spin about that LOCAL axis. Keyed EVERY frame — a start/end
        # quaternion pair slerps the short way and cannot represent >180 deg; per-frame keys are exact.
        _tot = math.radians(_wheel_final_deg.get(bname, degrees))
        _lax = Vector((1, 0, 0)) if clusters[_ci].get("is_tail") else Vector((0, 1, 0))
        pb.rotation_mode = 'QUATERNION'
        for _f in range(frames + 1):
            bpy.context.scene.frame_set(_f)
            pb.rotation_quaternion = Quaternion(_lax, _tot * (_f / float(frames)))
            pb.keyframe_insert("rotation_quaternion", frame=_f)
        continue
    pb.rotation_mode = 'XYZ'
    bpy.context.scene.frame_set(0)
    pb.rotation_euler = (0, 0, 0)
    pb.keyframe_insert("rotation_euler", frame=0)
    bpy.context.scene.frame_set(frames)
    pb.rotation_euler = (0, math.radians(_wheel_final_deg.get(bname, degrees)), 0)   # local Y = the axle
    pb.keyframe_insert("rotation_euler", frame=frames)
if _wheel_final_deg:
    _chg = {b: d for b, d in _wheel_final_deg.items() if abs(d - degrees) > 0.5}
    if _chg:
        print("VEHICLE rolling-contact speeds (largest wheel keeps %.0f deg): %s"
              % (degrees, ", ".join("%s=%.1f" % (b, d) for b, d in sorted(_chg.items()))))

# TREAD CONVEYOR v2: bottom run slides opposite the roll, top run WITH it, both by one drive-wheel surface
# distance per loop (the wrap arcs need no keys — they're skinned to the rotating sprocket/idler bones). Use
# small Spin degrees (~one sprocket tooth, 30°) so the advance ≈ one link pitch and the loop snap is invisible.
if track_infos and clusters:
    _drive_d = max(cl["m"] for cl in clusters)                     # largest wheel = drive sprocket diameter
    # The tread system runs its OWN quantum, decoupled from the visible wheels (fold-finder verdict: the 60 deg
    # wheel quantum gave a 0.42 advance that OVERSHOT the ~0.34 front ramp — panels folded inside-out). Wheels
    # keep the user's spoke-symmetric <degrees>; wraps+shuttles run a third of it, so the advance stays inside
    # every ramp span and the loop-restart tread snap shrinks to ~a link pitch.
    _conv_deg = degrees / 3.0
    _advance = math.pi * _drive_d * (abs(_conv_deg) / 360.0)
    _flow = 1.0 if degrees >= 0 else -1.0                          # circulation sense follows the roll direction
    for _tn, _tnames, _fcl, _rcl, _tc, _rfcl, _rrcl in track_infos:
        _botb, _topb, _rampfb, _ramprb, _ramprtb, _wrapfb, _wraprb, _wrapgfb, _wrapgrb = _tnames
        if _tn in _link_jobs:
            # PATH-INSTANCED LINKS: key every link bone riding the ring path, one cell period per loop —
            # restart maps link-onto-link exactly. s increases CCW (bottom of the ring runs +X), so a
            # forward-rolling tread (degrees>0, bottom must run -X) advances with NEGATIVE s.
            _job = _link_jobs[_tn]
            _adv_link = -_job["adv_cells"] * _job["cellL"] * (1.0 if degrees >= 0 else -1.0)
            _restM, _P0s, _t0s = {}, {}, {}
            for _ci in _job["cells"]:
                _bn = "%sL%02d" % (_job["prefix"], _ci)
                pb = arm.pose.bones[_bn]
                pb.rotation_mode = 'XYZ'
                _restM[_ci] = arm.data.bones[_bn].matrix_local.copy()
                _P0s[_ci], _t0s[_ci] = _path_eval(_job, _job["s_rest"][_ci])
            for _f in range(frames + 1):
                bpy.context.scene.frame_set(_f)
                for _ci in _job["cells"]:
                    _bn = "%sL%02d" % (_job["prefix"], _ci)
                    pb = arm.pose.bones[_bn]
                    _P1, _t1 = _path_eval(_job, _job["s_rest"][_ci] + _adv_link * _f / frames)
                    _q = _t0s[_ci].rotation_difference(_t1)
                    _M = Matrix.Translation(_P1) @ _q.to_matrix().to_4x4() @ Matrix.Translation(-_P0s[_ci])
                    pb.matrix = _M @ _restM[_ci]
                    pb.keyframe_insert("location", frame=_f)
                    pb.keyframe_insert("rotation_euler", frame=_f)
            # v2 shuttles: each straight run's bone translates by the SAME advance along the run direction
            # (linear keys; restart maps the molded pattern like every other carrier)
            for _ri, _run in enumerate(_job["runs"]):
                _bn = "%sS%02d" % (_job["prefix"], _ri)
                _smid = _job["s_rest"][_run[len(_run) // 2]]
                _Pm, _tm = _path_eval(_job, _smid)
                _world_vec = _tm * _adv_link
                pb = arm.pose.bones[_bn]
                db = arm.data.bones[_bn]
                _local = ((arm.matrix_world @ db.matrix_local).to_3x3().inverted() @ _world_vec)
                pb.rotation_mode = 'XYZ'
                bpy.context.scene.frame_set(0)
                pb.location = (0.0, 0.0, 0.0)
                pb.keyframe_insert("location", frame=0)
                bpy.context.scene.frame_set(frames)
                pb.location = _local
                pb.keyframe_insert("location", frame=frames)
            print("VEHICLE tread '%s' HYBRID conveyor: %d link bones on wraps + %d shuttle(s), advance %.3f/loop"
                  % (_tn, len(_job["cells"]), len(_job["runs"]), abs(_adv_link)))
            continue
        # snap the advance to ONE MEASURED LINK PITCH when plausible — the loop restart then maps the tread
        # pattern onto itself (invisible), instead of jerking by a fraction of a link every loop
        _p_meas = _link_pitch.get(_tn, 0.0)
        _adv = _p_meas if 0.04 <= _p_meas <= 0.3 else _advance
        print("VEHICLE tread '%s' advance: %.3f/loop (%s)" % (_tn, _adv,
              "= measured link pitch" if _adv == _p_meas else "quantum fallback, pitch %.3f rejected" % _p_meas))
        # wrap bones rotate so the surface AT THE MEASURED TREAD-BAND RADIUS moves exactly one conveyor
        # advance per loop (rim-based speed match ran wraps 20-60% fast — road wheels/idler stand well off
        # their rims). theta = advance / band_radius.
        for _wbn, _wcl in ((_wrapfb, _fcl), (_wraprb, _rcl), (_wrapgfb, _rfcl), (_wrapgrb, _rrcl)):
            _R = _band_rot.get(_wbn, 0.0)
            if _R <= 1e-6:
                _d_own = _wcl["m"] if (_wcl is not None and _wcl.get("m", 0.0) > 1e-6) else _drive_d
                _R = _d_own * 0.5
            _theta = math.degrees(_adv / _R) * (1.0 if degrees >= 0 else -1.0)
            pb = arm.pose.bones[_wbn]
            pb.rotation_mode = 'XYZ'
            bpy.context.scene.frame_set(0)
            pb.rotation_euler = (0, 0, 0)
            pb.keyframe_insert("rotation_euler", frame=0)
            bpy.context.scene.frame_set(frames)
            pb.rotation_euler = (0, math.radians(_theta), 0)
            pb.keyframe_insert("rotation_euler", frame=frames)
        _fdir, _rdir, _rtdir = _tread_dirs.get(_tn, (Vector((-1, 0, 0)), Vector((-1, 0, 0)), Vector((1, 0, 0))))
        _moves = ((_botb, Vector((-1.0, 0.0, 0.0)) * _flow),       # bottom runs backward
                  (_topb, Vector((1.0, 0.0, 0.0)) * _flow),        # top runs forward
                  (_rampfb, _fdir * _flow),                        # front ramp: sprocket -> first road wheel
                  (_ramprb, _rdir * _flow),                        # rear ramp: last road wheel -> idler
                  (_ramprtb, _rtdir * _flow))                      # upper-rear: idler -> rearmost roller (forward)
        for _bname, _dir in _moves:
            pb = arm.pose.bones[_bname]
            db = arm.data.bones[_bname]
            _local = ((arm.matrix_world @ db.matrix_local).to_3x3().inverted() @ _dir).normalized() * _adv
            pb.rotation_mode = 'XYZ'
            bpy.context.scene.frame_set(0)
            pb.location = (0.0, 0.0, 0.0)
            pb.keyframe_insert("location", frame=0)
            bpy.context.scene.frame_set(frames)
            pb.location = _local
            pb.keyframe_insert("location", frame=frames)
    print("VEHICLE tread conveyor v5: %d tread(s), tread quantum %.1f deg (wheels %.1f), advance %.3f/loop (drive d=%.2f): wraps ride DEDICATED wrap bones, ramps slide their slope, straights shuttle"
          % (len(track_infos), _conv_deg, degrees, _advance, _drive_d))
# Blender 5.x REMOVED Action.fcurves (slotted/layered actions): curves live under layers->strips->channelbags.
try:
    _fcs = list(act.fcurves)
except AttributeError:
    _fcs = [fc for layer in act.layers for strip in layer.strips
            for cb in strip.channelbags for fc in cb.fcurves]
for fc in _fcs:
    for kp in fc.keyframe_points:
        kp.interpolation = 'LINEAR'

# Strip source-file leftovers before export: a game-rip FBX (SKM_ prefix = skeletal mesh) carries its OWN
# skeleton and helper objects (icospheres etc.). They ride into the export, spam weightless-vertex warnings
# on import, and a second armature can confuse the animated bake's rig conversion. Keep only our rig + the
# meshes we skinned.
keep = set(objs); keep.add(arm)
for o in list(bpy.data.objects):   # bpy.data, not scene.objects — helpers can lurk outside the scene collection
    if o not in keep:
        bpy.data.objects.remove(o, do_unlink=True)

# PURGE THE SOURCE'S OWN ACTIONS before export (canoe finding 2026-07-31): the raw model's take (the canoe's
# "Take 001") rides along into the GLB even though nothing here plays it — the meshes are skinned to OUR armature
# now. It then shows up in the Factory's clip picker beside 'Spin', and picking it bakes a model that never moves.
# The rig authors exactly one clip; anything else in the file is a leftover.
# THE DEPLOY CLIP — the trails swinging open, authored as its OWN action so the state machine can use it four
# ways: Deploy (unfold, after-move), Deploy[N..0] (fold, pre-move), Deploy[N..N] (the deployed stance) and
# Deploy as the Idle/reference clip — which Law 2 requires to be real motion, not a held frame.
# Each arm rotates about the WORLD VERTICAL at its own hinge. The direction is not assumed: it is CHOSEN by
# testing which way moves the spade AWAY from the model's centreline, so left and right open outward whatever
# way the source happens to face. Keyed as a quaternion about the exact local axis that maps to world up —
# arbitrary-axis local rotations are safe on THIS rig (every scale is 1 and the chain is clean); they are not on
# a deploy-converted one, which is why that route could never carry an authored motion (HAF Animation-Pitfalls).
#
# THE GUN RIDES THE DEPLOY CLIP TOO (2026-08-22). A towed gun travels with its tube clamped level over the closed
# trails and only comes up to firing elevation once the trails are planted — so the raise belongs in the SAME clip
# as the spread, on the same 0..N frames, and every use the state machine already makes of Deploy comes along free:
# unfold raises, Deploy[N..0] lowers it back onto the travel lock before the unit rolls, Deploy[N..N] holds it up as
# the deployed stance. This is what the hand-converted M114 did with deployReadyFrame — it just had no choice,
# because a converted rig cannot carry authored bone motion; here it is a deliberate 2-line key.
# It does NOT replace HAF's runtime gunElevMax: that writes a BoneRotation slot, a channel the clip pose never
# touches, so the two COMPOSE — the clip sets the base firing elevation, the runtime adds the per-shot,
# distance-proportional lift on top. Set both, and dial gunElevMax against the raised base, not against level.
if trail_bones and trail_spread > 0.01:
    _dep = bpy.data.actions.new("Deploy")
    arm.animation_data.action = _dep
    try:
        if getattr(_dep, "slots", None):
            arm.animation_data.action_slot = _dep.slots.new(id_type='OBJECT', name=arm.name)
    except Exception:
        pass
    # THE SIGN TEST AND ITS LIMIT (measured 2026-08-22, headless drill on the M114 at 13 yaw angles).
    # Each arm's swing direction is chosen by asking which way moves the spade FARTHER FROM x = 0. That is the
    # right question only while the model's mirror plane is the x = 0 plane — i.e. while the gun is square to an
    # axis, which is what the Orientation dials exist to achieve and what every shipped rig has.
    #   * verified correct on the M114 at yaw 0 / 90 / 180: the two trails get OPPOSITE signs and open to a ~102
    #     unit spread (they ship folded ~7 units apart).
    #   * verified WRONG at every yaw in between: both arms get the SAME sign and swing together, collapsing the
    #     spread to ~12 units. Off-axis the test stops measuring distance-from-the-centreline and starts measuring
    #     the arm's foreshortening in x.
    # Two rewrites were tried against that harness — referencing the other spade, and a centreline derived from
    # the hinge/spade centroids — and BOTH were worse, because off-axis the real fault is upstream: the arm's ends
    # come from the dominant AXIS-ALIGNED bbox extent (see the trail_bones loop), which mis-picks the ends of a
    # diagonal arm. Fixing the sign alone cannot fix that, so the axis assumption STAYS and is stated instead of
    # implied — plus the cross-check below, so a mis-rig is loud rather than silent.
    # (The old comment here claimed "the model is recentred on its own centreline before this point". Nothing in
    # this script recentres anything; the drill measured the rotated model's centre at x = -6.35.)
    _centre_x = 0.0
    _opened = {}
    _signs = []
    for _bn, _hinge, _spade in trail_bones:
        _db = arm.data.bones.get(_bn); _pb = arm.pose.bones.get(_bn)
        if _db is None or _pb is None:
            continue
        _up = Vector((0.0, 0.0, 1.0))
        _arm_v = _spade - _hinge
        _test = _arm_v.copy(); _test.rotate(Quaternion(_up, math.radians(5.0)))
        _sign = 1.0 if abs((_hinge + _test).x - _centre_x) > abs(_spade.x - _centre_x) else -1.0
        _m3 = (arm.matrix_world @ _db.matrix_local).to_3x3()
        _local_up = (_m3.inverted() @ _up).normalized()
        _pb.rotation_mode = 'QUATERNION'
        _pb.rotation_quaternion = Quaternion((1.0, 0.0, 0.0, 0.0))
        _pb.keyframe_insert('rotation_quaternion', frame=0)
        _pb.rotation_quaternion = Quaternion(_local_up, math.radians(trail_spread) * _sign)
        _pb.keyframe_insert('rotation_quaternion', frame=trail_frames)
        deployed_pose[_bn] = _pb.rotation_quaternion.copy()   # the DEPLOYED pose, for clips that must hold it
        _opened[_bn] = ("out" if _sign > 0 else "in") + " %.0f deg" % trail_spread
        _signs.append(_sign)
    # ...and the GUN comes up on the same frames. Axis = the world horizontal perpendicular to the tube
    # (barrel × up), so it works whichever way the source model happens to face; the SIGN is chosen the same way
    # the trails choose theirs — by testing which direction actually lifts the muzzle — rather than assumed.
    if gun_deploy_elev != 0.0 and gun_axis is not None:
        _gb = arm.data.bones.get("Gun"); _gpb = arm.pose.bones.get("Gun")
        if _gb is not None and _gpb is not None:
            _trun, _mz = gun_axis
            _bar = (_mz - _trun)
            _ax = _bar.cross(Vector((0.0, 0.0, 1.0)))
            if _ax.length < 1e-4:                     # a tube already pointing straight up has no elevation plane
                _ax = Vector((1.0, 0.0, 0.0))
            _ax.normalize()
            _t2 = _bar.copy(); _t2.rotate(Quaternion(_ax, math.radians(5.0)))
            _gsign = 1.0 if _t2.z > _bar.z else -1.0  # +ve gun_deploy_elev must always RAISE the muzzle
            _gm3 = (arm.matrix_world @ _gb.matrix_local).to_3x3()
            _glocal = (_gm3.inverted() @ _ax).normalized()
            _gpb.rotation_mode = 'QUATERNION'
            _gpb.rotation_quaternion = Quaternion((1.0, 0.0, 0.0, 0.0))
            _gpb.keyframe_insert('rotation_quaternion', frame=0)
            _gpb.rotation_quaternion = Quaternion(_glocal, math.radians(abs(gun_deploy_elev)) * _gsign * (1.0 if gun_deploy_elev > 0 else -1.0))
            _gpb.keyframe_insert('rotation_quaternion', frame=trail_frames)
            deployed_pose["Gun"] = _gpb.rotation_quaternion.copy()
            print("VEHICLE DEPLOY gun: elevate %.1f deg about world (%.2f, %.2f, %.2f) over 0..%d frames"
                  % (gun_deploy_elev, _ax.x, _ax.y, _ax.z, trail_frames))
        else:
            print("VEHICLE DEPLOY gun: SKIPPED — no Gun bone (mark the barrel parts as G to elevate on deploy)")
    try:
        _dfcs = list(_dep.fcurves)
    except AttributeError:
        _dfcs = [fc for l in _dep.layers for s in l.strips for cb in s.channelbags for fc in cb.fcurves]
    for _fc in _dfcs:
        for _kp in _fc.keyframe_points:
            _kp.interpolation = 'LINEAR'
    arm.animation_data.action = act        # 'Spin' stays the active action, as before
    print("VEHICLE DEPLOY clip: %d trail(s) %s over 0..%d frames" % (len(_opened), _opened, trail_frames))
    # CROSS-CHECK (2026-08-22): a split-trail carriage MIRRORS — two arms must swing opposite ways. Same-sign
    # means they swing together: one opens, the other sweeps inward through the carriage and its twin. Until now
    # nothing checked, so an off-axis model produced that silently and only a careful look at the turntable would
    # have caught it. Two trails is the mirrored case; three or more (an unusual carriage) are left alone.
    if len(_signs) == 2 and _signs[0] == _signs[1]:
        print("VEHICLE TRAIL *** WARNING: both trails were given the SAME swing direction (%s) — a split-trail "
              "carriage must MIRROR, so one of these is sweeping INWARD through the other. The sign test assumes "
              "the gun is square to an axis; straighten it with the Orientation dials (yaw to a multiple of 90) "
              "and re-generate, then check the Deploy clip on the turntable."
              % ("out" if _signs[0] > 0 else "in"))

# THE RECOIL CLIP — the tube kicks back and rides forward again, authored as its own action so it can be assigned
# to the Attack role and fire on a bombard without disturbing Spin or Deploy.
# WHAT SELLS IT IS THE ASYMMETRY, not the distance: a real gun slams back in a few hundredths of a second and the
# recuperator eases it forward over the best part of a second. So the kick is a fixed ~15% of the clip and the
# return gets the rest, derived rather than given its own dial — one number to turn, and it cannot be set to a
# shape that reads wrong.
# This is a TRANSLATION, and the engine's clip bake discards per-bone translation unless the entry ticks
# `keepTranslations`. That flag is the whole reason this can be an honest slide instead of the far-pivot rotation
# trick `deploy_convert` had to use — see HAF Animation-Pitfalls, "the engine contract".
if recoil_bone is not None and gun_axis is not None:
    _rec = bpy.data.actions.new("Recoil")
    arm.animation_data.action = _rec
    try:
        if getattr(_rec, "slots", None):
            arm.animation_data.action_slot = _rec.slots.new(id_type='OBJECT', name=arm.name)
    except Exception:
        pass
    _rb = arm.pose.bones.get(recoil_bone)
    _rdb = arm.data.bones.get(recoil_bone)
    if _rb is not None and _rdb is not None:
        _pivot, _axis, _bore_d, _slide, _R = recoil_geom
        _len = (gun_bore[1] - gun_bore[0]).length if gun_bore is not None else _slide
        _back = -_bore_d * _slide                              # AWAY from the muzzle, along the bore (for reporting)
        # ROTATE THE ARM, don't translate the barrel — the arm's rotation is what survives to the GPU.
        # theta = slide / R by construction, so the tube swings exactly `slide` along its own bore. The SIGN is
        # chosen, as the trails' and the gun's are, by testing which way actually moves the muzzle BACKWARD.
        _rm3 = (arm.matrix_world @ _rdb.matrix_local).to_3x3()
        _local_axis = (_rm3.inverted() @ _axis).normalized()
        _theta = _slide / _R
        _test = (_tc - _pivot).copy(); _test.rotate(Quaternion(_axis, _theta))
        _rsign = 1.0 if (_pivot + _test - _tc).dot(_bore_d) < 0 else -1.0   # muzzle must go AWAY from the target
        _kick = max(1, int(round(recoil_frames * 0.15)))
        _rb.rotation_mode = 'QUATERNION'
        _rest_q = Quaternion((1.0, 0.0, 0.0, 0.0))
        _kick_q = Quaternion(_local_axis, _theta * _rsign)
        # LEAD-IN: hold the gun still for `recoil_lead` frames before the kick, so the shot reads as landing AFTER
        # the turn even when the engine starts the clip early. Two keys at the rest pose (0 and the lead) make it a
        # genuine hold rather than a slow drift into the kick.
        _total = recoil_lead + recoil_frames
        _keys = [(0, _rest_q)]
        if recoil_lead > 0:
            _keys.append((recoil_lead, _rest_q))
        _keys += [(recoil_lead + _kick, _kick_q), (_total, _rest_q)]
        for _f, _q in _keys:
            _rb.rotation_quaternion = _q
            _rb.keyframe_insert('rotation_quaternion', frame=_f)
        # HOLD THE DEPLOYED POSE THROUGH THE SHOT — as the PROVEN recoil does (2026-08-22).
        # A role clip poses the whole skeleton: bones it does not key sit at the reference pose, so keying only the
        # barrel fires the gun from its TRAVEL pose (level tube, folded trails). A gun only fires deployed.
        # This was briefly reverted on a misreading of the bind==frame-0 contract — that contract governs the PRIMARY
        # (Idle/reference) clip, which defines the reference pose; a ROLE clip legitimately encodes a non-identity
        # pose against it (Law 2 is the same point from the other side: the stance belongs in a role clip, not the
        # primary). The proven M114 settles it empirically: deploy_convert's make_role writes ABSOLUTE poses with no
        # delta-rebasing, and its 'recoil' role is authored from m_home captured at deploy_end — the deployed pose.
        # Keyed at both ends so it holds flat rather than drifting.
        for _bn3, _q3 in deployed_pose.items():
            _hb = arm.pose.bones.get(_bn3)
            if _hb is None:
                continue
            _hb.rotation_mode = 'QUATERNION'
            _hb.rotation_quaternion = _q3.copy()
            _hb.keyframe_insert('rotation_quaternion', frame=0)
            _hb.keyframe_insert('rotation_quaternion', frame=recoil_lead + recoil_frames)
        try:
            _rfcs = list(_rec.fcurves)
        except AttributeError:
            _rfcs = [fc for l in _rec.layers for s in l.strips for cb in s.channelbags for fc in cb.fcurves]
        for _fc in _rfcs:
            for _kp in _fc.keyframe_points:
                _kp.interpolation = 'LINEAR'
        print("VEHICLE RECOIL clip: '%s' slides %.2f of a %.1f-unit tube (%.2f units) back over %d frame(s), "
              "returns over %d after a %d-frame lead-in, holding the deployed pose (%s) — needs Keep bone translations at bake"
              % (recoil_bone, recoil_dist, _len, _len * recoil_dist, _kick, recoil_frames - _kick, recoil_lead,
                 ", ".join(sorted(deployed_pose)) if deployed_pose else "nothing to hold"))
        # BREECH CLEARANCE — the failure this feature invites, measured here rather than left to be found in-game.
        # Sliding back down a RAISED bore drives the breech down as well as back, so a stroke that is fine on a level
        # gun buries the breech at 45°. This is exactly why real howitzers of this class use variable recoil: a
        # shorter stroke the higher they elevate. Measured at the recoil peak, in the deployed pose, against the
        # lowest static vertex on the model.
        bpy.context.view_layer.update()

        # meshes are merged per bone and named "Mesh_<bone>" by this point — bone_of still holds SOURCE part names
        _statics = [o for o in bpy.data.objects
                    if o.type == 'MESH' and o.data.vertices and o.name != "Mesh_" + recoil_bone]
        if _statics:
            _ground = min(min((o.matrix_world @ _v.co).z for _v in o.data.vertices) for o in _statics)
            _bar_objs = [o for o in bpy.data.objects if o.type == 'MESH' and o.name == "Mesh_" + recoil_bone]
            if _bar_objs:
                # the barrel's own lowest point, deployed and slid fully back
                _dq = deployed_pose.get("Gun")
                _lowest = None
                # Barrel is a CHILD of Gun, so its local slide is rotated by the elevation too — the recoil vector
                # has to go INSIDE the rotation, not after it. (Left outside, this measured a level-bore slide and
                # reported a near-constant clearance whatever the stroke: 9.68 at 0.10 and 8.80 at 0.30, when the
                # truth is +4.86 and -7.03.)
                for _o in _bar_objs:
                    for _v in _o.data.vertices:
                        _p = (_o.matrix_world @ _v.co) - _trun + _back
                        if _dq is not None:
                            _p.rotate(_dq)
                        _p = _p + _trun
                        if _lowest is None or _p.z < _lowest:
                            _lowest = _p.z
                _clear = _lowest - _ground
                if _clear < 0:
                    print("VEHICLE RECOIL *** WARNING: at full recoil the breech reaches Z %.2f, %.2f BELOW the "
                          "model's lowest static point (Z %.2f) — it will punch through the ground. Lower the "
                          "recoil fraction (a real gun shortens its stroke as it elevates, for this exact reason) "
                          "or lower the deploy elevation." % (_lowest, -_clear, _ground))
                else:
                    print("VEHICLE RECOIL breech clearance at full recoil: %.2f above the model's lowest static "
                          "point (Z %.2f)" % (_clear, _ground))
    arm.animation_data.action = act        # 'Spin' stays the active action, as before

for _oa2 in [a for a in bpy.data.actions if a.name not in ("Spin", "Deploy", "Recoil")]:
    print("VEHICLE purged leftover source clip '%s' (only 'Spin'/'Deploy'/'Recoil' are authored here)" % _oa2.name)
    bpy.data.actions.remove(_oa2)
for _o2 in bpy.data.objects:
    if _o2.type != 'ARMATURE' and _o2.animation_data is not None:
        _o2.animation_data_clear()

bpy.ops.export_scene.gltf(filepath=out_glb, export_animations=True)
if preview_fbx:
    bpy.ops.export_scene.fbx(filepath=preview_fbx, add_leaf_bones=False, bake_anim=True)
print("VEHICLE RIG DONE: %d wheel part(s) clustered into %d wheel(s) %s, %d turret part(s) on one Turret bone, %d gun part(s) on one Gun bone%s, %d track loop(s) on own static bones, Spin 0..%d %.0f deg%s -> %s"
      % (len(wheel_names), len(clusters), {b: wheel_axes[b] for b in cluster_bones}, len(turret_names),
         len(gun_names), " (child of Turret)" if (gun_names and turret_names) else "", len(track_names), frames, degrees,
         (", wave rock %.1f deg over %d frames" % (rock_deg, rock_frames)) if rock_on else "", out_glb))
