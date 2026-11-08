#!/usr/bin/env bash
# Sweep stale HAF-generated temp entries, never infer ownership from generic tmp.* / vr.* names or common payloads.
# Older unnamespaced drill/gate leftovers and manual haf-* scratch directories need manual review.
# New gates use haf-gate.XXXXXXXXXX; their nested drill directories are cleaned with their parent.
#
# usage: temp_sweep.sh [--root DIR] [--age-days N] [--dry-run]
#   --root     temp root (default: $TMPDIR, $TEMP, $TMP, then /tmp); the gate passes its captured host root
#   --age-days every entry in a candidate tree must be at least N days old (default 2)
#   --dry-run  list what would go, delete nothing
# Symlinks are not followed. Inspection/cleanup failures preserve the entry and report; usage errors exit 2.
set -uo pipefail
ROOT_DIR="${TMPDIR:-${TEMP:-${TMP:-/tmp}}}"; AGE_DAYS=2; DRY=0
while [ $# -gt 0 ]; do
  case "$1" in
    --root|--age-days)
      [ $# -ge 2 ] && [ -n "$2" ] && [[ "$2" != --* ]] || { echo "temp_sweep: $1 needs a value" >&2; exit 2; }
      if [ "$1" = --root ]; then ROOT_DIR="$2"; else AGE_DAYS="$2"; fi
      shift 2 ;;
    --dry-run) DRY=1; shift ;;
    *) echo "temp_sweep: unknown argument '$1'" >&2; exit 2 ;;
  esac
done
[[ "$AGE_DAYS" =~ ^[0-9]+$ ]] || { echo "temp_sweep: --age-days takes a whole number" >&2; exit 2; }
ROOT_DIR="$(cd -- "$ROOT_DIR" 2>/dev/null && pwd -P)" || { echo "temp_sweep: no such temp directory" >&2; exit 2; }

# Match actual mktemp / Python tempfile / Guid producers, not the whole haf-* namespace (which includes manual work).
ours_dir() {
  local name="$1"
  [[ "$name" =~ ^haf-(gate|animationlab|bake_commit|bake-kernels|buffer-hook|parity-row|rig-import|vehicle-generate|vehicle_source_guard)\.[A-Za-z0-9]{10}$ ]] ||
  [[ "$name" =~ ^haf-riganim-connected\.[A-Za-z0-9]{8}$ ]] ||
  [[ "$name" =~ ^haf-(gate-test|oracle|oracle-sh|sweep-test|fbx-verdict|missing-mono)-[a-z0-9_]{8}$ ]] ||
  [[ "$name" =~ ^haf-(deploy-inproc|export-evidence|fbxmat|gitstamp|glbwriter|image-parity|gltf|material-evidence|loosefile|source-guard|names|parity-selftest)-[[:xdigit:]]{32}$ ]] ||
  [[ "$name" =~ ^haf_checkedreplace_[[:xdigit:]]{32}$ ]]
}

swept=0; swept_kb=0; kept_young=0
while IFS= read -r -d '' e; do
  name="${e##*/}"
  # find emits direct children of the canonical root. Recheck type/link immediately before inspection and removal.
  case "$e" in "$ROOT_DIR"/*) ;; *) continue;; esac
  [ ! -L "$e" ] || continue
  if [ -d "$e" ]; then ours_dir "$name" || continue
  elif [ -f "$e" ]; then [[ "$name" =~ ^riganim_inproc_[[:xdigit:]]{32}\.glb$ ]] || continue
  else continue
  fi
  # A directory's mtime does NOT change when an existing file (or a deeper directory) is written.
  # Check every descendant, and fail closed if find cannot inspect any part of the tree.
  if ! recent=$(find "$e" -mtime -"$AGE_DAYS" -print -quit 2>/dev/null); then
    printf 'LEFT (cannot inspect age) %s\n' "$name"; continue
  fi
  if [ -n "$recent" ]; then kept_young=$((kept_young + 1)); continue; fi
  if ! kb=$(du -sk "$e" 2>/dev/null | cut -f1) || ! [[ "$kb" =~ ^[0-9]+$ ]]; then
    printf 'LEFT (cannot inspect size) %s\n' "$name"; continue
  fi
  [ ! -L "$e" ] && [ -e "$e" ] || continue
  if [ "$DRY" -eq 1 ]; then printf 'WOULD SWEEP %8d KB  %s\n' "$kb" "$name"
  else rm -rf -- "$e" 2>/dev/null || { printf 'LEFT (in use?) %s\n' "$name"; continue; }
  fi
  swept=$((swept + 1)); swept_kb=$((swept_kb + kb))
done < <(find "$ROOT_DIR" -mindepth 1 -maxdepth 1 \( -name 'haf-*' -o -name 'haf_checkedreplace_*' -o -name 'riganim_inproc_*.glb' \) -print0 2>/dev/null)

verb="SWEPT"; [ "$DRY" -eq 1 ] && verb="WOULD SWEEP"
printf 'TEMP SWEEP %s %d of our leftover(s), %d MB, under %s (older than %s day(s); %d of ours kept as younger)\n' \
  "$verb" "$swept" "$((swept_kb / 1024))" "$ROOT_DIR" "$AGE_DAYS" "$kept_young"
exit 0
