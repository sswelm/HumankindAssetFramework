"""HAF_TEST_CULTURE from outside the suite: what a run of `dotnet test` does with a listed name, a made-up one, and none.

Tests/TestCultureTests.cs holds TestBootstrap.Resolve; it cannot hold what the module initializer DOES with a refusal,
because a test inside the assembly only runs when the initializer let it. The second review of PR #313 planted an
initializer that swallows the refusal and falls back to a culture of its choice: 2,923 tests green. Here the refusal is
seen as the gate and CI would see it - the run fails, and says why.

Needs the test assembly BUILT (Release): the gate and CI run this after the unit tests, which build it.
"""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "Tests/HumankindAssetFramework.Tests.csproj"
BUILT = ROOT / "Tests/bin/Release/HumankindAssetFramework.Tests.dll"
CULTURE_TEST = ("HumankindAssetFramework.Tests.TestCultureTests."
                "WithoutTheOverrideTheForcedCultureIsTheDefault")


def run_culture_test(culture, output_dir=None):
    env = dict(os.environ)
    for name in [n for n in env if n.upper() == "HAF_TEST_CULTURE"]:
        del env[name]
    if culture is not None:
        env["HAF_TEST_CULTURE"] = culture
    args = ["dotnet", "test", str(PROJECT), "-c", "Release", "--nologo", "--no-build",
            "--filter", f"FullyQualifiedName={CULTURE_TEST}"]
    if output_dir is not None:
        args += ["--results-directory", output_dir, "--logger", "trx;LogFileName=culture.trx"]
    return subprocess.run(args, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=600)


class CultureOverrideTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(BUILT.exists(), "build the tests first (dotnet test -c Release): %s is not there" % BUILT)

    def test_the_default_and_each_listed_override_run_under_the_requested_culture(self):
        for culture in (None, "tr-TR", "en-US"):
            with self.subTest(culture=culture), tempfile.TemporaryDirectory() as output_dir:
                r = run_culture_test(culture, output_dir)
                out = r.stdout + r.stderr
                self.assertEqual(0, r.returncode, out)
                report = Path(output_dir) / "culture.trx"
                self.assertTrue(report.is_file(), out)
                # VSTest localizes its console summary. The report always gives the same result counters.
                counters = ET.parse(report).find("./{*}ResultSummary/{*}Counters")
                self.assertIsNotNone(counters, out)
                self.assertEqual(("1", "1", "1"), tuple(counters.get(k) for k in ("total", "executed", "passed")), out)

    def test_a_made_up_name_fails_the_run_and_the_output_says_why(self):
        # en-UK and xx-YY are well-formed, so Windows answers them with a made-up dot-decimal culture; nl_NL is the
        # POSIX spelling of a real one
        for culture in ("en-UK", "xx-YY", "nl_NL"):
            r = run_culture_test(culture)
            out = r.stdout + r.stderr
            self.assertNotEqual(0, r.returncode, culture + "\n" + out)
            self.assertIn("HAF_TEST_CULTURE='%s' is not a culture this runtime lists" % culture, out)


if __name__ == "__main__":
    unittest.main()
