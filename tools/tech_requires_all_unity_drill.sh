#!/usr/bin/env bash
# Drive the real Tech Requires All window in a temporary Unity editor project: add, save, reload, remove, and read
# every deployed file back with the PLUGIN's parser. The game's config folder is a temp folder (the registry's seam);
# nothing under the Steam install or the modding project is touched. Not in the push gate (a Unity boot).
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
EDITOR="${UNITY_EDITOR:-$UNITY/../Unity.exe}"
[ -f "$EDITOR" ] || { echo 'SKIP — Unity missing; the Tech Requires All window was NOT tested'; exit 3; }
TASK_TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)" || exit 2
TMPD="$(mktemp -d "$TASK_TEMP_ROOT/haf-tech-all.XXXXXXXXXX")" || exit 2
cleanup() {
  local result=$?
  if [ "$result" -ne 0 ]; then echo "EVIDENCE retained $TMPD"; return 0; fi
  [ -d "$TMPD" ] || return 0
  local resolved="$(cd "$TMPD" && pwd -P)"
  case "$resolved" in
    "$TASK_TEMP_ROOT"/haf-tech-all.*)
      local attempt
      for ((attempt=0; attempt<30; attempt++)); do rm -rf -- "$resolved" 2>/dev/null && return 0; sleep 1; done
      echo "FAIL — could not clean the runner project: $resolved" >&2; return 1;;
    *) echo "FAIL — refusing cleanup outside the runner temp root: $resolved"; return 1;;
  esac
}
trap cleanup EXIT
echo "EVIDENCE $TMPD"
dotnet build "$ROOT/HumankindAssetFramework.csproj" -c Release --nologo -v q -p:WarningLevel=0 > "$TMPD/build.log" 2>&1
rc=$?; [ "$rc" -eq 0 ] || { cat "$TMPD/build.log"; exit 1; }
WROOT="$(cygpath -m "$ROOT")"; WTMP="$(cygpath -m "$TMPD")"; WUNITY="$(cygpath -m "$UNITY")"
mkdir -p "$TMPD/project/Assets/Editor" "$TMPD/project/Assets/Plugins" "$TMPD/project/Packages" "$TMPD/project/ProjectSettings" "$TMPD/config"
printf '%s\n' '{"dependencies":{}}' > "$TMPD/project/Packages/manifest.json"
printf '%s\n' 'm_EditorVersion: 2021.3.1f1' > "$TMPD/project/ProjectSettings/ProjectVersion.txt"
# the plugin DLL (its parser reads the deployed file back) beside the editor package's own schema DLL
cp "$ROOT/bin/Release/HumankindAssetFramework.dll" "$ROOT/editor/Plugins/Haf.Schema.dll" "$TMPD/project/Assets/Plugins/"
for name in BepInEx 0Harmony Mono.Cecil MonoMod.RuntimeDetour MonoMod.Utils Newtonsoft.Json; do cp "$ROOT/References/$name.dll" "$TMPD/project/Assets/Plugins/" || exit 2; done
WJSON="$(cygpath -m "$ROOT/References/Newtonsoft.Json.dll")"
sed -e "s|@UNITY@|$WUNITY|g" -e "s|@ROOT@|$WROOT|g" -e "s|@NEWTONSOFT@|$WJSON|g" -e "s|@OUT@|$WTMP/project/Assets/Editor/TechRequireAllDrill.dll|g" "$ROOT/tools/editor_compile_check.rsp.in" > "$TMPD/compile.rsp"
for f in "$ROOT"/editor/*.cs; do printf '"%s/editor/%s"\n' "$WROOT" "${f##*/}" >> "$TMPD/compile.rsp"; done
printf '"%s/tools/tech-requires-all-drill/TechRequireAllWindowDrill.cs"\n' "$WROOT" >> "$TMPD/compile.rsp"
dotnet "$UNITY/DotNetSdkRoslyn/csc.dll" "@$WTMP/compile.rsp" > "$TMPD/compile.log" 2>&1
rc=$?; [ "$rc" -eq 0 ] || { cat "$TMPD/compile.log"; echo 'FAIL — the window harness did not compile'; exit 1; }
# A graphics-enabled editor is needed to dispatch window events. Start it hidden; it exits from the drill, with a
# bounded wait that terminates only this runner's process if initialization prevents the drill from starting.
HAF_TA_EDITOR="$(cygpath -m "$EDITOR")" HAF_TA_PROJECT="$WTMP/project" HAF_TA_OUTPUT="$WTMP/result.txt" HAF_TA_CONFIG="$WTMP/config" HAF_TA_LOG="$WTMP/unity.log" powershell -NoProfile -Command '
  $runArguments = @("-batchmode", "-projectPath", ("`"" + $env:HAF_TA_PROJECT + "`""), "-executeMethod", "TechRequireAllWindowDrill.Run", "-hafTechAllOutput", ("`"" + $env:HAF_TA_OUTPUT + "`""), "-hafTechAllConfig", ("`"" + $env:HAF_TA_CONFIG + "`""), "-logFile", ("`"" + $env:HAF_TA_LOG + "`""))
  $runner = Start-Process -FilePath $env:HAF_TA_EDITOR -ArgumentList $runArguments -WindowStyle Hidden -PassThru
  if (!$runner.WaitForExit(240000)) { Stop-Process -Id $runner.Id -Force; Write-Error "Tech Requires All window runner timed out"; exit 1 }
  exit $runner.ExitCode
'
rc=$?
[ -f "$TMPD/result.txt" ] || { tail -40 "$TMPD/unity.log"; echo 'FAIL — the window harness did not run'; exit 1; }
cat "$TMPD/result.txt"
[ "$rc" -eq 0 ] && grep -q '^TOTAL failed 0' "$TMPD/result.txt" || { echo "FAIL — Tech Requires All window (rc=$rc)"; exit 1; }
echo 'PASS — Tech Requires All window under Unity'
