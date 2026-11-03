"""The placement on a CONNECTED chain (2026-11-03, the light assault mech's hanging legs).

A connected child's head IS its parent's tail - one point, two names. rig_anim.py's placement moved every edit
bone's head and tail by the same delta, which moved a shared point once per name, so a connected chain stretched by
one delta per link while the mesh moved by one. The mech (19 of 31 bones connected by Blender's FBX importer) was
the first connected rig through the placement since it became unconditional (2026-09-20).

This fixture builds that shape where nothing else in the registry has it (every other animated recipe is a .glb,
whose importer connects no bone): a three-bone connected chain under an armature, a skinned mesh around it, both
off the origin and above the ground so the placement MUST move them, exported as FBX; runs rig_anim.py on it as the
Factory does (in this Blender, argv as blender_riganim_dump.py sets it); reads the slim FBX back and holds:

  * lengths and heads against the input, at the known clean-unit export scale (never inferred from the output),
  * the mesh centred and grounded at the expected absolute bounds,
  * every parent link and connected joint, with finite coordinates throughout.

Usage: blender -b --python connected_placement_fixture.py -- <rig_anim.py> <work dir> [valid|old-loop|no-placement|double-scale]
Prints FIXTURE rows and a final PASS/FAIL line; exits 1 on FAIL."""
import bpy, os, sys, math
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
rig_anim, work = argv[0], argv[1]
mode = argv[2] if len(argv) > 2 else "valid"
if mode not in ("valid", "old-loop", "no-placement", "double-scale"):
    raise ValueError("unknown connected-placement control: " + mode)
os.makedirs(os.path.join(work, "anim"), exist_ok=True)
src = os.path.join(work, "connected_chain.fbx")
out = os.path.join(work, "anim", "connected_chain_anim.fbx")

# ---- the fixture: a connected chain and a skinned mesh, off the origin ---------------------------------------------
bpy.ops.wm.read_factory_settings(use_empty=True)
OFF = Vector((3.0, -2.0, 1.5))                       # where the artist left it: the placement must move it by -OFF
arm_data = bpy.data.armatures.new("Armature")
arm = bpy.data.objects.new("Armature", arm_data)
bpy.context.collection.objects.link(arm)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='EDIT')
LINKS = [("root", Vector((0, 0, 0)), Vector((0, 0, 1.0))), ("mid", Vector((0, 0, 1.0)), Vector((0, 0.5, 1.8))), ("tip", Vector((0, 0.5, 1.8)), Vector((0.3, 0.5, 2.4)))]
prev = None
for name, h, t in LINKS:
    eb = arm_data.edit_bones.new(name)
    eb.head = OFF + h; eb.tail = OFF + t
    if prev is not None: eb.parent = prev; eb.use_connect = True
    prev = eb
bpy.ops.object.mode_set(mode='OBJECT')
src_len = {n: (t - h).length for n, h, t in LINKS}

mesh = bpy.data.meshes.new("Body")
verts = []; faces = []
for i in range(4):                                   # a box around the chain, 8 verts
    verts.append(OFF + Vector((0.4 * (1 if i & 1 else -1), 0.4 * (1 if i & 2 else -1), -0.2)))
    verts.append(OFF + Vector((0.4 * (1 if i & 1 else -1), 0.4 * (1 if i & 2 else -1), 2.6)))
faces = [(0, 2, 6, 4), (1, 5, 7, 3), (0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6)]
mesh.from_pydata([tuple(v) for v in verts], [], faces); mesh.update()
mesh.materials.append(bpy.data.materials.new("Body"))   # rig_anim.py culls a material-less mesh at import as reference junk
body = bpy.data.objects.new("Body", mesh)
bpy.context.collection.objects.link(body)
body.parent = arm
mod = body.modifiers.new("Armature", 'ARMATURE'); mod.object = arm
for n, _, _ in LINKS: body.vertex_groups.new(name=n)
for v in mesh.vertices:
    g = "root" if v.co.z - OFF.z < 1.0 else "mid"
    body.vertex_groups[g].add([v.index], 1.0, 'REPLACE')
# one keyed frame so rig_anim.py has an action to slim (it rebakes the clip it is named)
arm.animation_data_create()
act = bpy.data.actions.new("ArmatureAction"); arm.animation_data.action = act   # the exporter names the take Armature|Armature|ArmatureAction
bpy.ops.object.mode_set(mode='POSE')
pb = arm.pose.bones["mid"]
for f, ang in ((1, 0.0), (10, 0.3)):
    pb.rotation_quaternion = (math.cos(ang / 2), math.sin(ang / 2), 0, 0)
    pb.keyframe_insert("rotation_quaternion", frame=f)
bpy.ops.object.mode_set(mode='OBJECT')
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.fbx(filepath=src, use_selection=True, add_leaf_bones=False)
src_centre = sum(((arm.matrix_world @ v.co) for v in mesh.vertices), Vector()) / len(mesh.vertices)
print("FIXTURE source: chain of %d connected bones at %s, mesh centre %s" % (len(LINKS), tuple(round(c, 3) for c in OFF), tuple(round(c, 3) for c in src_centre)))

# Confirm the FBX importer really gives the script a connected chain, and retain independent input geometry.
# Reading the expected scale from the OUTPUT root length made uniformly wrong exports pass this test.
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=src)
input_arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
input_bones = {b.name: b for b in input_arm.data.bones}
for i, (name, head, tail) in enumerate(LINKS):
    bone = input_bones[name]
    if i and (bone.parent != input_bones[LINKS[i - 1][0]] or not bone.use_connect):
        raise RuntimeError("the fixture FBX did not import a connected chain")
    if (input_arm.matrix_world @ bone.head_local - (OFF + head)).length > 1e-5:
        raise RuntimeError("the fixture FBX changed the authored head positions")
input_lengths = {name: (input_arm.matrix_world @ bone.tail_local - input_arm.matrix_world @ bone.head_local).length
                 for name, bone in input_bones.items()}
OUTPUT_SCALE = 0.01                                  # argv[8]=1: rig_anim's clean-unit export contract
source_min = Vector(tuple(min(v[k] for v in verts) for k in range(3)))
source_max = Vector(tuple(max(v[k] for v in verts) for k in range(3)))
delta = Vector((-(source_min.x + source_max.x) / 2, -(source_min.y + source_max.y) / 2, -source_min.z))

# ---- rig_anim.py on it, as the Factory runs it (the dump script's argv layout) ----------------------------------------
saved = sys.argv
sys.argv = ["blender", "--", src, out, "20000", "", "Armature|Armature|ArmatureAction", "", "1", "0,0,0", "1", "", "0", "", "0", "", "0"]
rig_rc = 0
try:
    with open(rig_anim, encoding="utf-8") as handle:
        code = handle.read()
    def replace_once(old, new):
        global code
        if code.count(old) != 1: raise RuntimeError("cannot plant the " + mode + " control")
        code = code.replace(old, new)
    if mode == "old-loop":
        replace_once('        _orig = {_eb.name: (_eb.head.copy(), _eb.tail.copy()) for _eb in arm.data.edit_bones}\n'
                     '        for _eb in arm.data.edit_bones:\n'
                     '            _h, _t = _orig[_eb.name]\n'
                     '            _eb.head = _h + _darm; _eb.tail = _t + _darm',
                     '        for _eb in arm.data.edit_bones:\n'
                     '            _eb.head += _darm; _eb.tail += _darm')
    elif mode == "no-placement":
        replace_once('if arm is not None and me.vertices:\n    _mw = joined.matrix_world',
                     'if False and arm is not None and me.vertices:\n    _mw = joined.matrix_world')
    elif mode == "double-scale":
        replace_once('gscale = 0.01 if clean_units else 1.0', 'gscale = 0.02 if clean_units else 1.0')
    exec(compile(code, rig_anim, "exec"), {"__name__": "__main__", "__file__": rig_anim})
except SystemExit as e:                               # rig_anim.py's own error exits (sys.exit(1)); a clean run falls through
    rig_rc = e.code if isinstance(e.code, int) else 1
finally:
    sys.argv = saved
if rig_rc:
    print("FIXTURE FAIL: rig_anim.py exited %d" % rig_rc); sys.exit(1)
if not os.path.exists(out):
    print("FIXTURE FAIL: rig_anim.py wrote no file at %s" % out); sys.exit(1)

# ---- the slim FBX back: fixed-scale absolute expectations, not ratios fitted to this output -----------------------
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=out)
arms = [o for o in bpy.data.objects if o.type == 'ARMATURE']
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
problems = []; checks = 0
def check(label, ok):
    global checks
    checks += 1
    if not ok: problems.append(label)
def finite(v): return all(math.isfinite(c) for c in v)
def close(a, b): return finite(a) and finite(b) and (a - b).length <= 1e-5 * max(OUTPUT_SCALE, b.length)
check("expected one armature and at least one mesh", len(arms) == 1 and bool(meshes))
if len(arms) == 1:
    a = arms[0]
    bones = {b.name.split("_", 1)[-1] if b.name[:1] == "b" and "_" in b.name else b.name: b for b in a.data.bones}
    check("expected exactly the fixture's bones", set(bones) == set(input_lengths))
    for i, (name, head, tail) in enumerate(LINKS):
        if name not in bones: continue
        bone = bones[name]
        hw = a.matrix_world @ bone.head_local; tw = a.matrix_world @ bone.tail_local
        want = (OFF + head + delta) * OUTPUT_SCALE
        check("bone '%s' has a nonfinite or incorrect absolute head (got %s, wanted %s)" % (name, tuple(hw), tuple(want)), close(hw, want))
        length = (tw - hw).length
        # FBX infers a leaf's tail: compare to the imported INPUT length, never an output-derived scale.
        expected_length = input_lengths[name] * OUTPUT_SCALE
        check("bone '%s' changed length (got %.8f, wanted %.8f)" % (name, length, expected_length),
              finite(tw) and math.isfinite(length) and abs(length - expected_length) <= 1e-5 * OUTPUT_SCALE)
        parent = bones.get(LINKS[i - 1][0]) if i else None
        check("bone '%s' has the wrong parent or lost its connected joint" % name,
              bone.parent == parent and (not i or bone.use_connect))
        print("FIXTURE bone %s head %s length %.8f expected %.8f" % (name, tuple(round(c, 6) for c in hw), length, expected_length))
points = [m.matrix_world @ v.co for m in meshes for v in m.data.vertices]
check("missing or nonfinite mesh vertices", bool(points) and all(finite(p) for p in points))
if points and all(finite(p) for p in points):
    lower = Vector(tuple(min(p[k] for p in points) for k in range(3)))
    upper = Vector(tuple(max(p[k] for p in points) for k in range(3)))
    check("mesh lower bounds are not centred and grounded", close(lower, (source_min + delta) * OUTPUT_SCALE))
    check("mesh upper bounds changed placement or scale", close(upper, (source_max + delta) * OUTPUT_SCALE))
    print("FIXTURE mesh bounds %s to %s" % (tuple(round(c, 6) for c in lower), tuple(round(c, 6) for c in upper)))
for p in problems: print("FIXTURE FAIL: " + p)
print("FIXTURE %s: connected-chain placement (%d checks)" % ("FAIL" if problems else "PASS", checks))
sys.exit(1 if problems else 0)
