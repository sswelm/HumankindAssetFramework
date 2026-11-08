"""tools/temp_sweep.sh against a fake temp root: every pattern it owns goes when old, every decoy stays, young ones stay."""
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
SWEEP = (ROOT / "tools/temp_sweep.sh").as_posix()
OLD = time.time() - 3 * 86400     # older than the default 2 days
YOUNG = time.time() - 3600


def age(path, when):
    os.utime(path, (when, when))


class TempSweepTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="haf-sweep-test-")
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        # Even invalid/default-root tests must never inspect the host's real temp root.
        self.env = dict(os.environ, TEMP=self.root.as_posix(), TMP=self.root.as_posix(), TMPDIR=self.root.as_posix())

    def d(self, name, *files, when=OLD, sub=None):
        p = self.root / name
        p.mkdir()
        for f in files:
            (p / f).write_text("x", encoding="utf-8")
            age(p / f, when)
        if sub:
            for s in sub:
                (p / s).mkdir(); age(p / s, when)
        age(p, when)
        return name

    def f(self, name, when=OLD):
        p = self.root / name
        p.write_text("x", encoding="utf-8"); age(p, when)
        return name

    def sweep(self, *args):
        r = subprocess.run([BASH, SWEEP, "--root", self.root.as_posix(), *args], env=self.env,
                           capture_output=True, text=True, timeout=120)
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        return r.stdout

    def remaining(self):
        return sorted(p.name for p in self.root.iterdir())

    def populate(self):
        ours = [
            self.d("haf-gate.aaaaaaaaaa"),
            self.d("haf-gate.bbbbbbbbbb", "0.rc", "0.out", "0.cmd"),
            self.d("haf-gate.cccccccccc", sub=["tmp.dddddddddd"]),
            self.d("haf-gate-test-abcdefgh", "progress.md"),
            self.d("haf-oracle-sh-abcdefgh", "repo.txt"),
            self.d("haf-vehicle-generate.0123456789", "big.bin"),
            self.d("haf_checkedreplace_" + "a" * 32, "pack.json"),
            self.f("riganim_inproc_" + "b" * 32 + ".glb"),
        ]
        decoys = [
            self.d("tmp.aaaaaaaaaa"),
            self.d("tmp.bbbbbbbbbb", "Newtonsoft.Json.dll", "notes.txt"),
            self.d("vr.abcdef", "user-data.txt"),
            self.d("haf-pr264-work", "notes.txt"),
            self.d("haf-gate.short"),
            self.d("haf-oracle-sh-near-miss"),
            self.d("haf_checkedreplace_notes"),
            self.f("riganim_inproc_notes.glb"),
            self.d("0003c9ad-db78-4b51-bff6-aa4f9ea39202"),
            self.d("Microsoft.NET.Workload_1_2", "x.json"),
            self.d("haf-gate.lock", "holder"),
            self.f("000ab83e-c3d5-4bec-af6f-4c371f9d4664.tmp.node"),
            self.f("haf-gate.eeeeeeeeee"),  # files never qualify as generated directories
            self.d("riganim_inproc_" + "c" * 32 + ".glb"),  # directories never qualify as GLB files
        ]
        young = [
            self.d("haf-gate.ffffffffff", when=YOUNG),
            self.d("haf-oracle-sh-ijklmnop", "x", when=YOUNG),
            self.f("riganim_inproc_" + "d" * 32 + ".glb", when=YOUNG),
        ]
        return ours, decoys, young

    def test_old_leftovers_of_ours_go_and_everything_else_stays(self):
        ours, decoys, young = self.populate()
        out = self.sweep()
        self.assertEqual(sorted(decoys + young), self.remaining(), out)
        self.assertIn("TEMP SWEEP SWEPT %d of our leftover(s)" % len(ours), out)
        self.assertIn("%d of ours kept as younger" % len(young), out)

    def test_dry_run_deletes_nothing_and_names_what_would_go(self):
        ours, decoys, young = self.populate()
        before = self.remaining()
        out = self.sweep("--dry-run")
        self.assertEqual(before, self.remaining())
        for name in ours:
            self.assertIn("WOULD SWEEP", out); self.assertIn(name, out)
        for name in decoys + young:
            self.assertNotIn(" " + name + "\n", out)
        self.assertIn("TEMP SWEEP WOULD SWEEP %d of our leftover(s)" % len(ours), out)

    def test_age_is_a_dial(self):
        self.d("haf-gate.aaaaaaaaaa", when=YOUNG)
        out = self.sweep("--age-days", "0")
        self.assertEqual([], self.remaining(), out)

    def test_generic_names_and_common_markers_do_not_prove_ownership(self):
        names = [
            self.d("tmp.aaaaaaaaaa"),
            self.d("tmp.bbbbbbbbbb", "Newtonsoft.Json.dll", "user-data.txt"),
            self.d("tmp.cccccccccc", "jobs.txt"),
            self.d("tmp.dddddddddd", "0.rc", "0.out", "0.cmd"),
            self.d("tmp.eeeeeeeeee", sub=["fixtures"]),
            self.d("tmp.ffffffffff", "..important"),
            self.d("vr.abcdef", "user-data.txt"),
            self.d("haf-pr264-work", "user-data.txt"),
        ]
        out = self.sweep()
        self.assertEqual(sorted(names), self.remaining(), out)

    def test_recent_nested_activity_keeps_an_old_parent(self):
        self.d("haf-oracle-sh-active01", sub=["nested"])
        nested = self.root / "haf-oracle-sh-active01" / "nested"
        (nested / "active.log").write_text("still running", encoding="utf-8")
        age(nested / "active.log", YOUNG)
        age(nested, OLD)
        age(nested.parent, OLD)
        out = self.sweep()
        self.assertTrue(nested.exists(), out)

    def test_unreadable_age_is_not_permission_to_delete(self):
        self.d("haf-oracle-sh-noaccess", "data.bin")
        # Simulate a stat/traversal failure even when the runner has admin rights.
        wrapper = '''find() {
  case "$1" in */haf-oracle-sh-noaccess) return 1;; esac
  command find "$@"
}
export -f find
exec bash "$@"
'''
        r = subprocess.run([BASH, "-c", wrapper, "_", SWEEP, "--root", self.root.as_posix()], env=self.env,
                           capture_output=True, text=True, timeout=60)
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        self.assertTrue((self.root / "haf-oracle-sh-noaccess" / "data.bin").exists(), r.stdout)

    def test_missing_option_values_are_usage_errors(self):
        for args in (("--root",), ("--age-days",), ("--root", ""), ("--root", "--dry-run")):
            with self.subTest(args=args):
                r = subprocess.run([BASH, SWEEP, *args], env=self.env, capture_output=True, text=True, timeout=60)
                self.assertEqual(2, r.returncode, r.stdout + r.stderr)

    def test_bad_arguments_and_a_missing_root_are_refused(self):
        r = subprocess.run([BASH, SWEEP, "--root", (self.root / "nope").as_posix()], env=self.env, capture_output=True, text=True, timeout=60)
        self.assertEqual(2, r.returncode)
        r = subprocess.run([BASH, SWEEP, "--age-days", "two"], env=self.env, capture_output=True, text=True, timeout=60)
        self.assertEqual(2, r.returncode)
        r = subprocess.run([BASH, SWEEP, "--what"], env=self.env, capture_output=True, text=True, timeout=60)
        self.assertEqual(2, r.returncode)

    def gate_helpers(self):
        source = (ROOT / "tools/check.sh").read_text(encoding="utf-8")
        return source[source.index("fail=0\n"):source.index("# ==== the gate itself")]

    def gate_shell(self, body):
        env = dict(self.env, HAF_GATE="push", HAF_GATE_LOCK=(self.root / "lock").as_posix(),
                   HAF_PROGRESS=(self.root / "progress.md").as_posix())
        script = self.root / "gate-test.sh"
        script.write_text('set -uo pipefail\nROOT="$1"\n' + self.gate_helpers() + body, encoding="utf-8", newline="\n")
        return subprocess.run([BASH, script.as_posix(), self.root.as_posix()], env=env,
                              capture_output=True, text=True, timeout=60)

    def test_gate_namespaces_and_removes_its_directory_at_exit(self):
        r = self.gate_shell('''
trap gate_unlock EXIT
printf '%s' "$BG_LOGS" > "$ROOT/allocated"
run_bg worker true
run_bg_wait
[ -d "$TMPDIR" ] || exit 1
''')
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        allocated = (self.root / "allocated").read_text()
        # Translate the MSYS path using cygpath for the Python host.
        r = subprocess.run([BASH, "-c", 'cygpath -am "$1"', "_", allocated], env=self.env,
                           capture_output=True, text=True, timeout=60) if os.name == "nt" else None
        allocated = Path(r.stdout.strip() if r else allocated)
        self.assertRegex(allocated.name, r"^haf-gate\.[A-Za-z0-9]{10}$")
        self.assertEqual(self.root.resolve(), allocated.parent.resolve())
        self.assertFalse(allocated.exists())

    def test_housekeeping_timeout_does_not_fail_the_gate(self):
        source = (ROOT / "tools/check.sh").read_text(encoding="utf-8")
        housekeeping = source[source.index("# 0)"):source.index("# 1) plugin compiles")]
        r = self.gate_shell('''
trap gate_unlock EXIT
timeout() { return 124; }
''' + housekeeping + "\nverdict\n")
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        self.assertIn("TEMP SWEEP unavailable", r.stdout)
        self.assertIn("CHECK: PASS", r.stdout)


if __name__ == "__main__":
    unittest.main()
