#!/usr/bin/env bash
# riganim_drill.sh - replacing rig_anim.py (2026-10-12), held to Blender stage by stage.
# STEP (a), THE STAGE DUMP: Blender runs rig_anim.py ITSELF on every animated recipe of the registry, with the arguments
# the Factory hands it (tools/riganim-drill/riganim_jobs.py, from pack.json as UniversalBaker formats them), the script
# cut into its four stages at four of its own lines and the scene written out after each (blender_riganim_dump.py: the
# RIGANIM lines, every object with its matrices, every mesh's geometry and weights hashed, the bones at rest, the pose,
# every action's fcurves hashed, the albedo's pixels). The C# side (RigAnimDrill.cs) holds the EVIDENCE: every recipe's
# block complete and consistent, an unexpected early stop failed with its stage and reason - and prints the census of the
# script's branches the recipes take (COVER rows), which the stage ports (b)..(e) build on. No rule of the script is
# compared yet: the PASS line says so.
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

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -optimize+ -out:"$WTMP/riganim.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" \
  "$WROOT/tools/riganim-drill/RigAnimDrill.cs" 2>&1); rc=$?
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
# Negative guards on the REAL dump: a missing or inconsistent record must never pass as evidence.
python "$ROOT/tools/riganim-drill/missing_riganim.py" "$WTMP/missing" "$WTMP/jobs.txt" "${DUMPS[@]}" > "$TMPD/tampers_raw.txt" || { echo "FAIL — could not construct the damaged dumps"; exit 1; }
tr -d '\r' < "$TMPD/tampers_raw.txt" > "$TMPD/tampers.txt"
"$TMPD/riganim.exe" "$WTMP/missing/jobs.txt" "$WTMP/missing/intact.txt" > "$TMPD/intact_out.txt" 2>&1; controlrc=$?
if [ "$controlrc" -ne 0 ] || ! grep -qE "^PASS " "$TMPD/intact_out.txt"; then head -5 "$TMPD/intact_out.txt"; echo "FAIL — rig_anim drill rejected the intact control (rc=$controlrc)"; exit 1; fi
n_t=0
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
    *) echo "FAIL — rig_anim drill: the tamper '$mode' has no expected reason here (add it)"; exit 1;;
  esac
  jf="$WTMP/missing/jobs.txt"
  case "$mode" in EXIT_ran_full|EXIT_marked_wrong_stage) jf="$WTMP/missing/jobs_exit.txt";; esac
  "$TMPD/riganim.exe" "${KEY_MODE[@]}" "$jf" "$WTMP/missing/$mode.txt" > "$TMPD/t_$mode.txt" 2>&1; badrc=$?
  if [ "$badrc" -ne 1 ] || ! grep -E "^FAIL " "$TMPD/t_$mode.txt" | grep -qE "$reason"; then
    head -3 "$TMPD/t_$mode.txt"; echo "FAIL — rig_anim drill accepted a damaged dump ($mode, rc=$badrc)"; exit 1
  fi
  n_t=$((n_t + 1))
done < "$TMPD/tampers.txt"
echo "PASS — rig_anim drill refuses $n_t kinds of damaged evidence (a missing, doubled, short or misplaced row of every kind; an early stop; an unknown job or row kind)"
n_size=$(grep -c "^LEFT	.*	over .* MB" "$TMPD/jobs.txt")
RESULT="PASS — rig_anim drill, step (a) the stage dump: $n_reg of $((n_reg + n_left)) animated recipes and $n_fx fixture jobs run by Blender through rig_anim.py cut at its four stages (Blender took $((t1 - t0)) s), $n_full through all four, $n_stop stopped by the script's own exit as their EXIT: key expects; $n_stages scene dumps with $n_logs RIGANIM lines, $n_bones bones and $n_fc fcurves on record, every block complete and consistent; $n_left recipe(s) left: $n_size over the size limit (FULL=1 takes them), $((n_left - n_size)) by name (Blender-only source until a native reader). NO RULE OF THE SCRIPT IS COMPARED YET: the stage ports (b)..(e) hold theirs against this dump"
echo "$RESULT"
