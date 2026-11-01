"""Negative coverage cases must not retain a green FBX model parity result."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools/fbx-drill'))
from model_verdict import verdict


class ModelVerdictTests(unittest.TestCase):
    def test_complete_mixed_population(self):
        text = 'SKIP source : bones\nPASS fixture : exact\nRESULT compared=1 skipped=1 failed=0\n'
        self.assertEqual(0, verdict(text, 0, ['source', 'fixture'], 1)[0])

    def test_all_skipped_is_skip_and_a_required_fixture_skip_is_failure(self):
        text = 'SKIP source : bones\nRESULT compared=0 skipped=1 failed=0\n'
        self.assertEqual(3, verdict(text, 3, ['source'], 1)[0])
        self.assertEqual(1, verdict(text, 0, ['source'], 1)[0])
        self.assertEqual(1, verdict(text, 3, ['source'], 0)[0])

    def test_incomplete_extra_reordered_or_fabricated_coverage(self):
        text = 'PASS source : exact\nRESULT compared=1 skipped=0 failed=0\n'
        for expected in ([], ['source', 'lost'], ['other']):
            self.assertEqual(1, verdict(text, 0, expected, len(expected))[0])
        for bad in (text.replace('compared=1', 'compared=2'), text + 'RESULT compared=1 skipped=0 failed=0\n',
                    'RESULT compared=0 skipped=0 failed=0\n', text.replace('PASS source', 'PASS'), ''):
            self.assertEqual(1, verdict(bad, 0, ['source'], 1)[0])

    def test_nonzero_comparator_exit_and_differences_fail(self):
        text = 'PASS source : exact\nRESULT compared=1 skipped=0 failed=0\n'
        self.assertEqual(1, verdict(text, 1, ['source'], 1)[0])
        for kind in ('DIFF', 'ERROR'):
            self.assertEqual(1, verdict(f'{kind} source : broken\nRESULT compared=0 skipped=0 failed=1\n', 1, ['source'], 1)[0])


if __name__ == '__main__':
    unittest.main()
