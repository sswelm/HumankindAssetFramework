#!/usr/bin/env bash
# check-hot-path.sh — nothing on the SHIPPED per-frame path may still call itself a spike.
#
# Review finding 2026-08-22: `Plugin.Update()` — the hot path, every frame of every game — called five things whose own
# comments labelled them SPIKE / EXPERIMENTAL: the wonder cell fill, the shared-cell district path, two district
# diagnostics, the district mesh-swap tick. One of them (`PollWonderRows`) had cost 1,350 µs/frame for weeks before a
# perf pass found it. The label was never wrong at the time — it just outlived the experiment, and nothing made anyone
# revisit it. "A shipped plugin shouldn't run code its own comments call a spike."
#
# The rule this enforces, in the Update() body only:
#   * SPIKE is not allowed at all. Promote the code (own it, drop the label) or delete it.
#   * EXPERIMENTAL is allowed ONLY when the same line names its gate — the marker must read `EXPERIMENTAL (opt-in…`,
#     so a reader sees immediately that the thing is off unless a config key turns it on.
# Everything OUTSIDE Update() is untouched: a config description that warns a user "EXPERIMENTAL: this footprint mask
# is a work in progress" is honest labelling, and the axis headers document real history. This guard is about the hot
# path only.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
F=Plugin.cs
[ -f "$F" ] || { echo "[FAIL] $F not found"; exit 2; }

start=$(grep -n "^        private void Update()" "$F" | head -1 | cut -d: -f1)
[ -n "$start" ] || { echo "[FAIL] could not find Plugin.Update() — was it renamed? (this guard would silently pass)"; exit 2; }
end=$(awk -v s="$start" 'NR>s && /^        }$/ {print NR; exit}' "$F")
[ -n "$end" ] || { echo "[FAIL] could not find the end of Plugin.Update()"; exit 2; }

bad=$(sed -n "${start},${end}p" "$F" | grep -nE "SPIKE|EXPERIMENTAL" | grep -vE "EXPERIMENTAL \(opt-in" || true)
if [ -z "$bad" ]; then
  echo "hot path: OK — Plugin.Update() ($((end - start)) lines) runs nothing labelled SPIKE, and every EXPERIMENTAL names its gate."
  exit 0
fi
echo "[FAIL] Plugin.Update() — the SHIPPED per-frame path — still calls code labelled as an experiment:"
printf '%s\n' "$bad" | awk -v s="$start" -F: '{printf "  %s:%d  %s\n", "Plugin.cs", s+$1-1, substr($0, index($0,$2))}' | cut -c1-190
echo
echo "Promote it (own the cost, drop the label), delete it, or — if it really is opt-in — gate it behind a config key"
echo "and write the marker as 'EXPERIMENTAL (opt-in, [Section] Key=default)' so the gate is visible at the call site."
exit 1
