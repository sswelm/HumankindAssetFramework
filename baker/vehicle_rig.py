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
import bpy, sys, math
from mathutils import Vector

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

def mesh_objects():
    return [o for o in bpy.context.scene.objects if o.type == 'MESH' and len(o.data.vertices) > 0]

def world_bbox(o):
    pts = [o.matrix_world @ Vector(c) for c in o.bound_box]
    mn = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    mx = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return (mn + mx) / 2.0, mx - mn

imp(inp)

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
    objs = mesh_objects()
    if len(objs) == 1:
        # a single combined mesh can't be role-assigned — try splitting into loose parts for the caller
        bpy.context.view_layer.objects.active = objs[0]
        objs[0].select_set(True)
        bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
        bpy.ops.mesh.separate(type='LOOSE'); bpy.ops.object.mode_set(mode='OBJECT')
        objs = mesh_objects()
        print("VEHICLE note: single mesh split into %d loose parts (names are synthetic)" % len(objs))
    for o in objs:
        c, s = world_bbox(o)
        print("PART|%s|%d|%.4f,%.4f,%.4f|%.4f,%.4f,%.4f" % (o.name, len(o.data.vertices), c.x, c.y, c.z, s.x, s.y, s.z))
    # optional argv[2]: export the SPLIT scene as a preview FBX so the Lab can show/zoom/highlight each part by name
    if len(argv) > 2 and argv[2].strip():
        bpy.ops.export_scene.fbx(filepath=argv[2], add_leaf_bones=False, bake_anim=False)
        print("VEHICLE probe preview: %s" % argv[2])
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
axis_arg = argv[6].upper()
frames = max(2, int(argv[7]))
degrees = float(argv[8])

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

# armature: Root at origin + ONE bone per wheel cluster (tail along the axle => local Y IS the axle) + Turret
arm_data = bpy.data.armatures.new("VehicleRig")
arm = bpy.data.objects.new("VehicleRig", arm_data)
bpy.context.scene.collection.objects.link(arm)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='EDIT')
eb_root = arm_data.edit_bones.new("Root")
eb_root.head = (0, 0, 0); eb_root.tail = (0, 0.25, 0)
bone_of = {}
wheel_axes = {}
cluster_bones = []
for i, cl in enumerate(clusters):
    ax = axle_axis(cl["s"])
    eb = arm_data.edit_bones.new("Wheel_%02d" % i)
    eb.head = cl["c"]
    eb.tail = cl["c"] + ax * max(0.05, max(cl["s"]) * 0.25)
    eb.parent = eb_root
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
    eb.parent = eb_root
    eb_turret = eb
    for tn in turret_names:
        bone_of[tn] = "Turret"

# ONE Gun bone for the whole gun assembly (barrel, mantlet, mount) — the natural muzzleBone/socket anchor.
# Parented to the Turret when there is one (the gun must ride the aiming turret); casemate guns (Jagdpanzer)
# hang off Root.
if gun_names:
    gc, gs = _combined_bbox(gun_names)
    eb = arm_data.edit_bones.new("Gun")
    eb.head = gc
    eb.tail = gc + Vector((0, 0, max(0.05, max(gs) * 0.25)))
    eb.parent = eb_turret if eb_turret is not None else eb_root
    for gn in gun_names:
        bone_of[gn] = "Gun"
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
        eb.parent = eb_root
        names.append(eb.name)
    track_infos.append((tn, names, front_cl, rear_cl, c.copy()))
bpy.ops.object.mode_set(mode='OBJECT')

# rigid skinning: each part full-weight on its bone (wheels/turret) or Root (body). TREAD parts skin
# PER-VERTEX into four regions: beyond the sprocket/idler centers -> that WHEEL's bone (the wrap arcs rotate
# with the wheel — no spoke penetration, wrapping for free), else top half -> Top bone, bottom half -> Bot bone.
_track_by_name = {t[0]: t for t in track_infos}
_tread_dirs = {}   # part -> (frontRampFlowDir, rearRampFlowDir) for degrees>0, filled at skinning
for o in objs:
    if o.name in _track_by_name:
        _tn, _tnames, _fcl, _rcl, _tc = _track_by_name[o.name]
        _botb, _topb, _rampfb, _ramprb, _ramprtb = _tnames
        for g in list(o.vertex_groups):
            o.vertex_groups.remove(g)
        # v4 (field-tuned): SIX regions with boundaries at the wheel TANGENT points, where surface velocities
        # naturally match — sprocket/idler wraps rotate with those wheels; the DIAGONAL RAMPS between them and
        # the first/last ROAD wheel slide along their own slope; top/bottom straights shuttle horizontally.
        # (v3's every-wheel carriers created shear boundaries mid-run — reverted.)
        _side_cls = [cl for cl in clusters if (cl["c"].y >= 0) == (_tc.y >= 0)] or clusters
        _sprb = cluster_bones[clusters.index(_fcl)] if _fcl is not None else _botb
        _idlb = cluster_bones[clusters.index(_rcl)] if _rcl is not None else _botb
        _low_cls = [cl for cl in _side_cls if cl["c"].z <= _tc.z and cl is not _fcl and cl is not _rcl]
        _roadF = max(_low_cls, key=lambda cl: cl["c"].x) if _low_cls else _fcl
        _roadR = min(_low_cls, key=lambda cl: cl["c"].x) if _low_cls else _rcl
        # flow directions for degrees>0 (bottom runs backward): front ramp = sprocket -> first road wheel,
        # rear ramp = last road wheel -> idler (continuing the backward+up circulation)
        _fdir = ((_roadF["c"] - _fcl["c"]).normalized() if (_fcl and _roadF and _roadF is not _fcl) else Vector((-1, 0, 0)))
        _rdir = ((_rcl["c"] - _roadR["c"]).normalized() if (_rcl and _roadR and _roadR is not _rcl) else Vector((-1, 0, 0)))
        # UPPER-REAR slope (field finding: "the track runs off at the upper back"): from the idler UP-FORWARD to
        # the rearmost return roller — part of the TOP circulation (flows forward), not the rear ramp's backward.
        _high_cls = [cl for cl in _side_cls if cl["c"].z > _tc.z and cl is not _fcl and cl is not _rcl]
        _rollR = min(_high_cls, key=lambda cl: cl["c"].x) if _high_cls else None
        _rtdir = ((_rollR["c"] - _rcl["c"]).normalized() if (_rcl and _rollR) else Vector((1, 0, 0)))
        _tread_dirs[_tn] = (_fdir, _rdir, _rtdir)
        _names = {_botb, _topb, _rampfb, _ramprb, _ramprtb, _sprb, _idlb}
        _vgs = {n: o.vertex_groups.new(name=n) for n in _names}
        _spr_c, _spr_r = (_fcl["c"], _fcl["m"] * 0.5) if _fcl else (Vector((1e9,) * 3), 0.0)
        _idl_c, _idl_r = (_rcl["c"], _rcl["m"] * 0.5) if _rcl else (Vector((1e9,) * 3), 0.0)

        def _shuttle_region(_p):
            if _roadF is not None and _p.x > _roadF["c"].x and _p.z > _roadF["c"].z:
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
        for _v in o.data.vertices:   # transforms were applied — local == world
            _p = Vector(_v.co)
            _pairs = None
            for _d0, _r0, _wb in (((_p - _spr_c).length, _spr_r, _sprb), ((_p - _idl_c).length, _idl_r, _idlb)):
                if _r0 <= 0.0:
                    continue
                if _d0 <= _r0:
                    _pairs = [(_wb, 1.0)]
                    break
                if _d0 <= _r0 * 1.6:
                    _t = (_d0 - _r0) / (0.6 * _r0)
                    _pairs = [(_wb, 1.0 - _t), (_shuttle_region(_p), _t)]
                    break
            if _pairs is None:
                _pairs = [(_shuttle_region(_p), 1.0)]
            for _gn, _w in _pairs:
                if _w > 0.001:
                    _vgs[_gn].add([_v.index], _w, 'REPLACE')
                    _stats[_gn] = _stats.get(_gn, 0) + 1
        _byg = {k: [0] * v for k, v in _stats.items()}   # counts only, for the report line
        md = o.modifiers.new("Armature", 'ARMATURE'); md.object = arm
        o.parent = arm
        print("VEHICLE tread '%s' skinned: %s" % (o.name, ", ".join("%s=%d" % (g, len(ix)) for g, ix in sorted(_byg.items()))))
        continue
    bname = bone_of.get(o.name, "Root")
    for g in list(o.vertex_groups):
        o.vertex_groups.remove(g)
    vg = o.vertex_groups.new(name=bname)
    vg.add(list(range(len(o.data.vertices))), 1.0, 'REPLACE')
    md = o.modifiers.new("Armature", 'ARMATURE'); md.object = arm
    o.parent = arm

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
        _k = ("__track__" + o.name) if o.name in _track_by_name else bone_of.get(o.name, "Root")
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
bpy.context.scene.frame_end = frames
for bname in cluster_bones:
    pb = arm.pose.bones[bname]
    pb.rotation_mode = 'XYZ'
    bpy.context.scene.frame_set(0)
    pb.rotation_euler = (0, 0, 0)
    pb.keyframe_insert("rotation_euler", frame=0)
    bpy.context.scene.frame_set(frames)
    pb.rotation_euler = (0, math.radians(degrees), 0)   # local Y = the axle (bone tail direction)
    pb.keyframe_insert("rotation_euler", frame=frames)

# TREAD CONVEYOR v2: bottom run slides opposite the roll, top run WITH it, both by one drive-wheel surface
# distance per loop (the wrap arcs need no keys — they're skinned to the rotating sprocket/idler bones). Use
# small Spin degrees (~one sprocket tooth, 30°) so the advance ≈ one link pitch and the loop snap is invisible.
if track_infos and clusters:
    _drive_d = max(cl["m"] for cl in clusters)                     # largest wheel = drive sprocket diameter
    _advance = math.pi * _drive_d * (abs(degrees) / 360.0)
    _flow = 1.0 if degrees >= 0 else -1.0                          # circulation sense follows the roll direction
    for _tn, _tnames, _fcl, _rcl, _tc in track_infos:
        _botb, _topb, _rampfb, _ramprb, _ramprtb = _tnames
        _fdir, _rdir, _rtdir = _tread_dirs.get(_tn, (Vector((-1, 0, 0)), Vector((-1, 0, 0)), Vector((1, 0, 0))))
        _moves = ((_botb, Vector((-1.0, 0.0, 0.0)) * _flow),       # bottom runs backward
                  (_topb, Vector((1.0, 0.0, 0.0)) * _flow),        # top runs forward
                  (_rampfb, _fdir * _flow),                        # front ramp: sprocket -> first road wheel
                  (_ramprb, _rdir * _flow),                        # rear ramp: last road wheel -> idler
                  (_ramprtb, _rtdir * _flow))                      # upper-rear: idler -> rearmost roller (forward)
        for _bname, _dir in _moves:
            pb = arm.pose.bones[_bname]
            db = arm.data.bones[_bname]
            _local = ((arm.matrix_world @ db.matrix_local).to_3x3().inverted() @ _dir).normalized() * _advance
            pb.rotation_mode = 'XYZ'
            bpy.context.scene.frame_set(0)
            pb.location = (0.0, 0.0, 0.0)
            pb.keyframe_insert("location", frame=0)
            bpy.context.scene.frame_set(frames)
            pb.location = _local
            pb.keyframe_insert("location", frame=frames)
    print("VEHICLE tread conveyor v4: %d tread(s), advance %.3f/loop (drive d=%.2f): wraps ride wheels, ramps slide their slope, straights shuttle"
          % (len(track_infos), _advance, _drive_d))
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

bpy.ops.export_scene.gltf(filepath=out_glb, export_animations=True)
if preview_fbx:
    bpy.ops.export_scene.fbx(filepath=preview_fbx, add_leaf_bones=False, bake_anim=True)
print("VEHICLE RIG DONE: %d wheel part(s) clustered into %d wheel(s) %s, %d turret part(s) on one Turret bone, %d gun part(s) on one Gun bone%s, %d track loop(s) on own static bones, Spin 0..%d %.0f deg -> %s"
      % (len(wheel_names), len(clusters), {b: wheel_axes[b] for b in cluster_bones}, len(turret_names),
         len(gun_names), " (child of Turret)" if (gun_names and turret_names) else "", len(track_names), frames, degrees, out_glb))
