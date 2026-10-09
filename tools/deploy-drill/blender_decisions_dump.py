"""The decisions deploy_convert.py takes before it builds anything, as Blender takes them (tools/deploy_drill.sh, part 2).

usage: blender --background --python blender_decisions_dump.py -- <deploy_convert.py> <jobs file>

A jobs file line is  <key>|<input>|<argv[2]>|<argv[3]>|...  - the arguments the Factory gives the script after the input
and the output. Each job runs THE SCRIPT ITSELF, cut where it starts to build the armature (the line that begins
"# --- 4. armature"), so nothing here restates a rule; what the script printed and what it left in its variables and in
the scene is then written out, every float as the hex of its bits.

    JOB     <key>
    LOG     <a DEPLOY line or a part line the script printed>
    EXIT    <code>                 the script stopped itself (no animated part)
    RANGE   <fmin> <fmax>
    NORM    <dim> <scale> <recenter 0|1> <off_h> <off_v>                   (float64 hex)
    FLAG    <legacy 0|1>
    PART    <name> <parent or ->            the parts left, in the script's order
    BAD     <name>                          a culled part
    ALIAS   <dropped part> <kept part>      a pair-merge
    ARM     <the armature's name>
    BONEOF  <part> <bone>                   which bone each part rides (a merged part: its neighbour's)
    RBONE   <bone> <parent or -> <head_local 3, tail_local 3, length, matrix_local 16>      arm.data.bones order
    ANCHOR  <the object StaticRoot is constrained to, or ->
    HULL    <the root-motion anchor or -> <travel> <model size>      (float64 hex, - when no mesh rides a bone)
    PINV    <the armature's matrix_parent_inverse, 16>
    OBJ     <name> <type> <parent or -> <data name or -> <action 0|1>    every object left, bpy.data.objects order
    M       <name> <16 float32 hex, rows>                                  its matrix_world at the bind frame
    T       <name> <location 3, rotation_quaternion 4 (w x y z), scale 3>   its own transform there (float32 hex)
    BOX     <name> <min 3, max 3>                                          a mesh object's bound_box (float32 hex)
    DONE    <key>
"""
import bpy, sys, io, struct, contextlib, traceback


def h32(v):
    return struct.pack(">f", v).hex()


def h64(v):
    return struct.pack(">d", float(v)).hex()


sys.stdout.reconfigure(encoding="utf-8")   # names are not ASCII: a redirected stdout is the ANSI code page otherwise
args = sys.argv[sys.argv.index("--") + 1:]
script, jobs = args[0], args[1]
source = open(script, encoding="utf-8").read()
cut = source.index("\nbpy.ops.nla.bake(")   # the armature, its constraints and the root-motion anchor are made; nothing is baked
code = compile(source[:cut], script, "exec")
fails = 0
for line in open(jobs, encoding="utf-8-sig").read().split("\n"):
    line = line.rstrip("\r")
    if not line.strip():
        continue
    t = line.split("|")
    key = t[0]
    print("JOB\t%s" % key, flush=True)
    saved = sys.argv
    try:
        sys.argv = ["blender", "--", t[1], "unused.glb"] + t[2:]
        g = {"__name__": "__main__"}
        out = io.StringIO()
        code_exit = None
        try:
            with contextlib.redirect_stdout(out):
                exec(code, g)
        except SystemExit as e:
            code_exit = e.code
        for l in out.getvalue().split("\n"):
            if l.startswith("DEPLOY") or l.startswith("   part:"):
                print("LOG\t%s" % l)
        if code_exit is not None:
            print("EXIT\t%s" % code_exit)
        else:
            print("RANGE\t%d\t%d" % (g["fmin"], g["fmax"]))
            print("NORM\t%s\t%s\t%d\t%s\t%s" % (h64(g["_nrm_dim"]), h64(g["_nrm_scale"]), 1 if g["_recenter"] else 0, h64(g["_off_h"]), h64(g["_off_v"])))
            print("FLAG\t%d" % (1 if g["_LEGACY"] else 0))
            for p in g["parts"]:
                print("PART\t%s\t%s" % (p.name, p.parent.name if p.parent else "-"))
            for n in sorted(g["_bad_names"]):
                print("BAD\t%s" % n)
            for d, k in g["_alias_pairs"]:
                print("ALIAS\t%s\t%s" % (d.name, k.name))
            # the armature the script made (part 3): its bones at rest, which bone each part rides, the anchors
            arm = g["arm"]
            print("ARM\t%s" % arm.name)
            for part, bone in g["bone_of"].items():
                print("BONEOF\t%s\t%s" % (part, bone))
            for b in arm.data.bones:
                ml = b.matrix_local
                print("RBONE\t%s\t%s\t%s" % (b.name, b.parent.name if b.parent else "-", "\t".join(
                    h32(v) for v in (*b.head_local, *b.tail_local, b.length, *(ml[r][c] for r in range(4) for c in range(4))))))
            print("ANCHOR\t%s" % (g["_static_anchor"].name if g["_static_anchor"] is not None else "-"))
            print("HULL\t%s\t%s\t%s" % (g["_hull"].name if g["_hull"] is not None else "-", h64(g["_travel"]) if "_travel" in g else "-", h64(g["_dim_now"]) if "_dim_now" in g else "-"))
            pi = arm.matrix_parent_inverse
            print("PINV\t%s" % "\t".join(h32(pi[r][c]) for r in range(4) for c in range(4)))
            bpy.context.view_layer.update()
            for o in bpy.data.objects:
                data = o.data.name if getattr(o, "data", None) is not None and hasattr(o.data, "name") else "-"
                print("OBJ\t%s\t%s\t%s\t%s\t%d" % (o.name, o.type, o.parent.name if o.parent else "-", data, 1 if o.animation_data and o.animation_data.action else 0))
                mw = o.matrix_world
                print("M\t%s\t%s" % (o.name, "\t".join(h32(mw[r][c]) for r in range(4) for c in range(4))))
                print("T\t%s\t%s" % (o.name, "\t".join(h32(v) for v in (*o.location, *o.rotation_quaternion, *o.scale))))
                if o.type == 'MESH':
                    bb = [tuple(c) for c in o.bound_box]
                    print("BOX\t%s\t%s" % (o.name, "\t".join(h32(v) for v in (*map(min, *bb), *map(max, *bb)))))
        print("DONE\t%s" % key, flush=True)
    except Exception as e:
        traceback.print_exc()
        print("FAIL\t%s\t%s: %s" % (key, type(e).__name__, e), flush=True)
        fails += 1
    finally:
        sys.argv = saved
sys.exit(1 if fails else 0)
