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
#   3. a by-name literal that is not in the catalog, read through the same wrapper;
#   4. (2026-10-11) three names that reach the reader WITHOUT a literal at the call — a `new[] { … }` loop, a
#      `static readonly string[]` table and a local function forwarding its parameter — which pass 3 of the catalog
#      gate must trace to their literals (the shape that hid `RenderMatFields` from the two literal passes);
#   5. (2026-10-11) a reader whose name is a parameter nothing in the file ever passes a literal to (`RunDynamic`):
#      pass 3 must refuse it by name rather than silently not count it.
#   6. a same-named loop in another method, an ambiguous table in another file, a parameter shadowing a table,
#      mixed literal/computed table entries, and a dynamic suffix must not manufacture a complete literal name;
#      a bounded numeric suffix must enumerate every generated name.
#
# Then removes the plant and re-runs both gates, which must PASS — so a drill that leaves the tree dirty, or a gate
# that fails for an unrelated reason, is reported as the drill's own failure. The plant lives in a COPY of the tree
# (2026-10-29), removed on every exit: the checkout is never touched, so the drill runs beside the gates it tests.
#
#   bash tools/drill-reader-gates.sh
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

# THE DRILL PLANTS IN A COPY OF THE TREE (2026-10-29). The gate runs it beside the two gates it tests (and beside every
# other step), and a plant in the checkout would be read by them; two gates at once lost pushes to a vanishing plant for
# weeks before that. The copy holds what the two gates read: every C# source the gates scan (not the build, test, baker
# or tools folders) and tools/ itself, as the working tree has them (tracked and untracked, not ignored).
SANDBOX=$(mktemp -d) || exit 2
trap 'rm -rf "$SANDBOX"' EXIT
git ls-files -z --cached --others --exclude-standard -- '*.cs' tools | grep -zvE '^(obj|bin|Temp|Tests|baker)/' | xargs -0 cp --parents -t "$SANDBOX" \
  || { echo "[FAIL] could not copy the tree for the reader-gate drill"; exit 2; }
cd "$SANDBOX" || exit 2

PLANT=Patches/_ReaderGateDrill.cs
SHADOW=Patches/_ReaderGateDrillShadow.cs
[ -e "$PLANT" ] || [ -e "$SHADOW" ] && { echo "[FAIL] a reader-gate drill plant already exists — inspect and delete it"; exit 2; }

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
        static readonly string[] DrillFields = { "BogusStaticTableMember" };
        static void RunTables(object unit)
        {
            foreach (var f in new[] { "BogusLoopMember" }) Peek(unit, f);
            foreach (var f in DrillFields) Peek(unit, f);
            object Loc(string n) => Peek(unit, n);
            Loc("BogusLocalMember");
        }
        // A prior loop with the same identifier must not lend its literal to a later method's parameter.
        static void Decoy() { foreach (var dyn in new[] { "BogusDecoyMember" }) { } }
        // string FIRST: the `(object, string)` helper self-check must not be the one that refuses this — pass 3 must
        static void RunDynamic(string dyn, object unit) { Peek(unit, dyn); }
        static void RunDynamicSuffix(string suffix, object unit) { Peek(unit, "BogusPrefix" + suffix); }
        static void RunMixed(string dynamicPart, object unit)
        {
            foreach (var mixedField in new[] { "BogusKnownMember", dynamicPart }) Peek(unit, mixedField);
        }
        static void RunShadowedTable(string DrillFields, object unit) { Peek(unit, DrillFields); }
        static void RunIndexed(object unit)
        {
            for (int i = 0; i < 3; i++) Peek(unit, "BogusIndexed" + i);
        }
    }
}
EOF
cat > "$SHADOW" <<'EOF'
// Same table identifier in another file: it must not replace the declaration local to _ReaderGateDrill.cs.
class ReaderGateDrillShadow { static readonly string[] DrillFields = { "BogusShadowMember" }; }
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
# Shape 4: the three table-carried names must be reported by NAME (pass 3 traced them to their literals) …
for nm in BogusLoopMember BogusStaticTableMember BogusLocalMember; do
  if printf '%s' "$out" | grep -q "$nm"; then echo "ok   — check-catalog.sh traces $nm through its table / loop / local function"
  else echo "[FAIL] check-catalog.sh did not report $nm — pass 3 lost a name read without a literal at the call:"; printf '%s\n' "$out" | head -8; fail=1; fi
done
# … and shape 5: the parameter nothing passes a literal to is refused by the function's name, not silently uncounted.
if printf '%s' "$out" | grep -q 'RunDynamic('; then echo "ok   — check-catalog.sh refuses the reader whose name has no literal behind it (RunDynamic)"
else echo "[FAIL] check-catalog.sh did not refuse RunDynamic's computed member name:"; printf '%s\n' "$out" | head -8; fail=1; fi
if printf '%s' "$out" | grep -q 'RunDynamicSuffix'; then echo "ok   — check-catalog.sh refuses a literal prefix plus a computed suffix"
else echo "[FAIL] check-catalog.sh treated a dynamic suffix as a whole literal:"; printf '%s\n' "$out" | head -8; fail=1; fi
if printf '%s' "$out" | grep -q 'mixedField'; then echo "ok   — check-catalog.sh refuses a table that mixes a literal with a computed element"
else echo "[FAIL] check-catalog.sh treated a mixed table as all literal:"; printf '%s\n' "$out" | head -8; fail=1; fi
if printf '%s' "$out" | grep -q 'RunShadowedTable('; then echo "ok   — check-catalog.sh follows a parameter that shadows a static table"
else echo "[FAIL] check-catalog.sh resolved a shadowed parameter as the static table:"; printf '%s\n' "$out" | head -8; fail=1; fi
for nm in BogusIndexed0 BogusIndexed1 BogusIndexed2; do
  if printf '%s' "$out" | grep -q "$nm"; then echo "ok   — check-catalog.sh enumerates $nm from a bounded name loop"
  else echo "[FAIL] check-catalog.sh did not enumerate $nm from a bounded name loop:"; printf '%s\n' "$out" | head -8; fail=1; fi
done
out=$(bash tools/check-member-shape.sh 2>&1); rc=$?
if [ $rc -eq 0 ] || ! printf '%s' "$out" | grep -q "IsLoadedDrill"; then echo "[FAIL] check-member-shape.sh did not report the dead-sentinel through a known wrapper:"; printf '%s\n' "$out" | head -6; fail=1
else echo "ok   — check-member-shape.sh sees the dead-sentinel through the known wrapper 'Mem'"; fi

rm -f "$PLANT"
bash tools/check-catalog.sh >/dev/null 2>&1      || { echo "[FAIL] check-catalog.sh does not pass on the clean tree"; fail=1; }
bash tools/check-member-shape.sh >/dev/null 2>&1 || { echo "[FAIL] check-member-shape.sh does not pass on the clean tree"; fail=1; }

if [ $fail -ne 0 ]; then echo "reader-gate drill: FAIL"; exit 1; fi
echo "reader-gate drill: OK — both gates refuse an unknown null-guarded wrapper and see the shapes through a known one."
