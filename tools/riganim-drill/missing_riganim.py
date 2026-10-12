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
open(os.path.join(out, "jobs_exit.txt"), "w", encoding="utf-8", newline="\n").write("\n".join(j.replace("JOB\t" + key + "\t", "JOB\tEXIT:1:" + key + "\t", 1) for j in mine) + "\n")


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
write("EXIT_ran_full", ["JOB\tEXIT:1:" + key] + lines[1:blk_end] + ["DONE\tEXIT:1:" + key] + lines[blk_end + 1:])
write("EXIT_marked_wrong_stage", ["JOB\tEXIT:1:" + key] + lines[1:i_s3] + ["EXIT\t3\t1", "DONE\tEXIT:1:" + key])
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
if any(l.startswith("K\t") for l in lines):
    i_k = first(lambda l: l.startswith("K\t"))
    write("K_missing", drop(i_k))
    k = lines[i_k].split("\t")
    write("K_bits", replace(i_k, "\t".join(k[:6] + [("0" if k[6][0] != "0" else "1") + k[6][1:]] + k[7:])))
    write("K_meta", replace(i_k, "\t".join(k[:11] + ["CHANGED"] + k[12:])))
    write("K_orphan", replace(i_k, "\t".join(k[:2] + ["no-such-action"] + k[3:])))
# Step (b), the stage 1 model (riganim.exe --stage1): a dump that is complete and consistent but says something ELSE about
# the scene than the C# does must be refused too - one changed value per row kind of stage 1, the dump otherwise valid.
# Named S1_*: the drill runs these in --stage1 mode, where the reason is "stage 1".


def flip_bits(h):
    """Another valid float32 (or SHA-256) hex: the last digit changed."""
    return h[:-1] + ("1" if h[-1] != "1" else "2")


def with_field(i, j, value):
    f = lines[i].split("\t"); f[j] = value
    return replace(i, "\t".join(f))


i_log1 = first(lambda l: l.startswith("LOG\t1\t")); write("S1_LOG_text", replace(i_log1, lines[i_log1] + " (changed)"))
write("S1_SCENE_frame", with_field(i_scene, 3, "251"))
write("S1_OBJ_matrix", with_field(i_obj, 10, flip_bits(lines[i_obj].split("\t")[10])))
i_obj_act = first(lambda l: l.startswith("OBJ\t1\t") and l.split("\t")[8] != "-"); write("S1_OBJ_action", with_field(i_obj_act, 8, "-"))
i_obj2 = first(lambda l: l.startswith("OBJ\t1\t") and l != lines[i_obj])
write("S1_OBJ_order", [lines[i_obj2] if i == i_obj else lines[i_obj] if i == i_obj2 else l for i, l in enumerate(lines)])
write("S1_MESH_hash", with_field(i_mesh, 6, flip_bits(lines[i_mesh].split("\t")[6])))
write("S1_MESH_count", with_field(i_mesh, 3, str(int(lines[i_mesh].split("\t")[3]) + 1)))
write("S1_VG_count", with_field(i_vg, 4, str(int(lines[i_vg].split("\t")[4]) + 1)))
write("S1_BONE_length", with_field(i_bone, 6, flip_bits(lines[i_bone].split("\t")[6])))
write("S1_PB_value", with_field(i_pb, 7, flip_bits(lines[i_pb].split("\t")[7])))
write("S1_ACT_range", with_field(i_act, 3, flip_bits(lines[i_act].split("\t")[3])))
write("S1_FC_hash", with_field(i_fc, 6, flip_bits(lines[i_fc].split("\t")[6])))
# Step (c), the stage 2 model: one changed value per row kind of stage 2 (the rows of stage 1 intact), refused in
# --stages 2 mode with the reason "stage 2".
i_obj_2 = first(lambda l: l.startswith("OBJ\t2\t")); write("S2_OBJ_matrix", with_field(i_obj_2, 10, flip_bits(lines[i_obj_2].split("\t")[10])))
write("S2_OBJ_parent", with_field(i_obj_2, 4, "-" if lines[i_obj_2].split("\t")[4] != "-" else lines[i_obj_2].split("\t")[2]))
write("S2_OBJ_extra", insert(i_obj_2, "\t".join(f if j != 2 else "planted_object" for j, f in enumerate(lines[i_obj_2].split("\t")))))
i_mesh_2 = first(lambda l: l.startswith("MESH\t2\t")); write("S2_MESH_groups", with_field(i_mesh_2, 12, "planted_group"))
write("S2_MESH_modifier", with_field(i_mesh_2, 13, "ARMATURE:Armature:planted"))
i_vg_2 = first(lambda l: l.startswith("VG\t2\t")); write("S2_VG_hash", with_field(i_vg_2, 5, flip_bits(lines[i_vg_2].split("\t")[5])))
i_pb_2 = first(lambda l: l.startswith("PB\t2\t")); write("S2_PB_value", with_field(i_pb_2, 7, flip_bits(lines[i_pb_2].split("\t")[7])))
i_fc_2 = first(lambda l: l.startswith("FC\t2\t")); write("S2_FC_hash", with_field(i_fc_2, 7, flip_bits(lines[i_fc_2].split("\t")[7])))
i_scene_2 = first(lambda l: l.startswith("SCENE\t2\t")); write("S2_SCENE_pose", with_field(i_scene_2, 5, "REST"))
i_stage_2 = first(lambda l: l == "STAGE\t2"); write("S2_LOG_added", insert(i_stage_2, "LOG\t2\tRIGANIM flattened 1 wrapper empty; rig reparented to root for identity export nodes (planted)"))
sys.stdout.write("".join(f[:-4] + "\n" for f in sorted(os.listdir(out)) if f.endswith(".txt") and f not in ("jobs.txt", "jobs_exit.txt", "intact.txt")))
