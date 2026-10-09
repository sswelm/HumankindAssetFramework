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
