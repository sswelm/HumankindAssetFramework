"""Merge the outputs of the prep drill's C# side when it ran as several processes, one per shard of the files
(tools/prep_drill.sh, PREP_JOBS): the per-run lines (FAIL, PASS, LEFT) pass through; RUNTIME must be the same in
every shard and is printed once; NOTE, COVER and TOTAL are sums. A shard that printed no TOTAL (it crashed) is a FAIL
line of its own, so a lost shard cannot pass as "nothing failed".

usage: merge_shards.py <shard output>...
"""
import collections
import re
import sys

runtime = set()
notes = collections.OrderedDict()
cover = collections.OrderedDict()
total = collections.OrderedDict()
converter = None
lost = 0
for path in sys.argv[1:]:
    seen_total = False
    for line in open(path, encoding="utf-8", errors="replace").read().replace("﻿", "").splitlines():
        line = line.rstrip("\r")
        if line.startswith("RUNTIME"):
            runtime.add(line)
        elif line.startswith("CONVERTER"):
            converter = line
        elif line.startswith("NOTE "):
            m = re.match(r"NOTE (\d+) (.*)", line)
            notes[m.group(2)] = notes.get(m.group(2), 0) + int(m.group(1))
        elif line.startswith("COVER "):
            m = re.match(r"COVER (\d+) (.*)", line)
            cover[m.group(2)] = cover.get(m.group(2), 0) + int(m.group(1))
        elif line.startswith("TOTAL "):
            seen_total = True
            words = line.split()[1:]
            for key, value in zip(words[0::2], words[1::2]):
                total[key] = total.get(key, 0) + int(value)
        elif line.strip():
            print(line)
    if not seen_total:
        lost += 1
        print("FAIL a shard of the drill gave no total (%s): it did not finish" % path.replace("\\", "/").split("/")[-1])
for line in sorted(runtime):
    print(line)
if len(runtime) != 1:
    print("FAIL the shards did not run in one and the same runtime (%d different RUNTIME lines)" % len(runtime))
    lost += 1
if converter:
    print(converter)
for text, n in notes.items():
    print("NOTE %d %s" % (n, text))
for text in sorted(cover):
    print("COVER %d %s" % (cover[text], text))
if total:
    total["failed"] = total.get("failed", 0) + lost
    print("TOTAL " + " ".join("%s %d" % kv for kv in total.items()))
sys.exit(1 if lost else 0)
