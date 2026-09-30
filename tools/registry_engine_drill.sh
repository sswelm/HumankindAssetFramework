#!/usr/bin/env bash
# registry_engine_drill.sh — runs the REAL editor/SingleSourceRegistry.cs (the district, formation and sound registry
# engine) against real files, one scenario per row of its exit table (tools/registry-engine-drill/Drill.cs).
#
# Why a drill and not a unit test: the engine is Unity-bound (JsonUtility, EditorPrefs, AssetDatabase), so the xunit
# project can't compile it, and until 2026-09-30 it had no test at all — the critical review found it still read `{}`
# as an empty registry that one bake then wrote over the source and the deploy. Tiny stand-ins for those few Unity
# APIs (Stubs.cs) let the engine's own source run here, compiled with Unity's Roslyn and run on Unity's Mono — the
# runtime the editor actually uses.
#
# Needs a Unity 2021.3 install (like editor_compile_check.sh): an absent prerequisite is a loud FAIL, never a pass.
set -u
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"
MONO="$UNITY/MonoBleedingEdge/bin/mono.exe"
API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$MONO" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; registry engine NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (registry engine NOT drilled)"; exit 2; }
NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "C:/Repo/ENCReload/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; registry engine NOT drilled)"; exit 2; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"
WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/drill.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/registry-engine-drill/Drill.cs" "$WROOT/tools/registry-engine-drill/Stubs.cs" \
  "$WROOT/editor/SingleSourceRegistry.cs" "$WROOT/editor/EditorRules.cs" "$WROOT/editor/CheckedReplace.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/drill.exe" ]; then
  echo "$OUT" | grep -E "error" | head -20
  echo "FAIL — the drill did not compile (csc rc=$rc)"
  exit 1
fi
RESULT=$("$MONO" "$TMPD/drill.exe" 2>&1); rc=$?
RESULT=$(printf '%s' "$RESULT" | LC_ALL=C sed 's/\xEF\xBB\xBF//g')   # with no console attached (the push hook) Mono writes a UTF-8 BOM per Console stream ahead of the first line, which hid that line from the count
echo "$RESULT" | grep -v "^PASS "
n_pass=$(echo "$RESULT" | grep -c "^PASS ")
if [ "$rc" -ne 0 ]; then echo "FAIL — registry engine drill ($n_pass passed)"; exit 1; fi
echo "PASS — registry engine drill: $n_pass checks against the real SingleSourceRegistry on Unity's Mono"
