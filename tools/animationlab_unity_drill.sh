#!/usr/bin/env bash
# Animation Lab preview regressions in a fresh Unity project; no Blender, game or user project is modified.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
EDITOR="${UNITY_EDITOR:-$UNITY/../Unity.exe}"
[ -f "$EDITOR" ] || { echo 'SKIP — Unity editor missing; Animation Lab previews NOT tested'; exit 3; }
TASK_TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)" || exit 2
TMPD="$(mktemp -d "$TASK_TEMP_ROOT/haf-animationlab.XXXXXXXXXX")" || exit 2
cleanup() {
  [ -d "$TMPD" ] || return 0
  local resolved="$(cd "$TMPD" && pwd -P)"
  case "$resolved" in
    "$TASK_TEMP_ROOT"/haf-animationlab.*)
      local attempt
      for attempt in 1 2 3 4 5; do rm -rf -- "$resolved" 2>/dev/null && return 0; sleep 1; done
      echo "FAIL — could not clean the Animation Lab runner: $resolved" >&2; return 1;;
    *) echo 'FAIL — refusing cleanup outside the Animation Lab runner root' >&2; return 1;;
  esac
}
trap cleanup EXIT
mkdir -p "$TMPD/project/Assets/Editor" "$TMPD/project/Assets/Plugins" "$TMPD/project/Packages" "$TMPD/project/ProjectSettings"
printf '%s\n' '{"dependencies":{}}' > "$TMPD/project/Packages/manifest.json"
printf '%s\n' 'm_EditorVersion: 2021.3.1f1' > "$TMPD/project/ProjectSettings/ProjectVersion.txt"
NEWTONSOFT="$ROOT/References/Newtonsoft.Json.dll"
[ -f "$NEWTONSOFT" ] || { echo 'FAIL — References/Newtonsoft.Json.dll missing'; exit 2; }
cp "$NEWTONSOFT" "$TMPD/project/Assets/Plugins/Newtonsoft.Json.dll"
cp "$ROOT/editor/Plugins/Haf.Schema.dll" "$TMPD/project/Assets/Plugins/Haf.Schema.dll"
WROOT="$(cygpath -m "$ROOT")"; WTMP="$(cygpath -m "$TMPD")"; WUNITY="$(cygpath -m "$UNITY")"; WJSON="$(cygpath -m "$NEWTONSOFT")"
sed -e "s|@UNITY@|$WUNITY|g" -e "s|@ROOT@|$WROOT|g" -e "s|@NEWTONSOFT@|$WJSON|g" -e "s|@OUT@|$WTMP/project/Assets/Editor/AnimationLabTests.dll|g" "$ROOT/tools/editor_compile_check.rsp.in" > "$TMPD/compile.rsp"
for f in "$ROOT"/editor/*.cs; do printf '"%s/editor/%s"\n' "$WROOT" "${f##*/}" >> "$TMPD/compile.rsp"; done
printf '"%s/tools/animationlab-drill/UnityAnimationLabTests.cs"\n' "$WROOT" >> "$TMPD/compile.rsp"
dotnet "$UNITY/DotNetSdkRoslyn/csc.dll" "@$WTMP/compile.rsp" > "$TMPD/compile.log" 2>&1; rc=$?
[ "$rc" -eq 0 ] || { cat "$TMPD/compile.log"; echo 'FAIL — Animation Lab runner did not compile'; exit 1; }
"$EDITOR" -batchmode -nographics -projectPath "$WTMP/project" -executeMethod UnityAnimationLabTests.Run -hafPreviewOutput "$WTMP/result.txt" -logFile "$WTMP/unity.log"; rc=$?
[ -f "$TMPD/result.txt" ] || { tail -35 "$TMPD/unity.log"; echo 'FAIL — Unity did not run Animation Lab regressions'; exit 1; }
cat "$TMPD/result.txt"
[ "$rc" -eq 0 ] && grep -q '^TOTAL failed 0$' "$TMPD/result.txt" || { echo "FAIL — Animation Lab preview regressions (rc=$rc)"; exit 1; }
echo 'PASS — Animation Lab: safe atlas clones, shared skin lifetime, read fallback and runtime pixel math'
