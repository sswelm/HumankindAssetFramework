"""The scene rows the stage dumps print (tools/riganim-drill/blender_riganim_dump.py, tools/vehiclerig-drill/
blender_vehiclerig_dump.py): one encoding for every float (the hex of its float32 bits), one row shape per kind - SCENE, OBJ,
MESH (with every UV layer), VG, BONE, PB, ACT, FC (and K under KEYS), IMG - so a C# model of either script is held to the same
evidence. Set KEYS before dumping; dump(n) reads bpy.context.scene."""
import bpy, os, struct, hashlib
import numpy as np

KEYS = False
FC_SLOT = False   # the FC and K rows carry the fcurve's slot identifier after the action (a slotted action's fcurves repeat a path per slot)


def h32(v): return struct.pack(">f", float(v)).hex()


def hv(xs): return "\t".join(h32(x) for x in xs)


def hm(m): return "\t".join(h32(m[r][c]) for r in range(4) for c in range(4))


def sha(b): return hashlib.sha256(b).hexdigest()


def f32(a): return np.asarray(a, dtype=np.float32).tobytes()


def fcurves_of(action):
    """Every fcurve of a (Blender 5 slotted) action, with its owning collection - the script's all_fcurve_owners."""
    return [fc for fc, _ in fcurves_with_slots(action)]


def fcurves_with_slots(action):
    """Every fcurve with its slot's identifier ("-" for a legacy action's own fcurves)."""
    if getattr(action, "fcurves", None) is not None and len(action.fcurves):
        return [(fc, "-") for fc in action.fcurves]
    out = []
    slots = {s.handle: s.identifier for s in getattr(action, "slots", [])}
    for layer in getattr(action, "layers", []):
        for strip in layer.strips:
            for cb in getattr(strip, "channelbags", []):
                ident = slots.get(getattr(cb, "slot_handle", None), "-")
                out.extend((fc, ident) for fc in cb.fcurves)
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
            layers = []
            for l in me.uv_layers:
                u = np.empty(len(me.loops) * 2, dtype=np.float32); l.data.foreach_get("uv", u); layers.append("%s:%s" % (l.name, sha(u.tobytes())))
            # the edges in order (v1, v2), the sharp_edge set (absent on a mesh with no edge marked) and the custom-normal shorts
            # per corner (absent without custom normals): the weld's order and marks, the data the export reads (step (c) review)
            ev = np.empty(len(me.edges) * 2, dtype=np.int32); me.edges.foreach_get("vertices", ev)
            sharp = "-"
            se = me.attributes.get("sharp_edge")
            if se is not None and se.domain == 'EDGE' and se.data_type == 'BOOLEAN':
                sb = np.empty(len(me.edges), dtype=bool); se.data.foreach_get("value", sb); sharp = sha(sb.astype(np.uint8).tobytes())
            cns = "-"
            cn = me.attributes.get("custom_normal")
            if cn is not None and cn.domain == 'CORNER' and cn.data_type == 'INT16_2D':
                ca = np.empty(len(me.loops) * 2, dtype=np.int32); cn.data.foreach_get("value", ca); cns = sha(ca.astype("<i2").tobytes())
            rows.append("MESH\t%d\t%s\t%d\t%d\t%d\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s" % (
                n, o.name, len(me.vertices), len(me.polygons), len(me.loops), sha(pos.tobytes()), sha(lv.astype("<i4").tobytes()),
                sha(ps.astype("<i4").tobytes() + pt.astype("<i4").tobytes()), sha(mi.astype("<i4").tobytes()), uv,
                ";".join(m.name if m else "(none)" for m in me.materials) or "-",
                ";".join(g.name for g in o.vertex_groups) or "-",
                ";".join("%s:%s:%s" % (md.type, md.name, getattr(md, "object", None).name if getattr(md, "object", None) else "-") for md in o.modifiers) or "-",
                ";".join(layers) or "-", sha(ev.astype("<i4").tobytes()), sharp, cns))
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
        fcs = fcurves_with_slots(a)
        rows.append("ACT\t%d\t%s\t%s\t%d\t%d" % (n, a.name, hv(a.frame_range), len(fcs), len(getattr(a, "slots", []) or [])))
        for fc, slot in fcs:
            ident = ("%s\t%s" % (a.name, slot)) if FC_SLOT else a.name
            k = len(fc.keyframe_points)
            co = np.empty(k * 2, dtype=np.float32); fc.keyframe_points.foreach_get("co", co)
            hl = np.empty(k * 2, dtype=np.float32); fc.keyframe_points.foreach_get("handle_left", hl)
            hr = np.empty(k * 2, dtype=np.float32); fc.keyframe_points.foreach_get("handle_right", hr)
            meta = "|".join("%s/%s/%s/%s" % (kp.interpolation, kp.easing, kp.handle_left_type, kp.handle_right_type) for kp in fc.keyframe_points)
            rows.append("FC\t%d\t%s\t%s\t%d\t%d\t%s\t%s" % (n, ident, fc.data_path, fc.array_index, k, sha(co.tobytes()), sha(hl.tobytes() + hr.tobytes() + meta.encode("utf-8"))))
            if KEYS:
                for j, kp in enumerate(fc.keyframe_points):
                    rows.append("K\t%d\t%s\t%s\t%d\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s" % (
                        n, ident, fc.data_path, fc.array_index, h32(co[2 * j]), h32(co[2 * j + 1]), h32(hl[2 * j]), h32(hl[2 * j + 1]),
                        h32(hr[2 * j]), h32(hr[2 * j + 1]), kp.interpolation, kp.easing, kp.handle_left_type, kp.handle_right_type))
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


