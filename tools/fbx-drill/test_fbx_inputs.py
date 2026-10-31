"""FBX input coverage: both source slots, BOM recipes, project-relative paths, failures and missing populations."""
import json
from pathlib import Path
import tempfile
import unittest
from fbx_inputs import collect


class FbxInputTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        self.recipes = self.root / 'Assets/FactorySource/VehicleLab/Recipes'
        self.recipes.mkdir(parents=True)

    def recipe(self, value):
        (self.recipes / 'model.json').write_text(json.dumps(value), encoding='utf-8-sig')

    def test_both_source_slots_bom_and_project_relative_paths_are_included(self):
        first, second = self.root / 'first.fbx', self.root / 'second.FBX'
        first.write_bytes(b'first'); second.write_bytes(b'second')
        self.recipe({'srcFile': str(first), 'srcFile2': 'second.FBX'})
        files, sources, missing = collect(self.root)
        self.assertEqual(files, [])
        self.assertEqual(sources, [first.as_posix(), second.as_posix()])
        self.assertEqual(missing, [])

    def test_sources_already_in_the_project_or_both_slots_are_only_drilled_once(self):
        source = self.root / 'Assets/source.FBX'
        source.write_bytes(b'input')
        self.recipe({'srcFile': str(source), 'srcFile2': str(source)})
        files, sources, missing = collect(self.root)
        self.assertEqual(files, [source.as_posix()])
        self.assertEqual(sources, [])
        self.assertEqual(missing, [])

    def test_missing_optional_sources_are_named(self):
        self.recipe({'srcFile': 'missing.fbx', 'srcFile2': 'missing.glb'})
        files, sources, missing = collect(self.root)
        self.assertEqual(files + sources, [])
        self.assertEqual(len(missing), 1)
        self.assertIn('srcFile is unavailable, NOT drilled: missing.fbx', missing[0])

    def test_malformed_recipes_do_not_silently_remove_a_population(self):
        path = self.recipes / 'model.json'
        for value in ('{broken', '[]', '{"srcFile":42}', '{"srcFile":0}'):
            with self.subTest(value=value):
                path.write_text(value, encoding='utf-8')
                with self.assertRaisesRegex(ValueError, 'cannot enumerate recipe inputs'):
                    collect(self.root)

    def test_no_project_is_an_explicit_empty_population(self):
        self.assertEqual(collect(self.root / 'absent'), ([], [], []))


if __name__ == '__main__':
    unittest.main()
