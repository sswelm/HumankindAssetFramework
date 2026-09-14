#!/usr/bin/env bash
# drill-reader-gates.sh — the gate that tests the two reflection-site gates. Plants a runtime file containing the
# shapes those gates exist to catch, runs them, and FAILS if either one passes:
#
#   1. a NULL-GUARDED, MULTI-LINE reader wrapper under a name neither gate knows (`Peek`) — the review of PR #48
#      (2026-09-14) showed the first self-check looked at three lines after the declaration, so this exact wrapper
#      hid its GetMember on line 5 and both gates went quiet again;
#   2. the headline dead-sentinel `bool x = true; try { x = Convert.ToBoolean(Peek(…)); } catch { }` through it;
#   3. a by-name literal that is not in the catalog, read through the same wrapper.
#
# Then removes the plant and re-runs both gates, which must PASS — so a drill that leaves the tree dirty, or a gate
# that fails for an unrelated reason, is reported as the drill's own failure. The plant is removed on every exit.
#
#   bash tools/drill-reader-gates.sh
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

PLANT=Patches/_ReaderGateDrill.cs
[ -e "$PLANT" ] && { echo "[FAIL] $PLANT already exists — a previous drill did not clean up; inspect and delete it"; exit 2; }
trap 'rm -f "$PLANT"' EXIT

cat > "$PLANT" <<'EOF'
using System;
namespace HumankindAssetFramework
{
    // PLANTED BY tools/drill-reader-gates.sh — if you can read this in the tree, the drill was interrupted; delete it.
    internal static class ReaderGateDrill
    {
        static object Peek(object o, string name)
        {
            if (o == null)
                return null;
            return UniversalInject.GetMember(o, name);
        }
        static void Run(object unit)
        {
            bool loaded = true; try { loaded = Convert.ToBoolean(Peek(unit, "IsLoadedDrill")); } catch { }
            if (!loaded) return;
            var x = Peek(unit, "BogusDrillMember");
        }
    }
}
EOF

fail=0
out=$(bash tools/check-catalog.sh 2>&1); rc=$?
if [ $rc -eq 0 ]; then echo "[FAIL] check-catalog.sh PASSED with an unknown guarded wrapper and an uncatalogued literal planted"; fail=1
elif ! printf '%s' "$out" | grep -q "Peek"; then echo "[FAIL] check-catalog.sh failed, but not on the planted wrapper 'Peek':"; printf '%s\n' "$out" | head -6; fail=1
else echo "ok   — check-catalog.sh refuses the null-guarded wrapper 'Peek' (self-check)"; fi

out=$(bash tools/check-member-shape.sh 2>&1); rc=$?
if [ $rc -eq 0 ]; then echo "[FAIL] check-member-shape.sh PASSED with an unknown guarded wrapper and a dead-sentinel through it planted"; fail=1
elif ! printf '%s' "$out" | grep -q "Peek"; then echo "[FAIL] check-member-shape.sh failed, but not on the planted wrapper 'Peek':"; printf '%s\n' "$out" | head -6; fail=1
else echo "ok   — check-member-shape.sh refuses the null-guarded wrapper 'Peek' (self-check)"; fi

# Second plant: the wrapper under a name the gates DO know (`Mem`) — now the self-checks are silent and the literal
# extraction and the dead-sentinel regex themselves must catch shapes 2 and 3.
sed -i 's/\bPeek\b/Mem/g' "$PLANT"
out=$(bash tools/check-catalog.sh 2>&1); rc=$?
if [ $rc -eq 0 ] || ! printf '%s' "$out" | grep -q "BogusDrillMember"; then echo "[FAIL] check-catalog.sh did not report the uncatalogued literal read through a known wrapper:"; printf '%s\n' "$out" | head -6; fail=1
else echo "ok   — check-catalog.sh sees the literal through the known wrapper 'Mem'"; fi
out=$(bash tools/check-member-shape.sh 2>&1); rc=$?
if [ $rc -eq 0 ] || ! printf '%s' "$out" | grep -q "IsLoadedDrill"; then echo "[FAIL] check-member-shape.sh did not report the dead-sentinel through a known wrapper:"; printf '%s\n' "$out" | head -6; fail=1
else echo "ok   — check-member-shape.sh sees the dead-sentinel through the known wrapper 'Mem'"; fi

rm -f "$PLANT"
bash tools/check-catalog.sh >/dev/null 2>&1      || { echo "[FAIL] check-catalog.sh does not pass on the clean tree"; fail=1; }
bash tools/check-member-shape.sh >/dev/null 2>&1 || { echo "[FAIL] check-member-shape.sh does not pass on the clean tree"; fail=1; }

if [ $fail -ne 0 ]; then echo "reader-gate drill: FAIL"; exit 1; fi
echo "reader-gate drill: OK — both gates refuse an unknown null-guarded wrapper and see the shapes through a known one."
