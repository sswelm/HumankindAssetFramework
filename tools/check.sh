#!/usr/bin/env bash
# check.sh — the one fast pre-push gate for the HAF plugin. Runs every quick guard so a push can't land a broken
# build, a failing unit test, a broken doc link, or a drifted registry schema. Wired as the pre-push hook (tools/git-hooks/pre-push);
# also runnable by hand any time:  bash tools/check.sh   (lowercase tools/ — this repo, unlike ENCReload's Tools/)
#
# Deliberately NOT here (too slow / need Unity or the game): deploy_regression.sh (Blender golden-master), the
# in-editor Feature Test, and the in-game binding report (haf_bindings_report.txt). This is the sub-minute gate.
set -uo pipefail
cd "$(dirname "$0")/.." && ROOT="$(pwd)" || exit 2
fail=0
run() {  # run <label> <command...>
  local label="$1"; shift
  printf '\n=== %s ===\n' "$label"
  if "$@"; then printf '[PASS] %s\n' "$label"; else printf '[FAIL] %s\n' "$label"; fail=1; fi
}

# 1) plugin compiles — dotnet build exits non-zero on ERRORS only (the one benign CS0169 warning is fine).
run "plugin build (dotnet build -c Release)" dotnet build "$ROOT/HumankindAssetFramework.csproj" -c Release --nologo -v q

# 2) unit tests — the pure logic that runs outside Unity (parse/schema/reflection-resolution/smoke rule).
run "plugin unit tests (dotnet test)" dotnet test "$ROOT/Tests/HumankindAssetFramework.Tests.csproj" -c Release --nologo -v q

# 3) docs guard — relative links resolve, docs/notes/ banners present, no flat-wiki basename collisions. The docs
#    publish to the repo, the Pages site AND the wiki, all resolving relative links, so one bad move breaks three.
run "docs guard (links + notes convention)" bash "$ROOT/tools/check-docs.sh"

# 4) registry schema parity — cross-repo: the guard lives in the ENCReload editor checkout and compares the plugin's
#    Newtonsoft + regex parse against the editor's ModelDef. Best-effort: a plugin parse change is one half of that
#    drift, so run it here too when the sibling checkout is present; skip with a note otherwise.
PARITY=""
for d in "$ROOT/../ENCReload" "/c/Repo/ENCReload"; do
  if [ -f "$d/Tools/check_schema_parity.sh" ]; then PARITY="$d/Tools/check_schema_parity.sh"; break; fi
done
if [ -n "$PARITY" ]; then run "registry schema parity" bash "$PARITY"
else printf '\n=== registry schema parity ===\n[SKIP] ENCReload editor checkout not found (../ENCReload or /c/Repo/ENCReload) — run its Tools/check.sh\n'; fi

printf '\n========================================\n'
if [ "$fail" -eq 0 ]; then printf 'CHECK: PASS — safe to push.\n'; else printf 'CHECK: FAIL — fix the [FAIL] step(s) above before pushing (or, only in a real emergency, git push --no-verify).\n'; fi
exit "$fail"
