# District Strategic Footprint — Investigation (spike)

**Status:** ROOT CAUSE FOUND. The footprint is the city-map decal system, present in the reactor's native tree — our
isolate injection was deleting it by replacing the whole selector with a single leaf. Fix identified (clone the selector,
not a leaf); implementation pending.
**Branch:** `spike/district-footprint`. **Not on master** — master carries only the shipped wins below.

The goal: a custom district (the breeder reactor) should show a **persistent top-down footprint silhouette** at
strategic zoom / in battle, the way vanilla districts do — instead of just vanishing when the building demotes.

This was a long, branching hunt. This document records **where the footprint actually lives**, the **complete list of
falsified leads** (so nobody re-walks them), the **wins the hunt produced along the way**, and the **concrete next
steps**.

---

## THE BREAKTHROUGH — the footprint is the city-map DECAL system

Enumerating `FxManager` components (`GetFxComponent(int)` / `FxComponentCount`, 21 components) surfaced a **third
level-build material type**, alongside the ones we already knew:

| Material | Role | How we relate to it |
|---|---|---|
| `FxEvolverMaterialLevelBuildSelector` | the close-up **building** | this is what our mesh injection replaces |
| `FxEvolverMaterialLevelBuildImpostor` | **vegetation** billboards (SpeedTree) | ruled out — 64 entries, all `Veget_*`/`POI_*` |
| **`FxEvolverMaterialLevelBuildDecal`** | **the city-map footprint** | **texture-based decal on the ground** |

The decal:
- Is **texture-based**, not mesh-based: `decalMesh` is empty; the footprint lives in `layer0/1/2`
  (`FxLevelBuildDecalTextureEntryProperty`) — a projected decal texture.
- Is **not in the district's plbc channels.** Dumping the reactor's plbc: `[0]` building (our leaf), `[1]`
  emitter→`FxEvolverMaterialLevelBuildMatching`, `[2]` null, `[3]` emitter→`Selector`s. **No decal.**
- Lives in a **global singleton** `FxEvolverDescriptorLevelBuildDecal` (reach via its static `GetInstance(bool)`),
  holding **1194 decal materials** named `Decal_CityMap_<biome>_<category>_<style>` and `Decal_CityBricks_*`
  — Industry / Money / Science / Garden / PublicOrder × Cold / Hot / Temperate. e.g. `Decal_CityMap_Industry_Gravel_01`,
  `Decal_CityMap_Fims_Industry_SimpleShape_16`, `Decal_CityMap_Industry_Trash_04`.

**So the vanilla footprint assets exist and are named** — the footprint is a strategic-map decal a district selects by
**biome × category** and draws when it demotes.

### ROOT CAUSE — our injection DELETES the footprint (DEFINITIVE)

Dumping **every district's** plbc material tree (`DumpAnyDistrictTree` — recurses emitter items + selector cache) settled
it beyond doubt:

- **Vanilla district** (e.g. `Extension_Base_Industry`) = **1 channel**, whose top `FxEvolverMaterialLevelBuildSelector`
  holds a **deep tree** of nested Selectors/Emitters that **includes the `FxEvolverMaterialLevelBuildDecal` drawers** —
  the footprints.
- **Reactor NATIVE** (`DistrictRepoint=false`, affinity `Base_Industry`) = **identical** to vanilla Industry: 1 channel,
  the same deep tree, **19 decal nodes present.** The footprint was there all along.
- **Reactor WITH our isolate injection** = **4 channels, ZERO decals.**

`PointTileAtPrivateLeaf` does `CollectLeaves(selector) → clone ONE building leaf → force that leaf onto the channel`. It
**replaces the entire selector** — deep tree, decals and all — **with a single building leaf**, every frame. We were
amputating the footprint subtree ourselves. It was never a missing asset or an unreachable system.

### THE FIX — clone the selector, not a leaf

1. **Clone the *selector*** (`Instantiate`). Its `NonSerialized` runtime cache resets, so on `Load` it re-builds its full
   tree **fresh and private** — decals included.
2. **Swap only the building leaves' `fxMesh`** inside the clone. Building `Element` leaves carry `fxMesh`; `Decal` leaves
   carry `decalMesh` — `CollectLeaves` already keys on `fxMesh`, so decals are untouched by construction.
3. **Force the cloned *selector*** onto the channel (instead of a lone leaf).

Result: custom building up close, vanilla footprint decals survive and draw at strategic zoom, still scoped to our tile
(private clone). This is a change to the injection core (`BuildPrivateLeaf`/`PointTileAtPrivateLeaf` — clone one level up).

### The fix is subtler than expected — the mesh swap ITSELF drops the decals

Re-verified global mode (`DistrictIsolate=false`, Industry affinity, zoomed out): **still no footprint.** This is a key
refinement: `ApplyLeaves` only swaps `fxMesh` on building leaves, and `CollectLeaves` skips decals (they carry
`decalMesh`), so **our swap never touches the decal leaves** — yet they stop rendering. So it's **not** merely the
selector-replacement; **swapping the building mesh drops the decals indirectly.** Prime suspects:
- **GPU mesh buffer eviction/collision** — registering our large custom mesh (via the building leaf's `Load` →
  `GetMeshIndex`) evicts or reindexes the decal meshes (the shared 100k-vert / 256-mesh per-layer buffer).
- **A presentation-state gate** — the decal renders only when the building is in a native state our swap changes.

**Next probe:** after the swap, read each decal leaf's `meshIndex` — `uint.MaxValue`/invalid = evicted (buffer problem);
still valid = present-but-gated (state problem). That fork decides the fix. So the clone-the-selector plan still stands,
but it must swap the building mesh **without** triggering whatever the swap does to the decal indices.

---

## FALSIFIED LEADS (do not re-walk)

Every one of these was a plausible suspect, checked and ruled out in-game or by decompile:

1. **`HgFxAnchorComponent.RenderModeEnum`** — `{Default, GhostOk, GhostNOk}` = building-placement ghost, not zoom.
   Stayed `Default` through the whole zoom.
2. **Unity cameras** — `MainView` is inert (pinned at origin, matrix eye = 0, fov 60). Mercury renders through its own
   render context; Unity `Camera` properties don't reflect the zoom. (`ImpostorCamera`, `AvatarCamera`, `Camera`,
   `MainView`, `UIFxCamera` — none usable.)
3. **The per-frame private-leaf force** — both isolate (private leaf) and global (shared-leaf swap) modes shrink
   identically. Not the injection method.
4. **The impostor system** — reached `FxComponentImpostorManager`; its descriptor holds **only vegetation/POI** (64
   `Veget_*`/`POI_*`, SpeedTree atlas). District buildings don't use it. (Re-tested at full zoom-out with a live
   descriptor re-read to rule out a caching miss — still zero building impostors.)
5. **`fadeInOutMode`** — passing `instantAppear:true` made no difference to the zoom transition; it only governs the
   on-load reveal ramp.
6. **`IconicConstructibleType` DB** — `MissileSilo` has a non-null entry, shared with Military/NuclearTest. Not the
   differentiator.
7. **The entire `AssetReferenceRepository`** — dumped all 49 databases (1D + both 2D affinity DBs). Compared
   `MissileSilo` vs `Base_Industry` across every affinity-keyed cell. The only gaps are `ConstructibleEvents` (VFX) and
   `*/District/Main` `Level2` (a build-evolution stage). **No footprint cell.**
8. **The `ConstructibleVisualAffinity` (the affinity swap)** — swapping to `Base_Industry` did not add a footprint (and
   still shrank via the runtime swap). Affinity doesn't drive the strategic transition.
9. **The channel material swap at zoom** — traced: the channel is only ever `selector → our-leaf` and **never swaps** at
   zoom-out. The game doesn't repoint the channel; it demotes in place.
10. **Selector-vs-leaf** — global mode keeps the selector *intact* (swaps only the mesh inside it) and still shows no
    footprint. Keeping the selector's strategic behavior is not enough.
11. **`FxMesh.lod`** — `FxMesh` has a real `lod` (Guid to a lower LOD) + `lodType`. Baked a far-LOD FxMesh and set it
    (verified in the asset). **Dead runtime path**: the consumer `fxMeshContentLods` is *never assigned anywhere in the
    code* — declared + read, zero writes — so it's null for everyone, and the LOD chain is never used
    (`meshIndexLod0 = uint.MaxValue`, `lodData = 0` on our leaf). Reverted.

The footprint works for the donor mesh and not ours, and none of these reachable, settable differences reproduces it —
because it was never in any of them. It's the decal.

---

## WINS THE HUNT PRODUCED (shipped, on master)

The footprint chase kept dragging us down untested paths, and those paths paid off:

- **Foundation plinth** (`03431aa`, `3d9af82`) — a bake knob extrudes the building footprint straight down as a concrete
  plinth so a district on a cliff plants on a base instead of overhanging. Cliff-verified.
- **Atlas-layer texture binding** (`3b18185`) — `BindAlbedo` now binds our sheet on an *atlas-managed* layer (falls back
  to the shader's albedo slot when nothing is bound), so a custom district can wear **any city affinity** and keep its
  texture — not just the missile silo's full-texture layer. Removes the missile-silo lock-in; enables graceful
  non-plugin fallback (a sensible vanilla building instead of a confusing missile silo).
- **Clean zoom-out** — setting a **city-district `ConstructibleVisualAffinity`** (`Base_Industry`) in the district
  definition makes it **fade/disappear cleanly** like a vanilla building instead of the awkward national-project shrink.
  This resolved the *original* complaint. (A plugin "back-off guard" `329ad6a`/`507e987` was kept as a harmless
  defensive measure, but the trace proved the affinity is what fixes the shrink, not the guard.)

**Design rule banked:** pick each custom district's `ConstructibleVisualAffinity` for the best non-plugin fallback
(closest vanilla building), not just whatever renders.

---

## Diagnostic probes (in this branch, `DistrictDebug`-gated)

- `DumpDecalDescriptor()` — reaches `FxEvolverDescriptorLevelBuildDecal` via static `GetInstance(bool)` and dumps all
  1194 decal materials (name + `decalMesh`).
- `DumpDistrictChannels()` / `DumpMatTree()` — recurse a district's plbc channels (emitter items + selector cache) and
  log every material type + any nested `Decal`.
- Reusable, deadlock-free reach patterns: `distFxManager.GetFxComponent<T>()` for Fx components; a descriptor's static
  `GetInstance(bool)` for the global singletons.
