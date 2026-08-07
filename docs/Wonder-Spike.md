# Wonder Spike — custom 3D models on (artificial) wonders

*Research spike, 2026-08-07 — the Oracle arc. Status: SHIPPED — the Oracle (a custom Greek temple on a
player-authored Artificial Wonder) is stable in-game with its own textures, surface maps, and card portraits,
and was publicly announced. The native wonder visual chain is a measured dead end; two session bugs were found
and fixed en route. This page records what was proven, what was falsified, and what remains.*

## The working recipe (proven in-game on `ArtificialWonderDefinition`)

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

## Falsified: the native wonder affinity (measured, don't re-attempt blindly)

Setting `ConstructibleVisualAffinity = DistrictVisualAffinity_ArtificialWonder` — the affinity the class is
*designed* for — was the obvious-better idea and produced a clean negative:

- The tile resolves to the **shared wonder-scaffolding material family** (`-1382607685,…` — the same GUID the
  July mapping recorded for every wonder's scaffolding).
- That family is a different composite from the district selector chain: the plugin's leaf walk finds **zero
  swappable mesh leaves** (`waiting for leaves to load...` forever), and the walk logs `FxEvolverMaterialLevelBuildDecal`
  members it has no fields for — a structure the walker doesn't know.
- A *custom* wonder gets **no completed-model visual at all** through this chain (vanilla artificial wonders map
  affinity+definition to their built model in game-core data a mod can't extend) — the tile renders bare terrain.

Net: the native affinity gives a custom wonder *nothing to see and nothing to swap*. The renderable-district donor
(step 2 above) remains the path.

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

- **Participation-phase skinning** (construction site as a second registry entry) — untested.

*Related: [District-Visuals.md](District-Visuals.md) (the axis this rides), the July wonder-material mapping in
that page's History section.*
