# The GPU Vertex Budget — how many custom models you can add

The real limit on custom models is **not download size** (that compresses ~5:1 in the shipped
bundle; a 190 MB bundle zips to ~69 MB, and mod.io's soft limit is 100 MB with generous headroom
above). For **unit models** the real limit is a **GPU vertex buffer** the game packs every skinned
mesh into — this page records what that buffer actually is, measured live, and how to budget against
it. But it is only one of THREE separate draw-budget systems (units / districts / terrain), each
limiting something different — terrain, notably, stores **no vertices at all**. The full map is in
[Three pipelines, three different budgets](#three-pipelines-three-different-budgets--the-map-of-every-draw-limit)
below; the sections in between are the unit pipeline's detail.

## The mechanism (decompiled: `Amplitude.Graphics.FxComponentMeshContentManager`)

- Skinned pawn meshes are packed into **shared GPU buffers, one per "content layer."** Each layer has a
  vertex buffer, an index buffer, and a mesh registry (`FxOneMeshStruct[]`), filled by a running
  `currentVertexIndex` / `currentIndexIndex` / `currentMeshAddedCount` cursor.
- **Instancing makes copies free.** Rendering is `DrawMeshInstancedIndirect` — each **unique mesh is
  stored once** and drawn for every pawn of that type. 1 tank or 100 tanks of the same type = **one**
  entry. Units on screen are irrelevant to the budget.
- **Overflow is silent.** When the cursor would exceed a buffer, the manager logs
  `"Unable to store mesh … vertex buffer is not large enough"` and **drops the mesh** — you get
  missing / see-through geometry in-game (this is the vanished-rotor-mast class of bug), never a crash.
- The `baseVertexBufferSize = 100000` / `baseIndexBufferSize = 250000` / `maxMeshCount = 256` constants
  in the decompiled source are only **default initializers** — at runtime the game sizes the layers
  **far larger** (see measured values below). Do not trust the source defaults; measure.
- **There is NO hard per-mesh cap in practice.** Each layer carries a `maxMeshTriangleCount` — a
  per-mesh triangle ceiling whose overflow is even nastier than the buffer's: quads beyond it are
  **silently truncated at encode** (`FillIndexBufferContentFromQuad` clamps to the cap) — the model
  renders with holes and *nothing* is logged. But the shipped layer data sets it to **0 = unlimited on
  every layer** (verified live), so a single unit's practical limit is simply the free space left in
  the shared pool.

## Measured live (F8 window — the always-on "GPU mesh buffer (live)" readout, a Contemporary-era save)

Three layers exist; custom models land in the pawn layer (`FXMeshLayerIndex = 2`):

| layer | name | vertices | indices | meshes | maxTris/mesh |
|---|---|---|---|---|---|
| 0 | `Visual` | 2,995,550 / **3,000,000** (99%)¹ | 5.73M / 9M (63%) | 4606 / 8000 | unlimited |
| 1 | `Emitter` | 2,700 / 10,000 (27%) | 10k / 90k (11%) | 20 / 2000 | unlimited |
| **2** | **`MeshWithSkeletonParticleIndexBuffer`** ← **custom models** | **694,126 / 1,000,000 (69%)** | 1.48M / 6.5M (22%) | 695 / 2500 | unlimited |

¹ before `DistrictBufferHeadroom`; with the district headroom set it reads e.g. 5,000,000.

**The pawn-layer ceiling is ~1,000,000 vertices / 6,500,000 indices / 2,500 meshes** — 10× the source
default. Vertices are the binding constraint (69% used vs 22% indices / 28% mesh slots). Layer 0
`Visual` is the shared **building/district** buffer (the district axis draws from it), unrelated to
your unit models.

## Raising the ceilings — `[Buffers] BufferOverrides`

All four limits are plain fields set at layer creation, and the plugin can override any of them
(same Harmony seam as `DistrictBufferHeadroom`, generalized):

```ini
[Buffers]
BufferOverrides = MeshWithSkeleton:verts=+1000000,idx=+2000000,meshes=+1000
```

- Format: `<layerNameSubstring>:verts=+N,idx=+N,meshes=+N,maxtris=N`, semicolon-separated for several
  layers. `verts`/`idx`/`meshes` **add** to the buffer sizes; `maxtris` **sets** the per-mesh cap
  absolutely (0 = unlimited). Layer names come from the Mesh Budget dump.
- Applied once at layer creation (next launch); the confirmation is logged as
  `[Buffers] '<layer>' baseVertexBufferSize: 1000000 -> 2000000`.
- Cost is VRAM only: pawn vertices are ~28 B packed, so `verts=+1000000` ≈ +28 MB, `idx=+2000000`
  ≈ +8 MB. The shader reads buffer sizes dynamically, so the change is transparent.

With the override above, a **100k-vert hero unit is entirely practical** — the shipped default pool
already fits one today (~300k free late-game); the override just restores comfortable headroom for
the rest of the roster.

## What actually counts

**buffer used = Σ (vertices of each *distinct loaded model type*)** — independent of instance count.

Do not confuse that vertex total with Model Factory's **Reduce to ~tris** field. The field is a Blender triangle
ceiling; it is not a vertex target and there is no stable tris→verts conversion. UV seams, material boundaries, skin
weights, and import splitting can make the baked vertex count substantially higher than the triangle count. Use the
bake log for the model's actual `verts=` cost and F8 for the roster-wide total.

- **Not units on screen** — instancing; copies are free.
- **Not the whole catalog** — only meshes that are **loaded** register. The layer held 695 meshes while
  ~10 units were visible, so it's "loaded types," far more than what's on screen, but not everything
  possible. A roster unit that never loads in a given game costs nothing.
- **Diversity is the cost, weighted by size.** A wide variety of *lean* units is cheap; a few *heavy*
  unique types can cost as much as many lean ones.
- **Resizing a unit is free.** The [unit-size axis](Unit-Size.md) multiplies the vertex positions of a mesh the
  layer already holds — no new mesh, no new vertices. (A future per-instance variant that needs a *second* size of
  the same unit would have to clone the mesh, and that clone would cost its full vertex count.)

## The fill is roster-wide at load, not per-era

Originally we assumed "era-clustering": that a late-game save loads more of the roster at once. The
measurements say otherwise — **a brand-new game reads 701,866 verts (70%), virtually identical to a
Contemporary save's 694,126 (69%)**. The pawn pool is filled at load with (nearly) the full roster's
meshes, regardless of era. Consequences:

- **The baseline is ~700k everywhere** — you always budget against the same ~300k free, whether Era 0
  or endgame. No per-era relief, but also no late-game surprise.
- **Every model you add costs its verts in every game**, from turn 1 — that's roughly room for
  **7–10 more heavy (30–42k) types** or **~20 lean (~15k) ones** on the default pool, or set
  `BufferOverrides` (above) and stop worrying.
- **Leanness still compounds**: trimming each model 40k→15k more than doubles how many distinct types
  fit (see the weld / low-poly notes in the Factory manual).

## How to measure (in-game)

The plugin exposes the live buffer usage:

- **F8 window — the "GPU mesh buffer (live)" readout** shows every layer's fill (verts/idx/meshes/maxTris),
  updating live in the panel (no button needed). **Shift+F8** also logs the same to `BepInEx/LogOutput.log`
  (`[Budget]` lines).

Spawning 10 more of the same unit won't move the numbers (instancing — copies are free).

## Answered (previously open) questions

1. **When do meshes register?** At load, roster-wide: a fresh Era-0 game already reads ~70% — the same
   as a Contemporary save. Not on-demand per spawn, not per era.
2. **Per-era unload vs accumulate?** Moot — the pool is filled up-front and stays ~constant. The real
   ceiling is "the whole loaded roster," identical in every era, and it's ~700k/1M with vanilla + the
   current ENC set.

## Three pipelines, three different budgets — the map of every draw limit

**There is no single "vertex limit" in this game.** Three separate rendering systems each budget something
different, and they fail the same way (silently not drawn) for different reasons. Everything below is
measured, not assumed:

| Pipeline | What is stored | Pool limit (shared) | Per-mesh limit | Raise it with |
|---|---|---|---|---|
| **Units (pawns)** | baked skinned meshes, one copy per TYPE (instances free) | **~1,000,000 verts** (pawn layer; ~700k used by the roster) | 16,320 quads per FRAGMENT (compiled shader stride) | `[Buffers] BufferOverrides` for the pool; the 0.5.7 **multi-fragment split** (both bake paths since 2026-09-20) for the per-mesh cap |

> **THE TWO CEILINGS PULL AGAINST EACH OTHER** (2026-09-20, the steam frigate). The split is the cure for the
> per-fragment quad ceiling, and it is paid for out of the pool: each chunk duplicates the vertices on its seam. How
> much depends entirely on where the cuts fall — that ship went 99,676 → **125,369 verts (+26%)** as four static
> fragments, but 99,676 → **100,648 (+1%)** as four animated ones. Read your own bake's `BAKED MESH` lines rather
> than assuming either figure. The pool is shared by every unit type, and when
> it fills the game stops uploading meshes entirely — **units and districts both stop drawing, with no error**. That
> day's buffer was already doubled to 2,000,000 by `BufferOverrides` and sat at 1,999,968; one static re-bake of one
> ship was the straw. Read the fill with **F8** (`L2 … verts %`), and raise the pool with
> `BufferOverrides = MeshWithSkeleton:verts=+2000000` (roughly +48 MB VRAM per million) before splitting a second
> large model. Reducing triangles pays both ceilings at once; splitting pays one by spending the other.
| **Districts / buildings** | baked static meshes in the shared `Visual` layer | **3,000,000 verts**, ~99% full late-game — the tightest real vertex wall | 255 sub-particles × PPC (PPC dynamic, auto-boosted since 0.5.7) | `DistrictBufferHeadroom` for the pool; `DistrictMeshDensityBoost` floor for the ceiling — [District-Visuals](District-Visuals.md) |
| **Terrain tiles** | **NO stored vertices at all** — `ProceduralTerrainRenderer` GENERATES the hex geometry on the GPU every frame (visibility kernel → repack → draw-procedural + tessellation) and throws it away | n/a — vertices are manufactured per frame | **10,000 visible hexagons** per frame (post-culling) and 800,000 draw commands, both plain ints on the technical-settings asset | `[Terrain] TerrainHexagonBufferMultiplier` (0.5.7, experimental — section below) |

The practical consequences:

- A **unit or district** that is "too detailed" starves a shared *vertex pool* — the cost is per distinct
  model type, paid at load, and VRAM buys it back.
- **Terrain** can never run out of vertices — when tiles stop rendering on huge maps, the suspect is the
  *visible-hexagon count* the visibility pass may emit, not geometry storage. Different disease, different
  medicine.
- Per-hex terrain DATA (altitude, biome, sculpt/river indices) is bit-packed in the const hex buffer — those
  masks bound *value ranges* (how many distinct terrain types, etc.), never vertex counts.

## Other engine ceilings — the terrain tile limit (community lead, UNVERIFIED)

Recorded 2026-09-13 after the multi-fragment unit split shipped (0.5.7): a community report (Discord)
says map **tiles beyond roughly 21,000 stop rendering**, that each tile draws as **3 parts**, and asks
whether the unit trick generalizes. Everything below is a **hypothesis on one probe session** — treat it
per the [Review-Backlog rules](Review-Backlog.md) (re-verify before acting), it is not a finding.

- **The number diagnoses itself**: 21,845 tiles × 3 parts = **65,535 — the 16-bit (ushort) ceiling**.
  That smells like an index/element count limit in the terrain pipeline, a *different species* from the
  units' 16,320-quad **per-fragment** clamp (255×64, pawn compute shader).
- **Round 2 (same day, deeper probe): the renderer is MANAGED after all.**
  `Amplitude.Mercury.Terrain.ProceduralTerrainRenderer` is C# — GPU-indirect (visibility/repack/draw
  compute kernels) but driven entirely from patchable code. Its `CreateOrResizeVisibleHexagonsBuffer` /
  `CreateOrResizeDrawCommandsBuffer` size their buffers from **two plain ints on the loaded
  `TerrainRendererTechnicalSettings` asset** (IL read, token-resolved; no clamp constant in the renderer).
- **Measured live** (the `[Terrain]` probe line, 2026-09-13): `VisibleHexagonsBufferSize = 10,000`,
  `DrawCommandBufferSize = 800,000`. **Not 65,536** — so the clean "ushort capacity" theory is dead in
  its original form. Two live theories remain:
  1. **The visible-hexagon budget**: 10,000 is the post-culling *on-screen* hex budget. A big world
     zoomed far out can plausibly exceed it, and the repack would drop the rest — matching "tiles beyond
     a certain amount will not render." Testable directly with the multiplier below.
  2. **A ushort×3 edge/part index elsewhere**: 21,845 × 3 = 65,535 still fits a 16-bit index over
     "3 parts per tile" (a hex owns 3 edges in standard storage). Candidates checked and CLEARED so far:
     `WorldMapProviderHelper.ImportFrom`'s 65536s are capability FLAG BITS; `Matching.BakedElement`'s
     65535 is the pattern-library sentinel (`NoMatchingEntryIndex`), not a tile count.
- **The instrument exists**: `[Terrain] TerrainHexagonBufferMultiplier` (plugin config, default 1 =
  vanilla) multiplies both settings ints before buffer creation, and the probe line logs the shipped
  values every launch. **The decisive field test**: a >10k-visible-tile view (huge map, max zoom-out)
  with the multiplier at 1 vs 4 — if missing far tiles appear, theory 1 is confirmed and the ceiling is
  effectively broken; if nothing changes, hunt theory 2's ushort in the compute kernels' data layout.
- **Scope caution**: a >21k-tile world stresses more than rendering (simulation, saves, pathing) —
  rendering may not even be the binding constraint.
