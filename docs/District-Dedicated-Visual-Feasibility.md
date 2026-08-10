# Dedicated District Visual — Feasibility Study

**Branch:** `spike/district-dedicated-visual` (off `spike/district-footprint`).
**Goal:** a clean single reactor **+ footprint at ALL zoom levels** — which the runtime deep-clone cannot deliver (its
mid-zoom LOD geometry is shared across all Industry districts; see `District-Footprint-Investigation.md`).

## Verdict: the RUNTIME dedicated-affinity path is DEAD — a criteria axis cannot grow at runtime

The idea is sound in principle (give the reactor its **own** criteria-resolved selector so the game draws it natively at
every zoom band, no runtime swap). But it hinged on one unverified question — can we add a **new value to a criteria axis**
at runtime? — and a probe answered **NO, decisively.**

### The axis-growth probe (`ProbeAxisGrowth`, DistrictDebug-gated)
Called `matrix.Add(new StaticString("HAF_AxisProbe"), guid, null)` on two live 1D matrices and measured the axis before/after
(same box, a fresh box read from the array, and after writing the box back):
```
'ArtificialWonder'        : before=760 afterBox=760 afterFresh=760(found=False) afterWriteback=760(found=False)
'*/District/Construction' : before=34  afterBox=34  afterFresh=34(found=False)  afterWriteback=34(found=False)
```
**`Add` does not grow the `CriteriaNames` axis at all** — not on the box, not persisted, and the new value is never found.
It only fills the cell of a name **already on the axis** (which is exactly why the wonder work succeeded — every wonder name
pre-existed). The axis is fixed at data-load time. So a **custom affinity value cannot be introduced at runtime**, and the
reactor (whose name isn't on the `ArtificialWonder` axis either) cannot be given a dedicated criteria-resolved selector by
the plugin.

### What's left (all data/content, not runtime)
- **Define the reactor as a real `ArtificialWonder` in the mod's DATA** so its name lands on the axis at load time (then
  `FillWonderCell` could point its cell at a dedicated selector). This is a definition-level change to the mod's databases
  (authored data — a design decision for the user), and it **still** needs the dedicated selector asset built.
- **Author the dedicated selector + register it via data** through the game's own datatable pipeline (not runtime `Add`).
- **Accept the runtime deep-clone's close+far result** (footprint at strategic, reactor complex up close), or the shipped
  clean single reactor (no footprint — original complaint already solved by clean-disappear).

**Bottom line:** there is no runtime path to a clean cross-zoom footprint. Every runtime avenue is now walled (deep-clone
mid-zoom LOD is shared; criteria axes can't grow). A clean result requires **authoring the reactor as a data-level wonder /
district with its own baked selector** — a content-pipeline effort, outside runtime injection.

---
### (original framing, now falsified by the probe)
The idea was: give the reactor its own criteria-resolved selector so the game's own resolution + LOD draws it at every zoom
band. The blocker turned out to be that criteria axes are immutable at runtime (above).

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

## The proper native-style path (concrete) — data-authored district visual

"Configure it like a native district" means giving the reactor its **own** district-visual, in **data**, the way every
native type (Industry, Science, MissileSilo…) has one. Located the exact wiring:

- **Where the reactor is configured:** `ENCReload/Assets/Databases/Settlement/ConstructibleCommonExtensionDefinitionENC.asset`
  → `Extension_Base_BreederReactor` (m_Name ~601) → **`ConstructibleVisualAffinity: DistrictVisualAffinity_Base_Industry`**
  (~680). That one line is why it borrows the Industry factory selector.
- **The affinity vocabulary** is a fixed axis of ~34 native district *types* (`DistrictVisualAffinity_Base_Industry`,
  `_Base_Science`, `_Base_LuxuryExtractor`, `_MissileSilo`, …), each mapping to its own authored `CityMapSelector_*` (which
  carries that type's building geometry + LODs + city-map footprint decals). All are in use; none is spare.
- **Runtime can't add a new axis value** (probe proved it), so a dedicated affinity must be introduced at **data-load** via
  the mod's datatables, not by the plugin.

### The build (a real content-pipeline chapter, in order)
1. **Author a district-visual selector asset for the reactor** — a `FxEvolverMaterialLevelBuildSelector` (or the minimal
   tree the engine renders) referencing our baked `BreederReactor_FxMesh` as its building element + a city-map decal drawer
   for the footprint + our texture layer. New baker capability: `DistrictBaker` today bakes only the `FxMesh`; it (or a
   Unity-authored asset) must emit a *selector* asset with a GUID. Likely approach: clone a native single-building selector
   at bake time and swap in our mesh, rather than build from scratch.
2. **Introduce a dedicated affinity + mapping in data** — add `DistrictVisualAffinity_Base_BreederReactor` (or similar) to
   the criteria vocabulary and a district-visual datatable row mapping it → our selector GUID, through the game's datatable
   modding pipeline (so the axis grows at load). **Open:** confirm the exact datatable(s) the native `Base_Industry →
   CityMapSelector_Industry_00` mapping lives in, and that a mod can extend the affinity axis at load.
3. **Point the reactor at it** — change `ConstructibleVisualAffinity` in the definition asset above to the new value
   (authored data — the user's call).
4. **Drop the runtime injection for the reactor** — no plugin swap needed; the engine resolves + LODs our selector
   natively, giving one clean reactor + footprint at every zoom, like any native district.

### Honest scope
This is a genuine content-pipeline + framework chapter (baker emits a selector; datatable authoring to grow the affinity
axis at load), not a config tweak. The next concrete investigative step is #2's open question: **find the native datatable
that maps a `DistrictVisualAffinity` value to its `CityMapSelector`, and verify a mod can add a row + a new axis value at
data-load.** That gates the whole proper path, the same way the runtime axis-growth probe gated the runtime path.

---

## Appendix — falsified initial hypothesis (audit trail)

The study first assumed the reactor was a **wonder**, registerable by name in the `ArtificialWonder` matrix via the existing
`PollWonderRows`/`FillWonderCell` + `WonderNativeRows` path (which renders natively as the non-plugin fallback). The build
spike's first check falsified this: a `[RepoDump]` of the live `AssetReferenceRepository` shows the `ArtificialWonder` name
axis is **760 entries — all real wonders / HolySites / participations**, and **`Extension_Base_BreederReactor` is not among
them**. `WonderNativeRows` today carries only the Oracle. The reactor is a base extension resolved by affinity, so the
wonder-name path cannot register it — hence the affinity path above. (Value of the spike: this was caught from one dump,
before building any asset against the wrong axis.)
