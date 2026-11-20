#!/usr/bin/env bash
# Drive the two TechRequireAll hooks against the REAL game assemblies, without launching the game: a
# DepartmentOfScience built without its constructor, six technologies, and the game's own
# CheckTechnologyPrerequisite / AddLockedTechnologyToQueue called by reflection - first unpatched (the control: one
# prerequisite unlocks, one parent chain is queued), then with the plugin's hooks applied as the plugin applies them.
# The two game methods that reach the live Sandbox (AddTechnologyToQueue, MoveTechnologyInQueue) are replaced by plain
# list operations; that part is not the game's code. Not in the push gate: it needs an installed game.
#
#   bash tools/tech_requires_all_game_drill.sh [<Humankind .../Humankind_Data/Managed>]
#
# The Managed dir is arg 1, else $HK_MANAGED, else the default Steam path (as tools/check-bindings.sh).
# HAF_DRILL_MONO=<mono.exe> runs it a second time under that Mono.
set -uo pipefail
cd "$(dirname "$0")/.." && ROOT="$(pwd)" || exit 2
MANAGED="${1:-${HK_MANAGED:-/c/Program Files (x86)/Steam/steamapps/common/Humankind/Humankind_Data/Managed}}"
[ -d "$MANAGED" ] || { echo "SKIP — Managed dir not found: $MANAGED (the hooks were NOT drilled)"; exit 3; }
dotnet build "$ROOT/HumankindAssetFramework.csproj" -c Release --nologo -v q -p:WarningLevel=0 || { echo 'FAIL — plugin build'; exit 1; }
dotnet build "$ROOT/tools/tech-requires-all-drill/TechRequireAllGameDrill.csproj" -c Release --nologo -v q || { echo 'FAIL — drill build'; exit 1; }
EXE="$ROOT/tools/tech-requires-all-drill/bin/Release/HumankindAssetFramework.Tests.exe"
WMANAGED="$(cygpath -m "$MANAGED" 2>/dev/null || echo "$MANAGED")"
"$EXE" "$WMANAGED"; rc=$?
[ "$rc" -eq 0 ] || { echo "FAIL — TechRequireAll game drill (rc=$rc)"; exit 1; }
if [ -n "${HAF_DRILL_MONO:-}" ]; then
  # Measured 2026-11-20 with the Unity editor's Mono 6.13: the process segfaults while EXITING once the game's
  # assemblies are loaded, with no hook applied and no HAF code run. So the exit code is judged against that
  # control: the same code as the load-only run, and the drill's own last line, decide.
  OUT="$(mktemp)"
  HAF_DRILL_STAGE=load "$HAF_DRILL_MONO" "$(cygpath -m "$EXE")" "$WMANAGED" > "$OUT" 2>&1; base=$?
  grep -q '^LOADED' "$OUT" || { cat "$OUT"; rm -f "$OUT"; echo 'FAIL — the game assemblies did not load under Mono'; exit 1; }
  "$HAF_DRILL_MONO" "$(cygpath -m "$EXE")" "$WMANAGED" > "$OUT" 2>&1; rc=$?
  cat "$OUT"; last="$(grep -E '^DRILL (PASS|FAIL)' "$OUT" | tail -1)"; rm -f "$OUT"
  [ "$last" = "DRILL PASS" ] || { echo "FAIL — TechRequireAll game drill under Mono (rc=$rc)"; exit 1; }
  [ "$rc" -eq "$base" ] || { echo "FAIL — under Mono the drill exited $rc, the load-only control $base"; exit 1; }
  [ "$rc" -eq 0 ] || echo "NOTE — Mono exited $rc after DRILL PASS, exactly as it does after only loading the game assemblies"
fi
echo 'PASS — TechRequireAll hooks against the game assemblies'
