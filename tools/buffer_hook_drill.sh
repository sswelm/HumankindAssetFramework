#!/usr/bin/env bash
# Real Harmony adapters on engine-shaped CPU records, isolated from the xUnit host and user/game state.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
[ -f "$CSC" ] || { echo 'SKIP — Unity compiler missing; buffer hook not exercised'; exit 3; }
TASK_TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)" || exit 2
TMPD="$(mktemp -d "$TASK_TEMP_ROOT/haf-buffer-hook.XXXXXXXXXX")" || exit 2
cleanup() {
  local resolved="$(cd "$TMPD" && pwd -P)"
  case "$resolved" in "$TASK_TEMP_ROOT"/haf-buffer-hook.*) rm -rf -- "$resolved";; *) echo 'FAIL — unexpected cleanup path';; esac
}
trap cleanup EXIT
PLUGIN="${HAF_BUFFER_PLUGIN:-$ROOT/bin/Release/HumankindAssetFramework.dll}"
if [ -z "${HAF_BUFFER_PLUGIN:-}" ]; then dotnet build "$ROOT/HumankindAssetFramework.csproj" -c Release --nologo -v q || exit 1; fi
cp "$ROOT"/References/*.dll "$TMPD/" || exit 2
cp "$PLUGIN" "$TMPD/HumankindAssetFramework.dll" || exit 2
cp "$ROOT/bin/Release/Haf.Schema.dll" "$TMPD/" || exit 2
WTMP="$(cygpath -m "$TMPD")"; WAPI="$(cygpath -m "$API")"; WROOT="$(cygpath -m "$ROOT")"
printf '%s\n' '-nologo' '-nostdlib+' '-langversion:9' '-target:exe' "-out:\"$WTMP/BufferHookDrill.exe\"" > "$TMPD/compile.rsp"
for dll in "$API/mscorlib.dll" "$API/System.dll" "$API/System.Core.dll" "$API/Facades/netstandard.dll" "$TMPD"/*.dll; do
  printf -- '-r:"%s"\n' "$(cygpath -m "$dll")" >> "$TMPD/compile.rsp"
done
printf '"%s/tools/buffer-hook-drill/BufferHookDrill.cs"\n' "$WROOT" >> "$TMPD/compile.rsp"
dotnet "$CSC" "@$WTMP/compile.rsp" || exit 1
"$TMPD/BufferHookDrill.exe" "$WTMP"
