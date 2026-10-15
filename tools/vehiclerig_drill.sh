#!/usr/bin/env bash
# vehiclerig_drill.sh - vehicle_rig.py held stage by stage, as Blender runs it, on the Vehicle Lab's saved recipes (step (a)
# of replacing it with C#, docs/Review-Backlog.md item (5); 2026-10-14). The script is the oracle: Blender runs ITS OWN
# SOURCE on every recipe's exact arguments (tools/vehiclerig-drill/VehicleRigJobs.cs, from the same builder the Lab uses),
# cut into five stages at four of its own top-level lines (tools/vehiclerig-drill/blender_vehiclerig_dump.py), the scene
# dumped after each (tools/riganim-drill/scene_rows.py: every object's matrices, every mesh's positions and weights hashed,
# every bone's rest and pose, every action's fcurves hashed) and the exported GLB read back (tools/deploy-drill/glb_rows.py).
# The C# side (VehicleRigDrill.cs) holds the EVIDENCE: every recipe's dump complete and consistent, every stage announced
# once, every row shaped and cross-referenced, a run to its end with its GLB rows, a stop named with the script's own
# exit; the census (COVER rows) says which branches the recipes take - a branch at zero is a fixture to write before its
# port. Damaged copies of the real dump (missing_vehiclerig.py) must each be refused.
#
# Needs Blender and the ENCReload project's recipes (HAF_UNITY_PROJECT, default C:/Repo/ENCReload): SKIP (exit 3) without
# them. The default sample is the recipes whose source is under 40 MB (VEHICLERIG_MAX_MB); FULL=1 takes them all.
# VEHICLERIG_JOBS (default 4) Blender processes run the shards; VEHICLERIG_KEYS=1 adds every key as a K row.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"
API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then
  for c in "/c/Program Files/Blender Foundation/Blender 5.1/blender.exe" "/c/Program Files/Blender Foundation/Blender 4.2/blender.exe"; do [ -f "$c" ] && { BLENDER="$c"; break; }; done
fi
[ -n "$BLENDER" ] && [ -f "$BLENDER" ] || { echo "SKIP — vehicle_rig drill: Blender not found (set BLENDER); vehicle_rig was NOT drilled"; exit 3; }
[ -f "$CSC" ] || { echo "FAIL — Unity compiler not found: $CSC (set UNITY)"; exit 2; }
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH"; exit 2; }
SCRIPT="$ROOT/editor/Tools~/vehicle_rig.py"
[ -f "$SCRIPT" ] || { echo "FAIL — vehicle_rig drill: $SCRIPT is missing"; exit 1; }
RECIPES="$PROJECT/Assets/FactorySource/VehicleLab/Recipes"
[ -d "$RECIPES" ] || { echo "SKIP — vehicle_rig drill: no saved recipes at $RECIPES (set HAF_UNITY_PROJECT); vehicle_rig was NOT drilled"; exit 3; }
NEWTONSOFT="$ROOT/References/Newtonsoft.Json.dll"
[ -f "$NEWTONSOFT" ] || NEWTONSOFT="$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"
[ -f "$NEWTONSOFT" ] || { echo "FAIL — vehicle_rig drill: Newtonsoft.Json.dll not found (References/ or the project's Plugins)"; exit 2; }
# a SHORT temp root: a recipe's names files sit under <work>/<key>/<key>_<role>.txt, and Windows stops at 260 characters
TMPD="$(mktemp -d "${TMPDIR:-/tmp}/vr.XXXXXX")"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
E="$WROOT/editor"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -optimize+ -out:"$WTMP/VehicleRigJobs.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WAPI/System.Numerics.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/vehiclerig-drill/VehicleRigJobs.cs" "$E/VehicleRigArgs.cs" "$E/EditorRules.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/VehicleRigJobs.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the vehicle_rig jobs writer did not compile (csc rc=$rc)"; exit 1; fi
# the drill with the stage models (editor/BlenderVehicleRig.cs) and the editor files they are built from - the rig_anim drill's list
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -optimize+ -out:"$WTMP/vehiclerig.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WAPI/System.Numerics.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/vehiclerig-drill/VehicleRigDrill.cs" "$E/BlenderVehicleRig.cs" "$E/BlenderVehicleRig.Stage2.cs" "$E/BlenderWeld.cs" "$E/VehicleRigArgs.cs" "$E/EditorRules.cs" \
  "$E/BlenderRigAnim.cs" "$E/BlenderRigExport.cs" "$E/BlenderRigAnimInProcess.cs" "$E/BlenderDeploy.cs" "$E/BlenderDeployExport.cs" "$E/BlenderMaterialExport.cs" "$E/BlenderChannelExport.cs" "$E/BlenderGlbWriter.cs" "$E/PyFloatRepr.cs" "$E/BlenderFCurve.cs" \
  "$E/BlenderExport.cs" "$E/BlenderReduce.cs" "$E/BlenderDecimate.cs" "$E/BlenderExportTree.cs" \
  "$E/HafModel.cs" "$E/GlbReader.cs" "$E/HafTransforms.cs" "$E/BlenderNames.cs" "$E/BlenderPosedState.cs" "$E/BlenderTrig.cs" "$E/BlenderEigen.cs" "$E/BlenderMesh.cs" "$E/BMesh.cs" "$E/BlenderColor.cs" \
  "$E/VehicleProbe.cs" "$E/VehicleProbe.Visibility.cs" "$E/VehicleProbe.Islands.cs" "$E/VehicleProbe.InsideOut.cs" \
  "$E/VehicleProbe.BlenderWorld.cs" "$E/VehicleProbe.BlenderSkin.cs" "$E/VehicleProbe.CustomNormals.cs" "$E/VehicleProbe.Merge.cs" "$E/VehicleProbe.BlenderArmature.cs" "$E/VehicleProbe.BlenderPose.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/vehiclerig.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the vehicle_rig drill did not compile (csc rc=$rc)"; exit 1; fi

MAXMB="${VEHICLERIG_MAX_MB:-40}"
ALL=(); [ "${FULL:-0}" != "1" ] || ALL=(--all)
"$TMPD/VehicleRigJobs.exe" "$(cygpath -m "$RECIPES")" "$WTMP/work" --max-mb "$MAXMB" "${ALL[@]}" > "$TMPD/jobs_raw.txt"; jrc=$?
tr -d '\r' < "$TMPD/jobs_raw.txt" > "$TMPD/jobs.txt"
grep -E '^(INVALID|UNAVAILABLE)[[:space:]]' "$TMPD/jobs.txt" || true
if [ "$jrc" -eq 1 ]; then echo "FAIL — vehicle_rig drill: a saved recipe cannot be read (INVALID above)"; exit 1; fi
if [ "$jrc" -eq 3 ]; then echo "SKIP — vehicle_rig drill: a recipe's source model is not on this machine (UNAVAILABLE above); vehicle_rig was NOT drilled"; exit 3; fi
n_reg=$(grep -cE "^(JOB|FAST)	" "$TMPD/jobs.txt"); n_left=$(grep -c "^LEFT	" "$TMPD/jobs.txt")
[ "$n_reg" -gt 0 ] || { echo "SKIP — vehicle_rig drill: no recipe with a readable source under $RECIPES; vehicle_rig was NOT drilled"; exit 3; }
grep "^LEFT	" "$TMPD/jobs.txt" | sed 's/^LEFT\t/LEFT /; s/\t/: /'
# the fixtures: the branches no saved recipe takes (vehiclerig_fixtures.py), recipes the jobs writer turns into jobs like the saved ones
python "$ROOT/tools/vehiclerig-drill/vehiclerig_fixtures.py" "$WTMP/fixtures" > "$TMPD/fixtures_raw.txt" || { echo "FAIL — could not write the vehicle_rig fixtures"; exit 1; }
"$TMPD/VehicleRigJobs.exe" "$WTMP/fixtures/recipes" "$WTMP/fxwork" --all > "$TMPD/fxjobs_raw.txt"; fjrc=$?
tr -d '\r' < "$TMPD/fxjobs_raw.txt" >> "$TMPD/jobs.txt"
[ "$fjrc" -eq 0 ] || { grep -E '^(INVALID|UNAVAILABLE)' "$TMPD/fxjobs_raw.txt"; echo "FAIL — vehicle_rig drill: a fixture recipe did not become a job (rc=$fjrc)"; exit 1; }
# the exit and death fixtures: recipes the writer turns into jobs, then post-edited (vehiclerig_fixtures.py --exits) into the malformed
# tagged tokens and dials the Lab's builder never writes, their keys EXIT:1: / DIES:1: naming what the script must do with them
"$TMPD/VehicleRigJobs.exe" "$WTMP/fixtures/recipes_exit" "$WTMP/fxwork" --all > "$TMPD/fxexit_raw.txt"; ferc=$?
[ "$ferc" -eq 0 ] || { grep -E '^(INVALID|UNAVAILABLE)' "$TMPD/fxexit_raw.txt"; echo "FAIL — vehicle_rig drill: an exit fixture recipe did not become a job (rc=$ferc)"; exit 1; }
tr -d '\r' < "$TMPD/fxexit_raw.txt" > "$TMPD/fxexit_jobs_in.txt"
python "$ROOT/tools/vehiclerig-drill/vehiclerig_fixtures.py" --exits "$TMPD/fxexit_jobs_in.txt" > "$TMPD/fxexit_jobs.txt" || { echo "FAIL — could not derive the vehicle_rig exit fixtures"; exit 1; }
cat "$TMPD/fxexit_jobs.txt" >> "$TMPD/jobs.txt"
n_fx=$(( $(grep -cE "^(JOB|FAST)	" "$TMPD/fxjobs_raw.txt") + $(grep -cE "^JOB	" "$TMPD/fxexit_jobs.txt") )); n_jobs=$((n_reg + n_fx)); n_fast=$(grep -c "^FAST	" "$TMPD/jobs.txt")
[ "$n_fx" -gt 0 ] || { echo "FAIL — the vehicle_rig fixtures gave no job"; exit 1; }
[ "$(grep -cE "^JOB	(EXIT|DIES):" "$TMPD/fxexit_jobs.txt")" -ge 8 ] || { echo "FAIL — the vehicle_rig exit fixtures gave fewer than 8 EXIT:/DIES: jobs"; exit 1; }

# Blender: the shards, the largest sources first, round robin
JOBS="${VEHICLERIG_JOBS:-4}"; [ "$JOBS" -ge 1 ] 2>/dev/null || JOBS=1
[ "$JOBS" -le "$n_jobs" ] || JOBS="$n_jobs"
python - "$TMPD/jobs.txt" "$TMPD" "$JOBS" <<'PYEOF'
import os, sys
jobs = [l for l in open(sys.argv[1], encoding="utf-8").read().split("\n") if l.startswith("JOB\t") or l.startswith("FAST\t")]
jobs.sort(key=lambda l: -os.path.getsize(l.split("\t")[3]))
n = int(sys.argv[3])
for k in range(n):
    open(os.path.join(sys.argv[2], "shard_%d.txt" % k), "w", encoding="utf-8", newline="\n").write("\n".join(jobs[k::n]) + "\n")
PYEOF
t0=$(date +%s)
BPIDS=()
for ((k = 0; k < JOBS; k++)); do
  mkdir -p "$TMPD/work_$k"
  "$BLENDER" --background --python "$(cygpath -m "$ROOT/tools/vehiclerig-drill/blender_vehiclerig_dump.py")" -- "$(cygpath -m "$SCRIPT")" "$WTMP/shard_$k.txt" "$WTMP/work_$k" > "$TMPD/blender_raw_$k.txt" 2>&1 &
  BPIDS+=($!)
done
brc=0; for pid in "${BPIDS[@]}"; do wait "$pid" || brc=$?; done
t1=$(date +%s)
n_b=0
for ((k = 0; k < JOBS; k++)); do
  tr -d '\r' < "$TMPD/blender_raw_$k.txt" | grep -E "^(JOB|STAGE|LOG|SCENE|OBJ|MESH|VG|BONE|PB|ACT|FC|K|EXIT|DIES|DONE|FAIL|X|XDIES|EVIDENCE)	" > "$TMPD/dump_$k.txt"
  n_b=$((n_b + $(grep -c "^DONE" "$TMPD/dump_$k.txt")))
done
if [ "$brc" -ne 0 ] || [ "$n_b" -ne "$n_jobs" ]; then
  cat "$TMPD"/blender_raw_*.txt | grep -E "^FAIL|Traceback|Error" | head -8
  echo "FAIL — vehicle_rig drill: Blender dumped $n_b of $n_jobs jobs (exit $brc)"; exit 1
fi
DUMPS=(); for ((k = 0; k < JOBS; k++)); do DUMPS+=("$WTMP/dump_$k.txt"); done
KEY_MODE=(); [ "${VEHICLERIG_KEYS:-0}" != "1" ] || KEY_MODE=(--keys)
"$TMPD/vehiclerig.exe" "${KEY_MODE[@]}" "$WTMP/jobs.txt" "${DUMPS[@]}" > "$TMPD/cs_raw.txt" 2>&1; rc=$?
t2=$(date +%s)
tr -d '\r' < "$TMPD/cs_raw.txt" > "$TMPD/cs.txt"
grep -E "^FAIL|^COVER" "$TMPD/cs.txt"
TOTAL=$(grep -E "^TOTAL" "$TMPD/cs.txt" | tail -1); echo "$TOTAL"
[ -n "$TOTAL" ] || { tail -5 "$TMPD/cs.txt"; echo "FAIL — vehicle_rig drill: the C# side gave no total (rc=$rc)"; exit 1; }
[ "$rc" -eq 0 ] || { echo "FAIL — vehicle_rig drill: the stage dump is incomplete or inconsistent"; exit 1; }
n_full=$(echo "$TOTAL" | awk '{print $5}'); n_fastrun=$(echo "$TOTAL" | awk '{print $7}'); n_stop=$(echo "$TOTAL" | awk '{print $9}')
n_stages=$(echo "$TOTAL" | awk '{print $13}'); n_logs=$(echo "$TOTAL" | awk '{print $15}'); n_bones=$(echo "$TOTAL" | awk '{print $23}'); n_fc=$(echo "$TOTAL" | awk '{print $25}'); n_glb=$(echo "$TOTAL" | awk '{print $29}')
n_exit=$(grep -cE "^JOB	(EXIT|DIES):" "$TMPD/jobs.txt")
[ "$n_full" -eq $((n_jobs - n_fast - n_exit)) ] && [ "$n_fastrun" -eq "$n_fast" ] && [ "$n_stop" -eq "$n_exit" ] || { echo "FAIL — vehicle_rig drill: not every recipe completed (full $n_full of $((n_jobs - n_fast - n_exit)), fast $n_fastrun of $n_fast, stopped $n_stop of $n_exit)"; exit 1; }
# STEPS (b) and (c), STAGES 1 and 2: the C# model of the first two stages (editor/BlenderVehicleRig.cs, .Stage2.cs) against every
# job's rows of those stages, as strings - the dump's own float32 bits and hashes. Every dumped job must be equal row for row,
# or left by name (a source the C# does not read: .fbx/.obj/.blend, the source-skeleton fast path; the dissolve pass of the
# RIGGING and SHROUD tiers until it is ported); any other decline is a FAIL.
"$TMPD/vehiclerig.exe" --stages 2 "${KEY_MODE[@]}" "$WTMP/jobs.txt" "${DUMPS[@]}" > "$TMPD/s1_raw.txt" 2>&1; s1rc=$?
t3=$(date +%s)
tr -d '\r' < "$TMPD/s1_raw.txt" > "$TMPD/s1.txt"
grep -E "^FAIL|^MODEL" "$TMPD/s1.txt"
S1=$(grep -E "^STAGES 2 jobs" "$TMPD/s1.txt" | tail -1); echo "$S1"
[ -n "$S1" ] || { tail -5 "$TMPD/s1.txt"; echo "FAIL — vehicle_rig drill: the stage models gave no total (rc=$s1rc)"; exit 1; }
[ "$s1rc" -eq 0 ] || { echo "FAIL — vehicle_rig drill: a stage model differs from Blender, or leaves a dumped job to it"; exit 1; }
n_s1=$(echo "$S1" | awk '{print $4}'); n_s1eq=$(echo "$S1" | awk '{print $6}'); n_s1left=$(echo "$S1" | awk '{print $8}'); n_s1rows=$(echo "$S1" | awk '{print $10}')
[ "$n_s1" -eq "$n_jobs" ] && [ $((n_s1eq + n_s1left)) -eq "$n_jobs" ] || { echo "FAIL — vehicle_rig drill: the stage models compared $n_s1 of $n_jobs jobs ($n_s1eq equal, $n_s1left left)"; exit 1; }
n_s2left=$(grep -c "^STAGE2 .*left to Blender by name" "$TMPD/s1.txt")
# Negative guards on the REAL dump: a missing or inconsistent record must never pass as evidence.
python "$ROOT/tools/vehiclerig-drill/missing_vehiclerig.py" "$WTMP/missing" "$WTMP/jobs.txt" "${DUMPS[@]}" > "$TMPD/tampers_raw.txt" || { echo "FAIL — could not construct the damaged dumps"; exit 1; }
tr -d '\r' < "$TMPD/tampers_raw.txt" > "$TMPD/tampers.txt"
"$TMPD/vehiclerig.exe" "$WTMP/missing/jobs.txt" "$WTMP/missing/intact.txt" > "$TMPD/intact_out.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/intact_out.txt"; then head -5 "$TMPD/intact_out.txt"; echo "FAIL — vehicle_rig drill rejected the intact control (rc=$controlrc)"; exit 1; fi
n_t=0; n_s1t=0; n_s2t=0
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
    EXIT_after_full) reason="a stop after all five stages";;
    EXIT_wrong_stage) reason="other than the one after the last dumped";;
    EXIT_code) reason="exits with 0 .* or 1";;
    EXIT_zero_not_fast) reason="an exit 0 belongs to the fast path alone";;
    STOP_missing) reason="no EXIT or DIES row";;
    STOP_valid) reason="stopped at stage 3";;
    STOP_with_export) reason="export rows on a run that stopped";;
    EXIT_ran_full) reason="expected to stop";;
    EXIT_marked_wrong_stage) reason="expected to stop at stage 1";;
    FAST_mode_mismatch) reason="a FAST line must run rigfast";;
    LOG_not_vehicle) reason="not a VEHICLE line";;
    LOG_timing) reason="a timing line";;
    LOG_misplaced) reason="out of place";;
    SCENE_missing) reason="no SCENE row";;
    SCENE_short|OBJ_short|BONE_short|FC_short) reason="fields, .* expected";;
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
    X_missing|X_no_glb_row) reason="no GLB rows for a run that completed";;
    X_unknown_kind) reason="unknown row kind";;
    X_after_done) reason="follows DONE";;
    X_died_and_rows) reason="an X row after the export died|both died and has GLB rows";;
    S1_*) reason="stage 1 (model|LOG|SCENE|OBJ|MESH|ACT|FC)";;   # a valid dump that says something else about the scene than the stage 1 model: refused BY THE MODEL in --stages 1 mode
    S2_*) reason="stage 2 (model|LOG|SCENE|OBJ|MESH|ACT|FC)";;   # likewise for stage 2, in --stages 2 mode
    K_missing) reason="K rows";;
    K_bits|K_meta) reason="K rows do not match FC hashes";;
    K_orphan) reason="K row names no FC row";;
    EVIDENCE_missing) reason="matching EVIDENCE row";;
    EVIDENCE_count|EVIDENCE_hash|LOG_lost|OBJ_changed) reason="evidence row count or checksum";;
    X_short_PRIM) reason="the export:|evidence row count or checksum";;
    X_header_only|X_short_*|X_duplicate_*) reason="the export:|export (NODE|IBM|CHAN)|JsonReaderException";;
    *) echo "FAIL — vehicle_rig drill: the tamper '$mode' has no expected reason here (add it)"; exit 1;;
  esac
  jf="$WTMP/missing/jobs.txt"
  case "$mode" in EXIT_ran_full|EXIT_marked_wrong_stage) jf="$WTMP/missing/jobs_exit.txt";; FAST_mode_mismatch) jf="$WTMP/missing/jobs_fast.txt";; esac
  S1_MODE=(); case "$mode" in S1_*) S1_MODE=(--stages 1); n_s1t=$((n_s1t + 1));; S2_*) S1_MODE=(--stages 2); n_s2t=$((n_s2t + 1));; esac
  "$TMPD/vehiclerig.exe" "${S1_MODE[@]}" "${KEY_MODE[@]}" "$jf" "$WTMP/missing/$mode.txt" > "$TMPD/t_$mode.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/t_$mode.txt" | grep -qE "$reason"; then
    head -3 "$TMPD/t_$mode.txt"; echo "FAIL — vehicle_rig drill accepted a damaged dump ($mode, rc=$badrc)"; exit 1
  fi
  n_t=$((n_t + 1))
done < "$TMPD/tampers.txt"
[ "$n_t" -ge 40 ] || { echo "FAIL — vehicle_rig drill: only $n_t tampers were run (missing_vehiclerig.py writes more)"; exit 1; }
[ "$n_s1t" -ge 10 ] || { echo "FAIL — vehicle_rig drill: only $n_s1t stage 1 tampers were run (missing_vehiclerig.py writes S1_* copies)"; exit 1; }
[ "$n_s2t" -ge 6 ] || { echo "FAIL — vehicle_rig drill: only $n_s2t stage 2 tampers were run (missing_vehiclerig.py writes S2_* copies)"; exit 1; }
echo "PASS — vehicle_rig drill refuses $n_t kinds of damaged evidence (a missing, doubled, short or misplaced row of every kind; an early stop; a stop with export rows; a completed run without its GLB rows; an unknown job, row kind or mode; $n_s1t changed values of stage 1 rows and $n_s2t of stage 2 rows, each caught by the model of that stage)"
n_size=$(grep -c "^LEFT	.*	over .* MB" "$TMPD/jobs.txt")
echo "PASS — vehicle_rig drill, step (a) the stage dump: $n_reg of $((n_reg + n_left)) saved recipes and $n_fx fixture jobs run by Blender through vehicle_rig.py cut at its five stages (Blender took $((t1 - t0)) s, $JOBS at a time), $n_full through all five and $n_fastrun on the source-skeleton fast path, $n_stop stopped by the script's own exit; $n_stages scene dumps with $n_logs VEHICLE lines, $n_bones bones and $n_fc fcurves on record and $n_glb rows of the exported GLBs read back, every block complete and consistent; $n_left recipe(s) left: $n_size over the size limit (FULL=1 takes them). Steps (b) and (c), stages 1 and 2 in C# (editor/BlenderVehicleRig.cs: the import, the bone-shape purge, the brightness line, the second-model merge, the flatten, the loose split, the placements, the ignored parts, the orientation, transform_apply; BlenderVehicleRig.Stage2.cs + BlenderWeld.cs: the reduction tiers - the weld within each material, the sharp edges over 40 degrees, the dial's target and the decimate): $n_s1eq of $n_jobs jobs equal to Blender row for row after both stages ($n_s1rows rows of objects, meshes and actions, as bits and hashes; $((t3 - t2)) s), $n_s1left left by name ($n_s2left at stage 2: the dissolve pass of a RIGGING or SHROUD tier, not ported yet; the rest a source the C# does not read, or the rigfast fast path)"
