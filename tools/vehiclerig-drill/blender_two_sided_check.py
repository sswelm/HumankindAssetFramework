"""Run the production script on the Two-sided fixture and hold the export pass to its contract.

The fixture's Tower is ONE mesh of five pieces, left to right along x (vehiclerig_fixtures.py two_sided_fixtures):
  a closed cube wound outward      -> every face left (its backs see only the cube's own inside)
  a closed cube wound inside-out   -> every face doubled (its backs face the open air)
  one free-standing sheet          -> doubled
  an authored pair on one quad     -> kept as authored
  a sheet under a cover in a well  -> doubled, and ONLY by the open-pocket test: no straight line from its back leaves the well
A copy lies on its original's own positions, wound the other way; nothing is reversed in place.
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
expected = {"fx_two_sided", "fx_two_sided_small", "fx_two_sided_thin"}
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
        if key != "fx_two_sided":
            line = re.search(r"VEHICLE two-sided 'Mesh___two__Closed': (\d+) face\(s\) doubled \(the back shows\), (\d+) left; (\d+) authored twin\(s\) kept; (\d+) -> (\d+) verts", text)
            assert line, "no closed-solid two-sided line"
            assert tuple(int(x) for x in line.groups()) == (0, 12, 0, 8, 8), "a small closed solid was changed: " + line.group(0)
            assert len(bpy.data.objects["Mesh___two__Closed"].data.polygons) == 12, "closed solid acquired back faces"
            if key == "fx_two_sided_small":
                # Even a direct/headless rigfast call must refuse the repair it cannot perform.
                fast = list(argv); fast[0] = "rigfast"
                sys.argv = [script, "--"] + fast
                fast_log = io.StringIO()
                with contextlib.redirect_stdout(fast_log):
                    try:
                        exec(compile(source, script, "exec"), {"__name__": "__main__", "__file__": script})
                    except SystemExit as e:
                        assert e.code == 1, "rigfast did not refuse the Two-sided role"
                    else:
                        raise AssertionError("rigfast silently accepted the Two-sided role")
                assert "VEHICLE ERROR: Two-sided needs mesh parts" in fast_log.getvalue(), fast_log.getvalue()
            print("TWOSIDED PASS\t" + key, flush=True)
            continue
        line = re.search(r"VEHICLE two-sided 'Mesh___two__Tower': (\d+) face\(s\) doubled \(the back shows\), (\d+) left; (\d+) authored twin\(s\) kept; (\d+) -> (\d+) verts", text)
        assert line, "no per-mesh two-sided line"
        dbl, left, twins, v0, v1 = (int(x) for x in line.groups())
        assert (dbl, left, twins) == (16, 16, 4), "doubled/left/twins = %r, expected (16, 16, 4)" % ((dbl, left, twins),)
        assert v1 - v0 == 16, "the copies added %d vertices, expected 16 (a cube's 8 and two quads' 4 each)" % (v1 - v0)
        o = bpy.data.objects["Mesh___two__Tower"]
        mw = o.matrix_world
        faces = []
        for p in o.data.polygons:
            pw = [mw @ o.data.vertices[i].co for i in p.vertices]
            c = sum(pw, pw[0] * 0) / len(pw)
            n = (pw[1] - pw[0]).cross(pw[2] - pw[0]).normalized()
            faces.append((c, n))
        faces.sort(key=lambda f: f[0].x)
        groups = [[faces[0]]]
        for prev, f in zip(faces, faces[1:]):
            if f[0].x - prev[0].x > 0.5: groups.append([])
            groups[-1].append(f)
        assert [len(g) for g in groups] == [12, 24, 4, 4, 4], "the pieces hold %r faces, expected [12, 24, 4, 4, 4]" % [len(g) for g in groups]
        centre = sum((c for c, n in groups[0]), groups[0][0][0] * 0) / 12
        assert all(n.dot(c - centre) > 0 for c, n in groups[0]), "the outward cube does not face outward on every face"
        centre = sum((c for c, n in groups[1]), groups[1][0][0] * 0) / 24
        outward = [f for f in groups[1] if f[1].dot(f[0] - centre) > 0]; inward = [f for f in groups[1] if f[1].dot(f[0] - centre) < 0]
        assert len(outward) == 12 and len(inward) == 12, "the inside-out cube is not 12 faces each way (%d out, %d in)" % (len(outward), len(inward))
        for name, grp, each in (("the inside-out cube", groups[1], 12), ("the sheet", groups[2], 2), ("the authored pair", groups[3], 2), ("the sheet in the well", groups[4], 2)):
            # every face has exactly one partner at its own centre, wound the other way
            for c, n in grp:
                partners = [1 for c2, n2 in grp if (c2 - c).length < 1e-5 and n2.dot(n) < -0.999]
                assert len(partners) == 1, name + ": a face has %d opposite partner(s) on its own position, expected 1" % len(partners)
            assert len(grp) == 2 * each, name + " holds %d faces" % len(grp)
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
