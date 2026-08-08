# Wonder Spike — custom 3D models on (artificial) wonders

*Research spike, 2026-08-07/08 — the Oracle arc. Status: SHIPPED, and upgraded to the NATIVE chain — the
Oracle (a custom Greek temple on a player-authored Artificial Wonder) renders through the game's own
Artificial Wonder pipeline: native affinity, its own row in the wonder visual database, the vanilla
bottom-to-roof level-build reveal on reload, custom mesh + marble surface maps + card portraits. Publicly
announced. This page records the recipe, the decompile that cracked the native chain, and what remains.*

## The original donor recipe (proven in-game; superseded by the native chain below)

A custom wonder is just a district with better press. The district axis
([District-Visuals.md](District-Visuals.md)) carries it unchanged — the swap machinery never asks what a
constructible *is*, only that its tile resolves to a leaf it can clone:

1. **Author the wonder definition** (ENC data, e.g. `Extension_ArtificialWonder_Era1_Oracle`, class
   `ArtificialWonderDefinition`): gameplay effects, unicity, costs as desired.
2. **`ConstructibleVisualAffinity` → a RENDERABLE donor district affinity** — `DistrictVisualAffinity_MissileSilo`
   is the proven donor (the reactor's). This is the whole "link to a donor district": it decides which vanilla
   building (and crucially which *loaded, context-bearing material chain*) the tile resolves to before the swap.
3. **Clear `Additional Visual Levels`** — a non-empty list drives `DistrictState`, the lookup is keyed on the full
   combo (constructible + affinity + era + state), and an unregistered combo resolves to `material 0,0,0,0` = an
   empty tile (the trap that ate days in July).
4. **District Factory**: *type* the wonder's exact `ConstructibleDefinitionName` (the Pick dropdown lists only
   `*DistrictDefinition` classes — `ArtificialWonderDefinition` isn't one), bake the model (Size / Facing /
   Position offset / hex clip all apply), **Isolate ON**, rebuild the mod.
5. The plugin does the rest: leaf clone, mesh swap, private-output-layer **texture injection** (the temple renders
   with its own atlas).

**Construction phases:** the artificial-wonder pattern splits the build site into a companion definition
(`Extension_ArtificialWonderParticipation_<name>`). The tile presents under the *Participation* name while under
construction and under the main name when complete — so vanilla scaffolding during the build and the custom model
on completion, which is the right feel by default. (A second registry entry keyed to the Participation name could
skin the construction site too; untested.)

**Card images / portraits:** a wonder's UI images ride the standard UIMapper mechanism — the wonder's UIMapper
entry (e.g. in a `ConstructibleArtificialWonderUIMappers` asset) has an `Images` list keyed `ArtificialWonderCard`
/ `Small` / `Tooltip`. Drop portrait PNGs into `Assets/Resources/Images/` and assign them on the mapper in the
Inspector (the serialized reference is Amplitude's `{a,b,c,d}` GUID encoding — assignment via Inspector handles
it). The Oracle ships a 512×366 card and a 512² small/tooltip portrait this way.

## THE NATIVE CHAIN — solved by decompile (2026-08-08, verified in-game incl. reload)

The July/early-spike verdict — "the native `DistrictVisualAffinity_ArtificialWonder` affinity gives a custom
wonder nothing to see and nothing to swap; game-core data a mod can't extend" — was **wrong**, and the way it
was wrong is the best news of the axis. Decompiling the visual-resolution chain
(`FxEvolverMaterialLevelBuildSelector` → `AssetReferenceRepository` → `AssetReferenceDatabaseContent`, in
`Amplitude.Mercury.Terrain.dll` / `Amplitude.Mercury.Data.dll`) showed:

- A district tile's visual resolves through **criteria-matrix databases** (`*/District/Main` and friends) in
  `AssetReferenceRepository`, keyed by ConstructibleVisualAffinity, culture, era, district state, health… and —
  crucially — **`CriteriaEnum.ArtificialWonder`: completed wonders key their model by wonder definition NAME**
  in a dedicated 1D **`ArtificialWonder` database** (name → `FxEvolverMaterial`).
- Selectors with `fillMode = LevelBuildDatabase(WithKey)` have legitimately **empty inline `pairs`** — their
  variants live in the repository. That's why the leaf walker starved on wonder (and Holy Site) families: it
  only knew inline tables. Nothing was broken; the data lives elsewhere.
- The repository's rows are **plain `AssetReferenceDatabaseContent` datatable elements** — moddable data, not
  engine core.
- A `[RepoDump]` diagnostic launch delivered the punchline: **our wonder's name was already in the database's
  criteria axis** (indexed from the ENC definition) **with a NULL guid**. The July "material 0,0,0,0" and the
  scaffolding-only tile were never a dead end — just an *empty cell waiting to be filled*.

**The working native recipe (replaces the donor-district hack entirely):**

1. `ConstructibleVisualAffinity = DistrictVisualAffinity_ArtificialWonder` — the native one.
2. The plugin's `[WonderRow]` poll (`WonderNativeRows = "WonderName=a,b,c,d;..."` in config) fills the wonder's
   cell in the `ArtificialWonder` database with an `FxEvolverMaterial` GUID and force-loads the cell asset. A
   vanilla wonder's material (the Oracle used the Temple of Artemis) is a zero-bake proof AND the loaded
   template for step 3. Re-arms after session reloads.
3. The district walker sources its swap template **from that database cell** when the channel selector has no
   inline leaves — then the proven isolate machinery runs unchanged: private leaf clone, custom mesh, albedo +
   surface-map atlases on the private layer.

Verified: the temple renders on the native chain, and an in-session reload plays the game's own
**bottom-to-roof level-build reveal** on the custom mesh — native wonder theatrics for free. Donor roulette
(silo / holy site / natural reserve) is over; en route it produced the donor laws below, kept for reference.

**Reveal-on-load (SOLVED — `fadeInOutMode = Instant`, verified in-game):** every session load used to replay
the game's bottom-to-roof level-build reveal a few seconds after the loading screen lifts. Mechanism: the
reveal is the element's appearance transition — vanilla wonders play the *same ramp on load*, it just finishes
behind the loading screen; our swap lands after the screen lifts because the template load can't start until
the district machinery tracks an FxManager. The lever (found by dumping every field of the cloned leaf):
**`FxEvolverMaterialLevelBuildElement.fadeInOutMode` — `{Stepped, Smooth, Instant}`**, encoded into the
element's GPU data. The wonder-path private clone sets `Instant` before its first Load — the temple stands
complete the moment the tile renders, at any load speed. Trade-off (open refinement): `Instant` is currently
unconditional on the wonder path, so a wonder completed *mid-game* also skips the ceremony; the designed fix
is a postfix on `PresentationDistrict.UpdateLevelBuild(eventName)` — the game itself says `None` on load vs
`Build`/`Upgrade` on genuine construction — choosing `Stepped` only for real construction events.

**FALSIFIED (two clean deadlocks): loading the template earlier to hide the ramp behind the screen.** Reaching
for `RenderContextAccess.GetInstance<IFxManager>` from a plugin Update tick during the load sequence hangs the
loading screen — with the synchronous AND the async loader; the game's own coroutine may do it only because it
runs at a controlled point in the load order. **LAW: never touch the render context before `distFxManager` is
tracked.** Symptom: silent hang, log stops after early-startup lines, no exception.

**Swap-first sequencing (VERIFIED — the template is never visible):** the first build filled the cell up
front, which raced the walker's swap against the native reveal — on a cold cache the template (Artemis) showed
for a few seconds before the swap landed. Now the template material is loaded *plugin-side* into a stash
(never via the repository cell), the walker builds the private leaf from the stash, and the cell is filled
only **after** the swap is live (fallback only). The native selector never has a drawable template on the
tile: briefly empty, then the custom model, at any load speed. The session reset clears the stash (corpse
assets after reload) and re-runs the same sequence.

**Donor-family laws (measured 2026-08-08, now historical):** a donor affinity only worked if its family was a
**culture-agnostic building-model family** with inline variant pairs (Missile Silo: works). Holy Site's table
is empty for a foreign constructible (culture+era criteria live in the repository), and Natural Reserve's
leaves are *scatter* drawers — the swap lands but draws with scatter semantics (terrain-keyed pairs, 15
variants, one leaf lottery).

## Bugs found en route (both in the plugin's session lifecycle, not the wonder class)

- **The corpse FxManager** (fixed, verified): `distFxManager` was `??`-cached from the first session forever; a
  second game in the same app run handed every leaf `LoadIFN` a dead manager (`fxComponents == null` → NRE spam)
  and the tile kept the donor's silo. Fresh-first read + full district state reset in the session rearm.
- **The corpse leaf on save-reload** (fixed, verification pending): an in-session save-reload rebuilds the world
  *without* the AnimationLoad rearm; the per-frame repoint then forced the new district channel onto the previous
  session's private leaf, whose `meshIndex` means nothing in the rebuilt GPU buffers — an actively-emptied tile.
  Fix: the district session reset also fires from the `Sandbox.Load` postfix.

## Stability (RESOLVED same day — the stability pass, verified in-game)

All three instability mechanisms were measured and fixed:

- **Texture streaming** ("perfect → brown → corrupt"): the reduction system kept loading proxy/mid/hi-res
  materials into the private layer, each arrival stomping the injected albedo. The layer clone now **opts out of
  streaming** (its mid/hi-res material GUIDs nulled — the game's own loader short-circuits), so the one bound
  material is stable under any camera abuse.
- **Corrupt surface detail**: the vanilla sheet's normal/roughness/metallic/AO maps stayed bound under the
  injected albedo, painting the donor building's bricks over the custom texture. **Neutral surface maps** are
  bound alongside the albedo — the model renders as authored. (Future knob: bake real normal/roughness maps from
  the source GLB.)
- **Save-reload** (the corpse-leaf empty tile): the district session reset also fires from the `Sandbox.Load`
  postfix. Verified: the temple rebuilds cleanly after an in-session reload.

## Surface-map atlases — SHIPPED & VERIFIED IN-GAME (same day)

The Oracle temple's wall albedo is 1024² of *pure white* — the marble lives in the **normal + roughness maps**
(`Temple_BaseColor` 5 KB, `Temple_Normal` 879 KB). The bake now packs **normal + roughness atlases with the albedo
pack's exact rects** (the remapped UVs index them for free), sourced from a `Textures/` folder of per-material
files next to the model (the Sketchfab original-format layout; missing files keep a neutral fill). The district
registry carries `normalAtlasGuid`/`roughAtlasGuid`; the runtime binds them on the private layer where the neutral
stand-ins go; the editor preview binds a DXT5nm-swizzled variant. Three lessons, paid for in bakes:

1. **Downsample with an area average** — a single bilinear tap aliases dense normal maps into rainbow static.
2. **Normal-atlas thumbnails always look like chaos** — judge by *measurement* (neighbor-delta statistics on the
   pre-compression PNG dump the bake now writes), never by eye; half an evening went to convicting healthy data on
   its thumbnail.
3. **Relief strength is calibrated into the data** (65% toward flat, tuned in-preview) so the preview and the game
   — whose shader has no scale knob — show the identical result, and a re-bake can never reset it.

Stale-bundle note stands: re-bakes reshuffle atlas packing — ALWAYS rebuild the mod after the final bake.

## Open items

- **Ceremony on genuine construction** — `fadeInOutMode = Instant` is unconditional on the wonder path, so a
  wonder completed mid-game skips the build reveal too; the `UpdateLevelBuild(eventName)` postfix (None = load,
  Build/Upgrade = real construction) would restore `Stepped` for real completions only.
- **Auto-derive the cell fill from the district registry** — today `WonderNativeRows` is a hand-authored config
  line; wonder-typed registry entries should register their own cell (and pick a template) without it.
- **Fully-native route**: bake a real `FxEvolverMaterial` asset in the editor and point the cell straight at it —
  no template clone, no runtime swap, no first-frames template flash.
- **Participation-phase skinning** (construction site) — untested; note the `ArtificialWonder` database also
  indexes `...Participation...` names, so the same cell-fill likely covers it.

*Related: [District-Visuals.md](District-Visuals.md) (the axis this rides), the July wonder-material mapping in
that page's History section.*
