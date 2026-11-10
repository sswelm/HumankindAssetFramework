"""Run the production rigid-link path, including its export, on real pitched-belt fixtures."""
import bpy
import contextlib
import io
import os
import sys
import traceback

script, jobs, work = sys.argv[sys.argv.index("--") + 1:]
source = open(script, encoding="utf-8").read()
expected = {"fx_links", "fx_links_dense", "fx_links_tight", "fx_links_full", "fx_links_fallback"}
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
        os.makedirs(work, exist_ok=True)
        argv[2], argv[3] = os.path.join(work, key + ".glb"), ""
        sys.argv = [script, "--"] + argv
        g = {"__name__": "__main__", "__file__": script}
        error = None
        with contextlib.redirect_stdout(log):
            try:
                exec(compile(source, script, "exec"), g)
            except (Exception, SystemExit) as exc:
                error = exc
        if key in {"fx_links_full", "fx_links_fallback"}:
            assert error is not None and "rigid links" in str(error) and "bone" in str(error), "exhausted rig was not refused: %r" % error
            if key == "fx_links_full":
                assert "need at least 4" in str(error), "missing allocation guard"
            else:
                assert "exceed the 126-bone budget" in str(error), "missing finished-armature guard"
            assert not os.path.exists(argv[2]), "exhausted rig was exported"
        else:
            if error is not None:
                raise error
            arm = g["arm"]
            assert len(arm.data.bones) <= 126, "bone wall exceeded: %d" % len(arm.data.bones)
            assert len(g["_link_jobs"]) == 2, "fixture missed the rigid-link path"
            assert os.path.getsize(argv[2]) > 0, "no GLB export"
            # A rigid cell has one full-weight bone per vertex; independently evaluate its
            # skinned mesh throughout the clip and hold every edge length and loop endpoint.
            link_names = {"%sL%02d" % (j["prefix"], c) for j in g["_link_jobs"].values() for c in j["cells"]}
            meshes = [o for o in bpy.context.scene.objects if o.type == "MESH" and any(v.name in link_names for v in o.vertex_groups)]
            assert len(meshes) == 2, "expected two link-weighted meshes"
            for o in meshes:
                owners = []
                for v in o.data.vertices:
                    weights = [w for w in v.groups if w.weight > 1e-6]
                    assert len(weights) == 1 and abs(weights[0].weight - 1) < 1e-6, "blended or unweighted link vertex"
                    name = o.vertex_groups[weights[0].group].name
                    assert name in link_names, "vertex not attached to a link"
                    owners.append(name)
                assert all(owners[e.vertices[0]] == owners[e.vertices[1]] for e in o.data.edges), "edge crosses a link hinge"
            baseline = {}
            moved = set()
            for frame in range(g["_clip_frames"] + 1):
                bpy.context.scene.frame_set(frame)
                deps = bpy.context.evaluated_depsgraph_get()
                for o in meshes:
                    ev = o.evaluated_get(deps); mesh = ev.to_mesh()
                    try:
                        positions = [v.co.copy() for v in mesh.vertices]
                        lengths = [(positions[e.vertices[0]] - positions[e.vertices[1]]).length for e in mesh.edges]
                        if frame == 0:
                            baseline[o.name] = (positions, lengths)
                        else:
                            rest, edges = baseline[o.name]
                            assert max((abs(a - b) for a, b in zip(lengths, edges)), default=0) < 2e-5, "link deformed at frame %d" % frame
                            if max((a - b).length for a, b in zip(positions, rest)) > .1:
                                moved.add(o.name)
                            if frame == g["_clip_frames"]:
                                assert max((a - b).length for a, b in zip(positions, rest)) < 2e-5, "loop did not close"
                    finally:
                        ev.to_mesh_clear()
            assert moved == {o.name for o in meshes}, "tread did not move"
            print("RIGID BONES\t%s\t%d" % (key, len(arm.data.bones)), flush=True)
        print("RIGID PASS\t" + key, flush=True)
    except BaseException:
        failed = True
        print(log.getvalue())
        traceback.print_exc()
        print("RIGID FAIL\t" + key, flush=True)
if seen != expected:
    failed = True
    print("RIGID FAIL\tmissing fixtures: " + repr(expected - seen), flush=True)
if failed:
    # Blender does not propagate an ordinary Python exception to its process exit status.
    sys.stdout.flush(); sys.stderr.flush(); os._exit(1)
