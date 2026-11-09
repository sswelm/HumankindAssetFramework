#!/usr/bin/env python
"""The Workshop advisor's metrics and baselines, against the corpus corpus.py exported.

Phase 0 of docs/Workshop-Advisor-Plan.md defines four tasks; each is scored on held-out MODELS (leave-one-model-out
over the folders: a proposer never sees a part of the model it is judged on), predictions pooled over the folds:

    role      per-role precision and recall, on the parts a recipe gives a role (the Lab's rows)
    delete    precision and recall of proposed deletions against the X marks (and the recipe's Ignore role) in
              every file the user judged; a wrong delete is the expensive error, so precision leads
    delete-hand  the same without the parts the Workshop marks X itself on first load (auto_mark): the user's own
              deletion judgement
    split     precision and recall of proposed splits against the user's S and T marks
    group     the fraction of part pairs correctly together or apart against the fuse sidecars (the Rand index), on
              files with at least one letter; an unlettered part is its own group
    dials     per dial the absolute error in percentage points against the saved recipe, and the projected triangle
              count under the proposed dials against the count under the saved ones, both held to the target

A proposer is a Python module with `propose(task, test_rows, train_rows, test_model, train_models)`, returning
{row key: label} for role/delete/split (label '' means no proposal), {row key: group id} for group, and {dial: pct}
for dials; `row_key(row)` is the key. The baselines below use the same contract, so a Phase 1 proposer is one more
module on the command line and nothing else changes.

Held-out rows contain only PART_FEATURES (identity, geometry and probe observations); test_model contains only
model/category/files. Truth, user marks, fuse outcomes, roles, placement edits and saved dials stay in training and
scoring data. Each call receives independent records, so in-place normalization cannot change later folds or truth.

    python tools/advisor/evaluate.py [--corpus tools/advisor/out] [--proposer path/to/module.py] [--target 30000]
                                     [--write baseline.md]

Prints the tables; --write puts them in a Markdown file in the corpus folder. Exit 0; 1 when the corpus is missing.
"""
import argparse
import csv
import importlib.util
import io
import json
import os
import statistics
import sys
from collections import Counter, defaultdict
from copy import deepcopy

# The Vehicle Lab's reduce tiers (VehicleLabWindow.Tier(...)): a role not listed is baked unreduced; Ignore is dropped.
ROLE_DIAL = {"Default": "defaultReducePct", "Rigging": "riggingReducePct", "Shroud": "shroudReducePct", "Structure": "structureReducePct",
             "Body": "bodyReducePct", "Oar": "oarReducePct", "Sail": "sailReducePct", "Rudder": "rudderReducePct", "Wheel": "wheelReducePct",
             "Flip": "flipReducePct", "Preserve": "preserveReducePct", "Detail": "detailReducePct", "Turret": "turretReducePct",
             "Gun": "gunReducePct", "Window": "windowReducePct"}
DIALS = sorted(set(ROLE_DIAL.values()))
# A recipe written before a dial existed carries no key for it; the Lab reads the field's initializer then
# (VehicleLabWindow's recipe class, "absent-key ... the do-nothing default"): rigging 75, structure 50, every other 0.
DIAL_DEFAULT = {"riggingReducePct": 75.0, "structureReducePct": 50.0}
TASKS = ("role", "delete", "delete-hand", "split", "group", "dials")

# Keep answer columns in the corpus for training/scoring, never in the held-out proposer's inputs. Use an allowlist
# so a future label or join column cannot silently become a feature. Visibility/flip/bone are probe observations.
PART_FEATURES = frozenset(("model", "category", "file", "stage", "node_index", "name", "file_name", "mesh_index",
                           "mesh_shared", "parent_index", "depth", "verts", "tris", "islands", "islands_merged", "island_top_pct",
                           "island_second_pct", "islands_big", "cx", "cy", "cz", "sx", "sy", "sz", "blocked", "triangle_free",
                           "vis", "flip", "bone", "vis_from"))
# Every other column of the corpus is a label or a join record and stays out of the held-out rows. A new corpus column
# must be placed in one set or the other (Tests/test_advisor_corpus.py holds the partition), so a feature a proposer
# needs is never silently stripped: the first Split proposer proposed nothing because its columns were not listed here.
PART_LABELS = frozenset(("mark", "auto_mark", "mark_later", "fuse_letter", "fuse_group_name", "fused_into", "fused_letter", "role", "role_id",
                         "placed", "recipe", "recipe_verts", "recipe_name", "name_join", "role_via_fuse", "recipe_via_fuse"))
MODEL_FEATURES = frozenset(("model", "category", "files"))


def features(record, columns):
    return deepcopy({k: v for k, v in record.items() if k in columns})


def row_key(r):
    return (r["file"], r["node_index"] if r["node_index"] != "" else r["name"])


def num(v, default=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def load_corpus(folder):
    with io.open(os.path.join(folder, "parts.csv"), encoding="utf-8", newline="") as f:
        parts = list(csv.DictReader(f))
    with io.open(os.path.join(folder, "models.csv"), encoding="utf-8", newline="") as f:
        models = list(csv.DictReader(f))
    for m in models:
        m["dials_parsed"] = json.loads(m["dials"]) if m.get("dials") else {}
    return parts, models


# ---------------------------------------------------------------- the task populations

def files_with(parts, column, values=None):
    """The files where at least one row has `column` set (to one of `values`, when given)."""
    out = set()
    for r in parts:
        v = r.get(column, "")
        if v and (values is None or v in values):
            out.add(r["file"])
    return out


def deleted(r):
    """The part was deleted: marked X at this file, marked X at a later stage file of the same model, or given the
    Lab's Ignore role."""
    return r["mark"] == "X" or r.get("mark_later", "") == "X" or r["role"] == "Ignore"


def population(task, parts):
    """The rows a task is scored on, with the truth filled in under 'truth'."""
    if task == "role":
        rows = [dict(r, truth=r["role"]) for r in parts if r["role"]]
    elif task == "delete":
        # every file the user judged counts: a Fuse writes the marks sidecar only when something is marked, so a
        # lettered file without one is a file where the user looked and deleted nothing - a true negative, not a gap.
        # A part deleted at a LATER stage file (corpus.carry_later_marks: the user splits at the raw file and deletes at
        # the split file) is a deletion here too; without it the Espana's raw rows read 2,000 deletions as "kept".
        judged = files_with(parts, "mark") | files_with(parts, "fuse_letter") | files_with(parts, "role") | files_with(parts, "mark_later")
        rows = [dict(r, truth="X" if deleted(r) else "") for r in parts
                if r["file"] in judged and r["stage"] != "recipe-only"]
    elif task == "delete-hand":
        # the same files, without the parts the Workshop marks X itself on first load (two triangles or fewer, no
        # group or split of their own): what is left is the user's own deletion judgement
        judged = files_with(parts, "mark") | files_with(parts, "fuse_letter") | files_with(parts, "role") | files_with(parts, "mark_later")
        rows = [dict(r, truth="X" if deleted(r) else "") for r in parts
                if r["file"] in judged and r["stage"] != "recipe-only" and str(r.get("auto_mark", "")) != "1"]   # int from build(), str from the CSV
    elif task == "split":
        # splitting happens on the raw and cut files; a _split file's marks are deletions, its parts are the splits;
        # a blocked part (lines, points, compressed) cannot be split and is no candidate
        marked = files_with(parts, "mark")
        rows = [dict(r, truth="S" if r["mark"] in ("S", "T") else "") for r in parts
                if r["file"] in marked and r["stage"] in ("raw", "cut") and not r["blocked"]]
    elif task == "group":
        lettered = files_with(parts, "fuse_letter")
        rows = [dict(r, truth=r["fuse_letter"] or ("solo:%s" % r["node_index"])) for r in parts if r["file"] in lettered]
    else:
        rows = []
    return rows


# ---------------------------------------------------------------- baselines (the same contract a proposer has)

class Baselines:
    """Named baseline proposers. Each is `(task, test_rows, train_rows, test_model, train_models) -> predictions`."""

    @staticmethod
    def never(task, test, train, tm, tms):
        return {row_key(r): "" for r in test}

    @staticmethod
    def default_role(task, test, train, tm, tms):
        return {row_key(r): "Default" for r in test}

    @staticmethod
    def majority_role(task, test, train, tm, tms):
        c = Counter(r["truth"] for r in train)
        top = c.most_common(1)[0][0] if c else "Default"
        return {row_key(r): top for r in test}

    @staticmethod
    def category_majority_role(task, test, train, tm, tms):
        cat = tm["category"]
        c = Counter(r["truth"] for r in train if r["category"] == cat) or Counter(r["truth"] for r in train)
        top = c.most_common(1)[0][0] if c else "Default"
        return {row_key(r): top for r in test}

    @staticmethod
    def interior(task, test, train, tm, tms):
        """delete every part the probe called interior (vis == 0); only recipe rows carry vis"""
        return {row_key(r): "X" if r["vis"] == "0" else "" for r in test}

    @staticmethod
    def smallest_tenth(task, test, train, tm, tms):
        """delete the tenth of each file's parts with the fewest vertices"""
        out = {}
        by_file = defaultdict(list)
        for r in test:
            by_file[r["file"]].append(r)
        for rows in by_file.values():
            verts = sorted(num(r["verts"]) for r in rows)
            cut = verts[max(0, len(verts) // 10 - 1)] if len(verts) >= 10 else -1
            for r in rows:
                out[row_key(r)] = "X" if num(r["verts"]) <= cut else ""
        return out

    @staticmethod
    def multi_island(task, test, train, tm, tms):
        """split every part with more than one island"""
        return {row_key(r): "S" if num(r["islands"]) > 1 else "" for r in test}

    @staticmethod
    def multi_island_large(task, test, train, tm, tms):
        """split a multi-island part in the upper half of its file by vertices"""
        out = {}
        by_file = defaultdict(list)
        for r in test:
            by_file[r["file"]].append(r)
        for rows in by_file.values():
            med = statistics.median(num(r["verts"]) for r in rows) if rows else 0
            for r in rows:
                out[row_key(r)] = "S" if num(r["islands"]) > 1 and num(r["verts"]) >= med else ""
        return out

    @staticmethod
    def all_apart(task, test, train, tm, tms):
        return {row_key(r): "solo:%s" % row_key(r)[1] for r in test}

    @staticmethod
    def all_together(task, test, train, tm, tms):
        return {row_key(r): "all" for r in test}

    @staticmethod
    def same_material(task, test, train, tm, tms):
        """group the parts a rip named after the same material: the name before the Cutter's _N suffix and the
        Split's _Part_NNN suffix"""
        out = {}
        for r in test:
            base = r["file_name"] or r["name"]
            for suffix in ("_Part_",):
                if suffix in base:
                    base = base.split(suffix)[0]
            out[row_key(r)] = "mat:%s" % base
        return out

    @staticmethod
    def median_dials(task, test, train, tm, tms):
        out = {}
        for d in DIALS:
            vals = [saved_dial(m, d) for m in tms]
            out[d] = statistics.median(vals) if vals else 0.0
        return out

    @staticmethod
    def category_median_dials(task, test, train, tm, tms):
        out = {}
        for d in DIALS:
            vals = [saved_dial(m, d) for m in tms if m["category"] == tm["category"]] or [saved_dial(m, d) for m in tms]
            out[d] = statistics.median(vals) if vals else 0.0
        return out


def saved_dial(model, dial):
    """A recipe's dial as the Lab reads it: a key the recipe does not carry is the field's initializer (DIAL_DEFAULT)."""
    return num(model["dials_parsed"].get(dial, DIAL_DEFAULT.get(dial, 0.0)))


BASELINES = {
    "role": [("always Default", Baselines.default_role), ("majority role of the other models", Baselines.majority_role),
             ("majority role of the other models in the category", Baselines.category_majority_role)],
    "delete": [("never delete", Baselines.never), ("delete every interior part (vis = 0)", Baselines.interior),
               ("delete the smallest tenth of each file", Baselines.smallest_tenth)],
    "delete-hand": [("never delete", Baselines.never), ("delete every interior part (vis = 0)", Baselines.interior),
                    ("delete the smallest tenth of each file", Baselines.smallest_tenth)],
    "split": [("never split", Baselines.never), ("split every multi-island part", Baselines.multi_island),
              ("split a multi-island part above the file's median size", Baselines.multi_island_large)],
    "group": [("every part alone", Baselines.all_apart), ("everything in one group", Baselines.all_together),
              ("one group per material name", Baselines.same_material)],
    "dials": [("median dial of the other models", Baselines.median_dials), ("median dial of the other models in the category", Baselines.category_median_dials)],
}


# ---------------------------------------------------------------- scoring

def leave_one_model_out(task, rows, models, proposer):
    """Pooled predictions {row key: label} over every fold; for dials {model: {dial: pct}}."""
    by_model = defaultdict(list)
    for r in rows:
        by_model[r["model"]].append(r)
    model_of = {m["model"]: m for m in models}
    preds = {}
    if task == "dials":
        for m in models:
            if not m["dials_parsed"]:
                continue
            others = [o for o in models if o["model"] != m["model"] and o["dials_parsed"]]
            preds[m["model"]] = proposer(task, [], [], features(m, MODEL_FEATURES), deepcopy(others))
        return preds
    for model, test in by_model.items():
        train = [r for r in rows if r["model"] != model]
        tm = model_of.get(model, {"model": model, "category": test[0]["category"]})
        tms = [o for o in models if o["model"] != model]
        # A proposer may normalize its arguments in place. No fold may mutate scoring truth or a later fold's input.
        # Part records contain scalar CSV cells; model records also contain nested dials_parsed dictionaries.
        preds.update(proposer(task, [features(r, PART_FEATURES) for r in test], [dict(r) for r in train],
                              features(tm, MODEL_FEATURES), deepcopy(tms)))
    return preds


def precision_recall(rows, preds, positive=None):
    """(precision, recall, tp, fp, fn) pooled; `positive` names the label class, else every non-empty truth label is
    scored per class and the dict per class is returned."""
    if positive is not None:
        tp = fp = fn = 0
        for r in rows:
            t = r["truth"] == positive
            p = preds.get(row_key(r), "") == positive
            tp += t and p
            fp += p and not t
            fn += t and not p
        return (tp / (tp + fp) if tp + fp else None, tp / (tp + fn) if tp + fn else None, tp, fp, fn)
    out = {}
    for label in sorted(set(r["truth"] for r in rows if r["truth"])):
        out[label] = precision_recall(rows, preds, label)
    return out


def rand_index(rows, preds):
    """Pairs together-or-apart agreement, per file then pooled over pairs: the Rand index from the contingency table."""
    agree = total = tp = fp = fn = 0
    by_file = defaultdict(list)
    for r in rows:
        by_file[r["file"]].append(r)
    for rs in by_file.values():
        n = len(rs)
        if n < 2:
            continue
        pairs = n * (n - 1) // 2
        a = Counter(r["truth"] for r in rs)
        b = Counter(preds.get(row_key(r), "solo:%s" % row_key(r)[1]) for r in rs)
        nij = Counter((r["truth"], preds.get(row_key(r), "solo:%s" % row_key(r)[1])) for r in rs)
        c2 = lambda k: k * (k - 1) // 2
        both_together = sum(c2(v) for v in nij.values())
        true_together = sum(c2(v) for v in a.values())
        pred_together = sum(c2(v) for v in b.values())
        both_apart = pairs - true_together - pred_together + both_together
        agree += both_together + both_apart
        total += pairs
        tp += both_together
        fp += pred_together - both_together
        fn += true_together - both_together
    # the Rand index is dominated by the pairs that are apart; the pairs proposed TOGETHER are what a Fuse acts on
    return {"rand": agree / total if total else None, "pairs": total,
            "together_precision": tp / (tp + fp) if tp + fp else None, "together_recall": tp / (tp + fn) if tp + fn else None,
            "together_true": tp + fn, "together_proposed": tp + fp}


def projection(parts, model, dials):
    """The Lab's Verify projection under `dials`: the recipe's probe vertex count per part, each role's tier taken off
    (VehicleLabWindow, Statistics: verts * (1 - dial/100), Ignore deleted, a role without a tier unreduced). The
    recipe's own count is used, so a recipe-only source projects too. None when the model has no recipe rows."""
    verts = 0.0
    seen = False
    for r in parts:
        if r["model"] != model or not r["role"]:
            continue
        seen = True
        if r["role"] == "Ignore":
            continue
        v = num(r["recipe_verts"] if r["recipe_verts"] != "" else r["verts"])
        d = ROLE_DIAL.get(r["role"])
        pct = num(dials.get(d, DIAL_DEFAULT.get(d, 0.0))) if d else 0.0
        verts += v * (1.0 - pct / 100.0) if pct > 0 else v
    return verts if seen else None


def pct(v):
    return "-" if v is None else "%.0f%%" % (100 * v)


def evaluate(parts, models, proposers, target=30000.0):
    """Every task against every proposer: a list of Markdown lines."""
    lines = []
    for task in ("role", "delete", "delete-hand", "split", "group"):
        rows = population(task, parts)
        n_models = len(set(r["model"] for r in rows))
        lines.append("")
        lines.append("### %s: %d parts in %d models, %d files" % (task, len(rows), n_models, len(set(r["file"] for r in rows))))
        if task == "role":
            truth = Counter(r["truth"] for r in rows)
            lines.append("")
            lines.append("Labels: " + ", ".join("%s %d" % (k, v) for k, v in truth.most_common()))
            cats = Counter(r["category"] for r in rows)
            lines.append("")
            lines.append("Per category: " + ", ".join("%s %d" % (k, v) for k, v in cats.most_common()) +
                         "; non-default labels " + ", ".join("%s %d" % (k, sum(1 for r in rows if r["category"] == k and r["truth"] not in ("Default", "Body"))) for k, _ in cats.most_common()))
        elif task in ("delete", "delete-hand", "split"):
            pos = sum(1 for r in rows if r["truth"])
            lines.append("")
            lines.append("Positives: %d of %d (%.1f%%)" % (pos, len(rows), 100.0 * pos / len(rows) if rows else 0))
        if not rows:
            lines.append("")
            lines.append("No rows; nothing to score.")
            continue
        for name, fn in proposers.get(task, []):
            preds = leave_one_model_out(task, rows, models, fn)
            lines.append("")
            lines.append("**%s**" % name)
            lines.append("")
            if task == "role":
                per = precision_recall(rows, preds)
                lines.append("| role | n | precision | recall |")
                lines.append("|---|---:|---:|---:|")
                for label, (p, r, tp, fp, fn_) in per.items():
                    lines.append("| %s | %d | %s | %s |" % (label, tp + fn_, pct(p), pct(r)))
                proposed = sum(1 for v in preds.values() if v)
                correct = sum(1 for r in rows if preds.get(row_key(r), "") == r["truth"])
                lines.append("")
                lines.append("Overall accuracy %s (%d proposed) - read the per-role rows, the Default majority dominates this number." % (pct(correct / len(rows)), proposed))
            elif task in ("delete", "delete-hand", "split"):
                label = "S" if task == "split" else "X"
                p, r, tp, fp, fn_ = precision_recall(rows, preds, label)
                lines.append("| precision | recall | proposed | right | wrong | missed |")
                lines.append("|---:|---:|---:|---:|---:|---:|")
                lines.append("| %s | %s | %d | %d | %d | %d |" % (pct(p), pct(r), tp + fp, tp, fp, fn_))
                by_cat = defaultdict(list)
                for r_ in rows:
                    by_cat[r_["category"]].append(r_)
                cats = []
                for cat, rs in sorted(by_cat.items()):
                    cp, cr, ctp, cfp, cfn = precision_recall(rs, preds, label)
                    cats.append("%s %s/%s" % (cat, pct(cp), pct(cr)))
                lines.append("")
                lines.append("Per category (precision/recall): " + "; ".join(cats))
            else:
                g = rand_index(rows, preds)
                lines.append("| together precision | together recall | pairs proposed together | pairs truly together | all pairs agreeing | pairs |")
                lines.append("|---:|---:|---:|---:|---:|---:|")
                lines.append("| %s | %s | %d | %d | %s | %d |" % (pct(g["together_precision"]), pct(g["together_recall"]), g["together_proposed"],
                                                                 g["together_true"], pct(g["rand"]), g["pairs"]))
    # dials
    rows = [m for m in models if m["dials_parsed"]]
    lines.append("")
    lines.append("### dials: %d models with a recipe carrying dials; the Lab's Verify projection (probe verts after the tiers) against a target of %d" % (len(rows), int(target)))
    for name, fn in proposers.get("dials", []):
        preds = leave_one_model_out("dials", [], models, fn)
        preds = {m: p for m, p in preds.items() if p}   # a proposer that has nothing to say about dials returns {}
        lines.append("")
        lines.append("**%s**" % name)
        lines.append("")
        if not preds:
            lines.append("No dial proposals.")
            continue
        lines.append("| dial | models | mean abs error (pct points) | median abs error |")
        lines.append("|---|---:|---:|---:|")
        for d in DIALS:
            errs = [abs(preds[m["model"]][d] - saved_dial(m, d)) for m in rows if m["model"] in preds]
            if errs:
                lines.append("| %s | %d | %.1f | %.1f |" % (d, len(errs), statistics.mean(errs), statistics.median(errs)))
        lines.append("")
        lines.append("| model | saved projection | proposed projection | saved under target | proposed under target |")
        lines.append("|---|---:|---:|---|---|")
        both = under_saved = under_pred = 0
        for m in rows:
            if m["model"] not in preds:
                continue
            saved = projection(parts, m["model"], m["dials_parsed"])
            prop = projection(parts, m["model"], preds[m["model"]])
            if saved is None or prop is None:
                lines.append("| %s | - | - | no recipe rows | |" % m["model"])
                continue
            both += 1
            under_saved += saved <= target
            under_pred += prop <= target
            lines.append("| %s | %d | %d | %s | %s |" % (m["model"], saved, prop, "yes" if saved <= target else "NO", "yes" if prop <= target else "NO"))
        lines.append("")
        lines.append("Under the target: saved dials %d of %d, proposed dials %d of %d." % (under_saved, both, under_pred, both))
    return lines


def load_proposer(path):
    spec = importlib.util.spec_from_file_location("advisor_proposer", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    if not hasattr(mod, "propose"):
        raise ValueError("%s has no propose(task, test_rows, train_rows, test_model, train_models)" % path)
    return mod


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--corpus", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
    ap.add_argument("--proposer", action="append", default=[], help="a module with propose(...); repeatable; scored beside the baselines")
    ap.add_argument("--no-baselines", action="store_true")
    ap.add_argument("--target", type=float, default=30000.0)
    ap.add_argument("--write", help="also write the tables to this Markdown file (relative to the corpus folder)")
    a = ap.parse_args(argv)
    if not os.path.exists(os.path.join(a.corpus, "parts.csv")):
        print("no corpus at %s (run tools/advisor/corpus.py first)" % a.corpus)
        return 1
    parts, models = load_corpus(a.corpus)
    proposers = {t: list(BASELINES[t]) for t in TASKS} if not a.no_baselines else {t: [] for t in TASKS}
    for p in a.proposer:
        mod = load_proposer(p)
        name = os.path.splitext(os.path.basename(p))[0]
        for t in TASKS:
            proposers[t].append((name, mod.propose))
    lines = ["## Advisor baseline (%d parts, %d models)" % (len(parts), len(models))] + evaluate(parts, models, proposers, a.target)
    text = "\n".join(lines) + "\n"
    print(text)
    if a.write:
        with io.open(os.path.join(a.corpus, a.write), "w", encoding="utf-8") as f:
            f.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
