"""The Vehicle Lab's SAVED RECIPES against the C# probe (tools/vehicle_probe_drill.sh).

A recipe (Assets/FactorySource/VehicleLab/Recipes/*.json) stores the parts Blender's probe listed when it was saved -
name, vertices, centre, size - and keys every role and placement by the part's NAME. That is the product's own
oracle: a probe that names a part differently orphans the recipe's markings. For each recipe whose source is a
.glb/.gltf on disk, every stored part must be in the C# probe's rows for that source, with the same vertex count
and, where the part carries no placement, the same box.

    python recipe_check.py --list <projectDir>                          the recipes' .glb/.gltf sources that exist, one per line
    python recipe_check.py <projectDir> <csharp rows> [<differing out>]  SAME / DIFFERS per recipe; the sources of the
                                                                        recipes that differ are written to <differing out>

A recipe that sets a second model, a placement or an orientation is probed WITH them (probe_jobs.py: the rows behind the
key `<source>#<recipe>`, since 2026-10-03, step 3d) and judged on those rows - its B_ parts and its placed parts' boxes
included. Without such rows (an older row file, a second model that is not a .glb/.gltf), the B_ parts and the placed
parts' boxes are not judged, and said. Recipes whose source is an FBX/OBJ/.blend are Blender's, not judged.
A recipe is the user's data and can be STALE - saved before its source was re-cut or re-fused - so a difference is
not a verdict on the probe by itself: the drill has Blender probe the differing recipes' sources as they are today,
and only a C# row that differs from BLENDER's fails. (First run, 2026-10-02: 17 of 20 recipes the same; the other
three differ from today's Blender in exactly the way they differ from the C# probe.)
"""
import glob, json, os, sys
sys.stdout.reconfigure(encoding="utf-8")


def recipes(project):
    for f in sorted(glob.glob(os.path.join(project, "Assets", "FactorySource", "VehicleLab", "Recipes", "*.json"))):
        try:
            yield f, json.load(open(f, encoding="utf-8-sig"))
        except Exception as e:
            print(f"FAIL {os.path.basename(f)}: unreadable recipe ({e})")


def is_gltf(p):
    return p.lower().endswith((".glb", ".gltf"))


def key(p):
    return p.replace("\\", "/").lower()


def load_rows(path):
    files = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        t = line.rstrip("\r\n").split("\t", 2)
        if len(t) < 3 or t[0] != "ROW" or not t[2].startswith("PART|"):
            continue
        f = t[2].split("|")
        name = "|".join(f[1:len(f) - 6]); tail = f[len(f) - 6:]
        files.setdefault(t[1], {})[name] = (int(tail[0]), [float(x) for x in tail[1].split(",")], [float(x) for x in tail[2].split(",")])
    return files


def main(argv):
    if argv[1] == "--list":
        seen = set()
        for _, r in recipes(argv[2]):
            p = r.get("srcFile") or ""
            if p and is_gltf(p) and os.path.isfile(p) and key(p) not in seen:
                seen.add(key(p)); print(p.replace("\\", "/"))
        return 0
    project, rows = argv[1], load_rows(argv[2])
    fails = 0; judged = 0; parts_checked = 0; differing = []
    for f, r in recipes(project):
        name = os.path.basename(f); src = r.get("srcFile") or ""
        if not src or not is_gltf(src):
            print(f"NOTE {name}: source is {os.path.splitext(src)[1] or 'unset'} - Blender keeps those, not judged"); continue
        if not os.path.isfile(src):
            print(f"NOTE {name}: source {src} is not on this machine - not judged"); continue
        own_key = key(src) + "#" + os.path.splitext(name)[0]
        with_inputs = own_key in rows   # probed with the recipe's second model, placements and orientation
        have = rows.get(own_key) or rows.get(key(src))
        if have is None:
            print(f"DIFFERS {name}: the C# probe has no rows for {src}"); fails += 1; differing.append(src); continue
        judged += 1
        stored = r.get("parts") or []
        second = [p for p in stored if p["name"].startswith("B_")] if r.get("srcFile2") and not with_inputs else []
        own = [p for p in stored if p not in second]
        missing = [p["name"] for p in own if p["name"] not in have]
        verts = []; boxes = []; placed = 0; worst = 0.0
        ext = max([abs(v) for c, s in [(x[1], x[2]) for x in have.values()] for v in c + s] + [1e-6])
        tol = 0.0002 + 2e-6 * ext + 5e-5   # the recipe stores 4-decimal rows as float32
        for p in own:
            row = have.get(p["name"])
            if row is None:
                continue
            parts_checked += 1
            if p.get("verts") != row[0]:
                verts.append(f"{p['name']} {row[0]} vs {p.get('verts')}")
            off = p.get("offset") or {}; scl = p.get("scale") or {}
            if not with_inputs and (any(abs(off.get(a, 0)) > 1e-9 for a in "xyz") or any(abs(scl.get(a, 1) - 1) > 1e-9 for a in "xyz")):
                placed += 1; continue
            c = [p["center"][a] for a in "xyz"]; s = [p["size"][a] for a in "xyz"]
            d = max(abs(x - y) for x, y in zip(c + s, row[1] + row[2]))
            worst = max(worst, d)
            if d > tol:
                boxes.append(f"{p['name']} {row[1]}/{row[2]} vs {c}/{s}")
        problems = []
        if missing:
            problems.append(f"{len(missing)} of {len(own)} stored part names are not in the C# probe (e.g. {missing[:3]})")
        if verts:
            problems.append(f"{len(verts)} vertex counts differ (C# vs recipe), e.g. {verts[0]}")
        if boxes:
            problems.append(f"{len(boxes)} boxes differ (C# vs recipe), e.g. {boxes[0]}")
        extra = len(have) - (len(own) - len(missing))
        tail = f"{len(own)} stored parts" + (" - probed with the recipe's second model, placements and orientation" if with_inputs else "") + (f", {len(second)} second-model parts not judged" if second else "") + (f", {placed} placed parts' boxes not judged" if placed else "") + (f", {extra} probe parts the recipe does not store" if extra else "")
        if problems:
            fails += 1; differing.append(src); print(f"DIFFERS {name}: " + "; ".join(problems) + f" ({tail})")
        else:
            print(f"SAME {name}: every stored part is in the C# probe with its vertex count and box ({tail}; largest box difference {worst:.4f})")
    print(f"RECIPES {judged} judged, {parts_checked} parts, {judged - fails} the same, {fails} differ")
    if len(argv) > 3:
        with open(argv[3], "w", encoding="utf-8", newline="\n") as out:
            for d in differing:
                out.write(d.replace("\\", "/") + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
