# Unit size — resize any unit at runtime (the sixth data axis)

Change **how big a unit's model renders** — vanilla units included — with **zero baked assets**: a rule in
`unitScales` names a pawn definition and a factor, and the plugin resizes that unit at load. No re-bake, no model
edit, no mod rebuild. Delete the rule and the unit is vanilla next launch.

> Status: **VERIFIED IN-GAME 2026-07-29** — an Era-1 Bireme scaled ×2 renders correctly assembled (hull, oars,
> mast in proportion) and **animates normally**. Authored in the **Resize Lab** (Tools ▸ HAF ▸ Resize Lab).

Together with [Formations.md](Formations.md) (how *many* models a unit fields) this covers both halves of
R.E.D.-style unit rebalancing: **count** and **scale**.

---

## What it controls

- **Render size of a unit's model**, uniformly — geometry *and* the placement of its parts. Animation keeps
  playing normally, because the scale never touches the animation data.
- **Any pawn definition**, vanilla or modded, matched by a substring of its name (`Era1_Common_Biremes_01`).
- **All matching rules multiply**, so a broad rule (`Biremes` ×0.9) and a specific correction (`Era1_Common_Biremes_01`
  ×1.1) compose.

**Humans are excluded on purpose.** Any pawn whose `AnimationCapabilityProfile` belongs to the human-carrying family
(Human, mounted fighter/driver, chariot crew, servant, Mount, Chariot) is skipped — scaled soldiers read as absurd and
their gear anchors are driven by procedural bone layers this axis does not touch (the same wall documented in
[Formations.md § Model scale](Formations.md#model-scale-two-modes-and-their-limits)). Animals, ships, planes,
vehicles, missiles and inanimate objects scale freely — the Resize Lab's picker only *offers* those.

## User workflow (no mod rebuild)

1. **Tools ▸ HAF ▸ Resize Lab.**
2. **+ Add rule**, then either type a name fragment or press **Pick** (a searchable list of every scalable pawn
   definition in the project — humans are filtered out).
3. Set the **scale** slider (0.1–10) and an optional note.
4. **Save** — writes the rule into your pack registry.
5. **Relaunch the game.** Rules apply as units spawn.

Verify in `BepInEx/LogOutput.log`:

```
[Resize] 1 unit-scale rule(s): 'Era1_Common_Biremes_01'x2
[Resize] 'Era1_Common_Biremes_01' -> desc 74 scale x2 (profile 7)
[Resize] desc 74: 3 mesh(es), 4812 vert(s) scaled x2 + per-pawn placement x2
```

Line 1 = the rule parsed, line 2 = it matched a live pawn definition (and passed the human check), line 3 = it was
applied. If line 2 says `SKIPPED (human-presentation profile N)`, the target is a human unit. If line 3 never
appears, no unit of that type has spawned yet.

## Registry format

```json
"unitScales": [
  { "match": "Era1_Common_Biremes_01", "scale": 2.0, "trueSize": 0.0, "note": "" }
]
```

| Field | Meaning |
|---|---|
| `match` | Substring of the `PresentationPawnDefinition` name, case-insensitive. |
| `scale` | Direct multiplier. All matching rules multiply together. |
| `trueSize` | **Reserved for v2** (real-world size in metres) — ignored today. |
| `note` | Free text for your own bookkeeping. |

## How it works

Two halves, both derived from reading the game's **compiled GPU shaders** (see [Why it must be done this
way](#why-it-must-be-done-this-way)):

**1. Geometry — once per unit type, per session.** The unit's GPU descriptor lists its mesh fragments; each
fragment's packed field carries the mesh's start index in the low 24 bits, which identifies the mesh in the Fx
content layer's mesh table. For every mesh the unit draws, the plugin multiplies the **raw vertex positions** by *s*
in the layer's CPU-side vertex buffer and re-uploads it. The pawn layer's vertex format
(`VertexDataPosUVNormalTangentBones`) stores positions as plain floats, so this is a direct multiply; a format guard
refuses to write anything else (the static layers quantize positions and would be corrupted). Because the scale is
uniform, normals and tangents stay valid untouched. Mesh and descriptor bounding boxes scale too, so culling
follows the new size.

**2. Placement — every frame, per pawn.** `PawnEntry.ObjectSpace.Scale *= s`. The animation compute pass multiplies
bone world positions by it, and the draw shader scales each part's bind-pose offset by it
(`entry.Scale = ObjectSpace.Scale / InverseBindPose.Scale`). That is what keeps oars, masts and turrets attached to a
model whose geometry just grew. The pawn buffer is immediate-mode — the game rebuilds it every frame — so this half
re-applies per pawn per frame; the geometry half is guarded to run exactly once.

**Cost: free.** Scaling in place adds no vertices and no draw calls — it edits geometry the game already loaded, so
the [vertex budget](Vertex-Budget.md) is untouched and instances stay GPU-instanced.

**Consequence of the same fact:** the mesh is shared by every unit of that type, so a rule resizes **all** of them.
One bireme big and another small at the same time is not possible this way — that needs a genuine mesh *clone*
(a second copy in the buffer, paid for in vertex budget) and a per-unit descriptor repoint.

### Why it must be done this way

Every attempt to resize a unit through a *transform* fails, and the shaders say why. Using the ShaderDump toolchain
(`tools/ShaderDump` — Unity bundle → AssetsTools.NET → `D3DDisassemble`) the whole pawn pipeline was disassembled:

1. **`CSAnimateFirstPass`** (pose sampling) uses a bone's `Local.Scale` only to multiply pose *translations*, then
   writes the bone's output scale as a **literal 1.0** (`mov r3.y, l(1.000000)` immediately before the store) — the
   GPU twin of the CPU path's hardcoded `result.Scale = 1f`.
2. **`CSAnimateSecondPass`** composes the bone chain and emits `entry.Scale = chainScale × (1/IBP.Scale) ×
   ObjectSpace.Scale`, and multiplies bone world positions by `ObjectSpace.Scale`.
3. **The draw vertex shader** (`Amplitude/ParticleSkinnedMeshRender`, vertex-pulling; all 128 D3D11 variants were
   swept) applies `entry.Scale` to **one thing only: the bind-pose translation**. Vertex positions are transformed
   by a pure rotation-plus-translation built from the blended quaternion. **No instruction multiplies vertex
   positions by any scale.**

So a transform can move a unit's parts *apart* but can never grow the parts themselves — which is exactly the
"spread oars, same hull" result the field experiments produced. Size lives in the vertex buffer, and nowhere else.

Three levers were falsified in the field before the shaders settled it — `PawnEntry.ObjectSpace.Scale` alone, bone
`Local.Scale` in the GPU skeleton buffer, and `InverseBindPose`/`BindPose` scale (whose two legs cancel exactly in
the draw: `entry.Scale × IBP.T` = `(1/2s) × 2T`). Don't re-attempt them; do reach for ShaderDump whenever the
decompiled C# and the rendered result disagree.

## Limits & caveats

- **Per unit type, not per instance** — see above. All units sharing the mesh scale together.
- **Humans excluded by design** (profile check at rule resolution).
- **Custom HAF models are not touched by this axis** — they scale at bake time via the Model Factory's **Size**
  field, which is exact and free. (The Resize Lab's custom-entry slider section is vestigial and slated for removal.)
- **Applies as units spawn**, so a rule takes effect on the next launch, and only once a unit of that type is drawn.
- **Extreme factors** are physically honest but visually silly: a ×10 ship overlaps neighbouring tiles and its
  selection/banner UI stays tile-sized. 0.5–2 is the sane band for rebalancing; the slider allows 0.1–10 for
  experiments.
- **Guard scope:** the "already scaled" set is keyed by (layer, mesh) and deliberately **not** cleared between
  sessions in one process, because the Fx content buffers survive save/load while descriptor ids do not — clearing
  it would double-scale. If a scaled unit ever renders vanilla-sized after a main-menu round trip, relaunch (and
  report it — that would be a buffer teardown the guard should learn about).

## Planned: era-anchored sizing (v2)

The founding use case: **ship sizes across ages never matched** — a Man O' War beside a Battleship is absurdly
large. The design (registry field `trueSize` is already reserved for it):

1. Each rule carries the unit's **real-world size** in metres.
2. The plugin reads the **current game era** — `Sandbox.Timeline.GetGlobalEraIndex()` (game-wide, computed from all
   empires' research; the right anchor for a shared presentation) — and computes `scale = trueSize / reference(era)`
   from a per-era reference table.
3. As ages advance the anchor moves, so older units render honestly smaller next to modern ones. Re-anchoring live
   pawns on an era change can reuse the `respawnAfterLoad` lever (re-running the game's own `UpdatePawns`).

The engine this document describes is what v2 rides on; only the rule arithmetic and the era poll are missing.
