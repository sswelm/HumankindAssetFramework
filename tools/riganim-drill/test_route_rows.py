"""Regression checks for the direct-against-round-trip route comparison (route_rows.py, step 6g)."""
import tempfile
import unittest
from pathlib import Path

import route_rows

ROWS = [
    "JOB\tMech",
    "LOG\t1\tRIGANIM import",
    "SCENE\t1\t1\t24\t1\t-",
    "OBJ\t1\tArmature\tARMATURE\t-\tOBJECT\t-\tArmature\tWalk\t-\t3f800000",
    "MESH\t1\tBody\t100\t50\t200\taaaa\tbbbb\tcccc\tdddd\teeee\tmat\t-\t-\t-",
    "STAGE\t1",
    "BONE\t4\t0\troot\t-\t1\t3f800000\t3f800000",
    "MESH\t4\tBody\t100\t50\t200\taaaa\tbbbb\tcccc\tdddd\teeee\tmat\t-\t-\t-",
    "LOG\t4\tRIGANIM placement 0.04",
    "STAGE\t4",
    "XLOG\tRIGANIM glb wrote C:/x.glb",
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
        rc, out = self.run_rows(d, self.dump("r.txt", ROWS + ["JOB\tMech", "STAGE\t1", "STAGE\t2", "STAGE\t3", "BONE\t4\t0\troot\t-\t1\t3f800000\t3f800000", "STAGE\t4", "DONE\tMech"]))
        self.assertEqual(rc, 1)
        self.assertIn("ROUTE\tMech\t4\tBONE\tdirect 1 roundtrip 2 differ 1", out)


if __name__ == "__main__":
    unittest.main()
