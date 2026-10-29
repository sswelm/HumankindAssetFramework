"""What vehicle_rig.py makes of a Vehicle Lab recipe, stage by stage, as Blender has it (tools/vehiclerig_drill.sh, step (a)
of the vehicle_rig port, 2026-10-14).

usage: blender --background --python blender_vehiclerig_dump.py -- <vehicle_rig.py> <jobs file> <work dir>

A jobs file line is  JOB <key> <argv[0]> <argv[1]> ... (tab-separated; VehicleRigJobs.cs) - the script's arguments after
`--`, exactly as the Lab's Generate hands them - or  FAST <key> ...  for a recipe on the source-skeleton fast path. argv[2]
(the output GLB) is replaced by <work>/<key>/<key>_Spin.glb and argv[3] (the preview FBX) by "" - no FBX in the drill.
Each job runs THE SCRIPT ITSELF, its source cut into five stages at four of its own top-level lines and executed in one
namespace, with the scene written out after each stage; the fifth stage is the script's own export, and the GLB it wrote
is read back row by row (tools/deploy-drill/glb_rows.py, the X rows). The stages, by the script's own headings:

    1  the import, the second-model merge, per-part placement, the modifier bake, rigged-source detection, the flatten,
       the orientation (the rigfast path exports here and exits 0: EXIT 1 0, then the X rows)
    2  the source-side reduction tiers per role (limited dissolve, welds, decimate)
    3  the rig: wheel clustering, the role name sets, the gun, turret and rotor bones, tread cells and link bones, the
       join per bone
    4  the actions: Spin, wave rock, rowing, Furl, the flag strike, recoil and trails
    5  the export: the inside-out fix, the flips, double-siding, the export totals, the GLB

Rows: the scene rows of tools/riganim-drill/scene_rows.py (SCENE, OBJ, MESH, VG, BONE, PB, ACT, FC, K under VEHICLERIG_KEYS=1; the FC
and K rows carry the fcurve's SLOT after the action - a slotted action repeats a data path per slot),
and  JOB <key> / STAGE <n> / LOG <n> <a VEHICLE line the stage printed, in order; the timing lines left out> /
EXIT <n> <code> / DIES <n> <exception> / X <a GLB row> / XDIES <why no GLB> /
WORK <the work folder this run wrote under> (the second row of a job) /
EVIDENCE <key> <preceding row count> <sha256 of UTF-8 LF rows, including final newline, with every spelling of the folder
ABOVE the WORK folder (the run's temp root, where its fixtures and names folders sit too) replaced by @TMP@ - the seal is
the job's evidence, not the temp folder it ran in: the push gate replays this dump from a record made in another folder
(tools/blender_oracle.sh)> / DONE <key>.
"""
import bpy, sys, io, os, contextlib, traceback, hashlib, re

sys.stdout.reconfigure(encoding="utf-8")
args = sys.argv[sys.argv.index("--") + 1:]
script, jobs, work = args[0], args[1], args[2]


def seal_text(rows, work):
    """The rows as the seal hashes them: LF-joined with a final newline, the folder above the work folder (the run's
    temp root) in either slash form and any case as @TMP@ (VehicleRigDump.WithoutWork and missing_vehiclerig.py do the same)."""
    text = "\n".join(rows) + "\n"
    root = os.path.dirname(work.rstrip("/\\").replace("\\", "/"))
    forms = {root, root.replace("/", "\\")}
    for form in sorted(forms, key=len, reverse=True):
        text = re.sub(re.escape(form), "@TMP@", text, flags=re.IGNORECASE)
    return text
source = open(script, encoding="utf-8").read()
os.makedirs(work, exist_ok=True)
here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(here, "..", "riganim-drill"))
sys.path.insert(0, os.path.join(here, "..", "deploy-drill"))
import scene_rows   # noqa: E402
from scene_rows import dump   # noqa: E402
from glb_rows import rows as glb_rows   # noqa: E402
scene_rows.KEYS = os.environ.get("VEHICLERIG_KEYS") == "1"
scene_rows.FC_SLOT = True   # a slotted action repeats a path per slot: the slot is part of the fcurve's identity here

# The stage cuts: the script's own top-level lines, each found exactly once at column 0. A script edit that moves or
# rewords one is a FAIL here, never a silently different cut.
MARKERS = [
    "# ---- SOURCE-SIDE reduction tiers",
    "# ---- wheel clustering ----",
    "# the LINEAR \"Spin\" action: frame 0 = rest identity",
    "# FIX INSIDE-OUT FACES (argv[46], opt-in, 2026-09-04)",
]
cuts = []
for m in MARKERS:
    hits = [i for i in range(len(source)) if source.startswith("\n" + m, i)]
    if len(hits) != 1:
        print("FAIL\t-\tthe stage marker is found %d times in %s: %r" % (len(hits), script, m), flush=True)
        sys.exit(1)
    cuts.append(hits[0] + 1)
if cuts != sorted(cuts):
    print("FAIL\t-\tthe stage markers are out of order in %s" % script, flush=True); sys.exit(1)
bounds = [0] + cuts + [len(source)]
chunks = []
for i in range(5):
    text = source[bounds[i]:bounds[i + 1]]
    pad = source[:bounds[i]].count("\n")
    chunks.append(compile("\n" * pad + text, script, "exec"))   # the line numbers of a traceback stay the script's


def export_rows(path):
    if not os.path.exists(path):
        return ["XDIES\tno GLB written"]
    try:
        return ["X\t%s" % r for r in glb_rows(path)]
    except Exception as e:
        return ["XDIES\t%s: %s" % (type(e).__name__, e)]


fails = 0
for line in open(jobs, encoding="utf-8-sig").read().split("\n"):
    line = line.rstrip("\r")
    if not (line.startswith("JOB\t") or line.startswith("FAST\t")):
        continue
    t = line.split("\t")
    key = t[1]
    evidence = []
    def emit(row):
        evidence.append(row)
        print(row)
    emit("JOB\t%s" % key)
    emit("WORK\t%s" % work)
    saved = sys.argv
    try:
        safe = key.replace(":", "_")
        jobdir = os.path.join(work, safe); os.makedirs(jobdir, exist_ok=True)
        out_glb = os.path.join(jobdir, safe + "_Spin.glb").replace("\\", "/")
        if os.path.exists(out_glb):
            os.remove(out_glb)
        argv = list(t[2:])
        argv[2] = out_glb; argv[3] = ""
        sys.argv = ["blender", "--"] + argv
        g = {"__name__": "__main__"}
        exported = False
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
                if l.startswith("VEHICLE") and not l.startswith("VEHICLE timing:"):
                    emit("LOG\t%d\t%s" % (n, l))
            if code_exit is not None:
                emit("EXIT\t%d\t%s" % (n, code_exit))
                exported = code_exit == 0   # the fast path exports its own GLB and exits 0
                break
            if died is not None:
                emit("DIES\t%d\t%s" % (n, died)); break
            emit("STAGE\t%d" % n)
            for r in dump(n):
                emit(r)
            if n == 5:
                exported = True
        if exported:
            for r in export_rows(out_glb):
                emit(r)
        digest = hashlib.sha256(seal_text(evidence, work).encode("utf-8")).hexdigest()
        print("EVIDENCE\t%s\t%d\t%s" % (key, len(evidence), digest))
        print("DONE\t%s" % key, flush=True)
    except Exception as e:
        traceback.print_exc()
        print("FAIL\t%s\t%s: %s" % (key, type(e).__name__, e), flush=True)
        fails += 1
    finally:
        sys.argv = saved
sys.exit(1 if fails else 0)
