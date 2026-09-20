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
#     new_x = old_x + move_x        new_y = old_y + move_y        new_z = old_z - lift   (only if NOT auto-grounded)
#
# An entry whose dial was PURE compensation lands on ~0, which is the tell that the sign is right. Measured on the
# shipped ENC pack, four independently hand-dialed entries collapse at once: GatlingGuns -3.70 + 3.34 = -0.36,
# AntiTankIFV +0.50 - 0.497 = +0.003, StealthHelicopter (-0.50, +0.50) + (+0.391, -0.525) = (-0.11, -0.02),
# TOW-Infantry -0.30 + 0.247 = -0.05. With the sign the other way those would DOUBLE (GatlingGuns to -7.04 on a
# size-2.5 model), which no one would have shipped.
#
# MEASURING THE SAME THING THE BAKE MEASURES — the two ways a migration tool gets this quietly wrong (PR #72
# review, both found there):
#
#   1. ROTATE THE GEOMETRY FIRST. rig_anim folds the registry rotation into the data and measures afterwards, so
#      the box is the ROTATED cloud's box. Rotating an unrotated box's CENTRE is not the same thing for asymmetric
#      geometry, and the longest axis — hence size/longest — differs too. Drilled on a size-5 triangular prism at
#      45 degrees: rotating the centre gave (+0.589, -2.946) at scale 0.8333, the bake's order (+0.500, -1.500) at
#      0.7071 — 1.45 game units of error. At 0/+-90/180 the two agree to 0.00000, which is why a pack of
#      axis-aligned entries shows no symptom and the bug waits for the first model dialed to an odd angle.
#
#   2. MEASURE THE REFERENCE POSE, not the file's raw rest. The rest skeleton is built from the Idle/reference
#      clip's frame, and that pose IS the geometry the bake places: reference a clip holding a struck or folded
#      pose and the lowest point becomes a yard under the hull, not the keel (the sky-lift trap). This applies the
#      entry's own animClip frame before measuring.
#
# WHAT IT STILL CANNOT REPRODUCE, and says so rather than printing a confident wrong number: a deploy-converted
# recipe (deployConvert), whose rig and clips are synthesized by a separate conversion. Those rows are reported as
# unsupported. The ground truth for any entry is the bake's own log line, "RIGANIM placement: world centre ...",
# which you can compare against this table after the first re-bake.
import bpy
import json
import math
import os
import re
import sys

from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index("--") + 1:]
if not argv:
    print("PLACEMENT usage: blender --background --python placement_shift.py -- <pack.json>")
    sys.exit(2)

pack_path = argv[0]
pack = json.load(open(pack_path, encoding="utf-8"))
models = pack.get("models", []) if isinstance(pack, dict) else pack


def load(path):
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
    # the same junk cull rig_anim does on import (the glTF importer's placeholder icosphere is not in the file)
    for junk in [o for o in bpy.context.scene.objects if o.type == 'MESH' and len(o.data.materials) == 0]:
        bpy.data.objects.remove(junk, do_unlink=True)


class Unresolved(Exception):
    """The reference pose could not be reproduced — measuring anyway would print a confident wrong number."""


# THE BAKER'S OWN SLICE GRAMMAR, copied verbatim from rig_anim.py's resolve_clip so the two cannot disagree:
# a clip may carry a frame range AND a speed step — "deploy[179..0/3]" is every 3rd source frame. A parser that
# only understands "[a..b]" silently fails to resolve "Spin[10..30/2]" (PR #72 review), and a reference the tool
# cannot resolve is exactly when it must NOT print a number.
_SLICE_RE = re.compile(r"^(.*)\[(\d+)\.\.(\d+)(?:/(\d+))?\]$")


def reference_frame(clip):
    """The (action, frame) the bake builds its rest skeleton from, or (None, why-not)."""
    spec = (clip or "").strip()
    if not spec:
        return None, "no reference clip configured"
    m = _SLICE_RE.match(spec)
    name, frame = (m.group(1), int(m.group(2))) if m else (spec, None)
    act = bpy.data.actions.get(name)          # exact, as resolve_clip matches — a near-miss is a misconfiguration
    if act is None:
        return None, "reference clip '%s' is not in the file" % name
    return act, (int(act.frame_range[0]) if frame is None else frame)


def pose_to_reference(clip):
    """Put the rig in the reference clip's frame — the pose the bake turns into the rest skeleton.

    Returns a note on success, or raises Unresolved so the row is reported as NOT MEASURED rather than
    measured against whatever pose the file happens to open in."""
    arm = next((o for o in bpy.context.scene.objects if o.type == 'ARMATURE'), None)
    if arm is None:
        raise Unresolved("no armature in the file")
    act, frame = reference_frame(clip)
    if act is None:
        raise Unresolved(frame)
    arm.animation_data_create()
    arm.animation_data.action = act
    bpy.context.scene.frame_set(frame)
    bpy.context.view_layer.update()
    return "posed at %s frame %d" % (act.name.split("|")[-1], frame)


def measure(rot):
    """The bake's own measurement: the POSED, ROTATED cloud's box."""
    R = (Matrix.Rotation(math.radians(rot.get("y", 0.0)), 4, 'Z')
         @ Matrix.Rotation(math.radians(rot.get("x", 0.0)), 4, 'X')
         @ Matrix.Rotation(math.radians(rot.get("z", 0.0)), 4, 'Y'))
    dg = bpy.context.evaluated_depsgraph_get()
    pts = []
    for o in bpy.context.scene.objects:
        if o.type != 'MESH':
            continue
        ev = o.evaluated_get(dg)           # the armature modifier applied: the reference pose's geometry
        if not len(ev.data.vertices):
            continue
        mw = o.matrix_world
        pts += [R @ (mw @ v.co) for v in ev.data.vertices]
    if not pts:
        raise RuntimeError("no mesh left after the junk cull")
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return (lo + hi) / 2, lo.z, max(hi - lo)


print("PLACEMENT pack: %s" % pack_path)
print("PLACEMENT %-22s %-24s %-24s %s" % ("entry", "Position today", "Position after re-bake", "note"))
print("PLACEMENT " + "-" * 100)

for e in models:
    if not e.get("animated"):
        continue
    name = e.get("resourceName") or "?"
    src = e.get("modelFile") or ""
    if not os.path.exists(src):
        print("PLACEMENT %-22s model file not found: %s" % (name, src))
        continue
    if e.get("deployConvert"):
        print("PLACEMENT %-22s NOT MEASURED — deploy conversion rebuilds the rig and clips; re-bake and read the "
              "bake's own 'RIGANIM placement:' line" % name)
        continue
    try:
        load(src)
        posed = pose_to_reference(e.get("animClip"))
        centre, lowest, longest = measure(e.get("rotation") or {})
    except Unresolved as why:
        print("PLACEMENT %-22s NOT MEASURED — %s; re-bake and read the bake's own 'RIGANIM placement:' line"
              % (name, why))
        continue
    except Exception as ex:
        print("PLACEMENT %-22s could not measure (%s)" % (name, str(ex)[:70]))
        continue

    size = e.get("size") or 5.0
    k = (size / longest) if longest > 0 else 0.0
    move_x, move_y, lift = -centre.x * k, -centre.y * k, -lowest * k
    pos = e.get("position") or {}
    old = (pos.get("x", 0.0), pos.get("y", 0.0), pos.get("z", 0.0))
    grounded = bool(e.get("autoGroundWheels"))   # RETIRED as a toggle, but it records what the last bake did
    new = (old[0] + move_x, old[1] + move_y, old[2] if grounded else old[2] - lift)

    moved = max(abs(new[i] - old[i]) for i in range(3))
    note = "unchanged" if moved <= 0.05 else "re-dial (%.2f)" % moved
    if not grounded and abs(lift) > 0.05:
        note += ", gains grounding"
    print("PLACEMENT %-22s (%+.2f, %+.2f, %+.2f)        (%+.2f, %+.2f, %+.2f)        %s [%s]"
          % (name, old[0], old[1], old[2], new[0], new[1], new[2], note, posed))

print("PLACEMENT " + "-" * 100)
print("PLACEMENT Nothing moves until an entry is RE-BAKED. An entry already re-baked since the change is done —")
print("PLACEMENT its dial is already in the new frame, and this table would move it a second time.")
print("PLACEMENT Verify on the entry with the LARGEST change first: if it lands right, the rest follow. The bake's")
print("PLACEMENT own 'RIGANIM placement: world centre ...' line is the ground truth to check a row against.")
