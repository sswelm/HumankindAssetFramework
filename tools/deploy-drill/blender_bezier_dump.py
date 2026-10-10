"""Blender's own FCurve.evaluate() on generated Bezier curves, for the deploy drill: BlenderFCurve.Evaluate must give
the same float, bit for bit.

usage: blender --background --python blender_bezier_dump.py -- <seed> <curves> [raw|setter]

Every curve is written as the float32 Blender STORES (read back from the key), every time as the float32 the
evaluation receives:
  CURVE <ordinal> <CONSTANT|LINEAR> <frame:value:lx:ly:rx:ry> ...
  TIMES <time> ... (ordered evaluation requests, including repeats)
  E <time> <value>
  END <curves> <values>
The keys are stored RAW: the extrapolation is set FIRST (its setter sorts the keys, drops doubled frames and
recalculates the handles - set last, as this script once did, no backward handle ever reached the evaluation) and
the keys then go in through foreach_set, which updates nothing. So: handles pointing backwards, keys out of order,
two keys on one frame, a first key without a right handle and a second one far away (both roots about zero: the
zero discriminant), time curves exactly quadratic both ways round, double roots placed by hand, thirds on whole
frames (what keyframe_insert makes), spans to 1e30, infinities, one key. And FIXED rows where the C runtime's acos
is not .NET's (found by a directed search: 4 in 576 million evaluations).
"""
import math
import random
import struct
import sys

import bpy


def h32(v):
    return "%08x" % struct.unpack("<I", struct.pack("<f", v))[0]


def f32(v):
    try:
        return struct.unpack("<f", struct.pack("<f", v))[0]
    except OverflowError:
        return math.copysign(math.inf, v)


def bits(v): return struct.unpack("<I", struct.pack("<f", v))[0]
def frombits(b): return struct.unpack("<f", struct.pack("<I", b & 0xffffffff))[0]


def nxt(v, d):
    v = f32(v)
    if math.isnan(v) or math.isinf(v): return v
    b = bits(v)
    if v == 0.0: return frombits(1 if d > 0 else 0x80000001)
    if (v > 0) == (d > 0): return frombits(b + 1)
    return frombits(b - 1)


def main(seed, count, mode):
    r = random.Random(seed)
    act = bpy.data.actions.new("bezier")
    slot = act.slots.new('OBJECT', "s")
    strip = act.layers.new("L").strips.new(type='KEYFRAME')
    bag = strip.channelbag(slot, ensure=True)
    made = 0; values = 0
    stat = {"backward_kept": 0, "backward_asked": 0, "dup_kept": 0, "unsorted_kept": 0, "nan": 0}

    def curve(keys, times, extrapolation='CONSTANT'):
        nonlocal made, values
        keys = [tuple(f32(v) for v in k) for k in keys]
        fc = bag.fcurves.new("location", index=0)
        fc.extrapolation = extrapolation      # FIRST: this setter's update sorts the keys, drops doubles and turns handles round
        fc.keyframe_points.add(len(keys))
        if mode == "raw":
            for kp in fc.keyframe_points:
                kp.interpolation = 'BEZIER'; kp.handle_left_type = 'FREE'; kp.handle_right_type = 'FREE'
            fc.keyframe_points.foreach_set("co", [v for k in keys for v in k[0:2]])
            fc.keyframe_points.foreach_set("handle_left", [v for k in keys for v in k[2:4]])
            fc.keyframe_points.foreach_set("handle_right", [v for k in keys for v in k[4:6]])
        else:
            for kp, (x, y, lx, ly, rx, ry) in zip(fc.keyframe_points, keys):
                kp.interpolation = 'BEZIER'; kp.handle_left_type = 'FREE'; kp.handle_right_type = 'FREE'
                kp.co = (x, y); kp.handle_left = (lx, ly); kp.handle_right = (rx, ry)
        st = [(kp.co[0], kp.co[1], kp.handle_left[0], kp.handle_left[1], kp.handle_right[0], kp.handle_right[1]) for kp in fc.keyframe_points]
        for a, b in zip(keys, st):
            if a[2] > a[0] or a[4] < a[0]:
                stat["backward_asked"] += 1
                if b[2] > b[0] or b[4] < b[0]: stat["backward_kept"] += 1
        for i in range(1, len(st)):
            if st[i][0] == st[i - 1][0]: stat["dup_kept"] += 1
            if st[i][0] < st[i - 1][0]: stat["unsorted_kept"] += 1
        times = [t for t in map(f32, times) if not math.isnan(t)]
        print("CURVE\t%d\t%s\t%s" % (made, extrapolation, "\t".join(":".join(h32(v) for v in k) for k in st)))
        print("TIMES\t%s" % "\t".join(h32(t) for t in times))
        for t in times:
            v = fc.evaluate(t)
            if math.isnan(v): stat["nan"] += 1
            print("E\t%s\t%s" % (h32(t), h32(v)))
            values += 1
        bag.fcurves.remove(fc)
        made += 1

    # where ucrtbase's acos and .NET's Math.Acos give another double AND the solver carries it into the float
    for row in ("432fe3a9:becec8d5:432ee3a9:00000000:43fea4cc:c1c2b1fc 4492fc75:bec1ff41:4452a7bc:41bd3f8c:44931c75:00000000 44161052",
                "41100000:3f51b443:41000000:00000000:431c0a31:c2021171 43e10000:3d516e1f:43978519:c22b0e4a:43e18000:00000000 41584618",
                "43f7c889:3f1559a3:43f74889:00000000:444f4635:417b9c0f 44baf222:3f3a1c0d:44914dc5:424e5b56:44bb1222:00000000 445ea088",
                "c3800000:3f4dc442:c3808000:00000000:c3797ab3:424ef81d c36c6bc7:bd894a2d:c372f3f5:4194c794:c36b6bc7:00000000 c37ba2a5"):
        k0, k1, t = row.split(" ")
        curve([tuple(frombits(int(v, 16)) for v in k.split(":")) for k in (k0, k1)], [frombits(int(t, 16))])

    def val():
        return r.choice((r.uniform(-1, 1), r.uniform(-100, 100), r.uniform(-1e-3, 1e-3), 0.0, -0.0, 1.0, r.uniform(-1e30, 1e30), 1e-40, 3.0e38))

    def times_for(xs):
        out = []
        fin = [x for x in xs if not (math.isnan(x) or math.isinf(x))]
        for x in fin:
            out += [x, nxt(x, 1), nxt(x, -1), x + 5e-9, x - 5e-9, x + 5e-5, x - 5e-5, x + 1e-4, x - 1e-4, x + 1.5e-4, x - 1.5e-4, x + 2e-4, x + 1e-3, x - 1e-3]
        s = sorted(set(fin))
        for a, b in zip(s, s[1:]):
            out += [(a + b) / 2, a + (b - a) / 3, a + (b - a) * 1e-7, b - (b - a) * 1e-7]
            out += [r.uniform(a, b) for _ in range(6)]
            if b - a <= 12 and a == int(a):
                out += [a + i for i in range(1, int(b - a) + 1)]
            for m in (1.0, 1e3, 1e6, 1e12, 1e-3):
                if a + 2e-4 * m < b: out.append(a + 2e-4 * m)
                if b - 2e-4 * m > a: out.append(b - 2e-4 * m)
        if s: out += [s[0] - 1, s[-1] + 1, s[0] - 1e9, s[-1] + 1e9]
        return out

    def frame():
        c = r.randrange(9)
        if c == 0: return float(r.randint(-4, 4))
        if c == 1: return float(r.randint(-3000, 3000))
        if c == 2: return float(r.choice((-1, 1)) * (1048574 - r.randint(0, 3)))
        if c == 3: return r.uniform(-50, 50)
        if c == 4: return r.choice((-1, 1)) * 10 ** r.uniform(6, 30)
        if c == 5: return r.choice((-1, 1)) * 10 ** r.uniform(-30, -3)
        if c == 6: return r.choice((0.0, -0.0, 1e-42, -1e-42, 16777216.0, 16777218.0, 33554432.0))
        if c == 7: return float(r.randint(-40, 40)) + r.choice((0.0, 0.00005, 0.0001, 1e-9, 0.5))
        return r.uniform(-2000, 2000)

    for i in range(count):
        kind = i % 14
        ext = 'LINEAR' if r.random() < 0.2 else 'CONSTANT'
        if kind in (0, 1, 2, 3):          # raw: sorted, random handles any direction
            n = r.randint(2, 6); xs = sorted(frame() for _ in range(n))
            if kind == 1: r.shuffle(xs)                                       # unsorted
            if kind == 2: xs = sorted(xs + [r.choice(xs) for _ in range(r.randint(1, 2))])   # two keys on one frame
            if kind == 3: xs = [float(r.randint(-3, 6)) for _ in range(r.randint(2, 7))]     # small whole frames, unsorted, repeated
            k = []
            for x in xs:
                sp = r.choice((1.0, 3.0, 0.01, abs(x) * 1e-3 + 1e-3, abs(x) + 1.0, 1e-6, 100.0))
                hl = r.choice((r.uniform(0, sp), -r.uniform(0, sp), 0.0, sp / 3, 4 * sp, -4 * sp))
                hr = r.choice((r.uniform(0, sp), -r.uniform(0, sp), 0.0, sp / 3, 4 * sp, -4 * sp))
                k.append((x, val(), x - hl, val(), x + hr, val()))
            curve(k, times_for(xs), ext)
        elif kind in (4, 5):              # two keys, handles backwards or forwards, on both
            x0 = frame(); span = r.choice((1.0, 2.0, 3.0, 24.0, 1000.0, r.uniform(0.001, 5), 10 ** r.uniform(3, 20)))
            x1 = x0 + span
            a = r.choice((-2, -1, -0.5, -1 / 3, 0, 1 / 3, 0.5, 1, 2, r.uniform(-2, 2)))
            b = r.choice((-2, -1, -0.5, -1 / 3, 0, 1 / 3, 0.5, 1, 2, r.uniform(-2, 2)))
            k = [(x0, val(), x0 - span, val(), x0 + a * span, val()), (x1, val(), x1 - b * span, val(), x1 + span, val())]
            curve(k, times_for([x0, x1]) + [x0 + span * r.random() for _ in range(20)], ext)
        elif kind in (6, 7):              # the first key at zero with no right handle, the second far away: both roots about zero
            e = r.randint(4, 100); x1 = (3.0 if kind == 6 else r.choice((1.0, 3.0, 5.0, 7.0))) * 2.0 ** e
            h = 2.0 ** e if kind == 6 else x1 * r.choice((1 / 3, 0.5, 0.25, 2 / 3, 1.0, 0.0, r.random()))
            x0 = r.choice((0.0, 0.0, 1.0, -7.0))
            k = [(x0, val(), x0 - 1, val(), x0 + r.choice((0.0, 0.0, 1e-30, -1e-30, 1e-12)), val()), (x0 + x1, val(), x0 + x1 - h, val(), x0 + x1 + 1, val())]
            ts = [x0 + m for m in (1.5e-4, 2e-4, 1e-3, 0.01, 0.5, 1.0, 2.0, 100.0, 1e4, 1e6, 1e9, x1 * 1e-9, x1 * 1e-6, x1 * 1e-3, x1 / 2, x1 * 0.999)]
            curve(k, ts + times_for([x0, x0 + x1]), ext)
        elif kind == 8:                   # exactly quadratic time curves, both ways round, with backward handles
            s = r.choice((0.25, 1.0, 4.0, 64.0, 2.0 ** 40, 2.0 ** 70)); x0 = float(r.randint(-64, 64)) if s < 1e6 else 0.0
            q1, q2 = r.choice(((-1, 0), (0, 1), (2, 3), (3, 4), (-2, -1), (4, 5), (0.5, 1.5), (-0.5, 0.5), (1, 2)))
            k = [(x0, val(), x0 - s, val(), x0 + q1 * s, val()), (x0 + 3 * s, val(), x0 + q2 * s, val(), x0 + 4 * s, val())]
            curve(k, times_for([x0, x0 + 3 * s]) + [x0 + 3 * s * j / 48 for j in range(49)], ext)
        elif kind == 9:                   # a double root placed by hand: x(t) - x = k (t - d)^2 (t - e), scaled by a power of two
            d = r.choice((0.5, 0.25, 0.75, 0.0, 1.0, 0.125)); e = r.choice((-1.0, 2.0, 0.5, 0.25, 3.0, -0.5, 1.0, 0.0))
            c3, c2, c1, c0 = 1.0, -(2 * d + e), d * d + 2 * d * e, -d * d * e
            s = r.choice((1.0, 8.0, 64.0, 4096.0)) * r.choice((1, 3, 4, 12, 16))
            # q1 - q0 = c1/3, q0 - 2 q1 + q2 = c2/3, q3 - q0 + 3 (q1 - q2) = c3, x = q0 - c0
            q0 = 0.0; q1 = q0 + c1 / 3; q2 = c2 / 3 - q0 + 2 * q1; q3 = c3 + q0 - 3 * (q1 - q2)
            x0 = float(r.randint(-8, 8)) * s
            k = [(x0 + q0 * s, val(), x0 - s, val(), x0 + q1 * s, val()), (x0 + q3 * s, val(), x0 + q2 * s, val(), x0 + q3 * s + s, val())]
            curve(k, [x0 - c0 * s] + times_for([x0, x0 + q3 * s]), ext)
        elif kind == 10:                  # special values
            n = r.randint(2, 4); xs = sorted(frame() for _ in range(n))
            sp = (math.inf, -math.inf, 3.4e38, -3.4e38, 1e-45, 0.0, -0.0)
            k = []
            for x in xs:
                row = [x, val(), x - r.random(), val(), x + r.random(), val()]
                for _ in range(r.randint(0, 2)): row[r.randrange(1, 6)] = r.choice(sp)
                k.append(tuple(row))
            curve(k, times_for(xs), ext)
        elif kind == 11:                  # many keys a hair apart about zero and about big frames
            base = r.choice((0.0, 1.0, 1000.0, 1048574.0, 16777216.0, 1e-3)); n = r.randint(3, 9); xs = [base]
            for _ in range(n - 1):
                xs.append(xs[-1] + r.choice((1.0, 0.00005, 0.0001, 0.0002, 1e-9, 5e-9, 2e-8, 1e-8, 0.0, r.uniform(0.0001, 0.001))))
            if r.random() < 0.3: r.shuffle(xs)
            k = [(x, val(), x - r.uniform(-1, 3), val(), x + r.uniform(-1, 3), val()) for x in xs]
            curve(k, times_for(xs), ext)
        elif kind == 12:                  # thirds on whole frames: what keyframe_insert makes - spans and values of every size
            x0 = float(r.choice((0, 1, -5, -40, 12, 500, 2500, 1048000, -1048574, r.randint(-3000, 3000))))
            span = float(r.choice((1, 2, 3, 5, 12, 24, 50, 100, 441, 1000, 2500, 5000, 500000, 1048574, 2097148)))
            x1 = x0 + span
            third = f32(f32(span) / f32(f32(6.0 / f32(2.5614)) * f32(2.5614)) * 2.0)
            y0, y1 = val(), val()
            k = [(x0, y0, x0 - third, y0, x0 + third, y0), (x1, y1, x1 - third, y1, x1 + third, y1)]
            ts = [x0 + j for j in range(0, int(min(span, 200)) + 1)] + [float(r.randint(int(x0), int(x1))) for _ in range(60)]
            curve(k, ts + times_for([x0, x1]), ext)
        else:                             # one key
            x = frame(); curve([(x, val(), x - 1, val(), x + 1, val())], times_for([x]), ext)
    for kk, vv in stat.items():
        print("STAT\t%s\t%d" % (kk, vv))
    print("END\t%d\t%d" % (made, values), flush=True)


if __name__ == "__main__":
    a = sys.argv[sys.argv.index("--") + 1:]
    main(int(a[0]), int(a[1]), a[2] if len(a) > 2 else "raw")
