"""Shared by the advisor's proposers: the one proposals file beside a GLB, which several proposers write their own letters
into. `<file>.glb.marks.proposed.txt` is in the marks sidecar's v2 layout (`<letter>|<node index>|<name>`); each proposer
owns its letters (split.py the S, delete.py the X) and a write replaces only those lines, so running the second proposer
never drops the first one's proposals. The live `<file>.glb.marks.txt` is never touched: the user renames or merges the
proposal when they agree with it."""
import io
import os

HEADER = "#fuse-groups v2"


def merge_proposals(path, letters, lines, tool):
    """Rewrite `path` with every line of another letter kept, these `letters`' lines replaced by `lines` (full
    `L|index|name` lines). An existing file is read first; a missing one is created. Returns the lines written."""
    kept = []
    if os.path.exists(path):
        with io.open(path, encoding="utf-8-sig") as f:
            for raw in f:
                line = raw.rstrip("\r\n")
                if not line or line.startswith("#"):
                    continue
                letter = line.split("|", 1)[0].strip()
                if letter not in letters:
                    kept.append(line)
    out = kept + list(lines)
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER + "\n#proposed by tools/advisor/proposers (%s); rename to .marks.txt or merge by hand\n" % tool)
        for line in out:
            f.write(line + "\n")
    return out
