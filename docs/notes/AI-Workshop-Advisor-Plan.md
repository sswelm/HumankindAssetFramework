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

### Phase 1: deterministic proposers

- Fuse groups from the contact graph at weld distance 0, mirror pairs merged, one letter per connected shell;
  proposals written as a `.fuse.txt` the Fuser loads with **Load groups**.
- Split candidates: parts with several islands whose largest island dominates by size, below the merge slider.
- Ignore candidates: fully enclosed by the escape rays and small; **never** proposed for a part that is the
  outermost surface along any probe ray.
- Measure all three against the corpus. Expect grouping to be close to the user's choices and the other two
  to be conservative. Whatever this phase gets right is time saved with no model running.

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

1. Whether the prize is worth the build: Phase 0's timing of one manual run answers that with minutes.
2. The order after Phase 0. The cheapest win is Phase 1, grouping in particular; the most interesting is Phase 4.
3. Where the advisor runs: on the home PC beside Unity, accepting the VRAM conflict, or on a second card.

## 7. Pointers

- Workshop windows and sidecar formats: `docs/Editor-Tools.md`, section *Model Cutter and Model Fuser*.
- Roles, dials and the large-model workflow: `docs/Vehicle-Lab-Quickstart.md`, sections 3, 4 and 9.
- Recipes: `C:\Repo\ENCReload\Assets\FactorySource\VehicleLab\Recipes\*.json`; migration: `tools/recipe_migrate.py`.
- Sources and sidecars: `D:\3DModels\<model>\`.
- Ollama on the home PC: `how-we-connected-the-laptop-to-the-home-model.md` and `model-recommendation.md` on the Desktop.
