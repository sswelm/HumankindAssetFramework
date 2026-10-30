#!/usr/bin/env bash
# refresh_blender_oracle.sh - record the Blender oracle the parity drills can replay (2026-10-28). SINCE 2026-10-30 THE
# PUSH READS NO RECORD: the parity drills are not in the push at all (tools/check.sh HAF_GATE=parity; tools/blender_parity.sh
# and the Bake Tests row run them with Blender). This script remains for an offline rerun of the drills (HAF_ORACLE=replay
# after a record made here); nothing requires it any more.
#
# The parity drills hold the C# ports to Blender's own output. They used to run Blender on every push; now the push
# REPLAYS a record and this script MAKES it: it runs the whole gate (tools/check.sh) with HAF_ORACLE=record, so every
# drill runs Blender exactly as the gate shapes it - the same samples, shards and fixtures - and stores each invocation's
# raw output, exit code and output folders under $HAF_BLENDER_ORACLE (default <modding project>/Tools/blender_oracle,
# self-ignored by git), keyed by the dump script, the Blender script under test and the input files' bytes. The gate
# then replays without Blender; a record that is missing or stale is a SKIP that names what changed and fails the push.
#
# Run it after changing a script under editor/Tools~, a saved recipe, a registry source, a drill's fixtures or its
# sample rule - or when a push says so. It takes the gate's lock and its time (about the old gate's: the Blender legs
# run here). The blender exit drill (Blender's own exit behaviour, nothing to record) runs here and only here.
# Blender is found like UniversalBaker.FindBlender (newest "Blender*" under Program Files); BLENDER=<exe> overrides.
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
. "$ROOT/tools/blender_oracle.sh"
oracle_find_blender
if [ -z "$BLENDER" ] || [ ! -x "$BLENDER" ]; then echo "FAIL — no Blender found (BLENDER=<exe>); nothing was recorded"; exit 2; fi
export BLENDER HAF_ORACLE=record HAF_GATE=parity
printf 'recording the Blender oracle under %s with %s\n' "$ORACLE_STORE" "$BLENDER"
exec bash "$ROOT/tools/check.sh"
