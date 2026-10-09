"""Plant missing records in a real decisions dump for the deploy gate: each must fail the drill.

usage: missing_decisions.py <output directory> <dump>

One job is cut out of the dump (the first that ran to its end, has a mesh object and at least one part) and written
once per mode with one kind of row taken away.
"""
from pathlib import Path
import sys


def main(out, dump):
    blocks, cur = [], None
    for line in Path(dump).read_text(encoding="utf-8-sig").split("\n"):
        line = line.rstrip("\r")
        if line.startswith("JOB\t"):
            cur = [line]; blocks.append(cur)
        elif cur is not None and line:
            cur.append(line)
    def kinds(b):
        return {l.split("\t")[0] for l in b}
    job = next((b for b in blocks if {"DONE", "RANGE", "PART", "BOX", "LOG"} <= kinds(b) and not b[0].startswith("JOB\tLEFT:")), None)
    if job is None:
        raise ValueError("no complete job in the dump for the missing-record regressions")
    out = Path(out); out.mkdir(parents=True, exist_ok=True)
    def first(kind):
        return next(i for i, l in enumerate(job) if l.startswith(kind + "\t"))
    def last(kind):
        return max(i for i, l in enumerate(job) if l.startswith(kind + "\t"))
    cuts = {"log": last("LOG"), "matrix": first("M"), "transform": first("T"), "box": first("BOX"), "object": first("OBJ"),
            "part": first("PART"), "range": first("RANGE"), "done": first("DONE")}
    for mode, i in cuts.items():
        lines = list(job); del lines[i]
        (out / (mode + ".txt")).write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
