#!/usr/bin/env python
"""The advisor's deletion proposer (Phase 1 of docs/Workshop-Advisor-Plan.md, "Ignore candidates"): which parts of a
raw, cut or split file to mark X, from the probe's visibility verdict and the part's size against its model.

A part is proposed when the probe calls it INTERIOR (vis = 0: every escape ray from every sampled vertex is blocked,
VehicleProbe.Visibility.cs - the corpus carries it for every file since tools/advisor/probe_vis.sh) and its largest
extent is at most A of the model's, or when it is tiny whatever the verdict: at most B of the model's largest extent.
A and B are learned on the other models by exhaustive search over a small grid, maximising F0.5 - precision weighed
double, because a wrong deletion is a hole in the model the user has to notice, while a missed one costs a click -
and ties go to the STRICTER rule for the same reason. A part the user splits (marked S or T) is not a candidate.

    python tools/advisor/evaluate.py --corpus <dir> --proposer tools/advisor/proposers/delete.py

or on its own, to write proposals for one model folder, trained on every other model:

    python tools/advisor/proposers/delete.py --corpus <dir> --model "Steam Frigate" [--write]

--write refreshes the X lines of `<file>.glb.marks.proposed.txt` beside each raw, cut or split GLB of the model (the
split proposer's S lines in the same file are kept; an empty proposal clears the old X lines). It never touches the
live `<file>.glb.marks.txt`: the user renames or merges the proposal when they agree with it.
"""
import argparse
import itertools
import os
import sys

A_GRID = (0.0, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 1.0)                  # interior parts up to this share of the model's largest extent (1.0: every interior part)
B_GRID = (0.0, 0.0002, 0.0005, 0.001, 0.002, 0.003, 0.005, 0.01, 0.02)   # any part up to this share (0: none by size alone)
BETA = 0.5
LAST_RULE = {}   # the thresholds the last propose() chose, for tests and for the report


def num(v, default=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def file_extents(rows):
    """The largest extent of any part per file - the model's size the parts are measured against."""
    out = {}
    for r in rows:
        e = max(num(r.get("sx")), num(r.get("sy")), num(r.get("sz")))
        if e > out.get(r["file"], 0.0):
            out[r["file"]] = e
    return out


def relative_size(row, extents):
    e = extents.get(row["file"], 0.0)
    return max(num(row.get("sx")), num(row.get("sy")), num(row.get("sz"))) / e if e > 0 else 1.0


def candidate(row, rel, a, b):
    interior = str(row.get("vis", "")) == "0"
    return (interior and rel <= a) or rel <= b


def f_beta(tp, fp, fn, beta=BETA):
    if tp == 0:
        return 0.0
    p = tp / (tp + fp)
    r = tp / (tp + fn)
    b2 = beta * beta
    return (1 + b2) * p * r / (b2 * p + r)


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


def propose(task, test, train, test_model, train_models):
    if task not in ("delete", "delete-hand"):
        return {}
    for r in test[:1] + train[:1]:
        missing = [c for c in NEEDED if c not in r]
        if missing:   # loud, not an empty proposal: an export older than the features, or a row stripped of them
            raise ValueError("delete proposer: rows lack %s - rebuild the corpus (tools/advisor/corpus.py --probe ...) or list the columns in evaluate.PART_FEATURES" % ", ".join(missing))
    if test and all(str(r.get("vis", "")) == "" for r in test):
        raise ValueError("delete proposer: no visibility verdict on the held-out rows - export the corpus with --probe (tools/advisor/probe_vis.sh)")
    rule = learn(train)
    LAST_RULE.clear()
    if rule is None:
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
    ap.add_argument("--corpus", required=True)
    ap.add_argument("--model", required=True, help="the model folder to propose for; every other model trains the rule")
    ap.add_argument("--models-root", default="D:/3DModels")
    ap.add_argument("--write", action="store_true", help="write the X lines of <glb>.marks.proposed.txt beside each raw/cut/split GLB of the model")
    a = ap.parse_args(argv)
    parts, models = evaluate.load_corpus(a.corpus)
    population = evaluate.population("delete-hand", parts)
    train = [r for r in population if r["model"] != a.model]
    test = [r for r in parts if r["model"] == a.model and r["stage"] in ("raw", "cut", "split")]
    if not test:
        print("no raw, cut or split parts of model '%s' in the corpus" % a.model)
        return 1
    preds = propose("delete", test, train, {"model": a.model}, [])
    if LAST_RULE:
        print("rule learned on %d parts of %d models: interior parts up to %g of the model's largest extent, any part up to %g"
              % (len(train), len(set(r["model"] for r in train)), LAST_RULE["a"], LAST_RULE["b"]))
    else:
        print("no rule found a training deletion; no proposals")
    by_file = {}
    for r in test:
        rows = by_file.setdefault(r["file"], [])
        key = (r["file"], r["node_index"] if r["node_index"] != "" else r["name"])
        if preds.get(key) == "X" and r.get("mark") not in ("S", "T"):
            rows.append(r)
    for rel, rows in sorted(by_file.items()):
        print("%s: %d proposal(s)" % (rel, len(rows)))
        for r in rows:
            agree = " (marked %s)" % r["mark"] if r.get("mark") else ""
            print("  X|%s|%s  %s verts, vis %s%s" % (r["node_index"], r["name"], r["verts"], r["vis"], agree))
        if a.write:
            path = os.path.join(a.models_root, rel) + ".marks.proposed.txt"
            common.merge_proposals(path, ("X",), ["X|%s|%s" % (r["node_index"], r["name"]) for r in rows], "delete.py")
            print("  written: %s" % path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
