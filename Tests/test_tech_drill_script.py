"""Verify the TechRequireAll drill's prerequisite and completion checks without installing the game or building the plugin."""
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

    def stub_native_drill(self, full_record, nolist_record):
        root = self.root / "fixture"
        tools = root / "tools"
        managed = root / "managed"
        bin_dir = root / "bin"
        exe_dir = tools / "tech-requires-all-drill/bin/Release"
        for directory in (managed, bin_dir, exe_dir):
            directory.mkdir(parents=True, exist_ok=True)
        for name in NAMED:
            (managed / name).touch()
        script = tools / DRILL.name
        shutil.copyfile(DRILL, script)
        dotnet = bin_dir / "dotnet"
        dotnet.write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8", newline="\n")
        dotnet.chmod(0o755)
        exe = exe_dir / "HumankindAssetFramework.Tests.exe"
        exe.write_text("""#!/usr/bin/env bash
printf 'stage %s\\n' "$HAF_DRILL_STAGE"
case "$HAF_DRILL_STAGE" in
    full) printf '%s\\r\\n' "$TECH_TEST_FULL_RECORD";;
    nolist) printf '%s\\r\\n' "$TECH_TEST_NOLIST_RECORD";;
    *) exit 1;;
esac
""", encoding="utf-8", newline="\n")
        exe.chmod(0o755)
        env = dict(os.environ)
        env.pop("HAF_DRILL_MONO", None)
        env.update(HAF_DRILL_STAGE="load", TECH_TEST_FULL_RECORD=full_record, TECH_TEST_NOLIST_RECORD=nolist_record)
        launcher = 'dir=$1; shift; if command -v cygpath >/dev/null; then dir=$(cygpath -u "$dir"); fi; PATH="$dir:$PATH" bash "$@"'
        return subprocess.run([BASH, "-c", launcher, "test-drill", bin_dir.as_posix(), script.as_posix(), managed.as_posix()],
                              env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)

    def test_zero_exit_requires_a_completion_record_in_both_native_stages(self):
        r = self.stub_native_drill("DRILL PASS", "DRILL PASS")
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        self.assertIn("stage full", r.stdout)
        self.assertIn("stage nolist", r.stdout)
        for stage, full, nolist in (("full", "LOADED", "DRILL PASS"), ("nolist", "DRILL PASS", "LOADED")):
            with self.subTest(stage=stage):
                r = self.stub_native_drill(full, nolist)
                self.assertEqual(1, r.returncode, r.stdout + r.stderr)
                self.assertIn(f"stage {stage}", r.stdout)
                self.assertIn("without DRILL PASS", r.stdout)


if __name__ == "__main__":
    unittest.main()
