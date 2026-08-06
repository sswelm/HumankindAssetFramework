# Editor / Authoring Tools reference

The **HAF Authoring Tools** are a suite of Unity editor windows under **`Tools ▸ HAF`**. They are the *bake* half of HAF —
you author custom content here, and the runtime plugin injects it in-game; the two halves talk only through the JSON pack
registry. (The editor scripts live in the [ENCReload](https://github.com/sswelm/ENCReload) repo, under
`Assets/Scripts/Editor/`; a stale snapshot is mirrored in this repo's `baker/`.)

This page is the **map** — every tool, its exact menu path, what it does, and what it writes. For the deep, field-by-field
workflows, follow the *Deep dive* links; this reference deliberately doesn't duplicate them.

## Where the tools write — the registries

All registries live in the game's `BepInEx/config` (auto-detected by `ModelRegistry.ConfigDir`), most with a git-tracked
backup mirror. The runtime plugin reads them on launch.

| Registry | Live path | Repo backup | Written by |
|---|---|---|---|
| **Model registry** (`pack.json`) | `config/haf_packs/ENCReload/pack.json` | `Assets/Pack/ENCReload/pack.json` | Model Factory, Animation Lab, Resize Lab, Unit Retexture, Sound Studio, Global Era Lab |
| **Districts** (`enc_districts.json`) | `config/enc_districts.json` | `Assets/Databases/enc_districts.backup.json` | District Factory |
| **Formations** (`enc_formations.json`) | `config/enc_formations.json` | project backup | Formation Override |
| **Sound overrides** (`enc_sounds.json`) | `config/enc_sounds.json` | project backup | Game Sound Lab |
| **Props** (`enc_props.json`) | `Assets/Databases/enc_props.json` | — | Prop Lab (editor-side recipe store; the runtime reads the baked GUIDs, not this file) |

> `pack.json` is ENC's own **pack** (see [Multi-Mod.md](Multi-Mod.md)). Older docs/config call the base model file
> `enc_models.json` colloquially — the real shipped filename is `pack.json` in the pack folder. Baked assets
> (`<name>_ModelMesh` / `_Skeleton` / `_Atlas`) go under `Assets/Resources`.

---

## Core model authoring

### Model Factory — `Tools ▸ HAF ▸ Model Factory`
Author a static model (or the *model half* of an animated one): pick/create a resource, a target pawn, and a model file;
tune geometry and shading; **Bake** → produces a `Skeleton` + `Atlas` and a registry entry. *Key controls:* target pawn,
model file, Size, Strip parts, reduce-to-tris, height-UVs, winding fix, double-sided, albedo brightness/saturation,
keep-black, atlas size (256–4096), material mode, hide-donor, freeze-donor, re-spawn-after-load, embedded 3D preview.
*Writes:* `pack.json` (via `ModelRegistry.Upsert`) + baked assets, through `ConfigFor → UniversalBaker`.
**Deep dive:** [Factory-Manual.md](Factory-Manual.md).

### Animation Lab — `Tools ▸ HAF ▸ Animation Lab`
The animation-only companion (docks beside the Factory): configures *which clip plays and how* for one model entry —
state-driven idle/move/after/attack, deploy conversion, recoil/slam, turret & muzzle bones, hand props, donor sockets.
*Key controls:* Clip field + range picker (▶) + Pick, state-driven toggle (idle-alt interval, attack repeats), deploy
block (frames, strip-parts, recoil frames/step/return, slam), turret bone + aim axis, muzzle bone + offset, hand prop
(bone/material/live rotation), animate-only bones, convert-raw-rig, fix-100×. *Writes:* `pack.json`, same
`UniversalBaker.BuildAnimated` pipeline. **Deep dive:** [Animated-Models.md](Animated-Models.md), [Factory-Manual.md](Factory-Manual.md) §16.

### Vehicle Lab — `Tools ▸ HAF ▸ Vehicle Lab`
Turn a **raw static** vehicle model into a rigged, animated GLB (Root + per-wheel/turret bones, a procedural *Spin*
action, optional rolling tracks and wave-rock) that the animated bake path then consumes — no Blender knowledge needed.
*Key controls:* raw model + output GLB, **Probe parts**, per-part role assignment, use-source-skeleton (for pre-rigged
`SKM_` rips), part-hiding sliders, model roll/pitch/yaw, axle axis, Spin frames/degrees, tread speed/detail, wave-rock
block, **Generate rig**, save/load **recipes**, **Verify** report. *Writes:* the rigged GLB + a recipe JSON (no unit
registry write here — the GLB is then baked via Model Factory/Animation Lab).

**Interior-part detection (strip what's never seen).** The probe classifies every part by **escape-ray sampling**:
a part is *external* if any sampled surface point has a straight, unblocked line to infinity; a part blocked in every
direction from every sample is **interior** — provably invisible (cockpit instruments, engine internals) and safe to
strip. The **Visibility** switch (All / External only / **Interior only**) filters the part list; set it to *Interior
only* and sweep the rows with `I` (**Ignore** = deleted from the output GLB) to reclaim the vertex budget for visible
surfaces — on the RAH-66 that was 47 parts and **28% of the model's vertices**. The verdict is deliberately
conservative: anything that peeks through an opening (canopy, gun bay) counts as external, so "inside-ish" parts
remain a manual judgment call. Classification happens at probe time — re-Probe an older session to populate it.
**Verify** also warns about interior parts *not yet Ignored* (name, role, vert count — clickable), so wasted budget
can't slip through unnoticed.

**Rotorcraft roles (helicopters).** Besides Wheel/Turret/Body there are **Rotor** (`R`) and **Tail rotor** (`L`) roles.
Each rotor group fuses into **one hub bone** (unlike wheels' proximity clusters, so a wide blade disc spins as one):
the **main rotor** pivots on its central hub part and spins about that hub's own *pole-to-pole* axis; the **tail fan**
pivots on the blades' centroid and spins about the axis *perpendicular to the duct ring* (lateral to the boom), with
**Tail-rotor axle** X/Y/Z override plus **yaw/pitch trim sliders** for the final degrees. Rotors are excluded from the
wheels' rolling-contact speed scaling, so main + tail spin at the same rate, and a rotor marking switches the printed
next-step recipe to the rotorcraft bake (**continuous** spin — State-driven OFF — and Auto-ground OFF). Preview aids
for dialing it in: **Pause**, **◀/▶ frame-step**, and a **Level line** (horizontal reference at rotor height).
Rotor bones are authored as **axle frames** (main: local Y = mast; tail fan: local X = the canted fan axle) so the
donor's own clip can drive them — see [Donor-Clip-Flight.md](Donor-Clip-Flight.md). Two workflow notes: Orientation
composes **yaw-first** (Pitch/Roll act on the grid-aligned model as you see it), and the sliders only take effect on
the next **Vehicleize** run — which is also a *mandatory separate step* before an Animation Lab rebake (the Lab
reuses the last rig GLB; skipping Vehicleize bakes the OLD rig).

---

## The injection-axis tools

### District Factory — `Tools ▸ HAF ▸ District Factory`
Bake a custom static district building — imports a model, bakes a **bone-free, auto-leveled FxMesh**, writes the
district registry entry (incl. the baked **albedo GUID** the plugin's texture injection binds) the district repoint
reads. An **embedded preview pane** shows the baked mesh, textured, on a tile-sized ground square at the true in-game
surface level — import angles rotate it live, orbit/zoom/pan like the Animation Lab. *Key controls:* district name +
Pick, model file, Size, rotation/import angles, target tris, normals, strip parts, isolate. *Writes:*
`enc_districts.json` (via `DistrictRegistry.Upsert`; mesh via `DistrictBaker.BakeFxMesh`). **Deep dive:**
[District-Visuals.md](District-Visuals.md).

### Prop Lab (attachments) — `Tools ▸ HAF ▸ Prop Lab (attachments)` · *experimental*
Author custom pawn attachments (weapons/gear): static bake → bone-free FxMesh → MeshCollection → FragmentMesh. Includes a
**dump** tool for vanilla fragment GUIDs. *Key controls:* fragment GUID (dump), Size, rotation/position offset, target
tris, borrowed material GUID. *Writes:* `Assets/Databases/enc_props.json` (editor recipe store; runtime reads the baked
GUIDs). **Deep dive:** [Pawn-Props.md](Pawn-Props.md).

### Projectile Lab (munitions) — `Tools ▸ HAF ▸ Projectile Lab (munitions)` · *experimental*
The projectile injection axis — today mostly a **dump/discovery** tool: paste a vanilla projectile GUID to log its FX
GUIDs/speed and walk each `FxEvolverMaterial` for mesh-typed fields, plus baking knobs toward a mesh-particle munition.
The final mesh-swap output field is still being discovered, so its registry/asset output isn't fully wired.
**Deep dive:** [Projectiles.md](Projectiles.md).

### Formation Override — `Tools ▸ HAF ▸ Formation Override`
Link a unit to a custom/vanilla formation (changing displayed pawn count) by serializing the formation's full layout into
config; supports a single-unit link or a full formation replacement (macro). *Key controls:* mode toggle, unit + Pick,
formation + Pick, re-read layout, packing jitter, formation scale + mode, footprint override. *Writes:*
`enc_formations.json` (dummy positions + 6 orientation grids, via `FormationRegistry.Upsert`). **Deep dive:** [Formations.md](Formations.md).

### Resize Lab — `Tools ▸ HAF ▸ Resize Lab`
Runtime per-unit rescaling, **no bake** — rules `{match, scale, era, trueSize, note}` the plugin applies to any unit whose
presentation name contains `match`. *Key controls:* the rule rows (match string, scale, era, note). *Writes:* the
`unitScales` array in `pack.json` (via `ModelRegistry.Save`). **Deep dive:** [Unit-Size.md](Unit-Size.md).

### Global Era Lab — `Tools ▸ HAF ▸ Global Era Lab`
Author how already-resized units rescale as the world ages — a **5×5 grid** of (unit era × current era) multipliers
(defaults 1.0), plus a "formation by size" threshold table. *Key controls:* the era-scale grid cells, formation-threshold
rows (threshold + formation Pick). *Writes:* era/threshold statics into `pack.json` (via `ModelRegistry.SaveStatics`,
preserving on-disk models). **Deep dive:** [Unit-Size.md](Unit-Size.md).

---

## Textures & audio

### Unit Retexture — `Tools ▸ HAF ▸ Unit Retexture`
Reskin an existing unit at runtime **without baking a model** — download its atlas to paint, replace with a PNG, or just
grey/tint it. *Key controls:* pawn description, replacement PNG, brightness (gamma), desaturate, RGB ±255 tint,
download-skin / Apply / Remove. *Writes:* texture-only entries in `pack.json`; skins under the pack's `skins/`.
**Deep dive:** [Textures.md](Textures.md).

### Sound Studio — `Tools ▸ HAF ▸ Sound Studio`
Configure one unit's whole audio profile onto its existing registry entry — **no bake**: silence donor sound, idle growl,
attack roar, death/battle cries, movement WAVs (spool-up → loop → spool-down), and the Wwise engine event. *Key
controls:* pawn Pick, silence-donor, per-clip fields with ▶ preview / ■ stop, start offsets, one-voice radius, engine-event
toggle. *Writes:* the unit's entry in `pack.json`; sound files under the pack's `sounds/`. **Deep dive:**
[Factory-Manual.md](Factory-Manual.md) §13–14.

### Game Sound Lab — `Tools ▸ HAF ▸ Game Sound Lab`
Author **global** audio overrides — silence any vanilla Wwise *event* by name-substring (units / ambient / music / UI).
Distinct from Sound Studio (which is per-model). *Key controls:* override rows (silence substring), category tabs, a
searchable catalog pick list. *Writes:* `enc_sounds.json` (via `SoundOverrideRegistry.Save`). **Deep dive:**
[Game-Sound-Lab.md](Game-Sound-Lab.md).

---

## Utilities, diagnostics & safety

- **Backup & Restore** — `Tools ▸ HAF ▸ Backup and Restore`. A safety net for everything git doesn't track (editor
  scripts, `FactorySource`, baked Resources, ENC databases, `Tools/`, live `BepInEx/config`). Timestamped, additive,
  guarded restore (auto-snapshots current state first). **Deep dive:** [Backup.md](Backup.md).
- **Database Browser** — `Tools ▸ HAF ▸ Database Browser`. Browse/search the game's definition assets (vanilla + mod) with
  an embedded inspector. Read-only viewer; persists UI prefs in EditorPrefs.
- **Tech Tree** — `Tools ▸ HAF ▸ Tech Tree ▸ Open Viewer` (+ `Diagnose Mod Split`, `Dump Data`). View/edit the tech tree;
  in edit mode drag node positions and remove prereqs, committed on Save (edits in place under `Databases/`, else
  copy-on-write into "New Additions"). New-tech creation / +prereq are noted not-yet-implemented.
- **Pawn Rig Dump** — `Tools ▸ HAF ▸ Diagnostics ▸ Pawn Rig Dump`. Dump every matching `PresentationPawnDefinition`'s full
  serialized graph (skeleton, clip collections, assets) to `rig_dump.txt` for rig investigation.
- **Export selected atlas to PNG** — `Tools ▸ HAF ▸ Export selected atlas to PNG`. Blit a selected (DXT1) atlas through a
  linear RT to a readable PNG in `C:/tmp` + log average RGB. (`AtlasDebug.cs`.)
- **Suppressed Console Noise** — `Tools ▸ HAF ▸ Suppressed Console Noise — open/clear log`. An `[InitializeOnLoad]` filter
  that hides known-harmless vanilla SDK console spam and re-logs matches to `Logs/SuppressedConsoleNoise.log`.

## Tests & gates — `Tools ▸ HAF ▸ Tests ▸ …`

Non-destructive (throwaway resource names). Run before committing baker/pipeline changes. See [Testing.md](Testing.md).

- **Bake Smoke Test** (`… (one per path)` / `… (ALL models)`) — re-bakes one model per bake path and asserts the assets
  are produced.
- **Bake Feature Test** (`…` / `… (Tier 2 — Blender + animated)`) — bakes synthetic cubes toggling one baker feature at a
  time and asserts a per-feature invariant.
- **Bake Conversion Gate Test** (`(litmus)` / `(registry converted models)` / `(deploy golden diff)`) — asserts raw-rig
  conversion invariants (scale==1, parent<child index, rotation-only curves) and the deploy-convert golden bone snapshots.

## Under the hood (non-window)

- **`UniversalBaker.cs`** — the bake core: model file + `BakeConfig` knobs → Amplitude `Skeleton` (+ atlas), static or
  animated. Called by the Factory, Animation Lab, and the tests (via `ConfigFor`, the one shared config path).
- **`DistrictBaker.cs`** — wraps a baked mesh as a bone-free district `FxMesh` (`BakeFxMesh`; also used by Prop Lab). One
  hand-driven menu: `Tools ▸ HAF ▸ District ▸ 1. Bake District FxMesh` (superseded by District Factory).
- **Registries** — `ModelRegistry` (master; `pack.json` + static `UnitScales`/`EraGrid`/`FormationThresholds`;
  corruption-guarded — won't overwrite an unparsable file), `DistrictRegistry`, `FormationRegistry`, `SoundOverrideRegistry`.
- **Dialogs** — `ClipRangeDialog` (scrub a clip → `clip[start..end/N]`), `SocketBonesDialog` (donor hardpoint → your bone
  map → `socketBones`), `StripPartsDialog` (pick parts → `deployStripExtra` CSV).
