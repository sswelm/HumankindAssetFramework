#!/usr/bin/env bash
# backup_dedup_drill.sh - the backup dedup, drilled on REAL files (PR #105, round 6).
#
# The real editor/BackupDedup.cs + editor/EditorRules.cs, compiled with Unity's Roslyn and run on Unity's Mono over a
# scratch tree (tools/backup-dedup-drill/Drill.cs): "unchanged" must mean the bytes. A file rewritten with new bytes
# under the same size and the same last-write time (to the tick) is copied, not linked; identical bytes hard-link;
# two snapshots of the same bytes sign the same; a previous snapshot without a content index links nothing.
# Same prerequisites and shape as registry_engine_drill.sh. Exit 0 = every check passed.
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"
MONO="$UNITY/MonoBleedingEdge/bin/mono.exe"
API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$MONO" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; backup dedup NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (backup dedup NOT drilled)"; exit 2; }
NEWTONSOFT=""   # EditorRules.cs references Newtonsoft in one helper; the drill never calls it
for cand in "$WROOT/References/Newtonsoft.Json.dll" "C:/Repo/ENCReload/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; backup dedup NOT drilled)"; exit 2; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"
WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/drill.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/backup-dedup-drill/Drill.cs" "$WROOT/tools/backup-dedup-drill/Stubs.cs" \
  "$WROOT/editor/BackupDedup.cs" "$WROOT/editor/EditorRules.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/drill.exe" ]; then
  echo "$OUT" | grep -E "error" | head -20
  echo "FAIL — the backup dedup drill did not compile (csc rc=$rc)"
  exit 1
fi
RESULT=$("$MONO" "$TMPD/drill.exe" 2>&1); rc=$?
RESULT=$(printf '%s' "$RESULT" | sed 's/^\xEF\xBB\xBF//')   # Mono emits a UTF-8 BOM ahead of the first line under the push hook, which hid that line from the count
echo "$RESULT" | grep -v "^PASS "
n_pass=$(echo "$RESULT" | grep -c "^PASS ")
if [ "$rc" -ne 0 ]; then echo "FAIL — backup dedup drill ($n_pass passed)"; exit 1; fi
echo "PASS — backup dedup drill: $n_pass checks against the real BackupDedup on Unity's Mono"
