"""Blender's own answers for the C# BlenderBvh and the Two-sided directions: printed as bit patterns.

  DIR k x y z                 _two_sided_dirs(64), float32 bits (the script's own function, read from vehicle_rig.py)
  SOUP name ...               a triangle soup: vertices (float32 bits) and triangles
  RAY name i ox oy oz dx dy dz maxdist -> index dist     every cast of a fixed list, max distance and result as bits
"""
import sys, struct, math, random, re
from mathutils import Vector
from mathutils.bvhtree import BVHTree

script = sys.argv[sys.argv.index("--") + 1]
src = open(script, encoding="utf-8").read()
m = re.search(r"^def _two_sided_dirs\(n\):\n(?:    .*\n)+", src, re.M)
ns = {"math": math, "Vector": Vector}
exec(m.group(0), ns)


def bits(f):
    return "%08x" % struct.unpack("<I", struct.pack("<f", f))[0]


for k, d in enumerate(ns["_two_sided_dirs"](64)):
    print("DIR %d %s %s %s" % (k, bits(d.x), bits(d.y), bits(d.z)))


def f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


def soup(name, seed, n, scale, dup):
    rnd = random.Random(seed)
    vs = []; ts = []
    for i in range(n):
        c = [rnd.uniform(-scale, scale) for _ in range(3)]
        for _ in range(3):
            vs.append(tuple(f32(c[k] + rnd.uniform(-scale * 0.15, scale * 0.15)) for k in range(3)))
        ts.append((3 * i, 3 * i + 1, 3 * i + 2))
    # plain duplicates and reversed twins on the same vertices: the ties a walk order decides
    for i in range(dup):
        a = ts[rnd.randrange(n)]
        ts.append(a if i % 2 == 0 else (a[2], a[1], a[0]))
    # a grid of coplanar triangles sharing edges: rays at vertices and along edges
    g0 = len(vs)
    for y in range(4):
        for x in range(4):
            vs.append((f32(x * scale * 0.25), f32(y * scale * 0.25), f32(scale * 0.5)))
    for y in range(3):
        for x in range(3):
            a = g0 + 4 * y + x
            ts.append((a, a + 1, a + 5)); ts.append((a, a + 5, a + 4))
    print("SOUP %s %d %d %s %s" % (name, len(vs), len(ts), " ".join(bits(c) for v in vs for c in v), " ".join(str(i) for t in ts for i in t)))
    bvh = BVHTree.FromPolygons(vs, ts)
    rays = []
    for i in range(400):
        o = tuple(f32(rnd.uniform(-scale * 1.5, scale * 1.5)) for _ in range(3))
        d = tuple(f32(rnd.uniform(-1, 1)) for _ in range(3))
        rays.append((o, d, None if i % 3 else f32(rnd.uniform(0.1, 2.0) * scale)))
    for i in range(60):   # axis-aligned rays through grid vertices and edges, from above and below
        gx = (i % 7) * scale * 0.125; gy = ((i // 7) % 7) * scale * 0.125
        up = i % 2 == 0
        rays.append(((f32(gx), f32(gy), f32(-scale * 2 if up else scale * 2)), (0.0, 0.0, 1.0 if up else -1.0), None))
    for i in range(60):   # rays that start ON a triangle's corner or centre
        t = ts[rnd.randrange(len(ts))]
        p = vs[t[0]] if i % 2 else tuple(f32((vs[t[0]][k] + vs[t[1]][k] + vs[t[2]][k]) / 3.0) for k in range(3))
        d = tuple(f32(rnd.uniform(-1, 1)) for _ in range(3))
        rays.append((p, d, None))
    for i, (o, d, md) in enumerate(rays):
        hit = bvh.ray_cast(Vector(o), Vector(d)) if md is None else bvh.ray_cast(Vector(o), Vector(d), md)
        print("RAY %s %d %s %s %s %s %s %s %s -> %s %s" % (name, i, bits(o[0]), bits(o[1]), bits(o[2]), bits(d[0]), bits(d[1]), bits(d[2]),
                                                       "none" if md is None else bits(md),
                                                       "-1" if hit[0] is None else str(hit[2]), "none" if hit[0] is None else bits(hit[3])))


soup("one", 1, 1, 1.0, 0)
soup("five", 5, 5, 2.0, 2)
soup("small", 7, 40, 1.0, 6)
soup("mid", 11, 700, 10.0, 40)
soup("large", 13, 6000, 250.0, 200)
