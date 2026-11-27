#!/usr/bin/env bash
# Generate after the source changed under a Probe (2026-11-26, the Skarv: the Model Fuser renamed Fused_P_Material2_11 to
# Fused_P_Material2_11_Part_001 and Generate stopped with "part not found"). Runs the real Vehicle Lab window in an
# isolated Unity project on a COPY of a real fused model: probe, mark, rewrite the source, Generate.
#   HAF_STALE_PROBE_MODEL  the fused .glb to copy (default: the Skarv). Missing -> SKIP (exit 3).
#   HAF_EDITOR_ROOT        the checkout whose editor/ is compiled (default: this one) - point it at master for the counterfactual.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
EROOT="${HAF_EDITOR_ROOT:-$ROOT}"
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
EDITOR="${UNITY_EDITOR:-$UNITY/../Unity.exe}"
MODEL="${HAF_STALE_PROBE_MODEL:-D:/3DModels/TurbineTorpedoBoat/hnoms_skarv_1906_split_fused.glb}"
[ -f "$EDITOR" ] || { echo 'SKIP — Unity missing; stale-probe Generate NOT tested'; exit 3; }
[ -f "$MODEL" ] || { echo "SKIP — model missing ($MODEL); stale-probe Generate NOT tested"; exit 3; }
TASK_TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)" || exit 2
TMPD="$(mktemp -d "$TASK_TEMP_ROOT/haf-vehiclelab-stale.XXXXXXXXXX")" || exit 2
cleanup() {
  local result=$?
  if [ "$result" -ne 0 ]; then echo "EVIDENCE retained $TMPD"; return 0; fi
  [ -d "$TMPD" ] || return 0
  local resolved="$(cd "$TMPD" && pwd -P)"
  case "$resolved" in
    "$TASK_TEMP_ROOT"/haf-vehiclelab-stale.*)
      local attempt
      for ((attempt=0; attempt<30; attempt++)); do rm -rf -- "$resolved" 2>/dev/null && return 0; sleep 1; done
      echo "FAIL — could not clean the runner project: $resolved" >&2; return 1;;
    *) echo "FAIL — refusing cleanup outside the runner temp root: $resolved"; return 1;;
  esac
}
trap cleanup EXIT
echo "EVIDENCE $TMPD"
WROOT="$(cygpath -m "$ROOT")"; WEROOT="$(cygpath -m "$EROOT")"; WTMP="$(cygpath -m "$TMPD")"; WUNITY="$(cygpath -m "$UNITY")"
mkdir -p "$TMPD/project/Assets/Editor" "$TMPD/project/Assets/Plugins/x86_64" "$TMPD/project/Packages" "$TMPD/project/ProjectSettings"
printf '%s\n' '{"dependencies":{}}' > "$TMPD/project/Packages/manifest.json"
printf '%s\n' 'm_EditorVersion: 2021.3.1f1' > "$TMPD/project/ProjectSettings/ProjectVersion.txt"
NEWTONSOFT="$ROOT/References/Newtonsoft.Json.dll"
[ -f "$NEWTONSOFT" ] || { echo 'FAIL — Newtonsoft.Json missing'; exit 2; }
cp "$NEWTONSOFT" "$TMPD/project/Assets/Plugins/Newtonsoft.Json.dll"
cp "$EROOT/editor/Plugins/Haf.Schema.dll" "$TMPD/project/Assets/Plugins/Haf.Schema.dll"
cp "$EROOT/editor/Plugins/x86_64/hafkernels.dll" "$EROOT/editor/Plugins/x86_64/hafkernels.dll.meta" "$TMPD/project/Assets/Plugins/x86_64/"
cp "$MODEL" "$TMPD/model.glb"   # a snapshot: the user may re-fuse while this runs
python "$ROOT/tools/vehiclerig-drill/vehiclerig_fixtures.py" "$WTMP/fixtures" > "$TMPD/fixtures.log" 2>&1 || { tail -5 "$TMPD/fixtures.log"; echo 'FAIL — the rig fixtures (a skinned source for the fast path) were not generated'; exit 1; }
[ -f "$TMPD/fixtures/models/fx_b_skinned.glb" ] || { echo 'FAIL — fx_b_skinned.glb missing from the rig fixtures'; exit 1; }
WJSON="$(cygpath -m "$NEWTONSOFT")"
sed -e "s|@UNITY@|$WUNITY|g" -e "s|@ROOT@|$WEROOT|g" -e "s|@NEWTONSOFT@|$WJSON|g" -e "s|@OUT@|$WTMP/project/Assets/Editor/HafStaleProbeTests.dll|g" "$ROOT/tools/editor_compile_check.rsp.in" > "$TMPD/compile.rsp"
for f in "$EROOT"/editor/*.cs; do printf '"%s/editor/%s"\n' "$WEROOT" "${f##*/}" >> "$TMPD/compile.rsp"; done
printf '"%s/tools/vehiclelab-stale-probe-drill/UnityVehicleLabStaleProbeTests.cs"\n' "$WROOT" >> "$TMPD/compile.rsp"
dotnet "$UNITY/DotNetSdkRoslyn/csc.dll" "@$WTMP/compile.rsp" > "$TMPD/compile.log" 2>&1
rc=$?; [ "$rc" -eq 0 ] || { cat "$TMPD/compile.log"; echo 'FAIL — stale-probe harness did not compile'; exit 1; }
"$EDITOR" -batchmode -nographics -projectPath "$WTMP/project" -executeMethod UnityVehicleLabStaleProbeTests.Run -hafStaleModel "$WTMP/model.glb" -hafStaleSkinned "$WTMP/fixtures/models/fx_b_skinned.glb" -hafStaleOutput "$WTMP/result.txt" -logFile "$WTMP/unity.log"
rc=$?
[ -f "$TMPD/result.txt" ] || { tail -30 "$TMPD/unity.log"; echo 'FAIL — stale-probe harness did not run'; exit 1; }
cat "$TMPD/result.txt"
[ "$rc" -eq 0 ] && grep -q '^TOTAL failed 0' "$TMPD/result.txt" || { echo "FAIL — Vehicle Lab Generate after a changed source (rc=$rc)"; exit 1; }
echo 'PASS — Vehicle Lab re-probes a changed source before Generate'
