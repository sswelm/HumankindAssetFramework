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
PURE_NAMES = {"_fitted_cycle_count", "_oar_recovery_metrics", "_merge2_scale", "_tagged_reduce_pct"}
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

    def test_khalandion_absolute_calibration(self):
        """Pin the drill-validated ABSOLUTES, not just the scaling law: the validation model's oar-bank
        diagonal 10.8132 must map to merge eps 0.085 and min island span 0.15 (exactly 64 recovered oars).
        The scaling tests alone let the 104-oar bug through — eps 0.0051x also scales linearly."""
        metrics = NAMESPACE["_oar_recovery_metrics"]
        diagonal, eps, span, _ = metrics((0.0, 0.0, 0.0), (10.8132, 0.0, 0.0))
        self.assertAlmostEqual(10.8132, diagonal)
        self.assertAlmostEqual(0.085, eps, places=3)
        self.assertAlmostEqual(0.15, span, places=3)

    def test_beam_centre_tracks_translation_instead_of_world_zero(self):
        metrics = NAMESPACE["_oar_recovery_metrics"]
        base = metrics((-8.0, -3.0, -1.0), (8.0, 3.0, 1.0))
        moved = metrics((92.0, 47.0, 9.0), (108.0, 53.0, 11.0))
        self.assertAlmostEqual(0.0, base[3])
        self.assertAlmostEqual(50.0, moved[3])
        self.assertAlmostEqual(base[0], moved[0])
        self.assertAlmostEqual(base[1], moved[1])
        self.assertAlmostEqual(base[2], moved[2])


    # ---- second-model scale, per axis (2026-09-21) ----
    def test_merge2_scale_reads_three_axes(self):
        parse = NAMESPACE["_merge2_scale"]
        self.assertEqual((0.5, 2.0, 1.25, []), parse("0.5,2,1.25"))

    def test_merge2_scale_single_number_is_still_uniform(self):
        """The pre-change argument carried ONE number; a stale Lab talking to a new script must mean the same thing."""
        parse = NAMESPACE["_merge2_scale"]
        self.assertEqual((0.01, 0.01, 0.01, []), parse("0.01"))
        self.assertEqual((1.0, 1.0, 1.0, []), parse(""))

    def test_merge2_scale_refuses_collapse_and_mirror_per_component(self):
        """Zero flattens the model; a negative component mirrors it and reverses every winding. Only the bad
        component falls back - the other two keep what the author typed."""
        parse = NAMESPACE["_merge2_scale"]
        sx, sy, sz, bad = parse("0,-2,3")
        self.assertEqual((1.0, 1.0, 3.0), (sx, sy, sz))
        self.assertEqual(["0", "-2"], bad)
        sx, sy, sz, bad = parse("nan,inf,abc")
        self.assertEqual((1.0, 1.0, 1.0), (sx, sy, sz))
        self.assertEqual(3, len(bad))

    def test_gunreduce_tag_is_the_percent_alone_clamped_and_absent_at_zero(self):
        """gunreduce=<percent> (2026-09-25): the Gun parts already travel positionally, so the tag carries the percent
        alone. Absent -> 0.0 (the dial at 0 sends nothing); clamped to 0..95 like every other tier; a value that is
        not a finite number is refused (the script turns that into a VEHICLE ERROR, never a silent skip)."""
        pct = NAMESPACE["_tagged_reduce_pct"]
        self.assertEqual(0.0, pct(["rig", "a.glb", "shroudreduce=@x.txt|50"], "gunreduce"))
        self.assertEqual(35.5, pct(["rig", "gunreduce=35.5", "flagfold=0"], "gunreduce"))
        self.assertEqual(95.0, pct(["gunreduce=120"], "gunreduce"))
        self.assertEqual(0.0, pct(["gunreduce=-4"], "gunreduce"))
        self.assertEqual(50.0, pct(["gunreduce=50", "shroudreduce=@x.txt|20"], "gunreduce"))
        for bad in ("gunreduce=", "gunreduce=abc", "gunreduce=nan", "gunreduce=inf", "gunreduce=@guns.txt|50"):
            with self.assertRaises(ValueError, msg=bad):
                pct([bad], "gunreduce")

    def test_merge2_scale_rejects_a_wrong_component_count(self):
        parse = NAMESPACE["_merge2_scale"]
        with self.assertRaises(ValueError):
            parse("1,2")
        with self.assertRaises(ValueError):
            parse("1,2,3,4")


if __name__ == "__main__":
    unittest.main()
