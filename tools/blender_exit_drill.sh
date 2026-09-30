#!/usr/bin/env bash
# blender_exit_drill.sh - a Blender script that crashes must fail the process (2026-09-30).
#
# Blender exits 0 on an uncaught Python exception unless `--python-exit-code` is given BEFORE `--python`; every
# ExitCode check in the editor was dead for a crashed script until BakerRules.BlenderScript. This drill runs the REAL
# Blender with the head that helper builds (asserted against editor/EditorRules.cs, so the two cannot drift):
#   1. a script that raises exits 1;  2. a clean script exits 0;
#   3. the real rig_anim.py clears the previous run's role clip BEFORE it touches the model, and a run that then dies
#      (no such input) exits non-zero - the shape that once left last run's roles beside a fresh primary.
# Blender is found like UniversalBaker.FindBlender (newest "Blender*" under Program Files; BLENDER=<exe> overrides).
# No Blender on this machine = SKIP (exit 0, said): unlike Unity, it is not a prerequisite of the gate.
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then
  for base in "/c/Program Files/Blender Foundation" "/c/Program Files (x86)/Blender Foundation"; do
    [ -d "$base" ] || continue
    cand=$(ls -d "$base"/Blender* 2>/dev/null | sort -V | tail -1)
    [ -n "$cand" ] && [ -x "$cand/blender.exe" ] && { BLENDER="$cand/blender.exe"; break; }
  done
fi
if [ -z "$BLENDER" ] || [ ! -x "$BLENDER" ]; then echo "SKIP — Blender not found (BakerRules.BlenderScript NOT drilled against a real Blender)"; exit 0; fi

# the head the editor builds, taken from the rule itself: the drill must run what the editor runs
HEAD=$(grep -o 'return \$"--background --python-exit-code 1 --python' "$ROOT/editor/EditorRules.cs" | head -1)
[ -n "$HEAD" ] || { echo "FAIL — BakerRules.BlenderScript no longer builds '--background --python-exit-code 1 --python' (update this drill with the rule)"; exit 1; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
fails=0
check() { if [ "$1" -eq 0 ]; then echo "PASS $2"; else echo "FAIL $2"; fails=$((fails+1)); fi; }

printf 'print("PARTIAL OUTPUT WRITTEN")\nraise RuntimeError("boom")\n' > "$TMPD/crash.py"
printf 'print("ok")\n' > "$TMPD/clean.py"
"$BLENDER" --background --python-exit-code 1 --python "$WTMP/crash.py" > "$TMPD/crash.log" 2>&1; rc=$?
check $([ "$rc" -ne 0 ]; echo $?) "a script that raises fails the process (exit $rc)"
"$BLENDER" --background --python "$WTMP/crash.py" > /dev/null 2>&1; rc=$?
check $([ "$rc" -eq 0 ]; echo $?) "without the flag the same crash exits $rc — the hole the helper closes"
"$BLENDER" --background --python-exit-code 1 --python "$WTMP/clean.py" > /dev/null 2>&1; rc=$?
check $([ "$rc" -eq 0 ]; echo $?) "a clean script still exits 0 (exit $rc)"

# 3. the real rig_anim.py: the stale role clip is cleared before the model is touched; the dead run exits non-zero
mkdir -p "$TMPD/res/anim" "$TMPD/res/anim_move"
printf 'stale' > "$TMPD/res/anim_move/x_anim.fbx"
"$BLENDER" --background --python-exit-code 1 --python "$(cygpath -m "$ROOT/editor/Tools~/rig_anim.py")" -- \
  "$WTMP/res/nonexistent.fbx" "$WTMP/res/anim/x_anim.fbx" 0 "" "" "" "" "" "" "move=Walk" > "$TMPD/rig.log" 2>&1; rc=$?
check $([ "$rc" -ne 0 ]; echo $?) "rig_anim on a missing model fails the process (exit $rc)"
check $([ ! -f "$TMPD/res/anim_move/x_anim.fbx" ]; echo $?) "the previous run's role clip was cleared before the model was touched"
check $(grep -q "RIGANIM cleared the previous run's move clip" "$TMPD/rig.log"; echo $?) "and rig_anim said so"
check $([ ! -f "$TMPD/res/anim/x_anim.fbx" ]; echo $?) "no primary was written by the dead run"

if [ "$fails" -ne 0 ]; then echo "FAIL — blender exit drill ($fails failed); logs: $TMPD"; trap - EXIT; exit 1; fi
echo "PASS — blender exit drill: 7 checks against $(basename "$(dirname "$BLENDER")")"
