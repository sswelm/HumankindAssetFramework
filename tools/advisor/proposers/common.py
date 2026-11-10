"""Shared by the advisor's proposers: the one proposals file beside a GLB, which several proposers write their own letters
into. `<file>.glb.marks.proposed.txt` is in the marks sidecar's v2 layout (`<letter>|<node index>|<name>`); each proposer
owns its letters (split.py the S, delete.py the X) and a write replaces only those lines, so running the second proposer
never drops the first one's proposals. Conflicting letters for one node are refused for manual resolution. The live
`<file>.glb.marks.txt` is never touched: the user renames or merges the
proposal when they agree with it."""
import io
import os
import tempfile

from corpus import int_token

HEADER = "#fuse-groups v2"


def merge_proposals(path, letters, lines, tool):
    """Rewrite `path` with every line of another letter kept (and every `#name|` line, a group name added by hand), these
    `letters`' lines replaced by `lines` (full `L|index|name` lines). An existing file is read first; a missing one is
    created. Returns the lines written. A file that is not UTF-8 (hand-edited and saved in another code page) is refused
    with a ValueError and left as it is: a name read with replacement characters would match no part. Conflicting
    letters for one node are refused before writing, and replacement is atomic so a failed write preserves the file."""
    kept = []
    if os.path.exists(path):
        try:
            with io.open(path, encoding="utf-8-sig") as f:
                text = f.read()
        except UnicodeDecodeError as e:
            raise ValueError("%s is not UTF-8 (byte %d): save it as UTF-8 or delete it; nothing written" % (path, e.start))
        for line in text.splitlines():
            if not line:
                continue
            if line.startswith("#"):
                if line.startswith("#name|"):
                    kept.append(line)
                continue
            letter = line.split("|", 1)[0].strip()
            if letter not in letters:
                kept.append(line)
    out = kept + list(lines)
    by_node = {}
    for line in out:
        if line.startswith("#"):
            continue
        fields = line.split("|", 2)
        node = int_token(fields[1]) if len(fields) == 3 else None
        if node is None or node < 0:
            continue
        letter = fields[0].strip()
        if node in by_node and by_node[node] != letter:
            raise ValueError("%s: conflicting proposals %s and %s for node %d (%r); resolve that node by hand; nothing written"
                             % (path, by_node[node], letter, node, fields[2]))
        by_node[node] = letter
    fd, pending = tempfile.mkstemp(prefix=".advisor-proposals-", suffix=".tmp", dir=os.path.dirname(os.path.abspath(path)))
    try:
        with io.open(fd, "w", encoding="utf-8", newline="\n") as f:
            f.write(HEADER + "\n#proposed by tools/advisor/proposers (split.py the S lines, delete.py the X; last written by %s); rename to .marks.txt or merge by hand\n" % tool)
            for line in out:
                f.write(line + "\n")
        os.replace(pending, path)
    finally:
        if os.path.exists(pending):
            os.unlink(pending)
    return out
