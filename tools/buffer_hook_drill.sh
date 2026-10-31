#!/usr/bin/env bash
# Real Harmony adapters on engine-shaped CPU records, isolated from the xUnit host and user/game state.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TASK_TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)" || exit 2
TMPD="$(mktemp -d "$TASK_TEMP_ROOT/haf-buffer-hook.XXXXXXXXXX")" || exit 2
cleanup() {
  local resolved="$(cd "$TMPD" && pwd -P)"
  case "$resolved" in "$TASK_TEMP_ROOT"/haf-buffer-hook.*) rm -rf -- "$resolved";; *) echo 'FAIL — unexpected cleanup path';; esac
}
trap cleanup EXIT
PLUGIN="${HAF_BUFFER_PLUGIN:-$ROOT/bin/Release/HumankindAssetFramework.dll}"
dotnet build "$ROOT/tools/buffer-hook-drill/BufferHookDrill.csproj" -c Release --nologo -v q || exit 1
cp "$ROOT"/References/*.dll "$TMPD/" || exit 2
cp "$PLUGIN" "$TMPD/HumankindAssetFramework.dll" || exit 2
cp "$ROOT/bin/Release/Haf.Schema.dll" "$TMPD/" || exit 2
WTMP="$(cygpath -m "$TMPD")"
cp "$ROOT/tools/buffer-hook-drill/bin/Release/BufferHookDrill.exe" "$TMPD/" || exit 2
[ ! -f "$ROOT/tools/buffer-hook-drill/bin/Release/BufferHookDrill.exe.config" ] || cp "$ROOT/tools/buffer-hook-drill/bin/Release/BufferHookDrill.exe.config" "$TMPD/" || exit 2
"$TMPD/BufferHookDrill.exe" "$WTMP"
