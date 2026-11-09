"""tools/advisor: the Workshop advisor's corpus export and its metrics (Phase 0 of docs/Workshop-Advisor-Plan.md).

A model folder is built from scratch here - a raw GLB with duplicate part names, its marks, a split GLB whose children
share one POSITION accessor and subset the indices (as the Workshop's Split writes them), its fuse sidecar and the fuse
report, the fused GLB and a recipe on it - so every join the export makes is checked against a known answer, and the
metrics are checked on populations whose precision, recall and pair agreement are computed by hand.
"""
import io
import json
import os
import shutil
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools", "advisor"))
import corpus  # noqa: E402
import evaluate  # noqa: E402


# ---------------------------------------------------------------- a GLB from scratch

def make_glb(nodes, meshes, positions, index_lists):
    """nodes: [{name, mesh, children?, translation?}], meshes: [[(position accessor, indices accessor or None)]],
    positions: [[(x,y,z), ...]] one list per position accessor, index_lists: [[i, ...]] one per index accessor."""
    bin_ = bytearray()
    views, accessors = [], []
    pos_acc = []
    for pts in positions:
        off = len(bin_)
        for p in pts:
            bin_ += struct.pack("<3f", *p)
        views.append({"buffer": 0, "byteOffset": off, "byteLength": len(pts) * 12})
        xs, ys, zs = zip(*pts)
        accessors.append({"bufferView": len(views) - 1, "componentType": 5126, "count": len(pts), "type": "VEC3",
                          "min": [min(xs), min(ys), min(zs)], "max": [max(xs), max(ys), max(zs)]})
        pos_acc.append(len(accessors) - 1)
    idx_acc = []
    for idx in index_lists:
        while len(bin_) % 4:
            bin_ += b"\0"
        off = len(bin_)
        for i in idx:
            bin_ += struct.pack("<H", i)
        views.append({"buffer": 0, "byteOffset": off, "byteLength": len(idx) * 2})
        accessors.append({"bufferView": len(views) - 1, "componentType": 5123, "count": len(idx), "type": "SCALAR"})
        idx_acc.append(len(accessors) - 1)
    while len(bin_) % 4:
        bin_ += b"\0"
    mesh_json = []
    for prims in meshes:
        ps = []
        for pa, ia in prims:
            prim = {"attributes": {"POSITION": pos_acc[pa]}, "mode": 4}
            if ia is not None:
                prim["indices"] = idx_acc[ia]
            ps.append(prim)
        mesh_json.append({"primitives": ps})
    root = {"asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [i for i, n in enumerate(nodes) if not n.get("_child")]}],
            "nodes": [{k: v for k, v in n.items() if not k.startswith("_")} for n in nodes],
            "meshes": mesh_json, "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(bin_)}]}
    js = json.dumps(root).encode("utf-8")
    while len(js) % 4:
        js += b" "
    total = 12 + 8 + len(js) + 8 + len(bin_)
    return (b"glTF" + struct.pack("<II", 2, total) + struct.pack("<II", len(js), 0x4E4F534A) + js
            + struct.pack("<II", len(bin_), 0x004E4942) + bytes(bin_))


# two triangles far apart = two islands; one triangle = one island
TWO_ISLANDS = [(0, 0, 0), (1, 0, 0), (0, 1, 0), (10, 0, 0), (11, 0, 0), (10, 1, 0)]
ONE_TRI = [(0, 0, 5), (2, 0, 5), (0, 2, 5)]


class CorpusFolder(unittest.TestCase):
    """One model folder: raw -> split -> fused with every sidecar, and a recipe on the fused file."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="advisor-")
        cls.models = os.path.join(cls.tmp, "models")
        cls.recipes = os.path.join(cls.tmp, "recipes")
        cls.out = os.path.join(cls.tmp, "out")
        folder = os.path.join(cls.models, "CoastalMonitor")   # a folder the category table knows
        os.makedirs(folder)
        os.makedirs(cls.recipes)
        # RAW: three mesh nodes, two of them called "Material2" (the Cutter renames the second to Material2_2), the third
        # a child of the first, translated; a nameless non-mesh root.
        raw = make_glb(
            nodes=[{"name": "Material2", "mesh": 0, "children": [1]}, {"name": "Material2", "mesh": 1, "translation": [0.0, 0.0, 100.0], "_child": True},
                   {"name": "Deck", "mesh": 0}, {"name": "Rig"}],
            meshes=[[(0, 0)], [(1, 1)]], positions=[TWO_ISLANDS, ONE_TRI], index_lists=[[0, 1, 2, 3, 4, 5], [0, 1, 2]])
        cls.raw = os.path.join(folder, "boat.glb")
        with open(cls.raw, "wb") as f:
            f.write(raw)
        with io.open(cls.raw + ".marks.txt", "w", encoding="utf-8") as f:
            f.write("#fuse-groups v2\nS|0|Material2\nX|1|Material2_2\nT|9|Nobody\n")
        # SPLIT: the Workshop's lossless split - two children against ONE position accessor (the raw two-island mesh),
        # each referencing three of its six vertices; plus the deck and the second material untouched.
        split = make_glb(
            nodes=[{"name": "Material2_Part_001", "mesh": 0}, {"name": "Material2_Part_002", "mesh": 1}, {"name": "Material2_2", "mesh": 2}, {"name": "Deck", "mesh": 3}],
            meshes=[[(0, 0)], [(0, 1)], [(1, 2)], [(0, 3)]], positions=[TWO_ISLANDS, ONE_TRI],
            index_lists=[[0, 1, 2], [3, 4, 5], [0, 1, 2], [0, 1, 2, 3, 4, 5]])
        cls.split = os.path.join(folder, "boat_split.glb")
        with open(cls.split, "wb") as f:
            f.write(split)
        with io.open(cls.split + ".fuse.txt", "w", encoding="utf-8") as f:
            f.write("#fuse-groups v2\n#name|H|Hull\nH|0|Material2_Part_001\nH|1|Material2_Part_002\nD|3|Deck\n")
        with io.open(cls.split + ".marks.txt", "w", encoding="utf-8") as f:
            f.write("#fuse-groups v2\nX|2|Material2_2\n")
        cls.fused_path = os.path.join(folder, "boat_split_fused.glb")
        with io.open(cls.fused_path + ".fuse-report.txt", "w", encoding="utf-8") as f:
            f.write("Model Workshop fuse report\nsource: %s\noutput: %s\nweld: 0 permille of the model's length\ngroups: 2\n\n"
                    "== group H — 2 part(s)\nparts: Material2_Part_001, Material2_Part_002\n"
                    "Fused 2 part(s) -> 'Fused_H_Hull': 6 -> 6 verts (seams welded within 0.001 = 0‰ of 11); islands 2 -> 2\n\n"
                    "== group D — 1 part(s)\nparts: Deck\nFused 1 part(s) -> 'Fused_D_Deck': 6 -> 6 verts (seams welded within 0.001 = 0‰ of 11); islands 2 -> 2\n"
                    % (cls.split.replace("\\", "/"), cls.fused_path.replace("\\", "/")))
        fused = make_glb(nodes=[{"name": "Fused_H_Hull", "mesh": 0}, {"name": "Fused_D_Deck", "mesh": 0}, {"name": "Material2_2", "mesh": 1}],
                         meshes=[[(0, 0)], [(1, 1)]], positions=[TWO_ISLANDS, ONE_TRI], index_lists=[[0, 1, 2, 3, 4, 5], [0, 1, 2]])
        with open(cls.fused_path, "wb") as f:
            f.write(fused)
        recipe = {"srcFile": cls.fused_path.replace("\\", "/"), "outGlb": cls.fused_path.replace("\\", "/")[:-4] + "_Spin.glb",
                  "bodyReducePct": 65.0, "defaultReducePct": 50.0, "structureReducePct": 80.0, "frames": 15,
                  "parts": [{"name": "Fused_H_Hull", "verts": 6, "role": 16, "vis": 1, "flip": 0, "offset": {"x": 0, "y": 0, "z": 0}, "scale": {"x": 1, "y": 1, "z": 1}},
                            {"name": "Fused_D_Deck", "verts": 6, "role": 0, "vis": 1, "flip": 0, "offset": {"x": 0, "y": 1.5, "z": 0}, "scale": {"x": 1, "y": 1, "z": 1}},
                            {"name": "Material2_2", "verts": 3, "role": 3, "vis": 0, "flip": 0, "offset": {"x": 0, "y": 0, "z": 0}, "scale": {"x": 1, "y": 1, "z": 1}},
                            {"name": "Gone", "verts": 9, "role": 4, "vis": 1, "flip": 0, "offset": {"x": 0, "y": 0, "z": 0}, "scale": {"x": 1, "y": 1, "z": 1}}]}
        with io.open(os.path.join(cls.recipes, "boat.json"), "w", encoding="utf-8") as f:
            json.dump(recipe, f)
        cls.parts, cls.models_rows, cls.report = corpus.build(cls.models, cls.recipes, cls.out, use_cache=False, log=lambda *_: None)
        cls.by = {(r["file"], r["name"]): r for r in cls.parts}

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def row(self, file, name):
        key = ("CoastalMonitor/" + file, name)
        self.assertIn(key, self.by, "no row %s; rows: %s" % (key, sorted(self.by)))
        return self.by[key]

    def test_unique_names_and_features_at_the_raw_file(self):
        a = self.row("boat.glb", "Material2")
        b = self.row("boat.glb", "Material2_2")
        self.assertEqual((a["file_name"], b["file_name"]), ("Material2", "Material2"))
        self.assertEqual((a["verts"], a["tris"], a["islands"]), (6, 2, 2))
        self.assertEqual((b["verts"], b["tris"], b["islands"]), (3, 1, 1))
        self.assertEqual(b["parent_index"], 0)
        self.assertEqual(b["depth"], 1)
        self.assertAlmostEqual(b["cz"], 105.0)   # the child's translation applied to the one triangle at z = 5
        self.assertEqual(a["mesh_shared"], 2)    # Material2 and Deck share mesh 0
        self.assertNotIn("Rig", [r["name"] for r in self.parts])   # a node without a mesh is not a part

    def test_marks_resolve_by_index_and_name_and_refuse_the_rest(self):
        self.assertEqual(self.row("boat.glb", "Material2")["mark"], "S")
        self.assertEqual(self.row("boat.glb", "Material2_2")["mark"], "X")
        self.assertEqual(self.row("boat.glb", "Deck")["mark"], "")
        self.assertTrue(any("T|9|Nobody fits no single part" in line for line in self.report), self.report)

    def test_split_children_count_only_the_vertices_they_reference(self):
        p1 = self.row("boat_split.glb", "Material2_Part_001")
        p2 = self.row("boat_split.glb", "Material2_Part_002")
        self.assertEqual((p1["verts"], p1["tris"], p1["islands"]), (3, 1, 1))
        self.assertEqual((p2["verts"], p2["tris"], p2["islands"]), (3, 1, 1))
        self.assertAlmostEqual(p2["cx"], 10.5)   # the far triangle only, not the parent's whole box
        self.assertEqual(p1["stage"], "split")

    def test_fuse_letters_group_names_and_the_report(self):
        p1 = self.row("boat_split.glb", "Material2_Part_001")
        self.assertEqual((p1["fuse_letter"], p1["fuse_group_name"], p1["fused_into"], p1["fused_letter"]), ("H", "Hull", "Fused_H_Hull", "H"))
        deck = self.row("boat_split.glb", "Deck")
        self.assertEqual((deck["fuse_letter"], deck["fuse_group_name"], deck["fused_into"]), ("D", "", "Fused_D_Deck"))
        self.assertEqual(self.row("boat_split.glb", "Material2_2")["mark"], "X")

    def test_recipe_roles_land_on_the_fused_file_and_travel_back_through_the_report(self):
        hull = self.row("boat_split_fused.glb", "Fused_H_Hull")
        self.assertEqual((hull["role"], hull["role_id"], hull["vis"], hull["placed"], hull["recipe"]), ("Structure", 16, 1, 0, "boat.json"))
        self.assertEqual(self.row("boat_split_fused.glb", "Fused_D_Deck")["placed"], 1)
        self.assertEqual(self.row("boat_split_fused.glb", "Material2_2")["role"], "Ignore")
        self.assertEqual(self.row("boat_split.glb", "Material2_Part_002")["role_via_fuse"], "Structure")
        self.assertEqual(self.row("boat_split.glb", "Deck")["role_via_fuse"], "Body")
        self.assertEqual(self.row("boat_split.glb", "Material2_2")["role_via_fuse"], "")   # not in any group
        self.assertTrue(any("part 'Gone' is not a node" in line for line in self.report), self.report)

    def test_models_row_carries_the_dials_and_the_match_count(self):
        m = [m for m in self.models_rows if m["model"] == "CoastalMonitor"]
        self.assertEqual(len(m), 1)
        self.assertEqual((m[0]["recipe"], m[0]["recipe_parts"], m[0]["recipe_parts_matched"], m[0]["recipe_src_stage"]), ("boat.json", 4, 3, "fused"))
        self.assertEqual(json.loads(m[0]["dials"]), {"bodyReducePct": 65.0, "defaultReducePct": 50.0, "structureReducePct": 80.0})
        self.assertEqual(m[0]["category"], "ship")

    def test_the_csv_round_trips_into_the_evaluator(self):
        parts, models = evaluate.load_corpus(self.out)
        self.assertEqual(len(parts), len(self.parts))
        roles = evaluate.population("role", parts)
        self.assertEqual(sorted(r["truth"] for r in roles), ["Body", "Ignore", "Structure"])
        deletes = evaluate.population("delete", parts)
        self.assertEqual(sum(1 for r in deletes if r["truth"]), 3)   # X at raw, X at split, Ignore at fused
        splits = evaluate.population("split", parts)
        self.assertEqual([r["file"] for r in splits if r["truth"]], ["CoastalMonitor/boat.glb"])   # the split file's parts are not split candidates
        groups = evaluate.population("group", parts)
        self.assertEqual(len(groups), 4)
        lines = evaluate.evaluate(parts, models, {t: list(evaluate.BASELINES[t]) for t in evaluate.TASKS}, target=30000)
        text = "\n".join(lines)
        self.assertIn("### role: 3 parts", text)
        self.assertIn("| CoastalMonitor |", text)   # the dials table has the model's projection


class Metrics(unittest.TestCase):
    """The scoring on populations small enough to do by hand."""

    @staticmethod
    def rows(spec):
        return [{"file": f, "node_index": i, "name": "n%d" % i, "model": "m", "category": "c", "truth": t} for i, (f, t) in enumerate(spec)]

    def test_precision_recall_per_class_and_for_one_class(self):
        rows = self.rows([("a", "X"), ("a", "X"), ("a", ""), ("a", "")])
        preds = {evaluate.row_key(rows[0]): "X", evaluate.row_key(rows[2]): "X"}
        p, r, tp, fp, fn = evaluate.precision_recall(rows, preds, "X")
        self.assertEqual((p, r, tp, fp, fn), (0.5, 0.5, 1, 1, 1))
        self.assertIsNone(evaluate.precision_recall(rows, {}, "X")[0])   # nothing proposed: precision undefined, never 0 or 100

    def test_pair_agreement_counts_pairs_together_and_apart(self):
        # truth: {0,1} together, 2 alone, 3 alone -> 1 of 6 pairs together
        rows = self.rows([("a", "A"), ("a", "A"), ("a", "solo:2"), ("a", "solo:3")])
        perfect = {evaluate.row_key(r): r["truth"] for r in rows}
        g = evaluate.rand_index(rows, perfect)
        self.assertEqual((g["rand"], g["pairs"], g["together_precision"], g["together_recall"]), (1.0, 6, 1.0, 1.0))
        # all together: every pair proposed, one right -> precision 1/6, recall 1, Rand 1/6
        g = evaluate.rand_index(rows, {evaluate.row_key(r): "all" for r in rows})
        self.assertAlmostEqual(g["together_precision"], 1 / 6)
        self.assertEqual(g["together_recall"], 1.0)
        self.assertAlmostEqual(g["rand"], 1 / 6)
        # all apart: nothing proposed together -> precision undefined, recall 0, Rand 5/6
        g = evaluate.rand_index(rows, {evaluate.row_key(r): "solo:%d" % r["node_index"] for r in rows})
        self.assertIsNone(g["together_precision"])
        self.assertEqual(g["together_recall"], 0.0)
        self.assertAlmostEqual(g["rand"], 5 / 6)

    def test_projection_is_the_labs_verify_statistics(self):
        # the recipe's probe verts, each role's tier off, Ignore deleted, a role without a tier unreduced
        parts = [{"model": "m", "role": "Body", "recipe_verts": "1000", "verts": "1"}, {"model": "m", "role": "Ignore", "recipe_verts": "5000", "verts": "1"},
                 {"model": "m", "role": "Rotor", "recipe_verts": "100", "verts": "1"}, {"model": "other", "role": "Body", "recipe_verts": "99999", "verts": "1"},
                 {"model": "m", "role": "Sail", "recipe_verts": "", "verts": "40"}]   # no recipe count: the file's stands in
        self.assertAlmostEqual(evaluate.projection(parts, "m", {"bodyReducePct": 75.0}), 250.0 + 100.0 + 40.0)
        self.assertIsNone(evaluate.projection(parts, "nobody", {}))

    def test_leave_one_model_out_never_trains_on_the_held_out_model(self):
        rows = []
        for m, t in (("a", "Body"), ("a", "Body"), ("b", "Sail"), ("b", "Sail"), ("b", "Sail")):
            rows.append({"file": m, "node_index": len(rows), "name": "", "model": m, "category": "c", "truth": t})
        models = [{"model": "a", "category": "c", "dials_parsed": {}}, {"model": "b", "category": "c", "dials_parsed": {}}]
        preds = evaluate.leave_one_model_out("role", rows, models, evaluate.Baselines.majority_role)
        self.assertEqual(preds[("a", 0)], "Sail")   # a is judged with b's majority
        self.assertEqual(preds[("b", 2)], "Body")

    def test_a_proposer_module_is_loaded_by_path_and_scored(self):
        tmp = tempfile.mkdtemp(prefix="advisor-prop-")
        try:
            path = os.path.join(tmp, "oracle.py")
            with io.open(path, "w", encoding="utf-8") as f:
                f.write("def propose(task, test, train, tm, tms):\n    return {(r['file'], r['node_index']): r['truth'] for r in test}\n")
            mod = evaluate.load_proposer(path)
            rows = self.rows([("a", "X"), ("a", "")])
            preds = mod.propose("delete", rows, [], {}, [])
            self.assertEqual(evaluate.precision_recall(rows, preds, "X")[:2], (1.0, 1.0))
            with io.open(path, "w", encoding="utf-8") as f:
                f.write("x = 1\n")
            with self.assertRaises(ValueError):
                evaluate.load_proposer(path)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


class BlenderNamesAndSecondModel(unittest.TestCase):
    """A recipe the Lab probed through Blender (duplicates named Box.001, Box.002), with a second model's B_ parts, and
    a recipe whose parts are one mesh's loose parts rather than the file's nodes."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="advisor-bl-")
        cls.models = os.path.join(cls.tmp, "models")
        cls.recipes = os.path.join(cls.tmp, "recipes")
        cls.out = os.path.join(cls.tmp, "out")
        heli = os.path.join(cls.models, "Rah66")
        gun = os.path.join(cls.models, "Ozhiga Organ Gun")
        os.makedirs(heli)
        os.makedirs(gun)
        os.makedirs(cls.recipes)
        glb = make_glb(nodes=[{"name": "Box", "mesh": 0}, {"name": "Box", "mesh": 1}, {"name": "Box", "mesh": 0}, {"name": "Rotor", "mesh": 1}],
                       meshes=[[(0, 0)], [(1, 1)]], positions=[TWO_ISLANDS, ONE_TRI], index_lists=[[0, 1, 2, 3, 4, 5], [0, 1, 2]])
        cls.heli_glb = os.path.join(heli, "rah66.glb")
        with open(cls.heli_glb, "wb") as f:
            f.write(glb)
        second = os.path.join(heli, "pod.glb")
        with open(second, "wb") as f:
            f.write(glb)

        def part(name, role, verts):
            return {"name": name, "verts": verts, "role": role, "vis": 1, "flip": 0, "offset": {"x": 0, "y": 0, "z": 0}, "scale": {"x": 1, "y": 1, "z": 1}}
        # Blender's suffixes, listed out of node order on purpose; the middle Box (3 verts) is Box.001 by node order
        recipe = {"srcFile": cls.heli_glb.replace("\\", "/"), "srcFile2": second.replace("\\", "/"), "bodyReducePct": 10.0,
                  "parts": [part("Box.002", 0, 6), part("Box", 7, 6), part("Rotor", 8, 3), part("Box.001", 2, 3), part("B_Box.001", 4, 3), part("Lost.004", 4, 1)]}
        with io.open(os.path.join(cls.recipes, "rah66.json"), "w", encoding="utf-8") as f:
            json.dump(recipe, f)
        gun_glb = os.path.join(gun, "organ.glb")
        with open(gun_glb, "wb") as f:
            f.write(make_glb(nodes=[{"name": "defaultMaterial", "mesh": 0}], meshes=[[(0, 0)]], positions=[TWO_ISLANDS], index_lists=[[0, 1, 2, 3, 4, 5]]))
        loose = {"srcFile": gun_glb.replace("\\", "/"), "parts": [part("defaultMaterial", 4, 3)] + [part("defaultMaterial.%03d" % i, 4, 3) for i in range(1, 6)]}
        with io.open(os.path.join(cls.recipes, "organ.json"), "w", encoding="utf-8") as f:
            json.dump(loose, f)
        cls.parts, cls.models_rows, cls.report = corpus.build(cls.models, cls.recipes, cls.out, use_cache=False, log=lambda *_: None)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def heli(self, name):
        return [r for r in self.parts if r["model"] == "Rah66" and r["name"] == name and r["stage"] == "raw"][0]

    def test_blender_suffixes_join_in_node_order_after_the_exact_names(self):
        self.assertEqual((self.heli("Box")["role"], self.heli("Box")["name_join"], self.heli("Box")["recipe_name"]), ("Gun", "exact", "Box"))
        self.assertEqual((self.heli("Box_2")["role"], self.heli("Box_2")["name_join"], self.heli("Box_2")["recipe_name"]), ("Turret", "order", "Box.001"))
        self.assertEqual((self.heli("Box_3")["role"], self.heli("Box_3")["recipe_name"]), ("Body", "Box.002"))
        self.assertEqual(self.heli("Rotor")["role"], "Rotor")
        self.assertTrue(any("part 'Lost.004' is not a node" in line for line in self.report), self.report)

    def test_the_second_models_parts_ride_along_as_recipe_only_rows(self):
        b = [r for r in self.parts if r["name"] == "B_Box.001"]
        self.assertEqual(len(b), 1)
        self.assertEqual((b[0]["stage"], b[0]["file"], b[0]["role"], b[0]["recipe"]), ("recipe-only", "Rah66/pod.glb", "Default", "rah66.json"))
        m = [m for m in self.models_rows if m["model"] == "Rah66"][0]
        self.assertEqual((m["recipe_parts"], m["recipe_parts_matched"]), (6, 5))   # Lost.004 is the one unmatched
        self.assertTrue(any("1 part(s) of the second model (pod.glb)" in line for line in self.report), self.report)

    def test_a_loose_parts_recipe_is_reported_not_guessed(self):
        m = [m for m in self.models_rows if m["model"] == "Ozhiga Organ Gun"][0]
        self.assertEqual((m["recipe_parts"], m["recipe_parts_matched"]), (6, 1))
        self.assertTrue(any("6 parts against 1 mesh node(s)" in line for line in self.report), self.report)
        self.assertFalse(any("defaultMaterial.001" in line for line in self.report))   # one summary line, not one per part

    def test_the_verts_projection_covers_the_second_model(self):
        parts, models = evaluate.load_corpus(self.out)
        m = [m for m in models if m["model"] == "Rah66"][0]
        # Box (Gun, no tier) 6 + Box_2 (Turret, no dial saved -> unreduced) 3 + Box_3 (Body at 10%) 5.4 + Rotor 3 + B_Box.001 (Default, no dial) 3 = 20.4
        self.assertAlmostEqual(evaluate.projection(parts, "Rah66", m["dials_parsed"]), 20.4)


class Rules(unittest.TestCase):
    def test_stage_from_the_file_name(self):
        self.assertEqual([corpus.stage_of(n) for n in ("a.glb", "a_cut.glb", "a_cut_split.glb", "a_split_fused.glb", "khelandion_2_object14_split5.glb")],
                         ["raw", "cut", "split", "fused", "split"])

    def test_unique_names_never_collide_with_a_name_the_file_already_has(self):
        nodes = [{"name": "Deck", "mesh": 0}, {"name": "Deck_2", "mesh": 0}, {"name": "Deck", "mesh": 0}, {"name": "Deck"}]
        self.assertEqual(corpus.unique_node_names(nodes), {0: "Deck", 1: "Deck_2", 2: "Deck_3"})

    def test_sidecar_lines_in_the_v2_and_legacy_layouts(self):
        tmp = tempfile.mkdtemp(prefix="advisor-sc-")
        try:
            p = os.path.join(tmp, "x.glb.fuse.txt")
            with io.open(p, "w", encoding="utf-8") as f:
                f.write("#fuse-groups v2\n#name|A|Hull\nA|3|Odd|Name\nB|Deck\n")
            lines, names = corpus.read_sidecar(p)
            self.assertEqual(lines, [("A", 3, "Odd|Name"), ("B", None, "Deck")])
            self.assertEqual(names, {"A": "Hull"})
            with io.open(p, "w", encoding="utf-8") as f:
                f.write("A|Hull|3\nB|Deck\n")
            lines, _ = corpus.read_sidecar(p)
            self.assertEqual(lines, [("A", 3, "Hull"), ("B", None, "Deck")])
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_resolution_prefers_the_index_when_its_node_still_carries_the_name(self):
        rows = [{"node_index": 0, "name": "Deck", "file_name": "Deck"}, {"node_index": 1, "name": "Deck_2", "file_name": "Deck"},
                {"node_index": 2, "name": "Mast", "file_name": "Mast"}]
        refused = []
        out = corpus.resolve_sidecar([("A", 1, "Deck"), ("B", 7, "Mast"), ("C", None, "Deck"), ("D", 5, "Ghost")], rows, refused)
        self.assertEqual(out, {1: "A", 2: "B"})   # 'Deck' at index 1 is the file's name there; Mast by its unique name
        self.assertEqual(len(refused), 2, refused)   # 'Deck' with no index names two parts; Ghost names none


if __name__ == "__main__":
    unittest.main()
