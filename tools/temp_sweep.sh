#!/usr/bin/env bash
# temp_sweep.sh - the gate's and the drills' leftovers under the user's temp directory, swept (2026-11-08).
#
# Measured before this existed: %TEMP% held 47,781 entries and 88 GB. Ours among them: 1,165 EMPTY `tmp.*` directories
# (tools/check.sh emptied its own mktemp directory after the steps but never removed it - one per gate run), 9 `tmp.*`
# directories of drills killed mid-run (12 GB; a drill's TMPD has its trap, a killed shell has not), 32 `vr.*` of the
# vehicle rig drill (10 GB), 24 `haf-vehicle*` / `haf-rig*` of the Unity-backed drills (3.9 GB), 136 `haf-oracle-sh-*` and
# a `haf-gate-test-*` from the Python tests (their cleanup swallows a failure), 2,289 `haf_checkedreplace_*` of the
# CheckedReplace tests (Directory.Delete swallowed). The other 40,000 entries are other programs' (GUID directories,
# .NET workload folders, .node files, ...) and are NOT ours to touch.
#
# WHAT IS SWEPT: top-level entries of the temp root, OLDER than --age-days (default 2, so no running session's files),
# and OURS by name or by content:
#   tmp.<10 chars>        mktemp's pattern - only when EMPTY, or holding a gate marker (<n>.rc / <n>.cmd / <n>.out), a
#                         drill marker (Newtonsoft.Json.dll, jobs.txt, blender_raw*.txt, shard_*.txt, a fixtures folder),
#                         or nothing but such tmp.* directories; a tmp.* holding anything else is another program's and stays
#   vr.<6 chars>          tools/vehiclerig_drill.sh's TMPD
#   haf-*                 the Unity-backed drills (haf-vehicle-generate., haf-rig-import., ...), the Python tests
#                         (haf-gate-test-, haf-oracle-sh-), manual scratch (haf-pr*) - NEVER haf-gate.lock, the gate's lock
#   haf_checkedreplace_*  Tests/CheckedReplaceTests.cs
#   riganim_inproc_*.glb  tools/riganim-drill/RigAnimDrill.cs's in-process file (deleted in a finally a killed run never reaches)
# Nothing else. Symlinks are not followed. The sweep never fails the gate: it reports and exits 0 (2 only for a bad argument).
#
# usage: temp_sweep.sh [--root DIR] [--age-days N] [--dry-run]
#   --root     the temp root (default: $TEMP, $TMP, then /tmp); the gate passes the host's temp, since it redirects TMPDIR
#   --age-days entries modified within the last N days stay (default 2)
#   --dry-run  list what would go, delete nothing
set -uo pipefail
ROOT_DIR=""; AGE_DAYS=2; DRY=0
while [ $# -gt 0 ]; do
  case "$1" in
    --root) ROOT_DIR="$2"; shift 2 ;;
    --age-days) AGE_DAYS="$2"; shift 2 ;;
    --dry-run) DRY=1; shift ;;
    *) echo "temp_sweep: unknown argument '$1'" >&2; exit 2 ;;
  esac
done
[[ "$AGE_DAYS" =~ ^[0-9]+$ ]] || { echo "temp_sweep: --age-days takes a whole number" >&2; exit 2; }
[ -n "$ROOT_DIR" ] || ROOT_DIR="${TEMP:-${TMP:-/tmp}}"
ROOT_DIR="$(cd "$ROOT_DIR" 2>/dev/null && pwd -P)" || { echo "temp_sweep: no such directory '$ROOT_DIR'" >&2; exit 2; }

# a tmp.* directory is ours when it is empty, carries a gate or drill marker, or holds nothing but tmp.* directories that are ours
ours_tmp() {  # $1 = directory; returns 0 when ours
  local d="$1" e name
  local any=0
  for e in "$d"/* "$d"/.[!.]*; do
    [ -e "$e" ] || [ -L "$e" ] || continue
    any=1; name="${e##*/}"
    case "$name" in
      Newtonsoft.Json.dll|jobs.txt|fixtures) return 0 ;;
      blender_raw*.txt|shard_*.txt) return 0 ;;
      [0-9]|[0-9][0-9]|[0-9][0-9][0-9]) return 0 ;;                  # a gate's step log directory
      [0-9]*.rc|[0-9]*.cmd|[0-9]*.out|[0-9]*.t0|[0-9]*.end) return 0 ;;   # a gate's step records
    esac
  done
  [ "$any" -eq 0 ] && return 0                                          # empty: the gate's emptied directory
  # nothing but tmp.* directories, each ours (a gate directory whose steps' directories were left)
  for e in "$d"/* "$d"/.[!.]*; do
    [ -e "$e" ] || [ -L "$e" ] || continue
    name="${e##*/}"
    [ -d "$e" ] && [ ! -L "$e" ] && [[ "$name" =~ ^tmp\.[A-Za-z0-9]{10}$ ]] && ours_tmp "$e" || return 1
  done
  return 0
}

swept=0; swept_kb=0; kept_young=0; skipped_foreign=0
while IFS= read -r -d '' e; do
  name="${e##*/}"
  if [ -L "$e" ]; then continue; fi
  ours=0
  case "$name" in
    haf-gate.lock) continue ;;
    haf-*) [ -d "$e" ] && ours=1 ;;
    haf_checkedreplace_*) [ -d "$e" ] && ours=1 ;;
    riganim_inproc_*.glb) [ -f "$e" ] && ours=1 ;;
    vr.??????) [ -d "$e" ] && ours=1 ;;
    tmp.??????????) [ -d "$e" ] && ours_tmp "$e" && ours=1 ;;
  esac
  if [ "$ours" -eq 0 ]; then case "$name" in tmp.??????????) skipped_foreign=$((skipped_foreign + 1)) ;; esac; continue; fi
  # age: the entry's own modification time (a directory's changes when its entries do), in whole days
  if [ -n "$(find "$e" -maxdepth 0 -mtime -"$AGE_DAYS" 2>/dev/null)" ]; then kept_young=$((kept_young + 1)); continue; fi
  kb=$(du -sk "$e" 2>/dev/null | cut -f1); [[ "$kb" =~ ^[0-9]+$ ]] || kb=0
  if [ "$DRY" -eq 1 ]; then printf 'WOULD SWEEP %8d KB  %s\n' "$kb" "$name"
  else rm -rf -- "$e" 2>/dev/null || { printf 'LEFT (in use?) %s\n' "$name"; continue; }
  fi
  swept=$((swept + 1)); swept_kb=$((swept_kb + kb))
done < <(find "$ROOT_DIR" -mindepth 1 -maxdepth 1 \( -name 'tmp.*' -o -name 'vr.*' -o -name 'haf-*' -o -name 'haf_checkedreplace_*' -o -name 'riganim_inproc_*.glb' \) -print0 2>/dev/null)

verb="SWEPT"; [ "$DRY" -eq 1 ] && verb="WOULD SWEEP"
printf 'TEMP SWEEP %s %d of our leftover(s), %d MB, under %s (older than %d day(s); %d of ours kept as younger, %d tmp.* directories not ours left alone)\n' \
  "$verb" "$swept" "$((swept_kb / 1024))" "$ROOT_DIR" "$AGE_DAYS" "$kept_young" "$skipped_foreign"
exit 0
