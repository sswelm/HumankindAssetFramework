# HAF documentation

**HAF gives a Humankind unit a genuinely custom 3D model** — a ship, vehicle, creature, or mech, animated and textured —
not just a reskin of the game's existing (human) units, which is where modding stopped before. It also handles districts,
pawn props, projectiles, textures, sounds, formations, and unit scaling, all from a JSON pack registry. You **supply the
model** (a licensed download, a commission, or your own build); HAF bakes and injects it, coexisting with other mods'
asset packs. Within engine bounds (rotation-only animation, a shared GPU budget).

Everything HAF documents is below, grouped by what you're trying to do. New here? This index is the map; the
[project overview and feature list](https://github.com/sswelm/HumankindAssetFramework#readme) live on the repo home,
then come back here to go deep.

---

## Get started
- [**Getting-Started.md**](Getting-Started.md) — **new here? start here.** The ordered path from nothing to a custom unit on the map (bake → build & deploy → launch & verify), with each step linked to its deep doc.
- [**Building.md**](Building.md) — build the plugin and set up the Blender dependency.
- [**Backup.md**](Backup.md) — snapshot and restore the un-versioned assets (source & baked models, tooling, runtime config).

## Author content
The core authoring workflows — bake a model, animate it, texture it, add sound.
- [**Editor-Tools.md**](Editor-Tools.md) — the **editor reference**: every window under `Tools ▸ HAF`, its menu path, what it does, and which registry it writes. Start here to find the right tool.
- [**Factory-Manual.md**](Factory-Manual.md) — the main guide. Every field, the static and animated workflows, and a troubleshooting table. **Start here to add a model.**
- [**Animated-Models.md**](Animated-Models.md) — can HAF import *your* model? The plain-language answer in three levels (clean rigs → rigid-part machines → full character rigs). **Start here for animation.**
- [**Donor-Clip-Flight.md**](Donor-Clip-Flight.md) — play the **donor's own animation on your custom rig** (`useDonorClip`): the measured engine contract (channels-by-index, rest∘delta, axle frames), the workflow, and the failure catalog. Proven on the helicopter.
- [**Turn-Ease.md**](Turn-Ease.md) — **turn first, aim true, fire second**: eased facing per unit TYPE (human/land/turret/hover/ship) or per unit/model, the attack hold that keeps every effect of the shot on one clock, true-bearing aim, **battle hull-aim** for turretless vehicles, distance-proportional **gun elevation**, the gun-local muzzle dial, and post-shot facing that settles toward the shot. Includes the choreography-seam map + graveyard for maintainers.
- [**Textures.md**](Textures.md) — the atlas pipeline: every knob, the complete failure catalog, and runtime re-skins.
- **Unit & creature audio** — engine/movement sounds, custom WAVs (the Sound Studio), and creature voices live in [Factory-Manual.md](Factory-Manual.md) §13–14.
- [**Game-Sound-Lab.md**](Game-Sound-Lab.md) — game-*wide* sound overrides: silence or replace any vanilla Wwise event (music, UI, ambience) by name, with in-game F8 audition.

### The injection axes
Each axis adds custom content in a different place, from the same JSON registry.
- [**District-Visuals.md**](District-Visuals.md) — a custom building on a single district tile, with its own texture, auto-leveled (the District Factory).
- [**District-Dedicated-Visual.md**](District-Dedicated-Visual.md) — a district's own **strategic-map footprint**: the real 3D building at strategic zoom (B&W + flattened), migrating a district onto the scoped render path, multi-district coexistence, and composed/alpha-cutout foliage.
- [**Pawn-Props.md**](Pawn-Props.md) — weapons and gear on a pawn's attachment slots (the Prop Lab).
- [**Projectiles.md**](Projectiles.md) — a custom model as a unit's fired munition (the Projectile Lab).
- [**Formations.md**](Formations.md) — how many models a unit fields and how they're arranged (data only, no bake).
- [**Unit-Size.md**](Unit-Size.md) — resize any unit, vanilla included, incl. era-based scaling (data only, no bake).

## Ship a pack
- [**Multi-Mod.md**](Multi-Mod.md) — the HAF pack format, the `haf_packs/` drop folder, how packs merge and conflict, and the load report. Read this to add assets **without touching ENC**. Template: [haf-pack.example.json](haf-pack.example.json).

## Understand the internals
How the runtime actually works, for anyone extending the plugin.
- [**Code-Map.md**](Code-Map.md) — where everything lives in the plugin source.
- [**Animated-Runtime.md**](Animated-Runtime.md) — the decompiled animation runtime: clip registration, the per-session re-arm (why `AnimationLoad` fires once per process and how HAF re-registers on `PawnManager.Load` / `Sandbox.Load` for save-loads and New Games), the per-frame pose hook, the GPU pose math, and the engine contracts (rotation-only, scale-1, name-ordered bones).
- [**Unit-Combat-Behavior.md**](Unit-Combat-Behavior.md) — how the game drives combat animation and how HAF hooks it.
- [**Firing-On-Attack.md**](Firing-On-Attack.md) — the one-shot fire trigger (Humankind's `SimulationEvent` bus).
- [**Facing-Persistence.md**](Facing-Persistence.md) — how unit facing survives save/load.
- [**Vertex-Budget.md**](Vertex-Budget.md) — the shared GPU mesh-buffer ceiling and how to budget by model *type*.
- [**Capabilities.md**](Capabilities.md) — the full capability list and known limitations, in reference form.
- [**Animation-Pitfalls.md**](Animation-Pitfalls.md) — the hard-won post-mortems: what broke, why, and the fix.

## Project & roadmap
Development-facing docs — status, review, testing, and the wider ecosystem.
- [**Decisions.md**](Decisions.md) — the **ADR log**: settled decisions and the *why* behind them (pack ordering, reflection strategy, the Factory/Lab ownership split, rotation-only animation, …). Check here before proposing a change to any of them.
- [**Framework-Review.md**](Framework-Review.md) — verified code-review findings (prioritized) and the hardening order.
- [**Review-Backlog.md**](Review-Backlog.md) — the open review backlog.
- [**Testing.md**](Testing.md) — the testing strategy: what's unit-tested, what's covered by in-editor instruments, and why.
- [**Ecosystem-Survey.md**](Ecosystem-Survey.md) — every Humankind BepInEx plugin on GitHub and the techniques worth borrowing.
- [**Wonder-Spike.md**](Wonder-Spike.md) — the wonder research spike: a custom model on a player-authored **Artificial Wonder** (proven recipe, the falsified native-affinity path, the session-lifecycle bugs it surfaced, open items).
- [**Pack-Validator-Design.md**](Pack-Validator-Design.md) — design note for the planned pack pre-flight validator (author-facing content validation); designed, not built.
- [**Headless-CLI.md**](Headless-CLI.md) — the **headless CLI**: run model re-bake and the full Humankind mod **build + deploy** from the command line (Unity batch mode), so scripts / CI / an AI agent can drive HAF without the editor GUI. Built + verified.
- [**Headless-CLI-Design.md**](Headless-CLI-Design.md) — the original design note behind it (superseded by the reference above).
- [**Audit-2026-07-31.md**](Audit-2026-07-31.md) — a point-in-time project audit.

---

**For AI agents** — the machine-readable map is
[`llms.txt`](https://raw.githubusercontent.com/sswelm/HumankindAssetFramework/master/llms.txt) (public raw Markdown, no
auth, fully crawlable), or the browsable site at <https://sswelm.github.io/HumankindAssetFramework/>.

**Project history** — the dated milestone log (what was proven when, and the war stories behind it) lives in
[../CHANGELOG.md](../CHANGELOG.md).
