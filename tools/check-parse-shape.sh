#!/usr/bin/env bash
# check-parse-shape.sh — the DEAD-DEFAULT gate. Fails on:
#
#     float x = 0.17f; float.TryParse(text, …, out x);
#
# which READS as "0.17 unless the config overrides it" and MEANS "0 unless it parses": `out` is
# definitely-assigned, so a failed parse writes the type's default straight over the initializer. Four sites
# carried this shape (2026-08-23); two were live, one was rescued by a range check on the next line, one was
# harmless only because its fallback happened to equal the failure value. Nothing else could catch it — it throws
# nothing, logs nothing, and yields a plausible number, so every loud-failure guard HAF has looks straight past it.
#
# THE FIX IT ENFORCES: config text becomes a number only through `Plugin.ParseFloat`/`CfgFloat`, where the
# fallback is a RETURN VALUE and never an out-param. See the policy comment at Plugin.ParseFloat.
#
#   bash tools/check-parse-shape.sh
# Wired into tools/check.sh (pre-push) and .github/workflows/ci.yml. Pure source analysis — no game, no Unity.
#
# WHAT IT CANNOT SEE (stated because a gate's "all clear" is only as wide as its regex — this repo has been
# bitten three times by a guard that silently excluded the shape it was meant to catch):
#   - the two statements separated by other statements, or by a brace;
#   - `out` into a FIELD or property rather than a local (`out this.x`);
#   - a TryParse whose result is assigned through a helper this script does not know about.
# It catches the compact idiom that actually occurred, on one line or two. Widen it when a new shape appears —
# and drill the new shape, never assume the old regex reaches it.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

# Production C# only. Tests are excluded ON PURPOSE: CfgParseTests must be free to *construct* the bad shape in a
# mutation drill without the gate blocking the drill.
FILES=$(find . -name '*.cs' \
          -not -path './obj/*' -not -path './bin/*' -not -path './Temp/*' \
          -not -path './Tests/*' -not -path './baker/*' -not -path './tools/*' | sort)

hits=0
for f in $FILES; do
  # Strip // line comments and /* */ blocks FIRST — the policy note at Plugin.ParseFloat quotes the banned shape
  # verbatim, and a gate that trips on its own documentation is a gate nobody keeps.
  # (Stripping is deliberately naive about "//" inside string literals: that can only hide a violation, never
  # invent one, so the gate stays fail-safe rather than fail-noisy.)
  # Then collapse newlines so the two-line form is caught as readily as the one-liner.
  # `tr -d '\0'` because -0777 leaves a trailing NUL that bash then warns about on every file, every run — and a
  # gate that prints a warning on a clean tree is a gate people learn to skim past.
  stripped=$(perl -0777 -pe 's{/\*.*?\*/}{}gs; s{//[^\n]*}{}g; s{\s+}{ }g' "$f" 2>/dev/null | tr -d '\0') || continue

  # <type> <name> = <literal…> ;   <type>.TryParse( … out <same name> )
  # \1 = declared type, \2 = the variable, back-referenced in the out — that back-reference is the whole test:
  # it is only a bug when the parse targets the SAME local the initializer just set.
  found=$(printf '%s' "$stripped" | grep -oP \
    '\b(?:float|double|int|long|uint|ulong|short|byte|decimal|bool)\s+(\w+)\s*=\s*[^;{}]+;\s*(?:float|double|int|long|uint|ulong|short|byte|decimal|bool)\.TryParse\([^;]*?\bout\s+\1\b' \
    || true)

  if [ -n "$found" ]; then
    while IFS= read -r m; do
      [ -z "$m" ] && continue
      hits=$((hits + 1))
      printf 'FAIL: %s\n      %s\n' "${f#./}" "$(printf '%s' "$m" | cut -c1-140)"
    done <<< "$found"
  fi
done

if [ "$hits" -gt 0 ]; then
  cat >&2 <<'MSG'

  The initializer above is DEAD CODE: `out` is definitely-assigned, so a failed parse overwrites it with the
  type's default (0 / false) and the default you wrote never survives.

  Fix: make the fallback a RETURN VALUE, not an out-param —
      float v = Plugin.CfgFloat(Plugin.SomeKey, 0.17f);          // config entry
      float v = Plugin.ParseFloat(text, 0.17f);                  // raw string
  See the policy comment at Plugin.ParseFloat and Tests/CfgParseTests.cs.
MSG
  printf 'parse shape: FAIL — %d dead-default site(s).\n' "$hits" >&2
  exit 1
fi

printf 'parse shape: OK — no dead-default TryParse sites (%d file(s) scanned).\n' "$(printf '%s\n' $FILES | wc -l)"
