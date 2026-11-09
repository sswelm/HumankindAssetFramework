"""Run the actual timing wrapper with fake compiler/runtime processes; no Unity or Blender required."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
BASH = str(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe") if os.name == "nt" else shutil.which("bash")


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")
    path.chmod(0o755)


class DeployTime(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="haf-deploy-time-test-")
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "with spaces"
        self.repo = self.path / "repo"
        self.script = self.repo / "tools/deploy_time.sh"
        write(self.script, (ROOT / "tools/deploy_time.sh").read_text(encoding="utf-8"))
        write(self.repo / "tools/deploy_drill.sh", '''UNITY="${UNITY:?}"
OUT=$(dotnet "$CSC" -out:"$WTMP/deploy.exe"); rc=$?
OUT=$(dotnet "$CSC" -out:"$WTMP/mono-host.exe"); rc=$?
''')
        unity = self.path / "unity"
        for rel in ("DotNetSdkRoslyn/csc.dll", "MonoBleedingEdge/lib/mono/4.7.1-api/mscorlib.dll", "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll"):
            write(unity / rel, "fixture")
        for rel in ("References/Newtonsoft.Json.dll", "editor/Plugins/x86_64/hafkernels.dll"):
            write(self.repo / rel, "fixture")
        self.project = self.path / "project"
        self.source = self.path / "source.glb"
        self.source.write_bytes(b"fixture source")
        write(self.project / "Assets/FactorySource/Model/deploy_converted.args.txt", self.source.as_posix() + "|111|222|0|24||||\n")
        self.oracle = self.path / "oracle.glb"
        self.oracle.write_bytes(b"test-conversion-0123456789")
        self.build = self.path / "build"
        self.build.mkdir()
        self.host = self.path / "host.sh"
        write(self.host, '''#!/usr/bin/env bash
IFS='|' read -r model src rest < "$4"
printf 'test-conversion-0123456789' > "$6/$model.glb"
printf '%s\n' "$6" >> "$HOST_CALLS"
echo "TIMING $model role clips 0.1s"
if [ "${HOST_FAIL:-0}" != 0 ]; then echo "FAIL $model: runtime failure"; exit "$HOST_FAIL"; fi
if ! cmp -s "$6/$model.glb" "$5/$model.glb"; then
  echo "FAIL $model: InvalidDataException: written GLB bytes differ from Blender's"; exit 1
fi
echo "PASS $model: bytes equal to Blender's"
''')
        mockbin = self.path / "bin"
        write(mockbin / "dotnet", '''#!/usr/bin/env bash
for arg in "$@"; do case "$arg" in -out:*) out="${arg#-out:}";; esac; done
cp "$FAKE_HOST" "$out" && chmod +x "$out"
''')
        self.calls = self.path / "calls.txt"
        # Build PATH inside Bash: Windows' semicolon-separated PATH must not be mixed with POSIX entries in Python.
        self.env = dict(os.environ, UNITY=unity.as_posix(), HAF_PROJECT=self.project.as_posix(),
                        HAF_BUILD=self.build.as_posix(), FAKE_HOST=self.host.as_posix(), HOST_CALLS=self.calls.as_posix(), HOST_FAIL="0")
        self.env.pop("HAF_UNITY_PROJECT", None)
        self.command = [BASH, "-c", 'mock=$(cygpath -u "$1" 2>/dev/null || printf "%s" "$1"); export PATH="$mock:$PATH"; shift; exec bash "$@"', "_", mockbin.as_posix(), self.script.as_posix()]

    def run_tool(self, oracle=True, **env):
        args = self.command + ["Model"] + ([self.oracle.as_posix()] if oracle else [])
        return subprocess.run(args, env=dict(self.env, **env), capture_output=True, text=True, timeout=60)

    def test_success_reports_timing_and_wall_time(self):
        result = self.run_tool()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("TIMING Model", result.stdout)
        self.assertIn("WALL ", result.stdout)

    def test_conversion_failure_survives_output_filtering_and_wall_time(self):
        result = self.run_tool(HOST_FAIL="42")
        self.assertEqual(result.returncode, 42, result.stdout + result.stderr)
        self.assertIn("FAIL Model: runtime failure", result.stdout)

    def test_missing_oracle_is_an_error(self):
        self.oracle.unlink()
        result = self.run_tool()
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(self.calls.exists(), "do not run against a missing oracle")

    def test_wrong_oracle_returns_failure(self):
        self.oracle.write_bytes(b"different Blender output")
        result = self.run_tool()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("bytes differ from Blender's", result.stdout)

    def test_warmup_allows_only_the_expected_empty_oracle_mismatch(self):
        result = self.run_tool(oracle=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(self.calls.read_text().splitlines()), 2)
        for code in ("1", "42"):
            self.calls.unlink()
            result = self.run_tool(oracle=False, HOST_FAIL=code)
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(len(self.calls.read_text().splitlines()), 1, "a failed warmup must not supply the oracle")

    def test_runs_preserve_existing_files_and_use_separate_directories(self):
        sentinel = self.build / "deploy_time/oracle/keep.glb"
        write(sentinel, "unrelated run")
        for _ in range(2):
            result = self.run_tool()
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(sentinel.exists(), "another run's oracle must never be deleted")
        calls = self.calls.read_text().splitlines()
        self.assertEqual(len(set(calls)), 2, "runs must not share outputs, jobs or compiler artifacts")

    def test_standard_project_override_is_honored(self):
        result = self.run_tool(HAF_UNITY_PROJECT=self.project.as_posix(), HAF_PROJECT=(self.path / "missing").as_posix())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_model_must_be_a_single_folder_and_job_key(self):
        for model in ("../Model", "Model/child", "Model\\child", "Model|extra", "Model\nextra"):
            with self.subTest(model=model):
                result = subprocess.run(self.command + [model, self.oracle.as_posix()], env=self.env,
                                        capture_output=True, text=True, timeout=60)
                self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertFalse(self.calls.exists())


if __name__ == "__main__":
    unittest.main()
