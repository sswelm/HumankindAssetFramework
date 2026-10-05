"""Blender's own Decimate COLLAPSE for MANY files in ONE process (tools/decimate-drill, step 5 milestone c): each file
imported as the Lab imports it (the glTF importer, factory settings); for every mesh object of more than 3 faces - the
importer's bone-shape "Icosphere" aside - and for each of three ratios, a copy of the object gets a Decimate modifier
(COLLAPSE, the ratio, nothing else) applied as prep_model.py applies it - bpy.ops.object.modifier_apply with its defaults,
whose merge_customdata snaps the UVs of a vertex's corners within 12 ulps together - and the result is written down as
DecimateDrill.cs writes it:

    DEC <TAB> <file key> <TAB> <object name> <TAB> <ratio tag> <TAB> <ratio as float32 bits> <TAB> <verts> <TAB> <faces>
        <TAB> <sha1 positions> <TAB> <sha1 faces> <TAB> <sha1 UVs> <TAB> <sha1 custom normals> <TAB> <sha1 face material+sharp>
        <TAB> <sha1 vertex groups> <TAB> <sha1 colour layers> <TAB> <first 3 vertices>

Positions are the vertices' float32 coordinates in order (little-endian bytes); faces the polygons' corner vertex indices
(int32); UVs every UV layer's float32 pairs in layer order; custom normals the "custom_normal" int16 pairs per corner (or
"none"); face data "material,sharp" per face; vertex groups "group:weight-bits ..." per vertex in dw order; colour layers
sorted by name, each its name, domain and sRGB bytes. The ratios: one (the modifier returns the mesh untouched; the apply
still merges), half (0.5) and third (prep_model's `min(1, max(0.001, target / total))` with target = total // 3, total the
file's triangle count over all mesh objects), each stored as the modifier stores it (float32).

usage: blender --background --python blender_decimate_many.py -- <file>...
"""
import bpy, hashlib, os, struct, sys
import numpy as np

sys.stdout.reconfigure(encoding="utf-8")


def f32bits(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]


def sha(b):
    return hashlib.sha1(b).hexdigest()


def dump_mesh(me, o):
    nv = len(me.vertices); nf = len(me.polygons)
    co = np.empty(nv * 3, dtype=np.float32); me.vertices.foreach_get("co", co)
    h_pos = sha(co.tobytes())
    tot = np.empty(nf, dtype=np.int32); me.polygons.foreach_get("loop_total", tot)
    if nf and (tot != 3).any():
        return None, "a polygon that is not a triangle"
    fv = np.empty(nf * 3, dtype=np.int32); me.polygons.foreach_get("vertices", fv)
    h_faces = sha(fv.tobytes())
    uvh = hashlib.sha1()
    for layer in me.uv_layers:
        uv = np.empty(len(me.loops) * 2, dtype=np.float32); layer.uv.foreach_get("vector", uv); uvh.update(uv.tobytes())
    h_uv = uvh.hexdigest() if len(me.uv_layers) else "none"
    cn = me.attributes.get("custom_normal")
    if cn is not None and cn.domain == 'CORNER' and cn.data_type == 'INT16_2D':
        arr = np.empty(len(me.loops) * 2, dtype=np.int32); cn.data.foreach_get("value", arr)
        h_cn = sha(arr.astype(np.int16).tobytes())
    else:
        h_cn = "none"
    mat = np.empty(nf, dtype=np.int32); me.polygons.foreach_get("material_index", mat)
    sf = me.attributes.get("sharp_face")
    sharp = np.zeros(nf, dtype=bool)
    if sf is not None:
        sf.data.foreach_get("value", sharp)
    h_face = sha("".join("%d,%d\n" % (mat[i], 1 if sharp[i] else 0) for i in range(nf)).encode("utf-8"))
    gh = hashlib.sha1()
    for v in me.vertices:
        gh.update((" ".join("%d:%08x" % (g.group, f32bits(g.weight)) for g in v.groups) + "\n").encode("utf-8"))
    h_groups = gh.hexdigest() if o.vertex_groups else "none"
    if len(me.color_attributes):
        ch = hashlib.sha1()
        for a in sorted(me.color_attributes, key=lambda a: a.name):
            if a.data_type != 'BYTE_COLOR':
                return None, "a colour layer that is not BYTE_COLOR"
            arr = np.empty(len(a.data) * 4, dtype=np.float32); a.data.foreach_get("color_srgb", arr)
            b = np.round(arr.astype(np.float64) * 255.0).astype(np.uint8)
            ch.update((a.name + "\n" + ("POINT" if a.domain == 'POINT' else "CORNER") + "\n").encode("utf-8")); ch.update(b.tobytes())
        colours = ch.hexdigest()
    else:
        colours = "none"
    first = " ".join("%08x,%08x,%08x" % (f32bits(co[3 * i]), f32bits(co[3 * i + 1]), f32bits(co[3 * i + 2])) for i in range(min(3, nv)))
    return (nv, nf, h_pos, h_faces, h_uv, h_cn, h_face, h_groups, colours, first), None


files = sys.argv[sys.argv.index("--") + 1:]
fails = 0
for path in files:
    key = path.replace("\\", "/").lower()
    try:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        ext = os.path.splitext(path)[1].lower()
        if ext in (".glb", ".gltf"):
            bpy.ops.import_scene.gltf(filepath=path)
        else:
            raise RuntimeError("not a glTF: " + ext)
        meshes = []
        for o in bpy.context.scene.objects:
            if o.type != 'MESH' or len(o.data.vertices) == 0:
                continue
            if o.name.startswith('Icosphere') and not o.vertex_groups and len(o.data.vertices) in (12, 42, 162, 642):
                continue
            meshes.append(o)
        total = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in meshes)   # prep_model's tri_count over the scene
        third = min(1.0, max(0.001, (total // 3) / max(1, total)))
        for o in meshes:
            if len(o.data.polygons) <= 3:
                continue   # MOD_decimate: "requires more than 3 input faces" - the drill compares the objects it collapses
            for tag, ratio in (("one", 1.0), ("half", 0.5), ("third", third)):
                o2 = o.copy(); me2 = o.data.copy(); o2.data = me2
                bpy.context.scene.collection.objects.link(o2)
                bpy.ops.object.select_all(action='DESELECT')
                o2.select_set(True)
                bpy.context.view_layer.objects.active = o2
                m = o2.modifiers.new("dec", 'DECIMATE')
                m.decimate_type = 'COLLAPSE'
                m.ratio = ratio
                bits = f32bits(m.ratio)
                bpy.ops.object.modifier_apply(modifier=m.name)
                row, why = dump_mesh(o2.data, o2)
                if row is None:
                    print("DEC\t%s\t%s\t%s\t%08x\tSKIP\t%s" % (key, o.name, tag, bits, why), flush=True)
                else:
                    print("DEC\t%s\t%s\t%s\t%08x\t%d\t%d\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s" % ((key, o.name, tag, bits) + row), flush=True)
                bpy.data.objects.remove(o2); bpy.data.meshes.remove(me2)
        print("FILE\t%s\tok" % key, flush=True)
    except Exception as e:
        print("FAIL\t%s\t%s: %s" % (key, type(e).__name__, e), flush=True)
        fails += 1
sys.exit(1 if fails else 0)
