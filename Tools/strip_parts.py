# strip_parts.py — remove named objects (and their children) from a model, export a cleaned GLB the Factory then bakes.
# Lets a modder drop parts they don't want baked in — e.g. a helicopter's own rotor, so the donor's spinning rotor
# shows through; or a crew figure / weapon pod. Matching is case-insensitive substring on the object name.
# Args after "--":  <input model>  <output .glb>  <comma,separated,name,substrings>
import bpy, sys

argv = sys.argv[sys.argv.index("--") + 1:]
inp, outp, subs_arg = argv[0], argv[1], argv[2]
subs = [s.strip().lower() for s in subs_arg.split(",") if s.strip()]

bpy.ops.wm.read_factory_settings(use_empty=True)

ext = inp.lower().rsplit(".", 1)[-1]
if ext in ("glb", "gltf"):
    bpy.ops.import_scene.gltf(filepath=inp)
elif ext == "fbx":
    bpy.ops.import_scene.fbx(filepath=inp)
elif ext == "obj":
    try:
        bpy.ops.wm.obj_import(filepath=inp)          # Blender 3.3+
    except AttributeError:
        bpy.ops.import_scene.obj(filepath=inp)       # legacy
else:
    print("[strip_parts] ERROR: unsupported input extension '%s'" % ext)
    sys.exit(1)

# Collect every object whose name matches a substring, plus all of its descendants (so stripping a group empty like
# 'vint_m' takes the whole rotor, not just the empty).
victims = set()
for o in list(bpy.data.objects):
    if any(s in o.name.lower() for s in subs):
        victims.add(o)
        for c in o.children_recursive:
            victims.add(c)

removed = sorted(v.name for v in victims)
for v in victims:
    bpy.data.objects.remove(v, do_unlink=True)

print("[strip_parts] matched %s -> removed %d object(s): %s" % (subs, len(removed), ", ".join(removed[:50])))
if not removed:
    print("[strip_parts] WARNING: no object name matched %s — nothing was stripped (check the names)" % subs)

bpy.ops.export_scene.gltf(filepath=outp, export_format="GLB")
print("[strip_parts] exported %s" % outp)
