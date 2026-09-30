# Blender's view of the same files, one line per file in the drill's format (the reference the C# reader is compared
# with): triangles, materials, images, bones, actions, and the importer's own time. One Blender process for every file:
# the 1.6 s boot is paid once; the scene is reset between files.
import bpy, os, sys, time
files = sys.argv[sys.argv.index("--") + 1:]
for path in files:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    t = time.time()
    bpy.ops.import_scene.gltf(filepath=path)
    ms = (time.time() - t) * 1000.0
    # the importer's bone-display custom shape (an 80-face icosphere per skinned file) is not in the file: skip meshes used as one
    shapes = set()
    for o in bpy.data.objects:
        if o.type == "ARMATURE":
            for pb in o.pose.bones:
                if pb.custom_shape is not None:
                    shapes.add(pb.custom_shape.name)
    tris = 0
    for o in bpy.data.objects:
        if o.type != "MESH" or o.name in shapes:
            continue
        me = o.data
        me.calc_loop_triangles()
        tris += len(me.loop_triangles)
    bones = sum(len(o.data.bones) for o in bpy.data.objects if o.type == "ARMATURE")
    images = len([i for i in bpy.data.images if i.name not in ("Render Result", "Viewer Node")])
    name = os.path.abspath(path).replace("\\", "/").lower()
    print("BLENDER\t%s\ttris=%d\tmaterials=%d\timages=%d\tjoints=%d\tanimations=%d\tms=%.0f" % (name, tris, len(bpy.data.materials), images, bones, len(bpy.data.actions), ms))
