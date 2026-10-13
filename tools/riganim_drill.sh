#!/usr/bin/env bash
# riganim_drill.sh - replacing rig_anim.py (2026-10-12), held to Blender stage by stage.
# STEP (a), THE STAGE DUMP: Blender runs rig_anim.py ITSELF on every animated recipe of the registry, with the arguments
# the Factory hands it (tools/riganim-drill/riganim_jobs.py, from pack.json as UniversalBaker formats them), the script
# cut into its four stages at four of its own lines and the scene written out after each (blender_riganim_dump.py: the
# RIGANIM lines, every object with its matrices, every mesh's geometry and weights hashed, the bones at rest, the pose,
# every action's fcurves hashed, the albedo's pixels). The C# side (RigAnimDrill.cs) holds the EVIDENCE: every recipe's
# block complete and consistent, an unexpected early stop failed with its stage and reason - and prints the census of the
# script's branches the recipes take (COVER rows), which the stage ports (b)..(e) build on.
# STEP (b), STAGE 1 IN C# (2026-10-12): editor/BlenderRigAnim.cs models the first stage - the arguments as the script
# parses them, the import, the material-less drop, the armature lookup and its exit, the contract marker, the
# object-animation clear - and riganim.exe --stage1 renders its scene as the dump's own rows (float32 bits, SHA-256
# hashes) and holds every job's stage 1 against Blender's, string for string: a differing field names the row and the
# values, an FC hash the first differing key when RIGANIM_KEYS=1. A job the model would leave to Blender FAILS unless
# its key says LEFT:. The S1_* tampers (missing_riganim.py) change one value per row kind and must be refused in that
# mode.
# STEP (c), STAGE 2 IN C# (2026-10-12): BlenderRigAnim.RunStage2 continues on that scene - the bone-parent -> skin bind
# (the governing bone up the parent chain, staticParts, the rest-world pin through Blender's matrix_world assignment
# arithmetic, the full-weight vertex group, the Armature modifier, the re-evaluation) and the wrapper flatten - and
# `riganim.exe --stages 2` compares stage 2's rows the same way; a LEFT: job may be declined at either stage, its
# earlier stage compared all the same. The S2_* tampers must be refused too. Stages 3 and 4 are not compared yet: the
# PASS line says so.
# The light assault mech's source is an .fbx, which the C# does not read until the native FBX reader: that recipe is
# LEFT by name and the coverage is reported as 23 of 24, never as the whole registry.
# The sample: the recipes whose source is under 40 MB; FULL=1 takes every size (507 MB in all, 17 s in four shards on
# 2026-10-12). Both sides run as several processes (RIGANIM_JOBS, default 4).
# Prerequisites and SKIP rules as deploy_drill.sh. Edit this script only while no FULL run is executing it.
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; rig_anim was NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (rig_anim was NOT drilled)"; exit 2; }
BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then
  for base in "/c/Program Files/Blender Foundation" "/c/Program Files (x86)/Blender Foundation"; do
    cand=$(ls -d "$base"/Blender* 2>/dev/null | sort -V | tail -1); [ -n "$cand" ] && [ -x "$cand/blender.exe" ] && { BLENDER="$cand/blender.exe"; break; }
  done
fi
[ -n "$BLENDER" ] || { echo "SKIP — rig_anim drill: Blender not found (BLENDER=<exe> to force); rig_anim was NOT drilled"; exit 3; }
SCRIPT="$ROOT/editor/Tools~/rig_anim.py"
[ -f "$SCRIPT" ] || { echo "FAIL — rig_anim.py not found at $SCRIPT"; exit 1; }
python "$ROOT/tools/riganim-drill/test_riganim_jobs.py" || { echo "FAIL — rig_anim job mapping regression checks"; exit 1; }

NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; rig_anim was NOT drilled)"; exit 2; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
E="$WROOT/editor"
# the drill and the stage 1 model (BlenderRigAnim.cs) with the editor files it is built from - the deploy drill's list
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -optimize+ -out:"$WTMP/riganim.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WAPI/System.Numerics.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/riganim-drill/RigAnimDrill.cs" "$E/BlenderRigAnim.cs" "$E/BlenderDeploy.cs" "$E/BlenderDeployExport.cs" "$E/BlenderMaterialExport.cs" "$E/BlenderChannelExport.cs" "$E/BlenderGlbWriter.cs" "$E/PyFloatRepr.cs" "$E/BlenderFCurve.cs" "$E/BlenderExportTree.cs" \
  "$E/BlenderExport.cs" "$E/BlenderReduce.cs" "$E/BlenderDecimate.cs" \
  "$E/HafModel.cs" "$E/GlbReader.cs" "$E/HafTransforms.cs" "$E/BlenderNames.cs" "$E/BlenderPosedState.cs" "$E/BlenderTrig.cs" "$E/BlenderEigen.cs" "$E/BlenderMesh.cs" "$E/BMesh.cs" "$E/BlenderColor.cs" \
  "$E/VehicleProbe.cs" "$E/VehicleProbe.Visibility.cs" "$E/VehicleProbe.Islands.cs" "$E/VehicleProbe.InsideOut.cs" \
  "$E/VehicleProbe.BlenderWorld.cs" "$E/VehicleProbe.BlenderSkin.cs" "$E/VehicleProbe.CustomNormals.cs" "$E/VehicleProbe.Merge.cs" "$E/VehicleProbe.BlenderArmature.cs" "$E/VehicleProbe.BlenderPose.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/riganim.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the rig_anim drill did not compile (csc rc=$rc)"; exit 1; fi

PACK=$(ls "$PROJECT"/Assets/Pack/*/pack.json 2>/dev/null | head -1)
n_jobs=0; n_left=0
if [ -n "$PACK" ]; then
  if [ "${FULL:-0}" = "1" ]; then
    python "$ROOT/tools/riganim-drill/riganim_jobs.py" "$PACK" "$PROJECT" --all > "$TMPD/jobs_raw.txt"; jrc=$?
  else
    python "$ROOT/tools/riganim-drill/riganim_jobs.py" "$PACK" "$PROJECT" > "$TMPD/jobs_raw.txt"; jrc=$?
  fi
  if [ "$jrc" -ne 0 ]; then
    tr -d '\r' < "$TMPD/jobs_raw.txt" | grep -E '^(INVALID|UNAVAILABLE)[[:space:]]' || true
    if [ "$jrc" -eq 3 ]; then
      echo "SKIP — rig_anim drill: required registry source is unavailable on this machine; rig_anim was NOT drilled"; exit 3
    fi
    echo "FAIL — rig_anim drill: invalid animated registry recipe at $PACK"; exit 1
  fi
  tr -d '\r' < "$TMPD/jobs_raw.txt" > "$TMPD/jobs.txt"
  n_jobs=$(grep -c "^JOB	" "$TMPD/jobs.txt"); n_left=$(grep -c "^LEFT	" "$TMPD/jobs.txt")
fi
n_reg=$n_jobs
[ "$n_reg" -gt 0 ] || { echo "SKIP — rig_anim drill: no animated recipe with a readable source in the registry at $PROJECT (set HAF_UNITY_PROJECT); rig_anim was NOT drilled"; exit 3; }
grep "^LEFT	" "$TMPD/jobs.txt" | sed 's/^LEFT\t/LEFT /; s/\t/: /'
# the fixtures: the branches no recipe takes (riganim_fixtures.py); EXIT:<stage>: names the expected exit stage
python "$ROOT/tools/riganim-drill/riganim_fixtures.py" "$WTMP/fixtures" > "$TMPD/fixtures_raw.txt" || { echo "FAIL — could not write the rig_anim fixtures"; exit 1; }
tr -d '\r' < "$TMPD/fixtures_raw.txt" >> "$TMPD/jobs.txt"
n_fx=$(grep -c "^JOB	" "$TMPD/fixtures_raw.txt"); n_jobs=$((n_reg + n_fx))
[ "$n_fx" -gt 0 ] || { echo "FAIL — the rig_anim fixtures gave no job"; exit 1; }

JOBS="${RIGANIM_JOBS:-4}"; [ "$JOBS" -ge 1 ] 2>/dev/null || JOBS=1
[ "$JOBS" -le "$n_jobs" ] || JOBS="$n_jobs"
# shards by size, the largest first, round robin
python - "$TMPD/jobs.txt" "$TMPD" "$JOBS" <<'PYEOF'
import os, sys
jobs = [l for l in open(sys.argv[1], encoding="utf-8").read().split("\n") if l.startswith("JOB\t")]
jobs.sort(key=lambda l: -os.path.getsize(l.split("\t")[2]))
n = int(sys.argv[3])
for k in range(n):
    open(os.path.join(sys.argv[2], "shard_%d.txt" % k), "w", encoding="utf-8", newline="\n").write("\n".join(jobs[k::n]) + "\n")
PYEOF
t0=$(date +%s)
BPIDS=()
for ((k = 0; k < JOBS; k++)); do
  mkdir -p "$TMPD/work_$k"
  "$BLENDER" --background --python "$(cygpath -m "$ROOT/tools/riganim-drill/blender_riganim_dump.py")" -- "$(cygpath -m "$SCRIPT")" "$WTMP/shard_$k.txt" "$WTMP/work_$k" > "$TMPD/blender_raw_$k.txt" 2>&1 &
  BPIDS+=($!)
done
brc=0; for pid in "${BPIDS[@]}"; do wait "$pid" || brc=$?; done
t1=$(date +%s)
n_b=0
for ((k = 0; k < JOBS; k++)); do
  tr -d '\r' < "$TMPD/blender_raw_$k.txt" | grep -E "^(JOB|STAGE|LOG|SCENE|OBJ|MESH|VG|BONE|PB|ACT|FC|K|IMG|EXIT|DIES|DONE|FAIL)	" > "$TMPD/dump_$k.txt"
  n_b=$((n_b + $(grep -c "^DONE" "$TMPD/dump_$k.txt")))
done
if [ "$brc" -ne 0 ] || [ "$n_b" -ne "$n_jobs" ]; then
  cat "$TMPD"/blender_raw_*.txt | grep -E "^FAIL|Traceback|Error" | head -8
  echo "FAIL — rig_anim drill: Blender dumped $n_b of $n_jobs jobs (exit $brc)"; exit 1
fi
DUMPS=(); for ((k = 0; k < JOBS; k++)); do DUMPS+=("$WTMP/dump_$k.txt"); done
KEY_MODE=(); [ "${RIGANIM_KEYS:-0}" != "1" ] || KEY_MODE=(--keys)
"$TMPD/riganim.exe" "${KEY_MODE[@]}" "$WTMP/jobs.txt" "${DUMPS[@]}" > "$TMPD/cs_raw.txt" 2>&1; rc=$?
t2=$(date +%s)
tr -d '\r' < "$TMPD/cs_raw.txt" > "$TMPD/cs.txt"
grep -E "^FAIL|^STOP|^COVER" "$TMPD/cs.txt"
TOTAL=$(grep -E "^TOTAL" "$TMPD/cs.txt" | tail -1); echo "$TOTAL"
[ -n "$TOTAL" ] || { tail -5 "$TMPD/cs.txt"; echo "FAIL — rig_anim drill: the C# side gave no total (rc=$rc)"; exit 1; }
[ "$rc" -eq 0 ] || { echo "FAIL — rig_anim drill: the stage dump is incomplete or inconsistent"; exit 1; }
n_full=$(echo "$TOTAL" | awk '{print $5}'); n_stop=$(echo "$TOTAL" | awk '{print $7}'); n_stages=$(echo "$TOTAL" | awk '{print $11}'); n_logs=$(echo "$TOTAL" | awk '{print $13}'); n_bones=$(echo "$TOTAL" | awk '{print $21}'); n_fc=$(echo "$TOTAL" | awk '{print $23}')
n_exit=$(grep -c "^JOB	EXIT:" "$TMPD/jobs.txt")
[ "$n_full" -eq $((n_jobs - n_exit)) ] && [ "$n_stop" -eq "$n_exit" ] || { echo "FAIL — rig_anim drill: not every selected recipe completed all four stages (or an EXIT: fixture did not stop)"; exit 1; }
# STEPS (b) AND (c), STAGES 1 AND 2: the C# model of the first two stages (editor/BlenderRigAnim.cs) against every job's
# rows of those stages, as strings - the dump's own float32 bits and hashes. Every dumped job must be equal row for row
# at both stages, or left by name (LEFT:) at one of them, its earlier stage equal.
"$TMPD/riganim.exe" --stages 3 "${KEY_MODE[@]}" "$WTMP/jobs.txt" "${DUMPS[@]}" > "$TMPD/s1_raw.txt" 2>&1; s1rc=$?
t3=$(date +%s)
tr -d '\r' < "$TMPD/s1_raw.txt" > "$TMPD/s1.txt"
grep -E "^FAIL|^MODEL" "$TMPD/s1.txt"
S1=$(grep -E "^STAGES 3 jobs" "$TMPD/s1.txt" | tail -1); echo "$S1"
[ -n "$S1" ] || { tail -5 "$TMPD/s1.txt"; echo "FAIL — rig_anim drill: the stage 1-2 model gave no total (rc=$s1rc)"; exit 1; }
[ "$s1rc" -eq 0 ] || { echo "FAIL — rig_anim drill: the stage 1-2 model differs from Blender, or leaves a dumped job to it"; exit 1; }
n_s1=$(echo "$S1" | awk '{print $4}'); n_s1eq=$(echo "$S1" | awk '{print $6}'); n_s1left=$(echo "$S1" | awk '{print $8}'); n_s1rows=$(echo "$S1" | awk '{print $10}'); n_s3def=$(echo "$S1" | awk '{print $12}')
[ "$n_s1" -eq "$n_jobs" ] && [ $((n_s1eq + n_s1left + n_s3def)) -eq "$n_jobs" ] || { echo "FAIL — rig_anim drill: the stage 1-3 model compared $n_s1 of $n_jobs jobs ($n_s1eq equal, $n_s1left left, $n_s3def deferred)"; exit 1; }
n_s3eq=$(grep -c "^STAGE3 .*: equal" "$TMPD/s1.txt")
n_s2eq=$(grep -c "^STAGE2 .*: equal" "$TMPD/s1.txt")
# Negative guards on the REAL dump: a missing or inconsistent record must never pass as evidence.
python "$ROOT/tools/riganim-drill/missing_riganim.py" "$WTMP/missing" "$WTMP/jobs.txt" "${DUMPS[@]}" > "$TMPD/tampers_raw.txt" || { echo "FAIL — could not construct the damaged dumps"; exit 1; }
tr -d '\r' < "$TMPD/tampers_raw.txt" > "$TMPD/tampers.txt"
"$TMPD/riganim.exe" "$WTMP/missing/jobs.txt" "$WTMP/missing/intact.txt" > "$TMPD/intact_out.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/intact_out.txt"; then head -5 "$TMPD/intact_out.txt"; echo "FAIL — rig_anim drill rejected the intact control (rc=$controlrc)"; exit 1; fi
"$TMPD/riganim.exe" --stages 3 "$WTMP/missing/jobs.txt" "$WTMP/missing/intact.txt" > "$TMPD/intact_s1.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/intact_s1.txt"; then head -5 "$TMPD/intact_s1.txt"; echo "FAIL — rig_anim drill: the stage 1-2 model rejected the intact control (rc=$controlrc)"; exit 1; fi
n_t=0; n_s1t=0; n_s2t=0; n_s3t=0
while IFS= read -r mode; do
  [ -n "$mode" ] || continue
  case "$mode" in
    DONE_missing) reason="no DONE row";;
    DONE_duplicate) reason="exactly one matching DONE row";;
    JOB_duplicate) reason="repeats a job";;
    JOB_unknown) reason="a job the jobs file does not";;
    UNKNOWN_kind) reason="unknown row kind";;
    FAIL_claimed) reason="Blender could not run it";;
    STAGE_gap|STAGE_duplicate) reason="not 1\.\.k in order";;
    EXIT_after_full) reason="a stop after all four stages";;
    EXIT_wrong_stage) reason="other than the one after the last dumped";;
    EXIT_code) reason="exits only with code 1";;
    STOP_missing) reason="no EXIT or DIES row";;
    STOP_valid) reason="stopped at stage 3";;
    EXIT_ran_full) reason="expected to stop";;
    EXIT_marked_wrong_stage) reason="expected to stop at stage 1";;
    K_missing) reason="K rows";;
    K_bits|K_meta) reason="K rows do not match FC hashes";;
    K_orphan) reason="K row names no FC row";;
    LOG_not_riganim) reason="not a RIGANIM line";;
    LOG_misplaced) reason="out of place";;
    SCENE_missing) reason="no SCENE row";;
    SCENE_short|OBJ_short|BONE_short|FC_short|IMG_short) reason="fields, .* expected";;
    OBJ_bits) reason="not float32 bits";;
    OBJ_duplicate) reason="two objects named";;
    MESH_missing) reason="mesh objects, .* MESH rows";;
    MESH_hash) reason="fields, .* expected|not SHA-256";;
    MESH_orphan) reason="names no mesh object";;
    VG_missing) reason="vertex groups listed, .* VG rows";;
    VG_orphan) reason="a group its mesh does not list";;
    BONE_missing|BONE_duplicate) reason="not indexed 0\.\.n-1|two bones named|pose bones";;
    PB_missing) reason="bones, .* pose bones";;
    PB_orphan) reason="names no bone";;
    ACT_missing) reason="an action the stage does not list";;
    FC_missing|FC_duplicate) reason="fcurves, .* FC rows|two FC rows";;
    IMG_missing) reason="no IMG row";;
    IMG_early) reason="not under its STAGE row|IMG row outside stage 4";;
    S1_*) reason="stage 1";;   # a valid dump that says something else about the scene than the stage 1 model: refused in --stages 2 mode
    S2_*) reason="stage 2";;   # the same for a row of stage 2
    S3_*) reason="stage 3";;   # a row of stage 3, on a job the model compares there (jobs_s3.txt)
    *) echo "FAIL — rig_anim drill: the tamper '$mode' has no expected reason here (add it)"; exit 1;;
  esac
  jf="$WTMP/missing/jobs.txt"
  case "$mode" in EXIT_ran_full|EXIT_marked_wrong_stage) jf="$WTMP/missing/jobs_exit.txt";; S3_*) jf="$WTMP/missing/jobs_s3.txt";; esac
  S1_MODE=(); case "$mode" in S1_*) S1_MODE=(--stages 3); n_s1t=$((n_s1t + 1));; S2_*) S1_MODE=(--stages 3); n_s2t=$((n_s2t + 1));; S3_*) S1_MODE=(--stages 3); n_s3t=$((n_s3t + 1));; esac
  "$TMPD/riganim.exe" "${S1_MODE[@]}" "${KEY_MODE[@]}" "$jf" "$WTMP/missing/$mode.txt" > "$TMPD/t_$mode.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/t_$mode.txt" | grep -qE "$reason"; then
    head -3 "$TMPD/t_$mode.txt"; echo "FAIL — rig_anim drill accepted a damaged dump ($mode, rc=$badrc)"; exit 1
  fi
  n_t=$((n_t + 1))
done < "$TMPD/tampers.txt"
[ "$n_s1t" -ge 10 ] || { echo "FAIL — rig_anim drill: only $n_s1t stage 1 tampers were run (missing_riganim.py writes S1_* copies)"; exit 1; }
[ "$n_s2t" -ge 8 ] || { echo "FAIL — rig_anim drill: only $n_s2t stage 2 tampers were run (missing_riganim.py writes S2_* copies)"; exit 1; }
[ "$n_s3t" -ge 6 ] || { echo "FAIL — rig_anim drill: only $n_s3t stage 3 tampers were run (missing_riganim.py writes S3_* copies)"; exit 1; }
echo "PASS — rig_anim drill refuses $n_t kinds of damaged evidence (a missing, doubled, short or misplaced row of every kind; an early stop; an unknown job or row kind; $n_s1t changed values of stage 1 rows, $n_s2t of stage 2 rows and $n_s3t of stage 3 rows, each caught by the model of its stage)"
n_size=$(grep -c "^LEFT	.*	over .* MB" "$TMPD/jobs.txt")
RESULT="PASS — rig_anim drill, step (a) the stage dump: $n_reg of $((n_reg + n_left)) animated recipes and $n_fx fixture jobs run by Blender through rig_anim.py cut at its four stages (Blender took $((t1 - t0)) s), $n_full through all four, $n_stop stopped by the script's own exit as their EXIT: key expects; $n_stages scene dumps with $n_logs RIGANIM lines, $n_bones bones and $n_fc fcurves on record, every block complete and consistent; $n_left recipe(s) left: $n_size over the size limit (FULL=1 takes them), $((n_left - n_size)) by name (Blender-only source until a native reader). Steps (b) to (d), stages 1 to 3 in C# (editor/BlenderRigAnim.cs: the arguments, the import, the material-less drop, the armature and its exit, the contract marker, the object-animation clear; the bone-parent -> skin bind with its rest-world pin, the wrapper flatten; the clip resolution and slicing, the action prune, the bone filter, the legacy translation rules, the strip, the frame range): $n_s1eq of $n_jobs jobs equal to Blender row for row at every compared stage ($n_s1rows rows of objects, meshes, weights, bones, pose, actions and fcurves, as bits and hashes, $n_s2eq stage 2 and $n_s3eq stage 3 scenes among them; $((t3 - t2)) s), $n_s1left left by name, $n_s3def deferred at stage 3 by name (the rest fold and rebake, the bone cap: step (d2), their stages 1 and 2 equal). Stage 4 is not compared yet"
echo "$RESULT"
