import bpy, sys, mathutils

argv = sys.argv[sys.argv.index("--") + 1:]
glb_path, out_path = argv[0], argv[1]
res = int(argv[2]) if len(argv) > 2 else 512

# empty scene
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

# import the model
bpy.ops.import_scene.gltf(filepath=glb_path)
meshes = [o for o in scene.objects if o.type == 'MESH']
if not meshes:
    print("NO MESHES"); sys.exit(1)

# world-space bbox
mn = mathutils.Vector((1e9, 1e9, 1e9))
mx = mathutils.Vector((-1e9, -1e9, -1e9))
for o in meshes:
    for c in o.bound_box:
        w = o.matrix_world @ mathutils.Vector(c)
        for i in range(3):
            mn[i] = min(mn[i], w[i]); mx[i] = max(mx[i], w[i])
# strip the flat ground/base plane so the silhouette is the BUILDING layout (domes/halls/towers), not a rectangle.
# delete faces sitting at/near the base level; anything that rises above it is kept.
import bmesh
z_cut = mn.z + (mx.z - mn.z) * float(argv[3]) if len(argv) > 3 else mn.z + (mx.z - mn.z) * 0.06
kept_faces = 0; del_faces = 0
for o in meshes:
    mw = o.matrix_world
    bm = bmesh.new(); bm.from_mesh(o.data)
    todel = []
    for f in bm.faces:
        cz = (mw @ f.calc_center_median()).z
        if cz < z_cut: todel.append(f)
        else: kept_faces += 1
    del_faces += len(todel)
    bmesh.ops.delete(bm, geom=todel, context='FACES')
    bm.to_mesh(o.data); bm.free()
print("Z-CUT=%.2f  deleted=%d kept=%d faces" % (z_cut, del_faces, kept_faces))

center = (mn + mx) * 0.5
size_x = mx.x - mn.x
size_y = mx.y - mn.y
span = max(size_x, size_y) * 1.02   # tiny margin
print("BBOX center=%s  x=%.2f y=%.2f z=%.2f  span=%.2f" % (center, size_x, size_y, mx.z - mn.z, span))

# flat white emission on everything -> the alpha is the silhouette mask
white = bpy.data.materials.new("SilhouetteWhite")
white.use_nodes = True
nt = white.node_tree
for n in list(nt.nodes): nt.nodes.remove(n)
emit = nt.nodes.new("ShaderNodeEmission"); emit.inputs[0].default_value = (1, 1, 1, 1)
out = nt.nodes.new("ShaderNodeOutputMaterial")
nt.links.new(emit.outputs[0], out.inputs[0])
for o in meshes:
    o.data.materials.clear(); o.data.materials.append(white)

# ORTHO camera straight down (-Z), above the model
cam_data = bpy.data.cameras.new("TopCam"); cam_data.type = 'ORTHO'
cam_data.ortho_scale = span
cam = bpy.data.objects.new("TopCam", cam_data)
scene.collection.objects.link(cam)
cam.location = (center.x, center.y, mx.z + 10.0)
cam.rotation_euler = (0.0, 0.0, 0.0)   # looks down -Z
scene.camera = cam

# render: transparent film, RGBA PNG, EEVEE (fast, flat)
for eng in ('BLENDER_EEVEE_NEXT', 'BLENDER_EEVEE', 'CYCLES'):
    try:
        scene.render.engine = eng
        print("ENGINE " + eng); break
    except Exception:
        continue
# transparent film -> the alpha channel IS the silhouette mask (white+opaque where the reactor is)
scene.render.film_transparent = True
scene.render.resolution_x = res
scene.render.resolution_y = res
scene.render.image_settings.file_format = 'PNG'
scene.render.image_settings.color_mode = 'RGBA'
scene.view_settings.view_transform = 'Standard'   # no filmic, keep flat white
scene.render.filepath = out_path
bpy.ops.render.render(write_still=True)
print("WROTE " + out_path)
