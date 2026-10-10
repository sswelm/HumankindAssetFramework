"""Plant missing records in a real decisions dump for the deploy gate: each must fail the drill.

usage: missing_decisions.py <output directory> <dump> <jobs file>

One job is cut out of the dump (the first that ran to its end, has a mesh object and at least one part) and written
once per mode with one kind of row taken away. Matching one-job lists and intact controls ensure failures come from
the mutation, not from other jobs missing from the dump. An abort job also tests the EXIT code and record shape.
"""
from pathlib import Path
import sys


def main(out, dump, jobs):
    blocks, cur = [], None
    for line in Path(dump).read_text(encoding="utf-8-sig").split("\n"):
        line = line.rstrip("\r")
        if line.startswith("JOB\t"):
            cur = [line]; blocks.append(cur)
        elif cur is not None and line:
            cur.append(line)
    def kinds(b):
        return {l.split("\t")[0] for l in b}
    job = next((b for b in blocks if {"DONE", "RANGE", "PART", "BOX", "LOG"} <= kinds(b) and not b[0].startswith(("JOB\tLEFT:", "JOB\tBAKELEFT:"))), None)
    if job is None:
        raise ValueError("no complete job in the dump for the missing-record regressions")
    out = Path(out); out.mkdir(parents=True, exist_ok=True)
    job_lines = {l.split('|')[0]: l for l in Path(jobs).read_text(encoding="utf-8-sig").splitlines() if l.strip()}
    def write(name, lines):
        (out / (name + ".txt")).write_text("\n".join(lines) + "\n", encoding="utf-8")
    def control(name, block):
        key = block[0].split('\t')[1]
        write(name + "_jobs", [job_lines[key]])
        write(name + "_intact", block)
    control("missing", job)
    def first(kind):
        return next(i for i, l in enumerate(job) if l.startswith(kind + "\t"))
    def last(kind):
        return max(i for i, l in enumerate(job) if l.startswith(kind + "\t"))
    cuts = {"log": last("LOG"), "matrix": first("M"), "transform": first("T"), "box": first("BOX"), "object": first("OBJ"),
            "part": first("PART"), "range": first("RANGE"), "done": first("DONE"),
            "bone": first("RBONE"), "boneof": first("BONEOF"), "anchor": first("ANCHOR"), "hull": first("HULL"), "pinv": first("PINV")}
    cuts.update({"curve": first("FC"), "log2": first("LOG2"), "act": first("ACT")})
    for mode, i in cuts.items():
        lines = list(job); del lines[i]
        write(mode, lines)
    # a baked curve one key short, and one with the last bit of its last value turned
    fc = first("FC")
    lines = list(job); lines[fc] = job[fc].rsplit("\t", 1)[0]
    write("key", lines)
    lines = list(job); last = job[fc][-1]; lines[fc] = job[fc][:-1] + ("0" if last != "0" else "1")
    write("value", lines)
    # a location curve labelled with a component a location does not have (3): it must not land in the quaternion's slot
    loc = next(i for i, l in enumerate(job) if l.startswith("FC" + "\t") and l.split("\t")[1].endswith(".location") and l.split("\t")[2] == "0")
    t = job[loc].split("\t"); t[2] = "3"
    lines = list(job); lines[loc] = "\t".join(t)
    write("index", lines)
    # the fire-window snapshot (5a): a job that has one - a snapshot row, a pose row of the new armature and an object
    # row after it, each missing, doubled in place of its neighbour, or under a name nothing has
    fire = next((b for b in blocks if "SNAP" in kinds(b) and "DONE" in kinds(b) and not b[0].startswith(("JOB\tLEFT:", "JOB\tBAKELEFT:"))), None)
    if fire is None:
        raise ValueError("no job with a fire-window snapshot in the dump")
    control("fire", fire)
    for kind in ("SNAP", "APB", "O4"):
        rows = [i for i, l in enumerate(fire) if l.startswith(kind + "\t")]
        lines = list(fire); del lines[rows[0]]
        write("fire_%s_missing" % kind, lines)
        lines = list(fire); lines[rows[1]] = fire[rows[0]]
        write("fire_%s_duplicate" % kind, lines)
        t = fire[rows[0]].split("\t"); t[2 if kind == "SNAP" else 1] = "no such name"
        lines = list(fire); lines[rows[0]] = "\t".join(t)
        write("fire_%s_unknown" % kind, lines)
        lines = list(fire); lines[rows[0]] = fire[rows[0]].rsplit("\t", 1)[0]
        write("fire_%s_short" % kind, lines)
        last = fire[rows[-1]][-1]
        lines = list(fire); lines[rows[-1]] = fire[rows[-1]][:-1] + ("0" if last != "0" else "1")
        write("fire_%s_value" % kind, lines)
    lines = [l for l in fire if not l.startswith("LOG3\t")]
    write("fire_LOG3_missing", lines)
    # the barrel retarget and the leg scale (5b, 5c): a job that re-keyed a bone - one of its Bezier curves, a pose row
    # and an object row after it, each missing, doubled in place of its neighbour, renamed, or one bit off
    gun = next((b for b in blocks if "LOG4" in kinds(b) and "DONE" in kinds(b) and any(l.startswith("FC4\t") and ":BEZIER:" in l for l in b)
                and not b[0].startswith(("JOB\tLEFT:", "JOB\tBAKELEFT:"))), None)
    if gun is None:
        raise ValueError("no job with a re-keyed bone in the dump")
    control("gun", gun)
    for kind in ("FC4", "APB4", "O5"):
        rows = [i for i, l in enumerate(gun) if l.startswith(kind + "\t") and (kind != "FC4" or ":BEZIER:" in l)]
        lines = list(gun); del lines[rows[0]]
        write("gun_%s_missing" % kind, lines)
        lines = list(gun); lines[rows[1]] = gun[rows[0]]
        write("gun_%s_duplicate" % kind, lines)
        t = gun[rows[0]].split("\t"); t[1] = 'pose.bones["no such bone"].location' if kind == "FC4" else "no such name"
        lines = list(gun); lines[rows[0]] = "\t".join(t)
        write("gun_%s_unknown" % kind, lines)
        last = gun[rows[-1]][-1]
        lines = list(gun); lines[rows[-1]] = gun[rows[-1]][:-1] + ("0" if last != "0" else "1")
        write("gun_%s_value" % kind, lines)
    lines = [l for l in gun if not l.startswith("LOG4\t")]
    write("gun_LOG4_missing", lines)
    # the frame sweep at the end (what the pose bones hold frame after frame, a Bezier curve evaluated between its
    # keys): a row missing, doubled in place of its neighbour of the same frame, renamed, cut short, one bit off - and
    # no sweep at all
    rows = [i for i, l in enumerate(gun) if l.startswith("SW\t")]
    if len(rows) < 12 or gun[rows[0]].split("\t")[1] != gun[rows[1]].split("\t")[1]:
        raise ValueError("the retarget job has no frame sweep of at least two bones")
    lines = list(gun); del lines[rows[1]]
    write("gun_SW_missing", lines)
    lines = list(gun); lines[rows[1]] = gun[rows[0]]
    write("gun_SW_duplicate", lines)
    t = gun[rows[1]].split("\t"); t[2] = "no such name"
    lines = list(gun); lines[rows[1]] = "\t".join(t)
    write("gun_SW_unknown", lines)
    lines = list(gun); lines[rows[1]] = gun[rows[1]].rsplit("\t", 1)[0]
    write("gun_SW_short", lines)
    last = gun[rows[-1]][-1]
    lines = list(gun); lines[rows[-1]] = gun[rows[-1]][:-1] + ("0" if last != "0" else "1")
    write("gun_SW_value", lines)
    write("gun_SW_none", [l for l in gun if not l.startswith("SW\t")])
    # a whole frame taken out of the middle, and the tail cut off: every remaining row is right - the dump just says less
    frames = []
    for i in rows:
        f = gun[i].split("\t")[1]
        if not frames or frames[-1][0] != f: frames.append((f, []))
        frames[-1][1].append(i)
    gone = set(frames[len(frames) // 2][1])
    write("gun_SW_frame", [l for i, l in enumerate(gun) if i not in gone])
    gone = set(i for _, g in frames[6:] for i in g)
    write("gun_SW_tail", [l for i, l in enumerate(gun) if i not in gone])
    # the recoil tail (5d): a job that keyed a RecoilArm - its measurements, the rebuilt bones, the arm's curves, the
    # pose rows, the pose matrices and the objects after it: each missing, doubled, renamed, cut short or one bit off;
    # the log line missing; the script's exit claimed
    rec = next((b for b in blocks if "R5" in kinds(b) and "DONE" in kinds(b) and any(l.startswith("RBONE5\tRecoilArm") for l in b)
                and not b[0].startswith(("JOB\tLEFT:", "JOB\tBAKELEFT:"))), None)
    if rec is None:
        raise ValueError("no job with a recoil tail in the dump")
    control("recoil", rec)
    for kind in ("R5", "RBONE5", "FC5", "APB5", "PM5", "O6"):
        rows = [i for i, l in enumerate(rec) if l.startswith(kind + "\t") and (kind != "FC5" or "RecoilArm" in l)]
        lines = list(rec); del lines[rows[0]]
        write("recoil_%s_missing" % kind, lines)
        lines = list(rec); lines[rows[1]] = rec[rows[0]]
        write("recoil_%s_duplicate" % kind, lines)
        t = rec[rows[0]].split("\t"); t[1 if kind != "FC5" else 1] = ('pose.bones["no such bone"].location' if kind == "FC5" else "no such name")
        lines = list(rec); lines[rows[0]] = "\t".join(t)
        write("recoil_%s_unknown" % kind, lines)
        lines = list(rec); lines[rows[0]] = rec[rows[0]].rsplit("\t", 1)[0]
        write("recoil_%s_short" % kind, lines)
        last = rec[rows[-1]][-1]
        lines = list(rec); lines[rows[-1]] = rec[rows[-1]][:-1] + ("0" if last != "0" else "1")
        write("recoil_%s_value" % kind, lines)
    write("recoil_LOG5_missing", [l for l in rec if not l.startswith("LOG5\t")])
    write("recoil_EXIT5_claimed", [l if not l.startswith("R5\t") else l for l in rec] + ["EXIT5\t1"])
    write("recoil_DIES5_claimed", list(rec) + ["DIES5\tValueError: Matrix.inverted(): singular"])
    # the bind (6): a job that bound at least two meshes, one of them from a copied datablock - a bind row, a vertex-group
    # row, a modifier row, a vertex hash, a sampled vertex, a custom-normal row, a datablock row and an object row after
    # it: each missing, doubled, renamed, cut short or one bit off; the log line missing; the script's death claimed
    bnd = next((b for b in blocks if "BIND" in kinds(b) and "DONE" in kinds(b) and sum(1 for l in b if l.startswith("BIND\t")) >= 2
                and any(l.startswith("N6\t") and "INT16_2D" in l for l in b) and not b[0].startswith(("JOB\tLEFT:", "JOB\tBAKELEFT:"))), None)
    if bnd is None:
        raise ValueError("no job with two bound meshes and custom normals in the dump")
    control("bind", bnd)
    for kind in ("BIND", "VG6", "MOD6", "V6", "VX6", "N6", "DATA6", "O7"):
        rows = [i for i, l in enumerate(bnd) if l.startswith(kind + "\t") and (kind != "N6" or "INT16_2D" in l)]
        lines = list(bnd); del lines[rows[0]]
        write("bind_%s_missing" % kind, lines)
        lines = list(bnd); lines[rows[1]] = bnd[rows[0]]
        write("bind_%s_duplicate" % kind, lines)
        t = bnd[rows[0]].split("\t"); t[1] = "no such name"
        lines = list(bnd); lines[rows[0]] = "\t".join(t)
        write("bind_%s_unknown" % kind, lines)
        lines = list(bnd); lines[rows[0]] = bnd[rows[0]].rsplit("\t", 1)[0]
        write("bind_%s_short" % kind, lines)
        last = bnd[rows[-1]][-1]
        lines = list(bnd); lines[rows[-1]] = bnd[rows[-1]][:-1] + ("0" if last != "0" else "1")
        write("bind_%s_value" % kind, lines)
    write("bind_LOG6_missing", [l for l in bnd if not l.startswith("LOG6\t")])
    write("bind_DIES6_claimed", list(bnd) + ["DIES6\tRuntimeError: Error: Mesh has no vertices"])
    abort = next((b for b in blocks if "EXIT\t1" in b and "DONE" in kinds(b) and not b[0].startswith("JOB\tLEFT:")), None)
    if abort is None:
        raise ValueError("no abort job in the dump for the EXIT regressions")
    control("exit", abort)
    index = abort.index("EXIT\t1")
    for mode, replacement in {"exit_zero": ["EXIT\t0"], "exit_short": ["EXIT"], "exit_duplicate": ["EXIT\t1", "EXIT\t1"]}.items():
        lines = list(abort); lines[index:index + 1] = replacement
        write(mode, lines)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
