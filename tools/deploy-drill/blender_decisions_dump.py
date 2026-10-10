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
cut2 = source.index("\n# --- 5a.")            # ... and then the bake, the scale-free step and the delta-form rebase
code2 = compile(source[cut:cut2], script, "exec")
cut3 = source.index("\n# --- 5b.")            # ... and then the fire-window snapshot (5a)
code3 = compile(source[cut2:cut3], script, "exec")
cut4 = source.index("\n# --- 5d.")            # ... and then the barrel retarget (5b) and the leg scale (5c)
code4 = compile(source[cut3:cut4], script, "exec")
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
            # ---- stage 2 (part 4): the script goes on - the bake, the scale-free step, the delta-form rebase - up to
            #      its `# --- 5a.`; what it printed there and the action it left: every fcurve, key by key
            out2 = io.StringIO()
            with contextlib.redirect_stdout(out2):
                exec(code2, g)
            for l in out2.getvalue().split("\n"):
                if l.startswith("DEPLOY"):
                    print("LOG2\t%s" % l)
            arm = g["arm"]
            act = arm.animation_data.action if arm.animation_data else None
            print("ACT\t%s\t%s" % (act.name if act else "-", arm.parent.name if arm.parent else "-"))
            if act is not None:
                for layer in act.layers:
                    for strip in layer.strips:
                        for cb in strip.channelbags:
                            for fc in cb.fcurves:
                                kps = fc.keyframe_points
                                print("FC\t%s\t%d\t%s\t%s" % (fc.data_path, fc.array_index, kps[0].interpolation if len(kps) else "-",
                                                              "\t".join("%s:%s" % (h32(kp.co[0]), h32(kp.co[1])) for kp in kps)))
            # the bake takes every selected armature: the action it gave each OTHER armature (part 4b), its slot's curves
            for o in bpy.data.objects:
                if o.type != 'ARMATURE' or o is arm or not o.animation_data or not o.animation_data.action:
                    continue
                oa = o.animation_data.action; slot = o.animation_data.action_slot
                for layer in oa.layers:
                    for strip in layer.strips:
                        cb = strip.channelbag(slot) if slot is not None else None
                        for fc in (cb.fcurves if cb is not None else []):
                            kps = fc.keyframe_points
                            print("FCA\t%s\t%s\t%d\t%s\t%s" % (o.name, fc.data_path, fc.array_index, kps[0].interpolation if len(kps) else "-",
                                                                "\t".join("%s:%s" % (h32(kp.co[0]), h32(kp.co[1])) for kp in kps)))
                # what each of its pose bones HOLDS when the script goes on (the later steps start from this state)
                for pb in o.pose.bones:
                    q = pb.rotation_quaternion
                    print("PB2\t%s\t%s\t%s" % (o.name, pb.name, "\t".join(h32(v) for v in (*pb.location, q.w, q.x, q.y, q.z, *pb.scale))))
            mw = arm.matrix_world
            print("M2\t%s" % "\t".join(h32(mw[r][c]) for r in range(4) for c in range(4)))
            # ... and the scene the bake leaves: every object's matrix_world again (the later steps read them)
            # (no view_layer.update() here: it would evaluate the action again - the rebased keys - where the script does not)
            for o in bpy.data.objects:
                mw = o.matrix_world
                print("O2\t%s\t%s" % (o.name, "\t".join(h32(mw[r][c]) for r in range(4) for c in range(4))))
            # ---- stage 3 (part 5a): the script goes on to its `# --- 5b.` - the fire-window snapshot. What it printed,
            #      the snapshot itself (per frame and bone: location, rotation_quaternion), what the new armature's pose
            #      bones hold afterwards and where the scene stands
            out3 = io.StringIO()
            with contextlib.redirect_stdout(out3):
                exec(code3, g)
            for l in out3.getvalue().split("\n"):
                if l.startswith("DEPLOY"):
                    print("LOG3\t%s" % l)
            for f in sorted(g["_fire_snap"]):
                for bone, (loc, quat) in g["_fire_snap"][f].items():
                    print("SNAP\t%d\t%s\t%s" % (f, bone, "\t".join(h32(v) for v in (*loc, quat.w, quat.x, quat.y, quat.z))))
            # (and none here: the snapshot's own loop updated the scene where the script does)
            for pb in arm.pose.bones:
                q = pb.rotation_quaternion
                print("APB\t%s\t%s" % (pb.name, "\t".join(h32(v) for v in (*pb.location, q.w, q.x, q.y, q.z, *pb.scale))))
            for o in bpy.data.objects:
                mw = o.matrix_world
                print("O4\t%s\t%s" % (o.name, "\t".join(h32(mw[r][c]) for r in range(4) for c in range(4))))
            # ---- stage 4 (parts 5b, 5c): the script goes on to its `# --- 5d.` - the barrel retargeted to its ready
            #      frame, the leg spread scaled. What it printed; the action again, now with EVERY key's interpolation and
            #      handles (the new keys are Bezier); what the pose bones hold; where the scene stands
            out4 = io.StringIO()
            with contextlib.redirect_stdout(out4):
                exec(code4, g)
            for l in out4.getvalue().split("\n"):
                if l.startswith("DEPLOY"):
                    print("LOG4\t%s" % l)
            act = arm.animation_data.action if arm.animation_data else None
            if act is not None:
                for layer in act.layers:
                    for strip in layer.strips:
                        for cb in strip.channelbags:
                            for fc in cb.fcurves:
                                print("FC4\t%s\t%d\t%s\t%s\t%s" % (fc.data_path, fc.array_index, fc.extrapolation, fc.auto_smoothing, "\t".join(
                                    "%s:%s:%s:%s:%s:%s:%s:%s:%s" % (h32(kp.co[0]), h32(kp.co[1]), kp.interpolation, kp.handle_left_type, kp.handle_right_type,
                                                                    h32(kp.handle_left[0]), h32(kp.handle_left[1]), h32(kp.handle_right[0]), h32(kp.handle_right[1]))
                                    for kp in fc.keyframe_points)))
            for pb in arm.pose.bones:
                q = pb.rotation_quaternion
                print("APB4\t%s\t%s" % (pb.name, "\t".join(h32(v) for v in (*pb.location, q.w, q.x, q.y, q.z, *pb.scale))))
            for o in bpy.data.objects:
                mw = o.matrix_world
                print("O5\t%s\t%s" % (o.name, "\t".join(h32(mw[r][c]) for r in range(4) for c in range(4))))
            # ... and at the LAST frame of the range: the bind frame cannot tell a frozen object from an animated one,
            # nor a stripped scale curve from a kept one (the script's later steps set frames all over the range)
            bpy.context.scene.frame_set(g["fmax"])
            bpy.context.view_layer.update()
            for o in bpy.data.objects:
                mw = o.matrix_world
                print("O3\t%s\t%s" % (o.name, "\t".join(h32(mw[r][c]) for r in range(4) for c in range(4))))
                if o.type == 'ARMATURE' and o is not arm:
                    for pb in o.pose.bones:
                        q = pb.rotation_quaternion
                        print("PB3\t%s\t%s\t%s" % (o.name, pb.name, "\t".join(h32(v) for v in (*pb.location, q.w, q.x, q.y, q.z, *pb.scale))))
        print("DONE\t%s" % key, flush=True)
    except Exception as e:
        traceback.print_exc()
        print("FAIL\t%s\t%s: %s" % (key, type(e).__name__, e), flush=True)
        fails += 1
    finally:
        sys.argv = saved
sys.exit(1 if fails else 0)
