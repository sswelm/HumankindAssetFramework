"""The direct FBX import against its glTF round trip, per .fbx rig job (tools/riganim_drill.sh, step 6g).

usage: route_rows.py [--left <key>]... <direct dump> <round-trip dump>...

The Factory's FBX route runs rig_anim.py on the .fbx itself; the drill's Blender side (step 6f) and the C# reader run on
Blender's own GLB export of that .fbx. A drill PASS on an .fbx job therefore holds the port equal to the round trip, not
to the shipped route (the light assault mech, 2026-11-03: 9 against 8 material slots, the placement 0.04 against 1.63).
This script measures that gap from two stage dumps of blender_riganim_dump.py of the same jobs - one on the .fbx, one on
the GLB - and prints, per job of the direct dump:

    ROUTE <key> <stage> <kind> direct <n> roundtrip <n> differ <n>     a row kind whose rows differ at that stage (1-4, X = the export)
    ROUTE <key> DIFF <stage> <kind> <D:|R:> <the row, cut at 160 characters>     the first rows on each side only the other does not have
    ROUTE <key> EQUAL|DIFFERS|FAIL|NOTE <...>                        the verdict, and the summary:
    ROUTES jobs <n> equal <n> differs <n> fail <n> note <n>

Rows are compared as strings, as the drill compares them (float32 bits, hashes), as multisets per stage and kind. LOG
rows (the script's own RIGANIM lines) are counted but never part of the verdict; the verdict is stage 4 and the export,
what reaches the bake. A job the C# route LEAVES to the FBX route by name (--left <key>, the drill reads them off the
INPROC lines) may differ: DIFFERS is the expected measurement, EQUAL is a NOTE (the leave could be lifted for that file).
A job the C# route TAKES must be EQUAL: a DIFFERS is a FAIL (the port would equal the round trip and not the shipped
route), exit 1. A job of the direct dump missing from the round-trip dumps, or a job without its DONE row, is an error
(exit 2).
"""
import sys
from collections import Counter, defaultdict

IGNORED = {"JOB", "DONE", "STAGE", "K"}          # structure, or the optional per-key rows the drill's own checks hold
EXPORT = {"X", "XLOG", "XDIES"}
LOGS = {"LOG", "XLOG"}
CUT = 160


def read_dump(path):
    """{key: {stage: Counter(row)}}; stage is 1..4 or 'X' (the export rows); the keys without a DONE row beside it."""
    jobs, key, stage, done = {}, None, None, set()
    for line in open(path, encoding="utf-8").read().split("\n"):
        if not line or "\t" not in line:
            continue
        f = line.split("\t")
        kind = f[0]
        if kind == "JOB":
            key, stage = f[1], None
            jobs.setdefault(key, defaultdict(Counter))
        elif kind == "DONE":
            done.add(f[1])
        elif kind == "STAGE":
            stage = int(f[1])
        elif key is None or kind in IGNORED:
            continue
        elif kind in EXPORT:
            jobs[key]["X"][line] += 1
        else:
            jobs[key][int(f[1])][line] += 1   # <kind> <stage> ...
    return jobs, [k for k in jobs if k not in done]


def compare(direct, roundtrip, key, out):
    """Appends the ROUTE lines of one job to out; returns (rows differing at stage 4 and the export, at stages 1-3, LOG rows)."""
    verdict = early = logs = 0
    for stage in (1, 2, 3, 4, "X"):
        d, r = direct.get(stage, Counter()), roundtrip.get(stage, Counter())
        by_kind = defaultdict(lambda: [Counter(), Counter()])
        for row, n in d.items():
            by_kind[row.split("\t", 1)[0]][0][row] += n
        for row, n in r.items():
            by_kind[row.split("\t", 1)[0]][1][row] += n
        for kind in sorted(by_kind):
            dk, rk = by_kind[kind]
            only_d, only_r = dk - rk, rk - dk
            differ = sum(only_d.values()) + sum(only_r.values())
            if differ == 0:
                continue
            out.append(f"ROUTE\t{key}\t{stage}\t{kind}\tdirect {sum(dk.values())} roundtrip {sum(rk.values())} differ {differ}")
            for side, rows in (("D:", only_d), ("R:", only_r)):
                for row in sorted(rows)[:2]:
                    out.append(f"ROUTE\t{key}\tDIFF\t{stage}\t{kind}\t{side} {row[:CUT]}")
            if kind in LOGS:
                logs += differ
            elif stage in (4, "X"):
                verdict += differ
            else:
                early += differ
    return verdict, early, logs


def main(argv, out=print):
    left, paths = set(), []
    i = 0
    while i < len(argv):
        if argv[i] == "--left" and i + 1 < len(argv):
            left.add(argv[i + 1]); i += 2
        else:
            paths.append(argv[i]); i += 1
    if len(paths) < 2:
        out(__doc__); return 2
    direct, missing = read_dump(paths[0])
    rt = {}
    for p in paths[1:]:
        jobs, m = read_dump(p); rt.update(jobs); missing += m
    if missing:
        out("ROUTE\tERROR\tjob(s) without a DONE row: " + " ".join(sorted(set(missing)))); return 2
    if not direct:
        out("ROUTE\tERROR\tthe direct dump holds no job"); return 2
    rc = 0; n = Counter()
    for key in direct:
        if key not in rt:
            out(f"ROUTE\tERROR\t{key}: not in the round-trip dump(s)"); return 2
        lines = []
        verdict, early, logs = compare(direct[key], rt[key], key, lines)
        kinds = sorted({l.split("\t")[3] for l in lines if l.split("\t")[2] in ("4", "X") and l.split("\t")[3] not in LOGS})
        for l in lines:
            out(l)
        measure = f"stage 4 and export {verdict} row(s) differ ({', '.join(kinds) or '-'}); stages 1-3 {early}; LOG {logs}"
        if verdict == 0 and key in left:
            word, tail = "NOTE", "equal to its round trip, yet left to the FBX route by name - the leave could be lifted for this file; "
        elif verdict == 0:
            word, tail = "EQUAL", ""
        elif key in left:
            word, tail = "DIFFERS", "left to the FBX route by name, as it must be; "
        else:
            word, tail = "FAIL", "the C# route takes this job, but the round trip it is held to is not the direct import rig_anim.py runs on; "
            rc = 1
        n[word] += 1
        out(f"ROUTE\t{key}\t{word}\t{tail}{measure}")
    out(f"ROUTES\tjobs {sum(n.values())} equal {n['EQUAL']} differs {n['DIFFERS']} fail {n['FAIL']} note {n['NOTE']}")
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
