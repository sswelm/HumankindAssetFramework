"""Mutate a real posed_bones dump: complete controls pass, invalid rest/pose evidence fails.

usage: missing_bones.py <output directory> <dump>...
"""
from pathlib import Path
import sys


def main(out, dumps):
    blocks = []
    for dump in dumps:
        cur = None
        for line in Path(dump).read_text(encoding="utf-8-sig").split("\n"):
            line = line.rstrip("\r")
            if line.startswith("FILE\t"):
                cur = [line]; blocks.append(cur)
            elif cur is not None and line:
                cur.append(line)
    job = next((b for b in blocks if b[0].endswith("/posed_bones.glb") and any(l.startswith("DONE\t") for l in b)), None)
    if job is None:
        raise ValueError("no complete posed_bones dump for the bone-record regressions")
    bone = [i for i, l in enumerate(job) if l.startswith("BONE\t")]
    pose = [i for i, l in enumerate(job) if l.startswith("PB\t")]
    if len(bone) < 2 or not pose:
        raise ValueError("posed_bones must hold at least two rest bones and a pose")
    out = Path(out); out.mkdir(parents=True, exist_ok=True)
    def write(name, lines):
        (out / (name + ".txt")).write_text("\n".join(lines) + "\n", encoding="utf-8")
    write("intact", job)
    for kind, index in (("bone", bone[0]), ("pose", pose[0])):
        lines = list(job); del lines[index]
        write("missing_" + kind, lines)
        for shape in ("short", "long"):
            lines = list(job)
            lines[index] = job[index].rsplit("\t", 1)[0] if shape == "short" else job[index] + "\t00000000"
            write(shape + "_" + kind, lines)
    lines = list(job); lines[bone[1]] = job[bone[0]]
    write("duplicate_bone", lines)   # same count, one bone missing: count alone cannot tell
    lines = list(job); lines.insert(pose[0], job[pose[0]])
    write("duplicate_pose", lines)
    lines = list(job); t = lines[pose[0]].split("\t"); t[3] = "unknown bone"; lines[pose[0]] = "\t".join(t)
    write("unknown_pose", lines)
    write("all_poses", [l for l in job if not l.startswith("PB\t")])


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2:])
