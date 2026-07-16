# District Visuals — replacing a district's on-map model

**Status: SOLVED (2026-07-16) — a custom 3D model renders on a *single* district tile in-game.** Render, fit, and
scope are all working: the reactor mesh sits on the reactor tile and the rest of the city is untouched. This was long
thought impossible (see the history at the end); it is not. This page documents the working recipe, the mechanism, and
the constraints. Only cosmetic tuning (scale/orientation/texture) remains.

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

**2. Runtime (the plugin, mode `DistrictFxMeshGuid`):** the rendered mesh lives deep inside the resolved material:

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

**3. Bake (the Factory, `DistrictBaker.cs`):** two hard requirements on the FxMesh —
- **Bone-free:** a unit static-bake rigs the mesh (boneWeights/bindposes) for its Skeleton; the district's *static*
  shader can't read a skinned vertex format, so it uploads but draws nothing. "District ▸ 1. Bake District FxMesh" now
  writes a bone-free `_DistrictMesh` copy and wraps that.
- **Lean:** district building parts are tiny (hundreds of verts) and share the GPU buffer (below). Bake the model at a
  low tri count (~2000 tris ≈ ~1,500 verts).

Put the FxMesh GUID in `[District] DistrictFxMeshGuid`, set `DistrictName`, `DistrictRepoint = true`, rebuild the mod,
launch. The reactor renders.

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

Diagnostics: **F8 window ▸ "Dump District"** shows our FxMesh verts + bounds, the collected leaf count, the resolved
`meshIndex`, and each mesh-manager layer's fill (`verts used / size`).

---

## Config reference — `[District]`

| Key | Meaning |
|---|---|
| `DistrictRepoint` | master enable |
| `DistrictName` | target `ConstructibleDefinitionName` (e.g. `Extension_Base_BreederReactor`) |
| `DistrictFxMeshGuid` | **the working mode** — our baked FxMesh GUID `a,b,c,d`; swaps the leaf meshes |
| `DistrictIsolate` | **scope to one tile** — build a private per-instance leaf so only `DistrictName` changes (else the swap is global). On by default recommended. |
| `DistrictBufferHeadroom` | extra verts for the Visual buffer at init (0 = off; 2000000 = +~96 MB VRAM) |
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

## Remaining refinements (cosmetic only)

1. **Scale + orientation.** The mesh bakes at unit orientation/size; tune Rotation / FxMesh `importAngles` / bake size
   so it seats on the tile.
2. **Texture (optional).** It rides the vanilla shader untextured → flat-dark; a baked atlas would color it.

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
