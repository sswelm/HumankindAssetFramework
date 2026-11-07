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
# earlier stage compared all the same. The S2_* tampers must be refused too.
# STEP (d), STAGE 3 IN C# (2026-10-13): RunStage3 - the bone cap, the clip resolution and slicing, the action prune, the
# bone filter, the rest fold and rebake (the armature modifier applied as Blender's linear deform, the pose applied as
# the rest through the edit-bone round trip, the visual rebake with mathutils' arithmetic), the legacy translation rules,
# the strip and amplify, the frame range - `riganim.exe --stages 3`; the S3_* tampers (on a clip-path job and on a fold
# job) must be refused. A job the model defers by name is counted apart (none today).
# STEP (e), STAGE 4 IN C# (2026-10-14): RunStage4 - the albedo (by the image's dimensions; the PNG's bytes are Blender's
# encoder's), the join, the material slots, the decimate, the rebind, the root collapse, the topological rename, the
# donor sockets, the rotation and scale fold (transform_apply as object_transform.cc does it), the placement -
# `riganim.exe --stages 4`; the S4_* tampers must be refused. The weld of a one-material, one-bone mesh is left by name.
# STEP (f), THE GLB EXPORT (2026-10-14): after stage 4 the dump exports the scene as a GLB through Blender's glTF exporter
# (the X rows, read back by tools/deploy-drill/glb_rows.py) and BlenderRigExport + BlenderGlbWriter write the same file -
# `riganim.exe --export`; the S5_* tampers must be refused. An EXPORTLEFT: job is left at the export alone.
# STEP 6f OF THE NATIVE FBX READER (2026-11-02): an entry whose source is an .fbx (the light assault mech) is a job: the C#
# reads the .fbx through the FBX reader (HafModelLoader.Read; one it leaves by name is a named leave), Blender's side runs
# rig_anim.py on Blender's OWN GLB export of the file (oracle_fbx_glb: editor/Tools~/fbx_export.py), the convention the
# native model is held to. A .blend source stays LEFT by name.
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
. "$ROOT/tools/blender_oracle.sh"; oracle_init riganim "rig_anim drill"   # replay (the gate: no Blender), record (the refresh) or live
SCRIPT="$ROOT/editor/Tools~/rig_anim.py"
[ -f "$SCRIPT" ] || { echo "FAIL — rig_anim.py not found at $SCRIPT"; exit 1; }
python "$ROOT/tools/riganim-drill/test_riganim_jobs.py" || { echo "FAIL — rig_anim job mapping regression checks"; exit 1; }
python "$ROOT/tools/riganim-drill/test_route_rows.py" || { echo "FAIL — the route comparison's regression checks (step 6g)"; exit 1; }

NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; rig_anim was NOT drilled)"; exit 2; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
cp "$ROOT/editor/Plugins/x86_64/hafkernels.dll" "$TMPD/hafkernels.dll"   # the native kernels beside the exe (HafNativeKernels.cs); absent, the managed twins run
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
E="$WROOT/editor"
# the drill and the stage 1 model (BlenderRigAnim.cs) with the editor files it is built from - the deploy drill's list
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -optimize+ -out:"$WTMP/riganim.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WAPI/System.Numerics.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/riganim-drill/RigAnimDrill.cs" "$WROOT/tools/riganim-drill/RigInProcessResultCheck.cs" "$WROOT/tools/deploy-drill/GlbRowsCompare.cs" "$WROOT/tools/deploy-drill/MaterialDrill.cs" "$E/BlenderRigAnim.cs"  "$E/FbxDocument.cs" "$E/FbxScene.cs" "$E/FbxAnimation.cs" "$E/FbxTriangulate.cs" "$E/FbxImport.cs" "$E/FbxModel.cs" "$E/FbxMaterials.cs" "$E/PngCodec.cs" "$E/FbxArmature.cs" "$E/FbxActions.cs" "$E/HafModelLoader.cs" "$E/BlenderPolyfill.cs" "$E/BlenderWeld.cs" "$E/VehicleProbe.PolygonNormals.cs" "$E/BlenderRigExport.cs" "$E/BlenderRigAnimInProcess.cs" "$E/BlenderDeploy.cs" "$E/BlenderDeployExport.cs" "$E/BlenderMaterialExport.cs" "$E/BlenderChannelExport.cs" "$E/BlenderGlbWriter.cs" "$E/PyFloatRepr.cs" "$E/BlenderFCurve.cs" "$E/BlenderExportTree.cs" \
  "$E/BlenderExport.cs" "$E/BlenderReduce.cs" "$E/BlenderDecimate.cs" \
  "$E/HafModel.cs" "$E/GlbReader.cs" "$E/HafTransforms.cs" "$E/BlenderNames.cs" "$E/BlenderPosedState.cs" "$E/BlenderTrig.cs" "$E/BlenderEigen.cs" "$E/BlenderMesh.cs" "$E/BMesh.cs" "$E/BlenderColor.cs" \
  "$E/VehicleProbe.cs" "$E/VehicleProbe.Visibility.cs" "$E/VehicleProbe.Islands.cs" "$E/VehicleProbe.InsideOut.cs" \
  "$E/VehicleProbe.BlenderWorld.cs" "$E/VehicleProbe.BlenderSkin.cs" "$E/VehicleProbe.CustomNormals.cs" "$E/HafNativeKernels.cs" "$E/HafNativeKernels.Png.cs" "$E/HafNativeKernels.Collapse.cs" "$E/HafNativeKernels.BMesh.cs" "$E/VehicleProbe.Merge.cs" "$E/VehicleProbe.BlenderArmature.cs" "$E/VehicleProbe.BlenderPose.cs" 2>&1); rc=$?
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
  # a registry job the port leaves by NAME at a later stage (the limits BlenderRigAnim.cs's header names) is keyed LEFT: here, as
  # a fixture would be: the drill holds the model to that leave (a model that took the job would be wrong too) and the summary
  # counts it (STAGES ... left N); the Factory's rig slim takes Blender's rig_anim.py for it
  KNOWN_LEFT=""   # recipes left by the model by name (a registry recipe the port names; empty since 2026-11-02: the light assault mech's stage 2 shared datablock is modelled)
  for k in $KNOWN_LEFT; do awk -F'\t' -v OFS='\t' -v k="$k" '$1 == "JOB" && $2 == k { $2 = "LEFT:" k } 1' "$TMPD/jobs.txt" > "$TMPD/jobs_k.txt" && mv "$TMPD/jobs_k.txt" "$TMPD/jobs.txt"; done
  # a registry job equal at all four stages whose EXPORT the exporter port leaves by name (BlenderRigExport's limits) is keyed
  # EXPORTLEFT: as a fixture would be; the drill holds the model to that leave and counts it (EXPORTS ... left N)
  KNOWN_EXPORTLEFT=""   # none since 2026-11-03: the mech's object-level location curve on the armature is written as the armature node's channel (BlenderChannelExport); 2026-11-02..03 it was LightAssaultMech
  for k in $KNOWN_EXPORTLEFT; do awk -F'\t' -v OFS='\t' -v k="$k" '$1 == "JOB" && $2 == k { $2 = "EXPORTLEFT:" k } 1' "$TMPD/jobs.txt" > "$TMPD/jobs_k.txt" && mv "$TMPD/jobs_k.txt" "$TMPD/jobs.txt"; done
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

# step 6f: the .fbx sources, converted once for Blender's side (the shards carry the GLB, the jobs file the .fbx)
awk -F'\t' '($1 == "JOB") && tolower($3) ~ /\.fbx$/ && !seen[$3]++ { print $3 }' "$TMPD/jobs.txt" > "$TMPD/fbx_needed.txt"
N_FBX=0; : > "$TMPD/fbxglb.txt"
while IFS= read -r f; do
  [ -n "$f" ] || continue
  glb=$(oracle_fbx_glb "$N_FBX" "$f"); rc=$?
  [ "$rc" -eq 0 ] || { oracle_check; echo "FAIL — rig_anim drill: Blender could not export $f as a GLB (exit $rc)"; exit 1; }
  printf '%s\t%s\n' "$f" "$glb" >> "$TMPD/fbxglb.txt"; N_FBX=$((N_FBX + 1))
done < "$TMPD/fbx_needed.txt"
[ "$N_FBX" -eq 0 ] || echo "NOTE — rig_anim drill: $N_FBX .fbx source(s) read by the FBX reader here, Blender's side on Blender's own GLB export of each (step 6f)"
JOBS="${RIGANIM_JOBS:-4}"; [ "$JOBS" -ge 1 ] 2>/dev/null || JOBS=1
[ "$JOBS" -le "$n_jobs" ] || JOBS="$n_jobs"
# shards by size, the largest first, round robin
python - "$TMPD/jobs.txt" "$TMPD" "$JOBS" "$TMPD/fbxglb.txt" <<'PYEOF'
import os, sys
jobs = [l for l in open(sys.argv[1], encoding="utf-8").read().split("\n") if l.startswith("JOB\t")]
jobs.sort(key=lambda l: -os.path.getsize(l.split("\t")[2]))
glb = dict(l.split("\t") for l in open(sys.argv[4], encoding="utf-8").read().split("\n") if "\t" in l)   # .fbx -> Blender's own GLB export (6f)
def blender_side(l):
    t = l.split("\t"); t[2] = glb.get(t[2], t[2]); return "\t".join(t)
n = int(sys.argv[3])
for k in range(n):
    open(os.path.join(sys.argv[2], "shard_%d.txt" % k), "w", encoding="utf-8", newline="\n").write("\n".join(blender_side(j) for j in jobs[k::n]) + "\n")
PYEOF
# step 6g (2026-11-06): the same .fbx jobs once more on the .fbx ITSELF - rig_anim.py as the Factory's FBX route runs it -
# for the route row below (the shards above carry the round trip, which is what the C# side is held to)
awk -F'\t' '($1 == "JOB") && tolower($3) ~ /\.fbx$/' "$TMPD/jobs.txt" > "$TMPD/shard_direct.txt"
n_direct=$(grep -c "^JOB	" "$TMPD/shard_direct.txt")
t0=$(date +%s)
BPIDS=()
for ((k = 0; k < JOBS; k++)); do
  mkdir -p "$TMPD/work_$k"
  oracle_blender "dump_$k" "$TMPD/blender_raw_$k.txt" --dir "work_$k" --key "$ROOT/tools/riganim-drill/blender_riganim_dump.py" "$SCRIPT" "$WTMP/shard_$k.txt" "$WTMP/work_$k" -- --background --python "$(cygpath -m "$ROOT/tools/riganim-drill/blender_riganim_dump.py")" -- "$(cygpath -m "$SCRIPT")" "$WTMP/shard_$k.txt" "$WTMP/work_$k" &
  BPIDS+=($!)
done
if [ "$n_direct" -gt 0 ]; then
  mkdir -p "$TMPD/work_direct"
  oracle_blender "dump_direct" "$TMPD/blender_raw_direct.txt" --dir "work_direct" --key "$ROOT/tools/riganim-drill/blender_riganim_dump.py" "$SCRIPT" "$WTMP/shard_direct.txt" "$WTMP/work_direct" -- --background --python "$(cygpath -m "$ROOT/tools/riganim-drill/blender_riganim_dump.py")" -- "$(cygpath -m "$SCRIPT")" "$WTMP/shard_direct.txt" "$WTMP/work_direct" &
  BPIDS+=($!)
fi
brc=0; for pid in "${BPIDS[@]}"; do wait "$pid" || brc=$?; done
oracle_check
t1=$(date +%s)
n_b=0
for ((k = 0; k < JOBS; k++)); do
  tr -d '\r' < "$TMPD/blender_raw_$k.txt" | grep -E "^(JOB|STAGE|LOG|SCENE|OBJ|MESH|VG|BONE|PB|ACT|FC|K|IMG|EXIT|DIES|DONE|FAIL|X|XLOG|XDIES)	" > "$TMPD/dump_$k.txt"
  n_b=$((n_b + $(grep -c "^DONE" "$TMPD/dump_$k.txt")))
done
if [ "$n_direct" -gt 0 ]; then
  tr -d '\r' < "$TMPD/blender_raw_direct.txt" | grep -E "^(JOB|STAGE|LOG|SCENE|OBJ|MESH|VG|BONE|PB|ACT|FC|K|IMG|EXIT|DIES|DONE|FAIL|X|XLOG|XDIES)	" > "$TMPD/dump_direct.txt"
  n_bd=$(grep -c "^DONE" "$TMPD/dump_direct.txt")
  [ "$n_bd" -eq "$n_direct" ] || { grep -E "^FAIL|Traceback|Error" "$TMPD/blender_raw_direct.txt" | head -5; echo "FAIL — rig_anim drill: Blender dumped $n_bd of $n_direct .fbx job(s) on the .fbx itself (step 6g)"; exit 1; }
fi
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
# every job ran through or stopped; at least every EXIT: job stopped (riganim.exe checks each one, and accepts a stop on a LEFT: job)
[ $((n_full + n_stop)) -eq "$n_jobs" ] && [ "$n_stop" -ge "$n_exit" ] || { echo "FAIL — rig_anim drill: not every selected recipe completed all four stages (or an EXIT: fixture did not stop)"; exit 1; }
# STEPS (b) AND (c), STAGES 1 AND 2: the C# model of the first two stages (editor/BlenderRigAnim.cs) against every job's
# rows of those stages, as strings - the dump's own float32 bits and hashes. Every dumped job must be equal row for row
# at both stages, or left by name (LEFT:) at one of them, its earlier stage equal.
# Blender's exported files, for the images it re-encoded: the comparison reads such an image's bytes out of the file and compares
# the rest whole (GlbRowsCompare, 2026-11-07); the work directories are part of the oracle's record, so replay has them too
GLBDIRS=""; for ((k = 0; k < JOBS; k++)); do GLBDIRS="$GLBDIRS${GLBDIRS:+;}$WTMP/work_$k"; done; export RIGANIM_GLB_DIR="$GLBDIRS"
"$TMPD/riganim.exe" --export "${KEY_MODE[@]}" "$WTMP/jobs.txt" "${DUMPS[@]}" > "$TMPD/s1_raw.txt" 2>&1; s1rc=$?
t3=$(date +%s)
tr -d '\r' < "$TMPD/s1_raw.txt" > "$TMPD/s1.txt"
grep -E "^FAIL|^MODEL|^EXPORT .*left to Blender by name" "$TMPD/s1.txt"
S1=$(grep -E "^STAGES 4 jobs" "$TMPD/s1.txt" | tail -1); echo "$S1"
XS=$(grep -E "^EXPORTS jobs" "$TMPD/s1.txt" | tail -1); echo "$XS"
n_exp=$(echo "$XS" | awk '{print $3}'); n_exprows=$(echo "$XS" | awk '{print $5}'); n_inproc=$(echo "$XS" | awk '{print $7}'); n_inprocleft=$(echo "$XS" | awk '{print $9}'); n_glb=$(grep -c "^EXPORT .*: equal .*the GLB's" "$TMPD/s1.txt"); n_expleft=$(grep -c "^EXPORT .*left to Blender by name" "$TMPD/s1.txt")
[ -n "$n_exp" ] || { echo "FAIL — rig_anim drill: the export comparison gave no EXPORTS line"; exit 1; }
[ "$n_glb" -eq "$n_exp" ] || { echo "FAIL — rig_anim drill: $n_glb of $n_exp exported files compared whole (a re-encoded image's bytes stand in from Blender's own file since 2026-11-07: no export may skip its bytes)"; exit 1; }
[ -n "$S1" ] || { tail -5 "$TMPD/s1.txt"; echo "FAIL — rig_anim drill: the stage 1-2 model gave no total (rc=$s1rc)"; exit 1; }
[ "$s1rc" -eq 0 ] || { echo "FAIL — rig_anim drill: the stage 1-2 model differs from Blender, or leaves a dumped job to it"; exit 1; }
n_s1=$(echo "$S1" | awk '{print $4}'); n_s1eq=$(echo "$S1" | awk '{print $6}'); n_s1left=$(echo "$S1" | awk '{print $8}'); n_s1rows=$(echo "$S1" | awk '{print $10}'); n_s3def=$(echo "$S1" | awk '{print $12}')
[ "$n_s1" -eq "$n_jobs" ] && [ $((n_s1eq + n_s1left + n_s3def)) -eq "$n_jobs" ] || { echo "FAIL — rig_anim drill: the stage 1-3 model compared $n_s1 of $n_jobs jobs ($n_s1eq equal, $n_s1left left, $n_s3def deferred)"; exit 1; }
n_s3eq=$(grep -c "^STAGE3 .*: equal" "$TMPD/s1.txt"); n_s4eq=$(grep -c "^STAGE4 .*: equal" "$TMPD/s1.txt")
n_s2eq=$(grep -c "^STAGE2 .*: equal" "$TMPD/s1.txt")
# STEP 6g, THE DIRECT IMPORT AGAINST THE ROUND TRIP (2026-11-06): every .fbx job's stage 4 and export on the .fbx itself
# (the FBX route's own run of rig_anim.py) against the same on the round trip the rows above are held to
# (tools/riganim-drill/route_rows.py). A job the C# route leaves to the FBX route by name (the INPROC lines) may differ -
# its ROUTE lines are the measurement of why the leave stands; one it TAKES must be equal, or the port equals a scene the
# shipped route never makes. An equal job that is still left is a NOTE: the leave could be lifted for that file.
n_route=0; n_route_eq=0; n_route_diff=0; n_route_note=0
if [ "$n_direct" -gt 0 ]; then
  # Validate the direct scene with the SAME full structural parser as the round-trip dump, without comparing it to C#.
  # Count alone cannot prove that the dump contains the requested jobs, complete stages, or consistent scene rows.
  "$TMPD/riganim.exe" "${KEY_MODE[@]}" "$WTMP/shard_direct.txt" "$WTMP/dump_direct.txt" > "$TMPD/direct_validate.txt" 2>&1; directrc=$?
  if [ "$directrc" -ne 0 ]; then
    grep -E '^FAIL' "$TMPD/direct_validate.txt"
    echo "FAIL — rig_anim drill, step 6g: invalid direct-FBX evidence (rc=$directrc)"; exit 1
  fi
  python "$ROOT/tools/riganim-drill/route_rows.py" --inproc "$WTMP/s1.txt" "$WTMP/dump_direct.txt" "${DUMPS[@]}" > "$TMPD/route_raw.txt" 2>&1; routerc=$?
  tr -d '\r' < "$TMPD/route_raw.txt" > "$TMPD/route.txt"
  grep -E "^ROUTE	[^	]+	(EQUAL|DIFFERS|FAIL|NOTE|ERROR)	|^ROUTE	ERROR	|^ROUTES	" "$TMPD/route.txt" | sed 's/\t/ /g'
  [ "$routerc" -eq 0 ] || { echo "FAIL — rig_anim drill, step 6g: an .fbx job the C# route takes differs between the direct import and the round trip, or the route comparison could not run (rc=$routerc; the ROUTE lines above)"; exit 1; }
  RS=$(grep -E "^ROUTES	" "$TMPD/route.txt" | tail -1)
  n_route=$(echo "$RS" | awk '{print $3}'); n_route_eq=$(echo "$RS" | awk '{print $5}'); n_route_diff=$(echo "$RS" | awk '{print $7}'); n_route_note=$(echo "$RS" | awk '{print $11}')
  [ "${n_route:-0}" -eq "$n_direct" ] || { echo "FAIL — rig_anim drill, step 6g: the route comparison covered ${n_route:-0} of $n_direct .fbx job(s)"; exit 1; }
  # the comparison's own control: the direct dump against itself is EQUAL for every job (a comparator that cannot see equal rows would pass nothing)
  python "$ROOT/tools/riganim-drill/route_rows.py" "$WTMP/dump_direct.txt" "$WTMP/dump_direct.txt" > "$TMPD/route_self_raw.txt" 2>&1; selfrc=$?
  n_self=$(tr -d '\r' < "$TMPD/route_self_raw.txt" | grep -cE "^ROUTE	[^	]+	EQUAL	")
  [ "$selfrc" -eq 0 ] && [ "$n_self" -eq "$n_direct" ] || { echo "FAIL — rig_anim drill, step 6g: the route comparison does not hold a dump equal to itself ($n_self of $n_direct, rc=$selfrc)"; exit 1; }
fi
# Negative guards on the REAL dump: a missing or inconsistent record must never pass as evidence.
python "$ROOT/tools/riganim-drill/missing_riganim.py" "$WTMP/missing" "$WTMP/jobs.txt" "${DUMPS[@]}" > "$TMPD/tampers_raw.txt" || { echo "FAIL — could not construct the damaged dumps"; exit 1; }
tr -d '\r' < "$TMPD/tampers_raw.txt" > "$TMPD/tampers.txt"
"$TMPD/riganim.exe" "$WTMP/missing/jobs.txt" "$WTMP/missing/intact.txt" > "$TMPD/intact_out.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/intact_out.txt"; then head -5 "$TMPD/intact_out.txt"; echo "FAIL — rig_anim drill rejected the intact control (rc=$controlrc)"; exit 1; fi
"$TMPD/riganim.exe" --export "$WTMP/missing/jobs.txt" "$WTMP/missing/intact.txt" > "$TMPD/intact_s1.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/intact_s1.txt"; then head -5 "$TMPD/intact_s1.txt"; echo "FAIL — rig_anim drill: the stage 1-2 model rejected the intact control (rc=$controlrc)"; exit 1; fi
# The SHA corruption control uses an image-free export whose complete bytes can be compared.
"$TMPD/riganim.exe" --export "$WTMP/missing/jobs_sha.txt" "$WTMP/missing/intact_sha.txt" > "$TMPD/intact_sha.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/intact_sha.txt" || ! grep -qE "^EXPORT .*the GLB.s [0-9]+ bytes$" "$TMPD/intact_sha.txt"; then head -5 "$TMPD/intact_sha.txt"; echo "FAIL — rig_anim drill rejected the byte-comparable SHA control (rc=$controlrc)"; exit 1; fi
# The same control on an IMAGE-BEARING export (2026-11-07): its bytes are compared whole too, with Blender's own bytes standing in for
# any image Blender re-encoded, so the sha tamper on it must be refused as well (the file came from the work directory above).
"$TMPD/riganim.exe" --export "$WTMP/missing/jobs_sha_img.txt" "$WTMP/missing/intact_sha_img.txt" > "$TMPD/intact_sha_img.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/intact_sha_img.txt" || ! grep -qE "^EXPORT .*the GLB.s [0-9]+ bytes$" "$TMPD/intact_sha_img.txt"; then head -5 "$TMPD/intact_sha_img.txt"; echo "FAIL — rig_anim drill rejected the image-bearing SHA control, or did not compare its bytes whole (rc=$controlrc)"; exit 1; fi
n_t=0; n_s1t=0; n_s2t=0; n_s3t=0; n_s4t=0; n_s5t=0
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
    MESH_hash|MESH_edges_hash|MESH_sharp_hash|MESH_normals_hash|MESH_edges_absent|MESH_colours_hash) reason="fields, .* expected|not SHA-256";;
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
    S3F_*) reason="stage 3";;  # a row of stage 3 on the first dumped job, through the fold (jobs.txt)
    S4_*) reason="stage 4";;   # a row of stage 4 on the first dumped job
    S5_*) reason="export";;    # a row of the GLB export (an X row) on the first dumped job
    *) echo "FAIL — rig_anim drill: the tamper '$mode' has no expected reason here (add it)"; exit 1;;
  esac
  jf="$WTMP/missing/jobs.txt"
  case "$mode" in S5_XGLB_sha) jf="$WTMP/missing/jobs_sha.txt";; S5_XGLB_sha_image) jf="$WTMP/missing/jobs_sha_img.txt";; EXIT_ran_full|EXIT_marked_wrong_stage) jf="$WTMP/missing/jobs_exit.txt";; S3_*) jf="$WTMP/missing/jobs_s3.txt";; esac
  S1_MODE=(); case "$mode" in S1_*) S1_MODE=(--export); n_s1t=$((n_s1t + 1));; S2_*) S1_MODE=(--export); n_s2t=$((n_s2t + 1));; S3_*|S3F_*) S1_MODE=(--export); n_s3t=$((n_s3t + 1));; S4_*) S1_MODE=(--export); n_s4t=$((n_s4t + 1));; S5_*) S1_MODE=(--export); n_s5t=$((n_s5t + 1));; esac
  "$TMPD/riganim.exe" "${S1_MODE[@]}" "${KEY_MODE[@]}" "$jf" "$WTMP/missing/$mode.txt" > "$TMPD/t_$mode.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/t_$mode.txt" | grep -qE "$reason"; then
    head -3 "$TMPD/t_$mode.txt"; echo "FAIL — rig_anim drill accepted a damaged dump ($mode, rc=$badrc)"; exit 1
  fi
  n_t=$((n_t + 1))
done < "$TMPD/tampers.txt"
[ "$n_s1t" -ge 10 ] || { echo "FAIL — rig_anim drill: only $n_s1t stage 1 tampers were run (missing_riganim.py writes S1_* copies)"; exit 1; }
[ "$n_s2t" -ge 8 ] || { echo "FAIL — rig_anim drill: only $n_s2t stage 2 tampers were run (missing_riganim.py writes S2_* copies)"; exit 1; }
[ "$n_s3t" -ge 8 ] || { echo "FAIL — rig_anim drill: only $n_s3t stage 3 tampers were run (missing_riganim.py writes S3_* copies)"; exit 1; }
[ "$n_s4t" -ge 8 ] || { echo "FAIL — rig_anim drill: only $n_s4t stage 4 tampers were run (missing_riganim.py writes S4_* copies)"; exit 1; }
[ "$n_s5t" -ge 8 ] || { echo "FAIL — rig_anim drill: only $n_s5t export tampers were run (missing_riganim.py writes S5_* copies)"; exit 1; }
echo "PASS — rig_anim drill refuses $n_t kinds of damaged evidence (a missing, doubled, short or misplaced row of every kind; an early stop; an unknown job or row kind; $n_s1t changed values of stage 1 rows, $n_s2t of stage 2 rows, $n_s3t of stage 3 rows, $n_s4t of stage 4 rows and $n_s5t of the GLB export's rows, each caught by the model of its stage)"
n_size=$(grep -c "^LEFT	.*	over .* MB" "$TMPD/jobs.txt")
RESULT="PASS — rig_anim drill, step (a) the stage dump: $n_reg of $((n_reg + n_left)) animated recipes and $n_fx fixture jobs run by Blender through rig_anim.py cut at its four stages (Blender took $((t1 - t0)) s), $n_full through all four, $n_stop stopped by the script's own exit ($n_exit as their EXIT: key expects, the rest LEFT: jobs); $n_stages scene dumps with $n_logs RIGANIM lines, $n_bones bones and $n_fc fcurves on record, every block complete and consistent; $n_left recipe(s) left: $n_size over the size limit (FULL=1 takes them), $((n_left - n_size)) by name (Blender-only source until a native reader). Steps (b) to (e), all four stages in C# (editor/BlenderRigAnim.cs: the arguments, the import, the material-less drop, the armature and its exit, the contract marker, the object-animation clear; the bone-parent -> skin bind with its rest-world pin, the wrapper flatten; the bone cap, the clip resolution and slicing, the action prune, the bone filter, the rest fold and rebake, the legacy translation rules, the strip, the frame range; the albedo, the join, the material slots, the decimate, the rebind, the root collapse, the rename, the sockets, the rotation and scale fold, the placement): $n_s1eq of $n_jobs jobs equal to Blender row for row at every stage ($n_s1rows rows of objects, meshes, weights, bones, pose, actions and fcurves, as bits and hashes, $n_s2eq stage 2, $n_s3eq stage 3 and $n_s4eq stage 4 scenes among them; $((t3 - t2)) s), $n_s1left left by name, $n_s3def deferred by name. The albedo PNG is compared by its dimensions only (its bytes and pixels are Blender's encoder's); the weld of a one-material, one-bone mesh is left by name. Step (f), the GLB rig route (editor/BlenderRigExport.cs: the stage 4 scene as Blender's glTF exporter writes it, through the deploy port's exporter model and BlenderGlbWriter): $n_exp jobs' files equal to Blender's - the scene, every node, the skins and their inverse bind matrices, the primitives with their bytes, the materials, textures, samplers and images, the animations' channels sample for sample ($n_exprows rows), $n_glb of them byte-identical GLBs, every one (an image Blender re-encoded - a JPEG albedo's PNG - has its bytes taken from Blender's own file and the rest of the file is compared whole, since 2026-11-07); $n_expleft left at the export by name (each limit is named in the EXPORT lines above). Step (g), the Factory's wrapper (editor/BlenderRigAnimInProcess.cs): on $n_inproc of those jobs it wrote the same file and named the primary's and each role's clip; ${n_inprocleft:-0} skinned or animated .fbx job(s) it leaves to the FBX route by name (the reader's round trip the rows above are held to is not rig_anim.py's direct import; the INPROC lines). Step 6g, the direct import against the round trip (tools/riganim-drill/route_rows.py): $n_route .fbx job(s) run by Blender on the .fbx itself as the FBX route runs it, stage 4 and the export against the round trip's - $n_route_eq equal, $n_route_diff differ and are left to the FBX route by name (the ROUTE lines measure the gap), $n_route_note equal yet left (the leave could be lifted); the comparison holds a dump equal to itself"
echo "$RESULT"
