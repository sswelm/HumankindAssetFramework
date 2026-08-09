# District Strategic Footprint — Investigation (spike)

## TL;DR — what we learned (read this first)

**Goal:** a custom district (the breeder reactor) should show a persistent top-down **footprint** at strategic zoom, the way
vanilla districts do, instead of shrinking away awkwardly.

**What the footprint actually is:** the vanilla **city-map decal system** — texture decals (`Decal_CityMap_<biome>_*`, a
1194-material global `FxEvolverDescriptorLevelBuildDecal`). For a district they live **inside channel [0]'s
`FxEvolverMaterialLevelBuildSelector`, interleaved with the building geometry** — not a separate channel. The plbc has
effectively **one** composited level-build content channel (`mainLevelBuildComponantLayer`), so building + footprint share it.

**Why it was missing on the reactor:** our **isolate injection replaces channel [0]'s whole selector with one private leaf**,
amputating the decal subtree. It was never a missing asset, an unreachable system, or a GPU gate (that conclusion came from
pre-swap dumps + the reactor's `isolate=true` registry flag silently overriding every "global" test — see the audit log below).

**What renders the footprint:** keeping the selector. **Global mode keeps it → footprint renders** (verified in-game + dump:
231 decals / 120 rendering, identical to native). But global swaps the *shared* Industry selector, so every Industry tile
becomes a reactor.

**Per-tile fix (built, on the spike branch):** a **private deep-clone** of channel [0]'s selector (`DeepCloneMat` +
`PointTileAtClonedSelector`). It **works** for close-zoom + far-zoom: footprint renders on **only the reactor tile**, our
reactor mesh wears our albedo (one shared private `FxOutputLayer`), and the dome count is tunable. Every reusable building
block exists (`BuildPrivateLeaf`, `CollectLeaves`, `LoadFxMaterial`, `ClonePrivateOutputLayer`, the texture path).

**The wall — mid-zoom LOD.** A district renders different geometry per zoom band: close = detailed buildings (per-tile
cache/emitter-items — **cloneable**), mid = a lower-detail LOD set that resolves through the selector's **`pairs` table into
SHARED materials** (**not** cloneable), far = the decals. A post-swap dump proved it: of 483 visible elements only **75 were
ours**, ~408 were shared donor. Those shared LOD elements can't be privatized — repointing is impossible (GUIDs), a per-frame
catch tanked perf and still missed them, and global-swapping them would reactor-ify every Industry district's mid-zoom. So
**close-zoom is per-tile-controllable; mid-zoom LOD is shared and is not.** That's the architectural limit of runtime
injection here.

## Roadmap — clean reactor + footprint at ALL zoom levels

A clean cross-zoom result cannot come from more runtime selector surgery (the mid-zoom LOD is shared). It needs the reactor
to **own its geometry at every LOD band** via the content pipeline, not borrow Industry's shared selector:

1. **Dedicated district visual for the reactor** — its own selector/affinity so it is NOT sharing Industry's `pairs`/LOD
   materials. This is new authoring capability the framework does not have yet (today the DistrictFactory *injects* a mesh
   into an existing selector; it does not author a new affinity/selector). Feasibility spike: can we clone an affinity's
   selector **per district-type** (shared by all reactors, not per-tile) and register it as the reactor's own visual, so a
   one-time global swap on it stays scoped to reactors AND covers the LOD bands?
2. **Reactor LOD meshes** — the game's LOD system needs our geometry at close/mid/far. Either bake real LOD levels, or make
   every LOD band resolve to our single mesh (acceptable for a wonder). This is where the mid-zoom donor is truly fixed —
   the LOD assets are *ours*, not shared donor GUIDs.
3. **Footprint via the affinity** — keep the city-map decals from the borrowed affinity (they're per-hex, tile-agnostic), or
   author a decal set for the dedicated affinity.
4. **Fallback that already ships:** the plain isolate reactor (one clean building) + `ConstructibleVisualAffinity =
   Base_Industry` makes it **disappear cleanly** at strategic zoom — which resolved the *original* complaint (the awkward
   shrink). The footprint is the enhancement on top.

**Current state:** `UseDeepClone=false` → the shipped clean single reactor (playable, no footprint). The deep-clone is parked
on the spike branch with all knobs, for whoever builds the content-pipeline path above.

---

**Status:** ⭐ SOLVED (mechanism). The footprint is the city-map decal system living **in channel [0]'s Selector alongside
the building**. **ISOLATE mode was deleting it** — it replaces the whole channel-[0] selector with our single private leaf,
so the decal subtree (231 drawers, 120 rendering) is amputated. **GLOBAL mode keeps the selector and the footprint
renders** — verified in-game (zoom out → footprint visible) AND by post-swap dump (231 decals / 120 rendering preserved,
identical to native). The earlier "GPU selection gate" conclusion was WRONG: the reactor's registry entry is `isolate=true`,
which silently overrode every `DistrictIsolate=false` "global" test, so global was never actually tested on the reactor
until now. The fix is the original instinct — **clone the selector (private to the tile), swap the building mesh inside
the clone, keep the decals** — giving per-tile scope (not "all Industry became reactors") WITH the footprint.

**Superseded below:** the "CONCLUSIVE — gate is BELOW C#" and "ShaderDump next route" sections were built on pre-swap dumps
(the [Tree] dump fired before the per-frame swap) and the isolate override — both artifacts. The decals were *removed*, not
*gated*. Kept for the audit trail; do not act on them.
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

**Refined again — the decals are PRESENT but GATED, and texture-based.** In global mode (swap active) the reactor's tree
**still has all 19 decal nodes** (identical count to vanilla Industry) — so the swap doesn't remove or evict the decal
*drawers*; they're right there and still don't render. And every decal's `decalMesh` is **Null** — these are
**texture-based** decals (the footprint is the `layer0` decal texture on a shared quad, not a per-decal mesh). So the
building-mesh swap **gates off the decals' rendering** while leaving them in the tree.

**Open — the gating mechanism.** Why do present, texture-based decals stop drawing when we swap the building mesh? Leading
suspects: (a) the decals share the building's **output layer** / render pass, and swapping the building mesh (+ `Load`)
disturbs the shared layer's state; (b) the decal draw is gated by the building's **demotion/LOD state**, which our custom
mesh (`lodData=0`, no LOD chain) never reaches; (c) our mesh's `Load` re-registers content in a way that invalidates the
decals' render data (`FxComponentBlobDecalRenderDataBuffer`). This is a deeper rendering interaction than a data edit and
warrants a focused implementation session — the clone-the-selector fix must preserve the building's native LOD/layer/
demotion state so the decal draw isn't gated off.

**Bottom line:** the footprint is definitively the city-map decal system, present in the reactor's native tree; our
injection breaks its *rendering* (not its presence in global mode). The remaining work is understanding the render gate,
not finding the footprint.

### CONCLUSIVE — the gate is BELOW C#, in the GPU per-hexagon selection shader

A three-way A/B settled it. In global mode (Industry selector swapped; Food/Science untouched), zoom out and compare
tiles: **Food/Science show a footprint, the reactor does not** — so the decals *are* the footprint, and the swap gates
*only* the swapped districts. Then we dumped each decal's full render-readiness state (`visualOutput.OutputLayerIndex`,
`LoadedOutputLayer`, its `Atlas`, the `layerEntryCount`/`levelBuildDecalRenderDataEntryIndex` it wrote). Result — the
reactor's gated decals are **byte-identical** to Food/Science's working ones:

```
reactor (GATED):  outLayerIdx=792 loadedLayer=True atlas=True layerEntryCount=1 renderDataIdx=1044
Food    (WORKS):  outLayerIdx=792 loadedLayer=True atlas=True layerEntryCount=1 renderDataIdx=913
Science (WORKS):  outLayerIdx=792 loadedLayer=True atlas=True layerEntryCount=1 renderDataIdx=1127
```

The reactor's decals are **fully loaded, atlas-resolved, and have written their render-data entries** — indistinguishable
from a working district. So **no C#-reachable state differs** between footprint-renders and footprint-gated. The decal is
render-ready; something below C# just doesn't *place* it at the reactor's hexagon.

That something is the **GPU evolve compute shader**. Per-hexagon content lives in `FxComponentLevelBuildContent`'s
`levelBuildContentCB` (per-hexagon × `layerOutputCount*2`=16 layers); the selector's evolve dispatch
(`FxComponentLevelBuildParticleAdder` → `AddFIMSParticle` / `FxComponentTerrain.AfterOneEvolveDelegate`) reads each
element/selector's **`BBoxMin`/`BBoxMax`** (`FxLevelBuildSelectorGPUData`, written in `WriteToGPUData` ~41792) against the
camera to decide, per hexagon, whether to place the **building element** or demote to the sibling **decal**. Our custom
reactor mesh has a very different bounding box than the donor missile silo, so the size-driven element↔decal switch lands
differently — the building fades out (affinity opacity), but the selector still doesn't hand the hexagon to the decal.

**This is the C#/GPU boundary. C# reflection is exhausted** — every reachable field is identical between the two cases.

### Falsified fix attempt: decals on a separate channel (08-09)

Tried the cheap version first — keep isolate's clean single reactor on the main channel and re-host a **clone of the native
selector on a free channel** so its decals still draw. Built (`PreserveFootprintChannel` + `LoadFxMaterial`), verified it
cloned the right source (`CityMapSelector_Industry_00`) and hosted it on channel 2. **Result: renders nothing** — close-up
shows no donor buildings from it either. Reason: the plbc has effectively **one composited level-build content channel**
(`mainLevelBuildComponantLayer`; the native reactor dump reads **"1 channel(s)"** — the extra channels seen post-swap are
not composited level-build layers). **Decals cannot live on a separate channel; the building and the decals must share the
one main selector.** Code left in place but **disabled** (call commented in `TickDistrictMeshSwap`); `LoadFxMaterial` is
reusable for the real fix. So the footprint genuinely needs the deep-clone/privatize path below — there is no side-channel
shortcut.

### The deep-clone build — got far, parked on a mid-zoom LOD wall (08-09)

Implemented the private deep-clone (`DeepCloneMat` + `PointTileAtClonedSelector`, `UseDeepClone`). It **worked** for the
hard goals and is a real result:
- **Footprint renders** at strategic zoom, scoped to only the reactor tile (1657 nodes privatized, other Industry untouched).
- **Texture correct**: all swapped reactor slots share ONE private `FxOutputLayer` (`e.deepLayer`, `ClonePrivateOutputLayer`)
  with our albedo bound via the existing `DistrictApplyTexture`/`BindAlbedo` path — coherent reactor sheet, no donor garble.
- **Count tunable**: swap large slots (bbox ≥ `DeepCloneBuildingMinSize`), hide small props (`size→0`), thin the rest
  (`DeepCloneKeepEvery`, keep 1-in-N proportionally so the visible subset thins evenly, not a broken walk-order cap).

**The wall — mid-zoom shows the donor.** The district renders different geometry per zoom band (close = detailed buildings,
mid = a lower-detail LOD set, far = footprint decals). A post-swap element dump proved it: of 483 visible elements only **75
were ours** (`meshIdx=4607`); **~408 were donor** meshes we never swapped. Those mid-LOD elements resolve through the
selector's **`pairs` variant table into SHARED materials**, not the per-tile cache/emitter-items we clone. They cannot be
privatized: repointing them is impossible (pairs are GUIDs, not instances), a per-frame catch (`EnsurePrivate`) walking the
1657-node tree **tanked performance** and still missed them (they're not in the cache/items paths), and global-swapping the
shared LOD materials would turn **every** Industry district's mid-zoom into reactors. That's an architectural wall for the
runtime-injection approach: close-zoom is per-tile (cloneable), mid-zoom LOD is shared (not).

**Status: parked** (`UseDeepClone=false`, back to the shipped clean single reactor). The deep-clone code is committed for
whoever picks this up. A clean cross-zoom result likely needs a real content-pipeline district (custom mesh with its own
baked LODs + a dedicated affinity), not runtime selector surgery.

### (earlier fix sketch, superseded by the build above) private cloned selector

Verified structure: the reactor's plbc channel **[0]** is an `FxEvolverMaterialLevelBuildSelector` that holds **both** the
building elements **and** all 231 decals (the footprint). Isolate replaces that whole channel material with our one private
leaf → decals amputated. Global keeps the selector but swaps the **shared** leaves → footprint works but every Industry
tile becomes a reactor. The fix threads the needle:

1. **Clone channel [0]'s selector** (`UnityEngine.Object.Instantiate` — private copy; NonSerialized runtime cache resets,
   rebuilds on Load). Its serialized `pairs`/`defaultMaterial` GUIDs and the decal subtree come along.
2. **Privatize the building leaves inside the clone.** Cloning the selector alone is NOT enough — its element leaves resolve
   by GUID through the shared `FxEvolverMaterial.TryLoad` cache, so mutating them is still global. For each building
   **Element** leaf the clone resolves (via `CollectLeaves`, which keys on `fxMesh` and already skips `decalMesh` decals),
   `Instantiate` a private copy (exactly `BuildPrivateLeaf` line 407: "a private copy — mutating it won't touch the shared
   leaf"), set its `fxMesh` to ours (+ the private-`FxOutputLayer` texture treatment), and repoint the clone's cache/pairs
   entry at our private leaf. **Leave the decal leaves as-is** — shared is fine, they're unmodified.
3. **Force the cloned selector onto this tile's channel [0]** (like `PointTileAtPrivateLeaf` does with the leaf), re-assert
   per frame, RefreshChannel. Reset on save-reload (`ResetDistrictSessionState`).

Result: only the reactor's tile uses the private clone → our building mesh + the surviving footprint decals; other Industry
tiles untouched. This is a real implementation (clone + per-leaf privatize + texture + load + per-frame + reload), but every
building block already exists (`BuildPrivateLeaf`, `CollectLeaves`, `ApplyLeaves`, the private-layer texture path). Design
note: the reactor tile will show our mesh at each building slot the selector picks (the "reactor-ified district" look from
the global test, but scoped to one tile) — confirm that reads well, or restrict the swap to the primary element.

### (SUPERSEDED) Old next route — the ShaderDump / BBox dig

- **~~Confirm the BBox lever~~ — DONE, ruled out** (falsified lead 13). Forcing a small element bbox did not summon the
  footprint. `lodData` (lead 12) is out too. **The C#-settable per-element levers are exhausted.**
- **Reverse-engineer the evolve/selection compute shader** with the ShaderDump toolchain (`ENCAccessProof/tools/ShaderDump`,
  see [[shaderdump-toolchain]]) — dump `AddFIMSParticle` / the level-build evolve kernels (`FxComponentLevelBuildParticleAdder`,
  `FxComponentTerrain.AfterOneEvolveDelegate`, the `ResolveElevationQueryMaterial`/`GeoDBQueryMaterial` dispatches) and read
  exactly how a hexagon's element-vs-decal winner is chosen, and which per-entry field our swap perturbs. This is the only
  route left below the C# decompile — every reachable C# field is either identical between gated/working or has been
  falsified. **This is where a fresh session must start.**
- **First, fix the probe** so the next element diff is trustworthy: extend `DumpMatTree` to recurse the selector's `pairs`
  variant table + `defaultMaterial` (mirror `CollectLeaves`), then dump the reactor's building element **native vs
  swapped** (per-district `seen`, no truncation) to see precisely which GPU-uploaded field our `Load`+swap changes on the
  *actual building* (not the shared props the current dump catches).
- The clone-the-selector fix (preserve decals) is *necessary but not sufficient* — global mode already preserves the
  decals and still gates them. The GPU-selection fix is the missing half.

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
12. **`FxEvolverMaterialLevelBuildElement.lodData`** — our swap re-resolves `lodData` from our LOD-less FxMesh
    (`lodType=0`) to **0**. But a live dump showed **working** Food/Science building elements *also* run `lodData=0`
    (only a shared decorative prop, meshIdx 547, carries `lodData=3`). So `lodData=0` is normal and renders footprints.
    Not the gate.
13. **Element `BBox` (the "cheap shot")** — the element uploads `BBoxMin/BBoxMax` to the GPU selection
    (`FxLevelBuildElementGPUData`, `WriteToGPUData` ~43350) and `bbox` is a settable field (`useCustomBBox` + `Bounds`).
    Directly tested: forced `useCustomBBox=true` + `Bounds(0, 0.15³)` on every swapped leaf in `ApplyLeaves`, global mode,
    zoomed out — **no footprint appeared.** So the element→decal selection is **not** driven by the element bbox the way
    we hoped. Ruled out. (Reverted.)

**Probe limitation discovered — and FIXED (commit `ae5cf25`):** `DumpMatTree` only recursed `levelBuildItems` +
`fxMaterialCacheEntries.Entries`, but a selector's main building lives in its **`pairs` variant table / `defaultMaterial`**
(which `CollectLeaves` handles and the dump did not). Extended the recursion to mirror `CollectLeaves` (`TryLoadMaterial`
per pair guid — cached, no explosion), switched to `GF` (silences the `AccessTools.Field` warn-spam), raised depth 6→10.

**Result — the real native composition (footprint working):** the reactor's native `Extension_Base_BreederReactor` tree is
**408 building element leaves (349 distinct meshes) + 231 decals (120 writing render data)** — vs the **1** the old probe
caught. That is the full footprint machinery, present and rich in the native district.

**SECOND methodology catch (blocks the swapped diff):** the `[Tree]` dump fires from `DistrictApplyEntries` (on
`UpdateLevelBuild`), which **only dumps + caches the tile — it does NOT swap.** The swap runs per-frame in
`TickDistrictMeshSwap`. `UpdateLevelBuild` fires before the tick swap settles, so **every dump so far captured the
PRE-swap (native) tree** — which is why a "native vs global-swapped" reactor dump came back *byte-identical* (0 mesh
diffs, 231/231 common decals). That identical result is a **timing artifact, not evidence about the swapped state.** To
capture the true post-swap tree, the dump must be invoked from `TickDistrictMeshSwap` **after** `ApplyLeaves`, or dump
`e.leaves`/`e.privateLeaf` directly. Do this first next session — then the swapped decal/element counts are finally
trustworthy.

Note this doesn't change the verdict: the decals' *render-readiness* was already proven byte-identical gated-vs-working,
and every per-element field is falsified. The swapped-tree diff would only tell us whether the swap drops or keeps the
decal drawers — the *selection* gate remains in the GPU kernel regardless.

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
