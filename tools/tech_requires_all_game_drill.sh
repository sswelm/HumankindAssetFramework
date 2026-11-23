#!/usr/bin/env bash
# Drive the TechRequireAll hooks against the installed game assemblies. Call the game's state update loop,
# locked enqueue and cascading removal on a DepartmentOfScience built without its constructor. Replace the
# state update's live simulation reads and the leaf queue mutations with stubs. This drill needs an installed game.
# It runs twice: with a list, and with NO list file (the removal guard must hold then too: a save made with a list
# keeps its queue constraints).
#
#   bash tools/tech_requires_all_game_drill.sh [<Humankind .../Humankind_Data/Managed>]
#
# The Managed dir is arg 1, else $HK_MANAGED, else the default Steam path (as tools/check-bindings.sh).
# HAF_DRILL_MONO=<mono.exe> runs it a second time under that Mono.
# HAF_DRILL_PLUGIN_PREBUILT=1 (the push gate sets it) drills bin/Release/HumankindAssetFramework.dll as it stands and does
# not build the plugin: the gate built it in its first step, and the buffer hook drill builds the same project beside this
# one - two builds of one project at once can fail on each other's files.
set -uo pipefail
cd "$(dirname "$0")/.." && ROOT="$(pwd)" || exit 2
MANAGED="${1:-${HK_MANAGED:-/c/Program Files (x86)/Steam/steamapps/common/Humankind/Humankind_Data/Managed}}"
[ -d "$MANAGED" ] || { echo "SKIP — Managed dir not found: $MANAGED (the hooks were NOT drilled)"; exit 3; }
if [ "${HAF_DRILL_PLUGIN_PREBUILT:-0}" = 1 ]; then
  [ -s "$ROOT/bin/Release/HumankindAssetFramework.dll" ] || { echo 'FAIL — HAF_DRILL_PLUGIN_PREBUILT=1, but bin/Release/HumankindAssetFramework.dll is not there (build the plugin first)'; exit 1; }
else
  dotnet build "$ROOT/HumankindAssetFramework.csproj" -c Release --nologo -v q -p:WarningLevel=0 || { echo 'FAIL — plugin build'; exit 1; }
fi
dotnet build "$ROOT/tools/tech-requires-all-drill/TechRequireAllGameDrill.csproj" -c Release --nologo -v q || { echo 'FAIL — drill build'; exit 1; }
EXE="$ROOT/tools/tech-requires-all-drill/bin/Release/HumankindAssetFramework.Tests.exe"
WMANAGED="$(cygpath -m "$MANAGED" 2>/dev/null || echo "$MANAGED")"
"$EXE" "$WMANAGED"; rc=$?
[ "$rc" -eq 0 ] || { echo "FAIL — TechRequireAll game drill (rc=$rc)"; exit 1; }
# The removal guard must hold WITHOUT a list: a save made with one keeps its queue constraints. A run of its own,
# because the plugin reads the list once per process.
HAF_DRILL_STAGE=nolist "$EXE" "$WMANAGED"; rc=$?
[ "$rc" -eq 0 ] || { echo "FAIL — TechRequireAll game drill, no list file (rc=$rc)"; exit 1; }
if [ -n "${HAF_DRILL_MONO:-}" ]; then
  # Measured 2026-11-20 with the Unity editor's Mono 6.13: the process segfaults while EXITING once the game's
  # assemblies are loaded, with no hook applied and no HAF code run. So the exit code is judged against that
  # control: the same code as the load-only run, and the drill's own last line, decide.
  OUT="$(mktemp)"
  HAF_DRILL_STAGE=load "$HAF_DRILL_MONO" "$(cygpath -m "$EXE")" "$WMANAGED" > "$OUT" 2>&1; base=$?
  grep -q '^LOADED' "$OUT" || { cat "$OUT"; rm -f "$OUT"; echo 'FAIL — the game assemblies did not load under Mono'; exit 1; }
  for stage in full nolist; do   # "full" is no stage the drill knows: the whole run, with a list
    HAF_DRILL_STAGE="$stage" "$HAF_DRILL_MONO" "$(cygpath -m "$EXE")" "$WMANAGED" > "$OUT" 2>&1; rc=$?
    cat "$OUT"; last="$(grep -E '^DRILL (PASS|FAIL)' "$OUT" | tail -1)"
    [ "$last" = "DRILL PASS" ] || { rm -f "$OUT"; echo "FAIL — TechRequireAll game drill under Mono, stage $stage (rc=$rc)"; exit 1; }
    [ "$rc" -eq "$base" ] || { rm -f "$OUT"; echo "FAIL — under Mono the drill (stage $stage) exited $rc, the load-only control $base"; exit 1; }
    [ "$rc" -eq 0 ] || echo "NOTE — Mono exited $rc after DRILL PASS, exactly as it does after only loading the game assemblies"
  done
  rm -f "$OUT"
fi
echo 'PASS — TechRequireAll hooks against the game assemblies'
