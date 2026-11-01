"""The actual scene-comparison verdict: matching output must never hide failed or incomplete readers."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('compare_fbx_scene', ROOT / 'tools/fbx-drill/compare_scene.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class SceneComparisonTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='haf-fbx-verdict-')
        self.addCleanup(self.tmp.cleanup)
        self.left, self.right = (Path(self.tmp.name) / n for n in ('cs.txt', 'native.txt'))
        self.good = '=== a.fbx\ncounts nodes=1\n=== b.fbx\ncounts nodes=2\n'
        self.write(self.good, self.good)

    def write(self, left, right):
        self.left.write_bytes(left.encode('utf-8'))
        self.right.write_bytes(right.encode('utf-8'))

    def check(self, count=2, cs=0, native=0):
        MODULE.verify(self.left, self.right, count, cs, native)

    def test_matching_complete_scenes_pass(self):
        self.check()

    def test_matching_output_does_not_hide_failed_processes(self):
        for cs, native in ((7, 0), (0, 7)):
            with self.subTest(cs=cs, native=native), self.assertRaisesRegex(ValueError, 'exited'):
                self.check(cs=cs, native=native)

    def test_both_readers_empty_or_truncated_still_fail(self):
        for text in ('', '=== a.fbx\ncounts nodes=1\n', '=== a.fbx\n=== b.fbx\ncounts nodes=1\n'):
            with self.subTest(text=text), self.assertRaisesRegex(ValueError, 'expected 2'):
                self.write(text, text)
                self.check()

    def test_matching_refusals_are_not_success(self):
        self.write(self.good + 'ERROR bad mesh\n', self.good + 'ERROR bad mesh\n')
        with self.assertRaisesRegex(ValueError, 'refused'):
            self.check()

    def test_changed_data_fails_with_context(self):
        self.write(self.good, self.good.replace('nodes=2', 'nodes=3'))
        with self.assertRaisesRegex(ValueError, 'differs from ufbx'):
            self.check()

    def test_no_selected_population_is_not_a_pass(self):
        self.write('', '')
        with self.assertRaisesRegex(ValueError, 'no scene population'):
            self.check(count=0)

    def test_bom_and_crlf_are_transport_only(self):
        self.write('\ufeff' + self.good.replace('\n', '\r\n'), self.good)
        self.check()


if __name__ == '__main__':
    unittest.main()
