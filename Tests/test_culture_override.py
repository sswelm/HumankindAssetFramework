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
import unittest

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "Tests/HumankindAssetFramework.Tests.csproj"
BUILT = ROOT / "Tests/bin/Release/HumankindAssetFramework.Tests.dll"


def run_culture_tests(culture):
    env = dict(os.environ)
    for name in [n for n in env if n.upper() == "HAF_TEST_CULTURE"]:
        del env[name]
    if culture is not None:
        env["HAF_TEST_CULTURE"] = culture
    return subprocess.run(["dotnet", "test", str(PROJECT), "-c", "Release", "--nologo", "--no-build",
                           "--filter", "FullyQualifiedName~TestCultureTests"],
                          env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=600)


class CultureOverrideTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(BUILT.exists(), "build the tests first (dotnet test -c Release): %s is not there" % BUILT)

    def test_without_the_variable_the_culture_tests_pass(self):
        r = run_culture_tests(None)
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        self.assertRegex(r.stdout, r"Passed!\s+- Failed:\s+0, Passed:\s+1[0-9],")

    def test_a_listed_name_runs_them_under_that_culture(self):
        # WithoutTheOverrideTheForcedCultureIsTheDefault passes only when the forced culture IS the asked one
        for culture in ("tr-TR", "en-US"):
            r = run_culture_tests(culture)
            self.assertEqual(0, r.returncode, culture + "\n" + r.stdout + r.stderr)
            self.assertRegex(r.stdout, r"Passed!\s+- Failed:\s+0, Passed:\s+1[0-9],")

    def test_a_made_up_name_fails_the_run_and_the_output_says_why(self):
        # en-UK and xx-YY are well-formed, so Windows answers them with a made-up dot-decimal culture; nl_NL is the
        # POSIX spelling of a real one
        for culture in ("en-UK", "xx-YY", "nl_NL"):
            r = run_culture_tests(culture)
            out = r.stdout + r.stderr
            self.assertNotEqual(0, r.returncode, culture + "\n" + out)
            self.assertNotIn("Passed!", out, culture)
            self.assertIn("HAF_TEST_CULTURE='%s' is not a culture this runtime lists" % culture, out)


if __name__ == "__main__":
    unittest.main()
