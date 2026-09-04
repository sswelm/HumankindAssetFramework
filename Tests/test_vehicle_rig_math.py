"""Pure regression tests for Vehicle Lab math; runs without Blender.

Usage: python Tests/test_vehicle_rig_math.py
"""
import ast
import math
from pathlib import Path
import unittest


SCRIPT = Path(__file__).parents[1] / "editor" / "Tools~" / "vehicle_rig.py"
SOURCE = SCRIPT.read_text(encoding="utf-8")
TREE = ast.parse(SOURCE, filename=str(SCRIPT))
PURE_NAMES = {"_fitted_cycle_count", "_oar_recovery_metrics"}
PURE_DEFS = [node for node in TREE.body if isinstance(node, ast.FunctionDef) and node.name in PURE_NAMES]
if {node.name for node in PURE_DEFS} != PURE_NAMES:
    raise RuntimeError("Vehicle Lab pure helper contract changed; update this test deliberately")
NAMESPACE = {"math": math}
exec(compile(ast.Module(body=PURE_DEFS, type_ignores=[]), str(SCRIPT), "exec"), NAMESPACE)


class VehicleRigMathTests(unittest.TestCase):
    def test_default_rowing_repeats_through_long_wave_clip(self):
        fitted = NAMESPACE["_fitted_cycle_count"]
        self.assertEqual(5, fitted(120, 24))
        self.assertEqual(2, fitted(24, 15))
        self.assertEqual(1, fitted(24, 90))

    def test_oar_tolerances_follow_uniform_source_scale(self):
        metrics = NAMESPACE["_oar_recovery_metrics"]
        base = metrics((-8.0, -3.0, -1.0), (8.0, 3.0, 1.0))
        scaled = metrics((-800.0, -300.0, -100.0), (800.0, 300.0, 100.0))
        self.assertAlmostEqual(base[0] * 100.0, scaled[0])
        self.assertAlmostEqual(base[1] * 100.0, scaled[1])
        self.assertAlmostEqual(base[2] * 100.0, scaled[2])

    def test_beam_centre_tracks_translation_instead_of_world_zero(self):
        metrics = NAMESPACE["_oar_recovery_metrics"]
        base = metrics((-8.0, -3.0, -1.0), (8.0, 3.0, 1.0))
        moved = metrics((92.0, 47.0, 9.0), (108.0, 53.0, 11.0))
        self.assertAlmostEqual(0.0, base[3])
        self.assertAlmostEqual(50.0, moved[3])
        self.assertAlmostEqual(base[0], moved[0])
        self.assertAlmostEqual(base[1], moved[1])
        self.assertAlmostEqual(base[2], moved[2])


if __name__ == "__main__":
    unittest.main()
