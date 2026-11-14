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
PURE_NAMES = {"_fitted_cycle_count", "_oar_recovery_metrics", "_merge2_scale", "_tagged_reduce_pct", "_tagged_names_pct", "_rigid_link_budget", "_two_sided_origin"}
PURE_DEFS = [node for node in TREE.body if isinstance(node, ast.FunctionDef) and node.name in PURE_NAMES]
if {node.name for node in PURE_DEFS} != PURE_NAMES:
    raise RuntimeError("Vehicle Lab pure helper contract changed; update this test deliberately")
NAMESPACE = {"math": math}
exec(compile(ast.Module(body=PURE_DEFS, type_ignores=[]), str(SCRIPT), "exec"), NAMESPACE)


class VehicleRigMathTests(unittest.TestCase):
    def test_two_sided_ray_bias_never_skips_an_intervening_wall(self):
        class Planes:
            def __init__(self, positions):
                self.positions = positions

            def ray_cast(self, origin, direction, limit):
                hits = [((p - origin) / direction, i, p) for i, p in enumerate(self.positions)
                        if 0 <= (p - origin) / direction <= limit]
                if not hits:
                    return (None, None, None, None)
                distance, index, point = min(hits)
                return (point, None, index, distance)

        origin = NAMESPACE["_two_sided_origin"]
        for sign in (-1.0, 1.0):
            walls = Planes([0.0, sign * 1e-5, sign * 1e-4, sign * 1e-3])
            start = origin(walls, 0.0, sign, 0.01, list(range(4)), 0)
            self.assertGreater(sign * start, 0.0)
            self.assertLess(sign * start, 1e-5)
            # A single sheet has no wall to jump: keep the original bias, on either side.
            self.assertEqual(sign * 0.01, origin(Planes([0.0]), 0.0, sign, 0.01, [0], 0))

    def test_rigid_links_respect_all_role_bones_and_exhausted_capacity(self):
        budget = NAMESPACE["_rigid_link_budget"]
        for existing, removable, tracks, expected in ((23, 18, 2, 60), (93, 18, 2, 25),
                                                     (132, 18, 2, 6), (136, 18, 2, 4),
                                                     (143, 18, 2, 0), (1, 0, 0, 0)):
            with self.subTest(existing=existing, tracks=tracks):
                actual = budget(existing, removable, tracks)
                self.assertEqual(expected, actual)
                if actual:
                    self.assertLessEqual(existing - removable + actual * tracks, 126)

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

    def test_windowreduce_tag_carries_the_names_file_and_the_percent(self):
        """windowreduce=@<names file>|<percent> (2026-09-25): the Window parts travel nowhere else, so the tag carries
        both. Absent -> ("", 0.0); the names text is everything before the LAST '|' (a part name may hold one); the
        percent is clamped 0..95; a missing '|' or a non-finite percent is refused (a VEHICLE ERROR in the script)."""
        tag = NAMESPACE["_tagged_names_pct"]
        self.assertEqual(("", 0.0), tag(["rig", "gunreduce=50", "shroudreduce=@s.txt|20"], "windowreduce"))
        self.assertEqual(("@C:/p/x_window.txt", 40.0), tag(["rig", "windowreduce=@C:/p/x_window.txt|40", "flagfold=0"], "windowreduce"))
        self.assertEqual(("@a|b.txt", 95.0), tag(["windowreduce=@a|b.txt|120"], "windowreduce"))
        self.assertEqual(("", 0.0), tag(["windowreduce=|-3"], "windowreduce"))
        self.assertEqual(("@s.txt", 20.0), tag(["windowreduce=@w.txt|10", "shroudreduce=@s.txt|20"], "shroudreduce"))
        for bad in ("windowreduce=", "windowreduce=@w.txt", "windowreduce=@w.txt|", "windowreduce=@w.txt|abc", "windowreduce=@w.txt|nan", "windowreduce=@w.txt|inf"):
            with self.assertRaises(ValueError, msg=bad):
                tag([bad], "windowreduce")

    def test_merge2_scale_rejects_a_wrong_component_count(self):
        parse = NAMESPACE["_merge2_scale"]
        with self.assertRaises(ValueError):
            parse("1,2")
        with self.assertRaises(ValueError):
            parse("1,2,3,4")


class DissolveOrderGuard(unittest.TestCase):
    # 2026-10-12: the chunked _dissolve_limited handed each cell's edges and verts to bmesh.ops.dissolve_limit in
    # Python SET order - by object address, different in every Blender process - and a limited dissolve is greedy
    # over that order. The Lab goldens recorded three dissolve counts for one part in three runs. The lists are
    # sorted by element index, after an index refresh; this keeps a `list(_es)` from coming back.
    def test_the_chunked_dissolve_hands_blender_its_cells_in_index_order(self):
        start = SOURCE.index("def _dissolve_limited(")
        body = SOURCE[start:SOURCE.index("\n_lap(", start)]
        self.assertIn("sorted(_es, key=lambda _e: _e.index)", body)
        self.assertIn("sorted(_vs, key=lambda _v: _v.index)", body)
        self.assertNotIn("list(_es)", body)
        self.assertNotIn("list(_vs)", body)
        self.assertLess(body.index("index_update()"), body.index("_jobs = []"), "indices are refreshed before the cells are gathered")


class RailingTierGuard(unittest.TestCase):
    # 2026-11-12 (user: "add Railing as a new reducible classification"): railingreduce=@<names>|<percent> is parsed right
    # after windowreduce, its names join _role_marked, and the RAILING tier runs right after SHROUD as a SOUP tier (no weld).
    # The C# port (BlenderVehicleRig) mirrors the three; this keeps the script's side.
    def test_railing_is_parsed_after_window_marked_and_a_soup_tier_after_shroud(self):
        win = SOURCE.index('_tagged_names_pct(argv, "windowreduce")')
        rai = SOURCE.index('_tagged_names_pct(argv, "railingreduce")')
        self.assertLess(win, rai)
        self.assertIn("shroud_names, window_names, railing_names,", SOURCE)
        loop = SOURCE[SOURCE.index("for _rlabel, _rnames, _rpct in ("):]
        loop = loop[:loop.index(chr(10))]
        self.assertIn('("SHROUD", shroud_names, shroud_reduce), ("RAILING", railing_names, railing_reduce), ("STRUCTURE"', loop)
        self.assertIn('_weld = _rlabel not in ("RIGGING", "SHROUD", "RAILING")', SOURCE)

    def test_railingreduce_tag_carries_the_names_file_and_the_percent(self):
        tag = NAMESPACE["_tagged_names_pct"]
        self.assertEqual(("@C:/p/x_railing.txt", 60.0), tag(["rig", "windowreduce=@w.txt|10", "railingreduce=@C:/p/x_railing.txt|60"], "railingreduce"))
        self.assertEqual(("", 0.0), tag(["rig", "windowreduce=@w.txt|10"], "railingreduce"))


class TurretTierGuard(unittest.TestCase):
    # 2026-10-16 (user: "could you add a turret reduce slider"): turretreduce=<percent> is parsed right after gunreduce
    # and the TURRET tier runs in the tier loop right before GUN, on the positional turret_names - both before the
    # Turret bone is measured. The C# port (BlenderVehicleRig) mirrors both orders; this keeps the script's side.
    def test_turretreduce_is_parsed_after_gunreduce_and_the_tier_runs_before_gun(self):
        gun = SOURCE.index('gun_reduce = _tagged_reduce_pct(argv, "gunreduce")')
        tur = SOURCE.index('turret_reduce = _tagged_reduce_pct(argv, "turretreduce")')
        win = SOURCE.index('_tagged_names_pct(argv, "windowreduce")')
        self.assertLess(gun, tur); self.assertLess(tur, win)
        loop = SOURCE[SOURCE.index("for _rlabel, _rnames, _rpct in ("):]
        loop = loop[:loop.index(chr(10))]
        self.assertIn('("WHEEL", wheel_names, wheel_reduce), ("TURRET", turret_names, turret_reduce), ("GUN", gun_names, gun_reduce)', loop)
        self.assertLess(SOURCE.index("for _rlabel, _rnames, _rpct in ("), SOURCE.index("tc, ts = _combined_bbox(turret_names)"))
        self.assertEqual(35.5, NAMESPACE["_tagged_reduce_pct"](["rig", "turretreduce=35.5", "gunreduce=10"], "turretreduce"))


class BoneBudgetGuard(unittest.TestCase):
    def test_the_cap_removes_only_the_excess_and_keeps_weighted_leaves(self):
        from types import SimpleNamespace
        rig_source = (SCRIPT.parent / "rig_anim.py").read_text(encoding="utf-8")
        fn = next(n for n in ast.parse(rig_source).body if isinstance(n, ast.FunctionDef) and n.name == "_zero_weight_leaves_for_cap")
        scope = {}
        exec(compile(ast.Module(body=[fn], type_ignores=[]), "rig_anim.py", "exec"), scope)
        choose = scope[fn.name]
        bones = [SimpleNamespace(name="Root", children=[1])] + [SimpleNamespace(name="Leaf%03d" % i, children=[]) for i in range(126)]
        self.assertEqual(["Leaf000"], choose(bones, {"Root"}, 126))
        self.assertEqual([], choose(bones[:126], {"Root"}, 126))
        self.assertEqual(["Leaf001"], choose(bones, {"Root", "Leaf000"}, 126))

    def test_the_fast_path_checks_its_finished_skeleton_before_export(self):
        fast = SOURCE[SOURCE.index('    def _fast():'):SOURCE.index('    _guard(_fast)')]
        self.assertIn('_bone_wall_check(arm)', fast)
        self.assertLess(fast.index('_bone_wall_check(arm)'), fast.index('bpy.ops.export_scene.gltf('))

    # 2026-09-28 (the wall corrected): the engine breaks past bone INDEX 127, so one budget of 126 in vehicle_rig.py and
    # rig_anim.py, and in their C# ports, whose log lines the drills hold to the scripts' own. A budget that drifts in
    # one place (240 again) sends a rig past the wall without a word.
    def test_one_bone_budget_of_126_in_both_scripts_and_both_ports(self):
        editor = SCRIPT.parent.parent
        rig_anim = (editor / "Tools~" / "rig_anim.py").read_text(encoding="utf-8")
        self.assertIn("_BONE_BUDGET = 126", SOURCE)
        self.assertIn("_bone_wall_check(arm)", SOURCE)
        self.assertIn("_BONE_LIMIT = 126", rig_anim)
        self.assertIn("bones after every cull and socket — past the %d-bone budget", rig_anim)
        port = (editor / "BlenderRigAnim.cs").read_text(encoding="utf-8")
        self.assertEqual(2, port.count("ad.BoneNodes.Count > 126"))
        self.assertIn("(budget 126: bone indices break past 127; weighted bones untouched)", port)
        self.assertIn("bones after every cull and socket — past the 126-bone budget", port)
        stage5 = (editor / "BlenderVehicleRig.Stage5.cs").read_text(encoding="utf-8")
        self.assertIn("s.Armature.BoneNodes.Count > 126", stage5)
        self.assertIn("past the 126-bone budget (bone indices break past 127 in-game", stage5)
        for text in (SOURCE, rig_anim, port, stage5):
            self.assertNotIn("> 240", text); self.assertNotIn("= 240", text)


class TwoSidedRoleGuard(unittest.TestCase):
    # 2026-11-14 (user: "the double walling should be applied discriminately"): twosided=@<names> marks parts whose faces are
    # doubled or left one by one at export. The pass itself needs Blender (tools/vehiclerig-drill/
    # blender_two_sided_check.py, in the vehicle_rig drill); this keeps the wiring around it.
    def test_the_tag_is_optional_and_its_names_are_marked(self):
        self.assertIn('_tsarg = next((a for a in argv if a.startswith("twosided=")), None)', SOURCE)
        self.assertIn('twosided_names = namelist(_tsarg[9:]) if _tsarg is not None and _tsarg[9:].strip() else []', SOURCE)
        marked = SOURCE[SOURCE.index("_role_marked = set()"):]
        self.assertIn("twosided_names):", marked[:marked.index("_role_marked.update(_rl)")])

    def test_a_marked_part_keeps_its_own_mesh_and_both_older_passes_leave_it(self):
        self.assertIn('_k = "__two__" + o.name', SOURCE)
        fix = SOURCE[SOURCE.index("if fix_inside_out:\n    # NOT a blind recalc"):]
        self.assertIn('_fo.name.startswith("Mesh___two__")', fix[:fix.index("_fb = bmesh.new()")])
        targets = SOURCE[SOURCE.index("_dtargets = (["):]
        self.assertIn('not o.name.startswith("Mesh___two__")', targets[:targets.index("if double_sided else")])

    def test_the_pass_measures_the_model_before_any_face_is_doubled(self):
        self.assertLess(SOURCE.index("    _two_sided_pass(_two_targets, _dall)"), SOURCE.index("_dtargets = (["))
        self.assertLess(SOURCE.index("VEHICLE FLIP applied"), SOURCE.index("    _two_sided_pass(_two_targets, _dall)"))

    def test_only_an_opposite_face_on_the_same_vertices_is_a_twin(self):
        body = SOURCE[SOURCE.index("def _two_sided_pass(targets, everything):"):SOURCE.index("_dall = [o for o in bpy.context.scene.objects")]
        self.assertIn("if any(_n2.dot(_n) < 0.0 for _n2 in _twin.get(", body)
        # the back side only, a pocket test on the longest blocked ray, a coincident copy and no reversal in place
        self.assertIn("if _d.dot(_n) > -1e-3:", body)
        self.assertIn("any(_bvh.ray_cast(_mid, _d2)[0] is None for _d2 in _dirs)", body)
        self.assertNotIn("_loff", body); self.assertNotIn("_flip", body)
        self.assertEqual(1, body.count("bmesh.ops.reverse_faces("))
        self.assertIn("bmesh.ops.reverse_faces(_twb, faces=[_g for _g in _ret['geom'] if isinstance(_g, bmesh.types.BMFace)])", body)


if __name__ == "__main__":
    unittest.main()
