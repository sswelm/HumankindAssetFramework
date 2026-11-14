"""Run the production script on the Two-sided fixture and hold the export pass to its contract.

The fixture's Tower is ONE mesh of four pieces, left to right along x (vehiclerig_fixtures.py two_sided_fixtures):
  a closed cube wound outward      -> every face left
  a closed cube wound inside-out   -> every face reversed (no new geometry)
  one free-standing sheet          -> doubled (a reversed copy, inset)
  an authored pair on one quad     -> kept as authored
The expectations below come from that input and the role's contract, not from the script's output."""
import bpy
import contextlib
import io
import os
import re
import sys
import traceback

script, jobs, work = sys.argv[sys.argv.index("--") + 1:]
source = open(script, encoding="utf-8").read()
expected = {"fx_two_sided"}
seen = set()
failed = False
for row in open(jobs, encoding="utf-8"):
    fields = row.rstrip("\r\n").split("\t")
    if fields[0] != "JOB":
        continue
    key, argv = fields[1], fields[2:]
    log = io.StringIO()
    try:
        assert key in expected and key not in seen, "unexpected or duplicate fixture: " + key
        seen.add(key)
        assert any(a.startswith("twosided=@") for a in argv), "the job carries no twosided= tag"
        os.makedirs(work, exist_ok=True)
        argv[2], argv[3] = os.path.join(work, key + ".glb"), ""
        sys.argv = [script, "--"] + argv
        g = {"__name__": "__main__", "__file__": script}
        with contextlib.redirect_stdout(log):
            exec(compile(source, script, "exec"), g)
        assert os.path.getsize(argv[2]) > 0, "no GLB export"
        text = log.getvalue()
        line = re.search(r"VEHICLE two-sided 'Mesh___two__Tower': (\d+) face\(s\) reversed .*?, (\d+) doubled .*?, (\d+) left; (\d+) authored twin\(s\) kept; (\d+) -> (\d+) verts", text)
        assert line, "no per-mesh two-sided line"
        rev, dbl, left, twins, v0, v1 = (int(x) for x in line.groups())
        assert (rev, dbl, left, twins) == (12, 2, 16, 4), "reversed/doubled/left/twins = %r, expected (12, 2, 16, 4)" % ((rev, dbl, left, twins),)
        assert v1 - v0 == 4, "the doubled sheet added %d vertices, expected 4" % (v1 - v0)
        o = bpy.data.objects["Mesh___two__Tower"]
        mw = o.matrix_world
        faces = []
        for p in o.data.polygons:
            pw = [mw @ o.data.vertices[i].co for i in p.vertices]
            c = sum(pw, pw[0] * 0) / len(pw)
            n = (pw[1] - pw[0]).cross(pw[2] - pw[0]).normalized()
            faces.append((c, n))
        assert len(faces) == 12 + 12 + 4 + 4, "the Tower holds %d faces, expected 32" % len(faces)
        faces.sort(key=lambda f: f[0].x)
        groups = [faces[0:12], faces[12:24], faces[24:28], faces[28:32]]
        for a, b in zip(groups, groups[1:]):
            assert b[0][0].x - a[-1][0].x > 0.5, "the four pieces are not apart along x"
        for name, grp in (("the outward cube", groups[0]), ("the inside-out cube", groups[1])):
            centre = sum((c for c, n in grp), grp[0][0] * 0) / len(grp)
            assert all(n.dot(c - centre) > 0 for c, n in grp), name + " does not face outward on every face"
        for name, grp, apart in (("the sheet", groups[2], True), ("the authored pair", groups[3], False)):
            total = sum((n for c, n in grp), grp[0][1] * 0)
            assert total.length < 1e-4, name + " is not two-sided (its normals do not cancel)"
            axis = grp[0][1]
            ups = [c.dot(axis) for c, n in grp if n.dot(axis) > 0]; downs = [c.dot(axis) for c, n in grp if n.dot(axis) < 0]
            assert len(ups) == 2 and len(downs) == 2, name + " is not two faces each way"
            gap = abs(sum(ups) / 2 - sum(downs) / 2)
            assert (gap > 1e-4) == apart, name + (" has coincident copies" if apart else " was moved (%.6f)" % gap)
        print("TWOSIDED PASS\t" + key, flush=True)
    except BaseException:
        failed = True
        print(log.getvalue())
        traceback.print_exc()
        print("TWOSIDED FAIL\t" + key, flush=True)
if seen != expected:
    failed = True
    print("TWOSIDED FAIL\tmissing fixtures: " + repr(expected - seen), flush=True)
if failed:
    # Blender does not propagate an ordinary Python exception to its process exit status.
    sys.stdout.flush(); sys.stderr.flush(); os._exit(1)
