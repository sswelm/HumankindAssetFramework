# Textures — the complete field guide

Everything texture-related in one place: how the bake turns a model's materials into the one atlas the game
renders, every knob that shapes the result, the complete failure catalog (each entry hit for real), and the
runtime re-skin system. Written after the Universal Tank's "pale tank" saga (2026-07-27), in which a perfectly
textured Sketchfab model shipped washed-out three separate ways at once — every cause is now either fixed in
the tooling or a one-checkbox recipe below.

## The pipeline in one paragraph

At bake time, each source material's **albedo** is extracted to
`Assets/FactorySource/<Model>/<Model>_matNN_<matname>_albedo.png` (Blender-side, from the GLB/FBX's textures).
The baker then packs all of them into **one atlas** (`<Model>_Atlas`), remaps every submesh's UVs into its
material's rect, merges the submeshes, and block-compresses the result. Opaque atlases use **DXT1**; the static
multi-material path preserves meaningful source alpha and uses **DXT5**. The animated multi-material path still
forces the atlas opaque — see the BLEND caveat.
The game renders exactly this atlas; the source textures never ship. Anything wrong at any stage — extraction,
packing, matching, remapping, post-processing — shows up as "the texture looks wrong" with very different
root causes, which is why the failure catalog below leads with symptoms.

## The knobs (Model Factory, per entry)

| Knob | What it really does | Recipe guidance |
| --- | --- | --- |
| **Atlas size** | The packed atlas's max dimension. | **Budget ~texture-area per material**: 100 materials in a 512 = ~50px each = mush. A multi-material vehicle wants **2048**. Cost: 2048 DXT1 ≈ 2.7 MB with mipmaps; alpha-bearing DXT5 is roughly double. |
| **Material mode** | Single collapses every polygon to material slot 0; Auto/Multi keep slots for per-material atlas rects. | Multi-material sources need Auto/Multi. NOTE: on the **animated** path, Multi rebuilds the mesh and needs tangents (see Animated-Models). |
| **Keep black (glass/cockpit)** | OFF (default) replaces every near-black texel (<32,32,32) with pale grey-blue (160,160,168) — a rescue for models whose "glass" is solid black. | **Dark/camo models MUST turn this ON** — otherwise every shadow, dark camo spot and rubber part paints pale grey-blue ("the washed-out tank"). |
| **Albedo brightness / saturation** | Post-multipliers on the packed atlas. | Leave 1/1 unless the source is uniformly too dark/garish; they cannot fix mapping problems. |
| **Keep extracted texture (reuseExtracted)** | The Blender step does NOT regenerate the extracted albedos. | **Required whenever you hand-edit an extracted file** (e.g. the white-swatch recipe below) — otherwise the next bake overwrites your edit. |
| **Height-gradient UVs / Weld & simplify** | Static-path shading/import options. | Static models only; see Factory-Manual. |
| **Texture file** (Unit Retexture) | Runtime hot-loaded PNG replacing the baked atlas. | See "Runtime re-skins" below. |

## Failure catalog — match your symptom

**Flat-colour (untextured) model comes out with wrong colours on some faces / grey fringes at part edges** →
**FIXED 2026-09-02 (0.5.0)** — just re-bake. A SketchUp-style model whose materials are pure colours (no image:
`paint`, `glass`, `copper`…) packs an 8×8 solid swatch per material, but the mesh's source UVs are garbage on
such models (islands parked anywhere, even outside 0..1), so faces sampled *neighbouring* rects and part edges
bilinear-sampled the padding between rects. The bake now pins every vertex of a flat-swatch submesh to the
**centre of its rect** — the per-submesh log says `(flat swatch — UVs pinned to rect centre)`. No external
"flat-colour atlas" rebuild of the GLB is needed any more; bake the original. (Found on the Bell H-13, a Vehicle
Lab rig with 10 flat materials.) To *repaint* one of those parts, edit its swatch file
(`<Model>_matNN_<name>_albedo.png`) to any size ≥ 16 px — a larger file returns that part to normal UV mapping.

**Washed-out / pale grey-blue wash over dark areas** → the **Keep black substitution** (see knob above). The
substitute color is literally (160,160,168); if your "damage" is that exact pale blue-grey, this is it.

**One part samples another part's texture (silver wheels, collaged panels)** → the atlas **rect-matcher
prefix-collision bug — FIXED 2026-07-27** (`UniversalBaker`). History for archaeology: extraction keys carry a
`matNN_` prefix that the name simplifier mangled into a leading index, so the EXACT name match could never fire
for any material; everything fell to a Contains fallback where prefix families collide first-hit
('tank_tracks' sampled 'tank_tracks_13''s rect). It was latent in **every multi-material bake ever made** —
most models survived because their material names don't prefix-collide. A rebake inherits the fix.

**A whole part renders flat white** → the source material has **no texture and no color** (glTF's default
base color is white; Sketchfab's auto-generated 'Merged_materials' is the classic case). The extractor honestly
writes a tiny white swatch. Recipe: overwrite the swatch file
(`<Model>_matNN_<name>_albedo.tga/png`) with a flat fill of a plausible color — the average of a sibling
texture works well (flat color = UV-proof) — then bake with **Keep extracted texture ✓**.

**Blurry/mushy everywhere but correctly mapped** → atlas too small for the material count. Raise Atlas size.

**Texture vanishes or renders flat although Blender shows it fine** → UVs parked in a non-[0,1] integer tile
relying on texture wrap. The static path's glbconv integer-shifts them back; the animated path folds each UV
into [0,1) at remap time. Diagnose via the OBJ's `vt` range or a GLB accessor scan — if UVs span e.g.
[3.0..4.0], this was it (pre-fix bakes).

**Subtle washed "dirt" layer differs from the source render** → the source material is **alphaMode=BLEND**
(Sketchfab layering: dirt/decals in alpha, blended over the layer beneath). DXT5 can preserve an alpha channel on
the static multi-material path, but HAF does not composite layered source materials into one final albedo; the
animated multi-material path also forces alpha opaque. Semi-transparent dirt/decal RGB can therefore differ from
the source render. Usually acceptable; no compositing fix has shipped yet.

**Preview looks wrong but you suspect the bake is fine** → previews flatten materials and can hold **persisted
wrong texture bindings**: Unity resolves FBX auto-material textures BY NAME SEARCH in the folder subtree and
writes the remap into the import settings. Two rules: (1) **never place foreign FBXs inside a model's
FactorySource folder** (the raw scrubber caches live in the shared `FactorySource/raw/` tree for exactly this
reason); (2) if a preview got poisoned, moving the intruder out is NOT enough — **rebake the entry** to
regenerate the import. The game renders baked assets, not previews: when in doubt, the in-game look is truth.

**Vehicle Lab looked acceptable, but the animated Factory bake is flat, black, or scrambled** → separate the two
stages. Vehicle Lab's **Checker** is an intentional material override and its normal view is a rigging aid, not the
packed HAF atlas. In Model Factory, use **Auto/Multi** (Single deliberately assigns every polygon to slot 0) and make a
control bake with **Reduce to ~tris = 0**. If the control is correct, reduction—not the source texture—crossed a
topology/material threshold; raise the ceiling gradually and keep the lowest verified value. This is not theoretical:
one animated rotorcraft failed at 20,000 triangles and mapped correctly at 24,000. **Keep black** only controls the
near-black→grey substitution; it does not repair material assignment or UV remapping.

**Portraits/UI images** are a different system entirely — Amplitude references them by nibble-swapped
{a,b,c,d} GUIDs, which makes their path irrelevant. Since 2026-07-28 they ALL live in
`Assets/Resources/Images/` (portraits of every size, tech/constructible icons, event JPGs — in-game
verified), keeping the Resources root a pure bake-output namespace. If a card ever renders magenta, the
sprite reference broke: recover the file WITH its original .meta (same GUID) — a fresh import gets a new
GUID and stays `<Missing>`.

## Runtime re-skins (no bake) — the Unit Retexture window

Reskin an existing unit at runtime: hot-load a PNG over the baked atlas, or grey/desaturate/tint —
isolated per pawn descriptor, FREE on the vertex budget (shares the mesh). Works on custom model entries too
(the PNG replaces the baked atlas; adjust-only is a no-op there). Hot-loaded PNGs **need a mip chain** or they
sparkle at distance. The window can also dump a unit's atlas to disk — the fastest way to see exactly what the
game is sampling, and the starting point for hand-painted variants.

## Debug workflow (what actually finds the cause)

1. **Dump or open the atlas** (Unit Retexture ▸ dump, or `Assets/Resources/<Model>_Atlas`) — is the damage in
   the atlas itself (extraction/post-processing) or only on the model (mapping)?
2. **Read the bake's pairing log** (`Editor.log`, lines `submesh N 'mat' -> rect[N] 'matNN_name'`) — mismatched
   names = mapping; matched names + wrong look = the texel data.
3. **Open the extracted per-material albedos** in `FactorySource/<Model>/` — a 274-byte file is a swatch
   (untextured source material); real textures are 100KB+.
4. **Check the source's material flags** (glTF JSON: `baseColorTexture` present? `alphaMode`?) before blaming
   the pipeline — some models are genuinely untextured or alpha-layered at the source.
5. Only then reach for brightness/saturation — they are taste knobs, not repair knobs.

For Vehicle Lab outputs, insert one extra control before step 1: bake once with **Auto/Multi** and reduction **off**.
That distinguishes source/material extraction from damage introduced while simplifying the animated mesh.

Cross-references: [Factory-Manual.md](Factory-Manual.md) (the windows and bake flow),
[Animated-Models.md](Animated-Models.md) (multi-material on the animated path, tangents),
[Animation-Pitfalls.md](Animation-Pitfalls.md) (the engine contract; previews lie),
[Vertex-Budget.md](Vertex-Budget.md) (why texture size is nearly free but meshes are not).
