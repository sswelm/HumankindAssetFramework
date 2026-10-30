#!/usr/bin/env bash
# parity_row_unity_drill.sh — the Bake Tests row "Do the C# ports still match Blender?" run from a REAL Unity editor
# process (batch mode, a throwaway project with the editor sources compiled in), the way the window runs it: the row
# starts Git's bash on tools/check.sh in its parity mode from Unity's own environment, and the drill reads the section
# back. Blender runs (about twenty minutes); the gate's lock is taken. SKIP without Unity.
#
#   bash tools/parity_row_unity_drill.sh [--self-test]  # verdict/cancellation regressions only; no Blender
#
# The throwaway project compiles editor/ into a DLL under Assets/Editor, so the package lookup the row uses for the
# repository (PackageInfo.FindForAssembly) finds no package there: HAF_REPO_ROOT names this checkout instead, as it may
# for any copy of the editor sources that is not the repository's editor/ folder.
set -uo pipefail
METHOD=Run
case "${1:-}" in '') ;; --self-test) METHOD=RunSelfTests;; *) echo 'FAIL — expected no argument or --self-test'; exit 2;; esac
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
EDITOR="${UNITY_EDITOR:-$UNITY/../Unity.exe}"
[ -f "$EDITOR" ] || { echo 'SKIP — Unity missing; the parity row was NOT executed'; exit 3; }
TASK_TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)" || exit 2
TMPD="$(mktemp -d "$TASK_TEMP_ROOT/haf-parity-row.XXXXXXXXXX")" || exit 2
cleanup() {
  [ -d "$TMPD" ] || return 0
  local resolved="$(cd "$TMPD" && pwd -P)"
  case "$resolved" in
    "$TASK_TEMP_ROOT"/haf-parity-row.*)
      # Unity can release its log handle just after the launcher exits on Windows.
      local attempt
      for attempt in 1 2 3 4 5; do rm -rf -- "$resolved" 2>/dev/null && return 0; sleep 1; done
      echo "FAIL — could not clean the runner project: $resolved" >&2; return 1;;
    *) echo "FAIL — refusing cleanup outside the runner temp root: $resolved"; return 1;;
  esac
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
sed -e "s|@UNITY@|$WUNITY|g" -e "s|@ROOT@|$WROOT|g" -e "s|@NEWTONSOFT@|$WJSON|g" -e "s|@OUT@|$WTMP/project/Assets/Editor/HafParityRow.dll|g" "$ROOT/tools/editor_compile_check.rsp.in" > "$TMPD/compile.rsp"
for f in "$ROOT"/editor/*.cs; do printf '"%s/editor/%s"\n' "$WROOT" "$(basename "$f")" >> "$TMPD/compile.rsp"; done
printf '"%s/tools/parity-row-drill/UnityParityRowDrill.cs"\n' "$WROOT" >> "$TMPD/compile.rsp"
dotnet "$UNITY/DotNetSdkRoslyn/csc.dll" "@$WTMP/compile.rsp" > "$TMPD/compile.log" 2>&1
rc=$?; [ "$rc" -eq 0 ] || { cat "$TMPD/compile.log"; echo 'FAIL — the row harness did not compile'; exit 1; }
# the modding project the drills read (recipes, registry sources): the one the gate uses by default
HAF_REPO_ROOT="$WROOT" HAF_UNITY_PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}" "$EDITOR" -batchmode -nographics -projectPath "$WTMP/project" -executeMethod "UnityParityRowDrill.$METHOD" -hafParityOutput "$WTMP/result.txt" -logFile "$WTMP/unity.log"
rc=$?
[ -f "$TMPD/result.txt" ] || { tail -30 "$TMPD/unity.log"; echo 'FAIL — the row did not run (no result written)'; exit 1; }
cat "$TMPD/result.txt"
if [ "$METHOD" = RunSelfTests ]; then
  [ "$rc" -eq 0 ] && ! grep -q '^FAIL ' "$TMPD/result.txt" && grep -q '^PASS ' "$TMPD/result.txt"
  exit $?
fi
if [ "$rc" -ne 0 ] || ! grep -q '^FAIL 0$' "$TMPD/result.txt" || ! grep -q '^SKIP 0$' "$TMPD/result.txt"; then echo "FAIL — the parity row from Unity (exit $rc)"; exit 1; fi
echo 'PASS — the parity row from a real Unity process: the gate ran in its parity mode with Blender and every step passed'
