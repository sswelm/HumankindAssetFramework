#!/usr/bin/env bash
# Exercise the real Bake Tests runner in an isolated Unity project, with and without the native loader enabled.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
EDITOR="${UNITY_EDITOR:-$UNITY/../Unity.exe}"
[ -f "$EDITOR" ] || { echo 'SKIP — Unity missing; Bake Tests runner NOT executed'; exit 3; }
TASK_TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)" || exit 2
TMPD="$(mktemp -d "$TASK_TEMP_ROOT/haf-bake-kernels.XXXXXXXXXX")" || exit 2
cleanup() {
  [ -d "$TMPD" ] || return 0
  local resolved="$(cd "$TMPD" && pwd -P)"
  case "$resolved" in "$TASK_TEMP_ROOT"/haf-bake-kernels.*) rm -rf -- "$resolved";; *) echo "FAIL — refusing cleanup outside the runner temp root: $resolved";; esac
}
trap cleanup EXIT
WROOT="$(cygpath -m "$ROOT")"; WTMP="$(cygpath -m "$TMPD")"; WUNITY="$(cygpath -m "$UNITY")"
mkdir -p "$TMPD/project/Assets/Editor" "$TMPD/project/Assets/Plugins/x86_64" "$TMPD/project/Packages" "$TMPD/project/ProjectSettings"
printf '%s\n' '{"dependencies":{}}' > "$TMPD/project/Packages/manifest.json"
printf '%s\n' 'm_EditorVersion: 2021.3.1f1' > "$TMPD/project/ProjectSettings/ProjectVersion.txt"
NEWTONSOFT="$ROOT/References/Newtonsoft.Json.dll"
[ -f "$NEWTONSOFT" ] || { echo 'FAIL — Newtonsoft.Json missing'; exit 2; }
cp "$NEWTONSOFT" "$TMPD/project/Assets/Plugins/Newtonsoft.Json.dll"
cp "$ROOT/editor/Plugins/Haf.Schema.dll" "$TMPD/project/Assets/Plugins/Haf.Schema.dll"
cp "$ROOT/editor/Plugins/x86_64/hafkernels.dll" "$TMPD/project/Assets/Plugins/x86_64/hafkernels.dll"
WJSON="$(cygpath -m "$NEWTONSOFT")"
sed -e "s|@UNITY@|$WUNITY|g" -e "s|@ROOT@|$WROOT|g" -e "s|@NEWTONSOFT@|$WJSON|g" -e "s|@OUT@|$WTMP/project/Assets/Editor/HafBakeKernelTests.dll|g" "$ROOT/tools/editor_compile_check.rsp.in" > "$TMPD/compile.rsp"
for f in "$ROOT"/editor/*.cs; do printf '"%s/editor/%s"\n' "$WROOT" "$(basename "$f")" >> "$TMPD/compile.rsp"; done
printf '"%s/tools/bake-tests-kernel-drill/UnityKernelRunnerTests.cs"\n' "$WROOT" >> "$TMPD/compile.rsp"
dotnet "$UNITY/DotNetSdkRoslyn/csc.dll" "@$WTMP/compile.rsp" > "$TMPD/compile.log" 2>&1
rc=$?; [ "$rc" -eq 0 ] || { cat "$TMPD/compile.log"; echo 'FAIL — runner harness did not compile'; exit 1; }
failed=0
for enabled in 1 0; do
  HAF_NATIVE_KERNELS="$enabled" HAF_KERNELS_DLL="$WTMP/project/Assets/Plugins/x86_64/hafkernels.dll" "$EDITOR" -batchmode -nographics -projectPath "$WTMP/project" -executeMethod UnityKernelRunnerTests.Run -hafKernelOutput "$WTMP/result_$enabled.txt" -logFile "$WTMP/unity_$enabled.log"
  rc=$?
  if [ ! -f "$TMPD/result_$enabled.txt" ]; then tail -30 "$TMPD/unity_$enabled.log"; echo "FAIL — runner not executed (native enabled=$enabled)"; failed=1; continue; fi
  cat "$TMPD/result_$enabled.txt"
  if [ "$rc" -ne 0 ] || ! grep -q '^TOTAL failed 0' "$TMPD/result_$enabled.txt"; then failed=1; fi
done
[ "$failed" -eq 0 ] || { echo 'FAIL — native Unity Bake Tests kernel runner'; exit 1; }
echo 'PASS — native Unity Bake Tests kernel runner: mode selection, process state, row labels, failure, cancellation and reports'
