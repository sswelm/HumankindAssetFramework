"""tools/recipe_migrate.py: a Vehicle Lab recipe carried across a Model Workshop fuse."""
import io
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
import recipe_migrate  # noqa: E402

REPORT = """Model Workshop fuse report
source: D:/Models/galley_split.glb
output: D:/Models/galley_split_fused.glb
weld: 0 permille of the model's length
groups: 3

== group A — 3 part(s)
parts: Hull_1, Hull_2, Hull_3
Fused 3 part(s) -> 'Fused_A_Hull_1': 300 -> 290 verts (seams welded within 0.0002 = 0‰ of 20.4); islands 3 -> 1
rewound by part: Hull_1 0/100; Hull_2 0/100; Hull_3 0/100

== group B — 2 part(s)
parts: Deck_1, Bench_7
Fused 2 part(s) -> 'Fused_B_Deck_1': 100 -> 98 verts (seams welded within 0.0002 = 0‰ of 20.4); islands 2 -> 1

== group C — 2 part(s)
parts: Stray_1, Stray_2
Fused 2 part(s) -> 'Fused_C_Stray_1': 10 -> 10 verts (seams welded within 0.0002 = 0‰ of 20.4); islands 2 -> 2
"""


def part(name, role, off=(0.0, 0.0, 0.0), scale=(1.0, 1.0, 1.0)):
    return {"name": name, "verts": 8, "role": role, "vis": 1, "bone": "", "flip": 0,
            "offset": {"x": off[0], "y": off[1], "z": off[2]}, "scale": {"x": scale[0], "y": scale[1], "z": scale[2]}}


def recipe(parts):
    return {"srcFile": "D:/Models/galley_split.glb", "outGlb": "D:/Models/galley_split_Spin.glb", "frames": 15, "degrees": -360.0,
            "oarSweepDeg": 24.0, "oarDipDeg": 18.0, "oarFrames": 24, "parts": parts}


class RecipeMigrateTests(unittest.TestCase):
    def test_untouched_parts_keep_their_entries_and_the_dials_survive(self):
        old = recipe([part("Oar_1", 13), part("Hull_1", 0), part("Hull_2", 0), part("Hull_3", 0), part("Sail", 14)])
        new, summary, notes, problems = recipe_migrate.migrate(old, REPORT)
        self.assertEqual(0, problems)
        self.assertEqual(["Oar_1", "Fused_A_Hull_1", "Sail"], [p["name"] for p in new["parts"]])
        self.assertEqual(13, new["parts"][0]["role"])
        self.assertEqual(0, new["parts"][1]["role"])
        self.assertEqual(24.0, new["oarSweepDeg"])
        self.assertEqual("D:/Models/galley_split_fused.glb", new["srcFile"])
        self.assertEqual("D:/Models/galley_split_fused_Spin.glb", new["outGlb"])
        self.assertIn("2 part(s) kept by name", summary)

    def test_a_mixed_group_takes_the_majority_and_is_flagged(self):
        old = recipe([part("Deck_1", 16), part("Bench_7", 3), part("Hull_1", 0), part("Hull_2", 0), part("Hull_3", 16)])
        new, summary, notes, problems = recipe_migrate.migrate(old, REPORT)
        self.assertEqual(2, problems)
        names = {p["name"]: p for p in new["parts"]}
        self.assertEqual(0, names["Fused_A_Hull_1"]["role"])       # Body x2 over Structure x1
        self.assertIn(names["Fused_B_Deck_1"]["role"], (16, 3))      # a tie: one of the two, and flagged
        self.assertEqual(2, sum(1 for n in notes if n.startswith("MIXED")))

    def test_different_placements_in_one_group_are_flagged(self):
        old = recipe([part("Hull_1", 0, off=(1.0, 0.0, 0.0)), part("Hull_2", 0), part("Hull_3", 0)])
        new, summary, notes, problems = recipe_migrate.migrate(old, REPORT)
        self.assertEqual(1, problems)
        self.assertTrue(any(n.startswith("PLACED group A") for n in notes))
        self.assertEqual({"x": 1.0, "y": 0.0, "z": 0.0}, new["parts"][0]["offset"])   # the first member's

    def test_groups_outside_the_recipe_and_members_new_to_it_are_said(self):
        old = recipe([part("Hull_1", 0)])
        new, summary, notes, problems = recipe_migrate.migrate(old, REPORT)
        self.assertEqual(0, problems)
        self.assertEqual(["Fused_A_Hull_1"], [p["name"] for p in new["parts"]])
        self.assertTrue(any("2 member(s) of group A were not in the recipe" in n for n in notes))
        self.assertTrue(any("2 group(s) had no member in the recipe" in n for n in notes))

    def test_a_file_that_is_not_a_report_fails(self):
        with self.assertRaises(ValueError):
            recipe_migrate.migrate(recipe([part("Hull_1", 0)]), "not a report\n")

    def test_main_writes_the_file_and_exits_3_on_a_mixed_group(self):
        d = tempfile.mkdtemp()
        old = os.path.join(d, "old.json"); rep = os.path.join(d, "r.txt"); out = os.path.join(d, "new.json")
        with io.open(old, "w", encoding="utf-8") as f:
            json.dump(recipe([part("Deck_1", 16), part("Bench_7", 3)]), f)
        with io.open(rep, "w", encoding="utf-8") as f:
            f.write(REPORT)
        self.assertEqual(3, recipe_migrate.main(["x", old, rep, out]))
        self.assertEqual(["Fused_B_Deck_1"], [p["name"] for p in json.load(io.open(out, encoding="utf-8"))["parts"]])
        with io.open(old, "w", encoding="utf-8") as f:
            json.dump(recipe([part("Hull_1", 0), part("Hull_2", 0)]), f)
        self.assertEqual(0, recipe_migrate.main(["x", old, rep, out]))


if __name__ == "__main__":
    unittest.main()
