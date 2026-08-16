#!/usr/bin/env bash
# check-bindings.sh — HEADLESS reflection-drift check: validate GameBinding's catalog against a Humankind build's
# assemblies WITHOUT launching the game. Run this after a Humankind update (or in CI on a game-version bump) — it
# names exactly which bindings the new build broke, before anyone plays. Separate from Tools/check.sh on purpose:
# the pre-push gate guards HAF *code* changes (build/test/schema); this guards *game* changes.
#
#   bash Tools/check-bindings.sh [<Humankind .../Humankind_Data/Managed>]
#
# The Managed dir is arg 1, else $HK_MANAGED, else the default Steam path. Needs the .NET SDK (builds the net8
# bindcheck tool once, then reuses it).
set -uo pipefail
cd "$(dirname "$0")/.." && ROOT="$(pwd)" || exit 2

MANAGED="${1:-${HK_MANAGED:-/c/Program Files (x86)/Steam/steamapps/common/Humankind/Humankind_Data/Managed}}"
if [ ! -d "$MANAGED" ]; then
  echo "[SKIP] Managed dir not found: $MANAGED"
  echo "       pass it as arg 1, or set HK_MANAGED, on a machine with Humankind installed."
  exit 2
fi

echo "=== building bindcheck (net8, once) ==="
dotnet build "$ROOT/tools/bindcheck/bindcheck.csproj" -c Release --nologo -v q || { echo "[FAIL] bindcheck build"; exit 2; }
DLL="$(find "$ROOT/tools/bindcheck/bin" -name bindcheck.dll | head -1)"
[ -n "$DLL" ] || { echo "[FAIL] bindcheck.dll not found after build"; exit 2; }

echo "=== bindcheck: GameBinding catalog vs the game build ==="
dotnet "$DLL" "$ROOT/Patches/GameBinding.cs" "$MANAGED"
