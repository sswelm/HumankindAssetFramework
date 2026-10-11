"""Plant missing and changed records in a real material dump for the deploy gate (part 8a-ii): each must fail the drill.

usage: missing_materials.py <output directory> <material dump>

One file is cut out of the dump (the first with a MAT, a TEX, a SAMP and an IMG row, that Blender ran to its end)
and written as mat_intact.txt, then once per mutation: a row of each kind missing, doubled in place of its neighbour,
one value changed, or cut short; the end row missing; a death claimed.
"""
from pathlib import Path
import json
import sys


def main(out, dump):
    blocks, cur = [], None
    for line in Path(dump).read_text(encoding="utf-8-sig").split("\n"):
        line = line.rstrip("\r")
        if line.startswith("FILE\t"):
            cur = [line]; blocks.append(cur)
        elif cur is not None and line:
            cur.append(line)
    def kinds(b):
        return {l.split("\t")[0] for l in b}
    job = next((b for b in blocks if {"DONE", "MAT", "TEX", "SAMP", "IMG"} <= kinds(b) and "DIES" not in kinds(b)), None)
    if job is None:
        raise ValueError("no complete file with a textured material in the dump for the missing-material regressions")
    out = Path(out); out.mkdir(parents=True, exist_ok=True)
    def write(name, lines):
        (out / (name + ".txt")).write_text("\n".join(lines) + "\n", encoding="utf-8")
    write("mat_intact", job)
    def rows(kind):
        return [i for i, l in enumerate(job) if l.startswith(kind + "\t")]
    for kind in ("MAT", "TEX", "SAMP", "IMG"):
        r = rows(kind)
        lines = list(job); del lines[r[0]]
        write("mat_%s_missing" % kind, lines)
        lines = list(job)
        if len(r) > 1: lines[r[1]] = job[r[0]]
        else: lines.insert(r[0] + 1, job[r[0]])
        write("mat_%s_duplicate" % kind, lines)
        t = job[r[0]].split("\t")
        if kind in ("MAT", "SAMP"):
            j = json.loads(t[2]); j["name" if kind == "MAT" else "magFilter"] = (j.get("name", "") + "x") if kind == "MAT" else 9728 if j.get("magFilter") != 9728 else 9729
            t[2] = json.dumps(j, sort_keys=True, separators=(",", ":"))
        elif kind == "TEX":
            t[2] = str(int(t[2]) + 1)
        else:
            t[5] = t[5][:-1] + ("0" if t[5][-1] != "0" else "1")
        lines = list(job); lines[r[0]] = "\t".join(t)
        write("mat_%s_value" % kind, lines)
        lines = list(job); lines[r[0]] = job[r[0]].rsplit("\t", 1)[0]
        write("mat_%s_short" % kind, lines)
    lines = list(job); del lines[rows("DONE")[0]]
    write("mat_DONE_missing", lines)
    lines = [job[0], "DIES\tRuntimeError: planted", job[rows("DONE")[0]]]
    write("mat_DIES_claimed", lines)

    done = rows("DONE")[0]
    lines = list(job); lines[done] = "DONE\twrong"; write("mat_DONE_wrong", lines)
    lines = list(job); lines[done] = "DONE"; write("mat_DONE_short", lines)
    lines = list(job); lines[done] += "\textra"; write("mat_DONE_extra", lines)
    lines = list(job); lines.insert(done, job[done]); write("mat_DONE_duplicate", lines)
    write("mat_DONE_trailing", job + ["MATS\tplanted"])
    lines = list(job); lines.insert(done, "FAIL\tplanted\tclaimed failure"); write("mat_FAIL_claimed", lines)
    write("mat_FILE_duplicate", job + job)
    lines = list(job); lines[0] += "\textra"; write("mat_FILE_extra", lines)
    lines = list(job); lines.insert(done, "UNKNOWN\tplanted"); write("mat_UNKNOWN", lines)
    write("mat_MATS_missing", [l for l in job if not l.startswith("MATS\t")])
    lines = list(job); lines.insert(done, "DIES\tRuntimeError: planted"); write("mat_DIES_rows", lines)
    write("mat_DIES_duplicate", [job[0], "DIES\tRuntimeError: planted", "DIES\tRuntimeError: twice", job[done]])


if __name__ == "__main__":
    main(*sys.argv[1:3])
