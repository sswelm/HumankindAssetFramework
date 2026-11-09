# Plan: an advisor for cutting, fusing and reducing unit models

Draft 2026-11-09. Status: proposal, nothing built. Written for Sebastiaan and for whoever implements a phase.

## 1. What the plan is for

Turning a ripped model into a unit costs hours of hand work in three windows, and almost all of that time is
**decisions**, not computation. The Cutter, the Fuser and the Vehicle Lab already do the cutting, welding and
collapsing in seconds; what takes the time is deciding, part by part, what each of up to 3,350 rows *is* and what
should happen to it. Three kinds of decision:

| Decision | Where | What the user does today | Why it is slow |
|---|---|---|---|
| **Identify**: junk, interior, outer skin, deck, canvas, rope | Cutter (Split, X), Lab (Ignore) | Clicks rows, reads the preview, deletes what looks useless | Nothing says a 10-triangle sheet is the ship's outer skin; wrong deletes come back as see-through hulls |
| **Group**: which plates form one shell, which part mirrors which | Fuser (⊕ letters, mirror finder) | Assigns letters row by row, side by side | Hundreds of plates per hull; the geometry already knows which plates touch |
| **Tier**: role per part, reduce dial per role, under 30k triangles | Lab (roles, Vertices control, Verify) | Marks roles, sets 15 dials, reads the projection, re-marks | Role is a judgement; the dials are an optimisation the user does by hand against the projection |

The aim is an **advisor** that makes a first pass at all three and hands the result to the user as *proposed marks*
in the windows that exist, to accept, change or reject. It never applies anything on its own. This keeps the one
design rule that has held up in this project: the user marks, roles decide (`explicit-marking-beats-heuristics`).
The advisor is a faster way to produce marks, not a hidden heuristic.

## 2. What we already have

**Ground truth, written by the user over months.** Every saved recipe, sidecar and fuse report is a record of the
user's judgement on a real model:

| Asset | Count | What it teaches |
|---|---|---|
| Vehicle Lab recipes | 26, with 5,284 parts | Per part: name, vertex count, centre, size, visibility, bone, offset, scale, and the **role** the user chose; per model the 15 reduce dials |
| Marks sidecars (`.marks.txt`) | 22 | Which parts the user split (S) and deleted (X) |
| Fuse sidecars (`.fuse.txt`) | 27 | Which parts the user grouped into which shell |
| Fuse reports | 14 | Per sheet verdicts, warnings, faces rewound |
| GLB files in `D:\3DModels` | 148 across 65 model folders, of which 50 split, 21 fused, 8 cut | The inputs and outputs of every stage |

Three caveats on the corpus. It is skewed to ships (about two thirds of the parts); tanks, aircraft and guns are thin.
Recipes can be stale against their re-cut sources, 3 of 20 were when last checked, so they must be re-probed
through `tools/recipe_migrate.py` and the fuse reports before use. And most parts carry the default role, so the
informative labels are a minority: on the role task the metric must be per role, not overall accuracy.

**Signals the code already computes.** The C# probe gives each part its island count, bounding box, vertex count,
and the escape-ray **Visibility** verdict; the Lab tags parts the inside-out fix would flip (⟲) and finds flat
parts (the deck finder); the Fuser's mirror finder pairs parts across the centreline; **Verify** prints the per-role
vertex statistics and the post-dial projection. These are the advisor's inputs, and they are deterministic and
tested.

**A local model that can read tables and images.** The home PC runs Ollama with `qwen3.6:35b-a3b-q3`, which has a
vision encoder, and `gpt-oss:20b`. Both are the user's own, on the user's own machine, so the privacy question that
constrains PET does not arise here; the models are still kept local.

## 3. Which tool for which decision

Not every decision wants a language model, and the plan says so up front:

- **Grouping plates into shells** is geometry. Plates that touch at weld distance 0 are one shell; the Fuser already
  computes contact when it welds. A contact graph plus the mirror finder should reproduce most of the 27 saved
  groupings with no model at all. A language model adds nothing here.
- **Setting reduce dials** is an optimisation with a known objective: the projection under 30k triangles, with
  bounds the user has stated (Sail at or below 50, Oar around 40, Rigging 85 to 90). A small solver against Verify's
  formula does this; the corpus gives the priors. No model needed.
- **Role classification** is structured data with a weak name signal. The honest first attempt is a plain classifier
  (nearest neighbours or gradient boosting) on the probe features, trained on 5,284 labelled parts. The language
  model is tried second, with the nearest recipes as worked examples, and kept only if it beats the plain
  classifier on models it has not seen. What a language model does add: it reads names (`shroud`, `window`,
  `Material2_5`), it can say *why* it chose a role, and it generalises to a vehicle class the corpus lacks by
  reasoning rather than by proximity.
- **Identification of junk versus skin** is the one decision where the model's eyes may matter. A rendered view of
  the candidate part, highlighted in place, with the question "outer surface, interior, or debris?", is something
  a vision model can answer and a feature table cannot. This is also the decision where mistakes cost the most,
  so the model is used as a **gate before a delete**, not as the deleter.

## 4. Phases

Each phase ends with a number against held-out models, never with "looks right". Hold out whole models, not
parts: leave-one-model-out over the 26 recipes.

### Phase 0: the harness and the baseline

- Export the corpus to one table: per part, the probe features and the user's role, S/X mark and fuse letter,
  joined by node name after `recipe_migrate.py` has brought stale recipes up to date.
- Define the metrics. Role: per-role precision and recall on held-out models. Deletion: precision of proposed X
  and Ignore against the user's marks, because a wrong delete is the expensive error. Grouping: fraction of part
  pairs correctly together or apart against the fuse sidecars. Dials: triangle projection error against the saved
  dials and whether the result lands under 30k.
- Time the manual process once on the next new model, stage by stage, so the prize is known in minutes.
- Deliverable: `tools/advisor/corpus.py` and `evaluate.py`, a Bake Tests row that runs them, and the baseline
  numbers in this document.

**Status (2026-11-09): built, except the Bake Tests row.** The two scripts exist; their unit tests run in the push
gate (`Tests/test_advisor_corpus.py`); the export and the evaluation are commands, not a row, because the corpus
lives on this machine's `D:\3DModels` and there is no proposer to run yet. The row comes with Phase 1's first
proposer. The manual timing has not been done; it waits for the next new model.

The evaluation keeps answers out of held-out proposer inputs. `test_rows` exposes the explicit `PART_FEATURES`
allowlist in `evaluate.py`: identifiers, names, geometry and the visibility/flip/bone probe observations. It omits truth,
roles, marks, fuse outcomes, placement edits and recipe joins; `test_model` exposes only model/category/files, never
saved dials. Training records retain their labels. Fold arguments are copied so a proposer cannot change scoring
truth or contaminate a later fold by normalizing its inputs in place. The corpus CSVs retain all columns for auditing.

The sidecar reader accepts ASCII signed Int32 indices, preserves both readings of legacy names ending in `|<number>`
and refuses ambiguous matches, as the Workshop does. Feature-cache keys retain nanosecond timestamps, so equal-size
edits within a second invalidate the cached geometry. These review regressions bring the advisor suite to 35 cases.
On the real 33,441-row corpus (65 folders), the built-in baseline reports before and after isolating proposer inputs
are identical: those baselines never used the leaked answers. A cached export still takes about 3 seconds.

```
python tools/advisor/corpus.py --out C:/tmp/haf-advisor        # 65 folders, about 6 minutes cold since the merge port, 3 s from the cache
python tools/advisor/evaluate.py --corpus C:/tmp/haf-advisor --write baseline.md
```

What the export found about the corpus itself, before any metric:

- 30,053 rows over 53 decision-bearing GLBs (22 raw, 2 cut, 18 split, 11 fused) in 26 folders, plus 3,388 rows
  carried from recipes whose source this reader cannot open (the two FBX recipes, the Ehrhardt's 3,350 parts among them, and the two second-model parts of
  the Wespe and the TOW). 24 of the 26 recipes join every part to a node. The Ozhiga's 688 parts are the loose parts
  of one mesh (the probe's `minVerts` split), not nodes, and are reported, not guessed. The Saleg's recipe is stale
  against its re-fuse: 9 parts name nodes the fused file no longer has.
- Recipes the Lab probed through Blender name duplicate nodes `Box.001`, `Box.002`; the Cutter names the same nodes
  `Box_2`, `Box_3`. 74 parts (the Rah66's 72, two on the Great Eastern) join by that rule, in node order.
- A Split writes every child against the parent's whole POSITION accessor and subsets the indices. Counting the
  accessor, a child has hundreds of vertices it never draws, each a singleton island; the export counts referenced
  vertices only (the triangle sums then equal the fuse report's to the face).
- 1,246 parts are line or point meshes (the Espana's 787 rigging lines, the Dragon's 422). The Workshop blocks them
  ("Only TRIANGLES primitives can be split safely"); counted as geometry they were one island per vertex and 1,035
  false split candidates. And the Workshop marks X itself, on first load, every part of two triangles or fewer that
  is not blocked (or is triangle-free) and has no split, tear or group of its own: 1,673 of the 6,998 X marks are
  the tool's, not the user's. The table carries `blocked`, `triangle_free` and `auto_mark`, and the deletion task is
  scored twice, with and without them. `auto_mark` applies the rule to the saved sidecar, the Workshop applied it at
  first load: 458 such parts carry no X today (413 of them the Dragon's raw file, loaded before the rule or cleared),
  and the second scoring drops them with the rest. A deliberate keep of a two-triangle part is lost there; the count
  is small and the alternative would score the Dragon's 413 tool-shaped parts as the user's negatives.
- The Romanic's raw-file fuse sidecar was re-lettered after the fuse: 319 parts carry a letter that differs from the
  report's. The sidecar is the user's latest intent, the report is what was fused; the table carries both.
- Of 16 recipes with dials, the saved dials put 4 under 30,000 in the Lab's Verify projection (probe vertices after
  the tiers). The dials are not set to hit that number; the Factory's **Reduce to ~tris** caps the shipped mesh
  afterwards. Phase 5's objective must be restated before it is built.

The baselines, leave-one-model-out, pooled (the full tables are in `baseline.md` beside the export):

| Task | Population | Trivial baseline | Best cheap baseline |
|---|---|---|---|
| Role | 4,588 labelled parts in 26 models (Body 2,830, Turret 607, Wheel 479, Ignore 256, then a long tail; armour 3,527 of them, nearly all the Ehrhardt) | always Default: 1 % right | majority role of the other models: Body 62 % precision / 100 % recall, every other role 0 % |
| Delete | 29,482 parts in the 52 files the user judged (marks, letters or a recipe), 10,673 deleted (36.2 %) once a deletion at a later stage file counts for the same part at the earlier one (Phase 1's status; it was 7,241 before) | never: recall 0 % | the smallest tenth of each file: 57 % precision / 20 % recall; every interior part (vis = 0, the probe's verdict for every file since Phase 1): 88 % precision / 14 % recall |
| Delete, the user's own marks | the same files without the 2,128 parts the Workshop marks itself: 27,354 parts, 8,589 positives (31.4 %) | never | the smallest tenth: 39 % precision / 15 % recall; every interior part: 84 % / 13 %. Size alone finds the tool's deletions, not the user's; the verdict finds a tenth of the user's at high precision |
| Split | 10,457 splittable parts in the 11 raw and cut files with marks, 178 marked S or T (1.7 %) | never | multi-island part above the file's median size: 18 % precision / 79 % recall; every multi-island part: 14 % / 100 % |
| Group | 19,056 parts in 27 files with letters; 1.34 M of 21.1 M pairs truly together | all apart: 0 % recall; all together: 6 % precision | one group per material name: 14 % precision / 1 % recall of the together pairs |
| Dials | 16 recipes carrying dials; a dial a recipe lacks reads as the Lab's default (rigging 75, structure 50, else 0) | median dial of the other models: body 23 points mean error, structure 13, rudder 40, default 50 | the category's median: body 22, structure 10, rudder 26, default 27 |

What the numbers say about the phases: the Split heuristic recalls four in five of the user's splits at one right
proposal in five, so Phase 1's island-dominance rule has a precision job, not a recall job; grouping by name is
useless, so Phase 1's contact graph has the whole job; deletion by size finds only what the Workshop already deletes
by itself, and the user's own deletions had no cheap signal until the probe's visibility verdict was in the table for
every file (Phase 1: it is now, and the deletion proposer builds on it); roles are 62 % Body and the interesting roles are a few hundred parts each, so Phase 2 and 3 are judged per
role, never overall.

### Phase 1: deterministic proposers

- Fuse groups from the contact graph at weld distance 0, mirror pairs merged, one letter per connected shell;
  proposals written as a `.fuse.txt` the Fuser loads with **Load groups**.
- Split candidates: parts with several islands whose largest island dominates by size, below the merge slider.
- Ignore candidates: fully enclosed by the escape rays and small; **never** proposed for a part that is the
  outermost surface along any probe ray.
- Measure all three against the corpus. Expect grouping to be close to the user's choices and the other two
  to be conservative. Whatever this phase gets right is time saved with no model running.

**Status (2026-11-09): the Split proposer and the deletion proposer are built and measured, with the probe's visibility
verdict in the table for every file; grouping is not started.**

- The corpus now carries the island count the user actually reads in the Cutter: the probe's 1 % distance merge
  (`GlbDisconnectedParts.AnalyzeMesh`, "DISTANCE MERGE") is ported into `corpus.py` as `islands_merged`, with the
  largest and second island's share of the part's triangles and the number of islands holding 1 % or more. The
  port follows the C# step for step (vertex-distance grid, the principal-axis gate that lets elongated islands
  chain only when colinear, the pair budget); its base islands are the exact-position weld where the probe welds
  at a few ulps, and there is no oracle for the merged count outside the editor. The cold export is about six
  minutes now (the Saleg's raw file alone 89 s), 3 s from the cache.
- `tools/advisor/proposers/split.py` proposes S for a part that is not blocked, has at least K merged islands, T
  triangles and B islands of 1 % or more; K, T, B are learned on the other models by exhaustive search over a
  small grid, maximising F1. Leave-one-model-out on the 10,457 splittable parts of the 11 marked raw and cut files
  (178 marked S or T): **74 % precision, 78 % recall** (187 proposed, 139 right, 48 wrong, 39 missed), against
  18 % / 79 % for the best size heuristic. The learned rule is the same on 9 of 10 folds: 6 merged islands,
  5,000 triangles, no B. F0.5 was measured first (72 % / 68 %) and lost to F1; a within-file size percentile added
  nothing.
- Where it misses: 31 of the 39 missed marks are parts under 5,000 triangles and 15 have fewer than 6 merged
  islands; the Svea loses 10 of its 22 (its marked fittings run from 170 to 6,500 triangles), the Sirius's one
  mark is missed while 6 are proposed. The Dragon, the hardest file (25 marks among 2,163 parts), gets 17 found
  and none wrong; the Saleg 67 of 73 with 20 wrong. The rule is a size rule with an island floor; the user also
  splits mid-sized rigging and fittings that the rule cannot tell from the thousands of small multi-island parts
  left alone. The next feature to try is what the parts around a marked part look like (the same material, the
  same size band) rather than the part alone.
- Writing proposals: `python tools/advisor/proposers/split.py --corpus <dir> --model "<folder>" --write` puts
  `<file>.glb.marks.proposed.txt` beside each raw or cut GLB, in the sidecar's format, trained on every other
  model. Every raw/cut file present in the corpus is refreshed, including an empty proposal when nothing is
  recommended or all parts are blocked, so a previous run cannot leave stale S marks behind. The learner abstains
  when no grid rule finds a training split, including when training is empty or has no positive labels.
  It never writes the live `.marks.txt`; the user renames or merges it. Loading a proposal into the
  Cutter is Phase 6.
- **The visibility verdict for every file.** `tools/advisor/probe_vis.sh` compiles `tools/advisor/ProbeVis.cs` against the
  editor sources (the vehicle probe drill's compile list) and runs the Lab's own probe (`VehicleProbe.Run`: the escape
  rays over one BVH of the whole file, the inside-out count) over every GLB `corpus.py --list` names, as a 64-bit
  process; all 53 files took 38 s wall on 2026-11-09 (the Saleg's split file 7 s). `corpus.py --probe <table>` joins the rows by
  glTF node (a single-mesh file's loose parts fold onto their node: external when any part escapes, flips summed) and
  checks each file's size and write time against the table - a file re-cut since is reported stale and falls back to
  the recipe's verdict, named in `vis_from`. The join's own check: the 1,128 recipe rows with a verdict agree with
  the probe on 1,126 and the other 2 are the Lab's -1 (unclassified). 30,740 probe rows fold onto 30,053 corpus rows,
  1,681 of them interior (the 3,388 recipe-only rows keep the recipe's verdict).
- **The deletion truth was understated.** The user splits at the raw file and deletes at the split file, so a raw
  row the split file marks X read as "kept": 2,000 parts of the Espana's raw file, 1,290 of the Dragon's, 3,432 in
  all over eight files (the other six: the Steam Frigate 78, the Sirius 58, the Svea's cut file 2 and raw 1, the
  Saleg 2, the Wespe 1). `corpus.carry_later_marks` carries the X back as `mark_later` when name and vertex count
  match in a later stage file of the same folder whose name extends this file's with stage tokens (3,432 of 3,432
  match on both, no conflict: no part is marked X at one later file and listed unmarked at another of the same or a
  higher stage); a part absent from the later file is not inferred deleted (the Teutonic's `_cut2_split` lists 220
  of 1,437 unmarked parts: a partial export), and an unnamed node is never the same part. The evaluator's delete tasks count `mark_later`; the table above carries the corrected
  baselines (the interior baseline went from 75 % precision on the uncorrected truth to 84 %).
- **`tools/advisor/proposers/delete.py`** proposes X for a part the probe calls interior whose largest extent is at
  most A of the model's, or any part at most B of the model's extent; A and B are learned on the other models over
  a small grid maximising F0.5 (precision double: a wrong deletion is a hole in the model, a missed one a click),
  ties to the stricter rule. Leave-one-model-out on the 27,354 hand-judged parts (8,589 deleted): **86 % precision,
  52 % recall** (5,180 proposed, 4,443 right, 737 wrong, 4,146 missed), against 84 % / 13 % for every interior part
  and 39 % / 15 % for the smallest tenth. The rule is interior up to 10 % of the model, anything up to 0.05 %, on
  23 of 26 folds (F1 would learn a looser one, every interior part and anything up to 0.1 %, on 24 of 26: the beta
  is a choice, not a detail). Per model: the Espana 2,937 right and 52 wrong, the Dragon 1,042 and 34, the Rah66 45
  and 0; the Romanic is where it is wrong (all its 494 wrong carry a fuse letter: tiny parts the user lettered into
  fuse groups instead of deleting; 603 of the 737 wrong over every model do), the Steam Frigate 115 wrong of 206.
  The misses are 3,696 exterior parts (450 of the 4,146 missed are interior), four in five of them between 0.07 %
  and 2.7 % of the model (median 0.35 %): visible fittings the user judged not worth their triangles, which no size
  rule separates from the ones kept - Phase 4's question. `--write` puts the X lines into the same
  `<file>.glb.marks.proposed.txt` the Split proposer writes its S lines to (`proposers/common.py`: each proposer
  owns its letters, the other's stay).

### Phase 2: role classifier without a language model

- Nearest neighbours and a gradient-boosted classifier on the probe features plus simple name tokens.
- Per-role numbers on held-out models. This is the bar the language model has to clear.

### Phase 3: the language model as role advisor

- Prompt: a compact part table for one model, chunked by height band so a chunk stays under about 200 rows,
  with the two nearest recipes of the same vehicle class as worked examples; output JSON per part: role, one-line
  reason, confidence. Thinking off first; thinking on and `gpt-oss:20b` as variants.
- Served by Ollama on the home PC over localhost. Context 32k holds a chunk with examples; a 3,350-part armoured
  car needs about 17 chunks.
- Measure as in Phase 2. Decision rule: the model's roles are used where they beat the classifier per role; where
  they do not, the model is kept as the explainer of the classifier's picks, which is still useful in the window.

### Phase 4: the vision gate on deletions

- For each proposed X or Ignore, render the part highlighted in its context from above and from the beam (the
  Workshop preview camera, or the headless GLB render path the drills use), and ask the vision model: outer surface,
  interior, debris, with a reason.
- Measure against the user's X marks and against the see-through cases on record (the Saleg's Revenge: 611 of 634
  hole cells came from three deleted parts).
- Deliverable: a warning in the Cutter before a delete the model calls outer skin. Cost: a few seconds per
  candidate, so candidates only, never every row.

### Phase 5: the dial solver

- Given the roles and Verify's per-role vertex counts, solve the 15 dials for a target under 30k with the corpus
  priors as starting point and the stated bounds as constraints. Report the projection next to the proposal.
- Measure against the saved dials and against the `BAKED MESH` verdicts of past bakes.

### Phase 6: integration

- A **Propose** button in the Cutter, the Fuser and the Lab. Proposals appear as pending marks in their own colour,
  with the reason on hover; **Accept all**, accept per row, or clear.
- Every accept and every override is appended to the corpus, so the next model starts from more evidence.
- The harness from Phase 0 runs in Bake Tests so a change to a proposer cannot regress silently.

## 5. Risks and limits

- **Video memory on one machine.** The Unity editor's preview of a large ship and a 14 GB model do not fit the
  16 GB card together; this week's measurements showed the model's speed halving as soon as other programs held
  VRAM. The advisor should run while the preview is unloaded, or the model should be unloaded during Probe and
  Fuse. A second card would remove the conflict.
- **Context.** Part tables for big models exceed 32k tokens; the chunking in Phase 3 is required, and chunk
  boundaries by height band keep related parts together.
- **Corpus skew.** With 26 models, two thirds of them ships, the numbers for tanks and aircraft will be weak until
  more of those are marked. Phase 3's reasoning is the hedge for new classes; the per-class numbers must be
  reported, not averaged away.
- **Stale recipes.** A recipe saved before its source was re-cut does not match the source's parts; migration
  through the fuse report is mandatory before a model is used as ground truth.
- **What the advisor cannot do.** It cannot see the game. "Believable from a distance" is decided by the user in
  the game, after a bake. The advisor shortens the first pass through a model; it does not replace the acceptance
  loop, and it must not be tuned to the point of making decisions the user should see.

## 6. What to decide now

1. Whether the prize is worth the build: Phase 0's timing of one manual run answers that with minutes. Still open;
   the harness is built, the stopwatch has not run.
2. The order after Phase 0. The baselines (above) say: Split first, because the island heuristic already recalls
   79 % of the user's splits and only its precision is missing; then the visibility verdict into the table (the
   probe's escape rays for every file, not only the recipe's), because the user's own deletions have no cheap signal
   without it; grouping by the contact graph after that. Split and the verdict are done (Phase 1's status), the
   deletion proposer with them; grouping is next. The most interesting is still Phase 4.
3. Where the advisor runs: on the home PC beside Unity, accepting the VRAM conflict, or on a second card.
4. What the dial solver optimises: the saved dials sit above 30,000 projected vertices on 12 of 16 recipes, so
   "under 30k" is not what the user has been doing. Read the Factory's **Reduce to ~tris** against the Lab's dials
   on the shipped bakes before Phase 5 gets an objective.

## 7. Pointers

- Workshop windows and sidecar formats: `Editor-Tools.md`, section *Model Cutter and Model Fuser*.
- Roles, dials and the large-model workflow: `Vehicle-Lab-Quickstart.md`, sections 3, 4 and 9.
- Recipes: `C:\Repo\ENCReload\Assets\FactorySource\VehicleLab\Recipes\*.json`; migration: `tools/recipe_migrate.py`.
- The corpus export and the metrics: `tools/advisor/corpus.py`, `tools/advisor/evaluate.py` (a proposer is a module
  with `propose(task, test_rows, train_rows, test_model, train_models)`); tests: `Tests/test_advisor_corpus.py`.
- Sources and sidecars: `D:\3DModels\<model>\`.
- Ollama on the home PC: `how-we-connected-the-laptop-to-the-home-model.md` and `model-recommendation.md` on the Desktop.
