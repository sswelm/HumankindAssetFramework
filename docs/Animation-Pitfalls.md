# Animation Pitfalls — a field guide to the woes

Every trap in this document was hit for real, cost hours, and is now either fixed in the tooling or has a
one-field recipe answer. Read this BEFORE debugging an animated model that "looks wrong": the odds are your
problem is on this page, and the odds are the first three explanations you'll think of are not the cause.
(Case study behind almost every entry: migrating the M114 howitzer from the legacy fire/deploy behaviors to the
state-driven machine, 2026-07-19 — a day in which the model file was accused of corruption, three preview
renderers were rewritten, and the actual causes turned out to be four engine constraints nobody had written down.)

## The four laws

These are engine-level facts. They are not bugs, they cannot be patched away, and every recipe must respect them.

> **REVISION 2026-07-25 — Laws 1 and 5 are OUR PIPELINE'S defaults, not engine walls.** The caterpillar
> investigation decompiled the runtime: the clip format has `RotationTranslation`(+Scale) encodings, vanilla
> tank clips use them liberally (tread shuttle bones, gun recoil), and `GetPoseTRS` zeroes translation ONLY for
> Rotation-encoded curves. Our bake historically (a) keyed rotations only in the conversion rebake and (b)
> stripped every location fcurve. Both now have an opt-out: the per-model **`keepTranslations`** flag
> (Animation Lab ▸ "Keep bone translations", conversion path only) keeps genuinely translation-animated bones —
> **verified end-to-end in-game** (a translating test bone baked as `RotationTranslation` and played with
> CORRECT amplitude; the clean-unit conversion export sidesteps the native-scale trap that motivated the
> strip). Laws 1/5 below remain the DEFAULT behavior and stay true for every model that doesn't opt in; the
> re-express-as-rotation recipes remain valid and battle-tested.
>
> **Worked example — the M114's REAL kickback (2026-07-26, in-game verified):** `keepTranslations` +
> Recoil frames `442..530,305..441/2` + Return slow 0 + Slam 0 plays the source's authored fire cycle complete:
> translation slam, slide home, reload, aiming raise (multi-segment epilogue). Implementation notes that matter:
> translations are kept ONLY in the attack-role clip (kept elsewhere they double-render pose offsets rotations
> already cover — the hovering-gun symptom), delta-rebased to zero at the clip's first frame (pure motion), and
> ×100-compensated on the legacy path (the m→cm sandwich folds 0.01 into bindposes, which Amplitude carries into
> translation curves while rotations pass scale-free — the exact mechanism behind the original Law-1 evidence).
> Also fixed en route: the slam-0 sentinel placed the RecoilArm pivot at 1e9, collapsing bone chains via float32
> cancellation — the source of every NaN/garbage-import symptom this pipeline ever showed.

### Law 1 — The clip bake is ROTATION-ONLY *(default — see the 2026-07-25 revision above)*
The engine's baked clips keep per-bone **rotation** and **discard translation** (`GetPoseTRS` forces
translation 0, scale 1). Any part whose source motion *slides* plays pivoting about the wrong point in-game.
- **Symptom:** a part sweeps through/into the model in-game (the M114's trail legs crossed inward) while every
  preview — Blender, the ▶ picker — looks perfect. Previews play full curves; the game doesn't.
- **Answer:** re-express the motion as pure rotation. For deploy-converted models that's one recipe field
  (**Leg spread scale** re-keys `*leg*` parts; the hidden far-pivot **RecoilArm** does it for barrel slides).
  For other rigs: author the motion rotationally, or accept the drift.

### Law 2 — A stance baked as the PRIMARY clip renders as REST
The primary clip defines the skeleton's **reference pose**; clip data encodes *against* it. Bake a 1–2-frame
stance as the primary and it encodes ~identity — in-game the unit shows the **rest pose** (usually the travel
pose) no matter what the stance was.
- **Symptom:** idle shows the travel/fold pose ("it forgot to deploy") although the stance clip previews
  correctly everywhere else. The byte tell: the baked `*_ClipsPoseData.bytes` rows are near-constant identity.
- **Answer:** the primary (Idle/reference clip) must be the **FULL source motion**; the stance goes in
  **Idle stance (override)** — a role clip, encoding real deltas against the full clip's reference (e.g.
  `deploy[179..180]`). This is why the Lab has two idle fields.

### Law 3 — Pacing is BAKED, never a runtime knob
The runtime plays every clip at its authored length (24 fps). A 7.5 s authored fold outlasts a one-tile map
move — the unit spends the whole trip in the fold's first frames ("it forgets to fold").
- **Answer:** the slice **speed step**: `deploy[179..0/6]` = every 6th frame = 6× faster (≈1.25 s). The step
  always lands exactly on the end frame. `/180` on a 180-frame motion degenerates to a 2-frame near-snap; an
  **empty** Pre-movement clip is a true instant snap (which is all the legacy howitzer ever did — its
  "instant fold" was the *absence* of a fold animation).

### Law 5 — Bone POSITIONS are pinned at the bind pose; only ROTATIONS animate *(default — see the revision above)*
Stricter than Law 1, discovered by experiment (Arc-R scaling test): the engine keeps every bone at its
bind-pose position and plays only orientations through the hierarchy. Consequences:
- a whole-body lurch (the M114's carriage recoiling backward) cannot be expressed in a clip at all;
- the far-pivot "RecoilArm" arc — designed to fake a slide as rotation — renders as a **small in-place pitch**
  (the tube tilts by θ instead of swinging R·θ along the arc). The beloved legacy "kickback" was always exactly
  this modest pitch, read at map zoom; up close it looks like a nose-dip. Judge fire effects at PLAYING distance.
- the practical kick recipe: slam-only recoil range (`445..451` on the M114) + the palindrome **Return slow**
  (the same frames played backward slowed, gliding home). Whole-body motion would need a runtime ObjectSpace
  nudge (possible, unbuilt).

### Law 4 — What a preview shows is NOT what the game plays
Three different things can lie to you independently:
- a **custom editor renderer** can corrupt the view itself (two hand-rolled BakeMesh draw paths each mangled
  the M114 — un-mirrored legs, then giant parts — while the data was provably fine; the ▶ picker now renders
  the real instance through Unity's own pipeline for exactly this reason);
- the **▶ picker is a raw player** — it deliberately shows the source's FULL curves (translations included),
  so on translation-driven parts it will look *better* than the game (Law 1);
- **in-game is the only truth** for the final bake — and even there, remember the unit may be standing on a
  slope, mid-state, or showing a donor-layer artifact.

## The method: sandbox + gate

What finally broke the debugging loop was not a cleverer theory — it was a **measurement protocol**:

1. **Sandbox on a donor unit.** Never iterate on a shipping entry. Clone the recipe onto a throwaway unit
   (the SiegeHowitzersCar donor) so every failed bake costs nothing and the working entry stays as reference.
2. **Gate against the proven bake.** When a "should-be-identical" bake misbehaves, don't stare at the screen —
   **diff the artifacts**:
   - `Assets/Resources/<name>_ClipsPoseData.bytes` — byte-compare against the proven model's file. Identical
     prefix + divergence at frame N is a *location*, not a mystery (a scene-state leak was found at frame ~103
     this way).
   - slim FBXs — sample both in headless Blender and diff evaluated bone poses per frame (the gate scripts:
     load both, `frame_set` sweep, compare world bone heads + local quats; PASS = 0.0000). The whole Blender
     stage can be gated **without ever opening Unity or the game**.
3. **Change ONE delta per bake.** The sandbox failures compounded exactly when several knobs moved at once
   (settings drift + stale caches + new file). The gate tells you which delta mattered.
4. **Bytes over eyes.** Every "the file is corrupt!" accusation this day was wrong; every byte/pose diff was
   right. Measure chirality, don't eyeball crossed legs; measure pose rows, don't squint at stances.

## Symptom index

| Symptom (in-game unless said otherwise) | Cause | Fix |
| --- | --- | --- |
| Part sweeps through the model; previews fine | Law 1 (translation stripped) | re-key as rotation (legScale / RecoilArm) |
| Idle shows travel pose; stance previews fine | Law 2 (stance-as-primary) | full clip as Idle/reference; stance in Idle override |
| "Forgets to fold" — travels deployed | Law 3 (fold outlasts the move) | speed step `/6`…, or empty Pre-move = instant snap |
| Fold/unfold plays but glacially | Law 3 | speed step |
| Unit invisible | wrong animation id (invisible ⇒ id, frozen ⇒ constant data — the diagnostic dichotomy) | check `[Uni] clip` log lines resolve |
| Unit frozen mid-pose | constant clip data (hollow bake) | byte-check PoseData; re-slim (cache busters below) |
| Whole unit tiny/huge/floating | FBX unit scale | **Fix 100× oversize** per model |
| A **rotating** part (wheel) flings off / orbits in the air while the body sits still; idle fine, only *movement* flings | the m→cm ×100 export **sandwich** mangles rotating bones' TRS (and **Fix 100× ON re-creates it**) | **Convert raw rig ON** (cancels the ×100) **+ Fix 100× OFF** — see below |
| Baked skin scrambled on one part (wheel) | multi-material albedos missing — the animated path now generates them (glbconv) but a failed extraction falls back to a single atlas, loudly | check `[glbconv]` Console errors, re-bake |
| Preview (custom window) shows mirrored/giant parts | Law 4 (renderer bug) | render real instances (`AddSingleGO`), never hand-rolled BakeMesh draws |
| Settings revert / edits ignored after compile | stale window form (survives domain reload) | the Lab re-syncs on reload + **↻ Reload** button; registry file is the truth |
| A knob change bakes identical output | stale slim cache — **edits made through the Lab re-slim automatically**; edits made directly to the registry file behind an open window do NOT (the cache compares form vs file) | ↻ Reload first, or delete `anim*/…_anim.fbx` |
| Crossed/wrong limbs in a stance ROLE clip (historical) | role slicing leaked pose values into channels the primary doesn't key | fixed: slicing saves/restores all pose bones (`rig_anim.py`) |
| Same bake differs run to run (historical) | export-time pose was whatever frame the tool last touched — it becomes the engine's reference | fixed: every export pins the scene to the clip's first frame |
| Whole gun pitches/dives when firing (close zoom) | Law 5: the arc kick IS a pitch; it reads as a dive only nose-to-the-glass | judge at map zoom; tune via recoil range/Return slow |
| Attack plays stale/old animation after a recipe edit (historical) | Blender exits 0 even when the conversion script CRASHES — the baker reused the old converted GLB and recorded the bad args as success | fixed: success = the script's own final marker; reversed recoil ranges rejected with a clear error |
| Aim-layer suspicion during fire | the donor streams runaway angles (5000°+) — but at the INVALID bone index sentinel (0xFFFFFFFF): applied to nothing | exonerated; a throttled `[Aim]` log in ClearAimLayer shows what streams |
| Model collapses flat onto the root, limbs flung (mech) | rig has NO skin weights — parts rigidly bone-parented; the join drops the binding, all verts fall to bone #0 (Unity warns) | fixed: conversion path converts bone-parenting to full-weight vertex groups (`rig_anim.py`) |
| Skeleton ~100× off the mesh, rigid parts become a "wing" | wrapper empty with non-identity scale (mech: 0.010) survives export; Amplitude reads bind poses without it | fixed: conversion path flattens wrapper empties before `transform_apply` |
| Huge stretch spikes in-game, Blender preview fine (detailed rig) | over Amplitude's 256-bone GPU skinning cap (mech: 332 bones) — verts on bone index >255 get garbage | fixed: zero-weight leaf bones removed to ≤240 (weighted bones untouched) |

## The rotating-bone fling — the metre→centimetre sandwich

Case study: the Ehrhardt armored car (first custom **spinning-wheels** vehicle, 2026-07-24). Wheels attached and
still at idle, but the moment the movement clip **rotated** them they flew off and orbited through the air while
the hull stayed put. The same class of bug as the Combine soldier whose "head rode off his shoulders."

**Why:** Blender's FBX exporter writes metres→centimetres by scaling the ROOT objects **×100**. Unity compensates
with **0.01 in every skinned-mesh bindpose + a ×100 root** — a *sandwich* Amplitude's uniform-scale TRS
composition mangles on any bone that **rotates** (a static bone composes fine; a rotating one orbits about a
mis-scaled pivot). That is why **idle looked perfect** (0° rotation) and only movement flung. The ▶ picker and
Unity preview also look perfect — they use a clean import, not the sandwiched bake (Law 4).

**The cruel part:** the size fix and the fling fix pull in opposite directions on the *legacy* path.
- **Fix 100× oversize (`animUnitFix`) ON** → correct render size, **but keeps the sandwich** → wheels fling.
- **Fix 100× OFF** → no sandwich, **but the model bakes ~100× too big** ("too large to see").

**The answer is neither toggle — it's `convertRig`.** The conversion path exports with `global_scale=0.01`, which
**cancels the exporter's ×100** at the source (`rig_anim.py` ~L691-699): net node scale 1, UnitScaleFactor 1, bind
clusters 1 — the clean profile. So a rotating-bone rig bakes correct **and** grounded with:

> **Convert raw rig ON  +  Fix 100× oversize OFF.**

This **overturns** the old "convertRig OFF for clean purpose-made rigs" guidance: a purpose-made rig with rotating
bones (wheels, turret, propeller-on-bone) still needs convertRig ON, *unless* its source file happens to carry a
0.01 object scale that already cancels the ×100 (the ReconDrone's luck — which is why the drone bakes fine OFF).
When in doubt for a rig with any spinning part: **convertRig ON**.

**Grounding — the animated path has no automatic keel→z=0 (only the static path does), so a vehicle whose tyres
stick out below the hull sinks.** Two ways to sit it on the terrain:
- **Auto-ground (sit on terrain)** toggle — *the hands-free way.* The bake drops the model's lowest point (the
  tyre contact) to the skeleton origin (lift by `−minZ`). It's **self-correcting** (a raw file lifts fully, an
  already-grounded one lifts ~0 → can't double-apply) and **size-proof**: the shift is in model space, so the bake's
  `globalScale = size/longest` scales it automatically — change Size and it stays grounded. (An earlier attempt used
  a "wheels-on minus wheels-off" *protrusion* measure — a fixed lift that FLOATED an already-grounded file; keel→
  origin replaced it.) Verified on the Ehrhardt: model-space lift 0.671 × size-scale (4/6) ≈ 0.45 in-game, matching
  the hand-dialed 0.42. OFF for a flyer/hover model (it would be pinned to the ground).
- **Position offset Z (waterline)** — the manual/runtime knob, applied at **spawn by the plugin**
  (`ApplyPositionOffset`: `ObjectSpace.Translation.y += z`), the same one you use for drone/aircraft height. It's in
  **in-game units**, so it does NOT scale with Size (a value dialed at Size 4 is wrong at Size 5). Use it for hover
  height, or as a small fine-tune on top of Auto-ground — Save + relaunch, no re-bake.

## Turretize — aim a turret (or artillery barrel) at the target

The game already computes the aim and streams it as a HEADING angle into a `PawnEntry.BoneRotation0-3` slot
(`{SkeletonBoneIndex, AxisIndex, Angle}`) — but on an injected model that slot's `SkeletonBoneIndex` is the invalid
`0xFFFFFFFF` sentinel, so it drives nothing. **Turretize retargets that slot to your turret bone**, so the engine's
own aim math rotates it — no per-frame trig.

- **Setup (runtime, no re-bake):** Animation Lab → **Turret bone** = a bone-name substring (e.g. `Turret`; the
  plugin substring-matches it against the renamed `b###_<orig>` bones) → **Turret aim axis** → **Save (no bake)** +
  relaunch. Verified on the Ehrhardt armored car (first custom unit with an aiming turret).
- **THE gotcha — the axis is per-model, and the game's default reads as PITCH, not YAW.** The streamed channel is
  "axis 1 = up in the GAME's frame", but on your turret bone (after the convert rebake folds the rig) that lands on
  whatever local axis it lands on — so it usually tilts (pitches/rolls) instead of yawing. There are only THREE
  local axes: try **0 / 1 / 2** in the *Turret aim axis* dropdown until it turns the way you want. (Ehrhardt: axis
  **2** = yaw. axis **1** pitched up, axis **0** pitched down.)
- **Yaw for a turret, PITCH for a barrel — same feature.** The axis that's "wrong" (tilts) for a turret is exactly
  what a mechanized howitzer / artillery barrel needs to ELEVATE at range. One knob, two unit types.

## Caterpillar treads — the loose-track saga (2026-07-26)

Making the Jagdpanzer's tread move took seventeen rig revisions; these are the lessons that survived, so nobody
walks the dead ends again (the working system is documented in Animated-Models.md → treadize):

- **Continuous-band skinning cannot look tight.** Blended carrier bones (wheel wraps + sliding runs) were
  refined until measured edge-tears fell 0.43 → 0.03 — and the user still called it loose, correctly: molded
  links visibly *bending* is what the eye reads as slack, no matter how small the numbers get. Rigid
  per-link instancing was the only cure. Corollary: metrics saturate — thin side-faces flip 180° for any
  seam mismatch bigger than the tread's thickness, so past a point only *renders* tell the truth.
- **Diagnose with tools, not eyes.** Three tiny headless scripts broke every impasse: a **tear finder** (edge
  length change between frames, ranked, with each endpoint's bone weights — names the exact seam), a **fold
  finder** (dihedral-angle change — catches what tears can't), and a **per-link displacement probe** (every
  link should move the same distance; outliers = parameterization bugs). Plus Workbench renders from the
  user's own camera angle *before* asking them to look.
- **θ-around-a-centroid is not a loop parameter.** Any concavity (a raised idler's rear ramp) makes a radial
  ray cross the band twice — two distant path sections merge, links teleport. The **belt-around-pulleys**
  construction (wheel centers + measured band radii, external tangents + wrap arcs) is exact, and all the
  inputs are measurable from the mesh.
- **Wheels and tread want different quanta.** Spoke symmetry pins the wheel spin (60° for six spokes); the
  tread restart pins the advance (integer links per loop). Decouple them — the tread system rides its own
  bones; never let tread geometry borrow the visible wheel bones (their rim radius isn't the band radius:
  rim-based rotation ran wraps 20–60 % fast).
- **Fades must be flow-aware.** A positional fade lets the animation carry a still-weighted vert PAST its
  exit tangent (tread drooping below the ground line at the road wheel). Exits hand off one advance-length
  upstream; entries don't care. (Moot under rigid links, still true for any blended carrier setup.)

## The spike plague — engine-seam debugging, a field method (2026-07-26)

The first in-game launch of the 242-bone translating tread skeleton exploded into map-spanning spike ribbons,
missing tread geometry, and twitching that touched VANILLA units. One afternoon of one-change-per-launch
debugging found FIVE independent real defects stacked on top of each other — worth recording because any
high-bone custom unit can hit each of them again:

1. **Decimation shreds link-cell skinning** (the biggest). The animated path's "Reduce to ~tris (0 = off)"
   silently substituted 12,000 — and a decimator eats flat, dense geometry (the subdivided tread band) first,
   merging verts ACROSS rigid-cell boundaries. Blended weights between distant link bones = vertices torn
   across the map as links move. For link-cell treads decimation must be OFF (0 now honestly means off).
2. **Zero-weight bones get silently dropped** between Blender and the baked assets — the side-skirt-hidden
   tread stretch produced empty cells whose bones vanished, shifting every bone index above them. The rig now
   creates bones ONLY for vert-owning cells; verify with the mesh line: `bones == bindposes`, no name gaps.
3. **A two-fragment donor draws its own skinned tread submesh** over yours, skinned by donor bone indices
   against YOUR skeleton (garbage). `hideMeshes` handles it — but only since the hide also patches…
4. **…the GPU pawn descriptor SNAPSHOTS** (fragments AND BonesCount) taken at RegisterPawnDefinition, BEFORE
   the plugin's swap. Both are now patched in place (the same surgical mechanism the hand-prop append uses).
5. **The shared per-frame animated-bone pool** (65,535 entries for ALL pawns on screen) overflows once
   high-bone customs multiply — overflowing pawns read other pawns' matrices (vanilla units spiking!).
   `SkeletonBoneBudget` (plugin config) now sizes it (default 262,144).

**The method that actually worked** — in order of leverage:
- **Read the artifacts, not the preview** (Law 4's corollary): grep the baked assets. EncodingFormat census
  of the clips, FrameCount calibrated against a known clip, bone-NAME gap scans of the skeleton, the SKMESH
  console line (`verts/bones/bindposes/maxBoneIdxUsed`). Every defect above was visible in artifacts.
- **One change per launch**, and verify the change actually landed (a slider that lies, a session that
  predates the registry write, a Factory field that went stale — three launches were wasted on phantoms).
- **Instrument the live path** when artifacts look clean: the `[PawnDiag]` dump (per-pawn descriptor
  bones/fragments at AddPawnEntry) ended a three-fix guessing streak in one launch.
- **Isolate with a static bake on a spare unit** (the statue test): the same GLB baked static renders
  perfectly → everything mesh-side exonerated in one launch. Cheap, decisive, should have been first.
- **Keep an elimination board.** By session's end: mesh ✓, bone count ✓ (84 still twitched), state machine ✓
  (transition log steady), tread-system-off ✓ (twitch stops). OPEN: idle micro-twitch triggered by the link
  system; next split = links with translations stripped (bones vs RotationTranslation playback).

## What the legacy howitzer really was (calibrate your expectations)

The "old functionality" everyone remembers was **one clip + two runtime tricks**: hold the full deploy clip at
normalized time 0.999 when idle (0.999, not 1.0 — `Repeat(1.0)` wraps to frame 0, the folded pose: the original
edge-overflow bug), and snap to frame 0 while moving. No stance clips, no fold animation, no state machine.
Recreating it state-driven therefore wasn't porting — it was building five clips through machinery the legacy
path never exercised, which is why "it worked before" was true and useless at the same time. The state-driven
equivalent that ends up matching it, entirely in data:

| Role | Clip | Why |
| --- | --- | --- |
| Idle / reference | `deploy` (full) | Law 2 — defines the reference pose |
| Idle stance (override) | `deploy[179..180]` | the deployed hold, as a role |
| Movement | `deploy[0..0]` | travel stance |
| Pre-movement | `deploy[179..0/12]` (or empty) | fast fold (empty = legacy instant snap) |
| After-movement | `deploy[0..179/3]` | the unfold |
| Attack | `deploy[180..250]` | the source's own recoil kick |

## The engine contract — decoded from the live engine (2026-07-26, the T-62 marathon)

One evening, one Sketchfab T-62 with object-baked animation, and seven consecutive in-game failure modes —
each one a real engine constraint nobody had written down. The instruments that ended the guessing are now
permanent plugin residents: **`[AnimDiag]`** (one-shot per entry: the engine's live per-bone GPUAnimationEntry
records — FrameCount/Format/StartPoseData/BBox — plus the engine's OWN `GetPoseTRS` decode at frame 0 and
mid-clip, plus the skeleton rest TRS) and **`[PawnLive]`** (throttled: the pawn entry AS THE GAME LEAVES IT —
pose slot ids/weights/times, BoneRotation records). Read both from BepInEx `LogOutput.log`; read the editor
side from Unity's `Editor.log` instead of squinting at the console.

**The contract itself.** Amplitude's clip encoder normalizes every clip against the skeleton's BIND rest and
**discards any constant frame-0 offset**. Every working unit shows the same shape in `[AnimDiag]`: skeleton
rest carries the full pose, clips decode to ~identity deltas at frame 0. Therefore **BIND must equal animation
frame 0** — a model whose bind differs from f0 renders its bind, forever, no matter what plays. The m114
satisfied this *by accident* (raw local verts + node transforms carried each part's rotation into the
bindposes); the clean-unit rework broke it, and the fix is structural, in `deploy_convert`: verts folded to
their full frame-0 world state, translation-only axis-aligned bones (safe through Blender→FBX bone-axis
conversion), pose-scale fcurves stripped (a cm-source's constant 0.01 lands in pose scale keys the engine
mishandles — the AW101 missing-fuselage class), and every bone's keys **delta-form rebased**
(`basis_f' = basis_f @ basis_0⁻¹`, hemisphere-continuous — identity at f0 by construction).

**The 128-bone-index GPU wall.** Per-vertex bone indices break past **127** — not 256. Bones 128+ render
collapsed/invisible (the T-62's turret and wheels, bones 128–140, vanished while links 1–120 animated). This
retroactively closes two cold cases: the Jagdpanzer's 241-bone spike ceiling and the mech's broken wings at
222 bones. `deploy_convert` clamps to 126 total by **pair-merging instanced link chains** (a dropped link
binds to its numeric neighbor's bone and rides it rigidly). Merges MUST be spread evenly across all chains —
clustered merges put every rider on one half of one track and that half fails together in-game; distributed,
each rider only mis-swings during its own brief wrap transit (~2 links visible at cinematic zoom, invisible at
gameplay zoom).

**Three smaller laws from the same night.**
- *1-frame stances wrap the sampler:* Unity's constant-curve dedupe collapses two identical padded frames back
  to FrameCount 1, and the engine's `Clamp(f, 0, FrameCount-2)` returns −1 → uint-wraps → a constant garbage
  pose-pool read (a STABLE wrong pose, not flicker — it looks like a broken bind). The slicer now nudges the
  pad frame by ~0.03° on one bone so the second frame survives import.
- *The ×100 translation amplify is legacy-only:* the FBX exporter's `global_scale` never scales ANIMATION
  curves, so on clean-unit exports the amplify made link crawls bake with ~300-unit bboxes (links crawling 300
  units off-map). Clean-unit sources skip it; raw-legacy sources still need it.
- *Per-role translation floors:* the move role keeps only LARGE slides (track links crawl ~6 u) and drops
  small ones (suspension bob 0.02–0.04 u — the source rode bumpy terrain; replayed on flat game ground the
  wheels wiggle in the air). The attack role keeps its historic 1e-4 floor (the m114's recoil slide is ~0.1 u
  raw). The two populations sit two orders of magnitude apart; the 0.5 floor splits them with margin.

**The from-source tracked-vehicle recipe** (what all of the above buys): any model whose animation is baked as
rigid-part object motion — no armature needed — becomes a fully animated vehicle with NO rigging work:
Deploy conversion ✓ + frame range, Idle/reference = full clip, Idle stance = `clip[0..0]`, Movement = full
clip (slice later for pacing), Keep bone translations ✓, Clear aim layer ✓ (artillery-family donors stream
aim junk onto arbitrary bones), Fix 100× OFF, Convert raw rig OFF. `deploy_convert` handles unit
normalization, recentering, root-motion anchoring (a source that drives across its scene bakes hull-relative,
in-place), bone slimming (bones only for binding targets — 1033-node wrapper rigs collapse to ~139) and the
128-wall budget automatically. "Has baked animation" is now a BONUS when sourcing models, not a complication.

**Meta-lesson (the trap that burned three bakes):** the Factory and Animation Lab windows hold separate
in-memory copies of shared entry state; baking from one silently reverts fields edited via the other (or via
the registry file directly) — `keepTranslations` was lost three times this way. Until the root cause is fixed:
after ANY field change, Reload in the window you'll bake from and eyeball the checkbox before pressing Bake.
