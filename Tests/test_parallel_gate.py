"""Exercise the actual gate shell functions with small commands, without Unity or Blender."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
if os.name == "nt":
    BASH = str(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe")
else:
    BASH = shutil.which("bash")


def write(path, text):
    path.write_text(text, encoding="utf-8", newline="\n")


class ParallelGateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="haf-gate-test-")
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name)
        self.env = dict(os.environ, HAF_PROGRESS=(self.path / "progress.md").as_posix())
        self.env.pop("HAF_ALLOW_SKIP", None)   # the machine's own setting must not decide a test
        self.env.pop("HAF_GATE", None)         # nor the mode of a shell this runs in: each test says its own, or gets the gate's default
        write(self.path / "progress.md", "")

    def script(self, text):
        path = self.path / "test.sh"
        write(path, 'set -uo pipefail\nROOT="$1"\n' + text)
        return [BASH, path.as_posix(), self.path.as_posix()]

    def run_shell(self, text, timeout=15):
        return subprocess.run(self.script(text), env=self.env, capture_output=True, text=True, timeout=timeout)

    def helpers(self):
        # every definition of the gate, up to the line where the real gate takes the lock and starts its steps
        source = (ROOT / "tools/check.sh").read_text(encoding="utf-8")
        return source[source.index("fail=0\n"):source.index("# ==== the gate itself")]

    def tampers(self):
        source = (ROOT / "tools/deploy_drill.sh").read_text(encoding="utf-8")
        return source[source.index('PAR="$TMPD/par";'):source.index('prerun_tampers "$WTMP/missing_dec"')]

    def test_progress_reports_completion_before_an_earlier_job_finishes_and_uses_real_duration(self):
        write(self.path / "clock", "100\n")
        # A controlled clock and release file avoid timing assumptions on a busy CI runner.
        text = '''date() { if [ "$1" = +%s ]; then cat "$ROOT/clock"; else echo 00:00; fi; }
'''+self.helpers()+'''
run_bg slow bash -c 'while [ ! -f "$1/release" ]; do sleep .05; done; echo slow-output' _ "$ROOT"
run_bg fast bash -c 'while [ ! -f "$1/started" ]; do sleep .05; done; echo 102 > "$1/clock"; echo fast-output' _ "$ROOT"
touch "$ROOT/started"
run_bg_wait
exit "$fail"
'''
        proc = subprocess.Popen(self.script(text), env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            # The clock is controlled, so this deadline is only how long the test will WAIT for the worker's note:
            # 3 s was missed twice on 2026-10-11 under another gate's load (the worker took 100+ s to be scheduled).
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                if "PASS fast" in (self.path / "progress.md").read_text():
                    break
                time.sleep(.05)
            before_release = (self.path / "progress.md").read_text()
        finally:
            write(self.path / "clock", "110\n")
            write(self.path / "release", "")
            out, err = proc.communicate(timeout=60)
        self.assertEqual(proc.returncode, 0, err)
        self.assertIn("PASS fast (2 s)", before_release)
        self.assertNotIn("PASS slow", before_release)
        self.assertIn("[PASS] fast (2 s)", out)
        self.assertLess(out.index("=== slow ==="), out.index("=== fast ==="))
        self.assertIn("slow-output", out)
        self.assertIn("fast-output", out)

    def scan_shapes(self, mode, path):
        env = dict(self.env, SHAPE_READER_RE=r"(?:\w+\.)?(?:GetMember)\b\s*\(")
        command = 'perl "$1" member "$SHAPE_READER_RE" "$2"' if mode == "member" else 'perl "$1" parse "$2"'
        return subprocess.run([BASH, "-c", command, "_", (ROOT / "tools/shape-scan.pl").as_posix(), path.as_posix()],
                              env=env, capture_output=True, text=True, timeout=15)

    def test_shape_scanner_catches_utf16_source_as_well_as_utf8(self):
        for mode, text in (("parse", 'float value = 1; float.TryParse("bad", out value);'),
                           ("member", 'bool value = true; try { value = Convert.ToBoolean(GetMember(o,"A")); } catch {}')):
            for encoding in ("utf-8", "utf-16", "utf-16-be"):
                with self.subTest(mode=mode, encoding=encoding):
                    path = self.path / "source.cs"; data = text.encode(encoding)
                    if encoding == "utf-16-be": data = b"\xfe\xff" + data
                    path.write_bytes(data)
                    result = self.scan_shapes(mode, path)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertIn("HITS 1", result.stdout)

    def test_shape_scanner_still_ignores_comments_after_decoding_utf16(self):
        path = self.path / "source.cs"
        path.write_bytes('// float value = 1; float.TryParse("bad", out value);'.encode("utf-16"))
        result = self.scan_shapes("parse", path)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("HITS 0", result.stdout)

    def test_shape_scanner_refuses_unreadable_source(self):
        result = self.scan_shapes("parse", self.path / "missing.cs")
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("HITS 0", result.stdout)

    def test_the_push_mode_runs_no_parity_step_and_the_parity_mode_no_push_step(self):
        script = self.helpers()+'''
push_step run_lane pushed bash -c 'echo pushed-output'
parity_step run_bg parity bash -c 'echo parity-output'
run_bg_wait
exit "$fail"
'''
        for mode, expected, absent in (("push", "pushed", "parity"), ("parity", "parity", "pushed"), ("all", "pushed", None)):
            with self.subTest(mode=mode):
                self.env["HAF_GATE"] = mode
                result = self.run_shell(script)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn("[PASS] " + expected, result.stdout)
                if absent: self.assertNotIn(absent, result.stdout)
                else: self.assertIn("[PASS] parity", result.stdout)
        self.env["HAF_GATE"] = "nonsense"
        result = self.run_shell(script)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("HAF_GATE must be push, parity or all", result.stdout + result.stderr)
        del self.env["HAF_GATE"]

    def test_pre_push_cannot_inherit_a_parity_only_gate(self):
        # Execute the actual hook against a tiny fake checkout, without running the expensive gate.
        root = self.path / "repo with spaces"
        (root / "tools").mkdir(parents=True)
        write(root / "tools/check.sh", 'printf "MODE=%s\\n" "$HAF_GATE"\n')
        hook = (ROOT / "tools/git-hooks/pre-push").as_posix()
        self.env["HAF_GATE"] = "parity"
        self.env["FAKE_ROOT"] = root.as_posix()
        result = self.run_shell('''git() { printf '%s\\n' "$FAKE_ROOT"; }; export -f git
printf 'refs/heads/test abc refs/heads/test def\\n' | bash "''' + hook + '''"
''')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("MODE=push", result.stdout)

    def test_real_step_declarations_are_routed_to_their_gate(self):
        source = (ROOT / "tools/check.sh").read_text(encoding="utf-8")
        steps = source[source.index("# ==== the gate itself"):]
        # Run every actual registration with inert commands, including the synchronous build/test registrations.
        stubs = '''
gate_lock() { :; }; gate_cancel_watch() { :; }; gate_unlock() { :; }
note() { :; }; run_lane_go() { :; }; run_bg_wait() { :; }; verdict() { :; }
run() { echo "STEP $*"; }; run_bg() { run "$@"; }; run_lane() { run "$@"; }
'''
        for mode in ("push", "parity", "all"):
            self.env["HAF_GATE"] = mode
            self.env["HAF_ORACLE"] = "live"
            result = self.run_shell(self.helpers() + stubs + steps)
            self.assertEqual(result.returncode, 0, result.stderr)
            for label in ("plugin build", "plugin unit tests", "native kernels drill", "exit status"):
                self.assertEqual("STEP " + label in result.stdout, mode != "parity", result.stdout)
            for label in ("blender exit drill", "GLB reader drill", "GLB writer drill", "vehicle probe drill",
                          "mesh layout drill", "prep drill", "deploy drill", "Workshop compaction drill",
                          "rig_anim drill", "vehicle_rig drill", "blender oracle tests"):
                self.assertEqual("STEP " + label in result.stdout, mode != "push", result.stdout)

    def test_gate_stop_request_signals_the_shell_before_it_takes_a_lock(self):
        self.env["HAF_GATE_CANCEL_FILE"] = (self.path / "cancel").as_posix()
        write(self.path / "cancel", "stop")
        result = self.run_shell(self.helpers() + '''
trap 'echo STOPPED; gate_unlock; exit 130' TERM
trap gate_unlock EXIT
gate_cancel_watch
for i in {1..30}; do sleep .1; done
exit 2
''')
        self.assertEqual(result.returncode, 130, result.stdout + result.stderr)
        self.assertIn("STOPPED", result.stdout)

    def test_unlock_reaps_a_long_heartbeat_without_holding_output_open(self):
        self.env["HAF_GATE_LOCK"] = (self.path / "own.lock").as_posix()
        self.env["HAF_GATE_LOCK_BEAT"] = "60"
        result = self.run_shell(self.helpers() + '''
trap gate_unlock EXIT
gate_lock
sleep .1
gate_unlock
echo RELEASED
''')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("RELEASED", result.stdout)
        self.assertFalse((self.path / "own.lock").exists())

    def test_lane_steps_run_in_order_each_judged_on_its_own_beside_the_parallel_steps(self):
        result = self.run_shell(self.helpers()+'''
run_lane first bash -c 'echo first-output; echo first > "$1/order"' _ "$ROOT"
run_lane broken bash -c 'echo broken-output; echo broken >> "$1/order"; exit 7' _ "$ROOT"
run_lane third bash -c 'echo third-output; echo third >> "$1/order"' _ "$ROOT"
run_bg beside bash -c 'echo beside-output'
run_lane_go
run_bg_wait
exit "$fail"
''')
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertEqual((self.path / "order").read_text().split(), ["first", "broken", "third"])
        for expected in ("[PASS] first", "[FAIL] broken", "[PASS] third", "[PASS] beside", "first-output", "broken-output", "third-output", "beside-output"):
            self.assertIn(expected, result.stdout)
        self.assertLess(result.stdout.index("=== first ==="), result.stdout.index("=== broken ==="))
        self.assertLess(result.stdout.index("=== third ==="), result.stdout.index("=== beside ==="))
        progress = (self.path / "progress.md").read_text()
        for expected in ("PASS first", "FAIL broken", "PASS third", "PASS beside"):
            self.assertIn(expected, progress)
        self.assertNotIn("not a child", result.stderr)

    def test_a_lane_nobody_started_still_runs_under_the_wait(self):
        result = self.run_shell(self.helpers()+'''
run_lane only bash -c 'echo only-output'
run_bg_wait
exit "$fail"
''')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("[PASS] only", result.stdout)
        self.assertIn("only-output", result.stdout)

    def test_completed_metadata_cannot_leak_between_phases(self):
        # Model failed cleanup and refusal to overwrite old files. This is independent of OS/root permissions.
        result = self.run_shell(self.helpers()+'''
find() { return 0; }
run_bg first true
run_bg_wait
set -o noclobber
run_bg must_fail bash -c 'exit 7'
run_bg_wait
exit "$fail"
''')
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertNotIn("[PASS] must_fail", result.stdout)

    def test_a_step_cannot_run_when_stale_log_cleanup_fails(self):
        for runner in ("run_bg", "run_lane"):
            with self.subTest(runner=runner):
                result = self.run_shell(self.helpers()+'''
rm() { return 1; }
'''+runner+''' must_not_run bash -c 'echo ran > "$1/ran"' _ "$ROOT"
run_bg_wait
exit "$fail"
''')
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertFalse((self.path / "ran").exists())
                self.assertIn("cannot clear old step metadata", result.stdout)

    def test_a_missing_lane_command_cannot_reexecute_the_previous_step(self):
        result = self.run_shell(self.helpers()+'''
run_lane first bash -c 'echo first >> "$1/order"' _ "$ROOT"
run_lane missing bash -c 'echo missing >> "$1/order"' _ "$ROOT"
rm "$BG_LOGS/1.cmd"
run_bg_wait
exit "$fail"
''')
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("[FAIL] missing", result.stdout)
        self.assertEqual((self.path / "order").read_text().split(), ["first"])

    def test_a_background_failure_cannot_be_hidden_by_a_later_success(self):
        result = self.run_shell(self.helpers()+'''
run_bg broken bash -c 'echo broken-output; exit 7'
run_bg good bash -c 'echo good-output'
run_bg_wait
exit "$fail"
''')
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("[FAIL] broken", result.stdout)
        self.assertIn("[PASS] good", result.stdout)
        self.assertIn("FAIL broken", (self.path / "progress.md").read_text())

    def test_missing_background_status_fails_closed(self):
        result = self.run_shell(self.helpers()+'''
run_bg good bash -c 'echo good-output'
wait "${BG_PID[0]}"
rm "$BG_LOGS/0.rc"
run_bg_wait
exit "$fail"
''')
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("[FAIL] good", result.stdout)

    def test_missing_or_malformed_completion_metadata_fails_closed(self):
        for metadata in (None, "garbage"):
            with self.subTest(metadata=metadata):
                change = 'rm "$BG_LOGS/0.end"' if metadata is None else 'echo garbage > "$BG_LOGS/0.end"'
                result = self.run_shell(self.helpers()+'''
run_bg good bash -c 'echo good-output'
wait "${BG_PID[0]}"
'''+change+'''
run_bg_wait
exit "$fail"
''')
                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIn("[FAIL] good", result.stdout)

    # SKIP (exit 3) is a third verdict, not a PASS (2026-10-11): a drill whose prerequisite is absent must be printed as
    # [SKIP], recorded as SKIP, and must fail the gate unless HAF_ALLOW_SKIP=1 accepts a partial gate on purpose.
    SKIP_SCRIPT = '''
run_bg skipper bash -c 'echo "SKIP — nothing to compare on this machine"; exit 3'
run_bg good bash -c 'echo good-output'
run_bg_wait
run serial-skip bash -c 'echo "SKIP — serial"; exit 3'
verdict; exit $?
'''

    def test_a_skipped_step_is_reported_as_skip_and_fails_the_verdict(self):
        result = self.run_shell(self.helpers()+self.SKIP_SCRIPT)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("[SKIP] skipper", result.stdout)
        self.assertIn("[SKIP] serial-skip", result.stdout)
        self.assertIn("[PASS] good", result.stdout)
        self.assertNotIn("[PASS] skipper", result.stdout)
        self.assertIn("CHECK: FAIL", result.stdout)              # ASCII only: the pipe is decoded in the console's code page
        self.assertIn("2 step(s) SKIPPED", result.stdout)
        progress = (self.path / "progress.md").read_text()
        self.assertIn("SKIP skipper", progress)
        self.assertIn("SKIP serial-skip", progress)
        self.assertIn("DONE FAIL (2 skipped)", progress)

    def test_haf_allow_skip_accepts_a_partial_gate_and_says_so(self):
        self.env["HAF_ALLOW_SKIP"] = "1"
        result = self.run_shell(self.helpers()+self.SKIP_SCRIPT)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("[SKIP] skipper", result.stdout)
        self.assertIn("CHECK: PASS WITH 2 SKIP", result.stdout)
        self.assertIn("DONE PASS (2 skipped, allowed by HAF_ALLOW_SKIP=1)", (self.path / "progress.md").read_text())

    def test_a_skip_never_hides_a_failure_even_when_allowed(self):
        self.env["HAF_ALLOW_SKIP"] = "1"
        result = self.run_shell(self.helpers()+'''
run_bg skipper bash -c 'echo "SKIP — nothing to compare"; exit 3'
run_bg broken bash -c 'echo broken-output; exit 7'
run_bg_wait
verdict; exit $?
''')
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("[SKIP] skipper", result.stdout)
        self.assertIn("[FAIL] broken", result.stdout)
        self.assertIn("CHECK: FAIL", result.stdout)
        self.assertIn("fix the [FAIL] step(s)", result.stdout)
        self.assertNotIn("SKIPPED", result.stdout)                 # the failure verdict, not the skip verdict

    # THE PARITY STAMP (2026-10-31): a parity or combined gate's full PASS writes tools/parity-stamp.txt through
    # tools/check-parity-stamp.sh --write <digest taken before the drills>, before its one CHECK line; a writer that
    # refuses turns the verdict into a FAIL; the push mode never calls it. The writer here is a stub that records its
    # arguments (the review of PR #229: with HAF_GATE=all inherited, the verdict looked for a writer the temporary
    # root did not have, and the combined mode failed its own tests).
    def stamp_writer(self, body):
        (self.path / "tools").mkdir(exist_ok=True)
        write(self.path / "tools/check-parity-stamp.sh", 'printf "%s\\n" "$*" >> "$(dirname "$0")/calls"\n' + body)

    def test_a_full_parity_pass_writes_the_stamp_before_its_check_line(self):
        self.stamp_writer('echo "parity stamp written: stub"; exit 0\n')
        for mode in ("parity", "all"):
            with self.subTest(mode=mode):
                self.env["HAF_GATE"] = mode
                (self.path / "tools/calls").unlink(missing_ok=True)
                result = self.run_shell(self.helpers() + '''
PARITY_START_DIGEST="abc123 150"
run_bg good bash -c 'echo good-output'
run_bg_wait
verdict; exit $?
''')
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual((self.path / "tools/calls").read_text().strip(), "--write abc123 150")
                self.assertLess(result.stdout.index("parity stamp written"), result.stdout.index("CHECK: PASS"))
                self.assertEqual(result.stdout.count("CHECK: "), 1)

    def test_a_refused_stamp_write_fails_the_verdict(self):
        self.stamp_writer('echo "FAIL — a covered file changed DURING the parity run"; exit 1\n')
        self.env["HAF_GATE"] = "parity"
        result = self.run_shell(self.helpers() + '''
PARITY_START_DIGEST="abc123 150"
run_bg good bash -c 'echo good-output'
run_bg_wait
verdict; exit $?
''')
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("changed DURING the parity run", result.stdout)
        self.assertIn("CHECK: FAIL", result.stdout)
        self.assertIn("parity stamp could not be written", result.stdout)
        self.assertIn("DONE FAIL (stamp)", (self.path / "progress.md").read_text())

    def test_the_push_mode_never_writes_the_stamp_and_a_skip_does_not_either(self):
        self.stamp_writer('echo "must not be called"; exit 1\n')
        self.env["HAF_GATE"] = "push"
        result = self.run_shell(self.helpers() + '''
run_bg good bash -c 'echo good-output'
run_bg_wait
verdict; exit $?
''')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("CHECK: PASS", result.stdout)
        self.assertFalse((self.path / "tools/calls").exists())
        self.env["HAF_GATE"] = "all"
        self.env["HAF_ALLOW_SKIP"] = "1"
        result = self.run_shell(self.helpers() + self.SKIP_SCRIPT)
        self.assertEqual(result.returncode, 0, result.stderr)   # allowed skips pass, but a partial gate earns no stamp
        self.assertIn("CHECK: PASS WITH 2 SKIP", result.stdout)
        self.assertFalse((self.path / "tools/calls").exists())

    def test_a_skip_with_missing_completion_metadata_fails_closed(self):
        self.env["HAF_ALLOW_SKIP"] = "1"
        result = self.run_shell(self.helpers()+'''
run_bg skipper bash -c 'echo "SKIP — nothing to compare"; exit 3'
wait "${BG_PID[0]}"
rm "$BG_LOGS/0.end"
run_bg_wait
verdict; exit $?
''')
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("[FAIL] skipper", result.stdout)
        self.assertNotIn("[SKIP] skipper", result.stdout)

    def test_project_drills_need_real_project_inputs_even_with_a_pack_file(self):
        # Exercise the actual final verdict lines without running Unity or Blender.
        guards = {
            "glb_reader_drill.sh": "N_REGISTRY",
            "glb_writer_drill.sh": "N_REGISTRY",
            "decimate_drill.sh": "REGISTRY",
            "prep_drill.sh": "REGISTRY",
            "vehicle_probe_drill.sh": "REGISTRY",
            "workshop_compact_drill.sh": "WORKSHOP",
            "deploy_drill.sh": "n_rec",
        }
        for name, count in guards.items():
            source = (ROOT / "tools" / name).read_text(encoding="utf-8")
            guard = next(line for line in source.splitlines()
                         if line.startswith("[") and "${RESULT#PASS" in line)
            if count == "N_REGISTRY":
                self.assertLess(source.index("N_REGISTRY=${#FILES[@]}"), source.index("FILES+=(\"${FIXTURES[@]}\")"))
            for present in (False, True):
                with self.subTest(drill=name, present=present):
                    value = "one.glb" if present else ""
                    setup = f'PACK=pack.json; PROJECT=project; RESULT="PASS — fixtures checked"; N_REGISTRY={int(present)}; n_rec={int(present)}; REGISTRY=(); WORKSHOP=()\n'
                    if present:
                        setup += f'{count}+=("{value}")\n' if count in ("REGISTRY", "WORKSHOP") else ""
                    result = self.run_shell(setup + guard + '\necho PASS\n')
                    self.assertEqual(result.returncode, 0 if present else 3, result.stderr)
                    self.assertIn("PASS" if present else "SKIP", result.stdout)

    # One gate at a time (2026-10-12): a second gate waits for the lock and says so; a holder that stopped
    # heartbeating is taken over; only the holder frees the lock.
    def test_a_second_gate_waits_for_the_lock_then_proceeds_and_frees_it(self):
        lock = self.path / "gate.lock"
        self.env.update(HAF_GATE_LOCK=lock.as_posix(), HAF_GATE_LOCK_POLL="0.2", HAF_GATE_LOCK_STALE="600", HAF_GATE_LOCK_BEAT="0.5")
        lock.mkdir(); write(lock / "holder", "other-gate pid 1"); write(lock / "beat", str(int(time.time())))
        proc = subprocess.Popen(self.script(self.helpers()+'''
gate_lock
echo acquired
gate_unlock
[ -d "$LOCK" ] && echo still-locked || echo released
'''), env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        waiting = "waiting: another gate holds the lock (other-gate pid 1)"
        progress = ""
        try:
            deadline = time.monotonic() + 60  # guard a stuck child; release follows its waiting signal
            while time.monotonic() < deadline:
                progress = (self.path / "progress.md").read_text()
                if waiting in progress or proc.poll() is not None:
                    break
                time.sleep(.05)
        finally:
            shutil.rmtree(lock, ignore_errors=True)
            try:
                out, err = proc.communicate(timeout=30)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.communicate()
                self.fail("gate did not finish after the other gate released its lock")
        self.assertIn(waiting, progress)
        self.assertEqual(proc.returncode, 0, err)
        self.assertIn("another gate is running (other-gate pid 1); waiting", out)
        self.assertIn("acquired", out)
        self.assertIn("released", out)

    def test_a_stale_lock_is_taken_over_and_a_waiter_never_frees_anothers(self):
        lock = self.path / "gate.lock"
        self.env.update(HAF_GATE_LOCK=lock.as_posix(), HAF_GATE_LOCK_POLL="0.2", HAF_GATE_LOCK_STALE="2", HAF_GATE_LOCK_BEAT="0.5")
        lock.mkdir(); write(lock / "holder", "dead-gate pid 2"); write(lock / "beat", "0")
        result = self.run_shell(self.helpers()+'gate_lock\necho acquired\ngate_unlock\n', timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("took over a stale gate lock (dead-gate pid 2", result.stdout)
        self.assertIn("acquired", result.stdout)
        self.assertFalse(lock.exists())
        # a live lock held by someone else: gate_unlock from a shell that is not the holder leaves it alone
        lock.mkdir(); write(lock / "holder", "other-gate pid 3"); write(lock / "beat", str(int(time.time())))
        result = self.run_shell(self.helpers()+'gate_unlock\n[ -d "$LOCK" ] && echo kept || echo freed\n')
        self.assertIn("kept", result.stdout)

    # 2026-10-12: a waiter read an empty beat (the holder was rewriting it) as 0, took over a LIVE gate eight minutes
    # in, and two gates ran side by side. Only a readable, old beat is stale; the holder's beat is renamed into place.
    def test_an_unreadable_beat_is_not_a_dead_holder(self):
        lock = self.path / "gate.lock"
        self.env.update(HAF_GATE_LOCK=lock.as_posix(), HAF_GATE_LOCK_POLL="0.2", HAF_GATE_LOCK_STALE="1", HAF_GATE_LOCK_WAIT="2", HAF_GATE_LOCK_BEAT="0.5")
        for beat in (None, "", "not-a-number"):
            shutil.rmtree(lock, ignore_errors=True)
            lock.mkdir(); write(lock / "holder", "live-gate pid 4")
            if beat is not None:
                write(lock / "beat", beat)
            result = self.run_shell(self.helpers()+'gate_lock\necho acquired\ngate_unlock\n', timeout=30)
            self.assertNotIn("took over", result.stdout, beat)
            self.assertNotIn("acquired", result.stdout, beat)
            self.assertIn("another gate is running (live-gate pid 4); waiting", result.stdout, beat)
            self.assertEqual(result.returncode, 1, result.stderr)      # gave up after HAF_GATE_LOCK_WAIT, never freed it
            self.assertTrue(lock.exists(), beat)
        # the holder's own beat advances, written beside and renamed over (the beat.<pid> file is transient; a reader
        # only ever opens "beat", which is never empty)
        shutil.rmtree(lock, ignore_errors=True)
        result = self.run_shell(self.helpers()+'''
gate_lock
b0=$(cat "$LOCK/beat"); sleep 1.6; b1=$(cat "$LOCK/beat")
[ -n "$b1" ] && [ "$b1" -gt "$b0" ] && echo beating || echo "not beating ($b0 -> $b1)"
gate_unlock
''', timeout=30)
        self.assertIn("beating", result.stdout, result.stdout + result.stderr)

    # 2026-10-12: run_bg_wait removed the directory it had exported as every step's TMPDIR; the first tail step to
    # call mktemp (the goldens check) failed with "No such file or directory". The directory is emptied, kept.
    def test_tmpdir_survives_the_parallel_phase(self):
        result = self.run_shell(self.helpers()+'''
run_bg "a worker" true
run_bg_wait
d=$(mktemp -d) && [ -d "$d" ] && echo tmp-ok || echo tmp-gone
[ "$(ls -A "$BG_LOGS" | grep -v "^tmp\\." | wc -l)" = 0 ] && echo emptied || echo "left: $(ls -A "$BG_LOGS")"
''', timeout=30)
        self.assertIn("tmp-ok", result.stdout, result.stdout + result.stderr)
        self.assertIn("emptied", result.stdout, result.stdout)

    def test_an_abort_kills_a_workers_grandchild_and_frees_the_lock(self):
        # The worker's shell is one process; what it spawned through Cygwin's exec has no usable Windows parent, so
        # the abort must find it another way (its command line names the gate's temp directory). The grandchild here
        # is a uniquely named copy of sleep, started under the worker's TMPDIR.
        lock = self.path / "gate.lock"
        self.env.update(HAF_GATE_LOCK=lock.as_posix(), HAF_GATE_LOCK_POLL="0.2", HAF_GATE_LOCK_BEAT="0.5")
        tag = f"hafsleep_{os.getpid()}"
        sleep_exe = Path(BASH).parent.parent / "usr/bin/sleep.exe" if os.name == "nt" else Path(shutil.which("sleep"))
        result = self.run_shell(self.helpers()+f'''
gate_lock
trap gate_abort TERM
run_bg deep bash -c 'cp "$0" "$TMPDIR/{tag}.exe"; "$TMPDIR/{tag}.exe" 300 & wait' "{sleep_exe.as_posix()}"
sleep 3
kill -TERM $$
sleep 5
echo "still here"
''', timeout=60)
        alive = subprocess.run([BASH, "-c", f"sleep 1; ps -W | grep -c '{tag}'"], capture_output=True, text=True).stdout.strip()
        subprocess.run([BASH, "-c", f"taskkill /F /IM '{tag}.exe' > /dev/null 2>&1; true"])
        self.assertEqual(result.returncode, 130, result.stderr)
        self.assertNotIn("still here", result.stdout)
        self.assertEqual(alive, "0", "the worker's grandchild survived the abort")
        self.assertFalse(lock.exists())
        self.assertIn("DONE ABORTED (interrupted)", (self.path / "progress.md").read_text())

    def test_a_hung_step_is_killed_and_failed_within_the_step_limit(self):
        self.env["HAF_STEP_TIMEOUT"] = "2"
        t0 = time.monotonic()
        result = self.run_shell(self.helpers()+'''
run_bg hung bash -c 'sleep 60; echo never'
run_bg good bash -c 'echo good-output'
run_bg_wait
run serial-hung bash -c 'sleep 60; echo never'
verdict; exit $?
''', timeout=90)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertLess(time.monotonic() - t0, 60)
        self.assertIn("[FAIL] hung", result.stdout)
        self.assertIn("[FAIL] serial-hung", result.stdout)
        self.assertIn("timed out: the step limit is 2 s", result.stdout)
        self.assertIn("[PASS] good", result.stdout)
        self.assertNotIn("never", result.stdout)

    def test_a_clean_gate_still_passes_with_the_verdict(self):
        result = self.run_shell(self.helpers()+'''
run_bg good bash -c 'echo good-output'
run_bg_wait
run also-good bash -c 'true'
verdict; exit $?
''')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("CHECK: PASS", result.stdout)
        self.assertIn("safe to push.", result.stdout)
        self.assertNotIn("SKIP", result.stdout)
        self.assertIn("DONE PASS", (self.path / "progress.md").read_text())

    def test_tamper_arguments_preserve_spaces_and_apostrophes_and_select_each_jobs_file(self):
        directory = self.path / "tamper inputs O'Brien"
        directory.mkdir()
        names = ["missing_intact", "exit_intact", "fire_intact", "gun_intact", "recoil_intact", "bind_intact", "role_intact", "log"]
        for group in ("missing", "exit", "fire", "gun", "recoil", "bind", "role"):
            write(directory / (group+"_jobs.txt"), group)
        for name in names:
            write(directory / (name+".txt"), name)
        fake = self.path / "deploy.exe"
        write(fake, '''#!/usr/bin/env bash
printf '%s\n' "$@" > "$3.called"
case "$3" in *intact.txt) echo 'PASS control'; exit 0;; *) echo 'FAIL changed evidence'; exit 1;; esac
''')
        result = self.run_shell('TMPD="$ROOT"\n'+self.tampers()+'''
chmod +x "$TMPD/deploy.exe"
DEPLOY_PAR=2 prerun_tampers "$ROOT/tamper inputs O'Brien"
''')
        self.assertEqual(result.returncode, 0, result.stderr)
        for name in names:
            with self.subTest(name=name):
                args = (directory / (name+".txt.called")).read_text().splitlines()
                group = name.split("_")[0] if name != "log" else "missing"
                self.assertEqual(args, ["--decisions", (directory / (group+"_jobs.txt")).as_posix(), (directory / (name+".txt")).as_posix()])
                self.assertEqual((self.path / "par" / (name+".txt.rc")).read_text().strip(), "1" if name == "log" else "0")

    def test_cached_status_requires_the_exact_expected_code_for_controls_and_mutations(self):
        source = (ROOT / "tools/deploy_drill.sh").read_text(encoding="utf-8")
        verdict_helper = source[source.index("read_tamper_status() {"):source.index("prerun_tampers() {")]
        controls = source[source.index("for control in missing exit; do"):source.index("for mode in log matrix")]
        negative = source[source.index("for mode in log matrix"):source.index("for mode in exit_zero")]
        negative = negative[negative.index('  _t="$WTMP/missing_dec/$mode.txt"'):negative.rindex("done")]
        par = self.path / "par"; par.mkdir()
        for name in ("missing_intact", "exit_intact"):
            write(par / (name+".txt"), "PASS control\n")
        write(par / "log.txt", "FAIL log line differs\n")
        for status in (None, "", "garbage", "0\n1", "2", "-1", "valid", "leading_space", "trailing_space", "leading_tab", "trailing_tab", "embedded_nul", "trailing_nul"):
            for control in (True, False):
                with self.subTest(status=status, control=control):
                    for name in ("missing_intact", "exit_intact", "log"):
                        path = par / (name+".txt.rc")
                        if status is None:
                            path.unlink(missing_ok=True)
                        else:
                            code = "1" if name == "log" else "0"
                            value = {"valid": code, "leading_space": " " + code, "trailing_space": code + " ",
                                     "leading_tab": "\t" + code, "trailing_tab": code + "\t", "embedded_nul": code + "\0extra", "trailing_nul": code + "\0"}.get(status, status)
                            write(path, value+"\n" if value else "")
                    text = 'TMPD="$ROOT"; WTMP="$ROOT"; PAR="$ROOT/par"; mode=log; reason="log line"\n'
                    result = self.run_shell(text+verdict_helper+(controls if control else negative))
                    self.assertEqual(result.returncode, 0 if status == "valid" else 1, result.stderr)

    def test_delete_only_hook_skips_but_updates_and_mixed_pushes_run_the_gate(self):
        tools = self.path / "tools"; tools.mkdir()
        write(tools / "check.sh", 'echo "gate invoked"\nexit 9\n')
        source = (ROOT / "tools/git-hooks/pre-push").read_text(encoding="utf-8")
        cmd = self.script('git() { printf "%s\\n" "$ROOT"; }\n'+source)
        zero = "0"*40; sha = "a"*40
        deleted = f"(delete) {zero} refs/heads/old {sha}\n"
        updated = f"refs/heads/new {sha} refs/heads/new {zero}\n"
        for refs, expected in ((deleted, 0), (updated, 9), (deleted+updated, 9)):
            with self.subTest(refs=refs):
                result = subprocess.run(cmd, input=refs, env=self.env, capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, expected, result.stderr)
                self.assertEqual("gate invoked" in result.stdout, expected == 9)

    def test_export_duration_ends_when_workers_finish_not_when_their_results_are_read(self):
        source = (ROOT / "tools/deploy_drill.sh").read_text(encoding="utf-8")
        launch = source[source.index("EPIDS=(); t_e0="):source.index("t3=$(date +%s)")]
        collect = source[source.index('erc=0; for pid in "${EPIDS[@]}"'):source.index(': > "$TMPD/export.txt"')]
        (self.path / "export").mkdir()
        self.env["TEST_CLOCK"] = (self.path / "clock").as_posix()
        for status in (0, 7, None):
            with self.subTest(status=status):
                write(self.path / "clock", "100\n")
                write(self.path / "blender", '#!/usr/bin/env bash\necho 102 > "$TEST_CLOCK"\nexit '+str(status or 0)+'\n')
                discard = 'rm "$TMPD/export/end_0.txt"\n' if status is None else ''
                result = self.run_shell('''
date() { cat "$ROOT/clock"; }
cygpath() { printf '%s\\n' "$2"; }
TMPD="$ROOT"; WTMP="$ROOT"; WROOT="$ROOT"; EXPORT_JOBS=1; BLENDER="$ROOT/blender"
chmod +x "$BLENDER"
HAF_ORACLE=live; . "''' + ROOT.as_posix() + '''/tools/blender_oracle.sh"; oracle_init deploy
'''+launch+'''
wait "${EPIDS[0]}"
echo 110 > "$ROOT/clock"
'''+discard+collect+'''
echo "export duration $((t_e1 - t_e0))"
echo "export status $erc"
''')
                self.assertEqual(result.returncode, 1 if status is None else 0, result.stderr)
                if status is None:
                    self.assertIn("no valid completion time", result.stdout)
                else:
                    self.assertIn("export duration 2", result.stdout)
                    self.assertIn("export status "+str(status), result.stdout)


class DeployRuntimePrerequisiteTests(unittest.TestCase):
    def test_missing_Mono_returns_skip_before_any_conversion_claim(self):
        with tempfile.TemporaryDirectory(prefix="haf-missing-mono-") as directory:
            env = dict(os.environ, UNITY=directory)
            result = subprocess.run([BASH, str(ROOT / "tools/deploy_drill.sh")], env=env,
                                    capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 3, result.stderr)
        self.assertIn("SKIP", result.stdout)
        self.assertIn("Mono not found", result.stdout)
        self.assertIn("NOT drilled", result.stdout)
        self.assertNotIn("PASS", result.stdout)


if __name__ == "__main__":
    unittest.main()
