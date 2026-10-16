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

After stage 4 (step (f), the GLB rig route) the scene is exported ONCE as a GLB by Blender's glTF exporter - the export
step of the shipped editor/Tools~/rig_anim_glb.py beside rig_anim.py (the scene's frame range widened to every action's
rounded range, the first frame evaluated as the script's own export pins the pose, `export_scene.gltf(GLB, animations,
frame range, Y up)` as deploy_convert.py calls it; the Factory's Bake Tests row runs the same script as its oracle) - and
the file read back row by row (tools/deploy-drill/glb_rows.py), each row under an X:

    XLOG   <a RIGANIM line of the export step>
    XDIES  <exception>                                                the export died (Blender's exporter raised)
    X      <a glb_rows.py row: GLB, SCENE, NODE, SKIN, IBM, MESH, PRIM, MAT, IMG, TEX, SAMP, ANIM, CHAN>

Rows (tab-separated; every float as the hex of its float32 bits, as the other dumps write them):

    JOB    <key>
    STAGE  <n>                                                        the stage that follows ran to its end
    LOG    <n> <a RIGANIM line the stage printed, in order>
    SCENE  <n> <frame_start> <frame_end> <frame_current> <armature pose_position or ->
    OBJ    <n> <name> <type> <parent or -> <parent_type> <parent_bone or -> <data name or -> <action or -> <slot or ->
              <matrix_world 16> <matrix_parent_inverse 16>
    MESH   <n> <object> <verts> <polys> <loops> <sha of positions> <sha of loop vertex indices> <sha of polygon starts
              and sizes> <sha of material indices> <sha of the active UV layer or -> <materials ;-joined or ->
              <vertex groups ;-joined or -> <modifiers type:name:object ;-joined or -> <every UV layer name:sha ;-joined or ->
    VG     <n> <object> <group> <vertices weighted> <sha of (vertex index, weight) in vertex order>
    BONE   <n> <index> <name> <parent or -> <use_deform> <length> <matrix_local 16>
    PB     <n> <name> <rotation_mode> <location 3> <rotation_quaternion 4> <rotation_euler 3> <scale 3> <matrix_basis 16>
    ACT    <n> <name> <frame_range 2> <fcurves> <slots>
    FC     <n> <action> <data_path> <array_index> <keys> <sha of the keys' co> <sha of handles, interpolation, easing>
    IMG    <n> <path> <bytes> <sha of the file> <width> <height> <sha of the 8-bit RGBA pixels>
    EXIT   <n> <code>                                                 the script stopped in this stage (sys.exit)
    DIES   <n> <exception>                                            the script died in this stage
    DONE   <key>

RIGANIM_KEYS=1 in the environment adds a  K <n> <action> <data_path> <array_index> <frame> <value> <handle_left 2>
<handle_right 2> <interpolation> <easing> <handle_left_type> <handle_right_type>  row per key.
"""
import bpy, sys, io, os, struct, hashlib, contextlib, traceback
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "deploy-drill"))
from glb_rows import rows as glb_rows   # noqa: E402
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


sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import scene_rows   # noqa: E402
from scene_rows import h32, hv, hm, sha, f32, fcurves_of, dump, dump_image   # noqa: E402
scene_rows.KEYS = KEYS


# The export step (step (f)): the shipped editor/Tools~/rig_anim_glb.py beside rig_anim.py - one export, one oracle for the
# drill and for the Factory's Bake Tests row (step (g))
sys.path.insert(0, os.path.dirname(os.path.abspath(script)))
import rig_anim_glb   # noqa: E402


def export_glb(g, path):
    """The X rows: the export's RIGANIM lines, its death, the GLB read back."""
    log, died = rig_anim_glb.export_glb(g, path)
    lines = ["XLOG\t%s" % l for l in log]
    if died is not None:
        return lines + ["XDIES\t%s" % died]
    return lines + ["X\t%s" % r for r in glb_rows(path)]


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
        safe = key.replace(":", "_")   # an "EXIT:" or "LEFT:" key is no folder name on Windows
        outp = os.path.join(work, safe, safe + ".fbx").replace("\\", "/")   # never written: the roles' stale clear looks beside it
        os.makedirs(os.path.dirname(outp), exist_ok=True)
        albedo = os.path.join(work, safe + "_albedo.png").replace("\\", "/") if t[5] == "1" else ""
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
                died = died.replace(chr(10), chr(92) + "n")   # one row: a newline in the text (Blender's operator errors end in one) as a backslash and an n
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
                for r in export_glb(g, os.path.join(work, safe, safe + ".glb").replace("\\", "/")):
                    print(r)
        print("DONE\t%s" % key, flush=True)
    except Exception as e:
        traceback.print_exc()
        print("FAIL\t%s\t%s: %s" % (key, type(e).__name__, e), flush=True)
        fails += 1
    finally:
        sys.argv = saved
sys.exit(1 if fails else 0)
