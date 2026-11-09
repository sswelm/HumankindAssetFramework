#!/usr/bin/env python
"""The advisor's deletion proposer (Phase 1 of docs/Workshop-Advisor-Plan.md, "Ignore candidates"): which parts of a
raw, cut or split file to mark X, from the probe's visibility verdict and the part's size against its model.

A part is proposed when the probe calls it INTERIOR (vis = 0: every escape ray from every sampled vertex is blocked,
VehicleProbe.Visibility.cs - the corpus carries it for every file since tools/advisor/probe_vis.sh) and its largest
extent is at most A of the model's, or when it is tiny whatever the verdict: at most B of the model's largest extent.
A and B are learned on the other models by exhaustive search over a small grid, maximising F0.5 - precision weighed
double, because a wrong deletion is a hole in the model the user has to notice, while a missed one costs a click -
and ties go to the STRICTER rule for the same reason. A zero threshold switches its clause off. At write time a part
the user has decided on - split (marked S or T) or put in a fuse group - is not a candidate; the evaluation does not see
the marks.

    python tools/advisor/evaluate.py --corpus <dir> --proposer tools/advisor/proposers/delete.py

or on its own, to write proposals for one model folder, trained on every other model:

    python tools/advisor/proposers/delete.py --corpus <dir> --model "Steam Frigate" [--write]

--write refreshes the X lines of `<file>.glb.marks.proposed.txt` beside each raw, cut or split GLB of the model (the
split proposer's S lines in the same file are kept; an empty proposal clears the old X lines, and so does a run on
which no grid rule finds a training deletion - a rerun must not leave an old proposal behind - but a model it cannot
judge, no visibility verdict on its rows, leaves the files as they are). It never touches the live
`<file>.glb.marks.txt`: the user renames or merges the proposal when they agree with it.
"""
import argparse
import itertools
import math
import os
import sys
from fractions import Fraction

A_GRID = (0.0, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 1.0)                  # interior parts up to this share of the model's largest extent (0: none; 1.0: every interior part)
B_GRID = (0.0, 0.0002, 0.0005, 0.001, 0.002, 0.003, 0.005, 0.01, 0.02)   # any part up to this share (0: none by size alone)
BETA = Fraction(1, 2)
LAST_RULE = {}   # the thresholds the last propose() chose, or the reason it abstained, for tests and for the report


def num(v):
    """A finite number, or None: an empty cell, nan or an infinity is "not measured", never a size."""
    try:
        f = float(v)
    except (TypeError, ValueError):
        return None
    return f if math.isfinite(f) else None


def extent(row):
    """The part's largest extent, or None when it was not measured (a blocked mesh: the corpus leaves its box empty)."""
    es = [e for e in (num(row.get("sx")), num(row.get("sy")), num(row.get("sz"))) if e is not None]
    return max(es) if es else None


def file_extents(rows):
    """The largest extent of any part per file - the model's size the parts are measured against."""
    out = {}
    for r in rows:
        e = extent(r)
        if e is not None and e > out.get(r["file"], 0.0):
            out[r["file"]] = e
    return out


def relative_size(row, extents):
    """The part's largest extent as a share of its file's; None when the part's is not known."""
    own = extent(row)
    if own is None:
        return None
    e = extents.get(row["file"], 0.0)
    return own / e if e > 0 else 1.0


def candidate(row, rel, a, b):
    """A threshold of 0 switches its clause off: a zero-extent part (a point) is not proposed by a rule that says
    "none". A part whose size is not known (rel None) is never a candidate: unknown is not small."""
    if rel is None:
        return False
    interior = str(row.get("vis", "")) == "0"
    return (interior and a > 0 and rel <= a) or (b > 0 and rel <= b)


def f_beta(tp, fp, fn, beta=BETA):
    """Exact: (1 + beta^2) tp / ((1 + beta^2) tp + beta^2 fn + fp) as a Fraction, so two rules with the same score tie
    and the tie rules decide, instead of the last bit of a float."""
    if tp == 0:
        return Fraction(0)
    b2 = Fraction(beta) * Fraction(beta)
    return (1 + b2) * tp / ((1 + b2) * tp + b2 * fn + fp)


def learn(train, beta=BETA):
    """(a, b) maximising F-beta on the training rows (which carry 'truth'); ties go to the stricter rule, the one that
    proposes the fewest training parts. None when no grid rule finds a training deletion: abstain."""
    extents = file_extents(train)
    rows = [(r, relative_size(r, extents), r.get("truth") == "X") for r in train]
    if not any(t for _, _, t in rows):
        return None
    best = None
    for a, b in itertools.product(A_GRID, B_GRID):
        tp = fp = fn = 0
        for r, rel, truth in rows:
            if candidate(r, rel, a, b):
                if truth:
                    tp += 1
                else:
                    fp += 1
            elif truth:
                fn += 1
        # ties: the rule that proposes the fewest training parts, then the smaller size-alone threshold, then the smaller interior one
        key = (f_beta(tp, fp, fn, beta), -(tp + fp), -b, -a)
        if best is None or key > best[0]:
            best = (key, (a, b))
    return best[1] if best and best[0][0] > 0 else None


NEEDED = ("vis", "sx", "sy", "sz", "file")


def has_verdict(rows):
    """-1 is the Lab's "unclassified": no verdict either."""
    return any(str(r.get("vis", "")) not in ("", "-1") for r in rows)


def propose(task, test, train, test_model, train_models):
    LAST_RULE.clear()   # before the task check: a note from an earlier call must not read as this task's
    if task not in ("delete", "delete-hand"):
        return {}
    for r in test[:1] + train[:1]:
        missing = [c for c in NEEDED if c not in r]
        if missing:   # loud, not an empty proposal: an export older than the features, or a row stripped of them
            raise ValueError("delete proposer: rows lack %s - rebuild the corpus (tools/advisor/corpus.py --probe ...) or list the columns in evaluate.PART_FEATURES" % ", ".join(missing))
    if not has_verdict(test) and not has_verdict(train):
        # no verdict anywhere: the export was made without --probe; loud, not 26 empty folds
        raise ValueError("delete proposer: no visibility verdict on any row - export the corpus with --probe (tools/advisor/probe_vis.sh)")
    if test and not has_verdict(test):
        # one model's files are all stale or failed in the probe table and have no recipe: abstain on that fold, say
        # why, and let the other folds run
        LAST_RULE["abstained"] = "no visibility verdict on the held-out rows (stale or failed in the probe table, no recipe)"
        LAST_RULE["unseen"] = True   # it could not look, as against looking and finding nothing
        return {}
    rule = learn(train)
    if rule is None:
        LAST_RULE["abstained"] = "no grid rule finds a training deletion"
        return {}
    a, b = rule
    LAST_RULE.update({"a": a, "b": b, "model": test_model.get("model", "")})
    extents = file_extents(test)
    return {(r["file"], r["node_index"] if r["node_index"] != "" else r["name"]): ("X" if candidate(r, relative_size(r, extents), a, b) else "") for r in test}


def main(argv=None):
    here = os.path.dirname(os.path.abspath(__file__))
    sys.path.insert(0, os.path.join(here, ".."))
    sys.path.insert(0, here)
    import evaluate  # noqa: E402
    import common  # noqa: E402

    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--corpus", required=True, help="the folder corpus.py exported (parts.csv, models.csv)")
    ap.add_argument("--model", required=True, help="the model folder to propose for; every other model trains the rule")
    ap.add_argument("--models-root", default="D:/3DModels", help="where the model folders are; the proposals file is written beside each GLB (default D:/3DModels)")
    ap.add_argument("--write", action="store_true", help="write the X lines of <glb>.marks.proposed.txt beside each raw/cut/split GLB of the model")
    a = ap.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")   # part names outside the console's code page print instead of crashing
    parts, models = evaluate.load_corpus(a.corpus)
    population = evaluate.population("delete-hand", parts)
    train = [r for r in population if r["model"] != a.model]
    test = [r for r in parts if r["model"] == a.model and r["stage"] in ("raw", "cut", "split")]
    if not test:
        print("no raw, cut or split parts of model '%s' in the corpus" % a.model)
        return 1
    if a.write and not os.path.isdir(os.path.join(a.models_root, a.model)):
        print("model folder not found under --models-root: %s" % os.path.join(a.models_root, a.model))
        return 1
    try:
        preds = propose("delete", test, train, {"model": a.model}, [])
    except ValueError as e:
        print(e)
        return 1
    if "a" in LAST_RULE:
        print("rule learned on %d parts of %d models: interior parts up to %g of the model's largest extent, any part up to %g"
              % (len(train), len(set(r["model"] for r in train)), LAST_RULE["a"], LAST_RULE["b"]))
    else:
        print("no proposals: %s" % LAST_RULE.get("abstained", "no rule"))
        if LAST_RULE.get("unseen"):
            # it could not look: the earlier proposals stand
            if a.write:
                print("no verdict to judge by: the proposals files are left as they are")
            return 0
        if not a.write:
            return 0
        # no rule learned on the other models: the X lines are cleared, as the split proposer clears its S lines - a
        # rerun must not leave an old proposal behind (the live marks are never touched)
    by_file = {}
    for r in test:
        rows = by_file.setdefault(r["file"], [])
        key = (r["file"], r["node_index"] if r["node_index"] != "" else r["name"])
        # the user's own decisions on the part stand: a split (S, T) or a fuse group (a letter) is a kept part
        if preds.get(key) == "X" and r.get("mark") not in ("S", "T") and not r.get("fuse_letter"):
            rows.append(r)
    for rel, rows in sorted(by_file.items()):
        print("%s: %d proposal(s)" % (rel, len(rows)))
        for r in rows:
            if r.get("mark"):
                agree = " (marked %s)" % r["mark"]
            elif r.get("mark_later") == "X":
                agree = " (marked X at a later stage)"
            elif r.get("role") == "Ignore":
                agree = " (role Ignore in the recipe)"
            else:
                agree = ""
            print("  X|%s|%s  %s verts, vis %s%s" % (r["node_index"], r["name"], r["verts"], r["vis"], agree))
        if a.write:
            path = os.path.join(a.models_root, rel) + ".marks.proposed.txt"
            try:
                common.merge_proposals(path, ("X",), ["X|%s|%s" % (r["node_index"], r["name"]) for r in rows], "delete.py")
            except ValueError as e:
                print("  %s" % e)
                return 1
            print("  written: %s" % path.replace("\\", "/"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
