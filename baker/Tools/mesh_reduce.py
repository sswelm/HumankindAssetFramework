# mesh_reduce.py — Model Factory vertex reducer. Headless Blender: import any model (glb/gltf/obj/fbx), quadric-collapse
# each mesh PER-OBJECT to hit ~targetTris total, and export a reduced GLB the Factory then bakes. Used to fit the
# engine's shared vertex/index buffer (~100k verts / ~83k tris across ALL injected models).
#
# Pure quadric COLLAPSE (not planar dissolve): collapse preserves distinct curved features like a hovercraft skirt.
# Planar dissolve flattens gentle curves (it merges near-coplanar faces), which destroyed the skirt — do NOT use it here.
#
# Invoked by UniversalBaker.ReduceViaBlender:  blender --background --python mesh_reduce.py -- <in> <out.glb> <targetTris>

import bpy, sys, os

argv = sys.argv[sys.argv.index("--") + 1:]
inp, outp, target = argv[0], argv[1], int(argv[2])

bpy.ops.wm.read_factory_settings(use_empty=True)

ext = os.path.splitext(inp)[1].lower()
if ext in (".glb", ".gltf"):
    bpy.ops.import_scene.gltf(filepath=inp)
elif ext == ".fbx":
    bpy.ops.import_scene.fbx(filepath=inp)
elif ext == ".obj":
    try:
        bpy.ops.wm.obj_import(filepath=inp)
    except Exception:
        bpy.ops.import_scene.obj(filepath=inp)
else:
    print("REDUCE_ERR unsupported input " + ext); sys.exit(1)

meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
if not meshes:
    print("REDUCE_ERR no meshes imported"); sys.exit(1)

total = sum(len(o.data.polygons) for o in meshes)
ratio = min(1.0, max(0.001, target / max(1, total)))

before = after = 0
for o in meshes:
    before += len(o.data.polygons)
    bpy.ops.object.select_all(action='DESELECT')
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    m = o.modifiers.new("dec", 'DECIMATE')
    m.decimate_type = 'COLLAPSE'
    m.ratio = ratio
    bpy.ops.object.modifier_apply(modifier=m.name)
    after += len(o.data.polygons)

print(f"REDUCE_OK tris {before} -> {after} (target {target}, ratio {ratio:.4f})")
bpy.ops.export_scene.gltf(filepath=outp, export_format='GLB', use_selection=False)
print(f"REDUCE_WROTE {outp}")
