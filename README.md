# Humankind Asset Framework (HAF)

**Give any Humankind unit — or district, pawn prop, or projectile — your own 3D model, texture, and sound. No
executable patching, no per-model code.** *(Formerly **ENC Access Proof**.)*

HAF augments [Humankind](https://www.games2gether.com/amplitude-studios/humankind) with custom assets. You **bake**
an ordinary model (`.glb` / `.fbx` / `.obj` / `.blend`) in the **HAF Authoring Tools** — a suite of Unity editor
windows: the **Model Factory** (the model itself), the **Animation Lab** (clips + behaviors), the **District
Factory**, **Prop Lab**, **Projectile Lab**, and **Unit Retexture / Sound Studio** — and a data-driven BepInEx plugin
**injects** the result onto the live game: correct geometry, correct texture, **its own animation**, and movement
audio, all driven by a JSON registry. Adding a model is just baking it — there is no code to write per model.

**What HAF changes.** Until now, giving a Humankind unit a different look meant reskinning the existing (human) models,
swapping their gear, or remapping a unit onto a **donor** unit — always a variation on something the game already ships.
HAF lets a unit be a **genuinely custom model** — a ship, vehicle, creature, or mech, animated and textured — within the
engine's bounds (rotation-only animation, a shared GPU vertex budget). One honest note for authors: **you supply the
model.** HAF is the *pipeline*, not an art library — bring a licensed download, a commission, or your own build, and
baking + injecting it (correct orientation, texture, animation, isolation) is HAF's job.

**It's multi-mod by design.** The runtime merges asset **packs** from many mods at once, so any modder ships their own
config + assets and joins *without editing anyone else's files*. **ENC** is the reference pack (a set of modern-era
units); a stranger's pack loads and merges right alongside it, with conflicts detected and reported. The aim is to make a
custom Humankind unit something **anyone willing to take some effort** can build. See [**Multi-Mod.md**](docs/Multi-Mod.md).

Custom units ride the game's own GPU-instanced renderer, so **repeated instances are cheap** — the mesh-buffer/asset
cost is per distinct model *type*, not per unit on screen (each visible instance still carries the game's normal
render/cull cost).

> **Two halves, one contract.** The **HAF Authoring Tools** (bake, in the Unity editor) and a **runtime plugin**
> (inject, in the game) talk only through a small JSON pack registry — so the tooling and the injector stay fully
> decoupled, and the registry is the public API other mods build against. *("Model Factory" names one window of the
> suite — the historical first one; in-editor the whole suite lives under `Tools ▸ HAF`.)*

## The six axes

HAF adds custom content in six places. Four inject custom **assets**; two retune units the game already ships, with no
bake and nothing to undo but a deleted line. Each is proven in-game with a shipped example.

| Axis | What it does | Assets? | Deep dive |
|---|---|---|---|
| **Units** | Replace a unit's whole 3D model — static or **animated**, with per-model runtime behaviors | bake | [Factory-Manual](docs/Factory-Manual.md) · [Animated-Models](docs/Animated-Models.md) |
| **Districts** | A district's on-map building — own model, **texture**, and its own **strategic-map footprint** (the real 3D building, B&W + flattened when zoomed out), on **every tile it's built** (others untouched), auto-leveled | bake | [District-Visuals](docs/District-Visuals.md) · [District-Dedicated-Visual](docs/District-Dedicated-Visual.md) |
| **Wonders** | A player-authored **Artificial Wonder** with a custom model, rendered through the game's **native wonder pipeline** (no donor district) | bake | [Wonder-Spike](docs/Wonder-Spike.md) |
| **Pawn props** | Weapons & gear on a pawn's **attachment slots** — no whole-model replacement | bake | [Pawn-Props](docs/Pawn-Props.md) |
| **Projectiles** | The **munition mesh** a unit fires | bake | [Projectiles](docs/Projectiles.md) |
| **Formations** | **How many** models a unit fields and **how they're arranged** | data only | [Formations](docs/Formations.md) |
| **Unit size** | **How big** any unit renders (vanilla included), incl. era-based scaling | data only | [Unit-Size](docs/Unit-Size.md) |

Formations + unit size together cover both halves of R.E.D.-style rebalancing — count and scale.

## Features

**Animation, audio, and retexturing are cross-cutting** — a unit model can carry its own baked animation, engine or
custom-WAV movement sound, and a runtime-hot-loaded skin or tint, all from the same JSON registry, no code.

**Custom unit models**
- Static or animated model replacement from **GLB / glTF / OBJ / FBX / `.blend`**, correctly oriented, textured, and
  placed (at the waterline for ships). Shipped examples include a zeppelin, an LCAC hovercraft, a USS Zumwalt cruiser,
  and a RAH-66 Comanche.
- **Heavy, single-sided, and multi-material meshes handled** — a built-in vertex reducer, a winding fix + double-sided
  fallback for CAD "sketch" meshes, height-based UVs, and an N-material atlas packer.
- **Strip parts at bake time** — delete named objects (and children) from your source mesh before baking.

**Custom districts**
- **A district's building is your own model + texture**, on every tile it's built (the rest of the map's districts
  untouched) — auto-leveled onto the tile, with per-entry ground paint and a raised platform.
- **Its own strategic-map footprint** — zoomed out, the district shows **its actual 3D building** as the footprint
  instead of a generic decal, optionally rendered **black-and-white** and **flattened to a sheet** to sit in the
  schematic map (the fade between close-up and strategic is a per-element GPU render-feature gate, not a camera swap).
  Authored per-district in the **District Factory** (one *Mesh footprint* toggle).
- **Composed "pizza" districts** — merge several source models (a temple + a grove of trees) into one district mesh and
  a single atlas, with **alpha-cutout foliage** that renders correctly at both zooms.
- **Any district migrates onto the modern (scoped) render path with one Bake** — the District Factory bakes a
  data-authored selector for it — and **multiple custom districts render fully independently** side by side.
  See [District-Dedicated-Visual](docs/District-Dedicated-Visual.md).

**Animation**
- **A model plays its own baked animation** — tick *Animated*, press *Bake*. A drone spins its own propellers; the M114
  howitzer plays the animator's full recoil cycle (real bone translation, not rotation-only).
- **State-driven characters** — a rigged character **idles standing, runs while moving**, holds a **combat stance** in
  battle, plays an **after-move settle**, and fires its **attack animation** when it shoots. Proven with a full 62-bone
  humanoid soldier; five clips per model, switched live by the runtime.
- **Fire-on-attack** — a baked clip plays once, on the unit's combat action (e.g. a howitzer barrel that elevates only
  when it bombards), via Humankind's own combat event bus.
- **Turret aim** — a turret yaws to track its target by retargeting the engine's own aim slot (no per-frame trig).
- **A static model becomes a moving vehicle — no Blender knowledge** — the **Vehicle Lab** probes a raw model into
  parts; mark what moves (**wheels**, **caterpillar tracks**, turret + gun, or a helicopter's **main + tail rotor**)
  and it generates the rigged, spin-animated source the bake consumes. Its probe also classifies **interior parts**
  (escape-ray sampling: provably-never-visible cockpit gear, engine guts) so hidden geometry is stripped before it
  costs shared GPU vertex budget — 28% of the test helicopter's vertices.
- **Borrow or freeze the donor's motion** — strip your model's rotor and the donor's spinning rotor shows through; or
  *Freeze donor animation* to pin a rigid model still while it glides tile-to-tile.
- **Fly the donor's animation on YOUR rig** — *Use donor animation clip* plays the donor's complete original motion
  (a helicopter's hover bob, main rotor, canted tail fan) natively on your baked skeleton: the plugin rebases the rig
  to the donor's rest conventions and the Vehicle Lab authors rotor bones as axle frames, so the clip channels land
  exactly on your parts. Plus **eased, banked turns** on move orders instead of the engine's facing snap, and
  **terrain hugging** — skim low over open country, climb only for built city districts (all live-tunable).
  The measured engine contract: [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md).
- **Turn first, aim true, fire second** — any unit with a turn rate (HAF models via the Factory, **vanilla units**
  via a Formation Lab link, or whole **categories** — human / land / turret / hover / ship — from one panel)
  **sweeps** to its new heading instead of the engine's instant facing snap, and an attack **waits for the
  pivot**: recoil, muzzle flash, shot sound, smoke and shell all hold on one shared clock and land together —
  aimed at the **true bearing**, not vanilla's hex-quantized angle. [docs/Turn-Ease.md](docs/Turn-Ease.md).
- **Real gunnery, even in battles** — vanilla never turns a vehicle's hull in battle (turret-slot aiming only);
  HAF's **battle hull-aim** lays a turretless vehicle on its actual target, the **gun elevates with range** to a
  configurable max, the muzzle chain fires from the **barrel end** (gun-local offset that rides aim + elevation),
  and after the shot the unit **stays laid**, settling on the nearest clean facing toward where it fired.
  A Turret bone yaws; a Gun bone elevates — configured in the Animation Lab. [docs/Turn-Ease.md](docs/Turn-Ease.md).

**Textures**
- **Correct, isolated skins** out of the box — glTF/OBJ V-convention reconciled, off-tile UVs shifted back in range, and
  each model gets a private layer so its skin never bleeds onto the donor.
- **Bake-time skin tuning** — albedo brightness/saturation, a keep-black toggle for glass canopies, and atlas sizing
  (256–2048, DXT1) that keeps each shipped skin ~0.1–2 MB.
- **Runtime retexture / recolour without a bake** — a hot-loaded PNG or a live desaturate + RGB adjust, per unit, free on
  the vertex budget. Works on baked custom models too.

**Audio**
- **Unit movement audio** — restores the engine sound re-loaded units lose, playing the game's own sound *by name* (F8
  *Dump Sound Catalog* lists all ~845), or **any custom WAV** as a spool-up → loop → spool-down sequence.
- **Creature voices** — silence a borrowed animal donor's Wwise voice and add your own idle growl and attack roar
  (camera-anchored, jittered, one-voice-per-stack).
- **Game-wide sound overrides** — the **Game Sound Lab** silences (and, reserved, replaces) any vanilla Wwise event —
  music, UI, ambience — by name, authored into `haf_sounds.json`, with in-game **F8 audition** to hear an event before
  you target it. Aimed at soundscape-overhaul mods. See [Game-Sound-Lab.md](docs/Game-Sound-Lab.md).

**Multi-mod & tooling**
- **Pack merging** — the runtime merges any number of third-party packs from `haf_packs/`: duplicate-`modId` rejection,
  `dependsOn` validation, load order that follows **Humankind's own mod order**, declared overrides, and a `haf_load_report.txt` of every decision.
- **Guided authoring** — Pick-driven clip/bone/hide fields, an embedded interactive 3D preview, auto-detected game and
  Blender paths, and an in-game **F8** status window (compatibility health, a Smoke Test, the live GPU-budget readout,
  a **per-frame cost meter** — HAF's own µs/frame, bucket by bucket, so its overhead is a number (~3% of a 30 fps frame), not a guess —
  and the audio/texture authoring aids). The Smoke Test runs deep per-entry checks *plus* a live **seam write-back
  self-test**, names uninjected entries with a diagnosis, flags untested coverage instead of staying silently green,
  and writes its verdict to `haf_smoke_report.txt`. Every launch also writes a machine-readable `haf_bindings_report.txt` — a
  diffable list of the reflection bindings — **91 game types, ~250 members**, every non-diagnostic by-name site the
  runtime binds, the structs derived along the path the code walks so there are no name-guess false positives — that
  names any game-update drift in one line, headless-checkable (`tools/check-bindings.sh`, no game launch).
- **Backup & Restore** — a guarded, additive snapshot of everything ENCReload's git doesn't track.
- **Pack validator, four surfaces** — one rule set (~30 content checks: bones, files, pawns, formats, ranges) runs
  pre-bake, on the **Validate pack** button, in the mod build (`-strict` fails CI), and as a boot-time pre-flight that
  explains silent failures in `haf_load_report.txt`. See [Pack-Validator-Design.md](docs/notes/Pack-Validator-Design.md).
- **Ship Status** — "baked ≠ built" made visible: which bakes the game hasn't seen yet, orphaned bakes that ship as
  dead weight, and a guard-snapshotted multi-select cleanup. See [Ship-Status.md](docs/Ship-Status.md).
- **Entry-state coherence** — the form-vs-registry banner (drilled), a bake-time model-file confirm, and a calibrated
  vessel waterline (`waterLevel` pack config + a numeric keel readout in the preview). See
  [Factory-Manual.md](docs/Factory-Manual.md).
- **Combat height offset** (`combatZ`) — a unit rides higher or *lower* while battle-locked, eased both ways: the
  submarine fights submerged (snorkel-only) and resurfaces after the battle. Calibrated numerically via the
  preview's "In combat" toggle; works for static and animated models. Drilled in-game.
- **Headless CLI** — re-bake a model and run the full Humankind mod **build + deploy** from the command line (Unity batch
  mode), reusing the exact code the editor buttons call — so scripts, CI, or an AI agent can drive HAF without the GUI.
  See [Headless-CLI.md](docs/Headless-CLI.md).

**Reliability**
- **Custom content survives every session change** — units and districts re-arm correctly across a **save-load**, an
  **in-session reload**, and starting a **New Game** in the same app run, not just a fresh launch. (The game's animation
  registration loads once per *process*, so HAF re-registers on the universal per-session seam; skipping this used to
  leave a reloaded custom unit skinning against stale GPU slots — torn geometry or the wrong skin.) See
  [Animated-Runtime.md](docs/Animated-Runtime.md).
- **Skins are never destroyed out from under a reload** — the re-arm only frees textures HAF creates, never the shared
  baked atlas asset.
- **No leaked GPU objects** — every runtime clone HAF makes (model isolated layers/skins, hand-prop layers, the
  district private leaves / cloned layers / B&W footprint texture) is freed — on session reset, before a re-inject
  overwrites it, and even if a texture bake throws mid-frame — so a long session with many reloads never accumulates
  orphaned native layers or textures.
- **Unit facing survives save/load** — a HAF side-file restores each unit's heading on load (the game save has none),
  including `respawnAfterLoad` units (helicopters) whose post-load pawn rebuild would otherwise reset it to neutral.

> **Polishing:** moving **caterpillar tracks** (treadize) run in-game, with a remaining idle micro-twitch to smooth out;
> the **death / battle-start war-cry** creature voices are built and awaiting in-game verification. See [CHANGELOG.md](CHANGELOG.md).

**The full story** — how each capability was proven and the war stories behind it — is in [**CHANGELOG.md**](CHANGELOG.md).
The complete capability list and known limitations are in [**Capabilities.md**](docs/Capabilities.md).

## How it works
**Editor — the Model Factory** (*Tools ▸ HAF ▸ Model Factory*, in [ENCReload](https://github.com/sswelm/ENCReload)): pick a target unit + a model file, set
transform / size / shading, **Bake**. Static models bake an Amplitude `Skeleton` on the proven single-bone vehicle rig +
a packed atlas; ticking **Animated** takes a parallel path (`UniversalBaker.BuildAnimated`) that keeps the model's **own
armature + clip** (Blender slims it, then bakes `Skeleton` + `ClipCollection` + atlas, with the clip isolated in a
per-model `anim/` subfolder). `ModelRegistry` writes the pack's `pack.json` into the auto-detected `BepInEx/config/haf_packs/ENCReload/`.

**Runtime — `UniversalInject`** (`Patches/`): one patch, any number of models. Reads the registry, registers each baked
skeleton, and on `AddOn.Load` repoints the matching pawn by **self-discovery** (reads the host's body-mesh name, renames
ours to match); the skin rides a private layer clone. For **animated** models it also injects the `ClipCollection` and,
via a `PawnManager.AddPawnEntry` hook, drives the pawn's pose onto the clip — matched by **pawn descriptor** and
normalizing the skeleton id, so every instance plays it.

*Founding insight (credit: CalmBreakfast): for a static swap, keep the unit's real skeleton and swap only the mesh —
injecting a mismatched skeleton hangs the GPU skinning. The animated work showed the corollary: a **properly baked**
custom `Skeleton` + `ClipCollection` (built through the SDK's own tooling) can be injected and played **without** the
hang — the danger was malformed skeletons, not custom ones per se.*

## Technology stack

| Layer | Technology |
|---|---|
| Runtime plugin (this repo) | **BepInEx 5.4** plugin in C#, targeting **.NET Framework 4.7.1** (the game's Mono runtime); builds with just the .NET SDK (`dotnet build`, no Unity needed) |
| Game patching | **Harmony** (`0Harmony`) runtime patches against the game's **`Amplitude.Mercury`** assemblies — no executable modification |
| Registry parsing | **Newtonsoft.Json** (shipped with the game, via mod.io) — `UnityEngine.JsonUtility` silently returns empty objects under the game's Mono runtime |
| Editor tooling ([ENCReload](https://github.com/sswelm/ENCReload)) | **Unity 2021.3.1f1** (Humankind's own engine version) + the **official Amplitude modding SDK**, which bakes the native `Skeleton` / `ClipCollection` / mesh / atlas assets; editor scripts live only there (the stale `baker/` mirror was deleted 2026-08-21) |
| `glbconv` converter | Standalone C# console app on **.NET 8** (self-contained single-file exe — adopters need no .NET install), built on **SharpGLTF** |
| Model-prep scripts | **Python** run headless inside **Blender** (`blender -b --python …`, `bpy` API) — rigging, decimation, clip extraction |
| Editor ↔ runtime contract | A plain **JSON** registry (the pack's `pack.json`), its shared fields (see [Shared-Schema](docs/Shared-Schema.md) for the exact count) defined once in a **`Haf.Schema`** netstandard2.0 DLL both halves inherit — so the schema can't drift |

## Zero-config adoption
Built to work on a stranger's machine, not just the author's:
- **Auto-detects the game.** No hardcoded paths — the Factory finds Humankind via Steam's library config (with a manual
  override in a Settings panel for odd layouts). Blender is auto-located too.
- **No .NET install needed.** The GLB/glTF converter ships as a self-contained single-file `glbconv.exe` that carries its
  own runtime — adopters don't need a .NET SDK or runtime.
- **One injection path.** A single `UniversalInject` drives every model from the registry, so there's one code path to
  understand and one to trust.
- **Guided, not guessy.** Clip / bone / hide-donor fields are Pick-driven (read from the model + the plugin log); a
  Settings panel shows the detected Blender path with an in-UI override; every feature that needs Blender warns *before* a
  failed bake; and an **embedded interactive 3D preview** shows the baked model right in the window.

## Models & licenses
Model files aren't committed — download each per its license, point the Factory's **Model file** at it, and bake (the
converter extracts it into `Assets/FactorySource/<name>/`, which stays out of the shipped mod). Authors + licenses in
[**CREDITS.md**](CREDITS.md) (CC-BY requires attribution).

## License
All code, scripts, and docs in this repo are **MIT** ([LICENSE](LICENSE)) — fork the plugin, vendor the injection
path, build on it. Two things the MIT grant does *not* cover, because they aren't ours: the game internals the docs
describe (decompiled `Amplitude.*` code remains Amplitude Studios' property; the required game DLLs are gitignored
and must be copied from your own install), and any 3D model you bake (each stays under its own license — see
[CREDITS.md](CREDITS.md)). The ENC mod's own game-data content lives in the
[ENCReload](https://github.com/sswelm/ENCReload) repo and is all-rights-reserved there.

## Config
The plugin reads `<Humankind>\BepInEx\config\haf_packs\ENCReload\pack.json` — ENC's **pack** (one entry per model: pawn description,
skeleton + atlas GUIDs, transform, shading flags; animated entries add `clip` + `animated`/`animClip`/`animateBones`),
wrapped with pack metadata (`schemaVersion`/`modId`). It then merges any additional packs in `BepInEx/config/haf_packs/*.json`
and writes a `haf_load_report.txt` of what loaded. The Factory writes ENC's registry and auto-detects the path; the
field-by-field breakdown is in the [Factory Manual](docs/Factory-Manual.md) and the pack format in [Multi-Mod.md](docs/Multi-Mod.md).
The plugin's own cfg (`…\community.humankind.haf.cfg`) — press **F8** in-game for the status/authoring window
(binding-health banner, Smoke Test, live GPU-budget readout, retexture/sound aids; see
[Editor-Tools.md](docs/Editor-Tools.md#in-game-f8-window-runtime--part-of-the-plugin-not-tools--haf)).

## Documentation

📖 **Docs site:** **<https://sswelm.github.io/HumankindAssetFramework/>** — the full documentation as a browsable static
site, generated from [`/docs`](docs/).

> **For AI agents:** the machine-readable entry point is [**`llms.txt`**](llms.txt) — fetch it raw at
> `https://raw.githubusercontent.com/sswelm/HumankindAssetFramework/master/llms.txt`. It maps the whole doc set with
> links that resolve to public raw Markdown, so an agent can crawl everything with no authentication.

The full documentation set, grouped by task (get started · author · ship a pack · internals · project), is indexed in
[**docs/README.md**](docs/README.md). The fastest starting points:

- **New here?** [Getting-Started.md](docs/Getting-Started.md) — the ordered path from nothing to a custom unit on the map (bake → build & deploy → launch & verify).
- **Find the right tool:** [Editor-Tools.md](docs/Editor-Tools.md) — every authoring window under `Tools ▸ HAF`, what it does, and what it writes.
- **Add a model:** [Factory-Manual.md](docs/Factory-Manual.md) — every field, the static + animated workflows, troubleshooting.
- **Animate a model:** [Animated-Models.md](docs/Animated-Models.md) — can HAF import *your* model? The plain-language answer.
- **Ship your own pack:** [Multi-Mod.md](docs/Multi-Mod.md) — the pack format and the `haf_packs/` drop folder.
- **Everything it does:** [Capabilities.md](docs/Capabilities.md) — the full capability list + known limitations.
- **Project history:** [CHANGELOG.md](CHANGELOG.md) — the dated milestone log and the war stories.

## Status & roadmap
HAF is a **working, in-game-proven framework**. All six axes ship with a verified example, the runtime merges packs from
multiple authors, and the ENC reference pack drives a roster of modern-era units. The plugin is stable, has a unit-test
suite over its pure-data layer plus an in-game smoke harness, and a full documentation set.

The remaining work is **productizing the authoring tools for third-party distribution** — the framework itself is done;
this is packaging:
- **Neutral naming** — drop the "ENC" prefix (→ `HumankindModelFactory`) across the editor tooling.
- **Package scaffolding** — a Unity `package.json` / asmdef and single-DLL plugin packaging.
- **An install guide + quickstart** for adopters bringing their own models.

Already in place toward that goal: zero-config path auto-detection, the self-contained converter (no .NET dependency), one
consolidated injection path, full multi-material GLB support, one-click animated import, bake-time skin controls,
configurable atlas sizing + bundle slimming, the multi-mod pack loader, and an MIT license on all code.
