# Unit facing persistence

Humankind's own save **does not store which way a unit is facing.** The simulation `Unit`/`Army` have no
orientation field — facing lives only on the presentation (`PresentationUnit.FormationAngle`, an int world
heading) and is recomputed from movement/actions on load, so a reloaded unit resets its heading to neutral.

This feature restores it, in a **HAF-owned side-file that never touches the game save**.

## Where

```
BepInEx/config/haf_state/facing/<saveName>.facing
```

One file per save, named after the save (`quick-save.facing`, `auto-save 3.facing`, your manual name…). Plain
text, one line per army:

```
<armyGUID>,<angleDegrees>
```

`armyGUID` = the army's serialized `SimulationEntityGUID` (the same id survives the reload — that's why it's the
key, not a tile position); `angleDegrees` = its `FormationAngle`.

## How

`Patches/FacingPersistPatch.cs`, all reflection (the game types aren't referenced), fail-soft throughout:

- **Capture (main thread, `Plugin.Update` → `FacingPersist.Tick`):** every ~¼ s in steady state, walk
  `Presentation.PresentationEntityFactoryController.PresentationArmyEntities` and refresh an in-memory
  `{guid → angle}` map from each loaded army's `FormationAngle`. Reads the presentation only from the main thread.
- **Save (`Sandbox.Save` postfix, `Hk_SandboxSave`):** the save may run off the main thread, so we write the
  **pre-captured** map (not a fresh presentation read) to `<StorageContainerInfo.Name>.facing`.
- **Load (`Sandbox.Load` postfix, `Hk_SandboxLoad`):** arm the matching file; the tick applies it once pawns exist.
- **Restore (main thread):** while a file is armed, the tick makes a pass over the armies and restores each saved
  unit's heading **exactly once** — the instant its pawn loads (no neutral flash) — via
  `PresentationUnit.FlipPawnsGrid(angle, FormationMoveBehaviour.Teleport)`, then marks it handled and **never touches
  it again**. A unit already **in motion** when the pass reaches it (`IsAnyPawnMoving`) is left alone — its heading
  is the game's. The restore **stops the moment every saved unit has been handled** (one cycle); a ~5 s frame cap
  only backstops saved units that never load this session.

  > **Why single-shot** (2026-08-01): the original design re-applied on *any* heading **drift** for a ~5 s window to
  > catch a `respawnAfterLoad` rebuild. But it couldn't tell a load/respawn reset from **the player moving the unit**,
  > so it snapped the heading back every frame and the unit **crab-walked sideways** for the first ~5 s after a load.
  > Single-shot restore + skip-if-moving removes that entirely (and drops the per-frame walk + a per-tick dictionary
  > allocation — it was a real perf cost too). Trade-off: a `respawnAfterLoad` unit that resets *after* the one pass
  > isn't re-corrected — an acceptable rarity versus fighting every unit's movement.

## Config

`Factory / PersistUnitFacing` (BepInEx config), default **on**. Applies to **all** armies (vanilla + custom),
keyed by GUID, per-save file. Turn off to disable capture and restore entirely.

## Reusable knowledge

The save/load choke points found here are useful for any HAF state that must survive a reload:
- **Save:** `Amplitude.Mercury.Sandbox.Sandbox.Save(StorageContainerInfo, SerializationFormat, GameSaveDescriptor)`
  (or the static event `Sandbox.OnSaveStateChange`). `StorageContainerInfo.Name` = the per-file identity.
- **Load:** `Sandbox.Load(StorageContainerInfo)` (or `Sandbox.OnLoadStateChange`) — fires when the **simulation**
  is deserialized; the **presentation is not built yet**, so apply presentation changes in the post-load poll, not
  in this hook.
- **Stable army key:** `PresentationArmy.ArmyInfo.SimulationEntityGUID` (ulong), serialized → survives load;
  `PresentationEntityFactoryController.GetArmy(ulong)` bridges a saved GUID back to its `PresentationArmy`.
