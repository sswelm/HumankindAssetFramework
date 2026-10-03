"""The probe jobs' argument text must be the Lab's own (tools/vehicle-probe-drill/probe_jobs.py formats a recipe's second
model, placements and orientation as VehicleLabWindow hands them to vehicle_rig.py; the in-process probe parses the same
text), so a recipe is drilled with the characters the Lab would send - a fifth decimal rounded differently would be a
different input on one side."""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools", "vehicle-probe-drill"))
import probe_jobs as J


class ProbeJobsTests(unittest.TestCase):
    def test_fmt_is_the_labs_tostring_with_hashes(self):
        # C#'s ToString("0.#####", invariant): rounded half away from zero, trailing zeros dropped, never "-0"
        self.assertEqual("0.5", J.fmt(0.5, 5))
        self.assertEqual("6", J.fmt(6.0, 2))
        self.assertEqual("-45", J.fmt(-45.0, 2))
        self.assertEqual("0.00001", J.fmt(0.00001, 5))
        self.assertEqual("0", J.fmt(0.000001, 5))
        self.assertEqual("0", J.fmt(-0.000001, 5))
        self.assertEqual("1.23457", J.fmt(1.234567, 5))
        self.assertEqual("-2.5", J.fmt(-2.5, 4))
        # exact binary ties round AWAY from zero, as the Lab's float.ToString does (measured on Mono and .NET Framework); printf would give 1.0312 and 0.12
        self.assertEqual("1.0313", J.fmt(1.03125, 4))
        self.assertEqual("-1.0313", J.fmt(-1.03125, 4))
        self.assertEqual("0.13", J.fmt(0.125, 2))
        self.assertEqual("0.0001", J.fmt(0.00005, 4))
        self.assertEqual("0", J.fmt(0.00005, 2))

    def test_midpoint_rounding_and_single_precision_match_the_labs_runtime(self):
        # Measured through Unity 2021.3's float.ToString(custom format, InvariantCulture).
        self.assertEqual("0.0313", J.fmt(0.03125, 4))
        self.assertEqual("-0.0313", J.fmt(-0.03125, 4))
        self.assertEqual("1.3", J.fmt(1.25, 1))
        self.assertEqual("0.00002", J.fmt(0.000015, 5))
        self.assertEqual("1.23445", J.fmt(1.234445, 5))

    def test_merge2_text_multiplies_the_legacy_uniform_scale_and_makes_bad_components_1(self):
        text = J.merge2_text("D:\\x\\b.glb", {"x": 0.5, "y": 0, "z": 2}, {"x": 0, "y": 0, "z": 30}, 2.0, {"x": 1.5, "y": 1, "z": 0})
        self.assertEqual("D:/x/b.glb|0.5,0,2|0,0,30|3,2,2", text)   # z: 0 is not positive -> 1, times the legacy 2

    def test_placement_line_has_four_decimals_and_keeps_pipes_in_the_name(self):
        line = J.placement_line({"name": "Skin|A", "offset": {"x": 1.23456, "y": 0, "z": -0.5}, "scale": {"x": 2, "y": 1, "z": 1}})
        self.assertEqual("Skin|A|1.2346,0,-0.5|2,1,1", line)
        self.assertTrue(J.is_placed({"name": "P", "offset": {"x": 0, "y": 0, "z": 0.001}, "scale": {}}))
        self.assertFalse(J.is_placed({"name": "P", "offset": {}, "scale": {"x": 1, "y": 1, "z": 1}}))

    def test_recipe_jobs_take_only_recipes_with_an_input_and_sources_on_disk(self):
        with tempfile.TemporaryDirectory() as tmp:
            rec = os.path.join(tmp, "Assets", "FactorySource", "VehicleLab", "Recipes"); os.makedirs(rec)
            src = os.path.join(tmp, "a.glb"); open(src, "wb").close()
            src2 = os.path.join(tmp, "b.glb"); open(src2, "wb").close()
            json.dump({"srcFile": src, "modelRot": {"x": 0, "y": 6, "z": -45}, "parts": []}, open(os.path.join(rec, "rot.json"), "w"))
            json.dump({"srcFile": src, "srcFile2": src2, "model2Off": {"x": 0, "y": 0, "z": 0}, "model2Rot": {"x": 0, "y": 0, "z": 0}, "model2Scale": 1.0, "parts": []}, open(os.path.join(rec, "merged.json"), "w"))
            json.dump({"srcFile": src, "parts": [{"name": "P", "offset": {"x": 1, "y": 0, "z": 0}, "scale": {"x": 1, "y": 1, "z": 1}}]}, open(os.path.join(rec, "placed.json"), "w"))
            json.dump({"srcFile": src, "parts": []}, open(os.path.join(rec, "plain.json"), "w"))
            json.dump({"srcFile": src, "srcFile2": os.path.join(tmp, "missing.glb"), "parts": []}, open(os.path.join(rec, "gone.json"), "w"))
            json.dump({"srcFile": os.path.join(tmp, "x.fbx"), "modelRot": {"x": 1, "y": 0, "z": 0}, "parts": []}, open(os.path.join(rec, "fbx.json"), "w"))
            jobs = {j["key"].split("#")[1]: j for j in J.recipe_jobs(tmp)}
        self.assertEqual({"rot", "merged", "placed"}, set(jobs))
        self.assertEqual("0,6,-45", jobs["rot"]["proberot"])
        self.assertTrue(jobs["merged"]["merge2"].endswith("|0,0,0|0,0,0|1,1,1"))
        self.assertEqual(["P|1,0,0|1,1,1"], jobs["placed"]["parttx"])


if __name__ == "__main__":
    unittest.main()
