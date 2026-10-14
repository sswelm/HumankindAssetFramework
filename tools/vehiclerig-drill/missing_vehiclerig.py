"""Damaged copies of a vehicle_rig stage dump (tools/vehiclerig_drill.sh): each must be REFUSED by vehiclerig.exe, so that a
missing or inconsistent record can never pass as evidence. Also writes intact.txt, the control, which must pass, and
jobs.txt, the jobs file naming just the job the copies hold (the first block that ran all five stages with bones, vertex
groups, fcurves and GLB rows: every row kind present).

usage: missing_vehiclerig.py <output directory> <jobs file> <dump>...
"""
import os, sys

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
jobs = [l.rstrip("\r") for l in open(sys.argv[2], encoding="utf-8").read().split("\n") if l.startswith("JOB\t") or l.startswith("FAST\t")]
lines = None
for d in sys.argv[3:]:
    rows = [l.rstrip("\r") for l in open(d, encoding="utf-8").read().split("\n") if l.strip()]
    starts = [i for i, l in enumerate(rows) if l.startswith("JOB\t")] + [len(rows)]
    for a, b in zip(starts, starts[1:]):
        block = rows[a:b]
        if "STAGE\t5" in block and any(l.startswith("VG\t") for l in block) and any(l.startswith("FC\t") for l in block) and any(l.startswith("X\tGLB\t") for l in block):
            lines = block; break
    if lines is not None:
        break
if lines is None:
    raise RuntimeError("no job ran through all five stages with groups, fcurves and a GLB: nothing to damage")
key = lines[0].split("\t")[1]
mine = [j for j in jobs if j.split("\t")[1] == key]
open(os.path.join(out, "jobs.txt"), "w", encoding="utf-8", newline="\n").write("\n".join(mine) + "\n")
# the same job under an "EXIT:" key (jobs_exit.txt): a dump of it running through all five stages must be refused
open(os.path.join(out, "jobs_exit.txt"), "w", encoding="utf-8", newline="\n").write("\n".join(j.replace("JOB\t" + key + "\t", "JOB\tEXIT:1:" + key + "\t", 1) for j in mine) + "\n")
# the same job as a FAST line (jobs_fast.txt): its mode is rig, so the jobs file itself is refused
open(os.path.join(out, "jobs_fast.txt"), "w", encoding="utf-8", newline="\n").write("\n".join(j.replace("JOB\t", "FAST\t", 1) for j in mine) + "\n")


def first(pred):
    for i, l in enumerate(lines):
        if pred(l):
            return i
    raise RuntimeError("the dump has no row for this tamper")


def write(name, rows):
    open(os.path.join(out, name + ".txt"), "w", encoding="utf-8", newline="\n").write("\n".join(rows) + "\n")


def drop(i): return lines[:i] + lines[i + 1:]


def replace(i, row): return lines[:i] + [row] + lines[i + 1:]


def insert(i, row): return lines[:i] + [row] + lines[i:]


def cut_last_field(i): return replace(i, lines[i].rsplit("\t", 1)[0])


write("intact", lines)
# the envelope
i_done = first(lambda l: l.startswith("DONE\t")); write("DONE_missing", drop(i_done))
write("DONE_duplicate", insert(i_done, lines[i_done]))
i_job = first(lambda l: l.startswith("JOB\t")); write("JOB_duplicate", lines + lines[i_job:i_done + 1])
write("JOB_unknown", replace(i_job, "JOB\tno-such-recipe"))
write("UNKNOWN_kind", insert(i_job + 1, "WHAT\t1\tx"))
write("FAIL_claimed", insert(i_done, "FAIL\t%s\tplanted" % key))
# the stages
i_s2 = first(lambda l: l == "STAGE\t2"); write("STAGE_gap", drop(i_s2))
i_s5 = first(lambda l: l == "STAGE\t5"); write("STAGE_duplicate", insert(i_s5, lines[i_s5]))
i_s3 = first(lambda l: l == "STAGE\t3")
i_x = first(lambda l: l.startswith("X\t"))
write("EXIT_after_full", insert(i_x, "EXIT\t6\t1"))   # after the fifth stage's rows, before the GLB rows
write("EXIT_wrong_stage", lines[:i_s3] + ["EXIT\t2\t1", lines[i_done]])
write("EXIT_code", lines[:i_s3] + ["EXIT\t3\t2", lines[i_done]])
write("EXIT_zero_not_fast", lines[:i_s3] + ["EXIT\t3\t0", lines[i_done]])
write("STOP_valid", lines[:i_s3] + ["EXIT\t3\t1", lines[i_done]])
write("STOP_missing", lines[:i_s3] + [lines[i_done]])
write("STOP_with_export", lines[:i_s3] + ["EXIT\t3\t1"] + lines[i_x:])
write("EXIT_ran_full", ["JOB\tEXIT:1:" + key] + lines[1:i_done] + ["DONE\tEXIT:1:" + key])
write("EXIT_marked_wrong_stage", ["JOB\tEXIT:1:" + key] + lines[1:i_s3] + ["EXIT\t3\t1", "DONE\tEXIT:1:" + key])
write("FAST_mode_mismatch", lines)   # run with jobs_fast.txt: the jobs file itself is refused
i_log = first(lambda l: l.startswith("LOG\t")); write("LOG_not_vehicle", replace(i_log, lines[i_log].rsplit("\t", 1)[0] + "\tInfo: something else"))
write("LOG_timing", insert(i_log, "LOG\t" + lines[i_log].split("\t")[1] + "\tVEHICLE timing: import          1.0s"))   # the same stage as the row beside it
write("LOG_misplaced", replace(i_log, "LOG\t5" + lines[i_log][5:]))
# the rows
i_scene = first(lambda l: l.startswith("SCENE\t1\t")); write("SCENE_missing", drop(i_scene))
write("SCENE_short", cut_last_field(i_scene))
i_obj = first(lambda l: l.startswith("OBJ\t1\t")); write("OBJ_short", cut_last_field(i_obj))
write("OBJ_bits", replace(i_obj, "\t".join(lines[i_obj].split("\t")[:-1] + ["zz000000"])))
write("OBJ_duplicate", insert(i_obj, lines[i_obj]))
i_mesh = first(lambda l: l.startswith("MESH\t1\t")); write("MESH_missing", drop(i_mesh))
write("MESH_hash", replace(i_mesh, "\t".join(f if j != 6 else "0" * 63 for j, f in enumerate(lines[i_mesh].split("\t")))))
write("MESH_orphan", replace(i_mesh, "\t".join(f if j != 2 else "no-such-object" for j, f in enumerate(lines[i_mesh].split("\t")))))
i_vg = first(lambda l: l.startswith("VG\t")); write("VG_missing", drop(i_vg))
write("VG_orphan", replace(i_vg, "\t".join(f if j != 3 else "no-such-group" for j, f in enumerate(lines[i_vg].split("\t")))))
i_bone = first(lambda l: l.startswith("BONE\t")); write("BONE_missing", drop(i_bone))
write("BONE_duplicate", insert(i_bone, lines[i_bone]))
write("BONE_short", cut_last_field(i_bone))
i_pb = first(lambda l: l.startswith("PB\t")); write("PB_missing", drop(i_pb))
write("PB_orphan", replace(i_pb, "\t".join(f if j != 2 else "no-such-bone" for j, f in enumerate(lines[i_pb].split("\t")))))
i_act = first(lambda l: l.startswith("ACT\t")); write("ACT_missing", drop(i_act))
i_fc = first(lambda l: l.startswith("FC\t")); write("FC_missing", drop(i_fc))
write("FC_duplicate", insert(i_fc, lines[i_fc]))
write("FC_short", cut_last_field(i_fc))
# the GLB rows
write("X_missing", [l for l in lines if not l.startswith("X\t")])
write("X_unknown_kind", insert(i_x, "X\tWHAT\t1"))
write("X_after_done", lines + [lines[i_x]])
write("X_died_and_rows", insert(i_x, "XDIES\tplanted"))
i_glb = first(lambda l: l.startswith("X\tGLB\t")); write("X_no_glb_row", drop(i_glb))
if any(l.startswith("K\t") for l in lines):
    i_k = first(lambda l: l.startswith("K\t"))
    write("K_missing", drop(i_k))
    k = lines[i_k].split("\t")
    write("K_bits", replace(i_k, "\t".join(k[:7] + [("0" if k[7][0] != "0" else "1") + k[7][1:]] + k[8:])))
    write("K_meta", replace(i_k, "\t".join(k[:12] + ["CHANGED"] + k[13:])))
    write("K_orphan", replace(i_k, "\t".join(k[:2] + ["no-such-action"] + k[3:])))
for name in sorted(os.listdir(out)):
    if name.endswith(".txt") and name not in ("intact.txt", "jobs.txt", "jobs_exit.txt", "jobs_fast.txt"):
        print(name[:-4])
