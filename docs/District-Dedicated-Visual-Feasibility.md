# Dedicated District Visual — Feasibility Study

**Branch:** `spike/district-dedicated-visual` (off `spike/district-footprint`).
**Goal:** a clean single reactor **+ footprint at ALL zoom levels**, which the runtime deep-clone cannot deliver (its
mid-zoom LOD geometry is shared across all Industry districts — see `District-Footprint-Investigation.md`).

## Verdict: VIABLE. Register a dedicated selector under the reactor's wonder-name; the game renders + LODs it natively.

### Why this dodges the deep-clone wall

The deep-clone failed at mid-zoom because it swaps a **borrowed, shared** selector at runtime, and the mid/far LOD elements
resolve through the shared `pairs` table (can't privatize per-tile). The dedicated-visual approach never borrows or swaps:
the reactor gets its **own** selector, and the **game's own criteria resolution** draws it at every zoom band with the
engine's native LOD handling. No runtime per-frame swap → no perf hit, no mid-zoom donor.

### How the selector is chosen (confirmed by decompile)

District building visuals are **criteria-resolved**, not hard-pointed:
- `FxEvolverMaterialLevelBuildSelector` picks materials via `SelectionMode` → a criteria axis
  (`AssetReferenceRepository.CriteriaIndexFrom(selectionMode, name, out criteriaIndex, out valueIndex)`,
  `Amplitude.Mercury.Terrain.dll` ~line 33108).
- Criteria axes include Biome (4), TerrainType (3), Faction, **BuildingVisualAffinity**, and — for wonders — an
  **`ArtificialWonder`** matrix keyed by wonder NAME (`AssetReferenceRepository.databaseMatrices1D`, the matrix whose
  `Name == "ArtificialWonder"`, cells indexed by `CriteriaNames`).
- The breeder reactor is a wonder, so its visual can be keyed by its own name — dedicated, not shared with Industry.

### The registration hook already exists

`UniversalInject.RepoDump.cs` → `PollWonderRows()` / `FillWonderCell(wname, guid)` already:
- Finds the `ArtificialWonder` 1D matrix in `AssetReferenceRepository.databaseMatrices1D`.
- Locates the reactor's name on the `CriteriaNames` axis and writes a GUID into its cell (`matrix.Add(StaticString(wname),
  guid, null)` — the boxed-struct/shared-array mutation trick).
- Driven by the `WonderNativeRows` config (`wname=guid;...`).

The current comment says the cell is filled "AFTER swap went live (fallback only — the tile draws our private leaf)" — i.e.
**the game already renders from that cell** for non-plugin users. So pointing the cell at a *proper dedicated selector* and
**dropping the runtime swap** makes the game render our selector natively, at all zoom bands.

## The one remaining piece: BUILD the dedicated selector asset

The cell needs a **loadable GUID** → the selector must be a **baked asset** (runtime-constructed materials have no
persistent GUID the DB can resolve). The asset is a `FxEvolverMaterialLevelBuildSelector` (or the minimal tree the engine
needs) containing:
1. **Our reactor building** — an `FxEvolverMaterialLevelBuildElement` pointing at our baked `BreederReactor_FxMesh`, shown
   across the close/mid LOD bands (one mesh at all bands is fine for a wonder; real LOD levels optional).
2. **The footprint** — a decal drawer referencing the city-map decal (reuse the Industry `Decal_CityMap_*` — tile-agnostic —
   or author one), so the far band shows the footprint the way vanilla does.
3. **Our texture/output layer** — the private-layer + albedo recipe we already proved (`ClonePrivateOutputLayer` /
   `BindAlbedo`), baked in rather than bound at runtime.

### Open questions for the build phase
- **Authoring path:** extend `DistrictBaker` to emit a selector asset (today it bakes only the `FxMesh`), or author the
  selector in the Unity project by hand and bake its GUID. Which is less work?
- **Minimal renderable structure:** what is the smallest selector tree the engine will render as a building + footprint?
  (Clone a vanilla single-building selector as the template and strip it down, vs. build from scratch.)
- **Decal wiring:** does a decal drawer render from our selector via the game's native resolution the same way it does
  inside the Industry selector? (The decal is texture-based, tile-agnostic — likely yes.)
- **Affinity vs. wonder-name precedence:** the reactor currently also sets `ConstructibleVisualAffinity = Base_Industry`.
  Confirm the `ArtificialWonder` name cell wins (or clear the affinity) so our selector is the one resolved.

## ⚠️ CORRECTION (build spike, first check) — the reactor is NOT a wonder

The wonder-name premise above is **FALSE for the breeder reactor.** A `[RepoDump]` of the live `AssetReferenceRepository`
shows the `ArtificialWonder` name axis is 760 entries — all real wonders (`Extension_ArtificialWonder_Era*_*`), HolySites,
and participations. **`Extension_Base_BreederReactor` is not in it.** The reactor is a **base extension district**; its
visual is resolved by the **`BuildingVisualAffinity`** criterion (currently `DistrictVisualAffinity_Base_Industry`), on a
**34-value** affinity axis — not the wonder-name matrix. So `FillWonderCell` / `WonderNativeRows` (which only fills the
`ArtificialWonder` matrix, and today only carries the Oracle) can never register the reactor. Good news: the spike caught
this before any asset was built.

### The path that actually applies: a dedicated BuildingVisualAffinity

Same idea (native criteria-resolved selector, no runtime swap), different axis:
1. **Add a custom affinity value** (e.g. `DistrictVisualAffinity_BreederReactor`) to the `DistrictVisualAffinity` matrix
   via the same `matrix.Add(StaticString, guid, null)` mechanism `FillWonderCell` uses — pointing at our dedicated selector
   GUID. Open: does adding a NEW value to a criteria axis take (vs. only overwriting existing cells)?
2. **Make the reactor resolve to it** — set its criteria value to the custom affinity. The affinity is written at
   `SetCriteriaValue(BuildingVisualAffinityCriteriaIndex, value)` (terrain ~35161) from the district's
   `ConstructibleVisualAffinity`. Either change the reactor's definition data to the custom affinity, or intercept and
   override that criteria value at runtime for the reactor's tile. Open: which is workable.
3. **Build the dedicated selector asset** — unchanged from above (reactor element + footprint decal + baked texture layer).
4. Since the custom affinity is used ONLY by the reactor, the selector is dedicated — other Industry unaffected, and the
   engine resolves + LODs it natively.

**Risk:** whether a criteria axis accepts a brand-new value at runtime is unverified (the wonder matrix work only ever
*filled existing empty cells* — every wonder name was already an axis entry). If new axis values don't take, the fallback is
to make the reactor a real `ArtificialWonder` in data (bigger change) or accept the runtime deep-clone's close+far result.

## Status
Feasibility premise **corrected**: the reactor is affinity-resolved, not wonder-named. The dedicated-visual path is still
plausible but now hinges on **adding a new `BuildingVisualAffinity` value** (unverified) plus building the selector asset.
Next concrete step: a tiny probe — try `matrix.Add` on the `DistrictVisualAffinity` matrix with a test value and see if the
axis grows and resolves, before investing in the selector asset build.
