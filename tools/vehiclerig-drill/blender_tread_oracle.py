"""Repeat the tread fixtures and independently check the exported recoil, including numerical negative controls.

Blender --python blender_tread_oracle.py -- <vehicle_rig.py> <two-fixture jobs> <work>
The tread is LEFT by the C# port, so parity alone cannot detect a regression in its recoil direction.
"""
import contextlib
import io
import math
from pathlib import Path
import runpy
import sys

import bpy
from mathutils import Vector


class DumpOutput(io.StringIO):
    def reconfigure(self, **kwargs):
        pass  # The dumper requests UTF-8 stdout; this buffer already holds Unicode strings.


def run_dump(script, jobs, work, visible=True):
    saved = sys.argv
    output = DumpOutput()
    try:
        sys.argv = [__file__, "--", str(script), str(jobs), str(work)]
        with contextlib.redirect_stdout(output):
            try:
                runpy.run_path(str(Path(__file__).with_name("blender_vehiclerig_dump.py")), run_name="__main__")
            except SystemExit as error:
                if error.code not in (None, 0):
                    raise RuntimeError("tread fixture dump failed: " + output.getvalue()[-2000:]) from error
    finally:
        sys.argv = saved
    text = output.getvalue()
    if visible:
        print(text, end="")
    return text


def measure_recoil(path):
    """Read the shipped artifact, not the sign variable or an unevaluated pose-bone quaternion."""
    if not path.is_file():
        raise ValueError("tracked recoil did not export a GLB")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(path))
    arms = [o for o in bpy.data.objects if o.type == "ARMATURE" and "Barrel" in o.pose.bones]
    if len(arms) != 1:
        raise ValueError("tracked recoil needs one exported Barrel skeleton")
    arm = arms[0]
    clips = [(s.action, s.action_slot) for t in arm.animation_data.nla_tracks for s in t.strips
             if s.action and s.action.name.split("|")[-1] == "Recoil"]
    if len(clips) != 1:
        raise ValueError("tracked recoil needs one exported Recoil clip")
    action, slot = clips[0]
    for obj in bpy.data.objects:
        if obj.animation_data:
            for track in list(obj.animation_data.nla_tracks):
                obj.animation_data.nla_tracks.remove(track)
            obj.animation_data.action = None
    arm.animation_data.action = action
    arm.animation_data.action_slot = slot
    # Fixed fixture contract: 16 frames, no lead, kick at frame 2. The bore points along +X and the requested slide is
    # 0.28 units. Neither the kick time nor its direction/magnitude is derived from the animation being checked.
    meshes = []
    for obj in bpy.data.objects:
        if obj.type != "MESH" or not any(m.type == "ARMATURE" and m.object == arm for m in obj.modifiers):
            continue
        group = obj.vertex_groups.get("Barrel")
        if group:
            indices = [v.index for v in obj.data.vertices if any(g.group == group.index and g.weight > .999 for g in v.groups)]
            if indices:
                meshes.append((obj, indices))
    if sum(len(indices) for _, indices in meshes) < 16:
        raise ValueError("tracked recoil needs the exported tube and brake skin")

    def sample(frame):
        bpy.context.scene.frame_set(frame)
        bpy.context.view_layer.update()
        points = []
        for obj, indices in meshes:
            evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
            mesh = evaluated.to_mesh()
            try:
                points.extend(evaluated.matrix_world @ mesh.vertices[i].co for i in indices)
            finally:
                evaluated.to_mesh_clear()
        centre = sum(points, Vector()) / len(points)
        head = arm.matrix_world @ arm.pose.bones["Barrel"].head
        if not all(math.isfinite(v) for p in (centre, head) for v in p):
            raise ValueError("nonfinite exported recoil pose")
        return centre.copy(), head.copy()

    rest, kick, end = sample(0), sample(2), sample(16)
    return [(k - r, e - r) for r, k, e in zip(rest, kick, end)]


def check_motion(measurements):
    for label, (kick, end) in zip(("tube skin", "Barrel joint"), measurements):
        if not (-.29 < kick.x < -.27 and end.length < 1e-4):
            raise AssertionError("%s must recoil 0.28 units along -X and return: kick=%s end=%s" % (label, tuple(kick), tuple(end)))


def main(script, jobs, work):
    # Preserve the caller's temp-path spelling (Windows may pass an 8.3 user directory). The repeated dump's logs
    # must differ only in work_<shard> versus work_fb2, just as they did before this independent check was added.
    script, jobs, work = Path(script).absolute(), Path(jobs).absolute(), Path(work)
    lines = jobs.read_text(encoding="utf-8-sig").splitlines()
    keys = [line.split("\t")[1] for line in lines if line.startswith("JOB\t")]
    if sorted(keys) != ["fx_r_tracks_fallback", "fx_r_tracks_recoil"]:
        raise ValueError("both tread oracle fixtures must be present exactly once")
    rows = run_dump(script, jobs, work)
    fallback = rows.split("JOB\tfx_r_tracks_fallback\n", 1)[-1].split("DONE\tfx_r_tracks_fallback", 1)[0]
    if "skinned (carrier blend fallback)" not in fallback:
        raise ValueError("tread fixture no longer exercises carrier-blend skinning")
    groups = [r.split("\t")[3] for r in fallback.splitlines() if r.startswith("VG\t3\t")
              and r.split("\t")[3].startswith("Track_00_R_")]
    expected = sorted("Track_00_R_" + n for n in ("Bot", "Top", "RampF", "RampR", "RampRT", "WrapF", "WrapR", "WrapGF", "WrapGR"))
    if groups != expected:
        raise ValueError("tread fallback must create its nine carrier groups in sorted order")
    exported = lambda folder: folder / "fx_r_tracks_recoil" / "fx_r_tracks_recoil_Spin.glb"
    measured = measure_recoil(exported(work))
    check_motion(measured)
    print("PASS — exported tracked recoil: tube skin and Barrel joint move 0.28 units backward and return")

    # A broken or missing export is an ERROR, never evidence that a numerical negative control was rejected.
    # These two complete exports must be structurally measurable before the movement assertion may reject them.
    source = script.read_text(encoding="utf-8")
    controls = {
        "old track centre": [("_test = (_gun_c - _pivot).copy()", "_test = (_tc - _pivot).copy()"),
                             ("(_pivot + _test - _gun_c).dot(_bore_d)", "(_pivot + _test - _tc).dot(_bore_d)")],
        "no kick": [("_kick_q = Quaternion(_local_axis, _theta * _rsign)", "_kick_q = _rest_q.copy()")],
    }
    control_root = work / "controls"
    control_root.mkdir(exist_ok=True)
    control_jobs = control_root / "jobs.txt"
    control_jobs.write_text(next(line for line in lines if line.startswith("JOB\tfx_r_tracks_recoil\t")) + "\n", encoding="utf-8")
    for index, (label, replacements) in enumerate(controls.items()):
        changed = source
        for old, new in replacements:
            if changed.count(old) != 1:
                raise ValueError("cannot construct tread control: " + label)
            changed = changed.replace(old, new)
        altered = control_root / ("vehicle_%d.py" % index)
        altered.write_text(changed, encoding="utf-8")
        folder = control_root / ("run_%d" % index)
        run_dump(altered, control_jobs, folder, visible=False)
        bad = measure_recoil(exported(folder))
        try:
            check_motion(bad)
        except AssertionError:
            print("PASS — tracked recoil control rejected: " + label)
        else:
            raise AssertionError("tracked recoil control passed: " + label)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    try:
        main(*sys.argv[sys.argv.index("--") + 1:])
    except Exception as error:
        print("FAIL — tread oracle: %s: %s" % (type(error).__name__, error), flush=True)
        sys.exit(1)
