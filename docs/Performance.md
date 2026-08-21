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
| `HAF N µs/frame (x% @ fps)` | everything HAF did in an average frame of the last 5 s; the percentage is of the **measured** frame (33 ms at 30 fps) |
| `Update N µs` | the `Plugin.Update` fan-out — every poll, every tick |
| `pose vanilla … = A adds × N ns` | the pose hook on pawns that are **not** ours (the early-out path): how many pawn-adds per frame, and the cost of each |
| `pose ours … = A adds × N ns` | the pose hook's full path on **our** pawns — this one scales with how many custom units are on screen |
| `top: …` | the six most expensive buckets. Sub-buckets (`PoseAnim`, `PoseDonor`, `SelTileLoop` …) are nested inside their parent and **double-count in a sum** — read them as a breakdown, not additively |

Every per-frame entry point in the plugin lives inside a named bucket (`Patches/FrameCost.cs`). The meter itself
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
| `SelectorTile` | ~210 µs | ~210 µs | diffuse per-district overhead in the scoped poll — **left as is** (0.6%) |

Not HAF's steady cost, but visible in the meter: **`DistrictDebug = true`** in the config runs the repository dump at
load — ~40 ms/frame for the first 5 s of every session. It is a diagnostic; keep it `false` for play.

## 3. The rules

Learned in one afternoon, each from a bucket that surprised ([Architecture](Architecture.md) §2b):

1. **A new per-frame path gets a bucket when it is written.** Unbucketed cost is invisible cost. Wrap the call in
   `Plugin.Update` (or the hook) with `FrameCost.Begin()` / `End(bucket, t)` and add the name to `FrameCost.names`.
2. **No full-scene `FindObjectsOfType` on a timer.** ~50 ms on a busy map, delivered as a hitch. Walk the presentation
   tree instead (`SubPawnScan.cs` is the template: targeted, *self-verified against the scan once per session*, with
   the scan as the fallback), or mark dirty from an event and cap the cadence in tens of seconds.
3. **No retry-every-frame until something exists.** Throttle unbound retries; twice a second is plenty.
4. **Resolve reflection once.** `AccessTools.TypeByName` walks every assembly; a bone-name lookup is a reflection read
   and a string allocation per bone. Cache per entry, keyed on whatever can change the answer (a dial signature, a
   session).
5. **The per-pawn path uses `PawnFast`.** Boxed-struct reflection is ~0.5–1 µs per get/set on Mono; the compiled
   accessors (`FastMember`) are ~10 ns and write into the box identically. Every accessor has a reflection fallback,
   so a renamed game field costs speed, never function — `[PawnFast]` in the log says which path is live.
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

## 6. Open items

- `SelectorTile` ~210 µs: diffuse, not worth a round at 0.6% — but it is the largest remaining steady bucket.
- The unit-name matcher (`FindEntryForUnitDefinition`) does not match units whose definition name lacks the
  pawnDescription (the hovercraft, the drones — found by the sub-pawn walk's self-check). The walk now handles it; the
  fire-on-attack and engine-audio paths use the same matcher and may be skipping those units. Correctness, not cost —
  recorded for the next drill.

Related: [Architecture](Architecture.md) (§2 threads, §2b per-frame), [Testing](Testing.md) (the headless tools),
[Vertex-Budget](Vertex-Budget.md) (the *GPU* budget — a different axis: mesh memory, not frame time).
