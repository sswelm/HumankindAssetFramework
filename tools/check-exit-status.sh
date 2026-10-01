#!/usr/bin/env bash
# check-exit-status.sh — a gate script must not take a command's exit status from the END of a pipeline.
#
# WHY THIS EXISTS. In bash, `$?` after `a | b` is b's status. Twice a drill's VERDICT was read that way:
#
#   CMP=$(python compare_probe.py csharp.txt blender.txt 2>&1 | tr -d '\r'); crc=$?
#   [ "$crc" -eq 0 ] || { echo "FAIL ..."; exit 1; }
#
# `tr` always succeeds, so `crc` was always 0: the comparator could print FAIL for every file and the drill went
# on to print PASS and exit 0. tools/vehicle_probe_drill.sh carried that line from PR #112 until 2026-10-02 - the
# gate that says the C# probe's rows equal Blender's could not fail on a difference - and
# tools/workshop_compact_drill.sh was written with a copy of it (external review of PR #114 found that one). The
# same slip had been made before with `| tail` (a pre-push run that failed and "passed"), and was known.
#
# THE SHAPE CAUGHT: a `|` and then, with no `;` in between, `; name=$?` - on one line, in tools/*.sh and
# tools/git-hooks/*. Take the status first and filter afterwards:
#
#   OUT=$(cmd 2>&1); rc=$?
#   OUT=$(printf '%s' "$OUT" | tr -d '\r')
#
# WHAT IT CANNOT SEE, said: a status read on the NEXT line after a pipeline, `if a | b; then`, and a verdict that is
# never read at all. It is a net for the one shape that has cost twice, not a proof.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

# a single `|` (not `||`), then no `;` and no further `|`... up to `; name=$?`
PATTERN='(^|[^|])\|[^|;][^;]*;[[:space:]]*[A-Za-z_][A-Za-z_0-9]*=\$\?'

# ---- the guard checks itself first: the line that cost us must match, its repair and the usual idioms must not
selftest() {
  local expect="$1" line="$2"
  if printf '%s\n' "$line" | grep -Eq "$PATTERN"; then got=match; else got=clean; fi
  [ "$got" = "$expect" ] || { echo "FAIL — check-exit-status: the guard's own pattern reads this line as '$got', expected '$expect': $line"; exit 2; }
}
selftest match 'CMP=$(python compare.py a b 2>&1 | tr -d "\r"); crc=$?'
selftest match 'cmd 2>&1 | tail -5; rc=$?'
selftest clean 'CMP=$(python compare.py a b 2>&1); crc=$?'
selftest clean 'BACK=$(mono read.exe 2>&1); rc=$?; BACK=$(printf "%s" "$BACK" | sed s/x//)'
selftest clean '[ -f "$f" ] || { run_it; rc=$?; }'
selftest clean 'cmd | tee log; rc=${PIPESTATUS[0]}'

found=0; checked=0
while IFS= read -r file; do
  checked=$((checked + 1))
  [ "$file" = "tools/check-exit-status.sh" ] && continue   # this file quotes the shape it forbids
  while IFS= read -r hit; do
    [ -n "$hit" ] || continue
    echo "  $file:$hit" | cut -c1-240
    found=$((found + 1))
  done < <(grep -nE "$PATTERN" "$file" | grep -vE '^[0-9]+:[[:space:]]*#')
done < <(git ls-files 'tools/*.sh' 'tools/git-hooks/*')

if [ "$found" -gt 0 ]; then
  echo "FAIL — $found line(s) read an exit status from the end of a pipeline: it is the LAST command's, so a failing"
  echo "       check before the pipe passes. Take the status first, filter afterwards (see this script's header)."
  exit 1
fi
echo "exit status: OK — no gate script reads \$? from the end of a pipeline ($checked script(s) checked)."
