# Unit size — resize any unit at runtime (the sixth data axis)

Change **how big a unit's model renders** — vanilla units included — with **zero baked assets**: a rule in
`unitScales` names a pawn definition and a factor, and the plugin resizes that unit at load. No re-bake, no model
edit, no mod rebuild. Delete the rule and the unit is vanilla next launch.

> Status: **VERIFIED IN-GAME 2026-07-29** — an Era-1 Bireme scaled ×2 (and ×4) renders correctly assembled (hull,
> oars, mast in proportion) and **animates normally**. Authored in the **Resize Lab** (Tools ▸ HAF ▸ Resize Lab).
> **Units also age**: the **[Global Era Lab](#era-ageing--the-global-era-lab)** grid says how much smaller a unit
> reads as later eras arrive, so an ancient hull becomes a toy beside a modern cruiser.

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
  { "match": "Era1_Common_Biremes_01", "scale": 4.0, "era": 0, "trueSize": 0.0, "note": "" }
],
"eraGrid": [
  { "unitEra": 1, "scales": [1, 1, 0.75, 0.5, 0.333, 0.2, 0.15], "note": "" }
]
```

`eraGrid` rows are indexed by **absolute era**, so `scales[5]` is the modifier while the world is in era 5.

| Field | Meaning |
|---|---|
| `match` | Substring of the `PresentationPawnDefinition` name, case-insensitive. |
| `scale` | The unit's size **in its own era**. All matching rules multiply together, then the [Global Era Lab](#era-ageing--the-global-era-lab) grid ages the result. |
| `era` | The unit's own era, i.e. its row in that grid. `0` = auto-detect from the name (`Era4_…` → 4); set it for definitions with no era token. |
| `trueSize` | Reserved (real-world size in metres) for a future reference-size layer — ignored today. |
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
instead of compounding — which is what makes [era anchoring](#era-ageing--the-global-era-lab) able to resize a unit *while
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

## Era ageing — the Global Era Lab

The founding use case: **ship sizes across ages never matched** — a Man O' War beside a Battleship is absurdly
large. A unit sized generously for its own age should recede into perspective as later ages arrive.

**Tools ▸ HAF ▸ Global Era Lab** authors that as a **grid**: rows are the era the *unit* belongs to, columns the era
the *world* is in, and the cell multiplies that unit's Resize Lab scale.

```
effective scale = rule.scale × grid[unit era][world era]
```

Why a grid rather than one modifier per era: how much a unit should shrink depends on **both** how old it is and how
far the world has moved. In the Contemporary age an Ancient trireme and an Industrial battleship must age very
differently — one curve cannot say that, a grid can.

| | 2 Cla | 3 Med | 4 Ear | 5 Ind | 6 Con |
|---|---|---|---|---|---|
| **1 Ancient** | 0.75 | 0.5 | 0.333 | 0.2 | 0.15 |
| **2 Classical** | — | 0.666 | 0.45 | 0.3 | 0.2 |
| **3 Medieval** | — | — | 1 | 1 | 1 |

*(An example authoring pass, not a shipped default.)* With the first row, a Bireme ruled ×4 renders ×4 in the
Ancient era, ×0.8 once industry arrives and ×0.6 in the Contemporary age.

**Rules that shape the grid:**

- **Naval only, for now.** Ships age with the world; land and air units keep their authored size in every era. Ships
  are where the mismatch is glaring and where scaling is safest (single pawn, no formation spacing, no gear anchors).
  This deliberately leaves the cave-bear case intact — an animal is a *land* unit, so it still scales via a Resize
  Lab rule, it just doesn't drift as ages pass.
- **Defaults are 1.0 everywhere.** The Lab ships neutral and the runtime invents no curve — every number that
  changes a unit's size is authored by you. An untouched grid means units simply keep their Resize Lab size.
- **Only units with a Resize Lab rule are affected.** The grid modifies opted-in units and can never resize
  anything else.
- **Ageing is relative to the unit's own era**, so a unit introduced in era 5 renders exactly as authored when it
  appears rather than being shrunk by era 5's modifier. A unit at or before its own era is always 1.0 — which is
  why the grid is 5×5 (rows 1–5, columns 2–6): an era-6 unit has no later age, and in era 1 nothing has aged.
- **The unit's era is read from its name** (`Era4_Common_ManOWar_01` → 4). For definitions with no era token, set
  the **Era** column on the rule in the Resize Lab.
- **Formation can swap as the unit shrinks** (VERIFIED 2026-07-30): per-unit `{scale up to, formation}` rows —
  authored in the **Formation Override** window on the unit's link, *not* here (an early global table in this Lab
  remains as a legacy fallback with a Clear button). When the effective scale crosses a threshold the unit
  re-forms live — an aged Bireme becomes three small wedge-formation hulls. Details:
  [Formations.md — Formation by size](Formations.md#formation-by-size-era-ageing--verified-in-game-2026-07-30).

### What counts as "the era the world is in"

```
anchor = max( built frontier for the unit's domain , the world's era )
```

**The built frontier** is what is actually on the map: every two seconds the plugin walks each major empire's armies
and squadrons, reads each unit's era off its definition name (Amplitude names them `Era1_…`), and keeps the maximum
**separately for land, naval, air and missile**. A ruled unit is measured against its *own* domain — the moment an
era-6 battleship exists, ships are compared to era 6, while land progress leaves them alone. A trireme should look
small beside a battleship, not beside a tank.

**The world's era** (`Timeline.GetGlobalEraIndex()`) is combined in as a floor, because the frontier alone says
nothing in a game where nobody bothered to build a navy — the trireme would stay huge into the Contemporary age.
Taking the higher of the two means the anchor only moves forward: ships pull it up the moment a modern hull exists,
and general progress carries it even at an empty sea.

Why *that* floor and not the empires' technological era — the two research anchors fail in opposite directions:

| Anchor | Behaviour |
|---|---|
| `Timeline.GetGlobalEraIndex()` | A threshold over the *sum* of all empires' techs, so it **lags** the frontier: a late game reported era 5 while era-6 ships were already sailing. Harmless as a floor inside a `max()`. |
| Empires' technological era | **Overshoots**: Humankind advances eras by *fame*, so an empire can sit in the last era without a single unit from it. As a floor it would undo the point of measuring what was built — kept only as a last resort if the aggregate index is unavailable. |

Era indices equal the numbers players see, with Neolithic at 0 — confirmed in-game.

**Live readout.** The F8 debug window (Humankind Asset Framework) shows the frontier per domain and, for each ruled
unit, its own era, the frontier it is measured against, the modifier that produces, and the size currently applied:

```
Anchor = max(built frontier, world era 5) — built: naval 6 Contemporary | land 5 Industrial | air none   (tech era 5)
era-grid rows authored: 5   |   scaled units: 1
  Era1_Common_Biremes_01: rule x4 (own era 1 Ancient, naval) vs naval frontier 6 Contemporary -> x0.15 = x0.6   [applied x0.6]
```

**Era changes re-scale live.** Thanks to the ratio engine [above](#how-it-works), a new era just sets a new target
and the geometry is multiplied by the difference — the ship changes size in place within the poll interval, no
reload and no compounding:

```
[Resize] ERA CHANGED 4 -> 5 — scaled units re-anchor live (an era-1 unit now renders x0.2)
```

> **Verified in-game 2026-07-29** with the crude predecessor of this system (`scale / era`, before the grid): a
> `Era1_Common_Biremes_01 ×4` rule rendered the bireme at **×0.8** in a global-era-5 game — a toy beside a custom
> Stealth Cruiser, where the same rule showed an epic full-size trireme in era 1. The grid replaces the arithmetic
> with authored numbers; the mechanism it drives is the one that produced that result. Watching a *live* shrink
> during an actual era transition is still untested.

### Possible refinement: enter real dimensions

The registry reserves `trueSize` (metres) per rule for a further step: a per-era **reference size** ("what reads as
normal in this age") would let the plugin compute `scale = trueSize / reference(era)`, so units could be entered by
their real dimensions — a 30 m trireme, a 270 m battleship — instead of by factors. The grid is the more direct
control and works today; this would be the convenience layer on top.
