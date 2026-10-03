"""The probe JOBS for tools/vehicle_probe_drill.sh (step 3d, 2026-10-03): probes with the Lab's OTHER inputs - a second
model, per-part placements, an orientation - run by BOTH ProbeDrill.cs and blender_probe_many.py from one JSON file, the
rows compared behind each job's key by compare_probe.py.

    python probe_jobs.py <fixtureDir> <projectDir|-> <out.json>      -> writes the jobs, prints one key per line

Two kinds of job:
  * the FIXTURE jobs (one rule each, the files from naming_fixtures.py): second_model_a.glb with second_model_b.glb merged
    at an offset, a rotation and a per-axis scale (the shear case: B's root is turned 30 degrees, so the scale is a shear in
    its frame - baked into the vertices exactly as Blender bakes it), the same pair with A and B swapped (the single-mesh
    split on each side, and the order the loose parts are linked in), placement_nested.glb with two placements (a part
    with children and a loose part of a split), insideout.glb judged under proberot=0,0,90;
  * the RECIPE jobs: every saved recipe whose source is a .glb/.gltf on disk and that sets a second model (also .glb/.gltf,
    on disk), a placement or an orientation - probed with the recipe's exact arguments, formatted as the Lab formats them
    (merge2: five decimals, the legacy uniform scale times the per-axis one; parttx: four decimals; proberot: two). These
    are the probe's real inputs: recipe_check.py judges the stored B_ and placed parts against these rows.
"""
import glob, json, os, struct, sys
from decimal import Decimal, ROUND_HALF_UP
sys.stdout.reconfigure(encoding="utf-8")


def fmt(v, places):
    """C#'s float.ToString("0.#####") (places decimals) on .NET Framework / Unity's Mono, measured 2026-10-03: the float32
    value at 7 significant digits, then rounded half AWAY from zero on that decimal (1.03125 -> 1.0313; printf's half-even on
    the binary gave 1.0312), trailing zeros stripped, never "-0"."""
    f = struct.unpack("<f", struct.pack("<f", float(v)))[0]
    d = Decimal("%.7g" % abs(f)).quantize(Decimal(1).scaleb(-places), rounding=ROUND_HALF_UP)
    s = format(d, "f")
    if "." in s:
        s = s.rstrip("0").rstrip(".")
    if s in ("", "0"):
        return "0"
    return ("-" if f < 0 else "") + s


def positive(v):
    v = float(v)
    return v if v > 0 and v != float("inf") and v == v else 1.0


def merge2_text(path, off, rot, legacy_scale, scale):
    sx, sy, sz = (positive(legacy_scale) * positive(scale.get(a, 1.0)) for a in "xyz")
    return "%s|%s,%s,%s|%s,%s,%s|%s,%s,%s" % (path.replace("\\", "/"), fmt(off["x"], 5), fmt(off["y"], 5), fmt(off["z"], 5),
                                             fmt(rot["x"], 5), fmt(rot["y"], 5), fmt(rot["z"], 5), fmt(positive(sx), 5), fmt(positive(sy), 5), fmt(positive(sz), 5))


def placement_line(p):
    off = p.get("offset") or {}; scl = p.get("scale") or {}
    return "%s|%s,%s,%s|%s,%s,%s" % (p["name"], fmt(off.get("x", 0), 4), fmt(off.get("y", 0), 4), fmt(off.get("z", 0), 4),
                                     fmt(scl.get("x", 1), 4), fmt(scl.get("y", 1), 4), fmt(scl.get("z", 1), 4))


def is_placed(p):
    off = p.get("offset") or {}; scl = p.get("scale") or {}
    return any(abs(off.get(a, 0)) > 1e-9 for a in "xyz") or any(abs(scl.get(a, 1) - 1) > 1e-9 for a in "xyz")


def key_of(path, tag):
    return path.replace("\\", "/").lower() + "#" + tag


def fixture_jobs(fx):
    a = os.path.join(fx, "second_model_a.glb").replace("\\", "/"); b = os.path.join(fx, "second_model_b.glb").replace("\\", "/"); c = os.path.join(fx, "second_model_c.glb").replace("\\", "/")
    nested = os.path.join(fx, "placement_nested.glb").replace("\\", "/"); split = os.path.join(fx, "placement_split.glb").replace("\\", "/"); io_ = os.path.join(fx, "insideout.glb").replace("\\", "/")
    jobs = []
    if os.path.isfile(a) and os.path.isfile(b):
        jobs.append({"key": key_of(a, "merged"), "file": a, "merge2": b + "|0.5,0,2|0,0,30|2,1,1"})
        jobs.append({"key": key_of(b, "merged-swapped"), "file": b, "merge2": a + "|0,-3,0|10,20,30|0.5"})
        jobs.append({"key": key_of(a, "merged-placed"), "file": a, "merge2": b + "|0,0,0|0,0,0|1,1,1", "parttx": ["B_Object_1.001|1,0,0|1,1,1", "Deck|0,0,0.5|2,1,1"]})
    if os.path.isfile(a) and os.path.isfile(c):
        jobs.append({"key": key_of(a, "merged-split"), "file": a, "merge2": c + "|0,0,-4|0,0,0|1"})
        jobs.append({"key": key_of(c, "merged-split-swapped"), "file": c, "merge2": a + "|0,0,4|0,0,0|1"})
        jobs.append({"key": key_of(c, "merged-both-split"), "file": c, "merge2": c + "|0,0,-6|0,0,0|1"})   # both sources single meshes: both split, the first's loose parts linked before the second's
    if os.path.isfile(nested):
        jobs.append({"key": key_of(nested, "placed"), "file": nested, "parttx": ["Carrier|0,0,2|1,1,1", "Turned|0,0,0|2,1,1", "NoSuchPart|1,1,1|1,1,1", "Plain|0.25,0,0|0,1,1"]})
    if os.path.isfile(split):
        jobs.append({"key": key_of(split, "placed"), "file": split, "parttx": ["Loose.001|0,1,0|0.5,0.5,0.5"]})
    if os.path.isfile(io_):
        jobs.append({"key": key_of(io_, "proberot"), "file": io_, "proberot": "0,0,90"})
        jobs.append({"key": key_of(io_, "proberot-x"), "file": io_, "proberot": "90,0,0"})
    return jobs


def recipe_jobs(project):
    jobs = []
    if not project or project == "-":
        return jobs
    for f in sorted(glob.glob(os.path.join(project, "Assets", "FactorySource", "VehicleLab", "Recipes", "*.json"))):
        try:
            r = json.load(open(f, encoding="utf-8-sig"))
        except Exception:
            continue
        src = (r.get("srcFile") or "").strip()
        if not src.lower().endswith((".glb", ".gltf")) or not os.path.isfile(src):
            continue
        job = {"key": key_of(src, os.path.splitext(os.path.basename(f))[0]), "file": src.replace("\\", "/")}
        src2 = (r.get("srcFile2") or "").strip()
        if src2:
            if not src2.lower().endswith((".glb", ".gltf")) or not os.path.isfile(src2):
                continue   # Blender keeps a non-glTF second model; a missing one cannot be probed on either side
            job["merge2"] = merge2_text(src2, r.get("model2Off") or {"x": 0, "y": 0, "z": 0}, r.get("model2Rot") or {"x": 0, "y": 0, "z": 0},
                                        r.get("model2Scale", 1.0), r.get("model2ScaleXYZ") or {"x": 1, "y": 1, "z": 1})
        placed = [placement_line(p) for p in (r.get("parts") or []) if is_placed(p)]
        if placed:
            job["parttx"] = placed
        rot = r.get("modelRot") or {}
        if any(abs(rot.get(a, 0)) > 1e-9 for a in "xyz"):
            job["proberot"] = "%s,%s,%s" % (fmt(rot.get("x", 0), 2), fmt(rot.get("y", 0), 2), fmt(rot.get("z", 0), 2))
        if "merge2" in job or "parttx" in job or "proberot" in job:
            jobs.append(job)
    return jobs


def main(argv):
    fx, project, out = argv[1], argv[2], argv[3]
    jobs = fixture_jobs(fx) + recipe_jobs(project)
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(jobs, f, indent=1, ensure_ascii=False)
    for j in jobs:
        print(j["key"])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
