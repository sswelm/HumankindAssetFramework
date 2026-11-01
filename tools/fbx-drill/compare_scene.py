"""Fail closed on scene-reader exits, incomplete populations, refusal rows, or different oracle dumps."""
import difflib
from itertools import islice
from pathlib import Path
import sys


def verify(left, right, expected_files, managed_rc=0, oracle_rc=0):
    if managed_rc != 0 or oracle_rc != 0:
        raise ValueError(f'scene readers exited C#={managed_rc}, ufbx={oracle_rc}')
    if expected_files <= 0:
        raise ValueError('no scene population selected')
    texts = []
    for label, path in (('C#', left), ('ufbx', right)):
        text = Path(path).read_text(encoding='utf-8-sig')
        rows = text.splitlines()
        headers = sum(row.startswith('=== ') for row in rows)
        counts = sum(row.startswith('counts ') for row in rows)
        if headers != expected_files or counts != expected_files:
            raise ValueError(f'{label} dumped {headers} headers and {counts} scene counts, expected {expected_files} of each')
        errors = [row for row in rows if row.startswith('ERROR ')]
        if errors:
            raise ValueError(label + ' refused a scene: ' + errors[0])
        texts.append(text)
    if texts[0] != texts[1]:
        difference = ''.join(islice(difflib.unified_diff(texts[0].splitlines(keepends=True), texts[1].splitlines(keepends=True),
                                                        fromfile='C#', tofile='ufbx'), 24))
        raise ValueError('object layer differs from ufbx:\n' + difference)


if __name__ == '__main__':
    try:
        verify(sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5]))
    except (OSError, ValueError) as error:
        print('FAIL - FBX scene comparison: ' + str(error))
        sys.exit(1)
