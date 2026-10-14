#!/usr/bin/env bash
# Focused Factory GLB import regressions in a separate Unity project, using the real editor code and native APIs.
# Requires an installed, licensed Unity editor and Blender. Does not open or edit the user's project.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
EDITOR="${UNITY_EDITOR:-$UNITY/../Unity.exe}"
[ -f "$EDITOR" ] || { echo "SKIP — Unity editor not installed (UNITY_EDITOR=<exe>); native rig import NOT tested"; exit 3; }
TASK_TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)" || exit 2
TMPD="$(mktemp -d "$TASK_TEMP_ROOT/haf-rig-import.XXXXXXXXXX")" || exit 2
cleanup() {
  [ -d "$TMPD" ] || return 0
  local resolved="$(cd "$TMPD" && pwd -P)"
  case "$resolved" in "$TASK_TEMP_ROOT"/haf-rig-import.*) rm -rf -- "$resolved";; *) echo "FAIL — refusing cleanup outside the rig-import temp root: $resolved";; esac
}
trap cleanup EXIT
WROOT="$(cygpath -m "$ROOT")"; WTMP="$(cygpath -m "$TMPD")"; WUNITY="$(cygpath -m "$UNITY")"
NEWTONSOFT="$ROOT/References/Newtonsoft.Json.dll"
[ -f "$NEWTONSOFT" ] || NEWTONSOFT="C:/Repo/ENCReload/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"
[ -f "$NEWTONSOFT" ] || { echo 'FAIL — Newtonsoft.Json is missing'; exit 2; }
mkdir -p "$TMPD/project/Assets/Editor" "$TMPD/project/Assets/Plugins" "$TMPD/project/Packages" "$TMPD/project/ProjectSettings"
printf '%s\n' '{"dependencies":{}}' > "$TMPD/project/Packages/manifest.json"
printf '%s\n' 'm_EditorVersion: 2021.3.1f1' > "$TMPD/project/ProjectSettings/ProjectVersion.txt"
cp "$NEWTONSOFT" "$TMPD/project/Assets/Plugins/Newtonsoft.Json.dll"
cp "$ROOT/editor/Plugins/Haf.Schema.dll" "$TMPD/project/Assets/Plugins/Haf.Schema.dll"
python - "$WROOT" "$WTMP/fixtures" <<'PYEOF'
import sys
sys.path.insert(0, sys.argv[1] + '/tools/riganim-drill')
from riganim_fixtures import unity_import_fixtures
unity_import_fixtures(sys.argv[2])
PYEOF
rc=$?; [ "$rc" -eq 0 ] || { echo 'FAIL — Unity rig fixtures could not be generated'; exit 1; }
# A real pair from the shipped slim script checks that the acceptance comparison also understands Unity's FBX importer.
BLENDER="${BLENDER:-/c/Program Files/Blender Foundation/Blender 5.1/blender.exe}"
[ -f "$BLENDER" ] || { echo 'SKIP — Blender missing; native cross-route comparison NOT tested'; exit 3; }
"$BLENDER" -b --python "$ROOT/editor/Tools~/rig_anim.py" -- "$WTMP/fixtures/plain.glb" "$WTMP/fixtures/route.fbx" 0 "" Spin "" 1 0,0,0 1 "" 0 "" 1 > "$TMPD/fbx.log" 2>&1
[ "$?" -eq 0 ] && [ -s "$TMPD/fixtures/route.fbx" ] || { cat "$TMPD/fbx.log"; echo 'FAIL — FBX fixture slim'; exit 1; }
"$BLENDER" -b --python "$ROOT/editor/Tools~/rig_anim_glb.py" -- "$WROOT/editor/Tools~/rig_anim.py" "$WTMP/fixtures/plain.glb" "$WTMP/fixtures/route.glb" 0 "" Spin "" 1 0,0,0 1 "" 0 "" 1 > "$TMPD/glb.log" 2>&1
[ "$?" -eq 0 ] && [ -s "$TMPD/fixtures/route.glb" ] || { cat "$TMPD/glb.log"; echo 'FAIL — GLB fixture slim'; exit 1; }
WJSON="$(cygpath -m "$NEWTONSOFT")"
sed -e "s|@UNITY@|$WUNITY|g" -e "s|@ROOT@|$WROOT|g" -e "s|@NEWTONSOFT@|$WJSON|g" -e "s|@OUT@|$WTMP/project/Assets/Editor/HafRigImportTests.dll|g" "$ROOT/tools/editor_compile_check.rsp.in" > "$TMPD/compile.rsp"
for f in "$ROOT"/editor/*.cs; do printf '"%s/editor/%s"\n' "$WROOT" "$(basename "$f")" >> "$TMPD/compile.rsp"; done
printf '"%s/tools/riganim-drill/UnityRigImportTests.cs"\n' "$WROOT" >> "$TMPD/compile.rsp"
dotnet "$UNITY/DotNetSdkRoslyn/csc.dll" "@$WTMP/compile.rsp" > "$TMPD/compile.log" 2>&1; rc=$?
[ "$rc" -eq 0 ] || { cat "$TMPD/compile.log"; echo 'FAIL — native rig import harness did not compile'; exit 1; }
# the Vehicle Lab's saved recipes (the window's rig arguments against the recipe reader's): the ENCReload project's, when present
RECIPES="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}/Assets/FactorySource/VehicleLab/Recipes"; [ -d "$RECIPES" ] || RECIPES=""
"$EDITOR" -batchmode -nographics -projectPath "$WTMP/project" -executeMethod UnityRigImportTests.Run -hafRigImportFixtures "$WTMP/fixtures" -hafRigImportOutput "$WTMP/result.txt" -hafRecipes "$(cygpath -m "$RECIPES" 2>/dev/null || echo "$RECIPES")" -logFile "$WTMP/unity.log"; rc=$?
[ -f "$TMPD/result.txt" ] || { tail -30 "$TMPD/unity.log"; echo 'FAIL — Unity did not execute the rig import regressions'; exit 1; }
cat "$TMPD/result.txt"
[ "$rc" -eq 0 ] && grep -q '^TOTAL failed 0' "$TMPD/result.txt" || { echo "FAIL — native Unity rig import regressions (rc=$rc)"; exit 1; }
echo 'PASS — native Unity rig import: bone and material identities, atlas rects, role subassets and previews, complete action names, scale and clip time'
