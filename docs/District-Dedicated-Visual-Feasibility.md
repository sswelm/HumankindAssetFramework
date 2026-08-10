# Dedicated District Visual — Feasibility Study

**Branch:** `spike/district-dedicated-visual` (off `spike/district-footprint`).
**Goal:** a clean single reactor **+ footprint at ALL zoom levels** — which the runtime deep-clone cannot deliver (its
mid-zoom LOD geometry is shared across all Industry districts; see `District-Footprint-Investigation.md`).

## Verdict: PLAUSIBLE via a dedicated `BuildingVisualAffinity` — hinges on ONE unverified question

The idea is sound: give the reactor its **own** criteria-resolved selector so the **game's own resolution + LOD system**
draws it at every zoom band — no runtime swap, so no perf hit and no mid-zoom donor. The open risk is whether we can add a
**new value to a criteria axis** at runtime. That single yes/no decides the whole path.

### Why a native dedicated selector dodges the deep-clone wall

The deep-clone failed at mid-zoom because it swaps a **borrowed, shared** selector at runtime, and the mid/far LOD elements
resolve through the shared `pairs` table (can't be privatized per-tile). A dedicated visual never borrows or swaps: the
reactor resolves to its **own** selector through the engine's normal path, LOD and all. Nothing runs per-frame.

### How a district's building visual is chosen (confirmed by decompile)

Building visuals are **criteria-resolved**, not hard-pointed:
- `FxEvolverMaterialLevelBuildSelector` resolves materials via `SelectionMode` → a criteria axis
  (`AssetReferenceRepository.CriteriaIndexFrom(selectionMode, name, out criteriaIndex, out valueIndex)`,
  `Amplitude.Mercury.Terrain.dll` ~line 33108).
- The criterion that resolves a **base extension district's** building is **`BuildingVisualAffinity`** — set per tile at
  `SetCriteriaValue(BuildingVisualAffinityCriteriaIndex, value)` (terrain ~35161) from the district's
  `ConstructibleVisualAffinity`. The reactor currently uses `DistrictVisualAffinity_Base_Industry` (a 34-value axis).
- (Wonders use a *different* axis — an `ArtificialWonder` matrix keyed by name. **The reactor is NOT a wonder** — see the
  falsified-hypothesis appendix; this is why the wonder-cell machinery cannot register it.)

### The applicable path: a custom affinity value that only the reactor uses

1. **Add a custom affinity value** — e.g. `DistrictVisualAffinity_BreederReactor` — to the `DistrictVisualAffinity` matrix,
   pointing at our dedicated selector's GUID. Mechanism on hand: the `matrix.Add(StaticString, guid, null)` call
   `FillWonderCell` uses (`UniversalInject.RepoDump.cs`), which mutates the live repository's matrix via its shared
   cells/axis arrays. **⚠️ UNVERIFIED:** the wonder work only ever *filled existing empty cells* — every wonder name was
   already on the axis. Whether `Add` **grows** an axis with a brand-new value (and whether resolution then honours it) is
   the make-or-break unknown.
2. **Make the reactor resolve to it** — point the reactor's `BuildingVisualAffinity` criteria value at the custom affinity:
   either change its definition data (`ConstructibleVisualAffinity`), or intercept/override the criteria value at runtime
   for the reactor's tile. (Open: which is cleaner.)
3. **Build the dedicated selector asset** (see below).
4. Because the custom affinity is used **only** by the reactor, the selector is dedicated: other Industry districts are
   untouched, and the engine resolves + LODs it natively at all zoom bands.

## The build piece: a baked dedicated selector asset

The affinity cell needs a **loadable GUID**, so the selector must be a **baked asset** (runtime-constructed materials have no
persistent GUID the DB can resolve). It is a `FxEvolverMaterialLevelBuildSelector` (or the minimal tree the engine renders)
containing:
1. **Our reactor building** — an `FxEvolverMaterialLevelBuildElement` pointing at our baked `BreederReactor_FxMesh`, present
   across the close/mid LOD bands (one mesh at all bands is fine; real LOD levels optional).
2. **The footprint** — a decal drawer referencing a city-map decal (reuse an Industry `Decal_CityMap_*`, tile-agnostic, or
   author one) so the far band shows the footprint like vanilla.
3. **Our texture layer** — the private-layer + albedo recipe we proved at runtime (`ClonePrivateOutputLayer` / `BindAlbedo`),
   baked in instead of bound live.

### Open questions for the build phase
- **Authoring path:** extend `DistrictBaker` to emit a selector asset (today it bakes only the `FxMesh`), or author the
  selector in the Unity project and bake its GUID. Which is less work?
- **Minimal renderable structure:** the smallest selector tree the engine renders as building + footprint — likely clone a
  vanilla single-building selector and strip it, rather than build from scratch.
- **Decal wiring:** does a decal drawer render from *our* selector via native resolution the way it does inside the Industry
  selector? (Texture-based, tile-agnostic — likely yes.)

## Status & next step

Premise **corrected** (the reactor is affinity-resolved, not a wonder). The dedicated-visual path is plausible but gated by
the unverified axis-growth question. **Next concrete step — a tiny probe before any asset work:** call `matrix.Add` on the
`DistrictVisualAffinity` matrix with a test value + a known selector GUID, point the reactor at that value, and check whether
the axis grows and the tile resolves to it. If yes → build the selector asset. If no → fall back to making the reactor a real
`ArtificialWonder` in data (bigger), or accept the runtime deep-clone's close+far result.

---

## Appendix — falsified initial hypothesis (audit trail)

The study first assumed the reactor was a **wonder**, registerable by name in the `ArtificialWonder` matrix via the existing
`PollWonderRows`/`FillWonderCell` + `WonderNativeRows` path (which renders natively as the non-plugin fallback). The build
spike's first check falsified this: a `[RepoDump]` of the live `AssetReferenceRepository` shows the `ArtificialWonder` name
axis is **760 entries — all real wonders / HolySites / participations**, and **`Extension_Base_BreederReactor` is not among
them**. `WonderNativeRows` today carries only the Oracle. The reactor is a base extension resolved by affinity, so the
wonder-name path cannot register it — hence the affinity path above. (Value of the spike: this was caught from one dump,
before building any asset against the wrong axis.)
