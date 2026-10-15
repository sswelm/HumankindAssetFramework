"""Damaged copies of a vehicle_rig stage dump (tools/vehiclerig_drill.sh): each must be REFUSED by vehiclerig.exe, so that a
missing or inconsistent record can never pass as evidence. Also writes intact.txt, the control, which must pass, and
jobs.txt, the jobs file naming just the job the copies hold (the first block that ran all five stages with bones, vertex
groups, fcurves and GLB rows: every row kind present).

usage: missing_vehiclerig.py <output directory> <jobs file> <dump>...
"""
import os, sys, hashlib

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
jobs = [l.rstrip("\r") for l in open(sys.argv[2], encoding="utf-8").read().split("\n") if l.startswith("JOB\t") or l.startswith("FAST\t")]
lines = None
for d in sys.argv[3:]:
    rows = [l.rstrip("\r") for l in open(d, encoding="utf-8").read().split("\n") if l.strip()]
    starts = [i for i, l in enumerate(rows) if l.startswith("JOB\t")] + [len(rows)]
    for a, b in zip(starts, starts[1:]):
        block = rows[a:b]
        # a glTF job that runs every stage model (no FBX source, no dissolve tier): the S1_/S2_ tampers must reach the MODEL's compare
        key0 = block[0].split("\t")[1]
        job0 = next((j for j in jobs if j.split("\t")[1] == key0), None)
        src_ok = job0 is not None and job0.split("\t")[3].lower().rsplit(".", 1)[-1] in ("glb", "gltf")
        soup = any(l.startswith("LOG\t2\tVEHICLE RIGGING ") or l.startswith("LOG\t2\tVEHICLE SHROUD ") for l in block)
        if src_ok and not soup and "STAGE\t5" in block and any(l.startswith("VG\t") for l in block) and any(l.startswith("FC\t") for l in block) and any(l.startswith("X\tGLB\t") for l in block):
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


def write(name, rows, repair=True):
    if repair:
        rows = [r for r in rows if not r.startswith("EVIDENCE\t")]
        # Keep structural tamper tests independent of the integrity check. Intentionally broken DONE framing is
        # left broken; every otherwise framed mutant receives a matching seal and must fail its semantic check.
        if sum(r.startswith("DONE\t") for r in rows) == 1 and rows[-1].startswith("DONE\t"):
            body = rows[:-1]
            digest = hashlib.sha256(("\n".join(body) + "\n").encode("utf-8")).hexdigest()
            rows = body + ["EVIDENCE\t%s\t%d\t%s" % (rows[0].split("\t")[1], len(body), digest), rows[-1]]
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
# Integrity counterexamples: retain the oracle's seal so even complete-looking losses/edits are refused.
i_evidence = first(lambda l: l.startswith("EVIDENCE\t"))
write("EVIDENCE_missing", drop(i_evidence), False)
fields = lines[i_evidence].split("\t")
write("EVIDENCE_count", replace(i_evidence, "\t".join(fields[:2] + [str(int(fields[2]) + 1), fields[3]])), False)
write("EVIDENCE_hash", replace(i_evidence, "\t".join(fields[:3] + ["0" * 64])), False)
write("LOG_lost", drop(i_log), False)
fields = lines[i_obj].split("\t"); fields[-1] = "00000000" if fields[-1] != "00000000" else "3f800000"
write("OBJ_changed", replace(i_obj, "\t".join(fields)), False)
write("X_header_only", [l for l in lines if not l.startswith("X\t") or l.startswith("X\tGLB\t")])
for kind in ("NODE", "SKIN", "IBM", "MESH", "PRIM", "MAT", "IMG", "TEX", "SAMP", "ANIM", "CHAN"):
    candidates = [i for i, line in enumerate(lines) if line.startswith("X\t" + kind + "\t")]
    if candidates:
        i = candidates[0]
        write("X_short_" + kind, cut_last_field(i), repair=kind != "PRIM")
        write("X_duplicate_" + kind, insert(i, lines[i]))
# Step (b), the stage 1 model (vehiclerig.exe --stages 1): a complete and consistent dump that says something ELSE about the
# scene than the C# does must be refused too - one changed value per row kind of stage 1, the seal repaired. Named S1_*: the
# drill runs these in --stages 1 mode, where the reason is "stage 1".


def flip_bits(h):
    """Another valid float32 (or SHA-256) hex: the last digit changed."""
    return h[:-1] + ("1" if h[-1] != "1" else "2")


def with_field(i, j, value):
    f = lines[i].split("\t"); f[j] = value
    return replace(i, "\t".join(f))


i_st1 = first(lambda l: l == "STAGE\t1"); write("S1_LOG_extra", insert(i_st1, "LOG\t1\tVEHICLE planted: a line the script never printed"))   # LOG rows precede their stage's row
if any(l.startswith("LOG\t1\t") for l in lines):   # a clean import prints no VEHICLE line in stage 1
    i_log1 = first(lambda l: l.startswith("LOG\t1\t")); write("S1_LOG_text", replace(i_log1, lines[i_log1] + " (changed)"))
    write("S1_LOG_dropped", drop(i_log1))
write("S1_SCENE_start", with_field(i_scene, 2, "2"))
write("S1_SCENE_frame", with_field(i_scene, 3, "251"))
write("S1_OBJ_matrix", with_field(i_obj, 10, flip_bits(lines[i_obj].split("\t")[10])))
write("S1_OBJ_data", with_field(i_obj, 7, "planted_mesh"))
if sum(l.startswith("OBJ\t1\t") for l in lines) > 1:
    i_obj2 = first(lambda l: l.startswith("OBJ\t1\t") and l != lines[i_obj])
    write("S1_OBJ_order", [lines[i_obj2] if i == i_obj else lines[i_obj] if i == i_obj2 else l for i, l in enumerate(lines)])
# an object more, WITH its mesh row (so the dump stays consistent and only the model can refuse it)
_extra_obj = "\t".join(f if j != 2 else "planted_object" for j, f in enumerate(lines[i_obj].split("\t")))
_extra_mesh = "\t".join(f if j != 2 else "planted_object" for j, f in enumerate(lines[i_mesh].split("\t")))
write("S1_OBJ_extra", insert(i_mesh, _extra_mesh)[:i_obj] + [_extra_obj] + insert(i_mesh, _extra_mesh)[i_obj:])
write("S1_MESH_hash", with_field(i_mesh, 6, flip_bits(lines[i_mesh].split("\t")[6])))
write("S1_MESH_loops", with_field(i_mesh, 7, flip_bits(lines[i_mesh].split("\t")[7])))
write("S1_MESH_count", with_field(i_mesh, 3, str(int(lines[i_mesh].split("\t")[3]) + 1)))
write("S1_MESH_materials", with_field(i_mesh, 11, "planted_material"))
if any(l.startswith("ACT\t1\t") for l in lines):
    i_act1 = first(lambda l: l.startswith("ACT\t1\t")); write("S1_ACT_range", with_field(i_act1, 3, flip_bits(lines[i_act1].split("\t")[3])))
    i_fc1 = first(lambda l: l.startswith("FC\t1\t")); write("S1_FC_hash", with_field(i_fc1, 7, flip_bits(lines[i_fc1].split("\t")[7])))
# Step (c), the stage 2 model (vehiclerig.exe --stages 2): one changed value per row kind of stage 2, refused by that model
i_st2 = first(lambda l: l == "STAGE\t2"); write("S2_LOG_extra", insert(i_st2, "LOG\t2\tVEHICLE planted: a tier line the script never printed"))
if any(l.startswith("LOG\t2\t") for l in lines):
    i_log2 = first(lambda l: l.startswith("LOG\t2\t")); write("S2_LOG_text", replace(i_log2, lines[i_log2] + " (changed)"))
i_obj2s = first(lambda l: l.startswith("OBJ\t2\t")); write("S2_OBJ_data", with_field(i_obj2s, 7, "planted_mesh"))
i_mesh2 = first(lambda l: l.startswith("MESH\t2\t")); write("S2_MESH_hash", with_field(i_mesh2, 6, flip_bits(lines[i_mesh2].split("\t")[6])))
write("S2_MESH_loops", with_field(i_mesh2, 7, flip_bits(lines[i_mesh2].split("\t")[7])))
write("S2_MESH_count", with_field(i_mesh2, 3, str(int(lines[i_mesh2].split("\t")[3]) + 1)))
write("S2_MESH_uv", with_field(i_mesh2, 10, flip_bits(lines[i_mesh2].split("\t")[10])))
write("S2_MESH_edges", with_field(i_mesh2, 15, flip_bits(lines[i_mesh2].split("\t")[15])))
# the sharp set and the custom normals of the first stage 2 mesh that has them (a weld tier marks edges; the importer sets normals)
i_sharp = next((i for i, l in enumerate(lines) if l.startswith("MESH\t2\t") and l.split("\t")[16] != "-"), -1)
if i_sharp >= 0: write("S2_MESH_sharp", with_field(i_sharp, 16, flip_bits(lines[i_sharp].split("\t")[16])))
i_cn = next((i for i, l in enumerate(lines) if l.startswith("MESH\t2\t") and l.split("\t")[17] != "-"), -1)
if i_cn >= 0: write("S2_MESH_normals", with_field(i_cn, 17, flip_bits(lines[i_cn].split("\t")[17])))
for name in sorted(os.listdir(out)):
    if name.endswith(".txt") and name not in ("intact.txt", "jobs.txt", "jobs_exit.txt", "jobs_fast.txt"):
        print(name[:-4])
