#!/usr/bin/env bash
# riganim_connected_drill.sh - the placement on a CONNECTED bone chain, in Blender (2026-11-03).
#
# A connected child's head IS its parent's tail; rig_anim.py's placement moved every head and tail by the delta and so
# moved each shared point once per name - a connected chain stretched by one delta per link while the mesh moved by
# one. The light assault mech (19 of 31 bones connected by the FBX importer) re-baked with its legs in the air. No
# registry source but the mech has a connected bone (every other animated recipe is a .glb; the glTF importer connects
# none), and the rig drill's Blender side for an .fbx is the glTF round trip, which strips the connection - so this
# shape is held by its own fixture: tools/riganim-drill/connected_placement_fixture.py builds the chain, runs
# editor/Tools~/rig_anim.py on it as the Factory does, reads the slim FBX back and holds every length and one delta.
#
# Parity mode only (it runs Blender): a parity_step in tools/check.sh. SKIP (exit 3) without Blender is a gate FAIL
# there, as for every parity drill. Exit 0 PASS, 1 FAIL, 2 error.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
. "$ROOT/tools/blender_oracle.sh"
oracle_find_blender
if [ -z "${BLENDER:-}" ] || [ ! -x "$BLENDER" ]; then echo "SKIP — no Blender found (BLENDER=<exe>); the connected-chain placement is NOT tested"; exit 3; fi
TASK_TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)" || exit 2
WORK="$(mktemp -d "$TASK_TEMP_ROOT/haf-riganim-connected.XXXXXXXX")" || exit 2
cleanup() {
  [ -d "$WORK" ] || return 0
  local resolved="$(cd "$WORK" && pwd -P)"
  case "$resolved" in
    "$TASK_TEMP_ROOT"/haf-riganim-connected.*) rm -rf -- "$resolved";;
    *) echo 'FAIL — refusing cleanup outside the connected-chain runner root' >&2; return 1;;
  esac
}
trap cleanup EXIT
for mode in valid old-loop no-placement double-scale; do
  echo "FIXTURE case $mode (valid must pass; regression controls must fail their output checks)"
  LOG="$WORK/$mode.log"
  "$BLENDER" -b --python-exit-code 1 --python "$ROOT/tools/riganim-drill/connected_placement_fixture.py" -- "$(cygpath -m "$ROOT/editor/Tools~/rig_anim.py")" "$(cygpath -m "$WORK/$mode")" "$mode" > "$LOG" 2>&1
  rc=$?
  grep -E "^FIXTURE|^RIGANIM placement" "$LOG"
  if [ "$mode" = valid ]; then
    if [ "$rc" -ne 0 ] || ! grep -q '^FIXTURE PASS: connected-chain placement ' "$LOG" || grep -q '^FIXTURE FAIL' "$LOG"; then
      tail -15 "$LOG"
      echo "FAIL — connected-chain placement did not pass (rc=$rc)"; exit 1
    fi
  else
    # A crash or missing output is not evidence the numerical checks rejected the intended regression.
    if [ "$rc" -eq 0 ] || ! grep -q '^FIXTURE FAIL: connected-chain placement ' "$LOG" || grep -q '^FIXTURE PASS' "$LOG"; then
      tail -15 "$LOG"
      echo "FAIL — the fixture did not reject the $mode control through its placement checks (rc=$rc)"; exit 1
    fi
    echo "PASS — connected-chain fixture rejects $mode"
  fi
done
echo "PASS — connected-chain placement: absolute heads, lengths, parents and mesh bounds; old-loop, missing placement and wrong scale rejected"
