"""tools/advisor: the Workshop advisor's corpus export and its metrics (Phase 0 of docs/Workshop-Advisor-Plan.md).

A model folder is built from scratch here - a raw GLB with duplicate part names, its marks, a split GLB whose children
share one POSITION accessor and subset the indices (as the Workshop's Split writes them), its fuse sidecar and the fuse
report, the fused GLB and a recipe on it - so every join the export makes is checked against a known answer, and the
metrics are checked on populations whose precision, recall and pair agreement are computed by hand.
"""
import io
from contextlib import redirect_stdout
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

def make_glb(nodes, meshes, positions, index_lists, modes=None):
    """nodes: [{name, mesh, children?, translation?}], meshes: [[(position accessor, indices accessor or None)]],
    positions: [[(x,y,z), ...]] one list per position accessor, index_lists: [[i, ...]] one per index accessor,
    modes: {mesh index: primitive mode} for anything but TRIANGLES."""
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
    for mi, prims in enumerate(meshes):
        ps = []
        for pa, ia in prims:
            prim = {"attributes": {"POSITION": pos_acc[pa]}, "mode": (modes or {}).get(mi, 4)}
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

    def test_the_delete_population_is_every_judged_file(self):
        base = {"model": "m", "category": "c", "stage": "raw", "mark": "", "fuse_letter": "", "role": "", "node_index": 0, "name": "n"}
        rows = [dict(base, file="marked", mark="X"), dict(base, file="marked"),
                dict(base, file="lettered", fuse_letter="A"), dict(base, file="lettered"),        # fused, nothing deleted: negatives
                dict(base, file="untouched"), dict(base, file="recipe", role="Ignore"), dict(base, file="recipe", role="Body")]
        pop = evaluate.population("delete", rows)
        self.assertEqual(sorted(set(r["file"] for r in pop)), ["lettered", "marked", "recipe"])
        self.assertEqual([r["truth"] for r in pop].count("X"), 2)   # the X mark and the Ignore role

    def test_a_dial_the_recipe_does_not_carry_reads_as_the_labs_default(self):
        m = {"category": "c", "dials_parsed": {"bodyReducePct": 40.0}}
        self.assertEqual((evaluate.saved_dial(m, "bodyReducePct"), evaluate.saved_dial(m, "sailReducePct")), (40.0, 0.0))
        # rigging and structure predate the recipe keys: the Lab reads 75 and 50 for a recipe without them
        self.assertEqual((evaluate.saved_dial(m, "riggingReducePct"), evaluate.saved_dial(m, "structureReducePct")), (75.0, 50.0))
        parts = [{"model": "m", "role": "Rigging", "recipe_verts": "100", "verts": "1"}]
        self.assertAlmostEqual(evaluate.projection(parts, "m", {}), 25.0)
        others = [{"model": "a", "category": "c", "dials_parsed": {"bodyReducePct": 40.0}}, {"model": "b", "category": "c", "dials_parsed": {"bodyReducePct": 60.0, "sailReducePct": 30.0}},
                  {"model": "d", "category": "c", "dials_parsed": {"bodyReducePct": 50.0}}]
        pred = evaluate.Baselines.median_dials("dials", [], [], {"category": "c"}, others)
        self.assertEqual((pred["bodyReducePct"], pred["sailReducePct"]), (50.0, 0.0))   # the two recipes without a sail dial vote 0

    def test_leave_one_model_out_never_trains_on_the_held_out_model(self):
        rows = []
        for m, t in (("a", "Body"), ("a", "Body"), ("b", "Sail"), ("b", "Sail"), ("b", "Sail")):
            rows.append({"file": m, "node_index": len(rows), "name": "", "model": m, "category": "c", "truth": t})
        models = [{"model": "a", "category": "c", "dials_parsed": {}}, {"model": "b", "category": "c", "dials_parsed": {}}]
        preds = evaluate.leave_one_model_out("role", rows, models, evaluate.Baselines.majority_role)
        self.assertEqual(preds[("a", 0)], "Sail")   # a is judged with b's majority
        self.assertEqual(preds[("b", 2)], "Body")

    def test_held_out_rows_and_models_expose_features_without_answers(self):
        answers = {"truth": "X", "mark": "X", "auto_mark": "1", "fuse_letter": "A", "fuse_group_name": "Hull",
                   "fused_into": "Fused_A_Hull", "fused_letter": "A", "role": "Ignore", "role_id": "3",
                   "role_via_fuse": "Ignore", "recipe": "saved.json", "recipe_via_fuse": "saved.json",
                   "placed": "1", "recipe_name": "Hull", "name_join": "exact", "future_answer": "X"}
        rows = [dict(answers, model=m, category="ship", file=m + ".glb", node_index=0, name="Hull",
                     verts="12", islands="2", vis="0") for m in ("a", "b")]
        models = [{"model": m, "category": "ship", "files": m + ".glb", "recipe": "saved.json",
                   "dials": '{"bodyReducePct": 75}', "dials_parsed": {"bodyReducePct": 75}} for m in ("a", "b")]
        calls = []
        def inspect(task, test, train, tm, tms):
            calls.append(task)
            self.assertTrue(set(answers).isdisjoint(test[0]), test[0])
            self.assertTrue({"dials", "dials_parsed", "recipe"}.isdisjoint(tm), tm)
            self.assertEqual((test[0]["verts"], test[0]["islands"], test[0]["vis"]), ("12", "2", "0"))
            self.assertEqual(train[0]["truth"], "X")
            self.assertEqual(tms[0]["dials_parsed"]["bodyReducePct"], 75)
            self.assertNotEqual(train[0]["model"], tm["model"])
            return {evaluate.row_key(r): "" for r in test}
        for task in ("role", "delete", "delete-hand", "split", "group"):
            evaluate.leave_one_model_out(task, rows, models, inspect)
        self.assertEqual(len(calls), 10)

    def test_saved_dials_are_not_given_to_the_held_out_proposer(self):
        models = [{"model": m, "category": "ship", "dials": '{"bodyReducePct": 75}',
                   "dials_parsed": {"bodyReducePct": 75}} for m in ("a", "b")]
        def inspect(task, test, train, tm, tms):
            self.assertTrue({"dials", "dials_parsed"}.isdisjoint(tm), tm)
            self.assertEqual(tms[0]["dials_parsed"]["bodyReducePct"], 75)
            tms[0]["dials_parsed"]["bodyReducePct"] = 0
            return {d: 0.0 for d in evaluate.DIALS}
        self.assertEqual(set(evaluate.leave_one_model_out("dials", [], models, inspect)), {"a", "b"})
        self.assertEqual([m["dials_parsed"]["bodyReducePct"] for m in models], [75, 75])

    def test_proposer_mutation_cannot_change_scoring_truth_or_later_folds(self):
        rows = [dict(r, model=m) for r, m in zip(self.rows([("a", "Body"), ("b", "Sail")]), ("a", "b"))]
        models = [{"model": m, "category": "c", "dials_parsed": {"bodyReducePct": 50}} for m in ("a", "b")]
        def mutate(task, test, train, tm, tms):
            self.assertIn(train[0]["truth"], ("Body", "Sail"))
            self.assertEqual(tms[0]["dials_parsed"]["bodyReducePct"], 50)
            test[0]["truth"] = "Ignore"
            train[0]["truth"] = "Ignore"
            tms[0]["dials_parsed"]["bodyReducePct"] = 0
            return {}
        evaluate.leave_one_model_out("role", rows, models, mutate)
        self.assertEqual([r["truth"] for r in rows], ["Body", "Sail"])
        self.assertEqual([m["dials_parsed"]["bodyReducePct"] for m in models], [50, 50])

    def test_a_proposer_module_is_loaded_by_path_and_scored(self):
        tmp = tempfile.mkdtemp(prefix="advisor-prop-")
        try:
            path = os.path.join(tmp, "oracle.py")
            with io.open(path, "w", encoding="utf-8") as f:
                f.write("def propose(task, test, train, tm, tms):\n    return {(r['file'], r['node_index']): 'X' if r['verts'] == '3' else '' for r in test}\n")
            mod = evaluate.load_proposer(path)
            rows = self.rows([("a", "X"), ("a", "")])
            rows[0]["verts"] = "3"; rows[1]["verts"] = "300"
            preds = evaluate.leave_one_model_out("delete", rows, [{"model": "m", "category": "c"}], mod.propose)
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


class ReviewRound2(unittest.TestCase):
    """The shapes an independent review of the export found unhandled: line meshes counted as islands, the tool's own
    tiny-part deletions scored as the user's, a node cycle killing the run, sidecar lines the Workshop skips, a part
    in two fuse groups, a recipe without parts."""

    def write_folder(self, folder_name, glb_bytes, sidecars=None, recipe=None, report=None):
        tmp = tempfile.mkdtemp(prefix="advisor-r2-")
        self.addCleanup(shutil.rmtree, tmp, True)
        models, recipes, out = (os.path.join(tmp, d) for d in ("models", "recipes", "out"))
        folder = os.path.join(models, folder_name)
        os.makedirs(folder)
        os.makedirs(recipes)
        glb = os.path.join(folder, "m.glb")
        with open(glb, "wb") as f:
            f.write(glb_bytes)
        for suffix, text in (sidecars or {}).items():
            with io.open(glb + suffix, "w", encoding="utf-8", newline="") as f:
                f.write(text)
        if recipe is not None:
            recipe = dict(recipe, srcFile=glb.replace("\\", "/"))
            with io.open(os.path.join(recipes, "m.json"), "w", encoding="utf-8") as f:
                json.dump(recipe, f)
        if report is not None:
            fused = os.path.join(folder, "m_fused.glb")
            with open(fused, "wb") as f:
                f.write(glb_bytes)
            with io.open(fused + ".fuse-report.txt", "w", encoding="utf-8") as f:
                f.write(report % (glb.replace("\\", "/"), fused.replace("\\", "/")))
        parts, models_rows, rep = corpus.build(models, recipes, out, use_cache=False, log=lambda *_: None)
        return parts, models_rows, rep, out

    def test_a_line_mesh_is_blocked_not_a_pile_of_islands_and_is_the_tools_own_deletion(self):
        glb = make_glb(nodes=[{"name": "Rope", "mesh": 0}, {"name": "Hull", "mesh": 1}], meshes=[[(0, 0)], [(0, 1)]],
                       positions=[TWO_ISLANDS], index_lists=[[0, 1, 1, 2, 3, 4], [0, 1, 2, 3, 4, 5]], modes={0: 1})   # mesh 0 is LINES
        g = corpus.Glb(glb)
        self.assertEqual([m["primitives"][0]["mode"] for m in g.meshes], [1, 4])
        parts, _, rep, out = self.write_folder("Rah66", glb, sidecars={".marks.txt": "#fuse-groups v2\nX|0|Rope\nX|1|Hull\n"})
        rope = [r for r in parts if r["name"] == "Rope"][0]
        hull = [r for r in parts if r["name"] == "Hull"][0]
        self.assertEqual((rope["blocked"], rope["tris"], rope["islands"], rope["triangle_free"], rope["verts"]), ("Only TRIANGLES primitives can be split safely.", 0, 0, 1, 6))
        self.assertEqual((rope["mark"], rope["auto_mark"]), ("X", 1))     # the Workshop marks a triangle-free part itself
        self.assertEqual((hull["mark"], hull["auto_mark"], hull["islands"]), ("X", 1, 2))   # two triangles: also the tool's
        parts2, _ = evaluate.load_corpus(out)
        self.assertEqual([r["name"] for r in evaluate.population("split", parts2)], ["Hull"])    # a blocked part is no split candidate
        self.assertEqual(len(evaluate.population("delete", parts2)), 2)
        self.assertEqual(evaluate.population("delete-hand", parts2), [])                        # both deletions are the tool's

    def test_a_blocked_mesh_has_no_box(self):
        # its vertices are not read, so its size is not known: empty cells, not the nan and -inf a translated node's
        # empty loop once left (which every size threshold was "above", so the delete proposer proposed every such part)
        glb = make_glb(nodes=[{"name": "Rope", "mesh": 0, "translation": [0.0, 0.0, 100.0]}, {"name": "Flat", "mesh": 0}, {"name": "Hull", "mesh": 1}],
                       meshes=[[(0, 0)], [(0, 1)]], positions=[TWO_ISLANDS], index_lists=[[0, 1, 1, 2, 3, 4], [0, 1, 2, 3, 4, 5]], modes={0: 1})
        parts, _, _, out = self.write_folder("Rah66", glb, sidecars={".marks.txt": "#fuse-groups v2\nX|2|Hull\n"})
        for name in ("Rope", "Flat"):
            r = [p for p in parts if p["name"] == name][0]
            self.assertEqual([r[c] for c in ("cx", "cy", "cz", "sx", "sy", "sz")], [""] * 6, name)
        hull = [p for p in parts if p["name"] == "Hull"][0]
        self.assertEqual(hull["sx"], 11.0)
        parts2, _ = evaluate.load_corpus(out)
        self.assertEqual([r["sx"] for r in parts2 if r["name"] == "Rope"], [""])

    def test_a_three_triangle_part_marked_x_is_the_users_judgement(self):
        glb = make_glb(nodes=[{"name": "Plate", "mesh": 0}], meshes=[[(0, 0)]], positions=[TWO_ISLANDS + [(20, 0, 0)]], index_lists=[[0, 1, 2, 3, 4, 5, 3, 4, 6]])
        parts, _, _, out = self.write_folder("Rah66", glb, sidecars={".marks.txt": "#fuse-groups v2\nX|0|Plate\n"})
        self.assertEqual((parts[0]["tris"], parts[0]["mark"], parts[0]["auto_mark"]), (3, "X", 0))
        parts2, _ = evaluate.load_corpus(out)
        self.assertEqual([r["truth"] for r in evaluate.population("delete-hand", parts2)], ["X"])

    def test_a_node_cycle_is_one_report_line_not_a_dead_run(self):
        glb = make_glb(nodes=[{"name": "A", "mesh": 0, "children": [1]}, {"name": "B", "mesh": 0, "children": [0], "_child": True}],
                       meshes=[[(0, 0)]], positions=[ONE_TRI], index_lists=[[0, 1, 2]])
        with self.assertRaises(ValueError):
            corpus.glb_parts(glb)
        parts, _, rep, _ = self.write_folder("Rah66", glb, sidecars={".marks.txt": "#fuse-groups v2\nX|0|A\n"})
        self.assertEqual(parts, [])
        self.assertTrue(any("GLB unreadable" in line and "node cycle" in line for line in rep), rep)
        # a matrix of the wrong length is the same kind of line, not a crash
        bad = make_glb(nodes=[{"name": "A", "mesh": 0, "matrix": [1.0, 2.0, 3.0]}], meshes=[[(0, 0)]], positions=[ONE_TRI], index_lists=[[0, 1, 2]])
        parts, _, rep, _ = self.write_folder("Rah66", bad, sidecars={".marks.txt": "#fuse-groups v2\nX|0|A\n"})
        self.assertEqual(parts, [])
        self.assertTrue(any("GLB unreadable" in line for line in rep), rep)

    def test_sidecar_lines_the_workshop_skips_are_skipped_here_and_named(self):
        tmp = tempfile.mkdtemp(prefix="advisor-sc2-")
        self.addCleanup(shutil.rmtree, tmp, True)
        p = os.path.join(tmp, "x.glb.marks.txt")
        with io.open(p, "w", encoding="utf-8-sig", newline="") as f:   # a BOM, CRLF, spaces around the fields
            f.write("#fuse-groups v2\r\nX| 1 | Mast \r\nX|Deck\r\nx|2|Keel\r\nXX|3|Keel\r\nS|4|\r\nA|--3|Rail\r\nA|²|Rail\r\nB|+5|Boom\r\nC|\r\n")
        skipped = []
        lines, _ = corpus.read_sidecar(p, skipped)
        # "+5" is an index to int.TryParse and so here; "--3" and a superscript digit are not (str.isdigit said yes and int() threw)
        self.assertEqual(lines, [("X", 1, "Mast"), ("B", 5, "Boom")])
        self.assertEqual(len(skipped), 7, skipped)

    def test_a_part_in_two_fuse_groups_is_reported(self):
        glb = make_glb(nodes=[{"name": "A", "mesh": 0}, {"name": "B", "mesh": 0}], meshes=[[(0, 0)]], positions=[ONE_TRI], index_lists=[[0, 1, 2]])
        report = ("Model Workshop fuse report\nsource: %s\noutput: %s\nweld: 0 permille of the model's length\ngroups: 2\n\n"
                  "== group H — 2 part(s)\nparts: A, B\nFused 2 part(s) -> 'Fused_H_A': 6 -> 6 verts (seams welded within 0.001 = 0‰ of 11); islands 2 -> 2\n\n"
                  "== group D — 1 part(s)\nparts: A\nFused 1 part(s) -> 'Fused_D_A': 3 -> 3 verts (seams welded within 0.001 = 0‰ of 11); islands 1 -> 1\n")
        parts, _, rep, _ = self.write_folder("Rah66", glb, report=report)
        self.assertTrue(any("names 'A' in two groups" in line for line in rep), rep)
        a = [r for r in parts if r["name"] == "A" and r["stage"] == "raw"][0]
        self.assertEqual((a["fused_into"], a["fused_letter"]), ("Fused_D_A", "D"))   # the later group, as the message says
        self.assertEqual([r for r in parts if r["name"] == "B" and r["stage"] == "raw"][0]["fused_into"], "Fused_H_A")

    def test_a_recipe_without_a_parts_list_is_named_in_the_report(self):
        glb = make_glb(nodes=[{"name": "A", "mesh": 0}], meshes=[[(0, 0)]], positions=[ONE_TRI], index_lists=[[0, 1, 2]])
        for recipe in ({"bodyReducePct": 10.0}, {"bodyReducePct": 10.0, "parts": "abc"}):   # no key; a non-list (the old loop crashed on it)
            _, models_rows, rep, _ = self.write_folder("Rah66", glb, recipe=recipe)
            self.assertTrue(any("m.json: no parts list" in line for line in rep), rep)
            self.assertEqual([m["recipe"] for m in models_rows if m["model"] == "Rah66"], [""])


def dash(x0, x1, y, z=0.0, w=0.02):
    """A thin triangle pair along X from x0 to x1 at height y: an elongated island."""
    return [(x0, y, z), (x1, y, z), (x0, y + w, z), (x1, y + w, z)]


def tri_mesh(positions_lists):
    """One mesh of one primitive over several quads (two triangles each), all as one position accessor."""
    pts, idx = [], []
    for quad in positions_lists:
        base = len(pts)
        pts.extend(quad)
        idx.extend([base, base + 1, base + 2, base + 1, base + 3, base + 2])
    return make_glb(nodes=[{"name": "P", "mesh": 0}], meshes=[[(0, 0)]], positions=[pts], index_lists=[idx])


class DistanceMerge(unittest.TestCase):
    """The Workshop's 1 % distance merge, ported: the island count the user reads in the Cutter's row."""

    def features(self, glb):
        return corpus.glb_parts(glb)[0]

    def test_two_islands_within_one_percent_of_the_diagonal_merge_and_far_ones_do_not(self):
        # the part spans 100 units: eps = 1 % of its diagonal ~ 1.0; a gap of 0.5 merges, a gap of 5 does not
        near = tri_mesh([dash(0, 50, 0), dash(50.5, 100, 0)])
        far = tri_mesh([[(0, 0, 0), (40, 0, 0), (0, 40, 0), (40, 40, 0)], [(60, 60, 0), (100, 60, 0), (60, 100, 0), (100, 100, 0)]])
        f = self.features(near)
        self.assertEqual((f["islands"], f["islands_merged"]), (2, 1))
        f = self.features(far)
        self.assertEqual((f["islands"], f["islands_merged"], f["island_top_pct"], f["islands_big"]), (2, 2, 50.0, 2))

    def test_two_elongated_islands_chain_only_when_colinear(self):
        # two dashes on one line, 0.5 apart: one rope. The same two dashes side by side 0.5 apart: two separate lines
        # (both elongated, parallel, but the perpendicular offset exceeds 0.35 * eps).
        colinear = tri_mesh([dash(0, 50, 0), dash(50.5, 100, 0)])
        parallel = tri_mesh([dash(0, 100, 0), dash(0, 100, 0.5)])
        self.assertEqual(self.features(colinear)["islands_merged"], 1)
        f = self.features(parallel)
        self.assertEqual((f["islands"], f["islands_merged"]), (2, 2))
        # a blob (not elongated) with a VERTEX within eps of a dash vertex merges on distance alone; the merge measures
        # vertex to vertex, as the Workshop does, so a blob hovering over the dash's middle (no vertex there) stays apart
        blob = tri_mesh([dash(0, 100, 0), [(100, 0.5, 0), (120, 0.5, 0), (100, 20, 0), (120, 20, 0)]])
        self.assertEqual(self.features(blob)["islands_merged"], 1)
        hover = tri_mesh([dash(0, 100, 0), [(40, 0.5, 0), (60, 0.5, 0), (40, 20, 0), (60, 20, 0)]])
        self.assertEqual(self.features(hover)["islands_merged"], 2)

    def test_a_single_island_part_skips_the_merge(self):
        f = self.features(tri_mesh([dash(0, 100, 0)]))
        self.assertEqual((f["islands"], f["islands_merged"], f["island_top_pct"], f["island_second_pct"], f["islands_big"]), (1, 1, 100.0, 0.0, 1))

    def test_principal_axis_aspect_of_a_dash_and_of_a_square(self):
        _, aspect, _ = corpus.principal_axis(dash(0, 100, 0), [0, 0, 0], [100, 0.02, 0])
        self.assertGreater(aspect, 3)
        _, aspect, _ = corpus.principal_axis([(0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0)], [0, 0, 0], [1, 1, 0])
        self.assertLess(aspect, 1.5)


class SplitProposer(unittest.TestCase):
    """tools/advisor/proposers/split.py: thresholds learned on the training rows, applied to feature-only test rows."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools", "advisor", "proposers"))
        import split  # noqa: E402
        cls.split = split

    @staticmethod
    def row(model, i, tris, merged, big, truth="", blocked=""):
        return {"model": model, "file": model + "/m.glb", "node_index": i, "name": "n%d" % i, "stage": "raw", "tris": str(tris),
                "islands_merged": str(merged), "islands_big": str(big), "blocked": blocked, "truth": truth}

    def test_the_rule_is_learned_from_the_training_rows_and_applied_without_truth(self):
        train = []
        for i in range(20):   # splits: big, many islands; the rest: small or single-island
            train.append(self.row("a", i, 5000 + i, 6, 4, "S"))
            train.append(self.row("a", 100 + i, 50 + i, 6, 4, ""))
            train.append(self.row("a", 200 + i, 9000, 1, 1, ""))
        k, t, b = self.split.learn(train)
        self.assertGreaterEqual(k, 2)
        self.assertGreater(t, 50 + 19)        # the threshold separates the big splits from the small non-splits
        self.assertLessEqual(t, 5000)
        test = [{k_: v for k_, v in r.items() if k_ != "truth"} for r in
                (self.row("b", 1, 7000, 5, 3), self.row("b", 2, 60, 5, 3), self.row("b", 3, 7000, 1, 1), self.row("b", 4, 7000, 5, 3, blocked="Only TRIANGLES"))]
        preds = self.split.propose("split", test, train, {"model": "b", "category": "ship", "files": ""}, [])
        self.assertEqual(preds, {("b/m.glb", 1): "S", ("b/m.glb", 2): "", ("b/m.glb", 3): "", ("b/m.glb", 4): ""})
        self.assertEqual(self.split.LAST_RULE["model"], "b")
        self.assertEqual(self.split.propose("delete", test, train, {}, []), {})   # nothing to say about other tasks

    def test_every_corpus_column_is_a_feature_or_a_label(self):
        # the held-out allowlist and the label set partition the export's columns: a new column lands in one of them or
        # this fails, so no feature is silently stripped from a proposer's rows again
        columns = set(corpus.PART_COLUMNS)
        self.assertEqual(evaluate.PART_FEATURES | evaluate.PART_LABELS, columns, columns - evaluate.PART_FEATURES - evaluate.PART_LABELS)
        self.assertEqual(evaluate.PART_FEATURES & evaluate.PART_LABELS, frozenset())
        for c in self.split.NEEDED:
            self.assertIn(c, evaluate.PART_FEATURES)

    def test_rows_without_the_feature_columns_fail_loudly(self):
        with self.assertRaises(ValueError):
            self.split.propose("split", [{"file": "f", "node_index": 1, "tris": "9"}], [], {}, [])

    def test_f_beta(self):
        self.assertAlmostEqual(self.split.f_beta(1, 0, 1), 0.6667, places=3)   # F1: precision 1, recall 0.5
        self.assertAlmostEqual(self.split.f_beta(1, 1, 0), 0.6667, places=3)   # F1: precision 0.5, recall 1 - the same
        self.assertAlmostEqual(self.split.f_beta(1, 0, 1, beta=0.5), 0.8333, places=3)   # F0.5 weighs precision double
        self.assertAlmostEqual(self.split.f_beta(1, 1, 0, beta=0.5), 0.5556, places=3)
        self.assertEqual(self.split.f_beta(0, 5, 5), 0.0)

    def test_no_supported_training_split_abstains_instead_of_choosing_the_loosest_rule(self):
        test = [self.row("b", 1, 9000, 8, 4)]
        for train in ([], [self.row("a", 1, 9000, 8, 4)],
                      [self.row("a", 1, 9000, 1, 1, "S")]):
            with self.subTest(train=train):
                self.assertIsNone(self.split.learn(train))
                self.assertEqual(self.split.propose("split", test, train, {"model": "b"}, []), {})

    def test_write_refreshes_empty_and_blocked_files_without_touching_live_marks(self):
        tmp = tempfile.TemporaryDirectory(prefix="advisor-proposals-")
        self.addCleanup(tmp.cleanup)
        root = tmp.name
        os.makedirs(os.path.join(root, "b"))
        train = [self.row("a", 1, 5000, 6, 4, "S"), self.row("a", 2, 60, 6, 4)]
        test = [self.row("b", 1, 7000, 6, 4), self.row("b", 2, 60, 6, 4),
                self.row("b", 3, 7000, 6, 4, blocked="Only TRIANGLES")]
        test[1]["file"] = "b/small.glb"
        test[2]["file"] = "b/blocked.glb"
        live = os.path.join(root, "b/m.glb.marks.txt")
        with io.open(live, "w", encoding="utf-8") as f:
            f.write("#fuse-groups v2\nX|1|n1\n")
        for row in test:
            with io.open(os.path.join(root, row["file"]) + ".marks.proposed.txt", "w", encoding="utf-8") as f:
                f.write("#fuse-groups v2\nS|99|stale\n")

        def run():
            rows = []
            for row in train + test:
                record = dict.fromkeys(corpus.PART_COLUMNS, "")
                record.update({k: v for k, v in row.items() if k != "truth"})
                record["mark"] = row["truth"]
                rows.append(record)
            corpus.write_csv(os.path.join(root, "parts.csv"), corpus.PART_COLUMNS, rows)
            corpus.write_csv(os.path.join(root, "models.csv"), corpus.MODEL_COLUMNS, [])
            with redirect_stdout(io.StringIO()):
                self.assertEqual(self.split.main(["--corpus", root, "--model", "b", "--models-root", root, "--write"]), 0)

        run()
        for row in test:
            lines, _ = corpus.read_sidecar(os.path.join(root, row["file"]) + ".marks.proposed.txt")
            self.assertEqual(lines, [("S", 1, "n1")] if row is test[0] else [])
        # Rerun with no training positives: an old positive file must become empty too.
        train[0]["truth"] = "X"
        run()
        for row in test:
            self.assertEqual(corpus.read_sidecar(os.path.join(root, row["file"]) + ".marks.proposed.txt")[0], [])
        # A model with only blocked parts still refreshes all its files, rather than returning early.
        for row in test:
            row["blocked"] = "Only TRIANGLES"
        run()
        with io.open(live, encoding="utf-8") as f:
            self.assertEqual(f.read(), "#fuse-groups v2\nX|1|n1\n")


class Rules(unittest.TestCase):
    def test_invalid_v2_indices_cannot_attach_a_mark_by_name(self):
        tmp = tempfile.TemporaryDirectory(prefix="advisor-index-")
        self.addCleanup(tmp.cleanup)
        path = os.path.join(tmp.name, "marks.txt")
        invalid = ("٢", "2147483648", "9" * 5000)
        with io.open(path, "w", encoding="utf-8") as f:
            f.write("#fuse-groups v2\n" + "".join("X|%s|Hull\n" % index for index in invalid))
        skipped = []
        lines, _ = corpus.read_sidecar(path, skipped)
        self.assertEqual(lines, [])
        self.assertEqual(len(skipped), len(invalid))
        self.assertEqual(corpus.resolve_sidecar(lines, [{"node_index": 0, "name": "Hull", "file_name": "Hull"}], []), {})

    def test_legacy_numeric_name_suffix_is_resolved_or_refused_without_guessing(self):
        tmp = tempfile.TemporaryDirectory(prefix="advisor-legacy-")
        self.addCleanup(tmp.cleanup)
        path = os.path.join(tmp.name, "marks.txt")
        with io.open(path, "w", encoding="utf-8") as f:
            f.write("X|Hull|3\n")
        indexed = {"node_index": 3, "name": "Hull", "file_name": "Hull"}
        literal = {"node_index": 4, "name": "Hull|3", "file_name": "Hull|3"}
        for rows, expected in (([indexed, literal], {}), ([literal], {4: "X"}), ([indexed], {3: "X"})):
            with self.subTest(rows=rows):
                lines, _ = corpus.read_sidecar(path)
                refused = []
                self.assertEqual(corpus.resolve_sidecar(lines, rows, refused), expected)
                self.assertEqual(bool(refused), not bool(expected))

    def test_cache_notices_same_size_changes_within_a_second(self):
        tmp = tempfile.TemporaryDirectory(prefix="advisor-cache-")
        self.addCleanup(tmp.cleanup)
        path = os.path.join(tmp.name, "mesh.glb")
        def data(x):
            return make_glb(nodes=[{"name": "Hull", "mesh": 0, "translation": [x, 0.0, 0.0]}],
                            meshes=[[(0, 0)]], positions=[ONE_TRI], index_lists=[[0, 1, 2]])
        first, second = data(0.0), data(1.0)
        self.assertEqual(len(first), len(second))
        cache = {}
        with open(path, "wb") as f: f.write(first)
        os.utime(path, ns=(1700000000100000000, 1700000000100000000))
        before, _ = corpus.file_features(path, cache, True, lambda *_: None)
        with open(path, "wb") as f: f.write(second)
        os.utime(path, ns=(1700000000200000000, 1700000000200000000))
        after, cached = corpus.file_features(path, cache, True, lambda *_: None)
        self.assertFalse(cached)
        self.assertAlmostEqual(after[0]["cx"] - before[0]["cx"], 1.0)

    def test_sidecar_indices_require_ascii_int32_tokens(self):
        for text in ("٢", "１２", "2147483648", "-2147483649", "9" * 5000):
            with self.subTest(text=text[:30]):
                self.assertIsNone(corpus.int_token(text))
        for text, expected in (("+2147483647", 2147483647), ("-2147483648", -2147483648),
                               ("0" * 5000 + "2", 2)):
            with self.subTest(text=text[:30]):
                self.assertEqual(corpus.int_token(text), expected)

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
            skipped = []
            lines, names = corpus.read_sidecar(p, skipped)
            self.assertEqual(lines, [("A", 3, "Odd|Name")])   # the name keeps its own bar; a v2 line without an index is skipped, as the Workshop skips it
            self.assertEqual(len(skipped), 1, skipped)
            self.assertEqual(names, {"A": "Hull"})
            with io.open(p, "w", encoding="utf-8") as f:
                f.write("A|Hull|3\nB|Deck\n")
            lines, _ = corpus.read_sidecar(p)
            refused = []
            legacy_rows = [{"node_index": 3, "name": "Hull", "file_name": "Hull"},
                           {"node_index": 4, "name": "Deck", "file_name": "Deck"}]
            self.assertEqual(corpus.resolve_sidecar(lines, legacy_rows, refused), {3: "A", 4: "B"})
            self.assertEqual(refused, [])
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_resolution_prefers_the_index_when_its_node_still_carries_the_name(self):
        rows = [{"node_index": 0, "name": "Deck", "file_name": "Deck"}, {"node_index": 1, "name": "Deck_2", "file_name": "Deck"},
                {"node_index": 2, "name": "Mast", "file_name": "Mast"}]
        refused = []
        out = corpus.resolve_sidecar([("A", 1, "Deck"), ("B", 7, "Mast"), ("C", None, "Deck"), ("D", 5, "Ghost")], rows, refused)
        self.assertEqual(out, {1: "A", 2: "B"})   # 'Deck' at index 1 is the file's name there; Mast by its unique name
        self.assertEqual(len(refused), 2, refused)   # 'Deck' with no index names two parts; Ghost names none


class ProbeTableAndLaterMarks(unittest.TestCase):
    """The probe's verdicts joined by node (tools/advisor/probe_vis.sh's table, corpus.py --probe) and the deletion a
    part received at a later stage file carried back to the earlier row (mark_later)."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="advisor-probe-")
        cls.models = os.path.join(cls.tmp, "models")
        cls.recipes = os.path.join(cls.tmp, "recipes")
        cls.out = os.path.join(cls.tmp, "out")
        folder = os.path.join(cls.models, "Espana")
        os.makedirs(folder)
        os.makedirs(cls.recipes)
        # RAW: Hull (two islands, marked S), Gear and Rivet (one triangle each), Lamp (the six-vertex mesh)
        raw = make_glb(nodes=[{"name": "Hull", "mesh": 0}, {"name": "Gear", "mesh": 1}, {"name": "Rivet", "mesh": 1}, {"name": "Lamp", "mesh": 0}],
                       meshes=[[(0, 0)], [(1, 1)]], positions=[TWO_ISLANDS, ONE_TRI], index_lists=[[0, 1, 2, 3, 4, 5], [0, 1, 2]])
        cls.raw = os.path.join(folder, "ship.glb")
        with open(cls.raw, "wb") as f:
            f.write(raw)
        with io.open(cls.raw + ".marks.txt", "w", encoding="utf-8") as f:
            f.write("#fuse-groups v2\nS|0|Hull\n")
        # SPLIT: the hull's two children, Gear and Rivet unchanged, Lamp now a three-vertex part (not the same part); Gear and Lamp marked X
        split = make_glb(nodes=[{"name": "Hull_Part_001", "mesh": 0}, {"name": "Hull_Part_002", "mesh": 1}, {"name": "Gear", "mesh": 2}, {"name": "Rivet", "mesh": 2}, {"name": "Lamp", "mesh": 2}],
                         meshes=[[(0, 0)], [(0, 1)], [(1, 2)]], positions=[TWO_ISLANDS, ONE_TRI], index_lists=[[0, 1, 2], [3, 4, 5], [0, 1, 2]])
        cls.split = os.path.join(folder, "ship_split.glb")
        with open(cls.split, "wb") as f:
            f.write(split)
        with io.open(cls.split + ".marks.txt", "w", encoding="utf-8") as f:
            f.write("#fuse-groups v2\nX|2|Gear\nX|4|Lamp\n")
        # a recipe on the raw file: Gear's verdict agrees with the probe, Rivet's differs, Lamp's is the Lab's -1 (unclassified)
        recipe = {"srcFile": cls.raw.replace("\\", "/"), "parts": [
            {"name": "Gear", "verts": 3, "role": 3, "vis": 0, "flip": 0}, {"name": "Rivet", "verts": 3, "role": 0, "vis": 0, "flip": 0},
            {"name": "Lamp", "verts": 6, "role": 0, "vis": -1, "flip": 0}]}
        with io.open(os.path.join(cls.recipes, "ship.json"), "w", encoding="utf-8") as f:
            json.dump(recipe, f)
        # the probe's table: the raw file as it is on disk (the hull in two loose parts, one of them escaping), the split file STALE
        st = os.stat(cls.raw)
        ticks = st.st_mtime_ns // 100 + corpus.WINDOWS_EPOCH_TICKS
        raw_key = cls.raw.replace("\\", "/")
        cls.probe = os.path.join(cls.tmp, "probe.tsv")
        with io.open(cls.probe, "w", encoding="utf-8") as f:
            f.write("FILE\t%s\t%d\t%d\tparts=5\tsplit=0\tms=3\n" % (raw_key, st.st_size, ticks))
            f.write("PART\t%s\t0\tHull\t3\t0\t1\t\n" % raw_key)
            f.write("PART\t%s\t0\tHull.001\t3\t1\t2\tspine\n" % raw_key)
            f.write("PART\t%s\t1\tGear\t3\t0\t0\t\n" % raw_key)
            f.write("PART\t%s\t2\tRivet\t3\t1\t0\t\n" % raw_key)
            f.write("PART\t%s\t3\tLamp\t6\t0\t0\t\n" % raw_key)
            f.write("FILE\t%s\t1\t1\tparts=5\tsplit=0\tms=3\n" % cls.split.replace("\\", "/"))
            f.write("PART\t%s\t2\tGear\t3\t0\t0\t\n" % cls.split.replace("\\", "/"))
            f.write("FAIL\t%s\tInvalidDataException: no chunk\n" % os.path.join(folder, "broken.glb").replace("\\", "/"))
        cls.parts, cls.models_rows, cls.report = corpus.build(cls.models, cls.recipes, cls.out, use_cache=False, log=lambda *_: None, probe_path=cls.probe)
        cls.by = {(r["file"], r["name"]): r for r in cls.parts}

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def test_the_table_is_read_with_its_file_identity_and_failures(self):
        report = []
        table = corpus.read_probe(self.probe, report)
        entry = table[corpus.norm_path(self.raw)]
        self.assertEqual(len(entry["nodes"][0]), 2)   # the hull's two loose parts share node 0
        self.assertEqual(entry["nodes"][0][1], (1, 2, "spine", 3, "Hull.001"))
        self.assertTrue(corpus.probe_current(entry, self.raw))
        self.assertFalse(corpus.probe_current(dict(entry, ticks=entry["ticks"] + 1), self.raw))   # rewritten at the same size
        self.assertFalse(corpus.probe_current(dict(entry, size=entry["size"] + 1), self.raw))
        self.assertFalse(corpus.probe_current(table[corpus.norm_path(self.split)], self.split))
        self.assertTrue(any(l.startswith("PROBE FAIL") and "broken.glb" in l for l in report), report)

    def test_verdicts_land_per_node_and_a_nodes_loose_parts_fold(self):
        hull = self.by[("Espana/ship.glb", "Hull")]
        self.assertEqual((hull["vis"], hull["flip"], hull["bone"], hull["vis_from"]), (1, 3, "spine", "probe"))   # any part escaping = external; flips summed
        gear = self.by[("Espana/ship.glb", "Gear")]
        self.assertEqual((gear["vis"], gear["vis_from"]), (0, "probe"))

    def test_a_stale_file_falls_back_and_is_reported(self):
        gear = self.by[("Espana/ship_split.glb", "Gear")]
        self.assertEqual((gear["vis"], gear["vis_from"]), ("", ""))   # no recipe on the split file either
        self.assertTrue(any(l.startswith("PROBE stale: Espana/ship_split.glb") for l in self.report), self.report)

    def test_the_recipes_verdict_checks_the_join_and_does_not_overwrite_the_probes(self):
        rivet = self.by[("Espana/ship.glb", "Rivet")]
        self.assertEqual((rivet["vis"], rivet["vis_from"], rivet["recipe"]), (1, "probe", "ship.json"))   # the probe's 1 stands over the recipe's 0
        self.assertTrue(any(l.startswith("PROBE differs at Espana/ship.glb 'Rivet'") for l in self.report), self.report)
        summary = [l for l in self.report if l.startswith("PROBE summary")][0]
        self.assertIn("recipe vis agree 1, recipe vis differ 1, recipe vis unclassified 1", summary)

    def test_a_deletion_at_the_split_file_is_carried_back_to_the_same_part_at_the_raw_file(self):
        self.assertEqual(self.by[("Espana/ship.glb", "Gear")]["mark_later"], "X")   # same name, same vertex count, X later
        self.assertEqual(self.by[("Espana/ship.glb", "Rivet")]["mark_later"], "")   # not deleted later
        self.assertEqual(self.by[("Espana/ship.glb", "Lamp")]["mark_later"], "")    # the later Lamp has 3 vertices, this one 6: another part
        self.assertEqual(self.by[("Espana/ship.glb", "Hull")]["mark_later"], "")    # split here; its children are other nodes
        self.assertEqual(self.by[("Espana/ship_split.glb", "Gear")]["mark_later"], "")   # the later file itself carries the mark, not mark_later
        self.assertTrue(any(l == "LATER Espana/ship.glb: 1 unmarked part(s) carry the X a later stage file gave them (mark_later)" for l in self.report), self.report)

    def test_the_delete_population_counts_the_carried_deletion(self):
        truth = {(r["file"], r["name"]): r["truth"] for r in evaluate.population("delete", self.parts)}
        self.assertEqual(truth[("Espana/ship.glb", "Gear")], "X")
        self.assertEqual(truth[("Espana/ship.glb", "Rivet")], "")
        self.assertEqual(truth[("Espana/ship_split.glb", "Lamp")], "X")

    def test_list_names_the_files_the_corpus_reads(self):
        files = corpus.list_files(self.models, self.recipes)
        self.assertEqual(sorted(os.path.basename(f) for f in files), ["ship.glb", "ship_split.glb"])

    def test_the_csv_round_trips_the_new_columns(self):
        parts, _ = evaluate.load_corpus(self.out)
        row = [r for r in parts if r["file"] == "Espana/ship.glb" and r["name"] == "Gear"][0]
        self.assertEqual((row["vis"], row["vis_from"], row["mark_later"]), ("0", "probe", "X"))

    def test_a_repeated_file_line_and_a_probe_node_the_file_lacks_are_reported(self):
        # a table appended to twice: the later run's rows replace the earlier, and the report says so; a probe node the
        # file's rows do not have means the table was made from another file - not silent either
        raw_key = self.raw.replace("\\", "/")
        st = os.stat(self.raw)
        table = os.path.join(self.tmp, "probe2.tsv")
        with io.open(table, "w", encoding="utf-8") as f:
            f.write("FILE\t%s\t%d\t%d\n" % (raw_key, st.st_size, st.st_mtime_ns // 100 + corpus.WINDOWS_EPOCH_TICKS))
            f.write("PART\t%s\t0\tHull\t3\t0\t1\t\n" % raw_key)
            f.write("FILE\t%s\t%d\t%d\n" % (raw_key, st.st_size, st.st_mtime_ns // 100 + corpus.WINDOWS_EPOCH_TICKS))
            f.write("PART\t%s\t99\tGhost\t3\t1\t0\t\n" % raw_key)
        report = []
        entry = corpus.read_probe(table, report)[corpus.norm_path(self.raw)]
        self.assertEqual(sorted(entry["nodes"]), [99])
        self.assertTrue(any(l.startswith("PROBE table: FILE line repeated for") for l in report), report)
        rows = [{"node_index": 0, "vis": "", "flip": "", "bone": "", "vis_from": ""}]
        corpus.apply_probe(rows, entry, "Espana/ship.glb", report)
        self.assertEqual(rows[0]["vis_from"], "")
        self.assertTrue(any(l.startswith("PROBE Espana/ship.glb: 1 probe node(s) the file's rows do not have (99)") for l in report), report)
        self.assertTrue(any(l.startswith("PROBE Espana/ship.glb: 1 node(s) without a probe row") for l in report), report)

    def test_an_empty_table_still_writes_the_summary(self):
        empty = os.path.join(self.tmp, "empty.tsv")
        io.open(empty, "w").close()
        _, _, report = corpus.build(self.models, self.recipes, os.path.join(self.tmp, "out-empty"), use_cache=False, log=lambda *_: None, probe_path=empty)
        self.assertIn("PROBE summary: files probed 0, files stale 0, files not in the table 2, recipe vis agree 0, recipe vis differ 0, recipe vis unclassified 0", report)

    def test_a_relative_models_root_lists_and_joins_the_same_absolute_paths(self):
        # --list prints absolute paths, the probe table keys them, --probe must find them again whatever the user typed
        cwd = os.getcwd()
        self.addCleanup(os.chdir, cwd)
        os.chdir(self.tmp)
        out = io.StringIO()
        with redirect_stdout(out):
            self.assertEqual(corpus.main(["--list", "--models", "models", "--recipes", "recipes"]), 0)
        self.assertEqual(sorted(out.getvalue().split()), sorted(p.replace("\\", "/") for p in (self.raw, self.split)))
        _, _, report = corpus.build("models", "recipes", os.path.join(self.tmp, "out-rel"), use_cache=False, log=lambda *_: None, probe_path=self.probe)
        self.assertTrue(any(l.startswith("PROBE summary: files probed 1, files stale 1, files not in the table 0") for l in report), report)


class LaterMarkRules(unittest.TestCase):
    """corpus.carry_later_marks: which later file counts, which part is the same part, and what a conflict is."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="advisor-later-")
        cls.models = os.path.join(cls.tmp, "models")
        cls.recipes = os.path.join(cls.tmp, "recipes")
        os.makedirs(os.path.join(cls.models, "Espana"))
        os.makedirs(os.path.join(cls.models, "Other"))
        os.makedirs(cls.recipes)
        NAMES = ["Gear", "Rivet", None, "Lamp", "Mast", "Deck", "Keel"]   # node 2 unnamed; every part one triangle, so only the name tells them apart

        def glb(names, marks):
            nodes = [({"name": n, "mesh": 0} if n else {"mesh": 0}) for n in names]
            return make_glb(nodes=nodes, meshes=[[(0, 0)]], positions=[ONE_TRI], index_lists=[[0, 1, 2]]), marks

        files = {
            # the raw file, with a Flag no later file lists; its sidecar's one line names a node the file lacks (a stale sidecar: no mark resolves)
            "Espana/HMS.glb": glb(NAMES + ["Flag"], "S|9|Ghost\n"),
            # the cut file marks Gear X and splits the Deck
            "Espana/hms_cut.glb": glb(NAMES, "X|0|Gear\nS|5|Deck\n"),
            # the split file lists Gear unmarked again (a conflict with the cut's X), marks Rivet, the unnamed node and the Deck X
            "Espana/hms_cut_split.glb": glb(NAMES, "X|1|Rivet\nX|2|node 2\nX|5|Deck\n"),
            # a second export of the split stage: marks the Mast X, lists Rivet and Deck unmarked (conflicts at the same stage)
            "Espana/hms_cut_split2.glb": glb(NAMES, "X|4|Mast\n"),
            # another model whose stem extends this one's with something that is not a stage token: its marks do not carry
            "Espana/hms_v2_split.glb": glb(NAMES, "X|3|Lamp\n"),
            # the same chain name in another folder: another model
            "Other/hms_cut_split.glb": glb(NAMES, "X|6|Keel\n"),
        }
        for name, (data, marks) in files.items():
            path = os.path.join(cls.models, name)
            with open(path, "wb") as f:
                f.write(data)
            with io.open(path + ".marks.txt", "w", encoding="utf-8") as f:
                f.write("#fuse-groups v2\n" + marks)
        cls.parts, _, cls.report = corpus.build(cls.models, cls.recipes, os.path.join(cls.tmp, "out"), use_cache=False, log=lambda *_: None)
        cls.by = {(r["file"], r["name"]): r for r in cls.parts}

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def later(self, file, name):
        return self.by[(file, name)]["mark_later"]

    def test_only_stage_tokens_extend_a_stem_and_the_case_does_not_matter(self):
        self.assertEqual(self.later("Espana/HMS.glb", "Rivet"), "X")     # HMS -> hms_cut_split
        self.assertEqual(self.later("Espana/HMS.glb", "Mast"), "X")      # HMS -> hms_cut_split2
        self.assertEqual(self.later("Espana/HMS.glb", "Lamp"), "")       # hms_v2_split is another model
        self.assertEqual(self.later("Espana/hms_cut.glb", "Rivet"), "X")

    def test_another_folder_and_an_absent_part_do_not_carry(self):
        self.assertEqual(self.later("Espana/HMS.glb", "Keel"), "")       # Other/hms_cut_split.glb is another model
        self.assertEqual(self.later("Espana/HMS.glb", "Flag"), "")       # no later file lists it: not inferred deleted
        self.assertEqual(self.later("Other/hms_cut_split.glb", "Keel"), "")   # marked there itself, not carried

    def test_a_file_of_the_same_stage_is_not_a_later_file(self):
        self.assertEqual(self.later("Espana/hms_cut_split.glb", "Mast"), "")   # hms_cut_split2 is the same stage

    def test_an_unnamed_node_is_never_the_same_part(self):
        self.assertEqual(self.later("Espana/HMS.glb", "node 2"), "")
        self.assertEqual(self.later("Espana/hms_cut.glb", "node 2"), "")

    def test_a_part_marked_here_is_not_touched(self):
        self.assertEqual(self.by[("Espana/hms_cut.glb", "Gear")]["mark"], "X")
        self.assertEqual(self.later("Espana/hms_cut.glb", "Gear"), "")   # X here already
        self.assertEqual(self.by[("Espana/hms_cut.glb", "Deck")]["mark"], "S")
        self.assertEqual(self.later("Espana/hms_cut.glb", "Deck"), "")   # split here; the split file's X on the Deck is not this row's

    def test_a_later_x_carries_and_an_unmarked_listing_at_that_stage_or_later_is_a_conflict(self):
        # Gear: X at cut, unmarked at split. Rivet and Deck: X at split, unmarked at split2. Mast: X at split2, unmarked at
        # split (two exports of one stage have no order the corpus knows: a conflict line is a prompt to look, not a verdict)
        self.assertEqual(self.later("Espana/HMS.glb", "Gear"), "X")
        self.assertEqual(self.later("Espana/HMS.glb", "Deck"), "X")
        self.assertIn("LATER Espana/HMS.glb: 4 unmarked part(s) carry the X a later stage file gave them (mark_later)", self.report)
        self.assertIn("LATER conflict at Espana/HMS.glb: 4 part(s) marked X at one later stage file and listed unmarked at another of the same or a higher stage; the X carries", self.report)
        self.assertIn("LATER Espana/hms_cut.glb: 2 unmarked part(s) carry the X a later stage file gave them (mark_later)", self.report)
        self.assertIn("LATER conflict at Espana/hms_cut.glb: 2 part(s) marked X at one later stage file and listed unmarked at another of the same or a higher stage; the X carries", self.report)
        self.assertFalse(any(l.startswith("LATER") and "hms_cut_split" in l.split(":")[0] for l in self.report), self.report)

    def test_a_file_judged_only_at_a_later_stage_is_in_the_delete_population(self):
        # the raw file's own sidecar resolved to nothing, so without the carried X it would not count as judged
        self.assertEqual(self.by[("Espana/HMS.glb", "Gear")]["mark"], "")
        truth = {(r["file"], r["name"]): r["truth"] for r in evaluate.population("delete", self.parts)}
        self.assertEqual(truth[("Espana/HMS.glb", "Gear")], "X")
        self.assertEqual(truth[("Espana/HMS.glb", "Lamp")], "")


class DeleteProposer(unittest.TestCase):
    """tools/advisor/proposers/delete.py: interior or tiny against the model, thresholds learned on the training rows."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools", "advisor", "proposers"))
        import delete  # noqa: E402
        import common  # noqa: E402
        cls.delete = delete
        cls.common = common

    @staticmethod
    def row(model, i, vis, size, truth="", file=None):
        return {"model": model, "file": file or model + "/m.glb", "node_index": i, "name": "n%d" % i, "stage": "split", "vis": str(vis),
                "sx": str(size), "sy": str(size / 2), "sz": str(size / 4), "verts": "9", "mark": "", "truth": truth}

    def training(self):
        train = [self.row("a", 0, 1, 100.0)]   # the hull: the model's extent
        for i in range(1, 11):
            train.append(self.row("a", i, 0, 2.0 + i / 10, "X"))        # interior fittings, 2 % of the model: deleted
            train.append(self.row("a", 20 + i, 1, 0.02 + i / 1000, "X"))  # exterior rivets, 0.02 %: deleted
            train.append(self.row("a", 40 + i, 1, 1.0 + i / 10))         # exterior fittings, 1 %: kept
            train.append(self.row("a", 60 + i, 0, 30.0 + i))             # a big interior part (a deck under the hull): kept
        return train

    def test_the_rule_is_learned_and_applied_without_truth(self):
        a, b = self.delete.learn(self.training())
        self.assertGreaterEqual(a, 0.03)       # the interior fittings are in ...
        self.assertLess(a, 0.3)                # ... the big interior deck is not
        self.assertGreaterEqual(b, 0.0003)     # the rivets are in ...
        self.assertLess(b, 0.01)               # ... the exterior fittings are not
        test = [{k: v for k, v in r.items() if k != "truth"} for r in
                (self.row("b", 0, 1, 50.0), self.row("b", 1, 0, 1.0), self.row("b", 2, 1, 0.01), self.row("b", 3, 1, 0.6), self.row("b", 4, 0, 20.0))]
        preds = self.delete.propose("delete-hand", test, self.training(), {"model": "b", "category": "ship", "files": ""}, [])
        self.assertEqual(preds, {("b/m.glb", 0): "", ("b/m.glb", 1): "X", ("b/m.glb", 2): "X", ("b/m.glb", 3): "", ("b/m.glb", 4): ""})
        self.assertEqual(self.delete.LAST_RULE["model"], "b")
        self.assertEqual(self.delete.propose("split", test, self.training(), {}, []), {})

    def test_the_size_is_relative_to_each_file(self):
        # the same absolute size is tiny on a battleship and a whole part on a canoe
        ext = self.delete.file_extents([self.row("a", 0, 1, 100.0), self.row("a", 1, 1, 0.5, file="a/canoe.glb"), self.row("a", 2, 1, 0.05, file="a/canoe.glb")])
        self.assertEqual(ext, {"a/m.glb": 100.0, "a/canoe.glb": 0.5})
        self.assertAlmostEqual(self.delete.relative_size(self.row("a", 2, 1, 0.05, file="a/canoe.glb"), ext), 0.1)

    def test_ties_go_to_the_stricter_rule_and_no_training_deletion_abstains(self):
        self.assertIsNone(self.delete.learn([]))
        self.assertIsNone(self.delete.learn([self.row("a", 0, 1, 100.0), self.row("a", 1, 0, 1.0)]))
        self.assertEqual(self.delete.propose("delete", [self.row("b", 1, 0, 1.0)], [self.row("a", 0, 1, 100.0)], {"model": "b"}, []), {})
        # one deleted interior part and nothing else: every a >= its share scores the same, the smallest wins
        a, b = self.delete.learn([self.row("a", 0, 1, 100.0), self.row("a", 1, 0, 2.0, "X")])
        self.assertEqual((a, b), (0.02, 0.0))
        # a training deletion no grid rule reaches (an exterior part half the model's size): abstain, not the "none" rule
        unreachable = [self.row("a", 0, 1, 100.0), self.row("a", 1, 1, 50.0, "X"), self.row("a", 2, 0, 1.0)]
        self.assertIsNone(self.delete.learn(unreachable))
        self.assertEqual(self.delete.propose("delete", [self.row("b", 1, 0, 1.0)], unreachable, {"model": "b"}, []), {})
        self.assertIn("no grid rule", self.delete.LAST_RULE["abstained"])

    def test_rows_without_the_features_or_without_any_verdict_fail_loudly(self):
        with self.assertRaises(ValueError):
            self.delete.propose("delete", [{"file": "f", "node_index": 1, "sx": "1"}], [], {}, [])
        # the column is there, the verdict is on no row at all: the corpus was exported without --probe
        test = [self.row("b", 1, "", 1.0)]
        train = [dict(r, vis="") for r in self.training()]
        with self.assertRaises(ValueError) as cm:
            self.delete.propose("delete", test, train, {"model": "b"}, [])
        self.assertIn("--probe", str(cm.exception))
        for c in self.delete.NEEDED:
            self.assertIn(c, evaluate.PART_FEATURES)

    def test_a_model_without_a_verdict_abstains_and_the_other_folds_run(self):
        # one model's files are stale or failed in the probe table and have no recipe: its fold proposes nothing and says
        # why; it does not abort the whole evaluation
        test = [self.row("b", 1, "", 1.0), self.row("b", 2, "", 0.001)]
        self.assertEqual(self.delete.propose("delete", test, self.training(), {"model": "b"}, []), {})
        self.assertIn("no visibility verdict", self.delete.LAST_RULE["abstained"])
        self.assertNotIn("a", self.delete.LAST_RULE)
        # the Lab's -1 (unclassified) is no verdict either
        unclassified = [self.row("b", 1, -1, 1.0), self.row("b", 2, -1, 0.001)]
        self.assertEqual(self.delete.propose("delete", unclassified, self.training(), {"model": "b"}, []), {})
        self.assertIn("abstained", self.delete.LAST_RULE)
        # under the evaluator the fold is named with its reason and its positives, so the recall number is readable
        parts = []
        for r in self.training() + [self.row("b", 1, "", 1.0, "X"), self.row("b", 2, "", 50.0)]:
            parts.append(dict({c: "" for c in corpus.PART_COLUMNS}, **{k: v for k, v in r.items() if k != "truth"}))
            parts[-1].update(mark=r["truth"], category="ship")
        models = [{"model": m, "category": "ship", "files": "", "dials_parsed": {}} for m in ("a", "b")]
        text = "\n".join(evaluate.evaluate(parts, models, {t: ([("delete", self.delete.propose)] if t == "delete" else []) for t in evaluate.TASKS}))
        self.assertIn("fold b: abstained - no visibility verdict on the held-out rows (stale or failed in the probe table, no recipe); its 1 positive(s) of 2 rows count as missed", text)

    def test_an_abstention_on_one_task_is_not_reported_under_another(self):
        # the delete proposer serves no split task: it returns nothing for it, and the note its delete fold left must
        # not read as the split fold's - the proposer clears first, and the evaluator clears before every fold
        self.delete.propose("delete", [self.row("b", 1, "", 1.0)], self.training(), {"model": "b"}, [])
        self.assertIn("abstained", self.delete.LAST_RULE)
        self.assertEqual(self.delete.propose("split", [self.row("b", 1, "", 1.0)], self.training(), {"model": "b"}, []), {})
        self.assertEqual(self.delete.LAST_RULE, {})
        self.delete.LAST_RULE["abstained"] = "a note left by another proposer's fold"
        notes = []
        rows = [dict(r, truth="S", category="ship") for r in self.training()[:3]]
        models = [{"model": "a", "category": "ship", "files": "", "dials_parsed": {}}]
        self.assertEqual(evaluate.leave_one_model_out("split", rows, models, self.delete.propose, notes), {})
        self.assertEqual(notes, [])
        # some rows without a verdict (one file stale beside a probed one): the fold runs, the blank rows are not interior
        mixed = [self.row("b", 0, 1, 50.0), self.row("b", 1, "", 1.0), self.row("b", 2, 0, 1.0)]
        self.assertEqual(self.delete.propose("delete", mixed, self.training(), {"model": "b"}, []), {("b/m.glb", 0): "", ("b/m.glb", 1): "", ("b/m.glb", 2): "X"})
        self.assertNotIn("abstained", self.delete.LAST_RULE)

    def test_ties_are_exact_not_the_last_bit_of_a_float(self):
        # two rules with the same F0.5 as a fraction (5/12 each): a = 0.05 proposes 2 training parts (1 right, 1 wrong,
        # 3 missed), a = 0.1 proposes 11 (4 right, 7 wrong, 0 missed); the stricter one wins the tie
        train = [self.row("a", 0, 1, 100.0)]
        train += [self.row("a", 1, 0, 3.0, "X"), self.row("a", 2, 0, 3.5)]
        train += [self.row("a", 10 + i, 0, 6.0 + i / 10, "X") for i in range(3)]
        train += [self.row("a", 20 + i, 0, 7.0 + i / 10) for i in range(6)]
        self.assertEqual(self.delete.f_beta(1, 1, 3), self.delete.f_beta(4, 7, 0))
        self.assertEqual(self.delete.learn(train), (0.05, 0.0))

    def test_a_part_whose_size_is_not_known_is_never_small(self):
        # a blocked mesh's box is empty in the corpus (it was nan and -inf once, and -inf is under every threshold):
        # not a candidate under any rule, and not the file's extent either
        for sx in ("", "nan", "-inf", "inf"):
            unknown = dict(self.row("b", 1, 0, 1.0), sx=sx, sy=sx, sz=sx)
            ext = self.delete.file_extents([self.row("b", 0, 1, 50.0), unknown])
            self.assertEqual(ext, {"b/m.glb": 50.0}, sx)
            self.assertIsNone(self.delete.relative_size(unknown, ext), sx)
            self.assertFalse(self.delete.candidate(unknown, None, 1.0, 0.02), sx)
        preds = self.delete.propose("delete", [self.row("b", 0, 1, 50.0), dict(self.row("b", 1, 0, 1.0), sx="-inf", sy="nan", sz="")],
                                    self.training(), {"model": "b"}, [])
        self.assertEqual(preds, {("b/m.glb", 0): "", ("b/m.glb", 1): ""})

    def test_one_unknown_or_negative_axis_does_not_make_a_part_tiny(self):
        hull = self.row("b", 0, 1, 50.0)
        for axis in ("sx", "sy", "sz"):
            for invalid in ("", "nan", "inf", "-inf", "-0.01"):
                with self.subTest(axis=axis, invalid=invalid):
                    unknown = dict(self.row("b", 1, 0, 0.001), **{axis: invalid})
                    self.assertIsNone(self.delete.extent(unknown))
                    extents = self.delete.file_extents([hull, unknown])
                    self.assertIsNone(self.delete.relative_size(unknown, extents))
                    preds = self.delete.propose("delete", [hull, unknown], self.training(), {"model": "b"}, [])
                    self.assertEqual(preds[("b/m.glb", 1)], "")

    def test_a_zero_threshold_switches_its_clause_off(self):
        # a zero-extent part (a point, or a row without extents) has relative size 0: a rule that says "none by size
        # alone" must not propose it, nor "no interior part"
        point = self.row("b", 1, 1, 0.0)
        self.assertFalse(self.delete.candidate(point, 0.0, 0.02, 0.0))
        interior_point = self.row("b", 2, 0, 0.0)
        self.assertFalse(self.delete.candidate(interior_point, 0.0, 0.0, 0.0))
        self.assertTrue(self.delete.candidate(interior_point, 0.0, 0.02, 0.0))   # the interior clause is on: a point inside goes
        self.assertTrue(self.delete.candidate(point, 0.0, 0.0, 0.001))

    def corpus_dir(self, rows):
        """A corpus folder (parts.csv, models.csv) from rows that carry only some of the columns."""
        tmp = tempfile.TemporaryDirectory(prefix="advisor-delete-cli-")
        self.addCleanup(tmp.cleanup)
        full = [dict({c: "" for c in corpus.PART_COLUMNS}, **{k: v for k, v in r.items() if k != "truth"}) for r in rows]
        corpus.write_csv(os.path.join(tmp.name, "parts.csv"), corpus.PART_COLUMNS, full)
        corpus.write_csv(os.path.join(tmp.name, "models.csv"), corpus.MODEL_COLUMNS,
                         [dict({c: "" for c in corpus.MODEL_COLUMNS}, model=m) for m in sorted(set(r["model"] for r in rows))])
        return tmp.name

    def test_the_cli_leaves_the_users_decisions_alone_and_writes_beside_the_glb(self):
        train = [dict(r, mark=r["truth"]) for r in self.training()]   # in the corpus the truth is the mark
        held = [self.row("b", 0, 1, 50.0), self.row("b", 1, 0, 1.0), self.row("b", 2, 0, 1.1), self.row("b", 3, 0, 1.2),
                self.row("b", 4, 0, 1.3), self.row("b", 5, 0, 1.4), self.row("b", 6, 1, 0.01)]
        held[2]["mark"] = "S"              # split by the user: not a candidate
        held[3]["fuse_letter"] = "A"       # in a fuse group: kept by the user, not a candidate
        held[4]["mark_later"] = "X"        # deleted at a later stage: proposed and said so
        held[5]["role"] = "Ignore"         # the recipe ignores it: proposed and said so
        cdir = self.corpus_dir(train + held)
        root = tempfile.TemporaryDirectory(prefix="advisor-delete-root-")
        self.addCleanup(root.cleanup)
        os.makedirs(os.path.join(root.name, "b"))
        out = io.StringIO()
        with redirect_stdout(out):
            rc = self.delete.main(["--corpus", cdir, "--model", "b", "--models-root", root.name, "--write"])
        self.assertEqual(rc, 0, out.getvalue())
        text = out.getvalue()
        self.assertIn("b/m.glb: 4 proposal(s)", text)
        self.assertNotIn("X|2|n2", text)
        self.assertNotIn("X|3|n3", text)
        self.assertIn("X|4|n4  9 verts, vis 0 (marked X at a later stage)", text)
        self.assertIn("X|5|n5  9 verts, vis 0 (role Ignore in the recipe)", text)
        lines, _ = corpus.read_sidecar(os.path.join(root.name, "b", "m.glb.marks.proposed.txt"))
        self.assertEqual(lines, [("X", 1, "n1"), ("X", 4, "n4"), ("X", 5, "n5"), ("X", 6, "n6")])
        # a models root without the model's folder is refused before anything is written, with the flag named
        out = io.StringIO()
        with redirect_stdout(out):
            rc = self.delete.main(["--corpus", cdir, "--model", "b", "--models-root", os.path.join(root.name, "nope"), "--write"])
        self.assertEqual(rc, 1)
        self.assertIn("--models-root", out.getvalue())
        # a held-out model without a verdict: no proposals, the reason printed, exit 0, and --write leaves the earlier
        # proposals as they are (an abstention is not a judgement)
        cdir2 = self.corpus_dir(train + [dict(r, vis="") for r in held])
        out = io.StringIO()
        with redirect_stdout(out):
            rc = self.delete.main(["--corpus", cdir2, "--model", "b", "--models-root", root.name, "--write"])
        self.assertEqual(rc, 0)
        self.assertIn("no proposals: no visibility verdict", out.getvalue())
        self.assertIn("left as they are", out.getvalue())
        self.assertNotIn("written:", out.getvalue())
        self.assertEqual(corpus.read_sidecar(os.path.join(root.name, "b", "m.glb.marks.proposed.txt"))[0], lines)
        # a file the merge refuses (not UTF-8) fails the write with the file named
        with open(os.path.join(root.name, "b", "m.glb.marks.proposed.txt"), "wb") as f:
            f.write(b"#fuse-groups v2\nS|7|Ma\xe9t\n")
        out = io.StringIO()
        with redirect_stdout(out):
            rc = self.delete.main(["--corpus", cdir, "--model", "b", "--models-root", root.name, "--write"])
        self.assertEqual(rc, 1)
        self.assertIn("not UTF-8", out.getvalue())

    def test_the_proposals_file_keeps_hand_added_group_names_and_refuses_another_code_page(self):
        tmp = tempfile.TemporaryDirectory(prefix="advisor-proposals-")
        self.addCleanup(tmp.cleanup)
        path = os.path.join(tmp.name, "m.glb.marks.proposed.txt")
        with io.open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write("#fuse-groups v2\n#name|A|Rigging\nA|3|Ma\u00e9t\nX|9|stale\n")
        self.common.merge_proposals(path, ("X",), ["X|1|rivet"], "delete.py")
        lines, _ = corpus.read_sidecar(path)
        self.assertEqual([(l, i, n) for l, i, n in lines], [("A", 3, "Ma\u00e9t"), ("X", 1, "rivet")])
        with io.open(path, encoding="utf-8") as f:
            self.assertIn("#name|A|Rigging\n", f.read())
        # hand-edited and saved in cp1252: a name read with a replacement character would match no part, so the merge
        # is refused and the file left as it is
        with open(path, "wb") as f:
            f.write(b"#fuse-groups v2\nA|3|Ma\xe9t\n")
        with self.assertRaises(ValueError) as cm:
            self.common.merge_proposals(path, ("X",), ["X|1|rivet"], "delete.py")
        self.assertIn("not UTF-8", str(cm.exception))
        with open(path, "rb") as f:
            self.assertEqual(f.read(), b"#fuse-groups v2\nA|3|Ma\xe9t\n")

    def test_proposals_of_two_proposers_share_one_file_by_letter(self):
        tmp = tempfile.TemporaryDirectory(prefix="advisor-proposals-")
        self.addCleanup(tmp.cleanup)
        path = os.path.join(tmp.name, "m.glb.marks.proposed.txt")
        with io.open(path, "w", encoding="utf-8") as f:
            f.write("#fuse-groups v2\n#proposed by split.py\nS|3|mast\nX|9|stale\n")
        self.common.merge_proposals(path, ("X",), ["X|1|rivet", "X|2|gear"], "delete.py")
        lines, _ = corpus.read_sidecar(path)
        self.assertEqual(lines, [("S", 3, "mast"), ("X", 1, "rivet"), ("X", 2, "gear")])   # the S kept, the stale X replaced
        self.common.merge_proposals(path, ("S",), [], "split.py")
        self.assertEqual(corpus.read_sidecar(path)[0], [("X", 1, "rivet"), ("X", 2, "gear")])   # an empty S proposal clears only the S
        self.assertEqual(self.common.merge_proposals(os.path.join(tmp.name, "new.glb.marks.proposed.txt"), ("X",), ["X|1|a"], "delete.py"), ["X|1|a"])

    def test_conflicting_actions_for_one_node_are_refused_in_both_run_orders(self):
        tmp = tempfile.TemporaryDirectory(prefix="advisor-proposals-")
        self.addCleanup(tmp.cleanup)
        path = os.path.join(tmp.name, "m.glb.marks.proposed.txt")
        for first, second in (("S", "X"), ("X", "S"), ("T", "X"), ("A", "X")):
            with self.subTest(first=first, second=second):
                before = ("#fuse-groups v2\n%s|003|mast|tip\n" % first).encode("utf-8")
                with open(path, "wb") as f:
                    f.write(before)
                with self.assertRaises(ValueError) as cm:
                    self.common.merge_proposals(path, (second,), ["%s|+3|mast|tip" % second], "test")
                self.assertIn("conflicting", str(cm.exception))
                with open(path, "rb") as f:
                    self.assertEqual(f.read(), before)

    def test_a_failed_write_preserves_the_other_proposers_file(self):
        tmp = tempfile.TemporaryDirectory(prefix="advisor-proposals-")
        self.addCleanup(tmp.cleanup)
        path = os.path.join(tmp.name, "m.glb.marks.proposed.txt")
        before = b"#fuse-groups v2\nS|3|mast\nX|9|old\n"
        with open(path, "wb") as f:
            f.write(before)
        # A real encoding failure after writing the header and kept lines must not truncate the old proposals.
        with self.assertRaises(UnicodeEncodeError):
            self.common.merge_proposals(path, ("X",), ["X|1|invalid\ud800"], "delete.py")
        with open(path, "rb") as f:
            self.assertEqual(f.read(), before)
        self.assertEqual(os.listdir(tmp.name), [os.path.basename(path)])


if __name__ == "__main__":
    unittest.main()
