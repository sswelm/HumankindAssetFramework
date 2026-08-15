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

All of it re-arms on save/reload via `ResetDistrictSessionState`.

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

## Unique footprint from a model silhouette (`DistrictFootprintMask`) — spike, private + loading

Instead of a generic donor outline, render the district's **own** top-down shape as its strategic footprint.

**Author the mask** (`baker/reactor_silhouette.py`, headless Blender): renders the model GLB top-down (orthographic),
**strips the flat base plane** (faces below `mn.z + 0.06·height`) so the silhouette is the buildings (domes/halls),
not a rectangle, and writes a **white-on-transparent PNG** (alpha = shape). Deploy the PNG anywhere the plugin can
read it.

**Config** (`[District]`):
- `DistrictFootprintMask` = path to the PNG (blank = off, keep the generic graft footprint).
- `DistrictFootprintMaskSize` = the size (drives the **item `LocalScale`**, NOT `defaultSize`); **~3 ≈ one tile**.

**Runtime** (`InjectReactorFootprint`, once): load the PNG → `Texture2D`; build a **private 1-entry `FxTextureAtlas`**
(`atlasEntries` GUID→0, `elementData[0].Uvs = (0,0,1,1)` full-texture, `outputEntries[0].unityTextureRef` = our mask);
**clone the SchematicView output layer** and set its **mask atlas** (`atlases[0]`) to ours; **clone the decal**
(`Instantiate`) so the mask/size are private; **repoint one footprint item** at the clone, set `LocalScale` = size,
centre it, **null `loadedEvolverMaterialGuid`** (else the emit reloads the original over the clone), and drop the
other footprint decals.

**THE TWO TRAPS (cost hours):**
1. **Modifying the SHARED decal leaks to EVERY district's footprint.** Must clone the decal (private copy).
2. **`Instantiate` does not copy the base `[NonSerialized] evolverDescriptorInstance`** → `FxEvolverMaterial.Resolve­Dependencies`
   **NREs** and the clone writes no render data (renders as a **pixel**). Fix: copy `evolverDescriptorInstance` from
   the original (it's the shared descriptor singleton) **before** `ResolveDependencies` + `Load`. Also: size is the
   item's **`LocalScale`** (the host "Tiny" brick's `0.04` was the shrink), not the decal's `defaultSize`/`bboxOverride`.

**KNOWN / open:** it currently renders as a **solid square** (the decal quad), **not yet cut to the silhouette** — the
mask **alpha isn't shaping it**. Next: try `maskOption = DistanceField` vs `Alpha`, or feed the shape as **luminance**
(the decal may sample the mask's colour, not alpha). Everything else (private, tile-sized, loads correctly) is verified
in-game and committed on `spike/district-unique-footprint`.

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
