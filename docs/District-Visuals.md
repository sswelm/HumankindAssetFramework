# District Visuals — replacing a district's on-map model

**Status: SOLVED (2026-07-16; textured 2026-08-06) — a custom 3D model renders on a *single* district tile in-game,
with its own baked texture.** Render, fit, scope, grounding and color are all working: the plant mesh sits level on the
reactor tile wearing its own albedo, and the rest of the city is untouched. This was long thought impossible (see the
history at the end); it is not. This page documents the working recipe, the mechanism, and the constraints.

The second injection axis, alongside units. Goal: let a pack replace a **district's** on-map building with a custom
static 3D model. It is far deeper than the unit path, but it works.

---

## The winning recipe (end-to-end)

Three parts — a data edit, a runtime mesh-swap, and a lean bone-free bake:

**1. Data (in the ENCReload Unity project):** on the target district definition, set
`ConstructibleVisualAffinity` to a *renderable* affinity (e.g. `DistrictVisualAffinity_MissileSilo`) **and clear its
`Additional Visual Levels`.** The visual-levels list drives the district's `DistrictState`, which is a *criterion* in
the building lookup — a mismatched state resolves to no building (`material 0,0,0,0` → empty tile). Clearing it falls
back to the default state, which resolves. (This — not the district *class* — is why foreign affinities kept coming up
empty.)

**2. Runtime (the plugin, driven by the `enc_districts.json` registry — below):** the rendered mesh lives deep inside
the resolved material:

```
channel-0 material = FxEvolverMaterialLevelBuildSelector   (picks by CULTURE)
  → pairs[culture] = GUID  → FxEvolverMaterialLevelBuildEmitter   (emits a SET of parts; may nest)
     → levelBuildItems[].loadedEvolverMaterial → FxEvolverMaterialLevelBuildElement   ← THE LEAF
          .fxMesh   (Guid)   ← the mesh handle (field is `fxMesh`, not `mesh`)
          .meshIndex (uint)  ← the RESOLVED GPU slot (sentinel uint.MaxValue), set in the leaf's Load()
```

The plugin (`Hk_DistrictRepoint` → `TickDistrictMeshSwap`) walks this: loads each `pairs` sub-material with
`FxEvolverMaterial.TryLoad(guid, synchrone:true)`, recurses `levelBuildItems[].loadedEvolverMaterial` to the leaf
Elements, sets each leaf's `fxMesh` to our FxMesh GUID, then calls the leaf's `Load(fxManager, doublonIndex)` so it
**re-resolves `meshIndex` from our GUID**. Because we reuse the game's *own* leaf (with its selector context/GPU data),
our mesh draws — where a foreign material handed in via `SetChannel` had no context and drew nothing.

**3. Bake (the District Factory window — `DistrictFactoryWindow.cs`):** **Tools ▸ HAF ▸ District Factory** does the
whole editor side in one Bake: pick the District (searchable dropdown over the project's district definitions), Browse a
model file, set Size / Rotation / Target-tris / Isolate, press **Bake**. It runs the same static bake core as the unit
Factory (`UniversalBaker.Build` — **no dummy pawn needed**; `pawnDescription` is registry-only there), wraps the result
as a district FxMesh via `DistrictBaker.BakeFxMesh`, and writes the registry entry. Two hard requirements the bake
handles for you —
- **Bone-free:** a unit static-bake rigs the mesh (boneWeights/bindposes) for its Skeleton; the district's *static*
  shader can't read a skinned vertex format, so it uploads but draws nothing. The bake writes a bone-free
  `_DistrictMesh` copy and wraps that.
- **Lean:** district building parts are tiny (hundreds of verts) and share the GPU buffer (below). Keep Target-tris
  modest, or set `DistrictBufferHeadroom`.

Then rebuild the mod (ships the FxMesh) and launch. The reactor renders.

## The district registry — `enc_districts.json`

The runtime side is data-driven: `BepInEx/config/enc_districts.json` (written by the District Factory window, mirrored
to the git-tracked `Assets/Databases/enc_districts.backup.json`) holds any number of district models at once:

```json
{ "districts": [
    { "district": "Extension_Base_BreederReactor",
      "fxMeshGuid": "1457749632,1176062388,715769744,1624515593",
      "atlasGuid": "260107174,1193535976,-95465828,-2065892038",
      "isolate": true }
] }
```

The plugin reads only `district` / `fxMeshGuid` / `atlasGuid` / `isolate` per entry (Newtonsoft — extra fields are
bake-time state for the window and are ignored). `atlasGuid` is the baked albedo the texture injection binds (below);
entries without it render untextured like before. Each entry gets its own leaf collection / private-leaf machinery, so several districts can
carry custom models simultaneously. The old single-model `[District]` keys (`DistrictName` + `DistrictFxMeshGuid`)
still work as a fallback **only when the registry has no entries**. `DistrictRepoint = true` remains the master enable.

---

## The GPU vertex-buffer constraint (and the lever for it)

District building meshes upload into a shared GPU mesh buffer — layer **'Visual'**, sized **3,000,000 verts**, drawn by
`FxComponentMeshContentManager.ContentLayer` (`GetFxMeshStructIndex` → `Register` → `FillMeshVertexAndBufferContent`).
In a built-up late-game city it runs ~99.85% full (measured: 2,995,550 / 3,000,000). An oversized mesh gets a slot but
**silently overflows the vertex buffer and is dropped** — the exact "assigned index 4606, renders nothing" symptom.

Two levers:
- **Lean bake** (above) so the mesh fits the free space.
- **`DistrictBufferHeadroom`** (config, opt-in, default 0): a Harmony prefix on `ContentLayer.LoadEncodingVertexAndBuffer`
  enlarges the **Visual** layer's `baseVertexBufferSize` at creation. Setting `2000000` grows it 3M → 5M
  (~+96 MB VRAM); the shader reads the buffer size dynamically so it's transparent. Verified in-game
  (`[District] enlarged 'Visual' mesh buffer: 3000000 -> 5000000`).

Diagnostics: **F8 window ▸ "Dump District"** lists every registry entry (matched? leaf built? resolved `meshIndex`, our
FxMesh verts + bounds) and each mesh-manager layer's fill (`verts used / size`).

---

## Config reference — `[District]`

| Key | Meaning |
|---|---|
| `DistrictRepoint` | master enable (for the registry AND the legacy keys) |
| `DistrictBufferHeadroom` | extra verts for the Visual buffer at init (0 = off; 2000000 = +~96 MB VRAM) |
| `DistrictName` | LEGACY fallback — target `ConstructibleDefinitionName`; used only when `enc_districts.json` has no entries |
| `DistrictFxMeshGuid` | LEGACY fallback — the baked FxMesh GUID `a,b,c,d` for `DistrictName` |
| `DistrictIsolate` | LEGACY fallback — scope the legacy entry to its own tiles (registry entries carry their own `isolate`) |
| `DistrictAffinityOverride` / `DistrictEvolverGuid` | earlier proof/experiment modes (superseded) |

**Gotcha:** a new config key only appears in the `.cfg` after the new build runs once; adding it by hand must go
**inside** the `[District]` section. The FxMesh ships only after a **mod rebuild/export**.

---

## Scoping to one tile — SOLVED (`DistrictIsolate`)

The leaf Elements are *shared across every district of a culture*, so the raw swap turns **every** matching building on
the map into our mesh. `DistrictIsolate` fixes that by giving only the target district a **private** leaf:

1. On the target district's channel-0 selector, `CollectLeaves` to a source leaf; `UnityEngine.Object.Instantiate` it
   (a private copy — mutating it can't touch the shared leaf).
2. Set the clone's `fxMesh` to our GUID, reset its `loadingStatus`, and call `LoadIFN(fxManager)` + `Load(fxManager,
   doublon)` so it gets a valid `MaterialIndex` and re-resolves `meshIndex` from our mesh.
3. Per-frame: set that district's `channels[layer].evolverMaterial` = the private leaf (**write the boxed struct back**
   into the array) and call the public `RefreshChannel(int, EventNameEnum)` so the Shuriken particle re-spawns and
   `PatchParticle` picks up the private leaf's `MaterialIndex`.

Because each `PresentationLevelBuildComponent` has its **own** channel + particle, this scopes the mesh to that one
tile. Build lazily (sub-materials load async → retry) and re-apply every frame (the game reloads the shared selector
into the channel on each `UpdateLevelBuild`). **Verified in-game: the reactor tile shows our mesh, the rest of the city
is untouched.**

## Placement: orientation, facing, position, grounding & preview

The District Factory has an **embedded preview pane** (2026-08-06): the baked mesh, textured, standing on a tile-sized
ground square pinned at the true in-game surface level. The placement controls split by job:

1. **Rotation offset — stand it up** (baked into the mesh; the preview shows the result after each Bake). The bake
   auto-aligns the *longest* axis, which can tip a near-cubic model onto its side around ANY axis (the plant needed
   `Z=-90`). Dial it in the preview — no relaunch round-trips.
2. **Facing on tile — turn it** (0–360°, previewed LIVE). Always rotates the standing building about the vertical, so
   it can never tip the model; written into the FxMesh at Bake. The **arrow on the preview tile** is the Facing
   reference — the direction 0° corresponds to — so re-dialing a building later stays consistent. *(The old
   three-field FxMesh import-angles control is retired from the UI — it overlapped Rotation offset and invited
   tipping; entries authored with it keep working.)*
3. **Position offset — place it** (previewed LIVE). Nudge across the tile in world units (a tile hex is ~7 across
   its flats; X/Z slide,
   Y lifts off the ground) — the same knob the Model Factory has for units, applied *after* the auto-level so the
   leveling can't cancel it.
4. **Grounding is automatic** (2026-08-06). The game plants the mesh by its origin at the tile surface and nothing
   re-grounds it — a rotation offset that changed which axis is "up" used to sink the model (the plant surfaced only
   its containment domes). The district bake **auto-levels**: it computes where the vertices land with the entry's
   draw-time rotation applied and shifts them so the lowest point sits on the surface, footprint centered. Any
   rotation combination comes out standing level. (District paths only — props/projectiles keep their meaningful
   pivots.)
5. **Scale.** Baked-in (the `Size` knob), so resizing needs a re-bake. A district tile hex is **~7 across its flats
   (~8 corner to corner)** — the preview hex is true size, measured from the map's center-to-center tile spacing
   (6.93). A full site-plan model can carry Size 5–6, a single building ~2.5–5.

## Texture — SOLVED (2026-08-06): the private output layer

Districts rendered **untextured** for three weeks — the swap reused the game's own leaf material, and our atlas had no
way in. Two measured facts cracked it:

- **The district building layer is a *full-texture* layer.** It has no `FxComponentTextureAtlasManager` entry; every
  leaf resolves `textureIndex` to the fixed full-texture slot (1) and the shader samples the layer material's bound
  sheet straight through the mesh UVs. (That's why an untextured custom mesh showed *patches of the culture's building
  sheet* — its 0..1 UVs swept a texture authored for baked-UV building parts.) An earlier design that painted a rect
  into the atlas-manager page was falsified by this trace and never shipped.
- **Texture is therefore a per-LAYER binding**, shared by every building drawn through that layer — recoloring the
  shared material would repaint the whole culture.

The unlock is one step up from the leaf clone: **clone the whole `FxOutputLayer`**. `BuildPrivateLeaf` instantiates the
leaf's output layer alongside the leaf (Unity resets the non-serialized runtime state, so the clone is unregistered);
during the leaf's own `Load`, the game's renderer **registers and loads the clone itself**
(`FxComponentRenderer.GetLayerIndexAddItIFN` — a real registration API), creating private runtime materials and command
buffers. `DistrictApplyTexture` then registers a null atlas-info slot for the new layer (so the game's own resolve
returns full-texture for it forever), points the leaf at slot 1, and **binds the baked albedo on the private runtime
materials** (`_MainTex` when present, else the largest bound sheet — `DistrictDebug` dumps every property to catch a
wrong pick). The mesh's own 0..1 UVs sample the albedo exactly; no other building is touched. Re-asserted periodically
because resolution switches rebuild runtime materials.

---

## History (for the record)

This was chased from ~8 angles that all *looked* like a wall before the recipe above cracked it:
- Inject a material via `SetChannel` → **vanishes** (no selector GPU context).
- Swap the affinity (runtime or data) → **null** unless the visual-levels/DistrictState also matches (the key insight).
- Every district material type (culture **selector**, National-Project **emitter**, wonder scaffolding) is a
  context-gated composite — there is no standalone plain drawer to point at.
- Adding an affinity→material mapping (`AssetReferenceDatabaseContent`) has no mod precedent (game-core).

The unlock was: don't hand the game a material — **reuse its own leaf Element and swap only the `fxMesh`**, with a
bone-free lean mesh that fits (or grow) the shared buffer.

Decompiled reference: `C:\tmp\reactor\` — `Selector.cs`, `Emitter.cs`, `Element.cs`, `DescElem.cs`, `GenDesc.cs`,
`MeshMgr.cs`, `FxMesh.cs`.
