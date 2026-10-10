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
        write(self.path / "progress.md", "")

    def script(self, text):
        path = self.path / "test.sh"
        write(path, 'set -uo pipefail\nROOT="$1"\n' + text)
        return [BASH, path.as_posix(), self.path.as_posix()]

    def run_shell(self, text):
        return subprocess.run(self.script(text), env=self.env, capture_output=True, text=True, timeout=15)

    def helpers(self):
        source = (ROOT / "tools/check.sh").read_text(encoding="utf-8")
        return source[source.index("fail=0\n"):source.index('note "started (')]

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
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                if "PASS fast" in (self.path / "progress.md").read_text():
                    break
                time.sleep(.05)
            before_release = (self.path / "progress.md").read_text()
        finally:
            write(self.path / "clock", "110\n")
            write(self.path / "release", "")
            out, err = proc.communicate(timeout=10)
        self.assertEqual(proc.returncode, 0, err)
        self.assertIn("PASS fast (2 s)", before_release)
        self.assertNotIn("PASS slow", before_release)
        self.assertIn("[PASS] fast (2 s)", out)
        self.assertLess(out.index("=== slow ==="), out.index("=== fast ==="))
        self.assertIn("slow-output", out)
        self.assertIn("fast-output", out)

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
        controls = source[source.index("for control in missing exit; do"):source.index("for mode in log matrix")]
        negative = source[source.index("for mode in log matrix"):source.index("for mode in exit_zero")]
        negative = negative[negative.index('  cp "$PAR/'):negative.rindex("done")]
        par = self.path / "par"; par.mkdir()
        for name in ("missing_intact", "exit_intact"):
            write(par / (name+".txt"), "PASS control\n")
        write(par / "log.txt", "FAIL log line differs\n")
        for status in (None, "", "garbage", "0\n1", "2", "-1", "valid"):
            for control in (True, False):
                with self.subTest(status=status, control=control):
                    for name in ("missing_intact", "exit_intact", "log"):
                        path = par / (name+".txt.rc")
                        if status is None:
                            path.unlink(missing_ok=True)
                        else:
                            value = ("1" if name == "log" else "0") if status == "valid" else status
                            write(path, value+"\n" if value else "")
                    text = 'TMPD="$ROOT"; WTMP="$ROOT"; PAR="$ROOT/par"; mode=log; reason="log line"\n'
                    result = self.run_shell(text+(controls if control else negative))
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


if __name__ == "__main__":
    unittest.main()
