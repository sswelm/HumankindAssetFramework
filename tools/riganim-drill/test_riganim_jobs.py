"""Regression checks for the registry coverage and Factory argument mapping."""
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from riganim_jobs import jobs


class RigAnimJobsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.source = self.root / "model.glb"
        self.source.write_bytes(b"glTF")
        self.pack = self.root / "pack.json"

    def run_recipe(self, model):
        self.pack.write_text(json.dumps({"models": [model]}), encoding="utf-8")
        result = subprocess.run([sys.executable, str(Path(__file__).with_name("riganim_jobs.py")),
                                 str(self.pack), str(self.root)], capture_output=True, text=True)
        return result.returncode, result.stdout.strip()

    def test_missing_source_cannot_reduce_coverage_silently(self):
        code, output = self.run_recipe({"animated": True, "resourceName": "Gone", "modelFile": str(self.root / "missing.glb")})
        self.assertEqual(code, 3)
        self.assertTrue(output.startswith("UNAVAILABLE\tGone\t"))

    def test_invalid_recipe_fails_instead_of_being_left(self):
        with self.source.open("r+b") as source:
            source.truncate(41_000_000)  # even a sample-size exclusion must not hide an invalid recipe
        code, output = self.run_recipe({"animated": True, "resourceName": "Broken", "modelFile": str(self.source),
                                        "animStateDriven": True})
        self.assertEqual(code, 1)
        self.assertTrue(output.startswith("INVALID\tBroken\t"))

    def test_config_for_trims_arguments_and_reuses_existing_albedo(self):
        albedo = self.root / "Assets" / "FactorySource" / "Unit" / "Unit_albedo.png"
        albedo.parent.mkdir(parents=True)
        albedo.write_bytes(b"edited")
        recipe = {"animated": True, "resourceName": "Unit", "modelFile": str(self.source),
                  "reuseExtracted": True, "animateBones": " wheel ", "animClip": " Idle ",
                  "socketBones": " Gun=Turret ", "staticParts": " sail "}
        fields = jobs(self.pack_for(recipe), str(self.root))[0].split("\t")
        self.assertEqual(fields[0], "JOB")
        self.assertEqual((fields[4], fields[5], fields[6], fields[12], fields[14]),
                         ("wheel", "Idle", "0", "Gun=Turret", "sail"))
        albedo.unlink()
        self.assertEqual(jobs(self.pack_for(recipe), str(self.root))[0].split("\t")[6], "1")

    def pack_for(self, model):
        self.pack.write_text(json.dumps({"models": [model]}), encoding="utf-8")
        return str(self.pack)


if __name__ == "__main__":
    unittest.main()
