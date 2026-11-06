"""The direct FBX import against its glTF round trip, per .fbx rig job (tools/riganim_drill.sh, step 6g).

usage: route_rows.py [--left <key>]... [--inproc <wrapper report>] <direct dump> <round-trip dump>...

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
INPROC lines, preserving colons in job keys) may differ: DIFFERS is the expected measurement, EQUAL is a NOTE (the leave could be lifted for that file).
A job the C# route TAKES must be EQUAL: a DIFFERS is a FAIL (the port would equal the round trip and not the shipped
route), exit 1. A job of the direct dump missing from the round-trip dumps, or a job without its DONE row, is an error
(exit 2). Equality requires four completed stages with scene rows and a successful GLB export; missing or stopped
output cannot establish that the fallback could be lifted. The shell also validates direct dumps with RigAnimDump's
full shape/consistency checks and the expected direct jobs before using this comparator.
"""
import re
import sys
from collections import Counter, defaultdict

EXPORT = {"X", "XLOG", "XDIES"}
EXPORT_KINDS = {"GLB", "SCENE", "NODE", "SKIN", "IBM", "MESH", "PRIM", "MAT", "IMG", "TEX", "SAMP", "ANIM", "CHAN"}
STAGE_KINDS = {"SCENE", "OBJ", "MESH", "VG", "BONE", "PB", "ACT", "FC", "K", "IMG"}
LOGS = {"LOG", "XLOG"}
CUT = 160


class DumpError(ValueError):
    pass


class Job:
    def __init__(self):
        self.rows = defaultdict(Counter)
        self.stages = []
        self.scenes = set()
        self.stopped = False
        self.export_started = False
        self.export_died = False

    def require_baked_output(self, key):
        exports = self.rows["X"]
        if (self.stages != [1, 2, 3, 4] or self.scenes != {1, 2, 3, 4} or self.stopped or self.export_died
                or sum(n for row, n in exports.items() if row.startswith("X\tGLB\t")) != 1
                or not any(row.startswith("X\tSCENE\t") for row in exports)):
            raise DumpError(key + ": no complete stage-4 scene and successful GLB export; equality cannot establish a safe route")


def read_dump(path):
    """Read framed jobs; incomplete but explicitly stopped unrelated jobs may coexist in the round-trip shards."""
    jobs, key, job = {}, None, None
    with open(path, encoding="utf-8") as handle:
        for raw in handle:
            line = raw.rstrip("\r\n")
            if not line:
                continue
            f = line.split("\t"); kind = f[0]
            if kind == "JOB":
                if key is not None: raise DumpError("job(s) without a DONE row: " + key)
                if len(f) != 2 or not f[1] or f[1] in jobs: raise DumpError("invalid or duplicate JOB row")
                key, job = f[1], Job(); jobs[key] = job
                continue
            if key is None: raise DumpError("a row outside JOB..DONE: " + kind)
            stage = len(job.stages)
            if kind == "DONE":
                if len(f) != 2 or f[1] != key: raise DumpError("DONE does not match its JOB")
                if job.scenes != set(job.stages): raise DumpError(key + ": a completed stage has no unique SCENE row")
                if stage < 4 and not job.stopped: raise DumpError(key + ": unfinished stages without EXIT or DIES")
                key, job = None, None
            elif kind == "STAGE":
                if (len(f) != 2 or int(f[1]) != stage + 1 or stage >= 4 or job.stopped or job.export_started
                        or job.scenes != set(job.stages)):
                    raise DumpError(key + ": invalid or out-of-order STAGE")
                job.stages.append(int(f[1]))
            elif kind in EXPORT:
                if stage != 4 or 4 not in job.scenes or job.stopped: raise DumpError(key + ": export before completed stage 4")
                job.export_started = True
                if kind == "XDIES":
                    if len(f) != 2 or not f[1] or job.export_died or any(r.startswith("X\t") for r in job.rows["X"]):
                        raise DumpError(key + ": invalid or contradictory XDIES")
                    job.export_died = True
                elif job.export_died:
                    raise DumpError(key + ": export row after XDIES")
                elif kind == "X":
                    if len(f) < 3 or f[1] not in EXPORT_KINDS: raise DumpError(key + ": unknown or empty export row")
                    if f[1] == "GLB" and (len(f) != 6 or any(int(v) < 0 for v in f[2:5]) or not re.fullmatch(r"[0-9a-f]{64}", f[5])):
                        raise DumpError(key + ": invalid GLB evidence")
                elif len(f) != 2 or not f[1].startswith("RIGANIM"):
                    raise DumpError(key + ": invalid XLOG")
                job.rows["X"][line] += 1
            elif kind in ("EXIT", "DIES"):
                if len(f) != 3 or int(f[1]) != stage + 1 or stage >= 4 or job.stopped or job.export_started:
                    raise DumpError(key + ": invalid EXIT or DIES")
                job.stopped = True; job.rows[int(f[1])][line] += 1
            elif kind == "LOG":
                if len(f) != 3 or int(f[1]) not in (stage, stage + 1) or not 1 <= int(f[1]) <= 4 or job.stopped or job.export_started:
                    raise DumpError(key + ": misplaced LOG")
                job.rows[int(f[1])][line] += 1
            elif kind in STAGE_KINDS:
                if len(f) < 2 or int(f[1]) != stage or stage == 0 or job.stopped or job.export_started:
                    raise DumpError(key + ": misplaced stage row: " + kind)
                if kind == "SCENE":
                    if len(f) != 6 or stage in job.scenes: raise DumpError(key + ": invalid or repeated SCENE")
                    job.scenes.add(stage)
                if kind != "K": job.rows[stage][line] += 1
            else:
                raise DumpError(key + ": unknown or failed row: " + kind)
    if key is not None: raise DumpError("job(s) without a DONE row: " + key)
    return jobs


def inprocess_left(path):
    marker = ": left to the FBX route by name, as the wrapper says: "
    left = set()
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("INPROC ") and marker in line:
                key, _, reason = line[7:].rstrip("\r\n").partition(marker)
                if not key or not reason: raise DumpError("malformed in-process fallback record")
                left.add(key)
    return left


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
        if argv[i] in ("--left", "--inproc"):
            if i + 1 >= len(argv): out("ROUTE\tERROR\tmissing value for " + argv[i]); return 2
            if argv[i] == "--left": left.add(argv[i + 1])
            else:
                try: left.update(inprocess_left(argv[i + 1]))
                except (OSError, ValueError) as error: out("ROUTE\tERROR\t" + str(error)); return 2
            i += 2
        elif argv[i].startswith("--"):
            out("ROUTE\tERROR\tunknown option " + argv[i]); return 2
        else:
            paths.append(argv[i]); i += 1
    if len(paths) < 2:
        out(__doc__); return 2
    try:
        direct = read_dump(paths[0]); rt = {}
        for p in paths[1:]:
            jobs = read_dump(p)
            if rt.keys() & jobs.keys(): raise DumpError("duplicate job across round-trip shards")
            rt.update(jobs)
        if left - direct.keys(): raise DumpError("fallback names a job absent from the direct dump")
        for key in direct:
            if key not in rt: raise DumpError(key + ": not in the round-trip dump(s)")
            direct[key].require_baked_output(key); rt[key].require_baked_output(key)
    except (OSError, ValueError) as error:
        out("ROUTE\tERROR\t" + str(error)); return 2
    if not direct:
        out("ROUTE\tERROR\tthe direct dump holds no job"); return 2
    rc = 0; n = Counter()
    for key in direct:
        if key not in rt:
            out(f"ROUTE\tERROR\t{key}: not in the round-trip dump(s)"); return 2
        lines = []
        verdict, early, logs = compare(direct[key].rows, rt[key].rows, key, lines)
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
