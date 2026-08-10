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

### ✅ GATING QUESTION ANSWERED — the data path is VIABLE (verified in the mod's own data)

Traced it end to end in the ENCReload mod's data:
- **Mods CAN add an affinity value at data-load** — proven: the mod already defines a custom one,
  `NationalProject_NuclearTest`, in `Assets/Databases/SettlementPresentation/ConstructibleVisualAffinityDefinition.asset`
  (game defines 26; the mod adds its own). Runtime can't grow the axis, but **data-load can.**
- **The affinity → visual mapping datatable** is `*/District/Main.Level1` (and `.Level2`) inside
  `Assets/~References/WorldPresentation/DistrictDefinition_ContentCollection.asset`. Each row is exactly:
  ```
  - Name: NationalProject_NuclearTest        # the affinity value
    Value: { a: -1883953677, b: 1215187674, c: -1533191005, d: -2060159479 }   # GUID of that type's CityMapSelector
    NameColor: {r:0,g:0,b:0,a:0}
    Comment:
    NameLocked: 0
  ```
- **`NationalProject_NuclearTest` maps to a SINGLE-BUILDING national-project visual WITH a footprint** — the clean shape we
  want (one building + footprint, native LODs), not the Industry multi-building factory. And because it's data-authored,
  **no plugin is needed** → the missile-silo fallback that forced the Base_Industry switch is a non-issue.

### The verified build recipe (this is the plan)
1. **Author the reactor's CityMapSelector asset** — bake/clone a single-building selector (the NuclearTest/MissileSilo one
   is the ideal template: single building + footprint) with our `BreederReactor_FxMesh` + our texture as its building
   element, keeping its decal/footprint. Get its GUID. (Baker capability: clone the template selector at bake time and
   swap the mesh, rather than authoring from scratch.)
2. **Define a dedicated affinity** — add `DistrictVisualAffinity_Base_BreederReactor` (or `NationalProject_BreederReactor`)
   to the mod's `ConstructibleVisualAffinityDefinition.asset`, mirroring the existing `NationalProject_NuclearTest` entry.
3. **Map affinity → our selector** — add a mod content row to `*/District/Main.Level1` **and** `.Level2` (via the mod's own
   `DistrictDefinition_ContentCollection`-style asset that the game merges): `Name: <new affinity>` → `Value: <our selector
   GUID>`.
4. **Point the reactor at it** — set `ConstructibleVisualAffinity` in `ConstructibleCommonExtensionDefinitionENC.asset`
   (`Extension_Base_BreederReactor`) to the new affinity.
5. **Drop the runtime injection** for the reactor — the engine now resolves + LODs our selector natively: one clean reactor
   + footprint at every zoom, like any native district, with no plugin.

**Only real unknown left:** step 1's authoring — whether the baker can cleanly clone the template selector and swap the mesh
into a shippable asset with a stable GUID. Everything downstream (2-5) is verified data edits following patterns the mod
already uses.

### Step 1 investigation — findings (editor probe, `DistrictBaker` → Tools/HAF/District/Probe)

Built an editor probe that loads a native `*/District/Main` visual by its Amplitude GUID and dumps its material tree
(`district_visual_dump_*.txt`). Results:

- **✅ CRUX RESOLVED — templates load in-editor.** `FxEvolverMaterial.TryLoad(guid, true)` works in the editor (not just at
  runtime). So we CAN reach and clone the native selector assets. (Committed: ENCReload `spike/district-dedicated-visual`.)
- **Both candidate templates are MULTI-building, not single:**
  - `NationalProject_NuclearTest` → `LvlBuild_Brick_Main_NationalProject_NuclearTest_01` (an emitter): **13** building
    `Element`s (distinct `fxMesh` each) + footprint decals + particle emitters.
  - `DistrictVisualAffinity_MissileSilo` → `CityMapSelector_MissileSilo`: **26** elements / **98** decals — and it **shares
    the same building-mesh family** as NuclearTest (identical `fxMesh` GUIDs). One military building set, reused.
- **Implication:** there is no clean one-building template to clone-and-swap. Districts are inherently multi-building
  compositions. So authoring the reactor's visual is **clone a template + reduce its building items down to ONE** (our
  reactor element, at the central position) **while keeping the decal/footprint subtree**, rather than a 1:1 mesh swap.
- **Structure shape** (from the dump): a root emitter whose `levelBuildItems` are the positioned building `Element`s plus
  nested `Selector`s that carry the footprint `Decal`s. So the reduction is: in the cloned root emitter, keep one
  Element item (point it at our baked reactor element), null the other Element items, leave the decal-bearing items intact.

**Composition mapped (NuclearTest, with positions + bboxes):** the elements are cleanly separable by bbox size —
- Root emitter bbox `9.09×2.00×9.48` = the whole district footprint envelope.
- A nested sub-emitter at `pos(0,0,0)`: the flat **ground slab** (`fxMesh=0d34b7b9`, bbox `5.98×0.05×6.91`) + its
  **footprint decal** + a stack of **particle FxEmitters** (steam/smoke).
- **One large main building** (`c57a539c` at `(0.51,0.88)`, bbox `4.43×0.68×2.56`).
- **~11 small props** (bbox `0.10–0.45`) scattered at various positions — pipes/details.
- Several `Selector → Decal` sub-trees = the city-map **footprint** decals.

So **large bbox = main structure, small bbox = prop** — a clean rule to reduce by. **Reduce-to-one recipe (verified by the
layout):** in the cloned template, keep ONE large-bbox Element slot → point it at our baked reactor element (centered),
**null the small-prop Element items**, and **leave every Decal / decal-Selector item intact** (the footprint). Optionally
keep the ground slab + steam emitters for flavor. This is the principled reduction the authoring command will apply.

**Step 1 status:** fully investigated. Templates load in-editor; structure + composition mapped; the reduce-to-one is
principled (bbox rule). Remaining build = the authoring command: (a) bake our reactor as an `FxEvolverMaterialLevelBuildElement`
asset (its own GUID), (b) clone the NuclearTest template, (c) apply the reduce-to-one (repoint one big slot → our element,
null props, keep decals), (d) save → the reactor's CityMapSelector GUID for the `*/District/Main` data rows.

### Step 1 BUILD — implemented, and it hit a serialization WALL (the honest blocker)

Built the baker commands (ENCReload `DistrictBaker`, branch `spike/district-dedicated-visual`):
- **`1b. Bake Reactor District Element`** — clones the template's largest building element, swaps `fxMesh` to
  `BreederReactor_FxMesh`, clears the donor LOD chain. Produces `BreederReactor_Element.asset`.
- **`1c. Bake Reactor District Selector`** — clones the template emitter, edits the CLONE's own `levelBuildItems`
  (repoint one big slot → our element, null the 34 prop slots, **keep all 104 decal/emitter items = the footprint**),
  nulls the broken `companion`. Produces `CityMapSelector_BreederReactor` (`BreederReactor_Selector.asset`).

Both **load and edit correctly in-editor** (reduce-to-one works, footprint preserved) — but **saving them as project assets
produces broken cross-bundle references** that the mod bundle build (`[Worker0]`) rejects:
- **`m_Script` zero-guid** (both assets) — `Instantiate` of a runtime-loaded DLL ScriptableObject loses the script GUID.
  **FIXABLE:** create via `ScriptableObject.CreateInstance(type)` (valid `m_Script`, like the mod's own assets which use
  `guid: b310e23…, type: 3`) and copy fields, instead of `Instantiate`.
- **`outputLayer` zero-guid** (the element) — **THE WALL.** The element's `outputLayer` points to a game-bundle
  `FxOutputLayer` (`LevelBuild_Brick_01OutputLayer`) that exists **nowhere in the mod project** (not in `~References`), so it
  can't be persisted or resolved. This is the exact asset the runtime plugin had to **clone live** (`ClonePrivateOutputLayer`)
  precisely because it isn't authorable in the project.

**Implication — pure-data authoring is blocked by the output layer.** A district element must reference an `FxOutputLayer`,
and that layer is not exposed to modding. Options for the next focused session:
1. **Bake/clone the FxOutputLayer into the mod project** (author a project asset for it) so the element can reference it —
   the real "pure data" fix, but the layer is a complex game asset (streaming render-outputs, atlas) to reproduce.
2. **Hybrid:** author the structure + footprint in data (this works), and bind only the *output layer/texture* at runtime
   with a minimal plugin hook (a fraction of the old injection) — pragmatic, not 100% plugin-free.
3. Confirm whether the game re-resolves a null `outputLayer` from the element's descriptor at load (would remove the wall).

**Net:** the approach, tooling, reduce-to-one, and footprint preservation are all proven; the blocker is one specific
un-authorable game asset (the output layer). That's the crux to crack next.

---

## Appendix — falsified initial hypothesis (audit trail)

The study first assumed the reactor was a **wonder**, registerable by name in the `ArtificialWonder` matrix via the existing
`PollWonderRows`/`FillWonderCell` + `WonderNativeRows` path (which renders natively as the non-plugin fallback). The build
spike's first check falsified this: a `[RepoDump]` of the live `AssetReferenceRepository` shows the `ArtificialWonder` name
axis is **760 entries — all real wonders / HolySites / participations**, and **`Extension_Base_BreederReactor` is not among
them**. `WonderNativeRows` today carries only the Oracle. The reactor is a base extension resolved by affinity, so the
wonder-name path cannot register it — hence the affinity path above. (Value of the spike: this was caught from one dump,
before building any asset against the wrong axis.)
