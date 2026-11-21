# compare_parts.py - holds `MaxDrill stats` (the C# reader's model) to max_world.py (the second reader, in Python) for
# one file: the same parts by name, the same triangle count per part (and the same number of parts whose file holds UVs),
# and area, bounding box and the two measures that turn sign with the winding - the signed volume (a closed part) and
# the vector area (an open sheet) - equal within REL (2e-4) of the part's size. A part that is neither (a sheet with both its sides, a closed part of almost no
# volume) has both near zero, and its winding is NOT held here.   usage: compare_parts.py <cs.txt> <py.txt>
import sys

REL = 2e-4   # the model holds float32 positions relative to a pivot; the Python side sums doubles


def load(path):
    parts = []; total = None
    for line in open(path, encoding='utf-8', errors='replace'):
        f = line.split()
        if not f:
            continue
        if f[0] == 'TOTAL':
            total = line.strip()
        elif f[0] == 'PART':
            kv = dict(x.split('=', 1) for x in f[2:])
            parts.append((f[1], int(kv['tris']), int(kv['uv']), float(kv['area']), float(kv['vol']),
                          [float(x) for x in kv['lo'].split(',')], [float(x) for x in kv['hi'].split(',')],
                          [float(x) for x in kv['varea'].split(',')]))
        elif f[0] == 'REFUSED':
            print('FAIL - the C# reader refused the file: ' + line.strip()); sys.exit(1)
    return sorted(parts, key=lambda p: (p[0], p[1], p[3])), total


a, ta = load(sys.argv[1]); b, tb = load(sys.argv[2])
bad = []
if ta != tb:
    bad.append('totals differ: C# "%s" / Python "%s"' % (ta, tb))
if len(a) != len(b):
    bad.append('%d parts in C#, %d in Python' % (len(a), len(b)))
for x, y in zip(a, b):
    # the per-part uv flag means two things (Python: the file holds UVs for the part; the model: the part is WRITTEN with
    # UVs, which it is only on faces that show a picture or whose picture is missing): the TOTAL line's uvparts, which
    # both count from the file, is what is compared
    if x[0] != y[0] or x[1] != y[1]:
        bad.append('part %s tris %d  against  %s tris %d' % (x[0], x[1], y[0], y[1])); continue
    size = max(max(abs(v) for v in y[5] + y[6]), 1e-9)
    ext = max(max(y[6][k] - y[5][k] for k in range(3)), 1e-9)
    if abs(x[3] - y[3]) > REL * max(abs(y[3]), ext * ext * 1e-6):
        bad.append('part %s area %g against %g' % (x[0], x[3], y[3]))
    # the volume is taken about the part's own centre, so its error scales with the part's extent, not with how far
    # from the origin the part stands
    if abs(x[4] - y[4]) > REL * max(abs(y[4]), y[3] * ext):
        bad.append('part %s signed volume %g against %g' % (x[0], x[4], y[4]))
    if max(abs(x[7][k] - y[7][k]) for k in range(3)) > REL * 10 * y[3]:
        bad.append('part %s vector area %s against %s' % (x[0], x[7], y[7]))
    for k in range(3):
        if abs(x[5][k] - y[5][k]) > REL * size or abs(x[6][k] - y[6][k]) > REL * size:
            bad.append('part %s bounding box %s..%s against %s..%s' % (x[0], x[5], x[6], y[5], y[6])); break
if bad:
    print('FAIL - %d difference(s):' % len(bad))
    for l in bad[:20]:
        print('  ' + l)
    sys.exit(1)
print('PASS - %d parts, %s' % (len(a), ta))
