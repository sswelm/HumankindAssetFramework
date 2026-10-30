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

# ONE PERL OVER EVERY FILE (2026-10-29): the loop this replaced ran perl, tr and grep -oP per file in subshells -
# six forks a file, 340 files, 67 s on Windows for a regex that takes a second. tools/shape-scan.pl holds the regex
# unchanged (and the comment stripping, with its deliberate naivety about "//" in strings) and prints the same FAIL
# lines, then `HITS <n>`.
scan=$(perl tools/shape-scan.pl parse $FILES) || { echo "[FAIL] parse shape: the scanner failed (perl tools/shape-scan.pl)"; exit 2; }
hits=$(printf '%s\n' "$scan" | awk '/^HITS /{print $2}' | tail -1)
case "$hits" in ''|*[!0-9]*) echo "[FAIL] parse shape: the scanner gave no count"; exit 2;; esac
[ "$hits" -eq 0 ] || printf '%s\n' "$scan" | grep -v '^HITS '

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
