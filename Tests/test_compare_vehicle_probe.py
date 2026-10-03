"""Regression coverage for the Blender parity gate's public row protocol, without Blender."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).parents[1] / "tools" / "vehicle-probe-drill" / "compare_probe.py"


def rows(name="Hull", under_bone=False):
    suffix = "|under-bone" if under_bone else ""
    return [f"PART|{name}|3|0,0,0|1,1,1|1||0",
            f"MATRIX|{name}|1 0 0 0 0 1 0 0 0 0 1 0 0 0 0 1{suffix}",
            f"VERTEX|{name}|0 0 0|0 0 1{suffix}"]


class CompareVehicleProbeTests(unittest.TestCase):
    def compare(self, cs, bl):
        with tempfile.TemporaryDirectory() as tmp:
            paths = [Path(tmp) / "cs.txt", Path(tmp) / "bl.txt"]
            for path, content in zip(paths, (cs, bl)):
                path.write_text("".join(f"ROW\tmodel\t{row}\n" for row in content), encoding="utf-8")
            result = subprocess.run([sys.executable, str(SCRIPT), *map(str, paths)], capture_output=True, text=True)
        self.assertNotIn("Traceback", result.stderr)
        return result

    def test_pipe_names_preserve_the_numeric_tail(self):
        result = self.compare(rows("Hull|Pipe|"), rows("Hull|Pipe|"))
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("1 matrices and 1 vertex positions", result.stdout)

    def test_empty_probe_output_cannot_pass(self):
        for cs, bl in (([], rows()), (rows(), []), ([], [])):
            self.assertEqual(1, self.compare(cs, bl).returncode)

    def test_missing_diagnostics_fail_on_either_side_even_under_bones(self):
        for under in (False, True):
            complete = rows(under_bone=under)
            for indices in ((1,), (2,), (1, 2)):
                incomplete = [r for i, r in enumerate(complete) if i not in indices]
                for cs, bl in ((incomplete, complete), (complete, incomplete)):
                    with self.subTest(under=under, missing=indices, cs=cs):
                        result = self.compare(cs, bl)
                        self.assertEqual(1, result.returncode)
                        self.assertIn("rows incomplete", result.stdout)

    def test_partial_diagnostics_cannot_hide_one_part(self):
        complete = rows() + rows("Turret")
        result = self.compare(complete[:-1], complete)
        self.assertEqual(1, result.returncode)
        self.assertIn("Turret", result.stdout)

    def test_malformed_diagnostic_shapes_fail_clearly(self):
        for invalid in ("MATRIX|Hull|1 2", "VERTEX|Hull|0 0|0 0 1"):
            result = self.compare([rows()[0], invalid], rows())
            self.assertEqual(1, result.returncode)
            self.assertIn("FAIL input", result.stdout)

    def test_positions_are_held_and_normal_differences_counted(self):
        for vertex, status in (("VERTEX|Hull|1 0 0|0 0 1", 1), ("VERTEX|Hull|0 0 0|0 1 0", 0)):
            result = self.compare(rows()[:2] + [vertex], rows())
            self.assertEqual(status, result.returncode, result.stdout)

    def test_under_bone_values_are_exempt_with_complete_rows(self):
        cs = rows(under_bone=True)
        cs[2] = "VERTEX|Hull|1 2 3|1 0 0|under-bone"
        result = self.compare(cs, rows())
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("1 under bones not held", result.stdout)


if __name__ == "__main__":
    unittest.main()
