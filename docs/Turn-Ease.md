# Turn ease & attack hold — units that pivot before they fire

*Verified in-game 2026-08-05 (TowedGunHowitzers map bombard). Flight-specific easing (bank, terrain hug) lives in
[Donor-Clip-Flight.md](Donor-Clip-Flight.md); this page covers the general feature for any HAF model.*

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

Live dials (`BepInEx/config/`, polled ~1/s, no restart):

- **`enc_turnease.txt`** — `rate=` overrides every model's rate while non-zero (spike/tuning tool); `bank=`
  overrides bank only while non-zero, so a global rate doesn't strip a flyer's bank or force one onto guns.
- **`enc_battleturn.txt`** — `hold=1` enables the **experimental, untested** battle-side hold (ranged attacks in
  deployed battles wait for the rotation FSM); `diag=1` turns on choreography forensics logging.

Planned: per-unit rates for **vanilla units** via the Formation Lab (its registry already reaches arbitrary
units by definition name — the same route as per-unit pawn counts and scale).

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
