# Dedicated District Visual — custom model **with a strategic footprint**

Give a single district its own building model **and** the persistent top-down *footprint* that vanilla
districts show at strategic zoom — scoped to that one district, with the shared visual affinity (and the
non-plugin fallback) left intact. Verified in-game 2026-08-12 on ENC's Breeder Reactor (a
`Extension_Base_BreederReactor` that otherwise borrows `DistrictVisualAffinity_Base_Industry`).

This is the successor to the old isolate mesh-swap ([District-Visuals.md](District-Visuals.md)), which could
render a custom building but **deleted the footprint** (it replaced the whole selector with a single leaf).
The full investigation that led here is in
[District-Footprint-Investigation.md](District-Footprint-Investigation.md) and
[District-Dedicated-Visual-Feasibility.md](District-Dedicated-Visual-Feasibility.md).

## The idea

A district's on-tile visual is a `FxEvolverMaterialLevelBuildSelector`/`…Emitter` whose `levelBuildItems`
hold the **building Elements** *and* the **Decal items** — and the decals are the footprint. So instead of
throwing that tree away, we author a **reduced-to-one** copy of a vanilla template: keep every decal, keep the
native LOD structure, but repoint the one main building slot at our own mesh. Then at runtime we drop that
selector onto our district's tile only.

One asset can't carry everything, though: the building Element's `outputLayer` is an **un-authorable game
bundle asset** (an `FxOutputLayer`), so we bake it `null` and **bind a borrowed one at runtime**. That single
runtime step is the whole reason this is a *hybrid* and not pure data.

## Bake (ENCReload editor — `Tools/HAF/District/…`)

1. **`1b. Bake Reactor District Element`** — clones the template's largest building Element and swaps its
   `fxMesh` to our baked `<name>_FxMesh`. Saves `Assets/Resources/<name>_Element.asset`.
2. **`1c. Bake Reactor District Selector`** — clones the template **emitter**, repoints the largest building
   slot at our Element, **nulls the other building (prop) slots**, and **keeps every Decal/Emitter item** (the
   footprint + smoke). Saves `Assets/Resources/<name>_Selector.asset` → its GUID is what the config points at.

Both use `ScriptableObject.CreateInstance(type)` + field-copy (a valid `m_Script`, unlike `Instantiate`),
with `outputLayer` and `companion` nulled so the mod bundle build accepts them.

The template is any single-building district family with an inline footprint — ENC uses **NuclearTest**
(`LvlBuild_Brick_Main`). The **footprint you get is that template's decals** (e.g. brick national-project
plant outlines), *not* something derived from your mesh.

## Config (`community.humankind.haf.cfg`, `[District]`)

```ini
DistrictRepoint = false          # the old forced mesh-swap path stays OFF
DistrictMainRows =               # the shared-cell path stays OFF (see "Two paths" below)
DistrictSelectorTile = Extension_Base_BreederReactor=1734045847,1174137851,-1040006991,1418115374
DistrictFootprint =              # optional: graft a donor's decals (blank = keep the baked footprint)
DistrictFootprintDrop = Gravel,CityBricks,Battlement,Destroyed,Dammaged,Damaged   # surface-texture filter; blank = keep the rock layers
```

`DistrictSelectorTile` = `ConstructibleDefinitionName=a,b,c,d` (semicolon-separated for several districts).
The building's texture comes from the **registry** entry's `atlasGuid` (`haf_districts.json`), so the district
must have a normal District Factory entry too (that's where the mesh + atlas are baked).

## What the plugin does (`PollDistrictSelectorTile`, every frame)

For each live district whose `ConstructibleDefinitionName` matches:

1. **Load** our selector by GUID and fully `Load` it (cached per district).
2. **Prepare once** (`CenterScopedBuilding`):
   - **Center** — our reactor inherited the template's off-center main-slot `LevelBuildItem.Position`; zero it
     so it sits at tile origin like the preview.
   - **Instant appear** — `SetInstantAppear` walks the whole tree and sets every node's
     `fadeInOutMode = Instant`, so the footprint shows the instant the tile draws instead of ramping in over
     ~1 s. One re-emit applies both.
3. **Place** our selector on `channels[mainLayer].evolverMaterial`, `RefreshChannel`, and re-assert each frame
   (a cheap `ReferenceEquals` guard — the game resets the channel on its own `UpdateLevelBuild`).
4. **Bind the output layer** (`BindReactorBuilding`) — our Element is the **only leaf with a null
   `outputLayer`** (every vanilla leaf has one, so no GUID needed); borrow a real building layer from a vanilla
   leaf, clone it private, bind it on, and re-`Load` so `meshIndex`/`outputLayerIndex` resolve → it renders.
5. **Texture** (`ApplyScopedAlbedo`) — bind the district's own baked albedo atlas onto that layer clone via the
   full-texture path (`textureIndex = 1` + `AddNullAtlasInfo`), re-asserted every 15 ticks (resolution switches
   rebuild the layer's materials). Keeps the donor's normal/rough maps when the district ships none.

All of it re-arms on save/reload via `ResetDistrictSessionState`, which drops every session-scoped binding and
**frees the session's runtime clones** — the private leaves, cloned selectors/output-layers, deep-clone material
nodes and the B&W gray albedo are all `Object.Instantiate`/`new Texture2D` copies that Unity's unused-asset
sweep never collects, so they're tracked as they're created (never a `LoadAsset`'d bundle atlas) and `Destroy`ed
on the main-thread `Update` after a reset (the reset itself can run off-thread via `Sandbox.Load`). Without this,
each in-session reload leaked one FxOutputLayer + N cloned materials + a gray texture per scoped district.

## Preview fidelity (District Factory)

The model orientation is already correct; only the **preview's reference frame** needed aligning to the game:
the hexgrid is drawn **+30° CW** and the compass / North indicator **+150° CW** (a hexagon is 60°-symmetric, so
+30 ≡ +150 — the frame stays consistent). Preview-only; it doesn't touch the bake or runtime.

## Two paths (why scoped)

- **Scoped (shipping) — `DistrictSelectorTile`.** Overrides only the named district's tile. Keeps the shared
  affinity, so a player **without** the plugin still sees the sensible vanilla fallback (Industry), and no other
  district is touched. Cost: a cheap guarded per-frame re-assert on that one tile.
- **Shared-cell (alternative) — `DistrictMainRows`.** Fills the `*/District/Main` criteria cell for a whole
  affinity and forces a re-resolve, so the game renders our selector *natively*. Clean, but it hits **every**
  district of that affinity — only useful with a **dedicated** affinity nothing else uses (runtime axis-add is
  dead; a custom affinity must be defined at data-load and the district pointed at it).

## Known limitation — one-time footprint reveal (~1s on first zoom-out)

The strategic footprint builds ~1s the **first** time you zoom out to it in a session, then shows instantly for
the rest of the session. This is an **engine limitation, not a fixable bug** — verified by exhausting every lever:

- The footprint decals' render-data is a **per-hex GPU blob** (`FxComponentBlobDecalRenderDataBuffer`) that
  evolves in its **own strategic/world-map render context** (`base.RenderContext` →
  `RenderContextAccess.GetInstance<IWorldMapProvider>`). That component only processes a hex **when the strategic
  view is live**, so nothing at close zoom can pre-build it.
- Tried and confirmed no-ops (each *fired*, none moved the reveal): `fadeInOutMode=Instant` on the whole tree;
  `RefreshChannel(layer, Build)` (the construction content-write); the game's own
  `SetChannel(guid, forceRefresh:true)` (full native resolution); and bumping the decal descriptor's revision via
  `OnEditionChange` (reached through `distFxManager.GetFxComponent`).
- Vanilla districts look instant only because their decals were prepared through the creation pipeline and/or
  already viewed earlier in the session; our runtime override presents a *different* decal set the strategic
  context must build fresh the first time it sees the hex.

Forcing it would require running the strategic decal evolve out of its render context (or poking the per-hex GPU
revision buffer directly) — high risk, low odds, not proportionate to a one-time cosmetic reveal. **Banked.**

## Footprint graft + the "rocks layer" trap (`GraftFootprint`)  ← read before touching donor footprints

The footprint is **configurable at runtime**: `GraftFootprint` swaps our selector's decal items for a chosen
**donor** district's decals (registry `footprintDonor`, or the `DistrictFootprint` config), keeping our building
Element item(s) and re-emitting. `CollectDecalItems` walks the **full** donor tree (levelBuildItems +
`fxMaterialCacheEntries` + `pairs` + default/invalid), deduped by name+position — a `CityMapSelector` repeats the
same decals per culture (MissileSilo collected 207 → ~81 distinct).

**THE TRAP (cost us a full day, 2026-08-15):** the reactor's long-blamed *"rocky texture in the centre"* and a
*ground **twitch*** were **neither terrain, model base, nor ground material** — they were **decals the graft
brought in**. The MissileSilo donor carries `Decal_CityBricks_Industry_Gravel_*` (gravel) **and**
`Decal_Destroyed_Battlement_*` / `Decal_Dammaged_Battlement_*` (rubble, on `PointOfInterest_Curiosities_OutputLayer`).
The rubble/POI layer **renders at close 3D zoom** (unlike the SchematicView/CityMap decals) **and toggles on/off at
the strategic↔3D zoom boundary** = the flicker. Fix: `GraftFootprint` **drops** donor decals by name via the
**`DistrictFootprintDrop`** config (comma-separated, case-insensitive substrings) — default
`Gravel,CityBricks,Battlement,Destroyed,Dammaged,Damaged`, so only `Decal_SchematicView_*` survives (clean strategic
footprint, zero close-zoom rock, zero twitch). Set it **blank to keep the full rock texture**, or list your own
substrings to tune which layers show. This is the "surface texture" knob.

**Rule:** when a district shows unexpected close-zoom ground artifacts or flicker, **suspect the grafted decals
first.** Dump them with the `[DecalBind]` probe (name · `OutputLayerIndex` · layer name · `maskedByTerrain`) before
theorising about terrain.

## Unique footprint (`DistrictFootprintMask`) — a private SOLID BLOCK, sized + rotated

Give the district its **own** strategic footprint instead of a generic donor outline. **Shipping form: a solid
tinted block** (the decal's full quad), sized and rotated to sit over the model's square base — verified in-game on
the Breeder Reactor.

**Bonus: it appears INSTANTLY.** Because the block is the decal's own quad (no per-hex strategic decal render-data to
evolve), it sidesteps the **~1s first-zoom-out reveal** that the graft footprint has (the engine limit banked under
"Known limitation" above). The block draws the moment the tile does.

**Why a block and not a silhouette:** we built the full silhouette pipeline (below) and it works mechanically, but
the **SchematicView shader renders any injected mask as faint, sketchy hand-drawn strokes** — a crisp reactor outline
(or even two bold domes) dissolves into an unreadable smudge at strategic zoom. That's a property of the game's
schematic-map rendering, not a bug we can tune out. The **solid block reads cleanly**, so that's what ships.

**Config** (`[District]`):
- `DistrictFootprintMask` = path to a PNG. Its **content is ignored** for the block (see below) — a non-empty path
  just **enables** the injection. Blank = off (generic graft footprint).
- `DistrictFootprintMaskSize` = size, drives the item's **`LocalScale`** (NOT `defaultSize`); **~3 ≈ one tile**.
- `DistrictFootprintMaskRotation` = degrees **clockwise** about vertical (rotates the item's `AxeZ`); negative = CCW.

**Runtime** (`InjectReactorFootprint`, once): **clone the SchematicView decal** into a private copy; repoint one
footprint item at it, `LocalScale` = size, `AxeZ` rotated by the config angle, centre it, **null
`loadedEvolverMaterialGuid`**, drop the other footprint decals. The decal's `maskTexture` GUID is left **invalid**,
so `FillLayerData` sees `maskTexture.IsNull`, **skips the mask, and draws the full solid quad** = the block.

**THE TWO TRAPS (cost hours — still apply to the private clone):**
1. **Mutating the SHARED SchematicView decal leaks to EVERY district's footprint.** Must clone it (private copy).
2. **`Instantiate` does not copy the base `[NonSerialized] evolverDescriptorInstance`** → `FxEvolverMaterial.ResolveDependencies`
   **NREs** and the clone writes no render data (renders as a single **pixel**). Fix: copy `evolverDescriptorInstance`
   from the original (shared descriptor singleton) **before** `ResolveDependencies` + `Load`. And size is the item's
   **`LocalScale`** (the host "Tiny" brick's `0.04` was the shrink), not the decal's `defaultSize`/`bboxOverride`.

**The silhouette pipeline (parked, reusable).** `baker/reactor_silhouette.py` (headless Blender) renders the GLB
top-down, **strips the base plane** (`mn.z + frac·height`), **fills enclosed holes** (numpy flood) into solid shapes,
and writes the mask. A **valid hex** `maskGuidStr` + binding our texture as the decal's **mask atlas** (`atlases[0]`,
`elementData[0].Uvs=(0,0,1,1)`) makes the decal actually cut to that shape — proven — but the SchematicView shader
renders the result faint (above), so it's not shipped. The experiments are stashed on `spike/district-unique-footprint`;
the crisp version would need a **different, solid-rendering output layer** (the CityMap/gravel one — which reintroduces
the close-zoom twitch we removed, see [District-Footprint-Investigation.md]).

## MESH footprint (`DistrictFootprintMesh`) — the district's REAL geometry at strategic zoom ← the winner

The decal routes above are flat *textures*, and shapes rendered through SchematicView come out sketchy. The clean
answer is to skip the decal entirely and **keep the district's own 3D building mesh visible at strategic zoom**, so
the footprint literally *is* the model. Verified in-game on the Breeder Reactor (2026-08-15).

**The fade is a per-element GPU gate, not a camera switch.** There is no separate "strategic camera" — building meshes
and SchematicView decals both render in the `Default` render context. Each `FxEvolverMaterialLevelBuildElement` carries
a `RenderFeatureSelector` whose `SelectionFlags0` bitmask says which camera **zoom-bands** ("render features") draw it.
`PresentationCameraController.OnCameraLayerChanged` enables a band's `TerrainRenderFeatureFlags` via
`RenderFeatureProvider.SetRenderFeatureState`, which animates a shader buffer 0↔1 (the smooth fade). The reactor's
element was gated to `SelectionFlags0 = 1` (`RealisticTerrain` = close band only) → it vanished at strategic zoom.
**`SelectionFlags0 == 0` = `AlwaysEnabled` → the same geometry renders in EVERY band, strategic included.** No re-bake,
no LOD hack. (`RenderFeatures` bits: `RealisticTerrain=1`, `TopographicTerrain=2`, `DiplomaticTerrain=4`, …)

**`KeepDistrictMeshAtStrategicZoom` (once per district):** walk the selector for every mesh-bearing element
(`CollectMeshElements`), read-modify-write its boxed `renderFeatureSelector.SelectionFlags0 = 0`, `OnEditionChange()`,
then `LoadFxMaterial(sel)`. Scoped + safe — the reactor's element is its own asset, and AlwaysEnabled only *adds* the
strategic band (close zoom unchanged). Bonus: like the block, it appears **instantly** (no ~1s reveal — no per-hex
decal render-data). Also drops the template's baked footprint **decal** items (`DistrictFootprintMeshHideDecal`, default
on) so the inherited donor outline (e.g. the MissileSilo silhouette) doesn't show beneath the mesh.

**B&W when zoomed out (`DistrictFootprintMeshBW`).** The reactor's skin is a runtime-bound `Texture2D` (`scopedAlbedo`
on `scopedDonorClone`, re-asserted every ~15 ticks by `BindScopedSheet`), so `DesiredScopedAlbedo()` just picks
colour-vs-grey each re-assert — no second element. Grey copy = `MakeGrayCopy` (Blit→RenderTexture→ReadPixels handles
the non-readable atlas, then `AdjustSkin(t,1,1,0,0,0)` = luminance desaturate). **Zoom signal (the crux):** ask
`RenderFeatureProvider.ComputeRenderState(RenderFeatureSelector)` for the current 0..1 visibility of the **Topographic**
band (`SelectionFlags0 = 2`, the schematic look) — grey when `≥ 0.5`. **Do NOT key the Realistic/close band (`=1`): it
stays on well past the schematic crossover, so the reactor kept its colour zoomed out.** Get the single provider via
`Resources.FindObjectsOfTypeAll`.

**Flat when zoomed out (`DistrictFootprintMeshFlat`).** A 3D model on the flat map still reads as a model; squash it.
`UpdateMeshFlatness()` (same Topographic signal) scales each element's **`size.y`** by a tunable **flatten height** on the
schematic map, restores full height up close, re-emitting via `materialDataHasChanged` + `RefreshChannel` on the crossover
only. (`size` is the element scale the scoped setup already uses `size = 0` to *hide* props with.) **The vertical is
terrain-owned** — the item's `Position.y` does NOT lift a level-build mesh (the selector GPU write never reads it; height
comes from the terrain adaptation), so a lift lever is a dead end. Instead, `size.y ≈ 0.02` is paper-flat but coplanar with
the ground and its edges drown where the tile's terrain rises over them; **`DistrictFootprintMeshFlatHeight`** (default
**0.17**) is the sweet spot that reads flat yet pokes clear of the terrain — **live-tunable in the F8 window** (slider +
±0.01/±0.05 buttons; `FlatHeightValue`/`SetFlatHeight`). Result: full 3D colour building up close, flat grayscale footprint
zoomed out.

**Authoring — per district in the District Factory (preferred).** These settings are registry fields on the district
entry (`footprintMesh` / `footprintMeshBW` / `footprintMeshFlat` / `footprintMeshFlatHeight` / `footprintMeshHideDecal`),
edited under **Mesh footprint** in the District Factory window and saved to `haf_districts.json` (Save settings, no re-bake).
When an entry sets `footprintMesh = true` its values are **authoritative**; otherwise the global config below stays in charge
(so a district keeps working before it's authored per-entry). The plugin resolves this in `ResolveScopedFootprint`.
**Foot-gun guarded:** because an authoritative entry *replaces* the global config, an entry with the master toggle on but the
sub-options off would regress the district to 3D colour — so ticking **Mesh footprint** in the window pre-fills B&W + Flatten +
Hide-decal (height 0.17). The decal-donor field is greyed out (`(superseded by Mesh footprint)`) while the mesh footprint is on,
since the two are mutually exclusive.

**Global config fallback** (`[District]`, all need `DistrictFootprintMesh = true`): `DistrictFootprintMesh`,
`DistrictFootprintMeshBW`, `DistrictFootprintMeshFlat`, `DistrictFootprintMeshFlatHeight` (default 0.17),
`DistrictFootprintMeshHideDecal` (default true). Leave `DistrictFootprintMask` blank (the decal route and the mesh route
are mutually exclusive).

## Ground under the district (`DistrictApplyGroundMaterial` + the `ApplyGroundMaterialDefinition` **prefix**)

The per-entry **ground paint** (`groundMaterial` in `haf_districts.json`, e.g. `Constructible_Temperate_03`) is
the game's own terrain material, resolved from the criteria-24 `GroundMaterialDefinition` vocabulary and forced
onto the district's tile. Two mechanisms, because a postfix alone doesn't hold:

- **Postfix** on `PresentationDistrict.UpdateGroundMaterial` → `DistrictApplyGroundMaterial` **resolves + applies
  once** (`groundApplied`). Re-applying every frame restarts the terrain blend (a second twitch), so it's one-shot.
- **Prefix** on `PresentationDistrict.ApplyGroundMaterialDefinition` (`GroundApplyOverride`) rewrites the index to
  ours on the first call, then **returns `false` to SKIP** the game's redundant per-frame calls. Needed because a
  **DEPOSIT** district (the reactor *"Creates a Deposit of Uranium"*) re-resolves its ground to natural terrain
  **directly**, reverting any postfix-only override. The prefix makes it hold with no blend restart.

Caveats learned the hard way: the ground paint only covers the district **perimeter**, never the built centre
(that's the model/decals); `GroundMaterialDefinition` reps in `haf_ground_colors.json` are **not** the rendered look
(Prairie_Mediterranean reads tan in data, renders green grass); the value is the user's **Factory** choice — propose,
don't hand-edit their registry.

## Migrating a district onto the scoped path (`BakeScopedSelector`) + multi-district coexistence

Every district can render via the **scoped path** (its own data-authored `CityMapSelector` on the tile — the route that
carries the mesh footprint) instead of the legacy per-frame isolate/repoint swap. The **District Factory's Bake** button
bakes this automatically: `BakeScopedSelector(resourceName)` clones a single-building footprint template, swaps in the
district's baked `FxMesh` (`<name>_Element`), reduces the template's building slots to that one while **keeping its
decals** (`CityMapSelector_<name>`), and stores the selector GUID on the registry entry (`selectorGuid`). The plugin
routes any entry with a `selectorGuid` through the scoped path (`IsScopedDistrict` + `PollDistrictSelectorTile` merge the
registry GUIDs with the `DistrictSelectorTile` config), so the legacy isolate path skips it. **Trap:** the footprint
template's building elements load **lazily** — a cold bake makes `FindLargestElement` return "no building Element found",
so `BakeScopedSelector` does a warm pass first (equivalent to running `Tools/HAF/District/Probe`). Only **single-building**
templates reduce cleanly (NuclearTest, MissileSilo).

**Two scoped districts coexist independently** — verified with the Breeder Reactor + a Greek-temple Oracle in one game,
each wearing its own texture and footprint. This needed a real fix. The scoped path first held texture/B&W/flatten in
**global statics** (built for one district); subtler still, its three driving calls (`BindReactorBuilding` /
`ApplyScopedAlbedo` / `UpdateMeshFlatness`) ran **after** the per-district loop, so they executed with the state of the
*last* district only, and `BindReactorBuilding` bound *every* district's element to *one* shared layer clone. The fix has
two halves: (1) the scoped state lives in a per-district `ScopedState` (a name-keyed dict; the `scopedX`/`fpX`/`mesh*`
names are **proxy properties** onto the current `S`, so every function body stayed byte-identical); (2) the three calls
moved **inside** the loop (per-district `S` set first), and `BindReactorBuilding(onlyName)` binds only that district's
element to its **own** layer clone, gated by `S.donorClone`. **Lesson: a "per-district state" refactor is incomplete
until the *call sites* are per-district too — check where the drivers are invoked, not just where the state lives.**

## Composed districts & alpha-cutout foliage

A district model can be a composed **"pizza"** — a base building plus extra parts (a grove of trees), each baked with its
own knobs and merged into one mesh + one super-atlas (the runtime still ships a single `FxMesh`/atlas). Two traps on the
scoped path:

- **Grove renders partially** — a district mesh draws as sub-particles hard-clamped at **255** (an 8-bit field); a
  tree-heavy pizza exceeds it and only *temple + 1 tree* draw. `DistrictMeshDensityBoost` multiplies the cloned layer's
  `PrimitivePerParticleCount` to raise the ceiling (`255 × PPC × boost`) with the same GPU work. The scoped path borrows
  the low-PPC `LevelBuild_Brick` layer, so the default **8** wasn't enough — **32** drew all four trees.
- **Foliage renders SOLID** — the borrowed building material is the opaque base `Amplitude/Standard PBR Particle
  Implementation` shader (`_Mode=0`, no `_ALPHATEST_ON`, queue 2000), so leaf-cards show as solid triangles. It exposes
  the Standard cutout API, so `BindScopedSheet` flips it to **Cutout** (`_Mode=1` + `_ALPHATEST_ON` + `_Cutoff=0.5`,
  queue 2450) exactly like the bake's preview material — guarded to alpha atlases so opaque districts stay untouched.
- **Beech-tree leaf tuning** — a source authored for a low alpha cutoff erodes to slivers against the game's fixed ~0.5
  threshold; **Leaf fullness** (`alphaBoost` ≈ 2.5) restores the leaves and **Leaf size ×** (`leafScale` ≈ 1.5) keeps
  them from going spiky. Composed parts ground to the base floor, so a tree whose pivot sits above its trunk base
  **hovers** — sink it with a small negative Position-offset Y per part + copy.

## Dead ends (do **not** re-walk — all falsified 2026-08-15)

- **No "exploitation → rock" terrain-matcher rule.** Scanning all 690 loaded `FxEvolverMaterialLevelBuildMatching`
  found only 3 `POIMatchingExploitationCondition_*`, all `Exploitation = ShouldNotBe` (they *suppress* POI
  decoration on exploitation tiles; nothing draws rock). The matching criteria have **no per-constructible key** —
  only Biome/TerrainType/POI + District/Exploitation/River/Road choices + `GroundMaterialDefinition`/`HexSculpt`.
- **A solid model base-slab fails** — the ground conforms to terrain undulation and **steps into cliffs**; a rigid
  flat plate floats/shears.
- **The native conforming paving isn't graftable** — it's modular `LvlBuild_Brick_City_*` meshes tiled by the block
  system, interleaved with the buildings; no liftable ground plane.
- **The gravel footprint decals are strategic-only** (`SchematicView_Albedo_01OutputLayer` 776,
  `Decal_CityMap_Library_CityBricks_OutputLayer` 785) — they never draw in the close 3D pass.

## Open / next

- Footprint graft is **built and shipping** (registry `footprintDonor` + `DistrictFootprint` config; the District
  Factory has a "Strategic footprint (decals)" dropdown). The rubble/gravel filter above is part of it.
- **Hybrid ground (deferred)** — to pave the built *centre* at close zoom (currently the model base covers it, with
  natural terrain in any gaps) we'd restore the old isolate technique's native selector under the scoped footprint.
  Not needed after the rocks-layer fix; parked.
