# Firing-on-attack — linking a unit's combat action to a model animation

**Status: COMPLETE (2026-07-11) — shipping.** A custom injected model **plays its own clip when the unit attacks** — a howitzer's barrel *raises the moment it bombards*, then returns to rest — driven end-to-end off Humankind's combat event bus. Enabled per-model by the Factory's **Fire on attack (play once)** toggle. This document records the research + build.

> **How it works (all three pieces proven in-game):**
> 1. **Detect the shot.** A bombard raises **`SimulationEvent_ArtilleryStrikeStarted`** (a unit-vs-unit bombard, not just siege — verified via a multi-event probe: *only* this event fired). `Patches/CombatEventPatch.cs` hooks its static `Raise`, reads `ArtilleryStrike.StrikerUnit.UnitDefinition` (= `LandUnit_Era6_Common_TowedGunHowitzers`), and matches it to the registry entry (`pawnDescription` `Era6_Common_TowedGunHowitzers_01`) by their **shared core** via `UniversalInject.FindEntryForUnitDefinition`. On a match it **enqueues the firer's `SimulationEntityGUID`(s)** (both the striker unit's and army's) and logs `[Fire] *** OUR MODEL '<name>' FIRED`.
> 2. **Resolve the firing pawn (per-instance).** The combat event runs on the **sim thread** (no Unity access), so `Plugin.Update` (main thread) drains the queue: it walks the presentation armies to the `PresentationUnit` whose `GUID` matches, and records **each of its pawns' render positions** (`PresentationPawn.Transform.position`) as "active fires." This is what makes only *the* firing howitzer animate instead of every howitzer of the type.
> 3. **Play once, on the matching pawn.** The `PawnManager.AddPawnEntry` pose hook, for a `fireOnAttack` model, plays a **single `0→1` pass** only on the pawn whose `ObjectSpace` position is **nearest an active fire** (both are Unity render coords; match radius ~4u); every other pawn of that model **rests at frame 0**. The pass is real-time (clamped by clip duration), then rest; finished fires are pruned in `Update`. Non-`fireOnAttack` animated models (a drone's prop) keep looping.
> 4. **The clip.** A 2-bone DIY barrel-elevation rig (root + barrel pivoting at the trunnion, rigid weights, one "Fire" action authored to **start *and* end at rest**), baked via the Animated path.
>
> **Two build gotchas (both now handled):** (a) the animated bake keeps the mesh's rig origin (no static "keel→z=0"), so **center the model on the origin** in the rig; (b) the **100× unit-conversion oversize is now fixed in the baker** (measure at true scale / bake at file scale — see below), so an animated model uses its **normal Size** like the static path.

---

## 1. The problem

The animated injection path (see the drone) plays a baked clip on a **continuous loop** via the `PawnManager.AddPawnEntry` pose hook. "Link to attack" needs the clip triggered **once, on the combat action**, not looped — a looping barrel (a howitzer pumping its barrel nonstop) looks worse than static. So the machinery to *play* a clip already exists; the missing piece was a reliable signal for **when a unit fires** and **which unit**.

Two routes were considered:
- **Route A — borrow the donor's attack motion.** A model skinned to the donor's rig inherits whatever the donor plays (same mechanism as the idle bob the *Freeze donor animation* flag suppresses). Free, but it's the donor's generic motion, and a **static** model baked on the single-bone vehicle rig can't inherit a *barrel-only* elevation (the barrel isn't a separate bone in that bake).
- **Route B — play our own clip on the attack event.** Bake a bespoke clip, detect the attack, trigger it. This doc is about proving Route B's unknown: **is there a hookable attack event?**

---

## 2. The finding — Humankind's SimulationEvent bus

Humankind exposes a **designed pub/sub event bus**, not just combat methods to Harmony-patch. Pattern (from the decompiled game):

```csharp
// subscribe:
SimulationEvent<SimulationEvent_ArtilleryStrikeStarted>.Raised
    += new Action<object, SimulationEvent_ArtilleryStrikeStarted>(handler);
// the game raises it:
SimulationEvent_ArtilleryStrikeStarted.Raise(artilleryStrike, artilleryStrike);
```

Each event is a class with a static `Self`, public data fields, and a static `Raise(sender, …)` that fills the fields and calls `SimulationEvent<T>.Raise(sender, Self)`. **The plugin can subscribe to `.Raised` and be notified** — cleaner and more update-stable than patching internal combat logic.

### Combat events (in `Amplitude.Mercury.Firstpass.dll`)

| Event | Fires when | Fit |
|---|---|---|
| **`SimulationEvent_ArtilleryStrikeStarted`** | a unit begins an **artillery/bombard** strike | **best** — the unit stays on its tile to animate (howitzer) |
| `SimulationEvent_AirStrikeStarted` / `…Terminated` | a bomber runs an air strike | good (bombers) |
| `SimulationEvent_NuclearWeaponFired` | a nuke/missile launches | poor for *unit* animation — the missile flies away, nothing persistent to animate |
| `SimulationEvent_BattleStarted` / `BattleReady` / `BattleTerminated` | melee/tactical battle | possible (melee units) |
| `SimulationEvent_UnitDamageReceived`, `UnitKilled`, `FortificationDamaged` | receiving/lethal outcomes | for hit/death reactions, not firing |

> To regenerate the full event menu: `grep -aoE "class SimulationEvent_[A-Za-z]+"` on the decompiled Firstpass.

### The artillery event's payload

```csharp
internal class SimulationEvent_ArtilleryStrikeStarted : SimulationEvent<SimulationEvent_ArtilleryStrikeStarted>
{
    internal int AttackerEmpireIndex;
    internal int TargetTileIndex;
    internal ArtilleryStrike ArtilleryStrike;   // full order/state object
    public static void Raise(object sender, ArtilleryStrike artilleryStrike) { … }
}
```

So on every bombard we get the **attacker's empire**, the **target tile**, and the whole **`ArtilleryStrike`** object — enough to know *that* an artillery unit fired and roughly where.

---

## 3. Feasibility verdict — YES

The scary unknown ("is there a hookable attack event?") was **answered: yes**, and the feature is **built and shipping** (per-model **Fire on attack** toggle).

### The 100× scale toggle (animated bake)

Getting the clip visible surfaced a scale gotcha worth recording. Some rigged FBX exports embed a **metre→centimetre unit scale** that the SDK Skeleton bake over-applies, so the model bakes **~100× too big** and floats high (fine in the preview, wrong in-game). The howitzer's rig does this; **the drone's does not** — and the two need *opposite* handling, so there is **no single rule**. The fix is a **per-model toggle**, `ModelDef.animUnitFix` / the Factory's **Fix 100× oversize (FBX unit scale)** checkbox:

- **On** (`UniversalBaker.BuildAnimated`): measure the FBX with `useFileScale` **off** (true native size, matches glbconv) then bake with it **on**, so the size factor is `size / true_longest` and **Size means in-game units**. The howitzer needs this.
- **Off** (default): Unity's normal import — the drone bakes correctly this way.

> ⚠️ **Cautionary tale:** this started as a "universal root fix" (always measure-off/bake-on). It fixed the howitzer but made the **drone vanish** — the drone's FBX measured a wild `native longest 25.365` where the howitzer's read a clean `2.0`, because their exporters embed different unit scales. One model is not a proof. The honest answer was a per-model switch, not a global constant.

---

## 4. Implementation — built

1. **Detect** — `Patches/CombatEventPatch.cs` (`Hk_ArtilleryStrike`) Harmony-Postfixes the static `SimulationEvent_ArtilleryStrikeStarted.Raise` (patched via the explicit hook list in `Plugin.cs`, not `PatchAll`). It reads `ArtilleryStrike.StrikerUnit.UnitDefinition`.
2. **Match the model** — `UniversalInject.FindEntryForUnitDefinition` matches the firing unit to the registry entry by their shared `pawnDescription` core (strips a trailing `_\d+`).
3. **Match the *instance*** — the fire flag can't live on the shared registry entry, or **every** pawn of that model fires together. So the combat hook enqueues the firer's `SimulationEntityGUID`(s) (unit + army — the on-map presentation entity is an army, so match either), and `UniversalInject.ProcessFireQueues` (in `Plugin.Update`, main thread) resolves the matching `PresentationUnit` and records its pawns' `Transform.position` as active fires. Two gotchas conquered: (a) the combat event is on the **sim thread** — touching Unity Transforms there is unsafe, hence the queue + main-thread drain; (b) `SimulationEntityGUID.ToInt64()` **throws `InvalidCastException`** — read the GUID via `ToString()` parse (`UniversalInject.GuidToLong`), the same way on both sides so the values compare equal.
4. **One-shot playback on the firer** — the `OnPawnAdded` pose hook, for a `fireOnAttack` entry, plays a single duration-clamped `0→1` pass **only on the pawn nearest an active fire** (`ObjectSpace` position vs the recorded fire position, ~4u radius); all other pawns rest at frame 0. Finished fires are pruned in `Update`. Non-`fireOnAttack` animated models keep looping. (The model↔pawn match still uses the same descriptor + forced-skeleton path as *Freeze*.)
5. **The clip** — a **2-bone** DIY barrel-elevation rig (root + barrel at the trunnion) on the **crew-stripped** gun GLB, fully weighted, one "Fire" action authored to start *and* end at rest, baked via the Animated path. *Why 2-bone DIY:* the store model's full crew rig broke the animated bake (53 bones, 97% unweighted → bone-#0 collapse). A clean 2-bone rig sidesteps it.
6. **The toggle** — `ModelDef.fireOnAttack` + the Factory's **Fire on attack (play once)** checkbox (Animation section) write it to the registry, so it survives re-bakes.

*Sim→presentation bridge (reusable):* `Presentation.PresentationEntityFactoryController.PresentationArmyEntities[].PresentationUnit` gives, per unit, its `SimulationEntityGUID GUID`, its `List<PresentationPawn> Pawns`, and each pawn's Unity `Transform` — the same walk the borrowed-rotor respawn fix uses. That's how any per-instance sim event can be mapped to specific pawns.

---

## 5. Extending it

- **Non-artillery units** — the same pattern extends to `AirStrikeStarted` (bombers) and `BattleStarted` (melee): re-add a probe (see the removed discovery probes in `CombatEventPatch.cs` git history), read that event's attacker, match the model, and enqueue its GUID(s) onto `fireGuidQueue` — the per-instance resolution + pose-hook playback are already shared.
- **Clip timing** — the event fires at strike *start*; if a clip needs to run longer than the strike visual, `…StrikeTerminated` bounds it.
- **Rapid fire** — already handled: the trigger is per-entry and re-entrant (a shot mid-clip restarts the single pass cleanly).

---

## 6. Reproducing the investigation

- **Decompiler:** `ilspycmd` 8.2 at `~/.dotnet/tools/ilspycmd`.
- **Game assemblies:** `…/Steam/steamapps/common/Humankind/Humankind_Data/Managed/` — combat lives in `Amplitude.Mercury.Firstpass.dll` (also `Amplitude.Mercury.dll`).
- **Decompile + search:**
  ```bash
  ilspycmd -o <outdir> Amplitude.Mercury.Firstpass.dll
  grep -aoE "class SimulationEvent_[A-Za-z]+" <outdir>/*.cs | sort -u   # the event menu
  grep -nA20 "class SimulationEvent_ArtilleryStrikeStarted" <outdir>/*.cs   # its payload + Raise
  ```

See also: the animated-model pose hook and the *Freeze donor animation* note in **Capabilities.md** / **Factory-Manual.md** (the pose machinery this feature extends), and the "mostly-static rigged model breaks the animated bake → bake static / DIY-rig" trap.
