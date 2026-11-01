import bpy, sys, json, struct, hashlib
argv = sys.argv[sys.argv.index("--") + 1:]
inp, outp = argv[0], argv[1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=inp)
objs = list(bpy.data.objects)
print("FBX2GLB objects:", len(objs), "meshes:", sum(1 for o in objs if o.type == 'MESH'), "armatures:", sum(1 for o in objs if o.type == 'ARMATURE'), "actions:", len(bpy.data.actions), "materials:", len(bpy.data.materials))
for o in objs[:12]:
    print("  obj", o.type, repr(o.name), "parent", repr(o.parent.name) if o.parent else None, "verts", len(o.data.vertices) if o.type == 'MESH' else '-', "polys", len(o.data.polygons) if o.type == 'MESH' else '-')
# Observe the exporter's own image decisions. Equal bytes in an unrelated input image cannot tell us whether
# an exported image was composed. The wrapper returns the original objects without modifying the export.
from io_scene_gltf2.blender.exp.material import image as image_export
original_gather = image_export.gather_image
decisions = {}


def traced_gather(*args, **kwargs):
    result = original_gather(*args, **kwargs)
    image, export_image = result[:2]
    if image is not None:
        key = (image.name or '', image.mime_type or '', hashlib.sha256(image.buffer_view.data).hexdigest())
        composed = not export_image._ExportImage__on_happy_path()
        decisions.setdefault(key, set()).add(composed)
    return result


image_export.gather_image = traced_gather
try:
    bpy.ops.export_scene.gltf(filepath=outp, export_format='GLB', use_selection=False)
finally:
    image_export.gather_image = original_gather

with open(outp, 'rb') as stream:
    glb = stream.read()
json_length = struct.unpack_from('<I', glb, 12)[0]
document = json.loads(glb[20:20 + json_length])
rows = []
for image in document.get('images', []):
    view = document['bufferViews'][image['bufferView']]
    start = 28 + json_length + view.get('byteOffset', 0)
    digest = hashlib.sha256(glb[start:start + view['byteLength']]).hexdigest()
    key = (image.get('name', ''), image.get('mimeType', ''), digest)
    if key not in decisions:
        raise RuntimeError('No exporter composition evidence for image ' + repr(key))
    rows.append(dict(name=key[0], mimeType=key[1], sha256=digest, composed=sorted(decisions[key])))
with open(outp + '.images.json', 'w', encoding='utf-8') as stream:
    json.dump(dict(version=1, images=rows), stream, ensure_ascii=False)
print("FBX2GLB wrote", outp)
