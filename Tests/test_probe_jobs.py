"""The probe jobs' argument text must be the Lab's own (tools/vehicle-probe-drill/probe_jobs.py formats a recipe's second
model, placements and orientation as VehicleLabWindow hands them to vehicle_rig.py; the in-process probe parses the same
text), so a recipe is drilled with the characters the Lab would send - a fifth decimal rounded differently would be a
different input on one side."""
import contextlib
import io
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools", "vehicle-probe-drill"))
import probe_jobs as J
import recipe_check as R
import oracle_inputs as O


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


class RecipeFallbackTests(unittest.TestCase):
    def test_a_recipe_left_for_its_second_model_is_not_judged_against_primary_only_rows(self):
        with tempfile.TemporaryDirectory() as tmp:
            rec = os.path.join(tmp, "Assets", "FactorySource", "VehicleLab", "Recipes"); os.makedirs(rec)
            src = os.path.join(tmp, "primary.glb"); open(src, "wb").close()
            own = R.key(src) + "#merged"
            recipe = {"srcFile": src, "srcFile2": "skinned.fbx", "parts": []}
            with open(os.path.join(rec, "merged.json"), "w") as f: json.dump(recipe, f)
            rows = os.path.join(tmp, "rows.txt")
            with open(rows, "w") as f:
                f.write("ROW\t" + R.key(src) + "\tPART|Hull|3|0,0,0|1,1,1|1||0\n")
                f.write("LEFT\t" + own + "\tthe FBX vehicle import needs Blender: skinned second model\n")
            output = io.StringIO()
            with contextlib.redirect_stdout(output): R.main(["recipe_check.py", tmp, rows])
            self.assertIn("not judged", output.getvalue())
            self.assertIn("RECIPES 0 judged", output.getvalue())
            self.assertNotIn("SAME merged.json", output.getvalue())


class OracleInputTests(unittest.TestCase):
    def test_declined_files_and_second_model_jobs_never_request_conversion(self):
        jobs = [{'key': 'merged', 'file': 'primary.glb', 'merge2': 'bad.fbx|0,0,0|0,0,0|1'},
                {'key': 'accepted', 'file': 'good.fbx', 'merge2': 'second.fbx|0,0,0|0,0,0|1'}]
        files, selected, needed = O.select_inputs(['bad.fbx', 'good.fbx'], jobs, {'bad.fbx', 'merged'})
        self.assertEqual(['good.fbx'], files)
        self.assertEqual(['accepted'], [j['key'] for j in selected])
        self.assertEqual(['good.fbx', 'second.fbx'], needed)

    def test_rewrites_only_oracle_paths_preserving_original_keys_and_merge_transforms(self):
        jobs = [{'key': 'recipe', 'file': 'A.FBX', 'merge2': 'B.fbx|1,2,3|4,5,6|2,2,2', 'parttx': ['Hull|1,0,0|1,1,1']}]
        files, output = O.convert_inputs(['A.FBX', 'plain.glb'], jobs, {'a.fbx': 'oracle-a.glb', 'b.fbx': 'oracle-b.glb'})
        self.assertEqual(['plain.glb'], files)
        self.assertEqual({'key': 'a.fbx', 'file': 'oracle-a.glb'}, output[0])
        self.assertEqual('recipe', output[1]['key'])
        self.assertEqual('oracle-b.glb|1,2,3|4,5,6|2,2,2', output[1]['merge2'])
        self.assertEqual(jobs[0]['parttx'], output[1]['parttx'])
        self.assertEqual('A.FBX', jobs[0]['file'])

    def test_missing_or_empty_conversion_mapping_is_an_error(self):
        for conversions in ({}, {'source.fbx': ''}):
            with self.subTest(conversions=conversions):
                with self.assertRaisesRegex(ValueError, 'no FBX oracle GLB'):
                    O.convert_inputs(['source.fbx'], [], conversions)


if __name__ == "__main__":
    unittest.main()
