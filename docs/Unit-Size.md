# Unit size — resize any unit at runtime (the sixth data axis)

Change **how big a unit's model renders** — vanilla units included — with **zero baked assets**: a rule in
`unitScales` names a pawn definition and a factor, and the plugin resizes that unit at load. No re-bake, no model
edit, no mod rebuild. Delete the rule and the unit is vanilla next launch.

> Status: **VERIFIED IN-GAME 2026-07-29** — an Era-1 Bireme scaled ×2 (and ×4) renders correctly assembled (hull,
> oars, mast in proportion) and **animates normally**. Authored in the **Resize Lab** (Tools ▸ HAF ▸ Resize Lab).
> **[Era anchoring](#era-anchoring-working) works too**: the same rule shrinks the ship as ages advance, so an
> ancient hull is a toy beside a modern cruiser.

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
[Resize] 1 unit-scale rule(s): 'Era1_Common_Biremes_01'x4
[Resize] 'Era1_Common_Biremes_01' -> desc 74 scale x4 (profile 7)
[Resize] era anchoring live — global era index 5 (divisor 5)
[Resize] desc 74 -> x0.8 (era 5): 2 mesh(es), 3040 vert(s) re-scaled by 0.8x + per-pawn placement x0.8
```

Line 1 = the rule parsed, line 2 = it matched a live pawn definition (and passed the human check), line 3 = the era
anchor, line 4 = the size actually applied (`rule.scale / era`). If line 2 says
`SKIPPED (human-presentation profile N)`, the target is a human unit. If the last line never appears, no unit of that
type has been drawn yet.

## Registry format

```json
"unitScales": [
  { "match": "Era1_Common_Biremes_01", "scale": 2.0, "trueSize": 0.0, "note": "" }
]
```

| Field | Meaning |
|---|---|
| `match` | Substring of the `PresentationPawnDefinition` name, case-insensitive. |
| `scale` | Base multiplier. All matching rules multiply together, then the result is divided by the current era — see [Era anchoring](#era-anchoring-working). |
| `trueSize` | Reserved (real-world size in metres) for the reference-table version of era anchoring — ignored today. |
| `note` | Free text for your own bookkeeping. |

## How it works

Two halves, both derived from reading the game's **compiled GPU shaders** (see [Why it must be done this
way](#why-it-must-be-done-this-way)):

**1. Geometry — whenever the target size changes.** The unit's GPU descriptor lists its mesh fragments; each
fragment's packed field carries the mesh's start index in the low 24 bits, which identifies the mesh in the Fx
content layer's mesh table. For every mesh the unit draws, the plugin multiplies the **raw vertex positions** in the
layer's CPU-side vertex buffer and re-uploads it. The pawn layer's vertex format
(`VertexDataPosUVNormalTangentBones`) stores positions as plain floats, so this is a direct multiply; a format guard
refuses to write anything else (the static layers quantize positions and would be corrupted). Because the scale is
uniform, normals and tangents stay valid untouched. Mesh and descriptor bounding boxes scale too, so culling
follows the new size.

The multiply is applied as a **ratio, not an absolute**: each mesh records the factor currently baked into its
vertices, and a new target multiplies by `target / applied`. Re-scaling is therefore idempotent and reversible
instead of compounding — which is what makes [era anchoring](#era-anchoring-working) able to resize a unit *while
the game runs*. The record is **self-verifying**: it also stores the first vertex exactly as it was left, so if the
engine ever reloads its Fx content (menu round trip, streaming) the probe stops matching and the plugin re-scales
from vanilla instead of trusting stale bookkeeping — closing both double-scaling and silent under-scaling.

**2. Placement — every frame, per pawn.** `PawnEntry.ObjectSpace.Scale *= s`. The animation compute pass multiplies
bone world positions by it, and the draw shader scales each part's bind-pose offset by it
(`entry.Scale = ObjectSpace.Scale / InverseBindPose.Scale`). That is what keeps oars, masts and turrets attached to a
model whose geometry just grew. The pawn buffer is immediate-mode — the game rebuilds it every frame — so this half
re-applies per pawn per frame, which is also why a size change takes effect immediately once the geometry follows.

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
- **Bookkeeping is self-correcting, not assumed.** Mesh records are keyed by (layer, mesh) and survive a session
  reset, because the Fx content buffers can outlive a save/load while descriptor ids are re-resolved. Rather than
  betting on that, each record carries a probe vertex: if the buffer comes back vanilla the plugin notices
  (`mesh N came back unscaled (Fx content reloaded) — re-scaling from 1`) and redoes the work. Descriptor bounding
  boxes deliberately err **large** — they only drive culling, and too big is invisible while too small pops units
  out at screen edges.

## Era anchoring (working)

The founding use case: **ship sizes across ages never matched** — a Man O' War beside a Battleship is absurdly
large. A unit sized generously for its own era should shrink into perspective as later ages arrive.

**Working today (crude proof of concept):**

```
effective scale = rule.scale / era
```

The plugin reads `Sandbox.Timeline.GetGlobalEraIndex()` by reflection (the class is `internal`) every two seconds.
That index is the **game-wide** era — Amplitude computes it from *every* major empire's research, not the local
player's, which is the correct anchor because unit visuals are shared by everyone on the map.

> **Verified in-game 2026-07-29.** A `Era1_Common_Biremes_01 ×4` rule in a game whose global era index was 5
> rendered the bireme at **×0.8** — a toy beside a custom Stealth Cruiser, where the same rule showed an epic
> full-size trireme back in era 1.
>
> ```
> [Resize] era anchoring live — global era index 5 (divisor 5)
> [Resize] desc 74 -> x0.8 (era 5): 2 mesh(es), 3040 vert(s) re-scaled by 0.8x + per-pawn placement x0.8
> ```

**The index base:** era index 5 was observed during normal late-game play, consistent with `0 = Neolithic,
1 = Ancient … 6 = Contemporary` — so the index equals the era number as players count them, and dividing by it
directly is meaningful (Neolithic is guarded to divide by 1).

**Mid-game era changes resize live.** The ratio machinery described [above](#how-it-works) means a new era simply
sets a new target and the geometry is multiplied by the difference — the ship shrinks in place within the poll
interval, no reload, no compounding. The plugin logs it:

```
[Resize] ERA CHANGED 4 -> 5 — rescaling by 1.25x (units shrink as ages advance)
```

*(The formula and the live resize are both implemented; the live shrink has not yet been watched during an actual
era transition — the verification above was a session that started in era 5.)*

### Why this is still crude, and what the real version is

Dividing by the era index is monotonic but arbitrary: era 1 → 2 halves a unit, while 5 → 6 barely changes it. It
proves the anchor moves; it does not express intent. The real design is already reserved in the registry:

1. Each rule carries the unit's **real-world size** in metres (`trueSize`).
2. A per-era **reference size** table says how many metres "reads as normal" in each age (a 30 m trireme is a big
   ship in the Ancient era; a 270 m battleship is normal in the Industrial one).
3. `scale = trueSize / reference(era)` — so each era's jump is authored, not an artifact of arithmetic, and units
   can be entered by their real dimensions instead of by trial and error.

Everything that made step 3 hard is already built: era detection, the ratio engine, and live re-scaling.
