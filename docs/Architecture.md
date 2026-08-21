# Architecture — the invariants

**Who this is for:** anyone changing the plugin who isn't its author. [Code-Map.md](Code-Map.md) says *where* things
live; [Decisions.md](Decisions.md) says *why* individual choices were made; this page says **what must stay true** —
the rules the runtime depends on that no compiler enforces. Every rule here was learned from a real failure; the
failure is named so the rule is falsifiable, not folklore. Nearly all of it already exists as comments in the source.
This collects it in one place so a maintainer can read it in ten minutes instead of finding it by breaking something.

If you change code and one of these stops being true, update this page in the same commit.

---

## 1. The shape: two halves, one contract

- **Editor half** — the HAF Authoring Tools in Unity ([ENCReload](https://github.com/sswelm/ENCReload)). It *bakes*:
  turns a model into Amplitude `Skeleton` / `ClipCollection` / mesh / atlas assets and writes `pack.json`.
- **Runtime half** — this repo, a BepInEx plugin. It *injects*: reads the packs, registers the baked assets in the
  live game, repoints pawns and districts onto them.
- **The contract** — a JSON pack, whose shared fields are defined **once** in `Haf.Schema` (netstandard2.0) and
  inherited by both halves' model classes ([Shared-Schema.md](Shared-Schema.md)). The pre-push gate's parity check
  fails if a read key isn't written or a GUID hand-list drifts.

**Invariant:** the runtime has **no compile-time reference to Unity-editor or game code**. Its only game surface is
string-based reflection (§4). That is why CI builds from public sources with no game files, and why the test suite can
host the plugin in a plain xUnit process.

**Invariant:** there is **no cross-repo copy** of editor code in this repo ([Decisions](Decisions.md): "a cross-repo
copy is either authoritative or it doesn't exist"). The `baker/` snapshot was deleted 2026-08-21; do not bring one back.

## 2. Threads

Humankind runs its simulation on a separate thread from Unity's main thread, and several of HAF's Harmony hooks fire
on it. The rule that guards this (and the failure it came from — two confirmed races, 2026-08-21) is in
[Decisions](Decisions.md) *"Thread safety is about shared HEAP, not the Unity API"*. The operational facts:

**Hooks that run (or may run) off the main thread:**

| Hook | Seam | What it may touch |
|---|---|---|
| `Hk_ArtilleryStrike` | `ArtilleryStrikeStarted` (sim) | reads via `GetMember`; **enqueues** to `ModelEntry.fireGuidQueue` (`ConcurrentQueue`) |
| `Hk_BattleStarted` | `SimulationEvent_BattleStarted.Raise` (sim) | reads via `GetMember`; **enqueues** to `battleCryQueue` |
| `Hk_AnimatedBonePoolHeadroom` | `PawnManager.Load` (per session, possibly off-thread) | **sets** `reloadRearmPending` (volatile) |
| Sandbox.Load hook | `Sandbox.Load` (save-load, possibly off-thread) | **sets** `districtResetPending` + `reloadRearmPending` (volatile) |
| `FacingPersist` save/load | game save/load (may be off-thread) | reads a main-thread snapshot under `lock`; arms a file for the tick |

Everything else — `Plugin.Update`, every `Process*`/`Poll*`/`Tick*`, the pose hook, the district handlers — is
main-thread.

**Invariants:**

1. **Off-thread code never touches Unity objects.** It queues work; `Plugin.Update` drains it
   (`ProcessFireQueues`, `ProcessBattleCries`, `ConsumePendingReloadRearm`, `ConsumePendingDistrictReset`,
   `DrainDistrictDestroys`). `UnityEngine.Object.Destroy` in particular is main-thread-only — hence the destroy
   *queues*, never a direct Destroy from a reset.
2. **Off-thread code never mutates a plain collection the main thread reads.** Shared state is one of exactly three
   shapes: a **concurrent type** (`ConcurrentQueue`, the `ConcurrentDictionary` reflection caches in
   `UniversalInject.Reflection.cs`), **locked on every access** (`e.stateSamples`, `e.activeFires`, `e.deploySamples`,
   `e.phaseTracks`, `FacingPersist.live`), or **published-once-and-snapshotted** (next point).
3. **`entries` is published once and never mutated.** `LoadRegistry` builds a fresh list and assigns the field in one
   write (`entries = built`). Readers — including the sim-thread `FindEntryForUnitDefinition` — take `var snap =
   entries` and iterate the snapshot. A retry publishes a *new* list; it never `Add`s into the live one. (The 07-19
   review found the race that rule fixed.)
4. **"Main-thread only" in a comment is a claim, not a guard.** Before trusting it, grep the hooks in the table above
   for the path. The two 08-21 races were both behind exactly that comment — `GetMember` hides a dictionary insert
   behind a read-shaped call; `ResetDistrictSessionState` hid thirteen `Clear()`s behind "reference-nulling".
5. **Per-frame hot paths don't allocate for logging they won't emit.** `Plugin.Diag` is gated on `VerboseLog`, but
   its *argument* is built by the caller; on a per-pawn-per-frame path, guard the construction too.

### 2b. Per-frame cost — measured, bucketed, never estimated

The full treatment — the meter, the baseline, the investigation recipe — is [Performance.md](Performance.md); the
invariants are repeated here because they are invariants. `FrameCost` times every per-frame entry point (the `Update` fan-out, the pose hook split vanilla/ours, sub-buckets
inside the hot paths) and prints µs/frame per bucket to the F8 panel and the log. The rules it enforces
([Decisions](Decisions.md) "Per-frame cost is a number"):

- **A new per-frame path gets a bucket when it is written.** Unbucketed cost is invisible cost.
- **No full-scene `FindObjectsOfType` on a timer.** It is ~50 ms on a busy map. The sub-pawn source
  (`SubPawnScan.cs`) walks the presentation tree instead and self-verifies against the scan once per session; the
  terrain-hug district map is dirty-driven from the district hook. If you must scan, mark it dirty from an event and
  cap the cadence in tens of seconds.
- **No retry-every-frame until something exists.** The scoped-district bind walked every leaf of every district each
  frame for the first 5 s of every load. Throttle unbound retries (twice a second is plenty).
- **Resolve reflection once, not per frame.** `AccessTools.TypeByName` is an uncached assembly walk; bone-name lookups
  are a reflection read + a string alloc per bone. Cache per entry, keyed on whatever can change the answer.
- **The per-pawn path uses `PawnFast`.** Boxed-struct reflection costs ~0.5-1 µs per get/set on Mono; the compiled
  accessors (`FastMember`) cost ~10 ns and write INTO the box the same way. Every accessor has a reflection fallback
  — a game update that renames a field degrades to the old speed, never to a crash — and `[PawnFast]` in the log
  says which path is live.
- **Two `Physics.RaycastAll` per pawn per frame is a budget line item.** Sample, hold, ease.

## 3. Session lifecycle — what re-arms, when, in what order

The game rebuilds its presentation world per **session** (new game, save-load, in-session reload), but some of its
own registration runs once per **process**. HAF learned these seams the hard way ([Animated-Runtime.md](Animated-Runtime.md) §2/§5);
the compressed facts:

| Seam | Fires | HAF uses it for |
|---|---|---|
| `AnimationManager.AnimationLoad` | **once per process** (even across a main-menu round trip) | first registration of skeletons + clip collections, *before* `Apply` builds the GPU buffers |
| `PawnManager.Load` | **every session** (save-load, reload, **and New Game**) | the universal re-arm request — `RequestReloadRearm()` |
| `Sandbox.Load` | save-load only | additionally flags the **district** reset so it lands before the district hooks bind |
| `PresentationPawnDefinitionAddOn.Load` | per unit type, lazily, as units come into view | the repoint itself (`RepointMatch`) — self-discovers the body mesh name, swaps skeleton/mesh, isolates the skin |

**Invariants:**

1. **Everything a session produces is session-scoped state and is reset on re-arm**: learned ids (`skeletonId`,
   `animId`, `descId`, the per-role anim ids), the per-unit state maps keyed by unit GUID / sub-pawn instance id (a new
   game can *reuse* those ids), the isolated layer / hand-prop layer / adjusted-atlas clones, the AudioListener latch,
   the district tiles / leaves / bind slots / scoped states. **Since 2026-08-21 this is enforced, not remembered:**
   every static collection in the plugin must carry `[SessionScoped]` (the `SessionState` registry clears it on the
   matching reset — `Model` in `RearmModelRegistration`, `District` in `ResetDistrictSessionState`),
   `[SessionScoped(Manual = "site")]` (reset by hand at the named seam — lock-guarded, nulled, or owned by another
   hook) or `[ProcessLived("why")]` (a type cache, a name-keyed once-log, per-tick scratch). A bare static collection
   fails `SessionStateTests` in CI — no game, no Unity. The first run of that test found two descId-keyed maps that had
   never been cleared (`sizeFormApplied`, `sizeFormUnitName`: the formation-by-size swap silently skipped in a second
   session) plus the turn/hug/aim state lists. What the registry cannot prove is **order** — the hand-written lines
   around the bulk clear (`cachedEra`, the per-entry id resets, the layer destroys, `S = new ScopedState()`) still own
   the sequence; keep them in the same function. Non-collection statics (`registered`, `cachedEra`, `deployMoveState`)
   are outside the rule and stay on the hand-list.
2. **Registration must precede `Apply`.** `Apply` snapshots `BoneInfos` into the GPU skeleton buffer; anything you
   change on a skeleton afterwards (a rebase, a rename) never reaches the GPU. Hence `RebaseRootIdentity` runs inside
   `EnsureRegistered`, before `RegisterMeshCollection` + `Apply` — not in `RepointMatch`.
3. **The district reset must land before the district hooks bind** in the new world, or they bind onto the previous
   session's dead leaves (the Oracle incident). Since 2026-08-21 the reset is *flagged* off-thread and *performed* on
   the main thread at the entry of every district handler (idempotent) and on the `Update` tick. Ordering preserved;
   keep it that way — don't move the consume later, and don't make the handlers skip it.
4. **Only destroy what you created.** `texOwned` is true only for textures HAF built (`LoadSkinPng`,
   `BuildAdjustedAtlas`). The raw bundle atlas from `LoadAtlas` is a **shared game asset**: destroying it makes
   `AssetDatabase.LoadAsset` return null on the next reload (the organ-gun-goes-red bug). The same discipline applies
   to layers (`isolatedLayer`, `handPropLayer`, the district clones) — every clone HAF makes is queued for destruction
   on reset; nothing HAF didn't make ever is.
5. **`registered` latches only on a successful load.** A transient registry-load failure must leave it unlatched so
   the retry can register; and `animMgrRef` is captured *before* the zero-model early return (a rules-only pack still
   needs the manager for scaling).

## 4. Reflection — the contract with a closed engine

HAF binds to `Amplitude.*` by **name**, at runtime, through Harmony. That is inherently fragile; the project's answer
is not to remove reflection but to make drift **loud and localised** ([Decisions](Decisions.md) "Make reflection drift loud").

**Invariants:**

1. **Every game type name lives in exactly one place: `GameBinding`.** Call sites use `GameBinding.<Type>`, never a
   scattered `TypeByName("…")`. A rename is fixed in one line.
2. **A type whose name never appears in code is DERIVED, not guessed.** Structs HAF reaches as array elements or
   field values (`PawnEntry`, `FragmentEntry`, `SkinnedMeshInfo`, the level-build channel chain…) are resolved by
   walking the same path the runtime walks — `ElementType(FieldOrPropType(Anchor, "member"))`. A renamed anchor
   *or* a renamed struct member both surface as one named line in the report. Never add a `Cached("GuessedName")`
   for a type you haven't seen in a decompile.
3. **Every by-name member read on a non-diagnostic path is in the `Catalog`**, attributed to the receiver the code
   *actually* reads it off (the A1 lesson: a member listed on the wrong type passes validation and guards nothing).
   What is deliberately outside is listed in the catalog itself (the `DistrictDebug`-gated dumps, Prober, two
   members that exist only on runtime subclasses).
4. **Run `tools/check-bindings.sh` before you launch.** It validates the whole catalog — derived chains included —
   against the game DLLs in seconds, and it catches wrong receivers and non-existent members (it caught five on
   the day the catalog was closed). The in-game `haf_bindings_report.txt` is the live twin; both must say `N/N`.
5. **All member access goes through `GetMember`/`SetMember`** (`UniversalInject.Reflection.cs`) — property-first,
   finds non-public, cached per `(type, name)`, null on a miss. The cache is a `ConcurrentDictionary` because the
   sim-thread hooks use it too (§2). Do not regress it to a `Dictionary` with a comment.
6. **Harmony patch counts are honest.** `Plugin.Awake` counts the methods Harmony *actually* patched and warns per
   hook whose `TargetMethod` resolved nothing. A hook that self-disables must return `null` from `TargetMethod`, not
   patch a stand-in.
7. **A failure in one model must not take down the rest.** Registration and repoint isolate each entry in its own
   `try`; a missing asset or a reflection miss skips that entry and logs it — it never aborts the loop that would
   skip `Apply` for everyone.

## 5. The registry and packs

- **Parse is generic, over the shared schema.** `ParseModels` whitelist-strips pack JSON to declared config keys,
  then `ToObject<ModelEntry>()`; the regex fallback covers a hand-edited file with a syntax error, *including the
  wrapper header* (`modId` / `schemaVersion` / `dependsOn` / `loadAfter` / `overrides`). A typo must never silently
  drop the header and downgrade a declared override to a first-wins conflict.
- **Pack order follows Humankind's own mod order**; `dependsOn` is enforced (a missing dependency skips the pack,
  named in `haf_load_report.txt`); duplicate `modId`s are rejected; undeclared clashes are first-loaded-wins **and
  logged loud** ([Multi-Mod.md](Multi-Mod.md), [Decisions](Decisions.md)).
- **Unit → entry matching is ONE function**: `LongestMatch` on the full `pawnDescription`, then `coreDesc`
  (the `_NN`-stripped form, >4 chars). Every path — repoint, combat, sound, the movement polls — resolves through it,
  so they can never disagree about which entry drives a unit. `coreDesc` is computed once at publish, never per call.
- **Validation explains, it never blocks.** The pack validator's rule set runs pre-bake, on the Validate button, in
  the mod build (`-strict` fails CI) and at boot; a Warning means the feature degrades, an Error means the entry
  can't work — but the pack still loads and the report says why ([Pack-Validator-Design.md](notes/Pack-Validator-Design.md)).
- **Numbers are invariant-culture everywhere** — files HAF reads *and* every log line that interpolates a float
  (`Inv($"…")`); the `combatZ` line once printed `-0,13` on a Dutch locale.

## 6. Districts — two render paths, two ledgers

The district axis is **its own class, `DistrictInject`** (`DistrictInject.cs` + `DistrictInject.Scoped.cs`, since
2026-08-21). It was a partial of `UniversalInject`, which meant every one of its ~40 statics was writable from any other
partial — the shape that let the session reset be called from a hook in another file. Now the rest of the plugin sees
only its `internal` surface (the hook entry points, `ResetDistrictSessionState`, `distModels`/`IsScopedDistrict`/
`scopedStates` for the smoke test), and `DistrictInject` reaches back only through `using static UniversalInject` for
the reflection and asset-loading helpers. **Keep it that way**: a new district feature goes in `DistrictInject`; a
new shared helper goes in `UniversalInject` and is imported, never duplicated.

A custom district renders through one of two paths, and they keep **separate state**:

| Path | Selected by | Live-tile ledger | Texture ledger |
|---|---|---|---|
| **Isolate** (private per-instance leaf) | default | `DistrictModel.tiles` | `DistrictModel.texApplied/texWait/texErrors` |
| **Scoped** (data-authored selector — the reactor) | `selectorGuid` in the registry, or `DistrictSelectorTile` config | `ScopedState.refreshPlbcs` | `ScopedState.texApplied/texWait/texErrors` |

**Invariants:**

1. **The isolate swap must leave scoped districts alone** (`IsScopedDistrict` guard in `TickDistrictMeshSwap`), or the
   two fight for channel 0.
2. **Scoped state is per district** (`scopedStates[name]`, the `S` proxy is pointed at the current one before any
   scoped work). Two scoped districts in one registry must not share texture / B&W / flatten state.
3. **Anything that reports on districts reads BOTH ledgers.** The smoke harness once read only `d.tiles` and declared
   the district path "UNTESTED" while the reactor was bound on screen.
4. **`texApplied` is not "texture succeeded."** Both apply paths give up after 3 exceptions by latching
   `texApplied = true` so the poll stops. Judge `texErrors` first.
5. **Per-tile targeting, per-entry sharing.** A district built on many tiles has one `PresentationDistrict` each; the
   channels HAF repoints are per tile, while the private leaf / layer clone / texture bindings are one per entry and
   shared. A single "current plbc" slot made ownership ping-pong between instances — that shape is gone; don't
   reintroduce it.

## 7. Animation — the engine contract in one paragraph

Custom animation is **rotation-only on the GPU path**, pose time is **normalized** (`Time = seconds / duration`), and
the per-frame pose decision for every pawn runs in the pose hook. The *decisions* (which clip, where in it) live in
the pure `PoseMath`; the hook keeps the I/O and the locks. Phases are tracked **by position**, not array slot — the
pawn array is rebuilt on every zoom and slot-derived state snaps visibly. The three match radii are **deliberately
different** (state 4u, fire 4u, deploy 3u) — a tidy-up that unifies them breaks formations. **The nine clip roles are
one table** (`ClipRoles.cs`, `ModelEntry.Roles[ClipRole]`): never add a role as a new field family, and never write
an "all roles" site as a list — loop `ClipRoles.All` (the lockstep-list shape shipped two bugs). Full detail:
[Animated-Runtime.md](Animated-Runtime.md), [Unit-Combat-Behavior.md](Unit-Combat-Behavior.md).

## 8. Verification — what proves what

| Layer | Proves | Runs |
|---|---|---|
| xUnit suite (`Tests/`) | the pure cores: parse/resolve, validator, `GameBinding` resolution, `DialConfig`, `PoseMath` (with legacy-oracle parity), the smoke *verdict* | every push (CI, no game files) |
| `tools/check.sh` pre-push gate | build, tests, docs links, schema parity | every push, locally |
| `tools/check-bindings.sh` | the whole reflection catalog against the game DLLs | after a game update; before a launch when the catalog changed |
| In-game **Smoke Test** (F8) | the plugin came up: bindings, injection, per-entry assets/roles/sounds/files, GPU budget, district tiles on both paths + texture health, seam write-back, shared Harmony seams | by hand, and it writes `haf_smoke_report.txt` |
| A **drill** | the feature actually does the thing on screen | by hand — nothing above replaces it ([Decisions](Decisions.md): "a tool is not trusted until it is DRILLED") |

**Invariant:** to make more of the runtime testable, **move the decision out of the method that does the I/O**
([Decisions](Decisions.md)) — `DialConfig` and `PoseMath` are the template; `Districts` is the obvious next candidate.
Do not try to unit-test the reflection layer directly, and do not build an in-game test framework.

## 9. Logging discipline

- **Log once, by key**: `Plugin.Once(key)` / `LogOnceWarning` / `DiagOnce` replace hand-rolled `static bool xLogged`
  guards (the pattern's failure mode is forgetting one).
- **Verbose is opt-in** (`VerboseLog`): bring-up detail goes through `Plugin.Diag`; a player's log shows decisions and
  failures, not per-pawn chatter.
- **Every launch writes three machine-readable files** next to the config: `haf_load_report.txt` (which packs, which
  decisions), `haf_bindings_report.txt` (which game bindings), `haf_smoke_report.txt` (the last F8 verdict). A bug
  report with those three attached is usually diagnosable without a repro.

---

### Adding to this page

A rule belongs here if (a) violating it produces a failure that is hard to trace back to the violation, and (b) the
compiler and the tests won't catch it. Name the failure. If a rule has a test or a gate check, it belongs in
[Testing.md](Testing.md) instead.
