# HAF — Milestones & project history

A reverse-chronological-ish log of capabilities as they were first proven in-game, with the war stories
behind them. This is the project's memory: what was hard, how it was cracked, and when. For *what HAF does
today*, see the [README](README.md) and the [docs index](docs/README.md); this page is the trail that got us here.

Dates are first-verified-in-game. Many entries pre-date the dating convention and carry no date.

---

## Units & animation

- **A HELICOPTER WITH ITS OWN SPINNING ROTORS — and the four-mechanism ghost hunt (2026-08-03/04).** The RAH-66
  Comanche now flies with **its own main + tail rotor spinning** (Vehicle Lab Rotor/Tail-rotor roles → continuous
  bake) instead of borrowing the donor gunship's. Getting there uncovered — and defeated — FOUR stacked mechanisms
  that together were the old "a donor's rotor can't be removed" wall: (1) gunship-class units spawn a **squadron
  of pawns** via the air hardcode (formation dummies don't cap them) → stacked copies of the model; fixed by
  keep-first-hide-rest (`hideSubPawns`); (2) our own `respawnAfterLoad` **leaked live sub-pawns** per attempt →
  off for own-rotor models; (3) one leaked pawn kept the **pre-injection donor cache** → the cached-struct repair
  (SRCFIX); (4) the last ghost — a translucent rotor that survived crushing every vertex of every ContentLayer,
  every pawn sweep, and a full renderer census — was **not geometry at all**: the donor's Mecanim-event **VFX
  billboard**, a 2D rotor sprite (the user's "it has no depth" observation cracked it), dropped by the July-era
  `silenceDonorVfx` flag. One registry flag; four hours of elimination to learn which one. The live **ghost-bisect
  tool** (file-driven in-session vertex surgery: crush/restore/census, no relaunches) ships from the hunt.
  Planned: a per-NAME VFX filter (`silenceDonorVfxNames`) to drop only the rotor sprite while keeping other donor
  effects, and a `moveTilt` nose-down attitude while moving (wired, dormant).
- **HAND PROPS — a weapon on a custom skeleton (2026-07-19).** The Combine soldier **carries a textured M60**,
  gripped correctly through idle, run, combat stance, and sustained fire. The donor (a vehicle) has no weapon
  slots, so the plugin constructs the pawn fragment itself and glues the Prop-Lab mesh to the injected skeleton's
  hand bone — with a surgical GPU-descriptor patch (the naive full rebuild scrambled other units), a per-tick
  repaint of the prop's own atlas on a private layer clone (Amplitude streams weapon textures and resets the
  material), and an always-stamped import-angle override (the baked angle field doesn't survive the mod bundle —
  the engine's `-90°X` class default silently tipped every prop until neutralized). Authoring: bake in the Prop
  Lab (now with per-prop saved recipes), pick it in the Animation Lab's **Hand prop** combobox, done.
- **STATE-DRIVEN characters — idle / run / after-move / combat stance / attack fire (2026-07-19).** The Combine
  soldier **idles standing, RUNS while moving, holds a weapon-raised COMBAT STANCE while its army is locked in a
  battle, and fires its ATTACK animation when it actually shoots** — five clips per model, switched live by the
  runtime (a ~20×/s state poll + per-pawn pose selection on the proven Pose0 slot; priority attack > move >
  after-move > combat > idle). The attack trigger is a hook on the game's own per-pawn ranged-fire sequence, so
  every battle volley animates the exact shooting pawn; an **Attack repeats** knob loops a short recoil-pop clip
  into sustained automatic fire (the soldier's 0.17s `shootAR2s` × 18 ≈ 3 s of fire, runtime-only — no re-bake).
  Configured entirely in the Animation Lab: a **State-driven** toggle with **Idle / Movement / After-movement /
  Attack / Combat-idle** clip pickers; all roles bake against **one shared skeleton** in a single Blender pass
  (every clip rebaked against the primary clip's frame-0 rest — per-role rests would displace the non-primary
  clips; single-frame stance clips are auto-padded so Unity's importer can't drop them). The bake-side war story:
  Blender's bone rename only syncs the *assigned* action's curve paths, so dormant role clips exported as frozen
  statues until the paths were patched explicitly — caught by byte-level pose-data analysis, fixed, and guarded by
  a tool-version cache-buster.
- **A HUMANOID character — a full 62-bone rigged soldier (2026-07-18/19).** The Combine soldier replaces a vehicle
  unit: right-sized, standing, head on his shoulders, **turning with its movement**, idling on his own baked clip —
  the first true *character* through the pipeline (props and machines came first). Getting him there built the
  **raw-rig conversion**: auto-rigged models whose clips *assemble the body from a scrambled rest via location keys*
  (which Amplitude, rotation-only, can never play) are now **rest-normalized and visually re-baked** at bake time —
  the assembled pose becomes the rest, the whole clip is re-derived as pure rotations (in-bake verified to ~1e-4),
  the export folds units/rotation/scale into the data, collapses no-op roots, and renames bones topologically
  (Amplitude sorts alphabetically and requires parents before children). A **litmus rig** (12-deep chain of cubes,
  `Tools/make_litmus.py`) proved the runtime renders clean rigs perfectly. Also discovered en route: the game turns
  pawns through a procedural **bone-rotation layer** — the plugin clears it only for artillery models and ignores
  vehicle donors' phantom wheel-spin slots. *(With the clean rig, the unit's fired-drone projectile also displays
  again during attacks — the fully working unit: stand, turn, idle, launch.)*
- **Animated custom models — a first, one-click.** A quadcopter drone injected onto a land unit renders full-size,
  textured, and **spins its own propellers from its own baked animation** — for any number of instances. Tick
  **Animated**, press Bake.
- **A wheeled vehicle with its OWN spinning wheels (2026-07-24).** The Ehrhardt armored car (Era5) replaces the
  Armoured Car — a purpose-made *skinned* rig whose **four wheels spin in place while it drives and are still when
  parked** (state-driven: Idle = a held frame, Movement = a spin slice). En route it pinned down a nasty engine
  trap: on the legacy path a **rotating** bone flings off in-game (idle fine, movement flings) because the
  metre→centimetre FBX export leaves a **×100 sandwich** Amplitude's TRS composition mangles — the same mechanism
  as the soldier's head. The fix is **Convert raw rig ON + Fix 100× OFF** (cancels the ×100 at export), which
  overturns the old "clean rigs skip convertRig" rule for any rig with a spinning part. It also grew a hands-free
  **Auto-ground (sit on terrain)** bake toggle — drops the tyres to the skeleton origin, self-correcting and
  **size-proof** (no manual height dial, stays grounded across Size changes). Extracted from an Unreal "Game
  Template" (Fab).
- **The rotation-only barrier is DEAD — true bone TRANSLATION plays in-game (2026-07-25/26).** Decompiling the
  runtime proved the clip format supports `RotationTranslation` (vanilla tank treads use it); the "rotation-only
  law" was our own bake's strip. The opt-in **`Keep bone translations`** flag carries authored slides through the
  bake — first verified on a sliding test bone, then shipped as **the M114 howitzer's REAL kickback**: fire, the
  tube slams back and glides home, barrel lowers, shell loads, aiming raise — the animator's complete cycle,
  finally rendered (multi-segment recoil windows with per-segment speed steps: `442..530,305..441/2`). En route,
  a decade-class root cause fell: a sentinel value placed a helper bone at 10⁹ units, collapsing bone chains via
  float32 cancellation — the origin of every NaN import warning this pipeline ever produced.
- **Moving caterpillar tracks — path-instanced rigid links (treadize, 2026-07-26).** Mark a tank's tread loop
  **C (Caterpillar)** (+ barrel **G**) in the Vehicle Lab and it becomes a real rolling track: the link pitch is
  measured off the cleats (autocorrelation), the loop path is built as the classic *belt around pulleys* from the
  wheel centers + measured band radii, the mesh is cut into **half-link cells at the cleat gaps — one bone each,
  no skin blending** — and every link rides the path with advance = exactly one link per loop (invisible restart,
  tread ≈ sprocket surface speed). Seventeen revisions of blended-skin approaches lost to the eye's verdict —
  "molded links bending = slack" — before rigid instancing won; the full post-mortem lives in
  [Animation-Pitfalls](docs/Animation-Pitfalls.md). Runs in-game (after a five-defect debugging chain); a remaining
  idle micro-twitch is the open polish item.
- **A turret that AIMS at the target (turretize, 2026-07-24).** The armored car's turret now yaws to track the
  enemy — by hijacking the game's OWN aim: the engine streams a heading angle into a `PawnEntry.BoneRotation` slot
  that lands on an invalid bone index for injected models, so we retarget that slot to the turret bone and the
  engine's aim math drives it (no per-frame trig). Runtime-only (**Turret bone** + **Turret aim axis** in the
  Animation Lab, Save + relaunch). The aim axis is per-model — yaw for a turret, and the *same* knob gives **pitch**
  for a future mechanized howitzer/artillery barrel to elevate at range.
- **Fire-on-attack — a model that animates when the unit *fires*.** Tick **Fire on attack** and the baked clip plays
  **once, on the combat action**, not on a loop: the model rests, then plays a single pass the moment the unit attacks and
  returns to rest. Proven with a **howitzer whose barrel elevates only when it bombards** — the plugin hooks Humankind's
  own combat event bus, matches the firing unit to the injected model, and triggers one playthrough.
- **First-instance rotor fix.** The engine draws the *first* borrowed-rotor pawn of a model, at the moment it's **created**,
  with its rotor ~1 unit low (a spawn race — every later instance is fine). Ticking **Re-spawn after load** makes the plugin
  watch for any such unit appearing — on a save-load, built in a city, or dev-spawned — and near-instantly re-run the game's
  own pawn rebuild (`PresentationUnit.UpdatePawns`) on it, a presentation-only refresh (no unit touched) that clears the low
  rotor. Applied to every instance as it appears (one brief flicker each) so a buggy one is never missed. Opt-in per model;
  the re-spawn delay is tunable in the plugin cfg (`Factory/RespawnDelayFrames`, default 1) for slower machines.
- **Freeze the donor's motion.** A *static* model riding an animated ground/hover donor inherits the donor's idle/move bob;
  **Freeze donor animation** pins the donor's pose so a rigid model (an airship) holds still while it still glides
  tile-to-tile — applied across *every* instance the same way animated models are (descriptor-matched + skeleton-forced).
- **Borrow the donor's animation — including *multiple* moving parts.** A model rides a donor unit's rig; injection can't
  *remove* a donor's animated sub-part (a rotor), but you can turn that into a feature: **strip your model's own rotor(s)**
  and the **donor's spinning rotor shows through**. The donor helicopter has *two* rotor bones (`Helix` main +
  `Helix_back` tail), so stripping both the Comanche's main *and* tail rotor gives it a spinning main rotor **and** a spinning
  shrouded fantail — two borrowed animations on one static model. Or give the model **its own** clip.

## Authoring tools

- **Vehicle Lab: helicopters + interior-part detection (2026-08-03).** Two new part roles rig a **rotorcraft** the
  same no-Blender way as a wheeled vehicle: **Rotor** (`R`) and **Tail rotor** (`L`). Each rotor fuses into **one
  hub bone** (proximity clustering would shred a wide blade disc into pinwheeling halves — the RAH-66's 18-unit
  disc proved it): the main rotor pivots on its central hub part and spins about that hub's own *pole-to-pole*
  axis, the tail fan pivots on its blades' centroid and spins about the axis *perpendicular to the duct ring*,
  with an own Auto/X/Y/Z override + **yaw/pitch trim sliders** for the last degrees by eye. Rotors are exempt from
  the wheels' rolling-contact speed scaling (it span the small tail fan ~3.6× too fast), Verify understands them
  (1 hub per group; car-only symmetry checks skipped), and new preview aids — **Pause**, **one-frame step ◀/▶**,
  a **Level line** at rotor height — make the axle judgeable. Preview-verified on the RAH-66; in-game bake pending.
  Same day, the probe gained **escape-ray visibility classification**: every part is tested for a straight
  line-of-sight to infinity, and a **Visibility switch** (All / External / **Interior only**) surfaces the parts
  that are *provably never visible* — cockpit gear, engine guts — for a one-key **Ignore** sweep. On the RAH-66 it
  found 47 interior parts worth **28% of the model's vertices** (11,042 → 8,651 in the generated rig), budget
  returned to the shared GPU vertex pool. Deliberately conservative: a part that peeks through any opening counts
  as external.
- **The Vehicle Lab — any static vehicle model becomes that unit, no Blender knowledge (2026-07-25).** A dedicated
  window "vehicleizes" a raw model: headless-probe its parts (a 3,350-shard game rip included), mark wheels &
  turret with a keyboard-driven review UI (zoom-highlight preview, classification filters, height-slab sliders,
  save/load **recipes**, a clustering-accurate **Verify** report), and it builds the rigged, LINEAR-`Spin` GLB the
  animated path consumes — wheel shards **clustered per hub** so spokes revolve around the axle, one mesh per bone,
  the rip's stowaway skeleton stripped. **Verified in-game the same day: the shipped ArmouredCar now runs a
  Lab-generated rig** — grounded, turret aiming (axis Y on generated rigs), muzzle flash re-anchored on the
  `Turret` bone. Rips that ship **already rigged** (`SKM_`) get a **fast path**: the probe detects the skinned
  artist skeleton and the Lab marks *bones* instead of shards — Spin authored straight onto the source rig,
  weapon/socket bones preserved (it inherits the artist's weighting; the shard flow stays the quality reference).
- **The Animation Lab — animation authoring in its own dialog (2026-07-18).** `Tools ▸ HAF ▸ Animation Lab` docks as
  a tab beside the Factory: the Factory owns the *model* (file, transform, size, shading), the Lab owns the
  *animation* (clip + bone-filter pickers, fire-on-attack, deploy-on-stop + recoil, and **Save (no bake)** for
  runtime flags). Settings are mutually exclusive between the windows and **enforced at bake time** — each window
  rebases on the freshest registry entry and writes only its own fields, so stale copies can't clobber each other.
  Geometry re-processing is **automatic** (the Blender step re-runs exactly when one of its inputs changed); the old
  "Reuse extracted" checkbox is now purely **"Keep extracted texture (hand-edits)"**.

## Textures & meshes

- **Multiple static models live**, no new code each: a **Zeppelin**, an **LCAC Hovercraft**, a fully-textured **USS
  Zumwalt stealth cruiser**, and a **RAH-66 Comanche** helicopter — correct orientation, correct skin, at the waterline.
- **Heavy / single-sided / multi-material meshes, handled** — a built-in vertex reducer, a winding fix + double-sided
  fallback for CAD "sketch" meshes, height-based UVs, and an N-material atlas packer. Formats: GLB / glTF / OBJ / FBX /
  `.blend`.
- **Correct, isolated textures.** Custom skins map right-side-up out of the box (the glTF-V-top vs OBJ/Unity-V-bottom
  convention is reconciled during OBJ import, and off-tile UVs — a skin mapped into the V 1→2 tile relying on wrap — are
  shifted back into range so they don't collapse to a flat smear), and each model gets a private `FxOutputLayer` so its
  skin never bleeds onto the donor.
- **Tune the skin, shrink the bundle.** Bake-time **Albedo brightness / saturation** lift a dark or washed-out skin (the
  injection ships *flat* albedo — donor PBR neutralized — so a shiny/dark source reads muddy without this); a **Keep black**
  toggle preserves an intentionally black material (a glass canopy); and **Atlas size** (256–2048, default 512) + DXT1
  compression keep each shipped skin ~0.1–2 MB. Bake *inputs* live in `Assets/FactorySource/` — out of the shipped mod, so
  the licensed source models are never redistributed.
- **Strip parts of your model at bake time.** A "Strip parts" field deletes named objects (+ children) from the source
  mesh before baking — the mirror of Hide-donor, on *your* model. Drop a helicopter's own rotor, a crew figure, a weapon
  pod… Name-Pick reads objects straight from the GLB/glTF. Proven removing the Comanche's rotor blades.
- **Retexture / recolour without a bake.** A separate **Unit Retexture** window reskins an existing unit at runtime —
  a hot-loaded PNG or a live Desaturate + RGB adjust on its own atlas — isolated per unit, free on the vertex budget.
  Works on **baked custom models** too (the PNG replaces the baked atlas — recolour without a re-bake), with a live
  in-editor preview of the exact skin that will be injected.

## Audio

- **Unit movement audio — engine sounds & custom WAVs.** Injected/retextured units are silent on move (the game's per-ship
  engine sound rides an audio-service path our re-loaded units never fire). The plugin restores it — playing the game's own
  sound **by name** (works from the *first* unit, no capture; F8 **Dump Sound Catalog** lists all ~845 event names) — or
  **any custom WAV you drop in**, as a **Start (spool-up) → Travel (loop) → Stop (spool-down)** sequence with per-clip
  volume, driven by the dedicated **Sound Studio** editor window (with in-editor ▶ preview). Runtime-only, no bake.
- **Creature voices — silence the donor, add your own growl and attack roar.** A borrowed animal donor drags its Wwise
  voice along (the Abomination's bear donor growled and mauled through every re-skin); `silenceDonorAudio` drops it at
  runtime. In its place: an **Idle growl** WAV on a jittered interval with a **one-voice radius** (a 5-stack snarls one
  pawn at a time, not in unison), and an **Attack sound** fired at attack *commit* — camera-anchored so it stays audible
  at battle zoom, with a **start offset** that skips a WAV's silent windup so the impact lands on the swing. A **Death
  sound** (rattle/scream as a pawn falls) and a **Battle-start war cry** (once, the moment a battle begins with the unit
  in it) complete the arc: alive → to arms → fighting → gone. (Growl + attack verified in-game 2026-07-23; death + war
  cry built, in-game verification pending.)

## Multi-mod & safety

- **Multi-mod — merge packs from many authors (2026-07-19).** The runtime is a **Humankind Asset Framework** host, not just
  ENC's loader: it merges ENC's base registry with any number of third-party **packs** dropped in `BepInEx/config/haf_packs/`,
  so a modder augments their own units with a custom model / texture / sound by shipping just a config file + assets — **no
  ENC edits, no code**. Pack resolution is **enforced**: duplicate `modId`s rejected, `dependsOn` validated, load order
  topologically sorted over `dependsOn`/`loadAfter` (cycles broken loudly), **declared `overrides` replace** the targeted
  entry, and an undeclared same-pawn clash stays first-loaded-wins, logged loud — no silent overrides. Every load writes a
  `haf_load_report.txt` with the resolution decisions.
- **Backup & Restore — a safety net for the un-versioned assets.** ENCReload's git tracks only `Assets/Databases`;
  a **Backup and Restore** editor window snapshots everything else (editor tooling, source & baked models, databases,
  `Tools/`, and the live BepInEx runtime config) to a timestamped, manifest-backed folder on `D:`. Restore is guarded —
  it auto-snapshots the current state first, copies back **additively** (never deletes work you've added since), and
  verifies file counts.
