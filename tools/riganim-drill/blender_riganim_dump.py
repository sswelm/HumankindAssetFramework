"""What rig_anim.py makes of a model, stage by stage, as Blender has it (tools/riganim_drill.sh).

usage: blender --background --python blender_riganim_dump.py -- <rig_anim.py> <jobs file> <work dir>

A jobs file line is  JOB <key> <input> <argv[2]> ... <argv[14]>, tab-separated (riganim_jobs.py); other lines are ignored. Each job
runs THE SCRIPT ITSELF, its source cut into four stages at four of its own top-level lines and executed in one namespace
(exactly the run the Factory gets, up to the FBX export, which is not run: its bytes only Unity reads), with the scene
written out after each stage. The stages, by the script's own headings:

    1  the import, the junk drop, the contract marker, the rest hold, the object-animation clear
    2  the bone-parent -> skin conversion, the wrapper flatten, the localNodeAnim transplant
    3  the bone cap, the clip resolution and slicing, the action prune, the bone filter, the rest fold and rebake,
       the translation strip and amplify, the frame range
    4  the albedo, the join, the weld, the material slots, the decimate, the rebind, the root collapse, the rename,
       the sockets, the rotation and scale fold, the placement

Rows (tab-separated; every float as the hex of its float32 bits, as the other dumps write them):

    JOB    <key>
    STAGE  <n>                                                        the stage that follows ran to its end
    LOG    <n> <a RIGANIM line the stage printed, in order>
    SCENE  <n> <frame_start> <frame_end> <frame_current> <armature pose_position or ->
    OBJ    <n> <name> <type> <parent or -> <parent_type> <parent_bone or -> <data name or -> <action or -> <slot or ->
              <matrix_world 16> <matrix_parent_inverse 16>
    MESH   <n> <object> <verts> <polys> <loops> <sha of positions> <sha of loop vertex indices> <sha of polygon starts
              and sizes> <sha of material indices> <sha of the active UV layer or -> <materials ;-joined or ->
              <vertex groups ;-joined or -> <modifiers type:name:object ;-joined or ->
    VG     <n> <object> <group> <vertices weighted> <sha of (vertex index, weight) in vertex order>
    BONE   <n> <index> <name> <parent or -> <use_deform> <length> <matrix_local 16>
    PB     <n> <name> <rotation_mode> <location 3> <rotation_quaternion 4> <rotation_euler 3> <scale 3> <matrix_basis 16>
    ACT    <n> <name> <frame_range 2> <fcurves> <slots>
    FC     <n> <action> <data_path> <array_index> <keys> <sha of the keys' co> <sha of handles, interpolation, easing>
    IMG    <n> <path> <bytes> <sha of the file> <width> <height> <sha of the 8-bit RGBA pixels>
    EXIT   <n> <code>                                                 the script stopped in this stage (sys.exit)
    DIES   <n> <exception>                                            the script died in this stage
    DONE   <key>

RIGANIM_KEYS=1 in the environment adds a  K <n> <action> <data_path> <array_index> <frame> <value>  row per key.
"""
import bpy, sys, io, os, struct, hashlib, contextlib, traceback
import numpy as np

sys.stdout.reconfigure(encoding="utf-8")
args = sys.argv[sys.argv.index("--") + 1:]
script, jobs, work = args[0], args[1], args[2]
source = open(script, encoding="utf-8").read()
os.makedirs(work, exist_ok=True)
KEYS = os.environ.get("RIGANIM_KEYS") == "1"

# The stage cuts: the script's own top-level lines, each found exactly once at column 0. A script edit that moves or
# rewords one is a FAIL here, never a silently different cut.
MARKERS = [
    "# BONE-PARENT -> SKIN-WEIGHT CONVERSION (mech finding 2026-07-20)",
    "if not arm.animation_data:\n    arm.animation_data_create()\ndef assign_action(a):",
    "# export the base-colour image as the albedo PNG",
    "bpy.ops.object.select_all(action='SELECT')\n# EXPORT SCALE",
]
cuts = []
for m in MARKERS:
    hits = [i for i in range(len(source)) if source.startswith("\n" + m, i)]
    if len(hits) != 1:
        print("FAIL\t-\tthe stage marker is found %d times in %s: %r" % (len(hits), script, m.split("\n")[0]), flush=True)
        sys.exit(1)
    cuts.append(hits[0] + 1)
if cuts != sorted(cuts):
    print("FAIL\t-\tthe stage markers are out of order in %s" % script, flush=True); sys.exit(1)
bounds = [0] + cuts
chunks = []
for i in range(4):
    text = source[bounds[i]:bounds[i + 1]]
    pad = source[:bounds[i]].count("\n")
    chunks.append(compile("\n" * pad + text, script, "exec"))   # the line numbers of a traceback stay the script's


def h32(v): return struct.pack(">f", float(v)).hex()


def hv(xs): return "\t".join(h32(x) for x in xs)


def hm(m): return "\t".join(h32(m[r][c]) for r in range(4) for c in range(4))


def sha(b): return hashlib.sha256(b).hexdigest()


def f32(a): return np.asarray(a, dtype=np.float32).tobytes()


def fcurves_of(action):
    """Every fcurve of a (Blender 5 slotted) action, with its owning collection - the script's all_fcurve_owners."""
    if getattr(action, "fcurves", None) is not None and len(action.fcurves):
        return list(action.fcurves)
    out = []
    for layer in getattr(action, "layers", []):
        for strip in layer.strips:
            for cb in getattr(strip, "channelbags", []):
                out.extend(list(cb.fcurves))
    return out


def dump(n):
    rows = []
    sc = bpy.context.scene
    arm = next((o for o in sc.objects if o.type == 'ARMATURE'), None)
    rows.append("SCENE\t%d\t%d\t%d\t%d\t%s" % (n, sc.frame_start, sc.frame_end, sc.frame_current, arm.data.pose_position if arm else "-"))
    for o in sc.objects:
        ad = o.animation_data
        act = ad.action.name if (ad and ad.action) else "-"
        slot = "-"
        if ad and ad.action and getattr(ad, "action_slot", None) is not None:
            slot = ad.action_slot.identifier
        rows.append("OBJ\t%d\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s" % (
            n, o.name, o.type, o.parent.name if o.parent else "-", o.parent_type, o.parent_bone or "-",
            o.data.name if o.data else "-", act, slot, hm(o.matrix_world), hm(o.matrix_parent_inverse)))
        if o.type == 'MESH':
            me = o.data
            pos = np.empty(len(me.vertices) * 3, dtype=np.float32); me.vertices.foreach_get("co", pos)
            lv = np.empty(len(me.loops), dtype=np.int32); me.loops.foreach_get("vertex_index", lv)
            ps = np.empty(len(me.polygons), dtype=np.int32); me.polygons.foreach_get("loop_start", ps)
            pt = np.empty(len(me.polygons), dtype=np.int32); me.polygons.foreach_get("loop_total", pt)
            mi = np.empty(len(me.polygons), dtype=np.int32); me.polygons.foreach_get("material_index", mi)
            uv = "-"
            if me.uv_layers.active is not None:
                u = np.empty(len(me.loops) * 2, dtype=np.float32); me.uv_layers.active.data.foreach_get("uv", u); uv = sha(u.tobytes())
            rows.append("MESH\t%d\t%s\t%d\t%d\t%d\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s" % (
                n, o.name, len(me.vertices), len(me.polygons), len(me.loops), sha(pos.tobytes()), sha(lv.astype("<i4").tobytes()),
                sha(ps.astype("<i4").tobytes() + pt.astype("<i4").tobytes()), sha(mi.astype("<i4").tobytes()), uv,
                ";".join(m.name if m else "(none)" for m in me.materials) or "-",
                ";".join(g.name for g in o.vertex_groups) or "-",
                ";".join("%s:%s:%s" % (md.type, md.name, getattr(md, "object", None).name if getattr(md, "object", None) else "-") for md in o.modifiers) or "-"))
            if len(o.vertex_groups):
                per = {g.index: [] for g in o.vertex_groups}
                for v in me.vertices:
                    for ge in v.groups:
                        if ge.group in per:
                            per[ge.group].append((v.index, ge.weight))
                for g in o.vertex_groups:
                    lst = per[g.index]
                    b = b"".join(struct.pack("<If", i, w) for i, w in lst)
                    rows.append("VG\t%d\t%s\t%s\t%d\t%s" % (n, o.name, g.name, len(lst), sha(b)))
    if arm is not None:
        for i, b in enumerate(arm.data.bones):
            rows.append("BONE\t%d\t%d\t%s\t%s\t%d\t%s\t%s" % (n, i, b.name, b.parent.name if b.parent else "-", 1 if b.use_deform else 0, h32(b.length), hm(b.matrix_local)))
        for pb in arm.pose.bones:
            rows.append("PB\t%d\t%s\t%s\t%s\t%s\t%s\t%s\t%s" % (n, pb.name, pb.rotation_mode, hv(pb.location), hv(pb.rotation_quaternion), hv(pb.rotation_euler), hv(pb.scale), hm(pb.matrix_basis)))
    for a in bpy.data.actions:
        fcs = fcurves_of(a)
        rows.append("ACT\t%d\t%s\t%s\t%d\t%d" % (n, a.name, hv(a.frame_range), len(fcs), len(getattr(a, "slots", []) or [])))
        for fc in fcs:
            k = len(fc.keyframe_points)
            co = np.empty(k * 2, dtype=np.float32); fc.keyframe_points.foreach_get("co", co)
            hl = np.empty(k * 2, dtype=np.float32); fc.keyframe_points.foreach_get("handle_left", hl)
            hr = np.empty(k * 2, dtype=np.float32); fc.keyframe_points.foreach_get("handle_right", hr)
            meta = "|".join("%s/%s/%s/%s" % (kp.interpolation, kp.easing, kp.handle_left_type, kp.handle_right_type) for kp in fc.keyframe_points)
            rows.append("FC\t%d\t%s\t%s\t%d\t%d\t%s\t%s" % (n, a.name, fc.data_path, fc.array_index, k, sha(co.tobytes()), sha(hl.tobytes() + hr.tobytes() + meta.encode("utf-8"))))
            if KEYS:
                for j in range(k):
                    rows.append("K\t%d\t%s\t%s\t%d\t%s\t%s" % (n, a.name, fc.data_path, fc.array_index, h32(co[2 * j]), h32(co[2 * j + 1])))
    return rows


def dump_image(n, path):
    if not path or not os.path.isfile(path):
        return []
    b = open(path, "rb").read()
    img = bpy.data.images.load(path)
    try:
        w, h = img.size
        px = np.empty(w * h * img.channels, dtype=np.float32); img.pixels.foreach_get(px)
        q = np.clip(np.rint(px * 255.0), 0, 255).astype(np.uint8)
        return ["IMG\t%d\t%s\t%d\t%s\t%d\t%d\t%d\t%s" % (n, path, len(b), sha(b), w, h, img.channels, sha(q.tobytes()))]
    finally:
        bpy.data.images.remove(img)


fails = 0
for line in open(jobs, encoding="utf-8-sig").read().split("\n"):
    line = line.rstrip("\r")
    if not line.startswith("JOB	"):
        continue
    t = line.split("	")[1:]
    key = t[0]
    print("JOB\t%s" % key, flush=True)
    saved = sys.argv
    try:
        outp = os.path.join(work, key, key + ".fbx").replace("\\", "/")   # never written: the roles' stale clear looks beside it
        os.makedirs(os.path.dirname(outp), exist_ok=True)
        albedo = os.path.join(work, key + "_albedo.png").replace("\\", "/") if t[5] == "1" else ""
        if albedo and os.path.exists(albedo):
            os.remove(albedo)
        argv = ["blender", "--", t[1], outp] + t[2:5] + [albedo] + t[6:]
        sys.argv = argv
        g = {"__name__": "__main__"}
        for n, code in enumerate(chunks, start=1):
            out = io.StringIO(); code_exit = None; died = None
            try:
                with contextlib.redirect_stdout(out):
                    exec(code, g)
            except SystemExit as e:
                code_exit = e.code
            except Exception as e:
                died = "%s: %s" % (type(e).__name__, e)
            for l in out.getvalue().split("\n"):
                if l.startswith("RIGANIM"):
                    print("LOG\t%d\t%s" % (n, l))
            if code_exit is not None:
                print("EXIT\t%d\t%s" % (n, code_exit)); break
            if died is not None:
                print("DIES\t%d\t%s" % (n, died)); break
            print("STAGE\t%d" % n)
            for r in dump(n):
                print(r)
            if n == 4:
                for r in dump_image(n, albedo):
                    print(r)
        print("DONE\t%s" % key, flush=True)
    except Exception as e:
        traceback.print_exc()
        print("FAIL\t%s\t%s: %s" % (key, type(e).__name__, e), flush=True)
        fails += 1
    finally:
        sys.argv = saved
sys.exit(1 if fails else 0)
