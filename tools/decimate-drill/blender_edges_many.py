"""Blender's own mesh layout for MANY files in ONE process (tools/decimate-drill, step 5 milestone a): each file imported
as the Lab imports it (the glTF importer, factory settings), and for every mesh object with vertices - the importer's
bone-shape "Icosphere" aside, as the probe purges it - one row as EdgesDrill.cs prints it:

    MESH <TAB> <file key> <TAB> <object name> <TAB> <verts> <TAB> <edges> <TAB> <faces> <TAB> <sha1 of the edge pairs> <TAB> <the first 8 edges>

The edge pairs are `mesh.edges[i].vertices` in index order, each as (v0, v1) as Blender stores them, hashed as little-endian
int32 pairs - the layout the Decimate modifier's heap sees.

usage: blender --background --python blender_edges_many.py -- <file>...
"""
import bpy, hashlib, os, struct, sys

sys.stdout.reconfigure(encoding="utf-8")
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
        for o in bpy.context.scene.objects:
            if o.type != 'MESH' or len(o.data.vertices) == 0:
                continue
            if o.name.startswith('Icosphere') and not o.vertex_groups and len(o.data.vertices) in (12, 42, 162, 642):
                continue   # the importer's bone-shape placeholder (the probe purges it by signature; the drill's files have no real one)
            me = o.data
            n = len(me.edges)
            flat = [0] * (2 * n)
            me.edges.foreach_get("vertices", flat)
            h = hashlib.sha1(struct.pack("<%di" % len(flat), *flat)).hexdigest()
            first = " ".join("%d:%d" % (flat[2 * i], flat[2 * i + 1]) for i in range(min(8, n)))
            print("MESH\t%s\t%s\t%d\t%d\t%d\t%s\t%s" % (key, o.name, len(me.vertices), n, len(me.polygons), h, first), flush=True)
        print("FILE\t%s\tok" % key, flush=True)
    except Exception as e:
        print("FAIL\t%s\t%s: %s" % (key, type(e).__name__, e), flush=True)
        fails += 1
sys.exit(1 if fails else 0)
