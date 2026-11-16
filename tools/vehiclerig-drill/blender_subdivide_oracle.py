"""Blender's own answers for the C# BlenderSubdivide (Tests/BlenderSubdivideTests.cs reads what this prints).

Per mesh: the start state, then up to three passes of the tread code's own call -
    bmesh.ops.subdivide_edges(bm, edges=<the edges longer than a threshold>, cuts=1, use_grid_fill=True)
- on ONE BMesh, as vehicle_rig.py runs them, with the state after each pass:

  STATE <mesh> <pass> V <x y z bits ...> E <v1 v2 ...> F <a,b,c;...> UV <u v bits per corner, face by face> COL <rgba per vertex>
  LONG  <mesh> <pass> <the edge numbers handed to the pass>

Pass 0 is the mesh before any cut. Every element is in the BMesh's own order.

Meshes "quads" and "fins" pick their edges by rule instead of by length.

usage: blender --background --factory-startup --python blender_subdivide_oracle.py
"""
import bpy, bmesh, random, struct


def bits(f):
    return "%08x" % struct.unpack("<I", struct.pack("<f", f))[0]


def state(name, n, bm, uv, col):
    bm.verts.index_update(); bm.edges.index_update(); bm.faces.index_update()
    v = " ".join(bits(c) for vert in bm.verts for c in vert.co)
    e = " ".join("%d %d" % (ed.verts[0].index, ed.verts[1].index) for ed in bm.edges)
    f = ";".join(",".join(str(l.vert.index) for l in face.loops) for face in bm.faces)
    u = " ".join(bits(c) for face in bm.faces for l in face.loops for c in l[uv].uv)
    c = " ".join("%d" % round(x * 255.0) for vert in bm.verts for x in vert[col])
    print("STATE %s %d V %s E %s F %s UV %s COL %s" % (name, n, v, e, f, u, c))


def run(name, verts, tris, thr, seed, pick=None):
    rnd = random.Random(seed)
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], tris)
    me.update()
    uvl = me.uv_layers.new(name="UVMap")
    for i, l in enumerate(me.loops):
        uvl.data[i].uv = (rnd.random(), rnd.random())
    cl = me.color_attributes.new(name="Col", type='BYTE_COLOR', domain='POINT')
    for i in range(len(me.vertices)):
        cl.data[i].color = (rnd.randrange(256) / 255.0, rnd.randrange(256) / 255.0, rnd.randrange(256) / 255.0, rnd.randrange(256) / 255.0)
    bm = bmesh.new(); bm.from_mesh(me)
    uv = bm.loops.layers.uv[0]; col = bm.verts.layers.color[0]
    state(name, 0, bm, uv, col)
    for p in range(3):
        long = [e for e in bm.edges if (e.verts[0].co - e.verts[1].co).length > thr] if pick is None else pick(p, list(bm.edges), rnd)
        bm.edges.index_update()
        print("LONG %s %d %s" % (name, p + 1, " ".join(str(e.index) for e in long)))
        if not long:
            break
        bmesh.ops.subdivide_edges(bm, edges=long, cuts=1, use_grid_fill=True)
        state(name, p + 1, bm, uv, col)
    bm.free()


def grid(n, seed, jitter):
    rnd = random.Random(seed)
    vs = [(x * 0.1 + x * x * 0.03 + rnd.uniform(-jitter, jitter), y * 0.17 + rnd.uniform(-jitter, jitter), rnd.uniform(0, 0.05)) for y in range(n) for x in range(n)]
    ts = []
    for y in range(n - 1):
        for x in range(n - 1):
            a = y * n + x
            if (x + y) % 2:
                ts += [(a, a + 1, a + n + 1), (a, a + n + 1, a + n)]
            else:
                ts += [(a, a + 1, a + n), (a + 1, a + n + 1, a + n)]
    return vs, ts


def band(n, seed):
    # a tread-like closed band: two rings of vertices, long edges along the run, short across
    import math
    rnd = random.Random(seed)
    vs = []
    for i in range(n):
        a = 2 * math.pi * i / n
        r = 1.0 + 0.3 * math.cos(2 * a)
        for y in (-0.1, 0.1):
            vs.append((r * math.cos(a) * 1.6 + rnd.uniform(-0.01, 0.01), y, r * math.sin(a) * 0.5))
    ts = []
    for i in range(n):
        a, b, c, d = 2 * i, 2 * i + 1, (2 * i + 2) % (2 * n), (2 * i + 3) % (2 * n)
        ts += [(a, c, b), (b, c, d)]
    return vs, ts


v, t = grid(5, 1, 0.02); run("grid5", v, t, 0.105, 1)
v, t = grid(6, 2, 0.04); run("grid6", v, t, 0.16, 2)
v, t = band(14, 3); run("band", v, t, 0.3, 3)
# a fan around one vertex plus a lone triangle and a triangle pair sharing one edge only (boundaries, valence, a non-manifold fin)
vs = [(0, 0, 0)] + [(0.5 * (1 + 0.2 * i) * __import__("math").cos(i), 0.5 * (1 + 0.2 * i) * __import__("math").sin(i), 0.0) for i in range(6)]
ts = [(0, 1 + i, 1 + (i + 1) % 6) for i in range(6)]
vs += [(3, 0, 0), (3.6, 0, 0), (3.3, 0.5, 0), (3.3, 0.2, 0.6)]
ts += [(7, 8, 9), (7, 8, 10)]
run("fan", vs, ts, 0.35, 4)
# passes that cut a RANDOM half of the edges: quads with one, two (adjacent or opposite), three and four cut edges, n-gons,
# and two cut halves of one earlier edge meeting at their shared vertex (parallel: counted as no corner)
v, t = grid(4, 5, 0.03); run("quads", v, t, 0.0, 5, pick=lambda p, es, rnd: [e for e in es if rnd.random() < 0.5])
# a face doubled on its three vertices, two of its edges cut: the two new vertices share BOTH faces, so neither is cut across
run("fins", [(0, 0, 0), (1, 0, 0), (0.3, 0.8, 0), (2, 2, 0)], [(0, 1, 2), (0, 2, 1), (1, 3, 2)], 0.0, 6,
    pick=lambda p, es, rnd: [e for e in es if {e.verts[0].index, e.verts[1].index} in ({0, 1}, {0, 2})] if p == 0 else [])
