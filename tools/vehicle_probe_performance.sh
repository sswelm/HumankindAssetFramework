#!/usr/bin/env bash
# One warm-up + five read/probe runs, with hashes of every output mesh buffer. Run without a competing gate.
# HAF_PROBE_PERF_UNITY=1 runs in an isolated Unity editor project; default is the x64 .NET Framework host.
# HAF_PROBE_PERF_OUT=<new directory> retains binaries/logs, including for comparisons with another checkout.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
if [ "$#" -eq 0 ]; then echo "Usage: bash tools/vehicle_probe_performance.sh <model.glb> [...]" >&2; exit 2; fi
OUT="${HAF_PROBE_PERF_OUT:-$(mktemp -d)}"
mkdir -p "$OUT"
WROOT="$(cygpath -m "$ROOT")"; WOUT="$(cygpath -m "$OUT")"
sources=(HafModel GlbReader HafTransforms BlenderNames VehicleProbe VehicleProbe.Visibility VehicleProbe.Islands
  VehicleProbe.InsideOut VehicleProbe.BlenderWorld VehicleProbe.BlenderSkin VehicleProbe.CustomNormals VehicleProbe.Merge
  VehicleProbe.BlenderArmature BlenderEigen BlenderTrig)
args=(-nologo -noconfig -nostdlib -optimize+ -platform:x64)
for ref in mscorlib System System.Core System.Numerics; do args+=("-r:$API/$ref.dll"); done
args+=("-r:$API/Facades/netstandard.dll" "-r:$WROOT/References/Newtonsoft.Json.dll")
files=("$WROOT/tools/vehicle-probe-drill/ProbePerformance.cs")
for source in "${sources[@]}"; do files+=("$WROOT/editor/$source.cs"); done
cp "$ROOT/References/Newtonsoft.Json.dll" "$OUT/Newtonsoft.Json.dll"
if [ "${HAF_PROBE_PERF_UNITY:-0}" = 1 ]; then
  dotnet "$UNITY/DotNetSdkRoslyn/csc.dll" "${args[@]}" -target:library "-out:$WOUT/ProbePerformance.dll" "${files[@]}"
  mkdir -p "$OUT/project/Assets/Editor" "$OUT/project/Packages" "$OUT/project/ProjectSettings"
  cp "$OUT/ProbePerformance.dll" "$OUT/Newtonsoft.Json.dll" "$OUT/project/Assets/Editor/"
  printf '{"dependencies":{}}\n' > "$OUT/project/Packages/manifest.json"
  printf 'm_EditorVersion: 2021.3.1f1\n' > "$OUT/project/ProjectSettings/ProjectVersion.txt"
  "${UNITY%/Data}/Unity.exe" -batchmode -nographics -quit -projectPath "$WOUT/project" \
    -logFile "$WOUT/unity.log" -executeMethod ProbePerformance.RunUnity -- "$@"
  grep -q "^BENCHMARK PASS files=$#" "$OUT/unity.log"
  grep -E '^(RUNTIME|MEASURE|MEDIAN)' "$OUT/unity.log" | tee "$OUT/results.txt"
else
  dotnet "$UNITY/DotNetSdkRoslyn/csc.dll" "${args[@]}" "-out:$WOUT/ProbePerformance.exe" "${files[@]}"
  "$OUT/ProbePerformance.exe" "$@" | tee "$OUT/results.txt"
fi
echo "Probe benchmark retained in $WOUT"
