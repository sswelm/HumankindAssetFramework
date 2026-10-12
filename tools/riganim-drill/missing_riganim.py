"""Damaged copies of a rig_anim stage dump (tools/riganim_drill.sh): each must be REFUSED by riganim.exe, so that a
missing or inconsistent record can never pass as evidence. Also writes intact.txt, the control, which must pass, and
jobs.txt, the jobs file naming just the job the copies hold (the first block that ran all four stages and wrote an
albedo: every row kind present).

usage: missing_riganim.py <output directory> <jobs file> <dump>...
"""
import os, sys

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
jobs = [l.rstrip("\r") for l in open(sys.argv[2], encoding="utf-8").read().split("\n") if l.startswith("JOB\t")]
lines = None
for d in sys.argv[3:]:
    rows = [l.rstrip("\r") for l in open(d, encoding="utf-8").read().split("\n") if l.strip()]
    starts = [i for i, l in enumerate(rows) if l.startswith("JOB\t")] + [len(rows)]
    for a, b in zip(starts, starts[1:]):
        block = rows[a:b]
        if "STAGE\t4" in block and any(l.startswith("IMG\t") for l in block):
            lines = block; break
    if lines is not None:
        break
if lines is None:
    raise RuntimeError("no job ran through all four stages with an albedo: nothing to damage")
key = lines[0].split("\t")[1]
mine = [j for j in jobs if j.split("\t")[1] == key]
open(os.path.join(out, "jobs.txt"), "w", encoding="utf-8", newline="\n").write("\n".join(mine) + "\n")
# the same job under an "EXIT:" key (jobs_exit.txt, for EXIT_ran_full): a dump of it running through all four stages must be refused
open(os.path.join(out, "jobs_exit.txt"), "w", encoding="utf-8", newline="\n").write("\n".join(j.replace("JOB\t" + key + "\t", "JOB\tEXIT:" + key + "\t", 1) for j in mine) + "\n")


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
write("FAIL_claimed", insert(i_done, "FAIL\t%s\tplanted" % lines[i_job].split("\t")[1]))
# the stages
i_s2 = first(lambda l: l == "STAGE\t2"); write("STAGE_gap", drop(i_s2))
i_s4 = first(lambda l: l == "STAGE\t4"); write("STAGE_duplicate", insert(i_s4, lines[i_s4]))
write("EXIT_after_full", insert(i_done, "EXIT\t5\t1"))
# a stopped job must name the next stage: cut the block at stage 3 and claim an exit at stage 2
blk_start = i_job; blk_end = i_done
i_s3 = first(lambda l: l == "STAGE\t3")
write("EXIT_wrong_stage", lines[blk_start:i_s3] + ["EXIT\t2\t1", lines[blk_end]] + lines[blk_end + 1:])
write("EXIT_code", lines[blk_start:i_s3] + ["EXIT\t3\t0", lines[blk_end]] + lines[blk_end + 1:])
write("STOP_valid", lines[blk_start:i_s3] + ["EXIT\t3\t1", lines[blk_end]] + lines[blk_end + 1:])
write("STOP_missing", lines[blk_start:i_s3] + [lines[blk_end]] + lines[blk_end + 1:])
write("EXIT_ran_full", ["JOB\tEXIT:" + key] + lines[1:blk_end] + ["DONE\tEXIT:" + key] + lines[blk_end + 1:])
i_log = first(lambda l: l.startswith("LOG\t")); write("LOG_not_riganim", replace(i_log, lines[i_log].rsplit("\t", 1)[0] + "\tInfo: something else"))
write("LOG_misplaced", replace(i_log, "LOG\t4" + lines[i_log][5:]))
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
i_bone = first(lambda l: l.startswith("BONE\t1\t")); write("BONE_missing", drop(i_bone))
write("BONE_duplicate", insert(i_bone, lines[i_bone]))
write("BONE_short", cut_last_field(i_bone))
i_pb = first(lambda l: l.startswith("PB\t1\t")); write("PB_missing", drop(i_pb))
write("PB_orphan", replace(i_pb, "\t".join(f if j != 2 else "no-such-bone" for j, f in enumerate(lines[i_pb].split("\t")))))
i_act = first(lambda l: l.startswith("ACT\t")); write("ACT_missing", drop(i_act))
i_fc = first(lambda l: l.startswith("FC\t")); write("FC_missing", drop(i_fc))
write("FC_duplicate", insert(i_fc, lines[i_fc]))
write("FC_short", cut_last_field(i_fc))
i_img = first(lambda l: l.startswith("IMG\t")); write("IMG_missing", drop(i_img))
write("IMG_short", cut_last_field(i_img))
write("IMG_early", replace(i_img, "IMG\t3" + lines[i_img][5:]))
sys.stdout.write("".join(f[:-4] + "\n" for f in sorted(os.listdir(out)) if f.endswith(".txt") and f not in ("jobs.txt", "jobs_exit.txt", "intact.txt")))
