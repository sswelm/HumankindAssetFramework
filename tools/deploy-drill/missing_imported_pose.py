"""Mutate imported pose rows in a real dump, with a matching one-job intact control.

usage: missing_imported_pose.py <output directory> <dump> <jobs file>
"""
from pathlib import Path
import sys


def main(out, dump, jobs):
    blocks = []
    for line in Path(dump).read_text(encoding="utf-8-sig").splitlines():
        if line.startswith("JOB\t"):
            blocks.append([])
        if blocks and line:
            blocks[-1].append(line)
    candidates = [b for b in blocks if "DONE\t" + b[0].split("\t")[1] in b
                  and not b[0].startswith(("JOB\tLEFT:", "JOB\tBAKELEFT:"))
                  and all(sum(l.startswith(k + "\t") for l in b) >= 2 for k in ("PB2", "PB3"))]
    if not candidates:
        raise ValueError("no complete job with at least two imported pose bones at both frames")
    block = min(candidates, key=len)
    key = block[0].split("\t")[1]
    job = next(l for l in Path(jobs).read_text(encoding="utf-8-sig").splitlines() if l.split("|")[0] == key)
    out = Path(out); out.mkdir(parents=True, exist_ok=True)

    def write(name, lines):
        (out / (name + ".txt")).write_text("\n".join(lines) + "\n", encoding="utf-8")

    write("jobs", [job]); write("intact", block)
    for kind in ("PB2", "PB3"):
        indices = [i for i, l in enumerate(block) if l.startswith(kind + "\t")]
        first, second = indices[:2]
        lines = list(block); del lines[first]
        write(kind + "_missing", lines)
        write(kind + "_all_missing", [l for l in block if not l.startswith(kind + "\t")])
        lines = list(block); lines[second] = lines[first]
        write(kind + "_duplicate", lines)
        for mode, field in (("unknown_armature", 1), ("unknown_bone", 2)):
            lines = list(block); row = lines[first].split("\t"); row[field] = "__unknown_imported_pose__"
            lines[first] = "\t".join(row)
            write(kind + "_" + mode, lines)
        # Keep the row count, but replace every identity and value (the review's complete PB2 bypass).
        lines = list(block)
        for i in indices:
            row = lines[i].split("\t"); row[1:3] = ["__unknown_imported_pose__"] * 2; row[3:] = ["deadbeef"] * 10
            lines[i] = "\t".join(row)
        write(kind + "_all_unknown", lines)
        lines = list(block); lines[first] = lines[first].rsplit("\t", 1)[0]
        write(kind + "_short", lines)
        lines = list(block); lines[first] += "\t00000000"
        write(kind + "_long", lines)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
