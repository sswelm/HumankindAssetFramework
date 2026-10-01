"""The C# probe's rows beside Blender's, per file (tools/vehicle_probe_drill.sh): PASS/FAIL per file, exit 1 on any FAIL.

    python compare_probe.py <csharp.txt> <blender.txt>

Both files hold lines  ROW <TAB> <file key> <TAB> PART|name|verts|cx,cy,cz|sx,sy,sz|vis|bone|flip   (and RIGBONE|name|count|c|s).
Compared, per part NAME (the recipe key - a name on one side only is a FAIL):
  verts, bone     exact
  centre, size    absolute tolerance = 0.0002 + 2e-6 of the model's largest extent: the rows print 4 decimals and the
                  positions are float32 through two matrix chains (first full run, 2026-10-02: the largest difference
                  on 31 registry files was 0.0003, on a model 4,422 units long). Both sides are posed at the first
                  clip's start (vehicle_rig.py posestart=1; Blender's untouched import is a blend one frame in)
  order           the rows must come in the same order (the Lab lists them as given)
  RIGBONE         name, count exact; centre, size with the same tolerance; order
Not compared yet, said: vis (field 6, step 3b) and flip (field 8, step 3c) - the C# side prints 1 and 0 for every part.
"""
import sys
sys.stdout.reconfigure(encoding="utf-8")


def load(path):
    files = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        line = line.rstrip("\r\n")
        t = line.split("\t", 2)
        if len(t) < 3 or t[0] != "ROW":
            continue
        row = t[2]
        d = files.setdefault(t[1], {"PART": [], "RIGBONE": []})
        if row.startswith("PART|"):
            f = row.split("|")
            # a '|' inside a name folds back into the name: the numeric tail is fixed (verts, c, s, vis, bone, flip)
            name = "|".join(f[1:len(f) - 6]); tail = f[len(f) - 6:]
            d["PART"].append({"name": name, "verts": tail[0], "c": tail[1], "s": tail[2], "vis": tail[3], "bone": tail[4].strip(), "flip": tail[5]})
        elif row.startswith("RIGBONE|"):
            f = row.split("|")
            name = "|".join(f[1:len(f) - 3]); tail = f[len(f) - 3:]
            d["RIGBONE"].append({"name": name, "verts": tail[0], "c": tail[1], "s": tail[2]})
    return files


def floats(s):
    out = []
    for x in s.split(","):
        try:
            out.append(float(x))
        except ValueError:
            out.append(float("nan"))
    return out


def close(a, b, tol):
    return len(a) == len(b) and all((x != x and y != y) or abs(x - y) <= tol for x, y in zip(a, b))


def main():
    cs, bl = load(sys.argv[1]), load(sys.argv[2])
    fails = 0; compared = 0
    for key in sorted(bl):
        short = "/".join(key.split("/")[-2:])
        if key not in cs:
            print(f"FAIL {short}: Blender probed it, the C# side has no rows"); fails += 1; continue
        compared += 1
        problems = []
        for kind in ("PART", "RIGBONE"):
            b, c = bl[key][kind], cs[key][kind]
            ext = max([abs(v) for r in b for v in floats(r["c"]) + floats(r["s"]) if v == v] + [1e-6])
            tol = 0.0002 + 2e-6 * ext
            bn, cn = [r["name"] for r in b], [r["name"] for r in c]
            if bn != cn:
                only_b = [n for n in bn if n not in cn]; only_c = [n for n in cn if n not in bn]
                if only_b or only_c:
                    problems.append(f"{kind} names differ: {len(bn)} in Blender, {len(cn)} in C#; only Blender {only_b[:4]}, only C# {only_c[:4]}")
                else:
                    first = next(i for i in range(len(bn)) if bn[i] != cn[i])
                    problems.append(f"{kind} order differs from row {first}: Blender {bn[first]!r}, C# {cn[first]!r}")
            cby = {r["name"]: r for r in c}
            bad = {"verts": [], "bone": [], "centre": [], "size": []}
            worst = 0.0
            for r in b:
                o = cby.get(r["name"])
                if o is None:
                    continue
                if r["verts"] != o["verts"]:
                    bad["verts"].append(f"{r['name']} {o['verts']} vs {r['verts']}")
                if kind == "PART" and r["bone"] != o["bone"]:
                    bad["bone"].append(f"{r['name']} {o['bone']!r} vs {r['bone']!r}")
                for field, label in (("c", "centre"), ("s", "size")):
                    fa, fb = floats(o[field]), floats(r[field])
                    if not close(fa, fb, tol):
                        bad[label].append(f"{r['name']} {o[field]} vs {r[field]}")
                    worst = max([worst] + [abs(x - y) for x, y in zip(fa, fb) if x == x and y == y])
            for label, items in bad.items():
                if items:
                    problems.append(f"{kind} {label}: {len(items)} of {len(b)} differ (C# vs Blender), e.g. {items[0]}")
            if kind == "PART":
                part_worst, part_tol, n_parts = worst, tol, len(b)
        if problems:
            fails += 1
            print(f"FAIL {short}: " + "; ".join(problems))
        else:
            print(f"PASS {short}: {n_parts} parts, {len(bl[key]['RIGBONE'])} rig bones - names, order, verts, bones, boxes agree (largest box difference {part_worst:.4f}, tolerance {part_tol:.4f})")
    print(f"COMPARED {compared} FAILED {fails}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
