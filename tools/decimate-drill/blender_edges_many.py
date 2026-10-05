"""Blender's own mesh layout and BMesh links for MANY files in ONE process (tools/decimate-drill, step 5 milestones a and
b): each file imported as the Lab imports it (the glTF importer, factory settings), and for every mesh object with vertices
- the importer's bone-shape "Icosphere" aside, as the probe purges it - rows as EdgesDrill.cs and BMeshDrill.cs print them:

    MESH <TAB> <file key> <TAB> <object name> <TAB> <verts> <TAB> <edges> <TAB> <faces> <TAB> <sha1 of the edge pairs> <TAB> <the first 8 edges>
    BM   <TAB> <file key> <TAB> <object name> <TAB> <verts> <TAB> <edges> <TAB> <loops> <TAB> <faces> <TAB> <sha1 of the link lists>
    OP   <TAB> <file key> <TAB> <object name> <TAB> <step> <TAB> <what> <TAB> <verts> <TAB> <edges> <TAB> <faces> <TAB> <sha1 of the link lists>

The edge pairs are `mesh.edges[i].vertices` in index order, each as (v0, v1) as Blender stores them, hashed as little-endian
int32 pairs - the layout the Decimate modifier's heap sees. The link lists come from bmesh.from_mesh on that mesh: per
vertex (bm.verts order) its link_edges then link_loops, per edge its verts then link_loops, per face its loops then verts,
every element by its index, one line each ("v0:1 2|0 5"), hashed as UTF-8 line by line. The OP rows (objects of at most
3,000 edges) follow a scripted sequence of kills and vert_splices driven by a fixed generator seeded per object, the same
sequence BMeshDrill.cs runs, with the lists hashed after every step.

usage: blender --background --python blender_edges_many.py -- <file>...
"""
import bpy, bmesh, hashlib, os, struct, sys

sys.stdout.reconfigure(encoding="utf-8")
OPS_MAX_EDGES = 3000


def dump(bm):
    h = hashlib.sha1()
    for v in bm.verts:
        h.update(("v%d:%s|%s\n" % (v.index, " ".join(str(e.index) for e in v.link_edges), " ".join(str(l.index) for l in v.link_loops))).encode("utf-8"))
    for e in bm.edges:
        h.update(("e%d:%d %d|%s\n" % (e.index, e.verts[0].index, e.verts[1].index, " ".join(str(l.index) for l in e.link_loops))).encode("utf-8"))
    for f in bm.faces:
        h.update(("f%d:%s|%s\n" % (f.index, " ".join(str(l.index) for l in f.loops), " ".join(str(v.index) for v in f.verts))).encode("utf-8"))
    return h.hexdigest()


def bmesh_rows(me, key, name):
    bm = bmesh.new()
    bm.from_mesh(me)
    try:
        print("BM\t%s\t%s\t%d\t%d\t%d\t%d\t%s" % (key, name, len(bm.verts), len(bm.edges), sum(len(f.loops) for f in bm.faces), len(bm.faces), dump(bm)), flush=True)
        V = list(bm.verts); E = list(bm.edges)
        V0, E0 = len(V), len(E)
        if E0 == 0 or E0 > OPS_MAX_EDGES:
            return
        rng = [1 + E0]

        def rnd():
            rng[0] = (rng[0] * 1103515245 + 12345) & 0x7fffffff
            return rng[0]

        def share_face(a, b):
            return any(b in f.verts for f in a.link_faces)

        steps = min(24, max(3, E0 // 4))
        for step in range(steps):
            if rnd() % 3 == 0:
                v = None
                for _ in range(64):
                    c = V[rnd() % V0]
                    if c.is_valid:
                        v = c; break
                if v is None:
                    what = "skip"
                else:
                    what = "kill-vert %d" % v.index
                    bm.verts.remove(v)
            else:
                e = None
                for _ in range(64):
                    c = E[rnd() % E0]
                    if c.is_valid:
                        e = c; break
                if e is None:
                    what = "skip"
                else:
                    v1, v2 = e.verts[0], e.verts[1]
                    i1, i2 = v1.index, v2.index
                    what = "kill-edge %d" % e.index
                    bm.edges.remove(e)
                    if v1.is_valid and v2.is_valid and i1 != i2 and bm.edges.get((v1, v2)) is None and not share_face(v2, v1):
                        bmesh.utils.vert_splice(v2, v1)
                        what += " splice %d<-%d" % (i1, i2)
            print("OP\t%s\t%s\t%d\t%s\t%d\t%d\t%d\t%s" % (key, name, step, what, len(bm.verts), len(bm.edges), len(bm.faces), dump(bm)), flush=True)
    finally:
        bm.free()


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
            bmesh_rows(me, key, o.name)
        print("FILE\t%s\tok" % key, flush=True)
    except Exception as e:
        print("FAIL\t%s\t%s: %s" % (key, type(e).__name__, e), flush=True)
        fails += 1
sys.exit(1 if fails else 0)
