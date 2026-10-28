# blender_oracle.sh - sourced by the push gate's Blender-parity drills (2026-10-28, user: "I don't want to use
# [Blender] anymore in the push, only in the bake test").
#
# Every parity drill holds a C# port to Blender's own output. Until now each one RAN Blender on every push (ten
# steps, the deploy drill alone 800 s). Now each Blender invocation goes through oracle_blender, which has three modes
# (HAF_ORACLE):
#   replay  (the default, the gate's mode): no Blender. The invocation's raw output, exit code and output folders come
#           from the RECORD under $HAF_BLENDER_ORACLE (default <modding project>/Tools/blender_oracle), keyed by what
#           the invocation depends on - the dump script, the Blender script under test, the input files' bytes, the
#           job and shard lists with every file they name - so a record cannot answer for inputs it was not made from.
#           No record, or a stale one, is a SKIP (exit 3) that names what changed; the gate counts a SKIP as a failure.
#   record  (tools/refresh_blender_oracle.sh): Blender runs and the record is written. Needs Blender.
#   live    (the behaviour before this file, for FULL=1 investigations): Blender runs, nothing is stored.
# The caller's TMPD/WTMP paths inside a record are stored as placeholders and put back on replay. A drill calls
# oracle_init once, oracle_blender per invocation (the shards in their background subshells too), and oracle_check
# after the invocation or after its shard waits - that is where a missing record becomes the drill's SKIP.
#
# Needs from the caller: ROOT (the repo), TMPD (posix temp), WTMP (its cygpath -m form); WROOT and PROJECT optional.

ORACLE_MODE="${HAF_ORACLE:-replay}"
case "$ORACLE_MODE" in replay|record|live) ;; *) echo "FAIL — HAF_ORACLE must be replay, record or live (got '$ORACLE_MODE')"; exit 2;; esac
ORACLE_STORE="${HAF_BLENDER_ORACLE:-${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}/Tools/blender_oracle}"
ORACLE_DRILL=""; ORACLE_LABEL=""

# Blender as UniversalBaker.FindBlender finds it: the newest "Blender*" under Program Files; BLENDER=<exe> overrides.
oracle_find_blender() {
  BLENDER="${BLENDER:-}"
  [ -n "$BLENDER" ] && return 0
  local base cand
  for base in "/c/Program Files/Blender Foundation" "/c/Program Files (x86)/Blender Foundation"; do
    [ -d "$base" ] || continue
    cand=$(ls -d "$base"/Blender* 2>/dev/null | sort -V | tail -1)
    [ -n "$cand" ] && [ -x "$cand/blender.exe" ] && { BLENDER="$cand/blender.exe"; return 0; }
  done
  return 0
}

oracle_init() {  # <drill> [label]: the drill's folder in the store; record and live need Blender, replay does not
  ORACLE_DRILL="$1"; ORACLE_LABEL="${2:-$1}"
  if [ "$ORACLE_MODE" != "replay" ]; then
    oracle_find_blender
    if [ -z "$BLENDER" ] || [ ! -x "$BLENDER" ]; then echo "FAIL — $ORACLE_LABEL: HAF_ORACLE=$ORACLE_MODE runs Blender and none was found (BLENDER=<exe>); nothing was recorded"; exit 2; fi
  fi
  return 0
}

# oracle_blender <name> <raw output file> [--dir <folder under WTMP Blender writes>]... --key <item>... -- <blender arguments>
# Returns Blender's exit code (recorded or live); 99 when replay found no usable record (noted for oracle_check).
oracle_blender() {
  local name="$1" raw="$2"; shift 2
  local dirs=() keys=() rc out
  while [ $# -gt 0 ]; do
    case "$1" in
      --dir) dirs+=("$2"); shift 2 ;;
      --key) shift; while [ $# -gt 0 ] && [ "$1" != "--" ]; do keys+=("$1"); shift; done ;;
      --) shift; break ;;
      *) echo "oracle_blender: unexpected argument '$1'" >&2; return 2 ;;
    esac
  done
  [ -n "$ORACLE_DRILL" ] || { echo "oracle_blender: oracle_init was not called" >&2; return 2; }
  local dirargs=(); [ "${#dirs[@]}" -eq 0 ] || dirargs=(--dirs "${dirs[@]}")
  case "$ORACLE_MODE" in
    live)
      "$BLENDER" "$@" > "$raw" 2>&1; return $? ;;
    record)
      "$BLENDER" "$@" > "$raw" 2>&1; rc=$?
      out=$(python "$ROOT/tools/blender-oracle/oracle.py" store --store "$ORACLE_STORE" --drill "$ORACLE_DRILL" --name "$name" \
              --wtmp "$WTMP" --tmpd "$TMPD" --wroot "${WROOT:-}" --rc "$rc" --raw "$raw" --blender "$BLENDER" "${dirargs[@]}" -- "${keys[@]}" 2>&1); src=$?
      if [ "$src" -ne 0 ]; then printf '%s: could not record (%s)\n' "$name" "$out" >> "$TMPD/oracle_missing"; return 99; fi
      return "$rc" ;;
    *)
      out=$(python "$ROOT/tools/blender-oracle/oracle.py" load --store "$ORACLE_STORE" --drill "$ORACLE_DRILL" --name "$name" \
              --wtmp "$WTMP" --tmpd "$TMPD" --wroot "${WROOT:-}" --raw "$raw" "${dirargs[@]}" -- "${keys[@]}" 2>&1); src=$?
      if [ "$src" -ne 0 ]; then printf '%s: %s\n' "$name" "$out" >> "$TMPD/oracle_missing"; return 99; fi
      rc=$(printf '%s\n' "$out" | awk '$1 == "RC" { print $2 }' | tail -1)
      case "$rc" in ''|*[!0-9]*) printf '%s: the record gave no exit code (%s)\n' "$name" "$out" >> "$TMPD/oracle_missing"; return 99 ;; esac
      return "$rc" ;;
  esac
}

oracle_check() {  # the SKIP for a replay without a usable record, after the invocation(s) it covers
  [ -f "$TMPD/oracle_missing" ] || return 0
  local what; what=$(tr '\n' ' ' < "$TMPD/oracle_missing" | cut -c1-900)
  if [ "$ORACLE_MODE" = "record" ]; then echo "FAIL — $ORACLE_LABEL: the Blender record could not be written: $what"; exit 1; fi
  echo "SKIP — $ORACLE_LABEL: no usable Blender record for this run's inputs, so NO parity comparison (run tools/refresh_blender_oracle.sh with Blender installed; HAF_ORACLE=live runs Blender here): $what"
  exit 3
}
