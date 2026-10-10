"""Blender's own handle calculation on generated curves, for the deploy drill: BlenderFCurve.RecalcHandles must give
the same handles, bit for bit.

usage: blender --background --python blender_handles_dump.py -- <seed> <curves>

Every key is AUTO_CLAMPED on both sides, as keyframe_insert makes it, and the curve's smoothing is the default
(CONT_ACCEL - the row says so). The curve is built one of four ways - the keys stored and FCurve.update() called,
keyframe_points.insert() key by key in frame order, or in any order (each insert recalculates), or stored RAW
(foreach_set, with doubled frames and keys out of order left in) and handles_recalc() called - and then read back:
  H <ordinal> <CONSTANT|LINEAR> <smoothing> <frame:value:lx:ly:rx:ry> ...
  END <curves> <keys>
The shapes: rising and falling runs between extremes (the smoothing solver's systems of 2 to 40 unknowns), steps
that overshoot (a handle locked at its neighbour's height, and released again), plateaus, zigzags (every key an
extreme), uneven spacing down to a thousandth of a frame and up to thousands, values from 1e-30 to 1e30, two keys,
three keys, more than 256 keys (the recalculation works in chunks of 256), LINEAR extrapolation (free ends).
"""
import math
import random
import struct
import sys

import bpy


def h32(v):
    return "%08x" % struct.unpack("<I", struct.pack("<f", v))[0]


def main(seed, count):
    r = random.Random(seed)
    act = bpy.data.actions.new("handles")
    slot = act.slots.new('OBJECT', "s")
    bag = act.layers.new("L").strips.new(type='KEYFRAME').channelbag(slot, ensure=True)
    made = 0; nkeys = 0

    def curve(xs, ys, extrapolation, how):
        nonlocal made, nkeys
        fc = bag.fcurves.new("location", index=0)
        fc.extrapolation = extrapolation
        if how == 0:
            fc.keyframe_points.add(len(xs))
            for kp, x, y in zip(fc.keyframe_points, xs, ys):
                kp.interpolation = 'BEZIER'; kp.handle_left_type = 'AUTO_CLAMPED'; kp.handle_right_type = 'AUTO_CLAMPED'
                kp.co = (x, y); kp.handle_left = (x - 1, y); kp.handle_right = (x + 1, y)
            fc.update()
        elif how == 3:
            # RAW: nothing sorts the keys or drops a doubled frame, and handles_recalc() takes them as they are
            fc.keyframe_points.add(len(xs))
            for kp in fc.keyframe_points:
                kp.interpolation = 'BEZIER'; kp.handle_left_type = 'AUTO_CLAMPED'; kp.handle_right_type = 'AUTO_CLAMPED'
            fc.keyframe_points.foreach_set("co", [v for x, y in zip(xs, ys) for v in (x, y)])
            fc.keyframe_points.foreach_set("handle_left", [v for x, y in zip(xs, ys) for v in (x - 1, y)])
            fc.keyframe_points.foreach_set("handle_right", [v for x, y in zip(xs, ys) for v in (x + 1, y)])
            fc.keyframe_points.handles_recalc()
        else:
            order = list(range(len(xs)))
            if how == 2: r.shuffle(order)
            for i in order:
                kp = fc.keyframe_points.insert(xs[i], ys[i])
                kp.interpolation = 'BEZIER'; kp.handle_left_type = 'AUTO_CLAMPED'; kp.handle_right_type = 'AUTO_CLAMPED'
            fc.update()
        for kp in fc.keyframe_points:
            if kp.handle_left_type != 'AUTO_CLAMPED' or kp.handle_right_type != 'AUTO_CLAMPED' or kp.interpolation != 'BEZIER':
                raise RuntimeError("a key that is not Bezier with AUTO_CLAMPED handles")
        print("H\t%d\t%s\t%s\t%s" % (made, extrapolation, fc.auto_smoothing, "\t".join(
            ":".join(h32(v) for v in (*kp.co, *kp.handle_left, *kp.handle_right)) for kp in fc.keyframe_points)))
        nkeys += len(fc.keyframe_points)
        bag.fcurves.remove(fc)
        made += 1

    def frames(n):
        c = r.randrange(7)
        if c == 0: step = lambda: 1.0
        elif c == 1: step = lambda: float(r.choice((1, 2, 3, 5, 12, 24, 100)))
        elif c == 2: step = lambda: r.uniform(0.02, 3.0)
        elif c == 3: step = lambda: r.choice((0.011, 0.5, 1.0, 40.0, 1000.0, 5000.0))
        elif c == 4: step = lambda: 10 ** r.uniform(-1.9, 4)
        elif c == 5: step = lambda: float(r.randint(1, 4))
        else: step = lambda: r.choice((1.0, 1.0, 1.0, 37.0))
        x = float(r.choice((0, 0, 1, -40, 24, 441, 3000, -1048000))) if c != 2 else r.uniform(-50, 50)
        out = [x]
        for _ in range(n - 1):
            x += step(); out.append(x)
        return out

    def values(n):
        c = r.randrange(10); s = r.choice((1.0, 1.0, 1e-3, 100.0, 1e-30, 1e30, 1e5))
        if c == 0:      # one long rise, uneven
            v = 0.0; out = []
            for _ in range(n): v += r.choice((r.random(), r.random() * 10, 0.01, 5.0)); out.append(v)
        elif c == 1:    # rises and falls in runs
            v = r.uniform(-1, 1); out = []; d = 1
            for _ in range(n):
                if r.random() < 0.2: d = -d
                v += d * r.choice((r.random(), r.random() * 4, 0.05)); out.append(v)
        elif c == 2:    # a step: flat, a jump, flat-ish - the handles next to it overshoot
            out = [r.choice((0.0, 0.001 * i, 0.1 * i)) + (10.0 if i >= n // 2 else 0.0) for i in range(n)]
        elif c == 3:    # zigzag: every key an extreme
            out = [r.uniform(0, 1) * (1 if i % 2 else -1) for i in range(n)]
        elif c == 4:    # plateaus between rises
            v = 0.0; out = []
            for _ in range(n):
                if r.random() < 0.5: v += r.choice((1.0, 0.25, 3.0))
                out.append(v)
        elif c == 5:    # an exponential: every ratio of neighbours large
            g = r.uniform(0.2, 1.5); out = [math.exp(min(g * i, 14.0) + 0.01 * i) * 1e-3 for i in range(n)]
        elif c == 6:    # a falling run with one tiny step in it
            v = 100.0; out = []
            for i in range(n): v -= 1e-4 if i == n // 3 else r.uniform(0.5, 9); out.append(v)
        elif c == 7:    # a recoil: a fast kick out and a slow glide back
            k = max(2, n // 4); out = [-(i / k) ** 0.5 if i <= k else -max(0.0, 1 - (i - k) / (n - k - 1 or 1)) for i in range(n)]
        elif c == 8:    # whole numbers
            out = [float(r.randint(-3, 3)) for _ in range(n)]
        else:           # anything
            out = [r.uniform(-1, 1) for _ in range(n)]
        return [v * s for v in out]

    for i in range(count):
        # long curves (the recalculation works in chunks of 256 keys) by update() and by insertion, in and out of
        # order - never RAW with a doubled frame: there Blender's own result is not one result (a key's thread marks
        # its doubled neighbour while that neighbour's thread resets the mark: 5 of 40 runs differed, measured)
        n = r.choice((2, 2, 3, 3, 4, 5, 6, 8, 12, 20, 40)) if i % 50 > 2 else r.choice((257, 300, 520))
        xs = frames(n); ys = values(n)
        how = i % 4
        if how == 3 and n > 256: how = 1     # (see above: a long curve is never stored raw)
        if how == 3 and r.random() < 0.6:
            # two keys on one frame (neither is smoothed), now and then a key out of order
            for _ in range(r.randint(1, 3)):
                j = r.randrange(1, n); xs[j] = xs[j - 1]
            if r.random() < 0.3 and n > 3:
                j = r.randrange(1, n - 1); xs[j], xs[j + 1] = xs[j + 1], xs[j]
        curve(xs, ys, 'LINEAR' if r.random() < 0.25 else 'CONSTANT', how)
    print("END\t%d\t%d" % (made, nkeys), flush=True)


if __name__ == "__main__":
    a = sys.argv[sys.argv.index("--") + 1:]
    main(int(a[0]), int(a[1]))
