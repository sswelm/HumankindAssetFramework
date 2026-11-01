import bpy, sys
argv = sys.argv[sys.argv.index("--") + 1:]
inp, outp = argv[0], argv[1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=inp)
objs = list(bpy.data.objects)
print("FBX2GLB objects:", len(objs), "meshes:", sum(1 for o in objs if o.type == 'MESH'), "armatures:", sum(1 for o in objs if o.type == 'ARMATURE'), "actions:", len(bpy.data.actions), "materials:", len(bpy.data.materials))
for o in objs[:12]:
    print("  obj", o.type, repr(o.name), "parent", repr(o.parent.name) if o.parent else None, "verts", len(o.data.vertices) if o.type == 'MESH' else '-', "polys", len(o.data.polygons) if o.type == 'MESH' else '-')
bpy.ops.export_scene.gltf(filepath=outp, export_format='GLB', use_selection=False)
print("FBX2GLB wrote", outp)
