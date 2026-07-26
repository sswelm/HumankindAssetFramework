# What kinds of animated models can HAF import?

The short answer: **more than the community thinks is possible.** The public consensus is still "anything moving is
not possible" in Humankind modding — HAF has shipped a spinning-prop drone, a folding/firing howitzer, and a full
humanoid character (a raw Sketchfab auto-rig) that **idles standing and runs while moving** as working in-game units.

This page is the plain-language front door. If you just want to know whether *your* model can work, read this; the
deep technical treatment lives in [Factory-Manual.md §16](Factory-Manual.md) (how the conversion works) and
[Animated-Runtime.md](Animated-Runtime.md) (how the engine plays it back).

## The one engine rule that shapes everything

Humankind's animation engine plays **bone rotations only**. Clips that move bones by *position* (location keys)
don't survive; scale animation doesn't either. Everything HAF does for animated models is about getting your model's
motion expressed as pure rotations — automatically where possible.

## The three levels

### Level 1 — Clean, purpose-made rigs *(easiest: works out of the box)*

A model with a proper armature, a sane rest pose, and rotation-driven animation — typical of models authored by an
actual rigger, or anything you rig yourself.

- **Examples shipped:** the ReconDrone (spinning propeller loop).
- **What you do:** pick the model and the clip in the Factory / Animation Lab, Bake. Done.
- **Settings:** "Convert raw rig" **OFF**. "Fix 100× oversize" per model (tick it if the bake comes out ~100× too
  big — the Lab auto-suggests it for GLB files).

### Level 2 — Rigid-part animations *(vehicles, artillery, machines)*

Models animated by **moving separate parts** (nodes) rather than a skinned skeleton — a howitzer's folding trail
legs, landing gear, a crane, turrets. Very common for Sketchfab vehicles.

- **Examples shipped:** the TowedGunHowitzers — folds for travel, deploys when it stops, recoils when it bombards.
- **What you do (2026-07-19, fully recipe-driven):** point the entry's Model file at the **raw original** and tick
  **"Deploy conversion (rigid-parts source)"** in the Animation Lab. The bake then runs the converter automatically
  (cached; re-runs only when a knob, the source, or the tool changed) and generates **ready-made state clips** —
  `deployed` / `folded` / `unfold` / `fold` / `recoil` — cut from two frame numbers you provide (deploy start/end,
  found by scrubbing the raw file in the ▶ clip picker; recoil range likewise). Assign them to the state-driven
  roles, Bake. Nothing is hand-run; the whole pipeline reproduces from the registry entry. The legacy hand-run
  `Tools/deploy_convert.py` invocation still works but is no longer the recommended path.
- **THE authoring law — the engine's clip bake is ROTATION-ONLY.** Baked clips keep per-bone rotation and DISCARD
  per-bone translation. Any part whose source motion *translates* must be re-expressed as rotation, or the game
  plays it pivoting about the wrong point (the M114's trail legs — which spread by rotation **plus** a slide —
  swept inward/under instead of out; a preview can look perfect and the game still mangles it, because previews
  play the full curves). Two converter knobs exist precisely for this:
  - **Leg spread scale** — empty keeps the source leg curves verbatim (fine only for purely-rotational legs);
    a number re-keys `*leg*` parts as a clean travel→spread pure rotation (`1` = full source width — what the
    proven howitzer uses; `0.5` = half as wide). If a sliding part isn't named "leg", rename it or expect drift.
  - the hidden far-pivot **"RecoilArm"** — fakes the barrel's recoil slide as a long-arc rotation automatically.
- **Skinned vehicles with spinning parts (wheels, turret, rotor-on-bone):** the Ehrhardt armored car (Era5
  Armoured Car) is a purpose-made **skinned** rig — 4 wheel bones + a turret bone — whose **wheels spin in place
  while moving and are still when parked** (state-driven: Idle = a held frame `Spin[0..0]`, Movement = a slice
  `Spin[5..15]`). Author the spin in Blender by rotating each wheel bone about its own axle axis (LINEAR = seamless
  loop). **The non-obvious trap:** rotating bones **fling off in-game** on the legacy path even though the rig is
  clean and previews perfectly — the metre→centimetre export sandwich. Bake such a rig with **Convert raw rig ON +
  Fix 100× oversize OFF** (full explanation: [Animation-Pitfalls](Animation-Pitfalls.md) → "the rotating-bone
  fling"). Sit it on the terrain with the **Auto-ground (sit on terrain)** toggle — the bake drops the tyres to the
  skeleton origin, self-correcting and **size-proof** (no manual Position-offset dial, and it stays grounded if you
  change Size). Verified end-to-end on the Ehrhardt.

#### Authoring the spin rig — the Vehicle Lab (automatic) or by hand

**Automatic: `Tools ▸ HAF ▸ Vehicle Lab` — VERIFIED end-to-end 2026-07-25; the shipped ArmouredCar now runs a
Lab-generated rig.** Browse the static model → **Probe parts** (headless Blender lists the mesh parts; a single
combined mesh is split into loose parts; roles auto-guessed from names) → mark the **Wheels** (and Turret) →
**Vehicleize**. The GLB path lands on your clipboard; bake settings are printed on success. The review scales to
real game-rips (the Ehrhardt probes into 3,350 shards):

- **Review UI** — click a row to zoom + yellow-highlight that part in the turntable; **↑/↓** walk the list;
  **W/T/B/I** mark Wheel/Turret/Body/Ignore, **C** = Caterpillar (tread loop — see the treadize section below),
  **G** = Gun (one bone for the barrel assembly, rides the Turret when there is one), **D** = Default
  (undecided), **E** = Edgecase ("not sure, revisit later" — rigs static like Body and stays visible in the
  undecided filter). Four hide sliders (min verts, min
  size, height-below, height-above — the height pair brackets a horizontal slab: turret-only or chassis-only
  views) plus a **Show only** classification filter with auto-advance (marking a part out of the active filter
  steps straight to the next row).
- **Recipes** — Save/Load the whole configuration (source, output, per-part roles, knobs) as JSON; all window
  state also survives domain reloads, so a recompile can never eat a marking session, and a saved recipe
  reproduces the rig exactly.
- **Verify** — a non-blocking report that previews the *exact* wheel bones a Vehicleize would build (same
  clustering as the rig script) and flags stray clusters, axle disagreement, unpaired wheels, turret outliers
  and undecided leftovers; every flagged part has a **Show** button that jumps both preview and list to it.
- **The generated rig** — wheel parts **cluster per hub**: the biggest part (the tire) anchors each wheel — its
  bbox center is the axle point, its thinnest extent the axle direction — and every member shard (spokes, rim,
  bolts) skins to that ONE bone, so off-center wheel furniture is safe to mark Wheel *by design*. All Turret
  parts share a single `Turret` bone. Shards are then **joined to one mesh per bone** (a 3,350-object GLB times
  out the bake's Blender step; 6 meshes fly), the source file's own stowaway skeleton/helpers are stripped
  (`SKM_` rips carry one), and the LINEAR `Spin` action exports as `<name>_Spin.glb` with a turntable preview
  playing the spin. Part lists pass via `@file` (hundreds of names overflow the Windows command line).

**Generated-rig conventions (they differ from a hand rig):** bones are `Root`, `Wheel_00…`, `Turret`. The **spin
sign depends on the nose direction** — +360 = forward for a +X-nosed model; check the preview and negate if the
wheels roll backward. At bake/runtime: **turret aim axis = Y** (the bone is built tail-up), `socketBones` /
`muzzleBone` must reference `Turret` (a config naming a missing bone fails the bake loudly, listing the rig's
bones), and the muzzle offset is re-dialed from the turret's center — registry-only, so each iteration is
Save (no bake) + relaunch.

**If the rip is already rigged — the SKM fast path (built 2026-07-25):** the probe detects an armature with
≥90% of vertices weighted (`SKM_` prefix is the tell) and the Lab flips into **bone-marking mode** (a toggle;
on by default when detected): the list shows the source skeleton's deform bones with their weighted-vert counts
and bounds, wheel-named bones pre-marked, and **Vehicleize (fast path)** authors the Spin action directly on the
marked bones — per bone the local axis closest to the world axle, signed so mirrored left/right bones turn the
same world way — shipping the artist skeleton unchanged (pivots, weights, weapon/socket bones like the
Ehrhardt's four `MW_*` mounts all kept, so the hand-rig-era fire-effect calibration applies verbatim). The
honest trade: the fast path **inherits the artist's weighting, good and sloppy** — on the Ehrhardt the original
artist weighted the front steering knuckles to the wheel bones, so they visibly rotate with the wheel ("bumping"
axle). When that matters, toggle the fast path off: the shard flow lets you decide every part's fate, which is
why the shipped ArmouredCar runs the shard-path rig.

#### Caterpillar tracks — treadize (path-instanced rigid links, 2026-07-26)

Tanks and halftracks add a part no wheel bone can carry: the **tread loop**. Mark it **C (Caterpillar)** in the
review (the gun barrel gets **G (Gun)** — one bone, parented to `Turret` when there is one, else `Root`;
casemate guns like the Jagdpanzer hang off Root). Vehicleize then builds the tread the way the industry's
"curve/path-based instancing" recipe does, translated to bakeable skeletal form:

1. **Link pitch is measured from the mesh** — circular autocorrelation of the cleat x-positions along the
   bottom run finds the physical link length (Jagdpanzer: 0.498) and its strong sub-grids.
2. **Long tread edges are subdivided** (shape-preserving midpoint cuts) so the low-poly band can articulate.
3. **The loop path is constructed analytically** — the classic *belt around pulleys*: the wheel centers plus
   the tread-band radius measured at each wrap wheel (sprocket, idler, ramp-end road wheels, return rollers)
   joined by external tangents and wrap arcs. Exact straights, exact arcs, immune to concave loops — every
   approximation tried first (θ-around-centroid, radius smoothing) failed on the raised idler's concavity.
4. **The loop is cut into HALF-LINK cells** along the path, at the cut phase crossed by the fewest mesh edges
   (so hinges land in the cleat gaps, not through cleats). **Every cell gets its own bone** — no skin blending
   anywhere; each molded link piece is 100 % one bone and moves rigidly.
5. **Every link bone is keyed riding the path** (location + rotation per frame). Advance = two cells = **one
   full link per loop**, so the loop restart maps link-onto-link (invisible) and the tread surface speed
   roughly matches the sprocket's — no visible tooth slip.

**Why rigid links:** a continuous-band skinning (blended carrier bones) was driven through eleven refinement
rounds — measured tears fell 0.43 → 0.03 — and *still* read as a loose rubber hose, because molded links
visibly bending IS what the eye calls slack. Real tracks (and the vanilla pair/impair treads) are rigid links
articulating at pins; only instancing reproduces that.

**Bake requirements:** `Keep bone translations` **✓** (the links are translation curves — without it the tread
freezes), Convert rig ON, Fix 100× OFF, Auto-ground ON, Idle `Spin[0..0]`, Movement `Spin[1..15]`. The
LARGEST wheel (drive sprocket) keeps the user's spin degrees (pick one matching its spoke symmetry — 60° for
a six-spoke — so its restart is invisible); every other wheel gets **rolling-contact speed** (angular speed ∝
1/diameter — small road wheels genuinely spin faster), snapped to the nearest multiple of its OWN spoke
symmetry (detected per wheel by angular autocorrelation of its rim verts) so all restarts stay invisible.
Bone budget: half-link cells put the Jagdpanzer at ~156 bones; a further halving would break Amplitude's
256-bone cap.

**Status:** preview-verified through headless renders + a per-link displacement probe (all 54 links move
identically — zero outliers); the TankDestroyers bake and in-game check are the next step.

**By hand** — the recipe the tool automates (still worth knowing when a model needs judgment):

1. **Import** the static model (`File ▸ Import`). Delete junk (stray spheres, ground planes).
2. **Add an armature** (`Add ▸ Armature`), enter Edit Mode on it, and create one bone per moving part:
   a **Root** at the origin, one bone per **wheel** (head at the wheel's CENTER — snap the 3D cursor to the wheel
   mesh: select it, `Shift+S ▸ Cursor to Selected`, then in the armature `Shift+A` a bone there), and a **Turret**
   bone at the turret ring if there is one. Parent wheels/turret bones to Root (in Edit Mode: select child, then
   Root, `Ctrl+P ▸ Keep Offset`).
3. **Name the bones** what you'll reference later: `Root`, `Wheel_F_L`, `Wheel_F_R`, `Wheel_R_L`, `Wheel_R_R`,
   `Turret`, `MW_T`… (these names are what `turretBone`/`muzzleBone`/`socketBones` substring-match).
4. **Skin rigidly** — no weight painting: select a wheel MESH, then the armature, `Ctrl+P ▸ Armature Deform`
   (empty groups), then in the mesh's Vertex Groups panel add ALL its vertices to its wheel-bone's group at
   weight 1. Repeat per part; everything that doesn't move gets full weight on `Root`. (Separate loose parts
   first if the model is one mesh: Edit Mode, hover a wheel, `L` to select linked, `P ▸ Selection`.)
5. **Author the `Spin` action**: Animation tab, new Action named `Spin`. Frame 0: keyframe every wheel bone's
   rotation at 0 (`I ▸ Rotation`). Frame 15: rotate each wheel bone **about its axle axis** (usually local X —
   `R X X` then the angle) by e.g. `-360°` and keyframe. Set ALL keyframe interpolation to **LINEAR**
   (select keys in the Dope Sheet, `T ▸ Linear`) — constant speed = a seamless loop when sliced.
   Frame 0 is deliberately the rest pose: `Spin[0..0]` becomes the motionless Idle.
6. **Export GLB** (`File ▸ Export ▸ glTF 2.0`), include the animation.
7. **Factory/Lab**: Animated + State-driven, Idle/reference `Spin[0..0]`, Movement `Spin[5..15]` (or any slice —
   the speed step controls apparent speed), **Convert raw rig ON + Fix 100× OFF** (the rotating-bone fling trap,
   see Pitfalls), **Auto-ground ON**. Bake.

The wheel-spin *rate* never needs to be physically right in the source — slice steps (`/N`) tune it at bake, and
the wheels only play while moving anyway.

### Level 3 — Full character rigs, including messy auto-rigs *(the breakthrough)*

Humanoids and creatures with real skeletons — including **auto-rigged downloads whose rest pose is scrambled** and
whose clips assemble the body every frame with location keys (typical of Sketchfab auto-rigs; these are unplayable
in the engine as-is, which is where the "not possible" consensus came from).

- **Examples shipped:** the Combine soldier (62-bone ValveBiped) — stands, turns with movement, **idles standing,
  runs while moving**, attacks.
- **What you do:** tick **"Convert raw rig"** in the Animation Lab and bake. The conversion is automatic: it makes
  the clip's first visual pose the new rest pose, re-derives the entire animation as pure rotations, renames bones
  so the engine's sorting can't scramble them, and exports unit-clean (usually with "Fix 100×" OFF).
- **If the result is mis-oriented:** add a Rotation and probe one axis at a time — and judge **in-game**, not in the
  preview (the preview's orientation is meaningless for animated models).

## State-driven characters (idle / run / after-move / combat / attack)

A character can play **different clips per state**: tick **"State-driven"** in the Animation Lab and pick
an **Idle clip** (plays standing), a **Movement clip** (loops while the unit travels — a run cycle), and optionally
an **After-movement clip** (played once on stopping, then back to Idle), an **Attack clip** (played when the unit
fires a ranged attack — the runtime hooks the game's own per-pawn fire sequence, so the exact shooting pawn
animates in battles and bombards alike), and a **Combat-idle clip** (a weapon-raised stance that replaces Idle
while the army is locked in a battle, from deployment to resolution — a single-frame pose clip works and is
auto-padded at bake time). Priority: attack > movement > after-move > combat-idle > idle.

Source clips are often authored as a single trigger-pull pop (the soldier's `shootAR2s` is 0.17 s) — the sim fires
once per attack, so at face value that's a blip. The **Attack repeats** slider replays the clip N times per trigger
(window = N × clip duration; 18 ≈ 3 s of sustained fire) and is **runtime-only**: change it, *Save (no bake)*,
rebuild the mod — no re-bake.

**Stance idles & pacing (artillery — 2026-07-19):** two rules make a deploy-style unit work, both data-only.
(1) The primary clip is the **reference** — keep the FULL motion there and put the deployed hold in
**Idle stance (override)** (`deploy[179..180]`); a stance baked as the primary renders as the travel pose in-game.
(2) Pacing is a **slice speed step** — `deploy[179..0/12]` folds at 12× (~0.6 s); an *empty* Pre-movement clip is
the legacy instant snap. The full worked recipe and every trap behind these rules:
[Animation-Pitfalls.md](Animation-Pitfalls.md).

All clips come from the same model file and bake against **one shared skeleton** in a single pass — pick, bake,
done. This is what makes a humanoid read as a *unit* instead of a statue gliding across the map.

### Clip slicing — one long clip, many states

Any clip field accepts a **frame range**: `deploy[0..180]`. The slice is cut from the source clip at bake time —
no Blender work needed. `start > end` plays the segment **reversed** (a fold from an unfold); a single frame
(`deploy[180..180]`) becomes a **held stance**. Many downloadable models ship one long clip containing several
motions in sequence — slicing turns that single timeline into a full state set.

**Worked recipe — artillery on one clip** (a deploy timeline `0..180` with a recoil tail `180..250`):

| State | Clip spec | Meaning |
|---|---|---|
| Idle | `deploy[180..180]` | held deployed stance |
| Movement | `deploy[0..0]` | held folded/travel stance |
| Pre-movement | `deploy[180..0]` | folds when it starts moving (reversed) |
| After-movement | `deploy[0..180]` | unfolds when it stops |
| Attack | `deploy[180..250]` + Attack repeats 1 | the recoil kick on fire |

Plus **Clear aim layer (artillery) ON** — vehicle/artillery donors stream aim & wheel junk into the game's
procedural bone layer that must be cleared (characters leave it OFF; that layer carries their facing).

## What this unlocks

New infantry, animated creatures, robots, crewed artillery, fantasy units, nonstandard skeletons — anything whose
motion can be expressed (or re-expressed) as bone rotations.

## Current limits, honestly

- **Translation *motion* can't play** (engine rule). Sliding parts need the far-pivot rotation trick.
- **Multiple armatures in one file:** only the first is used.
- **Morph targets / shape keys** aren't supported on the conversion path.
- Keep models to a sensible triangle budget (the "Reduce to ~tris" field; ~24k default) — the whole roster shares
  one GPU pool ([Vertex-Budget.md](Vertex-Budget.md)).

## "Is it my model or the pipeline?"

Run `Tools ▸ HAF ▸ Tests ▸ Bake Conversion Gate Test (litmus)`: it synthesizes a known-good rig, bakes it through
the full pipeline, and verifies every engine invariant. If the litmus passes, the pipeline is fine — the problem is
in your model, and the symptom table in [Factory-Manual.md §16.5](Factory-Manual.md) maps what you see in-game to
what's wrong with the rig.
