"""Plant missing/truncated records in the posed fixture's real Blender dump for the deploy gate.

usage: missing_rows.py <output directory> <dump>...
"""
from pathlib import Path
import sys


def mutate(output_dir, dumps):
    fixture = None
    for dump in dumps:
        block = []
        for line in Path(dump).read_text(encoding="utf-8-sig").split("\n"):
            line = line.rstrip("\r")
            if line.startswith("FILE\t"):
                block = [line]
            elif block:
                block.append(line)
                if line.startswith("DONE\t"):
                    if block[0].split("\t")[1].replace("\\", "/").endswith("/posed.glb"):
                        fixture = block
                        break
                    block = []
        if fixture is not None:
            break
    if fixture is None:
        raise ValueError("no complete posed.glb dump for the missing-record regression")

    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)
    # Turn is a compared animated mesh, so neither missing row can be explained by an intentional skip.
    matrix = next(i for i, l in enumerate(fixture) if l.startswith("M\t") and l.split("\t")[2] == "Turn")
    prop = next(i for i, l in enumerate(fixture) if l.startswith("L\t") and l.split("\t")[2] == "Turn")
    for mode in ("matrix", "property", "all_properties", "frames", "short_matrix", "short_property"):
        lines = list(fixture)
        if mode == "matrix":
            del lines[matrix]
        elif mode == "property":
            del lines[prop]
        elif mode == "all_properties":
            lines = [l for l in lines if not l.startswith("L\t")]
        elif mode == "frames":
            lines = [l for l in lines if not l.startswith("FRAMES\t")]
        else:
            index = matrix if mode == "short_matrix" else prop
            lines[index] = lines[index].rsplit("\t", 1)[0]
        (out / (mode + ".txt")).write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    mutate(sys.argv[1], sys.argv[2:])
