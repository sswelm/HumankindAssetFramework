# Turn ease & attack hold — units that pivot before they fire

*Verified in-game 2026-08-05: HAF models (TowedGunHowitzers), vanilla units via Formation Lab links (the Zulu
Kingdom siege howitzers), and the true-bearing aim. Flight-specific easing (bank, terrain hug) lives in
[Donor-Clip-Flight.md](Donor-Clip-Flight.md); this page covers the general feature for any unit.*

Humankind snaps a unit's facing instantly — on move orders and, most visibly, on attacks: a towed gun
teleport-pivots 150° and fires in the same instant. With turn ease, a HAF model **sweeps to its new heading at a
configurable rate**, and a map bombard **waits for the pivot**: muzzle flash, shot sound, shell, impact and the
model's own fire-on-attack clip all hold until the barrel actually faces the target, then land together.

## Configure

Per model (the proper way):

| Field | Where | Meaning |
|---|---|---|
| **Turn ease — rate** (`turnRate`) | Model Factory → Flight character | Max turn speed, degrees/second. `0` = vanilla snap. `180` ≈ a 90° pivot in half a second. |
| **Turn ease — bank** (`turnBank`) | Model Factory → Flight character | Max roll into the turn, degrees. Flyers only — leave `0` for ground vehicles. |

The attack hold needs no switch of its own: any model with a turn-ease rate automatically holds its bombard
until aligned (within 8°, 4 s failsafe). Vanilla units and models without a rate keep exact vanilla pacing.

**Category defaults** (verified 2026-08-06): global rates per unit **type**, for HAF models *and* vanilla
units — classified by **characteristic, never by name**:

| Key | Covers | Identified by |
|---|---|---|
| `human` | infantry, cavalry, chariots, animals | organic capability profiles |
| `land` | turretless land vehicles (towed guns, carts) | vehicle profile, no refinement |
| `turret` | land vehicles with a traversing turret | extra azimuth rotation transforms on the live pawn |
| `hover` | helicopters, hovercraft | the game's own `Hover` ability ("ignores terrain") |
| `ship` | boats and ships | boat profile |

Fixed-wing **planes and missiles are always excluded** — the engine flies them on natural curved paths; only
an explicit per-model rate can ease one. `hoverbank=` / `shipbank=` give hover and ship their own roll into
the turn (a chopper banks, a ship heels); other categories stay flat. **Precedence: per-model Factory value >
per-unit Formation link > category default > global `rate`.** Configure in the Formation Override window's
**Turn ease defaults** panel (writes the dial live) or edit `haf_turnease.txt` directly.

Classification runs two paths: capability profiles are read when a pawn definition loads (fast path), and a
slow **class scan** (~3 s, active only while category rates are set) reads every live unit directly — its
`Hover` ability, its turret transforms, and its pawn's profile — position-joined to descriptors once per
session. The scan is the authority: some pawn definitions (artillery guns among them) never pass the load
hook at all, and the scan classifies them from the rendered unit itself. The strike holds read the eased
pawn's **ground-truth rate off its live turn state**, so the visible turn and the fire hold can never disagree.

**Battle hull-aim** (verified 2026-08-06, the Jagdpanzer arc): vanilla **never rotates a vehicle's hull in a
deployed battle** — vehicles are flagged to avoid facing rotation and "aim" only via a turret bone slot
(invalid on custom rigs; the `turretBone` retarget exists for that). A turretless vehicle therefore aimed
with *nothing*. HAF now arms the same aim machinery the map bombard uses when a battle volley is
choreographed (our turretless land/ship models): the eased hull turns to the actual target (broadside offset
honored), `hold=1` makes the shot wait for the lay, and the whole muzzle chain fires from the laid barrel.

**Guns, turrets & elevation** (verified): the Animation Lab models a weapon as two concepts — a **Turret
bone** *yaws* at targets (and classifies the vehicle as turreted), while a fixed **Gun bone** aims with the
hull and only *elevates*: **Gun elevation — max** raises the barrel **proportional to target distance** (full
at ~3 tiles), rising while the hull turns, held through the shot, lowered a few seconds after. The **Muzzle
offset** dial is **gun-local** (it rotates with the aim and elevation) and moves flash, tracer *and* smoke —
note that *which* local axis runs along the barrel depends on the rig's bone frames (converted Blender-style
rigs often use X where Unity-style rigs use Z): dial one axis at a time and watch the flash. The shell itself
is always the game's own projectile — HAF only fixes *where* and *when*.

**Post-shot facing** (verified): after the shot the unit **stays laid** and, ~3 s later, settles onto the
**nearest clean facing toward the direction it fired** (30° grid) — no springing back to the pre-attack
facing. The aim releases only on unambiguous signals: the unit moves, the next attack re-aims it, or a 120 s
long-stop.

**Pivot in place** (verified in-game 2026-08-22 — "now it is moving perfect"): a ground or naval unit that has to turn **90° or
more** **turns on the spot first and only then moves off** — the way a tank, a cart or a ship actually behaves,
instead of sliding sideways into its new heading while already rolling. Mechanism: the simulation feeds the
presentation army a growing `positionHistory`; every frame `PresentationArmy.UpdateWaitForReadyToMove` hands
it to the unit as a move when the unit is idle, or extends a running move. HAF's prefix answers "not yet"
while the unit is idle, for as long as the eased yaw needs to face the **next history tile** (`turn ÷ rate` +
0.1 s, capped 4 s) — that bearing is set as an aim override so the ease turns to it — and so the **whole
vanilla move is deferred**, holder and pawns together: the unit turns standing still, then the game's own
smoothed path starts from rest, untouched, with the longer history it accumulated meanwhile. Nothing is
re-drawn, nothing catches up, the chunk pipeline stays in sync. The hold is **per unit** (decided by the turn
state at the unit centre), so an artillery piece's crew waits with its gun, and only from a **real stop** (no
movement for 1 s) — a unit that just rolled in keeps rolling and bends onto the next leg the vanilla way.
**Fold first** (verified 2026-08-22): a state-driven model with a **pre-move clip** (the howitzer folding its
legs) holds **every** move start for the clip's length — turn or no turn, whatever the pivot threshold — and
counts as *moving* from the moment the hold arms, so the fold plays during the hold (during the turn, when
there is one) and the gun rolls off fully folded. With a turn, the hold is the longer of the two
(`… turn 150 deg …, pre-move fold 2.0 s` in the `[Pivot]` line; `no turn, pre-move fold 2.0 s` without). Helicopters/hovercraft (`hover`) and planes are
**never** pivoted by default — a chopper translates while it yaws. Threshold via the dial: `pivot=<deg>` in
`haf_turnease.txt`, **default 90** (the one key with a non-zero default — a legacy file keeps the behaviour),
`pivot=0` turns it off. Every hold writes `[Pivot] holding move start … s: turn … deg` to the log.

*Graveyard (nine drills, one morning):* every approach that kept the vanilla movement running and **re-drew the
pawn's position** read as a sideways slide — parking the rendered unit and catching up along a chord; replaying
the game's recorded trail at 1.5×; turn → drive straight to the next hex → rejoin. The log showed why: a 150°
turn at 90°/s is 1.6 s, the real unit was 4 u ahead by the time the drive ended, and no catch-up closes that
naturally (the 4 s failsafe fired with 2.7 u of lag). Don't re-draw; **delay the game** — and delay it at the
**army** layer: holding only the pawns' `StartMoveAlongTilesIfPossible` put them 1.8 s behind the unit holder,
the army could no longer extend the running path, and the unit stood 1.5 s at the intermediate tile
(`stood 1.5 s, pawn-unit gap 0.0 u` in the log) before the next chunk started from rest.

**Per-unit override** — **Pivot in place** on a Formation Override **unit link** (`turnPivot` in
`haf_formations.json`, runtime-only: Save + relaunch), so **vanilla units and HAF models alike** get it — the
runtime maps the link to the unit's pawn descriptor exactly like the Turn ease row (`[TurnEase] pivot link …`
in the log confirms it), and an entry's descriptor *is* its unit's vanilla one. The row is a popup:
**Default** = the global dial under the category rule (ground/naval pivot, hover/planes don't); **Custom
angle** (`> 0`) = this unit's own threshold — `1°` makes it *always* turn fully before moving (a tank, a towed
gun), `150°` only on near-reversals, and it even opts a helicopter in; **Never** (`< 0`) = turn while rolling.
Independent of the Turn ease row: a unit eased by its *category* default may carry only this, and a link may
carry only this (no formation change). Precedence: link > dial.

Live dials (`BepInEx/config/`, polled ~1/s, no restart):

- **`haf_turnease.txt`** — the category defaults above, `pivot=` (pivot-in-place threshold, default 90), plus
  `rate=`/`bank=` as last-resort fallbacks for uncategorized units and per-model-eased flyers.
- **`haf_battleturn.txt`** — `hold=1` enables the **experimental, untested** battle-side hold (ranged attacks in
  deployed battles wait for the rotation FSM); `diag=1` turns on choreography forensics logging.

> **A typo is now reported, not swallowed (2026-08-20).** Every dial line the parser cannot understand produces a
> `[Dial] <file>: line N: …` **warning** in the BepInEx log, naming the line, the offending token, and the valid
> keys — an unknown key (`hoverbanks`), a non-number (`rate=fast`), a comma decimal (`rate=1,5` — dials are always
> parsed with `.`), a line with no `=` or with two. Before this, all of those vanished in silence and you got a
> working plugin that quietly ignored the setting. **Numbers echoed back in the `[TurnEase]` / `[Hug]` log lines
> are printed the way the file must spell them**, so a value copied out of the log always parses back in.

**Vanilla units** (verified): the Formation Override window has a per-unit **Turn ease** row — tick it on any
unit link (a link may even carry *only* turn ease, no formation change), Save, relaunch. The plugin resolves
the link to the unit's pawn descriptor at load (`[TurnEase] vanilla …` in the log confirms the mapping) and
eases those pawns exactly like a HAF model; the bombard hold follows automatically. A link on a `_Common_`
family unit **also covers its culture-emblematic variants** (the Zulu siege howitzers matched a link on the
Common unit); a link on a specific culture's own unit stays culture-exact. Picking a unit prefills the
Formation field with its current formation, so a turn-ease-only link starts as a neutral no-op.

**True-bearing aim** (verified): vanilla flips a bombarding unit to a **hex-quantized** angle — one of six
directions, up to 30° off the target (`GetHexagonAngleToPosition`); the instant snap hid it for years, the
eased turn made it obvious. While a strike is in progress the ease target becomes the **real bearing** to the
target tile (per pawn), the hold and the recoil release measure against it, and after ~10 s the unit eases
back to the game's own facing — the crew re-laying the gun. Falls back to the quantized angle if the tile
resolution ever fails.

**Strike synchronization** (verified — three separate vanilla shortcuts had to fall): every effect of a shot
fires off **one shared release clock** armed before the flip (mixing a dynamic release with padded static
delays drifted the bang ~0.25 s from the recoil); the attack clip starts **deterministically at frame 0**
(vanilla teleports into it at a *random phase* while timing the shell to the fire event's literal clip time);
and the shell's spawn pose is **re-captured at fire time and every bone TRS is rotated onto the aim** while
the strike is live — vanilla captures the muzzle *before* the pivot, and the pawn's invisible transform
skeleton (which mecanim smoke/flash spawn from) never turns with the eased GPU model.

## How it works (maintainers)

Two different worlds, two different treatments:

**World map (the verified chain).** The choreography rotation FSM is a **no-op** on the map — `StepTurning`
initializes `0→0` and completes on its first call (measured with the `diag=1` probes). The visible facing is
stamped straight into the GPU pawn data: a bombard's `PresentationArtilleryStrike.TriggerBombardAnimation` calls
`FlipPawnsGrid(angle, Teleport)` — *that* is the snap. So:

1. **The turn** is smoothed at the `ObjectSpace.Rotation` seam (`ApplyTurnEase` in the pose hook — the same
   mechanism the Comanche flies with, now applied to every HAF entry, not just donor-clip flyers).
2. **The shell + impact** are plain scheduled delays on `PresentationArtilleryStrikeController` — two prefixes
   (`Hk_ArtilleryHold`) add the remaining turn time to both, preserving flight time.
3. **Muzzle flash + shot sound** ride the mecanim events of `AttackFSM.TeleportToSimpleAttack()` — a prefix
   (`Hk_BombardAnimHold`) defers that teleport by the same hold and replays it from `Plugin.Update`.
4. **The model's fire-on-attack clip** arms *held* (`FireInstance.waitAlign`): its clock is pinned to "now"
   each frame until aligned, so a deployed howitzer holds its deployed pose mid-pivot and recoils on arrival.

The remaining-turn time is computed as **eased yaw vs the pawn Transform** — `FlipPawnsGrid` has already pointed
the Transform at the target in that same frame, while the eased yaw still lags. (The turn-ease state's own
target only refreshes on the *next* pose frame; computing against it raced to zero. Twice.)

**Battle (experimental, `hold=1`).** Deployed battles rotate pawn Transforms for real and start attacks through
`PawnActionRangedStartAttack` — `Hk_BattleHoldFire` un-latches the action's own `isReadyToStart` retry loop
while the shooter's rotation FSM runs, and `Hk_BattleAttackGate` gates the attack FSM's shared delay step for
volleys that bypass the action. Both are off by default until verified in a real battle.

### Graveyard — do not re-attempt

- **Postfixing `RotationPawnStateMachine.GetUnanimatedRotationProgress`** never executed, even while its
  unanimated branch demonstrably ran: the JIT **inlines** it into `StepTurning`. Patch methods that are invoked
  through the `StaticSteps` *delegate arrays* instead — delegate calls never inline the callee.
- **Bumping `AttackFSM.Start`'s `delayDuration` once at Start** computed zero: the facing snap lands *after*
  Start, so at that instant the pawn still reads aligned. Holds must be dynamic or transform-based.
- The battle-side machinery (`PawnActionLookAt`, `UnitActionFaceEnemy`, the rotation FSM) **does not drive the
  world map** — five iterations of patching it changed nothing there. Follow the strike report handler
  (`PresentationArtilleryStrike`) end-to-end before picking a seam.
- **Never give a strike two clocks.** A dynamically-released effect (aligned-within-ε) next to a
  statically-scheduled one (projected hold + pad) WILL drift audibly. Arm one release timestamp up front and
  make every consumer read it.
- `TeleportToSimpleAttack` plays the attack state at a **random clip phase** (`randomOffset: true`) while the
  artillery scheduler times the shell to the fire event's literal clip time — deterministic frame-0 playback
  is required for anything that must sync with the schedule.
- The pawn's **transform skeleton does not turn with the eased GPU model** — anything resolved off bones
  (mecanim VFX, `PrepareArtilleryStrikeFX`'s muzzle capture) sits at the stale/quantized angle. Rotate at the
  `GetBoneTRS` seam while the aim override is live; never patch `StartVFXEvent` arguments (the 18/19 IL
  incident).
- **Never resolve the same value through two different code paths.** The pose side eased at one rate while
  the strike side re-derived it by name and got zero — three separate times (an entry dead-end, the limbered
  render variant, the servant crew answering for the gun). The fix wasn't better name matching but a single
  source of truth: the hold reads the rate off the live ease state.
- **Some pawn definitions never pass the addon Load hook** (artillery main guns, measured). Any per-descriptor
  data keyed there has holes; the live class scan reads the rendered unit itself and is the authority.
- A unit's pawn family includes its **servant crew** — first-match resolution let the crew (human, rate 0)
  answer for the gun. The heaviest equipment governs: hover > turret > ship > land > human.
- **Don't infer "new facing order" from yaw changes.** The battle choreography's own post-fight facing reset
  is indistinguishable from a genuine order, so a yield-on-change heuristic yanked the hull home after every
  shot. Release aim holds on unambiguous signals only: position drift, a re-arm, a long-stop.
- **Bone-frame axis labels lie across rigs.** "Z = forward" holds for Unity-convention bones; converted
  Blender-style rigs put the barrel on X. Any bone-local knob (muzzle dial, elevation axis) must be dialed
  empirically per model — and an elevation axis parallel to the barrel is an invisible roll.
- A bone's TRS sits at its **pivot** (the breech), not the barrel end — and a world-space offset dial can't
  follow a turning hull. Muzzle corrections must be bone-local.
