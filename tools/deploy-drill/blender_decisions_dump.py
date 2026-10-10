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
    (stages 2-5: the bake, the snapshot, the retarget, the recoil - see the comments in the loop)
    stage 6, the bind (after `# --- 6.`, before `# --- 7.`), written before the last-frame rows and the sweep:
    LOG6    <a DEPLOY line the bind printed>
    DIES6   <exception>                     the script died in the bind (the port must have left the file to Blender)
    BIND    <mesh object> <bone (its vertex group)> <data name> <data users> <parent> <parent_type> <location 3, rotation_quaternion 4, scale 3> <matrix_parent_inverse 16>
    VG6     <mesh object> <vertex group names, comma-separated> <vertices> <vertices with exactly one weight, group 0 at 1.0>
    MOD6    <mesh object> <type:name:object, comma-separated>
    V6      <mesh object> <vertices> <sha256 of every vertex position, float32 little-endian, in order>
    VX6     <mesh object> <vertex index> <x y z>   a sample: index i*n//k for i in range(k), k = min(64, n)
    N6      <mesh object> <custom_normal data type or -> <domain or -> <count> <sha256 of the INT16_2D values, little-endian, or ->
    DATA6   <mesh datablock> <users>        every mesh datablock (bpy.data.meshes), the copies the bind made included
    O7      <name> <16>                     every object's matrix_world after a view_layer.update()
    DONE    <key>
"""
import bpy, sys, io, struct, contextlib, traceback, hashlib, array


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
cut5 = source.index("\n# --- 6.")             # ... and then the recoil tail (5d)
code5 = compile(source[cut4:cut5], script, "exec")
cut6 = source.index("\n# --- 7.")             # ... and then the bind (6)
code6 = compile(source[cut5:cut6], script, "exec")
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
            # ---- stage 5 (part 5d): the script goes on to its `# --- 6.` - the recoil tail: the source's kickback read
            #      over the fire window, a RecoilArm bone put between the tube and its parent (edit mode: every bone is
            #      rebuilt from its edit bone), the arm keyed. What it printed; what it measured on the way (the script's
            #      own variables); the bones at rest again; the action; what the pose bones hold and where they stand
            out5 = io.StringIO(); exit5 = None; dies5 = False
            try:
                with contextlib.redirect_stdout(out5):
                    exec(code5, g)
            except SystemExit as e:
                exit5 = e.code
            except Exception as e:   # the script's own death in the step (a matrix without an inverse): the port must have left it
                print("DIES5\t%s: %s" % (type(e).__name__, e)); dies5 = True
            for l in out5.getvalue().split("\n"):
                if l.startswith("DEPLOY"):
                    print("LOG5\t%s" % l)
            if exit5 is not None:
                print("EXIT5\t%s" % exit5)
            elif g.get("recoil_out_end") is not None:
                def m16(m): return "\t".join(h32(m[r][c]) for r in range(4) for c in range(4))
                def v3(v): return "\t".join(h32(c) for c in v)
                print("R5\tframes\t%d\t%d\t%d\t%s" % (g["rs"], g["re"], g["step"], ",".join(str(t) for t in g["frames"])))
                print("R5\tnames\t%s\t%s\t%s\t%s\t%s" % (g["driver"], g["cradle"], g["tube_root"], g["ra_name"], ",".join(g["ordered"])))
                print("R5\tscalars\t%s\t%s\t%s\t%d\t%d\t%d" % (h64(g["mag"]), h64(g["dist"]), h64(g["R"]), g["deploy_end"], g["kick_end"], g["recoil_out_end"]))
                for bn in g["ordered"]:
                    print("R5\thome\t%s\t%s" % (bn, m16(g["m_home"][bn])))
                    print("R5\taim\t%s\t%s" % (bn, m16(g["m_aim"][bn])))
                for bn, by in g["src_w"].items():
                    for t, m in by.items():
                        print("R5\tsrc\t%s\t%d\t%s" % (bn, t, m16(m)))
                for t, v in g["slide"].items():
                    print("R5\tslide\t%d\t%s" % (t, v3(v)))
                for n in ("peak", "d", "A", "radius", "tube_head", "pivot", "A_local"):
                    print("R5\tvec\t%s\t%s" % (n, v3(g[n])))
                print("R5\tcbar\t%s" % "\t".join(h32(g["Cbar3"][r][c]) for r in range(3) for c in range(3)))
                print("R5\tthetas\t%s" % "\t".join(h64(v) for v in g["thetas"]))
            for b in arm.data.bones:
                ml = b.matrix_local
                print("RBONE5\t%s\t%s\t%s" % (b.name, b.parent.name if b.parent else "-", "\t".join(
                    h32(v) for v in (*b.head_local, *b.tail_local, b.length, *(ml[r][c] for r in range(4) for c in range(4))))))
            act = arm.animation_data.action if arm.animation_data else None
            if act is not None:
                for layer in act.layers:
                    for strip in layer.strips:
                        for cb in strip.channelbags:
                            for fc in cb.fcurves:
                                print("FC5\t%s\t%d\t%s\t%s\t%s" % (fc.data_path, fc.array_index, fc.extrapolation, fc.auto_smoothing, "\t".join(
                                    "%s:%s:%s:%s:%s:%s:%s:%s:%s" % (h32(kp.co[0]), h32(kp.co[1]), kp.interpolation, kp.handle_left_type, kp.handle_right_type,
                                                                    h32(kp.handle_left[0]), h32(kp.handle_left[1]), h32(kp.handle_right[0]), h32(kp.handle_right[1]))
                                    for kp in fc.keyframe_points)))
            for pb in arm.pose.bones:
                q = pb.rotation_quaternion
                print("APB5\t%s\t%s" % (pb.name, "\t".join(h32(v) for v in (*pb.location, q.w, q.x, q.y, q.z, *pb.scale))))
                # pb.matrix is what the LAST evaluation left (a dropped scale curve lingers in it until a frame is set):
                # only where the recoil step evaluated last does it say what the properties say
                if exit5 is None and g.get("recoil_out_end") is not None:
                    pm = pb.matrix
                    print("PM5\t%s\t%s" % (pb.name, "\t".join(h32(pm[r][c]) for r in range(4) for c in range(4))))
            for o in bpy.data.objects:
                mw = o.matrix_world
                print("O6\t%s\t%s" % (o.name, "\t".join(h32(mw[r][c]) for r in range(4) for c in range(4))))
            # ---- stage 6 (part 6): the script goes on to its `# --- 7.` - the bind: the scene set to the bind frame, each
            #      mesh detached, its world transform folded into its vertices (a shared datablock copied first), one vertex
            #      group of its bone at weight 1, an Armature modifier, the mesh under the armature at the identity. What it
            #      printed; each bound mesh (its bone, its datablock and that one's users, its parent and own transform, its
            #      parent inverse), its vertex groups and the weights' census, its modifiers, its vertices (a hash of every
            #      float32 and a sample), its custom normals (unchanged: the attribute is INT16_2D, which mesh_transform leaves
            #      alone), every mesh datablock with its users, and every object's matrix_world after an update. (The rows at
            #      the last frame and the sweep below then see the bound scene: the meshes under the armature.)
            if exit5 is None and not dies5:
                out6 = io.StringIO(); dies6 = False
                try:
                    with contextlib.redirect_stdout(out6):
                        exec(code6, g)
                except Exception as e:
                    print("DIES6\t%s: %s" % (type(e).__name__, e)); dies6 = True
                for l in out6.getvalue().split("\n"):
                    if l.startswith("DEPLOY"):
                        print("LOG6\t%s" % l)
                if not dies6:
                    for m in g["meshes"]:
                        me = m.data
                        q = m.rotation_quaternion
                        print("BIND\t%s\t%s\t%s\t%d\t%s\t%s\t%s\t%s" % (m.name, m.vertex_groups[0].name if len(m.vertex_groups) else "-", me.name, me.users,
                              m.parent.name if m.parent else "-", m.parent_type, "\t".join(h32(v) for v in (*m.location, q.w, q.x, q.y, q.z, *m.scale)),
                              "\t".join(h32(m.matrix_parent_inverse[r][c]) for r in range(4) for c in range(4))))
                        n = len(me.vertices)
                        ok = sum(1 for v in me.vertices if len(v.groups) == 1 and v.groups[0].group == 0 and v.groups[0].weight == 1.0)
                        print("VG6\t%s\t%s\t%d\t%d" % (m.name, ",".join(vg.name for vg in m.vertex_groups), n, ok))
                        print("MOD6\t%s\t%s" % (m.name, ",".join("%s:%s:%s" % (md.type, md.name, md.object.name if md.type == 'ARMATURE' and md.object else "-") for md in m.modifiers)))
                        co = array.array("f", [0.0]) * (3 * n)
                        me.vertices.foreach_get("co", co)
                        if sys.byteorder != "little": co.byteswap()
                        print("V6\t%s\t%d\t%s" % (m.name, n, hashlib.sha256(co.tobytes()).hexdigest()))
                        k = min(64, n)
                        for i in sorted(set(i * n // k for i in range(k))):
                            print("VX6\t%s\t%d\t%s\t%s\t%s" % (m.name, i, h32(co[3 * i]), h32(co[3 * i + 1]), h32(co[3 * i + 2])))
                        cn = me.attributes.get("custom_normal")
                        if cn is None:
                            print("N6\t%s\t-\t-\t0\t-" % m.name)
                        elif cn.data_type == 'INT16_2D':
                            vals = array.array("i", [0]) * (2 * len(cn.data))
                            cn.data.foreach_get("value", vals)
                            shorts = array.array("h", vals)
                            if sys.byteorder != "little": shorts.byteswap()
                            print("N6\t%s\t%s\t%s\t%d\t%s" % (m.name, cn.data_type, cn.domain, len(cn.data), hashlib.sha256(shorts.tobytes()).hexdigest()))
                        else:
                            print("N6\t%s\t%s\t%s\t%d\t?" % (m.name, cn.data_type, cn.domain, len(cn.data)))
                    for me in bpy.data.meshes:
                        print("DATA6\t%s\t%d" % (me.name, me.users))
                    bpy.context.view_layer.update()
                    for o in bpy.data.objects:
                        mw = o.matrix_world
                        print("O7\t%s\t%s" % (o.name, "\t".join(h32(mw[r][c]) for r in range(4) for c in range(4))))
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
            # ... and, last of all, a SWEEP over the frames: what the new armature's pose bones hold at each - a
            # re-keyed bone between two of its Bezier keys is Blender's curve evaluation (the cubic solver)
            if any(l.startswith("DEPLOY") for l in (out4.getvalue() + out5.getvalue()).split("\n")):
                def whole(s, d):
                    try: return max(-3000, min(3000, int(s)))
                    except ValueError: return d
                av = g["argv"]
                ends = [g["fmin"], g["fmax"], whole(av[3] if len(av) > 3 else "", g["fmax"]), whole(av[5] if len(av) > 5 else "", g["fmin"])]
                lo, hi = min(ends) - 3, max(ends) + 3
                step = max(1, (hi - lo) // 600)
                def raw(s, d):
                    try: return int(s)
                    except ValueError: return d
                # ... and about the barrel's mid and end key and the ready frame wherever they lie (an end of 40000 is
                # far outside the range above), across the segment between them, at the frame limits - then back
                # again: a held zero keeps its sign. (DecisionsDrill makes the same list and takes no other.)
                e_ = raw(av[3] if len(av) > 3 else "", g["fmax"]); m_ = max(int(e_ * 0.5), 1); r_ = raw(av[5] if len(av) > 5 else "", g["fmin"])
                more = []
                for k_ in (m_, e_, r_, 1048574, -1048574):
                    more += [k_ - 2, k_ - 1, k_, k_ + 1, k_ + 2]
                a_, b_ = min(m_, e_), max(m_, e_)
                more += [a_ + (b_ - a_) * j // 97 for j in range(98)] + [a_ + j for j in range(40)] + [b_ - j for j in range(40)]
                if exit5 is None and g.get("recoil_out_end") is not None:
                    # ... and the recoil tail: about the kick's end and the settle, and across the return
                    k_, o_ = g["kick_end"], g["recoil_out_end"]
                    for x_ in (k_, o_):
                        more += [x_ - 2, x_ - 1, x_, x_ + 1, x_ + 2]
                    more += [k_ + (o_ - k_) * j // 8 for j in range(9)] + [lo, hi]
                frames = []
                for frame in list(range(lo, hi + 1, step)) + [lo + 1, hi + 1, (lo + hi) // 2] + more + [lo, hi]:
                    # frame_set accepts Int32, then clamps to Blender's scene limits. Neighbours of a valid
                    # extreme end/ready frame must remain valid API arguments too.
                    frame = max(-2147483648, min(2147483647, frame))
                    if not frames or frames[-1] != frame: frames.append(frame)
                for frame in frames:
                    bpy.context.scene.frame_set(frame)
                    for pb in arm.pose.bones:
                        q = pb.rotation_quaternion
                        print("SW\t%d\t%s\t%s" % (frame, pb.name, "\t".join(h32(v) for v in (*pb.location, q.w, q.x, q.y, q.z, *pb.scale))))
        print("DONE\t%s" % key, flush=True)
    except Exception as e:
        traceback.print_exc()
        print("FAIL\t%s\t%s: %s" % (key, type(e).__name__, e), flush=True)
        fails += 1
    finally:
        sys.argv = saved
sys.exit(1 if fails else 0)
