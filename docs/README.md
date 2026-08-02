# HAF documentation

Everything HAF documents, grouped by what you're trying to do. New here? This index is the map; the
[project overview and feature list](https://github.com/sswelm/HumankindAssetFramework#readme) live on the repo home,
then come back here to go deep.

---

## Get started
- [**Building.md**](Building.md) — build the plugin and set up the Blender dependency.
- [**Backup.md**](Backup.md) — snapshot and restore the un-versioned assets (source & baked models, tooling, runtime config).

## Author content
The core authoring workflows — bake a model, animate it, texture it, add sound.
- [**Factory-Manual.md**](Factory-Manual.md) — the main guide. Every field, the static and animated workflows, and a troubleshooting table. **Start here to add a model.**
- [**Animated-Models.md**](Animated-Models.md) — can HAF import *your* model? The plain-language answer in three levels (clean rigs → rigid-part machines → full character rigs). **Start here for animation.**
- [**Textures.md**](Textures.md) — the atlas pipeline: every knob, the complete failure catalog, and runtime re-skins.
- **Unit & creature audio** — engine/movement sounds, custom WAVs (the Sound Studio), and creature voices live in [Factory-Manual.md](Factory-Manual.md) §13–14.
- [**Game-Sound-Lab.md**](Game-Sound-Lab.md) — game-*wide* sound overrides: silence or replace any vanilla Wwise event (music, UI, ambience) by name, with in-game F8 audition.

### The injection axes
Each axis adds custom content in a different place, from the same JSON registry.
- [**District-Visuals.md**](District-Visuals.md) — a custom building on a single district tile (the District Factory).
- [**Pawn-Props.md**](Pawn-Props.md) — weapons and gear on a pawn's attachment slots (the Prop Lab).
- [**Projectiles.md**](Projectiles.md) — a custom model as a unit's fired munition (the Projectile Lab).
- [**Formations.md**](Formations.md) — how many models a unit fields and how they're arranged (data only, no bake).
- [**Unit-Size.md**](Unit-Size.md) — resize any unit, vanilla included, incl. era-based scaling (data only, no bake).

## Ship a pack
- [**Multi-Mod.md**](Multi-Mod.md) — the HAF pack format, the `haf_packs/` drop folder, how packs merge and conflict, and the load report. Read this to add assets **without touching ENC**. Template: [haf-pack.example.json](haf-pack.example.json).

## Understand the internals
How the runtime actually works, for anyone extending the plugin.
- [**Code-Map.md**](Code-Map.md) — where everything lives in the plugin source.
- [**Animated-Runtime.md**](Animated-Runtime.md) — the decompiled animation runtime: clip registration, the per-frame pose hook, the GPU pose math, and the engine contracts (rotation-only, scale-1, name-ordered bones).
- [**Unit-Combat-Behavior.md**](Unit-Combat-Behavior.md) — how the game drives combat animation and how HAF hooks it.
- [**Firing-On-Attack.md**](Firing-On-Attack.md) — the one-shot fire trigger (Humankind's `SimulationEvent` bus).
- [**Facing-Persistence.md**](Facing-Persistence.md) — how unit facing survives save/load.
- [**Vertex-Budget.md**](Vertex-Budget.md) — the shared GPU mesh-buffer ceiling and how to budget by model *type*.
- [**Capabilities.md**](Capabilities.md) — the full capability list and known limitations, in reference form.
- [**Animation-Pitfalls.md**](Animation-Pitfalls.md) — the hard-won post-mortems: what broke, why, and the fix.

## Project & roadmap
Development-facing docs — status, review, testing, and the wider ecosystem.
- [**Framework-Review.md**](Framework-Review.md) — verified code-review findings (prioritized) and the hardening order.
- [**Review-Backlog.md**](Review-Backlog.md) — the open review backlog.
- [**Testing.md**](Testing.md) — the testing strategy: what's unit-tested, what's covered by in-editor instruments, and why.
- [**Ecosystem-Survey.md**](Ecosystem-Survey.md) — every Humankind BepInEx plugin on GitHub and the techniques worth borrowing.
- [**Pack-Validator-Design.md**](Pack-Validator-Design.md) — design note for the planned pack pre-flight validator (author-facing content validation); designed, not built.
- [**Audit-2026-07-31.md**](Audit-2026-07-31.md) — a point-in-time project audit.

---

**For AI agents** — the machine-readable map is
[`llms.txt`](https://raw.githubusercontent.com/sswelm/HumankindAssetFramework/master/llms.txt) (public raw Markdown, no
auth, fully crawlable), or the browsable site at <https://sswelm.github.io/HumankindAssetFramework/>.

**Project history** — the dated milestone log (what was proven when, and the war stories behind it) lives in
[../CHANGELOG.md](../CHANGELOG.md).
