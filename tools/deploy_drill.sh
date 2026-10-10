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
  "$WROOT/tools/deploy-drill/DeployDrill.cs" "$WROOT/tools/deploy-drill/DecisionsDrill.cs" "$WROOT/tools/deploy-drill/BezierDrill.cs" "$E/BlenderDeploy.cs" "$E/BlenderFCurve.cs" "$E/BlenderExportTree.cs" \
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
KNOWN_BAKE_LEFT="DugoutCanoe"   # its strip list ("camera") leaves the importer's bone shape in: the bind folds the icosphere's vertices, which are Blender's (part 6)
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
tr -d '\r' < "$TMPD/decisions_raw.txt" | grep -E "^(JOB|LOG|LOG2|EXIT|RANGE|NORM|FLAG|PART|BAD|ALIAS|ARM|BONEOF|RBONE|ANCHOR|HULL|PINV|OBJ|M|T|BOX|ACT|FC|FCA|PB2|PB3|M2|O2|O3|LOG3|SNAP|APB|O4|LOG4|FC4|APB4|O5|LOG5|EXIT5|DIES5|R5|RBONE5|FC5|APB5|PM5|O6|SW|DONE|FAIL)	" > "$TMPD/decisions.txt"
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
python "$ROOT/tools/deploy-drill/missing_imported_pose.py" "$WTMP/imported_pose" "$WTMP/decisions.txt" "$WTMP/jobs.txt" || { echo "FAIL — could not construct the imported-pose regressions"; exit 1; }
"$TMPD/deploy.exe" --decisions "$WTMP/imported_pose/jobs.txt" "$WTMP/imported_pose/intact.txt" > "$TMPD/imported_pose_control.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/imported_pose_control.txt"; then
  head -5 "$TMPD/imported_pose_control.txt"
  echo "FAIL — deploy drill rejected the intact imported-pose control (rc=$controlrc)"; exit 1
fi
for kind in PB2 PB3; do
  for mode in missing all_missing duplicate unknown_armature unknown_bone all_unknown short long; do
    case "$mode" in
      missing|all_missing) reason="no $kind row for bone";;
      duplicate) reason="duplicate $kind bone";;
      unknown_armature|unknown_bone|all_unknown) reason="unknown $kind bone";;
      short|long) reason="$kind row has";;
    esac
    "$TMPD/deploy.exe" --decisions "$WTMP/imported_pose/jobs.txt" "$WTMP/imported_pose/${kind}_$mode.txt" > "$TMPD/imported_pose_${kind}_$mode.txt" 2>&1; badrc=$?
    if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/imported_pose_${kind}_$mode.txt" | grep -qF "$reason"; then
      head -5 "$TMPD/imported_pose_${kind}_$mode.txt"
      echo "FAIL — deploy drill accepted invalid $kind evidence ($mode, rc=$badrc)"; exit 1
    fi
  done
done
echo "PASS — deploy drill accepts intact imported poses and rejects missing, duplicate, unknown, truncated or oversized PB2 and PB3 rows"
echo "PASS — deploy drill rejects a decisions dump without a log line, a matrix, a transform, a box, an object, a part, the range, the end row, a bone, a part's bone, the anchors, the parent inverse, a baked curve, a key, the bake's log line or the action row - and one with a baked value changed by one bit or a curve under a component its channel does not have"
echo "PASS — deploy drill accepts intact single-job controls and rejects a successful, truncated or duplicated abort record"
# ... and the fire-window snapshot (5a): against an intact control, a snapshot row, a pose row of the new armature and an
# object row after it - each missing, doubled in place of its neighbour, under an unknown name, cut short, or one bit off
"$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/fire_jobs.txt" "$WTMP/missing_dec/fire_intact.txt" > "$TMPD/fire_control.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/fire_control.txt"; then
  head -5 "$TMPD/fire_control.txt"
  echo "FAIL — deploy drill rejected the intact fire-window control (rc=$controlrc)"; exit 1
fi
for kind in SNAP APB O4; do
  case "$kind" in SNAP) what="snapshot row";; APB) what="armature pose row";; O4) what="object row after the fire window";; esac
  for mode in missing duplicate unknown short value; do
    case "$mode" in
      missing) reason="has no $what";;
      duplicate) reason="twice";;
      unknown) reason="an unknown $what";;
      short) reason="fields, expected";;
      value) case "$kind" in SNAP) reason="snapshot rows differ";; APB) reason="hold other values after the fire window";; O4) reason="matrices after the fire window differ";; esac;;
    esac
    "$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/fire_jobs.txt" "$WTMP/missing_dec/fire_${kind}_$mode.txt" > "$TMPD/fire_${kind}_$mode.txt" 2>&1; badrc=$?
    if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/fire_${kind}_$mode.txt" | grep -qF "$reason"; then
      head -5 "$TMPD/fire_${kind}_$mode.txt"
      echo "FAIL — deploy drill accepted invalid fire-window evidence ($kind $mode, rc=$badrc)"; exit 1
    fi
  done
done
"$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/fire_jobs.txt" "$WTMP/missing_dec/fire_LOG3_missing.txt" > "$TMPD/fire_log3.txt" 2>&1; badrc=$?
if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/fire_log3.txt" | grep -qF "the fire window's log"; then head -5 "$TMPD/fire_log3.txt"; echo "FAIL — deploy drill accepted a dump without the fire window's log line (rc=$badrc)"; exit 1; fi
echo "PASS — deploy drill accepts an intact fire-window snapshot and rejects missing, doubled, unknown, truncated or changed snapshot, pose and object rows, and a missing log line"
# ... and the barrel retarget and the leg scale (5b, 5c): against an intact control, one of the new Bezier curves, a pose
# row and an object row after them - each missing, doubled in place of its neighbour, renamed, or one bit off
"$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/gun_jobs.txt" "$WTMP/missing_dec/gun_intact.txt" > "$TMPD/gun_control.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/gun_control.txt"; then
  head -5 "$TMPD/gun_control.txt"
  echo "FAIL — deploy drill rejected the intact retarget control (rc=$controlrc)"; exit 1
fi
for kind in FC4 APB4 O5; do
  for mode in missing duplicate unknown value; do
    case "$kind:$mode" in
      FC4:missing) reason="has no curve after the retarget";;
      FC4:unknown) reason="of an unknown bone";;
      FC4:value) reason="curves after the retarget differ";;
      APB4:missing) reason="has no armature pose row after the retarget";;
      APB4:unknown) reason="an unknown armature pose row after the retarget";;
      APB4:value) reason="hold other values after the retarget";;
      O5:missing) reason="has no object row after the retarget";;
      O5:unknown) reason="an unknown object row after the retarget";;
      O5:value) reason="matrices after the retarget differ";;
      *:duplicate) reason="twice";;
    esac
    "$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/gun_jobs.txt" "$WTMP/missing_dec/gun_${kind}_$mode.txt" > "$TMPD/gun_${kind}_$mode.txt" 2>&1; badrc=$?
    if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/gun_${kind}_$mode.txt" | grep -qF "$reason"; then
      head -5 "$TMPD/gun_${kind}_$mode.txt"
      echo "FAIL — deploy drill accepted invalid retarget evidence ($kind $mode, rc=$badrc)"; exit 1
    fi
  done
done
"$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/gun_jobs.txt" "$WTMP/missing_dec/gun_LOG4_missing.txt" > "$TMPD/gun_log4.txt" 2>&1; badrc=$?
if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/gun_log4.txt" | grep -qF "the retarget's log"; then head -5 "$TMPD/gun_log4.txt"; echo "FAIL — deploy drill accepted a dump without the retarget's log line (rc=$badrc)"; exit 1; fi
echo "PASS — deploy drill accepts an intact barrel retarget and rejects missing, doubled, unknown or changed curve, pose and object rows, and a missing log line"
# ... and the frame sweep after it: a row missing, doubled, renamed, cut short, one bit off, and no sweep at all
for mode in missing duplicate unknown short value none frame tail; do
  case "$mode" in
    missing) reason="has no sweep row at frame";;
    duplicate) reason="twice";;
    unknown) reason="of an unknown bone";;
    short) reason="fields, expected 13";;
    value) reason="of the frame sweep hold other values";;
    none) reason="has no frame sweep";;
    frame|tail) reason="is not the sweep the dump script makes";;
  esac
  "$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/gun_jobs.txt" "$WTMP/missing_dec/gun_SW_$mode.txt" > "$TMPD/gun_SW_$mode.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/gun_SW_$mode.txt" | grep -qF "$reason"; then
    head -5 "$TMPD/gun_SW_$mode.txt"
    echo "FAIL — deploy drill accepted an invalid frame sweep ($mode, rc=$badrc)"; exit 1
  fi
done
echo "PASS — deploy drill rejects a frame sweep with a missing, doubled, unknown, truncated or changed row, one with a frame taken out or its tail cut off, and a retarget without one"
# ... and the recoil tail (5d): against an intact control, a measurement row, a rebuilt bone, one of the arm's curves, a
# pose row, a pose matrix and an object row after it - each missing, doubled, renamed, cut short or one bit off; the
# log line missing; the script's exit or death claimed where the port went on
"$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/recoil_jobs.txt" "$WTMP/missing_dec/recoil_intact.txt" > "$TMPD/recoil_control.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/recoil_control.txt"; then
  head -5 "$TMPD/recoil_control.txt"
  echo "FAIL — deploy drill rejected the intact recoil control (rc=$controlrc)"; exit 1
fi
for kind in R5 RBONE5 FC5 APB5 PM5 O6; do
  for mode in missing duplicate unknown short value; do
    case "$kind" in
      R5) reason="the recoil step's measurements";;
      RBONE5) reason="bone";;
      FC5) reason="curve";;
      APB5) reason="pose row|pose bones of the new armature hold other values";;
      PM5|O6) reason="object row|matrices .*after the recoil";;
    esac
    "$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/recoil_jobs.txt" "$WTMP/missing_dec/recoil_${kind}_$mode.txt" > "$TMPD/recoil_${kind}_$mode.txt" 2>&1; badrc=$?
    if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/recoil_${kind}_$mode.txt" | grep -qE "$reason"; then
      head -5 "$TMPD/recoil_${kind}_$mode.txt"
      echo "FAIL — deploy drill accepted invalid recoil evidence ($kind $mode, rc=$badrc)"; exit 1
    fi
  done
done
for mode in LOG5_missing EXIT5_claimed DIES5_claimed; do
  case "$mode" in
    LOG5_missing) reason="the recoil step's log";;
    EXIT5_claimed) reason="exits in the recoil step";;
    DIES5_claimed) reason="died in the recoil step";;
  esac
  "$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/recoil_jobs.txt" "$WTMP/missing_dec/recoil_$mode.txt" > "$TMPD/recoil_$mode.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/recoil_$mode.txt" | grep -qE "$reason"; then
    head -5 "$TMPD/recoil_$mode.txt"; echo "FAIL — deploy drill accepted a recoil dump with $mode (rc=$badrc)"; exit 1
  fi
done
echo "PASS — deploy drill accepts an intact recoil tail and rejects missing, doubled, unknown, truncated or changed measurement, bone, curve, pose, pose-matrix and object rows, a missing log line, and a claimed exit or death of the script"
# ... and the bind (6): against an intact control, a bind row, a vertex-group row, a modifier row, a vertex hash, a sampled
# vertex, a custom-normal row, a datablock row and an object row after it - each missing, doubled, renamed, cut short or one
# bit off; the log line missing; the script's death claimed where the port went on
"$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/bind_jobs.txt" "$WTMP/missing_dec/bind_intact.txt" > "$TMPD/bind_control.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/bind_control.txt"; then
  head -5 "$TMPD/bind_control.txt"
  echo "FAIL — deploy drill rejected the intact bind control (rc=$controlrc)"; exit 1
fi
for kind in BIND VG6 MOD6 V6 VX6 N6 DATA6 O7; do
  for mode in missing duplicate unknown short value; do
    case "$kind" in
      BIND) reason="bound meshes|bind row";;
      VG6) reason="vertex groups after the bind";;
      MOD6) reason="modifiers after the bind";;
      V6) reason="vertices after the bind";;
      VX6) reason="sampled vertices after the bind";;
      N6) reason="custom normals after the bind";;
      DATA6) reason="mesh datablocks after the bind";;
      O7) reason="object row|matrices .*after the bind";;
    esac
    "$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/bind_jobs.txt" "$WTMP/missing_dec/bind_${kind}_$mode.txt" > "$TMPD/bind_${kind}_$mode.txt" 2>&1; badrc=$?
    if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/bind_${kind}_$mode.txt" | grep -qE "$reason"; then
      head -5 "$TMPD/bind_${kind}_$mode.txt"
      echo "FAIL — deploy drill accepted invalid bind evidence ($kind $mode, rc=$badrc)"; exit 1
    fi
  done
done
for mode in LOG6_missing DIES6_claimed; do
  case "$mode" in
    LOG6_missing) reason="the bind's log";;
    DIES6_claimed) reason="died in the bind";;
  esac
  "$TMPD/deploy.exe" --decisions "$WTMP/missing_dec/bind_jobs.txt" "$WTMP/missing_dec/bind_$mode.txt" > "$TMPD/bind_$mode.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/bind_$mode.txt" | grep -qE "$reason"; then
    head -5 "$TMPD/bind_$mode.txt"; echo "FAIL — deploy drill accepted a bind dump with $mode (rc=$badrc)"; exit 1
  fi
done
echo "PASS — deploy drill accepts an intact bind and rejects missing, doubled, unknown, truncated or changed bind, vertex-group, modifier, vertex-hash, sampled-vertex, custom-normal, datablock and object rows, a missing log line, and a claimed death of the script"

# ---- the handles of automatic keys: Blender's own calculation (FCurve.update(), keyframe_points.insert(), and
#      handles_recalc() on keys stored raw) on generated curves against BlenderFCurve.RecalcHandles, both handles of
#      every key to the bit. Every branch the generator can reach must be reached; a dump cut short, with a curve
#      replaced by its neighbour, one bit off, or of another smoothing fails.
HND_CURVES="${HND_CURVES:-6000}"
"$BLENDER" --background --python "$(cygpath -m "$ROOT/tools/deploy-drill/blender_handles_dump.py")" -- 1 "$HND_CURVES" 2> "$TMPD/handles_err.txt" | tr -d '\r' | grep -E "^(H|END)	" > "$TMPD/handles.txt"
"$TMPD/deploy.exe" --handles "$WTMP/handles.txt" > "$TMPD/handles_out.txt" 2>&1; hrc=$?
if [ "$hrc" -ne 0 ] || ! grep -q "^PASS " "$TMPD/handles_out.txt"; then
  tail -3 "$TMPD/handles_err.txt"; grep -E "^(DIFF|FAIL|TOTAL)" "$TMPD/handles_out.txt" | head -8
  echo "FAIL — the handles of automatic keys differ from Blender's (rc=$hrc)"; exit 1
fi
# every curve is judged on its own, so a dump that holds fewer is still consistent: the count asked for is the floor
hnd_n=$(grep "^TOTAL" "$TMPD/handles_out.txt" | awk '{print $3}')
if [ "${hnd_n:-0}" -ne "$HND_CURVES" ]; then echo "FAIL — the handles dump holds $hnd_n curves, $HND_CURVES were asked for"; exit 1; fi
for branch in "a key that is an extreme of its neighbours (flat)" "the left handle stopped at the previous key's height" "the right handle stopped at the next key's height" \
    "an end held flat (CONSTANT extrapolation)" "two keys on one frame" "a run of keys smoothed" "a free first key (LINEAR extrapolation)" "a free last key (LINEAR extrapolation)" \
    "the system has no finite solution (the handles stay)" "an overshooting handle locked on the second look (at zero)" "an overshooting handle locked at its limit" \
    "a locked handle released" "two unknowns" "a locked handle released a second time" "a locked handle kept after two releases"; do
  hits=$(grep -F "	handles: $branch" "$TMPD/handles_out.txt" | grep "^BRANCH" | head -1 | cut -f2)
  if [ -z "$hits" ] || [ "$hits" -le 0 ]; then echo "FAIL — the generated curves never reach the handle branch '$branch'"; exit 1; fi
done
python - "$TMPD/handles.txt" "$TMPD" <<'PYEOF'
import pathlib, sys
lines = pathlib.Path(sys.argv[1]).read_text(encoding="ascii").splitlines()
out = pathlib.Path(sys.argv[2])
def write(name, changed): (out / ("handles_" + name + ".txt")).write_text("\n".join(changed) + "\n", encoding="ascii")
write("cut", lines[:-1])
write("short", lines[:len(lines) // 2] + lines[-1:])
write("replaced", lines[:1] + lines[:1] + lines[2:])
i = max(k for k, l in enumerate(lines) if l.startswith("H\t") and len(l.split("\t")) > 6)
changed = list(lines); changed[i] = lines[i][:-1] + ("0" if lines[i][-1] != "0" else "1"); write("bit", changed)
t = lines[0].split("\t"); t[3] = "NONE"; write("smoothing", ["\t".join(t)] + lines[1:])
t = lines[0].split("\t"); write("key", ["\t".join(t[:-1])] + lines[1:])
write("junk", lines[:3] + ["GARBAGE\tx"] + lines[3:])
write("after_end", lines + [lines[0]])
PYEOF
for mode in cut short replaced bit smoothing key junk after_end; do
  case "$mode" in
    cut) reason="no end row";; short) reason="cut short";; replaced) reason="ordinal";; bit) reason="have other handles";;
    smoothing) reason="smoothing is NONE";; key) reason="cut short";; junk) reason="does not know";; after_end) reason="follows the dump's end row";;
  esac
  "$TMPD/deploy.exe" --handles "$WTMP/handles_$mode.txt" > "$TMPD/handles_${mode}_out.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep "^FAIL " "$TMPD/handles_${mode}_out.txt" | grep -qF "$reason"; then
    head -3 "$TMPD/handles_${mode}_out.txt"; echo "FAIL — the handles drill accepted malformed evidence ($mode, rc=$badrc)"; exit 1
  fi
done
echo "$(grep "^PASS " "$TMPD/handles_out.txt") - every key AUTO_CLAMPED, the default smoothing; keys stored and updated, inserted in and out of order, stored raw with doubled frames; a dump cut short, with a curve replaced or a key gone, one bit off, of another smoothing, or with an unknown row fails (never reached, ported from the source only: a fixed end handle scaled to fit)"

# ---- ... and the real thing: pose_bone.keyframe_insert on an armature, key after key the way the script's step 5d
#      keys its RecoilArm (holds, a turn whose first key lands on a hold, the turns back slower, a key again on a
#      frame that has one). Every curve is written after EVERY insert: each state of the handles must be the one
#      calculation from the keys alone. The dump writes no end row when a key is not BEZIER / AUTO_CLAMPED or a
#      replaced value is not old + (new - old).
KEY_TRIALS="${KEY_TRIALS:-40}"
"$BLENDER" --background --python "$(cygpath -m "$ROOT/tools/deploy-drill/blender_keyframe_insert_dump.py")" -- 1 "$KEY_TRIALS" 2> "$TMPD/keyins_err.txt" | tr -d '\r' | grep -E "^(H|END)	" > "$TMPD/keyins.txt"
"$TMPD/deploy.exe" --handles "$WTMP/keyins.txt" > "$TMPD/keyins_out.txt" 2>&1; krc=$?
key_n=$(grep "^TOTAL" "$TMPD/keyins_out.txt" | awk '{print $3}')
if [ "$krc" -ne 0 ] || ! grep -q "^PASS " "$TMPD/keyins_out.txt" || [ "${key_n:-0}" -lt $((KEY_TRIALS * 100)) ]; then
  tail -3 "$TMPD/keyins_err.txt"; grep -E "^(DIFF|FAIL|TOTAL)" "$TMPD/keyins_out.txt" | head -8
  echo "FAIL — the handles keyframe_insert leaves on a pose bone differ from the port's (rc=$krc, ${key_n:-0} curves)"; exit 1
fi
echo "$(grep "^PASS " "$TMPD/keyins_out.txt") - pose_bone.keyframe_insert on an armature, $KEY_TRIALS recoil key sequences, every curve after every insert"

# ---- the Bezier evaluation on its own: Blender's FCurve.evaluate() on generated curves (free handles, cut-back
#      handles, flat ones, exactly quadratic and linear time curves, many keys) against BlenderFCurve.Evaluate, to the
#      bit. Every branch of the solver the generator can reach must be reached, and a dump cut short or one bit off fails.
BEZ_CURVES="${BEZ_CURVES:-6000}"
"$BLENDER" --background --python "$(cygpath -m "$ROOT/tools/deploy-drill/blender_bezier_dump.py")" -- 1 "$BEZ_CURVES" 2> "$TMPD/bezier_err.txt" | tr -d '\r' | grep -E "^(CURVE|TIMES|E|END)	" > "$TMPD/bezier.txt"
"$TMPD/deploy.exe" --bezier "$WTMP/bezier.txt" > "$TMPD/bezier_out.txt" 2>&1; brc=$?
if [ "$brc" -ne 0 ] || ! grep -q "^PASS " "$TMPD/bezier_out.txt"; then
  tail -3 "$TMPD/bezier_err.txt"; grep -E "^(DIFF|FAIL|TOTAL)" "$TMPD/bezier_out.txt" | head -8
  echo "FAIL — the Bezier evaluation differs from Blender's FCurve.evaluate() (rc=$brc)"; exit 1
fi
for branch in "at or before the first key" "at or past the last key" "on a middle key (within 0.0001 frame)" "all at one height" "no root in range (0)" \
    "the first key's handle cut back" "the second key's handle cut back" "cubic, one real root" "cubic, zero discriminant: the first root" "cubic, zero discriminant: the second root" \
    "cubic, three real roots: the first" "cubic, three real roots: the second" "cubic, three real roots: the third" "quadratic: the first root" "quadratic: the second root" "linear"; do
  hits=$(grep -F "	$branch" "$TMPD/bezier_out.txt" | grep "^BRANCH" | head -1 | cut -f2)
  if [ -z "$hits" ] || [ "$hits" -le 0 ]; then echo "FAIL — the generated Bezier curves never reach the branch '$branch'"; exit 1; fi
done
sed '$d' "$TMPD/bezier.txt" > "$TMPD/bezier_cut.txt"
"$TMPD/deploy.exe" --bezier "$WTMP/bezier_cut.txt" > "$TMPD/bezier_cut_out.txt" 2>&1; badrc=$?
if [ "$badrc" -ne 1 ] || ! grep -q "cut short" "$TMPD/bezier_cut_out.txt"; then echo "FAIL — the Bezier drill accepted a dump without its end row (rc=$badrc)"; exit 1; fi
python - "$TMPD/bezier.txt" "$TMPD/bezier_bit.txt" <<'PYEOF'
import sys
lines = open(sys.argv[1], encoding="ascii").read().split("\n")
i = max(k for k, l in enumerate(lines) if l.startswith("E\t"))
lines[i] = lines[i][:-1] + ("0" if lines[i][-1] != "0" else "1")
open(sys.argv[2], "w", encoding="ascii", newline="\n").write("\n".join(lines))
PYEOF
"$TMPD/deploy.exe" --bezier "$WTMP/bezier_bit.txt" > "$TMPD/bezier_bit_out.txt" 2>&1; badrc=$?
if [ "$badrc" -ne 1 ] || ! grep -q "1 of .* values differ" "$TMPD/bezier_bit_out.txt"; then echo "FAIL — the Bezier drill accepted a value one bit off (rc=$badrc)"; exit 1; fi
# ... and one with most of its values gone (the end row counts them), or a row of another kind
awk -F'\t' '$1 != "E" || ++n % 50 == 0' "$TMPD/bezier.txt" > "$TMPD/bezier_few.txt"
"$TMPD/deploy.exe" --bezier "$WTMP/bezier_few.txt" > "$TMPD/bezier_few_out.txt" 2>&1; badrc=$?
if [ "$badrc" -ne 1 ] || ! grep -q "cut short" "$TMPD/bezier_few_out.txt"; then echo "FAIL — the Bezier drill accepted a dump with most of its values gone (rc=$badrc)"; exit 1; fi
{ head -3 "$TMPD/bezier.txt"; printf 'GARBAGE\tx\n'; tail -n +4 "$TMPD/bezier.txt"; } > "$TMPD/bezier_junk.txt"
"$TMPD/deploy.exe" --bezier "$WTMP/bezier_junk.txt" > "$TMPD/bezier_junk_out.txt" 2>&1; badrc=$?
if [ "$badrc" -eq 0 ] || ! grep -q "does not know" "$TMPD/bezier_junk_out.txt"; then echo "FAIL — the Bezier drill accepted a row of an unknown kind (rc=$badrc)"; exit 1; fi
# Counts alone cannot detect a lost evaluation replaced by a repeated constant value, or a curve replaced by
# another with the same number of evaluations. The request list and curve ordinal must catch both.
python - "$TMPD/bezier.txt" "$TMPD" <<'PYEOF'
import pathlib, sys
lines = pathlib.Path(sys.argv[1]).read_text(encoding="ascii").splitlines()
out = pathlib.Path(sys.argv[2])
def write(name, changed): (out / ("bezier_" + name + ".txt")).write_text("\n".join(changed) + "\n", encoding="ascii")
rows = []
for i, line in enumerate(lines):
    if line.startswith("CURVE\t"): rows = []
    elif line.startswith("E\t"):
        fields = line.split("\t")
        prior = next((j for j in rows if lines[j].split("\t")[2] == fields[2] and lines[j].split("\t")[1] != fields[1]), None)
        if prior is not None:
            changed = list(lines); changed[i] = lines[prior]; write("replaced_sample", changed); break
        rows.append(i)
else: raise RuntimeError("no two distinct evaluation times with an equal value for the negative control")
starts = [i for i, line in enumerate(lines) if line.startswith("CURVE\t")]
assert starts[1] - starts[0] == starts[2] - starts[1]
write("replaced_curve", lines[:starts[1]] + lines[starts[0]:starts[1]] + lines[starts[2]:])
write("missing_times", [line for i, line in enumerate(lines) if i != starts[0] + 1])
write("after_end", lines + [lines[0]])
write("double_end", lines + [lines[-1]])
PYEOF
for mode in replaced_sample replaced_curve missing_times after_end double_end; do
  "$TMPD/deploy.exe" --bezier "$WTMP/bezier_$mode.txt" > "$TMPD/bezier_${mode}_out.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep -q "^FAIL " "$TMPD/bezier_${mode}_out.txt"; then
    echo "FAIL — the Bezier drill accepted malformed evidence ($mode, rc=$badrc)"; exit 1
  fi
done
echo "$(grep "^PASS " "$TMPD/bezier_out.txt") - keys stored raw: backward handles, keys out of order, doubled frames; missing or replaced requests/curves, cut dumps, changed values, unknown rows and rows after the end fail (never reached, ported from the source only: a quadratic's zero discriminant, the 1e-8 key test, the segment miss, no equation left)"
n_j=$(echo "$TOTAL2" | awk '{print $3}'); n_l=$(echo "$TOTAL2" | awk '{print $7}'); n_o=$(echo "$TOTAL2" | awk '{print $9}'); n_ln=$(echo "$TOTAL2" | awk '{print $13}'); n_bones=$(echo "$TOTAL2" | awk '{print $15}'); n_keys=$(echo "$TOTAL2" | awk '{print $19}'); n_after=$(echo "$TOTAL2" | awk '{print $21}'); n_bl=$(grep -c "^BAKELEFT " "$TMPD/dec.txt")
NOTE_MISSING=""; [ -z "$MISSING" ] || NOTE_MISSING="; recorded jobs whose source file is GONE, not judged:$MISSING"
echo "PASS — deploy drill, the decisions: $n_j jobs ($n_rec recorded conversions, $n_fx fixture jobs) decided as deploy_convert.py decides them - $n_ln log lines to the letter, the parts, the cull and the merges, the armature it builds ($n_bones bones at rest, StaticRoot's anchor, the root-motion anchor) $n_o objects with their matrices, transforms and bound boxes to the bit, and the BAKE - $n_keys keys of the action it holds at its step 5a, each to the bit, with $n_after object matrices of the scene it leaves ($n_bl jobs are Blender's from the bake on, as marked: the scene before it is held); $n_l jobs left to Blender as marked (Blender took $((t4 - t3)) s)$NOTE_MISSING"
