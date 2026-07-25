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

if mode == "probe":
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
wheel_names = [n for n in argv[4].split(";") if n.strip()]
turret_names = [n for n in argv[5].split(";") if n.strip()]
axis_arg = argv[6].upper()
frames = max(2, int(argv[7]))
degrees = float(argv[8])

objs = mesh_objects()
if len(objs) == 1 and (wheel_names or turret_names):
    bpy.context.view_layer.objects.active = objs[0]
    objs[0].select_set(True)
    bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.separate(type='LOOSE'); bpy.ops.object.mode_set(mode='OBJECT')
    objs = mesh_objects()

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

def axle_axis(o):
    if axis_arg in ("X", "Y", "Z"):
        return {"X": Vector((1, 0, 0)), "Y": Vector((0, 1, 0)), "Z": Vector((0, 0, 1))}[axis_arg]
    _, s = world_bbox(o)   # AUTO: a wheel is THIN along its axle -> smallest extent
    return [Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))][min(range(3), key=lambda i: s[i])]

# armature: Root at origin + one bone per wheel (tail along the axle => local Y IS the axle) + turret bones
arm_data = bpy.data.armatures.new("VehicleRig")
arm = bpy.data.objects.new("VehicleRig", arm_data)
bpy.context.scene.collection.objects.link(arm)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='EDIT')
eb_root = arm_data.edit_bones.new("Root")
eb_root.head = (0, 0, 0); eb_root.tail = (0, 0.25, 0)
bone_of = {}
wheel_axes = {}
for i, wn in enumerate(wheel_names):
    o = find(wn)
    c, s = world_bbox(o)
    ax = axle_axis(o)
    eb = arm_data.edit_bones.new("Wheel_%02d_%s" % (i, wn[:20]))
    eb.head = c
    eb.tail = c + ax * max(0.05, max(s) * 0.25)
    eb.parent = eb_root
    bone_of[wn] = eb.name
    wheel_axes[wn] = tuple(ax)
# ONE Turret bone shared by every turret part (dome plates, gun shield, barrel...) so the whole assembly is a
# single unit — for future rotation and as the muzzle-socket anchor — placed at the parts' combined bbox center.
if turret_names:
    tos = [find(tn) for tn in turret_names]
    boxes = [world_bbox(o) for o in tos]
    mn = Vector(tuple(min(c[i] - s[i] / 2 for c, s in boxes) for i in range(3)))
    mx = Vector(tuple(max(c[i] + s[i] / 2 for c, s in boxes) for i in range(3)))
    tc, ts = (mn + mx) / 2.0, mx - mn
    eb = arm_data.edit_bones.new("Turret")
    eb.head = tc
    eb.tail = tc + Vector((0, 0, max(0.05, max(ts) * 0.25)))
    eb.parent = eb_root
    for tn in turret_names:
        bone_of[tn] = "Turret"
bpy.ops.object.mode_set(mode='OBJECT')

# rigid skinning: each part full-weight on its bone (wheels/turret) or Root (body)
for o in objs:
    bname = bone_of.get(o.name, "Root")
    for g in list(o.vertex_groups):
        o.vertex_groups.remove(g)
    vg = o.vertex_groups.new(name=bname)
    vg.add(list(range(len(o.data.vertices))), 1.0, 'REPLACE')
    md = o.modifiers.new("Armature", 'ARMATURE'); md.object = arm
    o.parent = arm

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
for wn in wheel_names:
    pb = arm.pose.bones[bone_of[wn]]
    pb.rotation_mode = 'XYZ'
    bpy.context.scene.frame_set(0)
    pb.rotation_euler = (0, 0, 0)
    pb.keyframe_insert("rotation_euler", frame=0)
    bpy.context.scene.frame_set(frames)
    pb.rotation_euler = (0, math.radians(degrees), 0)   # local Y = the axle (bone tail direction)
    pb.keyframe_insert("rotation_euler", frame=frames)
# Blender 5.x REMOVED Action.fcurves (slotted/layered actions): curves live under layers->strips->channelbags.
try:
    _fcs = list(act.fcurves)
except AttributeError:
    _fcs = [fc for layer in act.layers for strip in layer.strips
            for cb in strip.channelbags for fc in cb.fcurves]
for fc in _fcs:
    for kp in fc.keyframe_points:
        kp.interpolation = 'LINEAR'

bpy.ops.export_scene.gltf(filepath=out_glb, export_animations=True)
if preview_fbx:
    bpy.ops.export_scene.fbx(filepath=preview_fbx, add_leaf_bones=False, bake_anim=True)
print("VEHICLE RIG DONE: %d wheel(s) %s, %d turret part(s) on one Turret bone, Spin 0..%d %.0f deg -> %s"
      % (len(wheel_names), {w: wheel_axes[w] for w in wheel_names}, len(turret_names), frames, degrees, out_glb))
