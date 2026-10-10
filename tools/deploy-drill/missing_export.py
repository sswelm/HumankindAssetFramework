"""Plant missing and changed records in a real export dump for the deploy gate (part 8a): each must fail the drill.

usage: missing_export.py <output directory> <export dump> <jobs file>

One job is cut out of the dump (the first that ran to its end with a skinned mesh - a PRIM, a SKIN, an IBM and an ANIM
row - and is not marked as left to Blender) and written as exp_intact.txt beside a one-job jobs.txt, then once per
mutation: a row of each kind missing, doubled in place of its neighbour, one character changed, or cut short; the log
lines missing; the written line missing; the end row missing; a death or an exit of the script claimed.
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
    job = next((b for b in blocks if {"DONE", "SCENE", "NODE", "SKIN", "IBM", "MESH", "PRIM", "ANIM", "LOG8"} <= kinds(b)
                and ":" not in b[0].split("\t")[1]), None)
    if job is None:
        raise ValueError("no complete exported job with a skinned mesh in the dump for the missing-export regressions")
    out = Path(out); out.mkdir(parents=True, exist_ok=True)
    key = job[0].split("\t")[1]
    job_lines = {l.split("|")[0]: l for l in Path(jobs).read_text(encoding="utf-8-sig").splitlines() if l.strip()}
    def write(name, lines):
        (out / (name + ".txt")).write_text("\n".join(lines) + "\n", encoding="utf-8")
    write("jobs", [job_lines[key]])
    write("exp_intact", job)
    def rows(kind):
        return [i for i, l in enumerate(job) if l.startswith(kind + "\t")]
    def flip(s):
        # the last character turned: a hex digit, a name's letter, a count's digit
        c = s[-1]
        return s[:-1] + ("0" if c != "0" else "1")
    for kind in ("SCENE", "NODE", "SKIN", "IBM", "MESH", "PRIM", "ANIM"):
        r = rows(kind)
        lines = list(job); del lines[r[0]]
        write("exp_%s_missing" % kind, lines)
        if len(r) > 1:
            lines = list(job); lines[r[1]] = job[r[0]]
            write("exp_%s_duplicate" % kind, lines)
        else:
            lines = list(job); lines.insert(r[0] + 1, job[r[0]])
            write("exp_%s_duplicate" % kind, lines)
        # one value of the row changed - a field the drill compares: a joint's translation bit, a scene's name, a skin's
        # joint index, a mesh's primitive count, an animation's name; the last field (a hash) of an IBM or PRIM row
        if kind == "NODE":
            # a joint's translation: the first node row that carries one
            i = next(i for i in r if job[i].split("\t")[6] != "-")
            t = job[i].split("\t"); t[6] = flip(t[6]); lines = list(job); lines[i] = "\t".join(t)
        elif kind in ("SCENE", "ANIM"):
            t = job[r[0]].split("\t"); t[1 if kind == "SCENE" else 2] = t[1 if kind == "SCENE" else 2] + "x"; lines = list(job); lines[r[0]] = "\t".join(t)
        elif kind in ("SKIN", "MESH"):
            t = job[r[0]].split("\t"); t[3] = flip(t[3]); lines = list(job); lines[r[0]] = "\t".join(t)
        else:
            lines = list(job); lines[r[0]] = flip(job[r[0]])
        write("exp_%s_value" % kind, lines)
        lines = list(job); lines[r[0]] = job[r[0]].rsplit("\t", 1)[0]
        write("exp_%s_short" % kind, lines)
    # the log: the sanitize line gone; the written line gone; the end row gone
    log = rows("LOG8")
    lines = list(job); del lines[next(i for i in log if job[i].startswith("LOG8\tDEPLOY sanitized"))]
    write("exp_LOG8_missing", lines)
    lines = list(job); del lines[next(i for i in log if job[i].startswith("LOG8\tDEPLOY wrote:"))]
    write("exp_WROTE_missing", lines)
    lines = list(job); del lines[rows("DONE")[0]]
    write("exp_DONE_missing", lines)
    # the script's death or exit claimed where the port goes on
    lines = list(job); lines.insert(log[-1] + 1, "DIES8\tRuntimeError: planted")
    write("exp_DIES8_claimed", lines)
    lines = [job[0]] + [job[i] for i in log] + ["EXIT8\t1", job[rows("DONE")[0]]]
    write("exp_EXIT8_claimed", lines)


if __name__ == "__main__":
    main(*sys.argv[1:4])
