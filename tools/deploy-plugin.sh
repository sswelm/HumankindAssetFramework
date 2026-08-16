#!/usr/bin/env bash
# deploy-plugin.sh — copy the built plugin AND its Haf.Schema.dll dependency to BepInEx/plugins. Run after
#   dotnet build HumankindAssetFramework.csproj -c Release
# The plugin now DEPENDS on Haf.Schema.dll (the shared model schema) — BepInEx fails to load the plugin if it's
# missing, so they MUST be deployed together. Deploying only HumankindAssetFramework.dll (the old habit) breaks it.
#
#   bash Tools/deploy-plugin.sh [<BepInEx/plugins dir>]
#
# The plugins dir is arg 1, else $HK_PLUGINS, else the default Steam path.
set -uo pipefail
cd "$(dirname "$0")/.." && ROOT="$(pwd)" || exit 2
PLUGINS="${1:-${HK_PLUGINS:-/c/Program Files (x86)/Steam/steamapps/common/Humankind/BepInEx/plugins}}"
[ -d "$PLUGINS" ] || { echo "[FAIL] plugins dir not found: $PLUGINS  (pass as arg 1 or set HK_PLUGINS)"; exit 2; }

for dll in HumankindAssetFramework.dll Haf.Schema.dll; do
  src="$ROOT/bin/Release/$dll"
  [ -f "$src" ] || { echo "[FAIL] $src not found — run: dotnet build HumankindAssetFramework.csproj -c Release"; exit 2; }
  cp -f "$src" "$PLUGINS/$dll" || { echo "[FAIL] copy $dll (game running / file locked?)"; exit 2; }
  echo "  deployed $dll"
done
echo "OK - plugin + Haf.Schema.dll deployed to $PLUGINS"
