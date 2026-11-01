"""Exercise the actual compiled comparator against damaged exporter evidence; restore its oracle afterward."""
import json
from pathlib import Path
import subprocess
import sys


def main():
    exe, fbx, glb = sys.argv[1:]
    path = Path(glb + '.images.json')
    original = path.read_bytes()

    def compare(expected):
        result = subprocess.run([exe, fbx, glb], capture_output=True, encoding='utf-8-sig', errors='replace', timeout=30)
        summary = f'RESULT compared={1 if expected == 0 else 0} skipped=0 failed={0 if expected == 0 else 1}'
        if result.returncode != expected or summary not in result.stdout.splitlines():
            raise AssertionError(f'expected comparator exit {expected}: {result.returncode}: {result.stdout[:500]}')

    cases = {
        'hash': lambda d: d['images'][0].update(sha256='0' * 64),
        'name': lambda d: d['images'][0].update(name='other'),
        'MIME': lambda d: d['images'][0].update(mimeType='image/jpeg'),
        'composition': lambda d: d['images'][0].update(composed=[False]),
        'duplicate decision': lambda d: d['images'][0].update(composed=[True, True]),
        'empty decision': lambda d: d['images'][0].update(composed=[]),
        'invalid decision': lambda d: d['images'][0].update(composed=['true']),
        'count': lambda d: d.update(images=[]),
        'version': lambda d: d.update(version=2),
    }
    try:
        compare(0)
        for name, mutate in cases.items():
            doc = json.loads(original)
            mutate(doc)
            path.write_text(json.dumps(doc), encoding='utf-8')
            compare(1)
        path.unlink()
        compare(1)
        path.write_text('{broken', encoding='utf-8')
        compare(1)
    finally:
        path.write_bytes(original)
    compare(0)
    print(f'PASS — image oracle evidence: {len(cases) + 2} damaged/missing records rejected; intact record passes before and after')


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    try:
        main()
    except (AssertionError, OSError, ValueError, subprocess.SubprocessError) as error:
        print('FAIL — image oracle evidence: ' + str(error))
        sys.exit(1)
