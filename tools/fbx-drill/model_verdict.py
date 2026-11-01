"""Validate the FBX model comparator's complete transcript, including expected fixture coverage."""
from pathlib import Path
import re
import sys


def verdict(transcript, returncode, files, source_count):
    if not files or not 0 <= source_count <= len(files):
        return 1, 'invalid or empty expected population'
    lines = transcript.splitlines()
    rows = [re.fullmatch(r'(PASS|SKIP|DIFF|ERROR) (.*?) : .+', line) for line in lines[:-1]]
    summary = re.fullmatch(r'RESULT compared=(\d+) skipped=(\d+) failed=(\d+)', lines[-1]) if lines else None
    if summary is None or len(rows) != len(files) or any(row is None for row in rows):
        return 1, 'missing, extra or malformed comparison rows/RESULT'
    counts = [0, 0, 0]
    for i, (row, expected) in enumerate(zip(rows, files)):
        kind, path = row.groups()
        if path != expected:
            return 1, 'comparison path/order differs from expected input: ' + expected
        counts[0 if kind == 'PASS' else 1 if kind == 'SKIP' else 2] += 1
        if i >= source_count:
            # a fixture named *_fallback holds a shape the reader must leave to Blender by name: its row is a SKIP
            wanted = 'SKIP' if expected.endswith('_fallback.fbx') else 'PASS'
            if kind != wanted:
                return 1, ('required fixture did not compare: ' if wanted == 'PASS' else 'fallback fixture was not a named fallback: ') + expected
    if counts != [int(n) for n in summary.groups()]:
        return 1, 'RESULT counts disagree with comparison rows'
    expected_rc = 1 if counts[2] else 3 if not counts[0] else 0
    if returncode != expected_rc:
        return 1, 'comparator exit status disagrees with its results'
    if counts[2]:
        return 1, 'one or more comparisons failed'
    if not counts[0]:
        return 3, 'no FBX model was compared'
    return 0, f'{counts[0]} compared, {counts[1]} named fallbacks, all {len(files) - source_count} fixtures as expected'


def main():
    try:
        rc, message = verdict(Path(sys.argv[1]).read_text(encoding='utf-8-sig'), int(sys.argv[2]), sys.argv[4:], int(sys.argv[3]))
    except (OSError, ValueError, IndexError) as error:
        rc, message = 1, str(error)
    print(('PASS' if rc == 0 else 'SKIP' if rc == 3 else 'FAIL') + ' — FBX model coverage: ' + message)
    return rc


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    sys.exit(main())
