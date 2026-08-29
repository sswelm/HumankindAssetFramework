# HAF documentation

**HAF gives a Humankind unit a genuinely custom 3D model** — a ship, vehicle, creature, or mech, animated and textured —
not just a reskin of the game's existing (human) units, which is where modding stopped before. It also handles districts,
pawn props, projectiles, textures, sounds, formations, and unit scaling, all from a JSON pack registry. You **supply the
model** (a licensed download, a commission, or your own build); HAF bakes and injects it, coexisting with other mods'
asset packs. Within engine bounds (rotation-only animation, a shared GPU budget).

The [project overview and feature list](https://github.com/sswelm/HumankindAssetFramework#readme) live on the repo home.
This page is the map of the docs themselves.

---

## Start from what you're trying to do

| I want to… | Go to |
|---|---|
| **Put my first model on a unit** | [**Getting-Started.md**](Getting-Started.md) — the ordered path, bake → build → launch |
| **Diagnose something that is broken** | [**Troubleshooting.md**](Troubleshooting.md) — start from the symptom and follow the evidence to its owning guide |
| **Look up a Factory field, or fix a bad bake** | [Factory-Manual.md](Factory-Manual.md) — the main guide, every field + troubleshooting |
| **Find the right authoring window** | [Editor-Tools.md](Editor-Tools.md) — every window under `Tools ▸ HAF`, and what each one writes |
| **Make my model move** | [Animated-Models.md](Animated-Models.md) — can HAF import *your* rig? |
| **Understand why my animation looks wrong** | [Animation-Pitfalls.md](Animation-Pitfalls.md) — the failure catalogue |
| **Ship my own pack, without touching ENC** | [Multi-Mod.md](Multi-Mod.md) — the pack format + the `haf_packs/` drop folder |
| **Know if HAF can do X at all** | [Capabilities.md](Capabilities.md) — the full list, with known limitations |
| **Build or extend the plugin** | [Building.md](Building.md) → [Code-Map.md](Code-Map.md) |
| **Automate HAF from a script or CI** | [Headless-CLI.md](Headless-CLI.md) |

---


## The pages

### Get started
- [**Installation.md**](Installation.md) — install BepInEx, the runtime plugin, and the Unity authoring package; verify each layer.
- [**Getting-Started.md**](Getting-Started.md) — **new here? start here.** Nothing → a custom unit on the map, each step linked to its deep doc.
- [**Troubleshooting.md**](Troubleshooting.md) — **something failed? start with the symptom.** Evidence order and direct routes to each maintained failure catalog.
- [**Building.md**](Building.md) — build the plugin; the Blender dependency.
- [**Backup.md**](Backup.md) — the four-layer safety net for the un-versioned working set: manual versions, daily auto-versions, an undoable delete guard, offsite zips.

### Author content
- [**Editor-Tools.md**](Editor-Tools.md) — the **editor reference**: every window under `Tools ▸ HAF`, its menu path, and which registry it writes. *Start here to find the right tool.*
- [**Factory-Manual.md**](Factory-Manual.md) — the main guide: every field, the static + animated workflows, the troubleshooting table. *Start here to add a model.*
- [**Ship-Status.md**](Ship-Status.md) — baked ≠ built. Which bakes the game hasn't seen yet, plus guarded delete of stale/orphaned output.
- [**Textures.md**](Textures.md) — the atlas pipeline: every knob, the complete failure catalogue, runtime re-skins.
- [**Game-Sound-Lab.md**](Game-Sound-Lab.md) — game-*wide* audio overrides: silence or replace any vanilla Wwise event, with in-game F8 audition.
- *Unit & creature audio* (engine sounds, custom WAVs, creature voices) lives in [Factory-Manual.md](Factory-Manual.md) §13–14.

#### Animation — four pages, in reading order
1. [**Animated-Models.md**](Animated-Models.md) — *can HAF import my model?* The plain-language answer in three levels (clean rigs → rigid-part machines → full character rigs). **Start here.**
2. [**Animation-Pitfalls.md**](Animation-Pitfalls.md) — it baked but looks wrong: the field guide to every trap, each hit for real.
3. [**Donor-Clip-Flight.md**](Donor-Clip-Flight.md) — play the **donor's own animation on your rig** (`useDonorClip`): the measured engine contract and the failure catalogue. Proven on the helicopter.
4. [**Turn-Ease.md**](Turn-Ease.md) — turn first, aim true, fire second: eased facing per unit or category, the attack hold, true-bearing aim, gun elevation.

*(Extending the plugin rather than authoring? The engine-side companion is [Animated-Runtime.md](Animated-Runtime.md), under Internals.)*

### The injection axes
Each axis adds custom content in a different place, from the same JSON registry.

| Axis | Page | Bake? |
|---|---|---|
| **Units** | the Factory pages above | yes |
| **Districts** | [District-Visuals.md](District-Visuals.md) — a custom building on a district tile, auto-leveled. **Start here.** | yes |
| **Districts, strategic zoom** | [District-Dedicated-Visual.md](District-Dedicated-Visual.md) — …plus its own strategic-map footprint, the scoped render path, multi-district coexistence. | yes |
| **Wonders** | [Wonder-Spike.md](Wonder-Spike.md) — a custom model on a player-authored Artificial Wonder, through the game's native wonder pipeline. | yes |
| **Pawn props** | [Pawn-Props.md](Pawn-Props.md) — weapons and gear on a pawn's attachment slots. | yes |
| **Projectiles** | [Projectiles.md](Projectiles.md) — a custom model as a unit's fired munition. | yes |
| **Formations** | [Formations.md](Formations.md) — how many models a unit fields, and their layout. | data only |
| **Unit size** | [Unit-Size.md](Unit-Size.md) — how big any unit renders, vanilla included, incl. era scaling. | data only |

### Ship a pack
- [**Multi-Mod.md**](Multi-Mod.md) — the pack format, the `haf_packs/` drop folder, how packs merge and conflict, and the load report. Read this to add assets **without touching ENC**. Template: [haf-pack.example.json](haf-pack.example.json).

### Internals — for anyone extending the plugin
- [**Performance.md**](Performance.md) — what HAF costs per frame (the F8 meter, today's baseline by bucket, the rules that keep it there, what to do when a number grows). Measured, not estimated.
- [**Architecture.md**](Architecture.md) — **read this before changing the runtime.** The invariants no compiler enforces — threads, session re-arm order, the reflection contract, the two district ledgers — each with the failure it was learned from.
- [**Code-Map.md**](Code-Map.md) — where everything lives in the plugin source.
- [**Shared-Schema.md**](Shared-Schema.md) — the `Haf.Schema` library both halves inherit: what's shared, what's divergent, how to add a field. *(Owns the field count.)*
- [**Animated-Runtime.md**](Animated-Runtime.md) — the decompiled animation runtime: clip registration, the per-session re-arm, the per-frame pose hook, the GPU pose math, the engine contracts.
- [**Unit-Combat-Behavior.md**](Unit-Combat-Behavior.md) — how the game drives combat animation, and what's data-driven vs hardcoded.
- [**Firing-On-Attack.md**](Firing-On-Attack.md) — the one-shot fire trigger, off Humankind's `SimulationEvent` bus.
- [**Facing-Persistence.md**](Facing-Persistence.md) — how unit facing survives save/load, given the save has no facing field.
- [**Vertex-Budget.md**](Vertex-Budget.md) — the shared GPU mesh-buffer ceiling, and how to budget by model *type*.
- [**Capabilities.md**](Capabilities.md) — the full capability list + known limitations, in reference form.
- [**Headless-CLI.md**](Headless-CLI.md) — run re-bake and the full mod build + deploy from the command line, so a script, CI, or an agent can drive HAF without the GUI.

### Project & process — maintainer-facing
- [**Decisions.md**](Decisions.md) — the **ADR log**: settled decisions and the *why*. Check here before proposing a change to any of them.
- [**Testing.md**](Testing.md) — the testing strategy: what's unit-tested, what's covered by in-editor instruments, and what deliberately isn't.
- [**Framework-Review.md**](Framework-Review.md) — the living hardening roadmap: verified review findings, prioritized, with what was done when.
- [**Review-Backlog.md**](Review-Backlog.md) — findings verified real and deliberately deferred, ranked by when they'll bite.

---

## HAF in the wild

**[What HAF brings to ENCReload](https://sswelm.github.io/ENCReload/HAF-Effects.html)** — the first mod built on HAF,
and the most useful thing to read after this index if you want to know what the framework actually *looks like* in a
shipped game rather than in a feature list. It inventories 22 configured units and the type-wide dials behind them:
custom models, state-driven animation, traversing turrets and distance-proportional gun elevation, recoil, gradual
turning and pivot-in-place, terrain hugging, formations, sound. Most of HAF's harder capabilities exist because
something in that list needed them.

## 📁 Archived notes

Older design notes and investigations live in [`docs/notes/`](notes/) — frozen, not maintained, not instructions; each opens by
naming the page that replaced it. They are kept for the reasoning, not for reading first.

---

**For AI agents** — the machine-readable map is
[`llms.txt`](https://raw.githubusercontent.com/sswelm/HumankindAssetFramework/master/llms.txt) (public raw Markdown, no
auth, fully crawlable), or the browsable site at <https://sswelm.github.io/HumankindAssetFramework/>.

**Project history** — the dated milestone log (what was proven when, and the war stories behind it) is the
[CHANGELOG](https://github.com/sswelm/HumankindAssetFramework/blob/master/CHANGELOG.md).
