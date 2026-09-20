# placement_shift.py — what the 2026-09-20 placement change will do to every ANIMATED entry in a pack, and the
# Position offset each one needs afterwards to look exactly as it does today.
#
#   blender --background --python placement_shift.py -- "<path to pack.json>"
#
# WHY THIS EXISTS. Until 2026-09-20 an animated bake left the model wherever the artist happened to leave it inside
# its file: nothing centred it, and only the opt-in Auto-ground toggle grounded it. People compensated by hand with
# the registry's Position offset. The bake now centres the model's box on the origin and drops its lowest point to
# it, exactly as the static path always has — so those compensating dials are suddenly over-corrections, and every
# animated entry shifts on its NEXT re-bake (nothing moves until then: placement is baked in).
#
# THE RULE. The bake removes the model's own miscentring, so folding that same amount into the dial preserves
# today's appearance:
#
#     new_x = old_x + move_x        new_y = old_y + move_y        new_z = old_z - lift   (only if NOT already auto-grounded)
#
# An entry whose dial was PURE compensation lands on ~0, which is the tell that the sign is right. Measured on the
# shipped ENC pack, four independently hand-dialed entries collapse at once: GatlingGuns -3.70 + 3.34 = -0.36,
# AntiTankIFV +0.50 - 0.497 = +0.003, StealthHelicopter (-0.50, +0.50) + (+0.391, -0.525) = (-0.11, -0.03),
# TOW-Infantry -0.30 + 0.247 = -0.05. With the sign the other way those would DOUBLE (GatlingGuns to -7.04 on a
# size-2.5 model), which no one would have shipped.
#
# CAVEAT, and it matters: this assumes the entry is still baked with the OLD placement. An entry you have ALREADY
# re-baked is done — its dial is already in the new frame, and adding the move again would break it. The script
# cannot tell; it prints the warning and leaves the judgement to you.
#
# The measurement mirrors rig_anim.py's own: import, cull material-less junk meshes (the glTF importer's
# placeholder icosphere is not in the file), take the WORLD box, apply the registry rotation the way rig_anim
# composes it (Rz(Y) @ Rx(X) @ Ry(Z) in the Blender Z-up world), then scale by size/longest into game units.
import bpy
import json
import math
import os
import sys

from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index("--") + 1:]
if not argv:
    print("PLACEMENT usage: blender --background --python placement_shift.py -- <pack.json>")
    sys.exit(2)

pack_path = argv[0]
pack = json.load(open(pack_path, encoding="utf-8"))
models = pack.get("models", []) if isinstance(pack, dict) else pack


def world_box(path):
    """The box rig_anim would measure: every mesh that survives the junk cull, in world space."""
    ext = os.path.splitext(path)[1].lower()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    if ext in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif ext == ".fbx":
        bpy.ops.import_scene.fbx(filepath=path)
    elif ext == ".blend":
        bpy.ops.wm.open_mainfile(filepath=path)
    else:
        raise RuntimeError("unsupported extension " + ext)
    for junk in [o for o in bpy.context.scene.objects if o.type == 'MESH' and len(o.data.materials) == 0]:
        bpy.data.objects.remove(junk, do_unlink=True)
    pts = []
    for o in bpy.context.scene.objects:
        if o.type != 'MESH' or not len(o.data.vertices):
            continue
        m = o.matrix_world
        pts += [m @ v.co for v in o.data.vertices]
    if not pts:
        raise RuntimeError("no mesh left after the junk cull")
    return pts


print("PLACEMENT pack: %s" % pack_path)
print("PLACEMENT %-22s %-24s %-24s %s" % ("entry", "Position today", "Position after re-bake", "note"))
print("PLACEMENT " + "-" * 92)

for e in models:
    if not e.get("animated"):
        continue
    name = e.get("resourceName") or "?"
    src = e.get("modelFile") or ""
    if not os.path.exists(src):
        print("PLACEMENT %-22s model file not found: %s" % (name, src))
        continue
    try:
        pts = world_box(src)
    except Exception as ex:
        print("PLACEMENT %-22s could not measure (%s)" % (name, str(ex)[:60]))
        continue

    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    size = e.get("size") or 5.0
    longest = max(hi - lo)
    k = (size / longest) if longest > 0 else 0.0

    rot = e.get("rotation") or {}
    R = (Matrix.Rotation(math.radians(rot.get("y", 0.0)), 4, 'Z')
         @ Matrix.Rotation(math.radians(rot.get("x", 0.0)), 4, 'X')
         @ Matrix.Rotation(math.radians(rot.get("z", 0.0)), 4, 'Y'))
    centre = R @ ((lo + hi) / 2)
    lowest = min((R @ p).z for p in pts)

    move_x, move_y, lift = -centre.x * k, -centre.y * k, -lowest * k
    pos = e.get("position") or {}
    old = (pos.get("x", 0.0), pos.get("y", 0.0), pos.get("z", 0.0))
    grounded = bool(e.get("autoGroundWheels"))       # RETIRED as a toggle, but it records what the last bake did
    new = (old[0] + move_x, old[1] + move_y, old[2] if grounded else old[2] - lift)

    moved = max(abs(new[i] - old[i]) for i in range(3))
    note = "unchanged" if moved <= 0.05 else ("re-dial (%.2f)" % moved)
    if not grounded and abs(lift) > 0.05:
        note += ", gains grounding"
    print("PLACEMENT %-22s (%+.2f, %+.2f, %+.2f)%s(%+.2f, %+.2f, %+.2f)%s%s"
          % (name, old[0], old[1], old[2], "        ", new[0], new[1], new[2], "        ", note))

print("PLACEMENT " + "-" * 92)
print("PLACEMENT Nothing moves until an entry is RE-BAKED. An entry already re-baked since the change is done —")
print("PLACEMENT its dial is already in the new frame, and this table would move it a second time.")
print("PLACEMENT Verify on the entry with the LARGEST change first: if it lands right, the rest follow.")
