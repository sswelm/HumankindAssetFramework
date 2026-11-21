# max_world.py - a SECOND reader of a 3ds Max .max file, for tools/max_drill.sh: the compound-file container, the
# chunk tree, the scene's references, each node's world transform and the triangles of each Editable Mesh, printed as one
# PART line per mesh part in WORLD space and 3ds Max axes - the same lines `MaxDrill stats` prints from the C# reader's
# model (editor/MaxDocument.cs, MaxScene.cs, MaxModel.cs). It shares no CODE with the C# reader and every READING of
# the format with it (which chunk is what, the inverse quaternion, the scale's axis system, hidden faces, the re-wind
# of a mirrored part): it catches a slip in one implementation, never a shared misreading. Standard library only.
#   usage: python -I max_world.py <file.max>
import sys, struct, zlib, math


class Cfb:
    def __init__(self, path):
        b = self.b = open(path, 'rb').read()
        if b[:8] != b'\xD0\xCF\x11\xE0\xA1\xB1\x1A\xE1':
            raise ValueError('not a compound file')
        self.major = struct.unpack_from('<H', b, 0x1A)[0]
        self.ss = 1 << struct.unpack_from('<H', b, 0x1E)[0]
        self.mss = 1 << struct.unpack_from('<H', b, 0x20)[0]
        nfat, dirstart = struct.unpack_from('<I', b, 0x2C)[0], struct.unpack_from('<I', b, 0x30)[0]
        self.cutoff = struct.unpack_from('<I', b, 0x38)[0]
        minifat, nminifat, difat, ndifat = struct.unpack_from('<IIII', b, 0x3C)
        fatsects = [s for s in struct.unpack_from('<109I', b, 0x4C) if s < 0xFFFFFFFA]
        per = self.ss // 4
        while difat < 0xFFFFFFFA and ndifat > 0:
            ent = struct.unpack_from('<%dI' % per, b, self.off(difat))
            fatsects += [s for s in ent[:-1] if s < 0xFFFFFFFA]
            difat = ent[-1]; ndifat -= 1
        self.fat = []
        for s in fatsects[:nfat]:
            self.fat += struct.unpack_from('<%dI' % per, b, self.off(s))
        d = self.chain(dirstart)
        self.entries = []
        for i in range(0, len(d) - 127, 128):
            nlen = struct.unpack_from('<H', d, i + 0x40)[0]
            typ = d[i + 0x42]
            if typ == 0 or nlen < 2:
                continue
            name = d[i:i + nlen - 2].decode('utf-16le', 'replace')
            start = struct.unpack_from('<I', d, i + 0x74)[0]
            size = struct.unpack_from('<Q', d, i + 0x78)[0] if self.major >= 4 else struct.unpack_from('<I', d, i + 0x78)[0]
            self.entries.append((name, typ, start, size))
        root = [e for e in self.entries if e[1] == 5][0]
        self.mini = self.chain(root[2])[:root[3]]
        mf = self.chain(minifat) if minifat < 0xFFFFFFFA else b''
        self.minifat = struct.unpack_from('<%dI' % (len(mf) // 4), mf, 0) if mf else ()

    def off(self, sect):
        return (sect + 1) * self.ss

    def chain(self, start):
        out = []; s = start; guard = 0
        while s < 0xFFFFFFFA:
            out.append(self.b[self.off(s):self.off(s) + self.ss]); s = self.fat[s]; guard += 1
            if guard > len(self.fat) + 1:
                raise ValueError('FAT cycle')
        return b''.join(out)

    def stream(self, name):
        for n, typ, start, size in self.entries:
            if n == name and typ == 2:
                if size < self.cutoff:
                    out = []; s = start; guard = 0
                    while s < 0xFFFFFFFA:
                        out.append(self.mini[s * self.mss:(s + 1) * self.mss]); s = self.minifat[s]; guard += 1
                        if guard > len(self.minifat) + 1:
                            raise ValueError('miniFAT cycle')
                    raw = b''.join(out)[:size]
                else:
                    raw = self.chain(start)[:size]
                return zlib.decompress(raw, zlib.MAX_WBITS | 32) if raw[:2] == b'\x1f\x8b' else raw
        return None


class Chunk:
    __slots__ = ('type', 'data', 'kids')

    def __init__(self, t, data, kids):
        self.type = t; self.data = data; self.kids = kids

    def first(self, t):
        for k in self.kids or ():
            if k.type == t:
                return k
        return None


def parse(buf, lo=0, hi=None):
    hi = len(buf) if hi is None else hi
    out = []
    while lo + 6 <= hi:
        t, sz = struct.unpack_from('<HI', buf, lo)
        head = 6
        if sz == 0:
            sz = struct.unpack_from('<Q', buf, lo + 6)[0]; head = 14
            container = bool(sz >> 63); sz &= (1 << 63) - 1
        else:
            container = bool(sz >> 31); sz &= 0x7FFFFFFF
        if sz < head or lo + sz > hi:
            raise ValueError('bad chunk %04X size %d at %d' % (t, sz, lo))
        out.append(Chunk(t, None, parse(buf, lo + head, lo + sz)) if container else Chunk(t, buf[lo + head:lo + sz], None))
        lo += sz
    return out


def text(b):
    return b.decode('utf-16le', 'replace').rstrip('\0')


def main(path):
    cfb = Cfb(path)
    names = [n for n, typ, s, z in cfb.entries if typ == 2]
    cd = next(n for n in ('ClassDirectory3', 'ClassDirectory2', 'ClassDirectory') if n in names)
    classes = []
    for c in parse(cfb.stream(cd)):
        dll, a, b, sup = struct.unpack_from('<iIII', c.first(0x2060).data, 0)
        classes.append((a, b, sup))
    objs = next(c for c in parse(cfb.stream('Scene')) if c.kids is not None).kids

    def cls(c):
        return classes[c.type] if c is not None and c.type < len(classes) and c.type not in (0x2032, 0x2033) else None

    def get(i):
        return objs[i] if i is not None and i < len(objs) else None

    def refs(c):
        if c is None or c.kids is None:
            return {}
        r = c.first(0x2034)
        if r is not None:
            return {i: v for i, v in enumerate(struct.unpack_from('<%dI' % (len(r.data) // 4), r.data, 0)) if v != 0xFFFFFFFF}
        r = c.first(0x2035)
        v = struct.unpack_from('<%dI' % (len(r.data) // 4), r.data, 0) if r is not None else ()
        return {v[i]: v[i + 1] for i in range(1, len(v) - 1, 2) if v[i + 1] != 0xFFFFFFFF}

    def floats(c, t, n):
        k = c.first(t) if c is not None and c.kids is not None else None
        return struct.unpack_from('<%df' % n, k.data, 0) if k is not None and len(k.data) >= 4 * n else None

    def mul(a, b):
        return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]

    def ident():
        return [[1.0 if i == j else 0.0 for j in range(4)] for i in range(4)]

    def rot(q):
        x, y, z, w = -q[0], -q[1], -q[2], q[3]              # 3ds Max stores the inverse quaternion
        n = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
        x, y, z, w = x / n, y / n, z / n, w / n
        m = ident()
        m[0][0] = 1 - 2 * (y * y + z * z); m[0][1] = 2 * (x * y - z * w); m[0][2] = 2 * (x * z + y * w)
        m[1][0] = 2 * (x * y + z * w); m[1][1] = 1 - 2 * (x * x + z * z); m[1][2] = 2 * (y * z - x * w)
        m[2][0] = 2 * (x * z - y * w); m[2][1] = 2 * (y * z + x * w); m[2][2] = 1 - 2 * (x * x + y * y)
        return m

    def trs(p, q, s):
        sm = ident()
        for i in range(3):
            sm[i][i] = s[i]
        if len(s) >= 7 and not (s[0] == s[1] == s[2]):      # the scale's axis system (see MaxScene.Trs): U * diag * U-transposed
            u = rot(s[3:7])
            ut = [[u[j][i] for j in range(4)] for i in range(4)]
            sm = mul(mul(u, sm), ut)
        m = mul(rot(q), sm)
        for i in range(3):
            m[i][3] = p[i]
        return m

    PRS = (0x2005, 0, 0x9008); LISTS = (0x4B4B1002, 0x4B4B1003, 0x4B4B1004); NODE = (1, 0, 1)

    def value(c, t, n, depth=0):
        if c is None:
            return None
        v = floats(c, t, n)
        if v is None and cls(c) is not None and cls(c)[0] in LISTS and cls(c)[1] == 0 and depth < 8:
            r = refs(c)
            return value(get(r[min(r)]), t, n, depth + 1) if r else None
        return v

    def local(node):
        ctrl = get(refs(node).get(0))
        if ctrl is None or cls(ctrl) != PRS:
            return ident()
        r = refs(ctrl)
        p = value(get(r.get(0)), 0x2503, 3) or (0, 0, 0)
        q = value(get(r.get(1)), 0x2504, 4) or (0, 0, 0, 1)
        s = value(get(r.get(2)), 0x2505, 7) or value(get(r.get(2)), 0x2505, 3) or (1, 1, 1)
        return trs(p, q, s)

    index = {id(c): i for i, c in enumerate(objs)}
    cache = {}

    def world(node, depth=0):
        i = index[id(node)]
        if i in cache:
            return cache[i]
        m = local(node)
        pk = node.first(0x0960)
        parent = get(struct.unpack_from('<I', pk.data, 0)[0]) if pk is not None and len(pk.data) >= 4 else None
        if parent is not None and cls(parent) == NODE and parent is not node and depth < 4096:
            m = mul(world(parent, depth + 1), m)
        cache[i] = m
        return m

    lines = []; total = [0, 0, 0, 0]
    for node in objs:
        if cls(node) != NODE:
            continue
        o = get(refs(node).get(1)); depth = 0
        while o is not None and o.type in (0x2032, 0x2033) and depth < 16:
            rr = refs(o)
            o = get(rr[max(rr)]) if rr else None; depth += 1
        g = o.first(0x08FE) if o is not None and o.kids is not None else None
        v, f = (g.first(0x0914), g.first(0x0912)) if g is not None else (None, None)
        if v is None or f is None:
            continue
        op = floats(node, 0x096A, 3) or (0, 0, 0); oq = floats(node, 0x096B, 4) or (0, 0, 0, 1)
        os_ = floats(node, 0x096C, 7) or floats(node, 0x096C, 3) or (1, 1, 1)
        m = mul(world(node), trs(op, oq, os_))
        det = (m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1]) - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0]) + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]))
        nv = struct.unpack_from('<I', v.data, 0)[0]; nf = struct.unpack_from('<I', f.data, 0)[0]
        pts = []
        for i in range(nv):
            x, y, z = struct.unpack_from('<3f', v.data, 4 + 12 * i)
            pts.append(tuple(m[k][0] * x + m[k][1] * y + m[k][2] * z + m[k][3] for k in range(3)))
        tv, tf = g.first(0x0916), g.first(0x0918)
        uv = tv is not None and tf is not None and len(tv.data) >= 4 and struct.unpack_from('<I', tv.data, 0)[0] > 0 \
            and len(tv.data) == 4 + 12 * struct.unpack_from('<I', tv.data, 0)[0] and len(tf.data) == 12 * nf
        tris = 0; area = 0.0; vol = 0.0; va = [0.0, 0.0, 0.0]; lo = [1e300] * 3; hi = [-1e300] * 3; kept = []
        for i in range(nf):
            a, b, c, sg, flags, mid = struct.unpack_from('<IIIIHH', f.data, 4 + 20 * i)
            if flags & 0x8:
                total[3] += 1
                continue
            tri = [pts[a], pts[b], pts[c]]
            if det < 0:
                tri.reverse()
            for p in tri:
                for k in range(3):
                    lo[k] = min(lo[k], p[k]); hi[k] = max(hi[k], p[k])
            u = [tri[1][k] - tri[0][k] for k in range(3)]; w = [tri[2][k] - tri[0][k] for k in range(3)]
            n = (u[1] * w[2] - u[2] * w[1], u[2] * w[0] - u[0] * w[2], u[0] * w[1] - u[1] * w[0])
            area += 0.5 * math.sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2])
            va = [va[k] + 0.5 * n[k] for k in range(3)]
            kept.append(tri)
            tris += 1
        if tris == 0:
            continue
        # the signed volume about the part's OWN centre (the middle of its box): about the world's origin a small part
        # far from it has a volume term so large that turning the part inside out drowned in the tolerance
        mid = [(lo[k] + hi[k]) / 2 for k in range(3)]
        for tri in kept:
            p0, p1, p2 = ([p[k] - mid[k] for k in range(3)] for p in tri)
            vol += (p0[0] * (p1[1] * p2[2] - p1[2] * p2[1]) - p0[1] * (p1[0] * p2[2] - p1[2] * p2[0]) + p0[2] * (p1[0] * p2[1] - p1[1] * p2[0])) / 6
        nm = node.first(0x0962)
        name = text(nm.data).strip().replace(' ', '_') if nm else ''
        lines.append('PART %s tris=%d uv=%d area=%.6e vol=%.6e varea=%.6e,%.6e,%.6e lo=%.6e,%.6e,%.6e hi=%.6e,%.6e,%.6e'
                     % (name or 'Object', tris, 1 if uv else 0, area, vol, va[0], va[1], va[2], lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]))
        total[0] += 1; total[1] += tris; total[2] += 1 if uv else 0
    for l in sorted(lines):
        print(l)
    print('TOTAL parts=%d tris=%d uvparts=%d hidden=%d' % tuple(total))


main(sys.argv[1])
