#!/usr/bin/env bash
# drill-reader-gates.sh — the gate that tests the two reflection-site gates. Plants a runtime file containing the
# shapes those gates exist to catch, runs them, and FAILS if either one passes:
#
#   1. a NULL-GUARDED, MULTI-LINE reader wrapper under a name neither gate knows (`Peek`) — the review of PR #48
#      (2026-09-14) showed the first self-check looked at three lines after the declaration, so this exact wrapper
#      hid its GetMember on line 5 and both gates went quiet again;
#   1b. the same wrapper with a `}` inside a COMMENT above the read (`PeekComment`) and inside a STRING
#      (`PeekString`) — the second review: a brace the body walker took for code closed the body early — and one
#      whose read sits INSIDE an interpolation hole after a `}}` in the string's text (`PeekInterp`): a blanking
#      that treats the whole `$"…"` as text loses the read, one that treats it as code closes the body early;
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
        static object PeekComment(object o, string name)
        {
            // a stray brace in a comment: }
            if (o == null) return null;   /* and one in a block comment } */
            return UniversalInject.GetMember(o, name);
        }
        static object PeekString(object o, string name)
        {
            if (name == "}" || name == @"}" || name == $"{name}}}" || name == "\"}") return null;
            return UniversalInject.GetMember(o, name);
        }
        static object PeekInterp(object o, string name)
        {
            // the read is INSIDE an interpolation hole — code, not text — after a brace in the hole's surrounding text
            var s = $"[Drill] }} '{name}' -> '{UniversalInject.GetMember(o, name)}' {{ }}";
            return s.Length > 0 ? null : o;
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
# Every planted wrapper must be named by both self-checks — one missing means a body-boundary blind spot.
expect_all() {   # $1 gate label, $2 output
  local missing=""
  for w in Peek PeekComment PeekString PeekInterp; do printf '%s' "$2" | grep -qE "^\s*$w\s" || missing="$missing $w"; done
  if [ -n "$missing" ]; then echo "[FAIL] $1 failed, but did not name planted wrapper(s):$missing"; printf '%s\n' "$2" | head -8; return 1; fi
  return 0
}
out=$(bash tools/check-catalog.sh 2>&1); rc=$?
if [ $rc -eq 0 ]; then echo "[FAIL] check-catalog.sh PASSED with unknown guarded wrappers and an uncatalogued literal planted"; fail=1
elif ! expect_all "check-catalog.sh" "$out"; then fail=1
else echo "ok   — check-catalog.sh refuses the null-guarded / comment-brace / string-brace wrappers (self-check)"; fi

out=$(bash tools/check-member-shape.sh 2>&1); rc=$?
if [ $rc -eq 0 ]; then echo "[FAIL] check-member-shape.sh PASSED with unknown guarded wrappers and a dead-sentinel through one planted"; fail=1
elif ! expect_all "check-member-shape.sh" "$out"; then fail=1
else echo "ok   — check-member-shape.sh refuses the null-guarded / comment-brace / string-brace wrappers (self-check)"; fi

# Second plant: the wrappers under a name the gates DO know (`Mem`) — now the self-checks are silent and the literal
# extraction and the dead-sentinel regex themselves must catch shapes 2 and 3.
sed -i 's/\bPeekComment\b/MemC/g; s/\bPeekString\b/MemS/g; s/\bPeekInterp\b/MemI/g; s/\bPeek\b/Mem/g' "$PLANT"
sed -i '/static object MemC(/,/^        }/d; /static object MemS(/,/^        }/d; /static object MemI(/,/^        }/d' "$PLANT"
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
