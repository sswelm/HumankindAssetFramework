#!/usr/bin/env python
"""Carry a Vehicle Lab recipe across a Model Workshop fuse (2026-10-20, the Khelandion: fused after its recipe was marked).

The fuse joins the parts of every (+) group into one part with a new name ('Fused_A_<first member>'); the parts outside any
group keep their names. This rewrites the recipe for the fused source: an untouched part keeps its entry, each group becomes
ONE entry carrying its members' role (the majority; a mixed group is reported and must be looked at), the dials and the
clip settings are untouched, srcFile and outGlb point at the fused file. The probe fields (verts, box, visibility, bone,
flip) of a fused entry are its first member's and are refreshed by the Lab's Probe on the next load.

    python tools/recipe_migrate.py <recipe.json> <fused.glb.fuse-report.txt> <new recipe.json>

Exit 0 when every group resolved cleanly, 3 when a group mixed roles or carried different placements (the new recipe is
written either way; the report says what to look at), 1 on a malformed input.
"""
import io
import json
import os
import re
import sys
from collections import Counter

ROLE_NAMES = {0: "Body", 1: "Wheel", 2: "Turret", 3: "Ignore", 4: "Default", 5: "Edgecase", 6: "Caterpillar", 7: "Gun", 8: "Rotor", 9: "TailRotor",
              10: "Trail", 11: "Muzzle", 12: "Cradle", 13: "Oar", 14: "Sail", 15: "Rigging", 16: "Structure", 17: "Flag", 18: "Rudder",
              19: "Preserve", 20: "Flip", 21: "Detail", 22: "Shroud", 23: "Window"}


def parse_report(text):
    """The report's groups: [(letter, fused name, [member names])], and the output path it names."""
    output = None
    groups = []
    letter = members = None
    for line in text.splitlines():
        if line.startswith("output: "):
            output = line[len("output: "):].strip()
        m = re.match(r"^== group (\S+) ", line)
        if m:
            letter, members = m.group(1), None
            continue
        if line.startswith("parts: ") and letter is not None:
            members = [p.strip() for p in line[len("parts: "):].split(",") if p.strip()]
            continue
        m = re.match(r"^Fused \d+ part\(s\) -> '([^']+)'", line)
        if m and letter is not None and members is not None:
            groups.append((letter, m.group(1), members))
            letter = members = None
    return output, groups


def migrate(recipe, report_text, out_glb=None):
    """The new recipe and the notes; raises ValueError on a malformed report."""
    output, groups = parse_report(report_text)
    if output is None or not groups:
        raise ValueError("not a fuse report (no 'output:' line or no fused group)")
    by_name = {}
    for p in recipe.get("parts", []):
        by_name.setdefault(p["name"], p)
    member_of = {}
    for letter, fused, members in groups:
        for n in members:
            member_of[n] = fused
    new = dict(recipe)
    new["srcFile"] = output.replace("\\", "/")
    old_out = recipe.get("outGlb") or ""
    new["outGlb"] = out_glb.replace("\\", "/") if out_glb else os.path.splitext(new["srcFile"])[0] + "_Spin.glb"
    notes = []
    problems = 0
    parts = []
    seen_groups = set()
    kept = 0
    for p in recipe.get("parts", []):
        fused = member_of.get(p["name"])
        if fused is None:
            parts.append(p)
            kept += 1
            continue
        if fused in seen_groups:
            continue
        seen_groups.add(fused)
        letter, _, members = next(g for g in groups if g[1] == fused)
        present = [by_name[n] for n in members if n in by_name]
        missing = [n for n in members if n not in by_name]
        roles = Counter(q["role"] for q in present)
        role, _ = roles.most_common(1)[0]
        entry = dict(present[0])
        entry["name"] = fused
        entry["role"] = role
        tally = ", ".join("%s x%d" % (ROLE_NAMES.get(r, r), c) for r, c in roles.most_common())
        if len(roles) > 1:
            problems += 1
            notes.append("MIXED  group %s -> '%s': %s; took %s - check it" % (letter, fused, tally, ROLE_NAMES.get(role, role)))
        else:
            notes.append("group  %s -> '%s': %d part(s) as %s" % (letter, fused, len(present), ROLE_NAMES.get(role, role)))
        if missing:
            notes.append("       %d member(s) of group %s were not in the recipe (new to it): %s" % (len(missing), letter, ", ".join(missing[:6]) + (" ..." if len(missing) > 6 else "")))
        placements = {(json.dumps(q.get("offset"), sort_keys=True), json.dumps(q.get("scale"), sort_keys=True)) for q in present}
        if len(placements) > 1:
            problems += 1
            notes.append("PLACED group %s: its members carried different placements; the first member's kept - re-place it" % letter)
        parts.append(entry)
    new["parts"] = parts
    summary = "%d part(s) kept by name, %d group(s) -> %d fused entr%s, %d to check; srcFile -> %s" % (
        kept, len(groups), len(seen_groups), "y" if len(seen_groups) == 1 else "ies", problems, new["srcFile"])
    if old_out and old_out != new["outGlb"]:
        notes.append("outGlb %s -> %s" % (old_out, new["outGlb"]))
    unfused_groups = [g for g in groups if g[1] not in seen_groups]
    if unfused_groups:
        notes.append("%d group(s) had no member in the recipe and get no entry (the Lab's Probe lists them): %s" % (
            len(unfused_groups), ", ".join(g[1] for g in unfused_groups[:6]) + (" ..." if len(unfused_groups) > 6 else "")))
    return new, summary, notes, problems


def main(argv):
    if len(argv) != 4:
        print(__doc__)
        return 1
    recipe_path, report_path, out_path = argv[1:]
    try:
        recipe = json.load(io.open(recipe_path, encoding="utf-8"))
        report = io.open(report_path, encoding="utf-8").read()
        new, summary, notes, problems = migrate(recipe, report)
    except (OSError, ValueError, KeyError) as e:
        print("FAIL: %s" % e)
        return 1
    with io.open(out_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(new, f, indent=1)
        f.write("\n")
    print("MIGRATED " + summary)
    for n in notes:
        print("  " + n)
    print("written %s - load it in the Vehicle Lab, Probe parts, re-place any PLACED group, then Generate" % out_path)
    return 3 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
