# HAF — Milestones & project history

A reverse-chronological-ish log of capabilities as they were first proven in-game, with the war stories
behind them. This is the project's memory: what was hard, how it was cracked, and when. For *what HAF does
today*, see the [README](README.md) and the [docs index](docs/README.md); this page is the trail that got us here.

Dates are first-verified-in-game. Many entries pre-date the dating convention and carry no date.

---

## Units & animation

- **BATTLE GUNNERY — the Jagdpanzer arc (2026-08-06).** A casemate tank destroyer exposed, one shot at a
  time, that vanilla **never rotates a vehicle's hull in battle** (vehicles aim only via a turret bone slot —
  invalid on custom rigs), and grew the full gunnery chain in a day: **battle hull-aim** (the map bombard's
  aim machinery armed per volley — the eased hull lays on the actual target, `hold=1` waits for the lay);
  the **gun-vs-turret model** in the Animation Lab (a Turret bone *yaws* and classifies the vehicle turreted;
  a Gun bone aims with the hull and only *elevates*); **distance-proportional gun elevation** (user spec:
  raised by range to a configurable max, rising while the hull turns, lowered after the shot); the **muzzle
  dial gone gun-local** (rotates with aim + elevation, now moves flash, tracer AND smoke — a world-space dial
  can't follow a turning hull, and a bone's TRS sits at the breech, not the barrel end); and **post-shot
  facing that settles on the nearest clean facing toward the shot** (v1's yield-on-yaw-change heuristic
  couldn't tell a real order from the choreography's own post-fight reset — graveyarded). Every asset in the
  chain is the game's own; HAF only fixes where and when. See [docs/Turn-Ease.md](docs/Turn-Ease.md).
- **CATEGORY TURN EASE — every unit type turns in character (2026-08-06).** Turn ease graduated from a
  per-model knob to a **game-wide system with per-TYPE defaults** — human / land / turret / hover / ship —
  each classified by **characteristic, never by name** (user rule, enforced twice): capability profiles, the
  game's own `Hover` "ignores terrain" ability, and live azimuth-transform detection for turrets; fixed-wing
  planes are excluded outright (they already fly natural curves — user call). Hover and ship carry their own
  bank (`hoverbank`/`shipbank`: a chopper banks, a ship heels), and precedence flipped to per-model > per-unit
  link > category > global. Getting the *strike hold* to follow the category cost four measured bugs, each a
  different naming-layer trap (an entry dead-end, artillery rendering its LIMBERED variant whose name extends
  the unit's, the servant CREW answering for its gun, and artillery main-gun pawn definitions that never pass
  the addon hook at all) — closed structurally: the slow class scan reads the rendered unit itself and is the
  classification authority, and the hold reads the eased pawn's ground-truth rate off its live turn state, so
  the visible turn and the fire hold cannot disagree by construction. Configured from the Formation Override
  window's **Turn ease defaults** panel (live dial write). See [docs/Turn-Ease.md](docs/Turn-Ease.md).
- **ATTACK TURN — the howitzer pivots, THEN fires (2026-08-05).** A map bombard used to teleport-snap the
  unit's facing and fire in the same instant; now a HAF model with a turn-ease rate **sweeps to the attack
  heading first**, and every observable of the shot — muzzle flash, shot sound, shell, impact, the model's own
  recoil clip — **waits for the barrel** and lands together at alignment. Six iterations to find the real seam,
  each killed by a measurement: the battle choreography (LookAt actions, rotation FSM) is a **no-op on the
  world map** (`StepTurning` runs 0→0 — the snap is `FlipPawnsGrid(Teleport)` stamping the GPU pawn data);
  patching the unanimated-rotation method silently did nothing because **the JIT inlines it** into its caller;
  and a one-shot delay at attack start **raced the snap** and computed zero. The fix rides HAF's own seams: the
  Comanche's ObjectSpace turn ease generalized to every entry, plus three holds keyed off the same
  remaining-turn time — the artillery controller's scheduled launch/hit delays, a deferred
  `TeleportToSimpleAttack` (the muzzle/sound carrier), and the fire clip's clock pinned until aligned.
  Turn ease also smooths ordinary move-order facing for any model with a rate. Same day, two extensions, both
  verified: **vanilla units** get the identical treatment through a Formation Lab link (per-unit rate, resolved
  to the pawn descriptor at load; a link on a Common unit covers its culture-emblematic variants — found when
  the player's ZULU siege howitzers ignored the Common link, by a one-line-per-descriptor render census), and
  **true-bearing aim** — the eased turn exposed that vanilla bombards face a HEX-QUANTIZED angle (one of six
  directions, up to 30° off); the ease target now becomes the real bearing to the target tile while the strike
  plays out, so the barrel lays exactly on the city it shells. The aim then surfaced **three more vanilla
  shortcuts** (2026-08-06, each spotted by the user frame-stepping captures, each verified fixed): the strike
  ran on TWO CLOCKS (dynamic release vs padded schedule — the bang drifted ~0.25 s from the recoil; now one
  shared release timestamp armed before the flip), the attack clip teleported in at a RANDOM PHASE while the
  shell was timed to its literal event time (now deterministic frame-0 playback), and the shell + muzzle smoke
  spawned at the PRE-PIVOT barrel — vanilla captures the muzzle at schedule time, and the pawn's invisible
  transform skeleton never turns with the eased model (now: fire-time recapture + every bone TRS aim-rotated at
  the GetBoneTRS seam while the strike is live). See [docs/Turn-Ease.md](docs/Turn-Ease.md).
- **CLIFF ANTICIPATION — climbing before the edge, not into it (2026-08-05).** Terrain hug's lead point now
  also reads the *ground* ahead: where the terrain steps up, the aircraft gains that height immediately instead
  of rising at the cell boundary, and the engine's own tile-bound altitude catches up on arrival (climb-only —
  anticipating a descent would sink toward the ridge still being crossed). Needed a physics reference and one
  correction found by reading the log rather than the screen: the first probe was a plain downward raycast and
  measured the helicopter's **own army collider**, so it compared unit heights, not terrain; it now uses
  `RaycastAll`, skips units, and takes the lowest hit. Dial: `cliff` in `enc_hugterrain.txt`.
- **TERRAIN HUG — nap-of-the-earth flight, climbing only for the city (2026-08-05).** The helicopter now
  **skims low over open ground and climbs only for built districts**, instead of cruising at skyline height
  everywhere. The engine's air altitude is already terrain-relative (it follows hills for free) but ignores
  buildings — so the model's `position.z` lift is now *subtracted* wherever no built district sits under or
  ahead of the unit, with the probe **leading** along the movement vector so it climbs before the buildings.
  Two measurements replaced two guesses: the map's **tile spacing is derived** from the median
  nearest-neighbour distance between districts (6.93 units on the test map → auto match radius 3.81 = "this
  district's own tile"; a hand-picked radius lifted the unit for every field beside the city), and districts
  are classified by their private **`constructibleDefinitionName`** rather than the always-identical
  GameObject name — which exposed that Humankind renders cultivated tiles as districts too (`Exploitation`,
  `Ruin` are flat; only `Extension_*` carries buildings). Live-tunable via `enc_hugterrain.txt`
  (drop/radius/lookahead/ease + `only`/`skip` name filters). See
  [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md).
- **TURN EASE — flown turns instead of the facing snap (2026-08-04, same day as the flight milestone).** The
  engine snaps a pawn's facing instantly on a move order; the Comanche now **sweeps** to its new heading at a
  capped rate and **banks into the turn**, composed under the nose-down attitude machinery. Every angle eases
  (180s included) while teleports/battle placement snap naturally — the per-pawn state is position-matched, so
  a jumped pawn simply starts fresh at the target heading. Live-tunable in-game via `enc_turnease.txt`
  (rate/bank, ~1/s poll) — dialed to feel on the first flight. Spotted as a gap by **shakee** on the milestone
  video within minutes of posting; built and verified the same evening. Per-model Factory fields are the
  planned graduation. See [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md).
- **DONOR-CLIP NATIVE FLIGHT — the donor's own animation on our rig (2026-08-04).** The Comanche now flies with
  the donor gunship's **complete original animation** — hover bob, main rotor flat on the mast, tail fan spinning
  in its own **canted** ring — driving OUR baked mesh natively (`useDonorClip`, now a Factory checkbox). Cracked
  with instruments, not guesses: a `[Rest]` skeleton dump (donor rigs keep ALL rests identity; ours carried the
  glTF -90°X on bone 0 and the facing rotation on Root — each **conjugates** every animated descendant, because
  the engine composes clips ON TOP of rests) and a `[DonorAxis]` decoder that read the donor channels straight
  from the GPU records (ch2 main = pure local-Y spin ~18°/frame; ch3 tail = pure local-X ~36°/frame). The fix is
  two-sided: the plugin **rebases the injected skeleton at registration** (ancestors → identity rests with world
  positions preserved — and it MUST run before `AnimationManager.Apply`, which snapshots BoneInfos into the GPU;
  leaf rotor bones keep their orientation), and the Vehicle Lab **authors the axle frames** (main-rotor bone
  local Y = mast, tail-fan bone local X = the canted fan axle). Five failure modes catalogued on the way
  (index-shifted channels, rolled axis, orbiting rotor, vertical loop, stale-rig rebake) — the full contract
  and catalog: [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md). Plus a live `enc_rotortrim.txt` dial
  (constant BR-slot tilt, re-applied to live pawns ~1/s, no relaunch) kept inert as a finishing tool.
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
