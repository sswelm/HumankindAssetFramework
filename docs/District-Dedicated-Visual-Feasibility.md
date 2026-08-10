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

## Status
Feasibility confirmed at the mechanism level: the criteria-resolution + wonder-cell registration path is real, already
wired, and renders natively (no runtime swap). Next step is a build spike: produce a minimal dedicated selector asset and
point `WonderNativeRows` at it, with the runtime deep-clone/isolate swap disabled for the reactor.
