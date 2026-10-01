# Blender's view of the same files, one line per file in the drill's format (the reference the C# reader is compared
# with). The counts (triangles, materials, images, bones, actions) and the VALUES a count cannot vouch for, all computed
# in WORLD space through Blender's own matrix chain, order-independent so vertex merging cannot move them:
#   bbox      the bounding box of every mesh vertex
#   area      the total triangle area (positions and transforms)
#   centroid  the area-weighted centroid of every triangle (positions and transforms, weighted)
#   nsum      the area-weighted sum of face normals, from each triangle's own winding (winding and mirrored nodes)
#   bones     the armatures' bone names, sorted;  durations  the actions' lengths, sorted
# Blender's importer turns glTF's +Y up into +Z up, so the C# side is compared in that frame. One Blender process for
# every file: the 1.6 s boot is paid once; the scene is reset between files.
import bpy, os, sys, time
from mathutils import Vector
files = sys.argv[sys.argv.index("--") + 1:]
for path in files:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    t = time.time()
    bpy.ops.import_scene.gltf(filepath=path)
    ms = (time.time() - t) * 1000.0
    # THE POSE: animation 0 at time 0, the one state both sides can compute exactly. The importer puts EVERY clip on an
    # NLA track and leaves the first one active, so its untouched scene at frame 1 is a blend of all clips 42 ms in -
    # a pose no file defines. Drop the NLA tracks (the active action stays), go to frame 0 (= the clip's first key),
    # and keep the pose bones as evaluated: that is the C# side's PoseAt(animation 0, t = 0) through the joint blend.
    fps = bpy.context.scene.render.fps
    durations = sorted(set(round((a.frame_range[1] - a.frame_range[0]) / fps, 3) for a in bpy.data.actions))
    action_count = len(bpy.data.actions)
    for o in bpy.data.objects:
        ad = o.animation_data
        if ad is None:
            continue
        for track in list(ad.nla_tracks):
            ad.nla_tracks.remove(track)
    bpy.context.scene.frame_set(0)
    bpy.context.view_layer.update()
    # the importer's bone-display custom shape (an 80-face icosphere per skinned file) is not in the file: skip meshes used as one
    shapes = set()
    for o in bpy.data.objects:
        if o.type == "ARMATURE":
            for pb in o.pose.bones:
                if pb.custom_shape is not None:
                    shapes.add(pb.custom_shape.name)
    tris = 0
    mn = [float("inf")] * 3
    mx = [float("-inf")] * 3
    area = 0.0
    cen = [0.0, 0.0, 0.0]
    nsum = [0.0, 0.0, 0.0]
    # the EVALUATED mesh: modifiers applied - for a skinned mesh the armature at its rest pose - which is what Blender
    # shows and what the scripts operate on. The undeformed mesh data would miss a skin whose rest pose is not its bind pose.
    dg = bpy.context.evaluated_depsgraph_get()
    for o in bpy.data.objects:
        if o.type != "MESH" or o.name in shapes:
            continue
        eo = o.evaluated_get(dg)
        me = eo.to_mesh()
        me.calc_loop_triangles()
        tris += len(me.loop_triangles)
        mw = eo.matrix_world
        wv = [mw @ v.co for v in me.vertices]
        for p in wv:
            for i in range(3):
                if p[i] < mn[i]: mn[i] = p[i]
                if p[i] > mx[i]: mx[i] = p[i]
        for lt in me.loop_triangles:
            a, b, c = (wv[i] for i in lt.vertices)
            n = (b - a).cross(c - a)          # twice the area, along the face normal as wound
            ta = n.length * 0.5
            area += ta
            m = (a + b + c) / 3.0
            cen[0] += m.x * ta; cen[1] += m.y * ta; cen[2] += m.z * ta
            nsum[0] += n.x * 0.5; nsum[1] += n.y * 0.5; nsum[2] += n.z * 0.5
        eo.to_mesh_clear()
    if area > 0:
        cen = [c / area for c in cen]
    bones = sorted(b.name for o in bpy.data.objects if o.type == "ARMATURE" for b in o.data.bones)
    images = len([i for i in bpy.data.images if i.name not in ("Render Result", "Viewer Node")])
    name = os.path.abspath(path).replace("\\", "/").lower()
    f = lambda xs: ",".join("%.5f" % x for x in xs)
    print("BLENDER\t%s\ttris=%d\tmaterials=%d\timages=%d\tjoints=%d\tanimations=%d\tms=%.0f\tbbox=%s\tarea=%.5f\tcentroid=%s\tnsum=%s\tbones=%s\tdurations=%s"
          % (name, tris, len(bpy.data.materials), images, len(bones), action_count, ms,
             f(mn + mx) if tris else "", area, f(cen), f(nsum), "|".join(bones), ",".join("%.3f" % d for d in durations)))
