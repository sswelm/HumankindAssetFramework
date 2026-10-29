#!/usr/bin/env bash
# check.sh — the one fast pre-push gate for the HAF plugin. Runs every quick guard so a push can't land a broken
# build, a failing unit test, a broken doc link, or a drifted registry schema. Wired as the pre-push hook (tools/git-hooks/pre-push);
# also runnable by hand any time:  bash tools/check.sh   (lowercase tools/ — this repo, unlike ENCReload's Tools/)
#
# Deliberately NOT here (too slow / need Unity or the game): deploy_regression.sh (Blender golden-master), the
# in-editor Feature Test, and the in-game binding report (haf_bindings_report.txt). The source-only steps take
# seconds; with the parity drills (in parallel since 2026-10-11, everything after the build at once since 2026-10-29,
# Blender replaced by its record since 2026-10-28) the whole gate takes about as long as its longest drill.
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
  ( while sleep "${HAF_GATE_LOCK_BEAT:-20}"; do [ -d "$LOCK" ] || exit 0; date +%s > "$LOCK/beat.$$" 2>/dev/null && mv -f "$LOCK/beat.$$" "$LOCK/beat" 2>/dev/null; done ) & LOCK_BEAT_PID=$!
}
gate_unlock() {  # only the holder removes the lock: a waiter that gave up must not free another gate's
  [ -z "$LOCK_BEAT_PID" ] || kill "$LOCK_BEAT_PID" 2>/dev/null; LOCK_BEAT_PID=""
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
  if [ "$n" -eq 0 ]; then printf 'CHECK: PASS — safe to push.\n'; note "DONE PASS"; return 0; fi
  if [ "${HAF_ALLOW_SKIP:-0}" = "1" ]; then
    printf 'CHECK: PASS WITH %d SKIP — accepted by HAF_ALLOW_SKIP=1; these claims were NOT tested on this machine:%s\n' "$n" "$list"
    note "DONE PASS ($n skipped, allowed by HAF_ALLOW_SKIP=1)"; return 0
  fi
  printf 'CHECK: FAIL — %d step(s) SKIPPED: their claim was NOT tested on this machine, and a gate that cannot see is not green:%s\n' "$n" "$list"
  printf 'Install the missing prerequisite (the modding project, HAF_UNITY_PROJECT), refresh the Blender record (tools/refresh_blender_oracle.sh, Blender needed), or, meaning it: HAF_ALLOW_SKIP=1 git push\n'
  note "DONE FAIL ($n skipped)"; return 1
}
# The drills are independent of one another (each works in its own mktemp directory and builds its own exe), and the
# Blender-backed ones took the gate from a minute to 25 in sequence: they run AT ONCE, each into its own log, and the
# logs are printed in declaration order; each worker reports progress and records its duration when it finishes.
BG_LOGS=$(mktemp -d); bg_n=0; declare -a BG_LABEL BG_PID BG_T0
export TMPDIR="$BG_LOGS"   # every step's mktemp lands under the gate's own directory: what an abort has to find (gate_abort)
run_bg() {  # run_bg <label> <command...>: started now, judged by run_bg_wait
  local label="$1"; shift; local log="$BG_LOGS/$bg_n"
  BG_LABEL[$bg_n]="$label"; BG_T0[$bg_n]=$(date +%s)
  local t0="${BG_T0[$bg_n]}"
  ( timeout -k 30 "$STEP_TIMEOUT" "$@" > "$log.out" 2>&1; rc=$?; t1=$(date +%s)
    echo "$t1" > "$log.end"
    note "$(word "$rc") $label ($((t1 - t0)) s)"
    echo "$rc" > "$log.rc"
  ) &
  BG_PID[$bg_n]=$!; bg_n=$((bg_n + 1))
}
run_bg_wait() {
  local i t1 completed rc
  for ((i = 0; i < bg_n; i++)); do
    wait "${BG_PID[$i]}"
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
gate_lock
trap gate_abort INT TERM
trap gate_unlock EXIT
note "started ($(git rev-parse --short HEAD 2>/dev/null) $(git branch --show-current 2>/dev/null))"

# 1) plugin compiles — dotnet build exits non-zero on ERRORS only (the one benign CS0169 warning is fine).
run "plugin build (dotnet build -c Release)" dotnet build "$ROOT/HumankindAssetFramework.csproj" -c Release --nologo -v q

# 2) unit tests — the pure logic that runs outside Unity (parse/schema/reflection-resolution/smoke rule).
run "plugin unit tests (dotnet test)" dotnet test "$ROOT/Tests/HumankindAssetFramework.Tests.csproj" -c Release --nologo -v q

# The two test files that time SUBPROCESSES (a gate's workers, a shell sourcing the oracle wrapper) run first, beside
# each other only: under the thirteen drills they timed out (the refresh of 2026-10-29: five tests at the 60 s limit),
# as they did under a second gate before the lock. They take about a minute together.
run_bg "parallel gate regression tests (completion, paths and verdicts)" python "$ROOT/Tests/test_parallel_gate.py"
run_bg "blender oracle tests (the recorded Blender oracle: keyed by its inputs, stale-aware, replayable)" python "$ROOT/Tests/test_blender_oracle.py"
run_bg_wait

# ---- EVERYTHING ELSE RUNS AT ONCE (2026-10-29) ----
# The source guards, the remaining Python test files and the drills are independent of one another: each reads the tree or works
# in its own mktemp directory, and none writes into the checkout (the reader-gate drill plants in a COPY of the tree
# since this date). In sequence the guards alone took nine minutes before the first drill started, and the gate's wall
# was those nine plus its longest drill; now the wall is the longest step. The logs are printed in declaration order
# when the last one ends (run_bg_wait), so a failure's output is still found under its heading.
# 3) docs guard — relative links resolve, docs/notes/ banners present, no flat-wiki basename collisions. The docs
#    publish to the repo, the Pages site AND the wiki, all resolving relative links, so one bad move breaks three.
run_bg "docs guard (links + notes convention)" bash "$ROOT/tools/check-docs.sh"

# 3b) binding-catalog SURFACE — bindcheck proves the catalog RESOLVES against the game; this proves it COVERS the code.
#     Pure source analysis (no game DLLs), so it belongs in the fast gate; bindcheck stays on the game-update trigger.
run_bg "binding catalog surface (every by-name site catalogued)" bash "$ROOT/tools/check-catalog.sh"

# 3c) hot path — nothing Plugin.Update() runs every frame may still call itself a SPIKE (a label that outlives its
#     experiment is how 1,350 µs/frame hid for weeks). EXPERIMENTAL is allowed only when it names its config gate.
run_bg "hot path (no shipped spikes in Update)" bash "$ROOT/tools/check-hot-path.sh"

# 3d) parse shape — `float x = D; TryParse(…, out x)` silently overwrites D with 0. Four sites carried it before
#     2026-08-23; two were live and NOTHING else could see them (no throw, no log, a plausible number). Source-only.
run_bg "parse shape (no dead-default TryParse)" bash "$ROOT/tools/check-parse-shape.sh"

# 3e) member shape — the same bug class one layer down: `bool x = true; try { x = Convert.ToBoolean(GetMember(…)) }
#     catch {}`. GetMember returns null for a renamed member and Convert.To*(null) does NOT throw, so the catch
#     never runs and the default is dead. Two live sites then did `if (!x) continue;` — work skipped forever.
run_bg "member shape (no dead-sentinel Convert(GetMember))" bash "$ROOT/tools/check-member-shape.sh"

# 3f) the gate that tests 3b and 3e — plants a null-guarded reader wrapper under an unknown name plus the shapes
#     those gates exist to catch, and FAILS if either gate passes. Their helper lists are hand-maintained; a review of
#     PR #48 (2026-09-14) showed a 3-line body window let exactly this wrapper through. Source-only; it plants in
#     its own copy of the tree, so it runs beside the gates it tests.
run_bg "reader-gate drill (the gates refuse an unknown wrapper)" bash "$ROOT/tools/drill-reader-gates.sh"

# 4) registry schema parity — IN-REPO since 2026-08-24. The authoring tools moved from the ENCReload Unity project
#    into editor/, so both halves of the contract now live here: the guard compares the plugin's Newtonsoft + regex
#    parse against the baker's ModelDef with no sibling checkout, no best-effort [SKIP] branch, and no direction it
#    can only be checked from. That [SKIP] used to fire on every runner, which is exactly how a plugin-side change
#    went unguarded until somebody happened to push the other repo.
run_bg "registry schema parity" bash "$ROOT/tools/check_schema_parity.sh"

# 5) editor source guards — also in-repo since the move. Both guard editor/, so they belong with it.
# CI had this one and the hook did not (2026-10-12): a vehicle_rig.py helper change passed the hook and failed after the push.
run_bg "vehicle lab math tests (oar calibration guard)" python "$ROOT/Tests/test_vehicle_rig_math.py"
run_bg "recipe migration tests (a Vehicle Lab recipe across a Model Workshop fuse)" python "$ROOT/Tests/test_recipe_migrate.py"
#    5a) The editor compiles. Roslyn against Unity's own reference assemblies — the ONE check that needs a licensed
#        Unity install (UnityEditor.dll + the MonoBleedingEdge profile), so it stays hook-only and never runs in CI.
# No guard: the script itself fails LOUD when Unity/dotnet/Newtonsoft are absent. The old guard here was
# always-true (it accepted the committed .rsp as proof of Unity), and the script then PASSed while compiling
# nothing on any machine without Unity at the author's path — a fabricated green found 2026-09-02.
run_bg "editor scripts compile (Roslyn)" bash "$ROOT/tools/editor_compile_check.sh"
#    5a') The shared registry engine (districts, formations, sounds) RUN against real files: its own source, Unity's
#         Roslyn and Mono, tiny stand-ins for JsonUtility/EditorPrefs. Same Unity prerequisite as 5a, same loud FAIL.
run_bg "registry engine drill (SingleSourceRegistry on real files)" bash "$ROOT/tools/registry_engine_drill.sh"
run_bg "backup dedup drill (BackupDedup on real files: unchanged means the bytes)" bash "$ROOT/tools/backup_dedup_drill.sh"
# BLENDER DOES NOT RUN IN THE GATE (2026-10-28, user: "I don't want to use it anymore in the push, only in the bake test").
# Every parity drill below replays a RECORD of its Blender invocations (tools/blender_oracle.sh: HAF_ORACLE=replay, the
# default), keyed by the inputs' bytes and the scripts; tools/refresh_blender_oracle.sh runs this gate with HAF_ORACLE=record
# and Blender to make the record. A record that is missing or stale is that drill's SKIP, which fails the push and names what
# changed. The blender exit drill tests Blender's own exit behaviour and has no record: it runs with the refresh only.
[ "${HAF_ORACLE:-replay}" != "record" ] || run_bg "blender exit drill (a crashed script fails the process)" bash "$ROOT/tools/blender_exit_drill.sh"
run_bg "GLB reader drill (every registry GLB read; a sample compared with the Blender record; SKIP without the project or the record)" bash "$ROOT/tools/glb_reader_drill.sh"
run_bg "GLB writer drill (every registry GLB written, read back equal; a sample as the Blender record imports it; SKIP without the project or the record)" bash "$ROOT/tools/glb_writer_drill.sh"
run_bg "vehicle probe drill (the Lab's probe in C#: every source probed, a sample's rows equal to Blender's own probe, from the record)" bash "$ROOT/tools/vehicle_probe_drill.sh"
run_bg "vehicle probe comparator tests (diagnostic rows must be complete)" python "$ROOT/Tests/test_compare_vehicle_probe.py"
run_bg "native kernels drill (the probe's C++ kernels: hafkernels.dll equal to a fresh build of native/, and every output hash on every recipe source equal to the managed twins'; SKIP without g++)" bash "$ROOT/tools/native_kernels_drill.sh"
run_bg "vehicle probe jobs tests (the recipes' arguments as the Lab formats them)" python "$ROOT/Tests/test_probe_jobs.py"
run_bg "mesh layout drill (Blender's vertex and edge order in C#: a sample's edge lists equal to Blender's own, from the record)" bash "$ROOT/tools/decimate_drill.sh"
run_bg "prep drill (the reduce plus the glTF exporter's mesh layout in C#: a sample equal to what Blender's prep_model.py wrote, from the record)" bash "$ROOT/tools/prep_drill.sh"
run_bg "deploy drill (from the Blender record: Blender's posed state of an imported animation in C#: every object matrix and evaluated property at up to thirteen frames equal to Blender's own, the sign of a zero included; and deploy_convert.py's decisions and the armature it builds - its log, its parts, its bones at rest, its anchors and the scene it leaves - against the script itself, run up to the bake, and then the bake: every key of the action to the bit)" bash "$ROOT/tools/deploy_drill.sh"
run_bg "Workshop compaction drill (what no node uses is left out; every part reads as before - GLB reader and the Blender record; SKIP without the project or the record)" bash "$ROOT/tools/workshop_compact_drill.sh"
run_bg "rig_anim drill (step (a) the stage dump, from the Blender record: Blender ran rig_anim.py on every animated recipe as the Factory does, cut at its four stages, the scene after each on record, complete and consistent, the census of the branches the recipes take; steps (b) to (e), all four stages in C#: BlenderRigAnim's scene equal to Blender's row for row on every dumped job after each stage, the albedo by its dimensions; step (f): the GLB rig route - BlenderRigExport's file equal to the one Blender's glTF exporter writes of the same scene, nodes, skins, primitives, materials, channels and bytes; step (g): the Factory's wrapper (BlenderRigAnimInProcess) writes each exported job's file and names its clips; SKIP without the project)" bash "$ROOT/tools/riganim_drill.sh"
run_bg "vehicle_rig drill (all five ported stages and the Generate wrapper: saved recipes and fixtures against the Blender record, GLB bytes and ordered logs, damaged-evidence rejection; SKIP without the record or the recipes)" bash "$ROOT/tools/vehiclerig_drill.sh"
#    5b) The ownership-rebase hand-lists. A field the UI edits but the window's rebase doesn't re-apply is thrown
#        away on every Save — silent, and the reason this gate exists. Pure source analysis, so CI can run it too.
run_bg "hand-list gate (ownership rebases)" bash "$ROOT/tools/check_handlists.sh"

#    5c) Every path in editor/ has a COMMITTED .meta. Unity generates missing ones under Assets/ and cannot in a
#        package folder, so the working copy is always right and only the commit is short — invisible from the
#        authoring side by construction. Broke the install three times on 2026-08-24 (package.json + asmdef, then
#        HafPackageContext.cs, then the Plugins FOLDER); an ignored asmdef means the package compiles to nothing.
run_bg "package meta (every editor/ path has one)" bash "$ROOT/tools/check-package-meta.sh"
run_bg "bake-test golden checksums (SKIP without the project)" bash "$ROOT/tools/check-goldens.sh"
run_bg "exit status (no gate script reads a status from the end of a pipeline)" bash "$ROOT/tools/check-exit-status.sh"
run_bg_wait

verdict; exit $?
