#!/usr/bin/env bash
# check.sh — the one fast pre-push gate for the HAF plugin. Runs every quick guard so a push can't land a broken
# build, a failing unit test, a broken doc link, or a drifted registry schema. Wired as the pre-push hook (tools/git-hooks/pre-push);
# also runnable by hand any time:  bash tools/check.sh   (lowercase tools/ — this repo, unlike ENCReload's Tools/)
#
# Deliberately NOT here (too slow / need Unity or the game): deploy_regression.sh (Blender golden-master), the
# in-editor Feature Test, and the in-game binding report (haf_bindings_report.txt). The source-only steps take
# seconds; with the Unity-backed drills (in parallel since 2026-10-11, the source guards on a lane beside them since
# 2026-10-29) the push takes a few minutes.
#
# THE GATE HAS MODES (2026-10-30; user: "for pushes to github it should no longer use any blender!!! ... it should only be
# part of the bake test"). The push runs NO Blender-parity drill - not Blender, not a record of Blender, not a refresh.
#   HAF_GATE=push    (the default; the pre-push hook) the build, the unit tests, the source guards, the Unity-backed drills
#   HAF_GATE=parity  ONLY the nine parity drills that hold the C# ports to Blender's own output, with Blender
#                    (HAF_ORACLE=live unless set): tools/blender_parity.sh, and the Bake Tests row
#                    "Do the C# ports still match Blender?" runs exactly that from Unity
#   HAF_GATE=all     both (the gate as it was until 2026-10-30)
#
# A step's exit status means: 0 PASS (its claim held), 3 SKIP (a prerequisite was absent, so its claim was NOT
# tested; the step's own SKIP line says what did run), anything else FAIL. SKIP is not PASS (2026-10-11): until then a
# drill that printed "PASS … Blender not found, so NO parity comparison" and exited 0 kept its green on a machine
# without Blender, and the gate reported 26/26 while eight steps had lost their defining claim. A SKIP is printed as
# [SKIP], recorded in PROGRESS.md, and FAILS the push — unless HAF_ALLOW_SKIP=1 says a partial gate is what was meant.
set -uo pipefail
cd "$(dirname "$0")/.." && ROOT="$(pwd)" || exit 2
fail=0
skipped=()
# Progress: every step appends a line to PROGRESS.md (the file beside the worktrees, kept open by the user) when it
# ends, so a 20-minute gate can be watched without asking. Nothing here fails on its absence. The main checkout's
# parent has no such file; its gate reports to the worktrees' one, so every session sees every gate (2026-10-12).
PROGRESS="${HAF_PROGRESS:-$ROOT/../PROGRESS.md}"
[ -f "$PROGRESS" ] || [ ! -f "$ROOT/../haf-wt/PROGRESS.md" ] || PROGRESS="$ROOT/../haf-wt/PROGRESS.md"
# A step may take this long; a hung Blender or Mono used to block the push forever, with no message (2026-10-12).
STEP_TIMEOUT="${HAF_STEP_TIMEOUT:-1800}"
# The mode (see the header): push_step and parity_step prefix every step with the mode it belongs to.
GATE="${HAF_GATE:-push}"
case "$GATE" in push|parity|all) ;; *) echo "FAIL — HAF_GATE must be push, parity or all (got '$GATE')"; exit 2;; esac
[ "$GATE" = push ] || export HAF_ORACLE="${HAF_ORACLE:-live}"   # the parity drills run Blender unless a record is asked for
push_step() { [ "$GATE" = parity ] || "$@"; }     # push_step run_lane "<label>" <command...>
parity_step() { [ "$GATE" = push ] || "$@"; }     # parity_step run_bg "<label>" <command...>
note() { [ -f "$PROGRESS" ] && printf '%s  gate  %s\n' "$(date +%H:%M)" "$*" >> "$PROGRESS"; return 0; }
word() { case "$1" in 0) echo PASS;; 3) echo SKIP;; *) echo FAIL;; esac; }
record() {  # record <label> <rc> <seconds>: the verdict line, counted (the PROGRESS note is the caller's)
  local label="$1" rc="$2" s="$3"
  case "$rc" in
    0) printf '[PASS] %s (%d s)\n' "$label" "$s" ;;
    3) printf '[SKIP] %s (%d s) — its claim was NOT tested on this machine; the SKIP line above says what did run\n' "$label" "$s"; skipped+=("$label") ;;
    124) printf '[FAIL] %s (%d s) — timed out: the step limit is %s s (HAF_STEP_TIMEOUT)\n' "$label" "$s" "$STEP_TIMEOUT"; fail=1 ;;
    *) printf '[FAIL] %s (%d s)\n' "$label" "$s"; fail=1 ;;
  esac
}
run() {  # run <label> <command...>: in sequence, the output as it comes
  local label="$1"; shift; local t0=$(date +%s) rc s
  printf '\n=== %s ===\n' "$label"
  timeout -k 30 "$STEP_TIMEOUT" "$@"; rc=$?; s=$(( $(date +%s) - t0 ))
  record "$label" "$rc" "$s"; note "$(word "$rc") $label ($s s)"
}
# ---- ONE GATE AT A TIME on this machine (2026-10-12) ----
# Two gates at once — two sessions pushing, or a push beside a drill run — have failed each other for weeks: the
# reader-gate drill's plant vanishing under the other gate (it plants in a copy of the tree since 2026-10-29), and the
# timing test in Tests/test_parallel_gate.py missing its deadline under the other gate's thirteen drills (two rejected
# pushes on 2026-10-11 alone). The lock is a DIRECTORY
# (mkdir is atomic) under the user's temp dir, so every checkout and worktree shares it. A waiter says so once, here and
# in PROGRESS.md, then polls. The holder heartbeats; a holder that stopped beating (a killed shell) is taken over.
LOCK="${HAF_GATE_LOCK:-${TEMP:-${TMP:-/tmp}}/haf-gate.lock}"
LOCK_BEAT_PID=""
CANCEL_PID=""
gate_cancel_watch() {
  [ -n "${HAF_GATE_CANCEL_FILE:-}" ] || return 0
  ( sleeper=""
    trap '[ -z "$sleeper" ] || { kill "$sleeper" 2>/dev/null; wait "$sleeper" 2>/dev/null; }; exit 0' TERM INT
    while [ ! -f "$HAF_GATE_CANCEL_FILE" ]; do sleep 1 & sleeper=$!; wait "$sleeper"; done
    kill -TERM "$$"
  ) </dev/null >/dev/null 2>&1 & CANCEL_PID=$!
}
gate_lock() {
  local t0=$(date +%s) said=0 holder beat now poll="${HAF_GATE_LOCK_POLL:-10}" stale="${HAF_GATE_LOCK_STALE:-180}" max="${HAF_GATE_LOCK_WAIT:-2700}"
  while ! mkdir "$LOCK" 2>/dev/null; do
    holder=$(cat "$LOCK/holder" 2>/dev/null || echo unknown); beat=$(cat "$LOCK/beat" 2>/dev/null) || beat=""; now=$(date +%s)
    # an unreadable or empty beat is NOT a dead holder (2026-10-12): the holder rewrites the file every 20 s, and a read
    # that lands on the rewrite (or a sharing violation on Windows) came back empty, read as 0, and took over a LIVE gate
    # eight minutes into its run - two gates then ran side by side. Only a beat that is readable and old is stale.
    case "$beat" in ''|*[!0-9]*) beat=$now ;; esac
    if [ $(( now - beat )) -gt "$stale" ]; then
      rm -rf "$LOCK"; printf 'took over a stale gate lock (%s; no heartbeat for %d s)\n' "$holder" "$((now - beat))"; note "took over a stale gate lock ($holder)"; continue
    fi
    if [ "$said" = 0 ]; then printf 'another gate is running (%s); waiting for it, polling every %s s\n' "$holder" "$poll"; note "waiting: another gate holds the lock ($holder)"; said=1; fi
    sleep "$poll"
    if [ $(( $(date +%s) - t0 )) -gt "$max" ]; then
      printf 'FAIL — another gate has held %s for over %s s (holder: %s); if it is dead, remove that directory\n' "$LOCK" "$max" "$holder"
      note "DONE FAIL (the gate lock was held by $holder for over $max s)"; exit 1
    fi
  done
  printf '%s  %s  pid %s\n' "$(date +%H:%M)" "$ROOT" "$$" > "$LOCK/holder"; date +%s > "$LOCK/beat"
  # the beat is written beside and renamed over (a reader never sees a half-written or truncated file), and a failed
  # write is retried on the next beat, never a reason to stop beating (2026-10-12: the old `|| exit 0` let one failed
  # write end the heartbeat, and 180 s later the gate read as dead)
  ( sleeper=""
    trap '[ -z "$sleeper" ] || { kill "$sleeper" 2>/dev/null; wait "$sleeper" 2>/dev/null; }; exit 0' TERM INT
    while :; do
      sleep "${HAF_GATE_LOCK_BEAT:-20}" & sleeper=$!; wait "$sleeper"
      [ -d "$LOCK" ] || exit 0
      date +%s > "$LOCK/beat.$$" 2>/dev/null && mv -f "$LOCK/beat.$$" "$LOCK/beat" 2>/dev/null
    done
  ) </dev/null >/dev/null 2>&1 & LOCK_BEAT_PID=$!
}
gate_stop_aux() {  # reap sleepers too: inherited pipe/log handles otherwise survive their parent shell.
  local pid
  for pid in "$CANCEL_PID" "$LOCK_BEAT_PID"; do
    [ -z "$pid" ] || { kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null; }
  done
  CANCEL_PID=""; LOCK_BEAT_PID=""
}
gate_unlock() {  # only the holder removes the lock: a waiter that gave up must not free another gate's
  gate_stop_aux
  if grep -q "pid $$\$" "$LOCK/holder" 2>/dev/null; then rm -rf "$LOCK"; fi
  return 0
}
verdict() {  # the one summary line, and the status the hook returns
  local n=${#skipped[@]} list=""
  [ "$n" -eq 0 ] || list=$(printf '\n  [SKIP] %s' "${skipped[@]}")
  printf '\n========================================\n'
  if [ "$fail" -ne 0 ]; then
    printf 'CHECK: FAIL — fix the [FAIL] step(s) above before pushing (or, only in a real emergency, git push --no-verify).%s\n' "$list"
    note "DONE FAIL"; return 1
  fi
  if [ "$n" -eq 0 ]; then
    # THE PARITY STAMP (2026-10-31): a parity-mode PASS with every drill run is what the push's stamp step remembers.
    # Written here, before the one CHECK line (the Bake Tests row reads that line last), and only on a full PASS.
    if [ "$GATE" != push ] && ! bash "$ROOT/tools/check-parity-stamp.sh" --write "${PARITY_START_DIGEST:-}"; then
      printf 'CHECK: FAIL — the parity drills passed but the parity stamp could not be written (see above).\n'; note "DONE FAIL (stamp)"; return 1
    fi
    printf 'CHECK: PASS — safe to push.\n'; note "DONE PASS"; return 0
  fi
  if [ "${HAF_ALLOW_SKIP:-0}" = "1" ]; then
    printf 'CHECK: PASS WITH %d SKIP — accepted by HAF_ALLOW_SKIP=1; these claims were NOT tested on this machine:%s\n' "$n" "$list"
    note "DONE PASS ($n skipped, allowed by HAF_ALLOW_SKIP=1)"; return 0
  fi
  printf 'CHECK: FAIL — %d step(s) SKIPPED: their claim was NOT tested on this machine, and a gate that cannot see is not green:%s\n' "$n" "$list"
  printf 'Install the missing prerequisite (the modding project, HAF_UNITY_PROJECT; Blender for the parity mode), or, meaning it: HAF_ALLOW_SKIP=1 git push\n'
  note "DONE FAIL ($n skipped)"; return 1
}
# The drills are independent of one another (each works in its own mktemp directory and builds its own exe), and the
# Blender-backed ones took the gate from a minute to 25 in sequence: they run AT ONCE, each into its own log, and the
# logs are printed in declaration order; each worker reports progress and records its duration when it finishes.
BG_LOGS=$(mktemp -d); bg_n=0; declare -a BG_LABEL BG_PID BG_T0; LANE_STEPS=()   # assigned, not only declared: set -u counts an unassigned array as unbound
export TMPDIR="$BG_LOGS"   # every step's mktemp lands under the gate's own directory: what an abort has to find (gate_abort)
prepare_step_log() {  # <log prefix> <label>: stale files from a prior phase must never stand in for this step.
  local log="$1" label="$2"
  if ! rm -f -- "$log.out" "$log.end" "$log.rc" "$log.t0" "$log.cmd"; then
    printf '[FAIL] %s — cannot clear old step metadata\n' "$label"
    note "FAIL $label (cannot clear old step metadata)"; fail=1; return 1
  fi
}
run_bg() {  # run_bg <label> <command...>: started now, judged by run_bg_wait
  local label="$1"; shift; local log="$BG_LOGS/$bg_n"
  prepare_step_log "$log" "$label" || return 1
  BG_LABEL[$bg_n]="$label"; BG_T0[$bg_n]=$(date +%s)
  local t0="${BG_T0[$bg_n]}"
  ( timeout -k 30 "$STEP_TIMEOUT" "$@" > "$log.out" 2>&1; rc=$?; t1=$(date +%s)
    echo "$t1" > "$log.end"
    note "$(word "$rc") $label ($((t1 - t0)) s)"
    echo "$rc" > "$log.rc"
  ) &
  BG_PID[$bg_n]=$!; bg_n=$((bg_n + 1))
}
# THE LANE (2026-10-29): the source guards are shell scripts that fork for a living (a find, a perl, a grep per file),
# and twenty-five such steps at once did not overlap, they queued on process creation - the member-shape gate took
# 544 s beside the others where it takes 99 s alone, and the whole block outlasted the sequence it replaced. The guards
# run IN SEQUENCE on one background lane beside the drills (which are Mono and Python compute, not forks): each lane
# step is judged exactly as a run_bg step is - its own log, exit code, duration and PROGRESS note - and the lane's wall
# hides under the longest drill's.
run_lane() {  # run_lane <label> <command...>: queued on the lane; run_lane_go starts it
  local label="$1"; shift
  prepare_step_log "$BG_LOGS/$bg_n" "$label" || return 1
  BG_LABEL[$bg_n]="$label"; BG_T0[$bg_n]=$(date +%s); BG_PID[$bg_n]=""
  if ! printf '%s\0' "$@" > "$BG_LOGS/$bg_n.cmd"; then
    printf '[FAIL] %s — cannot write queued command\n' "$label"
    note "FAIL $label (cannot write queued command)"; fail=1; return 1
  fi
  LANE_STEPS+=("$bg_n"); bg_n=$((bg_n + 1))
}
run_lane_go() {  # the queued lane steps, one after another, in one background job
  [ "${#LANE_STEPS[@]}" -gt 0 ] || return 0
  local steps=("${LANE_STEPS[@]}") i; LANE_STEPS=()
  ( local cmd t0 t1 rc
    for i in "${steps[@]}"; do
      t0=$(date +%s); echo "$t0" > "$BG_LOGS/$i.t0"
      cmd=()
      if ! mapfile -d '' -t cmd < "$BG_LOGS/$i.cmd" || [ "${#cmd[@]}" -eq 0 ]; then
        printf 'FAIL — cannot read queued command\n' > "$BG_LOGS/$i.out"; rc=2
      else
        timeout -k 30 "$STEP_TIMEOUT" "${cmd[@]}" > "$BG_LOGS/$i.out" 2>&1; rc=$?
      fi
      t1=$(date +%s)
      echo "$t1" > "$BG_LOGS/$i.end"
      note "$(word "$rc") ${BG_LABEL[$i]} ($((t1 - t0)) s)"
      echo "$rc" > "$BG_LOGS/$i.rc"
    done ) &
  local pid=$!
  for i in "${steps[@]}"; do BG_PID[$i]=$pid; done
}
run_bg_wait() {
  local i t1 completed rc t0
  run_lane_go   # a lane nobody started runs now, so no queued step is ever forgotten
  for ((i = 0; i < bg_n; i++)); do
    [ -z "${BG_PID[$i]}" ] || wait "${BG_PID[$i]}" 2>/dev/null   # a lane's pid is shared by its steps: waited once, then already gone
    t0=$(cat "$BG_LOGS/$i.t0" 2>/dev/null); [[ "$t0" =~ ^[0-9]+$ ]] && BG_T0[$i]=$t0   # a lane step's clock starts when its turn comes
    t1=$(cat "$BG_LOGS/$i.end" 2>/dev/null)
    # Missing completion metadata is also a failed worker, never a fabricated success.
    completed=1
    if ! [[ "$t1" =~ ^[0-9]+$ ]]; then t1="${BG_T0[$i]}"; completed=0; fi
    printf '\n=== %s ===\n' "${BG_LABEL[$i]}"; cat "$BG_LOGS/$i.out"
    rc=$(cat "$BG_LOGS/$i.rc" 2>/dev/null)
    if [ "$completed" = "1" ] && [[ "$rc" =~ ^[0-9]+$ ]]; then record "${BG_LABEL[$i]}" "$rc" "$((t1 - BG_T0[$i]))"
    else printf '[FAIL] %s (%d s) — no completion status\n' "${BG_LABEL[$i]}" "$((t1 - BG_T0[$i]))"; fail=1; fi
  done
  # emptied, not removed: TMPDIR still names it for every step after the parallel phase (2026-10-12: the goldens step,
  # the first tail step to call mktemp, found its TMPDIR gone and failed on "No such file or directory")
  find "$BG_LOGS" -mindepth 1 -delete 2>/dev/null; bg_n=0
}
gate_abort() {  # INT/TERM: stop everything this gate spawned, drop the logs, free the lock, say so
  gate_stop_aux   # stop auxiliaries gracefully before the Windows worker sweep can kill their shells
  # Windows keeps no usable parent chain for what Cygwin exec'd — every drill script, Blender shard and Mono run has
  # a parent that is already gone — so `taskkill /T` stops at the worker's shell and the drills ran on underneath
  # (drilled 2026-10-12: a gate killed at 60 s into its drills left eleven processes that were still starting
  # Blender a minute later). What this gate spawned names, on its command line, this checkout's tools/ or a temp
  # directory under the gate's own one (TMPDIR, exported for every step): those are killed by name, this process
  # and the hook's own stub excluded. The other session's gate has another checkout and another temp directory.
  local self tag rt
  self=$(ps -W -p $$ 2>/dev/null | awk 'NR == 2 { print $4 }'); [ -n "$self" ] || self=0
  tag=$(basename "$BG_LOGS"); rt=$(basename "$ROOT")
  if [ -n "$tag" ] && [ -n "$rt" ]; then
    # the gate's temp directory name is unique (mktemp): anything naming it is this gate's; the checkout's tools/ path
    # is shared with the user's own shells, so that match is limited to the kinds of process a step runs
    powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { \$_.ProcessId -ne $self -and \$_.CommandLine -and -not \$_.CommandLine.Contains('git-hooks/pre-push') -and (\$_.CommandLine.Contains('$tag') -or ((@('bash.exe','sh.exe','timeout.exe','blender.exe','mono.exe','python.exe','perl.exe','dotnet.exe','csc.exe') -contains \$_.Name) -and \$_.CommandLine.Contains('$rt/tools/'))) } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }" > /dev/null 2>&1
  fi
  local p; for p in ${BG_PID[@]+"${BG_PID[@]}"}; do kill "$p" 2>/dev/null; done
  rm -rf "$BG_LOGS" 2>/dev/null; note "DONE ABORTED (interrupted)"; gate_unlock; exit 130
}

# ==== the gate itself: the lock, the traps, the steps (everything above is a definition; Tests/test_parallel_gate.py drives those) ====
trap gate_abort INT TERM
trap gate_unlock EXIT
gate_cancel_watch
gate_lock
note "started ($(git rev-parse --short HEAD 2>/dev/null) $(git branch --show-current 2>/dev/null); $GATE)"

# 1) plugin compiles — dotnet build exits non-zero on ERRORS only (the one benign CS0169 warning is fine).
push_step run "plugin build (dotnet build -c Release)" dotnet build "$ROOT/HumankindAssetFramework.csproj" -c Release --nologo -v q

# 2) unit tests — the pure logic that runs outside Unity (parse/schema/reflection-resolution/smoke rule).
push_step run "plugin unit tests (dotnet test)" dotnet test "$ROOT/Tests/HumankindAssetFramework.Tests.csproj" -c Release --nologo -v q

# The gate's own regression tests head the lane: they time subprocesses (a gate's workers, a shell sourcing the oracle
# wrapper) and timed out under the thirteen parity drills - which the push no longer runs (2026-10-30). The oracle tests
# belong to the parity mode, where the record machinery is used.
push_step run_lane "parallel gate regression tests (completion, paths and verdicts)" python "$ROOT/Tests/test_parallel_gate.py"
# THE PARITY STAMP'S DIGEST IS TAKEN HERE, before any parity drill starts: the verdict's write refuses a different one,
# so a covered file edited during the run - which the drills did not test - gets no stamp (the review of PR #228).
PARITY_START_DIGEST=""
if [ "$GATE" != push ]; then
  PARITY_START_DIGEST="$(bash "$ROOT/tools/check-parity-stamp.sh" --digest)" || { printf 'FAIL — the parity stamp cannot be prepared:\n%s\n' "$PARITY_START_DIGEST"; fail=1; }
fi
parity_step run_lane "blender oracle tests (the recorded Blender oracle: keyed by its inputs, stale-aware, replayable)" python "$ROOT/Tests/test_blender_oracle.py"

# ---- THE GUARD LANE BESIDE THE DRILLS (2026-10-29) ----
# The source guards and the small Python test files are queued on the lane (run_lane: in sequence, one background job);
# the drills and the Unity-backed checks start at once (run_bg). Each works in its own mktemp directory or reads the
# tree only (the reader-gate drill plants in a COPY of the tree since this date). Before this date the guards ran in
# sequence BEFORE the first drill started, nine minutes the drills then followed; now the lane runs under them.
# The logs are printed in declaration order when the last one ends (run_bg_wait), so a failure's output is still
# found under its heading.
# 3) docs guard — relative links resolve, docs/notes/ banners present, no flat-wiki basename collisions. The docs
#    publish to the repo, the Pages site AND the wiki, all resolving relative links, so one bad move breaks three.
push_step run_lane "docs guard (links + notes convention)" bash "$ROOT/tools/check-docs.sh"

# 3b) binding-catalog SURFACE — bindcheck proves the catalog RESOLVES against the game; this proves it COVERS the code.
#     Pure source analysis (no game DLLs), so it belongs in the fast gate; bindcheck stays on the game-update trigger.
push_step run_lane "binding catalog surface (every by-name site catalogued)" bash "$ROOT/tools/check-catalog.sh"

# 3c) hot path — nothing Plugin.Update() runs every frame may still call itself a SPIKE (a label that outlives its
#     experiment is how 1,350 µs/frame hid for weeks). EXPERIMENTAL is allowed only when it names its config gate.
push_step run_lane "hot path (no shipped spikes in Update)" bash "$ROOT/tools/check-hot-path.sh"

# 3d) parse shape — `float x = D; TryParse(…, out x)` silently overwrites D with 0. Four sites carried it before
#     2026-08-23; two were live and NOTHING else could see them (no throw, no log, a plausible number). Source-only.
push_step run_lane "parse shape (no dead-default TryParse)" bash "$ROOT/tools/check-parse-shape.sh"

# 3e) member shape — the same bug class one layer down: `bool x = true; try { x = Convert.ToBoolean(GetMember(…)) }
#     catch {}`. GetMember returns null for a renamed member and Convert.To*(null) does NOT throw, so the catch
#     never runs and the default is dead. Two live sites then did `if (!x) continue;` — work skipped forever.
push_step run_lane "member shape (no dead-sentinel Convert(GetMember))" bash "$ROOT/tools/check-member-shape.sh"

# 3f) the gate that tests 3b and 3e — plants a null-guarded reader wrapper under an unknown name plus the shapes
#     those gates exist to catch, and FAILS if either gate passes. Their helper lists are hand-maintained; a review of
#     PR #48 (2026-09-14) showed a 3-line body window let exactly this wrapper through. Source-only; it plants in
#     its own copy of the tree, so it runs beside the gates it tests.
push_step run_lane "reader-gate drill (the gates refuse an unknown wrapper)" bash "$ROOT/tools/drill-reader-gates.sh"

# 4) registry schema parity — IN-REPO since 2026-08-24. The authoring tools moved from the ENCReload Unity project
#    into editor/, so both halves of the contract now live here: the guard compares the plugin's Newtonsoft + regex
#    parse against the baker's ModelDef with no sibling checkout, no best-effort [SKIP] branch, and no direction it
#    can only be checked from. That [SKIP] used to fire on every runner, which is exactly how a plugin-side change
#    went unguarded until somebody happened to push the other repo.
push_step run_lane "registry schema parity" bash "$ROOT/tools/check_schema_parity.sh"

# 4b) THE PARITY STAMP (2026-10-31). The push runs no Blender-parity drill (PR #222), and until today nothing told it
#     whether the ports had EVER been held to Blender as they are: a ported script or its C# port could change and
#     push green with the parity claim untested (the 2026-10-31 review, P1). The parity mode's PASS writes
#     tools/parity-stamp.txt, a digest of every file the parity drills compile, run or read (derived from the drill
#     scripts, not hand-listed); this step compares it with those files as they are now and SKIPs - which the verdict
#     counts as a FAIL unless HAF_ALLOW_SKIP=1 says, meaning it - when a covered file changed since, naming the files.
#     No Blender, no record: a few seconds of git. Its drill, in a shared clone, proves the guard refuses a changed
#     Blender script, C# port and fixture, ignores an uncovered file, and refuses a missing stamp.
#     The stamp step is the PUSH mode's alone: in the combined mode the parity drills themselves are the claim and write
#     a fresh stamp at the verdict, which a stale-stamp SKIP beside them would have forbidden (the review of PR #228).
[ "$GATE" != push ] || run_lane "parity stamp (every file the parity drills cover is as the last Blender-parity PASS held it; SKIP when one changed since)" bash "$ROOT/tools/check-parity-stamp.sh"
push_step run_lane "parity stamp drill (the guard refuses a changed covered file, in a shared clone, and leaves the author's repository alone)" bash "$ROOT/tools/check-parity-stamp.sh" --self-test

# 5) editor source guards — also in-repo since the move. Both guard editor/, so they belong with it.
# CI had this one and the hook did not (2026-10-12): a vehicle_rig.py helper change passed the hook and failed after the push.
push_step run_lane "vehicle lab math tests (oar calibration guard)" python "$ROOT/Tests/test_vehicle_rig_math.py"
push_step run_lane "recipe migration tests (a Vehicle Lab recipe across a Model Workshop fuse)" python "$ROOT/Tests/test_recipe_migrate.py"
#    5a) The editor compiles. Roslyn against Unity's own reference assemblies — the ONE check that needs a licensed
#        Unity install (UnityEditor.dll + the MonoBleedingEdge profile), so it stays hook-only and never runs in CI.
# No guard: the script itself fails LOUD when Unity/dotnet/Newtonsoft are absent. The old guard here was
# always-true (it accepted the committed .rsp as proof of Unity), and the script then PASSed while compiling
# nothing on any machine without Unity at the author's path — a fabricated green found 2026-09-02.
push_step run_bg "editor scripts compile (Roslyn)" bash "$ROOT/tools/editor_compile_check.sh"
#    5a') The shared registry engine (districts, formations, sounds) RUN against real files: its own source, Unity's
#         Roslyn and Mono, tiny stand-ins for JsonUtility/EditorPrefs. Same Unity prerequisite as 5a, same loud FAIL.
push_step run_bg "registry engine drill (SingleSourceRegistry on real files)" bash "$ROOT/tools/registry_engine_drill.sh"
push_step run_bg "backup dedup drill (BackupDedup on real files: unchanged means the bytes)" bash "$ROOT/tools/backup_dedup_drill.sh"
push_step run_bg "GPU buffer hook drill (real Harmony adapters and configuration)" bash "$ROOT/tools/buffer_hook_drill.sh"
# THE PARITY DRILLS ARE NOT IN THE PUSH (2026-10-30; user: "for pushes to github it should no longer use any blender!!!").
# They hold the C# ports to Blender's own output and run in the parity mode only (HAF_GATE=parity: tools/blender_parity.sh,
# the Bake Tests row "Do the C# ports still match Blender?"), with Blender (HAF_ORACLE=live) - or, asked for explicitly,
# against a record of it (tools/blender_oracle.sh: HAF_ORACLE=record stores one, replay reads one; a missing or stale
# record is that drill's SKIP, which fails the run and names what changed). Until 2026-10-28 they ran Blender on every push;
# until this date they replayed a record that went stale with every script change and asked for Blender again to refresh.
# The blender exit drill tests Blender's own exit behaviour and has no record: it runs when Blender does.
[ "$GATE" = push ] || [ "$HAF_ORACLE" = replay ] || run_bg "blender exit drill (a crashed script fails the process)" bash "$ROOT/tools/blender_exit_drill.sh"
parity_step run_bg "GLB reader drill (every registry GLB read; a sample compared with Blender; SKIP without the project)" bash "$ROOT/tools/glb_reader_drill.sh"
parity_step run_bg "GLB writer drill (every registry GLB written, read back equal; a sample as Blender imports it; SKIP without the project)" bash "$ROOT/tools/glb_writer_drill.sh"
parity_step run_bg "vehicle probe drill (the Lab's probe in C#: every source probed, a sample's rows equal to Blender's own probe)" bash "$ROOT/tools/vehicle_probe_drill.sh"
push_step run_lane "vehicle probe comparator tests (diagnostic rows must be complete)" python "$ROOT/Tests/test_compare_vehicle_probe.py"
push_step run_bg "native kernels drill (the probe's C++ kernels: hafkernels.dll equal to a fresh build of native/, and every output hash on every recipe source equal to the managed twins'; SKIP without g++)" bash "$ROOT/tools/native_kernels_drill.sh"
push_step run_lane "vehicle probe jobs tests (the recipes' arguments as the Lab formats them)" python "$ROOT/Tests/test_probe_jobs.py"
parity_step run_bg "mesh layout drill (Blender's vertex and edge order in C#: a sample's edge lists equal to Blender's own)" bash "$ROOT/tools/decimate_drill.sh"
parity_step run_bg "prep drill (the reduce plus the glTF exporter's mesh layout in C#: a sample equal to what Blender's prep_model.py wrote)" bash "$ROOT/tools/prep_drill.sh"
parity_step run_bg "deploy drill (Blender's posed state of an imported animation in C#: every object matrix and evaluated property at up to thirteen frames equal to Blender's own, the sign of a zero included; and deploy_convert.py's decisions and the armature it builds - its log, its parts, its bones at rest, its anchors and the scene it leaves - against the script itself, run up to the bake, and then the bake: every key of the action to the bit)" bash "$ROOT/tools/deploy_drill.sh"
parity_step run_bg "Workshop compaction drill (what no node uses is left out; every part reads as before - GLB reader and Blender; SKIP without the project)" bash "$ROOT/tools/workshop_compact_drill.sh"
parity_step run_bg "rig_anim drill (step (a) the stage dump, Blender ran rig_anim.py on every animated recipe as the Factory does, cut at its four stages, the scene after each on record, complete and consistent, the census of the branches the recipes take; steps (b) to (e), all four stages in C#: BlenderRigAnim's scene equal to Blender's row for row on every dumped job after each stage, the albedo by its dimensions; step (f): the GLB rig route - BlenderRigExport's file equal to the one Blender's glTF exporter writes of the same scene, nodes, skins, primitives, materials, channels and bytes; step (g): the Factory's wrapper (BlenderRigAnimInProcess) writes each exported job's file and names its clips; SKIP without the project)" bash "$ROOT/tools/riganim_drill.sh"
parity_step run_bg "vehicle_rig drill (all five ported stages and the Generate wrapper: saved recipes and fixtures against Blender, GLB bytes and ordered logs, damaged-evidence rejection; SKIP without the recipes)" bash "$ROOT/tools/vehiclerig_drill.sh"
#    5b) The ownership-rebase hand-lists. A field the UI edits but the window's rebase doesn't re-apply is thrown
#        away on every Save — silent, and the reason this gate exists. Pure source analysis, so CI can run it too.
push_step run_lane "hand-list gate (ownership rebases)" bash "$ROOT/tools/check_handlists.sh"

#    5c) Every path in editor/ has a COMMITTED .meta. Unity generates missing ones under Assets/ and cannot in a
#        package folder, so the working copy is always right and only the commit is short — invisible from the
#        authoring side by construction. Broke the install three times on 2026-08-24 (package.json + asmdef, then
#        HafPackageContext.cs, then the Plugins FOLDER); an ignored asmdef means the package compiles to nothing.
push_step run_lane "package meta (every editor/ path has one)" bash "$ROOT/tools/check-package-meta.sh"
push_step run_lane "bake-test golden checksums (SKIP without the project)" bash "$ROOT/tools/check-goldens.sh"
push_step run_lane "exit status (no gate script reads a status from the end of a pipeline)" bash "$ROOT/tools/check-exit-status.sh"
run_lane_go
run_bg_wait

verdict; exit $?
