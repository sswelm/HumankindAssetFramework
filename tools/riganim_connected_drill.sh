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
WORK="$(mktemp -d "${TMPDIR:-/tmp}/haf-riganim-connected.XXXXXXXX")" || exit 2
trap 'rm -rf "$WORK"' EXIT
LOG="$WORK/blender.log"
"$BLENDER" -b --python "$ROOT/tools/riganim-drill/connected_placement_fixture.py" -- "$(cygpath -m "$ROOT/editor/Tools~/rig_anim.py")" "$(cygpath -m "$WORK")" > "$LOG" 2>&1
rc=$?
grep -E "^FIXTURE|^RIGANIM placement" "$LOG"
if [ "$rc" -ne 0 ] || ! grep -q "^FIXTURE PASS" "$LOG"; then
  grep -E "Error|Traceback" "$LOG" | head -5
  echo "FAIL — the placement on a connected chain: rig_anim.py stretched or tore the chain (rc=$rc)"; exit 1
fi
echo "PASS — the placement on a connected chain: every bone its own length, every head and the mesh moved by one delta"
