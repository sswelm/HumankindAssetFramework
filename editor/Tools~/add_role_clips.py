# add_role_clips.py — augment an ALREADY-CONVERTED deploy GLB (a deploy_convert.py output) with the state-machine
# ROLE actions, IN PLACE. For GLBs converted before deploy_convert step 7c existed (the M114): re-running the
# original conversion needs its historical args; this instead samples the baked 'deploy' action the GLB already
# carries — deterministic, no reconstruction. Adds: unfold (after-move), fold (pre-move, reversed), folded (move
# stance), deployed (idle stance), recoil (attack, when a tail exists). The legacy 'deploy' action is untouched
# and stays the active one, so existing legacy bakes are unaffected.
#
#   blender -b -P add_role_clips.py -- <deploy.glb> <deployEndFraction> [outPath]
#     deployEndFraction : where the deploy segment ends, as a fraction of the clip (the registry's deployPoseTime,
#                         e.g. 0.72) — robust against fps remapping on import, unlike an absolute frame.
#     outPath           : omit to write IN PLACE (a .bak copy is made first).
import bpy, sys, os, shutil

argv = sys.argv[sys.argv.index("--") + 1:]
inp = argv[0]
frac = float(argv[1])
outp = argv[2] if len(argv) > 2 and argv[2].strip() else inp
# WHEEL SPIN in the 'folded' (travel) role (2026-08-22, the towed howitzer): argv[3..6] = wheelBonesCsv axis frames degrees.
# The travel stance becomes an N-frame loop in which the named wheel bones roll about their axle (LINEAR, frame 0 =
# the folded rest) — the Lab's Movement clip is then `folded[1..N]`: folded legs + rolling wheels, fully baked.
wheel_names = [w.strip() for w in (argv[3] if len(argv) > 3 else "").split(",") if w.strip()]
wheel_axis = (argv[4] if len(argv) > 4 else "AUTO").strip().upper() or "AUTO"
wheel_frames = int(argv[5]) if len(argv) > 5 and argv[5].strip() else 15
wheel_deg = float(argv[6]) if len(argv) > 6 and argv[6].strip() else -360.0

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=inp)

# purge the Blender glTF importer's bone-shape placeholder (unskinned "Icosphere" mesh, not in the .glb itself)
for _ico in [o for o in bpy.data.objects if o.type == 'MESH' and o.name.startswith('Icosphere') and not o.vertex_groups]:
    print("ROLES purged glTF importer bone-shape artifact: %s" % _ico.name)
    bpy.data.objects.remove(_ico, do_unlink=True)

arm = next((o for o in bpy.data.objects if o.type == 'ARMATURE'), None)
if arm is None:
    print("ROLES ERROR: no armature in %s" % inp); sys.exit(1)
act = bpy.data.actions.get("deploy") \
      or (arm.animation_data.action if arm.animation_data else None) \
      or (bpy.data.actions[0] if len(bpy.data.actions) else None)
if act is None:
    print("ROLES ERROR: no action in %s" % inp); sys.exit(1)
if arm.animation_data is None:
    arm.animation_data_create()

def assign(a):
    arm.animation_data.action = a
    try: arm.animation_data.action_slot = a.slots[0]          # Blender 4.4+/5 slotted actions
    except Exception: pass

assign(act)
fmin, fmax = [int(round(v)) for v in act.frame_range]
deploy_end = int(round(fmin + frac * (fmax - fmin)))
scene = bpy.context.scene
scene.frame_start, scene.frame_end = fmin, fmax
print("ROLES source action '%s' frames %d..%d, deploy segment ends at %d (fraction %.3f)" % (act.name, fmin, fmax, deploy_end, frac))

for pb in arm.pose.bones:
    pb.rotation_mode = 'QUATERNION'

# snapshot the evaluated pose basis per frame (what the original bake keyed)
_snap = {}
for f in range(fmin, fmax + 1):
    scene.frame_set(f)
    bpy.context.view_layer.update()
    _snap[f] = {pb.name: (pb.location.copy(), pb.rotation_quaternion.copy()) for pb in arm.pose.bones}

def make_role(name, frames):
    old = bpy.data.actions.get(name)
    if old is not None:
        bpy.data.actions.remove(old)                          # idempotent re-runs
    a = bpy.data.actions.new(name)
    arm.animation_data.action = a
    try: arm.animation_data.action_slot = a.slots.new(id_type='OBJECT', name=arm.name)
    except Exception: pass
    for i, f in enumerate(frames):
        for pb in arm.pose.bones:
            loc, quat = _snap[f][pb.name]
            pb.location = loc
            pb.rotation_quaternion = quat
            pb.keyframe_insert('location', frame=fmin + i)    # keyed from fmin: stays inside any export frame range
            pb.keyframe_insert('rotation_quaternion', frame=fmin + i)
    print("ROLES made '%s' (%d frames)" % (name, len(frames)))
    return a

# WHEELSONLY (argv[7]): rebuild ONLY 'folded' — a retrofit on a GLB whose other roles/legacy slices are in use
# (the howitzer: legacy deploy[...] slices + a converter-tuned 'recoil' that a plain re-sample would destroy).
wheels_only = len(argv) > 7 and argv[7].strip().lower() == "wheelsonly"
dep = list(range(fmin, deploy_end + 1))
if not wheels_only:
    make_role("unfold", dep)
    make_role("fold", list(reversed(dep)))
def wheel_axle(db, axis_arg):
    """The wheel's axle as a WORLD direction: a forced X/Y/Z, else AUTO = the thinnest extent of the verts skinned
    to this bone (a wheel is thin along its axle). Signed to a common reference so left and right wheels — whose
    own axles point outward, opposite each other — still turn the same way in the world."""
    from mathutils import Vector
    if axis_arg in ("X", "Y", "Z"):
        axle = {"X": Vector((1, 0, 0)), "Y": Vector((0, 1, 0)), "Z": Vector((0, 0, 1))}[axis_arg]
    else:
        pts = []
        for o in bpy.data.objects:
            if o.type != 'MESH' or db.name not in o.vertex_groups: continue
            gi = o.vertex_groups[db.name].index
            for v in o.data.vertices:
                if any(g.group == gi and g.weight > 0.5 for g in v.groups): pts.append(o.matrix_world @ v.co)
        if len(pts) < 8:
            print("ROLES WHEEL '%s': no skinned verts for AUTO axle — assuming X" % db.name)
            axle = Vector((1, 0, 0))
        else:
            ext = [max(p[i] for p in pts) - min(p[i] for p in pts) for i in range(3)]
            axle = [Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))][min(range(3), key=lambda i: ext[i])]
    ref = max(range(3), key=lambda i: abs(axle[i]))
    return axle if axle[ref] >= 0 else -axle

def spin_wheels(action, names, axis_arg, nframes, degrees):
    """Roll each wheel about the LOCAL axis nearest its axle, WITHOUT touching the bone's rest — mirrors
    deploy_convert.py. Never re-orient the bone: a wheel bone with a non-identity rest rotation comes out of
    Amplitude's skeleton bake with a mangled offset (measured: FBX local T=(21.096,0,0) became (-0.00932,0,-0.00466),
    putting one wheel's pivot ~0.93 below its hub — it swept underground), while identity-rest bones like the legs
    bake symmetric and correct."""
    import math
    from mathutils import Vector
    if not names: return
    arm.animation_data.action = action
    spun = {}
    for bn in list(names):
        db = arm.data.bones.get(bn)
        if db is None:
            cands = [b for b in arm.data.bones if bn.lower() in b.name.lower()]
            db = cands[0] if cands else None
        pb = arm.pose.bones.get(db.name) if db else None
        if db is None or pb is None:
            print("ROLES WHEEL ERROR: bone '%s' not found. Bones: %s" % (bn, [b.name for b in arm.data.bones])); continue
        axle = wheel_axle(db, axis_arg)
        m3 = (arm.matrix_world @ db.matrix_local).to_3x3()
        best_i, best_d = 0, 0.0
        for i in range(3):
            v = Vector((0.0, 0.0, 0.0)); v[i] = 1.0
            d = (m3 @ v).normalized().dot(axle)
            if abs(d) > abs(best_d): best_i, best_d = i, d
        sign = 1.0 if best_d >= 0 else -1.0
        pb.rotation_mode = 'XYZ'
        for i in range(nframes + 1):
            eul = [0.0, 0.0, 0.0]
            eul[best_i] = math.radians(degrees) * sign * (i / float(nframes))
            pb.rotation_euler = tuple(eul)
            pb.keyframe_insert('rotation_euler', frame=fmin + i)
        spun[db.name] = ("+" if sign > 0 else "-") + "XYZ"[best_i]
    try: fcs = list(action.fcurves)
    except AttributeError: fcs = [fc for layer in action.layers for strip in layer.strips for cb in strip.channelbags for fc in cb.fcurves]
    for fc in fcs:
        for kp in fc.keyframe_points: kp.interpolation = 'LINEAR'
    print("ROLES WHEEL SPIN in 'folded': %s (rest untouched), %d frames, %.0f deg -> Movement clip = folded[1..%d]"
          % (spun, nframes, degrees, nframes))

if wheel_names:
    folded_act = make_role("folded", [fmin] * (wheel_frames + 1))   # N+1 held frames, then the wheels roll over them
    spin_wheels(folded_act, wheel_names, wheel_axis, wheel_frames, wheel_deg)
else:
    make_role("folded", [fmin, fmin])                             # 2 identical frames: a valid HELD pose
if not wheels_only:
    make_role("deployed", [deploy_end, deploy_end])
    has_recoil = fmax > deploy_end
    if has_recoil:
        make_role("recoil", list(range(deploy_end, fmax + 1)))

assign(act)                                                   # legacy 'deploy' stays the active action

if os.path.abspath(outp) == os.path.abspath(inp):
    shutil.copy2(inp, inp + ".bak")
    print("ROLES backup: %s.bak" % inp)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.gltf(filepath=outp, export_format='GLB', export_animations=True, export_yup=True)
print("ROLES wrote %s with actions: %s" % (outp, sorted(a.name for a in bpy.data.actions)))
