# Formations — custom unit formations & pawn counts (the fifth data axis)

Change **how many soldier models a unit fields and how they're arranged** on the world map, with **zero baked
assets** — a runtime override driven by `enc_formations.json`. Link a `PresentationUnitDefinition` to a formation
whose dummy count and layout you author in the Unity SDK, and the plugin injects it into the live database and
repoints the unit at load. Fully reversible: delete the link and the unit is vanilla next launch.

> Status: **VERIFIED IN-GAME 2026-07-28** — 12- and 16-model Warriors units render correctly (all models on the hex,
> banner centered). The fix is count-agnostic; the vanilla 9/10 ceiling is gone. See [The >9 story](#the-9-story).

---

## What it controls

- **Pawn count** — how many models the unit shows. On the map this is `ceil(healthRatio × Formation.DummyCount)`,
  so a full-health unit shows exactly `DummyCount` models; a damaged one shows proportionally fewer. There is **no
  hidden cap** — `DummyCount` is simply the length of the formation's `Dummies[]` array.
- **Layout** — each dummy's local `Position` places its model relative to the unit's tile (plus a small random
  jitter, the unit's `CoordinationValues.DummyOffsetPosition`). The six per-orientation `CoordinatePerDirection`
  grids + the hidden `ColumnsCountPerRow0..5` arrays drive the logical row/column grid used for facing and
  attack targeting.

## User workflow (no mod rebuild)

1. **Extract** a vanilla formation asset into the project (`Assets/Databases/UnitFormation/…`) — or duplicate one —
   so you have a `PresentationFormationDefinition` you can edit. Its Inspector shows a live hex preview with numbered
   dummies + XYZ fields.
2. **Author** it: add/remove dummies (each needs 6 `CoordinatePerDirection` entries), set positions, keep the six
   `ColumnsCountPerRow` arrays consistent (cell counts must equal the dummy count). Inconsistent grids make the game
   throw at load — see [Troubleshooting](#troubleshooting).
3. **Link** it: open **Tools ▸ HAF ▸ Formation Override**, **Pick** the unit (`PresentationUnitDefinition` name,
   e.g. `PresentationLandUnit_Era1_Common_Warriors_Default`), **Pick** the formation asset, **Save link**.
4. **Launch** — no rebuild. The plugin reads `enc_formations.json` from `BepInEx/config`, rebuilds the formation as a
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
| `FormationOverride` | `true` | Master switch. Reads `enc_formations.json`, injects + repoints. Inert if the file is absent/empty. |
| `FormationReinstantiate` | `true` | After apply, re-form already-spawned under-count units (load-race catch-up). Costs a one-time visible re-form pop. |

## Troubleshooting (read `BepInEx/LogOutput.log`)

- `[Formation] registry: N link(s)` — the file was read.
- `[Formation] '<formation>' … OVERWRITTEN in place (N dummies)` / `injected …` — the formation data is live.
- `[Formation] '<unit>' now uses formation '<formation>' (N pawns at full health)` — the repoint took.
- `[Formation] Formation3DPrefab dummy pool extended 9 -> N` — the >9 growth ran.
- `[Formation] replaced N prefab-bound dummy slot(s) …` — the origin-stranding fix ran (expected for any formation
  once the prefab is grown past vanilla).
- `[Formation] re-instantiated '<unit>': pawns A -> B …` — the load-race catch-up fired.
- **Fewer models than expected, no "Invalid pawn count" error** → the unit isn't full health, or it's a different
  definition (e.g. `_Rogue`).
- **A stray unit icon / models far away** → pre-fix origin stranding; make sure the plugin build has the
  `replaced … prefab-bound` fix.
- **"Mismatched mods" / crash at load** → inconsistent `ColumnsCountPerRow` vs dummy coords; the Formation Override
  window validates this before it lets you save, so re-save from the window.

## Files

- Editor: `Assets/Scripts/Editor/FormationOverrideWindow.cs`, `FormationRegistry.cs` → `enc_formations.json`.
- Plugin: `Patches/FormationOverridePatch.cs` (`FormationOverride` + the `Hk_FormationPrefabExtend` /
  `Hk_FormationInstanceCapacity` / `Hk_FormationSpawnDiag` hooks). Registered in `Plugin.cs`.
- Registry lives in the game's `BepInEx/config/enc_formations.json` (the editor writes there directly — same file the
  plugin reads, no source/deployed split).
