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

## Open / next

- **Configurable footprint** — a runtime knob to graft a chosen donor template's decals onto the selector, so
  the footprint can be swapped without re-baking. Chosen, not yet built.
