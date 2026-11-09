#!/usr/bin/env python
"""The Split proposer (Phase 1 of docs/Workshop-Advisor-Plan.md): which parts of a raw or cut file to mark S.

A deterministic rule with thresholds learned from the other models: propose a split for a part that is not blocked,
has at least K islands after the Workshop's 1 % distance merge (the count the user reads in the Cutter's row), at
least T triangles, and at least B islands that each hold 1 % or more of the part's triangles. K, T and B are chosen
on the training models by exhaustive search over a small grid, maximising F1, so a proposal never depends on the
model it is judged on. F1 over F0.5 was measured, not assumed (2026-11-09, leave-one-model-out on the 10 marked
files): F0.5 gave 72 % precision at 68 % recall, F1 74 % at 78 %; a within-file size percentile as a fourth
threshold changed nothing under F1 and cost recall under F0.5, so the rule stays at three absolute thresholds.

Used by the evaluator as a proposer module:

    python tools/advisor/evaluate.py --corpus <dir> --proposer tools/advisor/proposers/split.py

or on its own, to write proposals for one model folder, trained on every other model in the corpus:

    python tools/advisor/proposers/split.py --corpus <dir> --model "Steam Frigate" [--write]

--write refreshes `<file>.glb.marks.proposed.txt` beside each raw/cut GLB of the model, including empty proposals,
in the sidecar's v2 format. It never touches the live `<file>.glb.marks.txt`: the user renames or merges the proposal
when they agree with it.
"""
import argparse
import itertools
import os
import sys
from fractions import Fraction

K_GRID = (2, 3, 4, 5, 6, 8, 12)
T_GRID = (0, 100, 200, 500, 1000, 2000, 5000, 10000, 20000)
B_GRID = (0, 2, 3, 4)
LAST_RULE = {}   # the thresholds the last propose() chose, for tests and for the report


def num(v, default=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def candidate(row, k, t, b):
    return (not row.get("blocked")) and num(row.get("islands_merged")) >= k and num(row.get("tris")) >= t and num(row.get("islands_big")) >= b


def f_beta(tp, fp, fn, beta=1):
    """Exact: (1 + beta^2) tp / ((1 + beta^2) tp + beta^2 fn + fp) as a Fraction, so two rules with the same score tie
    and the tie rules decide, instead of the last bit of a float."""
    if tp == 0:
        return Fraction(0)
    b2 = Fraction(beta) * Fraction(beta)
    return (1 + b2) * tp / ((1 + b2) * tp + b2 * fn + fp)


def learn(train):
    """(k, t, b) maximising F1 on the training rows (which carry 'truth'); ties go to the loosest rule, the one
    that stops at the negatives' edge rather than the positives', so an unseen split a little smaller than the
    training ones is still proposed. None means no grid rule found a training split: abstain in that case."""
    positives = [r for r in train if r.get("truth") in ("S", "T")]
    if not positives:
        return None
    negatives = [r for r in train if r.get("truth") not in ("S", "T")]
    best = None
    for k, t, b in itertools.product(K_GRID, T_GRID, B_GRID):
        tp = sum(1 for r in positives if candidate(r, k, t, b))
        fp = sum(1 for r in negatives if candidate(r, k, t, b))
        fn = len(positives) - tp
        score = f_beta(tp, fp, fn)
        key = (score, -k, -t, -b)
        if best is None or key > best[0]:
            best = (key, (k, t, b))
    return best[1] if best and best[0][0] > 0 else None


NEEDED = ("islands_merged", "islands_big", "tris", "blocked")


def propose(task, test, train, test_model, train_models):
    if task != "split":
        return {}
    for r in test[:1] + train[:1]:
        missing = [c for c in NEEDED if c not in r]
        if missing:   # loud, not an empty proposal: an export older than the features, or a row stripped of them
            raise ValueError("split proposer: rows lack %s - rebuild the corpus (tools/advisor/corpus.py) or list the columns in evaluate.PART_FEATURES" % ", ".join(missing))
    rule = learn(train)
    LAST_RULE.clear()
    if rule is None:
        LAST_RULE["abstained"] = "no grid rule finds a training split"
        return {}
    k, t, b = rule
    LAST_RULE.update({"k": k, "t": t, "b": b, "model": test_model.get("model", "")})
    return {(r["file"], r["node_index"] if r["node_index"] != "" else r["name"]): ("S" if candidate(r, k, t, b) else "") for r in test}


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
    ap.add_argument("--write", action="store_true", help="write <glb>.marks.proposed.txt beside each raw/cut GLB of the model")
    a = ap.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")   # part names outside the console's code page print instead of crashing
    parts, models = evaluate.load_corpus(a.corpus)
    population = evaluate.population("split", parts)
    train = [r for r in population if r["model"] != a.model]
    # Include blocked rows so a file that no longer has any eligible parts still clears its old proposal.
    test = [r for r in parts if r["model"] == a.model and r["stage"] in ("raw", "cut")]
    if not test:
        print("no raw or cut parts of model '%s' in the corpus" % a.model)
        return 1
    preds = propose("split", test, train, {"model": a.model}, [])
    if "k" in LAST_RULE:
        print("rule learned on %d parts of %d models: islands after merge >= %d, triangles >= %d, islands of 1%% or more >= %d"
              % (len(train), len(set(r["model"] for r in train)), LAST_RULE["k"], LAST_RULE["t"], LAST_RULE["b"]))
    else:
        print("no split proposals: %s" % LAST_RULE.get("abstained", "no rule"))
    by_file = {}
    for r in test:
        rows = by_file.setdefault(r["file"], [])
        key = (r["file"], r["node_index"] if r["node_index"] != "" else r["name"])
        if preds.get(key) == "S":
            rows.append(r)
    for rel, rows in sorted(by_file.items()):
        print("%s: %d proposal(s)" % (rel, len(rows)))
        for r in rows:
            agree = " (marked %s)" % r["mark"] if r.get("mark") in ("S", "T") else (" (marked X)" if r.get("mark") == "X" else "")
            print("  S|%s|%s  %s tris, %s islands after merge%s" % (r["node_index"], r["name"], r["tris"], r["islands_merged"], agree))
        if a.write:
            # the S lines of the one proposals file; another proposer's letters (delete.py's X) stay
            path = os.path.join(a.models_root, rel) + ".marks.proposed.txt"
            try:
                common.merge_proposals(path, ("S",), ["S|%s|%s" % (r["node_index"], r["name"]) for r in rows], "split.py")
            except ValueError as e:
                print("  %s" % e)
                return 1
            print("  written: %s" % path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
