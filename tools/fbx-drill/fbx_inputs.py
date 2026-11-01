"""Enumerate the FBX drill's real inputs. Failed scans/recipes fail; unavailable sources are named, never hidden."""
import json
import os
from pathlib import Path
import sys


def collect(project, include_project=True):
    project = Path(project).resolve()
    assets = project / 'Assets'
    files, sources, missing = [], [], []
    if not assets.is_dir():
        return files, sources, missing

    def scan_error(error):
        raise error

    for directory, dirs, names in os.walk(assets, onerror=scan_error) if include_project else ():
        dirs[:] = sorted(d for d in dirs if d.lower() != 'library')
        files.extend((Path(directory) / n).as_posix() for n in sorted(names) if n.lower().endswith('.fbx'))
    files.sort()
    seen = {os.path.normcase(p) for p in files}
    for path in sorted((assets / 'FactorySource/VehicleLab/Recipes').glob('*.json')):
        try:
            with path.open(encoding='utf-8-sig') as stream:
                recipe = json.load(stream)
            if not isinstance(recipe, dict):
                raise ValueError('recipe is not an object')
            for field in ('srcFile', 'srcFile2'):
                source = recipe.get(field, '')
                if source is None:
                    source = ''
                if not isinstance(source, str):
                    raise ValueError(field + ' is not a path string')
                if not source.lower().endswith('.fbx'):
                    continue
                resolved = Path(source)
                if not resolved.is_absolute():
                    resolved = project / resolved
                resolved = resolved.resolve()
                if not resolved.is_file():
                    missing.append(f'{path.name}: {field} is unavailable, NOT drilled: {source}')
                    continue
                key = os.path.normcase(resolved.as_posix())
                if key not in seen:
                    seen.add(key)
                    sources.append(resolved.as_posix())
        except (OSError, ValueError, TypeError) as error:
            raise ValueError(f'{path}: cannot enumerate recipe inputs: {error}') from error
    return files, sources, missing


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    try:
        files, sources, missing = collect(sys.argv[1], include_project='--recipes-only' not in sys.argv[2:])
        for kind, paths in (('PROJECT', files), ('RECIPE', sources), ('NOTE', missing)):
            for path in paths:
                if '\n' in path or '\r' in path:
                    raise ValueError('input path contains a newline: ' + repr(path))
                print(kind + '\t' + path)
        return 0
    except (OSError, ValueError) as error:
        print('FAIL - FBX input scan: ' + str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
