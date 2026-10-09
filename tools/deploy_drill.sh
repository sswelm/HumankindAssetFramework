#!/usr/bin/env bash
# deploy_drill.sh - replacing deploy_convert.py (2026-10-09), held to Blender part by part.
# PART 1, THE POSED STATE: everything deploy_convert.py decides and bakes starts from what `scene.frame_set(f)` makes of
# the imported file - every object's matrix_world at a frame. Blender dumps that for a sample of files at thirteen
# frames each (tools/deploy-drill/blender_posed_dump.py: before the first key, on it, after it, eight across the range,
# the last, beyond it); the C# side rebuilds the importer's action (BlenderPosedState.Import: one fcurve per component,
# frames as time x 24, antipodal quaternions negated, keys sorted and those closer than 0.01 frame merged) and
# Blender's evaluation of it (fcurve_eval_keyframes), composes the matrices as Blender does
# (VehicleProbe.BlenderWorldMatrices) and compares all sixteen floats of each, bit for bit. Also held: the action's
# frame range, and which objects the animation touches (deploy_convert.py takes those for its parts).
# The sample: the fixtures (with posed.glb, the branches no real file reaches) and the DEPLOY SOURCES - the model files
# the project's deploy conversions were made from (Assets/FactorySource/*/deploy_converted.args.txt), those over 40 MB
# only with FULL=1, which also takes every registry model and recipe source.
# A COVER row at zero fails the drill: equal output says nothing about a branch no file took.
# Both sides run as several processes (DEPLOY_JOBS, default 6), as prep_drill.sh does.
# Prerequisites and SKIP rules as prep_drill.sh. Edit this script only while no FULL run is executing it.
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; the deploy conversion was NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (the deploy conversion was NOT drilled)"; exit 2; }
NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; the deploy conversion was NOT drilled)"; exit 2; }
BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then
  for base in "/c/Program Files/Blender Foundation" "/c/Program Files (x86)/Blender Foundation"; do
    cand=$(ls -d "$base"/Blender* 2>/dev/null | sort -V | tail -1); [ -n "$cand" ] && [ -x "$cand/blender.exe" ] && { BLENDER="$cand/blender.exe"; break; }
  done
fi
[ -n "$BLENDER" ] || { echo "SKIP — deploy drill: Blender not found (BLENDER=<exe> to force); the deploy conversion was NOT drilled"; exit 0; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
E="$WROOT/editor"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -optimize+ -out:"$WTMP/deploy.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/deploy-drill/DeployDrill.cs" "$WROOT/tools/deploy-drill/DecisionsDrill.cs" "$E/BlenderDeploy.cs" "$E/BlenderExportTree.cs" \
  "$E/HafModel.cs" "$E/GlbReader.cs" "$E/HafTransforms.cs" "$E/BlenderNames.cs" "$E/BlenderPosedState.cs" "$E/BlenderTrig.cs" "$E/BlenderEigen.cs" "$E/BlenderMesh.cs" "$E/BMesh.cs" "$E/BlenderColor.cs" \
  "$E/VehicleProbe.cs" "$E/VehicleProbe.Visibility.cs" "$E/VehicleProbe.Islands.cs" "$E/VehicleProbe.InsideOut.cs" \
  "$E/VehicleProbe.BlenderWorld.cs" "$E/VehicleProbe.BlenderSkin.cs" "$E/VehicleProbe.CustomNormals.cs" "$E/VehicleProbe.Merge.cs" "$E/VehicleProbe.BlenderArmature.cs" "$E/VehicleProbe.BlenderPose.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/deploy.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the deploy drill did not compile (csc rc=$rc)"; exit 1; fi

mapfile -t FIXTURES < <(python "$ROOT/tools/glb-reader-drill/fixtures.py" "$WTMP/fixtures" --posed | tr -d '\r')
[ "${#FIXTURES[@]}" -gt 0 ] || { echo "FAIL — could not write the fixtures"; exit 1; }
PACK=$(ls "$PROJECT"/Assets/Pack/*/pack.json 2>/dev/null | head -1)
REGISTRY=(); RECIPE_SOURCES=(); DEPLOY_SOURCES=()
if [ -n "$PACK" ]; then
  mapfile -t REGISTRY < <(python "$ROOT/tools/glb-reader-drill/registry_files.py" "$PACK" | tr -d '\r' | awk '!seen[tolower($0)]++')
  mapfile -t RECIPE_SOURCES < <(python "$ROOT/tools/vehicle-probe-drill/recipe_check.py" --list "$PROJECT" | tr -d '\r')
  # the model files the project's deploy conversions were made from: the first field of each recorded argument line
  mapfile -t DEPLOY_SOURCES < <(for a in "$PROJECT"/Assets/FactorySource/*/deploy_converted.args.txt; do [ -f "$a" ] && head -1 "$a" | cut -d'|' -f1; done | tr -d '\r' | awk '!seen[tolower($0)]++' | while IFS= read -r f; do [ -f "$f" ] && printf '%s\n' "$f"; done)
fi
NOTE_SOURCES="${#DEPLOY_SOURCES[@]} deploy sources"; [ -n "$PACK" ] || NOTE_SOURCES="NO modding project at $PROJECT (set HAF_UNITY_PROJECT): the fixtures alone"
if [ "${FULL:-0}" = "1" ]; then
  mapfile -t SAMPLE < <( { printf '%s\n' "${FIXTURES[@]}"; [ "${#DEPLOY_SOURCES[@]}" -eq 0 ] || printf '%s\n' "${DEPLOY_SOURCES[@]}"; [ "${#REGISTRY[@]}" -eq 0 ] || printf '%s\n' "${REGISTRY[@]}"; [ "${#RECIPE_SOURCES[@]}" -eq 0 ] || printf '%s\n' "${RECIPE_SOURCES[@]}"; } | grep -iE '\.(glb|gltf)$' | awk '!seen[tolower($0)]++' )
else
  mapfile -t SAMPLE < <( { printf '%s\n' "${FIXTURES[@]}" | grep -v '/big\.glb$'; [ "${#DEPLOY_SOURCES[@]}" -eq 0 ] || find "${DEPLOY_SOURCES[@]}" -size -40M; } | grep -iE '\.(glb|gltf)$' | awk '!seen[tolower($0)]++' )
fi

JOBS="${DEPLOY_JOBS:-6}"; [ "$JOBS" -ge 1 ] 2>/dev/null || JOBS=1
[ "$JOBS" -le "${#SAMPLE[@]}" ] || JOBS="${#SAMPLE[@]}"
mapfile -t BYSIZE < <(ls -S "${SAMPLE[@]}")
[ "${#BYSIZE[@]}" -eq "${#SAMPLE[@]}" ] || { echo "FAIL — deploy drill: ${#BYSIZE[@]} of ${#SAMPLE[@]} sample files could be listed"; exit 1; }
for ((k = 0; k < JOBS; k++)); do : > "$TMPD/shard_$k.txt"; done
for ((i = 0; i < ${#BYSIZE[@]}; i++)); do printf '%s\n' "${BYSIZE[$i]}" >> "$TMPD/shard_$((i % JOBS)).txt"; done
t0=$(date +%s)
BPIDS=()
for ((k = 0; k < JOBS; k++)); do
  mapfile -t SHARD < "$TMPD/shard_$k.txt"
  "$BLENDER" --background --python "$(cygpath -m "$ROOT/tools/deploy-drill/blender_posed_dump.py")" -- "${SHARD[@]}" > "$TMPD/blender_raw_$k.txt" 2>&1 &
  BPIDS+=($!)
done
brc=0; for pid in "${BPIDS[@]}"; do wait "$pid" || brc=$?; done
t1=$(date +%s)
n_b=0
for ((k = 0; k < JOBS; k++)); do
  tr -d '\r' < "$TMPD/blender_raw_$k.txt" | grep -E "^(FILE|SCENE|ACTION|OBJ|BONE|FRAMES|L|M|PB|DONE|FAIL)	" > "$TMPD/posed_$k.txt"
  n_b=$((n_b + $(grep -c "^DONE" "$TMPD/posed_$k.txt")))
done
if [ "$brc" -ne 0 ] || [ "$n_b" -ne "${#SAMPLE[@]}" ]; then
  cat "$TMPD"/blender_raw_*.txt | grep -E "^FAIL|Traceback|Error" | head -8
  echo "FAIL — deploy drill: Blender dumped $n_b of ${#SAMPLE[@]} files (exit $brc)"; exit 1
fi
CPIDS=()
for ((k = 0; k < JOBS; k++)); do
  "$TMPD/deploy.exe" "$WTMP/posed_$k.txt" > "$TMPD/csharp_raw_$k.txt" 2>&1 &
  CPIDS+=($!)
done
rc=0; for pid in "${CPIDS[@]}"; do wait "$pid" || rc=$?; done
t2=$(date +%s)
SHARDS=(); for ((k = 0; k < JOBS; k++)); do SHARDS+=("$WTMP/csharp_raw_$k.txt"); done
python "$ROOT/tools/prep-drill/merge_shards.py" "${SHARDS[@]}" > "$TMPD/csharp_merged.txt"; mergerc=$?
tr -d '\r' < "$TMPD/csharp_merged.txt" > "$TMPD/csharp.txt"
[ "$mergerc" -eq 0 ] || rc=1
grep -E "^FAIL|^LEFT|^NOTE|^COVER" "$TMPD/csharp.txt"
TOTAL=$(grep -E "^TOTAL" "$TMPD/csharp.txt" | tail -1)
echo "$TOTAL"
[ -n "$TOTAL" ] || { tail -5 "$TMPD/csharp.txt"; echo "FAIL — deploy drill: the C# side gave no total (rc=$rc)"; exit 1; }
[ "$rc" -eq 0 ] || { echo "FAIL — deploy drill: the posed state here differs from Blender's"; exit 1; }
n_files=$(echo "$TOTAL" | awk '{print $3}'); n_mat=$(echo "$TOTAL" | awk '{print $7}'); n_left=$(echo "$TOTAL" | awk '{print $11}')
[ "$n_mat" -gt 0 ] || { echo "FAIL — deploy drill: no matrix was compared at all"; exit 1; }
UNCOVERED=$(grep -E "^COVER 0 " "$TMPD/csharp.txt" | cut -d' ' -f3- | paste -sd';' -)
[ -z "$UNCOVERED" ] || { echo "FAIL — deploy drill: no compared file exercised: $UNCOVERED (a branch Blender did not judge; add it to fx_posed in tools/glb-reader-drill/fixtures.py)"; exit 1; }
# Negative guards use the REAL Blender dump: omitted evidence and an empty frame list must not pass as equality.
DUMPS=(); for ((k = 0; k < JOBS; k++)); do DUMPS+=("$WTMP/posed_$k.txt"); done
python "$ROOT/tools/deploy-drill/missing_rows.py" "$WTMP/missing_rows" "${DUMPS[@]}" || { echo "FAIL — could not construct missing posed-record regressions"; exit 1; }
for mode in matrix property all_properties frames short_matrix short_property; do
  "$TMPD/deploy.exe" "$WTMP/missing_rows/$mode.txt" > "$TMPD/missing_$mode.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep -qE "^FAIL .*InvalidDataException: dump (is missing|has no frames|matrix row|property row)" "$TMPD/missing_$mode.txt"; then
    cat "$TMPD/missing_$mode.txt"
    echo "FAIL — deploy drill accepted missing or truncated posed evidence ($mode, rc=$badrc)"; exit 1
  fi
done
echo "PASS — deploy drill rejects missing matrices, one or all properties, empty frame lists, and truncated records"
python "$ROOT/tools/deploy-drill/missing_bones.py" "$WTMP/missing_bones" "${DUMPS[@]}" || { echo "FAIL — could not construct bone-record regressions"; exit 1; }
"$TMPD/deploy.exe" "$WTMP/missing_bones/intact.txt" > "$TMPD/bones_control.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/bones_control.txt"; then
  cat "$TMPD/bones_control.txt" | head -5
  echo "FAIL — deploy drill rejected the intact bone-record control (rc=$controlrc)"; exit 1
fi
for mode in missing_bone duplicate_bone short_bone long_bone missing_pose duplicate_pose short_pose long_pose unknown_pose all_poses; do
  case "$mode" in
    missing_bone) reason="dump is missing bone row";;
    duplicate_bone) reason="dump has a duplicate bone row";;
    short_bone|long_bone) reason="dump bone row has";;
    missing_pose|all_poses) reason="dump is missing pose row";;
    duplicate_pose) reason="dump has a duplicate pose row";;
    short_pose|long_pose) reason="dump pose row has";;
    unknown_pose) reason="dump has an unknown pose row";;
  esac
  "$TMPD/deploy.exe" "$WTMP/missing_bones/$mode.txt" > "$TMPD/missing_bones_$mode.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL .*InvalidDataException:" "$TMPD/missing_bones_$mode.txt" | grep -qF "$reason"; then
    cat "$TMPD/missing_bones_$mode.txt" | head -5
    echo "FAIL — deploy drill accepted invalid bone evidence ($mode, rc=$badrc)"; exit 1
  fi
done
echo "PASS — deploy drill accepts intact bone evidence and rejects missing, duplicate, truncated, oversized or unknown bone and pose records"

n_prop=$(echo "$TOTAL" | awk '{print $13}')
n_pose=$(echo "$TOTAL" | awk '{print $15}')
echo "PASS — deploy drill, the posed state: $n_mat object matrices of $n_files files, at up to thirteen frames each, equal to Blender's bit for bit - the sign of a zero included - with $n_prop evaluated location/rotation/scale sets and $n_pose pose bones (location, rotation, scale, pose matrix); $n_left files left to Blender by name (Blender took $((t1 - t0)) s, the comparison $((t2 - t1)) s, in $JOBS processes each); ${#FIXTURES[@]} fixtures, $NOTE_SOURCES"

# PARTS 2 AND 3, THE DECISIONS AND THE ARMATURE (2026-10-10): what deploy_convert.py decides and builds before it BAKES
# (the dump runs the script up to `bpy.ops.nla.bake(`, shows the scene, and runs it on to its `# --- 5a.` for the
# baked action - PART 4, 2026-10-11): what it decides before it builds anything - the strip, the frame
# range, the unit normalization, the parts, the bone slimming, the path, the degenerate cull, the bone budget.
# tools/deploy-drill/blender_decisions_dump.py runs THE SCRIPT ITSELF, cut where it starts on the armature, for every
# job: the project's recorded conversions (Assets/FactorySource/*/deploy_converted.args.txt - the source and the
# arguments the Factory gave) and the fixtures of tools/deploy-drill/deploy_fixtures.py (each branch no recorded job
# takes). BlenderDeploy.Decide is then held to the script's own log lines, its decisions and the scene it left: every
# object's matrix, transform and bound box, as bits. A job the jobs file marks LEFT: must come out left to Blender, and
# no other may - a wrong decision that ends in a fallback is a failure. A recorded job whose source is gone is named.
KNOWN_BAKE_LEFT="DugoutCanoe"   # the bake re-bakes its imported armature, from whose bones ten objects hang: Blender's from the bake on
KNOWN_LEFT=""   # none: the dugout canoe (objects under animated bones) is decided here since the pose is modelled (2b)
: > "$TMPD/jobs.txt"; n_rec=0; MISSING=""
if [ -n "$PACK" ]; then
  for a in "$PROJECT"/Assets/FactorySource/*/deploy_converted.args.txt; do
    [ -f "$a" ] || continue
    key=$(basename "$(dirname "$a")"); line=$(head -1 "$a" | tr -d '\r'); src=${line%%|*}
    if [ ! -f "$src" ]; then MISSING="$MISSING $key"; continue; fi
    rest=$(printf '%s' "$line" | cut -d'|' -f4-)
    case " $KNOWN_LEFT " in *" $key "*) key="LEFT:$key";; esac
    case " $KNOWN_BAKE_LEFT " in *" $key "*) key="BAKELEFT:$key";; esac
    printf '%s|%s|%s\n' "$key" "$src" "$rest" >> "$TMPD/jobs.txt"; n_rec=$((n_rec + 1))
  done
fi
python "$ROOT/tools/deploy-drill/deploy_fixtures.py" "$WTMP/deploy_fixtures" | tr -d '\r' > "$TMPD/jobs_fx.txt" || { echo "FAIL — could not write the deploy fixtures"; exit 1; }
n_fx=$(grep -c "" "$TMPD/jobs_fx.txt"); [ "$n_fx" -gt 0 ] || { echo "FAIL — the deploy fixtures gave no job"; exit 1; }
cat "$TMPD/jobs_fx.txt" >> "$TMPD/jobs.txt"
n_jobs=$(grep -c "" "$TMPD/jobs.txt")
t3=$(date +%s)
"$BLENDER" --background --python "$(cygpath -m "$ROOT/tools/deploy-drill/blender_decisions_dump.py")" -- "$WROOT/editor/Tools~/deploy_convert.py" "$WTMP/jobs.txt" > "$TMPD/decisions_raw.txt" 2> "$TMPD/decisions_err.txt"; drc=$?   # stderr apart: a depsgraph warning lands in the middle of a row otherwise
t4=$(date +%s)
tr -d '\r' < "$TMPD/decisions_raw.txt" | grep -E "^(JOB|LOG|LOG2|EXIT|RANGE|NORM|FLAG|PART|BAD|ALIAS|ARM|BONEOF|RBONE|ANCHOR|HULL|PINV|OBJ|M|T|BOX|ACT|FC|M2|O2|DONE|FAIL)	" > "$TMPD/decisions.txt"
n_done=$(grep -c "^DONE" "$TMPD/decisions.txt")
if [ "$drc" -ne 0 ] || [ "$n_done" -ne "$n_jobs" ]; then
  grep -E "^FAIL|Traceback|Error" "$TMPD/decisions_raw.txt" "$TMPD/decisions_err.txt" | head -8
  echo "FAIL — deploy drill: Blender ran the script for $n_done of $n_jobs jobs (exit $drc)"; exit 1
fi
"$TMPD/deploy.exe" --decisions "$WTMP/jobs.txt" "$WTMP/decisions.txt" > "$TMPD/dec_raw.txt" 2>&1; rc2=$?
tr -d '\r' < "$TMPD/dec_raw.txt" > "$TMPD/dec.txt"
grep -E "^FAIL|^LEFT|^BAKELEFT|^COVER" "$TMPD/dec.txt"
TOTAL2=$(grep -E "^TOTAL" "$TMPD/dec.txt" | tail -1); echo "$TOTAL2"
[ -n "$TOTAL2" ] || { tail -5 "$TMPD/dec.txt"; echo "FAIL — deploy drill: the decisions gave no total (rc=$rc2)"; exit 1; }
[ "$rc2" -eq 0 ] || { echo "FAIL — deploy drill: the decisions here differ from deploy_convert.py's"; exit 1; }
UNCOVERED=$(grep -E "^COVER 0 " "$TMPD/dec.txt" | cut -d' ' -f3- | paste -sd';' -)
[ -z "$UNCOVERED" ] || { echo "FAIL — deploy drill: no compared job exercised: $UNCOVERED (a branch the script did not judge; add it to tools/deploy-drill/deploy_fixtures.py)"; exit 1; }
# negative guards on the REAL dump: evidence taken away must not pass as equality
python "$ROOT/tools/deploy-drill/missing_decisions.py" "$WTMP/missing_dec" "$WTMP/decisions.txt" "$WTMP/jobs.txt" || { echo "FAIL — could not construct the missing-decision regressions"; exit 1; }
# The same one-job lists must accept the intact controls before their mutations are judged.
for control in missing exit; do
  "$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/${control}_jobs.txt" "$WTMP/missing_dec/${control}_intact.txt" > "$TMPD/dec_control_$control.txt" 2>&1; controlrc=$?
  if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/dec_control_$control.txt"; then
    cat "$TMPD/dec_control_$control.txt" | head -5
    echo "FAIL — deploy drill rejected the intact $control control (rc=$controlrc)"; exit 1
  fi
done
for mode in log matrix transform box object part range done bone boneof anchor hull pinv curve key value log2 act index; do
  case "$mode" in
    log) reason="log line";;
    matrix|transform|box) reason="no complete $mode row";;
    object) reason="objects:";;
    part) reason="parts:";;
    range) reason="0 RANGE rows";;
    done) reason="no DONE row";;
    bone) reason="bones:";;
    boneof) reason="bone of each part:";;
    anchor) reason="0 ANCHOR rows";;
    hull) reason="0 HULL rows";;
    pinv) reason="0 PINV rows";;
    curve) reason="curves here";;
    key) reason="keys in Blender";;
    value) reason="baked keys";;
    log2) reason="bake log line";;
    act) reason="no single ACT and M2 row";;
    index) reason="a component its channel does not have";;
  esac
  "$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/missing_jobs.txt" "$WTMP/missing_dec/$mode.txt" > "$TMPD/missing_dec_$mode.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/missing_dec_$mode.txt" | grep -qF "$reason"; then
    cat "$TMPD/missing_dec_$mode.txt" | head -5
    echo "FAIL — deploy drill accepted a dump with a missing $mode row (rc=$badrc)"; exit 1
  fi
done
for mode in exit_zero exit_short exit_duplicate; do
  "$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/exit_jobs.txt" "$WTMP/missing_dec/$mode.txt" > "$TMPD/missing_dec_$mode.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep -qE "^FAIL .*InvalidDataException: the dump has an invalid EXIT row" "$TMPD/missing_dec_$mode.txt"; then
    cat "$TMPD/missing_dec_$mode.txt" | head -5
    echo "FAIL — deploy drill accepted an invalid abort record ($mode, rc=$badrc)"; exit 1
  fi
done
echo "PASS — deploy drill rejects a decisions dump without a log line, a matrix, a transform, a box, an object, a part, the range, the end row, a bone, a part's bone, the anchors, the parent inverse, a baked curve, a key, the bake's log line or the action row - and one with a baked value changed by one bit or a curve under a component its channel does not have"
echo "PASS — deploy drill accepts intact single-job controls and rejects a successful, truncated or duplicated abort record"
n_j=$(echo "$TOTAL2" | awk '{print $3}'); n_l=$(echo "$TOTAL2" | awk '{print $7}'); n_o=$(echo "$TOTAL2" | awk '{print $9}'); n_ln=$(echo "$TOTAL2" | awk '{print $13}'); n_bones=$(echo "$TOTAL2" | awk '{print $15}'); n_keys=$(echo "$TOTAL2" | awk '{print $19}'); n_after=$(echo "$TOTAL2" | awk '{print $21}'); n_bl=$(grep -c "^BAKELEFT " "$TMPD/dec.txt")
NOTE_MISSING=""; [ -z "$MISSING" ] || NOTE_MISSING="; recorded jobs whose source file is GONE, not judged:$MISSING"
echo "PASS — deploy drill, the decisions: $n_j jobs ($n_rec recorded conversions, $n_fx fixture jobs) decided as deploy_convert.py decides them - $n_ln log lines to the letter, the parts, the cull and the merges, the armature it builds ($n_bones bones at rest, StaticRoot's anchor, the root-motion anchor) $n_o objects with their matrices, transforms and bound boxes to the bit, and the BAKE - $n_keys keys of the action it holds at its step 5a, each to the bit, with $n_after object matrices of the scene it leaves ($n_bl jobs are Blender's from the bake on, as marked: the scene before it is held); $n_l jobs left to Blender as marked (Blender took $((t4 - t3)) s)$NOTE_MISSING"
