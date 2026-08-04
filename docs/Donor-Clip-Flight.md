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
   sliders act on the model as you see it, but only take effect on the next Vehicleize run).
2. **Vehicleize** (regenerate the rig). **This step is mandatory after any script/marking change** — see
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
| Rotor twists although the bake "was updated" | **stale rig**: Animation Lab reused the last rig GLB | run Vehicle Lab **Vehicleize**, then rebake. Signature: both rotor rests dump as the same permutation quat `(-0.5,-0.5,-0.5,0.5)` |
| Canted tail fan wobbles out of its ring | tail bone frame not authored (local X ≠ fan axle) | regenerate with the current `vehicle_rig.py` |
| Fan spins backwards | handedness of the constructed frame | negate the axle (sign flip in the rig script) |
| Whole unit moves like a zeppelin | `useDonorClip` lost from the pack | it's a Factory checkbox now; re-tick and rebuild |

## Instruments

- **`[Rest]`** — one-shot per entry: donor + our skeleton, per-bone Local + Bind TRS, logged at injection.
  The ground truth for every rest-frame question.
- **`[DonorAxis]`** — one-shot per entry: decodes the donor clip's channels from the GPU records at several
  frames. The ground truth for what the clip actually does.
- **`enc_rotortrim.txt`** (BepInEx/config) — live constant-tilt dial on named bones (`Bone@axis=deg`),
  polled ~1/s and re-applied to live pawns without a relaunch. Kept inert (comments only) unless a residual
  wobble needs hand-finishing.

## Open ends

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
