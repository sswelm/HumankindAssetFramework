#!/bin/bash
# Time the in-process deploy conversion of ONE recorded job (a model the Factory converted: its
# Assets/FactorySource/<model>/deploy_converted.args.txt) under Unity's bundled 64-bit Mono, outside the editor,
# and print the port's phase laps (BlenderDeployInProcess.Outcome.Timing) - "measure, never estimate".
#
#   bash tools/deploy_time.sh UniversalTanks [<oracle.glb>]
#
# Without an oracle the conversion's own output is the oracle (the run then PASSes trivially: this script measures,
# tools/deploy_drill.sh judges). With Blender's GLB for the same job (the Factory's deploy_converted.glb when it was
# written by Blender, or tools/deploy-drill/blender_export_dump.py's) the bytes are compared as the drill compares them.
# The same compile as the drill (its file list, read from deploy_drill.sh so the two cannot drift), in C:/tmp/haf-build.
set -u
MODEL="${1:?usage: deploy_time.sh <FactorySource model> [oracle.glb]}"; ORACLE="${2:-}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; WROOT=$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT"); E="$WROOT/editor"
PROJECT="${HAF_PROJECT:-/c/Repo/ENCReload}"
eval "$(grep -m1 '^UNITY=' "$ROOT/tools/deploy_drill.sh")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"; WAPI=$(cygpath -m "$API" 2>/dev/null || echo "$API")
for f in "$CSC" "$API/mscorlib.dll" "$UNITY/MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f (set UNITY=<Unity .../Editor/Data>)"; exit 2; }
done
ARGS="$PROJECT/Assets/FactorySource/$MODEL/deploy_converted.args.txt"
[ -f "$ARGS" ] || { echo "FAIL — no recorded conversion at $ARGS (bake the model with Deploy conversion on, or set HAF_PROJECT)"; exit 2; }
NEWTONSOFT=""
for cand in "$ROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }; done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs)"; exit 2; }
B="${HAF_BUILD:-/c/tmp/haf-build}/deploy_time"; rm -rf "$B/oracle" "$B/out"; mkdir -p "$B/oracle" "$B/out"; WTMP=$(cygpath -m "$B" 2>/dev/null || echo "$B"); TMPD="$B"
cp "$NEWTONSOFT" "$B/Newtonsoft.Json.dll"; cp "$ROOT/editor/Plugins/x86_64/hafkernels.dll" "$B/hafkernels.dll"
# the drill's own compile commands (deploy.exe, then the 64-bit Mono bootstrap), as written there
compile_lines() { awk "/^OUT=\\\$\\(dotnet \"\\\$CSC\" .*$1/,/rc=\\\$\\?\$/" "$ROOT/tools/deploy_drill.sh"; }
eval "$(compile_lines 'deploy\.exe')"
if [ "$rc" -ne 0 ] || [ ! -s "$B/deploy.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — deploy.exe did not compile (rc=$rc)"; exit 1; fi
eval "$(compile_lines 'mono-host\.exe')"
if [ "$rc" -ne 0 ] || [ ! -s "$B/mono-host.exe" ]; then echo "$OUT"; echo "FAIL — the Mono bootstrap did not compile (rc=$rc)"; exit 1; fi
line=$(head -1 "$ARGS" | tr -d '\r'); src=${line%%|*}; rest=$(printf '%s' "$line" | cut -d'|' -f4-)
[ -f "$src" ] || { echo "FAIL — the recorded source is gone: $src"; exit 2; }
printf '%s|%s|%s\n' "$MODEL" "$src" "$rest" > "$B/jobs.txt"
if [ -n "$ORACLE" ]; then cp "$ORACLE" "$B/oracle/$MODEL.glb"; else
  # no oracle given: convert once for the bytes (against an empty oracle: the host writes before it compares), then time against them
  : > "$B/oracle/$MODEL.glb"
  "$B/mono-host.exe" "$UNITY/MonoBleedingEdge" "$WTMP/deploy.exe" --in-process "$WTMP/jobs.txt" "$WTMP/oracle" "$WTMP/out" > "$B/first.txt" 2>&1
  [ -s "$B/out/$MODEL.glb" ] || { cat "$B/first.txt"; echo "FAIL — the first conversion wrote nothing"; exit 1; }
  mv "$B/out/$MODEL.glb" "$B/oracle/$MODEL.glb"
fi
echo "JOB $(cat "$B/jobs.txt")"
t0=$(date +%s%N)
"$B/mono-host.exe" "$UNITY/MonoBleedingEdge" "$WTMP/deploy.exe" --in-process "$WTMP/jobs.txt" "$WTMP/oracle" "$WTMP/out" 2>&1 | LC_ALL=C sed 's/\xEF\xBB\xBF//g' | tr -d '\r'
t1=$(date +%s%N); echo "WALL $(( (t1 - t0) / 1000000 )) ms (Mono start-up and the oracle read included)"
