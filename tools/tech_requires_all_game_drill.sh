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
set -uo pipefail
cd "$(dirname "$0")/.." && ROOT="$(pwd)" || exit 2
MANAGED="${1:-${HK_MANAGED:-/c/Program Files (x86)/Steam/steamapps/common/Humankind/Humankind_Data/Managed}}"
[ -d "$MANAGED" ] || { echo "SKIP — Managed dir not found: $MANAGED (the hooks were NOT drilled)"; exit 3; }
# A folder that is there but is not the game's Managed folder (HK_MANAGED pointed at the game's root, an emptied
# install) is a wrong setting, not a missing game: FAIL and say so, where the drill would die on its first load.
for dll in Assembly-CSharp.dll Amplitude.Mercury.Firstpass.dll Amplitude.Mercury.Data.dll; do
  [ -f "$MANAGED/$dll" ] || { echo "FAIL — $MANAGED is not the game's Managed folder: no $dll in it (HK_MANAGED, or argument 1, takes <Humankind>/Humankind_Data/Managed)"; exit 1; }
done
dotnet build "$ROOT/HumankindAssetFramework.csproj" -c Release --nologo -v q -p:WarningLevel=0 || { echo 'FAIL — plugin build'; exit 1; }
dotnet build "$ROOT/tools/tech-requires-all-drill/TechRequireAllGameDrill.csproj" -c Release --nologo -v q || { echo 'FAIL — drill build'; exit 1; }
EXE="$ROOT/tools/tech-requires-all-drill/bin/Release/HumankindAssetFramework.Tests.exe"
WMANAGED="$(cygpath -m "$MANAGED" 2>/dev/null || echo "$MANAGED")"
# The stage is this script's to set, never the shell's: with HAF_DRILL_STAGE=load or nolist still exported from a hand
# run, the first pass ran that stage and the script printed PASS on 12 or 24 checks instead of 47 (the second review of
# PR #312). "full" is no stage the drill knows: the whole run, with a list.
# The removal guard must hold WITHOUT a list: a save made with one keeps its queue constraints. A run of its own,
# because the plugin reads the list once per process.
for stage in full nolist; do
  output=$(HAF_DRILL_STAGE="$stage" "$EXE" "$WMANAGED" 2>&1); rc=$?
  printf '%s\n' "$output"
  [ "$rc" -eq 0 ] || { echo "FAIL — TechRequireAll game drill, stage $stage (rc=$rc)"; exit 1; }
  # Loading the game's assemblies can exit zero before any hook checks run.
  last=${output##*$'\n'}
  [ "${last%$'\r'}" = "DRILL PASS" ] || { echo "FAIL — TechRequireAll game drill, stage $stage exited without DRILL PASS"; exit 1; }
done
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
