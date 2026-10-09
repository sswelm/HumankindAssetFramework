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
  "$WROOT/tools/deploy-drill/DeployDrill.cs" \
  "$E/HafModel.cs" "$E/GlbReader.cs" "$E/HafTransforms.cs" "$E/BlenderNames.cs" "$E/BlenderPosedState.cs" "$E/BlenderTrig.cs" "$E/BlenderEigen.cs" "$E/BlenderMesh.cs" "$E/BMesh.cs" "$E/BlenderColor.cs" \
  "$E/VehicleProbe.cs" "$E/VehicleProbe.Visibility.cs" "$E/VehicleProbe.Islands.cs" "$E/VehicleProbe.InsideOut.cs" \
  "$E/VehicleProbe.BlenderWorld.cs" "$E/VehicleProbe.BlenderSkin.cs" "$E/VehicleProbe.CustomNormals.cs" "$E/VehicleProbe.Merge.cs" "$E/VehicleProbe.BlenderArmature.cs" 2>&1); rc=$?
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
  tr -d '\r' < "$TMPD/blender_raw_$k.txt" | grep -E "^(FILE|SCENE|ACTION|OBJ|FRAMES|L|M|DONE|FAIL)	" > "$TMPD/posed_$k.txt"
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
n_prop=$(echo "$TOTAL" | awk '{print $13}')
echo "PASS — deploy drill, the posed state: $n_mat object matrices of $n_files files, at up to thirteen frames each, equal to Blender's bit for bit - the sign of a zero included - and $n_prop evaluated location/rotation/scale sets; $n_left files left to Blender by name (Blender took $((t1 - t0)) s, the comparison $((t2 - t1)) s, in $JOBS processes each); ${#FIXTURES[@]} fixtures, $NOTE_SOURCES"
