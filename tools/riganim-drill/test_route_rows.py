"""Regression checks for the direct-against-round-trip route comparison (route_rows.py, step 6g)."""
import tempfile
import unittest
from pathlib import Path

import route_rows

ROWS = [
    "JOB\tMech",
    "LOG\t1\tRIGANIM import",
    "STAGE\t1",
    "SCENE\t1\t1\t24\t1\t-",
    "OBJ\t1\tArmature\tARMATURE\t-\tOBJECT\t-\tArmature\tWalk\t-\t3f800000",
    "MESH\t1\tBody\t100\t50\t200\taaaa\tbbbb\tcccc\tdddd\teeee\tmat\t-\t-\t-",
    "STAGE\t2",
    "SCENE\t2\t1\t24\t1\t-",
    "STAGE\t3",
    "SCENE\t3\t1\t24\t1\t-",
    "STAGE\t4",
    "SCENE\t4\t1\t24\t1\t-",
    "BONE\t4\t0\troot\t-\t1\t3f800000\t3f800000",
    "MESH\t4\tBody\t100\t50\t200\taaaa\tbbbb\tcccc\tdddd\teeee\tmat\t-\t-\t-",
    "LOG\t4\tRIGANIM placement 0.04",
    "XLOG\tRIGANIM glb wrote C:/x.glb",
    "X\tGLB\t100\t50\t20\t" + "d" * 64,
    "X\tSCENE\tScene\t0",
    "X\tNODE\t0\troot\t3f800000",
    "DONE\tMech",
]


class RouteRowsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def dump(self, name, rows):
        p = self.root / name
        p.write_text("\n".join(rows) + "\n", encoding="utf-8")
        return str(p)

    def run_rows(self, *argv):
        out = []
        rc = route_rows.main(list(argv), out=out.append)
        return rc, out

    def verdict(self, out, key="Mech"):
        return next(l for l in out if l.startswith("ROUTE\t" + key + "\t") and l.split("\t")[2] in ("EQUAL", "DIFFERS", "FAIL", "NOTE"))

    def test_the_same_dump_is_equal_and_a_taken_job_passes(self):
        d = self.dump("d.txt", ROWS)
        rc, out = self.run_rows(d, self.dump("r.txt", ROWS))
        self.assertEqual(rc, 0)
        self.assertTrue(self.verdict(out).startswith("ROUTE\tMech\tEQUAL\tstage 4 and export 0 row(s) differ (-); stages 1-3 0; LOG 0"))
        self.assertIn("ROUTES\tjobs 1 equal 1 differs 0 fail 0 note 0", out)

    def test_a_stage_4_difference_fails_a_taken_job_and_is_a_measurement_on_a_left_one(self):
        d = self.dump("d.txt", ROWS)
        changed = [r.replace("BONE\t4\t0\troot\t-\t1\t3f800000\t3f800000", "BONE\t4\t0\troot\t-\t1\t3fcccccd\t3f800000") for r in ROWS]
        r = self.dump("r.txt", changed)
        rc, out = self.run_rows(d, r)
        self.assertEqual(rc, 1)
        v = self.verdict(out)
        self.assertTrue(v.startswith("ROUTE\tMech\tFAIL\t"), v)
        self.assertIn("stage 4 and export 2 row(s) differ (BONE)", v)
        self.assertIn("ROUTE\tMech\t4\tBONE\tdirect 1 roundtrip 1 differ 2", out)
        self.assertTrue(any(l.startswith("ROUTE\tMech\tDIFF\t4\tBONE\tD: BONE\t4\t0\troot") for l in out))
        self.assertTrue(any(l.startswith("ROUTE\tMech\tDIFF\t4\tBONE\tR: BONE\t4\t0\troot") for l in out))
        rc, out = self.run_rows("--left", "Mech", d, r)
        self.assertEqual(rc, 0)
        self.assertTrue(self.verdict(out).startswith("ROUTE\tMech\tDIFFERS\tleft to the FBX route by name"))
        self.assertIn("ROUTES\tjobs 1 equal 0 differs 1 fail 0 note 0", out)

    def test_an_export_row_difference_is_in_the_verdict(self):
        d = self.dump("d.txt", ROWS)
        r = self.dump("r.txt", [x.replace("X\tNODE\t0\troot\t3f800000", "X\tNODE\t0\troot\t00000000") for x in ROWS])
        rc, out = self.run_rows(d, r)
        self.assertEqual(rc, 1)
        self.assertIn("stage 4 and export 2 row(s) differ (X)", self.verdict(out))

    def test_stage_1_and_log_differences_are_counted_but_not_the_verdict(self):
        d = self.dump("d.txt", ROWS)
        changed = [x.replace("MESH\t1\tBody\t100", "MESH\t1\tBody\t999").replace("RIGANIM placement 0.04", "RIGANIM placement 1.63")
                   .replace("wrote C:/x.glb", "wrote C:/y.glb") for x in ROWS]
        rc, out = self.run_rows(d, self.dump("r.txt", changed))
        self.assertEqual(rc, 0)
        v = self.verdict(out)
        self.assertTrue(v.startswith("ROUTE\tMech\tEQUAL\t"), v)
        self.assertIn("stages 1-3 2; LOG 4", v)
        self.assertIn("ROUTE\tMech\t1\tMESH\tdirect 1 roundtrip 1 differ 2", out)
        self.assertIn("ROUTE\tMech\tX\tXLOG\tdirect 1 roundtrip 1 differ 2", out)

    def test_an_equal_left_job_is_a_note(self):
        d = self.dump("d.txt", ROWS)
        rc, out = self.run_rows("--left", "Mech", d, self.dump("r.txt", ROWS))
        self.assertEqual(rc, 0)
        self.assertTrue(self.verdict(out).startswith("ROUTE\tMech\tNOTE\tequal to its round trip, yet left"))
        self.assertIn("ROUTES\tjobs 1 equal 0 differs 0 fail 0 note 1", out)

    def test_a_missing_job_or_done_row_is_an_error(self):
        d = self.dump("d.txt", ROWS)
        rc, out = self.run_rows(d, self.dump("r.txt", [x.replace("JOB\tMech", "JOB\tOther").replace("DONE\tMech", "DONE\tOther") for x in ROWS]))
        self.assertEqual(rc, 2)
        self.assertIn("ROUTE\tERROR\tMech: not in the round-trip dump(s)", out)
        rc, out = self.run_rows(d, self.dump("r2.txt", [x for x in ROWS if not x.startswith("DONE")]))
        self.assertEqual(rc, 2)
        self.assertIn("ROUTE\tERROR\tjob(s) without a DONE row: Mech", out)

    def test_a_doubled_row_is_a_difference(self):
        d = self.dump("d.txt", ROWS)
        doubled = ROWS.copy()
        # Keep the duplicate in stage 4, before the export.
        doubled.insert(doubled.index("XLOG\tRIGANIM glb wrote C:/x.glb"), "BONE\t4\t0\troot\t-\t1\t3f800000\t3f800000")
        rc, out = self.run_rows(d, self.dump("r.txt", doubled))
        self.assertEqual(rc, 1)
        self.assertIn("ROUTE\tMech\t4\tBONE\tdirect 1 roundtrip 2 differ 1", out)

    def test_missing_completed_output_cannot_recommend_lifting_a_fallback(self):
        cases = {
            "empty": ["JOB\tMech", "DONE\tMech"],
            "early_exit": ["JOB\tMech", "EXIT\t1\t1", "DONE\tMech"],
            "no_stage4": [r for r in ROWS if r != "STAGE\t4"],
            "no_scene4": [r for r in ROWS if not r.startswith("SCENE\t4\t")],
            "no_glb": [r for r in ROWS if not r.startswith("X\tGLB\t")],
            "no_export_scene": [r for r in ROWS if not r.startswith("X\tSCENE\t")],
            "doubled_glb": ROWS[:-1] + [next(r for r in ROWS if r.startswith("X\tGLB\t")), ROWS[-1]],
            "export_died": [r for r in ROWS[:-1] if not r.startswith("X\t")] + ["XDIES\texport failed", ROWS[-1]],
        }
        for label, rows in cases.items():
            with self.subTest(label=label):
                d = self.dump(label + ".txt", rows)
                rc, out = self.run_rows("--left", "Mech", d, d)
                self.assertEqual(rc, 2, out)
                self.assertTrue(any(r.startswith("ROUTE\tERROR\t") for r in out), out)

    def test_duplicate_job_definitions_cannot_overwrite_a_different_shard(self):
        d = self.dump("d.txt", ROWS)
        changed = [r.replace("BONE\t4\t0\troot", "BONE\t4\t0\tother") for r in ROWS]
        for paths in ((self.dump("twice.txt", ROWS + ROWS),),
                      (self.dump("bad.txt", changed), self.dump("good.txt", ROWS))):
            with self.subTest(paths=paths):
                rc, out = self.run_rows("--left", "Mech", d, *paths)
                self.assertEqual(rc, 2, out)

    def test_rows_after_done_and_unannounced_stages_are_errors(self):
        cases = [ROWS + ["MESH\t4\tBody\t100"],
                 ROWS[:2] + ["MESH\t4\tBody\t100"] + ROWS[2:],
                 [r.replace("DONE\tMech", "DONE\tOther") for r in ROWS]]
        for i, rows in enumerate(cases):
            with self.subTest(i=i):
                d = self.dump(str(i) + ".txt", rows)
                rc, out = self.run_rows("--left", "Mech", d, d)
                self.assertEqual(rc, 2, out)

    def test_inprocess_report_preserves_colons_in_a_job_key(self):
        rows = [r.replace("Mech", "LEFT:Mech") for r in ROWS]
        d = self.dump("d.txt", rows)
        r = self.dump("r.txt", [x.replace("BONE\t4\t0\troot", "BONE\t4\t0\tother") for x in rows])
        report = self.dump("inproc.txt", ["INPROC LEFT:Mech: left to the FBX route by name, as the wrapper says: the FBX rig import needs Blender: a skinned or animated FBX"])
        rc, out = self.run_rows("--inproc", report, d, r)
        self.assertEqual(rc, 0, out)
        self.assertTrue(self.verdict(out, "LEFT:Mech").startswith("ROUTE\tLEFT:Mech\tDIFFERS\t"))

    def test_unrelated_stopped_roundtrip_jobs_are_not_compared(self):
        d = self.dump("d.txt", ROWS)
        r = self.dump("r.txt", ROWS + ["JOB\tEXIT:1:Other", "EXIT\t1\t1", "DONE\tEXIT:1:Other"])
        rc, out = self.run_rows(d, r)
        self.assertEqual(rc, 0, out)
        self.assertTrue(self.verdict(out).startswith("ROUTE\tMech\tEQUAL\t"))

    def test_missing_files_and_unknown_fallback_jobs_are_errors(self):
        d = self.dump("d.txt", ROWS)
        for args in ((d, str(self.root / "missing")), ("--inproc", str(self.root / "missing"), d, d),
                     ("--left", "Other", d, d), ("--inproc",)):
            with self.subTest(args=args):
                rc, out = self.run_rows(*args)
                self.assertEqual(rc, 2, out)
                self.assertTrue(any(r.startswith("ROUTE\tERROR\t") for r in out), out)


if __name__ == "__main__":
    unittest.main()
