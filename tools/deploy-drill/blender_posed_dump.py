"""Blender's POSED STATE of an imported animated glTF file, for tools/deploy_drill.sh (replacing deploy_convert.py, part 1):
what `scene.frame_set(f)` makes of every object - the evaluated location, rotation and scale, and matrix_world - at a
handful of frames of each file. Everything deploy_convert.py decides and bakes starts from these matrices.

Rows (tab separated; floats as the hex of their float32 bits, so nothing is lost in print):

    FILE   <path, lower case>
    SCENE  <fps> <fps_base> <frame_start> <frame_end>
    ACTION <name> <frame_range start> <frame_range end> <slots>          one per action in the file
    OBJ    <name> <type> <parent or -> <parent_type> <parent_bone or -> <active action or -> <rotation_mode>
    FRAMES <f> <f> ...                                                     the frames that follow
    L      <frame> <name> <loc x y z> <quat w x y z> <scale x y z>         the evaluated properties
    M      <frame> <name> <matrix_world, 16 floats row by row>
    DONE   <path>

The frames: before the first key, the first, the one after, eight spread over the range, the last, and one beyond -
so a value on a key, between two, and held outside the range are all in.

usage: blender --background --python blender_posed_dump.py -- <file>...
"""
import bpy, struct, sys

sys.stdout.reconfigure(encoding="utf-8")
files = sys.argv[sys.argv.index("--") + 1:]


def hx(v):
    return "%08X" % struct.unpack("<I", struct.pack("<f", float(v)))[0]


fails = 0
for path in files:
    key = path.replace("\\", "/").lower()
    try:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.gltf(filepath=path)
        scene = bpy.context.scene
        print("FILE\t%s" % key)
        print("SCENE\t%d\t%s\t%d\t%d" % (scene.render.fps, hx(scene.render.fps_base), scene.frame_start, scene.frame_end))
        for a in bpy.data.actions:
            print("ACTION\t%s\t%s\t%s\t%d" % (a.name, hx(a.frame_range[0]), hx(a.frame_range[1]), len(a.slots)))
        objs = list(bpy.data.objects)
        for o in objs:
            act = o.animation_data.action.name if o.animation_data and o.animation_data.action else "-"
            print("OBJ\t%s\t%s\t%s\t%s\t%s\t%s\t%s" % (o.name, o.type, o.parent.name if o.parent else "-", o.parent_type, o.parent_bone or "-", act, o.rotation_mode))
        # an armature's bones at rest: the parent, the length, the head in the parent's space, bone.matrix (3x3, rows)
        # and bone.matrix_local (4x4, rows)
        for o in objs:
            if o.type == 'ARMATURE':
                for b in o.data.bones:
                    print("BONE\t%s\t%s\t%s\t%s" % (o.name, b.name, b.parent.name if b.parent else "-", "\t".join(
                        hx(v) for v in (b.length, *b.head, *(b.matrix[r][c] for r in range(3) for c in range(3)), *(b.matrix_local[r][c] for r in range(4) for c in range(4))))))
        fmin, fmax = 1e9, -1e9
        for o in objs:
            if o.animation_data and o.animation_data.action:
                fr = o.animation_data.action.frame_range
                fmin = min(fmin, fr[0]); fmax = max(fmax, fr[1])
        if fmin > fmax:
            fmin, fmax = 1.0, 1.0
        fmin, fmax = int(fmin), int(fmax)
        frames = [fmin - 3, fmin, fmin + 1] + [fmin + (fmax - fmin) * k // 9 for k in range(1, 9)] + [fmax, fmax + 4]
        frames = sorted(set(frames))
        print("FRAMES\t" + "\t".join(str(f) for f in frames))
        for f in frames:
            scene.frame_set(f)
            bpy.context.view_layer.update()
            for o in objs:
                q = o.rotation_quaternion
                print("L\t%d\t%s\t%s" % (f, o.name, "\t".join(hx(v) for v in (*o.location, q.w, q.x, q.y, q.z, *o.scale))))
                mw = o.matrix_world
                print("M\t%d\t%s\t%s" % (f, o.name, "\t".join(hx(mw[r][c]) for r in range(4) for c in range(4))))
                if o.type == 'ARMATURE':
                    for pb in o.pose.bones:
                        q = pb.rotation_quaternion; pm = pb.matrix
                        print("PB\t%d\t%s\t%s\t%s" % (f, o.name, pb.name, "\t".join(hx(v) for v in (*pb.location, q.w, q.x, q.y, q.z, *pb.scale, *(pm[r][c] for r in range(4) for c in range(4))))))
        print("DONE\t%s" % key, flush=True)
    except Exception as e:
        print("FAIL\t%s\t%s: %s" % (key, type(e).__name__, e), flush=True)
        fails += 1
sys.exit(1 if fails else 0)
