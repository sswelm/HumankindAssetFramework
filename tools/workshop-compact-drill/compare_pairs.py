"""Blender's view of each original beside its compacted copy (tools/workshop_compact_drill.sh): they must be THE SAME.

    python compare_pairs.py <pairs.txt> <blender.txt>

pairs.txt: one line per pair,  <original> <TAB> <compacted>.
blender.txt: the lines tools/glb-reader-drill/blender_counts.py prints (BLENDER <TAB> path <TAB> key=value ...).

Both files are imported by the same importer, and compaction leaves every live accessor's content and the order of the
live meshes as they were - so not "close": every count and every printed value (triangles, materials, images, joints,
animations, box, area, centroid, winding sum, bone names, durations) must be equal as printed. Only the path and the
import time may differ. A pair Blender did not report on both sides is a FAIL. Exit 1 on any FAIL, or when nothing
was compared.
"""
import os, sys
sys.stdout.reconfigure(encoding="utf-8")


def key(path):
    return os.path.abspath(path).replace("\\", "/").lower()


pairs = [l.rstrip("\r\n").split("\t") for l in open(sys.argv[1], encoding="utf-8") if l.strip()]
stats = {}
for line in open(sys.argv[2], encoding="utf-8", errors="replace"):
    t = line.rstrip("\r\n").split("\t")
    if len(t) < 3 or t[0] != "BLENDER":
        continue
    stats[key(t[1])] = dict(f.split("=", 1) for f in t[2:] if "=" in f and not f.startswith("ms="))

failed = compared = 0
for original, compacted in pairs:
    name = os.path.basename(original)
    a, b = stats.get(key(original)), stats.get(key(compacted))
    if a is None or b is None:
        print("FAIL %s: Blender did not report on %s" % (name, "the original" if a is None else "the compacted copy")); failed += 1; continue
    compared += 1
    diff = [k for k in sorted(set(a) | set(b)) if a.get(k) != b.get(k)]
    if diff:
        failed += 1
        print("FAIL %s: Blender sees a difference - %s" % (name, "; ".join("%s %s -> %s" % (k, a.get(k), b.get(k)) for k in diff)[:400]))
    else:
        print("PASS %s: tris=%s materials=%s images=%s joints=%s, box/area/centroid/winding/bones/durations equal as printed (%.1f -> %.1f MB)"
              % (name, a.get("tris"), a.get("materials"), a.get("images"), a.get("joints"), os.path.getsize(original) / 1e6, os.path.getsize(compacted) / 1e6))
print("COMPARED %d FAILED %d" % (compared, failed))
sys.exit(1 if failed or compared == 0 else 0)
