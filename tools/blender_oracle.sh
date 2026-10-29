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
#           The actual arguments and the Python files the key scripts import or name are included automatically (the
#           import closure, NOT every tracked .py: 2026-10-29); inputs are checked before/after recording.
#           Payload/manifest corruption and unsafe output paths are refused. No record, or a stale one, is a SKIP (exit 3) that names what changed; the gate counts a SKIP as a failure.
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
ORACLE_DRILL=""; ORACLE_LABEL=""; ORACLE_WROOT=""; ORACLE_CODE=""

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

oracle_init() {  # <drill> [label]: record/live need Blender; replay needs only the recorded reference
  ORACLE_DRILL="$1"; ORACLE_LABEL="${2:-$1}"
  if [ "$ORACLE_MODE" != "replay" ]; then
    oracle_find_blender
    if [ -z "$BLENDER" ] || [ ! -x "$BLENDER" ]; then echo "FAIL — $ORACLE_LABEL: HAF_ORACLE=$ORACLE_MODE runs Blender and none was found (BLENDER=<exe>); nothing was recorded"; exit 2; fi
  fi
  [ "$ORACLE_MODE" != "live" ] || return 0
  ORACLE_WROOT="${WROOT:-}"
  if [ -z "$ORACLE_WROOT" ]; then
    if command -v cygpath >/dev/null 2>&1; then ORACLE_WROOT=$(cygpath -m "$ROOT"); else ORACLE_WROOT="$ROOT"; fi
  fi
  return 0
}

# Callers may initialize before allocating TMPD/WTMP. Each invocation gets its own list so parallel shards never write the same file.
oracle_prepare_dependencies() {  # <invocation name> <key item>...: the tracked Python files the key scripts reach
  local stem="oracle_code_$1"; shift
  case "$stem" in oracle_code_|*[!a-zA-Z0-9_.-]*) echo "FAIL — invalid oracle invocation name"; exit 2;; esac
  # The IMPORT CLOSURE of the key scripts (oracle.py deps: what they import or name as a .py, followed, among the files
  # git tracks - ignored proof projects, caches and untracked files are not Blender source dependencies). Not every
  # tracked .py (the first format 2 did that, 2026-10-29: an edit to one drill's fixture generator staled every drill's
  # record and cost a 20-minute refresh); the closure is what Blender can execute for this invocation.
  ORACLE_CODE="$TMPD/$stem.json"
  python "$ROOT/tools/blender-oracle/oracle.py" deps --root "$ORACLE_WROOT" -- "$@" > "$ORACLE_CODE"
  local rc=$?
  if [ "$rc" -ne 0 ]; then echo "FAIL — $ORACLE_LABEL: cannot describe the Blender Python dependencies ($(cat "$ORACLE_CODE" 2>/dev/null))"; exit 2; fi
  return 0
}

# Keep machine-readable stdout rows intact: Blender diagnostics can arrive in the middle of a buffered row.
# The record retains both streams, with stderr appended after stdout once Blender has exited.
oracle_run_blender() {  # <raw output> <Blender arguments...>
  local raw="$1"; shift
  local err rc
  err=$(mktemp "$raw.stderr.XXXXXX") || { printf 'cannot allocate Blender diagnostics\n' >> "$TMPD/oracle_missing"; return 99; }
  "$BLENDER" "$@" > "$raw" 2> "$err"; rc=$?
  if ! cat "$err" >> "$raw"; then
    printf 'cannot collect Blender diagnostics\n' >> "$TMPD/oracle_missing"
    rm -f "$err"; return 99
  fi
  rm -f "$err"
  return "$rc"
}

# oracle_blender <name> <raw output file> [--dir <folder under WTMP Blender writes>]... --key <item>... -- <blender arguments>
# Returns Blender's exit code (recorded or live); 99 when replay found no usable record (noted for oracle_check).
oracle_blender() {
  local name="$1" raw="$2"; shift 2
  local dirs=() keys=() rc out src input_key
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
  local argvargs=() argument
  for argument in "$@"; do argvargs+=(--arg "$argument"); done
  if [ "$ORACLE_MODE" != "live" ]; then
    oracle_prepare_dependencies "$name" "${keys[@]}"
    keys+=("$ORACLE_CODE" "$ROOT/tools/blender_oracle.sh")
  fi
  case "$ORACLE_MODE" in
    live)
      oracle_run_blender "$raw" "$@"; return $? ;;
    record)
      input_key=$(python "$ROOT/tools/blender-oracle/oracle.py" key --wtmp "$WTMP" --tmpd "$TMPD" --wroot "$ORACLE_WROOT" "${argvargs[@]}" -- "${keys[@]}" 2>&1); src=$?
      if [ "$src" -ne 0 ]; then printf '%s: cannot fingerprint inputs (%s)\n' "$name" "$input_key" >> "$TMPD/oracle_missing"; return 99; fi
      oracle_run_blender "$raw" "$@"; rc=$?
      out=$(python "$ROOT/tools/blender-oracle/oracle.py" store --store "$ORACLE_STORE" --drill "$ORACLE_DRILL" --name "$name" \
              --wtmp "$WTMP" --tmpd "$TMPD" --wroot "$ORACLE_WROOT" --rc "$rc" --expected-key "$input_key" --raw "$raw" --blender "$BLENDER" "${dirargs[@]}" "${argvargs[@]}" -- "${keys[@]}" 2>&1); src=$?
      if [ "$src" -ne 0 ]; then printf '%s: could not record (%s)\n' "$name" "$out" >> "$TMPD/oracle_missing"; return 99; fi
      return "$rc" ;;
    *)
      out=$(python "$ROOT/tools/blender-oracle/oracle.py" load --store "$ORACLE_STORE" --drill "$ORACLE_DRILL" --name "$name" \
              --wtmp "$WTMP" --tmpd "$TMPD" --wroot "$ORACLE_WROOT" --raw "$raw" "${dirargs[@]}" "${argvargs[@]}" -- "${keys[@]}" 2>&1); src=$?
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
