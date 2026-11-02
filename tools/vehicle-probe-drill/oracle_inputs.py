"""Select comparable probe inputs before FBX export, then rewrite only Blender's side.

The C# probe has already run on the original paths. LEFT files/jobs must not cause an
oracle conversion, and every remaining FBX must have an explicit conversion mapping.
"""
import json
from pathlib import Path
import sys


def key(path):
    return path.replace('\\', '/').lower()


def select_inputs(files, jobs, left):
    files = [p for p in files if key(p) not in left]
    jobs = [dict(j) for j in jobs if j['key'] not in left]
    needed = {}; sources = list(files)
    for job in jobs:
        sources.append(job['file'])
        if job.get('merge2'): sources.append(job['merge2'].split('|')[0].strip())
    for path in sources:
        if path.lower().endswith('.fbx'): needed.setdefault(key(path), path)
    return files, jobs, list(needed.values())


def convert_inputs(files, jobs, conversions):
    conversions = {key(p): v for p, v in conversions.items()}
    def converted(path):
        if not path.lower().endswith('.fbx'): return path
        result = conversions.get(key(path))
        if not result: raise ValueError('no FBX oracle GLB for ' + path)
        return result
    output_files = []; output_jobs = []
    for path in files:
        if path.lower().endswith('.fbx'): output_jobs.append({'key': key(path), 'file': converted(path)})
        else: output_files.append(path)
    for original in jobs:
        job = dict(original); job['file'] = converted(job['file'])
        if job.get('merge2'):
            fields = job['merge2'].split('|'); fields[0] = converted(fields[0].strip()); job['merge2'] = '|'.join(fields)
        output_jobs.append(job)
    return output_files, output_jobs


def lines(path):
    return [s for s in Path(path).read_text(encoding='utf-8').splitlines() if s]


def write_lines(path, values):
    Path(path).write_text(''.join(s + '\n' for s in values), encoding='utf-8', newline='\n')


def main(argv):
    mode, files_path, jobs_path = argv[1:4]
    files = lines(files_path); jobs = json.loads(Path(jobs_path).read_text(encoding='utf-8'))
    if mode == 'plan':
        files, jobs, needed = select_inputs(files, jobs, set(lines(argv[4])))
        write_lines(argv[5], files); write_lines(argv[6], needed)
    elif mode == 'apply':
        conversions = dict(row.split('\t') for row in lines(argv[4]))
        files, jobs = convert_inputs(files, jobs, conversions)
        write_lines(files_path, files)
    else: raise ValueError('unknown oracle-input mode: ' + mode)
    Path(jobs_path).write_text(json.dumps(jobs, ensure_ascii=False), encoding='utf-8')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
