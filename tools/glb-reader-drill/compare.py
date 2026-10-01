# The comparison: one FILE line from the C# reader against one BLENDER line from blender_counts.py per file, on the
# counts (exact) and the values (with the tolerances named here), PASS/FAIL per file, exit 1 on any FAIL.
#   bbox, centroid  absolute tolerance = 1e-3 of the model's largest extent (float32 positions through two matrix chains)
#   area            relative 1e-4
#   nsum            absolute 1e-3 of the total area (a flipped winding moves it by whole triangle areas)
#   bones           exact, sorted
#   durations       the SET of distinct SPANS per animation (earliest first key to latest last key over every channel -
#                   what a Blender 5.1 slotted action spans), to the frame (Blender snaps to frames at 24 fps)
#   pose            both sides evaluate animation 0 at time 0 (blender_counts.py / HafTransforms.PoseAt); the skinned
#                   vertices go through the weighted joint blend on both sides
import sys
sys.stdout.reconfigure(encoding="utf-8")   # a sample is named with emoji

def parse(path, tag, by_basename):
    rows = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        line = line.rstrip("\r\n")
        if not line.startswith(tag + "\t"):
            continue
        t = line.split("\t")
        key = t[1].replace("\\", "/").lower()
        if by_basename:
            key = key.rsplit("/", 1)[-1].rsplit(".", 1)[0]   # without the extension: a .gltf source comes back as a written .glb
        d = {"name": t[1]}
        for f in t[2:]:
            k, _, v = f.partition("=")
            d[k] = v
        rows[key] = d
    return rows

def floats(v):
    return [float(x) for x in v.split(",")] if v else []

# usage: compare.py <left: FILE lines> <right> [--right-tag FILE|BLENDER] [--basename]
#   --right-tag FILE  compares the reader's view of two files (the writer drill: the original and the written copy)
#   --basename        keys both sides by file name alone, extension dropped (the written copy lives in another folder, as .glb)
right_tag = "BLENDER"; by_basename = False
for i, a in enumerate(sys.argv[3:]):
    if a == "--right-tag": right_tag = sys.argv[3 + i + 1]
    if a == "--basename": by_basename = True
csharp = parse(sys.argv[1], "FILE", by_basename)
blender = parse(sys.argv[2], right_tag, by_basename)
fails = 0
compared = 0
for name, b in blender.items():
    c = csharp.get(name)
    short = "/".join(name.split("/")[-2:])
    if c is None:
        print("FAIL %s: the right side has it, the left side has no line" % short); fails += 1; continue
    compared += 1
    problems = []
    for k in ("tris", "materials", "images", "joints"):
        # Blender has the materials a primitive USES plus one it invents for an unmaterialed COLOR_0 primitive: the C# line
        # states that count too (blendermaterials); file against file, the file's own count
        ck = "blender" + k if k in ("materials", "images") and right_tag == "BLENDER" and ("blender" + k) in c else k   # and the images those materials name
        if c.get(ck) != b.get(k):
            problems.append("%s C# %s vs Blender %s" % (k, c.get(ck), b.get(k)))
    cb, bb = floats(c.get("bbox", "")), floats(b.get("bbox", ""))
    extent = max([abs(x) for x in bb] + [1e-6]) if bb else 1.0
    tol = 1e-3 * extent
    if len(cb) != len(bb):
        problems.append("bbox C# %s vs Blender %s" % (c.get("bbox"), b.get("bbox")))
    elif any(abs(x - y) > tol for x, y in zip(cb, bb)):
        ci = floats(c.get("bboxidentity", ""))
        alt = " (the file's positions unblended would give %s)" % c.get("bboxidentity") if ci else ""
        problems.append("bbox differs by up to %.5f (tolerance %.5f): C# %s vs Blender %s%s" % (max(abs(x - y) for x, y in zip(cb, bb)), tol, c.get("bbox"), b.get("bbox"), alt))
    ca, ba = float(c.get("area", "0")), float(b.get("area", "0"))
    if abs(ca - ba) > 1e-4 * max(ba, 1e-9):
        problems.append("area C# %.5f vs Blender %.5f" % (ca, ba))
    cc, bc = floats(c.get("centroid", "")), floats(b.get("centroid", ""))
    if len(cc) == 3 and len(bc) == 3 and any(abs(x - y) > tol for x, y in zip(cc, bc)):
        problems.append("centroid differs by up to %.5f (tolerance %.5f): C# %s vs Blender %s" % (max(abs(x - y) for x, y in zip(cc, bc)), tol, c.get("centroid"), b.get("centroid")))
    cn, bn = floats(c.get("nsum", "")), floats(b.get("nsum", ""))
    ntol = 1e-3 * max(ba, 1e-9)
    if len(cn) == 3 and len(bn) == 3 and any(abs(x - y) > ntol for x, y in zip(cn, bn)):
        problems.append("normal sum (winding) differs by up to %.5f (tolerance %.5f): C# %s vs Blender %s" % (max(abs(x - y) for x, y in zip(cn, bn)), ntol, c.get("nsum"), b.get("nsum")))
    if c.get("bones", "") != b.get("bones", ""):
        problems.append("bone names differ: C# [%s] vs Blender [%s]" % (c.get("bones", ""), b.get("bones", "")))
    # Blender 5.1 makes ONE action per glTF animation (slotted actions), spanning its earliest first key to its latest
    # last key over every target; the C# side states the same span per animation. The SET of distinct spans (to the
    # frame) must match. (Blender before 4.4 made one action per animated object; this drill runs against 5.1.)
    cd, bd = sorted(set(round(x * 24) / 24 for x in floats(c.get("sorteddurations", "")))), sorted(set(round(x * 24) / 24 for x in floats(b.get("durations", b.get("sorteddurations", "")))))
    if len(cd) != len(bd) or any(abs(x - y) > 1.0 / 24 + 1e-6 for x, y in zip(cd, bd)):
        problems.append("distinct animation spans differ: C# %s vs Blender %s" % (c.get("sorteddurations"), b.get("durations")))
    if problems:
        print("FAIL %s: %s" % (short, "; ".join(problems))); fails += 1
    else:
        print("PASS %s: tris=%s materials=%s images=%s joints=%s, box/area/centroid/winding/bones/durations agree (left %s ms, right %s ms)"
              % (short, c["tris"], c["materials"], c["images"], c["joints"], c.get("ms"), b.get("ms")))
print("COMPARED %d FAILED %d" % (compared, fails))
sys.exit(1 if fails or compared == 0 else 0)
