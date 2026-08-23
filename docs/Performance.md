# Performance — what HAF costs per frame, and how it's kept that way

HAF runs code **every frame**: a fan-out of polls from `Plugin.Update`, and a Harmony hook on every pawn the game
adds. This page is the contract for that cost: how it is measured, what it is today, the rules that keep it there,
and what to do when a number grows. It exists because the first time anyone measured it, the answer was **15× the
confident estimate** — see [Decisions](Decisions.md) *"Per-frame cost is a number in the F8 panel, never an estimate"*.

## 1. The meter — `FrameCost`

Press **F8** in-game. Under the GPU-buffer lines:

```
HAF 780 µs/frame (2.3% @ 30 fps) | Update 447 µs | pose vanilla 116 µs = 82 adds × 1414 ns | pose ours 218 µs = 36 adds × 5996 ns
top: SelectorTile 231.8 µs, PoseOurs 217.6 µs, PoseVanilla 115.7 µs, PoseAnim 79.6 µs, AnimStates 40.3 µs, EngineAudio 34.7 µs
```

The same line is written to `BepInEx/LogOutput.log` once a minute as `[FrameCost]`, so a session leaves a trail.

How to read it:

| Field | Meaning |
|---|---|
| `HAF N µs/frame (x% @ fps)` | what HAF did **inside its timed buckets** in an average frame of the last 5 s — see the scope note below; the percentage is of the **measured** frame (33 ms at 30 fps) |
| `Update N µs` | the `Plugin.Update` fan-out — every poll, every tick |
| `pose vanilla … = A adds × N ns` | the pose hook on pawns that are **not** ours (the early-out path): how many pawn-adds per frame, and the cost of each |
| `pose ours … = A adds × N ns` | the pose hook's full path on **our** pawns — this one scales with how many custom units are on screen |
| `top: …` | the six most expensive buckets. Sub-buckets (`PoseAnim`, `PoseDonor`, `SelTileLoop` …) are nested inside their parent and **double-count in a sum** — read them as a breakdown, not additively |

**What the meter covers, exactly (narrowed 2026-08-22 after a review found the previous wording overstated).** The
33 timing sites cover the `Plugin.Update` fan-out, the pose hook (split vanilla/ours) and the district path. They do
**not** cover: the other ~36 Harmony hooks — several of which run per frame or per event, e.g. the mouse-cover
postfix on `SpecificUpdate`, the `GetBoneTRS` prefix+postfix, and the audio `PostEvent` hooks; `OnGUI`, which walks
the GPU budget by reflection on **every repaint while the F8 panel is open**; Harmony's own dispatch across every
patched method, since `FrameCost.Begin()` runs *inside* the postfix; and GC caused by HAF's own allocations, which
lands in someone else's frame. The number is also a **mean over 5 s** — a 40 ms hitch once a second reads as
1.2 ms/frame and looks unremarkable, so the meter is a budget tool, not a hitch detector. Read it as "HAF's steady
per-frame cost from Update plus the pose hook", which is what it measures honestly.

Every per-frame entry point *in that scope* lives inside a named bucket (`Patches/FrameCost.cs`). The meter itself
costs two `Stopwatch` reads per bucket per frame — well under what it can measure.

**The 30 fps cap.** The reference machine caps the game at 30 fps. µs/frame is absolute; the percentage is against a
33 ms frame, so the same work at 60 fps is double the percentage. Throttles written in *frames* (`% 6`, `% 30`) run
half as often as they would at 60 — a poll that reads as cheap here may be twice as frequent elsewhere.

## 2. Today's baseline (2026-08-21, the ENC reference pack, ~20 custom units + a scoped district on screen)

| Bucket | Before the pass | After | What it was |
|---|---|---|---|
| **Total** | **5,662 µs (16.7%)** | **565–780 µs (1.7–2.3%)** | |
| Load spike (first 5 s) | 42,000 µs/frame | — | the scoped-district bind retrying its full leaf walk every frame until the donor layer existed |
| `EngineAudio` + `SubPawnVisuals` | ~1,700 µs | ~35 µs | two independent full-scene `FindObjectsOfType` scans on timers (60–100 ms stalls, averaged) |
| `WonderRows` | 1,350 µs | 0 | an uncached `AccessTools.TypeByName` assembly walk every 30 frames, forever |
| `PoseOurs` (per our pawn) | 25–57 µs | ~5 µs | ~60 reflection get/sets on the boxed `PawnEntry`, then two raycasts + a bone-name re-resolve per helicopter per frame |
| `AnimStates` | 204 µs | ~40 µs | a per-unit name resolve + `ToList()` for every army every 3 frames |
| `SelectorTile` | ~210 µs | **6.3 µs** | per-district overhead in the scoped poll. Called "diffuse" and left alone in the 08-21 pass; **fully accounted for on 08-23** — the poll was walking **2,668 tracked districts every frame to find 1**, on a list nothing ever pruned. See §6. `Update` fell 391 → 167 µs with it. |

**A correction worth keeping (2026-08-23).** The Fx-tree caching was first written up as taking `SelectorTile` "out of
the top six, below 10.7 µs". That was measured in a session with 3 injected models and 1 live pawn against the
original's 19 and 18; the reasoning for quoting it anyway — that the district-state line was identical in both
(`2 district(s) [1 tile(s) live, 1 scoped]`) so the bucket was comparable — **did not hold.** A later heavy run put it
straight back at the top. Two runs reporting the same district state differed ~20× on that bucket, so that line does
not capture what drives it, and `SelectorTile` cannot be compared across unlike scenes. Like-for-like, both heavy:
HAF total 608 → 570 µs, `Update` 396 → 391 µs, `SelectorTile` 227.7 → 218.7 µs — **~9 µs, about 4%.** What the change
unambiguously fixed was the **log**: 23,194 → 149 AccessTools warnings (94% of the file), because that walk runs on the
bind retry ~1/s, not per frame. It was worth far more to the log than to the frame.

Also not steady cost: the **load-tier smoke test** (`SmokeOnLoad`, default on) runs once per session on the first
frame after the loading screen hides — a few ms, at a boundary the player is already waiting on — and never again
until the next load. The live-pawn checks (one `FindObjectsOfType`) stay on the F8 button.

Not HAF's steady cost, but visible in the meter: **`DistrictDebug = true`** (in the `[Debug]` config section since
2026-08-21) runs the repository dump at load — ~40 ms/frame for the first 5 s of every session. It is a diagnostic;
keep it `false` for play.

### A reading after the `SelectorTile` fix (2026-08-23, build `2026-08-23 15:31 UTC`)

First session on a build carrying §6's fix. Smoke `[full]` **PASS**, 0 injection errors, 22 models loaded / 19
injected, 2 district(s) `[1 tile(s) live, 1 scoped, 1/1 textured]`.

```
HAF 348 µs/frame (1.0% @ 30 fps) | Update 185 µs | pose vanilla 81 µs = 52 adds × 1573 ns
  | pose ours 82 µs = 18 adds × 4544 ns | districts 0 skipped 0 µs (0 ns ea), 1 ours 6.6 µs (6564 ns ea)
top: PoseOurs 81.8 µs, PoseVanilla 81.0 µs, EngineAudio 46.9 µs, AnimStates 45.4 µs, RespawnPostLoad 20.6 µs, PoseAdjust 20.4 µs
```

**The line that matters is `districts 0 skipped`.** §6's scan walked 2,668 tracked districts every frame to find one;
this session skips **zero**, at 0 µs, and the per-match work reads 6.6 µs against the 6.3 µs measured when the fix
landed. `SelectorTile` no longer appears in the top six at all. That is the fix confirmed on a live scene rather than
on the bench that produced it.

**What this reading does NOT say.** The total is 348 µs against the 565–780 µs above, and **that is not a 2× win** —
it is a different scene, which is the exact error the correction above this section exists to prevent. Read the
buckets: 18 live pawns (`pose ours 18 adds`) puts this at the *light* end of the range those figures span, and
`PoseOurs` at 4,544 ns/add against the ~5 µs/pawn in §5 is the like-for-like number, i.e. unchanged. Nothing here
claims an improvement outside the district bucket, because nothing here measured one.

**The meter's noise floor — measured by accident, worth keeping (same day, build `16:31 UTC`).** The plugin was
redeployed with **zero `.cs` changes** (docs and gate scripts only) and the smoke re-run, which makes the second
session a **control**: identical code, near-identical scene (19 live pawns against 18). Everything that moved is the
meter's own variance.

**Three samples, not two** (a third session followed on build `16:40`, which carried the district-boolean retype —
a real code change, but not one that touches these paths). Two points can only ever state a difference; the third is
what makes this a *range*:

| | `15:31` | `16:31` | `16:40` | spread |
|---|---|---|---|---|
| HAF total | 348 µs | 325 µs | 338 µs | ~7% |
| `Update` | 185 µs | 162 µs | 166 µs | ~13% |
| `PoseOurs` per add | 4,544 ns | 4,023 ns | 4,092 ns | ~11% |
| `PoseVanilla` per add | 1,573 ns | 1,748 ns | 1,783 ns | ~12% |
| `SelectorTile` (ours) | 6.6 µs | 5.4 µs | 5.4 µs | ~20% |
| live pawns (the scene) | 18 | 19 | 19 | — |

**So a swing under roughly 10% on these buckets is not a signal.** §4 says split a bucket when a number surprises;
this says what "surprising" has to clear first, and it means no single figure on this page is a constant — each is
one draw from a band this wide. Note the third sample lands *between* the first two rather than extending the range,
which is what a noise band should do and what a real regression would not.

The one number that did *not* move is the one that matters most: `districts 0 skipped` in **all three** sessions —
§6's fix confirmed on three independent runs rather than the bench that produced it.

Also recorded from the same panel — GPU mesh buffers, the other budget a custom model spends against:
`L0 'Visual'` 3,061,435 / 5,000,000 verts (61%), meshes 4,608/8,000 · `L2 'MeshWithSkeletonParticleIndexBuffer'`
(our models) 873,226 / 2,000,000 verts (43%), meshes 706/3,500. See [Vertex-Budget.md](Vertex-Budget.md).

## 3. The rules

Learned in one afternoon, each from a bucket that surprised ([Architecture](Architecture.md) §2b):

1. **A new per-frame path gets a bucket when it is written.** Unbucketed cost is invisible cost. Wrap the call in
   `Plugin.Update` (or the hook) with `FrameCost.Begin()` / `End(bucket, t)` and add the name to `FrameCost.names`.
2. **No full-scene `FindObjectsOfType` on a timer.** ~50 ms on a busy map, delivered as a hitch. Walk the presentation
   tree instead (`SubPawnScan.cs` is the template: targeted, *self-verified against the scan once per session*, with
   the scan as the fallback), or mark dirty from an event and cap the cadence in tens of seconds.
3. **No retry-every-frame until something exists.** Throttle unbound retries; twice a second is plenty.
4. **Resolve reflection once — but know which reflection.** Measured 2026-08-23 (see §7): the four resolvers differ by
   five orders of magnitude, and the expensive one is not the one that looks expensive.

   | call | cost | cached |
   |---|---|---|
   | `Type.GetField` | 20 ns | 42 ns — **caching it is a 2x pessimisation**; the runtime already keeps a per-type member cache |
   | `AccessTools.Field` / `.Property` / `.Method` | 41–344 ns | ~42 ns |
   | `AccessTools.TypeByName` — **hit** | 1,032 ns | 19 ns |
   | `AccessTools.TypeByName` — **miss** | **7.85 ms** | 13.7 µs |

   So: member lookups are cheap and a memo on them is worth little (and on `Type.GetField`, worth less than nothing).
   **Type** lookups are the ones that matter, and a *failed* one is 7.85 milliseconds — a quarter of a 30 fps frame,
   paid again on every call, because `TypeByName` memoises nothing. Resolve types through `GameBinding.Cached`, never
   a raw `TypeByName`; `tools/check-catalog.sh` fails the gate on one.
5. **The per-pawn path uses `PawnFast` — including the reads that GATE it.** Boxed-struct reflection is ~0.5–1 µs
   per get/set on Mono; the compiled accessors (`FastMember`) are ~10 ns and write into the box identically. Every
   accessor has a reflection fallback, so a renamed game field costs speed, never function — `[PawnFast]` in the log
   says which path is live.
   *Corollary, learned 2026-08-23:* the 08-21 pass compiled everything inside the PawnEntry **struct** and stopped
   there, leaving the two reads on the pawn **manager** (`pawnEntries`, `pawnCount`) on plain reflection — and those
   run *before* anything can tell whose pawn it is, so **every** add paid them. That was the entire 1,805 ns of
   `PoseVanilla`'s per-add cost; compiling them took vanilla to 1,006 ns and ours to 3,424 ns. **When optimising a
   hot path, the gate is part of the path** — an accessor table that stops at the type you were thinking about
   leaves the cost on the type you had to go through to reach it.
6. **Physics queries are budget line items.** Two `RaycastAll` per pawn per frame was the helicopters' entire cost.
   Sample, hold, ease.
7. **Don't build log strings the verbose gate will discard.** `Plugin.Diag` is gated, its *argument* is not.

## 4. When a number grows

1. **Read the bucket, not the code.** The `top:` list names it. If the bucket is coarse, add sub-buckets and run
   again — twice during the pass a fix aimed at the wrong cause (reflection on the entry; then the raycasts) until a
   sub-bucket showed the real one.
2. **Ask the DLLs before the game.** `tools/typeprobe` dumps a game type's real field/property layout from the
   Managed folder headlessly:

   ```
   dotnet tools/typeprobe/bin/Release/net8.0/typeprobe.dll "<Humankind>/Humankind_Data/Managed" PawnEntry PresentationSquadron
   ```

   It answered "why is the fast path off?" (`HideFactor` is a packed *property*) and "where do a squadron's pawns
   live?" (`PresentationAirPatrolController`) in seconds.
3. **Fix one bucket, redeploy, read the line again.** Numbers move between runs with what's on screen; compare like
   with like (same save, a minute idle, a minute panning).
4. **Keep the old path as the fallback** when replacing a slow-but-known mechanism with a fast one, and verify the
   new one against the old in-game at least once (the sub-pawn walk logs `walk verified … none missed`).

## 5. What scales with what

- **Custom units on screen** → `PoseOurs` (~5 µs each per frame today). A hundred custom pawns ≈ 0.5 ms.
- **All units on screen** → `PoseVanilla` (~1.4 µs per pawn-add — the early-out).
- **Armies on the map** → `AnimStates` / the sub-pawn walk (cached unit→entry, so mostly vanilla armies skipped cheaply).
- **Scoped districts** → `SelectorTile` (~0.2 ms for one; a per-district loop, so roughly linear).
- **Zoom changes** → pawn-add spikes (the engine re-adds every pawn), visible as a brief rise in both pose buckets.

## 6. `SelectorTile` — 219 µs → 6 µs (2026-08-23, RESOLVED)

**It was 219 µs/frame: 36% of HAF's entire per-frame cost, and the largest unexplained number in the runtime.** Looked
at twice before: 08-21 called it *"diffuse per-district overhead, left as is (0.6%)"*; 08-23 accounted for **~9 µs**
(the Fx-tree walk re-resolving every field on every visit) and left ~210 µs unattributed. Splitting the loop into
scan-vs-work produced the number that ended the guessing:

```
districts 2668 skipped 237.3 µs (89 ns ea), 1 ours 5.6 µs (5592 ns ea)
```

**2,668 districts walked every frame to find one.** The scan *was* the bucket; the per-match work was 5.6 µs. Three
causes, all in the tracking list — nothing ever pruned a destroyed district, the dedup on Add was a linear scan, and
the poll iterated everything instead of the matches. Drilled in-game twice, and `Update` moved by what the
measurement predicted:

| | before | after |
|---|---|---|
| districts walked/frame | 2,668 | **1** |
| `SelectorTile` | 218.7 µs | **6.3 µs** |
| `Update` total | 391 µs | **167 µs** (−224 µs, against 237 µs of measured scan) |

> **Read the bucket, not the total.** HAF's total went 612 → 671 µs across those runs and that is *not* a
> regression: the later scene had **46 live pawns against 18**, and `PoseOurs` alone went 74 → 294 µs. This is the
> same trap that produced a wrong claim earlier the same day — see the correction under §2.

**What it cost to get there, worth repeating:** the 08-21 pass recorded a *verdict* where it should have recorded a
*measurement*. "Diffuse, left as is" survived two years of readings and was simply wrong — the bucket was a list
nobody was pruning, growing with session length. The instrumentation that settled it took an afternoon; the verdict
had already cost two passes.

### Still open in this bucket

The per-match work reads **~6 µs steady but ~497 µs during the load window**, while selectors are still resolving —
visible now only because the 237 µs scan is no longer hiding it. Same shape as the 42 ms/frame load spike in §2:
a boundary the player is already waiting on, so it is not urgent, but it is the next thing in `SelectorTile`.

### The historical record (what was ruled out, and how)

### What is already known, from the buckets that exist

| Reading | What it rules out |
|---|---|
| `SelTileLoop ≈ SelectorTile` in every sample | Not the config parse, not anything outside the district loop |
| `SelTileBind` / `SelTileAlbedo` / `SelTileFlat` never reach the top six | Not the bind, the albedo rebind, or the flatten — the cost is the loop's own head |

### What was ruled out by reading, 2026-08-23

Every per-loop diagnostic is correctly `DistrictDebug`-gated **and** latched, so all are no-ops in normal play:
`DumpPlbcLevers`, `DumpAllChannels`, `DumpGroundMatchers`, `DumpSelectorElements`, `DumpNativeGroundCandidates`.
`ResolveMainLayer` is cached after the first resolve. None of them contribute.

### The question the old buckets could not answer

The loop walks **every district the game presents** — `trackedDistricts` accumulates each live `PresentationDistrict`,
so it grows with the map — to find the one or two that are ours. That means 219 µs is one of two completely different
problems, wanting opposite fixes:

| If the cost is… | …the fix is |
|---|---|
| **many districts skipped, cheap each** | stop walking them: keep a matched subset, rebuilt when `trackedDistricts` changes. The per-district skip still costs a Unity fake-null check, which is a native interop call, not a reference test |
| **few districts, expensive each** | the per-match work is the target, and the existing sub-buckets narrow it further |

It turned out to be the FIRST, overwhelmingly — and the second cause was one nothing had suspected: nothing ever
pruned a destroyed district, so the list grew for the whole session.

### The instrumentation that settled it

`SelTileSkip` / `SelTileOurs` split the loop, and their **call counts are the district counts** — a number HAF has
never printed. The summary states both sides the way it already states the pose hook, and stays silent when the
district axis isn't running:

```
| districts 47 skipped 47 µs (1000 ns ea), 1 ours 180 µs (180000 ns ea)
```

> The ours-timer closes in a `finally`: that loop body has **six `continue` paths**, and an `End()` they skipped would
> under-count the time *and* the call count — the same accounting leak the 08-22 `Update` fix closed, where a bucket
> lost frames while its window kept aging and the meter read healthiest exactly when it was most wrong.

Two tests pin the summary segment so it cannot silently stop reporting; nine more cover the filter, the prune and
the O(1) dedup that replaced the linear scan.

## 7. Reflection resolvers — the 7.85 ms miss (2026-08-23)

The question that started this: *"are all reflection lookups fully cached now?"* The answer needed a measurement, not
an audit, and the measurement moved the target.

### What was measured

A benchmark over the four resolvers HAF uses, warm (the first-ever call on a cold type is ~2,200 ns of metadata
warm-up and was excluded — an earlier figure of "1,524 ns for `AccessTools.Field`" quoted in the GF commit was that
warm-up leaking into a short average, and is **wrong**; the real number is 131 ns):

```text
FIELD  own-public      AccessTools  130 ns | cached  42 | Type.GetField  18
FIELD  inherited-priv  AccessTools   84 ns | cached  43
FIELD  MISS            AccessTools  125 ns | cached  47 | Type.GetField  24
PROP   own             AccessTools   64 ns | cached  41 | Type.GetProperty 26
PROP   MISS            AccessTools  344 ns | cached  44
METHOD own             AccessTools   41 ns | cached  42
METHOD MISS            AccessTools  147 ns | cached  41
TYPE   hit             AccessTools 1032 ns | cached  19
TYPE   MISS            AccessTools 7.85 ms | cached  13.7 µs      <-- five orders of magnitude
```

The miss is not a cold-start artefact: three consecutive rounds measured 7.54, 7.56 and 7.58 ms, and distinct names
each time cost the same. `TypeByName` memoises **nothing** — not the hit, and certainly not the miss.

### Why that is a stall and not a slow path

A failed type lookup is what happens when a game patch renames something. The code around it is written to degrade
gracefully — `if (rfpType == null) return -1f;` — so it *looks* safe. It isn't: the caller retries, so the failure
repeats, and each repeat costs 7.85 ms with no exception and no log line. `DistrictInject.SchematicVis()` re-probed
**two** types on every call and is called from `UpdateMeshFlatness` every 10 frames for the whole session; if either
name ever broke, that is 15.7 ms every 10 frames — 1.57 ms/frame averaged, with a half-frame spike on the frames it
lands — presenting as "the game feels bad" and nothing else.

The same shape is worse where the code looks *most* careful. A `??` fallback chain spends a **full** miss on every
probe that is meant to fail. `BattleTurnPatch`'s attack replay probed three names for `AnimationVariableNames`, the
first two of which are alternates — ~15.7 ms on the frame a battle first replays an aligned attack. `Prober`'s
database resolve loops three candidate type names the same way.

### What changed

Every type name now resolves through `GameBinding.Cached` — 19 ns on the hit, and a miss that re-resolves in 13.7 µs
because it scans loaded assemblies instead of doing whatever `TypeByName` does on a name it cannot find. Fallback
chains became the accessor's own fallback list (`Cached(primary, alt1, alt2)`), which memoises the **winner**, so the
probes that are meant to fail are paid once and never again.

This was half-built already: the A6/A7 catalog sweeps added `RenderFeatureProvider`, `AssetReferenceRepository`,
`AnimationVariableNames` and others as accessors — but never migrated the call sites, so the names lived in two places
and the hot ones still paid full price. A8 finished it. `tools/check-catalog.sh` now fails on a raw `TypeByName`
outside `GameBinding.cs`, which is what stops it drifting back out a fourth time.

**Fallback order turned out to matter more than expected**, and `bindcheck` cannot police it — it reports 132/132
whether an accessor resolved on its primary or limped in on a fallback. `typeprobe` against the shipped DLLs found two
chains leading with a name that does not exist, i.e. paying a guaranteed 7.85 ms miss before reaching the real one:
`GroundMaterialTextureData` probed a **nested** form (`GroundMaterialAuthoringData+GroundMaterialTextureData`) that
is NOT FOUND in this build, and the battle replay's `AnimationVariableNames` probed *three* names of which **none**
match — the real type is `Amplitude.Mercury.AnimationVariableNames`, and the bare-name probe only ever resolved
through `ResolveType`'s simple-name branch, a `GetTypes()` walk of every loaded assembly. Both now lead with the name
the build actually has, old names kept behind them, and `Tests/TypeResolutionTests.cs` pins the order.

Also on the way: `GameBinding`'s
`_typeCache` is a plain `Dictionary` with no stated thread contract. It is main-thread-only in practice — audited: the
three sim-thread hooks reach reflection only through the `ConcurrentDictionary` in `UniversalInject` — so it is now
annotated `[MainThread]` rather than converted, with the audit written down next to it.

### Verified in-game (2026-08-23, game 1.30)

Not off a green build — from the log:

- `[GameBinding] OK — 132 game type(s) + their members all resolved. [game 1.30, verified]` — runtime resolution inside
  the Unity domain, which is the half `bindcheck` cannot prove by reading DLLs offline. Every A8 accessor and both
  reordered fallback chains included.
- `[FootprintMesh] reactor mesh -> 3D` → `-> FLAT (strategic footprint)` → `-> 3D`. That toggle only happens if
  `SchematicVis()` returned real band values on **both** sides of the crossover, so `RenderFeatureProvider` and
  `RenderFeatureSelector` resolved through the new accessors and `ComputeRenderState` ran. This is the path that would
  have paid 15.7 ms every 10 frames under the old code if either name had broken.
- Smoke test `[load]` and `[full]` PASS, 0 injection errors, 2 districts (1 scoped, 1/1 textured). Frame cost 322 µs
  (1.0% @ 30 fps), shape unchanged.
- **Zero type-lookup misses in the entire session.** All 197 `AccessTools` warnings in the log are *member* misses at
  ~130 ns each.

Not exercised: no battle was fought, so the attack replay's `ReplayAligned` never ran. The failure that was actually
plausible there — `AnimationVariableNames` not resolving under its new full-name primary — is ruled out anyway, since
the validator resolved it as one of the 132.

### What was NOT changed, and why

`DistrictInject.GF` stays a bare `Type.GetField`. A cache was written for it and measured away: 20 ns becomes 42 ns,
because .NET already keeps a per-type member cache and hashing a `(Type, string)` tuple costs more than the runtime's
own lookup. Member-level memoising is worth ~90 ns a call at best — real, but three orders of magnitude below the
thing that actually mattered. **The lookup that looked expensive was cheap; the one nobody was counting was 7.85 ms.**

## 8. `Formation` — 63 µs of re-deciding (2026-08-23, RESOLVED)

`Formation` entered the top six at **61.8 µs** in a battle session, and `Update` went 166 → 224 µs with it — outside
the ~13% band §2's control run measures for that bucket, so: signal, not the meter breathing. This is the first time
the published noise floor was used to *classify* a reading rather than describe one.

Two suspects, opposite fixes. The poll does a `pending` retry (throttled 1-in-60 frames) **and** `MaybeReinstantiate`,
a 1-in-5-frame walk over every army. Following §4, the bucket was split before anything was touched — `FormRetry`,
plus the per-army loop divided into armies rejected vs armies matched, the way `SelTileSkip`/`SelTileOurs` was:

```text
armies 35 skipped 63 µs (1794 ns ea), 3 ours 9.3 µs (3380 ns ea), retry 0 µs
```

**`retry 0 µs` — the throttle was never the issue**, and a fix aimed there would have been wasted. 87% of the bucket
was the scan. The number that mattered is **1,794 ns to reject one army**: a rejected *district* costs 89 ns
(`SelTileSkip`), so saying "no" here cost twenty times more — re-deriving the unit, its definition, its name, its
formation reference (two uncached reflection lookups) and up to sixteen `OrdinalIgnoreCase` comparisons across the
registry's links. Every scan, ~12×/s, for a verdict that **cannot change**: a unit's definition is fixed for its
lifetime.

So the verdict is remembered (`reformRejected`, `[SessionScoped]`, cleared in `OnAnimationLoad` beside `reformed` —
a stale reject surviving a load would hide a unit from the catch-up, which is a correctness bug, not a slow one). A
repeat rejection is now one cached member read plus a reference-hash lookup.

**Scope, stated honestly: this bucket is transient.** The scan latches ~5 s after load (`ReformQuietLimit`), and a
panel read after settling shows no `armies` segment at all. It stays alive longer *in battle*, because each newly
handled unit resets the quiet counter — which is exactly when 63 µs/frame is least welcome.

A second finding, recorded not fixed: the per-army `fref` lookup is read by **one** match arm (macro-replacement
links, `unit: ""`). A pack without such links pays two reflection lookups per army for a string nothing reads; it is
now skipped when unwanted. Checked against the real registry rather than assumed — ENC has 8 links and **4 do** carry
`unit: ""`, so this buys ENC nothing. It helps other packs.

## 9. `PoseDonor` — the pose hook's variable cost (2026-08-23, OPEN — instrumented)

The pose hook's per-pawn cost is not one number. Within a single session, on the same pawn count:

| | `pose ours` | `PoseDonor` | share |
|---|---|---|---|
| panel | 182 µs = 32 adds × **5,678 ns** | *not in top six* | — |
| logged | 693 µs = 34 adds × **20,589 ns** | 495.7 µs | 72% |
| logged | 701 µs = 31 adds × **22,406 ns** | 521.9 µs | 74% |

A **4× swing**, far outside the ~11% §2 allows for that bucket, and the whole difference is whether the donor-clip
branch runs. So `PoseDonor` — not the load window, as an earlier 2,308 µs reading suggested — is the pose hook's
variable cost.

Not fixed, because `PoseDonor` is one bucket over nine applies and a total cannot say which. One suspect was removed
by *reading*: `DumpDonorChannels` latches on `donorAxisDumped.Add(resourceName)`, so it runs once per model, never per
frame. The rest are split by the **kind** of work, since that decides the fix:

| Sub-bucket | Work | Calls |
|---|---|---|
| `DonorRig` | bone / channel writes | `ApplyRotorSpin`, `ApplyRotorTrim` |
| `DonorWorld` | world queries + raycasts | `ApplyPositionOffset`, `ApplyCombatZ`, `ApplyTerrainHug` |
| `DonorMotion` | arithmetic on the entry | `ApplyTurnEase`, `ApplyMoveTilt`, `ApplyGunElevation`, `ApplyScale` |

`DonorWorld` is where §2's already-fixed *"two raycasts per helicopter per frame"* lived, so it is the one to
**disprove first**, not assume. The readout also prints the donor **count**, which is the decisive number and the same
question `SelTileSkip`/`SelTileOurs` answered — at ~495 µs it is either ~165 µs per helicopter or ~14 µs per pawn, and
those need opposite fixes:

```text
| donor N poses X µs (Y ns ea) = rig A + world B + motion C µs
```

**Would compiling against the game DLLs fix it?** Asked 2026-08-23; answered with the numbers, not intuition — **no.**
`FastMember` already emits a `DynamicMethod` doing what direct compiled access does (`unbox` to a managed pointer,
`ldflda` through the nested struct, `ldfld`/`stfld` the leaf) at **~10 ns**. Hard-typing would take that to ~1–2 ns.
Against `PoseOurs` at 5,678 ns/add, ~60 accesses × 10 ns ≈ 600 ns is roughly **10%** of the bucket — and against
`PoseDonor`'s ~165 µs per helicopter it is nothing at all. The speedup lands on the part that is already fast. The
costs are not theoretical: CI builds today from **public sources only** (`fetch-refs.ps1`: *"the plugin's only
compile-time game surface is string-based reflection"* — the last csproj game reference was removed 2026-08-17), and
`FastMember` returning null lets a renamed member **degrade to the old speed, never to a crash**, which a hard-typed
reference converts into a `TypeLoadException` that takes the whole plugin down. See
[Decisions](Decisions.md) *"Make reflection drift loud"*.

## 10. Open items

- ~~The unit-name matcher (`FindEntryForUnitDefinition`) does not match units whose definition name lacks the
  pawnDescription (the hovercraft, the drones — found by the sub-pawn walk's self-check). The walk now handles it; the
  fire-on-attack and engine-audio paths use the same matcher and may be skipping those units.~~ — **ADDRESSED
  2026-08-23, and the entry was half wrong.** `ResolveUnitEntry` now falls back to matching the unit's own **pawn**
  names against `pawnDescription` — the criterion `OurSubPawns` already used, so the unit-level and pawn-level
  resolvers cannot disagree — and says so once per unit definition rather than absorbing it silently.
  **The correction:** *engine audio was never affected.* `ProcessEngineAudio` consumes `OurSubPawns`, i.e. the walk
  that was already fixed, so it resolved these units all along. Checked against the shipped pack rather than assumed:
  of the named units, `Hovercraft` runs only `engineSound` (the safe path) and the drones' flags are off except
  **`animStateDriven` on `DroneSquadFPV`**, which is the one ENC feature the gap could actually have silenced.
  `DugoutCanoe` was never affected either — `NavalTransport_Era1_Common_DugoutCanoe_Default` does carry its coreDesc.
  **Still unverified in-game:** no session since has had a hovercraft or drone on the map, so the exact unit-definition
  strings are unconfirmed. That is now self-reporting instead of guesswork — the fallback logs
  `[Uni] unit '<name>' does not contain pawnDescription '<desc>' — matched '<model>' by PAWN name instead`, so the
  next session with one of those units on the map either prints it or proves the concern was never real.
  Pinned by two tests (the miss, and `DugoutCanoe` as the counter-case so the fallback isn't assumed universal).

Related: [Architecture](Architecture.md) (§2 threads, §2b per-frame), [Testing](Testing.md) (the headless tools),
[Vertex-Budget](Vertex-Budget.md) (the *GPU* budget — a different axis: mesh memory, not frame time).
