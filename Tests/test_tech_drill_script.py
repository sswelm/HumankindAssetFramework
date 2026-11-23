"""tools/tech_requires_all_game_drill.sh before it builds or loads anything: what it says about the game's folder.

The push gate reads this script's exit status: 0 PASS, 3 SKIP (the game is not installed: its claim was NOT tested),
anything else FAIL. Until 2026-11-23 no test read those statuses, and the plants of the second review of PR #312 passed
every check: a missing game exiting 0 (the gate would print [PASS] on a machine that drilled nothing), a wrong folder
exiting 3. The folder checks run before the first build, so these tests need neither the game nor dotnet.
"""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
if os.name == "nt":
    BASH = str(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe")
else:
    BASH = shutil.which("bash")
DRILL = ROOT / "tools/tech_requires_all_game_drill.sh"
NAMED = ("Assembly-CSharp.dll", "Amplitude.Mercury.Firstpass.dll", "Amplitude.Mercury.Data.dll")


class TechDrillScriptTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="haf-techall-test-")
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def drill(self, managed, as_argument=False):
        env = dict(os.environ)
        for name in [n for n in env if n.upper() in ("HK_MANAGED", "HAF_DRILL_STAGE", "HAF_DRILL_MONO")]:
            del env[name]
        args = [BASH, DRILL.as_posix()]
        if as_argument:
            args.append(managed.as_posix())
        else:
            env["HK_MANAGED"] = managed.as_posix()
        return subprocess.run(args, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)

    def test_a_missing_folder_is_a_skip_not_a_pass(self):
        for as_argument in (False, True):
            r = self.drill(self.root / "no-such-folder", as_argument)
            self.assertEqual(3, r.returncode, r.stdout + r.stderr)
            self.assertTrue(r.stdout.startswith("SKIP"), r.stdout)
            self.assertIn("NOT drilled", r.stdout)

    def test_an_empty_folder_is_a_fail_that_names_the_folder_and_what_it_lacks(self):
        empty = self.root / "empty"
        empty.mkdir()
        for as_argument in (False, True):
            r = self.drill(empty, as_argument)
            self.assertEqual(1, r.returncode, r.stdout + r.stderr)
            self.assertTrue(r.stdout.startswith("FAIL"), r.stdout)
            self.assertIn("is not the game's Managed folder", r.stdout)
            self.assertIn(empty.as_posix(), r.stdout)
            self.assertIn(NAMED[0], r.stdout)
            self.assertNotIn("Exception", r.stdout + r.stderr)

    def test_each_of_the_three_assemblies_the_drill_loads_by_name_is_required(self):
        for missing in NAMED:
            folder = self.root / ("without-" + missing)
            folder.mkdir()
            for name in NAMED:
                if name != missing:
                    (folder / name).write_bytes(b"")
            r = self.drill(folder)
            self.assertEqual(1, r.returncode, missing + "\n" + r.stdout + r.stderr)
            self.assertIn("no " + missing + " in it", r.stdout)

    def test_the_named_assemblies_are_the_ones_the_drill_loads(self):
        source = (ROOT / "tools/tech-requires-all-drill/TechRequireAllGameDrill.cs").read_text(encoding="utf-8")
        for name in NAMED:
            self.assertIn('Path.Combine(Managed, "' + name + '")', source, name)

    def test_every_pass_of_the_drill_sets_its_own_stage(self):
        # An exported HAF_DRILL_STAGE=load made the first pass run that stage: PASS on 12 checks instead of 47.
        text = DRILL.read_text(encoding="utf-8")
        runs = [line.strip() for line in text.splitlines() if '"$EXE"' in line and not line.lstrip().startswith("#")]
        self.assertGreaterEqual(len(runs), 4, runs)   # with a list, without one, and the two Mono runs
        self.assertEqual([], [line for line in runs if "HAF_DRILL_STAGE=" not in line], "a run of the drill without a stage of its own")
        self.assertIn('HAF_DRILL_STAGE=full "$EXE" "$WMANAGED"; rc=$?', text)
        self.assertIn('HAF_DRILL_STAGE=nolist "$EXE" "$WMANAGED"; rc=$?', text)


if __name__ == "__main__":
    unittest.main()
