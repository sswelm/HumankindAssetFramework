# Formations — custom unit formations & pawn counts (the fifth data axis)

Change **how many soldier models a unit fields and how they're arranged** on the world map, with **zero baked
assets** — a runtime override driven by `haf_formations.json`. Link a `PresentationUnitDefinition` to a formation
whose dummy count and layout you author in the Unity SDK, and the plugin injects it into the live database and
repoints the unit at load. Fully reversible: delete the link and the unit is vanilla next launch.

> Status: **VERIFIED IN-GAME 2026-07-28** — 12-, 16-, 19- and 32-model units render correctly (all models on the
> hex, banner centered). The fix is count-agnostic; the vanilla 9/10 ceiling is gone. See [The >9 story](#the-9-story).
> Model **scale** is built with two selectable modes — solved for spacing and non-human models, WIP on vanilla
> humans; see [Model scale](#model-scale-two-modes-and-their-limits) before using it.

---

## What it controls

- **Pawn count** — how many models the unit shows. On the map this is `ceil(healthRatio × Formation.DummyCount)`,
  so a full-health unit shows exactly `DummyCount` models; a damaged one shows proportionally fewer. There is **no
  hidden cap** — `DummyCount` is simply the length of the formation's `Dummies[]` array.
- **Layout** — each dummy's local `Position` places its model relative to the unit's tile (plus a small random
  jitter, the unit's `CoordinationValues.DummyOffsetPosition`). The six per-orientation `CoordinatePerDirection`
  grids + the hidden `ColumnsCountPerRow0..5` arrays drive the logical row/column grid used for facing and
  attack targeting.
- **Packing** — the random jitter makes formations read loose. The window's **Override packing jitter** toggle
  (registry `dummyOffset`, runtime-only) lets you tighten it: `0` sits models perfectly on the dummy grid, a small
  value (e.g. `0.05`) packs them tightly with a touch of variation, unticked (`-1`) keeps the vanilla scatter. On
  repoint the plugin sets the unit's `DummyOffsetPosition` to that value — no rebuild.
- **Scale** — the window's **Formation scale** toggle (registry `scale`) resizes the unit's models AND their
  spacing together (scaling a formation means the whole formation); **Footprint override** (`layoutScale`)
  decouples the spacing when you want small men on a wide line or vice versa. Two implementations selectable per
  link (`scaleMode`) — see [Model scale](#model-scale-two-modes-and-their-limits) for what works and what breaks.

## Two entry kinds: unit links and MACRO replacements

An `haf_formations.json` entry works in one of two modes, decided by whether **Unit** is set:

- **Unit link** (`unit` set) — repoints ONE `PresentationUnitDefinition` at the named formation. Precise, per-unit.
- **Macro replacement** (`unit` EMPTY) — overwrites a named formation in the live database with the entry's data.
  Every unit of every era — *including units from other mod packs that reference vanilla formation names and never
  rescaled anything* — inherits the new layout with ONE entry. Example: replace `Formation_Scatter_Spaced_9` with a
  19-dummy layout and the whole roster's scatter infantry fields 19 models. The window has an explicit entry-type
  toolbar: **Replace a formation (macro)** shows two fields — *Replace formation* (the target name, with a picker
  over the known vanilla formations) and *With layout* (Pick the project asset that carries the new layout; its data
  is used, its name isn't). Per-unit knobs (jitter/scale) don't apply — they live on unit definitions; use a unit
  link for those.

**Precedence:** macro replacements rewrite the shared formation; unit links repoint their unit at a *different*
formation and therefore overrule the replacement for that unit. A handful of macro entries + a few unit links for
showcases covers the entire roster without forgetting anyone.

*Why "macro":* the entry expresses a **rule**, not a single override — and the rule vocabulary is meant to grow.
Planned discriminators (reserved, not yet implemented): **era** (e.g. replace `Formation_Scatter_Spaced_9` with 19
dummies *only for Era 4+ units*), unit class, and land/naval — so one registry can express "denser formations as
eras progress" without touching every unit definition.

## User workflow (no mod rebuild)

1. **Extract** a vanilla formation asset into the project (`Assets/Databases/UnitFormation/…`) — or duplicate one —
   so you have a `PresentationFormationDefinition` you can edit. Its Inspector shows a live hex preview with numbered
   dummies + XYZ fields.
2. **Author** it: add/remove dummies (each needs 6 `CoordinatePerDirection` entries), set positions, keep the six
   `ColumnsCountPerRow` arrays consistent (cell counts must equal the dummy count). Inconsistent grids make the game
   throw at load — see [Troubleshooting](#troubleshooting).
3. **Link** it: open **Tools ▸ HAF ▸ Formation Override**, **Pick** the unit (`PresentationUnitDefinition` name,
   e.g. `PresentationLandUnit_Era1_Common_Warriors_Default`), **Pick** the formation asset, **Save link**.
4. **Launch** — no rebuild. The plugin reads `haf_formations.json` from `BepInEx/config`, rebuilds the formation as a
   runtime ScriptableObject, `Database.Add`s it, and repoints the unit.

**Save always re-reads the asset.** The window used to cache the formation data when you *Picked* it; if you then
edited the asset in the Inspector and hit Save, it silently shipped the stale Pick-time copy ("the save had no
effect"). Save now re-extracts the asset first, so a plain **Save link** always captures your current edits. There's
also a manual **Re-read** button. If in doubt, delete + recreate the link.

## Engine laws (decompiled, durable)

- **Count** = `Mathf.CeilToInt(healthRatio × Formation.DummyCount)` (`PresentationUnit.InstantiatePawns` /
  `CheckPawnCountValidity`). `DummyCount` = `Dummies.Length`. The game hard-errors "Invalid pawn count" if the actual
  count ever disagrees — so if you see fewer models than expected and there's **no** such error, the unit is simply
  at less than full health (or it's a different unit — see below).
- **Per-unit** via `PresentationUnitDefinition.PresentationFormationDefinition`, resolved **lazily by name at spawn**
  through `DatatableElementReference`. Repointing must install a **fresh** reference struct (never mutate the cached
  one, which caches its resolved element + revision).
- **Layout** is set in `FormationHelper.InitializeFormation3DForDefinition`: `Dummies[i].Transform.localPosition =
  definition.Dummies[i].Position`, then `Initialize()` captures that as the resting position. `BuildDummiesGrid()`
  only builds the lookup grid from `CoordinatePerDirection` — it does **not** move transforms.
- **Watch out — same "unit", different definitions.** Each cultural/independent variant is its own
  `PresentationUnitDefinition`: `…Warriors_Default` (your trained unit) vs `…Warriors_Rogue` (independent/barbarian,
  spawned by the animals faction) are separate and carry their own formations. A link to `_Default` does **not**
  touch `_Rogue`. If a "Warriors" unit shows the wrong count, confirm which definition it is.
- **Overwriting a vanilla formation name affects every unit using it.** The plugin overwrites the named formation's
  data in place (the registry is the source of truth), so if you reuse a vanilla name like `Formation_Scatter_Spaced_12`,
  every unit referencing that name gets your layout. Use a unique name to scope it to one unit. The log warns loudly.

## The >9 story (the vanilla dummy-pool ceiling)

Vanilla's biggest formation is **9–10** dummies, and `Formation3DPrefab` (the template every `Formation3D` is cloned
from) ships with that many **dummy child objects**. `SetDummyCount` never reallocates — it only
`GameObject.SetActive(i < count)` over the existing children — so **the prefab's child count is the real ceiling.**
That's the "magic number 9/10."

The plugin grows the prefab past it (`Hk_FormationPrefabExtend` clones the last dummy child before the pool is built).
But that surfaced a subtle bug: on a **pooled** `Formation3D`, the extra `Dummies[]` slots still **referenced the
prefab's** dummies (a runtime-added child isn't remapped on pool-clone the way a native child is). Those prefab
dummies sit at world origin, so the game's `Dummies[i].Transform.localPosition = Position` write moved a *prefab*
dummy and the instance's pawn was **stranded at (~0,0,0)** — 3 of a 12-unit's models teleported to the map origin
(which projected to "3 warriors lost far to the east"), while the army banner drifted toward them. Their `dummyLocal`
was correct; the unit's world offset was simply never applied because the dummy wasn't a child of the unit's
formation.

**The fix** (`EnsureInstanceCapacity`, a prefix that runs before the positioning loop): for each `Dummies[i]` whose
transform isn't a child of *this* instance, **replace it with a fresh clone of a genuine instance-child dummy**,
parented under the instance. Now every slot is a real child and inherits the unit's world position. Verified: 12/12
on the hex, log `[Formation] replaced N prefab-bound dummy slot(s) …`, zero pawns at origin. Battles already render
12+ models per unit — same engine — so this was always achievable; it was a binding bug, not a hard limit.

## The load-race safety net

The override applies a few frames into load (it waits for the databases). Units that spawn **before** it lands keep
the old formation/count until re-formed. `FormationReinstantiate` (default on) walks the live armies after the
override applies and re-runs the game's own `UpdatePawns` on any repointed unit that's **under** its target count, so
it catches up (a one-time re-form). In practice the repoint usually wins the race and this rarely fires; turn it off
to keep whatever count a unit had when it first rendered.

## Config

| Key (`[Formations]`) | Default | Effect |
|---|---|---|
| `FormationOverride` | `true` | Master switch. Reads `haf_formations.json`, injects + repoints. Inert if the file is absent/empty. |
| `FormationReinstantiate` | `true` | After apply, re-form already-spawned under-count units (load-race catch-up). Costs a one-time visible re-form pop. Covers both **inject/overwrite** entries and **pure-repoint** links (a unit pointed at a formation already in the DB) — the catch-up targets the resolved *target* formation's dummy count, so a repoint-only link's pre-override units are re-formed too. |

## Troubleshooting (read `BepInEx/LogOutput.log`)

- `[Formation] registry: N link(s)` — the file was read.
- `[Formation] '<formation>' … OVERWRITTEN in place (N dummies)` / `injected …` — the formation data is live.
- `[Formation] '<unit>' now uses formation '<formation>' (N pawns at full health)` — the repoint took.
- `[Formation] MACRO replacement live: every unit referencing '<formation>' now fields N pawns …` — a macro
  replacement entry applied (the OVERWRITTEN-in-place warning above it is expected and is the mechanism).
- `[Formation] Formation3DPrefab dummy pool extended 9 -> N` — the >9 growth ran.
- `[Formation] replaced N prefab-bound dummy slot(s) …` — the origin-stranding fix ran (expected for any formation
  once the prefab is grown past vanilla).
- `[Formation] re-instantiated '<unit>': pawns A -> B …` — the load-race catch-up fired.
- `[Formation] '<unit>' dummy jitter -> V (tighter packing).` — the packing override was applied.
- `[Formation] '<unit>' pawns scaled xS (Transform mode: root localScale).` — `transform` scale mode applied.
- `[Formation] '<def>': skeleton '<name>_HAFsS' — N bone binds ×S, M hosted mesh(es) scaled.` — `data` mode built
  the scaled skeleton clone.
- `[Formation] '<def>': SCALED xS in data (skeleton + k fragment(s) this pass); descriptor[id] repointed …` — `data`
  mode fully applied to the definition.
- `[Formation] '<def>': descriptor not yet populated — …` — normal on early Loads in `data` mode; a later pass (or
  the game's own fill from the already-replaced entries) completes it.
- **Gear floats above scaled bodies / heads tilt (`data` mode, humans)** → the known procedural-bone-layer limit —
  see [Model scale](#model-scale-two-modes-and-their-limits).
- **Fewer models than expected, no "Invalid pawn count" error** → the unit isn't full health, or it's a different
  definition (e.g. `_Rogue`).
- **A stray unit icon / models far away** → pre-fix origin stranding; make sure the plugin build has the
  `replaced … prefab-bound` fix.
- **"Mismatched mods" / crash at load** → inconsistent `ColumnsCountPerRow` vs dummy coords; the Formation Override
  window validates this before it lets you save, so re-save from the window.

## Model scale: two modes and their limits

> **For non-human units, prefer the Resize Lab instead ([Unit-Size.md](Unit-Size.md)).** That axis scales the unit's
> **vertex data** in the live Fx buffer plus its per-pawn placement — verified in-game, free on the vertex budget, and
> immune to both failure modes below, because it never asks a transform to grow geometry (the shaders don't do that;
> see Unit-Size § *Why it must be done this way*). The two modes here remain the way to scale **models and spacing
> together as one formation**, and the `data` mode's engine notes stay valuable — but for "make this ship bigger",
> use a `unitScales` rule.

The formation link carries a per-unit **scale** (window: *Formation scale*, 0.2–2.0). By default it scales the
models **and** the dummy spacing together; *Footprint override* (`layoutScale`) decouples the spacing. Two
implementations exist, selectable per link (window dropdown, registry `scaleMode`) — a hard-won field campaign
(2026-07-28) established exactly what each can and cannot do:

### `transform` (default — "Transform (simple)")

Sets each pawn root's `localScale` at `PresentationPawn.InstantiatePawn` (`Hk_FormationPawnScale`). One line of
mechanism, and bodies + spacing look right immediately.

**Known limits (field-proven, unfixable in this mode):** the engine applies a root scale **inconsistently across
three GPU subsystems** —
1. *Body skinning* follows it (bodies look right scaling down; scaling **up** distorts limbs — shriveled arms at 1.25);
2. *Rigid equipment fragments* (helmet/shield/weapon — bone-glued meshes) receive it **twice** on their vertices and
   once on their anchor: at 0.8 a helmet buries inside the skull ("bald legionaries") and shields hug the hand;
3. *Procedural weapon-slot / look-at bones* ignore it entirely.

**Verdict: usable for vehicles/creatures/custom models (single skinned mesh, no fragments) and for quick
experiments; NOT shippable on vanilla humans.**

### `data` ("Skeleton data (deep, WIP)")

Puts the scale **into the data** and leaves every transform at 1: clones the definition's `Skeleton`, multiplies all
bone `BindPose`/`Local` **translations** by *s* (rotations untouched — the engine's clips are rotation-only, so
vanilla animations replay correctly on a scaled bind *by construction*), scales every hosted body mesh and every EQ
fragment collection's pre-encoded vertices by *s*, then swaps the addon onto the clones (the custom-model repoint
idiom) with a FragmentEntry rebuild + surgical GPU-descriptor repoint.

**State: bodies and gear meshes verified correct; one subsystem still defeats it on humans** — the **procedural bone
layers** (head look-at, RLUDS weapon slots) write bone poses each frame in authored vanilla proportions, so helmets
anchor at vanilla head height above a scaled body and heads tilt. Next attack documented: decompile the
`BoneRotation0-3`/slot layer writer (the plugin already owns aim-layer levers from the barrel-twist work). Untested
but promising on vehicles (no fragments, no slots, no look-at).

### Engine internals the `data` mode ran into (reusable knowledge)

- Vanilla fragment geometry ships **pre-encoded** in `FxMeshContent.verticesBytes` inside the MeshCollection — there
  is **no loadable FxMesh asset** behind those guids. Positions are the first 3 floats of each vertex record
  (stride = `bytes/(vertexCount·4)` floats).
- Modified bytes are rejected as *"Mesh content is corrupted: checksum failed"* — **zero `verticesBytesCrc`** to skip
  the guard (0 disables validation by design).
- The encoder caches mesh slots **per guid** — a mutated content needs a **fresh guid** or the cached original wins.
- `RegisterMeshCollection` only encodes when the fx pipeline reports Loaded — call `LoadIFN` explicitly after it.
- The addon's `Load` runs **more than once**; vanilla `ReloadFragments` rebuilds `FragmentEntries` from the
  definition each time (clobbering replacements) — re-apply on every Load, tag clones (`_HAFs` name suffix) so they
  are never re-scaled, and expect the GPU descriptor slot to be **empty (count 0)** on early passes (the game fills
  it later from the addon's — by then replaced — array).

### Capability summary

| Target | `transform` | `data` |
|---|---|---|
| Vanilla humans | bodies OK (down only), gear breaks | bodies + gear meshes OK, gear **anchors** break (procedural layers) — WIP |
| Vehicles / ships / planes | expected clean (untested) | expected clean (untested) |
| Custom HAF models | expected clean | expected clean (or bake at the right size instead) |

Baking a scaled unit as a **custom model** (gear merged into the mesh, Model Factory pipeline) sidesteps every
runtime subsystem at the cost of per-pawn equipment variation — the pragmatic route if a scaled human unit is needed
before the procedural-layer work lands.

## Formation by size (era ageing) — VERIFIED IN-GAME 2026-07-30

Pairs the formation axis with the **Unit-Size axis** ([Unit-Size.md](Unit-Size.md)): as the Global Era Lab shrinks an
aged unit, its formation can swap so a tiny lone hull becomes a **squadron of small hulls** (field-proven: an aged
Bireme re-formed into three wedge-formation ships, live, when the era anchor crossed the threshold).

- **Authored PER UNIT** in the Formation Override window: a unit link's **"Formation by size"** rows are
  `{scale up to, formation}` — the first row whose threshold is **>=** the unit's *effective* scale
  (`Resize-Lab rule × era-grid cell`) wins; above every threshold the unit keeps its configured/own formation.
  Rows are sorted on Save; stored as `sizeFormations` on the link in `haf_formations.json`.
- **Only fires for units with a Resize Lab rule** — the thresholds compare against the effective scale the resize
  engine computes, so an unruled unit never swaps.
- **Live**: the check rides the same per-frame path as the era re-scaling — when the era anchor moves a unit across
  a threshold mid-game, the definition is repointed and every live unit re-forms in place (no reload). Rising back
  above all thresholds restores the unit's original formation.
- **Guard**: the target formation must exist in the live database (vanilla, or injected by any saved Formation
  Override entry) — otherwise the swap is skipped with a loud log.
- **Legacy fallback**: thresholds saved by the old *Global Era Lab table* (in `haf_models.json`) still apply to
  units **without** per-unit rows; the Era Lab shows them with a Clear button. Per-unit rows always win.
- Log: `[Resize] formation-by-size: '<unit>' at effective x0.3 -> 'Formation_Wedge_3' (N live unit(s) re-formed).`

## R.E.D.-style: the count + scale pair

This axis is one half of a **R.E.D.-style** rebalance (after the classic Civ 5 *R.E.D. Modpack* by Gedemon):
**model count** (the formation axis above — solved, verified to 32/unit) + **model scale** (above — solved for the
spacing half; model-size half usable on non-humans, WIP on vanilla humans). The eventual goal is an **optional
"R.E.D. Patch" pack** — a curated set of formation + size overrides across the roster (smaller, more-numerous
infantry; big-but-sparse tanks; smaller planes), opt-in and fully reversible. Practical counts: a hero/showcase unit
can go 30–50; a whole-roster rebalance wants ~12–20 per unit (cost scales with *total* on-screen pawns, not
per-unit), so ~18 is a good roster default.

## Files

- Editor: `Assets/Scripts/Editor/FormationOverrideWindow.cs`, `FormationRegistry.cs` → `haf_formations.json`.
- Plugin: `Patches/FormationOverridePatch.cs` (`FormationOverride` + the `Hk_FormationPrefabExtend` /
  `Hk_FormationInstanceCapacity` / `Hk_FormationSpawnDiag` / `Hk_FormationPawnScale` hooks; the `data` scale mode
  also rides `UniRepointHook`'s AddOn.Load postfix via `MaybeScaleFragments`). Registered in `Plugin.cs`.
- Registry lives in the game's `BepInEx/config/haf_formations.json` (the editor writes there directly — same file the
  plugin reads, no source/deployed split).
