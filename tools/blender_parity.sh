#!/usr/bin/env bash
# blender_parity.sh - the nine Blender-parity drills, with Blender, outside the push (2026-10-30; user: "for pushes to
# github it should no longer use any blender!!! ... it should only be part of the bake test").
#
# The drills hold the C# ports to Blender's own output: the GLB reader and writer, the Workshop's compaction, the
# vehicle probe, the mesh layout, the prep, the deploy conversion, rig_anim and vehicle_rig - every row, byte and folder
# Blender itself produces against what the port produces. They ran on every push until 2026-10-28, then replayed a
# RECORD of Blender that went stale with every script change and asked for Blender again to refresh it. They are no
# longer part of the push at all: this script is the gate (tools/check.sh) in its parity mode, HAF_GATE=parity, which
# runs ONLY those drills, with Blender (HAF_ORACLE=live), under the gate's lock, step limits and PROGRESS notes. The
# Bake Tests row "Do the C# ports still match Blender?" runs exactly this from Unity.
#
# Run it after changing a script under editor/Tools~, a port under editor/ that one of the drills covers, a saved recipe,
# a registry source or a drill's fixtures. About twenty minutes, Blender's share included. Blender is found like
# UniversalBaker.FindBlender (newest "Blender*" under Program Files); BLENDER=<exe> overrides. HAF_ORACLE=record or
# replay, set explicitly, still store or replay a record (tools/blender_oracle.sh) for an offline rerun; nothing in the
# push reads one.
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export HAF_GATE=parity HAF_ORACLE="${HAF_ORACLE:-live}"
if [ "$HAF_ORACLE" != replay ]; then
  . "$ROOT/tools/blender_oracle.sh"
  oracle_find_blender
  if [ -z "$BLENDER" ] || [ ! -x "$BLENDER" ]; then echo "FAIL — no Blender found (BLENDER=<exe>); the parity drills need the other side"; exit 2; fi
  export BLENDER
  printf 'the parity drills with %s (HAF_ORACLE=%s)\n' "$BLENDER" "$HAF_ORACLE"
fi
exec bash "$ROOT/tools/check.sh"
