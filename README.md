# Humankind Asset Framework (HAF)

**Give any Humankind unit — or district, pawn prop, or projectile — your own 3D model, texture, and sound. No
executable patching, no per-model code.** *(Formerly **ENC Access Proof**.)*

HAF augments [Humankind](https://www.games2gether.com/amplitude-studios/humankind) with custom assets. You **bake**
an ordinary model (`.glb` / `.fbx` / `.obj` / `.blend`) in the **HAF Authoring Tools** — a suite of Unity editor
windows: the **Model Factory** (the model itself), the **Animation Lab** (clips + behaviors), the **District
Factory**, **Prop Lab**, **Projectile Lab**, and **Unit Retexture / Sound Studio** — and a data-driven BepInEx plugin
**injects** the result onto the live game: correct geometry, correct texture, **its own animation**, and movement
audio, all driven by a JSON registry. Adding a model is just baking it — there is no code to write per model.

**It's multi-mod by design.** The runtime merges asset **packs** from many mods at once, so any modder ships their own
config + assets and joins *without editing anyone else's files*. **ENC** is the reference pack (a set of modern-era
units); a stranger's pack loads and merges right alongside it, with conflicts detected and reported. The aim is to make a
custom Humankind unit something **anyone willing to take some effort** can build. See [**Multi-Mod.md**](docs/Multi-Mod.md).

Custom units ride the game's own GPU-instanced renderer, so **instances are free** — the cost is the number of distinct
model *types* loaded, not units on screen.

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
| **Districts** | A district's on-map building, scoped to **one tile** | bake | [District-Visuals](docs/District-Visuals.md) |
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

**Animation**
- **A model plays its own baked animation** — tick *Animated*, press *Bake*. A drone spins its own propellers; the M114
  howitzer plays the animator's full recoil cycle (real bone translation, not rotation-only).
- **State-driven characters** — a rigged character **idles standing, runs while moving**, holds a **combat stance** in
  battle, plays an **after-move settle**, and fires its **attack animation** when it shoots. Proven with a full 62-bone
  humanoid soldier; five clips per model, switched live by the runtime.
- **Fire-on-attack** — a baked clip plays once, on the unit's combat action (e.g. a howitzer barrel that elevates only
  when it bombards), via Humankind's own combat event bus.
- **Turret aim** — a turret yaws to track its target by retargeting the engine's own aim slot (no per-frame trig).
- **Borrow or freeze the donor's motion** — strip your model's rotor and the donor's spinning rotor shows through; or
  *Freeze donor animation* to pin a rigid model still while it glides tile-to-tile.

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
  music, UI, ambience — by name, authored into `enc_sounds.json`, with in-game **F8 audition** to hear an event before
  you target it. Aimed at soundscape-overhaul mods. See [Game-Sound-Lab.md](docs/Game-Sound-Lab.md).

**Multi-mod & tooling**
- **Pack merging** — the runtime merges any number of third-party packs from `haf_packs/`: duplicate-`modId` rejection,
  `dependsOn` validation, topological load order, declared overrides, and a `haf_load_report.txt` of every decision.
- **Guided authoring** — Pick-driven clip/bone/hide fields, an embedded interactive 3D preview, auto-detected game and
  Blender paths, and an in-game **F8** scan/feedback window.
- **Backup & Restore** — a guarded, additive snapshot of everything ENCReload's git doesn't track.

> **Polishing:** moving **caterpillar tracks** (treadize) run in-game, with a remaining idle micro-twitch to smooth out;
> the **death / battle-start war-cry** creature voices are built and awaiting in-game verification. See [CHANGELOG.md](CHANGELOG.md).

**The full story** — how each capability was proven and the war stories behind it — is in [**CHANGELOG.md**](CHANGELOG.md).
The complete capability list and known limitations are in [**Capabilities.md**](docs/Capabilities.md).

## How it works
**Editor — the Model Factory** (`baker/`, *Tools ▸ HAF ▸ Model Factory*): pick a target unit + a model file, set
transform / size / shading, **Bake**. Static models bake an Amplitude `Skeleton` on the proven single-bone vehicle rig +
a packed atlas; ticking **Animated** takes a parallel path (`UniversalBaker.BuildAnimated`) that keeps the model's **own
armature + clip** (Blender slims it, then bakes `Skeleton` + `ClipCollection` + atlas, with the clip isolated in a
per-model `anim/` subfolder). `ModelRegistry` writes `enc_models.json` into the auto-detected `BepInEx/config`.

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
| Editor tooling ([ENCReload](https://github.com/sswelm/ENCReload)) | **Unity 2021.3.1f1** (Humankind's own engine version) + the **official Amplitude modding SDK**, which bakes the native `Skeleton` / `ClipCollection` / mesh / atlas assets; editor scripts mirrored here in `baker/` |
| `glbconv` converter | Standalone C# console app on **.NET 8** (self-contained single-file exe — adopters need no .NET install), built on **SharpGLTF** |
| Model-prep scripts | **Python** run headless inside **Blender** (`blender -b --python …`, `bpy` API) — rigging, decimation, clip extraction |
| Editor ↔ runtime contract | A plain **JSON** registry (`enc_models.json`) — the only thing the two halves share |

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
The plugin reads `<Humankind>\BepInEx\config\enc_models.json` — ENC's base **pack** (one entry per model: pawn description,
skeleton + atlas GUIDs, transform, shading flags; animated entries add `clip` + `animated`/`animClip`/`animateBones`),
wrapped with pack metadata (`schemaVersion`/`modId`). It then merges any additional packs in `BepInEx/config/haf_packs/*.json`
and writes a `haf_load_report.txt` of what loaded. The Factory writes ENC's registry and auto-detects the path; the
field-by-field breakdown is in the [Factory Manual](docs/Factory-Manual.md) and the pack format in [Multi-Mod.md](docs/Multi-Mod.md).
The plugin's own cfg (`…\community.humankind.haf.cfg`) — press **F8** in-game for a scan/feedback window.

## Documentation

📖 **Docs site:** **<https://sswelm.github.io/HumankindAssetFramework/>** — the full documentation as a browsable static
site (also mirrored in the [GitHub Wiki](https://github.com/sswelm/HumankindAssetFramework/wiki)).

> **For AI agents:** the machine-readable entry point is [**`llms.txt`**](llms.txt) — fetch it raw at
> `https://raw.githubusercontent.com/sswelm/HumankindAssetFramework/master/llms.txt`. It maps the whole doc set with
> links that resolve to public raw Markdown, so an agent can crawl everything with no authentication. (The GitHub
> *Wiki* is the human-browsable mirror; point AIs at the raw docs, not the rendered wiki HTML.)

The full documentation set, grouped by task (get started · author · ship a pack · internals · project), is indexed in
[**docs/README.md**](docs/README.md). The fastest starting points:

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
