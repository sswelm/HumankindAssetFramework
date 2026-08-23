#!/usr/bin/env bash
# check-member-shape.sh — the DEAD-SENTINEL gate, sibling of check-parse-shape.sh. Fails on:
#
#     bool loaded = true; try { loaded = Convert.ToBoolean(GetMember(unit, "IsLoaded")); } catch { }
#
# which READS as "true unless the game says otherwise" and MEANS "false whenever the member is missing".
# GetMember swallows its own exception and returns NULL for a renamed member; Convert.ToBoolean(null) is `false`
# and Convert.ToInt32(null) is `0` — they do NOT throw. So the catch never runs, the initializer is dead, and the
# variable silently takes the converted-null value. Two live sites did `if (!loaded) continue;` right after,
# which on a game rename would have skipped their work silently and forever (2026-08-23 review).
#
# Also caught: the `catch { continue; }` variant, whose intent is "if I cannot read this, leave the thing ALONE".
# It never fired either — in the muzzle loop an unreadable SkeletonBoneIndex read as 0, a VALID index, so the slot
# was stomped instead of skipped.
#
# THE FIX IT ENFORCES: typed member reads where the fallback is a RETURN VALUE and absence is observable —
#     if (!MemberBool(unit, "IsLoaded", true)) continue;          // fallback returned, never assigned-over
#     if (!TryMemberLong(br, "AxisIndex", out long axis)) continue;  // "absent" is a state you can branch on
# See the policy comment above TryConvert in Patches/UniversalInject.Reflection.cs and Tests/MemberReadTests.cs.
#
#   bash tools/check-member-shape.sh
# Wired into tools/check.sh (pre-push) and .github/workflows/ci.yml. Pure source analysis — no game, no Unity.
#
# WHAT IT CANNOT SEE (stated because a gate's "all clear" is only as wide as its regex — this repo has been bitten
# three times by a guard that silently excluded the shape it was meant to catch):
#   - the declaration and the try separated by other statements or a brace;
#   - NOT a violation, and deliberately skipped: `Convert.ToInt32(GetMember(o, "Count") ?? -1)`. The `??` supplies
#     the fallback BEFORE the convert, so the sentinel is genuinely reachable there. One real site had this shape
#     (ScaleEra), and a gate that cries wolf on correct code is a gate people start passing with --no-verify.
#   - a Convert reached through a helper this script does not know by name;
#   - the same shape around a reader other than GetMember.
# It catches the compact idiom that actually occurred. Widen it when a new shape appears — and DRILL the new
# shape, never assume the old regex reaches it.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

# Production C# only. Tests are excluded ON PURPOSE: MemberReadTests must be free to construct the bad shape in a
# mutation drill without the gate blocking the drill.
FILES=$(find . -name '*.cs' \
          -not -path './obj/*' -not -path './bin/*' -not -path './Temp/*' \
          -not -path './Tests/*' -not -path './baker/*' -not -path './tools/*' | sort)

hits=0
for f in $FILES; do
  # Strip comments first — the policy notes quote the banned shape verbatim, and a gate that trips on its own
  # documentation is a gate nobody keeps. Then collapse whitespace so the two-line form reads like the one-liner.
  stripped=$(perl -0777 -pe 's{/\*.*?\*/}{}gs; s{//[^\n]*}{}g; s{\s+}{ }g' "$f" 2>/dev/null | tr -d '\0') || continue

  # (a) DEAD INITIALIZER: <type> <var> = <init>; try { <var> = Convert.To*(GetMember(...
  # \1 back-reference is the whole test — it is only a bug when the try targets the SAME local just initialized.
  a=$(printf '%s' "$stripped" | grep -oP \
    '\b(?:bool|float|double|int|long|uint|ulong|short|byte|decimal)\s+(\w+)\s*=\s*[^;{}]+;\s*try\s*\{\s*\1\s*=\s*Convert\.To\w+\s*\(\s*GetMember\b(?![^{}]*\?\?)[^{}]*?\}\s*catch' \
    || true)

  # (b) PHANTOM SKIP: try { ... Convert.To*(GetMember(...)) ... } catch { continue; }  — the continue never fires.
  b=$(printf '%s' "$stripped" | grep -oP \
    'try\s*\{[^{}]*Convert\.To\w+\s*\(\s*GetMember\b[^{}]*\}\s*catch\s*(?:\([^)]*\))?\s*\{\s*continue\s*;\s*\}' \
    || true)

  for found in "$a" "$b"; do
    [ -z "$found" ] && continue
    while IFS= read -r m; do
      [ -z "$m" ] && continue
      hits=$((hits + 1))
      printf 'FAIL: %s\n      %s\n' "${f#./}" "$(printf '%s' "$m" | cut -c1-140)"
    done <<< "$found"
  done
done

if [ "$hits" -gt 0 ]; then
  cat >&2 <<'MSG'

  The default above is DEAD CODE. GetMember returns null for a missing/renamed member and Convert.To*(null)
  returns the type's zero WITHOUT throwing, so the catch never runs and your default never survives.

  Fix: use the typed reads, where the fallback is a RETURN VALUE and absence is observable —
      if (!MemberBool(unit, "IsLoaded", true)) continue;              // was: bool loaded = true; try { ... }
      if (!TryMemberLong(br, "AxisIndex", out long axis)) continue;   // was: catch { continue; }
  Also: MemberFloat / MemberInt / MemberLong / TryMemberFloat.
  See TryConvert in Patches/UniversalInject.Reflection.cs and Tests/MemberReadTests.cs.
MSG
  printf 'member shape: FAIL — %d dead-sentinel site(s).\n' "$hits" >&2
  exit 1
fi

printf 'member shape: OK — no dead-sentinel Convert(GetMember) sites (%d file(s) scanned).\n' "$(printf '%s\n' $FILES | wc -l)"
