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
        r = subprocess.run([BASH, SWEEP, "--root", self.root.as_posix(), *args], capture_output=True, text=True, timeout=120)
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        return r.stdout

    def remaining(self):
        return sorted(p.name for p in self.root.iterdir())

    def populate(self):
        ours = [
            self.d("tmp.aaaaaaaaaa"),                                      # the gate's emptied directory
            self.d("tmp.bbbbbbbbbb", "0.rc", "0.out", "0.cmd"),            # an aborted gate's step records
            self.d("tmp.cccccccccc", "Newtonsoft.Json.dll", "jobs.txt"),   # a killed drill
            self.d("tmp.dddddddddd", "blender_raw_0.txt"),
            self.d("tmp.eeeeeeeeee", sub=["tmp.ffffffffff"]),             # a gate directory holding a step's empty directory
            self.d("tmp.gggggggggg", sub=["0", "1"]),                      # a gate's step log directories
            self.d("vr.abcdef", "anything.txt"),                           # the vehicle rig drill's TMPD
            self.d("haf-gate-test-xyz", "progress.md"),
            self.d("haf-oracle-sh-k3", "repo.txt"),
            self.d("haf-vehicle-generate.0123456789", "big.bin"),
            self.d("haf_checkedreplace_0011", "pack.json"),
            self.f("riganim_inproc_0123abcd.glb"),
        ]
        decoys = [
            self.d("tmp.hhhhhhhhhh", "notes.txt"),                         # a tmp.* that is another program's
            self.d("tmp.iiiiiiiiii", "Newtonsoft.Json.dll", sub=["tmp.jjjjjjjjjj"]),   # ours by marker - goes; listed below
            self.d("tmp.kkkkkkkkkk", sub=["tmp.llllllllll"]),             # nested tmp.* holding a foreign file
            self.d("0003c9ad-db78-4b51-bff6-aa4f9ea39202"),                # a GUID directory: empty, old, NOT ours
            self.d("Microsoft.NET.Workload_1_2", "x.json"),
            self.d("haf-gate.lock", "holder"),                             # the gate's lock, whatever its age
            self.f("000ab83e-c3d5-4bec-af6f-4c371f9d4664.tmp.node"),
            self.f("riganim_inproc_notes.txt"),                            # not the .glb
            self.f("tmp.mmmmmmmmmm"),                                      # a FILE named like mktemp's directory
            self.d("tmp.short"),                                           # not mktemp's 10 characters
            self.d("vr.toolong1", "x"),
        ]
        (self.root / "tmp.kkkkkkkkkk" / "tmp.llllllllll" / "theirs.dat").write_text("x", encoding="utf-8")
        age(self.root / "tmp.kkkkkkkkkk" / "tmp.llllllllll", OLD); age(self.root / "tmp.kkkkkkkkkk", OLD)
        young = [self.d("tmp.nnnnnnnnnn", when=YOUNG), self.d("vr.young1", "x", when=YOUNG), self.d("haf-oracle-sh-young", "x", when=YOUNG)]
        return ours + ["tmp.iiiiiiiiii"], [x for x in decoys if x != "tmp.iiiiiiiiii"], young

    def test_old_leftovers_of_ours_go_and_everything_else_stays(self):
        ours, decoys, young = self.populate()
        out = self.sweep()
        self.assertEqual(sorted(decoys + young), self.remaining(), out)
        self.assertIn("TEMP SWEEP SWEPT %d of our leftover(s)" % len(ours), out)
        self.assertIn("%d of ours kept as younger" % len(young), out)
        self.assertIn("3 tmp.* directories not ours left alone", out)   # tmp.hhhhhhhhhh (a foreign file), tmp.kkkkkkkkkk (a foreign file nested), tmp.mmmmmmmmmm (a file, not a directory)

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
        self.d("tmp.aaaaaaaaaa", when=YOUNG)
        out = self.sweep("--age-days", "0")
        self.assertEqual([], self.remaining(), out)

    def test_bad_arguments_and_a_missing_root_are_refused(self):
        r = subprocess.run([BASH, SWEEP, "--root", (self.root / "nope").as_posix()], capture_output=True, text=True, timeout=60)
        self.assertEqual(2, r.returncode)
        r = subprocess.run([BASH, SWEEP, "--age-days", "two"], capture_output=True, text=True, timeout=60)
        self.assertEqual(2, r.returncode)
        r = subprocess.run([BASH, SWEEP, "--what"], capture_output=True, text=True, timeout=60)
        self.assertEqual(2, r.returncode)

    def test_the_gate_removes_its_own_directory_and_sweeps_at_the_start(self):
        source = (ROOT / "tools/check.sh").read_text(encoding="utf-8")
        self.assertIn('rm -rf "$BG_LOGS"', source[source.index("gate_unlock()"):source.index("verdict()")])
        self.assertIn("temp_sweep.sh", source[source.index("# ==== the gate itself"):])
        self.assertIn('HOST_TMP=', source[:source.index("BG_LOGS=$(mktemp -d)")])


if __name__ == "__main__":
    unittest.main()
