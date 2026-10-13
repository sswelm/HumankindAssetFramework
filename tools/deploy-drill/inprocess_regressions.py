"""Damage the successful Mono conversion control's inputs; keep its real Blender oracle for the fallback case."""
from pathlib import Path
import sys

root, jobs, oracle = map(Path, sys.argv[1:])
line = jobs.read_text(encoding="utf-8").strip()
t = line.split("|")
key = t[0].replace(":", "_") + ".glb"
original = (oracle / key).read_bytes()
for mode in ("empty", "missing", "changed", "fallback"):
    directory = root / mode
    (directory / "oracle").mkdir(parents=True, exist_ok=True)
    fields = list(t)
    if mode == "fallback":
        invalid = directory / "invalid.glb"
        invalid.write_bytes(b"invalid glb")
        fields[1] = invalid.as_posix()
    (directory / "jobs.txt").write_text("" if mode == "empty" else "|".join(fields) + "\n", encoding="utf-8")
    if mode in ("changed", "fallback"):
        damaged = bytearray(original)
        if mode == "changed":
            damaged[-1] ^= 1
        (directory / "oracle" / key).write_bytes(damaged)
