#!/usr/bin/env bash
# Vehicle Generate backend acceptance in a separate Unity project, using the real editor code and native APIs.
# Requires an installed, licensed Unity editor and Blender. Does not open or edit the user's project.
set -uo pipefail
METHOD="${HAF_GENERATE_TEST_METHOD:-Run}"
case "$METHOD" in Run|RunChecks|RunPreview|RunAvailability|RunPerformance|RunSaved) ;; *) echo "FAIL — unknown Vehicle Generate test method: $METHOD"; exit 2;; esac
# RunSaved: HAF_GENERATE_ONLY=a,b limits the saved recipes to those stems; HAF_GENERATE_REBLESS=a,b deletes those goldens from the
# drill's COPY of the project first, so they are captured from Blender and verified by a second generation - the captures are then
# left in $TMPD/rebless for the user to install (the drill never writes into the project; keep the folder with HAF_KEEP_UNITY_GENERATE=1)
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
EDITOR="${UNITY_EDITOR:-$UNITY/../Unity.exe}"
[ -f "$EDITOR" ] || { echo "SKIP — Unity editor not installed (UNITY_EDITOR=<exe>); native Vehicle Generate NOT tested"; exit 3; }
TASK_TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)" || exit 2
TMPD="$(mktemp -d "$TASK_TEMP_ROOT/haf-vehicle-generate.XXXXXXXXXX")" || exit 2
cleanup() {
  local result=$?
  if [ "$result" -ne 0 ]; then echo "EVIDENCE retained $TMPD"; return 0; fi
  [ -d "$TMPD" ] || return 0
  local resolved="$(cd "$TMPD" && pwd -P)"
  case "$resolved" in "$TASK_TEMP_ROOT"/haf-vehicle-generate.*) rm -rf -- "$resolved";; *) echo "FAIL — refusing cleanup outside the Vehicle Generate temp root: $resolved";; esac
}
if [ "${HAF_KEEP_UNITY_GENERATE:-0}" != 1 ]; then trap cleanup EXIT; fi
echo "EVIDENCE $TMPD"
WROOT="$(cygpath -m "$ROOT")"; WTMP="$(cygpath -m "$TMPD")"; WUNITY="$(cygpath -m "$UNITY")"
NEWTONSOFT="$ROOT/References/Newtonsoft.Json.dll"
[ -f "$NEWTONSOFT" ] || NEWTONSOFT="C:/Repo/ENCReload/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"
[ -f "$NEWTONSOFT" ] || { echo 'FAIL — Newtonsoft.Json is missing'; exit 2; }
mkdir -p "$TMPD/project/Assets/Editor" "$TMPD/project/Assets/Plugins" "$TMPD/project/Packages" "$TMPD/project/ProjectSettings"
printf '%s\n' '{"dependencies":{}}' > "$TMPD/project/Packages/manifest.json"
printf '%s\n' 'm_EditorVersion: 2021.3.1f1' > "$TMPD/project/ProjectSettings/ProjectVersion.txt"
cp "$NEWTONSOFT" "$TMPD/project/Assets/Plugins/Newtonsoft.Json.dll"
cp "$ROOT/editor/Plugins/Haf.Schema.dll" "$TMPD/project/Assets/Plugins/Haf.Schema.dll"
python "$ROOT/tools/vehiclerig-drill/vehiclerig_fixtures.py" "$WTMP/fixtures" > "$TMPD/fixtures.log" || exit 1
mkdir -p "$TMPD/project/Tools/lab_golden"
cp "$ROOT/editor/Tools~/vehicle_rig.py" "$TMPD/project/Tools/vehicle_rig.py"
if [ -d "${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}/Tools/lab_golden" ]; then cp "${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"/Tools/lab_golden/*.txt "$TMPD/project/Tools/lab_golden/"; fi
IFS=',' read -r -a REBLESS <<< "${HAF_GENERATE_REBLESS:-}"
for n in "${REBLESS[@]}"; do [ -n "$n" ] || continue; rm -f "$TMPD/project/Tools/lab_golden/$n.txt"; echo "REBLESS $n: its golden removed from the drill's copy - captured from Blender and verified below"; done
WJSON="$(cygpath -m "$NEWTONSOFT")"
sed -e "s|@UNITY@|$WUNITY|g" -e "s|@ROOT@|$WROOT|g" -e "s|@NEWTONSOFT@|$WJSON|g" -e "s|@OUT@|$WTMP/project/Assets/Editor/HafVehicleGenerateTests.dll|g" "$ROOT/tools/editor_compile_check.rsp.in" > "$TMPD/compile.rsp"
for f in "$ROOT"/editor/*.cs; do printf '"%s/editor/%s"\n' "$WROOT" "$(basename "$f")" >> "$TMPD/compile.rsp"; done
printf '"%s/tools/vehiclerig-drill/UnityVehicleGenerateTests.cs"\n' "$WROOT" >> "$TMPD/compile.rsp"
dotnet "$UNITY/DotNetSdkRoslyn/csc.dll" "@$WTMP/compile.rsp" > "$TMPD/compile.log" 2>&1; rc=$?
[ "$rc" -eq 0 ] || { cat "$TMPD/compile.log"; echo 'FAIL — native Vehicle Generate harness did not compile'; exit 1; }
# the Vehicle Lab's saved recipes (the window's rig arguments against the recipe reader's): the ENCReload project's, when present
RECIPES="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}/Assets/FactorySource/VehicleLab/Recipes"; [ -d "$RECIPES" ] || RECIPES=""
"$EDITOR" -batchmode -nographics -projectPath "$WTMP/project" -executeMethod "UnityVehicleGenerateTests.$METHOD" -hafGenerateFixtures "$WTMP/fixtures" -hafGenerateOutput "$WTMP/result.txt" -hafGenerateRecipes "$(cygpath -m "$RECIPES" 2>/dev/null || echo "$RECIPES")" -hafGeneratePerformanceModels "${HAF_GENERATE_PERFORMANCE_MODELS:-}" -hafGeneratePerformanceRuns "${HAF_GENERATE_PERFORMANCE_RUNS:-3}" -hafGenerateOnly "${HAF_GENERATE_ONLY:-}" -logFile "$WTMP/unity.log"; rc=$?
[ -f "$TMPD/result.txt" ] || { tail -30 "$TMPD/unity.log"; echo 'FAIL — Unity did not execute the Vehicle Generate acceptance'; exit 1; }
cat "$TMPD/result.txt"
[ "$rc" -eq 0 ] && grep -q '^TOTAL failed 0' "$TMPD/result.txt" || { echo "FAIL — native Unity Vehicle Generate acceptance (rc=$rc)"; exit 1; }
for n in "${REBLESS[@]}"; do
  [ -n "$n" ] || continue
  grep -q "^saved verified PASS $n:" "$TMPD/result.txt" || { echo "FAIL — the re-blessed golden $n was not verified by a second generation"; exit 1; }
  mkdir -p "$TMPD/rebless" && cp "$TMPD/project/Tools/lab_golden/$n.txt" "$TMPD/rebless/$n.txt" && echo "REBLESS $n: verified capture at $TMPD/rebless/$n.txt (sha256 $(sha256sum "$TMPD/rebless/$n.txt" | cut -c1-64))"
done
echo "PASS — native Unity Vehicle Generate $METHOD"
