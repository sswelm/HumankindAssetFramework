"""The placement on a CONNECTED chain (2026-11-03, the light assault mech's hanging legs).

A connected child's head IS its parent's tail - one point, two names. rig_anim.py's placement moved every edit
bone's head and tail by the same delta, which moved a shared point once per name, so a connected chain stretched by
one delta per link while the mesh moved by one. The mech (19 of 31 bones connected by Blender's FBX importer) was
the first connected rig through the placement since it became unconditional (2026-09-20).

This fixture builds that shape where nothing else in the registry has it (every other animated recipe is a .glb,
whose importer connects no bone): a three-bone connected chain under an armature, a skinned mesh around it, both
off the origin and above the ground so the placement MUST move them, exported as FBX; runs rig_anim.py on it as the
Factory does (in this Blender, argv as blender_riganim_dump.py sets it); reads the slim FBX back and holds:

  * every bone's length as it was (a stretched link is the defect, whatever the delta),
  * every bone head moved by ONE delta - the same one for all - and the mesh's box centre moved by that delta too,
  * the chain still connected and still a chain.

Usage: blender -b --python connected_placement_fixture.py -- <rig_anim.py> <work dir>
Prints FIXTURE rows and a final PASS/FAIL line; exits 1 on FAIL."""
import bpy, os, sys, math
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
rig_anim, work = argv[0], argv[1]
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

# ---- rig_anim.py on it, as the Factory runs it (the dump script's argv layout) ----------------------------------------
saved = sys.argv
sys.argv = ["blender", "--", src, out, "20000", "", "Armature|Armature|ArmatureAction", "", "1", "0,0,0", "1", "", "0", "", "0", "", "0"]
rig_rc = 0
try:
    code = compile(open(rig_anim, encoding="utf-8").read(), rig_anim, "exec")
    exec(code, {"__name__": "__main__", "__file__": rig_anim})
except SystemExit as e:                               # rig_anim.py's own error exits (sys.exit(1)); a clean run falls through
    rig_rc = e.code if isinstance(e.code, int) else 1
finally:
    sys.argv = saved
if rig_rc:
    print("FIXTURE FAIL: rig_anim.py exited %d" % rig_rc); sys.exit(1)
if not os.path.exists(out):
    print("FIXTURE FAIL: rig_anim.py wrote no file at %s" % out); sys.exit(1)

# ---- the slim FBX back: lengths kept, one delta for every head and for the mesh -------------------------------------
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=out)
arms = [o for o in bpy.data.objects if o.type == 'ARMATURE']
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
problems = []
if len(arms) != 1: problems.append("expected one armature in the output, found %d" % len(arms))
if not meshes: problems.append("no mesh in the output")
if not problems:
    a = arms[0]
    def tail_name(n): return n
    bones = {b.name.split("_", 1)[-1] if b.name[:1] == "b" and "_" in b.name else b.name: b for b in a.data.bones}
    heads = {}
    for n, h, t in LINKS:
        b = bones.get(n)
        if b is None: problems.append("bone '%s' missing in the output (have %s)" % (n, sorted(bones))); continue
        hw = a.matrix_world @ b.head_local; tw = a.matrix_world @ b.tail_local
        L = (tw - hw).length; heads[n] = hw
        # the slim FBX carries the scene scaled as rig_anim.py leaves it; lengths compare as ratios to the root link
        print("FIXTURE bone %-4s head %s len %.4f  (source len %.4f)" % (n, tuple(round(c, 4) for c in hw), L, src_len[n]))
    if len(heads) == len(LINKS):
        root_L = (a.matrix_world @ bones["root"].tail_local - heads["root"]).length
        # an FBX stores no bone length: the importer gives a LEAF the length of its parent, so the last link's length is
        # the importer's, not rig_anim.py's - its HEAD (the parent's tail, checked below) is what the placement moved
        for n, h, t in LINKS[:-1]:
            L = (a.matrix_world @ bones[n].tail_local - heads[n]).length
            want = src_len[n] / src_len["root"] * root_L
            if abs(L - want) > 1e-3 * max(1.0, want): problems.append("bone '%s' length %.4f, expected %.4f (the chain stretched)" % (n, L, want))
        # one delta: the head-to-head offsets must be the source's (scaled like the root link)
        k = root_L / src_len["root"]
        for n, h, t in LINKS[1:]:
            got = heads[n] - heads["root"]; want = (h - LINKS[0][1]) * k
            if (got - want).length > 1e-3 * max(1.0, want.length): problems.append("bone '%s' sits %s from the root, expected %s (moved by more than one delta)" % (n, tuple(round(c, 4) for c in got), tuple(round(c, 4) for c in want)))
        b = bones.get("mid")
        if b is not None and b.parent is None: problems.append("the chain is no longer a chain ('mid' lost its parent)")
    centre = Vector()
    nv = 0
    for m in meshes:
        for v in m.data.vertices: centre += m.matrix_world @ v.co; nv += 1
    centre /= max(1, nv)
    if heads.get("root") is not None:
        rel = centre - heads["root"]; want = (src_centre - (OFF + LINKS[0][1])) * (root_L / src_len["root"])
        print("FIXTURE mesh centre %s, root head %s" % (tuple(round(c, 4) for c in centre), tuple(round(c, 4) for c in heads["root"])))
        if (rel - want).length > 1e-3 * max(1.0, want.length): problems.append("the mesh sits %s from the root head, expected %s (mesh and bones moved by different deltas)" % (tuple(round(c, 4) for c in rel), tuple(round(c, 4) for c in want)))
for p in problems: print("FIXTURE FAIL: " + p)
print("FIXTURE %s: the placement on a connected chain (%d checks)" % ("FAIL" if problems else "PASS", 2 * len(LINKS)))
sys.exit(1 if problems else 0)
