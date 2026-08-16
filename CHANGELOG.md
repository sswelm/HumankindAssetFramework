# HAF — Milestones & project history

A reverse-chronological-ish log of capabilities as they were first proven in-game, with the war stories
behind them. This is the project's memory: what was hard, how it was cracked, and when. For *what HAF does
today*, see the [README](README.md) and the [docs index](docs/README.md); this page is the trail that got us here.

Dates are first-verified-in-game. Many entries pre-date the dating convention and carry no date.

---

## Units & animation

- **SAVE-RELOAD ISOLATION — the organ-gun load-order bug (2026-08-16).** Loading a heavy save then another in
  one app run tore an animated custom unit (the organ gun) and, once the mesh bound, painted it the wrong donor
  **red**; a fresh load was always clean. The F8 GPU-mesh-buffer readout (a `+1 mesh / +4.7k verts` diff on the
  second load) ruled out buffer overflow and pointed at stale isolation, and a per-load registration dump found
  the cause: **`AnimationManager.AnimationLoad` fires once per PROCESS, not per save-load** — the whole
  model-axis re-arm hung off it, so a second session never re-registered our skeletons into the game's rebuilt
  `AnimationManager`. Fix: re-arm on the seams that *do* fire per session — **`PawnManager.Load`** (the universal
  one: save-load, reload, *and* a New Game) plus `Sandbox.Load` (so the district axis resets synchronously) —
  all via a thread-safe flag consumed on the main-thread `Update` (the hooks may be off it). A `[SessionProbe]`
  proved the whole thing: `AnimationLoad` fired only once even across a main-menu trip, and a New Game after a
  load re-registered (fresh skel ids) only once `PawnManager.Load` was wired in. That exposed a second, older
  trap: the re-arm cleanup `Destroy()`'d `e.tex` unconditionally,
  but for a normally-textured model that is the **shared bundle atlas** from `AssetDatabase.LoadAsset` —
  destroying it made the reload's `LoadAsset` return `null` (the red skin). Fix: `ModelEntry.texOwned` — only
  destroy textures the plugin creates, never a `LoadAsset`'d asset. Both verified in-game on the load-order
  repro. Bonus: the model-axis session cleanup (audio/deploy/state maps) now runs on *every* reload, not just
  the first.

- **DISTRICT CLONE LEAK — critical-review follow-up (2026-08-16).** A full-framework critical review (plugin +
  editor) surfaced that the district axis had the *same* leak class just fixed on the model axis:
  `ResetDistrictSessionState` only **nulled** its runtime `Object.Instantiate` clones (private leaves, cloned
  selectors/output-layers, deep-clone material nodes, the B&W gray albedo), which Unity's unused-asset sweep
  never collects — so every in-session reload leaked a native FxOutputLayer + N cloned FxEvolverMaterials + a
  gray texture per scoped district. Fixed with explicit ownership tracking (never touching `LoadAsset`'d bundle
  assets) and a main-thread destroy queue (the reset runs off-thread via `Sandbox.Load`). In-game verified across
  reloads (`[District] freed N runtime clone(s)`, no district errors).

- **`hideSubPawns` COEXISTENCE — critical-review follow-up (2026-08-16).** The gunship duplicate-pawn hide (keeps
  one pawn, buries the stacked squadron copies) counted per model *type*, not per unit — so a second coexisting
  unit of the same model (yours + an enemy's, or two of yours) rendered **nothing**. Fixed by keying "already
  kept this frame" on unit *position* (a unit's stack shares a spot; a different unit is tiles away). Verified
  in-game with several gunship helicopters on screen at once, each a single clean model.

- **UNIT→ENTRY MATCH UNIFIED — critical-review follow-up (2026-08-16).** Repoint resolved a unit to its entry by
  longest-match on the full `pawnDescription`, but the movement/deploy/state polls used *first-in-registry*
  substring on `coreDesc` (the `_NN`-stripped stem) — so two entries sharing a stem (`Foo_01`/`Foo_02` = distinct
  models) repointed to distinct models but animated/deployed/sounded from whichever sorted first. Fixed by routing
  every per-unit path through one matcher (`FindEntryForUnitDefinition`) that tries the full `pawnDescription`
  first (distinguishes `_01`/`_02`) and falls back to `coreDesc` (never regresses a working bind). Latent for the
  reference pack (no stem collisions) but a real correctness gap for third-party packs.

- **FACING SURVIVES RESPAWN (2026-08-16).** `respawnAfterLoad` units (the helicopters) lost their saved heading on
  load: the ~3-frame post-load `UpdatePawns` rebuild recomputes `FormationAngle` to neutral *after* the single-shot
  facing-restore already fired and closed (non-respawn units like the organ gun kept theirs). Fixed by coordinating
  the two systems — `MaybeRespawnPostLoad` re-arms `FacingPersist` right after each respawn, which re-applies the
  saved angle once the rebuilt unit is loaded + stationary (same frame, no neutral flash; still skips units the
  player is moving, so no crab-walk). Verified: a helicopter saved facing east holds its heading across a reload
  (`[Facing] re-applied army … after respawn`).

- **FORMATION PURE-REPOINT REFORM — critical-review follow-up (2026-08-16).** The catch-up that re-instantiates
  units which spawned *before* a formation override landed only fired for entries carrying dummy data — a
  **pure-repoint link** (points a unit at a formation already in the DB, no authored dummies) was excluded by the
  `dummies.Count > 0` gate, and its "already full?" test compared against `e.dummies.Count` (= 0), so its
  pre-override units kept the old pawn count until a reload. Fixed with a new `Entry.targetCount` (the target
  formation's real `Dummies.Length`), computed in `ApplyOne`, and by including unit links in the reform selector.
  Zero change for ENC (all its entries carry dummy data → same inject/overwrite path); protects third-party packs
  that repoint a unit to a vanilla formation.

- **GAMEBINDING COVERAGE — the army-walk root (2026-08-16).** Critical-review finding #5: the `Presentation`
  Dep was catalogued with *zero members*, so `PresentationEntityFactoryController` — the static army-walk root
  that respawn, facing-persistence, class-scan and the descriptor census all read — wasn't validated. A game
  rename there would silently no-op all four with nothing in the health report. Added it (plus the factory's
  `PresentationArmyEntities` next hop): catalog 46 → 47 types, report clean (`OK — 47 game type(s)`). The
  fragility-plan "make drift loud" template applied to exactly the code the recent respawn/facing fixes touch.

- **AUDIO DEATH/BATTLE GATE (2026-08-16).** Critical-review finding #6: the `_audioOn` poll gate omitted
  `soundDeathFile`/`soundBattleFile` while the loader right below it (and `OnPawnDeath`/`ProcessBattleCries`)
  consume them — so an entry with *only* a death rattle or *only* a battle cry never entered the poll, its clip
  never loaded (silent death cue), and `ProcessBattleCries` re-enqueued the cry every frame forever. One-line fix:
  add both fields to the gate, mirroring the loader's own check. Zero change for ENC (no death/battle entries);
  protective for its built-but-unshipped creature voices and third-party packs.

- **STATE-MACHINE GATE (2026-08-16).** Critical-review #8: `StatePose`/`ProcessAnimStates` ran only when
  `moveAnimId >= 0`, but attacks armed on `attackAnimId >= 0` — so a move-less state-driven model (idle+attack,
  no move clip) armed fires that never animated. Fixed with a shared `ModelEntry.AnyStateRole` predicate driving
  all three gates, plus a guard on the `moving` pose branch so a move-less model that moves falls back to idle.
  Zero change for ENC (all its state-driven models have a move clip); protective for a stationary-turret-style unit.

- **RUNTIME-CLONE LEAKS — critical-review #7 (2026-08-16).** Three more leaks in the district-clone family:
  (1) `InjectHandProp` overwrote `e.handPropLayer` with a fresh `Instantiate` clone on every re-inject (LOD /
  save-load / respawn drops the prop fragment), orphaning the previous native FxOutputLayer — now the old clone is
  Destroyed first (affects ENC's hand-prop units, no visible change). (2) `BuildAdjustedAtlas` and (3) `MakeGrayCopy`
  (the B&W footprint) returned `null` on a `ReadPixels`/`Apply` throw without releasing the pooled RenderTexture,
  restoring `RenderTexture.active`, or freeing the half-built texture — and `TickOne` retries every frame. Both now
  use try/finally so the RT + active are always cleaned up and the partial texture is freed on failure. Normal
  rendering unchanged; verified no-regression in-game.

- **BAKE-SCRIPT SILENT-MIS-BAKE GUARDS — critical-review Tier 3 (2026-08-16, in the ENCReload `Tools/`).** Three
  bake foot-guns that shipped a broken rig with **exit 0** are now loud aborts: (4A) `rig_anim.py` printed the
  rest-fold frame-0 residual (`should be ~0`) but never asserted it — a fold that completes yet leaves a bone
  displaced (the "head off shoulders" class) shipped silently; now aborts on NaN or a residual > 25% of the rig's
  bone scale. (2A) a failed `transform_apply(rotation+scale)` on the conversion path was swallowed with a warning,
  shipping a skeleton ~100× off the mesh; now hard-fails. (4B) `deploy_convert.py` with zero animated parts built a
  StaticRoot-only rig and shipped a static single-bone model; now aborts. Verified: the OrganGun re-baked clean
  (residual `0.000000` asserted OK, rotation+scale applied, `ANIMATED DONE`, no false abort).

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
  `RaycastAll`, skips units, and takes the lowest hit. Dial: `cliff` in `haf_hugterrain.txt`.
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
  `Ruin` are flat; only `Extension_*` carries buildings). Live-tunable via `haf_hugterrain.txt`
  (drop/radius/lookahead/ease + `only`/`skip` name filters). See
  [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md).
- **TURN EASE — flown turns instead of the facing snap (2026-08-04, same day as the flight milestone).** The
  engine snaps a pawn's facing instantly on a move order; the Comanche now **sweeps** to its new heading at a
  capped rate and **banks into the turn**, composed under the nose-down attitude machinery. Every angle eases
  (180s included) while teleports/battle placement snap naturally — the per-pawn state is position-matched, so
  a jumped pawn simply starts fresh at the target heading. Live-tunable in-game via `haf_turnease.txt`
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
  and catalog: [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md). Plus a live `haf_rotortrim.txt` dial
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

## Districts

- **STRATEGIC MESH FOOTPRINT + scoped-path migration (2026-08-15).** A district's zoomed-out strategic footprint is now
  its **own 3D building**, not a flat decal. The strategic fade turned out to be a **per-element GPU render-feature gate**
  (`FxEvolverMaterialLevelBuildElement.RenderFeatureSelector.SelectionFlags0`), not a camera swap — zeroing it (AlwaysEnabled)
  keeps the mesh drawing in every zoom band. On top of it: **black-and-white when zoomed out** (bind a greyscale albedo
  keyed to `RenderFeatureProvider.ComputeRenderState` of the *Topographic* band) and **flatten to a sheet** (a `size.y`
  multiplier — vertical placement is terrain-owned, so a "lift" was a proven dead end). All five settings are authored
  **per-district in the District Factory** (`footprintMesh`/BW/Flat/FlatHeight/HideDecal), falling back to the plugin
  config. Any district **migrates onto the scoped render path with one Bake** (`BakeScopedSelector` clones a
  single-building footprint template, swaps in the district's FxMesh, keeps the decals → a data-authored
  `CityMapSelector`), retiring the legacy isolate/repoint route. **Two custom districts now coexist independently**
  (breeder reactor + a Greek-temple Oracle in one game, each with its own texture + footprint) — which needed a
  per-district `ScopedState` refactor *and* moving the driving calls inside the per-district loop (they had run for the
  *last* district only). Composed **grove foliage** rendered partially (255 sub-particle cap → raise
  `DistrictMeshDensityBoost` to 32) and solid (opaque borrowed material → flip it to alpha-cutout, `_Mode=1` +
  `_ALPHATEST_ON`). Also fixed early in the session: the reactor's long-hunted "center rock" + ground twitch were
  **grafted footprint decals**, not terrain (filtered in `GraftFootprint`). Full write-up:
  [District-Dedicated-Visual.md](docs/District-Dedicated-Visual.md).

- **FOUNDATION PLINTH — planting on a cliff (2026-08-09).** A district on a coastal cliff/uneven tile
  overhung into empty air (the breeder reactor floated off the ledge). A bake-time **Foundation depth** knob
  now extrudes the building's footprint **straight down into the earth** (true world −Y, taken in drawn space
  post-rotation so it's independent of the model's import angles, then inverse-rotated back) as a solid
  concrete plinth — four walls + a floor, wound outward, cap omitted under the building. Districts render one
  atlas, so the plinth needs concrete *in* it: `AppendConcreteStrip` grows the atlas set by a fresh strip
  (noised grey albedo / neutral normal / rough concrete), slides existing content down and remaps the mesh UVs
  — **no existing texel is overwritten**. Purely bake-time: the runtime still gets one FxMesh + atlas. Two
  preview fixes rode along: re-point the preview material after the strip rewrites the atlas asset, and frame
  the camera on the **above-ground** building so plinth depth doesn't shift the view center. The health panel
  also stopped false-warning "typo" on **base-game (`Extension_*`) targets** — their definitions live in the
  game, not the project, so it stays silent (only a non-namespaced miss is a real typo). Verified in-game on
  the reactor's cliff tile. **Known limit**: a Z-fight shimmer where the plinth meets the building's own walls
  at map distance — measured to be **depth-buffer precision** (far from world origin under a huge far plane),
  not geometry; a small gap is invisible to the buffer and a large one shows a visible slot, so it's deferred
  with the shape intact (the fix path is to inset the plinth behind the building wall so a depth-beating gap
  hides).
- **HEXAGON SCULPTING — the raised platform (2026-08-09).** A district carves a raised terrain plinth
  (`UpdateHexagonSculpting` → `HexagonSculptingDefinition` → `ApplyHexagonSculptingDefinition`); a custom
  wonder's cell is empty, so the Oracle sat flat. The **fourth empty-cell fix**: a postfix forces a chosen
  index — per-entry Factory **Footprint** field + global config + a `haf_hexsculpt.txt` **live dial** (re-carve
  without relaunch, cycle ~40 shapes fast). Measured which shape to use: most districts resolve to `None`; the
  raised plinth belongs to the **emblematic quarters** (`Extension_Era1_OlmecCivilization` →
  `EmblematicAndCityCenter26`). Verified in 3D on the Oracle. Two honest limits documented: the **preview can't
  show it** (runtime terrain deformation, not baked geometry — judged in-game like PBR shading), and the raised
  platform is **not** the top-down **strategic-zoom footprint** (a separate render-mode path, still open).
- **GROUND MATERIAL — the maintained field (2026-08-08).** A district paints the terrain under it via
  `UpdateGroundMaterial` (a `(Biome × affinity)` → `GroundMaterialDefinition` resolve); a custom wonder's
  affinity has no row, so the Oracle stood on bare desert. The plugin postfixes the resolve and **forces a
  chosen ground index** — the game's own blended terrain paint, not a flat mesh. It's a **per-district** field
  in the Factory (dropdown of the game's vocabulary — grass / paved / sparse), with a global config fallback.
  Verified: `Prairie_Grassland` under the Oracle's temple and grove — the same empty-cell insight as the wonder
  visual, applied a third time, now to the terrain layer. The Factory **preview textures its tile with the real
  terrain image** — extracted from the game's shared `DefaultTextureAtlas` (resolve authoring data → atlas +
  element GUID → `GUIDToIndex` → `GetElementData` UV rect → crop the page tile → PNG per material), plus the
  material's true colour as a fallback — so the terrain-paint choice reads as real grass/pavement/sand before
  launch.
- **DE-ENC — framework filenames dropped the pack prefix (2026-08-08).** HAF is a universal framework, so its
  registry and tuning files shed the `enc_` badge of one pack: `enc_districts.json` → `haf_districts.json`,
  and likewise `haf_models` / `haf_formations` / `haf_sounds` / `haf_props` and the live-tuning `haf_*.txt`
  dials (177 references across 35 files, both repos + the git-tracked backups + the deployed data). The pack
  itself keeps its identity — `haf_packs/ENCReload/`, `pack.json`, its own skins/sounds — because that name is
  the pack, not a prefix.
- **DISTRICTS GO MULTI-INSTANCE (2026-08-08, verified with a second reactor).** A critical review of the
  district axis found its one architectural flaw: each registry entry held ONE component slot, overwritten by
  whichever district instance last refreshed — build the same district in two cities and ownership ping-ponged,
  only one tile showing the custom model. Fixed by splitting targeting from assets: each entry now tracks a
  **list of live instances** (added per `UpdateLevelBuild`, pruned via fake-null when razed) while the private
  leaf, layer clone, and texture bindings stay **one per entry, shared by every tile** — a leaf is just a
  material, and vanilla's shared selectors serve many channels the same way. The same review also flattened the
  per-frame hot path (cached reflection handles, cached texture bind slots — twenty districts now cost what two
  used to) and collapsed a drifted hand-rolled copy of the session reset into the canonical one.
- **SURFACE MAPS GO PER-ENTRY — the reactor regression (2026-08-08, same day).** The stability pass had bound
  flat neutral surface maps on *every* custom district; the temple then earned real baked maps, but the Breeder
  Reactor silently kept the neutrals — which turned its verified look (albedo over the donor silo's vanilla
  maps) into chrome domes and near-black walls, unnoticed for two days until its city was next visited. Fix,
  verified on both districts: entries with baked normal/rough atlases bind them; entries without keep the donor
  material's own maps. Lesson: a shared-code change verified on one district is not verified on the axis.
- **THE REVEAL-RAMP LEVER — wonders load complete (2026-08-08, same day).** Every session load replayed the
  bottom-to-roof level-build reveal on the custom wonder — vanilla plays the same ramp, the loading screen just
  hides it, and our swap necessarily lands after the screen lifts. Racing the loading screen was **falsified
  twice** (silent deadlocks — reaching for the render context from a plugin Update tick during the load
  sequence hangs the game with sync AND async loaders; LAW: never before `distFxManager` is tracked). The
  answer was a field dump away: `FxEvolverMaterialLevelBuildElement.fadeInOutMode {Stepped, Smooth, Instant}`,
  the appearance transition itself, encoded per element into GPU data. The wonder-path private clone sets
  **`Instant`** before its first Load — the temple stands complete the moment the tile renders. Open refinement:
  an `UpdateLevelBuild` event capture to keep the `Stepped` ceremony for wonders genuinely completed mid-game.
- **NATIVE WONDER VISUALS — the empty-cell revelation (2026-08-08).** One day after shipping, the Oracle's
  donor-district hack died of obsolescence. Three donor swaps failed in a row (Holy Site: bare tile; Natural
  Reserve: swap landed but drew nothing visible), so instead of donor roulette the visual-resolution chain got
  decompiled — and the "mod can't extend this" verdict of July collapsed: district visuals resolve through
  **criteria-matrix databases** whose rows are **plain datatable elements**, and completed wonders key their
  model **by wonder name** in a dedicated `ArtificialWonder` database. A `[RepoDump]` launch delivered the
  punchline — *our wonder's name was already indexed there, with a NULL guid*. July's `material 0,0,0,0` was
  never a dead end, just an **empty cell waiting to be filled**. Now `[WonderRow]` fills it (Temple of Artemis
  material as zero-bake proof + loaded template), the walker sources its swap template from the cell, and the
  proven isolate machinery does the rest: the Oracle renders its custom temple through the **game's own wonder
  pipeline** — native affinity, no donor anywhere, and the vanilla **bottom-to-roof level-build reveal** plays
  on the custom mesh after a reload. Donor laws measured en route (building-model + culture-agnostic families
  only; scatter families draw wrong; repository-fed families have no inline leaves) are kept in
  [docs/Wonder-Spike.md](docs/Wonder-Spike.md) as history.
- **THE ORACLE — first custom Artificial Wonder, shipped & announced (2026-08-07).** A Sketchfab Greek temple
  became a fully playable custom wonder in one arc: the district swap machinery carries `ArtificialWonderDefinition`
  unchanged (donor = a renderable district affinity; the *designed-for* native wonder affinity was measured a
  dead end — scaffolding-only material family, zero swappable leaves). Stability took a same-day triad, each
  mechanism measured: **streaming opt-out** (the private layer clone nulls its mid/hi-res material GUIDs so the
  reduction system can't stomp the injected albedo), **neutral surface maps** (the donor's bricks no longer bleed
  through), and a **session reset from the `Sandbox.Load` postfix** (save-reload had been re-pointing onto a
  corpse leaf → empty tile). Then the temple got its marble: **normal + roughness atlases baked with the albedo
  pack's exact rects** (the walls' albedo is pure white — the beauty was in the surface maps all along), area-average
  downsampling (a single bilinear tap aliases dense normals into rainbow static), relief calibrated into the data
  so preview and game agree. Card/small/tooltip portraits ride the standard UIMapper `Images` slots. Announced on
  Discord the same evening. See [docs/Wonder-Spike.md](docs/Wonder-Spike.md).
- **DISTRICT TEXTURES — the nuclear plant arc (2026-08-06).** Replacing the Breeder Reactor's model (a
  Sketchfab site-plan plant) turned one swap into three capabilities. (1) The **District Factory grew an
  embedded preview pane** — the baked mesh, textured, on a tile-sized ground square at the true in-game
  surface level, import angles live — after its first version *hid* a grounding bug by anchoring the ground
  to the model's own bottom. (2) That bug (the plant surfaced only its containment domes) exposed that the
  game plants the mesh by its origin and nothing re-grounds a rotated bake — the district bake now
  **auto-levels**: vertices shifted so the model lands lowest-point-on-the-surface *with its import angles
  applied*, any rotation combination stands level. (3) **Districts finally wear their own texture.** Three
  weeks of flat-shaded custom districts ended with two measurements: the district building layer is a
  **full-texture layer** (no atlas manager — leaves sample the layer material's bound sheet through mesh UVs,
  which is why an unbound custom mesh wore *patches of the culture's building sheet*), so texture is a
  per-layer binding — and `FxComponentRenderer.GetLayerIndexAddItIFN` **registers any output layer handed to
  it**. So the private leaf now brings a **private clone of the whole FxOutputLayer**: the game registers and
  loads it itself during the leaf's own Load, and the plugin binds the baked albedo on the clone's runtime
  materials. One tile, exact UVs, zero effect on every other building. A rect-painting design targeting the
  atlas manager was built first, falsified by the trace, and never shipped. See
  [docs/District-Visuals.md](docs/District-Visuals.md).

## Authoring tools

- **THE PIZZA BAKERY — multi-model districts (2026-08-08, verified: the Oracle's temple + a beech tree).** The
  District Factory composes MULTIPLE models onto one tile: parts bake with their own knobs, auto-ground to the
  base's floor, and merge into one mesh with super albedo/normal/rough atlases sharing one rect set — the
  runtime never learns the word "pizza" (one FxMesh + atlas trio per entry, so isolation/wonders/multi-instance
  compose for free). The dressing fought back three times, each measured: the multi-material pack
  **force-flattened alpha** (a=255) and the atlas compressed to **DXT1 (no alpha channel)** — cutout foliage
  baked as solid triangles until both were made alpha-aware; the v1 albedo-only compose dropped the temple's
  surface maps and the donor's maps **turned the marble blue** — super normal/rough maps with same-rect
  area-average blits brought it back; and the game's **shadow pass doesn't alpha-test**, so a dense leaf crown
  casts a soft solid blob (cosmetic, documented). The headline discovery: **the district shader honors
  alpha cutout** — card-foliage trees are first-class district dressing.
- **DISTRICT FACTORY HEALTH PANEL (2026-08-08, verified through its full lifecycle).** The review's last
  finding: the week's two costly failures — registry-vs-asset GUID drift and the stale mod bundle — plus
  July's data-prerequisite trap were all detectable at authoring time, and now they are. On selection, after
  every Bake, and on Re-check, the window compares every shipped GUID against the asset on disk (mismatch =
  red box instead of a silent "waiting for leaves" launch), the newest baked asset against the newest built
  Community assetbundle (bake → STALE BUNDLE warning → rebuild → clears), and the district definition's data
  (non-empty Additional Visual Levels = the guaranteed-empty-tile error; missing affinity = warning). One
  green line when everything agrees.
- **DISTRICT COMPASS ROSE + CORNER-FORWARD HEX (2026-08-08, verified in-game).** The district preview's tile
  hex was drawn edge-forward — the *unit* convention — but the in-game district cell presents a **corner**
  toward the model's forward (user-measured on the reactor). The shared hex builder gained an orientation
  parameter (units 30°, districts 0°) and the bake's hex-clip planes rotated to match the real cell walls. The
  facing arrow became a **NESW compass rose** — lines to all four cardinals, letters reading North-up — since
  a district has no facing of its own: what its author needs is map orientation, and the preview and the game
  now agree on it.
- **THE PREVIEW TRUTH ARC (2026-08-07).** Two days that turned the editor previews from bake-inspection aids
  into placement instruments a pack author can trust. The shared conventions, across the Model Factory,
  Animation Lab and District Factory panes: a **true-size tile hex** (6.93 across flats — the measured
  center-to-center tile spacing; the old ~10 square flattered every fit) pinned at the **origin plane** (never
  anchored to the model's bounds — that once hid a half-sunk district bake); **water-blue for boats** (the
  pawn's own Boat capability profile, never the name); a **forward arrow** (+Z, verified against the
  Jagdpanzer's barrel; edge-on, the six hex facings); **Center** re-frame and 2× deeper zoom. The Factory and
  the Lab now share the faithful **rest-pose FBX view** for animated entries — attempt 1 force-reimported the
  shared FBX and scrambled tiling-UV preview textures (reverted, root-caused, re-attempted read-only:
  VERIFIED). The arc's crown was the stealth helicopter's centering: the hex made a years-invisible off-center
  bake obvious, which exposed that **Position offset was silently dead on the animated path** — now applied in
  the rig conversion in true **game units** (pre-divided by the FBX import's `size/longest` factor that used
  to multiply the dial ~3×) — and that **donor-clip models are re-anchored by the donor rebase** (in-game
  position ≠ FBX position; three launches burned misreading placement over a district tile). Previews now show
  donor-clip entries **footprint-centered** — the measured approximation, ±0.5 units on the helicopter, honest
  caption included; exact rebase-in-editor prediction is the documented open end. See
  [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md) and [docs/Editor-Tools.md](docs/Editor-Tools.md).
  **CORRECTION (same day, user-caught):** the animated-path offset bake and the donor-clip approximations were
  built on a **doubled signal** — the plugin had applied the registry `position` at RUNTIME all along
  (`ApplyPositionOffset`, per frame, pawn frame, game units), so baking it too made every animated model carry
  the offset twice: the helicopter flew at *exactly 2×* its dialed height, and the "calibration" launches were
  fitting multipliers to runtime + bake in two different frames. The user's arithmetic (halving the dial restored
  the exact old height) exposed it. Unwound: bake-side application removed, footprint-centering removed, previews
  draw the **runtime offset live** — one dial, one application, no re-bake to nudge a model. The lasting morals:
  *grep for a runtime consumer before resurrecting a "dead" knob*, and *a knob that seems to need calibration
  usually has two writers*.
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
