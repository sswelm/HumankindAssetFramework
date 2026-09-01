# Donor-Clip Flight — the donor's own animation on your custom rig

**Proven in-game 2026-08-04** on the RAH-66 Comanche (`StealthHelicopter`): the unit flies with the donor
gunship's complete original animation — body bob, main rotor flat on the mast, tail fan spinning in its own
**canted** ring — driving **our** baked mesh and skeleton. No runtime pose tricks, no baked spin clip: the
engine plays the donor clip natively and our rig is built so every channel lands where it should.

This page is the reference for the technique: the engine facts it rests on (all measured, not guessed), the
authoring workflow, and the failure catalog from the road to it. It is the deepest coupling between the
Vehicle Lab, the Animation Lab, and the plugin runtime — read [Animated-Runtime.md](Animated-Runtime.md)
first if the GPU pose pipeline is new to you.

---

## When to use it

Use `useDonorClip` when the donor's motion is *itself* the asset: helicopters (hover bob + rotor wash),
and in principle any donor whose body animation you want verbatim on your own model. The alternative paths —
your own baked clip (Animation Lab) or freezing the donor (`freezeDonorAnim`) — replace or suppress the
donor's motion; this path **keeps** it.

**Cost:** your rig must be built to the *donor's channel contract* (below). That constrains bone count,
order, and rest frames — the Vehicle Lab and the plugin handle it, but only if the workflow is followed
exactly.

---

## The engine contract (measured facts)

Everything below was established with instruments — the `[Rest]` skeleton dump, the `[DonorAxis]` channel
decoder (both in the plugin, `VerboseLog`-independent one-shots) — and verified in-game.

1. **Clip channels bind to bones BY INDEX, blindly.** The donor gunship's clip has 4 channels feeding its
   4-bone skeleton `[Dummy_Root, Base, Helix, Helix_back]`. Whatever skeleton the pawn wears, channel *i*
   drives bone *i*. Extra bones in your rig shift everything (our Gun bone made the donor's rotor channel
   spin the nose cannon); missing bones read past the clip.
2. **The measured channel axes** (decoded with `GetPoseTRS` straight from the GPU animation records):
   - ch0 `Dummy_Root` — untracked / identity throughout.
   - ch1 `Base` — the body: small rotation deltas + translations (the hover bob).
   - ch2 `Helix` — main rotor: **pure LOCAL-Y spin**, ~18°/frame.
   - ch3 `Helix_back` — tail rotor: **pure LOCAL-X spin**, ~36°/frame.
3. **The engine composes the clip ON TOP of each bone's Local rest** (`rest ∘ delta`; clip frame 0 =
   identity — the same bind==frame0 convention our own bakes follow). Consequence: a rest rotation on ANY
   ancestor bone **conjugates** every animated descendant — the donor's vertical rotor spin turns into a
   roll or a loop. This is the single most important fact on this page.
4. **Donor rigs are authored with ALL rests identity** and world-space translations (their Bind poses are
   pure `-worldPosition`). Blender-exported rigs are NOT: the glTF exporter leaves the armature object as
   bone 0 carrying the Z-up→Y-up **-90°X**, and the Factory's facing rotation lands as a rest on the Root
   bone. Both conjugate (fact 3) and must be neutralized.
5. **The GPU snapshots the skeleton's `BoneInfos` at `AnimationManager.Apply`.** Any skeleton surgery must
   happen **before** `RegisterMeshCollection` + `Apply` run (the plugin does it inside registration).
   Editing the asset afterwards changes nothing on screen — we proved that the hard way.

## The plugin's rebase (automatic)

For every `useDonorClip` entry, at registration time the plugin rebases the injected skeleton
(`RebaseRootIdentity`, v4):

- **Ancestor bones** (any bone with children): rest rotations → identity. The -90°X export rotation and
  the facing rotation are folded into the translations and bind poses; **world rest positions are
  preserved exactly**, so the static render is pixel-identical.
- **Leaf bones** (the rotors): rest orientation **preserved** — this is where the bake-authored axle
  frames live (below). Their binds are recomputed to match.

Idempotent, logged as `[Rest] <name>: rests rebased (...)`, and followed by the full before/after dump.

**Placement (the stealth-helicopter arc, resolved 2026-08-07):** a model's **Position offset is a RUNTIME
mechanism on the animated path** — the plugin adds the registry `position` to the pawn **every frame**
(`ApplyPositionOffset`: X/Y rotated into the pawn's frame so the offset turns with the unit, Z as world-up), in
true game units, with **no re-bake needed** (Save settings + mod rebuild). The editor previews draw it **live**.

The war story, so nobody repeats it: believing the knob was dead on the animated path, a bake-time application was
added — and every animated model then carried the offset **twice** (runtime + baked): the helicopter flew at
*exactly 2×* its dialed height, and X/Y centering became incoherent because the two copies lived in different
frames (pawn frame vs rig-through-rebase). Five launches of "calibration" fit multipliers to a doubled signal —
including a falsified "the rebase absorbs off-centering" theory and a footprint-centered preview approximation
built on it. The user's arithmetic (halving the dial restored the exact old height) exposed the doubling. The
bake-side copy is removed; **one dial, one application, previewed live**.

## The bake's axle frames (Vehicle Lab)

`vehicle_rig.py` orients each rotor bone so the donor's measured spin axis IS the rotor's real axle:

- **Main rotor** — bone tail along the axle ⇒ **local Y = mast axis**. The donor's Y-spin lands on the
  mast even if the mast isn't perfectly vertical (a leaning airframe no longer wobbles the disc).
- **Tail fan** — bone frame built with **local X = the fan's canted axle** (bone Y = the in-disc direction
  nearest world-up, roll set so X = Y×Z). The donor's X-spin lands in the fan's actual ring — canted
  fantails (the Comanche) spin correctly without any runtime trim.
- The own-clip Spin action keys per-frame quaternions about that same local axis, so the non-donor path
  stays correct too.

## Authoring workflow

1. **Vehicle Lab**: classify the rotors (`R` main / `L` tail), make sure no extra articulated bones exist
   between Root and the rotors (a Gun/Turret bone shifts the channel indices — mark those parts Body for a
   donor-clip model). Level the airframe in Orientation if desired (yaw first, then pitch/roll — the
   sliders act on the model as you see it, but only take effect on the next Generate rig run).
2. **Generate rig** (regenerate the rig). **This step is mandatory after any script/marking change** — see
   the stale-rig trap below.
3. **Animation Lab**: bake as usual (continuous, convertRig ON — the standard vehicle settings).
4. **Model Factory**: tick **Use donor animation clip** (Runtime section). It's a registry field —
   survives every build. Usual companions for a helicopter: `silenceDonorVfx` ON (the 2D rotor-sprite
   ghost), `respawnAfterLoad` OFF, `animPhaseSpread 0` (squadron copies overlap into one).
5. Build the mod, launch, and check the `[Rest]` dump if anything looks off.

## Failure catalog (what each symptom means)

| Symptom | Cause | Fix |
|---|---|---|
| Wrong parts animate (gun dances, tail gets garbage) | extra bone shifted the channel indices | remove the extra bone (mark part Body), rebake |
| Rotor spins on a fixed but wrong axis (rolls) | ancestor rest conjugation (export -90°X or facing) | plugin rebase handles it — check the `[Rest]` dump ran *before* Apply |
| Rotor detached, orbiting the aircraft | rest **positions** shifted (rotations folded without translations) | fixed in rebase v2+ — world positions are preserved |
| Rotor loops vertically after a re-bake | facing rotation became a Root rest | fixed in rebase v3+ (ALL ancestors flatten) |
| Rotor twists although the bake "was updated" | **stale rig**: Animation Lab reused the last rig GLB | run Vehicle Lab **Generate rig**, then rebake. Signature: both rotor rests dump as the same permutation quat `(-0.5,-0.5,-0.5,0.5)` |
| Canted tail fan wobbles out of its ring | tail bone frame not authored (local X ≠ fan axle) | regenerate with the current `vehicle_rig.py` |
| Fan spins backwards | handedness of the constructed frame | negate the axle (sign flip in the rig script) |
| Whole unit moves like a zeppelin | `useDonorClip` lost from the pack | it's a Factory checkbox now; re-tick and rebuild |

## Flight character

Three knobs decide how a unit *carries itself* in the air, independent of which animation is playing. In the
Model Factory they sit together under **Flight character**; all are runtime-only (no re-bake) and default to
off, so nothing changes for existing models.

| Knob | Question it answers |
|---|---|
| **Turn ease** (`turnRate` / `turnBank`) | how it changes heading — swept and banked, or the engine's instant snap |
| **Terrain hug** (`hugDrop` / `hugLookahead`) | how it holds altitude — nap-of-the-earth over open country, climbing for the city and ahead of cliffs |
| **Move tilt** (`moveTilt`) | its attitude while moving — nose-down forward-flight pitch |

## Turn ease — flown turns instead of the facing snap

The engine writes a pawn's facing as an **instant transform snap** when a move order changes heading — jarring
on an aircraft. The plugin smooths it (verified in-game 2026-08-04): a per-pawn eased yaw advances toward the
game's fresh target at a capped rate, with a **bank roll** proportional to how hard it's turning, composed
before the `moveTilt` nose-down. Every angle eases, full 180s included; teleports and battle placement still
snap naturally (a jumped pawn misses its position-matched state and starts at the target yaw).

**Per model** (Model Factory ▸ Runtime): **Turn ease — rate** (deg/s; 0 = the vanilla snap) and **Bank into
turn** (degrees). Registry keys `turnRate` / `turnBank`; runtime-only, no re-bake.

**Live dial** (overrides the per-model values while `rate` is non-zero): **`BepInEx/config/haf_turnease.txt`**
— `rate=180`, `bank=6`, polled ~1/s so you can dial the feel with the game running; `rate=0` hands control
back to each model's own setting.

## Terrain hug — low over open ground, climb for the city

The engine already flies air units at a **terrain-relative** altitude, so they follow hills for free — but that
altitude ignores *buildings*, which is why an aircraft needs a `position.z` lift to clear a city skyline.
Flying that high everywhere wastes the terrain-following. Terrain hug drops the unit back down (`drop`,
negative) whenever no **built** district is under or ahead of it, and eases the lift back in as it approaches
one. The probe point **leads** the unit along its own movement vector (`lookahead`), so it climbs *before*
the buildings like a pilot rather than reacting inside them.

Two things make the classification honest instead of guessed:

- **Tile scale is measured.** The median nearest-neighbour distance between districts *is* the map's tile
  spacing (adjacent districts sit one tile apart). With `radius=0` the match radius is auto-set to ~55% of it
  — "this district's own tile". A hand-picked world radius lifted the unit for every field and forest *next*
  to the city (observed at radius 6 on a 6.93-unit tile map).
- **Not every `PresentationDistrict` is a building.** Humankind renders cultivated tiles as districts too.
  The real identity is the component's private `constructibleDefinitionName` (the GameObject is always
  `PresentationDistrict(Clone)` — useless): `Extension_Base_CityCenter`, `Extension_Base_Food`,
  `Extension_Era5_ZuluKingdom`, `Extension_ArtificialWonder_*` … alongside the FLAT kinds **`Exploitation`**
  (fields, vineyards, mines) and **`Ruin`**. Only `Extension_*` carries buildings, so the flat kinds go in
  `skip`.

**Cliff anticipation** (same lead point, `cliff` in the dial file; 0 disables). The engine's terrain following
is tied to the tile the unit is *on*, so a step up in the ground arrives at the cell boundary — the aircraft
rises *into* the cliff instead of over it. The probe reads the ground height at the lead point and, if it
stands higher than the ground here, adds that difference immediately, so the climb starts before the edge and
the engine's own altitude catches up on arrival. Climb-only: anticipating a descent would drop the unit toward
the high ground it is still crossing. **Implementation note:** the ground is read with a downward
`Physics.RaycastAll`, taking the LOWEST hit and skipping units (layer 10 / `Presentation*` names) — the first
version used a plain raycast and measured the helicopter's own army collider, i.e. compared unit heights, not
terrain. The first probe logs what it hit; if it ever reports no ground collider, the fallback is the per-tile
`TileInfo[].Elevation` data (needs world→hex coordinates and a world-unit scale).

**Per model** (Model Factory ▸ Runtime): **Terrain hug — drop** (units, negative; 0 = off) and **Climb
anticipation**. Registry keys `hugDrop` / `hugLookahead`; runtime-only, no re-bake.

**Live dial** (overrides the per-model values while it has a non-zero `drop`, for tuning by feel in-game):
**`BepInEx/config/haf_hugterrain.txt`** — `drop=-2`, `radius=0` (auto), `lookahead=1.5`, `ease=4`,
`skip=Exploitation,Ruin` (or `only=` as a whitelist). Polled ~1/s; set `drop=0` (or delete the file) to hand
control back to each model's own setting. The log prints the district names it sees, the measured tile
spacing, and every climb/descend transition with the distance that decided it. Note the district set grows
while the map streams in — the 3-second rescan self-corrects.

## Instruments

- **`[Rest]`** — one-shot per entry: donor + our skeleton, per-bone Local + Bind TRS, logged at injection.
  The ground truth for every rest-frame question.
- **`[DonorAxis]`** — one-shot per entry: decodes the donor clip's channels from the GPU records at several
  frames. The ground truth for what the clip actually does.
- **`haf_rotortrim.txt`** (BepInEx/config) — live constant-tilt dial on named bones (`Bone@axis=deg`),
  polled ~1/s and re-applied to live pawns without a relaunch. Kept inert (comments only) unless a residual
  wobble needs hand-finishing.
- **`haf_turnease.txt`** (BepInEx/config) — the turn-ease dial (see the section above).
- **`haf_hugterrain.txt`** (BepInEx/config) — the terrain-hug dial (see the section above).

## Open ends

- **One placement verification outstanding.** With the double-application removed, the preview (rest-pose FBX +
  live runtime offset) should predict in-game placement exactly, since the rebase preserves world rest positions
  by design. One clean in-game check on a district-free tile confirms it; if a residual mismatch appears, measure
  it (an F8 pawn-vs-tile-center readout is the designed instrument) before touching anything.
- Terrain-hug tuning knobs still session-global: `radius`, `ease` and the `only`/`skip` district-name filters
  live only in the dial file (they describe the *map*, not the model, so per-model versions may never be
  needed — revisit if a second flying unit wants a different city definition).
- `donorClipSpeed` — whole-clip playback speed multiplier (body + rotors together); designed, not built.
- `silenceDonorVfxNames` — per-name VFX filter so only the donor's rotor sprite is dropped; backlog.
- **Per-rotor speed via pose-slot blending (the promising experiment).** The pawn has multiple pose slots
  (`Pose0`–`Pose8`, each its own clip + weight), and the plugin already knows how to write them (the soldier
  state machine). Leave Pose0 = the donor clip and set **Pose1 = our own baked Spin clip**: if the engine
  *sums* pose deltas, the body keeps the donor's motion untouched (our clip's body channels are empty) while
  the rotors get donor spin **plus** ours — i.e. the Vehicle Lab's Spin RPM becomes a per-rotor speed *offset*
  on top of the donor. One cheap in-game test decides it: additive → per-rotor speed control for free;
  averaging → half-speed mush, drop the idea. (Blocking the donor instead — dummy channel-eater bones at
  indices 2/3 — would only *freeze* our rotors: bones beyond the clip's channels get no animation at all,
  and continuous plugin-side driving is exactly what the spawn-frozen BR writes couldn't do.)
