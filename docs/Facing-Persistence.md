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
- **Restore (main thread):** while a file is armed, the tick runs **every frame** and, for each army, re-applies
  `PresentationUnit.FlipPawnsGrid(angle, FormationMoveBehaviour.Teleport)` whenever the current heading has
  **drifted** from the target. Drift-based means it turns the unit the instant its pawn loads (no neutral flash),
  re-corrects a `respawnAfterLoad` rebuild that resets the heading, and costs nothing for units already facing
  right. The restore window closes after ~5 s.

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
